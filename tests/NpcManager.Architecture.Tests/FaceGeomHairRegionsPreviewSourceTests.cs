using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task
        EmitAuthenticFaceGeomHairRegionsRequest(
            string analysisValue,
            string analysisSha256Value,
            string requestValue,
            string outputValue,
            string manifestValue)
    {
        WorkspacePath labRoot =
            new(@"K:\ExampleWorkspace");
        var documents =
            new FaceGeomHairRegionsDocumentCodec(
                labRoot);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysis =
                await documents.LoadAnalysisAsync(
                    new WorkspacePath(analysisValue),
                    new Sha256Hash(analysisSha256Value),
                    CancellationToken.None);
        ImmutableArray<FaceGeomHairRegionAssignment>
            assignments = analysis.Value.Regions
                .Select(region =>
                    new FaceGeomHairRegionAssignment(
                        region.StructuralId,
                        region.StructuralId switch
                        {
                            "shape:16:shader:20" or
                                "shape:44:shader:48" =>
                                FaceGeomHairRegionRole.Primary,
                            "shape:51:shader:55" =>
                                FaceGeomHairRegionRole.Accent,
                            _ =>
                                FaceGeomHairRegionRole.Preserve
                        }))
                .ToImmutableArray();
        var value = new FaceGeomHairRegionsRequest(
            FaceGeomHairRegionSchemas.Request,
            analysis.Sha256,
            analysis.Value.Source,
            "#D6BE83",
            "#F4E3B2",
            assignments,
            new WorkspacePath(outputValue),
            new WorkspacePath(manifestValue));
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            authority = documents.BindRequest(value);
        await using (var output = new FileStream(
                         requestValue,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.WriteThrough))
        {
            byte[] bytes = authority.Utf8Json.ToArray();
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        Console.WriteLine(
            "PASS authentic hair-region request " +
            $"{authority.Sha256.Value} " +
            $"{authority.Utf8Json.Length} {requestValue}");
    }

    private static async Task
        EmitFaceGeomHairRegionsReviewedIntake(
            string dataRootValue,
            string loadOrderValue,
            string outputRootValue,
            string intakeDocumentValue)
    {
        WorkspacePath labRoot =
            new(@"K:\ExampleWorkspace");
        var dataRoot = new WorkspacePath(dataRootValue);
        var loadOrder = new WorkspacePath(loadOrderValue);
        var outputRoot = new WorkspacePath(outputRootValue);
        var intakeDocument =
            new WorkspacePath(intakeDocumentValue);
        string[] reviewedPluginOrder =
            File.Exists(Path.Combine(
                dataRoot.Value,
                "BrigitteBardotNpcManager.esp"))
                ? AuthenticBrigittePluginOrder
                    .Append(
                        "BrigitteBardotNpcManager.esp")
                    .ToArray()
                : AuthenticBrigittePluginOrder;
        ImmutableArray<PluginClosureReviewEntry> plugins =
            reviewedPluginOrder
                .Select((name, index) =>
                {
                    string path = Path.Combine(
                        dataRoot.Value,
                        name);
                    return new PluginClosureReviewEntry(
                        new PluginName(name),
                        index,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(path),
                        HashNpcPreviewTestFile(path),
                        []);
                })
                .ToImmutableArray();
        AssetIndex providerIndex =
            await new BethesdaAssetIndexer().IndexAsync(
                new AssetIndexRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot),
                CancellationToken.None);
        if (providerIndex.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
            throw new InvalidDataException(
                "Authentic reviewed intake provider indexing failed: " +
                string.Join(
                    "; ",
                    providerIndex.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
        Sha256Hash loadOrderHash =
            HashNpcPreviewTestFile(
                loadOrder.Value);
        Sha256Hash assetFingerprint =
            AssetProviderInventoryAuthority.Fingerprint(
                providerIndex.Providers);
        Sha256Hash intakeFingerprint =
            ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                dataRoot,
                loadOrder,
                outputRoot,
                loadOrderHash,
                plugins,
                [],
                [],
                [],
                assetFingerprint);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            dataRoot,
            loadOrder,
            outputRoot,
            loadOrderHash,
            plugins,
            [],
            [],
            [],
            providerIndex.Providers.Length,
            assetFingerprint,
            intakeFingerprint,
            false);

        var documents =
            new FaceGeomHairRegionsDocumentCodec(
                labRoot);
        ReviewedGameIntakeDocumentAuthority authority =
            documents.BindReviewedIntake(
                intake,
                intakeDocument);
        await using (var output = new FileStream(
                         intakeDocument.Value,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.WriteThrough))
        {
            byte[] bytes =
                authority.Document.Utf8Json.ToArray();
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
        Console.WriteLine(
            "PASS authentic reviewed intake " +
            $"{authority.Document.Sha256.Value} " +
            $"{authority.Document.ByteLength} " +
            $"{intakeDocument.Value}");
    }

    private static async Task
        TestFaceGeomHairRegionsPreviewSource()
    {
        WorkspacePath labRoot = new(@"K:\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"hair-regions-preview-source-test-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "Data");
        string bsaSource = Path.Combine(root, "bsa-source");
        string lateBsaSource = Path.Combine(
            root,
            "late-bsa-source");
        string outputOne = Path.Combine(root, "resolved-one");
        string outputTwo = Path.Combine(root, "resolved-two");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(bsaSource);
        Directory.CreateDirectory(lateBsaSource);
        Directory.CreateDirectory(outputOne);
        Directory.CreateDirectory(outputTwo);
        try
        {
            string looseRoute =
                "textures/fixture/hair-loose.dds";
            string archiveRoute =
                "textures/fixture/hair-archive.dds";
            WriteNpcVisualFile(
                data,
                looseRoute,
                [0x44, 0x44, 0x53, 0x20, 0x11]);
            WriteNpcVisualFile(
                bsaSource,
                looseRoute,
                [0x44, 0x44, 0x53, 0x20, 0x22]);
            WriteNpcVisualFile(
                bsaSource,
                archiveRoute,
                [0x44, 0x44, 0x53, 0x20, 0x33]);
            byte[] lateArchiveBytes =
                [0x44, 0x44, 0x53, 0x20, 0x55];
            WriteNpcVisualFile(
                lateBsaSource,
                archiveRoute,
                lateArchiveBytes);

            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var bsaService = new BethesdaSkyrimBsaService(
                policy,
                labRoot);
            SkyrimBsaBuildResult bsa =
                await bsaService.BuildAsync(
                    new SkyrimBsaBuildRequest(
                        new WorkspacePath(bsaSource),
                        new WorkspacePath(Path.Combine(
                            data,
                            "HairRegionFixture.bsa")),
                        [
                            new AssetPath(looseRoute),
                            new AssetPath(archiveRoute)
                        ]),
                    CancellationToken.None);
            Assert(
                bsa.Written,
                "The physical hair-region provider BSA did not build.");
            SkyrimBsaBuildResult lateBsa =
                await bsaService.BuildAsync(
                    new SkyrimBsaBuildRequest(
                        new WorkspacePath(lateBsaSource),
                        new WorkspacePath(Path.Combine(
                            data,
                            "HairRegionLate.bsa")),
                        [new AssetPath(archiveRoute)]),
                    CancellationToken.None);
            Assert(
                lateBsa.Written,
                "The later physical hair-region provider BSA did not build.");

            string pluginPath = Path.Combine(
                data,
                "HairRegionFixture.esp");
            File.WriteAllBytes(pluginPath, [1, 2, 3, 4]);
            string latePluginPath = Path.Combine(
                data,
                "HairRegionLate.esp");
            File.WriteAllBytes(
                latePluginPath,
                [5, 6, 7, 8]);
            string loadOrderPath = Path.Combine(
                root,
                "loadorder.txt");
            File.WriteAllText(
                loadOrderPath,
                "UnrelatedBefore.esp\n" +
                "HairRegionFixture.esp\n" +
                "UnrelatedMiddle.esp\n" +
                "HairRegionLate.esp");
            var plugin = new PluginName(
                "HairRegionFixture.esp");
            var latePlugin = new PluginName(
                "HairRegionLate.esp");
            AssetIndex providerIndex =
                await new BethesdaAssetIndexer().IndexAsync(
                    new AssetIndexRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(data)),
                    CancellationToken.None);
            Assert(
                !providerIndex.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error),
                "Controlled provider inventory did not index.");
            var dataRootPath =
                new WorkspacePath(data);
            var loadOrderWorkspacePath =
                new WorkspacePath(loadOrderPath);
            var intakeOutputRoot =
                new WorkspacePath(Path.Combine(
                    root,
                    "unused-output"));
            Sha256Hash loadOrderHash =
                HashNpcPreviewTestFile(loadOrderPath);
            ImmutableArray<PluginClosureReviewEntry>
                reviewedPlugins =
                [
                    new PluginClosureReviewEntry(
                        plugin,
                        1,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(pluginPath),
                        HashNpcPreviewTestFile(pluginPath),
                        []),
                    new PluginClosureReviewEntry(
                        latePlugin,
                        3,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(latePluginPath),
                        HashNpcPreviewTestFile(latePluginPath),
                        [])
                ];
            Sha256Hash assetFingerprint =
                AssetProviderInventoryAuthority.Fingerprint(
                    providerIndex.Providers);
            Sha256Hash intakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                    GameEdition.SkyrimSpecialEdition,
                    labRoot,
                    dataRootPath,
                    loadOrderWorkspacePath,
                    intakeOutputRoot,
                    loadOrderHash,
                    reviewedPlugins,
                    [],
                    [],
                    [],
                    assetFingerprint);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                dataRootPath,
                loadOrderWorkspacePath,
                intakeOutputRoot,
                loadOrderHash,
                reviewedPlugins,
                [],
                [],
                [],
                providerIndex.Providers.Length,
                assetFingerprint,
                intakeFingerprint,
                false);
            string candidatePath = Path.Combine(
                root,
                "candidate.nif");
            byte[] candidateBytes = TextureSet(
                looseRoute,
                archiveRoute);
            File.WriteAllBytes(
                candidatePath,
                candidateBytes);
            var candidate = new FaceGeomHairRegionsFile(
                new WorkspacePath(candidatePath),
                candidateBytes.LongLength,
                new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(candidateBytes))));
            var service =
                new BethesdaFaceGeomHairRegionsPreviewSourceService(
                    policy,
                    labRoot);

            FaceGeomHairRegionsPreviewSourceResult first =
                await service.ComposeAsync(
                    new FaceGeomHairRegionsPreviewSourceRequest(
                        candidate,
                        candidateBytes.ToImmutableArray(),
                        intake,
                        new WorkspacePath(outputOne)),
                    CancellationToken.None);
            FaceGeomHairRegionsPreviewSourceResult second =
                await service.ComposeAsync(
                    new FaceGeomHairRegionsPreviewSourceRequest(
                        candidate,
                        candidateBytes.ToImmutableArray(),
                        intake,
                        new WorkspacePath(outputTwo)),
                    CancellationToken.None);

            Assert(
                first.Composed &&
                first.Source is not null &&
                second.Composed &&
                second.Source is not null,
                "The standalone hair-region source did not compose: " +
                string.Join(
                    "; ",
                    first.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));

            var semanticDrifts =
                new (string Name, string Contents)[]
                {
                    (
                        "missing",
                        "UnrelatedBefore.esp\n" +
                        "HairRegionFixture.esp\n" +
                        "UnrelatedMiddle.esp"),
                    (
                        "duplicate",
                        "UnrelatedBefore.esp\n" +
                        "HairRegionFixture.esp\n" +
                        "HairRegionFixture.esp\n" +
                        "HairRegionLate.esp"),
                    (
                        "order",
                        "UnrelatedBefore.esp\n" +
                        "HairRegionLate.esp\n" +
                        "UnrelatedMiddle.esp\n" +
                        "HairRegionFixture.esp"),
                    (
                        "extra-at-bound-order",
                        "UnrelatedBefore.esp\n" +
                        "Unexpected.esp\n" +
                        "UnrelatedMiddle.esp\n" +
                        "HairRegionLate.esp\n" +
                        "HairRegionFixture.esp")
                };
            foreach ((string name, string contents) in
                     semanticDrifts)
            {
                string driftLoadOrder = Path.Combine(
                    root,
                    $"loadorder-{name}.txt");
                File.WriteAllText(
                    driftLoadOrder,
                    contents);
                var driftPath =
                    new WorkspacePath(
                        driftLoadOrder);
                var driftHash =
                    HashNpcPreviewTestFile(
                        driftLoadOrder);
                ReviewedGameIntake drift =
                    intake with
                    {
                        LoadOrderPath = driftPath,
                        LoadOrderHash = driftHash,
                        IntakeFingerprint =
                            new Sha256Hash(
                                new string('0', 64))
                    };
                drift = drift with
                {
                    IntakeFingerprint =
                        ReviewedGameIntakeFingerprintAuthority
                            .Fingerprint(drift)
                };
                string driftOutput = Path.Combine(
                    root,
                    $"resolved-{name}");
                Directory.CreateDirectory(
                    driftOutput);
                FaceGeomHairRegionsPreviewSourceResult
                    refused =
                        await service.ComposeAsync(
                            new FaceGeomHairRegionsPreviewSourceRequest(
                                candidate,
                                candidateBytes
                                    .ToImmutableArray(),
                                drift,
                                new WorkspacePath(
                                    driftOutput)),
                            CancellationToken.None);
                Assert(
                    !refused.Composed &&
                    refused.Diagnostics.Any(item =>
                        item.Code ==
                        "facegeom-hair-regions-source-load-order-semantics"),
                    $"Semantic load-order drift '{name}' was not refused after its bytes/hash/fingerprint were rebound.");
            }
            FaceGeomHairRegionsPreviewSource source =
                first.Source!;
            Assert(
                source.Candidate.Sha256 ==
                    candidate.Sha256 &&
                source.Candidate.Bytes ==
                    candidate.ByteLength &&
                source.Candidate.MaterializedPath ==
                    candidate.Path &&
                source.Candidate.Materials.Length == 1,
                "The exact candidate bytes/material graph were not retained.");
            Assert(
                source.Textures.Select(item =>
                        item.AssetPath.Value)
                    .SequenceEqual(
                        [archiveRoute, looseRoute],
                        StringComparer.OrdinalIgnoreCase),
                "The texture inventory is not sorted deterministically.");
            Assert(
                source.Textures.Single(item =>
                        item.AssetPath.Value == looseRoute)
                    .Provider == "loose" &&
                source.Textures.Single(item =>
                        item.AssetPath.Value == archiveRoute)
                    .Provider ==
                    "bsa:HairRegionLate.bsa|plugin:HairRegionLate.esp|order:1" &&
                source.Textures.Single(item =>
                        item.AssetPath.Value == archiveRoute)
                    .Sha256 ==
                    new Sha256Hash(Convert.ToHexString(
                        SHA256.HashData(
                            lateArchiveBytes))),
                "Loose precedence or plugin/load-order BSA provenance drifted.");
            Assert(
                source.TextureFingerprintSha256 ==
                    second.Source!.TextureFingerprintSha256 &&
                source.Textures.All(item =>
                    item.MaterializedPath.IsUnder(
                        new WorkspacePath(outputOne)) &&
                    File.Exists(item.MaterializedPath.Value)),
                "The sorted provider fingerprint is unstable or materialized textures escaped the transient root.");

            string tamperedOutput = Path.Combine(
                root,
                "resolved-tampered-intake");
            Directory.CreateDirectory(tamperedOutput);
            FaceGeomHairRegionsPreviewSourceResult tampered =
                await service.ComposeAsync(
                    new FaceGeomHairRegionsPreviewSourceRequest(
                        candidate,
                        candidateBytes.ToImmutableArray(),
                        intake with
                        {
                            IntakeFingerprint =
                                new Sha256Hash(
                                    new string('f', 64))
                        },
                        new WorkspacePath(tamperedOutput)),
                    CancellationToken.None);
            Assert(
                !tampered.Composed &&
                tampered.Source is null &&
                tampered.Diagnostics.Any(item =>
                    item.Code ==
                    "facegeom-hair-regions-source-intake-fingerprint") &&
                !Directory.EnumerateFileSystemEntries(
                    tamperedOutput).Any(),
                "The source accepted a reviewed intake whose canonical fingerprint no longer matched its typed fields.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
