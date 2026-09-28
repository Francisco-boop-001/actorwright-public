using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Packages only the game-specific apply PEX required by the pinned VMAD contract.</summary>
/// <remarks>
/// This is deliberately separate from PEX inspection and from deployment. The package is a new
/// K-local directory containing <c>Data/Scripts/NPCM_Manolov_Apply*.pex</c> and a hash-bound manifest;
/// native RaceMenu/LooksMenu stub PEX files are never copied.
/// </remarks>
public sealed class RuntimeScriptPackageService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRuntimeScriptPackageService
{
    private const long MaxPexBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<RuntimeScriptPackageResult> PackageAsync(RuntimeScriptPackageRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(request, diagnostics);

        var script = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        var gameFolder = request.Edition == GameEdition.Fallout4 ? "fo4" : "sse";
        var source = FindFile(request.SourceRoot.Value, "src_" + gameFolder, script + ".psc");
        var pex = FindFile(request.SourceRoot.Value, "pex_" + gameFolder, script + ".pex");
        if (source is null || pex is null)
        {
            diagnostics.Add(new Diagnostic("runtime-script-package-files-missing", DiagnosticSeverity.Error,
                "The copied source root must contain the game-specific apply PSC and PEX pair."));
            return Refused(request, diagnostics);
        }

        var pexInfo = new FileInfo(pex);
        if (pexInfo.Length <= 0 || pexInfo.Length > MaxPexBytes)
        {
            diagnostics.Add(new Diagnostic("runtime-script-package-pex-size", DiagnosticSeverity.Error,
                "The apply PEX must be non-empty and no larger than 64 MiB."));
            return Refused(request, diagnostics);
        }

        var temporary = request.OutputRoot.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        var finalInstalled = Path.Combine(request.OutputRoot.Value, "Data", "Scripts", script + ".pex");
        var relativeInstalled = "Data/Scripts/" + script + ".pex";
        try
        {
            var sourcePscHash = await HashAsync(source, cancellationToken);
            var sourcePexHash = await HashAsync(pex, cancellationToken);
            Directory.CreateDirectory(Path.Combine(temporary, "Data", "Scripts"));
            var temporaryInstalled = Path.Combine(temporary, "Data", "Scripts", script + ".pex");
            await CopyFileAsync(pex, temporaryInstalled, cancellationToken);
            var installedInfo = new FileInfo(temporaryInstalled);
            if (installedInfo.Length <= 0 || installedInfo.Length > MaxPexBytes)
            {
                diagnostics.Add(new Diagnostic("runtime-script-package-copy-size", DiagnosticSeverity.Error,
                    "The staged apply PEX changed to an invalid size while it was being copied."));
                return Refused(request, diagnostics);
            }
            var installedHash = await HashAsync(temporaryInstalled, cancellationToken);
            if (!string.Equals(installedHash.Value, sourcePexHash.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("runtime-script-package-copy-mismatch", DiagnosticSeverity.Error,
                    "The staged apply PEX hash does not match the source PEX."));
                return Refused(request, diagnostics);
            }

            var artifact = new RuntimeScriptPackageArtifact(
                "1", "npc-apply-script-package", request.Edition.ToWireName(), script,
                request.SourceRoot.Value, source, sourcePscHash.Value, pex, sourcePexHash.Value,
                relativeInstalled, finalInstalled, installedHash.Value, installedInfo.Length, false, false);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
            var temporaryManifest = Path.Combine(temporary, "runtime-script-package.json");
            await WriteNewFileAsync(temporaryManifest, manifestBytes, cancellationToken);
            var manifestHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(manifestBytes)));
            Directory.Move(temporary, request.OutputRoot.Value);
            temporary = string.Empty;
            return new RuntimeScriptPackageResult(true, request.OutputRoot, artifact, manifestHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-package-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("runtime-script-package-write-denied", DiagnosticSeverity.Error, exception.Message));
        }
        finally
        {
            if (temporary.Length > 0) TryDeleteDirectory(temporary);
        }

        return Refused(request, diagnostics);
    }

    private void ValidatePaths(RuntimeScriptPackageRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.SourceRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("runtime-script-package-source-outside-lab", DiagnosticSeverity.Error,
                "The source root must remain under the K-only lab root."));
        if (!Directory.Exists(request.SourceRoot.Value))
            diagnostics.Add(new Diagnostic("runtime-script-package-source-missing", DiagnosticSeverity.Error,
                "The source root does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourceRoot));
            if (ContainsReparseBetween(labRoot.Value, request.SourceRoot.Value))
                diagnostics.Add(new Diagnostic("runtime-script-package-source-reparse", DiagnosticSeverity.Error,
                    "The source path contains a reparse point."));
        }

        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("runtime-script-package-output-outside-lab", DiagnosticSeverity.Error,
                "The package output root must remain under the K-only lab root."));
        if (File.Exists(request.OutputRoot.Value) || Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("runtime-script-package-output-exists", DiagnosticSeverity.Error,
                "Runtime-script packages never overwrite an existing file or directory."));
        var parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("runtime-script-package-parent-missing", DiagnosticSeverity.Error,
                "The package output parent directory must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
            if (ContainsReparseBetween(labRoot.Value, parent))
                diagnostics.Add(new Diagnostic("runtime-script-package-parent-reparse", DiagnosticSeverity.Error,
                    "The package output path contains a reparse point."));
        }
    }

    private static string? FindFile(string root, string directory, string file)
    {
        var candidates = new[]
        {
            Path.Combine(root, directory, file),
            Path.Combine(root, "Papyrus", directory, file)
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            if (File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint)) continue;
            var candidateParent = Path.GetDirectoryName(candidate);
            if (candidateParent is null || ContainsReparseBetween(root, candidateParent)) continue;
            return candidate;
        }
        return null;
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

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
            FileOptions.WriteThrough);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static RuntimeScriptPackageResult Refused(RuntimeScriptPackageRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, request.OutputRoot, null, null, diagnostics.ToImmutable());

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
