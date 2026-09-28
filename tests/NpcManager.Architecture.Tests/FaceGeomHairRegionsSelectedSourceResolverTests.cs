using System.Collections.Immutable;
using System.Drawing;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task
        TestFaceGeomHairRegionsSelectedSourceResolver()
    {
        var labRoot = new WorkspacePath(
            @"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"hair-regions-selected-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await TestOverriddenNpcFaceGeomOriginRoutingAsync(
                policy,
                labRoot,
                root);
            TestSelectedArchiveLengthPreReadGuard();
            await TestSelectedLooseFaceGeomAsync(
                policy,
                labRoot,
                root);
            await TestSelectedArchiveFaceGeomAsync(
                policy,
                labRoot,
                root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static async Task
        TestOverriddenNpcFaceGeomOriginRoutingAsync(
            IWorkspacePolicy policy,
            WorkspacePath labRoot,
            string parent)
    {
        const string ownerName = "Owner.esp";
        const string winnerName = "Patch.esp";
        foreach ((string assetOwner, bool expectedResolved) in
                 new[]
                 {
                     (ownerName, true),
                     (winnerName, false)
                 })
        {
            string root = Path.Combine(
                parent,
                $"overridden-{(expectedResolved ? "owner" : "winner")}-assets");
            string data = Path.Combine(root, "Data");
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(staging);
            WriteSelectedHairNpcPlugin(
                Path.Combine(data, ownerName));
            WriteSelectedHairNpcOverridePlugin(
                Path.Combine(data, winnerName),
                ownerName);
            string relativeFaceGeom =
                $"meshes/actors/character/FaceGenData/FaceGeom/{assetOwner}/00000800.nif";
            string source = Path.Combine(
                data,
                relativeFaceGeom.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(
                source,
                [0x4F, 0x57, 0x4E, 0x45, 0x52]);
            ReviewedGameIntake intake =
                await CreateSelectedSourceIntakeAsync(
                    labRoot,
                    root,
                    data,
                    ownerName,
                    winnerName);
            var owner = new PluginName(ownerName);
            var winner = new PluginName(winnerName);
            FaceGeomHairRegionsSelectedSourceResult result =
                await new BethesdaFaceGeomHairRegionsSelectedSourceResolver(
                        policy,
                        labRoot)
                    .ResolveAsync(
                        new FaceGeomHairRegionsSelectedSourceRequest(
                            intake,
                            new SkyrimMainWorkspaceIdentity(
                                owner,
                                winner,
                                new FormId(0x800),
                                "NPC_"),
                            new WorkspacePath(staging)),
                        CancellationToken.None);

            Assert(
                result.Resolved == expectedResolved &&
                (expectedResolved
                    ? result.Source is not null &&
                      result.Source.AssetPath == new AssetPath(
                          "meshes/actors/character/FaceGenData/FaceGeom/Owner.esp/00000800.nif") &&
                      result.Source.Source.Path ==
                          new WorkspacePath(source) &&
                      result.Source.PluginColorContext?.Plugin ==
                          winnerName
                    : result.Source is null),
                expectedResolved
                    ? "An overridden NPC did not resolve owner-directory FaceGeom while retaining Patch.esp winner provenance: " +
                      string.Join("; ", result.Diagnostics.Select(item =>
                          $"{item.Code}: {item.Message}"))
                    : "An overridden NPC incorrectly fell back to FaceGeom stored only under Patch.esp.");
        }
    }

    private static void
        TestSelectedArchiveLengthPreReadGuard()
    {
        const long maximum =
            128L * 1024L * 1024L;
        Assert(
            BethesdaNpcVisualSourceComposer
                .IsSelectedHairRegionsArchiveLengthAdmitted(
                    maximum) &&
            !BethesdaNpcVisualSourceComposer
                .IsSelectedHairRegionsArchiveLengthAdmitted(
                    0) &&
            !BethesdaNpcVisualSourceComposer
                .IsSelectedHairRegionsArchiveLengthAdmitted(
                    maximum + 1),
            "The selected-source resolver did not enforce its 128 MiB archive-member authority before materialization.");

        string implementation = File.ReadAllText(
            Path.Combine(
                FindGate220ProjectRoot(),
                "src",
                "NpcManager.Formats.Bethesda",
                "BethesdaFaceGeomHairRegionsSelectedSourceResolver.cs"));
        int boundCheck = implementation.IndexOf(
            "IsSelectedHairRegionsArchiveLengthAdmitted(",
            StringComparison.Ordinal);
        int archiveResolver = implementation.IndexOf(
            "new PreviewAssetResolver(",
            StringComparison.Ordinal);
        Assert(
            boundCheck >= 0 &&
            archiveResolver > boundCheck,
            "The 128 MiB archive winner check must remain before construction of the archive materializer and its first member read.");
    }

    private static async Task TestSelectedLooseFaceGeomAsync(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        string parent)
    {
        const string pluginName =
            "SelectedLooseFixture.esp";
        const string relativeFaceGeom =
            "meshes/actors/character/FaceGenData/FaceGeom/SelectedLooseFixture.esp/00000800.nif";
        string root = Path.Combine(
            parent,
            "loose");
        string data = Path.Combine(
            root,
            "Data");
        string staging = Path.Combine(
            root,
            "staging");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(staging);
        string pluginPath = Path.Combine(
            data,
            pluginName);
        WriteSelectedHairNpcPlugin(
            pluginPath);
        string source = Path.Combine(
            data,
            relativeFaceGeom.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(source)!);
        byte[] expected = [0x47, 0x41, 0x4D, 0x45, 0x2D, 0x4E, 0x49, 0x46];
        await File.WriteAllBytesAsync(
            source,
            expected);
        ReviewedGameIntake intake =
            await CreateSelectedSourceIntakeAsync(
                labRoot,
                root,
                data,
                pluginName);
        var resolver =
            new BethesdaFaceGeomHairRegionsSelectedSourceResolver(
                policy,
                labRoot);
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName(pluginName),
            new PluginName(pluginName),
            new FormId(0x800),
            "NPC_");

        FaceGeomHairRegionsSelectedSourceResult result =
            await resolver.ResolveAsync(
                new FaceGeomHairRegionsSelectedSourceRequest(
                    intake,
                    identity,
                    new WorkspacePath(staging)),
                CancellationToken.None);
        Assert(
            result.Resolved &&
            result.Source is not null,
            "The exact loose selected FaceGeom was not resolved: " +
            string.Join(
                "; ",
                result.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        FaceGeomHairRegionsSelectedSource selected =
            result.Source!;
        Assert(
            selected.AssetPath ==
                new AssetPath(relativeFaceGeom) &&
            selected.Source.Path ==
                new WorkspacePath(source) &&
            selected.Source.ByteLength ==
                expected.LongLength &&
            selected.Source.Sha256 ==
                HashSelectedSource(expected) &&
            selected.ProviderKind ==
                AssetProviderKind.Loose &&
            selected.Provider == "loose" &&
            !selected.MaterializedFromArchive &&
            selected.PluginColorContext?.HairColorHex ==
                "#D6BE83" &&
            !Directory.EnumerateFileSystemEntries(
                staging).Any(),
            "Loose selection did not preserve the exact winning path/hash/provider or wrote unnecessary staging bytes.");

        await File.AppendAllTextAsync(
            intake.LoadOrderPath.Value,
            Environment.NewLine + "Unexpected.esp");
        FaceGeomHairRegionsSelectedSourceResult stale =
            await resolver.ResolveAsync(
                new FaceGeomHairRegionsSelectedSourceRequest(
                    intake,
                    identity,
                    new WorkspacePath(staging)),
                CancellationToken.None);
        Assert(
            !stale.Resolved &&
            stale.Source is null &&
            stale.Diagnostics.Any(item =>
                item.Code ==
                "facegeom-hair-regions-source-load-order-stale"),
            "A selected-source request accepted stale reviewed load-order bytes.");

        ReviewedGameIntake rebound =
            await CreateSelectedSourceIntakeAsync(
                labRoot,
                root,
                data,
                pluginName);
        FaceGeomHairRegionsSelectedSourceResult wrongProvider =
            await resolver.ResolveAsync(
                new FaceGeomHairRegionsSelectedSourceRequest(
                    rebound,
                    identity with
                    {
                        WinningProvider =
                            new PluginName(
                                "WrongProvider.esp")
                    },
                    new WorkspacePath(staging)),
                CancellationToken.None);
        Assert(
            !wrongProvider.Resolved &&
            wrongProvider.Diagnostics.Any(item =>
                item.Code ==
                "facegeom-hair-regions-selected-source-provider"),
            "The selected-source resolver accepted a stale winning NPC provider.");

        FaceGeomHairRegionsSelectedSourceResult liveStaging =
            await resolver.ResolveAsync(
                new FaceGeomHairRegionsSelectedSourceRequest(
                    rebound,
                    identity,
                    new WorkspacePath(
                        @"F:\ExampleGame\forbidden-hair-region-staging")),
                CancellationToken.None);
        Assert(
            !liveStaging.Resolved &&
            liveStaging.Diagnostics.Any(item =>
                item.Code ==
                "facegeom-hair-regions-selected-source-staging"),
            "The selected-source resolver accepted a live-modlist staging path.");
    }

    private static async Task TestSelectedArchiveFaceGeomAsync(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        string parent)
    {
        const string pluginName =
            "SelectedArchiveFixture.esp";
        const string relativeFaceGeom =
            "meshes/actors/character/FaceGenData/FaceGeom/SelectedArchiveFixture.esp/00000800.nif";
        string root = Path.Combine(
            parent,
            "archive");
        string data = Path.Combine(
            root,
            "Data");
        string archiveInput = Path.Combine(
            root,
            "archive-input");
        string staging = Path.Combine(
            root,
            "staging");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(archiveInput);
        Directory.CreateDirectory(staging);
        string pluginPath = Path.Combine(
            data,
            pluginName);
        WriteSelectedHairNpcPlugin(
            pluginPath);
        string member = Path.Combine(
            archiveInput,
            relativeFaceGeom.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(member)!);
        byte[] expected =
            [0x42, 0x53, 0x41, 0x2D, 0x4E, 0x49, 0x46, 0x2D, 0x31];
        await File.WriteAllBytesAsync(
            member,
            expected);
        var bsa = new BethesdaSkyrimBsaService(
            policy,
            labRoot);
        SkyrimBsaBuildResult built =
            await bsa.BuildAsync(
                new SkyrimBsaBuildRequest(
                    new WorkspacePath(archiveInput),
                    new WorkspacePath(Path.Combine(
                        data,
                        "SelectedArchiveFixture.bsa")),
                    [new AssetPath(relativeFaceGeom)]),
                CancellationToken.None);
        Assert(
            built.Written,
            "The selected-source BSA fixture could not be built.");
        ReviewedGameIntake intake =
            await CreateSelectedSourceIntakeAsync(
                labRoot,
                root,
                data,
                pluginName);
        var resolver =
            new BethesdaFaceGeomHairRegionsSelectedSourceResolver(
                policy,
                labRoot);
        FaceGeomHairRegionsSelectedSourceResult result =
            await resolver.ResolveAsync(
                new FaceGeomHairRegionsSelectedSourceRequest(
                    intake,
                    new SkyrimMainWorkspaceIdentity(
                        new PluginName(pluginName),
                        new PluginName(pluginName),
                        new FormId(0x800),
                        "NPC_"),
                    new WorkspacePath(staging)),
                CancellationToken.None);
        Assert(
            result.Resolved &&
            result.Source is not null,
            "The exact BSA selected FaceGeom was not resolved: " +
            string.Join(
                "; ",
                result.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        FaceGeomHairRegionsSelectedSource selected =
            result.Source!;
        Assert(
            selected.ProviderKind ==
                AssetProviderKind.Archive &&
            selected.MaterializedFromArchive &&
            selected.Source.Path.IsUnder(
                new WorkspacePath(staging)) &&
            selected.Source.Sha256 ==
                HashSelectedSource(expected) &&
            selected.Source.ByteLength ==
                expected.LongLength &&
            (await File.ReadAllBytesAsync(
                selected.Source.Path.Value))
                .SequenceEqual(expected),
            "BSA selection did not materialize one exact hash-bound K-local FaceGeom.");
    }

    private static async ValueTask<ReviewedGameIntake>
        CreateSelectedSourceIntakeAsync(
            WorkspacePath labRoot,
            string root,
            string data,
            params string[] pluginNames)
    {
        string loadOrderPath = Path.Combine(
            root,
            "loadorder.txt");
        await File.WriteAllTextAsync(
            loadOrderPath,
            string.Join(Environment.NewLine, pluginNames));
        var plugins = ImmutableArray.CreateBuilder<
            PluginClosureReviewEntry>();
        for (int pluginIndex = 0;
             pluginIndex < pluginNames.Length;
             pluginIndex++)
        {
            string pluginPath = Path.Combine(
                data,
                pluginNames[pluginIndex]);
            plugins.Add(new PluginClosureReviewEntry(
                new PluginName(pluginNames[pluginIndex]),
                pluginIndex,
                true,
                true,
                true,
                false,
                true,
                new WorkspacePath(pluginPath),
                HashSelectedSource(
                    await File.ReadAllBytesAsync(pluginPath)),
                []));
        }
        AssetIndex index =
            await new BethesdaAssetIndexer().IndexAsync(
                new AssetIndexRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(data)),
                CancellationToken.None);
        Assert(
            !index.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error),
            "Selected-source asset indexing failed.");
        Sha256Hash loadOrderHash =
            HashSelectedSource(
                await File.ReadAllBytesAsync(loadOrderPath));
        Sha256Hash assetFingerprint =
            AssetProviderInventoryAuthority.Fingerprint(
                index.Providers);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(loadOrderPath),
            new WorkspacePath(Path.Combine(
                root,
                "unused-intake-output")),
            loadOrderHash,
            plugins.ToImmutable(),
            [],
            [],
            [],
            index.Providers.Length,
            assetFingerprint,
            new Sha256Hash(new string('0', 64)),
            RuntimeAuthority: false);
        return intake with
        {
            IntakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(intake)
        };
    }

    private static void WriteSelectedHairNpcPlugin(
        string path)
    {
        ModKey key =
            ModKey.FromNameAndExtension(
                Path.GetFileName(path));
        var mod = new SkyrimMod(
            key,
            SkyrimRelease.SkyrimSE);
        var colorKey = new FormKey(
            key,
            0x940);
        mod.Colors.Add(
            new ColorRecord(
                colorKey,
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "SelectedFixtureBlonde",
                Color = Color.FromArgb(
                    255,
                    0xD6,
                    0xBE,
                    0x83)
            });
        mod.Npcs.Add(
            new Npc(
                new FormKey(key, 0x800),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "SelectedHairNpc",
                HairColor =
                    new FormLinkNullable<
                        IColorRecordGetter>(
                        colorKey)
            });
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                RecordCount =
                    RecordCountOption.Iterate
            });
    }

    private static void WriteSelectedHairNpcOverridePlugin(
        string path,
        string ownerName)
    {
        ModKey owner = ModKey.FromNameAndExtension(ownerName);
        ModKey winner =
            ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(
            winner,
            SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = owner });
        mod.Npcs.Add(
            new Npc(
                new FormKey(owner, 0x800),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "SelectedHairNpcOverride",
                HairColor =
                    new FormLinkNullable<IColorRecordGetter>(
                        new FormKey(owner, 0x940))
            });
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                RecordCount =
                    RecordCountOption.Iterate
            });
    }

    private static Sha256Hash HashSelectedSource(
        ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));
}
