using System.Collections.Immutable;
using System.Drawing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaSkyrimFollowerFinishPairService
{
    private void ValidateCompanionFinishAuthority(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairSourceSnapshot companion)
    {
        if (request.CompanionFinish is not { } finish)
            return;
        if (companion.NextFormId !=
                finish.Allocation.PrivateArmorAddon ||
            companion.DefaultOutfit is not null ||
            companion.HairColor != new FormReference(
                request.Companion.Plugin,
                finish.Hair.ColorFormId) ||
            companion.RecordInventory.Any(row =>
                ParseInventoryId(row) >=
                finish.Allocation.PrivateArmorAddon.Value))
            Refuse(
                "follower-finish-pair-companion-source-finish",
                "The accepted companion collides with or differs from the reviewed hair/outfit baseline.");

        ModKey key =
            ModKey.FromNameAndExtension(
                request.Companion.Plugin.Value);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                key,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IColorRecordGetter color = mod.Colors.Single(record =>
            record.FormKey ==
            new FormKey(key, finish.Hair.ColorFormId.Value));
        if (PackRgb(color.Color) !=
            finish.Hair.OldPackedRgb)
            Refuse(
                "follower-finish-pair-companion-source-color",
                "The companion CLFM does not equal the bound source RGB.");

        SkyrimFollowerFinishPairFaceGeomRewrite rewrite =
            faceGeomService.Rewrite(
                File.ReadAllBytes(
                        request.Companion.FaceGeom.Path.Value)
                    .ToImmutableArray(),
                finish.Hair);
        if (rewrite.Bytes.Length !=
                request.Companion.FaceGeom.ByteLength ||
            rewrite.ChangedByteOffsets.IsDefaultOrEmpty)
            Refuse(
                "follower-finish-pair-companion-facegeom-source",
                "The companion FaceGeom did not admit the reviewed HairTint rewrite.");
    }

    private static void WriteCompanionPlugin(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        WorkspacePath output)
    {
        SkyrimFollowerFinishPairCompanionFinish finish =
            request.CompanionFinish ??
            throw new InvalidOperationException(
                "Companion finish data is required.");
        ImmutableArray<PluginName> outputMasters =
            proposal.CompanionOutputMasters ??
            throw new InvalidOperationException(
                "Companion output masters are required.");
        ModKey companionKey =
            ModKey.FromNameAndExtension(
                request.Companion.Plugin.Value);
        ModKey outfitKey =
            ModKey.FromNameAndExtension(
                request.Outfit.Plugin.Value);
        using var source = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                companionKey,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        var mod = (SkyrimMod)source.DeepCopy();
        mod.IsSmallMaster = true;
        mod.IsMaster = false;
        mod.ModHeader.Stats.NextFormID =
            finish.Allocation.NextFormId.Value;
        foreach (PluginName master in outputMasters.Skip(
                     proposal.CompanionSnapshot.Masters.Length))
        {
            mod.ModHeader.MasterReferences.Add(
                new MasterReference
                {
                    Master =
                        ModKey.FromNameAndExtension(master.Value)
                });
        }

        ColorRecord color = mod.Colors.Single(record =>
            record.FormKey ==
            new FormKey(
                companionKey,
                finish.Hair.ColorFormId.Value));
        uint packedRgb = finish.Hair.NewPackedRgb;
        color.Color = Color.FromArgb(
            255,
            checked((int)((packedRgb >> 16) & 0xFF)),
            checked((int)((packedRgb >> 8) & 0xFF)),
            checked((int)(packedRgb & 0xFF)));

        using var outfit = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                outfitKey,
                new FilePath(
                    request.Outfit.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IArmorAddonGetter sourceAddon =
            outfit.ArmorAddons.Single(record =>
                record.FormKey ==
                ToFormKey(request.Outfit.TorsoArmorAddon));
        FormKey privateAddonKey = new(
            companionKey,
            finish.Allocation.PrivateArmorAddon.Value);
        ArmorAddon privateAddon =
            mod.ArmorAddons.DuplicateInAsNewRecord(
                sourceAddon,
                privateAddonKey);
        privateAddon.FormVersion = RecordFormVersion;
        privateAddon.EditorID =
            BuildEditorId(
                request.Companion.Plugin,
                "PairPrivateTorsoAA");
        privateAddon.SkinTexture =
            new GenderedItem<
                IFormLinkNullableGetter<ITextureSetGetter>>(
                privateAddon.SkinTexture?.Male ??
                new FormLinkNullable<ITextureSetGetter>(),
                new FormLinkNullable<ITextureSetGetter>(
                    ToFormKey(
                        request.Outfit
                            .TargetFemaleSkinTextureSet)));

        IArmorGetter sourceArmor = outfit.Armors.Single(record =>
            record.FormKey ==
            ToFormKey(request.Outfit.TorsoArmor));
        FormKey privateArmorKey = new(
            companionKey,
            finish.Allocation.PrivateArmor.Value);
        Armor privateArmor = mod.Armors.DuplicateInAsNewRecord(
            sourceArmor,
            privateArmorKey);
        privateArmor.FormVersion = RecordFormVersion;
        privateArmor.EditorID =
            BuildEditorId(
                request.Companion.Plugin,
                "PairPrivateTorso");
        FormKey sourceAddonKey =
            ToFormKey(request.Outfit.TorsoArmorAddon);
        FormKey[] armatures = sourceArmor.Armature
            .Select(link => link.FormKey == sourceAddonKey
                ? privateAddonKey
                : link.FormKey)
            .ToArray();
        if (armatures.Count(key => key == privateAddonKey) != 1)
            Refuse(
                "follower-finish-pair-companion-private-armor",
                "The companion private armor could not replace exactly one admitted torso ARMA.");
        privateArmor.Armature.Clear();
        foreach (FormKey armature in armatures)
        {
            privateArmor.Armature.Add(
                new FormLink<IArmorAddonGetter>(armature));
        }

        FormKey outfitKeyOwned = new(
            companionKey,
            finish.Allocation.Outfit.Value);
        mod.Outfits.Add(
            new Outfit(
                outfitKeyOwned,
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = RecordFormVersion,
                EditorID =
                    BuildEditorId(
                        request.Companion.Plugin,
                        "PairOutfit"),
                Items =
                [
                    new FormLink<IOutfitTargetGetter>(
                        privateArmorKey),
                    new FormLink<IOutfitTargetGetter>(
                        ToFormKey(request.Outfit.Boots)),
                    new FormLink<IOutfitTargetGetter>(
                        ToFormKey(request.Outfit.Gauntlets))
                ]
            });

        Npc npc = mod.Npcs.Single(record =>
            record.FormKey ==
            new FormKey(
                companionKey,
                request.Companion.ActorFormId.Value));
        npc.DefaultOutfit =
            new FormLinkNullable<IOutfitGetter>(
                outfitKeyOwned);

        mod.WriteToBinary(
            new FilePath(output.Value),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                MastersListOrdering =
                    MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
        SkyrimStructuralWorldspaceRecordSanitizer
            .RemoveRequiredPartialMasterRecord(
                output.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        SkyrimStructuralWorldspaceRecordSanitizer
            .MinimizeRequiredExteriorCellRecord(
                output.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value,
                request.Placement.Cell.FormId.Value,
                request.Placement.CellGridX,
                request.Placement.CellGridY);
    }

    private static void VerifyCompanionPlugin(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        WorkspacePath outputPath)
    {
        SkyrimFollowerFinishPairCompanionFinish finish =
            request.CompanionFinish ??
            throw new InvalidOperationException(
                "Companion finish data is required.");
        if (!File.Exists(outputPath.Value))
            Refuse(
                "follower-finish-pair-companion-output-missing",
                "The staged companion plugin is missing.");
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireNoMasterRecord(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireNoForbiddenWorldRecords(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        ModKey key =
            ModKey.FromNameAndExtension(
                request.Companion.Plugin.Value);
        using var source = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                key,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        using var output = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                key,
                new FilePath(outputPath.Value)),
            SkyrimRelease.SkyrimSE);
        if (!output.IsSmallMaster ||
            output.IsMaster ||
            output.ModHeader.Stats.NextFormID !=
                finish.Allocation.NextFormId.Value ||
            proposal.CompanionOutputMasters is not
                { } outputMasters ||
            !output.ModHeader.MasterReferences
                .Select(master =>
                    new PluginName(master.Master.ToString()))
                .SequenceEqual(outputMasters))
            Refuse(
                "follower-finish-pair-companion-output-header",
                "The companion TES4 surface differs from the reviewed finish.");

        INpcGetter sourceNpc = source.Npcs.Single(record =>
            record.FormKey ==
            new FormKey(
                key,
                request.Companion.ActorFormId.Value));
        INpcGetter outputNpc = output.Npcs.Single(record =>
            record.FormKey == sourceNpc.FormKey);
        if (!AppearanceMatches(sourceNpc, outputNpc) ||
            !sourceNpc.Packages.Select(link => link.FormKey)
                .SequenceEqual(
                    outputNpc.Packages.Select(link =>
                        link.FormKey)) ||
            outputNpc.DefaultOutfit.FormKeyNullable !=
                new FormKey(
                    key,
                    finish.Allocation.Outfit.Value))
            Refuse(
                "follower-finish-pair-companion-output-appearance",
                "The companion changed outside the reviewed CLFM/DOFT surface.");

        IColorRecordGetter sourceColor =
            source.Colors.Single(record =>
                record.FormKey.ID ==
                finish.Hair.ColorFormId.Value);
        IColorRecordGetter outputColor =
            output.Colors.Single(record =>
                record.FormKey.ID ==
                finish.Hair.ColorFormId.Value);
        if (PackRgb(sourceColor.Color) !=
                finish.Hair.OldPackedRgb ||
            PackRgb(outputColor.Color) !=
                finish.Hair.NewPackedRgb ||
            sourceColor.EditorID != outputColor.EditorID ||
            sourceColor.Playable != outputColor.Playable)
            Refuse(
                "follower-finish-pair-companion-output-color",
                "The companion CLFM does not equal the reviewed transition.");

        VerifyCompanionOutfitGraph(request, output, key, finish);
        string[] expectedNew =
        [
            $"ARMA 0x{finish.Allocation.PrivateArmorAddon.Value:X8}",
            $"ARMO 0x{finish.Allocation.PrivateArmor.Value:X8}",
            $"OTFT 0x{finish.Allocation.Outfit.Value:X8}"
        ];
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireSelfOwnedInventory(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value,
                proposal.CompanionSnapshot.RecordInventory
                    .Concat(expectedNew));
    }

    private static void VerifyCompanionOutfitGraph(
        SkyrimFollowerFinishPairRequest request,
        ISkyrimModGetter output,
        ModKey key,
        SkyrimFollowerFinishPairCompanionFinish finish)
    {
        IArmorAddonGetter addon =
            output.ArmorAddons.Single(record =>
                record.FormKey.ID ==
                finish.Allocation.PrivateArmorAddon.Value);
        IArmorGetter armor = output.Armors.Single(record =>
            record.FormKey.ID ==
            finish.Allocation.PrivateArmor.Value);
        IOutfitGetter outfit = output.Outfits.Single(record =>
            record.FormKey.ID ==
            finish.Allocation.Outfit.Value);
        FormKey privateAddon = new(
            key,
            finish.Allocation.PrivateArmorAddon.Value);
        FormKey privateArmor = new(
            key,
            finish.Allocation.PrivateArmor.Value);
        FormKey[] expectedItems =
        [
            privateArmor,
            ToFormKey(request.Outfit.Boots),
            ToFormKey(request.Outfit.Gauntlets)
        ];
        if (addon.SkinTexture?.Female?.FormKeyNullable !=
                ToFormKey(
                    request.Outfit.TargetFemaleSkinTextureSet) ||
            armor.Armature.Count(link =>
                link.FormKey == privateAddon) != 1 ||
            outfit.Items is null ||
            !outfit.Items.Select(link => link.FormKey)
                .SequenceEqual(expectedItems))
            Refuse(
                "follower-finish-pair-companion-output-outfit",
                "The companion private COtR outfit graph differs from the reviewed route.");
    }

    private void WriteCompanionFaceGeom(
        SkyrimFollowerFinishPairRequest request,
        WorkspacePath output)
    {
        SkyrimFollowerFinishPairHairFinish finish =
            request.CompanionFinish?.Hair ??
            throw new InvalidOperationException(
                "Companion hair finish data is required.");
        SkyrimFollowerFinishPairFaceGeomRewrite rewrite =
            faceGeomService.Rewrite(
                File.ReadAllBytes(
                        request.Companion.FaceGeom.Path.Value)
                    .ToImmutableArray(),
                finish);
        Directory.CreateDirectory(
            Path.GetDirectoryName(output.Value)!);
        using var stream = new FileStream(
            output.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(rewrite.Bytes.AsSpan());
        stream.Flush(flushToDisk: true);
    }

    private void VerifyCompanionFaceGeom(
        SkyrimFollowerFinishPairRequest request,
        WorkspacePath output)
    {
        SkyrimFollowerFinishPairHairFinish finish =
            request.CompanionFinish?.Hair ??
            throw new InvalidOperationException(
                "Companion hair finish data is required.");
        byte[] source =
            File.ReadAllBytes(
                request.Companion.FaceGeom.Path.Value);
        byte[] actual = File.ReadAllBytes(output.Value);
        ImmutableArray<int> changed = Enumerable.Range(
                0,
                source.Length)
            .Where(index => source[index] != actual[index])
            .ToImmutableArray();
        faceGeomService.Verify(
            source.ToImmutableArray(),
            actual.ToImmutableArray(),
            finish,
            changed);
        if (source.Length != actual.Length ||
            changed.IsDefaultOrEmpty ||
            changed.Length >
                finish.FaceGeomShapeNames.Length * 12)
            Refuse(
                "follower-finish-pair-companion-facegeom-delta",
                "The companion FaceGeom delta escaped the exact RGB float-triplet envelope.");
    }

    private static WorkspacePath CompanionOutputPlugin(
        SkyrimFollowerFinishPairRequest request,
        string root) =>
        new(Path.Combine(
            root,
            "Data",
            request.Companion.Plugin.Value));

    private static WorkspacePath CompanionOutputFaceGeom(
        SkyrimFollowerFinishPairRequest request,
        string root) =>
        new(Path.Combine(
            root,
            "Data",
            "meshes",
            "actors",
            "character",
            "FaceGenData",
            "FaceGeom",
            request.Companion.Plugin.Value,
            $"{request.Companion.ActorFormId.Value:X8}.nif"));

    private static uint PackRgb(Color color) =>
        ((uint)color.R << 16) |
        ((uint)color.G << 8) |
        color.B;
}
