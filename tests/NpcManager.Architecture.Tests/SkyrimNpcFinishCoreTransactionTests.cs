using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNpcFinishCoreTransaction()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        SkyrimNpcFinishCoreRequest request = fixture.Proposal.Request!;
        string[] expectedProposalPackageFiles =
        [
            "Data/" + request.Output.PluginFileName,
            "NPCManager/Evidence/finish-core-request.json",
            "NPCManager/Evidence/finish-core-proposal.json",
            "NPCManager/Evidence/finish-core-manifest.json"
        ];
        Assert(fixture.Proposal.PackageFiles.SequenceEqual(expectedProposalPackageFiles),
            "Finish Core proposal PackageFiles did not bind to the actual package-root evidence paths.");
        SkyrimNpcFinishCorePluginSnapshot snapshot =
            BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                fixture.SourcePlugin,
                request.Source.Plugin!.Value,
                request.Actor.FormId!.Value,
                request.Actor.EditorId!.Value,
                CancellationToken.None);
        var sourceResult = new SkyrimNpcFinishCoreSourceReadResult(
            true, null, request.Source.PackageTreeSha256!.Value,
            snapshot.PluginSha256, snapshot.BaseNpc, snapshot.TargetEditorId,
            true, "None", snapshot.TypedForbiddenCounts,
            snapshot.RawForbiddenCounts, snapshot.Diagnostics)
        {
            NextFormId = new FormId(snapshot.NextFormId),
            Tes4Flags = snapshot.Tes4Flags,
            MasterOrder = snapshot.MasterOrder,
            OccupiedIds = snapshot.OccupiedIds,
            TargetConfigurationFlags = snapshot.TargetConfigurationFlags,
            FactionRanks = snapshot.FactionRanks,
            CombatStyle = snapshot.CombatStyle,
            CombatStyleMatchesDefensiveContract = snapshot.CombatStyleMatchesDefensiveContract,
            DefaultOutfit = snapshot.DefaultOutfit,
            Inventory = snapshot.Inventory,
            PackageLinks = snapshot.PackageLinks,
            Relationships = snapshot.Relationships,
            SemanticSurfaceValues = []
        };
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root));
        Sha256Hash requestSha = fixture.Proposal.RequestSha256!.Value;
        Sha256Hash proposalSha = fixture.Proposal.ProposalSha256!.Value;
        byte[] sourcePluginBefore = await File.ReadAllBytesAsync(fixture.SourcePlugin.Value);
        string sourceManifestPath = request.Source.PackageManifest!.Value.Value;
        byte[] sourceManifestBefore = await File.ReadAllBytesAsync(sourceManifestPath);
        var stagingRaceService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            createOwnedDirectory: _ => false);
        SkyrimNpcFinishCoreApplyResult stagingRace =
            await stagingRaceService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!stagingRace.Applied && stagingRace.Diagnostics.Any(item =>
                   item.Code == "finish-core-transaction-staging-raced") &&
               !Directory.EnumerateDirectories(
                       fixture.Root,
                       ".finish-core-transaction-*",
                       SearchOption.TopDirectoryOnly)
                   .Any(),
            "Finish Core did not fail closed when exclusive staging ownership was unavailable.");

        WorkspacePath validationProposal = new(Path.Combine(
            fixture.Root, "validate-only-proposal.json"));
        SkyrimNpcFinishCoreValidationResult validation =
            await service.ValidateAnalyzeAsync(
                request, requestSha, validationProposal, CancellationToken.None);
        Assert(
            validation.Valid &&
            validation.Phases.Any(phase =>
                phase.Name == "disposable-write" &&
                phase.State == SkyrimNpcFinishCoreValidationPhaseState.Reached) &&
            !File.Exists(validationProposal.Value) &&
            !Directory.Exists(request.Output.Root!.Value.Value) &&
            !File.Exists(request.Output.Archive!.Value.Value) &&
            !Directory.EnumerateDirectories(
                    fixture.Root,
                    ".finish-core-validation-*",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "Finish Core validate-all did not complete disposable write/readback cleanly without promotion: " +
            string.Join(" | ", validation.Diagnostics.Select(x => x.Code + ":" + x.Message)));

        SkyrimNpcFinishCoreValidationResult applyValidation =
            await service.ValidateApplyAsync(
                request,
                requestSha,
                fixture.Proposal,
                proposalSha,
                CancellationToken.None);
        Assert(
            applyValidation.Valid &&
            applyValidation.Phases.Any(phase =>
                phase.Name == "disposable-write" &&
                phase.State == SkyrimNpcFinishCoreValidationPhaseState.Reached) &&
            !Directory.Exists(request.Output.Root!.Value.Value) &&
            !File.Exists(request.Output.Archive!.Value.Value),
            "Finish Core apply --validate-all did not validate the supplied proposal through disposable readback without promotion.");

        SkyrimNpcFinishCoreRequest invalidRequest = request with
        {
            FollowerPolicy = request.FollowerPolicy with { DefensiveOnly = false },
            AiPolicy = request.AiPolicy! with
            {
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsNobody
            },
            Output = request.Output with
            {
                Root = request.Source.PackageRoot,
                Archive = request.Source.PluginPath
            }
        };
        SkyrimNpcFinishCoreValidationResult invalidValidation =
            await service.ValidateAnalyzeAsync(
                invalidRequest,
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                    invalidRequest, new WorkspacePath(fixture.Root)),
                new WorkspacePath(sourceManifestPath),
                CancellationToken.None);
        Assert(
            !invalidValidation.Valid &&
            invalidValidation.Diagnostics.Any(item =>
                item.Code == "finish-core-policy-defensive-only") &&
            invalidValidation.Diagnostics.Any(item =>
                item.Code == "finish-core-policy-assistance") &&
            invalidValidation.Diagnostics.Any(item =>
                item.Code == "finish-core-transaction-root-overlap") &&
            invalidValidation.Phases.Any(phase =>
                phase.Name == "disposable-write" &&
                phase.State == SkyrimNpcFinishCoreValidationPhaseState.Skipped &&
                phase.Diagnostics.Any(item =>
                    item.Code == "finish-core-validate-phase-skipped")) &&
            !Directory.Exists(request.Output.Root!.Value.Value) &&
            !File.Exists(request.Output.Archive!.Value.Value),
            "Finish Core validate-all did not accumulate independent failures and label the blocked disposable phase.");

        SkyrimNpcFinishCoreApplyResult applied = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha, CancellationToken.None);
        Assert(applied.Applied && applied.Manifest is not null && applied.OutputRoot is not null &&
               applied.Archive is not null,
            "Finish Core transaction did not publish a manifest, package root, and archive: " +
            string.Join(" | ", applied.Diagnostics.Select(x => x.Code + ":" + x.Message)));
        Sha256Hash expectedLegacySourceTree = LegacyTreeHash(
            request.Source.PackageRoot!.Value.Value,
            request.Source.PluginPath!.Value.Value);
        Assert(applied.Manifest!.Schema == SkyrimNpcFinishCoreManifest.SchemaIdentifier &&
               applied.Manifest.SourcePackageTreeSha256 == expectedLegacySourceTree &&
               applied.Manifest.SourcePackageTreeSha256 != request.Source.PackageTreeSha256,
            "Legacy Finish publication did not persist the historical plugin-excluded source tree authority.");
        string outputRoot = applied.OutputRoot!.Value.Value;
        string archivePath = applied.Archive!.Value.Value;
        string pluginName = request.Source.Plugin!.Value.Value;
        string outputPlugin = Path.Combine(outputRoot, "Data", pluginName);
        Assert(File.Exists(outputPlugin) &&
               !File.Exists(Path.Combine(outputRoot, pluginName)),
            "Finish Core did not preserve the manifest-bound Data/plugin package path.");
        byte[] sourcePluginAfter = await File.ReadAllBytesAsync(fixture.SourcePlugin.Value);
        byte[] sourceManifestAfter = await File.ReadAllBytesAsync(sourceManifestPath);
        Assert(sourcePluginBefore.SequenceEqual(sourcePluginAfter) &&
               sourceManifestBefore.SequenceEqual(sourceManifestAfter),
            "Finish Core changed immutable source plugin or package-manifest evidence.");
        string evidenceRoot = Path.Combine(outputRoot, "NPCManager", "Evidence");
        foreach (string name in new[]
                 {
                     "finish-core-request.json", "finish-core-proposal.json",
                     "finish-core-manifest.json", "finish-core-verification.json",
                     "runtime-identities.json"
                 })
        {
            Assert(File.Exists(Path.Combine(evidenceRoot, name)),
                $"Required Finish Core evidence '{name}' is missing.");
        }
        string diag = Path.Combine(evidenceRoot,
            "diag-" + request.Actor.EditorId!.Value.Value + ".txt");
        Assert(File.Exists(diag), "The canonical base-actor diagnostic batch is missing.");
        string[] expectedEvidence =
        [
            "NPCManager/Evidence/finish-core-request.json",
            "NPCManager/Evidence/finish-core-proposal.json",
            "NPCManager/Evidence/runtime-identities.json",
            "NPCManager/Evidence/diag-" + request.Actor.EditorId!.Value.Value + ".txt"
        ];
        SkyrimNpcFinishCoreManifest persistedManifest = applied.Manifest!;
        Assert(persistedManifest.Evidence.Files.Select(item => item.Path.Value)
                   .SequenceEqual(expectedEvidence) &&
               !persistedManifest.Evidence.Files.Any(item =>
                   item.Path.Value.EndsWith("finish-core-manifest.json",
                       StringComparison.Ordinal) ||
                   item.Path.Value.EndsWith("finish-core-verification.json",
                       StringComparison.Ordinal)),
            "Legacy Finish evidence set was not the exact non-circular canonical four-file set.");
        string diagText = await File.ReadAllTextAsync(diag);
        Assert(!diagText.Contains("prid", StringComparison.OrdinalIgnoreCase) &&
               !diagText.Contains("placeatme", StringComparison.OrdinalIgnoreCase) &&
               !diagText.Contains("moveto", StringComparison.OrdinalIgnoreCase),
            "The Finish Core diagnostic batch contains a placed-reference command.");
        Assert(File.Exists(archivePath), "The direct-install Finish Core archive is missing.");
        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
        {
            string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert(names.Contains(pluginName, StringComparer.Ordinal) &&
                   names.All(name => !name.StartsWith("Data/Data/", StringComparison.OrdinalIgnoreCase)) &&
                   names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
                "The direct-install archive lost the nested plugin or introduced a Data/Data or duplicate entry.");
        }

        WorkspacePath manifestPath = new(Path.Combine(evidenceRoot, "finish-core-manifest.json"));
        SkyrimNpcFinishCoreVerificationResult verified = await service.VerifyAsync(
            manifestPath, HashFile(manifestPath.Value), CancellationToken.None);
        Assert(verified.Verified && verified.Verification is not null &&
               !verified.Verification.PlacementIncluded &&
               !verified.Verification.RuntimeAuthority &&
               !verified.Verification.VisualAuthority,
            "Finish Core verify did not independently reopen the published package: " +
            string.Join(" | ", verified.Diagnostics.Select(x => x.Code + ":" + x.Message)));
        Assert(verified.Verification!.ArchiveSha256 == HashFile(archivePath),
            "Legacy v2 Finish verify did not persist the physical archive hash.");
        byte[] publishedManifestBytes = await File.ReadAllBytesAsync(manifestPath.Value);
        SkyrimNpcFinishCoreManifest duplicateEvidenceManifest = applied.Manifest! with
        {
            Evidence = applied.Manifest.Evidence with
            {
                Files = applied.Manifest.Evidence.Files.Add(
                    applied.Manifest.Evidence.Files[0])
            }
        };
        await File.WriteAllBytesAsync(
            manifestPath.Value,
            SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                duplicateEvidenceManifest,
                new WorkspacePath(fixture.Root)));
        SkyrimNpcFinishCoreVerificationResult duplicateEvidence =
            await service.VerifyAsync(
                manifestPath,
                HashFile(manifestPath.Value),
                CancellationToken.None);
        Assert(!duplicateEvidence.Verified && duplicateEvidence.Diagnostics.Any(item =>
                   item.Code == "finish-core-verify-evidence-set"),
            "Legacy verify accepted an extra/duplicate evidence member.");
        await File.WriteAllBytesAsync(manifestPath.Value, publishedManifestBytes);
        string extraEvidencePath = Path.Combine(evidenceRoot, "unexpected-extra.txt");
        await File.WriteAllTextAsync(extraEvidencePath, "extra evidence");
        SkyrimNpcFinishCoreVerificationResult extraEvidence = await service.VerifyAsync(
            manifestPath,
            HashFile(manifestPath.Value),
            CancellationToken.None);
        Assert(!extraEvidence.Verified && extraEvidence.Diagnostics.Any(item =>
                   item.Code == "finish-core-verify-evidence-set"),
            "Legacy verify accepted an unlisted physical evidence file.");
        File.Delete(extraEvidencePath);

        // Explicit legacy wire compatibility: v1 request/proposal still
        // publish a v1 manifest, retain Hlegacy, and reopen through Verify.
        string legacyOutputRoot = Path.Combine(fixture.Root, "legacy-v1-output");
        string legacyArchive = Path.Combine(fixture.Root, "legacy-v1-output.zip");
        SkyrimNpcFinishCoreRequest legacyRequest = request with
        {
            Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
            AiPolicy = request.AiPolicy! with { Mood = null },
            Output = request.Output with
            {
                Root = new WorkspacePath(legacyOutputRoot),
                Archive = new WorkspacePath(legacyArchive)
            }
        };
        Sha256Hash legacyRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
            legacyRequest,
            new WorkspacePath(fixture.Root));
        WorkspacePath legacyProposalPath = new(Path.Combine(
            fixture.Root, "legacy-v1-proposal.json"));
        SkyrimNpcFinishCoreProposalResult legacyAnalysis = await service.AnalyzeAsync(
            legacyRequest,
            legacyRequestSha,
            legacyProposalPath,
            CancellationToken.None);
        Assert(legacyAnalysis.Proposed && legacyAnalysis.Proposal is not null &&
               legacyAnalysis.Proposal.Schema ==
                   SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
            "The explicit legacy v1 request did not produce a v1 proposal.");
        SkyrimNpcFinishCoreApplyResult legacyApplied = await service.ApplyAsync(
            legacyRequest,
            legacyRequestSha,
            legacyAnalysis.Proposal!,
            legacyAnalysis.ProposalSha256!.Value,
            CancellationToken.None);
        Assert(legacyApplied.Applied && legacyApplied.Manifest?.Schema ==
                   SkyrimNpcFinishCoreManifest.SchemaIdentifier &&
               legacyApplied.Manifest.SourcePackageTreeSha256 == expectedLegacySourceTree,
            "The explicit legacy v1 transaction did not persist a v1 manifest with Hlegacy.");
        WorkspacePath legacyManifestPath = new(Path.Combine(
            legacyOutputRoot, "NPCManager", "Evidence", "finish-core-manifest.json"));
        SkyrimNpcFinishCoreVerificationResult legacyVerified = await service.VerifyAsync(
            legacyManifestPath,
            HashFile(legacyManifestPath.Value),
            CancellationToken.None);
        Assert(legacyVerified.Verified &&
               legacyVerified.Verification?.ArchiveSha256 == HashFile(legacyArchive),
            "The explicit legacy v1 package did not reopen and persist its physical archive hash: " +
            string.Join(" | ", legacyVerified.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        Directory.Delete(legacyOutputRoot, recursive: true);
        File.Delete(legacyArchive);
        File.Delete(legacyProposalPath.Value);

        // Round-4 RED: the source can drift after the staged Hsource TOCTOU
        // check but before publication.  Legacy Hlegacy must be derived from
        // that exact copied staging snapshot, not reread from the mutable
        // source tree at the end of Apply.
        string boundaryOutputRoot = Path.Combine(fixture.Root, "boundary-output");
        string boundaryArchive = Path.Combine(fixture.Root, "boundary-output.zip");
        SkyrimNpcFinishCoreRequest boundaryRequest = request with
        {
            Output = request.Output with
            {
                Root = new WorkspacePath(boundaryOutputRoot),
                Archive = new WorkspacePath(boundaryArchive)
            }
        };
        Sha256Hash boundaryRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
            boundaryRequest, new WorkspacePath(fixture.Root));
        WorkspacePath boundaryProposalPath = new(Path.Combine(
            fixture.Root, "boundary-proposal.json"));
        SkyrimNpcFinishCoreProposalResult boundaryAnalysis = await service.AnalyzeAsync(
            boundaryRequest,
            boundaryRequestSha,
            boundaryProposalPath,
            CancellationToken.None);
        Assert(boundaryAnalysis.Proposed && boundaryAnalysis.Proposal is not null,
            "The legacy source-boundary mutation setup did not produce a proposal.");
        Sha256Hash boundaryLegacyBefore = LegacyTreeHash(
            request.Source.PackageRoot!.Value.Value,
            request.Source.PluginPath!.Value.Value);
        byte[] boundarySourceManifestBefore = File.ReadAllBytes(sourceManifestPath);
        int stagingProbeCount = 0;
        var boundaryService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            path =>
            {
                if (Path.GetFileName(path).StartsWith(
                        ".finish-core-transaction-", StringComparison.Ordinal) &&
                    ++stagingProbeCount == 2)
                    File.WriteAllBytes(
                        sourceManifestPath,
                        boundarySourceManifestBefore.Concat(new byte[] { 0x42 }).ToArray());
                return false;
            });
        SkyrimNpcFinishCoreApplyResult boundaryApplied;
        try
        {
            boundaryApplied = await boundaryService.ApplyAsync(
                boundaryRequest,
                boundaryRequestSha,
                boundaryAnalysis.Proposal!,
                boundaryAnalysis.ProposalSha256!.Value,
                CancellationToken.None);
        }
        finally
        {
            File.WriteAllBytes(sourceManifestPath, boundarySourceManifestBefore);
        }
        Assert(boundaryApplied.Applied &&
               boundaryApplied.Manifest?.SourcePackageTreeSha256 == boundaryLegacyBefore,
            "Legacy publication reread the mutable source instead of retaining the copied Hlegacy snapshot.");
        Directory.Delete(boundaryOutputRoot, recursive: true);
        File.Delete(boundaryArchive);
        File.Delete(boundaryProposalPath.Value);

        // Portable round-4 seam: the archive temp becomes transaction-owned
        // immediately after CreateNew, then a stateful boundary probe refuses
        // promotion and blocks cleanup.  The refusal result must retain the
        // cleanup diagnostic and the exact surviving owned path.
        Directory.Delete(outputRoot, recursive: true);
        File.Delete(archivePath);
        string temporaryArchivePath = archivePath + ".tmp";
        var unsafeService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            path => path.Equals(temporaryArchivePath,
                StringComparison.OrdinalIgnoreCase) && File.Exists(path));
        SkyrimNpcFinishCoreApplyResult blockedPromotion =
            await unsafeService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!blockedPromotion.Applied &&
               blockedPromotion.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-cleanup" &&
                   item.Message.Contains(temporaryArchivePath,
                       StringComparison.OrdinalIgnoreCase)) &&
               File.Exists(temporaryArchivePath) &&
               !Directory.Exists(outputRoot) && !File.Exists(archivePath),
            "Archive post-CreateNew refusal did not return cleanup failure evidence or preserve the exact surviving temp path.");
        File.Delete(temporaryArchivePath);
        SkyrimNpcFinishCoreApplyResult republished = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha,
            CancellationToken.None);
        Assert(republished.Applied,
            "Finish Core could not republish after a blocked archive promotion cleanup seam.");

        // Round-4 RED: a boundary can change the transaction-owned temp from
        // an archive file to a directory after the independent archive
        // readback. The second immediate gate must refuse the archive move,
        // and cleanup must report that the owned path has the wrong type.
        Directory.Delete(republished.OutputRoot!.Value.Value, recursive: true);
        File.Delete(republished.Archive!.Value.Value);
        string postReadbackTemporary = republished.Archive.Value.Value + ".tmp";
        int temporaryAncestorChecks = 0;
        FinishCoreTestOwnedFileLease? postReadbackLease = null;
        var postReadbackLeaseFactory = new FinishCoreTestOwnedLeaseFactory(
            lease =>
            {
                if (lease is FinishCoreTestOwnedFileLease fileLease &&
                    fileLease.ReadbackOpened)
                    postReadbackLease = fileLease;
            });
        var postReadbackGateService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            path =>
            {
                if (path.Equals(postReadbackTemporary,
                        StringComparison.OrdinalIgnoreCase))
                {
                    temporaryAncestorChecks++;
                    if (temporaryAncestorChecks == 2 &&
                        postReadbackLease is not null &&
                        !postReadbackLease.IsForeign)
                    {
                        postReadbackLease.ReplaceWithDirectory(
                            "post-readback archive directory survivor");
                        return true;
                    }
                }
                return false;
            },
            ownedLeaseFactory: postReadbackLeaseFactory);
        SkyrimNpcFinishCoreApplyResult postReadbackRefusal =
            await postReadbackGateService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!postReadbackRefusal.Applied && temporaryAncestorChecks >= 2 &&
               postReadbackRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-cleanup" &&
                   item.Message.Contains(postReadbackTemporary,
                       StringComparison.OrdinalIgnoreCase)) &&
               Directory.Exists(postReadbackTemporary) &&
               !Directory.Exists(republished.OutputRoot.Value.Value) &&
               !File.Exists(republished.Archive.Value.Value),
            "The post-readback archive gate did not refuse a replaced temp directory or report its cleanup survivor.");
        Directory.Delete(postReadbackTemporary, recursive: true);
        republished = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha,
            CancellationToken.None);
        Assert(republished.Applied,
            "Finish Core could not republish after the post-readback archive gate proof.");

        // Round-5 RED: replacing the transaction-owned archive temp with a
        // different ordinary file after readback must be refused and the
        // replacement must survive cleanup diagnostics.
        Directory.Delete(republished.OutputRoot!.Value.Value, recursive: true);
        File.Delete(republished.Archive!.Value.Value);
        bool archiveReplacedBySameType = false;
        string sameTypeArchiveSurvivor = republished.Archive.Value.Value + ".tmp";
        var sameTypeArchiveLeaseFactory = new FinishCoreTestOwnedLeaseFactory(
            lease =>
            {
                if (lease is FinishCoreTestOwnedFileLease fileLease &&
                    fileLease.ReadbackOpened &&
                    !fileLease.IsForeign &&
                    fileLease.Path.Value.Equals(
                        sameTypeArchiveSurvivor,
                        StringComparison.OrdinalIgnoreCase))
                {
                    fileLease.ReplaceWithFile("archive survivor");
                    archiveReplacedBySameType = true;
                }
            });
        var sameTypeArchiveService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            ownedLeaseFactory: sameTypeArchiveLeaseFactory);
        SkyrimNpcFinishCoreApplyResult sameTypeArchiveRefusal =
            await sameTypeArchiveService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!sameTypeArchiveRefusal.Applied && archiveReplacedBySameType &&
               sameTypeArchiveRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-cleanup" &&
                   item.Message.Contains(sameTypeArchiveSurvivor,
                       StringComparison.OrdinalIgnoreCase)) &&
               File.Exists(sameTypeArchiveSurvivor) &&
               !Directory.Exists(republished.OutputRoot.Value.Value) &&
               !File.Exists(republished.Archive.Value.Value),
            "Archive cleanup did not refuse or diagnose a same-type replacement after readback.");
        File.Delete(sameTypeArchiveSurvivor);
        republished = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha,
            CancellationToken.None);
        Assert(republished.Applied,
            "Finish Core could not republish after the same-type archive replacement proof.");

        // Round-4 RED: an owned staging directory can be replaced by a file
        // before cleanup.  The exact-owned cleanup must not treat that file
        // as absent.
        Directory.Delete(republished.OutputRoot!.Value.Value, recursive: true);
        File.Delete(republished.Archive!.Value.Value);
        bool stagingReplacedByFile = false;
        string? stagingSurvivor = null;
        var stagingTypeLeaseFactory = new FinishCoreTestOwnedLeaseFactory(
            lease =>
            {
                if (lease is FinishCoreTestOwnedDirectoryLease directoryLease &&
                    !stagingReplacedByFile &&
                    Directory.Exists(directoryLease.Path.Value) &&
                    Directory.EnumerateFileSystemEntries(directoryLease.Path.Value)
                        .Any())
                {
                    stagingSurvivor = directoryLease.Path.Value;
                    directoryLease.ReplaceWithFile("staging survivor");
                    stagingReplacedByFile = true;
                }
            });
        var stagingTypeService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            ownedLeaseFactory: stagingTypeLeaseFactory);
        SkyrimNpcFinishCoreApplyResult stagingTypeRefusal =
            await stagingTypeService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!stagingTypeRefusal.Applied && stagingReplacedByFile &&
               stagingSurvivor is not null &&
               stagingTypeRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-cleanup" &&
                   item.Message.Contains(stagingSurvivor,
                       StringComparison.OrdinalIgnoreCase)) &&
               File.Exists(stagingSurvivor) &&
               !Directory.Exists(republished.OutputRoot.Value.Value) &&
               !File.Exists(republished.Archive.Value.Value),
            "Owned staging cleanup silently treated a replacement file as absent.");
        File.Delete(stagingSurvivor!);
        republished = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha,
            CancellationToken.None);
        Assert(republished.Applied,
            "Finish Core could not republish after the staging wrong-type cleanup proof.");

        // Round-5 RED: replacing the owned staging directory with another
        // ordinary directory before promotion must never promote or delete
        // the replacement, and cleanup must report its survivor path.
        Directory.Delete(republished.OutputRoot!.Value.Value, recursive: true);
        File.Delete(republished.Archive!.Value.Value);
        bool stagingReplacedBySameType = false;
        string? sameTypeStagingSurvivor = null;
        var sameTypeStagingLeaseFactory = new FinishCoreTestOwnedLeaseFactory(
            lease =>
            {
                if (lease is FinishCoreTestOwnedDirectoryLease directoryLease &&
                    !stagingReplacedBySameType &&
                    Directory.Exists(directoryLease.Path.Value) &&
                    Directory.EnumerateFileSystemEntries(directoryLease.Path.Value)
                        .Any())
                {
                    sameTypeStagingSurvivor = directoryLease.Path.Value;
                    directoryLease.ReplaceWithDirectory("same-type staging survivor");
                    stagingReplacedBySameType = true;
                }
            });
        var sameTypeStagingService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            ownedLeaseFactory: sameTypeStagingLeaseFactory);
        SkyrimNpcFinishCoreApplyResult sameTypeStagingRefusal =
            await sameTypeStagingService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!sameTypeStagingRefusal.Applied && stagingReplacedBySameType &&
               sameTypeStagingSurvivor is not null &&
               sameTypeStagingRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-cleanup" &&
                   item.Message.Contains(sameTypeStagingSurvivor,
                       StringComparison.OrdinalIgnoreCase)) &&
               Directory.Exists(sameTypeStagingSurvivor) &&
               !Directory.Exists(republished.OutputRoot.Value.Value) &&
               !File.Exists(republished.Archive.Value.Value),
            "Staging cleanup did not refuse or diagnose a same-type replacement before promotion.");
        Directory.Delete(sameTypeStagingSurvivor!, recursive: true);
        republished = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha,
            CancellationToken.None);
        Assert(republished.Applied,
            "Finish Core could not republish after the same-type staging replacement proof.");

        string refusalOutputRoot = Path.Combine(fixture.Root, "preexisting-tmp-output");
        string refusalArchive = Path.Combine(fixture.Root, "preexisting-tmp-output.zip");
        SkyrimNpcFinishCoreRequest refusalRequest = request with
        {
            Output = request.Output with
            {
                Root = new WorkspacePath(refusalOutputRoot),
                Archive = new WorkspacePath(refusalArchive)
            }
        };
        Sha256Hash refusalRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
            refusalRequest, new WorkspacePath(fixture.Root));
        WorkspacePath refusalProposalPath = new(Path.Combine(
            fixture.Root, "preexisting-tmp-proposal.json"));
        SkyrimNpcFinishCoreProposalResult refusalAnalysis = await service.AnalyzeAsync(
            refusalRequest,
            refusalRequestSha,
            refusalProposalPath,
            CancellationToken.None);
        Assert(refusalAnalysis.Proposed && refusalAnalysis.Proposal is not null,
            "The pre-existing temporary archive refusal setup did not produce a proposal.");
        string refusalTemporaryArchive = refusalArchive + ".tmp";
        await File.WriteAllTextAsync(refusalTemporaryArchive, "sentinel");
        SkyrimNpcFinishCoreApplyResult preexistingTemporaryRefusal =
            await service.ApplyAsync(
                refusalRequest,
                refusalRequestSha,
                refusalAnalysis.Proposal!,
                refusalAnalysis.ProposalSha256!.Value,
                CancellationToken.None);
        Assert(!preexistingTemporaryRefusal.Applied &&
               preexistingTemporaryRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-exception" &&
                   item.Message.Contains("temporary path already exists",
                       StringComparison.OrdinalIgnoreCase)) &&
               File.ReadAllText(refusalTemporaryArchive) == "sentinel" &&
               !Directory.Exists(refusalOutputRoot) &&
               !File.Exists(refusalArchive),
            "An early temporary-path refusal did not preserve the pre-existing sentinel or exact output boundary.");
        File.Delete(refusalTemporaryArchive);
        Directory.CreateDirectory(refusalTemporaryArchive);
        SkyrimNpcFinishCoreApplyResult preexistingTemporaryDirectoryRefusal =
            await service.ApplyAsync(
                refusalRequest,
                refusalRequestSha,
                refusalAnalysis.Proposal!,
                refusalAnalysis.ProposalSha256!.Value,
                CancellationToken.None);
        Assert(!preexistingTemporaryDirectoryRefusal.Applied &&
               preexistingTemporaryDirectoryRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-exception" &&
                   item.Message.Contains("temporary path already exists",
                       StringComparison.OrdinalIgnoreCase)) &&
               Directory.Exists(refusalTemporaryArchive) &&
               !Directory.Exists(refusalOutputRoot) &&
               !File.Exists(refusalArchive),
            "An early temporary-directory refusal did not preserve the pre-existing sentinel or exact output boundary.");
        Directory.Delete(refusalTemporaryArchive, recursive: true);
        File.Delete(refusalProposalPath.Value);

        // Round-4 RED: corrupt the transaction-owned temp archive after the
        // deterministic writer closes it.  Apply must independently read back
        // the temp archive before moving it into the final archive path.
        Directory.Delete(republished.OutputRoot!.Value.Value, recursive: true);
        File.Delete(republished.Archive!.Value.Value);
        bool archiveCorrupted = false;
        var archiveReadbackService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            path =>
            {
                if (!archiveCorrupted &&
                    path.Equals(republished.Archive.Value.Value + ".tmp",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path))
                {
                    archiveCorrupted = true;
                    File.WriteAllBytes(path, [0x13, 0x37]);
                }
                return false;
            });
        SkyrimNpcFinishCoreApplyResult corruptArchive =
            await archiveReadbackService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!corruptArchive.Applied && archiveCorrupted &&
               corruptArchive.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-exception" ||
                   item.Code == "finish-core-verify-archive") &&
               !Directory.Exists(republished.OutputRoot.Value.Value) &&
               !File.Exists(republished.Archive.Value.Value),
            "Apply promoted an archive whose bytes failed an independent temp readback.");

        // Round-4 RED: if cancellation occurs after transaction ownership is
        // established and cleanup is blocked by the boundary probe, the
        // cancellation exception must carry the cleanup diagnostic/survivor.
        using var cleanupCancelled = new CancellationTokenSource();
        bool cancellationTriggered = false;
        var cancellationService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            path =>
            {
                if (!cancellationTriggered &&
                    Path.GetFileName(path).StartsWith(
                        ".finish-core-transaction-", StringComparison.Ordinal))
                {
                    cancellationTriggered = true;
                    cleanupCancelled.Cancel();
                    return false;
                }
                return cancellationTriggered;
            });
        OperationCanceledException? cancellationException = null;
        try
        {
            await cancellationService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                cleanupCancelled.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancellationException = exception;
        }
        Assert(cancellationException is not null &&
               cancellationException.Data.Contains("finish-core-apply-cleanup"),
            "Cancellation lost cleanup diagnostics when owned staging cleanup was blocked.");

        foreach (string survivor in Directory.EnumerateDirectories(
                     fixture.Root,
                     ".finish-core-transaction-*",
                     SearchOption.TopDirectoryOnly).ToArray())
            Directory.Delete(survivor, recursive: true);
        SkyrimNpcFinishCoreApplyResult restored = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha, CancellationToken.None);
        Assert(restored.Applied, "Finish Core could not restore the transaction fixture after cancellation cleanup proof.");

        // Portable descendant-boundary seam: refuse the populated staging
        // tree immediately before archive creation/promotion, without needing
        // host support for creating a real Windows reparse point.
        Directory.Delete(restored.OutputRoot!.Value.Value, recursive: true);
        File.Delete(restored.Archive!.Value.Value);
        bool descendantChecked = false;
        bool rejectDescendant = true;
        var descendantService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            path =>
            {
                if (rejectDescendant &&
                    Path.GetFileName(path).StartsWith(
                        ".finish-core-transaction-", StringComparison.Ordinal) &&
                    Directory.Exists(path) &&
                    Directory.EnumerateFileSystemEntries(path).Any())
                {
                    descendantChecked = true;
                    rejectDescendant = false;
                    return true;
                }
                return false;
            });
        SkyrimNpcFinishCoreApplyResult descendantRefusal =
            await descendantService.ApplyAsync(
                request, requestSha, fixture.Proposal, proposalSha,
                CancellationToken.None);
        Assert(!descendantRefusal.Applied && descendantChecked &&
               descendantRefusal.Diagnostics.Any(item =>
                   item.Code == "finish-core-apply-exception" ||
                   item.Code == "finish-core-transaction-paths") &&
               !Directory.Exists(restored.OutputRoot.Value.Value) &&
               !File.Exists(restored.Archive.Value.Value),
            "The immediate pre-promotion descendant boundary was not enforced.");

        SkyrimNpcFinishCoreApplyResult restoredAfterDescendant = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha, CancellationToken.None);
        Assert(restoredAfterDescendant.Applied,
            "Finish Core could not restore the transaction fixture after descendant-boundary proof.");

        // Round-5 RED: cleanup for a valid nested output root must use its
        // exact staging parent, not the project root.
        string nestedParent = Path.Combine(fixture.Root, "nested-output-parent");
        string nestedOutputRoot = Path.Combine(nestedParent, "package", "output");
        string nestedArchive = Path.Combine(nestedParent, "archives", "output.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedOutputRoot)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nestedArchive)!);
        SkyrimNpcFinishCoreRequest nestedRequest = request with
        {
            Output = request.Output with
            {
                Root = new WorkspacePath(nestedOutputRoot),
                Archive = new WorkspacePath(nestedArchive)
            }
        };
        Sha256Hash nestedRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
            nestedRequest, new WorkspacePath(fixture.Root));
        WorkspacePath nestedProposalPath = new(Path.Combine(
            fixture.Root, "nested-output-proposal.json"));
        SkyrimNpcFinishCoreProposalResult nestedAnalysis = await service.AnalyzeAsync(
            nestedRequest, nestedRequestSha, nestedProposalPath, CancellationToken.None);
        Assert(nestedAnalysis.Proposed && nestedAnalysis.Proposal is not null,
            "Nested output-root cleanup setup did not produce a proposal.");
        bool nestedCleanupBoundary = false;
        var nestedCleanupService = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            new WorkspacePath(fixture.Root),
            _ => false,
            path =>
            {
                if (!nestedCleanupBoundary &&
                    Path.GetFileName(path).StartsWith(
                        ".finish-core-transaction-", StringComparison.Ordinal) &&
                    Directory.Exists(path) &&
                    Directory.EnumerateFileSystemEntries(path).Any())
                {
                    nestedCleanupBoundary = true;
                    return true;
                }
                return false;
            });
        SkyrimNpcFinishCoreApplyResult nestedRefusal =
            await nestedCleanupService.ApplyAsync(
                nestedRequest, nestedRequestSha, nestedAnalysis.Proposal!,
                nestedAnalysis.ProposalSha256!.Value, CancellationToken.None);
        Assert(!nestedRefusal.Applied && nestedCleanupBoundary &&
               !Directory.Exists(nestedOutputRoot) && !File.Exists(nestedArchive) &&
               !Directory.EnumerateDirectories(
                    nestedParent, ".finish-core-transaction-*",
                    SearchOption.AllDirectories).Any(),
            "Nested output-root refusal left its transaction staging directory behind.");
        File.Delete(nestedProposalPath.Value);
        Directory.Delete(nestedParent, recursive: true);

        SkyrimNpcFinishCoreApplyResult collision = await service.ApplyAsync(
            request, requestSha, fixture.Proposal, proposalSha, CancellationToken.None);
        Assert(!collision.Applied && collision.Diagnostics.Any(x =>
            x.Code == "finish-core-transaction-output-exists"),
            "Finish Core apply did not refuse an existing output root.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool cancelledRaised = false;
        try
        {
            await service.ApplyAsync(request, requestSha, fixture.Proposal, proposalSha,
                cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancelledRaised = true;
        }
        Assert(cancelledRaised, "Finish Core cancellation was relabeled as a normal refusal.");
    }

    private sealed class FinishCoreTestOwnedLeaseFactory(
        Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent = null) :
        NpcManager.Pipeline.ISkyrimNpcFinishCoreOwnedLeaseFactory
    {
        private readonly Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent =
            beforeEnsureCurrent;

        public NpcManager.Pipeline.ISkyrimNpcFinishCoreOwnedDirectoryLease
            CreateDirectory(
                WorkspacePath workspaceRoot,
                WorkspacePath path,
                string role)
        {
            _ = workspaceRoot;
            _ = role;
            if (File.Exists(path.Value) || Directory.Exists(path.Value))
                throw new IOException("The fake owned directory path already exists.");
            Directory.CreateDirectory(path.Value);
            return new FinishCoreTestOwnedDirectoryLease(
                path,
                beforeEnsureCurrent);
        }

        public NpcManager.Pipeline.ISkyrimNpcFinishCoreOwnedFileLease
            CreateFile(
                WorkspacePath workspaceRoot,
                WorkspacePath temporaryPath,
                WorkspacePath destinationPath,
                string role)
        {
            _ = workspaceRoot;
            _ = role;
            return new FinishCoreTestOwnedFileLease(
                temporaryPath,
                destinationPath,
                beforeEnsureCurrent);
        }
    }

    private abstract class FinishCoreTestOwnedLease(
        WorkspacePath initialPath,
        Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent) : IDisposable
    {
        private readonly Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent =
            beforeEnsureCurrent;

        public WorkspacePath Path { get; protected set; } = initialPath;

        public bool IsPromoted { get; protected set; }

        public bool IsForeign { get; private set; }

        protected void EnsureIdentity()
        {
            beforeEnsureCurrent?.Invoke(this);
            if (IsForeign)
                throw new UnauthorizedAccessException(
                    $"The fake owned path '{Path.Value}' no longer names its original object.");
        }

        protected void MarkForeign() => IsForeign = true;

        public abstract void Dispose();
    }

    private sealed class FinishCoreTestOwnedDirectoryLease(
        WorkspacePath initialPath,
        Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent) :
        FinishCoreTestOwnedLease(initialPath, beforeEnsureCurrent),
        NpcManager.Pipeline.ISkyrimNpcFinishCoreOwnedDirectoryLease
    {
        public void EnsureCurrent() => EnsureIdentity();

        public ImmutableArray<WorkspacePath> DeleteTree()
        {
            EnsureIdentity();
            Directory.Delete(Path.Value, recursive: true);
            return [];
        }

        public void PromoteNoOverwrite(WorkspacePath destination)
        {
            EnsureIdentity();
            if (File.Exists(destination.Value) || Directory.Exists(destination.Value))
                throw new IOException("The fake owned directory destination already exists.");
            Directory.Move(Path.Value, destination.Value);
            Path = destination;
            IsPromoted = true;
        }

        public void Release() => Dispose();

        public void ReplaceWithFile(string contents)
        {
            Directory.Delete(Path.Value, recursive: true);
            File.WriteAllText(Path.Value, contents);
            MarkForeign();
        }

        public void ReplaceWithDirectory(string sentinel)
        {
            Directory.Delete(Path.Value, recursive: true);
            Directory.CreateDirectory(Path.Value);
            File.WriteAllText(
                System.IO.Path.Combine(Path.Value, "foreign-sentinel.txt"),
                sentinel);
            MarkForeign();
        }

        public override void Dispose()
        {
        }
    }

    private sealed class FinishCoreTestOwnedFileLease(
        WorkspacePath initialPath,
        WorkspacePath destinationPath,
        Action<FinishCoreTestOwnedLease>? beforeEnsureCurrent) :
        FinishCoreTestOwnedLease(initialPath, beforeEnsureCurrent),
        NpcManager.Pipeline.ISkyrimNpcFinishCoreOwnedFileLease
    {
        private readonly string destinationPath = destinationPath.Value;
        private FileStream stream = new(
            initialPath.Value,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous);

        public bool ReadbackOpened { get; private set; }

        public void EnsureCurrent() => EnsureIdentity();

        public async ValueTask WriteAsync(
            Func<Stream, CancellationToken, ValueTask> writer,
            CancellationToken cancellationToken)
        {
            EnsureIdentity();
            await writer(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            EnsureIdentity();
        }

        public Stream OpenReadbackStream()
        {
            EnsureIdentity();
            ReadbackOpened = true;
            return new FileStream(
                Path.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous);
        }

        public void PromoteNoOverwrite()
        {
            EnsureIdentity();
            stream.Flush(flushToDisk: true);
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                throw new IOException("The fake owned file destination already exists.");
            File.Move(Path.Value, destinationPath);
            Path = new WorkspacePath(destinationPath);
            IsPromoted = true;
        }

        public bool TryDelete(out string? failure)
        {
            failure = null;
            if (IsForeign)
            {
                failure =
                    $"The fake owned file '{Path.Value}' no longer names its original object.";
                Dispose();
                return false;
            }
            try
            {
                EnsureIdentity();
                File.Delete(Path.Value);
                Dispose();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failure = exception.Message;
                Dispose();
                return false;
            }
        }

        public void ReplaceWithFile(string contents)
        {
            stream.Dispose();
            stream = null!;
            File.Delete(Path.Value);
            File.WriteAllText(Path.Value, contents);
            MarkForeign();
        }

        public void ReplaceWithDirectory(string sentinel)
        {
            stream.Dispose();
            stream = null!;
            File.Delete(Path.Value);
            Directory.CreateDirectory(Path.Value);
            File.WriteAllText(
                System.IO.Path.Combine(Path.Value, "foreign-sentinel.txt"),
                sentinel);
            MarkForeign();
        }

        public override void Dispose()
        {
            stream?.Dispose();
            stream = null!;
        }
    }

    private static Sha256Hash LegacyTreeHash(string root, string pluginPath)
    {
        string fullRoot = Path.GetFullPath(root);
        string excluded = Path.GetRelativePath(fullRoot, Path.GetFullPath(pluginPath))
            .Replace('\\', '/');
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(
                     fullRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(fullRoot, path)
                         .Replace('\\', '/'), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            if (string.Equals(relative, excluded, StringComparison.OrdinalIgnoreCase))
                continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }
}
