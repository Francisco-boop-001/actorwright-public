using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Projects one complete immutable resource snapshot into deterministic CPU
/// renderer inputs. It performs no provider discovery and writes no files.
/// </summary>
public sealed class ReferencePresetRenderInputBuilder
    : IReferencePresetRenderInputBuilder
{
    public ValueTask<ReferencePresetRenderInputResult> BuildAsync(
        ReferencePresetRenderInputRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Snapshot.SchemaVersion != 1 ||
            request.Snapshot.ResourceFingerprint == ZeroHash() ||
            request.Snapshot.CatalogAuthority is null ||
            request.Snapshot.ReviewedDesignSha256 == ZeroHash())
        {
            diagnostics.Add(Error("reference-render-input-snapshot",
                "A complete schema-1 hash-bound resource snapshot is required."));
            return ValueTask.FromResult(Refused(diagnostics));
        }
        diagnostics.AddRange(ReferencePresetAuthoringRules
            .ValidateReviewedDesignForResourceSnapshot(
            request.ReviewedDesign,
            request.ReviewedDesign.ProposalSha256));
        if (request.ReviewedDesign.Authority !=
            ReferencePresetAuthorityKind.ReviewedDesign)
        {
            diagnostics.Add(Error("reference-render-input-design",
                "Only a reviewed reference design may produce render input."));
        }

        var shapes = ImmutableArray.CreateBuilder<ReferenceRenderShape>(
            request.Snapshot.RenderShapes.Length);
        foreach (ReferenceRenderShapeAuthority authority in
                 request.Snapshot.RenderShapes)
        {
            int vertexCount = authority.RestPositions.Length;
            int triangleCount = authority.TriangleIndices.Length / 3;
            if (vertexCount <= 0 ||
                authority.TriangleIndices.Length <= 0 ||
                authority.TriangleIndices.Length % 3 != 0 ||
                authority.TriangleIndices.Any(index =>
                    index < 0 || index >= vertexCount) ||
                authority.TextureCoordinates.Length != vertexCount ||
                authority.Normals.Length is not 0 &&
                authority.Normals.Length != vertexCount ||
                authority.TriangleMaterialOrdinals.Length != triangleCount ||
                authority.TriangleMaterialOrdinals.Any(index =>
                    index < 0 || index >= authority.Materials.Length) ||
                !authority.RenderPlacement.IsFinite)
            {
                diagnostics.Add(Error("reference-render-input-geometry",
                    $"Render shape '{authority.NifIdentity}/{authority.ShapeIdentity}' has unresolved or invalid geometry."));
                continue;
            }
            Sha256Hash positionsHash = HashPositions(authority.RestPositions);
            if (positionsHash != authority.RestPositionsSha256)
            {
                diagnostics.Add(Error("reference-render-input-position-hash",
                    $"Render shape '{authority.NifIdentity}/{authority.ShapeIdentity}' rest positions no longer match their authority hash."));
                continue;
            }
            Sha256Hash geometryHash = HashGeometry(authority);
            ImmutableArray<Vector3> renderPositions =
                TransformPositions(
                    authority.RestPositions,
                    authority.RenderPlacement);
            ImmutableArray<Vector3> renderNormals =
                TransformNormals(
                    authority.Normals,
                    authority.RenderPlacement);
            if (renderPositions.Any(value => !IsFinite(value)) ||
                renderNormals.Any(value => !IsFinite(value)))
            {
                diagnostics.Add(Error("reference-render-input-placement",
                    $"Render shape '{authority.NifIdentity}/{authority.ShapeIdentity}' produced non-finite placed geometry."));
                continue;
            }
            shapes.Add(new ReferenceRenderShape(
                authority.NifIdentity,
                authority.ShapeIdentity,
                renderPositions,
                authority.TriangleIndices,
                authority.TextureCoordinates,
                renderNormals,
                authority.TriangleMaterialOrdinals,
                geometryHash)
            {
                NifSha256 = authority.NifSha256,
                TopologySha256 = authority.TopologySha256,
                RestPositionsSha256 = authority.RestPositionsSha256,
                SourceRestPositions = authority.RestPositions,
                RenderPlacement = authority.RenderPlacement,
                Materials = authority.Materials
            });
        }

        HashSet<string> texturePaths = request.Snapshot.RenderTextures
            .Select(item => item.Authority.AssetPath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ReferenceRenderMaterialAuthority material in
                 request.Snapshot.RenderShapes.SelectMany(item => item.Materials))
        {
            RequireTexture(material.Diffuse, texturePaths, material.MaterialIdentity,
                diagnostics);
        }
        var hydratedTextures =
            ImmutableArray.CreateBuilder<ReferenceRenderTexture>(
                request.Snapshot.RenderTextures.Length);
        foreach (ReferenceRenderTexture texture in request.Snapshot.RenderTextures)
        {
            if (!texture.TryGetCanonicalRgba(
                    out ImmutableArray<byte> canonicalRgba,
                    out string error))
            {
                diagnostics.Add(Error("reference-render-input-texture",
                    $"Render texture '{texture.Authority.AssetPath}' has invalid decoded authority: {error}"));
                continue;
            }
            hydratedTextures.Add(texture with
            {
                CanonicalRgba = canonicalRgba
            });
        }
        if (HasErrors(diagnostics))
            return ValueTask.FromResult(Refused(diagnostics));

        ImmutableArray<ReferenceOrthographicCamera> cameras = BuildCameras(
            shapes.ToImmutable(), request.ReviewedDesign.Views, diagnostics);
        if (HasErrors(diagnostics))
            return ValueTask.FromResult(Refused(diagnostics));
        ImmutableArray<ReferenceRenderShape> immutableShapes = shapes.ToImmutable();
        Sha256Hash inputHash = HashInput(
            request.Snapshot.ResourceFingerprint,
            immutableShapes,
            hydratedTextures.ToImmutable(),
            cameras);
        diagnostics.Add(new Diagnostic(
            "reference-render-input-built",
            DiagnosticSeverity.Info,
            $"Built {immutableShapes.Length} real Skyrim render shape(s), {request.Snapshot.RenderTextures.Length} decoded texture(s), and {cameras.Length} reviewed camera(s)."));
        return ValueTask.FromResult(new ReferencePresetRenderInputResult(
            new ReferencePresetRenderInput(
                immutableShapes,
                hydratedTextures.ToImmutable(),
                cameras,
                inputHash),
            diagnostics.ToImmutable()));
    }

    private static ImmutableArray<ReferenceOrthographicCamera> BuildCameras(
        ImmutableArray<ReferenceRenderShape> shapes,
        ImmutableArray<ReviewedReferenceView> views,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Vector3[] positions = shapes
            .SelectMany(item => item.Positions)
            .ToArray();
        if (positions.Length == 0)
        {
            diagnostics.Add(Error("reference-render-input-bounds",
                "The selected shape set has no finite nonzero camera bounds."));
            return [];
        }

        var cameras = ImmutableArray.CreateBuilder<ReferenceOrthographicCamera>(
            views.Length);
        foreach (ReviewedReferenceView view in views.OrderBy(item =>
                     (int)item.ViewRole))
        {
            if (!view.ReviewAccepted ||
                !double.IsFinite(view.ReviewedYawDegrees) ||
                view.ReviewedYawDegrees is < -90 or > 90)
            {
                diagnostics.Add(Error("reference-render-input-camera-view",
                    $"View '{view.ImageId}' has no accepted finite reviewed yaw."));
                continue;
            }
            double radians =
                view.ReviewedYawDegrees * Math.PI / 180.0;
            Vector3 right = new(
                (float)-Math.Cos(radians),
                (float)Math.Sin(radians),
                0);
            Vector3 forward = new(
                (float)-Math.Sin(radians),
                (float)-Math.Cos(radians),
                0);
            double minimumX = double.PositiveInfinity;
            double maximumX = double.NegativeInfinity;
            double minimumY = double.PositiveInfinity;
            double maximumY = double.NegativeInfinity;
            double minimumDepth = double.PositiveInfinity;
            double maximumDepth = double.NegativeInfinity;
            foreach (Vector3 position in positions)
            {
                double x = Vector3.Dot(position, right);
                double y = position.Z;
                double cameraDepth = Vector3.Dot(position, forward);
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);
                minimumDepth = Math.Min(minimumDepth, cameraDepth);
                maximumDepth = Math.Max(maximumDepth, cameraDepth);
            }
            double halfWidth =
                (maximumX - minimumX) * 0.60;
            double halfHeight =
                (maximumY - minimumY) * 0.60;
            double maximumSpan = Math.Max(
                Math.Max(maximumX - minimumX,
                    maximumY - minimumY),
                maximumDepth - minimumDepth);
            double depthMargin = maximumSpan * 2.0;
            if (!double.IsFinite(halfWidth) ||
                !double.IsFinite(halfHeight) ||
                !double.IsFinite(depthMargin) ||
                halfWidth <= 0 ||
                halfHeight <= 0 ||
                depthMargin <= 0)
            {
                diagnostics.Add(Error(
                    "reference-render-input-bounds",
                    $"View '{view.ImageId}' has no finite nonzero camera bounds."));
                continue;
            }
            double centerX = (minimumX + maximumX) * 0.5;
            double centerY = (minimumY + maximumY) * 0.5;
            double near = minimumDepth - depthMargin;
            double far = maximumDepth + depthMargin;
            Sha256Hash hash = HashCamera(
                view.ViewRole, view.ReviewedYawDegrees, 0,
                centerX - halfWidth, centerX + halfWidth,
                centerY - halfHeight, centerY + halfHeight,
                near, far);
            cameras.Add(new ReferenceOrthographicCamera(
                view.ViewRole,
                view.ReviewedYawDegrees,
                0,
                centerX - halfWidth,
                centerX + halfWidth,
                centerY - halfHeight,
                centerY + halfHeight,
                near,
                far,
                hash));
        }
        return cameras.ToImmutable();
    }

    private static void RequireTexture(
        SkyrimAssetAuthority authority,
        HashSet<string> texturePaths,
        string materialIdentity,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!texturePaths.Contains(authority.AssetPath.Value))
        {
            diagnostics.Add(Error("reference-render-input-material-texture",
                $"Material '{materialIdentity}' texture '{authority.AssetPath}' has no decoded render texture."));
        }
    }

    private static Sha256Hash HashPositions(ImmutableArray<Vector3> positions)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[12];
        foreach (Vector3 value in positions)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes,
                BitConverter.SingleToInt32Bits(value.X));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..],
                BitConverter.SingleToInt32Bits(value.Y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..],
                BitConverter.SingleToInt32Bits(value.Z));
            hash.AppendData(bytes);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static Sha256Hash HashGeometry(ReferenceRenderShapeAuthority shape)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, shape.NifIdentity);
        Append(hash, shape.ShapeIdentity);
        Append(hash, shape.RestPositionsSha256.Value);
        Append(hash, shape.TopologySha256.Value);
        Span<byte> four = stackalloc byte[4];
        foreach (int index in shape.TriangleIndices)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four, index);
            hash.AppendData(four);
        }
        foreach (Vector2 uv in shape.TextureCoordinates)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four,
                BitConverter.SingleToInt32Bits(uv.X));
            hash.AppendData(four);
            BinaryPrimitives.WriteInt32LittleEndian(four,
                BitConverter.SingleToInt32Bits(uv.Y));
            hash.AppendData(four);
        }
        foreach (Vector3 normal in shape.Normals)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four,
                BitConverter.SingleToInt32Bits(normal.X));
            hash.AppendData(four);
            BinaryPrimitives.WriteInt32LittleEndian(four,
                BitConverter.SingleToInt32Bits(normal.Y));
            hash.AppendData(four);
            BinaryPrimitives.WriteInt32LittleEndian(four,
                BitConverter.SingleToInt32Bits(normal.Z));
            hash.AppendData(four);
        }
        foreach (int ordinal in shape.TriangleMaterialOrdinals)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four, ordinal);
            hash.AppendData(four);
        }
        foreach (ReferenceRenderMaterialAuthority material in shape.Materials)
        {
            Append(hash, material.MaterialIdentity);
            hash.AppendData(
            [
                material.AlphaTestEnabled ? (byte)1 : (byte)0,
                material.AlphaTestThreshold
            ]);
        }
        foreach (float value in PlacementValues(shape.RenderPlacement))
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                four,
                BitConverter.SingleToInt32Bits(value));
            hash.AppendData(four);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static ImmutableArray<Vector3> TransformPositions(
        ImmutableArray<Vector3> positions,
        SseSelectedHeadpartNifPlacement placement) =>
        positions.Select(placement.TransformPoint).ToImmutableArray();

    private static ImmutableArray<Vector3> TransformNormals(
        ImmutableArray<Vector3> normals,
        SseSelectedHeadpartNifPlacement placement) =>
        normals.Select(value =>
            {
                if (value == Vector3.Zero)
                    return Vector3.Zero;
                Vector3 transformed = placement.TransformDirection(value);
                float length = transformed.Length();
                return float.IsFinite(length) && length > 0.000001F
                    ? transformed / length
                    : new Vector3(float.NaN);
            })
            .ToImmutableArray();

    private static IEnumerable<float> PlacementValues(
        SseSelectedHeadpartNifPlacement placement)
    {
        yield return placement.M11;
        yield return placement.M12;
        yield return placement.M13;
        yield return placement.M21;
        yield return placement.M22;
        yield return placement.M23;
        yield return placement.M31;
        yield return placement.M32;
        yield return placement.M33;
        yield return placement.TranslationX;
        yield return placement.TranslationY;
        yield return placement.TranslationZ;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static Sha256Hash HashCamera(
        ReferenceImageViewRole role,
        params double[] values)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "reference-orthographic-camera-v2;front=positive-y;right=negative-x");
        Append(hash, role.ToWireName());
        Span<byte> bytes = stackalloc byte[8];
        foreach (double value in values)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes,
                BitConverter.DoubleToInt64Bits(value));
            hash.AppendData(bytes);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static Sha256Hash HashInput(
        Sha256Hash resourceFingerprint,
        ImmutableArray<ReferenceRenderShape> shapes,
        ImmutableArray<ReferenceRenderTexture> textures,
        ImmutableArray<ReferenceOrthographicCamera> cameras)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, resourceFingerprint.Value);
        foreach (ReferenceRenderShape shape in shapes)
        {
            Append(hash, shape.NifIdentity);
            Append(hash, shape.ShapeIdentity);
            Append(hash, shape.GeometrySha256.Value);
        }
        foreach (ReferenceRenderTexture texture in textures.OrderBy(
                     item => item.Authority.AssetPath.Value,
                     StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, texture.Authority.AssetPath.Value);
            Append(hash, texture.CanonicalRgbaSha256.Value);
        }
        foreach (ReferenceOrthographicCamera camera in cameras)
            Append(hash, camera.CameraSha256.Value);
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static Sha256Hash ZeroHash() => new(new string('0', 64));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferencePresetRenderInputResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());
}
