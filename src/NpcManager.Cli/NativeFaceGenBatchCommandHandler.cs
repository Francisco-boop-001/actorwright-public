using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class NativeFaceGenBatchCommandHandler(
    IFaceGenBakeAllService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message))
            return Usage(command.Json, message);

        var progress = new BatchProgressSink(command.Json ? null : output);
        FaceGenBakeAllResult result = await service.RunAsync(
            request, progress, cancellationToken).ConfigureAwait(false);
        var response = new NativeFaceGenBatchResponse(
            "1", result.Status, result.Discovered, result.Baked,
            result.Skipped, result.Failed, RuntimeAuthority: false,
            result.Outcomes, progress.Events, result.Diagnostics);
        if (command.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            output.WriteLine(
                $"facegen bake-all-native: {result.Status.ToString().ToUpperInvariant()} " +
                $"({result.Baked} baked, {result.Skipped} skipped, " +
                $"{result.Failed} failed of {result.Discovered}; runtime authority false)");
        }

        return result.Status switch
        {
            FaceGenBakeAllStatus.Succeeded => CommandExitCode.Success,
            FaceGenBakeAllStatus.SomeFailed => CommandExitCode.GeneralFailure,
            FaceGenBakeAllStatus.Cancelled => CommandExitCode.Cancelled,
            FaceGenBakeAllStatus.Fatal =>
                DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics),
            _ => CommandExitCode.GeneralFailure
        };
    }

    private static bool TryBuildRequest(
        ParsedCommand command,
        out FaceGenBakeAllRequest request,
        out string message)
    {
        request = default!;
        string? editionText = command.Options.GetValueOrDefault("edition") ??
                              command.Options.GetValueOrDefault("game");
        if (editionText is null ||
            !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            edition != GameEdition.SkyrimSpecialEdition)
        {
            message = "facegen bake-all-native requires --game skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
        {
            message = "facegen bake-all-native requires --data-root <copied-Data-root>.";
            return false;
        }
        if (!command.Options.TryGetValue("plugins", out var pluginText) ||
            string.IsNullOrWhiteSpace(pluginText))
        {
            message = "facegen bake-all-native requires --plugins Skyrim.esm,Plugin.esp in ascending load order.";
            return false;
        }
        if (!command.Options.TryGetValue("output-root", out var outputRoot))
        {
            message = "facegen bake-all-native requires --output-root <empty-K-local-Data-root>.";
            return false;
        }

        try
        {
            ImmutableArray<PluginName> plugins = pluginText.Split(',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(value => new PluginName(value))
                .ToImmutableArray();
            if (plugins.IsDefaultOrEmpty)
            {
                message = "--plugins requires at least one plugin name.";
                return false;
            }
            PluginName? winningPlugin =
                command.Options.TryGetValue("winning-plugin", out var winner)
                    ? new PluginName(winner)
                    : null;
            request = new FaceGenBakeAllRequest(
                edition, new WorkspacePath(dataRoot), plugins,
                new WorkspacePath(outputRoot), winningPlugin);
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }

        message = string.Empty;
        return true;
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(
            new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json)
        {
            error.WriteLine(JsonSerializer.Serialize(
                new ErrorResponse("usage-error", diagnostics), JsonOptions));
        }
        else
        {
            error.WriteLine($"ERROR usage-error: {message}");
        }
        return CommandExitCode.UsageError;
    }

    private sealed class BatchProgressSink(TextWriter? writer) :
        IProgress<FaceGenBakeAllProgress>
    {
        private readonly ImmutableArray<FaceGenBakeAllProgress>.Builder _events =
            ImmutableArray.CreateBuilder<FaceGenBakeAllProgress>();

        internal ImmutableArray<FaceGenBakeAllProgress> Events =>
            _events.ToImmutable();

        public void Report(FaceGenBakeAllProgress value)
        {
            _events.Add(value);
            writer?.WriteLine(
                $"[{value.Completed}/{value.Total}] {value.Phase}: {value.Message}");
        }
    }

    private sealed record NativeFaceGenBatchResponse(
        string SchemaVersion,
        FaceGenBakeAllStatus Status,
        int Discovered,
        int Baked,
        int Skipped,
        int Failed,
        bool RuntimeAuthority,
        ImmutableArray<FaceGenNpcBakeResult> Outcomes,
        ImmutableArray<FaceGenBakeAllProgress> Progress,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(
        string Code,
        ImmutableArray<Diagnostic> Diagnostics);
}
