using System.Collections.Immutable;
using System.Drawing;
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
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimRecordCarrierCoherence(bool shaderlessDummy = false)
    {
        string rootPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests",
            "record-carrier-" + Guid.NewGuid().ToString("N"));
        var root = new WorkspacePath(rootPath);
        var data = new WorkspacePath(Path.Combine(rootPath, "Data"));
        Directory.CreateDirectory(data.Value);
        try
        {
            var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(Path.Combine(rootPath, "protected")));
            var plugin = new PluginName("Coherence.esp");
            var overridePlugin = new PluginName("CoherenceOverride.esp");
            string pluginPath = Path.Combine(data.Value, plugin.Value);
            var key = ModKey.FromNameAndExtension(plugin.Value);
            var race = new FormReference(plugin, new FormId(0x900));
            FormReference[] parts = [new(plugin, new FormId(0x800)), new(plugin, new FormId(0x801)), new(plugin, new FormId(0x802))];
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Races.Add(new Race(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoherenceRace", HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
            });
            mod.Colors.Add(new ColorRecord(new FormKey(key, 0x901), SkyrimRelease.SkyrimSE)
            {
                EditorID = "RecordHairColor", Color = Color.FromArgb(255, 0x22, 0x22, 0x22)
            });
            mod.TextureSets.Add(new TextureSet(new FormKey(key, 0x902), SkyrimRelease.SkyrimSE)
            {
                EditorID = "RecordGreenEyes", Diffuse = "eyes/EyeGreen.dds"
            });
            foreach ((uint id, string name, HeadPart.TypeEnum type, string model) in new[]
            {
                (0x800u, "CoherenceFace", HeadPart.TypeEnum.Face, "face"),
                (0x801u, "CoherenceHair", HeadPart.TypeEnum.Hair, "hair"),
                (0x802u, "CoherenceEyes", HeadPart.TypeEnum.Eyes, "eyes")
            })
            {
                var headPart = new HeadPart(new FormKey(key, id), SkyrimRelease.SkyrimSE)
                {
                    EditorID = name, Type = type, Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female,
                    Model = new Model { File = new AssetLink<SkyrimModelAssetType>("fixture/" + model + ".nif") }
                };
                if (type == HeadPart.TypeEnum.Eyes) headPart.TextureSet.SetTo(new FormKey(key, 0x902));
                mod.HeadParts.Add(headPart);
                WriteTopologyAsset(data.Value, new AssetPath("meshes/fixture/" + model + ".nif"),
                    WriteTopologyModel(141, type == HeadPart.TypeEnum.Eyes ? "textures/eyes/EyeBrown.dds" : null,
                        type == HeadPart.TypeEnum.Hair ? 0x222222u : null));
            }
            if (shaderlessDummy)
            {
                var dummy = new HeadPart(new FormKey(key, 0x803), SkyrimRelease.SkyrimSE)
                {
                    EditorID = "CoherenceLensDummy", Type = HeadPart.TypeEnum.Eyes,
                    Flags = HeadPart.Flag.Female | (HeadPart.Flag)8, // HDPT extra-part flag
                    Model = new Model { File = new AssetLink<SkyrimModelAssetType>("fixture/LensDummy.nif") }
                };
                mod.HeadParts.Add(dummy);
                mod.HeadParts.Single(part => part.FormKey.ID == 0x802).ExtraParts.Add(
                    new FormLink<IHeadPartGetter>(dummy.FormKey));
                byte[] dummyBytes = WriteTopologyModel(141, shaderless: true, shapeName: "FixtureLens");
                var dummyPath = new AssetPath("meshes/fixture/LensDummy.nif");
                WriteTopologyAsset(data.Value, dummyPath, dummyBytes);
                var intake = new SseSelectedHeadpartNifGeometryReader().Read(new(dummyPath,
                    TopologyHash(dummyBytes), [.. dummyBytes]));
                Assert(intake.Accepted && intake.Diagnostics.Any(item =>
                        item.Code == "sse-headpart-nif-shaderless-dummy-admitted"),
                    "Synthetic dummy must pass real reader admission before native build: " + Format(intake.Diagnostics));
                var ordinary = new SseSelectedHeadpartNifGeometryReader().Read(new(
                    new AssetPath("meshes/fixture/LensOrdinary.nif"), TopologyHash(dummyBytes), [.. dummyBytes]));
                Assert(!ordinary.Accepted, "An unadmitted shaderless model must retain its material refusal.");
            }
            var npc = new Npc(new FormKey(key, 0xA00), SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoherenceNpc", Race = new FormLink<IRaceGetter>(new FormKey(key, 0x900)),
                HairColor = new FormLinkNullable<IColorRecordGetter>(new FormKey(key, 0x901)),
                Configuration = new NpcConfiguration { Flags = NpcConfiguration.Flag.Female }
            };
            foreach (FormReference part in parts) npc.HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(key, part.FormId.Value)));
            mod.Npcs.Add(npc);
            WriteCoherencePlugin(mod, pluginPath);
            var overrideKey = ModKey.FromNameAndExtension(overridePlugin.Value);
            var winner = new SkyrimMod(overrideKey, SkyrimRelease.SkyrimSE);
            winner.ModHeader.MasterReferences.Add(new MasterReference { Master = key });
            winner.Npcs.Add(npc.DeepCopy());
            winner.Colors.Add(new ColorRecord(new FormKey(key, 0x901), SkyrimRelease.SkyrimSE)
            {
                EditorID = "WinningHairColor", Color = Color.FromArgb(255, 0x5C, 0x58, 0x50)
            });
            winner.Colors.Add(new ColorRecord(new FormKey(overrideKey, 0x901), SkyrimRelease.SkyrimSE)
            {
                EditorID = "DistinctOwnerSameLocalId", Color = Color.FromArgb(255, 0xAA, 0xBB, 0xCC)
            });
            WriteCoherencePlugin(winner, Path.Combine(data.Value, overridePlugin.Value));
            Directory.CreateDirectory(Path.Combine(data.Value, "textures", "eyes"));
            foreach (string texture in new[] { "EyeGreen.dds", "EyeBrown.dds" })
                WriteSolidBgra8Dds(Path.Combine(data.Value, "textures", "eyes", texture), 1, 1, 0, 255, 0, 255);

            var indexer = new BethesdaAssetIndexer();
            var loader = new SkyrimFaceRecordPluginAuthorityLoader(policy, root);
            var content = new SkyrimAssetContentResolver(policy, root);
            var assetPlanner = new SkyrimAssetAuthorityPlanner(indexer, policy, root);
            var materializer = new SseFaceGeomCarrierMaterializationService(new SseFaceGeomCarrierAssembler(), policy, root);
            var geom = new SkyrimNativeFaceGeomBuildService(loader,
                new BethesdaSkyrimFaceRecordRouteResolver(policy, root),
                new BethesdaSkyrimFaceMorphSnapshotService(policy, root), assetPlanner, content,
                new SkyrimRaceMenuCatalogAuthorityLoader(indexer, assetPlanner, content),
                new NpcManager.FaceGen.RaceMenuSliderCatalogParserCore(), new SseSelectedHeadpartNifGeometryReader(),
                new NpcManager.FaceGen.SseRaceMenuFaceBakeService(), materializer);
            var decoder = new InProcessDdsTextureDecoder(root);
            var tintRecords = new BethesdaSkyrimNativeFaceTintRecordResolver(policy, root);
            var tint = new SkyrimNativeFaceTintPipelineService(loader, tintRecords,
                new SkyrimNativeFaceTintAuthorityPlanner(indexer, policy, root),
                new SkyrimNativeFaceTintMaterializationService(tintRecords, content,
                    new SkyrimNativeFaceTintBuildService(policy, root, decoder)));
            var service = new SkyrimFaceGenNpcBakeService(geom, tint, materializer, decoder, policy, root);
            var target = new FaceGenBakeTarget(new FormId(0xA00), plugin, overridePlugin, [plugin, overridePlugin],
                "CoherenceNpc", "Coherence NPC", NpcSex.Female, race, [.. parts], 50F);
            var request = new FaceGenNpcBakeRequest(GameEdition.SkyrimSpecialEdition, data, [plugin, overridePlugin], target,
                new WorkspacePath(Path.Combine(rootPath, "baseline")));
            var errors = new List<string>();
            FaceGenNpcBakeResult baseline = await service.BakeAsync(request, CancellationToken.None);
            Assert(baseline.Status == FaceGenNpcBakeStatus.Baked && baseline.Artifact is not null,
                "Real record/carrier fixture failed before coherence checks: " + Format(baseline.Diagnostics));
            FaceGenNpcBakeArtifact artifact = baseline.Artifact!;
            var hair = await new FaceGeomHairRegionsAnalyzer(root).AnalyzeAsync(artifact.FaceGeomNif,
                artifact.FaceGeomSha256, null, CancellationToken.None);
            if (!hair.Regions.Any(region => region.Name == "CoherenceHair" && region.CurrentColor == "#5C5850"))
                errors.Add("Record CLFM #5C5850 was not applied to carrier hair: " + string.Join(", ", hair.Regions.Select(region => region.CurrentColor)));
            byte[] carrier = File.ReadAllBytes(artifact.FaceGeomNif.Value);
            var reopened = new SseSelectedHeadpartNifGeometryReader().Read(new(
                new AssetPath("meshes/fixture/carrier.nif"), artifact.FaceGeomSha256, [.. carrier]));
            Assert(reopened.Accepted && reopened.Document is not null,
                "Final carrier did not independently reopen: " + Format(reopened.Diagnostics));
            if (shaderlessDummy)
                Assert(reopened.Document!.Shapes.Length == 3 &&
                       reopened.Document.Shapes.All(shape => !shape.Name.Contains("Dummy", StringComparison.Ordinal)) &&
                       baseline.Diagnostics.Any(item => item.Code == "sse-headpart-nif-shaderless-dummy-admitted"),
                    "Native bake must omit only the admitted dummy and retain all three renderable shapes.");
            Assert(reopened.Document!.Shapes.Single(shape => shape.Name == "CoherenceEyes").Materials
                    .SelectMany(binding => binding.TextureSlots).Any(slot => slot.Slot == 0 &&
                        slot.Path.Value.Equals("textures/eyes/EyeGreen.dds", StringComparison.OrdinalIgnoreCase)),
                "Pre-existing native eye TXST override did not reach final carrier slot 0.");

            foreach ((string name, FaceGenNpcBakeRequest altered) in new[]
            {
                ("stale-pnam", request with { Target = target with { HeadParts = [parts[0], parts[1]] } }),
                ("stale-color", request with { EffectiveHairColorPackedRgb = 0x222222 })
            })
            {
                var output = new WorkspacePath(Path.Combine(rootPath, name));
                FaceGenNpcBakeResult result = await service.BakeAsync(altered with { OutputDataRoot = output }, CancellationToken.None);
                if (result.Status != FaceGenNpcBakeStatus.Failed || !result.Diagnostics.Any(d => d.Code == "sse-face-bake-record-carrier-mismatch") || Directory.Exists(output.Value))
                    errors.Add(name + " was not refused against winning records before staging: " + Format(result.Diagnostics));
            }
            if (shaderlessDummy)
            {
                var forgedService = new SkyrimFaceGenNpcBakeService(new ForgedDummyNative(geom),
                    tint, materializer, decoder, policy, root);
                var forgedRoot = new WorkspacePath(Path.Combine(rootPath, "forged-dummy"));
                var forged = await forgedService.BakeAsync(request with { OutputDataRoot = forgedRoot }, CancellationToken.None);
                Assert(forged.Status == FaceGenNpcBakeStatus.Failed && !Directory.Exists(forgedRoot.Value) &&
                       forged.Diagnostics.Any(item => item.Code == "sse-face-bake-record-carrier-mismatch"),
                    "A claimed dummy shape must be independently matched to the actual bound model.");
            }
            var staleCarrierService = new SkyrimFaceGenNpcBakeService(new StaleColorCoherenceNative(geom),
                tint, materializer, decoder, policy, root);
            var staleCarrierRoot = new WorkspacePath(Path.Combine(rootPath, "stale-carrier-color"));
            var staleCarrier = await staleCarrierService.BakeAsync(request with { OutputDataRoot = staleCarrierRoot }, CancellationToken.None);
            if (staleCarrier.Status != FaceGenNpcBakeStatus.Failed ||
                !staleCarrier.Diagnostics.Any(d => d.Code == "sse-face-bake-record-carrier-mismatch") ||
                Directory.Exists(staleCarrierRoot.Value))
                errors.Add("An independently valid native carrier with stale colour escaped record comparison: " + Format(staleCarrier.Diagnostics));
            mod.Npcs.Single(row => row.FormKey.ID == 0xA00).HairColor.SetTo(FormKey.Null);
            winner.Npcs.Single(row => row.FormKey.ID == 0xA00).HairColor.SetTo(FormKey.Null);
            WriteCoherencePlugin(mod, pluginPath);
            WriteCoherencePlugin(winner, Path.Combine(data.Value, overridePlugin.Value));
            var noHairColorRoot = new WorkspacePath(Path.Combine(rootPath, "no-hair-color"));
            var noHairColor = await service.BakeAsync(request with { OutputDataRoot = noHairColorRoot }, CancellationToken.None);
            if (noHairColor.Status != FaceGenNpcBakeStatus.Baked || noHairColor.Artifact is null)
                errors.Add("An owned Hair part with no optional HCLF/CLFM was refused: " + Format(noHairColor.Diagnostics));
            mod.Npcs.Single(row => row.FormKey.ID == 0xA00).HairColor.SetTo(new FormKey(key, 0x901));
            winner.Npcs.Single(row => row.FormKey.ID == 0xA00).HairColor.SetTo(new FormKey(key, 0x901));
            WriteCoherencePlugin(mod, pluginPath);
            WriteCoherencePlugin(winner, Path.Combine(data.Value, overridePlugin.Value));
            var renamedService = new SkyrimFaceGenNpcBakeService(new MutatingCoherenceNative(geom, () =>
            {
                mod.HeadParts.Single(part => part.FormKey.ID == 0x802).EditorID = "RenamedEyes";
                WriteCoherencePlugin(mod, pluginPath);
            }), tint, materializer, decoder, policy, root);
            var renamedRoot = new WorkspacePath(Path.Combine(rootPath, "renamed-after-bake"));
            var renamedResult = await renamedService.BakeAsync(request with { OutputDataRoot = renamedRoot }, CancellationToken.None);
            if (renamedResult.Status != FaceGenNpcBakeStatus.Failed ||
                !renamedResult.Diagnostics.Any(d => d.Code == "sse-face-bake-record-carrier-mismatch") ||
                Directory.Exists(renamedRoot.Value))
                errors.Add("HDPT rename after native bake did not invalidate final carrier: " + Format(renamedResult.Diagnostics));
            Assert(errors.Count == 0, string.Join(" | ", errors));
        }
        finally
        {
            string parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests") + Path.DirectorySeparatorChar;
            Assert(Path.GetFullPath(rootPath).StartsWith(parent, StringComparison.OrdinalIgnoreCase), "Coherence cleanup escaped owned tests root.");
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void WriteCoherencePlugin(SkyrimMod mod, string path) => mod.WriteToBinary(new FilePath(path),
        new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });

    private sealed class ForgedDummyNative(ISkyrimNativeFaceGeomBuildService inner) : ISkyrimNativeFaceGeomBuildService
    {
        public async ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(
            SkyrimNativeFaceGeomBuildRequest request, CancellationToken token)
        {
            var result = await inner.BuildAsync(request, token);
            return result.Artifact is null ? result : result with
            {
                Artifact = result.Artifact with
                {
                    OmittedShaderlessDummies = result.Artifact.OmittedShaderlessDummies!.Value
                        .Select(item => item with { ModelShapeName = "ForgedLens" }).ToImmutableArray()
                }
            };
        }
    }

    private sealed class MutatingCoherenceNative(ISkyrimNativeFaceGeomBuildService inner, Action mutate) : ISkyrimNativeFaceGeomBuildService
    {
        public async ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(SkyrimNativeFaceGeomBuildRequest request, CancellationToken token)
        {
            var result = await inner.BuildAsync(request, token);
            if (result.Written) mutate();
            return result;
        }
    }

    private sealed class StaleColorCoherenceNative(ISkyrimNativeFaceGeomBuildService inner) : ISkyrimNativeFaceGeomBuildService
    {
        public ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(SkyrimNativeFaceGeomBuildRequest request, CancellationToken token) =>
            inner.BuildAsync(request with { EffectiveHairColorPackedRgb = 0x222222 }, token);
    }
}
