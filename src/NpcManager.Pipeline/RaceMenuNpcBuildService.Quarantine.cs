using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private void QuarantineFailedCompletedOutputRoot(
        WorkspacePath requestedRoot,
        WorkspacePath artifactRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!string.Equals(requestedRoot.Value, artifactRoot.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !requestedRoot.IsUnder(LaboratoryRoot) ||
                string.Equals(requestedRoot.Value, LaboratoryRoot.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("racemenu-build-output-quarantine-refused",
                    "Refused to quarantine a failed postcheck root that was not the exact fresh K-local output."));
                return;
            }

            var parentValue = Path.GetDirectoryName(requestedRoot.Value);
            if (parentValue is null || !Directory.Exists(parentValue) ||
                File.GetAttributes(parentValue).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("racemenu-build-output-quarantine-parent",
                    "The failed output parent is absent or no longer an ordinary directory."));
                return;
            }

            if (!Directory.Exists(requestedRoot.Value) && !File.Exists(requestedRoot.Value))
            {
                diagnostics.Add(new Diagnostic("racemenu-build-output-already-absent",
                    DiagnosticSeverity.Warning,
                    "The failed postcheck output was already absent from its requested deployable path."));
                return;
            }

            var quarantine = new WorkspacePath(Path.Combine(parentValue,
                ".npcmanager-rejected-" + Guid.NewGuid().ToString("N")));
            var boundary = WorkspacePolicy.Evaluate(LaboratoryRoot, quarantine);
            diagnostics.AddRange(boundary);
            if (boundary.Any(item => item.Severity == DiagnosticSeverity.Error)) return;

            if (Directory.Exists(requestedRoot.Value))
                Directory.Move(requestedRoot.Value, quarantine.Value);
            else
                File.Move(requestedRoot.Value, quarantine.Value, overwrite: false);

            if (Directory.Exists(requestedRoot.Value) || File.Exists(requestedRoot.Value))
            {
                diagnostics.Add(Error("racemenu-build-output-quarantine-incomplete",
                    "The failed package still exists at its requested deployable path after quarantine."));
                return;
            }
            diagnostics.Add(new Diagnostic("racemenu-build-output-quarantined",
                DiagnosticSeverity.Warning,
                $"The failed postcheck package was atomically moved to '{quarantine.Value}' and is not available at the requested output path."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-build-output-quarantine-failed",
                $"The failed package could not be removed from its requested output path: {exception.Message}"));
        }
    }
}
