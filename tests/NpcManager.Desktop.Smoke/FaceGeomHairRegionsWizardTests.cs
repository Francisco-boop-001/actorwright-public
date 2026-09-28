using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.TestInfrastructure;

namespace NpcManager.Desktop.Smoke;

internal static class FaceGeomHairRegionsWizardTests
{
    private static string WorkspaceRoot =>
        ActorwrightWorkspace.ResolveRoot().Value;

    private static string WorkRoot =>
        ActorwrightWorkspace.WorkRoot(
            new WorkspacePath(WorkspaceRoot)).Value;

    private static string SyntheticSourcePath =>
        SyntheticFaceGeomHairRegionsFixture.Load(WorkspaceRoot).Path;

    private static readonly string[] ExpectedStepTitles =
    [
        "Source",
        "Regions",
        "Colors and Preview",
        "Byte-plan Review",
        "Write and Verify"
    ];

    public static void Run()
    {
        RunInitialStateContract();
        RunCacheKeyAuthorityContract();
        RunStateInvalidationContractAsync().GetAwaiter().GetResult();
        RunOperationCancellationAndNewestResultContractAsync()
            .GetAwaiter()
            .GetResult();
        RunCallerCancellationContractAsync()
            .GetAwaiter()
            .GetResult();
        RunContentAddressedCacheContractAsync()
            .GetAwaiter()
            .GetResult();
        RunApplyAndVerifyGateContractAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static void RunInitialStateContract()
    {
        var source = new WorkspacePath(
            SyntheticSourcePath);
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName("SyntheticHairRegions.esp"),
            new PluginName("SyntheticHairRegions.esp"),
            new FormId(0x00000800),
            "NPC_");
        var launch = new FaceGeomHairRegionsWizardLaunchContext(
            identity,
            source,
            "Selected NPC SyntheticHairRegions.esp|00000800");

        using var viewModel = new FaceGeomHairRegionsWizardViewModel(
            new NeverCalledHairRegionsWizardTransaction(),
            null,
            new WorkspacePath(WorkspaceRoot),
            null,
            launch);

        Assert(
            viewModel.Steps.Select(step => step.Title).SequenceEqual(
                ExpectedStepTitles,
                StringComparer.Ordinal) &&
            viewModel.CurrentStepIndex == 0 &&
            viewModel.CurrentStep.Title == "Source",
            "Dual-tone wizard did not expose the five ordered production steps.");
        Assert(
            viewModel.SourcePath == source.Value &&
            viewModel.SelectedNpcSummary == launch.DisplayName &&
            !viewModel.IsStandaloneSource,
            "Selected-NPC launch did not retain its exact suggested FaceGeom authority.");
        Assert(
            viewModel.AuthorityLabel ==
            "Off-engine HairTint preview — Skyrim runtime remains authoritative" &&
            !viewModel.CanWrite,
            "Wizard startup either hid the runtime-authority boundary or enabled writing.");

        using var standalone = new FaceGeomHairRegionsWizardViewModel(
            new NeverCalledHairRegionsWizardTransaction(),
            null,
            new WorkspacePath(WorkspaceRoot),
            null,
            FaceGeomHairRegionsWizardLaunchContext.Standalone);
        Assert(
            standalone.IsStandaloneSource &&
            string.IsNullOrEmpty(standalone.SourcePath),
            "Standalone launch guessed a source instead of waiting for K-local selection.");
    }

