using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    // Compose the historical provider graph from real synthetic assembler payloads.
    // Geometry, skin weights, partition data, shader bytes and node transforms stay intact.
    internal static byte[] BuildSyntheticLegacyCarrier(byte[] sourceBytes)
    {
        var source = SseFaceGeomCarrierCodec.Parse(sourceBytes);
        var shapes = source.Blocks.Where(b => b.Type == "BSDynamicTriShape").ToArray();
        var nonHead = shapes.Where(b => !b.Name!.Contains("Head", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(source.Blocks.Length == 46 && shapes.Length == 7 && nonHead.Length == 6 &&
            source.Blocks.Count(b => b.Type == "BSDismemberSkinInstance") == 7 &&
            source.Blocks.Count(b => b.Type == "NiNode") == 3,
            "Synthetic legacy composition requires the actual seven-part, 46-block assembler carrier.");
        int Target(SseNifBlock block, string kind) => block.References.Single(r => r.Kind == kind).Target;
        byte[] Bytes(SseNifBlock block) => sourceBytes.AsSpan(block.Offset, block.Size).ToArray();
        int keptTexture = Target(source.Blocks[Target(nonHead[0], "shader")], "textureset");
        int removedTexture = Target(source.Blocks[Target(nonHead[1], "shader")], "textureset");
        Require(keptTexture != removedTexture && Bytes(source.Blocks[keptTexture]).AsSpan().SequenceEqual(Bytes(source.Blocks[removedTexture])),
            "Only byte-identical non-head texture sets may share the historical two-shader route.");
        var retained = source.Blocks.Where(b => b.Index != removedTexture).ToArray();
        var indexes = retained.Select((block, index) => (block.Index, NewIndex: index)).ToDictionary(p => p.Index, p => p.NewIndex);
        int Remap(int index) => index < 0 ? -1 : indexes[index == removedTexture ? keptTexture : index];
        var alphaIndexes = nonHead.Select((shape, index) => (shape.Index, Alpha: retained.Length + index))
            .ToDictionary(p => p.Index, p => p.Alpha);
        var ordinarySkinIndexes = nonHead.Take(3).Select(shape => Target(shape, "skin")).ToHashSet();
        var output = new List<(string Type, byte[] Bytes)>();
        foreach (var block in retained)
        {
            byte[] bytes = Bytes(block);
            foreach (var reference in block.References)
            {
                int target = reference.Kind == "alpha" && alphaIndexes.TryGetValue(block.Index, out int alpha)
                    ? alpha : Remap(reference.Target);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(reference.Offset - block.Offset, 4), target);
            }
            string type = block.Type;
            if (ordinarySkinIndexes.Contains(block.Index))
            {
                int boneCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
                int skinEnd = checked(16 + boneCount * 4);
                int partitionCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(skinEnd, 4)));
                Require(bytes.Length == skinEnd + 4 + partitionCount * 4, "Synthetic skin partition tail is not exact.");
                bytes = bytes[..skinEnd];
                type = "NiSkinInstance";
            }
            output.Add((type, bytes));
        }
        foreach (var _ in nonHead)
        {
            byte[] alpha = new byte[15];
            BinaryPrimitives.WriteInt32LittleEndian(alpha, -1); // unnamed NiObjectNET
            BinaryPrimitives.WriteInt32LittleEndian(alpha.AsSpan(8), -1); // no controller
            BinaryPrimitives.WriteUInt16LittleEndian(alpha.AsSpan(12), 4845);
            alpha[14] = 128;
            output.Add(("NiAlphaProperty", alpha));
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true))
        {
            void Text(string value) { byte[] bytes = Encoding.Latin1.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes); }
            writer.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            writer.Write(0x14020007u); writer.Write((byte)1); writer.Write(12u); writer.Write((uint)output.Count); writer.Write(100u);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
            string[] types = output.Select(b => b.Type).Distinct(StringComparer.Ordinal).ToArray();
            writer.Write((ushort)types.Length);
            foreach (string type in types) Text(type);
            foreach (var block in output) writer.Write((ushort)Array.IndexOf(types, block.Type));
            foreach (var block in output) writer.Write((uint)block.Bytes.Length);
            writer.Write((uint)source.Strings.Length);
            writer.Write(source.Strings.IsEmpty ? 0u : (uint)source.Strings.Max(Encoding.Latin1.GetByteCount));
            foreach (string name in source.Strings) Text(name);
            writer.Write(0u);
            foreach (var block in output) writer.Write(block.Bytes);
            writer.Write((uint)source.Roots.Length);
            foreach (int root in source.Roots) writer.Write(Remap(root));
        }
        byte[] result = stream.ToArray();
        var reopened = SseFaceGeomCarrierCodec.Parse(result);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(reopened, SseFaceGeomCarrierCodec.BuildStructure(reopened), diagnostics);
        Require(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
            "Synthetic 51-block provider qualification failed: " + string.Join(";", diagnostics));
        foreach (var shape in shapes)
        {
            var actual = reopened.Blocks.Single(b => b.Type == "BSDynamicTriShape" && b.Name == shape.Name);
            var expectedGeometry = shape.GeometryPayload!;
            var actualGeometry = actual.GeometryPayload!;
            Require(sourceBytes.AsSpan(expectedGeometry.Offset, expectedGeometry.Length)
                .SequenceEqual(result.AsSpan(actualGeometry.Offset, actualGeometry.Length)), "Synthetic provider composition changed actual geometry bytes.");
            Require(SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(source, shape) ==
                SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(reopened, actual), "Synthetic provider composition changed partition topology.");
        }
        return result;
    }
}
