using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimRaceMenuPaintChoiceCommandHandler(
    ISkyrimRaceMenuPaintChoiceService choiceService,
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

        SkyrimRaceMenuPaintChoiceResult result = await choiceService.SearchAsync(
            new SkyrimRaceMenuPaintChoiceRequest(
                parsed.DataRoot,
                parsed.Plugins,
                parsed.Category,
                parsed.Search),
            cancellationToken);
        var response = new Response(
            "1",
            parsed.Category.ToWireName(),
            result.Candidates.Select(CandidateResponse.From).ToImmutableArray(),
            result.Summary,
            result.Diagnostics,
            result.RuntimeAuthority,
            result.TextureRenderAuthority);
        Write(
            response,
            command.Json,
            $"paint choices: {response.Candidates.Length} {response.Category} registration(s)");
        return Exit(result.Diagnostics);
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
            errorMessage = "paint choices requires --edition skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("data-root", out string? dataRootValue) ||
            string.IsNullOrWhiteSpace(dataRootValue))
        {
            errorMessage = "paint choices requires --data-root <copied Data>.";
            return false;
        }
        if (!command.Options.TryGetValue("plugins", out string? pluginValue) ||
            string.IsNullOrWhiteSpace(pluginValue))
        {
            errorMessage = "paint choices requires an ascending comma-separated --plugins order.";
            return false;
        }
        if (!command.Options.TryGetValue("category", out string? categoryValue) ||
            !SkyrimRaceMenuPaintCategoryExtensions.TryParseWireName(
                categoryValue,
                out SkyrimRaceMenuPaintCategory category))
        {
            errorMessage = "paint choices requires --category warpaint|body|hands|feet|face.";
            return false;
        }

        try
        {
            ImmutableArray<PluginName> plugins = pluginValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item))
                .ToImmutableArray();
            if (plugins.IsDefaultOrEmpty)
            {
                errorMessage = "paint choices requires at least one plugin.";
                return false;
            }
            request = new ParsedRequest(
                new WorkspacePath(dataRootValue),
                plugins,
                category,
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
        SkyrimRaceMenuPaintCategory Category,
        string? Search);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record Response(
        string SchemaVersion,
        string Category,
        ImmutableArray<CandidateResponse> Candidates,
        SkyrimRaceMenuPaintCatalogSummary? Summary,
        ImmutableArray<Diagnostic> Diagnostics,
        bool RuntimeAuthority,
        bool TextureRenderAuthority);

    private sealed record CandidateResponse(
        string RegisteredName,
        string DisplayName,
        string RegisteredPath,
        string CanonicalTexturePath,
        ImmutableArray<SlotResponse> TextureSlots,
        ImmutableArray<SourceResponse> Sources)
    {
        public static CandidateResponse From(SkyrimRaceMenuPaintChoiceCandidate candidate) =>
            new(
                candidate.RegisteredName,
                candidate.DisplayName,
                candidate.RegisteredPath.Value,
                candidate.CanonicalTexturePath.Value,
                candidate.TextureSlots.Select(SlotResponse.From).ToImmutableArray(),
                candidate.Sources.Select(SourceResponse.From).ToImmutableArray());
    }

    private sealed record SlotResponse(
        int Index,
        string Kind,
        string? RegisteredValue,
        string? TexturePath)
    {
        public static SlotResponse From(SkyrimRaceMenuPaintTextureSlot slot) =>
            new(
                slot.Index,
                slot.Kind.ToString(),
                slot.RegisteredValue,
                slot.TexturePath?.Value);
    }

    private sealed record SourceResponse(
        string ScriptPath,
        string ProviderKind,
        string ProviderPath,
        string ProviderSha256,
        string PexSha256,
        long PexLength)
    {
        public static SourceResponse From(SkyrimRaceMenuPaintRegistrationSource source) =>
            new(
                source.ScriptPath.Value,
                source.ProviderKind.ToString(),
                source.ProviderPath.Value,
                source.ProviderSha256.Value,
                source.PexSha256.Value,
                source.PexLength);
    }
}
