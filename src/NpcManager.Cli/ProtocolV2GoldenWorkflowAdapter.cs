using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

/// <summary>
/// First production golden-workflow adapter. Its command set is intentionally
/// closed to reviewed protocol-v2 workspace preflight.
/// </summary>
public sealed class ProtocolV2GoldenWorkflowAdapter(
    WorkspacePath workspaceRoot,
    IReviewedGameIntakeService reviewedIntakeService,
    ReviewedGameIntakeArtifactStore artifactStore,
    AgentWorkflowBundleTransitionService? workflowLifecycle = null) :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ImmutableArray<string> Commands { get; } = ["workspace preflight"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        ReviewedWorkspacePreflightBinding binding;
        try
        {
            binding = ReviewedWorkspacePreflightBinder.Bind(
                command,
                workspaceRoot,
                requireExplicitWorkspace: true);
        }
        catch (ArgumentException exception)
        {
            return Refused(
                [ValidationDiagnostic(exception.Message)],
                requestDigest,
                null,
                BindingFailureAuthority());
        }
        if (!binding.IsValid)
            return Refused(
                [UsageDiagnostic(binding.ErrorMessage!)],
                requestDigest,
                null,
                BindingFailureAuthority());
        if (!command.Options.TryGetValue(
                "intake-output",
                out string? intakeOutputValue))
            return Refused(
                [UsageDiagnostic(
                    "Protocol-v2 reviewed workspace preflight requires --intake-output.")],
                requestDigest,
                null,
                BindingFailureAuthority());
        if (!command.Options.TryGetValue(
                "npc-editor-id",
                out string? npcEditorId) ||
            string.IsNullOrWhiteSpace(npcEditorId) ||
            npcEditorId.Contains('\r') ||
            npcEditorId.Contains('\n'))
            return Refused(
                [UsageDiagnostic(
                    "Protocol-v2 reviewed workspace preflight requires --npc-editor-id.")],
                requestDigest,
                null,
                BindingFailureAuthority());
        if (!command.Options.TryGetValue(
                "workflow-output",
                out string? workflowOutputValue))
            return Refused(
                [UsageDiagnostic(
                    "Protocol-v2 reviewed workspace preflight requires --workflow-output.")],
                requestDigest,
                null,
                BindingFailureAuthority());
        ArgumentNullException.ThrowIfNull(workflowLifecycle);
        var intakeOutput = new WorkspacePath(intakeOutputValue);
        var workflowOutput = new WorkspacePath(workflowOutputValue);
        try
        {
            workflowLifecycle.AdmitFreshOutput(
                workflowOutput,
                intakeOutput);
        }
        catch (AgentWorkflowCodecException exception)
        {
            return Refused(
                [WorkflowDiagnostic(exception)],
                requestDigest,
                null,
                BindingFailureAuthority());
        }

        ReviewedGameIntakeRequest request = binding.Request!;
        ReviewedGameIntakeResult review;
        try
        {
            review = await reviewedIntakeService.ReviewAsync(
                request,
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Refused(
                [ValidationDiagnostic(exception.Message)],
                requestDigest,
                null,
                AdmissionFailureAuthority());
        }

        ImmutableArray<ProtocolDiagnostic> reviewDiagnostics = review.Diagnostics
            .Select(ToProtocolDiagnostic)
            .ToImmutableArray();
        if (!review.IsAccepted || review.Intake is null)
            return Refused(reviewDiagnostics, requestDigest,
                ReviewedGameIntakeResponse.From(request, review),
                AdmissionFailureAuthority());

        ReviewedGameIntakeDocumentAuthority authority;
        try
        {
            authority = await artifactStore.WriteNewAsync(
                review.Intake,
                intakeOutput,
                cancellationToken);
        }
        catch (ReviewedGameIntakeArtifactException exception)
        {
            return Refused(
                reviewDiagnostics.Add(ArtifactDiagnostic(exception)),
                requestDigest,
                ReviewedGameIntakeResponse.From(request, review),
                PersistenceFailureAuthority());
        }

        WorkflowArtifactBinding intakeArtifact = IntakeArtifact(
            authority,
            requestDigest);
        AgentWorkflowBundleTransition workflow;
        try
        {
            workflow = workflowLifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    npcEditorId,
                    null,
                    null,
                    null),
                requestDigest,
                [intakeArtifact],
                workflowOutput);
        }
        catch (AgentWorkflowCodecException exception)
        {
            return new ProtocolCommandResult(
                [
                    ProtocolEffect.Create(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    ProtocolEffect.Create(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Refused,
                        ApplicationEffectScope.KLocalOutput)
                ],
                reviewDiagnostics.Add(WorkflowDiagnostic(exception)),
                [ToProtocolArtifact(intakeArtifact)],
                PersistenceFailureAuthority(),
                [],
                AgentProtocolSchemaIds.WorkspacePreflightResult,
                JsonSerializer.SerializeToElement(
                    ReviewedGameIntakeResponse.From(request, review),
                    JsonOptions));
        }

        return new ProtocolCommandResult(
            [
                ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.Workspace),
                ProtocolEffect.Create(
                    AgentEffectKind.WriteNewArtifact,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.KLocalOutput)
            ],
            reviewDiagnostics,
            [
                ToProtocolArtifact(intakeArtifact),
                ProtocolV2WorkflowBundleProjection.Artifact(
                    workflow.Document,
                    "workspace preflight",
                    requestDigest)
            ],
            Authority(),
            ProtocolV2WorkflowBundleProjection.NextActions(workflow),
            AgentProtocolSchemaIds.WorkspacePreflightResult,
            JsonSerializer.SerializeToElement(
                ReviewedGameIntakeResponse.From(request, review),
                JsonOptions));
    }

    private static ProtocolCommandResult Refused(
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        string requestDigest,
        ReviewedGameIntakeResponse? response,
        ImmutableArray<ProtocolAuthority> authority) =>
        new(
            [ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                ApplicationEffectStatus.Refused,
                ApplicationEffectScope.ReviewedWorkspace)],
            diagnostics,
            [],
            authority,
            [],
            AgentProtocolSchemaIds.WorkspacePreflightResult,
            RefusedResult(response));

    private static JsonElement RefusedResult(
        ReviewedGameIntakeResponse? response) =>
        response is null
            ? JsonSerializer.SerializeToElement(
                new
                {
                    SchemaVersion = "2",
                    IsAccepted = false
                },
                JsonOptions)
            : JsonSerializer.SerializeToElement(response, JsonOptions);

    private static ImmutableArray<string> InputBindings(
        ReviewedGameIntake intake)
    {
        IEnumerable<string> hashes =
            new[]
                {
                    intake.LoadOrderHash.Value,
                    intake.AssetIndexFingerprint.Value
                }
                .Concat(intake.Plugins
                    .Where(item => item.SourceHash is not null)
                    .Select(item => item.SourceHash!.Value.Value))
                .Concat(intake.BodySidecars.SelectMany(item =>
                    new[] { item.SourceHash.Value, item.CanonicalHash.Value }))
                .Concat(intake.GeneratedPlugins
                    .Where(item => item.Sha256 is not null)
                    .Select(item => item.Sha256!.Value.Value))
                .Concat(intake.GeneratedSidecars.Select(item => item.Sha256.Value));
        return hashes.Distinct(StringComparer.Ordinal)
            .Select(value => value.ToUpperInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static WorkflowArtifactBinding IntakeArtifact(
        ReviewedGameIntakeDocumentAuthority authority,
        string requestDigest) => new(
        WorkflowArtifactKinds.ReviewedWorkspaceIntake,
        "npcmanager-reviewed-game-intake/2",
        authority.Document.Path,
        authority.Document.ByteLength,
        authority.Document.Sha256.Value.ToUpperInvariant(),
        "workspace preflight",
        requestDigest,
        InputBindings(authority.Value));

    private static ProtocolArtifact ToProtocolArtifact(
        WorkflowArtifactBinding artifact) => new(
        artifact.Kind,
        artifact.SchemaOrMediaType,
        artifact.Path.Value,
        artifact.Size,
        artifact.Sha256,
        artifact.ProducerCommand,
        artifact.RequestDigest,
        artifact.InputArtifactHashes,
        "independentlyVerified");

    private static ImmutableArray<ProtocolAuthority> Authority() =>
    [
        new(
            AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "Exact K-local copied-workspace inputs were admitted."),
        new(
            AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The selected plugin closure and provider inventory are hash-bound."),
        new(
            AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "Schema-2 reviewed intake was canonically materialized."),
        new(
            AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "Promoted bytes were read back and parsed through the retained file handle."),
        new(
            AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "Workspace preflight does not render a preview."),
        new(
            AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.NotApplicable,
            "Workspace preflight does not request human visual acceptance."),
        new(
            AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "The reviewed intake establishes no game-runtime authority."),
        new(
            AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.NotApplicable,
            "Workspace preflight does not promote a package or release.")
    ];

    private static ProtocolDiagnostic ToProtocolDiagnostic(Diagnostic diagnostic)
    {
        if (ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                diagnostic.Code,
                out ProtocolDiagnosticSemantics semantics) &&
            semantics.Severity == diagnostic.Severity)
            return new ProtocolDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message,
                semantics.Class,
                RecoveryFor(diagnostic.Code, semantics.Class));

        string code = diagnostic.Severity switch
        {
            DiagnosticSeverity.Info =>
                ProtocolV2DiagnosticCodes.ReviewedIntakeInfo,
            DiagnosticSeverity.Warning =>
                ProtocolV2DiagnosticCodes.ReviewedIntakeWarning,
            _ => ProtocolV2DiagnosticCodes.ReviewedIntakeValidationFailed
        };
        return new ProtocolDiagnostic(
            code,
            diagnostic.Severity,
            $"{diagnostic.Code}: {diagnostic.Message}",
            DiagnosticClass.Validation,
            RecoveryFor(code, DiagnosticClass.Validation));
    }

    private static ImmutableArray<ProtocolAuthority>
        BindingFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required);

    private static ImmutableArray<ProtocolAuthority>
        AdmissionFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required);

    private static ImmutableArray<ProtocolAuthority>
        PersistenceFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Established,
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Blocked);

    private static ImmutableArray<ProtocolAuthority> FailureAuthority(
        AgentAuthorityState inputAdmission,
        AgentAuthorityState sourceProviderIdentity,
        AgentAuthorityState deterministicMaterialization,
        AgentAuthorityState independentStaticVerification) =>
        Authority().Select(authority => authority.Kind switch
            {
                AgentAuthorityKind.InputAdmission => authority with
                    {
                        State = inputAdmission,
                        Reason = FailureReason(
                            AgentAuthorityKind.InputAdmission,
                            inputAdmission)
                    },
                AgentAuthorityKind.SourceProviderIdentity => authority with
                    {
                        State = sourceProviderIdentity,
                        Reason = FailureReason(
                            AgentAuthorityKind.SourceProviderIdentity,
                            sourceProviderIdentity)
                    },
                AgentAuthorityKind.DeterministicMaterialization => authority with
                    {
                        State = deterministicMaterialization,
                        Reason = FailureReason(
                            AgentAuthorityKind.DeterministicMaterialization,
                            deterministicMaterialization)
                    },
                AgentAuthorityKind.IndependentStaticVerification => authority with
                    {
                        State = independentStaticVerification,
                        Reason = FailureReason(
                            AgentAuthorityKind.IndependentStaticVerification,
                            independentStaticVerification)
                    },
                _ => authority
            })
            .ToImmutableArray();

    private static string FailureReason(
        AgentAuthorityKind kind,
        AgentAuthorityState state) =>
        state switch
        {
            AgentAuthorityState.Established => kind switch
            {
                AgentAuthorityKind.InputAdmission =>
                    "Exact K-local copied-workspace inputs were admitted before persistence failed.",
                AgentAuthorityKind.SourceProviderIdentity =>
                    "The selected plugin closure and provider inventory were hash-bound before persistence failed.",
                _ => "This authority was established before the refusal."
            },
            AgentAuthorityState.Blocked =>
                $"{kind} was blocked by this refused preflight.",
            AgentAuthorityState.Required =>
                $"{kind} remains required after this refused preflight.",
            _ => "This authority is not applicable to workspace preflight."
        };

    private static ProtocolDiagnostic ArtifactDiagnostic(
        ReviewedGameIntakeArtifactException exception)
    {
        ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
            exception.Code,
            out ProtocolDiagnosticSemantics semantics);
        return new ProtocolDiagnostic(
            exception.Code,
            semantics.Severity,
            exception.Message,
            semantics.Class,
            RecoveryFor(exception.Code, semantics.Class));
    }

    private static ProtocolDiagnostic WorkflowDiagnostic(
        AgentWorkflowCodecException exception)
    {
        ProtocolFailureProjection projection =
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                exception.Code);
        return new ProtocolDiagnostic(
            projection.Code,
            DiagnosticSeverity.Error,
            $"{exception.Code}: {exception.Message}",
            projection.Class,
            new DiagnosticRecovery(
                projection.Class == DiagnosticClass.Security
                    ? RecoveryAction.ChooseFreshOutput
                    : projection.Class == DiagnosticClass.Operation
                        ? RecoveryAction.RepairEnvironment
                        : RecoveryAction.CorrectInput,
                "workflow-output",
                "workflow-bundle",
                "Correct the workflow binding or choose a fresh K-local workflow output.",
                false));
    }

    private static ProtocolDiagnostic UsageDiagnostic(string message) =>
        new(
            ProtocolV2DiagnosticCodes.OptionRequired,
            DiagnosticSeverity.Error,
            message,
            DiagnosticClass.Usage,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "Supply every required reviewed workspace preflight option.",
                false));

    private static ProtocolDiagnostic ValidationDiagnostic(string message) =>
        new(
            ProtocolV2DiagnosticCodes.ReviewedIntakeValidationFailed,
            DiagnosticSeverity.Error,
            message,
            DiagnosticClass.Validation,
            RecoveryFor(
                ProtocolV2DiagnosticCodes.ReviewedIntakeValidationFailed,
                DiagnosticClass.Validation));

    private static DiagnosticRecovery RecoveryFor(
        string code,
        DiagnosticClass diagnosticClass) =>
        code == ProtocolV2DiagnosticCodes.ReviewedIntakeOutputParentMissing
            ? new DiagnosticRecovery(
                RecoveryAction.ChooseFreshOutput,
                "output-root",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "Choose a fresh --output-root whose parent is an existing ordinary directory outside DataRoot and OutputRoot.",
                false)
            : code is ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists or
            ProtocolV2DiagnosticCodes.ReviewedIntakeOutputOverlap or
            ProtocolV2DiagnosticCodes.SchemaOutputParentMissing
            ? new DiagnosticRecovery(
                RecoveryAction.ChooseFreshOutput,
                "intake-output",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "Choose a fresh ordinary intake output with an existing parent outside DataRoot and OutputRoot.",
                false)
            : new DiagnosticRecovery(
                diagnosticClass == DiagnosticClass.Operation
                    ? RecoveryAction.RepairEnvironment
                    : RecoveryAction.CorrectInput,
                null,
                null,
                diagnosticClass == DiagnosticClass.Operation
                    ? "Repair the local filesystem condition before retrying with a fresh output."
                    : "Correct the copied-workspace inputs and run preflight again.",
                false);
}
