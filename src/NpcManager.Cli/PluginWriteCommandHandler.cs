using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PluginWriteCommandHandler(IPluginWriteService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) ||
            !command.Options.TryGetValue("proposal", out var proposal) || !command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "plugin write requires --edition|--game, --proposal, and --output.");
        if (command.Options.TryGetValue("no-overwrite", out var noOverwrite) &&
            !string.Equals(noOverwrite, "true", StringComparison.OrdinalIgnoreCase) && noOverwrite != "1")
            return Usage(command.Json, "--no-overwrite is a required safety invariant and cannot be disabled.");
        try
        {
            WorkspacePath? PathOption(string name) => command.Options.TryGetValue(name, out var path) ? new WorkspacePath(path) : null;
            var result = await service.WriteAsync(new PluginWriteRequest(edition, new WorkspacePath(proposal), new WorkspacePath(outputPath), true,
                PathOption("plugin"), command.Options.TryGetValue("expected-sha256", out var hash) ? new Sha256Hash(hash) : null,
                PathOption("data-root"), PathOption("private-root")), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(PluginWriteResponse.From(result), JsonOptions));
            else output.WriteLine(result.Applied ? "plugin write: APPLIED" : "plugin write: REFUSED");
            return result.Applied
                ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record PluginWriteResponse(bool Applied, string Proposal, string Output, string? OutputSha256,
        ImmutableArray<MutationChange> Changes, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static PluginWriteResponse From(PluginWriteResult result) => new(result.Applied, result.Proposal.Value,
            result.Output.Value, result.OutputHash?.Value, result.Changes, result.Diagnostics);
    }
}
