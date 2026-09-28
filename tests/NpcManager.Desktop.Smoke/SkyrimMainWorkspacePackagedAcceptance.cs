using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Desktop;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static readonly JsonSerializerOptions SkyGui002JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly string[] SkyGui002AnimationRoles =
        ["Core", "MT"];
    private static readonly string[] SkyGui002ScreenshotNames =
        ["catalog", "drafts", "preview", "routes", "final"];

    private sealed record SkyGui002JourneyResult(
        string CatalogPath,
        string SettingsPath,
        string SessionPath,
        string ScenePath,
        string ImagePath,
        string NifPath,
        string ChildProposalPath,
        string ChildOutputPath,
        string NegativeSessionPath,
        IReadOnlyDictionary<string, bool> Checks);

    private static int PrepareSkyrimMainWorkspacePackagedAcceptance(
        string intakeRoot,
        string realAssetRoot)
    {
        if (Directory.Exists(intakeRoot) || File.Exists(intakeRoot))
            throw new IOException(
                $"The connected intake must be fresh: {intakeRoot}");

        string dataRoot = Path.Combine(intakeRoot, "Data");
        string evidenceRoot = Path.Combine(intakeRoot, "real-evidence");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(evidenceRoot);

        string skyrimPath = Path.Combine(dataRoot, "Skyrim.esm");
        string actorsPath = Path.Combine(dataRoot, "Actors.esp");
        string patchPath = Path.Combine(dataRoot, "ActorsPatch.esp");
        WriteSkyGui002Master(skyrimPath);
        WriteSkyGui002Actors(actorsPath);
        WriteSkyGui002Patch(patchPath);

        string realSkyrim = Path.Combine(realAssetRoot, "Skyrim.esm");
        string realBriar = Path.Combine(realAssetRoot, "Briar.esp");
        RequireFile(realSkyrim, "Copied real Skyrim.esm");
        RequireFile(realBriar, "Copied real Briar.esp");
        CopyFresh(
            realSkyrim,
            Path.Combine(evidenceRoot, "Skyrim.esm"));
        CopyFresh(
            realBriar,
            Path.Combine(evidenceRoot, "Briar.esp"));

        string[] assetPaths =
        [
            "Meshes/armor/Briar/Briar_0.nif",
            "Meshes/armor/Briar/Briar_1.nif",
            "Meshes/armor/Briar/Briar_GND.nif",
            "Textures/armor/Briar/Briar.dds",
            "Textures/armor/Briar/Briar_n.dds",
            "Textures/armor/Briar/Briar_m.dds"
        ];
        foreach (string relative in assetPaths)
        {
            string normalized = relative.Replace(
                '/', Path.DirectorySeparatorChar);
            CopyFresh(
                Path.Combine(realAssetRoot, normalized),
                Path.Combine(dataRoot, normalized));
        }

        string loadOrderPath = Path.Combine(intakeRoot, "load-order.json");
        File.WriteAllBytes(
            loadOrderPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    edition = "skyrimse",
                    plugins = new[]
                    {
                        new { name = "Skyrim.esm", enabled = true, order = 0 },
                        new { name = "Actors.esp", enabled = true, order = 1 },
                        new { name = "ActorsPatch.esp", enabled = true, order = 2 }
                    }
                },
                SkyGui002JsonOptions));

        string previewManifest = Path.Combine(
            intakeRoot,
            "preview-manifest.json");
        File.WriteAllBytes(
            previewManifest,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    edition = "skyrimse",
                    npcFormId = "0x00000800",
                    assets = new object[]
                    {
                        PreviewInput(
                            "face",
                            assetPaths[0],
                            dataRoot)
                    },
                    morphs = Array.Empty<object>(),
                    variants = new object[]
                    {
                        new
                        {
                            id = "variant-a",
                            outfit = "Actors.esp|0x00000800",
                            assets = assetPaths.Take(1).ToArray()
                        },
                        new
                        {
                            id = "variant-b",
                            outfit = "Actors.esp|0x00000801",
                            assets = assetPaths.Take(1).ToArray()
                        }
                    },
                    animations = new object[]
                    {
                        new
                        {
                            id = "walk",
                            name = "Acceptance Walk",
                            path = "animations/MT/Neutral/walk.hkx",
                            skeleton = "meshes/actors/character/character assets female/skeleton_female.nif",
                            frames = 10,
                            fps = 30,
                            roles = SkyGui002AnimationRoles,
                            stateAxes = "Forward"
                        }
                    }
                },
                SkyGui002JsonOptions));

        string[] sourceFiles =
        [
            skyrimPath,
            actorsPath,
            patchPath,
            loadOrderPath,
            previewManifest,
            Path.Combine(evidenceRoot, "Skyrim.esm"),
            Path.Combine(evidenceRoot, "Briar.esp"),
            .. assetPaths.Select(relative => Path.Combine(
                dataRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)))
        ];
        string sourceManifest = Path.Combine(
            intakeRoot,
            "source-manifest.json");
        File.WriteAllBytes(
            sourceManifest,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    artifactKind = "sky-gui-002-connected-source",
                    runtimeAuthority = false,
                    files = sourceFiles.Select(path => new
                    {
                        path,
                        relativePath = Path.GetRelativePath(
                                intakeRoot,
                                path)
                            .Replace(
                                Path.DirectorySeparatorChar,
                                '/'),
                        sha256 = HashAcceptanceFile(path),
                        byteLength = new FileInfo(path).Length
                    }).ToArray()
                },
                SkyGui002JsonOptions));

        Console.WriteLine(sourceManifest);
        return 0;
    }

    private static int RunSkyrimMainWorkspacePackagedAcceptance(
        string executable,
        string intakeRoot,
        string previewManifest,
        string assetRoot,
        string outputRoot,
        string screenshotRoot,
        string reportPath,
        string rawReportPath,
        string negativeReportPath,
        string verifierPath)
    {
        executable = Path.GetFullPath(executable);
        intakeRoot = Path.GetFullPath(intakeRoot);
        previewManifest = Path.GetFullPath(previewManifest);
        assetRoot = Path.GetFullPath(assetRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        screenshotRoot = Path.GetFullPath(screenshotRoot);
        reportPath = Path.GetFullPath(reportPath);
        rawReportPath = Path.GetFullPath(rawReportPath);
        negativeReportPath = Path.GetFullPath(negativeReportPath);
        verifierPath = Path.GetFullPath(verifierPath);
        string projectRoot =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation";
        string proposalPath = Path.Combine(
            projectRoot,
            "05-reports",
            "sky-gui-002-main-workspace-proposed-change-2026-07-23.json");
        string sourceManifest = Path.Combine(
            intakeRoot,
            "source-manifest.json");
        string outputOrdinal = Path.GetFileName(outputRoot)
            .Split('-')
            .Last();
        string runSuffix = outputOrdinal == "1"
            ? string.Empty
            : "-" + outputOrdinal;
        string[] screenshots =
        [
            Path.Combine(
                screenshotRoot,
                $"20260723-SKY-GUI-002-catalog{runSuffix}.png"),
            Path.Combine(
                screenshotRoot,
                $"20260723-SKY-GUI-002-drafts{runSuffix}.png"),
            Path.Combine(
                screenshotRoot,
                $"20260723-SKY-GUI-002-preview{runSuffix}.png"),
            Path.Combine(
                screenshotRoot,
                $"20260723-SKY-GUI-002-routes{runSuffix}.png"),
            Path.Combine(
                screenshotRoot,
                $"20260723-SKY-GUI-002-final{runSuffix}.png")
        ];
        foreach (string required in
                 new[]
                 {
                     executable,
                     previewManifest,
                     sourceManifest,
                     verifierPath,
                     proposalPath
                 })
            RequireFile(required, "SKY-GUI-002 acceptance input");
        foreach (string fresh in
                 new[]
                 {
                     outputRoot,
                     reportPath,
                     rawReportPath,
                     negativeReportPath
                 }.Concat(screenshots))
            if (File.Exists(fresh) || Directory.Exists(fresh))
                throw new IOException(
                    $"SKY-GUI-002 acceptance output already exists: {fresh}");
        Directory.CreateDirectory(screenshotRoot);

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        ExerciseSkyGui002PackagedLifecycle(executable);
        checks["selfContainedProcessLifecycle"] = true;

        ReviewedGameIntake intake = CreateSkyGui002Intake(
            intakeRoot,
            assetRoot,
            outputRoot);
        SkyGui002JourneyResult journey = RunSkyGui002UiJourney(
            intake,
            previewManifest,
            assetRoot,
            outputRoot,
            screenshots);
        foreach (KeyValuePair<string, bool> check in journey.Checks)
            checks[check.Key] = check.Value;

        RunSkyGui002Verifier(
            verifierPath,
            sourceManifest,
            journey.CatalogPath,
            journey.SessionPath,
            journey.SettingsPath,
            journey.ScenePath,
            journey.ImagePath,
            journey.NifPath,
            rawReportPath);
        RunSkyGui002Verifier(
            verifierPath,
            sourceManifest,
            journey.CatalogPath,
            journey.NegativeSessionPath,
            journey.SettingsPath,
            journey.ScenePath,
            journey.ImagePath,
            journey.NifPath,
            negativeReportPath);
        using JsonDocument raw = JsonDocument.Parse(
            File.ReadAllBytes(rawReportPath));
        using JsonDocument negative = JsonDocument.Parse(
            File.ReadAllBytes(negativeReportPath));
        Assert(
            raw.RootElement.GetProperty("verdict").GetString() == "PASS" &&
            raw.RootElement.GetProperty("findingCount").GetInt32() == 0,
            "The independent SKY-GUI-002 verifier did not return a clean PASS.");
        Assert(
            negative.RootElement.GetProperty("verdict").GetString() ==
                "EXPECTED_FAIL" &&
            negative.RootElement.GetProperty("findingCount").GetInt32() == 1,
            "The planted child-hash negative control did not produce exactly one expected finding.");
        checks["independentVerifier"] = true;
        checks["singleFindingNegativeControl"] = true;

        object[] screenshotEvidence = screenshots
            .Select((path, index) => AcceptanceScreenshotEvidence(
                projectRoot,
                SkyGui002ScreenshotNames[index],
                path))
            .ToArray();
        File.WriteAllBytes(
            reportPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    artifactKind =
                        "sky-gui-002-packaged-main-workspace-acceptance",
                    surfaceId = "SKY-GUI-002",
                    verdict = "PASS_STATIC_RUNTIME_REQUIRED",
                    packagedExecutable = Relative(
                        projectRoot,
                        executable),
                    packagedExecutableSha256 =
                        HashAcceptanceFile(executable),
                    connectedIntake = Relative(
                        projectRoot,
                        intakeRoot),
                    connectedOutput = Relative(
                        projectRoot,
                        outputRoot),
                    sourceManifest = Relative(
                        projectRoot,
                        sourceManifest),
                    catalog = Relative(
                        projectRoot,
                        journey.CatalogPath),
                    settings = Relative(
                        projectRoot,
                        journey.SettingsPath),
                    session = Relative(
                        projectRoot,
                        journey.SessionPath),
                    scene = Relative(
                        projectRoot,
                        journey.ScenePath),
                    image = Relative(
                        projectRoot,
                        journey.ImagePath),
                    nif = Relative(
                        projectRoot,
                        journey.NifPath),
                    childProposal = Relative(
                        projectRoot,
                        journey.ChildProposalPath),
                    childOutput = Relative(
                        projectRoot,
                        journey.ChildOutputPath),
                    independentReport = Relative(
                        projectRoot,
                        rawReportPath),
                    negativeControl = Relative(
                        projectRoot,
                        negativeReportPath),
                    checks,
                    screenshots = screenshotEvidence,
                    staticAuthority = true,
                    runtimeAuthority = false,
                    equipmentAuthority = false,
                    visualAuthority = false
                },
                SkyGui002JsonOptions));
        Console.WriteLine(reportPath);
        return 0;
    }

    private static SkyGui002JourneyResult RunSkyGui002UiJourney(
        ReviewedGameIntake intake,
        string previewManifest,
        string assetRoot,
        string outputRoot,
        IReadOnlyList<string> screenshots)
    {
        App? application = null;
        SkyGui002JourneyResult? result = null;
        Exception? failure = null;
        try
        {
            application = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            application.InitializeComponent();
            var frame = new DispatcherFrame();
            _ = application.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                async () =>
                {
                    try
                    {
                        result = await RunSkyGui002UiJourneyAsync(
                            intake,
                            previewManifest,
                            assetRoot,
                            outputRoot,
                            screenshots);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                });
            Dispatcher.PushFrame(frame);
            if (failure is not null)
                throw new InvalidOperationException(
                    "The production SKY-GUI-002 UI journey failed.",
                    failure);
            return result ??
                   throw new InvalidOperationException(
                       "The SKY-GUI-002 UI journey returned no evidence.");
        }
        finally
        {
            if (application is not null &&
                !application.Dispatcher.HasShutdownStarted)
                application.Shutdown();
        }
    }

    private static async Task<SkyGui002JourneyResult>
        RunSkyGui002UiJourneyAsync(
            ReviewedGameIntake intake,
            string previewManifest,
            string assetRoot,
            string outputRoot,
            IReadOnlyList<string> screenshots)
    {
        const string projectRoot =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation";
        const string labRootText = "K:\\ExampleWorkspace";
        string artifactSuffix = Path.GetFileName(outputRoot)
            .Split('-')
            .Last();
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                Left = 8,
                Top = 8,
                Width = 1500,
                Height = 1100
            };
            window.Show();
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);
            IntPtr handle = new WindowInteropHelper(window).Handle;
            DpiScale initialDpi =
                VisualTreeHelper.GetDpi(window);
            Assert(
                handle != IntPtr.Zero &&
                SkyGui023AcceptanceNative.MoveWindow(
                    handle,
                    8,
                    8,
                    (int)Math.Ceiling(
                        1500 * initialDpi.DpiScaleX),
                    (int)Math.Ceiling(
                        1100 * initialDpi.DpiScaleY),
                    true),
                "The production workbench could not be visibly resized.");

            var shell = (WorkspaceShellViewModel)window.DataContext;
            await shell.ApplyAcceptedIntakeAsync(intake);
            SkyrimMainWorkspaceViewModel workbench =
                shell.MainWorkspace ??
                throw new InvalidOperationException(
                    "The production shell has no integrated workbench.");
            Assert(
                workbench.Rows.Count == 4 &&
                workbench.Rows.Count(row =>
                    row.Source.Kind ==
                    SkyrimMainWorkspaceRecordKind.Npc) == 2 &&
                workbench.Rows.Count(row =>
                    row.Source.Kind ==
                    SkyrimMainWorkspaceRecordKind.LeveledNpc) == 2,
                "The production workbench did not load two NPCs and two LVLNs.");
            window.MainTasks.SelectedItem = window.NpcWorkspaceTab;
            await PresentSkyGui002Async(window, handle);
            CaptureSkyGui002Window(window, screenshots[0]);
            checks["reviewAndCatalog"] = true;

            SkyrimMainWorkspaceRowViewModel female =
                workbench.Rows.Single(row =>
                    row.Identity.FormId == new FormId(0x800));
            SkyrimMainWorkspaceRowViewModel male =
                workbench.Rows.Single(row =>
                    row.Identity.FormId == new FormId(0x801));
            SkyrimMainWorkspaceRowViewModel leveled =
                workbench.Rows.Single(row =>
                    row.Identity.FormId == new FormId(0x900));
            SkyrimMainWorkspaceRowViewModel empty =
                workbench.Rows.Single(row =>
                    row.Identity.FormId == new FormId(0x901));
            Assert(
                female.Identity.OwnerPlugin ==
                    new PluginName("Actors.esp") &&
                female.Identity.WinningProvider ==
                    new PluginName("ActorsPatch.esp") &&
                female.Source.RawRecordSha256.Length == 64 &&
                female.Source.Sex == NpcSex.Female &&
                leveled.Source.LeveledNpcEntries.Select(item => item.FormId)
                    .SequenceEqual(
                        [new FormId(0x800), new FormId(0x801)]),
                "Owner, winner, raw hash, sex, or LVLN entry facts were not visible in the loaded model.");

            workbench.Search = "Catalog Female";
            Assert(workbench.VisibleRows.Count == 1,
                "Name search did not isolate the female NPC.");
            workbench.Search = "0x00000801";
            Assert(workbench.VisibleRows.Count == 1,
                "FormID search did not isolate the male NPC.");
            workbench.Search = "ActorsPatch.esp";
            Assert(workbench.VisibleRows.Count >= 2,
                "Provider search did not expose the winning records.");
            workbench.Search = string.Empty;
            workbench.ShowNpcs = false;
            Assert(workbench.VisibleRows.Count == 2,
                "NPC-kind filtering did not leave both LVLNs.");
            workbench.ShowNpcs = true;
            workbench.ShowLeveledNpcs = false;
            Assert(workbench.VisibleRows.Count == 2,
                "LVLN-kind filtering did not leave both NPCs.");
            workbench.ShowLeveledNpcs = true;
            workbench.Gender = SkyrimMainWorkspaceGender.Female;
            Assert(workbench.VisibleRows.SequenceEqual([female]),
                "Female filtering did not isolate the female NPC.");
            workbench.Gender = SkyrimMainWorkspaceGender.Male;
            Assert(workbench.VisibleRows.SequenceEqual([male]),
                "Male filtering did not isolate the male NPC.");
            workbench.Gender = SkyrimMainWorkspaceGender.Random;
            workbench.ShowUnique = false;
            workbench.ShowGeneric = false;
            workbench.ShowTemplate = false;
            workbench.ShowUnused = false;
            workbench.ShowUnique = true;
            workbench.ShowGeneric = true;
            workbench.ShowTemplate = true;
            workbench.ShowUnused = true;
            workbench.ChangedOnly = true;
            workbench.ChangedOnly = false;
            workbench.IncludeDeleted = false;
            workbench.IncludeDeleted = true;
            checks["allFilters"] = true;

            workbench.SelectVisibleRange(
                workbench.VisibleRows.First(),
                workbench.VisibleRows.Last());
            Assert(
                workbench.SelectedRows.Contains(female) &&
                workbench.SelectedRows.Contains(male) &&
                workbench.SelectedRows.Contains(leveled),
                "Visible-order range selection did not span the two NPCs and LVLN.");
            workbench.MarkChangedCommand.Execute(null);
            workbench.MarkDeleteCommand.Execute(null);
            await PresentSkyGui002Async(window, handle);
            CaptureSkyGui002Window(window, screenshots[1]);
            workbench.RestoreCommand.Execute(null);
            workbench.ResetCommand.Execute(null);
            Assert(
                workbench.SelectedRows.All(row =>
                    !row.IsChanged && !row.IsDeletePending),
                "Draft reset/restore did not return the selected range to clean state.");
            checks["draftLifecycle"] = true;

            workbench.SelectedRow = empty;
            await workbench.RerollAsync();
            Assert(
                workbench.Diagnostics.Any(item =>
                    item.Contains(
                        "empty",
                        StringComparison.OrdinalIgnoreCase)),
                "The empty LVLN reroll refusal was not visible.");
            workbench.SelectedRow = female;

            Sha256Hash manifestHash = HashAsDomain(previewManifest);
            string previewRoot = Path.Combine(outputRoot, "preview");
            Directory.CreateDirectory(previewRoot);
            string baselineHash = await RenderSkyGui002PreviewAsync(
                workbench,
                previewManifest,
                manifestHash,
                assetRoot,
                previewRoot,
                "baseline");
            Assert(
                workbench.PreviewImagePath is not null &&
                File.Exists(workbench.PreviewImagePath) &&
                workbench.PreviewAuthorityText.Contains(
                    "runtime authority: false",
                    StringComparison.OrdinalIgnoreCase),
                "The real copied Briar preview did not become a hash-accepted visible image.");
            await PresentSkyGui002Async(window, handle);
            CaptureSkyGui002Window(window, screenshots[2]);

            workbench.PreviewMode =
                SkyrimMainWorkspacePreviewMode.FaceOnly;
            string faceHash = await RenderSkyGui002PreviewAsync(
                workbench,
                previewManifest,
                manifestHash,
                assetRoot,
                previewRoot,
                "face-only");
            Assert(faceHash != baselineHash,
                "Full/face mode did not change the semantic scene hash.");
            workbench.PreviewMode =
                SkyrimMainWorkspacePreviewMode.FullCharacter;
            workbench.RenderBody = false;
            workbench.RenderUnderarmor = false;
            workbench.RenderArmor = false;
            workbench.RenderHeadwear = false;
            workbench.RenderGore = false;
            _ = await RenderSkyGui002PreviewAsync(
                workbench,
                previewManifest,
                manifestHash,
                assetRoot,
                previewRoot,
                "visibility");
            workbench.ApplyBoneMorphs = false;
            workbench.ApplyVertexMorphs = false;
            workbench.ApplyBodyWeight = false;
            workbench.ApplySculpt = false;
            _ = await RenderSkyGui002PreviewAsync(
                workbench,
                previewManifest,
                manifestHash,
                assetRoot,
                previewRoot,
                "morphs-off");
            workbench.PreviewOptions =
                SkyrimMainWorkspacePreviewOptions.Default;
            checks["previewChannels"] = true;

            workbench.RerollSeed = 73;
            workbench.ConfigurePreview(
                new WorkspacePath(previewManifest),
                manifestHash,
                new WorkspacePath(assetRoot),
                new WorkspacePath(Path.Combine(
                    previewRoot,
                    "reroll-a.json")),
                new WorkspacePath(Path.Combine(
                    previewRoot,
                    "reroll-a.png")));
            await workbench.RerollAsync();
            string firstVariant = ReadSceneVariant(
                workbench.PreviewScenePath?.Value ??
                throw new InvalidDataException(
                    "First reroll produced no scene."));
            workbench.RerollSeed = 73;
            workbench.ConfigurePreview(
                new WorkspacePath(previewManifest),
                manifestHash,
                new WorkspacePath(assetRoot),
                new WorkspacePath(Path.Combine(
                    previewRoot,
                    "reroll-b.json")),
                new WorkspacePath(Path.Combine(
                    previewRoot,
                    "reroll-b.png")));
            await workbench.RerollAsync();
            string secondVariant = ReadSceneVariant(
                workbench.PreviewScenePath?.Value ??
                throw new InvalidDataException(
                    "Second reroll produced no scene."));
            Assert(
                firstVariant == secondVariant &&
                firstVariant.Length > 0,
                "Equal reroll seeds did not resolve the same exact variant.");
            checks["deterministicReroll"] = true;

            await shell.Lighting.InitializeAsync();
            using (SkyrimLightingEditorViewModel lightingEditor =
                   shell.Lighting.CreateEditor())
            {
                Assert(
                    SkyrimLightingRules.AreEquivalent(
                        lightingEditor.PreviewPreset,
                        shell.Lighting.CurrentPreset),
                    "The production lighting editor did not open on the accepted rig.");
                lightingEditor.Cancel();
            }
            shell.AnimationPicker.ManifestPath = previewManifest;
            shell.AnimationPicker.SelectedEdition =
                GameEdition.SkyrimSpecialEdition;
            shell.AnimationPicker.IsFemale = true;
            await shell.AnimationPicker.LoadAsync(
                CancellationToken.None);
            AnimationTreeItemViewModel animationLeaf =
                FlattenAnimationTree(shell.AnimationPicker.Groups)
                    .First(item => item.Clip?.Id == "walk");
            shell.AnimationPicker.Select(animationLeaf);
            PreviewAnimationSelection animation =
                shell.AnimationPicker.AcceptSelection() ??
                throw new InvalidDataException(
                    "The animation picker returned no exact selection.");
            shell.AnimationPicker.CommitSelection(animation);
            checks["lightingAndAnimationControls"] = true;

            workbench.CopyPlaceAtMeCommand.Execute(null);
            Assert(
                workbench.PlaceAtMeText ==
                    "player.placeatme XX000800 1",
                "The workbench did not copy the exact unresolved-prefix placeatme text.");

            AssertRoute(
                window,
                workbench.EditNpcCommand,
                window.EditNpcTab,
                "NPC");
            AssertRoute(
                window,
                workbench.EditHeadPartsCommand,
                window.HeadPartsTab,
                "Head");
            AssertRoute(
                window,
                workbench.EditFaceCommand,
                window.FaceTab,
                "Face");
            AssertRoute(
                window,
                workbench.EditBodyCommand,
                window.BodyTab,
                "Body");
            Assert(
                shell.BodyEdit.SourcePlugin == "ActorsPatch.esp" &&
                shell.BodyEdit.NpcFormId == "0x00000800",
                "The body route did not retain the exact actor/winning provider.");
            workbench.SelectedRow = male;
            AssertRoute(
                window,
                workbench.EditBodyCommand,
                window.BodyTab,
                "Body source-owned write");
            Assert(
                shell.BodyEdit.SourcePlugin == "Actors.esp" &&
                shell.BodyEdit.NpcFormId == "0x00000801",
                "The source-owned body route did not retain the exact male actor/provider.");

            string childRoot = Path.Combine(outputRoot, "child");
            Directory.CreateDirectory(childRoot);
            string childProposal = Path.Combine(
                childRoot,
                $"Actors-body-proposal-{artifactSuffix}.json");
            string childOutput = Path.Combine(
                childRoot,
                $"Actors-body-{artifactSuffix}.esp");
            shell.BodyEdit.ProposalPath = childProposal;
            shell.BodyEdit.OutputPlugin = childOutput;
            await shell.BodyEdit.LoadAsync();
            if (!shell.BodyEdit.IsLoaded)
                throw new InvalidOperationException(
                    "The routed production body editor did not load the exact NPC. " +
                    $"Status: {shell.BodyEdit.Status} Diagnostics: " +
                    string.Join(" | ", shell.BodyEdit.Diagnostics));
            SkyrimBodyEditorViewModel editor =
                shell.BodyEdit.CreateEditor();
            editor.Weight.Value =
                editor.Weight.Value < 50 ? 50 : 25;
            Assert(
                editor.TryAccept() &&
                shell.BodyEdit.ApplyEditor(editor),
                "The production child body transaction did not stage one exact NAM7 change.");
            await shell.BodyEdit.ReviewAsync();
            Assert(
                shell.BodyEdit.IsReviewed &&
                File.Exists(childProposal) &&
                !File.Exists(childOutput),
                "Child review either failed or wrote the final destination prematurely.");
            checks["proposalBeforeChildWrite"] = true;
            await shell.BodyEdit.ExecuteAsync();
            await WaitSkyGui002Async(
                () => workbench.Artifacts.Count == 1,
                "The verified child result did not return to the workbench.",
                TimeSpan.FromSeconds(20));
            Assert(
                File.Exists(childOutput) &&
                workbench.Artifacts.Single().Kind == "body-plugin" &&
                workbench.Artifacts.Single().Identity == male.Identity &&
                male.IsChanged,
                "The real verified body child did not create a matching dirty-row handoff.");
            checks["verifiedChildHandoff"] = true;

            workbench.ReplaceSelection(
                [female.Identity, male.Identity]);
            workbench.SelectedRow = female;
            AssertRoute(
                window,
                workbench.EditOutfitCommand,
                window.OutfitsTab,
                "Outfit");
            AssertRoute(
                window,
                workbench.LoadRaceMenuPresetCommand,
                window.PresetToNpcTab,
                "Preset");
            workbench.CopyAppearanceCommand.Execute(null);
            Assert(
                workbench.CopiedAppearanceIdentity == female.Identity,
                "Copy appearance lost the exact source identity.");
            AssertRoute(
                window,
                workbench.PasteAppearanceCommand,
                window.PasteAppearanceTab,
                "Paste");
            AssertRoute(
                window,
                workbench.CharGenOptionsCommand,
                window.CharGenOptionsTab,
                "CharGen");
            AssertRoute(
                window,
                workbench.BuildCharGenCommand,
                window.FaceGenTab,
                "FaceGen");
            AssertRoute(
                window,
                workbench.SavePackageCommand,
                window.SavePackageTab,
                "Save");
            window.MainTasks.SelectedItem = window.NpcWorkspaceTab;
            await PresentSkyGui002Async(window, handle);
            CaptureSkyGui002Window(window, screenshots[3]);
            checks["typedRoutes"] = true;

            workbench.RenderBody = true;
            workbench.RenderUnderarmor = true;
            workbench.RenderArmor = true;
            workbench.RenderHeadwear = true;
            workbench.RenderGore = true;
            workbench.ApplyBoneMorphs = true;
            workbench.ApplyVertexMorphs = true;
            workbench.ApplyBodyWeight = true;
            workbench.ApplySculpt = true;
            string finalScene = Path.Combine(
                previewRoot,
                "scene-final.json");
            string finalImage = Path.Combine(
                previewRoot,
                "scene-final.png");
            workbench.ConfigurePreview(
                new WorkspacePath(previewManifest),
                manifestHash,
                new WorkspacePath(assetRoot),
                new WorkspacePath(finalScene),
                new WorkspacePath(finalImage),
                null,
                null,
                shell.Lighting.CurrentPreset);
            await workbench.RenderPreviewAsync();
            if (workbench.PreviewScenePath?.Value != finalScene ||
                workbench.PreviewImagePath != finalImage ||
                workbench.PreviewIsStale)
                throw new InvalidOperationException(
                    "The final lighting-bound preview was not accepted. " +
                    $"Status: {workbench.Status} Diagnostics: " +
                    string.Join(" | ", workbench.Diagnostics));

            string nifPath = Path.Combine(
                previewRoot,
                "scene-final.nif");
            workbench.ConfigureNifDestination(
                new WorkspacePath(nifPath));
            workbench.ExportSceneNifCommand.Execute(null);
            await WaitSkyGui002Async(
                () => File.Exists(nifPath) &&
                      !workbench.HasActiveOperation,
                "The scene NIF did not complete.",
                TimeSpan.FromSeconds(120));
            byte[] nifBytes = File.ReadAllBytes(nifPath);
            Assert(
                nifBytes.Length > 32 &&
                Encoding.ASCII.GetString(
                        nifBytes,
                        0,
                        Math.Min(64, nifBytes.Length))
                    .StartsWith(
                        "Gamebryo File Format, Version 20.2.0.7",
                        StringComparison.Ordinal),
                "The exported scene NIF did not reopen with the Skyrim header.");
            File.WriteAllBytes(
                nifPath + ".evidence.json",
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        schemaVersion = 1,
                        nifPath,
                        nifSha256 = HashAcceptanceFile(nifPath),
                        scenePath = finalScene,
                        sceneSourceSha256 =
                            HashAcceptanceFile(finalScene),
                        runtimeAuthority = false
                    },
                    SkyGui002JsonOptions));
            checks["sceneNifExport"] = true;

            string catalogPath = Path.Combine(
                outputRoot,
                "catalog.json");
            WriteSkyGui002Catalog(
                catalogPath,
                workbench);

            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath(labRootText),
                new WorkspacePath("F:\\ExampleGame"));
            var settingsService =
                new SkyrimMainWorkspaceSettingsService(
                    policy,
                    new WorkspacePath(labRootText),
                    new WorkspacePath(projectRoot));
            string settingsPath = Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "npc-studio-settings",
                "main-workspace.json");
            if (File.Exists(settingsPath))
            {
                SkyrimMainWorkspaceSettingsLoadResult loadedSettings =
                    await settingsService.LoadAsync(
                        CancellationToken.None);
                Assert(
                    loadedSettings.LoadedFromDisk &&
                    loadedSettings.Sha256 is not null &&
                    SkyGui002SettingsEquivalent(
                        loadedSettings.Settings,
                        workbench.CurrentSettings),
                    "The retained production workbench settings did not reopen exactly.");
            }
            else
            {
                SkyrimMainWorkspaceSettingsSaveResult savedSettings =
                    await settingsService.SaveAsync(
                        workbench.CurrentSettings,
                        CancellationToken.None);
                Assert(
                    savedSettings.Saved &&
                    savedSettings.Sha256 is not null,
                    "The production workbench settings did not save and reopen.");
            }

            string sessionPath = Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "main-workspace-sessions",
                $"sky-gui-002-20260723-{artifactSuffix}.npc-workspace.json");
            workbench.ConfigureSessionDestination(
                new WorkspacePath(sessionPath));
            await workbench.SaveSessionAsync();
            if (workbench.SessionSha256 is null ||
                !File.Exists(sessionPath))
                throw new InvalidOperationException(
                    "The production session did not save and reopen. " +
                    $"Status: {workbench.Status} Diagnostics: " +
                    string.Join(" | ", workbench.Diagnostics));
            string negativeSession = Path.Combine(
                outputRoot,
                "negative-session-wrong-child-hash.json");
            CreateSkyGui002NegativeSession(
                sessionPath,
                negativeSession);
            checks["settingsAndSession"] = true;

            string cancelScene = Path.Combine(
                previewRoot,
                "cancelled-scene.json");
            string cancelImage = Path.Combine(
                previewRoot,
                "cancelled-scene.png");
            workbench.ConfigurePreview(
                new WorkspacePath(previewManifest),
                manifestHash,
                new WorkspacePath(assetRoot),
                new WorkspacePath(cancelScene),
                new WorkspacePath(cancelImage));
            Task cancelledRender = workbench.RenderPreviewAsync();
            Assert(workbench.HasActiveOperation,
                "The production render did not expose an active operation.");
            window.Close();
            Assert(window.IsVisible,
                "The title close was not blocked while rendering.");
            workbench.CancelCommand.Execute(null);
            await cancelledRender;
            Assert(
                !File.Exists(cancelScene) &&
                !File.Exists(cancelImage) &&
                !workbench.HasActiveOperation &&
                !shell.Message.Contains(
                    "A task is active",
                    StringComparison.Ordinal),
                "Cancellation retained a partial scene/image or stale " +
                "close-blocking status.");
            checks["renderCancellationAndCloseBlock"] = true;

            string recoveredScene = Path.Combine(
                previewRoot,
                "scene-post-cancel.json");
            string recoveredImage = Path.Combine(
                previewRoot,
                "scene-post-cancel.png");
            workbench.ConfigurePreview(
                new WorkspacePath(previewManifest),
                manifestHash,
                new WorkspacePath(assetRoot),
                new WorkspacePath(recoveredScene),
                new WorkspacePath(recoveredImage),
                null,
                null,
                shell.Lighting.CurrentPreset);
            await workbench.RenderPreviewAsync();
            Assert(
                workbench.PreviewScenePath?.Value == recoveredScene &&
                workbench.PreviewImagePath == recoveredImage &&
                !workbench.PreviewIsStale &&
                !workbench.PreviewAuthorityText.StartsWith(
                    "STALE",
                    StringComparison.Ordinal) &&
                workbench.PreviewAuthorityText.Contains(
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    StringComparison.Ordinal) &&
                !shell.Message.Contains(
                    "stale",
                    StringComparison.OrdinalIgnoreCase) &&
                shell.Message.Contains(
                    "accepted",
                    StringComparison.OrdinalIgnoreCase),
                "A fresh accepted preview did not clear the intentional " +
                "post-cancellation stale state in both the workbench and " +
                "global footer.");

            TextBox search = FindVisualDescendant<TextBox>(
                    window,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Main workspace search",
                        StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    "The visible workbench search box is missing.");
            window.Width = 1460;
            window.Height = 840;
            window.UpdateLayout();
            Assert(
                search.Focus() &&
                Math.Abs(window.ActualWidth - 1460) < 4 &&
                Math.Abs(window.ActualHeight - 840) < 4,
                "The production workbench did not retain resize and keyboard focus.");
            checks["resizeAndKeyboard"] = true;
            Assert(
                workbench.RuntimeAuthorityText.Contains(
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    StringComparison.Ordinal) &&
                workbench.RuntimeAuthorityText.Contains(
                    "runtime authority: false",
                    StringComparison.OrdinalIgnoreCase),
                "The final workbench lost its explicit static/runtime boundary.");
            BringSkyGui002AuthorityIntoView(window);
            await PresentSkyGui002Async(window, handle);
            CaptureSkyGui002Window(
                window,
                screenshots[4],
                requireAuthorityVisible: true);
            checks["visibleStaticBoundary"] = true;

            window.Close();
            Assert(!window.IsVisible,
                "The idle production window did not close through its title-bar path.");
            window = null;
            checks["titleClose"] = true;
            return new SkyGui002JourneyResult(
                catalogPath,
                settingsPath,
                sessionPath,
                finalScene,
                finalImage,
                nifPath,
                childProposal,
                childOutput,
                negativeSession,
                checks);
        }
        finally
        {
            if (window is { IsVisible: true })
            {
                if (window.DataContext is WorkspaceShellViewModel shell &&
                    shell.MainWorkspace?.HasActiveOperation == true)
                    shell.MainWorkspace.CancelCommand.Execute(null);
                window.Close();
            }
        }
    }

    private static async Task<string> RenderSkyGui002PreviewAsync(
        SkyrimMainWorkspaceViewModel workbench,
        string manifest,
        Sha256Hash manifestHash,
        string assetRoot,
        string outputRoot,
        string name)
    {
        string scene = Path.Combine(outputRoot, name + ".json");
        string image = Path.Combine(outputRoot, name + ".png");
        workbench.ConfigurePreview(
            new WorkspacePath(manifest),
            manifestHash,
            new WorkspacePath(assetRoot),
            new WorkspacePath(scene),
            new WorkspacePath(image));
        await workbench.RenderPreviewAsync();
        Sha256Hash? acceptedHash =
            workbench.PreviewSceneSha256;
        if (workbench.PreviewScenePath?.Value != scene ||
            workbench.PreviewImagePath != image ||
            acceptedHash is null ||
            !File.Exists(scene) ||
            !File.Exists(image))
            throw new InvalidOperationException(
                $"Preview stage '{name}' was not accepted. " +
                $"Status: {workbench.Status} Diagnostics: " +
                string.Join(" | ", workbench.Diagnostics));
        return acceptedHash?.Value ??
               throw new InvalidDataException(
                   $"Preview stage '{name}' returned no scene hash.");
    }

    private static async Task PresentSkyGui002Async(
        MainWindow window,
        IntPtr handle)
    {
        window.UpdateLayout();
        _ = SkyGui023AcceptanceNative.SetForegroundWindow(handle);
        await window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static void CaptureSkyGui002Window(
        Window window,
        string path,
        bool requireAuthorityVisible = false)
    {
        window.UpdateLayout();
        AssertSkyGui002Viewport(
            window,
            requireAuthorityVisible);
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(
            1,
            (int)Math.Ceiling(
                window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(
            1,
            (int)Math.Ceiling(
                window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        encoder.Save(stream);
        stream.Flush(flushToDisk: true);
    }

    private static void AssertSkyGui002Viewport(
        Window window,
        bool requireAuthorityVisible)
    {
        SkyrimMainWorkspacePanel panel =
            FindVisualDescendant<SkyrimMainWorkspacePanel>(
                window,
                _ => true) ??
            throw new InvalidOperationException(
                "The integrated main-workspace panel is not rendered.");
        FrameworkElement viewport =
            FindVisualDescendant<FrameworkElement>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The main-workspace viewport is not rendered.");
        ListBox records =
            FindVisualDescendant<ListBox>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "NPC and leveled NPC record browser",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The main-workspace record browser is not rendered.");
        ScrollViewer previewViewport =
            FindVisualDescendant<ScrollViewer>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace preview viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The preview column viewport is not rendered.");
        ScrollViewer detailsViewport =
            FindVisualDescendant<ScrollViewer>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace details viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The details column viewport is not rendered.");
        FrameworkElement details =
            FindVisualDescendant<FrameworkElement>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace selected details",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The exact-selection pane is not rendered.");
        TextBlock authority =
            FindVisualDescendant<TextBlock>(
                panel,
                item => item.Text.Contains(
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The static/runtime authority boundary is not rendered.");
        Point detailsRight = details
            .TransformToAncestor(panel)
            .Transform(new Point(details.ActualWidth, 0));
        Point authorityBottom = authority
            .TransformToAncestor(panel)
            .Transform(new Point(0, authority.ActualHeight));
        Assert(
            viewport is Grid &&
            records.ActualHeight > 0 &&
            records.ActualHeight <= panel.ActualHeight + 1 &&
            previewViewport.ActualHeight > 0 &&
            detailsViewport.ActualHeight > 0 &&
            previewViewport.ScrollableWidth <= 0.5 &&
            detailsViewport.ScrollableWidth <= 0.5 &&
            details.ActualWidth >= 240 &&
            detailsRight.X <= panel.ActualWidth + 1 &&
            authority.ActualHeight > 0 &&
            (!requireAuthorityVisible ||
             (authorityBottom.Y >= authority.ActualHeight &&
              authorityBottom.Y <= panel.ActualHeight + 1)),
            "The canonical screenshot viewport clips the exact-selection " +
            "pane or hides the static/runtime authority boundary. " +
            $"recordsHeight={records.ActualHeight:F2}; " +
            $"previewScrollableWidth={previewViewport.ScrollableWidth:F2}; " +
            $"detailsScrollableWidth={detailsViewport.ScrollableWidth:F2}; " +
            $"detailsWidth={details.ActualWidth:F2}; " +
            $"detailsRight={detailsRight.X:F2}; " +
            $"panelWidth={panel.ActualWidth:F2}; " +
            $"authorityBottom={authorityBottom.Y:F2}; " +
            $"panelHeight={panel.ActualHeight:F2}.");
    }

    private static void BringSkyGui002AuthorityIntoView(
        Window window)
    {
        SkyrimMainWorkspacePanel panel =
            FindVisualDescendant<SkyrimMainWorkspacePanel>(
                window,
                _ => true) ??
            throw new InvalidOperationException(
                "The integrated main-workspace panel is not rendered.");
        TextBlock authority =
            FindVisualDescendant<TextBlock>(
                panel,
                item => item.Text.Contains(
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The static/runtime authority boundary is not rendered.");
        FrameworkElement details =
            FindVisualDescendant<FrameworkElement>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace selected details",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The exact-selection pane is not rendered.");
        ScrollViewer? detailsScroller =
            FindVisualDescendant<ScrollViewer>(
                details,
                _ => true);
        authority.BringIntoView();
        detailsScroller?.ScrollToEnd();
        window.UpdateLayout();
    }

    private static void AssertRoute(
        MainWindow window,
        ICommand command,
        TabItem expected,
        string route)
    {
        Assert(command.CanExecute(null),
            $"{route} route was unexpectedly disabled.");
        command.Execute(null);
        Assert(
            ReferenceEquals(
                window.MainTasks.SelectedItem,
                expected),
            $"{route} route did not select its exact production tab.");
    }

    private static async Task WaitSkyGui002Async(
        Func<bool> predicate,
        string failure,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (predicate()) return;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(failure);
    }

    private static void ExerciseSkyGui002PackagedLifecycle(
        string executable)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException(
            "The self-contained NPC Manager desktop did not start.");
        WaitUntil(
            () =>
            {
                process.Refresh();
                return process.MainWindowHandle != IntPtr.Zero;
            },
            "The self-contained NPC Manager desktop produced no window.",
            TimeSpan.FromSeconds(30));
        _ = process.WaitForInputIdle(10_000);
        AutomationElement root =
            AutomationElement.FromHandle(
                process.MainWindowHandle);
        var pattern = (WindowPattern)root.GetCurrentPattern(
            WindowPattern.Pattern);
        pattern.Close();
        Assert(
            process.WaitForExit(15_000) &&
            process.ExitCode == 0,
            "The self-contained desktop did not close normally.");
    }

    private static ReviewedGameIntake CreateSkyGui002Intake(
        string intakeRoot,
        string dataRoot,
        string outputRoot)
    {
        string skyrim = Path.Combine(dataRoot, "Skyrim.esm");
        string actors = Path.Combine(dataRoot, "Actors.esp");
        string patch = Path.Combine(dataRoot, "ActorsPatch.esp");
        string loadOrder = Path.Combine(
            intakeRoot,
            "load-order.json");
        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(intakeRoot),
            new WorkspacePath(dataRoot),
            new WorkspacePath(loadOrder),
            new WorkspacePath(outputRoot),
            HashAsDomain(loadOrder),
            [
                SkyGui002Entry(
                    "Skyrim.esm",
                    0,
                    skyrim,
                    []),
                SkyGui002Entry(
                    "Actors.esp",
                    1,
                    actors,
                    [new PluginName("Skyrim.esm")]),
                SkyGui002Entry(
                    "ActorsPatch.esp",
                    2,
                    patch,
                    [
                        new PluginName("Skyrim.esm"),
                        new PluginName("Actors.esp")
                    ])
            ],
            [],
            [],
            [],
            6,
            HashAsDomain(Path.Combine(
                intakeRoot,
                "source-manifest.json")),
            HashAsDomain(Path.Combine(
                intakeRoot,
                "source-manifest.json")),
            false);
    }

    private static PluginClosureReviewEntry SkyGui002Entry(
        string plugin,
        int order,
        string path,
        ImmutableArray<PluginName> masters) =>
        new(
            new PluginName(plugin),
            order,
            true,
            true,
            true,
            order != 2,
            true,
            new WorkspacePath(path),
            HashAsDomain(path),
            masters);

    private static void WriteSkyGui002Catalog(
        string path,
        SkyrimMainWorkspaceViewModel workbench)
    {
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    artifactKind = "sky-gui-002-catalog",
                    runtimeAuthority = false,
                    records = workbench.Rows.Select(row => new
                    {
                        owner = row.Identity.OwnerPlugin.Value,
                        winner =
                            row.Identity.WinningProvider.Value,
                        formId = row.Identity.FormId.ToString(),
                        signature = row.Identity.Signature,
                        editorId = row.Source.EditorId,
                        name = row.Source.Name,
                        rawRecordSha256 =
                            row.Source.RawRecordSha256,
                        categories = row.Source.Categories
                            .Select(item => item.ToString())
                            .ToArray(),
                        sex = row.Source.Sex?.ToString(),
                        entries = row.Source.LeveledNpcEntries
                            .Select(item => new
                            {
                                owner =
                                    item.OwnerPlugin.Value,
                                winner =
                                    item.WinningProvider.Value,
                                formId =
                                    item.FormId.ToString(),
                                signature = item.Signature
                            })
                            .ToArray(),
                        changed = row.IsChanged,
                        deletePending =
                            row.IsDeletePending
                    }).ToArray()
                },
                SkyGui002JsonOptions));
    }

    private static void CreateSkyGui002NegativeSession(
        string sessionPath,
        string negativePath)
    {
        JsonNode root = JsonNode.Parse(
                            File.ReadAllBytes(sessionPath)) ??
                        throw new InvalidDataException(
                            "The accepted session is empty.");
        JsonArray artifacts =
            root["artifacts"]?.AsArray() ??
            throw new InvalidDataException(
                "The accepted session has no artifacts array.");
        JsonObject artifact =
            artifacts.Single()?.AsObject() ??
            throw new InvalidDataException(
                "The accepted session must contain exactly one artifact.");
        artifact["sha256"] = new string('0', 64);
        File.WriteAllText(
            negativePath,
            root.ToJsonString(SkyGui002JsonOptions));
    }

    private static void RunSkyGui002Verifier(
        string verifier,
        string sourceManifest,
        string catalog,
        string session,
        string settings,
        string scene,
        string image,
        string nif,
        string report)
    {
        var start = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            verifier,
            "--source-manifest", sourceManifest,
            "--catalog", catalog,
            "--session", session,
            "--settings", settings,
            "--scene", scene,
            "--image", image,
            "--nif", nif,
            "--expected-owner", "Actors.esp",
            "--expected-winner", "ActorsPatch.esp",
            "--expected-npc", "0x00000800",
            "--expected-lvln", "0x00000900",
            "--report", report
        ];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
                                throw new InvalidOperationException(
                                    "The SKY-GUI-002 verifier did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException(
                $"The SKY-GUI-002 verifier failed ({process.ExitCode}): {output} {error}");
    }

    private static string ReadSceneVariant(string scenePath)
    {
        using JsonDocument scene = JsonDocument.Parse(
            File.ReadAllBytes(scenePath));
        return scene.RootElement.GetProperty("variant")
            .GetProperty("id")
            .GetString() ?? string.Empty;
    }

    private static bool SkyGui002SettingsEquivalent(
        SkyrimMainWorkspaceSettings left,
        SkyrimMainWorkspaceSettings right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.Filter.Search == right.Filter.Search &&
        left.Filter.ShowNpcs == right.Filter.ShowNpcs &&
        left.Filter.ShowLeveledNpcs ==
            right.Filter.ShowLeveledNpcs &&
        left.Filter.Categories.SetEquals(
            right.Filter.Categories) &&
        left.Filter.Gender == right.Filter.Gender &&
        left.Filter.ChangedOnly == right.Filter.ChangedOnly &&
        left.Filter.IncludeDeleted ==
            right.Filter.IncludeDeleted &&
        left.Preview == right.Preview;

    private static Sha256Hash HashAsDomain(string path) =>
        new(HashAcceptanceFile(path));

    private static object PreviewInput(
        string category,
        string relative,
        string dataRoot) =>
        new
        {
            category,
            path = relative,
            provider = "copied:Briar.esp",
            sha256 = HashAcceptanceFile(Path.Combine(
                dataRoot,
                relative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)))
        };

    private static void RequireFile(
        string path,
        string role)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{role} is absent.",
                path);
    }

    private static void CopyFresh(
        string source,
        string destination)
    {
        RequireFile(source, "Connected source");
        if (File.Exists(destination) ||
            Directory.Exists(destination))
            throw new IOException(
                $"Connected destination is not fresh: {destination}");
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination) ??
            throw new InvalidDataException(
                "Connected destination has no parent."));
        File.Copy(source, destination, overwrite: false);
    }

    private static void WriteSkyGui002Master(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(
            Path.GetFileName(path));
        WriteSkyGui002Mod(
            new SkyrimMod(key, SkyrimRelease.SkyrimSE),
            path);
    }

    private static void WriteSkyGui002Actors(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(
            Path.GetFileName(path));
        ModKey skyrim =
            ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = skyrim });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogFemale",
            Name = "Catalog Female",
            Weight = 55F,
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            }
        });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x801),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogMale",
            Name = "Catalog Male",
            Weight = 45F,
            Configuration = new NpcConfiguration()
        });
        mod.LeveledNpcs.Add(SkyGui002List(
            new FormKey(key, 0x900),
            "CatalogActors",
            [
                new FormKey(key, 0x800),
                new FormKey(key, 0x801)
            ]));
        mod.LeveledNpcs.Add(SkyGui002List(
            new FormKey(key, 0x901),
            "CatalogActorsEmpty",
            []));
        WriteSkyGui002Mod(mod, path);
    }

    private static void WriteSkyGui002Patch(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(
            Path.GetFileName(path));
        ModKey skyrim =
            ModKey.FromNameAndExtension("Skyrim.esm");
        ModKey actors =
            ModKey.FromNameAndExtension("Actors.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = skyrim });
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = actors });
        mod.Npcs.Add(new Npc(
            new FormKey(actors, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogFemale",
            Name = "Catalog Female Patched",
            Weight = 55F,
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            }
        });
        mod.LeveledNpcs.Add(SkyGui002List(
            new FormKey(actors, 0x900),
            "CatalogActors",
            [
                new FormKey(actors, 0x800),
                new FormKey(actors, 0x801)
            ]));
        WriteSkyGui002Mod(mod, path);
    }

    private static LeveledNpc SkyGui002List(
        FormKey key,
        string editorId,
        ImmutableArray<FormKey> entries)
    {
        var list = new LeveledNpc(
            key,
            SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            Entries = []
        };
        foreach (FormKey entry in entries)
        {
            list.Entries.Add(new LeveledNpcEntry
            {
                Data = new LeveledNpcEntryData
                {
                    Level = 1,
                    Count = 1,
                    Reference =
                        new FormLink<INpcSpawnGetter>(entry)
                }
            });
        }
        return list;
    }

    private static void WriteSkyGui002Mod(
        SkyrimMod mod,
        string path)
    {
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                MastersListOrdering =
                    MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }
}
