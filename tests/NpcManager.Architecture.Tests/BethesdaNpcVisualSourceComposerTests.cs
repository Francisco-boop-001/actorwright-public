using System.Collections.Immutable;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly string[] ExpectedNpcVisualFixtureTextures =
    [
        "textures/fixture/body.dds",
        "textures/fixture/dress.dds",
        "textures/fixture/head.dds"
    ];
    private static readonly string[] AuthenticSophiaPluginOrder =
    [
        "Skyrim.esm",
        "Update.esm",
        "Dawnguard.esm",
        "RaceCompatibility.esm",
        "High Poly Head.esm",
        "Improved Eyes Skyrim.esp",
        "Koralina's Eyebrows.esp",
        "KS Hairdo's.esp",
        "COR_AllRace.esp",
        "COR_TheEyesOfBeauty_Compability Patch.esp",
        "Koralina's COtR Makeup Female.esp",
        "RaceMenu.esp",
        "RaceCompatibilityUSKPOverride.esp",
        "EmiCarrierProbe.esp"
    ];
    private static readonly string[] AuthenticBrigittePluginOrder =
    [
        "Skyrim.esm",
        "Update.esm",
        "Dawnguard.esm",
        "RaceCompatibility.esm",
        "RaceMenu.esp",
        "High Poly Head.esm",
        "Improved Eyes Skyrim.esp",
        "Koralina's Eyebrows.esp",
        "KS Hairdo's.esp",
        "COR_AllRace.esp",
        "COR_TheEyesOfBeauty_Compability Patch.esp",
        "Koralina's COtR Makeup Female.esp",
        "Even More Brows COtR.esp"
    ];
    private static readonly string[] AuthenticBrigitteHairShapes =
    [
        "0Sky160",
        "0Sky160HL",
        "0_HAIRLINE_Female_Human_Straight"
    ];

    private static Task TestNpcVisualSkinTextureSetRouting()
    {
        NpcVisualTextureSlot bodyDiffuse = PreviewTextureSlot(
            0, "textures/fixture/body.dds");
        NpcVisualTextureSlot bodyNormal = PreviewTextureSlot(
            1, "textures/fixture/body_n.dds");
        NpcVisualTextureSlot clothDiffuse = PreviewTextureSlot(
            0, "textures/fixture/dress.dds");
        NpcVisualTextureSlot replacementDiffuse = PreviewTextureSlot(
            0, "textures/fixture/cotr-body.dds");
        NpcVisualTextureSlot replacementNormal = PreviewTextureSlot(
            1, "textures/fixture/cotr-body_n.dds");
        ImmutableArray<NpcVisualMaterial> routed =
            SkyrimNpcVisualMaterialRouting.ApplySkinTextureSet(
            [
                new NpcVisualMaterial(
                    "CBBE",
                    5,
                    [bodyDiffuse, bodyNormal],
                    0,
                    0,
                    false,
                    1,
                    null),
                new NpcVisualMaterial(
                    "traveler",
                    0,
                    [clothDiffuse],
                    0,
                    0,
                    false,
                    1,
                    null)
            ],
            [replacementDiffuse, replacementNormal]);

        NpcVisualMaterial skin = routed.Single(item =>
            item.Shape == "CBBE");
        NpcVisualMaterial cloth = routed.Single(item =>
            item.Shape == "traveler");
        Assert(
            skin.TextureSlots.Single(item => item.Slot == 0)
                .AssetPath.Value ==
            "textures/fixture/cotr-body.dds" &&
            skin.TextureSlots.Single(item => item.Slot == 1)
                .AssetPath.Value ==
            "textures/fixture/cotr-body_n.dds",
            "The SkinTint shape did not receive the armor-addon TXST.");
        Assert(
            cloth.TextureSlots is
            [
                {
                    Slot: 0,
                    AssetPath.Value: "textures/fixture/dress.dds"
                }
            ],
            "The armor-addon TXST replaced a non-skin outfit material.");
        return Task.CompletedTask;
    }

    private static NpcVisualTextureSlot PreviewTextureSlot(
        int slot,
        string path) =>
        new(
            slot,
            slot == 0 ? "Diffuse" : "Normal",
            new AssetPath(path),
            "fixture",
            new Sha256Hash(new string(
                slot == 0 ? 'A' : 'B', 64)),
            new WorkspacePath(Path.Combine(
                "K:\\ExampleWorkspace",
                path.Replace(
                    '/', Path.DirectorySeparatorChar))));

    private static async Task TestBethesdaNpcVisualSourceComposer()
    {
        WorkspacePath labRoot = new(Environment.CurrentDirectory);
        string root = Path.Combine(
            labRoot.Value,
            "artifacts",
            $"npc-visual-source-test-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "Data");
        string output = Path.Combine(root, "preview");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(output);
        try
        {
            await TestOverriddenNpcFaceGenOriginRouting(
                labRoot,
                root);
            string pluginPath = Path.Combine(data, "VisualFixture.esp");
            WriteNpcVisualFixture(pluginPath);
            WriteNpcVisualFixtureAssets(data);
            string bodyGenPath = Path.Combine(
                data,
                "meshes",
                "actors",
                "character",
                "BodyGenData",
                "VisualFixture.esp",
                "morphs.ini");
            Directory.CreateDirectory(
                Path.GetDirectoryName(bodyGenPath)!);
            File.WriteAllText(
                bodyGenPath,
                "VisualFixture.esp|00000800=FixtureBody\n");
            string bodyGenTemplatesPath = Path.Combine(
                Path.GetDirectoryName(bodyGenPath)!,
                "templates.ini");
            File.WriteAllText(
                bodyGenTemplatesPath,
                "FixtureBody=Waist@0.2,Breasts@-0.3\n");
            string bsaSource = Path.Combine(
                root, "bsa-source");
            string bsaDress = Path.Combine(
                bsaSource,
                "textures",
                "fixture",
                "dress.dds");
            Directory.CreateDirectory(
                Path.GetDirectoryName(bsaDress)!);
            string looseDress = Path.Combine(
                data,
                "textures",
                "fixture",
                "dress.dds");
            File.Copy(looseDress, bsaDress);
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var bsaService = new BethesdaSkyrimBsaService(
                policy,
                labRoot);
            SkyrimBsaBuildResult bsa =
                await bsaService.BuildAsync(
                    new SkyrimBsaBuildRequest(
                        new WorkspacePath(bsaSource),
                        new WorkspacePath(Path.Combine(
                            data,
                            "VisualFixtureAssets.bsa")),
                        [
                            new AssetPath(
                                "textures/fixture/dress.dds")
                        ]),
                    CancellationToken.None);
            Assert(
                bsa.Written &&
                bsa.Artifact is
                {
                    Members.Length: 1
                },
                "The physical BSA preview fixture did not build.");
            File.Delete(looseDress);
            Sha256Hash pluginHash =
                HashNpcPreviewTestFile(pluginPath);
            var plugin = new PluginName("VisualFixture.esp");
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(data),
                new WorkspacePath(Path.Combine(root, "loadorder.txt")),
                new WorkspacePath(Path.Combine(root, "unused")),
                new Sha256Hash(new string('1', 64)),
                [
                    new PluginClosureReviewEntry(
                        plugin,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(pluginPath),
                        pluginHash,
                        [])
                ],
                [], [], [], 7,
                new Sha256Hash(new string('2', 64)),
                new Sha256Hash(new string('3', 64)),
                false);
            var identity = new SkyrimMainWorkspaceIdentity(
                plugin, plugin, new FormId(0x800), "NPC_");
            var request = new NpcVisualPreviewComposeRequest(
                intake,
                identity,
                null,
                new NpcVisualPreviewOptions(),
                new WorkspacePath(output));
            var composer = new BethesdaNpcVisualSourceComposer(
                policy,
                labRoot);
            NpcVisualSourceComposeResult sourceResult =
                await composer.ComposeSourceAsync(
                    request, CancellationToken.None);
            NpcVisualSourceGraph? source = sourceResult.Source;

            Assert(sourceResult.Composed && source is not null,
                "The real Bethesda graph did not compose: " +
                string.Join("; ", sourceResult.Diagnostics.Select(
                    item => $"{item.Code}: {item.Message}")));
            Assert(source!.Route == NpcVisualPreviewRoute.Cbbe3Ba &&
                   source.Sex == NpcSex.Female &&
                   source.Weight == 73 &&
                   source.HairColorHex == "#D6BE83" &&
                   source.SkinTintHex == "#E7B79D",
                "NPC race/sex/weight/tint authority was not retained.");
            Assert(source.Assets.Count(item =>
                       item.Role == NpcVisualAssetRole.FaceGeom) == 1 &&
                   source.Assets.Count(item =>
                       item.Role == NpcVisualAssetRole.FaceTint) == 1 &&
                   source.Assets.All(item =>
                       item.Role != NpcVisualAssetRole.Body) &&
                   source.Assets.Any(item =>
                       item.Role == NpcVisualAssetRole.Outfit &&
                       item.BipedSlotMask ==
                       (uint)BipedObjectFlag.Body) &&
                   source.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-preview-biped-skin-suppressed"),
                "The composed actor did not retain FaceGeom/FaceTint/outfit ownership while suppressing the naked slot-32 body hidden by the declared outfit.");
            NpcVisualAsset outfitAsset = source.Assets.Single(item =>
                item.Role == NpcVisualAssetRole.Outfit);
            Assert(
                outfitAsset.LowWeightAssetPath?.Value ==
                "meshes/fixture/dress_0.nif" &&
                outfitAsset.LowWeightMaterializedPath is not null &&
                File.Exists(
                    outfitAsset.LowWeightMaterializedPath.Value.Value) &&
                outfitAsset.LowWeightSha256 is not null,
                "The weight-73 outfit did not retain its exact _0.nif companion for Skyrim weight interpolation.");
            Assert(source.Assets.Where(item =>
                       item.Role == NpcVisualAssetRole.Texture)
                   .Select(item => item.AssetPath.Value)
                   .Order(StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(
                       ExpectedNpcVisualFixtureTextures,
                       StringComparer.OrdinalIgnoreCase),
                "Embedded material textures were not resolved exactly.");
            NpcVisualAsset bsaTexture = source.Assets.Single(item =>
                item.Role == NpcVisualAssetRole.Texture &&
                string.Equals(
                    item.AssetPath.Value,
                    "textures/fixture/dress.dds",
                    StringComparison.OrdinalIgnoreCase));
            Assert(
                bsaTexture.Provider ==
                "bsa:VisualFixtureAssets.bsa",
                "A texture available only in the physical BSA did not retain its archive provider.");
            Assert(
                source.Morphs.Length == 2 &&
                source.Morphs.Any(item =>
                    item.Source == "BodyGen:FixtureBody" &&
                    item.Name == "Waist" &&
                    Math.Abs(item.Value - 0.2f) < 0.0001 &&
                    item.Provider is not null &&
                    item.AssetPath?.Value ==
                    "meshes/actors/character/BodyGenData/VisualFixture.esp/templates.ini" &&
                    item.Sha256 is not null) &&
                source.Morphs.Any(item =>
                    item.Source == "BodyGen:FixtureBody" &&
                    item.Name == "Breasts" &&
                    Math.Abs(item.Value - -0.3f) < 0.0001) &&
                source.Diagnostics.Any(item =>
                    item.Code ==
                    "npc-preview-bodygen-runtime-morph-unapplied"),
                "BodyGen template sliders, provider/hash provenance, or the explicit unapplied-runtime warning was not retained.");
            Assert(source.Assets.All(item =>
                       item.MaterializedPath.IsUnder(
                           new WorkspacePath(output)) &&
                       File.Exists(item.MaterializedPath.Value) &&
                       HashNpcPreviewTestFile(
                           item.MaterializedPath.Value) == item.Sha256),
                "Materialized assets escaped the output or failed hash readback.");
            NpcVisualMaterial headMaterial = source.Assets
                .Single(item =>
                    item.Role == NpcVisualAssetRole.FaceGeom)
                .Materials.Single();
            Assert(headMaterial.TextureSlots.Single(item =>
                       item.Slot == 0).AssetPath.Value ==
                   "textures/fixture/head.dds" &&
                   headMaterial.TextureSlots.Single(item =>
                       item.Slot == 6).AssetPath.Value.EndsWith(
                       "/FaceTint/VisualFixture.esp/00000800.dds",
                       StringComparison.OrdinalIgnoreCase),
                "Face diffuse slot 0 or canonical actor FaceTint slot 6 drifted.");
            Assert(source.Assets.All(item => !item.BakedIntoFaceGeom) &&
                   source.Assets.All(item =>
                       item.Role is not NpcVisualAssetRole.Hair and
                           not NpcVisualAssetRole.Eyes),
                "Baked FaceGeom headparts were reintroduced as duplicate meshes.");

            WriteNpcVisualFile(
                data,
                "textures/fixture/dress.dds",
                [0x44, 0x44, 0x53, 0x20, 0x99]);
            string loosePrecedenceOutput = Path.Combine(
                root, "loose-precedence-preview");
            Directory.CreateDirectory(loosePrecedenceOutput);
            NpcVisualSourceComposeResult loosePrecedence =
                await composer.ComposeSourceAsync(
                    request with
                    {
                        OutputRoot =
                            new WorkspacePath(
                                loosePrecedenceOutput)
                    },
                    CancellationToken.None);
            Assert(
                loosePrecedence.Composed &&
                loosePrecedence.Source is not null &&
                loosePrecedence.Source.Assets.Single(item =>
                    item.Role == NpcVisualAssetRole.Texture &&
                    string.Equals(
                        item.AssetPath.Value,
                        "textures/fixture/dress.dds",
                        StringComparison.OrdinalIgnoreCase))
                    .Provider.StartsWith(
                        "loose:",
                        StringComparison.Ordinal),
                "Loose-over-BSA provider precedence was not preserved.");

            File.Delete(looseDress);
            string reparseTarget = Path.Combine(
                root, "reparse-target-dress.dds");
            File.WriteAllBytes(
                reparseTarget,
                [0x44, 0x44, 0x53, 0x20, 0x77]);
            if (PhysicalReparseFixture.TryCreateFileLink(
                    looseDress, reparseTarget, root))
            {
                try
                {
                    string reparseOutput = Path.Combine(
                        root, "reparse-preview");
                    Directory.CreateDirectory(reparseOutput);
                    NpcVisualSourceComposeResult reparse =
                        await composer.ComposeSourceAsync(
                            request with
                            {
                                OutputRoot =
                                    new WorkspacePath(
                                        reparseOutput)
                            },
                            CancellationToken.None);
                    Assert(
                        !reparse.Composed &&
                        reparse.Diagnostics.Any(item =>
                            item.Code ==
                            "npc-preview-asset-reparse-refused"),
                        "A nested reparse-point asset was accepted or silently fell back to the BSA.");
                }
                finally
                {
                    if (File.Exists(looseDress))
                        File.Delete(looseDress);
                }
            }
            else
            {
                Assert(
                    File.ReadAllText(Path.Combine(
                            labRoot.Value,
                            "src",
                            "NpcManager.Formats.Bethesda",
                            "BethesdaNpcVisualSourceComposer.Assets.cs"))
                        .Contains(
                            "npc-preview-asset-reparse-refused",
                            StringComparison.Ordinal),
                    "Reparse creation was unavailable and the resolver has no retained fail-closed branch.");
            }

            string pairedRoot = Path.Combine(
                root, "paired-overlay");
            string pairedData = Path.Combine(
                pairedRoot, "Data");
            string pairedPlugin = Path.Combine(
                pairedData, "VisualFixture.esp");
            string pairedTint = Path.Combine(
                pairedData, "textures", "actors", "character",
                "FaceGenData", "FaceTint", "VisualFixture.esp",
                "00000800.dds");
            string undeclaredGeom = Path.Combine(
                pairedData, "meshes", "actors", "character",
                "FaceGenData", "FaceGeom", "VisualFixture.esp",
                "00000800.nif");
            Directory.CreateDirectory(
                Path.GetDirectoryName(pairedPlugin)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(pairedTint)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(undeclaredGeom)!);
            File.Copy(pluginPath, pairedPlugin);
            File.WriteAllBytes(
                pairedTint, [91, 92, 93, 94, 95]);
            File.WriteAllBytes(
                undeclaredGeom, [1, 1, 2, 3, 5, 8]);
            string pairedManifest = Path.Combine(
                pairedRoot, "npcmanager-paired-package.json");
            File.WriteAllText(
                pairedManifest,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    artifactKind =
                        "skyrim-paired-follower-finish-package",
                    artifacts = new object[]
                    {
                        new
                        {
                            relativePath =
                                "Data/VisualFixture.esp",
                            byteLength =
                                new FileInfo(pairedPlugin).Length,
                            sha256 =
                                HashNpcPreviewTestFile(
                                    pairedPlugin).Value
                        },
                        new
                        {
                            relativePath =
                                "Data/textures/actors/character/" +
                                "FaceGenData/FaceTint/" +
                                "VisualFixture.esp/00000800.dds",
                            byteLength =
                                new FileInfo(pairedTint).Length,
                            sha256 =
                                HashNpcPreviewTestFile(
                                    pairedTint).Value
                        }
                    }
                }));
            string pairedOutput = Path.Combine(
                root, "paired-preview");
            Directory.CreateDirectory(pairedOutput);
            NpcVisualSourceComposeResult paired =
                await composer.ComposeSourceAsync(
                    request with
                    {
                        PackageOverlay =
                            new NpcVisualPreviewPackageOverlay(
                                new WorkspacePath(pairedManifest),
                                HashNpcPreviewTestFile(
                                    pairedManifest)),
                        OutputRoot =
                            new WorkspacePath(pairedOutput)
                    },
                    CancellationToken.None);
            Assert(paired.Composed &&
                   paired.Source is not null &&
                   paired.Source.Assets.Single(item =>
                       item.Role ==
                       NpcVisualAssetRole.FaceTint).Sha256 ==
                   HashNpcPreviewTestFile(pairedTint) &&
                   paired.Source.Assets.Single(item =>
                       item.Role ==
                       NpcVisualAssetRole.FaceGeom).Sha256 ==
                   source.Assets.Single(item =>
                       item.Role ==
                       NpcVisualAssetRole.FaceGeom).Sha256,
                "The paired package overlay did not win for its declared FaceTint or allowed an undeclared corrupt FaceGeom to win.");

            File.AppendAllText(pairedTint, "tamper");
            string stalePackageOutput = Path.Combine(
                root, "stale-package-preview");
            Directory.CreateDirectory(stalePackageOutput);
            NpcVisualSourceComposeResult stalePackage =
                await composer.ComposeSourceAsync(
                    request with
                    {
                        PackageOverlay =
                            new NpcVisualPreviewPackageOverlay(
                                new WorkspacePath(pairedManifest),
                                HashNpcPreviewTestFile(
                                    pairedManifest)),
                        OutputRoot =
                            new WorkspacePath(
                                stalePackageOutput)
                    },
                    CancellationToken.None);
            Assert(!stalePackage.Composed &&
                   stalePackage.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-preview-package-artifact-stale"),
                "A paired package artifact changed after admission without refusal.");

            File.AppendAllText(pluginPath, "tamper");
            string staleOutput = Path.Combine(
                root, "stale-preview");
            Directory.CreateDirectory(staleOutput);
            NpcVisualSourceComposeResult stale =
                await composer.ComposeSourceAsync(
                    request with
                    {
                        OutputRoot = new WorkspacePath(
                            staleOutput)
                    },
                    CancellationToken.None);
            Assert(!stale.Composed &&
                   stale.Source is null &&
                   stale.Diagnostics.Any(item =>
                       item.Code == "npc-preview-plugin-stale"),
                "A plugin that changed after intake was accepted.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestOverriddenNpcFaceGenOriginRouting(
        WorkspacePath labRoot,
        string parent)
    {
        const string ownerName = "Owner.esp";
        const string winnerName = "Patch.esp";
        foreach ((string assetOwner, bool expectedComposed) in
                 new[]
                 {
                     (ownerName, true),
                     (winnerName, false)
                 })
        {
            string root = Path.Combine(
                parent,
                $"overridden-{(expectedComposed ? "owner" : "winner")}-assets");
            string data = Path.Combine(root, "Data");
            string output = Path.Combine(root, "preview");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(output);
            string ownerPath = Path.Combine(data, ownerName);
            string winnerPath = Path.Combine(data, winnerName);
            WriteNpcVisualFixture(ownerPath);
            WriteNpcVisualOverrideFixture(
                winnerPath,
                ownerName);
            WriteNpcVisualFaceGenAssets(
                data,
                assetOwner);
            var owner = new PluginName(ownerName);
            var winner = new PluginName(winnerName);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(data),
                new WorkspacePath(Path.Combine(root, "loadorder.txt")),
                new WorkspacePath(Path.Combine(root, "unused")),
                new Sha256Hash(new string('1', 64)),
                [
                    new PluginClosureReviewEntry(
                        owner,
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(ownerPath),
                        HashNpcPreviewTestFile(ownerPath),
                        []),
                    new PluginClosureReviewEntry(
                        winner,
                        1,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(winnerPath),
                        HashNpcPreviewTestFile(winnerPath),
                        [])
                ],
                [], [], [], 3,
                new Sha256Hash(new string('2', 64)),
                new Sha256Hash(new string('3', 64)),
                false);
            var identity = new SkyrimMainWorkspaceIdentity(
                owner,
                winner,
                new FormId(0x800),
                "NPC_");
            NpcVisualSourceComposeResult result =
                await new BethesdaNpcVisualSourceComposer(
                        new KOnlyWorkspacePolicy(
                            labRoot,
                            new WorkspacePath("F:\\ExampleGame")),
                        labRoot)
                    .ComposeSourceAsync(
                        new NpcVisualPreviewComposeRequest(
                            intake,
                            identity,
                            null,
                            new NpcVisualPreviewOptions(
                                RenderBody: false,
                                RenderOutfit: false),
                            new WorkspacePath(output)),
                        CancellationToken.None);

            Assert(
                result.Composed == expectedComposed &&
                (expectedComposed
                    ? result.Source is not null &&
                      result.Source.Identity.WinningProvider == winner &&
                      result.Source.Assets.Any(item =>
                          item.Role == NpcVisualAssetRole.FaceGeom &&
                          item.AssetPath.Value.Contains(
                              "/Owner.esp/00000800.nif",
                              StringComparison.OrdinalIgnoreCase)) &&
                      result.Source.Assets.Any(item =>
                          item.Role == NpcVisualAssetRole.FaceTint &&
                          item.AssetPath.Value.Contains(
                              "/Owner.esp/00000800.dds",
                              StringComparison.OrdinalIgnoreCase))
                    : result.Source is null),
                expectedComposed
                    ? "An overridden NPC did not resolve owner-directory FaceGen while retaining Patch.esp winner provenance: " +
                      string.Join("; ", result.Diagnostics.Select(item =>
                          $"{item.Code}: {item.Message}"))
                    : "An overridden NPC incorrectly fell back to FaceGen stored only under Patch.esp.");
        }
    }

    private static async Task TestAuthenticSophiaNpcVisualSourceComposer()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string projectRoot = Path.Combine(
            labRoot.Value, "projects", "NpcManagerReimplementation");
        string data = Path.Combine(
            projectRoot, "01-source-copies",
            "sophia-live-closure-20260723", "Data");
        string manifest = Path.Combine(
            projectRoot, "03-builds", "work",
            "sophia-jslot-manager-only-20260724-8",
            "npcmanager-package.json");
        string packageRoot =
            Path.GetDirectoryName(manifest)!;
        Sha256Hash packageTreeBefore =
            HashNpcPreviewTestTree(packageRoot);
        string output = Path.Combine(
            projectRoot, "03-builds", "work",
            $"npc-visual-sophia-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);

        Assert(Directory.Exists(data) && File.Exists(manifest) &&
               AuthenticSophiaPluginOrder.All(name =>
                   File.Exists(Path.Combine(data, name))),
            "The retained Sophia v0.4.1 closure is incomplete.");

        ImmutableArray<PluginClosureReviewEntry> plugins =
            AuthenticSophiaPluginOrder.Select((name, index) =>
            {
                string path = Path.Combine(data, name);
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
            }).ToImmutableArray();
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(Path.Combine(
                projectRoot, "03-builds", "work",
                "sophia-visual-loadorder.txt")),
            new WorkspacePath(Path.Combine(
                projectRoot, "03-builds", "work",
                "sophia-visual-unused")),
            new Sha256Hash(new string('1', 64)),
            plugins,
            [], [], [], 7,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
        var outputPlugin = new PluginName(
            "SophiaLorenNpcManager.esp");
        var request = new NpcVisualPreviewComposeRequest(
            intake,
            new SkyrimMainWorkspaceIdentity(
                outputPlugin,
                outputPlugin,
                new FormId(0x800),
                "NPC_"),
            new NpcVisualPreviewPackageOverlay(
                new WorkspacePath(manifest),
                HashNpcPreviewTestFile(manifest)),
            new NpcVisualPreviewOptions(),
            new WorkspacePath(output));
        var composer = new BethesdaNpcVisualSourceComposer(
            new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath("F:\\ExampleGame")),
            labRoot);
        NpcVisualSourceComposeResult result =
            await composer.ComposeSourceAsync(
                request, CancellationToken.None);
        NpcVisualSourceGraph? source = result.Source;
        Assert(result.Composed && source is not null,
            "Authentic Sophia v0.4.1 did not compose: " +
            string.Join("; ", result.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}")));
        Assert(source!.Route == NpcVisualPreviewRoute.Cotr &&
               source.Assets.Count(item =>
                   item.Role == NpcVisualAssetRole.FaceGeom) == 1 &&
               source.Assets.Count(item =>
                   item.Role == NpcVisualAssetRole.FaceTint) == 1 &&
               source.Assets.All(item =>
                   item.Role is not NpcVisualAssetRole.Hair and
                       not NpcVisualAssetRole.Eyes),
            "Sophia's COtR route or authoritative FaceGeom ownership drifted.");
        Assert(source.Assets.Single(item =>
                   item.Role == NpcVisualAssetRole.FaceGeom).Sha256 ==
               new Sha256Hash(
                   "7590DF02EC57ECFB70D5F3803D430FBC3C1A5B464DEA29560115C5CE0D7BCA58") &&
               source.Assets.Single(item =>
                   item.Role == NpcVisualAssetRole.FaceTint).Sha256 ==
               new Sha256Hash(
                   "4A028548542F1F16EFF8F957D023AAEDC769D8E363D94BFA9A835CACD06F7DE9"),
            "Sophia's accepted FaceGeom or FaceTint bytes drifted.");
        NpcVisualMaterial faceMaterial = source.Assets.Single(item =>
                item.Role == NpcVisualAssetRole.FaceGeom)
            .Materials.Single(material =>
                material.TextureSlots.Any(slot =>
                    slot.Slot == 6));
        Assert(faceMaterial.TextureSlots.Any(slot =>
                   slot.Slot == 0) &&
               faceMaterial.TextureSlots.Single(slot =>
                   slot.Slot == 6).AssetPath.Value ==
               "textures/actors/character/FaceGenData/FaceTint/" +
               "SophiaLorenNpcManager.esp/00000800.dds",
            "Sophia's accepted COtR slot-0/slot-6 material rule drifted.");
        NpcVisualAsset hands = source.Assets.Single(item =>
            item.Role == NpcVisualAssetRole.Hands);
        NpcVisualMaterial handsMaterial =
            hands.Materials.Single();
        Assert(
            handsMaterial.Shape ==
            "CalienteHandFemale1st.00" &&
            handsMaterial.TextureSlots.Single(slot =>
                slot.Slot == 0).AssetPath.Value ==
            "textures/!COR/Hands/femalehands_1.dds" &&
            handsMaterial.TextureSlots.Single(slot =>
                slot.Slot == 1).AssetPath.Value ==
            "textures/!COR/Hands/femalehands_1_msn.dds" &&
            handsMaterial.TextureSlots.Single(slot =>
                slot.Slot == 7).AssetPath.Value ==
            "textures/!COR/Hands/femalehands_1_s.dds" &&
            handsMaterial.TextureSlots.All(slot =>
                slot.Slot != 4),
            "Sophia's inherited COtR hand TXST did not preserve the NIF shape and override physical Skyrim slots 0/1/7 exactly.");
        ImmutableArray<NpcVisualAsset> bodyMorphTris =
            source.Assets
                .Where(item =>
                    item.Role == NpcVisualAssetRole.Tri)
                .OrderBy(item =>
                    item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        Assert(
            bodyMorphTris is
            [
                {
                    AssetPath.Value:
                        "meshes/!COR/Body/femalebody.tri",
                    Sha256.Value:
                        "E77655F8142F5915FCB9FE79573FD10A1083D3A13441156611F638C8DD8C186E"
                },
                {
                    AssetPath.Value:
                        "meshes/!COR/Feet/femalefeet.tri",
                    Sha256.Value:
                        "CE55D8FC666918431D09E8B42C396126C04E27E5E96E40E82ABB520A6B8FCF53"
                }
            ] &&
            source.Morphs.Length == 224 &&
            source.Diagnostics.All(item =>
                item.Code !=
                "npc-preview-bodygen-runtime-morph-unapplied"),
            "Sophia's authentic BodyGen assignments did not retain the exact available COtR PIRT geometry or were still reported as wholly unapplied.");
        Assert(
            HashNpcPreviewTestTree(packageRoot) ==
            packageTreeBefore,
            "Sophia's accepted Manager package changed during preview composition.");
    }

    private static void InspectAuthenticSophiaSkinGraph()
    {
        const uint npcId = 0x00000800;
        string projectRoot = Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation");
        string data = Path.Combine(
            projectRoot,
            "01-source-copies",
            "sophia-live-closure-20260723",
            "Data");
        string packagePlugin = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            "sophia-jslot-manager-only-20260724-8",
            "Data",
            "SophiaLorenNpcManager.esp");
        string[] paths =
        [
            .. AuthenticSophiaPluginOrder.Select(name =>
                Path.Combine(data, name)),
            packagePlugin
        ];
        var npcs = new Dictionary<FormKey, Npc>();
        var races = new Dictionary<FormKey, Race>();
        var armors = new Dictionary<FormKey, Armor>();
        var addons = new Dictionary<FormKey, ArmorAddon>();
        var textures = new Dictionary<FormKey, TextureSet>();
        foreach (string path in paths)
        {
            ModKey key = ModKey.FromNameAndExtension(
                Path.GetFileName(path));
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(key, new FilePath(path)),
                SkyrimRelease.SkyrimSE);
            foreach (INpcGetter item in mod.Npcs)
                npcs[item.FormKey] = item.DeepCopy();
            foreach (IRaceGetter item in mod.Races)
                races[item.FormKey] = item.DeepCopy();
            foreach (IArmorGetter item in mod.Armors)
                armors[item.FormKey] = item.DeepCopy();
            foreach (IArmorAddonGetter item in mod.ArmorAddons)
                addons[item.FormKey] = item.DeepCopy();
            foreach (ITextureSetGetter item in mod.TextureSets)
                textures[item.FormKey] = item.DeepCopy();
        }

        FormKey npcKey = new(
            ModKey.FromNameAndExtension(
                "SophiaLorenNpcManager.esp"),
            npcId);
        Npc npc = npcs[npcKey];
        Race race = races[npc.Race.FormKey];
        FormKey skinKey =
            npc.WornArmor.FormKeyNullable ??
            race.Skin.FormKeyNullable ??
            throw new InvalidDataException(
                "Sophia has no inherited skin armor.");
        Console.WriteLine(
            $"NPC={npc.FormKey}|RACE={race.FormKey}|" +
            $"ARMOR_RACE={race.ArmorRace.FormKey}|" +
            $"SKIN={skinKey}");
        Armor skin = armors[skinKey];
        foreach (IFormLinkGetter<IArmorAddonGetter> link in
                 skin.Armature)
        {
            ArmorAddon addon = addons[link.FormKey];
            FormKey? txstKey =
                addon.SkinTexture?.Female?.FormKeyNullable;
            string model =
                addon.WorldModel?.Female?.File ?? "-";
            Console.WriteLine(
                $"ARMA={addon.FormKey}|MODEL={model}|" +
                $"RACE={addon.Race.FormKey}|TXST={txstKey}");
            if (txstKey is { } key &&
                textures.TryGetValue(key, out TextureSet? texture))
            {
                Console.WriteLine(
                    $"  DIFFUSE={texture.Diffuse}|NORMAL={texture.NormalOrGloss}|" +
                    $"SPECULAR={texture.BacklightMaskOrSpecular}|" +
                    $"SUBSURFACE={texture.EnvironmentMaskOrSubsurfaceTint}");
            }
        }
    }

    private static async Task
        TestAuthenticBrigitteNpcVisualSourceComposer(
            bool paired,
            bool repairedCandidate)
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string projectRoot = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation");
        string data = Path.Combine(
            projectRoot,
            "01-source-copies",
            "brigitte-live-closure-20260726",
            "Data");
        string manifest = paired
            ? Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                repairedCandidate
                    ? "brigitte-sofia-paired-followers-v0.5-20260728"
                    : "brigitte-sofia-paired-followers-v0.4.1-20260727",
                "npcmanager-paired-package.json")
            : Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-jslot-manager-only-20260726-1",
                "npcmanager-package.json");
        string packageRoot =
            Path.GetDirectoryName(manifest)!;
        Sha256Hash packageTreeBefore =
            HashNpcPreviewTestTree(packageRoot);
        string output = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            $"npc-visual-brigitte-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);

        Assert(
            Directory.Exists(data) &&
            File.Exists(manifest) &&
            AuthenticBrigittePluginOrder.All(name =>
                File.Exists(Path.Combine(data, name))),
            "The retained Brigitte provider closure is incomplete.");
        ImmutableArray<PluginClosureReviewEntry> plugins =
            AuthenticBrigittePluginOrder
                .Select((name, index) =>
                {
                    string path = Path.Combine(data, name);
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
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-visual-loadorder.txt")),
            new WorkspacePath(Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-visual-unused")),
            new Sha256Hash(new string('4', 64)),
            plugins,
            [],
            [],
            [],
            7,
            new Sha256Hash(new string('5', 64)),
            new Sha256Hash(new string('6', 64)),
            false);
        var plugin =
            new PluginName("BrigitteBardotNpcManager.esp");
        var request = new NpcVisualPreviewComposeRequest(
            intake,
            new SkyrimMainWorkspaceIdentity(
                plugin,
                plugin,
                new FormId(0x800),
                "NPC_"),
            new NpcVisualPreviewPackageOverlay(
                new WorkspacePath(manifest),
                HashNpcPreviewTestFile(manifest)),
            new NpcVisualPreviewOptions(),
            new WorkspacePath(output));
        var composer = new BethesdaNpcVisualSourceComposer(
            new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame")),
            labRoot);
        NpcVisualSourceComposeResult result =
            await composer.ComposeSourceAsync(
                request,
                CancellationToken.None);
        NpcVisualSourceGraph? source = result.Source;
        Assert(
            HashNpcPreviewTestTree(packageRoot) ==
            packageTreeBefore,
            "Brigitte's accepted Manager package changed during preview composition.");
        if (repairedCandidate)
        {
            Assert(
                !result.Composed &&
                result.Diagnostics.Any(item =>
                    item.Code ==
                    "npc-preview-asset-missing") &&
                result.Diagnostics.Any(item =>
                    item.Code ==
                    "npc-preview-outfit-item-unsupported"),
                "The unconfirmed repair candidate was accepted without its exact Toughened Traveler provider closure.");
            return;
        }
        Assert(
            result.Composed &&
            source is not null,
            "Authentic Brigitte did not compose: " +
            string.Join(
                "; ",
                result.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        Assert(
            source!.Route == NpcVisualPreviewRoute.Cotr &&
            source.Assets.Count(item =>
                item.Role ==
                NpcVisualAssetRole.FaceGeom) == 1 &&
            source.Assets.Count(item =>
                item.Role ==
                NpcVisualAssetRole.FaceTint) == 1 &&
            source.Assets.All(item =>
                item.Role is not NpcVisualAssetRole.Hair and
                    not NpcVisualAssetRole.Eyes),
            "Brigitte's COtR or authoritative FaceGeom ownership drifted.");
        Sha256Hash expectedGeom = new(
            repairedCandidate
                ? "566C62CEC3791B1CC0F7CA2E8E47FE6FDB3CC89676A36B755AAB5F0DCC2A3403"
                : "77337F70C0BAA102B7BF2E79F3735F97D311D635ECFF48DC978E809C99AD20B1");
        Assert(
            source.Assets.Single(item =>
                item.Role ==
                NpcVisualAssetRole.FaceGeom).Sha256 ==
            expectedGeom &&
            source.Assets.Single(item =>
                item.Role ==
                NpcVisualAssetRole.FaceTint).Sha256 ==
            new Sha256Hash(
                "9A1CC74646B9F8FE61526B6978ADA366813092E4D7F73884F9744E6F68D0DCE1"),
            "Brigitte's selected package FaceGeom or FaceTint bytes drifted.");
        ImmutableArray<NpcVisualMaterial> bakedHair =
            source.Assets.Single(item =>
                    item.Role ==
                    NpcVisualAssetRole.FaceGeom)
                .Materials.Where(item =>
                    item.TintHex is not null &&
                    item.TextureSlots.Any(slot =>
                        slot.AssetPath.Value.Contains(
                            "hair",
                            StringComparison.OrdinalIgnoreCase)))
                .ToImmutableArray();
        Assert(
            bakedHair.Length == 3 &&
            bakedHair.All(item =>
                item.TintHex == "#222222") &&
            bakedHair.Select(item => item.Shape)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    AuthenticBrigitteHairShapes,
                    StringComparer.Ordinal),
            "The observed package's three baked FaceGeom HairTint authorities were not recovered as #222222.");
        Assert(
            source.HasDeclaredOutfit == repairedCandidate &&
            source.Assets.Any(item =>
                item.Role == NpcVisualAssetRole.Outfit) ==
            repairedCandidate,
            repairedCandidate
                ? "The unconfirmed repair candidate omitted its declared outfit."
                : "The observed brunette/unclothed fixture falsely acquired an outfit.");
    }

    private static async Task<string>
        TestAuthenticBrigitteNpcVisualRenderer(bool paired)
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string projectRoot = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation");
        string data = Path.Combine(
            projectRoot,
            "01-source-copies",
            "brigitte-live-closure-20260726",
            "Data");
        string manifest = paired
            ? Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-sofia-paired-followers-v0.4.1-20260727",
                "npcmanager-paired-package.json")
            : Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-jslot-manager-only-20260726-1",
                "npcmanager-package.json");
        string output = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            paired
                ? $"npc-visual-brigitte-paired-render-{Guid.NewGuid():N}"
                : $"npc-visual-brigitte-v01-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        ImmutableArray<PluginClosureReviewEntry> plugins =
            AuthenticBrigittePluginOrder
                .Select((name, index) =>
                {
                    string path = Path.Combine(data, name);
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
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-render-loadorder.txt")),
            new WorkspacePath(Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "brigitte-render-unused")),
            new Sha256Hash(new string('4', 64)),
            plugins,
            [],
            [],
            [],
            7,
            new Sha256Hash(new string('5', 64)),
            new Sha256Hash(new string('6', 64)),
            false);
        var plugin =
            new PluginName("BrigitteBardotNpcManager.esp");
        var request = new NpcVisualPreviewComposeRequest(
            intake,
            new SkyrimMainWorkspaceIdentity(
                plugin,
                plugin,
                new FormId(0x800),
                "NPC_"),
            new NpcVisualPreviewPackageOverlay(
                new WorkspacePath(manifest),
                HashNpcPreviewTestFile(manifest)),
            new NpcVisualPreviewOptions(),
            new WorkspacePath(output));
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath("F:\\ExampleGame"));
        NpcVisualSourceComposeResult source =
            await new BethesdaNpcVisualSourceComposer(
                    policy,
                    labRoot)
                .ComposeSourceAsync(
                    request,
                    CancellationToken.None);
        Assert(
            source.Composed &&
            source.Source is not null &&
            !source.Source.HasDeclaredOutfit,
            "Authentic Brigitte source did not compose as the unclothed diagnostic fixture: " +
            string.Join(
                "; ",
                source.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));

        WorkspacePath blender = new(Path.Combine(
            labRoot.Value,
            "tools",
            "external",
            "blender-4.5.1-windows-x64",
            "blender-4.5.1-windows-x64",
            "blender.exe"));
        WorkspacePath script = new(Path.Combine(
            labRoot.Value,
            "tools",
            "rendering",
            "render_npc_preview_bundle.py"));
        WorkspacePath texconv = new(Path.Combine(
            labRoot.Value,
            "tools",
            "external",
            "directxtex-texconv-2026.5.7",
            "texconv.exe"));
        ApplicationResourcePath profileManifest = new(Path.Combine(
            Environment.CurrentDirectory,
            "runtime",
            "rendering",
            "npc-preview-profile-manifest.json"));
        var renderer =
            new NpcManager.Rendering
                .BlenderNpcVisualPreviewRenderer(
                    blender,
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "blender-4.5.1-pynifly-profile")),
                    profileManifest,
                    HashNpcPreviewTestFile(
                        profileManifest.Value),
                    script,
                    texconv,
                    policy,
                    labRoot,
                    HashNpcPreviewTestFile(blender.Value),
                    HashNpcPreviewTestFile(script.Value),
                    HashNpcPreviewTestFile(texconv.Value));
        NpcVisualPreviewRenderResult render =
            await renderer.RenderAsync(
                new NpcVisualPreviewRenderRequest(
                    "npc-preview-scene/2",
                    source.Source!,
                    new WorkspacePath(output),
                    request.Options),
                CancellationToken.None);
        Assert(
            render.Rendered &&
            render.Views.Length == 6 &&
            render.ContactSheetPath is not null &&
            render.Evidence is
            {
                FaceGeomImportCount: 1,
                FaceCameraUsedAuthoritativeGeometry: true
            },
            "Authentic Brigitte rendering failed: " +
            string.Join(
                "; ",
                render.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        return output;
    }

    private static async Task<string>
        TestAuthenticSophiaNpcVisualRenderer()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string projectRoot = Path.Combine(
            labRoot.Value, "projects", "NpcManagerReimplementation");
        string data = Path.Combine(
            projectRoot, "01-source-copies",
            "sophia-live-closure-20260723", "Data");
        string manifest = Path.Combine(
            projectRoot, "03-builds", "work",
            "sophia-jslot-manager-only-20260724-8",
            "npcmanager-package.json");
        string output = Path.Combine(
            projectRoot, "03-builds", "work",
            $"npc-visual-sophia-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        ImmutableArray<PluginClosureReviewEntry> plugins =
            AuthenticSophiaPluginOrder.Select((name, index) =>
            {
                string path = Path.Combine(data, name);
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
            }).ToImmutableArray();
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(data),
            new WorkspacePath(Path.Combine(
                projectRoot, "03-builds", "work",
                "sophia-visual-render-loadorder.txt")),
            new WorkspacePath(Path.Combine(
                projectRoot, "03-builds", "work",
                "sophia-visual-render-unused")),
            new Sha256Hash(new string('1', 64)),
            plugins,
            [], [], [], 7,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
        var outputPlugin =
            new PluginName("SophiaLorenNpcManager.esp");
        var request = new NpcVisualPreviewComposeRequest(
            intake,
            new SkyrimMainWorkspaceIdentity(
                outputPlugin,
                outputPlugin,
                new FormId(0x800),
                "NPC_"),
            new NpcVisualPreviewPackageOverlay(
                new WorkspacePath(manifest),
                HashNpcPreviewTestFile(manifest)),
            new NpcVisualPreviewOptions(),
            new WorkspacePath(output));
        var sourceComposer = new BethesdaNpcVisualSourceComposer(
            new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath("F:\\ExampleGame")),
            labRoot);
        NpcVisualSourceComposeResult source =
            await sourceComposer.ComposeSourceAsync(
                request, CancellationToken.None);
        Assert(source.Composed && source.Source is not null,
            "Authentic Sophia source did not compose for rendering: " +
            string.Join("; ", source.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}")));

        WorkspacePath blender = new(Path.Combine(
            labRoot.Value, "tools", "external",
            "blender-4.5.1-windows-x64",
            "blender-4.5.1-windows-x64", "blender.exe"));
        WorkspacePath script = new(Path.Combine(
            labRoot.Value, "tools", "rendering",
            "render_npc_preview_bundle.py"));
        WorkspacePath texconv = new(Path.Combine(
            labRoot.Value, "tools", "external",
            "directxtex-texconv-2026.5.7", "texconv.exe"));
        ApplicationResourcePath profileManifest = new(Path.Combine(
            Environment.CurrentDirectory,
            "runtime",
            "rendering",
            "npc-preview-profile-manifest.json"));
        var renderer = new NpcManager.Rendering
            .BlenderNpcVisualPreviewRenderer(
                blender,
                new WorkspacePath(Path.Combine(
                    labRoot.Value, "tools", "external",
                    "blender-4.5.1-pynifly-profile")),
                profileManifest,
                HashNpcPreviewTestFile(
                    profileManifest.Value),
                script,
                texconv,
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot,
                HashNpcPreviewTestFile(blender.Value),
                HashNpcPreviewTestFile(script.Value),
                HashNpcPreviewTestFile(texconv.Value));
        NpcVisualPreviewRenderResult render =
            await renderer.RenderAsync(
                new NpcVisualPreviewRenderRequest(
                    "npc-preview-scene/2",
                    source.Source!,
                    new WorkspacePath(output),
                    request.Options),
                CancellationToken.None);
        Assert(render.Rendered &&
               render.Evidence is
               {
                   FaceGeomImportCount: 1,
                   FaceCameraUsedAuthoritativeGeometry: true
               } &&
               render.Evidence.MaterialApplicationCounts
                   .TryGetValue(
                       "faceDiffuseTimesFaceTintCount",
                       out int faceCompositionCount) &&
               faceCompositionCount == 1 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenTriAssetCount"] == 2 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenRequestedMorphCount"] == 59 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenResolvedMorphCount"] == 13 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenUnresolvedMorphCount"] == 46 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenMorphedMeshCount"] == 1 &&
               render.Evidence.MaterialApplicationCounts[
                   "bodyGenPositionOffsetCount"] == 10570 &&
               render.Views.Length == 6 &&
               render.ContactSheetPath is not null &&
               render.ContactSheetSha256 ==
               new Sha256Hash(
                   "772E78246092EF4A01E8C47DEF3659DCDCBD98E21BC79427D555FAF926C34102"),
            "Authentic Sophia did not retain one active slot-0 diffuse multiplied by the canonical slot-6 FaceTint and exact available COtR BodyGen geometry: " +
            string.Join("; ", render.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}")));
        return output;
    }

    private static async Task
        TestNpcVisualPreviewVisualValidator(string imagePath)
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        Assert(File.Exists(imagePath),
            "The NPC visual validation image does not exist.");
        WorkspacePath runtimeRoot = new(Path.Combine(
            labRoot.Value, "projects",
            "NpcManagerReimplementation", "src",
            "NpcManager.Desktop", "bin", "Release",
            "net10.0-windows", "runtime",
            "reference-preset"));
        using var native = new MediaPipeNativeApi(
            labRoot, runtimeRoot);
        ReferencePresetRuntimeAdmissionResult admission =
            native.AdmitRuntime();
        Assert(admission.Accepted &&
               admission.ManifestSha256 is not null,
            "The admitted reference runtime was unavailable: " +
            string.Join("; ", admission.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}")));
        var view = new NpcVisualPreviewView(
            "face-front",
            new WorkspacePath(imagePath),
            HashNpcPreviewTestFile(imagePath),
            new WorkspacePath(imagePath),
            HashNpcPreviewTestFile(imagePath),
            900,
            900);
        var validator = new NpcVisualPreviewVisualValidator(
            new SkiaReferenceImageDecoder(labRoot),
            new MediaPipeFaceLandmarkInferenceService(native),
            new ReferenceSemanticLandmarkProjector(),
            admission.ManifestSha256!.Value);
        NpcVisualPreviewVisualEvidence evidence =
            await validator.ValidateEncodedAsync(
                view,
                await File.ReadAllBytesAsync(imagePath),
                CancellationToken.None);
        Assert(evidence.DetectedFaceCount == 1 &&
               evidence.LandmarkCount == 478 &&
               evidence.SemanticAnchorCount == 31 &&
               evidence.EyesNoseAndMouthBounded &&
               !evidence.Diagnostics.Any(item =>
                   item.Severity == DiagnosticSeverity.Error),
            "The rendered NPC face did not pass native structural validation: " +
            string.Join("; ", evidence.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}")));
    }

    private static void WriteNpcVisualFixture(string path)
    {
        ModKey key =
            ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var npcKey = new FormKey(key, 0x800);
        var raceKey = new FormKey(key, 0x900);
        var bodyAddonKey = new FormKey(key, 0x910);
        var outfitAddonKey = new FormKey(key, 0x911);
        var skinKey = new FormKey(key, 0x920);
        var dressKey = new FormKey(key, 0x921);
        var outfitKey = new FormKey(key, 0x930);
        var colorKey = new FormKey(key, 0x940);

        var bodyAddon = new ArmorAddon(
            bodyAddonKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureBodyAA",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = BipedObjectFlag.Body
            },
            WorldModel = new GenderedItem<Model?>(
                null,
                new Model { File = "fixture\\body_1.nif" })
        };
        mod.ArmorAddons.Add(bodyAddon);
        var outfitAddon = new ArmorAddon(
            outfitAddonKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureDressAA",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = BipedObjectFlag.Body
            },
            WorldModel = new GenderedItem<Model?>(
                null,
                new Model { File = "fixture\\dress_1.nif" })
        };
        mod.ArmorAddons.Add(outfitAddon);
        var skin = new Armor(
            skinKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureSkin",
            Race = new FormLinkNullable<IRaceGetter>(raceKey)
        };
        skin.Armature.Add(
            new FormLink<IArmorAddonGetter>(bodyAddonKey));
        mod.Armors.Add(skin);
        var dress = new Armor(
            dressKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureDress",
            Race = new FormLinkNullable<IRaceGetter>(raceKey)
        };
        dress.Armature.Add(
            new FormLink<IArmorAddonGetter>(outfitAddonKey));
        mod.Armors.Add(dress);
        mod.Outfits.Add(new Outfit(
            outfitKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureOutfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(dressKey)
            ]
        });
        mod.Races.Add(new Race(
            raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureNordRace",
            Skin = new FormLinkNullable<IArmorGetter>(skinKey)
        });
        mod.Colors.Add(new ColorRecord(
            colorKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureBlonde",
            Color = Color.FromArgb(255, 0xD6, 0xBE, 0x83)
        });
        mod.Npcs.Add(new Npc(
            npcKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureNpc",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(raceKey),
            HairColor =
                new FormLinkNullable<IColorRecordGetter>(colorKey),
            DefaultOutfit =
                new FormLinkNullable<IOutfitGetter>(outfitKey),
            TextureLighting =
                Color.FromArgb(255, 0xE7, 0xB7, 0x9D),
            Weight = 73
        });
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                RecordCount = RecordCountOption.Iterate
            });
    }

    private static void WriteNpcVisualOverrideFixture(
        string path,
        string ownerName)
    {
        ModKey owner = ModKey.FromNameAndExtension(ownerName);
        ModKey winner =
            ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(winner, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = owner });
        mod.Npcs.Add(new Npc(
            new FormKey(owner, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "VisualFixtureNpcOverride",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(
                new FormKey(owner, 0x900)),
            HairColor = new FormLinkNullable<IColorRecordGetter>(
                new FormKey(owner, 0x940)),
            TextureLighting =
                Color.FromArgb(255, 0xE7, 0xB7, 0x9D),
            Weight = 73
        });
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                RecordCount = RecordCountOption.Iterate
            });
    }

    private static void WriteNpcVisualFaceGenAssets(
        string dataRoot,
        string faceGenOwner)
    {
        WriteNpcVisualFile(
            dataRoot,
            $"meshes/actors/character/FaceGenData/FaceGeom/{faceGenOwner}/00000800.nif",
            TextureSet(
                "textures/fixture/head.dds",
                "",
                "",
                "",
                "",
                "",
                $"textures/actors/character/FaceGenData/FaceTint/{faceGenOwner}/00000800.dds",
                ""));
        WriteNpcVisualFile(
            dataRoot,
            $"textures/actors/character/FaceGenData/FaceTint/{faceGenOwner}/00000800.dds",
            [0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4]);
        WriteNpcVisualFile(
            dataRoot,
            "textures/fixture/head.dds",
            [0x44, 0x44, 0x53, 0x20, 5]);
    }

    private static void WriteNpcVisualFixtureAssets(string dataRoot)
    {
        WriteNpcVisualFile(
            dataRoot,
            "meshes/actors/character/FaceGenData/FaceGeom/VisualFixture.esp/00000800.nif",
            TextureSet(
                "textures/fixture/head.dds",
                "",
                "",
                "",
                "",
                "",
                "textures/actors/character/FaceGenData/FaceTint/VisualFixture.esp/00000800.dds",
                ""));
        WriteNpcVisualFile(
            dataRoot,
            "textures/actors/character/FaceGenData/FaceTint/VisualFixture.esp/00000800.dds",
            [0x44, 0x44, 0x53, 0x20, 1, 2, 3, 4]);
        WriteNpcVisualFile(
            dataRoot,
            "meshes/fixture/body_1.nif",
            TextureSet("textures/fixture/body.dds"));
        WriteNpcVisualFile(
            dataRoot,
            "meshes/fixture/body_0.nif",
            TextureSet("textures/fixture/body.dds"));
        WriteNpcVisualFile(
            dataRoot,
            "meshes/fixture/dress_1.nif",
            TextureSet("textures/fixture/dress.dds"));
        WriteNpcVisualFile(
            dataRoot,
            "meshes/fixture/dress_0.nif",
            TextureSet("textures/fixture/dress.dds"));
        WriteNpcVisualFile(
            dataRoot,
            "textures/fixture/head.dds",
            [0x44, 0x44, 0x53, 0x20, 5]);
        WriteNpcVisualFile(
            dataRoot,
            "textures/fixture/body.dds",
            [0x44, 0x44, 0x53, 0x20, 6]);
        WriteNpcVisualFile(
            dataRoot,
            "textures/fixture/dress.dds",
            [0x44, 0x44, 0x53, 0x20, 7]);
    }

    private static byte[] TextureSet(params string[] paths)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x10203040);
        writer.Write(paths.Length);
        foreach (string path in paths)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(path);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteNpcVisualFile(
        string dataRoot,
        string relative,
        byte[] bytes)
    {
        string path = Path.Combine(
            dataRoot,
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static Sha256Hash HashNpcPreviewTestTree(
        string root)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(
            stream,
            new System.Text.UTF8Encoding(false),
            leaveOpen: true);
        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderBy(
                         path => path,
                         StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(
                    root,
                    file)
                .Replace('\\', '/');
            writer.Write(
                HashNpcPreviewTestFile(file).Value);
            writer.Write("  ");
            writer.WriteLine(relative);
        }
        writer.Flush();
        stream.Position = 0;
        return new Sha256Hash(
            Convert.ToHexString(
                SHA256.HashData(stream)));
    }
}
