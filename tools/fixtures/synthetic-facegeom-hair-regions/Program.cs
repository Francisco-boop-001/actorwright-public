using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

if (args.Length != 2 || args[0] is not ("--regenerate" or "--check"))
{
    Console.Error.WriteLine("Usage: synthetic-facegeom-hair-regions-generator (--regenerate|--check) <Actorwright root>");
    return 2;
}

string root = Path.GetFullPath(args[1]);
string sourcePath = Path.Combine(root, "tests", "fixtures", "sse-packed-normals", "dynamic-16.nif");
string sourceManifestPath = Path.Combine(root, "tests", "fixtures", "sse-packed-normals", "manifest.json");
string fixtureDirectory = Path.Combine(root, "tests", "fixtures", "synthetic-facegeom-hair-regions");
string outputPath = Path.Combine(fixtureDirectory, "facegeom.nif");
string manifestPath = Path.Combine(fixtureDirectory, "manifest.json");

byte[] source = File.ReadAllBytes(sourcePath);
using JsonDocument sourceManifest = JsonDocument.Parse(File.ReadAllBytes(sourceManifestPath));
string expectedSourceHash = sourceManifest.RootElement.GetProperty("fixtures").EnumerateArray()
    .Single(item => item.GetProperty("id").GetString() == "dynamic-16")
    .GetProperty("sourceSha256").GetString()!;
string sourceHash = Hash(source);
if (!string.Equals(sourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("The packed-normal base fixture differs from its generated manifest.");

byte[] hairSource = WithHairTintShaderType(source);
const string headName = "FemaleHead";
var hairShapes = new (string Name, uint Color, string Role)[]
{
    ("SyntheticMainHair", 0x0022_2222, "primary"),
    ("SyntheticHairline", 0x0022_2222, "primary"),
    ("SyntheticBrow", 0x0030_3030, "preserve"),
    ("SyntheticHairHighlight", 0x0030_3030, "accent"),
    ("SyntheticLashes", 0x0022_2222, "preserve")
};
var parts = ImmutableArray.CreateBuilder<SseFaceGeomCarrierAssemblyPart>(6);
parts.Add(CreatePart(source, "SyntheticHeadPart.esp", 0x800,
    "meshes/actorwright/tests/facegeom-head.nif", headName, usesFaceTint: true));
for (int index = 0; index < hairShapes.Length; index++)
{
    (string name, uint color, _) = hairShapes[index];
    parts.Add(CreatePart(hairSource, "SyntheticHairPart.esp",
        checked((uint)(0x801 + index)),
        $"meshes/actorwright/tests/facegeom-hair-{index}.nif",
        name, usesFaceTint: false) with { HairTintPackedRgb = color });
}

var request = new SseFaceGeomCarrierAssemblyRequest(
    parts.MoveToImmutable(),
    new AssetPath("Textures/Actors/Character/FaceGenData/FaceTint/SyntheticHairRegions.esp/00000800.dds"),
    SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones);
SseFaceGeomCarrierAssemblyResult assembled =
    new SseFaceGeomCarrierAssembler().Assemble(request);
if (!assembled.Assembled || !assembled.Verified || assembled.Artifact is null ||
    assembled.Artifact.RuntimeAuthority ||
    !assembled.Artifact.Shapes.Select(item => item.OutputShapeName)
        .SequenceEqual(new[] { headName }.Concat(hairShapes.Select(item => item.Name)), StringComparer.Ordinal))
{
    throw new InvalidDataException("Synthetic FaceGeom fixture assembly failed: " +
        string.Join("; ", assembled.Diagnostics.Select(item => item.Code + ": " + item.Message)));
}

byte[] output = assembled.Artifact.Bytes.ToArray();
var manifest = new
{
    schemaVersion = 1,
    id = "synthetic-facegeom-hair-regions-v1",
    purpose = "Owned, non-visual test input for FaceGeom HairTint analysis and byte-exact mutation checks.",
    runtimeAuthority = false,
    visualAuthority = false,
    generator = "tools/fixtures/synthetic-facegeom-hair-regions",
    assembly = "NpcManager.Infrastructure.SseFaceGeomCarrierAssembler",
    baseFixture = new
    {
        path = "tests/fixtures/sse-packed-normals/dynamic-16.nif",
        sha256 = sourceHash
    },
    nif = new
    {
        path = "tests/fixtures/synthetic-facegeom-hair-regions/facegeom.nif",
        byteLength = output.Length,
        sha256 = Hash(output)
    },
    shapes = new object[]
    {
        new { name = headName, role = "face", usesFaceTint = true, hairTint = (string?)null }
    }.Concat(hairShapes.Select(item => (object)new
    {
        name = item.Name,
        role = item.Role,
        usesFaceTint = false,
        hairTint = $"#{item.Color:X6}"
    })).ToArray(),
    skin = new
    {
        skeletonAuthority = "identity-facegen-bones",
        boneNames = new[] { "NPC Head [Head]", "NPC Spine2 [Spn2]" },
        runtimeAuthority = false
    }
};
byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest,
    new JsonSerializerOptions { WriteIndented = true }).Concat(new byte[] { (byte)'\n' }).ToArray();

if (args[0] == "--regenerate")
{
    Directory.CreateDirectory(fixtureDirectory);
    File.WriteAllBytes(outputPath, output);
    File.WriteAllBytes(manifestPath, manifestBytes);
    Console.WriteLine($"Generated synthetic FaceGeom fixture: {output.Length} bytes, SHA-256 {Hash(output)}.");
    return 0;
}

if (!File.Exists(outputPath) || !File.Exists(manifestPath) ||
    !File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(output) ||
    !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifestBytes))
{
    Console.Error.WriteLine("Synthetic FaceGeom fixture bytes or manifest differ from deterministic assembly output.");
    return 1;
}
Console.WriteLine($"PASS synthetic FaceGeom fixture: {output.Length} bytes, SHA-256 {Hash(output)}.");
return 0;

static SseFaceGeomCarrierAssemblyPart CreatePart(
    byte[] bytes, string plugin, uint formId, string sourcePath,
    string shapeName, bool usesFaceTint) =>
    new(
        new FormReference(new PluginName(plugin), new FormId(formId)),
        new AssetPath(sourcePath),
        new Sha256Hash(Hash(bytes)),
        bytes.ToImmutableArray(),
        shapeName,
        usesFaceTint);

static string Hash(byte[] bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes));

static byte[] WithHairTintShaderType(byte[] source)
{
    SseNifBlock[] shaders = SseFaceGeomCarrierCodec.Parse(source).Blocks
        .Where(block => block.Type == "BSLightingShaderProperty")
        .ToArray();
    if (shaders.Length != 1 || shaders[0].Size < sizeof(uint) ||
        BinaryPrimitives.ReadUInt32LittleEndian(
            source.AsSpan(shaders[0].Offset, sizeof(uint))) != 5)
    {
        throw new InvalidDataException(
            "The packed-normal base fixture must contain one type-5 lighting shader.");
    }

    byte[] result = source.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan(shaders[0].Offset, sizeof(uint)), 6);
    return result;
}
