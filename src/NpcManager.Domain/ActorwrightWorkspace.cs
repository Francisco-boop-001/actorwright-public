namespace NpcManager.Domain;

public sealed class ProtectedRootConfigurationException(
    string message,
    Exception? innerException = null) : ArgumentException(
        message,
        innerException)
{
}

public static class ActorwrightWorkspace
{
    public const string WorkspaceRootEnvironmentVariable =
        "ACTORWRIGHT_WORKSPACE_ROOT";
    public const string ProtectedRootEnvironmentVariable =
        "ACTORWRIGHT_PROTECTED_ROOT";
    public const string WorkspaceRootNotKLocalCode =
        "workspace-root-not-k-local";

    public static WorkspacePath ResolveRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(
            WorkspaceRootEnvironmentVariable);
        string selected = string.IsNullOrWhiteSpace(configured)
            ? Environment.CurrentDirectory
            : configured;
        return new WorkspacePath(Path.GetFullPath(selected));
    }

    public static WorkspacePath ResolveAdmittedRoot() =>
        RequireAdmittedRoot(ResolveRoot());

    public static WorkspacePath RequireAdmittedRoot(
        WorkspacePath workspaceRoot)
    {
        string? failure = GetWorkspaceRootAdmissionFailureMessage(
            workspaceRoot);
        if (failure is not null)
            throw new InvalidOperationException(
                $"{WorkspaceRootNotKLocalCode}: {failure}");
        return workspaceRoot;
    }

    public static string? GetWorkspaceRootAdmissionFailureMessage(
        WorkspacePath workspaceRoot)
    {
        if (string.Equals(
                Path.GetPathRoot(workspaceRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"The workspace root '{workspaceRoot.Value}' is not on drive K:. " +
            "Set ACTORWRIGHT_WORKSPACE_ROOT to a K:\\ path or run from a K:\\ directory.";
    }

    public static WorkspacePath ResolveProtectedRoot(
        WorkspacePath workspaceRoot)
    {
        string? configured = Environment.GetEnvironmentVariable(
            ProtectedRootEnvironmentVariable);
        if (configured is null)
            return new WorkspacePath(@"F:\ExampleGame");
        if (string.IsNullOrWhiteSpace(configured))
            throw new ProtectedRootConfigurationException(
                $"{ProtectedRootEnvironmentVariable} must name a valid absolute or workspace-relative path.");
        if (Path.IsPathRooted(configured) &&
            !Path.IsPathFullyQualified(configured))
        {
            throw new ProtectedRootConfigurationException(
                $"{ProtectedRootEnvironmentVariable} must not use a drive-relative or root-relative path.");
        }

        try
        {
            string resolved = Path.IsPathFullyQualified(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, workspaceRoot.Value);
            return new WorkspacePath(resolved);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            throw new ProtectedRootConfigurationException(
                $"{ProtectedRootEnvironmentVariable} must name a valid absolute or workspace-relative path.",
                exception);
        }
    }

    public static bool IsAdmittedRuntimeRoot(
        WorkspacePath workspaceRoot,
        WorkspacePath runtimeRoot)
    {
        return runtimeRoot.IsUnder(workspaceRoot);
    }

    // Pure exclusions for offline voice authoring; these roots are never opened.
    public static bool IsVoiceExcludedPath(WorkspacePath path) =>
        path.IsUnder(new WorkspacePath(@"F:\")) ||
        path.IsUnder(new WorkspacePath(@"K:\ExampleExchange")) ||
        path.IsUnder(new WorkspacePath(@"K:\ExampleMigrationArchive"));

    public static WorkspacePath WorkRoot(
        WorkspacePath workspaceRoot,
        params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new WorkspacePath(Path.Combine(
            [workspaceRoot.Value, ".actorwright", "work", .. segments]));
    }
}
