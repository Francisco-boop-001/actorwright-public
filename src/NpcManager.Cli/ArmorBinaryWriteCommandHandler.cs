using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ArmorBinaryWriteCommandHandler(IArmorBinaryWriteService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await service.WriteAsync(request, cancellationToken);
        var response = new Response(result.Written, result.Output.Value, result.TargetFormId?.ToString(), result.OutputSha256?.Value, result.Diagnostics);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(response, JsonOptions) : result.Written ? "armor write: PASS" : "armor write: REFUSED");
        return ExitCode(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out ArmorBinaryWriteRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "armor write requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("proposal", out var proposal) || !command.Options.TryGetValue("output", out var output))
        { message = "armor write requires --proposal and --output."; return false; }
        try { request = new ArmorBinaryWriteRequest(edition, new WorkspacePath(proposal), new WorkspacePath(output)); message = string.Empty; return true; }
        catch (ArgumentException exception) { message = exception.Message; return false; }
    }

    private static CommandExitCode ExitCode(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record Response(bool Written, string Output, string? TargetFormId, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);
}
