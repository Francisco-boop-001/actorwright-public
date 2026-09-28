using System.Collections.Immutable;

namespace NpcManager.Application;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticRecovery? Recovery { get; init; }
}

public static class WorkspaceDiagnosticCodes
{
    public const string RootNotKLocal = "workspace-root-not-k-local";
}

public sealed record DiagnosticSet(ImmutableArray<Diagnostic> Items)
{
    public bool HasErrors => Items.Any(item => item.Severity == DiagnosticSeverity.Error);

    public static DiagnosticSet Empty { get; } = new(ImmutableArray<Diagnostic>.Empty);
}
