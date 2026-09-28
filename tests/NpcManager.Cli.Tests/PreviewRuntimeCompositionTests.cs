using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class PreviewRuntimeCompositionTests
{
    private static readonly (string Path, string Role)[] Assets =
    [
        ("libmediapipe.dll", "native-c-api"),
        ("opencv_world3410.dll", "opencv-runtime"),
        ("concrt140.dll", "vc-runtime-concurrency"),
        ("msvcp140.dll", "vc-runtime-cpp"),
        ("vcruntime140.dll", "vc-runtime-core"),
        ("vcruntime140_1.dll", "vc-runtime-core-1"),
        ("blaze_face_short_range.tflite", "face-detector-model"),
        ("face_landmarker.task", "face-landmarker-model")
    ];

    public static async Task RunAsync()
    {
        await LocatorRejectsUnavailableAndDriftedClosureAsync();
        await LeaseDisposesOwnedStateAsync();
        await ReferencePresetHandlerCreatesFactoryOnlyAfterValidationAsync();
        await NpcHandlerCreatesFactoryOnlyAfterValidationAsync();
        await HairHandlerCreatesFactoryOnlyAfterValidationAsync();
    }

    private static async Task
        ReferencePresetHandlerCreatesFactoryOnlyAfterValidationAsync()
    {
        var factory = new CountingReferencePresetFactory();
        IReferencePresetAuthoringTransaction transaction =
            ReferencePresetCliComposition.CreateLazy(factory);
        var handler = new ReferencePresetCommandHandler(
            transaction,
            null!,
            null!,
            new StringWriter(),
            new StringWriter());

        CommandExitCode invalid = await handler.RunAsync(
            CommandLine.Parse(
                ["npc", "create-from-reference", "--json"]),
            CancellationToken.None);
        Assert(
            invalid == CommandExitCode.UsageError &&
            factory.CreateCount == 0,
            "Invalid reference-preset arguments admitted the packaged runtime.");
    }

    private static async Task HairHandlerCreatesFactoryOnlyAfterValidationAsync()
    {
        string root = NewRoot("lazy-hair-handler");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new CountingHairFactory();
            var output = new StringWriter();
            var error = new StringWriter();
            WorkspacePath workspace = new(root);
            FaceGeomHairRegionsCommandHandler handler =
                FaceGeomHairRegionsCliComposition.Create(
                    workspace,
                    factory,
                    output,
                    error);
            CommandExitCode invalid = await handler.RunAsync(
                CommandLine.Parse(
                    ["facegen", "hair-regions", "preview", "--json"]),
                CancellationToken.None);
            Assert(
                invalid == CommandExitCode.UsageError &&
                factory.CreateCount == 0,
                "Invalid HairTint preview arguments constructed the preview runtime.");
        }
        finally
        {
            Delete(root);
        }
    }

    private static async Task LocatorRejectsUnavailableAndDriftedClosureAsync()
    {
        string root = NewRoot("locator");
        Directory.CreateDirectory(root);
        try
        {
            var locator = new ApplicationResourceRuntimeLocator(
                new ApplicationResourcePath(root));
            ApplicationResourceRuntimeAdmissionResult missing =
                locator.AdmitReferencePresetRuntime();
            Assert(
                !missing.Accepted &&
                missing.Diagnostics.Any(item =>
                    item.Code == "reference-runtime-unavailable" &&
                    item.Message.Contains(
                        Path.Combine(root, "runtime", "reference-preset"),
                        StringComparison.OrdinalIgnoreCase)),
                "The locator did not return the shared typed missing-runtime cause and expected path.");

            string runtime = Path.Combine(
                root, "runtime", "reference-preset");
            Directory.CreateDirectory(runtime);
            WriteSyntheticRuntime(runtime, "windows-x64");
            ApplicationResourceRuntimeAdmissionResult admitted =
                locator.AdmitReferencePresetRuntime();
            Assert(
                admitted.Accepted &&
                admitted.Authority is not null &&
                admitted.Authority.Assets.Length == Assets.Length,
                "The locator did not admit an exact manifest closure.");

            File.AppendAllText(
                Path.Combine(runtime, Assets[0].Path),
                "drift");
            ApplicationResourceRuntimeAdmissionResult drifted =
                locator.AdmitReferencePresetRuntime();
            Assert(
                !drifted.Accepted &&
                drifted.Diagnostics.Any(item =>
                    item.Code == "reference-runtime-unavailable"),
                "The locator accepted runtime length/hash drift.");

            Directory.Delete(runtime, recursive: true);
            Directory.CreateDirectory(runtime);
            WriteSyntheticRuntime(runtime, "linux-x64");
            ApplicationResourceRuntimeAdmissionResult wrongArchitecture =
                locator.AdmitReferencePresetRuntime();
            Assert(
                !wrongArchitecture.Accepted &&
                wrongArchitecture.Diagnostics.Any(item =>
                    item.Message.Contains(
                        "architecture",
                        StringComparison.OrdinalIgnoreCase)),
                "The locator accepted a wrong-architecture runtime.");
        }
        finally
        {
            Delete(root);
        }

        await Task.CompletedTask;
    }

    private static async Task LeaseDisposesOwnedStateAsync()
    {
        string root = NewRoot("lease");
        Directory.CreateDirectory(root);
        var disposable = new ObservedDisposable();
        await using (var lease = new PreviewServiceLease<object>(
                         new object(),
                         [disposable],
                         new WorkspacePath(root)))
        {
            Assert(
                ReferenceEquals(lease.Service, lease.Service),
                "The preview lease did not expose its service.");
        }
        Assert(
            disposable.Disposed && !Directory.Exists(root),
            "The preview lease did not dispose native state and remove its exact owned work root.");
    }

    private static async Task NpcHandlerCreatesFactoryOnlyAfterValidationAsync()
    {
        string root = NewRoot("lazy-handler");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new CountingComposerFactory();
            var output = new StringWriter();
            var error = new StringWriter();
            var labRoot = new WorkspacePath(root);
            var handler = new NpcVisualPreviewCommandHandler(
                factory,
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot,
                new FaceGeomHairRegionsDocumentCodec(labRoot),
                output,
                error);

            CommandExitCode invalid = await handler.RunAsync(
                CommandLine.Parse(["preview", "npc", "--json"]),
                CancellationToken.None);
            Assert(
                invalid == CommandExitCode.UsageError &&
                factory.CreateCount == 0,
                "Invalid preview arguments constructed the preview runtime.");

            WorkspacePath intake = WriteIntake(root);
            CommandExitCode valid = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intake.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x800",
                    "--output-root", Path.Combine(root, "output"),
                    "--json"
                ]),
                CancellationToken.None);
            Assert(
                valid == CommandExitCode.ValidationFailure &&
                factory.CreateCount == 1 &&
                factory.DisposeCount == 1,
                "A valid preview action did not acquire and dispose exactly one lazy lease.");
        }
        finally
        {
            Delete(root);
        }
    }

    private static void WriteSyntheticRuntime(
        string runtime,
        string architecture)
    {
        var rows = new List<object>();
        for (var index = 0; index < Assets.Length; index++)
        {
            byte[] bytes = [(byte)(index + 1), (byte)(index + 11)];
            string path = Path.Combine(runtime, Assets[index].Path);
            File.WriteAllBytes(path, bytes);
            rows.Add(new
            {
                path = Assets[index].Path,
                role = Assets[index].Role,
                length = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
        }

        File.WriteAllText(
            Path.Combine(runtime, "runtime-asset-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runtimeArchitecture = architecture,
                offline = true,
                cpuOnly = true,
                assets = rows
            }));
    }

    private static WorkspacePath WriteIntake(string root)
    {
        string data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        string plugin = Path.Combine(data, "Fixture.esp");
        File.WriteAllBytes(plugin, [1, 2, 3]);
        string loadOrder = Path.Combine(root, "loadorder.txt");
        File.WriteAllText(loadOrder, "Fixture.esp");
        string output = Path.Combine(root, "reviewed-output");
        WorkspacePath workspaceRoot = new(root);
        WorkspacePath dataRoot = new(data);
        WorkspacePath loadOrderWorkspacePath = new(loadOrder);
        WorkspacePath outputRoot = new(output);
        Sha256Hash loadOrderHash = new(Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(loadOrder))));
        Sha256Hash sourceHash = new(Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(plugin))));
        Sha256Hash assetFingerprint = new(new string('2', 64));
        var plugins = ImmutableArray.Create(new PluginClosureReviewEntry(
            new PluginName("Fixture.esp"),
            0,
            true,
            true,
            true,
            false,
            true,
            new WorkspacePath(plugin),
            sourceHash,
            []));
        Sha256Hash intakeFingerprint =
            ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                GameEdition.SkyrimSpecialEdition,
                workspaceRoot,
                dataRoot,
                loadOrderWorkspacePath,
                outputRoot,
                loadOrderHash,
                plugins,
                [],
                [],
                [],
                assetFingerprint);
        var path = new WorkspacePath(Path.Combine(root, "intake.json"));
        File.WriteAllText(
            path.Value,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                edition = "skyrimse",
                isAccepted = true,
                runtimeAuthority = false,
                workspaceRoot = workspaceRoot.Value,
                dataRoot = data,
                loadOrderPath = loadOrder,
                outputRoot = output,
                loadOrderHash = loadOrderHash.Value,
                assetIndexFingerprint = assetFingerprint.Value,
                intakeFingerprint = intakeFingerprint.Value,
                plugins = new[]
                {
                    new
                    {
                        plugin = "Fixture.esp",
                        order = 0,
                        active = true,
                        requested = true,
                        requiredMaster = false,
                        sourceHash = sourceHash.Value,
                        masters = Array.Empty<string>()
                    }
                },
                bodySidecarCount = 0,
                generatedPluginCount = 0,
                generatedSidecarCount = 0,
                assetProviderCount = 1,
                diagnostics = Array.Empty<object>()
            }));
        return path;
    }

    private static string NewRoot(string label) => Path.Combine(
        "K:\\Actorwright",
        ".tmp",
        "preview-runtime-tests",
        $"{label}-{Guid.NewGuid():N}");

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ObservedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class CountingComposerFactory :
        IPreviewServiceFactory<INpcVisualPreviewComposer>
    {
        public int CreateCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(
                    new RefusingComposer(),
                    [new CallbackDisposable(() => DisposeCount++)]));
        }
    }

    private sealed class CountingReferencePresetFactory :
        IPreviewServiceFactory<IReferencePresetAuthoringTransaction>
    {
        public int CreateCount { get; private set; }

        public ValueTask<PreviewServiceLease<
            IReferencePresetAuthoringTransaction>> CreateAsync(
                CancellationToken cancellationToken)
        {
            CreateCount++;
            throw new InvalidOperationException(
                "The invalid request must not acquire this factory.");
        }
    }

    private sealed class RefusingComposer : INpcVisualPreviewComposer
    {
        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                false,
                null,
                ImmutableArray.Create(new Diagnostic(
                    "npc-preview-native-runtime-unavailable",
                    DiagnosticSeverity.Error,
                    "Unavailable for focused lazy-composition proof."))));
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    private sealed class CountingHairFactory :
        IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>
    {
        public int CreateCount { get; private set; }

        public ValueTask<PreviewServiceLease<
            FaceGeomHairRegionsPreviewServices>> CreateAsync(
                CancellationToken cancellationToken)
        {
            CreateCount++;
            throw new InvalidOperationException(
                "The invalid request must not acquire this factory.");
        }
    }
}
