using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FacePoseCommandHandler(IFacePoseResolver resolver, TextWriter output, TextWriter error)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = await resolver.ResolveAsync(request, cancellationToken);
        var response = FacePoseResponse.From(result);
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else
            output.WriteLine(response.Resolved ? $"face pose: RESOLVED ({response.BonePoses.Length} bones, {response.VertexDeltas.Length} vertices)" : "face pose: REFUSED");
        return ExitFor(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out FacePoseResolveRequest request, out string errorMessage)
    {
        request = default!;
        var gameText = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (gameText is null || !GameEditionExtensions.TryParseWireName(gameText, out var edition))
        {
            errorMessage = "face pose resolve requires --game fallout4|skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npc) || npc.Value == 0)
        {
            errorMessage = "face pose resolve requires a non-zero hexadecimal --npc FormID.";
            return false;
        }
        if (!command.Options.TryGetValue("preset", out var presetText))
        {
            errorMessage = "face pose resolve requires --preset <K-local JSON file>.";
            return false;
        }
        try
        {
            request = new FacePoseResolveRequest(edition, npc, new WorkspacePath(presetText));
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
        if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message }));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private sealed record FacePoseResponse(
        bool Resolved,
        string Edition,
        string Npc,
        string SourceSha256,
        ImmutableArray<FaceBonePose> BonePoses,
        ImmutableArray<FaceVertexDeltaResult> VertexDeltas,
        ImmutableArray<string> AppliedChannels,
        ImmutableArray<string> CombinationOrder,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FacePoseResponse From(FacePoseResolveResult result) => new(
            result.IsResolved,
            result.Input.Edition.ToWireName(),
            result.Input.NpcFormId.ToString(),
            result.SourceHash.Value,
            result.BonePoses,
            result.VertexDeltas,
            result.AppliedChannels,
            result.CombinationOrder,
            result.Diagnostics);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
