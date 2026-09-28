using System.Security.Cryptography;
using System.Text.Json;

namespace NpcManager.TestInfrastructure;

internal sealed record SyntheticFaceGeomHairRegionsDocument(
    string Path,
    byte[] Bytes,
    string Sha256,
    SyntheticFaceGeomHairRegionsShape[] Shapes);

internal sealed record SyntheticFaceGeomHairRegionsShape(
    string Name,
    string Role,
    string? HairTint,
    bool UsesFaceTint);

internal static class SyntheticFaceGeomHairRegionsFixture
{
    private const string RelativeNifPath =
        "tests/fixtures/synthetic-facegeom-hair-regions/facegeom.nif";
    private const string RelativeManifestPath =
        "tests/fixtures/synthetic-facegeom-hair-regions/manifest.json";
    private const string RelativeBasePath =
        "tests/fixtures/sse-packed-normals/dynamic-16.nif";

    private static readonly SyntheticFaceGeomHairRegionsShape[] ExpectedShapes =
    [
        new("FemaleHead", "face", null, true),
        new("SyntheticMainHair", "primary", "#222222", false),
        new("SyntheticHairline", "primary", "#222222", false),
        new("SyntheticBrow", "preserve", "#303030", false),
        new("SyntheticHairHighlight", "accent", "#303030", false),
        new("SyntheticLashes", "preserve", "#222222", false)
    ];

    internal static SyntheticFaceGeomHairRegionsDocument Load(
        string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string manifestPath = Path.Combine(root,
            RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath));
        JsonElement manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("id").GetString() !=
                "synthetic-facegeom-hair-regions-v1" ||
            manifest.GetProperty("runtimeAuthority").GetBoolean() ||
            manifest.GetProperty("visualAuthority").GetBoolean())
        {
            throw new InvalidDataException(
                "The synthetic FaceGeom fixture manifest does not declare test-only authority.");
        }

        JsonElement baseFixture = manifest.GetProperty("baseFixture");
        string basePathValue = baseFixture.GetProperty("path").GetString()!;
        if (basePathValue != RelativeBasePath)
            throw new InvalidDataException(
                "The synthetic FaceGeom fixture names an unexpected base input.");
        string basePath = Path.Combine(root,
            RelativeBasePath.Replace('/', Path.DirectorySeparatorChar));
        string actualBaseHash = Hash(File.ReadAllBytes(basePath));
        if (!string.Equals(actualBaseHash,
                baseFixture.GetProperty("sha256").GetString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The synthetic FaceGeom base input differs from its recorded identity.");
        }

        JsonElement nif = manifest.GetProperty("nif");
        string pathValue = nif.GetProperty("path").GetString()!;
        if (pathValue != RelativeNifPath)
            throw new InvalidDataException(
                "The synthetic FaceGeom fixture path is not canonical.");
        string path = Path.GetFullPath(Path.Combine(root,
            RelativeNifPath.Replace('/', Path.DirectorySeparatorChar)));
        byte[] bytes = File.ReadAllBytes(path);
        string sha256 = Hash(bytes);
        string expectedHash = nif.GetProperty("sha256").GetString()!;
        if (!string.Equals(sha256, expectedHash,
                StringComparison.OrdinalIgnoreCase) ||
            nif.GetProperty("byteLength").GetInt64() != bytes.LongLength)
        {
            throw new InvalidDataException(
                "The synthetic FaceGeom fixture bytes differ from their generated manifest.");
        }

        SyntheticFaceGeomHairRegionsShape[] shapes = manifest
            .GetProperty("shapes")
            .EnumerateArray()
            .Select(item => new SyntheticFaceGeomHairRegionsShape(
                item.GetProperty("name").GetString()!,
                item.GetProperty("role").GetString()!,
                item.GetProperty("hairTint").ValueKind == JsonValueKind.Null
                    ? null
                    : item.GetProperty("hairTint").GetString(),
                item.GetProperty("usesFaceTint").GetBoolean()))
            .ToArray();
        if (!shapes.SequenceEqual(ExpectedShapes))
            throw new InvalidDataException(
                "The synthetic FaceGeom shape/role contract differs from the reviewed fixture contract.");

        return new(path, bytes, sha256, shapes);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}
