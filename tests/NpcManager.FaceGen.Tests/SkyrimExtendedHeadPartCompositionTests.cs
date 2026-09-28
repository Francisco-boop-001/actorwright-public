using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
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
    private const uint ExtendedHeadPartPnam = 71; // UBE eye wetness (Rachel R2); Nerissa hits 72/41.

    /// <summary>
    /// WI-NPC-3 carrier proof: a selected HDPT whose PNAM is a mod-defined type
    /// (71) is routed by its model, not by the closed enum. With a shader-bound
    /// model it is composed into the FaceGen carrier; as an explicitly named
    /// shaderless dummy lens its omission is recorded on the artifact.
    /// </summary>
    private static async Task TestExtendedHeadPartCarrierComposition()
    {
        foreach (bool shaderlessDummy in new[] { false, true })
        {
            string rootPath = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests",
                "extended-hdpt-" + Guid.NewGuid().ToString("N"));
            var root = new WorkspacePath(rootPath);
            var data = new WorkspacePath(Path.Combine(rootPath, "Data"));
            Directory.CreateDirectory(data.Value);
            try
            {
                var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(Path.Combine(rootPath, "protected")));
                var plugin = new PluginName("Extended.esp");
                var key = ModKey.FromNameAndExtension(plugin.Value);
                var race = new FormReference(plugin, new FormId(0x900));
                var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
                mod.Races.Add(new Race(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
                {
                    EditorID = "ExtendedRace", HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
                });
                string extendedModel = shaderlessDummy ? "fixture/WetnessLensDummy.nif" : "fixture/wetness.nif";
                foreach ((uint id, string name, HeadPart.TypeEnum type, string model) in new[]
                {
                    (0x800u, "ExtendedFace", HeadPart.TypeEnum.Face, "fixture/face.nif"),
                    (0x801u, "ExtendedHair", HeadPart.TypeEnum.Hair, "fixture/hair.nif"),
                    (0x802u, "ExtendedEyes", HeadPart.TypeEnum.Eyes, "fixture/eyes.nif"),
                    (0x803u, "ExtendedWetness", (HeadPart.TypeEnum)ExtendedHeadPartPnam, extendedModel)
                })
                {
                    mod.HeadParts.Add(new HeadPart(new FormKey(key, id), SkyrimRelease.SkyrimSE)
                    {
                        EditorID = name, Type = type, Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female,
                        Model = new Model { File = new AssetLink<SkyrimModelAssetType>(model) }
                    });
                    bool dummy = id == 0x803 && shaderlessDummy;
                    WriteTopologyAsset(data.Value, new AssetPath("meshes/" + model),
                        WriteTopologyModel(141, hairTint: type == HeadPart.TypeEnum.Hair ? 0x222222u : null,
                            shaderless: dummy, shapeName: dummy ? "WetnessLens" : name + "Shape"));
                }
                var upperReference = new FormReference(plugin, new FormId(0x804));
                var upperPart = mod.HeadParts.AddNew(new FormKey(key, upperReference.FormId.Value));
                upperPart.EditorID = "ExtendedUintMax";
                upperPart.Type = unchecked((HeadPart.TypeEnum)uint.MaxValue);
                upperPart.Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female;
                upperPart.Model = new Model { File = "fixture/uint-max.nif" };
                FormReference[] parts = [new(plugin, new FormId(0x800)), new(plugin, new FormId(0x801)),
                    new(plugin, new FormId(0x802)), new(plugin, new FormId(0x803))];
                var npc = new Npc(new FormKey(key, 0xA00), SkyrimRelease.SkyrimSE)
                {
                    EditorID = "ExtendedNpc", Race = new FormLink<IRaceGetter>(new FormKey(key, 0x900)),
                    Configuration = new NpcConfiguration { Flags = NpcConfiguration.Flag.Female }
                };
                foreach (FormReference part in parts)
                    npc.HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(key, part.FormId.Value)));
                mod.Npcs.Add(npc);
                string pluginPath = Path.Combine(data.Value, plugin.Value);
                WriteCoherencePlugin(mod, pluginPath);

                NpcFaceSnapshot snapshot = BethesdaNpcFaceAdapter.Read(
                    GameEdition.SkyrimSpecialEdition, new WorkspacePath(pluginPath), new FormId(0xA00));
                NpcHeadPartSelection extendedSelection = snapshot.HeadParts.Single(item => item.Reference == parts[3]);
                Assert(extendedSelection.Type == NpcHeadPartType.Misc &&
                       extendedSelection.RawPnamType == ExtendedHeadPartPnam &&
                       extendedSelection.PnamType == ExtendedHeadPartPnam,
                    "The NPC face reader did not carry raw PNAM 71 beside its Misc projection.");
                NpcHeadPartProvider extendedProvider = BethesdaNpcFaceAdapter.ReadProviders(
                    GameEdition.SkyrimSpecialEdition, new WorkspacePath(pluginPath), [extendedSelection]).Single();
                Assert(extendedProvider.Type == NpcHeadPartType.Misc &&
                       extendedProvider.RawPnamType == ExtendedHeadPartPnam &&
                       extendedProvider.PnamType == ExtendedHeadPartPnam,
                    "The head-part provider reader did not carry raw PNAM 71 beside its Misc projection.");

                var upperSelection = new NpcHeadPartSelection(upperReference, NpcHeadPartType.Misc)
                    { RawPnamType = uint.MaxValue };
                NpcHeadPartProvider upperProvider = BethesdaNpcFaceAdapter.ReadProviders(
                    GameEdition.SkyrimSpecialEdition, new WorkspacePath(pluginPath), [upperSelection]).Single();
                Assert(upperProvider.Type == NpcHeadPartType.Misc &&
                       upperProvider.RawPnamType == uint.MaxValue,
                    "The provider reader did not preserve PNAM uint.MaxValue as an unsigned value.");
                var creationAppearance = new FullyAuthoredSkyrimNpcAppearanceSource(
                    [(SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(upperSelection)],
                    new OutputOwnedSkyrimNpcHairColor(new FormId(0x810), new SkyrimPackedRgb(0x11_22_33)),
                    new OutputOwnedSkyrimNpcFaceTextureSet(
                        new FormId(0x811),
                        new SkyrimPrivateHeadTexturePaths(
                            new AssetPath("textures/fixture/head.dds"),
                            new AssetPath("textures/fixture/head_msn.dds"),
                            new AssetPath("textures/fixture/head_sk.dds"),
                            new AssetPath("textures/fixture/head_detail.dds"),
                            new AssetPath("textures/fixture/head_s.dds"))),
                    50F,
                    new SkyrimFaceMorphPatch([], 0F, []),
                    new SkyrimFaceTintPatch([]),
                    new SkyrimQnamRgb(0F, 0F, 0F));
                BethesdaNpcCreationAppearanceReferenceValidation creationValidation =
                    BethesdaNpcCreationAdapter.ValidateAppearanceReferenceTypes(
                        data, creationAppearance, NpcSex.Female);
                Assert(creationValidation.HeadParts is
                       [{ Exists: true, TypeMatches: true, ModelSourceQualified: true }] &&
                       creationValidation.IsValid,
                    "Actual creation validation did not admit the PNAM uint.MaxValue provider.");

                var indexer = new BethesdaAssetIndexer();
                var content = new SkyrimAssetContentResolver(policy, root);
                var assetPlanner = new SkyrimAssetAuthorityPlanner(indexer, policy, root);
                var geom = new SkyrimNativeFaceGeomBuildService(
                    new SkyrimFaceRecordPluginAuthorityLoader(policy, root),
                    new BethesdaSkyrimFaceRecordRouteResolver(policy, root),
                    new BethesdaSkyrimFaceMorphSnapshotService(policy, root), assetPlanner, content,
                    new SkyrimRaceMenuCatalogAuthorityLoader(indexer, assetPlanner, content),
                    new NpcManager.FaceGen.RaceMenuSliderCatalogParserCore(), new SseSelectedHeadpartNifGeometryReader(),
                    new NpcManager.FaceGen.SseRaceMenuFaceBakeService(),
                    new SseFaceGeomCarrierMaterializationService(new SseFaceGeomCarrierAssembler(), policy, root));
                var request = new SkyrimNativeFaceGeomBuildRequest(
                    GameEdition.SkyrimSpecialEdition, data, [plugin],
                    new FaceGenBakeTarget(new FormId(0xA00), plugin, plugin, [plugin],
                        "ExtendedNpc", "Extended NPC", NpcSex.Female, race, [.. parts], 50F),
                    new AssetPath("textures/actors/character/facegendata/facetint/Extended.esp/00000A00.dds"),
                    new WorkspacePath(Path.Combine(rootPath, "carrier.nif")));
                SkyrimNativeFaceGeomBuildResult result = await geom.BuildAsync(request, CancellationToken.None);
                Assert(result.Written && result.Verified && result.Artifact is not null,
                    $"Native FaceGeom build with a PNAM {ExtendedHeadPartPnam} head part failed (dummy={shaderlessDummy}): " +
                    Format(result.Diagnostics));
                var extended = parts[3];
                var composed = result.Artifact!.Shapes.Where(shape => shape.HeadPart == extended).ToArray();
                var omitted = (result.Artifact.OmittedShaderlessDummies ?? [])
                    .Where(shape => shape.HeadPart == extended).ToArray();
                byte[] carrier = File.ReadAllBytes(request.OutputNif.Value);
                var reopened = new SseSelectedHeadpartNifGeometryReader().Read(new(
                    new AssetPath("meshes/fixture/carrier.nif"), TopologyHash(carrier), [.. carrier]));
                Assert(reopened.Accepted && reopened.Document is not null,
                    "The composed carrier did not independently reopen: " + Format(reopened.Diagnostics));
                if (shaderlessDummy)
                {
                    Assert(composed.Length == 0 && omitted is [{ ModelShapeName: "WetnessLens" }] &&
                           reopened.Document!.Shapes.Length == 3 &&
                           result.Diagnostics.Any(item => item.Code == "sse-headpart-nif-shaderless-dummy-admitted"),
                        "The extended shaderless dummy lens must be explicitly recorded as omitted: " +
                        Format(result.Diagnostics));
                }
                else
                {
                    Assert(composed is [{ EffectiveType: NpcHeadPartType.Misc, ModelShapeName: "ExtendedWetnessShape" }] &&
                           omitted.Length == 0 &&
                           reopened.Document!.Shapes.Any(shape => shape.Name == "ExtendedWetness"),
                        "The extended part's model must be composed into the carrier by model route: " +
                        Format(result.Diagnostics));
                }
            }
            finally
            {
                string parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests") + Path.DirectorySeparatorChar;
                Assert(Path.GetFullPath(rootPath).StartsWith(parent, StringComparison.OrdinalIgnoreCase),
                    "Extended head-part cleanup escaped owned tests root.");
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
