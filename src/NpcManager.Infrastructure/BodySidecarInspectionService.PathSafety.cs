using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class BodySidecarInspectionService
{
    private static bool ContainsReparsePoint(string path, string root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (true)
        {
            if (!IsPathUnder(current, boundary))
            {
                diagnostics.Add(new Diagnostic("body-sidecar-path-outside-root", DiagnosticSeverity.Error,
                    "BodySlide sidecar path escaped the declared K workspace."));
                return true;
            }
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("body-sidecar-reparse", DiagnosticSeverity.Error,
                        $"BodySlide sidecar path '{current}' is a reparse point."));
                    return true;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-attributes", DiagnosticSeverity.Error, exception.Message));
                return true;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-attributes", DiagnosticSeverity.Error, exception.Message));
                return true;
            }
            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return true;
            current = parent;
        }
    }

    private static bool IsPathUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

}
