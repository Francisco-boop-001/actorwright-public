using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimFaceEditorDocument()
    {
        PluginName skyrim = new("Skyrim.esm");
        PluginName hairPlugin = new("Hair.esp");
        FormReference face = Ref(skyrim, 0x100);
        FormReference eyes = Ref(skyrim, 0x101);
        FormReference hairA = Ref(hairPlugin, 0x200);
        FormReference hairB = Ref(hairPlugin, 0x201);
        FormReference misc = Ref(hairPlugin, 0x202);
        var baseline = new SkyrimFaceEditorDocument(
            new SkyrimFaceEditorParts(
                [
                    new NpcHeadPartSelection(face, NpcHeadPartType.Face),
                    new NpcHeadPartSelection(eyes, NpcHeadPartType.Eyes),
                    new NpcHeadPartSelection(hairA, NpcHeadPartType.Hair),
                    new NpcHeadPartSelection(misc, NpcHeadPartType.Misc)
                ],
                OptionalFormReference.Set(Ref(skyrim, 0x300)),
                Ref(skyrim, 0x400),
                false),
            new SkyrimFaceMorphPatch(
                Enumerable.Range(0, 18).Select(index => index / 100F).ToImmutableArray(),
                0.125F,
                [uint.MaxValue, 0, 2, 3]),
            [
                new SkyrimRaceMenuCustomMorphValue("NoseLength", 0.25F),
                new SkyrimRaceMenuCustomMorphValue("Uncatalogued", -0.2F)
            ],
            [
                new SkyrimFaceEditorTintLayer(
                    new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 50, 0),
                    true,
                    new AssetPath("actors/character/tint/default.dds"),
                    null),
                new SkyrimFaceEditorTintLayer(
                    new SkyrimFaceTintLayer(2, 40, 50, 60, 255, 25, 1),
                    false,
                    new AssetPath("actors/character/tint/second.dds"),
                    null)
            ],
            [new RaceMenuSculptPart("Head", 10, [], true, true)],
            [
                Overlay("Body [Ovl0]", "body.dds"),
                Overlay("Face [Ovl0]", "face0.dds"),
                Overlay("Face [Ovl2]", "face2.dds")
            ]);

        SkyrimFaceEditorValidationResult validation =
            SkyrimFaceEditorDocumentRules.Validate(baseline);
        Assert(validation.Accepted, "A complete typed face baseline was refused.");
        Assert(SkyrimFaceEditorDocumentRules.Equivalent(
                baseline,
                baseline with
                {
                    NativeMorphs = baseline.NativeMorphs with
                    {
                        Nam9Sliders = ImmutableArray.CreateRange(
                            baseline.NativeMorphs.Nam9Sliders.AsEnumerable()),
                        NamaValues = ImmutableArray.CreateRange(
                            baseline.NativeMorphs.NamaValues.AsEnumerable())
                    },
                    BodyOverlays = ImmutableArray.CreateRange(
                        baseline.BodyOverlays.AsEnumerable())
                }),
            "Structural face-document equality leaked immutable-array identity.");

        SkyrimFaceEditorValidationResult malformedRows =
            SkyrimFaceEditorDocumentRules.Validate(baseline with
            {
                Parts = baseline.Parts with
                {
                    OrderedHeadParts = ImmutableArray.CreateRange<NpcHeadPartSelection>(
                        new NpcHeadPartSelection[] { null! })
                },
                Tints = ImmutableArray.CreateRange<SkyrimFaceEditorTintLayer>(
                    new SkyrimFaceEditorTintLayer[] { null! }),
                BodyOverlays = ImmutableArray.CreateRange<RaceMenuBodyOverlay>(
                    new RaceMenuBodyOverlay[] { null! })
            });
        Assert(!malformedRows.Accepted && malformedRows.Diagnostics.Length >= 3,
            "Malformed nullable face rows did not fail closed through typed diagnostics.");

        SkyrimFaceEditorDocument replaced = SkyrimFaceEditorDocumentRules.ReplaceHeadPart(
            baseline,
            new NpcHeadPartSelection(hairB, NpcHeadPartType.Hair));
        Assert(replaced.Parts.OrderedHeadParts[2].Reference == hairB &&
               replaced.Parts.OrderedHeadParts.Count(item =>
                   item.Type == NpcHeadPartType.Hair) == 1 &&
               replaced.Parts.OrderedHeadParts.Any(item => item.Reference == misc),
            "Non-Misc replacement changed ordering, duplicated the type, or guessed at orphan removal.");
        AssertThrows<ArgumentException>(() =>
            SkyrimFaceEditorDocumentRules.AddMiscHeadPart(baseline, misc));

        SkyrimFaceEditorDocument native = SkyrimFaceEditorDocumentRules.SetNativeFamily(
            baseline, 0, 24);
        Assert(baseline.NativeMorphs.NamaValues[0] == uint.MaxValue &&
               native.NativeMorphs.NamaValues[0] == 24 &&
               native.NativeMorphs.Nam9Trailing == baseline.NativeMorphs.Nam9Trailing,
            "A custom-race NAMA preset index was clipped or NAM9 trailing authority drifted.");

        SkyrimFaceEditorDocument custom = SkyrimFaceEditorDocumentRules.SetCustomMorph(
            baseline, "noselength", 0.00001F);
        Assert(custom.CustomMorphs.All(item =>
                !string.Equals(item.Name, "NoseLength", StringComparison.OrdinalIgnoreCase)) &&
               custom.CustomMorphs.Single().Name == "Uncatalogued",
            "Near-zero custom morph removal or ordered-row preservation drifted.");

        SkyrimFaceEditorDocument customMask = SkyrimFaceEditorDocumentRules.SetTintMask(
            baseline, 1, new AssetPath("textures/actors/character/tint/custom.dds"));
        Assert(customMask.Tints[0].MaskOverride?.Value.EndsWith(
                   "custom.dds", StringComparison.OrdinalIgnoreCase) == true,
            "A custom tint mask was not retained.");
        SkyrimFaceEditorDocument defaultMask = SkyrimFaceEditorDocumentRules.SetTintMask(
            customMask, 1, new AssetPath("textures/actors/character/tint/default.dds"));
        Assert(defaultMask.Tints[0].MaskOverride is null,
            "Selecting the race-default mask did not clear the redundant override.");

        SkyrimRaceMenuPaintChoiceCandidate paint = FacePaint();
        SkyrimFaceEditorDocument added = SkyrimFaceEditorDocumentRules.AddFaceOverlay(
            baseline, paint, 4);
        ImmutableArray<RaceMenuBodyOverlay> drawOrder =
            SkyrimFaceEditorDocumentRules.FaceOverlaysInDrawOrder(added);
        Assert(drawOrder.Select(item => item.Node).SequenceEqual(
                   ["Face [Ovl2]", "Face [Ovl1]", "Face [Ovl0]"]) &&
               added.BodyOverlays.Any(item => item.Node == "Body [Ovl0]") &&
               drawOrder[1].Normal == "actors/paint/new_n.dds",
            "Face paint did not use the lowest free slot, preserve non-face rows, or retain the normal slot.");
        SkyrimFaceEditorDocument moved = SkyrimFaceEditorDocumentRules.MoveFaceOverlay(
            added, 1, 2);
        Assert(moved.BodyOverlays.Count(item => item.Node == "Face [Ovl1]") == 1 &&
               moved.BodyOverlays.Count(item => item.Node == "Face [Ovl2]") == 1,
            "Face-overlay reorder did not swap exact slot identities.");

        SkyrimFaceEditorDocument reset = SkyrimFaceEditorDocumentRules.ResetSection(
            replaced with { CustomMorphs = custom.CustomMorphs },
            baseline,
            SkyrimFaceEditorSection.CustomMorphs);
        Assert(reset.CustomMorphs == baseline.CustomMorphs &&
               reset.Parts == replaced.Parts,
            "Section reset did not restore only the selected immutable section.");

        Console.WriteLine(
            "PASS Skyrim face document preserves typed section, sentinel, overlay, reset, and cancellation foundations.");
        return Task.CompletedTask;

        FormReference Ref(PluginName plugin, uint id) => new(plugin, new FormId(id));

        RaceMenuBodyOverlay Overlay(string node, string diffuse) =>
            new(node, diffuse, null, [], 1F, []);

        SkyrimRaceMenuPaintChoiceCandidate FacePaint()
        {
            var source = new SkyrimRaceMenuPaintRegistrationSource(
                new AssetPath("scripts/paint.pex"),
                SkyrimRaceMenuPaintProviderKind.Bsa,
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\RaceMenu.bsa"),
                new Sha256Hash(new string('a', 64)),
                new Sha256Hash(new string('b', 64)),
                100);
            return new SkyrimRaceMenuPaintChoiceCandidate(
                SkyrimRaceMenuPaintCategory.Face,
                "New face paint",
                "New face paint",
                new AssetPath("actors/paint/new.dds"),
                new AssetPath("textures/actors/paint/new.dds"),
                [
                    new SkyrimRaceMenuPaintTextureSlot(0,
                        SkyrimRaceMenuPaintSlotKind.Texture,
                        "actors/paint/new.dds",
                        new AssetPath("textures/actors/paint/new.dds")),
                    new SkyrimRaceMenuPaintTextureSlot(1,
                        SkyrimRaceMenuPaintSlotKind.Texture,
                        "actors/paint/new_n.dds",
                        new AssetPath("textures/actors/paint/new_n.dds"))
                ],
                [source]);
        }
    }
}
