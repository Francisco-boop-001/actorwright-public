using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimHeadPartChoiceCommandHandler(
    ISkyrimHeadPartChoiceService choiceService,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryParse(command, out ParsedRequest parsed, out string errorMessage))
            return Usage(command.Json, errorMessage);

        SkyrimHeadPartChoiceResult result = await choiceService.SearchAsync(
            new SkyrimHeadPartChoiceRequest(
                parsed.DataRoot,
                parsed.Plugins,
                parsed.Race,
                parsed.Sex,
                parsed.Type,
                parsed.Search),
            cancellationToken);
        var response = new Response(
            "1",
            parsed.Race.ToString(),
            parsed.Sex.ToString().ToLowerInvariant(),
            parsed.Type.ToWireName(),
            result.Candidates.Select(CandidateResponse.From).ToImmutableArray(),
            result.Diagnostics);
        Write(
            response,
            command.Json,
            $"headpart choices: {response.Candidates.Length} compatible {response.Type} record(s)");
        return result.Accepted ? Exit(result.Diagnostics) : CommandExitCode.ValidationFailure;
    }

    private static bool TryParse(
        ParsedCommand command,
        out ParsedRequest request,
        out string errorMessage)
    {
        request = default!;
        string? editionValue = command.Options.GetValueOrDefault("edition") ??
                               command.Options.GetValueOrDefault("game");
        if (editionValue is null ||
            !GameEditionExtensions.TryParseWireName(editionValue, out GameEdition edition) ||
            edition != GameEdition.SkyrimSpecialEdition)
        {
            errorMessage = "headpart choices requires --edition skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("data-root", out string? dataRootValue) ||
            string.IsNullOrWhiteSpace(dataRootValue))
        {
            errorMessage = "headpart choices requires --data-root <copied Data>.";
            return false;
        }
        if (!command.Options.TryGetValue("plugins", out string? pluginValue) ||
            string.IsNullOrWhiteSpace(pluginValue))
        {
            errorMessage = "headpart choices requires an ascending comma-separated --plugins order.";
            return false;
        }
        if (!command.Options.TryGetValue("race", out string? raceValue) ||
            !FormReference.TryParse(raceValue, out FormReference race))
        {
            errorMessage = "headpart choices requires --race Plugin|0xFormID.";
            return false;
        }
        NpcSex? sex = command.Options.GetValueOrDefault("sex")?.Trim().ToLowerInvariant() switch
        {
            "female" => NpcSex.Female,
            "male" => NpcSex.Male,
            _ => null
        };
        if (sex is null)
        {
            errorMessage = "headpart choices requires --sex female|male.";
            return false;
        }
        if (!command.Options.TryGetValue("type", out string? typeValue) ||
            !NpcHeadPartTypeExtensions.TryParseWireName(typeValue, out NpcHeadPartType type))
        {
            errorMessage = "headpart choices requires a typed --type such as face, eyes, hair, or eyebrows.";
            return false;
        }

        try
        {
            var plugins = pluginValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item))
                .ToImmutableArray();
            if (plugins.IsDefaultOrEmpty)
            {
                errorMessage = "headpart choices requires at least one plugin.";
                return false;
            }
            request = new ParsedRequest(
                new WorkspacePath(dataRootValue),
                plugins,
                race,
                sex.Value,
                type,
                command.Options.GetValueOrDefault("search") ??
                command.Options.GetValueOrDefault("query"));
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static CommandExitCode Exit(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json
            ? JsonSerializer.Serialize(response, JsonOptions)
            : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json
            ? JsonSerializer.Serialize(response, JsonOptions)
            : humanMessage);

    private sealed record ParsedRequest(
        WorkspacePath DataRoot,
        ImmutableArray<PluginName> Plugins,
        FormReference Race,
        NpcSex Sex,
        NpcHeadPartType Type,
        string? Search);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record Response(
        string SchemaVersion,
        string Race,
        string Sex,
        string Type,
        ImmutableArray<CandidateResponse> Candidates,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record CandidateResponse(
        string Reference,
        string FormId,
        string EditorId,
        string? Name,
        string? ModelNif,
        bool IsExtra,
        bool SupportsMale,
        bool SupportsFemale,
        string RaceMatch,
        ImmutableArray<string> ExtraParts,
        ImmutableArray<PreviewModelResponse> PreviewModels,
        string ProviderPlugin,
        string ProviderPath,
        string ProviderSha256)
    {
        public static CandidateResponse From(SkyrimHeadPartChoiceCandidate candidate) => new(
            candidate.Reference.ToString(),
            candidate.Reference.FormId.ToString(),
            candidate.EditorId,
            candidate.Name,
            candidate.ModelNif?.Value,
            candidate.IsExtra,
            candidate.SupportsMale,
            candidate.SupportsFemale,
            candidate.RaceMatch.ToString(),
            candidate.ExtraParts.Select(item => item.ToString()).ToImmutableArray(),
            candidate.PreviewModels.Select(PreviewModelResponse.From).ToImmutableArray(),
            candidate.Provider.Plugin.Value,
            candidate.Provider.Path.Value,
            candidate.Provider.Sha256.Value);
    }

    private sealed record PreviewModelResponse(
        string Reference,
        string ModelNif,
        string ProviderPlugin,
        string ProviderSha256,
        bool IsRoot)
    {
        public static PreviewModelResponse From(SkyrimHeadPartPreviewModel model) => new(
            model.Reference.ToString(),
            model.ModelNif.Value,
            model.Provider.Plugin.Value,
            model.Provider.Sha256.Value,
            model.IsRoot);
    }
}
