using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private static void TestSkyrimNativeFaceTintExtendedRaceTable()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        string root = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests",
            "native-facetint-extended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var plugin = new PluginName("CorShapedFixture.esp");
            var provider = new SkyrimFaceRecordProvider(
                plugin,
                new WorkspacePath(Path.Combine(root, "CorShapedFixture.esp")),
                FixtureHash);
            ImmutableArray<SkyrimNativeFaceTintLayerRoute> layers =
                Enumerable.Range(0, 108)
                    .Select(order =>
                    {
                        ushort index = checked((ushort)(order is >= 76 and <= 100
                            ? order - 43
                            : order));
                        return new SkyrimNativeFaceTintLayerRoute(
                            order,
                            index,
                            Convert.ToInt32(TintAssets.TintMaskType.Paint),
                            new AssetPath($"textures/fixture/mask-{order:D3}.dds"),
                            byte.MaxValue,
                            byte.MaxValue,
                            byte.MaxValue,
                            0F,
                            SkyrimNativeFaceTintColorSource.RaceDefault,
                            null,
                            null);
                    })
                    .ToImmutableArray();
            var route = new SkyrimNativeFaceTintRecordRoute(
                new FormReference(plugin, new FormId(0x801)),
                provider,
                new FormReference(plugin, new FormId(0x800)),
                provider,
                NpcSex.Female,
                layers);
            var output = new WorkspacePath(Path.Combine(root, "00000801.dds"));
            SkyrimNativeFaceTintBuildResult result =
                new SkyrimNativeFaceTintBuildService(
                        policy, labRoot, new InProcessDdsTextureDecoder(labRoot))
                    .BuildAsync(
                        new SkyrimNativeFaceTintBuildRequest(route, [], output),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();

            Assert(result.Written && result.Artifact is not null &&
                   result.Artifact.Layers.Length == 108 &&
                   result.Artifact.Layers.All(item => !item.Applied) &&
                   File.Exists(output.Value),
                "A bounded 108-row COR-shaped default table did not survive composition and DDS readback: " +
                Format(result.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void TestSkyrimNativeFaceTintMaterialization()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        string? retainedFixtureRoot = Environment.GetEnvironmentVariable(
            "NPCM_RETAIN_NATIVE_FACEGEN_FIXTURE");
        bool retainFixture = !string.IsNullOrWhiteSpace(retainedFixtureRoot);
        string root = retainFixture
            ? Path.GetFullPath(retainedFixtureRoot!)
            : Path.Combine(
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests",
                "native-facetint-" + Guid.NewGuid().ToString("N"));
        var allowedRetainedRoot = new WorkspacePath(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work");
        if (retainFixture &&
            (!new WorkspacePath(root).IsUnder(allowedRetainedRoot) ||
             Directory.Exists(root)))
        {
            throw new InvalidOperationException(
                "Retained native FaceGen fixtures require a fresh 03-builds/work child path.");
        }
        Directory.CreateDirectory(root);
        try
        {
            string dataRoot = Path.Combine(root, "Data");
            Directory.CreateDirectory(dataRoot);
            string pluginPath = Path.Combine(dataRoot, "NativeTintFixture.esp");
            string maskPath = Path.Combine(dataRoot, "textures", "fixture", "mask.dds");
            string overrideMaskPath = Path.Combine(dataRoot, "textures", "fixture",
                "override-mask.dds");
            string faceModelPath = Path.Combine(dataRoot, "meshes", "KL",
                "High Poly Head", "FemaleHead.nif");
            string outputPath = Path.Combine(root, "00000802.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(maskPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(faceModelPath)!);
            WriteSolidBgra8Dds(maskPath, width: 2, height: 2,
                blue: 0, green: 0, red: 255, alpha: 255);
            WriteSolidBgra8Dds(overrideMaskPath, width: 2, height: 2,
                blue: 255, green: 0, red: 0, alpha: 64);
            File.Copy(
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KL\High Poly Head\FemaleHead.nif",
                faceModelPath,
                overwrite: false);
            CreateNativeTintPlugin(pluginPath);

            Sha256Hash maskHash = HashNativeTintFile(maskPath);
            var plugin = new PluginName("NativeTintFixture.esp");
            var npc = new FormReference(plugin, new FormId(0x802));
            var race = new FormReference(plugin, new FormId(0x801));
            var start = new ProcessStartInfo
            {
                FileName = @"K:\ExampleWorkspace\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(CliRunner).Assembly.Location);
            foreach (string argument in new[]
                     {
                         "facegen", "build-tint-native", "--game", "skyrimse",
                         "--data-root", dataRoot, "--plugins", plugin.Value,
                         "--npc", npc.ToString(), "--race", race.ToString(),
                         "--sex", "female", "--output", outputPath, "--json"
                     })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start) ??
                                    throw new InvalidOperationException("Native FaceTint CLI did not start.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0 && string.IsNullOrWhiteSpace(standardError) &&
                    File.Exists(outputPath),
                $"Native Skyrim FaceTint CLI failed: {standardError}");

            using JsonDocument document = JsonDocument.Parse(standardOutput);
            JsonElement response = document.RootElement;
            JsonElement artifact = response.GetProperty("artifact");
            JsonElement pluginAuthority = response.GetProperty("pluginAuthorities")[0];
            JsonElement maskAuthority = response.GetProperty("maskAuthorities")[0];
            Assert(response.GetProperty("written").GetBoolean() &&
                    pluginAuthority.GetProperty("plugin").GetProperty("value").GetString() == plugin.Value &&
                    pluginAuthority.GetProperty("path").GetProperty("value").GetString() == pluginPath &&
                    maskAuthority.GetProperty("providerKind").GetString() == "loose" &&
                    maskAuthority.GetProperty("contentSha256").GetProperty("value").GetString() == maskHash.Value,
                "The executable pipeline did not retain exact plugin and loose-mask authority evidence.");
            Assert(artifact.GetProperty("width").GetInt32() == 512 &&
                    artifact.GetProperty("height").GetInt32() == 512 &&
                    artifact.GetProperty("format").GetString() == "bc3-dxt5" &&
                    artifact.GetProperty("mipCount").GetInt32() == 10 &&
                    !artifact.GetProperty("runtimeAuthority").GetBoolean(),
                "Native Skyrim FaceTint artifact dimensions, DXT5 format, mips, or authority marker drifted.");
            JsonElement layers = artifact.GetProperty("layers");
            Assert(layers.GetArrayLength() == 2 &&
                    layers[0].GetProperty("colorSource").GetString() == "npcAuthored" &&
                    layers[1].GetProperty("colorSource").GetString() == "raceDefault" &&
                    layers.EnumerateArray().All(item =>
                        item.GetProperty("applied").GetBoolean() &&
                        item.GetProperty("maskSha256").GetProperty("value").GetString() == maskHash.Value),
                "Authored/default precedence or shared mask evidence drifted.");

            var decoder = new InProcessDdsTextureDecoder(labRoot);
            FaceTintTextureDecodeResult readback = decoder.DecodeAsync(
                new WorkspacePath(outputPath), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(readback.Decoded && readback.Bytes is not null &&
                    readback.SourceSha256?.Value == artifact.GetProperty("outputSha256")
                        .GetProperty("value").GetString(),
                "The retained DXT5 FaceTint did not reopen with its artifact hash.");
            byte[] pixels = readback.Bytes!;
            int center = ((256 * 512) + 256) * 4;
            Assert(pixels[center] is >= 120 and <= 136 &&
                    pixels[center + 1] is >= 120 and <= 136 &&
                    pixels[center + 2] <= 8 &&
                    pixels[center + 3] == 255,
                "Race-ordered authored blue then default green composition did not survive DXT5 readback.");

            string sidecarPath = Path.Combine(dataRoot,
                "NativeTintFixture.bssliders");
            File.WriteAllText(sidecarPath,
                """
                {
                  "version": 11,
                  "plugin": "NativeTintFixture.esp",
                  "npcs": {
                    "NativeTintFixture.esp|000802": {
                      "editorId": "NativeTintNpc",
                      "sseTintTextures": [
                        { "index": 2, "texture": "textures/fixture/override-mask.dds" }
                      ]
                    }
                  }
                }
                """);
            Sha256Hash overrideMaskHash = HashNativeTintFile(overrideMaskPath);

            string nativeBatchRoot = Path.Combine(root, "NativeBatchOutput");
            Directory.CreateDirectory(nativeBatchRoot);
            var batchStart = new ProcessStartInfo
            {
                FileName = @"K:\ExampleWorkspace\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            batchStart.ArgumentList.Add(typeof(CliRunner).Assembly.Location);
            foreach (string argument in new[]
                     {
                         "facegen", "bake-all-native", "--game", "skyrimse",
                         "--data-root", dataRoot, "--plugins", plugin.Value,
                         "--output-root", nativeBatchRoot, "--json"
                     })
            {
                batchStart.ArgumentList.Add(argument);
            }
            using Process batchProcess = Process.Start(batchStart) ??
                throw new InvalidOperationException("Native FaceGen batch CLI did not start.");
            string batchOutput = batchProcess.StandardOutput.ReadToEnd();
            string batchError = batchProcess.StandardError.ReadToEnd();
            batchProcess.WaitForExit();
            Assert(batchProcess.ExitCode == 0 && string.IsNullOrWhiteSpace(batchError),
                $"Native Skyrim FaceGen batch CLI failed: {batchError}; output: {batchOutput}");
            using JsonDocument batchDocument = JsonDocument.Parse(batchOutput);
            JsonElement batchResponse = batchDocument.RootElement;
            JsonElement batchArtifact = batchResponse.GetProperty("outcomes")[0]
                .GetProperty("artifact");
            string batchNif = batchArtifact.GetProperty("faceGeomNif")
                .GetProperty("value").GetString()!;
            string batchDds = batchArtifact.GetProperty("faceTintDds")
                .GetProperty("value").GetString()!;
            Assert(batchResponse.GetProperty("status").GetString() == "succeeded" &&
                   batchResponse.GetProperty("discovered").GetInt32() == 1 &&
                   batchResponse.GetProperty("baked").GetInt32() == 1 &&
                   !batchResponse.GetProperty("runtimeAuthority").GetBoolean() &&
                   File.Exists(batchNif) && File.Exists(batchDds),
                "The shipped native batch did not retain one canonical static-only NIF/DDS pair.");
            Sha256Hash batchDdsHash = HashNativeTintFile(batchDds);
            Assert(batchDdsHash != HashNativeTintFile(outputPath),
                "The shipped native batch did not apply the hydrated sidecar tint override.");
            Sha256Hash batchNifHash = HashNativeTintFile(batchNif);
            string reopenProbeParent = Path.Combine(
                root,
                "ReopenProbe",
                "meshes",
                "actors",
                "character",
                "FaceGenData",
                "FaceGeom",
                "NativeTintReopen.esp");
            Directory.CreateDirectory(reopenProbeParent);
            QualifiedFaceGeomCarrierAnalysisResult batchGeometry =
                new QualifiedFaceGeomCarrierService(policy, labRoot)
                    .AnalyzeAsync(
                        new QualifiedFaceGeomCarrierAnalyzeRequest(
                            new WorkspacePath(batchNif),
                            batchNifHash,
                            new WorkspacePath(Path.Combine(
                                reopenProbeParent,
                                "00000802.nif")),
                            new AssetPath(
                                "textures/actors/character/FaceGenData/FaceTint/NativeTintReopen.esp/00000802.dds"))
                        {
                            QualificationProfile =
                                QualifiedFaceGeomCarrierProfile
                                    .ManagerAssembledComplete
                        },
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            FaceTintTextureDecodeResult batchTint = decoder.DecodeAsync(
                    new WorkspacePath(batchDds), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(batchGeometry.Qualified &&
                   batchGeometry.Proposal?.Structure.DynamicShapeCount == 1 &&
                   batchTint.Decoded && batchTint.Width == 512 &&
                   batchTint.Height == 512,
                $"The native batch outputs did not independently reopen as a qualified FaceGeom NIF and 512x512 DDS: {string.Join(" | ", batchGeometry.Diagnostics.Select(item => $"{item.Code}:{item.Message}"))}");

            var recordResolver = new BethesdaSkyrimNativeFaceTintRecordResolver(
                policy, labRoot);
            var nativeTintPipeline = new SkyrimNativeFaceTintPipelineService(
                new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot),
                recordResolver,
                new SkyrimNativeFaceTintAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, labRoot),
                new SkyrimNativeFaceTintMaterializationService(
                    recordResolver,
                    new SkyrimAssetContentResolver(policy, labRoot),
                    new SkyrimNativeFaceTintBuildService(policy, labRoot, decoder)));

            var optionsService = new FaceGenOptionsService(policy, labRoot);
            CharGenOptions supportedOptions =
                SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
                    CharGenOptionsDefaults.For(
                        GameEdition.SkyrimSpecialEdition) with
                    {
                        DiffuseResolution = FaceGenChannelResolution.R512,
                        NormalResolution = FaceGenChannelResolution.R512,
                        SpecularResolution = FaceGenChannelResolution.R512,
                        BakeSseRaceMenuOverlays = false
                    });
            var optionsPath = new WorkspacePath(Path.Combine(root,
                "accepted-options.json"));
            FaceGenOptionsDocumentWriteResult writtenOptions = optionsService
                .WriteAsync(
                    new FaceGenOptionsDocumentWriteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        supportedOptions,
                        optionsPath),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(writtenOptions.Written &&
                   writtenOptions.OutputSha256 is not null,
                "The native FaceTint integration fixture could not persist supported CharGen options.");
            Sha256Hash supportedOptionsHash = writtenOptions.OutputSha256 ??
                throw new InvalidOperationException(
                    "The supported CharGen options write returned no hash.");
            var optionsBoundService = new SkyrimCharGenFaceTintBakeService(
                optionsService,
                nativeTintPipeline,
                policy,
                labRoot);
            var optionsBoundDds = new WorkspacePath(Path.Combine(root,
                "options-bound.dds"));
            var optionsBoundReceipt = new WorkspacePath(Path.Combine(root,
                "options-bound-receipt.json"));
            SkyrimCharGenFaceTintBakeResult optionsBound = optionsBoundService
                .BuildAsync(
                    new SkyrimCharGenFaceTintBakeRequest(
                        optionsPath,
                        supportedOptionsHash,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        npc,
                        NpcSex.Female,
                        race,
                        optionsBoundDds,
                        optionsBoundReceipt),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(optionsBound.Written && optionsBound.Receipt is not null &&
                   optionsBound.ReceiptSha256 is not null &&
                   optionsBound.Receipt.OptionsSha256 ==
                       writtenOptions.OutputSha256 &&
                   optionsBound.Receipt.DiffuseResolution ==
                       FaceGenChannelResolution.R512 &&
                   optionsBound.Receipt.DiffuseCompression ==
                       FaceGenDiffuseCompression.Bc3 &&
                   optionsBound.Receipt.NativeFaceTint.OutputSha256 ==
                       HashNativeTintFile(optionsBoundDds.Value) &&
                   File.Exists(optionsBoundReceipt.Value),
                "The native FaceTint output was not bound to and reopened from the exact accepted options hash.");

            CharGenOptions unsupportedOptions =
                SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
                    supportedOptions with
                    {
                        DiffuseCompression = FaceGenDiffuseCompression.Bc7
                    });
            var unsupportedOptionsPath = new WorkspacePath(Path.Combine(root,
                "unsupported-options.json"));
            FaceGenOptionsDocumentWriteResult unsupportedWritten = optionsService
                .WriteAsync(
                    new FaceGenOptionsDocumentWriteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        unsupportedOptions,
                        unsupportedOptionsPath),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Sha256Hash unsupportedOptionsHash = unsupportedWritten.OutputSha256 ??
                throw new InvalidOperationException(
                    "The unsupported-but-persistable options write returned no hash.");
            var refusedDds = new WorkspacePath(Path.Combine(root,
                "unsupported-options.dds"));
            var refusedReceipt = new WorkspacePath(Path.Combine(root,
                "unsupported-options-receipt.json"));
            SkyrimCharGenFaceTintBakeResult refusedOptions = optionsBoundService
                .BuildAsync(
                    new SkyrimCharGenFaceTintBakeRequest(
                        unsupportedOptionsPath,
                        unsupportedOptionsHash,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        npc,
                        NpcSex.Female,
                        race,
                        refusedDds,
                        refusedReceipt),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!refusedOptions.Written &&
                   refusedOptions.Diagnostics.Any(item => item.Code ==
                       "skyrim-chargen-native-facetint-compression") &&
                   !File.Exists(refusedDds.Value) &&
                   !File.Exists(refusedReceipt.Value),
                "Unsupported accepted CharGen options reached native FaceTint output.");

            var productionService = new SkyrimCharGenOptionsProductionService(
                optionsService,
                optionsService,
                optionsBoundService,
                policy,
                labRoot);
            var productionProposal = new WorkspacePath(Path.Combine(root,
                "production-options-proposal.json"));
            var productionRoot = new WorkspacePath(Path.Combine(root,
                "ProductionOutput"));
            SkyrimCharGenOptionsProductionReviewResult productionReview =
                productionService.ReviewAsync(
                        new SkyrimCharGenOptionsProductionReviewRequest(
                            optionsPath,
                            supportedOptionsHash,
                            supportedOptions,
                            new WorkspacePath(dataRoot),
                            [plugin],
                            npc,
                            NpcSex.Female,
                            race,
                            productionRoot,
                            productionProposal),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            Assert(productionReview.Reviewed &&
                   productionReview.ProposalSha256 is not null &&
                   File.Exists(productionProposal.Value) &&
                   !Directory.Exists(productionRoot.Value),
                "Production review did not remain proposal-only.");
            SkyrimCharGenOptionsProductionApplyResult productionApply =
                productionService.ApplyAsync(
                        new SkyrimCharGenOptionsProductionApplyRequest(
                            productionProposal,
                            productionReview.ProposalSha256 ??
                            throw new InvalidOperationException(
                                "The reviewed production proposal returned no hash.")),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            Assert(productionApply.Applied &&
                   productionApply.OptionsWrite?.ReadbackVerified == true &&
                   productionApply.FaceTintBake?.Written == true &&
                   File.Exists(Path.Combine(productionRoot.Value,
                       "accepted-options.json")) &&
                   File.Exists(Path.Combine(productionRoot.Value,
                       "facetint.dds")) &&
                   File.Exists(Path.Combine(productionRoot.Value,
                       "options-to-facetint-receipt.json")),
                "The reviewed production transaction did not persist, reopen, bake, and retain its exact outputs.");

            var staleProductionRoot = new WorkspacePath(Path.Combine(root,
                "StaleProductionOutput"));
            var staleProposal = new WorkspacePath(Path.Combine(root,
                "stale-production-proposal.json"));
            SkyrimCharGenOptionsProductionReviewResult staleReview =
                productionService.ReviewAsync(
                        new SkyrimCharGenOptionsProductionReviewRequest(
                            optionsPath,
                            supportedOptionsHash,
                            supportedOptions,
                            new WorkspacePath(dataRoot),
                            [plugin],
                            npc,
                            NpcSex.Female,
                            race,
                            staleProductionRoot,
                            staleProposal),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            SkyrimCharGenOptionsProductionApplyResult staleApply =
                productionService.ApplyAsync(
                        new SkyrimCharGenOptionsProductionApplyRequest(
                            staleProposal,
                            new Sha256Hash(new string('0', 64))),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            Assert(staleReview.Reviewed && !staleApply.Applied &&
                   !Directory.Exists(staleProductionRoot.Value),
                "A stale proposal hash reached the production output root.");

            string explicitOverrideOutput = Path.Combine(root,
                "explicit-override.dds");
            SkyrimNativeFaceTintPipelineResult explicitOverride = nativeTintPipeline
                .BuildAsync(
                    new SkyrimNativeFaceTintPipelineRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        npc,
                        NpcSex.Female,
                        race,
                        new WorkspacePath(explicitOverrideOutput),
                        [new SkyrimNativeFaceTintMaskOverride(
                            2, new AssetPath("textures/fixture/override-mask.dds"))]),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(explicitOverride.Written && explicitOverride.Artifact is not null &&
                   explicitOverride.Artifact.OutputSha256 == batchDdsHash &&
                   explicitOverride.Artifact.Layers[1].MaskPath.Value ==
                       "textures/fixture/override-mask.dds" &&
                   explicitOverride.Artifact.Layers[1].MaskSha256 == overrideMaskHash,
                "The native batch sidecar result did not equal the exact typed tint override pipeline.");
            string sourceNif =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KL\High Poly Head\FemaleHead.nif";
            var target = new FaceGenBakeTarget(
                new FormId(0x802), plugin, plugin, [plugin],
                "NativeTintNpc", "Native Tint NPC", NpcSex.Female, race, [], 50F);
            var verifier = new ExactHashFaceGeomVerifier();
            string pairRoot = Path.Combine(root, "PairOutput");
            var copyingFaceGeom =
                new CopyingFaceGeomBuildService(sourceNif);
            var pairService = new SkyrimFaceGenNpcBakeService(
                copyingFaceGeom,
                nativeTintPipeline,
                verifier,
                decoder,
                policy,
                labRoot);
            FaceGenNpcBakeResult pair = pairService.BakeAsync(
                    new FaceGenNpcBakeRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        target,
                        new WorkspacePath(pairRoot))
                    {
                        SkeletonAuthority =
                            SseFaceGeomCarrierSkeletonAuthority
                                .IdentityFaceGenBones
                    },
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(pair.Status == FaceGenNpcBakeStatus.Baked &&
                    pair.Artifact is not null && !pair.Artifact.RuntimeAuthority &&
                    File.Exists(pair.Artifact.FaceGeomNif.Value) &&
                    File.Exists(pair.Artifact.FaceTintDds.Value) &&
                    verifier.Calls == 1 &&
                    copyingFaceGeom.LastSkeletonAuthority ==
                    SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones,
                "The per-NPC transaction did not retain one independently reopened NIF/DDS pair.");
            FaceTintTextureDecodeResult pairTint = decoder.DecodeAsync(
                    pair.Artifact!.FaceTintDds, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(pairTint.Decoded &&
                   pairTint.SourceSha256 == pair.Artifact.FaceTintSha256 &&
                   HashNativeTintFile(pair.Artifact.FaceGeomNif.Value) ==
                   pair.Artifact.FaceGeomSha256,
                "The retained per-NPC pair changed after final promotion.");

            string rollbackRoot = Path.Combine(root, "RollbackOutput");
            var rollbackService = new SkyrimFaceGenNpcBakeService(
                new CopyingFaceGeomBuildService(sourceNif),
                new RefusingNativeFaceTintPipelineService(),
                new ExactHashFaceGeomVerifier(),
                decoder,
                policy,
                labRoot);
            FaceGenNpcBakeResult rolledBack = rollbackService.BakeAsync(
                    new FaceGenNpcBakeRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        target,
                        new WorkspacePath(rollbackRoot)),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            string[] retainedRollbackFiles = Directory.Exists(rollbackRoot)
                ? Directory.EnumerateFiles(rollbackRoot, "*", SearchOption.AllDirectories)
                    .ToArray()
                : [];
            Assert(rolledBack.Status == FaceGenNpcBakeStatus.Failed &&
                   retainedRollbackFiles.Length == 0,
                $"A failed FaceTint stage retained a half-pair or staging file: {string.Join(", ", retainedRollbackFiles)}; diagnostics: {string.Join(" | ", rolledBack.Diagnostics.Select(item => $"{item.Code}:{item.Message}"))}");
            if (retainFixture)
            {
                Console.WriteLine($"EVIDENCE NATIVE-FACEGEN-FIXTURE {root}");
            }
        }
        finally
        {
            if (!retainFixture)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void CreateNativeTintPlugin(string path)
    {
        ModKey modKey = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
        var redKey = new FormKey(modKey, 0x800);
        var raceKey = new FormKey(modKey, 0x801);
        var npcKey = new FormKey(modKey, 0x802);
        var greenKey = new FormKey(modKey, 0x803);
        var faceKey = new FormKey(modKey, 0x804);
        mod.Colors.Add(new ColorRecord(redKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NativeTintDefaultRed",
            Color = Color.FromArgb(255, 255, 0, 0)
        });
        mod.Colors.Add(new ColorRecord(greenKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NativeTintDefaultGreen",
            Color = Color.FromArgb(255, 0, 255, 0)
        });

        var female = new HeadData();
        female.TintMasks.Add(CreateRaceMask(index: 1, redKey,
            TintAssets.TintMaskType.SkinTone, defaultCoverage: 0.25F));
        female.TintMasks.Add(CreateRaceMask(index: 2, greenKey,
            TintAssets.TintMaskType.Paint, defaultCoverage: 0.5F));
        mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NativeTintRace",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), female)
        });
        mod.HeadParts.Add(new HeadPart(faceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NativeTintFemaleFace",
            Type = HeadPart.TypeEnum.Face,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "KL/High Poly Head/FemaleHead.nif")
            }
        });

        var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NativeTintNpc",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(raceKey)
        };
        npc.TintLayers.Add(new TintLayer
        {
            Index = 1,
            Color = Color.FromArgb(255, 0, 0, 255),
            InterpolationValue = 1F,
            Preset = 0
        });
        npc.HeadParts.Add(new FormLink<IHeadPartGetter>(faceKey));
        mod.Npcs.Add(npc);
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static TintAssets CreateRaceMask(
        ushort index,
        FormKey color,
        TintAssets.TintMaskType type,
        float defaultCoverage)
    {
        var result = new TintAssets
        {
            Index = index,
            FileName = new AssetLink<SkyrimTextureAssetType>("fixture/mask.dds"),
            MaskType = type,
            PresetDefault = new FormLinkNullable<IColorRecordGetter>(color)
        };
        result.Presets.Add(new TintPreset
        {
            Color = new FormLinkNullable<IColorRecordGetter>(color),
            DefaultValue = defaultCoverage,
            Index = 0
        });
        return result;
    }

    private static void WriteSolidBgra8Dds(
        string path,
        int width,
        int height,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        int payload = checked(width * height * 4);
        var bytes = new byte[128 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0000_100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        for (var offset = 128; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = blue;
            bytes[offset + 1] = green;
            bytes[offset + 2] = red;
            bytes[offset + 3] = alpha;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static Sha256Hash HashNativeTintFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class CopyingFaceGeomBuildService(string sourceNif) :
        ISkyrimNativeFaceGeomBuildService
    {
        public SseFaceGeomCarrierSkeletonAuthority LastSkeletonAuthority
        { get; private set; } =
            SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations;

        public async ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(
            SkyrimNativeFaceGeomBuildRequest request,
            CancellationToken cancellationToken)
        {
            LastSkeletonAuthority = request.SkeletonAuthority;
            byte[] bytes = await File.ReadAllBytesAsync(sourceNif, cancellationToken);
            await File.WriteAllBytesAsync(request.OutputNif.Value, bytes, cancellationToken);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            var immutableBytes = ImmutableArray.CreateRange(bytes);
            var assembly = new SseFaceGeomCarrierAssemblyRequest([], request.FaceTintPath);
            var predicted = new SseFaceGeomCarrierAssemblyArtifact(
                immutableBytes, hash, bytes.Length, 1, request.FaceTintPath, [],
                RuntimeAuthority: false);
            var proposal = new SseFaceGeomCarrierMaterializationProposal(
                "fixture-facegeom", assembly, request.OutputNif, predicted);
            var materialization = new SseFaceGeomCarrierMaterializationArtifact(
                proposal, request.OutputNif, hash, bytes.Length, 1, 0,
                RuntimeAuthority: false);
            var provider = new SkyrimFaceRecordProvider(
                request.Target.WinningPlugin, new WorkspacePath(sourceNif), hash);
            var route = new SkyrimFaceRecordRoute(
                new SkyrimRaceFaceRecordRoute(
                    request.Target.Race, provider, "FixtureRace", null,
                    "NordRace", [], [], [], []),
                [],
                []);
            var snapshot = new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, 18).ToImmutableArray(), 0F,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                HasNam9: false, HasNama: false);
            var artifact = new SkyrimNativeFaceGeomBuildArtifact(
                "1", "fixture-native-facegeom", request.Target, route,
                snapshot, [], [], [], materialization,
                RuntimeAuthority: false);
            return new SkyrimNativeFaceGeomBuildResult(true, true, artifact, []);
        }
    }

    private sealed class ExactHashFaceGeomVerifier :
        ISseFaceGeomCarrierMaterializationService
    {
        public int Calls { get; private set; }

        public ValueTask<SseFaceGeomCarrierMaterializationAnalysisResult> AnalyzeAsync(
            SseFaceGeomCarrierMaterializationAnalyzeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SseFaceGeomCarrierMaterializationResult> ApplyAsync(
            SseFaceGeomCarrierMaterializationProposal proposal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SseFaceGeomCarrierMaterializationVerificationResult> VerifyAsync(
            SseFaceGeomCarrierMaterializationProposal proposal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (!File.Exists(proposal.OutputNif.Value))
            {
                return ValueTask.FromResult(
                    new SseFaceGeomCarrierMaterializationVerificationResult(
                        false, proposal.OutputNif, null, null,
                        [new Diagnostic("fixture-nif-missing", DiagnosticSeverity.Error,
                            "The promoted NIF is missing.")]));
            }
            byte[] bytes = File.ReadAllBytes(proposal.OutputNif.Value);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            bool verified = hash == proposal.PredictedArtifact.Sha256 &&
                            bytes.Length == proposal.PredictedArtifact.ByteLength;
            return ValueTask.FromResult(
                new SseFaceGeomCarrierMaterializationVerificationResult(
                    verified, proposal.OutputNif, hash, bytes.Length,
                    verified
                        ? []
                        : [new Diagnostic("fixture-nif-drift", DiagnosticSeverity.Error,
                            "The promoted NIF differs from its proposal.")]));
        }
    }

    private sealed class RefusingNativeFaceTintPipelineService :
        ISkyrimNativeFaceTintPipelineService
    {
        public ValueTask<SkyrimNativeFaceTintPipelineResult> BuildAsync(
            SkyrimNativeFaceTintPipelineRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimNativeFaceTintPipelineResult(
                false, null, [], [],
                [new Diagnostic("fixture-tint-refusal", DiagnosticSeverity.Error,
                    "The fixture refused FaceTint after FaceGeom staging.")]));
        }
    }
}
