using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpRouteGraphScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-external-headpart-route-graph";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        ExternalHeadPartRouteGraphTests.RunAsync(cancellationToken);
}

internal sealed class Preview254ExternalSmpPinnedCleanupTree : IDisposable
{
    private readonly Preview254ExternalSmpPinnedDirectory? directory;
    private readonly Preview254ExternalSmpPinnedFile? file;
    private readonly List<Preview254ExternalSmpPinnedCleanupTree> children = [];

    private Preview254ExternalSmpPinnedCleanupTree(
        Preview254ExternalSmpPinnedDirectory directory)
    {
        this.directory = directory;
    }

    private Preview254ExternalSmpPinnedCleanupTree(
        Preview254ExternalSmpPinnedFile file)
    {
        this.file = file;
    }

    internal static Preview254ExternalSmpPinnedCleanupTree Build(
        string path, Preview254ExternalSmpPinnedDirectory? pinnedRoot = null)
    {
        var expected = Preview254ExternalSmpFixtureFilesystem.Stat(path);
        var tree = new Preview254ExternalSmpPinnedCleanupTree(
            pinnedRoot ?? Preview254ExternalSmpFixtureFilesystem.OpenDirectoryLease(
                path, owned: true));
        try
        {
            tree.directory!.RequireIdentity(expected);
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                var childStat = Preview254ExternalSmpFixtureFilesystem.Stat(child);
                if (childStat.IsDirectory)
                {
                    var childDirectory = Preview254ExternalSmpFixtureFilesystem
                        .OpenDirectoryLease(child, owned: true);
                    childDirectory.RequireIdentity(childStat);
                    tree.children.Add(Build(child, childDirectory));
                }
                else
                {
                    var childFile = Preview254ExternalSmpFixtureFilesystem
                        .OpenFileForDelete(child);
                    childFile.RequireIdentity(childStat);
                    tree.children.Add(new Preview254ExternalSmpPinnedCleanupTree(childFile));
                }
            }
            return tree;
        }
        catch
        {
            tree.Dispose();
            throw;
        }
    }

    internal void DeleteBottomUp()
    {
        foreach (var child in children) child.DeleteBottomUp();
        if (file is not null)
        {
            Preview254ExternalSmpFixtureFilesystem.DeleteByHandle(file.Handle);
            file.Dispose();
        }
        else if (directory is not null)
        {
            Preview254ExternalSmpFixtureFilesystem.DeleteByHandle(directory.Handle);
            directory.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var child in children.AsEnumerable().Reverse()) child.Dispose();
        file?.Dispose();
        directory?.Dispose();
    }
}

