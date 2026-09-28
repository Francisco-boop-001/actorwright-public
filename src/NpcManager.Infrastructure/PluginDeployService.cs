using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Copies one hash-bound Bethesda plugin into an explicit copied K-local Data root.
/// </summary>
/// <remarks>
/// The destination is never overwritten. A byte-identical destination is an idempotent
/// success; a different destination is a validation failure. This service does not
/// infer a live load order and never writes to a game installation.
/// </remarks>
public sealed class PluginDeployService(IWorkspacePolicy policy, WorkspacePath labRoot) : IPluginDeployService
{
    private const long MaxPluginBytes = 512L * 1024 * 1024;
    private static readonly ImmutableHashSet<string> SupportedExtensions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ".esp", ".esm", ".esl");

    public async ValueTask<PluginDeployResult> DeployAsync(PluginDeployRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var destination = GetDestination(request, diagnostics);
        ValidatePaths(request, destination, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(request, destination, diagnostics);

        Sha256Hash sourceHash;
        long sourceLength;
        try
        {
            var sourceInfo = new FileInfo(request.InputPlugin.Value);
            sourceLength = sourceInfo.Length;
            if (sourceLength > MaxPluginBytes)
            {
                diagnostics.Add(new Diagnostic("plugin-deploy-source-too-large", DiagnosticSeverity.Error,
                    "The source plugin exceeds the deployment safety limit."));
                return Refused(request, destination, diagnostics);
            }

            sourceHash = await HashAsync(request.InputPlugin.Value, cancellationToken);
            if (sourceHash != request.ExpectedInputSha256)
            {
                diagnostics.Add(new Diagnostic("plugin-deploy-source-hash-mismatch", DiagnosticSeverity.Error,
                    "The source plugin does not match the expected SHA-256 binding."));
                return Refused(request, destination, diagnostics, sourceHash, sourceLength);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-source-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, destination, diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-source-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(request, destination, diagnostics);
        }

        try
        {
            if (File.Exists(destination.Value))
            {
                if (File.GetAttributes(destination.Value).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("plugin-deploy-destination-reparse", DiagnosticSeverity.Error,
                        "The destination plugin may not be a reparse point."));
                    return Refused(request, destination, diagnostics, sourceHash, sourceLength);
                }

                var destinationInfo = new FileInfo(destination.Value);
                var destinationHash = await HashAsync(destination.Value, cancellationToken);
                if (destinationInfo.Length == sourceLength && destinationHash == sourceHash)
                {
                    return new PluginDeployResult(false, true, request.Edition, request.InputPlugin, destination,
                        sourceHash, destinationHash, sourceLength, diagnostics.ToImmutable());
                }

                diagnostics.Add(new Diagnostic("plugin-deploy-destination-conflict", DiagnosticSeverity.Error,
                    "A different plugin already exists at the destination; deployment never overwrites it."));
                return Refused(request, destination, diagnostics, sourceHash, sourceLength, destinationHash);
            }

            var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await CopyAndVerifyAsync(request.InputPlugin.Value, temporary, sourceHash, sourceLength,
                    cancellationToken);
                File.Move(temporary, destination.Value, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination.Value))
            {
                diagnostics.Add(new Diagnostic("plugin-deploy-destination-conflict", DiagnosticSeverity.Error,
                    "A destination appeared during deployment; deployment never overwrites it."));
                return Refused(request, destination, diagnostics, sourceHash, sourceLength);
            }
            finally
            {
                TryDelete(temporary);
            }

            var outputHash = await HashAsync(destination.Value, cancellationToken);
            if (outputHash != sourceHash)
            {
                diagnostics.Add(new Diagnostic("plugin-deploy-output-hash-mismatch", DiagnosticSeverity.Error,
                    "The promoted plugin does not match the source hash."));
                return Refused(request, destination, diagnostics, sourceHash, sourceLength, outputHash);
            }

            return new PluginDeployResult(true, false, request.Edition, request.InputPlugin, destination,
                sourceHash, outputHash, sourceLength, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-write-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-write-denied", DiagnosticSeverity.Error,
                exception.Message));
        }

        return Refused(request, destination, diagnostics, sourceHash, sourceLength);
    }

    private void ValidatePaths(PluginDeployRequest request, WorkspacePath destination,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.InputPlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("plugin-deploy-input-outside-lab", DiagnosticSeverity.Error,
                "The input plugin must remain under the K-only lab root."));
        if (!request.DataRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("plugin-deploy-data-outside-lab", DiagnosticSeverity.Error,
                "The copied Data root must remain under the K-only lab root."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("plugin-deploy-data-missing", DiagnosticSeverity.Error,
                "The copied Data root does not exist."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));

        var extension = Path.GetExtension(request.InputPlugin.Value);
        if (!SupportedExtensions.Contains(extension))
            diagnostics.Add(new Diagnostic("plugin-deploy-extension-unsupported", DiagnosticSeverity.Error,
                "Only .esp, .esm, and .esl plugins may be deployed."));
        if (!File.Exists(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("plugin-deploy-input-missing", DiagnosticSeverity.Error,
                "The input plugin does not exist."));
        else if (File.GetAttributes(request.InputPlugin.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("plugin-deploy-input-reparse", DiagnosticSeverity.Error,
                "The input plugin may not be a reparse point."));

        var inputParent = Path.GetDirectoryName(request.InputPlugin.Value);
        if (inputParent is null || ContainsReparseBetween(labRoot.Value, inputParent))
            diagnostics.Add(new Diagnostic("plugin-deploy-input-path-reparse", DiagnosticSeverity.Error,
                "The input plugin path contains a reparse point."));
        if (ContainsReparseBetween(labRoot.Value, request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("plugin-deploy-data-reparse", DiagnosticSeverity.Error,
                "The copied Data root path contains a reparse point."));
        if (string.Equals(request.InputPlugin.Value, destination.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("plugin-deploy-source-destination-same", DiagnosticSeverity.Error,
                "The source plugin and destination must be different paths."));
    }

    private static WorkspacePath GetDestination(PluginDeployRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var name = Path.GetFileName(request.InputPlugin.Value);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-input-name-invalid", DiagnosticSeverity.Error,
                "The input plugin must have a simple file name."));
            return request.DataRoot;
        }

        try { return new WorkspacePath(Path.Combine(request.DataRoot.Value, name)); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-deploy-destination-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return request.DataRoot;
        }
    }

    private static async ValueTask CopyAndVerifyAsync(string source, string temporary, Sha256Hash expectedHash,
        long expectedLength, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                           FileOptions.SequentialScan))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           128 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 128 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var stagedInfo = new FileInfo(temporary);
        var stagedHash = await HashAsync(temporary, cancellationToken);
        if (stagedInfo.Length != expectedLength || stagedHash != expectedHash)
            throw new IOException("The staged plugin bytes no longer match the source binding.");
    }

    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
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

    private static PluginDeployResult Refused(PluginDeployRequest request, WorkspacePath destination,
        ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? sourceHash = null, long? sourceLength = null,
        Sha256Hash? destinationHash = null) =>
        new(false, false, request.Edition, request.InputPlugin, destination, sourceHash, destinationHash,
            sourceLength, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
