using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly JsonSerializerOptions PackedNormalJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static Task TestSseFaceGeomPackedNormals()
    {
        PackedNormalFixtureManifest manifest = ReadPackedNormalFixtureManifest();
        PackedNormalFixture dynamic16 = manifest.Fixtures.Single(item =>
            item.Id == "dynamic-16");
        PackedNormalFixture dynamic24 = manifest.Fixtures.Single(item =>
            item.Id == "dynamic-24");

        (byte[] bytes16, SseNifDocument document16, SseNifBlock shape16) =
            LoadPackedNormalFixture(dynamic16);
        (byte[] bytes24, SseNifDocument document24, SseNifBlock shape24) =
            LoadPackedNormalFixture(dynamic24);

        var extracted16 = SseFaceGeomCarrierCodec.ExtractPackedNormals(
            document16, shape16);
        var extracted24 = SseFaceGeomCarrierCodec.ExtractPackedNormals(
            document24, shape24);
        AssertPackedNormalExtraction(dynamic16, bytes16, extracted16);
        AssertPackedNormalExtraction(dynamic24, bytes24, extracted24);
        try
        {
            _ = SseFaceGeomCarrierCodec.ExtractPackedNormals(
                document16, shape24);
            Assert(false,
                "Packed-normal extraction accepted a shape owned by another parsed document.");
        }
        catch (InvalidDataException exception)
        {
            Assert(exception.Message.Contains("owned by the parsed NIF",
                    StringComparison.Ordinal),
                "Foreign-shape packed-normal refusal lost its ownership diagnostic.");
        }

        var fixtureReader = new SseSelectedHeadpartNifGeometryReader();
        AssertSelectedPackedNormalFixture(fixtureReader, dynamic16, bytes16);
        AssertSelectedPackedNormalFixture(fixtureReader, dynamic24, bytes24);
        PackedNormalFixture noNormalFixture = manifest.Fixtures.Single(item =>
            item.Id == "dynamic-16-no-normals");
        byte[] noNormalBytes = File.ReadAllBytes(noNormalFixture.Path);
        Assert(noNormalFixture.SourceSha256 ==
                   "C59A162D18F1E10ECD23EFD7D34E09E238368A0E8C9C8ED0323738574B5A411D" &&
               Sha256(noNormalBytes) == noNormalFixture.SourceSha256,
            "The synthetic 16-byte no-normal control fixture hash drifted.");
        SseNifDocument noNormalDocument =
            SseFaceGeomCarrierCodec.Parse(noNormalBytes);
        SseNifBlock noNormalShape = noNormalDocument.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape");
        SsePackedNormalExtraction noNormals =
            SseFaceGeomCarrierCodec.ExtractPackedNormals(
                noNormalDocument, noNormalShape);
        Assert(noNormalFixture.VertexSize == 16 &&
               noNormalFixture.VertexDescription == "0044200010000044" &&
               !noNormalFixture.HasPackedNormals &&
               !noNormalFixture.HasTangents &&
               noNormals.VertexSize == noNormalFixture.VertexSize &&
               noNormals.VertexDescription == Convert.ToUInt64(
                   noNormalFixture.VertexDescription, 16) &&
               noNormals.NormalOffset == noNormalFixture.NormalOffset &&
               noNormals.NormalOffset == -1 &&
               noNormals.PackedNormalBytes.IsEmpty,
            "The synthetic 16-byte no-normal layout was misread as packed-normal data.");

        var sourceHash16 = new Sha256Hash(dynamic16.SourceSha256);
        var part = new SseFaceGeomCarrierAssemblyPart(
            new FormReference(new PluginName("PackedNormals.esp"),
                new FormId(0x800)),
            new AssetPath("meshes/tests/dynamic-16.nif"),
            sourceHash16,
            bytes16.ToImmutableArray(),
            "PackedNormalProbe",
            UsesFaceTint: true,
            ExpectedPackedNormalBytes: extracted16.PackedNormalBytes);
        var part24 = new SseFaceGeomCarrierAssemblyPart(
            new FormReference(new PluginName("PackedNormals.esp"),
                new FormId(0x801)),
            new AssetPath("meshes/tests/dynamic-24.nif"),
            new Sha256Hash(dynamic24.SourceSha256),
            bytes24.ToImmutableArray(),
            "PackedNormalProbe24",
            UsesFaceTint: true,
            ExpectedPackedNormalBytes: extracted24.PackedNormalBytes);
        var request = new SseFaceGeomCarrierAssemblyRequest(
            [part],
            new AssetPath(
                "Textures/Actors/Character/FaceGenData/FaceTint/PackedNormals.esp/00000800.dds"));
        var request24 = new SseFaceGeomCarrierAssemblyRequest(
            [part24],
            new AssetPath(
                "Textures/Actors/Character/FaceGenData/FaceTint/PackedNormals.esp/00000801.dds"));
        var assembler = new SseFaceGeomCarrierAssembler();
        SseFaceGeomCarrierAssemblyResult assembled = assembler.Assemble(request);
        Assert(assembled.Assembled && assembled.Verified &&
               assembled.Artifact is not null,
            "A complete carrier was refused because packed-normal verification reopened it as a selected headpart: " +
            GeometryDiagnostics(assembled.Diagnostics));
        SseFaceGeomCarrierAssemblyArtifact artifact = assembled.Artifact ??
            throw new InvalidOperationException(
                "Verified packed-normal carrier omitted its artifact.");
        SseNifDocument carrier = SseFaceGeomCarrierCodec.Parse(
            artifact.Bytes.ToArray());
        Assert(carrier.Blocks.Count(block => block.Type == "NiNode") >= 3,
            "The packed-normal carrier regression no longer exercises a complete multi-bone graph.");

        AssertPackedNormalCarrierRoundTrip(
            artifact, "PackedNormalProbe", dynamic16, bytes16, extracted16);
        SseFaceGeomCarrierAssemblyResult assembled24 =
            assembler.Assemble(request24);
        Assert(assembled24.Assembled && assembled24.Verified &&
               assembled24.Artifact is not null,
            "The 24-byte packed-normal carrier was refused: " +
            GeometryDiagnostics(assembled24.Diagnostics));
        SseFaceGeomCarrierAssemblyArtifact artifact24 =
            assembled24.Artifact ?? throw new InvalidOperationException(
                "Verified 24-byte packed-normal carrier omitted its artifact.");
        AssertPackedNormalCarrierRoundTrip(
            artifact24, "PackedNormalProbe24", dynamic24, bytes24, extracted24);
        var selectedReader = new SseSelectedHeadpartNifGeometryReader();
        SseSelectedHeadpartNifGeometryReadResult selectedNoNormals =
            selectedReader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
                new AssetPath($"meshes/tests/{noNormalFixture.Id}.nif"),
                new Sha256Hash(noNormalFixture.SourceSha256),
                noNormalBytes.ToImmutableArray()));
        Assert(selectedNoNormals.Accepted &&
               selectedNoNormals.Document?.Shapes is
               [{ PackedNormalBytes.IsEmpty: true }],
            "The selected-headpart reader rejected the synthetic 16-byte no-normal control: " +
            GeometryDiagnostics(selectedNoNormals.Diagnostics));
        byte[] conflictingBindBytes = CreateSyntheticBindConflict(
            artifact.Bytes.ToArray(), "PackedNormalProbe");
        SseSelectedHeadpartNifGeometryReadResult selectedCarrier =
            selectedReader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
                new AssetPath("meshes/tests/synthetic-bind-conflict.nif"),
                new Sha256Hash(Sha256(conflictingBindBytes)),
                conflictingBindBytes.ToImmutableArray()));
        Assert(!selectedCarrier.Accepted && selectedCarrier.Diagnostics.Any(item =>
                item.Code == "sse-headpart-nif-malformed" &&
                item.Message.Contains("bind placements disagree",
                    StringComparison.Ordinal)),
            "The selected-headpart reader no longer refuses an explicitly mutated synthetic bind-placement conflict.");
        const int mutationOffset = 17;
        var mutatedExpected = extracted16.PackedNormalBytes.ToBuilder();
        mutatedExpected[mutationOffset] ^= 0x01;
        ImmutableArray<byte> mutatedBytes = mutatedExpected.MoveToImmutable();
        string expectedHash = Sha256(mutatedBytes.AsSpan());
        string actualHash = Sha256(extracted16.PackedNormalBytes.AsSpan());
        SseFaceGeomCarrierAssemblyResult mismatch = assembler.Assemble(
            request with
            {
                Parts = [part with { ExpectedPackedNormalBytes = mutatedBytes }]
            });
        Diagnostic? mismatchDiagnostic = mismatch.Diagnostics.SingleOrDefault(item =>
            item.Code == "sse-facegeom-carrier-packed-normal-mismatch");
        Assert(!mismatch.Assembled && mismatch.Artifact is null &&
               mismatchDiagnostic is not null &&
               mismatchDiagnostic.Message.Contains("PackedNormalProbe",
                   StringComparison.Ordinal) &&
               mismatchDiagnostic.Message.Contains(
                   $"0x{extracted16.VertexDescription:X16}",
                   StringComparison.Ordinal) &&
               mismatchDiagnostic.Message.Contains(
                   $"vertex-size={extracted16.VertexSize}",
                   StringComparison.Ordinal) &&
               mismatchDiagnostic.Message.Contains(expectedHash,
                   StringComparison.Ordinal) &&
               mismatchDiagnostic.Message.Contains(actualHash,
                   StringComparison.Ordinal) &&
               mismatchDiagnostic.Message.Contains(
                   $"first-difference={mutationOffset}",
                   StringComparison.Ordinal),
            "A one-byte packed-normal authority mutation was not refused with the exact layout, hashes, and first differing offset: " +
            GeometryDiagnostics(mismatch.Diagnostics));
        return Task.CompletedTask;
    }

    private static void AssertSelectedPackedNormalFixture(
        SseSelectedHeadpartNifGeometryReader reader,
        PackedNormalFixture fixture,
        byte[] bytes)
    {
        SseSelectedHeadpartNifGeometryReadResult result = reader.Read(
            new SseSelectedHeadpartNifGeometryReadRequest(
                new AssetPath($"meshes/tests/{fixture.Id}.nif"),
                new Sha256Hash(fixture.SourceSha256),
                bytes.ToImmutableArray()));
        Assert(result.Accepted && result.Document is not null,
            $"The selected-headpart reader rejected synthetic {fixture.VertexSize}-byte packed-normal geometry: " +
            GeometryDiagnostics(result.Diagnostics));
        SseSelectedHeadpartNifRestShape shape =
            result.Document!.Shapes.Single();
        Assert(shape.VertexCount == fixture.VertexCount &&
               shape.TriangleIndices.Length == fixture.TriangleCount * 3 &&
               shape.TextureCoordinates.Length == fixture.VertexCount &&
               shape.Normals.Length == fixture.VertexCount &&
               shape.Normals[0].Z > 0.99F &&
               shape.Normals[1].Y > 0.99F &&
               shape.Normals[2].X > 0.99F,
            $"The selected-headpart reader did not decode the synthetic {fixture.VertexSize}-byte layout and its varying normal rows.");
    }

    private static void AssertPackedNormalCarrierRoundTrip(
        SseFaceGeomCarrierAssemblyArtifact artifact,
        string outputShapeName,
        PackedNormalFixture fixture,
        byte[] sourceBytes,
        SsePackedNormalExtraction sourceExtraction)
    {
        byte[] carrierBytes = artifact.Bytes.ToArray();
        SseNifDocument carrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
        SseNifBlock shape = carrier.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape" &&
            string.Equals(block.Name, outputShapeName, StringComparison.Ordinal));
        SsePackedNormalExtraction carrierExtraction =
            SseFaceGeomCarrierCodec.ExtractPackedNormals(carrier, shape);
        byte[] sourceTangents = ReadVertexAttribute(
            sourceBytes, sourceExtraction, fixture.TangentOffset);
        byte[] carrierTangents = ReadVertexAttribute(
            carrierBytes, carrierExtraction, fixture.TangentOffset);
        SseNifBlock skin = carrier.Blocks[shape.References.Single(reference =>
            reference.Kind == "skin").Target];
        string[] boneNames = skin.References
            .Where(reference => reference.Kind == "bone" && reference.Target >= 0)
            .Select(reference => carrier.Blocks[reference.Target].Name ?? string.Empty)
            .ToArray();
        Assert(carrierExtraction.VertexDescription ==
                   sourceExtraction.VertexDescription &&
               carrierExtraction.VertexSize == fixture.VertexSize &&
               carrierExtraction.NormalOffset == fixture.NormalOffset &&
               carrierExtraction.PackedNormalBytes.AsSpan().SequenceEqual(
                   sourceExtraction.PackedNormalBytes.AsSpan()) &&
               carrierTangents.AsSpan().SequenceEqual(sourceTangents) &&
               boneNames.SequenceEqual(
                   ["NPC Head [Head]", "NPC Spine2 [Spn2]"],
                   StringComparer.Ordinal),
            $"Carrier assembly changed the {fixture.VertexSize}-byte normal/tangent rows or complete two-bone skin graph.");

        SseSelectedHeadpartNifGeometryReadResult selected = new
            SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    new AssetPath($"meshes/tests/{outputShapeName}.nif"),
                    artifact.Sha256,
                    artifact.Bytes));
        Assert(selected.Accepted && selected.Document?.Shapes is
                   [{ PackedNormalBytes.Length: > 0 }],
            $"The assembled {fixture.VertexSize}-byte packed-normal carrier did not reparse through the selected-headpart reader: " +
            GeometryDiagnostics(selected.Diagnostics));
    }

    private static byte[] ReadVertexAttribute(
        byte[] bytes,
        SsePackedNormalExtraction extraction,
        int offset)
    {
        if (offset < 0 || offset > extraction.VertexSize - sizeof(uint))
            throw new InvalidOperationException(
                "Synthetic packed attribute offset escaped its vertex row.");
        var result = new byte[checked(extraction.VertexCount * sizeof(uint))];
        for (int vertex = 0; vertex < extraction.VertexCount; vertex++)
        {
            int sourceOffset = checked(extraction.VertexDataOffset +
                vertex * extraction.VertexSize + offset);
            bytes.AsSpan(sourceOffset, sizeof(uint)).CopyTo(
                result.AsSpan(vertex * sizeof(uint), sizeof(uint)));
        }
        return result;
    }

    private static byte[] CreateSyntheticBindConflict(
        byte[] carrierBytes,
        string shapeName)
    {
        SseNifDocument document =
            SseFaceGeomCarrierCodec.Parse(carrierBytes);
        SseNifBlock shape = document.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape" &&
            string.Equals(block.Name, shapeName, StringComparison.Ordinal));
        SseNifBlock skin = document.Blocks[shape.References.Single(reference =>
            reference.Kind == "skin").Target];
        SseNifBlock skinData = document.Blocks[skin.References.Single(reference =>
            reference.Kind == "skindata").Target];
        int position = checked(skinData.Offset + 52);
        uint boneCount =
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                carrierBytes.AsSpan(position, sizeof(uint)));
        position += sizeof(uint);
        if (boneCount != 2 || carrierBytes[position++] != 1)
            throw new InvalidOperationException(
                "The synthetic bind-conflict mutation requires the complete two-bone skin-data layout.");
        int secondBoneTransformOffset = -1;
        for (uint bone = 0; bone < boneCount; bone++)
        {
            int transformOffset = position;
            position += 52 + 16;
            int weightCount =
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                    carrierBytes.AsSpan(position, sizeof(ushort)));
            position += sizeof(ushort) + checked(weightCount * 6);
            if (bone == 1) secondBoneTransformOffset = transformOffset;
        }
        if (secondBoneTransformOffset < 0)
            throw new InvalidOperationException(
                "The synthetic second-bone transform was not found.");
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
            carrierBytes.AsSpan(secondBoneTransformOffset + 36, sizeof(float)),
            2.0F);
        return carrierBytes;
    }

    private static PackedNormalFixtureManifest ReadPackedNormalFixtureManifest()
    {
        string path = Path.GetFullPath(
            "tests/fixtures/sse-packed-normals/manifest.json");
        PackedNormalFixtureManifest? manifest = JsonSerializer.Deserialize<
            PackedNormalFixtureManifest>(File.ReadAllText(path),
            PackedNormalJsonOptions);
        return manifest is { SchemaVersion: 2, Fixtures.Length: 3 }
            ? manifest
            : throw new InvalidOperationException(
                "Packed-normal fixture manifest must contain exactly three schema-2 fixtures.");
    }

    private static (byte[] Bytes, SseNifDocument Document, SseNifBlock Shape)
        LoadPackedNormalFixture(PackedNormalFixture fixture)
    {
        string path = Path.GetFullPath(fixture.Path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert(Sha256(bytes) == fixture.SourceSha256,
            $"Packed-normal fixture '{fixture.Id}' hash drifted.");
        SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
        SseNifBlock shape = document.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape" &&
            string.Equals(block.Name, fixture.ShapeName,
                StringComparison.Ordinal));
        return (bytes, document, shape);
    }

    private static void AssertPackedNormalExtraction(
        PackedNormalFixture fixture,
        byte[] bytes,
        SsePackedNormalExtraction extraction)
    {
        byte[] tangentBytes = ReadVertexAttribute(
            bytes, extraction, fixture.TangentOffset);
        Assert(extraction.VertexDescription ==
                   Convert.ToUInt64(fixture.VertexDescription, 16) &&
               extraction.VertexSize == fixture.VertexSize &&
               extraction.NormalOffset == fixture.NormalOffset &&
               SseFaceGeomCarrierCodec.GetVertexAttributeOffset(
                   extraction.VertexDescription, 4) == fixture.TangentOffset &&
               extraction.VertexCount == fixture.VertexCount &&
               extraction.PackedNormalBytes.Length ==
                   checked(fixture.VertexCount * 4) &&
               Sha256(extraction.PackedNormalBytes.AsSpan()) ==
                   fixture.PackedNormalSha256 &&
               Convert.ToHexString(extraction.PackedNormalBytes.AsSpan(0, 8)) ==
                   fixture.FirstEightBytes &&
               Convert.ToHexString(extraction.PackedNormalBytes.AsSpan(
                   extraction.PackedNormalBytes.Length - 8, 8)) ==
                   fixture.LastEightBytes &&
               Sha256(tangentBytes) == fixture.PackedTangentSha256 &&
               Convert.ToHexString(tangentBytes.AsSpan(0, 8)) ==
                   fixture.FirstTangentBytes &&
               Convert.ToHexString(tangentBytes.AsSpan(
                   tangentBytes.Length - 8, 8)) == fixture.LastTangentBytes,
            $"Packed normal/tangent extraction for '{fixture.Id}' did not preserve its row-varying bytes and layout.");
    }
    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record PackedNormalFixtureManifest(
        int SchemaVersion,
        ImmutableArray<PackedNormalFixture> Fixtures);

    private sealed record PackedNormalFixture(
        string Id,
        string Path,
        string SourceSha256,
        string ShapeName,
        string VertexDescription,
        int VertexSize,
        bool HasPackedNormals,
        bool HasTangents,
        int NormalOffset,
        int TangentOffset,
        int VertexCount,
        int TriangleCount,
        string PackedNormalSha256,
        string PackedTangentSha256,
        string FirstEightBytes,
        string LastEightBytes,
        string FirstTangentBytes,
        string LastTangentBytes);
}
