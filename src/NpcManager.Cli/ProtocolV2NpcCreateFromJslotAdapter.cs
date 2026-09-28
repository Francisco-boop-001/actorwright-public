using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2NpcCreateFromJslotAdapter :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IRaceMenuNpcExecutionRequestFileLoader requestLoader;
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsDocumentCodec intakeCodec;
    private readonly INpcBuildPreflightDocumentCodec preflightDocuments;
    private readonly IRaceMenuJslotNpcBuildCommandExecutor buildExecutor;
    private readonly IPackageVerifyService packageVerifier;
    private readonly INpcStaticBuildPackageAdmission packageAdmission;
    private readonly AgentWorkflowBundleTransitionService workflowLifecycle;
    private readonly ProtocolV2NpcCreatePreflightAdapter preflightAdapter;
    private readonly IRaceMenuNpcStandaloneAuthorityReader
        standaloneAuthorityReader;
    private readonly RaceMenuJslotNpcBuildCommandExecutionBridge?
        typedBuildBridge;

    internal ProtocolV2NpcCreateFromJslotAdapter(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsDocumentCodec intakeCodec,
        IRaceMenuNpcExecutionRequestFileLoader requestLoader,
        INpcBuildPreflightService preflightService,
        INpcBuildPreflightDocumentCodec preflightDocuments,
        IRaceMenuJslotNpcBuildCommandExecutor buildExecutor,
        IPackageVerifyService packageVerifier,
        PackageManifestReader packageManifestReader,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        INpcStaticBuildPackageAdmission? packageAdmission = null,
        RaceMenuJslotNpcBuildCommandExecutionBridge? typedBuildBridge = null,
        IRaceMenuNpcStandaloneAuthorityReader? standaloneAuthorityReader = null)
    {
        this.workspaceRoot = workspaceRoot;
        this.intakeCodec = intakeCodec ??
            throw new ArgumentNullException(nameof(intakeCodec));
        this.requestLoader = requestLoader ??
            throw new ArgumentNullException(nameof(requestLoader));
        this.preflightDocuments = preflightDocuments ??
            throw new ArgumentNullException(nameof(preflightDocuments));
        this.buildExecutor = buildExecutor ??
            throw new ArgumentNullException(nameof(buildExecutor));
        this.packageVerifier = packageVerifier ??
            throw new ArgumentNullException(nameof(packageVerifier));
        ArgumentNullException.ThrowIfNull(packageManifestReader);
        this.packageAdmission = packageAdmission ??
            new NpcStaticBuildPackageAdmission(
                workspaceRoot,
                packageManifestReader);
        this.workflowLifecycle = workflowLifecycle ??
            throw new ArgumentNullException(nameof(workflowLifecycle));
        this.typedBuildBridge = typedBuildBridge;
        this.standaloneAuthorityReader = standaloneAuthorityReader ??
            throw new ArgumentNullException(nameof(standaloneAuthorityReader));
        preflightAdapter = new ProtocolV2NpcCreatePreflightAdapter(
            requestLoader,
            preflightService ?? throw new ArgumentNullException(
                nameof(preflightService)),
            workflowLifecycle);
    }

    public ImmutableArray<string> Commands { get; } =
        ["npc create-from-jslot"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Options.ContainsKey("preflight-output"))
            return await preflightAdapter.RunAsync(
                command,
                requestDigest,
                cancellationToken).ConfigureAwait(false);

        RaceMenuJslotNpcBuildCommandBindingResult bindingResult =
            RaceMenuJslotNpcBuildCommandBinder.Bind(
                command,
                strict: true,
                requireReviewedPreflight: true);
        if (!bindingResult.IsValid)
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildValidationFailed,
                DiagnosticClass.Validation,
                bindingResult.ErrorMessage ??
                "Reviewed NPC build inputs are invalid.");
        RaceMenuJslotNpcBuildCommandBinding binding = bindingResult.Binding!;

        if (!TryBindWorkflow(
                command,
                out WorkspacePath workflowInput,
                out string workflowInputSha256,
                out WorkspacePath workflowOutput,
                out string? workflowFailure))
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildValidationFailed,
                DiagnosticClass.Validation,
                workflowFailure!);

        AgentWorkflowBundleTransition inputWorkflow;
        WorkflowArtifactBinding intakeArtifact;
        WorkflowArtifactBinding presetArtifact;
        WorkflowArtifactBinding preflightArtifact;
        ReviewedGameIntakeDocumentAuthority admittedIntake;
        NpcBuildPreflightDocument reviewedPreflight;
        RaceMenuNpcExecutionRequestFileLoadResult loaded;
        try
        {
            workflowLifecycle.AdmitFreshOutput(
                workflowOutput,
                workflowInput,
                binding.ReviewedPreflight!.Path);
        }
        catch (AgentWorkflowCodecException exception)
        {
            return RefusedWorkflow(
                exception,
                WorkflowRecoveryStage.OutputBundle);
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        {
            return RefusedException(exception);
        }

        try
        {
            inputWorkflow = workflowLifecycle.LoadForCommand(
                workflowInput,
                workflowInputSha256,
                "npc create-from-jslot");
        }
        catch (AgentWorkflowCodecException exception)
        {
            return RefusedWorkflow(
                exception,
                WorkflowRecoveryStage.InputBundle);
        }

        BuildAdmissionTarget admissionTarget =
            BuildAdmissionTarget.WorkflowBundle;
        try
        {
            intakeArtifact = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => item.Kind ==
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake);
            presetArtifact = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => item.Kind == WorkflowArtifactKinds.RaceMenuJslot);
            preflightArtifact = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => item.Kind == WorkflowArtifactKinds.NpcBuildPreflight);
            RequireArtifactOption(
                presetArtifact,
                binding.Preset,
                binding.ExpectedPresetSha256,
                "preset");
            RequireArtifactOption(
                preflightArtifact,
                binding.ReviewedPreflight.Path,
                binding.ReviewedPreflight.ExpectedSha256,
                "reviewed preflight");
            admissionTarget = BuildAdmissionTarget.ReviewedIntake;
            admittedIntake = await intakeCodec.LoadReviewedIntakeAsync(
                intakeArtifact.Path,
                cancellationToken).ConfigureAwait(false);
            RequireReviewedIntakeBinding(
                intakeArtifact,
                admittedIntake,
                binding);
            admissionTarget = BuildAdmissionTarget.ReviewedPreflight;
            reviewedPreflight = await preflightDocuments.ReadExactAsync(
                binding.ReviewedPreflight.Path,
                binding.ReviewedPreflight.ExpectedSha256,
                cancellationToken).ConfigureAwait(false);
            RequirePreflightBinding(reviewedPreflight.Value, binding);
            admissionTarget = BuildAdmissionTarget.SourceRequest;
            loaded = await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    binding.SourceRequest,
                    binding.SourceRequestSha256),
                cancellationToken).ConfigureAwait(false);
            if (!loaded.Loaded || loaded.Request is null)
                throw new InvalidDataException(string.Join(
                    " | ",
                    loaded.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
            RequireNpcIdentity(inputWorkflow.Document.Bundle, loaded.Request);
        }
        catch (OperationCanceledException)
        {
            return RefusedAdmissionCancellation(admissionTarget);
        }
        catch (AgentWorkflowCodecException exception)
        {
            if (admissionTarget == BuildAdmissionTarget.WorkflowBundle)
                return RefusedWorkflow(
                    exception,
                    WorkflowRecoveryStage.InputBundle,
                    effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
            return RefusedAdmission(
                exception.Message,
                admissionTarget,
                ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }
        catch (Exception exception) when (IsAdmissionFailure(exception))
        {
            return RefusedAdmission(
                exception.Message,
                admissionTarget,
                ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }

        ProtocolCommandResult? intakeOutputReuseRefusal =
            RefuseReviewedIntakeOutputRootReuse(
                loaded.Request.Build.OutputRoot,
                admittedIntake.Value.OutputRoot);
        if (intakeOutputReuseRefusal is not null)
            return intakeOutputReuseRefusal;

        ProtocolCommandResult? overlapRefusal = RefuseProtectedOutputOverlap(
            workflowOutput,
            loaded.Request.Build.OutputRoot,
            admittedIntake.Value.DataRoot);
        if (overlapRefusal is not null)
            return overlapRefusal;

        RaceMenuJslotNpcBuildCommandExecution execution;
        bool externalSmpRequest = false;
        try
        {
            externalSmpRequest =
                await RequiresExternalSmpAsync(
                    loaded.Request,
                    cancellationToken).ConfigureAwait(false);
            if (externalSmpRequest)
            {
                if (typedBuildBridge is null)
                    return RefuseMissingExternalTypedBridge(
                        "The external-SMP JSlot route requires the typed in-memory command bridge.");

                RaceMenuJslotNpcBuildRequest typedRequest =
                    new(
                        loaded.Request,
                        binding.Preset,
                        binding.ExpectedPresetSha256,
                        binding.DataRoot,
                        binding.PluginOrder,
                        binding.CompanionRoot)
                    {
                        SourceRequest = binding.SourceRequest,
                        SourceRequestSha256 = binding.SourceRequestSha256,
                        ReviewedPreflight = binding.ReviewedPreflight
                    };
                execution = await typedBuildBridge.ExecuteAsync(
                        command,
                        typedRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                // RaceMenuJslotNpcBuildService owns the strict external-SMP
                // artifact gate and performs it before moving the package.
                // A successful typed result is therefore the sole admission
                // authority; do not introduce a rejectable post-promotion
                // validation seam here.
            }
            else
            {
                execution = await buildExecutor.ExecuteAsync(
                        command,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return RefusedPostAdmissionCancellation(
                "The static NPC build was cancelled.");
        }
        if (execution.ExitCode == CommandExitCode.Cancelled)
            return RefusedPostAdmissionCancellation(
                "The static NPC build was cancelled.");
        if (execution.ExitCode != CommandExitCode.Success)
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildOperationFailed,
                DiagnosticClass.Operation,
                FailureDetail(execution),
                effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                authority: PostAdmissionAuthority(externalSmpRequest));

        WorkspacePath manifestPath = new(Path.Combine(
            loaded.Request.Build.OutputRoot.Value,
            "npcmanager-package.json"));
        PackageVerifyResult verification;
        try
        {
            verification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(manifestPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RefusedPostAdmissionCancellation(
                "NPC package verification was cancelled.");
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
                DiagnosticClass.Verification,
                exception.Message,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                authority: PostAdmissionAuthority());
        }
        if (!verification.Verified || verification.Artifact is not
            { RuntimeProof: false } verified)
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
                DiagnosticClass.Verification,
                verification.Diagnostics.IsEmpty
                    ? "The produced NPC package failed independent verification."
                    : string.Join(" | ", verification.Diagnostics.Select(
                        item => $"{item.Code}: {item.Message}")),
                serviceDiagnostics: verification.Diagnostics,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                authority: PostAdmissionAuthority());
        if (!string.Equals(
                verified.OutputPlugin,
                loaded.Request.Build.OutputPlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                verified.ManifestPath.Value,
                manifestPath.Value,
                StringComparison.OrdinalIgnoreCase))
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
                DiagnosticClass.Verification,
                "The verified package identity differs from the admitted NPC request.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                authority: PostAdmissionAuthority());

        NpcStaticBuildPackageLease packageLease;
        try
        {
            packageLease = await packageAdmission.AdmitAsync(
                verified,
                binding,
                loaded.Request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RefusedPostAdmissionCancellation(
                "NPC package admission was cancelled.");
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
                DiagnosticClass.Verification,
                exception.Message,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
                authority: PostAdmissionAuthority());
        }
        using var terminalLease = new ProtocolV2TerminalArtifactLeaseScope();
        terminalLease.Own(packageLease);
        PackageManifestIdentity packageIdentity = packageLease.Identity;
        string manifestSha256 = packageIdentity.ManifestSha256.Value
            .ToUpperInvariant();
        ImmutableArray<string> physicalInputs = inputWorkflow.Document.Bundle
            .Artifacts.Select(item => item.Sha256)
            .Concat(packageIdentity.Files.Select(item =>
                item.Sha256.Value.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var manifestBinding = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcPackageManifest,
            "application/json",
            manifestPath,
            packageLease.ManifestSize,
            manifestSha256,
            "npc create-from-jslot",
            requestDigest,
            physicalInputs);
        string targetFormId = verified.TargetFormId.Value.ToString(
            "X8",
            CultureInfo.InvariantCulture);
        AgentWorkflowBundleTransition outputWorkflow;
        try
        {
            AgentWorkflowBundleTransitionLease retainedWorkflow =
                workflowLifecycle.AdvanceRetained(
                inputWorkflow,
                inputWorkflow.Document.Bundle.Npc with
                {
                    DisplayName =
                        inputWorkflow.Document.Bundle.Npc.DisplayName ??
                        loaded.Request.Build.Identity.Name.Value,
                    Plugin = verified.OutputPlugin,
                    LocalFormId = targetFormId
                },
                requestDigest,
                [manifestBinding, intakeArtifact],
                workflowOutput);
            terminalLease.Own(retainedWorkflow);
            outputWorkflow = retainedWorkflow.Transition;
        }
        catch (AgentWorkflowCodecException exception)
        {
            return RefusedWorkflow(
                exception,
                WorkflowRecoveryStage.OutputBundle,
                manifestBinding,
                ProtocolV2CommitEffects.PublishedArtifactWorkflowFailed(),
                SuccessAuthority()) with
            {
                TerminalArtifactLease = terminalLease.Transfer()
            };
        }

        PackageManifestFile auditedPlugin = packageIdentity.Files.Single(item => item.Kind == "plugin");
        var defaults = NpcInheritedDefaultsAudit.Read(GameEdition.SkyrimSpecialEdition, workspaceRoot,
            new WorkspacePath(Path.Combine(loaded.Request.Build.OutputRoot.Value,
                auditedPlugin.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar))),
            new FormReference(new PluginName(verified.OutputPlugin), verified.TargetFormId), cancellationToken, auditedPlugin.Sha256);
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
            [],
            [
                ToProtocolArtifact(manifestBinding),
                ProtocolV2WorkflowBundleProjection.Artifact(
                    outputWorkflow.Document,
                    "npc create-from-jslot",
                    requestDigest)
            ],
            SuccessAuthority(),
            ProtocolV2WorkflowBundleProjection.NextActions(outputWorkflow),
            AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult,
            JsonSerializer.SerializeToElement(
                BuildResponse.Success(
                    manifestPath.Value,
                    packageLease.ManifestSize,
                    manifestSha256,
                    verified.OutputPlugin,
                    targetFormId,
                    execution.TypedResult?.Execution?.ExternalInstallPrepublication is
                        { } externalInstallPrepublication
                        ? ExternalInstallPrepublicationProtocolProjection.Serialize(
                            externalInstallPrepublication)
                        : null,
                    defaults.Fields,
                    defaults.Diagnostics),
                JsonOptions))
        {
            TerminalArtifactLease = terminalLease.Transfer()
        };
    }

    private static ProtocolCommandResult RefuseMissingExternalTypedBridge(
        string message) =>
        Refused(
            ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
            DiagnosticClass.Verification,
            message,
            effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
            authority: PostAdmissionAuthority(externalSmpFailure: true));

    private static void RequireArtifactOption(
        WorkflowArtifactBinding artifact,
        WorkspacePath path,
        Sha256Hash hash,
        string role)
    {
        if (!string.Equals(
                artifact.Path.Value,
                path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                artifact.Sha256,
                hash.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new AgentWorkflowCodecException(
                "workflow-artifact-binding-mismatch",
                $"The {role} options differ from the workflow binding.");
    }

    private static void RequireReviewedIntakeBinding(
        WorkflowArtifactBinding artifact,
        ReviewedGameIntakeDocumentAuthority admitted,
        RaceMenuJslotNpcBuildCommandBinding binding)
    {
        string physicalSha256 = admitted.Document.Sha256.Value
            .ToUpperInvariant();
        ImmutableArray<PluginClosureReviewEntry> plugins =
            admitted.Value.Plugins;
        if (!string.Equals(
                artifact.Path.Value,
                admitted.Document.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            artifact.Size != admitted.Document.ByteLength ||
            !string.Equals(
                artifact.Sha256,
                physicalSha256,
                StringComparison.Ordinal) ||
            ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                admitted.Value) != admitted.Value.IntakeFingerprint)
            throw new InvalidDataException(
                "The reviewed intake no longer binds the exact physical intake and semantic fingerprint used by this build.");
        if (!string.Equals(
                admitted.Value.DataRoot.Value,
                binding.DataRoot.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The reviewed intake DataRoot differs from the exact build DataRoot.");
        if (!IsDistinctOrderedPluginSubset(
                plugins,
                binding.PluginOrder))
            throw new InvalidDataException(
                "The reviewed intake does not contain the build plugins as one distinct ordered subset.");
    }

    private static bool IsDistinctOrderedPluginSubset(
        ImmutableArray<PluginClosureReviewEntry> admitted,
        ImmutableArray<PluginName> selected)
    {
        if (selected.IsDefaultOrEmpty)
            return false;

        var distinct = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (PluginName plugin in selected)
        {
            if (!distinct.Add(plugin.Value))
                return false;
        }

        int selectedIndex = 0;
        foreach (PluginClosureReviewEntry plugin in admitted)
        {
            if (!string.Equals(
                    plugin.Plugin.Value,
                    selected[selectedIndex].Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            selectedIndex++;
            if (selectedIndex == selected.Length)
                return true;
        }

        return false;
    }

    private static void RequirePreflightBinding(
        NpcBuildPreflightArtifact preflight,
        RaceMenuJslotNpcBuildCommandBinding binding)
    {
        if (!preflight.ReadyForBuild || preflight.RuntimeAuthority ||
            !string.Equals(
                preflight.SourceRequest.Value,
                binding.SourceRequest.Value,
                StringComparison.OrdinalIgnoreCase) ||
            preflight.SourceRequestSha256 != binding.SourceRequestSha256 ||
            preflight.PresetSha256 != binding.ExpectedPresetSha256 ||
            !preflight.WinningPluginOrder.SequenceEqual(
                binding.PluginOrder.Select(item => item.Value),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The reviewed preflight no longer binds the admitted build inputs.");
    }

    private static void RequireNpcIdentity(
        AgentWorkflowBundle workflow,
        RaceMenuNpcExecutionRequest request)
    {
        if (!string.Equals(
                workflow.Npc.EditorId,
                request.Build.Identity.EditorId.Value,
                StringComparison.Ordinal) ||
            workflow.Npc.DisplayName is { } displayName && !string.Equals(
                displayName,
                request.Build.Identity.Name.Value,
                StringComparison.Ordinal) ||
            workflow.Npc.Plugin is { } plugin && !string.Equals(
                plugin,
                request.Build.OutputPlugin.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new AgentWorkflowCodecException(
                "workflow-npc-identity-mismatch",
                "The NPC request identity differs from the workflow identity.");
    }

    private static string FailureDetail(
        RaceMenuJslotNpcBuildCommandExecution execution)
    {
        string detail = string.IsNullOrWhiteSpace(execution.Error)
            ? execution.Output
            : execution.Error;
        if (string.IsNullOrWhiteSpace(detail))
            return $"NPC build exited with {execution.ExitCode}.";
        detail = detail.Trim();
        if (TryProjectFailureDiagnostics(detail, out string projected))
            return projected;
        return detail.Length <= 2048 ? detail : detail[..2048];
    }

    private async ValueTask<bool> RequiresExternalSmpAsync(
        RaceMenuNpcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        RaceMenuNpcStandaloneAuthorityReadResult read =
            await standaloneAuthorityReader.ReadAsync(
                request.AssetAuthority,
                cancellationToken).ConfigureAwait(false);
        if (read.Accepted && read.Assets is not null)
            return RaceMenuJslotNpcBuildCommandExecutionBridge
                .RequiresExternalSmp(read.Assets);

        // A production composition cannot safely classify a malformed
        // standalone authority as ordinary input: it may be a damaged
        // schema-8 external route.  Fail through the typed bridge so no
        // legacy stdout path can publish it without prepublication evidence.
        return true;
    }

    private static bool TryProjectFailureDiagnostics(
        string detail,
        out string projected)
    {
        const int maximumJsonCharacters = 256 * 1024;
        const int maximumDiagnosticRows = 256;
        const int maximumProjectedCharacters = 2048;
        projected = string.Empty;
        if (detail.Length > maximumJsonCharacters)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                detail,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(
                    "diagnostics",
                    out JsonElement diagnostics) ||
                diagnostics.ValueKind != JsonValueKind.Array ||
                diagnostics.GetArrayLength() > maximumDiagnosticRows)
                return false;
            string[] errors = diagnostics.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("severity", out JsonElement severity) &&
                    severity.ValueKind == JsonValueKind.String &&
                    string.Equals(
                        severity.GetString(),
                        "error",
                        StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("code", out JsonElement code) &&
                    code.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(code.GetString()) &&
                    item.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(message.GetString()))
                .Select(item =>
                    $"{item.GetProperty("code").GetString()}: " +
                    item.GetProperty("message").GetString())
                .ToArray();
            if (errors.Length == 0)
                return false;
            projected = string.Join(" | ", errors);
            if (projected.Length > maximumProjectedCharacters)
                projected = projected[..maximumProjectedCharacters];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAdmissionFailure(Exception exception) =>
        exception is AgentWorkflowCodecException or InvalidDataException or
            IOException or
            UnauthorizedAccessException or InvalidOperationException or
            KeyNotFoundException or ArgumentException;

    private static ProtocolCommandResult? RefuseProtectedOutputOverlap(
        WorkspacePath workflowOutput,
        WorkspacePath packageOutputRoot,
        WorkspacePath dataRoot)
    {
        if (!Overlaps(workflowOutput, packageOutputRoot) &&
            !Overlaps(workflowOutput, dataRoot))
            return null;
        return Refused(
            ProtocolV2DiagnosticCodes.ProtectedRootRefused,
            DiagnosticClass.Security,
            "The workflow output must not overlap the admitted package output root or intake DataRoot.",
            effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
            recoveryOption: "workflow-output",
            recoveryArtifactKind: "workflow-bundle",
            authority: PostAdmissionAuthority(),
            recoveryAction: RecoveryAction.ChooseFreshOutput,
            recoveryConstraint:
                "Choose a fresh workflow output outside the admitted package output root and intake DataRoot.");
    }

    private static ProtocolCommandResult? RefuseReviewedIntakeOutputRootReuse(
        WorkspacePath packageOutputRoot,
        WorkspacePath reservedOutputRoot)
    {
        if (!string.Equals(
                packageOutputRoot.Value,
                reservedOutputRoot.Value,
                StringComparison.OrdinalIgnoreCase))
            return null;
        return Refused(
            ProtocolV2DiagnosticCodes.ReviewedIntakeOutputReuse,
            DiagnosticClass.Security,
            "The package --output-root must differ from the retained reviewed-intake reserved --output-root.",
            effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
            recoveryOption: "output-root",
            recoveryArtifactKind: WorkflowArtifactKinds.NpcBuildPreflight,
            authority: PostAdmissionAuthority(),
            recoveryAction: RecoveryAction.ChooseFreshOutput,
            recoveryConstraint:
                "Choose a fresh package --output-root distinct from the retained reviewed-intake reserved output root.");
    }

    private static bool Overlaps(WorkspacePath left, WorkspacePath right) =>
        left.IsUnder(right) || right.IsUnder(left);

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
                "workflow-bundle", out string? inputValue) ||
            !command.Options.TryGetValue(
                "workflow-bundle-sha256", out string? sha256) ||
            !IsUpperSha256(sha256) ||
            !command.Options.TryGetValue(
                "workflow-output", out string? outputValue))
        {
            failure = "Protocol-v2 NPC build requires --workflow-bundle, uppercase --workflow-bundle-sha256, and --workflow-output.";
            return false;
        }
        input = new WorkspacePath(inputValue);
        inputSha256 = sha256!;
        output = new WorkspacePath(outputValue);
        return true;
    }

    private static bool IsUpperSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

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

    private static ImmutableArray<ProtocolAuthority> SuccessAuthority() =>
    [
        Authority(AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact request, reviewed preflight, JSlot, copied Data, and plugin order were admitted."),
        Authority(AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The reviewed preflight retains the complete provider and dependency closure."),
        Authority(AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The static NPC package was deterministically materialized."),
        Authority(AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "The package manifest and every declared file were independently reopened."),
        Authority(AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "NPC build does not render an off-engine preview."),
        Authority(AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.NotApplicable,
            "Static build does not request human visual acceptance."),
        Authority(AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Game-runtime verification remains user-operated and required."),
        Authority(AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.NotApplicable,
            "Static build performs no promotion.")
    ];

    private static ImmutableArray<ProtocolAuthority> RefusalAuthority() =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.InputAdmission => authority with
            {
                State = AgentAuthorityState.Blocked,
                Reason = "Input admission was blocked by the refused build."
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

    private static ImmutableArray<ProtocolAuthority> PostAdmissionAuthority(
        bool externalSmpFailure = false) =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.SourceProviderIdentity when externalSmpFailure =>
                authority with
                {
                    State = AgentAuthorityState.Required,
                    Reason = "External SMP provider/precheck authority was not established."
                },
            AgentAuthorityKind.InputAdmission or
            AgentAuthorityKind.SourceProviderIdentity => authority,
            AgentAuthorityKind.DeterministicMaterialization or
            AgentAuthorityKind.IndependentStaticVerification => authority with
            {
                State = AgentAuthorityState.Required,
                Reason = "The admitted build did not produce an independently verified package."
            },
            _ => authority
        }).ToImmutableArray();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ProtocolCommandResult RefusedException(
        Exception exception,
        WorkflowArtifactBinding? manifest = null,
        ImmutableArray<ProtocolEffect> effects = default)
    {
        bool path = exception is UnauthorizedAccessException ||
            exception.Message.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase);
        return Refused(
            path
                ? ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused
                : ProtocolV2DiagnosticCodes.NpcBuildValidationFailed,
            path ? DiagnosticClass.Security : DiagnosticClass.Validation,
            exception is AgentWorkflowCodecException workflow
                ? $"{workflow.Code}: {workflow.Message}"
                : exception.Message,
            manifest,
            effects: effects);
    }

    private static ProtocolCommandResult RefusedWorkflow(
        AgentWorkflowCodecException exception,
        WorkflowRecoveryStage stage,
        WorkflowArtifactBinding? manifest = null,
        ImmutableArray<ProtocolEffect> effects = default,
        ImmutableArray<ProtocolAuthority> authority = default) => new(
        effects.IsDefault
            ?
            [
                ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Refused,
                    ApplicationEffectScope.Workspace),
                ProtocolEffect.Create(
                    AgentEffectKind.WriteNewArtifact,
                    ApplicationEffectStatus.Refused,
                    ApplicationEffectScope.KLocalOutput)
            ]
            : effects,
        [WorkflowDiagnostic(exception, stage)],
        manifest is null ? [] : [ToProtocolArtifact(manifest)],
        authority.IsDefault ? RefusalAuthority() : authority,
        [],
        AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult,
        JsonSerializer.SerializeToElement(
            BuildResponse.Refused([]),
            JsonOptions));

    private static ProtocolDiagnostic WorkflowDiagnostic(
        AgentWorkflowCodecException exception,
        WorkflowRecoveryStage stage)
    {
        ProtocolFailureProjection projection =
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                exception.Code);
        string option = stage == WorkflowRecoveryStage.InputBundle
            ? exception.Code is "workflow-hash-invalid" or
                "workflow-hash-mismatch"
                ? "workflow-bundle-sha256"
                : "workflow-bundle"
            : "workflow-output";
        RecoveryAction action = projection.Class == DiagnosticClass.Operation
            ? RecoveryAction.RepairEnvironment
            : stage == WorkflowRecoveryStage.OutputBundle &&
              projection.Class == DiagnosticClass.Security
                ? RecoveryAction.ChooseFreshOutput
                : RecoveryAction.CorrectInput;
        return new ProtocolDiagnostic(
            projection.Code,
            DiagnosticSeverity.Error,
            $"{exception.Code}: {exception.Message}",
            projection.Class,
            new DiagnosticRecovery(
                action,
                option,
                "workflow-bundle",
                stage == WorkflowRecoveryStage.InputBundle
                    ? "Correct the exact workflow bundle path or SHA-256 and retry."
                    : "Repair workflow persistence or retry with a fresh K-local workflow output.",
                false));
    }

    private static ProtocolCommandResult RefusedAdmission(
        string message,
        BuildAdmissionTarget target,
        ImmutableArray<ProtocolEffect> effects)
    {
        return Refused(
            ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed,
            DiagnosticClass.Verification,
            message,
            effects: effects,
            recoveryOption: AdmissionOption(target, message),
            recoveryArtifactKind: AdmissionArtifactKind(target));
    }

    private static ProtocolCommandResult RefusedAdmissionCancellation(
        BuildAdmissionTarget target) => Refused(
        ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
        DiagnosticClass.Cancellation,
        "Reviewed NPC build admission was cancelled.",
        effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
        recoveryOption: AdmissionOption(target, string.Empty),
        recoveryArtifactKind: AdmissionArtifactKind(target),
        recoveryAction: RecoveryAction.RetryUnchanged,
        recoveryConstraint:
            "Retry the exact reviewed build admission unchanged.",
        retryUnchangedSafe: true);

    private static ProtocolCommandResult RefusedPostAdmissionCancellation(
        string message) => Refused(
        ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
        DiagnosticClass.Cancellation,
        message,
        effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
        recoveryOption: "request",
        recoveryArtifactKind: WorkflowArtifactKinds.NpcBuildPreflight,
        authority: PostAdmissionAuthority(),
        recoveryAction: RecoveryAction.ChooseFreshOutput,
        recoveryConstraint:
            "Prepare a fresh request output binding and reviewed preflight before retrying the build.");

    private static string AdmissionOption(
        BuildAdmissionTarget target,
        string message) => target switch
    {
        BuildAdmissionTarget.ReviewedIntake when message.Contains(
            "DataRoot", StringComparison.OrdinalIgnoreCase) => "data-root",
        BuildAdmissionTarget.ReviewedIntake when message.Contains(
            "plugin", StringComparison.OrdinalIgnoreCase) => "plugins",
        BuildAdmissionTarget.ReviewedIntake => "intake",
        BuildAdmissionTarget.ReviewedPreflight => "reviewed-preflight",
        BuildAdmissionTarget.SourceRequest => "request",
        _ => "workflow-bundle"
    };

    private static string AdmissionArtifactKind(BuildAdmissionTarget target) =>
        target switch
        {
            BuildAdmissionTarget.ReviewedIntake =>
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            BuildAdmissionTarget.ReviewedPreflight or
            BuildAdmissionTarget.SourceRequest =>
                WorkflowArtifactKinds.NpcBuildPreflight,
            _ => "workflow-bundle"
        };

    private static ProtocolCommandResult Refused(
        string code,
        DiagnosticClass diagnosticClass,
        string message,
        WorkflowArtifactBinding? manifest = null,
        ImmutableArray<Diagnostic> serviceDiagnostics = default,
        ImmutableArray<ProtocolEffect> effects = default,
        string? recoveryOption = null,
        string? recoveryArtifactKind = null,
        ImmutableArray<ProtocolAuthority> authority = default,
        RecoveryAction? recoveryAction = null,
        string? recoveryConstraint = null,
        bool retryUnchangedSafe = false) => new(
        effects.IsDefault
            ?
            [
                ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Refused,
                    ApplicationEffectScope.Workspace),
                ProtocolEffect.Create(
                    AgentEffectKind.WriteNewArtifact,
                    ApplicationEffectStatus.Refused,
                    ApplicationEffectScope.KLocalOutput)
            ]
            : effects,
        [new ProtocolDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            diagnosticClass,
            new DiagnosticRecovery(
                recoveryAction ?? (diagnosticClass switch
                {
                    DiagnosticClass.Operation =>
                        RecoveryAction.ChooseFreshOutput,
                    DiagnosticClass.Cancellation =>
                        RecoveryAction.RetryUnchanged,
                    _ => RecoveryAction.CorrectInput
                }),
                recoveryOption,
                recoveryArtifactKind ?? manifest?.Kind ??
                    WorkflowArtifactKinds.NpcBuildPreflight,
                recoveryConstraint ??
                    "Correct the exact reviewed build bindings and retry with a fresh workflow output.",
                retryUnchangedSafe))],
        manifest is null ? [] : [ToProtocolArtifact(manifest)],
        authority.IsDefault ? RefusalAuthority() : authority,
        [],
        AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult,
        JsonSerializer.SerializeToElement(
            BuildResponse.Refused(serviceDiagnostics),
            JsonOptions));

    private enum WorkflowRecoveryStage
    {
        InputBundle,
        OutputBundle
    }

    private enum BuildAdmissionTarget
    {
        WorkflowBundle,
        ReviewedIntake,
        ReviewedPreflight,
        SourceRequest
    }

    private sealed record BuildResponse(
        bool Completed,
        string Status,
        string? ManifestPath,
        long? ManifestSize,
        string? ManifestSha256,
        string? OutputPlugin,
        string? TargetFormId,
        bool RuntimeAuthority,
        ImmutableArray<BuildDiagnostic> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement?
            ExternalInstallPrepublication,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<string>? InheritedDefaults = null)
    {
        public static BuildResponse Success(
            string manifestPath,
            long manifestSize,
            string manifestSha256,
            string outputPlugin,
            string targetFormId,
            JsonElement? externalInstallPrepublication,
            ImmutableArray<string>? inheritedDefaults,
            ImmutableArray<Diagnostic> diagnostics) => new(
            true,
            "staticPassRuntimeRequired",
            manifestPath,
            manifestSize,
            manifestSha256,
            outputPlugin,
            targetFormId,
            false,
            diagnostics.Select(item => new BuildDiagnostic(item.Code, item.Severity.ToString().ToLowerInvariant(), item.Message)).ToImmutableArray(),
            externalInstallPrepublication,
            inheritedDefaults);

        public static BuildResponse Refused(
            ImmutableArray<Diagnostic> diagnostics) => new(
            false,
            "refused",
            null,
            null,
            null,
            null,
            null,
            false,
            diagnostics.IsDefaultOrEmpty
                ? []
                : diagnostics.Select(item => new BuildDiagnostic(
                    item.Code,
                    item.Severity.ToString().ToLowerInvariant(),
                    item.Message)).ToImmutableArray(),
            null);
    }

    private sealed record BuildDiagnostic(
        string Code,
        string Severity,
        string Message);
}
