using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNativeTriTopology()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts",
            "tests", "native-topology-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        var workspace = new WorkspacePath(root);
        var policy = new KOnlyWorkspacePolicy(workspace,
            new WorkspacePath(Path.Combine(root, "protected")));
        var plugin = new PluginName("TopologyFixture.esp");
        string pluginPath = Path.Combine(dataRoot, plugin.Value);
        File.WriteAllBytes(pluginPath, [1, 2, 3, 4]);
        Sha256Hash pluginHash = TopologyHash(File.ReadAllBytes(pluginPath));
        var provider = new SkyrimFaceRecordProvider(plugin,
            new WorkspacePath(pluginPath), pluginHash);
        var race = new FormReference(plugin, new FormId(0x900));
        var face = new FormReference(plugin, new FormId(0x800));
        var brow = new FormReference(plugin, new FormId(0x801));
        var faceModel = new AssetPath("meshes/fixture/face.nif");
        var browModel = new AssetPath("meshes/fixture/brow.nif");
        var raceTri = new AssetPath("meshes/fixture/brow-race.tri");
        var dialogueTri = new AssetPath("meshes/fixture/brow-dialogue.tri");
        var chargenTri = new AssetPath("meshes/fixture/brow-chargen.tri");
        var extension = new AssetPath(
            "meshes/actors/character/FaceGenMorphs/morphs/optional.tri");
        AssetPath[] tris = [dialogueTri, raceTri, chargenTri, extension];
        byte[] model = WriteTopologyModel(141);
        WriteTopologyAsset(dataRoot, faceModel, model);
        WriteTopologyAsset(dataRoot, browModel, model);
        var parsed = new SseSelectedHeadpartNifGeometryReader().Read(
            new SseSelectedHeadpartNifGeometryReadRequest(
                browModel, TopologyHash(model), [.. model]));
        Assert(parsed.Accepted && parsed.Document?.Shapes.Single().VertexCount == 141,
            "Synthetic 141-vertex dynamic model failed intake: " + Format(parsed.Diagnostics));
        var route = new SkyrimFaceRecordRoute(
            new SkyrimRaceFaceRecordRoute(race, provider, "FixtureRace", null,
                "NordRace", [], [], [], []), [face, brow],
            [new SkyrimFaceHeadPartRecordRoute(face, provider, "FixtureFace",
                NpcHeadPartType.Face, NpcHeadPartType.Face, faceModel, [], [], null,
                0, IsSelected: true, IsRaceDefault: false),
             new SkyrimFaceHeadPartRecordRoute(brow, provider, "FixtureBrow",
                NpcHeadPartType.Eyebrows, NpcHeadPartType.Eyebrows, browModel,
                [new(SkyrimHdptTriRole.RaceMorph, raceTri),
                 new(SkyrimHdptTriRole.Mesh, dialogueTri),
                 new(SkyrimHdptTriRole.CharGen, chargenTri)], [], null,
                0, IsSelected: true, IsRaceDefault: false)]);
        byte[] ini = Encoding.ASCII.GetBytes(
            "extension = fixture/brow-chargen.tri, optional.tri\n");
        var catalog = new SkyrimRaceMenuCatalogParseRequest([plugin],
            [new SkyrimRaceMenuCatalogAsset(new AssetPath(
                "meshes/actors/character/FaceGenMorphs/TopologyFixture.esp/morphs.ini"),
                TopologyHash(ini), [.. ini])]);
        var authority = new TopologyAuthority(plugin, pluginPath, pluginHash, route, catalog);
        var service = new SkyrimNativeFaceGeomBuildService(authority, authority, authority,
            new SkyrimAssetAuthorityPlanner(new BethesdaAssetIndexer(), policy, workspace),
            new SkyrimAssetContentResolver(policy, workspace), authority,
            new NpcManager.FaceGen.RaceMenuSliderCatalogParserCore(),
            new SseSelectedHeadpartNifGeometryReader(),
            new NpcManager.FaceGen.SseRaceMenuFaceBakeService(),
            new SseFaceGeomCarrierMaterializationService(
                new SseFaceGeomCarrierAssembler(), policy, workspace));
        var request = new SkyrimNativeFaceGeomBuildRequest(
            GameEdition.SkyrimSpecialEdition, new WorkspacePath(dataRoot), [plugin],
            new FaceGenBakeTarget(new FormId(0xA00), plugin, plugin, [plugin],
                "FixtureNpc", "Fixture NPC", NpcSex.Female, race, [face, brow], 50F),
            new AssetPath("textures/actors/character/facegendata/facetint/TopologyFixture.esp/00000A00.dds"),
            new WorkspacePath(Path.Combine(root, "matching.nif")));

        foreach (AssetPath tri in tris.Take(3))
            WriteTopologyAsset(dataRoot, tri, WriteTopologyTri(141));
        var matching = await service.BuildAsync(request, CancellationToken.None);
        Assert(matching.Written && matching.Verified && matching.Artifact is not null &&
               File.Exists(request.OutputNif.Value),
            "Matching 141/141 native bake failed: " + Format(matching.Diagnostics));
        Assert(matching.Diagnostics.Any(item =>
                item.Code == "sse-face-bake-extension-unavailable" &&
                item.Severity == DiagnosticSeverity.Warning),
            "Absent optional extension must remain an availability warning.");
        const string golden = "c39b2f97157770ffb4be5e806b688e53887681d5f5320b6b5deb5e09a26d0626";
        Assert(matching.Artifact!.Materialization.OutputSha256.Value == golden,
            "The matching synthetic carrier changed from the pre-fix golden bytes.");
        Console.WriteLine("GOLDEN native-topology-141 " +
            matching.Artifact.Materialization.OutputSha256.Value);

        // Each role must refuse independently, even when it contains no morphs.
        foreach (AssetPath mismatch in tris)
        {
            WriteTopologyAsset(dataRoot, mismatch, WriteTopologyTri(501));
            var refusedRequest = request with
            {
                OutputNif = new WorkspacePath(Path.Combine(root,
                    Path.GetFileNameWithoutExtension(mismatch.Value) + "-refused.nif"))
            };
            var refused = await service.BuildAsync(refusedRequest, CancellationToken.None);
            Assert(!refused.Written && !refused.Verified && refused.Artifact is null &&
                   !File.Exists(refusedRequest.OutputNif.Value),
                $"Native bake admitted model 141 versus TRI 501 for {mismatch}: " +
                Format(refused.Diagnostics));
            Assert(refused.Diagnostics.Any(item =>
                item.Code == "sse-face-bake-tri-topology-mismatch" &&
                item.Severity == DiagnosticSeverity.Error &&
                item.Message.Contains("FixtureBrow", StringComparison.Ordinal) &&
                item.Message.Contains(brow.ToString(), StringComparison.Ordinal) &&
                item.Message.Contains(browModel.Value, StringComparison.Ordinal) &&
                item.Message.Contains(mismatch.Value, StringComparison.Ordinal) &&
                item.Message.Contains("141", StringComparison.Ordinal) &&
                item.Message.Contains("501", StringComparison.Ordinal)),
                "Topology refusal omitted headpart/model/TRI/count evidence: " +
                Format(refused.Diagnostics));
            string role = mismatch == dialogueTri ? "Mesh (dialogue)" :
                mismatch == raceTri ? "Race" : mismatch == chargenTri ? "Chargen" : "Extended";
            Assert(refused.Diagnostics.Any(item =>
                    item.Code == "sse-face-bake-tri-topology-mismatch" &&
                    item.Message.Contains(role, StringComparison.Ordinal)),
                "The topology refusal omitted the resolved TRI role.");
            WriteTopologyAsset(dataRoot, mismatch, WriteTopologyTri(141));
        }
        var availableExtension = await service.BuildAsync(request with
        {
            OutputNif = new WorkspacePath(Path.Combine(root, "matching-extension.nif"))
        }, CancellationToken.None);
        Assert(availableExtension.Written && availableExtension.Verified &&
               availableExtension.Artifact?.Materialization.OutputSha256.Value == golden &&
               !availableExtension.Diagnostics.Any(item =>
                   item.Code == "sse-face-bake-extension-unavailable"),
            "Present matching optional TRI changed valid carrier bytes or remained unavailable: " +
            Format(availableExtension.Diagnostics));
        foreach (AssetPath tri in tris.Take(3))
            WriteTopologyAsset(dataRoot, tri, WriteTopologyTri(501));
        var allRefused = await service.BuildAsync(request with
        {
            OutputNif = new WorkspacePath(Path.Combine(root, "all-refused.nif"))
        }, CancellationToken.None);
        Assert(!allRefused.Written && allRefused.Diagnostics.Count(item =>
                item.Code == "sse-face-bake-tri-topology-mismatch" &&
                item.Severity == DiagnosticSeverity.Error) == 3,
            "A simultaneous mismatch must report all three resolved TRI roles: " +
            Format(allRefused.Diagnostics));

        // Schema-4/5 callers do not carry native HDPT/model metadata, but their
        // dynamic carrier vertex count still has to agree with every resolved TRI.
        var legacy = CreateSyntheticBakeRequest();
        var legacyResult = new NpcManager.FaceGen.SseRaceMenuFaceBakeService().Bake(legacy with
        {
            CatalogRequest = new SkyrimRaceMenuCatalogParseRequest(
                legacy.CatalogRequest.LoadedPlugins, []),
            CarrierShapes = [legacy.CarrierShapes[2]],
            ShapeTriInputs = [new SkyrimRaceMenuFaceBakeShapeTriInputs("Hair", null, null,
                SyntheticTri("meshes/fixture/legacy.tri", [Vector3.Zero, Vector3.One]), [])],
            CustomMorphs = [], SculptParts = [], ExplicitUnavailableExtendedTris = []
        });
        Assert(!legacyResult.Accepted && legacyResult.Shapes.IsEmpty &&
               legacyResult.Diagnostics.Any(item =>
                   item.Code == "sse-face-plan-source-topology" &&
                   item.Severity == DiagnosticSeverity.Error &&
                   item.Message.Contains("has 1 vertices", StringComparison.Ordinal) &&
                   item.Message.Contains("has 2", StringComparison.Ordinal)),
            "A schema-4/5 dynamic carrier admitted a resolved TRI with a different vertex count: " +
            Format(legacyResult.Diagnostics));
    }

    private static Sha256Hash TopologyHash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void WriteTopologyAsset(string root, AssetPath path, byte[] bytes)
    {
        string output = Path.Combine(root, path.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, bytes);
    }

    private static byte[] WriteTopologyTri(int count) => WriteTri(
        Enumerable.Repeat(Vector3.Zero, count).ToArray(), [], [], [], [], []);

    private sealed class TopologyAuthority(PluginName plugin, string pluginPath,
        Sha256Hash hash, SkyrimFaceRecordRoute route, SkyrimRaceMenuCatalogParseRequest catalog) :
        ISkyrimFaceRecordPluginAuthorityLoader, ISkyrimFaceRecordRouteResolver,
        ISkyrimFaceMorphSnapshotService, ISkyrimRaceMenuCatalogAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request, CancellationToken token) =>
            ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(true,
                [new(plugin, new WorkspacePath(pluginPath), hash)], []));

        public ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
            SkyrimFaceRecordRouteRequest request, CancellationToken token) =>
            ValueTask.FromResult(new SkyrimFaceRecordRouteResult(true, route, []));

        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request, CancellationToken token) =>
            ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(true, hash,
                new SkyrimFaceMorphSnapshot(Enumerable.Repeat(0F, 18).ToImmutableArray(),
                    0F, Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                    HasNam9: false, HasNama: false), []));

        public ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
            SkyrimRaceMenuCatalogAuthorityRequest request, CancellationToken token) =>
            ValueTask.FromResult(new SkyrimRaceMenuCatalogAuthorityResult(true, catalog, [], []));
    }

    // A complete test-owned NIF: root, head bone, dynamic shape, skin, data,
    // partition, shader and texture set. No consumer model bytes are needed.
    private static byte[] WriteTopologyModel(int count, string? diffuse = null, uint? hairTint = null,
        bool shaderless = false, string? shapeName = null, (string Type, byte[] Payload)? extraBlock = null)
    {
        const ulong descriptor = 0x0040100000000004; // full-precision position, 16 bytes
        static byte[] Block(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            write(writer);
            return stream.ToArray();
        }
        static void Rotation(BinaryWriter w)
        {
            for (int i = 0; i < 9; i++) w.Write(i % 4 == 0 ? 1F : 0F);
        }
        static void Transform(BinaryWriter w)
        {
            Rotation(w); w.Write(new byte[12]); w.Write(1F);
        }
        static void Av(BinaryWriter w, int name)
        {
            w.Write(name); w.Write(0U); w.Write(-1); w.Write(0U);
            w.Write(new byte[12]); Rotation(w); w.Write(1F); w.Write(-1);
        }
        static void Text(BinaryWriter w, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value); w.Write(bytes.Length); w.Write(bytes);
        }
        byte[][] blocks =
        [
            Block(w => { Av(w, 0); w.Write(3U); w.Write(1); w.Write(2); w.Write(8); w.Write(0U); }),
            Block(w => { Av(w, 1); w.Write(0U); w.Write(0U); }),
            Block(w =>
            {
                Av(w, 2); w.Write(new byte[16]); w.Write(3); w.Write(shaderless ? -1 : 6); w.Write(-1);
                w.Write(descriptor); w.Write((ushort)0); w.Write((ushort)count);
                w.Write(0U); w.Write(0U); w.Write(count * 16);
                for (int i = 0; i < count; i++)
                {
                    w.Write(i == 1 ? 1F : 0F); w.Write(i == 2 ? 1F : 0F);
                    w.Write(0F); w.Write(0F);
                }
            }),
            Block(w => { w.Write(4); w.Write(5); w.Write(0); w.Write(1U); w.Write(1); }),
            Block(w =>
            {
                Transform(w); w.Write(1U); w.Write((byte)1); Transform(w);
                w.Write(new byte[16]); w.Write((ushort)count);
                for (int i = 0; i < count; i++) { w.Write((ushort)i); w.Write(1F); }
            }),
            Block(w =>
            {
                w.Write(1U); w.Write(count * 16); w.Write(16U); w.Write(descriptor);
                w.Write(new byte[count * 16]);
                w.Write((ushort)count); w.Write((ushort)1); w.Write((ushort)1);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)0);
                w.Write((byte)1);
                for (int i = 0; i < count; i++) w.Write((ushort)i);
                w.Write((byte)1);
                for (int i = 0; i < count; i++) w.Write(1F);
                w.Write((byte)1); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
                w.Write((byte)1); w.Write(new byte[count]); w.Write((ushort)0); w.Write(descriptor);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
            }),
            Block(w =>
            {
                w.Write(hairTint is null ? 5U : 6U); w.Write(uint.MaxValue); w.Write(0U); w.Write(-1);
                w.Write(new byte[24]); w.Write(7); w.Write(new byte[60]);
                if (hairTint is { } rgb)
                {
                    w.Write(((rgb >> 16) & 255) / 255F);
                    w.Write(((rgb >> 8) & 255) / 255F);
                    w.Write((rgb & 255) / 255F);
                }
            }),
            Block(w =>
            {
                w.Write(9U); Text(w, diffuse ?? "textures/actors/character/female/femalehead.dds");
                for (int i = 1; i < 9; i++) Text(w, string.Empty);
            }),
            Block(w => { Av(w, 3); w.Write(0U); w.Write(0U); })
        ];
        // An optional trailing block outside the admitted parser envelope (WI-NPC-6 fixtures).
        if (extraBlock is { } extra) blocks = [.. blocks, extra.Payload];
        return Block(w =>
        {
            w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            w.Write(0x14020007U); w.Write((byte)1); w.Write(12U); w.Write((uint)blocks.Length); w.Write(100U);
            w.Write(new byte[3]);
            string[] types = ["NiNode", "BSDynamicTriShape", "NiSkinInstance",
                "NiSkinData", "NiSkinPartition", "BSLightingShaderProperty", "BSShaderTextureSet"];
            if (extraBlock is { } extraType) types = [.. types, extraType.Type];
            w.Write((ushort)types.Length);
            foreach (string type in types)
                Text(w, type);
            ushort[] typeIndexes = [0, 0, 1, 2, 3, 4, 5, 6, 0];
            if (extraBlock is not null) typeIndexes = [.. typeIndexes, (ushort)(types.Length - 1)];
            foreach (ushort index in typeIndexes) w.Write(index);
            foreach (byte[] block in blocks) w.Write(block.Length);
            string[] names = ["FixtureRoot", "NPC Head [Head]", shapeName ?? "FixtureShape", "NPC Spine2 [Spn2]"];
            w.Write(4U); w.Write((uint)names.Max(name => name.Length));
            foreach (string name in names) Text(w, name);
            w.Write(0U);
            foreach (byte[] block in blocks) w.Write(block);
            w.Write(1U); w.Write(0);
        });
    }
}
