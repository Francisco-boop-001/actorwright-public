using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ChangeTrackingCommandHandler(IChangeTrackingService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) || !command.Options.TryGetValue("session", out var session)) return Usage(command.Json, "changes list requires --edition|--game and --session.");
        try
        {
            var result = await service.ListAsync(new ChangeListRequest(edition, new WorkspacePath(session)), cancellationToken);
            output.WriteLine(command.Json ? JsonSerializer.Serialize(new Response(result.Succeeded, result.Edition, result.Changes, result.Diagnostics), JsonOptions) : $"changes list: {result.Changes.Length} changed records");
            return result.Succeeded
                ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }
    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private sealed record Response(bool Succeeded, GameEdition Edition, ImmutableArray<ChangedRecord> Changes, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record ErrorResponse(string Code, string Message);
}
