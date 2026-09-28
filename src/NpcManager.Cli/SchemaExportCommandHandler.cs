using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SchemaExportCommandHandler(ISchemaExportService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("output", out var outputPath))
            return ValueTask.FromResult(Usage(
                command.Json,
                "schema export requires --output <new-json-file>.",
                output,
                error));

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CommandExitCode.Cancelled);

        var request = new SchemaExportRequest(command.Options.GetValueOrDefault("command"),
            new WorkspacePath(outputPath));
        var result = service.Export(request);
        var response = new
        {
            schemaVersion = "1",
            command = result.CommandName,
            output = result.Output.Value,
            written = result.Written,
            diagnostics = result.Diagnostics
        };
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else
            output.WriteLine(result.Written ? $"schema export: PASS {result.Output.Value}" : "schema export: REFUSED");

        if (!result.Written)
            foreach (var diagnostic in result.Diagnostics) error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        if (result.Written)
            return ValueTask.FromResult(CommandExitCode.Success);
        if (result.Diagnostics.Any(item =>
                item.Code == "schema-command-unknown"))
            return ValueTask.FromResult(CommandExitCode.UsageError);
        return ValueTask.FromResult(
            DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private static CommandExitCode Usage(bool json, string message, TextWriter output, TextWriter error)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = "1", error = "usage", message }, JsonOptions));
        else error.WriteLine(message);
        return CommandExitCode.UsageError;
    }
}
