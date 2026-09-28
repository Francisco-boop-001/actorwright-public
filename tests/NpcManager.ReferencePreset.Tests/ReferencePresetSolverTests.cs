using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferencePresetSolverTests
{
    internal static PresetDocument CreateEmptyPresetForTests() =>
        EmptyPreset();

    private const string AuthenticHeadNif =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KL\High Poly Head\FemaleHead.nif";
    private const string AuthenticHeadTri =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\face-bake-bases\meshes\KL\High Poly Head\FemaleHeadCharGen.tri";

    public static Sha256Hash? AuthenticMatrixSha256 { get; private set; }
    public static Sha256Hash? SolverResultSha256 { get; private set; }
    public static Sha256Hash? SculptResultSha256 { get; private set; }

    public static async Task TestRealFaceMathResponseMatrix()
    {
        ReferencePresetRenderInput renderInput =
            ReferenceMeshAnchorBindingTests.Input();
        ReferenceMeshAnchorBinding binding = Binding(
            ReferenceImageViewRole.Front,
            ReferenceSemanticAnchorKind.ForeheadCenter,
            0,
            1,
            2,
            0.5,
            0,
            0.5);
        ReviewedReferencePresetDesign design = Design(
            [ReferenceMeshAnchorBindingTests.View(
                ReferenceMeshAnchorBindingTests.Anchor())],
            [binding],
            []);
        ImmutableArray<SseTriHeadVertexDelta> right =
        [
            new(0, new Vector3(0.2F, 0, 0)),
            new(1, new Vector3(0.2F, 0, 0)),
            new(2, new Vector3(0.2F, 0, 0)),
            new(3, new Vector3(0.2F, 0, 0))
        ];
        ImmutableArray<SseTriHeadVertexDelta> up =
        [
            new(0, new Vector3(0, 0, 0.2F)),
            new(1, new Vector3(0, 0, 0.2F)),
            new(2, new Vector3(0, 0, 0.2F)),
            new(3, new Vector3(0, 0, 0.2F))
        ];
        SseTriHeadDocument tri = Tri(
            new SseTriHeadMorph(
                "MoveRight",
                SseTriHeadMorphEncoding.DenseInt16,
                1,
                right),
            new SseTriHeadMorph(
                "MoveUp",
                SseTriHeadMorphEncoding.DenseInt16,
                1,
                up));
        ReferencePresetResourceSnapshot snapshot = Snapshot(
            design,
            [
                Channel("MoveRight", 0, right),
                Channel("MoveUp", 1, up)
            ]);
        var request = new RaceMenuTriResponseMatrixBuildRequest(
            design,
            snapshot,
            renderInput)
        {
            MorphBases =
            [
                new ReferenceFaceMorphShapeBasis(
                    "meshes/head.nif",
                    "Head",
                    PlanTemplate(tri))
            ]
        };
        var builder = new RaceMenuTriResponseMatrixBuilder(
            new SseFaceMorphPlanBuilder(),
            new SseFaceMorphEvaluator());
        RaceMenuTriResponseMatrixBuildResult result =
            await builder.BuildAsync(request, CancellationToken.None);
        Require(result.Accepted,
            $"response matrix refused: {Codes(result.Diagnostics)}");
        Require(result.Responses.Length == 2 &&
                result.BaselineProjections.Length == 1 &&
                result.MatrixSha256 is not null,
            "response matrix lost channels, baseline, or hash authority");
        ReferenceAnchorDisplacement horizontal =
            result.Responses[0].Displacements.Single();
        ReferenceAnchorDisplacement vertical =
            result.Responses[1].Displacements.Single();
        Require(Close(horizontal.DeltaX, -0.1) &&
                Close(horizontal.DeltaY, 0) &&
                Close(vertical.DeltaX, 0) &&
                Close(vertical.DeltaY, -0.1),
            "response columns did not use the real face-plan/evaluator and exact camera projection");

        RaceMenuTriResponseMatrixBuildResult repeated =
            await builder.BuildAsync(request, CancellationToken.None);
        Require(repeated.MatrixSha256 == result.MatrixSha256,
            "identical response-matrix builds changed hash");

        RaceMenuTriResponseMatrixBuildResult drifted =
            await builder.BuildAsync(
                request with
                {
                    RenderInput = renderInput with
                    {
                        InputSha256 = Hash('1')
                    }
                },
                CancellationToken.None);
        Require(!drifted.Accepted &&
                drifted.Diagnostics.Any(item =>
                    item.Code == "reference-response-render-drift"),
            "response matrix accepted render authority drift");
    }

    public static async Task TestFixedSolverAndBoundedSculpt()
    {
        ReferenceSemanticAnchor known =
            ReferenceMeshAnchorBindingTests.Anchor(
                ReferenceSemanticAnchorKind.LeftWidestCheek,
                0.74,
                0.5);
        ReferenceSemanticAnchor unknown =
            ReferenceMeshAnchorBindingTests.Anchor(
                ReferenceSemanticAnchorKind.NoseTip,
                0.99,
                0.01,
                ReferenceAnchorReviewState.UnknownHidden,
                required: false);
        ReviewedReferenceView front = new(
            "front",
            ReferenceImageViewRole.Front,
            Hash('9'),
            1,
            0,
            true,
            [known, unknown]);
        ReferenceMeshAnchorBinding knownBinding = Binding(
            ReferenceImageViewRole.Front,
            known.Anchor,
            0, 1, 2, 0.5, 0, 0.5);
        ReferenceMeshAnchorBinding unknownBinding = Binding(
            ReferenceImageViewRole.Front,
            unknown.Anchor,
            0, 1, 2, 0.5, 0, 0.5);
        ReviewedReferencePresetDesign design = Design(
            [front],
            [knownBinding, unknownBinding],
            []);
        ReferencePresetResourceSnapshot snapshot = Snapshot(
            design,
            [
                Channel("WideCheeksA", 0, []),
                Channel("WideCheeksB", 1, []),
                Channel("NAMA[0]=1", 2, [], true, 0, 1),
                Channel("NAMA[0]=2", 3, [], true, 0, 2)
            ]);
        var baseline = new[]
        {
            Projection(ReferenceImageViewRole.Front, known.Anchor, 0.5, 0.5),
            Projection(ReferenceImageViewRole.Front, unknown.Anchor, 0.5, 0.5)
        }.ToImmutableArray();
        var displacements = new[]
        {
            Response(snapshot.MorphChannels[0], 0.30, 0, known.Anchor),
            Response(snapshot.MorphChannels[1], 0.10, 0, known.Anchor),
            Response(snapshot.MorphChannels[2], 0.01, 0, known.Anchor),
            Response(snapshot.MorphChannels[3], 0.01, 0, known.Anchor)
        }.ToImmutableArray();
        RaceMenuTriResponseMatrixBuildResult matrix = Matrix(
            baseline, displacements);
        var solver = new ReferenceRaceMenuPresetSolver();
        var request = new ReferenceRaceMenuPresetSolverRequest(
            design, snapshot, matrix);
        ReferenceRaceMenuPresetSolverResult first =
            await solver.SolveAsync(request, CancellationToken.None);
        ReferenceRaceMenuPresetSolverResult second =
            await solver.SolveAsync(request, CancellationToken.None);
        Require(first.Accepted &&
                first.Iterations ==
                ReferencePresetAuthoringRules.SolverIterations &&
                first.ResultSha256 == second.ResultSha256,
            $"fixed solver was refused or nondeterministic: {Codes(first.Diagnostics)}");
        SolverResultSha256 = first.ResultSha256;
        Require(first.NativeMorphs["NAMA[0]"] == 1,
            "equal discrete NAMA candidates did not choose the first ordinal");
        Require(first.CustomMorphs.Values.All(value =>
                    Math.Abs(value) <=
                    ReferencePresetAuthoringRules.GeneralMorphMagnitudeCap) &&
                first.CustomMorphs.Values.All(value =>
                    value == Math.Round(value, 6,
                        MidpointRounding.AwayFromZero)),
            "continuous solver escaped cap or six-decimal quantization");
        Require(first.Residuals.Single(item =>
                    item.Anchor == unknown.Anchor).Weight == 0,
            "unknown anchor influenced the objective");
        Require(first.Sculpt.IsEmpty &&
                first.Losses.Any(item =>
                    item.Kind == ReferencePresetLossKind.SculptUnavailable),
            "single-view solve invented depth/sculpt");

        ReferenceSemanticAnchor negativeTarget = known with
        {
            X = 0.5,
            Y = 0.34
        };
        ReviewedReferencePresetDesign piecewiseDesign = Design(
            [front with { Anchors = [negativeTarget] }],
            [knownBinding],
            []);
        var nativeChannel = new ReferenceMorphChannelAuthority(
            ReferenceMorphChannelKind.NativePreset,
            "NAM9[1]",
            0,
            -1,
            1,
            Hash('1'))
        {
            NifIdentity = "meshes/head.nif",
            ShapeIdentity = "Head",
            Deltas = [],
            NegativeTriSha256 = Hash('2'),
            NegativeDeltas = []
        };
        ReferencePresetResourceSnapshot piecewiseSnapshot =
            Snapshot(piecewiseDesign, [nativeChannel]);
        var piecewiseResponse = new RaceMenuMorphResponse(
            nativeChannel,
            [new ReferenceAnchorDisplacement(
                front.ViewRole,
                known.Anchor,
                0.2,
                0)],
            Hash('b'),
            Hash('6'),
            Hash('5'))
        {
            MorphSha256 = Hash('a'),
            NegativeDisplacements =
            [
                new ReferenceAnchorDisplacement(
                    front.ViewRole,
                    known.Anchor,
                    0,
                    -0.2)
            ]
        };
        ReferenceRaceMenuPresetSolverResult piecewise =
            await solver.SolveAsync(
                new ReferenceRaceMenuPresetSolverRequest(
                    piecewiseDesign,
                    piecewiseSnapshot,
                    Matrix(
                        [Projection(
                            front.ViewRole,
                            known.Anchor,
                            0.5,
                            0.5)],
                        [piecewiseResponse])),
                CancellationToken.None);
        Require(piecewise.Accepted &&
                piecewise.NativeMorphs["NAM9[1]"] < -0.70 &&
                Math.Abs(piecewise.Residuals.Single().ActualX - 0.5) <
                    0.000001 &&
                piecewise.Residuals.Single().ActualY < 0.37,
            "solver treated the negative NAM9 side as an inverse positive morph instead of its separately proven TRI response");

        ReferenceSemanticAnchor sideTarget = known with { X = 0.695 };
        ReviewedReferenceView side = new(
            "left-three-quarter",
            ReferenceImageViewRole.LeftThreeQuarter,
            Hash('8'),
            1,
            -40,
            true,
            [sideTarget]);
        ReferenceMeshAnchorBinding sideBinding = Binding(
            side.ViewRole,
            known.Anchor,
            0, 1, 2, 0.5, 0, 0.5,
            cameraHash: Hash('7'));
        ReviewedReferencePresetDesign sculptDesign = Design(
            [front with { Anchors = [known with { X = 0.71 }] }, side],
            [knownBinding, sideBinding],
            []);
        ReferencePresetResourceSnapshot sculptSnapshot = Snapshot(
            sculptDesign, []);
        RaceMenuTriResponseMatrixBuildResult sculptMatrix = Matrix(
            [
                Projection(front.ViewRole, known.Anchor, 0.7, 0.5),
                Projection(side.ViewRole, known.Anchor, 0.7, 0.5)
            ],
            []);
        ReferenceRaceMenuPresetSolverResult sculpted =
            await solver.SolveAsync(
                new ReferenceRaceMenuPresetSolverRequest(
                    sculptDesign,
                    sculptSnapshot,
                    sculptMatrix)
                {
                    RenderInput = RenderInputWithSideCamera(),
                    ProtectedNeckRingVertexIndices = [0]
                },
                CancellationToken.None);
        double headDiagonal = Math.Sqrt(8);
        Require(sculpted.Accepted && !sculpted.Sculpt.IsEmpty,
            $"eligible multi-view residual did not produce sculpt: {Codes(sculpted.Diagnostics)}");
        Require(sculpted.Sculpt.All(delta =>
                    delta.VertexIndex != 0 &&
                    Math.Sqrt(delta.X * delta.X +
                              delta.Y * delta.Y +
                              delta.Z * delta.Z) <=
                    headDiagonal *
                    ReferencePresetAuthoringRules.MaximumSculptDisplacementRatio +
                    0.0000001),
            "sculpt changed a protected neck vertex or exceeded its cap");
        SculptResultSha256 = sculpted.ResultSha256;
    }

    public static async Task TestAuthenticHighPolyHeadResponseMatrix()
    {
        byte[] nifBytes = await File.ReadAllBytesAsync(AuthenticHeadNif);
        byte[] triBytes = await File.ReadAllBytesAsync(AuthenticHeadTri);
        var nifPath = new AssetPath(
            "meshes/KL/High Poly Head/FemaleHead.nif");
        var triPath = new AssetPath(
            "meshes/KL/High Poly Head/FemaleHeadCharGen.tri");
        SseSelectedHeadpartNifGeometryReadResult nifResult =
            new SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    nifPath,
                    Sha(nifBytes),
                    ImmutableArray.CreateRange(nifBytes)));
        SseTriHeadReadResult triResult =
            new SseTriHeadReader().Read(
                new SseTriHeadReadRequest(
                    triPath,
                    Sha(triBytes),
                    ImmutableArray.CreateRange(triBytes)));
        Require(nifResult.Accepted &&
                nifResult.Document is not null &&
                triResult.Accepted &&
                triResult.Document is not null,
            $"authentic HPH NIF/TRI parsing failed: {Codes(nifResult.Diagnostics.AddRange(triResult.Diagnostics))}");
        SseSelectedHeadpartNifRestShape sourceShape =
            nifResult.Document!.Shapes.Single();
        SseTriHeadDocument tri = triResult.Document!;
        SseTriHeadMorph noseLong = tri.Morphs.Single(item =>
            item.Name.Equals(
                "NoseLong",
                StringComparison.OrdinalIgnoreCase));
        SseTriHeadMorph noseShort = tri.Morphs.Single(item =>
            item.Name.Equals(
                "NoseShort",
                StringComparison.OrdinalIgnoreCase));

        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in sourceShape.RestPositions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Vector3 extents = maximum - minimum;
        double halfWidth = Math.Max(extents.X, extents.Y) * 0.60;
        double halfHeight = Math.Max(extents.Z, extents.Y) * 0.60;
        double depth =
            Math.Max(Math.Max(extents.X, extents.Y), extents.Z) * 4;
        var camera = new ReferenceOrthographicCamera(
            ReferenceImageViewRole.Front,
            37,
            0,
            -halfWidth,
            halfWidth,
            -halfHeight,
            halfHeight,
            -depth,
            depth,
            Hash('e'));
        ReferenceMeshAnchorBinding positiveBinding =
            BindingAtVisibleMovedVertex(
                sourceShape,
                noseLong,
                camera,
                ReferenceSemanticAnchorKind.NoseTip,
                nifResult.Document.SourceSha256);
        ReferenceMeshAnchorBinding negativeBinding =
            BindingAtVisibleMovedVertex(
                sourceShape,
                noseShort,
                camera,
                ReferenceSemanticAnchorKind.NoseBridge,
                nifResult.Document.SourceSha256);
        var positiveAnchor = new ReferenceSemanticAnchor(
            positiveBinding.Anchor,
            1,
            0.5,
            0.5,
            1,
            true,
            ReferenceAnchorReviewState.Accepted);
        var negativeAnchor = positiveAnchor with
        {
            Anchor = negativeBinding.Anchor,
            SourceLandmarkIndex = 168
        };
        var view = new ReviewedReferenceView(
            "front",
            ReferenceImageViewRole.Front,
            Hash('9'),
            1,
            37,
            true,
            [positiveAnchor, negativeAnchor]);
        ReviewedReferencePresetDesign design = Design(
            [view],
            [positiveBinding, negativeBinding],
            []);
        var renderShape = new ReferenceRenderShape(
            nifPath.Value,
            sourceShape.Name,
            sourceShape.RestPositions,
            sourceShape.TriangleIndices,
            sourceShape.TextureCoordinates,
            sourceShape.Normals,
            Enumerable.Repeat(
                    0,
                    sourceShape.TriangleIndices.Length / 3)
                .ToImmutableArray(),
            Hash('d'))
        {
            NifSha256 = nifResult.Document.SourceSha256,
            TopologySha256 = sourceShape.TopologySha256,
            RestPositionsSha256 =
                sourceShape.PackedPositionSha256
        };
        var renderInput = new ReferencePresetRenderInput(
            [renderShape], [], [camera], Hash('f'));
        ReferenceMorphChannelAuthority channel = new(
            ReferenceMorphChannelKind.NativePreset,
            "NAM9[0]",
            0,
            -1,
            1,
            tri.SourceSha256)
        {
            NifIdentity = nifPath.Value,
            ShapeIdentity = sourceShape.Name,
            Deltas = noseLong.Deltas,
            NegativeTriSha256 = tri.SourceSha256,
            NegativeDeltas = noseShort.Deltas
        };
        SkyrimFaceMorphPlanBuildRequest plan = PlanTemplate(tri) with
        {
            VertexCount = sourceShape.VertexCount,
            ChargenMorphTri = new SkyrimFaceMorphTriSource(
                SkyrimFaceMorphTriRole.Chargen, tri),
            MeshMorphTri = null
        };
        var basis = new ReferenceFaceMorphShapeBasis(
            nifPath.Value,
            sourceShape.Name,
            plan);
        ReferencePresetResourceSnapshot snapshot =
            Snapshot(design, [channel]) with
            {
                MorphBases = [basis]
            };
        var request = new RaceMenuTriResponseMatrixBuildRequest(
            design,
            snapshot,
            renderInput)
        {
            ReviewedDesignSha256 = design.ProposalSha256
        };
        var builder = new RaceMenuTriResponseMatrixBuilder(
            new SseFaceMorphPlanBuilder(),
            new SseFaceMorphEvaluator());
        RaceMenuTriResponseMatrixBuildResult first =
            await builder.BuildAsync(request, CancellationToken.None);
        RaceMenuTriResponseMatrixBuildResult second =
            await builder.BuildAsync(request, CancellationToken.None);
        Require(first.Accepted &&
                first.MatrixSha256 == second.MatrixSha256 &&
                first.Responses.Length == 1,
            $"authentic HPH response matrix failed or drifted: {Codes(first.Diagnostics)}");
        AuthenticMatrixSha256 = first.MatrixSha256;
        RaceMenuMorphResponse response = first.Responses.Single();
        Require(response.Displacements.Any(item =>
                    Math.Abs(item.DeltaX) +
                    Math.Abs(item.DeltaY) > 0.00000001) &&
                response.NegativeDisplacements.Any(item =>
                    Math.Abs(item.DeltaX) +
                    Math.Abs(item.DeltaY) > 0.00000001) &&
                response.MorphSha256 != Hash('0'),
            "authentic HPH NAM9 positive/negative TRI sides did not produce distinct nonzero hash-bound geometry responses");
    }

    private static ReferencePresetRenderInput RenderInputWithSideCamera()
    {
        ReferencePresetRenderInput input =
            ReferenceMeshAnchorBindingTests.Input();
        ReferenceOrthographicCamera front = input.Cameras.Single();
        var side = new ReferenceOrthographicCamera(
            ReferenceImageViewRole.LeftThreeQuarter,
            -40,
            0,
            -1,
            1,
            -1,
            1,
            -1,
            1,
            Hash('7'));
        return input with { Cameras = [front, side] };
    }

    private static ReferenceMeshAnchorBinding
        BindingAtVisibleMovedVertex(
        SseSelectedHeadpartNifRestShape shape,
        SseTriHeadMorph morph,
        ReferenceOrthographicCamera camera,
        ReferenceSemanticAnchorKind anchor,
        Sha256Hash nifSha256)
    {
        foreach (SseTriHeadVertexDelta delta in morph.Deltas)
        {
            if (!ReferenceOrthographicProjection.TryProject(
                    camera,
                    shape.RestPositions[delta.VertexIndex],
                    out double baselineX,
                    out double baselineY,
                    out _) ||
                !ReferenceOrthographicProjection.TryProject(
                    camera,
                    shape.RestPositions[delta.VertexIndex] +
                    delta.Delta,
                    out double changedX,
                    out double changedY,
                    out _) ||
                Math.Abs(changedX - baselineX) +
                Math.Abs(changedY - baselineY) <= 0.00000001)
            {
                continue;
            }
            for (var triangle = 0;
                 triangle < shape.TriangleIndices.Length / 3;
                 triangle++)
            {
                int offset = triangle * 3;
                int a = shape.TriangleIndices[offset];
                int b = shape.TriangleIndices[offset + 1];
                int c = shape.TriangleIndices[offset + 2];
                if (a != delta.VertexIndex &&
                    b != delta.VertexIndex &&
                    c != delta.VertexIndex)
                {
                    continue;
                }
                return new ReferenceMeshAnchorBinding(
                    ReferenceImageViewRole.Front,
                    anchor,
                    "meshes/KL/High Poly Head/FemaleHead.nif",
                    shape.Name,
                    nifSha256,
                    shape.TopologySha256,
                    shape.PackedPositionSha256,
                    camera.CameraSha256,
                    Hash('f'),
                    triangle,
                    a,
                    b,
                    c,
                    a == delta.VertexIndex ? 1 : 0,
                    b == delta.VertexIndex ? 1 : 0,
                    c == delta.VertexIndex ? 1 : 0);
            }
        }
        throw new InvalidOperationException(
            $"authentic morph '{morph.Name}' has no visible triangle-bound moved vertex");
    }

    private static ReferenceMeshAnchorBinding Binding(
        ReferenceImageViewRole role,
        ReferenceSemanticAnchorKind anchor,
        int a,
        int b,
        int c,
        double wa,
        double wb,
        double wc,
        Sha256Hash? cameraHash = null) =>
        new(
            role,
            anchor,
            "meshes/head.nif",
            "Head",
            Hash('a'),
            Hash('b'),
            Hash('c'),
            cameraHash ?? Hash('e'),
            Hash('f'),
            0,
            a,
            b,
            c,
            wa,
            wb,
            wc);

    private static ReferenceMorphChannelAuthority Channel(
        string name,
        int ordinal,
        ImmutableArray<SseTriHeadVertexDelta> deltas,
        bool discrete = false,
        int family = -1,
        int value = -1) =>
        new(
            discrete && family >= 0
                ? ReferenceMorphChannelKind.NativePreset
                : ReferenceMorphChannelKind.Custom,
            name,
            ordinal,
            -1,
            1,
            Hash((char)('1' + ordinal)))
        {
            NifIdentity = "meshes/head.nif",
            ShapeIdentity = "Head",
            Deltas = deltas,
            IsDiscrete = discrete,
            DiscreteFamilyOrdinal = family,
            DiscreteValue = value
        };

    private static RaceMenuMorphResponse Response(
        ReferenceMorphChannelAuthority channel,
        double dx,
        double dy,
        ReferenceSemanticAnchorKind anchor) =>
        new(
            channel,
            [new ReferenceAnchorDisplacement(
                ReferenceImageViewRole.Front,
                anchor,
                dx,
                dy)],
            Hash('b'),
            Hash('6'),
            Hash('5'))
        {
            MorphSha256 = Hash((char)('a' + channel.Ordinal))
        };

    private static ReferenceAnchorProjectionBaseline Projection(
        ReferenceImageViewRole role,
        ReferenceSemanticAnchorKind anchor,
        double x,
        double y) =>
        new(role, anchor, x, y, 1.0);

    private static RaceMenuTriResponseMatrixBuildResult Matrix(
        ImmutableArray<ReferenceAnchorProjectionBaseline> baseline,
        ImmutableArray<RaceMenuMorphResponse> responses) =>
        new(responses, Hash('4'), [])
        {
            BaselineProjections = baseline,
            RenderInputSha256 = Hash('f'),
            ReviewedDesignSha256 = Hash('2')
        };

    private static ReferencePresetResourceSnapshot Snapshot(
        ReviewedReferencePresetDesign design,
        ImmutableArray<ReferenceMorphChannelAuthority> channels) =>
        new(
            1,
            "solver-test",
            design.ProposalSha256,
            Hash('3'),
            EmptyPreset(),
            design.CatalogSelection,
            [],
            channels,
            [],
            Hash('4'),
            Hash('5'),
            []);

    private static ReviewedReferencePresetDesign Design(
        ImmutableArray<ReviewedReferenceView> views,
        ImmutableArray<ReferenceMeshAnchorBinding> bindings,
        ImmutableArray<ReferenceDescriptionTrait> traits)
    {
        FormReference face = new(
            new PluginName("Skyrim.esm"),
            new FormId(0x5161E));
        return new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            Hash('2'),
            true,
            views,
            traits,
            [],
            new ReferencePresetCatalogSelection(
                true, face, face, face, face, face, []),
            bindings);
    }

    private static SkyrimFaceMorphPlanBuildRequest PlanTemplate(
        SseTriHeadDocument tri) =>
        new(
            4,
            "NordRace",
            true,
            new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, 18).ToImmutableArray(),
                0,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                true,
                true),
            [],
            [],
            [],
            100,
            new SkyrimRaceMenuSliderCatalog([], []),
            null,
            null,
            new SkyrimFaceMorphTriSource(
                SkyrimFaceMorphTriRole.Mesh, tri),
            [],
            true);

    private static SseTriHeadDocument Tri(
        params SseTriHeadMorph[] morphs) =>
        new(
            new AssetPath("meshes/head.tri"),
            Hash('0'),
            4,
            2,
            4,
            0,
            ReferenceMeshAnchorBindingTests.Input()
                .Shapes.Single().Positions,
            morphs.ToImmutableArray());

    private static PresetDocument EmptyPreset()
    {
        var presence = new PresetFieldPresence(
            true, true, true, true, true, false, true, false, false, false);
        var appearance = new PresetAppearance(
            1,
            [],
            null,
            new PresetWeight(50, null, null, null),
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            Enumerable.Repeat(0F, 18).ToImmutableArray(),
            [],
            [],
            null,
            presence,
            [],
            RaceMenu: new RaceMenuPresetData(
                null,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                10_000,
                [],
                ImmutableDictionary<string,
                    ImmutableDictionary<string, float>>.Empty,
                [],
                [],
                []));
        return new PresetDocument(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            appearance,
            Hash('3'),
            []);
    }

    private static Sha256Hash Hash(char value) =>
        ReferenceMeshAnchorBindingTests.Hash(value);

    private static Sha256Hash Sha(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 0.000001;

    private static void Require(bool condition, string message) =>
        ReferenceMeshAnchorBindingTests.Require(condition, message);

    private static string Codes(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));
}
