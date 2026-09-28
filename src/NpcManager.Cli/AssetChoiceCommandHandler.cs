using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class AssetChoiceCommandHandler(IAssetChoiceService service, TextWriter output, TextWriter error)
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
        var response = AssetChoiceResponse.From(result);
        Write(response, command.Json, $"assets: {response.Kind} {response.Candidates.Length} candidates");
        return ExitCodeForDiagnostics(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out AssetChoiceSearchRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "assets search requires --edition fallout4|skyrimse.";
            return false;
        }

        var kindValue = command.Options.GetValueOrDefault("kind");
        if (kindValue is null || !TryParseKind(kindValue, out var kind))
        {
            errorMessage = "assets search requires --kind mesh|headpart.";
            return false;
        }

        var pluginOption = command.Options.GetValueOrDefault("plugin");
        var dataRootValue = command.Options.GetValueOrDefault("data-root");
        if (dataRootValue is null && pluginOption is not null)
        {
            try
            {
                dataRootValue = Directory.GetParent(new WorkspacePath(pluginOption).Value)?.FullName;
            }
            catch (ArgumentException exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }
        if (dataRootValue is null)
        {
            errorMessage = "assets search requires an explicit --data-root or --plugin path.";
            return false;
        }

        try
        {
            var pluginValue = command.Options.GetValueOrDefault("plugins");
            if (pluginValue is null && pluginOption is not null)
                pluginValue = Path.GetFileName(pluginOption);
            var plugins = ImmutableArray<PluginName>.Empty;
            if (!string.IsNullOrWhiteSpace(pluginValue))
            {
                var pluginBuilder = ImmutableArray.CreateBuilder<PluginName>();
                foreach (var value in pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    pluginBuilder.Add(new PluginName(value));
                plugins = pluginBuilder.ToImmutable();
            }

            request = new AssetChoiceSearchRequest(edition, new WorkspacePath(dataRootValue), kind,
                command.Options.GetValueOrDefault("query") ?? command.Options.GetValueOrDefault("search"), plugins);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryParseKind(string value, out AssetChoiceKind kind)
    {
        if (string.Equals(value, "mesh", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetChoiceKind.Mesh;
            return true;
        }
        if (string.Equals(value, "headpart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "head-part", StringComparison.OrdinalIgnoreCase))
        {
            kind = AssetChoiceKind.HeadPart;
            return true;
        }
        kind = default;
        return false;
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

    private sealed record AssetChoiceResponse(string SchemaVersion, string Edition, string Kind,
        ImmutableArray<AssetChoiceCandidateResponse> Candidates, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static AssetChoiceResponse From(AssetChoiceSearchResult result) => new("1", result.Edition.ToWireName(),
            result.Kind.ToString().ToLowerInvariant(), result.Candidates.Select(AssetChoiceCandidateResponse.From).ToImmutableArray(),
            result.Diagnostics);
    }

    private sealed record AssetChoiceCandidateResponse(string Path, string ProviderStatus,
        ImmutableArray<AssetChoiceProviderResponse> Providers, string? Plugin, string? FormId, string? Signature,
        string? EditorId, string? Name, string? ProvenanceKind, ImmutableArray<string>? OverrideChain)
    {
        public static AssetChoiceCandidateResponse From(AssetChoiceCandidate candidate) =>
            new(candidate.Path.Value, candidate.Provider.Status.ToString().ToLowerInvariant(),
                candidate.Provider.Evidence.Select(provider => new AssetChoiceProviderResponse(
                    provider.Kind.ToString().ToLowerInvariant(), provider.Source, provider.Size, provider.Sha256.Value)).ToImmutableArray(),
                candidate.Plugin?.Value, candidate.FormId?.ToString(), candidate.Signature?.Value,
                candidate.EditorId, candidate.Name, candidate.Provenance?.Kind.ToString(),
                candidate.Provenance?.OverrideChain.Select(plugin => plugin.Value).ToImmutableArray());
    }

    private sealed record AssetChoiceProviderResponse(string Kind, string Source, long Size, string Sha256);
}
