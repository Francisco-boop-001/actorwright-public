using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FacePatchCommandHandler(INpcFacePatchService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return WriteUsageError(command.Json, errorMessage);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        NpcFacePatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = FacePatchResponse.From(proposal, result);
        var message = response.Applied ? "npc face-patch: APPLIED" : response.IsApplicable ? "npc face-patch: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "npc face-patch: REFUSED" : "npc face-patch: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out NpcFacePatchRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "plugin", out var plugin, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) ||
            !TryPath(command, "data-root", out var dataRoot, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage) ||
            !TryPatch(command, out var patch, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText))
        {
            try { hash = new Sha256Hash(hashText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            try { proposal = new WorkspacePath(proposalText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new NpcFacePatchRequest(edition, plugin, output, formId, patch, dataRoot, hash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPatch(ParsedCommand command, out NpcFacePatch patch, out string errorMessage)
    {
        patch = new NpcFacePatch(null, default);
        var hasHeadParts = command.Options.TryGetValue("headparts", out var headPartsText);
        var hasHairColor = command.Options.TryGetValue("hair-color", out var hairColorText);
        var hasReplacement = command.Options.TryGetValue("headpart-replace", out var replacementText);
        NpcHeadPartReplacement? replacement = null;
        if (hasReplacement)
        {
            var pieces = replacementText!.Split('=');
            if (hasHeadParts || pieces.Length != 2 || !FormReference.TryParse(pieces[0], out var old) ||
                !FormId.TryParse(pieces[1], out var next) || next.Value is < 0x800 or > 0xFFFFFF)
            { errorMessage = "--headpart-replace requires old Plugin|FormID=new local FormID and cannot combine with --headparts."; return false; }
            replacement = new NpcHeadPartReplacement(old, next);
        }
        if (!hasHeadParts && !hasHairColor && !hasReplacement) { errorMessage = "The command requires --headparts, --headpart-replace and/or --hair-color."; return false; }
        ImmutableArray<NpcHeadPartSelection>? headParts = null;
        if (hasHeadParts)
        {
            try
            {
                if (!headPartsText!.TrimStart().StartsWith('['))
                {
                    var compact = ImmutableArray.CreateBuilder<NpcHeadPartSelection>();
                    foreach (var token in headPartsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var separator = token.IndexOf('=');
                        if (separator <= 0 || separator == token.Length - 1 || !FormReference.TryParse(token[(separator + 1)..], out var compactReference) || !NpcHeadPartTypeExtensions.TryParseWireName(token[..separator], out var compactType))
                            throw new FormatException("Compact --headparts syntax is type=Plugin|FormID,... .");
                        compact.Add(new NpcHeadPartSelection(compactReference, compactType));
                    }
                    headParts = compact.ToImmutable();
                }
                else
                {
                    using var document = JsonDocument.Parse(headPartsText!);
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--headparts must be a JSON array.");
                    var builder = ImmutableArray.CreateBuilder<NpcHeadPartSelection>();
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("form", out var form) || !item.TryGetProperty("type", out var type))
                            throw new FormatException("Each headpart must contain string properties 'form' and 'type'.");
                        if (!FormReference.TryParse(form.GetString() ?? string.Empty, out var reference) ||
                            !NpcHeadPartTypeExtensions.TryParseWireName(type.GetString() ?? string.Empty, out var parsedType))
                            throw new FormatException("Headpart form must be Plugin|FormID and type must be misc|face|eyes|hair|facial-hair|scar|eyebrows|meatcaps|teeth|head-rear.");
                        builder.Add(new NpcHeadPartSelection(reference, parsedType));
                    }
                    headParts = builder.ToImmutable();
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
            { errorMessage = exception.Message; return false; }
        }
        var hairColor = default(OptionalFormReference);
        if (hasHairColor)
        {
            if (string.Equals(hairColorText, "none", StringComparison.OrdinalIgnoreCase)) hairColor = OptionalFormReference.Clear();
            else if (FormReference.TryParse(hairColorText!, out var reference)) hairColor = OptionalFormReference.Set(reference);
            else { errorMessage = "--hair-color must be none or Plugin|FormID."; return false; }
        }
        patch = new NpcFacePatch(headParts, hairColor, replacement);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition)) { errorMessage = "The command requires --game|--edition fallout4|skyrimse."; return false; }
        errorMessage = string.Empty; return true;
    }
    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; }
        try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }
    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc");
        if (value is null || !FormId.TryParse(value, out formId)) { errorMessage = "The command requires --npc|--form-id with a hexadecimal FormID."; return false; }
        errorMessage = string.Empty; return true;
    }
    private CommandExitCode WriteUsageError(bool json, string message)
    { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message }, JsonOptions)); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? (DiagnosticExitCodeClassifier.Classify(diagnostics)) : CommandExitCode.Success;

    private sealed record FacePatchResponse(bool IsApplicable, bool Applied, string Edition, string Plugin, string Output,
        string FormId, Sha256Hash InputSha256, Sha256Hash? OutputSha256, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields,
        ImmutableArray<NpcHeadPartProvider> Providers, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FacePatchResponse From(NpcFacePatchProposal proposal, NpcFacePatchResult? result) => new(
            proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPlugin.Value,
            proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash, result?.OutputHash, proposal.Changes,
            proposal.PreservedFields, proposal.Providers, result?.Diagnostics ?? proposal.Diagnostics);
    }
}
