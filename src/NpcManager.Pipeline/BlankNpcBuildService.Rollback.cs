using System.Collections.Immutable;
using System.ComponentModel;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private RollbackOutcome RollbackOwnedOutputRoot(
        WorkspacePath root,
        PinnedOutputTree? pinnedTree,
        OwnedFileLedger ownedFiles)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!root.IsUnder(labRoot) ||
            string.Equals(root.Value, labRoot.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("blank-npc-rollback-outside-lab", DiagnosticSeverity.Error,
                "Refused rollback outside the exact owned K-local output root."));
            return Incomplete();
        }
        if (pinnedTree is null)
        {
            diagnostics.Add(new Diagnostic("blank-npc-rollback-lease-missing", DiagnosticSeverity.Error,
                "Refused path-based rollback because the output-directory identity lease is missing."));
            return Incomplete();
        }

        try
        {
            diagnostics.AddRange(ownedFiles.RollbackFiles());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            diagnostics.Add(new Diagnostic("blank-npc-rollback-file-failed", DiagnosticSeverity.Error,
                $"Identity-bound file rollback failed: {exception.Message}"));
        }
        try
        {
            diagnostics.AddRange(pinnedTree.RollbackOwnedDirectories());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            pinnedTree.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-rollback-directory-failed", DiagnosticSeverity.Error,
                $"Identity-bound directory rollback failed: {exception.Message}"));
        }

        var remains = Directory.Exists(root.Value) || File.Exists(root.Value);
        if (remains || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Incomplete();
        return new RollbackOutcome(true, diagnostics.ToImmutable());

        RollbackOutcome Incomplete()
        {
            diagnostics.Add(new Diagnostic("blank-npc-rollback-incomplete", DiagnosticSeverity.Error,
                $"The failed build left an unclaimed or changed entry at '{root.Value}'."));
            return new RollbackOutcome(false, diagnostics.ToImmutable());
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static ImmutableArray<Diagnostic> FreezeDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Distinct().ToImmutableArray();

    private static void Report(IProgress<BlankNpcBuildProgress>? progress,
        BlankNpcBuildStage stage, int percent, string message) =>
        progress?.Report(new BlankNpcBuildProgress(stage, percent, message));

    private static BlankNpcBuildResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, null, null, null, null, FreezeDiagnostics(diagnostics));

    private sealed record RollbackOutcome(bool Complete, ImmutableArray<Diagnostic> Diagnostics);
}
