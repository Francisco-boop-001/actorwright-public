using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.BodyGen;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.TestInfrastructure;

namespace NpcManager.BethesdaFaceRouting.Tests;

internal static class Program
{
    private const string Selector_LightPluginCapturedHdptRouting = "--test-light-plugin-captured-hdpt-routing";

    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static WorkspacePath LabRoot => FindLabRoot();

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        if (args is [Selector_LightPluginCapturedHdptRouting])
        {
            WorkspacePath productRoot = FindProductRoot();
            string fixtureBase = Path.Combine(
                productRoot.Value,
                "artifacts",
                "tests",
                "NpcManager.BethesdaFaceRouting.Tests");
            TestLightPluginCapturedHdptCleanupOwnership(productRoot, fixtureBase);
            await TestLightPluginCapturedHdptRouting(
                productRoot,
                fixtureBase,
                Path.Combine(fixtureBase,
                    "light-formid-routing-" + Guid.NewGuid().ToString("N")));
            Console.WriteLine("PASS captured light-plugin HDPT IDs normalize to provider-local records");
            Console.WriteLine("RESULT PASS 1/1");
            return 0;
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("FAIL unknown standalone test selector.");
            return 2;
        }

        _ = LabRoot;
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("captured light-plugin HDPT IDs normalize to provider-local records", TestLightPluginCapturedHdptRouting),
            ("native tint routing admits default-only reused TINI rows and rejects ambiguous overrides", TestNativeTintDuplicateTiniRouting),
            ("whole-skin authority resolves inherited WNAM body hands and feet",
                TestWholeSkinAuthority),
            ("real Sophia COR closure resolves the exact failed inherited skin route",
                TestRealSophiaWholeSkinAuthority),
            ("real Emi2 providers expose exact head, eye, brow, mouth, and hair routes", TestRealEmi2Route),
            ("winning RACE tint authority exposes exact gendered TINI order", TestRaceTintAuthority),
            ("captured COR race preserves duplicate TINI rows without ambiguous NPC routing", TestCapturedCorRaceTintAuthority),
            ("direct RaceMenu faceTextures resolve one exact winning TXST", TestDirectFaceTextureSetMatch),
            ("RACE routing and recursive HNAM type inheritance are deterministic", TestRaceAndHnamSemantics),
            ("RaceMenu preset compatibility uses HDPT valid races and RACE defaults", TestPresetRaceCompatibility),
            ("Ruby custom HDPT PNAM values preserve provider identity and map to Misc", TestRubyCustomHdptPnamAuthority),
            ("Ruby Dint admits only its 22 hash-bound absent Hair NAM0 TRI routes", TestRubyDintAbsentHairTriBoundary),
            ("stale hashes and providers outside K fail before decode", TestProviderAuthorityRefusals),
            ("missing roles, cycles, duplicate HNAM, and malformed records fail closed", TestGraphRefusals),
            ("malformed copied plugins fail closed", TestMalformedPluginRefusal)
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

