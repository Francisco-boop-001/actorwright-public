using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NpcManager.Application;
using NpcManager.Desktop;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void
        TestFaceGeomHairRegionsWizardWindow()
    {
        WorkspacePath labRoot = ActorwrightWorkspace.ResolveRoot();
        string root = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "hair-regions-window",
            Guid.NewGuid().ToString("N")).Value;
        string data = Path.Combine(
            root,
            "Data");
        Directory.CreateDirectory(
            data);
        App? app = null;
        bool ownsApplication =
            System.Windows.Application.Current is null;
        if (ownsApplication)
        {
            app = new App
            {
                ShutdownMode =
                    ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();
        }
        SynchronizationContext? priorContext =
            SynchronizationContext.Current;
        bool dispatcherContextInstalled = false;
        FaceGeomHairRegionsWizardWindow? window = null;
        try
        {
            ReviewedGameIntake intake =
                CreateHairWizardIntakeAsync(
                    labRoot,
                    root,
                    data)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(
                    Dispatcher.CurrentDispatcher));
            dispatcherContextInstalled = true;
            var transaction =
                new RenderedHairWizardTransaction(
                    labRoot);
            using var viewModel =
                CreateRenderedHairWizard(
                    transaction,
                    intake,
                    root);
            window =
                new FaceGeomHairRegionsWizardWindow(
                    viewModel)
                {
                    Left = 30,
                    Top = 30,
                    Width = 980,
                    Height = 680,
                    WindowStartupLocation =
                        WindowStartupLocation.Manual
                };
            window.Show();
            window.UpdateLayout();

            AssertHairWizardWindowContract(
                window,
                expectedWidth: 980,
                expectedHeight: 680);
            Button analyze =
                RequireHairWizardVisual<Button>(
                    window,
                    "Analyze exact FaceGeom");
            analyze.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            PumpHairWizardUntil(
                () =>
                    viewModel.CurrentStepIndex == 1 &&
                    viewModel.Regions.Count >= 3);
            window.UpdateLayout();

            ListBox regions =
                RequireHairWizardVisual<ListBox>(
                    window,
                    "HairTint regions");
            ScrollViewer regionScroll =
                RequireHairWizardVisual<ScrollViewer>(
                    window,
                    "HairTint region cards scroll");
            Assert(
                regions.SelectionMode ==
                    SelectionMode.Extended &&
                regionScroll.ActualHeight > 0 &&
                double.IsFinite(
                    regionScroll.ViewportHeight),
                "Rendered Regions step lacks a finite extended-selection viewport.");

            FaceGeomHairRegionCardViewModel primary =
                viewModel.Regions.Single(item =>
                    string.Equals(
                        item.Name,
                        "SyntheticMainHair",
                        StringComparison.Ordinal));
            FaceGeomHairRegionCardViewModel accent =
                viewModel.Regions.Single(item =>
                    string.Equals(
                        item.Name,
                        "SyntheticHairHighlight",
                        StringComparison.Ordinal));
            FaceGeomHairRegionCardViewModel hairline =
                viewModel.Regions.Single(item =>
                    item.Name.Contains(
                        "HAIRLINE",
                        StringComparison.OrdinalIgnoreCase));
            regions.SelectedItems.Add(
                primary);
            RaiseHairWizardKey(
                window,
                Key.P);
            Assert(
                viewModel.Regions
                    .Where(item =>
                        item.SharedShaderGroupId ==
                        primary.SharedShaderGroupId)
                    .All(item =>
                        item.Role ==
                        FaceGeomHairRegionRole.Primary),
                "Keyboard P did not classify the complete shared tint group as Primary.");
            regions.SelectedItems.Clear();
            regions.SelectedItems.Add(
                accent);
            RaiseHairWizardKey(
                window,
                Key.A);
            Assert(
                viewModel.Regions
                    .Where(item =>
                        item.SharedShaderGroupId ==
                        accent.SharedShaderGroupId)
                    .All(item =>
                        item.Role ==
                        FaceGeomHairRegionRole.Accent),
                "Keyboard A did not classify the complete shared tint group as Accent.");
            RaiseHairWizardKey(
                window,
                Key.D0);
            Assert(
                accent.Role ==
                    FaceGeomHairRegionRole.Preserve,
                "Keyboard 0 did not restore Preserve.");
            RaiseHairWizardKey(
                window,
                Key.A);
            regions.SelectedItems.Clear();
            regions.SelectedItems.Add(
                hairline);
            RaiseHairWizardKey(
                window,
                Key.P);
            Assert(
                hairline.Role ==
                    FaceGeomHairRegionRole.Primary &&
                viewModel.Regions
                    .Where(item =>
                        item.Name.Contains(
                            "lash",
                            StringComparison.OrdinalIgnoreCase) ||
                        item.Name.Contains(
                            "brow",
                            StringComparison.OrdinalIgnoreCase))
                    .All(item =>
                        item.Role ==
                        FaceGeomHairRegionRole.Preserve),
                "The synthetic fixture did not keep the hairline Primary while preserving brows and lashes.");

            TextBox filter =
                RequireHairWizardVisual<TextBox>(
                    window,
                    "Filter HairTint regions");
            filter.Text =
                accent.Name;
            PumpHairWizardUntil(
                () =>
                    regions.Items.Count >= 1 &&
                    regions.Items.Count <
                    viewModel.Regions.Count);
            filter.Clear();
            PumpHairWizardUntil(
                () =>
                    regions.Items.Count ==
                    viewModel.Regions.Count);
            regions.ScrollIntoView(
                viewModel.Regions[^1]);
            window.UpdateLayout();
            Assert(
                regions.ItemContainerGenerator
                    .ContainerFromItem(
                        viewModel.Regions[^1]) is
                    ListBoxItem,
                "Region-card scrolling could not realize the final exact HairTint shape.");

            window.Width = 1240;
            window.Height = 820;
            window.UpdateLayout();
            AssertHairWizardWindowContract(
                window,
                expectedWidth: 1240,
                expectedHeight: 820);

            ClickHairWizard(
                window,
                "Next wizard step");
            Assert(
                viewModel.CurrentStepIndex == 2,
                "Regions step could not advance to Colors and Preview.");
            ClickHairWizard(
                window,
                "Create HairTint byte plan");
            PumpHairWizardUntil(
                () =>
                    viewModel.HasCurrentProposal);
            Assert(
                transaction.PreviewCalls == 0,
                "Creating a byte plan launched preview automatically.");
            ClickHairWizard(
                window,
                "Render HairTint preview");
            PumpHairWizardUntil(
                () =>
                    viewModel.IsPreviewCurrent);
            PumpHairWizardUntil(
                () =>
                    FindHairWizardVisual<Image>(
                        window,
                        item =>
                            string.Equals(
                                AutomationProperties
                                    .GetName(item),
                                "Isolated HairTint region thumbnail",
                                StringComparison.Ordinal) &&
                            item.Source is not null)
                    is not null);
            Assert(
                transaction.PreviewCalls == 1 &&
                RequireHairWizardVisual<Image>(
                    window,
                    "Combined HairTint face preview")
                    .Source is not null &&
                RequireHairWizardVisual<Image>(
                    window,
                    "HairTint contact sheet")
                    .Source is not null,
                "Manual preview did not publish the combined face and contact sheet.");
            Assert(
                viewModel.Regions.All(item =>
                    item.HasPreviewArtifacts) &&
                primary.TargetColor ==
                    viewModel.PrimaryColor &&
                accent.TargetColor ==
                    viewModel.AccentColor,
                "Rendered region cards did not retain their isolated thumbnail, role mask, and exact target swatch.");
            Assert(
                RequireHairWizardVisual<TextBlock>(
                    window,
                    FaceGeomHairRegionsWizardViewModel
                        .RuntimeAuthorityLabel)
                    .Text ==
                    FaceGeomHairRegionsWizardViewModel
                        .RuntimeAuthorityLabel,
                "Rendered wizard hid or changed the exact runtime-authority warning.");
            CaptureHairWizardEvidence(
                window,
                labRoot,
                "dual-tone-hair-wizard-preview-rendered-test.png");

            ClickHairWizard(
                window,
                "Next wizard step");
            Assert(
                viewModel.CurrentStepIndex == 3,
                "A current preview could not reach Byte-plan Review.");
            window.UpdateLayout();
            ItemsControl envelopes =
                RequireHairWizardVisual<ItemsControl>(
                    window,
                    "HairTint authorized envelopes");
            Assert(
                viewModel.BytePlanEnvelopes.Length >= 2 &&
                envelopes.Items.Count ==
                    viewModel.BytePlanEnvelopes.Length &&
                viewModel.BytePlanEnvelopes.All(item =>
                    item.ByteEnvelopeText.Contains(
                        "12 bytes",
                        StringComparison.Ordinal) &&
                    item.OldFloatBitsText.Contains(
                        "0x",
                        StringComparison.Ordinal) &&
                    item.NewFloatBitsText.Contains(
                        "0x",
                        StringComparison.Ordinal)) &&
                viewModel.BytePlanProposalSha256.Length == 64 &&
                string.Equals(
                    viewModel.BytePlanExpectedOutputPath,
                    viewModel.OutputPath,
                    StringComparison.Ordinal) &&
                viewModel.BytePlanExpectedOutputSha256.Length == 64 &&
                long.TryParse(
                    viewModel.BytePlanExpectedOutputLength,
                    out long expectedLength) &&
                expectedLength > 0 &&
                string.Equals(
                    viewModel.BytePlanManifestPath,
                    viewModel.ManifestPath,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(
                    viewModel.BytePlanChangedByteOffsetsText),
                "Byte-plan Review did not expose the exact envelopes, IEEE-754 pre/post bits, changed offsets, output length/hash/path, and manifest path.");
            Assert(
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint proposal hash").Text ==
                    viewModel.BytePlanProposalSha256 &&
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint expected output path").Text ==
                    viewModel.BytePlanExpectedOutputPath &&
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint expected output hash").Text ==
                    viewModel.BytePlanExpectedOutputSha256 &&
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint expected output length").Text.Contains(
                        viewModel.BytePlanExpectedOutputLength,
                        StringComparison.Ordinal) &&
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint manifest path").Text ==
                    viewModel.BytePlanManifestPath &&
                RequireHairWizardVisual<TextBlock>(
                    window,
                    "HairTint changed byte offsets").Text ==
                    viewModel.BytePlanChangedByteOffsetsText,
                "Rendered byte-plan review did not bind the exact proposal authority values.");
            CaptureHairWizardEvidence(
                window,
                labRoot,
                "dual-tone-hair-wizard-byte-plan-rendered-test.png");
            ClickHairWizard(
                window,
                "Accept exact HairTint byte plan");
            ClickHairWizard(
                window,
                "Next wizard step");
            Assert(
                viewModel.CurrentStepIndex == 4 &&
                viewModel.CanWrite &&
                RequireHairWizardVisual<Button>(
                    window,
                    "Write and independently verify HairTint FaceGeom")
                    .IsEnabled,
                "Write became reachable without the current preview and explicit byte-plan acceptance gate.");

            ClickHairWizard(
                window,
                "Previous wizard step");
            Assert(
                viewModel.CurrentStepIndex == 3 &&
                !viewModel.IsPreviewCurrent &&
                !viewModel.HasCurrentProposal &&
                !viewModel.CanWrite,
                "Back navigation retained stale preview or write authority.");
            ClickHairWizard(
                window,
                "Cancel HairTint wizard");
            PumpHairWizardUntil(
                () =>
                    !window.IsVisible);
            window = null;

            SynchronizationContext.SetSynchronizationContext(
                priorContext);
            dispatcherContextInstalled = false;
            TestBlankHairWizardPreviewRefusal(
                labRoot,
                intake,
                root);
            TestHairWizardWindowCloseCancels(
                labRoot,
                intake,
                root);
        }
        finally
        {
            window?.Close();
            if (dispatcherContextInstalled)
            {
                SynchronizationContext
                    .SetSynchronizationContext(
                        priorContext);
            }
            if (ownsApplication)
            {
                app?.Shutdown();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static void
        TestBlankHairWizardPreviewRefusal(
            WorkspacePath labRoot,
            ReviewedGameIntake intake,
            string root)
    {
        var transaction =
            new RenderedHairWizardTransaction(
                labRoot)
            {
                ReturnBlankPreview = true
            };
        using FaceGeomHairRegionsWizardViewModel viewModel =
            CreateRenderedHairWizard(
                transaction,
                intake,
                root,
                "blank");
        PrepareRenderedHairWizardPreview(
            viewModel);
        viewModel.RenderPreviewAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Assert(
            !viewModel.IsPreviewCurrent &&
            !viewModel.MoveNext() &&
            viewModel.Diagnostics.Any(item =>
                item.Code ==
                "hair-regions-preview-authority-mismatch"),
            "A declared blank combined-face image was admitted as a current visual preview.");
    }

    private static void
        TestHairWizardWindowCloseCancels(
            WorkspacePath labRoot,
            ReviewedGameIntake intake,
            string root)
    {
        var transaction =
            new RenderedHairWizardTransaction(
                labRoot)
            {
                BlockPreview = true
            };
        var viewModel =
            CreateRenderedHairWizard(
                transaction,
                intake,
                root,
                "cancel");
        PrepareRenderedHairWizardPreview(
            viewModel);
        var window =
            new FaceGeomHairRegionsWizardWindow(
                viewModel)
            {
                Width = 980,
                Height = 680,
                WindowStartupLocation =
                    WindowStartupLocation.Manual
            };
        bool closedBeforePreviewExited =
            false;
        window.Closed += (_, _) =>
            closedBeforePreviewExited =
                !transaction.PreviewExited;
        window.Show();
        window.UpdateLayout();
        ClickHairWizard(
            window,
            "Render HairTint preview");
        PumpHairWizardUntil(
            () =>
                transaction.PreviewCalls == 1);
        window.Close();
        PumpHairWizardUntil(
            () =>
                transaction.PreviewCancellationObserved);
        Assert(
            transaction.PreviewCancellationObserved &&
            window.IsVisible &&
            !transaction.PreviewExited,
            "The rendered wizard closed before its canceled preview phase drained.");
        transaction.ReleaseBlockedPreview();
        PumpHairWizardUntil(
            () =>
                transaction.PreviewExited &&
                !window.IsVisible);
        Assert(
            !closedBeforePreviewExited,
            "The rendered wizard released its source/service lifetime before the active preview phase exited.");
    }

    private static void
        PrepareRenderedHairWizardPreview(
            FaceGeomHairRegionsWizardViewModel viewModel)
    {
        viewModel.AnalyzeAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        FaceGeomHairRegionCardViewModel primary =
            viewModel.Regions[0];
        FaceGeomHairRegionCardViewModel accent =
            viewModel.Regions.First(item =>
                item.SharedShaderGroupId !=
                primary.SharedShaderGroupId);
        primary.Role =
            FaceGeomHairRegionRole.Primary;
        accent.Role =
            FaceGeomHairRegionRole.Accent;
        Assert(
            viewModel.MoveNext(),
            "Rendered wizard fixture could not reach Colors and Preview.");
        viewModel.ProposeAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private static FaceGeomHairRegionsWizardViewModel
        CreateRenderedHairWizard(
            IFaceGeomHairRegionsWizardTransaction transaction,
            ReviewedGameIntake intake,
            string root,
            string suffix = "main") =>
        new(
            transaction,
            null,
            ActorwrightWorkspace.ResolveRoot(),
            intake,
            FaceGeomHairRegionsWizardLaunchContext
                .Standalone)
        {
            SourcePath =
                SyntheticFaceGeomHairRegionsFixture.Load(ActorwrightWorkspace.ResolveRoot().Value).Path,
            OutputPath = Path.Combine(
                root,
                $"synthetic-{suffix}.nif"),
            ManifestPath = Path.Combine(
                root,
                $"synthetic-{suffix}.manifest.json")
        };

    private static void AssertHairWizardWindowContract(
        FaceGeomHairRegionsWizardWindow window,
        double expectedWidth,
        double expectedHeight)
    {
        window.UpdateLayout();
        Assert(
            window.ResizeMode ==
                ResizeMode.CanResize &&
            window.ActualWidth >=
                expectedWidth - 2 &&
            window.ActualHeight >=
                expectedHeight - 2 &&
            window.MinWidth <= 980 &&
            window.MinHeight <= 680,
            "Dual-tone Hair wizard is not resizable at the required viewport.");
        string[] required =
        [
            "Dual-tone Hair / Accent Regions wizard",
            "HairTint wizard steps",
            "Select K-local FaceGeom",
            "Previous wizard step",
            "Next wizard step",
            "Cancel HairTint wizard"
        ];
        foreach (string name in required)
        {
            Assert(
                FindHairWizardVisual<FrameworkElement>(
                    window,
                    item =>
                        string.Equals(
                            AutomationProperties.GetName(
                                item),
                            name,
                            StringComparison.Ordinal)) is
                not null,
                $"Rendered wizard is missing accessible control '{name}'.");
        }
        Assert(
            Enumerable.Range(0, 5)
                .Select(index =>
                    $"HairTint wizard step {index + 1}")
                .All(name =>
                    FindHairWizardVisual<
                        FrameworkElement>(
                        window,
                        item =>
                            string.Equals(
                                AutomationProperties
                                    .GetName(item),
                                name,
                                StringComparison.Ordinal))
                    is not null),
            "Rendered wizard does not expose all five ordered step indicators.");
    }

    private static T RequireHairWizardVisual<T>(
        DependencyObject root,
        string automationName)
        where T : FrameworkElement =>
        FindHairWizardVisual<T>(
            root,
            item =>
                string.Equals(
                    AutomationProperties.GetName(item),
                    automationName,
                    StringComparison.Ordinal)) ??
        throw new InvalidOperationException(
            $"Missing rendered HairTint control '{automationName}'.");

    private static T? FindHairWizardVisual<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        if (root is T rootCandidate &&
            predicate(rootCandidate))
        {
            return rootCandidate;
        }

        int count =
            VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0;
             index < count;
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(
                    root,
                    index);
            if (child is T candidate &&
                predicate(candidate))
            {
                return candidate;
            }
            T? descendant =
                FindHairWizardVisual(
                    child,
                    predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }

    private static void ClickHairWizard(
        DependencyObject root,
        string automationName)
    {
        Button button =
            RequireHairWizardVisual<Button>(
                root,
                automationName);
        button.RaiseEvent(
            new RoutedEventArgs(
                Button.ClickEvent));
        (root as UIElement)?.UpdateLayout();
    }

    private static void RaiseHairWizardKey(
        UIElement target,
        Key key)
    {
        target.RaiseEvent(
            new KeyEventArgs(
                Keyboard.PrimaryDevice,
                Keyboard.PrimaryDevice
                    .ActiveSource,
                0,
                key)
            {
                RoutedEvent =
                    Keyboard.PreviewKeyDownEvent
            });
        target.UpdateLayout();
    }

    private static void PumpHairWizardUntil(
        Func<bool> condition)
    {
        var stopwatch =
            Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >
                TimeSpan.FromSeconds(8))
            {
                throw new TimeoutException(
                    "Timed out waiting for the rendered HairTint wizard.");
            }
            var frame =
                new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher
                .BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                        frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void CaptureHairWizardEvidence(
        FaceGeomHairRegionsWizardWindow window,
        WorkspacePath labRoot,
        string fileName)
    {
        string evidenceRoot = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "hair-regions-wpf-evidence",
            Guid.NewGuid().ToString("N")).Value;
        Directory.CreateDirectory(
            evidenceRoot);
        string path = Path.Combine(
            evidenceRoot,
            fileName);
        window.UpdateLayout();
        DpiScale dpi =
            VisualTreeHelper.GetDpi(
                window);
        int width = Math.Max(
            1,
            (int)Math.Ceiling(
                window.ActualWidth *
                dpi.DpiScaleX));
        int height = Math.Max(
            1,
            (int)Math.Ceiling(
                window.ActualHeight *
                dpi.DpiScaleY));
        var bitmap =
            new RenderTargetBitmap(
                width,
                height,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
        bitmap.Render(
            window);
        var encoder =
            new PngBitmapEncoder();
        encoder.Frames.Add(
            BitmapFrame.Create(
                bitmap));
        using (FileStream stream = new(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            encoder.Save(stream);
            stream.Flush(
                flushToDisk: true);
        }

        string hash = Convert.ToHexString(
            SHA256.HashData(
                File.ReadAllBytes(path)));
        Console.WriteLine(
            $"ARTIFACT {path} SHA256 {hash}");
    }

    private sealed class RenderedHairWizardTransaction :
        IFaceGeomHairRegionsWizardTransaction
    {
        private readonly WorkspacePath labRoot;
        private readonly FaceGeomHairRegionsDocumentCodec
            documents;
        private readonly FaceGeomHairRegionsExactSourceAnalyzer
            analyzer;
        private readonly FaceGeomHairRegionsAnalyzer
            templateFactory;
        private readonly FaceGeomHairRegionsProposer
            proposer;
        private readonly FaceGeomHairRegionsApplyService
            apply;
        private readonly Sha256Hash renderer =
            new(new string('A', 64));
        private readonly Sha256Hash resolvedTextures =
            new(new string('B', 64));

        public RenderedHairWizardTransaction(
            WorkspacePath labRoot)
        {
            this.labRoot = labRoot;
            documents =
                new FaceGeomHairRegionsDocumentCodec(
                    labRoot);
            analyzer =
                new FaceGeomHairRegionsExactSourceAnalyzer(
                    labRoot);
            templateFactory =
                new FaceGeomHairRegionsAnalyzer(
                    labRoot);
            proposer =
                new FaceGeomHairRegionsProposer(
                    labRoot,
                    documents);
            apply =
                new FaceGeomHairRegionsApplyService(
                    labRoot,
                    new BethesdaFaceGeomHairRegionsVerifier(),
                    documents);
        }

        public bool ReturnBlankPreview
        {
            get;
            init;
        }

        public bool BlockPreview
        {
            get;
            init;
        }

        public bool PreviewCancellationObserved
        {
            get;
            private set;
        }

        public bool PreviewExited
        {
            get;
            private set;
        }

        private TaskCompletionSource<bool>
            PreviewExitRelease
        {
            get;
        } = new(
            TaskCreationOptions
                .RunContinuationsAsynchronously);

        public void ReleaseBlockedPreview() =>
            PreviewExitRelease.TrySetResult(
                true);

        public int PreviewCalls
        {
            get;
            private set;
        }

        public async ValueTask<
            FaceGeomHairRegionsWizardAnalysisState>
            AnalyzeAsync(
                WorkspacePath source,
                WorkspacePath output,
                WorkspacePath manifest,
                CancellationToken cancellationToken)
        {
            FaceGeomHairRegionsAnalysis value =
                await analyzer.AnalyzeAsync(
                    source,
                    null,
                    cancellationToken);
            StrictJsonDocumentAuthority<
                FaceGeomHairRegionsAnalysis> authority =
                    documents.BindAnalysis(value);
            return new(
                authority,
                templateFactory
                    .CreateAssignmentTemplate(
                        authority,
                        output,
                        manifest),
                []);
        }

        public async ValueTask<
            FaceGeomHairRegionsWizardProposalState>
            ProposeAsync(
                StrictJsonDocumentAuthority<
                    FaceGeomHairRegionsAnalysis> analysis,
                FaceGeomHairRegionsRequest request,
                CancellationToken cancellationToken)
        {
            StrictJsonDocumentAuthority<
                FaceGeomHairRegionsRequest> bound =
                    documents.BindRequest(
                        request);
            FaceGeomHairRegionsProposalResult proposed =
                await proposer.ProposeAsync(
                    analysis,
                    bound,
                    cancellationToken);
            FaceGeomHairRegionsProposalMaterialization
                materialized =
                    await apply.MaterializeAsync(
                        bound,
                        proposed.Proposal,
                        cancellationToken);
            return new(
                bound,
                proposed.Proposal,
                materialized,
                proposed.Diagnostics);
        }

        public async ValueTask<
            FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake,
                WorkspacePath outputRoot,
                CancellationToken cancellationToken)
        {
            PreviewCalls++;
            if (BlockPreview)
            {
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    PreviewCancellationObserved =
                        true;
                    await PreviewExitRelease.Task;
                    throw;
                }
                finally
                {
                    PreviewExited = true;
                }
            }

            Directory.CreateDirectory(
                outputRoot.Value);
            var artifacts =
                ImmutableArray.CreateBuilder<
                    FaceGeomHairRegionsPreviewArtifact>();
            WorkspacePath combined =
                WritePng(
                    outputRoot,
                    "combined-face.png",
                    Colors.CornflowerBlue);
            artifacts.Add(
                Artifact(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace,
                    null,
                    combined,
                    ReturnBlankPreview
                        ? 0
                        : 64));
            WorkspacePath contact =
                WritePng(
                    outputRoot,
                    "contact-sheet.png",
                    Colors.SlateBlue);
            artifacts.Add(
                Artifact(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet,
                    null,
                    contact,
                    64));
            foreach (
                FaceGeomHairRegionAssignment assignment
                in proposal.ProposalDocument.Value
                    .Assignments)
            {
                string safe =
                    assignment.StructuralId
                        .Replace(
                            ':',
                            '_')
                        .Replace(
                            '/',
                            '_');
                WorkspacePath thumbnail =
                    WritePng(
                        outputRoot,
                        $"thumb-{safe}.png",
                        Colors.Goldenrod);
                artifacts.Add(
                    Artifact(
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionThumbnail,
                        assignment.StructuralId,
                        thumbnail,
                        64));
                WorkspacePath mask =
                    WritePng(
                        outputRoot,
                        $"mask-{safe}.png",
                        Colors.White);
                artifacts.Add(
                    Artifact(
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask,
                        assignment.StructuralId,
                        mask,
                        64));
            }

            FaceGeomHairRegionsPreviewCacheAuthority authority =
                CacheAuthority(
                    proposal,
                    intake);
            return new(
                true,
                new FaceGeomHairRegionsPreviewEvidence(
                    proposal.Materialization
                        .Candidate.Sha256,
                    proposal.ProposalDocument.Sha256,
                    authority.Key.IntakeSha256,
                    renderer,
                    resolvedTextures,
                    new Sha256Hash(
                        new string('C', 64)),
                    ReturnBlankPreview
                        ? 0
                        : 1,
                    ReturnBlankPreview
                        ? 0
                        : 478,
                    ReturnBlankPreview
                        ? 0
                        : 31,
                    RenderAuthority()),
                artifacts.ToImmutable(),
                [],
                VisualAuthority: false,
                RuntimeAuthority: false);
        }

        public ValueTask<FaceGeomHairRegionsApplyResult>
            ApplyAsync(
                FaceGeomHairRegionsWizardProposalState proposal,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "The rendered window test never writes a FaceGeom.");
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
                CacheAuthority(
                    proposal,
                    intake));
        }

        private FaceGeomHairRegionsPreviewCacheAuthority
            CacheAuthority(
                FaceGeomHairRegionsWizardProposalState proposal,
                ReviewedGameIntake intake)
        {
            ReviewedGameIntakeDocumentAuthority intakeAuthority =
                documents.BindReviewedIntake(
                    intake,
                    ActorwrightWorkspace.WorkRoot(
                        labRoot,
                        "hair-window-intake.json"));
            FaceGeomHairRegionsPreviewCacheKey key =
                FaceGeomHairRegionsPreviewCacheKey
                    .Create(
                        proposal.Materialization
                            .Candidate.Sha256,
                        proposal.RequestDocument.Sha256,
                        proposal.ProposalDocument.Sha256,
                        intakeAuthority.Document.Sha256,
                        renderer,
                        intake.AssetIndexFingerprint,
                        resolvedTextures);
            return new(
                key,
                intake.IntakeFingerprint);
        }

        private FaceGeomHairRegionsRenderAuthority
            RenderAuthority() =>
            new(
                new Sha256Hash(new string('D', 64)),
                new Sha256Hash(new string('E', 64)),
                new Sha256Hash(new string('F', 64)),
                renderer,
                new Sha256Hash(new string('1', 64)),
                resolvedTextures,
                new Sha256Hash(new string('2', 64)),
                new Sha256Hash(new string('3', 64)),
                1,
                new Sha256Hash(new string('4', 64)),
                1,
                [],
                []);

        private static WorkspacePath WritePng(
            WorkspacePath root,
            string name,
            Color color)
        {
            string path =
                Path.Combine(
                    root.Value,
                    name);
            const int width = 8;
            const int height = 8;
            const int stride = width * 4;
            byte[] pixels =
                new byte[stride * height];
            for (int offset = 0;
                 offset < pixels.Length;
                 offset += 4)
            {
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
            BitmapSource bitmap =
                BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);
            var encoder =
                new PngBitmapEncoder();
            encoder.Frames.Add(
                BitmapFrame.Create(bitmap));
            using FileStream stream =
                File.Create(path);
            encoder.Save(stream);
            return new WorkspacePath(path);
        }

        private static FaceGeomHairRegionsPreviewArtifact
            Artifact(
                FaceGeomHairRegionsPreviewArtifactKind kind,
                string? structuralId,
                WorkspacePath path,
                long pixels)
        {
            byte[] bytes =
                File.ReadAllBytes(path.Value);
            return new(
                kind,
                structuralId,
                path,
                bytes.LongLength,
                new Sha256Hash(
                    Convert.ToHexString(
                        SHA256.HashData(bytes))),
                pixels);
        }
    }
}
