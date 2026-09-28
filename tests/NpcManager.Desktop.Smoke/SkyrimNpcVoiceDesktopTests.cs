using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimNpcVoiceDesktopTest()
    {
        App? app = null;
        try
        {
            if (System.Windows.Application.Current is null)
            {
                app = new App(() => new WorkspacePath(Environment.CurrentDirectory))
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                app.InitializeComponent();
            }

            SynchronizationContext? prior = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(
                        Dispatcher.CurrentDispatcher));
                PumpUntilComplete(RunSkyrimNpcVoiceDesktopTestAsync());
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prior);
            }
        }
        finally
        {
            app?.Shutdown();
        }
    }

    private static void PumpUntilComplete(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunSkyrimNpcVoiceDesktopTestAsync()
    {
        string root = Environment.CurrentDirectory;
        string sample = Path.Combine(root, "artifacts", "voice-panel-sample.wav");
        string output = Path.Combine(root, "artifacts", "voice-panel-authority");
        int uiThread = Environment.CurrentManagedThreadId;
        await ProveServerSetupAsync(root, output, uiThread);
        var service = new ControlledVoiceService(Dispatcher.CurrentDispatcher);
        using var viewModel = new SkyrimNpcVoiceViewModel(
            service,
            new WorkspacePath(root),
            new WorkspacePath(output),
            service)
        {
            Plugin = "VoiceNpc.esp",
            FormId = "0x00000800",
            EditorId = "VoiceNpc",
            VoicePrefix = "ActorwrightVoiceNpc"
        };
        int resultUpdateThread = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName ==
                    nameof(SkyrimNpcVoiceViewModel.AuthorityPath) &&
                viewModel.AuthorityPath != "—")
                resultUpdateThread = Environment.CurrentManagedThreadId;
        };
        var panel = new SkyrimNpcVoicePanel { DataContext = viewModel };
        panel.Measure(new Size(1000, 760));
        panel.Arrange(new Rect(0, 0, 1000, 760));
        panel.UpdateLayout();
        Assert(panel.FindName("VoiceSampleDropZone") is FrameworkElement
               {
                   AllowDrop: true
               } &&
               panel.FindName("BrowseVoiceSampleButton") is FrameworkElement &&
               panel.FindName("VoiceAuthorityStatus") is FrameworkElement,
            "The voice panel did not first-render its drop, browse, and authority controls.");

        var wavDrop = new DataObject();
        wavDrop.SetData(DataFormats.FileDrop, new[] { sample });
        Task import = panel.HandleDropAsync(wavDrop);
        SkyrimVoiceSampleImportRequest request = await service.WaitForRequestAsync();
        Assert(viewModel.IsBusy && !import.IsCompleted,
            "The WAV drop blocked the caller or failed to expose its active import state.");
        Assert(!service.ExecutionHadUiAccess &&
               service.ExecutionThreadId != uiThread,
            "The shared WAV import service executed on the WPF dispatcher.");
        Assert(request.Sample == new WorkspacePath(sample) &&
               request.Plugin == new PluginName("VoiceNpc.esp") &&
               request.FormId == new FormId(0x800) &&
               request.EditorId == new EditorId("VoiceNpc") &&
               request.VoicePrefix == "ActorwrightVoiceNpc" &&
               request.OutputRoot == new WorkspacePath(output),
            "The WAV drop changed the bound sample, NPC identity, or output root.");

        SkyrimVoiceSampleImportResult accepted = Accepted(request);
        WorkspacePath authorityPath = accepted.AuthorityPath ??
            throw new InvalidOperationException("Fixture authority path is missing.");
        Sha256Hash authoritySha256 = accepted.AuthoritySha256 ??
            throw new InvalidOperationException("Fixture authority hash is missing.");
        service.Complete(accepted);
        await import;
        Assert(!viewModel.IsBusy && viewModel.SamplePath == sample &&
               resultUpdateThread == uiThread &&
               viewModel.AuthorityPath == authorityPath.Value &&
               viewModel.AuthoritySha256 == authoritySha256.Value &&
               viewModel.Status.Contains("Import complete", StringComparison.Ordinal) &&
               viewModel.TemplateCommand.Contains("--npc-plugin 'VoiceNpc.esp'", StringComparison.Ordinal) &&
               viewModel.TemplateCommand.Contains("--form-id 0x00000800", StringComparison.Ordinal) &&
               viewModel.TemplateCommand.Contains("--editor-id 'VoiceNpc'", StringComparison.Ordinal) &&
               viewModel.TemplateCommand.Contains("--voice-prefix 'ActorwrightVoiceNpc'", StringComparison.Ordinal) &&
               viewModel.SynthesisCommand.Contains(authorityPath.Value, StringComparison.Ordinal) &&
               viewModel.SynthesisCommand.Contains(authoritySha256.Value, StringComparison.Ordinal) &&
               viewModel.FollowUpRequirements.Contains("profile", StringComparison.OrdinalIgnoreCase) &&
               viewModel.FollowUpRequirements.Contains("manifest SHA-256", StringComparison.OrdinalIgnoreCase),
            "The accepted drop did not retain authority evidence and truthful follow-up command inputs.");

        await ProvePowerShellLiteralCommandsAsync(root);

        var textDrop = new DataObject();
        textDrop.SetData(DataFormats.FileDrop, new[] { Path.ChangeExtension(sample, ".txt") });
        await panel.HandleDropAsync(textDrop);
        Assert(service.RequestCount == 1 &&
               viewModel.AuthorityPath == "—" &&
               viewModel.SynthesisCommand.StartsWith(
                   "Import a valid WAV",
                   StringComparison.Ordinal) &&
               viewModel.Diagnostics.Any(item =>
                   item.Contains("voice-sample-invalid", StringComparison.Ordinal) &&
                   item.Contains(".wav", StringComparison.OrdinalIgnoreCase)),
            "The panel forwarded a non-WAV drop or hid its validation diagnostic.");

        OpenFileDialog browse = SkyrimNpcVoicePanel.CreateBrowseDialog();
        Assert(browse.Filter == "WAV audio (*.wav)|*.wav" &&
               browse.CheckFileExists && !browse.Multiselect,
            "Browse does not constrain selection to one existing WAV file.");

        var cancellingService = new ControlledVoiceService();
        var cancelling = new SkyrimNpcVoiceViewModel(
            cancellingService,
            new WorkspacePath(root),
            new WorkspacePath(output + "-cancelled"),
            cancellingService)
        {
            Plugin = "VoiceNpc.esp",
            FormId = "0x00000800",
            VoicePrefix = "ActorwrightVoiceNpc"
        };
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(root),
            new WorkspacePath(@"F:\ExampleGame"));
        WorkspaceShellViewModel shell =
            DesktopExternalSmpCompositionTests.CreateShellWithVoice(
            policy,
            new WorkspacePath(root),
            cancelling);
        Task pending = cancelling.ImportSampleAsync(sample);
        _ = await cancellingService.WaitForRequestAsync();
        Assert(ReferenceEquals(shell.Voice, cancelling) &&
               shell.HasActiveOperation,
            "The shell did not include active voice import work in its close gate.");
        shell.Dispose();
        await pending;
        Assert(cancellingService.Disposed && cancellingService.CancellationObserved &&
               !cancelling.IsBusy &&
               cancelling.Status.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
            "Disposal did not cancel an active import and release its owned service lifetime.");
    }

    private static async Task ProveServerSetupAsync(
        string root,
        string sampleOutput,
        int uiThread)
    {
        string serverFolder = Path.Combine(
            root, "artifacts", "voice-panel-xtts-server");
        var saved = new SkyrimVoiceServerSetup(
            "http://127.0.0.1:8020",
            "windows",
            serverFolder,
            Path.Combine(serverFolder, "output"));
        SkyrimVoiceServiceInventory inventory = ServerInventory(saved);
        var service = new ControlledVoiceService(Dispatcher.CurrentDispatcher)
        {
            LoadSetupResult = new SkyrimVoiceServerSetupResult(
                true, saved, null, ImmutableArray<Diagnostic>.Empty),
            CheckSetupResultFactory = setup => new SkyrimVoiceServerSetupResult(
                true, setup, inventory, ImmutableArray<Diagnostic>.Empty),
            SaveSetupResultFactory = setup => new SkyrimVoiceServerSetupResult(
                true, setup, inventory, ImmutableArray<Diagnostic>.Empty)
        };
        using var viewModel = new SkyrimNpcVoiceViewModel(
            service,
            new WorkspacePath(root),
            new WorkspacePath(sampleOutput));
        var panel = new SkyrimNpcVoicePanel { DataContext = viewModel };

        await panel.HandleLoadedAsync();
        await panel.HandleLoadedAsync();
        Assert(service.SetupLoadCount == 1 &&
               !service.SetupExecutionHadUiAccess &&
               service.SetupExecutionThreadId != uiThread &&
               viewModel.ServerEndpoint == saved.Endpoint &&
               viewModel.ServerPlatform == saved.Platform &&
               viewModel.ServerFolder == saved.ServerFolder &&
               viewModel.ServerOutputFolder == saved.OutputFolder &&
               viewModel.OutputRoot == sampleOutput,
            "The Voice panel did not load the saved server setup once, off the dispatcher, without changing the sample output root.");
        Assert(panel.FindName("ServerEndpointTextBox") is FrameworkElement &&
               panel.FindName("ServerPlatformComboBox") is FrameworkElement &&
               panel.FindName("ServerFolderTextBox") is FrameworkElement &&
               panel.FindName("ServerOutputFolderTextBox") is FrameworkElement &&
               panel.FindName("CheckServerSetupButton") is FrameworkElement &&
               panel.FindName("SaveServerSetupButton") is FrameworkElement,
            "The Voice panel did not expose the editable XTTS setup controls.");

        await viewModel.CheckServerSetupAsync();
        Assert(service.SetupCheckCount == 1 &&
               service.SetupSaveCount == 0 &&
               service.RequestCount == 0 &&
               service.LastCheckedSetup == saved &&
               viewModel.ServerSetupStatus.Contains(saved.Endpoint, StringComparison.Ordinal) &&
               viewModel.ServerSetupStatus.Contains(saved.ServerFolder, StringComparison.Ordinal) &&
               viewModel.ServerSetupStatus.Contains("declared", StringComparison.OrdinalIgnoreCase) &&
               viewModel.OutputRoot == sampleOutput,
            "Check did not distinguish the queried endpoint from the user-declared working folder or it invoked a mutating voice operation.");

        viewModel.ServerFolder = serverFolder + "-moved";
        Assert(viewModel.ServerSetupStatus.Contains(
                   "check again", StringComparison.OrdinalIgnoreCase),
            "Editing a server field retained stale successful-check status.");
        SkyrimVoiceServerSetup changed = saved with
        {
            ServerFolder = viewModel.ServerFolder
        };
        await viewModel.SaveServerSetupAsync();
        Assert(service.SetupSaveCount == 1 &&
               service.LastSavedSetup == changed &&
               viewModel.ServerSetupStatus.Contains("saved", StringComparison.OrdinalIgnoreCase) &&
               viewModel.OutputRoot == sampleOutput,
            "Check and save did not persist the currently edited setup or changed the sample output root.");

        viewModel.ServerPlatform = "wsl";
        Assert(!viewModel.AreServerFoldersEnabled &&
               viewModel.ServerFolder.Length == 0 &&
               viewModel.ServerOutputFolder.Length == 0 &&
               viewModel.ServerSetupStatus.Contains(
                   "check again", StringComparison.OrdinalIgnoreCase),
            "WSL mode retained configurable Windows folder claims.");

        var firstUseService = new ControlledVoiceService(Dispatcher.CurrentDispatcher)
        {
            LoadSetupResult = new SkyrimVoiceServerSetupResult(
                false,
                null,
                null,
                [new Diagnostic(
                    SkyrimNpcVoiceDiagnosticCodes.ServerSetupUnavailable,
                    DiagnosticSeverity.Info,
                    "No saved server setup exists.")])
        };
        using var firstUse = new SkyrimNpcVoiceViewModel(
            firstUseService,
            new WorkspacePath(root),
            new WorkspacePath(sampleOutput));
        await firstUse.InitializeServerSetupAsync();
        Assert(firstUse.ServerEndpoint == "http://127.0.0.1:8020" &&
               firstUse.ServerPlatform == "windows" &&
               firstUse.ServerFolder.Length == 0 &&
               firstUse.ServerOutputFolder.Length == 0 &&
               firstUse.ServerSetupStatus.Contains("enter", StringComparison.OrdinalIgnoreCase),
            "First use did not retain the loopback default with explicit folder guidance.");
    }

    private static SkyrimVoiceServiceInventory ServerInventory(
        SkyrimVoiceServerSetup setup)
    {
        var descriptor = new SkyrimVoiceServiceDescriptor(
            setup.Endpoint,
            "config",
            true,
            "XTTS API Server",
            "1.0",
            ["model"],
            ["en"],
            1,
            "speakers",
            "output",
            "models",
            "{}",
            "safe",
            $"Resolved against user-declared working folder '{setup.ServerFolder}'.",
            new string('D', 64),
            ImmutableArray<Diagnostic>.Empty)
        {
            Platform = setup.Platform
        };
        return new SkyrimVoiceServiceInventory(
            SkyrimNpcVoiceSchemas.Services,
            setup.Endpoint,
            [descriptor],
            ImmutableArray<Diagnostic>.Empty,
            "2026-09-07T00:00:00Z");
    }

    private static async Task ProvePowerShellLiteralCommandsAsync(string root)
    {
        string fixtureRoot = Path.Combine(
            root,
            "artifacts",
            "$voice`tick's");
        string sample = Path.Combine(fixtureRoot, "sam'ple.wav");
        string output = Path.Combine(fixtureRoot, "$output`tick's");
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllBytes(sample, [1]);
        try
        {
            var service = new ControlledVoiceService(Dispatcher.CurrentDispatcher)
            {
                ImmediateResultFactory = Accepted
            };
            using var viewModel = new SkyrimNpcVoiceViewModel(
                service,
                new WorkspacePath(root),
                new WorkspacePath(output),
                service)
            {
                Plugin = "Voice$Npc.esp",
                FormId = "0x00000800",
                EditorId = "VoiceNpc",
                VoicePrefix = "ActorwrightVoiceNpc"
            };

            await viewModel.ImportSampleAsync(sample);
            string expectedSample =
                $"--sample '{root}\\artifacts\\$voice`tick''s\\sam''ple.wav'";
            string expectedOutput =
                $"--output '{root}\\artifacts\\$voice`tick''s\\$output`tick''s'";
            string expectedAuthority =
                $"--sample-authority '{root}\\artifacts\\$voice`tick''s\\$output`tick''s\\voice-sample.json'";
            Assert(File.Exists(sample) &&
                   viewModel.ImportCommandLine.Contains(
                       expectedSample,
                       StringComparison.Ordinal) &&
                   viewModel.ImportCommandLine.Contains(
                       "--plugin 'Voice$Npc.esp'",
                       StringComparison.Ordinal) &&
                   viewModel.ImportCommandLine.Contains(
                       expectedOutput,
                       StringComparison.Ordinal) &&
                   viewModel.SynthesisCommand.Contains(
                       expectedAuthority,
                       StringComparison.Ordinal),
                "PowerShell commands did not preserve valid dollar, backtick, and apostrophe path content literally.");

            service.ImmediateResultFactory = _ => RefusedImport();
            await viewModel.ImportSampleAsync(sample);
            Assert(viewModel.AuthorityPath == "—" &&
                   viewModel.AuthoritySha256 == "—" &&
                   viewModel.SynthesisCommand.StartsWith(
                       "Import a valid WAV",
                       StringComparison.Ordinal) &&
                   viewModel.Status.Contains(
                       SkyrimNpcVoiceDiagnosticCodes.OutputExists,
                       StringComparison.Ordinal),
                "A same-path refused retry retained stale authority or commands.");

            service.ImmediateResultFactory = Accepted;
            await viewModel.ImportSampleAsync(sample);
            Assert(viewModel.Status.Contains(
                       "Import complete",
                       StringComparison.Ordinal),
                "The retry fixture did not restore a successful result.");
            viewModel.Plugin = "ChangedVoiceNpc.esp";
            Assert(viewModel.AuthorityPath == "—" &&
                   !viewModel.Status.Contains(
                       "Import complete",
                       StringComparison.Ordinal) &&
                   viewModel.Status.StartsWith(
                       "Choose an NPC identity",
                       StringComparison.Ordinal),
                "An identity edit retained the stale successful import status.");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static SkyrimVoiceSampleImportResult RefusedImport() => new(
        false,
        null,
        null,
        null,
        [new Diagnostic(
            SkyrimNpcVoiceDiagnosticCodes.OutputExists,
            DiagnosticSeverity.Error,
            "The voice output already exists.")]);

    private static SkyrimVoiceSampleImportResult Accepted(
        SkyrimVoiceSampleImportRequest request)
    {
        var originalHash = new Sha256Hash(new string('B', 64));
        var normalizedHash = new Sha256Hash(new string('C', 64));
        var authorityHash = new Sha256Hash(new string('A', 64));
        var audio = new SkyrimVoiceSampleAudio(
            22050, 1, 16, "pcm", 66150, 3, -1, -12);
        string normalized = Path.Combine(
            request.OutputRoot.Value,
            "ref-cccccccccccccccc.wav");
        var authority = new SkyrimVoiceSampleAuthority(
            SkyrimNpcVoiceSchemas.Sample,
            request.Plugin,
            request.FormId,
            request.EditorId,
            request.VoicePrefix,
            request.Sample.Value,
            originalHash,
            audio,
            normalized,
            normalizedHash,
            audio,
            "2026-09-07T00:00:00Z");
        return new SkyrimVoiceSampleImportResult(
            true,
            authority,
            new WorkspacePath(Path.Combine(
                request.OutputRoot.Value,
                "voice-sample.json")),
            authorityHash,
            ImmutableArray<Diagnostic>.Empty);
    }

    private sealed class ControlledVoiceService :
        ISkyrimNpcVoiceService,
        IDisposable
    {
        private readonly Dispatcher uiDispatcher;
        private TaskCompletionSource<SkyrimVoiceSampleImportRequest> requested =
            NewCompletion<SkyrimVoiceSampleImportRequest>();
        private TaskCompletionSource<SkyrimVoiceSampleImportResult> result =
            NewCompletion<SkyrimVoiceSampleImportResult>();

        public int RequestCount { get; private set; }
        public int ExecutionThreadId { get; private set; }
        public bool ExecutionHadUiAccess { get; private set; }
        public int SetupLoadCount { get; private set; }
        public int SetupCheckCount { get; private set; }
        public int SetupSaveCount { get; private set; }
        public int SetupExecutionThreadId { get; private set; }
        public bool SetupExecutionHadUiAccess { get; private set; }
        public SkyrimVoiceServerSetup? LastCheckedSetup { get; private set; }
        public SkyrimVoiceServerSetup? LastSavedSetup { get; private set; }
        public bool CancellationObserved { get; private set; }
        public bool Disposed { get; private set; }
        public SkyrimVoiceServerSetupResult LoadSetupResult { get; set; } =
            new(false, null, null, ImmutableArray<Diagnostic>.Empty);
        public Func<SkyrimVoiceServerSetup, SkyrimVoiceServerSetupResult>?
            CheckSetupResultFactory { get; set; }
        public Func<SkyrimVoiceServerSetup, SkyrimVoiceServerSetupResult>?
            SaveSetupResultFactory { get; set; }
        public Func<SkyrimVoiceSampleImportRequest,
            SkyrimVoiceSampleImportResult>? ImmediateResultFactory
        {
            get;
            set;
        }

        public ControlledVoiceService(Dispatcher? uiDispatcher = null)
        {
            this.uiDispatcher = uiDispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public ValueTask<SkyrimVoiceServerSetupResult> LoadServerSetupAsync(
            CancellationToken cancellationToken)
        {
            RecordSetupExecution();
            SetupLoadCount++;
            return ValueTask.FromResult(LoadSetupResult);
        }

        public ValueTask<SkyrimVoiceServerSetupResult> CheckServerSetupAsync(
            SkyrimVoiceServerSetup setup,
            CancellationToken cancellationToken)
        {
            RecordSetupExecution();
            SetupCheckCount++;
            LastCheckedSetup = setup;
            return ValueTask.FromResult(
                CheckSetupResultFactory?.Invoke(setup) ?? LoadSetupResult);
        }

        public ValueTask<SkyrimVoiceServerSetupResult> SaveServerSetupAsync(
            SkyrimVoiceServerSetup setup,
            CancellationToken cancellationToken)
        {
            RecordSetupExecution();
            SetupSaveCount++;
            LastSavedSetup = setup;
            return ValueTask.FromResult(
                SaveSetupResultFactory?.Invoke(setup) ?? LoadSetupResult);
        }

        public ValueTask<SkyrimVoiceServiceInventory> DiscoverAsync(
            string? endpointOverride,
            int timeoutSeconds,
            WorkspacePath? outputPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask<SkyrimVoiceSampleImportResult> ImportSampleAsync(
            SkyrimVoiceSampleImportRequest request,
            CancellationToken cancellationToken)
        {
            ExecutionThreadId = Environment.CurrentManagedThreadId;
            ExecutionHadUiAccess = uiDispatcher.CheckAccess();
            RequestCount++;
            requested.TrySetResult(request);
            if (ImmediateResultFactory is { } immediate)
                return immediate(request);
            try
            {
                return await result.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public ValueTask<SkyrimVoiceSynthesisResult> SynthesizeAsync(
            SkyrimVoiceSynthesisRequest request,
            IProgress<string>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SkyrimVoiceSampleImportRequest> WaitForRequestAsync() =>
            requested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Complete(SkyrimVoiceSampleImportResult value) =>
            result.TrySetResult(value);

        public void Dispose() => Disposed = true;

        private void RecordSetupExecution()
        {
            SetupExecutionThreadId = Environment.CurrentManagedThreadId;
            SetupExecutionHadUiAccess = uiDispatcher.CheckAccess();
        }

        private static TaskCompletionSource<T> NewCompletion<T>() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
