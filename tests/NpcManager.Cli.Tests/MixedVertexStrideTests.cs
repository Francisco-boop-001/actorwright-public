using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly ulong[] MixedDynamicDescriptors =
        [0x0044200010000044, 0x0045A00030210046, 0x0055A06030210047];
    private static readonly ulong[] MixedStaticDescriptors =
        [0x0005B0007065040A, 0x0007B0008765040B];
    private const ulong Uv2Descriptor = (0x407UL << 44) | 0x5406;
    private const ulong LandscapeDescriptor = (0x581UL << 44) | (4UL << 32) | (6UL << 36) | 7;

    internal static async Task RunMixedVertexStridesAsync()
    {
        var root = Child(new WorkspacePath(Environment.CurrentDirectory), "artifacts", "task25", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root.Value);
        var failures = new List<string>();
        var evidence = new List<object>();
        var reader = new BethesdaNifGeometryReadbackService();
        foreach (ulong dynamicDescriptor in MixedDynamicDescriptors)
        foreach (ulong staticDescriptor in MixedStaticDescriptors.Prepend(0UL))
        {
            byte[] bytes = BuildMixedStrideFixture(dynamicDescriptor, staticDescriptor);
            var path = Child(root, $"{dynamicDescriptor:X16}-{staticDescriptor:X16}.nif");
            File.WriteAllBytes(path.Value, bytes);
            var result = await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, path,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)))), CancellationToken.None);
            if (!result.Accepted || result.Document is null)
            {
                failures.Add($"{path.Value}: {string.Join(';', result.Diagnostics.Select(d => d.Code + ": " + d.Message))}");
                continue;
            }
            var dynamic = result.Document.Shapes.Single(s => s.BlockType == "BSDynamicTriShape");
            if (dynamic.VertexDescriptor != dynamicDescriptor)
                failures.Add($"Dynamic descriptor {dynamicDescriptor:X16} read as {dynamic.VertexDescriptor:X16}.");
            Require(dynamic.VertexStride == 16 && dynamic.VertexCount == 3,
                "Dynamic position lanes were conflated with the packed attribute stride.");
            var parsedFixture = SseFaceGeomCarrierCodec.Parse(bytes);
            var dynamicPartition = parsedFixture.Blocks.First(b => b.Type == "NiSkinPartition");
            Require(dynamic.TriangleTopologySha256 == new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                bytes.AsSpan(dynamicPartition.Offset, dynamicPartition.Size)))), "Dynamic partition topology hash drifted.");
            if (staticDescriptor != 0)
            {
                var shape = result.Document.Shapes.Single(s => s.BlockType == "BSTriShape");
                Require(shape.VertexDescriptor == staticDescriptor && shape.VertexStride == (int)(staticDescriptor & 15) * 4 && shape.VertexCount == 3,
                    "Static descriptor/stride readback differs from the actual fixture.");
                byte[] triangles = [0, 0, 1, 0, 2, 0];
                Require(shape.TriangleTopologySha256 == new Sha256Hash(Convert.ToHexString(SHA256.HashData(triangles))),
                    "Static triangle topology was not read independently.");
            }
            foreach (var shape in result.Document.Shapes)
                Require(shape.VertexPayloadSha256 == new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                    bytes.AsSpan((int)shape.VertexPayloadOffset, shape.VertexPayloadLength)))), "Per-shape payload hash drifted.");
            Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path.Value)), "Readback mutated its source file.");
            Record(path, result.Document);
        }
        await Check("uv2", BuildMixedStrideFixture(MixedDynamicDescriptors[1], Uv2Descriptor), true);
        await Check("landscape-eye", BuildMixedStrideFixture(MixedDynamicDescriptors[2], LandscapeDescriptor), true);
        await Check("landscape-overlap", BuildMixedStrideFixture(MixedDynamicDescriptors[2],
            LandscapeDescriptor ^ (4UL << 32) ^ (3UL << 32)), false);
        await Check("landscape-outside", BuildMixedStrideFixture(MixedDynamicDescriptors[2],
            LandscapeDescriptor ^ (4UL << 32) ^ (8UL << 32)), false);
        await Check("uv2-overlap", BuildMixedStrideFixture(MixedDynamicDescriptors[1], Uv2Descriptor ^ (5UL << 12) ^ (4UL << 12)), false);
        await Check("instance", BuildMixedStrideFixture(MixedDynamicDescriptors[1], MixedStaticDescriptors[0] | (0x200UL << 44)), false);
        await Check("dynamic-unknown", BuildMixedStrideFixture(MixedDynamicDescriptors[1] | (0x200UL << 44), 0), false);
        for (int precision = 0; precision < 2; precision++)
            await Check("fallout4-" + (precision == 0 ? "half" : "full"),
                Preview258ConsumerDefectTests.BuildFallout4TriShapeFixture(halfPrecision: precision == 0), true, GameEdition.Fallout4);
        byte[] valid = BuildMixedStrideFixture(MixedDynamicDescriptors[1], MixedStaticDescriptors[0]);
        var parsed = SseFaceGeomCarrierCodec.Parse(valid);
        var staticBlock = parsed.Blocks.Single(b => b.Type == "BSTriShape");
        byte[] badSize = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badSize.AsSpan(staticBlock.GeometryPayload!.Offset + 12), 1);
        await Check("wrong-size", badSize, false);
        byte[] particles = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(particles.AsSpan(staticBlock.Offset + staticBlock.Size - 4), 1);
        await Check("nonempty-particles", particles, false);
        await Check("truncated", valid[..^8], false);
        var wrongHashPath = Child(root, "hash-bound.nif");
        File.WriteAllBytes(wrongHashPath.Value, valid);
        var wrongHash = await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, wrongHashPath, new Sha256Hash(new string('1', 64))), CancellationToken.None);
        Require(!wrongHash.Accepted && wrongHash.Diagnostics.Any(d => d.Code == "nif-geometry-readback-hash-mismatch"), "Expected source hash was not enforced.");
        File.WriteAllText(Child(root, "readback-evidence.json").Value, JsonSerializer.Serialize(evidence));
        Require(failures.Count == 0, "Mixed descriptor readback failed:\n" + string.Join('\n', failures));
        Console.WriteLine("Exact mixed descriptors and per-shape readback verified: " + root.Value);

        async Task Check(string name, byte[] bytes, bool accepted, GameEdition edition = GameEdition.SkyrimSpecialEdition)
        {
            var path = Child(root, name + ".nif");
            File.WriteAllBytes(path.Value, bytes);
            var result = await reader.ReadAsync(new(edition, path, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)))), CancellationToken.None);
            Require(result.Accepted == accepted, name + ": " + string.Join(';', result.Diagnostics.Select(d => d.Message)));
            Require(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path.Value)), "Control read changed source bytes.");
            if (result.Document is { } document) Record(path, document);
            else evidence.Add(new { path = path.Value, edition = edition == GameEdition.Fallout4 ? "fallout4" : "skyrimse", accepted = false });
        }
        void Record(WorkspacePath path, NifGeometryReadbackDocument document) => evidence.Add(new
        {
            path = path.Value, edition = document.Edition == GameEdition.Fallout4 ? "fallout4" : "skyrimse", accepted = true,
            aggregate = document.AggregateGeometrySha256.Value,
            shapes = document.Shapes.Select(s => new { name = s.Name, descriptor = s.VertexDescriptor,
                stride = s.VertexStride, offset = s.VertexPayloadOffset, length = s.VertexPayloadLength, hash = s.VertexPayloadSha256.Value })
        });
    }

    private static byte[] BuildMixedStrideFixture(ulong dynamicDescriptor, ulong staticDescriptor)
    {
        byte[] baseline = WriteHdptTopologyModel(3);
        var source = SseFaceGeomCarrierCodec.Parse(baseline);
        var blocks = source.Blocks.Select(b => (b.Type, Bytes: baseline.AsSpan(b.Offset, b.Size).ToArray())).ToList();
        var dynamic = source.Blocks.Single(b => b.Type == "BSDynamicTriShape");
        int descriptorOffset = dynamic.GeometryPayload!.Offset - dynamic.Offset;
        BinaryPrimitives.WriteUInt64LittleEndian(blocks[dynamic.Index].Bytes.AsSpan(descriptorOffset), dynamicDescriptor);
        var partition = source.Blocks.Single(b => b.Type == "NiSkinPartition");
        blocks[partition.Index] = (partition.Type, Partition(dynamicDescriptor, false));
        var strings = source.Strings.ToList();
        if (staticDescriptor != 0)
        {
            int shapeIndex = blocks.Count;
            int skinIndex = shapeIndex + 1;
            int partitionIndex = shapeIndex + 2;
            byte[] shape = Block(w =>
            {
                byte[] prefix = blocks[dynamic.Index].Bytes.AsSpan(0, descriptorOffset).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)strings.Count);
                int skinOffset = dynamic.References.Single(r => r.Kind == "skin").Offset - dynamic.Offset;
                BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(skinOffset), skinIndex);
                w.Write(prefix); w.Write(staticDescriptor); w.Write((ushort)1); w.Write((ushort)3);
                byte[] vertices = Vertices(staticDescriptor, true);
                w.Write((uint)(vertices.Length + 6)); w.Write(vertices);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
                w.Write(0U); // exact SSE particle-data-size field
            });
            strings.Add("MixedStatic");
            var skin = source.Blocks.Single(b => b.Type == "NiSkinInstance");
            byte[] skinBytes = blocks[skin.Index].Bytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(skinBytes.AsSpan(4), partitionIndex);
            blocks.Add(("BSTriShape", shape)); blocks.Add((skin.Type, skinBytes));
            blocks.Add((partition.Type, Partition(staticDescriptor, true)));
            var root = source.Blocks[source.Roots.Single()];
            int childCountOffset = root.References.First(r => r.Kind == "child").Offset - root.Offset - 4;
            uint childCount = BinaryPrimitives.ReadUInt32LittleEndian(blocks[root.Index].Bytes.AsSpan(childCountOffset));
            blocks[root.Index] = (root.Type, Block(w =>
            {
                w.Write(blocks[root.Index].Bytes.AsSpan(0, childCountOffset));
                w.Write(childCount + 1);
                w.Write(blocks[root.Index].Bytes.AsSpan(childCountOffset + 4, (int)childCount * 4));
                w.Write(shapeIndex); w.Write(0U);
            }));
        }
        return Block(w =>
        {
            void Text(string text) { byte[] data = Encoding.Latin1.GetBytes(text); w.Write(data.Length); w.Write(data); }
            w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            w.Write(0x14020007U); w.Write((byte)1); w.Write(12U); w.Write(blocks.Count); w.Write(100U); w.Write(new byte[3]);
            string[] types = blocks.Select(b => b.Type).Distinct(StringComparer.Ordinal).ToArray();
            w.Write((ushort)types.Length); foreach (string type in types) Text(type);
            foreach (var block in blocks) w.Write((ushort)Array.IndexOf(types, block.Type));
            foreach (var block in blocks) w.Write(block.Bytes.Length);
            w.Write(strings.Count); w.Write(strings.Max(Encoding.Latin1.GetByteCount));
            foreach (string text in strings) Text(text);
            w.Write(0U); foreach (var block in blocks) w.Write(block.Bytes);
            w.Write(1U); w.Write(0);
        });

        byte[] Partition(ulong descriptor, bool positions) => Block(w =>
        {
            byte[] vertices = Vertices(descriptor, positions);
            w.Write(1U); w.Write(vertices.Length); w.Write((uint)(descriptor & 15) * 4); w.Write(descriptor); w.Write(vertices);
            byte[] tail = baseline.AsSpan(partition.Offset + 20 + 48, partition.Size - 20 - 48).ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(tail.Length - 14), descriptor);
            w.Write(tail);
        });
        static byte[] Vertices(ulong descriptor, bool positions)
        {
            int stride = (int)(descriptor & 15) * 4;
            var bytes = new byte[stride * 3];
            if (positions)
                for (int i = 0; i < 3; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * stride), i == 1 ? 1F : 0F);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * stride + 4), i == 2 ? 1F : 0F);
                }
            return bytes;
        }
        static byte[] Block(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.Latin1, true)) write(writer);
            return stream.ToArray();
        }
    }
}
