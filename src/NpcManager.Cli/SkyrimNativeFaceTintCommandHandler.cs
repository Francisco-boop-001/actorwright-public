using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimNativeFaceTintCommandHandler(
    ISkyrimNativeFaceTintPipelineService service,
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
        {
            return Usage(command.Json, message);
        }

        SkyrimNativeFaceTintPipelineResult result = await service.BuildAsync(
            request, cancellationToken).ConfigureAwait(false);
        var response = new NativeFaceTintResponse(
            result.Written, request.OutputPath.Value, result.Artifact,
            result.PluginAuthorities, result.MaskAuthorities, result.Diagnostics);
        Write(response, command.Json, result.Written
            ? "facegen build-tint-native: PASS"
            : "facegen build-tint-native: REFUSED");

        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        }
        return result.Written ? CommandExitCode.Success : CommandExitCode.ValidationFailure;
    }

    private static bool TryBuildRequest(
        ParsedCommand command,
        out SkyrimNativeFaceTintPipelineRequest request,
        out string message)
    {
        request = default!;
        var editionText = command.Options.GetValueOrDefault("edition") ??
                          command.Options.GetValueOrDefault("game");
        if (editionText is null ||
            !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            edition != GameEdition.SkyrimSpecialEdition)
        {
            message = "facegen build-tint-native requires --game skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
        {
            message = "facegen build-tint-native requires --data-root <copied-Data-root>.";
            return false;
        }
        if (!command.Options.TryGetValue("plugins", out var pluginText) ||
            string.IsNullOrWhiteSpace(pluginText))
        {
            message = "facegen build-tint-native requires --plugins Skyrim.esm,Plugin.esp in ascending load order.";
            return false;
        }
        if (!command.Options.TryGetValue("npc", out var npcText) ||
            !FormReference.TryParse(npcText, out var npc))
        {
            message = "facegen build-tint-native requires --npc Plugin.esp|0xXXXXXXXX.";
            return false;
        }
        if (!command.Options.TryGetValue("race", out var raceText) ||
            !FormReference.TryParse(raceText, out var race))
        {
            message = "facegen build-tint-native requires --race Plugin.esm|0xXXXXXXXX.";
            return false;
        }
        if (!command.Options.TryGetValue("sex", out var sexText) ||
            !Enum.TryParse<NpcSex>(sexText, true, out var sex) || !Enum.IsDefined(sex))
        {
            message = "facegen build-tint-native requires --sex male|female.";
            return false;
        }
        if (!command.Options.TryGetValue("output", out var output))
        {
            message = "facegen build-tint-native requires --output <new-K-local-dds>.";
            return false;
        }

        try
        {
            var plugins = ImmutableArray.CreateBuilder<PluginName>();
            foreach (string value in pluginText.Split(',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                plugins.Add(new PluginName(value));
            }
            if (plugins.Count == 0)
            {
                message = "--plugins requires at least one plugin name.";
                return false;
            }
            request = new SkyrimNativeFaceTintPipelineRequest(
                edition, new WorkspacePath(dataRoot), plugins.ToImmutable(),
                npc, sex, race, new WorkspacePath(output));
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void Write<T>(T value, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        else output.WriteLine(human);
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

    private sealed record NativeFaceTintResponse(
        bool Written,
        string Output,
        SkyrimNativeFaceTintBuildArtifact? Artifact,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginAuthorities,
        ImmutableArray<SkyrimNativeFaceTintMaskAuthority> MaskAuthorities,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(
        string Code,
        ImmutableArray<Diagnostic> Diagnostics);
}
