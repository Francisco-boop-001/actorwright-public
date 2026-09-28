using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNativeFaceGeomBuild()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        string root = Path.Combine(AppContext.BaseDirectory,
            "native-facegeom-" + Guid.NewGuid().ToString("N"));
        string dataRootPath = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRootPath);
        try
        {
            foreach (string asset in HeadpartModelAssets)
            {
                string destination = Path.Combine(dataRootPath,
                    asset.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(PhysicalPath(asset), destination, overwrite: false);
            }
            ImmutableArray<AssetPath> referencedTextures = HeadpartModelAssets
                .SelectMany(asset =>
                {
                    byte[] bytes = File.ReadAllBytes(PhysicalPath(asset));
                    SseSelectedHeadpartNifGeometryReadResult read =
                        new SseSelectedHeadpartNifGeometryReader().Read(
                            new SseSelectedHeadpartNifGeometryReadRequest(
                                new AssetPath(asset),
                                new Sha256Hash(Convert.ToHexString(
                                    SHA256.HashData(bytes))),
                                [.. bytes]));
                    Assert(read.Accepted && read.Document is not null,
                        $"Could not inventory model textures for {asset}: {GeometryDiagnostics(read.Diagnostics)}");
                    return read.Document!.ReferencedTextures;
                })
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            ImmutableArray<AssetPath> externalTextures = referencedTextures
                .Where(item => !IsExpectedSkyrimPlatformTexture(item))
                .ToImmutableArray();
            foreach (AssetPath texture in externalTextures)
                WriteAsset(dataRootPath, texture, [9, 8, 7, 6]);

            var chargenTri = new AssetPath(
                "meshes/fixtures/FemaleHeadCharGen.tri");
            var meshTri = new AssetPath(
                "meshes/fixtures/FemaleHead.tri");
            var sharedHairTri = new AssetPath(
                "meshes/fixtures/Pixie.tri");
            var extendedTri = new AssetPath(
                "meshes/actors/character/FaceGenMorphs/morphs/TestExt.tri");
            WriteAsset(dataRootPath, chargenTri,
                WriteDenseTri(3832, morphName: null));
            WriteAsset(dataRootPath, meshTri,
                WriteDenseTri(3832, morphName: null));
            WriteAsset(dataRootPath, sharedHairTri,
                WriteDenseTri(7362, morphName: null));
            WriteAsset(dataRootPath, extendedTri,
                WriteDenseTri(3832, "ExtSmile"));

            var plugin = new PluginName("GeometryFixtures.esp");
            string pluginPath = Path.Combine(dataRootPath, plugin.Value);
            await File.WriteAllBytesAsync(pluginPath, [1, 2, 3, 4]);
            var pluginHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(pluginPath))));
            string sidecarPath = Path.Combine(dataRootPath,
                "GeometryFixtures.bssliders");
            await File.WriteAllBytesAsync(sidecarPath, [5, 6, 7, 8]);
            var sidecarHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(sidecarPath))));
            var provider = new SkyrimFaceRecordProvider(plugin,
                new WorkspacePath(pluginPath), pluginHash);
            var headParts = HeadpartModelAssets.Select((asset, index) =>
                new FormReference(plugin,
                    new FormId(checked((uint)(0x800 + index)))))
                .ToImmutableArray();
            var race = new FormReference(plugin, new FormId(0x900));
            ImmutableArray<SkyrimFaceHeadPartRecordRoute> routedHeadParts =
                HeadpartModelAssets.Select((asset, index) =>
                    new SkyrimFaceHeadPartRecordRoute(
                        headParts[index],
                        provider,
                        ExpectedHeadpartGeometryByAsset[asset].CarrierShapeName,
                        HeadPartType(index),
                        HeadPartType(index),
                        new AssetPath(asset),
                        index == 0
                            ? [
                                new SkyrimHdptTriRoute(
                                    SkyrimHdptTriRole.CharGen, chargenTri),
                                new SkyrimHdptTriRoute(
                                    SkyrimHdptTriRole.Mesh, meshTri)
                            ]
                            : index is 4 or 5
                                ? [new SkyrimHdptTriRoute(
                                    SkyrimHdptTriRole.Mesh, sharedHairTri)]
                            : [],
                        [],
                        null,
                        0,
                        IsSelected: true,
                        IsRaceDefault: false)).ToImmutableArray();
            var route = new SkyrimFaceRecordRoute(
                new SkyrimRaceFaceRecordRoute(
                    race, provider, "GeometryRace", null, "NordRace", [], [], [], []),
                headParts,
                routedHeadParts);
            var target = new FaceGenBakeTarget(
                new FormId(0xA00), plugin, plugin, [plugin],
                "GeometryNpc", "Geometry NPC", NpcSex.Female, race,
                headParts, 50F);
            var snapshot = new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, 18).ToImmutableArray(),
                0F,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                HasNam9: false,
                HasNama: false);
            string outputPath = Path.Combine(root, "00000A00.nif");
            byte[] morphsIni = Encoding.ASCII.GetBytes(
                "extension = fixtures/FemaleHeadCharGen.tri, TestExt.tri, MissingExt.tri\n");
            var catalogRequest = new SkyrimRaceMenuCatalogParseRequest(
                [plugin],
                [new SkyrimRaceMenuCatalogAsset(
                    new AssetPath(
                        "meshes/actors/character/FaceGenMorphs/GeometryFixtures.esp/morphs.ini"),
                    HashCatalogBytes(morphsIni), [.. morphsIni])]);
            var sidecarOverlay = new SkyrimFaceGenSidecarOverlay(
                plugin,
                target.FormId,
                [new SkyrimRaceMenuCustomMorphValue("ExtSmile", 0.5F)],
                [],
                [new BodySidecarSculptPart(
                    meshTri.Value,
                    [new RaceMenuSculptVertex(1, 0.125F, 0F, 0F)]),
                 new BodySidecarSculptPart(
                     sharedHairTri.Value,
                     [new RaceMenuSculptVertex(1, 0F, 0.125F, 0F)]),
                 new BodySidecarSculptPart(
                     sharedHairTri.Value,
                     [new RaceMenuSculptVertex(1, 0F, 0.125F, 0F)])],
                [],
                [new SkyrimFaceGenSidecarAuthority(
                    plugin, new WorkspacePath(sidecarPath), sidecarHash)]);
            var assembler = new SseFaceGeomCarrierAssembler();
            var service = new SkyrimNativeFaceGeomBuildService(
                new FixedPluginAuthorityLoader(plugin, pluginPath, pluginHash),
                new FixedFaceRecordRouteResolver(route),
                new FixedFaceMorphSnapshotService(snapshot, pluginHash),
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, labRoot),
                new SkyrimAssetContentResolver(policy, labRoot),
                new FixedRaceMenuCatalogAuthorityLoader(catalogRequest),
                new RaceMenuSliderCatalogParserCore(),
                new SseSelectedHeadpartNifGeometryReader(),
                new SseRaceMenuFaceBakeService(),
                new SseFaceGeomCarrierMaterializationService(
                    assembler, policy, labRoot));

            var buildRequest = new SkyrimNativeFaceGeomBuildRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(dataRootPath),
                [plugin],
                target,
                new AssetPath(
                    "textures/actors/character/facegendata/facetint/GeometryFixtures.esp/00000A00.dds"),
                new WorkspacePath(outputPath),
                sidecarOverlay);
            SkyrimNativeFaceGeomBuildResult result = await service.BuildAsync(
                buildRequest,
                CancellationToken.None);

            Assert(result.Written && result.Verified && result.Artifact is not null &&
                   File.Exists(outputPath),
                $"Native FaceGeom build failed: {GeometryDiagnostics(result.Diagnostics)}");
            SkyrimNativeFaceGeomBuildArtifact artifact = result.Artifact!;
            Assert(!artifact.RuntimeAuthority && artifact.Shapes.Length == 7 &&
                   artifact.AssetAuthorities.Length == 11 &&
                   artifact.ExternalDependencyAuthorities.Length ==
                   artifact.AssetAuthorities.Length + externalTextures.Length &&
                   artifact.ExternalDependencyAuthorities
                       .Select(item => item.AssetPath.Value)
                       .ToHashSet(StringComparer.OrdinalIgnoreCase)
                       .SetEquals(artifact.AssetAuthorities
                           .Select(item => item.AssetPath.Value)
                           .Concat(externalTextures.Select(item => item.Value))) &&
                   artifact.ExternalDependencyAuthorities.All(item =>
                       !IsExpectedSkyrimPlatformTexture(item.AssetPath)) &&
                   result.Diagnostics.Any(item =>
                       item.Code ==
                       "skyrim-native-facegeom-platform-textures") &&
                   artifact.Shapes.Count(item => item.UsesFaceTint) == 1 &&
                   artifact.Shapes.Count(item =>
                       item.BasePositionSha256 != item.FinalPositionSha256) == 3 &&
                   artifact.SidecarOverlay == sidecarOverlay,
                "Native FaceGeom did not apply and retain the exact sidecar morph/sculpt overlay.");
            Assert(artifact.RaceMenuCatalog is not null &&
                   artifact.RaceMenuCatalog.MorphExtensions.Length == 1 &&
                   artifact.Shapes[0].TriEvidence.Any(item =>
                       item.Role == SkyrimFaceMorphTriRole.Extended &&
                       item.SourcePath == extendedTri) &&
                   result.Diagnostics.Any(item =>
                       item.Code == "sse-face-bake-extension-unavailable"),
                "Native FaceGeom did not bind the applicable RaceMenu extension TRI.");
            byte[] reopened = await File.ReadAllBytesAsync(outputPath);
            var reopenedHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(reopened)));
            Assert(reopenedHash == artifact.Materialization.OutputSha256 &&
                   SseFaceGeomCarrierCodec.Parse(reopened).Blocks.Count(item =>
                       item.Type == "BSDynamicTriShape") == 7,
                "Native FaceGeom output did not independently reopen as the evidenced carrier.");

            string identityOutputPath = Path.Combine(
                root, "00000A00-identity.nif");
            SkyrimNativeFaceGeomBuildResult identityResult =
                await service.BuildAsync(
                    buildRequest with
                    {
                        OutputNif = new WorkspacePath(identityOutputPath),
                        SkeletonAuthority =
                            SseFaceGeomCarrierSkeletonAuthority
                                .IdentityFaceGenBones
                    },
                    CancellationToken.None);
            Assert(identityResult.Written &&
                   identityResult.Verified &&
                   identityResult.Artifact is not null &&
                   identityResult.Diagnostics.Any(item =>
                       item.Code ==
                       "sse-facegeom-carrier-identity-skeleton-authority"),
                $"Native FaceGeom did not propagate identity skeleton authority: {GeometryDiagnostics(identityResult.Diagnostics)}");
            SseNifDocument identityDocument =
                SseFaceGeomCarrierCodec.Parse(
                    await File.ReadAllBytesAsync(identityOutputPath));
            Assert(ReadCarrierBoneTranslations(identityDocument)
                    .All(translation => translation == Vector3.Zero),
                "Native FaceGeom identity authority retained source-model bone translations.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static NpcHeadPartType HeadPartType(int index) => index switch
    {
        0 => NpcHeadPartType.Face,
        1 => NpcHeadPartType.Eyebrows,
        2 => NpcHeadPartType.Eyes,
        3 => NpcHeadPartType.Teeth,
        4 => NpcHeadPartType.Hair,
        5 => NpcHeadPartType.Misc,
        _ => NpcHeadPartType.HeadRear
    };

    private static bool IsExpectedSkyrimPlatformTexture(AssetPath path) =>
        path.Value.StartsWith(
            "textures/cubemaps/", StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/female/femalehead.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/female/femalehead_msn.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/female/femalehead_s.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/female/femalehead_sk.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/male/malehead.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/male/malehead_msn.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/male/malehead_s.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals(
            "textures/actors/character/male/malehead_sk.dds",
            StringComparison.OrdinalIgnoreCase);

    private sealed class FixedPluginAuthorityLoader(
        PluginName plugin,
        string path,
        Sha256Hash hash) : ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(
                true,
                [new SkyrimFaceRecordPluginAuthority(
                    plugin, new WorkspacePath(path), hash)],
                []));
        }
    }

    private sealed class FixedFaceRecordRouteResolver(SkyrimFaceRecordRoute route) :
        ISkyrimFaceRecordRouteResolver
    {
        public ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
            SkyrimFaceRecordRouteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceRecordRouteResult(
                true, route, []));
        }
    }

    private sealed class FixedFaceMorphSnapshotService(
        SkyrimFaceMorphSnapshot snapshot,
        Sha256Hash pluginHash) : ISkyrimFaceMorphSnapshotService
    {
        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(
                true, pluginHash, snapshot, []));
        }
    }

    private static void WriteAsset(
        string dataRoot,
        AssetPath path,
        byte[] bytes)
    {
        string output = Path.Combine(dataRoot,
            path.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, bytes);
    }

    private static byte[] WriteDenseTri(int vertexCount, string? morphName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII,
            leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        writer.Write((uint)vertexCount);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(morphName is null ? 0U : 1U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        for (int index = 0; index < vertexCount; index++)
        {
            writer.Write(Vector3.Zero.X);
            writer.Write(Vector3.Zero.Y);
            writer.Write(Vector3.Zero.Z);
        }
        if (morphName is not null)
        {
            byte[] name = Encoding.ASCII.GetBytes(morphName);
            writer.Write((uint)(name.Length + 1));
            writer.Write(name);
            writer.Write((byte)0);
            writer.Write(0.001F);
            for (int index = 0; index < vertexCount; index++)
            {
                writer.Write(index == 0 ? (short)1 : (short)0);
                writer.Write((short)0);
                writer.Write((short)0);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static Sha256Hash HashCatalogBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class FixedRaceMenuCatalogAuthorityLoader(
        SkyrimRaceMenuCatalogParseRequest catalogRequest)
        : ISkyrimRaceMenuCatalogAuthorityLoader
    {
        public ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
            SkyrimRaceMenuCatalogAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SkyrimRaceMenuCatalogAuthorityResult(
                    true,
                    catalogRequest,
                    [],
                    []));
        }
    }
}
