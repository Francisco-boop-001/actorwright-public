using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Independent post-write verifier for source-owned appearance overrides. It
/// combines Mutagen read-back with a separate bounded TES4/NPC byte walker.
/// </summary>
public static class BethesdaNpcAppearanceOverrideVerifier
{
    private const uint CompressedRecordFlag = 0x0004_0000;

    public static NpcAppearanceOverrideVerificationResult Verify(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        WorkspacePath artifact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(artifact.Value))
        {
            diagnostics.Add(Error("npc-appearance-override-output-missing",
                "The appearance override plugin does not exist."));
            return Empty(artifact, diagnostics);
        }

        Sha256Hash? outputHash = null;
        ImmutableArray<PluginName> observedMasters = [];
        ImmutableArray<RecordSignature> observedSignatures = [];
        var sourceOwnedTargetCount = 0;
        var selfOwnedTargetCount = 0;
        var appearanceMatches = false;
        var recordPatchMatches = false;
        var unrelatedSubrecordsPreserved = false;
        var unrelatedScriptsPreserved = false;
        try
        {
            outputHash = HashFile(artifact.Value);
            var ownership = BethesdaNpcOverrideAdapter.InspectOwnership(
                artifact,
                proposal.OutputPluginName,
                proposal.SourcePluginName,
                proposal.TargetFormId);
            observedMasters = ownership.TypedMasters;
            observedSignatures = ownership.MajorRecordSignatures;
            sourceOwnedTargetCount = ownership.SourceOwnedTargetCount;
            selfOwnedTargetCount = ownership.SelfOwnedTargetCount;
            if (!ownership.TypedMasters.SequenceEqual(proposal.RequiredMasters) ||
                !ownership.RawMasters.SequenceEqual(proposal.RequiredMasters))
            {
                diagnostics.Add(Error("npc-appearance-override-master-drift",
                    "Typed and raw TES4 master lists must exactly match the appearance proposal."));
            }
            if (!SameSignatureMultiset(
                    observedSignatures,
                    proposal.ExpectedMajorRecordSignatures) ||
                ownership.NpcRecordCount != 1 ||
                sourceOwnedTargetCount != 1 || selfOwnedTargetCount != 0)
            {
                diagnostics.Add(Error("npc-appearance-override-record-surface",
                    "The output record families or NPC ownership differ from the exact appearance proposal."));
            }

            var sourceKey = ModKey.FromNameAndExtension(proposal.SourcePluginName.Value);
            var outputKey = ModKey.FromNameAndExtension(proposal.OutputPluginName.Value);
            using var source = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(sourceKey, new FilePath(request.SourcePlugin.Value)),
                SkyrimRelease.SkyrimSE);
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(artifact.Value)),
                SkyrimRelease.SkyrimSE);
            var sourceNpc = source.Npcs.First(item =>
                item.FormKey == new FormKey(sourceKey, request.TargetFormId.Value));
            var outputNpc = output.Npcs.First(item =>
                item.FormKey == new FormKey(sourceKey, request.TargetFormId.Value));

            var appearanceErrorCount = ErrorCount(diagnostics);
            if (outputNpc.Race.FormKey != ToFormKey(request.Race))
                diagnostics.Add(Error("npc-appearance-override-race",
                    "Typed read-back race does not match the preset-derived race."));
            var sourceFlags = sourceNpc.Configuration.Flags;
            var outputFlags = outputNpc.Configuration.Flags;
            const NpcConfiguration.Flag authoredFlagMask =
                NpcConfiguration.Flag.Female |
                NpcConfiguration.Flag.IsCharGenFacePreset;
            if ((sourceFlags & ~authoredFlagMask) !=
                (outputFlags & ~authoredFlagMask) ||
                outputFlags.HasFlag(NpcConfiguration.Flag.Female) !=
                (request.Sex == NpcSex.Female))
            {
                diagnostics.Add(Error("npc-appearance-override-sex",
                    "The output changed an unauthorized ACBS flag or did not apply the requested sex."));
            }
            BethesdaNpcCreationVerifier.VerifyTypedAuthoredAppearance(
                output,
                outputNpc,
                request.Appearance,
                proposal.SourceEditorId,
                request.Sex,
                new WorkspacePath(Path.GetDirectoryName(request.SourcePlugin.Value)!),
                proposal.OutputPluginName,
                diagnostics,
                request.PluginAuthorities);
            BethesdaNpcCreationVerifier.VerifyTypedRuntimeAppearance(
                outputNpc,
                request.RuntimeAppearance,
                diagnostics,
                permitUnrelatedScripts: true);
            if (request.Appearance.NakedSkinBinding is { } skin &&
                outputNpc.WornArmor.FormKey != new FormKey(outputKey, skin.AllocatedArmorLocalFormId.Value))
                diagnostics.Add(Error("npc-appearance-override-skin-binding",
                    "The source-owned NPC WNAM does not reach its allocated private skin ARMO."));
            appearanceMatches = ErrorCount(diagnostics) == appearanceErrorCount;

            var recordPatchErrorCount = ErrorCount(diagnostics);
            bool expectedCharGen = request.IsCharGenFacePreset ??
                sourceFlags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset);
            if (outputFlags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset) !=
                expectedCharGen)
            {
                diagnostics.Add(Error(
                    "npc-appearance-override-chargen",
                    "Typed read-back did not preserve or apply the requested ACBS CharGen face-preset flag."));
            }
            VerifyOutfit(
                "default",
                sourceNpc.DefaultOutfit.FormKeyNullable,
                outputNpc.DefaultOutfit.FormKeyNullable,
                request.Appearance.ExposedOutfitSkinBinding is { } exposed
                    ? OptionalFormReference.Set(new FormReference(proposal.OutputPluginName, exposed.AllocatedOutfitLocalFormId))
                    : request.OutfitPatch?.DefaultOutfit,
                diagnostics);
            VerifyOutfit(
                "sleeping",
                sourceNpc.SleepingOutfit.FormKeyNullable,
                outputNpc.SleepingOutfit.FormKeyNullable,
                request.OutfitPatch?.SleepingOutfit,
                diagnostics);
            recordPatchMatches = ErrorCount(diagnostics) == recordPatchErrorCount;

            unrelatedScriptsPreserved = ScriptFingerprints(sourceNpc)
                .SequenceEqual(ScriptFingerprints(outputNpc));
            if (!unrelatedScriptsPreserved)
                diagnostics.Add(Error("npc-appearance-override-script-drift",
                    "One or more unrelated VMAD scripts or properties changed."));

            var sourceRaw = ReadRawNpc(
                request.SourcePlugin,
                proposal.SourcePluginName,
                proposal.SourcePluginName,
                request.TargetFormId,
                cancellationToken);
            var outputRaw = ReadRawNpc(
                artifact,
                proposal.SourcePluginName,
                proposal.OutputPluginName,
                request.TargetFormId,
                cancellationToken);
            unrelatedSubrecordsPreserved = UnchangedSubrecordsMatch(
                sourceRaw,
                outputRaw,
                proposal.ChangedNpcSubrecords);
            if (!unrelatedSubrecordsPreserved)
                diagnostics.Add(Error("npc-appearance-override-subrecord-drift",
                    "An NPC subrecord outside the declared appearance surface changed."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("npc-appearance-override-verification-failed",
                exception.Message));
        }

        return new NpcAppearanceOverrideVerificationResult(
            !HasErrors(diagnostics),
            artifact,
            outputHash,
            observedMasters,
            observedSignatures,
            sourceOwnedTargetCount,
            selfOwnedTargetCount,
            appearanceMatches,
            recordPatchMatches,
            unrelatedSubrecordsPreserved,
            unrelatedScriptsPreserved,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<string> ScriptFingerprints(INpcGetter npc)
    {
        return npc.VirtualMachineAdapter?.Scripts
            .Where(item => !string.Equals(item.Name,
                SkyrimNpcApplySseContract.ScriptName,
                StringComparison.Ordinal))
            .Select(script =>
            {
                var properties = script.Properties.Select(property =>
                    JsonSerializer.Serialize(property, property.GetType()));
                var canonical = script.Name + "\n" + string.Join("\n", properties);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            })
            .ToImmutableArray() ?? [];
    }

    private static void VerifyOutfit(
        string role,
        FormKey? source,
        FormKey? output,
        OptionalFormReference? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        OptionalFormReference value = patch.GetValueOrDefault();
        FormKey? expected = patch.HasValue && value.IsSpecified
            ? value.Value is { } reference
                ? ToFormKey(reference)
                : null
            : source;
        if (output != expected)
        {
            diagnostics.Add(Error(
                $"npc-appearance-override-{role}-outfit",
                $"Typed read-back did not preserve or apply the requested {role} outfit."));
        }
    }

    private static RawNpcSnapshot ReadRawNpc(
        WorkspacePath plugin,
        PluginName owner,
        PluginName filePlugin,
        FormId localFormId,
        CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(plugin.Value);
        var masters = BethesdaNpcOverrideAdapter.ReadRawMasters(plugin);
        var ownerIndex = masters.Length;
        if (!string.Equals(owner.Value, filePlugin.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            ownerIndex = -1;
            for (var index = 0; index < masters.Length; index++)
            {
                if (string.Equals(masters[index].Value, owner.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ownerIndex = index;
                    break;
                }
            }
        }
        if (ownerIndex < 0)
            throw new InvalidDataException(
                $"Record owner {owner} is neither the file nor one of its raw masters.");
        var rawFormId = checked((uint)(ownerIndex << 24)) | localFormId.Value;
        var matches = new List<RawNpcSnapshot>();
        ReadRange(bytes, 0, bytes.Length, rawFormId, matches, cancellationToken);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"Raw NPC 0x{rawFormId:X8} was not found."),
            _ => throw new InvalidDataException(
                $"Raw NPC 0x{rawFormId:X8} occurred more than once.")
        };
    }

    private static void ReadRange(
        byte[] bytes,
        int start,
        int end,
        uint targetFormId,
        List<RawNpcSnapshot> matches,
        CancellationToken cancellationToken)
    {
        var position = start;
        while (position + 8 <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            if (size > int.MaxValue)
                throw new InvalidDataException("A TES record exceeds the supported size.");
            var recordEnd = checked(position +
                (signature == "GRUP" ? (int)size : 24 + (int)size));
            if (recordEnd > end)
                throw new InvalidDataException("A TES record extends beyond its parent boundary.");
            if (signature == "GRUP")
            {
                ReadRange(bytes, position + 24, recordEnd, targetFormId,
                    matches, cancellationToken);
            }
            else if (signature == "NPC_" &&
                     BinaryPrimitives.ReadUInt32LittleEndian(
                         bytes.AsSpan(position + 12, 4)) == targetFormId)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 8, 4));
                if ((flags & CompressedRecordFlag) != 0)
                    throw new InvalidDataException(
                        "Compressed NPC records are outside the bounded appearance verifier.");
                matches.Add(ReadNpcPayload(bytes, position + 24, recordEnd));
            }
            position = recordEnd;
        }
        if (position != end)
            throw new InvalidDataException(
                "Trailing bytes do not form a complete TES record header.");
    }

    private static RawNpcSnapshot ReadNpcPayload(byte[] bytes, int start, int end)
    {
        var fields = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var position = start;
        int? extendedSize = null;
        while (position < end)
        {
            if (end - position < 6)
                throw new InvalidDataException("The NPC ends inside a subrecord header.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            position += 6;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || end - position < 4)
                    throw new InvalidDataException("The NPC contains a malformed XXXX marker.");
                extendedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position, 4)));
                position += 4;
                continue;
            }
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size < 0 || size > end - position)
                throw new InvalidDataException(
                    $"NPC subrecord {signature} extends beyond the record boundary.");
            if (!fields.TryGetValue(signature, out var values))
                fields[signature] = values = [];
            values.Add(bytes.AsSpan(position, size).ToArray());
            position += size;
        }
        if (extendedSize is not null)
            throw new InvalidDataException("The NPC ends after an XXXX marker.");
        return new RawNpcSnapshot(fields.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.Ordinal));
    }

    private static bool UnchangedSubrecordsMatch(
        RawNpcSnapshot source,
        RawNpcSnapshot output,
        ImmutableArray<string> changed)
    {
        var excluded = changed.ToHashSet(StringComparer.Ordinal);
        var fields = source.Fields.Keys.Concat(output.Fields.Keys)
            .Where(item => !excluded.Contains(item))
            .Distinct(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!source.Fields.TryGetValue(field, out var before) ||
                !output.Fields.TryGetValue(field, out var after) ||
                before.Length != after.Length)
                return false;
            for (var index = 0; index < before.Length; index++)
            {
                if (!before[index].AsSpan().SequenceEqual(after[index]))
                    return false;
            }
        }
        return true;
    }

    private static bool SameSignatureMultiset(
        ImmutableArray<RecordSignature> actual,
        ImmutableArray<RecordSignature> expected) =>
        actual.OrderBy(item => item.Value, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(item => item.Value, StringComparer.Ordinal));

    private static FormKey ToFormKey(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value),
        reference.FormId.Value);

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static int ErrorCount(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static NpcAppearanceOverrideVerificationResult Empty(
        WorkspacePath artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, artifact, null, [], [], 0, 0, false, false, false, false,
            diagnostics.ToImmutable());

    private sealed record RawNpcSnapshot(
        ImmutableDictionary<string, ImmutableArray<byte[]>> Fields);
}
