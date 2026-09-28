using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reviews one exact K-local Skyrim package and derives a new verified package
/// from it. Source packages and plugins are never overwritten.
/// </summary>
public sealed class SkyrimSavePackageService(
    IPackageInspectService inspector,
    IPackageVerifyService verifier,
    ISkyrimSavePluginTransformer pluginTransformer,
    ISkyrimSavePluginVerifier pluginVerifier,
    ISkyrimBsaService bsaService,
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimSavePackageService
{
    private const uint MasterFlag = 0x0000_0001U;
    private const uint LightFlag = 0x0000_0200U;
    private const string ManifestName = "npcmanager-package.json";
    private const string ProposalName = "npcmanager-save-proposal.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async ValueTask<SkyrimSavePackageReviewResult> ReviewAsync(
        SkyrimSavePackageReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRoots(request, diagnostics);
        if (HasErrors(diagnostics)) return RefusedReview(diagnostics);

        var manifestPath = new WorkspacePath(Path.Combine(
            request.SourceRoot.Value,
            ManifestName));
        PackageInspectResult inspection = await inspector.InspectAsync(
            new PackageInspectRequest(manifestPath),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(inspection.Diagnostics);
        if (!inspection.Valid || inspection.Identity is null ||
            HasErrors(diagnostics))
            return RefusedReview(diagnostics);

        PackageVerifyResult verification = await verifier.VerifyAsync(
            new PackageVerifyRequest(manifestPath),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(verification.Diagnostics);
        if (!verification.Verified || verification.Artifact is null ||
            HasErrors(diagnostics))
            return new SkyrimSavePackageReviewResult(
                false,
                null,
                verification,
                diagnostics.ToImmutable());

        PackageManifestIdentity identity = inspection.Identity;
        if (identity.ManifestSha256 != verification.Artifact.ManifestSha256)
            Error(diagnostics, "save-package-manifest-substitution",
                "The package manifest changed between inspection and verification.");
        if (!string.Equals(identity.Edition, "skyrimse",
                StringComparison.OrdinalIgnoreCase))
            Error(diagnostics, "save-package-edition-unsupported",
                "SKY-GUI-023 accepts only Skyrim Special Edition packages.");
        PackageManifestFile[] pluginRows = PluginRows(identity);
        if (pluginRows.Length != 1)
            Error(diagnostics, "save-package-plugin-inventory",
                "The verified package must declare exactly one output plugin.");
        if (HasErrors(diagnostics))
            return new SkyrimSavePackageReviewResult(
                false,
                null,
                verification,
                diagnostics.ToImmutable());

        string pluginPath = ResolveUnder(
            request.SourceRoot.Value,
            pluginRows[0].RelativePath);
        try
        {
            Sha256Hash beforeRead = await HashFileAsync(
                pluginPath,
                cancellationToken).ConfigureAwait(false);
            if (beforeRead != pluginRows[0].Sha256)
            {
                Error(diagnostics, "save-package-plugin-substitution",
                    "The output plugin changed after package verification.");
                return new SkyrimSavePackageReviewResult(
                    false,
                    null,
                    verification,
                    diagnostics.ToImmutable());
            }

            uint flags = ReadTes4Flags(pluginPath);
            PluginInspection pluginInspection = await pluginReader.ReadAsync(
                new PluginReadRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(pluginPath)),
                cancellationToken).ConfigureAwait(false);
            Sha256Hash afterRead = await HashFileAsync(
                pluginPath,
                cancellationToken).ConfigureAwait(false);
            if (afterRead != beforeRead || afterRead != pluginRows[0].Sha256)
            {
                Error(diagnostics, "save-package-plugin-substitution",
                    "The output plugin changed while its save facts were read.");
                return new SkyrimSavePackageReviewResult(
                    false,
                    null,
                    verification,
                    diagnostics.ToImmutable());
            }

            diagnostics.AddRange(pluginInspection.Diagnostics);
            int targetCount = pluginInspection.Records.Count(item =>
                item.IsNpc && item.FormId == identity.TargetFormId);
            if (targetCount != 1)
                Error(diagnostics, "save-package-target-mismatch",
                    "The output plugin must contain the manifest target NPC exactly once.");

            PriorOutputFacts priorFacts = ReadPriorOutputFacts(
                request.SourceRoot.Value,
                identity,
                diagnostics);
            SkyrimSavePackageSnapshot snapshot = CreateSnapshot(
                identity,
                flags,
                pluginInspection,
                DetectEncoding(pluginPath),
                priorFacts);
            SkyrimSavePackageOptions options =
                SkyrimSavePackageRules.CreateOptions(snapshot, request.Choices);
            SkyrimSavePackageDecision decision =
                SkyrimSavePackageRules.Save(snapshot, options);
            diagnostics.AddRange(decision.Diagnostics);
            if (HasErrors(diagnostics) || !decision.Accepted)
                return new SkyrimSavePackageReviewResult(
                    false,
                    null,
                    verification,
                    diagnostics.ToImmutable());

            var artifact = new SkyrimSavePackageReviewArtifact(
                "2",
                "skyrim-save-package-production-review",
                request.SourceRoot,
                request.OutputRoot,
                identity.ManifestPath,
                identity.ManifestSha256,
                snapshot,
                options,
                true,
                false);
            return new SkyrimSavePackageReviewResult(
                true,
                artifact,
                verification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException)
        {
            Error(diagnostics, "save-package-plugin-read-failed",
                exception.Message);
            return new SkyrimSavePackageReviewResult(
                false,
                null,
                verification,
                diagnostics.ToImmutable());
        }
    }

    public async ValueTask<SkyrimSavePackageExecutionResult> ExecuteAsync(
        SkyrimSavePackageExecutionRequest request,
        IProgress<SkyrimSavePackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string temporaryRoot = request.Review.OutputRoot.Value + ".tmp-" +
                               Guid.NewGuid().ToString("N");

        try
        {
            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.Revalidate,
                8,
                "Revalidating source bytes, destination, and every reviewed choice."));
            SkyrimSavePackageReviewResult current = await ReviewAsync(
                new SkyrimSavePackageReviewRequest(
                    request.Review.SourceRoot,
                    request.Review.OutputRoot,
                    ToChoices(request.Review.Options)),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(current.Diagnostics);
            if (!current.Accepted || current.Artifact is null ||
                !BindingMatches(request.Review, current.Artifact))
            {
                if (current.Accepted && current.Artifact is not null)
                    Error(diagnostics, "save-package-review-stale",
                        "The source package, reviewed choices, or destination changed after review.");
                return RefusedExecution(diagnostics, current.PackageVerification);
            }

            PackageInspectResult sourceInspection = await inspector.InspectAsync(
                new PackageInspectRequest(current.Artifact.ManifestPath),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(sourceInspection.Diagnostics);
            if (!sourceInspection.Valid || sourceInspection.Identity is null ||
                HasErrors(diagnostics))
                return RefusedExecution(diagnostics, current.PackageVerification);
            PackageManifestIdentity sourceIdentity = sourceInspection.Identity;

            Directory.CreateDirectory(temporaryRoot);
            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.PersistProposal,
                18,
                "Persisting the immutable hash-bound proposal before game-facing writes."));
            Sha256Hash proposalHash = await WriteProposalAsync(
                temporaryRoot,
                current.Artifact,
                cancellationToken).ConfigureAwait(false);

            PackageManifestFile pluginRow = PluginRows(sourceIdentity).Single();
            string sourcePluginPath = ResolveUnder(
                current.Artifact.SourceRoot.Value,
                pluginRow.RelativePath);
            string temporaryPluginPath = ResolveUnder(
                temporaryRoot,
                pluginRow.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(
                temporaryPluginPath)!);

            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.TransformPlugin,
                35,
                "Writing and reopening the reviewed Skyrim plugin result."));
            SkyrimSavePluginTransformArtifact pluginArtifact =
                await ProducePluginAsync(
                    sourcePluginPath,
                    temporaryPluginPath,
                    current.Artifact,
                    cancellationToken).ConfigureAwait(false);
            SkyrimSavePluginVerifyResult pluginReadback =
                await pluginVerifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(temporaryPluginPath),
                        pluginArtifact),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(pluginReadback.Diagnostics);
            if (!pluginReadback.Verified ||
                pluginReadback.Artifact is null)
                return RefusedExecution(
                    diagnostics,
                    current.PackageVerification);

            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.TransformAssets,
                58,
                "Producing the reviewed loose/archive layout and verifying member bytes."));
            LayoutResult layout = await ProduceLayoutAsync(
                current.Artifact.SourceRoot.Value,
                temporaryRoot,
                sourceIdentity,
                pluginRow.RelativePath,
                current.Artifact.Options,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics))
                return RefusedExecution(diagnostics, current.PackageVerification);

            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.WriteManifest,
                72,
                "Regenerating the canonical manifest from actual output files."));
            await WriteManifestAsync(
                temporaryRoot,
                sourceIdentity,
                cancellationToken).ConfigureAwait(false);

            PackageVerifyResult temporaryVerification = await verifier.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(Path.Combine(
                    temporaryRoot,
                    ManifestName))),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(temporaryVerification.Diagnostics);
            PackageVerifyResult sourceAfter = await verifier.VerifyAsync(
                new PackageVerifyRequest(current.Artifact.ManifestPath),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(sourceAfter.Diagnostics);
            if (!temporaryVerification.Verified ||
                temporaryVerification.Artifact is null ||
                !VerificationMatches(
                    current.PackageVerification?.Artifact,
                    sourceAfter.Artifact))
            {
                if (!VerificationMatches(
                        current.PackageVerification?.Artifact,
                        sourceAfter.Artifact))
                    Error(diagnostics, "save-package-source-changed",
                        "The source package changed during production.");
                return RefusedExecution(diagnostics, temporaryVerification);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryRoot, request.Review.OutputRoot.Value);
            temporaryRoot = string.Empty;

            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.VerifyDestination,
                90,
                "Reopening the promoted package and independently verifying every file."));
            var outputManifest = new WorkspacePath(Path.Combine(
                request.Review.OutputRoot.Value,
                ManifestName));
            PackageVerifyResult outputVerification = await verifier.VerifyAsync(
                new PackageVerifyRequest(outputManifest),
                CancellationToken.None).ConfigureAwait(false);
            diagnostics.AddRange(outputVerification.Diagnostics);
            if (!outputVerification.Verified ||
                outputVerification.Artifact is null ||
                !VerificationMatches(
                    temporaryVerification.Artifact,
                    outputVerification.Artifact))
            {
                Error(diagnostics, "save-package-destination-readback",
                    "The final destination did not match its verified temporary package.");
                TryRemoveCreatedDestination(request.Review.OutputRoot, diagnostics);
                return RefusedExecution(diagnostics, outputVerification);
            }

            ImmutableArray<SkyrimSavePackageFileComparison> comparisons =
                CompareProducedFiles(
                    current.PackageVerification?.Artifact,
                    outputVerification.Artifact);
            WorkspacePath finalPlugin = new(ResolveUnder(
                request.Review.OutputRoot.Value,
                pluginRow.RelativePath));
            pluginArtifact = pluginArtifact with { OutputPlugin = finalPlugin };
            SkyrimSavePluginVerificationArtifact pluginVerification =
                pluginReadback.Artifact with { Plugin = finalPlugin };
            SkyrimBsaBuildArtifact? finalArchive = layout.ArchiveBuild is null
                ? null
                : layout.ArchiveBuild with
                {
                    OutputArchive = new WorkspacePath(ResolveUnder(
                        request.Review.OutputRoot.Value,
                        RelativeFromRoot(
                            temporaryRoot: layout.TemporaryPackageRoot,
                            layout.ArchiveBuild.OutputArchive.Value)))
                };
            var artifact = new SkyrimSavePackageExecutionArtifact(
                "2",
                "skyrim-save-package-production",
                request.Review.SourceRoot,
                request.Review.OutputRoot,
                current.Artifact.ManifestSha256,
                outputVerification.Artifact.ManifestSha256,
                current.Artifact.Snapshot.OutputPlugin,
                current.Artifact.Snapshot.TargetFormId,
                comparisons,
                true,
                false,
                proposalHash,
                pluginArtifact,
                pluginVerification,
                finalArchive,
                layout.ArchiveMembers,
                true);
            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.Complete,
                100,
                "Verified fresh package production complete; runtime authority remains false."));
            return new SkyrimSavePackageExecutionResult(
                true,
                artifact,
                outputVerification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or OverflowException)
        {
            Error(diagnostics, "save-package-production-failed",
                exception.Message);
            return RefusedExecution(diagnostics, null);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private async ValueTask<SkyrimSavePluginTransformArtifact>
        ProducePluginAsync(
            string sourcePlugin,
            string outputPlugin,
            SkyrimSavePackageReviewArtifact review,
            CancellationToken cancellationToken)
    {
        if (RequiresPluginTransform(review))
        {
            SkyrimSavePluginTransformResult transformed =
                await pluginTransformer.TransformAsync(
                    new SkyrimSavePluginTransformRequest(
                        new WorkspacePath(sourcePlugin),
                        new WorkspacePath(outputPlugin),
                        review.Snapshot,
                        review.Options),
                    cancellationToken).ConfigureAwait(false);
            if (!transformed.Completed || transformed.Artifact is null)
                throw new InvalidDataException(string.Join(
                    "; ",
                    transformed.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
            return transformed.Artifact;
        }

        Sha256Hash sourceHash = await CopyAndHashAsync(
            sourcePlugin,
            outputPlugin,
            cancellationToken).ConfigureAwait(false);
        Sha256Hash sourceAfter = await HashFileAsync(
            sourcePlugin,
            cancellationToken).ConfigureAwait(false);
        if (sourceHash != sourceAfter)
            throw new InvalidDataException(
                "The source plugin changed during exact-byte promotion.");
        return new SkyrimSavePluginTransformArtifact(
            new WorkspacePath(sourcePlugin),
            new WorkspacePath(outputPlugin),
            sourceHash,
            sourceHash,
            review.Snapshot.MarkAsMaster,
            review.Snapshot.LightMaster,
            SkyrimSaveEncodingMode.PreservePackageBytes,
            review.Snapshot.Npcs
                .Select(item => item.FormId)
                .OrderBy(item => item.Value)
                .ToImmutableArray(),
            null,
            [],
            review.Snapshot.AuthoredRecordCount,
            true,
            false);
    }

    private async ValueTask<LayoutResult> ProduceLayoutAsync(
        string sourceRoot,
        string temporaryRoot,
        PackageManifestIdentity identity,
        AssetPath pluginPath,
        SkyrimSavePackageOptions options,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        PackageManifestFile[] rows = identity.Files
            .Where(item =>
                item.RelativePath != pluginPath &&
                !string.Equals(
                    item.RelativePath.Value,
                    ProposalName,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RelativePath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var archiveMembers =
            ImmutableArray.CreateBuilder<SkyrimBsaMemberArtifact>();

        if (options.ArchiveMode ==
            SkyrimSaveArchiveMode.PreservePackageLayout)
        {
            foreach (PackageManifestFile row in rows)
                await CopyArtifactAsync(
                    sourceRoot,
                    temporaryRoot,
                    row,
                    cancellationToken).ConfigureAwait(false);
            return new LayoutResult(
                temporaryRoot,
                null,
                archiveMembers.ToImmutable());
        }

        var occupied = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        if (options.ArchiveMode == SkyrimSaveArchiveMode.Loose)
        {
            foreach (PackageManifestFile row in rows.Where(item =>
                         !IsArchive(item.RelativePath)))
            {
                occupied.Add(row.RelativePath.Value);
                await CopyArtifactAsync(
                    sourceRoot,
                    temporaryRoot,
                    row,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (PackageManifestFile archive in rows.Where(item =>
                         IsArchive(item.RelativePath)))
            {
                string extractionRoot = Path.Combine(
                    temporaryRoot,
                    ".extract-" + Guid.NewGuid().ToString("N"));
                SkyrimBsaExtractResult extracted = await bsaService.ExtractAsync(
                    new SkyrimBsaExtractRequest(
                        new WorkspacePath(ResolveUnder(
                            sourceRoot,
                            archive.RelativePath)),
                        new WorkspacePath(extractionRoot)),
                    cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(extracted.Diagnostics);
                if (!extracted.Extracted || extracted.Artifact is null)
                    return new LayoutResult(
                        temporaryRoot,
                        null,
                        archiveMembers.ToImmutable());
                try
                {
                    foreach (SkyrimBsaMemberArtifact member in
                             extracted.Artifact.Members)
                    {
                        if (!occupied.Add(member.Path.Value))
                            throw new InvalidDataException(
                                $"Loose/archive provider collision for '{member.Path.Value}'.");
                        await CopyExactMemberAsync(
                            extractionRoot,
                            temporaryRoot,
                            member,
                            cancellationToken).ConfigureAwait(false);
                        archiveMembers.Add(member);
                    }
                }
                finally
                {
                    TryDeleteDirectory(extractionRoot);
                }
            }
            return new LayoutResult(
                temporaryRoot,
                null,
                archiveMembers.ToImmutable());
        }

        string inputRoot = Path.Combine(
            temporaryRoot,
            ".archive-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputRoot);
        try
        {
            foreach (PackageManifestFile row in rows.Where(item =>
                         !IsArchive(item.RelativePath)))
            {
                if (IsArchivable(row.RelativePath))
                {
                    if (!occupied.Add(row.RelativePath.Value))
                        throw new InvalidDataException(
                            $"Duplicate archive provider '{row.RelativePath.Value}'.");
                    await CopyArtifactAsync(
                        sourceRoot,
                        inputRoot,
                        row,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await CopyArtifactAsync(
                        sourceRoot,
                        temporaryRoot,
                        row,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (PackageManifestFile archive in rows.Where(item =>
                         IsArchive(item.RelativePath)))
            {
                string extractionRoot = Path.Combine(
                    temporaryRoot,
                    ".extract-" + Guid.NewGuid().ToString("N"));
                SkyrimBsaExtractResult extracted = await bsaService.ExtractAsync(
                    new SkyrimBsaExtractRequest(
                        new WorkspacePath(ResolveUnder(
                            sourceRoot,
                            archive.RelativePath)),
                        new WorkspacePath(extractionRoot)),
                    cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(extracted.Diagnostics);
                if (!extracted.Extracted || extracted.Artifact is null)
                    return new LayoutResult(
                        temporaryRoot,
                        null,
                        archiveMembers.ToImmutable());
                try
                {
                    foreach (SkyrimBsaMemberArtifact member in
                             extracted.Artifact.Members)
                    {
                        if (!occupied.Add(member.Path.Value))
                            throw new InvalidDataException(
                                $"Loose/archive provider collision for '{member.Path.Value}'.");
                        await CopyExactMemberAsync(
                            extractionRoot,
                            inputRoot,
                            member,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    TryDeleteDirectory(extractionRoot);
                }
            }

            if (occupied.Count == 0)
                throw new InvalidDataException(
                    "BSA mode requires at least one admitted loose or archived asset.");
            string archiveName =
                Path.GetFileNameWithoutExtension(
                    identity.OutputPlugin) + ".bsa";
            var outputArchive = new WorkspacePath(Path.Combine(
                temporaryRoot,
                archiveName));
            SkyrimBsaBuildResult built = await bsaService.BuildAsync(
                new SkyrimBsaBuildRequest(
                    new WorkspacePath(inputRoot),
                    outputArchive,
                    occupied
                        .OrderBy(item => item,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(item => new AssetPath(item))
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(built.Diagnostics);
            if (!built.Written || built.Artifact is null)
                return new LayoutResult(
                    temporaryRoot,
                    null,
                    archiveMembers.ToImmutable());
            archiveMembers.AddRange(built.Artifact.Members);
            return new LayoutResult(
                temporaryRoot,
                built.Artifact,
                archiveMembers.ToImmutable());
        }
        finally
        {
            TryDeleteDirectory(inputRoot);
        }
    }

    private static async ValueTask WriteManifestAsync(
        string outputRoot,
        PackageManifestIdentity source,
        CancellationToken cancellationToken)
    {
        var sourceKinds = source.Files.ToDictionary(
            item => item.RelativePath.Value,
            item => item.Kind,
            StringComparer.OrdinalIgnoreCase);
        string[] files = Directory.EnumerateFiles(
                outputRoot,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                ManifestName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(outputRoot, path),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0 || files.Length > 10_000)
            throw new InvalidDataException(
                "The produced package must contain 1 to 10,000 artifacts.");

        var artifacts = new List<object>(files.Length);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativeText = Path.GetRelativePath(outputRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var relative = new AssetPath(relativeText);
            long length = new FileInfo(file).Length;
            if (length <= 0)
                throw new InvalidDataException(
                    $"Produced artifact '{relative.Value}' is empty.");
            Sha256Hash hash = await HashFileAsync(
                file,
                cancellationToken).ConfigureAwait(false);
            string kind = KindFor(
                relative,
                source.OutputPlugin,
                sourceKinds);
            artifacts.Add(new
            {
                kind,
                relativePath = relative.Value,
                byteLength = length,
                sha256 = hash.Value
            });
        }

        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = source.SchemaVersion,
            edition = source.Edition,
            presetFormat = source.PresetFormat,
            sourcePreset = source.SourcePreset,
            sourcePresetSha256 = source.SourcePresetSha256.Value,
            sourcePlugin = source.SourcePlugin,
            sourcePluginSha256 = source.SourcePluginSha256.Value,
            outputPlugin = source.OutputPlugin,
            targetFormId = source.TargetFormId.ToString(),
            artifacts
        }, JsonOptions);
        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, ManifestName),
            manifest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Sha256Hash> WriteProposalAsync(
        string outputRoot,
        SkyrimSavePackageReviewArtifact review,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            artifactKind = "skyrim-save-package-proposal",
            sourceManifest = ManifestName,
            sourceManifestSha256 = review.ManifestSha256.Value,
            outputPlugin = review.Snapshot.OutputPlugin.Value,
            targetFormId = review.Snapshot.TargetFormId.ToString(),
            npcFormIds = review.Snapshot.Npcs
                .Select(item => item.FormId.ToString())
                .ToArray(),
            options = new
            {
                scope = review.Options.Scope.ToString(),
                targetMode = review.Options.TargetMode.ToString(),
                markAsMaster = review.Options.MarkAsMaster,
                lightMaster = review.Options.LightMaster,
                encoding = review.Options.EncodingMode.ToString(),
                archive = review.Options.ArchiveMode.ToString(),
                leveledList = review.Options.LeveledListMode.ToString(),
                leveledListEditorId =
                    review.Options.LeveledListEditorId,
                noDuplicateLeveledEntries =
                    review.Options.NoDuplicateLeveledEntries
            },
            immutableOutputs = new
            {
                faceGeom = review.Snapshot.HasFaceGeom,
                faceTint = review.Snapshot.HasFaceTint,
                bodySlide = review.Snapshot.HasBodySlideSidecar,
                bodyGen = review.Snapshot.HasBodyGen,
                applyScript = review.Snapshot.HasApplyScript,
                authoredRecords = review.Snapshot.AuthoredRecordCount
            },
            noWriteToSource = true,
            runtimeAuthority = false
        }, JsonOptions);
        string path = Path.Combine(outputRoot, ProposalName);
        await File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken).ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private void ValidateRoots(
        SkyrimSavePackageReviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.SourceRoot.IsUnder(labRoot))
            Error(diagnostics, "save-package-source-outside-lab",
                "The source package must remain under the K-only lab root.");
        if (!Directory.Exists(request.SourceRoot.Value))
            Error(diagnostics, "save-package-source-missing",
                "The source package directory does not exist.");
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.SourceRoot));
            if (HasReparsePoint(request.SourceRoot.Value))
                Error(diagnostics, "save-package-source-reparse",
                    "The source package root cannot be a reparse point.");
        }

        if (!request.OutputRoot.IsUnder(labRoot))
            Error(diagnostics, "save-package-output-outside-lab",
                "The destination package must remain under the K-only lab root.");
        if (request.OutputRoot.IsUnder(request.SourceRoot) ||
            request.SourceRoot.IsUnder(request.OutputRoot))
            Error(diagnostics, "save-package-root-overlap",
                "Source and destination package roots must not overlap.");
        if (File.Exists(request.OutputRoot.Value) ||
            Directory.Exists(request.OutputRoot.Value))
            Error(diagnostics, "save-package-output-exists",
                "Fresh and fresh-derived package writes never overwrite a destination.");
        string? parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            Error(diagnostics, "save-package-output-parent-missing",
                "The destination parent directory must already exist.");
        else
        {
            diagnostics.AddRange(policy.Evaluate(
                labRoot,
                new WorkspacePath(parent)));
            if (HasReparsePoint(parent))
                Error(diagnostics, "save-package-output-parent-reparse",
                    "The destination parent cannot be a reparse point.");
        }
    }

    private static SkyrimSavePackageSnapshot CreateSnapshot(
        PackageManifestIdentity identity,
        uint flags,
        PluginInspection inspection,
        SkyrimSaveEncodingMode encoding,
        PriorOutputFacts prior)
    {
        static bool KindOrPath(
            PackageManifestFile item,
            string token) =>
            item.Kind.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            item.RelativePath.Value.Contains(token,
                StringComparison.OrdinalIgnoreCase);

        ImmutableArray<SkyrimSavePackageNpc> npcs = inspection.Records
            .Where(item => item.IsNpc)
            .Select(item => new SkyrimSavePackageNpc(
                item.FormId,
                TryEditorId(item.EditorId),
                item.OwnerPlugin == inspection.Plugin,
                item.OwnerPlugin))
            .OrderBy(item => item.FormId.Value)
            .ToImmutableArray();
        PluginRecordSummary[] selfOwned = inspection.Records
            .Where(item => item.OwnerPlugin == inspection.Plugin)
            .ToArray();
        int authored = inspection.Records.Count(item =>
            item.Signature is "ARMO" or "ARMA" or "OTFT" or "LVLI");
        int leveled = inspection.Records.Count(item =>
            item.Signature is "LVLN" or "LVLI");
        ImmutableArray<string> leveledNpcEditorIds = inspection.Records
            .Where(item => item.Signature == "LVLN" &&
                           !string.IsNullOrWhiteSpace(item.EditorId))
            .Select(item => item.EditorId!)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        uint highest = selfOwned
            .Select(item => item.FormId.Value)
            .DefaultIfEmpty(0U)
            .Max();
        int looseAssets = identity.Files.Count(item =>
            IsArchivable(item.RelativePath));

        return new SkyrimSavePackageSnapshot(
            new PluginName(identity.OutputPlugin),
            identity.TargetFormId,
            npcs.Length,
            (flags & MasterFlag) != 0,
            (flags & LightFlag) != 0,
            prior.FaceGeom ||
            identity.Files.Any(item => KindOrPath(item, "facegeom")),
            prior.FaceTint ||
            identity.Files.Any(item => KindOrPath(item, "facetint")),
            prior.BodySlide ||
            identity.Files.Any(item =>
                KindOrPath(item, "bsslider") ||
                item.RelativePath.Value.EndsWith(
                    ".bssliders",
                    StringComparison.OrdinalIgnoreCase)),
            prior.BodyGen ||
            identity.Files.Any(item => KindOrPath(item, "bodygen")),
            prior.ApplyScript ||
            identity.Files.Any(item =>
                KindOrPath(item, "apply-script") ||
                KindOrPath(item, "runtime-script") ||
                item.RelativePath.Value.EndsWith(
                    "/NPCM_Manolov_ApplySSE.pex",
                    StringComparison.OrdinalIgnoreCase)),
            identity.Files.Any(item => IsArchive(item.RelativePath)),
            authored,
            leveled,
            identity.Files,
            npcs,
            encoding,
            leveledNpcEditorIds,
            highest,
            looseAssets);
    }

    private static PriorOutputFacts ReadPriorOutputFacts(
        string sourceRoot,
        PackageManifestIdentity identity,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        PackageManifestFile? row = identity.Files.SingleOrDefault(item =>
            string.Equals(
                item.RelativePath.Value,
                ProposalName,
                StringComparison.OrdinalIgnoreCase));
        if (row is null) return new(false, false, false, false, false);
        try
        {
            string path = ResolveUnder(sourceRoot, row.RelativePath);
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(
                    "immutableOutputs",
                    out JsonElement outputs) ||
                outputs.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "The prior save proposal lacks immutableOutputs.");
            return new PriorOutputFacts(
                ReadBoolean(outputs, "faceGeom"),
                ReadBoolean(outputs, "faceTint"),
                ReadBoolean(outputs, "bodySlide"),
                ReadBoolean(outputs, "bodyGen"),
                ReadBoolean(outputs, "applyScript"));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException)
        {
            Error(
                diagnostics,
                "save-package-prior-proposal-invalid",
                exception.Message);
            return new(false, false, false, false, false);
        }
    }

    private static bool ReadBoolean(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(
                $"The prior save proposal field '{name}' is not Boolean.");
        return value.GetBoolean();
    }

    private static SkyrimSaveEncodingMode DetectEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int utf8Signals = 0;
        for (int index = 0; index + 1 < bytes.Length; index++)
        {
            byte first = bytes[index];
            if (first is >= 0xC2 and <= 0xDF &&
                bytes[index + 1] is >= 0x80 and <= 0xBF)
            {
                utf8Signals++;
                index++;
            }
        }
        return utf8Signals > 0
            ? SkyrimSaveEncodingMode.Utf8
            : SkyrimSaveEncodingMode.Windows1252;
    }

    private static bool RequiresPluginTransform(
        SkyrimSavePackageReviewArtifact review) =>
        review.Options.MarkAsMaster != review.Snapshot.MarkAsMaster ||
        review.Options.LightMaster != review.Snapshot.LightMaster ||
        review.Options.EncodingMode !=
            SkyrimSaveEncodingMode.PreservePackageBytes ||
        review.Options.LeveledListMode !=
            SkyrimSaveLeveledListMode.PreservePackageRecords;

    private static PackageManifestFile[] PluginRows(
        PackageManifestIdentity identity) =>
        identity.Files
            .Where(item =>
                string.Equals(
                    item.Kind,
                    "plugin",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item.RelativePath.Value,
                    identity.OutputPlugin,
                    StringComparison.OrdinalIgnoreCase))
            .DistinctBy(
                item => item.RelativePath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static SkyrimSavePackageChoiceRequest ToChoices(
        SkyrimSavePackageOptions options) =>
        new(
            options.Scope,
            options.TargetMode,
            options.MarkAsMaster,
            options.LightMaster,
            options.EncodingMode,
            options.ArchiveMode,
            options.LeveledListMode,
            options.LeveledListEditorId,
            options.NoDuplicateLeveledEntries);

    private static bool BindingMatches(
        SkyrimSavePackageReviewArtifact expected,
        SkyrimSavePackageReviewArtifact actual) =>
        expected.SourceRoot == actual.SourceRoot &&
        expected.OutputRoot == actual.OutputRoot &&
        expected.ManifestPath == actual.ManifestPath &&
        expected.ManifestSha256 == actual.ManifestSha256 &&
        expected.Options == actual.Options &&
        SnapshotMatches(expected.Snapshot, actual.Snapshot);

    private static bool SnapshotMatches(
        SkyrimSavePackageSnapshot expected,
        SkyrimSavePackageSnapshot actual) =>
        expected with
        {
            Files = [],
            Npcs = [],
            LeveledNpcEditorIds = []
        } == actual with
        {
            Files = [],
            Npcs = [],
            LeveledNpcEditorIds = []
        } &&
        expected.Files.Length == actual.Files.Length &&
        expected.Files.Zip(actual.Files).All(pair =>
            pair.First == pair.Second) &&
        expected.Npcs.SequenceEqual(actual.Npcs) &&
        expected.LeveledNpcEditorIds.SequenceEqual(
            actual.LeveledNpcEditorIds,
            StringComparer.OrdinalIgnoreCase);

    private static bool VerificationMatches(
        PackageVerificationArtifact? expected,
        PackageVerificationArtifact? actual)
    {
        if (expected is null || actual is null ||
            expected.ManifestSha256 != actual.ManifestSha256 ||
            expected.Files.Length != actual.Files.Length)
            return false;
        var actualByPath = actual.Files.ToDictionary(
            item => item.RelativePath.Value,
            StringComparer.OrdinalIgnoreCase);
        return expected.Files.All(item =>
            actualByPath.TryGetValue(
                item.RelativePath.Value,
                out PackageFileVerification? other) &&
            item.ActualByteLength == other.ActualByteLength &&
            item.ActualSha256 == other.ActualSha256 &&
            other.Matches);
    }

    private static ImmutableArray<SkyrimSavePackageFileComparison>
        CompareProducedFiles(
            PackageVerificationArtifact? source,
            PackageVerificationArtifact output)
    {
        var sourceByPath = (source?.Files ??
                            ImmutableArray<PackageFileVerification>.Empty)
            .ToDictionary(
                item => item.RelativePath.Value,
                StringComparer.OrdinalIgnoreCase);
        return output.Files
            .OrderBy(item => item.RelativePath.Value,
                StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                sourceByPath.TryGetValue(
                    item.RelativePath.Value,
                    out PackageFileVerification? old);
                return new SkyrimSavePackageFileComparison(
                    item.RelativePath,
                    old?.ActualByteLength ?? 0,
                    item.ActualByteLength,
                    old?.ActualSha256 ??
                    new Sha256Hash(new string('0', 64)),
                    item.ActualSha256,
                    item.Matches);
            })
            .ToImmutableArray();
    }

    private static async ValueTask CopyArtifactAsync(
        string sourceRoot,
        string outputRoot,
        PackageManifestFile row,
        CancellationToken cancellationToken)
    {
        string source = ResolveUnder(sourceRoot, row.RelativePath);
        string output = ResolveUnder(outputRoot, row.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Sha256Hash copied = await CopyAndHashAsync(
            source,
            output,
            cancellationToken).ConfigureAwait(false);
        if (copied != row.Sha256 ||
            new FileInfo(output).Length != row.ByteLength)
            throw new InvalidDataException(
                $"Source artifact '{row.RelativePath.Value}' changed while copied.");
    }

    private static async ValueTask CopyExactMemberAsync(
        string sourceRoot,
        string outputRoot,
        SkyrimBsaMemberArtifact member,
        CancellationToken cancellationToken)
    {
        string source = ResolveUnder(sourceRoot, member.Path);
        string output = ResolveUnder(outputRoot, member.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Sha256Hash copied = await CopyAndHashAsync(
            source,
            output,
            cancellationToken).ConfigureAwait(false);
        if (copied != member.Sha256 ||
            new FileInfo(output).Length != member.ByteLength)
            throw new InvalidDataException(
                $"Archive member '{member.Path.Value}' changed while copied.");
    }

    private static async ValueTask<Sha256Hash> CopyAndHashAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(
                   buffer,
                   cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false)));
    }

    private static uint ReadTes4Flags(string pluginPath)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(
            pluginPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.SequentialScan);
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException(
                "The declared output plugin does not begin with a TES4 record.");
        return BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    }

    private static EditorId? TryEditorId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return new EditorId(value); }
        catch (ArgumentException) { return null; }
    }

    private static string KindFor(
        AssetPath path,
        string outputPlugin,
        IReadOnlyDictionary<string, string> sourceKinds)
    {
        if (string.Equals(
                path.Value,
                outputPlugin,
                StringComparison.OrdinalIgnoreCase))
            return "plugin";
        if (string.Equals(
                path.Value,
                ProposalName,
                StringComparison.OrdinalIgnoreCase))
            return "save-proposal";
        if (path.Value.EndsWith(
                ".bsa",
                StringComparison.OrdinalIgnoreCase))
            return "archive";
        return sourceKinds.GetValueOrDefault(path.Value) ??
               (IsArchivable(path) ? "asset" : "package-output");
    }

    private static bool IsArchive(AssetPath path) =>
        path.Value.EndsWith(
            ".bsa",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsArchivable(AssetPath path) =>
        !path.Value.EndsWith(
            ".ini",
            StringComparison.OrdinalIgnoreCase) &&
        !path.Value.EndsWith(
            ".pex",
            StringComparison.OrdinalIgnoreCase) &&
        !path.Value.EndsWith(
            ".bssliders",
            StringComparison.OrdinalIgnoreCase) &&
        !path.Value.EndsWith(
            ".json",
            StringComparison.OrdinalIgnoreCase) &&
        (path.Value.StartsWith(
             "meshes/",
             StringComparison.OrdinalIgnoreCase) ||
         path.Value.StartsWith(
             "textures/",
             StringComparison.OrdinalIgnoreCase));

    private static string ResolveUnder(
        string root,
        AssetPath relative)
    {
        string full = Path.GetFullPath(Path.Combine(
            root,
            relative.Value.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        string relation = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relation) ||
            relation.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relation == "..")
            throw new InvalidDataException(
                $"Path '{relative.Value}' escaped its package root.");
        return full;
    }

    private static AssetPath RelativeFromRoot(
        string temporaryRoot,
        string path) =>
        new(Path.GetRelativePath(temporaryRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/'));

    private static bool HasReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private void TryRemoveCreatedDestination(
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (outputRoot.IsUnder(labRoot) &&
                Directory.Exists(outputRoot.Value))
                Directory.Delete(outputRoot.Value, recursive: true);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            Error(diagnostics, "save-package-cleanup-failed",
                exception.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) &&
                Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static SkyrimSavePackageReviewResult RefusedReview(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static SkyrimSavePackageExecutionResult RefusedExecution(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        PackageVerifyResult? verification) =>
        new(false, null, verification, diagnostics.ToImmutable());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static void Error(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string code,
        string message) =>
        diagnostics.Add(new Diagnostic(
            code,
            DiagnosticSeverity.Error,
            message));

    private sealed record LayoutResult(
        string TemporaryPackageRoot,
        SkyrimBsaBuildArtifact? ArchiveBuild,
        ImmutableArray<SkyrimBsaMemberArtifact> ArchiveMembers);

    private sealed record PriorOutputFacts(
        bool FaceGeom,
        bool FaceTint,
        bool BodySlide,
        bool BodyGen,
        bool ApplyScript);
}
