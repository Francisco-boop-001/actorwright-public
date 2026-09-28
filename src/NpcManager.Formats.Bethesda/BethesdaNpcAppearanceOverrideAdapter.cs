using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaNpcAppearanceOverrideSourceSnapshot(
    EditorId EditorId,
    ImmutableArray<PluginName> Masters,
    ImmutableArray<string> ScriptNames);

/// <summary>
/// Skyrim binary boundary for a complete appearance-only override. The target
/// retains its source FormKey; only explicitly requested output-owned
/// appearance support records may be added to the fresh output plugin.
/// </summary>
public static class BethesdaNpcAppearanceOverrideAdapter
{
    public static bool RaceExists(
        WorkspacePath dataRoot,
        FormReference race)
    {
        var providerPath = Path.Combine(dataRoot.Value, race.Plugin.Value);
        if (!File.Exists(providerPath)) return false;
        var providerKey = ModKey.FromNameAndExtension(race.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerKey, new FilePath(providerPath)),
            SkyrimRelease.SkyrimSE);
        return provider.Races.Any(item => item.FormKey == new FormKey(
            providerKey,
            race.FormId.Value));
    }

    public static bool OutfitExists(
        WorkspacePath dataRoot,
        FormReference outfit)
    {
        var providerPath = Path.Combine(dataRoot.Value, outfit.Plugin.Value);
        if (!File.Exists(providerPath)) return false;
        var providerKey = ModKey.FromNameAndExtension(outfit.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerKey, new FilePath(providerPath)),
            SkyrimRelease.SkyrimSE);
        return provider.Outfits.Any(item => item.FormKey == new FormKey(
            providerKey,
            outfit.FormId.Value));
    }

    public static BethesdaNpcAppearanceOverrideSourceSnapshot ReadSource(
        WorkspacePath sourcePlugin,
        FormId targetFormId)
    {
        var sourceKey = ModKey.FromNameAndExtension(Path.GetFileName(sourcePlugin.Value));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceKey, new FilePath(sourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs.FirstOrDefault(item =>
            item.FormKey == new FormKey(sourceKey, targetFormId.Value)) ??
            throw new InvalidDataException(
                $"NPC {targetFormId} is not owned by {sourceKey.FileName}.");
        if (string.IsNullOrWhiteSpace(npc.EditorID))
            throw new InvalidDataException(
                "The selected NPC requires an EditorID before appearance support records can be named safely.");
        return new BethesdaNpcAppearanceOverrideSourceSnapshot(
            new EditorId(npc.EditorID),
            overlay.ModHeader.MasterReferences
                .Select(item => new PluginName(item.Master.FileName.String))
                .ToImmutableArray(),
            npc.VirtualMachineAdapter?.Scripts
                .Select(item => item.Name)
                .ToImmutableArray() ?? []);
    }

    public static ImmutableArray<PluginName> BuildRequiredMasters(
        NpcAppearanceOverrideRequest request,
        BethesdaNpcAppearanceOverrideSourceSnapshot source)
    {
        var sourceName = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var baseMasters = source.Masters
            .Append(sourceName)
            .Append(request.Race.Plugin)
            .Concat(OutfitProviders(request.OutfitPatch))
            .ToImmutableArray();
        return BethesdaNpcCreationAdapter.BuildMasterList(
            baseMasters,
            request.Appearance,
            new PluginName(Path.GetFileName(request.OutputPlugin.Value)), request.PluginAuthorities);
    }

    public static ImmutableArray<RecordSignature> ExpectedMajorRecordSignatures(
        FullyAuthoredSkyrimNpcAppearanceSource appearance)
    {
        var signatures = ImmutableArray.CreateBuilder<RecordSignature>();
        signatures.Add(new RecordSignature("NPC_"));
        if (appearance.HairColor is OutputOwnedSkyrimNpcHairColor)
            signatures.Add(new RecordSignature("CLFM"));
        if (appearance.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet)
            signatures.Add(new RecordSignature("TXST"));
        signatures.AddRange(appearance.OrderedHeadParts
            .OfType<OutputOwnedSkyrimNpcFaceHeadPart>()
            .Select(_ => new RecordSignature("HDPT")));
        if (appearance.NakedSkinBinding is { } skin)
        {
            signatures.AddRange(skin.Regions.Select(_ => new RecordSignature("ARMA")));
            signatures.Add(new RecordSignature("ARMO"));
        }
        if (appearance.ExposedOutfitSkinBinding is not null)
        {
            signatures.Add(new RecordSignature("ARMA"));
            signatures.Add(new RecordSignature("ARMO"));
            signatures.Add(new RecordSignature("OTFT"));
        }
        return signatures.ToImmutable();
    }

    public static void Write(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        WorkspacePath destination)
    {
        var sourceKey = ModKey.FromNameAndExtension(proposal.SourcePluginName.Value);
        var outputKey = ModKey.FromNameAndExtension(proposal.OutputPluginName.Value);
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceKey, new FilePath(request.SourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var sourceNpc = overlay.Npcs.FirstOrDefault(item =>
            item.FormKey == new FormKey(sourceKey, request.TargetFormId.Value)) ??
            throw new InvalidDataException(
                $"NPC {request.TargetFormId} is not owned by {sourceKey.FileName}.");
        if (!string.Equals(sourceNpc.EditorID, proposal.SourceEditorId.Value,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The selected NPC EditorID changed after proposal analysis.");

        var output = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        foreach (var master in proposal.RequiredMasters)
        {
            output.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master.Value)
            });
        }

        var target = sourceNpc.DeepCopy();
        target.Race = new FormLink<IRaceGetter>(new FormKey(
            ModKey.FromNameAndExtension(request.Race.Plugin.Value),
            request.Race.FormId.Value));
        var flags = target.Configuration.Flags;
        target.Configuration.Flags = request.Sex == NpcSex.Female
            ? flags | NpcConfiguration.Flag.Female
            : flags & ~NpcConfiguration.Flag.Female;
        BethesdaNpcCreationAdapter.ApplyAuthoredAppearance(
            target,
            output,
            outputKey,
            new WorkspacePath(Path.GetDirectoryName(request.SourcePlugin.Value)!),
            proposal.SourceEditorId.Value,
            request.Sex,
            request.Appearance,
            request.PluginAuthorities);
        if (request.Appearance.NakedSkinBinding is { } skin)
            target.WornArmor.SetTo(new FormKey(outputKey, skin.AllocatedArmorLocalFormId.Value));
        BethesdaNpcCreationAdapter.ApplyRuntimeAppearance(
            target,
            request.RuntimeAppearance,
            preserveUnrelatedScripts: true);
        ApplyRecordPatch(target, request, destination);
        output.Npcs.Add(target);

        output.WriteToBinary(new FilePath(destination.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck
        });
    }

    private static IEnumerable<PluginName> OutfitProviders(NpcOutfitPatch? patch)
    {
        if (patch?.DefaultOutfit.Value is { } defaultOutfit)
            yield return defaultOutfit.Plugin;
        if (patch?.SleepingOutfit.Value is { } sleepingOutfit)
            yield return sleepingOutfit.Plugin;
    }

    private static void ApplyRecordPatch(
        Npc target,
        NpcAppearanceOverrideRequest request,
        WorkspacePath destination)
    {
        NpcStatsPatch? stats = request.IsCharGenFacePreset is { } enabled
            ? new NpcStatsPatch(
                null, null, null, null, null, null, null, null, null, null,
                null, null,
                enabled
                    ? new NpcFlagPatch([NpcFlag.IsCharGenFacePreset], [])
                    : new NpcFlagPatch([], [NpcFlag.IsCharGenFacePreset]))
            : null;
        BethesdaNpcMutationAdapter.ApplySkyrimMutation(
            target,
            new NpcMutationRequest(
                GameEdition.SkyrimSpecialEdition,
                request.SourcePlugin,
                destination,
                request.TargetFormId,
                null,
                null,
                null,
                request.ExpectedSourceSha256,
                false,
                request.ProposalPath,
                Stats: stats,
                OutfitPatch: request.OutfitPatch));
    }
}
