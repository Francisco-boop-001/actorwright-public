using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed record ReviewedWorkspacePreflightBinding(
    ReviewedGameIntakeRequest? Request,
    string? ErrorMessage)
{
    public bool IsValid => Request is not null && ErrorMessage is null;
}

internal static class ReviewedWorkspacePreflightBinder
{
    public static ReviewedWorkspacePreflightBinding Bind(
        ParsedCommand command,
        WorkspacePath defaultWorkspaceRoot,
        bool requireExplicitWorkspace)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? editionValue = command.Options.GetValueOrDefault("edition") ??
                               command.Options.GetValueOrDefault("game");
        if (editionValue is null ||
            !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return Invalid("Reviewed workspace preflight requires --game skyrimse.");
        if (!command.Options.TryGetValue("data-root", out string? dataRoot) ||
            !command.Options.TryGetValue("output-root", out string? outputRoot))
            return Invalid(
                "Reviewed workspace preflight requires --data-root and --output-root.");
        string? loadOrderValue = command.Options.GetValueOrDefault("load-order") ??
                                 command.Options.GetValueOrDefault("loadorder");
        if (loadOrderValue is null)
            return Invalid("Reviewed workspace preflight requires --load-order.");
        if (requireExplicitWorkspace &&
            !command.Options.TryGetValue("workspace-root", out _))
            return Invalid(
                "Protocol-v2 reviewed workspace preflight requires --workspace-root.");

        if (!Path.IsPathFullyQualified(loadOrderValue))
            return InvalidLoadOrder(loadOrderValue);
        var workspace = new WorkspacePath(command.Options.GetValueOrDefault("workspace-root") ?? defaultWorkspaceRoot.Value);
        var loadOrder = new WorkspacePath(loadOrderValue);
        // Leave out-of-policy paths to the existing typed policy boundary; do not probe them here.
        if (workspace.IsUnder(defaultWorkspaceRoot) && loadOrder.IsUnder(workspace) &&
            string.Equals(Path.GetPathRoot(loadOrder.Value), @"K:\", StringComparison.OrdinalIgnoreCase) &&
            loadOrder.Value.IndexOf(':', 2) < 0 &&
            !new KOnlyWorkspacePolicy(defaultWorkspaceRoot, ActorwrightWorkspace.ResolveProtectedRoot(defaultWorkspaceRoot))
                .EvaluateReadRoot(workspace, loadOrder).Any(item => item.Severity == DiagnosticSeverity.Error) &&
            !File.Exists(loadOrder.Value))
            return InvalidLoadOrder(loadOrderValue);

        ImmutableArray<PluginName> selected = default;
        if (command.Options.TryGetValue("selected", out string? selectedValue))
        {
            selected = selectedValue.Split([',', ';'],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(value => new PluginName(value))
                .ToImmutableArray();
        }

        return new ReviewedWorkspacePreflightBinding(
            new ReviewedGameIntakeRequest(
                edition,
                workspace,
                new WorkspacePath(dataRoot),
                loadOrder,
                new WorkspacePath(outputRoot),
                selected),
            null);
    }

    private static ReviewedWorkspacePreflightBinding Invalid(string message) =>
        new(null, message);

    private static ReviewedWorkspacePreflightBinding InvalidLoadOrder(string value) =>
        Invalid($"--load-order '{value}': expected a schema-1 load-order JSON file; use --plugins for a comma list. CLI paths must be absolute K-local filesystem paths.");
}
