using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class NativeFaceGeomExternalExclusionTests :
    IPreview254ExternalSmpArchitectureScenario
{
    private const string PhysicsLocator = "HDT Skinned Mesh Physics Object";
    private const string PhysicsXml =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/generic.xml";
    private const string PhysicsXmlBethesda =
        "SKSE\\Plugins\\hdtSkinnedMeshConfigs\\generic.xml";
    private const string PhysicsMapping =
        "SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml";
    private const string PhysicsMappingBethesda =
        "SKSE\\Plugins\\hdtSkinnedMeshConfigs\\defaultBBPs.xml";
    private const string ProviderTexture =
        "textures/actors/character/female/HairSmpXYZ.dds";
    private const string ProviderTextureBethesda =
        "textures\\actors\\character\\female\\HairSmpXYZ.dds";
    private const string ProviderCollider = "NativeExternalHairCollider";

    private static readonly string HeadpartGeometryRoot = Path.GetFullPath(
        "tests/fixtures/sse-packed-normals/dynamic-16.nif");

    private static readonly ImmutableArray<string> ModelAssets =
    [
        "meshes/fixtures/NativeFacePart-0.nif",
        "meshes/fixtures/NativeFacePart-1.nif",
        "meshes/fixtures/NativeFacePart-2.nif",
        "meshes/fixtures/NativeFacePart-3.nif",
        "meshes/fixtures/NativeFacePart-4.nif",
        "meshes/fixtures/NativeFacePart-5.nif",
        "meshes/fixtures/NativeFacePart-6.nif"
    ];

    private static readonly ImmutableDictionary<string, string> ShapeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelAssets[0]] = Fill(PhysicsLocator.Length, 'o'),
            [ModelAssets[1]] = Fill(PhysicsXml.Length, 'x'),
            [ModelAssets[2]] = Fill(PhysicsMapping.Length, 'm'),
            [ModelAssets[3]] = Fill(ProviderCollider.Length, 'c'),
            [ModelAssets[4]] = "NativeExternalHairRoot",
            [ModelAssets[5]] = "NativeExternalHairChild",
            [ModelAssets[6]] = Fill("NativeExternalHairRoot".Length, 'p')
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public string Selector => "--test-native-facegeom-external-exclusion";

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(AppContext.BaseDirectory,
            "native-facegeom-exclusion-" + Guid.NewGuid().ToString("N"));
        string dataRootPath = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRootPath);
        try
        {
            foreach (string asset in ModelAssets)
            {
                string destination = Path.Combine(dataRootPath,
                    asset.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(PhysicalPath(asset), destination, overwrite: false);
            }

            ImmutableArray<AssetPath> referencedTextures = ModelAssets
                .SelectMany(asset =>
                {
                    byte[] bytes = File.ReadAllBytes(PhysicalPath(asset));
                    SseSelectedHeadpartNifGeometryReadResult read =
                        new SseSelectedHeadpartNifGeometryReader().Read(
                            new SseSelectedHeadpartNifGeometryReadRequest(
                                new AssetPath(asset),
                                HashBytes(bytes),
                                [.. bytes]));
                    Assert(read.Accepted && read.Document is not null,
                        $"Could not inventory model textures for {asset}: {GeometryDiagnostics(read.Diagnostics)}");
                    return read.Document!.ReferencedTextures;
                })
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            foreach (AssetPath texture in referencedTextures.Where(
                         item => !IsExpectedSkyrimPlatformTexture(item)))
                WriteAsset(dataRootPath, texture, [9, 8, 7, 6]);

            var chargenTri = new AssetPath("meshes/fixtures/FemaleHeadCharGen.tri");
            var meshTri = new AssetPath("meshes/fixtures/FemaleHead.tri");
            WriteAsset(dataRootPath, chargenTri, WriteDenseTri(3832, null));
            WriteAsset(dataRootPath, meshTri, WriteDenseTri(3832, null));

            var plugin = new PluginName("GenericExternalHair.esp");
            string pluginPath = Path.Combine(dataRootPath, plugin.Value);
            File.WriteAllBytes(pluginPath, [1, 2, 3, 4]);
            Sha256Hash pluginHash = HashBytes(File.ReadAllBytes(pluginPath));
            var provider = new SkyrimFaceRecordProvider(
                plugin, new WorkspacePath(pluginPath), pluginHash);
            ImmutableArray<FormReference> headParts = ModelAssets
                .Select((_, index) => new FormReference(plugin,
                    new FormId(checked((uint)(0x800 + index)))))
                .ToImmutableArray();
            var race = new FormReference(plugin, new FormId(0x900));
            ImmutableArray<SkyrimFaceHeadPartRecordRoute> routedHeadParts =
                ModelAssets.Select((asset, index) =>
                    new SkyrimFaceHeadPartRecordRoute(
                        headParts[index],
                        provider,
                        ShapeNames[asset],
                        index is 4 or 5 ? NpcHeadPartType.Hair : HeadPartType(index),
                        index is 4 or 5 ? NpcHeadPartType.Hair : HeadPartType(index),
                        new AssetPath(asset),
                        index == 0
                            ? [
                                new SkyrimHdptTriRoute(
                                    SkyrimHdptTriRole.CharGen, chargenTri),
                                new SkyrimHdptTriRoute(
                                    SkyrimHdptTriRole.Mesh, meshTri)
                            ]
                            : [],
                        [],
                        index == 5 ? headParts[4] : null,
                        index == 5 ? 1 : 0,
                        IsSelected: true,
                        IsRaceDefault: false)).ToImmutableArray();
            var route = new SkyrimFaceRecordRoute(
                new SkyrimRaceFaceRecordRoute(
                    race, provider, "GenericExternalRace", null, "NordRace",
                    [], [], [], []),
                headParts,
                routedHeadParts)
            {
                HeadPartGraph =
                [
                    new SkyrimFaceHeadPartGraphRoute(
                        headParts[4], plugin, headParts[4], plugin, pluginHash, 4,
                        Hash("root-record"), "ExternalHairRoot", NpcHeadPartType.Hair,
                        NpcHeadPartType.Hair, new AssetPath(ModelAssets[4]),
                        [], [headParts[5]], null, 0, true, false, 0, NpcSex.Female,
                        race),
                    new SkyrimFaceHeadPartGraphRoute(
                        headParts[5], plugin, headParts[5], plugin, pluginHash, 4,
                        Hash("child-record"), "ExternalHairChild", NpcHeadPartType.Hair,
                        NpcHeadPartType.Hair, new AssetPath(ModelAssets[5]),
                        [], [], headParts[4], 1, true, false, 1, NpcSex.Female,
                        race)
                ]
            };
            var target = new FaceGenBakeTarget(
                new FormId(0xA00), plugin, plugin, [plugin],
                "GenericExternalNpc", "Generic External NPC", NpcSex.Female,
                race, headParts, 50F);
            var snapshot = new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, 18).ToImmutableArray(), 0F,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                HasNam9: false, HasNama: false);
            string outputPath = Path.Combine(root, "00000A00.nif");
            byte[] morphsIni = Encoding.ASCII.GetBytes(
                "extension = fixtures/FemaleHeadCharGen.tri, MissingExt.tri\n");
            var catalogRequest = new SkyrimRaceMenuCatalogParseRequest(
                [plugin],
                [new SkyrimRaceMenuCatalogAsset(
                    new AssetPath(
                        "meshes/actors/character/FaceGenMorphs/GenericExternalHair.esp/morphs.ini"),
                    HashBytes(morphsIni), [.. morphsIni])]);

            ExternalHeadPartDependencyDescriptor descriptor =
                CreateDescriptor(plugin, pluginHash, race, headParts);
            var events = new List<string>();
            var discovery = new RecordingDiscovery(descriptor, dataRootPath, events);
            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath(root), new WorkspacePath(@"F:\ExampleGame"));
            var assembler = new SseFaceGeomCarrierAssembler();
            var contentResolver = new RecordingContentResolver(
                new SkyrimAssetContentResolver(policy, new WorkspacePath(root)),
                events);
            var geometryReader = new RecordingGeometryReader(
                new SseSelectedHeadpartNifGeometryReader(), events);
            var faceBake = new RecordingFaceBakeService(
                new SseRaceMenuFaceBakeService(), events);
            var materialization = new RecordingMaterializationService(
                new SseFaceGeomCarrierMaterializationService(
                    assembler, policy, new WorkspacePath(root)), events);
            SkyrimNativeFaceGeomBuildService MakeService(
                IExternalHeadPartDependencyDiscovery? selectedDiscovery,
                IExternalHeadPartFaceGeomExclusionVerifier? selectedVerifier) =>
                new(
                    new FixedPluginAuthorityLoader(plugin, pluginPath, pluginHash),
                    new FixedFaceRecordRouteResolver(route),
                    new FixedFaceMorphSnapshotService(snapshot, pluginHash),
                    new SkyrimAssetAuthorityPlanner(
                        new BethesdaAssetIndexer(), policy, new WorkspacePath(root)),
                    contentResolver,
                    new FixedRaceMenuCatalogAuthorityLoader(catalogRequest),
                    new RaceMenuSliderCatalogParserCore(),
                    geometryReader,
                    faceBake,
                    materialization,
                    externalHeadPartDependencyDiscovery: selectedDiscovery,
                    externalHeadPartFaceGeomExclusionVerifier: selectedVerifier);
            var service = MakeService(
                discovery, new ExternalHeadPartFaceGeomExclusionVerifier());

            SkyrimNativeFaceGeomBuildResult result = await service.BuildAsync(
                new SkyrimNativeFaceGeomBuildRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRootPath),
                    [plugin],
                    target,
                    new AssetPath(
                        "textures/actors/character/facegendata/facetint/GenericExternalHair.esp/00000A00.dds"),
                    new WorkspacePath(outputPath))
                {
                    ExpectedExternalHeadPartDescriptor = descriptor
                },
                cancellationToken);

            Assert(result.Written && result.Verified && result.Artifact is not null,
                $"Generic external native FaceGeom build was refused: {GeometryDiagnostics(result.Diagnostics)}");
            SkyrimNativeFaceGeomBuildArtifact artifact = result.Artifact!;
            Assert(discovery.Calls == 1 && discovery.SawProviderBytes &&
                   events.SequenceEqual(["provider-bytes", "discovery",
                       "shape-materialization", "face-bake", "materialization"]),
                "External discovery did not run once after provider bytes and before materialization.");
            Assert(artifact.Shapes.Length == 5 &&
                   artifact.Shapes.All(item => item.HeadPart != headParts[4] &&
                       item.HeadPart != headParts[5]) &&
                   artifact.ExternalHeadPartDependencies is { Length: 1 } &&
                   artifact.ExternalHeadPartExclusionAttestations is { Length: 1 } &&
                   artifact.ExternalHeadPartDependencies.Value[0].DescriptorId ==
                       descriptor.DescriptorId &&
                   artifact.ExternalHeadPartExclusionAttestations.Value[0].DescriptorId ==
                       descriptor.DescriptorId && !artifact.RuntimeAuthority,
                "Native FaceGeom did not exclude the generic external graph or emit bound evidence.");
            using (JsonDocument evidenceDocument = JsonDocument.Parse(
                       ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                           artifact.ExternalHeadPartExclusionAttestations!.Value[0])))
            {
                JsonElement[] triRows = evidenceDocument.RootElement.GetProperty("includedOrdinaryShapes")
                    .EnumerateArray().SelectMany(shape => shape.GetProperty("triEvidence").EnumerateArray()).ToArray();
                Assert(triRows.Length > 0 && triRows.All(row =>
                        row.TryGetProperty("evidenceId", out JsonElement id) && id.GetString()?.Length == 64),
                    "Actual native external attestation omitted stable TRI evidence identities.");
            }
            ImmutableHashSet<FormReference> ordinaryHeadParts = headParts
                .Where((_, index) => index is not 4 and not 5)
                .ToImmutableHashSet();
            Assert(artifact.Shapes.Select(item => item.HeadPart)
                       .ToImmutableHashSet().SetEquals(ordinaryHeadParts) &&
                   artifact.Shapes.Single(item => item.HeadPart == headParts[0])
                       .UsesFaceTint &&
                   artifact.RecordRoute.HeadPartGraph.Length == 2 &&
                   artifact.RecordRoute.HeadPartGraph[0].RequiredOutputMaster == plugin &&
                   artifact.RecordRoute.HeadPartGraph[0].HnamEdges.SequenceEqual([headParts[5]]) &&
                   artifact.RecordRoute.HeadPartGraph[1].Parent == headParts[4] &&
                   artifact.RecordRoute.RootHeadParts.Contains(headParts[4]),
                "Native FaceGeom ordinary inventory did not preserve FaceTint, PNAM, and HNAM/master evidence.");

            ExternalHeadPartDependencyDescriptor mismatchedDescriptor =
                descriptor with
                {
                    Provider = descriptor.Provider with
                    {
                        PluginSha256 = Hash("mismatched-provider")
                    }
                };
            mismatchedDescriptor = mismatchedDescriptor with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(mismatchedDescriptor)
            };
            string mismatchedExpectedPath = Path.Combine(root,
                "mismatched-expected.nif");
            SkyrimNativeFaceGeomBuildResult mismatchedExpected =
                await MakeService(discovery,
                    new ExternalHeadPartFaceGeomExclusionVerifier())
                    .BuildAsync(
                        new SkyrimNativeFaceGeomBuildRequest(
                            GameEdition.SkyrimSpecialEdition,
                            new WorkspacePath(dataRootPath),
                            [plugin], target,
                            new AssetPath(
                                "textures/actors/character/facegendata/facetint/" +
                                "GenericExternalHair.esp/00000A00.dds"),
                            new WorkspacePath(mismatchedExpectedPath))
                        {
                            ExpectedExternalHeadPartDescriptor =
                                mismatchedDescriptor
                        }, cancellationToken);
            Assert(!mismatchedExpected.Written &&
                   !File.Exists(mismatchedExpectedPath),
                "Discovery accepted a descriptor that differed from the expected identity.");

            string secondOutputPath = Path.Combine(root, "00000A01.nif");
            SkyrimNativeFaceGeomBuildResult second = await service.BuildAsync(
                new SkyrimNativeFaceGeomBuildRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRootPath),
                    [plugin],
                    target,
                    new AssetPath(
                        "textures/actors/character/facegendata/facetint/GenericExternalHair.esp/00000A00.dds"),
                    new WorkspacePath(secondOutputPath))
                {
                    SkeletonAuthority =
                        SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones
                },
                cancellationToken);
            Assert(second.Written && second.Verified && second.Artifact is not null,
                $"Second external native FaceGeom build was refused: {GeometryDiagnostics(second.Diagnostics)}");
            Assert(second.Artifact!.ExternalHeadPartDependencies is { Length: 1 } &&
                   second.Artifact.ExternalHeadPartExclusionAttestations is { Length: 1 } &&
                   second.Artifact.ExternalHeadPartDependencies.Value[0].DescriptorId ==
                       artifact.ExternalHeadPartDependencies!.Value[0].DescriptorId &&
                   second.Artifact.ExternalHeadPartExclusionAttestations.Value[0]
                           .AttestationSha256 !=
                       artifact.ExternalHeadPartExclusionAttestations!.Value[0]
                           .AttestationSha256,
                "External descriptor identity drifted or the per-build attestation did not bind the changed output.");

            byte[] pristine = await File.ReadAllBytesAsync(outputPath,
                cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[6]],
                ShapeNames[ModelAssets[4]], descriptor, artifact.Shapes,
                "provider shape", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[0]],
                PhysicsLocator, descriptor, artifact.Shapes,
                "physics locator", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[1]],
                PhysicsXml, descriptor, artifact.Shapes,
                "physics XML", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[1]],
                PhysicsXmlBethesda, descriptor, artifact.Shapes,
                "Bethesda-backslash physics XML", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[2]],
                PhysicsMapping, descriptor, artifact.Shapes,
                "physics mapping", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[2]],
                PhysicsMappingBethesda, descriptor, artifact.Shapes,
                "Bethesda-backslash physics mapping", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine, ShapeNames[ModelAssets[3]],
                ProviderCollider, descriptor, artifact.Shapes,
                "collision body", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine,
                "textures\\actors\\character\\female\\FemaleHead.dds",
                ProviderTexture, descriptor, artifact.Shapes,
                "provider texture asset", cancellationToken);
            await AssertCarrierContamination(
                outputPath, pristine,
                "textures\\actors\\character\\female\\FemaleHead.dds",
                ProviderTextureBethesda, descriptor, artifact.Shapes,
                "Bethesda-backslash provider texture asset", cancellationToken);

            File.Delete(outputPath);
            var rollbackService = MakeService(
                new RecordingDiscovery(descriptor, dataRootPath, events),
                new RejectingExclusionVerifier());
            SkyrimNativeFaceGeomBuildResult rollback = await rollbackService.BuildAsync(
                new SkyrimNativeFaceGeomBuildRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRootPath),
                    [plugin],
                    target,
                    new AssetPath(
                        "textures/actors/character/facegendata/facetint/GenericExternalHair.esp/00000A00.dds"),
                    new WorkspacePath(outputPath)),
                cancellationToken);
            Assert(!rollback.Written && !File.Exists(outputPath),
                "A refused external exclusion build left its materialized FaceGeom output behind.");

            string ordinaryOutputPath = Path.Combine(root, "ordinary.nif");
            SkyrimNativeFaceGeomBuildRequest ordinaryRequest =
                new SkyrimNativeFaceGeomBuildRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRootPath),
                    [plugin], target,
                    new AssetPath(
                        "textures/actors/character/facegendata/facetint/GenericExternalHair.esp/00000A00.dds"),
                    new WorkspacePath(ordinaryOutputPath));
            SkyrimNativeFaceGeomBuildResult ordinary = await MakeService(null, null)
                .BuildAsync(ordinaryRequest, cancellationToken);
            Assert(ordinary.Written && ordinary.Verified && ordinary.Artifact is not null &&
                   !ordinary.Artifact.RuntimeAuthority &&
                   ordinary.Artifact.ExternalHeadPartDependencies is null &&
                   ordinary.Artifact.ExternalHeadPartExclusionAttestations is null,
                "Legacy ordinary FaceGeom fallback did not remain readable and runtime-inert.");
            byte[] ordinaryBytes = await File.ReadAllBytesAsync(ordinaryOutputPath,
                cancellationToken);
            byte[] ordinaryArtifactJson = JsonSerializer.SerializeToUtf8Bytes(
                ordinary.Artifact);

            FaceGenNpcBakeArtifact ordinaryFaceGenArtifact =
                CreateLegacyFaceGenArtifact(ordinary.Artifact!, root);
            byte[] ordinaryFaceGenArtifactJson = JsonSerializer.SerializeToUtf8Bytes(
                ordinaryFaceGenArtifact);
            byte[] preChangeFaceGenArtifactJson = JsonSerializer.SerializeToUtf8Bytes(
                new PreChangeFaceGenNpcBakeArtifact(
                    ordinaryFaceGenArtifact.Target,
                    ordinaryFaceGenArtifact.FaceGeomNif,
                    ordinaryFaceGenArtifact.FaceGeomSha256,
                    ordinaryFaceGenArtifact.FaceGeomByteLength,
                    ordinaryFaceGenArtifact.FaceTintDds,
                    ordinaryFaceGenArtifact.FaceTintSha256,
                    ordinaryFaceGenArtifact.FaceTintByteLength,
                    ordinaryFaceGenArtifact.RuntimeAuthority)
                {
                    ExternalDependencyAuthorities =
                        ordinaryFaceGenArtifact.ExternalDependencyAuthorities,
                    ExternalProviderSidecarAuthorities =
                        ordinaryFaceGenArtifact.ExternalProviderSidecarAuthorities
                });
            Assert(ordinaryFaceGenArtifactJson.SequenceEqual(
                       preChangeFaceGenArtifactJson),
                "Ordinary FaceGenNpcBakeArtifact JSON changed from the pre-change contract.");
            File.Delete(ordinaryOutputPath);

            var notApplicableDiscovery = new RecordingDiscovery(
                descriptor, dataRootPath, events,
                ExternalHeadPartDependencyDiscoveryStatus.NotApplicable);
            SkyrimNativeFaceGeomBuildResult notApplicable =
                await MakeService(notApplicableDiscovery, null)
                    .BuildAsync(ordinaryRequest, cancellationToken);
            byte[] notApplicableBytes = await File.ReadAllBytesAsync(
                ordinaryOutputPath, cancellationToken);
            byte[] notApplicableArtifactJson = JsonSerializer.SerializeToUtf8Bytes(
                notApplicable.Artifact);
            FaceGenNpcBakeArtifact notApplicableFaceGenArtifact =
                CreateLegacyFaceGenArtifact(notApplicable.Artifact!, root);
            byte[] notApplicableFaceGenArtifactJson =
                JsonSerializer.SerializeToUtf8Bytes(notApplicableFaceGenArtifact);
            Assert(notApplicable.Written && notApplicable.Verified &&
                   notApplicable.Artifact is not null &&
                   !notApplicable.Artifact.RuntimeAuthority &&
                   notApplicable.Artifact.ExternalHeadPartDependencies is null &&
                   notApplicable.Artifact.ExternalHeadPartExclusionAttestations is null &&
                   ordinaryBytes.SequenceEqual(notApplicableBytes) &&
                   ordinaryArtifactJson.SequenceEqual(notApplicableArtifactJson) &&
                   ordinaryFaceGenArtifactJson.SequenceEqual(
                       notApplicableFaceGenArtifactJson) &&
                   preChangeFaceGenArtifactJson.SequenceEqual(
                       notApplicableFaceGenArtifactJson),
                "NotApplicable discovery changed ordinary FaceGeom bytes or artifact JSON.");
            File.Delete(ordinaryOutputPath);

            string missingDiscoveryExpectedPath = Path.Combine(root,
                "missing-discovery-expected.nif");
            SkyrimNativeFaceGeomBuildResult missingDiscoveryExpected =
                await MakeService(null, null).BuildAsync(
                    ordinaryRequest with
                    {
                        OutputNif = new WorkspacePath(missingDiscoveryExpectedPath),
                        ExpectedExternalHeadPartDescriptor = descriptor
                    }, cancellationToken);
            Assert(!missingDiscoveryExpected.Written &&
                   !File.Exists(missingDiscoveryExpectedPath),
                "A non-null expected descriptor bypassed a missing discovery dependency.");

            string notApplicableExpectedPath = Path.Combine(root,
                "not-applicable-expected.nif");
            SkyrimNativeFaceGeomBuildResult notApplicableExpected =
                await MakeService(
                    new RecordingDiscovery(descriptor, dataRootPath, events,
                        ExternalHeadPartDependencyDiscoveryStatus.NotApplicable),
                    null).BuildAsync(
                    ordinaryRequest with
                    {
                        OutputNif = new WorkspacePath(notApplicableExpectedPath),
                        ExpectedExternalHeadPartDescriptor = descriptor
                    }, cancellationToken);
            Assert(!notApplicableExpected.Written &&
                   !File.Exists(notApplicableExpectedPath),
                "NotApplicable discovery bypassed a non-null expected descriptor.");

            string refusedExpectedPath = Path.Combine(root,
                "refused-expected.nif");
            SkyrimNativeFaceGeomBuildResult refusedExpected =
                await MakeService(
                    new RecordingDiscovery(descriptor, dataRootPath, events,
                        ExternalHeadPartDependencyDiscoveryStatus.Refused),
                    null).BuildAsync(
                    ordinaryRequest with
                    {
                        OutputNif = new WorkspacePath(refusedExpectedPath),
                        ExpectedExternalHeadPartDescriptor = descriptor
                    }, cancellationToken);
            Assert(!refusedExpected.Written && !File.Exists(refusedExpectedPath),
                "Refused discovery bypassed a non-null expected descriptor.");

            PluginName dintPlugin = new(
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin);
            Sha256Hash dintHash = new(
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPluginSha256);
            string dintPluginPath = Path.Combine(dataRootPath, dintPlugin.Value);
            File.WriteAllBytes(dintPluginPath, [1, 2, 3, 4]);
            SkyrimFaceRecordRoute dintRoute = RebindRouteToDint(
                route, headParts, race, dintPlugin, dintHash, dintPluginPath);
            var dintRootModel = new AssetPath(
                "meshes/armor/[dint999]/02 Hair/hairS/legacyRoot.nif");
            var dintChildModel = new AssetPath(
                "meshes/armor/[dint999]/02 Hair/hairS/legacyChild.nif");
            WriteAsset(dataRootPath, dintRootModel,
                File.ReadAllBytes(PhysicalPath(ModelAssets[4])));
            WriteAsset(dataRootPath, dintChildModel,
                File.ReadAllBytes(PhysicalPath(ModelAssets[5])));
            foreach (string platformTexture in new[]
                     {
                         "textures/actors/character/female/FemaleHead.dds",
                         "textures/actors/character/female/FemaleHead_msn.dds",
                         "textures/actors/character/female/FemaleHead_sk.dds",
                         "textures/actors/character/female/FemaleHead_S.dds"
                     })
            {
                WriteAsset(dataRootPath, new AssetPath(platformTexture),
                    [8, 7, 6, 5]);
            }
            dintRoute = dintRoute with
            {
                HeadParts = dintRoute.HeadParts.Select((item, index) => index switch
                {
                    4 => item with { ModelNif = dintRootModel },
                    5 => item with { ModelNif = dintChildModel },
                    _ => item
                }).ToImmutableArray(),
                HeadPartGraph = dintRoute.HeadPartGraph
                    .Select((item, index) => index switch
                    {
                        0 => item with { ModelNif = dintRootModel },
                        1 => item with { ModelNif = dintChildModel },
                        _ => item
                    }).ToImmutableArray()
            };
            FaceGenBakeTarget dintTarget = RebindTargetToDint(
                target, headParts, race, dintPlugin);
            SkyrimRaceMenuCatalogParseRequest dintCatalogRequest =
                catalogRequest with { LoadedPlugins = [dintPlugin] };
            string dintOutputPath = Path.Combine(root, "dint-legacy.nif");
            var dintService = new SkyrimNativeFaceGeomBuildService(
                new FixedPluginAuthorityLoader(dintPlugin, dintPluginPath,
                    dintHash),
                new FixedFaceRecordRouteResolver(dintRoute),
                new FixedFaceMorphSnapshotService(snapshot, dintHash),
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, new WorkspacePath(root)),
                contentResolver,
                new FixedRaceMenuCatalogAuthorityLoader(dintCatalogRequest),
                new RaceMenuSliderCatalogParserCore(),
                geometryReader,
                faceBake,
                materialization,
                externalHeadPartDependencyDiscovery: null,
                externalHeadPartFaceGeomExclusionVerifier: null);
            var exactDintRow = new SkyrimNativeFaceGeomExternalHeadPart(
                0,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .DintSourceFormIdentifier,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintProviderFormKey,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .RecordOnlyExternalDisposition);
            SkyrimNativeFaceGeomBuildResult dintResult =
                await dintService.BuildAsync(
                    new SkyrimNativeFaceGeomBuildRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRootPath),
                        [dintPlugin],
                        dintTarget,
                        new AssetPath(
                            "textures/actors/character/facegendata/facetint/" +
                            "[dint999] HairPack02.esp/00000A00.dds"),
                        new WorkspacePath(dintOutputPath))
                    {
                        NativeFaceGeomExternalHeadParts = [exactDintRow]
                    }, cancellationToken);
            Assert(dintResult.Written && dintResult.Verified &&
                   dintResult.Artifact is not null &&
                   !dintResult.Artifact.RuntimeAuthority &&
                   dintResult.Artifact.ExternalHeadPartDependencies is null &&
                   dintResult.Artifact.ExternalHeadPartExclusionAttestations is null &&
                   dintResult.Artifact.ExternalHeadParts.SequenceEqual([exactDintRow]) &&
                   dintResult.Artifact.PluginAuthorities.Length == 1 &&
                   dintResult.Artifact.PluginAuthorities[0].Plugin == dintPlugin &&
                   dintResult.Artifact.PluginAuthorities[0].ExpectedSha256 == dintHash &&
                   dintResult.Artifact.RecordRoute.HeadPartGraph.Length == 2 &&
                   dintResult.Artifact.RecordRoute.HeadPartGraph.All(item =>
                       item.WinningPlugin == dintPlugin &&
                       item.WinningPluginSha256 == dintHash) &&
                   dintResult.Artifact.Shapes.Length == 5 &&
                   dintResult.Artifact.Shapes.All(item =>
                       item.HeadPart != dintRoute.HeadParts[4].Reference &&
                       item.HeadPart != dintRoute.HeadParts[5].Reference) &&
                   dintResult.Artifact.Shapes.Single(item =>
                       item.HeadPart == dintRoute.HeadParts[0].Reference)
                       .UsesFaceTint,
                "The exact admitted Dint row did not produce a readable ordinary, " +
                "runtime-inert carrier: " + GeometryDiagnostics(dintResult.Diagnostics));
            SkyrimNativeFaceGeomBuildArtifact dintArtifact = dintResult.Artifact!;
            byte[] dintBytes = await File.ReadAllBytesAsync(dintOutputPath,
                cancellationToken);
            Assert(dintBytes.Length == dintArtifact.Materialization
                       .OutputByteLength &&
                   HashBytes(dintBytes) == dintArtifact.Materialization
                       .OutputSha256 &&
                   dintArtifact.RecordRoute.RootHeadParts.Contains(
                       dintRoute.HeadParts[4].Reference) &&
                   dintArtifact.RecordRoute.HeadPartGraph[0].HnamEdges
                       .SequenceEqual([dintRoute.HeadParts[5].Reference]),
                "The legacy Dint carrier lost its FaceTint/master/PNAM route evidence.");
            File.Delete(dintOutputPath);

            string refusedPath = Path.Combine(root, "refused.nif");
            SkyrimNativeFaceGeomBuildResult discoveryRefused =
                await MakeService(
                    new RecordingDiscovery(descriptor, dataRootPath, events,
                        ExternalHeadPartDependencyDiscoveryStatus.Refused), null)
                    .BuildAsync(ordinaryRequest with { OutputNif = new WorkspacePath(refusedPath) },
                        cancellationToken);
            Assert(!discoveryRefused.Written && !File.Exists(refusedPath),
                "Refused external discovery produced a FaceGeom output.");

            string stalePath = Path.Combine(root, "stale-attestation.nif");
            SkyrimNativeFaceGeomBuildResult stale = await MakeService(
                    new RecordingDiscovery(descriptor, dataRootPath, events),
                    new StaleAttestationVerifier(new ExternalHeadPartFaceGeomExclusionVerifier()))
                .BuildAsync(ordinaryRequest with { OutputNif = new WorkspacePath(stalePath) },
                    cancellationToken);
            Assert(!stale.Written && !File.Exists(stalePath),
                "A stale output-bound attestation was accepted or left output behind.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ExternalHeadPartDependencyDescriptor CreateDescriptor(
        PluginName plugin,
        Sha256Hash pluginHash,
        FormReference race,
        ImmutableArray<FormReference> headParts)
    {
        var provider = new ExternalHeadPartProviderIdentity(
            plugin, pluginHash, 4, ExternalHeadPartRedistributionMode.ExternalProviderRequired);
        var rootModel = new AssetPath(ModelAssets[4]);
        var childModel = new AssetPath(ModelAssets[5]);
        var xml = new AssetPath(PhysicsXml);
        var mappingPath = new AssetPath(PhysicsMapping);
        var providerTexture = new AssetPath(ProviderTexture);
        var members = ImmutableArray.Create(
            new ExternalHeadPartRecordDependency(
                headParts[4], plugin, headParts[4], plugin, pluginHash, 4,
                Hash("root-record"), "ExternalHairRoot", NpcHeadPartType.Hair,
                NpcHeadPartType.Hair, rootModel, [], [headParts[5]], null, 0, 0,
                NpcSex.Female, race),
            new ExternalHeadPartRecordDependency(
                headParts[5], plugin, headParts[5], plugin, pluginHash, 4,
                Hash("child-record"), "ExternalHairChild", NpcHeadPartType.Hair,
                NpcHeadPartType.Hair, childModel, [], [], headParts[4], 1, 1,
                NpcSex.Female, race));
        var physics = new ExternalHeadPartPhysicsBinding(
            ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
            [
                new ExternalHeadPartPhysicsShapeBinding(
                    headParts[4], rootModel, ShapeNames[ModelAssets[4]], xml,
                    Hash("xml"), 4),
                new ExternalHeadPartPhysicsShapeBinding(
                    headParts[5], childModel, ShapeNames[ModelAssets[5]], xml,
                    Hash("xml"), 4)
            ], new ExternalHeadPartPhysicsMappingAuthority(
                mappingPath, Hash("mapping"), 4));
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            default,
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            headParts[4], headParts[4], NpcHeadPartType.Hair, Hash("graph"),
            provider, members, physics,
            [
                new ExternalHeadPartAssetDependency(
                    rootModel, Hash("root-model"), 4, plugin, pluginHash, null),
                new ExternalHeadPartAssetDependency(
                    childModel, Hash("child-model"), 4, plugin, pluginHash, null),
                new ExternalHeadPartAssetDependency(
                    xml, Hash("xml"), 4, plugin, pluginHash, null),
                new ExternalHeadPartAssetDependency(
                    mappingPath, Hash("mapping"), 4, plugin, pluginHash, null),
                new ExternalHeadPartAssetDependency(
                    providerTexture, Hash("provider-texture"), 4, plugin,
                    pluginHash, null),
                new ExternalHeadPartAssetDependency(
                    new AssetPath("meshes/fixtures/NativeExternalHair.tri"),
                    Hash("provider-tri"), 4, plugin, pluginHash, null)
            ],
            [new ExternalHeadPartRuntimePrerequisite(
                "collision-body", ProviderCollider, "provider-collider")]);
        return descriptor with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptor)
        };
    }

    private static FaceGenNpcBakeArtifact CreateLegacyFaceGenArtifact(
        SkyrimNativeFaceGeomBuildArtifact nativeArtifact,
        string root)
    {
        return new FaceGenNpcBakeArtifact(
            nativeArtifact.Target,
            nativeArtifact.Materialization.OutputNif,
            nativeArtifact.Materialization.OutputSha256,
            nativeArtifact.Materialization.OutputByteLength,
            new WorkspacePath(Path.Combine(root, "face-tint.dds")),
            Hash("face-tint"),
            4096,
            RuntimeAuthority: false)
        {
            ExternalDependencyAuthorities =
                nativeArtifact.ExternalDependencyAuthorities,
            ExternalProviderSidecarAuthorities =
                nativeArtifact.ExternalProviderSidecarAuthorities
        };
    }

    private static SkyrimFaceRecordRoute RebindRouteToDint(
        SkyrimFaceRecordRoute route,
        ImmutableArray<FormReference> oldHeadParts,
        FormReference oldRace,
        PluginName plugin,
        Sha256Hash pluginHash,
        string pluginPath)
    {
        ImmutableArray<FormReference> newHeadParts = RebindDintHeadParts(
            oldHeadParts, plugin);
        FormReference newRace = new(plugin, oldRace.FormId);
        var replacements = oldHeadParts
            .Select((oldReference, index) =>
                (oldReference, newReference: newHeadParts[index]))
            .Append((oldReference: oldRace, newReference: newRace))
            .ToDictionary(item => item.oldReference, item => item.newReference);
        FormReference Replace(FormReference value) =>
            replacements.TryGetValue(value, out FormReference replacement)
                ? replacement
                : new FormReference(plugin, value.FormId);
        SkyrimFaceRecordProvider provider = new(
            plugin, new WorkspacePath(pluginPath), pluginHash);

        SkyrimRaceFaceRecordRoute race = route.Race with
        {
            Reference = newRace,
            Provider = provider,
            MorphRaceReference = route.Race.MorphRaceReference is
                { } morphRace ? Replace(morphRace) : null,
            MaleDefaultHeadParts = route.Race.MaleDefaultHeadParts
                .Select(Replace).ToImmutableArray(),
            FemaleDefaultHeadParts = route.Race.FemaleDefaultHeadParts
                .Select(Replace).ToImmutableArray(),
            SelectedGenderDefaultHeadParts = route.Race
                .SelectedGenderDefaultHeadParts.Select(Replace).ToImmutableArray()
        };
        ImmutableArray<SkyrimFaceHeadPartRecordRoute> headParts = route
            .HeadParts.Select(item => item with
            {
                Reference = Replace(item.Reference),
                Provider = provider,
                ExtraParts = item.ExtraParts.Select(Replace).ToImmutableArray(),
                Parent = item.Parent is { } parent ? Replace(parent) : null,
                TextureSet = item.TextureSet is { } textureSet
                    ? textureSet with
                    {
                        Reference = Replace(textureSet.Reference),
                        Provider = provider
                    }
                    : null
            }).ToImmutableArray();
        ImmutableArray<SkyrimFaceHeadPartGraphRoute> graph = route
            .HeadPartGraph.Select(item => item with
            {
                OriginForm = Replace(item.OriginForm),
                RequiredOutputMaster = plugin,
                WinningForm = Replace(item.WinningForm),
                WinningPlugin = plugin,
                WinningPluginSha256 = pluginHash,
                HnamEdges = item.HnamEdges.Select(Replace).ToImmutableArray(),
                Parent = item.Parent is { } parent ? Replace(parent) : null,
                ValidRace = item.ValidRace is { } validRace
                    ? Replace(validRace)
                    : null
            }).ToImmutableArray();
        return route with
        {
            Race = race,
            RootHeadParts = route.RootHeadParts.Select(Replace).ToImmutableArray(),
            HeadParts = headParts,
            HeadPartGraph = graph
        };
    }

    private static FaceGenBakeTarget RebindTargetToDint(
        FaceGenBakeTarget target,
        ImmutableArray<FormReference> oldHeadParts,
        FormReference oldRace,
        PluginName plugin) =>
        target with
        {
            OriginatingPlugin = plugin,
            WinningPlugin = plugin,
            OverrideChain = [plugin],
            Race = new FormReference(plugin, oldRace.FormId),
            HeadParts = RebindDintHeadParts(oldHeadParts, plugin)
        };

    private static ImmutableArray<FormReference> RebindDintHeadParts(
        ImmutableArray<FormReference> oldHeadParts,
        PluginName plugin) =>
        oldHeadParts.Select((item, index) => new FormReference(
                plugin,
                index == 4
                    ? new FormId(0xBC05)
                    : index == 5
                        ? new FormId(0xBC06)
                        : item.FormId))
            .ToImmutableArray();

    private sealed record PreChangeFaceGenNpcBakeArtifact(
        FaceGenBakeTarget Target,
        WorkspacePath FaceGeomNif,
        Sha256Hash FaceGeomSha256,
        int FaceGeomByteLength,
        WorkspacePath FaceTintDds,
        Sha256Hash FaceTintSha256,
        int FaceTintByteLength,
        bool RuntimeAuthority)
    {
        public ImmutableArray<SkyrimAssetAuthority> ExternalDependencyAuthorities
        { get; init; } = [];

        public ImmutableArray<SkyrimAssetAuthority>
            ExternalProviderSidecarAuthorities { get; init; } = [];
    }

    private static Sha256Hash Hash(string value) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));

    private static string Fill(int length, char value) =>
        new(value, length);

    private static Sha256Hash HashBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void WriteAsset(string dataRoot, AssetPath path, byte[] bytes)
    {
        string output = Path.Combine(dataRoot,
            path.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, bytes);
    }

    private static byte[] WriteDenseTri(int vertexCount, string? morphName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        writer.Write((uint)vertexCount);
        writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
        writer.Write(0U); writer.Write(0U); writer.Write(morphName is null ? 0U : 1U);
        writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
        writer.Write(0U); writer.Write(0U);
        for (int index = 0; index < vertexCount; index++)
        {
            writer.Write(0F); writer.Write(0F); writer.Write(0F);
        }
        writer.Flush();
        return stream.ToArray();
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

    private static string PhysicalPath(string asset) => HeadpartGeometryRoot;

    private static bool IsExpectedSkyrimPlatformTexture(AssetPath path) =>
        path.Value.StartsWith("textures/cubemaps/", StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/female/femalehead.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/female/femalehead_msn.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/female/femalehead_s.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/female/femalehead_sk.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/male/malehead.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/male/malehead_msn.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/male/malehead_s.dds",
            StringComparison.OrdinalIgnoreCase) ||
        path.Value.Equals("textures/actors/character/male/malehead_sk.dds",
            StringComparison.OrdinalIgnoreCase);

    private static string GeometryDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            $"{item.Severity}:{item.Code}:{item.Message}"));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async ValueTask AssertCarrierContamination(
        string outputPath,
        byte[] pristine,
        string source,
        string replacement,
        ExternalHeadPartDependencyDescriptor descriptor,
        ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> ordinaryShapes,
        string label,
        CancellationToken cancellationToken)
    {
        byte[] contaminated = ReplaceSameLength(pristine, source, replacement);
        await File.WriteAllBytesAsync(outputPath, contaminated, cancellationToken);
        ExternalHeadPartFaceGeomExclusionVerificationResult result =
            await new ExternalHeadPartFaceGeomExclusionVerifier().VerifyAsync(
                new ExternalHeadPartFaceGeomExclusionVerificationRequest(
                    new WorkspacePath(outputPath), HashBytes(contaminated),
                    contaminated.LongLength, descriptor, ordinaryShapes),
                cancellationToken);
        Assert(!result.Verified && result.Attestation is null &&
               result.Diagnostics.Any(item =>
                   item.Code == ExternalHeadPartDiagnosticCodes.FaceGeomContaminated),
            $"A contaminated FaceGeom carrier containing {label} was accepted.");
        await File.WriteAllBytesAsync(outputPath, pristine, cancellationToken);
    }

    private static byte[] ReplaceSameLength(
        byte[] source,
        string oldValue,
        string newValue)
    {
        byte[] oldBytes = Encoding.Latin1.GetBytes(oldValue);
        byte[] newBytes = Encoding.Latin1.GetBytes(newValue);
        Assert(oldBytes.Length == newBytes.Length,
            $"Contamination fixture values '{oldValue}' and '{newValue}' differ in length.");
        byte[] copy = source.ToArray();
        int offset = copy.AsSpan().IndexOf(oldBytes);
        Assert(offset >= 0,
            $"Contamination fixture source '{oldValue}' was not present in the carrier.");
        newBytes.CopyTo(copy.AsSpan(offset, newBytes.Length));
        return copy;
    }

    private sealed class RecordingDiscovery(
        ExternalHeadPartDependencyDescriptor descriptor,
        string dataRoot,
        List<string> events,
        ExternalHeadPartDependencyDiscoveryStatus status =
            ExternalHeadPartDependencyDiscoveryStatus.Accepted) :
        IExternalHeadPartDependencyDiscovery
    {
        public int Calls { get; private set; }
        public bool SawProviderBytes { get; private set; }

        public ValueTask<ExternalHeadPartDependencyDiscoveryResult> DiscoverAsync(
            ExternalHeadPartDependencyDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Add("discovery");
            SawProviderBytes = request.RecordRoute.HeadPartGraph.Length == 2 &&
                File.Exists(Path.Combine(dataRoot,
                    request.RecordRoute.HeadPartGraph[0].ModelNif!.Value.Value));
            return ValueTask.FromResult(new ExternalHeadPartDependencyDiscoveryResult(
                status,
                status == ExternalHeadPartDependencyDiscoveryStatus.Accepted
                    ? descriptor : null,
                status == ExternalHeadPartDependencyDiscoveryStatus.Refused
                    ? [new Diagnostic("test-discovery-refused",
                        DiagnosticSeverity.Error, "Test discovery refusal.")] : []));
        }
    }

    private sealed class StaleAttestationVerifier(
        IExternalHeadPartFaceGeomExclusionVerifier inner) :
        IExternalHeadPartFaceGeomExclusionVerifier
    {
        public async ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult>
            VerifyAsync(
                ExternalHeadPartFaceGeomExclusionVerificationRequest request,
                CancellationToken cancellationToken)
        {
            ExternalHeadPartFaceGeomExclusionVerificationResult result =
                await inner.VerifyAsync(request, cancellationToken);
            if (!result.Verified || result.Attestation is null)
                return result;
            ExternalHeadPartFaceGeomExclusionAttestation stale =
                result.Attestation with
                {
                    OutputFaceGeomSha256 = Hash("stale-output")
                };
            stale = stale with
            {
                AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeAttestationHash(stale)
            };
            return result with { Attestation = stale };
        }
    }

    private sealed class RecordingMaterializationService(
        ISseFaceGeomCarrierMaterializationService inner,
        List<string> events) : ISseFaceGeomCarrierMaterializationService
    {
        public ValueTask<SseFaceGeomCarrierMaterializationAnalysisResult> AnalyzeAsync(
            SseFaceGeomCarrierMaterializationAnalyzeRequest request,
            CancellationToken cancellationToken) =>
            inner.AnalyzeAsync(request, cancellationToken);

        public async ValueTask<SseFaceGeomCarrierMaterializationResult> ApplyAsync(
            SseFaceGeomCarrierMaterializationProposal proposal,
            CancellationToken cancellationToken)
        {
            events.Add("materialization");
            return await inner.ApplyAsync(proposal, cancellationToken);
        }

        public ValueTask<SseFaceGeomCarrierMaterializationVerificationResult> VerifyAsync(
            SseFaceGeomCarrierMaterializationProposal proposal,
            CancellationToken cancellationToken) =>
            inner.VerifyAsync(proposal, cancellationToken);
    }

    private sealed class RecordingContentResolver(
        ISkyrimAssetContentResolver inner,
        List<string> events) : ISkyrimAssetContentResolver
    {
        public async ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            events.Add("provider-bytes");
            return await inner.ResolveAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingGeometryReader(
        ISseSelectedHeadpartNifGeometryReader inner,
        List<string> events) : ISseSelectedHeadpartNifGeometryReader
    {
        private bool recorded;

        public SseSelectedHeadpartNifGeometryReadResult Read(
            SseSelectedHeadpartNifGeometryReadRequest request)
        {
            if (!recorded)
            {
                events.Add("shape-materialization");
                recorded = true;
            }
            return inner.Read(request);
        }
    }

    private sealed class RecordingFaceBakeService(
        ISkyrimRaceMenuFaceBakeService inner,
        List<string> events) : ISkyrimRaceMenuFaceBakeService
    {
        public SkyrimRaceMenuFaceBakeResult Bake(
            SkyrimRaceMenuFaceBakeRequest request)
        {
            events.Add("face-bake");
            return inner.Bake(request);
        }
    }

    private sealed class RejectingExclusionVerifier :
        IExternalHeadPartFaceGeomExclusionVerifier
    {
        public ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult> VerifyAsync(
            ExternalHeadPartFaceGeomExclusionVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ExternalHeadPartFaceGeomExclusionVerificationResult(
                false,
                null,
                [new Diagnostic(
                    "test-external-facegeom-exclusion-refused",
                    DiagnosticSeverity.Error,
                    "Test verifier refusal." )]));
        }
    }

    private sealed class FixedPluginAuthorityLoader(
        PluginName plugin, string path, Sha256Hash hash) :
        ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(
                true, [new SkyrimFaceRecordPluginAuthority(
                    plugin, new WorkspacePath(path), hash)], []));
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
            return ValueTask.FromResult(new SkyrimFaceRecordRouteResult(true, route, []));
        }
    }

    private sealed class FixedFaceMorphSnapshotService(
        SkyrimFaceMorphSnapshot snapshot, Sha256Hash pluginHash) :
        ISkyrimFaceMorphSnapshotService
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

    private sealed class FixedRaceMenuCatalogAuthorityLoader(
        SkyrimRaceMenuCatalogParseRequest catalogRequest) :
        ISkyrimRaceMenuCatalogAuthorityLoader
    {
        public ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
            SkyrimRaceMenuCatalogAuthorityRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimRaceMenuCatalogAuthorityResult(
                true, catalogRequest, [], []));
        }
    }
}
