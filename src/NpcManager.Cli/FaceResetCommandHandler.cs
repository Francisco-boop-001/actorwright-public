using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceResetCommandHandler(IFaceResetService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message))
            return WriteUsageError(command.Json, message);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");

        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        FaceResetResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = FaceResetResponse.From(proposal, result);
        var status = response.Applied ? "face reset: APPLIED" : response.IsApplicable ? "face reset: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face reset: REFUSED" :
            "face reset: NO-OP";
        Write(response, command.Json, status);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out FaceResetRequest request, out string message)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out message) ||
            !TryFormId(command, out var npc, out message) ||
            !TryPath(command, "current", out var current, out message) ||
            !TryPath(command, "baseline", out var baseline, out message) ||
            !TryPath(command, "output", out var output, out message) ||
            !TrySection(command, out var section, out message)) return false;

        Sha256Hash? expected = null;
        if (command.Options.TryGetValue("expected-sha256", out var hash))
        {
            try { expected = new Sha256Hash(hash); }
            catch (ArgumentException exception) { message = exception.Message; return false; }
        }

        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            try { proposal = new WorkspacePath(proposalText); }
            catch (ArgumentException exception) { message = exception.Message; return false; }
        }

        request = new FaceResetRequest(edition, npc, current, baseline, output, section, expected,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        message = string.Empty;
        return true;
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        {
            message = "The command requires --game|--edition fallout4|skyrimse.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryFormId(ParsedCommand command, out FormId formId, out string message)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("npc") ?? command.Options.GetValueOrDefault("form-id");
        if (value is null || !FormId.TryParse(value, out formId))
        {
            message = "The command requires --npc|--form-id with a hexadecimal FormID.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string message)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value))
        {
            message = $"The command requires --{key}.";
            return false;
        }

        try
        {
            path = new WorkspacePath(value);
            message = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }
    }

    private static bool TrySection(ParsedCommand command, out FaceResetSection section, out string message)
    {
        section = default;
        if (!command.Options.TryGetValue("section", out var value) ||
            !FaceResetSectionExtensions.TryParseWireName(value, out section))
        {
            message = "The command requires --section face-parts|tints|vertex-morphs|bone-regions|skyrim-morphs|skyrim-tints.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        if (json) error.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else error.WriteLine($"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(humanMessage);
    }

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record FaceResetResponse(
        bool IsApplicable,
        bool Applied,
        string Edition,
        string FormId,
        string Section,
        string CurrentSnapshot,
        string BaselineSnapshot,
        string OutputSnapshot,
        string CurrentSha256,
        string BaselineSha256,
        string? OutputSha256,
        ImmutableArray<MutationChange> Changes,
        ImmutableArray<string> PreservedFields,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FaceResetResponse From(FaceResetProposal proposal, FaceResetResult? result) => new(
            proposal.IsApplicable,
            result?.Applied == true,
            proposal.Edition.ToWireName(),
            proposal.NpcFormId.ToString(),
            proposal.Section.ToWireName(),
            proposal.CurrentSnapshot.Value,
            proposal.BaselineSnapshot.Value,
            proposal.OutputSnapshot.Value,
            proposal.CurrentHash.Value,
            proposal.BaselineHash.Value,
            result?.OutputHash?.Value,
            proposal.Changes,
            proposal.PreservedFields,
            result?.Diagnostics ?? proposal.Diagnostics);
    }
}
