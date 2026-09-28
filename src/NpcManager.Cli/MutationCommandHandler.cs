using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler(INpcMutationService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunPatchAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildPatchRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);

        var proposal = await service.AnalyzeAsync(request, cancellationToken);
        NpcMutationResult? result = null;
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");
        if (apply)
            result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);

        var response = PatchResponse.From(proposal, result);
        Write(response, command.Json, response.Applied ? "npc patch: APPLIED" : response.IsApplicable ? "npc patch: PROPOSAL" : "npc patch: REFUSED");
        return ExitFor(response.Diagnostics);
    }

    public async ValueTask<CommandExitCode> RunVerifyAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildVerificationRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);
        var result = await service.VerifyAsync(request, cancellationToken);
        var response = VerifyResponse.From(result);
        Write(response, command.Json, response.IsValid ? "plugin verify: PASS" : "plugin verify: FAIL");
        return response.IsValid ? CommandExitCode.Success : ExitFor(response.Diagnostics);
    }

    private CommandExitCode WriteUsageError(bool json, string message)
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

    private void Write<T>(T response, bool json, string humanMessage)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(humanMessage);
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record PatchResponse(bool IsApplicable, bool Applied, string Edition, string InputPlugin, string OutputPlugin,
        string FormId, string InputSha256, string? OutputSha256, ImmutableArray<MutationChange> Changes,
        ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpcAidtPatch? Aidt = null)
    {
        public static PatchResponse From(NpcMutationProposal proposal, NpcMutationResult? result) => new(
            proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPlugin.Value,
            proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash.Value, result?.OutputHash?.Value,
            proposal.Changes, proposal.PreservedFields, (result?.Diagnostics ?? proposal.Diagnostics), proposal.Aidt);
    }

    private sealed record VerifyResponse(bool IsValid, ImmutableArray<MutationChange> ObservedChanges, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static VerifyResponse From(PluginVerificationResult result) => new(result.IsValid, result.ObservedChanges, result.Diagnostics);
    }
}
