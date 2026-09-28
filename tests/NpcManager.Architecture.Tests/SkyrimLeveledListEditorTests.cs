using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimLeveledListEditor()
    {
        SkyrimLeveledListEditorResult created =
            SkyrimLeveledListEditorRules.Create(
                GameEdition.SkyrimSpecialEdition,
                "  Accessories  ",
                15,
                42,
                calculateAllLevels: true,
                calculateEachInCount: true,
                useAll: true,
                [new EditorId("npcm_LVLI_Taken")]);
        Assert(created.Accepted && created.Document is not null,
            "The Skyrim LVLI editor refused valid input.");
        SkyrimLeveledListEditorDocument document = created.Document ??
            throw new InvalidOperationException("The accepted LVLI document is missing.");
        Assert(document.NameSuffix == "Accessories" &&
               document.EditorId.Value == "npcm_LVLI_Accessories" &&
               document.ChanceNone == 15 && document.MaxCount == 42 &&
               document.PackedFlags == 0x07 &&
               !document.Entries.IsDefault && document.Entries.IsEmpty,
            "The Skyrim LVLI editor lost its normalized identity, header values, flags, or explicit empty entry state.");

        SkyrimLeveledListEditorResult duplicate =
            SkyrimLeveledListEditorRules.Create(
                GameEdition.SkyrimSpecialEdition,
                "taken",
                0,
                0,
                false,
                false,
                false,
                [new EditorId("npcm_LVLI_TAKEN")]);
        Assert(!duplicate.Accepted && duplicate.Document is null &&
               duplicate.Diagnostics.Any(item =>
                   item.Code == "leveled-list-editor-id-duplicate"),
            "Case-insensitive duplicate LVLI identity did not fail closed.");

        Assert(!SkyrimLeveledListEditorRules.Create(
                    GameEdition.Fallout4,
                    "WrongGame",
                    101,
                    256,
                    false,
                    false,
                    false,
                    []).Accepted &&
               !SkyrimLeveledListEditorRules.Create(
                    GameEdition.SkyrimSpecialEdition,
                    "bad/name",
                    0,
                    0,
                    false,
                    false,
                    false,
                    []).Accepted,
            "Wrong-edition, out-of-range, or unsafe LVLI input did not fail closed.");

        var provisional = new FormReference(
            new PluginName("NpcManager.esp"), new FormId(0x901));
        SkyrimLeveledListOutfitItemResult attached =
            SkyrimLeveledListEditorRules.AttachToOutfit(document, provisional);
        Assert(attached.Accepted && attached.Item is { } item &&
               item.Reference == provisional &&
               item.Kind == SkyrimOutfitEditorItemKind.LeveledList &&
               item.AuthoredLeveledList == document &&
               item.RealizationSeed is null &&
               !item.PreviewRealization.IsDefault &&
               item.PreviewRealization.IsEmpty &&
               item.EffectiveSlotMask == 0,
            "The accepted LVLI document did not enter the outfit as one initialized typed child draft.");

        SkyrimOutfitDraftEditResult started = SkyrimOutfitEditorRules.BeginNew(
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures\\Template.esp"),
            new FormId(0x900),
            new EditorId("NpcManager_Outfit"),
            new FormId(0x902));
        Assert(started.Accepted && started.Draft is not null,
            "Could not start the outer outfit transaction for the LVLI handoff proof.");
        SkyrimOutfitDraftEditResult added = SkyrimOutfitEditorRules.Add(
            started.Draft!, attached.Item!);
        Assert(added.Accepted && added.Draft is not null,
            "Could not retain the authored LVLI inside the outfit transaction.");
        SkyrimOutfitEditorCommitResult committed = SkyrimOutfitEditorRules.Save(
            GameEdition.SkyrimSpecialEdition,
            added.Draft!,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\lvli-handoff.outfit-proposal.json"));
        Assert(committed.Accepted &&
               committed.AuthoredLeveledLists.SequenceEqual([document]),
            "The accepted outfit result lost its authored LVLI header proposal input.");

        SkyrimLeveledListEditorResult cancelled =
            SkyrimLeveledListEditorRules.Cancel();
        Assert(!cancelled.Accepted && cancelled.Document is null &&
               cancelled.Diagnostics.IsEmpty,
            "LVLI Cancel returned child state or fabricated an error.");
        return Task.CompletedTask;
    }
}
