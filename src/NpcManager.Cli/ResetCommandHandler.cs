using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ResetCommandHandler(INpcResetService service, TextWriter output, TextWriter error)
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
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");

        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        NpcResetResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = ResetResponse.From(proposal, result);
        var message = response.Applied ? "npc reset: APPLIED" : response.IsApplicable ? "npc reset: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "npc reset: REFUSED" : "npc reset: NO-OP";
        Write(response, command.Json, message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out NpcResetRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, command.Options.ContainsKey("current-plugin") ? "current-plugin" : "plugin", out var current, out errorMessage) ||
            !TryPath(command, "baseline", out var baseline, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) ||
            !TrySection(command, out var section, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage)) return false;

        Sha256Hash? expectedHash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText))
        {
            try { expectedHash = new Sha256Hash(hashText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            try { proposal = new WorkspacePath(proposalText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new NpcResetRequest(edition, current, baseline, output, formId, section, expectedHash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "The command requires --game|--edition fallout4|skyrimse."; return false; }
        errorMessage = string.Empty; return true;
    }

    private static bool TrySection(ParsedCommand command, out NpcResetSection section, out string errorMessage)
    {
        section = default;
        if (!command.Options.TryGetValue("section", out var value) || !NpcResetSectionExtensions.TryParseWireName(value, out section))
        { errorMessage = "The command requires --section identity|archetype|weight|stats|keywords|factions|inventory|outfits|perks|actor-effects|properties."; return false; }
        errorMessage = string.Empty; return true;
    }

    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; }
        try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }

    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc");
        if (value is null || !FormId.TryParse(value, out formId))
        { errorMessage = "The command requires --npc|--form-id with a hexadecimal FormID."; return false; }
        errorMessage = string.Empty; return true;
    }

    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        if (json) error.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else error.WriteLine($"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(humanMessage);
    }

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private sealed record ErrorResponse(string Code, string Message);

    private sealed record ResetResponse(bool IsApplicable, bool Applied, string Edition, string Section,
        string CurrentPlugin, string BaselinePlugin, string OutputPlugin, string FormId, string CurrentSha256,
        string BaselineSha256, string? OutputSha256, ImmutableArray<MutationChange> Changes,
        ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ResetResponse From(NpcResetProposal proposal, NpcResetResult? result) => new(
            proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.Section.ToWireName(),
            proposal.CurrentPlugin.Value, proposal.BaselinePlugin.Value, proposal.OutputPlugin.Value,
            proposal.TargetFormId.ToString(), proposal.CurrentHash.Value, proposal.BaselineHash.Value,
            result?.OutputHash?.Value, proposal.Changes, proposal.PreservedFields,
            result?.Diagnostics ?? proposal.Diagnostics);
    }
}
