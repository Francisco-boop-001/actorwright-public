using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Manager-owned JSlot-to-NPC facade. It parses and hash-binds the source,
/// manufactures native companions, rebinds the ordinary preset transaction,
/// and invokes the existing final package writer.
/// </summary>
public sealed class RaceMenuJslotNpcBuildService(
    IPresetService presetService,
    ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
    IRaceMenuJslotCompanionBuildService companionBuildService,
    IRaceMenuPresetSelectionTransactionService selectionTransactionService,
    IRaceMenuNpcBuildService npcBuildService,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot,
    INpcBuildPreflightService? preflightService = null,
    IRaceMenuJslotExternalHeadPartPrecheckService?
        externalHeadPartPrecheckService = null,
    IRaceMenuJslotOutputPluginBindingReader?
        outputPluginBindingReader = null,
    IExternalHeadPartInstallVerifier?
        externalHeadPartInstallVerifier = null) : IRaceMenuJslotNpcBuildService
{
    public async ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
        RaceMenuJslotNpcBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(null, null, null, null, null, diagnostics);

        if (preflightService is not null)
        {
            if (request.SourceRequest is not { } sourceRequest ||
                request.SourceRequestSha256 is not { } sourceRequestSha256)
            {
                diagnostics.Add(Error("npc-build-preflight-source-required",
                    "The production JSlot build requires its exact source request path and SHA-256 for preflight."));
                return Refused(null, null, null, null, null, diagnostics);
            }
            var preflightRequest = new NpcBuildPreflightRequest(
                request.CurrentRequest,
                sourceRequest,
                sourceRequestSha256,
                request.Preset,
                request.ExpectedPresetSha256,
                request.DataRoot,
                request.PluginOrder,
                request.CompanionRoot);
            NpcBuildPreflightResult preflight =
                request.ReviewedPreflight is { } reviewed
                    ? await preflightService.VerifyReviewedAsync(
                        preflightRequest, reviewed, cancellationToken)
                        .ConfigureAwait(false)
                    : await preflightService.CreateAsync(
                        preflightRequest, cancellationToken)
                        .ConfigureAwait(false);
            AddDistinct(diagnostics, preflight.Diagnostics);
            if (!preflight.Created || !preflight.ReadyForBuild ||
                preflight.Document is null || HasErrors(diagnostics))
            {
                if (!diagnostics.Any(item => item.Code ==
                        "preflight-derived-plan-stale"))
                    diagnostics.Add(Error("npc-build-preflight-refused",
                        "Required NPC build preflight gates did not pass before staging."));
                return Refused(null, null, null, null, null, diagnostics);
            }
        }

        PresetParseResult parsed = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.Preset),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, parsed.Diagnostics);
        if (parsed.Document is not { IsValid: true } preset ||
            preset.SourceHash != request.ExpectedPresetSha256 ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error("jslot-npc-preset-hash",
                "The JSlot did not parse as valid Skyrim SE data with the caller's exact SHA-256."));
            return Refused(parsed.Document, null, null, null, null, diagnostics);
        }

        RaceMenuNpcExternalCharGenExportAuthority? externalCharGen = null;
        if (npcBuildService is IRaceMenuNpcStandaloneAuthorityReader
                standaloneReader)
        {
            RaceMenuNpcStandaloneAuthorityReadResult currentAssets =
                await standaloneReader.ReadAsync(
                    request.CurrentRequest.AssetAuthority,
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, currentAssets.Diagnostics);
            if (!currentAssets.Accepted || currentAssets.Assets is null ||
                HasErrors(diagnostics))
            {
                diagnostics.Add(Error(
                    "jslot-npc-standalone-authority",
                    "The current standalone authority could not be reopened before companion selection."));
                return Refused(
                    preset, null, null, null, null, diagnostics);
            }
            externalCharGen =
                currentAssets.Assets.ExternalCharGenExportAuthority;
            if (externalCharGen is { } external &&
                (external.PresetSha256 != preset.SourceHash ||
                 external.Race != request.CurrentRequest.Build.References.Race ||
                 external.Sex != request.CurrentRequest.Build.Traits.Sex ||
                 !external.UserConfirmedVisualMatch ||
                 external.RuntimeAuthority))
            {
                diagnostics.Add(Error(
                    "jslot-npc-external-chargen-binding",
                    "The external CharGen export does not match the exact selected preset hash, race, sex, user confirmation, and non-runtime limits."));
                return Refused(
                    preset, null, null, null, null, diagnostics);
            }
        }

        SkyrimFaceRecordPluginAuthorityResult loaded =
            await pluginAuthorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.DataRoot,
                    request.PluginOrder),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, loaded.Diagnostics);
        if (!loaded.Accepted || loaded.Authorities.Length != request.PluginOrder.Length ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error("jslot-npc-provider-authority",
                "The explicit copied-Data plugin order did not reopen as a complete hash-bound authority."));
            return Refused(preset, null, null, null, null, diagnostics);
        }

        var target = new RaceMenuPresetTarget(
            BuildAuthorityId(preset, request.CurrentRequest.Build, loaded.Authorities),
            request.CurrentRequest.Build.References.Race,
            request.CurrentRequest.Build.Traits.Sex,
            request.DataRoot,
            loaded.Authorities);
        RaceMenuJslotExternalHeadPartPrecheckAcceptance?
            acceptedExternalHeadPartPrecheck =
            request.AcceptedExternalHeadPartPrecheck;
        if (acceptedExternalHeadPartPrecheck is { } suppliedAcceptance)
        {
            if (suppliedAcceptance.PresetSha256 != preset.SourceHash ||
                !TargetsShareReviewedContext(suppliedAcceptance.ReviewedTarget,
                    target) ||
                !ValidateDescriptor(suppliedAcceptance.Descriptor, diagnostics))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    "The supplied external-head-part precheck receipt does not match the reopened preset or reviewed target context."));
                return Refused(preset, target, null, null, null, diagnostics);
            }
        }
        else if (externalHeadPartPrecheckService is not null)
        {
            RaceMenuJslotExternalHeadPartPrecheckResult precheck =
                await externalHeadPartPrecheckService.PrecheckAsync(
                    new RaceMenuJslotExternalHeadPartPrecheckRequest(
                        request.Preset,
                        preset.SourceHash,
                        target,
                        null),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, precheck.Diagnostics);
            bool admissible = precheck.Status switch
            {
                RaceMenuJslotExternalHeadPartPrecheckStatus.Accepted =>
                    precheck.Acceptance is not null,
                RaceMenuJslotExternalHeadPartPrecheckStatus.NotApplicable =>
                    precheck.Acceptance is null,
                RaceMenuJslotExternalHeadPartPrecheckStatus.Refused =>
                    false,
                _ => false
            };
            if (!admissible || HasErrors(diagnostics))
            {
                if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                    diagnostics.Add(Error(
                        "external-headpart-precheck-refused",
                        "The external-head-part precheck did not produce an admissible decision."));
                return Refused(preset, target, null, null, null, diagnostics);
            }
            acceptedExternalHeadPartPrecheck = precheck.Acceptance;
            if (acceptedExternalHeadPartPrecheck is { } accepted &&
                (accepted.PresetSha256 != preset.SourceHash ||
                 !TargetsShareReviewedContext(accepted.ReviewedTarget, target) ||
                 !ValidateDescriptor(accepted.Descriptor, diagnostics)))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    "The direct external-head-part precheck returned a receipt for a different reopened target context."));
                return Refused(preset, target, null, null, null, diagnostics);
            }
        }
        RaceMenuPresetTarget companionTarget =
            acceptedExternalHeadPartPrecheck?.ReviewedTarget ?? target;
        RaceMenuJslotCompanionBuildResult companions =
            await companionBuildService.BuildAsync(
                new RaceMenuJslotCompanionBuildRequest(
                    request.CurrentRequest,
                    preset,
                    request.Preset,
                    companionTarget,
                    request.CompanionRoot)
                {
                    AcceptedExternalHeadPartPrecheck =
                        acceptedExternalHeadPartPrecheck
                },
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, companions.Diagnostics);
        FaceGenNpcBakeArtifact? faceGenArtifact = companions.FaceGen?.Artifact;
        if (!companions.Completed || companions.Companion is null ||
            faceGenArtifact is null)
        {
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return Refused(preset, companionTarget, companions, null, null, diagnostics);
        }

        bool hasExternalDescriptors = faceGenArtifact?.ExternalHeadPartDependencies is
            { IsDefault: false, Length: > 0 };
        bool hasExternalAttestations = faceGenArtifact?.ExternalHeadPartExclusionAttestations is
            { IsDefault: false, Length: > 0 };
        bool externalEvidenceMatchesReceipt =
            acceptedExternalHeadPartPrecheck is null
                ? !hasExternalDescriptors && !hasExternalAttestations
                : ExternalEvidenceMatchesReceipt(
                    faceGenArtifact, acceptedExternalHeadPartPrecheck,
                    diagnostics);
        if (faceGenArtifact is not
            {
                ExternalDependencyAuthorities.IsDefaultOrEmpty: false
            } ||
            (hasExternalDescriptors != hasExternalAttestations) ||
            (hasExternalDescriptors && externalCharGen is not null) ||
            (hasExternalDescriptors && acceptedExternalHeadPartPrecheck is null) ||
            !externalEvidenceMatchesReceipt || HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                "jslot-npc-dependency-authority",
                "Manager's native companion bake did not retain an independently reopened, receipt-bound external provider closure."));
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return Refused(preset, companionTarget, companions, null, null, diagnostics);
        }

        try
        {
        RaceMenuPresetCompanionExport selectedCompanion =
            externalCharGen is { } externalCarrier
                ? await StageExternalCompanionAsync(
                    companions.Companion,
                    externalCarrier,
                    request.CompanionRoot,
                    cancellationToken).ConfigureAwait(false)
                : companions.Companion;
        if (externalCharGen is not null)
            diagnostics.Add(new Diagnostic(
                "jslot-npc-external-chargen-selected",
                DiagnosticSeverity.Info,
                "Selected the exact hash-bound user-confirmed RaceMenu FaceGeom export as the complete carrier; runtime authority remains false."));
        bool firstUseOutputBinding = hasExternalDescriptors &&
            externalCharGen is null &&
            request.ExternalInstallDependencies.IsDefaultOrEmpty;
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency>
            externalInstallDependencies = request.ExternalInstallDependencies;
        RaceMenuSelectedDependencyManifestOutputPluginBinding? probeBinding = null;
        ImmutableArray<FormReference> probeFullPnam = [];

        async ValueTask<RaceMenuPresetSelectionTransactionResult>
            RebindSelectionAsync(
                string name,
                ImmutableArray<
                    RaceMenuSelectedDependencyManifestExternalInstallDependency>
                    installDependencies,
                bool outputBindingProbe)
        {
            string parentValue = Path.Combine(
                request.CompanionRoot.Value, name);
            Directory.CreateDirectory(parentValue);
            var parent = new WorkspacePath(parentValue);
            return await selectionTransactionService.RebindAsync(
                new RaceMenuPresetSelectionTransactionRequest(
                    request.CurrentRequest,
                    preset,
                    selectedCompanion,
                    companionTarget,
                    request.CurrentRequest.ApplyBodySlide,
                    parent)
                {
                    FaceGenDependencyAuthorities = faceGenArtifact
                        .ExternalDependencyAuthorities,
                    FaceGenProviderSidecarAuthorities = faceGenArtifact
                        .ExternalProviderSidecarAuthorities,
                    ExternalHeadPartDependencies = faceGenArtifact
                        .ExternalHeadPartDependencies ?? [],
                    ExternalHeadPartExclusionAttestations = faceGenArtifact
                        .ExternalHeadPartExclusionAttestations ?? [],
                    ManagerOwnedFaceGeomCarrier = hasExternalDescriptors
                        ? new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                            selectedCompanion.FaceGeom,
                            selectedCompanion.FaceGeomSha256)
                        : null,
                    ExternalInstallDependencies = installDependencies,
                    JslotOutputBindingProbe = outputBindingProbe
                        ? RaceMenuJslotProbeToken.Instance
                        : null
                },
                cancellationToken).ConfigureAwait(false);
        }

        RaceMenuNpcExecutionRequest BindOutputRoot(
            RaceMenuNpcExecutionRequest candidate,
            WorkspacePath outputRoot,
            bool outputBindingProbe) =>
            (externalCharGen is null
                ? candidate with
                {
                    Build = candidate.Build with { OutputRoot = outputRoot },
                    ManagerOwnedFaceGeomCarrier =
                        new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                            selectedCompanion.FaceGeom,
                            selectedCompanion.FaceGeomSha256)
                }
                : candidate with
                {
                    Build = candidate.Build with { OutputRoot = outputRoot }
                }) with
            {
                JslotOutputBindingProbe = outputBindingProbe
                    ? RaceMenuJslotProbeToken.Instance
                    : null
            };

        RaceMenuPresetSelectionTransactionResult? selection = null;
        if (firstUseOutputBinding)
        {
            RaceMenuPresetSelectionTransactionResult probeSelection =
                await RebindSelectionAsync(
                    "probe-selection", [], outputBindingProbe: true)
                    .ConfigureAwait(false);
            AddDistinct(diagnostics, probeSelection.Diagnostics);
            if (!probeSelection.Committed ||
                probeSelection.CandidateRequest is null ||
                HasErrors(diagnostics))
            {
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return Refused(preset, companionTarget, companions, null, null,
                    diagnostics);
            }

            var probePackageRoot = new WorkspacePath(Path.Combine(
                request.CompanionRoot.Value, "probe-package"));
            RaceMenuNpcExecutionResult probeExecution =
                await npcBuildService.ExecuteAsync(
                    BindOutputRoot(
                        probeSelection.CandidateRequest,
                        probePackageRoot,
                        outputBindingProbe: true),
                    progress,
                    cancellationToken).ConfigureAwait(false);
            if (probeExecution.Completed ||
                probeExecution.Build is not null ||
                probeExecution.ExternalInstallOutputPluginProbe is not
                    { } probeArtifact ||
                HasErrors(probeExecution.Diagnostics))
            {
                AddDistinct(diagnostics, probeExecution.Diagnostics);
                diagnostics.Add(Error(
                    "jslot-npc-output-binding-probe",
                    "The private JSlot output-plugin binding probe did not complete."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return Refused(preset, companionTarget, companions, null, null,
                    diagnostics);
            }

            if (probeArtifact.OutputPlugin !=
                    request.CurrentRequest.Build.OutputPlugin ||
                !probeArtifact.PluginPath.IsUnder(request.CompanionRoot) ||
                !File.Exists(probeArtifact.PluginPath.Value))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The private JSlot output-plugin probe escaped its owned companion root."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return Refused(preset, companionTarget, companions, null, null,
                    diagnostics);
            }

            RaceMenuJslotOutputPluginBindingReadResult probeRead =
                await (outputPluginBindingReader ??
                    new RaceMenuJslotOutputPluginBindingReader()).ReadAsync(
                    new RaceMenuJslotOutputPluginBindingReadRequest(
                        GameEdition.SkyrimSpecialEdition,
                        probeArtifact.PluginPath,
                        request.CurrentRequest.Build.OutputPlugin,
                        probeArtifact.AllocatedFormId),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, probeRead.Diagnostics);
            if (!probeRead.Accepted || probeRead.Binding is not { } binding ||
                binding.Plugin != request.CurrentRequest.Build.OutputPlugin ||
                binding.Sha256 != probeArtifact.PluginSha256 ||
                binding.ByteLength != new FileInfo(
                    probeArtifact.PluginPath.Value).Length ||
                !ValidateOutputPluginBinding(
                    binding,
                    faceGenArtifact.ExternalHeadPartDependencies ?? [],
                    probeRead.FullPnam,
                    diagnostics))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The private JSlot output-plugin probe did not reopen the exact hash, length, master, and PNAM closure."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return Refused(preset, companionTarget, companions, null, null,
                    diagnostics);
            }

            probeBinding = binding;
            probeFullPnam = probeRead.FullPnam;
            externalInstallDependencies =
                (faceGenArtifact.ExternalHeadPartDependencies ?? [])
                    .Zip(faceGenArtifact.ExternalHeadPartExclusionAttestations ?? [])
                    .Select(pair => new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                        pair.First,
                        pair.Second,
                        binding with
                        {
                            PnamBindings = pair.First.Members
                                .Select(item => item.WinningForm)
                                .ToImmutableArray()
                        },
                        pair.Second.OutputFaceGeomPath,
                        pair.Second.OutputFaceGeomSha256,
                        pair.Second.OutputFaceGeomByteLength))
                    .ToImmutableArray();
        }

        selection = await RebindSelectionAsync(
            firstUseOutputBinding ? "final-selection" : "selection",
            externalInstallDependencies,
            outputBindingProbe: false).ConfigureAwait(false);
        AddDistinct(diagnostics, selection.Diagnostics);
        if (!selection.Committed || selection.CandidateRequest is null ||
            HasErrors(diagnostics))
        {
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return Refused(preset, companionTarget, companions, selection, null,
                diagnostics);
        }

        var packageCandidateRoot = new WorkspacePath(Path.Combine(
            request.CompanionRoot.Value, "package-candidate"));
        RaceMenuNpcExecutionRequest managerBoundRequest = BindOutputRoot(
            selection.CandidateRequest, packageCandidateRoot,
            outputBindingProbe: false);
        RaceMenuNpcExecutionResult execution =
            await npcBuildService.ExecuteAsync(
                managerBoundRequest,
                progress,
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, execution.Diagnostics);
        if (!execution.Completed || execution.Build?.Artifact is null ||
            HasErrors(diagnostics))
        {
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return new RaceMenuJslotNpcBuildResult(
                false, preset, companionTarget, companions, selection, execution,
                diagnostics.ToImmutable());
        }

        if (firstUseOutputBinding && probeBinding is { } expectedBinding)
        {
            RaceMenuJslotOutputPluginBindingReadResult finalRead =
                await (outputPluginBindingReader ??
                    new RaceMenuJslotOutputPluginBindingReader()).ReadAsync(
                    new RaceMenuJslotOutputPluginBindingReadRequest(
                        GameEdition.SkyrimSpecialEdition,
                        execution.Build.Artifact.Plugin,
                        request.CurrentRequest.Build.OutputPlugin,
                        execution.Build.Artifact.AllocatedFormId),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, finalRead.Diagnostics);
            if (!finalRead.Accepted || finalRead.Binding is not { } finalBinding ||
                finalRead.FullPnam.IsDefaultOrEmpty ||
                !OutputPluginBindingEqual(expectedBinding, finalBinding) ||
                !finalRead.FullPnam.SequenceEqual(probeFullPnam))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The final JSlot output plugin binding drifted from the private probe."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }
        }

        if (hasExternalDescriptors)
        {
            if (selection.CandidateRequest.SelectedDependencyManifest is not { } selectedManifest ||
                execution.Build?.PackageVerification is not
                    { Verified: true, Artifact: not null } packageVerification ||
                packageVerification.Artifact.ManifestSha256 !=
                    execution.Build.Artifact.ManifestSha256 ||
                packageVerification.Artifact.RuntimeProof)
            {
                diagnostics.Add(Error(
                    "jslot-npc-prepublication-verification",
                    "The final external-SMP candidate did not provide verified package and selected-manifest authorities."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }

            var packagedSelectedManifest = new WorkspacePath(Path.Combine(
                packageCandidateRoot.Value,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                    .Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(packagedSelectedManifest.Value))
            {
                diagnostics.Add(Error(
                    "jslot-npc-prepublication-verification",
                    "The final package did not contain the canonical selected dependency manifest."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }

            if (externalHeadPartInstallVerifier is null)
            {
                diagnostics.Add(Error(
                    "jslot-npc-prepublication-verifier-unavailable",
                    "External-SMP JSlot publication requires the existing strict install verifier."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }

            var verificationRequest =
                new ExternalHeadPartInstallVerificationRequest(
                    packageCandidateRoot,
                    execution.Build.Artifact.Plugin,
                    request.CurrentRequest.Build.OutputPlugin,
                    execution.Build.Artifact.PluginSha256,
                    packagedSelectedManifest,
                    selectedManifest.ExpectedManifestSha256,
                    new ExternalHeadPartInstallVerificationContext(
                        request.DataRoot,
                        request.PluginOrder,
                        request.CurrentRequest.Build.References.Race),
                    RequireCurrentAuthority: true,
                    CreatePrepublication: true,
                    TargetActorFormId: execution.Build.Artifact.AllocatedFormId);
            ExternalHeadPartInstallVerificationResult installVerification =
                await externalHeadPartInstallVerifier.VerifyAsync(
                    verificationRequest,
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, installVerification.Diagnostics);
            ImmutableArray<Sha256Hash> expectedDescriptorIds =
                externalInstallDependencies
                    .Select(item => item.Descriptor.DescriptorId)
                    .OrderBy(item => item.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
            if (!IsStrictPrepublicationPass(
                    installVerification,
                    verificationRequest,
                    selectedManifest.ExpectedManifestSha256,
                    expectedDescriptorIds,
                    diagnostics))
            {
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }

            var bindings = externalInstallDependencies
                .Select(item => new RaceMenuNpcExternalInstallPrepublicationBinding(
                    item.Descriptor.DescriptorId,
                    item.Attestation.AttestationSha256))
                .OrderBy(item => item.DescriptorId.Value, StringComparer.Ordinal)
                .ThenBy(item => item.FaceGeomExclusionAttestationSha256.Value,
                    StringComparer.Ordinal)
                .Distinct()
                .ToImmutableArray();
            ImmutableArray<Sha256Hash> bindingDescriptorIds = bindings
                .Select(item => item.DescriptorId)
                .ToImmutableArray();
            if (bindings.IsDefaultOrEmpty ||
                bindings.Length != externalInstallDependencies.Length ||
                bindingDescriptorIds.Length !=
                    bindingDescriptorIds.Distinct().Count() ||
                !bindingDescriptorIds.SequenceEqual(
                    installVerification.Artifact.DescriptorIds))
            {
                diagnostics.Add(Error(
                    "jslot-npc-prepublication-verification",
                    "Strict external-SMP verification descriptors did not match the unique ordered prepublication bindings."));
                CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                    postPromotion: false);
                return new RaceMenuJslotNpcBuildResult(
                    false, preset, companionTarget, companions, selection, null,
                    diagnostics.ToImmutable());
            }
            execution = execution with
            {
                ExternalInstallPrepublication =
                    new RaceMenuNpcExternalInstallPrepublicationArtifact(
                        packageVerification.Artifact.ManifestSha256,
                        selectedManifest.ExpectedManifestSha256,
                        bindings,
                        installVerification.Artifact)
            };
        }

        RaceMenuNpcExecutionResult promotedExecution;
        try
        {
            if (!Directory.Exists(packageCandidateRoot.Value) ||
                Directory.Exists(request.CurrentRequest.Build.OutputRoot.Value) ||
                File.Exists(request.CurrentRequest.Build.OutputRoot.Value))
                throw new InvalidDataException(
                    "Final package promotion requires one complete candidate and one absent final output root.");
            Directory.Move(packageCandidateRoot.Value,
                request.CurrentRequest.Build.OutputRoot.Value);
            BlankNpcBuildArtifact promotedArtifact = PromoteArtifactPaths(
                execution.Build.Artifact,
                packageCandidateRoot,
                request.CurrentRequest.Build.OutputRoot);
            promotedExecution = execution with
            {
                Build = execution.Build with { Artifact = promotedArtifact }
            };
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("jslot-npc-final-promotion",
                exception.Message));
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return new RaceMenuJslotNpcBuildResult(
                false, preset, companionTarget, companions, selection, execution,
                diagnostics.ToImmutable());
        }

        CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
            postPromotion: true);
        bool completed = promotedExecution.Completed && !HasErrors(diagnostics);
        if (completed)
        {
            diagnostics.Add(new Diagnostic(
                "jslot-npc-manager-only-complete",
                DiagnosticSeverity.Info,
                externalCharGen is null
                    ? "Manager parsed the JSlot, created its temporary NPC, natively baked and reopened its NIF/DDS, rebound the exact companions, and independently reopened the final static package. Runtime authority remains false."
                    : "Manager parsed the JSlot, reopened its hash-bound user-confirmed RaceMenu FaceGeom export, retained the Manager FaceTint and dependency authorities, and independently reopened the final static package. Runtime authority remains false."));
        }
        return new RaceMenuJslotNpcBuildResult(
            completed,
            preset,
            companionTarget,
            companions,
            selection,
            promotedExecution,
            diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException)
        {
            diagnostics.Add(Error("jslot-npc-transaction",
                exception.Message));
            CleanupOwnedCompanionRoot(request.CompanionRoot, diagnostics,
                postPromotion: false);
            return Refused(preset, companionTarget, companions, null, null, diagnostics);
        }
    }

    private static bool ValidateOutputPluginBinding(
        RaceMenuSelectedDependencyManifestOutputPluginBinding binding,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        ImmutableArray<FormReference> fullPnam,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (descriptors.IsDefaultOrEmpty || binding.Masters.IsDefaultOrEmpty ||
            fullPnam.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                "The output plugin binding has no complete descriptor, master, or PNAM closure."));
            return false;
        }

        var masterNames = binding.Masters
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool valid = true;
        foreach (ExternalHeadPartDependencyDescriptor descriptor in descriptors)
        {
            bool mastersValid = descriptor.Members
                .Select(item => item.RequiredOutputMaster.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .All(masterNames.Contains);
            bool pnamValid = IsOrderedSubsequence(
                descriptor.Members.Select(item => item.WinningForm), fullPnam);
            if (!mastersValid)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                    $"The output plugin binding is missing a master required by descriptor '{descriptor.DescriptorId}'."));
                valid = false;
            }
            if (!pnamValid)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    $"The output plugin PNAM binding does not match descriptor '{descriptor.DescriptorId}'."));
                valid = false;
            }
        }
        return valid;
    }

    private static bool IsOrderedSubsequence(
        IEnumerable<FormReference> expected,
        IEnumerable<FormReference> actual)
    {
        using IEnumerator<FormReference> expectedEnumerator =
            expected.GetEnumerator();
        if (!expectedEnumerator.MoveNext()) return true;
        foreach (FormReference observed in actual)
        {
            if (FormReferencesEqual(expectedEnumerator.Current, observed) &&
                !expectedEnumerator.MoveNext())
                return true;
        }
        return false;
    }

    private static bool FormReferencesEqual(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId &&
        string.Equals(left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static bool OutputPluginBindingEqual(
        RaceMenuSelectedDependencyManifestOutputPluginBinding left,
        RaceMenuSelectedDependencyManifestOutputPluginBinding right) =>
        left.Plugin == right.Plugin &&
        left.Sha256 == right.Sha256 &&
        left.ByteLength == right.ByteLength &&
        left.Masters.SequenceEqual(right.Masters) &&
        left.PnamBindings.SequenceEqual(right.PnamBindings);

    private static bool IsStrictPrepublicationPass(
        ExternalHeadPartInstallVerificationResult verification,
        ExternalHeadPartInstallVerificationRequest request,
        Sha256Hash expectedSelectedManifestSha256,
        ImmutableArray<Sha256Hash> expectedDescriptorIds,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ExternalHeadPartInstallVerificationArtifact artifact =
            verification.Artifact;
        try
        {
            byte[] canonicalBytes =
                ExternalHeadPartDependencyDescriptorCodec
                    .SerializeInstallVerificationArtifact(artifact);
            _ = ExternalHeadPartDependencyDescriptorCodec
                .ParseInstallVerificationArtifact(canonicalBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           FormatException or
                                           OverflowException)
        {
            diagnostics.Add(Error(
                "jslot-npc-prepublication-verification",
                $"The external-SMP verification artifact was not canonical: {exception.Message}"));
            return false;
        }
        if (artifact.HistoricalSnapshotValid is not null)
        {
            diagnostics.Add(Error(
                "jslot-npc-prepublication-verification",
                "Create-time external-SMP verification cannot claim historical snapshot validity."));
            return false;
        }
        ExternalHeadPartVerifiedInstallSnapshot? snapshot =
            artifact.VerifiedInstallSnapshot;
        bool snapshotMatchesCurrentContext = snapshot is not null &&
            snapshot.SelectedManifestSha256 == expectedSelectedManifestSha256 &&
            snapshot.DescriptorIds.SequenceEqual(expectedDescriptorIds) &&
            !snapshot.ContextFingerprint.Observations.IsDefaultOrEmpty &&
            snapshot.ContextFingerprint.Observations
                .Select(item => item.Order)
                .SequenceEqual(Enumerable.Range(
                    0, snapshot.ContextFingerprint.Observations.Length)) &&
            EnabledPluginObservationsMatch(
                snapshot.ContextFingerprint.Observations,
                request.Context?.EnabledPluginOrder ?? []);
        bool accepted = verification.DescriptorClosureValid &&
            artifact.SchemaIdentifier ==
                ExternalHeadPartSchemaIdentifiers.InstallVerification &&
            artifact.PackageIntegrity &&
            artifact.DescriptorClosureValid &&
            artifact.CurrentInstallDependencyState ==
                ExternalInstallDependencyState.Verified &&
            artifact.InstallReady &&
            artifact.InstallDependencyAuthority &&
            snapshotMatchesCurrentContext &&
            !artifact.RuntimeAuthority &&
            !artifact.VisualAuthority;
        if (!accepted)
            diagnostics.Add(Error(
                "jslot-npc-prepublication-verification",
                "Strict external-SMP prepublication verification did not establish package, descriptor, install, and non-runtime authority."));
        return accepted && !HasErrors(diagnostics);
    }

    private static bool EnabledPluginObservationsMatch(
        ImmutableArray<ExternalHeadPartInstallObservation> observations,
        ImmutableArray<PluginName> pluginOrder)
    {
        if (pluginOrder.IsDefaultOrEmpty)
            return false;
        ExternalHeadPartInstallObservation[] enabled = observations
            .Where(item => string.Equals(
                item.Kind, "enabled-plugin", StringComparison.Ordinal))
            .OrderBy(item => item.Order)
            .ToArray();
        return enabled.Length == pluginOrder.Length &&
            enabled.Select(item => item.PortableIdentity)
                .SequenceEqual(pluginOrder.Select(item => item.Value),
                    StringComparer.OrdinalIgnoreCase) &&
            enabled.Select(item => item.Order)
                .SequenceEqual(Enumerable.Range(0, pluginOrder.Length));
    }

    private static BlankNpcBuildArtifact PromoteArtifactPaths(
        BlankNpcBuildArtifact artifact,
        WorkspacePath candidateRoot,
        WorkspacePath finalRoot) => artifact with
    {
        OutputRoot = finalRoot,
        Plugin = RemapPromotedPath(artifact.Plugin, candidateRoot, finalRoot),
        FaceGeom = RemapPromotedPath(artifact.FaceGeom, candidateRoot, finalRoot),
        FaceTint = RemapPromotedPath(artifact.FaceTint, candidateRoot, finalRoot),
        Manifest = RemapPromotedPath(artifact.Manifest, candidateRoot, finalRoot)
    };

    private static WorkspacePath RemapPromotedPath(
        WorkspacePath path,
        WorkspacePath candidateRoot,
        WorkspacePath finalRoot)
    {
        if (!path.IsUnder(candidateRoot))
            throw new InvalidDataException(
                $"Candidate artifact '{path}' escaped the package root.");
        string relative = Path.GetRelativePath(candidateRoot.Value, path.Value);
        var promoted = new WorkspacePath(Path.Combine(finalRoot.Value, relative));
        if (!promoted.IsUnder(finalRoot) || !File.Exists(promoted.Value))
            throw new InvalidDataException(
                $"Promoted artifact '{promoted}' did not reopen below the final package root.");
        return promoted;
    }

    private static void CleanupOwnedCompanionRoot(
        WorkspacePath root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool postPromotion)
    {
        if (!Directory.Exists(root.Value)) return;
        try
        {
            var info = new DirectoryInfo(root.Value);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                    .Any(item => item.Attributes.HasFlag(
                        FileAttributes.ReparsePoint)))
                throw new InvalidDataException(
                    "The owned companion tree contains a reparse point and was quarantined instead of traversed.");
            Directory.Delete(root.Value, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                postPromotion
                    ? "jslot-npc-post-promotion-cleanup"
                    : "jslot-npc-staging-quarantined",
                postPromotion
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error,
                $"Owned companion staging remains quarantined at '{root.Value}': {exception.Message}"));
        }
    }

    private static async ValueTask<RaceMenuPresetCompanionExport>
        StageExternalCompanionAsync(
            RaceMenuPresetCompanionExport managerCompanion,
            RaceMenuNpcExternalCharGenExportAuthority external,
            WorkspacePath companionRoot,
            CancellationToken cancellationToken)
    {
        var root = new WorkspacePath(Path.Combine(
            companionRoot.Value,
            "external-chargen"));
        if (!root.IsUnder(companionRoot) ||
            Directory.Exists(root.Value) ||
            File.Exists(root.Value))
            throw new InvalidDataException(
                "External CharGen companion staging requires one new directory below the owned companion root.");
        Directory.CreateDirectory(root.Value);

        var preset = new WorkspacePath(Path.Combine(
            root.Value, "UBE_Chel.jslot"));
        var faceGeom = new WorkspacePath(Path.Combine(
            root.Value, "UBE_Chel.nif"));
        var faceTint = new WorkspacePath(Path.Combine(
            root.Value, "UBE_Chel.dds"));
        await CopyAndVerifyAsync(
            managerCompanion.Preset,
            managerCompanion.PresetSha256,
            preset,
            cancellationToken).ConfigureAwait(false);
        await CopyAndVerifyAsync(
            external.FaceGeom,
            external.FaceGeomSha256,
            faceGeom,
            cancellationToken).ConfigureAwait(false);
        await CopyAndVerifyAsync(
            managerCompanion.FaceTint,
            managerCompanion.FaceTintSha256,
            faceTint,
            cancellationToken).ConfigureAwait(false);

        return new RaceMenuPresetCompanionExport(
            preset,
            managerCompanion.PresetSha256,
            faceGeom,
            external.FaceGeomSha256,
            faceTint,
            managerCompanion.FaceTintSha256);
    }

    private static async ValueTask CopyAndVerifyAsync(
        WorkspacePath source,
        Sha256Hash expectedSha256,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(
                         source.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        await using (var output = new FileStream(
                         destination.Value,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.WriteThrough))
        {
            await input.CopyToAsync(
                output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        await using var reopened = new FileStream(
            destination.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        var actual = new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                reopened, cancellationToken).ConfigureAwait(false)));
        if (actual != expectedSha256)
            throw new InvalidDataException(
                $"Staged external CharGen companion '{destination.Value}' did not retain SHA-256 {expectedSha256}.");
    }

    private void ValidateRequest(
        RaceMenuJslotNpcBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CurrentRequest.Build.Edition != GameEdition.SkyrimSpecialEdition ||
            request.CurrentRequest.Build.ExistingNpcTarget is not null)
            diagnostics.Add(Error("jslot-npc-request-mode",
                "The JSlot facade currently creates one fresh Skyrim SE NPC from a reviewed new-NPC request."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
            diagnostics.Add(Error("jslot-npc-plugin-order",
                "PluginOrder must be one explicit, distinct ascending array."));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.Preset));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.DataRoot));
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, request.CompanionRoot));
        if (!request.Preset.IsUnder(labRoot) || !File.Exists(request.Preset.Value))
            diagnostics.Add(Error("jslot-npc-preset-path",
                "Preset must be one existing K-local file."));
        if (!request.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("jslot-npc-data-root",
                "DataRoot must be one existing copied Data directory below the lab root."));
        if (!request.CompanionRoot.IsUnder(labRoot) ||
            request.CompanionRoot == labRoot ||
            request.CompanionRoot.IsUnder(request.DataRoot) ||
            request.DataRoot.IsUnder(request.CompanionRoot) ||
            request.CompanionRoot == request.CurrentRequest.Build.OutputRoot ||
            request.CompanionRoot.IsUnder(
                request.CurrentRequest.Build.OutputRoot) ||
            request.CurrentRequest.Build.OutputRoot.IsUnder(
                request.CompanionRoot) ||
            Directory.Exists(request.CompanionRoot.Value) ||
            File.Exists(request.CompanionRoot.Value))
            diagnostics.Add(Error("jslot-npc-companion-root",
                "CompanionRoot must be one absent K-local path disjoint from DataRoot and the final output root."));
    }

    private static string BuildAuthorityId(
        PresetDocument preset,
        RaceMenuNpcBuildRequest build,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> plugins)
    {
        string identity = string.Join('\n',
            new[]
            {
                preset.SourceHash.Value,
                build.References.Race.ToString(),
                build.Traits.Sex.ToString()
            }.Concat(plugins.Select(item =>
                $"{item.Plugin.Value}|{item.Path.Value}|{item.ExpectedSha256.Value}")));
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "manager-jslot-" + hash[..24].ToLowerInvariant();
    }

    private static bool TargetsShareReviewedContext(
        RaceMenuPresetTarget supplied,
        RaceMenuPresetTarget reopened)
    {
        if (supplied.Race != reopened.Race || supplied.Sex != reopened.Sex ||
            !string.Equals(supplied.DataRoot.Value, reopened.DataRoot.Value,
                StringComparison.OrdinalIgnoreCase) ||
            supplied.PluginOrder.Length != reopened.PluginOrder.Length)
            return false;
        return supplied.PluginOrder.Zip(reopened.PluginOrder).All(pair =>
            pair.First.Plugin == pair.Second.Plugin &&
            string.Equals(pair.First.Path.Value, pair.Second.Path.Value,
                StringComparison.OrdinalIgnoreCase) &&
            pair.First.ExpectedSha256 == pair.Second.ExpectedSha256);
    }

    private static bool ValidateDescriptor(
        ExternalHeadPartDependencyDescriptor descriptor,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                descriptor);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           FormatException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                $"The supplied external-head-part descriptor is not canonical: {exception.Message}"));
            return false;
        }
    }

    private static bool ExternalEvidenceMatchesReceipt(
        FaceGenNpcBakeArtifact? artifact,
        RaceMenuJslotExternalHeadPartPrecheckAcceptance acceptance,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (artifact?.ExternalHeadPartDependencies is not
                { IsDefault: false, Length: 1 } descriptors ||
            artifact.ExternalHeadPartExclusionAttestations is not
                { IsDefault: false, Length: 1 } attestations)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "The native companion artifact did not retain one descriptor and one exclusion attestation."));
            return false;
        }
        try
        {
            bool descriptorEqual =
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    descriptors[0]).AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    acceptance.Descriptor));
            bool attestationEqual = attestations[0].DescriptorId ==
                                        descriptors[0].DescriptorId &&
                                    attestations[0].OutputFaceGeomSha256 ==
                                        artifact.FaceGeomSha256 &&
                                    attestations[0].OutputFaceGeomByteLength ==
                                        artifact.FaceGeomByteLength;
            if (!descriptorEqual || !attestationEqual)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "Native external-head-part rediscovery or its FaceGeom exclusion attestation disagrees with the accepted receipt."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           FormatException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                $"Native external-head-part evidence was not canonical: {exception.Message}"));
            return false;
        }
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
                target.Add(diagnostic);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuJslotNpcBuildResult Refused(
        PresetDocument? preset,
        RaceMenuPresetTarget? target,
        RaceMenuJslotCompanionBuildResult? companions,
        RaceMenuPresetSelectionTransactionResult? selection,
        RaceMenuNpcExecutionResult? execution,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, preset, target, companions, selection, execution,
            diagnostics.ToImmutable());
}
