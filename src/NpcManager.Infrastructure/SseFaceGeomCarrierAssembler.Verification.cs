using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class SseFaceGeomCarrierAssembler
{
    private static ImmutableArray<SseFaceGeomCarrierAssemblyShape> VerifyCarrier(
        byte[] bytes,
        BuiltCarrier built,
        AssetPath faceTintPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SseNifDocument output = SseFaceGeomCarrierCodec.Parse(bytes);
        if (output.UserVersion != 12 || output.BethesdaStreamVersion != 100 ||
            output.Blocks.Length != built.Blocks.Length ||
            output.Roots.Length != 1 || output.Roots[0] != 0)
            throw Invalid("Reparsed carrier header, block count, or root identity drifted.");
        HashSet<int> reachable =
            SseFaceGeomCarrierCodec.FindReachableBlockIndexes(output);
        if (reachable.Count != output.Blocks.Length)
            throw Invalid("Reparsed carrier contains unreachable blocks.");
        QualifiedFaceGeomCarrierStructure structure =
            SseFaceGeomCarrierCodec.BuildStructure(output);
        SseFaceGeomCarrierCodec.Qualify(
            output,
            structure,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
            diagnostics);
        NifTextureTarget? faceTintTarget =
            SseFaceGeomCarrierCodec.FindFaceTintTarget(
                output,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                diagnostics);
        if (faceTintTarget is not null &&
            !string.Equals(faceTintTarget.OriginalPath,
                NifTexturePath(faceTintPath), StringComparison.Ordinal))
            diagnostics.Add(Error("sse-facegeom-carrier-facetint-binding",
                "The reparsed carrier FaceTint route does not match the exact requested path."));

        int[] rootChildren = built.Bones
            .Select(bone => bone.OutputBlockIndex)
            .Append(built.FaceNodeIndex)
            .ToArray();
        RequireNode(output.Blocks[0], "BSFadeNode", null, rootChildren);
        foreach (OutputBoneBinding bone in built.Bones)
            RequireNode(output.Blocks[bone.OutputBlockIndex], "NiNode",
                bone.Name, []);
        RequireNode(output.Blocks[built.FaceNodeIndex], "NiNode", FaceNodeName,
            built.Shapes.Select(shape => shape.OutputBlockIndex).ToArray());
        if (output.Blocks.Count(block =>
                block.Type is "NiNode" or "BSFadeNode") != built.Bones.Length + 2)
            throw Invalid("Reparsed carrier does not contain the exact standard and admitted extra-bone nodes.");

        var evidence = ImmutableArray.CreateBuilder<SseFaceGeomCarrierAssemblyShape>(
            built.Shapes.Length);
        foreach (ShapeBinding binding in built.Shapes)
        {
            SseNifBlock shape = output.Blocks[binding.OutputBlockIndex];
            if (shape.Type != "BSDynamicTriShape" ||
                shape.DynamicGeometry is null ||
                !string.Equals(shape.Name, binding.Part.Request.OutputShapeName,
                    StringComparison.Ordinal))
                throw Invalid($"Reparsed shape for '{binding.Part.Request.HeadPart}' lost its output identity.");
            Sha256Hash sourceTopology =
                SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(
                    binding.Part.Document, binding.Part.Shape);
            Sha256Hash outputTopology =
                SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(output,
                    shape);
            if (outputTopology != sourceTopology ||
                shape.DynamicGeometry.VertexCount !=
                binding.Part.Shape.DynamicGeometry!.VertexCount)
                throw Invalid($"Reparsed shape '{shape.Name}' changed source topology or vertex count.");
            if (!binding.Part.Request.ExpectedPackedNormalBytes.IsDefaultOrEmpty)
            {
                SsePackedNormalExtraction packedNormals =
                    SseFaceGeomCarrierCodec.ExtractPackedNormals(output,
                        shape);
                ImmutableArray<byte> expectedPackedNormals =
                    binding.Part.Request.ExpectedPackedNormalBytes;
                if (!packedNormals.PackedNormalBytes.SequenceEqual(
                        expectedPackedNormals))
                {
                    string expectedHash = Convert.ToHexString(
                        SHA256.HashData(expectedPackedNormals.AsSpan()));
                    string actualHash = Convert.ToHexString(
                        SHA256.HashData(packedNormals.PackedNormalBytes.AsSpan()));
                    int firstDifference = FirstPackedNormalDifference(
                        expectedPackedNormals,
                        packedNormals.PackedNormalBytes);
                    diagnostics.Add(Error(
                        "sse-facegeom-carrier-packed-normal-mismatch",
                        $"Reparsed shape '{shape.Name}' packed-normal bytes differ: descriptor=0x{packedNormals.VertexDescription:X16} vertex-size={packedNormals.VertexSize} expected-sha256={expectedHash} actual-sha256={actualHash} first-difference={firstDifference}."));
                }
            }
            (ImmutableArray<System.Numerics.Vector3> sourcePositions,
                Sha256Hash sourcePositionHash) = ReadShapePositions(
                    binding.Part.Document, binding.Part.Shape);
            (ImmutableArray<System.Numerics.Vector3> outputPositions,
                Sha256Hash outputPositionHash) = ReadShapePositions(output, shape);
            ImmutableArray<System.Numerics.Vector3> expectedPositions =
                binding.Part.Request.BakedPositions.IsDefaultOrEmpty
                    ? sourcePositions
                    : binding.Part.Request.BakedPositions;
            if (!outputPositions.SequenceEqual(expectedPositions))
                throw Invalid($"Reparsed shape '{shape.Name}' does not contain the exact requested baked positions.");
            bool positionsChanged = !sourcePositions.SequenceEqual(outputPositions);

            int skinIndex = SingleOutputTarget(shape, "skin");
            SseNifBlock skin = output.Blocks[skinIndex];
            var admittedBoneTargets = built.Bones
                .Select(bone => bone.OutputBlockIndex)
                .ToHashSet();
            if (SingleOutputTarget(skin, "skeletonroot") != built.FaceNodeIndex ||
                skin.References.Where(reference => reference.Kind == "bone")
                    .Any(reference => !admittedBoneTargets.Contains(reference.Target)))
                throw Invalid($"Reparsed shape '{shape.Name}' lost the standard FaceGen skeleton binding.");
            int shaderIndex = SingleOutputTarget(shape, "shader");
            int textureSetIndex = SingleOutputTarget(output.Blocks[shaderIndex],
                "textureset");
            ImmutableArray<string> actualTextures =
                output.Blocks[textureSetIndex].Textures;
            string[] expectedTextures = binding.Part.TextureSet.Textures.ToArray();
            if (!binding.Part.Request.TextureSetOverride.IsDefaultOrEmpty)
            {
                for (int slot = 0; slot < 8; slot++)
                    expectedTextures[slot] =
                        binding.Part.Request.TextureSetOverride[slot];
            }
            if (binding.Part.Request.UsesFaceTint)
                expectedTextures[6] = NifTexturePath(faceTintPath);
            if (!actualTextures.SequenceEqual(expectedTextures, StringComparer.Ordinal))
                throw Invalid($"Reparsed shape '{shape.Name}' changed its admitted texture routes.");
            if (binding.Part.Request.HairTintPackedRgb is { } expectedHairTint)
            {
                SseNifBlock outputShader = output.Blocks[shaderIndex];
                if (outputShader.Size < 16 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                        output.Data.AsSpan(outputShader.Offset, 4)) != 6 ||
                    ReadPackedHairTint(output.Data, outputShader) != expectedHairTint)
                    throw Invalid($"Reparsed shape '{shape.Name}' changed its requested HairTint RGB bits.");
            }

            evidence.Add(new SseFaceGeomCarrierAssemblyShape(
                binding.Part.Request.HeadPart,
                binding.Part.Request.SourcePath,
                binding.Part.Request.ExpectedSourceSha256,
                binding.Part.Shape.Name!,
                shape.Name!,
                shape.DynamicGeometry.VertexCount,
                outputTopology,
                sourcePositionHash,
                outputPositionHash,
                positionsChanged,
                binding.Part.Request.UsesFaceTint));
        }

        int dynamicShapeCount = output.Blocks.Count(block =>
            block.Type == "BSDynamicTriShape");
        if (dynamicShapeCount != built.Shapes.Length ||
            output.Blocks.Any(block => block.Type == "BSTriShape"))
            throw Invalid("Reparsed carrier shape count or dynamic-shape envelope drifted.");
        return evidence.MoveToImmutable();
    }

    private static uint ReadPackedHairTint(byte[] bytes, SseNifBlock shader)
    {
        int offset = checked(shader.Offset + shader.Size - 12);
        byte Channel(int channel)
        {
            float value = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(offset + channel * 4, 4));
            if (!float.IsFinite(value) || value is < 0F or > 1F)
                throw Invalid("HairTint channel is outside the canonical range.");
            int scaled = checked((int)MathF.Round(value * 255F));
            if (BitConverter.SingleToInt32Bits(value) !=
                BitConverter.SingleToInt32Bits(scaled / 255F))
                throw Invalid("HairTint channel does not use canonical channel / 255 bits.");
            return checked((byte)scaled);
        }
        return ((uint)Channel(0) << 16) |
               ((uint)Channel(1) << 8) |
               Channel(2);
    }

    private static int FirstPackedNormalDifference(
        ImmutableArray<byte> expected,
        ImmutableArray<byte> actual)
    {
        int sharedLength = Math.Min(expected.Length, actual.Length);
        for (int index = 0; index < sharedLength; index++)
            if (expected[index] != actual[index]) return index;
        return expected.Length == actual.Length ? -1 : sharedLength;
    }

    private static void RequireNode(
        SseNifBlock block,
        string type,
        string? name,
        int[] children)
    {
        int[] actualChildren = block.References
            .Where(reference => reference.Kind == "child")
            .Select(reference => reference.Target).ToArray();
        if (block.Type != type ||
            !string.Equals(block.Name, name, StringComparison.Ordinal) ||
            !actualChildren.SequenceEqual(children))
            throw Invalid($"Reparsed standard node {block.Index} does not match the carrier graph contract.");
    }

    private static int SingleOutputTarget(SseNifBlock block, string kind)
    {
        int[] targets = block.References
            .Where(reference => reference.Kind == kind)
            .Select(reference => reference.Target).ToArray();
        if (targets.Length != 1 || targets[0] < 0)
            throw Invalid($"Reparsed block {block.Index} does not have one non-null {kind} reference.");
        return targets[0];
    }
}
