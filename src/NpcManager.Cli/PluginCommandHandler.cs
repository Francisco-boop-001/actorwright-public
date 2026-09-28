using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PluginCommandHandler(IPluginLoadOrderService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunResolveAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildResolveRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = await service.ResolveAsync(request, cancellationToken);
        var response = ResolveResponse.From(result);
        Write(response, command.Json, response.IsValid ? "plugins resolve-load-order: PASS" : "plugins resolve-load-order: REFUSED");
        return ExitFor(response.Diagnostics);
    }

    internal async ValueTask<CommandExitCode> RunValidateAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildValidateRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = await service.ValidateAsync(request, cancellationToken);
        var response = ValidateResponse.From(result);
        Write(response, command.Json, response.IsCompatible ? $"{command.Name}: PASS" : $"{command.Name}: REFUSED");
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildResolveRequest(ParsedCommand command, out PluginLoadOrderRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "plugins", out var plugins, out errorMessage) ||
            !TryLoadOrderPath(command, out var loadOrder, out errorMessage)) return false;
        request = new PluginLoadOrderRequest(edition, plugins, loadOrder);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryBuildValidateRequest(ParsedCommand command, out PluginCompatibilityRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "plugin", out var plugin, out errorMessage) ||
            !TryLoadOrderPath(command, out var loadOrder, out errorMessage)) return false;
        request = new PluginCompatibilityRequest(edition, plugin, loadOrder);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "The command requires --edition|--game fallout4|skyrimse."; return false; }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryLoadOrderPath(ParsedCommand command, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        var key = command.Options.ContainsKey("load-order") ? "load-order" : "loadorder";
        return TryPath(command, key, out path, out errorMessage);
    }

    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value))
        { errorMessage = $"The command requires --{key}."; return false; }
        try
        {
            path = new WorkspacePath(value);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        { errorMessage = exception.Message; return false; }
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        if (json) error.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else error.WriteLine($"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private sealed record ResolveResponse(bool IsValid, string Edition, string? SourceHash,
        ImmutableArray<EntryResponse> Entries, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ResolveResponse From(PluginLoadOrderResult result) => new(result.IsValid, result.Edition.ToWireName(),
            result.SourceHash?.Value, result.Entries.Select(EntryResponse.From).ToImmutableArray(), result.Diagnostics);
    }

    private sealed record ValidateResponse(bool IsCompatible, string Edition, string? Plugin,
        ImmutableArray<string> Masters, ResolveResponse LoadOrder, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ValidateResponse From(PluginCompatibilityResult result) => new(result.IsCompatible,
            result.Edition.ToWireName(), result.Plugin?.Value, result.Masters.Select(item => item.Value).ToImmutableArray(),
            ResolveResponse.From(result.LoadOrder), result.Diagnostics);
    }

    private sealed record EntryResponse(string Plugin, int Order, bool Enabled, bool Exists, ImmutableArray<string> Masters)
    {
        public static EntryResponse From(PluginLoadOrderResolvedEntry entry) => new(entry.Plugin.Value, entry.Order,
            entry.Enabled, entry.Exists, entry.Masters.Select(item => item.Value).ToImmutableArray());
    }

    private sealed record ErrorResponse(string Code, string Message);
}
