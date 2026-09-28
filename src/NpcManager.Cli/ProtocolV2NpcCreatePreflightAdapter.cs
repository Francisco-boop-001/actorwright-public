using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2NpcCreatePreflightAdapter(
    IRaceMenuNpcExecutionRequestFileLoader requestLoader,
    INpcBuildPreflightService preflightService,
    AgentWorkflowBundleTransitionService? workflowLifecycle = null) :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ImmutableArray<string> Commands { get; } =
        ["npc create-from-jslot"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!TryBindWorkflow(
                command,
                out WorkspacePath workflowInput,
                out string workflowInputSha256,
                out WorkspacePath workflowOutput,
                out string? workflowFailure))
            return Refused(workflowFailure!);
        ArgumentNullException.ThrowIfNull(workflowLifecycle);
        AgentWorkflowBundleTransition inputWorkflow;
        try
        {
            var preflightOutput = new WorkspacePath(
                command.Options["preflight-output"]);
            workflowLifecycle.AdmitFreshOutput(
                workflowOutput,
                workflowInput,
                preflightOutput);
            if (command.Options.TryGetValue("face-bake-authority-output", out string? derivedPath))
                workflowLifecycle.AdmitFreshOutput(new WorkspacePath(derivedPath), workflowInput, workflowOutput, preflightOutput);
            inputWorkflow = workflowLifecycle.LoadForCommand(
                workflowInput,
                workflowInputSha256,
                "npc create-from-jslot");
            WorkflowArtifactBinding presetBinding =
                inputWorkflow.Document.Bundle.Artifacts.Single(item =>
                    string.Equals(
                        item.Kind,
                        WorkflowArtifactKinds.RaceMenuJslot,
                        StringComparison.Ordinal));
            if (!command.Options.TryGetValue("preset", out string? presetPath) ||
                !command.Options.TryGetValue(
                    "preset-sha256",
                    out string? presetSha256) ||
                !string.Equals(
                    presetBinding.Path.Value,
                    presetPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    presetBinding.Sha256,
                    presetSha256,
                    StringComparison.Ordinal))
                throw new AgentWorkflowCodecException(
                    "workflow-artifact-binding-mismatch",
                    "The preset options do not match the workflow JSlot binding.");
        }
        catch (Exception exception) when (
            exception is AgentWorkflowCodecException or
                InvalidOperationException or KeyNotFoundException)
        {
            return RefusedWorkflow(exception);
        }

        RaceMenuNpcPreflightCommandBindingResult bindingResult =
            RaceMenuNpcPreflightCommandBinder.Bind(command, strict: true);
        if (!bindingResult.IsValid)
            return Refused(bindingResult.ErrorMessage ??
                "NPC build preflight inputs are invalid.");

        RaceMenuNpcPreflightCommandBinding binding = bindingResult.Binding!;
        RaceMenuNpcExecutionRequestFileLoadResult loaded =
            await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    binding.SourceRequest,
                    binding.SourceRequestSha256),
                cancellationToken).ConfigureAwait(false);
        if (!loaded.Loaded || loaded.Request is null)
            return Refused(string.Join(" | ", loaded.Diagnostics.Select(item =>
                $"{item.Code}: {item.Message}")));
        if (!string.Equals(
                loaded.Request.Build.Identity.EditorId.Value,
                inputWorkflow.Document.Bundle.Npc.EditorId,
                StringComparison.Ordinal))
            return RefusedWorkflow(new AgentWorkflowCodecException(
                "workflow-npc-identity-mismatch",
                "The NPC request EditorID does not match the workflow identity."));

        NpcBuildPreflightResult preflight = await preflightService.CreateAsync(
            new NpcBuildPreflightRequest(
                loaded.Request,
                binding.SourceRequest,
                binding.SourceRequestSha256,
                binding.Preset,
                binding.ExpectedPresetSha256,
                binding.DataRoot,
                binding.PluginOrder,
                binding.CompanionRoot,
                binding.Output,
                binding.FaceBakeAuthorityOutput),
            cancellationToken).ConfigureAwait(false);
        ImmutableArray<ProtocolDiagnostic> diagnostics = preflight.Diagnostics
            .Select(ToDiagnostic)
            .DistinctBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!preflight.Created || preflight.Document is null)
            return Refused(
                diagnostics.IsEmpty
                    ? "NPC build preflight was refused."
                    : string.Join(" | ", diagnostics.Select(item => item.Message)));

        NpcBuildPreflightDocument document = preflight.Document;
        string sha256 = document.Sha256.Value.ToUpperInvariant();
        WorkflowArtifactBinding preflightBinding = new(
            WorkflowArtifactKinds.NpcBuildPreflight,
            NpcBuildPreflightSchemas.Artifact,
            document.Path ?? binding.Output,
            document.Utf8Json.Length,
            sha256,
            "npc create-from-jslot",
            requestDigest,
            InputBindings(document.Value));
        if (!preflight.ReadyForBuild)
            return new ProtocolCommandResult(
                [
                    ProtocolEffect.Create(AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed, ApplicationEffectScope.Workspace),
                    ProtocolEffect.Create(AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed, ApplicationEffectScope.KLocalOutput)
                ],
                diagnostics,
                preflight.FaceBakeAuthority is { } derived
                    ? [ToProtocolArtifact(preflightBinding), new ProtocolArtifact("skyrim-face-bake-authority",
                        "skyrim-face-bake-authority/1", derived.Path.Value, new FileInfo(derived.Path.Value).Length,
                        derived.Sha256.Value.ToUpperInvariant(), "npc create-from-jslot", requestDigest, InputBindings(document.Value), "independentlyVerified")]
                    : [ToProtocolArtifact(preflightBinding)],
                RefusalAuthority(),
                [],
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                JsonSerializer.SerializeToElement(PreflightResponse.From(preflight), JsonOptions));
        AgentWorkflowBundleTransition outputWorkflow;
        try
        {
            outputWorkflow = workflowLifecycle.Advance(
                inputWorkflow,
                inputWorkflow.Document.Bundle.Npc with
                {
                    DisplayName =
                        inputWorkflow.Document.Bundle.Npc.DisplayName ??
                        loaded.Request.Build.Identity.Name.Value,
                    Plugin = inputWorkflow.Document.Bundle.Npc.Plugin ??
                        loaded.Request.Build.OutputPlugin.Value
                },
                requestDigest,
                inputWorkflow.Document.Bundle.Artifacts.Add(preflightBinding),
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
                diagnostics.Add(WorkflowDiagnostic(exception)),
                [ToProtocolArtifact(preflightBinding)],
                RefusalAuthority(),
                [],
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                JsonSerializer.SerializeToElement(
                    PreflightResponse.From(preflight),
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
            diagnostics,
            [
                ToProtocolArtifact(preflightBinding),
                ProtocolV2WorkflowBundleProjection.Artifact(
                    outputWorkflow.Document,
                    "npc create-from-jslot",
                    requestDigest)
            ],
            SuccessAuthority(),
            ProtocolV2WorkflowBundleProjection.NextActions(outputWorkflow),
            AgentProtocolSchemaIds.NpcCreatePreflightResult,
            JsonSerializer.SerializeToElement(
                PreflightResponse.From(preflight),
                JsonOptions));
    }

    private static ImmutableArray<string> InputBindings(
        NpcBuildPreflightArtifact artifact) =>
        new[]
        {
            artifact.SourceRequestSha256.Value.ToUpperInvariant(),
            artifact.PresetSha256.Value.ToUpperInvariant()
        }
        .Concat(artifact.Authorities.Select(item =>
            item.Sha256.Value.ToUpperInvariant()))
        .Concat(artifact.FinalDependencyClosure.Select(item =>
            item.Sha256.Value.ToUpperInvariant()))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

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

    private static bool TryBindWorkflow(
        ParsedCommand command,
        out WorkspacePath input,
        out string inputSha256,
        out WorkspacePath output,
        out string? failure)
    {
        input = default;
        output = default;
        inputSha256 = string.Empty;
        failure = null;
        if (!command.Options.TryGetValue(
                "workflow-bundle",
                out string? inputValue) ||
            !command.Options.TryGetValue(
                "workflow-bundle-sha256",
                out string? inputSha256Value) ||
            !IsUpperSha256(inputSha256Value) ||
            !command.Options.TryGetValue(
                "workflow-output",
                out string? outputValue))
        {
            failure = "Protocol-v2 NPC preflight requires --workflow-bundle, uppercase --workflow-bundle-sha256, and --workflow-output.";
            return false;
        }
        inputSha256 = inputSha256Value!;
        input = new WorkspacePath(inputValue);
        output = new WorkspacePath(outputValue);
        return true;
    }

    private static bool IsUpperSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static ImmutableArray<ProtocolAuthority> SuccessAuthority() =>
    [
        Authority(AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact request, JSlot, copied Data, and plugin order were admitted."),
        Authority(AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The current preflight binds the complete provider and dependency closure."),
        Authority(AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The canonical NPC build-preflight artifact was materialized."),
        Authority(AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "The promoted artifact was retained-read and independently reopened."),
        Authority(AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "NPC preflight does not render a preview."),
        Authority(AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.NotApplicable,
            "NPC preflight does not request visual acceptance."),
        Authority(AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Game-runtime verification remains required."),
        Authority(AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.NotApplicable,
            "NPC preflight performs no promotion.")
    ];

    private static ImmutableArray<ProtocolAuthority> RefusalAuthority() =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.InputAdmission => authority with
            {
                State = AgentAuthorityState.Blocked,
                Reason = "Input admission was blocked by the refused preflight."
            },
            AgentAuthorityKind.SourceProviderIdentity or
            AgentAuthorityKind.DeterministicMaterialization or
            AgentAuthorityKind.IndependentStaticVerification => authority with
            {
                State = AgentAuthorityState.Required,
                Reason = "This authority remains required after refusal."
            },
            _ => authority
        }).ToImmutableArray();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ProtocolCommandResult Refused(string message) =>
        new(
            [ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                ApplicationEffectStatus.Refused,
                ApplicationEffectScope.Workspace)],
            [new ProtocolDiagnostic(
                ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed,
                DiagnosticSeverity.Error,
                message,
                DiagnosticClass.Validation,
                new DiagnosticRecovery(
                    RecoveryAction.CorrectInput,
                    null,
                    WorkflowArtifactKinds.NpcBuildPreflight,
                    "Correct the exact NPC preflight bindings and retry with a fresh output.",
                    false))],
            [],
            RefusalAuthority(),
            [],
            AgentProtocolSchemaIds.NpcCreatePreflightResult,
            JsonSerializer.SerializeToElement(
                new PreflightResponse(
                    false, false, false, null, null, [], [], [], []),
                JsonOptions));

    private static ProtocolCommandResult RefusedWorkflow(
        Exception exception)
    {
        AgentWorkflowCodecException normalized = exception as
            AgentWorkflowCodecException ?? new AgentWorkflowCodecException(
                "workflow-transition-invalid",
                exception.Message,
                exception);
        return
        new(
            [ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                ApplicationEffectStatus.Refused,
                ApplicationEffectScope.Workspace)],
            [WorkflowDiagnostic(normalized)],
            [],
            RefusalAuthority(),
            [],
            AgentProtocolSchemaIds.NpcCreatePreflightResult,
            JsonSerializer.SerializeToElement(
                new PreflightResponse(
                    false, false, false, null, null, [], [], [], []),
                JsonOptions));
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
                projection.Class is DiagnosticClass.Security or DiagnosticClass.Operation
                    ? RecoveryAction.ChooseFreshOutput
                    : RecoveryAction.CorrectInput,
                "workflow-output",
                "workflow-bundle",
                "Supply the exact prior workflow bundle and a fresh output.",
                false));
    }

    private static ProtocolDiagnostic ToDiagnostic(Diagnostic diagnostic) =>
        new(
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Info =>
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightInfo,
                DiagnosticSeverity.Warning =>
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightWarning,
                _ => ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed
            },
            diagnostic.Severity,
            $"{diagnostic.Code}: {diagnostic.Message}",
            DiagnosticClass.Validation,
            diagnostic.Recovery ?? new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                WorkflowArtifactKinds.NpcBuildPreflight,
                "Correct the exact NPC preflight bindings and retry with a fresh output.",
                false));

    private sealed record PreflightResponse(
        bool Created,
        bool ReadyForBuild,
        bool PreviewReady,
        string? Path,
        string? Sha256,
        ImmutableArray<NpcBuildPreflightGate> RequiredGates,
        ImmutableArray<NpcBuildPreflightGate> OptionalPreview,
        ImmutableArray<NpcBuildPreflightDependency> DependencyClosure,
        ImmutableArray<Diagnostic> Diagnostics,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        DerivedFaceBakeResponse? FaceBakeAuthority = null)
    {
        public static PreflightResponse From(NpcBuildPreflightResult result) =>
            new(
                result.Created,
                result.ReadyForBuild,
                result.Document?.Value.PreviewReady ?? false,
                result.Document?.Path?.Value,
                result.Document?.Sha256.Value.ToUpperInvariant(),
                result.Document?.Value.RequiredGates ?? [],
                result.Document?.Value.OptionalPreview ?? [],
                result.Document?.Value.DependencyClosure ?? [],
                result.Diagnostics,
                result.FaceBakeAuthority is { } derived ? new(derived.Path.Value, derived.Sha256.Value.ToUpperInvariant()) : null);
    }

    private sealed record DerivedFaceBakeResponse(string Path, string Sha256);
}
