using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PluginVerifyCommandHandler(IPluginVerifyService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) ||
            !command.Options.TryGetValue("before", out var before) || !command.Options.TryGetValue("after", out var after) ||
            !command.Options.TryGetValue("proposal", out var proposal))
            return Usage(command.Json, "plugin verify requires --edition|--game, --before, --after, and --proposal.");
        try
        {
            var result = await service.VerifyAsync(new PluginVerifyRequest(edition, new WorkspacePath(before), new WorkspacePath(after), new WorkspacePath(proposal)), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(new VerifyResponse(result.IsValid, result.ObservedChanges, result.Diagnostics), JsonOptions));
            else output.WriteLine(result.IsValid ? "plugin verify: PASS" : "plugin verify: FAIL");
            if (result.IsValid) return CommandExitCode.Success;
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private CommandExitCode Usage(bool json, string message)
    { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private sealed record ErrorResponse(string Code, string Message);
    private sealed record VerifyResponse(bool IsValid, ImmutableArray<MutationChange> ObservedChanges, ImmutableArray<Diagnostic> Diagnostics);
}
