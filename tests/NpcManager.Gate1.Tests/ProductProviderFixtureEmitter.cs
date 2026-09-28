using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Gate1.Tests;

internal static partial class Program
{
    private const string ProductProviderPlugin =
        "ActorwrightBlankNpcProvider.esp";
    private static readonly double[] TransparentBlack = [0d, 0d, 0d, 0d];
    private static readonly double[] OpaqueWhite = [1d, 1d, 1d, 1d];
    private static readonly string[] ProductFixtureSources =
    [
        "tools/fixtures/m2 deterministic Skyrim plugin writer",
        "tests/fixtures/sse-packed-normals/generate.py (synthetic test-only NIF writer)",
        "tests/fixtures/sse-packed-normals/dynamic-16.nif (generated test input)",
        "Actorwright deterministic 1024x1024 BGRA8 FaceTint writer"
    ];
    private static readonly JsonSerializerOptions ProductFixtureJsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private static async Task EmitProductProviderFixtureAsync(
        string outputRoot)
    {
        string root = Path.GetFullPath(outputRoot);
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException(
                "The product-provider output root must be absent.");
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "Data");
        string plugin = Path.Combine(data, ProductProviderPlugin);
        string faceGeom = Path.Combine(data, "meshes", "actors",
            "character", "FaceGenData", "FaceGeom",
            ProductProviderPlugin, "00000800.nif");
        string faceTint = Path.Combine(data, "textures", "actors",
            "character", "FaceGenData", "FaceTint",
            ProductProviderPlugin, "00000800.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeom)!);
        Directory.CreateDirectory(Path.GetDirectoryName(faceTint)!);

        WriteTemplatePlugin(plugin);
        byte[] carrier = BuildCarrier();
        await File.WriteAllBytesAsync(faceGeom, carrier);
        await File.WriteAllBytesAsync(faceTint, BuildFaceTintDds());

        string sourceRoute =
            $"textures/actors/character/FaceGenData/FaceTint/{ProductProviderPlugin}/00000800.dds";
        byte[] faceTintManifest = Serialize(new
        {
            schemaVersion = "1",
            npcFormId = "0x00000800",
            width = 1024,
            height = 1024,
            format = "bgra8",
            mipCount = 1,
            alphaMode = "preserve",
            baseColor = TransparentBlack,
            layers = new[]
            {
                new
                {
                    name = "product-facetint",
                    source = sourceRoute,
                    provider = "Actorwright",
                    blend = "over",
                    opacity = 1d,
                    color = OpaqueWhite
                }
            },
            probes = new[] { new { x = 0, y = 0 },
                new { x = 1023, y = 1023 } }
        });
        string faceTintManifestPath = Path.Combine(root,
            "facetint-manifest.json");
        await File.WriteAllBytesAsync(faceTintManifestPath,
            faceTintManifest);

        byte[] dependencyManifest = Serialize(new
        {
            schemaVersion = 1,
            id = "actorwright-blank-npc-v1",
            headParts = Array.Empty<object>(),
            looseAssets = Array.Empty<object>(),
            archives = Array.Empty<object>()
        });
        string dependencyPath = Path.Combine(root,
            "dependency-manifest.json");
        await File.WriteAllBytesAsync(dependencyPath,
            dependencyManifest);

        SseNifDocument carrierDocument =
            SseFaceGeomCarrierCodec.Parse(carrier);
        QualifiedFaceGeomCarrierStructure carrierStructure =
            SseFaceGeomCarrierCodec.BuildStructure(carrierDocument);
        var carrierDiagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(
            carrierDocument,
            carrierStructure,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
            carrierDiagnostics);
        if (carrierDiagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "The generated product carrier did not qualify: " +
                string.Join(" | ", carrierDiagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));
        BethesdaNpcCreationTemplateSnapshot template =
            BethesdaNpcCreationAdapter.ReadTemplate(
                new WorkspacePath(plugin), new FormId(0x800));
        byte[] providerManifest = Serialize(new
        {
            schemaVersion = 1,
            providerId = "actorwright-blank-npc-v1",
            edition = "skyrimse",
            sex = "female",
            template = new
            {
                path = $"Data/{ProductProviderPlugin}",
                sha256 = HashFile(plugin).Value,
                npcFormId = "0x00000800",
                masters = template.Masters.Select(item => item.Value)
            },
            faceGeom = new
            {
                path =
                    $"Data/meshes/actors/character/FaceGenData/FaceGeom/{ProductProviderPlugin}/00000800.nif",
                sha256 = Hash(carrier).Value,
                graphSha256 = carrierStructure.GraphSha256.Value,
                shapeNames = carrierStructure.ReachableShapeNames
            },
            faceTint = new
            {
                manifestPath = "facetint-manifest.json",
                manifestSha256 = Hash(faceTintManifest).Value,
                providerRoot = "Data",
                sourceAssetPath = sourceRoute,
                sourceAssetSha256 = HashFile(faceTint).Value
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
        });
        string providerPath = Path.Combine(root,
            "provider-manifest.json");
        await File.WriteAllBytesAsync(providerPath, providerManifest);

        byte[] provenance = Serialize(new
        {
            schemaVersion = 1,
            bundleId = "blank-npc-v1",
            owner = "Actorwright",
            redistribution = "product-generated-test-fixture",
            sources = ProductFixtureSources,
            runtimeAuthority = false
        });
        string provenancePath = Path.Combine(root, "provenance.json");
        await File.WriteAllBytesAsync(provenancePath, provenance);

        object[] rows =
        [
            Row("provenance", root, provenancePath),
            Row("provider-manifest", root, providerPath),
            Row("template-plugin", root, plugin),
            Row("facegeom-carrier", root, faceGeom),
            Row("facetint-manifest", root, faceTintManifestPath),
            Row("facetint-source", root, faceTint),
            Row("dependency-manifest", root, dependencyPath)
        ];
        byte[] registry = Serialize(new
        {
            schemaVersion = 1,
            bundleId = "blank-npc-v1",
            edition = "skyrimse",
            sex = "female",
            templateNpcFormId = "0x00000800",
            dataRootSha256 = HashDirectory(data).Value,
            assets = rows
        });
        await File.WriteAllBytesAsync(Path.Combine(root, "registry.json"),
            registry);
    }

    private static void WriteTemplatePlugin(string output)
    {
        var key = new ModKey("ActorwrightBlankNpcProvider",
            ModType.Plugin);
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(new FormKey(key, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightBlankNpcTemplate",
            Name = "Actorwright Blank NPC Template",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(new FormKey(skyrim,
                0x00013746)),
            Voice = new FormLinkNullable<IVoiceTypeGetter>(new FormKey(
                skyrim, 0x00013ADC)),
            Class = new FormLink<IClassGetter>(new FormKey(skyrim,
                0x00013181)),
            CombatStyle = new FormLinkNullable<ICombatStyleGetter>(
                new FormKey(skyrim, 0x0003BE1D)),
            DefaultOutfit = new FormLinkNullable<IOutfitGetter>(new FormKey(
                skyrim, 0x0001DC10))
        });
        mod.WriteToBinary(new FilePath(output));
    }

    private static byte[] BuildFaceTintDds()
    {
        const int width = 1024;
        const int height = 1024;
        var bytes = new byte[128 + width * height * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0002_100F);
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

    private static byte[] BuildCarrier()
    {
        byte[] source = File.ReadAllBytes(Path.GetFullPath(
            "tests/fixtures/sse-packed-normals/dynamic-16.nif"));
        Sha256Hash hash = Hash(source);
        string[] names =
        [
            "00KLH_FemaleHeadNord",
            "KoralinaEyebrowsF02",
            "MJBFemaleEyesHumanGreen04",
            "FemaleMouthHumanoidDefault",
            "0_HAIRLINE_Female_Human_Straight",
            "0LassiHL",
            "0Lassi"
        ];
        ImmutableArray<SseFaceGeomCarrierAssemblyPart> parts = names
            .Select((name, index) =>
                new SseFaceGeomCarrierAssemblyPart(
                    new FormReference(
                        new PluginName(ProductProviderPlugin),
                        new FormId((uint)(0x810 + index))),
                    new AssetPath(
                        $"meshes/actorwright/product-part-{index}.nif"),
                    hash,
                    source.ToImmutableArray(),
                    name,
                    UsesFaceTint: index == 0))
            .ToImmutableArray();
        SseFaceGeomCarrierAssemblyResult result =
            new SseFaceGeomCarrierAssembler().Assemble(
                new SseFaceGeomCarrierAssemblyRequest(
                    parts,
                    new AssetPath(
                        $"Textures/Actors/Character/FaceGenData/FaceTint/{ProductProviderPlugin}/00000800.dds"),
                    SseFaceGeomCarrierSkeletonAuthority
                        .IdentityFaceGenBones));
        if (!result.Assembled || !result.Verified || result.Artifact is null)
            throw new InvalidOperationException(
                "Product carrier assembly failed: " + string.Join(" | ",
                    result.Diagnostics.Select(item =>
                        $"{item.Code}:{item.Message}")));
        return result.Artifact.Bytes.ToArray();
    }

    private static object Row(string role, string root, string path)
    {
        var info = new FileInfo(path);
        return new
        {
            role,
            path = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            length = info.Length,
            sha256 = HashFile(path).Value
        };
    }

    private static Sha256Hash HashDirectory(string root)
    {
        var text = new StringBuilder();
        foreach (string path in Directory.EnumerateFiles(root, "*",
                     SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            text.Append(Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Append('|').Append(HashFile(path).Value).Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value,
            ProductFixtureJsonOptions);

    private static Sha256Hash HashFile(string path) =>
        Hash(File.ReadAllBytes(path));

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
