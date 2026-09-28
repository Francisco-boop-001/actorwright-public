using System.Diagnostics;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private static void TestCapturedEmi2FaceTextureComposition() =>
        TestCapturedEmi2FaceTextureCompositionAsync().GetAwaiter().GetResult();

    private static async Task TestCapturedEmi2FaceTextureCompositionAsync()
    {
        string projectRoot = FindProjectRoot();
        string labRootValue = Directory.GetParent(Directory.GetParent(projectRoot)!.FullName)!.FullName;
        var labRoot = new WorkspacePath(labRootValue);
        var requestFile = new WorkspacePath(Path.Combine(projectRoot, "01-source-copies",
            "gate2-emi2", "execution-request-v4.json"));
        var requestHash = HashFile(requestFile.Value);
        var loader = new RaceMenuNpcExecutionRequestFileLoader(labRoot);
        RaceMenuNpcExecutionRequestFileLoadResult loaded = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(requestFile, requestHash),
            CancellationToken.None);
        Assert(loaded.Loaded && loaded.Request is not null,
            "Real Gate 2 request load failed: " + Format(loaded.Diagnostics));

        var policy = new KOnlyWorkspacePolicy(labRoot, new WorkspacePath(@"F:\ExampleGame"));
        string futureOutput = Path.Combine(projectRoot, "03-builds", "work",
            $"face-texture-plan-{Environment.ProcessId}-{Guid.NewGuid():N}");
        RaceMenuNpcBuildRequest buildRequest = loaded.Request!.Build with
        {
            OutputRoot = new WorkspacePath(futureOutput)
        };
        var planService = new RaceMenuNpcAppearancePlanService(
            new PresetService(policy, labRoot),
            new BlankNpcProviderService(policy, labRoot),
            policy,
            labRoot);
        RaceMenuNpcAppearancePlanResult planned = await planService.AnalyzeAsync(
            buildRequest, CancellationToken.None);
        Assert(planned.Accepted && planned.Plan is { IsReady: true },
            "Real Gate 2 appearance planning failed: " + Format(planned.Diagnostics));

        string testParent = Path.Combine(projectRoot, "tests", "NpcManager.FaceGen.Tests");
        string workRootValue = Path.Combine(testParent,
            $".work-face-texture-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRootValue);
        var workRoot = new WorkspacePath(workRootValue);
        try
        {
            var authority = new WorkspacePath(Path.Combine(projectRoot, "01-source-copies",
                "gate2-emi2", "face-texture-authority.json"));
            var faceTint = new WorkspacePath(Path.Combine(workRootValue, "00000800.dds"));
            var privateDiffuse = new WorkspacePath(Path.Combine(workRootValue, "femalehead.dds"));
            var evidence = new WorkspacePath(Path.Combine(workRootValue, "evidence.json"));
            var service = new RaceMenuNpcFaceTextureBuildService(
                policy,
                labRoot,
                new InProcessDdsTextureDecoder(labRoot),
                new Bgra8FaceTintTextureDecoder(labRoot));
            var stopwatch = Stopwatch.StartNew();
            RaceMenuNpcFaceTextureBuildResult result = await service.BuildAsync(
                new RaceMenuNpcFaceTextureBuildRequest(
                    planned.Plan!,
                    labRoot,
                    new RaceMenuNpcFaceTextureBakeAuthorityReference(
                        authority, HashFile(authority.Value)),
                    2048,
                    2048,
                    new AssetPath("Textures/Actors/Character/Emi2/Skin/femalehead.dds"),
                    workRoot,
                    faceTint,
                    privateDiffuse,
                    evidence),
                CancellationToken.None);
            stopwatch.Stop();
            Assert(result.Written && result.Verified && result.Artifact is not null,
                "Real FaceTint composition failed: " + Format(result.Diagnostics));
            RaceMenuNpcFaceTextureBuildArtifact artifact = result.Artifact!;
            Console.WriteLine(
                $"EVIDENCE faceTexture elapsedMs={stopwatch.ElapsedMilliseconds} " +
                $"faceTint={artifact.ConventionalFaceTintSha256.Value} " +
                $"privateDiffuse={artifact.PrivateDiffuseSha256.Value} " +
                $"reconstruction={artifact.ReconstructionMaximumRgbByteError}/" +
                $"{artifact.ReconstructionMeanRgbByteError:F10}");
            using (JsonDocument evidenceDocument = JsonDocument.Parse(
                       File.ReadAllBytes(evidence.Value)))
            {
                JsonElement root = evidenceDocument.RootElement;
                Console.WriteLine(
                    $"EVIDENCE tintModel={root.GetProperty("compositeModelMaximumRgbByteError").GetInt32()}/" +
                    $"{root.GetProperty("compositeModelMeanRgbByteError").GetDouble():F10}");
            }
            PrintByteComparison("faceTint",
                File.ReadAllBytes(faceTint.Value),
                File.ReadAllBytes(Path.Combine(labRoot.Value, "projects", "Emi2FreshBuild",
                    "03-builds", "v0.3-static-candidate", "package", "textures", "actors",
                    "character", "FaceGenData", "FaceTint", "EmiRedDossier.esp",
                    "00000800.dds")));
            PrintByteComparison("privateDiffuse",
                File.ReadAllBytes(privateDiffuse.Value),
                File.ReadAllBytes(Path.Combine(labRoot.Value, "projects", "Emi2FreshBuild",
                    "03-builds", "v0.3-static-candidate", "package", "textures", "actors",
                    "character", "Emi2", "Skin", "femalehead.dds")));
            Assert(artifact.ConventionalFaceTintSha256 == new Sha256Hash(
                    "18C5D0996929C3AAC0AC18F18E794E643341F2BE4D9914CAA4A990BA1DD76C46"),
                "Product FaceTint hash does not reproduce the accepted 18C artifact.");
            Assert(artifact.PrivateDiffuseSha256 == new Sha256Hash(
                    "8CE2339356CE9950CC5CCF734D804669EB168AAAE776B47E3E1B126BECE3A0C1"),
                "Product private-diffuse hash does not reproduce the accepted 8CE artifact.");
            Assert(artifact.ReconstructionMaximumRgbByteError == 1 &&
                   artifact.ReconstructionMeanRgbByteError <= 0.05D,
                "Product shader reconstruction exceeded its independently bound tolerance.");
        }
        finally
        {
            string owned = Path.GetFullPath(workRootValue);
            string parent = Path.GetFullPath(testParent) + Path.DirectorySeparatorChar;
            if (owned.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(owned))
            {
                Directory.Delete(owned, recursive: true);
            }
        }
    }

    private static void TestCapturedEmi2FaceTextureAuthorityRefusals() =>
        TestCapturedEmi2FaceTextureAuthorityRefusalsAsync().GetAwaiter().GetResult();

    private static async Task TestCapturedEmi2FaceTextureAuthorityRefusalsAsync()
    {
        RealFaceTexturePlan real = await LoadRealFaceTexturePlanAsync();
        string authorityPath = Path.Combine(real.ProjectRoot, "01-source-copies",
            "gate2-emi2", "face-texture-authority.json");
        string valid = await File.ReadAllTextAsync(authorityPath);
        string testParent = Path.Combine(real.ProjectRoot, "tests", "NpcManager.FaceGen.Tests");
        string workRootValue = Path.Combine(testParent,
            $".work-face-texture-refusals-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRootValue);
        try
        {
            JsonObject reorderedRoot = ParseAuthority(valid);
            JsonArray reorderedLayers = (JsonArray)reorderedRoot["layers"]!;
            JsonNode first = reorderedLayers[0]!.DeepClone();
            JsonNode second = reorderedLayers[1]!.DeepClone();
            reorderedLayers[0] = second;
            reorderedLayers[1] = first;

            JsonObject duplicateRoot = ParseAuthority(valid);
            JsonArray duplicateLayers = (JsonArray)duplicateRoot["layers"]!;
            duplicateLayers[1] = duplicateLayers[0]!.DeepClone();

            var scenarios = new (string Name, string Json, Sha256Hash? StaleHash,
                string ExpectedDiagnostic)[]
            {
                ("manifest-tamper",
                    ReplaceOnce(valid, "gate2-emi2-product-face-texture-split-v1",
                        "gate2-emi2-product-face-texture-split-tampered"),
                    HashFile(authorityPath), "face-texture-authority-invalid"),
                ("layer-order", reorderedRoot.ToJsonString(), null,
                    "face-texture-authority-invalid"),
                ("mask-hash",
                    ReplaceOnce(valid,
                        "3737A69581C0DD9EB33D771CDA474BFEB09B7C3A851D1226C4EE0CB6FFDE96A6",
                        new string('0', 64)),
                    null, "face-texture-mask-hash"),
                ("classification",
                    ReplaceOnce(valid, "\"disposition\": \"mapped-record\"",
                        "\"disposition\": \"baked\""),
                    null, "face-texture-authority-invalid"),
                ("duplicate-row", duplicateRoot.ToJsonString(), null,
                    "face-texture-authority-invalid"),
                ("neck-range",
                    ReplaceOnce(valid, "\"endRowInclusive\": 2047",
                        "\"endRowInclusive\": 2048"),
                    null, "face-texture-authority-invalid")
            };

            foreach ((string name, string json, Sha256Hash? staleHash,
                         string expectedDiagnostic) in scenarios)
            {
                string scenarioRootValue = Path.Combine(workRootValue, name);
                Directory.CreateDirectory(scenarioRootValue);
                var scenarioRoot = new WorkspacePath(scenarioRootValue);
                var manifest = new WorkspacePath(Path.Combine(scenarioRootValue,
                    "authority.json"));
                await File.WriteAllTextAsync(manifest.Value, json);
                var faceTint = new WorkspacePath(Path.Combine(scenarioRootValue,
                    "facetint.dds"));
                var privateDiffuse = new WorkspacePath(Path.Combine(scenarioRootValue,
                    "femalehead.dds"));
                var evidence = new WorkspacePath(Path.Combine(scenarioRootValue,
                    "evidence.json"));
                var service = new RaceMenuNpcFaceTextureBuildService(
                    real.Policy,
                    real.LabRoot,
                    new InProcessDdsTextureDecoder(real.LabRoot),
                    new Bgra8FaceTintTextureDecoder(real.LabRoot));
                RaceMenuNpcFaceTextureBuildResult result = await service.BuildAsync(
                    new RaceMenuNpcFaceTextureBuildRequest(
                        real.Plan,
                        real.LabRoot,
                        new RaceMenuNpcFaceTextureBakeAuthorityReference(
                            manifest, staleHash ?? HashFile(manifest.Value)),
                        2048,
                        2048,
                        new AssetPath("Textures/Actors/Character/Emi2/Skin/femalehead.dds"),
                        scenarioRoot,
                        faceTint,
                        privateDiffuse,
                        evidence),
                    CancellationToken.None);
                Assert(!result.Written && !result.Verified && result.Artifact is null &&
                       result.Diagnostics.Any(item => item.Code == expectedDiagnostic),
                    $"{name} did not fail closed at {expectedDiagnostic}: " +
                    Format(result.Diagnostics));
                Assert(!File.Exists(faceTint.Value) && !File.Exists(privateDiffuse.Value) &&
                       !File.Exists(evidence.Value),
                    $"{name} retained an output after refusal.");
            }
        }
        finally
        {
            DeleteOwnedTestRoot(workRootValue, testParent);
        }
    }

    private static void TestSchema4MixedTintRefusal() =>
        TestSchema4MixedTintRefusalAsync().GetAwaiter().GetResult();

    private static void TestFaceTextureCleanupFailureIsObservable() =>
        TestFaceTextureCleanupFailureIsObservableAsync().GetAwaiter().GetResult();

    private static async Task TestFaceTextureCleanupFailureIsObservableAsync()
    {
        RealFaceTexturePlan real = await LoadRealFaceTexturePlanAsync();
        string testParent = Path.Combine(real.ProjectRoot, "tests", "NpcManager.FaceGen.Tests");
        string workRootValue = Path.Combine(testParent,
            $".work-face-texture-cleanup-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRootValue);
        try
        {
            OnePixelFaceTextureFixture fixture = await CreateOnePixelFaceTextureFixtureAsync(
                real, workRootValue, workRootValue);
            using var refusingVerifier = new LockingRefusingDecoder();
            var service = new RaceMenuNpcFaceTextureBuildService(real.Policy, real.LabRoot,
                new InProcessDdsTextureDecoder(real.LabRoot), refusingVerifier);
            RaceMenuNpcFaceTextureBuildResult result = await service.BuildAsync(
                fixture.Request,
                CancellationToken.None);
            Assert(!result.Written && result.Diagnostics.Any(item =>
                       item.Code == "face-texture-output-cleanup-failed") &&
                   result.Diagnostics.Any(item =>
                       item.Code == "face-texture-output-cleanup-incomplete") &&
                   File.Exists(fixture.FaceTint.Value) &&
                   File.Exists(fixture.PrivateDiffuse.Value) &&
                   !Directory.EnumerateFiles(workRootValue, "*.tmp-*",
                       SearchOption.AllDirectories).Any(),
                "A locked failed output did not produce observable cleanup diagnostics: " +
                Format(result.Diagnostics));
        }
        finally
        {
            DeleteOwnedTestRoot(workRootValue, testParent);
        }

    }

    private static void TestFaceTextureCompositionBudgets() =>
        TestFaceTextureCompositionBudgetsAsync().GetAwaiter().GetResult();

    private static void TestFaceTextureOutputAncestorIdentityIsPinned() =>
        TestFaceTextureOutputAncestorIdentityIsPinnedAsync().GetAwaiter().GetResult();

    private static async Task TestFaceTextureOutputAncestorIdentityIsPinnedAsync()
    {
        RealFaceTexturePlan real = await LoadRealFaceTexturePlanAsync();
        string testParent = Path.Combine(real.ProjectRoot, "tests", "NpcManager.FaceGen.Tests");
        string workRootValue = Path.Combine(testParent,
            $".work-face-texture-ancestor-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string sourceRootValue = Path.Combine(workRootValue, "sources");
        string stagingRootValue = Path.Combine(workRootValue, "staging");
        string movedRootValue = Path.Combine(workRootValue, "staging-substituted");
        Directory.CreateDirectory(sourceRootValue);
        Directory.CreateDirectory(stagingRootValue);
        try
        {
            OnePixelFaceTextureFixture fixture = await CreateOnePixelFaceTextureFixtureAsync(
                real, sourceRootValue, stagingRootValue);
            var verifier = new AncestorRenameAttemptingDecoder(
                new Bgra8FaceTintTextureDecoder(real.LabRoot),
                stagingRootValue,
                movedRootValue);
            var service = new RaceMenuNpcFaceTextureBuildService(real.Policy, real.LabRoot,
                new InProcessDdsTextureDecoder(real.LabRoot), verifier);
            RaceMenuNpcFaceTextureBuildResult result = await service.BuildAsync(
                fixture.Request, CancellationToken.None);

            Assert(result.Written && result.Verified && verifier.Attempted &&
                   verifier.RenameBlocked && !verifier.RenameSucceeded &&
                   Directory.Exists(stagingRootValue) && !Directory.Exists(movedRootValue),
                "The output ancestor was not continuously identity-pinned: " +
                Format(result.Diagnostics));
        }
        finally
        {
            DeleteOwnedTestRoot(workRootValue, testParent);
        }
    }

    private static async Task TestFaceTextureCompositionBudgetsAsync()
    {
        RealFaceTexturePlan real = await LoadRealFaceTexturePlanAsync();
        string authorityPath = Path.Combine(real.ProjectRoot, "01-source-copies",
            "gate2-emi2", "face-texture-authority.json");
        JsonObject authority = ParseAuthority(await File.ReadAllTextAsync(authorityPath));
        JsonArray layers = (JsonArray)authority["layers"]!;
        JsonObject providerRow = layers.Select(item => (JsonObject)item!)
            .First(row => row.ContainsKey("provider"));
        foreach (JsonObject row in layers.Select(item => (JsonObject)item!))
        {
            if (!string.Equals(row["disposition"]!.GetValue<string>(), "inactive",
                    StringComparison.Ordinal))
                continue;
            row["disposition"] = "mapped-record";
            row["presetColor"] = row["presetColor"]!.GetValue<uint>() | 0xFF00_0000U;
            row["provider"] = providerRow["provider"]!.DeepClone();
            row["sourcePath"] = providerRow["sourcePath"]!.DeepClone();
            row["sourceSha256"] = providerRow["sourceSha256"]!.DeepClone();
        }
        var activeDispositions = real.Plan.TintDispositions.Select(item =>
            item.Kind == RaceMenuNpcTintDispositionKind.Inactive
                ? new RaceMenuNpcTintDisposition(
                    item.Source with { Color = item.Source.Color | 0xFF00_0000U },
                    RaceMenuNpcTintDispositionKind.MappedRecord,
                    null,
                    item.FaceGeomSha256,
                    item.FaceTintSha256)
                : item).ToImmutableArray();
        Assert(activeDispositions.Count(item =>
                item.Kind != RaceMenuNpcTintDispositionKind.Inactive) == 33,
            "The active-layer refusal fixture no longer crosses the 32-layer budget.");

        string testParent = Path.Combine(real.ProjectRoot, "tests", "NpcManager.FaceGen.Tests");
        string workRootValue = Path.Combine(testParent,
            $".work-face-texture-budgets-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRootValue);
        try
        {
            var decoder = new DecodeMustNotRun();
            var service = new RaceMenuNpcFaceTextureBuildService(
                real.Policy, real.LabRoot, decoder, decoder);
            RaceMenuNpcFaceTextureBuildResult oversized = await service.BuildAsync(
                BuildRequest(real.Plan, new WorkspacePath(authorityPath),
                    HashFile(authorityPath), 4096, 4096, "oversized"),
                CancellationToken.None);
            Assert(!oversized.Written && oversized.Diagnostics.Any(item =>
                    item.Code == "face-texture-dimensions") && decoder.Calls == 0,
                "The oversized raster was not refused before decoder allocation: " +
                Format(oversized.Diagnostics));

            string activeAuthorityPath = Path.Combine(workRootValue,
                "active-layers-authority.json");
            await File.WriteAllTextAsync(activeAuthorityPath, authority.ToJsonString());
            RaceMenuNpcFaceTextureBuildResult excessiveLayers = await service.BuildAsync(
                BuildRequest(real.Plan with { TintDispositions = activeDispositions },
                    new WorkspacePath(activeAuthorityPath), HashFile(activeAuthorityPath),
                    2048, 2048, "active-layers"),
                CancellationToken.None);
            Assert(!excessiveLayers.Written && excessiveLayers.Diagnostics.Any(item =>
                       item.Code == "face-texture-authority-invalid" &&
                       item.Message.Contains("at most 32", StringComparison.Ordinal)) &&
                   decoder.Calls == 0,
                "The excessive active-layer set was not refused before decode: " +
                Format(excessiveLayers.Diagnostics));

            RaceMenuNpcFaceTextureBuildRequest BuildRequest(
                RaceMenuNpcAppearancePlan plan,
                WorkspacePath manifest,
                Sha256Hash manifestHash,
                int width,
                int height,
                string prefix) =>
                new(plan, real.LabRoot,
                    new RaceMenuNpcFaceTextureBakeAuthorityReference(manifest, manifestHash),
                    width, height,
                    new AssetPath("Textures/Actors/Character/Emi2/Skin/femalehead.dds"),
                    new WorkspacePath(workRootValue),
                    new WorkspacePath(Path.Combine(workRootValue, $"{prefix}-facetint.dds")),
                    new WorkspacePath(Path.Combine(workRootValue, $"{prefix}-diffuse.dds")),
                    new WorkspacePath(Path.Combine(workRootValue, $"{prefix}-evidence.json")));
        }
        finally
        {
            DeleteOwnedTestRoot(workRootValue, testParent);
        }
    }

    private static async Task TestSchema4MixedTintRefusalAsync()
    {
        RealFaceTexturePlan real = await LoadRealFaceTexturePlanAsync();
        Assert(real.Plan.TintDispositions.Any(item =>
                   item.Kind == RaceMenuNpcTintDispositionKind.MappedRecord) &&
               real.Plan.TintDispositions.Any(item =>
                   item.Kind == RaceMenuNpcTintDispositionKind.Baked),
            "The real Gate 2 plan no longer contains both mapped and baked tints.");
        string outputRootValue = Path.Combine(real.ProjectRoot, "03-builds", "work",
            $"schema4-refusal-{Environment.ProcessId}-{Guid.NewGuid():N}");
        RaceMenuNpcExecutionRequest request = real.ExecutionRequest with
        {
            Build = real.ExecutionRequest.Build with
            {
                OutputRoot = new WorkspacePath(outputRootValue)
            }
        };
        var service = new RaceMenuNpcBuildService(
            new RaceMenuNpcAppearancePlanService(
                new PresetService(real.Policy, real.LabRoot),
                new BlankNpcProviderService(real.Policy, real.LabRoot),
                real.Policy,
                real.LabRoot),
            null!,
            null!,
            null!,
            new BethesdaAssetIndexer(),
            real.Policy,
            real.LabRoot);
        RaceMenuNpcExecutionResult result = await service.ExecuteAsync(
            request, null, CancellationToken.None);
        Assert(!result.Completed &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-schema4-mixed-tints"),
            "Schema v4 did not refuse the real mixed mapped/baked plan: " +
            Format(result.Diagnostics));
        Assert(!Directory.Exists(outputRootValue),
            "Schema v4 created an output root before refusing mixed tint domains.");
    }

    private static async Task<OnePixelFaceTextureFixture> CreateOnePixelFaceTextureFixtureAsync(
        RealFaceTexturePlan real,
        string fixtureRootValue,
        string stagingRootValue)
    {
        Directory.CreateDirectory(fixtureRootValue);
        Directory.CreateDirectory(stagingRootValue);
        RaceMenuNpcTintDisposition skin = real.Plan.TintDispositions.Single(item =>
            item.Source.Index == 0 &&
            item.Kind == RaceMenuNpcTintDispositionKind.MappedRecord);
        static byte Quantized(byte value) =>
            (byte)MathF.Floor(value / 255F * 255F);
        byte red = Quantized((byte)(skin.Source.Color >> 16));
        byte green = Quantized((byte)(skin.Source.Color >> 8));
        byte blue = Quantized((byte)skin.Source.Color);
        var full = new WorkspacePath(Path.Combine(fixtureRootValue, "full.dds"));
        var baseDiffuse = new WorkspacePath(Path.Combine(fixtureRootValue, "base.dds"));
        var mask = new WorkspacePath(Path.Combine(fixtureRootValue, "mask.dds"));
        WriteBgra8Dds(full.Value, blue, green, red, byte.MaxValue);
        WriteBgra8Dds(baseDiffuse.Value, 96, 112, 128, 173);
        WriteBgra8Dds(mask.Value, 0, 0, 255, 255);
        Sha256Hash fullHash = HashFile(full.Value);
        RaceMenuNpcAppearancePlan onePixelPlan = real.Plan with
        {
            Request = real.Plan.Request with
            {
                PresetBundle = real.Plan.Request.PresetBundle with
                {
                    CharGenFaceTint = full,
                    ExpectedCharGenFaceTintSha256 = fullHash
                }
            },
            CharGenFaceTintSha256 = fullHash,
            TintDispositions = [skin]
        };
        string Relative(WorkspacePath path) =>
            Path.GetRelativePath(real.LabRoot.Value, path.Value)
                .Replace(Path.DirectorySeparatorChar, '/');
        var authority = new WorkspacePath(Path.Combine(fixtureRootValue, "authority.json"));
        await File.WriteAllTextAsync(authority.Value, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            authorityId = "one-pixel-output-transaction",
            edition = "skyrimse",
            presetSha256 = onePixelPlan.Request.PresetBundle.ExpectedPresetSha256.Value,
            privateDiffuseDestination =
                "Textures/Actors/Character/Emi2/Skin/femalehead.dds",
            fullComposite = new
            {
                sourcePath = Relative(full),
                sha256 = fullHash.Value,
                width = 1,
                height = 1
            },
            baseDiffuse = new
            {
                sourcePath = Relative(baseDiffuse),
                sha256 = HashFile(baseDiffuse.Value).Value,
                width = 1,
                height = 1
            },
            protectedNeck = new
            {
                startRowInclusive = 0,
                endRowInclusive = 0,
                policy = "copy-base-rgba-exact"
            },
            tolerances = new
            {
                tintModel = new
                {
                    maximumRgbByteError = 0,
                    maximumMeanRgbByteError = 0D
                },
                splitShader = new
                {
                    maximumRgbByteError = 0,
                    maximumMeanRgbByteError = 0D
                }
            },
            layers = new[]
            {
                new
                {
                    jslotIndex = skin.Source.Index,
                    disposition = "mapped-record",
                    presetColor = skin.Source.Color,
                    presetTexture = skin.Source.Texture,
                    provider = "test-provider",
                    sourcePath = Relative(mask),
                    sourceSha256 = HashFile(mask.Value).Value
                }
            }
        }));
        var stagingRoot = new WorkspacePath(stagingRootValue);
        var faceTint = new WorkspacePath(Path.Combine(stagingRootValue, "facetint.dds"));
        var privateDiffuse = new WorkspacePath(Path.Combine(stagingRootValue, "diffuse.dds"));
        var evidence = new WorkspacePath(Path.Combine(stagingRootValue, "evidence.json"));
        var request = new RaceMenuNpcFaceTextureBuildRequest(
            onePixelPlan,
            real.LabRoot,
            new RaceMenuNpcFaceTextureBakeAuthorityReference(
                authority, HashFile(authority.Value)),
            1,
            1,
            new AssetPath("Textures/Actors/Character/Emi2/Skin/femalehead.dds"),
            stagingRoot,
            faceTint,
            privateDiffuse,
            evidence);
        return new OnePixelFaceTextureFixture(request, faceTint, privateDiffuse, evidence);
    }

    private static async Task<RealFaceTexturePlan> LoadRealFaceTexturePlanAsync()
    {
        string projectRoot = FindProjectRoot();
        var labRoot = new WorkspacePath(
            Directory.GetParent(Directory.GetParent(projectRoot)!.FullName)!.FullName);
        var requestFile = new WorkspacePath(Path.Combine(projectRoot, "01-source-copies",
            "gate2-emi2", "execution-request-v4.json"));
        var loader = new RaceMenuNpcExecutionRequestFileLoader(labRoot);
        RaceMenuNpcExecutionRequestFileLoadResult loaded = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(
                requestFile, HashFile(requestFile.Value)), CancellationToken.None);
        Assert(loaded.Loaded && loaded.Request is not null,
            "Real Gate 2 request load failed: " + Format(loaded.Diagnostics));
        var policy = new KOnlyWorkspacePolicy(labRoot, new WorkspacePath(@"F:\ExampleGame"));
        RaceMenuNpcBuildRequest build = loaded.Request!.Build with
        {
            OutputRoot = new WorkspacePath(Path.Combine(projectRoot, "03-builds", "work",
                $"face-texture-plan-{Environment.ProcessId}-{Guid.NewGuid():N}"))
        };
        var planService = new RaceMenuNpcAppearancePlanService(
            new PresetService(policy, labRoot),
            new BlankNpcProviderService(policy, labRoot),
            policy,
            labRoot);
        RaceMenuNpcAppearancePlanResult planned = await planService.AnalyzeAsync(
            build, CancellationToken.None);
        Assert(planned.Accepted && planned.Plan is { IsReady: true },
            "Real Gate 2 appearance planning failed: " + Format(planned.Diagnostics));
        return new RealFaceTexturePlan(projectRoot, labRoot, policy, loaded.Request,
            planned.Plan!);
    }

    private static JsonObject ParseAuthority(string json) =>
        JsonNode.Parse(json) as JsonObject ??
        throw new InvalidOperationException("Authority fixture root is not an object.");

    private static string ReplaceOnce(string value, string oldValue, string newValue)
    {
        int index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert(index >= 0, $"Mutation marker was absent: {oldValue}");
        return string.Concat(value.AsSpan(0, index), newValue,
            value.AsSpan(index + oldValue.Length));
    }

    private static void DeleteOwnedTestRoot(string workRootValue, string testParent)
    {
        string owned = Path.GetFullPath(workRootValue);
        string parent = Path.GetFullPath(testParent) + Path.DirectorySeparatorChar;
        if (owned.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(owned))
        {
            Directory.Delete(owned, recursive: true);
        }
    }

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void PrintByteComparison(string label, byte[] actual, byte[] expected)
    {
        Assert(actual.Length == expected.Length, $"{label} accepted length drifted.");
        long mismatchCount = 0;
        long absoluteError = 0;
        var maximumError = 0;
        var firstMismatch = -1;
        var examples = new List<string>(8);
        for (var index = 0; index < actual.Length; index++)
        {
            var error = Math.Abs(actual[index] - expected[index]);
            if (error == 0) continue;
            if (firstMismatch < 0) firstMismatch = index;
            mismatchCount++;
            absoluteError += error;
            maximumError = Math.Max(maximumError, error);
            if (examples.Count < 8) examples.Add($"{index}:{actual[index]}!={expected[index]}");
        }
        Console.WriteLine(
            $"COMPARE {label} headerEqual={actual.AsSpan(0, 128).SequenceEqual(expected.AsSpan(0, 128))} " +
            $"firstMismatch={firstMismatch} mismatches={mismatchCount} max={maximumError} " +
            $"meanAcrossAllBytes={absoluteError / (double)actual.Length:F12} " +
            $"examples={string.Join(',', examples)}");
    }

    private static void WriteBgra8Dds(
        string path,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        var bytes = new byte[132];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0000_100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        bytes[128] = blue;
        bytes[129] = green;
        bytes[130] = red;
        bytes[131] = alpha;
        File.WriteAllBytes(path, bytes);
    }

    private sealed record RealFaceTexturePlan(
        string ProjectRoot,
        WorkspacePath LabRoot,
        KOnlyWorkspacePolicy Policy,
        RaceMenuNpcExecutionRequest ExecutionRequest,
        RaceMenuNpcAppearancePlan Plan);

    private sealed record OnePixelFaceTextureFixture(
        RaceMenuNpcFaceTextureBuildRequest Request,
        WorkspacePath FaceTint,
        WorkspacePath PrivateDiffuse,
        WorkspacePath Evidence);

    private sealed class DecodeMustNotRun : IFaceTintTextureDecoder
    {
        public int Calls { get; private set; }

        public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            WorkspacePath sourceDds,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException(
                $"Decoder unexpectedly ran for '{sourceDds.Value}'.");
        }
    }

    private sealed class LockingRefusingDecoder : IFaceTintTextureDecoder, IDisposable
    {
        private readonly List<FileStream> locks = [];

        public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            WorkspacePath sourceDds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            locks.Add(new FileStream(sourceDds.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read));
            return ValueTask.FromResult(new FaceTintTextureDecodeResult(
                false, 0, 0, null, null,
                [new Diagnostic("test-output-readback-refusal", DiagnosticSeverity.Error,
                    "The test holds the output open while refusing readback.")]));
        }

        public void Dispose()
        {
            foreach (FileStream stream in locks) stream.Dispose();
            locks.Clear();
        }
    }

    private sealed class AncestorRenameAttemptingDecoder(
        IFaceTintTextureDecoder inner,
        string stagingRoot,
        string movedRoot) : IFaceTintTextureDecoder
    {
        public bool Attempted { get; private set; }
        public bool RenameBlocked { get; private set; }
        public bool RenameSucceeded { get; private set; }

        public async ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            WorkspacePath sourceDds,
            CancellationToken cancellationToken)
        {
            if (!Attempted)
            {
                Attempted = true;
                try
                {
                    Directory.Move(stagingRoot, movedRoot);
                    RenameSucceeded = true;
                    Directory.Move(movedRoot, stagingRoot);
                }
                catch (Exception exception) when (exception is IOException or
                                                    UnauthorizedAccessException)
                {
                    RenameBlocked = true;
                }
            }
            return await inner.DecodeAsync(sourceDds, cancellationToken);
        }
    }
}
