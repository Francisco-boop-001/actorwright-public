using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class OutfitChoiceCommandHandler(IOutfitChoiceService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);
        var result = await service.SearchAsync(request, cancellationToken);
        var response = OutfitChoiceResponse.From(result);
        Write(response, command.Json, $"outfit list: {response.Candidates.Length} candidates");
        return ExitCodeForDiagnostics(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out OutfitChoiceSearchRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "outfit list requires --edition fallout4|skyrimse.";
            return false;
        }
        var pluginOption = command.Options.GetValueOrDefault("plugin");
        var dataRootValue = command.Options.GetValueOrDefault("data-root");
        if (dataRootValue is null && pluginOption is not null)
        {
            try { dataRootValue = Directory.GetParent(new WorkspacePath(pluginOption).Value)?.FullName; }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        if (dataRootValue is null)
        {
            errorMessage = "outfit list requires an explicit --data-root or --plugin path.";
            return false;
        }
        try
        {
            var pluginValue = command.Options.GetValueOrDefault("plugins");
            if (pluginValue is null && pluginOption is not null) pluginValue = Path.GetFileName(pluginOption);
            var plugins = string.IsNullOrWhiteSpace(pluginValue)
                ? ImmutableArray<PluginName>.Empty
                : pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => new PluginName(value)).ToImmutableArray();
            request = new OutfitChoiceSearchRequest(edition, new WorkspacePath(dataRootValue),
                command.Options.GetValueOrDefault("query") ?? command.Options.GetValueOrDefault("search"), plugins);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }

    private static CommandExitCode ExitCodeForDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record OutfitChoiceResponse(string SchemaVersion, string Edition,
        ImmutableArray<OutfitChoiceCandidateResponse> Candidates, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static OutfitChoiceResponse From(OutfitChoiceSearchResult result) => new("1", result.Edition.ToWireName(),
            result.Candidates.Select(candidate => new OutfitChoiceCandidateResponse(
                 candidate.Plugin.Value, candidate.FormId.ToString(), candidate.EditorId, candidate.Name,
                 candidate.Items.Select(item => item.ToString()).ToImmutableArray(), candidate.IsDeleted,
                 candidate.Provenance.Kind.ToString(), candidate.Provenance.OverrideChain
                    .Select(plugin => plugin.Value).ToImmutableArray(),
                 candidate.ItemReferences.IsDefault
                     ? []
                     : candidate.ItemReferences.Select(item => item.ToString()).ToImmutableArray()))
                .ToImmutableArray(), result.Diagnostics);
    }

    private sealed record OutfitChoiceCandidateResponse(string Plugin, string FormId, string? EditorId,
        string? Name, ImmutableArray<string> Items, bool IsDeleted, string ProvenanceKind,
        ImmutableArray<string> OverrideChain, ImmutableArray<string> ItemReferences);
}
