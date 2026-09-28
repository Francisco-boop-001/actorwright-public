using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ProfileScanCommandHandler(
    IProfileScanService service,
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
        var result = await service.ScanAsync(request, cancellationToken);
        var response = new ProfileResponse("1", result.Edition.ToWireName(), result.DataRoot.Value,
            result.Plugins.Select(item => new PluginResponse(item.Plugin.Value, item.Bytes, item.Sha256.Value))
                .ToImmutableArray(), result.LoadOrderSha256?.Value, result.LoadOrderValid,
            result.Fingerprint.Value, result.Diagnostics);
        Write(response, command.Json,
            result.IsValid ? $"profile scan: PASS {response.Plugins.Length} plugins fingerprint={response.Fingerprint}" :
            "profile scan: REFUSED");
        return Exit(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out ProfileScanRequest request,
        out string errorMessage)
    {
        request = default!;
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition))
        {
            errorMessage = "profile scan requires --edition|--game fallout4|skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
        {
            errorMessage = "profile scan requires --data-root <copied Data root>.";
            return false;
        }
        try
        {
            WorkspacePath? loadOrder = null;
            if (command.Options.TryGetValue("load-order", out var loadOrderValue) ||
                command.Options.TryGetValue("loadorder", out loadOrderValue))
                loadOrder = new WorkspacePath(loadOrderValue);
            request = new ProfileScanRequest(edition, new WorkspacePath(dataRoot), loadOrder);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
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



    private sealed record ProfileResponse(string SchemaVersion, string Edition, string DataRoot,
        ImmutableArray<PluginResponse> Plugins, string? LoadOrderSha256, bool? LoadOrderValid,
        string Fingerprint, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record PluginResponse(string Name, long Bytes, string Sha256);

    private sealed record ErrorResponse(string Code, string Message);
}
