using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2FinishVerifyAdapter(
    WorkspacePath workspaceRoot,
    ISkyrimNpcFinishCoreService service,
    SkyrimNpcFinishCoreVerificationArtifactStore artifactStore,
    AgentWorkflowBundleTransitionService? workflowLifecycle = null,
    AgentReviewReceiptService? reviewReceipts = null) :
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

    public ImmutableArray<string> Commands { get; } = ["npc finish verify"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!TryBind(command, out WorkspacePath manifest,
                out string manifestSha256, out WorkspacePath output,
                out WorkspacePath workflowInput,
                out string workflowInputSha256,
                out WorkspacePath workflowOutput,
                out ExternalHeadPartInstallContextBindingResult installContextBinding,
                out string? failure))
            return Refused(
                manifest,
                manifestSha256,
                [ReadEffect(ApplicationEffectStatus.Refused)],
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionRequired,
                    failure!,
                    DiagnosticClass.Usage)],
                BindingFailureAuthority());
        ArgumentNullException.ThrowIfNull(workflowLifecycle);

        AgentWorkflowBundleTransition inputWorkflow;
        WorkflowArtifactBinding manifestBinding;
        try
        {
            workflowLifecycle.AdmitFreshOutput(
                workflowOutput,
                workflowInput,
                manifest,
                output);
            inputWorkflow = workflowLifecycle.LoadForFinishCommand(
                workflowInput,
                workflowInputSha256,
                "npc finish verify", reviewReceipts is null ? null : document =>
                {
                    WorkflowArtifactBinding receipt = document.Bundle.Artifacts.Single(item => item.Kind == WorkflowArtifactKinds.ReviewReceipt);
                    return reviewReceipts.LoadForSuccessor(document, receipt.Path, receipt.Sha256);
                });
            manifestBinding = inputWorkflow.Document.Bundle.Artifacts.Single(
                item => string.Equals(
                    item.Kind,
                    WorkflowArtifactKinds.NpcFinishCoreManifest,
                    StringComparison.Ordinal));
            if (!string.Equals(
                    manifestBinding.Path.Value,
                    manifest.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    manifestBinding.Sha256,
                    manifestSha256,
                    StringComparison.Ordinal))
                throw new AgentWorkflowCodecException(
                    "workflow-artifact-binding-mismatch",
                    "The Finish manifest options do not match the workflow bundle.");
        }
        catch (Exception exception) when (
            exception is AgentWorkflowCodecException or AgentReviewReceiptException or InvalidOperationException)
        {
            AgentWorkflowCodecException normalized = exception as
                AgentWorkflowCodecException ??
                new AgentWorkflowCodecException(
                    "workflow-transition-invalid",
                    "The Finish workflow shape is invalid: " + exception.Message,
                    exception);
            return Refused(
                manifest,
                manifestSha256,
                [ReadEffect(ApplicationEffectStatus.Refused)],
                [WorkflowDiagnostic(normalized)],
                BindingFailureAuthority());
        }

        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest;
        try
        {
            verifiedManifest = artifactStore.LoadManifest(
                manifest, manifestSha256);
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException exception)
        {
            (string code, DiagnosticClass diagnosticClass) =
                ClassifyArtifactFailure(exception.Code);
            return Refused(
                manifest,
                manifestSha256,
                [ReadEffect(ApplicationEffectStatus.Refused)],
                [Diagnostic(
                    code,
                    $"{exception.Code}: {exception.Message}",
                    diagnosticClass)],
                BindingFailureAuthority());
        }

        SkyrimNpcFinishCoreVerificationResult verification =
            await VerifyAsync(
                installContextBinding,
                verifiedManifest,
                manifest,
                manifestSha256,
                cancellationToken);
        if (verification.Verification is null &&
            verifiedManifest.Manifest.Schema ==
                SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier)
            verification = ProjectExternalFailure(
                verifiedManifest.Manifest,
                installContextBinding.IsSpecified,
                verification.Diagnostics);
        ImmutableArray<ProtocolDiagnostic> diagnostics = verification.Diagnostics
            .Select(ToProtocolDiagnostic)
            .ToImmutableArray();

        ExternalHeadPartInstallVerificationArtifact? externalArtifact =
            verification.Verification?.ExternalHeadParts?.Verification;
        if (externalArtifact is not null &&
            !HasCurrentExternalInstallAuthority(externalArtifact))
        {
            string failureCode = ExternalFailureCode(
                externalArtifact,
                installContextBinding.IsSpecified,
                verification.Diagnostics);
            if (!diagnostics.Any(item => item.Code ==
                    ProtocolV2DiagnosticCodes.FinishVerificationFailed))
                diagnostics = diagnostics.Add(Diagnostic(
                    ProtocolV2DiagnosticCodes.FinishVerificationFailed,
                    $"{failureCode}: {ExternalFailureMessage(failureCode)}",
                    DiagnosticClass.Verification));
            return Refused(
                manifest,
                manifestSha256,
                [ReadEffect(ApplicationEffectStatus.Completed)],
                diagnostics,
                ExternalVerificationFailureAuthority(),
                verification.Verification,
                ExternalInstallAction(
                    manifest,
                    manifestSha256,
                    externalArtifact,
                    installContextBinding.IsSpecified,
                    failureCode),
                SkyrimNpcFinishCoreStatus.Refused);
        }
        if (!verification.Verified || verification.Verification is null)
            return Refused(
                manifest,
                manifestSha256,
                [ReadEffect(ApplicationEffectStatus.Completed)],
                diagnostics.IsEmpty
                    ? [Diagnostic(
                        ProtocolV2DiagnosticCodes.FinishVerificationFailed,
                        "Finish Verify did not establish independent static verification.",
                        DiagnosticClass.Verification)]
                    : diagnostics,
                VerificationFailureAuthority());

        SkyrimNpcFinishCoreVerificationArtifactDocument persisted;
        try
        {
            persisted = await artifactStore.WriteNewAsync(
                verification.Verification,
                verifiedManifest,
                requestDigest,
                output,
                cancellationToken);
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException exception)
        {
            (string code, DiagnosticClass diagnosticClass) =
                ClassifyArtifactFailure(exception.Code);
            return Refused(
                manifest,
                manifestSha256,
                [
                    ReadEffect(ApplicationEffectStatus.Completed),
                    WriteEffect(ApplicationEffectStatus.Refused)
                ],
                diagnostics.Add(Diagnostic(
                    code,
                    $"{exception.Code}: {exception.Message}",
                    diagnosticClass)),
                VerificationFailureAuthority());
        }

        AgentWorkflowBundleTransition outputWorkflow;
        try
        {
            WorkflowArtifactBinding archive = new(
                WorkflowArtifactKinds.PackageArchive,
                "application/zip",
                verifiedManifest.ArchivePath,
                verifiedManifest.ArchiveSize,
                verifiedManifest.ArchiveSha256,
                manifestBinding.ProducerCommand,
                manifestBinding.RequestDigest,
                [manifestBinding.Sha256]);
            outputWorkflow = workflowLifecycle.Advance(
                inputWorkflow,
                inputWorkflow.Document.Bundle.Npc,
                requestDigest,
                [persisted.Artifact, archive,
                    .. inputWorkflow.Document.Bundle.Phase == AgentWorkflowPhase.ReviewRequired
                        ? inputWorkflow.Document.Bundle.Artifacts.Where(item => item.Kind != WorkflowArtifactKinds.NpcFinishCoreManifest)
                        : []],
                workflowOutput);
        }
        catch (AgentWorkflowCodecException exception)
        {
            return new ProtocolCommandResult(
                [
                    ReadEffect(ApplicationEffectStatus.Completed),
                    WriteEffect(ApplicationEffectStatus.Refused)
                ],
                diagnostics.Add(WorkflowDiagnostic(exception)),
                [VerificationArtifact(persisted, requestDigest)],
                VerificationFailureAuthority(),
                [],
                AgentProtocolSchemaIds.FinishVerifyResult,
                Result(
                    true,
                    verification.Verification.Status,
                    manifest,
                    manifestSha256,
                    persisted,
                    verification.Verification,
                    diagnostics));
        }

        return new ProtocolCommandResult(
            [
                ReadEffect(ApplicationEffectStatus.Completed),
                WriteEffect(ApplicationEffectStatus.Completed)
            ],
            diagnostics,
            [
                VerificationArtifact(persisted, requestDigest),
                ProtocolV2WorkflowBundleProjection.Artifact(
                    outputWorkflow.Document,
                    "npc finish verify",
                    requestDigest)
            ],
            SuccessAuthority(),
            ProtocolV2WorkflowBundleProjection.NextActions(outputWorkflow),
            AgentProtocolSchemaIds.FinishVerifyResult,
            Result(
                true,
                verification.Verification.Status,
                manifest,
                manifestSha256,
                persisted,
                verification.Verification,
                diagnostics));
    }

    private bool TryBind(
        ParsedCommand command,
        out WorkspacePath manifest,
        out string manifestSha256,
        out WorkspacePath output,
        out WorkspacePath workflowInput,
        out string workflowInputSha256,
        out WorkspacePath workflowOutput,
        out ExternalHeadPartInstallContextBindingResult installContextBinding,
        out string? failure)
    {
        manifest = workspaceRoot;
        output = workspaceRoot;
        workflowInput = workspaceRoot;
        workflowOutput = workspaceRoot;
        installContextBinding = new(false, null, [], null);
        manifestSha256 = string.Empty;
        workflowInputSha256 = string.Empty;
        failure = null;
        SkyrimNpcFinishVerifyCommandBindingResult bound;
        try
        {
            bound = SkyrimNpcFinishVerifyCommandBinder.Bind(
                command, protocolV2: true);
        }
        catch (ArgumentException exception)
        {
            failure = exception.Message;
            return false;
        }
        if (!bound.IsValid)
        {
            failure = bound.ErrorMessage;
            return false;
        }
        try
        {
            installContextBinding = ExternalHeadPartInstallContextBinder.Bind(command);
        }
        catch (ArgumentException exception)
        {
            failure = exception.Message;
            return false;
        }
        if (!installContextBinding.IsValid)
        {
            failure = installContextBinding.ErrorMessage;
            return false;
        }
        manifest = bound.Binding!.Manifest;
        manifestSha256 = bound.Binding.ManifestSha256Wire;
        output = bound.Binding.VerificationOutput!.Value;
        if (!command.Options.TryGetValue(
                "workflow-bundle",
                out string? workflowInputValue) ||
            !command.Options.TryGetValue(
                "workflow-bundle-sha256",
                out string? workflowInputSha256Value) ||
            !IsUpperSha256(workflowInputSha256Value) ||
            !command.Options.TryGetValue(
                "workflow-output",
                out string? workflowOutputValue))
        {
            failure = "Protocol-v2 Finish Verify requires --workflow-bundle, uppercase --workflow-bundle-sha256, and --workflow-output.";
            return false;
        }
        workflowInputSha256 = workflowInputSha256Value!;
        workflowInput = new WorkspacePath(workflowInputValue);
        workflowOutput = new WorkspacePath(workflowOutputValue);
        if (!Path.IsPathFullyQualified(manifest.Value) ||
            !Path.IsPathFullyQualified(output.Value) ||
            !Path.IsPathFullyQualified(workflowInput.Value) ||
            !Path.IsPathFullyQualified(workflowOutput.Value) ||
            !manifest.IsUnder(workspaceRoot) ||
            !output.IsUnder(workspaceRoot) ||
            !workflowInput.IsUnder(workspaceRoot) ||
            !workflowOutput.IsUnder(workspaceRoot))
        {
            failure = "Finish Verify paths must be absolute beneath the exact K-local workspace.";
            return false;
        }
        return true;
    }

    private async ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        ExternalHeadPartInstallContextBindingResult installContextBinding,
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest,
        WorkspacePath manifest,
        string manifestSha256,
        CancellationToken cancellationToken)
    {
        if (!installContextBinding.IsSpecified ||
            verifiedManifest.Manifest.Schema !=
                SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier)
            return await service.VerifyAsync(
                manifest,
                new Sha256Hash(manifestSha256),
                cancellationToken);

        if (!ExternalHeadPartInstallContextBinder.TryReadTargetRace(
                verifiedManifest.Manifest,
                out FormReference targetRace,
                out string targetRaceError))
            return new SkyrimNpcFinishCoreVerificationResult(
                false,
                null,
                [new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    DiagnosticSeverity.Error,
                    targetRaceError)]);

        if (service is not ISkyrimNpcFinishCoreInstallContextService contextService)
            return new SkyrimNpcFinishCoreVerificationResult(
                false,
                null,
                [new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    DiagnosticSeverity.Error,
                    "External Finish Core verification is unavailable without a typed install-context service.")]);

        return await contextService.VerifyWithInstallContextAsync(
            manifest,
            new Sha256Hash(manifestSha256),
            installContextBinding.CreateContext(targetRace),
            cancellationToken);
    }

    private static ProtocolCommandResult Refused(
        WorkspacePath manifest,
        string manifestSha256,
        ImmutableArray<ProtocolEffect> effects,
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        ImmutableArray<ProtocolAuthority> authority,
        SkyrimNpcFinishCoreVerification? verification = null,
        ImmutableArray<ProtocolNextAction> nextActions = default,
        SkyrimNpcFinishCoreStatus? status = null) => new(
        effects,
        diagnostics,
        [],
        authority,
        nextActions.IsDefault ? [] : nextActions,
        AgentProtocolSchemaIds.FinishVerifyResult,
        Result(
            false,
            status ?? verification?.Status,
            manifest,
            manifestSha256,
            null,
            verification,
            diagnostics));

    private static JsonElement Result(
        bool verified,
        SkyrimNpcFinishCoreStatus? status,
        WorkspacePath manifest,
        string manifestSha256,
        SkyrimNpcFinishCoreVerificationArtifactDocument? persisted,
        SkyrimNpcFinishCoreVerification? verification,
        ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        if (verification?.ExternalHeadParts is { } external)
        {
            JsonElement installVerification = SerializeExternalArtifact(
                external.Verification);
            return JsonSerializer.SerializeToElement(new
            {
                SchemaVersion = "1",
                Verified = verified,
                Status = status,
                ManifestPath = manifest.Value,
                ManifestSha256 = IsUpperSha256(manifestSha256)
                    ? manifestSha256
                    : new string('0', 64),
                VerificationPath = persisted?.Path.Value,
                VerificationSize = persisted?.Size,
                VerificationSha256 = persisted?.Sha256,
                Verification = new
                {
                    verification.Schema,
                    verification.Status,
                    verification.Verified,
                    verification.PlacementIncluded,
                    verification.RuntimeAuthority,
                    verification.VisualAuthority,
                    verification.TypedForbiddenCounts,
                    verification.RawForbiddenCounts,
                    verification.PluginSha256,
                    verification.PackageTreeSha256,
                    verification.SourcePackageTreeSha256,
                    verification.ArchiveSha256,
                    verification.RuntimeIdentity,
                    ExternalHeadParts = new
                    {
                        Verification = installVerification
                    }
                },
                Diagnostics = diagnostics.Select(item => new
                {
                    item.Code,
                    item.Severity,
                    item.Message
                })
            }, JsonOptions);
        }

        return JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = "1",
            Verified = verified,
            Status = status,
            ManifestPath = manifest.Value,
            ManifestSha256 = IsUpperSha256(manifestSha256)
                ? manifestSha256
                : new string('0', 64),
            VerificationPath = persisted?.Path.Value,
            VerificationSize = persisted?.Size,
            VerificationSha256 = persisted?.Sha256,
            Verification = verification is null
                ? null
                : new
                {
                    verification.Schema,
                    verification.Status,
                    verification.Verified,
                    verification.PlacementIncluded,
                    verification.RuntimeAuthority,
                    verification.VisualAuthority,
                    verification.TypedForbiddenCounts,
                    verification.RawForbiddenCounts,
                    verification.PluginSha256,
                    verification.PackageTreeSha256,
                    verification.SourcePackageTreeSha256,
                    verification.ArchiveSha256,
                    verification.RuntimeIdentity
                },
            Diagnostics = diagnostics.Select(item => new
            {
                item.Code,
                item.Severity,
                item.Message
            })
        }, JsonOptions);
    }

    private static JsonElement SerializeExternalArtifact(
        ExternalHeadPartInstallVerificationArtifact artifact)
    {
        byte[] bytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact);
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static bool HasCurrentExternalInstallAuthority(
        ExternalHeadPartInstallVerificationArtifact artifact) =>
        artifact.PackageIntegrity &&
        artifact.DescriptorClosureValid &&
        artifact.HistoricalSnapshotValid == true &&
        artifact.VerifiedInstallSnapshot is not null &&
        artifact.CurrentInstallDependencyState ==
            ExternalInstallDependencyState.Verified &&
        artifact.InstallReady &&
        artifact.InstallDependencyAuthority &&
        !artifact.RuntimeAuthority &&
        !artifact.VisualAuthority;

    private static ImmutableArray<ProtocolNextAction> ExternalInstallAction(
        WorkspacePath manifest,
        string manifestSha256,
        ExternalHeadPartInstallVerificationArtifact artifact,
        bool contextSpecified,
        string failureCode)
    {
        string providerIdentity = artifact.ProviderObservations
            .Select(item => item.ProviderPlugin.Value)
            .FirstOrDefault() ?? artifact.MissingPrerequisites
            .Select(item => item.PortableIdentity)
            .FirstOrDefault() ?? "external-headpart-provider";
        string reason = $"{failureCode}: {ExternalFailureMessage(failureCode)}";
        reason += !contextSpecified ||
            failureCode == ExternalHeadPartDiagnosticCodes.InstallContextAbsent
            ? " Supply --data-root and --plugins and retry with a fresh --verification-output."
            : $" Provider '{providerIdentity}' must be repaired before retrying with a fresh --verification-output.";
        return
        [
            new ProtocolNextAction(
                "npc finish verify",
                reason,
                [
                    new ProtocolNextActionBinding(
                        "--manifest", manifest.Value, manifestSha256),
                    new ProtocolNextActionBinding(
                        "--manifest-sha256", manifestSha256, manifestSha256)
                ],
                ["--verification-output"],
                true)
        ];
    }

    private static string ExternalFailureCode(
        ExternalHeadPartInstallVerificationArtifact artifact,
        bool contextSpecified,
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (!contextSpecified)
            return ExternalHeadPartDiagnosticCodes.InstallContextAbsent;
        string? diagnosticCode = diagnostics
            .Select(item => item.Code)
            .FirstOrDefault(IsExternalFailureCode);
        return diagnosticCode ?? artifact.MissingPrerequisites
            .Select(item => item.DiagnosticCode)
            .FirstOrDefault(IsExternalFailureCode) ??
            ExternalHeadPartDiagnosticCodes.PrecheckUnavailable;
    }

    private static bool IsExternalFailureCode(string code) =>
        code.StartsWith("external-headpart-", StringComparison.Ordinal);

    private static string ExternalFailureMessage(string code) => code switch
    {
        ExternalHeadPartDiagnosticCodes.InstallContextAbsent =>
            "External install dependency authority is absent; supply the complete ephemeral context and retry Finish Verify.",
        ExternalHeadPartDiagnosticCodes.ProviderMissing =>
            "The exact external provider plugin is missing; install it and retry Finish Verify.",
        ExternalHeadPartDiagnosticCodes.ProviderDisabled =>
            "The exact external provider plugin is disabled; enable it in the supplied order and retry Finish Verify.",
        ExternalHeadPartDiagnosticCodes.AssetDrift or
        ExternalHeadPartDiagnosticCodes.RecordDrift =>
            "The external provider bytes or records drifted; restore the exact provider installation and retry Finish Verify.",
        _ =>
            "External install dependency verification is unavailable; repair the provider installation and retry Finish Verify."
    };

    private static SkyrimNpcFinishCoreVerificationResult ProjectExternalFailure(
        SkyrimNpcFinishCoreManifest manifest,
        bool contextSpecified,
        ImmutableArray<Diagnostic> diagnostics)
    {
        SkyrimNpcFinishCoreExternalHeadPartManifestAuthority? authority =
            manifest.ExternalHeadParts;
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors =
            authority?.Descriptors ?? [];
        ImmutableArray<Sha256Hash> descriptorIds = descriptors
            .Select(item => item.DescriptorId)
            .OrderBy(item => item.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        string failureCode = contextSpecified
            ? diagnostics.Select(item => item.Code)
                .FirstOrDefault(IsExternalFailureCode) ??
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable
            : ExternalHeadPartDiagnosticCodes.InstallContextAbsent;
        ImmutableArray<ExternalHeadPartInstallProviderObservation> providers =
            descriptors
                .GroupBy(item => item.Provider.Plugin.Value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().Provider)
                .OrderBy(item => item.Plugin.Value, StringComparer.Ordinal)
                .Select(provider => new ExternalHeadPartInstallProviderObservation(
                    provider.Plugin,
                    provider.PluginSha256,
                    null,
                    null))
                .ToImmutableArray();
        ImmutableArray<ExternalHeadPartInstallPrerequisite> prerequisites =
            providers.Length == 0
                ? [new ExternalHeadPartInstallPrerequisite(
                    failureCode,
                    "external-headpart-provider",
                    null,
                    null,
                    null,
                    ExternalFailureMessage(failureCode))]
                : providers.Select(provider => new ExternalHeadPartInstallPrerequisite(
                    // A disabled result without a returned artifact does not
                    // establish the current provider hash.  Keep that fact
                    // unknown instead of fabricating a ProviderDisabled row
                    // that the portable codec would interpret as observed.
                    failureCode == ExternalHeadPartDiagnosticCodes.ProviderDisabled
                        ? ExternalHeadPartDiagnosticCodes.PrecheckUnavailable
                        : failureCode,
                    provider.ProviderPlugin.Value,
                    provider.ExpectedSha256,
                    null,
                    null,
                    ExternalFailureMessage(failureCode))).ToImmutableArray();
        var artifact = new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            false,
            false,
            null,
            descriptorIds,
            null,
            ExternalInstallDependencyState.DeclaredUnverified,
            false,
            false,
            false,
            false,
            providers,
            prerequisites);
        var verification = new SkyrimNpcFinishCoreVerification
        {
            Schema = SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
            Status = SkyrimNpcFinishCoreStatus.Refused,
            Verified = false,
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            PluginSha256 = manifest.PluginSha256,
            PackageTreeSha256 = manifest.PackageTreeSha256,
            SourcePackageTreeSha256 = manifest.SourcePackageTreeSha256,
            ArchiveSha256 = manifest.ArchiveSha256,
            RuntimeIdentity = manifest.RuntimeIdentity,
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartVerification(
                artifact)
        };
        return new SkyrimNpcFinishCoreVerificationResult(
            false,
            verification,
            diagnostics);
    }

    private static ProtocolEffect ReadEffect(
        ApplicationEffectStatus status) => ProtocolEffect.Create(
        AgentEffectKind.ReadWorkspace,
        status,
        ApplicationEffectScope.Workspace);

    private static ProtocolEffect WriteEffect(
        ApplicationEffectStatus status) => ProtocolEffect.Create(
        AgentEffectKind.WriteNewArtifact,
        status,
        ApplicationEffectScope.KLocalOutput);

    private static ProtocolArtifact VerificationArtifact(
        SkyrimNpcFinishCoreVerificationArtifactDocument persisted,
        string requestDigest) => new(
        persisted.Artifact.Kind,
        persisted.Artifact.SchemaOrMediaType,
        persisted.Path.Value,
        persisted.Size,
        persisted.Sha256,
        "npc finish verify",
        requestDigest,
        persisted.Artifact.InputArtifactHashes,
        "independentlyVerified");

    private static ProtocolNextAction RuntimeAction() => new(
        "runtime smoke verify",
        "Runtime authority requires the operator runtime report and package acceptance.",
        [new ProtocolNextActionBinding("--edition", "skyrimse", null)],
        ["--runtime-report", "--package-acceptance"],
        true);

    private static ImmutableArray<ProtocolAuthority> SuccessAuthority() =>
    [
        Authority(AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact manifest and archive bytes were admitted."),
        Authority(AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The manifest-bound plugin and source identity were verified."),
        Authority(AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The Finish package and archive remain hash-bound."),
        Authority(AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "Independent static verification was persisted and reopened."),
        Authority(AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "Finish Verify does not render an off-engine preview."),
        Authority(AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.Required,
            "Static verification does not establish human visual acceptance."),
        Authority(AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Game-runtime verification remains required."),
        Authority(AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.Required,
            "Static verification never grants promotion approval.")
    ];

    private static ImmutableArray<ProtocolAuthority> BindingFailureAuthority() =>
        SuccessAuthority().Select(item => item.Kind switch
            {
                AgentAuthorityKind.InputAdmission => item with
                    { State = AgentAuthorityState.Blocked },
                AgentAuthorityKind.SourceProviderIdentity or
                AgentAuthorityKind.DeterministicMaterialization or
                AgentAuthorityKind.IndependentStaticVerification => item with
                    { State = AgentAuthorityState.Required },
                _ => item
            }).ToImmutableArray();

    private static ImmutableArray<ProtocolAuthority>
        VerificationFailureAuthority() =>
        SuccessAuthority().Select(item => item.Kind ==
                AgentAuthorityKind.IndependentStaticVerification
            ? item with { State = AgentAuthorityState.Blocked }
            : item).ToImmutableArray();

    private static ImmutableArray<ProtocolAuthority>
        ExternalVerificationFailureAuthority() =>
        VerificationFailureAuthority();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ProtocolDiagnostic Diagnostic(
        string code,
        string message,
        DiagnosticClass diagnosticClass) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        diagnosticClass,
        new DiagnosticRecovery(
            RecoveryAction.CorrectInput,
            null,
            null,
            "Correct the Finish verification inputs and choose a fresh output.",
            false));

    private static ProtocolDiagnostic ToProtocolDiagnostic(
        Diagnostic diagnostic)
    {
        string code = diagnostic.Severity switch
        {
            DiagnosticSeverity.Info =>
                ProtocolV2DiagnosticCodes.FinishVerificationInfo,
            DiagnosticSeverity.Warning =>
                ProtocolV2DiagnosticCodes.FinishVerificationWarning,
            _ => ProtocolV2DiagnosticCodes.FinishVerificationFailed
        };
        return new ProtocolDiagnostic(
            code,
            diagnostic.Severity,
            $"{diagnostic.Code}: {diagnostic.Message}",
            DiagnosticClass.Verification,
            diagnostic.Recovery ?? new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "Correct the manifest-bound Finish package before retrying.",
                false));
    }

    private static ProtocolDiagnostic WorkflowDiagnostic(
        AgentWorkflowCodecException exception)
    {
        ProtocolFailureProjection projection =
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                exception.Code);
        return Diagnostic(
            projection.Code,
            $"{exception.Code}: {exception.Message}",
            projection.Class);
    }

    private static (string Code, DiagnosticClass Class) ClassifyArtifactFailure(
        string sourceCode)
    {
        ProtocolFailureProjection projection =
            ProtocolV2DiagnosticCodes.ProjectFinishVerificationFailure(
                sourceCode);
        return (projection.Code, projection.Class);
    }

    private static bool IsUpperSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
