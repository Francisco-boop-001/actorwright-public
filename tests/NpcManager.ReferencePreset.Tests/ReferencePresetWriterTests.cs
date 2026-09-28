using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferencePresetWriterTests
{
    private const string LabRoot = @"K:\ExampleWorkspace";
    public static Sha256Hash? CanonicalPresetSha256 { get; private set; }

    public static async Task TestTransactionalCanonicalWriter()
    {
        (PresetDocument baseline,
            ReferencePresetResourceSnapshot snapshot,
            ReferencePresetAuthoringProposal proposal,
            Sha256Hash resourceDocumentHash) = Fixture();
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var presetService = new PresetService(
            policy, new WorkspacePath(LabRoot));
        var writer = new ReferenceRaceMenuPresetWriter(
            policy,
            new WorkspacePath(LabRoot),
            presetService);
        string parent = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests");
        string first = Path.Combine(
            parent,
            $"scratch-reference-writer-a-{Environment.ProcessId}-{Guid.NewGuid():N}.jslot");
        string second = Path.Combine(
            parent,
            $"scratch-reference-writer-b-{Environment.ProcessId}-{Guid.NewGuid():N}.jslot");
        string mismatch = Path.Combine(
            parent,
            $"scratch-reference-writer-mismatch-{Environment.ProcessId}-{Guid.NewGuid():N}.jslot");
        try
        {
            ReferencePresetWriteArtifact firstResult =
                await writer.WriteAsync(
                    Request(first), CancellationToken.None);
            ReferencePresetWriteArtifact secondResult =
                await writer.WriteAsync(
                    Request(second), CancellationToken.None);
            byte[] firstBytes =
                await File.ReadAllBytesAsync(first);
            byte[] secondBytes =
                await File.ReadAllBytesAsync(second);
            Require(!HasErrors(firstResult.Diagnostics) &&
                    !HasErrors(secondResult.Diagnostics) &&
                    File.Exists(first) &&
                    File.Exists(second) &&
                    firstResult.PresetSha256 ==
                    secondResult.PresetSha256 &&
                    firstBytes.SequenceEqual(secondBytes),
                $"canonical writes were refused or differed: {Codes(firstResult.Diagnostics)}");
            CanonicalPresetSha256 = firstResult.PresetSha256;

            PresetAppearance result =
                firstResult.Readback.Appearance;
            RaceMenuPresetData resultRaceMenu =
                result.RaceMenu ??
                throw new InvalidOperationException(
                    "written RaceMenu data was absent");
            RaceMenuPresetData baselineRaceMenu =
                baseline.Appearance.RaceMenu ??
                throw new InvalidOperationException(
                    "baseline RaceMenu data was absent");
            Require(resultRaceMenu.Version ==
                    baselineRaceMenu.Version,
                "writer did not preserve RaceMenu version");
            Require(resultRaceMenu.ModNames.SequenceEqual(
                        baselineRaceMenu.ModNames) &&
                    resultRaceMenu.Mods.SequenceEqual(
                        baselineRaceMenu.Mods),
                "writer did not preserve RaceMenu provider tables");
            Require(resultRaceMenu.BodyMorphsKeyed.TryGetValue(
                        "CBBE", out ImmutableDictionary<string, float>?
                            writtenBody) &&
                    writtenBody.TryGetValue(
                        "NPCManager", out float writtenBodyValue) &&
                    writtenBodyValue == 0.4F,
                "writer did not preserve keyed body morphs");
            Require(resultRaceMenu.BodyOverlays.Single().Values.Any(
                        item => item.Key == 42 &&
                                item.Index == 7),
                "writer did not preserve typed unknown overlay values");
            Require(result.HairColor ==
                    baseline.Appearance.HairColor &&
                    resultRaceMenu.HeadTexture ==
                    baselineRaceMenu.HeadTexture,
                "writer changed unreviewed hair or head-texture authority");
            Require(result.HeadParts.Length == 5 &&
                    result.HeadParts.All(item =>
                        item.Identifier.Plugin is not null &&
                        item.Identifier.FormId is not null),
                "reviewed catalog headparts were not re-resolved to portable form identities");
            Require(result.Tints.Length == 1 &&
                    result.Tints[0].Index == 7 &&
                    result.Tints[0].Texture ==
                    "textures/actors/character/character assets/tintmasks/femalehead_brows_01.dds",
                "reviewed target-race tint did not replace baseline tints");
            Require(result.SliderMorphs.Length == 20 &&
                    result.SliderMorphs[0] == 0.25F &&
                    result.SliderMorphs.Skip(1).Take(17)
                        .All(value => value == 0) &&
                    result.SliderMorphs[18] == 18.5F &&
                    result.SliderMorphs[19] == 19.5F &&
                    resultRaceMenu.FaceMorphPresets
                        .SequenceEqual(
                            [2U, uint.MaxValue,
                                uint.MaxValue, uint.MaxValue, 99U]),
                "writer did not replace only NAM9/NAMA face controls");
            Require(result.CustomMorphs["CheekWidth"] ==
                    -0.375F &&
                    result.CustomMorphs["KeepMe"] == 0.25F &&
                    !result.CustomMorphs.ContainsKey("LegalUnused") &&
                    result.OrderedCustomMorphs.Last().Name ==
                    "CheekWidth",
                "writer did not replace legal custom controls while preserving unrelated rows");
            Require(resultRaceMenu.SculptParts.Length == 1 &&
                    resultRaceMenu.SculptParts[0].Host ==
                    "meshes/unrelated.tri",
                "writer did not remove the known face host while preserving unrelated sculpt");

            ReferencePresetWriteArtifact collision =
                await writer.WriteAsync(
                    Request(first), CancellationToken.None);
            Require(HasErrors(collision.Diagnostics) &&
                    collision.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-preset-output-exists"),
                "writer overwrote or accepted a destination collision");

            var mismatchingWriter =
                new ReferenceRaceMenuPresetWriter(
                    policy,
                    new WorkspacePath(LabRoot),
                    new MismatchingPresetService(presetService));
            ReferencePresetWriteArtifact mismatchResult =
                await mismatchingWriter.WriteAsync(
                    Request(mismatch), CancellationToken.None);
            Require(HasErrors(mismatchResult.Diagnostics) &&
                    mismatchResult.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-preset-readback-mismatch") &&
                    !File.Exists(mismatch),
                "semantic readback mismatch was not refused and rolled back");
        }
        finally
        {
            Delete(first);
            Delete(second);
            Delete(mismatch);
        }

        ReferenceRaceMenuPresetWriteRequest Request(
            string destination) =>
            new(
                baseline,
                proposal,
                Hash('e'),
                new WorkspacePath(destination))
            {
                Snapshot = snapshot,
                ResourceSnapshotDocumentSha256 =
                    resourceDocumentHash
            };
    }

    internal static (
        PresetDocument Baseline,
        ReferencePresetResourceSnapshot Snapshot,
        ReferencePresetAuthoringProposal Proposal,
        Sha256Hash ResourceDocumentHash) Fixture()
    {
        ImmutableArray<float> sliders =
            Enumerable.Range(0, 20)
                .Select(index => index + 0.5F)
                .ToImmutableArray();
        var overlayUnknown = new RaceMenuValue(
            42, 9, 7, RaceMenuScalar.FromText("keep"));
        var raceMenu = new RaceMenuPresetData(
            "Skyrim.esm|001234",
            [9U, 9U, 9U, 9U, 99U],
            10_000,
            [
                new RaceMenuSculptPart(
                    "meshes/known.tri",
                    3,
                    [new RaceMenuSculptVertex(
                        0, 0.01F, 0, 0)],
                    true,
                    true),
                new RaceMenuSculptPart(
                    "meshes/unrelated.tri",
                    3,
                    [new RaceMenuSculptVertex(
                        1, 0, 0.01F, 0)],
                    true,
                    true)
            ],
            ImmutableDictionary<string,
                ImmutableDictionary<string, float>>.Empty
                .Add("CBBE", ImmutableDictionary<string, float>
                    .Empty.Add("NPCManager", 0.4F)),
            [
                new RaceMenuBodyOverlay(
                    "Body [Ovl0]",
                    "textures/body.dds",
                    null,
                    [1F, 0.5F, 0.25F, 1F],
                    0.8F,
                    [overlayUnknown])
            ],
            [],
            [],
            [new RaceMenuFaceTexture(
                0, "textures/face.dds")],
            [new PluginName("Skyrim.esm"),
                new PluginName("High Poly Head.esm")],
            [
                new RaceMenuModEntry(
                    0, new PluginName("Skyrim.esm")),
                new RaceMenuModEntry(
                    1, new PluginName("High Poly Head.esm"))
            ],
            new RaceMenuVersion(4, 0x01060000, 0x1234, 0x02020000));
        var appearance = new PresetAppearance(
            1,
            [new PresetHeadPart(
                PresetIdentifier.Parse("Old.esp|000123"), 0)],
            PresetHairColor.FromPackedRgb(0x123456),
            new PresetWeight(35, 0.2F, 0.3F, 0.5F),
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty
                .Add("CheekWidth", 0.8F)
                .Add("LegalUnused", 0.7F)
                .Add("KeepMe", 0.25F),
            sliders,
            [new PresetTint(
                99, 0xFFFF0000, "textures/old.dds")],
            [],
            null,
            new PresetFieldPresence(
                true, true, true, true, true,
                true, true, true, true),
            [],
            RaceMenu: raceMenu,
            OrderedCustomMorphs:
            [
                new SkyrimRaceMenuCustomMorphValue(
                    "CheekWidth", 0.8F),
                new SkyrimRaceMenuCustomMorphValue(
                    "LegalUnused", 0.7F),
                new SkyrimRaceMenuCustomMorphValue(
                    "KeepMe", 0.25F)
            ]);
        var baseline = new PresetDocument(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            appearance,
            Hash('b'),
            []);

        var race = new FormReference(
            new PluginName("Skyrim.esm"),
            new FormId(0x013746));
        FormReference face = Form(
            "High Poly Head.esm", 0x000A06);
        FormReference mouth = Form("Skyrim.esm", 0x0004D0D);
        FormReference eyes = Form("Eyes.esp", 0x001234);
        FormReference brows = Form("Brows.esp", 0x002345);
        FormReference hair = Form("Hair.esp", 0x003456);
        var selection = new ReferencePresetCatalogSelection(
            true, face, mouth, eyes, brows, hair,
            [new ReferenceTintSelection(
                7, 1, 0xCCB08070, 0.75)]);
        var provider = new SkyrimFaceRecordProvider(
            new PluginName("Catalog.esp"),
            new WorkspacePath(Path.Combine(
                LabRoot, "projects", "controlled",
                "Catalog.esp")),
            Hash('a'));
        var catalog = new ReferencePresetCatalogAuthority(
            race,
            provider,
            NpcSex.Female,
            [
                Part(face, NpcHeadPartType.Face, provider),
                Part(mouth, NpcHeadPartType.Misc, provider),
                Part(eyes, NpcHeadPartType.Eyes, provider),
                Part(brows, NpcHeadPartType.Eyebrows, provider),
                Part(hair, NpcHeadPartType.Hair, provider)
            ],
            [
                new ReferencePresetCatalogTint(
                    selection.Tints[0],
                    0,
                    SkyrimRaceTintMaskKind.Other,
                    new AssetPath(
                        "textures/actors/character/character assets/tintmasks/femalehead_brows_01.dds"))
            ])
        {
            RaceEditorId = "NordRace",
            MorphRaceEditorId = "NordRace",
            RaceKeywordEditorIds = []
        };
        var plan = new SkyrimFaceMorphPlanBuildRequest(
            3,
            "NordRace",
            true,
            new SkyrimFaceMorphSnapshot(
                ImmutableArray.CreateRange(new float[18]),
                float.MaxValue,
                ImmutableArray.CreateRange(
                    Enumerable.Repeat(uint.MaxValue, 4)),
                true,
                true),
            [],
            [],
            [],
            50,
            new SkyrimRaceMenuSliderCatalog([], []),
            null,
            new SkyrimFaceMorphTriSource(
                SkyrimFaceMorphTriRole.Chargen,
                new SseTriHeadDocument(
                    new AssetPath("meshes/known.tri"),
                    Hash('1'),
                    3, 0, 0, 0,
                    [], [])),
            null,
            [],
            true);
        var snapshot = new ReferencePresetResourceSnapshot(
            1,
            "writer-test",
            Hash('c'),
            baseline.SourceHash,
            baseline,
            selection,
            [],
            [
                new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.NativePreset,
                    "NAM9[0]", 0, -1, 1, Hash('1')),
                new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.NativePreset,
                    "NAMA[0]", 1, 0, 8, Hash('2')),
                new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.Custom,
                    "CheekWidth", 2, -1, 1, Hash('3')),
                new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.Custom,
                    "LegalUnused", 3, -1, 1, Hash('4'))
            ],
            [],
            Hash('d'),
            Hash('f'),
            [])
        {
            CatalogAuthority = catalog,
            RaceMenuCatalog =
                new SkyrimRaceMenuSliderCatalog([], []),
            MorphBases =
            [
                new ReferenceFaceMorphShapeBasis(
                    "meshes/head.nif", "Head", plan)
            ]
        };
        var solver = new ReferenceRaceMenuPresetSolverResult(
            ImmutableDictionary<string, double>.Empty
                .Add("NAM9[0]", 0.25)
                .Add("NAMA[0]", 2),
            ImmutableDictionary<string, double>.Empty
                .Add("CheekWidth", -0.375),
            [],
            [],
            [],
            ReferencePresetAuthoringRules.SolverIterations,
            0.1,
            Hash('9'),
            []);
        var comparison = new ReferencePresetComparisonResult(
            [], [], [], []);
        Sha256Hash resourceDocumentHash = Hash('d');
        var proposal = new ReferencePresetAuthoringProposal(
            1,
            ReferencePresetAuthorityKind.AuthoringProposal,
            "writer-test",
            race,
            NpcSex.Female,
            50,
            snapshot.ReviewedDesignSha256,
            resourceDocumentHash,
            solver,
            comparison,
            [new ReferencePresetLoss(
                "accepted-loss",
                ReferencePresetLossKind.SculptUnavailable,
                "accepted",
                true)],
            []);
        return (baseline, snapshot, proposal,
            resourceDocumentHash);
    }

    private static ReferencePresetCatalogHeadPart Part(
        FormReference reference,
        NpcHeadPartType type,
        SkyrimFaceRecordProvider provider) =>
        new(
            reference,
            provider,
            "Part" + ((int)type).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            type,
            new AssetPath(
                $"meshes/part-{(int)type}.nif"),
            [],
            [],
            null,
            true);

    private static FormReference Form(
        string plugin,
        uint formId) =>
        new(new PluginName(plugin), new FormId(formId));

    private static Sha256Hash Hash(char value) =>
        ReferenceMeshAnchorBindingTests.Hash(value);

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static string Codes(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));

    private static void Require(
        bool condition,
        string message) =>
        ReferenceMeshAnchorBindingTests.Require(
            condition, message);

    private static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class MismatchingPresetService(
        IPresetService inner) : IPresetService
    {
        public async ValueTask<PresetParseResult> InspectAsync(
            PresetParseRequest request,
            CancellationToken cancellationToken)
        {
            PresetParseResult parsed =
                await inner.InspectAsync(
                    request, cancellationToken);
            return parsed.Document is null
                ? parsed
                : parsed with
                {
                    Document = parsed.Document with
                    {
                        Appearance =
                            parsed.Document.Appearance with
                            {
                                HairColor =
                                    PresetHairColor.FromPackedRgb(
                                        0xFFFFFF)
                            }
                    }
                };
        }

        public ValueTask<PresetExportResult> ExportAsync(
            PresetExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PresetDiffResult> DiffAsync(
            PresetDiffRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
