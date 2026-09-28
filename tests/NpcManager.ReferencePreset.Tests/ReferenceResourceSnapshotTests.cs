using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceResourceSnapshotTests
{
    private const string LabRoot = @"K:\ExampleWorkspace";
    private const string ProviderRoot =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\dependency-providers";
    private const string HeadNif =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KL\High Poly Head\FemaleHead.nif";
    private const string HeadTri =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\face-bake-bases\meshes\KL\High Poly Head\FemaleHead.tri";
    private const string BaselineJslot =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\fresh-export-drop\emi2-neutral.jslot";
    private const string ReferenceImage =
        @"K:\ExampleWorkspace\Resources\Screenshot\Sofia Fergar.png";

    public static async Task TestAuthenticCatalogSelection()
    {
        string scratch = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests",
            $"scratch-authentic-catalog-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var sources = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Skyrim.esm"] = Path.Combine(
                    ProviderRoot, "official-masters", "Skyrim.esm"),
                ["High Poly Head.esm"] = Path.Combine(
                    ProviderRoot, "High Poly Head.esm"),
                ["Improved Eyes Skyrim.esp"] = Path.Combine(
                    ProviderRoot, "Improved Eyes Skyrim.esp"),
                ["Koralina's Eyebrows.esp"] = Path.Combine(
                    ProviderRoot, "Koralina's Eyebrows.esp"),
                ["KS Hairdo's.esp"] = Path.Combine(
                    ProviderRoot, "KS Hairdo's.esp")
            };
            foreach ((string plugin, string source) in sources)
            {
                string destination = Path.Combine(scratch, plugin);
                File.Copy(source, destination, overwrite: false);
            }
            const string winnerPlugin = "P12ReferenceWinner.esp";
            string winnerPath = Path.Combine(scratch, winnerPlugin);
            ModKey highPolyKey =
                ModKey.FromNameAndExtension("High Poly Head.esm");
            using (var highPoly = SkyrimMod.CreateFromBinaryOverlay(
                       Path.Combine(scratch, "High Poly Head.esm"),
                       SkyrimRelease.SkyrimSE))
            {
                IHeadPartGetter sourceFace = highPoly.HeadParts.Single(item =>
                    item.FormKey == new FormKey(highPolyKey, 0x000A06));
                var winner = new SkyrimMod(
                    ModKey.FromNameAndExtension(winnerPlugin),
                    SkyrimRelease.SkyrimSE);
                foreach (IMasterReferenceGetter master in
                         highPoly.ModHeader.MasterReferences)
                {
                    winner.ModHeader.MasterReferences.Add(
                        new MasterReference { Master = master.Master });
                }
                winner.ModHeader.MasterReferences.Add(
                    new MasterReference { Master = highPolyKey });
                HeadPart winningFace = sourceFace.DeepCopy();
                winningFace.EditorID = "P12WinningHighPolyFemaleHead";
                winner.HeadParts.Add(winningFace);
                winner.WriteToBinary(
                    new FilePath(winnerPath),
                    new BinaryWriteParameters
                    {
                        ModKey = ModKeyOption.NoCheck,
                        MastersListContent =
                            MastersListContentOption.NoCheck,
                        MastersListOrdering =
                            MastersListOrderingOption.NoCheck,
                        NextFormID = NextFormIDOption.NoCheck
                    });
            }

            var labRoot = new WorkspacePath(LabRoot);
            var dataRoot = new WorkspacePath(scratch);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var loader = new SkyrimFaceRecordPluginAuthorityLoader(
                policy, labRoot);
            var tintReader = new BethesdaSkyrimRaceTintAuthorityReader(loader);
            var reader = new BethesdaReferencePresetCatalogReader(
                loader, tintReader);
            ImmutableArray<PluginName> pluginNames = sources.Keys
                .Append(winnerPlugin)
                .Select(item => new PluginName(item)).ToImmutableArray();
            SkyrimFaceRecordPluginAuthorityResult loaded =
                await loader.LoadAsync(
                    new SkyrimFaceRecordPluginAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        dataRoot,
                        pluginNames),
                    CancellationToken.None);
            Require(loaded.Accepted,
                $"authentic plugin authority refused: {Codes(loaded.Diagnostics)}");
            var race = Form("Skyrim.esm", 0x013746);
            var target = new RaceMenuPresetTarget(
                "p12-009-authentic-emi2-catalog",
                race,
                NpcSex.Female,
                dataRoot,
                loaded.Authorities);
            ReferencePresetCatalogSelection selection = Selection();
            ReferencePresetCatalogReadResult result = await reader.ReadAsync(
                new ReferencePresetCatalogReadRequest(target, selection),
                CancellationToken.None);
            Require(result.Accepted && result.Authority is not null,
                $"authentic catalog selection refused: {Codes(result.Diagnostics)}");
            ReferencePresetCatalogAuthority authority = result.Authority!;
            Require(authority.Race == race &&
                    authority.Sex == NpcSex.Female &&
                    authority.RaceEditorId == "NordRace" &&
                    !string.IsNullOrWhiteSpace(
                        authority.MorphRaceEditorId) &&
                    !authority.RaceKeywordEditorIds.IsDefault &&
                    authority.HeadParts.Length >= 5,
                "authentic catalog did not retain target race/sex/morph metadata and selected closure");
            Require(authority.HeadParts.Any(item =>
                        item.Reference == selection.Face &&
                        item.Provider.Plugin ==
                        new PluginName(winnerPlugin) &&
                        item.EditorId == "P12WinningHighPolyFemaleHead" &&
                        item.Type == NpcHeadPartType.Face &&
                        item.ModelNif.Value.EndsWith(
                            "FemaleHead.nif",
                            StringComparison.OrdinalIgnoreCase)) &&
                    authority.HeadParts.Any(item =>
                        item.Reference == selection.Hair &&
                        item.Provider.Plugin ==
                        new PluginName("KS Hairdo's.esp") &&
                        item.Type == NpcHeadPartType.Hair),
                "authentic copied providers did not win the selected face/hair records");

            var staleTarget = target with
            {
                PluginOrder = target.PluginOrder.SetItem(
                    0,
                    target.PluginOrder[0] with
                    {
                        ExpectedSha256 = Hash('0')
                    })
            };
            ReferencePresetCatalogReadResult stale = await reader.ReadAsync(
                new ReferencePresetCatalogReadRequest(staleTarget, selection),
                CancellationToken.None);
            Require(!stale.Accepted && stale.Diagnostics.Any(item =>
                    item.Code == "reference-catalog-plugin-order-stale"),
                "catalog accepted a stale reviewed provider hash");

            ReferencePresetCatalogReadResult wrongTint = await reader.ReadAsync(
                new ReferencePresetCatalogReadRequest(
                    target,
                    selection with
                    {
                        Tints =
                        [
                            new ReferenceTintSelection(
                                ushort.MaxValue, -999, 0xFFFF_FFFF, 1.0)
                        ]
                    }),
                CancellationToken.None);
            Require(!wrongTint.Accepted && wrongTint.Diagnostics.Any(item =>
                    item.Code == "reference-catalog-tint-unavailable"),
                "catalog accepted a tint index/type absent from the target race");

            File.Delete(Path.Combine(scratch, "KS Hairdo's.esp"));
            ReferencePresetCatalogReadResult disabled = await reader.ReadAsync(
                new ReferencePresetCatalogReadRequest(target, selection),
                CancellationToken.None);
            Require(!disabled.Accepted,
                "catalog accepted a selected provider removed from the reviewed order");
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    public static async Task TestRealNifRenderInput()
    {
        byte[] bytes = await File.ReadAllBytesAsync(HeadNif);
        var reader = new SseSelectedHeadpartNifGeometryReader();
        SseSelectedHeadpartNifGeometryReadResult read = reader.Read(
            new SseSelectedHeadpartNifGeometryReadRequest(
                new AssetPath("meshes/KL/High Poly Head/FemaleHead.nif"),
                Sha(bytes),
                ImmutableArray.CreateRange(bytes)));
        Require(read.Accepted && read.Document is not null,
            $"real head NIF refused: {Codes(read.Diagnostics)}");
        SseSelectedHeadpartNifGeometryDocument document = read.Document!;
        SseSelectedHeadpartNifRestShape sourceShape = document.Shapes.Single();
        SseSelectedHeadpartMaterial sourceMaterial =
            sourceShape.Materials.Single();
        AssetPath diffusePath = sourceMaterial.TextureSlots
            .Single(item => item.Slot == 0).Path;
        var diffuse = new SkyrimAssetAuthority(
            "controlled-real-nif-texture",
            AssetProviderKind.Loose,
            new WorkspacePath(Path.Combine(
                LabRoot, "projects", "NpcManagerReimplementation",
                "01-source-copies", "controlled", "diffuse.dds")),
            Hash('d'),
            diffusePath,
            4,
            Hash('d'));
        ReferenceRenderMaterialAuthority material = new(
            sourceMaterial.MaterialIdentity,
            diffuse,
            null,
            null,
            0xFFFF_FFFF);
        ReferenceRenderShapeAuthority shape = new(
            document.SourcePath.Value,
            sourceShape.Name,
            document.SourceSha256,
            sourceShape.TopologySha256,
            sourceShape.PackedPositionSha256,
            [material])
        {
            RestPositions = sourceShape.RestPositions,
            TriangleIndices = sourceShape.TriangleIndices,
            TextureCoordinates = sourceShape.TextureCoordinates,
            Normals = sourceShape.Normals,
            TriangleMaterialOrdinals = Enumerable.Repeat(
                0, sourceShape.TriangleIndices.Length / 3).ToImmutableArray()
        };
        ReferenceRenderTexture texture = new(
            diffuse,
            1,
            1,
            [10, 20, 30, 255],
            Sha([10, 20, 30, 255]));
        ReviewedReferencePresetDesign design = Design();
        ReferencePresetCatalogAuthority catalog = new(
            Form("Skyrim.esm", 0x013746),
            new SkyrimFaceRecordProvider(
                new PluginName("Skyrim.esm"),
                new WorkspacePath(Path.Combine(
                    LabRoot, "projects", "controlled", "Skyrim.esm")),
                Hash('a')),
            NpcSex.Female,
            [],
            []);
        ReferencePresetResourceSnapshot snapshot = new(
            1,
            "p12-009-real-nif-render",
            Hash('c'),
            Hash('b'),
            EmptyPreset(),
            design.CatalogSelection,
            [diffuse],
            [],
            [shape],
            Hash('e'),
            Hash('f'),
            [])
        {
            CatalogAuthority = catalog,
            RenderTextures = [texture]
        };
        var builder = new ReferencePresetRenderInputBuilder();
        ReferencePresetRenderInputResult result = await builder.BuildAsync(
            new ReferencePresetRenderInputRequest(snapshot, design),
            CancellationToken.None);
        Require(result.Input is not null &&
                !result.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error),
            $"real NIF render input refused: {Codes(result.Diagnostics)}");
        ReferencePresetRenderInput input = result.Input ??
            throw new InvalidOperationException("render input unexpectedly null");
        Require(input.Shapes.Single().Positions.Length == 3832 &&
                input.Shapes.Single().TriangleIndices.Length ==
                7164 * 3 &&
                input.Cameras.Length == 1 &&
                input.InputSha256 != Hash('0'),
            "real NIF render input lost exact geometry or deterministic camera authority");

        ReferenceRenderShapeAuthority corrupted = shape with
        {
            RestPositions = shape.RestPositions.SetItem(
                0, shape.RestPositions[0] + Vector3.UnitX)
        };
        ReferencePresetRenderInputResult refused = await builder.BuildAsync(
            new ReferencePresetRenderInputRequest(
                snapshot with { RenderShapes = [corrupted] },
                design),
            CancellationToken.None);
        Require(refused.Input is null && refused.Diagnostics.Any(item =>
                item.Code == "reference-render-input-position-hash"),
            "render input accepted geometry that drifted from its exact NIF hash");
    }

    public static async Task TestCompleteResourceSnapshot()
    {
        byte[] nifBytes = await File.ReadAllBytesAsync(HeadNif);
        byte[] triBytes = await File.ReadAllBytesAsync(HeadTri);
        byte[] baselineBytes = await File.ReadAllBytesAsync(BaselineJslot);
        byte[] imageBytes = await File.ReadAllBytesAsync(ReferenceImage);
        var nifPath = new AssetPath(
            "meshes/KL/High Poly Head/FemaleHead.nif");
        var triPath = new AssetPath(
            "meshes/KL/High Poly Head/FemaleHead.tri");
        var availableExtensionPath = new AssetPath(
            "meshes/actors/character/FaceGenMorphs/morphs/controlled/available.tri");
        var missingExtensionPath = new AssetPath(
            "meshes/actors/character/FaceGenMorphs/morphs/controlled/missing.tri");
        var content = new Dictionary<string, ImmutableArray<byte>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [nifPath.Value] = ImmutableArray.CreateRange(nifBytes),
            [triPath.Value] = ImmutableArray.CreateRange(triBytes),
            [availableExtensionPath.Value] =
                ImmutableArray.CreateRange(triBytes)
        };
        var planner = new ControlledAssetPlanner(content);
        var materializer = new ControlledAssetMaterializer(content);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var presetService = new PresetService(
            policy, new WorkspacePath(LabRoot));
        ReviewedReferencePresetDesign design = Design();
        FormReference race = Form("Skyrim.esm", 0x013746);
        var pluginAuthority = new SkyrimFaceRecordPluginAuthority(
            new PluginName("Skyrim.esm"),
            new WorkspacePath(Path.Combine(
                ProviderRoot, "official-masters", "Skyrim.esm")),
            Sha(await File.ReadAllBytesAsync(Path.Combine(
                ProviderRoot, "official-masters", "Skyrim.esm"))));
        var target = new RaceMenuPresetTarget(
            "p12-009-controlled-complete-snapshot",
            race,
            NpcSex.Female,
            new WorkspacePath(Path.Combine(
                LabRoot, "projects", "NpcManagerReimplementation")),
            [pluginAuthority]);
        var intake = new ReferencePresetIntake(
            1,
            "p12-009-complete-snapshot",
            "Sofia",
            race,
            NpcSex.Female,
            50,
            "high-poly-head-female",
            new WorkspacePath(BaselineJslot),
            Sha(baselineBytes),
            "wide cheeks",
            [
                new ReferenceImageAuthority(
                    "front",
                    new WorkspacePath(ReferenceImage),
                    Sha(imageBytes),
                    imageBytes.Length,
                    ReferenceImageViewRole.Front)
            ],
            target);
        var provider = new SkyrimFaceRecordProvider(
            new PluginName("P12ReferenceWinner.esp"),
            new WorkspacePath(Path.Combine(
                LabRoot, "projects", "controlled",
                "P12ReferenceWinner.esp")),
            Hash('a'));
        var catalog = new ReferencePresetCatalogAuthority(
            race,
            provider,
            NpcSex.Female,
            [
                new ReferencePresetCatalogHeadPart(
                    design.CatalogSelection.Face,
                    provider,
                    "P12WinningHighPolyFemaleHead",
                    NpcHeadPartType.Face,
                    nifPath,
                    [new SkyrimHdptTriRoute(
                        SkyrimHdptTriRole.Mesh, triPath),
                     new SkyrimHdptTriRoute(
                         SkyrimHdptTriRole.CharGen, triPath)],
                    [],
                    null,
                    true)
            ],
            [])
        {
            RaceEditorId = "NordRace",
            MorphRaceEditorId = "NordRace",
            RaceKeywordEditorIds = []
        };
        var effectiveCompatibility =
            new SelectedCatalogPresetEvaluator(catalog);
        var service = new ReferencePresetResourceSnapshotService(
            presetService,
            effectiveCompatibility,
            new ControlledCatalogReader(catalog),
            planner,
            materializer,
            new EmptyRaceMenuCatalogLoader(),
            new UnrelatedMorphExtensionCatalogParser(
                triPath,
                availableExtensionPath,
                missingExtensionPath),
            new SseSelectedHeadpartNifGeometryReader(),
            new NpcManager.FaceGen.SseTriHeadReader(),
            new ControlledTextureDecoder());
        var request = new ReferencePresetResourceSnapshotRequest(
            intake, design, Hash('c'));
        ReferencePresetResourceSnapshotResult result =
            await service.CreateAsync(request, CancellationToken.None);
        Require(result.Accepted && result.Snapshot is not null,
            $"complete resource snapshot refused: {Codes(result.Diagnostics)}; requested={string.Join(",", planner.RequestedPaths.Select(item => item.Value))}");
        Require(effectiveCompatibility.EvaluationCount == 1,
            "complete resource snapshot did not evaluate exactly one effective candidate");
        ReferencePresetResourceSnapshot snapshot = result.Snapshot!;
        Require(snapshot.Baseline.SourceHash == Sha(baselineBytes) &&
                snapshot.CatalogAuthority == catalog &&
                snapshot.RenderShapes.Length == 1 &&
                snapshot.RenderShapes[0].RestPositions.Length == 3832 &&
                snapshot.RenderShapes[0].TriangleIndices.Length == 7164 * 3 &&
                snapshot.MorphBases.Length == 1 &&
                snapshot.MorphBases[0].PlanTemplate.VertexCount == 3832 &&
                snapshot.AssetAuthorities.Any(item =>
                    item.AssetPath == nifPath) &&
                snapshot.AssetAuthorities.Any(item =>
                    item.AssetPath == triPath) &&
                snapshot.RenderTextures.Length > 0 &&
                !planner.RequestedPaths.Any(path =>
                    path.Value.Contains(
                        "unrelated-extension",
                        StringComparison.OrdinalIgnoreCase)) &&
                planner.RequestedPaths.Contains(
                    availableExtensionPath) &&
                planner.RequestedPaths.Contains(
                    missingExtensionPath) &&
                result.Diagnostics.Any(item =>
                    item.Code ==
                    "reference-resource-optional-extension-unavailable") &&
                snapshot.EnvironmentFingerprint != Hash('0') &&
                snapshot.ResourceFingerprint != Hash('0'),
            "complete snapshot did not retain its baseline, geometry, providers, textures, and fingerprints");

        ReferencePresetResourceSnapshotResult repeated =
            await service.CreateAsync(request, CancellationToken.None);
        Require(repeated.Accepted &&
                repeated.Snapshot?.EnvironmentFingerprint ==
                snapshot.EnvironmentFingerprint &&
                repeated.Snapshot?.ResourceFingerprint ==
                snapshot.ResourceFingerprint,
            "identical complete resource snapshots were not deterministic");

        ReviewedReferencePresetDesign preBindingDesign = design with
        {
            MeshBindings = []
        };
        ReferencePresetResourceSnapshotResult preBinding =
            await service.CreateAsync(
                new ReferencePresetResourceSnapshotRequest(
                    intake,
                    preBindingDesign,
                    Hash('d')),
                CancellationToken.None);
        Require(preBinding.Accepted &&
                preBinding.Snapshot is not null &&
                preBinding.Snapshot.ResourceFingerprint ==
                    snapshot.ResourceFingerprint,
            $"pre-binding resource pass was refused or changed byte authority: {Codes(preBinding.Diagnostics)}");
        var renderBuilder = new ReferencePresetRenderInputBuilder();
        ReferencePresetRenderInputResult preBindingRender =
            await renderBuilder.BuildAsync(
                new ReferencePresetRenderInputRequest(
                    preBinding.Snapshot!,
                    preBindingDesign),
                CancellationToken.None);
        ReferencePresetRenderInputResult finalBindingRender =
            await renderBuilder.BuildAsync(
                new ReferencePresetRenderInputRequest(
                    snapshot,
                    design),
                CancellationToken.None);
        Require(preBindingRender.Input is not null &&
                finalBindingRender.Input is not null &&
                preBindingRender.Input.InputSha256 ==
                    finalBindingRender.Input.InputSha256,
            "pre-binding and final-binding passes did not reproduce one stable reviewed render authority");

        ReferencePresetResourceSnapshotResult staleBaseline =
            await service.CreateAsync(
                request with
                {
                    Intake = intake with
                    {
                        BaselineJslotSha256 = Hash('0')
                    }
                },
                CancellationToken.None);
        Require(!staleBaseline.Accepted &&
                staleBaseline.Diagnostics.Any(item =>
                    item.Code == "reference-resource-baseline-stale"),
            "complete resource snapshot accepted a stale baseline JSlot hash");
    }

    private static ReferencePresetCatalogSelection Selection() => new(
        true,
        Form("High Poly Head.esm", 0x000A06),
        Form("Skyrim.esm", 0x05150F),
        Form("Improved Eyes Skyrim.esp", 0x002889),
        Form("Koralina's Eyebrows.esp", 0x000801),
        Form("KS Hairdo's.esp", 0x0A9555),
        []);

    private static ReviewedReferencePresetDesign Design()
    {
        var anchor = new ReferenceSemanticAnchor(
            ReferenceSemanticAnchorKind.ForeheadCenter,
            10,
            0.5,
            0.2,
            0.9,
            true,
            ReferenceAnchorReviewState.Accepted);
        var view = new ReviewedReferenceView(
            "front",
            ReferenceImageViewRole.Front,
            Hash('a'),
            0.9,
            0,
            true,
            [anchor]);
        var selection = Selection();
        var binding = new ReferenceMeshAnchorBinding(
            ReferenceImageViewRole.Front,
            ReferenceSemanticAnchorKind.ForeheadCenter,
            "meshes/KL/High Poly Head/FemaleHead.nif",
            "FemaleHead_KLH",
            Hash('b'),
            Hash('c'),
            Hash('d'),
            Hash('c'),
            Hash('f'),
            0,
            0,
            1,
            2,
            0.2,
            0.3,
            0.5);
        return new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            Hash('a'),
            true,
            [view],
            [],
            [],
            selection,
            [binding]);
    }

    private static PresetDocument EmptyPreset() => new(
        PresetFormat.RaceMenuJslot,
        GameEdition.SkyrimSpecialEdition,
        new PresetAppearance(
            1,
            [],
            null,
            new PresetWeight(50, null, null, null),
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            [],
            [],
            [],
            null,
            new PresetFieldPresence(
                true, true, false, true, true, false, true, false, false),
            []),
        Hash('b'),
        []);

    private static FormReference Form(string plugin, uint id) =>
        new(new PluginName(plugin), new FormId(id));

    private static Sha256Hash Sha(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash Hash(char value) => new(new string(value, 64));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Codes(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(",", diagnostics.Select(item =>
            $"{item.Severity}:{item.Code}"));

    private sealed class CompatiblePresetEvaluator
        : IRaceMenuPresetCompatibilityEvaluator
    {
        public ValueTask<RaceMenuPresetCompatibilityResult> EvaluateAsync(
            PresetDocument preset,
            RaceMenuPresetTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuPresetCompatibilityResult(
                RaceMenuPresetCompatibilityKind.Compatible, []));
    }

    private sealed class SelectedCatalogPresetEvaluator(
        ReferencePresetCatalogAuthority catalog)
        : IRaceMenuPresetCompatibilityEvaluator
    {
        public int EvaluationCount { get; private set; }

        public ValueTask<RaceMenuPresetCompatibilityResult> EvaluateAsync(
            PresetDocument preset,
            RaceMenuPresetTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvaluationCount++;
            string[] expected = catalog.HeadParts
                .OrderBy(item => (int)item.Type)
                .ThenBy(item => item.Reference.Plugin.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Reference.FormId.Value)
                .Select(item =>
                    $"{item.Reference.Plugin.Value}|{item.Reference.FormId.Value:X6}")
                .ToArray();
            string[] actual = preset.Appearance.HeadParts
                .Select(item => item.Identifier.Raw)
                .ToArray();
            bool matches = expected.SequenceEqual(
                actual, StringComparer.OrdinalIgnoreCase);
            return ValueTask.FromResult(
                new RaceMenuPresetCompatibilityResult(
                    matches
                        ? RaceMenuPresetCompatibilityKind.Compatible
                        : RaceMenuPresetCompatibilityKind.Incompatible,
                    []));
        }
    }

    private sealed class ControlledCatalogReader(
        ReferencePresetCatalogAuthority authority)
        : IBethesdaReferencePresetCatalogReader
    {
        public ValueTask<ReferencePresetCatalogReadResult> ReadAsync(
            ReferencePresetCatalogReadRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferencePresetCatalogReadResult(
                authority, []));
    }

    private sealed class ControlledAssetPlanner(
        Dictionary<string, ImmutableArray<byte>> content)
        : ISkyrimAssetAuthorityPlanner
    {
        private static readonly ImmutableArray<byte> Dds = [1, 2, 3, 4];
        public List<AssetPath> RequestedPaths { get; } = [];

        public ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
            SkyrimAssetAuthorityPlanRequest request,
            CancellationToken cancellationToken)
        {
            ImmutableArray<AssetPath> optional =
                request.OptionalAssets.IsDefault
                    ? []
                    : request.OptionalAssets;
            RequestedPaths.AddRange(
                request.RequiredAssets.AddRange(optional));
            AssetPath[] unexpected = request.RequiredAssets.Where(path =>
                !path.Value.EndsWith(
                    ".dds", StringComparison.OrdinalIgnoreCase) &&
                !content.ContainsKey(path.Value)).ToArray();
            if (unexpected.Length > 0)
            {
                return ValueTask.FromResult(
                    new SkyrimAssetAuthorityPlanResult(
                        false,
                        [],
                        [new Diagnostic(
                            "controlled-asset-unexpected",
                            DiagnosticSeverity.Error,
                            $"Unexpected asset request '{unexpected[0]}'.")]));
            }
            ImmutableArray<AssetPath> unavailableOptional = optional
                .Where(path =>
                    !path.Value.EndsWith(
                        ".dds", StringComparison.OrdinalIgnoreCase) &&
                    !content.ContainsKey(path.Value))
                .ToImmutableArray();
            var authorities = request.RequiredAssets
                .AddRange(optional)
                .Where(path => !unavailableOptional.Contains(path))
                .Select(path =>
            {
                ImmutableArray<byte> bytes =
                    path.Value.EndsWith(".dds",
                        StringComparison.OrdinalIgnoreCase)
                        ? Dds
                        : content[path.Value];
                Sha256Hash hash = Sha(bytes.AsSpan());
                return new SkyrimAssetAuthority(
                    "controlled-snapshot-provider",
                    AssetProviderKind.Loose,
                    new WorkspacePath(Path.Combine(
                        LabRoot, "projects", "controlled",
                        path.Value.Replace('/',
                            Path.DirectorySeparatorChar))),
                    hash,
                    path,
                    bytes.Length,
                    hash);
            }).ToImmutableArray();
            return ValueTask.FromResult(
                new SkyrimAssetAuthorityPlanResult(
                    true, authorities, [], unavailableOptional));
        }
    }

    private sealed class ControlledAssetMaterializer(
        Dictionary<string, ImmutableArray<byte>> content)
        : IReferencePresetAssetMaterializer
    {
        private static readonly ImmutableArray<byte> Dds = [1, 2, 3, 4];

        public ValueTask<ReferencePresetAssetMaterializeResult>
            MaterializeAsync(
                ReferencePresetAssetMaterializeRequest request,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferencePresetAssetMaterializeResult(
                request.Authorities.Select(authority =>
                    new ReferencePresetAssetContent(
                        authority,
                        authority.AssetPath.Value.EndsWith(
                            ".dds", StringComparison.OrdinalIgnoreCase)
                            ? Dds
                            : content[authority.AssetPath.Value]))
                    .ToImmutableArray(),
                []));
    }

    private sealed class EmptyRaceMenuCatalogLoader
        : ISkyrimRaceMenuCatalogAuthorityLoader
    {
        public ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
            SkyrimRaceMenuCatalogAuthorityRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimRaceMenuCatalogAuthorityResult(
                true,
                new SkyrimRaceMenuCatalogParseRequest(
                    request.LoadedPlugins, []),
                [],
                []));
    }

    private sealed class EmptySliderCatalogParser
        : IRaceMenuSliderCatalogParserCore
    {
        public SkyrimRaceMenuCatalogParseResult Parse(
            SkyrimRaceMenuCatalogParseRequest request) =>
            new(
                true,
                new SkyrimRaceMenuSliderCatalog([], []),
                []);
    }

    private sealed class UnrelatedMorphExtensionCatalogParser(
        AssetPath selectedBase,
        AssetPath availableExtension,
        AssetPath missingExtension)
        : IRaceMenuSliderCatalogParserCore
    {
        public SkyrimRaceMenuCatalogParseResult Parse(
            SkyrimRaceMenuCatalogParseRequest request) =>
            new(
                true,
                new SkyrimRaceMenuSliderCatalog(
                    [],
                    [new SkyrimRaceMenuMorphExtension(
                        selectedBase,
                        [availableExtension, missingExtension]),
                     new SkyrimRaceMenuMorphExtension(
                        new AssetPath(
                            "meshes/unrelated-extension/base.tri"),
                        Enumerable.Range(0, 513)
                            .Select(index => new AssetPath(
                                $"meshes/unrelated-extension/{index:D4}.tri"))
                            .ToImmutableArray())]),
                []);
    }

    private sealed class ControlledTextureDecoder
        : IFaceTintTextureContentDecoder
    {
        public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            ImmutableArray<byte> sourceDds,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FaceTintTextureDecodeResult(
                true,
                1,
                1,
                [30, 20, 10, 255],
                Sha(sourceDds.AsSpan()),
                []));
    }
}
