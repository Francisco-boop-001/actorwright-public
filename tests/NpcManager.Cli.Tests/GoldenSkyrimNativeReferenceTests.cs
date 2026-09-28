using System.Text.Json;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly JsonSerializerOptions NativeEvidenceJson = new() { WriteIndented = true };
    private static readonly string[] NativeReferencePlugins = ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp"];
    internal static async Task RunNativeReferenceWorkflowAsync()
    {
        var repository = new WorkspacePath(Path.GetFullPath(Directory.GetCurrentDirectory()));
        var evidence = Child(repository, "artifacts", "task-4-native");
        Directory.CreateDirectory(evidence.Value);
        var imagePath = Child(repository, "tests", "fixtures", "reference-native",
            "fictional-adult-front.jpg");
        var imageHash = new Sha256Hash(HashFile(imagePath));
        Require(imageHash == new Sha256Hash("0408CF550BE91CE02E89E8C93CE7BD470C5BAEBD5984D1947BE702F5C99E33FC"),
            "The product-owned synthetic portrait changed.");
        var image = new ReferenceImageAuthority("fictional-adult-front", imagePath,
            imageHash, new FileInfo(imagePath.Value).Length, ReferenceImageViewRole.Front);
        ReferenceImageDecodeResult decoded = await new SkiaReferenceImageDecoder(repository)
            .DecodeAsync(new ReferenceImageDecodeRequest(image,
                ReferencePresetAuthoringRules.MaximumDecodedBytes), CancellationToken.None);
        Require(decoded.Accepted, "Native portrait decode: " + JsonSerializer.Serialize(decoded.Diagnostics));
        using var native = new MediaPipeNativeApi(repository,
            Child(repository, "runtime", "reference-preset"));
        ReferencePresetRuntimeAdmissionResult admission = native.AdmitRuntime();
        Require(admission.Accepted, "Native runtime admission: " + JsonSerializer.Serialize(admission.Diagnostics));
        ReferenceFaceInferenceResult inferred = await new MediaPipeFaceLandmarkInferenceService(native)
            .InferAsync(new ReferenceFaceInferenceRequest(decoded.Image!, admission.ManifestSha256!.Value),
                CancellationToken.None);
        File.WriteAllText(Child(evidence, "inference.json").Value,
            JsonSerializer.Serialize(new { Image = image, Runtime = admission, Result = inferred },
                NativeEvidenceJson));
        Require(inferred.Accepted && inferred.Inference!.Landmarks.Length == 478,
            "Native portrait inference: " + JsonSerializer.Serialize(inferred.Diagnostics));
        Console.WriteLine($"NATIVE portrait admitted: 478 landmarks, detector={inferred.Inference!.DetectorScore}, image={imageHash.Value}");
        using var scratch = new OwnedScratchDirectory(
            Child(repository, "artifacts", "x").Value,
            "nr-");
        var root = new WorkspacePath(scratch.Root);
        ReferencePresetCatalogSelection selection = PrepareNativeReferenceFixture(repository, root);
        var localImage = Child(root, "portrait.jpg");
        File.Copy(imagePath.Value, localImage.Value);
        var policy = new KOnlyWorkspacePolicy(root, Child(root, "protected"));
        var presets = new PresetService(policy, root);
        var sessions = new ReferencePresetSessionService(policy, root);
        var jslot = ProtocolV2GoldenWorkflowComposition.CreateJslotBuildComposition(root);
        var transaction = ReferencePresetCliComposition.Create(policy, root, sessions, presets, jslot.BuildService);
        var baseline = Child(root, "npc-preflight", "fixture.jslot");
        var race = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746));
        var plugins = NativeReferencePlugins.Select(name =>
        {
            var path = Child(root, "Data", name);
            return new SkyrimFaceRecordPluginAuthority(new PluginName(name), path, new Sha256Hash(HashFile(path)));
        }).ToImmutableArray();
        var intake = new ReferencePresetIntake(1, "native-reference-regression", "NativeReference",
            race, NpcSex.Female, 50, "product-blank-head", baseline, new Sha256Hash(HashFile(baseline)),
            "", [image with { SourcePath = localImage }],
            new RaceMenuPresetTarget("product-native-reference", race, NpcSex.Female, Child(root, "Data"), plugins));
        var intakePath = Child(root, "intake.json");
        Sha256Hash intakeHash = await PersistNativeSession(sessions, intakePath,
            new ReferencePresetSessionDocument(ReferencePresetSessionDocumentKind.Intake, Intake: intake));
        var proposed = await transaction.ProposeDesignAsync(new ReferencePresetDesignProposalRequest(
            intake, intakeHash, Child(root, "inference")), null, CancellationToken.None);
        Require(proposed.Completed, "Actual design proposal: " + JsonSerializer.Serialize(proposed.Diagnostics));
        var inference = proposed.Proposal!.Images.Single();
        var projected = await new ReferenceSemanticLandmarkProjector().ProjectAsync(
            new ReferenceSemanticLandmarkProjectionRequest(inference), CancellationToken.None);
        Require(projected.Anchors.Length > 0, "Portrait semantic projection returned no anchors.");
        var nose = projected.Anchors.Single(a => a.Anchor == ReferenceSemanticAnchorKind.NoseTip);
        var anchor = new ReferenceSemanticAnchor(nose.Anchor, nose.SourceLandmarkIndex, nose.X, nose.Y,
            nose.Confidence, true, ReferenceAnchorReviewState.Accepted);
        var view = new ReviewedReferenceView(inference.ImageId, inference.ViewRole,
            proposed.ProposalSha256!.Value, inference.DetectorScore, 0, true, [anchor]);
        var design = new ReviewedReferencePresetDesign(1, ReferencePresetAuthorityKind.ReviewedDesign,
            proposed.ProposalSha256.Value, true, [view], [], [], selection, []);
        var reviewPath = Child(root, "review-before-binding.json");
        Sha256Hash reviewHash = await PersistNativeSession(sessions, reviewPath,
            new ReferencePresetSessionDocument(ReferencePresetSessionDocumentKind.ReviewedDesign, ReviewedDesign: design));
        var indexer = new BethesdaAssetIndexer();
        var content = new SkyrimAssetContentResolver(policy, root);
        var planner = new SkyrimAssetAuthorityPlanner(indexer, policy, root);
        var pluginLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, root);
        var snapshots = new ReferencePresetResourceSnapshotService(presets,
            new BethesdaRaceMenuPresetCompatibilityEvaluator(pluginLoader),
            new BethesdaReferencePresetCatalogReader(pluginLoader, new BethesdaSkyrimRaceTintAuthorityReader(pluginLoader)),
            planner, new ReferencePresetAssetMaterializer(content),
            new SkyrimRaceMenuCatalogAuthorityLoader(indexer, planner, content), new RaceMenuSliderCatalogParserCore(),
            new SseSelectedHeadpartNifGeometryReader(), new SseTriHeadReader(),
            new InProcessDdsTextureDecoder(root, InProcessDdsTextureDecodeProfile.ReferencePreview));
        var resources = await snapshots.CreateAsync(new ReferencePresetResourceSnapshotRequest(intake, design, reviewHash),
            CancellationToken.None);
        File.WriteAllText(Child(root, "snapshot-admission.json").Value,
            JsonSerializer.Serialize(resources.Diagnostics, NativeEvidenceJson));
        Require(resources.Accepted, "Real resource snapshot: " + JsonSerializer.Serialize(resources.Diagnostics));
        var render = await new ReferencePresetRenderInputBuilder().BuildAsync(
            new ReferencePresetRenderInputRequest(resources.Snapshot!, design), CancellationToken.None);
        Require(render.Input is not null, "Real render input: " + JsonSerializer.Serialize(render.Diagnostics));
        var camera = render.Input!.Cameras.Single();
        var cpu = new ReferencePresetCpuRenderer().Render(new ReferencePresetCpuRenderRequest(
            render.Input.Shapes, render.Input.Textures, camera, 512, 512));
        Require(cpu.Accepted, "Baseline CPU pixels: " + JsonSerializer.Serialize(cpu.Diagnostics));
        File.WriteAllBytes(Child(root, "baseline.png").Value, cpu.PngBytes.ToArray());
        File.WriteAllText(Child(root, "baseline-camera.json").Value,
            JsonSerializer.Serialize(new { Camera = camera, Anchor = anchor, Shapes = render.Input.Shapes.Select(s =>
                new { s.NifIdentity, s.ShapeIdentity, s.GeometrySha256, s.NifSha256, s.TopologySha256, s.RestPositionsSha256 }) }, NativeEvidenceJson));
        Console.WriteLine($"SNAPSHOT admitted: shapes={resources.Snapshot!.RenderShapes.Length}, morphs={resources.Snapshot.MorphChannels.Length}");
        var side = await new ReferencePresetRenderInputBuilder().BuildAsync(
            new ReferencePresetRenderInputRequest(resources.Snapshot, design with { Views = [view with { ReviewedYawDegrees = 90 }] }),
            CancellationToken.None);
        var sidePixels = new ReferencePresetCpuRenderer().Render(new ReferencePresetCpuRenderRequest(
            side.Input!.Shapes, side.Input.Textures, side.Input.Cameras.Single(), 512, 512));
        File.WriteAllBytes(Child(root, "baseline-side.png").Value, sidePixels.PngBytes.ToArray());
        var headShape = render.Input.Shapes.Single(s => s.ShapeIdentity == "FemaleHead");
        // Explicit product-fixture review: the side silhouette identifies the +Y nose tip
        // near Z=120.97. This baseline click is independent of the portrait target.
        var binding = new ReferencePresetMeshAnchorBinder().Bind(new ReferenceMeshAnchorBindRequest(
            view, anchor, render.Input, headShape.NifIdentity, headShape.ShapeIdentity,
            headShape.GeometrySha256, camera.CameraSha256, render.Input.InputSha256, 0.5, 0.491)
        {
            ExpectedNifSha256 = headShape.NifSha256, ExpectedTopologySha256 = headShape.TopologySha256,
            ExpectedRestPositionsSha256 = headShape.RestPositionsSha256
        });
        Require(binding.Accepted, "Reviewed nose click: " + JsonSerializer.Serialize(binding.Diagnostics));
        var b = binding.Binding!;
        var boundPoint = headShape.Positions[b.VertexIndex0] * (float)b.Barycentric0 +
            headShape.Positions[b.VertexIndex1] * (float)b.Barycentric1 +
            headShape.Positions[b.VertexIndex2] * (float)b.Barycentric2;
        File.WriteAllText(Child(root, "nose-binding.json").Value, JsonSerializer.Serialize(new
        {
            Binding = binding, Point = new { boundPoint.X, boundPoint.Y, boundPoint.Z },
            PortraitTarget = anchor, Review = "Synthetic operator attestation from product head front/side; no human likeness approval"
        }, NativeEvidenceJson));
        Require(boundPoint.Y > 9, $"Default front camera bound posterior head instead of the reviewed nose: Y={boundPoint.Y}.");
        Require(camera.CameraSha256 != new Sha256Hash("b6fb80eab77a3bd0490831e94601003d352b0f5becbdd82265a111eecbbd2af1"),
            "Corrected camera reused the prior posterior-view identity.");
        design = design with { MeshBindings = [b] };
        reviewPath = Child(root, "review.json");
        reviewHash = await PersistNativeSession(sessions, reviewPath,
            new ReferencePresetSessionDocument(ReferencePresetSessionDocumentKind.ReviewedDesign, ReviewedDesign: design));
        resources = await snapshots.CreateAsync(new ReferencePresetResourceSnapshotRequest(intake, design, reviewHash), CancellationToken.None);
        Require(resources.Accepted, "Final reviewed snapshot: " + JsonSerializer.Serialize(resources.Diagnostics));
        var snapshotPath = Child(root, "resources.json");
        Sha256Hash snapshotHash = await PersistNativeSession(sessions, snapshotPath,
            new ReferencePresetSessionDocument(ReferencePresetSessionDocumentKind.ResourceSnapshot, ResourceSnapshot: resources.Snapshot));
        render = await new ReferencePresetRenderInputBuilder().BuildAsync(
            new ReferencePresetRenderInputRequest(resources.Snapshot!, design), CancellationToken.None);
        var matrix = await new RaceMenuTriResponseMatrixBuilder(new SseFaceMorphPlanBuilder(), new SseFaceMorphEvaluator())
            .BuildAsync(new RaceMenuTriResponseMatrixBuildRequest(design, resources.Snapshot!, render.Input!), CancellationToken.None);
        File.WriteAllText(Child(root, "matrix.json").Value, JsonSerializer.Serialize(matrix, NativeEvidenceJson));
        Require(matrix.Accepted && matrix.Responses.Any(response => response.Displacements.Any(d =>
            double.IsFinite(d.DeltaY) && Math.Abs(d.DeltaY) > 0.00001)),
            "Actual TRI response matrix has no nonzero finite portrait-anchor response: " + JsonSerializer.Serialize(matrix.Diagnostics));
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var solverRequest = new ReferenceRaceMenuPresetSolverRequest(design, resources.Snapshot!, matrix)
                { RenderInput = render.Input, ReviewedDesignSha256 = reviewHash };
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var dot = await new ReferenceRaceMenuPresetSolver().SolveAsync(solverRequest, CancellationToken.None);
            var commaCulture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            System.Globalization.CultureInfo.CurrentCulture = commaCulture;
            var comma = await new ReferenceRaceMenuPresetSolver().SolveAsync(solverRequest, CancellationToken.None);
            Require(dot.Accepted && comma.Accepted && dot.ResultSha256 == comma.ResultSha256 &&
                dot.Losses.SequenceEqual(comma.Losses), "Native solver hash-bound loss messages change with decimal culture.");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
        var writeRequest = new ReferencePresetWriteRequest(intakePath, intakeHash,
            Child(root, "inference", "landmark-proposal.json"), proposed.ProposalSha256.Value,
            reviewPath, reviewHash, snapshotPath, snapshotHash, Child(root, "proposal"), false, null);
        var authored = await transaction.WritePresetAsync(writeRequest, null, CancellationToken.None);
        File.WriteAllText(Child(root, "authoring-result.json").Value, JsonSerializer.Serialize(authored, NativeEvidenceJson));
        Require(authored.Completed && authored.Proposal is not null,
            "Real solver/comparison/proposal: " + JsonSerializer.Serialize(authored.Diagnostics));
        double morphValue = authored.Proposal!.SolverResult.NativeMorphs["NAM9[0]"];
        Require(double.IsFinite(morphValue) && Math.Abs(morphValue) > 0.00001,
            "The actual portrait target did not drive a nonzero native morph.");
        var applied = await transaction.WritePresetAsync(writeRequest with
        {
            OutputRoot = Child(root, "apply"), Apply = true, AcceptedAuthoringProposalSha256 = authored.ProposalSha256
        }, null, CancellationToken.None);
        File.WriteAllText(Child(root, "apply-result.json").Value, JsonSerializer.Serialize(applied, NativeEvidenceJson));
        Require(applied.Completed && applied.VerifiedPreset is not null,
            "Actual accepted JSlot apply: " + JsonSerializer.Serialize(applied.Diagnostics));
        var verified = applied.VerifiedPreset!;
        var readback = await presets.InspectAsync(new PresetParseRequest(PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition, verified.PresetPath), CancellationToken.None);
        Require(readback.Document is not null && verified.PresetSha256 == new Sha256Hash(HashFile(verified.PresetPath)),
            "Independent emitted JSlot parse/hash failed.");
        using var outputPreset = JsonDocument.Parse(File.ReadAllBytes(verified.PresetPath.Value));
        double writtenMorph = outputPreset.RootElement.GetProperty("morphs").GetProperty("default")
            .GetProperty("morphs")[0].GetDouble();
        Require(Math.Abs(writtenMorph - morphValue) < 0.000001, "Emitted NAM9[0] does not match the actual solver result.");
        foreach (bool wrongType in new[] { true, false })
        {
            var snapshot = resources.Snapshot!;
            var wrongCatalog = snapshot.CatalogAuthority! with
            {
                HeadParts = snapshot.CatalogAuthority!.HeadParts.Select(part => part.Reference == selection.Mouth
                    ? wrongType ? part with { Type = NpcHeadPartType.Teeth } : part with { IsRootSelection = false }
                    : part).ToImmutableArray()
            };
            var refusedPath = Child(root, wrongType ? "wrong-mouth-type.jslot" : "nonroot-mouth.jslot");
            var refused = await new ReferenceRaceMenuPresetWriter(policy, root, presets).WriteAsync(
                new ReferenceRaceMenuPresetWriteRequest(snapshot.Baseline,
                    authored.Proposal with { Losses = authored.Proposal.Losses.Select(loss => loss with { Acknowledged = true }).ToImmutableArray() },
                    authored.ProposalSha256!.Value, refusedPath)
                { Snapshot = snapshot with { CatalogAuthority = wrongCatalog }, ResourceSnapshotDocumentSha256 = snapshotHash },
                CancellationToken.None);
            Require(refused.Diagnostics.Any(d => d.Code == "reference-preset-headpart-authority") && !File.Exists(refusedPath.Value),
                "Writer admitted an incompatible or non-root mouth authority.");
        }
        Console.WriteLine($"SOLVER/APPLY admitted: NAM9[0]={morphValue}, jslot={verified.PresetSha256}");
        await CompleteNativeNpcAsync(repository, root, writeRequest, authored.ProposalSha256!.Value, verified);
    }

    private static async Task<Sha256Hash> PersistNativeSession(ReferencePresetSessionService sessions,
        WorkspacePath path, ReferencePresetSessionDocument document)
    {
        var result = await sessions.WriteAsync(new ReferencePresetSessionWriteRequest(path, document), CancellationToken.None);
        Require(result.Written, "Real session persistence: " + JsonSerializer.Serialize(result.Diagnostics));
        return result.ContentSha256!.Value;
    }
}
