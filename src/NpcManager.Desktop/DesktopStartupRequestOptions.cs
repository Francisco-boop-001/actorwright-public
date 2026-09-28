using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal sealed record DesktopStartupRequestSelection(
    WorkspacePath? RequestFile,
    string? RequestSha256,
    string? Error,
    DesktopWorkflowLaunchBinding? WorkflowReview = null)
{
    public bool HasExplicitRequest => RequestFile is not null && RequestSha256 is not null;

    public bool HasWorkflowReview => WorkflowReview is not null;
}

internal static class DesktopStartupRequestOptions
{
    private const string RequestOption = "--race-menu-request";
    private const string HashOption = "--race-menu-request-sha256";
    private const string WorkflowBundleOption = "--workflow-bundle";
    private const string WorkflowHashOption = "--workflow-bundle-sha256";
    private const string Usage =
        "Use --race-menu-request <absolute K-local JSON> together with " +
        "--race-menu-request-sha256 <64 hexadecimal characters>, or " +
        "--workflow-bundle <absolute K-local JSON> together with " +
        "--workflow-bundle-sha256 <64 uppercase hexadecimal characters>.";

    public static DesktopStartupRequestSelection Resolve(
        IEnumerable<string> arguments,
        WorkspacePath labRoot)
    {
        var tokens = arguments.ToArray();
        if (tokens.Length == 0)
            return new DesktopStartupRequestSelection(null, null, null);

        string? request = null;
        string? hash = null;
        string? workflowBundle = null;
        string? workflowHash = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var option = tokens[index];
            if (!option.Equals(RequestOption, StringComparison.OrdinalIgnoreCase) &&
                !option.Equals(HashOption, StringComparison.OrdinalIgnoreCase) &&
                !option.Equals(WorkflowBundleOption, StringComparison.OrdinalIgnoreCase) &&
                !option.Equals(WorkflowHashOption, StringComparison.OrdinalIgnoreCase))
                return Error($"Unknown desktop startup option '{option}'.");
            if (index + 1 >= tokens.Length || tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
                return Error($"Desktop startup option '{option}' requires a value.");

            var value = tokens[++index];
            if (option.Equals(RequestOption, StringComparison.OrdinalIgnoreCase))
            {
                if (request is not null) return Error($"Desktop startup option '{RequestOption}' was repeated.");
                request = value;
            }
            else if (option.Equals(HashOption, StringComparison.OrdinalIgnoreCase))
            {
                if (hash is not null) return Error($"Desktop startup option '{HashOption}' was repeated.");
                hash = value;
            }
            else if (option.Equals(WorkflowBundleOption, StringComparison.OrdinalIgnoreCase))
            {
                if (workflowBundle is not null) return Error($"Desktop startup option '{WorkflowBundleOption}' was repeated.");
                workflowBundle = value;
            }
            else
            {
                if (workflowHash is not null) return Error($"Desktop startup option '{WorkflowHashOption}' was repeated.");
                workflowHash = value;
            }
        }

        bool hasRequestMode = request is not null || hash is not null;
        bool hasWorkflowMode = workflowBundle is not null || workflowHash is not null;
        if (hasRequestMode && hasWorkflowMode)
            return Error("RaceMenu request and workflow review startup modes cannot be combined.");
        if (hasRequestMode && (request is null || hash is null))
            return Error("The request path and SHA-256 must be supplied together.");
        if (hasWorkflowMode && (workflowBundle is null || workflowHash is null))
            return Error("The workflow bundle path and SHA-256 must be supplied together.");
        try
        {
            if (hasWorkflowMode)
            {
                var bundleFile = new WorkspacePath(workflowBundle!);
                if (!bundleFile.IsUnder(labRoot) || string.Equals(
                        bundleFile.Value, labRoot.Value,
                        StringComparison.OrdinalIgnoreCase))
                    return Error("The desktop workflow bundle must be below the configured Actorwright workspace.");
                RequireUppercaseSha256(workflowHash!);
                return new DesktopStartupRequestSelection(
                    null,
                    null,
                    null,
                    new DesktopWorkflowLaunchBinding(bundleFile, workflowHash!));
            }
            var requestFile = new WorkspacePath(request!);
            if (!requestFile.IsUnder(labRoot) || string.Equals(
                    requestFile.Value, labRoot.Value, StringComparison.OrdinalIgnoreCase))
                return Error("The desktop startup request must be below the configured Actorwright workspace.");
            var normalizedHash = new Sha256Hash(hash!);
            return new DesktopStartupRequestSelection(requestFile, normalizedHash.Value, null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Error(exception.Message);
        }
    }

    private static DesktopStartupRequestSelection Error(string message) =>
        new(null, null, $"{message} {Usage}");

    private static void RequireUppercaseSha256(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F'))))
            throw new ArgumentException(
                "The workflow bundle SHA-256 must contain exactly 64 uppercase hexadecimal characters.");
    }
}
