using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task<int>
        EmitSavePackageProductionFixtureAsync(string destination)
    {
        try
        {
            var labRoot = new WorkspacePath(
                "K:\\ExampleWorkspace");
            var root = new WorkspacePath(destination);
            if (!root.IsUnder(labRoot))
                throw new InvalidDataException(
                    "The Gate 023 fixture must remain under K:\\ExampleWorkspace.");
            if (File.Exists(root.Value) ||
                Directory.Exists(root.Value))
                throw new IOException(
                    "The Gate 023 fixture destination already exists.");
            string? parent = Path.GetDirectoryName(root.Value);
            if (parent is null || !Directory.Exists(parent))
                throw new DirectoryNotFoundException(
                    "The Gate 023 fixture parent must already exist.");
            Directory.CreateDirectory(root.Value);
            try
            {
                await WriteGate23ProductionPackageAsync(
                    labRoot,
                    root.Value,
                    Gate23BriarMembers());
            }
            catch
            {
                if (Directory.Exists(root.Value))
                    Directory.Delete(root.Value, recursive: true);
                throw;
            }
            Console.WriteLine(root.Value);
            return 0;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task
        TestSkyrimSavePackageProductionWithRealBriar()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-023-production-tests",
            Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string bsaOutput = Path.Combine(root, "bsa-output");
        string bsaOutputRepeat = Path.Combine(
            root,
            "bsa-output-repeat");
        string looseOutput = Path.Combine(root, "loose-output");
        string cancelledOutput = Path.Combine(root, "cancelled-output");
        Directory.CreateDirectory(source);
        try
        {
            ImmutableArray<AssetPath> briar = Gate23BriarMembers();
            await WriteGate23ProductionPackageAsync(
                labRoot,
                source,
                briar);
            string sourceManifest = Path.Combine(
                source,
                "npcmanager-package.json");
            Sha256Hash sourceManifestHash =
                HashGate23ProductionFile(sourceManifest);

            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var manifestReader = new PackageManifestReader(
                policy,
                labRoot);
            var verifier = new PackageVerifyService(manifestReader);
            var bsaService = new BethesdaSkyrimBsaService(
                policy,
                labRoot);
            var service = new SkyrimSavePackageService(
                new PackageInspectService(manifestReader),
                verifier,
                new BethesdaSkyrimSavePluginTransformer(
                    policy,
                    labRoot),
                new BethesdaSkyrimSavePluginVerifier(
                    policy,
                    labRoot),
                bsaService,
                new BethesdaPluginReader(),
                policy,
                labRoot);

            var bsaChoices = new SkyrimSavePackageChoiceRequest(
                SkyrimSaveScope.AllChanged,
                SkyrimSaveTargetMode.UpdateExisting,
                true,
                false,
                SkyrimSaveEncodingMode.Utf8,
                SkyrimSaveArchiveMode.Bsa,
                SkyrimSaveLeveledListMode.Existing,
                "Gate23ExistingActors",
                true);
            SkyrimSavePackageReviewResult review =
                await service.ReviewAsync(
                    new SkyrimSavePackageReviewRequest(
                        new WorkspacePath(source),
                        new WorkspacePath(bsaOutput),
                        bsaChoices),
                    CancellationToken.None);
            Assert(review.Accepted &&
                   review.Artifact is not null &&
                   review.Artifact.Snapshot.NpcRecordCount == 2 &&
                   review.Artifact.Snapshot.Npcs.Length == 2 &&
                   review.Artifact.Snapshot.AuthoredRecordCount == 4 &&
                   review.Artifact.Snapshot.LeveledRecordCount == 2 &&
                   review.Artifact.Snapshot.LeveledNpcEditorIds.Contains(
                       "Gate23ExistingActors",
                       StringComparer.OrdinalIgnoreCase) &&
                   review.Artifact.Snapshot.HasFaceGeom &&
                   review.Artifact.Snapshot.HasFaceTint &&
                   review.Artifact.Snapshot.HasBodySlideSidecar &&
                   review.Artifact.Snapshot.HasBodyGen &&
                   review.Artifact.Snapshot.HasApplyScript,
                $"Connected production review lost NPC/record/output authority: {string.Join(
                    "; ",
                    review.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"))}");

            SkyrimSavePackageExecutionResult bsa =
                await service.ExecuteAsync(
                    new SkyrimSavePackageExecutionRequest(
                        review.Artifact!),
                    null,
                    CancellationToken.None);
            Assert(bsa.Completed &&
                   bsa.Artifact is not null &&
                   bsa.Artifact.PluginTransform is not null &&
                   bsa.Artifact.PluginTransform.MarkAsMaster &&
                   !bsa.Artifact.PluginTransform.LightMaster &&
                   bsa.Artifact.PluginTransform.LeveledNpcEntries
                       .SequenceEqual(
                       [
                           new FormId(0x800),
                           new FormId(0x801)
                       ]) &&
                   bsa.Artifact.PluginTransform.OtherRecordCount == 4 &&
                   bsa.Artifact.ArchiveBuild is not null &&
                   bsa.Artifact.ArchiveBuild.Version == 105 &&
                   !bsa.Artifact.ArchiveBuild.Compressed &&
                   bsa.Artifact.ArchiveBuild.Members.Length ==
                   briar.Length + 2 &&
                   bsa.Artifact.ArchiveBuild.Members.All(item =>
                       item.Matches) &&
                   bsa.PackageVerification?.Verified == true &&
                   HashGate23ProductionFile(sourceManifest) ==
                   sourceManifestHash,
                $"Connected BSA package production failed: {string.Join(
                    "; ",
                    bsa.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"))}");

            string archive = Path.Combine(
                bsaOutput,
                "Gate23Transform.bsa");
            Assert(File.Exists(archive) &&
                   briar.All(path => !File.Exists(
                       Gate23Path(bsaOutput, path))) &&
                   File.Exists(Path.Combine(
                       bsaOutput,
                       "meshes",
                       "actors",
                       "character",
                       "BodyGenData",
                       "Gate23Transform.esp",
                       "morphs.ini")) &&
                   File.Exists(Path.Combine(
                       bsaOutput,
                       "scripts",
                       "NPCM_Manolov_ApplySSE.pex")),
                "BSA mode did not archive mesh/texture assets while retaining loose-only outputs.");
            SkyrimBsaVerifyResult bsaReadback =
                await bsaService.VerifyAsync(
                    new SkyrimBsaVerifyRequest(
                        new WorkspacePath(archive),
                        bsa.Artifact!.ArchiveBuild!.Members),
                    CancellationToken.None);
            Assert(bsaReadback.Verified &&
                   bsaReadback.Members.All(item => item.Matches),
                "Final connected BSA failed independent member readback.");

            SkyrimSavePackageReviewResult repeatedReview =
                await service.ReviewAsync(
                    new SkyrimSavePackageReviewRequest(
                        new WorkspacePath(source),
                        new WorkspacePath(bsaOutputRepeat),
                        bsaChoices),
                    CancellationToken.None);
            SkyrimSavePackageExecutionResult repeated =
                await service.ExecuteAsync(
                    new SkyrimSavePackageExecutionRequest(
                        repeatedReview.Artifact ??
                        throw new InvalidOperationException(
                            "Repeated production review failed.")),
                    null,
                    CancellationToken.None);
            Assert(repeated.Completed &&
                   Gate23PackageHashes(bsaOutput)
                       .SequenceEqual(
                           Gate23PackageHashes(
                               bsaOutputRepeat)),
                "Two connected save/package productions did not reproduce every relative output hash.");

            var looseChoices = new SkyrimSavePackageChoiceRequest(
                SkyrimSaveScope.SelectedOnly,
                SkyrimSaveTargetMode.UpdateExisting,
                true,
                false,
                SkyrimSaveEncodingMode.PreservePackageBytes,
                SkyrimSaveArchiveMode.Loose,
                SkyrimSaveLeveledListMode.PreservePackageRecords);
            SkyrimSavePackageReviewResult looseReview =
                await service.ReviewAsync(
                    new SkyrimSavePackageReviewRequest(
                        new WorkspacePath(bsaOutput),
                        new WorkspacePath(looseOutput),
                        looseChoices),
                    CancellationToken.None);
            Assert(looseReview.Accepted &&
                   looseReview.Artifact is not null &&
                   looseReview.Artifact.Snapshot.HasFaceGeom &&
                   looseReview.Artifact.Snapshot.HasFaceTint,
                $"Fresh-derived BSA review lost archived output facts: {string.Join(
                    "; ",
                    looseReview.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"))}");
            SkyrimSavePackageExecutionResult loose =
                await service.ExecuteAsync(
                    new SkyrimSavePackageExecutionRequest(
                        looseReview.Artifact!),
                    null,
                    CancellationToken.None);
            Assert(loose.Completed &&
                   loose.Artifact is not null &&
                   loose.Artifact.ArchiveBuild is null &&
                   loose.Artifact.ArchiveMembers.Length ==
                   briar.Length + 2 &&
                   loose.Artifact.PluginTransform is not null &&
                   loose.Artifact.PluginTransform.SourceSha256 ==
                   loose.Artifact.PluginTransform.OutputSha256 &&
                   !Directory.EnumerateFiles(
                       looseOutput,
                       "*.bsa",
                       SearchOption.TopDirectoryOnly).Any() &&
                   briar.All(path =>
                       File.Exists(Gate23Path(looseOutput, path)) &&
                       HashGate23ProductionFile(
                           Gate23Path(looseOutput, path)) ==
                       HashGate23ProductionFile(
                           Gate23Path(source, path))),
                $"Connected loose conversion did not restore exact real Briar bytes: {string.Join(
                    "; ",
                    loose.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"))}");
            string proposalText = await File.ReadAllTextAsync(
                Path.Combine(
                    looseOutput,
                    "npcmanager-save-proposal.json"));
            Assert(proposalText.Contains(
                       "\"targetMode\": \"UpdateExisting\"",
                       StringComparison.Ordinal) &&
                   proposalText.Contains(
                       "\"runtimeAuthority\": false",
                       StringComparison.Ordinal),
                "Fresh-derived update did not persist its reviewed mode and runtime boundary.");

            SkyrimSavePackageReviewResult cancelReview =
                await service.ReviewAsync(
                    new SkyrimSavePackageReviewRequest(
                        new WorkspacePath(looseOutput),
                        new WorkspacePath(cancelledOutput)),
                    CancellationToken.None);
            Assert(cancelReview.Accepted &&
                   cancelReview.Artifact is not null,
                "Cancellation control could not bind the produced loose package.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool cancellationObserved = false;
            try
            {
                _ = await service.ExecuteAsync(
                    new SkyrimSavePackageExecutionRequest(
                        cancelReview.Artifact!),
                    null,
                    cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Assert(cancellationObserved &&
                   !Directory.Exists(cancelledOutput),
                "Cancelled production retained a destination package.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteGate23ProductionPackageAsync(
        WorkspacePath labRoot,
        string root,
        ImmutableArray<AssetPath> briar)
    {
        string plugin = Path.Combine(root, "Gate23Transform.esp");
        WriteGate23TransformSource(plugin);
        string realData = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "01-source-copies",
            "real-modlist",
            "briar-armor-20260723",
            "Data");
        foreach (AssetPath path in briar)
        {
            string destination = Gate23Path(root, path);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            File.Copy(
                Gate23Path(realData, path),
                destination);
        }

        var copiedExtras =
            new Dictionary<AssetPath, (string Kind, string Source)>
            {
                [new AssetPath(
                "meshes/actors/character/FaceGenData/FaceGeom/Gate23Transform.esp/00000800.nif")] =
                (
                    "facegeom",
                    Path.Combine(
                        FindGate220ProjectRoot(),
                        "tests",
                        "fixtures",
                        "sse-packed-normals",
                        "dynamic-16-no-normals.nif")),
                [new AssetPath(
                "textures/actors/character/FaceGenData/FaceTint/Gate23Transform.esp/00000800.dds")] =
                (
                    "facetint",
                    Path.Combine(
                        labRoot.Value,
                        "projects",
                        "NpcManagerReimplementation",
                        "tests",
                        "fixtures",
                        "skyrim-production",
                        "Gate23FaceTint.dds"))
            };
        foreach ((AssetPath path, (string _, string source)) in
                 copiedExtras)
        {
            string destination = Gate23Path(root, path);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        var extras = new Dictionary<AssetPath, (string Kind, byte[] Bytes)>
        {
            [new AssetPath(
                "SKSE/Plugins/CharGen/Presets/Gate23Transform.bssliders")] =
                ("bodyslide-sidecar", [0x42, 0x53, 0x53, 0x23]),
            [new AssetPath(
                "meshes/actors/character/BodyGenData/Gate23Transform.esp/morphs.ini")] =
                ("bodygen", "[Gate23]\nMorph=1\n"u8.ToArray()),
            [new AssetPath(
                "scripts/NPCM_Manolov_ApplySSE.pex")] =
                ("apply-script", [0xFA, 0x57, 0xC0, 0xDE])
        };
        foreach ((AssetPath path, (string _, byte[] bytes)) in extras)
        {
            string destination = Gate23Path(root, path);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes);
        }

        var rows = new List<(string Kind, AssetPath Path)>
        {
            ("plugin", new AssetPath("Gate23Transform.esp"))
        };
        rows.AddRange(briar.Select(path => ("real-briar-asset", path)));
        rows.AddRange(copiedExtras.Select(item =>
            (item.Value.Kind, item.Key)));
        rows.AddRange(extras.Select(item =>
            (item.Value.Kind, item.Key)));
        object[] artifacts = rows
            .OrderBy(item => item.Path.Value,
                StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                string path = Gate23Path(root, item.Path);
                return (object)new
                {
                    kind = item.Kind,
                    relativePath = item.Path.Value,
                    byteLength = new FileInfo(path).Length,
                    sha256 = HashGate23ProductionFile(path).Value
                };
            })
            .ToArray();
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            edition = "skyrimse",
            presetFormat = "gate23-connected-production",
            sourcePreset = "gate23-connected.jslot",
            sourcePresetSha256 = new string('1', 64),
            sourcePlugin = "Gate23Transform.esp",
            sourcePluginSha256 =
                HashGate23ProductionFile(plugin).Value,
            outputPlugin = "Gate23Transform.esp",
            targetFormId = "0x00000800",
            artifacts
        }, Gate23JsonOptions);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "npcmanager-package.json"),
            manifest);
    }

    private static ImmutableArray<AssetPath> Gate23BriarMembers() =>
    [
        new("Meshes/armor/Briar/Briar_0.nif"),
        new("Meshes/armor/Briar/Briar_1.nif"),
        new("Meshes/armor/Briar/Briar_GND.nif"),
        new("Textures/armor/Briar/Briar.dds"),
        new("Textures/armor/Briar/Briar_m.dds"),
        new("Textures/armor/Briar/Briar_n.dds")
    ];

    private static string Gate23Path(
        string root,
        AssetPath path) =>
        Path.Combine(
            root,
            path.Value.Replace(
                '/',
                Path.DirectorySeparatorChar));

    private static Sha256Hash HashGate23ProductionFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static ImmutableArray<string> Gate23PackageHashes(
        string root) =>
        Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Select(path =>
                Path.GetRelativePath(root, path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/') +
                "|" +
                HashGate23ProductionFile(path).Value)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
}
