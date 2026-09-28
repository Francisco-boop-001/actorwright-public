using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Projects only NPC_ and LVLN records from one exact Skyrim SE plugin.
/// Provider identity comes from the opened file; source ownership comes from
/// each record's FormKey. No link cache or live game path is involved.
/// </summary>
public sealed class BethesdaSkyrimMainWorkspaceReader
    : ISkyrimMainWorkspacePluginReader
{
    public SkyrimMainWorkspacePluginReadResult Read(
        WorkspacePath pluginPath)
    {
        string path = pluginPath.Value;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The reviewed Skyrim plugin does not exist.", path);

        PluginName provider = new(Path.GetFileName(path));
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            path, SkyrimRelease.SkyrimSE);
        ImmutableArray<ModKey> masterKeys = mod.MasterReferences
            .Select(reference => reference.Master)
            .ToImmutableArray();
        ImmutableArray<PluginName> masters = masterKeys
            .Select(master => new PluginName(master.ToString()))
            .ToImmutableArray();
        var knownOwners = masterKeys
            .Append(mod.ModKey)
            .ToHashSet();
        NpcMetadataScanResult metadata = BethesdaNpcMetadataReader.Read(path);
        IReadOnlyDictionary<uint, string> rawNames =
            BethesdaRawNpcNameReader.Read(path);
        IReadOnlyDictionary<(uint FormId, string Signature), string>
            rawDigests = BethesdaRawRecordDigestReader.Read(path);
        HashSet<FormKey> placedNpcs = mod.EnumerateMajorRecords()
            .OfType<IPlacedNpcGetter>()
            .Select(placed => placed.Base.FormKey)
            .Where(key => !key.IsNull)
            .ToHashSet();
        HashSet<FormKey> leveledNpcs = mod.LeveledNpcs
            .SelectMany(list => list.Entries ?? [])
            .Where(entry => entry.Data is not null)
            .Select(entry => entry.Data!.Reference.FormKey)
            .Where(key => !key.IsNull)
            .ToHashSet();
        HashSet<FormKey> templateSources = mod.Npcs
            .Select(npc => npc.Template.FormKeyNullable)
            .Where(key => key.HasValue)
            .Select(key => key!.Value)
            .ToHashSet();
        var records =
            ImmutableArray.CreateBuilder<SkyrimMainWorkspacePluginRecord>();

        foreach (INpcGetter npc in mod.Npcs)
        {
            ValidateOwner(npc.FormKey, knownOwners, provider);
            uint rawFormId = RawFormId(
                npc.FormKey, mod.ModKey, masterKeys);
            NpcRecordMetadata npcMetadata =
                metadata.Metadata.TryGetValue(
                    rawFormId, out NpcRecordMetadata? scanned)
                    ? EnrichMetadata(scanned, npc)
                    : TypedMetadata(
                        npc,
                        placedNpcs,
                        leveledNpcs,
                        templateSources);
            records.Add(new SkyrimMainWorkspacePluginRecord(
                provider,
                new PluginName(npc.FormKey.ModKey.ToString()),
                new FormId(npc.FormKey.ID),
                "NPC_",
                npc.EditorID,
                rawNames.GetValueOrDefault(rawFormId) ??
                    npc.Name?.String,
                npc.IsDeleted,
                npcMetadata,
                [],
                RawDigest(rawDigests, rawFormId, "NPC_", npc.FormKey)));
        }

        foreach (ILeveledNpcGetter list in mod.LeveledNpcs)
        {
            ValidateOwner(list.FormKey, knownOwners, provider);
            uint rawFormId = RawFormId(
                list.FormKey, mod.ModKey, masterKeys);
            var entries = ImmutableArray.CreateBuilder<FormReference>();
            foreach (ILeveledNpcEntryGetter entry in list.Entries ?? [])
            {
                if (entry.Data is null)
                    throw new InvalidDataException(
                        $"LVLN {list.FormKey} contains an entry without data.");
                FormKey reference = entry.Data.Reference.FormKey;
                if (reference.IsNull)
                    throw new InvalidDataException(
                        $"LVLN {list.FormKey} contains a null entry.");
                ValidateOwner(reference, knownOwners, provider);
                entries.Add(new FormReference(
                    new PluginName(reference.ModKey.ToString()),
                    new FormId(reference.ID)));
            }
            records.Add(new SkyrimMainWorkspacePluginRecord(
                provider,
                new PluginName(list.FormKey.ModKey.ToString()),
                new FormId(list.FormKey.ID),
                "LVLN",
                list.EditorID,
                list.EditorID,
                list.IsDeleted,
                null,
                entries.ToImmutable(),
                RawDigest(rawDigests, rawFormId, "LVLN", list.FormKey)));
        }

        return new SkyrimMainWorkspacePluginReadResult(
            provider,
            masters,
            records
                .OrderBy(record => record.Signature, StringComparer.Ordinal)
                .ThenBy(record => record.FormId.Value)
                .ToImmutableArray());
    }

    private static NpcRecordMetadata EnrichMetadata(
        NpcRecordMetadata metadata,
        INpcGetter npc) =>
        metadata with
        {
            HeadPartFormIds = npc.HeadParts
                .Select(link => new FormId(link.FormKey.ID))
                .ToImmutableArray(),
            Race = npc.Race.FormKey.IsNull
                ? null
                : Reference(npc.Race.FormKey),
            HeadParts = npc.HeadParts
                .Where(link => !link.FormKey.IsNull)
                .Select(link => Reference(link.FormKey))
                .ToImmutableArray()
        };

    private static NpcRecordMetadata TypedMetadata(
        INpcGetter npc,
        HashSet<FormKey> placedNpcs,
        HashSet<FormKey> leveledNpcs,
        HashSet<FormKey> templateSources)
    {
        FormKey? template = npc.Template.FormKeyNullable;
        return new NpcRecordMetadata(
            npc.Configuration.Flags.HasFlag(
                NpcConfiguration.Flag.Female)
                ? NpcSex.Female
                : NpcSex.Male,
            npc.Race.FormKey.IsNull
                ? null
                : new FormId(npc.Race.FormKey.ID),
            npc.Weight,
            null,
            null,
            null,
            npc.Configuration.TemplateFlags != 0 ||
            template.HasValue,
            template.HasValue
                ? new FormId(template.Value.ID)
                : null,
            templateSources.Contains(npc.FormKey),
            placedNpcs.Contains(npc.FormKey),
            leveledNpcs.Contains(npc.FormKey),
            npc.Configuration.Flags.HasFlag(
                NpcConfiguration.Flag.IsCharGenFacePreset),
            npc.HeadParts
                .Select(link => new FormId(link.FormKey.ID))
                .ToImmutableArray(),
            npc.Race.FormKey.IsNull
                ? null
                : Reference(npc.Race.FormKey),
            npc.HeadParts
                .Where(link => !link.FormKey.IsNull)
                .Select(link => Reference(link.FormKey))
                .ToImmutableArray());
    }

    private static FormReference Reference(FormKey key) =>
        new(
            new PluginName(key.ModKey.ToString()),
            new FormId(key.ID));

    private static void ValidateOwner(
        FormKey key,
        HashSet<ModKey> knownOwners,
        PluginName provider)
    {
        if (key.IsNull || !knownOwners.Contains(key.ModKey))
            throw new InvalidDataException(
                $"Plugin '{provider}' contains an unresolved FormKey '{key}'.");
    }

    private static uint RawFormId(
        FormKey key,
        ModKey self,
        ImmutableArray<ModKey> masters)
    {
        int ownerIndex = key.ModKey == self
            ? masters.Length
            : masters.IndexOf(key.ModKey);
        if (ownerIndex is < 0 or > byte.MaxValue ||
            key.ID > 0x00FF_FFFF)
            throw new InvalidDataException(
                $"Record {key} cannot be represented by the plugin master table.");
        return ((uint)ownerIndex << 24) | key.ID;
    }

    private static Sha256Hash RawDigest(
        IReadOnlyDictionary<(uint FormId, string Signature), string>
            rawDigests,
        uint rawFormId,
        string signature,
        FormKey formKey)
    {
        if (!rawDigests.TryGetValue(
                (rawFormId, signature), out string? digest))
            throw new InvalidDataException(
                $"{signature} {formKey} did not have one bounded raw-record digest.");
        return new Sha256Hash(digest);
    }
}
