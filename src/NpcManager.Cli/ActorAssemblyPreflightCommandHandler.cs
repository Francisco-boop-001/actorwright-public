using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ActorAssemblyPreflightCommandHandler(
    IActorAssemblyPreflightService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var allowed = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "contract", "contract-sha256");
        if (command.Positionals.Length != 3 || !command.Positionals.SequenceEqual(["npc", "assembly", "preflight"], StringComparer.OrdinalIgnoreCase))
            return Usage(command, "npc assembly preflight accepts no positional arguments beyond the command name.");
        if (command.DuplicateOptions.Length > 0)
            return Usage(command, $"Duplicate option '--{command.DuplicateOptions[0]}' is not allowed.");
        var unknown = command.Options.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null) return Usage(command, $"Unknown option '--{unknown}'.");
        if (!command.Options.TryGetValue("contract", out var contractValue) || !command.Options.TryGetValue("contract-sha256", out var hashValue))
            return Usage(command, "npc assembly preflight requires --contract <absolute-K-local.json> and --contract-sha256 <SHA256>.");
        if (hashValue.Length != 64 || hashValue.Any(character => !Uri.IsHexDigit(character)))
            return Usage(command, "--contract-sha256 must be exactly 64 hexadecimal characters without whitespace.");
        try
        {
            var result = await service.PreflightAsync(new ActorAssemblyPreflightRequest(
                new WorkspacePath(contractValue), new Sha256Hash(hashValue)), cancellationToken);
            if (result.Error is not null)
            {
                output.WriteLine(JsonSerializer.Serialize(ProjectError(result.Error), JsonOptions));
                return DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Error.Diagnostics);
            }
            if (result.Artifact is null) return Usage(command, "The preflight service returned no result envelope.");
            output.WriteLine(JsonSerializer.Serialize(ProjectArtifact(result.Artifact), JsonOptions));
            return result.Artifact.Outcome is ActorAssemblyOutcome.Pass or ActorAssemblyOutcome.NotApplicable
                ? CommandExitCode.Success : CommandExitCode.ValidationFailure;
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException exception) { return Usage(command, exception.Message); }
    }

    private CommandExitCode Usage(ParsedCommand command, string message)
    {
        error.WriteLine(command.Json ? JsonSerializer.Serialize(new { code = "usage-error", message }, JsonOptions) : $"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static object ProjectError(ActorAssemblyPreflightErrorArtifact artifact) => new
    {
        schemaVersion = artifact.SchemaVersion,
        artifactKind = artifact.ArtifactKind,
        contractAdmitted = artifact.ContractAdmitted,
        diagnostics = artifact.Diagnostics.Select(ProjectDiagnostic).ToImmutableArray()
    };

    private static object ProjectArtifact(ActorAssemblyPreflightArtifact artifact) => new
    {
        schemaVersion = artifact.SchemaVersion,
        artifactKind = artifact.ArtifactKind,
        admitted = artifact.Admitted,
        outcome = WireOutcome(artifact.Outcome),
        contractSha256 = artifact.ContractSha256.Value.ToUpperInvariant(),
        packageManifestSha256 = artifact.PackageManifestSha256.Value.ToUpperInvariant(),
        baseNpcEvidence = new
        {
            plugin = artifact.BaseNpcEvidence.Plugin.Value,
            declaredFormId = artifact.BaseNpcEvidence.DeclaredFormId.ToString(),
            typedRecord = ProjectRecordObservation(artifact.BaseNpcEvidence.TypedRecord),
            rawRecord = ProjectRecordObservation(artifact.BaseNpcEvidence.RawRecord),
            outcome = WireOutcome(artifact.BaseNpcEvidence.Outcome)
        },
        placedReferenceEvidence = artifact.PlacedReferenceEvidence is null ? null : ProjectPlaced(artifact.PlacedReferenceEvidence),
        diagnosticTarget = artifact.DiagnosticTarget,
        checks = artifact.Checks.Select(ProjectCheck).ToImmutableArray(),
        noWrite = artifact.NoWrite,
        runtimeAuthority = artifact.RuntimeAuthority
    };

    private static object ProjectPlaced(ActorAssemblyPlacedReferenceEvidence evidence) => new
    {
        plugin = evidence.Plugin.Value,
        declaredFormId = evidence.DeclaredFormId.ToString(),
        typedRecord = ProjectRecordObservation(evidence.TypedRecord),
        rawRecord = ProjectRecordObservation(evidence.RawRecord),
        typedBase = ProjectReferenceObservation(evidence.TypedBase),
        rawNameBase = ProjectReferenceObservation(evidence.RawNameBase),
        outcome = WireOutcome(evidence.Outcome)
    };

    private static object ProjectRecordObservation(ActorAssemblyRecordObservation observation) => observation.Status == ActorAssemblyObservationStatus.Found
        ? new { status = "found", signature = observation.Signature!, formId = observation.FormId!.Value.ToString() }
        : new { status = WireObservation(observation.Status), reason = observation.Reason ?? "No record evidence was available." };

    private static object ProjectReferenceObservation(ActorAssemblyReferenceObservation observation) => observation.Status == ActorAssemblyObservationStatus.Found
        ? new { status = "found", plugin = observation.Plugin!.Value.Value, formId = observation.FormId!.Value.ToString() }
        : new { status = WireObservation(observation.Status), reason = observation.Reason ?? "No reference evidence was available." };

    private static object ProjectCheck(ActorAssemblyCheck check) => new
    {
        code = check.Code,
        outcome = WireOutcome(check.Outcome),
        message = check.Message,
        evidence = check.Evidence.Select(evidence => new EvidenceOutput(
            WireEvidenceKind(evidence.Kind), evidence.Value,
            evidence.Sha256 is null ? null : evidence.Sha256.Value.Value.ToUpperInvariant())).ToImmutableArray()
    };

    private static object ProjectDiagnostic(Diagnostic diagnostic) => new { code = diagnostic.Code, severity = diagnostic.Severity.ToString().ToLowerInvariant(), message = diagnostic.Message };
    private static string WireOutcome(ActorAssemblyOutcome value) => value switch { ActorAssemblyOutcome.Pass => "pass", ActorAssemblyOutcome.Blocked => "blocked", ActorAssemblyOutcome.Unknown => "unknown", _ => "notApplicable" };
    private static string WireObservation(ActorAssemblyObservationStatus value) => value switch { ActorAssemblyObservationStatus.Found => "found", ActorAssemblyObservationStatus.Missing => "missing", _ => "unknown" };
    private static string WireEvidenceKind(ActorAssemblyEvidenceKind value) => value switch { ActorAssemblyEvidenceKind.Contract => "contract", ActorAssemblyEvidenceKind.Manifest => "manifest", ActorAssemblyEvidenceKind.File => "file", ActorAssemblyEvidenceKind.PackageFile => "packageFile", ActorAssemblyEvidenceKind.Record => "record", _ => "decision" };

    private sealed record EvidenceOutput(string Kind, string Value, string? Sha256);
}
