using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFaceGenBakeAll()
    {
        string ownedRoot = Path.Combine(AppContext.BaseDirectory,
            "facegen-bake-all-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(ownedRoot, "input", "Data");
        string partialOutput = Path.Combine(ownedRoot, "partial", "Data");
        string cancelOutput = Path.Combine(ownedRoot, "cancel", "Data");
        string fatalOutput = Path.Combine(ownedRoot, "fatal", "Data");
        string invalidOverlayOutput = Path.Combine(ownedRoot, "invalid-overlay", "Data");
        string skipOutput = Path.Combine(ownedRoot, "skip", "Data");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(partialOutput);
        Directory.CreateDirectory(cancelOutput);
        Directory.CreateDirectory(fatalOutput);
        Directory.CreateDirectory(invalidOverlayOutput);
        Directory.CreateDirectory(skipOutput);
        try
        {
            var plugin = new PluginName("BatchFixture.esp");
            ImmutableArray<FaceGenBakeTarget> targets =
            [
                Target(plugin, 0x800),
                Target(plugin, 0x801),
                Target(plugin, 0x802)
            ];
            var root = new WorkspacePath("K:\\ExampleWorkspace");
            var discovery = new FixedBakeDiscovery(targets);
            var partialBaker = new FixtureNpcBaker(target =>
                target.FormId.Value switch
                {
                    0x800 => FaceGenNpcBakeStatus.Baked,
                    0x801 => FaceGenNpcBakeStatus.Failed,
                    _ => FaceGenNpcBakeStatus.Skipped
                });
            var progressItems = new List<FaceGenBakeAllProgress>();
            var snapshots = new TrackingSnapshotFactory();
            SkyrimFaceGenSidecarOverlay batchOverlay = OverlayFor(targets[0]);
            var service = new FaceGenBakeAllService(discovery, partialBaker,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath("F:\\ExampleGame")), root,
                snapshots, new FixedSidecarLoader([batchOverlay]));
            FaceGenBakeAllResult partial = await service.RunAsync(
                Request(input, partialOutput, plugin),
                new DirectProgress<FaceGenBakeAllProgress>(progressItems.Add),
                CancellationToken.None);
            Assert(partial.Status == FaceGenBakeAllStatus.SomeFailed &&
                   partial.ExitCode == 2 && partial.Discovered == 3 &&
                   partial.Baked == 1 && partial.Failed == 1 &&
                   partial.Skipped == 1 && partial.Outcomes.Length == 3,
                "Batch partial-failure accounting drifted from ordered per-NPC outcomes.");
            Assert(snapshots.Begun == 1 && snapshots.Disposed == 1,
                "Batch did not bound its shared asset snapshot to exactly one run.");
            Assert(ReferenceEquals(partialBaker.Requests[0].SidecarOverlay,
                       batchOverlay) &&
                   partialBaker.Requests.Skip(1).All(item =>
                       item.SidecarOverlay is null),
                "Batch did not bind the resolved sidecar overlay to exactly its target bake.");
            Assert(progressItems.Select(item => item.Sequence)
                       .SequenceEqual(Enumerable.Range(1, progressItems.Count)) &&
                   progressItems[0].Phase ==
                   FaceGenBakeAllProgressPhase.Discovering &&
                   progressItems[^1].Phase ==
                   FaceGenBakeAllProgressPhase.Completed,
                "Batch progress was not ordered from discovery through completion.");

            using var cancellation = new CancellationTokenSource();
            var cancellingBaker = new FixtureNpcBaker(
                _ => FaceGenNpcBakeStatus.Baked,
                afterWrite: cancellation.Cancel);
            var cancellingService = new FaceGenBakeAllService(discovery,
                cancellingBaker,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath("F:\\ExampleGame")), root);
            FaceGenBakeAllResult cancelled = await cancellingService.RunAsync(
                Request(input, cancelOutput, plugin), progress: null,
                cancellation.Token);
            Assert(cancelled.Status == FaceGenBakeAllStatus.Cancelled &&
                   cancelled.ExitCode == 3 && cancelled.Baked == 1 &&
                   cancelled.Outcomes.Length == 1 &&
                   cancellingBaker.Calls == 1,
                "Cancellation did not finish exactly the active NPC before stopping.");
            Assert(File.Exists(ExpectedNif(cancelOutput, plugin, 0x800)) &&
                   !File.Exists(ExpectedNif(cancelOutput, plugin, 0x801)),
                "Cancellation retained an incomplete active NPC or started the next NPC.");

            string collision = ExpectedNif(fatalOutput, plugin, 0x800);
            Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
            await File.WriteAllBytesAsync(collision, [0xFF]);
            var fatalBaker = new FixtureNpcBaker(
                _ => FaceGenNpcBakeStatus.Baked);
            var fatalService = new FaceGenBakeAllService(discovery, fatalBaker,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath("F:\\ExampleGame")), root);
            FaceGenBakeAllResult fatal = await fatalService.RunAsync(
                Request(input, fatalOutput, plugin), progress: null,
                CancellationToken.None);
            Assert(fatal.Status == FaceGenBakeAllStatus.Fatal &&
                   fatal.ExitCode == 1 && fatal.Outcomes.IsEmpty &&
                   fatalBaker.Calls == 0 && File.Exists(collision),
                "A pre-existing canonical half-pair did not fail before all writes.");

            var invalidOverlayBaker = new FixtureNpcBaker(
                _ => FaceGenNpcBakeStatus.Baked);
            SkyrimFaceGenSidecarOverlay duplicateOverlay = OverlayFor(targets[0]);
            var invalidOverlayService = new FaceGenBakeAllService(
                discovery, invalidOverlayBaker,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath("F:\\ExampleGame")), root,
                sidecarOverlays: new FixedSidecarLoader(
                    [duplicateOverlay, duplicateOverlay]));
            FaceGenBakeAllResult invalidOverlay = await invalidOverlayService.RunAsync(
                Request(input, invalidOverlayOutput, plugin), progress: null,
                CancellationToken.None);
            Assert(invalidOverlay.Status == FaceGenBakeAllStatus.Fatal &&
                   invalidOverlayBaker.Calls == 0 &&
                   invalidOverlay.Diagnostics.Any(item =>
                       item.Code == "facegen-bake-all-sidecar-closure"),
                "An accepted duplicate sidecar overlay escaped the batch pre-write closure gate.");

            FaceGenBakeTarget skipTarget = targets[0];
            string existingNif = ExpectedNif(skipOutput, plugin, 0x800);
            string existingDds = ExpectedDds(skipOutput, plugin, 0x800);
            Directory.CreateDirectory(Path.GetDirectoryName(existingNif)!);
            Directory.CreateDirectory(Path.GetDirectoryName(existingDds)!);
            byte[] existingNifBytes = [0x4E, 0x49, 0x46, 0x80];
            byte[] existingDdsBytes = [0x44, 0x44, 0x53, 0x80];
            await File.WriteAllBytesAsync(existingNif, existingNifBytes);
            await File.WriteAllBytesAsync(existingDds, existingDdsBytes);
            var skipBaker = new FixtureNpcBaker(
                _ => FaceGenNpcBakeStatus.Skipped);
            var skipService = new FaceGenBakeAllService(
                new FixedBakeDiscovery([skipTarget]), skipBaker,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath("F:\\ExampleGame")), root);
            FaceGenBakeAllResult skipped = await skipService.RunAsync(
                Request(input, skipOutput, plugin), progress: null,
                CancellationToken.None);
            Assert(skipped.Status == FaceGenBakeAllStatus.Succeeded &&
                   skipped.Skipped == 1 && skipped.Failed == 0 &&
                   skipBaker.Calls == 1 &&
                   File.ReadAllBytes(existingNif).SequenceEqual(existingNifBytes) &&
                   File.ReadAllBytes(existingDds).SequenceEqual(existingDdsBytes),
                "A complete pre-existing pair was not preserved as an unclaimed skip.");
        }
        finally
        {
            DeleteOwnedDirectory(ownedRoot);
        }
    }

    private static FaceGenBakeAllRequest Request(
        string input,
        string output,
        PluginName plugin) =>
        new(GameEdition.SkyrimSpecialEdition, new WorkspacePath(input),
            [plugin], new WorkspacePath(output));

    private static FaceGenBakeTarget Target(PluginName plugin, uint formId) =>
        new(new FormId(formId), plugin, plugin, [plugin],
            $"Batch{formId:X8}", $"Batch {formId:X8}", NpcSex.Female,
            new FormReference(plugin, new FormId(0x19)),
            [new FormReference(plugin, new FormId(0x100))], 50F);

    private static SkyrimFaceGenSidecarOverlay OverlayFor(FaceGenBakeTarget target) =>
        new(target.OriginatingPlugin, target.FormId,
            [new SkyrimRaceMenuCustomMorphValue("FixtureMorph", 0.25F)],
            [], [], [],
            [new SkyrimFaceGenSidecarAuthority(
                target.WinningPlugin,
                new WorkspacePath("K:\\ExampleWorkspace\\fixture.bssliders"),
                new Sha256Hash(new string('A', 64)))]);

    private static string ExpectedNif(
        string output,
        PluginName plugin,
        uint formId) =>
        Path.Combine(output, "meshes", "actors", "character", "FaceGenData",
            "FaceGeom", plugin.Value, $"{formId:X8}.nif");

    private static string ExpectedDds(
        string output,
        PluginName plugin,
        uint formId) =>
        Path.Combine(output, "textures", "actors", "character", "FaceGenData",
            "FaceTint", plugin.Value, $"{formId:X8}.dds");

    private static void DeleteOwnedDirectory(string path)
    {
        string root = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Fixture cleanup escaped the architecture-test output root.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private sealed class FixedBakeDiscovery(
        ImmutableArray<FaceGenBakeTarget> targets) :
        IFaceGenBakeTargetDiscoveryService
    {
        public ValueTask<FaceGenBakeTargetDiscoveryResult> DiscoverAsync(
            FaceGenBakeTargetDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new FaceGenBakeTargetDiscoveryResult(
                true, targets, []));
        }
    }

    private sealed class FixtureNpcBaker(
        Func<FaceGenBakeTarget, FaceGenNpcBakeStatus> chooseStatus,
        Action? afterWrite = null) : IFaceGenNpcBakeService
    {
        public int Calls { get; private set; }
        public List<FaceGenNpcBakeRequest> Requests { get; } = [];

        public async ValueTask<FaceGenNpcBakeResult> BakeAsync(
            FaceGenNpcBakeRequest request,
            CancellationToken cancellationToken)
        {
            Assert(!cancellationToken.CanBeCanceled,
                "The batch forwarded cancellation into the active NPC bake.");
            Calls++;
            Requests.Add(request);
            FaceGenNpcBakeStatus status = chooseStatus(request.Target);
            if (status != FaceGenNpcBakeStatus.Baked)
                return new FaceGenNpcBakeResult(status, request.Target, null, []);

            string nif = ExpectedNif(request.OutputDataRoot.Value,
                request.Target.OriginatingPlugin, request.Target.FormId.Value);
            string dds = ExpectedDds(request.OutputDataRoot.Value,
                request.Target.OriginatingPlugin, request.Target.FormId.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(nif)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dds)!);
            byte[] nifBytes = [0x4E, 0x49, 0x46,
                checked((byte)(request.Target.FormId.Value & 0xFF))];
            byte[] ddsBytes = [0x44, 0x44, 0x53,
                checked((byte)(request.Target.FormId.Value & 0xFF))];
            await File.WriteAllBytesAsync(nif, nifBytes,
                CancellationToken.None);
            await File.WriteAllBytesAsync(dds, ddsBytes,
                CancellationToken.None);
            afterWrite?.Invoke();
            var artifact = new FaceGenNpcBakeArtifact(request.Target,
                new WorkspacePath(nif), Hash(nifBytes), nifBytes.Length,
                new WorkspacePath(dds), Hash(ddsBytes), ddsBytes.Length,
                RuntimeAuthority: false);
            return new FaceGenNpcBakeResult(status, request.Target, artifact, []);
        }

        private static Sha256Hash Hash(byte[] bytes) =>
            new(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private sealed class FixedSidecarLoader(
        ImmutableArray<SkyrimFaceGenSidecarOverlay> overlays)
        : ISkyrimFaceGenSidecarOverlayLoader
    {
        public ValueTask<SkyrimFaceGenSidecarOverlayLoadResult> LoadAsync(
            SkyrimFaceGenSidecarOverlayLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceGenSidecarOverlayLoadResult(
                true, overlays,
                overlays.SelectMany(item => item.SourceAuthorities)
                    .Distinct().ToImmutableArray(), []));
        }
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TrackingSnapshotFactory : IAssetIndexSnapshotScopeFactory
    {
        public int Begun { get; private set; }
        public int Disposed { get; private set; }

        public IDisposable BeginSnapshot()
        {
            Begun++;
            return new CallbackDisposable(() => Disposed++);
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}
