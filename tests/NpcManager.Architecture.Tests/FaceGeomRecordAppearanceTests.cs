using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFaceGeomRecordAppearance()
    {
        TestFinalFaceGeomTextureComposition();
        TestCarrierRecordAppearanceRewrite();
        await TestWinningHeadPartTextureSetRouting();
        await TestOnePromotionNpcTransaction();
    }

    private static void TestCarrierRecordAppearanceRewrite()
    {
        var plugin = new PluginName("AppearanceFixture.esp");
        ImmutableArray<string> overrideSlots =
        [
            "actorwright\\nif0.dds", "actorwright\\nif1.dds",
            "actorwright\\nif2.dds", "actorwright\\nif3.dds",
            "actorwright\\nif4.dds", "actorwright\\nif5.dds",
            "actorwright\\nif6.dds", "actorwright\\nif7.dds"
        ];
        string fixturePath = Path.GetFullPath(
            "tests/fixtures/sse-packed-normals/dynamic-16.nif");
        byte[] baseBytes = File.ReadAllBytes(fixturePath);
        var parts = Enumerable.Range(0, 6).Select(index =>
        {
            byte[] bytes = baseBytes.ToArray();
            if (index is 4 or 5)
            {
                SseNifDocument source = SseFaceGeomCarrierCodec.Parse(bytes);
                SseNifBlock shape = source.Blocks.Single(item =>
                    item.Type == "BSDynamicTriShape");
                SseNifBlock shader = source.Blocks[shape.References.Single(item =>
                    item.Kind == "shader").Target];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(shader.Offset, 4), 6);
            }
            return new SseFaceGeomCarrierAssemblyPart(
                new FormReference(plugin,
                    new FormId(checked((uint)(0x810 + index)))),
                new AssetPath($"meshes/actorwright/appearance/source-{index}.nif"),
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
                ImmutableArray.CreateRange(bytes),
                $"Appearance-{index}",
                UsesFaceTint: index == 0)
            {
                TextureSetOverride = index is 1 or 2 ? overrideSlots : [],
                HairTintPackedRgb = index is 4 or 5 ? 0x12_34_56U : null
            };
        }).ToImmutableArray();
        var faceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/AppearanceFixture.esp/00000800.dds");
        SseFaceGeomCarrierAssemblyResult result =
            new SseFaceGeomCarrierAssembler().Assemble(
                new SseFaceGeomCarrierAssemblyRequest(parts, faceTint));
        Assert(result.Assembled && result.Verified && result.Artifact is not null,
            $"Carrier appearance rewrite failed: {GeometryDiagnostics(result.Diagnostics)}");
        SseNifDocument output = SseFaceGeomCarrierCodec.Parse(
            result.Artifact!.Bytes.ToArray());
        foreach (int index in new[] { 1, 2 })
        {
            SseNifBlock shape = output.Blocks.Single(item =>
                item.Name == $"Appearance-{index}");
            SseNifBlock shader = output.Blocks[shape.References.Single(item =>
                item.Kind == "shader").Target];
            SseNifBlock textureSet = output.Blocks[shader.References.Single(item =>
                item.Kind == "textureset").Target];
            Assert(textureSet.Textures.Take(8).SequenceEqual(
                    overrideSlots, StringComparer.Ordinal),
                $"Record texture override did not reach carrier shape {shape.Name}.");
        }
        foreach (int index in new[] { 4, 5 })
        {
            SseNifBlock shape = output.Blocks.Single(item =>
                item.Name == $"Appearance-{index}");
            SseNifBlock shader = output.Blocks[shape.References.Single(item =>
                item.Kind == "shader").Target];
            int offset = shader.Offset + shader.Size - 12;
            uint packed = 0;
            foreach (int channel in Enumerable.Range(0, 3))
            {
                float value = System.Buffers.Binary.BinaryPrimitives
                    .ReadSingleLittleEndian(output.Data.AsSpan(
                        offset + channel * 4, 4));
                packed = (packed << 8) |
                         checked((byte)MathF.Round(value * 255F));
            }
            Assert(packed == 0x12_34_56U,
                $"Hair or inherited-Misc carrier shape {shape.Name} lost packed RGB tint.");
        }

        ImmutableArray<SseFaceGeomCarrierAssemblyPart> invalidTint = parts
            .Select((part, index) => index == 2
                ? part with { HairTintPackedRgb = 0x12_34_56U }
                : part)
            .ToImmutableArray();
        SseFaceGeomCarrierAssemblyResult refused =
            new SseFaceGeomCarrierAssembler().Assemble(
                new SseFaceGeomCarrierAssemblyRequest(invalidTint, faceTint));
        Assert(!refused.Assembled && refused.Diagnostics.Any(item =>
                item.Code == "sse-facegeom-carrier-hair-tint-shader"),
            "Hair tint on a non-shader-type-6 carrier shape did not fail closed.");
    }

    private static void TestFinalFaceGeomTextureComposition()
    {
        var plugin = new PluginName("AppearanceFixture.esp");
        var provider = new SkyrimFaceRecordProvider(
            plugin,
            new WorkspacePath(@"K:\Actorwright\tests\fixtures\appearance.esp"),
            new Sha256Hash(new string('A', 64)));
        var textureSet = new SkyrimFaceTextureSetRecordRoute(
            new FormReference(plugin, new FormId(0x801)),
            provider,
            [
                "actorwright\\appearance\\tx00.dds",
                "actorwright\\appearance\\tx01.dds",
                "actorwright\\appearance\\tx02.dds",
                "actorwright\\appearance\\tx03.dds",
                "actorwright\\appearance\\tx04.dds",
                "actorwright\\appearance\\tx05.dds",
                "actorwright\\appearance\\tx06.dds",
                "actorwright\\appearance\\tx07.dds"
            ]);
        ImmutableArray<string> providerSlots =
        [
            "provider\\0.dds", "provider\\1.dds", "provider\\2.dds",
            "provider\\3.dds", "provider\\4.dds", "provider\\5.dds",
            "provider\\6.dds", "provider\\7.dds", "provider\\8.dds",
            "provider\\9.dds"
        ];
        var faceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/AppearanceFixture.esp/00000800.dds");
        var service = new FinalFaceGeomTexturePlanService();

        FinalFaceGeomTexturePlan planned = service.Plan(
            new FinalFaceGeomTexturePlanRequest(
                providerSlots, textureSet, UsesFaceTint: true, faceTint));
        Assert(planned.Accepted && planned.NifSlots.SequenceEqual(
            new[]
            {
                textureSet.RawTxSlots[0],
                textureSet.RawTxSlots[1],
                textureSet.RawTxSlots[3],
                textureSet.RawTxSlots[4],
                textureSet.RawTxSlots[5],
                textureSet.RawTxSlots[2],
                faceTint.Value.Replace('/', '\\'),
                textureSet.RawTxSlots[7],
                providerSlots[8],
                providerSlots[9]
            }, StringComparer.Ordinal),
            "Final FaceGeom texture composition did not apply the semantic TXST mapping, FaceTint-last rule, or provider extension slots.");
        Assert(planned.RequiredInputTextures.All(item =>
                   !item.Value.EndsWith("provider/0.dds",
                       StringComparison.OrdinalIgnoreCase) &&
                   !item.Value.EndsWith("tx06.dds",
                       StringComparison.OrdinalIgnoreCase)) &&
               planned.RequiredInputTextures.Any(item =>
                   item.Value.EndsWith("provider/8.dds",
                       StringComparison.OrdinalIgnoreCase)),
            "Final FaceGeom DDS closure retained superseded provider/TX06 routes or dropped provider slot 8.");

        FinalFaceGeomTexturePlan preserved = service.Plan(
            new FinalFaceGeomTexturePlanRequest(
                providerSlots, null, UsesFaceTint: false, faceTint));
        Assert(preserved.Accepted &&
               preserved.NifSlots.SequenceEqual(providerSlots),
            "Absent TNAM and absent FaceTint authority did not preserve provider routes byte-for-byte.");

        FinalFaceGeomTexturePlan tooShort = service.Plan(
            new FinalFaceGeomTexturePlanRequest(
                providerSlots.Take(7).ToImmutableArray(),
                textureSet, UsesFaceTint: false, faceTint));
        Assert(!tooShort.Accepted && tooShort.Diagnostics.Any(item =>
                item.Code == "carrier-texture-slot-capacity"),
            "A TXST override incorrectly admitted a provider texture array shorter than eight slots.");
    }

    private static async Task TestWinningHeadPartTextureSetRouting()
    {
        string root = Path.Combine(AppContext.BaseDirectory,
            "record-appearance-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string pluginPath = Path.Combine(dataRoot, "AppearanceFixture.esp");
            WriteAppearancePlugin(pluginPath);
            var plugin = new PluginName("AppearanceFixture.esp");
            var authority = new SkyrimFaceRecordPluginAuthority(
                plugin,
                new WorkspacePath(pluginPath),
                AppearanceHashFile(pluginPath));
            var workspaceRoot = new WorkspacePath(root);
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var resolver = new BethesdaSkyrimFaceRecordRouteResolver(
                policy, workspaceRoot);
            var race = new FormReference(plugin, new FormId(0x900));

            SkyrimFaceRecordRouteResult resolved = await resolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    race,
                    NpcSex.Female,
                    [new SkyrimFaceRecordHeadPartSelection(
                        new FormReference(plugin, new FormId(0x800)), [])],
                    [authority]),
                CancellationToken.None);
            string[] expectedRawSlots =
            [
                "actorwright\\appearance\\diffuse.dds",
                "actorwright\\appearance\\normal.dds",
                "actorwright\\appearance\\environment-mask.dds",
                "actorwright\\appearance\\glow.dds",
                "actorwright\\appearance\\height.dds",
                "actorwright\\appearance\\environment.dds",
                "actorwright\\appearance\\multilayer.dds",
                "actorwright\\appearance\\backlight.dds"
            ];
            Assert(resolved.Accepted && resolved.Route is not null &&
                   resolved.Route.HeadParts.Single().TextureSet is { } txst &&
                   txst.RawTxSlots.SequenceEqual(
                       expectedRawSlots, StringComparer.Ordinal),
                $"Winning HDPT TNAM did not retain the exact raw TX00-TX07 vector: {GeometryDiagnostics(resolved.Diagnostics)}");

            SkyrimFaceRecordRouteResult absent = await resolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    race,
                    NpcSex.Female,
                    [new SkyrimFaceRecordHeadPartSelection(
                        new FormReference(plugin, new FormId(0x802)), [])],
                    [authority]),
                CancellationToken.None);
            Assert(absent.Accepted &&
                   absent.Route!.HeadParts.Single().TextureSet is null,
                "An HDPT with no TNAM did not preserve provider texture authority.");

            SkyrimFaceRecordRouteResult missing = await resolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    race,
                    NpcSex.Female,
                    [new SkyrimFaceRecordHeadPartSelection(
                        new FormReference(plugin, new FormId(0x803)), [])],
                    [authority]),
                CancellationToken.None);
            Assert(!missing.Accepted && missing.Diagnostics.Any(item =>
                    item.Code == "headpart-texture-set-unavailable"),
                "A declared unresolved HDPT TNAM did not fail closed with its typed diagnostic.");

            SkyrimFaceRecordRouteResult deleted = await resolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    race,
                    NpcSex.Female,
                    [new SkyrimFaceRecordHeadPartSelection(
                        new FormReference(plugin, new FormId(0x805)), [])],
                    [authority]),
                CancellationToken.None);
            Assert(!deleted.Accepted && deleted.Diagnostics.Any(item =>
                    item.Code == "headpart-texture-set-unavailable"),
                "A declared deleted HDPT TNAM resurrected stale provider appearance.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteAppearancePlugin(string path)
    {
        var key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var textureKey = new FormKey(key, 0x801);
        mod.TextureSets.Add(new TextureSet(textureKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "AppearanceTextureSet",
            Diffuse = "Textures/actorwright/appearance/diffuse.dds",
            NormalOrGloss = "Textures/actorwright/appearance/normal.dds",
            EnvironmentMaskOrSubsurfaceTint =
                "Textures/actorwright/appearance/environment-mask.dds",
            GlowOrDetailMap = "Textures/actorwright/appearance/glow.dds",
            Height = "Textures/actorwright/appearance/height.dds",
            Environment = "Textures/actorwright/appearance/environment.dds",
            Multilayer = "Textures/actorwright/appearance/multilayer.dds",
            BacklightMaskOrSpecular =
                "Textures/actorwright/appearance/backlight.dds"
        });
        var deletedTextureKey = new FormKey(key, 0x804);
        mod.TextureSets.Add(new TextureSet(
            deletedTextureKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "DeletedAppearanceTextureSet",
            Diffuse = "Textures/actorwright/appearance/deleted.dds",
            IsDeleted = true
        });
        mod.HeadParts.Add(AppearanceHeadPart(
            new FormKey(key, 0x800), "WithTexture", textureKey));
        mod.HeadParts.Add(AppearanceHeadPart(
            new FormKey(key, 0x802), "WithoutTexture", null));
        mod.HeadParts.Add(AppearanceHeadPart(
            new FormKey(key, 0x803), "MissingTexture",
            new FormKey(key, 0x8FF)));
        mod.HeadParts.Add(AppearanceHeadPart(
            new FormKey(key, 0x805), "DeletedTexture",
            deletedTextureKey));
        mod.Races.Add(new Race(new FormKey(key, 0x900),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "AppearanceRace"
        });
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static HeadPart AppearanceHeadPart(
        FormKey key,
        string editorId,
        FormKey? textureSet)
    {
        var part = new HeadPart(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            Name = editorId,
            Flags = HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Face,
            Model = new Model
            {
                File = $"meshes/actorwright/appearance/{editorId}.nif"
            }
        };
        if (textureSet is { } route)
            part.TextureSet = new FormLinkNullable<ITextureSetGetter>(route);
        return part;
    }

    private static Sha256Hash AppearanceHashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static async Task TestOnePromotionNpcTransaction()
    {
        string root = Path.Combine("K:\\Actorwright", "artifacts", "test-work",
            "one-promotion-" + Guid.NewGuid().ToString("N"));
        string dataPath = Path.Combine(root, "Data");
        string presetPath = Path.Combine(root, "source.jslot");
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(presetPath, "{}");
        var presetHash = AppearanceHashFile(presetPath);
        var plugin = new PluginName("AppearanceFixture.esp");
        string pluginPath = Path.Combine(dataPath, plugin.Value);
        File.WriteAllBytes(pluginPath, [1]);
        var pluginHash = AppearanceHashFile(pluginPath);
        var race = new FormReference(plugin, new FormId(0x900));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            null!,
            null!,
            new WorkspacePath(Path.Combine(root, "final-success")),
            plugin,
            new NpcCreationIdentity(new EditorId("AppearanceNpc"),
                new NpcName("Appearance NPC")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female, NpcCreationRole.StaticValidation,
                true, false, false, false, false),
            new SkyrimNpcCreationReferences(
                race, race, race, race, null),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1),
                0, 0, 0, 1, 1, 100, 0, 0,
                100, 100, 100, 1F, 50F, 0));
        var current = new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(
                new WorkspacePath(presetPath), presetHash));
        var preset = new PresetDocument(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            null!,
            presetHash,
            []);
        var authority = new SkyrimFaceRecordPluginAuthority(
            plugin, new WorkspacePath(pluginPath), pluginHash);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(@"K:\Actorwright"),
            new WorkspacePath(@"F:\ExampleGame"));
        try
        {
            async Task<RaceMenuJslotNpcBuildResult> Execute(bool failAfterWrite,
                string suffix)
            {
                WorkspacePath finalRoot = new(Path.Combine(root, "final-" + suffix));
                WorkspacePath companionRoot = new(Path.Combine(root,
                    "companion-" + suffix));
                RaceMenuNpcExecutionRequest requestCurrent = current with
                {
                    Build = current.Build with { OutputRoot = finalRoot }
                };
                var service = new RaceMenuJslotNpcBuildService(
                    new FixedAppearancePresetService(preset),
                    new FixedAppearancePluginLoader(authority),
                    new FixedAppearanceCompanionService(),
                    new FixedAppearanceSelectionService(),
                    new ControlledAppearanceNpcBuildService(failAfterWrite),
                    policy,
                    new WorkspacePath(@"K:\Actorwright"));
                return await service.ExecuteAsync(
                    new RaceMenuJslotNpcBuildRequest(
                        requestCurrent,
                        new WorkspacePath(presetPath),
                        presetHash,
                        new WorkspacePath(dataPath),
                        [plugin],
                        companionRoot),
                    null,
                    CancellationToken.None);
            }

            RaceMenuJslotNpcBuildResult failed = await Execute(
                failAfterWrite: true, "failure");
            Assert(!failed.Completed &&
                   !Directory.Exists(Path.Combine(root, "final-failure")) &&
                   !Directory.Exists(Path.Combine(root, "companion-failure")),
                "A failure after staged package bytes leaked final or companion output.");

            RaceMenuJslotNpcBuildResult succeeded = await Execute(
                failAfterWrite: false, "success");
            Assert(succeeded.Completed &&
                   Directory.Exists(Path.Combine(root, "final-success")) &&
                   !Directory.Exists(Path.Combine(root, "companion-success")) &&
                   succeeded.Execution?.Build?.Artifact is { } artifact &&
                   artifact.OutputRoot.Value == Path.Combine(root, "final-success") &&
                   artifact.Plugin.IsUnder(artifact.OutputRoot) &&
                   File.Exists(artifact.Plugin.Value),
                "The complete package was not promoted once with final response paths and no durable companion directory.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedAppearancePresetService(PresetDocument preset) :
        IPresetService
    {
        public ValueTask<PresetParseResult> InspectAsync(
            PresetParseRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PresetParseResult(preset, []));

        public ValueTask<PresetExportResult> ExportAsync(
            PresetExportRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PresetDiffResult> DiffAsync(
            PresetDiffRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedAppearancePluginLoader(
        SkyrimFaceRecordPluginAuthority authority) :
        ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(
                true, [authority], []));
    }

    private sealed class FixedAppearanceCompanionService :
        IRaceMenuJslotCompanionBuildService
    {
        public ValueTask<RaceMenuJslotCompanionBuildResult> BuildAsync(
            RaceMenuJslotCompanionBuildRequest request,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.CompanionRoot.Value);
            string companions = Path.Combine(request.CompanionRoot.Value,
                "companions");
            Directory.CreateDirectory(companions);
            WorkspacePath preset = Write("source.jslot");
            WorkspacePath nif = Write("source.nif");
            WorkspacePath dds = Write("source.dds");
            Sha256Hash hash = AppearanceHashFile(nif.Value);
            var export = new RaceMenuPresetCompanionExport(
                preset, AppearanceHashFile(preset.Value),
                nif, hash,
                dds, AppearanceHashFile(dds.Value));
            var target = new FaceGenBakeTarget(
                new FormId(0x800),
                new PluginName("AppearanceFixture.esp"),
                new PluginName("AppearanceFixture.esp"),
                [new PluginName("AppearanceFixture.esp")],
                "AppearanceNpc", "Appearance NPC", NpcSex.Female,
                new FormReference(new PluginName("AppearanceFixture.esp"),
                    new FormId(0x900)),
                [], 50F);
            var dependency = new SkyrimAssetAuthority(
                "loose", AssetProviderKind.Loose, nif, hash,
                new AssetPath("meshes/actorwright/dependency.nif"),
                new FileInfo(nif.Value).Length, hash);
            var artifact = new FaceGenNpcBakeArtifact(
                target, nif, hash, checked((int)new FileInfo(nif.Value).Length),
                dds, AppearanceHashFile(dds.Value),
                checked((int)new FileInfo(dds.Value).Length), false)
            {
                ExternalDependencyAuthorities = [dependency]
            };
            return ValueTask.FromResult(new RaceMenuJslotCompanionBuildResult(
                true, request.CompanionRoot, export, null,
                new FaceGenNpcBakeResult(
                    FaceGenNpcBakeStatus.Baked, target, artifact, []), []));

            WorkspacePath Write(string name)
            {
                var path = new WorkspacePath(Path.Combine(companions, name));
                File.WriteAllBytes(path.Value, [1, 2, 3]);
                return path;
            }
        }
    }

    private sealed class FixedAppearanceSelectionService :
        IRaceMenuPresetSelectionTransactionService
    {
        public ValueTask<RaceMenuPresetSelectionTransactionResult> RebindAsync(
            RaceMenuPresetSelectionTransactionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuPresetSelectionTransactionResult(
                true, request.CurrentRequest, request.CandidateParent, []));
    }

    private sealed class ControlledAppearanceNpcBuildService(bool failAfterWrite) :
        IRaceMenuNpcBuildService
    {
        public ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
            RaceMenuNpcExecutionRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            WorkspacePath root = request.Build.OutputRoot;
            Directory.CreateDirectory(root.Value);
            WorkspacePath plugin = Write("Data/AppearanceFixture.esp");
            WorkspacePath nif = Write("Data/meshes/appearance.nif");
            WorkspacePath dds = Write("Data/textures/appearance.dds");
            WorkspacePath manifest = Write("npcmanager-package.json");
            Sha256Hash hash = AppearanceHashFile(plugin.Value);
            var artifact = new BlankNpcBuildArtifact(
                "1", "blank-npc-package", "STATIC_PASS_RUNTIME_REQUIRED",
                root, plugin, hash, new FormId(0x800),
                nif, AppearanceHashFile(nif.Value),
                dds, AppearanceHashFile(dds.Value),
                manifest, AppearanceHashFile(manifest.Value), false);
            var diagnostics = failAfterWrite
                ? ImmutableArray.Create(new Diagnostic(
                    "controlled-after-staging-failure",
                    DiagnosticSeverity.Error,
                    "Controlled failure after staged NIF/DDS/package creation."))
                : [];
            var build = new BlankNpcBuildResult(
                !failAfterWrite, artifact, null, null, null, null, null,
                diagnostics);
            return ValueTask.FromResult(new RaceMenuNpcExecutionResult(
                !failAfterWrite, null, null, null, null, null, build,
                diagnostics));

            WorkspacePath Write(string relative)
            {
                var path = new WorkspacePath(Path.Combine(root.Value,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
                File.WriteAllBytes(path.Value, [4, 5, 6]);
                return path;
            }
        }
    }
}
