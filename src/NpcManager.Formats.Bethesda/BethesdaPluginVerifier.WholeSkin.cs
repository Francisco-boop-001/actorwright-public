using System.Buffers.Binary;
using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private static void VerifyWholeSkin(PluginVerificationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.WholeSkin is null) return;
        NpcWholeSkinPlan plan = BethesdaNpcMutationAdapter.PlanWholeSkin(new NpcMutationRequest(request.Edition,
            request.SourcePlugin, request.OutputPlugin, request.TargetFormId, null, null, null, null, true, null) { WholeSkin = request.WholeSkin });
        using var source = SkyrimMod.CreateFromBinaryOverlay(request.SourcePlugin.Value, SkyrimRelease.SkyrimSE);
        using var output = SkyrimMod.CreateFromBinaryOverlay(request.OutputPlugin.Value, SkyrimRelease.SkyrimSE);
        uint first = plan.FirstAllocatedLocalFormId.Value;
        bool preserve = plan.Patch.PreservesHead;
        FormKey Key(uint offset) => new(output.ModKey, first + offset);
        void Check(bool accepted, string message)
        {
            if (!accepted) diagnostics.Add(new Diagnostic("npc-whole-skin-readback", DiagnosticSeverity.Error, message));
        }
        var npc = output.Npcs.Single(row => row.FormKey == new FormKey(output.ModKey, request.TargetFormId.Value));
        var sourceNpc = source.Npcs.Single(row => row.FormKey == new FormKey(source.ModKey, request.TargetFormId.Value));
        uint armorOffset = preserve ? 5U : 7U;
        uint armorAddonOffset = preserve ? 2U : 4U;
        Check(npc.WornArmor.FormKey == Key(armorOffset), "wholeSkin NPC WNAM does not bind its allocated output-owned ARMO.");
        if (!preserve)
            Check(npc.HeadTexture.FormKey == Key(0), "wholeSkin NPC FTST does not bind its allocated output-owned TXST.");
        var armor = output.Armors.Single(row => row.FormKey == Key(armorOffset));
        Check(armor.Armature.Select(row => row.FormKey).SequenceEqual(new[] { Key(armorAddonOffset), Key(armorAddonOffset + 1), Key(armorAddonOffset + 2) }), "wholeSkin ARMO armature is not the exclusive allocated body/hands/feet set.");
        string[] signatures = preserve
            ? ["TXST", "TXST", "ARMA", "ARMA", "ARMA", "ARMO"]
            : ["TXST", "TXST", "TXST", "HDPT", "ARMA", "ARMA", "ARMA", "ARMO"];
        byte[] sourceBytes = File.ReadAllBytes(request.SourcePlugin.Value);
        byte[] outputBytes = File.ReadAllBytes(request.OutputPlugin.Value);
        var sourceSnapshot = BethesdaSkyrimNpcFinishCoreRaw.Read(sourceBytes, request.TargetFormId.Value);
        var outputSnapshot = BethesdaSkyrimNpcFinishCoreRaw.Read(outputBytes, request.TargetFormId.Value);
        Check(outputSnapshot.Tes4.RecordCount == checked(sourceSnapshot.Tes4.RecordCount + (uint)(plan.AllocatedRecordCount * 2)),
            $"wholeSkin raw HEDR record/group count delta is not +{plan.AllocatedRecordCount * 2}.");
        Check(outputSnapshot.Tes4.NextFormId == checked(first + (uint)plan.AllocatedRecordCount),
            "wholeSkin raw HEDR NextFormID does not equal first allocated ID plus the record count.");
        byte[] normalizedTes4 = outputSnapshot.Tes4.Record.Bytes.ToArray();
        var outputHedr = outputSnapshot.Tes4.Subrecords.Single(row => row.Signature == "HEDR");
        int outputHedrOffset = outputHedr.Offset - outputSnapshot.Tes4.Record.Offset;
        BinaryPrimitives.WriteUInt32LittleEndian(normalizedTes4.AsSpan(outputHedrOffset + 10, 4), sourceSnapshot.Tes4.RecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(normalizedTes4.AsSpan(outputHedrOffset + 14, 4), sourceSnapshot.Tes4.NextFormId);
        Check(normalizedTes4.AsSpan().SequenceEqual(sourceSnapshot.Tes4.Record.Bytes),
            "wholeSkin changed TES4 bytes outside the declared raw HEDR count and NextFormID fields.");
        var sourceRaw = BethesdaRawPluginInventory.Read(request.SourcePlugin);
        var outputRaw = BethesdaRawPluginInventory.Read(request.OutputPlugin);
        uint owner = (uint)source.ModHeader.MasterReferences.Count << 24;
        Check(source.ModHeader.MasterReferences.Select(row => row.Master).SequenceEqual(output.ModHeader.MasterReferences.Select(row => row.Master)), "wholeSkin changed the source master table.");
        Check(outputRaw.Length == sourceRaw.Length + plan.AllocatedRecordCount, "wholeSkin added or removed undeclared records.");
        for (uint i = 0; i < (uint)plan.AllocatedRecordCount; i++)
            Check(outputRaw.Count(row => row.RawFormId == (owner | first + i) && row.Signature == signatures[i]) == 1, "wholeSkin allocated record owner/signature mismatch.");
        foreach (var record in sourceRaw.Where(row => row.Signature != "TES4" && !(row.Signature == "NPC_" && row.RawFormId == (owner | request.TargetFormId.Value))))
            Check(outputRaw.Count(row => row.RawFormId == record.RawFormId && row.Signature == record.Signature && row.Sha256 == record.Sha256) == 1,
                $"wholeSkin changed retained source record {record.Signature}/{record.RawFormId:X8}.");
        string[] slots = ["diffuse", "normalOrGloss", "glowOrDetailMap", "height", "backlightMaskOrSpecular", "environmentMaskOrSubsurfaceTint", "environment", "multilayer"];
        var sets = preserve ? new[] { plan.Patch.Body, plan.Patch.Hands } : new[] { plan.Patch.Head, plan.Patch.Body, plan.Patch.Hands };
        for (int index = 0; index < sets.Length; index++)
        {
            ITextureSetGetter texture = output.TextureSets.Single(row => row.FormKey == Key((uint)index));
            string?[] paths = [texture.Diffuse?.ToString(), texture.NormalOrGloss?.ToString(), texture.GlowOrDetailMap?.ToString(), texture.Height?.ToString(),
                texture.BacklightMaskOrSpecular?.ToString(), texture.EnvironmentMaskOrSubsurfaceTint?.ToString(), texture.Environment?.ToString(), texture.Multilayer?.ToString()];
            for (int i = 0; i < slots.Length; i++)
                Check(PathValue(paths[i]) == (sets[index].TryGetValue(slots[i], out var expected) ? PathValue(expected.Path.Value) : string.Empty), "wholeSkin TXST slot mismatch: " + slots[i]);
            Check(texture.Flags == (!preserve && index == 0 ? TextureSet.Flag.FaceGenTextures | TextureSet.Flag.HasModelSpaceNormalMap : 0), "wholeSkin TXST uses unexpected material flags.");
        }
        for (int index = 0; index < plan.Skin.Regions.Length; index++)
        {
            var region = plan.Skin.Regions[index];
            var actual = output.ArmorAddons.Single(row => row.FormKey == Key(armorAddonOffset + (uint)index));
            using var provider = SkyrimMod.CreateFromBinaryOverlay(region.SourceArmorAddon.ProviderPlugin.Value, SkyrimRelease.SkyrimSE);
            var old = provider.ArmorAddons.Single(row => row.FormKey == ToKey(region.SourceArmorAddon.Reference));
            Check(actual.SkinTexture?.Female.FormKey == Key(region.Region == SkyrimNpcSkinRegion.Hands ? (preserve ? 1U : 2U) : (preserve ? 0U : 1U)), "wholeSkin private ARMA reaches the wrong TXST.");
            Check(PathValue(actual.WorldModel?.Female?.File.ToString()) == PathValue(old.WorldModel?.Female?.File.ToString()) &&
                  PathValue(actual.WorldModel?.Male?.File.ToString()) == PathValue(old.WorldModel?.Male?.File.ToString()) &&
                  Remap(old.Race.FormKey) == actual.Race.FormKey && actual.BodyTemplate?.FirstPersonFlags == old.BodyTemplate?.FirstPersonFlags &&
                  actual.SkinTexture?.Male.FormKey == Remap(old.SkinTexture?.Male.FormKey ?? FormKey.Null), "wholeSkin ARMA changed retained model, male texture, race or slot fields.");
        }
        if (preserve)
        {
            Check(npc.HeadTexture.FormKey == Remap(sourceNpc.HeadTexture.FormKey) &&
                  npc.HeadParts.Select(row => row.FormKey).SequenceEqual(sourceNpc.HeadParts.Select(row => Remap(row.FormKey))),
                "wholeSkin preserve mode changed FTST or exact PNAM identity/order.");
            var outputAllFields = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(outputBytes, outputSnapshot.Target!).ToArray();
            int[] outputWnamPositions = [.. outputAllFields.Select((row, index) => (row, index)).Where(item => item.row.Signature == "WNAM").Select(item => item.index)];
            int[] outputRnamPositions = [.. outputAllFields.Select((row, index) => (row, index)).Where(item => item.row.Signature == "RNAM").Select(item => item.index)];
            Check(outputWnamPositions.Length == 1 && outputRnamPositions.Length == 1 &&
                  outputWnamPositions[0] == outputRnamPositions[0] + 1,
                "wholeSkin preserve mode WNAM position is not immediately after RNAM.");
            var sourceFields = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(sourceBytes, sourceSnapshot.Target!).Where(row => row.Signature != "WNAM").ToArray();
            var outputFields = outputAllFields.Where(row => row.Signature != "WNAM").ToArray();
            Check(sourceFields.Length == outputFields.Length && sourceFields.Zip(outputFields).All(pair =>
                    pair.First.Signature == pair.Second.Signature && pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes)),
                "wholeSkin preserve mode changed target NPC subrecords outside WNAM, including order or duplicates.");
        }
        else
        {
            RaceMenuNpcFormBinding faceBinding = plan.FaceHeadPart ?? throw new InvalidDataException("wholeSkin replacement plan lacks its face HDPT.");
            var head = output.HeadParts.Single(row => row.FormKey == Key(3));
            var sourceFaceKey = ToKey(faceBinding.Reference);
            using var faceProvider = SkyrimMod.CreateFromBinaryOverlay(faceBinding.ProviderPlugin.Value, SkyrimRelease.SkyrimSE);
            var sourceHead = faceProvider.HeadParts.Single(row => row.FormKey == sourceFaceKey);
            var expectedHeadParts = sourceNpc.HeadParts.Select(row => row.FormKey == sourceFaceKey ? Key(3) : Remap(row.FormKey));
            Check(npc.HeadParts.Select(row => row.FormKey).SequenceEqual(expectedHeadParts) && head.TextureSet.FormKey == Key(0) &&
                  PathValue(head.Model?.File.ToString()) == PathValue(sourceHead.Model?.File.ToString()) && head.Type == sourceHead.Type && head.Flags == sourceHead.Flags,
                "wholeSkin private face HDPT or target PNAM changed outside the declared texture binding.");
        }
        FormKey Remap(FormKey key) => key.ModKey == source.ModKey ? new FormKey(output.ModKey, key.ID) : key;
    }

    private static FormKey ToKey(FormReference reference) => new(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);
    private static string PathValue(string? value) => (value ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
}
