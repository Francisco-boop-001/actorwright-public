using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Converts one explicitly reviewed baseline-render click into an exact
/// topology-bound triangle/barycentric identity. It performs no semantic
/// landmark-to-Skyrim-vertex inference.
/// </summary>
public sealed class ReferencePresetMeshAnchorBinder
    : IReferencePresetMeshAnchorBinder
{
    private const double IntersectionTolerance = 0.00000001;
    private const double TieTolerance = 0.000000001;

    public ReferenceMeshAnchorBindResult Bind(
        ReferenceMeshAnchorBindRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (request.View is null ||
            !request.View.ReviewAccepted ||
            request.Anchor is null ||
            request.Anchor.ReviewState is not
                (ReferenceAnchorReviewState.Accepted or
                 ReferenceAnchorReviewState.Corrected) ||
            !request.View.Anchors.Any(item =>
                item == request.Anchor &&
                item.ReviewState is
                    (ReferenceAnchorReviewState.Accepted or
                     ReferenceAnchorReviewState.Corrected)))
        {
            diagnostics.Add(Error(
                "reference-bind-anchor-unreviewed",
                "Only an accepted or corrected anchor in an accepted reviewed view may be bound."));
        }

        if (!double.IsFinite(request.RenderX) ||
            !double.IsFinite(request.RenderY) ||
            request.RenderX < 0 ||
            request.RenderX > 1 ||
            request.RenderY < 0 ||
            request.RenderY > 1)
        {
            diagnostics.Add(Error(
                "reference-bind-click-bounds",
                "The reviewed render click must be finite and within normalized zero through one."));
        }

        if (request.RenderInput is null ||
            request.RenderInput.InputSha256 != request.ExpectedRenderSha256)
        {
            diagnostics.Add(Error(
                "reference-bind-render-drift",
                "The current render input no longer matches the reviewed render hash."));
        }

        ReferenceOrthographicCamera[] matchingCameras =
            request.RenderInput?.Cameras
                .Where(item => item.ViewRole == request.View!.ViewRole)
                .ToArray() ?? [];
        ReferenceOrthographicCamera? camera =
            matchingCameras.Length == 1 ? matchingCameras[0] : null;
        if (camera is null ||
            camera.CameraSha256 != request.ExpectedCameraSha256)
        {
            diagnostics.Add(Error(
                "reference-bind-camera-drift",
                "The current reviewed camera no longer matches its expected hash."));
        }

        ReferenceRenderShape[] matchingShapes =
            request.RenderInput?.Shapes
                .Where(item =>
                    string.Equals(
                        item.NifIdentity,
                        request.HeadNifIdentity,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        item.ShapeIdentity,
                        request.HeadShapeIdentity,
                        StringComparison.Ordinal))
                .ToArray() ?? [];
        if (matchingShapes.Length != 1)
        {
            diagnostics.Add(Error(
                "reference-bind-head-shape",
                "The reviewed head NIF/shape identity must resolve to exactly one render shape."));
        }

        ReferenceRenderShape? shape =
            matchingShapes.Length == 1 ? matchingShapes[0] : null;
        if (shape is not null &&
            shape.GeometrySha256 != request.ExpectedGeometrySha256)
        {
            diagnostics.Add(Error(
                "reference-bind-geometry-drift",
                "The current head geometry no longer matches its reviewed hash."));
        }
        if (shape is not null &&
            request.ExpectedNifSha256 is { } expectedNif &&
            shape.NifSha256 != expectedNif)
        {
            diagnostics.Add(Error(
                "reference-bind-nif-drift",
                "The selected head NIF no longer matches its reviewed hash."));
        }
        if (shape is not null &&
            request.ExpectedTopologySha256 is { } expectedTopology &&
            shape.TopologySha256 != expectedTopology)
        {
            diagnostics.Add(Error(
                "reference-bind-topology-drift",
                "The selected head topology no longer matches its reviewed hash."));
        }
        if (shape is not null &&
            request.ExpectedRestPositionsSha256 is { } expectedPositions &&
            shape.RestPositionsSha256 != expectedPositions)
        {
            diagnostics.Add(Error(
                "reference-bind-rest-position-drift",
                "The selected head rest positions no longer match their reviewed hash."));
        }

        if (shape is not null &&
            (!IsHashBound(shape.NifSha256) ||
             !IsHashBound(shape.TopologySha256) ||
             !IsHashBound(shape.RestPositionsSha256) ||
             shape.Positions.IsDefaultOrEmpty ||
             shape.TriangleIndices.IsDefaultOrEmpty ||
             shape.TriangleIndices.Length % 3 != 0))
        {
            diagnostics.Add(Error(
                "reference-bind-head-authority",
                "The selected head shape lacks complete NIF, topology, position, or triangle authority."));
        }

        if (HasErrors(diagnostics) ||
            camera is null ||
            shape is null)
        {
            return new ReferenceMeshAnchorBindResult(
                null, diagnostics.ToImmutable());
        }
        ReviewedReferenceView reviewedView = request.View!;
        ReferenceSemanticAnchor reviewedAnchor = request.Anchor!;
        ReferencePresetRenderInput renderInput = request.RenderInput!;

        if (!ReferenceOrthographicProjection.TryCreateRay(
                camera,
                request.RenderX,
                request.RenderY,
                out Vector3 origin,
                out Vector3 direction,
                out double maximumDistance))
        {
            diagnostics.Add(Error(
                "reference-bind-camera",
                "The reviewed camera cannot produce a finite fixed orthographic ray."));
            return new ReferenceMeshAnchorBindResult(
                null, diagnostics.ToImmutable());
        }

        Hit? nearest = null;
        for (var triangle = 0;
             triangle < shape.TriangleIndices.Length / 3;
             triangle++)
        {
            int offset = triangle * 3;
            int a = shape.TriangleIndices[offset];
            int b = shape.TriangleIndices[offset + 1];
            int c = shape.TriangleIndices[offset + 2];
            if (a < 0 || b < 0 || c < 0 ||
                a >= shape.Positions.Length ||
                b >= shape.Positions.Length ||
                c >= shape.Positions.Length ||
                a == b || a == c || b == c)
            {
                diagnostics.Add(Error(
                    "reference-bind-triangle",
                    $"Head triangle {triangle} has invalid vertex indices."));
                return new ReferenceMeshAnchorBindResult(
                    null, diagnostics.ToImmutable());
            }

            if (!TryIntersect(
                    origin,
                    direction,
                    maximumDistance,
                    shape.Positions[a],
                    shape.Positions[b],
                    shape.Positions[c],
                    out double distance,
                    out double barycentric0,
                    out double barycentric1,
                    out double barycentric2))
            {
                continue;
            }

            if (nearest is null ||
                distance < nearest.Distance - TieTolerance)
            {
                nearest = new Hit(
                    triangle,
                    a,
                    b,
                    c,
                    distance,
                    barycentric0,
                    barycentric1,
                    barycentric2);
            }
        }

        if (nearest is null)
        {
            diagnostics.Add(Error(
                "reference-bind-no-hit",
                "The reviewed render click did not intersect the exact head geometry."));
            return new ReferenceMeshAnchorBindResult(
                null, diagnostics.ToImmutable());
        }

        Hit hit = nearest;
        var binding = new ReferenceMeshAnchorBinding(
            reviewedView.ViewRole,
            reviewedAnchor.Anchor,
            shape.NifIdentity,
            shape.ShapeIdentity,
            shape.NifSha256,
            shape.TopologySha256,
            shape.RestPositionsSha256,
            camera.CameraSha256,
            renderInput.InputSha256,
            hit.TriangleOrdinal,
            hit.Vertex0,
            hit.Vertex1,
            hit.Vertex2,
            hit.Barycentric0,
            hit.Barycentric1,
            hit.Barycentric2);
        diagnostics.Add(new Diagnostic(
            "reference-bind-accepted",
            DiagnosticSeverity.Info,
            $"Bound '{reviewedAnchor.Anchor.ToWireName()}' to triangle {hit.TriangleOrdinal} on '{shape.ShapeIdentity}'."));
        return new ReferenceMeshAnchorBindResult(
            binding, diagnostics.ToImmutable());
    }

    private static bool TryIntersect(
        Vector3 origin,
        Vector3 direction,
        double maximumDistance,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out double distance,
        out double barycentric0,
        out double barycentric1,
        out double barycentric2)
    {
        distance = 0;
        barycentric0 = 0;
        barycentric1 = 0;
        barycentric2 = 0;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        double determinant = Vector3.Dot(edge1, p);
        if (!double.IsFinite(determinant) ||
            Math.Abs(determinant) <= IntersectionTolerance)
        {
            return false;
        }

        double inverse = 1.0 / determinant;
        Vector3 offset = origin - a;
        double u = Vector3.Dot(offset, p) * inverse;
        if (!double.IsFinite(u) ||
            u < -IntersectionTolerance ||
            u > 1.0 + IntersectionTolerance)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(offset, edge1);
        double v = Vector3.Dot(direction, q) * inverse;
        if (!double.IsFinite(v) ||
            v < -IntersectionTolerance ||
            u + v > 1.0 + IntersectionTolerance)
        {
            return false;
        }

        double t = Vector3.Dot(edge2, q) * inverse;
        if (!double.IsFinite(t) ||
            t < -IntersectionTolerance ||
            t > maximumDistance + IntersectionTolerance)
        {
            return false;
        }

        double w = 1.0 - u - v;
        w = ClampBoundary(w);
        u = ClampBoundary(u);
        v = ClampBoundary(v);
        double sum = w + u + v;
        if (!double.IsFinite(sum) || sum <= 0)
        {
            return false;
        }

        distance = Math.Max(0, t);
        barycentric0 = w / sum;
        barycentric1 = u / sum;
        barycentric2 = v / sum;
        return true;
    }

    private static double ClampBoundary(double value) =>
        value is < 0 and >= -IntersectionTolerance
            ? 0
            : value is > 1 and <= 1 + IntersectionTolerance
                ? 1
                : value;

    private static bool IsHashBound(Sha256Hash hash) =>
        hash.Value is { Length: 64 } &&
        hash.Value.Any(character => character != '0');

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record Hit(
        int TriangleOrdinal,
        int Vertex0,
        int Vertex1,
        int Vertex2,
        double Distance,
        double Barycentric0,
        double Barycentric1,
        double Barycentric2);
}
