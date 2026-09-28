using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private const string HeadpartGeometryRoot =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases";

    private static readonly ImmutableArray<string> HeadpartModelAssets =
    [
        "meshes/KL/High Poly Head/FemaleHead.nif",
        "meshes/KL/High Poly Head/FaceParts/FemaleBrows.nif",
        "meshes/Actors/Character/Character Assets/EyesFemale.nif",
        "meshes/Actors/Character/Character Assets/Mouth/MouthHumanF.nif",
        "meshes/KS Hairdo's/Lassi.nif",
        "meshes/KS Hairdo's/LassiHL.nif",
        "meshes/KS Hairdo's/hairline/straightscalpHUMAN.nif"
    ];

    private static readonly ImmutableDictionary<string, ExpectedHeadpartGeometry>
        ExpectedHeadpartGeometryByAsset =
            new Dictionary<string, ExpectedHeadpartGeometry>(StringComparer.Ordinal)
            {
                ["meshes/KL/High Poly Head/FemaleHead.nif"] = new(
                    "A2A9835C506E067F89E273006CDA4FD7F365EF94722EC0620B2C22BB59AE0BAD",
                    "FemaleHead_KLH", "00KLH_FemaleHeadNord", 3832,
                    "3D48DBB1637CB6105FB8BFEDD86053CD60DA2D21BD4BDA22FF47DFDE7B9E4773",
                    7164, false),
                ["meshes/KL/High Poly Head/FaceParts/FemaleBrows.nif"] = new(
                    "C6A8F44AD50B889299475DCF34E43287302D413DC4112411DD69B50062419DE3",
                    "FemaleHeadBrows", "KoralinaEyebrowsF02", 371,
                    "C31873746309FFD2C84E3C11528E5490BD6B789FAE1746FC8C8586BAFE454B8A",
                    652, true),
                ["meshes/Actors/Character/Character Assets/EyesFemale.nif"] = new(
                    "90FD9BD845865EAB2DE743E08699CB6E8FD621C53A60FC4ACD725B8457E33B74",
                    "EyesFemaleV2", "MJBFemaleEyesHumanGreen04", 176,
                    "F52BE3D1B765AECE1F4E312940446CFADED9D12D28F41EF909A9EA9778FAB6E9",
                    202, true),
                ["meshes/Actors/Character/Character Assets/Mouth/MouthHumanF.nif"] = new(
                    "53110E262DC14084A848A295A0E941DD9FD9478F6013DC86AEF72A31F8F4E5E5",
                    "MouthHumanF", "FemaleMouthHumanoidDefault", 141,
                    "B1EF0EA833A50557E412CF4C99FE16EBE6DFBA640F4C0189B4F4E01C9A347775",
                    188, true),
                ["meshes/KS Hairdo's/Lassi.nif"] = new(
                    "FB638096A80E949D69110441FB6766F64088D5011EB6D0E073C8C0FA410D149B",
                    "s4studio_mesh_3", "0Lassi", 7362,
                    "374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A",
                    9853, true),
                ["meshes/KS Hairdo's/LassiHL.nif"] = new(
                    "C0392F50DC13591103A26B93D192367A1E67A19EEB120A32E09BA830DA747D35",
                    "s4studio_mesh_3", "0LassiHL", 7362,
                    "374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A",
                    9853, true),
                ["meshes/KS Hairdo's/hairline/straightscalpHUMAN.nif"] = new(
                    "C53E9F58BAE59BE203653DFC131234A6F6403D3E8DEE05D84DE00B1FDDD233C5",
                    "straightscalpHUMAN", "0_HAIRLINE_Female_Human_Straight", 897,
                    "5556EB1D3E9379DAFFD9F95F1ED65D3B6A07D5647EF64C7E61C21920675CC360",
                    1680, true)
            }.ToImmutableDictionary(StringComparer.Ordinal);

    private static Task TestSseSelectedHeadpartNifGeometryReader()
    {
        var reader = new SseSelectedHeadpartNifGeometryReader();
        var carrier = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(CarrierPath));
        var carrierShapes = carrier.Blocks.Where(block =>
                block.Type == "BSDynamicTriShape")
            .ToDictionary(block => block.Name!, StringComparer.Ordinal);
        var readDocuments = new Dictionary<string, SseSelectedHeadpartNifGeometryDocument>(
            StringComparer.Ordinal);
        foreach (var asset in HeadpartModelAssets)
        {
            var expected = ExpectedHeadpartGeometryByAsset[asset];
            var bytes = File.ReadAllBytes(PhysicalPath(asset));
            var result = reader.Read(Request(asset, expected.SourceSha256, bytes));
            Assert(result.Accepted && result.Document is not null,
                $"Real selected-headpart NIF was refused for {asset}: {GeometryDiagnostics(result.Diagnostics)}");
            var document = result.Document ?? throw new InvalidOperationException(
                $"Accepted selected-headpart NIF {asset} returned no document.");
            Assert(document.SourceSha256 == new Sha256Hash(expected.SourceSha256) &&
                   document.Shapes.Length == 1,
                $"Selected-headpart NIF {asset} drifted from its one-shape source authority.");
            var shape = document.Shapes[0];
            Assert(shape.Name == expected.InternalShapeName &&
                   shape.VertexCount == expected.VertexCount &&
                   shape.RestPositions.Length == expected.VertexCount &&
                   shape.PackedPositionSha256 == new Sha256Hash(expected.PositionSha256),
                $"Selected-headpart NIF {asset} returned unexpected NIF-rest geometry.");
            Assert(shape.TriangleIndices.Length == expected.TriangleCount * 3 &&
                   shape.TriangleIndices.Length % 3 == 0 &&
                   shape.TriangleIndices.All(index => index >= 0 && index < shape.VertexCount),
                $"Selected-headpart NIF {asset} returned invalid triangle geometry.");
            Assert(shape.TextureCoordinates.Length == shape.VertexCount &&
                   shape.TextureCoordinates.All(uv =>
                       float.IsFinite(uv.X) && float.IsFinite(uv.Y)),
                $"Selected-headpart NIF {asset} did not return one finite UV per vertex.");
            Assert(shape.Normals.Length == (expected.HasNormals ? shape.VertexCount : 0) &&
                   shape.Normals.All(normal =>
                       float.IsFinite(normal.X) && float.IsFinite(normal.Y) &&
                       float.IsFinite(normal.Z)),
                $"Selected-headpart NIF {asset} returned unexpected normal geometry.");
            Assert(shape.Materials.Length == 1 &&
                   shape.Materials[0].FirstTriangleOrdinal == 0 &&
                   shape.Materials[0].TriangleCount == expected.TriangleCount &&
                   shape.Materials[0].ShaderBlockIndex >= 0 &&
                   shape.Materials[0].TextureSetBlockIndex >= 0 &&
                   !shape.Materials[0].TexturePaths.IsDefaultOrEmpty,
                $"Selected-headpart NIF {asset} did not bind its full triangle range to one material.");
            Assert(carrierShapes.TryGetValue(expected.CarrierShapeName, out var carrierShape) &&
                   carrierShape.DynamicGeometry!.VertexCount == shape.VertexCount &&
                   SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(
                       carrier, carrierShape) == shape.TopologySha256,
                $"Selected asset {asset} does not topology-map to carrier shape {expected.CarrierShapeName}.");
            readDocuments.Add(asset, document);
            Console.WriteLine(
                $"EVIDENCE HEADPART-NIF {asset} => internal:{shape.Name} => carrier:{expected.CarrierShapeName} vertices={shape.VertexCount} positions={shape.PackedPositionSha256} topology={shape.TopologySha256}");
        }

        AssertNifRestIsNotTriBaseVertices(readDocuments);
        AssertReaderRefusals(reader);
        AssertUbeShaderlessDummyIntake(reader);
        return Task.CompletedTask;
    }

    private static void AssertNifRestIsNotTriBaseVertices(
        Dictionary<string, SseSelectedHeadpartNifGeometryDocument> documents)
    {
        const string lassiAsset = "meshes/KS Hairdo's/Lassi.nif";
        var nifShape = documents[lassiAsset].Shapes.Single();
        AssertSingleLassiModelShape(lassiAsset, nifShape);
        AssertSingleLassiModelShape("meshes/KS Hairdo's/LassiHL.nif",
            documents["meshes/KS Hairdo's/LassiHL.nif"].Shapes.Single());
        var triBytes = File.ReadAllBytes(PhysicalPath("meshes/KS Hairdo's/Lassi.tri"));
        Assert(triBytes.AsSpan(0, 8).SequenceEqual("FRTRI003"u8),
            "The Lassi TRI rest-source discriminator lost its FRTRI003 header.");
        var triVertexCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            triBytes.AsSpan(8, sizeof(uint))));
        var morphCount = BinaryPrimitives.ReadUInt32LittleEndian(
            triBytes.AsSpan(36, sizeof(uint)));
        var modifierCount = BinaryPrimitives.ReadUInt32LittleEndian(
            triBytes.AsSpan(40, sizeof(uint)));
        var modifierVertexCount = BinaryPrimitives.ReadUInt32LittleEndian(
            triBytes.AsSpan(44, sizeof(uint)));
        var triBasePositionBytes = triBytes.AsSpan(64, checked(triVertexCount * 12));
        var triBasePositionSha256 = Sha256Of(triBasePositionBytes);
        Assert(triVertexCount == 7432 && nifShape.VertexCount == 7362 &&
               morphCount == 0 && modifierCount == 0 && modifierVertexCount == 0 &&
               triBasePositionSha256 ==
               new Sha256Hash("5877EF52B23A3C2BC70DF10365D91F2CFB4A57EA4A3041C20B92AA51B6499CFF") &&
               triBasePositionSha256 != nifShape.PackedPositionSha256,
            "The reader did not distinguish exact Lassi NIF-rest positions from TRI BaseVertices.");
        Console.WriteLine(
            "EVIDENCE LASSI-TRI vertices=7432 SkinnyMorph=absent deltas=0 index-range=none; no whole-TRI application is authorized for either 7362-vertex model shape.");
    }

    private static void AssertSingleLassiModelShape(
        string asset,
        SseSelectedHeadpartNifRestShape exposedShape)
    {
        var nif = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(PhysicalPath(asset)));
        var reachable = SseFaceGeomCarrierCodec.FindReachableBlockIndexes(nif);
        var reachableShapes = reachable.Order()
            .Select(index => nif.Blocks[index])
            .Where(block => block.Type is "BSDynamicTriShape" or "BSTriShape")
            .ToImmutableArray();
        Assert(reachableShapes.Length == 1 &&
               reachableShapes[0].Type == "BSDynamicTriShape" &&
               reachableShapes[0].DynamicGeometry?.VertexCount == 7362 &&
               reachableShapes[0].Name == exposedShape.Name,
            $"{asset} unexpectedly contains a companion or partition shape.");
        Console.WriteLine(
            $"EVIDENCE LASSI-MODEL {asset} reachable-shapes=1 dynamic:{exposedShape.Name}:7362 companion-shapes=0");
    }

    private static void AssertReaderRefusals(SseSelectedHeadpartNifGeometryReader reader)
    {
        var asset = HeadpartModelAssets[0];
        var bytes = File.ReadAllBytes(PhysicalPath(asset));
        var wrongHash = reader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
            new AssetPath(asset),
            new Sha256Hash(new string('0', 64)),
            ImmutableArray.CreateRange(bytes)));
        Assert(!wrongHash.Accepted && wrongHash.Diagnostics.Any(item =>
                item.Code == "sse-headpart-nif-hash-mismatch"),
            "Selected-headpart NIF reader accepted a mismatched source hash.");

        var trailing = new byte[bytes.Length + 1];
        bytes.CopyTo(trailing, 0);
        trailing[^1] = 0x7F;
        var trailingResult = reader.Read(Request(asset, trailing));
        Assert(!trailingResult.Accepted && trailingResult.Diagnostics.Any(item =>
                item.Code == "sse-headpart-nif-malformed" &&
                item.Message.Contains("consume the file exactly", StringComparison.Ordinal)),
            "Selected-headpart NIF reader accepted unparsed trailing bytes.");

        var nonFinite = (byte[])bytes.Clone();
        var parsed = SseFaceGeomCarrierCodec.Parse(nonFinite);
        var dynamicShape = parsed.Blocks.First(block =>
            block.Type == "BSDynamicTriShape" && block.DynamicGeometry is not null);
        BinaryPrimitives.WriteInt32LittleEndian(
            nonFinite.AsSpan(dynamicShape.DynamicGeometry!.VertexDataOffset, sizeof(float)),
            BitConverter.SingleToInt32Bits(float.NaN));
        var nonFiniteResult = reader.Read(Request(asset, nonFinite));
        Assert(!nonFiniteResult.Accepted && nonFiniteResult.Diagnostics.Any(item =>
                item.Code == "sse-headpart-nif-malformed" &&
                item.Message.Contains("non-finite", StringComparison.Ordinal)),
            "Selected-headpart NIF reader accepted a non-finite rest position.");

        var ambiguous = File.ReadAllBytes(CarrierPath);
        var carrier = SseFaceGeomCarrierCodec.Parse(ambiguous);
        var carrierShapes = carrier.Blocks.Where(block =>
                block.Type == "BSDynamicTriShape")
            .Take(2)
            .ToArray();
        Assert(carrierShapes.Length == 2, "Qualified carrier lost its ambiguity fixture shapes.");
        ambiguous.AsSpan(carrierShapes[0].Offset, sizeof(uint)).CopyTo(
            ambiguous.AsSpan(carrierShapes[1].Offset, sizeof(uint)));
        var ambiguousResult = reader.Read(Request(
            "meshes/tests/duplicate-shape-name.nif", ambiguous));
        Assert(!ambiguousResult.Accepted && ambiguousResult.Diagnostics.Any(item =>
                item.Code == "sse-headpart-nif-malformed" &&
                item.Message.Contains("distinct and non-empty", StringComparison.Ordinal)),
            "Selected-headpart NIF reader accepted ambiguous duplicate shape names.");
    }

    private static void AssertUbeShaderlessDummyIntake(
        SseSelectedHeadpartNifGeometryReader reader)
    {
        const string relative =
            "meshes/!UBE/Eyes/LensFemaleLeft_Dummy.nif";
        string physical = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\chel-schema7-provider-20260725-2\Data",
            relative.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = File.ReadAllBytes(physical);
        SseSelectedHeadpartNifGeometryReadResult admitted =
            reader.Read(Request(relative, bytes));
        Assert(admitted.Accepted &&
               admitted.Document?.Shapes is
               [{ Materials.IsDefaultOrEmpty: true }] &&
               admitted.Diagnostics.Any(item =>
                   item.Code ==
                   "sse-headpart-nif-shaderless-dummy-admitted"),
            $"The exact UBE shaderless dummy lens was not admitted for the downstream bounded dummy filter: {GeometryDiagnostics(admitted.Diagnostics)}");

        SseSelectedHeadpartNifGeometryReadResult aliased =
            reader.Read(Request(
                "meshes/tests/LensFemaleLeft.nif", bytes));
        Assert(!aliased.Accepted &&
               aliased.Diagnostics.Any(item =>
                   item.Code == "sse-headpart-nif-malformed" &&
                   item.Message.Contains(
                       "shader authority",
                       StringComparison.Ordinal)),
            "Shaderless geometry was admitted without the bounded dummy lens path/name discriminator.");
    }

    private static SseSelectedHeadpartNifGeometryReadRequest Request(
        string asset,
        byte[] bytes) =>
        new(new AssetPath(asset), Sha256Of(bytes), ImmutableArray.CreateRange(bytes));

    private static SseSelectedHeadpartNifGeometryReadRequest Request(
        string asset,
        string expectedSha256,
        byte[] bytes) =>
        new(new AssetPath(asset), new Sha256Hash(expectedSha256),
            ImmutableArray.CreateRange(bytes));

    private static string PhysicalPath(string asset) =>
        Path.Combine(HeadpartGeometryRoot, asset.Replace('/', Path.DirectorySeparatorChar));

    private static Sha256Hash Sha256Of(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static string GeometryDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            $"{item.Severity}:{item.Code}:{item.Message}"));

    private sealed record ExpectedHeadpartGeometry(
        string SourceSha256,
        string InternalShapeName,
        string CarrierShapeName,
        int VertexCount,
        string PositionSha256,
        int TriangleCount,
        bool HasNormals);
}