    private static async Task TestRubyCustomHdptPnamAuthority()
    {
        string dataRoot = Path.Combine(
            LabRoot.Value, "projects", "RubyPresetAudit",
            "02-normalized-resources", "ruby-fresh-build-v0.1",
            "provider-tree", "Data");
        const string cveoPlugin = "CompleteVanillaEyeOverhaul_CVEObyLDD.esp";
        const string advancedPlugin = "AdvancedEyesAddon_CVEObyLDD.esp";
        string cveoPath = Path.Combine(dataRoot, cveoPlugin);
        string advancedPath = Path.Combine(dataRoot, advancedPlugin);
        var cveoKey = ModKey.FromNameAndExtension(cveoPlugin);
        var advancedKey = ModKey.FromNameAndExtension(advancedPlugin);
        var cveoAuthority = new SkyrimFaceRecordPluginAuthority(
            new PluginName(cveoPlugin), new WorkspacePath(cveoPath), HashFile(cveoPath));
        var advancedAuthority = new SkyrimFaceRecordPluginAuthority(
            new PluginName(advancedPlugin), new WorkspacePath(advancedPath), HashFile(advancedPath));
        (PluginName Plugin, uint FormId)[] selected =
        [
            (new PluginName(cveoPlugin), 0x915),
            (new PluginName(cveoPlugin), 0x8E7),
            (new PluginName(cveoPlugin), 0xCC0),
            (new PluginName(cveoPlugin), 0xCA1),
            (new PluginName(advancedPlugin), 0xB7A),
            (new PluginName(cveoPlugin), 0x827),
            (new PluginName(cveoPlugin), 0x845),
            (new PluginName(cveoPlugin), 0xCCE),
            (new PluginName(cveoPlugin), 0x836),
            (new PluginName(cveoPlugin), 0x8CA),
            (new PluginName(cveoPlugin), 0x88F),
            (new PluginName(cveoPlugin), 0x854),
            (new PluginName(cveoPlugin), 0x881),
            (new PluginName(cveoPlugin), 0x872),
            (new PluginName(cveoPlugin), 0x805)
        ];
        ImmutableArray<FormReference> references = selected
            .Select(item => new FormReference(item.Plugin, new FormId(item.FormId)))
            .ToImmutableArray();
        var rawValues = new List<int>(selected.Length);
        using (var cveo = SkyrimMod.CreateFromBinaryOverlay(
                   new ModPath(cveoKey, new FilePath(cveoPath)), SkyrimRelease.SkyrimSE))
        using (var advanced = SkyrimMod.CreateFromBinaryOverlay(
                   new ModPath(advancedKey, new FilePath(advancedPath)), SkyrimRelease.SkyrimSE))
        {
            foreach ((PluginName plugin, uint formId) in selected)
            {
                var mod = plugin == new PluginName(cveoPlugin) ? cveo : advanced;
                IHeadPartGetter record = mod.HeadParts.Single(item =>
                    item.FormKey.ID == formId);
                rawValues.Add(Convert.ToInt32(record.Type));
            }
        }
        int[] expectedRawValues =
            [184, 182, 191, 187, 169, 172, 174, 193, 173, 181, 178, 175, 177, 176, 171];
        Assert(rawValues.SequenceEqual(expectedRawValues),
            "Ruby's selected custom HDPT fixture did not expose the exact 15 reviewed PNAM values.");
        Console.WriteLine("EVIDENCE Ruby custom HDPT PNAM=" + string.Join(',', rawValues));

        var authorities = ImmutableArray.Create(cveoAuthority, advancedAuthority);
        var reader = new BethesdaSkyrimMajorRecordBindingReader(
            new FixedAuthorityLoader(authorities));
        SkyrimMajorRecordBindingResult result = await reader.ReadAsync(
            new SkyrimMajorRecordBindingRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(dataRoot),
                references.Select(item => new SkyrimMajorRecordBindingSelection(
                    item, new RecordSignature("HDPT"))).ToImmutableArray(),
                authorities),
            CancellationToken.None);
        Assert(result.Accepted && result.Bindings.Length == 15 &&
               result.Bindings.Select(item => item.Reference).SequenceEqual(references) &&
               result.Bindings.All(item => item.HeadPartType == NpcHeadPartType.Misc),
            $"Ruby custom PNAM bindings were not preserved and mapped to Misc: {FormatDiagnostics(result.Diagnostics)}");
    }

    private static Task TestRubyDintAbsentHairTriBoundary()
    {
        (uint FormId, string Path)[] expected =
        [
            (0xBC05, "meshes/armor/[dint999]/02 hair/hairS/16/1Hl.tri"),
            (0x3090, "meshes/armor/[dint999]/02 hair/hairS/16/a5_1.tri"),
            (0x308F, "meshes/armor/[dint999]/02 hair/hairS/16/a5_4.tri"),
            (0x308E, "meshes/armor/[dint999]/02 hair/hairS/16/a5_2.tri"),
            (0x308D, "meshes/armor/[dint999]/02 hair/hairS/16/a5_2.tri"),
            (0x308C, "meshes/armor/[dint999]/02 hair/hairS/16/a5_1.tri"),
            (0x308B, "meshes/armor/[dint999]/02 hair/hairS/16/a4_4.tri"),
            (0x308A, "meshes/armor/[dint999]/02 hair/hairS/16/a4_3.tri"),
            (0x3089, "meshes/armor/[dint999]/02 hair/hairS/16/a4_2.tri"),
            (0x3088, "meshes/armor/[dint999]/02 hair/hairS/16/a4_1.tri"),
            (0x3087, "meshes/armor/[dint999]/02 hair/hairS/16/a3_4.tri"),
            (0x3086, "meshes/armor/[dint999]/02 hair/hairS/16/a3_3.tri"),
            (0x3085, "meshes/armor/[dint999]/02 hair/hairS/16/a3_2.tri"),
            (0x3084, "meshes/armor/[dint999]/02 hair/hairS/16/a3_1.tri"),
            (0x3083, "meshes/armor/[dint999]/02 hair/hairS/16/a2_4.tri"),
            (0x3082, "meshes/armor/[dint999]/02 hair/hairS/16/a2_3.tri"),
            (0x3081, "meshes/armor/[dint999]/02 hair/hairS/16/a2_2.tri"),
            (0x3080, "meshes/armor/[dint999]/02 hair/hairS/16/a2_1.tri"),
            (0x307F, "meshes/armor/[dint999]/02 hair/hairS/16/a1_4.tri"),
            (0x307E, "meshes/armor/[dint999]/02 hair/hairS/16/a1_3.tri"),
            (0x307D, "meshes/armor/[dint999]/02 hair/hairS/16/a1_2.tri"),
            (0x307C, "meshes/armor/[dint999]/02 hair/hairS/16/a1_1.tri")
        ];
        Assert(expected.Length == 22 &&
               SkyrimExternalHairTriOmissionPolicy.DeclaredAbsentHairNam0Paths.Length == 22,
            "The Dint omission policy does not retain exactly 22 declared-absent routes.");
        var provider = new SkyrimFaceRecordProvider(
            new PluginName(SkyrimExternalHairTriOmissionPolicy.DintPlugin),
            new WorkspacePath(Path.Combine(LabRoot.Value, "projects", "RubyPresetAudit")),
            new Sha256Hash(SkyrimExternalHairTriOmissionPolicy.DintPluginSha256));
        foreach ((uint formId, string path) in expected)
        {
            var tri = new SkyrimHdptTriRoute(
                SkyrimHdptTriRole.Mesh, new AssetPath(path));
            var headPart = new SkyrimFaceHeadPartRecordRoute(
                Reference(SkyrimExternalHairTriOmissionPolicy.DintPlugin, formId),
                provider, "DintHair", NpcHeadPartType.Misc, NpcHeadPartType.Misc,
                new AssetPath("meshes/armor/[dint999]/02 hair/hairS/16/1HL.nif"),
                [tri], [], null, 0, true, false);
            Assert(SkyrimExternalHairTriOmissionPolicy
                       .IsAllowedMissingHairNam0(headPart, tri),
                $"Declared-absent Dint Hair NAM0 route was refused: {formId:X8} {path}");
        }

        var admitted = new SkyrimFaceHeadPartRecordRoute(
            Reference(SkyrimExternalHairTriOmissionPolicy.DintPlugin, 0xBC05),
            provider, "DintHair", NpcHeadPartType.Misc, NpcHeadPartType.Misc,
            new AssetPath("meshes/armor/[dint999]/02 hair/hairS/16/1HL.nif"),
            [], [], null, 0, true, false);
        var wrongRole = new SkyrimHdptTriRoute(
            SkyrimHdptTriRole.CharGen,
            new AssetPath("meshes/armor/[dint999]/02 hair/hairS/16/1HL.tri"));
        Assert(!SkyrimExternalHairTriOmissionPolicy
                   .IsAllowedMissingHairNam0(admitted, wrongRole),
            "Dint omission policy admitted a non-NAM0 TRI role.");
        var wrongProvider = admitted with
        {
            Provider = provider with
            {
                Sha256 = new Sha256Hash(new string('0', 64))
            }
        };
        Assert(!SkyrimExternalHairTriOmissionPolicy
                   .IsAllowedMissingHairNam0(wrongProvider, new SkyrimHdptTriRoute(
                       SkyrimHdptTriRole.Mesh,
                       new AssetPath("meshes/armor/[dint999]/02 hair/hairS/16/1HL.tri"))),
            "Dint omission policy admitted a wrong provider hash.");
        var unlisted = new SkyrimHdptTriRoute(
            SkyrimHdptTriRole.Mesh,
            new AssetPath("meshes/armor/[dint999]/02 hair/hairS/16/not-declared.tri"));
        Assert(!SkyrimExternalHairTriOmissionPolicy
                   .IsAllowedMissingHairNam0(admitted, unlisted),
            "Dint omission policy admitted an unlisted TRI path.");
        return Task.CompletedTask;
    }

    private static async Task TestNativeTintDuplicateTiniRouting()
    {
        string root = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests",
            "native-tint-duplicate-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        string pluginPath = Path.Combine(dataRoot, "DuplicateTintFixture.esp");
        try
        {
            ModKey key = ModKey.FromNameAndExtension("DuplicateTintFixture.esp");
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var raceKey = new FormKey(key, 0x800);
            var npcKey = new FormKey(key, 0x801);
            var female = new HeadData();
            female.TintMasks.Add(new TintAssets
            {
                Index = 1,
                FileName = new AssetLink<SkyrimTextureAssetType>("fixture/skin.dds"),
                MaskType = TintAssets.TintMaskType.SkinTone
            });
            female.TintMasks.Add(new TintAssets
            {
                Index = 2,
                FileName = new AssetLink<SkyrimTextureAssetType>("fixture/makeup.dds"),
                MaskType = TintAssets.TintMaskType.Paint
            });
            female.TintMasks.Add(new TintAssets
            {
                Index = 2,
                FileName = new AssetLink<SkyrimTextureAssetType>("fixture/warpaint.dds"),
                MaskType = TintAssets.TintMaskType.Paint
            });
            mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "DuplicateTintRace",
                HeadData = new GenderedItem<HeadData?>(new HeadData(), female)
            });
            var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "DuplicateTintNpc",
                Configuration = new NpcConfiguration
                {
                    Flags = NpcConfiguration.Flag.Female
                },
                Race = new FormLink<IRaceGetter>(raceKey)
            };
            npc.TintLayers.Add(new TintLayer
            {
                Index = 1,
                Color = System.Drawing.Color.FromArgb(255, 200, 150, 100),
                InterpolationValue = 1F,
                Preset = 0
            });
            mod.Npcs.Add(npc);
            mod.WriteToBinary(new Noggog.FilePath(pluginPath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            var authority = new SkyrimFaceRecordPluginAuthority(
                new PluginName("DuplicateTintFixture.esp"),
                new WorkspacePath(pluginPath),
                HashFile(pluginPath));
            var resolver = new BethesdaSkyrimNativeFaceTintRecordResolver(
                new KBoundTestPolicy(), LabRoot);
            var request = new SkyrimNativeFaceTintRecordRequest(
                GameEdition.SkyrimSpecialEdition,
                Reference("DuplicateTintFixture.esp", 0x801),
                NpcSex.Female,
                Reference("DuplicateTintFixture.esp", 0x800),
                [authority]);
            SkyrimNativeFaceTintRecordResult accepted = await resolver.ResolveAsync(
                request, CancellationToken.None);
            Assert(accepted.Accepted && accepted.Route is not null &&
                   accepted.Route.Layers.Select(item => item.Index)
                       .SequenceEqual(new ushort[] { 1, 2, 2 }) &&
                   accepted.Route.Layers.Where(item => item.Index == 2)
                       .All(item => item.ColorSource ==
                                    SkyrimNativeFaceTintColorSource.RaceDefault),
                $"Default-only duplicate TINI rows were refused: {FormatDiagnostics(accepted.Diagnostics)}");

            SkyrimNativeFaceTintRecordResult refused = await resolver.ResolveAsync(
                request with
                {
                    MaskOverrides =
                    [
                        new SkyrimNativeFaceTintMaskOverride(
                            2, new AssetPath("textures/fixture/override.dds"))
                    ]
                },
                CancellationToken.None);
            Assert(!refused.Accepted && refused.Diagnostics.Any(item =>
                       item.Code == "skyrim-native-tint-race-index-ambiguous"),
                "A mask override targeting reused RACE TINI 2 was not refused as ambiguous.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestDirectFaceTextureSetMatch()
    {
        string root = Path.Combine(LabRoot.Value, "03-builds", "work",
            "face-texture-match-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        string pluginPath = Path.Combine(dataRoot, "FaceTextureMatch.esp");
        try
        {
            ModKey key = ModKey.FromNameAndExtension("FaceTextureMatch.esp");
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.TextureSets.Add(new TextureSet(
                new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NotThePresetTextureSet",
                Diffuse = @"!COR\Head\OtherHead.dds",
                NormalOrGloss = @"!COR\Head\femalehead_msn_009.dds",
                GlowOrDetailMap = @"!COR\Head\femalehead_sk.dds",
                Height = @"Actors\Character\Male\BlankDetailmap.dds",
                BacklightMaskOrSpecular = @"!COR\Head\femalehead_s.dds"
            });
            mod.TextureSets.Add(new TextureSet(
                new FormKey(key, 0x801), SkyrimRelease.SkyrimSE)
            {
                EditorID = "SophiaDirectFaceTextures",
                Diffuse = @"!COR\Head\FemaleHead.dds",
                NormalOrGloss = @"!COR\Head\femalehead_msn_009.dds",
                GlowOrDetailMap = @"!COR\Head\femalehead_sk.dds",
                Height = @"Actors\Character\Male\BlankDetailmap.dds",
                BacklightMaskOrSpecular = @"!COR\Head\femalehead_s.dds"
            });
            mod.TextureSets.Add(new TextureSet(
                new FormKey(key, 0x802), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ChelStyleDirectFaceTextures",
                Diffuse = @"!COR\Head\FemaleHead.dds",
                NormalOrGloss = @"!COR\Head\femalehead_msn_009.dds",
                GlowOrDetailMap = @"!COR\Head\femalehead_sk.dds",
                Height = @"Actors\Character\Male\BlankDetailmap.dds"
            });
            mod.WriteToBinary(new Noggog.FilePath(pluginPath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            var authority = new SkyrimFaceRecordPluginAuthority(
                new PluginName("FaceTextureMatch.esp"),
                new WorkspacePath(pluginPath),
                HashFile(pluginPath));
            var resolver = new BethesdaSkyrimFaceTextureSetMatchResolver(
                new FixedAuthorityLoader([authority]));
            SkyrimFaceTextureSetMatchResult result = await resolver.ResolveAsync(
                new SkyrimFaceTextureSetMatchRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot),
                    [
                        new RaceMenuFaceTexture(0, @"!COR/Head/FemaleHead.dds"),
                        new RaceMenuFaceTexture(1, @"!COR/Head/femalehead_msn_009.dds"),
                        new RaceMenuFaceTexture(2, @"!COR/Head/femalehead_sk.dds"),
                        new RaceMenuFaceTexture(7, @"!COR/Head/femalehead_s.dds")
                    ],
                    [authority]),
                CancellationToken.None);

            Assert(result.Accepted && result.Authority is
            {
                TextureSet.FormId.Value: 0x801,
                Paths.Height.Value: "Actors/Character/Male/BlankDetailmap.dds",
                RuntimeAuthority: false
            },
                $"Direct faceTextures did not resolve the unique complete TXST: {FormatDiagnostics(result.Diagnostics)}");

            SkyrimFaceTextureSetMatchResult inferredSlot = await resolver.ResolveAsync(
                new SkyrimFaceTextureSetMatchRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot),
                    [
                        new RaceMenuFaceTexture(0, @"!COR/Head/FemaleHead.dds"),
                        new RaceMenuFaceTexture(1, @"!COR/Head/femalehead_msn_009.dds"),
                        new RaceMenuFaceTexture(2, @"!COR/Head/femalehead_sk.dds")
                    ],
                    [authority],
                    new FormReference(new PluginName("FaceTextureMatch.esp"), new FormId(0x801))),
                CancellationToken.None);

            Assert(inferredSlot.Accepted && inferredSlot.Authority is
            {
                TextureSet.FormId.Value: 0x801,
                Paths.BacklightMaskOrSpecular.Value: "!COR/Head/femalehead_s.dds",
                RuntimeAuthority: false
            } && inferredSlot.Diagnostics.Any(item =>
                item.Code == "skyrim-face-texture-match-inferred-slot7"),
                "Direct faceTextures with omitted slot 7 did not inherit the explicit TXST specular/backlight route: " +
                FormatDiagnostics(inferredSlot.Diagnostics));

            SkyrimFaceTextureSetMatchResult blankSlot = await resolver.ResolveAsync(
                new SkyrimFaceTextureSetMatchRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot),
                    [
                        new RaceMenuFaceTexture(0, @"!COR/Head/FemaleHead.dds"),
                        new RaceMenuFaceTexture(1, @"!COR/Head/femalehead_msn_009.dds"),
                        new RaceMenuFaceTexture(2, @"!COR/Head/femalehead_sk.dds")
                    ],
                    [authority],
                    new FormReference(new PluginName("FaceTextureMatch.esp"), new FormId(0x802))),
                CancellationToken.None);

            Assert(blankSlot.Accepted && blankSlot.Authority is
            {
                TextureSet.FormId.Value: 0x802,
                Paths.BacklightMaskOrSpecular.Value: "Actors/Character/Male/BlankDetailmap.dds",
                RuntimeAuthority: false
            } && blankSlot.Diagnostics.Any(item =>
                item.Code == "skyrim-face-texture-match-blank-slot7"),
                "Direct faceTextures with omitted slot 7 did not fall back to the blank detail map for a three-slot TXST: " +
                FormatDiagnostics(blankSlot.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestWholeSkinAuthority()
    {
        string root = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests",
            "whole-skin-authority-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        string pluginPath = Path.Combine(dataRoot, "WholeSkinFixture.esp");
        try
        {
            ModKey key = ModKey.FromNameAndExtension("WholeSkinFixture.esp");
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var raceKey = new FormKey(key, 0x800);
            var skinKey = new FormKey(key, 0x801);
            var bodyAddonKey = new FormKey(key, 0x802);
            var handAddonKey = new FormKey(key, 0x803);
            var footAddonKey = new FormKey(key, 0x804);
            var bodyTextureKey = new FormKey(key, 0x805);
            var handTextureKey = new FormKey(key, 0x806);
            var footTextureKey = new FormKey(key, 0x807);
            var outfitKey = new FormKey(key, 0x808);
            var outfitArmorKey = new FormKey(key, 0x809);
            var outfitBodyAddonAKey = new FormKey(key, 0x80A);
            var outfitBodyAddonBKey = new FormKey(key, 0x80B);
            var repairOutfitKey = new FormKey(key, 0x80C);
            var repairArmorKey = new FormKey(key, 0x80D);
            var repairBodyAddonKey = new FormKey(key, 0x80E);

            mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "WholeSkinRace",
                Skin = new FormLinkNullable<IArmorGetter>(skinKey)
            });
            var skin = new Armor(skinKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "WholeSkinNaked",
                Race = new FormLinkNullable<IRaceGetter>(raceKey),
                BodyTemplate = new BodyTemplate
                {
                    FirstPersonFlags = (BipedObjectFlag)(0x04 | 0x08 | 0x80)
                }
            };
            skin.Armature.Add(new FormLink<IArmorAddonGetter>(bodyAddonKey));
            skin.Armature.Add(new FormLink<IArmorAddonGetter>(handAddonKey));
            skin.Armature.Add(new FormLink<IArmorAddonGetter>(footAddonKey));
            mod.Armors.Add(skin);

            mod.ArmorAddons.Add(BuildSkinAddon(
                bodyAddonKey, raceKey, bodyTextureKey, 0x04, "WholeSkinBody"));
            mod.ArmorAddons.Add(BuildSkinAddon(
                handAddonKey, raceKey, handTextureKey, 0x08, "WholeSkinHands"));
            mod.ArmorAddons.Add(BuildSkinAddon(
                footAddonKey, raceKey, footTextureKey, 0x80, "WholeSkinFeet"));
            mod.TextureSets.Add(BuildSkinTextureSet(
                bodyTextureKey, "fixture/body"));
            mod.TextureSets.Add(BuildSkinTextureSet(
                handTextureKey, "fixture/hands"));
            mod.TextureSets.Add(BuildSkinTextureSet(
                footTextureKey, "fixture/feet"));
            var outfitArmor = new Armor(
                outfitArmorKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "AmbiguousExposedBodyArmor"
            };
            outfitArmor.Armature.Add(
                new FormLink<IArmorAddonGetter>(
                    outfitBodyAddonAKey));
            outfitArmor.Armature.Add(
                new FormLink<IArmorAddonGetter>(
                    outfitBodyAddonBKey));
            mod.Armors.Add(outfitArmor);
            mod.ArmorAddons.Add(BuildSkinAddon(
                outfitBodyAddonAKey,
                raceKey,
                bodyTextureKey,
                0x04,
                "AmbiguousExposedBodyA"));
            mod.ArmorAddons.Add(BuildSkinAddon(
                outfitBodyAddonBKey,
                raceKey,
                bodyTextureKey,
                0x04,
                "AmbiguousExposedBodyB"));
            mod.Outfits.Add(new Outfit(
                outfitKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "AmbiguousExposedBodyOutfit",
                Items =
                [
                    new FormLink<IOutfitTargetGetter>(
                        outfitArmorKey)
                ]
            });
            var repairArmor = new Armor(
                repairArmorKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "RepairRequiredBodyArmor"
            };
            repairArmor.Armature.Add(
                new FormLink<IArmorAddonGetter>(repairBodyAddonKey));
            mod.Armors.Add(repairArmor);
            var repairAddon = BuildSkinAddon(
                repairBodyAddonKey,
                raceKey,
                bodyTextureKey,
                0x04,
                "RepairRequiredBodyAddon");
            repairAddon.SkinTexture =
                new GenderedItem<
                    IFormLinkNullableGetter<ITextureSetGetter>>(
                    repairAddon.SkinTexture?.Male ??
                    new FormLinkNullable<ITextureSetGetter>(),
                    new FormLinkNullable<ITextureSetGetter>());
            mod.ArmorAddons.Add(repairAddon);
            mod.Outfits.Add(new Outfit(
                repairOutfitKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "RepairRequiredBodyOutfit",
                Items =
                [
                    new FormLink<IOutfitTargetGetter>(repairArmorKey)
                ]
            });
            mod.WriteToBinary(new FilePath(pluginPath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });

            foreach (string stem in new[] { "body", "hands", "feet" })
            {
                foreach (string suffix in new[] { "_d", "_n", "_sk", "_s" })
                {
                    string path = Path.Combine(
                        dataRoot, "textures", "fixture", stem + suffix + ".dds");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path,
                        Encoding.ASCII.GetBytes("DDS " + stem + suffix));
                }
            }

            var plugin = new SkyrimFaceRecordPluginAuthority(
                new PluginName("WholeSkinFixture.esp"),
                new WorkspacePath(pluginPath),
                HashFile(pluginPath));
            var resolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                new FixedAuthorityLoader([plugin]),
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), new KBoundTestPolicy(), LabRoot));
            SkyrimNpcWholeSkinAuthorityResult result = await resolver.ResolveAsync(
                new SkyrimNpcWholeSkinAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot),
                    Reference("WholeSkinFixture.esp", 0x800),
                    NpcSex.Female,
                    [plugin]),
                CancellationToken.None);

            Assert(result.Accepted && result.Authority is
            {
                Route: SkyrimNpcSkinRouteKind.InheritedRace,
                RuntimeAuthority: false,
                Regions.Length: 3
            },
                "Complete inherited whole-skin route was refused: " +
                FormatDiagnostics(result.Diagnostics));
            SkyrimNpcWholeSkinAuthority authority = result.Authority!;
            Assert(authority.Race == Reference("WholeSkinFixture.esp", 0x800) &&
                   authority.SkinArmor.Reference ==
                   Reference("WholeSkinFixture.esp", 0x801) &&
                   authority.Regions.Select(item => item.Region)
                       .SequenceEqual(new[]
                       {
                           SkyrimNpcSkinRegion.Body,
                           SkyrimNpcSkinRegion.Hands,
                           SkyrimNpcSkinRegion.Feet
                       }),
                "Whole-skin record route lost its race, WNAM armor, or region order.");
            Assert(authority.Regions.All(item =>
                       item.TextureAssets.Length == 4 &&
                       item.TextureAssets.All(asset =>
                           asset.ProviderKind == AssetProviderKind.Loose &&
                           asset.ContentLength > 0)),
                "Whole-skin texture paths did not retain four exact loose providers per region.");

            SkyrimNpcWholeSkinAuthorityResult ambiguous =
                await resolver.ResolveAsync(
                    new SkyrimNpcWholeSkinAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRoot),
                        Reference("WholeSkinFixture.esp", 0x800),
                        NpcSex.Female,
                        [plugin])
                    {
                        DefaultOutfit = Reference(
                            "WholeSkinFixture.esp", 0x808)
                    },
                    CancellationToken.None);
            Assert(!ambiguous.Accepted &&
                   ambiguous.Diagnostics.Any(item =>
                       item.Code ==
                       "skyrim-whole-skin-outfit-body-ambiguous"),
                "An outfit with two exposed torso routes was not refused before output.");

            string manifestPath = Path.Combine(root, "whole-skin-authority.json");
            var writer = new RaceMenuNpcWholeSkinAuthorityWriter(
                new KBoundTestPolicy(), LabRoot);
            SkyrimNpcSkinRegionAuthority body = authority.Regions.Single(
                item => item.Region == SkyrimNpcSkinRegion.Body);
            var malformedSnapshot = authority with
            {
                Regions = [],
                ExposedOutfitSkinBinding =
                    new SkyrimNpcExposedOutfitSkinBinding(
                        authority.SkinArmor with
                        {
                            Signature =
                                new RecordSignature("OTFT")
                        },
                        authority.SkinArmor,
                        body.ArmorAddon,
                        body.TextureSet!,
                        0x04)
            };
            RaceMenuNpcWholeSkinAuthorityWriteResult malformedWrite =
                await writer.WriteAsync(
                    new RaceMenuNpcWholeSkinAuthorityWriteRequest(
                        malformedSnapshot,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        new WorkspacePath(Path.Combine(
                            root,
                            "malformed-whole-skin-authority.json"))),
                    CancellationToken.None);
            Assert(!malformedWrite.Written &&
                   malformedWrite.Diagnostics.Any(item =>
                       item.Code is
                           "racemenu-whole-skin-authority-snapshot" or
                           "racemenu-whole-skin-authority-outfit-binding"),
                "A malformed whole-skin binding threw or wrote instead of returning a typed refusal.");
            RaceMenuNpcWholeSkinAuthorityWriteResult written =
                await writer.WriteAsync(
                    new RaceMenuNpcWholeSkinAuthorityWriteRequest(
                        authority,
                        new WorkspacePath(dataRoot),
                        [plugin],
                        new WorkspacePath(manifestPath)),
                    CancellationToken.None);
            Assert(written.Written && written.Artifact is not null,
                "Whole-skin authority manifest was not written and reopened: " +
                FormatDiagnostics(written.Diagnostics));

            var reader = new RaceMenuNpcWholeSkinAuthorityReader(
                resolver, new KBoundTestPolicy(), LabRoot);
            RaceMenuNpcWholeSkinAuthorityReadResult reopened =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact!.Authority,
                        Reference("WholeSkinFixture.esp", 0x800),
                        NpcSex.Female),
                    CancellationToken.None);
            Assert(reopened.Accepted && reopened.Snapshot is
            {
                Route: SkyrimNpcSkinRouteKind.InheritedRace,
                Race.Plugin.Value: "WholeSkinFixture.esp",
                Race.FormId.Value: 0x800,
                Regions.Length: 3,
                RuntimeAuthority: false
            } reopenedSnapshot &&
                   reopenedSnapshot.Regions
                       .SelectMany(item => item.TextureAssets)
                       .Select(item => item.ContentSha256)
                       .SequenceEqual(authority.Regions
                           .SelectMany(item => item.TextureAssets)
                           .Select(item => item.ContentSha256)),
                "Whole-skin authority did not survive semantic readback: " +
                FormatDiagnostics(reopened.Diagnostics));

            RaceMenuNpcWholeSkinAuthorityReadResult wrongOutfit =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact.Authority,
                        Reference("WholeSkinFixture.esp", 0x800),
                        NpcSex.Female,
                        Reference("WholeSkinFixture.esp", 0x80C)),
                    CancellationToken.None);
            Assert(!wrongOutfit.Accepted &&
                   wrongOutfit.Diagnostics.Any(item =>
                       item.Code == "racemenu-whole-skin-authority-stale"),
                "A whole-skin authority with no outfit binding was accepted for a build whose selected outfit requires a private torso repair.");

            RaceMenuNpcWholeSkinAuthorityReadResult wrongTarget =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact!.Authority,
                        Reference("WholeSkinFixture.esp", 0x809),
                        NpcSex.Female),
                    CancellationToken.None);
            Assert(!wrongTarget.Accepted && wrongTarget.Diagnostics.Any(item =>
                       item.Code == "racemenu-whole-skin-authority-target"),
                "A whole-skin authority was accepted for a different NPC race.");

            string missingTexture = Path.Combine(
                dataRoot, "textures", "fixture", "feet_s.dds");
            byte[] missingTextureBytes = File.ReadAllBytes(missingTexture);
            File.Delete(missingTexture);
            RaceMenuNpcWholeSkinAuthorityReadResult incomplete =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact.Authority,
                        Reference("WholeSkinFixture.esp", 0x800),
                        NpcSex.Female),
                    CancellationToken.None);
            Assert(!incomplete.Accepted && incomplete.Diagnostics.Any(item =>
                       item.Code == "skyrim-asset-authority-missing"),
                "A whole-skin authority with an incomplete feet material was accepted.");
            File.WriteAllBytes(missingTexture, missingTextureBytes);

            string changedTexture = Path.Combine(
                dataRoot, "textures", "fixture", "body_d.dds");
            File.WriteAllBytes(changedTexture, Encoding.ASCII.GetBytes("DDS changed"));
            RaceMenuNpcWholeSkinAuthorityReadResult stale =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact.Authority,
                        Reference("WholeSkinFixture.esp", 0x800),
                        NpcSex.Female),
                    CancellationToken.None);
            Assert(!stale.Accepted && stale.Diagnostics.Any(item =>
                       item.Code == "racemenu-whole-skin-authority-stale"),
                "Changed whole-skin texture bytes did not invalidate the authority.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestRealSophiaWholeSkinAuthority()
    {
        var sourceData = new WorkspacePath(Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "01-source-copies",
            "sophia-live-closure-20260723",
            "Data"));
        string fixtureRoot = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests",
            "real-sophia-whole-skin-" + Guid.NewGuid().ToString("N"));
        var dataRoot = new WorkspacePath(Path.Combine(fixtureRoot, "Data"));
        Directory.CreateDirectory(dataRoot.Value);
        ImmutableArray<SkyrimFaceRecordPluginAuthority> pluginOrder =
        [
            LocalAuthority(sourceData, "Skyrim.esm"),
            LocalAuthority(sourceData, "Update.esm"),
            LocalAuthority(sourceData, "Dawnguard.esm"),
            LocalAuthority(sourceData, "RaceCompatibility.esm"),
            LocalAuthority(sourceData, "COR_AllRace.esp")
        ];
        try
        {
            CopyDirectory(
                Path.Combine(sourceData.Value, "textures", "!COR", "Body"),
                Path.Combine(dataRoot.Value, "textures", "!COR", "Body"));
            CopyDirectory(
                Path.Combine(sourceData.Value, "textures", "!COR", "Hands"),
                Path.Combine(dataRoot.Value, "textures", "!COR", "Hands"));
            var policy = new KOnlyWorkspacePolicy(
                LabRoot, new WorkspacePath(@"F:\ExampleGame"));
            var resolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                new FixedAuthorityLoader(pluginOrder),
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, LabRoot));
            SkyrimNpcWholeSkinAuthorityResult result = await resolver.ResolveAsync(
                new SkyrimNpcWholeSkinAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot,
                    Reference("COR_AllRace.esp", 0x5A184),
                    NpcSex.Female,
                    pluginOrder)
                {
                    DefaultOutfit = Reference("Skyrim.esm", 0x1DC10)
                },
                CancellationToken.None);

            Assert(result.Accepted && result.Authority is
            {
                Route: SkyrimNpcSkinRouteKind.InheritedRace,
                RuntimeAuthority: false,
                SkinArmor.Reference.Plugin.Value: "COR_AllRace.esp",
                SkinArmor.Reference.FormId.Value: 0x144E7,
                Regions.Length: 3
            },
                "The copied Sophia COR inherited route was not resolved exactly: " +
                FormatDiagnostics(result.Diagnostics));
            SkyrimNpcWholeSkinAuthority authority = result.Authority!;
            Assert(authority.Regions.Single(item =>
                           item.Region == SkyrimNpcSkinRegion.Body)
                       .ArmorAddon.Reference.FormId.Value == 0x11D4A &&
                   authority.Regions.Single(item =>
                           item.Region == SkyrimNpcSkinRegion.Hands)
                       .ArmorAddon.Reference.FormId.Value == 0x144E6 &&
                   authority.Regions.Single(item =>
                           item.Region == SkyrimNpcSkinRegion.Feet)
                       .ArmorAddon.Reference.FormId.Value == 0x11D4B,
                "The copied Sophia COR body, hand, or foot ARMA identity drifted.");
            Assert(authority.Regions.All(item =>
                           item.TextureAssets.Length == 4 &&
                           item.TextureAssets.All(asset =>
                               asset.ProviderKind == AssetProviderKind.Loose &&
                               asset.ContentLength > 0)) &&
                   authority.Regions.Single(item =>
                           item.Region == SkyrimNpcSkinRegion.Body)
                       .TextureAssets.Single(asset =>
                           asset.AssetPath == new AssetPath(
                               "textures/!COR/Body/femalebody_1.dds"))
                       .ContentSha256 == new Sha256Hash(
                           "D365F25D7BA077E8AB0DC6753658C3DD4C18BB0456C374043E328147590A1AE4") &&
                   authority.Regions.Single(item =>
                           item.Region == SkyrimNpcSkinRegion.Hands)
                       .TextureAssets.Single(asset =>
                           asset.AssetPath == new AssetPath(
                               "textures/!COR/Hands/femalehands_1.dds"))
                       .ContentSha256 == new Sha256Hash(
                           "DCA7D88554C72BB7D0B0DE4A08D9B4A4132DE24DC26FB414656DC81EE344788B"),
                "The copied Sophia COR material providers no longer match the runtime post-mortem.");
            Assert(authority.ExposedOutfitSkinBinding is
                   {
                       Outfit.Reference.Plugin.Value: "Skyrim.esm",
                       Outfit.Reference.FormId.Value: 0x1DC10,
                       Armor.Reference.Plugin.Value: "Skyrim.esm",
                       Armor.Reference.FormId.Value: 0x1BE1A,
                       ArmorAddon.Reference.Plugin.Value: "Skyrim.esm",
                       ArmorAddon.Reference.FormId.Value: 0x1BE18,
                       TargetFemaleSkinTextureSet.Reference.Plugin.Value: "COR_AllRace.esp",
                       TargetFemaleSkinTextureSet.Reference.FormId.Value: 0xF5AB,
                       ExposedSlotMask: 0x04
                   },
                "The copied Sophia farm outfit did not retain the exact exposed-torso repair binding.");

            var writer = new RaceMenuNpcWholeSkinAuthorityWriter(
                policy, LabRoot);
            var manifestPath = new WorkspacePath(Path.Combine(
                fixtureRoot,
                "sophia-whole-skin-authority.json"));
            RaceMenuNpcWholeSkinAuthorityWriteResult written =
                await writer.WriteAsync(
                    new RaceMenuNpcWholeSkinAuthorityWriteRequest(
                        authority,
                        dataRoot,
                        pluginOrder,
                        manifestPath),
                    CancellationToken.None);
            Assert(written.Written &&
                   written.Artifact is not null,
                "The real Sophia schema-v2 whole-skin binding was not persisted: " +
                FormatDiagnostics(written.Diagnostics));
            var reader = new RaceMenuNpcWholeSkinAuthorityReader(
                resolver, policy, LabRoot);
            FormReference sophiaOutfit =
                Reference("Skyrim.esm", 0x1DC10);
            RaceMenuNpcWholeSkinAuthorityReadResult reopened =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact!.Authority,
                        Reference("COR_AllRace.esp", 0x5A184),
                        NpcSex.Female,
                        sophiaOutfit),
                    CancellationToken.None);
            Assert(reopened.Accepted &&
                   reopened.Snapshot?.ExposedOutfitSkinBinding is not null,
                "The real Sophia schema-v2 outfit binding did not survive exact selected-outfit readback: " +
                FormatDiagnostics(reopened.Diagnostics));

            RaceMenuNpcWholeSkinAuthorityReadResult nullOutfit =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        written.Artifact.Authority,
                        Reference("COR_AllRace.esp", 0x5A184),
                        NpcSex.Female,
                        null),
                    CancellationToken.None);
            Assert(!nullOutfit.Accepted &&
                   nullOutfit.Diagnostics.Any(item =>
                       item.Code ==
                       "racemenu-whole-skin-authority-stale"),
                "The Sophia outfit-bound manifest was accepted for a build with a different selected outfit.");

            JsonObject legacy = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath.Value))!
                .AsObject();
            legacy["schemaVersion"] = 1;
            legacy.Remove("exposedOutfitSkinBinding");
            var legacyPath = new WorkspacePath(Path.Combine(
                fixtureRoot,
                "sophia-whole-skin-authority-schema1.json"));
            await File.WriteAllTextAsync(
                legacyPath.Value,
                legacy.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            var legacyAuthority =
                new RaceMenuNpcWholeSkinAuthority(
                    legacyPath,
                    HashFile(legacyPath.Value));
            RaceMenuNpcWholeSkinAuthorityReadResult legacyResult =
                await reader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        legacyAuthority,
                        Reference("COR_AllRace.esp", 0x5A184),
                        NpcSex.Female,
                        sophiaOutfit),
                    CancellationToken.None);
            Assert(!legacyResult.Accepted &&
                   legacyResult.Diagnostics.Any(item =>
                       item.Code ==
                       "racemenu-whole-skin-authority-stale"),
                "A legacy whole-skin manifest bypassed Sophia's required exposed-outfit repair.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static SkyrimFaceRecordPluginAuthority LocalAuthority(
        WorkspacePath dataRoot,
        string plugin)
    {
        var path = new WorkspacePath(Path.Combine(dataRoot.Value, plugin));
        return new SkyrimFaceRecordPluginAuthority(
            new PluginName(plugin), path, HashFile(path.Value));
    }

    private static ArmorAddon BuildSkinAddon(
        FormKey addon,
        FormKey race,
        FormKey textureSet,
        uint slotMask,
        string editorId) => new(addon, SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)slotMask
            },
            Race = new FormLinkNullable<IRaceGetter>(race),
            SkinTexture = new GenderedItem<
            IFormLinkNullableGetter<ITextureSetGetter>>(
            new FormLinkNullable<ITextureSetGetter>(textureSet),
            new FormLinkNullable<ITextureSetGetter>(textureSet))
        };

    private static TextureSet BuildSkinTextureSet(
        FormKey key,
        string stem) => new(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = Path.GetFileName(stem) + "Skin",
            Diffuse = stem + "_d.dds",
            NormalOrGloss = stem + "_n.dds",
            GlowOrDetailMap = stem + "_sk.dds",
            BacklightMaskOrSpecular = stem + "_s.dds"
        };

    private static async Task TestRaceTintAuthority()
    {
        SkyrimFaceRecordPluginAuthority skyrim = Authority("Skyrim.esm",
            "projects/Emi2FreshBuild/01-source-copies/dependency-providers/official-masters/Skyrim.esm",
            "06A9881F6AB277AFD2A82E71F8A3719183B7031DB4D9C78A7F40C08E1E4FFA91");
        var reader = new BethesdaSkyrimRaceTintAuthorityReader(
            new FixedAuthorityLoader([skyrim]));
        var request = new SkyrimRaceTintAuthorityRequest(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(Path.GetDirectoryName(skyrim.Path.Value) ??
                throw new InvalidOperationException("Skyrim provider has no copied Data root.")),
            Reference("Skyrim.esm", 0x013746),
            NpcSex.Female,
            [skyrim]);

        SkyrimRaceTintAuthorityResult result = await reader.ReadAsync(
            request, CancellationToken.None);
        Assert(result.Accepted && result.Authority is not null,
            $"Nord RACE tint authority was refused: {FormatDiagnostics(result.Diagnostics)}");
        SkyrimRaceTintAuthority authority = result.Authority ??
            throw new InvalidOperationException("Accepted RACE tint authority is null.");
        Assert(authority.Race == request.Race && authority.Sex == NpcSex.Female &&
               authority.Provider.Plugin == skyrim.Plugin &&
               authority.Provider.Sha256 == skyrim.ExpectedSha256 &&
               !authority.RuntimeAuthority,
            "RACE tint provider identity, target sex, or runtime limitation drifted.");
        Assert(authority.Layers.Length > 0 &&
               authority.Layers.Select(item => item.Index).Distinct().Count() ==
               authority.Layers.Length &&
               authority.Layers.Select(item => item.RaceOrder)
                   .SequenceEqual(Enumerable.Range(0, authority.Layers.Length)),
            "RACE tint layers did not preserve unique TINI values in engine order.");
        Assert(authority.Layers.Count(item =>
                   item.Kind == SkyrimRaceTintMaskKind.SkinTone) == 1,
            "Nord female tint authority did not expose exactly one typed skin layer.");
        Assert(authority.Layers.All(item =>
                   item.MaskPath.Value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) &&
                   item.MaskPath.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)),
            "A winning RACE tint mask was not normalized to textures/*.dds.");

        var presetPath = new WorkspacePath(Path.Combine(
            LabRoot.Value,
            "projects",
            "Emi2FreshBuild",
            "01-source-copies",
            "fresh-export-drop",
            "emi2-neutral.jslot"));
        PresetParseResult parsed = await new PresetService(
                new KBoundTestPolicy(), LabRoot)
            .InspectAsync(new PresetParseRequest(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    presetPath),
                CancellationToken.None);
        Assert(parsed.Document is not null && parsed.Document.IsValid,
            $"Real Emi2 preset did not parse: {FormatDiagnostics(parsed.Diagnostics)}");
        RaceMenuPresetTintAuthorityPlanResult mapped =
            new RaceMenuPresetTintAuthorityMapper().Map(
                parsed.Document ?? throw new InvalidOperationException("Parsed preset is null."),
                authority);
        Assert(mapped.Accepted && mapped.Plan is not null,
            $"Real Emi2 tint mapping was refused: {FormatDiagnostics(mapped.Diagnostics)}");
        RaceMenuPresetTintAuthorityPlan tintPlan = mapped.Plan ??
            throw new InvalidOperationException("Accepted tint plan is null.");
        Assert(tintPlan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.MappedRecord) == 9 &&
               tintPlan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.Baked) == 6 &&
               tintPlan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.Inactive) == 18,
            "Real Emi2 tints did not reproduce the accepted 9 mapped / 6 baked / 18 inactive split.");
        Assert(tintPlan.Qnam.SourceJslotTintIndex == 0 &&
               Math.Abs(tintPlan.Qnam.Red - 143F / 255F) < 0.000001F &&
               Math.Abs(tintPlan.Qnam.Green - 114F / 255F) < 0.000001F &&
               Math.Abs(tintPlan.Qnam.Blue - 101F / 255F) < 0.000001F,
            "QNAM was not derived from the one mapped skin tint's RGB channels.");

        PresetIdentifier headTexture = PresetIdentifier.Parse(
            parsed.Document.Appearance.RaceMenu?.HeadTexture ?? string.Empty);
        Assert(headTexture.Plugin is not null && headTexture.FormId is not null,
            "Real Emi2 preset has no portable head-texture TXST reference.");
        var textureReader = new BethesdaSkyrimFaceTextureSetAuthorityReader(
            new FixedAuthorityLoader([skyrim]));
        SkyrimFaceTextureSetAuthorityResult textureResult =
            await textureReader.ReadAsync(
                new SkyrimFaceTextureSetAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.DataRoot,
                    new FormReference(headTexture.Plugin!.Value, headTexture.FormId!.Value),
                    [skyrim]),
                CancellationToken.None);
        Assert(textureResult.Accepted && textureResult.Authority is not null,
            $"Real Emi2 TXST authority was refused: {FormatDiagnostics(textureResult.Diagnostics)}");
        SkyrimFaceTextureSetAuthority textureAuthority = textureResult.Authority ??
            throw new InvalidOperationException("Accepted TXST authority is null.");
        Assert(textureAuthority.Provider.Plugin == skyrim.Plugin &&
               textureAuthority.Provider.Sha256 == skyrim.ExpectedSha256 &&
               !textureAuthority.RuntimeAuthority &&
               textureAuthority.Paths.Diffuse.Value.EndsWith(
                   "femalehead.dds", StringComparison.OrdinalIgnoreCase) &&
               textureAuthority.Paths.NormalOrGloss.Value.EndsWith(
                   "femalehead_msn.dds", StringComparison.OrdinalIgnoreCase),
            "Winning head TXST paths or provider identity did not survive typed readback.");

        ImmutableArray<SkyrimFaceRecordPluginAuthority> fullOrder =
        [
            skyrim,
            Authority("Update.esm",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/official-masters/Update.esm",
                "981DF7CF4C4F16F3918ADB6CB8CB0623D18CEFABDC0330780FBC830F43470B70"),
            Authority("Dawnguard.esm",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/official-masters/Dawnguard.esm",
                "B4915D8518E8EC1493F16C76D4F7476F4CD76C40A4580A76FC27694062C2265B"),
            Authority("High Poly Head.esm",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/High Poly Head.esm",
                "5E2020D348181A518ECD1EACEC3BBFFB7B6DA8DFF0032C23C9A30B0CD51C0113"),
            Authority("Improved Eyes Skyrim.esp",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/Improved Eyes Skyrim.esp",
                "E7E66190E4E2063482A8F92E60FD40C35E31C510ABFEADCC80C8B5688CC51780"),
            Authority("Koralina's Eyebrows.esp",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/Koralina's Eyebrows.esp",
                "B076B6BDFD645E71E98B558685F59D128E94CF2355D923B69273201C0DB47407"),
            Authority("KS Hairdo's.esp",
                "projects/Emi2FreshBuild/01-source-copies/dependency-providers/KS Hairdo's.esp",
                "1A281DD84E90EDA6CDD3356DE9E22456EB01E643E869562AA3C43DDBA2EB20D5"),
            Authority("GoamElvenEars.esp",
                "projects/Emi2FreshBuild/01-source-copies/provider-evidence/GoamElvenEars.esp",
                "957BD8AA4E479EE1E73710E1F0945BB6313F89425374D36BBF23404B08CCA615")
        ];
        var fullLoader = new FixedAuthorityLoader(fullOrder);
        var recordBuilder = new RaceMenuPresetRecordAuthorityBuilder(
            fullLoader,
            new BethesdaSkyrimMajorRecordBindingReader(fullLoader),
            new BethesdaSkyrimRaceTintAuthorityReader(fullLoader),
            new BethesdaSkyrimFaceTextureSetAuthorityReader(fullLoader),
            new RaceMenuPresetTintAuthorityMapper(),
            new BethesdaSkyrimFaceTextureSetMatchResolver(fullLoader));
        var recordTarget = new RaceMenuPresetTarget(
            "real-emi2-record-authority",
            request.Race,
            request.Sex,
            new WorkspacePath(Path.Combine(
                LabRoot.Value, "projects", "Emi2RenderProbe", "03-builds",
                "v0.1-render-probe", "ck-root", "Data")),
            fullOrder);
        RaceMenuPresetRecordAuthorityBuildResult recordResult =
            await recordBuilder.BuildAsync(
                parsed.Document,
                recordTarget,
                CancellationToken.None);
        Assert(recordResult.Accepted && recordResult.Draft is not null,
            $"Real Emi2 record-authority draft was refused: {FormatDiagnostics(recordResult.Diagnostics)}");
        RaceMenuPresetRecordAuthorityDraft draft = recordResult.Draft ??
            throw new InvalidOperationException("Accepted record-authority draft is null.");
        var directAppearance = parsed.Document.Appearance with
        {
            RaceMenu = parsed.Document.Appearance.RaceMenu! with
            {
                HeadTexture = null,
                FaceTextures =
                [
                    new RaceMenuFaceTexture(0,
                        draft.HeadTextureAuthority.Paths.Diffuse.Value),
                    new RaceMenuFaceTexture(1,
                        draft.HeadTextureAuthority.Paths.NormalOrGloss.Value),
                    new RaceMenuFaceTexture(2,
                        draft.HeadTextureAuthority.Paths.GlowOrDetailMap.Value),
                    new RaceMenuFaceTexture(7,
                        draft.HeadTextureAuthority.Paths.BacklightMaskOrSpecular.Value)
                ]
            }
        };
        RaceMenuPresetRecordAuthorityBuildResult directTextureResult =
            await recordBuilder.BuildAsync(
                parsed.Document with { Appearance = directAppearance },
                recordTarget,
                CancellationToken.None);
        Assert(directTextureResult.Accepted &&
               directTextureResult.Draft?.HeadTextureBinding.Reference ==
               draft.HeadTextureBinding.Reference,
            $"A JSlot with direct faceTextures but no headTexture FormID did not derive the same unique TXST authority: {FormatDiagnostics(directTextureResult.Diagnostics)}");
        RaceMenuPresetRecordAuthorityBuildResult explicitDirectTextureResult =
            await recordBuilder.BuildAsync(
                parsed.Document with
                {
                    Appearance = directAppearance with
                    {
                        RaceMenu = directAppearance.RaceMenu! with
                        {
                            HeadTexture = parsed.Document.Appearance.RaceMenu!.HeadTexture
                        }
                    }
                },
                recordTarget,
                CancellationToken.None);
        Assert(explicitDirectTextureResult.Accepted &&
               explicitDirectTextureResult.Draft?.HeadTextureBinding.Reference ==
               draft.HeadTextureBinding.Reference &&
               explicitDirectTextureResult.Diagnostics.Any(item =>
                   item.Code == "racemenu-record-head-texture-direct"),
            $"A JSlot with both an explicit TXST and authoritative direct faceTextures did not use the direct slot paths: {FormatDiagnostics(explicitDirectTextureResult.Diagnostics)}");
        Console.WriteLine("EVIDENCE TXST=" + string.Join('|', new[]
        {
            draft.HeadTextureAuthority.Paths.Diffuse.Value,
            draft.HeadTextureAuthority.Paths.NormalOrGloss.Value,
            draft.HeadTextureAuthority.Paths.GlowOrDetailMap.Value,
            draft.HeadTextureAuthority.Paths.Height.Value,
            draft.HeadTextureAuthority.Paths.BacklightMaskOrSpecular.Value
        }));
        Assert(draft.HeadParts.Length == 7 &&
               draft.HeadParts.Count(item =>
                   item.Binding.HeadPartType == NpcHeadPartType.Face) == 1 &&
               draft.RaceBinding.Reference == Reference("Skyrim.esm", 0x013746) &&
               draft.RaceBinding.ProviderPluginName == new PluginName("Update.esm") &&
               draft.TintPlan.Dispositions.Length == 33 &&
               draft.HeadTextureBinding.Signature == new RecordSignature("TXST") &&
               draft.OutputOwnedHairColorFormId == new FormId(0x801) &&
               !draft.RuntimeAuthority,
            "Real preset record-authority draft lost a source headpart, TXST, tint, hair, or static limitation.");
        RaceMenuJslotCompanionAppearance companionAppearance =
            RaceMenuJslotCompanionAppearanceMapper.Map(
                draft,
                templateNam9Trailing: 0F,
                new PluginName("NPCM_Jslot_Test.esp"),
                new FormId(0x800));
        Assert(companionAppearance.Appearance.OrderedHeadParts.Length ==
               draft.HeadParts.Length &&
               companionAppearance.Appearance.OrderedHeadParts
                   .OfType<OutputOwnedSkyrimNpcFaceHeadPart>()
                   .Single().AllocatedLocalFormId == new FormId(0x803) &&
               companionAppearance.Appearance.HairColor is
                   OutputOwnedSkyrimNpcHairColor
               {
                   AllocatedLocalFormId.Value: 0x801
               } &&
               companionAppearance.Appearance.FaceTextureSet is
                   OutputOwnedSkyrimNpcFaceTextureSet
               {
                   AllocatedLocalFormId.Value: 0x802
               } &&
               companionAppearance.Appearance.FaceMorphs.Nam9Sliders.Length == 18 &&
               companionAppearance.Appearance.FaceMorphs.NamaValues.Length == 4 &&
               companionAppearance.Appearance.FaceTints.Layers.Length ==
               draft.TintPlan.Dispositions.Count(item =>
                   item.Kind != RaceMenuPresetTintAuthorityDispositionKind.Inactive) &&
               companionAppearance.SidecarOverlay.OriginatingPlugin ==
               new PluginName("NPCM_Jslot_Test.esp") &&
               companionAppearance.SidecarOverlay.FormId == new FormId(0x800) &&
               companionAppearance.SidecarOverlay.CustomMorphs.SequenceEqual(
                   parsed.Document.Appearance.OrderedCustomMorphs) &&
               companionAppearance.SidecarOverlay.TintTextureOverrides.Length ==
               draft.TintPlan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.Baked),
            "JSlot companion mapping did not preserve the real preset's complete native NPC and FaceGen sidecar authority.");
        RaceMenuPresetHeadPartAuthority ears = draft.HeadParts.Single(item =>
            string.Equals(item.Source.Identifier.Plugin?.Value,
                "GoamElvenEars.esp", StringComparison.OrdinalIgnoreCase));
        Assert(ears.Source.Identifier.FormId == new FormId(0x825D61) &&
               ears.Binding.Reference.FormId == new FormId(0x0D61),
            "ESPFE source FormID did not normalize from captured 0x825D61 to provider-local 0x0D61.");

        string writeRoot = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.BethesdaFaceRouting.Tests",
            "obj",
            "record-authority-writer");
        if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true);
        Directory.CreateDirectory(writeRoot);
        try
        {
            var destination = new WorkspacePath(Path.Combine(
                writeRoot, "record-authority.json"));
            var writer = new RaceMenuPresetRecordAuthorityWriter(
                new KBoundTestPolicy(), LabRoot);
            RaceMenuPresetRecordAuthorityWriteResult written =
                await writer.WriteAsync(
                    new RaceMenuPresetRecordAuthorityWriteRequest(draft, destination),
                    CancellationToken.None);
            Assert(written.Written && written.Artifact is not null &&
                   File.Exists(destination.Value) &&
                   written.Artifact.ManifestSha256 ==
                   new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                       await File.ReadAllBytesAsync(destination.Value)))),
                $"Record authority was not written and hash-reopened: {FormatDiagnostics(written.Diagnostics)}");
            RaceMenuPresetRecordAuthorityArtifact recordArtifact = written.Artifact ??
                throw new InvalidOperationException("Written record authority artifact is null.");
            using JsonDocument recordDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(destination.Value));
            JsonElement root = recordDocument.RootElement;
            Assert(root.GetProperty("schemaVersion").GetInt32() == 2 &&
                   root.GetProperty("formBindings").GetArrayLength() == 8 &&
                   root.GetProperty("headPartDispositions").GetArrayLength() == 7 &&
                   root.GetProperty("tintMappings").GetArrayLength() == 33 &&
                   root.GetProperty("qnam").GetProperty("jslotIndex").GetInt32() == 0 &&
                   root.GetProperty("race").GetProperty("providerPluginName").GetString() ==
                   "Update.esm",
                "Serialized authority lost a record, disposition, tint, or QNAM source.");

            var faceGeom = new WorkspacePath(Path.Combine(
                LabRoot.Value, "projects", "Emi2FreshBuild", "01-source-copies",
                "fresh-export-drop", "emi2-neutral.nif"));
            var faceTint = new WorkspacePath(Path.Combine(
                LabRoot.Value, "projects", "Emi2FreshBuild", "01-source-copies",
                "fresh-export-drop", "emi2-neutral.dds"));
            var companion = new RaceMenuPresetCompanionExport(
                presetPath,
                parsed.Document.SourceHash,
                faceGeom,
                HashFile(faceGeom.Value),
                faceTint,
                HashFile(faceTint.Value));
            var providerContext = new BlankNpcProviderBindingRequest(
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects",
                    "NpcManagerReimplementation", "01-source-copies", "m5-fixtures",
                    "gate1-blank-provider-bundle.json")),
                new Sha256Hash("EACB7112F3D0177F1C8C1B14604B3F36F413185C52973BC6027059EAE1D7D06E"),
                GameEdition.SkyrimSpecialEdition,
                NpcSex.Female,
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects", "Emi2FreshBuild",
                    "03-builds", "feasibility-probes", "ck-carrier-root", "Data",
                    "EmiCarrierProbe.esp")),
                new Sha256Hash("421C3A902A87F343D50F8791CC4B9CAFB2B71BF3B7D94F7C2F7BE2DB6535BBF0"),
                new FormId(0x800),
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects", "Emi2FreshBuild",
                    "03-builds", "feasibility-probes", "ck-carrier-root", "Data", "meshes",
                    "Actors", "Character", "FaceGenData", "FaceGeom", "EmiCarrierProbe.esp",
                    "00000800.NIF")),
                new Sha256Hash("4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9"),
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects",
                    "NpcManagerReimplementation", "01-source-copies", "m5-fixtures",
                    "gate1-qualified-facetint.json")),
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects", "Emi2FreshBuild",
                    "03-builds", "feasibility-probes", "ck-carrier-root", "Data")),
                new WorkspacePath(Path.Combine(LabRoot.Value, "projects", "Emi2FreshBuild",
                    "02-normalized-resources", "dependency-manifest.json")));
            var bundleWriter = new RaceMenuPresetBundleAuthorityWriter(
                new KBoundTestPolicy(), LabRoot);
            RaceMenuPresetBundleAuthorityWriteResult mismatchedCompanions =
                await bundleWriter.WriteAsync(
                    new RaceMenuPresetBundleAuthorityWriteRequest(
                        parsed.Document,
                        companion with
                        {
                            FaceTint = faceGeom,
                            FaceTintSha256 = companion.FaceGeomSha256
                        },
                        providerContext,
                        recordArtifact,
                        new WorkspacePath(writeRoot)),
                    CancellationToken.None);
            Assert(!mismatchedCompanions.Written &&
                   mismatchedCompanions.Diagnostics.Any(item =>
                       item.Code == "racemenu-bundle-authority-companions" &&
                       item.Severity == DiagnosticSeverity.Error) &&
                   Directory.GetFileSystemEntries(writeRoot).Length == 1,
                "Bundle writer did not fail closed before writing mismatched companions.");
            RaceMenuPresetRecordAuthorityArtifact forgedRecordIdentity = recordArtifact with
            {
                Authority = recordArtifact.Authority with
                {
                    ExpectedManifestSha256 = new Sha256Hash(new string('A', 64))
                }
            };
            RaceMenuPresetBundleAuthorityWriteResult forgedRecord =
                await bundleWriter.WriteAsync(
                    new RaceMenuPresetBundleAuthorityWriteRequest(
                        parsed.Document,
                        companion,
                        providerContext,
                        forgedRecordIdentity,
                        new WorkspacePath(writeRoot)),
                    CancellationToken.None);
            Assert(!forgedRecord.Written &&
                   forgedRecord.Diagnostics.Any(item =>
                       item.Code == "racemenu-bundle-authority-record" &&
                       item.Severity == DiagnosticSeverity.Error) &&
                   Directory.GetFileSystemEntries(writeRoot).Length == 1,
                "Bundle writer accepted a split record-authority identity.");
            RaceMenuPresetBundleAuthorityWriteResult bundleResult =
                await bundleWriter.WriteAsync(
                    new RaceMenuPresetBundleAuthorityWriteRequest(
                        parsed.Document,
                        companion,
                        providerContext,
                        recordArtifact,
                        new WorkspacePath(writeRoot)),
                    CancellationToken.None);
            Assert(bundleResult.Written && bundleResult.Artifact is not null &&
                   bundleResult.Artifact.CreatedFiles.Length == 4,
                $"Bundle/runtime authority was not written: {FormatDiagnostics(bundleResult.Diagnostics)}");
            RaceMenuPresetBundleAuthorityArtifact bundleArtifact = bundleResult.Artifact ??
                throw new InvalidOperationException("Written bundle authority artifact is null.");
            using JsonDocument bundleDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(
                    bundleArtifact.Bundle.ManifestPath.Value));
            using JsonDocument routesDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(
                    bundleArtifact.RuntimeRoutes.ManifestPath.Value));
            Assert(bundleDocument.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
                   routesDocument.RootElement.GetProperty("routes").GetArrayLength() == 2 &&
                   bundleArtifact.Bundle.ExpectedPresetSha256 ==
                   parsed.Document.SourceHash,
                "Generated bundle lost its preset binding or exact two runtime routes.");

            var policy = new KOnlyWorkspacePolicy(
                LabRoot, new WorkspacePath(@"F:\ExampleGame"));
            var assetIndexer = new BethesdaAssetIndexer();
            var preparedRequestPath = new WorkspacePath(Path.Combine(
                LabRoot.Value, "projects", "NpcManagerReimplementation",
                "01-source-copies", "gate2-emi2", "execution-request-v5.json"));
            RaceMenuNpcExecutionRequestFileLoadResult prepared =
                await new RaceMenuNpcExecutionRequestFileLoader(LabRoot).LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        preparedRequestPath, HashFile(preparedRequestPath.Value)),
                    CancellationToken.None);
            Assert(prepared.Loaded && prepared.Request is not null,
                $"Prepared real request did not reopen: {FormatDiagnostics(prepared.Diagnostics)}");
            var productionAssetReader = new RaceMenuNpcBuildService(
                    null!,
                    null!,
                    null!,
                    null!,
                    assetIndexer,
                    policy,
                    LabRoot);
            RaceMenuNpcStandaloneAuthorityReadResult currentAssets =
                await productionAssetReader.ReadAsync(
                    prepared.Request!.AssetAuthority, CancellationToken.None);
            Assert(currentAssets.Accepted && currentAssets.Assets is not null,
                $"Current request NAM9 authority did not reopen: {FormatDiagnostics(currentAssets.Diagnostics)}");
            RaceMenuNpcBuildRequest candidateBuild = prepared.Request!.Build with
            {
                PresetBundle = bundleArtifact.Bundle,
                OutputRoot = new WorkspacePath(Path.Combine(writeRoot, "future-output"))
            };
            var providerService = new BlankNpcProviderService(
                policy, LabRoot, assetIndexer);
            var productionPlanService = new RaceMenuNpcAppearancePlanService(
                new PresetService(policy, LabRoot), providerService, policy, LabRoot);
            RaceMenuNpcAppearancePlanResult candidatePlan =
                await productionPlanService.AnalyzeAsync(
                    candidateBuild, CancellationToken.None);
            Assert(candidatePlan.Accepted && candidatePlan.Plan is { IsReady: true },
                $"Generated record/runtime/bundle authority did not reopen through the production plan: {FormatDiagnostics(candidatePlan.Diagnostics)}");

            var standaloneDestination = new WorkspacePath(Path.Combine(
                writeRoot, "standalone-assets.json"));
            var standaloneAuthorityWriter =
                new RaceMenuPresetStandaloneAuthorityWriter(
                    new BethesdaSkyrimFaceMorphSnapshotService(policy, LabRoot),
                    new SkyrimAssetAuthorityPlanner(assetIndexer, policy, LabRoot),
                    new InProcessDdsTextureDecoder(LabRoot),
                    policy,
                    LabRoot);
            RaceMenuPresetStandaloneAuthorityWriteResult standalone =
                await standaloneAuthorityWriter.WriteAsync(
                    new RaceMenuPresetStandaloneAuthorityWriteRequest(
                        candidatePlan.Plan!, draft, currentAssets.Assets!.Nam9Authority,
                        currentAssets.Assets.OverlayDecisions,
                        currentAssets.Assets.ExternalTextureAuthorities,
                        standaloneDestination),
                    CancellationToken.None);
            Assert(standalone.Written && standalone.Artifact is
            {
                RuntimeAuthority: false,
                FaceTintWidth: > 0,
                FaceTintHeight: > 0,
                ExternalTextures.Length: 6
            },
                $"Direct CharGen standalone authority was not generated: {FormatDiagnostics(standalone.Diagnostics)}");
            RaceMenuPresetStandaloneAuthorityArtifact standaloneArtifact =
                standalone.Artifact ?? throw new InvalidOperationException(
                    "Written standalone authority artifact is null.");
            var selectedDependencyDestination = new WorkspacePath(Path.Combine(
                writeRoot, "selected-preset-dependencies.json"));
            var faceGeomDependency = new SkyrimAssetAuthority(
                "loose",
                AssetProviderKind.Loose,
                faceGeom,
                companion.FaceGeomSha256,
                new AssetPath("meshes/fixtures/selected-face.nif"),
                new FileInfo(faceGeom.Value).Length,
                companion.FaceGeomSha256);
            RaceMenuNpcExternalTextureAuthority overlappingTexture =
                standaloneArtifact.ExternalTextures[0];
            string caseVariantSource = overlappingTexture.Source.Value.Replace(
                @"K:\ExampleWorkspace",
                @"k:\exampleworkspace",
                StringComparison.Ordinal);
            Assert(!string.Equals(
                       caseVariantSource,
                       overlappingTexture.Source.Value,
                       StringComparison.Ordinal) &&
                   File.Exists(caseVariantSource),
                "The Windows case-variant dependency fixture did not resolve to the same file.");
            var overlappingFaceGenDependency = new SkyrimAssetAuthority(
                overlappingTexture.Provider,
                string.Equals(
                    overlappingTexture.Provider,
                    "loose",
                    StringComparison.OrdinalIgnoreCase)
                    ? AssetProviderKind.Loose
                    : AssetProviderKind.Archive,
                new WorkspacePath(caseVariantSource),
                overlappingTexture.ExpectedSourceSha256,
                overlappingTexture.DataRelativePath,
                1,
                overlappingTexture.ExpectedMemberSha256);
            var selectedDependencyWriter =
                new RaceMenuSelectedDependencyManifestWriter(
                    policy, LabRoot);
            var providerSidecarPath = new WorkspacePath(Path.Combine(
                writeRoot, "dint-provider-sidecar.xml"));
            byte[] providerSidecarBytes = [7, 6, 5, 4];
            await File.WriteAllBytesAsync(
                providerSidecarPath.Value, providerSidecarBytes);
            var providerSidecarHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(providerSidecarBytes)));
            var providerSidecarAuthority = new SkyrimAssetAuthority(
                "loose",
                AssetProviderKind.Loose,
                providerSidecarPath,
                providerSidecarHash,
                new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/wig/16/16.xml"),
                providerSidecarBytes.Length,
                providerSidecarHash);
            RaceMenuSelectedDependencyManifestWriteResult selectedDependencies =
                await selectedDependencyWriter.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        parsed.Document.SourceHash,
                        draft,
                        [faceGeomDependency, overlappingFaceGenDependency],
                        standaloneArtifact.ExternalTextures,
                        selectedDependencyDestination)
                    {
                        ExternalProviderSidecars = [providerSidecarAuthority]
                    },
                    CancellationToken.None);
            Assert(selectedDependencies.Written &&
                   selectedDependencies.Artifact is
                   {
                       RuntimeAuthority: false,
                       HeadPartCount: 7,
                       LooseAssetCount: > 0
                   } &&
                   File.Exists(selectedDependencyDestination.Value),
                $"Selected dependency manifest was not generated and reopened: {FormatDiagnostics(selectedDependencies.Diagnostics)}");
            using JsonDocument selectedDependencyDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(selectedDependencyDestination.Value));
            Assert(selectedDependencyDocument.RootElement
                       .GetProperty("looseAssets")
                       .EnumerateArray()
                       .Any(item => item.GetProperty("gamePath").GetString() ==
                                    "meshes/fixtures/selected-face.nif") &&
                   selectedDependencyDocument.RootElement
                       .GetProperty("headParts").GetArrayLength() == 7,
                "Selected dependency manifest lost its exact model or headpart evidence.");
            JsonElement sidecars = selectedDependencyDocument.RootElement
                .GetProperty("externalProviderSidecars");
            Assert(sidecars.GetArrayLength() == 1 &&
                   sidecars[0].GetProperty("gamePath").GetString() ==
                   "meshes/armor/[dint999]/02 Hair/wig/16/16.xml" &&
                   !selectedDependencyDocument.RootElement
                       .GetProperty("looseAssets")
                       .EnumerateArray()
                       .Any(item => item.GetProperty("gamePath").GetString()!
                           .EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) &&
                   !selectedDependencyDocument.RootElement
                       .GetProperty("archives")
                       .EnumerateArray()
                       .SelectMany(item => item.GetProperty("members")
                           .EnumerateArray())
                       .Any(item => item.GetString()!
                           .EndsWith(".xml", StringComparison.OrdinalIgnoreCase)),
                "Selected dependency manifest did not keep the XML provider sidecar in its separate authority array.");
            int overlappingRows = selectedDependencyDocument.RootElement
                .GetProperty("looseAssets")
                .EnumerateArray()
                .Count(item => string.Equals(
                    item.GetProperty("gamePath").GetString(),
                    overlappingTexture.DataRelativePath.Value,
                    StringComparison.OrdinalIgnoreCase));
            overlappingRows += selectedDependencyDocument.RootElement
                .GetProperty("archives")
                .EnumerateArray()
                .SelectMany(item => item.GetProperty("members").EnumerateArray())
                .Count(item => string.Equals(
                    item.GetString(),
                    overlappingTexture.DataRelativePath.Value,
                    StringComparison.OrdinalIgnoreCase));
            Assert(overlappingRows == 1,
                "Case variants of one exact Windows provider were not deduplicated.");
            RaceMenuNpcStandaloneAuthorityReadResult reopenedStandalone =
                await productionAssetReader.ReadAsync(
                    new RaceMenuNpcStandaloneAssetAuthority(
                        standaloneArtifact.ManifestPath,
                        standaloneArtifact.ManifestSha256),
                    CancellationToken.None);
            Assert(reopenedStandalone.Accepted && reopenedStandalone.Assets is
            {
                SchemaVersion: 6,
                ExternalTextureAuthorities.Length: 6,
                FinalOutputAuthority: null,
                FaceBakeAuthority: null,
                FaceTextureBakeAuthority: null
            },
                $"Production standalone reader refused generated schema 6: {FormatDiagnostics(reopenedStandalone.Diagnostics)}");

            var capturingWholeSkinResolver =
                new CapturingWholeSkinAuthorityResolver(
                    new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                        fullLoader,
                        new SkyrimAssetAuthorityPlanner(
                            assetIndexer, policy, LabRoot)));
            var transaction = new RaceMenuPresetSelectionTransactionService(
                recordBuilder,
                writer,
                bundleWriter,
                productionPlanService,
                productionAssetReader,
                standaloneAuthorityWriter,
                selectedDependencyWriter,
                capturingWholeSkinResolver,
                new RaceMenuNpcWholeSkinAuthorityWriter(policy, LabRoot),
                policy,
                LabRoot);
            RaceMenuNpcExecutionRequest transactionCurrent = prepared.Request! with
            {
                AllowInheritedMeshEmbeddedSkinTextureRoute = true,
                Build = prepared.Request.Build with
                {
                    OutputRoot = new WorkspacePath(Path.Combine(
                        writeRoot, "transaction-future-output")),
                    Stats = prepared.Request.Build.Stats with { Weight = 42F }
                }
            };
            RaceMenuPresetSelectionTransactionResult rebound =
                await transaction.RebindAsync(
                    new RaceMenuPresetSelectionTransactionRequest(
                        transactionCurrent,
                        parsed.Document,
                        companion,
                        recordTarget,
                        ApplyBodySlide: false,
                        new WorkspacePath(writeRoot))
                    {
                        FaceGenDependencyAuthorities = [faceGeomDependency]
                    },
                    CancellationToken.None);
            Assert(rebound.Committed && rebound.CandidateRequest is
            {
                ApplyBodySlide: false,
                Build.PresetBundle.ExpectedPresetSha256: var reboundPreset
            } && reboundPreset == parsed.Document.SourceHash &&
                   rebound.CandidateRoot is { } committedRoot &&
                   Directory.Exists(committedRoot.Value) &&
                   Directory.GetFiles(committedRoot.Value).Length == 8 &&
                   rebound.CandidateRequest.Build.WholeSkinAuthority is not null &&
                   rebound.CandidateRequest.SelectedDependencyManifest is
                   {
                       DependencyId: var selectedDependencyId
                   } &&
                   selectedDependencyId.StartsWith(
                       "selected-preset-dependencies-",
                       StringComparison.Ordinal) &&
                   rebound.CandidateRequest.Build.Stats.Weight ==
                   parsed.Document.Appearance.Weight?.Value &&
                   rebound.CandidateRequest.Build.PluginAuthorities.Length ==
                   fullOrder.Length &&
                   rebound.CandidateRequest.Build.PluginAuthorities
                       .Zip(fullOrder)
                       .All(pair =>
                           pair.First.Plugin == pair.Second.Plugin &&
                           pair.First.PluginPath == pair.Second.Path &&
                           pair.First.ExpectedSha256 == pair.Second.ExpectedSha256) &&
                   capturingWholeSkinResolver.LastRequest?
                       .AllowMeshEmbeddedSkinTextureRoute == true &&
                   rebound.CandidateRequest
                       .AllowInheritedMeshEmbeddedSkinTextureRoute &&
                   transactionCurrent.ApplyBodySlide &&
                   rebound.CandidateRequest.Build.PresetBundle.ManifestPath !=
                   transactionCurrent.Build.PresetBundle.ManifestPath &&
                   rebound.CandidateRequest.AssetAuthority.ManifestPath !=
                   transactionCurrent.AssetAuthority.ManifestPath,
                $"Atomic arbitrary-preset rebind did not commit a complete candidate or changed the current request: {FormatDiagnostics(rebound.Diagnostics)}");

            var emptyData = new WorkspacePath(Path.Combine(writeRoot, "empty-data"));
            Directory.CreateDirectory(emptyData.Value);
            int candidatesBefore = Directory.GetDirectories(
                writeRoot, "racemenu-selection-*", SearchOption.TopDirectoryOnly).Length;
            RaceMenuPresetSelectionTransactionResult rolledBack =
                await transaction.RebindAsync(
                    new RaceMenuPresetSelectionTransactionRequest(
                        transactionCurrent,
                        parsed.Document,
                        companion,
                        recordTarget with { DataRoot = emptyData },
                        ApplyBodySlide: true,
                        new WorkspacePath(writeRoot)),
                    CancellationToken.None);
            int candidatesAfter = Directory.GetDirectories(
                writeRoot, "racemenu-selection-*", SearchOption.TopDirectoryOnly).Length;
            Assert(!rolledBack.Committed && rolledBack.CandidateRequest is null &&
                   rolledBack.CandidateRoot is null &&
                   candidatesAfter == candidatesBefore &&
                   rolledBack.Diagnostics.Any(item =>
                       item.Code == "skyrim-asset-authority-missing" &&
                       item.Severity == DiagnosticSeverity.Error),
                "Failed preset rebind retained a candidate directory or mutated the current request.");

            var qualifiedCarrier = new QualifiedFaceGeomCarrierService(policy, LabRoot);
            var exactFaceTintDecoder = new Bgra8FaceTintTextureDecoder(LabRoot);
            var blankBuild = new BlankNpcBuildService(
                NpcCreationComposition.Create(policy, LabRoot),
                providerService,
                qualifiedCarrier,
                new ExactOnlyFaceTintBuildService(),
                exactFaceTintDecoder,
                new PackageVerifyService(
                    new PackageManifestReader(policy, LabRoot)),
                policy,
                LabRoot);
            var directBuildService = new RaceMenuNpcBuildService(
                productionPlanService,
                new BethesdaSkyrimFaceMorphSnapshotService(policy, LabRoot),
                new BodyGenService(policy, LabRoot),
                blankBuild,
                assetIndexer,
                policy,
                LabRoot,
                qualifiedFaceGeomCarrierService: qualifiedCarrier,
                exactFaceTintEvidenceDecoder: exactFaceTintDecoder,
                directCharGenFaceGeomBuildService:
                    new RaceMenuDirectCharGenFaceGeomBuildService(
                        new RaceMenuCharGenFaceGeomMergeService(policy, LabRoot),
                        policy,
                        LabRoot),
                wholeSkinAuthorityReader:
                    new RaceMenuNpcWholeSkinAuthorityReader(
                        new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                            fullLoader,
                            new SkyrimAssetAuthorityPlanner(
                                assetIndexer, policy, LabRoot)),
                        policy,
                        LabRoot));
            RaceMenuNpcExecutionResult directBuild =
                await directBuildService.ExecuteAsync(
                    rebound.CandidateRequest!, null, CancellationToken.None);
            Assert(directBuild.Completed && directBuild.Plan is
            {
                IsReady: true,
                RuntimeAuthority: false
            } && directBuild.Build is
            {
                Completed: true,
                Artifact.Manifest: { } packageManifest
            } && File.Exists(packageManifest.Value),
                $"The committed direct CharGen candidate did not produce a statically verified NPC package: {FormatDiagnostics(directBuild.Diagnostics)}");
            WorkspacePath directPackageManifest = directBuild.Build!.Artifact!.Manifest;
            using (JsonDocument packageDocument = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(directPackageManifest.Value)))
            {
                JsonElement evidenceArtifact = packageDocument.RootElement
                    .GetProperty("artifacts")
                    .EnumerateArray()
                    .Single(item => string.Equals(
                        item.GetProperty("relativePath").GetString(),
                        "Data/NPCManager/Evidence/FaceGeom/direct-chargen-merge.json",
                        StringComparison.Ordinal));
                string evidenceRelative = evidenceArtifact
                    .GetProperty("relativePath").GetString()!;
                string evidencePath = Path.Combine(
                    Path.GetDirectoryName(directPackageManifest.Value)!,
                    evidenceRelative.Replace('/', Path.DirectorySeparatorChar));
                using JsonDocument evidence = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(evidencePath));
                JsonElement evidenceRoot = evidence.RootElement;
                JsonElement[] shapes = evidenceRoot.GetProperty("shapes")
                    .EnumerateArray().ToArray();
                Assert(evidenceRoot.GetProperty("schemaVersion").GetInt32() == 1 &&
                       evidenceRoot.GetProperty("routedShapeCount").GetInt32() == 7 &&
                       evidenceRoot.GetProperty("changedPositionShapeCount").GetInt32() == 2 &&
                       !evidenceRoot.GetProperty("creationKitAuthority").GetBoolean() &&
                       !evidenceRoot.GetProperty("runtimeAuthority").GetBoolean() &&
                       evidenceRoot.GetProperty("independentVerification")
                           .GetProperty("verified").GetBoolean() &&
                       shapes.Length == 7 &&
                       shapes.Count(item => item.GetProperty("route").GetString() ==
                           "CharGenXyz") == 5 &&
                       shapes.Count(item => item.GetProperty("route").GetString() ==
                           "CarrierPreserved") == 2,
                    "The final package did not retain the exact seven-shape direct CharGen merge evidence.");
            }

            RaceMenuPresetRecordAuthorityWriteResult overwrite =
                await writer.WriteAsync(
                    new RaceMenuPresetRecordAuthorityWriteRequest(draft, destination),
                    CancellationToken.None);
            Assert(!overwrite.Written && overwrite.Artifact is null &&
                   overwrite.Diagnostics.Any(item =>
                       item.Code == "racemenu-record-authority-destination" &&
                       item.Severity == DiagnosticSeverity.Error),
                "Record-authority writer did not refuse an existing destination.");
        }
        finally
        {
            if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true);
        }

        var staleReader = new BethesdaSkyrimRaceTintAuthorityReader(
            new FixedAuthorityLoader(
                [skyrim with { ExpectedSha256 = new Sha256Hash(new string('A', 64)) }]));
        SkyrimRaceTintAuthorityResult stale = await staleReader.ReadAsync(
            request, CancellationToken.None);
        AssertRejected(stale, "skyrim-race-tint-authority-stale");
    }

    private sealed class CapturingWholeSkinAuthorityResolver(
        ISkyrimNpcWholeSkinAuthorityResolver inner)
        : ISkyrimNpcWholeSkinAuthorityResolver
    {
        public SkyrimNpcWholeSkinAuthorityRequest? LastRequest { get; private set; }

        public ValueTask<SkyrimNpcWholeSkinAuthorityResult> ResolveAsync(
            SkyrimNpcWholeSkinAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return inner.ResolveAsync(request, cancellationToken);
        }
    }

    private static async Task TestCapturedCorRaceTintAuthority()
    {
        string dataRoot = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "01-source-copies",
            "sophia-live-closure-20260723",
            "Data");
        var cor = Authority(
            "COR_AllRace.esp",
            "projects/NpcManagerReimplementation/01-source-copies/sophia-live-closure-20260723/Data/COR_AllRace.esp",
            "793F9CA5181C7CA5B5B6C3EB6CF803F273C2D154732EC80BCDEC2AEE2E5CC351");
        var reader = new BethesdaSkyrimRaceTintAuthorityReader(
            new FixedAuthorityLoader([cor]));
        SkyrimRaceTintAuthorityResult result = await reader.ReadAsync(
            new SkyrimRaceTintAuthorityRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(dataRoot),
                Reference("COR_AllRace.esp", 0x0005A184),
                NpcSex.Female,
                [cor]),
            CancellationToken.None);

        Assert(result.Accepted && result.Authority is not null,
            $"Captured COR RACE tint authority was refused: {FormatDiagnostics(result.Diagnostics)}");
        SkyrimRaceTintAuthority authority = result.Authority ??
            throw new InvalidOperationException("Accepted COR RACE tint authority is null.");
        Assert(authority.Layers.Length == 108 &&
               authority.Layers.Select(item => item.RaceOrder)
                   .SequenceEqual(Enumerable.Range(0, 108)),
            "Captured COR female RACE did not preserve all 108 tint rows in engine order.");
        Assert(authority.Layers.GroupBy(item => item.Index)
                   .Count(group => group.Count() == 2) == 25 &&
               authority.Layers.GroupBy(item => item.Index)
                   .All(group => group.Count() is 1 or 2),
            "Captured COR female RACE duplicate TINI surface drifted from the reviewed provider.");

        var presetPath = new WorkspacePath(Path.Combine(
            LabRoot.Value,
            "Resources",
            "Sophia Loren-112955-1-00-1709402972",
            "Data",
            "SKSE",
            "Plugins",
            "CharGen",
            "Presets",
            "Sophia Loren COR.jslot"));
        PresetParseResult parsed = await new PresetService(
                new KBoundTestPolicy(), LabRoot)
            .InspectAsync(new PresetParseRequest(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    presetPath),
                CancellationToken.None);
        Assert(parsed.Document is not null && parsed.Document.IsValid,
            $"Sophia preset did not parse: {FormatDiagnostics(parsed.Diagnostics)}");
        RaceMenuPresetTintAuthorityPlanResult mapped =
            new RaceMenuPresetTintAuthorityMapper().Map(
                parsed.Document ??
                throw new InvalidOperationException("Parsed Sophia preset is null."),
                authority);
        Assert(mapped.Accepted && mapped.Plan is not null,
            $"Sophia tint mapping was refused: {FormatDiagnostics(mapped.Diagnostics)}");
        RaceMenuPresetTintAuthorityPlan plan = mapped.Plan ??
            throw new InvalidOperationException("Accepted Sophia tint plan is null.");
        Assert(plan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.MappedRecord) == 4 &&
               plan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.Baked) == 4 &&
               plan.Dispositions.Count(item =>
                   item.Kind == RaceMenuPresetTintAuthorityDispositionKind.Inactive) == 100,
            "Sophia active COR tints did not split into four unambiguous TINI rows and four baked overlays.");
        ushort[] mappedIndexes = plan.Dispositions
            .Where(item => item.Kind == RaceMenuPresetTintAuthorityDispositionKind.MappedRecord)
            .Select(item => item.TiniIndex ??
                            throw new InvalidOperationException("Mapped tint has no TINI index."))
            .Order()
            .ToArray();
        Assert(mappedIndexes.SequenceEqual(new ushort[] { 24, 28, 30, 31 }),
            "Sophia base skin, cheeks, frown, and lip masks did not map to the exact COR TINI indexes.");
    }

    private static async Task TestRealEmi2Route()
    {
        var race = Reference("Skyrim.esm", 0x013746);
        var face = Reference("High Poly Head.esm", 0x000A06);
        var eyes = Reference("Improved Eyes Skyrim.esp", 0x002889);
        var brows = Reference("Koralina's Eyebrows.esp", 0x000801);
        var hair = Reference("KS Hairdo's.esp", 0x0A9555);
        var request = new SkyrimFaceRecordRouteRequest(
            GameEdition.SkyrimSpecialEdition,
            race,
            NpcSex.Female,
            [
                new(face, [SkyrimHdptTriRole.RaceMorph, SkyrimHdptTriRole.Mesh, SkyrimHdptTriRole.CharGen]),
                new(eyes, []),
                new(brows, []),
                new(hair, [])
            ],
            [
                Authority("Skyrim.esm",
                    "projects/Emi2FreshBuild/01-source-copies/dependency-providers/official-masters/Skyrim.esm",
                    "06A9881F6AB277AFD2A82E71F8A3719183B7031DB4D9C78A7F40C08E1E4FFA91"),
                Authority("High Poly Head.esm",
                    "projects/Emi2FreshBuild/01-source-copies/dependency-providers/High Poly Head.esm",
                    "5E2020D348181A518ECD1EACEC3BBFFB7B6DA8DFF0032C23C9A30B0CD51C0113"),
                Authority("Improved Eyes Skyrim.esp",
                    "projects/Emi2FreshBuild/01-source-copies/dependency-providers/Improved Eyes Skyrim.esp",
                    "E7E66190E4E2063482A8F92E60FD40C35E31C510ABFEADCC80C8B5688CC51780"),
                Authority("Koralina's Eyebrows.esp",
                    "projects/Emi2FreshBuild/01-source-copies/dependency-providers/Koralina's Eyebrows.esp",
                    "B076B6BDFD645E71E98B558685F59D128E94CF2355D923B69273201C0DB47407"),
                Authority("KS Hairdo's.esp",
                    "projects/Emi2FreshBuild/01-source-copies/dependency-providers/KS Hairdo's.esp",
                    "1A281DD84E90EDA6CDD3356DE9E22456EB01E643E869562AA3C43DDBA2EB20D5")
            ]);

        var result = await Service().ResolveAsync(request, CancellationToken.None);
        Assert(result.Accepted && result.Route is not null,
            $"Real copied providers were refused: {FormatDiagnostics(result.Diagnostics)}");
        Assert(!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
            $"Accepted route contains errors: {FormatDiagnostics(result.Diagnostics)}");

        var route = result.Route ?? throw new InvalidOperationException("Accepted real route is null.");
        Assert(route.Race.Reference == race && route.Race.EditorId == "NordRace",
            "Nord RACE identity did not survive typed routing.");
        Assert(route.Race.SelectedGenderDefaultHeadParts.Length > 0,
            "Nord female default HDPT list was not exposed.");

        AssertSelected(route, face, NpcHeadPartType.Face);
        AssertSelected(route, eyes, NpcHeadPartType.Eyes);
        AssertSelected(route, brows, NpcHeadPartType.Eyebrows);
        AssertSelected(route, hair, NpcHeadPartType.Hair);

        var routedFace = FindPart(route, face);
        Assert(string.Equals(routedFace.ModelNif.Value,
                "meshes/KL/High Poly Head/FemaleHead.nif", StringComparison.OrdinalIgnoreCase),
            $"Unexpected HPH face model route '{routedFace.ModelNif}'.");
        AssertTri(routedFace, SkyrimHdptTriRole.RaceMorph,
            "meshes/KL/High Poly Head/FemaleHeadRaces.tri");
        AssertTri(routedFace, SkyrimHdptTriRole.Mesh,
            "meshes/KL/High Poly Head/FemaleHead.tri");
        AssertTri(routedFace, SkyrimHdptTriRole.CharGen,
            "meshes/KL/High Poly Head/FemaleHeadCharGen.tri");

        var mouthReference = Reference("Skyrim.esm", 0x05150F);
        var mouth = route.HeadParts.FirstOrDefault(item => item.Reference == mouthReference);
        Assert(mouth is not null && mouth.IsRaceDefault &&
               (mouth.EditorId.Contains("mouth", StringComparison.OrdinalIgnoreCase) ||
                mouth.ModelNif.Value.Contains("mouth", StringComparison.OrdinalIgnoreCase)),
            "The RACE-default mouth HDPT route was not retained. Routed parts: " +
            string.Join(", ", route.HeadParts.Select(item =>
                $"{item.EffectiveType}:{item.EditorId}:{item.Reference}:default={item.IsRaceDefault}")));
        Assert(route.HeadParts.Any(item => item.Parent == hair &&
                                          item.EffectiveType == NpcHeadPartType.Hair),
            "KS Hairdo's HNAM extras were not recursively routed as hair.");

        foreach (var selected in new[] { eyes, brows, hair })
        {
            var part = FindPart(route, selected);
            Assert(part.ModelNif.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
                   part.ModelNif.Value.Contains('/'),
                $"{selected} was reduced to a basename instead of its record-declared path.");
        }

        Console.WriteLine(
            $"EVIDENCE real-route race={route.Race.EditorId} roots={route.RootHeadParts.Length} " +
            $"expanded={route.HeadParts.Length} faceRoles={routedFace.TriRoutes.Length} mouth={mouth!.EditorId}");
        foreach (var part in route.HeadParts)
        {
            Console.WriteLine($"EVIDENCE HDPT={part.Reference} type={part.EffectiveType} " +
                $"selected={part.IsSelected} default={part.IsRaceDefault} parent={part.Parent?.ToString() ?? "none"} " +
                $"model={part.ModelNif}");
        }
    }

    private static Task TestLightPluginCapturedHdptRouting()
    {
        string fixtureBase = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests");
        return TestLightPluginCapturedHdptRouting(
            LabRoot,
            fixtureBase,
            Path.Combine(fixtureBase,
                "light-formid-routing-" + Guid.NewGuid().ToString("N")));
    }

    private static async Task TestLightPluginCapturedHdptRouting(
        WorkspacePath workspaceRoot,
        string fixtureBase,
        string root)
    {
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        const string lightPlugin = "LightFaceRoute.esp";
        const string ordinaryPlugin = "OrdinaryFaceRoute.esp";
        string lightPath = Path.Combine(dataRoot, lightPlugin);
        string ordinaryPath = Path.Combine(dataRoot, ordinaryPlugin);
        var lightLocal = Reference(lightPlugin, 0x915);
        var lightCaptured = Reference(lightPlugin, 0x181915);
        var missingLightCaptured = Reference(lightPlugin, 0x181916);
        var ordinaryHeadPart = Reference(ordinaryPlugin, 0xBC05);
        var race = Reference(ordinaryPlugin, 0x800);

        try
        {
            ModKey lightKey = ModKey.FromNameAndExtension(lightPlugin);
            var light = new SkyrimMod(lightKey, SkyrimRelease.SkyrimSE)
            {
                IsSmallMaster = true
            };
            light.HeadParts.Add(new Mutagen.Bethesda.Skyrim.HeadPart(
                new FormKey(lightKey, lightLocal.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "LightFaceRouteHead",
                Name = "Light face route head",
                Flags = Mutagen.Bethesda.Skyrim.HeadPart.Flag.Female,
                Type = Mutagen.Bethesda.Skyrim.HeadPart.TypeEnum.Face,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(
                        "meshes/light-route/face.nif")
                }
            });
            light.WriteToBinary(new FilePath(lightPath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });

            ModKey ordinaryKey = ModKey.FromNameAndExtension(ordinaryPlugin);
            var ordinary = new SkyrimMod(ordinaryKey, SkyrimRelease.SkyrimSE);
            ordinary.Races.Add(new Race(
                new FormKey(ordinaryKey, race.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "LightFaceRouteRace"
            });
            ordinary.HeadParts.Add(new Mutagen.Bethesda.Skyrim.HeadPart(
                new FormKey(ordinaryKey, ordinaryHeadPart.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "OrdinaryFaceRouteHead",
                Name = "Ordinary face route head",
                Flags = Mutagen.Bethesda.Skyrim.HeadPart.Flag.Female,
                Type = Mutagen.Bethesda.Skyrim.HeadPart.TypeEnum.Hair,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(
                        "meshes/ordinary-route/hair.nif")
                }
            });
            ordinary.WriteToBinary(new FilePath(ordinaryPath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });

            SkyrimFaceRecordPluginAuthority lightAuthority =
                LocalAuthority(new WorkspacePath(dataRoot), lightPlugin);
            SkyrimFaceRecordPluginAuthority ordinaryAuthority =
                LocalAuthority(new WorkspacePath(dataRoot), ordinaryPlugin);
            var request = new SkyrimFaceRecordRouteRequest(
                GameEdition.SkyrimSpecialEdition,
                race,
                NpcSex.Female,
                [
                    new(lightCaptured, []),
                    new(ordinaryHeadPart, [])
                ],
                [lightAuthority, ordinaryAuthority]);

            SkyrimFaceRecordRouteResult accepted = await Service(workspaceRoot).ResolveAsync(
                request, CancellationToken.None);
            Assert(accepted.Accepted && accepted.Route is not null,
                $"Captured light-plugin HDPT ID was not normalized: {FormatDiagnostics(accepted.Diagnostics)}");
            SkyrimFaceRecordRoute route = accepted.Route ??
                throw new InvalidOperationException("Accepted light-plugin route is null.");
            Assert(route.RootHeadParts.Contains(lightLocal) &&
                   route.RootHeadParts.Contains(ordinaryHeadPart) &&
                   route.HeadParts.Any(item => item.Reference == lightLocal && item.IsSelected) &&
                   route.HeadParts.Any(item => item.Reference == ordinaryHeadPart && item.IsSelected),
                "Accepted route did not retain both provider-local selected HDPT IDs.");
            SkyrimFaceRecordRouteResult missing = await Service(workspaceRoot).ResolveAsync(
                request with
                {
                    SelectedHeadParts =
                    [
                        new(missingLightCaptured, []),
                        new(ordinaryHeadPart, [])
                    ]
                },
                CancellationToken.None);
            AssertRejected(missing, "skyrim-face-record-hdpt-missing");
            Console.WriteLine(
                "EVIDENCE captured=0x181915 local=0x915 ordinary=0xBC05 " +
                "missing=0x181916 refusal=skyrim-face-record-hdpt-missing");
        }
        finally
        {
            DeleteOwnedFixtureDirectory(fixtureBase, root);
        }
    }

    private static Task TestRaceAndHnamSemantics()
    {
        var race = Reference("Synthetic.esm", 0x100);
        var morphRace = Reference("Synthetic.esm", 0x101);
        var keyword = Reference("Synthetic.esm", 0x200);
        var face = Reference("Synthetic.esm", 0x300);
        var promotedMisc = Reference("Synthetic.esm", 0x301);
        var nestedMisc = Reference("Synthetic.esm", 0x302);
        var logicalEyes = Reference("Synthetic.esm", 0x303);
        var logicalEyeMesh = Reference("Synthetic.esm", 0x304);
        var noScar = Reference("Synthetic.esm", 0x305);
        var provider = SyntheticProvider("Synthetic.esm");
        var catalog = Catalog(
            [
                HeadPart(face, provider, "SyntheticFace", NpcHeadPartType.Face,
                    "actors/character/face.nif", [promotedMisc]),
                HeadPart(promotedMisc, provider, "SyntheticExtra", NpcHeadPartType.Misc,
                    "actors/character/extra.nif", [nestedMisc]),
                HeadPart(nestedMisc, provider, "SyntheticNested", NpcHeadPartType.Misc,
                    "actors/character/nested.nif", []),
                HeadPart(logicalEyes, provider, "SyntheticLogicalEyes",
                    NpcHeadPartType.Eyes, null, [logicalEyeMesh]),
                HeadPart(logicalEyeMesh, provider, "SyntheticEyeMesh",
                    NpcHeadPartType.Misc, "actors/character/eyes.nif", []),
                HeadPart(noScar, provider, "SyntheticNoScar",
                    NpcHeadPartType.Scar, null, [])
            ],
            [
                new(race, provider, "SyntheticRace", morphRace, [keyword], [],
                    [face, promotedMisc], false),
                new(morphRace, provider, "SyntheticMorphRace", null, [], [], [], false)
            ],
            [new(keyword, provider, "SyntheticHeadMorph", false)]);
        var request = Request(race,
            [new(logicalEyes, []), new(noScar, [])], "Synthetic.esm");

        var result = BethesdaSkyrimFaceRecordGraphResolver.Resolve(request, catalog);
        Assert(result.Accepted && result.Route is not null,
            $"Valid synthetic graph was refused: {FormatDiagnostics(result.Diagnostics)}");
        var route = result.Route ?? throw new InvalidOperationException("Accepted synthetic route is null.");
        Assert(route.Race.MorphRaceReference == morphRace &&
               route.Race.MorphRaceEditorId == "SyntheticMorphRace",
            "RACE NAM8 redirect was not exposed as a single typed redirect.");
        Assert(route.Race.Keywords is [{ EditorId: "SyntheticHeadMorph" }],
            "RACE KWDA did not resolve to its exact KYWD EditorID.");

        var promoted = FindPart(route, promotedMisc);
        var nested = FindPart(route, nestedMisc);
        Assert(promoted.DeclaredType == NpcHeadPartType.Misc &&
               promoted.EffectiveType == NpcHeadPartType.Face &&
               promoted.Parent is null && promoted.Depth == 0,
            "Top-level Misc HNAM child did not inherit its parent main type.");
        Assert(nested.DeclaredType == NpcHeadPartType.Misc &&
               nested.EffectiveType == NpcHeadPartType.Face &&
               nested.Parent == promotedMisc && nested.Depth == 1,
            "Recursive Misc HNAM type inheritance or parent/depth provenance is incorrect.");
        var eyeMesh = FindPart(route, logicalEyeMesh);
        Assert(route.RootHeadParts.Contains(logicalEyes) &&
               route.RootHeadParts.Contains(noScar) &&
               route.HeadParts.All(item => item.Reference != logicalEyes) &&
               route.HeadParts.All(item => item.Reference != noScar) &&
               eyeMesh.EffectiveType == NpcHeadPartType.Eyes &&
               eyeMesh.Parent == logicalEyes,
            "Model-less logical or explicit-none HDPT roots were not retained while only real HNAM children supplied geometry.");
        return Task.CompletedTask;
    }

    private static Task TestPresetRaceCompatibility()
    {
        var provider = SyntheticProvider("Synthetic.esm");
        var race = Reference("Synthetic.esm", 0x100);
        var otherRace = Reference("Synthetic.esm", 0x101);
        var defaultFace = Reference("Synthetic.esm", 0x200);
        var openHair = Reference("Synthetic.esm", 0x201);
        var nordBrows = Reference("Synthetic.esm", 0x202);
        var elfEyes = Reference("Synthetic.esm", 0x203);
        var lightLocal = Reference("LightHead.esp", 0x0ABC);
        var lightCaptured = Reference("LightHead.esp", 0x123ABC);
        var nordList = Reference("Synthetic.esm", 0x300);
        var elfList = Reference("Synthetic.esm", 0x301);
        var decodedRace = new SkyrimFaceDecodedRace(
            race, provider, "SyntheticRace", null, [], [], [defaultFace], false);
        var catalog = Catalog(
            [
                HeadPart(defaultFace, provider, "DefaultFace", NpcHeadPartType.Face,
                    "face.nif", [], validRaces: elfList),
                HeadPart(openHair, provider, "OpenHair", NpcHeadPartType.Hair,
                    "hair.nif", []),
                HeadPart(nordBrows, provider, "NordBrows", NpcHeadPartType.Eyebrows,
                    "brows.nif", [], validRaces: nordList),
                HeadPart(elfEyes, provider, "ElfEyes", NpcHeadPartType.Eyes,
                    "eyes.nif", [], validRaces: elfList),
                HeadPart(lightLocal, provider, "LightCaptured", NpcHeadPartType.Misc,
                    "light.nif", [])
            ],
            [decodedRace],
            [],
            [
                new(nordList, provider, [race], false),
                new(elfList, provider, [otherRace], false)
            ]);
        var target = new RaceMenuPresetTarget(
            "synthetic-race-v1", race, NpcSex.Female, LabRoot,
            [SyntheticAuthority("Synthetic.esm")]);
        var snapshot = new BethesdaRaceMenuPresetCompatibilityEvaluator.CompatibilitySnapshot(
            catalog, decodedRace, false);

        RaceMenuPresetCompatibilityResult compatible =
            BethesdaRaceMenuPresetCompatibilityEvaluator.Evaluate(
                Preset(defaultFace, openHair, nordBrows), target, snapshot);
        Assert(compatible.Kind == RaceMenuPresetCompatibilityKind.Compatible,
            $"Compatible head parts were refused: {FormatDiagnostics(compatible.Diagnostics)}");

        RaceMenuPresetCompatibilityResult lightNormalized =
            BethesdaRaceMenuPresetCompatibilityEvaluator.Evaluate(
                Preset(defaultFace, lightCaptured), target, snapshot with
                {
                    LightPlugins = ImmutableHashSet.Create(
                        StringComparer.OrdinalIgnoreCase, "LightHead.esp")
                });
        Assert(lightNormalized.Kind == RaceMenuPresetCompatibilityKind.Compatible,
            $"Captured ESPFE head part did not normalize to its provider-local ID: {FormatDiagnostics(lightNormalized.Diagnostics)}");

        RaceMenuPresetCompatibilityResult incompatible =
            BethesdaRaceMenuPresetCompatibilityEvaluator.Evaluate(
                Preset(elfEyes), target, snapshot);
        Assert(incompatible.Kind == RaceMenuPresetCompatibilityKind.Incompatible &&
               incompatible.Diagnostics.Any(item =>
                   item.Code == "preset-compatibility-race-mismatch"),
            "A statically foreign-race HDPT was not rejected with its exact reason.");

        RaceMenuPresetCompatibilityResult runtimeUnknown =
            BethesdaRaceMenuPresetCompatibilityEvaluator.Evaluate(
                Preset(elfEyes), target, snapshot with
                {
                    HasUnmodeledRuntimeRaceCompatibility = true
                });
        Assert(runtimeUnknown.Kind == RaceMenuPresetCompatibilityKind.Unavailable &&
               runtimeUnknown.Diagnostics.Any(item =>
                   item.Code == "preset-compatibility-runtime-proxy-unmodeled"),
            "An unmodeled runtime RaceCompatibility insertion was guessed as incompatible.");
        return Task.CompletedTask;
    }

    private static async Task TestProviderAuthorityRefusals()
    {
        var hph = Authority("High Poly Head.esm",
            "projects/Emi2FreshBuild/01-source-copies/dependency-providers/High Poly Head.esm",
            new string('a', 64));
        var stale = await Service().ResolveAsync(
            Request(Reference("High Poly Head.esm", 1), [], hph),
            CancellationToken.None);
        AssertRejected(stale, "skyrim-face-record-provider-hash");

        var outside = new SkyrimFaceRecordPluginAuthority(
            new PluginName("kernel32.esm"),
            new WorkspacePath("C:\\Windows\\System32\\kernel32.dll"),
            new Sha256Hash(EmptyHash));
        var escaped = await Service().ResolveAsync(
            Request(Reference("kernel32.esm", 1), [], outside),
            CancellationToken.None);
        AssertRejected(escaped, "skyrim-face-record-provider-outside-workspace");

        var unconstructed = await Service().ResolveAsync(
            new SkyrimFaceRecordRouteRequest(
                GameEdition.SkyrimSpecialEdition,
                Reference("Skyrim.esm", 0x013746),
                NpcSex.Female,
                [],
                [new SkyrimFaceRecordPluginAuthority(default, default, default)]),
            CancellationToken.None);
        AssertRejected(unconstructed, "skyrim-face-record-provider-authority");
    }

    private static Task TestGraphRefusals()
    {
        var provider = SyntheticProvider("Graph.esm");
        var race = Reference("Graph.esm", 0x100);
        var a = Reference("Graph.esm", 0x200);
        var b = Reference("Graph.esm", 0x201);
        var baseRace = new SkyrimFaceDecodedRace(
            race, provider, "GraphRace", null, [], [], [], false);

        var missingRoleCatalog = Catalog(
            [HeadPart(a, provider, "MeshOnly", NpcHeadPartType.Face,
                "face.nif", [], [new((int)SkyrimHdptTriRole.Mesh, "face.tri")])],
            [baseRace], []);
        var missingRole = BethesdaSkyrimFaceRecordGraphResolver.Resolve(
            Request(race, [new(a, [SkyrimHdptTriRole.CharGen])], "Graph.esm"),
            missingRoleCatalog);
        AssertRejected(missingRole, "skyrim-face-record-required-role-missing");

        var cycleCatalog = Catalog(
            [
                HeadPart(a, provider, "CycleA", NpcHeadPartType.Face, "a.nif", [b]),
                HeadPart(b, provider, "CycleB", NpcHeadPartType.Misc, "b.nif", [a])
            ],
            [baseRace], []);
        var cycle = BethesdaSkyrimFaceRecordGraphResolver.Resolve(
            Request(race, [new(a, [])], "Graph.esm"), cycleCatalog);
        AssertRejected(cycle, "skyrim-face-record-hnam-cycle");

        var duplicateCatalog = Catalog(
            [
                HeadPart(a, provider, "DuplicateA", NpcHeadPartType.Face, "a.nif", [b, b]),
                HeadPart(b, provider, "DuplicateB", NpcHeadPartType.Misc, "b.nif", [])
            ],
            [baseRace], []);
        var duplicate = BethesdaSkyrimFaceRecordGraphResolver.Resolve(
            Request(race, [new(a, [])], "Graph.esm"), duplicateCatalog);
        AssertRejected(duplicate, "skyrim-face-record-reference-duplicate");

        var malformedCatalog = Catalog(
            [HeadPart(a, provider, "Malformed", NpcHeadPartType.Face, "../escape.nif", [])],
            [baseRace], []);
        var malformed = BethesdaSkyrimFaceRecordGraphResolver.Resolve(
            Request(race, [new(a, [])], "Graph.esm"), malformedCatalog);
        AssertRejected(malformed, "skyrim-face-record-asset-path");
        return Task.CompletedTask;
    }

    private static async Task TestMalformedPluginRefusal()
    {
        var fixtureDirectory = Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.BethesdaFaceRouting.Tests",
            "obj",
            "malformed-provider");
        Directory.CreateDirectory(fixtureDirectory);
        var path = Path.Combine(fixtureDirectory, "MalformedFaceRoute.esp");
        var bytes = Encoding.ASCII.GetBytes("not-a-bethesda-plugin");
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var authority = new SkyrimFaceRecordPluginAuthority(
                new PluginName("MalformedFaceRoute.esp"),
                new WorkspacePath(path),
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))));
            var result = await Service().ResolveAsync(
                Request(Reference("MalformedFaceRoute.esp", 1), [], authority),
                CancellationToken.None);
            AssertRejected(result, "skyrim-face-record-provider-malformed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BethesdaSkyrimFaceRecordRouteResolver Service() =>
        Service(LabRoot);

    private static BethesdaSkyrimFaceRecordRouteResolver Service(WorkspacePath workspaceRoot) =>
        new(new KBoundTestPolicy(workspaceRoot), workspaceRoot);

    private static SkyrimFaceRecordPluginAuthority Authority(
        string plugin,
        string workspaceRelativePath,
        string hash) =>
        new(new PluginName(plugin),
            new WorkspacePath(Path.Combine(LabRoot.Value,
                workspaceRelativePath.Replace('/', Path.DirectorySeparatorChar))),
            new Sha256Hash(hash));

    private static SkyrimFaceRecordRouteRequest Request(
        FormReference race,
        ImmutableArray<SkyrimFaceRecordHeadPartSelection> selected,
        string plugin)
        => Request(race, selected, SyntheticAuthority(plugin));

    private static SkyrimFaceRecordRouteRequest Request(
        FormReference race,
        ImmutableArray<SkyrimFaceRecordHeadPartSelection> selected,
        SkyrimFaceRecordPluginAuthority authority)
        => new(GameEdition.SkyrimSpecialEdition, race, NpcSex.Female, selected, [authority]);

    private static SkyrimFaceRecordPluginAuthority SyntheticAuthority(string plugin) =>
        new(new PluginName(plugin),
            new WorkspacePath(Path.Combine(LabRoot.Value, "synthetic", plugin)),
            new Sha256Hash(EmptyHash));

    private static SkyrimFaceRecordProvider SyntheticProvider(string plugin)
    {
        var authority = SyntheticAuthority(plugin);
        return new(authority.Plugin, authority.Path, authority.ExpectedSha256);
    }

    private static SkyrimFaceDecodedHeadPart HeadPart(
        FormReference reference,
        SkyrimFaceRecordProvider provider,
        string editorId,
        NpcHeadPartType type,
        string? model,
        ImmutableArray<FormReference> extras,
        ImmutableArray<SkyrimFaceDecodedTriPart> triParts = default,
        FormReference? validRaces = null) =>
        new(
            reference,
            provider,
            editorId,
            Name: null,
            DeclaredType: (int)type,
            IsExtra: false,
            SupportsMale: false,
            SupportsFemale: false,
            ModelPath: model,
            TriParts: triParts.IsDefault ? [] : triParts,
            ValidRaces: validRaces,
            TextureSet: null,
            ExtraParts: extras,
            IsDeleted: false);

    private static SkyrimFaceRecordCatalog Catalog(
        ImmutableArray<SkyrimFaceDecodedHeadPart> headParts,
        ImmutableArray<SkyrimFaceDecodedRace> races,
        ImmutableArray<SkyrimFaceDecodedKeyword> keywords,
        ImmutableArray<SkyrimFaceDecodedFormList> formLists = default) =>
        new(
            headParts.ToImmutableDictionary(item => SkyrimFaceRecordKey.From(item.Reference)),
            races.ToImmutableDictionary(item => SkyrimFaceRecordKey.From(item.Reference)),
            keywords.ToImmutableDictionary(item => SkyrimFaceRecordKey.From(item.Reference)),
            formLists.IsDefault
                ? ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedFormList>.Empty
                : formLists.ToImmutableDictionary(item => SkyrimFaceRecordKey.From(item.Reference)));

    private static PresetDocument Preset(params FormReference[] headParts)
    {
        var appearance = new PresetAppearance(
            null,
            headParts.Select(item => new PresetHeadPart(
                    PresetIdentifier.Parse($"{item.Plugin.Value}|{item.FormId}"), 0))
                .ToImmutableArray(),
            null,
            null,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            [],
            [new PresetTint(999, 0xFFFF_FFFF, string.Empty)],
            [],
            null,
            new PresetFieldPresence(false, true, false, false, false, false,
                true, false, false),
            []);
        return new PresetDocument(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            appearance,
            new Sha256Hash(EmptyHash),
            []);
    }

    private static FormReference Reference(string plugin, uint formId) =>
        new(new PluginName(plugin), new FormId(formId));

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static SkyrimFaceHeadPartRecordRoute FindPart(
        SkyrimFaceRecordRoute route,
        FormReference reference) =>
        route.HeadParts.Single(item => item.Reference == reference);

    private static void AssertSelected(
        SkyrimFaceRecordRoute route,
        FormReference reference,
        NpcHeadPartType expectedType)
    {
        var part = FindPart(route, reference);
        Assert(part.IsSelected && part.EffectiveType == expectedType &&
               route.RootHeadParts.Contains(reference),
            $"Selected {expectedType} HDPT {reference} did not win its typed root bucket.");
    }

    private static void AssertTri(
        SkyrimFaceHeadPartRecordRoute part,
        SkyrimHdptTriRole role,
        string expectedPath)
    {
        var route = part.TriRoutes.SingleOrDefault(item => item.Role == role);
        Assert(route is not null && string.Equals(route.Path.Value, expectedPath,
                StringComparison.OrdinalIgnoreCase),
            $"HDPT {part.Reference} role {(int)role} did not expose exact path '{expectedPath}'.");
    }

    private static void AssertRejected(SkyrimFaceRecordRouteResult result, string diagnosticCode)
    {
        Assert(!result.Accepted && result.Route is null &&
               result.Diagnostics.Any(item => item.Code == diagnosticCode &&
                                              item.Severity == DiagnosticSeverity.Error),
            $"Expected refusal '{diagnosticCode}', got: {FormatDiagnostics(result.Diagnostics)}");
    }

    private static void AssertRejected(
        SkyrimRaceTintAuthorityResult result,
        string diagnosticCode)
    {
        Assert(!result.Accepted && result.Authority is null &&
               result.Diagnostics.Any(item => item.Code == diagnosticCode &&
                                              item.Severity == DiagnosticSeverity.Error),
            $"Expected refusal '{diagnosticCode}', got: {FormatDiagnostics(result.Diagnostics)}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static WorkspacePath FindLabRoot() =>
        new(TestAuthorityWorkspace.ResolveLabRoot(
            AppContext.BaseDirectory));

    private static WorkspacePath FindProductRoot()
    {
        string root = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(root, "Actorwright.sln")) ||
            !File.Exists(Path.Combine(root, "global.json")))
        {
            throw new InvalidOperationException(
                "The isolated light-HDPT selector must run from the product root.");
        }
        return new WorkspacePath(root);
    }

    private static void TestLightPluginCapturedHdptCleanupOwnership(
        WorkspacePath productRoot,
        string productFixtureBase)
    {
        AssertOwnedFixtureDirectory(
            productFixtureBase,
            Path.Combine(productFixtureBase, "light-formid-routing-product"));
        string historicalFixtureBase = Path.Combine(
            productRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests");
        AssertOwnedFixtureDirectory(
            historicalFixtureBase,
            Path.Combine(historicalFixtureBase, "light-formid-routing-historical"));
        Console.WriteLine(
            "EVIDENCE cleanup artifact and historical fixture bases are admitted.");
    }

    private static void DeleteOwnedFixtureDirectory(
        string fixtureBase,
        string fixtureRoot)
    {
        AssertOwnedFixtureDirectory(fixtureBase, fixtureRoot);
        string resolvedFixtureRoot = Path.GetFullPath(fixtureRoot);
        if (Directory.Exists(resolvedFixtureRoot))
            Directory.Delete(resolvedFixtureRoot, recursive: true);
    }

    private static void AssertOwnedFixtureDirectory(
        string fixtureBase,
        string fixtureRoot)
    {
        string resolvedFixtureBase = Path.GetFullPath(fixtureBase);
        string resolvedFixtureRoot = Path.GetFullPath(fixtureRoot);
        string relative = Path.GetRelativePath(resolvedFixtureBase, resolvedFixtureRoot);
        Assert(relative is not ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar) &&
               !Path.IsPathRooted(relative),
            "Synthetic light-HDPT cleanup escaped its owned fixture base.");
    }

    private sealed class KBoundTestPolicy(WorkspacePath? root = null) : IWorkspacePolicy
    {
        public ImmutableArray<Diagnostic> Evaluate(
            WorkspacePath workspaceRoot,
            WorkspacePath outputRoot) =>
            EvaluateReadRoot(workspaceRoot, outputRoot);

        public ImmutableArray<Diagnostic> EvaluateReadRoot(
            WorkspacePath workspaceRoot,
            WorkspacePath readRoot) =>
            workspaceRoot.IsUnder(root ?? LabRoot) && readRoot.IsUnder(workspaceRoot)
                ? []
                : [new Diagnostic("test-read-root-refused", DiagnosticSeverity.Error,
                    "Test reads must remain beneath the K-local workspace root.")];
    }

    private sealed class FixedAuthorityLoader(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> authorities)
        : ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(
                true, authorities, []));
        }
    }
}
