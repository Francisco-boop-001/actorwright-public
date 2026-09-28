using System.Collections.Immutable;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaPluginReader : IPluginReader
{
    public ValueTask<PluginInspection> ReadAsync(PluginReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PluginPath.Value;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The explicit plugin path does not exist.", path);
        }

        var plugin = new PluginName(Path.GetFileName(path));
        return ValueTask.FromResult(request.Edition switch
        {
            GameEdition.Fallout4 => ReadFallout4(path, plugin),
            GameEdition.SkyrimSpecialEdition => ReadSkyrim(path, plugin, request.NormalizedMasterOrder, request.AllowSkeletalWorldParents, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Edition, "Unsupported game edition.")
        });
    }

    private static PluginInspection ReadSkyrim(
        string path,
        PluginName plugin,
        ImmutableArray<PluginName> normalizedMasters,
        bool allowSkeletalWorldParents,
        CancellationToken cancellationToken)
    {
        BethesdaSkyrimRawTopologyPreflight.Validate(path, cancellationToken);
        if (allowSkeletalWorldParents && ReadSkeletalWorldAudit(path, plugin, normalizedMasters) is { } rawAudit)
            return rawAudit;
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var metadata = BethesdaNpcMetadataReader.Read(path);
        var rawNames = BethesdaRawNpcNameReader.Read(path);
        var rawDigests = normalizedMasters.IsDefault
            ? BethesdaRawRecordDigestReader.Read(path, exactGroupLabels: allowSkeletalWorldParents)
            : ReadNormalizedDigests(path, plugin, normalizedMasters);
        var masterKeys = mod.MasterReferences
            .Select(reference => reference.Master)
            .ToImmutableArray();
        var masters = masterKeys
            .Select(master => new PluginName(master.ToString()))
            .ToImmutableArray();
        var records = mod.EnumerateMajorRecords()
            .Select(record => ToSummary(
                record,
                record is Mutagen.Bethesda.Skyrim.INpcGetter,
                RawFormId(record, mod.ModKey, masterKeys),
                metadata.Metadata,
                metadata.Signatures,
                rawNames,
                rawDigests))
            .OrderBy(record => record.FormId.Value)
            .ThenBy(record => record.Signature, StringComparer.Ordinal)
            .ToImmutableArray();
        return new PluginInspection(GameEdition.SkyrimSpecialEdition, plugin, masters, records, metadata.Diagnostics);
    }

    private static Dictionary<(uint RawFormId, string Signature), string> ReadNormalizedDigests(
        string path, PluginName plugin, ImmutableArray<PluginName> masters)
    {
        byte[] original = File.ReadAllBytes(path);
        var source = SkyrimMod.CreateFromBinary(
            new ModPath(ModKey.FromNameAndExtension(plugin.Value), new Noggog.FilePath(path)), SkyrimRelease.SkyrimSE);
        byte[] normalized = BethesdaSkyrimMasterIndexRewriter.RewriteRecordIndices(
            original, source, masters.Select(item => item.Value).ToImmutableArray());
        var hashes = BethesdaRawRecordDigestReader.Read(normalized, exactGroupLabels: true);
        return BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(original).NonTes4Records.ToDictionary(
            row => (row.RawFormId, row.Signature),
            row => hashes[(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                normalized.AsSpan(row.Offset + 12, 4)), row.Signature)]);
    }

    private static PluginInspection ReadFallout4(string path, PluginName plugin)
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(path, Fallout4Release.Fallout4);
        var metadata = BethesdaNpcMetadataReader.Read(path);
        var rawNames = BethesdaRawNpcNameReader.Read(path);
        var rawDigests = BethesdaRawRecordDigestReader.Read(path);
        var masterKeys = mod.MasterReferences
            .Select(reference => reference.Master)
            .ToImmutableArray();
        var masters = masterKeys
            .Select(master => new PluginName(master.ToString()))
            .ToImmutableArray();
        var records = mod.EnumerateMajorRecords()
            .Select(record => ToSummary(
                record,
                record is Mutagen.Bethesda.Fallout4.INpcGetter,
                RawFormId(record, mod.ModKey, masterKeys),
                metadata.Metadata,
                metadata.Signatures,
                rawNames,
                rawDigests))
            .OrderBy(record => record.FormId.Value)
            .ThenBy(record => record.Signature, StringComparer.Ordinal)
            .ToImmutableArray();
        return new PluginInspection(GameEdition.Fallout4, plugin, masters, records, metadata.Diagnostics);
    }

    private static PluginRecordSummary ToSummary(
        IMajorRecordGetter record,
        bool isNpc,
        uint rawFormId,
        ImmutableDictionary<uint, NpcRecordMetadata> metadata,
        ImmutableDictionary<uint, string> signatures,
        IReadOnlyDictionary<uint, string> rawNames,
        IReadOnlyDictionary<(uint FormId, string Signature), string> rawDigests)
    {
        if (record is not IFormKeyGetter keyed)
        {
            throw new InvalidDataException("A major record did not expose a typed FormKey.");
        }
        var name = rawNames.GetValueOrDefault(rawFormId) ?? record switch
        {
            Mutagen.Bethesda.Skyrim.INpcGetter npc => npc.Name?.String,
            Mutagen.Bethesda.Fallout4.INpcGetter npc => npc.Name?.String,
            _ => null
        };
        var signature = signatures.GetValueOrDefault(rawFormId) ?? record.GetType().Name;
        if (signature.EndsWith("BinaryOverlay", StringComparison.Ordinal)) signature = signature[..^"BinaryOverlay".Length];
        if (isNpc) signature = "NPC_";
        var rawDigest = rawDigests.GetValueOrDefault((rawFormId, signature.ToUpperInvariant()));
        var npcMetadata = isNpc && metadata.TryGetValue(rawFormId, out var parsedMetadata)
            ? parsedMetadata with
            {
                HeadPartFormIds = TryReadHeadPartFormIds(record),
                Race = TryReadRaceReference(record),
                HeadParts = TryReadHeadPartReferences(record)
            }
            : null;
        return new PluginRecordSummary(
            new FormId(keyed.FormKey.ID),
            signature,
            record.EditorID,
            name,
            isNpc,
            record.IsDeleted,
            npcMetadata,
            TryReadModelPath(record),
            TryReadOutfitItems(record),
            rawDigest,
            new PluginName(keyed.FormKey.ModKey.ToString()),
            TryReadOutfitItemReferences(record));
    }

    private static uint RawFormId(
        IMajorRecordGetter record,
        ModKey self,
        ImmutableArray<ModKey> masters)
    {
        if (record is not IFormKeyGetter keyed)
            throw new InvalidDataException(
                "A major record did not expose a typed FormKey.");
        var ownerIndex = keyed.FormKey.ModKey == self
            ? masters.Length
            : masters.IndexOf(keyed.FormKey.ModKey);
        if (ownerIndex is < 0 or > byte.MaxValue ||
            keyed.FormKey.ID > 0x00FF_FFFF)
        {
            throw new InvalidDataException(
                $"Record {keyed.FormKey} cannot be represented by this plugin's ordinary master table.");
        }
        return ((uint)ownerIndex << 24) | keyed.FormKey.ID;
    }

    private static ImmutableArray<FormId> TryReadHeadPartFormIds(IMajorRecordGetter record) => record switch
    {
        Mutagen.Bethesda.Skyrim.INpcGetter npc => npc.HeadParts
            .Select(link => new FormId(link.FormKey.ID)).ToImmutableArray(),
        Mutagen.Bethesda.Fallout4.INpcGetter npc => npc.HeadParts
            .Select(link => new FormId(link.FormKey.ID)).ToImmutableArray(),
        _ => []
    };

    private static FormReference? TryReadRaceReference(IMajorRecordGetter record) => record switch
    {
        Mutagen.Bethesda.Skyrim.INpcGetter npc when !npc.Race.FormKey.IsNull =>
            Reference(npc.Race.FormKey),
        Mutagen.Bethesda.Fallout4.INpcGetter npc when !npc.Race.FormKey.IsNull =>
            Reference(npc.Race.FormKey),
        _ => null
    };

    private static ImmutableArray<FormReference> TryReadHeadPartReferences(
        IMajorRecordGetter record) => record switch
        {
            Mutagen.Bethesda.Skyrim.INpcGetter npc => npc.HeadParts
                .Select(link => Reference(link.FormKey)).ToImmutableArray(),
            Mutagen.Bethesda.Fallout4.INpcGetter npc => npc.HeadParts
                .Select(link => Reference(link.FormKey)).ToImmutableArray(),
            _ => []
        };

    private static FormReference Reference(FormKey key) =>
        new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static AssetPath? TryReadModelPath(IMajorRecordGetter record)
    {
        var path = record switch
        {
            Mutagen.Bethesda.Skyrim.IHeadPartGetter headPart => headPart.Model?.File?.ToString(),
            Mutagen.Bethesda.Fallout4.IHeadPartGetter headPart => headPart.Model?.File?.ToString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return new AssetPath(path); }
        catch (ArgumentException) { return null; }
    }

    private static ImmutableArray<FormId> TryReadOutfitItems(IMajorRecordGetter record) => record switch
    {
        Mutagen.Bethesda.Skyrim.IOutfitGetter outfit => (outfit.Items ?? []).Select(item => new FormId(item.FormKey.ID)).ToImmutableArray(),
        Mutagen.Bethesda.Fallout4.IOutfitGetter outfit => (outfit.Items ?? []).Select(item => new FormId(item.FormKey.ID)).ToImmutableArray(),
        _ => []
    };

    private static ImmutableArray<FormReference> TryReadOutfitItemReferences(
        IMajorRecordGetter record) => record switch
        {
            Mutagen.Bethesda.Skyrim.IOutfitGetter outfit => (outfit.Items ?? [])
                .Select(item => Reference(item.FormKey)).ToImmutableArray(),
            Mutagen.Bethesda.Fallout4.IOutfitGetter outfit => (outfit.Items ?? [])
                .Select(item => Reference(item.FormKey)).ToImmutableArray(),
            _ => []
        };
}
