using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class TemplateCommandHandler(INpcTemplateMaterializationService service, TextWriter output, TextWriter error)
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
        var proposal = await service.AnalyzeAsync(request, cancellationToken);
        NpcTemplateMaterializationResult? result = null;
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = TemplateResponse.From(proposal, result);
        Write(response, command.Json, response.Applied ? "npc materialize-template: APPLIED" : response.IsApplicable ? "npc materialize-template: PROPOSAL" : "npc materialize-template: REFUSED");
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out NpcTemplateMaterializationRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, command.Options.ContainsKey("input-plugin") ? "input-plugin" : "plugin", out var input, out errorMessage) ||
            !TryPath(command, "output", out var outputPath, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage)) return false;

        ImmutableHashSet<NpcTemplateCategory>? categories = null;
        if (command.Options.TryGetValue("categories", out var categoryText))
        {
            var builder = ImmutableHashSet.CreateBuilder<NpcTemplateCategory>();
            foreach (var value in categoryText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!NpcTemplateCategoryExtensions.TryParseWireName(value, out var category))
                {
                    errorMessage = $"--categories contains unknown template category '{value}'.";
                    return false;
                }
                if (!builder.Add(category))
                {
                    errorMessage = $"--categories contains duplicate category '{value}'.";
                    return false;
                }
            }
            if (builder.Count == 0) { errorMessage = "--categories must contain at least one category."; return false; }
            categories = builder.ToImmutable();
        }

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
        request = new NpcTemplateMaterializationRequest(edition, input, outputPath, formId, categories, expectedHash,
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
        if (!command.Options.TryGetValue("form-id", out var value) || !FormId.TryParse(value, out formId))
        { errorMessage = "The command requires a hexadecimal --form-id, for example 0x00000800."; return false; }
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
    private sealed record TemplateResponse(bool IsApplicable, bool Applied, string Edition, string InputPlugin, string OutputPlugin,
        string FormId, string InputSha256, string? OutputSha256, ImmutableArray<NpcTemplateCategoryEvidence> Categories,
        ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static TemplateResponse From(NpcTemplateMaterializationProposal proposal, NpcTemplateMaterializationResult? result) => new(
            proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPlugin.Value,
            proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash.Value, result?.OutputHash?.Value,
            proposal.Categories, proposal.Changes, proposal.PreservedFields, result?.Diagnostics ?? proposal.Diagnostics);
    }
}
