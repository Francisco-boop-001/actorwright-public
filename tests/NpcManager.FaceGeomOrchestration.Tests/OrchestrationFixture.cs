using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGeomOrchestration.Tests;

internal sealed class OrchestrationFixture : IDisposable
{
    private readonly WorkspacePath _root;

    private OrchestrationFixture(
        WorkspacePath root,
        RaceMenuNpcFaceGeomBuildRequest request,
        SkyrimFaceBakeAuthority authority,
        SkyrimFaceRecordRoute route,
        ImmutableDictionary<string, FixtureGeometry> geometry,
        byte[] outputBytes,
        QualifiedFaceGeomCarrierStructure structure)
    {
        _root = root;
        Request = request;
        Authority = authority;
        Route = route;
        Geometry = geometry;
        OutputBytes = outputBytes;
        Structure = structure;
    }

    public RaceMenuNpcFaceGeomBuildRequest Request { get; }
    public SkyrimFaceBakeAuthority Authority { get; }
    public SkyrimFaceRecordRoute Route { get; }
    public ImmutableDictionary<string, FixtureGeometry> Geometry { get; }
    public byte[] OutputBytes { get; }
    public QualifiedFaceGeomCarrierStructure Structure { get; }

    public static OrchestrationFixture Create()
    {
        WorkspacePath projectRoot = FindProjectRoot();
        string testParent = Path.Combine(projectRoot.Value, "03-builds", "work",
            "facegeom-orchestration-tests");
        Directory.CreateDirectory(testParent);
        var root = new WorkspacePath(Path.Combine(testParent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var stage = new WorkspacePath(Path.Combine(root.Value, "stage"));
        Directory.CreateDirectory(stage.Value);

        WorkspacePath manifest = Write(root, "authority.json", Encoding.UTF8.GetBytes("authority-v1"));
        WorkspacePath charGen = Write(root, "source-chargen.nif", Encoding.UTF8.GetBytes("chargen-evidence"));
        WorkspacePath carrier = Write(root, "carrier.nif", Encoding.UTF8.GetBytes("complete-carrier"));
        WorkspacePath recordPlugin = Write(root, "Synthetic.esm", Encoding.UTF8.GetBytes("record-provider"));
        Sha256Hash manifestHash = FixtureHash.File(manifest);
        Sha256Hash charGenHash = FixtureHash.File(charGen);
        Sha256Hash carrierHash = FixtureHash.File(carrier);
        Sha256Hash recordHash = FixtureHash.File(recordPlugin);
        var plugin = new PluginName("Synthetic.esm");
        var race = new FormReference(plugin, new FormId(0x100));
        ImmutableArray<FormReference> parts = Enumerable.Range(1, 7)
            .Select(index => new FormReference(plugin, new FormId((uint)index)))
            .ToImmutableArray();

        var assets = ImmutableArray.CreateBuilder<SkyrimFaceBakeAuthorityAsset>();
        var carriers = ImmutableArray.CreateBuilder<SkyrimFaceBakeCarrierShapeAuthority>();
        var triInputs = ImmutableArray.CreateBuilder<SkyrimFaceBakeShapeTriAuthority>();
        var geometries = ImmutableDictionary.CreateBuilder<string, FixtureGeometry>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < parts.Length; index++)
        {
            string modelName = $"Model{index + 1}";
            string carrierName = $"Carrier{index + 1}";
            var modelPath = new AssetPath($"meshes/synthetic/model-{index + 1}.nif");
            var triPath = new AssetPath($"meshes/synthetic/model-{index + 1}.tri");
            byte[] modelBytes = [(byte)(index + 1), 0x4E, 0x49, 0x46];
            byte[] triBytes = [(byte)(index + 1), 0x54, 0x52, 0x49];
            SkyrimFaceBakeAuthorityAsset model = Asset(root,
                $"model-{index + 1}", modelPath, modelBytes);
            SkyrimFaceBakeAuthorityAsset tri = Asset(root,
                $"tri-{index + 1}", triPath, triBytes);
            assets.Add(model);
            assets.Add(tri);

            ImmutableArray<Vector3> positions =
            [
                new Vector3(index + 1F, index + 0.25F, -index),
                new Vector3(index + 1.5F, index + 0.75F, -index - 0.5F)
            ];
            Sha256Hash positionHash = FixtureHash.Positions(positions);
            Sha256Hash topologyHash = FixtureHash.Text($"topology-{index + 1}");
            carriers.Add(new SkyrimFaceBakeCarrierShapeAuthority(
                parts[index], model, modelName, carrierName,
                positionHash, topologyHash, topologyHash));
            triInputs.Add(new SkyrimFaceBakeShapeTriAuthority(
                carrierName, null, null, tri,
                ImmutableArray<SkyrimFaceBakeAuthorityAsset>.Empty));
            geometries.Add(modelPath.Value, new FixtureGeometry(
                modelName, positions, positionHash, topologyHash));
        }

        byte[] configBytes = Encoding.UTF8.GetBytes("[Male]\n[Female]\n");
        SkyrimFaceBakeAuthorityAsset config = Asset(root, "catalog",
            new AssetPath("meshes/actors/character/facegenmorphs/Synthetic.esm/sliders.ini"),
            configBytes);
        assets.Add(config);
        var providerAuthority = new SkyrimFaceRecordPluginAuthority(plugin,
            recordPlugin, recordHash);
        var authority = new SkyrimFaceBakeAuthority(
            "synthetic-seven-shape-authority-v1",
            [plugin],
            [providerAuthority],
            assets.ToImmutable(),
            [new SkyrimFaceBakeCatalogConfigAuthority(plugin, config)],
            triInputs.ToImmutable(),
            carriers.ToImmutable(),
            ImmutableArray<FormReference>.Empty,
            ImmutableArray<AssetPath>.Empty,
            manifestHash);
        SkyrimFaceRecordRoute route = BuildRoute(race, parts, providerAuthority,
            carriers.ToImmutable(), triInputs.ToImmutable());
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedCustomMorphs =
        [
            new("SyntheticNose", 0.25F),
            new("SyntheticJaw", -0.125F),
            new("SyntheticNose", 0.5F)
        ];
        RaceMenuNpcAppearancePlan plan = BuildPlan(root, race, parts[..4],
            recordPlugin, recordHash, charGen, charGenHash, carrier, carrierHash,
            orderedCustomMorphs);
        var output = new WorkspacePath(Path.Combine(stage.Value, "compiled-facegeom.nif"));
        var request = new RaceMenuNpcFaceGeomBuildRequest(
            plan,
            new SkyrimFaceMorphSnapshot(ImmutableArray<float>.Empty, 0F,
                ImmutableArray<uint>.Empty, HasNam9: false, HasNama: false),
            orderedCustomMorphs,
            root,
            manifest,
            manifestHash,
            carrier,
            carrierHash,
            charGen,
            charGenHash,
            stage,
            output);
        byte[] outputBytes = Encoding.UTF8.GetBytes("verified-generated-facegeom");
        var structure = new QualifiedFaceGeomCarrierStructure(
            8, 8, 1, 1, 0, 7, 0,
            carriers.Select(item => item.CarrierShapeName).ToImmutableArray(),
            [new QualifiedFaceGeomCarrierCensusEntry("BSDynamicTriShape", 7)],
            FixtureHash.Text("graph"));
        return new OrchestrationFixture(root, request, authority, route,
            geometries.ToImmutable(), outputBytes, structure);
    }

    private static RaceMenuNpcAppearancePlan BuildPlan(
        WorkspacePath root,
        FormReference race,
        ImmutableArray<FormReference> selected,
        WorkspacePath recordPlugin,
        Sha256Hash recordHash,
        WorkspacePath charGen,
        Sha256Hash charGenHash,
        WorkspacePath carrier,
        Sha256Hash carrierHash,
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedCustomMorphs)
    {
        ImmutableArray<PresetHeadPart> presetParts = selected.Select(reference =>
            new PresetHeadPart(new PresetIdentifier(reference.ToString(),
                reference.Plugin, reference.FormId), 0)).ToImmutableArray();
        var bindings = selected.Select((reference, index) =>
            new RaceMenuResolvedHeadPart(presetParts[index],
                new RaceMenuNpcFormBinding(new RecordSignature("HDPT"),
                    reference, reference, reference.Plugin, recordPlugin, recordHash,
                    index switch
                    {
                        0 => NpcHeadPartType.Face,
                        1 => NpcHeadPartType.Eyes,
                        2 => NpcHeadPartType.Eyebrows,
                        _ => NpcHeadPartType.Hair
                    }))).ToImmutableArray();
        ImmutableArray<RaceMenuNpcHeadPartDisposition> dispositions = bindings
            .Select(item => new RaceMenuNpcHeadPartDisposition(item.Source,
                RaceMenuNpcHeadPartDispositionKind.MappedRecord, item, null))
            .ToImmutableArray();
        var appearance = new PresetAppearance(
            Gender: 1,
            HeadParts: presetParts,
            HairColor: null,
            Weight: new PresetWeight(0F, null, null, null),
            Morphs: ImmutableDictionary<string, float>.Empty,
            BodyMorphs: ImmutableDictionary<string, float>.Empty,
            CustomMorphs: ImmutableDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, float>("SyntheticJaw", -0.125F),
                new KeyValuePair<string, float>("SyntheticNose", 0.5F),
                new KeyValuePair<string, float>("IgnoredRootOnlyMorph", 0.75F)
            ]),
            SliderMorphs: ImmutableArray<float>.Empty,
            Tints: ImmutableArray<PresetTint>.Empty,
            Overlays: ImmutableArray<PresetOverlay>.Empty,
            Skin: null,
            Presence: new PresetFieldPresence(true, true, false, true,
                false, false, false, false, false),
            UnknownFields: ImmutableArray<PresetUnknownField>.Empty,
            RaceMenu: new RaceMenuPresetData(
                null,
                ImmutableArray<uint>.Empty,
                10000,
                ImmutableArray<RaceMenuSculptPart>.Empty,
                ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty,
                ImmutableArray<RaceMenuBodyOverlay>.Empty,
                ImmutableArray<SkyrimNodeTransform>.Empty,
                ImmutableArray<SkyrimSkinOverride>.Empty),
            OrderedCustomMorphs: orderedCustomMorphs);
        var preset = new PresetDocument(PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition, appearance, FixtureHash.Text("preset"),
            ImmutableArray<Diagnostic>.Empty);
        var bundle = new RaceMenuNpcPresetBundle(
            PathAt(root, "bundle.json"), FixtureHash.Text("bundle"),
            PathAt(root, "preset.jslot"), preset.SourceHash,
            charGen, charGenHash,
            PathAt(root, "facetint.dds"), FixtureHash.Text("facetint"),
            new RaceMenuNpcRecordAuthority(PathAt(root, "record-authority.json"),
                FixtureHash.Text("record-authority")));
        var provider = new BlankNpcProviderBindingRequest(
            PathAt(root, "provider.json"), FixtureHash.Text("provider"),
            GameEdition.SkyrimSpecialEdition, NpcSex.Female,
            PathAt(root, "template.esp"), FixtureHash.Text("template"),
            new FormId(0x800), carrier, carrierHash,
            PathAt(root, "facetint-manifest.json"), root,
            PathAt(root, "dependency.json"));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            bundle,
            provider,
            PathAt(root, "final-output"),
            new PluginName("Output.esp"),
            new NpcCreationIdentity(new EditorId("GeneratedNpc"), new NpcName("Generated NPC")),
            new SkyrimNpcCreationTraits(NpcSex.Female, NpcCreationRole.Follower,
                true, false, true, false, true),
            new SkyrimNpcCreationReferences(race, race, race, race, race),
            new SkyrimNpcCreationStats(new NpcLevelValue(NpcLevelMode.Multiplier, 1M),
                0, 0, 0, 1, 80, 100, 0, 0, 50, 50, 50, 1F, 0F, 255));
        var providerArtifact = new BlankNpcProviderArtifact(
            "1", "qualified-provider", "synthetic", provider.ManifestPath,
            provider.ExpectedManifestSha256, GameEdition.SkyrimSpecialEdition,
            NpcSex.Female, [race.Plugin], FixtureHash.Text("carrier-graph"),
            selected.Select(item => item.ToString()).ToImmutableArray(),
            new AssetPath("textures/synthetic/facetint.dds"),
            FixtureHash.Text("facetint-source"), FixtureHash.Text("facetint-manifest"),
            "synthetic-dependencies", FixtureHash.Text("dependencies"),
            selected.Length, 0, 0);
        var raceBinding = new RaceMenuNpcFormBinding(new RecordSignature("RACE"),
            race, race, race.Plugin, recordPlugin, recordHash, null);
        return new RaceMenuNpcAppearancePlan(
            "1", "synthetic-bundle", build, preset, providerArtifact,
            FixtureHash.Text("bundle-manifest"), "record-authority",
            FixtureHash.Text("record-authority"), charGenHash,
            FixtureHash.Text("facetint"), raceBinding, dispositions, bindings,
            null, null, ImmutableArray<RaceMenuNpcTintDisposition>.Empty,
            ImmutableArray<RaceMenuNpcTintLayerBinding>.Empty, null,
            [race.Plugin], [race.Plugin], ImmutableArray<RaceMenuNpcFieldCoverage>.Empty,
            ImmutableArray<Diagnostic>.Empty, RuntimeAuthority: false);
    }

    private static SkyrimFaceRecordRoute BuildRoute(
        FormReference race,
        ImmutableArray<FormReference> parts,
        SkyrimFaceRecordPluginAuthority provider,
        ImmutableArray<SkyrimFaceBakeCarrierShapeAuthority> carriers,
        ImmutableArray<SkyrimFaceBakeShapeTriAuthority> triInputs)
    {
        var identity = new SkyrimFaceRecordProvider(provider.Plugin,
            provider.Path, provider.ExpectedSha256);
        var records = ImmutableArray.CreateBuilder<SkyrimFaceHeadPartRecordRoute>();
        for (int index = 0; index < parts.Length; index++)
        {
            SkyrimFaceBakeCarrierShapeAuthority carrier = carriers[index];
            SkyrimFaceBakeShapeTriAuthority tri = triInputs[index];
            records.Add(new SkyrimFaceHeadPartRecordRoute(
                parts[index], identity, $"HeadPart{index + 1}",
                index switch
                {
                    0 => NpcHeadPartType.Face,
                    1 => NpcHeadPartType.Eyes,
                    2 => NpcHeadPartType.Eyebrows,
                    3 or 5 or 6 => NpcHeadPartType.Hair,
                    _ => NpcHeadPartType.Misc
                },
                index switch
                {
                    0 => NpcHeadPartType.Face,
                    1 => NpcHeadPartType.Eyes,
                    2 => NpcHeadPartType.Eyebrows,
                    3 or 5 or 6 => NpcHeadPartType.Hair,
                    _ => NpcHeadPartType.Misc
                },
                carrier.ModelNif.AssetPath,
                tri.MeshMorphTri is null
                    ? ImmutableArray<SkyrimHdptTriRoute>.Empty
                    : [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh,
                        tri.MeshMorphTri.AssetPath)],
                index == 3 ? [parts[5], parts[6]] : ImmutableArray<FormReference>.Empty,
                index is 5 or 6 ? parts[3] : null,
                index is 5 or 6 ? 1 : 0,
                IsSelected: index < 4,
                IsRaceDefault: index == 4));
        }
        var raceRoute = new SkyrimRaceFaceRecordRoute(
            race, identity, "SyntheticRace", null, "SyntheticRace",
            ImmutableArray<SkyrimRaceKeywordRoute>.Empty,
            ImmutableArray<FormReference>.Empty,
            [parts[4]], [parts[4]]);
        return new SkyrimFaceRecordRoute(raceRoute, parts[..5], records.ToImmutable());
    }

    private static SkyrimFaceBakeAuthorityAsset Asset(
        WorkspacePath root, string id, AssetPath path, byte[] content)
    {
        Sha256Hash hash = FixtureHash.Of(content);
        return new SkyrimFaceBakeAuthorityAsset(id, "synthetic-loose",
            SkyrimFaceBakeAuthorityProviderKind.Loose,
            PathAt(root, $"providers/{id}{Path.GetExtension(path.Value)}"),
            hash, path, content.LongLength, hash, content.ToImmutableArray());
    }

    private static WorkspacePath Write(WorkspacePath root, string relative, byte[] content)
    {
        WorkspacePath path = PathAt(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
        File.WriteAllBytes(path.Value, content);
        return path;
    }

    private static WorkspacePath PathAt(WorkspacePath root, string relative) =>
        new(Path.Combine(root.Value, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static WorkspacePath FindProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
            {
                return new WorkspacePath(current.FullName);
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Actorwright.sln project root was not found.");
    }

    public void Dispose()
    {
        string parent = Path.Combine(FindProjectRoot().Value, "03-builds", "work",
            "facegeom-orchestration-tests");
        if (!_root.IsUnder(new WorkspacePath(parent)))
        {
            throw new InvalidOperationException("Test cleanup root escaped its owned directory.");
        }
        if (Directory.Exists(_root.Value)) Directory.Delete(_root.Value, recursive: true);
    }
}

internal sealed record FixtureGeometry(
    string ShapeName,
    ImmutableArray<Vector3> Positions,
    Sha256Hash PositionSha256,
    Sha256Hash TopologySha256);

internal static class FixtureHash
{
    public static Sha256Hash Of(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    public static Sha256Hash Text(string value) => Of(Encoding.UTF8.GetBytes(value));

    public static Sha256Hash File(WorkspacePath path) =>
        Of(System.IO.File.ReadAllBytes(path.Value));

    public static Sha256Hash Positions(ImmutableArray<Vector3> positions)
    {
        byte[] bytes = new byte[positions.Length * 12];
        int offset = 0;
        foreach (Vector3 position in positions)
        {
            Write(bytes, ref offset, position.X);
            Write(bytes, ref offset, position.Y);
            Write(bytes, ref offset, position.Z);
        }
        return Of(bytes);
    }

    private static void Write(Span<byte> bytes, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..],
            BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
    }
}
