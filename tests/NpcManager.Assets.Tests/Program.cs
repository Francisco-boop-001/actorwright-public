using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;

namespace NpcManager.Assets.Tests;

internal static partial class Program
{
    private const string LooseHash = "0baad5f804a483f3a1e61dfe5101042b3405443f39cb625898d2f30ccf70231c";
    private const string ArchiveHash = "d2e2d20556d843fda6b8ef73e0386370ecfbfedfb53d3ec58046f7ea966c666e";
    private const string ArchiveMemberHash = "919e4b4395398242e356bd2f6ed41e0d7bc59b515a5751900fb263d72533b765";

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("exact loose and BSA authorities resolve content", TestExactProviders),
            ("hash, member, and root mismatches fail closed", TestAuthorityRefusals),
            ("ambiguous paths and cancellation fail closed", TestAmbiguityAndCancellation),
            ("strict face-bake authority materializes only declared content", TestStrictFaceBakeAuthority),
            ("face-bake authority schema and path attacks fail closed", TestFaceBakeAuthorityRefusals),
            ("real Emi2 face-bake authority preserves exact providers and order", TestRealEmi2FaceBakeAuthority)
        };

        var passed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"RESULT PASS {passed}/{tests.Length}");
        return 0;
    }

    private static async Task TestExactProviders()
    {
        var fixture = Fixture();
        var service = Service();
        var loose = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(fixture.Root,
            [fixture.Loose]), CancellationToken.None);
        Assert(loose.Resolved && loose.Assets.Length == 1 && loose.Diagnostics.IsEmpty,
            "Exact loose authority was not resolved cleanly.");
        Assert(Encoding.ASCII.GetString(loose.Assets[0].Content.AsSpan()) == "M2 SSE fixture mesh marker\n",
            "Loose content bytes changed.");

        var archive = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(fixture.Root,
            [fixture.Archive]), CancellationToken.None);
        Assert(archive.Resolved && archive.Assets.Length == 1 && archive.Diagnostics.IsEmpty,
            "Exact BSA authority was not resolved cleanly.");
        Assert(Encoding.ASCII.GetString(archive.Assets[0].Content.AsSpan()) == "archive-provider-head",
            "BSA member bytes changed.");
    }

    private static async Task TestAuthorityRefusals()
    {
        var fixture = Fixture();
        var service = Service();
        var wrongHash = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(fixture.Root,
            [fixture.Archive with { ContentSha256 = new Sha256Hash(new string('a', 64)) }]),
            CancellationToken.None);
        Assert(!wrongHash.Resolved && wrongHash.Assets.IsEmpty &&
               wrongHash.Diagnostics.Any(item => item.Code == "asset-content-hash-mismatch"),
            "Wrong BSA member hash did not fail closed.");

        var missingMember = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(fixture.Root,
            [fixture.Archive with { AssetPath = new AssetPath("meshes/m2-fixture/missing.nif") }]),
            CancellationToken.None);
        Assert(!missingMember.Resolved && missingMember.Assets.IsEmpty &&
               missingMember.Diagnostics.Any(item => item.Code == "asset-content-archive-member-missing"),
            "Missing BSA member did not fail closed.");

        var narrowRoot = new WorkspacePath(Path.Combine(fixture.Root.Value, "src"));
        var outside = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(narrowRoot,
            [fixture.Loose]), CancellationToken.None);
        Assert(!outside.Resolved && outside.Assets.IsEmpty &&
               outside.Diagnostics.Any(item => item.Code == "data-root-outside-workspace"),
            "Provider outside the declared root was not refused.");
    }

    private static async Task TestAmbiguityAndCancellation()
    {
        var fixture = Fixture();
        var service = Service();
        var duplicate = fixture.Loose with { AssetPath = new AssetPath("Meshes/M2-Fixture/HEAD.nif") };
        var ambiguous = await service.ResolveAsync(new SkyrimAssetContentResolutionRequest(fixture.Root,
            [fixture.Loose, duplicate]), CancellationToken.None);
        Assert(!ambiguous.Resolved && ambiguous.Assets.IsEmpty &&
               ambiguous.Diagnostics.Any(item => item.Code == "asset-content-path-ambiguous"),
            "Case-insensitive duplicate canonical path was not refused.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => service.ResolveAsync(
            new SkyrimAssetContentResolutionRequest(fixture.Root, [fixture.Loose]),
            cancellation.Token).AsTask());
    }

    private static FixtureAuthority Fixture()
    {
        var root = FindProjectRoot();
        var data = Path.Combine(root.Value, "01-source-copies", "m2-fixtures", "sse", "Data");
        var assetPath = new AssetPath("meshes/m2-fixture/head.nif");
        return new FixtureAuthority(root,
            new SkyrimAssetContentAuthority("M2 loose fixture", SkyrimAssetContentProviderKind.Loose,
                new WorkspacePath(Path.Combine(data, "meshes", "m2-fixture", "head.nif")),
                new Sha256Hash(LooseHash), assetPath, 27, new Sha256Hash(LooseHash)),
            new SkyrimAssetContentAuthority("M2 BSA fixture", SkyrimAssetContentProviderKind.Bsa,
                new WorkspacePath(Path.Combine(data, "M2Fixture.bsa")), new Sha256Hash(ArchiveHash),
                assetPath, 21, new Sha256Hash(ArchiveMemberHash)));
    }

    private static WorkspacePath FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcManager.sln")))
            {
                return new WorkspacePath(directory.FullName);
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("NpcManager.sln project root was not found.");
    }

    private static SkyrimAssetContentResolver Service() =>
        new(new KBoundTestPolicy(), new WorkspacePath("K:\\ExampleWorkspace"));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed record FixtureAuthority(
        WorkspacePath Root,
        SkyrimAssetContentAuthority Loose,
        SkyrimAssetContentAuthority Archive);

    private sealed class KBoundTestPolicy : IWorkspacePolicy
    {
        private static readonly WorkspacePath LabRoot = new("K:\\ExampleWorkspace");
        private static readonly WorkspacePath ProtectedRoot = new("F:\\ExampleGame");

        public ImmutableArray<Diagnostic> Evaluate(WorkspacePath workspaceRoot, WorkspacePath outputRoot) =>
            EvaluateReadRoot(workspaceRoot, outputRoot);

        public ImmutableArray<Diagnostic> EvaluateReadRoot(WorkspacePath workspaceRoot, WorkspacePath readRoot)
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!workspaceRoot.IsUnder(LabRoot))
            {
                diagnostics.Add(new Diagnostic("workspace-root-outside-lab", DiagnosticSeverity.Error,
                    "Test workspace must remain under K."));
            }
            if (!readRoot.IsUnder(workspaceRoot))
            {
                diagnostics.Add(new Diagnostic("data-root-outside-workspace", DiagnosticSeverity.Error,
                    "Test provider must remain under its declared workspace."));
            }
            if (workspaceRoot.IsUnder(ProtectedRoot) || readRoot.IsUnder(ProtectedRoot))
            {
                diagnostics.Add(new Diagnostic("protected-root-refused", DiagnosticSeverity.Error,
                    "Protected root is read-only."));
            }
            return diagnostics.ToImmutable();
        }
    }
}
