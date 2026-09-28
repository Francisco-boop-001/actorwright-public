using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceMeshAnchorBindingTests
{
    private static readonly Sha256Hash NifHash = Hash('a');
    private static readonly Sha256Hash TopologyHash = Hash('b');
    private static readonly Sha256Hash PositionHash = Hash('c');
    private static readonly Sha256Hash GeometryHash = Hash('d');
    private static readonly Sha256Hash CameraHash = Hash('e');
    private static readonly Sha256Hash RenderHash = Hash('f');

    public static Task TestDeterministicReviewedRaycast()
    {
        ReferencePresetRenderInput input = Input();
        ReferenceSemanticAnchor anchor = Anchor();
        ReviewedReferenceView view = View(anchor);
        var binder = new ReferencePresetMeshAnchorBinder();
        var request = new ReferenceMeshAnchorBindRequest(
            view,
            anchor,
            input,
            "meshes/head.nif",
            "Head",
            GeometryHash,
            CameraHash,
            RenderHash,
            0.5,
            0.5)
        {
            ExpectedNifSha256 = NifHash,
            ExpectedTopologySha256 = TopologyHash,
            ExpectedRestPositionsSha256 = PositionHash
        };

        ReferenceMeshAnchorBindResult edge = binder.Bind(request);
        Require(edge.Binding is not null && !HasErrors(edge.Diagnostics),
            $"edge raycast refused: {Codes(edge.Diagnostics)}");
        ReferenceMeshAnchorBinding binding = edge.Binding!;
        Require(binding.TriangleOrdinal == 0 &&
                binding.VertexIndex0 == 0 &&
                binding.VertexIndex1 == 1 &&
                binding.VertexIndex2 == 2 &&
                Close(binding.Barycentric0, 0.5) &&
                Close(binding.Barycentric1, 0.0) &&
                Close(binding.Barycentric2, 0.5),
            "shared-edge tie did not retain the first triangle and exact barycentrics");

        ReferenceMeshAnchorBindResult vertex = binder.Bind(
            request with { RenderX = 1.0, RenderY = 1.0 });
        Require(vertex.Binding is not null &&
                vertex.Binding.TriangleOrdinal == 0 &&
                Close(vertex.Binding.Barycentric0, 1.0) &&
                Close(vertex.Binding.Barycentric1, 0.0) &&
                Close(vertex.Binding.Barycentric2, 0.0),
            "vertex hit did not retain the first triangle and unit vertex weight");

        ReferencePresetRenderInput occludedInput = input with
        {
            Shapes = ImmutableArray.Create(input.Shapes[0] with
            {
                Positions =
                [
                    new Vector3(-1, 0, -1),
                    new Vector3(1, 0, -1),
                    new Vector3(1, 0, 1),
                    new Vector3(-1, 0, 1),
                    new Vector3(-1, -0.5F, -1),
                    new Vector3(1, -0.5F, -1),
                    new Vector3(1, -0.5F, 1)
                ],
                TriangleIndices = [0, 1, 2, 0, 2, 3, 4, 5, 6]
            })
        };
        ReferenceMeshAnchorBindResult occluded = binder.Bind(
            request with { RenderInput = occludedInput });
        Require(occluded.Binding is not null &&
                occluded.Binding.TriangleOrdinal == 0,
            "raycast selected an occluded back triangle");

        RequireCode(binder.Bind(request with { RenderX = 1.01 }).Diagnostics,
            "reference-bind-click-bounds");
        RequireCode(binder.Bind(request with
        {
            ExpectedGeometrySha256 = Hash('1')
        }).Diagnostics, "reference-bind-geometry-drift");
        RequireCode(binder.Bind(request with
        {
            ExpectedCameraSha256 = Hash('2')
        }).Diagnostics, "reference-bind-camera-drift");
        RequireCode(binder.Bind(request with
        {
            ExpectedTopologySha256 = Hash('3')
        }).Diagnostics, "reference-bind-topology-drift");
        RequireCode(binder.Bind(request with
        {
            HeadShapeIdentity = "Body"
        }).Diagnostics, "reference-bind-head-shape");
        RequireCode(binder.Bind(request with
        {
            Anchor = anchor with
            {
                ReviewState = ReferenceAnchorReviewState.UnknownHidden
            }
        }).Diagnostics, "reference-bind-anchor-unreviewed");
        return Task.CompletedTask;
    }

    internal static ReferencePresetRenderInput Input()
    {
        var shape = new ReferenceRenderShape(
            "meshes/head.nif",
            "Head",
            [
                new Vector3(-1, 0, -1),
                new Vector3(1, 0, -1),
                new Vector3(1, 0, 1),
                new Vector3(-1, 0, 1)
            ],
            [0, 1, 2, 0, 2, 3],
            [
                Vector2.Zero,
                Vector2.UnitX,
                Vector2.One,
                Vector2.UnitY
            ],
            [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            [0, 0],
            GeometryHash)
        {
            NifSha256 = NifHash,
            TopologySha256 = TopologyHash,
            RestPositionsSha256 = PositionHash
        };
        var camera = new ReferenceOrthographicCamera(
            ReferenceImageViewRole.Front,
            0,
            0,
            -1,
            1,
            -1,
            1,
            -1,
            1,
            CameraHash);
        return new ReferencePresetRenderInput([shape], [], [camera], RenderHash);
    }

    internal static ReferenceSemanticAnchor Anchor(
        ReferenceSemanticAnchorKind kind =
            ReferenceSemanticAnchorKind.ForeheadCenter,
        double x = 0.5,
        double y = 0.5,
        ReferenceAnchorReviewState reviewState =
            ReferenceAnchorReviewState.Accepted,
        bool required = true) =>
        new(kind, 10, x, y, 1.0, required, reviewState);

    internal static ReviewedReferenceView View(
        ReferenceSemanticAnchor anchor,
        ReferenceImageViewRole role = ReferenceImageViewRole.Front,
        double yaw = 0) =>
        new(role.ToWireName(), role, Hash('9'), 1.0, yaw, true, [anchor]);

    internal static Sha256Hash Hash(char value) => new(new string(value, 64));

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 0.000001;

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void RequireCode(
        ImmutableArray<Diagnostic> diagnostics,
        string code) =>
        Require(diagnostics.Any(item => item.Code == code),
            $"missing-diagnostic:{code}:{Codes(diagnostics)}");

    private static string Codes(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));
}
