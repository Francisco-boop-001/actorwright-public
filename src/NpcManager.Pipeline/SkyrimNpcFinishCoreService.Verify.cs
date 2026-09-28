using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private async ValueTask<SkyrimNpcFinishCoreVerificationResult>
        VerifyExternalAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            ExternalHeadPartInstallVerificationContext? installContext,
            bool requireCurrentAuthority,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifestPath.IsUnder(projectRoot) ||
                !File.Exists(manifestPath.Value) ||
                HasReparsePoint(manifestPath.Value))
                return RefusedVerification(
                    "finish-core-verify-manifest-path",
                    "The manifest must be an existing K-local ordinary file.");
            byte[] manifestBytes = await File.ReadAllBytesAsync(
                manifestPath.Value,
                cancellationToken);
            Sha256Hash rawManifestSha = HashBytes(manifestBytes);
            Sha256Hash canonicalManifestSha = HashBytes(
                SkyrimNpcFinishCoreDocumentCodec.CanonicalizeManifest(manifestBytes, projectRoot));
            if (rawManifestSha != manifestSha256 && canonicalManifestSha != manifestSha256)
                return RefusedVerification(
                    "finish-core-verify-manifest-hash",
                    $"Manifest hash binding failed: expectedSha256={manifestSha256.Value}; " +
                    $"receivedSha256={rawManifestSha.Value}; canonicalSha256={canonicalManifestSha.Value}.");
            SkyrimNpcFinishCoreManifest manifest =
                SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                    manifestBytes,
                    projectRoot);
            if (manifest.Schema != SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier ||
                manifest.ExternalHeadParts is null)
                return RefusedVerification(
                    "finish-core-verify-schema",
                    "The manifest is not an external Finish Core v2 document.");
            SkyrimNpcFinishCoreExternalHeadPartManifestAuthority externalManifest =
                manifest.ExternalHeadParts;
            if (manifest.RuntimeAuthority || manifest.VisualAuthority ||
                manifest.PlacementIncluded)
                return RefusedVerification(
                    "finish-core-verify-boundary",
                    "External Finish Core verification cannot claim placement, runtime, or visual authority.");
            if (manifest.PackageRoot is not { } packageRoot ||
                manifest.Archive is not { } archivePath ||
                manifest.Plugin is not { } plugin ||
                manifest.PluginSha256 is not { } pluginSha ||
                manifest.BaseNpc is not { } baseNpc ||
                manifest.RequestSha256 is not { } requestSha ||
                manifest.ProposalSha256 is not { } proposalSha ||
                manifest.PackageTreeSha256 is not { } packageTree ||
                manifest.SourcePackageTreeSha256 is not { } sourceTree)
                return RefusedVerification(
                    "finish-core-verify-fields",
                    "The external Finish manifest is missing a required hash-bound field.");

            string evidenceRoot = Path.Combine(
                packageRoot.Value,
                "NPCManager",
                "Evidence");
            string requestPath = Path.Combine(evidenceRoot, "finish-core-request.json");
            string proposalPath = Path.Combine(evidenceRoot, "finish-core-proposal.json");
            string verificationPath = Path.Combine(evidenceRoot, "finish-core-verification.json");
            foreach (string path in new[]
                     {
                         requestPath,
                         proposalPath,
                         verificationPath,
                         Path.Combine(evidenceRoot, "runtime-identities.json")
                     })
            {
                if (!File.Exists(path) || HasReparsePoint(path))
                    diagnostics.Add(new Diagnostic(
                        "finish-core-verify-evidence",
                        DiagnosticSeverity.Error,
                        $"Required external Finish evidence file '{path}' is missing or unsafe."));
            }
            if (HasErrors(diagnostics))
                return RefusedVerification(diagnostics);
            SkyrimNpcFinishCoreRequest request =
                SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                    await File.ReadAllBytesAsync(requestPath, cancellationToken),
                    projectRoot);
            SkyrimNpcFinishCoreProposal proposal =
                SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                    await File.ReadAllBytesAsync(proposalPath, cancellationToken),
                    projectRoot);
            if (request.Schema != SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier ||
                proposal.Schema != SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier ||
                proposal.Request is null ||
                proposal.RequestSha256 != requestSha ||
                proposal.ProposalSha256 != proposalSha ||
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, projectRoot) != requestSha ||
                !SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                        proposal.Request, projectRoot).AsSpan().SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                        request, projectRoot)) ||
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        proposal with { ProposalSha256 = null }, projectRoot)) != proposalSha ||
                request.Source.Plugin != plugin ||
                request.Source.PackageTreeSha256 != sourceTree ||
                baseNpc != new FormReference(plugin, request.Actor.FormId!.Value) ||
                request.Output.Root != packageRoot)
                return RefusedVerification(
                    "finish-core-verify-document-binding",
                    "External Finish request, proposal, and manifest bindings differ.");
            if (manifest.Evidence.PackageTreeSha256 != packageTree ||
                manifest.Evidence.SourcePackageTreeSha256 != sourceTree)
                return RefusedVerification(
                    "finish-core-verify-evidence-binding",
                    "External manifest evidence top-level tree bindings differ from the manifest authority.");
            if (!await VerifyManifestEvidenceAsync(
                    manifest.Evidence.Files,
                    packageRoot.Value,
                    BuildCanonicalEvidencePaths(request, external: true),
                    diagnostics,
                    cancellationToken))
                return RefusedVerification(diagnostics);
            if (!await VerifyInheritedEvidenceAsync(
                    manifest.Evidence.Inherited,
                    packageRoot.Value,
                    request.Source.PackageTreeSha256,
                    diagnostics,
                    cancellationToken))
                return RefusedVerification(diagnostics);
            if (!VerifyPhysicalEvidenceClosure(
                    packageRoot.Value,
                    external: true,
                    BuildCanonicalEvidencePaths(request, external: true),
                    manifest.Evidence.Inherited.Select(entry => entry.Path.Value).ToImmutableArray(),
                    diagnostics))
                return RefusedVerification(diagnostics);
            if (proposal.ExternalHeadParts is null ||
                proposal.ExternalHeadParts.Authority.SelectedManifestPath !=
                    externalManifest.SelectedManifestPath)
                return RefusedVerification(
                    "finish-core-verify-external-binding",
                    "External selected-dependency locator differs between proposal and manifest.");

            if (request.Authorities.ExternalHeadParts is not { } sourceAuthority)
                return RefusedVerification(
                    "finish-core-verify-source-binding",
                    "External Finish selected-dependency authority is missing.");
            if (externalManifest.SelectedManifestPath != sourceAuthority.SelectedManifestPath ||
                externalManifest.SelectedManifestSha256 != sourceAuthority.SelectedManifestSha256 ||
                externalManifest.PromotedOutputBindingPath.Value !=
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath)
                return RefusedVerification(
                    "finish-core-verify-source-binding",
                    "External Finish source authority is not bound to the package manifest.");
            SkyrimNpcFinishCoreExternalHeadPartProposalAuthority proposalAuthority =
                proposal.ExternalHeadParts;
            ImmutableArray<Sha256Hash> manifestAttestationHashes =
                externalManifest.Attestations
                    .Select(item => item.AttestationSha256)
                    .ToImmutableArray();
            ImmutableArray<Sha256Hash> requestBindingAttestationHashes =
                sourceAuthority.Bindings
                    .Select(item => item.FaceGeomExclusionAttestationSha256)
                    .ToImmutableArray();
            ImmutableArray<Sha256Hash> proposalBindingAttestationHashes =
                proposalAuthority.Authority.Bindings
                    .Select(item => item.FaceGeomExclusionAttestationSha256)
                    .ToImmutableArray();
            if (proposalAuthority.Authority.SelectedManifestPath != sourceAuthority.SelectedManifestPath ||
                proposalAuthority.Authority.SelectedManifestSha256 != sourceAuthority.SelectedManifestSha256 ||
                !proposalAuthority.Authority.Bindings.SequenceEqual(sourceAuthority.Bindings) ||
                !requestBindingAttestationHashes.SequenceEqual(manifestAttestationHashes) ||
                !proposalBindingAttestationHashes.SequenceEqual(manifestAttestationHashes) ||
                !proposal.ExternalHeadParts.Verification.DescriptorIds.SequenceEqual(
                    externalManifest.Descriptors.Select(item => item.DescriptorId)) ||
                proposal.ExternalHeadParts.Verification.CurrentInstallDependencyState !=
                    ExternalInstallDependencyState.Verified ||
                !proposal.ExternalHeadParts.Verification.InstallReady ||
                !proposal.ExternalHeadParts.Verification.InstallDependencyAuthority ||
                proposal.ExternalHeadParts.ContextFingerprint is null)
                return RefusedVerification(
                    "finish-core-verify-proposal-authority",
                    "The persisted proposal external authority is not bound to the request and manifest.");

            string selectedPath = Path.Combine(
                packageRoot.Value,
                SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath
                    .Replace('/', Path.DirectorySeparatorChar));
            string bindingPath = Path.Combine(
                packageRoot.Value,
                SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath
                    .Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(selectedPath) || HasReparsePoint(selectedPath) ||
                !File.Exists(bindingPath) || HasReparsePoint(bindingPath))
                return RefusedVerification(
                    "finish-core-verify-external-evidence",
                    "The package canonical selected manifest or promoted binding is missing or unsafe.");
            FileInfo selectedInfoBefore = new(selectedPath);
            byte[] selectedBytes = await File.ReadAllBytesAsync(selectedPath, cancellationToken);
            FileInfo selectedInfoAfter = new(selectedPath);
            if (selectedInfoBefore.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                selectedInfoBefore.Attributes.HasFlag(FileAttributes.Device) ||
                selectedInfoAfter.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                selectedInfoAfter.Attributes.HasFlag(FileAttributes.Device) ||
                selectedInfoBefore.Length != selectedInfoAfter.Length ||
                selectedBytes.LongLength != selectedInfoAfter.Length ||
                HashBytes(selectedBytes) != externalManifest.SelectedManifestSha256)
                return RefusedVerification(
                    "finish-core-verify-selected-manifest-hash",
                    "The package canonical selected dependency manifest hash drifted.");
            FileInfo bindingInfoBefore = new(bindingPath);
            byte[] bindingBytes = await File.ReadAllBytesAsync(bindingPath, cancellationToken);
            FileInfo bindingInfoAfter = new(bindingPath);
            if (bindingInfoBefore.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                bindingInfoBefore.Attributes.HasFlag(FileAttributes.Device) ||
                bindingInfoAfter.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                bindingInfoAfter.Attributes.HasFlag(FileAttributes.Device) ||
                bindingInfoBefore.Length != bindingInfoAfter.Length ||
                bindingBytes.LongLength != bindingInfoAfter.Length ||
                HashBytes(bindingBytes) != externalManifest.PromotedOutputBindingSha256)
                return RefusedVerification(
                    "finish-core-verify-promoted-binding-hash",
                    "The package promoted output binding hash drifted.");
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding promotedBinding =
                SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(bindingBytes);
            if (promotedBinding.SourceSelectedManifestSha256 !=
                    externalManifest.SelectedManifestSha256)
                return RefusedVerification(
                    "finish-core-verify-promoted-source-binding",
                    "The promoted output binding does not retain the canonical source hash.");

            RaceMenuSelectedDependencyManifestReadResult selected =
                await new RaceMenuSelectedDependencyManifestReader().ReadAsync(
                    new WorkspacePath(selectedPath),
                    externalManifest.SelectedManifestSha256,
                    packageRoot,
                    cancellationToken);
            diagnostics.AddRange(selected.Diagnostics);
            byte[] selectedBytesAfterRead = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);
            if (!selectedBytes.AsSpan().SequenceEqual(selectedBytesAfterRead))
                return RefusedVerification(
                    "finish-core-verify-selected-manifest-toctou",
                    "The selected dependency manifest changed while it was being verified.");
            if (selected.Artifact is null ||
                selected.Artifact.SchemaVersion != 3 ||
                selected.Artifact.ExternalInstallDependencies.IsDefaultOrEmpty)
                return RefusedVerification(
                    "finish-core-verify-selected-manifest",
                    "The package canonical selected dependency manifest is not an admitted schema-3 document.");
            ImmutableArray<Sha256Hash> selectedIds = selected.Artifact.ExternalInstallDependencies
                .Select(item => item.Descriptor.DescriptorId).ToImmutableArray();
            if (!selectedIds.SequenceEqual(externalManifest.Descriptors.Select(item => item.DescriptorId)) ||
                !selected.Artifact.ExternalInstallDependencies.Select(item => item.Attestation.AttestationSha256)
                    .SequenceEqual(externalManifest.Attestations.Select(item => item.AttestationSha256)) ||
                !selected.Artifact.ExternalInstallDependencies.Select(item =>
                    Convert.ToHexString(ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(item.Descriptor)))
                    .SequenceEqual(externalManifest.Descriptors.Select(item =>
                        Convert.ToHexString(ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(item)))) ||
                !selected.Artifact.ExternalInstallDependencies.Select(item =>
                    Convert.ToHexString(ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(item.Attestation)))
                    .SequenceEqual(externalManifest.Attestations.Select(item =>
                        Convert.ToHexString(ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(item)))) ||
                !selectedIds.SequenceEqual(promotedBinding.Groups.Select(item => item.DescriptorId)))
                return RefusedVerification(
                    "finish-core-verify-external-content",
                    "The package selected groups, manifest authority, and promoted binding differ.");

            string pluginRelativePath = RequirePackageRelativePluginPath(request);
            string outputPluginPath = Path.Combine(
                packageRoot.Value,
                pluginRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(outputPluginPath) || HashFile(outputPluginPath) != pluginSha)
                return RefusedVerification(
                    "finish-core-verify-plugin-hash",
                    "The promoted external output plugin is missing or hash-drifted.");

            var externalDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            ExternalHeadPartInstallVerificationResult promotedResult =
                externalPromotedOutputVerifier is null
                    ? throw new InvalidDataException(
                        "External Finish Core promoted output verification is unavailable.")
                    : await externalPromotedOutputVerifier.VerifyPromotedOutputAsync(
                        new ExternalHeadPartPromotedOutputVerificationRequest(
                            packageRoot,
                            new WorkspacePath(outputPluginPath),
                            plugin,
                            pluginSha,
                            new WorkspacePath(selectedPath),
                            externalManifest.SelectedManifestSha256,
                            new WorkspacePath(bindingPath),
                            externalManifest.PromotedOutputBindingSha256,
                            installContext,
                            requireCurrentAuthority,
                            request.Actor.FormId),
                        cancellationToken);
            diagnostics.AddRange(promotedResult.Diagnostics);
            if (!promotedResult.DescriptorClosureValid ||
                (!requireCurrentAuthority &&
                 (promotedResult.Artifact.CurrentInstallDependencyState !=
                      ExternalInstallDependencyState.DeclaredUnverified ||
                  promotedResult.Artifact.InstallReady ||
                  promotedResult.Artifact.InstallDependencyAuthority ||
                  promotedResult.Artifact.VerifiedInstallSnapshot is not null)) ||
                (requireCurrentAuthority &&
                 (promotedResult.Artifact.CurrentInstallDependencyState !=
                      ExternalInstallDependencyState.Verified ||
                  !promotedResult.Artifact.InstallReady ||
                  !promotedResult.Artifact.InstallDependencyAuthority ||
                  promotedResult.Artifact.VerifiedInstallSnapshot is null)))
                return RefusedVerification(diagnostics);
            ExternalFinishCoreState currentState = new(
                new ExternalHeadPartInstallVerificationResult(
                    promotedResult.DescriptorClosureValid,
                    promotedResult.Artifact,
                    promotedResult.Diagnostics),
                new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
                    sourceAuthority,
                    promotedResult.Artifact,
                    promotedResult.Artifact.VerifiedInstallSnapshot?.ContextFingerprint),
                externalManifest.Descriptors,
                externalManifest.Attestations,
                selected.Artifact);
            diagnostics.AddRange(externalDiagnostics);

            ExternalHeadPartVerifiedInstallSnapshot snapshot =
                externalManifest.VerifiedInstallSnapshot;
            if (snapshot.SelectedManifestSha256 !=
                    externalManifest.SelectedManifestSha256 ||
                !snapshot.DescriptorIds.SequenceEqual(
                    externalManifest.Descriptors.Select(item => item.DescriptorId)) ||
                (requireCurrentAuthority &&
                 currentState.Result.Artifact.VerifiedInstallSnapshot is not
                    ExternalHeadPartVerifiedInstallSnapshot))
                return RefusedVerification(
                    "finish-core-verify-external-snapshot",
                    "The persisted external install snapshot is incomplete or unbound.");
            if (proposal.ExternalHeadParts.ContextFingerprint is not
                    ExternalHeadPartInstallContextFingerprint proposalFingerprint ||
                !FingerprintsEqual(proposalFingerprint, snapshot.ContextFingerprint))
                return RefusedVerification(
                    "finish-core-verify-proposal-fingerprint",
                    "The persisted proposal fingerprint differs from the historical apply snapshot.");

            bool currentMatches = currentState.Result.Artifact
                .VerifiedInstallSnapshot is ExternalHeadPartVerifiedInstallSnapshot currentSnapshot &&
                FingerprintsEqual(snapshot.ContextFingerprint, currentSnapshot.ContextFingerprint);
            if (requireCurrentAuthority && !currentMatches)
            {
                diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    DiagnosticSeverity.Error,
                    "The current external install fingerprint differs from the historical apply snapshot."));
                return RefusedVerification(diagnostics);
            }

            ImmutableArray<Diagnostic> outputClosure =
                BethesdaSkyrimNpcFinishCoreVerifier
                    .VerifyExternalHeadPartOutputClosure(
                        new WorkspacePath(outputPluginPath),
                        plugin,
                        request.Actor.FormId!.Value,
                        currentState.Result.Artifact.ProviderObservations,
                        externalManifest.Descriptors
                            .SelectMany(item => item.Members)
                            .ToImmutableArray(),
                        cancellationToken,
                        promotedBinding.MasterOrder,
                        promotedBinding.DeclaredExternalPnam);
            diagnostics.AddRange(outputClosure);
            if (HasErrors(outputClosure))
                return RefusedVerification(diagnostics);

            ImmutableDictionary<string, int> emptyForbidden =
                ImmutableDictionary<string, int>.Empty;
            Sha256Hash observedPackageTree = ComputeFinishOutputTree(
                packageRoot.Value, pluginRelativePath, generatedManifest: false);
            if (observedPackageTree != packageTree)
            {
                observedPackageTree = ComputeFinishOutputTree(
                    packageRoot.Value, pluginRelativePath, generatedManifest: true);
                if (observedPackageTree == packageTree &&
                    !VerifyFinishedPackageManifest(request, packageRoot.Value, diagnostics, cancellationToken))
                    return RefusedVerification(diagnostics);
            }
            if (observedPackageTree != packageTree)
                return RefusedVerification(
                    "finish-core-verify-package-tree",
                    $"The external Finish package tree differs from its manifest authority (expected {packageTree.Value}, observed {observedPackageTree.Value}).");
            if (!await VerifyArchiveAsync(
                    archivePath.Value,
                    packageRoot.Value,
                    cancellationToken))
                return RefusedVerification(
                    "finish-core-verify-archive",
                    "The external Finish archive failed independent readback.");

            ExternalHeadPartInstallVerificationArtifact artifact =
                currentState.Result.Artifact with
                {
                    HistoricalSnapshotValid = true,
                    VerifiedInstallSnapshot = snapshot,
                    RuntimeAuthority = false,
                    VisualAuthority = false
                };
            var verification = new SkyrimNpcFinishCoreVerification
            {
                Schema = SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
                Status = requireCurrentAuthority
                    ? SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired
                    : SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
                Verified = true,
                PlacementIncluded = false,
                RuntimeAuthority = false,
                VisualAuthority = false,
                TypedForbiddenCounts = emptyForbidden,
                RawForbiddenCounts = emptyForbidden,
                PluginSha256 = pluginSha,
                PackageTreeSha256 = packageTree,
                SourcePackageTreeSha256 = sourceTree,
                // The physical ZIP hash remains non-authoritative for the
                // external v2 contract; VerifyArchiveAsync establishes exact
                // logical member closure.
                ArchiveSha256 = null,
                RuntimeIdentity = manifest.RuntimeIdentity,
                ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartVerification(
                    artifact)
            };
            return new SkyrimNpcFinishCoreVerificationResult(
                true,
                verification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
                JsonException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-exception",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedVerification(diagnostics);
        }
    }

    private async ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyTransactionAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifestPath.IsUnder(projectRoot) || !File.Exists(manifestPath.Value) ||
                HasReparsePoint(manifestPath.Value))
                return RefusedVerification(
                    "finish-core-verify-manifest-path",
                    "The manifest must be an existing K-local ordinary file.");
            byte[] bytes = await File.ReadAllBytesAsync(manifestPath.Value, cancellationToken);
            Sha256Hash actualManifestSha = HashBytes(bytes);
            Sha256Hash canonicalManifestSha = HashBytes(
                SkyrimNpcFinishCoreDocumentCodec.CanonicalizeManifest(bytes, projectRoot));
            if (actualManifestSha != manifestSha256 && canonicalManifestSha != manifestSha256)
                return RefusedVerification(
                    "finish-core-verify-manifest-hash",
                    $"Manifest hash binding failed: expectedSha256={manifestSha256.Value}; " +
                    $"receivedSha256={actualManifestSha.Value}; canonicalSha256={canonicalManifestSha.Value}.");
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                RequiredString(root, "schema") != SkyrimNpcFinishCoreManifest.SchemaIdentifier)
                return RefusedVerification(
                    "finish-core-verify-schema",
                    "The manifest schema is not npc.finish-core.manifest.v1.");
            bool placementIncluded = RequiredBoolean(root, "placementIncluded");
            bool runtimeAuthority = RequiredBoolean(root, "runtimeAuthority");
            bool visualAuthority = RequiredBoolean(root, "visualAuthority");
            if (placementIncluded || runtimeAuthority || visualAuthority)
                return RefusedVerification(
                    "finish-core-verify-boundary",
                    "Finish Core verification requires placementIncluded, runtimeAuthority, and visualAuthority to remain false.");

            PluginName plugin = new(RequiredString(root, "plugin"));
            Sha256Hash pluginSha = new(RequiredString(root, "pluginSha256"));
            FormReference baseNpc = ParseFormReference(RequiredString(root, "baseNpc"));
            Sha256Hash requestSha = new(RequiredString(root, "requestSha256"));
            Sha256Hash proposalSha = new(RequiredString(root, "proposalSha256"));
            WorkspacePath packageRoot = ParsePath(root, "packageRoot");
            WorkspacePath archivePath = ParsePath(root, "archive");
            Sha256Hash packageTree = new(RequiredString(root, "packageTreeSha256"));
            Sha256Hash sourceTree = new(RequiredString(root, "sourcePackageTreeSha256"));
            SkyrimNpcFinishCoreManifest parsedManifest =
                SkyrimNpcFinishCoreDocumentCodec.ParseManifest(bytes, projectRoot);

            string evidenceRoot = Path.Combine(packageRoot.Value, "NPCManager", "Evidence");
            string requestPath = Path.Combine(evidenceRoot, "finish-core-request.json");
            string proposalPath = Path.Combine(evidenceRoot, "finish-core-proposal.json");
            string verificationPath = Path.Combine(evidenceRoot, "finish-core-verification.json");
            foreach (string path in new[] { requestPath, proposalPath, verificationPath,
                                             Path.Combine(evidenceRoot, "runtime-identities.json") })
                if (!File.Exists(path) || HasReparsePoint(path))
                    diagnostics.Add(new Diagnostic(
                        "finish-core-verify-evidence",
                        DiagnosticSeverity.Error,
                        $"Required evidence file '{path}' is missing or unsafe."));
            if (HasErrors(diagnostics))
                return RefusedVerification(diagnostics);

            SkyrimNpcFinishCoreRequest request = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                await File.ReadAllBytesAsync(requestPath, cancellationToken), projectRoot);
            SkyrimNpcFinishCoreProposal proposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                await File.ReadAllBytesAsync(proposalPath, cancellationToken), projectRoot);
            if (parsedManifest.Evidence.PackageTreeSha256 != packageTree ||
                parsedManifest.Evidence.SourcePackageTreeSha256 != sourceTree)
                return RefusedVerification(
                    "finish-core-verify-evidence-binding",
                    "Manifest evidence top-level tree bindings differ from the manifest authority.");
            if (!await VerifyManifestEvidenceAsync(
                    parsedManifest.Evidence.Files,
                    packageRoot.Value,
                    BuildCanonicalEvidencePaths(request, external: false),
                    diagnostics,
                    cancellationToken))
                return RefusedVerification(diagnostics);
            if (!await VerifyInheritedEvidenceAsync(
                    parsedManifest.Evidence.Inherited,
                    packageRoot.Value,
                    request.Source.PackageTreeSha256,
                    diagnostics,
                    cancellationToken))
                return RefusedVerification(diagnostics);
            if (!VerifyPhysicalEvidenceClosure(
                    packageRoot.Value,
                    external: false,
                    BuildCanonicalEvidencePaths(request, external: false),
                    parsedManifest.Evidence.Inherited.Select(entry => entry.Path.Value).ToImmutableArray(),
                    diagnostics))
                return RefusedVerification(diagnostics);
            if (SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, projectRoot) != requestSha ||
                proposal.ProposalSha256 != proposalSha || proposal.RequestSha256 != requestSha ||
                proposal.Request is null || baseNpc != new FormReference(plugin, request.Actor.FormId!.Value))
                return RefusedVerification(
                    "finish-core-verify-document-binding",
                    "Request, proposal, manifest, or base-NPC bindings differ.");
            if (request.Source.Plugin != plugin || request.Output.Root is null ||
                Path.GetFullPath(request.Output.Root.Value.Value) != Path.GetFullPath(packageRoot.Value))
                return RefusedVerification(
                    "finish-core-verify-output-binding",
                    "The request output root does not match the promoted package root.");
            string pluginRelativePath = RequirePackageRelativePluginPath(request);
            string outputPluginPath = Path.Combine(
                packageRoot.Value,
                pluginRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(outputPluginPath))
                return RefusedVerification(
                    "finish-core-verify-plugin-missing",
                    "The promoted output plugin is missing from its manifest-bound package-relative path.");
            if (HashFile(outputPluginPath) != pluginSha)
                return RefusedVerification(
                    "finish-core-verify-plugin-hash",
                    "The promoted output plugin hash differs from the manifest.");

            ImmutableArray<Diagnostic> sourceDiagnostics = ValidateTransactionPathsForVerify(request);
            diagnostics.AddRange(sourceDiagnostics.Where(item => item.Code.Contains("source", StringComparison.OrdinalIgnoreCase)));
            if (request.Source.PluginPath is not { } sourcePlugin ||
                request.SandboxAuthority.CopiedMaster is not { } copiedMaster)
                return RefusedVerification(
                    "finish-core-verify-source-fields",
                    "The retained request does not bind source plugin and copied master.");

            SkyrimNpcFinishCoreSourceReadResult source = await inspectSource(
                request, cancellationToken);
            if (!source.Admitted)
                return RefusedVerification(source.Diagnostics);

            ImmutableArray<Diagnostic> requestDiagnostics =
                ValidateSourceAndRequest(request, source);
            if (HasErrors(requestDiagnostics))
                return RefusedVerification(requestDiagnostics);

            SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source);
            if (!masterPlan.Admitted)
                return RefusedVerification(masterPlan.Diagnostics);

            ImmutableArray<string> plannedMasterOrder = masterPlan.MasterOrder
                .Select(value => value.Value)
                .ToImmutableArray();
            ImmutableArray<string> plannedAppendedMasters = masterPlan.AppendedMasters
                .Select(value => value.Value)
                .ToImmutableArray();
            if (!proposal.MasterOrder.SequenceEqual(plannedMasterOrder) ||
                !proposal.AppendedMasters.SequenceEqual(plannedAppendedMasters))
                return RefusedVerification(
                    "finish-core-verify-master-plan",
                    "The persisted proposal master plan differs from fresh physical source and additional-master authority admission.");

            BethesdaSkyrimNpcFinishCoreVerification binary =
                new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                    sourcePlugin, new WorkspacePath(outputPluginPath), proposal, copiedMaster, cancellationToken);
            diagnostics.AddRange(binary.Diagnostics);
            if (!binary.Verified)
                return RefusedVerification(diagnostics);
            Sha256Hash outputTree = ComputeFinishOutputTree(
                packageRoot.Value, pluginRelativePath, generatedManifest: false);
            if (outputTree != packageTree)
            {
                outputTree = ComputeFinishOutputTree(
                    packageRoot.Value, pluginRelativePath, generatedManifest: true);
                if (outputTree == packageTree &&
                    !VerifyFinishedPackageManifest(request, packageRoot.Value, diagnostics, cancellationToken))
                    return RefusedVerification(diagnostics);
            }
            Sha256Hash historicalSourceTree = ComputeTreeHash(
                request.Source.PackageRoot!.Value.Value,
                relative => string.Equals(
                    relative,
                    pluginRelativePath,
                    StringComparison.OrdinalIgnoreCase));
            if (outputTree != packageTree || historicalSourceTree != sourceTree)
                return RefusedVerification(
                    "finish-core-verify-package-tree",
                    "The retained package sidecar tree differs from the manifest authority.");
            if (!await VerifyArchiveAsync(archivePath.Value, packageRoot.Value, cancellationToken))
                return RefusedVerification(
                    "finish-core-verify-archive",
                    "The direct-install archive failed independent readback.");

            var verification = new SkyrimNpcFinishCoreVerification
            {
                Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
                Verified = true,
                PlacementIncluded = false,
                RuntimeAuthority = false,
                VisualAuthority = false,
                TypedForbiddenCounts = binary.TypedForbiddenCounts,
                RawForbiddenCounts = binary.RawForbiddenCounts,
                PluginSha256 = pluginSha,
                PackageTreeSha256 = outputTree,
                SourcePackageTreeSha256 = sourceTree,
                ArchiveSha256 = HashFile(archivePath.Value),
                RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
                {
                    BaseNpc = baseNpc,
                    PlacedReference = null,
                    PlacementIncluded = false
                }
            };
            return new SkyrimNpcFinishCoreVerificationResult(
                true, verification, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
                JsonException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-exception",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedVerification(diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> ValidateTransactionPathsForVerify(
        SkyrimNpcFinishCoreRequest request) =>
        request.Source.PackageRoot is { } sourceRoot && sourceRoot.IsUnder(projectRoot)
            ? ImmutableArray<Diagnostic>.Empty
            : [new Diagnostic(
                "finish-core-verify-source-path",
                DiagnosticSeverity.Error,
                "The retained source package root is outside the K-local project.")];

    private static async ValueTask<bool> VerifyArchiveAsync(
        string archivePath,
        string packageRoot,
        CancellationToken cancellationToken,
        ISkyrimNpcFinishCoreOwnedFileLease? ownedArchive = null)
    {
        const int maximumArchiveEntries = 8192;
        const long maximumArchiveMemberBytes = 64L * 1024 * 1024;
        const long maximumArchiveAggregateBytes = 512L * 1024 * 1024;
        if (!File.Exists(archivePath) || HasReparsePoint(archivePath) ||
            HasReparseAncestor(archivePath))
            throw new InvalidDataException("Finish Core archive is missing or unsafe.");
        string fullPackageRoot = Path.GetFullPath(packageRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expected = new Dictionary<string, ArchiveExpectedMember>(
            StringComparer.Ordinal);
        var expectedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expectedAggregateBytes = 0;
        try
        {
            foreach (string path in EnumerateOrdinaryFiles(fullPackageRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(fullPackageRoot, path)
                    .Replace('\\', '/');
                string entryName = FinishCoreArchiveEntryName(relative);
                if (!expectedAliases.Add(entryName) || expected.ContainsKey(entryName))
                    throw new InvalidDataException(
                        $"Finish Core archive expected member '{entryName}' is duplicated or case-aliased.");
                byte[] bytes = await ReadBoundedBytesAsync(
                    File.OpenRead(path),
                    maximumArchiveMemberBytes,
                    cancellationToken);
                FileInfo afterRead = new(path);
                if (bytes.LongLength != afterRead.Length ||
                    expectedAggregateBytes > maximumArchiveAggregateBytes - bytes.LongLength)
                    throw new InvalidDataException(
                        "Finish Core archive expected members exceed the bounded verification budget.");
                expectedAggregateBytes += bytes.LongLength;
                expected.Add(entryName, new ArchiveExpectedMember(
                    path,
                    bytes,
                    HashBytes(bytes),
                    bytes.LongLength));
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Finish Core archive expected member set contains a duplicate canonical entry.",
                exception);
        }
        long archiveLengthBefore = new FileInfo(archivePath).Length;
        await using Stream stream = ownedArchive?.OpenReadbackStream() ??
            new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var observedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long observedAggregateBytes = 0;
        if (archive.Entries.Count > maximumArchiveEntries)
            throw new InvalidDataException(
                "Finish Core archive contains more members than the bounded verification limit.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') ||
                entry.FullName.Length == 0 ||
                entry.FullName.Contains('\\') ||
                entry.FullName.StartsWith('/') ||
                entry.FullName.Contains(':') ||
                entry.FullName.Split('/').Any(segment => segment is "" or "." or "..") ||
                !observedAliases.Add(entry.FullName) ||
                !observed.Add(entry.FullName) ||
                !expected.TryGetValue(entry.FullName, out ArchiveExpectedMember? member))
                throw new InvalidDataException(
                    $"Finish Core archive member '{entry.FullName}' is not canonical or is undeclared.");
            if (!member.Path.StartsWith(fullPackageRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(member.Path, fullPackageRoot,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Finish Core archive member '{entry.FullName}' escapes the package root.");
            if (entry.Length < 0 || entry.Length > maximumArchiveMemberBytes ||
                observedAggregateBytes > maximumArchiveAggregateBytes - entry.Length)
                throw new InvalidDataException(
                    "Finish Core archive members exceed the bounded verification budget.");
            await using Stream input = entry.Open();
            byte[] archiveBytes = await ReadBoundedBytesAsync(
                input,
                maximumArchiveMemberBytes,
                cancellationToken);
            observedAggregateBytes += archiveBytes.LongLength;
            if (archiveBytes.LongLength != member.Length ||
                HashBytes(archiveBytes) != member.Sha256 ||
                !archiveBytes.AsSpan().SequenceEqual(member.Bytes))
                throw new InvalidDataException(
                    $"Finish Core archive bytes differ for '{entry.FullName}'.");
        }
        if (stream.Length != archiveLengthBefore ||
            !observed.SetEquals(expected.Keys))
            throw new InvalidDataException(
                "Finish Core archive member set is incomplete.");
        foreach (ArchiveExpectedMember member in expected.Values)
        {
            if (!File.Exists(member.Path) || HasReparsePoint(member.Path) ||
                HasReparseAncestor(member.Path))
                throw new InvalidDataException(
                    $"Finish Core archive source member for '{member.Path}' is missing or unsafe.");
            byte[] postRead = await ReadBoundedBytesAsync(
                File.OpenRead(member.Path),
                maximumArchiveMemberBytes,
                cancellationToken);
            if (postRead.LongLength != member.Length ||
                !postRead.AsSpan().SequenceEqual(member.Bytes))
                throw new InvalidDataException(
                    $"Finish Core archive source member '{member.Path}' changed after archive readback.");
        }
        return true;
    }

    private sealed record ArchiveExpectedMember(
        string Path,
        byte[] Bytes,
        Sha256Hash Sha256,
        long Length);

    private static async ValueTask<byte[]> ReadBoundedBytesAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using (source.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                if (output.Length > maximumBytes - read)
                    throw new InvalidDataException(
                        "Finish Core archive member exceeds the bounded read limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }

    private static ImmutableArray<string> BuildCanonicalEvidencePaths(
        SkyrimNpcFinishCoreRequest request,
        bool external)
    {
        string editorId = request.Actor.EditorId?.Value ??
            throw new InvalidDataException(
                "Finish Core evidence requires the request actor editor ID.");
        var paths = ImmutableArray.CreateBuilder<string>();
        paths.Add("NPCManager/Evidence/finish-core-request.json");
        paths.Add("NPCManager/Evidence/finish-core-proposal.json");
        paths.Add("NPCManager/Evidence/runtime-identities.json");
        paths.Add("NPCManager/Evidence/diag-" + editorId + ".txt");
        if (external)
        {
            paths.Add(
                SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath);
            paths.Add(
                SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath);
        }
        return paths.ToImmutable();
    }

    private static async ValueTask<bool> VerifyManifestEvidenceAsync(
        ImmutableArray<SkyrimNpcFinishCoreEvidenceEntry> entries,
        string packageRoot,
        ImmutableArray<string> expectedPaths,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        const int maximumEvidenceFiles = 16;
        const long maximumEvidenceBytes = 64L * 1024 * 1024;
        const long maximumEvidenceMemberBytes = 16L * 1024 * 1024;
        if (entries.IsDefault || entries.Length != expectedPaths.Length ||
            entries.Length > maximumEvidenceFiles)
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-evidence-set",
                DiagnosticSeverity.Error,
                "Finish Core evidence does not equal the exact canonical member set."));
            return false;
        }

        long totalBytes = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            SkyrimNpcFinishCoreEvidenceEntry entry = entries[index];
            string relative = entry.Path.Value;
            if (!string.Equals(relative, expectedPaths[index],
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') ||
                relative.StartsWith('/') ||
                relative.Split('/').Any(segment => segment is "" or "." or "..") ||
                entry.ByteLength < 0 || entry.ByteLength > maximumEvidenceMemberBytes)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-evidence-set",
                    DiagnosticSeverity.Error,
                    "Finish Core evidence paths are not the exact canonical ordered set."));
                return false;
            }
            if (totalBytes > maximumEvidenceBytes - entry.ByteLength)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-evidence-size",
                    DiagnosticSeverity.Error,
                    "Finish Core evidence exceeds the bounded verification budget."));
                return false;
            }
            totalBytes += entry.ByteLength;
            string path = Path.GetFullPath(Path.Combine(
                packageRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            string fullRoot = Path.GetFullPath(packageRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || HasReparseAncestor(path) ||
                HasReparsePoint(path))
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-evidence-missing",
                    DiagnosticSeverity.Error,
                    $"Finish Core evidence member '{relative}' is missing or unsafe."));
                return false;
            }
            FileInfo before = new(path);
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            FileInfo after = new(path);
            if (before.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                before.Attributes.HasFlag(FileAttributes.Device) ||
                after.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                after.Attributes.HasFlag(FileAttributes.Device) ||
                before.Length != after.Length || bytes.LongLength != entry.ByteLength ||
                HashBytes(bytes) != entry.Sha256)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-evidence-drift",
                    DiagnosticSeverity.Error,
                    $"Finish Core evidence member '{relative}' differs from its manifest hash/length."));
                return false;
            }
        }
        return true;
    }

    private const int MaximumInheritedEvidenceVerifyMembers = 1024;
    private const long MaximumInheritedEvidenceVerifyMemberBytes = 64L * 1024 * 1024;
    private const long MaximumInheritedEvidenceVerifyBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Inherited evidence is provenance the source host carried, relocated by
    /// apply under <c>NPCManager/Evidence/Inherited/&lt;source-tree-sha256-8&gt;/</c>.
    /// Every listed member must sit under that exact namespace, mirror its
    /// declared source path, and still carry its manifest hash and length.
    /// </summary>
    private static async ValueTask<bool> VerifyInheritedEvidenceAsync(
        ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry> entries,
        string packageRoot,
        Sha256Hash? sourcePackageTree,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (entries.IsDefaultOrEmpty)
            return true;
        // Apply names the namespace after the request-bound source package tree
        // (the reader's hash), not the plugin-excluded staged tree the manifest
        // records as sourcePackageTreeSha256.
        string inheritedRoot = SkyrimNpcFinishCoreManifestEvidence.InheritedNamespacePrefix +
            (sourcePackageTree?.Value[..8] ?? "") + "/";
        if (sourcePackageTree is null ||
            entries.Length > MaximumInheritedEvidenceVerifyMembers ||
            entries.Select(entry => entry.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length ||
            !entries.Select(entry => entry.Path.Value)
                .SequenceEqual(entries.Select(entry => entry.Path.Value)
                    .OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-inherited-evidence-set",
                DiagnosticSeverity.Error,
                "Finish Core inherited evidence is not a bounded, ordered, alias-free list."));
            return false;
        }
        long totalBytes = 0;
        string fullRoot = Path.GetFullPath(packageRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (SkyrimNpcFinishCoreInheritedEvidenceEntry entry in entries)
        {
            string relative = entry.Path.Value;
            string sourcePath = entry.SourcePath.Value;
            if (!relative.StartsWith(inheritedRoot, StringComparison.Ordinal) ||
                !string.Equals(relative, inheritedRoot + sourcePath, StringComparison.Ordinal) ||
                !sourcePath.StartsWith("NPCManager/Evidence/", StringComparison.Ordinal) ||
                entry.ByteLength < 0 || entry.ByteLength > MaximumInheritedEvidenceVerifyMemberBytes)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-inherited-evidence-set",
                    DiagnosticSeverity.Error,
                    $"Finish Core inherited evidence member '{relative}' is not bound to its source-tree namespace and source path."));
                return false;
            }
            if (totalBytes > MaximumInheritedEvidenceVerifyBytes - entry.ByteLength)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-inherited-evidence-size",
                    DiagnosticSeverity.Error,
                    "Finish Core inherited evidence exceeds the bounded verification budget."));
                return false;
            }
            totalBytes += entry.ByteLength;
            string path = Path.GetFullPath(Path.Combine(
                packageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || HasReparseAncestor(path) || HasReparsePoint(path))
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-inherited-evidence-missing",
                    DiagnosticSeverity.Error,
                    $"Finish Core inherited evidence member '{relative}' is missing or unsafe."));
                return false;
            }
            StableFileHash observed;
            try
            {
                observed = await HashStableBoundedFileAsync(
                    path,
                    MaximumInheritedEvidenceVerifyMemberBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or
                                                UnauthorizedAccessException or
                                                InvalidDataException)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-inherited-evidence-drift",
                    DiagnosticSeverity.Error,
                    $"Finish Core inherited evidence member '{relative}' could not be read within its byte limit: {exception.Message}"));
                return false;
            }
            if (observed.ByteLength != entry.ByteLength ||
                observed.Sha256 != entry.Sha256)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-verify-inherited-evidence-drift",
                    DiagnosticSeverity.Error,
                    $"Finish Core inherited evidence member '{relative}' differs from its manifest hash/length."));
                return false;
            }
        }
        return true;
    }

    private static bool VerifyPhysicalEvidenceClosure(
        string packageRoot,
        bool external,
        ImmutableArray<string> logicalPaths,
        ImmutableArray<string> inheritedPaths,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expected = logicalPaths
            .Add("NPCManager/Evidence/finish-core-manifest.json")
            .Add("NPCManager/Evidence/finish-core-verification.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        var observed = ImmutableArray.CreateBuilder<string>();
        var observedInherited = ImmutableArray.CreateBuilder<string>();
        string[] evidenceRoots = external
            ? [
                Path.Combine(packageRoot, "NPCManager", "Evidence"),
                Path.Combine(packageRoot, "Data", "NPCManager", "Evidence")
            ]
            : [Path.Combine(packageRoot, "NPCManager", "Evidence")];
        try
        {
            foreach (string root in evidenceRoots)
            {
                if (!Directory.Exists(root))
                    throw new InvalidDataException(
                        $"Finish Core evidence root '{root}' is missing.");
                foreach (string path in EnumerateOrdinaryFiles(root))
                {
                    string relative = Path.GetRelativePath(packageRoot, path)
                        .Replace('\\', '/');
                    if (relative.StartsWith(
                            SkyrimNpcFinishCoreManifestEvidence.InheritedNamespacePrefix,
                            StringComparison.OrdinalIgnoreCase))
                        observedInherited.Add(relative);
                    else
                        observed.Add(relative);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-evidence-set",
                DiagnosticSeverity.Error,
                $"Finish Core physical evidence closure could not be enumerated: {exception.Message}"));
            return false;
        }

        ImmutableArray<string> actual = observed
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() ||
            !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-evidence-set",
                DiagnosticSeverity.Error,
                "Finish Core physical evidence contains an extra, missing, duplicate, or case-aliased member."));
            return false;
        }
        ImmutableArray<string> actualInherited = observedInherited
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        ImmutableArray<string> expectedInherited = inheritedPaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (actualInherited.Length != actualInherited.Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
            !actualInherited.SequenceEqual(expectedInherited, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-inherited-evidence-set",
                DiagnosticSeverity.Error,
                "Finish Core inherited evidence contains an unlisted, missing, duplicate, or case-aliased member."));
            return false;
        }
        return true;
    }

    private static FormReference ParseFormReference(string value) =>
        FormReference.TryParse(value, out FormReference parsed) && parsed.ToString() == value
            ? parsed
            : throw new InvalidDataException("The manifest base NPC FormReference is not canonical.");

    private WorkspacePath ParsePath(JsonElement root, string name)
    {
        string value = RequiredString(root, name);
        if (value.Contains('\\') || value.StartsWith('/') ||
            value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Manifest path '{name}' is not canonical.");
        WorkspacePath path = new(Path.GetFullPath(Path.Combine(
            projectRoot.Value, value.Replace('/', Path.DirectorySeparatorChar))));
        return path.IsUnder(projectRoot)
            ? path
            : throw new InvalidDataException($"Manifest path '{name}' escaped the project root.");
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Manifest member '{name}' is required.");

    private static bool RequiredBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"Manifest member '{name}' must be boolean.");

    private static Sha256Hash HashBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static SkyrimNpcFinishCoreVerificationResult RefusedVerification(
        string code,
        string message) =>
        new(false, null, [new Diagnostic(code, DiagnosticSeverity.Error, message)]);

    private static SkyrimNpcFinishCoreVerificationResult RefusedVerification(
        IEnumerable<Diagnostic> diagnostics) =>
        new(false, null, diagnostics.ToImmutableArray());
}
