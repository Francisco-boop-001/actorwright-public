using System.Collections.Immutable;
using System.Reflection;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class BoundedReadinessArchitectureTests
{
    public static async Task RunAsync()
    {
        TestWorkspacePathContainment();
        TestApplicationResourcePathContainment();
        TestCanonicalContainmentHelpers();
        await TestMediaPipeDisposalAsync();
        Console.WriteLine(
            "PASS bounded readiness architecture: exact-parent containment and atomic nonblocking MediaPipe disposal");
    }

    private static void TestWorkspacePathContainment()
    {
        string seed = Path.Combine(
            Path.GetTempPath(),
            "actorwright-containment-" + Guid.NewGuid().ToString("N"));
        var labRoot = new WorkspacePath(Path.Combine(seed, "lab"));
        var same = new WorkspacePath(labRoot.Value + Path.DirectorySeparatorChar);
        var child = new WorkspacePath(Path.Combine(labRoot.Value, "project", "output"));
        var parent = new WorkspacePath(seed);
        var grandparent = new WorkspacePath(
            Directory.GetParent(seed)!.FullName);
        var volumeRoot = new WorkspacePath(Path.GetPathRoot(seed)!);
        var sibling = new WorkspacePath(seed + "-sibling");
        var sharedPrefix = new WorkspacePath(labRoot.Value + "-copy");
        var caseVariant = new WorkspacePath(labRoot.Value.ToUpperInvariant());

        Require(same.IsUnder(labRoot), "same-path-with-trailing-separator");
        Require(child.IsUnder(labRoot), "ordinary-descendant");
        Require(caseVariant.IsUnder(labRoot), "windows-case-variant");
        Require(!parent.IsUnder(labRoot), "exact-parent-refused");
        Require(!grandparent.IsUnder(labRoot), "grandparent-refused");
        Require(!volumeRoot.IsUnder(labRoot), "volume-root-refused");
        Require(!sibling.IsUnder(labRoot), "sibling-refused");
        Require(!sharedPrefix.IsUnder(labRoot), "shared-prefix-sibling-refused");

        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var climbedWorkspace = parent;
        var climbedOutput = grandparent;
        var diagnostics = policy.Evaluate(climbedWorkspace, climbedOutput);
        Require(diagnostics.Any(item =>
                item.Code == "workspace-root-outside-lab") &&
            diagnostics.Any(item =>
                item.Code == "output-root-outside-workspace"),
            "two-step-parent-climb-refused");
    }

    private static void TestApplicationResourcePathContainment()
    {
        var root = new ApplicationResourcePath(@"K:\work");

        Require(root.IsUnder(root), "application-resource-equal-root");
        Require(new ApplicationResourcePath(@"K:\work\child").IsUnder(root),
            "application-resource-descendant");
        Require(!root.IsUnder(new ApplicationResourcePath(@"K:\work\child")),
            "application-resource-exact-parent-refused");
        Require(!root.IsUnder(new ApplicationResourcePath(@"K:\work\child\grandchild")),
            "application-resource-grandparent-refused");
        Require(!new ApplicationResourcePath(@"K:\").IsUnder(root),
            "application-resource-volume-root-refused");
        Require(!new ApplicationResourcePath(@"K:\sibling").IsUnder(root),
            "application-resource-sibling-refused");
        Require(!new ApplicationResourcePath(@"K:\work-copy").IsUnder(root),
            "application-resource-shared-prefix-sibling-refused");
    }

    private static void TestCanonicalContainmentHelpers()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] helperFiles =
        [
            "src/NpcManager.Infrastructure/FaceGenPackService.cs",
            "src/NpcManager.Infrastructure/FaceGenPackPlanService.cs",
            "src/NpcManager.Infrastructure/FaceGenDeployService.cs",
            "src/NpcManager.Infrastructure/PackageArchiveService.cs",
            "src/NpcManager.Infrastructure/PackageBuildService.cs",
            "src/NpcManager.Infrastructure/PackageVerifyService.cs",
            "src/NpcManager.Formats.Bethesda/BethesdaSkyrimBsaService.cs",
            "src/NpcManager.Infrastructure/SkyrimFollowerFinishSourcePackageReader.cs",
            "src/NpcManager.Pipeline/ProviderMigrationService.cs",
            "src/NpcManager.Pipeline/BlankNpcBuildService.PathSafety.cs",
            "src/NpcManager.Cli/PackageCommandHandler.cs"
        ];
        var rawHelpers = new List<string>();
        foreach (string relativePath in helperFiles)
        {
            string source = File.ReadAllText(Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string helperSignature = relativePath switch
            {
                "src/NpcManager.Pipeline/BlankNpcBuildService.PathSafety.cs" =>
                    "private static bool IsSameOrUnder(string path, string root)",
                _ => "private static bool IsUnder(string path, string root)"
            };
            int helperStart = source.IndexOf(
                helperSignature,
                StringComparison.Ordinal);
            Require(helperStart >= 0,
                "missing-containment-helper:" + relativePath);
            int helperEnd = source.IndexOf(
                "\n    private static",
                helperStart + helperSignature.Length,
                StringComparison.Ordinal);
            string helperSource = source[helperStart..(
                helperEnd >= 0 ? helperEnd : source.Length)];
            if (helperSource.Contains(
                    "Path.GetRelativePath(",
                    StringComparison.Ordinal))
                rawHelpers.Add(relativePath);
            Require(helperSource.Contains(
                    "WorkspacePath",
                    StringComparison.Ordinal),
                "noncanonical-containment-helper:" + relativePath);
        }

        Require(rawHelpers.Count == 0,
            "raw-string-containment-helpers:" + string.Join(",", rawHelpers));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Actorwright repository root.");
    }

    private static async Task TestMediaPipeDisposalAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "actorwright-mediapipe-dispose-" + Guid.NewGuid().ToString("N"));
        var labRoot = new WorkspacePath(root);
        var runtimeRoot = new WorkspacePath(Path.Combine(root, "runtime"));
        var api = new MediaPipeNativeApi(labRoot, runtimeRoot);
        var serial = (SemaphoreSlim)(typeof(MediaPipeNativeApi)
            .GetField("_serial", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(api)!);

        await serial.WaitAsync();
        Task<ReferenceFaceNativeInferenceResult> waiting = api.InferAsync(
            CreateInferenceRequest(labRoot),
            CancellationToken.None).AsTask();
        var disposeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task dispose = Task.Run(() =>
        {
            disposeStarted.SetResult(true);
            api.Dispose();
        });
        try
        {
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task completed = await Task.WhenAny(
                dispose,
                Task.Delay(TimeSpan.FromMilliseconds(500)));
            Require(ReferenceEquals(completed, dispose),
                "mediapipe-dispose-blocked");
        }
        finally
        {
            serial.Release();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await RequireThrowsAsync<ObjectDisposedException>(() => waiting);
        RequireThrows<ObjectDisposedException>(() => api.AdmitRuntime());
        Parallel.For(0, 32, _ => api.Dispose());
    }

    private static ReferenceFaceInferenceRequest CreateInferenceRequest(
        WorkspacePath labRoot) =>
        new(
            new DecodedReferenceImage(
                "front",
                ReferenceImageViewRole.Front,
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "front.png")),
                new Sha256Hash(new string('a', 64)),
                4,
                ReferenceImageFormat.Png,
                ReferenceImageOrientation.TopLeft,
                1,
                1,
                4,
                ImmutableArray.Create<byte>(255, 255, 255, 255),
                new Sha256Hash(new string('b', 64))),
            new Sha256Hash(new string('c', 64)));

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            "expected-exception:" + typeof(TException).Name);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            "expected-exception:" + typeof(TException).Name);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }
}
