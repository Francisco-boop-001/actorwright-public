using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RecordListCommandHandler(
    IRecordListingService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = await service.ListAsync(request, cancellationToken);
        var response = new RecordResponse("1", result.Edition.ToWireName(),
            result.Plugins.Select(item => item.Value).ToImmutableArray(),
            result.Records.Select(record => new ListedRecordResponse(record.Plugin.Value,
                record.FormId.ToString(), record.Signature, record.EditorId, record.Name,
                record.IsNpc, record.IsDeleted, record.RawRecordSha256)).ToImmutableArray(),
            result.Diagnostics);
        Write(response, command.Json,
            result.IsValid ? $"records list: {response.Records.Length} records across {response.Plugins.Length} plugins" :
            "records list: REFUSED");
        return Exit(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out RecordListRequest request,
        out string errorMessage)
    {
        request = default!;
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition))
        {
            errorMessage = "records list requires --edition|--game fallout4|skyrimse.";
            return false;
        }

        WorkspacePath dataRoot;
        ImmutableArray<PluginName> pluginOrder;
        try
        {
            if (command.Options.TryGetValue("plugin", out var pluginPathValue))
            {
                var pluginPath = new WorkspacePath(pluginPathValue);
                var parent = Path.GetDirectoryName(pluginPath.Value);
                if (parent is null)
                {
                    errorMessage = "--plugin must have an explicit parent Data root.";
                    return false;
                }
                dataRoot = new WorkspacePath(parent);
                pluginOrder = [new PluginName(Path.GetFileName(pluginPath.Value))];
            }
            else if (command.Options.TryGetValue("data-root", out var dataRootValue))
            {
                dataRoot = new WorkspacePath(dataRootValue);
                if (!TryParsePlugins(command.Options.GetValueOrDefault("plugins"), out pluginOrder,
                        out errorMessage)) return false;
            }
            else
            {
                errorMessage = "records list requires --data-root or --plugin.";
                return false;
            }

            var signature = command.Options.GetValueOrDefault("signature");
            if (!ValidateText(signature, 8, out errorMessage, "--signature")) return false;
            var search = command.Options.GetValueOrDefault("search");
            if (!ValidateText(search, 256, out errorMessage, "--search")) return false;
            request = new RecordListRequest(edition, dataRoot, pluginOrder,
                string.IsNullOrWhiteSpace(signature) ? null : signature.Trim(),
                string.IsNullOrWhiteSpace(search) ? null : search.Trim());
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryParsePlugins(string? value,
        out ImmutableArray<PluginName> plugins,
        out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            plugins = [];
            errorMessage = string.Empty;
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<PluginName>();
        try
        {
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.Add(new PluginName(token));
        }
        catch (ArgumentException exception)
        {
            plugins = [];
            errorMessage = exception.Message;
            return false;
        }
        if (builder.Count == 0)
        {
            plugins = [];
            errorMessage = "--plugins requires at least one plugin name.";
            return false;
        }
        plugins = builder.ToImmutable();
        errorMessage = string.Empty;
        return true;
    }

    private static bool ValidateText(string? value, int maxLength, out string errorMessage, string option)
    {
        if (value is null)
        {
            errorMessage = string.Empty;
            return true;
        }
        if (value.Length == 0 || value.Length > maxLength || value != value.Trim() || value.Any(char.IsControl))
        {
            errorMessage = $"{option} must be non-empty, trimmed, and at most {maxLength} characters.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) :
            $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private static CommandExitCode Exit(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private sealed record RecordResponse(string SchemaVersion, string Edition,
        ImmutableArray<string> Plugins, ImmutableArray<ListedRecordResponse> Records,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ListedRecordResponse(string Plugin, string FormId, string Signature,
        string? EditorId, string? Name, bool IsNpc, bool IsDeleted, string? RawRecordSha256);

    private sealed record ErrorResponse(string Code, string Message);
}
