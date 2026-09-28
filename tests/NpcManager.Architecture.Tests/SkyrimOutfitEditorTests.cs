using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimOutfitEditor()
    {
        PluginName skyrim = new("Skyrim.esm");
        PluginName armors = new("Armors.esp");
        FormReference armorA = new(armors, new FormId(0x100));
        FormReference armorB = new(armors, new FormId(0x101));
        FormReference list = new(armors, new FormId(0x200));
        FormReference rolledA = new(skyrim, new FormId(0x300));
        FormReference rolledB = new(skyrim, new FormId(0x301));

        var first = new SkyrimOutfitEditorItem(
            armorA, SkyrimOutfitEditorItemKind.Armor, "First armor", 0x04);
        var later = new SkyrimOutfitEditorItem(
            armorB, SkyrimOutfitEditorItemKind.Armor, "Later armor", 0x04);
        var leveled = new SkyrimOutfitEditorItem(
            list,
            SkyrimOutfitEditorItemKind.LeveledList,
            "Leveled accessory",
            0x08,
            11,
            [new SkyrimOutfitPreviewArmor(rolledA, 0x08)]);

        WorkspacePath source = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures\\Template.esp");
        SkyrimOutfitDraftEditResult started = SkyrimOutfitEditorRules.BeginNew(
            source,
            new FormId(0x800),
            new EditorId("NpcManager_NewOutfit"),
            new FormId(0x801));
        Assert(started.Accepted && started.Draft is { Items.Length: 0 },
            "New outfit authoring did not start from an empty immutable draft.");
        SkyrimOutfitEditorDraft original = started.Draft!;

        SkyrimOutfitEditorDraft draft = Add(Add(Add(original, first), later), leveled);
        SkyrimOutfitPreviewResolution preview =
            SkyrimOutfitEditorRules.ResolvePreview(draft);
        Assert(preview.Winners.Select(item => item.Reference)
                   .SequenceEqual([armorB, list]) &&
               preview.Losers.Select(item => item.Reference)
                   .SequenceEqual([armorA]) &&
               preview.RenderItems.Select(item => item.Reference)
                   .SequenceEqual([armorB, rolledA]),
            "Later-wins preview resolution did not preserve the all-item save sequence.");

        SkyrimOutfitDraftEditResult rerolled = SkyrimOutfitEditorRules.Reroll(
            draft,
            2,
            99,
            [new SkyrimOutfitPreviewArmor(rolledB, 0x10)]);
        Assert(rerolled.Accepted && rerolled.Draft is { } rerolledDraft &&
               rerolledDraft.Items[2].Reference == list &&
               rerolledDraft.Items[2].RealizationSeed == 99 &&
               rerolledDraft.Items[2].PreviewRealization.Single().Reference == rolledB &&
               rerolledDraft.Items[2].EffectiveSlotMask == 0x10,
            "LVLI reroll did not replace only its deterministic preview realization.");

        SkyrimOutfitDraftEditResult moved = SkyrimOutfitEditorRules.Move(draft, 1, -1);
        Assert(moved.Accepted && moved.Draft is { } movedDraft &&
               movedDraft.Items.Select(item => item.Reference)
                   .SequenceEqual([armorB, armorA, list]) &&
               SkyrimOutfitEditorRules.ResolvePreview(movedDraft).Winners
                   .Select(item => item.Reference).SequenceEqual([armorA, list]),
            "Outfit order did not control the later-wins preview deterministically.");

        WorkspacePath proposal = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\new.outfit-proposal.json");
        SkyrimOutfitEditorCommitResult saved = SkyrimOutfitEditorRules.Save(
            GameEdition.SkyrimSpecialEdition, draft, proposal);
        Assert(saved.Accepted &&
               saved.Choice?.Kind == SkyrimOutfitChoiceKind.Authored &&
               saved.Choice.Outfit is null &&
               saved.Proposal is { Mode: OutfitProposalMode.New } request &&
               request.Items.SequenceEqual([armorA, armorB, list]) &&
               request.TargetFormId == new FormId(0x801),
            "Save did not return the complete ordered typed outfit proposal.");

        SkyrimOutfitDraftEditResult duplicate = SkyrimOutfitEditorRules.Add(draft, first);
        SkyrimOutfitDraftEditResult wrongReroll = SkyrimOutfitEditorRules.Reroll(
            draft, 0, 1, []);
        Assert(!duplicate.Accepted && duplicate.Diagnostics.Any(item =>
                   item.Code == "outfit-editor-item-duplicate") &&
               !wrongReroll.Accepted && wrongReroll.Diagnostics.Any(item =>
                   item.Code == "outfit-editor-reroll-type-invalid"),
            "Outfit authoring accepted a duplicate item or rerolled an ARMO.");

        SkyrimOutfitEditorDraft reset = SkyrimOutfitEditorRules.Reset(original);
        SkyrimOutfitEditorCommitResult cancelled = SkyrimOutfitEditorRules.Cancel();
        Assert(ReferenceEquals(reset, original) && original.Items.IsEmpty &&
               !cancelled.Accepted && cancelled.Choice is null &&
               cancelled.Proposal is null,
            "Reset or Cancel leaked mutable outfit transaction state.");

        var candidate = new OutfitChoiceCandidate(
            armors,
            new FormId(0x900),
            "ExistingOutfit",
            "Existing outfit",
            [armorA.FormId, list.FormId],
            false,
            new OutfitChoiceProvenance(
                OutfitChoiceProvenanceKind.Override,
                skyrim,
                [skyrim, armors]),
            [armorA, list]);
        SkyrimOutfitEditorCommitResult existing =
            SkyrimOutfitEditorRules.UseExisting(candidate);
        SkyrimOutfitDraftEditResult overriding =
            SkyrimOutfitEditorRules.BeginOverride(candidate, source, [first, leveled]);
        Assert(existing.Accepted &&
               existing.Choice?.Outfit == new FormReference(skyrim, candidate.FormId) &&
               overriding.Accepted && overriding.Draft is { } overrideDraft &&
               overrideDraft.Mode == OutfitProposalMode.Override &&
               overrideDraft.Items.Select(item => item.Reference)
                   .SequenceEqual([armorA, list]),
            "Existing and override choices lost owner identity or authored item order.");

        var unqualified = candidate with { ItemReferences = default };
        Assert(!SkyrimOutfitEditorRules.BeginOverride(unqualified, source, [first, leveled]).Accepted,
            "Override authoring guessed providers for unqualified outfit items.");
        Assert(SkyrimOutfitEditorRules.RecordDefault().Kind ==
                   SkyrimOutfitChoiceKind.RecordDefault &&
               SkyrimOutfitEditorRules.NoOutfit().Kind ==
                   SkyrimOutfitChoiceKind.None,
            "Record-default or explicit no-outfit choices drifted.");

        return Task.CompletedTask;

        static SkyrimOutfitEditorDraft Add(
            SkyrimOutfitEditorDraft draft,
            SkyrimOutfitEditorItem item)
        {
            SkyrimOutfitDraftEditResult result =
                SkyrimOutfitEditorRules.Add(draft, item);
            Assert(result.Accepted && result.Draft is not null,
                $"Could not add typed outfit item {item.Reference}.");
            return result.Draft!;
        }
    }
}
