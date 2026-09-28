using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimBodyEditorDocument()
    {
        SkyrimBodyEditorDocument baseline = BodyBaseline();
        SkyrimBodyEditorValidationResult validation = SkyrimBodyEditorDocumentRules.Validate(baseline);
        Assert(validation.Accepted, "A complete typed body baseline was refused.");
        Assert(SkyrimBodyEditorDocumentRules.Equivalent(
                baseline,
                baseline with
                {
                    BodySlide = ImmutableArray.CreateRange(baseline.BodySlide),
                    NodeTransforms = baseline.NodeTransforms.Select(CopyTransform).ToImmutableArray(),
                    SkinOverrides = baseline.SkinOverrides.Select(CopySkin).ToImmutableArray(),
                    BodyOverlays = baseline.BodyOverlays.Select(CopyOverlay).ToImmutableArray()
                }),
            "Structural body-document equality leaked immutable collection identity.");

        SkyrimBodyEditorValidationResult malformed = SkyrimBodyEditorDocumentRules.Validate(
            baseline with
            {
                NodeTransforms = ImmutableArray.CreateRange<SkyrimNodeTransform>([null!]),
                SkinOverrides = ImmutableArray.CreateRange<SkyrimSkinOverride>([null!]),
                BodyOverlays = ImmutableArray.CreateRange<RaceMenuBodyOverlay>([null!])
            });
        Assert(!malformed.Accepted && malformed.Diagnostics.Length >= 3,
            "Malformed nullable body rows did not fail closed through typed diagnostics.");

        SkyrimBodyEditorDocument weight = SkyrimBodyEditorDocumentRules.SetWeight(baseline, 73F);
        SkyrimBodyEditorDocument weightReset = SkyrimBodyEditorDocumentRules.ResetSection(
            weight, baseline, SkyrimBodyEditorSection.Weight);
        Assert(weight.Weight == 73F && weightReset.Weight == baseline.Weight,
            "Weight update or opening-baseline reset changed semantics.");

        SkyrimBodyEditorDocument sliders = SkyrimBodyEditorDocumentRules.SetBodySlideValue(
            baseline, "waist", 0.75F);
        sliders = SkyrimBodyEditorDocumentRules.SetBodySlideValue(sliders, "New Slider", 0.4F);
        ImmutableArray<BodySlideSliderValue> filtered = SkyrimBodyEditorDocumentRules.FilterBodySlide(
            sliders, ["Waist", "Catalog only"], "only");
        Assert(sliders.BodySlide.Count(item => item.Name.Equals("Waist", StringComparison.OrdinalIgnoreCase)) == 1 &&
               sliders.BodySlide.Single(item => item.Name.Equals("Waist", StringComparison.OrdinalIgnoreCase)).Value == 0.75F &&
               filtered is [{ Name: "Catalog only", Value: 0F }],
            "BodySlide case identity, uncatalogued preservation, or filter semantics drifted.");
        sliders = SkyrimBodyEditorDocumentRules.SetBodySlideValue(sliders, "new slider", 0.0005F);
        SkyrimBodyEditorDocument sliderReset = SkyrimBodyEditorDocumentRules.ResetSection(
            sliders, baseline, SkyrimBodyEditorSection.BodySlide);
        Assert(sliders.BodySlide.All(item => item.Name != "New Slider") && sliderReset.BodySlide.IsEmpty,
            "BodySlide epsilon removal or upstream clear-all reset semantics drifted.");

        ImmutableArray<float> position = [4F, 5F, 6F];
        SkyrimBodyEditorDocument transforms = SkyrimBodyEditorDocumentRules.UpsertTransform(
            baseline, "NPC Spine [Spn0]", false, 1.2F, 2, position, []);
        SkyrimNodeTransform edited = transforms.NodeTransforms.Single(item =>
            item.Node == "NPC Spine [Spn0]" && !item.FirstPerson);
        SkyrimNodeTransform firstPerson = transforms.NodeTransforms.Single(item =>
            item.Node == "NPC Spine [Spn0]" && item.FirstPerson);
        Assert(edited.Scale == 1.2F && edited.Position.SequenceEqual(position) &&
               edited.KeySets.Any(item => item.Name == "PluginAux" &&
                                          item.Values.SequenceEqual(baseline.NodeTransforms[0].KeySets[1].Values)) &&
               firstPerson.Scale == 0.9F,
            "Transform editing lost plugin key sets or collapsed first-person identity.");
        SkyrimBodyEditorDocument removedTransform = SkyrimBodyEditorDocumentRules.RemoveTransform(
            transforms, "npc spine [spn0]", false);
        Assert(removedTransform.NodeTransforms.Length == 1 && removedTransform.NodeTransforms[0].FirstPerson,
            "Per-node reset removed the wrong first-person identity.");
        AssertThrows<ArgumentException>(() => SkyrimBodyEditorDocumentRules.UpsertTransform(
            baseline, "NPC Pelvis [Pelv]", false, null, null, [],
            [1F, 0F, 0F, 0F, 1F, 0F, 0F, 0F, -1F]));

        SkyrimBodyEditorDocument skin = SkyrimBodyEditorDocumentRules.SetSkinTexture(
            baseline, 0x20, false, 0, "textures/actors/body/new.dds");
        skin = SkyrimBodyEditorDocumentRules.SetSkinTint(skin, 0x20, false, [0.1F, 0.2F, 0.3F, 1F]);
        skin = SkyrimBodyEditorDocumentRules.SetSkinAlpha(skin, 0x20, false, 0.5F);
        SkyrimSkinOverride editedSkin = skin.SkinOverrides.Single();
        Assert(editedSkin.Textures[5] == "textures/actors/body/preserved.dds" &&
               editedSkin.Values.Any(item => item.Key == 9 && item.Index == 5 &&
                                             item.Data.StringValue == "textures/actors/body/preserved.dds") &&
               editedSkin.Values.Any(item => item.Key == 7) && editedSkin.Values.Any(item => item.Key == 8),
            "Skin editing discarded a non-editable texture slot or failed to synchronize typed values.");

        SkyrimBodyEditorDocument overlays = SkyrimBodyEditorDocumentRules.AddBodyOverlay(
            baseline, BodyOverlayTarget.Body, Paint(SkyrimRaceMenuPaintCategory.Body, "body-new.dds"), 4);
        overlays = SkyrimBodyEditorDocumentRules.AddBodyOverlay(
            overlays, BodyOverlayTarget.Hands, Paint(SkyrimRaceMenuPaintCategory.Hands, "hands.dds"), 4);
        overlays = SkyrimBodyEditorDocumentRules.AddBodyOverlay(
            overlays, BodyOverlayTarget.Feet, Paint(SkyrimRaceMenuPaintCategory.Feet, "feet.dds"), 4);
        ImmutableArray<RaceMenuBodyOverlay> draw = SkyrimBodyEditorDocumentRules.BodyOverlaysInDrawOrder(overlays);
        Assert(overlays.BodyOverlays.Any(item => item.Node == "Face [Ovl0]") &&
               overlays.BodyOverlays.Any(item => item.Node == "Body [Ovl1]") &&
               overlays.BodyOverlays.Any(item => item.Node == "Hands [Ovl0]") &&
               overlays.BodyOverlays.Any(item => item.Node == "Feet [Ovl0]") &&
               draw[0].Node == "Body [Ovl1]" &&
               overlays.BodyOverlays.Single(item => item.Node == "Body [Ovl1]").Values.Any(item =>
                   item.Key == 9 && item.Index == 0) &&
               overlays.BodyOverlays.Single(item => item.Node == "Body [Ovl1]").Values.Any(item => item.Key == 7) &&
               overlays.BodyOverlays.Single(item => item.Node == "Body [Ovl1]").Values.Any(item => item.Key == 8),
            "Body-zone paint callers lost hidden Face data, lowest-free slots, draw order, or typed values.");
        SkyrimBodyEditorDocument moved = SkyrimBodyEditorDocumentRules.MoveBodyOverlay(
            overlays, BodyOverlayTarget.Body, 1, 0);
        Assert(moved.BodyOverlays.Count(item => item.Node == "Body [Ovl0]") == 1 &&
               moved.BodyOverlays.Count(item => item.Node == "Body [Ovl1]") == 1,
            "Body-overlay reorder did not swap exact in-zone identities.");
        SkyrimBodyEditorDocument overlayReset = SkyrimBodyEditorDocumentRules.ResetSection(
            moved, baseline, SkyrimBodyEditorSection.BodyOverlays);
        Assert(SkyrimBodyEditorDocumentRules.Equivalent(overlayReset, baseline),
            "Overlay reset did not restore the complete opening collection.");

        return Task.CompletedTask;
    }

    private static SkyrimBodyEditorDocument BodyBaseline()
    {
        RaceMenuTransformKeySet primary = new("RSMTransform",
        [
            new RaceMenuValue(30, 4, 2, RaceMenuScalar.FromNumber(1.05F)),
            new RaceMenuValue(31, 4, 0, RaceMenuScalar.FromNumber(1F)),
            new RaceMenuValue(31, 4, 1, RaceMenuScalar.FromNumber(2F)),
            new RaceMenuValue(31, 4, 2, RaceMenuScalar.FromNumber(3F))
        ]);
        RaceMenuTransformKeySet auxiliary = new("PluginAux",
        [
            new RaceMenuValue(30, 4, 9, RaceMenuScalar.FromNumber(1.1F))
        ]);
        SkyrimNodeTransform third = new("NPC Spine [Spn0]", false, [primary, auxiliary],
            1.05F, null, [1F, 2F, 3F], []);
        SkyrimNodeTransform first = new("NPC Spine [Spn0]", true,
            [new RaceMenuTransformKeySet("RSMTransform",
            [
                new RaceMenuValue(30, 4, 2, RaceMenuScalar.FromNumber(0.9F))
            ])], 0.9F, null, [], []);
        var textures = ImmutableDictionary<int, string>.Empty
            .Add(0, "textures/actors/body/base.dds")
            .Add(5, "textures/actors/body/preserved.dds");
        SkyrimSkinOverride skin = new(0x20, false,
        [
            new RaceMenuValue(9, 2, 0, RaceMenuScalar.FromText(textures[0])),
            new RaceMenuValue(9, 2, 5, RaceMenuScalar.FromText(textures[5]))
        ], textures, [], null);
        return new SkyrimBodyEditorDocument(
            50F,
            [new BodySlideSliderValue("Waist", 0.25F)],
            [third, first],
            [skin],
            [BodyOverlay("Body [Ovl0]", "body.dds"), BodyOverlay("Face [Ovl0]", "face.dds")]);
    }

    private static SkyrimNodeTransform CopyTransform(SkyrimNodeTransform value) => value with
    {
        KeySets = value.KeySets.Select(item => item with
        {
            Values = ImmutableArray.CreateRange(item.Values)
        }).ToImmutableArray(),
        Position = ImmutableArray.CreateRange(value.Position),
        RotationMatrix = ImmutableArray.CreateRange(value.RotationMatrix)
    };

    private static SkyrimSkinOverride CopySkin(SkyrimSkinOverride value) => value with
    {
        Values = ImmutableArray.CreateRange(value.Values),
        Textures = ImmutableDictionary.CreateRange(value.Textures),
        Tint = ImmutableArray.CreateRange(value.Tint)
    };

    private static RaceMenuBodyOverlay CopyOverlay(RaceMenuBodyOverlay value) => value with
    {
        Tint = ImmutableArray.CreateRange(value.Tint),
        Values = ImmutableArray.CreateRange(value.Values)
    };

    private static RaceMenuBodyOverlay BodyOverlay(string node, string texture) =>
        new(node, texture, null, [], 1F, []);

    private static SkyrimRaceMenuPaintChoiceCandidate Paint(
        SkyrimRaceMenuPaintCategory category,
        string texture) =>
        new(category, texture, texture, new AssetPath(texture), new AssetPath(texture),
            [new SkyrimRaceMenuPaintTextureSlot(1, SkyrimRaceMenuPaintSlotKind.Texture,
                texture.Replace(".dds", "_n.dds", StringComparison.Ordinal),
                new AssetPath(texture.Replace(".dds", "_n.dds", StringComparison.Ordinal)))],
            [new SkyrimRaceMenuPaintRegistrationSource(
                new AssetPath("scripts/body-paint.pex"),
                SkyrimRaceMenuPaintProviderKind.Bsa,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\paint.bsa"),
                new Sha256Hash(new string('a', 64)),
                new Sha256Hash(new string('b', 64)),
                128)]);
}
