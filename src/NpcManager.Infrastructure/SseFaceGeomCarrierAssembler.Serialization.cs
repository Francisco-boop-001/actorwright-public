using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class SseFaceGeomCarrierAssembler
{
    private const uint NifVersion = 0x14020007;
    private static readonly Encoding NifEncoding = Encoding.Latin1;

    private static BuiltCarrier BuildCarrier(
        ImmutableArray<AdmittedPart> parts,
        AssetPath faceTintPath,
        SseFaceGeomCarrierSkeletonAuthority skeletonAuthority)
    {
        ImmutableArray<OutputBoneBinding> outputBones =
            BuildOutputBones(parts, skeletonAuthority);
        var outputBoneIndexesByName = outputBones.ToDictionary(
            item => item.Name, item => item.OutputBlockIndex,
            StringComparer.Ordinal);
        int faceNodeIndex = checked(1 + outputBones.Length);
        int prefixCount = checked(faceNodeIndex + 1);
        var copied = new List<CarrierOutputBlock>();
        var bindings = ImmutableArray.CreateBuilder<ShapeBinding>(parts.Length);
        foreach (AdmittedPart part in parts)
        {
            var outputBoneIndexesBySource = part.BoneNodes.ToDictionary(
                node => node.Index,
                node => outputBoneIndexesByName[node.Name!]);
            var indexMap = part.Closure.Select((block, index) =>
                    (block.Index, Output: checked(prefixCount + copied.Count + index)))
                .ToDictionary(item => item.Index, item => item.Output);
            bindings.Add(new ShapeBinding(part, indexMap[part.Shape.Index]));
            foreach (SseNifBlock block in part.Closure)
            {
                byte[] blockBytes;
                int? nameOffset;
                if (block.Index == part.TextureSet.Index &&
                    (part.Request.UsesFaceTint ||
                     !part.Request.TextureSetOverride.IsDefaultOrEmpty))
                {
                    blockBytes = BuildTextureSet(
                        part.TextureSet.Textures,
                        part.Request.TextureSetOverride,
                        part.Request.UsesFaceTint ? faceTintPath : null);
                    nameOffset = null;
                }
                else
                {
                    blockBytes = part.Document.Data.AsSpan(block.Offset, block.Size)
                        .ToArray();
                    PatchReferences(blockBytes, block, part, indexMap,
                        outputBoneIndexesBySource, faceNodeIndex);
                    if (block.Index == part.Shape.Index &&
                        !part.Request.BakedPositions.IsDefaultOrEmpty)
                        PatchShapePositions(blockBytes, block,
                            part.Request.BakedPositions);
                    if (block.Index == part.Shader.Index &&
                        part.Request.HairTintPackedRgb is { } packedRgb)
                        PatchHairTint(blockBytes, packedRgb);
                    nameOffset = block.NameIndexOffset is { } absolute
                        ? checked(absolute - block.Offset)
                        : null;
                }

                string? outputName = block.Index == part.Shape.Index
                    ? part.Request.OutputShapeName
                    : block.Name;
                copied.Add(new CarrierOutputBlock(block.Type, outputName,
                    blockBytes, nameOffset));
            }
        }

        int[] shapeIndexes = bindings.Select(binding => binding.OutputBlockIndex)
            .ToArray();
        int[] rootChildren = outputBones
            .Select(bone => bone.OutputBlockIndex)
            .Append(faceNodeIndex)
            .ToArray();
        var standard = new List<CarrierOutputBlock>(prefixCount)
        {
            Node("BSFadeNode", null, Vector3.Zero, rootChildren)
        };
        standard.AddRange(outputBones.Select(bone =>
            Node("NiNode", bone.Name, bone.Translation, [])));
        standard.Add(Node("NiNode", FaceNodeName, Vector3.Zero, shapeIndexes));
        return new BuiltCarrier(standard.Concat(copied).ToImmutableArray(),
            outputBones, faceNodeIndex, bindings.MoveToImmutable());
    }

    private static void PatchReferences(
        byte[] bytes,
        SseNifBlock block,
        AdmittedPart part,
        Dictionary<int, int> indexMap,
        Dictionary<int, int> outputBoneIndexesBySource,
        int faceNodeIndex)
    {
        foreach (SseNifReference reference in block.References)
        {
            int relative = checked(reference.Offset - block.Offset);
            if (reference.Offset < block.Offset || relative > bytes.Length - sizeof(int))
                throw Invalid($"Source block {block.Index} reference offset escaped its block.");
            int target = reference.Target switch
            {
                < 0 => -1,
                var value when value == part.Root.Index => faceNodeIndex,
                var value when outputBoneIndexesBySource.TryGetValue(value, out int mappedBone) => mappedBone,
                var value when indexMap.TryGetValue(value, out int mapped) => mapped,
                _ => throw Invalid(
                    $"Source block {block.Index} reference target {reference.Target} is outside the admitted closure.")
            };
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(relative, sizeof(int)), target);
        }
    }

    private static ImmutableArray<OutputBoneBinding> BuildOutputBones(
        ImmutableArray<AdmittedPart> parts,
        SseFaceGeomCarrierSkeletonAuthority skeletonAuthority)
    {
        var output = ImmutableArray.CreateBuilder<OutputBoneBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddBone(SseNifDocument document, SseNifBlock node)
        {
            string name = node.Name!;
            if (!seen.Add(name)) return;
            output.Add(new OutputBoneBinding(
                name,
                checked(output.Count + 1),
                skeletonAuthority switch
                {
                    SseFaceGeomCarrierSkeletonAuthority
                        .SourceModelWorldTranslations =>
                        ReadNodeWorldTranslation(document, node),
                    SseFaceGeomCarrierSkeletonAuthority
                        .IdentityFaceGenBones => Vector3.Zero,
                    _ => throw Invalid(
                        $"Unsupported carrier skeleton authority '{skeletonAuthority}'.")
                }));
        }

        AdmittedPart transformAuthority = parts[0];
        AddBone(transformAuthority.Document, transformAuthority.HeadNode);
        AdmittedPart spineAuthority = parts.FirstOrDefault(part =>
            part.SpineNode is not null) ?? throw Invalid(
            "At least one selected headpart source must carry the standard spine-node transform.");
        AddBone(spineAuthority.Document, spineAuthority.SpineNode!);
        foreach (AdmittedPart part in parts)
            foreach (SseNifBlock bone in part.BoneNodes)
                AddBone(part.Document, bone);
        return output.ToImmutable();
    }

    private static CarrierOutputBlock Node(
        string type,
        string? name,
        Vector3 translation,
        int[] children)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, NifEncoding, leaveOpen: true);
        writer.Write(uint.MaxValue);
        writer.Write(0u);
        writer.Write(-1);
        writer.Write(14u);
        writer.Write(translation.X);
        writer.Write(translation.Y);
        writer.Write(translation.Z);
        WriteIdentityRotation(writer);
        writer.Write(1f);
        writer.Write(-1);
        writer.Write(checked((uint)children.Length));
        foreach (int child in children) writer.Write(child);
        writer.Write(0u);
        return new CarrierOutputBlock(type, name, stream.ToArray(),
            NameIndexOffset: 0);
    }

    private static void WriteIdentityRotation(BinaryWriter writer)
    {
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
    }

    private static Vector3 ReadNodeTranslation(
        SseNifDocument document,
        SseNifBlock block)
    {
        ReadOnlySpan<byte> bytes = document.Data.AsSpan(block.Offset, block.Size);
        if (block.Type != "NiNode" || bytes.Length < 80 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != 0)
            throw Invalid($"Bone node '{block.Name}' is not an admitted no-extra NiNode transform carrier.");
        return new Vector3(ReadSingle(bytes, 16), ReadSingle(bytes, 20),
            ReadSingle(bytes, 24));
    }

    private static Vector3 ReadNodeWorldTranslation(
        SseNifDocument document,
        SseNifBlock block)
    {
        var memo = new Dictionary<int, Vector3>();
        var visiting = new HashSet<int>();
        return ReadNodeWorldTranslation(document, block, memo, visiting);
    }

    private static Vector3 ReadNodeWorldTranslation(
        SseNifDocument document,
        SseNifBlock block,
        Dictionary<int, Vector3> memo,
        HashSet<int> visiting)
    {
        if (memo.TryGetValue(block.Index, out Vector3 cached)) return cached;
        if (document.Roots.Contains(block.Index))
        {
            memo.Add(block.Index, Vector3.Zero);
            return Vector3.Zero;
        }
        if (!visiting.Add(block.Index))
            throw Invalid($"Node '{block.Name}' participates in a child-cycle.");
        Vector3 local = ReadNodeTranslation(document, block);
        SseNifBlock[] parents = document.Blocks
            .Where(candidate => candidate.Type is "NiNode" or "BSFadeNode")
            .Where(candidate => candidate.References.Any(reference =>
                reference.Kind == "child" && reference.Target == block.Index))
            .ToArray();
        Vector3 value = parents.Length switch
        {
            0 => local,
            1 => local + ReadNodeWorldTranslation(
                document, parents[0], memo, visiting),
            _ => throw Invalid($"Node '{block.Name}' has multiple parent nodes.")
        };
        visiting.Remove(block.Index);
        memo.Add(block.Index, value);
        return value;
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(offset, sizeof(float))));

    private static byte[] BuildTextureSet(
        ImmutableArray<string> sourceTextures,
        ImmutableArray<string> textureSetOverride,
        AssetPath? faceTintPath)
    {
        string[] textures = sourceTextures.ToArray();
        if (!textureSetOverride.IsDefaultOrEmpty)
        {
            if (textureSetOverride.Length != 8 || textures.Length < 8)
                throw Invalid(
                    "A TXST texture override requires exactly eight routes and at least eight provider slots.");
            for (int slot = 0; slot < 8; slot++)
                textures[slot] = textureSetOverride[slot];
        }
        if (faceTintPath is not null && textures.Length <= 6)
            throw Invalid(
                "The FaceTint-owning head texture set must expose Skyrim texture slot 6.");
        if (faceTintPath is { } tintPath)
            textures[6] = NifTexturePath(tintPath);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, NifEncoding, leaveOpen: true);
        writer.Write(checked((uint)textures.Length));
        foreach (string texture in textures)
            WriteSizedString(writer, texture, 4096, "texture path");
        return stream.ToArray();
    }

    private static void PatchHairTint(byte[] shaderBytes, uint packedRgb)
    {
        if (shaderBytes.Length < 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(shaderBytes) != 6)
            throw Invalid("The requested hair tint does not target one shader-type-6 envelope.");
        int offset = shaderBytes.Length - 12;
        byte red = checked((byte)((packedRgb >> 16) & 0xFF));
        byte green = checked((byte)((packedRgb >> 8) & 0xFF));
        byte blue = checked((byte)(packedRgb & 0xFF));
        BinaryPrimitives.WriteSingleLittleEndian(shaderBytes.AsSpan(offset, 4), red / 255F);
        BinaryPrimitives.WriteSingleLittleEndian(shaderBytes.AsSpan(offset + 4, 4), green / 255F);
        BinaryPrimitives.WriteSingleLittleEndian(shaderBytes.AsSpan(offset + 8, 4), blue / 255F);
    }

    private static byte[] SerializeCarrier(
        ImmutableArray<CarrierOutputBlock> blocks)
    {
        var names = ImmutableArray.CreateBuilder<string>();
        var nameIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CarrierOutputBlock block in blocks)
        {
            if (block.Name is null || nameIndexes.ContainsKey(block.Name)) continue;
            nameIndexes.Add(block.Name, names.Count);
            names.Add(block.Name);
        }

        foreach (CarrierOutputBlock block in blocks)
        {
            if (block.NameIndexOffset is not { } offset) continue;
            if (offset < 0 || offset > block.Bytes.Length - sizeof(uint))
                throw Invalid($"Output block '{block.Type}' has an invalid name-index offset.");
            uint index = block.Name is null
                ? uint.MaxValue
                : checked((uint)nameIndexes[block.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(
                block.Bytes.AsSpan(offset, sizeof(uint)), index);
        }

        var typeNames = ImmutableArray.CreateBuilder<string>();
        var typeIndexes = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (CarrierOutputBlock block in blocks)
        {
            if (typeIndexes.ContainsKey(block.Type)) continue;
            ushort index = checked((ushort)typeNames.Count);
            typeIndexes.Add(block.Type, index);
            typeNames.Add(block.Type);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, NifEncoding, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        writer.Write(NifVersion);
        writer.Write((byte)1);
        writer.Write(12u);
        writer.Write(checked((uint)blocks.Length));
        writer.Write(100u);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(checked((ushort)typeNames.Count));
        foreach (string typeName in typeNames)
            WriteSizedString(writer, typeName, 256, "block type");
        foreach (CarrierOutputBlock block in blocks)
            writer.Write(typeIndexes[block.Type]);
        foreach (CarrierOutputBlock block in blocks)
            writer.Write(checked((uint)block.Bytes.Length));
        writer.Write(checked((uint)names.Count));
        uint maximumNameBytes = names.Count == 0
            ? 0
            : checked((uint)names.Max(name => NifEncoding.GetByteCount(name)));
        writer.Write(maximumNameBytes);
        foreach (string name in names)
            WriteSizedString(writer, name, 1024 * 1024, "name");
        writer.Write(0u);
        foreach (CarrierOutputBlock block in blocks) writer.Write(block.Bytes);
        writer.Write(1u);
        writer.Write(0);
        return stream.ToArray();
    }

    private static void WriteSizedString(
        BinaryWriter writer,
        string value,
        int maximumBytes,
        string role)
    {
        byte[] bytes = NifEncoding.GetBytes(value);
        if (bytes.Length > maximumBytes ||
            !string.Equals(NifEncoding.GetString(bytes), value,
                StringComparison.Ordinal))
            throw Invalid($"{role} cannot be represented in the bounded Latin-1 NIF field.");
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static string NifTexturePath(AssetPath path) =>
        path.Value.Replace('/', '\\');
}
