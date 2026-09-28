using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static readonly WorkspacePath MainWorkspaceLabRoot =
        new("K:\\ExampleWorkspace");

    private static void RunSkyrimMainWorkspaceResilienceTest()
    {
        RunSkyrimMainWorkspaceResilienceTestAsync().GetAwaiter().GetResult();
        RunSkyrimMainWorkspaceCatalogContextTest(asynchronousHash: false);
        RunSkyrimMainWorkspaceCatalogContextTest(asynchronousHash: true);
    }

    private static void RunSkyrimMainWorkspaceCatalogContextTest(bool asynchronousHash)
    {
        WorkspacePath labRoot = new(
            Path.GetFullPath(Directory.GetCurrentDirectory()));
        string root = Path.Combine(
            labRoot.Value,
            "artifacts",
            "test-work",
            $"main-workspace-context-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SynchronizationContext? previous = SynchronizationContext.Current;
        var ownerContext = new MainWorkspaceSentinelContext();
        SynchronizationContext.SetSynchronizationContext(ownerContext);
        try
        {
            PluginName plugin = new("Context.esp");
            string pluginPath = Path.Combine(root, plugin.Value);
            File.WriteAllBytes(pluginPath, new byte[asynchronousHash ? 8 * 1024 * 1024 : 0]);
            WorkspacePath source = new(pluginPath);
            Sha256Hash sourceHash = Hash(source);
            var reader = new ContextRecordingMainWorkspacePluginReader(
                new SkyrimMainWorkspacePluginReadResult(
                    plugin,
                    [],
                    [
                        new SkyrimMainWorkspacePluginRecord(
                            plugin,
                            plugin,
                            new FormId(0x800),
                            "NPC_",
                            "ContextNpc",
                            "Context NPC",
                            false,
                            null,
                            [],
                            new Sha256Hash(new string('A', 64)))
                    ]));
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "load-order.txt")),
                new WorkspacePath(Path.Combine(root, "future-output")),
                new Sha256Hash(new string('B', 64)),
                [
                    new PluginClosureReviewEntry(
                        plugin,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        source,
                        sourceHash,
                        [])
                ],
                [],
                [],
                [],
                0,
                new Sha256Hash(new string('C', 64)),
                new Sha256Hash(new string('D', 64)),
                false);
            var catalog = asynchronousHash
                ? new SkyrimMainWorkspaceCatalogService(reader)
                : new SkyrimMainWorkspaceCatalogService(reader, (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(Hash(new WorkspacePath(path)));
                });
            using var viewModel = new SkyrimMainWorkspaceViewModel(
                catalog,
                new FakeMainWorkspaceSettingsService(),
                new FakeMainWorkspaceSessionService(),
                new FakeMainWorkspacePreviewService(),
                new FakeMainWorkspacePresetService(),
                labRoot);
            SynchronizationContext? commitContext = null;
            viewModel.Rows.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    commitContext = SynchronizationContext.Current;
            };

            Task load = viewModel.LoadAsync(intake);
            if (asynchronousHash)
                Assert(!load.IsCompleted,
                    "The catalog context fixture did not cross its real asynchronous file boundary.");
            ownerContext.DrainUntil(load, TimeSpan.FromSeconds(30));
            load.GetAwaiter().GetResult();

            Assert(ReferenceEquals(commitContext, ownerContext),
                "The ViewModel collection commit did not resume on its owner context.");
            Assert(reader.ObservedContext is null,
                $"Plugin parsing retained the UI context (asynchronous hash: {asynchronousHash}).");
            Assert(viewModel.Rows.Count == 1,
                "The catalog context fixture did not commit its parsed NPC row.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunSkyrimMainWorkspaceResilienceTestAsync()
    {
        WorkspacePath labRoot = new(
            Path.GetFullPath(Directory.GetCurrentDirectory()));
        string root = Path.Combine(
            labRoot.Value,
            "artifacts",
            "test-work",
            $"main-workspace-resilience-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PluginName owner = new("Actors.esp");
            SkyrimMainWorkspaceIdentity identity = new(
                owner,
                owner,
                new FormId(0x800),
                "NPC_");
            Sha256Hash intakeHash = new(new string('1', 64));
            var snapshot = new SkyrimMainWorkspaceSnapshot(
                intakeHash,
                [
                    Record(
                        identity,
                        SkyrimMainWorkspaceRecordKind.Npc,
                        "ResilientNpc",
                        "resilient npc",
                        NpcSex.Female,
                        [NpcCategory.Unique])
                ],
                []);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "load-order.txt")),
                new WorkspacePath(Path.Combine(root, "future-output")),
                new Sha256Hash(new string('2', 64)),
                [
                    new PluginClosureReviewEntry(
                        owner,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(Path.Combine(root, owner.Value)),
                        new Sha256Hash(new string('3', 64)),
                        [])
                ],
                [],
                [],
                [],
                0,
                new Sha256Hash(new string('4', 64)),
                intakeHash,
                false);
            var catalog = new FakeMainWorkspaceCatalogService(snapshot);
            var preview = new FakeMainWorkspacePreviewService();
            var reportPath = new WorkspacePath(
                Path.Combine(root, "operation-failure.json"));
            var captured = new List<Exception>();
            var previewCaptured = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? expectedPreviewFailure = null;
            var viewModel = new SkyrimMainWorkspaceViewModel(
                catalog,
                new FakeMainWorkspaceSettingsService(),
                new FakeMainWorkspaceSessionService(),
                preview,
                new FakeMainWorkspacePresetService(),
                labRoot,
                operationFailureSink: exception =>
                {
                    captured.Add(exception);
                    if (ReferenceEquals(exception, expectedPreviewFailure))
                        previewCaptured.TrySetResult();
                    return reportPath;
                });

            await viewModel.LoadAsync(intake);
            SkyrimMainWorkspaceSnapshot accepted = viewModel.Snapshot ??
                throw new InvalidOperationException(
                    "The resilience fixture did not accept its baseline snapshot.");
            var catalogFailure = new InvalidOperationException(
                "catalog failure fixture",
                new IOException("catalog inner fixture"));
            catalog.NextException = catalogFailure;

            await viewModel.LoadAsync(intake);

            Assert(
                ReferenceEquals(captured.Single(), catalogFailure),
                "The catalog failure sink did not receive the original exception.");
            Assert(
                ReferenceEquals(viewModel.Snapshot, accepted) &&
                viewModel.Rows.Count == 1,
                "A failed catalog reload replaced the prior accepted snapshot.");
            Assert(
                viewModel.Diagnostics.Any(item =>
                    item.Contains(reportPath.Value, StringComparison.Ordinal)) &&
                viewModel.Diagnostics.All(item =>
                    !item.Contains(" at ", StringComparison.Ordinal)),
                "The catalog failure diagnostic omitted the report path or dumped a stack.");

            viewModel.SelectedRow = viewModel.Rows.Single();
            WorkspacePath manifestPath = Write(root, "preview-manifest.json", "{}");
            viewModel.ConfigurePreview(
                manifestPath,
                Hash(manifestPath),
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "preview-scene.json")),
                new WorkspacePath(Path.Combine(root, "preview.png")));
            expectedPreviewFailure = new InvalidOperationException(
                "preview failure fixture",
                new IOException("preview inner fixture"));
            preview.NextException = expectedPreviewFailure;

            viewModel.RenderPreviewCommand.Execute(null);
            await previewCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert(
                captured.Count == 2 &&
                ReferenceEquals(captured[1], expectedPreviewFailure),
                "The preview command sink did not receive the original exception.");
            Assert(
                ReferenceEquals(viewModel.Snapshot, accepted) &&
                viewModel.Diagnostics[^1].Contains(
                    reportPath.Value,
                    StringComparison.Ordinal) &&
                !viewModel.Diagnostics[^1].Contains(
                    nameof(RunSkyrimMainWorkspaceResilienceTestAsync),
                    StringComparison.Ordinal),
                "The preview failure did not retain state and present only a concise report diagnostic.");

            int navigationCount = 0;
            viewModel.NavigationRequested += (_, _) => navigationCount++;
            ICommand staleCommand = viewModel.EditFaceCommand;
            Assert(staleCommand.CanExecute(null),
                "The stale-command fixture did not begin enabled.");
            viewModel.SelectedRow = null;
            Assert(!staleCommand.CanExecute(null),
                "The stale-command fixture did not become disabled.");

            staleCommand.Execute(null);

            Assert(navigationCount == 0,
                "A stale workbench command invoked its action.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RunSkyrimMainWorkspaceViewModelTest() =>
        RunSkyrimMainWorkspaceViewModelTestAsync().GetAwaiter().GetResult();

    private static async Task RunSkyrimMainWorkspaceViewModelTestAsync()
    {
        WorkspacePath labRoot = MainWorkspaceLabRoot;
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"main-workspace-viewmodel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PluginName owner = new("Actors.esp");
            PluginName winner = new("ActorsPatch.esp");
            SkyrimMainWorkspaceIdentity femaleIdentity = new(
                owner, winner, new FormId(0x800), "NPC_");
            SkyrimMainWorkspaceIdentity maleIdentity = new(
                owner, owner, new FormId(0x801), "NPC_");
            SkyrimMainWorkspaceIdentity deletedIdentity = new(
                owner, winner, new FormId(0x802), "NPC_");
            SkyrimMainWorkspaceIdentity listIdentity = new(
                owner, winner, new FormId(0x900), "LVLN");
            SkyrimMainWorkspaceIdentity emptyListIdentity = new(
                owner, owner, new FormId(0x901), "LVLN");
            ImmutableArray<SkyrimMainWorkspaceRecord> records =
            [
                Record(
                    femaleIdentity,
                    SkyrimMainWorkspaceRecordKind.Npc,
                    "Gate2Female",
                    "female",
                    NpcSex.Female,
                    [NpcCategory.Unique]),
                Record(
                    maleIdentity,
                    SkyrimMainWorkspaceRecordKind.Npc,
                    "Gate2Male",
                    "male",
                    NpcSex.Male,
                    [NpcCategory.Generic]),
                Record(
                    listIdentity,
                    SkyrimMainWorkspaceRecordKind.LeveledNpc,
                    "Gate2List",
                    "list",
                    null,
                    [],
                    entries: [femaleIdentity, maleIdentity]),
                Record(
                    deletedIdentity,
                    SkyrimMainWorkspaceRecordKind.Npc,
                    "Gate2Deleted",
                    "deleted",
                    NpcSex.Female,
                    [NpcCategory.Unused],
                    deleted: true),
                Record(
                    emptyListIdentity,
                    SkyrimMainWorkspaceRecordKind.LeveledNpc,
                    "Gate2Empty",
                    "empty",
                    null,
                    [],
                    empty: true)
            ];
            Sha256Hash intakeHash = new(new string('1', 64));
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "load-order.txt")),
                new WorkspacePath(Path.Combine(root, "future-output")),
                new Sha256Hash(new string('2', 64)),
                [
                    new PluginClosureReviewEntry(
                        owner,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(Path.Combine(root, owner.Value)),
                        new Sha256Hash(new string('3', 64)),
                        []),
                    new PluginClosureReviewEntry(
                        winner,
                        1,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(Path.Combine(root, winner.Value)),
                        new Sha256Hash(new string('4', 64)),
                        [owner])
                ],
                [],
                [],
                [],
                0,
                new Sha256Hash(new string('5', 64)),
                intakeHash,
                false);
            var previewService = new FakeMainWorkspacePreviewService();
            var viewModel = new SkyrimMainWorkspaceViewModel(
                new FakeMainWorkspaceCatalogService(
                    new SkyrimMainWorkspaceSnapshot(intakeHash, records, [])),
                new FakeMainWorkspaceSettingsService(),
                new FakeMainWorkspaceSessionService(),
                previewService,
                new FakeMainWorkspacePresetService(),
                labRoot);

            await viewModel.LoadAsync(intake);
            Assert(
                viewModel.VisibleRows.Count == 5,
                "Accepted intake did not populate the workbench.");
            viewModel.Search = "female";
            Assert(
                viewModel.VisibleRows.Select(row => row.EditorId)
                    .SequenceEqual(["Gate2Female"]),
                "Search did not update the visible collection.");
            viewModel.Search = string.Empty;
            SkyrimMainWorkspaceRowViewModel first = viewModel.VisibleRows[0];
            SkyrimMainWorkspaceRowViewModel third = viewModel.VisibleRows[2];
            viewModel.SelectVisibleRange(first, third);
            Assert(
                viewModel.SelectedRows.Count == 3,
                "Range selection did not retain exact visible order.");
            viewModel.MarkChangedCommand.Execute(null);
            viewModel.MarkDeleteCommand.Execute(null);
            viewModel.RestoreCommand.Execute(null);
            Assert(
                viewModel.SelectedRows.All(row =>
                    row.IsChanged && !row.IsDeletePending),
                "Draft/delete/restore projection diverged.");

            SkyrimMainWorkspaceRowViewModel female =
                viewModel.Rows.Single(row => row.Identity == femaleIdentity);
            SkyrimMainWorkspaceRoute? navigation = null;
            viewModel.NavigationRequested += (_, request) =>
                navigation = request.Route;
            viewModel.SelectedRow = female;
            viewModel.EditFaceCommand.Execute(null);
            Assert(
                navigation == SkyrimMainWorkspaceRoute.EditFace,
                "Face route was not raised for the exact selected actor.");

            WorkspacePath manifestPath =
                Write(root, "preview-manifest.json", "{}");
            WorkspacePath scenePath =
                new(Path.Combine(root, "preview-scene.json"));
            WorkspacePath imagePath =
                new(Path.Combine(root, "preview.png"));
            Sha256Hash expectedPngHash = new(new string('A', 64));
            previewService.NextResult = new SkyrimMainWorkspacePreviewResult(
                true,
                female.Source,
                null,
                scenePath,
                new Sha256Hash(new string('B', 64)),
                imagePath,
                expectedPngHash,
                false,
                []);
            viewModel.ConfigurePreview(
                manifestPath,
                Hash(manifestPath),
                new WorkspacePath(root),
                scenePath,
                imagePath);
            await viewModel.RenderPreviewAsync();
            Assert(
                viewModel.PreviewImageSha256 == expectedPngHash.Value,
                "Accepted preview hash was not retained.");

            SkyrimMainWorkspaceRowViewModel deleted =
                viewModel.Rows.Single(row => row.Identity == deletedIdentity);
            viewModel.SelectedRow = deleted;
            Assert(
                !viewModel.EditFaceCommand.CanExecute(null) &&
                !viewModel.EditNpcCommand.CanExecute(null),
                "Source-deleted NPC editor commands remained enabled.");

            SkyrimMainWorkspaceRowViewModel empty =
                viewModel.Rows.Single(row => row.Identity == emptyListIdentity);
            viewModel.SelectedRow = empty;
            Assert(
                !viewModel.RerollCommand.CanExecute(null) &&
                viewModel.Diagnostics.Any(item =>
                    item.Contains("empty", StringComparison.OrdinalIgnoreCase)),
                "Empty LVLN reroll was not visibly refused.");
            viewModel.ReplaceSelection(
                viewModel.Rows
                    .Where(row => !row.Source.IsSourceDeleted)
                    .Select(row => row.Identity)
                    .ToImmutableArray());
            viewModel.ResetCommand.Execute(null);

            WorkspacePath artifactPath =
                Write(root, "face-plugin.esp", "verified-output");
            var staleHandoff = new SkyrimMainWorkspaceArtifactHandoff(
                femaleIdentity,
                "face-plugin",
                artifactPath,
                new Sha256Hash(new string('C', 64)),
                null,
                null,
                false);
            Assert(
                !await viewModel.AcceptArtifactHandoffAsync(staleHandoff),
                "A stale child artifact handoff was accepted.");

            var validHandoff = staleHandoff with
            {
                Sha256 = Hash(artifactPath)
            };
            Assert(
                await viewModel.AcceptArtifactHandoffAsync(validHandoff),
                "A hash-valid child artifact handoff was refused.");
            Assert(
                viewModel.Rows.Single(row => row.Identity == femaleIdentity)
                    .IsChanged &&
                viewModel.Rows.Where(row => row.Identity != femaleIdentity)
                    .All(row => !row.IsChanged),
                "A valid child handoff did not mark only its matching row changed.");
            viewModel.SelectedRow = female;
            Assert(
                !viewModel.SaveRaceMenuPresetCommand.CanExecute(null) &&
                viewModel.SaveRaceMenuAvailabilityText.Contains(
                    "No complete RaceMenu carrier",
                    StringComparison.Ordinal),
                "NPC_ bytes incorrectly granted RaceMenu save authority.");
            WorkspacePath jslotPath =
                Write(root, "complete-carrier.jslot", "{}");
            Assert(
                await viewModel.AcceptArtifactHandoffAsync(
                    validHandoff with
                    {
                        Kind = "appearance-plugin-jslot",
                        ProposalPath = jslotPath,
                        ProposalSha256 = Hash(jslotPath)
                    }),
                "A verified complete RaceMenu carrier was refused.");
            viewModel.ConfigurePresetDestination(
                new WorkspacePath(Path.Combine(root, "fresh-export.jslot")));
            Assert(
                viewModel.SaveRaceMenuPresetCommand.CanExecute(null),
                "A verified complete RaceMenu carrier did not enable fresh export.");

            viewModel.ConfigureSessionDestination(
                new WorkspacePath(Path.Combine(root, "session.npc-workspace.json")));
            await viewModel.SaveSessionAsync();
            string? acceptedPreview = viewModel.PreviewImageSha256;
            Sha256Hash? acceptedSession = viewModel.SessionSha256;
            Assert(
                acceptedSession is not null &&
                viewModel.Status.Contains(
                    "saved, reopened",
                    StringComparison.OrdinalIgnoreCase),
                "A semantically identical deserialized session was not accepted.");
            previewService.WaitForCancellation = true;
            Task cancelledRender = viewModel.RenderPreviewAsync();
            await previewService.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            viewModel.CancelCommand.Execute(null);
            await cancelledRender;
            Assert(
                viewModel.PreviewImageSha256 == acceptedPreview &&
                viewModel.SessionSha256 == acceptedSession,
                "Cancellation changed the previous preview or session.");
            Assert(
                viewModel.RuntimeAuthorityText.Contains(
                    "false",
                    StringComparison.OrdinalIgnoreCase),
                "The workbench implied runtime authority.");
            Assert(
                viewModel.PreviewIsStale,
                "Cancelled rerender did not retain and identify the prior preview as stale.");
            viewModel.ClearReviewedIntake();
            Assert(
                viewModel.Rows.Count == 0 &&
                viewModel.SelectedRows.Count == 0 &&
                viewModel.Artifacts.Count == 0 &&
                viewModel.PreviewImageSha256 is null &&
                viewModel.SessionSha256 is null,
                "Intake invalidation retained stale catalog, selection, artifact, preview, or session state.");
            Console.WriteLine(
                "PASS Skyrim main workspace view model covers intake, filters, selection, drafts, navigation, preview, handoff hashes, cancellation, and false runtime authority.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RunSkyrimMainWorkspaceProductionPreviewTest() =>
        RunSkyrimMainWorkspaceProductionPreviewTestAsync()
            .GetAwaiter().GetResult();

    private static async Task
        RunSkyrimMainWorkspaceProductionPreviewTestAsync()
    {
        var labRoot = new WorkspacePath(
            "K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"main-workspace-production-preview-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "Data");
        string outputParent = Path.Combine(root, "previews");
        string comparisonOutputParent = Path.Combine(
            root, "comparisons");
        Directory.CreateDirectory(data);
        try
        {
            var plugin = new PluginName("ViewerFixture.esp");
            WorkspacePath pluginPath = Write(
                data, plugin.Value, "plugin");
            var identity = new SkyrimMainWorkspaceIdentity(
                plugin, plugin, new FormId(0x800), "NPC_");
            var record = Record(
                identity,
                SkyrimMainWorkspaceRecordKind.Npc,
                "ViewerFixture",
                "Viewer Fixture",
                NpcSex.Female,
                [NpcCategory.Unique]);
            Sha256Hash intakeHash =
                new(new string('7', 64));
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(data),
                new WorkspacePath(Path.Combine(
                    root, "loadorder.txt")),
                new WorkspacePath(Path.Combine(
                    root, "unused")),
                new Sha256Hash(new string('8', 64)),
                [
                    new PluginClosureReviewEntry(
                        plugin,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        pluginPath,
                        Hash(pluginPath),
                        [])
                ],
                [], [], [], 1,
                new Sha256Hash(new string('9', 64)),
                intakeHash,
                false);
            var composer =
                new FakeNpcVisualPreviewComposer();
            var comparisonService =
                new FakeNpcVisualComparisonService();
            WorkspacePath runtimeImage = Write(
                root, "runtime.png", "runtime");
            using var viewModel =
                new SkyrimMainWorkspaceViewModel(
                    new FakeMainWorkspaceCatalogService(
                        new SkyrimMainWorkspaceSnapshot(
                            intakeHash, [record], [])),
                    new FakeMainWorkspaceSettingsService(),
                    new FakeMainWorkspaceSessionService(),
                    new FakeMainWorkspacePreviewService(),
                    new FakeMainWorkspacePresetService(),
                    labRoot,
                    composer,
                    new WorkspacePath(outputParent),
                    null,
                    comparisonService,
                    new WorkspacePath(
                        comparisonOutputParent));
            await viewModel.LoadAsync(intake);
            viewModel.SelectedRow = viewModel.Rows.Single();
            Assert(
                viewModel.RenderPreviewCommand.CanExecute(null),
                "Production Render still required ConfigurePreview(...).");
            await viewModel.RenderPreviewAsync();
            Assert(
                composer.CallCount == 1 &&
                viewModel.PreviewViews.Count == 6 &&
                viewModel.ResolvedPreviewAssets.Count > 0 &&
                viewModel.PreviewImagePath?.EndsWith(
                    "contact-sheet.png",
                    StringComparison.OrdinalIgnoreCase) == true &&
                viewModel.HighFidelityPreviewLabel.Contains(
                    "Skyrim runtime remains authoritative",
                    StringComparison.Ordinal),
                "Production Render did not expose the composed face/body bundle.");
            await viewModel.RenderPreviewAsync();
            Assert(
                composer.CallCount == 1 &&
                viewModel.Status.Contains(
                    "hash-identical",
                    StringComparison.OrdinalIgnoreCase),
                "A hash-identical production bundle was not cached.");
            viewModel.RefreshPreviewCommand.Execute(null);
            while (viewModel.HasActiveOperation)
                await Task.Delay(10);
            Assert(
                composer.CallCount == 2,
                "Manual Refresh did not bypass the hash-identical cache.");
            viewModel.RuntimeComparisonImagePath =
                runtimeImage.Value;
            Assert(
                viewModel.ComparePreviewCommand.CanExecute(null),
                "A rendered face and configured runtime screenshot did not enable manual comparison.");
            await viewModel.ComparePreviewAsync();
            Assert(
                comparisonService.CallCount == 1 &&
                viewModel.PreviewComparisons.Count == 1 &&
                viewModel.PreviewComparisons[0].Label ==
                "Skyrim runtime" &&
                viewModel.Status.Contains(
                    "human verdict",
                    StringComparison.OrdinalIgnoreCase),
                "The desktop did not expose aligned comparison evidence without issuing a visual verdict.");
            Console.WriteLine(
                "PASS production main-workspace Render composes without ConfigurePreview, exposes six views/providers, caches exact bundles, refreshes manually, and creates human-unreviewed aligned comparisons.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void
        RunSkyrimMainWorkspaceExternalPackagePreviewTest() =>
        RunSkyrimMainWorkspaceExternalPackagePreviewTestAsync()
            .GetAwaiter().GetResult();

    private static async Task
        RunSkyrimMainWorkspaceExternalPackagePreviewTestAsync()
    {
        var labRoot = new WorkspacePath(
            "K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"main-workspace-external-package-preview-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "Data");
        string packageRoot = Path.Combine(root, "retained-package");
        string outputParent = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "desktop-npc-preview");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(packageRoot);
        try
        {
            var intakePlugin = new PluginName("ReviewedActors.esp");
            WorkspacePath intakePluginPath = Write(
                data, intakePlugin.Value, "reviewed-plugin");
            var intakeIdentity = new SkyrimMainWorkspaceIdentity(
                intakePlugin,
                intakePlugin,
                new FormId(0x900),
                "NPC_");
            var intakeRecord = Record(
                intakeIdentity,
                SkyrimMainWorkspaceRecordKind.Npc,
                "ReviewedActor",
                "Reviewed Actor",
                NpcSex.Female,
                [NpcCategory.Unique]);
            Sha256Hash intakeHash =
                new(new string('A', 64));
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(data),
                new WorkspacePath(Path.Combine(
                    root, "loadorder.txt")),
                new WorkspacePath(Path.Combine(
                    root, "unused")),
                new Sha256Hash(new string('B', 64)),
                [
                    new PluginClosureReviewEntry(
                        intakePlugin,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        intakePluginPath,
                        Hash(intakePluginPath),
                        [])
                ],
                [], [], [], 1,
                new Sha256Hash(new string('C', 64)),
                intakeHash,
                false);
            WorkspacePath manifest = Write(
                packageRoot,
                "npcmanager-package.json",
                "{\"fixture\":\"retained-sophia\"}");
            Sha256Hash manifestHash = Hash(manifest);
            var outputPlugin =
                new PluginName("SophiaLorenNpcManager.esp");
            var verifier = new FakePackageVerifyService(
                new PackageVerifyResult(
                    true,
                    new PackageVerificationArtifact(
                        "1",
                        "npcmanager-package-verification",
                        "skyrim-se",
                        "racemenu-jslot",
                        outputPlugin.Value,
                        new FormId(0x800),
                        manifest,
                        manifestHash,
                        [],
                        true,
                        true,
                        false),
                    []));
            var composer =
                new FakeNpcVisualPreviewComposer();
            using var viewModel =
                new SkyrimMainWorkspaceViewModel(
                    new FakeMainWorkspaceCatalogService(
                        new SkyrimMainWorkspaceSnapshot(
                            intakeHash, [intakeRecord], [])),
                    new FakeMainWorkspaceSettingsService(),
                    new FakeMainWorkspaceSessionService(),
                    new FakeMainWorkspacePreviewService(),
                    new FakeMainWorkspacePresetService(),
                    labRoot,
                    composer,
                    new WorkspacePath(outputParent),
                    null,
                    null,
                    null,
                    verifier);

            await viewModel.LoadAsync(intake);
            Assert(
                viewModel.Rows.Count == 1 &&
                viewModel.Rows.All(row =>
                    row.Identity.OwnerPlugin != outputPlugin) &&
                viewModel.SelectedRow is null,
                "The fixture did not prove that the retained package target is absent from the reviewed browser.");
            string[] dataFilesBefore =
                Directory.GetFiles(
                    data, "*", SearchOption.AllDirectories);
            Sha256Hash intakePluginHashBefore =
                Hash(intakePluginPath);
            Sha256Hash manifestHashBefore = Hash(manifest);

            await viewModel.OpenNpcVisualPackageAsync(manifest);

            Assert(
                verifier.CallCount == 1 &&
                viewModel.NpcVisualPackageManifestPath ==
                    manifest.Value &&
                viewModel.NpcVisualPackageSha256 ==
                    manifestHash.Value &&
                viewModel.NpcVisualPreviewTargetText.Contains(
                    outputPlugin.Value,
                    StringComparison.Ordinal) &&
                viewModel.NpcVisualPreviewTargetText.Contains(
                    "00000800",
                    StringComparison.Ordinal) &&
                viewModel.RenderPreviewCommand.CanExecute(null),
                "A completely verified retained package did not become a renderable external preview target.");

            await viewModel.RenderPreviewAsync();

            NpcVisualPreviewComposeRequest? request =
                composer.LastRequest;
            Assert(
                composer.CallCount == 1 &&
                request is not null &&
                request.Identity.OwnerPlugin == outputPlugin &&
                request.Identity.WinningProvider == outputPlugin &&
                request.Identity.FormId == new FormId(0x800) &&
                request.PackageOverlay?.ManifestPath == manifest &&
                request.PackageOverlay.ExpectedManifestSha256 ==
                    manifestHash,
                "Desktop Render did not pass the exact verified package identity and hash-bound overlay to the production composer.");
            string relativePreviewRoot = Path.GetRelativePath(
                outputParent,
                request!.OutputRoot.Value);
            string anticipatedFaceGeom = Path.Combine(
                request.OutputRoot.Value,
                "assets",
                "Data",
                "meshes",
                "actors",
                "character",
                "FaceGenData",
                "FaceGeom",
                outputPlugin.Value,
                "00000800.nif");
            Assert(
                relativePreviewRoot.Split(
                    Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries).Length == 1 &&
                anticipatedFaceGeom.Length < 260,
                "Desktop preview scratch nesting exceeds Blender Python's Windows path limit: " +
                $"root={request.OutputRoot.Value}; relative={relativePreviewRoot}; " +
                $"facegeom-length={anticipatedFaceGeom.Length}.");
            Assert(
                Directory.GetFiles(
                        data, "*", SearchOption.AllDirectories)
                    .SequenceEqual(dataFilesBefore) &&
                Hash(intakePluginPath) == intakePluginHashBefore &&
                Hash(manifest) == manifestHashBefore,
                "Opening or rendering the external package copied or mutated reviewed Data/package authority.");

            int successfulComposeCount = composer.CallCount;
            verifier.Result = new PackageVerifyResult(
                false,
                null,
                [
                    new Diagnostic(
                        "package-verification-fixture-failed",
                        DiagnosticSeverity.Error,
                        "The retained package changed after it was opened.")
                ]);
            await viewModel.RenderPreviewAsync();
            Assert(
                composer.CallCount == successfulComposeCount &&
                viewModel.NpcVisualPackageManifestPath == manifest.Value &&
                viewModel.NpcVisualPackageSha256 == manifestHash.Value &&
                viewModel.Diagnostics.Any(
                    line => line.Contains(
                        "package-verification-fixture-failed",
                        StringComparison.Ordinal)),
                "Render-time package re-verification did not fail closed while preserving the last accepted target.");

            using var cancelledOpen = new CancellationTokenSource();
            cancelledOpen.Cancel();
            await viewModel.OpenNpcVisualPackageAsync(
                manifest,
                cancelledOpen.Token);
            Assert(
                viewModel.NpcVisualPackageManifestPath == manifest.Value &&
                viewModel.NpcVisualPackageSha256 == manifestHash.Value &&
                viewModel.Diagnostics.Any(
                    line => line.Contains(
                        "main-workspace-preview-package-cancelled",
                        StringComparison.Ordinal)),
                "Cancelled package verification did not preserve the last accepted target with an explicit diagnostic.");

            viewModel.ClearNpcVisualPackagePreview();
            Assert(
                viewModel.NpcVisualPackageManifestPath is null &&
                viewModel.NpcVisualPackageSha256 is null &&
                !viewModel.RenderPreviewCommand.CanExecute(null),
                "Clearing the external package did not return preview targeting to the empty browser selection.");
            Console.WriteLine(
                "PASS a fully verified retained Manager package absent from the browser becomes the exact high-fidelity target without copying or mutating Data/package inputs, and Clear restores browser targeting.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SkyrimMainWorkspaceRecord Record(
        SkyrimMainWorkspaceIdentity identity,
        SkyrimMainWorkspaceRecordKind kind,
        string editorId,
        string name,
        NpcSex? sex,
        ImmutableArray<NpcCategory> categories,
        bool deleted = false,
        bool empty = false,
        ImmutableArray<SkyrimMainWorkspaceIdentity> entries = default) =>
        new(
            identity,
            kind,
            editorId,
            name,
            deleted,
            empty,
            sex,
            categories,
            deleted
                ? NpcChangeState.Deleted
                : NpcChangeState.Unchanged,
            identity.OwnerPlugin == identity.WinningProvider
                ? [identity.OwnerPlugin]
                : [identity.OwnerPlugin, identity.WinningProvider],
            entries.IsDefault ? [] : entries,
            new string('D', 64));

    private static WorkspacePath Write(
        string root,
        string name,
        string contents)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, contents);
        return new WorkspacePath(path);
    }

    private static Sha256Hash Hash(WorkspacePath path)
    {
        using FileStream stream = File.OpenRead(path.Value);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakeMainWorkspaceCatalogService(
        SkyrimMainWorkspaceSnapshot snapshot)
        : ISkyrimMainWorkspaceCatalogService
    {
        public Exception? NextException { get; set; }

        public ValueTask<SkyrimMainWorkspaceCatalogResult> LoadAsync(
            SkyrimMainWorkspaceCatalogRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NextException is not null)
                throw NextException;
            return ValueTask.FromResult(
                new SkyrimMainWorkspaceCatalogResult(
                    true,
                    snapshot,
                    []));
        }
    }

    private sealed class FakeMainWorkspaceSettingsService
        : ISkyrimMainWorkspaceSettingsService
    {
        public ValueTask<SkyrimMainWorkspaceSettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SkyrimMainWorkspaceSettingsLoadResult(
                    false,
                    new SkyrimMainWorkspaceSettings(
                        "1",
                        SkyrimMainWorkspaceFilter.Default,
                        SkyrimMainWorkspacePreviewOptions.Default),
                    null,
                    []));
        }

        public ValueTask<SkyrimMainWorkspaceSettingsSaveResult> SaveAsync(
            SkyrimMainWorkspaceSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SkyrimMainWorkspaceSettingsSaveResult(
                    true,
                    settings,
                    new Sha256Hash(new string('6', 64)),
                    []));
        }
    }

    private sealed class FakeMainWorkspaceSessionService
        : ISkyrimMainWorkspaceSessionService
    {
        public ValueTask<SkyrimMainWorkspaceSessionResult> SaveAsync(
            SkyrimMainWorkspaceSessionSaveRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkyrimMainWorkspaceSession reopened =
                request.Session with
                {
                    Selection = [.. request.Session.Selection],
                    Drafts = [.. request.Session.Drafts],
                    Artifacts = [.. request.Session.Artifacts]
                };
            return ValueTask.FromResult(
                new SkyrimMainWorkspaceSessionResult(
                    true,
                    reopened,
                    request.Destination,
                    new Sha256Hash(new string('7', 64)),
                    []));
        }

        public ValueTask<SkyrimMainWorkspaceSessionResult> ReadAsync(
            SkyrimMainWorkspaceSessionReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMainWorkspacePreviewService
        : ISkyrimMainWorkspacePreviewService
    {
        public SkyrimMainWorkspacePreviewResult? NextResult { get; set; }

        public Exception? NextException { get; set; }

        public bool WaitForCancellation { get; set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SkyrimMainWorkspacePreviewResult> RenderAsync(
            SkyrimMainWorkspacePreviewRequest request,
            CancellationToken cancellationToken)
        {
            if (NextException is not null)
                throw NextException;
            if (WaitForCancellation)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return NextResult ??
                throw new InvalidOperationException(
                    "No fake preview result was configured.");
        }

        public ValueTask<SkyrimMainWorkspacePreviewResult> RerollAsync(
            SkyrimMainWorkspaceRerollRequest request,
            CancellationToken cancellationToken) =>
            RenderAsync(request.Preview, cancellationToken);

        public ValueTask<SkyrimMainWorkspaceNifExportResult> ExportNifAsync(
            SkyrimMainWorkspaceNifExportRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new SkyrimMainWorkspaceNifExportResult(
                    false,
                    null,
                    null,
                    null,
                    false,
                    []));
    }

    private sealed class FakeMainWorkspacePresetService : IPresetService
    {
        public ValueTask<PresetParseResult> InspectAsync(
            PresetParseRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PresetExportResult> ExportAsync(
            PresetExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PresetDiffResult> DiffAsync(
            PresetDiffRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeNpcVisualPreviewComposer
        : INpcVisualPreviewComposer
    {
        public int CallCount { get; private set; }

        public NpcVisualPreviewComposeRequest? LastRequest
        {
            get;
            private set;
        }

        public async ValueTask<NpcVisualPreviewComposeResult>
            ComposeAsync(
                NpcVisualPreviewComposeRequest request,
                CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            Directory.CreateDirectory(
                request.OutputRoot.Value);
            string assetPath = Path.Combine(
                request.OutputRoot.Value,
                "assets", "Data", "face.nif");
            Directory.CreateDirectory(
                Path.GetDirectoryName(assetPath)!);
            await File.WriteAllTextAsync(
                assetPath, "face", cancellationToken);
            var face = new NpcVisualAsset(
                NpcVisualAssetRole.FaceGeom,
                new AssetPath("meshes/face.nif"),
                "package-overlay:ViewerFixture",
                Hash(new WorkspacePath(assetPath)),
                new FileInfo(assetPath).Length,
                new WorkspacePath(assetPath),
                false,
                []);
            var source = new NpcVisualSourceGraph(
                NpcVisualPreviewRoute.Cotr,
                request.Identity,
                NpcSex.Female,
                50,
                "COR_AllRace.esp|0x00000800",
                "#443322",
                "#D7A88D",
                [face],
                [],
                false,
                []);
            var views =
                ImmutableArray.CreateBuilder<
                    NpcVisualPreviewView>();
            foreach (string id in new[]
                     {
                         "face-front", "face-left",
                         "face-right",
                         "face-alternate-light",
                         "body-front", "body-back"
                     })
            {
                WorkspacePath image = Write(
                    request.OutputRoot.Value,
                    $"{id}.png", id);
                WorkspacePath mask = Write(
                    request.OutputRoot.Value,
                    $"{id}.roles.png", id + "-mask");
                views.Add(new(
                    id,
                    image,
                    Hash(image),
                    mask,
                    Hash(mask),
                    900,
                    900));
            }
            WorkspacePath contact = Write(
                request.OutputRoot.Value,
                "contact-sheet.png", "contact");
            WorkspacePath status = Write(
                request.OutputRoot.Value,
                "npc-preview-render-evidence.json",
                "{}");
            var renderEvidence =
                new NpcVisualPreviewRenderEvidence(
                    "4.5.1",
                    "BLENDER_EEVEE_NEXT",
                    1,
                    true,
                    1,
                    ["Skeleton"],
                    0,
                    ImmutableDictionary<string, int>.Empty,
                    ImmutableDictionary<string, long>.Empty,
                    ImmutableDictionary<string, double>.Empty,
                    [],
                    [],
                    status,
                    Hash(status));
            var visualEvidence =
                new NpcVisualPreviewVisualEvidence(
                    1, 0.99, 478, 31, true, []);
            WorkspacePath bundlePath = Write(
                request.OutputRoot.Value,
                "npc-preview-bundle.json", "{}");
            WorkspacePath hashesPath = Write(
                request.OutputRoot.Value,
                "npc-preview.hashes.sha256", "hashes");
            var bundle = new NpcVisualPreviewBundle(
                "npc-preview-bundle/1",
                "npc-preview-scene/2",
                "High-fidelity off-engine preview — Skyrim runtime remains authoritative",
                false,
                source,
                views.ToImmutable(),
                contact,
                Hash(contact),
                bundlePath,
                Hash(bundlePath),
                hashesPath,
                Hash(hashesPath),
                renderEvidence,
                visualEvidence,
                []);
            return new NpcVisualPreviewComposeResult(
                true, bundle, []);
        }
    }

    private sealed class ContextRecordingMainWorkspacePluginReader(
        SkyrimMainWorkspacePluginReadResult result)
        : ISkyrimMainWorkspacePluginReader
    {
        public SynchronizationContext? ObservedContext { get; private set; }

        public SkyrimMainWorkspacePluginReadResult Read(
            WorkspacePath pluginPath)
        {
            ObservedContext = SynchronizationContext.Current;
            return result;
        }
    }

    private sealed class MainWorkspaceSentinelContext
        : SynchronizationContext
    {
        private readonly ConcurrentQueue<(
            SendOrPostCallback Callback,
            object? State)> callbacks = new();

        public override void Post(
            SendOrPostCallback callback,
            object? state) => callbacks.Enqueue((callback, state));

        public void DrainUntil(Task task, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                Drain();
                if (task.IsCompleted)
                    break;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Queued main-workspace UI work did not complete.");
                Thread.Sleep(1);
            }
            Drain();
        }

        private void Drain()
        {
            while (callbacks.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }

    private sealed class FakePackageVerifyService(
        PackageVerifyResult result) : IPackageVerifyService
    {
        public int CallCount { get; private set; }

        public PackageVerifyResult Result { get; set; } = result;

        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeNpcVisualComparisonService
        : INpcVisualComparisonService
    {
        public int CallCount { get; private set; }

        public ValueTask<NpcVisualComparisonResult> CompareAsync(
            NpcVisualComparisonRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Directory.CreateDirectory(
                request.OutputRoot.Value);
            WorkspacePath aligned = Write(
                request.OutputRoot.Value,
                "aligned-comparison.png",
                "aligned");
            WorkspacePath side = Write(
                request.OutputRoot.Value,
                "side-by-side.png",
                "side");
            WorkspacePath overlay = Write(
                request.OutputRoot.Value,
                "overlay.png",
                "overlay");
            WorkspacePath heatmap = Write(
                request.OutputRoot.Value,
                "heatmap.png",
                "heatmap");
            WorkspacePath blinkPreview = Write(
                request.OutputRoot.Value,
                "blink-preview.png",
                "preview");
            WorkspacePath blinkComparison = Write(
                request.OutputRoot.Value,
                "blink-comparison.png",
                "comparison");
            WorkspacePath evidence = Write(
                request.OutputRoot.Value,
                "comparison-evidence.json",
                "{\"humanVerdict\":\"unreviewed\"}");
            return ValueTask.FromResult(
                new NpcVisualComparisonResult(
                    true,
                    aligned,
                    Hash(aligned),
                    side,
                    Hash(side),
                    overlay,
                    Hash(overlay),
                    heatmap,
                    Hash(heatmap),
                    blinkPreview,
                    Hash(blinkPreview),
                    blinkComparison,
                    Hash(blinkComparison),
                    evidence,
                    Hash(evidence),
                    new NpcVisualComparisonTransform(
                        1, 0, 0, 0),
                    12.5,
                    []));
        }
    }
}
