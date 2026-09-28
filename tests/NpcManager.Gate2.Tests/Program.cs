using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.TestInfrastructure;

namespace NpcManager.Gate2.Tests;

internal static class Program
{
    private static readonly string[] TemplateMasters =
    [
        "Skyrim.esm", "High Poly Head.esm", "Improved Eyes Skyrim.esp",
        "Koralina's Eyebrows.esp", "KS Hairdo's.esp"
    ];

    private static readonly string[] FaceGeomShapeNames =
    [
        "0_HAIRLINE_Female_Human_Straight", "0LassiHL", "0Lassi",
        "KoralinaEyebrowsF02", "MJBFemaleEyesHumanGreen04",
        "00KLH_FemaleHeadNord", "FemaleMouthHumanoidDefault"
    ];

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("typed forms and Race/sex tint authority are admitted", TestCompleteAuthority),
            ("invalid form and provenance bindings are refused", TestFormBindingRefusals),
            ("absent, ambiguous, non-finite, and out-of-range tint authority is refused", TestTintRefusals),
            ("runtime routes remain validated declarations rather than runtime proof", TestRuntimeDeclarations),
            ("direct faceTextures admit derived TXST authority without a headTexture FormID", TestDirectHeadTexturePlan),
            ("accepted plan maps to authored NPC appearance without the RaceMenu sentinel", TestAppearanceMapping),
            ("RaceMenu active custom morph order and duplicates survive parse and export", TestCustomMorphDocumentOrder),
            ("RaceMenu preset catalog is bounded, hash-bound, ordered, and omits malformed files", TestRaceMenuPresetCatalog),
            ("product-owned SSE PEX is embedded, verified, and reserved", TestProductOwnedRuntimeAsset)
        };
        var passed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
                passed++;
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

    private static async Task TestCompleteAuthority()
    {
        using var context = CreateContext("complete");
        var request = CreateRequest(context, "complete", AuthorityMutation.None,
            includeRuntimeFields: true, includeRuntimeRoutes: true);
        var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
        Assert(result.Accepted && result.Plan is { IsReady: true },
            "Complete authority was refused: " + Format(result.Diagnostics));
        var plan = result.Plan ?? throw new InvalidOperationException("Accepted plan is absent.");
        Assert(!plan.RuntimeAuthority, "Read-only admission incorrectly claimed runtime authority.");
        Assert(plan.RaceBinding.Signature == new RecordSignature("RACE"),
            "Race authority did not preserve its verified RACE signature.");
        Assert(plan.ResolvedHeadParts.Length == 4 &&
               plan.ResolvedHeadParts.All(item => item.Binding.Signature == new RecordSignature("HDPT")),
            "HDPT bindings were not preserved for all head parts.");
        Assert(plan.ResolvedHeadParts.Select(item => item.Binding.HeadPartType).SequenceEqual(
                [NpcHeadPartType.Face, NpcHeadPartType.Eyes, NpcHeadPartType.Eyebrows, NpcHeadPartType.Hair]) &&
               plan.HeadPartDispositions.Length == 5 &&
               plan.HeadPartDispositions[4] is
               {
                   Kind: RaceMenuNpcHeadPartDispositionKind.Baked,
                   FaceGeomSha256: not null
               },
            "Provider-read HDPT types were not retained independently of RaceMenu slot numbers.");
        Assert(plan.ResolvedHeadTexture?.Signature == new RecordSignature("TXST"),
            "Head texture did not resolve through a TXST binding.");
        Assert(plan.ResolvedHairColor is RaceMenuNpcOutputOwnedHairColorAuthority
        {
            PackedRgb: 1706249,
            AllocatedLocalFormId.Value: 0x0000_0801
        }, "Packed hair color did not retain its explicit output-owned CLFM authority.");
        var tint = plan.ResolvedTintLayers.Single();
        Assert(tint.Layer.Index == 16 && tint.Layer.PresetIndex == -1 &&
               tint.Layer.Coverage == 45 && tint.IsSkinTint,
            "Race/sex tint authority did not preserve the exact TINI/TIAS skin mapping.");
        Assert(plan.TintDispositions.Length == 3 &&
               plan.TintDispositions[1] is
               {
                   Kind: RaceMenuNpcTintDispositionKind.Baked,
                   FaceGeomSha256: not null,
                   FaceTintSha256: not null
               } &&
               plan.TintDispositions[2].Kind == RaceMenuNpcTintDispositionKind.Inactive,
            "Mapped, baked, and alpha-zero tint dispositions were not retained exactly.");
        Assert(plan.QnamDerivation is
        {
            SourceKind: RaceMenuNpcQnamSourceKind.MappedSkinTint,
            SourceJslotTintIndex: 0,
            Red: 1F,
            Green: 1F,
            Blue: 1F
        }, "QNAM was not deterministically derived from the mapped white skin tint.");
        Assert(plan.Provider.TemplateMasters.Select(item => item.Value)
                   .SequenceEqual(TemplateMasters, StringComparer.OrdinalIgnoreCase),
            "Provider authority did not retain the copied template's exact master list.");
        Assert(plan.SourceDependencies.Any(item =>
                   string.Equals(item.Value, "GoamElvenEars.esp",
                       StringComparison.OrdinalIgnoreCase)),
            "Baked Goam provenance was not retained as a source dependency.");
        Assert(plan.RequiredOutputMasters.Select(item => item.Value).SequenceEqual(
                [
                    "Skyrim.esm", "High Poly Head.esm", "Improved Eyes Skyrim.esp",
                    "Koralina's Eyebrows.esp", "KS Hairdo's.esp", "Sylvia SSE.esp",
                    "AuntCassV2.esp"
                ], StringComparer.OrdinalIgnoreCase) &&
               plan.RequiredOutputMasters.All(item =>
                   !string.Equals(item.Value, "GoamElvenEars.esp",
                       StringComparison.OrdinalIgnoreCase)),
            "Source-only baked Goam provenance leaked into the required output masters.");
        AssertCoverage(plan, RaceMenuNpcAppearanceField.CustomMorphs,
            RaceMenuNpcFieldCoverageKind.Baked);
        AssertCoverage(plan, RaceMenuNpcAppearanceField.Sculpt,
            RaceMenuNpcFieldCoverageKind.Baked);
        AssertCoverage(plan, RaceMenuNpcAppearanceField.FaceTextures,
            RaceMenuNpcFieldCoverageKind.Baked);
        AssertCoverage(plan, RaceMenuNpcAppearanceField.ModNames,
            RaceMenuNpcFieldCoverageKind.Mapped);
        AssertCoverage(plan, RaceMenuNpcAppearanceField.Mods,
            RaceMenuNpcFieldCoverageKind.Mapped);
        AssertCoverage(plan, RaceMenuNpcAppearanceField.Version,
            RaceMenuNpcFieldCoverageKind.Mapped);
        foreach (var field in RaceMenuNpcAppearanceFieldExtensions.RuntimeFields)
            AssertCoverage(plan, field, RaceMenuNpcFieldCoverageKind.RuntimeDeclared);
        var bodyAppearance = plan.Preset.Appearance with
        {
            BodyMorphs = ImmutableDictionary<string, float>.Empty
                .Add("CBBE Breast", 0.25F)
        };
        var bodyPlan = plan with
        {
            Preset = plan.Preset with { Appearance = bodyAppearance },
            FieldCoverage = plan.FieldCoverage.Select(item =>
                    item.Field == RaceMenuNpcAppearanceField.BodyMorphs
                        ? new RaceMenuNpcFieldCoverage(
                            item.Field, true, RaceMenuNpcFieldCoverageKind.Mapped,
                            "typed BodyGen test authority")
                        : item)
                .ToImmutableArray()
        };
        SkyrimNpcRuntimeAppearancePayload bodySlideApplied =
            RaceMenuNpcRuntimeAppearanceMapper.Map(bodyPlan, null, applyBodyMorphs: true);
        SkyrimNpcRuntimeAppearancePayload bodySlideOmitted =
            RaceMenuNpcRuntimeAppearanceMapper.Map(bodyPlan, null, applyBodyMorphs: false);
        Assert(bodySlideApplied.Dispositions.Any(item =>
                   item.Surface == SkyrimNpcRuntimeAppearanceSurface.BodyMorphs &&
                   item.Kind == SkyrimNpcRuntimeDispositionKind.BodyGenExcludedFromVmad) &&
               bodySlideOmitted.Dispositions.Any(item =>
                   item.Surface == SkyrimNpcRuntimeAppearanceSurface.BodyMorphs &&
                   item.Kind == SkyrimNpcRuntimeDispositionKind.UserOmitted),
            "The checked-by-default BodySlide choice was not retained as an explicit apply/omit disposition.");

        var inactiveLegacyOverlay = new RaceMenuBodyOverlay(
            "Body [Ovl4]",
            @"\SL Survival\spanky\spank_breasts_light.dds",
            null,
            [0F, 0F, 0F, 0F],
            0F,
            [
                new RaceMenuValue(0, 3, -1, RaceMenuScalar.FromInteger(0)),
                new RaceMenuValue(2, 4, -1, RaceMenuScalar.FromInteger(0)),
                new RaceMenuValue(3, 4, -1, RaceMenuScalar.FromInteger(0)),
                new RaceMenuValue(7, 3, -1, RaceMenuScalar.FromInteger(0)),
                new RaceMenuValue(8, 4, -1, RaceMenuScalar.FromInteger(0)),
                new RaceMenuValue(9, 2, 0,
                    RaceMenuScalar.FromText(
                        @"\SL Survival\spanky\spank_breasts_light.dds"))
            ]);
        var inactiveRaceMenu = bodyPlan.Preset.Appearance.RaceMenu! with
        {
            BodyOverlays = [inactiveLegacyOverlay]
        };
        var inactiveAppearance = bodyPlan.Preset.Appearance with
        {
            RaceMenu = inactiveRaceMenu
        };
        var inactivePlan = bodyPlan with
        {
            Preset = bodyPlan.Preset with { Appearance = inactiveAppearance }
        };
        SkyrimNpcRuntimeAppearancePayload inactiveRuntime =
            RaceMenuNpcRuntimeAppearanceMapper.Map(
                inactivePlan, null, applyBodyMorphs: true);
        Assert(inactiveRuntime.Overlays.IsEmpty &&
               inactiveRuntime.Dispositions.Any(item =>
                   item.Surface == SkyrimNpcRuntimeAppearanceSurface.Overlay &&
                   item.SourceIndex == 0 &&
                   item.Kind == SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride),
            "An alpha-zero legacy overlay with only zero-valued controls was emitted as an active runtime override.");
        Assert(!Directory.Exists(request.OutputRoot.Value), "Admission created the future output root.");
    }

    private static async Task TestCustomMorphDocumentOrder()
    {
        using var context = CreateContext("custom-morph-order");
        var source = new WorkspacePath(Path.Combine(context.WorkRoot, "ordered-custom-morphs.jslot"));
        var standardSource = new WorkspacePath(Path.Combine(context.WorkRoot,
            "ordered-standard-custom-morphs.jslot"));
        var output = new WorkspacePath(Path.Combine(context.WorkRoot, "ordered-custom-morphs-export.jslot"));
        WriteText(source.Value, """
            {
              "customMorphs": [
                { "name": "RootZ", "value": 0.1 },
                { "name": "Shared", "value": 0.2 },
                { "name": "RootA", "value": 0.3 }
              ],
              "morphs": {
                "custom": [
                  { "name": "NestedB", "value": 0.4 },
                  { "name": "Shared", "value": 0.45 },
                  { "name": "Shared", "value": 0.5 },
                  { "name": "NestedC", "value": 0.6 }
                ]
              }
            }
            """);
        var policy = new KOnlyWorkspacePolicy(context.LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var service = new PresetService(policy, context.LabRoot);
        var parsed = await service.InspectAsync(new PresetParseRequest(
            PresetFormat.RaceMenuJslot, GameEdition.SkyrimSpecialEdition, source),
            CancellationToken.None);
        Assert(parsed.Document is not null && parsed.Document.IsValid,
            "Ordered custom morph fixture was refused: " + Format(parsed.Diagnostics));
        var appearance = parsed.Document!.Appearance;
        Assert(appearance.OrderedCustomMorphs.Select(item => item.Name).SequenceEqual(
                ["NestedB", "Shared", "Shared", "NestedC"]),
            "Typed custom morph sequence did not retain the active nested list, including duplicates.");
        Assert(BitConverter.SingleToInt32Bits(appearance.CustomMorphs["Shared"]) ==
               BitConverter.SingleToInt32Bits(0.5F),
            "Nested duplicate did not remain the typed last-value authority.");

        WriteText(standardSource.Value, """
            {
              "morphs": {
                "custom": [
                  { "name": "NestedB", "value": 0.4 },
                  { "name": "Shared", "value": 0.45 },
                  { "name": "Shared", "value": 0.5 },
                  { "name": "NestedC", "value": 0.6 }
                ]
              }
            }
            """);
        var exported = await service.ExportAsync(new PresetExportRequest(
            PresetFormat.RaceMenuJslot, GameEdition.SkyrimSpecialEdition, standardSource, output),
            CancellationToken.None);
        Assert(exported.Written,
            "Ordered custom morph export failed: " + Format(exported.Diagnostics));
        using var document = JsonDocument.Parse(File.ReadAllBytes(output.Value));
        var names = document.RootElement.GetProperty("morphs").GetProperty("custom")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert(names.SequenceEqual(["NestedB", "Shared", "Shared", "NestedC"]),
            "RaceMenu export reordered the typed custom morph sequence.");
    }

    private static async Task TestDirectHeadTexturePlan()
    {
        using var context = CreateContext("direct-head-textures");
        RaceMenuNpcBuildRequest request = CreateRequest(
            context,
            "direct-head-textures",
            AuthorityMutation.None,
            includeRuntimeFields: true,
            includeRuntimeRoutes: true,
            directHeadTexture: true);
        RaceMenuNpcAppearancePlanResult result = await context.Service.AnalyzeAsync(
            request, CancellationToken.None);
        Assert(result.Accepted && result.Plan is
        {
            IsReady: true,
            ResolvedHeadTexture.Signature.Value: "TXST"
        } &&
               result.Plan.Preset.Appearance.RaceMenu?.HeadTexture is null,
            "A real JSlot-style direct faceTextures source did not admit the one hash-bound derived TXST binding: " +
            Format(result.Diagnostics));
    }

    private static async Task TestRaceMenuPresetCatalog()
    {
        using var context = CreateContext("preset-catalog");
        var presetDirectory = new WorkspacePath(Path.Combine(context.WorkRoot, "Presets"));
        Directory.CreateDirectory(presetDirectory.Value);
        WriteText(Path.Combine(presetDirectory.Value, "Zulu.jslot"), StaticPresetJson);
        WriteText(Path.Combine(presetDirectory.Value, "alpha.JSLOT"), StaticPresetJson);
        WriteText(Path.Combine(presetDirectory.Value, "broken.jslot"), "{ not-json");
        WriteText(Path.Combine(presetDirectory.Value, "ignored.json"), StaticPresetJson);

        var policy = new KOnlyWorkspacePolicy(context.LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var evaluator = new CountingCompatibilityEvaluator();
        var service = new RaceMenuPresetCatalogService(
            new PresetService(policy, context.LabRoot), policy, context.LabRoot, evaluator);
        var target = new RaceMenuPresetTarget("test-race-closure-v1",
            new FormReference(new PluginName("Skyrim.esm"), new FormId(0x0001_3746)),
            NpcSex.Female, context.LabRoot, []);

        RaceMenuPresetCatalogResult result = await service.LoadAsync(
            new RaceMenuPresetCatalogRequest(presetDirectory, target),
            CancellationToken.None);
        Assert(result.Accepted && result.Entries.Length == 2,
            "The catalog did not retain exactly the two valid .jslot files: " +
            Format(result.Diagnostics));
        Assert(result.Entries.Select(item => item.DisplayName)
                .SequenceEqual(["alpha", "Zulu"]),
            "The catalog did not use stable case-insensitive filename ordering.");
        Assert(result.Entries.All(item => item.SourceSha256 == item.Document.SourceHash &&
                                          item.Compatibility == RaceMenuPresetCompatibilityKind.Compatible),
            "A catalog entry lost its parsed hash or compatibility result.");
        Assert(result.Entries.All(item => item.Summary.HeadParts == 5 &&
                                          item.Summary.Tints > 0 &&
                                          item.Summary.Weight == 42),
            "The catalog summary did not come from the admitted parsed document.");
        Assert(evaluator.Calls == 2,
            "Compatibility was not evaluated exactly once per admitted preset.");
        Assert(HasCode(result.Diagnostics, "preset-catalog-entry-omitted"),
            "The malformed .jslot omission was not visible in diagnostics.");

        RaceMenuPresetCatalogResult refused = await service.LoadAsync(
            new RaceMenuPresetCatalogRequest(new WorkspacePath(@"F:\ExampleGame"), target),
            CancellationToken.None);
        Assert(!refused.Accepted && refused.Entries.Length == 0 &&
               HasCode(refused.Diagnostics, "preset-catalog-outside-lab"),
            "The catalog did not refuse a protected/outside-workspace directory before scanning.");
    }

    private static async Task TestFormBindingRefusals()
    {
        foreach (var mutation in new[]
                 {
                     AuthorityMutation.WrongSignature,
                     AuthorityMutation.DuplicateBinding,
                     AuthorityMutation.UnknownSignature,
                     AuthorityMutation.HairColorProviderValueMismatch,
                     AuthorityMutation.UnqualifiedMetadataMaster
                 })
        {
            using var context = CreateContext($"form-{mutation}");
            var request = CreateRequest(context, mutation.ToString(), mutation,
                includeRuntimeFields: true, includeRuntimeRoutes: true);
            var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(!result.Accepted,
                $"{mutation} form authority was admitted unexpectedly.");
            Assert(HasCode(result.Diagnostics, "racemenu-plan-record-authority-invalid") ||
                   HasCode(result.Diagnostics, "racemenu-plan-form-provider-signature-mismatch") ||
                   HasCode(result.Diagnostics, "racemenu-plan-haircolor-provider-value-mismatch"),
                $"{mutation} refusal lacked a form-authority diagnostic: {Format(result.Diagnostics)}");
            Assert(!Directory.Exists(request.OutputRoot.Value),
                $"{mutation} refusal created the future output root.");
        }
    }

    private static async Task TestTintRefusals()
    {
        foreach (var mutation in new[]
                 {
                     AuthorityMutation.MissingTintMapping,
                     AuthorityMutation.AmbiguousQnamSource,
                     AuthorityMutation.NonFiniteQnam,
                     AuthorityMutation.OutOfRangeCoverage
                 })
        {
            using var context = CreateContext($"tint-{mutation}");
            var request = CreateRequest(context, mutation.ToString(), mutation,
                includeRuntimeFields: true, includeRuntimeRoutes: true);
            var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(!result.Accepted &&
                   HasCode(result.Diagnostics, "racemenu-plan-record-authority-invalid"),
                $"{mutation} tint authority was not refused explicitly: {Format(result.Diagnostics)}");
            Assert(!Directory.Exists(request.OutputRoot.Value),
                $"{mutation} refusal created the future output root.");
        }
    }

    private static async Task TestRuntimeDeclarations()
    {
        using (var context = CreateContext("runtime-absent"))
        {
            var request = CreateRequest(context, "runtime-absent", AuthorityMutation.None,
                includeRuntimeFields: false, includeRuntimeRoutes: true);
            var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(!result.Accepted && result.Plan is not null &&
                   RaceMenuNpcAppearanceFieldExtensions.RuntimeFields.All(field =>
                       HasCode(result.Diagnostics,
                           $"racemenu-plan-{field.ToWireName()}-route-unexpected")),
                "Routes declared for absent fields were not refused: " + Format(result.Diagnostics));
            Assert(!result.Plan!.RuntimeAuthority,
                "A refused declaration incorrectly claimed runtime authority.");
        }

        using (var context = CreateContext("runtime-malformed"))
        {
            var request = CreateRequest(context, "runtime-malformed", AuthorityMutation.None,
                includeRuntimeFields: true, includeRuntimeRoutes: true,
                malformedRuntimeArtifact: true);
            var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(!result.Accepted &&
                   HasCode(result.Diagnostics, "racemenu-plan-runtime-route-manifest-invalid"),
                "Malformed runtime declaration artifact was not refused: " +
                Format(result.Diagnostics));
            Assert(!Directory.Exists(request.OutputRoot.Value),
                "Malformed runtime declaration created the future output root.");
        }
    }

    private static async Task TestProductOwnedRuntimeAsset()
    {
        var labRoot = FindLabRoot();
        var testRoot = Path.Combine(labRoot.Value, "projects", "NpcManagerReimplementation",
            "tests", "NpcManager.Gate2.Tests");
        var owned = Path.Combine(testRoot,
            $".work-product-runtime-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var canonicalTestRoot = Path.GetFullPath(testRoot);
        var canonicalOwned = Path.GetFullPath(owned);
        if (!canonicalOwned.StartsWith(canonicalTestRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to use a non-owned test path.");
        Directory.CreateDirectory(owned);
        try
        {
            var asset = await SkyrimApplySseProductAsset.MaterializeAsync(
                new WorkspacePath(owned), CancellationToken.None);
            Assert(asset.Destination.Value == "Scripts/NPCM_Manolov_ApplySSE.pex",
                "Embedded SSE PEX materialized at the wrong Data-relative destination.");
            Assert(SkyrimApplySseProductAsset.IsReservedDestination(asset.Destination) &&
                   SkyrimApplySseProductAsset.IsReservedDestination(
                       new AssetPath("scripts/npcm_manolov_applysse.PEX")),
                "Caller substitution guard did not reserve the SSE PEX destination case-insensitively.");
            Assert(File.Exists(asset.Source.Value) &&
                   new FileInfo(asset.Source.Value).Length == 10_426 &&
                   HashFile(asset.Source.Value) == asset.ExpectedSha256 &&
                   asset.ExpectedSha256 == new Sha256Hash(
                       "993D994391357233AED8E7EDC8E3C1DD2BD7A06616A0E209BB9B3169BDF61332"),
                "Embedded SSE PEX bytes did not retain the pinned size and SHA-256.");
        }
        finally
        {
            if (Directory.Exists(canonicalOwned)) Directory.Delete(canonicalOwned, recursive: true);
        }

        using var context = CreateContext("reserved-product-runtime");
        var buildRequest = CreateRequest(context, "reserved-product-runtime",
            AuthorityMutation.None, includeRuntimeFields: true, includeRuntimeRoutes: true);
        var callerManifest = new WorkspacePath(Path.Combine(context.WorkRoot,
            "caller-runtime-substitution.json"));
        WriteText(callerManifest.Value, JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            assetSetId = "caller-runtime-substitution",
            edition = "skyrimse",
            nam9Authority = new
            {
                pluginPath = "does-not-exist.esp",
                pluginSha256 = new string('0', 64),
                npcFormId = "0x00000800",
                trailingValue = 0F
            },
            faceTint = new { width = 1, height = 1 },
            privateHeadTextures = new
            {
                diffuse = "Actors/Character/Test/femalehead.dds",
                normalOrGloss = "Actors/Character/Test/femalehead_msn.dds",
                glowOrDetailMap = "Actors/Character/Test/femalehead_sk.dds",
                height = "Actors/Character/Male/BlankDetailmap.dds",
                backlightMaskOrSpecular = "Actors/Character/Test/femalehead_s.dds",
                environmentMaskOrSubsurfaceTint = (string?)null,
                environment = (string?)null,
                multilayer = (string?)null
            },
            packageAssets = new[]
            {
                new
                {
                    sourcePath = "does-not-exist.pex",
                    sha256 = new string('0', 64),
                    destination = "scripts/npcm_manolov_applysse.PEX"
                }
            },
            overlayDecisions = (object?)null,
            externalTextureAuthorities = Array.Empty<object>()
        }));
        var policy = new KOnlyWorkspacePolicy(context.LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var service = new RaceMenuNpcBuildService(
            null!, null!, null!, null!, null!, policy, context.LabRoot);
        var refusal = await service.ExecuteAsync(
            new RaceMenuNpcExecutionRequest(buildRequest,
                new RaceMenuNpcStandaloneAssetAuthority(
                    callerManifest, HashFile(callerManifest.Value))),
            null, CancellationToken.None);
        Assert(!refusal.Completed &&
               refusal.Diagnostics.Any(item =>
                   item.Code == "racemenu-assets-manifest-invalid" &&
                   item.Message.Contains("product-owned", StringComparison.Ordinal)),
            "Execution manifest admitted caller substitution of the product-owned SSE PEX.");
    }

    private static async Task TestAppearanceMapping()
    {
        using var context = CreateContext("appearance-map");
        var request = CreateRequest(context, "appearance-map", AuthorityMutation.None,
            includeRuntimeFields: true, includeRuntimeRoutes: true);
        var result = await context.Service.AnalyzeAsync(request, CancellationToken.None);
        var plan = result.Plan;
        Assert(result.Accepted && plan is { IsReady: true },
            "Mapping fixture was not admitted: " + Format(result.Diagnostics));
        var admittedPlan = plan ?? throw new InvalidOperationException(
            "Accepted mapping plan is absent.");

        const float templateTrailing = 0.375F;
        var privateHeadTextures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("textures/actors/character/Gate2/Skin/femalehead.dds"),
            new AssetPath("textures/actors/character/Gate2/Skin/femalehead_msn.dds"),
            new AssetPath("textures/actors/character/Gate2/Skin/femalehead_sk.dds"),
            new AssetPath("textures/actors/character/male/BlankDetailmap.dds"),
            new AssetPath("textures/actors/character/Gate2/Skin/femalehead_s.dds"));
        var appearance = RaceMenuNpcCreationAppearanceMapper.Map(
            admittedPlan, templateTrailing, privateHeadTextures);
        Assert(appearance.OrderedHeadParts.Length == 4 &&
               appearance.OrderedHeadParts.Select(item => item.Type).SequenceEqual(
                    [NpcHeadPartType.Face, NpcHeadPartType.Eyes, NpcHeadPartType.Eyebrows, NpcHeadPartType.Hair]) &&
               appearance.OrderedHeadParts[0] is OutputOwnedSkyrimNpcFaceHeadPart
               {
                   AllocatedLocalFormId.Value: 0x0000_0803
               },
            "Mapper did not preserve ordered provider-read HDPT types.");
        var identityPreserved = RaceMenuNpcCreationAppearanceMapper.Map(
            admittedPlan,
            templateTrailing,
            privateHeadTextures,
            preserveQualifiedFaceEditorId: true);
        Assert(identityPreserved.OrderedHeadParts[0] is
            OutputOwnedSkyrimNpcFaceHeadPart
            {
                PreserveQualifiedEditorId: true
            },
            "Mapper did not retain qualified Face HDPT identity for a complete external RaceMenu export.");
        Assert(appearance.HairColor is OutputOwnedSkyrimNpcHairColor
        {
            AllocatedLocalFormId.Value: 0x0000_0801,
            PackedRgb.Value: 1706249
        }, "Mapper did not preserve the explicit output-owned CLFM route.");
        Assert(appearance.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet
        {
            AllocatedLocalFormId.Value: 0x0000_0802,
            Paths: var mappedPaths
        } &&
               mappedPaths == privateHeadTextures &&
               appearance.Weight == 42F,
            "Mapper did not author the private TXST chain or preserve weight authority.");
        Assert(admittedPlan.Preset.Appearance.SliderMorphs[18] == float.MaxValue &&
               appearance.FaceMorphs.Nam9Sliders.Length == 18 &&
               appearance.FaceMorphs.Nam9Sliders.All(value => value == 0F) &&
               appearance.FaceMorphs.Nam9Trailing == templateTrailing &&
               appearance.FaceMorphs.Nam9Trailing != admittedPlan.Preset.Appearance.SliderMorphs[18],
            "Mapper reused RaceMenu slider 18 instead of explicit template NAM9 trailing authority.");
        Assert(appearance.FaceMorphs.NamaValues.SequenceEqual([0U, 0U, 0U, 0U]) &&
               appearance.FaceTints.Layers.SequenceEqual(admittedPlan.ResolvedTintLayers.Select(item => item.Layer)) &&
               appearance.FaceTints.Layers.Single().Coverage == 45 &&
               appearance.Qnam == new SkyrimQnamRgb(1F, 1F, 1F),
            "Mapper changed NAMA, resolved tint, alpha-derived TINV, or QNAM authority.");

        var refusedNonFiniteTrailing = false;
        try
        {
            _ = RaceMenuNpcCreationAppearanceMapper.Map(
                admittedPlan, float.NaN, privateHeadTextures);
        }
        catch (InvalidDataException)
        {
            refusedNonFiniteTrailing = true;
        }
        Assert(refusedNonFiniteTrailing,
            "Mapper accepted a non-finite template NAM9 trailing value.");
        Assert(!Directory.Exists(request.OutputRoot.Value),
            "Pure appearance mapping created the future output root.");
    }

    private static Gate2Context CreateContext(string scenario)
    {
        var labRoot = FindLabRoot();
        var projectRoot = Path.Combine(labRoot.Value, "projects", "NpcManagerReimplementation");
        var workRoot = Path.Combine(projectRoot, "tests", "NpcManager.Gate2.Tests",
            $".work-{scenario}-{Environment.ProcessId}");
        if (Directory.Exists(workRoot))
            throw new InvalidOperationException($"Owned test work root already exists: {workRoot}");
        Directory.CreateDirectory(workRoot);

        var carrierData = Path.Combine(labRoot.Value, "projects", "Emi2FreshBuild", "03-builds",
            "feasibility-probes", "ck-carrier-root", "Data");
        var template = new WorkspacePath(Path.Combine(carrierData, "EmiCarrierProbe.esp"));
        var faceGeom = new WorkspacePath(Path.Combine(carrierData, "meshes", "Actors", "Character",
            "FaceGenData", "FaceGeom", "EmiCarrierProbe.esp", "00000800.NIF"));
        var faceTintManifest = new WorkspacePath(Path.Combine(projectRoot, "01-source-copies",
            "m5-fixtures", "gate1-qualified-facetint.json"));
        var dependencyManifest = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "Emi2FreshBuild", "02-normalized-resources", "dependency-manifest.json"));
        var providerManifest = new WorkspacePath(Path.Combine(workRoot, "provider.json"));
        WriteText(providerManifest.Value, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            providerId = "gate2-typed-authority-provider-v1",
            edition = "skyrimse",
            sex = "female",
            template = new
            {
                path = Relative(labRoot, template.Value),
                sha256 = HashFile(template.Value).Value,
                npcFormId = "0x00000800",
                masters = TemplateMasters
            },
            faceGeom = new
            {
                path = Relative(labRoot, faceGeom.Value),
                sha256 = HashFile(faceGeom.Value).Value,
                graphSha256 = "F0C80FC9104A6B5D22A073E1531DA0A7C07172E9B04F5DD529CADDADFE498811",
                shapeNames = FaceGeomShapeNames
            },
            faceTint = new
            {
                manifestPath = Relative(labRoot, faceTintManifest.Value),
                manifestSha256 = HashFile(faceTintManifest.Value).Value,
                providerRoot = Relative(labRoot, carrierData),
                sourceAssetPath =
                    "textures/actors/character/FaceGenData/FaceTint/EmiCarrierProbe.esp/00000800.dds",
                sourceAssetSha256 = HashFile(Path.Combine(carrierData, "textures", "actors",
                    "character", "FaceGenData", "FaceTint", "EmiCarrierProbe.esp",
                    "00000800.dds")).Value
            },
            dependencies = new
            {
                manifestPath = Relative(labRoot, dependencyManifest.Value),
                manifestSha256 = HashFile(dependencyManifest.Value).Value,
                dependencyId = "emi2-v0.3a-static-appearance-dependencies-v1",
                headPartCount = 3,
                looseAssetCount = 9,
                archiveCount = 1
            }
        }));

        var providerRequest = new BlankNpcProviderBindingRequest(
            providerManifest, HashFile(providerManifest.Value), GameEdition.SkyrimSpecialEdition,
            NpcSex.Female, template, HashFile(template.Value), new FormId(0x0000_0800),
            faceGeom, HashFile(faceGeom.Value), faceTintManifest,
            new WorkspacePath(carrierData), dependencyManifest);
        var policy = new KOnlyWorkspacePolicy(labRoot, new WorkspacePath(@"F:\ExampleGame"));
        var service = new RaceMenuNpcAppearancePlanService(
            new PresetService(policy, labRoot), new BlankNpcProviderService(policy, labRoot),
            policy, labRoot);
        var faceTint = new WorkspacePath(Path.Combine(carrierData, "textures", "actors", "character",
            "FaceGenData", "FaceTint", "EmiCarrierProbe.esp", "00000800.dds"));
        return new Gate2Context(labRoot, workRoot, providerRequest, service, faceGeom, faceTint);
    }

    private static RaceMenuNpcBuildRequest CreateRequest(
        Gate2Context context,
        string suffix,
        AuthorityMutation mutation,
        bool includeRuntimeFields,
        bool includeRuntimeRoutes,
        bool malformedRuntimeArtifact = false,
        bool directHeadTexture = false)
    {
        var preset = new WorkspacePath(Path.Combine(context.WorkRoot, $"preset-{suffix}.jslot"));
        var presetJson = includeRuntimeFields ? CompletePresetJson : StaticPresetJson;
        if (directHeadTexture)
        {
            presetJson = presetJson.Replace(
                "\"headTexture\": \"AuntCassV2.esp|00080A\",\r\n",
                string.Empty,
                StringComparison.Ordinal);
            presetJson = presetJson.Replace(
                "\"headTexture\": \"AuntCassV2.esp|00080A\",\n",
                string.Empty,
                StringComparison.Ordinal);
        }
        if (mutation == AuthorityMutation.HairColorProviderValueMismatch)
        {
            presetJson = presetJson.Replace("1706249", "7352880", StringComparison.Ordinal);
        }
        if (mutation == AuthorityMutation.UnqualifiedMetadataMaster)
        {
            presetJson = presetJson.Replace("\"modNames\": [",
                "\"modNames\": [\"UnqualifiedMetadata.esp\", ", StringComparison.Ordinal);
        }
        WriteText(preset.Value, presetJson);
        var presetHash = HashFile(preset.Value);
        var bundleId = $"gate2-{suffix}";
        var recordAuthority = WriteRecordAuthority(context, suffix, mutation);

        RaceMenuNpcRuntimeRouteAuthority? runtimeAuthority = null;
        object? runtimeBinding = null;
        if (includeRuntimeRoutes)
        {
            var routes = RaceMenuNpcAppearanceFieldExtensions.RuntimeFields.Select((field, index) =>
            {
                var artifact = Path.Combine(context.WorkRoot, $"{suffix}-{field.ToWireName()}.json");
                WriteText(artifact, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    field = malformedRuntimeArtifact && index == 0
                        ? "wrong-field"
                        : field.ToWireName()
                }));
                return new
                {
                    field = field.ToWireName(),
                    classification = "runtime-declared",
                    artifactPath = Relative(context.LabRoot, artifact),
                    artifactSha256 = HashFile(artifact).Value
                };
            }).ToArray();
            var routeManifest = new WorkspacePath(Path.Combine(context.WorkRoot,
                $"runtime-routes-{suffix}.json"));
            WriteText(routeManifest.Value, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                routeId = $"gate2-routes-{suffix}",
                bundleId,
                routes
            }));
            var routeHash = HashFile(routeManifest.Value);
            runtimeAuthority = new RaceMenuNpcRuntimeRouteAuthority(routeManifest, routeHash);
            runtimeBinding = new
            {
                manifestPath = Relative(context.LabRoot, routeManifest.Value),
                manifestSha256 = routeHash.Value
            };
        }

        var bundleManifest = new WorkspacePath(Path.Combine(context.WorkRoot,
            $"bundle-{suffix}.json"));
        WriteText(bundleManifest.Value, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            bundleId,
            edition = "skyrimse",
            preset = new
            {
                path = Relative(context.LabRoot, preset.Value),
                sha256 = presetHash.Value
            },
            charGen = new
            {
                faceGeomPath = Relative(context.LabRoot, context.FaceGeom.Value),
                faceGeomSha256 = HashFile(context.FaceGeom.Value).Value,
                faceTintPath = Relative(context.LabRoot, context.FaceTint.Value),
                faceTintSha256 = HashFile(context.FaceTint.Value).Value
            },
            providerContext = new
            {
                manifestPath = Relative(context.LabRoot, context.Provider.ManifestPath.Value),
                manifestSha256 = context.Provider.ExpectedManifestSha256.Value,
                dependencyManifestPath = Relative(context.LabRoot,
                    context.Provider.DependencyManifest.Value),
                dependencyManifestSha256 = HashFile(context.Provider.DependencyManifest.Value).Value
            },
            recordAuthority = new
            {
                manifestPath = Relative(context.LabRoot, recordAuthority.ManifestPath.Value),
                manifestSha256 = recordAuthority.ExpectedManifestSha256.Value
            },
            runtimeRoutes = runtimeBinding
        }));
        var bundle = new RaceMenuNpcPresetBundle(
            bundleManifest,
            HashFile(bundleManifest.Value),
            preset,
            presetHash,
            context.FaceGeom,
            HashFile(context.FaceGeom.Value),
            context.FaceTint,
            HashFile(context.FaceTint.Value),
            recordAuthority,
            runtimeAuthority);
        return BuildRequest(context, bundle, suffix);
    }

    private static RaceMenuNpcRecordAuthority WriteRecordAuthority(
        Gate2Context context,
        string suffix,
        AuthorityMutation mutation)
    {
        var dependencyRoot = Path.Combine(context.LabRoot.Value, "projects", "Emi2FreshBuild",
            "01-source-copies", "dependency-providers");
        var donorRoot = Path.Combine(context.LabRoot.Value, "projects", "Emi2FreshBuild",
            "01-source-copies", "diagnosis-donors");
        var eyes = Path.Combine(dependencyRoot, "Improved Eyes Skyrim.esp");
        var brows = Path.Combine(dependencyRoot, "Koralina's Eyebrows.esp");
        var hair = Path.Combine(dependencyRoot, "KS Hairdo's.esp");
        var highPolyHead = Path.Combine(context.LabRoot.Value, "projects", "Emi2FreshBuild",
            "03-builds", "feasibility-probes", "ck-carrier-root", "Data", "High Poly Head.esm");
        var auntCass = Path.Combine(donorRoot, "AuntCassV", "AuntCassV2.esp");
        var raceProvider = Path.Combine(context.LabRoot.Value, "projects", "Eslified",
            "03-flagged-esps", "Sylvia SSE.esp");

        Dictionary<string, object?> Binding(
            string signature,
            string sourceFormKey,
            string providerFormKey,
            string provider,
            string? headPartType = null)
        {
            var binding = new Dictionary<string, object?>
            {
                ["signature"] = signature,
                ["sourceFormKey"] = sourceFormKey,
                ["providerFormKey"] = providerFormKey,
                ["providerPluginPath"] = Relative(context.LabRoot, provider),
                ["providerPluginSha256"] = HashFile(provider).Value
            };
            if (headPartType is not null) binding["headPartType"] = headPartType;
            return binding;
        }

        var bindings = new List<Dictionary<string, object?>>
        {
            Binding("HDPT", "High Poly Head.esm|0x000A06",
                "High Poly Head.esm|0x000A06", highPolyHead, "face"),
            Binding("HDPT", "Improved Eyes Skyrim.esp|0x002889",
                "Improved Eyes Skyrim.esp|0x002889", eyes, "eyes"),
            Binding("HDPT", "Koralina's Eyebrows.esp|0x000801",
                "Koralina's Eyebrows.esp|0x000801", brows, "eyebrows"),
            Binding("HDPT", "KS Hairdo's.esp|0x0A9555",
                "KS Hairdo's.esp|0x0A9555", hair, "hair"),
            Binding("TXST", "AuntCassV2.esp|0x00080A",
                "AuntCassV2.esp|0x00080A", auntCass)
        };
        if (mutation == AuthorityMutation.HairColorProviderValueMismatch)
        {
            bindings.Add(Binding("CLFM", "AuntCassV2.esp|0x000800",
                "AuntCassV2.esp|0x000800", auntCass));
        }
        if (mutation == AuthorityMutation.WrongSignature)
            bindings[0]["signature"] = "TXST";
        else if (mutation == AuthorityMutation.DuplicateBinding)
            bindings.Add(new Dictionary<string, object?>(bindings[0]));
        else if (mutation == AuthorityMutation.UnknownSignature)
            bindings[0]["signature"] = "NPC_";

        var tintMappings = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["jslotIndex"] = 0,
                ["disposition"] = "mapped-record",
                ["tiniIndex"] = 16,
                ["tiasPresetIndex"] = -1,
                ["skinTint"] = true
            },
            new()
            {
                ["jslotIndex"] = 1,
                ["disposition"] = "baked"
            },
            new()
            {
                ["jslotIndex"] = 2,
                ["disposition"] = "inactive"
            }
        };
        if (mutation == AuthorityMutation.OutOfRangeCoverage)
            tintMappings[0]["coverage"] = 101;
        if (mutation == AuthorityMutation.MissingTintMapping)
            tintMappings.Clear();

        object hairColorAuthority;
        if (mutation == AuthorityMutation.HairColorProviderValueMismatch)
        {
            hairColorAuthority = new
            {
                kind = "external-clfm",
                packedRgb = 7352880,
                providerFormKey = "AuntCassV2.esp|0x000800"
            };
        }
        else
        {
            hairColorAuthority = new
            {
                kind = "output-owned-clfm",
                packedRgb = 1706249,
                allocatedLocalFormId = "0x00000801"
            };
        }

        var qnam = new Dictionary<string, object?>
        {
            ["source"] = "mapped-skin-tint",
            ["jslotIndex"] = mutation == AuthorityMutation.AmbiguousQnamSource ? 1 : 0,
            ["red"] = 1D,
            ["green"] = 1D,
            ["blue"] = 1D
        };
        var manifest = new WorkspacePath(Path.Combine(context.WorkRoot,
            $"record-authority-{suffix}.json"));
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            authorityId = $"gate2-record-authority-{suffix}",
            edition = "skyrimse",
            race = Binding("RACE", "Sylvia SSE.esp|0x00080B",
                "Sylvia SSE.esp|0x00080B", raceProvider),
            sex = "female",
            formBindings = bindings,
            headPartDispositions = new object[]
            {
                new { sourceFormKey = "High Poly Head.esm|0x000A06", disposition = "mapped-record" },
                new { sourceFormKey = "Improved Eyes Skyrim.esp|0x002889", disposition = "mapped-record" },
                new { sourceFormKey = "Koralina's Eyebrows.esp|0x000801", disposition = "mapped-record" },
                new { sourceFormKey = "KS Hairdo's.esp|0x0A9555", disposition = "mapped-record" },
                new { sourceFormKey = "GoamElvenEars.esp|0x825D61", disposition = "baked" }
            },
            hairColorAuthority,
            tintMappings,
            qnam
        });
        if (mutation == AuthorityMutation.NonFiniteQnam)
        {
            var changed = json.Replace("\"red\":1", "\"red\":1e309", StringComparison.Ordinal);
            if (string.Equals(changed, json, StringComparison.Ordinal))
                throw new InvalidOperationException("Could not inject the non-finite QNAM test value.");
            json = changed;
        }
        WriteText(manifest.Value, json);
        return new RaceMenuNpcRecordAuthority(manifest, HashFile(manifest.Value));
    }

    private static RaceMenuNpcBuildRequest BuildRequest(
        Gate2Context context,
        RaceMenuNpcPresetBundle bundle,
        string suffix)
    {
        var skyrim = new PluginName("Skyrim.esm");
        return new RaceMenuNpcBuildRequest(GameEdition.SkyrimSpecialEdition, bundle,
            context.Provider, new WorkspacePath(Path.Combine(context.WorkRoot, $"output-{suffix}")),
            new PluginName($"Gate2{Sanitize(suffix)}.esp"),
            new NpcCreationIdentity(new EditorId($"Gate2{Sanitize(suffix)}Npc"),
                new NpcName("Gate 2 NPC")),
            new SkyrimNpcCreationTraits(NpcSex.Female, NpcCreationRole.StaticValidation,
                true, false, false, false, true),
            new SkyrimNpcCreationReferences(
                new FormReference(new PluginName("Sylvia SSE.esp"),
                    new FormId(0x0000_080B)),
                new FormReference(skyrim, new FormId(0x0001_3ADC)),
                new FormReference(skyrim, new FormId(0x0001_3181)),
                new FormReference(skyrim, new FormId(0x0003_BE1D)),
                new FormReference(skyrim, new FormId(0x0008_0012))),
            new SkyrimNpcCreationStats(new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 35, 0, 50, 50, 50, 1f, 42f, 255));
    }

    private static void AssertCoverage(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcAppearanceField field,
        RaceMenuNpcFieldCoverageKind expected)
    {
        var item = plan.FieldCoverage.Single(entry => entry.Field == field);
        Assert(item.Present && item.Classification == expected,
            $"{field} coverage was {item.Classification} (present={item.Present}), expected {expected}.");
    }

    private static WorkspacePath FindLabRoot() =>
        new(TestAuthorityWorkspace.ResolveLabRoot(
            AppContext.BaseDirectory));

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static string Relative(WorkspacePath root, string path) =>
        Path.GetRelativePath(root.Value, path).Replace('\\', '/');

    private static string Sanitize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Take(36).ToArray());

    private static void WriteText(string path, string contents) =>
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static bool HasCode(IEnumerable<Diagnostic> diagnostics, string code) =>
        diagnostics.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal));

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private enum AuthorityMutation
    {
        None,
        WrongSignature,
        DuplicateBinding,
        UnknownSignature,
        HairColorProviderValueMismatch,
        UnqualifiedMetadataMaster,
        MissingTintMapping,
        AmbiguousQnamSource,
        NonFiniteQnam,
        OutOfRangeCoverage
    }

    private sealed class Gate2Context : IDisposable
    {
        internal Gate2Context(
            WorkspacePath labRoot,
            string workRoot,
            BlankNpcProviderBindingRequest provider,
            RaceMenuNpcAppearancePlanService service,
            WorkspacePath faceGeom,
            WorkspacePath faceTint)
        {
            LabRoot = labRoot;
            WorkRoot = workRoot;
            Provider = provider;
            Service = service;
            FaceGeom = faceGeom;
            FaceTint = faceTint;
        }

        internal WorkspacePath LabRoot { get; }
        internal string WorkRoot { get; }
        internal BlankNpcProviderBindingRequest Provider { get; }
        internal RaceMenuNpcAppearancePlanService Service { get; }
        internal WorkspacePath FaceGeom { get; }
        internal WorkspacePath FaceTint { get; }

        public void Dispose()
        {
            var testRoot = Path.GetFullPath(Path.Combine(LabRoot.Value, "projects",
                "NpcManagerReimplementation", "tests", "NpcManager.Gate2.Tests"));
            var owned = Path.GetFullPath(WorkRoot);
            if (!owned.StartsWith(testRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clean a non-owned test path.");
            }
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }
    }

    private sealed class CountingCompatibilityEvaluator : IRaceMenuPresetCompatibilityEvaluator
    {
        internal int Calls { get; private set; }

        public ValueTask<RaceMenuPresetCompatibilityResult> EvaluateAsync(
            PresetDocument preset,
            RaceMenuPresetTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert(preset.Format == PresetFormat.RaceMenuJslot &&
                   target.AuthorityId == "test-race-closure-v1",
                "The catalog changed the evaluator's typed preset or target context.");
            return ValueTask.FromResult(new RaceMenuPresetCompatibilityResult(
                RaceMenuPresetCompatibilityKind.Compatible, []));
        }
    }

    private const string StaticPresetJson = """
        {
          "headParts": [
            { "formIdentifier": "High Poly Head.esm|000A06", "type": 1 },
            { "formIdentifier": "Improved Eyes Skyrim.esp|002889", "type": 2 },
            { "formIdentifier": "Koralina's Eyebrows.esp|000801", "type": 3 },
            { "formIdentifier": "KS Hairdo's.esp|0A9555", "type": 4 },
            { "formIdentifier": "GoamElvenEars.esp|825D61", "type": 7 }
          ],
          "actor": {
            "hairColor": 1706249,
            "headTexture": "AuntCassV2.esp|00080A",
            "weight": 42
          },
          "morphs": {
            "default": {
              "morphs": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
              "presets": [0, 0, 0, 0]
            },
            "custom": [{ "name": "NoseLength", "value": 0.25 }],
            "sculptDivisor": 10000,
            "sculpt": [{ "host": "NPC Head [Head]", "vertices": 100, "data": [[1, 100, 0, -100]] }]
          },
          "tintInfo": [
            { "index": 0, "color": 1929379839, "texture": "" },
            { "index": 1, "color": 3556704256, "texture": "textures/custom-mask.dds" },
            { "index": 2, "color": 0, "texture": "" }
          ]
        }
        """;

    private const string CompletePresetJson = """
        {
          "headParts": [
            { "formIdentifier": "High Poly Head.esm|000A06", "type": 1 },
            { "formIdentifier": "Improved Eyes Skyrim.esp|002889", "type": 2 },
            { "formIdentifier": "Koralina's Eyebrows.esp|000801", "type": 3 },
            { "formIdentifier": "KS Hairdo's.esp|0A9555", "type": 4 },
            { "formIdentifier": "GoamElvenEars.esp|825D61", "type": 7 }
          ],
          "actor": {
            "hairColor": 1706249,
            "headTexture": "AuntCassV2.esp|00080A",
            "weight": 42
          },
          "morphs": {
            "default": {
              "morphs": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3.4028235E+38],
              "presets": [0, 0, 0, 0]
            },
            "custom": [{ "name": "NoseLength", "value": 0.25 }],
            "sculptDivisor": 10000,
            "sculpt": [{ "host": "NPC Head [Head]", "vertices": 100, "data": [[1, 100, 0, -100]] }]
          },
          "tintInfo": [
            { "index": 0, "color": 1929379839, "texture": "" },
            { "index": 1, "color": 3556704256, "texture": "textures/custom-mask.dds" },
            { "index": 2, "color": 0, "texture": "" }
          ],
          "faceTextures": [{
            "index": 0,
            "texture": "Actors/Character/Female/FemaleHead.dds"
          }],
          "modNames": [
            "Skyrim.esm",
            "Improved Eyes Skyrim.esp",
            "Koralina's Eyebrows.esp",
            "KS Hairdo's.esp",
            "AuntCassV2.esp",
            "GoamElvenEars.esp"
          ],
          "mods": [
            { "index": 0, "name": "Skyrim.esm" },
            { "index": 2, "name": "Improved Eyes Skyrim.esp" },
            { "index": 3, "name": "Koralina's Eyebrows.esp" },
            { "index": 4, "name": "KS Hairdo's.esp" },
            { "index": 5, "name": "AuntCassV2.esp" }
          ],
          "version": {
            "formatVersion": 3,
            "runtimeVersion": 17039392,
            "signature": 1163086675,
            "skseVersion": 33554736
          },
          "overrides": [{
            "node": "NPC L Hand [LHnd]",
            "diffuse": "textures/body/overlay.dds",
            "normal": "textures/body/overlay_n.dds",
            "tint": [1, 1, 1, 1],
            "alpha": 0.5
          }],
          "transforms": [{
            "firstPerson": false,
            "node": "NPC L Hand [LHnd]",
            "keys": [{ "name": "RSMTransform", "values": [
              { "key": 30, "type": 4, "index": 0, "data": 1.05 }
            ] }]
          }],
          "skinOverrides": [{
            "firstPerson": false,
            "slotMask": 32,
            "values": [{
              "key": 9,
              "type": 2,
              "index": 0,
              "data": "textures/actors/character/female/femalebody_1.dds"
            }]
          }]
        }
        """;
}
