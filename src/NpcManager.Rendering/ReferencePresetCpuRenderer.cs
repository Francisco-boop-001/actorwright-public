using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// A deliberately small deterministic CPU renderer for reference-preset
/// evidence. It resolves exact texture authorities and never consults the
/// filesystem or a graphics driver.
/// </summary>
public sealed class ReferencePresetCpuRenderer : IReferencePresetCpuRenderer
{
    private const int MaximumDimension = 4096;
    private static readonly byte[] Background = [32, 32, 32, 255];
    private static readonly Sha256Hash RendererIdentity =
        Hash(Encoding.UTF8.GetBytes(
            "npc-manager-reference-cpu-raster-v3;front=positive-y;right=negative-x;sample=center;uv=clamp-nearest;light=two-sided-0.35+0.65;alpha=nif-test-cutoff+source-over;png=zlib"));
    private readonly Sha256Hash identity = RendererIdentity;

    public ReferencePresetCpuRenderResult Render(
        ReferencePresetCpuRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        Dictionary<string, ReferenceRenderTexture> textures =
            ResolveTextures(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        int pixelCount = checked(request.Width * request.Height);
        byte[] rgba = new byte[checked(pixelCount * 4)];
        double[] depth = new double[pixelCount];
        string?[] tieKeys = new string?[pixelCount];
        for (var index = 0; index < pixelCount; index++)
        {
            int offset = index * 4;
            Background.CopyTo(rgba, offset);
            depth[index] = double.PositiveInfinity;
        }

        ImmutableArray<RasterTriangle> triangles = BuildTriangles(
            request, textures, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);
        foreach (RasterTriangle triangle in triangles)
            Rasterize(triangle, request.Width, request.Height,
                rgba, depth, tieKeys);

        byte[] png = EncodeCanonicalPng(
            request.Width, request.Height, rgba);
        ImmutableArray<byte> immutableRgba = ImmutableArray.CreateRange(rgba);
        ImmutableArray<byte> immutablePng = ImmutableArray.CreateRange(png);
        return new ReferencePresetCpuRenderResult(
            true,
            request.Width,
            request.Height,
            immutableRgba,
            immutablePng,
            Hash(png),
            identity,
            HashGeometry(request.Shapes),
            HashTextureSet(request.Textures),
            HashTintSet(request.Shapes),
            diagnostics.ToImmutable());
    }

    private static void Validate(
        ReferencePresetCpuRenderRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Width <= 0 || request.Height <= 0 ||
            request.Width > MaximumDimension ||
            request.Height > MaximumDimension)
        {
            diagnostics.Add(Error("reference-render-dimensions",
                $"Render dimensions must be within 1..{MaximumDimension}."));
        }
        if (request.Shapes.IsDefault || request.Shapes.IsEmpty)
        {
            diagnostics.Add(Error("reference-render-shapes",
                "At least one explicit render shape is required."));
        }
        if (request.Textures.IsDefault)
        {
            diagnostics.Add(Error("reference-render-textures",
                "The render texture set must be explicit."));
        }
        if (!ReferenceOrthographicProjection.TryGetBasis(
                request.Camera, out _, out _, out _))
        {
            diagnostics.Add(Error("reference-render-camera",
                "The orthographic camera is invalid or unsupported."));
        }

        foreach (ReferenceRenderTexture texture in request.Textures)
        {
            long expected;
            try
            {
                expected = checked((long)texture.Width *
                                   texture.Height * 4);
            }
            catch (OverflowException)
            {
                expected = -1;
            }
            if (texture.Width <= 0 || texture.Height <= 0 ||
                texture.CanonicalRgba.Length != expected ||
                Hash(texture.CanonicalRgba.AsSpan()) !=
                texture.CanonicalRgbaSha256)
            {
                diagnostics.Add(Error("reference-render-texture-bytes",
                    $"Texture '{texture.Authority.AssetPath}' has invalid canonical pixels."));
            }
        }

        foreach (ReferenceRenderShape shape in request.Shapes)
        {
            int vertexCount = shape.Positions.Length;
            int triangleCount = shape.TriangleIndices.Length / 3;
            if (string.IsNullOrWhiteSpace(shape.NifIdentity) ||
                string.IsNullOrWhiteSpace(shape.ShapeIdentity) ||
                vertexCount == 0 ||
                shape.TriangleIndices.IsDefaultOrEmpty ||
                shape.TriangleIndices.Length % 3 != 0 ||
                shape.TriangleIndices.Any(index =>
                    index < 0 || index >= vertexCount) ||
                shape.TextureCoordinates.Length != vertexCount ||
                shape.Normals.Length is not 0 &&
                shape.Normals.Length != vertexCount ||
                shape.TriangleMaterialOrdinals.Length != triangleCount ||
                shape.Materials.IsDefaultOrEmpty ||
                shape.TriangleMaterialOrdinals.Any(ordinal =>
                    ordinal < 0 || ordinal >= shape.Materials.Length) ||
                shape.Positions.Any(value => !IsFinite(value)) ||
                shape.TextureCoordinates.Any(value => !IsFinite(value)) ||
                shape.Normals.Any(value => !IsFinite(value)))
            {
                diagnostics.Add(Error("reference-render-geometry",
                    $"Shape '{shape.NifIdentity}/{shape.ShapeIdentity}' has invalid geometry or material bindings."));
            }
        }
    }

