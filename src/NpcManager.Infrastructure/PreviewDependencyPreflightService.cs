using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.Infrastructure;

public sealed class PreviewDependencyPreflightService
{
    private const long MaximumDependencyBytes =
        1024L * 1024L * 1024L;

    public static PreviewDependencyPreflightResult AdmitNpcPreview(
        ApplicationResourceRuntimeAdmissionResult runtime,
        EmbeddedBlenderScriptId scriptId,
        Sha256Hash expectedScriptSha256,
        WorkspacePath blenderPath,
        Sha256Hash expectedBlenderSha256,
        WorkspacePath profileRoot,
        ApplicationResourcePath profileManifestPath,
        WorkspacePath texconvPath,
        Sha256Hash expectedTexconvSha256)
    {
        var authorities =
            ImmutableArray.CreateBuilder<PreviewDependencyAuthority>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddRuntime(runtime, authorities, diagnostics);
        AddScript(
            "npc-preview-render-script-hash",
            scriptId,
            expectedScriptSha256,
            authorities,
            diagnostics);
        AddExactFile(
            "npc-preview-blender-hash",
            "Blender",
            blenderPath,
            expectedBlenderSha256,
            authorities,
            diagnostics);
        AddNpcProfile(
            profileRoot,
            profileManifestPath,
            expectedBlenderSha256,
            authorities,
            diagnostics);
        AddExactFile(
            "npc-preview-texconv-hash",
            "Texconv",
            texconvPath,
            expectedTexconvSha256,
            authorities,
            diagnostics);
        return new(
            authorities.ToImmutable(),
            diagnostics.ToImmutable());
    }

