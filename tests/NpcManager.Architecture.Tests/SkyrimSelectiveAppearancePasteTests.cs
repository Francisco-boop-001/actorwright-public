using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimSelectiveAppearancePaste()
    {
        SkyrimSelectiveAppearancePasteDocument source = PasteDocument("source", 72F, true, 0x900);
        SkyrimSelectiveAppearancePasteDocument target = PasteDocument("target", 31F, false, 0xA00);
        SkyrimSelectiveAppearancePasteDocument sourceSnapshot = Clone(source);

        var selection = new SkyrimSelectiveAppearancePasteSelection(
        [
            SkyrimAppearancePasteCategory.BodyWeight,
            SkyrimAppearancePasteCategory.FaceParts,
            SkyrimAppearancePasteCategory.Sculpt,
            SkyrimAppearancePasteCategory.Outfits
        ]);
        SkyrimSelectiveAppearancePasteResult selective =
            SkyrimSelectiveAppearancePasteRules.Merge(source, target, selection);
        Assert(selective is { Accepted: true, HasChanges: true, Document: not null },
            "A valid selective Skyrim appearance merge was refused.");
        SkyrimSelectiveAppearancePasteDocument merged = selective.Document!;
        Assert(merged.Body.Weight == source.Body.Weight &&
               merged.Body.BodySlide.SequenceEqual(target.Body.BodySlide) &&
               merged.Body.NodeTransforms.SequenceEqual(target.Body.NodeTransforms) &&
               merged.Body.SkinOverrides.SequenceEqual(target.Body.SkinOverrides) &&
               merged.Face.Parts.OrderedHeadParts.SequenceEqual(
                   source.Face.Parts.OrderedHeadParts) &&
               merged.Face.Parts.HeadTexture == source.Face.Parts.HeadTexture &&
               merged.Face.Parts.HairColor == target.Face.Parts.HairColor &&
               merged.Face.Parts.IsCharGenFacePreset ==
                   target.Face.Parts.IsCharGenFacePreset &&
               merged.Face.Tints.SequenceEqual(target.Face.Tints) &&
               merged.Face.SculptParts.SequenceEqual(source.Face.SculptParts) &&
               merged.Outfits == source.Outfits,
            "Selected categories were coupled or an unchecked target category drifted.");
        Assert(SkyrimSelectiveAppearancePasteRules.Equivalent(source, sourceSnapshot),
            "Selective paste mutated or aliased the immutable source document.");

        SkyrimSelectiveAppearancePasteResult all = SkyrimSelectiveAppearancePasteRules.Merge(
            source, target, SkyrimSelectiveAppearancePasteSelection.All);
        Assert(all is { Accepted: true, HasChanges: true, Document: not null } &&
               SkyrimSelectiveAppearancePasteRules.Equivalent(source, all.Document),
            "Select All did not cover every Skyrim appearance carrier.");

        SkyrimSelectiveAppearancePasteResult none = SkyrimSelectiveAppearancePasteRules.Merge(
            source, target, SkyrimSelectiveAppearancePasteSelection.None);
        Assert(none is { Accepted: true, HasChanges: false, Document: not null } &&
               SkyrimSelectiveAppearancePasteRules.Equivalent(target, none.Document),
            "Deselect All was not an exact non-dirty target no-op.");

        SkyrimSelectiveAppearancePasteResult duplicate = SkyrimSelectiveAppearancePasteRules.Merge(
            source,
            target,
            new SkyrimSelectiveAppearancePasteSelection(
            [
                SkyrimAppearancePasteCategory.FaceTints,
                SkyrimAppearancePasteCategory.FaceTints
            ]));
        Assert(!duplicate.Accepted && duplicate.Document is null &&
               duplicate.Diagnostics.Any(item =>
                   item.Code == "selective-paste-category-duplicate"),
            "A duplicate category selection did not fail closed without partial state.");

        SkyrimSelectiveAppearancePasteDocument inconsistent = target with
        {
            Face = target.Face with
            {
                BodyOverlays =
                [new RaceMenuBodyOverlay("Face [Ovl7]", "wrong.dds", null, [], 1F, [])]
            }
        };
        SkyrimSelectiveAppearancePasteResult mismatch = SkyrimSelectiveAppearancePasteRules.Merge(
            source, inconsistent, SkyrimSelectiveAppearancePasteSelection.All);
        Assert(!mismatch.Accepted && mismatch.Document is null &&
               mismatch.Diagnostics.Any(item =>
                   item.Code == "selective-paste-overlay-carrier-mismatch"),
            "Mismatched shared overlay carriers did not fail closed.");

        Assert(SkyrimSelectiveAppearancePasteSelection.All.Categories.Length == 10 &&
               SkyrimSelectiveAppearancePasteSelection.All.Categories.Distinct().Count() == 10,
            "The default Skyrim selective-paste category set is incomplete or duplicated.");

        TestSelectivePasteProjection(source, target, selection, merged);
        return Task.CompletedTask;
    }

    private static void TestSelectivePasteProjection(
        SkyrimSelectiveAppearancePasteDocument source,
        SkyrimSelectiveAppearancePasteDocument target,
        SkyrimSelectiveAppearancePasteSelection selection,
        SkyrimSelectiveAppearancePasteDocument expected)
    {
        SkyrimSelectiveAppearancePasteEndpoint SourceEndpoint() => Endpoint(
            "source.esp", 0x900, source, 'A');
        SkyrimSelectiveAppearancePasteEndpoint TargetEndpoint() => Endpoint(
            "target.esp", 0xA00, target, 'B');
        var state = new SkyrimSelectiveAppearancePasteLoadedState(
            SourceEndpoint(),
            TargetEndpoint());
        SkyrimSelectiveAppearancePasteProjectionResult projected =
            SkyrimSelectiveAppearancePasteProjector.Project(
                new SkyrimSelectiveAppearancePasteProjectionRequest(
                    state,
                    selection,
                    new WorkspacePath(
                        "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\selective-paste-test.proposal.json"),
                    new WorkspacePath(
                        "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\SelectivePasteTest.esp")));

        Assert(projected.Accepted &&
               projected.AcceptedDocument is not null &&
               SkyrimSelectiveAppearancePasteRules.Equivalent(
                   expected,
                   projected.AcceptedDocument) &&
               projected.PluginRequest is { } plugin &&
               plugin.Appearance.Weight == source.Body.Weight &&
               plugin.Appearance.Qnam == TargetEndpoint().Appearance.Qnam &&
               plugin.OutfitPatch?.DefaultOutfit.Value ==
                   source.Outfits.DefaultOutfit &&
               plugin.OutfitPatch?.SleepingOutfit.Value ==
                   source.Outfits.SleepingOutfit &&
               plugin.IsCharGenFacePreset is null &&
               projected.PresetSections.SequenceEqual(
               [
                   PresetCopySection.BodyWeight,
                   PresetCopySection.FaceParts,
                   PresetCopySection.Sculpt
               ]),
            "The production projector did not preserve target-only state or map selected carriers exactly.");

        SkyrimSelectiveAppearancePasteProjectionResult noOp =
            SkyrimSelectiveAppearancePasteProjector.Project(
                new SkyrimSelectiveAppearancePasteProjectionRequest(
                    state,
                    SkyrimSelectiveAppearancePasteSelection.None,
                    new WorkspacePath(
                        "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\selective-paste-none.proposal.json"),
                    new WorkspacePath(
                        "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\SelectivePasteNone.esp")));
        Assert(!noOp.Accepted && noOp.PluginRequest is null &&
               noOp.Diagnostics.Any(item => item.Code ==
                   "selective-paste-projection-no-selection"),
            "The production projector admitted Select None as a write transaction.");

        static SkyrimSelectiveAppearancePasteEndpoint Endpoint(
            string pluginName,
            uint formId,
            SkyrimSelectiveAppearancePasteDocument document,
            char hashCharacter)
        {
            PluginName plugin = new(pluginName);
            FullyAuthoredSkyrimNpcAppearanceSource appearance = new(
                document.Face.Parts.OrderedHeadParts.Select(item =>
                        (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
                    .ToImmutableArray(),
                new ExternalSkyrimNpcHairColor(
                    document.Face.Parts.HairColor.Value ??
                    throw new InvalidDataException("Test hair color is absent.")),
                new ExternalSkyrimNpcFaceTextureSet(
                    document.Face.Parts.HeadTexture ??
                    throw new InvalidDataException("Test head texture is absent.")),
                document.Body.Weight,
                document.Face.NativeMorphs,
                new SkyrimFaceTintPatch(document.Face.Tints
                    .Where(item => item.IsAuthored)
                    .Select(item => item.Value)
                    .ToImmutableArray()),
                pluginName.StartsWith("target", StringComparison.Ordinal)
                    ? new SkyrimQnamRgb(0.1F, 0.2F, 0.3F)
                    : new SkyrimQnamRgb(0.7F, 0.8F, 0.9F));
            var presetAppearance = new PresetAppearance(
                null,
                [],
                null,
                new PresetWeight(document.Body.Weight, null, null, null),
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                [],
                [],
                [],
                null,
                new PresetFieldPresence(
                    false, false, false, true, false, false, false, false, false),
                [],
                RaceMenu: new RaceMenuPresetData(
                    null, [], 10_000, [],
                    ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty,
                    document.Body.BodyOverlays,
                    document.Body.NodeTransforms,
                    document.Body.SkinOverrides));
            return new SkyrimSelectiveAppearancePasteEndpoint(
                plugin,
                new WorkspacePath(
                    $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\{pluginName}"),
                new Sha256Hash(new string(hashCharacter, 64)),
                new FormId(formId),
                new EditorId(pluginName.StartsWith("target", StringComparison.Ordinal)
                    ? "SelectiveTargetNpc"
                    : "SelectiveSourceNpc"),
                new FormReference(plugin, new FormId(formId + 1)),
                NpcSex.Female,
                appearance,
                null,
                new WorkspacePath(
                    $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\{Path.GetFileNameWithoutExtension(pluginName)}.jslot"),
                new PresetDocument(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    presetAppearance,
                    new Sha256Hash(new string(hashCharacter, 64)),
                    []),
                document);
        }
    }

    private static SkyrimSelectiveAppearancePasteDocument PasteDocument(
        string prefix,
        float weight,
        bool charGen,
        uint formBase)
    {
        PluginName plugin = new($"{prefix}.esp");
        FormReference Reference(uint offset) => new(plugin, new FormId(formBase + offset));
        ImmutableArray<RaceMenuBodyOverlay> overlays =
        [
            new RaceMenuBodyOverlay("Body [Ovl0]", $"{prefix}-body.dds", null,
                [0.1F, 0.2F, 0.3F, 1F], 0.8F, []),
            new RaceMenuBodyOverlay("Face [Ovl0]", $"{prefix}-face.dds", null,
                [], 1F, [])
        ];

        SkyrimBodyEditorDocument body = BodyBaseline() with
        {
            Weight = weight,
            BodySlide = [new BodySlideSliderValue("Waist", weight / 100F)],
            BodyOverlays = overlays
        };
        body = SkyrimBodyEditorDocumentRules.UpsertTransform(
            body,
            "NPC Spine [Spn0]",
            false,
            1F + (weight / 1000F),
            null,
            [],
            []);

        SkyrimFaceEditorDocument face = new(
            new SkyrimFaceEditorParts(
            [
                new NpcHeadPartSelection(Reference(1), NpcHeadPartType.Face),
                new NpcHeadPartSelection(Reference(2), NpcHeadPartType.Hair)
            ],
            OptionalFormReference.Set(Reference(3)),
            Reference(4),
            charGen),
            new SkyrimFaceMorphPatch(
                Enumerable.Range(0, 18)
                    .Select(index => (weight + index) / 100F)
                    .ToImmutableArray(),
                weight / 100F,
                [0, 1, 2, 3]),
            [new SkyrimRaceMenuCustomMorphValue($"{prefix}-morph", 0.25F)],
            [new SkyrimFaceEditorTintLayer(
                new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 50, 0),
                true,
                new AssetPath($"actors/{prefix}/tint.dds"),
                null)],
            [new RaceMenuSculptPart($"{prefix}-head", 12, [], true, true)],
            overlays);

        return new SkyrimSelectiveAppearancePasteDocument(
            face,
            body,
            new NpcOutfitSnapshot(Reference(5), Reference(6)));
    }

    private static SkyrimSelectiveAppearancePasteDocument Clone(
        SkyrimSelectiveAppearancePasteDocument value) => value with
        {
            Face = value.Face with
            {
                Parts = value.Face.Parts with
                {
                    OrderedHeadParts = ImmutableArray.CreateRange(
                        value.Face.Parts.OrderedHeadParts)
                },
                NativeMorphs = value.Face.NativeMorphs with
                {
                    Nam9Sliders = ImmutableArray.CreateRange(
                        value.Face.NativeMorphs.Nam9Sliders),
                    NamaValues = ImmutableArray.CreateRange(
                        value.Face.NativeMorphs.NamaValues)
                },
                CustomMorphs = ImmutableArray.CreateRange(value.Face.CustomMorphs),
                Tints = ImmutableArray.CreateRange(value.Face.Tints),
                SculptParts = ImmutableArray.CreateRange(value.Face.SculptParts),
                BodyOverlays = value.Face.BodyOverlays.Select(CloneOverlay).ToImmutableArray()
            },
            Body = value.Body with
            {
                BodySlide = ImmutableArray.CreateRange(value.Body.BodySlide),
                NodeTransforms = value.Body.NodeTransforms.Select(item => item with
                {
                    KeySets = item.KeySets.Select(set => set with
                    {
                        Values = ImmutableArray.CreateRange(set.Values)
                    }).ToImmutableArray(),
                    Position = ImmutableArray.CreateRange(item.Position),
                    RotationMatrix = ImmutableArray.CreateRange(item.RotationMatrix)
                }).ToImmutableArray(),
                SkinOverrides = value.Body.SkinOverrides.Select(item => item with
                {
                    Values = ImmutableArray.CreateRange(item.Values),
                    Textures = ImmutableDictionary.CreateRange(item.Textures),
                    Tint = ImmutableArray.CreateRange(item.Tint)
                }).ToImmutableArray(),
                BodyOverlays = value.Body.BodyOverlays.Select(CloneOverlay).ToImmutableArray()
            }
        };

    private static RaceMenuBodyOverlay CloneOverlay(RaceMenuBodyOverlay value) =>
        value with
        {
            Tint = ImmutableArray.CreateRange(value.Tint),
            Values = ImmutableArray.CreateRange(value.Values)
        };
}
