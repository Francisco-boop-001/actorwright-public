using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2NpcVisualPreviewAdapter :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly WorkspacePath workspaceRoot;
    private readonly IWorkspacePolicy policy;
    private readonly FaceGeomHairRegionsDocumentCodec intakeCodec;
    private readonly IPackageVerifyService packageVerifier;
    private readonly INpcStaticBuildPackageAdmission packageAdmission;
    private readonly IPreviewServiceFactory<INpcVisualPreviewComposer>
        composerFactory;
    private readonly NpcVisualPreviewArtifactReader reader;
    private readonly AgentWorkflowBundleTransitionService workflowLifecycle;

    internal ProtocolV2NpcVisualPreviewAdapter(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        FaceGeomHairRegionsDocumentCodec intakeCodec,
        IPackageVerifyService packageVerifier,
        PackageManifestReader packageManifestReader,
        IPreviewServiceFactory<INpcVisualPreviewComposer> composerFactory,
        NpcVisualPreviewArtifactReader reader,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        INpcStaticBuildPackageAdmission? packageAdmission = null)
    {
        this.workspaceRoot = workspaceRoot;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.intakeCodec = intakeCodec ??
            throw new ArgumentNullException(nameof(intakeCodec));
        this.packageVerifier = packageVerifier ??
            throw new ArgumentNullException(nameof(packageVerifier));
        ArgumentNullException.ThrowIfNull(packageManifestReader);
        this.packageAdmission = packageAdmission ??
            new NpcStaticBuildPackageAdmission(
                workspaceRoot,
                packageManifestReader);
        this.composerFactory = composerFactory ??
            throw new ArgumentNullException(nameof(composerFactory));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.workflowLifecycle = workflowLifecycle ??
            throw new ArgumentNullException(nameof(workflowLifecycle));
    }

    public ImmutableArray<string> Commands { get; } = ["preview npc"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        NpcVisualPreviewCommandBindingResult bindingResult =
            NpcVisualPreviewCommandBinder.Bind(command, strict: true);
        if (!bindingResult.IsValid)
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewValidationFailed,
                DiagnosticClass.Validation,
                bindingResult.ErrorMessage ?? "Preview inputs are invalid.");
        NpcVisualPreviewCommandBinding binding = bindingResult.Binding!;

        ProtocolCommandResult? outputOverlap = RefuseOutputPairOverlap(
            binding.OutputRoot,
            binding.WorkflowOutput!.Value);
        if (outputOverlap is not null)
            return outputOverlap;

        AgentWorkflowBundleTransition inputWorkflow;
        WorkflowArtifactBinding intakeBinding;
        WorkflowArtifactBinding packageBinding;
        ReviewedGameIntakeDocumentAuthority admittedIntake;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<Diagnostic> boundary =
                policy.EvaluateReadRoot(workspaceRoot, binding.IntakePath)
                    .AddRange(policy.Evaluate(
                        workspaceRoot,
                        binding.OutputRoot));
            if (boundary.Any(item =>
                    item.Severity == DiagnosticSeverity.Error))
                throw new UnauthorizedAccessException(string.Join(
                    " | ",
                    boundary.Select(item => $"{item.Code}: {item.Message}")));
            string? outputParent = Path.GetDirectoryName(
                binding.OutputRoot.Value);
            if (string.IsNullOrWhiteSpace(outputParent) ||
                !Directory.Exists(outputParent) ||
                Directory.Exists(binding.OutputRoot.Value) ||
                File.Exists(binding.OutputRoot.Value))
                throw new InvalidDataException(
                    "The preview output must be fresh beneath an existing ordinary parent.");
            workflowLifecycle.AdmitFreshOutput(
                binding.WorkflowOutput!.Value,
                binding.WorkflowInput!.Value,
                binding.IntakePath,
                binding.PackageOverlay!.ManifestPath,
                binding.OutputRoot);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                DiagnosticClass.Cancellation,
                "NPC preview was cancelled before rendering.");
        }
        catch (AgentWorkflowCodecException exception)
        {
            return RefusedWorkflow(
                exception,
                WorkflowRecoveryStage.OutputBundle);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return RefusedException(exception);
        }

        try
        {
            inputWorkflow = workflowLifecycle.LoadForCommand(
                binding.WorkflowInput.Value,
                binding.WorkflowInputSha256!,
                "preview npc");
        }
        catch (OperationCanceledException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                DiagnosticClass.Cancellation,
                "NPC preview was cancelled before rendering.");
        }
        catch (AgentWorkflowCodecException exception)
        {
            return RefusedWorkflow(
                exception,
                WorkflowRecoveryStage.InputBundle);
        }

        try
        {
            intakeBinding = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => item.Kind ==
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake);
            packageBinding = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => item.Kind == WorkflowArtifactKinds.NpcPackageManifest);
            RequirePackageOption(packageBinding, binding.PackageOverlay);
            RequireNpcIdentity(
                inputWorkflow.Document.Bundle.Npc,
                binding.Plugin,
                binding.FormId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                DiagnosticClass.Verification,
                exception is AgentWorkflowCodecException workflow
                    ? $"{workflow.Code}: {workflow.Message}"
                    : exception.Message,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }

        try
        {
            admittedIntake =
                await intakeCodec.LoadReviewedIntakeAsync(
                    binding.IntakePath,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                DiagnosticClass.Cancellation,
                "NPC preview intake admission was cancelled.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return RefusedWorkflow(new AgentWorkflowCodecException(
                "workflow-artifact-binding-mismatch",
                "The retained reviewed-intake artifact could not be reopened exactly.",
                exception),
                WorkflowRecoveryStage.ReviewedIntake,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }

        string admittedIntakeSha256 = admittedIntake.Document.Sha256.Value
            .ToUpperInvariant();
        if (!string.Equals(
                intakeBinding.Path.Value,
                binding.IntakePath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                intakeBinding.Sha256,
                admittedIntakeSha256,
                StringComparison.Ordinal) ||
            intakeBinding.Size != admittedIntake.Document.ByteLength ||
            !packageBinding.InputArtifactHashes.Contains(
                intakeBinding.Sha256,
                StringComparer.Ordinal))
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                DiagnosticClass.Verification,
                "The reviewed intake does not match the exact intake retained by the package workflow lineage.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());

        PackageVerifyResult verification;
        try
        {
            verification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(binding.PackageOverlay!.ManifestPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                DiagnosticClass.Cancellation,
                "NPC preview package verification was cancelled.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                DiagnosticClass.Verification,
                exception.Message,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }
        if (!verification.Verified || verification.Artifact is not
            { RuntimeProof: false } verified)
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                DiagnosticClass.Verification,
                verification.Diagnostics.IsDefaultOrEmpty
                    ? "The preview package failed independent verification."
                    : string.Join(" | ", verification.Diagnostics.Select(
                        item => $"{item.Code}: {item.Message}")),
                serviceDiagnostics: verification.Diagnostics,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());

        NpcStaticBuildPackageLease packageLease;
        try
        {
            packageLease = await packageAdmission.AdmitPreviewAsync(
                verified,
                packageBinding,
                inputWorkflow.Document.Bundle.Npc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refused(
                ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                DiagnosticClass.Cancellation,
                "NPC preview package admission was cancelled.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                recoveryAction: RecoveryAction.RetryUnchanged,
                recoveryOption: "package-manifest",
                recoveryConstraint:
                    "Retry the exact verified package admission unchanged.",
                recoveryArtifactKind:
                    WorkflowArtifactKinds.NpcPackageManifest,
                retryUnchangedSafe: true);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Refused(
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                DiagnosticClass.Verification,
                exception.Message,
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused());
        }

        using var terminalLease = new ProtocolV2TerminalArtifactLeaseScope();
        terminalLease.Own(packageLease);
            WorkspacePath packageRoot = new(
                Path.GetDirectoryName(packageLease.Identity.ManifestPath.Value) ??
                throw new InvalidDataException(
                    "The verified package manifest has no package root."));
            ProtocolCommandResult? overlapRefusal = RefuseProtectedOutputOverlap(
                binding.OutputRoot,
                binding.WorkflowOutput!.Value,
                packageRoot,
                admittedIntake.Value.DataRoot);
            if (overlapRefusal is not null)
                return overlapRefusal;

            NpcVisualPreviewComposeResult composed;
            try
            {
                await using PreviewServiceLease<INpcVisualPreviewComposer>
                    lease = await composerFactory.CreateAsync(
                        cancellationToken).ConfigureAwait(false);
                composed = await lease.Service.ComposeAsync(
                    new NpcVisualPreviewComposeRequest(
                        admittedIntake.Value,
                        new SkyrimMainWorkspaceIdentity(
                            binding.Plugin,
                            binding.Plugin,
                            binding.FormId,
                            "NPC_"),
                        binding.PackageOverlay,
                        new NpcVisualPreviewOptions(),
                        binding.OutputRoot),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    DiagnosticClass.Cancellation,
                    "NPC preview rendering was cancelled.",
                    binding.OutputRoot);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.NpcPreviewOperationFailed,
                    DiagnosticClass.Operation,
                    exception.Message,
                    binding.OutputRoot);
            }
            if (!composed.Composed || composed.Bundle is not { } bundle)
            {
                string failure = composed.Diagnostics.IsDefaultOrEmpty
                    ? "The off-engine preview was not composed."
                    : string.Join(" | ", composed.Diagnostics.Select(
                        item => $"{item.Code}: {item.Message}"));
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.NpcPreviewOperationFailed,
                    DiagnosticClass.Operation,
                    failure,
                    binding.OutputRoot,
                    composed.Diagnostics);
            }

            NpcVisualPreviewArtifactDocument? preview = null;
            try
            {
                preview = reader.Load(
                    bundle.BundlePath,
                    bundle.BundleSha256.Value.ToUpperInvariant(),
                    cancellationToken);
                terminalLease.Own(preview);
                RequirePreviewBinding(
                    preview,
                    bundle,
                    packageLease.Identity,
                    binding);
            }
            catch (OperationCanceledException)
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    DiagnosticClass.Cancellation,
                    "NPC preview verification was cancelled.",
                    binding.OutputRoot,
                    composed.Diagnostics);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                    DiagnosticClass.Verification,
                    exception.Message,
                    binding.OutputRoot,
                    composed.Diagnostics);
            }

            try
            {
                preview.Revalidate();
            }
            catch (OperationCanceledException)
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    DiagnosticClass.Cancellation,
                    "NPC preview revalidation was cancelled.",
                    binding.OutputRoot,
                    composed.Diagnostics);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                return RefusedAfterComposition(
                    ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                    DiagnosticClass.Verification,
                    exception.Message,
                    binding.OutputRoot,
                    composed.Diagnostics);
            }

                NpcVisualPreviewPhysicalArtifact hashManifest =
                    preview.Artifacts.Single(item => string.Equals(
                        Path.GetFileName(item.Path.Value),
                        "npc-preview.hashes.sha256",
                        StringComparison.OrdinalIgnoreCase));
                var previewBinding = new WorkflowArtifactBinding(
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    NpcVisualPreviewPersistenceContract.BundleSchema,
                    preview.Path,
                    preview.Size,
                    preview.Sha256,
                    "preview npc",
                    requestDigest,
                    new[]
                    {
                        intakeBinding.Sha256,
                        packageBinding.Sha256
                    }
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray());
                AgentWorkflowBundleTransition outputWorkflow;
                try
                {
                    AgentWorkflowBundleTransitionLease retainedWorkflow =
                        workflowLifecycle.AdvanceRetained(
                        inputWorkflow,
                        inputWorkflow.Document.Bundle.Npc,
                        requestDigest,
                        [packageBinding, previewBinding],
                        binding.WorkflowOutput.Value);
                    terminalLease.Own(retainedWorkflow);
                    outputWorkflow = retainedWorkflow.Transition;
                }
                catch (AgentWorkflowCodecException exception)
                {
                    return RefusedWorkflow(
                        exception,
                        WorkflowRecoveryStage.OutputBundle,
                        previewBinding,
                        ProtocolV2CommitEffects
                            .PublishedArtifactWorkflowFailed(),
                        SuccessAuthority()) with
                    {
                        TerminalArtifactLease = terminalLease.Transfer()
                    };
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
                    [],
                    [
                        ToProtocolArtifact(previewBinding),
                        ProtocolV2WorkflowBundleProjection.Artifact(
                            outputWorkflow.Document,
                            "preview npc",
                            requestDigest)
                    ],
                    SuccessAuthority(),
                    ProtocolV2WorkflowBundleProjection.NextActions(
                        outputWorkflow),
                    AgentProtocolSchemaIds.NpcVisualPreviewResult,
                    JsonSerializer.SerializeToElement(
                        PreviewResponse.Success(
                            preview,
                            hashManifest,
                            composed.Diagnostics),
                        JsonOptions))
                {
                    TerminalArtifactLease = terminalLease.Transfer()
                };
    }

    private static void RequirePackageOption(
        WorkflowArtifactBinding package,
        NpcVisualPreviewPackageOverlay overlay)
    {
        if (!string.Equals(
                package.Path.Value,
                overlay.ManifestPath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                package.Sha256,
                overlay.ExpectedManifestSha256.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The preview package options differ from the workflow binding.");
    }

    private static void RequireNpcIdentity(
        WorkflowNpcIdentity npc,
        PluginName plugin,
        FormId formId)
    {
        if (!string.Equals(
                npc.Plugin,
                plugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                npc.LocalFormId,
                formId.Value.ToString("X8", CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The preview NPC identity differs from the workflow binding.");
    }

    private static void RequirePreviewBinding(
        NpcVisualPreviewArtifactDocument preview,
        NpcVisualPreviewBundle bundle,
        PackageManifestIdentity package,
        NpcVisualPreviewCommandBinding binding)
    {
        ProtocolV2PreviewBindingAdmission.RequireExactPhysical(
            preview,
            new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPreviewManifest,
                NpcVisualPreviewPersistenceContract.BundleSchema,
                bundle.BundlePath,
                preview.Size,
                bundle.BundleSha256.Value.ToUpperInvariant(),
                "preview npc",
                new string('0', 64),
                []));
        NpcVisualPreviewPersistenceDocument value = preview.Value;
        if (!string.Equals(
                preview.Path.Value,
                bundle.BundlePath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                preview.Sha256,
                bundle.BundleSha256.Value,
                StringComparison.OrdinalIgnoreCase) ||
            value.Source.Identity.OwnerPlugin != binding.Plugin ||
            value.Source.Identity.WinningProvider != binding.Plugin ||
            value.Source.Identity.FormId != binding.FormId ||
            !string.Equals(
                package.OutputPlugin,
                binding.Plugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            package.TargetFormId != binding.FormId)
            throw new InvalidDataException(
                "The reopened preview differs from its package/NPC binding.");

        foreach ((NpcVisualAssetRole role, string kind) in new[]
                 {
                     (NpcVisualAssetRole.FaceGeom, "facegeom"),
                     (NpcVisualAssetRole.FaceTint, "facetint")
                 })
        {
            PackageManifestFile packageFile = package.Files.Single(item =>
                string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));
            NpcVisualAsset source = value.Source.Assets.Single(item =>
                item.Role == role);
            string expectedPath = Path.GetFullPath(Path.Combine(
                binding.OutputRoot.Value,
                "assets",
                packageFile.RelativePath.Value.Replace(
                    '/', Path.DirectorySeparatorChar)));
            string expectedAssetPath = packageFile.RelativePath.Value["Data/".Length..];
            if (!string.Equals(
                    source.MaterializedPath.Value,
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    source.AssetPath.Value,
                    expectedAssetPath,
                    StringComparison.OrdinalIgnoreCase) ||
                source.Bytes != packageFile.ByteLength ||
                source.Sha256 != packageFile.Sha256)
                throw new InvalidDataException(
                    $"The preview {kind} source is not the exact package artifact.");
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is AgentWorkflowCodecException or ArgumentException or
            InvalidDataException or IOException or UnauthorizedAccessException or
            InvalidOperationException or KeyNotFoundException or JsonException;

    private static ProtocolCommandResult? RefuseProtectedOutputOverlap(
        WorkspacePath outputRoot,
        WorkspacePath workflowOutput,
        WorkspacePath packageRoot,
        WorkspacePath dataRoot)
    {
        if (Overlaps(outputRoot, packageRoot) ||
            Overlaps(outputRoot, dataRoot))
            return Refused(
                ProtocolV2DiagnosticCodes.ProtectedRootRefused,
                DiagnosticClass.Security,
                "The preview output must not overlap the verified package root or admitted intake DataRoot.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                recoveryAction: RecoveryAction.ChooseFreshOutput,
                recoveryOption: "output-root",
                recoveryConstraint:
                    "Choose a fresh preview output outside the verified package and admitted intake DataRoot.",
                recoveryArtifactKind: WorkflowArtifactKinds.NpcPreviewManifest,
                authority: VerifiedInputAuthority());
        if (Overlaps(workflowOutput, packageRoot) ||
            Overlaps(workflowOutput, dataRoot))
            return Refused(
                ProtocolV2DiagnosticCodes.ProtectedRootRefused,
                DiagnosticClass.Security,
                "The workflow output must not overlap the verified package root or admitted intake DataRoot.",
                effects: ProtocolV2CommitEffects.ReadCompletedWriteRefused(),
                recoveryAction: RecoveryAction.ChooseFreshOutput,
                recoveryOption: "workflow-output",
                recoveryConstraint:
                    "Choose a fresh workflow output outside the verified package and admitted intake DataRoot.",
                recoveryArtifactKind: "workflow-bundle",
                authority: VerifiedInputAuthority());
        return null;
    }

    private static ProtocolCommandResult? RefuseOutputPairOverlap(
        WorkspacePath outputRoot,
        WorkspacePath workflowOutput)
    {
        if (!Overlaps(outputRoot, workflowOutput))
            return null;
        return Refused(
            ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
            DiagnosticClass.Security,
            "The preview output and workflow output must not overlap.",
            recoveryAction: RecoveryAction.ChooseFreshOutput,
            recoveryOption: "workflow-output",
            recoveryConstraint:
                "Choose a fresh workflow output outside the preview output root.",
            recoveryArtifactKind: "workflow-bundle");
    }

    private static bool Overlaps(WorkspacePath left, WorkspacePath right) =>
        left.IsUnder(right) || right.IsUnder(left);

    private static ProtocolCommandResult RefusedAfterComposition(
        string code,
        DiagnosticClass diagnosticClass,
        string primary,
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic> serviceDiagnostics = default)
    {
        bool outputExists = Directory.Exists(outputRoot.Value) ||
            File.Exists(outputRoot.Value);
        string message = outputExists
            ? $"{primary} | The preview output is unverified and was preserved because directory ownership cannot be proven. Retry with a fresh --output-root."
            : $"{primary} | Preview composition began, so unseen partial output cannot be ruled out. Retry with a fresh --output-root.";
        return Refused(
            code,
            diagnosticClass,
            message,
            serviceDiagnostics: serviceDiagnostics,
            effects: ProtocolV2CommitEffects.ReadCompletedWriteFailed(),
            recoveryAction: RecoveryAction.ChooseFreshOutput,
            recoveryOption: "output-root",
            recoveryConstraint:
                "Preview composition began; preserve any unverified output and retry with a fresh --output-root.",
            recoveryArtifactKind: WorkflowArtifactKinds.NpcPreviewManifest,
            authority: VerifiedInputAuthority());
    }

    private static ProtocolCommandResult RefusedException(
        Exception exception,
        WorkflowArtifactBinding? preview = null,
        ImmutableArray<ProtocolEffect> effects = default)
    {
        bool path = exception is UnauthorizedAccessException ||
            exception.Message.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("outside", StringComparison.OrdinalIgnoreCase);
        return Refused(
            path
                ? ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused
                : ProtocolV2DiagnosticCodes.NpcPreviewValidationFailed,
            path ? DiagnosticClass.Security : DiagnosticClass.Validation,
            exception is AgentWorkflowCodecException workflow
                ? $"{workflow.Code}: {workflow.Message}"
                : exception.Message,
            preview,
            effects: effects);
    }

    private static ProtocolCommandResult RefusedWorkflow(
        AgentWorkflowCodecException exception,
        WorkflowRecoveryStage stage,
        WorkflowArtifactBinding? preview = null,
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
        preview is null ? [] : [ToProtocolArtifact(preview)],
        authority.IsDefault ? RefusalAuthority() : authority,
        [],
        AgentProtocolSchemaIds.NpcVisualPreviewResult,
        JsonSerializer.SerializeToElement(
            PreviewResponse.Refused([]),
            JsonOptions));

    private static ProtocolDiagnostic WorkflowDiagnostic(
        AgentWorkflowCodecException exception,
        WorkflowRecoveryStage stage)
    {
        ProtocolFailureProjection projection =
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                exception.Code);
        string option = stage switch
        {
            WorkflowRecoveryStage.InputBundle when exception.Code is
                "workflow-hash-invalid" or "workflow-hash-mismatch" =>
                    "workflow-bundle-sha256",
            WorkflowRecoveryStage.InputBundle => "workflow-bundle",
            WorkflowRecoveryStage.ReviewedIntake => "intake",
            _ => "workflow-output"
        };
        string artifactKind = stage == WorkflowRecoveryStage.ReviewedIntake
            ? WorkflowArtifactKinds.ReviewedWorkspaceIntake
            : "workflow-bundle";
        RecoveryAction action = projection.Class == DiagnosticClass.Operation
            ? RecoveryAction.RepairEnvironment
            : stage == WorkflowRecoveryStage.OutputBundle &&
              projection.Class == DiagnosticClass.Security
                ? RecoveryAction.ChooseFreshOutput
                : RecoveryAction.CorrectInput;
        string constraint = stage switch
        {
            WorkflowRecoveryStage.InputBundle =>
                "Correct the exact workflow bundle path or SHA-256 and retry.",
            WorkflowRecoveryStage.ReviewedIntake =>
                "Correct the retained reviewed intake binding and retry.",
            _ =>
                "Repair workflow persistence or retry with a fresh K-local workflow output."
        };
        return new ProtocolDiagnostic(
            projection.Code,
            DiagnosticSeverity.Error,
            $"{exception.Code}: {exception.Message}",
            projection.Class,
            new DiagnosticRecovery(
                action,
                option,
                artifactKind,
                constraint,
                false));
    }

    private static ProtocolCommandResult Refused(
        string code,
        DiagnosticClass diagnosticClass,
        string message,
        WorkflowArtifactBinding? preview = null,
        ImmutableArray<Diagnostic> serviceDiagnostics = default,
        ImmutableArray<ProtocolEffect> effects = default,
        RecoveryAction? recoveryAction = null,
        string? recoveryOption = null,
        string? recoveryConstraint = null,
        string? recoveryArtifactKind = null,
        ImmutableArray<ProtocolAuthority> authority = default,
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
                recoveryAction ??
                    (diagnosticClass == DiagnosticClass.Operation
                        ? RecoveryAction.RepairEnvironment
                        : diagnosticClass == DiagnosticClass.Cancellation
                            ? RecoveryAction.RetryUnchanged
                            : RecoveryAction.CorrectInput),
                recoveryOption,
                recoveryArtifactKind ?? WorkflowArtifactKinds.NpcPreviewManifest,
                recoveryConstraint ??
                    "Correct the exact preview bindings or renderer environment and retry with fresh outputs.",
                retryUnchangedSafe))],
        preview is null ? [] : [ToProtocolArtifact(preview)],
        authority.IsDefault ? RefusalAuthority() : authority,
        [],
        AgentProtocolSchemaIds.NpcVisualPreviewResult,
        JsonSerializer.SerializeToElement(
            PreviewResponse.Refused(serviceDiagnostics),
            JsonOptions));

    private enum WorkflowRecoveryStage
    {
        InputBundle,
        ReviewedIntake,
        OutputBundle
    }

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
            "The exact package, intake, workflow, and NPC identity were admitted."),
        Authority(AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The preview source graph is bound to the package FaceGeom and FaceTint."),
        Authority(AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The exact static NPC package remains materialized."),
        Authority(AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "Every package and preview artifact is retained and independently reopened."),
        Authority(AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.Established,
            "Six deterministic off-engine views and evidence were persisted."),
        Authority(AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.Required,
            "An LLM may inspect evidence but cannot attest human acceptance."),
        Authority(AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Skyrim runtime testing remains user-operated."),
        Authority(AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.Required,
            "Preview production never grants promotion approval.")
    ];

    private static ImmutableArray<ProtocolAuthority> RefusalAuthority() =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.HumanVisualAcceptance or
            AgentAuthorityKind.GameRuntimeVerification or
            AgentAuthorityKind.PromotionApproval => authority,
            AgentAuthorityKind.InputAdmission => authority with
            {
                State = AgentAuthorityState.Blocked,
                Reason = "Preview input admission was blocked."
            },
            _ => authority with
            {
                State = AgentAuthorityState.Required,
                Reason = "This authority remains required after preview refusal."
            }
        }).ToImmutableArray();

    private static ImmutableArray<ProtocolAuthority> VerifiedInputAuthority() =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.InputAdmission or
            AgentAuthorityKind.SourceProviderIdentity or
            AgentAuthorityKind.DeterministicMaterialization => authority,
            AgentAuthorityKind.IndependentStaticVerification => authority with
            {
                State = AgentAuthorityState.Established,
                Reason = "The exact static package was independently verified before preview composition."
            },
            AgentAuthorityKind.OffEnginePreview => authority with
            {
                State = AgentAuthorityState.Required,
                Reason = "The off-engine preview was not independently verified."
            },
            _ => authority
        }).ToImmutableArray();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private sealed record PreviewResponse(
        bool Composed,
        string Status,
        bool RuntimeAuthority,
        string? BundlePath,
        long? BundleSize,
        string? BundleSha256,
        string? HashManifestPath,
        long? HashManifestSize,
        string? HashManifestSha256,
        string? ContactSheetPath,
        string? ContactSheetSha256,
        ImmutableArray<PreviewView> Views,
        ImmutableArray<PreviewDiagnostic> Diagnostics)
    {
        public static PreviewResponse Success(
            NpcVisualPreviewArtifactDocument document,
            NpcVisualPreviewPhysicalArtifact hashManifest,
            ImmutableArray<Diagnostic> diagnostics) => new(
            true,
            "offEnginePreviewRuntimeRequired",
            false,
            document.Path.Value,
            document.Size,
            document.Sha256,
            hashManifest.Path.Value,
            hashManifest.Size,
            hashManifest.Sha256,
            document.Value.ContactSheetPath.Value,
            document.Value.ContactSheetSha256.Value.ToUpperInvariant(),
            document.Value.Views.Select(item => new PreviewView(
                item.Id,
                item.ImagePath.Value,
                item.ImageSha256.Value.ToUpperInvariant(),
                item.RoleMaskPath.Value,
                item.RoleMaskSha256.Value.ToUpperInvariant(),
                item.Width,
                item.Height)).ToImmutableArray(),
            Project(diagnostics));

        public static PreviewResponse Refused(
            ImmutableArray<Diagnostic> diagnostics) => new(
            false,
            "refused",
            false,
            null, null, null, null, null, null, null, null,
            [],
            Project(diagnostics));

        private static ImmutableArray<PreviewDiagnostic> Project(
            ImmutableArray<Diagnostic> diagnostics) =>
            diagnostics.IsDefaultOrEmpty
                ? []
                : diagnostics.Select(item => new PreviewDiagnostic(
                    item.Code,
                    item.Severity.ToString().ToLowerInvariant(),
                    item.Message)).ToImmutableArray();
    }

    private sealed record PreviewView(
        string Id,
        string ImagePath,
        string ImageSha256,
        string RoleMaskPath,
        string RoleMaskSha256,
        int Width,
        int Height);

    private sealed record PreviewDiagnostic(
        string Code,
        string Severity,
        string Message);
}
