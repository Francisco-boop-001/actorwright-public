using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2ReviewReceiptAdapter : IProtocolV2CommandAdapter
{
    private const string FullReplayRecovery =
        "Replay the unchanged review with both fresh --receipt-output and --workflow-output paths.";
    private static readonly ImmutableHashSet<string> AllowedOptions =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "workflow-bundle",
            "workflow-bundle-sha256",
            "proposal",
            "proposal-sha256",
            "preview-manifest",
            "preview-manifest-sha256",
            "outcome",
            "operator-attestation",
            "reviewer-note",
            "receipt-output",
            "workflow-output");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private readonly WorkspacePath workspaceRoot;
    private readonly AgentReviewReceiptService receiptService;
    private readonly AgentWorkflowBundleTransitionService workflowLifecycle;
    private readonly IProtocolV2ReviewPredecessorAdmission admission;

    public ProtocolV2ReviewReceiptAdapter(
        WorkspacePath workspaceRoot,
        AgentReviewReceiptService receiptService,
        AgentWorkflowBundleTransitionService workflowLifecycle) : this(
            workspaceRoot,
            receiptService,
            workflowLifecycle,
            ProtocolV2ReviewPredecessorAdmission.CreateDefault(
                workspaceRoot,
                workflowLifecycle))
    {
    }

    internal ProtocolV2ReviewReceiptAdapter(
        WorkspacePath workspaceRoot,
        AgentReviewReceiptService receiptService,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        IProtocolV2ReviewPredecessorAdmission admission)
    {
        this.workspaceRoot = workspaceRoot;
        this.receiptService = receiptService ??
            throw new ArgumentNullException(nameof(receiptService));
        this.workflowLifecycle = workflowLifecycle ??
            throw new ArgumentNullException(nameof(workflowLifecycle));
        this.admission = admission ??
            throw new ArgumentNullException(nameof(admission));
    }

    public ImmutableArray<string> Commands { get; } = ["gui"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.Name, "gui", StringComparison.Ordinal))
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.ProtocolAdapterMissing,
                "The review adapter handles only the exact 'gui' command.");
        ProtocolCommandResult? syntax = BindSyntax(
            command,
            out ReviewOutcome outcome,
            out string? reviewerNote);
        if (syntax is not null)
            return syntax;

        var binder = new ProtocolV2WorkflowPathBinder(
            workspaceRoot,
            new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot));
        if (!binder.TryExistingFilePair(
                command,
                "workflow-bundle",
                "workflow-bundle-sha256",
                out ProtocolV2PhysicalFileBinding? workflow,
                out ImmutableArray<ProtocolDiagnostic> diagnostics) ||
            !binder.TryExistingFilePair(
                command,
                "proposal",
                "proposal-sha256",
                out ProtocolV2PhysicalFileBinding? proposal,
                out diagnostics) ||
            !binder.TryExistingFilePair(
                command,
                "preview-manifest",
                "preview-manifest-sha256",
                out ProtocolV2PhysicalFileBinding? preview,
                out diagnostics) ||
            !binder.TryFreshFile(
                command,
                "receipt-output",
                out WorkspacePath receiptOutput,
                out diagnostics) ||
            !binder.TryFreshFile(
                command,
                "workflow-output",
                out WorkspacePath workflowOutput,
                out diagnostics) ||
            !binder.TryNoOverlap(
                [workflow!.Path, proposal!.Path, preview!.Path],
                [receiptOutput, workflowOutput],
                out diagnostics))
            return Refused(
                ProjectReviewBindingDiagnostics(diagnostics),
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                BindingAuthority());

        ProtocolV2ReviewPredecessorLease predecessor;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            predecessor = await admission.AdmitAsync(
                workflow!,
                proposal!,
                preview!,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    DiagnosticClass.Cancellation,
                    "Review admission was cancelled before receipt publication.",
                    RecoveryAction.RetryUnchanged,
                    "workflow-bundle",
                    "workflow-bundle",
                    "Retry the exact unchanged review inputs.",
                    retryUnchangedSafe: true)],
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                BindingAuthority());
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
                    DiagnosticClass.Validation,
                    Sanitize(exception),
                    RecoveryAction.CorrectInput,
                    "workflow-bundle",
                    "workflow-bundle",
                    "Correct the exact workflow, proposal, and preview bindings and retry.")],
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                BindingAuthority());
        }

        using var terminal = new ProtocolV2TerminalArtifactLeaseScope();
        terminal.Own(predecessor);
        if (!binder.TryNoOverlap(
                predecessor.ProtectedRoots,
                [receiptOutput, workflowOutput],
                out diagnostics))
            return Refused(
                ProjectReviewBindingDiagnostics(diagnostics),
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                BindingAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        AgentReviewReceiptDocumentLease receiptLease;
        try
        {
            predecessor.Revalidate();
            receiptLease = receiptService.CreateRetainedForCommand(
                predecessor.Workflow.Document,
                requestDigest,
                predecessor.Proposal.Sha256,
                predecessor.DisplayedArtifacts,
                outcome,
                reviewerNote,
                receiptOutput,
                predecessor.Revalidate,
                cancellationToken);
            terminal.Own(receiptLease);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    DiagnosticClass.Cancellation,
                    "Review receipt publication was cancelled before commit.",
                    RecoveryAction.ChooseFreshOutput,
                    "receipt-output",
                    WorkflowArtifactKinds.ReviewReceipt,
                    "Retry with a fresh receipt output.")],
                ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                AdmittedAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }
        catch (AgentReviewReceiptPublishedException exception)
        {
            terminal.Own(exception.PublishedReceipt);
            AgentReviewReceiptDocument published =
                exception.PublishedReceipt.Document;
            ImmutableArray<ProtocolEffect> effects = outcome ==
                    ReviewOutcome.Accepted
                ?
                [
                    Effect(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.KLocalOutput),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Blocked,
                        ApplicationEffectScope.KLocalOutput)
                ]
                :
                [
                    Effect(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.KLocalOutput)
                ];
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
                    DiagnosticClass.Operation,
                    $"{exception.Code}: {exception.Message}",
                    RecoveryAction.RepairEnvironment,
                    null,
                    WorkflowArtifactKinds.ReviewReceipt,
                    $"Repair the receipt publication environment. {FullReplayRecovery}")],
                effects,
                PublishedUnverifiedAuthority(),
                [ReceiptArtifact(published, "publishedUnverified")],
                outcome,
                published,
                "review-receipt-persisted-unverified") with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }
        catch (AgentReviewReceiptException exception) when (
            string.Equals(
                exception.Code,
                "review-pre-publication-validation-failed",
                StringComparison.Ordinal))
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
                    DiagnosticClass.Validation,
                    Sanitize(exception),
                    RecoveryAction.CorrectInput,
                    "workflow-bundle",
                    "workflow-bundle",
                    "Correct the changed predecessor evidence and retry with fresh outputs.")],
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                AdmittedAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }
        catch (AgentReviewReceiptException exception)
        {
            (string code, DiagnosticClass diagnosticClass,
                RecoveryAction action, string constraint) =
                ClassifyReceiptFailure(exception.Code);
            return Refused(
                [Diagnostic(
                    code,
                    diagnosticClass,
                    Sanitize(exception),
                    action,
                    "receipt-output",
                    WorkflowArtifactKinds.ReviewReceipt,
                    constraint)],
                diagnosticClass == DiagnosticClass.Operation
                    ? ProtocolV2CommitEffects.ReadCompletedWriteFailed()
                    : ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                AdmittedAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
                    DiagnosticClass.Validation,
                    Sanitize(exception),
                    RecoveryAction.CorrectInput,
                    "workflow-bundle",
                    "workflow-bundle",
                    "Correct the changed predecessor evidence and retry with fresh outputs.")],
                ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                AdmittedAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }

        AgentReviewReceiptDocument receipt = receiptLease.Document;
        if (outcome != ReviewOutcome.Accepted)
            return Success(
                outcome,
                receipt,
                workflow: null,
                effects:
                [
                    Effect(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.KLocalOutput)
                ],
                authority: AdmittedAuthority()) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };

        AgentWorkflowBundleTransitionLease workflowLease;
        try
        {
            predecessor.Revalidate();
            workflowLease = workflowLifecycle.AdvanceReviewedRetained(
                predecessor.Workflow,
                predecessor.Workflow.Document.Bundle.Npc,
                requestDigest,
                receipt,
                [
                    .. predecessor.Workflow.Document.Bundle.Artifacts,
                    receipt.Artifact
                ],
                workflowOutput);
            terminal.Own(workflowLease);
        }
        catch (AgentWorkflowCodecException exception)
        {
            ProtocolFailureProjection projection =
                ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                    exception.Code);
            return Refused(
                [Diagnostic(
                    projection.Code,
                    projection.Class,
                    $"{exception.Code}: {exception.Message}",
                    projection.Class == DiagnosticClass.Operation
                        ? RecoveryAction.RepairEnvironment
                        : RecoveryAction.ChooseFreshOutput,
                    null,
                    "workflow-bundle",
                    projection.Class == DiagnosticClass.Operation
                        ? $"Repair the workflow publication environment. {FullReplayRecovery}"
                        : FullReplayRecovery)],
                ProtocolV2CommitEffects.PublishedArtifactWorkflowFailed(),
                AdmittedAuthority(),
                [ReceiptArtifact(receipt)],
                outcome,
                receipt) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        {
            return Refused(
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
                    DiagnosticClass.Validation,
                    Sanitize(exception),
                    RecoveryAction.CorrectInput,
                    "workflow-bundle",
                    "workflow-bundle",
                    $"Correct the changed predecessor evidence. {FullReplayRecovery}")],
                [
                    Effect(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.KLocalOutput),
                    Effect(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Refused,
                        ApplicationEffectScope.KLocalOutput)
                ],
                AdmittedAuthority(),
                [ReceiptArtifact(receipt)],
                outcome,
                receipt) with
            {
                TerminalArtifactLease = terminal.Transfer()
            };
        }

        return Success(
            outcome,
            receipt,
            workflowLease.Transition,
            [
                Effect(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.Workspace),
                Effect(
                    AgentEffectKind.WriteNewArtifact,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.KLocalOutput),
                Effect(
                    AgentEffectKind.WriteNewArtifact,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.KLocalOutput)
            ],
            AdmittedAuthority()) with
        {
            TerminalArtifactLease = terminal.Transfer()
        };
    }

    private static ProtocolCommandResult? BindSyntax(
        ParsedCommand command,
        out ReviewOutcome outcome,
        out string? reviewerNote)
    {
        outcome = default;
        reviewerNote = command.Options.TryGetValue(
            "reviewer-note",
            out string? note) ? note : null;
        if (!command.Positionals.IsEmpty)
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.PositionalUnexpected,
                "The protocol-v2 gui receipt mode accepts no positional values.");
        string? unknown = command.Options.Keys.FirstOrDefault(
            key => !AllowedOptions.Contains(key));
        if (unknown is not null)
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionUnknown,
                $"Unknown protocol-v2 gui receipt option '--{unknown}'.",
                unknown);
        if (!command.DuplicateOptions.IsDefaultOrEmpty)
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionDuplicate,
                "Protocol-v2 gui receipt options must not be repeated.",
                command.DuplicateOptions[0]);
        if (!command.Options.TryGetValue("outcome", out string? rawOutcome))
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionRequired,
                "Option '--outcome' is required.",
                "outcome");
        outcome = rawOutcome switch
        {
            "accepted" => ReviewOutcome.Accepted,
            "rejected" => ReviewOutcome.Rejected,
            "revision-requested" => ReviewOutcome.RevisionRequested,
            _ => (ReviewOutcome)(-1)
        };
        if (!Enum.IsDefined(outcome))
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionEnumValue,
                "Option '--outcome' must be exactly accepted, rejected, or revision-requested.",
                "outcome");
        bool hasAttestation = command.Options.TryGetValue(
            "operator-attestation",
            out string? attestation);
        bool valueless = !command.ValuelessOptions.IsDefault &&
            command.ValuelessOptions.Contains(
                "operator-attestation",
                StringComparer.OrdinalIgnoreCase);
        if (outcome == ReviewOutcome.Accepted &&
            (!hasAttestation || valueless ||
             !string.Equals(attestation, "true", StringComparison.Ordinal)))
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionFlagValue,
                "Accepted review requires explicitly valued '--operator-attestation true'.",
                "operator-attestation");
        if (outcome != ReviewOutcome.Accepted && hasAttestation)
            return RefusedUsage(
                ProtocolV2DiagnosticCodes.OptionConflict,
                "Operator attestation is valid only for an accepted review outcome.",
                "operator-attestation");
        return null;
    }

    private static ProtocolCommandResult Success(
        ReviewOutcome outcome,
        AgentReviewReceiptDocument receipt,
        AgentWorkflowBundleTransition? workflow,
        ImmutableArray<ProtocolEffect> effects,
        ImmutableArray<ProtocolAuthority> authority)
    {
        ImmutableArray<ProtocolArtifact> artifacts = workflow is null
            ? [ReceiptArtifact(receipt)]
            :
            [
                ReceiptArtifact(receipt),
                ProtocolV2WorkflowBundleProjection.Artifact(
                    workflow.Document,
                    "gui",
                    receipt.Artifact.RequestDigest)
            ];
        return new ProtocolCommandResult(
            effects,
            [],
            artifacts,
            authority,
            workflow is null
                ? []
                : ProtocolV2WorkflowBundleProjection.NextActions(workflow),
            AgentProtocolSchemaIds.ReviewReceiptResult,
            Result(true, outcome, receipt, workflow));
    }

    private static ProtocolCommandResult RefusedUsage(
        string code,
        string message,
        string? option = null) => Refused(
        [Diagnostic(
            code,
            DiagnosticClass.Usage,
            message,
            RecoveryAction.CorrectInput,
            option,
            null,
            "Correct the exact protocol-v2 gui receipt options and retry.")],
        ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
        BindingAuthority());

    private static ProtocolCommandResult Refused(
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        ImmutableArray<ProtocolEffect> effects,
        ImmutableArray<ProtocolAuthority> authority,
        ImmutableArray<ProtocolArtifact> artifacts = default,
        ReviewOutcome? durableOutcome = null,
        AgentReviewReceiptDocument? durableReceipt = null,
        string? durableStatus = null) => new(
        effects,
        diagnostics,
        artifacts.IsDefault ? [] : artifacts,
        authority,
        [],
        AgentProtocolSchemaIds.ReviewReceiptResult,
        Result(
            durableReceipt is not null,
            durableOutcome,
            durableReceipt,
            workflow: null,
            durableStatus));

    private static ImmutableArray<ProtocolDiagnostic>
        ProjectReviewBindingDiagnostics(
            ImmutableArray<ProtocolDiagnostic> diagnostics) => diagnostics
        .Select(diagnostic =>
        {
            bool receiptPathFailure = string.Equals(
                    diagnostic.Recovery?.Option,
                    "receipt-output",
                    StringComparison.Ordinal) ||
                (string.Equals(
                     diagnostic.Code,
                     ProtocolV2DiagnosticCodes.PresetInspectionPathRefused,
                     StringComparison.Ordinal) &&
                 diagnostic.Message.Contains(
                     "--receipt-output",
                     StringComparison.Ordinal));
            if (receiptPathFailure)
                return diagnostic with
                {
                    Code = ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
                    Class = DiagnosticClass.Security
                };
            return string.Equals(
                    diagnostic.Recovery?.Option,
                    "workflow-output",
                    StringComparison.Ordinal)
                ? diagnostic with
                {
                    Code = ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
                    Class = DiagnosticClass.Security
                }
                : diagnostic;
        })
        .ToImmutableArray();

    private static JsonElement Result(
        bool created,
        ReviewOutcome? outcome,
        AgentReviewReceiptDocument? receipt,
        AgentWorkflowBundleTransition? workflow,
        string? status = null) =>
        JsonSerializer.SerializeToElement(
            new ReviewReceiptResult(
                created,
                status ?? (created ? "review-receipt-persisted" : "refused"),
                outcome,
                receipt?.Path.Value,
                receipt?.Size,
                receipt?.Sha256,
                workflow?.Document.Path.Value,
                workflow?.Document.Size,
                workflow?.Document.Sha256,
                HumanVisualAuthority: false,
                RuntimeAuthority: false,
                PromotionAuthority: false),
            JsonOptions);

    private static ProtocolArtifact ReceiptArtifact(
        AgentReviewReceiptDocument receipt,
        string state = "independentlyVerified") => new(
        receipt.Artifact.Kind,
        receipt.Artifact.SchemaOrMediaType,
        receipt.Path.Value,
        receipt.Size,
        receipt.Sha256,
        receipt.Artifact.ProducerCommand,
        receipt.Artifact.RequestDigest,
        receipt.Artifact.InputArtifactHashes,
        state);

    private static ImmutableArray<ProtocolAuthority> AdmittedAuthority() =>
    [
        Authority(AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact workflow, proposal, and preview were physically admitted."),
        Authority(AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The review predecessor retains its exact source identity evidence."),
        Authority(AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The reviewed predecessor artifacts remain materialized."),
        Authority(AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "The reviewed predecessor and receipt artifacts were independently reopened."),
        Authority(AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.Established,
            "The exact off-engine preview evidence was admitted."),
        Authority(AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.Required,
            "The recorded attestation does not prove human visual acceptance."),
        Authority(AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Skyrim runtime testing remains user-operated."),
        Authority(AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.Required,
            "A review receipt never grants promotion approval.")
    ];

    private static ImmutableArray<ProtocolAuthority>
        PublishedUnverifiedAuthority() => AdmittedAuthority()
        .Select(item => item.Kind ==
            AgentAuthorityKind.IndependentStaticVerification
            ? item with
            {
                State = AgentAuthorityState.Required,
                Reason = "The promoted review receipt still requires independent physical readback verification."
            }
            : item)
        .ToImmutableArray();

    private static ImmutableArray<ProtocolAuthority> BindingAuthority() =>
        AdmittedAuthority().Select(item => item.Kind switch
        {
            AgentAuthorityKind.HumanVisualAcceptance or
            AgentAuthorityKind.GameRuntimeVerification or
            AgentAuthorityKind.PromotionApproval => item,
            AgentAuthorityKind.InputAdmission => item with
            {
                State = AgentAuthorityState.Blocked,
                Reason = "Review input admission was blocked."
            },
            _ => item with
            {
                State = AgentAuthorityState.Required,
                Reason = "This authority remains required after review refusal."
            }
        }).ToImmutableArray();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ProtocolEffect Effect(
        AgentEffectKind kind,
        ApplicationEffectStatus status,
        ApplicationEffectScope scope) => ProtocolEffect.Create(
        kind,
        status,
        scope);

    private static ProtocolDiagnostic Diagnostic(
        string code,
        DiagnosticClass diagnosticClass,
        string message,
        RecoveryAction action,
        string? option,
        string? artifactKind,
        string constraint,
        bool retryUnchangedSafe = false) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        diagnosticClass,
        new DiagnosticRecovery(
            action,
            option,
            artifactKind,
            constraint,
            retryUnchangedSafe));

    private static (string Code, DiagnosticClass Class,
        RecoveryAction Action, string Constraint) ClassifyReceiptFailure(
        string sourceCode)
    {
        if (string.Equals(
                sourceCode,
                "review-reparse-refused",
                StringComparison.Ordinal))
            return (
                ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
                DiagnosticClass.Security,
                RecoveryAction.ChooseFreshOutput,
                "Choose a fresh --receipt-output with a non-reparse parent and replay the unchanged review request.");
        if (string.Equals(
                sourceCode,
                "review-write-failed",
                StringComparison.Ordinal))
            return (
                ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
                DiagnosticClass.Operation,
                RecoveryAction.RepairEnvironment,
                "Repair the receipt publication environment and replay the unchanged review request with a fresh --receipt-output.");
        if (sourceCode.Contains("path", StringComparison.Ordinal) ||
            sourceCode.Contains("parent", StringComparison.Ordinal) ||
            sourceCode.Contains("output-exists", StringComparison.Ordinal))
            return (
                ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
                DiagnosticClass.Security,
                RecoveryAction.ChooseFreshOutput,
                "Preserve any committed receipt and retry with a fresh receipt output.");
        if (sourceCode.Contains("write", StringComparison.Ordinal) ||
            sourceCode.Contains("readback", StringComparison.Ordinal))
            return (
                ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
                DiagnosticClass.Operation,
                RecoveryAction.RepairEnvironment,
                "Repair the receipt publication environment and replay the unchanged review request with a fresh --receipt-output.");
        return (
            ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed,
            DiagnosticClass.Validation,
            RecoveryAction.CorrectInput,
            "Correct the exact admitted review inputs and retry with a fresh receipt output.");
    }

    private static bool IsAdmissionFailure(Exception exception) =>
        exception is AgentWorkflowCodecException or ArgumentException or
            InvalidDataException or IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException;

    private static string Sanitize(Exception exception) => exception switch
    {
        AgentWorkflowCodecException workflow =>
            $"{workflow.Code}: {workflow.Message}",
        _ => exception.Message
    };
}
