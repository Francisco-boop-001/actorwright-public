using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class SyntheticProductProviderFixture
{
    private const string BundleId = "blank-npc-v1";
    private const string PluginName = "ActorwrightBlankNpcProvider.esp";
    private const string SyntheticDiffusePath = "textures/actorwright/test-diffuse.dds";
    private const string RedistributionMarker = "product-generated-test-fixture";
    private static readonly string[] ShapeNames =
    [
        "SyntheticHead",
        "SyntheticBrows",
        "SyntheticEyes",
        "SyntheticMouth",
        "SyntheticHairline",
        "SyntheticHairLeft",
        "SyntheticHairRight"
    ];
    private static readonly double[] TransparentBaseColor = [0d, 0d, 0d, 0d];
    private static readonly double[] OpaqueWhiteLayerColor = [1d, 1d, 1d, 1d];
    private static readonly string[] SkyrimMasterNames = ["Skyrim.esm"];
    private static readonly string[] ProvenanceSources =
    [
        "Synthetic one-shape Skyrim SE NIF writer in NpcManager.Cli.Tests",
        "Synthetic seven-shape carrier assembly in NpcManager.Cli.Tests",
        "Synthetic 1024x1024 BGRA8 FaceTint writer in NpcManager.Cli.Tests"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static string Ensure(string applicationBaseDirectory)
    {
        string basePath = Path.GetFullPath(applicationBaseDirectory);
        string root = Path.Combine(basePath, "runtime", "product-fixtures",
            BundleId);
        if (Directory.Exists(root) || File.Exists(root))
        {
            if (File.Exists(Path.Combine(root, "provenance.json")) &&
                IsGeneratedFixture(Path.Combine(root, "provenance.json")))
            {
                var registry = new ApplicationProviderResourceRegistry(
                    new ApplicationResourcePath(basePath));
                if (!registry.TryGetDefaultBlankNpcFixture(out var reference) ||
                    reference is null)
                    throw new InvalidDataException(
                        "The generated test provider lost its registry.");
                var admitted = registry.Admit(reference, new FormId(0x800),
                    GameEdition.SkyrimSpecialEdition, NpcSex.Female);
                if (!admitted.Accepted)
                    throw new InvalidDataException(
                        "The generated test provider is incomplete or drifted: " +
                        string.Join("; ", admitted.Diagnostics.Select(item =>
                            item.Message)));
                return root;
            }

            throw new InvalidDataException(
                "A non-synthetic or partial provider already occupies the test output. " +
                "Clean the test output before generating the synthetic fixture.");
        }

        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "Data");
        string pluginPath = Path.Combine(data, PluginName);
        string faceGeomPath = Path.Combine(data, "meshes", "actors",
            "character", "FaceGenData", "FaceGeom", PluginName,
            "00000800.nif");
        string faceTintPath = Path.Combine(data, "textures", "actors",
            "character", "FaceGenData", "FaceTint", PluginName,
            "00000800.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(faceTintPath)!);

        WriteTemplatePlugin(pluginPath);
        byte[] faceTint = WriteFaceTintDds();
        File.WriteAllBytes(faceTintPath, faceTint);
        byte[] carrier = BuildCarrier();
        File.WriteAllBytes(faceGeomPath, carrier);

        string sourceAsset = $"textures/actors/character/FaceGenData/FaceTint/{PluginName}/00000800.dds";
        byte[] tintManifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1",
            npcFormId = "0x00000800",
            width = 1024,
            height = 1024,
            format = "bgra8",
            mipCount = 1,
            alphaMode = "preserve",
            baseColor = TransparentBaseColor,
            layers = new[]
            {
                new
                {
                    name = "synthetic-test-facetint",
                    source = sourceAsset,
                    provider = "Actorwright test fixture",
                    blend = "over",
                    opacity = 1d,
                    color = OpaqueWhiteLayerColor
                }
            },
            probes = new[] { new { x = 0, y = 0 },
                new { x = 1023, y = 1023 } }
        }, JsonOptions);
        string tintManifestPath = Path.Combine(root, "facetint-manifest.json");
        File.WriteAllBytes(tintManifestPath, tintManifest);

        byte[] dependencyManifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            id = "actorwright-blank-npc-v1",
            headParts = Array.Empty<object>(),
            looseAssets = Array.Empty<object>(),
            archives = Array.Empty<object>()
        }, JsonOptions);
        string dependencyManifestPath = Path.Combine(root,
            "dependency-manifest.json");
        File.WriteAllBytes(dependencyManifestPath, dependencyManifest);

        SseNifDocument document = SseFaceGeomCarrierCodec.Parse(carrier);
        QualifiedFaceGeomCarrierStructure structure =
            SseFaceGeomCarrierCodec.BuildStructure(document);
        var diagnostics = System.Collections.Immutable.ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(document, structure,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
            diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ||
            !structure.ReachableShapeNames.SequenceEqual(ShapeNames,
                StringComparer.Ordinal))
            throw new InvalidDataException(
                "The synthetic provider carrier failed its seven-shape contract: " +
                string.Join("; ", diagnostics.Select(item => item.Message)));

        byte[] providerManifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            providerId = "actorwright-blank-npc-v1",
            edition = "skyrimse",
            sex = "female",
            template = new
            {
                path = $"Data/{PluginName}",
                sha256 = HashFile(pluginPath),
                npcFormId = "0x00000800",
                masters = SkyrimMasterNames
            },
            faceGeom = new
            {
                path = $"Data/meshes/actors/character/FaceGenData/FaceGeom/{PluginName}/00000800.nif",
                sha256 = Hash(carrier).Value,
                graphSha256 = structure.GraphSha256.Value,
                shapeNames = structure.ReachableShapeNames
            },
            faceTint = new
            {
                manifestPath = "facetint-manifest.json",
                manifestSha256 = Hash(tintManifest).Value,
                providerRoot = "Data",
                sourceAssetPath = sourceAsset,
                sourceAssetSha256 = Hash(faceTint).Value
            },
            dependencies = new
            {
                manifestPath = "dependency-manifest.json",
                manifestSha256 = Hash(dependencyManifest).Value,
                dependencyId = "actorwright-blank-npc-v1",
                headPartCount = 0,
                looseAssetCount = 0,
                archiveCount = 0
            }
        }, JsonOptions);
        string providerManifestPath = Path.Combine(root,
            "provider-manifest.json");
        File.WriteAllBytes(providerManifestPath, providerManifest);

        byte[] provenance = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            bundleId = BundleId,
            owner = "Actorwright test suite",
            redistribution = RedistributionMarker,
            sources = ProvenanceSources,
            runtimeAuthority = false
        }, JsonOptions);
        string provenancePath = Path.Combine(root, "provenance.json");
        File.WriteAllBytes(provenancePath, provenance);

        object Row(string role, string path) => new
        {
            role,
            path = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            length = new FileInfo(path).Length,
            sha256 = HashFile(path)
        };
        byte[] registryBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            bundleId = BundleId,
            edition = "skyrimse",
            sex = "female",
            templateNpcFormId = "0x00000800",
            dataRootSha256 = HashDirectory(data),
            assets = new[]
            {
                Row("provenance", provenancePath),
                Row("provider-manifest", providerManifestPath),
                Row("template-plugin", pluginPath),
                Row("facegeom-carrier", faceGeomPath),
                Row("facetint-manifest", tintManifestPath),
                Row("facetint-source", faceTintPath),
                Row("dependency-manifest", dependencyManifestPath)
            }
        }, JsonOptions);
        File.WriteAllBytes(Path.Combine(root, "registry.json"), registryBytes);

        var generatedRegistry = new ApplicationProviderResourceRegistry(
            new ApplicationResourcePath(basePath));
        if (!generatedRegistry.TryGetDefaultBlankNpcFixture(out var bundle) ||
            bundle is null || !generatedRegistry.Admit(bundle,
                new FormId(0x800), GameEdition.SkyrimSpecialEdition,
                NpcSex.Female).Accepted)
            throw new InvalidDataException(
                "The generated synthetic product-provider bundle did not admit.");
        return root;
    }

    internal static byte[] WriteHeadpartNif(string? diffuse = null) =>
        GoldenSkyrimWorkflowResumptionTests.WriteHdptTopologyModel(
            141, diffuse, positions: VisibleFacePositions());

    private static bool IsGeneratedFixture(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty("redistribution",
                   out JsonElement marker) &&
               string.Equals(marker.GetString(), RedistributionMarker,
                   StringComparison.Ordinal);
    }

    private static void WriteTemplatePlugin(string path)
    {
        var key = ModKey.FromNameAndExtension(PluginName);
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = skyrim
        });
        mod.Npcs.Add(new Npc(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightSyntheticTestNpc",
            Name = "Synthetic Test NPC",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(new FormKey(skyrim, 0x13746)),
            Voice = new FormLinkNullable<IVoiceTypeGetter>(new FormKey(skyrim, 0x13ADC)),
            Class = new FormLink<IClassGetter>(new FormKey(skyrim, 0x13181)),
            CombatStyle = new FormLinkNullable<ICombatStyleGetter>(new FormKey(skyrim, 0x3BE1D)),
            DefaultOutfit = new FormLinkNullable<IOutfitGetter>(new FormKey(skyrim, 0x1DC10))
        });
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static byte[] BuildCarrier()
    {
        string faceTintPath =
            $"textures/actors/character/FaceGenData/FaceTint/{PluginName}/00000800.dds";
        System.Numerics.Vector3[] facePositions = VisibleFacePositions();
        var parts = ShapeNames.Select((name, index) =>
        {
            bool hair = index >= 4;
            string diffuse = index == 0
                ? SyntheticDiffusePath
                : hair
                    ? $"textures/actorwright/test-hair-{index}.dds"
                    : $"textures/actorwright/test-{name.ToLowerInvariant()}.dds";
            byte[] source = GoldenSkyrimWorkflowResumptionTests.WriteHdptTopologyModel(
                141,
                diffuse,
                hair ? 0x00FF_FFFFU : null,
                facePositions,
                name);
            return new SseFaceGeomCarrierAssemblyPart(
                new FormReference(new PluginName(PluginName),
                    new FormId(checked((uint)(0x810 + index)))),
                new AssetPath($"meshes/actorwright/test-part-{index}.nif"),
                Hash(source),
                source.ToImmutableArray(),
                name,
                UsesFaceTint: index == 0);
        }).ToImmutableArray();
        SseFaceGeomCarrierAssemblyResult result =
            new SseFaceGeomCarrierAssembler().Assemble(
                new SseFaceGeomCarrierAssemblyRequest(
                    parts,
                    new AssetPath(faceTintPath),
                    SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones));
        if (!result.Assembled || !result.Verified || result.Artifact is null)
            throw new InvalidDataException(
                "Synthetic seven-shape provider assembly failed: " +
                string.Join("; ", result.Diagnostics.Select(item =>
                    item.Code + ": " + item.Message)));
        return result.Artifact.Bytes.ToArray();
    }

    private static System.Numerics.Vector3[] VisibleFacePositions()
    {
        System.Numerics.Vector3[] positions = Enumerable.Repeat(
            new System.Numerics.Vector3(0F, 0F, 0.6F), 141).ToArray();
        positions[0] = new System.Numerics.Vector3(-0.65F, 0F, 0F);
        positions[1] = new System.Numerics.Vector3(0.65F, 0F, 0F);
        positions[2] = new System.Numerics.Vector3(0F, 0F, 1.2F);
        return positions;
    }

    internal static byte[] WriteFaceTintDds()
    {
        const int width = 1024;
        const int height = 1024;
        var bytes = new byte[128 + width * height * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4),
            0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4),
            0x0002_100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), width * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        for (int offset = 128; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = 160;
            bytes[offset + 1] = 180;
            bytes[offset + 2] = 210;
            bytes[offset + 3] = 255;
        }
        return bytes;
    }

    private static string HashDirectory(string root)
    {
        var text = new StringBuilder();
        foreach (string path in Directory.EnumerateFiles(root, "*",
                     SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            text.Append(Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Append('|').Append(HashFile(path)).Append('\n');
        return Hash(Encoding.UTF8.GetBytes(text.ToString())).Value;
    }

    private static string HashFile(string path) => Hash(File.ReadAllBytes(path)).Value;

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
