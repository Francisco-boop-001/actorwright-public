using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimMainWorkspaceRules()
    {
        PluginName owner = new("Actors.esp");
        PluginName winner = new("ActorsPatch.esp");
        var firstId = new SkyrimMainWorkspaceIdentity(
            owner, winner, new FormId(0x800), "NPC_");
        var secondId = new SkyrimMainWorkspaceIdentity(
            owner, winner, new FormId(0x801), "NPC_");
        var listId = new SkyrimMainWorkspaceIdentity(
            owner, winner, new FormId(0x900), "LVLN");

        ImmutableArray<SkyrimMainWorkspaceRecord> rows =
        [
            SkyrimMainWorkspaceTestRecord.Npc(
                firstId, "Gate2Female", "Gate 2 Female", NpcSex.Female,
                [NpcCategory.Unique], NpcChangeState.Changed),
            SkyrimMainWorkspaceTestRecord.Npc(
                secondId, "Gate2Male", "Gate 2 Male", NpcSex.Male,
                [NpcCategory.Generic], NpcChangeState.Unchanged),
            SkyrimMainWorkspaceTestRecord.LeveledNpc(
                listId, "Gate2Actors", [firstId, secondId], isEmpty: false)
        ];

        var filtered = SkyrimMainWorkspaceRules.Filter(
            rows,
            SkyrimMainWorkspaceFilter.Default with
            {
                Search = "Gate 2",
                Gender = SkyrimMainWorkspaceGender.Female,
                ChangedOnly = true
            });
        Assert(filtered.Select(item => item.Identity).SequenceEqual([firstId]),
            "Female/changed/search filtering selected the wrong record.");

        var range = SkyrimMainWorkspaceRules.SelectRange(rows, firstId, listId);
        Assert(range.SequenceEqual([firstId, secondId, listId]),
            "Visible range selection lost deterministic row order.");

        SkyrimMainWorkspaceDraft draft =
            SkyrimMainWorkspaceRules.MarkChanged(firstId);
        draft = SkyrimMainWorkspaceRules.MarkDelete(draft);
        draft = SkyrimMainWorkspaceRules.Restore(draft);
        Assert(draft.IsChanged && !draft.IsDeletePending,
            "Restore must preserve an existing change.");
        Assert(SkyrimMainWorkspaceRules.Reset(draft) ==
               SkyrimMainWorkspaceDraft.Clean(firstId),
            "Reset must restore the complete clean baseline.");

        SkyrimMainWorkspacePreviewProjection face =
            SkyrimMainWorkspaceRules.ProjectPreview(
                SkyrimMainWorkspacePreviewOptions.Default with
                {
                    Mode = SkyrimMainWorkspacePreviewMode.FaceOnly,
                    RenderArmor = true,
                    ApplySculpt = false
                });
        Assert(face.VisibleAssets.SetEquals(
            [
                PreviewAssetCategory.Face,
                PreviewAssetCategory.Hair,
                PreviewAssetCategory.Headwear,
                PreviewAssetCategory.Accessory
            ]), "Face-only projection leaked full-body categories.");
        Assert(!face.Morphs.Contains(PreviewMorphCategory.Sculpt),
            "Disabled sculpt leaked into the preview.");

        Assert(SkyrimMainWorkspaceRules.AvailableRoutes(rows[0])
                .Contains(SkyrimMainWorkspaceRoute.EditFace),
            "Live NPC did not expose face editing.");
        Assert(!SkyrimMainWorkspaceRules.AvailableRoutes(rows[2])
                .Contains(SkyrimMainWorkspaceRoute.EditFace),
            "LVLN incorrectly exposed an NPC-only editor.");
        Assert(SkyrimMainWorkspaceRules.PlaceAtMe(firstId) ==
               "player.placeatme XX000800 1",
            "The explicit unresolved runtime-prefix command changed.");
        return Task.CompletedTask;
    }

    private static class SkyrimMainWorkspaceTestRecord
    {
        internal static SkyrimMainWorkspaceRecord Npc(
            SkyrimMainWorkspaceIdentity identity,
            string editorId,
            string name,
            NpcSex sex,
            ImmutableArray<NpcCategory> categories,
            NpcChangeState changeState) =>
            new(identity, SkyrimMainWorkspaceRecordKind.Npc, editorId, name,
                false, false, sex, categories, changeState,
                [identity.WinningProvider], [], "A".PadLeft(64, 'A'));

        internal static SkyrimMainWorkspaceRecord LeveledNpc(
            SkyrimMainWorkspaceIdentity identity,
            string editorId,
            ImmutableArray<SkyrimMainWorkspaceIdentity> entries,
            bool isEmpty) =>
            new(identity, SkyrimMainWorkspaceRecordKind.LeveledNpc, editorId,
                editorId, false, isEmpty, null, [], NpcChangeState.Unchanged,
                [identity.WinningProvider], entries, "B".PadLeft(64, 'B'));
    }
}
