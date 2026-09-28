using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Pipeline;

/// <summary>
/// Produces the first complete blank-to-new-NPC walking product. Each writer
/// performs its own atomic file promotion; this coordinator owns a previously
/// absent package root and removes that root on any failed stage.
/// </summary>
public sealed partial class BlankNpcBuildService(
    INpcCreationService npcCreationService,
    IBlankNpcProviderService providerService,
    IQualifiedFaceGeomCarrierService faceGeomCarrierService,
    IFaceTintBuildService faceTintBuildService,
    IFaceTintTextureDecoder faceTintDecoder,
    IPackageVerifyService packageVerifier,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IBlankNpcBuildService
{
    public async ValueTask<BlankNpcBuildResult> ExecuteAsync(
        BlankNpcBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Report(progress, BlankNpcBuildStage.Preflight, 2,
            "Verifying the complete hash-bound appearance provider.");
        var providerBinding = await providerService.QualifyAsync(
            CreateProviderBindingRequest(request), cancellationToken);
        diagnostics.AddRange(providerBinding.Diagnostics);
        if (!providerBinding.Qualified || providerBinding.Artifact is null)
            return Refused(diagnostics);
        var provider = providerBinding.Artifact;

        var outputOwned = false;
        var completed = false;
        var ownedFiles = new OwnedFileLedger(request.OutputRoot);
        WorkspacePath? productInputRoot = null;
        PinnedOutputTree? pinnedTree = null;
        NpcCreationResult? npcCreation = null;
        QualifiedFaceGeomCarrierMaterializationResult? faceGeom = null;
        FaceTintBuildResult? faceTint = null;
        FaceTintTextureDecodeResult? faceTintReadback = null;
        PackageVerifyResult? packageVerification = null;
        Sha256Hash? faceGeomEvidenceHash = null;
        Sha256Hash? faceTintEvidenceHash = null;
        Sha256Hash? runtimeKitHash = null;
        Sha256Hash? runtimeDiagnosticBatchHash = null;

        BlankNpcBuildResult FailedWithCleanup()
        {
            if (outputOwned)
            {
                var rollback = RollbackOwnedOutputRoot(
                    request.OutputRoot, pinnedTree, ownedFiles);
                pinnedTree = null;
                diagnostics.AddRange(rollback.Diagnostics);
                outputOwned = !rollback.Complete;
            }
            return new BlankNpcBuildResult(false, null, npcCreation, faceGeom, faceTint,
                faceTintReadback, packageVerification, FreezeDiagnostics(diagnostics));
        }

        try
        {
            if (request.ProviderResources is not null)
            {
                ProviderMaterializationResult materialized =
                    await MaterializeProductProviderAsync(
                        request, diagnostics, cancellationToken);
                if (!materialized.Materialized || materialized.Request is null ||
                    materialized.Root is null)
                    return Refused(diagnostics);
                request = materialized.Request;
                productInputRoot = materialized.Root;
            }

            if (!await PreflightBoundAssetsAsync(request, diagnostics, cancellationToken))
                return Refused(diagnostics);

            Report(progress, BlankNpcBuildStage.Preflight, 5, "Preparing a new transactional package root.");
            pinnedTree = PinOutputAncestry(labRoot, request.OutputRoot, diagnostics);
            if (pinnedTree is null || !pinnedTree.TryCreateOutputRoot(diagnostics))
                return Refused(diagnostics);
            outputOwned = true;
            diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
            if (HasErrors(diagnostics)) return FailedWithCleanup();

            var paths = CreatePaths(request);
            if (!pinnedTree.TryCreateRequiredDirectories(paths.RequiredDirectories, diagnostics))
                return FailedWithCleanup();

            Report(progress, BlankNpcBuildStage.Plugin, 15, "Allocating the new NPC record.");
            var creationRequest = new NpcCreationRequest(
                request.Edition,
                request.TemplatePlugin,
                request.ExpectedTemplatePluginSha256,
                request.TemplateNpcFormId,
                paths.NpcProposal,
                paths.Plugin,
                request.Identity,
                request.Traits,
                request.References,
                request.Appearance,
                request.Stats,
                RuntimeAppearance: request.RuntimeAppearance)
            {
                PluginAuthorities = request.PluginAuthorities,
                PluginType = request.PluginType
            };
            var creationProposal = await npcCreationService.AnalyzeAsync(creationRequest, cancellationToken);
            diagnostics.AddRange(creationProposal.Diagnostics);
            if (!creationProposal.IsApplicable || creationProposal.ProposalHash is null)
                return FailedWithCleanup();
            if (!TryRegisterOwnedFile(ownedFiles, paths.NpcProposal,
                    creationProposal.ProposalHash.Value,
                    "blank-npc-proposal-ownership", "NPC proposal", diagnostics))
                return FailedWithCleanup();
            if (!ValidateProviderMasters(provider, creationProposal.Masters, diagnostics))
                return FailedWithCleanup();
            if (!request.ExpectedOutputMasters.IsDefault &&
                !(request.Appearance is
                      FullyAuthoredSkyrimNpcAppearanceSource
                      {
                          ExposedOutfitSkinBinding: not null
                      }
                    ? creationProposal.Masters.Length >=
                      request.ExpectedOutputMasters.Length &&
                      creationProposal.Masters
                          .Take(request.ExpectedOutputMasters.Length)
                          .SequenceEqual(
                              request.ExpectedOutputMasters,
                              PluginNameComparer.Instance)
                    : creationProposal.Masters.SequenceEqual(
                        request.ExpectedOutputMasters,
                        PluginNameComparer.Instance)))
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-output-masters-mismatch",
                    DiagnosticSeverity.Error,
                    "The deterministic creation master list does not preserve the caller's exact output-master authority prefix; only provider-bound cloned outfit records may append retained reference owners."));
                return FailedWithCleanup();
            }

            npcCreation = await npcCreationService.ApplyAsync(creationRequest, creationProposal, cancellationToken);
            diagnostics.AddRange(npcCreation.Diagnostics);
            if (!npcCreation.Applied || npcCreation.OutputHash is null ||
                npcCreation.Verification is not { IsValid: true }) return FailedWithCleanup();

            var pluginReadback = await npcCreationService.VerifyAsync(
                creationRequest, creationProposal, cancellationToken);
            diagnostics.AddRange(pluginReadback.Diagnostics);
            if (!pluginReadback.IsValid || pluginReadback.OutputHash is null ||
                pluginReadback.OutputHash != npcCreation.OutputHash) return FailedWithCleanup();
            if (!TryRegisterOwnedFile(ownedFiles, paths.Plugin, pluginReadback.OutputHash.Value,
                    "blank-npc-plugin-ownership", "plugin", diagnostics))
                return FailedWithCleanup();

            Report(progress, BlankNpcBuildStage.FaceGeom, 38, "Materializing the qualified complete FaceGeom carrier.");
            var faceGeomSource = ResolveFaceGeomSource(request);
            SkyrimPrivateHeadTexturePaths? embeddedHeadTextures =
                request.Appearance is FullyAuthoredSkyrimNpcAppearanceSource
                {
                    FaceTextureSet: OutputOwnedSkyrimNpcFaceTextureSet outputOwnedTextureSet
                }
                    ? outputOwnedTextureSet.Paths
                    : null;
            var faceGeomAnalysis = await faceGeomCarrierService.AnalyzeAsync(
                new QualifiedFaceGeomCarrierAnalyzeRequest(
                    faceGeomSource.Source,
                    faceGeomSource.Sha256,
                    paths.FaceGeom,
                    paths.FaceTintAssetPath)
                {
                    TargetHeadTextures = embeddedHeadTextures,
                    QualificationProfile =
                        faceGeomSource.QualificationProfile
                },
                cancellationToken);
            diagnostics.AddRange(faceGeomAnalysis.Diagnostics);
            if (!faceGeomAnalysis.Qualified || faceGeomAnalysis.Proposal is null) return FailedWithCleanup();
            if (faceGeomSource.ProviderBound &&
                !ValidateProviderFaceGeom(provider, faceGeomAnalysis.Proposal.Structure, diagnostics))
                return FailedWithCleanup();

            faceGeom = await faceGeomCarrierService.ApplyAsync(faceGeomAnalysis.Proposal, cancellationToken);
            diagnostics.AddRange(faceGeom.Diagnostics);
            if (!faceGeom.Written || !faceGeom.Verified || faceGeom.Artifact is null)
                return FailedWithCleanup();

            var faceGeomEvidence = new QualifiedFaceGeomCarrierMaterializationEvidence(
                Relative(request.OutputRoot, paths.FaceGeom),
                faceGeom.Artifact);
            faceGeomEvidenceHash = await WriteJsonAtomicallyAsync(
                paths.FaceGeomEvidence, faceGeomEvidence, cancellationToken);
            if (!TryRegisterOwnedFile(ownedFiles, paths.FaceGeomEvidence, faceGeomEvidenceHash.Value,
                    "blank-npc-facegeom-evidence-ownership", "FaceGeom materialization evidence", diagnostics))
                return FailedWithCleanup();

            var faceGeomReadback = await faceGeomCarrierService.VerifyEvidenceFileAsync(
                request.OutputRoot,
                Relative(request.OutputRoot, paths.FaceGeomEvidence),
                cancellationToken);
            diagnostics.AddRange(faceGeomReadback.Diagnostics);
            if (!faceGeomReadback.Verified || faceGeomReadback.OutputSha256 is null ||
                faceGeomReadback.ReconstructedSourceSha256 is null ||
                faceGeomReadback.OutputSha256 != faceGeom.Artifact.OutputSha256 ||
                faceGeomReadback.ReconstructedSourceSha256 != faceGeom.Artifact.SourceSha256)
                return FailedWithCleanup();
            if (!TryRegisterOwnedFile(ownedFiles, paths.FaceGeom,
                    faceGeomReadback.OutputSha256.Value,
                    "blank-npc-facegeom-ownership", "FaceGeom NIF", diagnostics))
                return FailedWithCleanup();

            switch (request.FaceTintSource)
            {
                case GeneratedBlankNpcFaceTintSource:
                    Report(progress, BlankNpcBuildStage.FaceTint, 60,
                        "Building and decoding the provider-sampled FaceTint DDS.");
                    faceTint = await faceTintBuildService.BuildAsync(new FaceTintBuildRequest(
                        request.Edition,
                        request.FaceTintManifest,
                        paths.FaceTintEvidence,
                        creationProposal.AllocatedFormId,
                        null,
                        null,
                        null,
                        null,
                        paths.FaceTint,
                        request.FaceTintProviderRoot), cancellationToken);
                    diagnostics.AddRange(faceTint.Diagnostics);
                    if (!faceTint.Written || faceTint.OutputSha256 is null ||
                        faceTint.TextureOutputSha256 is null)
                        return FailedWithCleanup();
                    if (!ValidateProviderFaceTint(provider, faceTint.Artifact, diagnostics))
                        return FailedWithCleanup();

                    faceTintReadback = await faceTintDecoder.DecodeAsync(paths.FaceTint, cancellationToken);
                    diagnostics.AddRange(faceTintReadback.Diagnostics);
                    if (!faceTintReadback.Decoded || faceTintReadback.SourceSha256 is null ||
                        faceTintReadback.SourceSha256 != faceTint.TextureOutputSha256 ||
                        faceTintReadback.Width != 1024 || faceTintReadback.Height != 1024)
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-facetint-readback", DiagnosticSeverity.Error,
                            "The generated FaceTint DDS did not independently decode as the expected 1024x1024 texture."));
                        return FailedWithCleanup();
                    }
                    faceTintEvidenceHash = faceTint.OutputSha256.Value;
                    if (!TryRegisterOwnedFile(ownedFiles, paths.FaceTintEvidence,
                            faceTintEvidenceHash.Value,
                            "blank-npc-facetint-evidence-ownership", "FaceTint evidence", diagnostics) ||
                        !TryRegisterOwnedFile(ownedFiles, paths.FaceTint,
                            faceTint.TextureOutputSha256.Value,
                            "blank-npc-facetint-ownership", "FaceTint DDS", diagnostics))
                        return FailedWithCleanup();
                    break;

                case ExactDdsBlankNpcFaceTintSource exact:
                    Report(progress, BlankNpcBuildStage.FaceTint, 60,
                        "Materializing and independently decoding the hash-bound exact FaceTint DDS.");
                    await CopyHashBoundAtomicallyAsync(
                        exact.SourceDds, paths.FaceTint, exact.ExpectedSha256, cancellationToken);
                    if (!TryRegisterOwnedFile(ownedFiles, paths.FaceTint, exact.ExpectedSha256,
                            "blank-npc-facetint-ownership", "exact FaceTint DDS", diagnostics))
                        return FailedWithCleanup();
                    var exactOutputHash = await HashFileAsync(paths.FaceTint, cancellationToken);
                    if (exactOutputHash != exact.ExpectedSha256)
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-exact-facetint-copy-hash",
                            DiagnosticSeverity.Error,
                            "The exact FaceTint bytes changed during atomic materialization."));
                        return FailedWithCleanup();
                    }

                    faceTintReadback = await faceTintDecoder.DecodeAsync(paths.FaceTint, cancellationToken);
                    diagnostics.AddRange(faceTintReadback.Diagnostics);
                    if (!faceTintReadback.Decoded || faceTintReadback.SourceSha256 != exact.ExpectedSha256 ||
                        faceTintReadback.Width != exact.Width || faceTintReadback.Height != exact.Height)
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-exact-facetint-readback",
                            DiagnosticSeverity.Error,
                            $"The materialized exact FaceTint did not decode as the admitted " +
                            $"{exact.Width}x{exact.Height} DDS."));
                        return FailedWithCleanup();
                    }

                    faceTintEvidenceHash = await WriteExactFaceTintEvidenceAsync(
                        exact, request.OutputRoot, paths.FaceTint, exactOutputHash,
                        paths.FaceTintEvidence,
                        cancellationToken);
                    if (!TryRegisterOwnedFile(ownedFiles, paths.FaceTintEvidence,
                            faceTintEvidenceHash.Value,
                            "blank-npc-facetint-evidence-ownership", "exact FaceTint evidence", diagnostics))
                        return FailedWithCleanup();
                    break;

                default:
                    diagnostics.Add(new Diagnostic("blank-npc-facetint-source",
                        DiagnosticSeverity.Error, "The FaceTint source is not supported."));
                    return FailedWithCleanup();
            }

            if (!await MaterializeTransitiveAssetsAsync(
                    paths.TransitiveAssets, ownedFiles, diagnostics, cancellationToken))
                return FailedWithCleanup();

            Report(progress, BlankNpcBuildStage.Package, 78, "Writing the runtime kit and exact package manifest.");
            CopyAtomically(request.ProviderManifest, paths.ProviderManifest, cancellationToken);
            if (!TryRegisterOwnedFile(ownedFiles, paths.ProviderManifest, provider.ManifestSha256,
                    "blank-npc-provider-copy-hash", "provider bundle", diagnostics))
                return FailedWithCleanup();
            CopyAtomically(request.DependencyManifest, paths.DependencyManifest, cancellationToken);
            if (!TryRegisterOwnedFile(ownedFiles, paths.DependencyManifest,
                    provider.DependencyManifestSha256,
                    "blank-npc-dependency-copy-hash", "dependency manifest", diagnostics))
                return FailedWithCleanup();
            runtimeKitHash = await WriteRuntimeKitAsync(
                request, creationProposal.AllocatedFormId, paths.RuntimeKit, cancellationToken);
            if (!TryRegisterOwnedFile(ownedFiles, paths.RuntimeKit, runtimeKitHash.Value,
                    "blank-npc-runtime-kit-ownership", "runtime kit", diagnostics))
                return FailedWithCleanup();
            runtimeDiagnosticBatchHash = await WriteRuntimeDiagnosticBatchAsync(
                request, creationProposal.AllocatedFormId, paths.RuntimeDiagnosticBatch, cancellationToken);
            if (!TryRegisterOwnedFile(ownedFiles, paths.RuntimeDiagnosticBatch,
                    runtimeDiagnosticBatchHash.Value,
                    "blank-npc-runtime-diagnostic-batch-ownership",
                    "runtime diagnostic batch", diagnostics))
                return FailedWithCleanup();
            var artifacts = await BuildArtifactInventoryAsync(paths, cancellationToken);
            var packageManifest = new PresetToNpcPackageManifest(
                1,
                request.Edition.ToWireName(),
                "blank-npc-creation-proposal",
                Relative(request.OutputRoot, paths.NpcProposal).Value,
                creationProposal.ProposalHash!.Value,
                request.TemplatePlugin.Value,
                request.ExpectedTemplatePluginSha256,
                request.OutputPlugin.Value,
                creationProposal.AllocatedFormId,
                artifacts);
            var manifestWrite = await PackageManifestWriter.WriteAsync(
                packageManifest, paths.Manifest, cancellationToken);
            diagnostics.AddRange(manifestWrite.Diagnostics);
            if (!manifestWrite.Written || manifestWrite.Hash is null) return FailedWithCleanup();
            if (!TryRegisterOwnedFile(ownedFiles, paths.Manifest, manifestWrite.Hash.Value,
                    "blank-npc-manifest-ownership", "package manifest", diagnostics))
                return FailedWithCleanup();

            Report(progress, BlankNpcBuildStage.Verification, 90, "Reopening every declared package artifact.");
            packageVerification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(paths.Manifest), cancellationToken);
            diagnostics.AddRange(packageVerification.Diagnostics);
            if (!packageVerification.Verified || packageVerification.Artifact is null ||
                !packageVerification.Artifact.NoUndeclaredFiles) return FailedWithCleanup();

            // The semantic readers run again after the manifest inventory has been
            // accepted. Final read handles then lock every declared file against writes
            // while its current hash is compared to both semantic and inventory evidence.
            var finalPluginReadback = await npcCreationService.VerifyAsync(
                creationRequest, creationProposal, cancellationToken);
            diagnostics.AddRange(finalPluginReadback.Diagnostics);
            var finalFaceGeomReadback = await faceGeomCarrierService.VerifyEvidenceFileAsync(
                request.OutputRoot,
                Relative(request.OutputRoot, paths.FaceGeomEvidence),
                cancellationToken);
            diagnostics.AddRange(finalFaceGeomReadback.Diagnostics);
            var finalFaceTintReadback = await faceTintDecoder.DecodeAsync(paths.FaceTint, cancellationToken);
            diagnostics.AddRange(finalFaceTintReadback.Diagnostics);
            ExactFaceTintEvidenceVerificationResult? finalExactFaceTintEvidence = null;
            if (request.FaceTintSource is ExactDdsBlankNpcFaceTintSource exactFaceTint)
            {
                finalExactFaceTintEvidence = await VerifyExactFaceTintEvidenceFileAsync(
                    request.OutputRoot,
                    Relative(request.OutputRoot, paths.FaceTintEvidence),
                    policy,
                    labRoot,
                    faceTintDecoder,
                    cancellationToken);
                diagnostics.AddRange(finalExactFaceTintEvidence.Diagnostics);
            }
            var (expectedFaceTintWidth, expectedFaceTintHeight) = FaceTintDimensions(request.FaceTintSource);
            var expectedFaceTintHash = request.FaceTintSource switch
            {
                GeneratedBlankNpcFaceTintSource => faceTint?.TextureOutputSha256,
                ExactDdsBlankNpcFaceTintSource exact => exact.ExpectedSha256,
                _ => null
            };
            if (!finalPluginReadback.IsValid || finalPluginReadback.OutputHash is null ||
                !finalFaceGeomReadback.Verified || finalFaceGeomReadback.OutputSha256 is null ||
                !finalFaceTintReadback.Decoded || finalFaceTintReadback.SourceSha256 is null ||
                expectedFaceTintHash is null ||
                finalFaceTintReadback.SourceSha256 != expectedFaceTintHash ||
                finalFaceTintReadback.Width != expectedFaceTintWidth ||
                finalFaceTintReadback.Height != expectedFaceTintHeight ||
                request.FaceTintSource is ExactDdsBlankNpcFaceTintSource &&
                (finalExactFaceTintEvidence is not
                {
                    Verified: true, OutputDds: not null,
                    SourceSha256: not null, OutputSha256: not null
                } ||
                 !string.Equals(finalExactFaceTintEvidence.OutputDds.Value.Value,
                     paths.FaceTint.Value, StringComparison.OrdinalIgnoreCase) ||
                 finalExactFaceTintEvidence.SourceSha256 != expectedFaceTintHash ||
                 finalExactFaceTintEvidence.OutputSha256 != expectedFaceTintHash ||
                 finalExactFaceTintEvidence.Width != expectedFaceTintWidth ||
                 finalExactFaceTintEvidence.Height != expectedFaceTintHeight))
            {
                diagnostics.Add(new Diagnostic("blank-npc-final-semantic-readback", DiagnosticSeverity.Error,
                    "A game-facing artifact failed its final semantic readback after inventory creation."));
                return FailedWithCleanup();
            }

            var semanticHashes = new Dictionary<string, Sha256Hash>(StringComparer.OrdinalIgnoreCase)
            {
                ["plugin"] = finalPluginReadback.OutputHash.Value,
                ["facegeom"] = finalFaceGeomReadback.OutputSha256.Value,
                ["facetint"] = finalFaceTintReadback.SourceSha256.Value,
                ["npc-creation-proposal"] = creationProposal.ProposalHash!.Value,
                ["facegeom-carrier-materialization"] = faceGeomEvidenceHash.Value,
                [paths.FaceTintEvidenceKind] = faceTintEvidenceHash!.Value,
                ["provider-bundle"] = provider.ManifestSha256,
                ["dependencies"] = provider.DependencyManifestSha256,
                ["runtime-kit"] = runtimeKitHash.Value,
                ["runtime-diagnostic-batch"] = runtimeDiagnosticBatchHash.Value
            };
            foreach (var asset in paths.TransitiveAssets)
                semanticHashes.Add(asset.Kind, asset.Source.ExpectedSha256);

            using var finalInventory = await AcquireFinalInventoryLeaseAsync(
                request.OutputRoot,
                packageVerification.Artifact,
                manifestWrite.Hash.Value,
                semanticHashes,
                diagnostics,
                cancellationToken);
            if (finalInventory is null) return FailedWithCleanup();

            npcCreation = npcCreation with
            {
                OutputHash = finalPluginReadback.OutputHash,
                Verification = finalPluginReadback
            };
            faceTintReadback = finalFaceTintReadback;

            var artifact = new BlankNpcBuildArtifact(
                "1",
                "blank-npc-walking-product",
                "STATIC_PASS_RUNTIME_REQUIRED",
                request.OutputRoot,
                paths.Plugin,
                finalInventory.Hashes["plugin"],
                creationProposal.AllocatedFormId,
                paths.FaceGeom,
                finalInventory.Hashes["facegeom"],
                paths.FaceTint,
                finalInventory.Hashes["facetint"],
                paths.Manifest,
                finalInventory.ManifestHash,
                false);
            completed = true;
            Report(progress, BlankNpcBuildStage.Complete, 100,
                "Package complete. Skyrim runtime authority is still required.");
            return new BlankNpcBuildResult(true, artifact, npcCreation, faceGeom, faceTint,
                faceTintReadback, packageVerification, FreezeDiagnostics(diagnostics));

        }
        catch (OperationCanceledException exception)
        {
            if (outputOwned)
            {
                var rollback = RollbackOwnedOutputRoot(
                    request.OutputRoot, pinnedTree, ownedFiles);
                pinnedTree = null;
                if (!rollback.Complete)
                {
                    throw new IOException(
                        "Blank NPC creation was canceled and rollback was incomplete: " +
                        string.Join(" | ", rollback.Diagnostics.Select(item => $"{item.Code}: {item.Message}")),
                        exception);
                }
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add(new Diagnostic("blank-npc-build-failed", DiagnosticSeverity.Error, exception.Message));
            return FailedWithCleanup();
        }
        catch (Exception exception)
        {
            if (outputOwned && !completed)
            {
                var rollback = RollbackOwnedOutputRoot(
                    request.OutputRoot, pinnedTree, ownedFiles);
                pinnedTree = null;
                if (!rollback.Complete)
                {
                    throw new IOException(
                        "Blank NPC creation failed unexpectedly and rollback was incomplete: " +
                        string.Join(" | ", rollback.Diagnostics.Select(item => $"{item.Code}: {item.Message}")),
                        exception);
                }
            }
            throw;
        }
        finally
        {
            pinnedTree?.Dispose();
            ownedFiles.Dispose();
            if (productInputRoot is not null)
                CleanupProductProviderMaterialization(
                    productInputRoot.Value, request.OutputRoot, diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> Validate(BlankNpcBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("blank-npc-edition", DiagnosticSeverity.Error,
                "Blank NPC creation currently supports Skyrim SE/AE only."));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        if (request.ProviderResources is null)
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ProviderManifest));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.TemplatePlugin));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.FaceGeomCarrier));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.FaceTintManifest));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.FaceTintProviderRoot));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DependencyManifest));
        }
        ValidateBoundAssetContracts(request, diagnostics);
        if (string.Equals(request.OutputRoot.Value, labRoot.Value, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("blank-npc-output-exists", DiagnosticSeverity.Error,
                "The blank NPC output root must be a new directory below the workspace root."));
        // Defense in depth: preflight evaluates the same shared predicate on the
        // blank route before any native work; the writer re-checks it here.
        diagnostics.AddRange(BlankNpcOutputPolicy.Evaluate(
            request.OutputPlugin, request.PluginType, "blank-npc-plugin-type"));
        diagnostics.AddRange(BlankNpcOutputPolicy.EvaluateLightBudget(
            request.PluginType,
            BethesdaNpcCreationAdapter.ExpectedNextFormIdFor(request.Appearance, request.Traits.Role)));
        if (request.ProviderResources is null &&
            (!File.Exists(request.ProviderManifest.Value) || !File.Exists(request.TemplatePlugin.Value) ||
             !File.Exists(request.FaceGeomCarrier.Value) ||
             !File.Exists(request.FaceTintManifest.Value) || !Directory.Exists(request.FaceTintProviderRoot.Value) ||
             !File.Exists(request.DependencyManifest.Value)))
            diagnostics.Add(new Diagnostic("blank-npc-input-missing", DiagnosticSeverity.Error,
                "The provider bundle, template plugin, FaceGeom carrier, FaceTint manifest, provider root, and dependency manifest must exist."));
        return diagnostics.ToImmutable();
    }

}