    private static void RunCacheKeyAuthorityContract()
    {
        FaceGeomHairRegionsPreviewCacheKey baseline =
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'),
                Hash('2'),
                Hash('3'),
                Hash('4'),
                Hash('5'),
                Hash('6'),
                Hash('7'),
                Hash('8'));
        string[] mutations =
        [
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('9'), Hash('2'), Hash('3'), Hash('4'),
                Hash('5'), Hash('6'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('9'), Hash('3'), Hash('4'),
                Hash('5'), Hash('6'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('9'), Hash('4'),
                Hash('5'), Hash('6'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('3'), Hash('9'),
                Hash('5'), Hash('6'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('3'), Hash('4'),
                Hash('9'), Hash('6'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('3'), Hash('4'),
                Hash('5'), Hash('9'), Hash('7'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('3'), Hash('4'),
                Hash('5'), Hash('6'), Hash('9'), Hash('8')).Fingerprint.Value,
            FaceGeomHairRegionsPreviewCacheKey.Create(
                Hash('1'), Hash('2'), Hash('3'), Hash('4'),
                Hash('5'), Hash('6'), Hash('7'), Hash('9')).Fingerprint.Value
        ];

        Assert(
            mutations.All(value =>
                !string.Equals(
                    value,
                    baseline.Fingerprint.Value,
                    StringComparison.Ordinal)) &&
            mutations.Distinct(StringComparer.Ordinal).Count() ==
            mutations.Length,
            "Preview cache identity omitted a source, request, proposal, intake, renderer-environment, renderer-script, texture-catalog, or resolved-texture authority.");
    }

    private static async Task RunStateInvalidationContractAsync()
    {
        var transaction = new RecordingHairRegionsWizardTransaction();
        using var viewModel = new FaceGeomHairRegionsWizardViewModel(
            transaction,
            null,
            new WorkspacePath(WorkspaceRoot),
            ReviewedIntake(),
            FaceGeomHairRegionsWizardLaunchContext.Standalone)
        {
            SourcePath =
                SyntheticSourcePath,
            OutputPath =
                Path.Combine(WorkRoot, "wizard-state", "dual-tone.nif"),
            ManifestPath =
                Path.Combine(WorkRoot, "wizard-state", "dual-tone.json")
        };

        await viewModel.AnalyzeAsync();
        Assert(
            viewModel.CurrentStepIndex == 1 &&
            viewModel.Regions.Count == 3 &&
            transaction.AnalyzeCalls == 1 &&
            transaction.PreviewCalls == 0,
            "Analysis did not populate the Regions step without starting preview.");

        FaceGeomHairRegionCardViewModel primary =
            viewModel.Regions.Single(item =>
                item.StructuralId == "shape-10");
        FaceGeomHairRegionCardViewModel linked =
            viewModel.Regions.Single(item =>
                item.StructuralId == "shape-11");
        FaceGeomHairRegionCardViewModel accent =
            viewModel.Regions.Single(item =>
                item.StructuralId == "shape-20");
        primary.Role = FaceGeomHairRegionRole.Primary;
        Assert(
            linked.Role == FaceGeomHairRegionRole.Primary,
            "Changing one shared-shader shape did not link the role of its physical tint peers.");
        accent.Role = FaceGeomHairRegionRole.Accent;
        viewModel.PrimaryColor = "#201814";
        viewModel.AccentColor = "#F4F1E8";
        Assert(
            transaction.PreviewCalls == 0 &&
            !viewModel.HasCurrentProposal &&
            !viewModel.IsPreviewCurrent,
            "Role or color editing launched preview or retained stale proposal state.");

        Assert(
            viewModel.MoveNext() &&
            viewModel.CurrentStepIndex == 2,
            "Regions could not advance to Colors and Preview.");
        await viewModel.ProposeAsync();
        Assert(
            viewModel.HasCurrentProposal &&
            transaction.ProposeCalls == 1 &&
            transaction.PreviewCalls == 0,
            "Proposal generation either failed to bind the current revision or started preview automatically.");
        await viewModel.RenderPreviewAsync();
        Assert(
            viewModel.IsPreviewCurrent &&
            transaction.PreviewCalls == 1 &&
            !viewModel.CanWrite &&
            !viewModel.IsProposalAccepted,
            "A current successful preview bypassed explicit proposal acceptance.");
        Assert(
            viewModel.MoveNext() &&
            viewModel.CurrentStepIndex == 3 &&
            viewModel.TryAcceptProposal() &&
            !viewModel.CanWrite &&
            viewModel.MoveNext() &&
            viewModel.CurrentStepIndex == 4 &&
            viewModel.CanWrite,
            "Write became available without both a current preview and explicit accepted byte plan.");

        viewModel.PrimaryColor = "#302018";
        Assert(
            transaction.PreviewCalls == 1 &&
            !viewModel.IsPreviewCurrent &&
            !viewModel.HasCurrentProposal &&
            !viewModel.IsProposalAccepted &&
            !viewModel.CanWrite,
            "A color edit retained current preview, proposal acceptance, or write authority.");

        await viewModel.ProposeAsync();
        await viewModel.RenderPreviewAsync();
        Assert(
            viewModel.MoveBack() &&
            viewModel.CurrentStepIndex == 3 &&
            !viewModel.IsPreviewCurrent &&
            !viewModel.HasCurrentProposal &&
            !viewModel.IsProposalAccepted &&
            !viewModel.CanWrite,
            "Back navigation retained downstream preview or write authority.");
    }

    private static async Task
        RunOperationCancellationAndNewestResultContractAsync()
    {
        WorkspacePath realDiscardRoot = new(
            Path.Combine(
                Path.Combine(WorkRoot, "desktop-hair-regions-preview"),
                $"cleanup-proof-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(
            realDiscardRoot.Value);
        File.WriteAllBytes(
            Path.Combine(
                realDiscardRoot.Value,
                "partial-render.png"),
            [1, 2, 3]);
        try
        {
            ImmutableArray<Diagnostic> cleanupDiagnostics =
                new FileSystemFaceGeomHairRegionsPreviewOutputCleanup(
                        new WorkspacePath(
                            WorkspaceRoot))
                    .Cleanup(realDiscardRoot);
            Assert(
                cleanupDiagnostics.IsEmpty &&
                !Directory.Exists(realDiscardRoot.Value),
                "The default cleanup did not remove the exact discarded preview tree.");
        }
        finally
        {
            if (Directory.Exists(realDiscardRoot.Value))
            {
                Directory.Delete(
                    realDiscardRoot.Value,
                    recursive: true);
            }
        }

        var transaction =
            new BlockingHairRegionsWizardTransaction();
        var cleanup =
            new RecordingPreviewOutputCleanup();
        using var viewModel = NewWizard(
            transaction,
            cache: null,
            cleanup: cleanup);

        Task firstAnalyze =
            viewModel.AnalyzeAsync().AsTask();
        Assert(
            transaction.AnalyzeOperations.Count == 1,
            "The first controlled analysis did not start.");
        Task secondAnalyze =
            viewModel.AnalyzeAsync().AsTask();
        Assert(
            transaction.AnalyzeOperations.Count == 2 &&
            transaction.AnalyzeOperations[0]
                .CancellationToken.IsCancellationRequested,
            "A newer analysis did not cancel the older analysis token.");
        transaction.CompleteAnalyze(1, 'B');
        await secondAnalyze;
        transaction.CompleteAnalyze(0, 'A');
        await firstAnalyze;
        Assert(
            viewModel.HasCurrentAnalysis &&
            viewModel.Regions.Count == 3,
            "A stale analysis displaced the newest analysis state.");

        ConfigurePrimaryAndAccent(viewModel);
        Assert(
            viewModel.MoveNext() &&
            viewModel.CurrentStepIndex == 2,
            "The controlled wizard did not reach proposal state.");
        Task firstProposal =
            viewModel.ProposeAsync().AsTask();
        Task secondProposal =
            viewModel.ProposeAsync().AsTask();
        Assert(
            transaction.ProposeOperations.Count == 2 &&
            transaction.ProposeOperations[0]
                .CancellationToken.IsCancellationRequested,
            "A newer proposal did not cancel the older proposal token.");
        transaction.CompleteProposal(1, 'D');
        await secondProposal;
        transaction.CompleteProposal(0, 'C');
        await firstProposal;
        Assert(
            viewModel.HasCurrentProposal,
            "A stale proposal displaced the newest byte plan.");

        Task firstPreview =
            viewModel.RenderPreviewAsync().AsTask();
        Assert(
            transaction.PreviewOperations.Count == 1 &&
            !viewModel.IsPreviewCurrent &&
            !viewModel.IsProposalAccepted,
            "Starting a preview retained a prior preview or acceptance.");
        Task secondPreview =
            viewModel.RenderPreviewAsync().AsTask();
        Assert(
            transaction.PreviewOperations.Count == 2 &&
            transaction.PreviewOperations[0]
                .CancellationToken.IsCancellationRequested,
            "A newer render did not cancel the older render token.");
        WorkspacePath firstOutputRoot =
            transaction.PreviewOperations[0].OutputRoot;
        transaction.CompletePreview(1, 'F');
        await secondPreview;
        Assert(
            viewModel.Preview?.Evidence?.RendererSha256 ==
                Hash('F') &&
            transaction.PreviewCalls == 2,
            "The newest controlled preview did not publish.");
        transaction.CompletePreview(0, 'E');
        await firstPreview;
        Assert(
            viewModel.Preview?.Evidence?.RendererSha256 ==
                Hash('F') &&
            cleanup.Cleaned.SequenceEqual(
                [firstOutputRoot]),
            "An older render displaced the newest render or its exact output root was not discarded.");

        Task editedPreview =
            viewModel.RenderPreviewAsync().AsTask();
        WorkspacePath editedOutputRoot =
            transaction.PreviewOperations[2].OutputRoot;
        Assert(
            cleanup.Cleaned.Count == 2 &&
            cleanup.Cleaned[1] ==
                transaction.PreviewOperations[1].OutputRoot,
            "Replacing a successful fresh preview did not release its exact owned output root.");
        cleanup.FailureMessage =
            "locked discarded preview";
        viewModel.PrimaryColor = "#312119";
        Assert(
            transaction.PreviewOperations[2]
                .CancellationToken.IsCancellationRequested,
            "Editing a preview authority did not cancel the active render.");
        transaction.CompletePreview(2, '1');
        await editedPreview;
        Assert(
            !viewModel.IsPreviewCurrent &&
            viewModel.SurvivingPreviewOutputs.Contains(
                editedOutputRoot) &&
            viewModel.Diagnostics.Any(item =>
                item.Code ==
                    "hair-regions-preview-discard-cleanup-failed" &&
                item.Message.Contains(
                    editedOutputRoot.Value,
                    StringComparison.Ordinal)),
            "Discard cleanup failure was hidden instead of surfacing its exact output root.");

        Task cancelledByBack =
            viewModel.ProposeAsync().AsTask();
        viewModel.MoveBack();
        Assert(
            transaction.ProposeOperations[^1]
                .CancellationToken.IsCancellationRequested,
            "Back navigation did not cancel the active proposal.");
        transaction.CompleteProposal(
            transaction.ProposeOperations.Count - 1,
            '2');
        await cancelledByBack;

        var disposeTransaction =
            new BlockingHairRegionsWizardTransaction();
        var disposedViewModel = NewWizard(
            disposeTransaction,
            cache: null,
            cleanup: null);
        Task disposedAnalyze =
            disposedViewModel.AnalyzeAsync().AsTask();
        disposedViewModel.Dispose();
        Assert(
            disposeTransaction.AnalyzeOperations[0]
                .CancellationToken.IsCancellationRequested,
            "Disposing the wizard did not cancel its active operation.");
        disposeTransaction.CompleteAnalyze(0, '3');
        await disposedAnalyze;

        var failedTransaction =
            new BlockingHairRegionsWizardTransaction();
        var failedCleanup =
            new RecordingPreviewOutputCleanup();
        using var failedViewModel = NewWizard(
            failedTransaction,
            cache: null,
            cleanup: failedCleanup);
        Task failedAnalyze =
            failedViewModel.AnalyzeAsync().AsTask();
        failedTransaction.CompleteAnalyze(0, 'A');
        await failedAnalyze;
        ConfigurePrimaryAndAccent(
            failedViewModel);
        Assert(
            failedViewModel.MoveNext(),
            "Failed-preview test could not reach colors.");
        Task failedProposal =
            failedViewModel.ProposeAsync().AsTask();
        failedTransaction.CompleteProposal(0, 'D');
        await failedProposal;
        Task failedPreview =
            failedViewModel.RenderPreviewAsync().AsTask();
        WorkspacePath failedRoot =
            failedTransaction.PreviewOperations[0].OutputRoot;
        failedTransaction.CompletePreview(
            0,
            'F',
            succeeded: false);
        await failedPreview;
        Assert(
            !failedViewModel.IsPreviewCurrent &&
            failedCleanup.Cleaned.SequenceEqual(
                [failedRoot]) &&
            failedViewModel.SurvivingPreviewOutputs.IsEmpty,
            "A failed preview retained its output root.");
    }

    private static async Task RunCallerCancellationContractAsync()
    {
        var transaction =
            new BlockingHairRegionsWizardTransaction();
        var cleanup =
            new RecordingPreviewOutputCleanup();
        using var viewModel = NewWizard(
            transaction,
            cache: null,
            cleanup: cleanup);

        using (var cancellation = new CancellationTokenSource())
        {
            Task operation =
                viewModel.AnalyzeAsync(
                        cancellation.Token)
                    .AsTask();
            cancellation.Cancel();
            transaction.CompleteAnalyze(0, '4');
            await operation;
            Assert(
                !viewModel.HasCurrentAnalysis,
                "Analyze published after caller cancellation when its backend returned normally.");
        }

        Task acceptedAnalyze =
            viewModel.AnalyzeAsync().AsTask();
        transaction.CompleteAnalyze(1, '5');
        await acceptedAnalyze;
        ConfigurePrimaryAndAccent(viewModel);
        Assert(
            viewModel.MoveNext(),
            "Caller-cancellation test could not reach proposal.");

        using (var cancellation = new CancellationTokenSource())
        {
            Task operation =
                viewModel.ProposeAsync(
                        cancellation.Token)
                    .AsTask();
            cancellation.Cancel();
            transaction.CompleteProposal(0, '6');
            await operation;
            Assert(
                !viewModel.HasCurrentProposal,
                "Propose published after caller cancellation when its backend returned normally.");
        }

        Task acceptedProposal =
            viewModel.ProposeAsync().AsTask();
        transaction.CompleteProposal(1, 'D');
        await acceptedProposal;

        using (var cancellation = new CancellationTokenSource())
        {
            Task operation =
                viewModel.RenderPreviewAsync(
                        cancellation.Token)
                    .AsTask();
            WorkspacePath cancelledRoot =
                transaction.PreviewOperations[0].OutputRoot;
            cancellation.Cancel();
            transaction.CompletePreview(0, 'F');
            await operation;
            Assert(
                !viewModel.IsPreviewCurrent &&
                cleanup.Cleaned.Contains(
                    cancelledRoot),
                "Preview published after caller cancellation or retained its owned output.");
        }

        Task acceptedPreview =
            viewModel.RenderPreviewAsync().AsTask();
        WorkspacePath acceptedPreviewRoot =
            transaction.PreviewOperations[1].OutputRoot;
        transaction.CompletePreview(1, 'F');
        await acceptedPreview;
        Assert(
            viewModel.MoveNext() &&
            viewModel.TryAcceptProposal() &&
            viewModel.MoveNext(),
            "Caller-cancellation test could not reach Apply.");

        using (var cancellation = new CancellationTokenSource())
        {
            Task operation =
                viewModel.ApplyAndVerifyAsync(
                        cancellation.Token)
                    .AsTask();
            cancellation.Cancel();
            transaction.CompleteApply(
                0,
                VerifiedApplyResult());
            await operation;
            Assert(
                viewModel.ApplyResult is null &&
                !viewModel.HasVerifiedApplyResult &&
                !viewModel.CanWrite &&
                viewModel.SurvivingApplyArtifacts.Contains(
                    ExpectedApplyOutput()) &&
                viewModel.SurvivingApplyArtifacts.Contains(
                    ExpectedApplyManifest()) &&
                viewModel.Diagnostics.Any(item =>
                    item.Code ==
                        "hair-regions-apply-stale-committed-artifacts"),
                "Caller-cancelled Apply hid possibly committed output/manifest or re-enabled writing.");
        }

        viewModel.Dispose();
        Assert(
            cleanup.Cleaned.Contains(
                acceptedPreviewRoot),
            "Disposing the wizard retained its current fresh preview output.");
    }

    private static async Task
        RunContentAddressedCacheContractAsync()
    {
        string root = Path.Combine(
            Path.Combine(WorkRoot, "desktop-hair-regions-cache"),
            $"hair-cache-{Guid.NewGuid():N}");
        string sources = Path.Combine(root, "sources");
        string cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(sources);
        try
        {
            FaceGeomHairRegionsPreviewCacheKey firstKey =
                CacheKey('1');
            FaceGeomHairRegionsPreviewResult firstResult =
                FileBackedPreview(
                    sources,
                    "first.png",
                    4096,
                    0x31,
                    firstKey);
            var firstCache =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(cacheRoot));
            FaceGeomHairRegionsPreviewCacheStoreResult stored =
                firstCache.Store(firstKey, firstResult);
            FaceGeomHairRegionsPreviewResult cachedFirst =
                stored.CachedResult ??
                throw new InvalidOperationException(
                    "Cache Store did not return its admitted result.");
            string cachedEntryName =
                Path.GetFileName(
                    Path.GetDirectoryName(
                        cachedFirst.Artifacts[0].Path.Value) ??
                    throw new InvalidOperationException(
                        "Cached artifact had no parent directory.")) ??
                throw new InvalidOperationException(
                    "Cached artifact parent had no directory name.");
            Assert(
                stored.Stored &&
                stored.StoredBytes > 4096 &&
                cachedEntryName ==
                    firstKey.Fingerprint.Value &&
                cachedEntryName.All(character =>
                    !char.IsLetter(character) ||
                    char.IsLower(character)) &&
                firstCache.CurrentBytes <=
                    FaceGeomHairRegionsPreviewCache.MaximumBytes,
                "The cache did not use canonical lowercase SHA directory identity or account for all bytes.");

            var reopened =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(cacheRoot));
            FaceGeomHairRegionsPreviewCacheLookupResult hit =
                reopened.Lookup(firstKey);
            FaceGeomHairRegionsPreviewResult? hitResult =
                hit.Result;
            Assert(
                hit.Hit &&
                hitResult is not null &&
                hitResult.Artifacts.Length == 1 &&
                File.Exists(
                    hitResult.Artifacts[0].Path.Value),
                "A persisted, content-addressed cache entry was not reusable after reopening.");

            byte[] tampered = new byte[4096];
            Array.Fill(tampered, (byte)0x77);
            File.WriteAllBytes(
                hitResult!.Artifacts[0].Path.Value,
                tampered);
            var reopenedAfterTamper =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(cacheRoot));
            FaceGeomHairRegionsPreviewCacheLookupResult rejected =
                reopenedAfterTamper.Lookup(firstKey);
            Assert(
                !rejected.Hit &&
                reopenedAfterTamper.StartupDiagnostics.Any(item =>
                    item.Code ==
                    "hair-regions-preview-cache-invalid"),
                "A tampered persisted render survived cache reopening or crashed cache admission.");

            string calibrationRoot =
                Path.Combine(root, "eviction-probe");
            FaceGeomHairRegionsPreviewCacheStoreResult calibrated =
                new FaceGeomHairRegionsPreviewCache(
                        LabRoot(),
                        new WorkspacePath(calibrationRoot))
                    .Store(
                        CacheKey('9'),
                        FileBackedPreview(
                            sources,
                            "probe.png",
                            4096,
                            0x39,
                            CacheKey('9')));
            Directory.Delete(
                calibrationRoot,
                recursive: true);
            string evictionRoot =
                Path.Combine(root, "eviction-cache");
            long twoEntryCapacity =
                checked(calibrated.StoredBytes * 2);
            var evictionCache =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(evictionRoot),
                    twoEntryCapacity);
            FaceGeomHairRegionsPreviewCacheKey keyA =
                CacheKey('A');
            FaceGeomHairRegionsPreviewCacheKey keyB =
                CacheKey('B');
            FaceGeomHairRegionsPreviewCacheKey keyC =
                CacheKey('C');
            FaceGeomHairRegionsPreviewCacheStoreResult storedA =
                evictionCache.Store(
                    keyA,
                    FileBackedPreview(
                        sources,
                        "a.png",
                        4096,
                        0x41,
                        keyA));
            FaceGeomHairRegionsPreviewCacheStoreResult storedB =
                evictionCache.Store(
                    keyB,
                    FileBackedPreview(
                        sources,
                        "b.png",
                        4096,
                        0x42,
                        keyB));
            FaceGeomHairRegionsPreviewCacheStoreResult storedC =
                evictionCache.Store(
                    keyC,
                    FileBackedPreview(
                        sources,
                        "c.png",
                        4096,
                        0x43,
                        keyC));
            Assert(
                storedA.Stored &&
                storedB.Stored &&
                storedC.Stored &&
                !evictionCache.Contains(keyA) &&
                evictionCache.Contains(keyB) &&
                evictionCache.Contains(keyC) &&
                evictionCache.CurrentBytes <=
                    twoEntryCapacity,
                "Bounded cache eviction was not deterministic FIFO or exceeded its exact cap.");

            bool rejectedOversizedCapacity = false;
            try
            {
                _ = new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(
                        Path.Combine(root, "invalid-capacity")),
                    FaceGeomHairRegionsPreviewCache.MaximumBytes +
                    1);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedOversizedCapacity = true;
            }

            Assert(
                rejectedOversizedCapacity,
                "The disposable preview cache accepted a cap above 1 GiB.");

            AssertRejectedCacheRoot(
                new WorkspacePath(
                    @"F:\ExampleGame\forbidden-cache"));
            AssertRejectedCacheRoot(
                new WorkspacePath(
                    @"\\untrusted-server\share\device-cache"));
            AssertRejectedCacheRoot(
                new WorkspacePath(
                    Path.Combine(WorkRoot, "outside-preview-cache")));
            AssertRejectedReparseCacheRoot(root);

            string viewModelCacheRoot =
                Path.Combine(root, "viewmodel-cache");
            var viewModelCache =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(
                        viewModelCacheRoot));
            var cacheTransaction =
                new CacheAwareHairRegionsWizardTransaction();
            using var cacheViewModel = NewWizard(
                cacheTransaction,
                viewModelCache,
                cleanup: null);
            await cacheViewModel.AnalyzeAsync();
            ConfigurePrimaryAndAccent(
                cacheViewModel);
            Assert(
                cacheViewModel.MoveNext(),
                "Cache-hit test could not reach colors.");
            await cacheViewModel.ProposeAsync();
            await cacheViewModel.RenderPreviewAsync();
            WorkspacePath firstFreshRoot =
                cacheTransaction.OutputRoots.Single();
            Assert(
                cacheTransaction.PreviewCalls == 1 &&
                cacheViewModel.IsPreviewCurrent &&
                cacheViewModel.Preview!.Artifacts.All(item =>
                    item.Path.IsUnder(
                        viewModelCache.Root)) &&
                !Directory.Exists(
                    firstFreshRoot.Value),
                "First render did not publish the admitted cache copy and release its fresh output.");
            await cacheViewModel.RenderPreviewAsync();
            Assert(
                cacheTransaction.PreviewCalls == 1 &&
                cacheViewModel.IsPreviewCurrent &&
                cacheViewModel.Preview!.Artifacts.All(item =>
                    item.Path.IsUnder(
                        viewModelCache.Root)),
                "Second identical render missed the cache or invoked PreviewAsync again.");

            var wrongKeyTransaction =
                new CacheAwareHairRegionsWizardTransaction
                {
                    ReturnWrongRequestAuthority = true
                };
            using var wrongKeyViewModel = NewWizard(
                wrongKeyTransaction,
                viewModelCache,
                cleanup: null);
            await PrepareReadyForPreviewAsync(
                wrongKeyViewModel);
            await wrongKeyViewModel.RenderPreviewAsync();
            Assert(
                wrongKeyTransaction.PreviewCalls == 0 &&
                !wrongKeyViewModel.IsPreviewCurrent &&
                wrongKeyViewModel.Diagnostics.Any(item =>
                    item.Code ==
                    "hair-regions-preview-authority-mismatch"),
                "A cache authority bound to a different request reached PreviewAsync or the UI.");

            var wrongEvidenceTransaction =
                new CacheAwareHairRegionsWizardTransaction
                {
                    ReturnWrongFreshEvidence = true
                };
            var wrongEvidenceCache =
                new FaceGeomHairRegionsPreviewCache(
                    LabRoot(),
                    new WorkspacePath(
                        Path.Combine(
                            root,
                            "wrong-evidence-cache")));
            using var wrongEvidenceViewModel = NewWizard(
                wrongEvidenceTransaction,
                wrongEvidenceCache,
                cleanup: null);
            await PrepareReadyForPreviewAsync(
                wrongEvidenceViewModel);
            await wrongEvidenceViewModel.RenderPreviewAsync();
            Assert(
                wrongEvidenceTransaction.PreviewCalls == 1 &&
                !wrongEvidenceViewModel.IsPreviewCurrent &&
                wrongEvidenceTransaction.OutputRoots.All(rootPath =>
                    !Directory.Exists(
                        rootPath.Value)) &&
                wrongEvidenceViewModel.Diagnostics.Any(item =>
                    item.Code ==
                    "hair-regions-preview-authority-mismatch"),
                "Fresh preview evidence bound to another proposal was published or retained.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task RunApplyAndVerifyGateContractAsync()
    {
        var transaction =
            new RecordingHairRegionsWizardTransaction();
        using var viewModel = NewWizard(
            transaction,
            cache: null,
            cleanup: null);
        await viewModel.AnalyzeAsync();
        ConfigurePrimaryAndAccent(viewModel);
        Assert(viewModel.MoveNext(), "Apply test could not reach colors.");
        await viewModel.ProposeAsync();

        bool refusedBeforePreview = false;
        try
        {
            await viewModel.ApplyAndVerifyAsync();
        }
        catch (InvalidOperationException)
        {
            refusedBeforePreview = true;
        }

        Assert(
            refusedBeforePreview &&
            transaction.ApplyCalls == 0,
            "Apply ran without the current preview and explicit byte-plan acceptance.");

        await viewModel.RenderPreviewAsync();
        Assert(
            viewModel.MoveNext() &&
            viewModel.TryAcceptProposal() &&
            viewModel.MoveNext() &&
            viewModel.CanWrite,
            "Apply test could not establish the complete write gate.");

        Diagnostic failedDiagnostic = new(
            "hair-regions-test-apply-failed",
            DiagnosticSeverity.Error,
            "Verifier readback refused the candidate.");
        transaction.ApplyResults.Enqueue(
            new FaceGeomHairRegionsApplyResult(
                false,
                null,
                null,
                null,
                [],
                [failedDiagnostic]));
        ImmutableArray<Diagnostic> beforeFailure =
            viewModel.Diagnostics;
        await viewModel.ApplyAndVerifyAsync();
        Assert(
            viewModel.ApplyResult is { Succeeded: false } &&
            !viewModel.HasVerifiedApplyResult &&
            viewModel.CanWrite &&
            viewModel.Diagnostics.Any(item =>
                item == failedDiagnostic) &&
            beforeFailure.SequenceEqual(
                viewModel.Diagnostics.Take(
                    beforeFailure.Length)),
            "A failed Apply was hidden, destroyed earlier diagnostics, or incorrectly closed the gate.");

        transaction.ApplyResults.Enqueue(
            VerifiedApplyResult());
        await viewModel.ApplyAndVerifyAsync();
        Assert(
            transaction.ApplyCalls == 2 &&
            viewModel.ApplyResult is { Succeeded: true } &&
            viewModel.HasVerifiedApplyResult &&
            !viewModel.CanWrite,
            "A successful verified Apply did not publish immutable result authority.");

        var wrongProposalTransaction =
            new RecordingHairRegionsWizardTransaction();
        using var wrongProposalViewModel = NewWizard(
            wrongProposalTransaction,
            cache: null,
            cleanup: null);
        await PrepareReadyForApplyAsync(
            wrongProposalViewModel);
        wrongProposalTransaction.ApplyResults.Enqueue(
            VerifiedApplyResult('F'));
        await wrongProposalViewModel.ApplyAndVerifyAsync();
        Assert(
            !wrongProposalViewModel.HasVerifiedApplyResult &&
            !wrongProposalViewModel.CanWrite &&
            wrongProposalViewModel.SurvivingApplyArtifacts.Contains(
                ExpectedApplyOutput()) &&
            wrongProposalViewModel.SurvivingApplyArtifacts.Contains(
                ExpectedApplyManifest()) &&
            wrongProposalViewModel.Diagnostics.Any(item =>
                item.Code ==
                    "hair-regions-apply-unverified-committed-artifacts"),
            "A successful wrong-proposal Apply did not fail closed around its committed paths.");

        WorkspacePath survivor = new(
            Path.Combine(WorkRoot, "wizard-state", "surviving-output.tmp"));
        var survivorTransaction =
            new RecordingHairRegionsWizardTransaction();
        using var survivorViewModel = NewWizard(
            survivorTransaction,
            cache: null,
            cleanup: null);
        await PrepareReadyForApplyAsync(
            survivorViewModel);
        survivorTransaction.ApplyResults.Enqueue(
            VerifiedApplyResult() with
            {
                SurvivingArtifacts =
                    [survivor]
            });
        await survivorViewModel.ApplyAndVerifyAsync();
        bool survivorRefusedReapply = false;
        try
        {
            await survivorViewModel.ApplyAndVerifyAsync();
        }
        catch (InvalidOperationException)
        {
            survivorRefusedReapply = true;
        }

        Assert(
            !survivorViewModel.HasVerifiedApplyResult &&
            !survivorViewModel.CanWrite &&
            survivorRefusedReapply &&
            survivorTransaction.ApplyCalls == 1 &&
            survivorViewModel.SurvivingApplyArtifacts.Contains(
                survivor) &&
            survivorViewModel.Diagnostics.Any(item =>
                item.Code ==
                    "hair-regions-apply-surviving-artifacts" &&
                item.Message.Contains(
                    survivor.Value,
                    StringComparison.Ordinal)),
            "Apply surviving artifacts were hidden, accepted as verified, or allowed a second write.");

        WorkspacePath exceptionSurvivor = new(
            Path.Combine(WorkRoot, "wizard-state", "exception-survivor.tmp"));
        var exceptionTransaction =
            new RecordingHairRegionsWizardTransaction();
        using var exceptionViewModel = NewWizard(
            exceptionTransaction,
            cache: null,
            cleanup: null);
        await PrepareReadyForApplyAsync(
            exceptionViewModel);
        exceptionTransaction.ApplyExceptions.Enqueue(
            new FaceGeomHairRegionsOperationalException(
                "Apply rollback failed.",
                [exceptionSurvivor],
                new IOException("locked output")));
        await exceptionViewModel.ApplyAndVerifyAsync();
        Assert(
            exceptionViewModel.ApplyResult is null &&
            !exceptionViewModel.CanWrite &&
            exceptionViewModel.SurvivingApplyArtifacts.Contains(
                exceptionSurvivor) &&
            exceptionViewModel.Diagnostics.Any(item =>
                item.Code ==
                    "hair-regions-apply-operation-failed"),
            "An operational Apply failure lost its survivor or UI diagnostic.");

        var staleTransaction =
            new BlockingHairRegionsWizardTransaction();
        using var staleViewModel = NewWizard(
            staleTransaction,
            cache: null,
            cleanup: null);
        Task staleAnalyze =
            staleViewModel.AnalyzeAsync().AsTask();
        staleTransaction.CompleteAnalyze(0, 'A');
        await staleAnalyze;
        ConfigurePrimaryAndAccent(
            staleViewModel);
        Assert(
            staleViewModel.MoveNext(),
            "Stale Apply test could not reach colors.");
        Task staleProposal =
            staleViewModel.ProposeAsync().AsTask();
        staleTransaction.CompleteProposal(0, 'D');
        await staleProposal;
        Task stalePreview =
            staleViewModel.RenderPreviewAsync().AsTask();
        staleTransaction.CompletePreview(0, 'F');
        await stalePreview;
        Assert(
            staleViewModel.MoveNext() &&
            staleViewModel.TryAcceptProposal() &&
            staleViewModel.MoveNext(),
            "Stale Apply test could not open the write gate.");
        Task staleApply =
            staleViewModel.ApplyAndVerifyAsync().AsTask();
        staleViewModel.AccentColor = "#E8E2D8";
        Assert(
            staleTransaction.ApplyOperations[0]
                .CancellationToken.IsCancellationRequested,
            "Editing current authority did not cancel active Apply.");
        WorkspacePath staleReportedSurvivor = new(
            Path.Combine(WorkRoot, "wizard-state", "stale-reported-survivor.tmp"));
        staleTransaction.CompleteApply(
            0,
            VerifiedApplyResult() with
            {
                SurvivingArtifacts =
                    [staleReportedSurvivor]
            });
        await staleApply;
        Assert(
            staleViewModel.ApplyResult is null &&
            !staleViewModel.HasVerifiedApplyResult &&
            staleViewModel.SurvivingApplyArtifacts.Contains(
                staleReportedSurvivor) &&
            staleViewModel.SurvivingApplyArtifacts.Contains(
                ExpectedApplyOutput()) &&
            staleViewModel.SurvivingApplyArtifacts.Contains(
                ExpectedApplyManifest()) &&
            staleViewModel.Diagnostics.Any(item =>
                item.Code ==
                    "hair-regions-apply-stale-committed-artifacts"),
            "A cancelled stale Apply result hid possibly committed output/manifest.");
    }

    private static async Task PrepareReadyForApplyAsync(
        FaceGeomHairRegionsWizardViewModel viewModel)
    {
        await PrepareReadyForPreviewAsync(
            viewModel);
        await viewModel.RenderPreviewAsync();
        Assert(
            viewModel.MoveNext() &&
            viewModel.TryAcceptProposal() &&
            viewModel.MoveNext(),
            "Apply fixture could not establish its write gate.");
    }

    private static async Task PrepareReadyForPreviewAsync(
        FaceGeomHairRegionsWizardViewModel viewModel)
    {
        await viewModel.AnalyzeAsync();
        ConfigurePrimaryAndAccent(viewModel);
        Assert(
            viewModel.MoveNext(),
            "Preview fixture could not reach colors.");
        await viewModel.ProposeAsync();
    }

    private static FaceGeomHairRegionsWizardViewModel NewWizard(
        IFaceGeomHairRegionsWizardTransaction transaction,
        FaceGeomHairRegionsPreviewCache? cache,
        IFaceGeomHairRegionsPreviewOutputCleanup? cleanup) =>
        new(
            transaction,
            cache,
            new WorkspacePath(WorkspaceRoot),
            ReviewedIntake(),
            FaceGeomHairRegionsWizardLaunchContext.Standalone,
            cleanup)
        {
            SourcePath =
                SyntheticSourcePath,
            OutputPath =
                Path.Combine(WorkRoot, "wizard-state", "dual-tone.nif"),
            ManifestPath =
                Path.Combine(WorkRoot, "wizard-state", "dual-tone.json")
        };

    private static void AssertRejectedCacheRoot(
        WorkspacePath candidate)
    {
        bool rejected = false;
        try
        {
            _ = new FaceGeomHairRegionsPreviewCache(
                LabRoot(),
                candidate);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                  InvalidDataException)
        {
            rejected = true;
        }

        Assert(
            rejected,
            $"Cache boundary admitted forbidden root {candidate.Value}.");
    }

    private static void AssertRejectedReparseCacheRoot(
        string testRoot)
    {
        string reparse =
            Path.Combine(
                testRoot,
                $"reparse-link-{Guid.NewGuid():N}");
        WorkspacePath candidate = new(
            Path.Combine(
                reparse,
                "cache"));
        bool rejected = false;
        try
        {
            FaceGeomHairRegionsDesktopPathBoundary
                .RequireExistingAncestorsOrdinary(
                    LabRoot(),
                    candidate,
                    "preview cache root",
                    path =>
                        new FaceGeomHairRegionsPathObservation(
                            FileExists: false,
                            DirectoryExists: true,
                            Attributes:
                                string.Equals(
                                    path,
                                    reparse,
                                    StringComparison.OrdinalIgnoreCase)
                                    ? FileAttributes.Directory |
                                      FileAttributes.ReparsePoint
                                    : FileAttributes.Directory));
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(
            rejected,
            "The production ancestor validator admitted a reparse directory.");
    }

    private static void ConfigurePrimaryAndAccent(
        FaceGeomHairRegionsWizardViewModel viewModel)
    {
        viewModel.Regions.Single(item =>
                item.StructuralId == "shape-10")
            .Role = FaceGeomHairRegionRole.Primary;
        viewModel.Regions.Single(item =>
                item.StructuralId == "shape-20")
            .Role = FaceGeomHairRegionRole.Accent;
        viewModel.PrimaryColor = "#201814";
        viewModel.AccentColor = "#F4F1E8";
    }

    private static FaceGeomHairRegionsPreviewCacheKey CacheKey(
        char value) =>
        FaceGeomHairRegionsPreviewCacheKey.Create(
            Hash(value),
            Hash(value),
            Hash(value),
            Hash(value),
            Hash(value),
            Hash(value),
            Hash(value));

    private static FaceGeomHairRegionsPreviewResult
        FileBackedPreview(
            string sourceRoot,
            string fileName,
            int length,
            byte fill,
            FaceGeomHairRegionsPreviewCacheKey key)
    {
        byte[] content = new byte[length];
        Array.Fill(content, fill);
        string path = Path.Combine(sourceRoot, fileName);
        File.WriteAllBytes(path, content);
        Sha256Hash artifactHash = new(
            Convert.ToHexString(
                SHA256.HashData(content)));
        return new FaceGeomHairRegionsPreviewResult(
            true,
            new FaceGeomHairRegionsPreviewEvidence(
                key.SourceSha256,
                key.ProposalSha256,
                key.IntakeSha256,
                key.RendererScriptSha256,
                key.ResolvedTextureAuthoritySha256,
                Hash('E'),
                1,
                478,
                31,
                RenderAuthority(
                    key.RendererScriptSha256,
                    key.ResolvedTextureAuthoritySha256,
                    Hash('2'))),
            [
                new FaceGeomHairRegionsPreviewArtifact(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace,
                    null,
                    new WorkspacePath(path),
                    content.LongLength,
                    artifactHash,
                    100)
            ],
            [],
            VisualAuthority: false,
            RuntimeAuthority: false);
    }

    private static FaceGeomHairRegionsApplyResult
        VerifiedApplyResult(
            char proposalSeed = 'D')
    {
        FaceGeomHairRegionsFile source = new(
            new WorkspacePath(
                SyntheticSourcePath),
            1000,
            Hash('9'));
        FaceGeomHairRegionsFile output = new(
            new WorkspacePath(
                Path.Combine(WorkRoot, "wizard-state", "dual-tone.nif")),
            1000,
            Hash('C'));
        FaceGeomHairRegionsFingerprints fingerprints = new(
            Hash('4'),
            Hash('5'),
            Hash('6'),
            Hash('7'),
            Hash('8'));
        FaceGeomHairRegionsManifest manifest = new(
            FaceGeomHairRegionSchemas.Manifest,
            Hash(proposalSeed),
            source,
            output,
            [],
            [100],
            fingerprints,
            [],
            RuntimeAuthority: false);
        return new FaceGeomHairRegionsApplyResult(
            true,
            manifest,
            new StrictJsonDocumentAuthority<
                FaceGeomHairRegionsManifest>(
                manifest,
                [1, 2, 3],
                Hash('E')),
            new FaceGeomHairRegionsVerification(
                true,
                output,
                [100],
                fingerprints,
                fingerprints,
                []),
            [],
            []);
    }

    private static WorkspacePath ExpectedApplyOutput() =>
        new(
            Path.Combine(WorkRoot, "wizard-state", "dual-tone.nif"));

    private static WorkspacePath ExpectedApplyManifest() =>
        new(
            Path.Combine(WorkRoot, "wizard-state", "dual-tone.json"));

    private static FaceGeomHairRegionsRenderAuthority
        RenderAuthority(
            Sha256Hash renderer,
            Sha256Hash textureCatalog,
            Sha256Hash resolvedTextures) =>
        new(
            renderer,
            Hash('5'),
            Hash('6'),
            renderer,
            Hash('8'),
            textureCatalog,
            resolvedTextures,
            Hash('B'),
            1,
            Hash('C'),
            1,
            [],
            []);

    private static FaceGeomHairRegionsPreviewCacheAuthority
        PreviewCacheAuthority(
            FaceGeomHairRegionsWizardProposalState proposal,
            ReviewedGameIntake intake) =>
        new(
            FaceGeomHairRegionsPreviewCacheKey.Create(
                proposal.Materialization.Candidate.Sha256,
                proposal.RequestDocument.Sha256,
                proposal.ProposalDocument.Sha256,
                Hash('E'),
                Hash('F'),
                Hash('1'),
                Hash('2')),
            intake.IntakeFingerprint);

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static WorkspacePath LabRoot() =>
        new(WorkspaceRoot);

    private static ReviewedGameIntake ReviewedIntake() =>
        new(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(WorkspaceRoot),
            new WorkspacePath(
                Path.Combine(WorkspaceRoot, "tests", "fixtures", "reviewed-data")),
            new WorkspacePath(
                Path.Combine(WorkspaceRoot, "tests", "fixtures", "plugins.txt")),
            new WorkspacePath(
                Path.Combine(WorkRoot, "wizard-state", "intake")),
            Hash('0'),
            [],
            [],
            [],
            [],
            0,
            Hash('1'),
            Hash('2'),
            RuntimeAuthority: false);

    private sealed class RecordingPreviewOutputCleanup :
        IFaceGeomHairRegionsPreviewOutputCleanup
    {
        public List<WorkspacePath> Cleaned { get; } = [];

        public string? FailureMessage { get; set; }

        public ImmutableArray<Diagnostic> Cleanup(
            WorkspacePath outputRoot)
        {
            Cleaned.Add(outputRoot);
            if (FailureMessage is null)
            {
                return [];
            }

            string message = FailureMessage;
            FailureMessage = null;
            return
            [
                new Diagnostic(
                    "hair-regions-preview-discard-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"{message}: {outputRoot.Value}")
            ];
        }
    }

    private sealed record PendingAnalyzeOperation(
        WorkspacePath Source,
        WorkspacePath Output,
        WorkspacePath Manifest,
        TaskCompletionSource<
            FaceGeomHairRegionsWizardAnalysisState> Completion,
        CancellationToken CancellationToken);

    private sealed record PendingProposalOperation(
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsAnalysis> Analysis,
        FaceGeomHairRegionsRequest Request,
        TaskCompletionSource<
            FaceGeomHairRegionsWizardProposalState> Completion,
        CancellationToken CancellationToken);

    private sealed record PendingPreviewOperation(
        FaceGeomHairRegionsWizardProposalState Proposal,
        ReviewedGameIntake Intake,
        WorkspacePath OutputRoot,
        TaskCompletionSource<
            FaceGeomHairRegionsPreviewResult> Completion,
        CancellationToken CancellationToken);

    private sealed record PendingApplyOperation(
        FaceGeomHairRegionsWizardProposalState Proposal,
        TaskCompletionSource<
            FaceGeomHairRegionsApplyResult> Completion,
        CancellationToken CancellationToken);

    private sealed class BlockingHairRegionsWizardTransaction :
        IFaceGeomHairRegionsWizardTransaction
    {
        public List<PendingAnalyzeOperation>
            AnalyzeOperations { get; } = [];

        public List<PendingProposalOperation>
            ProposeOperations { get; } = [];

        public List<PendingPreviewOperation>
            PreviewOperations { get; } = [];

        public List<PendingApplyOperation>
            ApplyOperations { get; } = [];

        public int PreviewCalls =>
            PreviewOperations.Count;

        public ValueTask<FaceGeomHairRegionsWizardAnalysisState>
            AnalyzeAsync(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<
                FaceGeomHairRegionsWizardAnalysisState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            AnalyzeOperations.Add(
                new PendingAnalyzeOperation(
                    source,
                    output,
                    manifest,
                    completion,
                    cancellationToken));
            return new ValueTask<
                FaceGeomHairRegionsWizardAnalysisState>(
                completion.Task);
        }

        public ValueTask<FaceGeomHairRegionsWizardProposalState>
            ProposeAsync(
                StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsAnalysis> analysis,
                FaceGeomHairRegionsRequest request,
                CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<
                FaceGeomHairRegionsWizardProposalState>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ProposeOperations.Add(
                new PendingProposalOperation(
                    analysis,
                    request,
                    completion,
                    cancellationToken));
            return new ValueTask<
                FaceGeomHairRegionsWizardProposalState>(
                completion.Task);
        }

        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                WorkspacePath outputRoot,
                CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<
                FaceGeomHairRegionsPreviewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PreviewOperations.Add(
                new PendingPreviewOperation(
                    proposal,
                    intake,
                    outputRoot,
                    completion,
                    cancellationToken));
            return new ValueTask<
                FaceGeomHairRegionsPreviewResult>(
                completion.Task);
        }

        public ValueTask<FaceGeomHairRegionsApplyResult>
            ApplyAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<
                FaceGeomHairRegionsApplyResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ApplyOperations.Add(
                new PendingApplyOperation(
                    proposal,
                    completion,
                    cancellationToken));
            return new ValueTask<
                FaceGeomHairRegionsApplyResult>(
                completion.Task);
        }

        public ValueTask<
            FaceGeomHairRegionsPreviewCacheAuthority>
            ResolvePreviewCacheAuthorityAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                PreviewCacheAuthority(
                    proposal,
                    intake));
        }

        public void CompleteAnalyze(
            int index,
            char seed)
        {
            PendingAnalyzeOperation pending =
                AnalyzeOperations[index];
            pending.Completion.SetResult(
                RecordingHairRegionsWizardTransaction
                    .CreateAnalysisState(
                        pending.Source,
                        pending.Output,
                        pending.Manifest,
                        seed));
        }

        public void CompleteProposal(
            int index,
            char seed)
        {
            PendingProposalOperation pending =
                ProposeOperations[index];
            pending.Completion.SetResult(
                RecordingHairRegionsWizardTransaction
                    .CreateProposalState(
                        pending.Analysis,
                        pending.Request,
                        seed));
        }

        public void CompletePreview(
            int index,
            char rendererSeed,
            bool succeeded = true)
        {
            PendingPreviewOperation pending =
                PreviewOperations[index];
            FaceGeomHairRegionsPreviewResult result =
                RecordingHairRegionsWizardTransaction
                    .CreatePreviewResult(
                        pending.Proposal,
                        pending.OutputRoot,
                        rendererSeed);
            if (!succeeded)
            {
                result = result with
                {
                    Succeeded = false,
                    Evidence = null,
                    Diagnostics =
                    [
                        new Diagnostic(
                            "hair-regions-test-preview-failed",
                            DiagnosticSeverity.Error,
                            "Controlled renderer failure.")
                    ]
                };
            }

            pending.Completion.SetResult(result);
        }

        public void CompleteApply(
            int index,
            FaceGeomHairRegionsApplyResult result) =>
            ApplyOperations[index].Completion.SetResult(
                result);
    }

