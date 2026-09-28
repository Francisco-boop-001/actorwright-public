using System.Collections.Immutable;
using System.Text.Json;
using Mutagen.Bethesda.Plugins.Exceptions;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed partial class ProtocolV2FinishCoreAdapter(
    WorkspacePath workspaceRoot,
    ISkyrimNpcFinishCoreService service,
    AgentWorkflowBundleTransitionService workflowLifecycle,
    AgentReviewReceiptService reviewReceipts) : IProtocolV2CommandAdapter
{
    public ImmutableArray<string> Commands { get; } = ["npc finish analyze", "npc finish apply"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command, string requestDigest, CancellationToken cancellationToken)
    {
        using var terminal = new ProtocolV2TerminalArtifactLeaseScope();
        bool apply = command.Name == "npc finish apply";
        ImmutableArray<ProtocolArtifact> published = [];
        bool materialized = false;
        SkyrimNpcFinishCoreProposalResult? analysis = null;
        SkyrimNpcFinishCoreApplyResult? application = null;
        SkyrimNpcFinishCoreValidationResult? validation = null;
        try
        {
            SkyrimNpcFinishCoreCommandBindingResult bound = SkyrimNpcFinishCoreCommandBinder.Bind(command, protocolV2: true);
            if (!bound.IsValid) return Failure(ProtocolV2DiagnosticCodes.OptionRequired, bound.ErrorMessage!, DiagnosticClass.Usage);
            SkyrimNpcFinishCoreCommandBinding binding = bound.Binding!;
            ExternalHeadPartInstallContextBindingResult external = ExternalHeadPartInstallContextBinder.Bind(command);
            if (!external.IsValid) return Failure(ProtocolV2DiagnosticCodes.OptionRequired, external.ErrorMessage!, DiagnosticClass.Usage);
            WorkspacePath workflowInput = binding.WorkflowInput!.Value;
            WorkspacePath workflowOutput = binding.WorkflowOutput!.Value;
            workflowLifecycle.AdmitFreshOutput(workflowOutput, workflowInput, binding.Request, binding.Proposal);
            PinFile(workflowInput, terminal);
            PinFile(binding.Request, terminal);
            if (apply) PinFile(binding.Proposal, terminal);
            if (binding.ReviewReceipt is { } receiptPath) PinFile(receiptPath, terminal);
            AgentReviewReceiptDocument? verifiedReceipt = null;
            AgentWorkflowBundleTransition input = binding.ReviewReceipt is { } receipt
                ? workflowLifecycle.LoadForReviewedCommand(workflowInput, binding.WorkflowInputSha256!, command.Name,
                    document => verifiedReceipt = reviewReceipts.LoadForSuccessor(
                        document, receipt, binding.ReviewReceiptSha256!))
                : workflowLifecycle.LoadForFinishCommand(workflowInput, binding.WorkflowInputSha256!, command.Name);

            var reader = new SkyrimNpcFinishCoreCommandDocumentReader(workspaceRoot);
            byte[] requestBytes = await reader.ReadBoundFileAsync(binding.Request, binding.RequestSha256,
                bytes => SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(bytes, workspaceRoot), cancellationToken);
            SkyrimNpcFinishCoreRequest request = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(requestBytes, workspaceRoot);
            Sha256Hash requestHash = SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, workspaceRoot);
            RequireIdentity(input.Document.Bundle.Npc, request);
            WorkflowArtifactBinding requestArtifact = BindFile(binding.Request, WorkflowArtifactKinds.NpcFinishCoreRequest,
                request.Schema, command.Name, requestDigest, [requestHash.Value.ToUpperInvariant()], terminal);
            SkyrimNpcFinishCoreProposal? proposal = null;
            Sha256Hash proposalHash = default;
            WorkflowArtifactBinding? proposalArtifact = null;
            if (apply)
            {
                RequireBinding(input, requestArtifact);
                byte[] proposalBytes = await reader.ReadProposalBoundFileAsync(binding.Proposal, binding.ProposalSha256!.Value, cancellationToken);
                proposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(proposalBytes, workspaceRoot);
                proposalHash = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(proposalBytes);
                proposalArtifact = BindFile(binding.Proposal, WorkflowArtifactKinds.NpcFinishCoreProposal,
                    proposal.Schema, command.Name, requestDigest, [requestHash.Value.ToUpperInvariant()], terminal, proposalHash.Value.ToUpperInvariant());
                RequireBinding(input, proposalArtifact);
                if (proposal.RequestSha256 != requestHash || proposal.ProposalSha256 != proposalHash)
                    throw new InvalidDataException("finish-core-proposal-binding: The proposal does not bind the canonical request and semantic proposal hash.");
            }
            else
            {
                WorkflowArtifactBinding package = input.Document.Bundle.Artifacts.Single(item => item.Kind == WorkflowArtifactKinds.NpcPackageManifest);
                if (request.Source.PackageManifest is not { } sourceManifest ||
                    !SamePath(package.Path, sourceManifest) || request.Source.PackageManifestSha256 != new Sha256Hash(package.Sha256))
                    throw new InvalidDataException("workflow-artifact-binding-mismatch: The request source package does not match the workflow package.");
            }

            if (binding.ValidateAll)
            {
                validation = apply
                    ? await service.ValidateApplyAsync(request, requestHash, proposal!, proposalHash, cancellationToken)
                    : await service.ValidateAnalyzeAsync(request, requestHash, binding.Proposal, cancellationToken);
                return Complete(validation.Diagnostics, [], null);
            }
            ExternalHeadPartInstallVerificationContext? context = null;
            if (request.Authorities.ExternalHeadParts is not null)
            {
                if (apply && !external.IsSpecified)
                    throw new InvalidDataException("external-hdpt-install-context-absent: External Finish apply requires a complete ephemeral install context.");
                if (external.IsSpecified)
                {
                    if (!ExternalHeadPartInstallContextBinder.TryReadTargetRace(request, out FormReference race, out string error))
                        throw new InvalidDataException(error);
                    if (service is not ISkyrimNpcFinishCoreInstallContextService)
                        throw new InvalidDataException("External Finish requires a typed install-context service.");
                    context = external.CreateContext(race);
                }
            }
            ImmutableArray<WorkflowArtifactBinding> successors;
            ImmutableArray<Diagnostic> diagnostics;
            if (!apply)
            {
                analysis = context is null
                    ? await service.AnalyzeAsync(request, requestHash, binding.Proposal, cancellationToken)
                    : await ((ISkyrimNpcFinishCoreInstallContextService)service).AnalyzeWithInstallContextAsync(
                        request, requestHash, binding.Proposal, context, cancellationToken);
                diagnostics = analysis.Diagnostics;
                if (!analysis.Proposed || analysis.Proposal is null || analysis.ProposalPath is null || analysis.ProposalSha256 is null)
                    return Complete(diagnostics, [], null);
                materialized = true;
                proposalArtifact = BindFile(analysis.ProposalPath.Value, WorkflowArtifactKinds.NpcFinishCoreProposal,
                    analysis.Proposal.Schema, command.Name, requestDigest, [requestArtifact.Sha256], terminal,
                    analysis.ProposalSha256.Value.Value.ToUpperInvariant());
                published = [Project(proposalArtifact)];
                successors = [requestArtifact, proposalArtifact,
                    .. input.Document.Bundle.Artifacts.Where(item => item.Kind == WorkflowArtifactKinds.NpcPreviewManifest ||
                        (binding.ReviewReceipt is null && item.Kind == WorkflowArtifactKinds.NpcPackageManifest))];
            }
            else
            {
                application = context is null
                    ? await service.ApplyAsync(request, requestHash, proposal!, proposalHash, cancellationToken)
                    : await ((ISkyrimNpcFinishCoreInstallContextService)service).ApplyWithInstallContextAsync(
                        request, requestHash, proposal!, proposalHash, context, cancellationToken);
                diagnostics = application.Diagnostics;
                if (!application.Applied || application.Manifest is null || application.OutputRoot is null || application.Archive is null)
                    return Complete(diagnostics, [], null);
                materialized = true;
                var manifestPath = new WorkspacePath(Path.Combine(application.OutputRoot.Value.Value,
                    "NPCManager", "Evidence", "finish-core-manifest.json"));
                WorkflowArtifactBinding manifest = BindFile(manifestPath, WorkflowArtifactKinds.NpcFinishCoreManifest,
                    application.Manifest.Schema, command.Name, requestDigest, [requestArtifact.Sha256, proposalArtifact!.Sha256], terminal);
                WorkflowArtifactBinding archive = BindFile(application.Archive.Value, WorkflowArtifactKinds.PackageArchive,
                    "application/zip", command.Name, requestDigest, [manifest.Sha256], terminal);
                published = [Project(manifest), Project(archive)];
                successors = [manifest, .. input.Document.Bundle.Artifacts.Select(item =>
                    item.Kind == WorkflowArtifactKinds.ReviewReceipt
                        ? item with { RequestDigest = requestDigest }
                        : item)];
            }
            AgentWorkflowBundleTransition output = terminal.Own(!apply || verifiedReceipt is null
                ? workflowLifecycle.AdvanceRetained(input,
                    input.Document.Bundle.Npc, requestDigest, successors, workflowOutput)
                : workflowLifecycle.AdvanceWithReviewedReceiptRetained(input,
                    input.Document.Bundle.Npc, requestDigest, verifiedReceipt, successors, workflowOutput)).Transition;
            published = published.Add(ProtocolV2WorkflowBundleProjection.Artifact(output.Document, command.Name, requestDigest));
            return Complete(diagnostics, published, output);
        }
        catch (AgentWorkflowCodecException exception)
        {
            ProtocolFailureProjection projected = ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(exception.Code);
            return Failure(projected.Code, exception.Code + ": " + exception.Message, projected.Class);
        }
        catch (AgentReviewReceiptException exception)
        {
            return Failure(ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
                exception.Code + ": " + exception.Message, DiagnosticClass.Validation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or RecordException)
        {
            return Failure(ProtocolV2DiagnosticCodes.FinishCoreValidationFailed, exception.Message, DiagnosticClass.Validation);
        }

        ProtocolCommandResult Complete(ImmutableArray<Diagnostic> diagnostics, ImmutableArray<ProtocolArtifact> artifacts,
            AgentWorkflowBundleTransition? output) => new(
            Effects(materialized), diagnostics.Select(ProjectDiagnostic).ToImmutableArray(), artifacts,
                Authority(!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)),
                output is null ? [] : ProtocolV2WorkflowBundleProjection.NextActions(output),
                apply ? AgentProtocolSchemaIds.FinishApplyResult : AgentProtocolSchemaIds.FinishAnalyzeResult,
                Result(apply, analysis, application, validation)) { TerminalArtifactLease = terminal.Transfer() };

        ProtocolCommandResult Failure(string code, string message, DiagnosticClass diagnosticClass) => new(
            Effects(materialized), [new ProtocolDiagnostic(code, DiagnosticSeverity.Error, message, diagnosticClass,
                new DiagnosticRecovery(RecoveryAction.CorrectInput, null, null, "Correct the exact Finish inputs and choose fresh outputs.", false))],
            published, Authority(false), [],
            apply ? AgentProtocolSchemaIds.FinishApplyResult : AgentProtocolSchemaIds.FinishAnalyzeResult,
            Result(apply, analysis, application, validation)) { TerminalArtifactLease = terminal.Transfer() };
    }

    private static void RequireIdentity(WorkflowNpcIdentity npc, SkyrimNpcFinishCoreRequest request)
    {
        if (!string.Equals(npc.EditorId, request.Actor.EditorId?.Value, StringComparison.Ordinal) ||
            !string.Equals(npc.Plugin, request.Source.Plugin?.Value, StringComparison.OrdinalIgnoreCase) ||
            npc.LocalFormId is null || !FormId.TryParse(npc.LocalFormId, out FormId localFormId) || request.Actor.FormId != localFormId)
            throw new InvalidDataException("workflow-artifact-binding-mismatch: Finish request NPC identity differs from the workflow.");
    }

    private static void RequireBinding(AgentWorkflowBundleTransition input, WorkflowArtifactBinding observed)
    {
        WorkflowArtifactBinding? expected = input.Document.Bundle.Artifacts.SingleOrDefault(item => item.Kind == observed.Kind);
        if (expected is null || !SamePath(expected.Path, observed.Path) || expected.Size != observed.Size ||
            expected.Sha256 != observed.Sha256 || expected.SemanticSha256 != observed.SemanticSha256)
            throw new InvalidDataException("workflow-artifact-binding-mismatch: The Finish options do not match their exact workflow artifacts.");
    }

    private static bool SamePath(WorkspacePath left, WorkspacePath right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
}
