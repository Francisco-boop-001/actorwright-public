using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FormChoiceCommandHandler(IFormChoiceService service, TextWriter output, TextWriter error)
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
        var response = FormChoiceResponse.From(result);
        Write(response, command.Json, $"forms: {response.Candidates.Length} candidates");
        return ExitCodeForDiagnostics(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out FormChoiceSearchRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "forms search requires --edition fallout4|skyrimse.";
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
            errorMessage = "forms search requires an explicit --data-root or --plugin path.";
            return false;
        }

        var signatureText = command.Options.GetValueOrDefault("type") ?? command.Options.GetValueOrDefault("signature");
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            errorMessage = "forms search requires --type <signature[,signature...]> (for example NPC_ or RACE).";
            return false;
        }

        try
        {
            var signatures = ImmutableArray.CreateBuilder<RecordSignature>();
            foreach (var value in signatureText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                signatures.Add(new RecordSignature(value.ToUpperInvariant()));

            var plugins = ImmutableArray<PluginName>.Empty;
            var pluginValue = command.Options.GetValueOrDefault("plugins");
            if (pluginValue is null && pluginOption is not null)
                pluginValue = Path.GetFileName(pluginOption);
            if (!string.IsNullOrWhiteSpace(pluginValue))
            {
                var pluginBuilder = ImmutableArray.CreateBuilder<PluginName>();
                foreach (var value in pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    pluginBuilder.Add(new PluginName(value));
                plugins = pluginBuilder.ToImmutable();
            }

            FormId? formId = null;
            if (command.Options.TryGetValue("form-id", out var formIdValue))
            {
                if (!FormId.TryParse(formIdValue, out var parsed))
                {
                    errorMessage = "--form-id must be hexadecimal, for example 0x00000800.";
                    return false;
                }
                formId = parsed;
            }

            var allowNull = false;
            if (command.Options.TryGetValue("allow-null", out var allowNullValue) &&
                !bool.TryParse(allowNullValue, out allowNull))
            {
                errorMessage = "--allow-null must be true or false.";
                return false;
            }

            request = new FormChoiceSearchRequest(edition, new WorkspacePath(dataRootValue), signatures.ToImmutable(),
                command.Options.GetValueOrDefault("query") ?? command.Options.GetValueOrDefault("search"),
                formId, allowNull, plugins);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
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

    private void Write<T>(T response, bool json, string humanMessage)
    {
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);
    }

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record FormChoiceResponse(string SchemaVersion, string Edition, bool AllowNull,
        ImmutableArray<FormChoiceCandidateResponse> Candidates, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FormChoiceResponse From(FormChoiceSearchResult result) => new("1", result.Edition.ToWireName(),
            result.AllowNull, result.Candidates.Select(candidate => new FormChoiceCandidateResponse(
                candidate.Plugin.Value, candidate.FormId.ToString(), candidate.Signature.Value, candidate.EditorId,
                candidate.Name, candidate.IsDeleted, candidate.Provenance.Kind.ToString(),
                candidate.Provenance.OverrideChain.Select(plugin => plugin.Value).ToImmutableArray())).ToImmutableArray(),
            result.Diagnostics);
    }

    private sealed record FormChoiceCandidateResponse(string Plugin, string FormId, string Signature,
        string? EditorId, string? Name, bool IsDeleted, string ProvenanceKind, ImmutableArray<string> OverrideChain);
}
