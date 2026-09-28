using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.Desktop.Smoke;

internal static class ReferencePresetAuthoringViewModelTests
{
    public static void Run()
    {
        RunCompleteStateMachine().GetAwaiter().GetResult();
        RunStaleCancellationAndRecovery().GetAwaiter().GetResult();
        RunBaselinePreviewDoesNotBlockDispatcher();
        RunBaselinePreviewCancellation().GetAwaiter().GetResult();
    }

    private static async Task RunCompleteStateMachine()
    {
        string rootValue = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation",
            "03-builds",
            "work",
            "desktop-reference-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        var sessions = new ControlledSessionService();
        var transaction = new ControlledTransaction();
        ReferencePresetNpcDesktopHandoff? handoff = null;
        using var viewModel = new ReferencePresetAuthoringViewModel(
            transaction,
            sessions,
            new ControlledResourceService(),
            new ControlledRenderInputBuilder(),
            new ControlledMeshBinder(),
            new ControlledProjector(),
            new WorkspacePath(@"K:\ExampleWorkspace"),
            new WorkspacePath(rootValue),
            candidate =>
            {
                handoff = candidate;
                return true;
            });

        Require(
            viewModel.CurrentStep == ReferencePresetDesktopStep.Empty &&
            viewModel.NewImageViewRole ==
                ReferenceImageViewRole.Front &&
            !viewModel.CanAnalyze &&
            !viewModel.AnalyzeCommand.CanExecute(null),
            "reference-desktop-empty-state-and-image-role");

        ReviewedGameIntake reviewedIntake = ReviewedIntake(rootValue);
        viewModel.ApplyReviewedIntake(reviewedIntake);
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Defining &&
            viewModel.FocusTarget == "Reference target name",
            "reference-desktop-intake-state");

        ReferencePresetIntake intake = Intake(rootValue);
        viewModel.DefineTarget(intake);
        Require(
            viewModel.CanAnalyze &&
            viewModel.AnalyzeCommand.CanExecute(null),
            "reference-desktop-define-enablement");

