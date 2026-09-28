using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaNpcOverrideOwnershipSnapshot(
    ImmutableArray<PluginName> TypedMasters,
    ImmutableArray<PluginName> RawMasters,
    int MajorRecordCount,
    int NpcRecordCount,
    int SourceOwnedTargetCount,
    int SelfOwnedTargetCount,
    ImmutableArray<RecordSignature> MajorRecordSignatures);

/// <summary>
/// Minimal Skyrim override writer. It creates a fresh output mod, declares the
/// source plugin and its dependencies as masters, and carries only the selected
/// source-owned NPC record. It never deep-copies the complete source mod.
/// </summary>
public static class BethesdaNpcOverrideAdapter
{
    private const int MajorRecordHeaderSize = 24;
    private const int SubrecordHeaderSize = 6;

    public static ImmutableArray<PluginName> ReadRequiredMasters(
        WorkspacePath sourcePlugin)
    {
        var sourceKey = ModKey.FromNameAndExtension(Path.GetFileName(sourcePlugin.Value));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceKey, new FilePath(sourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        return overlay.ModHeader.MasterReferences
            .Select(item => new PluginName(item.Master.FileName.String))
            .Append(new PluginName(sourceKey.FileName.String))
            .Distinct()
            .ToImmutableArray();
    }

    public static void Write(
        NpcOverrideRequest request,
        WorkspacePath destination)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            throw new InvalidDataException("The true NPC override writer supports Skyrim SE/AE only.");

        var sourceKey = ModKey.FromNameAndExtension(Path.GetFileName(request.SourcePlugin.Value));
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(request.OutputPlugin.Value));
        if (sourceKey == outputKey)
            throw new InvalidDataException("The override plugin must have a different name from its source plugin.");

        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceKey, new FilePath(request.SourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var sourceNpc = overlay.Npcs.FirstOrDefault(item =>
            item.FormKey == new FormKey(sourceKey, request.TargetFormId.Value)) ??
            throw new InvalidDataException(
                $"NPC {request.TargetFormId} is not owned by {sourceKey.FileName}.");

        var output = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        foreach (var master in overlay.ModHeader.MasterReferences
                     .Select(item => item.Master)
                     .Append(sourceKey)
                     .Where(item => item != outputKey)
                     .Distinct())
        {
            output.ModHeader.MasterReferences.Add(new MasterReference { Master = master });
        }

        var overrideNpc = sourceNpc.DeepCopy();
        BethesdaNpcMutationAdapter.ApplySkyrimMutation(
            overrideNpc,
            ToMutationRequest(request, destination));
        output.Npcs.Add(overrideNpc);
        output.WriteToBinary(new FilePath(destination.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck
        });
    }

    public static BethesdaNpcOverrideOwnershipSnapshot InspectOwnership(
        WorkspacePath outputPlugin,
        PluginName outputPluginName,
        PluginName sourcePlugin,
        FormId targetFormId)
    {
        var outputKey = ModKey.FromNameAndExtension(outputPluginName.Value);
        var sourceKey = ModKey.FromNameAndExtension(sourcePlugin.Value);
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(outputKey, new FilePath(outputPlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var records = overlay.EnumerateMajorRecords().ToArray();
        var sourceTarget = new FormKey(sourceKey, targetFormId.Value);
        var selfTarget = new FormKey(outputKey, targetFormId.Value);
        return new BethesdaNpcOverrideOwnershipSnapshot(
            overlay.ModHeader.MasterReferences
                .Select(item => new PluginName(item.Master.FileName.String))
                .ToImmutableArray(),
            ReadRawMasters(outputPlugin),
            records.Length,
            overlay.Npcs.Count,
            overlay.Npcs.Count(item => item.FormKey == sourceTarget),
            overlay.Npcs.Count(item => item.FormKey == selfTarget),
            records.Select(ToRecordSignature).ToImmutableArray());
    }

    private static RecordSignature ToRecordSignature(IMajorRecordGetter record) => record switch
    {
        INpcGetter => new RecordSignature("NPC_"),
        IColorRecordGetter => new RecordSignature("CLFM"),
        ITextureSetGetter => new RecordSignature("TXST"),
        IHeadPartGetter => new RecordSignature("HDPT"),
        IArmorGetter => new RecordSignature("ARMO"),
        IArmorAddonGetter => new RecordSignature("ARMA"),
        IOutfitGetter => new RecordSignature("OTFT"),
        _ => throw new InvalidDataException(
            $"The override contains unsupported major record type {record.GetType().Name}.")
    };

    private static NpcMutationRequest ToMutationRequest(
        NpcOverrideRequest request,
        WorkspacePath destination) =>
        new(
            request.Edition,
            request.SourcePlugin,
            destination,
            request.TargetFormId,
            request.Patch.EditorId,
            request.Patch.Name,
            request.Patch.Weight,
            request.ExpectedSourceSha256,
            false,
            null,
            Archetype: request.Patch.Archetype,
            Stats: request.Patch.Stats,
            KeywordPatch: request.Patch.Keywords,
            FactionPatch: request.Patch.Factions,
            InventoryPatch: request.Patch.Inventory,
            OutfitPatch: request.Patch.Outfits,
            PerkPatch: request.Patch.Perks,
            ActorEffectPatch: request.Patch.ActorEffects,
            Names: request.Patch.Names);

    internal static ImmutableArray<PluginName> ReadRawMasters(WorkspacePath plugin)
    {
        var bytes = File.ReadAllBytes(plugin.Value);
        if (bytes.Length < MajorRecordHeaderSize ||
            !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The output does not begin with a TES4 header record.");
        var dataSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        var end = checked(MajorRecordHeaderSize + dataSize);
        if (end > bytes.Length)
            throw new InvalidDataException("The TES4 header payload exceeds the output length.");

        var masters = ImmutableArray.CreateBuilder<PluginName>();
        var position = MajorRecordHeaderSize;
        int? extendedSize = null;
        while (position < end)
        {
            if (end - position < SubrecordHeaderSize)
                throw new InvalidDataException("The TES4 header ends inside a subrecord header.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            position += SubrecordHeaderSize;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || end - position < 4)
                    throw new InvalidDataException("The TES4 header contains a malformed XXXX size marker.");
                extendedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position, 4)));
                position += 4;
                continue;
            }

            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size < 0 || size > end - position)
                throw new InvalidDataException($"TES4 subrecord {signature} exceeds its record bounds.");
            if (signature == "MAST")
            {
                var payload = bytes.AsSpan(position, size);
                var nul = payload.IndexOf((byte)0);
                if (nul <= 0 || nul != payload.Length - 1)
                    throw new InvalidDataException("A TES4 MAST value is not one null-terminated plugin name.");
                masters.Add(new PluginName(Encoding.UTF8.GetString(payload[..nul])));
            }
            position += size;
        }
        if (extendedSize is not null)
            throw new InvalidDataException("The TES4 header ends after an XXXX marker.");
        return masters.ToImmutable();
    }
}
