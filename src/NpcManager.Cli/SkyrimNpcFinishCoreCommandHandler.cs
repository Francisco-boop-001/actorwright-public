using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Plugins.Exceptions;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class SkyrimNpcFinishCoreCommandHandler(
    ISkyrimNpcFinishCoreService service,
    WorkspacePath workspaceRoot,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                "npc finish analyze" => await RunAnalyzeAsync(command, cancellationToken),
                "npc finish apply" => await RunApplyAsync(command, cancellationToken),
                "npc finish verify" => await RunVerifyAsync(command, cancellationToken),
                _ => Usage(command, "Unsupported Finish Core command.")
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(command, "cancelled", "Finish Core work was cancelled.", CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException or JsonException or RecordException)
        {
            if (IsValidateAll(command))
                return ValidationLoadFailure(command, exception.Message);
            return Failure(command, "finish-core-invalid", exception.Message,
                CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> RunAnalyzeAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreCommandBindingResult bound = SkyrimNpcFinishCoreCommandBinder.Bind(command, protocolV2: false);
        if (!bound.IsValid) return Usage(command, bound.ErrorMessage!);
        ExternalHeadPartInstallContextBindingResult contextBinding =
            ExternalHeadPartInstallContextBinder.Bind(command);
        if (!contextBinding.IsValid)
            return Usage(command, contextBinding.ErrorMessage!);
        (SkyrimNpcFinishCoreRequest? request, Sha256Hash requestHash, CommandExitCode? loadFailure) =
            await LoadRequestAsync(command, cancellationToken);
        if (loadFailure is not null)
            return loadFailure.Value;
        WorkspacePath proposalPath = bound.Binding!.Proposal;
        if (bound.Binding.ValidateAll)
        {
            SkyrimNpcFinishCoreValidationResult validation =
                await service.ValidateAnalyzeAsync(
                    request!, requestHash, proposalPath, cancellationToken);
            return WriteValidationResult(command, validation);
        }
        CommandExitCode? contextFailure = TryCreateContext(
            command,
            request!,
            contextBinding,
            out ExternalHeadPartInstallVerificationContext? installContext);
        if (contextFailure is not null)
            return contextFailure.Value;

        SkyrimNpcFinishCoreProposalResult result = installContext is not null &&
                request!.Authorities.ExternalHeadParts is not null
            ? await AnalyzeWithInstallContextAsync(
                request,
                requestHash,
                proposalPath,
                installContext,
                cancellationToken)
            : await service.AnalyzeAsync(
                request!, requestHash, proposalPath, cancellationToken);
        return WriteResult(command, new
        {
            schema = (request!.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier ||
                    request.Schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier) &&
                result.Proposal?.Schema is { } versionedProposalSchema
                ? versionedProposalSchema
                : SkyrimNpcFinishCoreProposal.SchemaIdentifier,
            command = command.Name,
            proposed = result.Proposed,
            status = result.Proposal?.Status.ToString() ?? SkyrimNpcFinishCoreStatus.Refused.ToString(),
            proposalPath = result.ProposalPath?.Value,
            proposalSha256 = result.ProposalSha256?.Value,
            diagnostics = result.Diagnostics
        }, result.Proposed
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> RunApplyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreCommandBindingResult bound = SkyrimNpcFinishCoreCommandBinder.Bind(command, protocolV2: false);
        if (!bound.IsValid) return Usage(command, bound.ErrorMessage!);
        ExternalHeadPartInstallContextBindingResult contextBinding =
            ExternalHeadPartInstallContextBinder.Bind(command);
        if (!contextBinding.IsValid)
            return Usage(command, contextBinding.ErrorMessage!);
        (SkyrimNpcFinishCoreRequest? request, Sha256Hash requestHash, CommandExitCode? loadFailure) =
            await LoadRequestAsync(command, cancellationToken);
        if (loadFailure is not null)
            return loadFailure.Value;
        WorkspacePath proposalPath = bound.Binding!.Proposal;
        Sha256Hash proposalHash = bound.Binding.ProposalSha256!.Value;
        byte[] proposalBytes = await ReadProposalBoundFileAsync(
            proposalPath, proposalHash, cancellationToken);
        SkyrimNpcFinishCoreProposal proposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            proposalBytes, workspaceRoot);
        proposalHash = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(proposalBytes);
        if (bound.Binding.ValidateAll)
        {
            SkyrimNpcFinishCoreValidationResult validation =
                await service.ValidateApplyAsync(
                    request!, requestHash, proposal, proposalHash, cancellationToken);
            return WriteValidationResult(command, validation);
        }
        if (proposal.RequestSha256 != requestHash || proposal.ProposalSha256 != proposalHash)
            return Failure(command, "finish-core-proposal-binding",
                "The proposal does not bind the exact request and supplied proposal hash.",
                CommandExitCode.ValidationFailure);
        if (request!.Authorities.ExternalHeadParts is not null &&
            !contextBinding.IsSpecified)
            return Failure(
                command,
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                "External Finish Core apply requires a complete ephemeral install context.",
                CommandExitCode.ValidationFailure);
        CommandExitCode? contextFailure = TryCreateContext(
            command,
            request!,
            contextBinding,
            out ExternalHeadPartInstallVerificationContext? installContext);
        if (contextFailure is not null)
            return contextFailure.Value;

        SkyrimNpcFinishCoreApplyResult result = installContext is not null &&
                request!.Authorities.ExternalHeadParts is not null
            ? await ApplyWithInstallContextAsync(
                request,
                requestHash,
                proposal,
                proposalHash,
                installContext,
                cancellationToken)
            : await service.ApplyAsync(
                request!, requestHash, proposal, proposalHash, cancellationToken);
        bool noChanges = result.Diagnostics.Any(item => item.Code == "finish-core-no-changes");
        return WriteResult(command, new
        {
            schema = result.Manifest?.Schema ??
                SkyrimNpcFinishCoreManifest.SchemaIdentifier,
            command = command.Name,
            applied = result.Applied,
            status = noChanges
                ? SkyrimNpcFinishCoreStatus.NoChanges.ToString()
                : result.Applied
                    ? SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired.ToString()
                    : SkyrimNpcFinishCoreStatus.Refused.ToString(),
            outputRoot = result.OutputRoot?.Value,
            archive = result.Archive?.Value,
            diagnostics = result.Diagnostics
        }, result.Applied || noChanges
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> RunVerifyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishVerifyCommandBindingResult bound =
            SkyrimNpcFinishVerifyCommandBinder.Bind(command, protocolV2: false);
        if (!bound.IsValid)
            return Usage(command, bound.ErrorMessage!);
        ExternalHeadPartInstallContextBindingResult contextBinding =
            ExternalHeadPartInstallContextBinder.Bind(command);
        if (!contextBinding.IsValid)
            return Usage(command, contextBinding.ErrorMessage!);
        WorkspacePath manifest = bound.Binding!.Manifest;
        Sha256Hash manifestHash = bound.Binding.ManifestSha256;
        byte[] manifestBytes = await ReadBoundFileAsync(
            manifest, manifestHash,
            bytes => SkyrimNpcFinishCoreDocumentCodec.CanonicalizeManifest(bytes, workspaceRoot),
            cancellationToken);
        bool externalManifest = IsExternalManifest(manifestBytes);
        SkyrimNpcFinishCoreVerificationResult result;
        if (contextBinding.IsSpecified && externalManifest)
        {
            SkyrimNpcFinishCoreManifest parsedManifest =
                SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                    manifestBytes, workspaceRoot);
            if (!ExternalHeadPartInstallContextBinder.TryReadTargetRace(
                    parsedManifest,
                    out FormReference targetRace,
                    out string targetRaceError))
                return Failure(
                    command,
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    targetRaceError,
                    CommandExitCode.ValidationFailure);
            ExternalHeadPartInstallVerificationContext installContext =
                contextBinding.CreateContext(targetRace);
            if (service is not ISkyrimNpcFinishCoreInstallContextService contextService)
                return Failure(
                    command,
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    "External Finish Core verification is unavailable without a typed install-context service.",
                    CommandExitCode.ValidationFailure);
            result = await contextService.VerifyWithInstallContextAsync(
                manifest,
                manifestHash,
                installContext,
                cancellationToken);
        }
        else
        {
            result = await service.VerifyAsync(
                manifest, manifestHash, cancellationToken);
        }
        return WriteResult(command, new
        {
            schema = result.Verification?.Schema ??
                SkyrimNpcFinishCoreVerification.SchemaIdentifier,
            command = command.Name,
            verified = result.Verified,
            status = result.Verification?.Status.ToString() ?? SkyrimNpcFinishCoreStatus.Refused.ToString(),
            verification = result.Verification,
            diagnostics = result.Diagnostics
        }, result.Verified
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<(
        SkyrimNpcFinishCoreRequest? Request,
        Sha256Hash Hash,
        CommandExitCode? Failure)> LoadRequestAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        WorkspacePath path = PathOption(command, "request");
        Sha256Hash hash = HashOption(command, "request-sha256");
        byte[] bytes = await ReadBoundFileAsync(path, hash,
            document => SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(document, workspaceRoot),
            cancellationToken);
        SkyrimNpcFinishCoreRequest request = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(bytes, workspaceRoot);
        return (request, SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, workspaceRoot), null);
    }

    private ValueTask<byte[]> ReadBoundFileAsync(
        WorkspacePath path, Sha256Hash expected, Func<byte[], byte[]> canonicalize,
        CancellationToken cancellationToken) =>
        new SkyrimNpcFinishCoreCommandDocumentReader(workspaceRoot).ReadBoundFileAsync(
            path, expected, canonicalize, cancellationToken);

    private ValueTask<byte[]> ReadProposalBoundFileAsync(
        WorkspacePath path, Sha256Hash expected, CancellationToken cancellationToken) =>
        new SkyrimNpcFinishCoreCommandDocumentReader(workspaceRoot).ReadProposalBoundFileAsync(
            path, expected, cancellationToken);
    private static WorkspacePath PathOption(ParsedCommand command, string name) =>
        new(command.Options[name]);

    private static Sha256Hash HashOption(ParsedCommand command, string name) =>
        new(command.Options[name]);

    private CommandExitCode? TryCreateContext(
        ParsedCommand command,
        SkyrimNpcFinishCoreRequest request,
        ExternalHeadPartInstallContextBindingResult binding,
        out ExternalHeadPartInstallVerificationContext? installContext)
    {
        installContext = null;
        if (!binding.IsSpecified ||
            request.Authorities.ExternalHeadParts is null)
            return null;
        if (!ExternalHeadPartInstallContextBinder.TryReadTargetRace(
                request,
                out FormReference targetRace,
                out string targetRaceError))
            return Failure(
                command,
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                targetRaceError,
                CommandExitCode.ValidationFailure);
        if (service is not ISkyrimNpcFinishCoreInstallContextService)
            return Failure(
                command,
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "External Finish Core context is unavailable without a typed install-context service.",
                CommandExitCode.ValidationFailure);
        installContext = binding.CreateContext(targetRace);
        return null;
    }

    private async ValueTask<SkyrimNpcFinishCoreProposalResult>
        AnalyzeWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestHash,
            WorkspacePath proposalPath,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        return await ((ISkyrimNpcFinishCoreInstallContextService)service)
            .AnalyzeWithInstallContextAsync(
                request,
                requestHash,
                proposalPath,
                installContext,
                cancellationToken);
    }

    private async ValueTask<SkyrimNpcFinishCoreApplyResult>
        ApplyWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestHash,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalHash,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        return await ((ISkyrimNpcFinishCoreInstallContextService)service)
            .ApplyWithInstallContextAsync(
                request,
                requestHash,
                proposal,
                proposalHash,
                installContext,
                cancellationToken);
    }

    private static bool IsExternalManifest(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return document.RootElement.TryGetProperty("schema", out JsonElement schema) &&
                schema.ValueKind == JsonValueKind.String &&
                schema.GetString() == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private CommandExitCode WriteResult(
        ParsedCommand command,
        object result,
        CommandExitCode exitCode)
    {
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else
            output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return exitCode;
    }

    private CommandExitCode WriteValidationResult(
        ParsedCommand command,
        SkyrimNpcFinishCoreValidationResult result) =>
        WriteResult(command, new
        {
            schema = SkyrimNpcFinishCoreValidationResult.SchemaIdentifier,
            command = command.Name,
            valid = result.Valid,
            phases = result.Phases,
            diagnostics = result.Diagnostics
        }, result.Valid
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics));

    private CommandExitCode ValidationLoadFailure(
        ParsedCommand command,
        string message)
    {
        Diagnostic failure = new(
            "finish-core-invalid",
            DiagnosticSeverity.Error,
            message);
        string[] skipped = [
            "source-admission", "proposal-derivation", "transaction-paths", "disposable-write"
        ];
        ImmutableArray<SkyrimNpcFinishCoreValidationPhase> phases =
            [
                new(
                    "request-binding",
                    SkyrimNpcFinishCoreValidationPhaseState.Reached,
                    [failure]),
                .. skipped.Select(name => new SkyrimNpcFinishCoreValidationPhase(
                    name,
                    SkyrimNpcFinishCoreValidationPhaseState.Skipped,
                    [new Diagnostic(
                        "finish-core-validate-phase-skipped",
                        DiagnosticSeverity.Info,
                        $"Phase '{name}' was not checked because prerequisites failed: {failure.Code}")]))
            ];
        return WriteValidationResult(command, new SkyrimNpcFinishCoreValidationResult(
            false,
            phases,
            phases.SelectMany(phase => phase.Diagnostics).ToImmutableArray()));
    }

    private static bool IsValidateAll(ParsedCommand command) =>
        command.Options.TryGetValue("validate-all", out string? value) &&
        (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private CommandExitCode Usage(ParsedCommand command, string message) =>
        Failure(command, "usage", message, CommandExitCode.UsageError);

    private CommandExitCode Failure(
        ParsedCommand command,
        string code,
        string message,
        CommandExitCode exitCode)
    {
        var payload = new { code, message };
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        else
            error.WriteLine($"{code}: {message}");
        return exitCode;
    }
}
