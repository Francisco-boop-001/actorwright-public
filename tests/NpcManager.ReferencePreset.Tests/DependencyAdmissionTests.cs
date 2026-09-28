using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NpcManager.ReferencePreset.Tests;

internal static class DependencyAdmissionTests
{
    private static readonly (string Path, string Role)[] ExpectedAssets =
    [
        ("libmediapipe.dll", "native-c-api"),
        ("opencv_world3410.dll", "opencv-runtime"),
        ("concrt140.dll", "vc-runtime-concurrency"),
        ("msvcp140.dll", "vc-runtime-cpp"),
        ("vcruntime140.dll", "vc-runtime-core"),
        ("vcruntime140_1.dll", "vc-runtime-core-1"),
        ("blaze_face_short_range.tflite", "face-detector-model"),
        ("face_landmarker.task", "face-landmarker-model")
    ];

    private static readonly string[] ExpectedMediaPipeExports =
    [
        "MpErrorFree",
        "MpFaceDetectorClose",
        "MpFaceDetectorCloseResult",
        "MpFaceDetectorCreate",
        "MpFaceDetectorDetectImage",
        "MpFaceLandmarkerClose",
        "MpFaceLandmarkerCloseResult",
        "MpFaceLandmarkerCreate",
        "MpFaceLandmarkerDetectImage",
        "MpImageCreateFromUint8Data",
        "MpImageFree"
    ];

    public static Task TestReferenceRuntimeManifest()
    {
        string projectRoot = FindProjectRoot();
        string runtimeRoot = Path.Combine(projectRoot, "runtime", "reference-preset");
        string manifestPath = Path.Combine(runtimeRoot, "runtime-asset-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"reference-runtime-manifest-missing: {manifestPath}");
        }

        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        using JsonDocument document = JsonDocument.Parse(
            manifestBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        JsonElement root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object,
            "reference-runtime-manifest-root");
        EnsureNoDuplicateProperties(root, "$");
        Require(root.GetProperty("schemaVersion").GetInt32() == 1,
            "reference-runtime-manifest-schema");
        Require(root.GetProperty("mediaPipeVersion").GetString() == "0.10.35",
            "reference-runtime-manifest-mediapipe-version");
        Require(root.GetProperty("mediaPipeCommit").GetString() ==
                "f8ef212d5c962c0e853db7e59d217056b187084b",
            "reference-runtime-manifest-mediapipe-commit");
        Require(root.GetProperty("skiaSharpVersion").GetString() == "3.119.4",
            "reference-runtime-manifest-skia-version");
        Require(root.GetProperty("openCvVersion").GetString() == "3.4.10",
            "reference-runtime-manifest-opencv-version");
        Require(root.GetProperty("vcRuntimeVersion").GetString() ==
                "14.51.36247.0",
            "reference-runtime-manifest-vc-version");

        JsonElement assets = root.GetProperty("assets");
        Require(assets.ValueKind == JsonValueKind.Array,
            "reference-runtime-manifest-assets");
        Require(assets.GetArrayLength() == ExpectedAssets.Length,
            "reference-runtime-manifest-inventory");

        for (var index = 0; index < ExpectedAssets.Length; index++)
        {
            JsonElement row = assets[index];
            string relativePath = row.GetProperty("path").GetString() ??
                throw new InvalidOperationException(
                    "reference-runtime-manifest-empty-path");
            string role = row.GetProperty("role").GetString() ??
                throw new InvalidOperationException(
                    "reference-runtime-manifest-empty-role");
            Require(relativePath == ExpectedAssets[index].Path &&
                    role == ExpectedAssets[index].Role,
                $"reference-runtime-manifest-order:{index}");
            Require(relativePath == Path.GetFileName(relativePath) &&
                    !Path.IsPathRooted(relativePath) &&
                    !relativePath.Contains(':', StringComparison.Ordinal),
                $"reference-runtime-manifest-path:{relativePath}");

            string assetPath = Path.Combine(runtimeRoot, relativePath);
            Require(File.Exists(assetPath),
                $"reference-runtime-asset-missing:{relativePath}");
            FileInfo info = new(assetPath);
            Require(info.Length == row.GetProperty("length").GetInt64(),
                $"reference-runtime-asset-length:{relativePath}");
            Require(!IsReparsePoint(info),
                $"reference-runtime-asset-reparse:{relativePath}");
            string measuredHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(assetPath)));
            string declaredHash = row.GetProperty("sha256").GetString() ??
                throw new InvalidOperationException(
                    $"reference-runtime-asset-hash-empty:{relativePath}");
            Require(declaredHash.Length == 64 &&
                    declaredHash.All(IsUpperHex) &&
                    measuredHash == declaredHash,
                $"reference-runtime-asset-hash:{relativePath}");
        }

        string[] actualFiles = Directory.EnumerateFiles(
                runtimeRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Where(name => name != "runtime-asset-manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedFiles = ExpectedAssets
            .Select(item => item.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(actualFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal),
            "reference-runtime-manifest-undeclared-file");
        return Task.CompletedTask;
    }

    public static Task TestMediaPipeExports()
    {
        string projectRoot = FindProjectRoot();
        string libraryPath = Path.Combine(
            projectRoot,
            "runtime",
            "reference-preset",
            "libmediapipe.dll");
        Require(File.Exists(libraryPath),
            "reference-runtime-mediapipe-missing");

        if (!NativeLibrary.TryLoad(libraryPath, out nint library))
        {
            throw new InvalidOperationException(
                "reference-runtime-mediapipe-load");
        }

        try
        {
            foreach (string export in ExpectedMediaPipeExports)
            {
                Require(
                    NativeLibrary.TryGetExport(
                        library,
                        export,
                        out nint address) &&
                    address != nint.Zero,
                    $"reference-runtime-mediapipe-export:{export}");
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }

        return Task.CompletedTask;
    }

    private static string FindProjectRoot()
    {
        foreach (string start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            DirectoryInfo? current = new(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName, "Actorwright.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "reference-runtime-project-root-missing");
    }

    private static void EnsureNoDuplicateProperties(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name),
                    $"reference-runtime-manifest-duplicate:{path}.{property.Name}");
                EnsureNoDuplicateProperties(
                    property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static bool IsReparsePoint(FileInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsUpperHex(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F';

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }
}
