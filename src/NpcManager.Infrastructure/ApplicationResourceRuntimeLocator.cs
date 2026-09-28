using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class ApplicationResourceRuntimeLocator
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

    private readonly ApplicationResourcePath _resourceBase;

    public ApplicationResourceRuntimeLocator(
        ApplicationResourcePath resourceBase)
    {
        _resourceBase = resourceBase;
    }

    public ApplicationResourceRuntimeAdmissionResult
        AdmitReferencePresetRuntime()
    {
        var runtimeRoot = new ApplicationResourcePath(Path.Combine(
            _resourceBase.Value,
            "runtime",
            "reference-preset"));
        try
        {
            if (!Directory.Exists(runtimeRoot.Value) ||
                !runtimeRoot.IsUnder(_resourceBase) ||
                HasReparseAncestor(runtimeRoot.Value, _resourceBase.Value))
                return Refused(
                    runtimeRoot,
                    "The expected runtime directory is absent, escapes the application resource base, or crosses a reparse point.");

            string manifestPath = Path.Combine(
                runtimeRoot.Value,
                "runtime-asset-manifest.json");
            if (!IsOrdinaryFile(manifestPath))
                return Refused(
                    runtimeRoot,
                    $"The runtime manifest is absent or not an ordinary file: '{manifestPath}'.");

            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            Sha256Hash manifestHash = Hash(manifestBytes);
            using JsonDocument document = JsonDocument.Parse(
                manifestBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            EnsureNoDuplicateProperties(root, "$");
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("schemaVersion").GetInt32() != 1 ||
                !string.Equals(
                    root.GetProperty("runtimeArchitecture").GetString(),
                    "windows-x64",
                    StringComparison.Ordinal) ||
                !root.GetProperty("offline").GetBoolean() ||
                !root.GetProperty("cpuOnly").GetBoolean())
                return Refused(
                    runtimeRoot,
                    "The runtime manifest architecture or offline CPU contract is not admitted.");

            JsonElement assets = root.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array ||
                assets.GetArrayLength() != ExpectedAssets.Length)
                return Refused(
                    runtimeRoot,
                    "The runtime manifest inventory is not exact.");

            var authorities =
                ImmutableArray.CreateBuilder<ApplicationResourceAuthority>();
            for (var index = 0; index < ExpectedAssets.Length; index++)
            {
                JsonElement row = assets[index];
                string relative = row.GetProperty("path").GetString() ?? "";
                string role = row.GetProperty("role").GetString() ?? "";
                if (!string.Equals(
                        relative,
                        ExpectedAssets[index].Path,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        role,
                        ExpectedAssets[index].Role,
                        StringComparison.Ordinal) ||
                    relative != Path.GetFileName(relative) ||
                    Path.IsPathRooted(relative) ||
                    relative.Contains(':', StringComparison.Ordinal))
                    return Refused(
                        runtimeRoot,
                        $"Runtime manifest row {index} is not admitted.");

                string path = Path.Combine(runtimeRoot.Value, relative);
                if (!IsOrdinaryFile(path))
                    return Refused(
                        runtimeRoot,
                        $"Runtime asset '{path}' is absent or not an ordinary file.");
                var info = new FileInfo(path);
                long length = row.GetProperty("length").GetInt64();
                var expectedHash = new Sha256Hash(
                    row.GetProperty("sha256").GetString() ?? "");
                Sha256Hash measuredHash = HashFile(path);
                if (info.Length != length || measuredHash != expectedHash)
                    return Refused(
                        runtimeRoot,
                        $"Runtime asset '{path}' does not match its declared length or SHA-256.");
                authorities.Add(new ApplicationResourceAuthority(
                    role,
                    new ApplicationResourcePath(path),
                    length,
                    measuredHash,
                    manifestHash));
            }

            string[] actual = Directory.EnumerateFiles(
                    runtimeRoot.Value,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(item => item is not null)
                .Select(item => item!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expected = ExpectedAssets
                .Select(item => item.Path)
                .Append("runtime-asset-manifest.json")
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal) ||
                Directory.EnumerateDirectories(
                    runtimeRoot.Value,
                    "*",
                    SearchOption.TopDirectoryOnly).Any())
                return Refused(
                    runtimeRoot,
                    "The runtime directory contains undeclared files or directories.");

            var manifestAuthority = new ApplicationResourceAuthority(
                "reference-runtime-manifest",
                new ApplicationResourcePath(manifestPath),
                manifestBytes.LongLength,
                manifestHash,
                manifestHash);
            return new ApplicationResourceRuntimeAdmissionResult(
                new ApplicationResourceRuntimeAuthority(
                    _resourceBase,
                    runtimeRoot,
                    manifestAuthority,
                    authorities.ToImmutable()),
                []);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                KeyNotFoundException or
                InvalidOperationException or
                ArgumentException)
        {
            return Refused(
                runtimeRoot,
                $"The runtime authority could not be verified: {exception.Message}");
        }
    }

    private static ApplicationResourceRuntimeAdmissionResult Refused(
        ApplicationResourcePath runtimeRoot,
        string reason) =>
        new(
            null,
            ImmutableArray.Create(new Diagnostic(
                "reference-runtime-unavailable",
                DiagnosticSeverity.Error,
                $"Reference runtime unavailable at '{runtimeRoot.Value}': {reason}")));

    private static bool IsOrdinaryFile(string path) =>
        File.Exists(path) &&
        !Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    internal static bool HasReparseAncestor(string path, string root)
    {
        DirectoryInfo? current = new(path);
        string boundary = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(
                    current.FullName.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    boundary,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            current = current.Parent;
        }
        return true;
    }

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void EnsureNoDuplicateProperties(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate property '{path}.{property.Name}'.");
                EnsureNoDuplicateProperties(
                    property.Value,
                    $"{path}.{property.Name}");
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
}
