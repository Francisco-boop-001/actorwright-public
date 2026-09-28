using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Desktop;
using NpcManager.Domain;
using NpcManager.TestInfrastructure;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static async Task
        TestFaceGeomHairRegionsWizardProductionTransaction()
    {
        WorkspacePath labRoot = ActorwrightWorkspace.ResolveRoot();
        SyntheticFaceGeomHairRegionsDocument fixture =
            SyntheticFaceGeomHairRegionsFixture.Load(labRoot.Value);
        string source = fixture.Path;
        string expectedSourceSha256 = fixture.Sha256;
        Assert(
            File.Exists(source) &&
            HashHairWizardFile(source) ==
                new Sha256Hash(
                    expectedSourceSha256),
            "The synthetic FaceGeom transaction fixture drifted.");

        WorkspacePath projectedRendererRoot =
            FaceGeomHairRegionsWizardDesktopComposition
                .BuildRendererRootPath(
                    labRoot,
                    Guid.Empty);
        var syntheticFaceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/SyntheticHairRegions.esp/00000800.dds");
        string blenderBoundFaceTint =
            FaceGeomHairRegionsWizardDesktopComposition
                .ProjectBlenderBoundTexturePath(
                    projectedRendererRoot,
                    syntheticFaceTint);
        string expectedRendererParent = Path.Combine(
            labRoot.Value,
            ".actorwright",
            "work");
        Assert(
            string.Equals(
                Path.GetDirectoryName(
                    projectedRendererRoot.Value),
                expectedRendererParent,
                StringComparison.OrdinalIgnoreCase) &&
            blenderBoundFaceTint.Length <
                FaceGeomHairRegionsWizardDesktopComposition
                    .BlenderInteropPathLimit,
            $"The desktop renderer would stage synthetic FaceTint beyond the Windows Blender path budget ({blenderBoundFaceTint.Length} characters): {blenderBoundFaceTint}");

        string root = Path.Combine(
            expectedRendererParent,
            $"desktop-hair-regions-transaction-{Guid.NewGuid():N}");
        string transactionRoot =
            Path.Combine(
                root,
                "transaction");
        string data =
            Path.Combine(
                root,
                "Data");
        Directory.CreateDirectory(
            transactionRoot);
        Directory.CreateDirectory(
            data);
        try
        {
            ReviewedGameIntake intake =
                await CreateHairWizardIntakeAsync(
                    labRoot,
                    root,
                    data);
            Sha256Hash rendererEnvironment =
                HashHairWizard('A');
            Sha256Hash rendererScript =
                HashHairWizard('D');
            Sha256Hash loadedTextureObservation =
                HashHairWizard('E');
            var previewSource =
                new InspectingHairWizardPreviewSourceService();
            var preview =
                new InspectingHairWizardPreviewService(
                    rendererScript,
                    previewSource.TextureFingerprint,
                    loadedTextureObservation);
            var transaction =
                new FaceGeomHairRegionsWizardTransaction(
                    labRoot,
                    preview,
                    previewSource,
                    new WorkspacePath(transactionRoot),
                    rendererEnvironment,
                    rendererScript);
            var output =
                new WorkspacePath(Path.Combine(
                    root,
                    "rogue-output.nif"));
            var manifest =
                new WorkspacePath(Path.Combine(
                    root,
                    "rogue-output.manifest.json"));

            byte[] sourceBefore =
                await File.ReadAllBytesAsync(
                    source);
            FaceGeomHairRegionsWizardAnalysisState analysis =
                await transaction.AnalyzeAsync(
                    new WorkspacePath(source),
                    output,
                    manifest,
                    CancellationToken.None);
            Assert(
                analysis.AnalysisDocument.Value.Source.Sha256 ==
                    new Sha256Hash(
                        expectedSourceSha256) &&
                analysis.AnalysisDocument.Utf8Json
                    .IsDefaultOrEmpty == false &&
                analysis.AssignmentTemplate.Assignments.All(
                    item =>
                        item.Role ==
                        FaceGeomHairRegionRole.Preserve),
                "Production desktop analysis did not bind canonical JSON and a Preserve-only assignment template.");
            var admittedSource =
                new FaceGeomHairRegionsSelectedSource(
                    analysis.AnalysisDocument.Value.Source,
                    new AssetPath(
                        "meshes/actors/character/FaceGenData/FaceGeom/Test.esp/00000800.nif"),
                    AssetProviderKind.Loose,
                    "Test loose provider",
                    MaterializedFromArchive: false,
                    PluginColorContext: null);
            transaction.AdmitSelectedSource(
                admittedSource,
                []);
            Assert(
                transaction.AdmittedSelectedSourceCount == 1,
                "The production transaction did not retain one selected-source admission for wizard lifetime.");
            transaction.RevokeSelectedSource(
                admittedSource.Source.Path);
            Assert(
                transaction.AdmittedSelectedSourceCount == 0,
                "The production transaction retained stale selected-source metadata after wizard lifetime.");

            IGrouping<string, FaceGeomHairRegionsRegion>[] groups =
                analysis.AnalysisDocument.Value.Regions
                    .GroupBy(item =>
                        item.SharedShaderGroupId)
                    .OrderBy(item =>
                        item.Min(region =>
                            region.TintByteOffset))
                    .ToArray();
            Assert(
                groups.Length >= 2,
                "The synthetic fixture lacks two physical HairTint groups.");
            string[] primaryGroups =
                analysis.AnalysisDocument.Value.Regions
                    .Where(region =>
                        string.Equals(
                            region.Name,
                            "SyntheticMainHair",
                            StringComparison.Ordinal) ||
                        region.Name.Contains(
                            "HAIRLINE",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(region =>
                        region.SharedShaderGroupId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            string accent =
                analysis.AnalysisDocument.Value.Regions
                    .Single(region =>
                        string.Equals(
                            region.Name,
                            "SyntheticHairHighlight",
                            StringComparison.Ordinal))
                    .SharedShaderGroupId;
            Assert(
                primaryGroups.Length == 2 &&
                analysis.AnalysisDocument.Value.Regions
                    .Where(region =>
                        region.Name.Contains(
                            "lash",
                            StringComparison.OrdinalIgnoreCase) ||
                        region.Name.Contains(
                            "brow",
                            StringComparison.OrdinalIgnoreCase))
                    .All(region =>
                        region.DefaultRole ==
                        FaceGeomHairRegionRole.Preserve),
                "The synthetic fixture no longer exposes main hair plus hairline separately from highlight, brows, and lashes.");
            FaceGeomHairRegionsRequest request =
                analysis.AssignmentTemplate with
                {
                    PrimaryColor = "#191919",
                    AccentColor = "#F5F5F5",
                    Assignments =
                        analysis.AnalysisDocument.Value.Regions
                            .Select(region =>
                                new FaceGeomHairRegionAssignment(
                                    region.StructuralId,
                                    primaryGroups.Contains(
                                        region.SharedShaderGroupId,
                                        StringComparer.Ordinal)
                                        ? FaceGeomHairRegionRole
                                            .Primary
                                        : region.SharedShaderGroupId ==
                                            accent
                                            ? FaceGeomHairRegionRole
                                                .Accent
                                            : FaceGeomHairRegionRole
                                                .Preserve))
                            .ToImmutableArray()
                };
            FaceGeomHairRegionsWizardProposalState proposal =
                await transaction.ProposeAsync(
                    analysis.AnalysisDocument,
                    request,
                    CancellationToken.None);
            Assert(
                !proposal.RequestDocument.Utf8Json
                    .IsDefaultOrEmpty &&
                !proposal.ProposalDocument.Utf8Json
                    .IsDefaultOrEmpty &&
                proposal.Materialization.Candidate ==
                    proposal.ProposalDocument.Value
                        .ExpectedOutput &&
                proposal.Materialization
                    .ChangedByteOffsets
                    .SequenceEqual(
                        proposal.ProposalDocument.Value
                            .PredictedChangedByteOffsets),
                "Production desktop proposal did not close the canonical request/proposal/materialization chain.");

            var previewRoot =
                new WorkspacePath(Path.Combine(
                    root,
                    "preview"));
            FaceGeomHairRegionsPreviewResult rendered =
                await transaction.PreviewAsync(
                    proposal,
                    intake,
                    previewRoot,
                    CancellationToken.None);
            Assert(
                rendered.Succeeded &&
                preview.Calls == 1 &&
                preview.ObservedAuthority is not null &&
                preview.ObservedAuthority.Document.Sha256 ==
                    preview.ObservedDocumentHash &&
                !File.Exists(
                    preview.ObservedAuthority.Document.Path.Value) &&
                !Directory.EnumerateFiles(
                        transactionRoot,
                        "*reviewed-intake*.json",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                "Preview did not create, reload, pass, and then remove one exact reviewed-intake document authority.");

            FaceGeomHairRegionsPreviewCacheAuthority
                cacheAuthority =
                    await transaction
                        .ResolvePreviewCacheAuthorityAsync(
                            proposal,
                            intake,
                            CancellationToken.None);
            Assert(
                previewSource.Calls == 1 &&
                cacheAuthority.Key.SourceSha256 ==
                    proposal.Materialization.Candidate
                        .Sha256 &&
                cacheAuthority.Key.RequestSha256 ==
                    proposal.RequestDocument.Sha256 &&
                cacheAuthority.Key.ProposalSha256 ==
                    proposal.ProposalDocument.Sha256 &&
                cacheAuthority.Key.RendererAuthoritySha256 ==
                    rendererEnvironment &&
                cacheAuthority.Key.RendererScriptSha256 ==
                    rendererScript &&
                cacheAuthority.Key.TextureCatalogSha256 ==
                    intake.AssetIndexFingerprint &&
                cacheAuthority.Key
                    .ResolvedTextureAuthoritySha256 ==
                    previewSource.TextureFingerprint &&
                !Directory.EnumerateDirectories(
                        transactionRoot,
                        "cache-source-*",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                "The pre-render cache authority did not bind exact source/request/proposal/intake/renderer/provider bytes or clean its staging root.");

            var viewModelOutput =
                new WorkspacePath(Path.Combine(
                    root,
                    "rogue-viewmodel-output.nif"));
            var viewModelManifest =
                new WorkspacePath(Path.Combine(
                    root,
                    "rogue-viewmodel-output.manifest.json"));
            using (var viewModel =
                   new FaceGeomHairRegionsWizardViewModel(
                       transaction,
                       cache: null,
                       labRoot,
                       intake,
                       FaceGeomHairRegionsWizardLaunchContext
                           .Standalone)
                   {
                       SourcePath = source,
                       OutputPath = viewModelOutput.Value,
                       ManifestPath = viewModelManifest.Value
                   })
            {
                await viewModel.AnalyzeAsync();
                IGrouping<string,
                    FaceGeomHairRegionCardViewModel>[] viewModelGroups =
                    viewModel.Regions
                        .GroupBy(item =>
                            item.SharedShaderGroupId)
                        .OrderBy(item =>
                            item.Min(region =>
                                region.Region.TintByteOffset))
                        .ToArray();
                Assert(
                    viewModelGroups.Length >= 2,
                    "The real wizard ViewModel did not expose two physical HairTint groups.");
                string[] viewModelPrimary =
                    viewModel.Regions
                        .Where(region =>
                            string.Equals(
                                region.Name,
                                "SyntheticMainHair",
                                StringComparison.Ordinal) ||
                            region.Name.Contains(
                                "HAIRLINE",
                                StringComparison.OrdinalIgnoreCase))
                        .Select(region =>
                            region.SharedShaderGroupId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                string viewModelAccent =
                    viewModel.Regions
                        .Single(region =>
                            string.Equals(
                                region.Name,
                                "SyntheticHairHighlight",
                                StringComparison.Ordinal))
                        .SharedShaderGroupId;
                foreach (FaceGeomHairRegionCardViewModel
                         region in viewModel.Regions)
                {
                    region.Role =
                        viewModelPrimary.Contains(
                            region.SharedShaderGroupId,
                            StringComparer.Ordinal)
                            ? FaceGeomHairRegionRole.Primary
                            : region.SharedShaderGroupId ==
                              viewModelAccent
                                ? FaceGeomHairRegionRole.Accent
                                : FaceGeomHairRegionRole.Preserve;
                }
                viewModel.PrimaryColor = "#191919";
                viewModel.AccentColor = "#F5F5F5";
                Assert(
                    viewModel.MoveNext(),
                    "The real wizard ViewModel could not enter Colors and Preview.");
                await viewModel.ProposeAsync();
                await viewModel.RenderPreviewAsync();
                Assert(
                    viewModel.IsPreviewCurrent &&
                    preview.Calls == 2 &&
                    previewSource.Calls == 2 &&
                    !viewModel.Diagnostics.Any(item =>
                        item.Code ==
                        "hair-regions-preview-authority-mismatch"),
                    "The real wizard ViewModel refused a non-no-op proposal whose candidate, renderer script, texture source, and loaded-texture observation authorities were distinct.");
            }

            FaceGeomHairRegionsApplyResult applied =
                await transaction.ApplyAsync(
                    proposal,
                    CancellationToken.None);
            Assert(
                applied.Succeeded &&
                applied.ManifestDocument is not null &&
                applied.Verification?.Succeeded == true &&
                applied.SurvivingArtifacts.IsEmpty &&
                File.Exists(output.Value) &&
                File.Exists(manifest.Value),
                "Production desktop Apply did not finish with the independent Bethesda verifier.");
            Assert(
                (await File.ReadAllBytesAsync(source))
                    .SequenceEqual(sourceBefore),
                "Production desktop transaction modified its FaceGeom source.");

            string selectedLeaseRoot = Path.Combine(
                root,
                "selected-source-lease");
            var ownedSelectedRoot =
                new NpcManager.Infrastructure
                    .FaceGeomHairRegionsOwnedDirectoryLease(
                        labRoot,
                        new WorkspacePath(
                            selectedLeaseRoot),
                        "selected-source lease regression");
            int selectedLeaseReleases = 0;
            var selectedLease =
                new FaceGeomHairRegionsSelectedSourceLease(
                    new FaceGeomHairRegionsSelectedSourceResult(
                        false,
                        null,
                        []),
                    retainsArchiveStaging: true,
                    () =>
                    {
                        selectedLeaseReleases++;
                        ownedSelectedRoot.Dispose();
                    });
            Assert(
                Directory.Exists(
                    selectedLeaseRoot) &&
                FaceGeomHairRegionsWizardDesktopComposition
                    .MaximumRetainedSelectedSources == 1,
                "The desktop composition did not expose one bounded selected-source lease.");
            selectedLease.Dispose();
            selectedLease.Dispose();
            Assert(
                selectedLeaseReleases == 1 &&
                !Directory.Exists(
                    selectedLeaseRoot),
                "An archive-backed selected-source lease was not released exactly once at wizard lifetime end.");

            byte[] outputBefore =
                await File.ReadAllBytesAsync(
                    output.Value);
            FaceGeomHairRegionsApplyResult collision =
                await transaction.ApplyAsync(
                    proposal,
                    CancellationToken.None);
            Assert(
                !collision.Succeeded &&
                (await File.ReadAllBytesAsync(output.Value))
                    .SequenceEqual(outputBefore),
                "Production desktop transaction overwrote an existing FaceGeom output.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static async ValueTask<ReviewedGameIntake>
        CreateHairWizardIntakeAsync(
            WorkspacePath labRoot,
            string root,
            string data)
    {
        const string pluginName =
            "HairWizardAuthority.esp";
        string plugin = Path.Combine(
            data,
            pluginName);
        byte[] pluginBytes =
            [0x54, 0x45, 0x53, 0x34, 0x01];
        await File.WriteAllBytesAsync(
            plugin,
            pluginBytes);
        string loadOrder = Path.Combine(
            root,
            "plugins.txt");
        await File.WriteAllTextAsync(
            loadOrder,
            pluginName);
        var entry = new PluginClosureReviewEntry(
            new PluginName(pluginName),
            0,
            true,
            true,
            true,
            false,
            true,
            new WorkspacePath(plugin),
            HashHairWizardBytes(pluginBytes),
            []);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(loadOrder),
            new WorkspacePath(Path.Combine(
                root,
                "unused-intake-output")),
            HashHairWizardFile(loadOrder),
            [entry],
            [],
            [],
            [],
            0,
            HashHairWizard('B'),
            new Sha256Hash(new string('0', 64)),
            RuntimeAuthority: false);
        return intake with
        {
            IntakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(intake)
        };
    }

    private sealed class InspectingHairWizardPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        private readonly Sha256Hash rendererScriptSha256;
        private readonly Sha256Hash textureSourceSha256;
        private readonly Sha256Hash
            loadedTextureObservationSha256;

        public InspectingHairWizardPreviewService(
            Sha256Hash rendererScriptSha256,
            Sha256Hash textureSourceSha256,
            Sha256Hash loadedTextureObservationSha256)
        {
            this.rendererScriptSha256 =
                rendererScriptSha256;
            this.textureSourceSha256 =
                textureSourceSha256;
            this.loadedTextureObservationSha256 =
                loadedTextureObservationSha256;
        }

        public int Calls { get; private set; }

        public ReviewedGameIntakeDocumentAuthority?
            ObservedAuthority { get; private set; }

        public Sha256Hash? ObservedDocumentHash
        {
            get;
            private set;
        }

        public async ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            ObservedAuthority =
                request.IntakeAuthority;
            Assert(
                File.Exists(
                    request.IntakeDocument.Path.Value),
                "The preview service did not receive a persisted reviewed-intake authority.");
            await using var persisted = new FileStream(
                request.IntakeDocument.Path.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var copied =
                new MemoryStream();
            await persisted.CopyToAsync(
                copied,
                cancellationToken);
            byte[] exact =
                copied.ToArray();
            ObservedDocumentHash =
                HashHairWizardBytes(exact);
            Assert(
                exact.AsSpan().SequenceEqual(
                    request.IntakeDocument.Utf8Json.AsSpan()) &&
                exact.LongLength ==
                    request.IntakeDocument.ByteLength &&
                ObservedDocumentHash ==
                    request.IntakeDocument.Sha256,
                "The reviewed-intake file did not equal its exact canonical document authority.");
            Directory.CreateDirectory(
                request.OutputRoot.Value);
            string image = Path.Combine(
                request.OutputRoot.Value,
                "combined-face.png");
            byte[] imageBytes =
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A];
            await File.WriteAllBytesAsync(
                image,
                imageBytes,
                cancellationToken);
            var renderAuthority =
                new FaceGeomHairRegionsRenderAuthority(
                    HashHairWizard('1'),
                    HashHairWizard('2'),
                    HashHairWizard('3'),
                    rendererScriptSha256,
                    HashHairWizard('4'),
                    textureSourceSha256,
                    loadedTextureObservationSha256,
                    HashHairWizard('5'),
                    1,
                    HashHairWizard('6'),
                    1,
                    [],
                    []);
            return new FaceGeomHairRegionsPreviewResult(
                true,
                new FaceGeomHairRegionsPreviewEvidence(
                    request.Materialization.Candidate.Sha256,
                    request.ProposalDocument.Sha256,
                    request.IntakeDocument.Sha256,
                    rendererScriptSha256,
                    textureSourceSha256,
                    HashHairWizard('7'),
                    1,
                    478,
                    31,
                    renderAuthority),
                [
                    new FaceGeomHairRegionsPreviewArtifact(
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace,
                        null,
                        new WorkspacePath(image),
                        imageBytes.LongLength,
                        HashHairWizardBytes(imageBytes),
                        1)
                ],
                [],
                VisualAuthority: false,
                RuntimeAuthority: false);
        }
    }

    private sealed class
        InspectingHairWizardPreviewSourceService :
        IFaceGeomHairRegionsPreviewSourceService
    {
        public Sha256Hash TextureFingerprint { get; } =
            HashHairWizard('C');

        public int Calls { get; private set; }

        public ValueTask<FaceGeomHairRegionsPreviewSourceResult>
            ComposeAsync(
                FaceGeomHairRegionsPreviewSourceRequest request,
                CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Assert(
                Directory.Exists(
                    request.OutputDataRoot.Value) &&
                request.CandidateBytes.Length ==
                    request.Candidate.ByteLength &&
                HashHairWizardBytes(
                    request.CandidateBytes.AsSpan()) ==
                    request.Candidate.Sha256,
                "Cache preflight did not bind the exact candidate or owned staging root.");
            return ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewSourceResult(
                    true,
                    new FaceGeomHairRegionsPreviewSource(
                        new NpcVisualAsset(
                            NpcVisualAssetRole.FaceGeom,
                            new AssetPath(
                                "meshes/npcmanager/hair-regions/candidate.nif"),
                            "exact-proposal",
                            request.Candidate.Sha256,
                            request.Candidate.ByteLength,
                            request.Candidate.Path,
                            false,
                            []),
                        [],
                        TextureFingerprint),
                    []));
        }
    }

    private static Sha256Hash HashHairWizard(
        char seed) =>
        new(new string(seed, 64));

    private static Sha256Hash HashHairWizardFile(
        string path) =>
        HashHairWizardBytes(
            File.ReadAllBytes(path));

    private static Sha256Hash HashHairWizardBytes(
        ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));
}
