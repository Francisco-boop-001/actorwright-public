using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Installs a packaged apply PEX into a copied K-local Data root.</summary>
/// <remarks>
/// Deployment is intentionally narrower than game execution: the protected live root is rejected,
/// identical files are treated as an idempotent success, and differing files never get overwritten.
/// </remarks>
public sealed class RuntimeScriptDeployService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRuntimeScriptDeployService
{
    private const long MaxManifestBytes = 1 * 1024 * 1024;
    private const long MaxPexBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<RuntimeScriptDeployResult> DeployAsync(RuntimeScriptDeployRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(request, diagnostics);

        RuntimeScriptPackageArtifact artifact;
        try
        {
            artifact = await ReadArtifactAsync(request.PackageManifest.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-manifest-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-manifest-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }

        var script = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        var packageRoot = Path.GetDirectoryName(request.PackageManifest.Value)!;
        var source = Path.Combine(packageRoot, "Data", "Scripts", script + ".pex");
        var destination = Path.Combine(request.DataRoot.Value, "Scripts", script + ".pex");
        ValidateArtifact(request, artifact, script, packageRoot, source, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(request, diagnostics);

        Sha256Hash expectedHash;
        try { expectedHash = new Sha256Hash(artifact.InstalledSha256); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-hash-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }
        byte[] sourceBytes;
        try
        {
            sourceBytes = await File.ReadAllBytesAsync(source, cancellationToken);
            if (sourceBytes.LongLength != artifact.InstalledByteLength || sourceBytes.LongLength > MaxPexBytes)
            {
                diagnostics.Add(new Diagnostic("runtime-script-deploy-source-size", DiagnosticSeverity.Error,
                    "The packaged PEX size does not match its manifest or the safety limit."));
                return Refused(request, diagnostics);
            }
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(sourceBytes)));
            if (sourceHash != expectedHash)
            {
                diagnostics.Add(new Diagnostic("runtime-script-deploy-source-hash", DiagnosticSeverity.Error,
                    "The packaged PEX bytes do not match the manifest hash."));
                return Refused(request, diagnostics);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-source-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-source-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, diagnostics);
        }

        var scriptsDirectory = Path.GetDirectoryName(destination)!;
        var createdScriptsDirectory = false;
        var createdDestination = false;
        var keepDestination = false;
        try
        {
            if (Directory.Exists(scriptsDirectory))
            {
                if (File.GetAttributes(scriptsDirectory).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("runtime-script-deploy-scripts-reparse", DiagnosticSeverity.Error,
                        "The copied Data\\Scripts directory may not be a reparse point."));
                    return Refused(request, diagnostics);
                }
            }
            else
            {
                Directory.CreateDirectory(scriptsDirectory);
                createdScriptsDirectory = true;
            }

            if (File.Exists(destination))
            {
                if (File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("runtime-script-deploy-destination-reparse", DiagnosticSeverity.Error,
                        "The destination PEX may not be a reparse point."));
                    return Refused(request, diagnostics);
                }
                var existingHash = await HashAsync(destination, cancellationToken);
                if (existingHash == expectedHash && new FileInfo(destination).Length == artifact.InstalledByteLength)
                    return new RuntimeScriptDeployResult(false, true, request.DataRoot,
                        new WorkspacePath(destination), existingHash, diagnostics.ToImmutable());
                diagnostics.Add(new Diagnostic("runtime-script-deploy-destination-conflict", DiagnosticSeverity.Error,
                    "A different apply PEX already exists; deployment never overwrites it."));
                return Refused(request, diagnostics);
            }

            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await WriteNewFileAsync(temporary, sourceBytes, cancellationToken);
                var stagedHash = await HashAsync(temporary, cancellationToken);
                if (stagedHash != expectedHash)
                {
                    diagnostics.Add(new Diagnostic("runtime-script-deploy-staged-hash", DiagnosticSeverity.Error,
                        "The staged deployment bytes do not match the package hash."));
                    return Refused(request, diagnostics);
                }
                File.Move(temporary, destination, overwrite: false);
                createdDestination = true;
            }
            finally { TryDelete(temporary); }

            var outputHash = await HashAsync(destination, cancellationToken);
            if (outputHash != expectedHash)
            {
                diagnostics.Add(new Diagnostic("runtime-script-deploy-output-hash", DiagnosticSeverity.Error,
                    "The deployed PEX bytes do not match the package hash."));
                return Refused(request, diagnostics);
            }
            keepDestination = true;
            return new RuntimeScriptDeployResult(true, false, request.DataRoot,
                new WorkspacePath(destination), outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-write-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-deploy-write-denied", DiagnosticSeverity.Error,
                exception.Message));
        }
        finally
        {
            if (createdDestination && !keepDestination) TryDelete(destination);
            if (createdScriptsDirectory) TryDeleteEmptyDirectory(scriptsDirectory);
        }

        return Refused(request, diagnostics);
    }

    private void ValidatePaths(RuntimeScriptDeployRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.PackageManifest.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-package-outside-lab", DiagnosticSeverity.Error,
                "The package manifest must remain under the K-only lab root."));
        if (!File.Exists(request.PackageManifest.Value))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-package-missing", DiagnosticSeverity.Error,
                "The package manifest does not exist."));
        else if (File.GetAttributes(request.PackageManifest.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-package-reparse", DiagnosticSeverity.Error,
                "The package manifest may not be a reparse point."));
        var packageParent = Path.GetDirectoryName(request.PackageManifest.Value);
        if (packageParent is null || ContainsReparseBetween(labRoot.Value, packageParent))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-package-path-reparse", DiagnosticSeverity.Error,
                "The package path contains a reparse point."));

        if (!request.DataRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-data-outside-lab", DiagnosticSeverity.Error,
                "The copied Data root must remain under the K-only lab root."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-data-missing", DiagnosticSeverity.Error,
                "The copied Data root does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
            if (ContainsReparseBetween(labRoot.Value, request.DataRoot.Value))
                diagnostics.Add(new Diagnostic("runtime-script-deploy-data-reparse", DiagnosticSeverity.Error,
                    "The copied Data root contains a reparse point."));
        }
    }

    private static async ValueTask<RuntimeScriptPackageArtifact> ReadArtifactAsync(string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxManifestBytes)
            throw new JsonException("The runtime-script package manifest exceeds the 1 MiB safety limit.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RuntimeScriptPackageArtifact>(stream, JsonOptions,
                   cancellationToken) ?? throw new JsonException("The runtime-script package manifest is null.");
    }

    private static void ValidateArtifact(RuntimeScriptDeployRequest request, RuntimeScriptPackageArtifact artifact,
        string script, string packageRoot, string source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (artifact.SchemaVersion != "1" || artifact.ArtifactKind != "npc-apply-script-package")
            diagnostics.Add(new Diagnostic("runtime-script-deploy-artifact-kind", DiagnosticSeverity.Error,
                "The package manifest is not a supported runtime-script package."));
        if (artifact.Edition != request.Edition.ToWireName() || artifact.ScriptName != script)
            diagnostics.Add(new Diagnostic("runtime-script-deploy-game-mismatch", DiagnosticSeverity.Error,
                "The package edition or script does not match the requested game."));
        if (artifact.InstalledRelativePath != "Data/Scripts/" + script + ".pex")
            diagnostics.Add(new Diagnostic("runtime-script-deploy-relative-path", DiagnosticSeverity.Error,
                "The package manifest does not name the expected Data/Scripts apply PEX."));
        if (string.IsNullOrWhiteSpace(artifact.InstalledSha256) || string.IsNullOrWhiteSpace(artifact.SourcePexSha256) ||
            !string.Equals(artifact.InstalledSha256, artifact.SourcePexSha256, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-hash-binding", DiagnosticSeverity.Error,
                "The package manifest must bind the installed PEX hash to the source PEX hash."));
        var expectedPath = Path.GetFullPath(source);
        var pathBound = false;
        if (!string.IsNullOrWhiteSpace(artifact.InstalledPath))
        {
            try
            {
                pathBound = string.Equals(Path.GetFullPath(artifact.InstalledPath), expectedPath,
                    StringComparison.OrdinalIgnoreCase) &&
                    new WorkspacePath(expectedPath).IsUnder(new WorkspacePath(packageRoot));
            }
            catch (ArgumentException) { pathBound = false; }
        }
        if (!pathBound)
            diagnostics.Add(new Diagnostic("runtime-script-deploy-path-binding", DiagnosticSeverity.Error,
                "The package manifest installed path is not bound to its package root."));
        if (!File.Exists(source) || File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("runtime-script-deploy-source-missing", DiagnosticSeverity.Error,
                "The package apply PEX is missing or is a reparse point."));
    }

    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
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

    private static RuntimeScriptDeployResult Refused(RuntimeScriptDeployRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, request.DataRoot, new WorkspacePath(Path.Combine(request.DataRoot.Value, "Scripts")), null,
            diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