    public static PreviewDependencyPreflightResult AdmitHairRegionsPreview(
        ApplicationResourceRuntimeAdmissionResult runtime,
        EmbeddedBlenderScriptId scriptId,
        Sha256Hash expectedScriptSha256,
        WorkspacePath blenderPath,
        Sha256Hash expectedBlenderSha256,
        WorkspacePath profileRoot,
        Sha256Hash expectedProfileFingerprint,
        WorkspacePath pyniflyArchivePath,
        Sha256Hash expectedPyniflySha256,
        WorkspacePath texconvPath,
        Sha256Hash expectedTexconvSha256)
    {
        var authorities =
            ImmutableArray.CreateBuilder<PreviewDependencyAuthority>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddRuntime(runtime, authorities, diagnostics);
        AddScript(
            "facegeom-hair-regions-render-script-preflight",
            scriptId,
            expectedScriptSha256,
            authorities,
            diagnostics);
        AddExactFile(
            "facegeom-hair-regions-blender-preflight",
            "Blender",
            blenderPath,
            expectedBlenderSha256,
            authorities,
            diagnostics);
        AddProfileFingerprint(
            profileRoot,
            expectedProfileFingerprint,
            authorities,
            diagnostics);
        AddExactFile(
            "facegeom-hair-regions-pynifly-preflight",
            "Pynifly archive",
            pyniflyArchivePath,
            expectedPyniflySha256,
            authorities,
            diagnostics);
        AddExactFile(
            "facegeom-hair-regions-texconv-preflight",
            "Texconv",
            texconvPath,
            expectedTexconvSha256,
            authorities,
            diagnostics);
        return new(
            authorities.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static void AddRuntime(
        ApplicationResourceRuntimeAdmissionResult runtime,
        ImmutableArray<PreviewDependencyAuthority>.Builder authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(runtime.Diagnostics);
        if (runtime.Authority is null)
            return;
        authorities.Add(new PreviewDependencyAuthority(
            "MediaPipe runtime",
            runtime.Authority.RuntimeRoot.Value,
            runtime.Authority.Manifest.ByteLength,
            runtime.Authority.ManifestSha256,
            ApplicationOwned: true));
    }

    private static void AddScript(
        string code,
        EmbeddedBlenderScriptId scriptId,
        Sha256Hash expectedHash,
        ImmutableArray<PreviewDependencyAuthority>.Builder authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            EmbeddedBlenderScript script =
                EmbeddedBlenderScriptBundle.Load(scriptId.Value);
            if (script.Sha256 != expectedHash)
            {
                diagnostics.Add(Error(
                    code,
                    $"Embedded renderer script '{scriptId.Value}' does not match its admitted SHA-256. " +
                    $"Expected {expectedHash.Value}; actual {script.Sha256.Value}."));
                return;
            }
            authorities.Add(new PreviewDependencyAuthority(
                "Embedded renderer script",
                $"assembly://NpcManager.Rendering/{script.EntryFileName}",
                script.SourceBytes.Length,
                script.Sha256,
                ApplicationOwned: true));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                ArgumentException)
        {
            diagnostics.Add(Error(
                code,
                $"Embedded renderer script '{scriptId.Value}' is unavailable: {exception.Message}"));
        }
    }

    private static void AddExactFile(
        string code,
        string role,
        WorkspacePath path,
        Sha256Hash expectedHash,
        ImmutableArray<PreviewDependencyAuthority>.Builder authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!IsOrdinaryFile(path.Value))
            {
                diagnostics.Add(Error(
                    code,
                    $"{role} is missing or not an ordinary file at '{path.Value}'."));
                return;
            }
            var info = new FileInfo(path.Value);
            if (info.Length > MaximumDependencyBytes)
                throw new InvalidDataException(
                    $"{role} exceeds the admitted byte limit.");
            Sha256Hash measured = HashFile(path.Value);
            if (measured != expectedHash)
            {
                diagnostics.Add(Error(
                    code,
                    $"{role} at '{path.Value}' does not match its admitted SHA-256."));
                return;
            }
            authorities.Add(new PreviewDependencyAuthority(
                role,
                path.Value,
                info.Length,
                measured,
                ApplicationOwned: false));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException)
        {
            diagnostics.Add(Error(
                code,
                $"{role} at '{path.Value}' could not be admitted: {exception.Message}"));
        }
    }

    private static void AddNpcProfile(
        WorkspacePath profileRoot,
        ApplicationResourcePath manifestPath,
        Sha256Hash expectedBlenderSha256,
        ImmutableArray<PreviewDependencyAuthority>.Builder authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        const string code = "npc-preview-profile-unavailable";
        try
        {
            if (!Directory.Exists(profileRoot.Value) ||
                IsReparse(profileRoot.Value))
            {
                diagnostics.Add(Error(
                    code,
                    $"Blender profile is missing or not an ordinary directory at '{profileRoot.Value}'."));
                return;
            }
            if (!IsOrdinaryFile(manifestPath.Value))
            {
                diagnostics.Add(Error(
                    code,
                    $"NPC profile manifest is unavailable at '{manifestPath.Value}'."));
                return;
            }

            byte[] bytes = File.ReadAllBytes(manifestPath.Value);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                new Sha256Hash(root.GetProperty(
                    "blenderSha256").GetString() ?? "") !=
                    expectedBlenderSha256)
                throw new InvalidDataException(
                    "The NPC profile manifest is not cross-bound to the admitted Blender executable.");

            JsonElement files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() == 0)
                throw new InvalidDataException(
                    "The NPC profile manifest file inventory is empty.");
            var declared = new HashSet<string>(StringComparer.Ordinal);
            long cumulative = 0;
            foreach (JsonElement row in files.EnumerateArray())
            {
                string relative = row.GetProperty("path").GetString() ?? "";
                if (Path.IsPathRooted(relative) ||
                    relative.Contains(':', StringComparison.Ordinal) ||
                    relative.Split('/').Any(part => part is "" or "." or "..") ||
                    !declared.Add(relative))
                    throw new InvalidDataException(
                        $"NPC profile manifest path is invalid: '{relative}'.");
                string path = Path.Combine(
                    profileRoot.Value,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (!IsOrdinaryFile(path))
                    throw new InvalidDataException(
                        $"NPC profile file is missing or not ordinary: '{path}'.");
                var info = new FileInfo(path);
                long expectedLength = row.GetProperty("length").GetInt64();
                Sha256Hash expectedHash = new(
                    row.GetProperty("sha256").GetString() ?? "");
                cumulative = checked(cumulative + info.Length);
                if (info.Length != expectedLength ||
                    cumulative > MaximumDependencyBytes ||
                    HashFile(path) != expectedHash)
                    throw new InvalidDataException(
                        $"NPC profile file drifted: '{path}'.");
            }

            string[] actual = Directory.EnumerateFiles(
                    profileRoot.Value,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !Path.GetRelativePath(profileRoot.Value, path)
                        .Split(Path.DirectorySeparatorChar)
                        .Contains("__pycache__", StringComparer.Ordinal) &&
                    !Path.GetExtension(path).Equals(
                        ".pyc",
                        StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(
                        profileRoot.Value,
                        path)
                    .Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actual.SequenceEqual(
                    declared.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw new InvalidDataException(
                    "The NPC profile contains undeclared files or omits declared files.");
            Sha256Hash manifestHash = new(Convert.ToHexString(
                SHA256.HashData(bytes)));
            authorities.Add(new PreviewDependencyAuthority(
                "Blender profile",
                profileRoot.Value,
                cumulative,
                manifestHash,
                ApplicationOwned: false));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                KeyNotFoundException or
                ArgumentException or
                OverflowException)
        {
            diagnostics.Add(Error(
                code,
                $"Blender profile at '{profileRoot.Value}' could not be admitted: {exception.Message}"));
        }
    }

    private static void AddProfileFingerprint(
        WorkspacePath profileRoot,
        Sha256Hash expectedFingerprint,
        ImmutableArray<PreviewDependencyAuthority>.Builder authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        const string code =
            "facegeom-hair-regions-pynifly-profile-preflight";
        string addonRoot = Path.Combine(
            profileRoot.Value,
            "scripts",
            "addons",
            "io_scene_nifly");
        try
        {
            if (!Directory.Exists(addonRoot) || IsReparse(addonRoot))
                throw new InvalidDataException(
                    $"The PyNifly addon root is absent or not ordinary: '{addonRoot}'.");
            string[] files = Directory.EnumerateFiles(
                    addonRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !Path.GetRelativePath(addonRoot, path)
                        .Split(Path.DirectorySeparatorChar)
                        .Contains("__pycache__", StringComparer.Ordinal) &&
                    !Path.GetExtension(path).Equals(
                        ".pyc",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(
                        addonRoot,
                        path)
                    .Replace('\\', '/'), StringComparer.Ordinal)
                .ToArray();
            using IncrementalHash fingerprint =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long cumulative = 0;
            foreach (string file in files)
            {
                if (!IsOrdinaryFile(file))
                    throw new InvalidDataException(
                        $"PyNifly profile file is not ordinary: '{file}'.");
                var info = new FileInfo(file);
                cumulative = checked(cumulative + info.Length);
                if (cumulative > MaximumDependencyBytes)
                    throw new InvalidDataException(
                        "The PyNifly profile exceeds the admitted byte limit.");
                string relative = Path.GetRelativePath(addonRoot, file)
                    .Replace('\\', '/');
                fingerprint.AppendData(Encoding.UTF8.GetBytes(
                    $"{relative}\0{info.Length}\0"));
                fingerprint.AppendData(Convert.FromHexString(
                    HashFile(file).Value));
            }
            Sha256Hash measured = new(Convert.ToHexString(
                fingerprint.GetHashAndReset()));
            if (files.Length == 0 || measured != expectedFingerprint)
                throw new InvalidDataException(
                    "The PyNifly profile inventory does not match its admitted fingerprint.");
            authorities.Add(new PreviewDependencyAuthority(
                "PyNifly Blender profile",
                profileRoot.Value,
                cumulative,
                measured,
                ApplicationOwned: false));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                OverflowException)
        {
            diagnostics.Add(Error(
                code,
                $"PyNifly Blender profile at '{profileRoot.Value}' could not be admitted: {exception.Message}"));
        }
    }

    private static bool IsOrdinaryFile(string path) =>
        File.Exists(path) &&
        !Directory.Exists(path) &&
        !IsReparse(path);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
