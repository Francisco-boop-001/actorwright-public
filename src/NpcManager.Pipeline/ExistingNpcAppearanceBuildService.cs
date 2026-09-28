using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Produces one independently verified, source-owner-keyed appearance package
/// for an NPC that already exists in Skyrim. The override ESP, FaceGen assets,
/// transitive assets, manifest, and evidence share one fresh output root and
/// are removed together when any stage fails.
/// </summary>
public sealed partial class ExistingNpcAppearanceBuildService(
    INpcAppearanceOverrideService appearanceOverrideService,
    IBodyGenService bodyGenService,
    IQualifiedFaceGeomCarrierService faceGeomCarrierService,
    IFaceTintTextureDecoder faceTintDecoder,
    IPackageVerifyService packageVerifier,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IExistingNpcAppearanceBuildService
{
    private static readonly string[] RuntimeChecks =
    [
        "Confirm the source NPC and override plugin are active.",
        "Confirm active FaceGeom and FaceTint provider hashes.",
        "Capture face, neck, body, hands, eyes, and outfit in normal and alternate lighting.",
        "Keep a known-good control NPC in the same frame and lighting."
    ];

    public async ValueTask<ExistingNpcAppearanceBuildResult> ExecuteAsync(
        ExistingNpcAppearanceBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        BuildPaths paths;
        try
        {
            paths = CreatePaths(request);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidDataException or
                                           OverflowException)
        {
            diagnostics.Add(Error("existing-npc-build-paths", exception.Message));
            return Refused(diagnostics);
        }

        Report(progress, BlankNpcBuildStage.Preflight, 3,
            "Preparing a fresh source-owned NPC appearance package.");
        using var outputTree = BlankNpcBuildService.PinOutputAncestry(
            labRoot,
            request.OutputRoot,
            diagnostics);
        if (outputTree is null || !outputTree.TryCreateOutputRoot(diagnostics))
        {
            return Refused(diagnostics);
        }
        if (!outputTree.TryCreateRequiredDirectories(
                paths.RequiredDirectories,
                diagnostics))
        {
            diagnostics.AddRange(outputTree.RollbackOwnedDirectories());
            return Refused(diagnostics);
        }

        using var ownedFiles = new BlankNpcBuildService.OwnedFileLedger(
            request.OutputRoot);
        NpcAppearanceOverrideResult? appearanceOverride = null;
        BodyGenBuildResult? bodyGen = null;
        QualifiedFaceGeomCarrierMaterializationResult? faceGeom = null;
        FaceTintTextureDecodeResult? faceTintReadback = null;
        PackageVerifyResult? packageVerification = null;
        var completed = false;

        bool Register(
            WorkspacePath path,
            Sha256Hash expected,
            string role)
        {
            if (!ownedFiles.TryRegister(path, expected, out var error))
            {
                diagnostics.Add(Error("existing-npc-build-owned-file",
                    $"Could not claim generated {role} as an immutable transaction file: {error}"));
                return false;
            }
            return true;
        }

        var rollbackAttempted = false;

        bool Cleanup()
        {
            var fileRollback = ownedFiles.RollbackFiles();
            var directoryRollback = outputTree.RollbackOwnedDirectories();
            diagnostics.AddRange(fileRollback);
            diagnostics.AddRange(directoryRollback);
            return !Directory.Exists(request.OutputRoot.Value) &&
                   !File.Exists(request.OutputRoot.Value) &&
                   !HasErrors(fileRollback) &&
                   !HasErrors(directoryRollback);
        }

        ExistingNpcAppearanceBuildResult FailedWithCleanup()
        {
            rollbackAttempted = true;
            Cleanup();
            return Refused(diagnostics);
        }

        try
        {
            Report(progress, BlankNpcBuildStage.Plugin, 15,
                "Writing one true source-owned NPC appearance override.");
            var overrideRequest = new NpcAppearanceOverrideRequest(
                request.Edition,
                request.SourcePlugin,
                request.ExpectedSourcePluginSha256,
                request.TargetFormId,
                paths.Proposal,
                paths.Plugin,
                request.Race,
                request.Sex,
                request.Appearance,
                request.RuntimeAppearance) { PluginAuthorities = request.PluginAuthorities };
            var proposal = await appearanceOverrideService.AnalyzeAsync(
                overrideRequest,
                cancellationToken);
            diagnostics.AddRange(proposal.Diagnostics);
            if (!proposal.IsApplicable || proposal.ProposalSha256 is null ||
                !Register(
                    paths.Proposal,
                    proposal.ProposalSha256.Value,
                    "appearance proposal"))
            {
                return FailedWithCleanup();
            }

            appearanceOverride = await appearanceOverrideService.ApplyAsync(
                overrideRequest,
                proposal,
                cancellationToken);
            diagnostics.AddRange(appearanceOverride.Diagnostics);
            if (!appearanceOverride.Applied ||
                appearanceOverride.Verification is not
                {
                    IsValid: true,
                    OutputSha256: not null,
                    SourceOwnedTargetCount: 1,
                    SelfOwnedTargetCount: 0
                } pluginVerification ||
                !Register(
                    paths.Plugin,
                    pluginVerification.OutputSha256.Value,
                    "override plugin"))
            {
                return FailedWithCleanup();
            }

            if (!request.BodyMorphs.IsDefaultOrEmpty)
            {
                bodyGen = await bodyGenService.BuildTypedAsync(
                    new BodyGenTypedBuildRequest(
                        GameEdition.SkyrimSpecialEdition,
                        paths.SourceOwner,
                        request.TargetFormId,
                        paths.SourceOwner.Value,
                        request.BodyMorphs,
                        new WorkspacePath(Path.Combine(
                            request.OutputRoot.Value,
                            "Data")))
                    {
                        EditorId = proposal.SourceEditorId
                    },
                    cancellationToken);
                diagnostics.AddRange(bodyGen.Diagnostics);
                if (!bodyGen.Written || bodyGen.Files.IsDefaultOrEmpty ||
                    bodyGen.Plugin != paths.SourceOwner ||
                    bodyGen.NpcFormId != request.TargetFormId)
                {
                    diagnostics.Add(Error("existing-npc-build-bodygen",
                        "BodyGen did not produce source-owner-keyed sidecars for the exact target NPC."));
                    return FailedWithCleanup();
                }
                foreach (var file in bodyGen.Files)
                {
                    if (!Register(
                            file.AbsolutePath,
                            file.Sha256,
                            "BodyGen sidecar"))
                    {
                        return FailedWithCleanup();
                    }
                }
            }

            Report(progress, BlankNpcBuildStage.FaceGeom, 40,
                "Materializing FaceGeom under the source plugin and local FormID.");
            var privateHeadTextures = request.Appearance.FaceTextureSet is
                OutputOwnedSkyrimNpcFaceTextureSet outputOwnedTextureSet
                    ? outputOwnedTextureSet.Paths
                    : null;
            var faceGeomAnalysis = await faceGeomCarrierService.AnalyzeAsync(
                new QualifiedFaceGeomCarrierAnalyzeRequest(
                    request.FaceGeomSource.SourceNif,
                    request.FaceGeomSource.ExpectedSha256,
                    paths.FaceGeom,
                    paths.FaceTintAssetPath)
                {
                    TargetHeadTextures = privateHeadTextures,
                    QualificationProfile =
                        request.FaceGeomSource.QualificationProfile
                },
                cancellationToken);
            diagnostics.AddRange(faceGeomAnalysis.Diagnostics);
            if (!faceGeomAnalysis.Qualified || faceGeomAnalysis.Proposal is null)
                return FailedWithCleanup();
            faceGeom = await faceGeomCarrierService.ApplyAsync(
                faceGeomAnalysis.Proposal,
                cancellationToken);
            diagnostics.AddRange(faceGeom.Diagnostics);
            if (!faceGeom.Written || !faceGeom.Verified ||
                faceGeom.Artifact is null ||
                !Register(
                    paths.FaceGeom,
                    faceGeom.Artifact.OutputSha256,
                    "FaceGeom NIF"))
            {
                return FailedWithCleanup();
            }

            var faceGeomEvidenceHash = await WriteJsonAsync(
                paths.FaceGeomEvidence,
                new QualifiedFaceGeomCarrierMaterializationEvidence(
                    Relative(request.OutputRoot, paths.FaceGeom),
                    faceGeom.Artifact),
                cancellationToken);
            if (!Register(
                    paths.FaceGeomEvidence,
                    faceGeomEvidenceHash,
                    "FaceGeom evidence"))
            {
                return FailedWithCleanup();
            }
            var faceGeomReadback = await faceGeomCarrierService.VerifyEvidenceFileAsync(
                request.OutputRoot,
                Relative(request.OutputRoot, paths.FaceGeomEvidence),
                cancellationToken);
            diagnostics.AddRange(faceGeomReadback.Diagnostics);
            if (!faceGeomReadback.Verified ||
                faceGeomReadback.OutputSha256 != faceGeom.Artifact.OutputSha256 ||
                faceGeomReadback.ReconstructedSourceSha256 !=
                faceGeom.Artifact.SourceSha256)
            {
                diagnostics.Add(Error("existing-npc-build-facegeom-readback",
                    "Packaged FaceGeom evidence did not reconstruct the exact source and output."));
                return FailedWithCleanup();
            }

            Report(progress, BlankNpcBuildStage.FaceTint, 60,
                "Materializing and decoding FaceTint under the source owner.");
            await CopyHashBoundAsync(
                request.FaceTintSource.SourceDds,
                request.FaceTintSource.ExpectedSha256,
                paths.FaceTint,
                cancellationToken);
            if (!Register(
                    paths.FaceTint,
                    request.FaceTintSource.ExpectedSha256,
                    "FaceTint DDS"))
            {
                return FailedWithCleanup();
            }
            faceTintReadback = await faceTintDecoder.DecodeAsync(
                paths.FaceTint,
                cancellationToken);
            diagnostics.AddRange(faceTintReadback.Diagnostics);
            if (!faceTintReadback.Decoded ||
                faceTintReadback.SourceSha256 != request.FaceTintSource.ExpectedSha256 ||
                faceTintReadback.Width != request.FaceTintSource.Width ||
                faceTintReadback.Height != request.FaceTintSource.Height)
            {
                diagnostics.Add(Error("existing-npc-build-facetint-readback",
                    "The source-owner-keyed FaceTint did not decode with its admitted hash and dimensions."));
                return FailedWithCleanup();
            }
            var faceTintEvidenceHash = await WriteJsonAsync(
                paths.FaceTintEvidence,
                new ExactFaceTintEvidence(
                    "1",
                    "exact-dds-facetint-materialization-v2",
                    request.FaceTintSource.ExpectedSha256.Value,
                    Relative(request.OutputRoot, paths.FaceTint).Value,
                    request.FaceTintSource.ExpectedSha256.Value,
                    request.FaceTintSource.Width,
                    request.FaceTintSource.Height,
                    true,
                    false),
                cancellationToken);
            if (!Register(
                    paths.FaceTintEvidence,
                    faceTintEvidenceHash,
                    "FaceTint evidence"))
            {
                return FailedWithCleanup();
            }

            foreach (var asset in paths.TransitiveAssets)
            {
                await CopyHashBoundAsync(
                    asset.Source.Source,
                    asset.Source.ExpectedSha256,
                    asset.Destination,
                    cancellationToken);
                if (!Register(
                        asset.Destination,
                        asset.Source.ExpectedSha256,
                        asset.Kind))
                {
                    return FailedWithCleanup();
                }
            }

            Report(progress, BlankNpcBuildStage.Package, 78,
                "Writing the source-owner runtime kit and exact package inventory.");
            var runtimeKitHash = await WriteJsonAsync(
                paths.RuntimeKit,
                new
                {
                    schemaVersion = "1",
                    artifactKind = "skyrim-existing-npc-runtime-validation-kit",
                    overridePlugin = request.OutputPlugin.Value,
                    sourceOwnerPlugin = paths.SourceOwner.Value,
                    sourceOwnerLocalFormId = request.TargetFormId.ToString(),
                    editorId = proposal.SourceEditorId.Value,
                    runtimeAuthority = false,
                    checks = RuntimeChecks
                },
                cancellationToken);
            if (!Register(paths.RuntimeKit, runtimeKitHash, "runtime kit"))
                return FailedWithCleanup();

            var inventory = await BuildInventoryAsync(
                paths,
                bodyGen,
                cancellationToken);
            var manifestWrite = await PackageManifestWriter.WriteAsync(
                new PresetToNpcPackageManifest(
                    1,
                    request.Edition.ToWireName(),
                    "racemenu-existing-npc-appearance-override",
                    Relative(request.OutputRoot, paths.Proposal).Value,
                    proposal.ProposalSha256.Value,
                    request.SourcePlugin.Value,
                    request.ExpectedSourcePluginSha256,
                    request.OutputPlugin.Value,
                    request.TargetFormId,
                    inventory),
                paths.Manifest,
                cancellationToken);
            diagnostics.AddRange(manifestWrite.Diagnostics);
            if (!manifestWrite.Written || manifestWrite.Hash is null ||
                !Register(
                    paths.Manifest,
                    manifestWrite.Hash.Value,
                    "package manifest"))
            {
                return FailedWithCleanup();
            }

            Report(progress, BlankNpcBuildStage.Verification, 92,
                "Reopening the plugin, FaceGen, and every declared package file.");
            packageVerification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(paths.Manifest),
                cancellationToken);
            diagnostics.AddRange(packageVerification.Diagnostics);
            var finalPlugin = await appearanceOverrideService.VerifyAsync(
                overrideRequest,
                proposal,
                cancellationToken);
            diagnostics.AddRange(finalPlugin.Diagnostics);
            var finalFaceGeom = await faceGeomCarrierService.VerifyEvidenceFileAsync(
                request.OutputRoot,
                Relative(request.OutputRoot, paths.FaceGeomEvidence),
                cancellationToken);
            diagnostics.AddRange(finalFaceGeom.Diagnostics);
            var finalFaceTint = await faceTintDecoder.DecodeAsync(
                paths.FaceTint,
                cancellationToken);
            diagnostics.AddRange(finalFaceTint.Diagnostics);
            if (packageVerification is not
                {
                    Verified: true,
                    Artifact: { NoUndeclaredFiles: true }
                } ||
                !finalPlugin.IsValid ||
                finalPlugin.OutputSha256 != pluginVerification.OutputSha256 ||
                !finalFaceGeom.Verified ||
                finalFaceGeom.OutputSha256 != faceGeom.Artifact.OutputSha256 ||
                !finalFaceTint.Decoded ||
                finalFaceTint.SourceSha256 != request.FaceTintSource.ExpectedSha256 ||
                HasErrors(diagnostics))
            {
                diagnostics.Add(Error("existing-npc-build-final-readback",
                    "A package artifact failed final semantic or inventory read-back."));
                return FailedWithCleanup();
            }

            completed = true;
            Report(progress, BlankNpcBuildStage.Complete, 100,
                "Existing-NPC appearance package completed with static verification.");
            diagnostics.Add(new Diagnostic(
                "existing-npc-build-static-complete",
                DiagnosticSeverity.Info,
                "The source-owned NPC override and origin-keyed assets were independently reopened; Skyrim runtime and visual authority remain separate."));
            return new ExistingNpcAppearanceBuildResult(
                true,
                new ExistingNpcAppearanceBuildArtifact(
                    "1",
                    "existing-npc-appearance-walking-product",
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    request.OutputRoot,
                    paths.Plugin,
                    finalPlugin.OutputSha256!.Value,
                    paths.SourceOwner,
                    request.TargetFormId,
                    paths.FaceGeom,
                    finalFaceGeom.OutputSha256!.Value,
                    paths.FaceTint,
                    finalFaceTint.SourceSha256!.Value,
                    paths.Manifest,
                    manifestWrite.Hash.Value,
                    false),
                appearanceOverride,
                bodyGen,
                faceGeom,
                finalFaceTint,
                packageVerification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            rollbackAttempted = true;
            if (!Cleanup())
            {
                throw new IOException(
                    "Existing-NPC appearance creation was canceled and rollback was incomplete: " +
                    string.Join(" | ", diagnostics
                        .Where(item => item.Severity == DiagnosticSeverity.Error)
                        .Select(item => $"{item.Code}: {item.Message}")),
                    exception);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException)
        {
            diagnostics.Add(Error("existing-npc-build-failed", exception.Message));
            return FailedWithCleanup();
        }
        finally
        {
            if (!completed && !rollbackAttempted) Cleanup();
        }
    }

    private static AssetPath Relative(WorkspacePath root, WorkspacePath path) =>
        new(Path.GetRelativePath(root.Value, path.Value));

    private static void Report(
        IProgress<BlankNpcBuildProgress>? progress,
        BlankNpcBuildStage stage,
        int percent,
        string message) =>
        progress?.Report(new BlankNpcBuildProgress(stage, percent, message));

    private static ExistingNpcAppearanceBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, null, null, null, null, diagnostics.ToImmutable());
}
