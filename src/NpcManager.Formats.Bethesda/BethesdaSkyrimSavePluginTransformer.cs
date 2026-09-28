using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Rewrites one complete reviewed Skyrim plugin into a fresh K-local output.
/// It never edits the source. Save-specific changes are limited to TES4 flags,
/// string encoding, and one new or existing LVLN.
/// </summary>
public sealed class BethesdaSkyrimSavePluginTransformer(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimSavePluginTransformer
{
    private const uint MasterFlag = 0x0000_0001;
    private const uint LightFlag = 0x0000_0200;

    public async ValueTask<SkyrimSavePluginTransformResult> TransformAsync(
        SkyrimSavePluginTransformRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request).ToBuilder();
        SkyrimSavePackageDecision decision =
            SkyrimSavePackageRules.Save(request.Snapshot, request.Options);
        diagnostics.AddRange(decision.Diagnostics);
        if (!decision.Accepted || HasErrors(diagnostics))
            return Refused(diagnostics);

        string temporary = request.OutputPlugin.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            Sha256Hash sourceHash = await HashFileAsync(
                request.SourcePlugin.Value,
                cancellationToken).ConfigureAwait(false);
            ModKey sourceKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.SourcePlugin.Value));
            using var overlay = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(sourceKey, new FilePath(request.SourcePlugin.Value)),
                SkyrimRelease.SkyrimSE);
            var mod = (SkyrimMod)overlay.DeepCopy();
            ValidateNpcInventory(mod, request.Snapshot);

            mod.IsMaster = request.Options.MarkAsMaster;
            mod.IsSmallMaster = request.Options.LightMaster;
            ApplyLeveledNpc(mod, request);
            cancellationToken.ThrowIfCancellationRequested();

            mod.WriteToBinary(new FilePath(temporary), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck,
                Encodings = EncodingFor(
                    request.Options.EncodingMode,
                    request.Snapshot.DetectedEncoding)
            });

            TransformReadback readback = ReadBack(temporary, request);
            uint rawFlags = ReadTes4Flags(temporary);
            bool rawMaster = (rawFlags & MasterFlag) != 0;
            bool rawLight = (rawFlags & LightFlag) != 0;
            if (rawMaster != request.Options.MarkAsMaster ||
                rawLight != request.Options.LightMaster)
                throw new InvalidDataException(
                    "Raw TES4 flag readback did not match the reviewed flags.");

            Sha256Hash sourceAfter = await HashFileAsync(
                request.SourcePlugin.Value,
                cancellationToken).ConfigureAwait(false);
            if (sourceAfter != sourceHash)
                throw new InvalidDataException(
                    "The source plugin changed during the fresh-output transform.");

            File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
            temporary = string.Empty;
            Sha256Hash outputHash = await HashFileAsync(
                request.OutputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
            var artifact = new SkyrimSavePluginTransformArtifact(
                request.SourcePlugin,
                request.OutputPlugin,
                sourceHash,
                outputHash,
                rawMaster,
                rawLight,
                request.Options.EncodingMode,
                readback.NpcFormIds,
                readback.LeveledNpcEditorId,
                readback.LeveledNpcEntries,
                readback.OtherRecordCount,
                true,
                false);
            return new(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or OverflowException)
        {
            TryDelete(temporary);
            diagnostics.Add(Error(
                "save-package-plugin-transform-failed",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> Validate(
        SkyrimSavePluginTransformRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.SourcePlugin.IsUnder(labRoot))
            diagnostics.Add(Error(
                "save-package-plugin-source-outside-lab",
                "The reviewed source plugin must remain under the K-only lab root."));
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(Error(
                "save-package-plugin-source-missing",
                "The reviewed source plugin does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.SourcePlugin));
            AddReparseDiagnostic(
                request.SourcePlugin.Value,
                "source plugin",
                diagnostics);
        }

        if (!request.OutputPlugin.IsUnder(labRoot))
            diagnostics.Add(Error(
                "save-package-plugin-output-outside-lab",
                "The transformed plugin must remain under the K-only lab root."));
        if (File.Exists(request.OutputPlugin.Value) ||
            Directory.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error(
                "save-package-plugin-output-exists",
                "The transformed plugin writer never overwrites an output."));
        string? parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error(
                "save-package-plugin-output-parent",
                "The transformed plugin parent must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(
                labRoot,
                new WorkspacePath(parent)));
            AddReparseDiagnostic(parent, "output parent", diagnostics);
        }
        if (string.Equals(
                Path.GetFullPath(request.SourcePlugin.Value),
                Path.GetFullPath(request.OutputPlugin.Value),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "save-package-plugin-source-output-same",
                "The transformed plugin cannot overwrite its source."));
        return diagnostics.ToImmutable();
    }

    private static void ValidateNpcInventory(
        ISkyrimModGetter mod,
        SkyrimSavePackageSnapshot snapshot)
    {
        var actual = mod.Npcs
            .Select(item => new SkyrimSavePackageNpc(
                new FormId(item.FormKey.ID),
                TryEditorId(item.EditorID),
                item.FormKey.ModKey == mod.ModKey,
                new PluginName(item.FormKey.ModKey.FileName)))
            .OrderBy(item => item.FormId.Value)
            .ToArray();
        if (actual.Length != snapshot.NpcRecordCount)
            throw new InvalidDataException(
                "The source NPC count changed after review.");
        if (!snapshot.Npcs.IsDefaultOrEmpty)
        {
            var expected = snapshot.Npcs
                .OrderBy(item => item.FormId.Value)
                .ToArray();
            if (actual.Length != expected.Length)
                throw new InvalidDataException(
                    "The source NPC inventory changed after review.");
            for (int index = 0; index < actual.Length; index++)
            {
                if (actual[index].FormId != expected[index].FormId ||
                    actual[index].EditorId != expected[index].EditorId ||
                    actual[index].SelfOwned != expected[index].SelfOwned)
                    throw new InvalidDataException(
                        "The source NPC identity changed after review.");
            }
        }
    }

    private static void ApplyLeveledNpc(
        SkyrimMod mod,
        SkyrimSavePluginTransformRequest request)
    {
        if (request.Options.LeveledListMode ==
            SkyrimSaveLeveledListMode.PreservePackageRecords)
            return;

        FormKey[] scoped = ScopedNpcKeys(mod, request);
        LeveledNpc list;
        if (request.Options.LeveledListMode ==
            SkyrimSaveLeveledListMode.Existing)
        {
            list = mod.LeveledNpcs.SingleOrDefault(item =>
                       string.Equals(
                           item.EditorID,
                           request.Options.LeveledListEditorId,
                           StringComparison.OrdinalIgnoreCase)) ??
                   throw new InvalidDataException(
                       "The reviewed existing LVLN was not found.");
            list.Entries ??= [];
        }
        else
        {
            uint next = NextSelfFormId(mod);
            if (request.Options.LightMaster && next > 0x0000_0FFF)
                throw new InvalidDataException(
                    "The new LVLN would exceed the ESL compact FormID range.");
            list = new LeveledNpc(
                new FormKey(mod.ModKey, next),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = request.Options.LeveledListEditorId,
                Entries = []
            };
            mod.LeveledNpcs.Add(list);
        }

        var existing = list.Entries
            .Where(item => item.Data is not null)
            .Select(item => item.Data!.Reference.FormKey)
            .ToHashSet();
        foreach (FormKey npc in scoped)
        {
            if (request.Options.NoDuplicateLeveledEntries &&
                existing.Contains(npc))
                continue;
            list.Entries.Add(new LeveledNpcEntry
            {
                Data = new LeveledNpcEntryData
                {
                    Level = 1,
                    Count = 1,
                    Reference = new FormLink<INpcSpawnGetter>(npc)
                }
            });
            existing.Add(npc);
        }
    }

    private static FormKey[] ScopedNpcKeys(
        ISkyrimModGetter mod,
        SkyrimSavePluginTransformRequest request)
    {
        IEnumerable<INpcGetter> records = mod.Npcs;
        if (request.Options.Scope == SkyrimSaveScope.SelectedOnly)
            records = records.Where(item =>
                item.FormKey.ID == request.Snapshot.TargetFormId.Value);
        FormKey[] keys = records
            .Select(item => item.FormKey)
            .OrderBy(item => item.ID)
            .ToArray();
        if (keys.Length == 0)
            throw new InvalidDataException(
                "The reviewed scope contains no NPC records.");
        return keys;
    }

    private static uint NextSelfFormId(ISkyrimModGetter mod)
    {
        uint highest = mod.EnumerateMajorRecords()
            .Where(item => item.FormKey.ModKey == mod.ModKey)
            .Select(item => item.FormKey.ID)
            .DefaultIfEmpty(0x7FFU)
            .Max();
        if (highest >= 0x00FF_FFFF)
            throw new InvalidDataException(
                "The plugin has no remaining self-owned FormID.");
        return Math.Max(0x800, highest + 1);
    }

    private static TransformReadback ReadBack(
        string path,
        SkyrimSavePluginTransformRequest request)
    {
        ModKey key = ModKey.FromNameAndExtension(
            Path.GetFileName(request.OutputPlugin.Value));
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(key, new FilePath(path)),
            SkyrimRelease.SkyrimSE);
        if (mod.IsMaster != request.Options.MarkAsMaster ||
            mod.IsSmallMaster != request.Options.LightMaster)
            throw new InvalidDataException(
                "Mutagen readback did not preserve the reviewed plugin flags.");

        ImmutableArray<FormId> npcs = mod.Npcs
            .Select(item => new FormId(item.FormKey.ID))
            .OrderBy(item => item.Value)
            .ToImmutableArray();
        string? editorId = null;
        ImmutableArray<FormId> entries = [];
        if (request.Options.LeveledListMode !=
            SkyrimSaveLeveledListMode.PreservePackageRecords)
        {
            ILeveledNpcGetter list = mod.LeveledNpcs.SingleOrDefault(item =>
                string.Equals(
                    item.EditorID,
                    request.Options.LeveledListEditorId,
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    "The transformed LVLN was absent during readback.");
            editorId = list.EditorID;
            entries = (list.Entries ?? [])
                .Where(item => item.Data is not null)
                .Select(item => new FormId(item.Data!.Reference.FormKey.ID))
                .ToImmutableArray();
        }
        int other = mod.EnumerateMajorRecords().Count() -
                    mod.Npcs.Count -
                    mod.LeveledNpcs.Count;
        return new(npcs, editorId, entries, other);
    }

    private static EncodingBundle EncodingFor(
        SkyrimSaveEncodingMode requested,
        SkyrimSaveEncodingMode detected)
    {
        SkyrimSaveEncodingMode resolved =
            requested == SkyrimSaveEncodingMode.PreservePackageBytes
                ? detected
                : requested;
        IMutagenEncoding encoding =
            resolved == SkyrimSaveEncodingMode.Utf8
                ? MutagenEncoding._utf8
                : MutagenEncoding._1252;
        return new EncodingBundle(encoding, encoding);
    }

    private static uint ReadTes4Flags(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.SequentialScan);
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException(
                "The transformed plugin does not begin with TES4.");
        return BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    }

    private static EditorId? TryEditorId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return new EditorId(value); }
        catch (ArgumentException) { return null; }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(
                    FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error(
                    "save-package-plugin-reparse",
                    $"The {role} traverses a reparse point."));
                return;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                return;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimSavePluginTransformResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record TransformReadback(
        ImmutableArray<FormId> NpcFormIds,
        string? LeveledNpcEditorId,
        ImmutableArray<FormId> LeveledNpcEntries,
        int OtherRecordCount);
}
