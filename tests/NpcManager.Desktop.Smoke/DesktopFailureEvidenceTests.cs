using System.IO;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Desktop.Smoke;

internal static class DesktopFailureEvidenceTests
{
    public static void Run(
        App app,
        WorkspacePath workspaceRoot)
    {
        Task run = RunAsync(app, workspaceRoot);
        var frame = new DispatcherFrame();
        bool timedOut = false;
        var timeout = new DispatcherTimer(
            DispatcherPriority.Send,
            app.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            timedOut = true;
            frame.Continue = false;
        };
        _ = run.ContinueWith(
            _ => app.Dispatcher.BeginInvoke(
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        if (timedOut)
            throw new TimeoutException(
                "Desktop failure evidence smoke timed out after 60 seconds.");
        run.GetAwaiter().GetResult();
    }

    public static async Task RunAsync(
        App app,
        WorkspacePath workspaceRoot)
    {
        string fixtureRoot = Path.Combine(
            workspaceRoot.Value,
            "artifacts",
            "reassessment-work",
            "desktop-failure-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        string? priorProtectedRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.ProtectedRootEnvironmentVariable);
        MainWindow? window = null;
        try
        {
            DesktopFailureEvidenceListener listener = app.FailureEvidence ??
                throw new InvalidOperationException(
                    "The admitted desktop startup did not attach its failure listener.");
            using Activity activity = new Activity(
                    "Actorwright.Desktop.Smoke.FailureEvidence")
                .SetIdFormat(ActivityIdFormat.W3C)
                .Start();

            NpcVisualPreviewRenderResult processFailure =
                await RenderSyntheticFailureAsync(
                    fixtureRoot,
                    exitCode: 17,
                    diagnosticCode: "npc-preview-process-failed")
                    .ConfigureAwait(false);
            Require(!processFailure.Rendered &&
                    processFailure.Diagnostics.Any(item =>
                        item.Code == "npc-preview-process-failed"),
                "synthetic-Blender-nonzero-exit-remains-a-handled-refusal");

            WorkspacePath evidenceRoot = ActorwrightWorkspace.WorkRoot(
                workspaceRoot,
                "desktop-failure-evidence");
            string[] files = Directory.GetFiles(
                evidenceRoot.Value,
                "failure-*.json");
            Require(files.Length == 1,
                "default-desktop-listener-persists-process-failure");
            using (JsonDocument processRecord = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(files[0])
                           .ConfigureAwait(false)))
            {
                JsonElement record = processRecord.RootElement;
                string[] expectedProperties =
                [
                    "schemaVersion",
                    "artifactKind",
                    "timestampUtc",
                    "traceId",
                    "processId",
                    "threadId",
                    "operation",
                    "stage",
                    "failureKind",
                    "diagnosticCodes",
                    "processIdentity",
                    "executableSha256",
                    "exitCode"
                ];
                string[] actualProperties = record
                    .EnumerateObject()
                    .Select(item => item.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                string recordText = record.GetRawText();
                Require(actualProperties.SequenceEqual(
                            expectedProperties.Order(StringComparer.Ordinal),
                            StringComparer.Ordinal) &&
                        record.GetProperty("traceId").GetString() ==
                            activity.TraceId.ToString() &&
                        record.GetProperty("operation").GetString() ==
                            "blenderNpcVisualPreview" &&
                        record.GetProperty("stage").GetString() == "process" &&
                        record.GetProperty("failureKind").GetString() ==
                            "processExit" &&
                        record.GetProperty("processIdentity").GetString() ==
                            "blender" &&
                        record.GetProperty("executableSha256").GetString() ==
                            HashFile(Path.Combine(
                                fixtureRoot,
                                "tools",
                                "blender.exe")).Value &&
                        record.GetProperty("exitCode").GetInt32() == 17 &&
                        record.GetProperty("diagnosticCodes")
                            .EnumerateArray()
                            .Any(item => item.GetString() ==
                                "npc-preview-process-failed") &&
                        !recordText.Contains("SECRET_STDOUT", StringComparison.Ordinal) &&
                        !recordText.Contains("SECRET_STDERR", StringComparison.Ordinal),
                    "process-failure-record-is-correlated-typed-and-path-free");
            }

            string[] beforeHandledBuild = Directory.GetFiles(
                evidenceRoot.Value,
                "failure-*.json");
            string providerRoot = Path.Combine(fixtureRoot, "provider");
            await NpcManager.Gate1.Tests.Program
                .EmitDesktopSmokeProviderFixtureAsync(providerRoot)
                .ConfigureAwait(false);
            JsonObject workspaceManifest = JsonNode.Parse(
                    await File.ReadAllTextAsync(Path.Combine(
                        providerRoot, "provider-manifest.json"))
                        .ConfigureAwait(false))!
                .AsObject();
            string relativeProviderRoot = Path.GetRelativePath(
                    workspaceRoot.Value, providerRoot)
                .Replace(Path.DirectorySeparatorChar, '/');
            JsonObject template = workspaceManifest["template"]!.AsObject();
            template["path"] = $"{relativeProviderRoot}/{template["path"]!.GetValue<string>()}";
            JsonObject faceGeom = workspaceManifest["faceGeom"]!.AsObject();
            faceGeom["path"] = $"{relativeProviderRoot}/{faceGeom["path"]!.GetValue<string>()}";
            JsonObject faceTint = workspaceManifest["faceTint"]!.AsObject();
            faceTint["manifestPath"] = $"{relativeProviderRoot}/{faceTint["manifestPath"]!.GetValue<string>()}";
            faceTint["providerRoot"] = $"{relativeProviderRoot}/{faceTint["providerRoot"]!.GetValue<string>()}";
            JsonObject dependencies = workspaceManifest["dependencies"]!.AsObject();
            dependencies["manifestPath"] = $"{relativeProviderRoot}/{dependencies["manifestPath"]!.GetValue<string>()}";
            string workspaceManifestPath = Path.Combine(
                fixtureRoot, "workspace-provider-manifest.json");
            await File.WriteAllTextAsync(
                    workspaceManifestPath,
                    workspaceManifest.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                    new UTF8Encoding(false))
                .ConfigureAwait(false);
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
            BlankNpcDesktopContext desktop = BlankNpcDesktopComposition.Create(
                policy, workspaceRoot);
            var viewModel = app.Dispatcher.Invoke(() =>
                new CreateNpcViewModel(
                    new FailedBlankNpcBuildService(),
                    desktop.InitialRequest,
                    desktop.ProviderSelector));
            string[] handledPaths;
            try
            {
                app.Dispatcher.Invoke(() => Require(
                    !viewModel.CreateCommand.CanExecute(null) &&
                    viewModel.ProviderSelectionStatus.Contains(
                        "No appearance provider is selected",
                        StringComparison.Ordinal),
                    "blank-NPC-build-waits-for-provider-admission"));
                Task providerSelection = app.Dispatcher.Invoke(() =>
                    viewModel.SelectProviderManifestAsync(new WorkspacePath(
                        workspaceManifestPath)));
                await providerSelection.ConfigureAwait(false);
                app.Dispatcher.Invoke(() => Require(
                    viewModel.CreateCommand.CanExecute(null) &&
                    viewModel.ProviderSelectionStatus.Contains(
                        "passed admission", StringComparison.Ordinal),
                    "synthetic-provider-is-admitted-through-desktop-selection: " +
                    viewModel.ProviderSelectionStatus + "; diagnostics=" +
                    string.Join(" | ", viewModel.Diagnostics)));

                handledPaths = app.Dispatcher.Invoke(() =>
                {
                    viewModel.CreateCommand.Execute(null);
                    Require(!viewModel.IsBusy &&
                            viewModel.Diagnostics.Any(item =>
                                item.StartsWith(
                                    "blank-npc-test-refused:",
                                    StringComparison.Ordinal)),
                        "blank-NPC-viewmodel-shows-handled-build-failure");
                    return Directory.GetFiles(
                        evidenceRoot.Value,
                        "failure-*.json");
                });
            }
            finally
            {
                app.Dispatcher.Invoke(viewModel.Dispose);
            }
            Require(handledPaths.Length == beforeHandledBuild.Length + 1,
                "handled-desktop-build-failure-persists-from-viewmodel");
            string handledPath = handledPaths.Single(path =>
                !beforeHandledBuild.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase));
            using (JsonDocument handledRecord = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(handledPath)
                           .ConfigureAwait(false)))
            {
                Require(handledRecord.RootElement.GetProperty("operation")
                            .GetString() == "blankNpcBuild" &&
                        handledRecord.RootElement.GetProperty("stage")
                            .GetString() == "build" &&
                        handledRecord.RootElement.GetProperty("failureKind")
                            .GetString() == "handledResult" &&
                        handledRecord.RootElement.GetProperty("diagnosticCodes")
                            .EnumerateArray()
                            .Single()
                            .GetString() == "blank-npc-test-refused",
                    "handled-build-record-retains-only-diagnostic-codes");
            }

            string[] beforeMalformedDiagnostics = Directory.GetFiles(
                evidenceRoot.Value,
                "failure-*.json");
            DesktopEvidenceStoreWriteResult malformedDiagnostics =
                listener.WriteOperationFailure(
                    activity,
                    ActorwrightObservabilityEventSource.DesktopFailureOperationId.BlankNpcBuild,
                    ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                    ThrowAfterOneDiagnostic());
            Require(!malformedDiagnostics.Written &&
                    Directory.GetFiles(evidenceRoot.Value, "failure-*.json")
                        .Length == beforeMalformedDiagnostics.Length,
                "diagnostic-enumeration-failure-does-not-escape-or-write");

            window = app.Dispatcher.Invoke(() =>
                new MainWindow(workspaceRoot, []));
            app.Dispatcher.Invoke(() => app.MainWindow = window);
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                evidenceRoot.Value);
            _ = await RenderSyntheticFailureAsync(
                    fixtureRoot,
                    exitCode: 17,
                    diagnosticCode: "npc-preview-process-failed")
                .ConfigureAwait(false);
            app.Dispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
            string message = app.Dispatcher.Invoke(() =>
                ((WorkspaceShellViewModel)window.DataContext).Message);
            Require(message ==
                    "Desktop failure evidence could not be safely saved.",
                "process-only-persistence-refusal-is-shown-in-desktop-status");
            Require(Directory.GetFiles(evidenceRoot.Value, "failure-*.json")
                    .Length == beforeMalformedDiagnostics.Length,
                "configured-protected-evidence-root-is-not-written");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                priorProtectedRoot);
            app.Dispatcher.Invoke(() =>
            {
                app.MainWindow = null!;
                window?.Close();
            });
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async Task<NpcVisualPreviewRenderResult>
        RenderSyntheticFailureAsync(
            string root,
            int exitCode,
            string diagnosticCode)
    {
        WorkspacePath blender = WriteFile(
            root,
            "tools/blender.exe",
            "controlled Blender fixture");
        WorkspacePath texconv = WriteFile(
            root,
            "tools/texconv.exe",
            "controlled Texconv fixture");
        WorkspacePath profile = new(Path.Combine(root, "profile"));
        Directory.CreateDirectory(profile.Value);
        ApplicationResourcePath profileManifest = new(Path.Combine(
            root,
            "application-resources",
            "npc-preview-profile-manifest.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(
            profileManifest.Value)!);
        await File.WriteAllTextAsync(
            profileManifest.Value,
            "{\"schemaVersion\":1,\"fixture\":true}",
            new UTF8Encoding(false)).ConfigureAwait(false);

        WorkspacePath output = CreateOutput(root, out NpcVisualSourceGraph source);
        EmbeddedBlenderScript script = EmbeddedBlenderScriptBundle.Load(
            "render_npc_preview_bundle");
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(root),
            new WorkspacePath(@"F:\ExampleGame"));
        var renderer = new BlenderNpcVisualPreviewRenderer(
            blender,
            profile,
            profileManifest,
            HashFile(profileManifest.Value),
            script.Id,
            texconv,
            policy,
            new WorkspacePath(root),
            HashFile(blender.Value),
            script.Sha256,
            HashFile(texconv.Value),
            new FailedProcessRunner(exitCode, diagnosticCode));
        return await Task.Run(async () =>
            await renderer.RenderAsync(
                new NpcVisualPreviewRenderRequest(
                    "npc-preview-scene/2",
                    source,
                    output,
                    new NpcVisualPreviewOptions()),
                CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static WorkspacePath CreateOutput(
        string root,
        out NpcVisualSourceGraph source)
    {
        WorkspacePath output = new(Path.Combine(
            root,
            "output-" + Guid.NewGuid().ToString("N")));
        string faceGeom = Path.Combine(
            output.Value,
            "assets",
            "Data",
            "meshes",
            "actors",
            "character",
            "FaceGenData",
            "FaceGeom",
            "Fixture.esp",
            "00000800.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeom)!);
        File.WriteAllText(
            faceGeom,
            "controlled FaceGeom fixture",
            new UTF8Encoding(false));
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName("Fixture.esp"),
            new PluginName("Fixture.esp"),
            new FormId(0x800),
            "NPC_");
        source = new NpcVisualSourceGraph(
            NpcVisualPreviewRoute.Cotr,
            identity,
            NpcSex.Female,
            50,
            "FixtureRace",
            "#101010",
            "#F0D0C0",
            [
                new NpcVisualAsset(
                    NpcVisualAssetRole.FaceGeom,
                    new AssetPath(
                        "meshes/actors/character/FaceGenData/FaceGeom/Fixture.esp/00000800.nif"),
                    "Fixture.esp",
                    HashFile(faceGeom),
                    new FileInfo(faceGeom).Length,
                    new WorkspacePath(faceGeom),
                    false,
                    [])
            ],
            [],
            false,
            []);
        return output;
    }

    private static WorkspacePath WriteFile(
        string root,
        string relative,
        string contents)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return new WorkspacePath(path);
    }

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static IEnumerable<string> ThrowAfterOneDiagnostic()
    {
        yield return "npc-build-preflight-input";
        throw new InvalidOperationException(
            "fixture diagnostic enumerator failure");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"Desktop failure evidence assertion failed: {name}.");
    }

    private sealed class FailedProcessRunner(
        int exitCode,
        string diagnosticCode) : INpcVisualPreviewProcessRunner
    {
        public ValueTask<NpcVisualPreviewProcessResult> RunAsync(
            WorkspacePath executable,
            ImmutableArray<string> arguments,
            WorkspacePath? blenderProfile,
            string? standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualPreviewProcessResult(
                    exitCode,
                    new NpcVisualPreviewCapturedStream(
                        "SECRET_STDOUT",
                        false),
                    new NpcVisualPreviewCapturedStream(
                        "SECRET_STDERR",
                        false),
                    [new Diagnostic(
                        diagnosticCode,
                        DiagnosticSeverity.Error,
                        "Controlled renderer failed.")]));
        }
    }

    private sealed class FailedBlankNpcBuildService : IBlankNpcBuildService
    {
        public ValueTask<BlankNpcBuildResult> ExecuteAsync(
            BlankNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BlankNpcBuildResult(
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                [new Diagnostic(
                    "blank-npc-test-refused",
                    DiagnosticSeverity.Error,
                    "Controlled build failure.")]));
    }
}
