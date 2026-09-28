using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Builds a complete preset authority candidate off to the side. The caller's
/// immutable current request is never changed; only a fully reopened candidate
/// is returned.
/// </summary>
public sealed class RaceMenuPresetSelectionTransactionService(
    IRaceMenuPresetRecordAuthorityBuilder recordBuilder,
    IRaceMenuPresetRecordAuthorityWriter recordWriter,
    IRaceMenuPresetBundleAuthorityWriter bundleWriter,
    IRaceMenuNpcAppearancePlanService planService,
    IRaceMenuNpcStandaloneAuthorityReader standaloneReader,
    IRaceMenuPresetStandaloneAuthorityWriter standaloneWriter,
    IRaceMenuSelectedDependencyManifestWriter selectedDependencyWriter,
    ISkyrimNpcWholeSkinAuthorityResolver wholeSkinResolver,
    IRaceMenuNpcWholeSkinAuthorityWriter wholeSkinWriter,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot,
    IRaceMenuSelectedDependencyManifestReader?
        selectedDependencyReader = null)
    : IRaceMenuPresetSelectionTransactionService
{
    public async ValueTask<RaceMenuPresetSelectionTransactionResult> RebindAsync(
        RaceMenuPresetSelectionTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        bool hasExternalEvidence = HasExternalEvidence(request);
        bool outputBindingProbe = RaceMenuJslotProbeToken.IsValid(
            request.JslotOutputBindingProbe);
        if (RaceMenuJslotProbeToken.IsInvalid(request.JslotOutputBindingProbe))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "The selection transaction received an unknown JSlot probe token."));
        if (outputBindingProbe &&
            (request.ExternalHeadPartDependencies.IsDefaultOrEmpty ||
             request.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "The JSlot output-binding probe requires paired external descriptor and FaceGeom attestation evidence."));
        }
        if (hasExternalEvidence)
        {
            if (selectedDependencyReader is null && !outputBindingProbe)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    "An external selection transaction requires the strict selected-dependency manifest reader."));
            ValidateExternalEnvelope(request, diagnostics, outputBindingProbe);
        }
        if (HasErrors(diagnostics)) return Refused(null, diagnostics);

        RaceMenuNpcStandaloneAuthorityReadResult currentAssets =
            await standaloneReader.ReadAsync(
                request.CurrentRequest.AssetAuthority,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(currentAssets.Diagnostics);
        if (!currentAssets.Accepted || currentAssets.Assets is null ||
            HasErrors(diagnostics)) return Refused(null, diagnostics);
        if (hasExternalEvidence &&
            !await VerifyManagerCarrierAsync(
                request.ManagerOwnedFaceGeomCarrier!,
                request.ExternalHeadPartExclusionAttestations,
                diagnostics,
                cancellationToken).ConfigureAwait(false))
            return Refused(null, diagnostics);

        WorkspacePath? candidateRoot = null;
        try
        {
            candidateRoot = CreateCandidateRoot(request);
            RaceMenuPresetRecordAuthorityBuildResult recordDraft =
                await recordBuilder.BuildAsync(
                    request.SelectedPreset,
                    request.Target,
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(recordDraft.Diagnostics);
            if (!recordDraft.Accepted || recordDraft.Draft is null ||
                HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            var recordDestination = new WorkspacePath(Path.Combine(
                candidateRoot.Value.Value, "record-authority.json"));
            RaceMenuPresetRecordAuthorityWriteResult record =
                await recordWriter.WriteAsync(
                    new RaceMenuPresetRecordAuthorityWriteRequest(
                        recordDraft.Draft, recordDestination),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(record.Diagnostics);
            if (!record.Written || record.Artifact is null || HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuPresetBundleAuthorityWriteResult bundle =
                await bundleWriter.WriteAsync(
                    new RaceMenuPresetBundleAuthorityWriteRequest(
                        request.SelectedPreset,
                        request.Companion,
                        request.CurrentRequest.Build.ProviderContext,
                        record.Artifact,
                        candidateRoot.Value),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(bundle.Diagnostics);
            if (!bundle.Written || bundle.Artifact is null || HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            SkyrimNpcWholeSkinAuthorityResult wholeSkinDraft =
                await wholeSkinResolver.ResolveAsync(
                    new SkyrimNpcWholeSkinAuthorityRequest(
                        request.CurrentRequest.Build.Edition,
                        request.Target.DataRoot,
                        request.Target.Race,
                        request.Target.Sex,
                        request.Target.PluginOrder)
                    {
                        DefaultOutfit =
                            request.CurrentRequest.Build.References.DefaultOutfit,
                        AllowMeshEmbeddedSkinTextureRoute =
                            currentAssets.Assets.BodyMeshAuthority is not null ||
                            request.CurrentRequest
                                .AllowInheritedMeshEmbeddedSkinTextureRoute,
                        BodyMeshAuthority =
                            currentAssets.Assets.BodyMeshAuthority
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(wholeSkinDraft.Diagnostics);
            if (!wholeSkinDraft.Accepted || wholeSkinDraft.Authority is null ||
                HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuNpcWholeSkinAuthorityWriteResult wholeSkin =
                await wholeSkinWriter.WriteAsync(
                    new RaceMenuNpcWholeSkinAuthorityWriteRequest(
                        wholeSkinDraft.Authority,
                        request.Target.DataRoot,
                        request.Target.PluginOrder,
                        new WorkspacePath(Path.Combine(
                            candidateRoot.Value.Value,
                            "whole-skin-authority.json"))),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(wholeSkin.Diagnostics);
            if (!wholeSkin.Written || wholeSkin.Artifact is null ||
                HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuNpcBuildRequest candidateBuild =
                request.CurrentRequest.Build with
                {
                    PresetBundle = bundle.Artifact.Bundle,
                    WholeSkinAuthority = wholeSkin.Artifact.Authority,
                    Stats = request.CurrentRequest.Build.Stats with
                    {
                        Weight = request.SelectedPreset.Appearance.Weight?.Value ??
                             throw new InvalidDataException(
                                 "The selected RaceMenu preset has no actor weight.")
                    },
                    PluginAuthorities = request.Target.PluginOrder
                         .Select(item => new NpcCreationPluginAuthority(
                             item.Plugin, item.Path, item.ExpectedSha256))
                         .ToImmutableArray()
                };
            RaceMenuNpcAppearancePlanResult provisionalPlan =
                await planService.AnalyzeAsync(
                    candidateBuild, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(provisionalPlan.Diagnostics);
            if (!provisionalPlan.Accepted || provisionalPlan.Plan is not { IsReady: true } plan ||
                HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuNpcStandaloneAssets prior = currentAssets.Assets;
            RaceMenuNpcOverlayDecisionSet? retainedDecisions =
                prior.OverlayDecisions is { } decisions &&
                decisions.PresetSha256 == request.SelectedPreset.SourceHash
                    ? decisions
                    : null;
            var standaloneDestination = new WorkspacePath(Path.Combine(
                candidateRoot.Value.Value, "standalone-assets.json"));
            RaceMenuPresetStandaloneAuthorityWriteResult standalone =
                await standaloneWriter.WriteAsync(
                    new RaceMenuPresetStandaloneAuthorityWriteRequest(
                        plan,
                        recordDraft.Draft,
                        prior.Nam9Authority,
                        retainedDecisions,
                        prior.ExternalTextureAuthorities,
                        standaloneDestination,
                        prior.BodySlidePresetAuthority,
                        prior.BodyMeshAuthority,
                        hasExternalEvidence
                            ? null
                            : prior.ExternalCharGenExportAuthority)
                    {
                        PrivateHeadTextures = prior.PrivateHeadTextures,
                        RetainedPackageAssets = prior.PackageAssets,
                        ExternalHeadPartDependencies =
                            request.ExternalHeadPartDependencies,
                        ExternalHeadPartExclusionAttestations =
                            request.ExternalHeadPartExclusionAttestations
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(standalone.Diagnostics);
            if (!standalone.Written || standalone.Artifact is null ||
                HasErrors(diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuSelectedDependencyManifestAuthority? selectedDependencies = null;
            if (!outputBindingProbe &&
                (hasExternalEvidence ||
                 !request.FaceGenDependencyAuthorities.IsDefaultOrEmpty))
            {
                WorkspacePath dependencyDestination = hasExternalEvidence
                    ? CreateCanonicalDependencyDestination(candidateRoot.Value)
                    : new WorkspacePath(Path.Combine(
                        candidateRoot.Value.Value,
                        "selected-preset-dependencies.json"));
                RaceMenuSelectedDependencyManifestWriteResult dependencies =
                    await selectedDependencyWriter.WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            request.SelectedPreset.SourceHash,
                            recordDraft.Draft,
                            request.FaceGenDependencyAuthorities,
                            standalone.Artifact.ExternalTextures,
                            dependencyDestination)
                         {
                             ExternalProviderSidecars =
                                 request.FaceGenProviderSidecarAuthorities,
                             ExternalInstallDependencies =
                                 request.ExternalInstallDependencies
                         },
                         cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(dependencies.Diagnostics);
                if (!dependencies.Written || dependencies.Artifact is null ||
                    HasErrors(diagnostics))
                    return RefusedWithCleanup(candidateRoot, diagnostics);
                if (hasExternalEvidence &&
                    (dependencies.Artifact.ManifestPath != dependencyDestination ||
                     !dependencies.Artifact.ManifestPath.IsUnder(candidateRoot.Value) ||
                     dependencies.Artifact.SchemaVersion != 3 ||
                     !SelectedExternalGroupsEqual(
                         dependencies.Artifact.ExternalInstallDependencies,
                         request.ExternalInstallDependencies)))
                {
                    diagnostics.Add(Error(
                        "racemenu-selection-selected-manifest-envelope",
                        "The selected dependency writer did not return the canonical schema-3 manifest envelope."));
                    return RefusedWithCleanup(candidateRoot, diagnostics);
                }
                selectedDependencies =
                    new RaceMenuSelectedDependencyManifestAuthority(
                        dependencies.Artifact.DependencyId,
                        dependencies.Artifact.ManifestPath,
                        dependencies.Artifact.ManifestSha256);
                if (hasExternalEvidence)
                {
                    RaceMenuSelectedDependencyManifestReadResult reopenedManifest =
                        await selectedDependencyReader!.ReadAsync(
                            dependencies.Artifact.ManifestPath,
                            dependencies.Artifact.ManifestSha256,
                            candidateRoot.Value,
                            cancellationToken).ConfigureAwait(false);
                    diagnostics.AddRange(reopenedManifest.Diagnostics);
                    if (reopenedManifest.Artifact is not
                            { SchemaVersion: 3 } reopenedArtifact ||
                        reopenedArtifact.ManifestPath !=
                            dependencies.Artifact.ManifestPath ||
                        reopenedArtifact.ManifestSha256 !=
                            dependencies.Artifact.ManifestSha256 ||
                        reopenedArtifact.PresetSha256 !=
                            request.SelectedPreset.SourceHash ||
                        !SelectedExternalGroupsEqual(
                            reopenedArtifact.ExternalInstallDependencies,
                            request.ExternalInstallDependencies))
                    {
                        diagnostics.Add(Error(
                            "racemenu-selection-selected-manifest-readback",
                            "The canonical selected schema-3 manifest did not reopen with the exact transaction external dependency envelope."));
                        return RefusedWithCleanup(candidateRoot, diagnostics);
                    }
                }
            }

            var candidateAuthority = new RaceMenuNpcStandaloneAssetAuthority(
                standalone.Artifact.ManifestPath,
                standalone.Artifact.ManifestSha256);
            RaceMenuNpcStandaloneAuthorityReadResult reopened =
                await standaloneReader.ReadAsync(
                    candidateAuthority, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(reopened.Diagnostics);
            if (!ReopenedAuthorityMatches(
                    reopened, prior.Nam9Authority, prior.PrivateHeadTextures, retainedDecisions,
                    standalone.Artifact, request.ExternalHeadPartDependencies,
                    request.ExternalHeadPartExclusionAttestations, diagnostics))
                return RefusedWithCleanup(candidateRoot, diagnostics);

            RaceMenuNpcAppearancePlanResult finalPlan =
                await planService.AnalyzeAsync(
                    candidateBuild, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(finalPlan.Diagnostics);
            if (!finalPlan.Accepted || finalPlan.Plan is not { IsReady: true } accepted ||
                accepted.BundleManifestSha256 !=
                bundle.Artifact.Bundle.ExpectedManifestSha256 ||
                accepted.RecordAuthoritySha256 !=
                record.Artifact.ManifestSha256 ||
                HasErrors(diagnostics))
            {
                diagnostics.Add(Error("racemenu-selection-final-plan",
                    "The complete candidate did not reopen through the production appearance plan with the same authority hashes."));
                return RefusedWithCleanup(candidateRoot, diagnostics);
            }

            var candidateRequest = new RaceMenuNpcExecutionRequest(
                candidateBuild,
                candidateAuthority)
            {
                ApplyBodySlide = request.ApplyBodySlide,
                AllowInheritedMeshEmbeddedSkinTextureRoute =
                    request.CurrentRequest
                        .AllowInheritedMeshEmbeddedSkinTextureRoute,
                FaceGeomSkeletonAuthority =
                    request.CurrentRequest.FaceGeomSkeletonAuthority,
                SelectedDependencyManifest = selectedDependencies,
                ExternalHeadPartDependencies =
                    standalone.Artifact.ExternalHeadPartDependencies,
                ExternalHeadPartExclusionAttestations =
                    standalone.Artifact.ExternalHeadPartExclusionAttestations,
                ManagerOwnedFaceGeomCarrier =
                    request.ManagerOwnedFaceGeomCarrier,
                JslotOutputBindingProbe = outputBindingProbe
                    ? RaceMenuJslotProbeToken.Instance
                    : null
            };
            return new RaceMenuPresetSelectionTransactionResult(
                true,
                candidateRequest,
                candidateRoot,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            if (candidateRoot is not null &&
                !CleanupCandidate(candidateRoot.Value, diagnostics))
            {
                throw new IOException(
                    $"Preset selection was cancelled, but its incomplete candidate could not be removed: {candidateRoot.Value.Value}",
                    exception);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-selection-transaction", exception.Message));
            return RefusedWithCleanup(candidateRoot, diagnostics);
        }
    }

    private void ValidateRequest(
        RaceMenuPresetSelectionTransactionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        RaceMenuNpcBuildRequest current = request.CurrentRequest.Build;
        if (request.SelectedPreset.Format != PresetFormat.RaceMenuJslot ||
            request.SelectedPreset.Edition != GameEdition.SkyrimSpecialEdition ||
            !request.SelectedPreset.IsValid ||
            request.SelectedPreset.SourceHash != request.Companion.PresetSha256 ||
            current.Edition != GameEdition.SkyrimSpecialEdition ||
            current.References.Race != request.Target.Race ||
            current.Traits.Sex != request.Target.Sex)
        {
            diagnostics.Add(Error("racemenu-selection-input",
                "Selection transaction requires one valid same-hash Skyrim preset for the reviewed request's exact race and sex."));
        }
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, request.Target.DataRoot));
        diagnostics.AddRange(workspacePolicy.Evaluate(
            labRoot, request.CandidateParent));
        if (!request.CandidateParent.IsUnder(labRoot) ||
            request.CandidateParent == labRoot ||
            !Directory.Exists(request.CandidateParent.Value))
        {
            diagnostics.Add(Error("racemenu-selection-candidate-parent",
                "Candidate parent must be one existing K-local transaction directory."));
            return;
        }
        try
        {
            if (HasReparsePath(request.CandidateParent.Value))
                diagnostics.Add(Error("racemenu-selection-candidate-reparse",
                    "Candidate parent traverses a reparse point."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-selection-candidate-inspection",
                exception.Message));
        }
    }

    private static bool HasExternalEvidence(
        RaceMenuPresetSelectionTransactionRequest request) =>
        !request.ExternalHeadPartDependencies.IsDefaultOrEmpty ||
        !request.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty ||
        !request.ExternalInstallDependencies.IsDefaultOrEmpty;

    private static void ValidateExternalEnvelope(
        RaceMenuPresetSelectionTransactionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool outputBindingProbe)
    {
        bool hasDescriptors = !request.ExternalHeadPartDependencies.IsDefaultOrEmpty;
        bool hasAttestations =
            !request.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty;
        if (!hasDescriptors || !hasAttestations ||
            request.ExternalHeadPartDependencies.Length !=
                request.ExternalHeadPartExclusionAttestations.Length)
        {
            diagnostics.Add(Error(
                "racemenu-selection-external-pair",
                "External selection requires paired non-empty descriptor and FaceGeom exclusion-attestation arrays."));
        }
        if (request.ManagerOwnedFaceGeomCarrier is not
                { FaceGeomSha256.Value.Length: 64 } carrier ||
            carrier.FaceGeom.Value.Length == 0)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "External selection requires a non-empty hash-bound Manager-owned FaceGeom carrier."));
        }
        if (!outputBindingProbe &&
            request.ExternalInstallDependencies.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "External selection requires output-plugin-bound schema-3 dependency groups."));
        }

        var descriptorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExternalHeadPartDependencyDescriptor descriptor in
                     request.ExternalHeadPartDependencies)
        {
            try
            {
                _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    descriptor);
                if (!descriptorIds.Add(descriptor.DescriptorId.Value))
                    diagnostics.Add(Error(
                        "racemenu-selection-external-descriptor-duplicate",
                        $"External descriptor '{descriptor.DescriptorId}' occurs more than once."));
            }
            catch (Exception exception) when (exception is InvalidDataException or
                                               ArgumentException or
                                               FormatException)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    exception.Message));
            }
        }
        var attestationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                     request.ExternalHeadPartExclusionAttestations)
        {
            try
            {
                _ = ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                    attestation);
                if (!attestationIds.Add(attestation.DescriptorId.Value) ||
                    !descriptorIds.Contains(attestation.DescriptorId.Value))
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.DescriptorLost,
                        "External FaceGeom exclusion attestations must bind one unique descriptor."));
            }
            catch (Exception exception) when (exception is InvalidDataException or
                                               ArgumentException or
                                               FormatException)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    exception.Message));
            }
        }
        if (descriptorIds.Any(id => !attestationIds.Contains(id)))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "External selection is missing a FaceGeom exclusion attestation for one descriptor."));
        if (!outputBindingProbe && !SelectedExternalGroupsEqual(
                request.ExternalInstallDependencies,
                request.ExternalHeadPartDependencies,
                request.ExternalHeadPartExclusionAttestations))
            diagnostics.Add(Error(
                "racemenu-selection-external-group-binding",
                "Schema-3 external install groups do not match the native descriptor and attestation arrays."));
    }

    private async ValueTask<bool> VerifyManagerCarrierAsync(
        RaceMenuManagerOwnedFaceGeomCarrierAuthority carrier,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
            attestations,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!VerifyManagerCarrierPath(carrier.FaceGeom, diagnostics))
                return false;
            if (!File.Exists(carrier.FaceGeom.Value) ||
                (File.GetAttributes(carrier.FaceGeom.Value) &
                    FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The Manager-owned FaceGeom carrier is missing or is a reparse point."));
                return false;
            }
            await using var stream = new FileStream(
                carrier.FaceGeom.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            Sha256Hash actual = new(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)));
            if (actual != carrier.FaceGeomSha256)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The Manager-owned FaceGeom carrier changed before selection."));
                return false;
            }
            if (attestations.Any(attestation =>
                    attestation.OutputFaceGeomSha256 != actual ||
                    attestation.OutputFaceGeomByteLength != length))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The Manager-owned FaceGeom carrier does not match the native exclusion attestation."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                $"The Manager-owned FaceGeom carrier could not be reopened: {exception.Message}"));
            return false;
        }
    }

    private bool VerifyManagerCarrierPath(
        WorkspacePath carrierPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!carrierPath.IsUnder(labRoot))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "The Manager-owned FaceGeom carrier must remain under the selection lab root."));
            return false;
        }

        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, carrierPath));
        if (HasErrors(diagnostics)) return false;

        string fullPath = Path.GetFullPath(carrierPath.Value);
        string fullRoot = Path.GetFullPath(labRoot.Value);
        string current = fullPath;
        while (true)
        {
            bool exists = File.Exists(current) || Directory.Exists(current);
            if (!exists || (File.GetAttributes(current) &
                FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The Manager-owned FaceGeom carrier path contains a missing or reparse component."));
                return false;
            }
            if (string.Equals(current, fullRoot,
                StringComparison.OrdinalIgnoreCase))
                return true;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The Manager-owned FaceGeom carrier path could not be contained by the selection lab root."));
                return false;
            }
            current = parent;
        }
    }

    private static WorkspacePath CreateCanonicalDependencyDestination(
        WorkspacePath candidateRoot)
    {
        string evidence = Path.Combine(
            candidateRoot.Value, "Data", "NPCManager", "Evidence");
        Directory.CreateDirectory(evidence);
        EnsureOrdinaryDirectory(evidence, "selected dependency evidence directory");
        return new WorkspacePath(Path.Combine(
            evidence, "selected-preset-dependencies.json"));
    }

    private static void EnsureOrdinaryDirectory(string path, string role)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The {role} is not an ordinary directory.");
    }

    private static bool SelectedExternalGroupsEqual(
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> actual,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair =>
            ExternalGroupEqual(pair.First, pair.Second));

    private static bool SelectedExternalGroupsEqual(
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> groups,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation> attestations) =>
        groups.Length == descriptors.Length &&
        groups.Length == attestations.Length &&
        groups.Zip(descriptors).Zip(attestations).All(item =>
            ExternalGroupMatchesEvidence(
                item.First.First, item.First.Second, item.Second));

    private static bool ExternalGroupMatchesEvidence(
        RaceMenuSelectedDependencyManifestExternalInstallDependency group,
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation)
    {
        try
        {
            return ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                       group.Descriptor).AsSpan().SequenceEqual(
                   ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                       descriptor)) &&
                   ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                       group.Attestation).AsSpan().SequenceEqual(
                   ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                       attestation));
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           FormatException)
        {
            return false;
        }
    }

    private static bool ExternalGroupEqual(
        RaceMenuSelectedDependencyManifestExternalInstallDependency left,
        RaceMenuSelectedDependencyManifestExternalInstallDependency right)
    {
        try
        {
            return ExternalGroupMatchesEvidence(
                       left, right.Descriptor, right.Attestation) &&
                   OutputPluginEqual(left.OutputPlugin, right.OutputPlugin) &&
                   left.FaceGeomPath == right.FaceGeomPath &&
                   left.FaceGeomSha256 == right.FaceGeomSha256 &&
                   left.FaceGeomByteLength == right.FaceGeomByteLength;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool OutputPluginEqual(
        RaceMenuSelectedDependencyManifestOutputPluginBinding left,
        RaceMenuSelectedDependencyManifestOutputPluginBinding right) =>
        left.Plugin == right.Plugin && left.Sha256 == right.Sha256 &&
        left.ByteLength == right.ByteLength &&
        left.Masters.SequenceEqual(right.Masters) &&
        left.PnamBindings.SequenceEqual(right.PnamBindings);

    private static bool ExternalDescriptorsEqual(
        ImmutableArray<ExternalHeadPartDependencyDescriptor> actual,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair =>
        {
            try
            {
                return ExternalHeadPartDependencyDescriptorCodec
                    .SerializeDescriptor(pair.First).AsSpan().SequenceEqual(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeDescriptor(pair.Second));
            }
            catch (Exception exception) when (exception is InvalidDataException or
                                               ArgumentException or
                                               FormatException)
            {
                return false;
            }
        });

    private static bool ExternalAttestationsEqual(
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation> actual,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation> expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair =>
        {
            try
            {
                return ExternalHeadPartDependencyDescriptorCodec
                    .SerializeAttestation(pair.First).AsSpan().SequenceEqual(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeAttestation(pair.Second));
            }
            catch (Exception exception) when (exception is InvalidDataException or
                                               ArgumentException or
                                               FormatException)
            {
                return false;
            }
        });

    private WorkspacePath CreateCandidateRoot(
        RaceMenuPresetSelectionTransactionRequest request)
    {
        string hash = request.SelectedPreset.SourceHash.Value[..16].ToLowerInvariant();
        var root = new WorkspacePath(Path.Combine(
            request.CandidateParent.Value,
            $"racemenu-selection-{hash}-{Guid.NewGuid():N}"));
        if (!root.IsUnder(request.CandidateParent) || !root.IsUnder(labRoot))
            throw new InvalidDataException("Candidate root escaped its owned parent.");
        Directory.CreateDirectory(root.Value);
        if (File.GetAttributes(root.Value).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Created candidate root is a reparse point.");
        return root;
    }

    private static bool ReopenedAuthorityMatches(
        RaceMenuNpcStandaloneAuthorityReadResult reopened,
        RaceMenuNpcNam9TrailingAuthority expectedNam9,
        SkyrimPrivateHeadTexturePaths expectedHeadTextures,
        RaceMenuNpcOverlayDecisionSet? expectedDecisions,
        RaceMenuPresetStandaloneAuthorityArtifact artifact,
        ImmutableArray<ExternalHeadPartDependencyDescriptor>
            expectedDescriptors,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
            expectedAttestations,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!reopened.Accepted || reopened.Assets is not
            {
                FinalOutputAuthority: null,
                FaceBakeAuthority: null,
                FaceTextureBakeAuthority: null
            } assets ||
            assets.SchemaVersion !=
                (!expectedDescriptors.IsDefaultOrEmpty
                    ? 8
                    : artifact.BodyMeshAuthority is null ? 6 : 7) ||
            assets.Nam9Authority != expectedNam9 ||
            assets.PrivateHeadTextures != expectedHeadTextures ||
            !OverlayDecisionsEqual(assets.OverlayDecisions, expectedDecisions) ||
            assets.FaceTintWidth != artifact.FaceTintWidth ||
            assets.FaceTintHeight != artifact.FaceTintHeight ||
            !assets.ExternalTextureAuthorities.SequenceEqual(
                artifact.ExternalTextures) ||
            !BodySlideAuthorityEqual(
                assets.BodySlidePresetAuthority,
                artifact.BodySlidePresetAuthority) ||
            !BodyMeshAuthorityEqual(
                assets.BodyMeshAuthority,
                artifact.BodyMeshAuthority) ||
            assets.ExternalCharGenExportAuthority !=
                artifact.ExternalCharGenExportAuthority ||
            !ExternalDescriptorsEqual(
                assets.ExternalHeadPartDependencies,
                expectedDescriptors) ||
            !ExternalAttestationsEqual(
                assets.ExternalHeadPartExclusionAttestations,
                expectedAttestations))
        {
            diagnostics.Add(Error("racemenu-selection-standalone-readback",
                "The generated direct-CharGen standalone authority changed during production readback."));
            return false;
        }
        return !HasErrors(diagnostics);
    }

    private static bool BodySlideAuthorityEqual(
        RaceMenuNpcBodySlidePresetAuthority? left,
        RaceMenuNpcBodySlidePresetAuthority? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) =>
                first.ManifestPath == second.ManifestPath &&
                first.ManifestSha256 == second.ManifestSha256 &&
                first.AuthorityId == second.AuthorityId &&
                first.PresetXml == second.PresetXml &&
                first.PresetXmlSha256 == second.PresetXmlSha256 &&
                first.PresetName == second.PresetName &&
                first.SliderSet == second.SliderSet &&
                first.SliderCount == second.SliderCount &&
                first.RuntimeAuthority == second.RuntimeAuthority &&
                first.Groups.SequenceEqual(second.Groups),
            _ => false
        };

    private static bool BodyMeshAuthorityEqual(
        RaceMenuNpcBodyMeshAuthority? left,
        RaceMenuNpcBodyMeshAuthority? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) =>
                first.ManifestPath == second.ManifestPath &&
                first.ManifestSha256 == second.ManifestSha256 &&
                first.AuthorityId == second.AuthorityId &&
                first.BodySlidePresetAuthority ==
                    second.BodySlidePresetAuthority &&
                first.RuntimeAuthority == second.RuntimeAuthority &&
                first.Meshes.SequenceEqual(second.Meshes),
            _ => false
        };

    private static bool OverlayDecisionsEqual(
        RaceMenuNpcOverlayDecisionSet? left,
        RaceMenuNpcOverlayDecisionSet? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) =>
                first.ManifestPath == second.ManifestPath &&
                first.ManifestSha256 == second.ManifestSha256 &&
                first.PresetSha256 == second.PresetSha256 &&
                first.Decisions.SequenceEqual(second.Decisions),
            _ => false
        };

    private RaceMenuPresetSelectionTransactionResult RefusedWithCleanup(
        WorkspacePath? candidateRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (candidateRoot is null ||
            CleanupCandidate(candidateRoot.Value, diagnostics))
            return Refused(null, diagnostics);

        return Refused(candidateRoot, diagnostics);
    }

    private bool CleanupCandidate(
        WorkspacePath candidateRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!candidateRoot.IsUnder(labRoot))
            {
                diagnostics.Add(Error("racemenu-selection-cleanup-boundary",
                    $"Refused to remove an incomplete candidate outside the lab root: {candidateRoot.Value}"));
                return false;
            }
            if (!Directory.Exists(candidateRoot.Value)) return true;
            if (HasReparsePath(candidateRoot.Value))
            {
                diagnostics.Add(Error("racemenu-selection-cleanup-reparse",
                    $"Refused to traverse a reparse point while removing the incomplete candidate: {candidateRoot.Value}"));
                return false;
            }
            Directory.Delete(candidateRoot.Value, recursive: true);
            if (!Directory.Exists(candidateRoot.Value)) return true;

            diagnostics.Add(Error("racemenu-selection-cleanup-retained",
                $"The incomplete candidate still exists after cleanup: {candidateRoot.Value}"));
            return false;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-selection-cleanup-failed",
                $"Could not remove the incomplete candidate {candidateRoot.Value}: {exception.Message}"));
            return false;
        }
    }

    private bool HasReparsePath(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, labRoot.Value,
                    StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) return true;
            current = parent;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuPresetSelectionTransactionResult Refused(
        WorkspacePath? candidateRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, candidateRoot, diagnostics.ToImmutable());
}
