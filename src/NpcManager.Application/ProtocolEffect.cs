namespace NpcManager.Application;

public enum ApplicationEffectStatus
{
    Attempted,
    Completed,
    Failed,
    Refused,
    Blocked,
    Skipped
}

public enum ApplicationEffectScope
{
    Workspace,
    ReviewedWorkspace,
    KLocalOutput,
    Stdout,
    InlineResult,
    WorkspaceLocalJournal
}

public sealed record ProtocolEffect
{
    private ProtocolEffect(
        AgentEffectKind kind,
        string status,
        string scope)
    {
        Kind = kind;
        Status = status;
        Scope = scope;
    }

    public AgentEffectKind Kind { get; }

    public string Status { get; }

    public string Scope { get; }

    public static ProtocolEffect Create(
        AgentEffectKind kind,
        ApplicationEffectStatus status,
        ApplicationEffectScope scope)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        string statusWire = ApplicationEffectVocabulary.ToWire(status);
        string scopeWire = ApplicationEffectVocabulary.ToWire(scope);
        if (!ApplicationEffectVocabulary.IsAdmittedPair(kind, scope))
            throw new ArgumentOutOfRangeException(nameof(scope));
        return CreateCore(kind, statusWire, scopeWire);
    }

    internal static ProtocolEffect CreateUncheckedForTesting(
        AgentEffectKind kind,
        string status,
        string scope) => CreateCore(kind, status, scope);

    private static ProtocolEffect CreateCore(
        AgentEffectKind kind,
        string status,
        string scope) => new ProtocolEffect(kind, status, scope);
}
