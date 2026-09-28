using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Owns path and plugin-extension policy for NPC mutation requests.</summary>
internal static class NpcMutationPathPolicy
{
    internal static ImmutableArray<Diagnostic> ValidateReadOnlyPaths(
        IWorkspacePolicy policy, WorkspacePath labRoot, WorkspacePath source, WorkspacePath output)
    {
        var outputParent = Path.GetDirectoryName(output.Value);
        if (outputParent is null)
            return [new Diagnostic("output-parent-invalid", DiagnosticSeverity.Error, "Output path has no parent directory.")];

        var diagnostics = policy.Evaluate(labRoot, new WorkspacePath(outputParent)).ToBuilder();
        if (!source.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error,
                "Input plugins must be copied under the K-only lab root."));
        AddReparseDiagnostic(diagnostics, source.Value, "input-plugin");
        AddReparseDiagnostic(diagnostics, outputParent, "output-parent");
        return diagnostics.ToImmutable();
    }

    internal static bool IsPluginPath(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);

    internal static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
                            $"The {role} traverses a reparse point."));
                        return;
                    }
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error,
                    $"The {role} could not be inspected: {exception.Message}"));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error,
                    $"The {role} could not be inspected: {exception.Message}"));
                return;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }
}
