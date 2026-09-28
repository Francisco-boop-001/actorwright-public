using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PluginSurfaceAuditCommandHandler(
    IPluginSurfaceAuditService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) ||
            !command.Options.TryGetValue("before", out var before) ||
            !command.Options.TryGetValue("after", out var after))
        {
            return Usage(command.Json, "plugin audit requires --edition|--game, --before, and --after.");
        }

        try
        {
            if (command.Options.TryGetValue("normalize-master-index", out var normalize) && normalize is not ("true" or "1"))
                return Usage(command.Json, "--normalize-master-index accepts only true or 1.");
            var pluginsRootValue = command.Options.GetValueOrDefault("plugins-root") ??
                command.Options.GetValueOrDefault("data-root");
            var loadOrderValue = command.Options.GetValueOrDefault("load-order") ??
                command.Options.GetValueOrDefault("loadorder");
            if ((pluginsRootValue is null) != (loadOrderValue is null))
                return Usage(command.Json, "plugin audit provider resolution requires both --plugins-root and --load-order.");
            WorkspacePath? pluginsRoot = pluginsRootValue is null ? null : new WorkspacePath(pluginsRootValue);
            WorkspacePath? loadOrder = loadOrderValue is null ? null : new WorkspacePath(loadOrderValue);
            var result = await service.AuditAsync(
                new PluginSurfaceAuditRequest(edition, new WorkspacePath(before), new WorkspacePath(after),
                    pluginsRoot, loadOrder, command.Options.ContainsKey("normalize-master-index")),
                cancellationToken);
            if (command.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                output.WriteLine($"plugin audit: {(result.IsValid ? "PASS" : "FAIL")} " +
                    $"records before={result.BeforeRecordCount} after={result.AfterRecordCount} " +
                    $"added={result.AddedRecordCount} removed={result.RemovedRecordCount} " +
                    $"changed={result.ChangedRecordCount} risky={result.RiskyRecordSignatures.Length}");
            }

            if (result.IsValid) return CommandExitCode.Success;
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
}
