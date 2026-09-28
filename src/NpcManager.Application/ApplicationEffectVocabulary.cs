using System.Collections.Frozen;

namespace NpcManager.Application;

public static class ApplicationEffectVocabulary
{
    public const int MaximumRecordedEffects = 32;
    public const int TerminalJournalEffects = 2;
    public const int MaximumCommandEffects =
        MaximumRecordedEffects - TerminalJournalEffects;

    public const string Attempted = "attempted";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Refused = "refused";
    public const string Blocked = "blocked";
    public const string Skipped = "skipped";

    public const string Workspace = "workspace";
    public const string ReviewedWorkspace = "reviewed-workspace";
    public const string KLocalOutput = "k-local-output";
    public const string Stdout = "stdout";
    public const string InlineResult = "inline-result";
    public const string WorkspaceLocalJournal = "workspace-local-journal";

    private static readonly FrozenSet<string> AdmittedStatuses = new[]
    {
        Attempted,
        Completed,
        Failed,
        Refused,
        Blocked,
        Skipped
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AdmittedScopes = new[]
    {
        Workspace,
        ReviewedWorkspace,
        KLocalOutput,
        Stdout,
        InlineResult,
        WorkspaceLocalJournal
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsAdmittedStatus(string? value) =>
        value is not null && AdmittedStatuses.Contains(value);

    public static bool IsAdmittedScope(string? value) =>
        value is not null && AdmittedScopes.Contains(value);

    public static string ToWire(ApplicationEffectStatus status) =>
        status switch
        {
            ApplicationEffectStatus.Attempted => Attempted,
            ApplicationEffectStatus.Completed => Completed,
            ApplicationEffectStatus.Failed => Failed,
            ApplicationEffectStatus.Refused => Refused,
            ApplicationEffectStatus.Blocked => Blocked,
            ApplicationEffectStatus.Skipped => Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static string ToWire(ApplicationEffectScope scope) =>
        scope switch
        {
            ApplicationEffectScope.Workspace => Workspace,
            ApplicationEffectScope.ReviewedWorkspace => ReviewedWorkspace,
            ApplicationEffectScope.KLocalOutput => KLocalOutput,
            ApplicationEffectScope.Stdout => Stdout,
            ApplicationEffectScope.InlineResult => InlineResult,
            ApplicationEffectScope.WorkspaceLocalJournal =>
                WorkspaceLocalJournal,
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

    public static bool TryParseStatus(
        string? value,
        out ApplicationEffectStatus status)
    {
        status = value switch
        {
            Attempted => ApplicationEffectStatus.Attempted,
            Completed => ApplicationEffectStatus.Completed,
            Failed => ApplicationEffectStatus.Failed,
            Refused => ApplicationEffectStatus.Refused,
            Blocked => ApplicationEffectStatus.Blocked,
            Skipped => ApplicationEffectStatus.Skipped,
            _ => default
        };
        return IsAdmittedStatus(value);
    }

    public static bool TryParseScope(
        string? value,
        out ApplicationEffectScope scope)
    {
        scope = value switch
        {
            Workspace => ApplicationEffectScope.Workspace,
            ReviewedWorkspace => ApplicationEffectScope.ReviewedWorkspace,
            KLocalOutput => ApplicationEffectScope.KLocalOutput,
            Stdout => ApplicationEffectScope.Stdout,
            InlineResult => ApplicationEffectScope.InlineResult,
            WorkspaceLocalJournal =>
                ApplicationEffectScope.WorkspaceLocalJournal,
            _ => default
        };
        return IsAdmittedScope(value);
    }

    public static bool IsAdmittedPair(
        AgentEffectKind kind,
        ApplicationEffectScope scope) =>
        Enum.IsDefined(kind) && Enum.IsDefined(scope) &&
        (kind, scope) switch
        {
            (AgentEffectKind.ReadWorkspace,
                ApplicationEffectScope.Workspace or
                ApplicationEffectScope.ReviewedWorkspace) => true,
            (AgentEffectKind.WriteNewArtifact,
                ApplicationEffectScope.KLocalOutput) => true,
            (AgentEffectKind.DeployToCopiedData,
                ApplicationEffectScope.KLocalOutput) => true,
            (AgentEffectKind.AppendLocalOperationJournal,
                ApplicationEffectScope.WorkspaceLocalJournal) => true,
            (AgentEffectKind.InvokeAdmittedProcess or AgentEffectKind.LaunchDesktop,
                ApplicationEffectScope.Workspace) => true,
            _ => false
        };

    public static bool IsAdmittedPair(
        AgentEffectKind kind,
        string? scope) =>
        TryParseScope(scope, out ApplicationEffectScope parsed) &&
        IsAdmittedPair(kind, parsed);
}
