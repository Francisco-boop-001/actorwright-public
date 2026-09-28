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
    private static readonly JsonSerializerOptions Gate23JsonOptions = new()
    {
        WriteIndented = true
    };

    private static async Task TestSkyrimSavePackage()
    {
        TestSkyrimSavePackageRules();
        await TestBethesdaArchiveStreamTransfer();
        await TestSkyrimSavePluginTransformer();
        await TestSkyrimBsaProduction();
        await TestSkyrimSavePackageService();
        await TestSkyrimSavePackageProductionWithRealBriar();
    }

    private static void TestSkyrimSavePackageRules()
    {
        var snapshot = new SkyrimSavePackageSnapshot(
            new PluginName("Gate23.esp"),
            new FormId(0x800),
            2,
            false,
            false,
            true,
            true,
            false,
            true,
            true,
            false,
            2,
            0,
            [
                new PackageManifestFile(
                    "plugin",
                    new AssetPath("Gate23.esp"),
                    100,
                    new Sha256Hash(new string('A', 64)))
            ],
            [
                new SkyrimSavePackageNpc(
                    new FormId(0x800),
                    new EditorId("Gate23First"),
                    true),
                new SkyrimSavePackageNpc(
                    new FormId(0x801),
                    new EditorId("Gate23Second"),
                    true)
            ],
            SkyrimSaveEncodingMode.Windows1252,
            ["Gate23Existing"],
            0x801,
            4);
        SkyrimSavePackageOptions exact = SkyrimSavePackageRules.CreateExactOptions(snapshot);
        SkyrimSavePackageDecision accepted = SkyrimSavePackageRules.Save(snapshot, exact);
        Assert(accepted.Accepted && accepted.Options == exact && accepted.Diagnostics.Length == 0,
            "Exact package facts were not accepted as one immutable save review.");

        SkyrimSavePackageDecision cancelled = SkyrimSavePackageRules.Cancel(snapshot);
        Assert(!cancelled.Accepted && cancelled.Options is null && cancelled.Diagnostics.Length == 0,
            "Cancel retained an accepted save-package decision.");

        AssertAccepted(snapshot, exact with { Scope = SkyrimSaveScope.AllChanged });
        AssertAccepted(snapshot, exact with
        {
            TargetMode = SkyrimSaveTargetMode.UpdateExisting
        });
        AssertAccepted(snapshot, exact with { MarkAsMaster = true });
        AssertAccepted(snapshot, exact with
        {
            EncodingMode = SkyrimSaveEncodingMode.Utf8
        });
        AssertAccepted(snapshot, exact with
        {
            ArchiveMode = SkyrimSaveArchiveMode.Bsa
        });
        AssertAccepted(snapshot, exact with
        {
            ArchiveMode = SkyrimSaveArchiveMode.Loose
        });
        AssertAccepted(snapshot, exact with
        {
            LeveledListMode = SkyrimSaveLeveledListMode.New,
            LeveledListEditorId = "Gate23Generated",
            NoDuplicateLeveledEntries = true
        });
        AssertAccepted(snapshot, exact with
        {
            LeveledListMode = SkyrimSaveLeveledListMode.Existing,
            LeveledListEditorId = "Gate23Existing",
            NoDuplicateLeveledEntries = true
        });
        AssertAccepted(snapshot, exact with { LightMaster = true });

        AssertRefused(snapshot, exact with { HasBodySlideSidecar = true },
            "save-package-output-mismatch");
        AssertRefused(snapshot, exact with
        {
            LeveledListMode = SkyrimSaveLeveledListMode.New,
            LeveledListEditorId = "contains spaces"
        }, "save-package-leveled-editor-id");
        AssertRefused(snapshot, exact with
        {
            LeveledListMode = SkyrimSaveLeveledListMode.Existing,
            LeveledListEditorId = "MissingList"
        }, "save-package-leveled-existing-missing");

        SkyrimSavePackageSnapshot nonCompact = snapshot with
        {
            HighestSelfOwnedLocalFormId = 0x1000
        };
        AssertRefused(nonCompact, exact with { LightMaster = true },
            "save-package-light-form-id-range");
        SkyrimSavePackageSnapshot targetMissing = snapshot with
        {
            Npcs =
            [
                new SkyrimSavePackageNpc(
                    new FormId(0x801),
                    new EditorId("Gate23Second"),
                    true)
            ]
        };
        AssertRefused(targetMissing, exact,
            "save-package-selected-target-missing");
    }

    private static void AssertAccepted(
        SkyrimSavePackageSnapshot snapshot,
        SkyrimSavePackageOptions candidate)
    {
        SkyrimSavePackageDecision decision =
            SkyrimSavePackageRules.Save(snapshot, candidate);
        Assert(decision.Accepted && decision.Options == candidate &&
               decision.Diagnostics.Length == 0,
            $"Save-package option set was unexpectedly refused: {string.Join(
                ", ",
                decision.Diagnostics.Select(item => item.Code))}");
    }

    private static void AssertRefused(
        SkyrimSavePackageSnapshot snapshot,
        SkyrimSavePackageOptions candidate,
        string expectedCode)
    {
        SkyrimSavePackageDecision decision = SkyrimSavePackageRules.Save(snapshot, candidate);
        Assert(!decision.Accepted && decision.Options is null &&
               decision.Diagnostics.Any(item => item.Code == expectedCode),
            $"Save-package mismatch did not fail with {expectedCode}.");
    }

    private static async Task TestSkyrimSavePackageService()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string parent = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-023-service-tests",
            Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(parent, "source");
        string outputRoot = Path.Combine(parent, "promoted");
        string staleOutput = Path.Combine(parent, "stale-promoted");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            string sourceFixture = Path.Combine(
                labRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "01-source-copies",
                "gate3-fixtures",
                "identity",
                "Data",
                "M3ArchetypeSSE.esp");
            string plugin = Path.Combine(sourceRoot, "Gate23.esp");
            File.Copy(sourceFixture, plugin);
            string faceGeom = Path.Combine(
                sourceRoot,
                "meshes",
                "actors",
                "character",
                "FaceGenData",
                "FaceGeom",
                "Gate23.esp",
                "00000800.nif");
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeom)!);
            await File.WriteAllBytesAsync(faceGeom, [1, 2, 3, 4]);
            await WriteGate23Manifest(sourceRoot, plugin, faceGeom);

            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var reader = new PackageManifestReader(policy, labRoot);
            var verifier = new PackageVerifyService(reader);
            var service = new SkyrimSavePackageService(
                new PackageInspectService(reader),
                verifier,
                new BethesdaSkyrimSavePluginTransformer(policy, labRoot),
                new BethesdaSkyrimSavePluginVerifier(policy, labRoot),
                new BethesdaSkyrimBsaService(policy, labRoot),
                new BethesdaPluginReader(),
                policy,
                labRoot);

            var request = new SkyrimSavePackageReviewRequest(
                new WorkspacePath(sourceRoot),
                new WorkspacePath(outputRoot));
            SkyrimSavePackageReviewResult review = await service.ReviewAsync(
                request,
                CancellationToken.None);
            Assert(review.Accepted && review.Artifact is not null &&
                   review.Artifact.Snapshot.OutputPlugin.Value == "Gate23.esp" &&
                   review.Artifact.Snapshot.NpcRecordCount == 1 &&
                   review.Artifact.Snapshot.HasFaceGeom &&
                   !review.Artifact.Snapshot.HasFaceTint &&
                   review.Artifact.Options == SkyrimSavePackageRules.CreateExactOptions(
                       review.Artifact.Snapshot),
                "Real package review did not bind exact Skyrim plugin and artifact facts.");

            var progress = new List<SkyrimSavePackageProgress>();
            SkyrimSavePackageExecutionResult execution = await service.ExecuteAsync(
                new SkyrimSavePackageExecutionRequest(review.Artifact!),
                new Progress<SkyrimSavePackageProgress>(progress.Add),
                CancellationToken.None);
            Assert(execution.Completed && execution.Artifact is not null &&
                   Directory.Exists(outputRoot) &&
                   execution.Artifact.SourceManifestSha256 !=
                   execution.Artifact.OutputManifestSha256 &&
                   execution.Artifact.ProposalSha256 is not null &&
                   File.Exists(Path.Combine(
                       outputRoot,
                       "npcmanager-save-proposal.json")) &&
                   execution.Artifact.PluginTransform is not null &&
                   execution.Artifact.PluginTransform.SourceSha256 ==
                   execution.Artifact.PluginTransform.OutputSha256 &&
                   execution.Artifact.Files.All(item => item.Matches) &&
                   execution.PackageVerification?.Verified == true &&
                   !execution.Artifact.RuntimeAuthority,
                $"Verified package production did not retain exact source bytes and add a bound proposal: {string.Join(
                    "; ",
                    execution.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"))}");

            var staleRequest = new SkyrimSavePackageReviewRequest(
                new WorkspacePath(sourceRoot),
                new WorkspacePath(staleOutput));
            SkyrimSavePackageReviewResult staleReview = await service.ReviewAsync(
                staleRequest,
                CancellationToken.None);
            Assert(staleReview.Accepted && staleReview.Artifact is not null,
                "Second exact package review failed before the substitution test.");
            await File.AppendAllTextAsync(faceGeom, "changed");
            SkyrimSavePackageExecutionResult stale = await service.ExecuteAsync(
                new SkyrimSavePackageExecutionRequest(staleReview.Artifact!),
                null,
                CancellationToken.None);
            Assert(!stale.Completed && !Directory.Exists(staleOutput) &&
                   stale.Diagnostics.Any(item =>
                       item.Code is "package-artifact-hash-mismatch" or
                           "save-package-review-stale"),
                "Source substitution after review was not refused without a destination.");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static async Task WriteGate23Manifest(
        string root,
        string plugin,
        string faceGeom)
    {
        var manifest = new
        {
            schemaVersion = 1,
            edition = "skyrimse",
            presetFormat = "gate23-test",
            sourcePreset = "gate23-test.jslot",
            sourcePresetSha256 = new string('1', 64),
            sourcePlugin = "gate23-source.esp",
            sourcePluginSha256 = new string('2', 64),
            outputPlugin = "Gate23.esp",
            targetFormId = "0x00000800",
            artifacts = new object[]
            {
                new
                {
                    kind = "plugin",
                    relativePath = "Gate23.esp",
                    byteLength = new FileInfo(plugin).Length,
                    sha256 = HashGate23File(plugin)
                },
                new
                {
                    kind = "facegeom",
                    relativePath = "meshes/actors/character/FaceGenData/FaceGeom/Gate23.esp/00000800.nif",
                    byteLength = new FileInfo(faceGeom).Length,
                    sha256 = HashGate23File(faceGeom)
                }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "npcmanager-package.json"),
            JsonSerializer.Serialize(manifest, Gate23JsonOptions));
    }

    private static string HashGate23File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
