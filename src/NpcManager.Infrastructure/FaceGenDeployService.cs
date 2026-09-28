using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Deploys every file declared by a hash-bound FaceGen pack manifest into an
/// explicit copied K-local Data root.
/// </summary>
/// <remarks>
/// All source files are preflighted before any destination is touched. Existing
/// identical files are idempotent; any differing destination refuses the whole
/// request. This is copied-root deployment evidence, not game-loadability or
/// runtime-appearance authority.
/// </remarks>
public sealed class FaceGenDeployService(IWorkspacePolicy policy, WorkspacePath labRoot) : IFaceGenDeployService
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new Sha256HashJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenDeployResult> DeployAsync(FaceGenDeployRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var files = ImmutableArray.CreateBuilder<FaceGenDeployFileResult>();
        ValidateRoots(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(request, files, diagnostics);

        FaceGenPackArtifact artifact;
        try
        {
            artifact = await ReadManifestAsync(request.PackageManifest.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-manifest-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, files, diagnostics);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, files, diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-manifest-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, files, diagnostics);
        }

        var packageRoot = new WorkspacePath(Path.GetDirectoryName(request.PackageManifest.Value)!);
        ValidateArtifact(request, artifact, packageRoot, diagnostics);
        if (HasErrors(diagnostics)) return Refused(request, files, diagnostics);

        var prepared = new List<PreparedFile>(artifact.Files.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in artifact.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryResolveEntry(entry, packageRoot, request.DataRoot, seen, diagnostics,
                        out var source, out var destination, out var relativePath))
                    continue;
                if (!File.Exists(source.Value) || File.GetAttributes(source.Value).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("facegen-deploy-source-missing-or-reparse", DiagnosticSeverity.Error,
                        $"The package source '{entry.RelativePath}' is missing or is a reparse point."));
                    continue;
                }

                var sourceInfo = new FileInfo(source.Value);
                if (sourceInfo.Length <= 0 || sourceInfo.Length > MaximumFileBytes || sourceInfo.Length != entry.Size)
                {
                    diagnostics.Add(new Diagnostic("facegen-deploy-source-size-mismatch", DiagnosticSeverity.Error,
                        $"The package source '{entry.RelativePath}' does not match its declared size."));
                    continue;
                }

                var sourceHash = await HashAsync(source.Value, cancellationToken);
                if (sourceHash != entry.Sha256)
                {
                    diagnostics.Add(new Diagnostic("facegen-deploy-source-hash-mismatch", DiagnosticSeverity.Error,
                        $"The package source '{entry.RelativePath}' does not match its declared SHA-256."));
                    continue;
                }

                Sha256Hash? existingHash = null;
                if (File.Exists(destination.Value))
                {
                    if (File.GetAttributes(destination.Value).HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(new Diagnostic("facegen-deploy-destination-reparse", DiagnosticSeverity.Error,
                            $"The destination '{relativePath}' may not be a reparse point."));
                        continue;
                    }
                    var destinationInfo = new FileInfo(destination.Value);
                    existingHash = await HashAsync(destination.Value, cancellationToken);
                    if (destinationInfo.Length != sourceInfo.Length || existingHash != sourceHash)
                    {
                        diagnostics.Add(new Diagnostic("facegen-deploy-destination-conflict", DiagnosticSeverity.Error,
                            $"A different file already exists at '{relativePath}'; deployment never overwrites it."));
                        continue;
                    }
                }
                else if (Directory.Exists(destination.Value))
                {
                    diagnostics.Add(new Diagnostic("facegen-deploy-destination-directory-conflict", DiagnosticSeverity.Error,
                        $"A directory already exists where '{relativePath}' must be deployed."));
                    continue;
                }

                prepared.Add(new PreparedFile(relativePath, source, destination, sourceInfo.Length,
                    sourceHash, existingHash));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-preflight-read-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-preflight-read-denied", DiagnosticSeverity.Error,
                exception.Message));
        }

        if (HasErrors(diagnostics)) return Refused(request, files, diagnostics);

        try
        {
            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ExistingHash is { } existing)
                {
                    files.Add(new FaceGenDeployFileResult(item.RelativePath, item.Destination, item.Length,
                        item.SourceHash, existing, false, true));
                    continue;
                }

                var parent = Path.GetDirectoryName(item.Destination.Value)!;
                EnsureDirectoryChain(parent, request.DataRoot.Value, diagnostics);
                if (HasErrors(diagnostics)) break;
                var temporary = item.Destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await CopyAndVerifyAsync(item.Source.Value, temporary, item.SourceHash, item.Length,
                        cancellationToken);
                    File.Move(temporary, item.Destination.Value, overwrite: false);
                }
                finally { TryDelete(temporary); }

                var destinationHash = await HashAsync(item.Destination.Value, cancellationToken);
                if (destinationHash != item.SourceHash)
                {
                    diagnostics.Add(new Diagnostic("facegen-deploy-output-hash-mismatch", DiagnosticSeverity.Error,
                        $"The deployed file '{item.RelativePath}' does not match the package hash."));
                    break;
                }
                files.Add(new FaceGenDeployFileResult(item.RelativePath, item.Destination, item.Length,
                    item.SourceHash, destinationHash, true, false));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-write-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-write-denied", DiagnosticSeverity.Error,
                exception.Message));
        }

        if (HasErrors(diagnostics))
            return Refused(request, files, diagnostics);

        var deployed = files.Any(item => item.Deployed);
        var alreadyPresent = !deployed && files.Count == prepared.Count && files.Count > 0;
        return new FaceGenDeployResult(deployed, alreadyPresent, request.Edition, request.PackageManifest,
            request.DataRoot, files.ToImmutable(), diagnostics.ToImmutable());
    }

    private void ValidateRoots(FaceGenDeployRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.PackageManifest.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facegen-deploy-package-outside-lab", DiagnosticSeverity.Error,
                "The FaceGen package manifest must remain under the K-only lab root."));
        if (!File.Exists(request.PackageManifest.Value))
            diagnostics.Add(new Diagnostic("facegen-deploy-package-missing", DiagnosticSeverity.Error,
                "The FaceGen package manifest does not exist."));
        else if (File.GetAttributes(request.PackageManifest.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("facegen-deploy-package-reparse", DiagnosticSeverity.Error,
                "The FaceGen package manifest may not be a reparse point."));
        var packageParent = Path.GetDirectoryName(request.PackageManifest.Value);
        if (packageParent is null || ContainsReparseBetween(labRoot.Value, packageParent))
            diagnostics.Add(new Diagnostic("facegen-deploy-package-path-reparse", DiagnosticSeverity.Error,
                "The FaceGen package path contains a reparse point."));

        if (!request.DataRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facegen-deploy-data-outside-lab", DiagnosticSeverity.Error,
                "The copied Data root must remain under the K-only lab root."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("facegen-deploy-data-missing", DiagnosticSeverity.Error,
                "The copied Data root does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
            if (ContainsReparseBetween(labRoot.Value, request.DataRoot.Value))
                diagnostics.Add(new Diagnostic("facegen-deploy-data-reparse", DiagnosticSeverity.Error,
                    "The copied Data root contains a reparse point."));
        }
        if (packageParent is not null && Directory.Exists(packageParent))
        {
            var packageRoot = new WorkspacePath(packageParent);
            if (packageRoot.IsUnder(request.DataRoot) || request.DataRoot.IsUnder(packageRoot))
                diagnostics.Add(new Diagnostic("facegen-deploy-root-overlap", DiagnosticSeverity.Error,
                    "The FaceGen package root and destination Data root must be distinct and non-overlapping."));
        }
    }

    private static void ValidateArtifact(FaceGenDeployRequest request, FaceGenPackArtifact artifact,
        WorkspacePath packageRoot, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (artifact.SchemaVersion != "1" || artifact.ArtifactKind != "facegen-pack")
            diagnostics.Add(new Diagnostic("facegen-deploy-artifact-kind", DiagnosticSeverity.Error,
                "The manifest is not a supported FaceGen pack."));
        if (artifact.Edition != request.Edition.ToWireName())
            diagnostics.Add(new Diagnostic("facegen-deploy-game-mismatch", DiagnosticSeverity.Error,
                "The FaceGen pack edition does not match the requested game."));
        if (!artifact.NoWriteToSource || artifact.RuntimeProof)
            diagnostics.Add(new Diagnostic("facegen-deploy-boundary-flags", DiagnosticSeverity.Error,
                "The FaceGen pack must declare no source mutation and no runtime proof."));
        if (artifact.Files.IsDefaultOrEmpty || artifact.Files.Length > MaximumFiles)
            diagnostics.Add(new Diagnostic("facegen-deploy-file-count", DiagnosticSeverity.Error,
                "The FaceGen pack must contain between one and 128 files."));
        if (string.IsNullOrWhiteSpace(artifact.OutputRoot))
            diagnostics.Add(new Diagnostic("facegen-deploy-output-root-missing", DiagnosticSeverity.Error,
                "The FaceGen pack output root is required for path binding."));
        else
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(artifact.OutputRoot), packageRoot.Value,
                        StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(new Diagnostic("facegen-deploy-output-root-binding", DiagnosticSeverity.Error,
                        "The FaceGen pack output root is not bound to the manifest directory."));
            }
            catch (ArgumentException)
            {
                diagnostics.Add(new Diagnostic("facegen-deploy-output-root-invalid", DiagnosticSeverity.Error,
                    "The FaceGen pack output root is invalid."));
            }
        }
    }

    private static bool TryResolveEntry(FaceGenPackFileArtifact entry, WorkspacePath packageRoot,
        WorkspacePath dataRoot, HashSet<string> seen, ImmutableArray<Diagnostic>.Builder diagnostics,
        out WorkspacePath source, out WorkspacePath destination, out string relativePath)
    {
        source = packageRoot;
        destination = dataRoot;
        relativePath = entry.RelativePath;
        if (!IsSafeDataRelative(entry.RelativePath) || !IsSafeDataRelative(entry.SourcePath))
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-relative-path-invalid", DiagnosticSeverity.Error,
                $"The FaceGen pack contains an unsafe relative path '{entry.RelativePath}'."));
            return false;
        }
        if (!seen.Add(entry.RelativePath))
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-duplicate-path", DiagnosticSeverity.Error,
                $"The FaceGen pack contains duplicate path '{entry.RelativePath}'."));
            return false;
        }
        try
        {
            var packageFile = Path.GetFullPath(Path.Combine(packageRoot.Value,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var destinationFile = Path.GetFullPath(Path.Combine(dataRoot.Value,
                entry.RelativePath["Data/".Length..].Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(packageFile, packageRoot.Value) || !IsUnder(destinationFile, dataRoot.Value))
            {
                diagnostics.Add(new Diagnostic("facegen-deploy-path-escape", DiagnosticSeverity.Error,
                    $"The FaceGen pack path '{entry.RelativePath}' escaped its root."));
                return false;
            }
            source = new WorkspacePath(packageFile);
            destination = new WorkspacePath(destinationFile);
            relativePath = entry.RelativePath["Data/".Length..];
            return true;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-path-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return false;
        }
    }

    private static bool IsSafeDataRelative(string value) =>
        value.StartsWith("Data/", StringComparison.Ordinal) &&
        !value.Contains('\\') && !value.Contains("//", StringComparison.Ordinal) &&
        value.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not "." and not ".." &&
                                                !segment.Contains(':'));

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static async ValueTask<FaceGenPackArtifact> ReadManifestAsync(string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumManifestBytes)
            throw new JsonException("The FaceGen pack manifest exceeds the 4 MiB safety limit.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FaceGenPackArtifact>(stream, JsonOptions,
                   cancellationToken) ?? throw new JsonException("The FaceGen pack manifest is null.");
    }

    private static async ValueTask CopyAndVerifyAsync(string source, string temporary,
        Sha256Hash expectedHash, long expectedLength, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                           128 * 1024, FileOptions.SequentialScan))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           128 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 128 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        var info = new FileInfo(temporary);
        var hash = await HashAsync(temporary, cancellationToken);
        if (info.Length != expectedLength || hash != expectedHash)
            throw new IOException("The staged FaceGen file does not match its package binding.");
    }

    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static void EnsureDirectoryChain(string path, string root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!IsUnder(path, root))
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-directory-escape", DiagnosticSeverity.Error,
                "The FaceGen destination directory escaped the copied Data root."));
            return;
        }
        var missing = new Stack<string>();
        var current = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!Directory.Exists(current) && !string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
        {
            diagnostics.Add(new Diagnostic("facegen-deploy-directory-reparse", DiagnosticSeverity.Error,
                "The FaceGen destination directory contains a reparse point."));
            return;
        }
        foreach (var directory in missing)
        {
            Directory.CreateDirectory(directory);
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("facegen-deploy-directory-reparse", DiagnosticSeverity.Error,
                    "A created FaceGen destination directory became a reparse point."));
                return;
            }
        }
    }

    private static bool ContainsReparseBetween(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (current.Length >= rootFull.Length)
        {
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGenDeployResult Refused(FaceGenDeployRequest request,
        ImmutableArray<FaceGenDeployFileResult>.Builder files,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, request.Edition, request.PackageManifest, request.DataRoot,
            files.ToImmutable(), diagnostics.ToImmutable());

    private sealed record PreparedFile(
        string RelativePath,
        WorkspacePath Source,
        WorkspacePath Destination,
        long Length,
        Sha256Hash SourceHash,
        Sha256Hash? ExistingHash);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Sha256HashJsonConverter : JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return new Sha256Hash(reader.GetString() ?? string.Empty);
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("SHA-256 values must be strings or objects with a value field.");
            using var document = JsonDocument.ParseValue(ref reader);
            if (!document.RootElement.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String)
                throw new JsonException("SHA-256 object is missing its value field.");
            return new Sha256Hash(value.GetString() ?? string.Empty);
        }

        public override void Write(Utf8JsonWriter writer, Sha256Hash value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Value);
        }
    }
}