    private static Dictionary<string, ReferenceRenderTexture> ResolveTextures(
        ReferencePresetCpuRenderRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, List<ReferenceRenderTexture>> byPath =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ReferenceRenderTexture texture in request.Textures)
        {
            if (!byPath.TryGetValue(texture.Authority.AssetPath.Value,
                    out List<ReferenceRenderTexture>? candidates))
            {
                candidates = [];
                byPath.Add(texture.Authority.AssetPath.Value, candidates);
            }
            candidates.Add(texture);
        }

        Dictionary<string, ReferenceRenderTexture> resolved =
            new(StringComparer.Ordinal);
        foreach (ReferenceRenderMaterialAuthority material in request.Shapes
                     .SelectMany(shape => shape.Materials))
        {
            SkyrimAssetAuthority expected = material.Diffuse;
            if (!byPath.TryGetValue(expected.AssetPath.Value,
                    out List<ReferenceRenderTexture>? candidates))
            {
                diagnostics.Add(Error("reference-render-texture-missing",
                    $"Material '{material.MaterialIdentity}' requires absent texture '{expected.AssetPath}'."));
                continue;
            }

            ReferenceRenderTexture[] exact = candidates.Where(item =>
                    SameAuthority(item.Authority, expected))
                .ToArray();
            if (exact.Length != 1)
            {
                diagnostics.Add(Error("reference-render-texture-authority",
                    $"Material '{material.MaterialIdentity}' does not resolve to exactly one texture with the reviewed provider and content hashes."));
                continue;
            }
            resolved[MaterialKey(material)] = exact[0];
        }
        return resolved;
    }

    private static ImmutableArray<RasterTriangle> BuildTriangles(
        ReferencePresetCpuRenderRequest request,
        Dictionary<string, ReferenceRenderTexture> textures,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var triangles = ImmutableArray.CreateBuilder<RasterTriangle>();
        foreach (ReferenceRenderShape shape in request.Shapes
                     .OrderBy(item => item.NifIdentity, StringComparer.Ordinal)
                     .ThenBy(item => item.ShapeIdentity, StringComparer.Ordinal))
        {
            for (var offset = 0;
                 offset < shape.TriangleIndices.Length;
                 offset += 3)
            {
                int triangleOrdinal = offset / 3;
                int a = shape.TriangleIndices[offset];
                int b = shape.TriangleIndices[offset + 1];
                int c = shape.TriangleIndices[offset + 2];
                ReferenceRenderMaterialAuthority material =
                    shape.Materials[
                        shape.TriangleMaterialOrdinals[triangleOrdinal]];
                if (!textures.TryGetValue(MaterialKey(material),
                        out ReferenceRenderTexture? texture))
                {
                    diagnostics.Add(Error("reference-render-texture-missing",
                        $"Triangle material '{material.MaterialIdentity}' has no exact texture."));
                    continue;
                }
                if (!TryVertex(request.Camera, shape, a, out RasterVertex va) ||
                    !TryVertex(request.Camera, shape, b, out RasterVertex vb) ||
                    !TryVertex(request.Camera, shape, c, out RasterVertex vc))
                {
                    diagnostics.Add(Error("reference-render-projection",
                        $"Shape '{shape.NifIdentity}/{shape.ShapeIdentity}' contains an unprojectable triangle."));
                    continue;
                }

                int[] identity = [a, b, c];
                Array.Sort(identity);
                string tieKey = string.Join('\0',
                    shape.NifIdentity,
                    shape.ShapeIdentity,
                    material.MaterialIdentity,
                    identity[0].ToString("D10",
                        System.Globalization.CultureInfo.InvariantCulture),
                    identity[1].ToString("D10",
                        System.Globalization.CultureInfo.InvariantCulture),
                    identity[2].ToString("D10",
                        System.Globalization.CultureInfo.InvariantCulture));
                triangles.Add(new RasterTriangle(
                    va,
                    vb,
                    vc,
                    texture,
                    material.TintArgb,
                    material.AlphaTestEnabled,
                    material.AlphaTestThreshold,
                    tieKey));
            }
        }

        return triangles.OrderBy(item => item.TieKey,
                StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool TryVertex(
        ReferenceOrthographicCamera camera,
        ReferenceRenderShape shape,
        int index,
        out RasterVertex vertex)
    {
        vertex = default;
        if (!ReferenceOrthographicProjection.TryProject(
                camera, shape.Positions[index],
                out double x, out double y, out double depth))
            return false;
        Vector3 normal = shape.Normals.IsEmpty
            ? Vector3.Zero
            : shape.Normals[index];
        vertex = new RasterVertex(
            x, y, depth, shape.TextureCoordinates[index], normal);
        return true;
    }

    private static void Rasterize(
        RasterTriangle triangle,
        int width,
        int height,
        byte[] rgba,
        double[] depths,
        string?[] tieKeys)
    {
        double ax = triangle.A.X * width;
        double ay = triangle.A.Y * height;
        double bx = triangle.B.X * width;
        double by = triangle.B.Y * height;
        double cx = triangle.C.X * width;
        double cy = triangle.C.Y * height;
        double area = Edge(ax, ay, bx, by, cx, cy);
        if (!double.IsFinite(area) || Math.Abs(area) <= 1E-12)
            return;

        int minimumX = Math.Clamp(
            (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) - 0.5),
            0, width - 1);
        int maximumX = Math.Clamp(
            (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) - 0.5),
            0, width - 1);
        int minimumY = Math.Clamp(
            (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)) - 0.5),
            0, height - 1);
        int maximumY = Math.Clamp(
            (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)) - 0.5),
            0, height - 1);
        const double edgeEpsilon = 1E-10;
        for (int y = minimumY; y <= maximumY; y++)
        {
            double py = y + 0.5;
            for (int x = minimumX; x <= maximumX; x++)
            {
                double px = x + 0.5;
                double wa = Edge(bx, by, cx, cy, px, py) / area;
                double wb = Edge(cx, cy, ax, ay, px, py) / area;
                double wc = 1.0 - wa - wb;
                if (wa < -edgeEpsilon ||
                    wb < -edgeEpsilon ||
                    wc < -edgeEpsilon)
                    continue;

                double z = wa * triangle.A.Depth +
                           wb * triangle.B.Depth +
                           wc * triangle.C.Depth;
                int pixel = y * width + x;
                bool sameDepth = Math.Abs(z - depths[pixel]) <= 1E-10;
                if (z > depths[pixel] + 1E-10 ||
                    sameDepth &&
                    string.CompareOrdinal(triangle.TieKey,
                        tieKeys[pixel]) >= 0)
                    continue;

                Vector2 uv = triangle.A.Uv * (float)wa +
                             triangle.B.Uv * (float)wb +
                             triangle.C.Uv * (float)wc;
                Vector3 normal = triangle.A.Normal * (float)wa +
                                 triangle.B.Normal * (float)wb +
                                 triangle.C.Normal * (float)wc;
                Sample(triangle.Texture, uv,
                    out byte red, out byte green,
                    out byte blue, out byte alpha);
                byte tintAlpha = (byte)(triangle.TintArgb >> 24);
                byte effectiveAlpha = Multiply(
                    alpha, tintAlpha, 1);
                if (effectiveAlpha == 0 ||
                    triangle.AlphaTestEnabled &&
                    effectiveAlpha < triangle.AlphaTestThreshold)
                {
                    continue;
                }
                float intensity = Lighting(normal);
                uint tint = triangle.TintArgb;
                byte tintRed = (byte)(tint >> 16);
                byte tintGreen = (byte)(tint >> 8);
                byte tintBlue = (byte)tint;
                int output = pixel * 4;
                byte litRed = Multiply(red, tintRed, intensity);
                byte litGreen = Multiply(green, tintGreen, intensity);
                byte litBlue = Multiply(blue, tintBlue, intensity);
                if (!triangle.AlphaTestEnabled &&
                    effectiveAlpha < byte.MaxValue)
                {
                    rgba[output] = Blend(
                        litRed, rgba[output], effectiveAlpha);
                    rgba[output + 1] = Blend(
                        litGreen, rgba[output + 1], effectiveAlpha);
                    rgba[output + 2] = Blend(
                        litBlue, rgba[output + 2], effectiveAlpha);
                }
                else
                {
                    rgba[output] = litRed;
                    rgba[output + 1] = litGreen;
                    rgba[output + 2] = litBlue;
                }
                rgba[output + 3] = byte.MaxValue;
                depths[pixel] = z;
                tieKeys[pixel] = triangle.TieKey;
            }
        }
    }

    private static void Sample(
        ReferenceRenderTexture texture,
        Vector2 uv,
        out byte red,
        out byte green,
        out byte blue,
        out byte alpha)
    {
        float u = Math.Clamp(uv.X, 0, 1);
        float v = Math.Clamp(uv.Y, 0, 1);
        int x = Math.Clamp(
            (int)MathF.Floor(u * (texture.Width - 1) + 0.5F),
            0, texture.Width - 1);
        int y = Math.Clamp(
            (int)MathF.Floor(v * (texture.Height - 1) + 0.5F),
            0, texture.Height - 1);
        int offset = (y * texture.Width + x) * 4;
        red = texture.CanonicalRgba[offset];
        green = texture.CanonicalRgba[offset + 1];
        blue = texture.CanonicalRgba[offset + 2];
        alpha = texture.CanonicalRgba[offset + 3];
    }

    private static float Lighting(Vector3 normal)
    {
        if (normal.LengthSquared() <= 1E-12F)
            return 1;
        Vector3 normalized = Vector3.Normalize(normal);
        float diffuse = MathF.Abs(Vector3.Dot(
            normalized, Vector3.Normalize(new Vector3(0.2F, -1, 0.4F))));
        return 0.35F + 0.65F * diffuse;
    }

    private static byte Multiply(byte source, byte tint, float lighting)
    {
        double value = source * (tint / 255.0) * lighting;
        return (byte)Math.Clamp(
            (int)Math.Round(value, MidpointRounding.AwayFromZero),
            0, 255);
    }

    private static byte Blend(
        byte source,
        byte destination,
        byte sourceAlpha)
    {
        int inverse = byte.MaxValue - sourceAlpha;
        int value =
            source * sourceAlpha +
            destination * inverse;
        return (byte)((value + 127) / byte.MaxValue);
    }

    private static double Edge(
        double ax, double ay,
        double bx, double by,
        double px, double py) =>
        (px - ax) * (by - ay) -
        (py - ay) * (bx - ax);

    internal static byte[] EncodeCanonicalPng(
        int width,
        int height,
        ReadOnlySpan<byte> rgba)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        byte[] scanlines = new byte[checked((width * 4 + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            int target = y * (width * 4 + 1);
            scanlines[target] = 0;
            rgba.Slice(y * width * 4, width * 4)
                .CopyTo(scanlines.AsSpan(target + 1));
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed, CompressionLevel.SmallestSize,
                   leaveOpen: true))
            zlib.Write(scanlines);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(
        Stream output,
        string type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput.AsSpan(typeBytes.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB8_8320 & mask);
            }
        }
        return ~crc;
    }

    private static Sha256Hash HashGeometry(
        ImmutableArray<ReferenceRenderShape> shapes)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[4];
        foreach (ReferenceRenderShape shape in shapes
                     .OrderBy(item => item.NifIdentity,
                         StringComparer.Ordinal)
                     .ThenBy(item => item.ShapeIdentity,
                         StringComparer.Ordinal))
        {
            Append(hash, shape.NifIdentity);
            Append(hash, shape.ShapeIdentity);
            foreach (Vector3 value in shape.Positions)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes,
                    BitConverter.SingleToInt32Bits(value.X));
                hash.AppendData(bytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes,
                    BitConverter.SingleToInt32Bits(value.Y));
                hash.AppendData(bytes);
                BinaryPrimitives.WriteInt32LittleEndian(bytes,
                    BitConverter.SingleToInt32Bits(value.Z));
                hash.AppendData(bytes);
            }
            foreach (int value in shape.TriangleIndices)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static Sha256Hash HashTextureSet(
        ImmutableArray<ReferenceRenderTexture> textures)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ReferenceRenderTexture texture in textures
                     .OrderBy(item => item.Authority.AssetPath.Value,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Authority.ContentSha256.Value,
                         StringComparer.Ordinal))
        {
            Append(hash, texture.Authority.AssetPath.Value.ToLowerInvariant());
            Append(hash, texture.Authority.ContentSha256.Value);
            Append(hash, texture.CanonicalRgbaSha256.Value);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static Sha256Hash HashTintSet(
        ImmutableArray<ReferenceRenderShape> shapes)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[4];
        foreach (ReferenceRenderMaterialAuthority material in shapes
                     .OrderBy(item => item.NifIdentity,
                         StringComparer.Ordinal)
                     .ThenBy(item => item.ShapeIdentity,
                         StringComparer.Ordinal)
                     .SelectMany(item => item.Materials))
        {
            Append(hash, material.MaterialIdentity);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes, material.TintArgb);
            hash.AppendData(bytes);
            hash.AppendData(
            [
                material.AlphaTestEnabled ? (byte)1 : (byte)0,
                material.AlphaTestThreshold
            ]);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static bool SameAuthority(
        SkyrimAssetAuthority actual,
        SkyrimAssetAuthority expected) =>
        actual.AssetPath.Value.Equals(
            expected.AssetPath.Value,
            StringComparison.OrdinalIgnoreCase) &&
        actual.ContentSha256 == expected.ContentSha256 &&
        actual.ProviderSha256 == expected.ProviderSha256 &&
        actual.ProviderId.Equals(
            expected.ProviderId, StringComparison.Ordinal) &&
        actual.ProviderKind == expected.ProviderKind &&
        actual.ContentLength == expected.ContentLength;

    private static string MaterialKey(
        ReferenceRenderMaterialAuthority material) =>
        string.Join('\0',
            material.MaterialIdentity,
            material.Diffuse.AssetPath.Value.ToLowerInvariant(),
            material.Diffuse.ContentSha256.Value,
            material.Diffuse.ProviderSha256.Value);

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private ReferencePresetCpuRenderResult Refused(
        ReferencePresetCpuRenderRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, request.Width, request.Height, [], [], null,
            identity, null, null, null,
            diagnostics.ToImmutable());

    private readonly record struct RasterVertex(
        double X,
        double Y,
        double Depth,
        Vector2 Uv,
        Vector3 Normal);

    private sealed record RasterTriangle(
        RasterVertex A,
        RasterVertex B,
        RasterVertex C,
        ReferenceRenderTexture Texture,
        uint TintArgb,
        bool AlphaTestEnabled,
        byte AlphaTestThreshold,
        string TieKey);
}
