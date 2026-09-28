using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

/// <summary>
/// Exact protocol-v2 adapter for the RaceMenu JSlot inspection step only.
/// </summary>
public sealed class ProtocolV2PresetInspectAdapter(
    WorkspacePath workspaceRoot,
    IPresetExactInspectionService inspectionService,
    PresetInspectionArtifactStore artifactStore,
    AgentWorkflowBundleTransitionService? workflowLifecycle = null,
    FaceGeomHairRegionsWorkspaceBoundary? workflowPathBoundary = null) :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase)
        }
    };

    public ImmutableArray<string> Commands { get; } = ["preset inspect"];

    private readonly FaceGeomHairRegionsWorkspaceBoundary pathBoundary =
        workflowPathBoundary ??
        new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot);

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!ProtocolV2PresetInspectBinder.TryBind(
                command,
                workspaceRoot,
                pathBoundary,
                out ProtocolV2PresetInspectBinding? binding,
                out ImmutableArray<ProtocolDiagnostic> bindingDiagnostics))
            return Refused(
                Effects(ApplicationEffectStatus.Refused),
                bindingDiagnostics,
                requestDigest,
                BindingFailureAuthority());
        PresetParseRequest request = binding!.Request;
        ArgumentNullException.ThrowIfNull(workflowLifecycle);

        AgentWorkflowBundleTransition inputWorkflow;
        try
        {
            workflowLifecycle.AdmitFreshOutput(
                binding.WorkflowOutput,
                binding.WorkflowInput,
                request.SourcePath,
                binding.InspectionOutput);
            inputWorkflow = workflowLifecycle.LoadForCommand(
                binding.WorkflowInput,
                binding.WorkflowInputSha256,
                "preset inspect");
        }
        catch (AgentWorkflowCodecException exception)
        {
            return Refused(
                Effects(ApplicationEffectStatus.Refused),
                [WorkflowDiagnostic(exception)],
                requestDigest,
                BindingFailureAuthority());
        }

        PresetExactInputDocument source;
        try
        {
            source = await artifactStore.ReadExactAsync(
                request.SourcePath,
                binding.InputSha256,
                cancellationToken);
        }
        catch (PresetInspectionArtifactException exception)
        {
            return Refused(
                Effects(ApplicationEffectStatus.Refused),
                [ArtifactDiagnostic(exception)],
                requestDigest,
                BindingFailureAuthority());
        }

        PresetParseResult inspection = await inspectionService.InspectExactAsync(
            request,
            source.Utf8Json.AsMemory(),
            new Sha256Hash(source.Sha256),
            cancellationToken);
        ImmutableArray<ProtocolDiagnostic> diagnostics = inspection.Diagnostics
            .Select(ToProtocolDiagnostic)
            .ToImmutableArray();
        if (inspection.Document is null)
            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics.IsEmpty
                    ? [ValidationDiagnostic(
                        "The admitted preset bytes could not be inspected.")]
                    : diagnostics,
                requestDigest,
                InspectionFailureAuthority(),
                source);

        PresetInspectionReceipt receipt = PresetInspectionReceipt.From(
            inspection.Document,
            source.Path,
            source.Sha256);
        PresetInspectionArtifactDocument persisted;
        try
        {
            persisted = await artifactStore.WriteNewAsync(
                receipt,
                binding.InspectionOutput,
                cancellationToken);
        }
        catch (PresetInspectionArtifactException exception)
        {
            return Refused(
                Effects(
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Failed),
                diagnostics.Add(ArtifactDiagnostic(exception)),
                requestDigest,
                exception.Promoted
                    ? PostPromotionFailureAuthority()
                    : PersistenceFailureAuthority(),
                source,
                inspection.Document);
        }

        var result = PresetInspectResponse.From(
            inspection.Document,
            source.Path,
            persisted);
        WorkflowArtifactBinding sourceBinding = SourceBinding(
            source,
            requestDigest);
        AgentWorkflowBundleTransition? outputWorkflow = null;
        try
        {
            if (inspection.Document.IsValid)
                outputWorkflow = workflowLifecycle.Advance(
                    inputWorkflow,
                    inputWorkflow.Document.Bundle.Npc,
                    requestDigest,
                    inputWorkflow.Document.Bundle.Artifacts.Add(sourceBinding),
                    binding.WorkflowOutput);
        }
        catch (AgentWorkflowCodecException exception)
        {
            return new ProtocolCommandResult(
                Effects(
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Failed),
                diagnostics.Add(WorkflowDiagnostic(exception)),
                [
                    SourceArtifact(sourceBinding),
                    ReceiptArtifact(persisted, source, requestDigest)
                ],
                PostPromotionFailureAuthority(),
                [],
                AgentProtocolSchemaIds.PresetInspectResult,
                JsonSerializer.SerializeToElement(result, JsonOptions));
        }
        ImmutableArray<ProtocolArtifact> artifacts =
            inspection.Document.IsValid
                ?
                [
                    SourceArtifact(sourceBinding),
                    ReceiptArtifact(persisted, source, requestDigest),
                    ProtocolV2WorkflowBundleProjection.Artifact(
                        outputWorkflow!.Document,
                        "preset inspect",
                        requestDigest)
                ]
                : [ReceiptArtifact(persisted, source, requestDigest)];
        ImmutableArray<ProtocolNextAction> nextActions =
            inspection.Document.IsValid
                ? ProtocolV2WorkflowBundleProjection.NextActions(
                    outputWorkflow!)
                : [];
        return new ProtocolCommandResult(
            inspection.Document.IsValid
                ? Effects(
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Completed)
                : Effects(
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectStatus.Completed),
            diagnostics,
            artifacts,
            SuccessAuthority(),
            nextActions,
            AgentProtocolSchemaIds.PresetInspectResult,
            JsonSerializer.SerializeToElement(result, JsonOptions));
    }

    private static ProtocolArtifact SourceArtifact(
        WorkflowArtifactBinding source) =>
        new(
            source.Kind,
            source.SchemaOrMediaType,
            source.Path.Value,
            source.Size,
            source.Sha256,
            source.ProducerCommand,
            source.RequestDigest,
            source.InputArtifactHashes,
            "independentlyVerified");

    private static WorkflowArtifactBinding SourceBinding(
        PresetExactInputDocument source,
        string requestDigest) => new(
        WorkflowArtifactKinds.RaceMenuJslot,
        "application/json",
        source.Path,
        source.Size,
        source.Sha256,
        "preset inspect",
        requestDigest,
        []);

    private static ProtocolArtifact ReceiptArtifact(
        PresetInspectionArtifactDocument persisted,
        PresetExactInputDocument source,
        string requestDigest) =>
        new(
            PresetInspectionSchemas.ArtifactKind,
            PresetInspectionSchemas.CurrentDocument,
            persisted.Path.Value,
            persisted.Size,
            persisted.Sha256,
            "preset inspect",
            requestDigest,
            [source.Sha256],
            "independentlyVerified");

    private static ProtocolNextAction CreatePreflightAction(
        PresetExactInputDocument source) =>
        new(
            "npc create-from-jslot",
            "The exact admitted JSlot is ready, but real NPC request, provider, workspace, companion, and fresh preflight-output bindings remain required.",
            [
                new ProtocolNextActionBinding(
                    "--preset",
                    source.Path.Value,
                    source.Sha256),
                new ProtocolNextActionBinding(
                    "--preset-sha256",
                    source.Sha256,
                    source.Sha256)
            ],
            [
                "--request", "--request-sha256", "--data-root",
                "--plugins", "--companion-root", "--preflight-output"
            ],
            false);

    private static ImmutableArray<ProtocolEffect> Effects(
        ApplicationEffectStatus readStatus,
        params ApplicationEffectStatus[] writeStatuses) =>
    [
        ProtocolEffect.Create(
            AgentEffectKind.ReadWorkspace,
            readStatus,
            ApplicationEffectScope.Workspace),
        .. writeStatuses.Select(status => ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            status,
            ApplicationEffectScope.KLocalOutput))
    ];

    private static ProtocolCommandResult Refused(
        ImmutableArray<ProtocolEffect> effects,
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        string requestDigest,
        ImmutableArray<ProtocolAuthority> authority,
        PresetExactInputDocument? source = null,
        PresetDocument? document = null) =>
        new(
            effects,
            diagnostics,
            [],
            authority,
            [],
            AgentProtocolSchemaIds.PresetInspectResult,
            JsonSerializer.SerializeToElement(
                document is null
                    ? new PresetInspectResponse(
                        "1", "racemenu-jslot", "skyrimse",
                        source?.Path.Value, source?.Sha256, false,
                        null, [], null, null, null)
                    : new PresetInspectResponse(
                        "1", document.Format.ToWireName(),
                        document.Edition.ToWireName(), source!.Path.Value,
                        source!.Sha256, document.IsValid,
                        document.Appearance, document.Diagnostics,
                        null, null, null),
                JsonOptions));

    private static ImmutableArray<ProtocolAuthority> SuccessAuthority() =>
    [
        new(
            AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact ordinary K-local JSlot bytes and uppercase digest were admitted."),
        new(
            AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Required,
            "Preset inspection does not establish plugin or asset provider identity."),
        new(
            AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The strict preset-inspection receipt was canonically materialized."),
        new(
            AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "The receipt was read back, strictly parsed, and independently pinned and reloaded."),
        new(
            AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "Preset inspection does not render a preview."),
        new(
            AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.NotApplicable,
            "Preset inspection does not request visual acceptance."),
        new(
            AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Game-runtime verification remains required."),
        new(
            AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.NotApplicable,
            "Preset inspection performs no promotion.")
    ];

    private static ImmutableArray<ProtocolAuthority>
        BindingFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required);

    private static ImmutableArray<ProtocolAuthority>
        InspectionFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required);

    private static ImmutableArray<ProtocolAuthority>
        PersistenceFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Blocked);

    private static ImmutableArray<ProtocolAuthority>
        PostPromotionFailureAuthority() => FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Established,
            AgentAuthorityState.Blocked);

    private static ImmutableArray<ProtocolAuthority> FailureAuthority(
        AgentAuthorityState input,
        AgentAuthorityState materialization,
        AgentAuthorityState verification) =>
        SuccessAuthority().Select(authority => authority.Kind switch
            {
                AgentAuthorityKind.InputAdmission => authority with
                    {
                        State = input,
                        Reason = $"Input admission is {StateName(input)} after refusal."
                    },
                AgentAuthorityKind.DeterministicMaterialization => authority with
                    {
                        State = materialization,
                        Reason = $"Receipt materialization is {StateName(materialization)} after refusal."
                    },
                AgentAuthorityKind.IndependentStaticVerification => authority with
                    {
                        State = verification,
                        Reason = $"Receipt verification is {StateName(verification)} after refusal."
                    },
                _ => authority
            }).ToImmutableArray();

    private static string StateName(AgentAuthorityState state) =>
        state.ToString().ToLowerInvariant();

    private static ProtocolDiagnostic ToProtocolDiagnostic(
        Diagnostic diagnostic)
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
                ProtocolV2DiagnosticCodes.PresetInspectionInfo,
            DiagnosticSeverity.Warning =>
                ProtocolV2DiagnosticCodes.PresetInspectionWarning,
            _ => ProtocolV2DiagnosticCodes.PresetInspectionValidationFailed
        };
        return new ProtocolDiagnostic(
            code,
            diagnostic.Severity,
            $"{diagnostic.Code}: {diagnostic.Message}",
            DiagnosticClass.Validation,
            RecoveryFor(code, DiagnosticClass.Validation));
    }

    private static ProtocolDiagnostic ArtifactDiagnostic(
        PresetInspectionArtifactException exception)
    {
        if (!ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                exception.Code,
                out ProtocolDiagnosticSemantics semantics))
            semantics = new ProtocolDiagnosticSemantics(
                DiagnosticSeverity.Error,
                DiagnosticClass.Operation);
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
                    : RecoveryAction.CorrectInput,
                "workflow-output",
                "workflow-bundle",
                "Correct the workflow binding or choose a fresh K-local workflow output.",
                false));
    }

    private static ProtocolDiagnostic ValidationDiagnostic(string message) =>
        new(
            ProtocolV2DiagnosticCodes.PresetInspectionValidationFailed,
            DiagnosticSeverity.Error,
            message,
            DiagnosticClass.Validation,
            RecoveryFor(
                ProtocolV2DiagnosticCodes.PresetInspectionValidationFailed,
                DiagnosticClass.Validation));

    internal static DiagnosticRecovery RecoveryFor(
        string code,
        DiagnosticClass diagnosticClass) =>
        code == ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed
            ? new DiagnosticRecovery(
                RecoveryAction.None,
                null,
                PresetInspectionSchemas.ArtifactKind,
                "Retain the exact receipt bytes and report the product consistency failure; upgrade Actorwright before retrying.",
                false)
            : code is ProtocolV2DiagnosticCodes.PresetInspectionOutputExists or
            ProtocolV2DiagnosticCodes.SchemaOutputParentMissing
            ? new DiagnosticRecovery(
                RecoveryAction.ChooseFreshOutput,
                "inspection-output",
                PresetInspectionSchemas.ArtifactKind,
                "Choose a fresh ordinary inspection output with an existing ordinary parent beneath the exact workspace.",
                false)
            : new DiagnosticRecovery(
                diagnosticClass == DiagnosticClass.Operation
                    ? RecoveryAction.RepairEnvironment
                    : RecoveryAction.CorrectInput,
                null,
                null,
                diagnosticClass == DiagnosticClass.Operation
                    ? "Repair the local filesystem condition and retry with a fresh output."
                    : "Correct the exact preset bindings and retry.",
                false);

    private sealed record PresetInspectResponse(
        string SchemaVersion,
        string Format,
        string Edition,
        string? SourcePath,
        string? SourceSha256,
        bool IsValid,
        PresetAppearance? Appearance,
        ImmutableArray<Diagnostic> Diagnostics,
        string? InspectionPath,
        long? InspectionSize,
        string? InspectionSha256)
    {
        public static PresetInspectResponse From(
            PresetDocument document,
            WorkspacePath sourcePath,
            PresetInspectionArtifactDocument persisted) =>
            new(
                "1",
                document.Format.ToWireName(),
                document.Edition.ToWireName(),
                sourcePath.Value,
                persisted.Receipt.SourceSha256,
                document.IsValid,
                document.Appearance,
                document.Diagnostics,
                persisted.Path.Value,
                persisted.Size,
                persisted.Sha256);
    }
}
