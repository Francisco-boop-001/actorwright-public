using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial record SkyrimNpcFinishCorePluginSnapshot(
    bool Admitted,
    PluginName Plugin,
    Sha256Hash PluginSha256,
    FormReference BaseNpc,
    EditorId TargetEditorId,
    ImmutableArray<string> MasterOrder,
    uint NextFormId,
    uint Tes4Flags,
    ImmutableArray<string> OccupiedIds,
    uint TargetConfigurationFlags,
    ImmutableArray<NpcFactionEntry> FactionRanks,
    FormReference? CombatStyle,
    bool CombatStyleMatchesDefensiveContract,
    FormReference? DefaultOutfit,
    ImmutableArray<SkyrimNpcFinishCoreInventoryEntry> Inventory,
    ImmutableArray<FormReference> PackageLinks,
    ImmutableArray<SkyrimNpcFinishCoreRelationshipSnapshot> Relationships,
    ImmutableDictionary<string, int> TypedForbiddenCounts,
    ImmutableDictionary<string, int> RawForbiddenCounts,
    bool CompressedTarget,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed partial record SkyrimNpcFinishCorePluginSnapshot
{
    /// <summary>True only when the one linked package is the exact closed Finish Core sandbox.</summary>
    public bool PackageMatchesFinishCoreContract { get; init; }

    public SkyrimNpcFinishCoreAiPolicy? AiData { get; init; }

    public ImmutableArray<SkyrimNpcFinishCorePerk> Perks { get; init; } = [];

    public ImmutableArray<FormReference> OutfitArmaturesToClone { get; init; } = [];
    public ImmutableArray<FormReference> OutfitArmorsToClone { get; init; } = [];
}

/// <summary>
/// Independent typed/raw admission for the world-clean Finish Core source
/// plugin.  This reader never writes or normalizes a source plugin.
/// </summary>
public sealed class BethesdaSkyrimNpcFinishCoreSourceReader
{
    public static ImmutableArray<Diagnostic> CheckOutfitRace(WorkspacePath sourcePlugin,
        FormReference actorRace, SkyrimNpcFinishCoreRequest request)
    {
        try
        {
            using var source = SkyrimMod.CreateFromBinaryOverlay(new ModPath(
                ModKey.FromNameAndExtension(Path.GetFileName(sourcePlugin.Value)), sourcePlugin.Value), SkyrimRelease.SkyrimSE);
            var selection = BethesdaSkyrimNpcFinishCoreOutfit.Read(source,
                BethesdaSkyrimNpcFinishCoreOutfit.Key(actorRace), request);
            return selection.ExcludedArmatures.Select(row =>
                BethesdaSkyrimNpcFinishCoreOutfit.Exclusion(row, selection.Race)).ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or OverflowException)
        {
            return [new Diagnostic(OutfitFailureCode(exception, "finish-core-outfit-provider"), DiagnosticSeverity.Error, exception.Message)];
        }
    }

    public static readonly ImmutableArray<string> ForbiddenSignatures =
    [
        "CELL", "WRLD", "ACHR", "REFR", "LAND", "NAVM", "NAVI",
        "WATR", "LTEX", "LCTN", "REGN", "CLMT", "MUSC", "IMGS"
    ];

    /// <summary>
    /// Reads only the retained TES4 header bytes needed to establish master
    /// dependencies.  This seam is deliberately independent of a filesystem
    /// path and of the full typed plugin inspection below.
    /// </summary>
    public static ImmutableArray<PluginName> ReadMasterDependencies(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException("The retained plugin bytes must begin with TES4.");

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        if (payloadLength > int.MaxValue || 24L + payloadLength > bytes.Length)
        {
            throw new InvalidDataException(
                "The retained TES4 header payload exceeds the available bytes.");
        }

        int payloadEnd = checked(24 + (int)payloadLength);
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        int position = 24;
        bool awaitingData = false;
        while (position < payloadEnd)
        {
            if (position + 6 > payloadEnd)
                throw new InvalidDataException("A retained TES4 subrecord header is truncated.");

            ReadOnlySpan<byte> signature = bytes.Slice(position, 4);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.Slice(position + 4, 2));
            position += 6;
            if (position + length > payloadEnd)
                throw new InvalidDataException("A retained TES4 subrecord exceeds the header.");

            if (awaitingData)
            {
                if (!signature.SequenceEqual("DATA"u8) || length != 8)
                {
                    throw new InvalidDataException(
                        "Every retained MAST subrecord must be followed by an eight-byte DATA subrecord.");
                }

                awaitingData = false;
            }
            else if (signature.SequenceEqual("MAST"u8))
            {
                ReadOnlySpan<byte> value = bytes.Slice(position, length);
                int terminator = value.IndexOf((byte)0);
                bool nonAscii = false;
                for (int i = 0; i < Math.Max(terminator, 0); i++)
                {
                    if (value[i] > 0x7F)
                    {
                        nonAscii = true;
                        break;
                    }
                }
                if (terminator <= 0 || terminator != value.Length - 1 || nonAscii)
                {
                    throw new InvalidDataException(
                        "A retained MAST subrecord must contain one null-terminated plugin name.");
                }

                string plugin = Encoding.UTF8.GetString(value[..terminator]);
                try
                {
                    masters.Add(new PluginName(plugin));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        "A retained MAST subrecord contains an invalid plugin name.",
                        exception);
                }

                awaitingData = true;
            }
            else if (signature.SequenceEqual("DATA"u8))
            {
                throw new InvalidDataException(
                    "A retained DATA subrecord must follow its MAST subrecord.");
            }

            position += length;
        }

        if (awaitingData)
            throw new InvalidDataException(
                "The retained TES4 header ends before the DATA subrecord paired with MAST.");

        return masters.ToImmutable();
    }

    public static SkyrimNpcFinishCorePluginSnapshot Inspect(
        WorkspacePath pluginPath,
        PluginName expectedPlugin,
        FormId targetFormId,
        EditorId targetEditorId,
        CancellationToken cancellationToken,
        SkyrimNpcFinishCoreRequest? request = null,
        ImmutableArray<string> outfitMasterOrder = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(pluginPath.Value) || Directory.Exists(pluginPath.Value))
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-plugin-missing",
                "The source plugin is not an ordinary file.");

        byte[] bytes = File.ReadAllBytes(pluginPath.Value);
        Sha256Hash pluginSha = Hash(bytes);
        ImmutableArray<RawRecord> raw;
        try
        {
            raw = ReadRawRecords(bytes);
        }
        catch (InvalidDataException exception)
        {
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-plugin-raw-invalid", exception.Message,
                pluginSha);
        }

        var rawCounts = ForbiddenSignatures.ToImmutableDictionary(
            signature => signature,
            signature => raw.Count(record => record.Signature == signature),
            StringComparer.Ordinal);
        var targetRecords = raw.Where(record =>
                record.Signature == "NPC_" &&
                LocalFormId(record.FormId) == targetFormId.Value)
            .Take(2)
            .ToArray();
        if (raw.Any(record => ForbiddenSignatures.Contains(record.Signature)))
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-forbidden-signature",
                "The source plugin contains a forbidden world or placed-reference signature.",
                pluginSha, rawCounts);
        if (targetRecords.Length != 1)
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-npc-count",
                ExactOneMessage(RecordIdentity("NPC_", targetFormId.Value),
                    targetRecords.Length),
                pluginSha, rawCounts);
        if ((targetRecords[0].Flags & 0x0004_0000u) != 0)
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-compressed-target",
                "The target NPC is compressed; protected subrecord splicing is not admitted for compressed records.",
                pluginSha, rawCounts, compressedTarget: true);
        if (!string.Equals(Path.GetFileName(pluginPath.Value), expectedPlugin.Value,
                StringComparison.OrdinalIgnoreCase))
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                "finish-core-source-plugin-self-key",
                "The source filename does not retain the requested plugin self-key.",
                pluginSha, rawCounts);

        try
        {
            BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot rawSnapshot =
                BethesdaSkyrimNpcFinishCoreRaw.Read(bytes, targetFormId.Value);
            BethesdaSkyrimNpcFinishCoreRaw.RawRecord rawTarget = rawSnapshot.Target ??
                throw new InvalidDataException(
                    "The raw target NPC is missing after the exact-one preflight.");
            ImmutableArray<BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord> targetSubrecords =
                BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, rawTarget);
            Diagnostic? linkedDuplicate = FindLinkedRecordDuplicate(
                rawSnapshot,
                targetSubrecords,
                rawTarget.RawFormId,
                "ZNAM",
                "CSTY",
                "finish-core-source-csty-count",
                "CSTY");
            if (linkedDuplicate is not null)
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    linkedDuplicate.Code, linkedDuplicate.Message, pluginSha, rawCounts);
            linkedDuplicate = FindLinkedRecordDuplicate(
                rawSnapshot,
                targetSubrecords,
                rawTarget.RawFormId,
                "PKID",
                "PACK",
                "finish-core-source-pack-count",
                "PACK");
            if (linkedDuplicate is not null)
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    linkedDuplicate.Code, linkedDuplicate.Message, pluginSha, rawCounts);

            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                pluginPath.Value,
                SkyrimRelease.SkyrimSE);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(mod.ModKey.FileName, expectedPlugin.Value,
                    StringComparison.OrdinalIgnoreCase))
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    "finish-core-source-plugin-self-key",
                    "The typed ModKey does not match the requested plugin self-key.",
                    pluginSha, rawCounts);

            INpcGetter[] npcs = mod.Npcs.Where(npc =>
                    npc.FormKey.ModKey == mod.ModKey &&
                    npc.FormKey.ID == targetFormId.Value)
                .Take(2)
                .ToArray();
            if (npcs.Length != 1)
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    "finish-core-source-npc-count",
                    ExactOneMessage(RecordIdentity("NPC_", targetFormId.Value),
                        npcs.Length),
                    pluginSha, rawCounts);
            if (!string.Equals(npcs[0].EditorID, targetEditorId.Value,
                    StringComparison.Ordinal))
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    "finish-core-source-base-npc",
                    "The target NPC EditorID does not match the request.",
                    pluginSha, rawCounts);

            HashSet<uint> typedIds = mod.EnumerateMajorRecords()
                .Select(record => record.FormKey.ID)
                .ToHashSet();
            ImmutableDictionary<string, int> typedCounts =
                ForbiddenSignatures.ToImmutableDictionary(
                    signature => signature,
                    signature => raw.Count(record =>
                        record.Signature == signature &&
                        typedIds.Contains(LocalFormId(record.FormId))),
                    StringComparer.Ordinal);
            ImmutableArray<string> masters = mod.ModHeader.MasterReferences
                .Select(reference => reference.Master.FileName.ToString())
                .ToImmutableArray();
            ImmutableArray<string> occupied = raw
                .Where(record => record.FormId != 0 &&
                                 record.FormId != 0x0000_0000)
                .Select(record => $"{record.Signature} {new FormId(record.FormId)}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToImmutableArray();
            INpcGetter targetNpc = npcs[0];
            ImmutableArray<NpcFactionEntry> factionRanks = (targetNpc.Factions ?? [])
                .Select(entry => new NpcFactionEntry(
                    ToReference(entry.Faction.FormKey), entry.Rank))
                .ToImmutableArray();
            FormReference? combatStyle = ToReference(targetNpc.CombatStyle.FormKeyNullable);
            bool combatStyleMatches = false;
            if (request?.CombatPolicy is not null)
            {
                CombatStyle template = BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(mod, targetNpc, request);
                if (request.CombatPolicy.Profile is SkyrimNpcFinishCoreCombatProfile.RangedFirst or SkyrimNpcFinishCoreCombatProfile.MeleeFirst)
                    _ = BethesdaSkyrimNpcFinishCoreCombatStyle.PreferredEquipmentScore(template);
            }
            else if (targetNpc.CombatStyle.FormKeyNullable is { } styleKey)
            {
                ImmutableArray<Diagnostic>.Builder combatStyleDiagnostics =
                    ImmutableArray.CreateBuilder<Diagnostic>();
                IEnumerable<ICombatStyleGetter> styles = mod.CombatStyles
                    .Where(style => style.FormKey == styleKey);
                if (!TryRequireExactlyOne(
                        styles,
                        "finish-core-source-csty-count",
                        "CSTY",
                        combatStyleDiagnostics,
                        out ICombatStyleGetter styleRecord))
                {
                    Diagnostic diagnostic = combatStyleDiagnostics[0];
                    return Refused(expectedPlugin, targetFormId, targetEditorId,
                        diagnostic.Code, diagnostic.Message, pluginSha, rawCounts);
                }

                combatStyleMatches = IsDefensiveContract(styleRecord);
            }
            FormReference? defaultOutfit = ToReference(targetNpc.DefaultOutfit.FormKeyNullable);
            FinishCoreOutfitSelection? outfitSelection = null;
            if (request is not null)
            {
                outfitSelection = BethesdaSkyrimNpcFinishCoreOutfit.Read(mod, targetNpc.Race.FormKey, request, outfitMasterOrder);
                if (request.OutfitRacePolicy != SkyrimNpcFinishCoreOutfitRacePolicy.Clone &&
                    !outfitSelection.ExcludedArmatures.IsEmpty)
                {
                    Diagnostic excluded = BethesdaSkyrimNpcFinishCoreOutfit.Exclusion(
                        outfitSelection.ExcludedArmatures[0], outfitSelection.Race);
                    return Refused(expectedPlugin, targetFormId, targetEditorId,
                        excluded.Code, excluded.Message, pluginSha, rawCounts);
                }
            }
            ImmutableArray<SkyrimNpcFinishCoreInventoryEntry> inventory =
                (targetNpc.Items ?? [])
                    .Select(entry => new SkyrimNpcFinishCoreInventoryEntry(
                        ToReference(entry.Item.Item.FormKey), entry.Item.Count))
                    .ToImmutableArray();
            ImmutableArray<FormReference> packageLinks = (targetNpc.Packages ?? [])
                .Select(link => ToReference(link.FormKey))
                .ToImmutableArray();
            ImmutableArray<Diagnostic>.Builder packageDiagnostics =
                ImmutableArray.CreateBuilder<Diagnostic>();
            bool packageMatches = IsFinishCorePackage(
                mod.ModKey, mod.Packages, targetNpc, packageLinks, targetEditorId,
                packageDiagnostics);
            if (packageDiagnostics.Count != 0)
            {
                Diagnostic diagnostic = packageDiagnostics[0];
                return Refused(expectedPlugin, targetFormId, targetEditorId,
                    diagnostic.Code, diagnostic.Message, pluginSha, rawCounts);
            }
            ImmutableArray<SkyrimNpcFinishCoreRelationshipSnapshot> relationships =
                mod.Relationships
                    .Where(relationship =>
                        relationship.Parent.FormKey == targetNpc.FormKey ||
                        relationship.Child.FormKey == targetNpc.FormKey)
                    .Select(relationship => new SkyrimNpcFinishCoreRelationshipSnapshot(
                        ToReference(relationship.FormKey),
                        ToReference(relationship.Parent.FormKey),
                        ToReference(relationship.Child.FormKey),
                        relationship.Rank.ToString(),
                        (byte)relationship.Flags,
                        ToReference(relationship.AssociationType.FormKeyNullable)))
                    .ToImmutableArray();
            SkyrimNpcFinishCoreAiPolicy? aiData = targetNpc.AIData is { } ai
                ? new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = (SkyrimNpcFinishCoreAggression)(int)ai.Aggression,
                    Confidence = (SkyrimNpcFinishCoreConfidence)(int)ai.Confidence,
                    Energy = ai.EnergyLevel,
                    Morality = (SkyrimNpcFinishCoreMorality)(int)ai.Responsibility,
                    Assistance = (SkyrimNpcFinishCoreAssistance)(int)ai.Assistance,
                    Mood = ToFinishCoreMood(ai.Mood)
                }
                : null;
            return new SkyrimNpcFinishCorePluginSnapshot(
                true,
                expectedPlugin,
                pluginSha,
                new FormReference(expectedPlugin, targetFormId),
                targetEditorId,
                masters,
                mod.ModHeader.Stats.NextFormID,
                (uint)mod.ModHeader.Flags,
                occupied,
                (uint)targetNpc.Configuration.Flags,
                factionRanks,
                combatStyle,
                combatStyleMatches,
                defaultOutfit,
                inventory,
                packageLinks,
                relationships,
                typedCounts,
                rawCounts,
                false,
                [new Diagnostic(
                    "finish-core-source-plugin-admitted",
                    DiagnosticSeverity.Info,
                    "Typed and raw source-plugin inspection agree on the world-clean base NPC.")])
            {
                PackageMatchesFinishCoreContract = packageMatches,
                OutfitArmaturesToClone = outfitSelection?.ExcludedArmatures.Select(row => ToReference(row.FormKey)).ToImmutableArray() ?? [],
                OutfitArmorsToClone = outfitSelection?.ArmorsToClone.Select(row => ToReference(row.FormKey)).ToImmutableArray() ?? [],
                AiData = aiData,
                Perks = (targetNpc.Perks ?? []).Select(perk => new SkyrimNpcFinishCorePerk(
                    ToReference(perk.Perk.FormKey), perk.Rank)).ToImmutableArray()
            };
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or OverflowException or RecordException)
        {
            return Refused(expectedPlugin, targetFormId, targetEditorId,
                exception.Message.StartsWith("finish-core-combat-seed-required:", StringComparison.Ordinal)
                    ? "finish-core-combat-seed-required"
                    : OutfitFailureCode(exception, "finish-core-source-plugin-typed-invalid"), exception.Message,
                pluginSha, rawCounts);
        }
    }

    private static string OutfitFailureCode(Exception exception, string fallback)
    {
        int separator = exception.Message.IndexOf(':');
        return separator > 0 && exception.Message.StartsWith("finish-core-outfit-", StringComparison.Ordinal)
            ? exception.Message[..separator] : fallback;
    }

    private static SkyrimNpcFinishCorePluginSnapshot Refused(
        PluginName plugin,
        FormId target,
        EditorId editorId,
        string code,
        string message,
        Sha256Hash? pluginSha = null,
        IReadOnlyDictionary<string, int>? rawCounts = null,
        bool compressedTarget = false)
    {
        ImmutableDictionary<string, int> empty =
            ForbiddenSignatures.ToImmutableDictionary(
                signature => signature,
                signature => rawCounts?.GetValueOrDefault(signature) ?? 0,
                StringComparer.Ordinal);
        return new SkyrimNpcFinishCorePluginSnapshot(
            false,
            plugin,
            pluginSha ?? new Sha256Hash(new string('0', 64)),
            new FormReference(plugin, target),
            editorId,
            ImmutableArray<string>.Empty,
            0,
            0,
            ImmutableArray<string>.Empty,
            0,
            ImmutableArray<NpcFactionEntry>.Empty,
            null,
            false,
            null,
            ImmutableArray<SkyrimNpcFinishCoreInventoryEntry>.Empty,
            ImmutableArray<FormReference>.Empty,
            ImmutableArray<SkyrimNpcFinishCoreRelationshipSnapshot>.Empty,
            empty,
            empty,
            compressedTarget,
            [new Diagnostic(code, DiagnosticSeverity.Error, message)
            {
                Recovery = code == "finish-core-combat-seed-required"
                    ? new DiagnosticRecovery(RecoveryAction.Reanalyze, "request", null,
                        "Set combatPolicy.seedLocalStyle=true for the referenced master CSTY, retain its copied master/provider hash authority, rehash the request and reanalyze. Other source validation and human review requirements still apply.", false)
                        { AlternativeCommand = "npc finish analyze" }
                    : null
            }]);
    }

    private static ImmutableArray<RawRecord> ReadRawRecords(byte[] bytes)
    {
        if (bytes.Length < 24 ||
            !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The source plugin must begin with TES4.");
        var records = ImmutableArray.CreateBuilder<RawRecord>();
        int firstRecord = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        if (firstRecord > bytes.Length)
            throw new InvalidDataException("The TES4 header exceeds the source plugin.");
        Walk(firstRecord, bytes.Length, records, bytes);
        return records.ToImmutable();
    }

    private static void Walk(
        int start,
        int end,
        ImmutableArray<RawRecord>.Builder records,
        byte[] bytes)
    {
        int position = start;
        while (position < end)
        {
            if (position + 24 > end)
                throw new InvalidDataException("A TES4 record header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                if (size < 24 || size > int.MaxValue)
                    throw new InvalidDataException("A TES4 group has an invalid size.");
                int groupEnd = checked(position + (int)size);
                if (groupEnd > end)
                    throw new InvalidDataException("A TES4 group exceeds its containing group.");
                Walk(position + 24, groupEnd, records, bytes);
                position = groupEnd;
                continue;
            }
            if (size > int.MaxValue || position + 24 + (int)size > end)
                throw new InvalidDataException("A TES4 record exceeds its containing group.");
            int recordEnd = checked(position + 24 + (int)size);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 8, 4));
            uint formId = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 12, 4));
            records.Add(new RawRecord(signature, formId, flags));
            position = recordEnd;
        }
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static FormReference ToReference(FormKey key) =>
        new FormReference(
            new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static FormReference? ToReference(FormKey? key) =>
        key is { } value && !value.IsNull
            ? new FormReference(
                new PluginName(value.ModKey.ToString()), new FormId(value.ID))
            : null;

    private static SkyrimNpcFinishCoreMood ToFinishCoreMood(Mood mood) =>
        mood switch
        {
            Mood.Neutral => SkyrimNpcFinishCoreMood.Neutral,
            Mood.Angry => SkyrimNpcFinishCoreMood.Angry,
            Mood.Fear => SkyrimNpcFinishCoreMood.Fear,
            Mood.Happy => SkyrimNpcFinishCoreMood.Happy,
            Mood.Sad => SkyrimNpcFinishCoreMood.Sad,
            Mood.Surprised => SkyrimNpcFinishCoreMood.Surprise,
            Mood.Puzzled => SkyrimNpcFinishCoreMood.Puzzled,
            Mood.Disgusted => SkyrimNpcFinishCoreMood.Disgusted,
            _ => throw new InvalidDataException(
                $"Unsupported Mutagen Skyrim mood '{mood}' ({(int)mood}).")
        };

    private static bool IsDefensiveContract(ICombatStyleGetter style) =>
        style.OffensiveMult == 0f && style.DefensiveMult == 1f &&
        style.GroupOffensiveMult == 0f &&
        style.EquipmentScoreMultMelee == 0f &&
        style.EquipmentScoreMultMagic == 0f &&
        style.EquipmentScoreMultRanged == 0f &&
        style.EquipmentScoreMultShout == 0f &&
        style.EquipmentScoreMultUnarmed == 0f &&
        style.EquipmentScoreMultStaff == 0f && style.AvoidThreatChance == 1f &&
        style.CloseRange is { FallbackMult: 1f } &&
        style.Flight is { HoverChance: 0f, DiveBombChance: 0f,
            GroundAttackChance: 0f, PerchAttackChance: 0f,
            FlyingAttackChance: 0f };

    private static bool IsFinishCorePackage(
        ModKey modKey,
        IEnumerable<IPackageGetter> packages,
        INpcGetter targetNpc,
        ImmutableArray<FormReference> packageLinks,
        EditorId targetEditorId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (packageLinks.Length == 0)
            return false;
        if (!TryRequireExactlyOne(
                packageLinks,
                "finish-core-source-pack-count",
                "PACK",
                diagnostics,
                out FormReference packageLink) ||
            !string.Equals(packageLink.Plugin.Value, modKey.FileName.ToString(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        FormKey packageKey = new(modKey, packageLink.FormId.Value);
        if (!TryRequireExactlyOne(
                packages.Where(row => row.FormKey == packageKey),
                "finish-core-source-pack-count",
                "PACK",
                diagnostics,
                out IPackageGetter package))
            return false;
        if (!string.Equals(package.EditorID,
                BuildEditorId(targetEditorId.Value, "FinishCoreSandbox"),
                StringComparison.Ordinal) ||
            package.OwnerQuest.FormKeyNullable is not null ||
            package.VirtualMachineAdapter is not null)
        {
            return false;
        }

        if (!TryRequireExactlyOne(
                package.Conditions,
                "finish-core-source-pack-condition-count",
                "PACK condition",
                diagnostics,
                out IConditionGetter condition))
            return false;
        if (!TryRequireExactlyOne(
                package.Data.Values.OfType<PackageDataLocation>(),
                "finish-core-source-pack-location-count",
                "PACK location",
                diagnostics,
                out PackageDataLocation location) ||
            location.Location is not LocationTargetRadius
            {
                Radius: 512,
                Target: LocationFallback fallback
            } ||
            !string.Equals(fallback.Type.ToString(), "NearEditorLocation", StringComparison.Ordinal) ||
            fallback.Data != 0)
        {
            return false;
        }

        GetFactionRankConditionData? data = condition?.Data as GetFactionRankConditionData;
        FormKey currentFollower = new(
            ModKey.FromNameAndExtension("Skyrim.esm"), 0x0005C84E);
        bool conditionMatch = condition is not null &&
               condition.CompareOperator == CompareOperator.LessThan &&
               HasZeroComparisonValue(condition) && data is
               {
                   RunOnType: Condition.RunOnType.Subject,
               } && data.Faction.Link.FormKey == currentFollower;
        if (!TryRequireExactlyOne(
                targetNpc.Packages.Where(row => row.FormKey == packageKey),
                "finish-core-source-pack-count",
                "PACK",
                diagnostics,
                out _))
            return false;
        bool schedule = package.ScheduleMonth == -1 &&
               package.ScheduleDayOfWeek == Package.DayOfWeek.Any &&
               package.ScheduleDate == 0 &&
               package.ScheduleHour == -1 &&
               package.ScheduleMinute == -1 &&
               package.ScheduleDurationInMinutes == 0;
        return conditionMatch && schedule;
    }

    private static Diagnostic? FindLinkedRecordDuplicate(
        BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot rawSnapshot,
        ImmutableArray<BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord> targetSubrecords,
        uint targetRawFormId,
        string linkSignature,
        string recordSignature,
        string code,
        string identity)
    {
        BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord[] links = targetSubrecords
            .Where(row => row.Signature == linkSignature)
            .Take(2)
            .ToArray();
        if (links.Length == 0)
            return null;
        if (links.Length > 1)
            return new Diagnostic(
                code,
                DiagnosticSeverity.Error,
                ExactOneMessage(identity, links.Length));

        BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord link = links[0];
        if (link.Bytes.Length != 10)
            throw new InvalidDataException(
                $"The target NPC {linkSignature} subrecord must contain exactly 4 payload bytes.");
        uint linkedRawFormId = BinaryPrimitives.ReadUInt32LittleEndian(
            link.Bytes.AsSpan(6, 4));
        if (!IsSelfOwnedRawFormId(linkedRawFormId, targetRawFormId))
            return null;

        BethesdaSkyrimNpcFinishCoreRaw.RawRecord[] records = rawSnapshot.Records
            .Where(row => row.Signature == recordSignature &&
                          row.RawFormId == linkedRawFormId)
            .Take(2)
            .ToArray();
        return records.Length > 1
            ? new Diagnostic(
                code,
                DiagnosticSeverity.Error,
                ExactOneMessage(identity, records.Length))
            : null;
    }

    private static bool IsSelfOwnedRawFormId(
        uint rawFormId,
        uint targetRawFormId) =>
        rawFormId != 0 &&
        ((rawFormId ^ targetRawFormId) & 0xFF00_0000u) == 0;

    private static bool TryRequireExactlyOne<T>(
        IEnumerable<T> source,
        string code,
        string identity,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out T value)
    {
        T[] matches = source.Take(2).ToArray();
        if (matches.Length == 1)
        {
            value = matches[0];
            return true;
        }

        string observed = matches.Length == 2
            ? "at least 2"
            : matches.Length.ToString(CultureInfo.InvariantCulture);
        diagnostics.Add(new Diagnostic(
            code,
            DiagnosticSeverity.Error,
            $"{identity} requires exactly 1 record; observed {observed} " +
            $"(expected=1; observed={observed})."));
        value = default!;
        return false;
    }

    private static string ExactOneMessage(string identity, int observedCount)
    {
        string observed = observedCount == 2
            ? "at least 2"
            : observedCount.ToString(CultureInfo.InvariantCulture);
        return $"{identity} requires exactly 1 record; observed {observed} " +
            $"(expected=1; observed={observed}).";
    }

    private static string RecordIdentity(string signature, uint formId) =>
        $"{signature} {new FormId(formId)}";

    private static string BuildEditorId(string editorId, string suffix)
    {
        string text = "_" + suffix;
        int length = Math.Min(editorId.Length, 64 - text.Length);
        return editorId[..length] + text;
    }

    private static bool HasZeroComparisonValue(IConditionGetter condition)
    {
        object? value = condition.GetType().GetProperty("ComparisonValue")?.GetValue(condition);
        return value is not null && Convert.ToSingle(value, CultureInfo.InvariantCulture) == 0f;
    }

    private static uint LocalFormId(uint rawFormId) => rawFormId & 0x00FF_FFFFu;

    private sealed record RawRecord(string Signature, uint FormId, uint Flags);
}
