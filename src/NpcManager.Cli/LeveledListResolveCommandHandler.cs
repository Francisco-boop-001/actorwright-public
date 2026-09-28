using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class LeveledListResolveCommandHandler(ILeveledListResolveService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await service.ResolveAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(ResolveResponse.From(result), JsonOptions) : result.Written ? "leveled-list resolve: PASS" : "leveled-list resolve: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out LeveledListResolveRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "leveled-list resolve requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("list", out var list) || !command.Options.TryGetValue("seed", out var seed) ||
            !command.Options.TryGetValue("output", out var output))
        { message = "leveled-list resolve requires --list proposal, --seed, and --output."; return false; }
        if (!long.TryParse(seed, out var parsedSeed)) { message = "--seed must be a signed 64-bit integer."; return false; }
        request = new LeveledListResolveRequest(edition, new WorkspacePath(list), parsedSeed, new WorkspacePath(output));
        message = string.Empty;
        return true;
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record ResolveResponse(bool Written, string? ArtifactKind, string? Edition, string? SourceProposal,
        string? InputSha256, string? ListFormId, string? EditorId, long? Seed, int? ListChanceRoll,
        bool? ListSuppressed, ImmutableArray<LeveledListResolutionSelection> Selections,
        ImmutableArray<string> ResolvedItems, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ResolveResponse From(LeveledListResolveResult result) => new(result.Written,
            result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.SourceProposal,
            result.Artifact?.InputSha256, result.Artifact?.ListFormId, result.Artifact?.EditorId,
            result.Artifact?.Seed, result.Artifact?.ListChanceRoll, result.Artifact?.ListSuppressed,
            result.Artifact?.Selections ?? [], result.Artifact?.ResolvedItems ?? [], result.Diagnostics);
    }
}
