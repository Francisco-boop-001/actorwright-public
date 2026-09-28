using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaSkyrimNpcFinishCoreVerifier
{
    private static void VerifyRetainedFormLinks(
        SkyrimMod source, SkyrimMod output, Npc sourceNpc, Npc outputNpc,
        SkyrimNpcFinishCoreProposal proposal, uint? cstyId, uint? outfitId, uint? packageId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // This comparison uses parsed identities, not the writer's relocation
        // offsets. Every retained major record must still have its full owner/key.
        var actual = output.EnumerateMajorRecords().ToLookup(row => row.FormKey);
        foreach (IMajorRecordGetter record in source.EnumerateMajorRecords())
        {
            IMajorRecordGetter[] matches = actual[record.FormKey].Cast<IMajorRecordGetter>().ToArray();
            if (!RequireExactlyOne(matches, "finish-core-verify-record-identity-count",
                    record.FormKey.ToString(), diagnostics, out var retained))
                continue;
            if (record.FormKey == sourceNpc.FormKey)
                continue;
            Require(record.EnumerateFormLinks().Select(link => link.FormKey).SequenceEqual(
                    retained!.EnumerateFormLinks().Select(link => link.FormKey)),
                "finish-core-verify-form-links",
                $"Retained record {record.FormKey} changed a full FormLink identity.", diagnostics);
        }

        SkyrimNpcFinishCoreRequest request = proposal.Request!;
        var expectedNpc = (Npc)sourceNpc.DeepCopy();
        foreach (uint id in new uint[] { 0x5C84D, 0x5C84E })
        {
            var faction = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), id);
            if (!expectedNpc.Factions.Any(row => row.Faction.FormKey == faction))
                expectedNpc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(faction) });
        }
        if (cstyId is { } style)
            expectedNpc.CombatStyle.SetTo(new FormKey(output.ModKey, style));
        if (outfitId is { } outfit)
            expectedNpc.DefaultOutfit.SetTo(new FormKey(output.ModKey, outfit));
        else if (request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit &&
                 request.OutfitPolicy.ExistingOutfit is { } selected)
            expectedNpc.DefaultOutfit.SetTo(ToFormKey(selected));
        if (packageId is { } package)
        {
            expectedNpc.Packages.Clear();
            expectedNpc.Packages.Add(new FormLink<IPackageGetter>(new FormKey(output.ModKey, package)));
        }
        // Exact requested inventory/perk identities are separately verified by
        // VerifyTypedSurface. All omitted and protected link fields stay source-bound.
        if (request.InventoryPolicy.Policy == SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory)
            expectedNpc.Items = outputNpc.Items;
        if (!request.PerkPolicy.IsDefault)
            expectedNpc.Perks = outputNpc.Perks;
        Require(expectedNpc.EnumerateFormLinks().Select(link => link.FormKey).SequenceEqual(
                outputNpc.EnumerateFormLinks().Select(link => link.FormKey)),
            "finish-core-verify-form-links",
            $"Retained NPC {sourceNpc.FormKey} changed a full FormLink identity outside the reviewed edits.", diagnostics);
    }
}