    private sealed class CacheAwareHairRegionsWizardTransaction :
        IFaceGeomHairRegionsWizardTransaction
    {
        public int PreviewCalls { get; private set; }

        public List<WorkspacePath> OutputRoots { get; } = [];

        public bool ReturnWrongRequestAuthority { get; init; }

        public bool ReturnWrongFreshEvidence { get; init; }

        public ValueTask<FaceGeomHairRegionsWizardAnalysisState>
            AnalyzeAsync(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                RecordingHairRegionsWizardTransaction
                    .CreateAnalysisState(
                        source,
                        output,
                        manifest,
                        'A'));
        }

        public ValueTask<FaceGeomHairRegionsWizardProposalState>
            ProposeAsync(
                StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsAnalysis> analysis,
                FaceGeomHairRegionsRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                RecordingHairRegionsWizardTransaction
                    .CreateProposalState(
                        analysis,
                        request,
                        'D'));
        }

        public ValueTask<
            FaceGeomHairRegionsPreviewCacheAuthority>
            ResolvePreviewCacheAuthorityAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FaceGeomHairRegionsPreviewCacheAuthority authority =
                PreviewCacheAuthority(
                    proposal,
                    intake);
            if (ReturnWrongRequestAuthority)
            {
                authority = authority with
                {
                    Key =
                        FaceGeomHairRegionsPreviewCacheKey.Create(
                            authority.Key.SourceSha256,
                            Hash('9'),
                            authority.Key.ProposalSha256,
                            authority.Key.IntakeSha256,
                            authority.Key.RendererAuthoritySha256,
                            authority.Key.RendererScriptSha256,
                            authority.Key.TextureCatalogSha256,
                            authority.Key.ResolvedTextureAuthoritySha256)
                };
            }

            return ValueTask.FromResult(authority);
        }

        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                WorkspacePath outputRoot,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviewCalls++;
            OutputRoots.Add(outputRoot);
            Directory.CreateDirectory(
                outputRoot.Value);
            FaceGeomHairRegionsPreviewCacheAuthority authority =
                PreviewCacheAuthority(
                    proposal,
                    intake);
            FaceGeomHairRegionsPreviewResult result =
                FileBackedPreview(
                    outputRoot.Value,
                    "combined-face.png",
                    4096,
                    0x52,
                    authority.Key);
            if (ReturnWrongFreshEvidence)
            {
                result = result with
                {
                    Evidence = result.Evidence! with
                    {
                        ProposalSha256 =
                            Hash('9')
                    }
                };
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<FaceGeomHairRegionsApplyResult>
            ApplyAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The cache test must not Apply.");
    }

    private sealed class NeverCalledHairRegionsWizardTransaction :
        IFaceGeomHairRegionsWizardTransaction
    {
        public ValueTask<FaceGeomHairRegionsWizardAnalysisState>
            AnalyzeAsync(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The initial-state test must not execute analysis.");

        public ValueTask<FaceGeomHairRegionsWizardProposalState>
            ProposeAsync(
                StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
                    analysis,
                FaceGeomHairRegionsRequest request,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The initial-state test must not execute proposal.");

        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                WorkspacePath outputRoot,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The initial-state test must not execute preview.");

        public ValueTask<FaceGeomHairRegionsApplyResult>
            ApplyAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The initial-state test must not execute Apply.");

        public ValueTask<
            FaceGeomHairRegionsPreviewCacheAuthority>
            ResolvePreviewCacheAuthorityAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The initial-state test must not resolve preview authority.");
    }

    private sealed class RecordingHairRegionsWizardTransaction :
        IFaceGeomHairRegionsWizardTransaction
    {
        public int AnalyzeCalls { get; private set; }
        public int ProposeCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public int ApplyCalls { get; private set; }

        public Queue<FaceGeomHairRegionsApplyResult>
            ApplyResults { get; } = [];

        public Queue<Exception>
            ApplyExceptions { get; } = [];

        public ValueTask<FaceGeomHairRegionsWizardAnalysisState>
            AnalyzeAsync(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzeCalls++;
            return ValueTask.FromResult(
                CreateAnalysisState(
                    source,
                    output,
                    manifest,
                    'A'));
        }

        public ValueTask<FaceGeomHairRegionsWizardProposalState>
            ProposeAsync(
                StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
                    analysis,
                FaceGeomHairRegionsRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProposeCalls++;
            return ValueTask.FromResult(
                CreateProposalState(
                    analysis,
                    request,
                    'D'));
        }

        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                WorkspacePath outputRoot,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviewCalls++;
            return ValueTask.FromResult(
                CreatePreviewResult(
                    proposal,
                    outputRoot,
                    'F'));
        }

        public ValueTask<FaceGeomHairRegionsApplyResult>
            ApplyAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            if (ApplyExceptions.Count > 0)
            {
                throw ApplyExceptions.Dequeue();
            }

            if (ApplyResults.Count == 0)
            {
                throw new InvalidOperationException(
                    "The state-only test did not configure an Apply result.");
            }

            return ValueTask.FromResult(
                ApplyResults.Dequeue());
        }

        public ValueTask<
            FaceGeomHairRegionsPreviewCacheAuthority>
            ResolvePreviewCacheAuthorityAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                PreviewCacheAuthority(
                    proposal,
                    intake));
        }

        public static FaceGeomHairRegionsWizardAnalysisState
            CreateAnalysisState(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                char seed)
        {
            FaceGeomHairRegionsAnalysis analysis =
                Analysis(source);
            var analysisDocument =
                new StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsAnalysis>(
                    analysis,
                    [1, 2, 3],
                    Hash(seed));
            return new FaceGeomHairRegionsWizardAnalysisState(
                analysisDocument,
                new FaceGeomHairRegionsRequest(
                    FaceGeomHairRegionSchemas.Request,
                    analysisDocument.Sha256,
                    analysis.Source,
                    "#000000",
                    "#FFFFFF",
                    analysis.Regions.Select(region =>
                            new FaceGeomHairRegionAssignment(
                                region.StructuralId,
                                FaceGeomHairRegionRole.Preserve))
                        .ToImmutableArray(),
                    output,
                    manifest),
                []);
        }

        public static FaceGeomHairRegionsWizardProposalState
            CreateProposalState(
                StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsAnalysis> analysis,
                FaceGeomHairRegionsRequest request,
                char seed)
        {
            var requestDocument =
                new StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsRequest>(
                    request,
                    [4, 5, 6],
                    Hash('B'));
            var proposal = new FaceGeomHairRegionsProposal(
                FaceGeomHairRegionSchemas.Proposal,
                analysis.Sha256,
                requestDocument.Sha256,
                request.Source,
                request.Output,
                request.Manifest,
                request.PrimaryColor,
                request.AccentColor,
                request.Assignments,
                [],
                [100],
                analysis.Value.Fingerprints,
                analysis.Value.Fingerprints,
                request.Source with
                {
                    Path = request.Output,
                    Sha256 = Hash('C')
                },
                null);
            var proposalDocument =
                new StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsProposal>(
                    proposal,
                    [7, 8, 9],
                    Hash(seed));
            return new FaceGeomHairRegionsWizardProposalState(
                requestDocument,
                proposalDocument,
                new FaceGeomHairRegionsProposalMaterialization(
                    [1],
                    [2],
                    proposal.ExpectedOutput,
                    [100],
                    proposal.SourceFingerprints,
                    proposal.ExpectedOutputFingerprints),
                []);
        }

        public static FaceGeomHairRegionsPreviewResult
            CreatePreviewResult(
                FaceGeomHairRegionsWizardProposalState proposal,
                WorkspacePath outputRoot,
                char rendererSeed) =>
            new(
                true,
                new FaceGeomHairRegionsPreviewEvidence(
                    Hash('C'),
                    proposal.ProposalDocument.Sha256,
                    Hash('E'),
                    Hash(rendererSeed),
                    Hash('2'),
                    Hash('3'),
                    1,
                    478,
                    31,
                    RenderAuthority(
                        Hash(rendererSeed),
                        Hash('2'),
                        Hash('A'))),
                [
                    new FaceGeomHairRegionsPreviewArtifact(
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace,
                        null,
                        new WorkspacePath(Path.Combine(
                            outputRoot.Value,
                            "combined-face.png")),
                        16,
                        Hash('3'),
                        10)
                ],
                [],
                VisualAuthority: false,
                RuntimeAuthority: false);

        private static FaceGeomHairRegionsAnalysis Analysis(
            WorkspacePath source)
        {
            var file = new FaceGeomHairRegionsFile(
                source,
                1000,
                Hash('9'));
            var fingerprints = new FaceGeomHairRegionsFingerprints(
                Hash('4'),
                Hash('5'),
                Hash('6'),
                Hash('7'),
                Hash('8'));
            return new FaceGeomHairRegionsAnalysis(
                FaceGeomHairRegionSchemas.Analysis,
                file,
                [
                    Region(
                        "shape-10",
                        "Hair outer",
                        "shader-4",
                        10,
                        4,
                        400),
                    Region(
                        "shape-11",
                        "Hair outer duplicate",
                        "shader-4",
                        11,
                        4,
                        400),
                    Region(
                        "shape-20",
                        "Hair highlight",
                        "shader-8",
                        20,
                        8,
                        800)
                ],
                fingerprints,
                null);
        }

        private static FaceGeomHairRegionsRegion Region(
            string structuralId,
            string name,
            string group,
            int shapeBlock,
            int shaderBlock,
            long tintOffset) =>
            new(
                structuralId,
                name,
                0,
                "BSTriShape",
                shapeBlock,
                0,
                "BSLightingShaderProperty",
                shaderBlock,
                group,
                [structuralId],
                group == "shader-4"
                    ? ["shape-10", "shape-11"]
                    : [structuralId],
                30,
                ["textures\\actors\\character\\hair\\hair.dds"],
                "#806040",
                [0x3F000000U, 0x3EC00000U, 0x3E800000U],
                tintOffset,
                12,
                FaceGeomHairRegionRole.Preserve);

    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
