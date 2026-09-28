using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ChangeActionCommandHandler(IChangeActionService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) ||
            !command.Options.TryGetValue("session", out var session) || !command.Options.TryGetValue("record", out var recordText) ||
            !FormId.TryParse(recordText, out var record) || record.Value == 0 || !command.Options.TryGetValue("action", out var actionText) ||
            !TryParseAction(actionText, out var action) || !command.Options.TryGetValue("output", out var outputPath)) return Usage(command.Json, "changes update requires --edition|--game, --session, --record, --action reset|delete, and --output.");
        try
        {
            var result = await service.UpdateAsync(new ChangeActionRequest(edition, new WorkspacePath(session), record, command.Options.GetValueOrDefault("signature"), action, new WorkspacePath(outputPath)), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); else output.WriteLine(result.Written ? $"changes update: {actionText} proposal written" : "changes update: refused");
            return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics) : CommandExitCode.Success;
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private static bool TryParseAction(string value, out ChangeAction action)
    {
        switch (value)
        {
            case "reset": action = ChangeAction.Reset; return true;
            case "delete": action = ChangeAction.Delete; return true;
            default: action = default; return false;
        }
    }
    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private sealed record ErrorResponse(string Code, string Message);
}
