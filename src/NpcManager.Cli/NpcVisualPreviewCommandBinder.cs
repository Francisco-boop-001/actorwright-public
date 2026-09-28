using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed record NpcVisualPreviewCommandBinding(
    WorkspacePath IntakePath,
    PluginName Plugin,
    FormId FormId,
    NpcVisualPreviewPackageOverlay? PackageOverlay,
    WorkspacePath OutputRoot,
    WorkspacePath? WorkflowInput,
    string? WorkflowInputSha256,
    WorkspacePath? WorkflowOutput);

internal sealed record NpcVisualPreviewCommandBindingResult(
    NpcVisualPreviewCommandBinding? Binding,
    string? ErrorMessage)
{
    public bool IsValid => Binding is not null && ErrorMessage is null;
}

internal static class NpcVisualPreviewCommandBinder
{
    private static readonly ImmutableArray<string> BaseOptions =
    [
        "intake", "plugin", "form", "package-manifest",
        "expected-package-sha256", "output-root"
    ];

    private static readonly ImmutableArray<string> WorkflowOptions =
    [
        "workflow-bundle", "workflow-bundle-sha256", "workflow-output"
    ];

    public static NpcVisualPreviewCommandBindingResult Bind(
        ParsedCommand command,
        bool strict)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (strict && (command.Positionals.Length != 2 ||
            !command.Positionals.SequenceEqual(
                ["preview", "npc"],
                StringComparer.OrdinalIgnoreCase)))
            return Invalid("preview npc accepts no extra positional arguments.");
        if (strict && command.DuplicateOptions.Length > 0)
            return Invalid(
                $"Duplicate option '--{command.DuplicateOptions[0]}' is not allowed.");
        if (strict)
        {
            string? unknown = command.Options.Keys.FirstOrDefault(option =>
                !BaseOptions.Concat(WorkflowOptions).Contains(
                    option,
                    StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return Invalid($"Unknown option '--{unknown}'.");
        }

        if (!command.Options.TryGetValue("intake", out string? intakeText))
            return Invalid(
                "preview npc requires --intake <reviewed-intake.json>.");
        if (!command.Options.TryGetValue("plugin", out string? pluginText))
            return Invalid("preview npc requires --plugin <name>.");
        if (!command.Options.TryGetValue("form", out string? formText) ||
            !FormId.TryParse(formText, out FormId formId) ||
            formId.Value == 0)
            return Invalid(
                "preview npc requires --form <non-null hexadecimal FormID>.");
        if (!command.Options.TryGetValue(
                "output-root",
                out string? outputText))
            return Invalid(
                "preview npc requires --output-root <new-K-path>.");

        bool hasManifest = command.Options.TryGetValue(
            "package-manifest", out string? manifestText);
        bool hasHash = command.Options.TryGetValue(
            "expected-package-sha256", out string? hashText);
        if (hasManifest != hasHash)
            return Invalid(
                "preview npc requires --package-manifest and --expected-package-sha256 together.");
        if (strict && !hasManifest)
            return Invalid(
                "Protocol-v2 preview requires the exact package manifest path/hash pair.");

        bool hasWorkflow = command.Options.TryGetValue(
            "workflow-bundle", out string? workflowText);
        bool hasWorkflowHash = command.Options.TryGetValue(
            "workflow-bundle-sha256", out string? workflowHash);
        bool hasWorkflowOutput = command.Options.TryGetValue(
            "workflow-output", out string? workflowOutputText);
        if (strict && (!hasWorkflow || !hasWorkflowHash ||
                       !hasWorkflowOutput))
            return Invalid(
                "Protocol-v2 preview requires --workflow-bundle, --workflow-bundle-sha256, and --workflow-output.");
        if (strict && (!IsUpperSha256(hashText!) ||
                       !IsUpperSha256(workflowHash!)))
            return Invalid(
                "Protocol-v2 preview requires uppercase 64-character SHA-256 bindings.");

        try
        {
            return new NpcVisualPreviewCommandBindingResult(
                new NpcVisualPreviewCommandBinding(
                    new WorkspacePath(intakeText),
                    new PluginName(pluginText),
                    formId,
                    hasManifest
                        ? new NpcVisualPreviewPackageOverlay(
                            new WorkspacePath(manifestText!),
                            new Sha256Hash(hashText!))
                        : null,
                    new WorkspacePath(outputText),
                    hasWorkflow ? new WorkspacePath(workflowText!) : null,
                    hasWorkflowHash ? workflowHash : null,
                    hasWorkflowOutput
                        ? new WorkspacePath(workflowOutputText!)
                        : null),
                null);
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception.Message);
        }
    }

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static NpcVisualPreviewCommandBindingResult Invalid(
        string message) => new(null, message);
}
