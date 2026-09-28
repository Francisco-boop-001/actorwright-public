using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Copies an already verified pipeline package into a new K-local package root.</summary>
public sealed class PackageBuildService(
    IPackageVerifyService verifier,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IPackageBuildService
{
    public async ValueTask<PackageBuildResult> BuildAsync(
        PackageBuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRoots(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var manifestPath = new WorkspacePath(Path.Combine(request.SourceRoot.Value, "npcmanager-package.json"));
        var sourceVerification = await verifier.VerifyAsync(new PackageVerifyRequest(manifestPath), cancellationToken);
        diagnostics.AddRange(sourceVerification.Diagnostics);
        if (!sourceVerification.Verified || sourceVerification.Artifact is null)
            return Refused(diagnostics);

        var identityManifest = sourceVerification.Artifact.ManifestPath;
        if (!string.Equals(Path.GetFullPath(identityManifest.Value), Path.GetFullPath(manifestPath.Value),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("package-build-manifest-root", DiagnosticSeverity.Error,
                "The source package manifest must be the root npcmanager-package.json file."));
            return Refused(diagnostics);
        }

        var temporaryRoot = request.OutputRoot.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var copied = ImmutableArray.CreateBuilder<PackageManifestFile>(sourceVerification.Artifact.Files.Length);
            var sourceFiles = sourceVerification.Artifact.Files
                .Select(item => item.RelativePath)
                .Append(new AssetPath("npcmanager-package.json"));
            foreach (var relative in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(Path.Combine(request.SourceRoot.Value,
                    relative.Value.Replace('/', Path.DirectorySeparatorChar)));
                var destination = Path.GetFullPath(Path.Combine(temporaryRoot,
                    relative.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsUnder(source, request.SourceRoot.Value) || !IsUnder(destination, temporaryRoot))
                {
                    diagnostics.Add(new Diagnostic("package-build-path-escape", DiagnosticSeverity.Error,
                        $"Package file '{relative.Value}' escaped its source or destination root."));
                    return Refused(diagnostics);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (relative.Value == "npcmanager-package.json")
                {
                    await CopyFileAsync(source, destination, cancellationToken);
                    continue;
                }

                var hash = await CopyAndHashAsync(source, destination, cancellationToken);
                var expected = sourceVerification.Artifact.Files.Single(item => item.RelativePath == relative);
                var size = new FileInfo(destination).Length;
                if (size != expected.ExpectedByteLength ||
                    !string.Equals(hash.Value, expected.ExpectedSha256.Value, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new Diagnostic("package-build-copy-mismatch", DiagnosticSeverity.Error,
                        $"Package file '{relative.Value}' changed while it was copied."));
                    return Refused(diagnostics);
                }
                copied.Add(new PackageManifestFile(expected.Kind, relative, size, hash));
            }

            var temporaryVerification = await verifier.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(Path.Combine(temporaryRoot, "npcmanager-package.json"))),
                cancellationToken);
            diagnostics.AddRange(temporaryVerification.Diagnostics);
            if (!temporaryVerification.Verified)
                return Refused(diagnostics);

            var artifact = new PackageBuildArtifact("1", "npcmanager-package-build",
                request.SourceRoot, request.OutputRoot, copied.ToImmutable(), true, false);
            Directory.Move(temporaryRoot, request.OutputRoot.Value);
            temporaryRoot = string.Empty;
            return new PackageBuildResult(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("package-build-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("package-build-write-denied", DiagnosticSeverity.Error, exception.Message));
        }
        finally
        {
            if (temporaryRoot.Length > 0) TryDeleteDirectory(temporaryRoot);
        }
        return Refused(diagnostics);
    }

    private void ValidateRoots(PackageBuildRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.SourceRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("package-build-source-outside-lab", DiagnosticSeverity.Error,
                "The package source root must remain under the K-only lab root."));
        if (!Directory.Exists(request.SourceRoot.Value))
            diagnostics.Add(new Diagnostic("package-build-source-missing", DiagnosticSeverity.Error,
                "The package source root does not exist."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourceRoot));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("package-build-output-outside-lab", DiagnosticSeverity.Error,
                "The package output root must remain under the K-only lab root."));
        if (request.OutputRoot.IsUnder(request.SourceRoot) || request.SourceRoot.IsUnder(request.OutputRoot))
            diagnostics.Add(new Diagnostic("package-build-root-overlap", DiagnosticSeverity.Error,
                "Package source and output roots must be distinct and non-overlapping."));
        if (File.Exists(request.OutputRoot.Value) || Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("package-build-output-exists", DiagnosticSeverity.Error,
                "Package builds never overwrite an existing output."));
        var parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("package-build-parent-missing", DiagnosticSeverity.Error,
                "The package output parent must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async ValueTask<Sha256Hash> CopyAndHashAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PackageBuildResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
