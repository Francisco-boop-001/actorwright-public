using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static class FaceGeomHairRegionsSupport
{
    internal const long MaximumSourceBytes = 128L * 1024L * 1024L;
    internal const int MaximumHairTintShapes = 64;
    private const uint HairTintShaderType = 6;

    internal static async ValueTask<byte[]> ReadBoundSourceAsync(
        FaceGeomHairRegionsWorkspaceBoundary boundary,
        FaceGeomHairRegionsFile authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        byte[] bytes = await boundary.ReadExactFileAsync(
            authority.Path,
            MaximumSourceBytes,
            "FaceGeom source",
            cancellationToken);
        ValidateLength(bytes.LongLength);
        if (bytes.LongLength != authority.ByteLength)
            throw Invalid(
                "The FaceGeom source length no longer matches its authority.");
        RequireHash(bytes, authority.Sha256, "FaceGeom source");
        return bytes;
    }

    internal static async ValueTask<(byte[] Bytes, FaceGeomHairRegionsFile File)>
        ReadSourceAsync(
            FaceGeomHairRegionsWorkspaceBoundary boundary,
            WorkspacePath source,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        byte[] bytes = await boundary.ReadExactFileAsync(
            source,
            MaximumSourceBytes,
            "FaceGeom source",
            cancellationToken);
        ValidateLength(bytes.LongLength);
        RequireHash(bytes, expectedSha256, "FaceGeom source");
        return (
            bytes,
            new FaceGeomHairRegionsFile(
                new WorkspacePath(Path.GetFullPath(source.Value)),
                bytes.LongLength,
                expectedSha256));
    }

    internal static FaceGeomHairRegionsAnalysis AnalyzeBytes(
        FaceGeomHairRegionsFile source,
        byte[] bytes,
        FaceGeomHairRegionPluginColorContext? pluginColorContext)
    {
        SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
        var provisional = new List<ProvisionalRegion>();
        var duplicateCounts = new Dictionary<string, int>(
            StringComparer.Ordinal);
        foreach (SseNifBlock shape in document.Blocks)
        {
            if (shape.Type is not (
                    "BSTriShape" or
                    "BSDynamicTriShape" or
                    "BSSubIndexTriShape"))
                continue;
            SseNifReference[] shaderReferences = shape.References
                .Where(reference =>
                    string.Equals(
                        reference.Kind,
                        "shader",
                        StringComparison.Ordinal))
                .ToArray();
            if (shaderReferences.Length != 1 ||
                shaderReferences[0].Target < 0)
                continue;
            SseNifBlock shader =
                document.Blocks[shaderReferences[0].Target];
            if (!string.Equals(
                shader.Type,
                    "BSLightingShaderProperty",
                    StringComparison.Ordinal) ||
                shader.Size < 16 ||
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(shader.Offset, 4)) !=
                HairTintShaderType)
                continue;

            int tintOffset = checked(shader.Offset + shader.Size - 12);
            ImmutableArray<uint> bits = ReadFloatBits(bytes, tintOffset);
            foreach (uint value in bits)
            {
                float channel = BitConverter.Int32BitsToSingle(
                    unchecked((int)value));
                if (!float.IsFinite(channel) ||
                    channel is < 0F or > 1F)
                    throw Invalid(
                        $"HairTint shader {shader.Index} has a non-canonical color channel.");
            }

            SseNifReference[] textureReferences = shader.References
                .Where(reference =>
                    string.Equals(
                        reference.Kind,
                        "textureset",
                        StringComparison.Ordinal))
                .ToArray();
            if (textureReferences.Length != 1 ||
                textureReferences[0].Target < 0 ||
                !string.Equals(
                    document.Blocks[textureReferences[0].Target].Type,
                    "BSShaderTextureSet",
                    StringComparison.Ordinal))
                throw Invalid(
                    $"HairTint shader {shader.Index} lacks one exact texture-set route.");
            SseNifBlock textureSet =
                document.Blocks[textureReferences[0].Target];
            string name = shape.Name ?? string.Empty;
            duplicateCounts.TryGetValue(name, out int duplicateOrdinal);
            duplicateCounts[name] = checked(duplicateOrdinal + 1);
            provisional.Add(
                new ProvisionalRegion(
                    $"shape:{shape.Index}:shader:{shader.Index}",
                    name,
                    duplicateOrdinal,
                    shape,
                    shaderReferences[0],
                    shader,
                    textureSet,
                    bits,
                    tintOffset));
        }

        if (provisional.Count is < 1 or > MaximumHairTintShapes)
            throw Invalid(
                $"The FaceGeom must expose 1 through {MaximumHairTintShapes} structurally recognized HairTint shapes.");

        var owners = provisional
            .GroupBy(region => region.Shader.Index)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(region => region.StructuralId)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray());
        ImmutableArray<FaceGeomHairRegionsRegion> regions = provisional
            .Select(region =>
            {
                ImmutableArray<string> groupOwners =
                    owners[region.Shader.Index];
                return new FaceGeomHairRegionsRegion(
                    region.StructuralId,
                    region.Name,
                    region.DuplicateOrdinal,
                    region.Shape.Type,
                    region.Shape.Index,
                    region.ShaderReference.Offset,
                    region.Shader.Type,
                    region.Shader.Index,
                    $"shader:{region.Shader.Index}",
                    groupOwners,
                    groupOwners
                        .Where(value =>
                            !string.Equals(
                                value,
                                region.StructuralId,
                                StringComparison.Ordinal))
                        .ToImmutableArray(),
                    region.TextureSet.Index,
                    region.TextureSet.Textures,
                    ToCanonicalColor(region.Bits),
                    region.Bits,
                    region.TintOffset,
                    12,
                    FaceGeomHairRegionRole.Preserve);
            })
            .OrderBy(region => region.ShapeBlockId)
            .ToImmutableArray();

        return new FaceGeomHairRegionsAnalysis(
            FaceGeomHairRegionSchemas.Analysis,
            source,
            regions,
            ComputeFingerprints(document),
            pluginColorContext);
    }

    internal static FaceGeomHairRegionsFingerprints ComputeFingerprints(
        SseNifDocument document)
    {
        var topology = new StringBuilder("roots:");
        foreach (int root in document.Roots)
            topology.Append(root.ToString(CultureInfo.InvariantCulture))
                .Append(',');
        topology.AppendLine();
        foreach (SseNifBlock block in document.Blocks)
        {
            topology
                .Append(block.Index.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(block.Type)
                .Append('|')
                .Append(block.Name ?? "<null>");
            foreach (SseNifReference reference in block.References)
                topology.Append('|')
                    .Append(reference.Kind)
                    .Append(':')
                    .Append(reference.Target.ToString(
                        CultureInfo.InvariantCulture));
            topology.AppendLine();
        }

        using IncrementalHash geometry =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash skinning =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash textures =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash shaders =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (SseNifBlock block in document.Blocks)
        {
            if (block.Type is
                "BSTriShape" or
                "BSDynamicTriShape" or
                "BSSubIndexTriShape")
            {
                AppendIdentity(geometry, block);
                if (block.GeometryPayload is { } payload)
                    geometry.AppendData(
                        document.Data,
                        payload.Offset,
                        payload.Length);
            }
            if (block.Type is
                "NiSkinInstance" or
                "BSDismemberSkinInstance" or
                "NiSkinData" or
                "NiSkinPartition")
            {
                AppendIdentity(skinning, block);
                skinning.AppendData(
                    document.Data,
                    block.Offset,
                    block.Size);
            }
            if (string.Equals(
                    block.Type,
                    "BSShaderTextureSet",
                    StringComparison.Ordinal))
            {
                AppendIdentity(textures, block);
                foreach (string route in block.Textures)
                    AppendUtf8(textures, route + "\n");
            }
            if (string.Equals(
                    block.Type,
                    "BSLightingShaderProperty",
                    StringComparison.Ordinal))
            {
                AppendIdentity(shaders, block);
                byte[] normalized = document.Data.AsSpan(
                        block.Offset,
                        block.Size)
                    .ToArray();
                if (block.Size >= 12 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        normalized.AsSpan(0, 4)) ==
                    HairTintShaderType)
                    normalized.AsSpan(normalized.Length - 12, 12)
                        .Clear();
                shaders.AppendData(normalized);
            }
        }

        return new FaceGeomHairRegionsFingerprints(
            HashUtf8(topology.ToString()),
            Finish(geometry),
            Finish(skinning),
            Finish(textures),
            Finish(shaders));
    }

    internal static ImmutableArray<uint> ParseColorBits(string color)
    {
        ValidateColor(color, "color");
        return Enumerable.Range(0, 3)
            .Select(index =>
            {
                byte channel = byte.Parse(
                    color.AsSpan(1 + index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                return unchecked((uint)BitConverter.SingleToInt32Bits(
                    channel / 255F));
            })
            .ToImmutableArray();
    }

    internal static void ValidateColor(string color, string role)
    {
        if (color is null ||
            color.Length != 7 ||
            color[0] != '#' ||
            color.Skip(1).Any(character =>
                character is not (
                    >= '0' and <= '9' or
                    >= 'A' and <= 'F')))
            throw Invalid(
                $"{role} must be canonical uppercase #RRGGBB.");
    }

    internal static ImmutableArray<uint> ReadFloatBits(
        byte[] bytes,
        int offset) =>
        Enumerable.Range(0, 3)
            .Select(channel =>
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(offset + channel * 4, 4)))
            .ToImmutableArray();

    internal static void WriteFloatBits(
        byte[] bytes,
        long offset,
        ImmutableArray<uint> bits)
    {
        if (bits.Length != 3 ||
            offset < 0 ||
            offset > bytes.LongLength - 12)
            throw Invalid("A proposed HairTint envelope is invalid.");
        for (int channel = 0; channel < 3; channel++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(
                    checked((int)offset + channel * 4),
                    4),
                bits[channel]);
    }

    internal static ImmutableArray<int> Diff(
        byte[] source,
        byte[] output)
    {
        if (source.Length != output.Length)
            throw Invalid("FaceGeom transaction changed file length.");
        return Enumerable.Range(0, source.Length)
            .Where(index => source[index] != output[index])
            .ToImmutableArray();
    }

    internal static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    internal static void RequireHash(
        byte[] bytes,
        Sha256Hash expected,
        string role)
    {
        Sha256Hash actual = Hash(bytes);
        if (actual != expected)
            throw Invalid(
                $"{role} hash {actual} does not match {expected}.");
    }

    internal static void ValidateInputPath(
        WorkspacePath workspaceRoot,
        WorkspacePath path) =>
        new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot)
            .RequireExistingFile(path, "FaceGeom source");

    internal static void ValidateOutputPaths(
        WorkspacePath workspaceRoot,
        WorkspacePath source,
        WorkspacePath output,
        WorkspacePath manifest)
    {
        if (string.Equals(
                source.Value,
                output.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                source.Value,
                manifest.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                output.Value,
                manifest.Value,
                StringComparison.OrdinalIgnoreCase))
            throw Invalid(
                "Source, output, and manifest paths must be distinct.");
        ValidateNewOutput(workspaceRoot, output, "output");
        ValidateNewOutput(workspaceRoot, manifest, "manifest");
    }

    internal static void ValidateNewOutput(
        WorkspacePath workspaceRoot,
        WorkspacePath path,
        string role) =>
        new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot)
            .RequireNewFile(path, role);

    private static void ValidateLength(long length)
    {
        if (length is <= 0 or > MaximumSourceBytes)
            throw Invalid(
                "FaceGeom input must contain 1 byte through 128 MiB.");
    }

    private static string ToCanonicalColor(
        ImmutableArray<uint> bits)
    {
        byte[] channels = bits
            .Select(value => checked((byte)Math.Round(
                BitConverter.Int32BitsToSingle(
                    unchecked((int)value)) * 255F,
                MidpointRounding.AwayFromZero)))
            .ToArray();
        return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
    }

    private static void AppendIdentity(
        IncrementalHash hash,
        SseNifBlock block) =>
        AppendUtf8(
            hash,
            $"{block.Index}|{block.Type}|{block.Name ?? "<null>"}\n");

    private static void AppendUtf8(
        IncrementalHash hash,
        string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static Sha256Hash HashUtf8(string value) =>
        new(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value))));

    private static Sha256Hash Finish(IncrementalHash hash) =>
        new(Convert.ToHexString(hash.GetHashAndReset()));

    private static InvalidDataException Invalid(string message) =>
        new(message);

    private sealed record ProvisionalRegion(
        string StructuralId,
        string Name,
        int DuplicateOrdinal,
        SseNifBlock Shape,
        SseNifReference ShaderReference,
        SseNifBlock Shader,
        SseNifBlock TextureSet,
        ImmutableArray<uint> Bits,
        int TintOffset);
}