internal static class ExternalHeadPartRouteGraphTests
{
    private const uint ProviderRawHairRoot = 0x0100_0800u;
    private const uint ProviderRawHairChild = 0x0100_0801u;
    private static readonly Dictionary<string,
        Preview254ExternalSmpFixtureFilesystemBinding> ScratchBindings = [];

    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string repoRoot = FindRepositoryRoot();
        WorkspacePath scratchRoot = CreateScratchRoot(
            repoRoot, "preview254-route-graph-");
        string scratchPath = scratchRoot.Value;
        var factory = new Preview254ExternalSmpBethesdaFixtureFactory();
        try
        {
            string nestedMissing = Path.Combine(
                scratchPath, "missing-parent", "nested-leaf.bin");
            var nestedMissingBinding = Preview254ExternalSmpFixtureFilesystem.Bind(
                scratchPath, nestedMissing);
            nestedMissingBinding.RequireMissing(
                nestedMissing, "nested missing fixture leaf");

            string nonDirectoryParent = Path.Combine(scratchPath, "not-a-directory");
            File.WriteAllBytes(nonDirectoryParent, [0x4E, 0x4F, 0x54, 0x44]);
            Exception? nonDirectoryRefusal = null;
            try
            {
                Preview254ExternalSmpFixtureFilesystem.Bind(
                    scratchPath, Path.Combine(nonDirectoryParent, "leaf.bin"));
            }
            catch (Exception exception)
            {
                nonDirectoryRefusal = exception;
            }
            Require(nonDirectoryRefusal is not null,
                "A non-directory ancestor was admitted as a missing fixture path.");

            var blockedScratchRoot = CreateScratchRoot(
                repoRoot, "preview254-route-graph-existing-destination-");
            try
            {
                string blockedData = Path.Combine(blockedScratchRoot.Value, "Data");
                Preview254ExternalSmpFixtureFilesystem.EnsureOrdinaryDirectory(
                    blockedData, blockedScratchRoot.Value, "fixture Data directory");
                string blockedProvider = Path.Combine(blockedData, "OrchidAdornment.esp");
                byte[] marker = [0x52, 0x45, 0x46, 0x55, 0x53, 0x45];
                File.WriteAllBytes(blockedProvider, marker);
                Exception? refusal = null;
                try
                {
                    await factory.CreateAsync(
                        blockedScratchRoot,
                        Preview254ExternalSmpBethesdaFixtureMode.ModelLessRootWithChild,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    refusal = exception;
                }
                Require(refusal is not null,
                    "The fixture factory accepted an existing provider destination.");
                Require(File.ReadAllBytes(blockedProvider).SequenceEqual(marker),
                    "The fixture factory changed an existing provider destination before refusal.");
            }
            finally
            {
                CleanupScratchRoot(repoRoot, blockedScratchRoot.Value);
            }

            var driftScratchRoot = CreateScratchRoot(
                repoRoot, "preview254-route-graph-cleanup-drift-");
            string driftMovedPath = driftScratchRoot.Value + "-moved";
            bool driftMoved = false;
            try
            {
                Directory.Move(driftScratchRoot.Value, driftMovedPath);
                driftMoved = true;
                Exception? refusal = null;
                try
                {
                    CleanupScratchRoot(repoRoot, driftScratchRoot.Value);
                }
                catch (Exception exception)
                {
                    refusal = exception;
                }
                Require(refusal is not null,
                    "Cleanup accepted a fixture root whose bound identity had changed.");
            }
            finally
            {
                if (driftMoved)
                    Directory.Move(driftMovedPath, driftScratchRoot.Value);
                CleanupScratchRoot(repoRoot, driftScratchRoot.Value);
            }

            var fixture = await factory.CreateAsync(
                new WorkspacePath(scratchPath),
                Preview254ExternalSmpBethesdaFixtureMode.ModelLessRootWithChild,
                cancellationToken);

        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(repoRoot), new WorkspacePath(@"F:\ExampleGame"));
        var resolver = new BethesdaSkyrimFaceRecordRouteResolver(
            policy, new WorkspacePath(repoRoot));
        var request = new SkyrimFaceRecordRouteRequest(
            GameEdition.SkyrimSpecialEdition,
            new FormReference(fixture.ProviderPlugin, new FormId(0x900)),
            NpcSex.Male,
            [new SkyrimFaceRecordHeadPartSelection(fixture.HairRoot, [])],
            [new SkyrimFaceRecordPluginAuthority(
                fixture.ProviderPlugin,
                fixture.ProviderPluginPath,
                fixture.ProviderPluginSha256)]);

        SkyrimFaceRecordRouteResult result = await resolver.ResolveAsync(
            request, cancellationToken);
        Require(result.Accepted && result.Route is not null,
            "The model-less HNAM fixture was refused: " + FormatDiagnostics(result.Diagnostics));
        var route = result.Route ?? throw new InvalidOperationException(
            "Accepted route was null.");
        var graph = route.HeadPartGraph;
        Require(graph.Length == 2,
            "The bounded route graph must contain the model-less root and its child.");

        var root = graph[0];
        var child = graph[1];
        var rawDigests = BethesdaRawRecordDigestReader.Read(
            fixture.ProviderPluginPath.Value);
        RequireGraphRecord(root,
            fixture.HairRoot, fixture.ProviderPlugin, fixture.HairRoot,
            fixture.ProviderPlugin, fixture.ProviderPluginSha256,
            fixture.ProviderPluginByteLength,
            new Sha256Hash(rawDigests[(ProviderRawHairRoot, "HDPT")]),
            "OrchidRootHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
            null, [], [fixture.HairChild], null, 0, true, false, 0, null, null);
        RequireGraphRecord(child,
            fixture.HairChild, fixture.ProviderPlugin, fixture.HairChild,
            fixture.ProviderPlugin, fixture.ProviderPluginSha256,
            fixture.ProviderPluginByteLength,
            new Sha256Hash(rawDigests[(ProviderRawHairChild, "HDPT")]),
            "OrchidChildHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
            new AssetPath("meshes/actors/character/character assets/hair/orchid-child.nif"),
            [new SkyrimHdptTriRoute(
                SkyrimHdptTriRole.Mesh,
                new AssetPath("meshes/actors/character/character assets/hair/orchid-child.tri"))],
            [], fixture.HairRoot, 1, false, false, 1, null, null);

            Require(route.RootHeadParts.SequenceEqual([fixture.HairRoot]) &&
                route.HeadParts.Length == 1 &&
                route.HeadParts[0].Reference == fixture.HairChild &&
                route.HeadParts[0].Parent == fixture.HairRoot,
            "The legacy model-bearing HeadParts projection changed while adding graph evidence.");

            var legacyScratchRoot = CreateScratchRoot(
                repoRoot, "preview254-route-graph-legacy-");
            try
            {
                var legacyFixture = await factory.CreateAsync(
                    legacyScratchRoot,
                    Preview254ExternalSmpBethesdaFixtureMode.LegacyMultipleOrderedRoots,
                    cancellationToken);
                var legacyRequest = request with
                {
                    Race = new FormReference(legacyFixture.ProviderPlugin, new FormId(0x900)),
                    SelectedHeadParts = [
                        new SkyrimFaceRecordHeadPartSelection(
                            new FormReference(legacyFixture.ProviderPlugin, new FormId(0x802)), []),
                        new SkyrimFaceRecordHeadPartSelection(
                            new FormReference(legacyFixture.ProviderPlugin, new FormId(0x800)), [])],
                    PluginOrder = [new SkyrimFaceRecordPluginAuthority(
                        legacyFixture.ProviderPlugin,
                        legacyFixture.ProviderPluginPath,
                        legacyFixture.ProviderPluginSha256)]
                };
                SkyrimFaceRecordRouteResult legacy = await resolver.ResolveAsync(
                    legacyRequest, cancellationToken);
                Require(legacy.Accepted && legacy.Route is not null,
                    "The ordered legacy fixture was refused: " +
                    FormatDiagnostics(legacy.Diagnostics));
                var expectedLegacyRoots = new[] {
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x800)),
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x802)) };
                var expectedLegacyHeadParts = new[] {
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x801)),
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x803)) };
                Require(legacy.Route!.RootHeadParts.SequenceEqual(expectedLegacyRoots) &&
                    legacy.Route.HeadParts.Select(item => item.Reference).SequenceEqual(expectedLegacyHeadParts),
                    $"The legacy multi-root fixture changed root or model-bearing ordering. " +
                    $"roots={string.Join(",", legacy.Route.RootHeadParts)} " +
                    $"heads={string.Join(",", legacy.Route.HeadParts.Select(item => item.Reference))}");
                var legacyProvider = new SkyrimFaceRecordProvider(
                    legacyFixture.ProviderPlugin,
                    legacyFixture.ProviderPluginPath,
                    legacyFixture.ProviderPluginSha256);
                RequireLegacyRecord(legacy.Route.HeadParts[0],
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x801)),
                    legacyProvider,
                    "OrchidChildHair",
                    new AssetPath("meshes/actors/character/character assets/hair/orchid-child.nif"),
                    new AssetPath("meshes/actors/character/character assets/hair/orchid-child.tri"),
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x800)));
                RequireLegacyRecord(legacy.Route.HeadParts[1],
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x803)),
                    legacyProvider,
                    "OrchidSecondChildHair",
                    new AssetPath("meshes/actors/character/character assets/hair/orchid-second-child.nif"),
                    new AssetPath("meshes/actors/character/character assets/hair/orchid-second-child.tri"),
                    new FormReference(legacyFixture.ProviderPlugin, new FormId(0x802)));
            }
            finally
            {
                CleanupScratchRoot(repoRoot, legacyScratchRoot.Value);
            }

            var reparseScratchRoot = CreateScratchRoot(
                repoRoot, "preview254-route-graph-reparse-");
            var redirectTarget = CreateScratchRoot(
                repoRoot, "preview254-route-graph-reparse-target-");
            string reparseDataPath = Path.Combine(reparseScratchRoot.Value, "Data");
            string redirectDataPath = Path.Combine(redirectTarget.Value, "Data");
            bool reparseCreated = false;
            try
            {
                Preview254ExternalSmpFixtureFilesystem.EnsureOrdinaryDirectory(
                    redirectDataPath, redirectTarget.Value, "reparse redirect Data directory");
                reparseCreated = PhysicalReparseFixture.TryCreateDirectoryLink(
                    reparseDataPath, redirectDataPath, reparseScratchRoot.Value);
                if (reparseCreated)
                {
                    Exception? refusal = null;
                    try
                    {
                        await factory.CreateAsync(
                            reparseScratchRoot,
                            Preview254ExternalSmpBethesdaFixtureMode.ModelLessRootWithChild,
                            cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        refusal = exception;
                    }
                    Require(refusal is not null,
                        "The fixture factory wrote through a reparse-point Data directory.");
                    Require(!Directory.EnumerateFileSystemEntries(redirectDataPath).Any(),
                        "The fixture factory redirected bytes before refusing the reparse-point Data directory.");
                }
            }
            finally
            {
                if (reparseCreated && Directory.Exists(reparseDataPath))
                    Directory.Delete(reparseDataPath);
                CleanupScratchRoot(repoRoot, redirectTarget.Value);
                CleanupScratchRoot(repoRoot, reparseScratchRoot.Value);
            }

            var loadedCatalog = BethesdaSkyrimFaceRecordCatalogLoader.Load(
                request.PluginOrder, cancellationToken, requireWinningRecordEvidence: true);
            var missingDigestCatalog = loadedCatalog with
            {
                HeadParts = loadedCatalog.HeadParts.ToImmutableDictionary(
                    pair => pair.Key,
                    pair => pair.Value with { WinningRecordSha256 = null })
            };
            SkyrimFaceRecordRouteResult missingDigest =
                BethesdaSkyrimFaceRecordGraphResolver.Resolve(request, missingDigestCatalog);
            Require(!missingDigest.Accepted && missingDigest.Diagnostics.Any(item =>
                        item.Code == "skyrim-face-record-hdpt-digest"),
                "A graph route synthesized or admitted a missing winning HDPT digest.");

            var overrideScratchRoot = CreateScratchRoot(
                repoRoot, "preview254-route-graph-override-");
            try
            {
                var overrideFixture = await factory.CreateAsync(
                    overrideScratchRoot,
                    Preview254ExternalSmpBethesdaFixtureMode.CrossPluginWinningOverride,
                    cancellationToken);
                var overridePath = new WorkspacePath(Path.Combine(
                    overrideFixture.DataRoot.Value, "OrchidAdornmentPatch.esp"));
                var overrideAuthority = new SkyrimFaceRecordPluginAuthority(
                    new PluginName("OrchidAdornmentPatch.esp"),
                    overridePath,
                    HashFile(overridePath.Value));
                var overrideRequest = request with
                {
                    Race = new FormReference(overrideFixture.ProviderPlugin, new FormId(0x900)),
                    SelectedHeadParts = [new SkyrimFaceRecordHeadPartSelection(
                        overrideFixture.HairRoot, [])],
                    PluginOrder = [
                        new SkyrimFaceRecordPluginAuthority(
                            overrideFixture.ProviderPlugin,
                            overrideFixture.ProviderPluginPath,
                            overrideFixture.ProviderPluginSha256),
                        overrideAuthority
                    ]
                };
                SkyrimFaceRecordRouteResult overridden = await resolver.ResolveAsync(
                    overrideRequest, cancellationToken);
                Require(overridden.Accepted && overridden.Route is not null,
                    "The cross-plugin winning override fixture was refused: " +
                    FormatDiagnostics(overridden.Diagnostics));
                var overrideGraph = overridden.Route!.HeadPartGraph;
                var overrideRoot = overrideGraph[0];
                var overrideChild = overrideGraph[1];
                var overrideRawDigests = BethesdaRawRecordDigestReader.Read(
                    overridePath.Value);
                var overrideProviderRawDigests = BethesdaRawRecordDigestReader.Read(
                    overrideFixture.ProviderPluginPath.Value);
                var overridePlugin = new PluginName("OrchidAdornmentPatch.esp");
                var overrideLength = new FileInfo(overridePath.Value).Length;
                RequireGraphRecord(overrideRoot,
                    overrideFixture.HairRoot, overrideFixture.ProviderPlugin,
                    overrideFixture.HairRoot, overridePlugin,
                    overrideAuthority.ExpectedSha256, overrideLength,
                    new Sha256Hash(overrideRawDigests[(ProviderRawHairRoot, "HDPT")]),
                    "OrchidRootHairOverride", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
                    null, [], [overrideFixture.HairChild], null, 0, true, false, 0, null, null);
                RequireGraphRecord(overrideChild,
                    overrideFixture.HairChild, overrideFixture.ProviderPlugin,
                    overrideFixture.HairChild, overrideFixture.ProviderPlugin,
                    overrideFixture.ProviderPluginSha256,
                    overrideFixture.ProviderPluginByteLength,
                    new Sha256Hash(overrideProviderRawDigests[(ProviderRawHairChild, "HDPT")]),
                    "OrchidChildHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
                    new AssetPath("meshes/actors/character/character assets/hair/orchid-child.nif"),
                    [new SkyrimHdptTriRoute(
                        SkyrimHdptTriRole.Mesh,
                        new AssetPath("meshes/actors/character/character assets/hair/orchid-child.tri"))],
                    [], overrideFixture.HairRoot, 1, false, false, 1, null, null);
            }
            finally
            {
                CleanupScratchRoot(repoRoot, overrideScratchRoot.Value);
            }
        }
        finally
        {
            CleanupScratchRoot(repoRoot, scratchPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                (File.Exists(Path.Combine(current.FullName, ".git")) ||
                 Directory.Exists(Path.Combine(current.FullName, ".git"))))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the lane repository root.");
    }

    private static WorkspacePath CreateScratchRoot(string repoRoot, string prefix)
    {
        var repoBinding = Preview254ExternalSmpFixtureFilesystem.Bind(repoRoot);
        repoBinding.RequireOrdinary(repoRoot, "repository root");
        string artifacts = Path.Combine(repoRoot, "artifacts");
        Preview254ExternalSmpFixtureFilesystem.EnsureOrdinaryDirectory(
            artifacts, repoRoot, "artifacts directory");
        string testWork = Path.Combine(artifacts, "test-work");
        Preview254ExternalSmpFixtureFilesystem.EnsureOrdinaryDirectory(
            testWork, artifacts, "test-work directory");
        string path = Path.Combine(testWork,
            prefix + Guid.NewGuid().ToString("N"));
        Preview254ExternalSmpFixtureFilesystem.EnsureOrdinaryDirectory(
            path, testWork, "scratch root");
        string full = Path.GetFullPath(path);
        var binding = Preview254ExternalSmpFixtureFilesystem.Bind(
            repoRoot, artifacts, testWork, full);
        binding.RequireOrdinary(repoRoot, "repository root");
        binding.RequireOrdinary(artifacts, "artifacts directory");
        binding.RequireOrdinary(testWork, "test-work directory");
        binding.RequireOrdinary(full, "scratch root");
        ScratchBindings.Add(full, binding);
        return new WorkspacePath(path);
    }

    private static void CleanupScratchRoot(string repoRoot, string scratchPath)
    {
        string full = Path.GetFullPath(scratchPath);
        if (!ScratchBindings.TryGetValue(full, out var binding))
            throw new InvalidOperationException(
                $"Refused to clean an unbound fixture path: '{scratchPath}'.");
        binding.RequireUnchanged();
        binding.RequireOrdinary(full, "scratch root");
        var ancestors = new List<Preview254ExternalSmpPinnedDirectory>();
        Preview254ExternalSmpPinnedCleanupTree? tree = null;
        Preview254ExternalSmpPinnedDirectory? rootLease = null;
        try
        {
            foreach (string ancestor in binding.Paths.Take(3))
                ancestors.Add(Preview254ExternalSmpFixtureFilesystem.OpenDirectoryLease(
                    ancestor, owned: false));
            binding.RequireUnchanged();
            var rootStat = binding.Stat(full);
            rootLease = Preview254ExternalSmpFixtureFilesystem.OpenDirectoryLease(
                full, owned: true);
            rootLease.RequireIdentity(rootStat);
            binding.RequireUnchanged();
            tree = Preview254ExternalSmpPinnedCleanupTree.Build(full, rootLease);
            rootLease = null;
            binding.RequireUnchanged();
            tree.DeleteBottomUp();
            ScratchBindings.Remove(full);
        }
        finally
        {
            tree?.Dispose();
            rootLease?.Dispose();
            foreach (var ancestor in ancestors.AsEnumerable().Reverse()) ancestor.Dispose();
        }
    }

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(path))));

    private static void RequireLegacyRecord(
        SkyrimFaceHeadPartRecordRoute actual,
        FormReference reference,
        SkyrimFaceRecordProvider provider,
        string editorId,
        AssetPath modelNif,
        AssetPath triPath,
        FormReference parent)
    {
        Require(actual.Reference == reference && actual.Provider == provider &&
                actual.EditorId == editorId &&
                actual.DeclaredType == NpcHeadPartType.Hair &&
                actual.EffectiveType == NpcHeadPartType.Hair &&
                actual.ModelNif == modelNif &&
                actual.TriRoutes.SequenceEqual([
                    new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, triPath)]) &&
                actual.ExtraParts.IsEmpty && actual.Parent == parent &&
                actual.Depth == 1 && !actual.IsSelected && !actual.IsRaceDefault &&
                actual.TextureSet is null,
            $"The complete legacy record golden changed for {reference}.");
    }

    private static void RequireGraphRecord(
        SkyrimFaceHeadPartGraphRoute actual,
        FormReference origin,
        PluginName requiredOutputMaster,
        FormReference winningForm,
        PluginName winningPlugin,
        Sha256Hash winningPluginSha256,
        long winningPluginByteLength,
        Sha256Hash winningRecordSha256,
        string editorId,
        NpcHeadPartType declaredType,
        NpcHeadPartType effectiveType,
        AssetPath? modelNif,
        ImmutableArray<SkyrimHdptTriRoute> triRoutes,
        ImmutableArray<FormReference> hnamEdges,
        FormReference? parent,
        int depth,
        bool isSelected,
        bool isRaceDefault,
        int routeOrder,
        NpcSex? appliesToSex,
        FormReference? validRace)
    {
        Require(actual.OriginForm == origin &&
                actual.RequiredOutputMaster == requiredOutputMaster &&
                actual.WinningForm == winningForm &&
                actual.WinningPlugin == winningPlugin &&
                actual.WinningPluginSha256 == winningPluginSha256 &&
                actual.WinningPluginByteLength == winningPluginByteLength &&
                actual.WinningRecordSha256 == winningRecordSha256 &&
                actual.EditorId == editorId &&
                actual.DeclaredType == declaredType &&
                actual.EffectiveType == effectiveType &&
                actual.ModelNif == modelNif &&
                actual.TriRoutes.SequenceEqual(triRoutes) &&
                actual.HnamEdges.SequenceEqual(hnamEdges) &&
                actual.Parent == parent && actual.Depth == depth &&
                actual.IsSelected == isSelected &&
                actual.IsRaceDefault == isRaceDefault &&
                actual.RouteOrder == routeOrder &&
                actual.AppliesToSex == appliesToSex &&
                actual.ValidRace == validRace,
            $"The complete graph golden changed for {origin}.");
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
