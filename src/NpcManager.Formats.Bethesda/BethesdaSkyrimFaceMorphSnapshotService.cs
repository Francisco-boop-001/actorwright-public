using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Independently reopens one hash-bound copied plugin and exposes its native
/// NAM9/NAMA record payload without writing or inferring missing engine data.
/// </summary>
public sealed class BethesdaSkyrimFaceMorphSnapshotService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimFaceMorphSnapshotService
{
    public async ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
        SkyrimFaceMorphSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Plugin));
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("face-morph-snapshot-edition",
                "Native NAM9/NAMA snapshot authority supports Skyrim SE/AE only."));
        if (request.NpcFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("face-morph-snapshot-formid",
                "The authority NPC FormID must be a nonzero plugin-local value."));
        if (!request.Plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
            !request.Plugin.Value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
            !request.Plugin.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("face-morph-snapshot-extension",
                "The authority plugin must use .esp, .esm, or .esl."));

        Sha256Hash? actualHash = null;
        try
        {
            var info = new FileInfo(request.Plugin.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > int.MaxValue)
            {
                diagnostics.Add(Error("face-morph-snapshot-file",
                    "The authority plugin must be an existing non-empty ordinary file."));
            }
            else if (File.GetAttributes(request.Plugin.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("face-morph-snapshot-reparse",
                    "The authority plugin may not be a reparse point."));
            }

            if (HasErrors(diagnostics)) return Refused(actualHash, diagnostics);

            await using (var stream = new FileStream(request.Plugin.Value, FileMode.Open,
                             FileAccess.Read, FileShare.Read, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = new Sha256Hash(Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)));
            }
            if (actualHash != request.ExpectedPluginSha256)
            {
                diagnostics.Add(Error("face-morph-snapshot-hash",
                    $"Authority plugin hash {actualHash} does not match " +
                    $"{request.ExpectedPluginSha256}."));
                return Refused(actualHash, diagnostics);
            }

            var snapshot = BethesdaSkyrimFaceMorphAdapter.Read(
                request.Edition, request.Plugin, request.NpcFormId);
            diagnostics.Add(new Diagnostic("face-morph-snapshot-read",
                DiagnosticSeverity.Info,
                "The hash-bound copied NPC NAM9/NAMA payload was independently reopened."));
            return new SkyrimFaceMorphSnapshotResult(
                true, actualHash, snapshot, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or NotSupportedException)
        {
            diagnostics.Add(Error("face-morph-snapshot-read-failed", exception.Message));
            return Refused(actualHash, diagnostics);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceMorphSnapshotResult Refused(
        Sha256Hash? hash,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, hash, null, diagnostics.ToImmutable());
}
