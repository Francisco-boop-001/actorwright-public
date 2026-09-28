using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed record SkyrimNpcFinishCoreCommandBinding(
    WorkspacePath Request, Sha256Hash RequestSha256, WorkspacePath Proposal,
    Sha256Hash? ProposalSha256, bool ValidateAll,
    WorkspacePath? WorkflowInput, string? WorkflowInputSha256,
    WorkspacePath? WorkflowOutput, WorkspacePath? ReviewReceipt, string? ReviewReceiptSha256);

internal sealed record SkyrimNpcFinishCoreCommandBindingResult(
    SkyrimNpcFinishCoreCommandBinding? Binding, string? ErrorMessage)
{
    public bool IsValid => Binding is not null && ErrorMessage is null;
}

internal static class SkyrimNpcFinishCoreCommandBinder
{
    private static readonly string[] AnalyzeOptions =
        ["request", "request-sha256", "proposal", "validate-all", "data-root", "plugins"];
    private static readonly string[] ApplyOptions =
        ["request", "request-sha256", "proposal", "proposal-sha256", "validate-all", "data-root", "plugins"];
    private static readonly string[] WorkflowOptions =
        ["workflow-bundle", "workflow-bundle-sha256", "workflow-output", "review-receipt", "review-receipt-sha256"];
    private static readonly string[] AnalyzeRequired = ["request", "request-sha256", "proposal"];
    private static readonly string[] ApplyRequired = ["request", "request-sha256", "proposal", "proposal-sha256"];

    public static SkyrimNpcFinishCoreCommandBindingResult Bind(ParsedCommand command, bool protocolV2)
    {
        bool apply = command.Name == "npc finish apply";
        if (protocolV2 && (command.Positionals.Length != 3 ||
            !command.Positionals.SequenceEqual(command.Name.Split(' '), StringComparer.OrdinalIgnoreCase)))
            return Invalid("Finish Core accepts no positional arguments beyond the command name.");
        if (command.DuplicateOptions.Length != 0)
            return Invalid("Finish Core options may not be repeated.");
        string[] allowed = apply ? ApplyOptions : AnalyzeOptions;
        string? unknown = command.Options.Keys.FirstOrDefault(key =>
            !allowed.Contains(key, StringComparer.OrdinalIgnoreCase) &&
            !(protocolV2 && WorkflowOptions.Contains(key, StringComparer.OrdinalIgnoreCase)));
        if (unknown is not null) return Invalid($"Finish Core does not support --{unknown}.");
        IEnumerable<string> required = apply ? ApplyRequired : AnalyzeRequired;
        if (protocolV2) required = required.Concat(WorkflowOptions.Take(3));
        string? missing = required.FirstOrDefault(key =>
            !command.Options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) || value == "true");
        if (missing is not null) return Invalid($"Finish Core requires --{missing}.");
        if (protocolV2)
        {
            if (command.Options.ContainsKey("review-receipt") != command.Options.ContainsKey("review-receipt-sha256"))
                return Invalid("Finish Core requires --review-receipt and --review-receipt-sha256 together.");
            foreach ((string key, string value) in command.Options)
                if (key.EndsWith("-sha256", StringComparison.OrdinalIgnoreCase) &&
                    (value.Length != 64 || !value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')))
                    return Invalid($"Protocol-v2 Finish Core requires an uppercase 64-character --{key}.");
        }
        return new(new(
            new WorkspacePath(command.Options["request"]), new Sha256Hash(command.Options["request-sha256"]),
            new WorkspacePath(command.Options["proposal"]), apply ? new Sha256Hash(command.Options["proposal-sha256"]) : null,
            command.Options.TryGetValue("validate-all", out string? validation) &&
                (string.Equals(validation, "true", StringComparison.OrdinalIgnoreCase) || validation == "1"),
            Path("workflow-bundle"), Value("workflow-bundle-sha256"), Path("workflow-output"),
            Path("review-receipt"), Value("review-receipt-sha256")), null);

        string? Value(string key) => protocolV2 ? command.Options.GetValueOrDefault(key) : null;
        WorkspacePath? Path(string key) => Value(key) is { } value ? new WorkspacePath(value) : null;
    }

    private static SkyrimNpcFinishCoreCommandBindingResult Invalid(string message) => new(null, message);
}