        await viewModel.AnalyzeAsync();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.ReviewRequired &&
            viewModel.InferenceProposal is not null &&
            viewModel.FocusTarget == "Review semantic anchors" &&
            !viewModel.IsBusy,
            "reference-desktop-review-state");

        viewModel.ApplyReviewedDesign(
            ReviewedDesign(transaction.ProposalSha256));
        Require(
            viewModel.CanAdvance &&
            viewModel.AdvanceCommand.CanExecute(null),
            "reference-desktop-reviewed-enablement");

        await viewModel.AdvanceAsync();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.ResourceChoice &&
            viewModel.ResourceSnapshot is not null &&
            viewModel.RenderInput is not null,
            "reference-desktop-resource-state");

        await viewModel.AdvanceAsync();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.ProposalReady &&
            viewModel.AuthoringProposal is not null &&
            viewModel.AuthoringProposalSha256 ==
                transaction.AuthoringProposalSha256 &&
            viewModel.CanApply,
            "reference-desktop-proposal-state");

        await viewModel.ApplyAsync();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Completed &&
            viewModel.VerifiedPreset is not null &&
            viewModel.CanContinueToNpc,
            "reference-desktop-completed-state");

        viewModel.ContinueToNpc();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.DownstreamHandoff &&
            handoff?.Preset.PresetSha256 ==
                viewModel.VerifiedPreset!.PresetSha256 &&
            handoff.ReviewedIntake == reviewedIntake,
            "reference-desktop-exact-handoff");
    }

    private static async Task RunStaleCancellationAndRecovery()
    {
        string rootValue = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation",
            "03-builds",
            "work",
            "desktop-reference-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        var sessions = new ControlledSessionService();
        var transaction = new ControlledTransaction
        {
            HoldProposal = true
        };
        using var viewModel = new ReferencePresetAuthoringViewModel(
            transaction,
            sessions,
            new ControlledResourceService(),
            new ControlledRenderInputBuilder(),
            new ControlledMeshBinder(),
            new ControlledProjector(),
            new WorkspacePath(@"K:\ExampleWorkspace"),
            new WorkspacePath(rootValue));
        viewModel.ApplyReviewedIntake(ReviewedIntake(rootValue));
        ReferencePresetIntake first = Intake(rootValue);
        viewModel.DefineTarget(first);

        Task analyzing = viewModel.AnalyzeAsync();
        await transaction.ProposalStarted.Task;
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Analyzing &&
            viewModel.IsBusy &&
            viewModel.BlocksClose &&
            viewModel.CancelCommand.CanExecute(null),
            "reference-desktop-busy-close");

        viewModel.DefineTarget(first with
        {
            Description = "changed while inference was active"
        });
        transaction.ReleaseProposal();
        await analyzing;
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Stale &&
            viewModel.InferenceProposal is null &&
            viewModel.CanAnalyze,
            "reference-desktop-stale-suppression");

        transaction.HoldProposal = true;
        Task cancelled = viewModel.AnalyzeAsync();
        await transaction.ProposalStarted.Task;
        viewModel.Cancel();
        transaction.ReleaseProposal();
        await cancelled;
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Cancelled &&
            !viewModel.IsBusy &&
            viewModel.CanAnalyze,
            "reference-desktop-cancelled-state");

        transaction.HoldProposal = false;
        transaction.RefuseProposal = true;
        await viewModel.AnalyzeAsync();
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.RecoverableError &&
            viewModel.Diagnostics.Any(item =>
                item.Contains(
                    "controlled-proposal-refusal",
                    StringComparison.Ordinal)),
            "reference-desktop-recoverable-error");

        ReferencePresetIntake intake = Intake(rootValue);
        WorkspacePath intakePath = new(Path.Combine(
            rootValue, "recovery-intake.json"));
        WorkspacePath proposalPath = new(Path.Combine(
            rootValue, "recovery-proposal.json"));
        ReferencePresetSessionWriteResult intakeWrite =
            await sessions.WriteAsync(
                new ReferencePresetSessionWriteRequest(
                    intakePath,
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind.Intake,
                        Intake: intake)),
                CancellationToken.None);
        LandmarkInferenceProposal proposal =
            ControlledTransaction.Proposal(
                intakeWrite.ContentSha256!.Value);
        ReferencePresetSessionWriteResult proposalWrite =
            await sessions.WriteAsync(
                new ReferencePresetSessionWriteRequest(
                    proposalPath,
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind
                            .InferenceProposal,
                        InferenceProposal: proposal)),
                CancellationToken.None);
        await viewModel.RecoverAsync(
            new ReferencePresetDesktopRecovery(
                ReviewedIntake(rootValue),
                intakePath,
                intakeWrite.ContentSha256.Value,
                proposalPath,
                proposalWrite.ContentSha256!.Value));
        Require(
            viewModel.CurrentStep ==
                ReferencePresetDesktopStep.ReviewRequired &&
            viewModel.InferenceProposal == proposal &&
            viewModel.ProgressText.Contains(
                "Recovered",
                StringComparison.Ordinal),
            "reference-desktop-session-recovery");
    }

    private static void RunBaselinePreviewDoesNotBlockDispatcher()
    {
        string rootValue = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation",
            "03-builds",
            "work",
            "desktop-reference-responsive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        var renderer = new ControlledSlowRenderer();
        try
        {
            using var viewModel = CreatePreviewViewModel(
                rootValue,
                renderer);
            PrepareForResourceClosure(viewModel, rootValue);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            var heartbeat = new ManualResetEventSlim();
            bool heartbeatBeforeRelease = false;
            Task coordinator = Task.Run(() =>
            {
                Require(
                    renderer.Started.Wait(TimeSpan.FromSeconds(5)),
                    "reference-desktop-render-started");
                _ = dispatcher.BeginInvoke(
                    new Action(heartbeat.Set),
                    DispatcherPriority.Send);
                heartbeatBeforeRelease =
                    heartbeat.Wait(TimeSpan.FromSeconds(2));
                renderer.Release.Set();
            });

            Task? advancing = null;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(
                DispatcherPriority.Send,
                dispatcher)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                renderer.Release.Set();
                frame.Continue = false;
            };
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                advancing = viewModel.AdvanceAsync();
                _ = advancing.ContinueWith(
                    _ => dispatcher.BeginInvoke(
                        new Action(() => frame.Continue = false),
                        DispatcherPriority.Send),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }));
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            coordinator.GetAwaiter().GetResult();
            (advancing ?? throw new InvalidOperationException(
                "The resource-closure operation never started."))
                .GetAwaiter().GetResult();

            Require(
                heartbeatBeforeRelease &&
                renderer.RenderThreadId != dispatcherThreadId &&
                viewModel.CurrentStep ==
                ReferencePresetDesktopStep.ResourceChoice &&
                viewModel.BaselinePreviews is [{ ImagePath: var imagePath }] &&
                File.Exists(imagePath.Value),
                "reference-desktop-baseline-render-off-dispatcher");
        }
        finally
        {
            renderer.Release.Set();
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static async Task RunBaselinePreviewCancellation()
    {
        string rootValue = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation",
            "03-builds",
            "work",
            "desktop-reference-render-cancel-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        var renderer = new ControlledSlowRenderer();
        try
        {
            using var viewModel = CreatePreviewViewModel(
                rootValue,
                renderer);
            PrepareForResourceClosure(viewModel, rootValue);
            Task advancing = Task.Run(viewModel.AdvanceAsync);
            Require(
                renderer.Started.Wait(TimeSpan.FromSeconds(5)),
                "reference-desktop-cancel-render-started");
            viewModel.Cancel();
            renderer.Release.Set();
            await advancing;
            Require(
                viewModel.CurrentStep ==
                ReferencePresetDesktopStep.Cancelled &&
                viewModel.BaselinePreviews.IsDefaultOrEmpty,
                "reference-desktop-baseline-render-cancellation");
        }
        finally
        {
            renderer.Release.Set();
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static ReferencePresetAuthoringViewModel CreatePreviewViewModel(
        string rootValue,
        IReferencePresetCpuRenderer renderer) =>
        new(
            new ControlledTransaction(),
            new ControlledSessionService(),
            new ControlledResourceService(),
            new ControlledRenderInputBuilder(includeCamera: true),
            new ControlledMeshBinder(),
            new ControlledProjector(),
            new WorkspacePath(@"K:\ExampleWorkspace"),
            new WorkspacePath(rootValue),
            baselineRenderer: renderer);

    private static void PrepareForResourceClosure(
        ReferencePresetAuthoringViewModel viewModel,
        string rootValue)
    {
        viewModel.ApplyReviewedIntake(ReviewedIntake(rootValue));
        viewModel.DefineTarget(Intake(rootValue));
        viewModel.AnalyzeAsync().GetAwaiter().GetResult();
        viewModel.ApplyReviewedDesign(ReviewedDesign(
            viewModel.InferenceProposalSha256 ??
            throw new InvalidOperationException(
                "The controlled proposal hash was not retained.")));
    }

    private static ReferencePresetIntake Intake(string root)
    {
        FormReference race = new(
            new PluginName("Skyrim.esm"),
            new FormId(0x00013746));
        WorkspacePath dataRoot = new(Path.Combine(root, "Data"));
        Directory.CreateDirectory(dataRoot.Value);
        WorkspacePath image = new(Path.Combine(root, "front.png"));
        WorkspacePath baseline = new(Path.Combine(root, "baseline.jslot"));
        File.WriteAllBytes(image.Value, [1, 2, 3, 4]);
        File.WriteAllBytes(baseline.Value, [5, 6, 7, 8]);
        var pluginAuthority = new SkyrimFaceRecordPluginAuthority(
            new PluginName("Skyrim.esm"),
            new WorkspacePath(Path.Combine(dataRoot.Value, "Skyrim.esm")),
            Hash('1'));
        var target = new RaceMenuPresetTarget(
            "controlled-target",
            race,
            NpcSex.Female,
            dataRoot,
            [pluginAuthority]);
        return new ReferencePresetIntake(
            1,
            "controlled-reference",
            "Controlled Reference",
            race,
            NpcSex.Female,
            50.0F,
            "high-poly-head",
            baseline,
            HashFile(baseline.Value),
            "oval face",
            [
                new ReferenceImageAuthority(
                    "front",
                    image,
                    HashFile(image.Value),
                    new FileInfo(image.Value).Length,
                    ReferenceImageViewRole.Front)
            ],
            target);
    }

    private static ReviewedGameIntake ReviewedIntake(string root)
    {
        WorkspacePath dataRoot = new(Path.Combine(root, "Data"));
        Directory.CreateDirectory(dataRoot.Value);
        WorkspacePath pluginPath =
            new(Path.Combine(dataRoot.Value, "Skyrim.esm"));
        if (!File.Exists(pluginPath.Value))
            File.WriteAllBytes(pluginPath.Value, [9, 8, 7, 6]);
        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            dataRoot,
            new WorkspacePath(Path.Combine(root, "loadorder.txt")),
            new WorkspacePath(Path.Combine(root, "output")),
            Hash('2'),
            [
                new PluginClosureReviewEntry(
                    new PluginName("Skyrim.esm"),
                    0,
                    true,
                    true,
                    true,
                    false,
                    true,
                    pluginPath,
                    HashFile(pluginPath.Value),
                    [])
            ],
            [],
            [],
            [],
            1,
            Hash('3'),
            Hash('4'),
            false);
    }

    private static ReviewedReferencePresetDesign ReviewedDesign(
        Sha256Hash proposalHash)
    {
        FormReference race = new(
            new PluginName("Skyrim.esm"),
            new FormId(0x00013746));
        var anchor = new ReferenceSemanticAnchor(
            ReferenceSemanticAnchorKind.NoseTip,
            1,
            0.5,
            0.5,
            0.95,
            true,
            ReferenceAnchorReviewState.Accepted);
        return new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            proposalHash,
            true,
            [
                new ReviewedReferenceView(
                    "front",
                    ReferenceImageViewRole.Front,
                    Hash('5'),
                    0.95,
                    0.0,
                    true,
                    [anchor])
            ],
            [],
            [],
            new ReferencePresetCatalogSelection(
                true,
                race,
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x10)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x11)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x12)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x13)),
                []),
            [
                new ReferenceMeshAnchorBinding(
                    ReferenceImageViewRole.Front,
                    ReferenceSemanticAnchorKind.NoseTip,
                    "head.nif",
                    "Head",
                    Hash('6'),
                    Hash('7'),
                    Hash('8'),
                    Hash('9'),
                    Hash('a'),
                    0,
                    0,
                    1,
                    2,
                    1.0,
                    0.0,
                    0.0)
            ]);
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(path))));

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
            throw new InvalidOperationException(diagnostic);
    }

    private sealed class ControlledTransaction
        : IReferencePresetAuthoringTransaction
    {
        private TaskCompletionSource? proposalRelease;
        public TaskCompletionSource ProposalStarted { get; private set; } =
            NewSignal();
        public bool HoldProposal { get; set; }
        public bool RefuseProposal { get; set; }
        public Sha256Hash ProposalSha256 { get; } = Hash('b');
        public Sha256Hash AuthoringProposalSha256 { get; } = Hash('c');

        public async ValueTask<ReferencePresetDesignProposalResult>
            ProposeDesignAsync(
                ReferencePresetDesignProposalRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            progress?.Report(new ReferencePresetProgress(
                ReferencePresetProgressStage.InferLandmarks,
                0,
                1,
                "Controlled inference."));
            ProposalStarted.TrySetResult();
            if (HoldProposal)
            {
                proposalRelease = NewSignal();
                await proposalRelease.Task.WaitAsync(cancellationToken);
            }
            if (RefuseProposal)
            {
                return new ReferencePresetDesignProposalResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    [
                        new Diagnostic(
                            "controlled-proposal-refusal",
                            DiagnosticSeverity.Error,
                            "Controlled proposal refusal.")
                    ]);
            }
            return new ReferencePresetDesignProposalResult(
                true,
                Proposal(request.ExpectedIntakeSha256),
                ProposalSha256,
                null,
                null,
                []);
        }

        public ValueTask<ReferencePresetWriteResult> WritePresetAsync(
            ReferencePresetWriteRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ReferencePresetProgress(
                request.Apply
                    ? ReferencePresetProgressStage.WritePreset
                    : ReferencePresetProgressStage.Solve,
                1,
                1,
                request.Apply
                    ? "Controlled write."
                    : "Controlled comparison."));
            ReferencePresetAuthoringProposal proposal =
                AuthoringProposal(request);
            if (!request.Apply)
            {
                return ValueTask.FromResult(
                    new ReferencePresetWriteResult(
                        true,
                        proposal,
                        AuthoringProposalSha256,
                        null,
                        []));
            }

            Directory.CreateDirectory(request.OutputRoot.Value);
            WorkspacePath presetPath = new(Path.Combine(
                request.OutputRoot.Value,
                "controlled-reference.jslot"));
            File.WriteAllBytes(presetPath.Value, [10, 20, 30, 40]);
            Sha256Hash presetHash = HashFile(presetPath.Value);
            return ValueTask.FromResult(
                new ReferencePresetWriteResult(
                    true,
                    proposal,
                    AuthoringProposalSha256,
                    new VerifiedReferencePreset(
                        1,
                        ReferencePresetAuthorityKind.VerifiedPreset,
                        "controlled-reference",
                        new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x00013746)),
                        NpcSex.Female,
                        50.0F,
                        AuthoringProposalSha256,
                        presetPath,
                        presetHash,
                        Hash('d'),
                        []),
                    []));
        }

        public ValueTask<ReferencePresetNpcBuildResult>
            WritePresetAndBuildNpcAsync(
                ReferencePresetNpcBuildRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void ReleaseProposal()
        {
            proposalRelease?.TrySetResult();
            ProposalStarted = NewSignal();
        }

        public static LandmarkInferenceProposal Proposal(
            Sha256Hash intakeSha256) =>
            new(
                1,
                ReferencePresetAuthorityKind.InferenceProposal,
                intakeSha256,
                Hash('e'),
                [
                    new ReferenceImageInference(
                        "front",
                        ReferenceImageViewRole.Front,
                        Hash('f'),
                        Hash('1'),
                        100,
                        100,
                        0.95,
                        [
                            new ReferenceFaceLandmark(
                                1, 0.5, 0.5, 0.0, 1.0, 1.0)
                        ],
                        ImmutableArray.CreateRange(
                            Enumerable.Repeat(0.0, 16)),
                        0.0,
                        Hash('2'),
                        Hash('3'),
                        Hash('4'))
                ],
                [],
                []);

        private static ReferencePresetAuthoringProposal
            AuthoringProposal(ReferencePresetWriteRequest request) =>
            new(
                1,
                ReferencePresetAuthorityKind.AuthoringProposal,
                "controlled-reference",
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x00013746)),
                NpcSex.Female,
                50.0F,
                request.ReviewedDesignSha256,
                request.ResourceSnapshotSha256,
                new ReferenceRaceMenuPresetSolverResult(
                    ImmutableDictionary<string, double>.Empty,
                    ImmutableDictionary<string, double>.Empty,
                    [],
                    [],
                    [],
                    256,
                    0.0,
                    Hash('5'),
                    []),
                new ReferencePresetComparisonResult([], [], [], []),
                [],
                []);

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledSessionService
        : IReferencePresetSessionService
    {
        private readonly Dictionary<string, (
            ReferencePresetSessionDocument Document,
            Sha256Hash Hash)> documents =
            new(StringComparer.OrdinalIgnoreCase);
        private int hashOrdinal = 20;

        public ValueTask<ReferencePresetSessionWriteResult> WriteAsync(
            ReferencePresetSessionWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(
                Path.GetDirectoryName(request.DestinationPath.Value)!);
            File.WriteAllText(
                request.DestinationPath.Value,
                request.Document.Kind.ToString());
            Sha256Hash hash = new(
                hashOrdinal.ToString(
                    "X64",
                    CultureInfo.InvariantCulture));
            hashOrdinal++;
            documents.Add(
                request.DestinationPath.Value,
                (request.Document, hash));
            return ValueTask.FromResult(
                new ReferencePresetSessionWriteResult(
                    true, hash, []));
        }

        public ValueTask<ReferencePresetSessionReadResult> ReadAsync(
            ReferencePresetSessionReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documents.TryGetValue(
                    request.SourcePath.Value,
                    out var row) &&
                row.Hash == request.ExpectedSha256 &&
                row.Document.Kind == request.ExpectedKind)
            {
                return ValueTask.FromResult(
                    new ReferencePresetSessionReadResult(
                        row.Document, row.Hash, []));
            }
            return ValueTask.FromResult(
                new ReferencePresetSessionReadResult(
                    null,
                    null,
                    [
                        new Diagnostic(
                            "controlled-session-missing",
                            DiagnosticSeverity.Error,
                            "Controlled session is absent.")
                    ]));
        }
    }

    private sealed class ControlledProjector
        : IReferenceSemanticLandmarkProjector
    {
        public ValueTask<ReferenceSemanticLandmarkProjectionResult>
            ProjectAsync(
                ReferenceSemanticLandmarkProjectionRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferenceSemanticLandmarkProjectionResult(
                    [
                        new ReferenceSemanticAnchorProposal(
                            ReferenceSemanticAnchorKind.NoseTip,
                            1,
                            0.5,
                            0.5,
                            0.95,
                            true,
                            false)
                    ],
                    []));
        }
    }

    private sealed class ControlledResourceService
        : IReferencePresetResourceSnapshotService
    {
        public ValueTask<ReferencePresetResourceSnapshotResult>
            CreateAsync(
                ReferencePresetResourceSnapshotRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferencePresetResourceSnapshotResult(
                    new ReferencePresetResourceSnapshot(
                        1,
                        request.Intake.ProjectId,
                        request.ReviewedDesignSha256,
                        request.Intake.BaselineJslotSha256,
                        null!,
                        request.ReviewedDesign.CatalogSelection,
                        [],
                        [],
                        [],
                        Hash('6'),
                        Hash('7'),
                        []),
                    []));
        }
    }

    private sealed class ControlledRenderInputBuilder(
        bool includeCamera = false)
        : IReferencePresetRenderInputBuilder
    {
        public ValueTask<ReferencePresetRenderInputResult> BuildAsync(
            ReferencePresetRenderInputRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferencePresetRenderInputResult(
                    new ReferencePresetRenderInput(
                        [],
                        [],
                        includeCamera
                            ?
                            [
                                new ReferenceOrthographicCamera(
                                    ReferenceImageViewRole.Front,
                                    0,
                                    0,
                                    -1,
                                    1,
                                    -1,
                                    1,
                                    -1,
                                    1,
                                    Hash('f'))
                            ]
                            : [],
                        Hash('a')),
                    []));
        }
    }

    private sealed class ControlledSlowRenderer
        : IReferencePresetCpuRenderer
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int RenderThreadId { get; private set; }

        public ReferencePresetCpuRenderResult Render(
            ReferencePresetCpuRenderRequest request)
        {
            RenderThreadId = Environment.CurrentManagedThreadId;
            Started.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException(
                    "The controlled preview renderer was not released.");
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10];
            Sha256Hash pngHash = new(
                Convert.ToHexString(SHA256.HashData(png)));
            return new ReferencePresetCpuRenderResult(
                true,
                request.Width,
                request.Height,
                [],
                ImmutableArray.CreateRange(png),
                pngHash,
                Hash('1'),
                Hash('2'),
                Hash('3'),
                Hash('4'),
                []);
        }
    }

    private sealed class ControlledMeshBinder
        : IReferencePresetMeshAnchorBinder
    {
        public ReferenceMeshAnchorBindResult Bind(
            ReferenceMeshAnchorBindRequest request) =>
            new(null, []);
    }
}
