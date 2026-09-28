using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static void PrepareFaceBakeDerivationFixture(string repositoryRoot, WorkspacePath root)
    {
        var requestPath = Child(root, "npc-preflight", "request.json");
        var request = JsonNode.Parse(File.ReadAllBytes(requestPath.Value))!;
        var providerPath = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        string oldProviderHash = HashFile(providerPath);
        var provider = SkyrimMod.CreateFromBinary(providerPath.Value, SkyrimRelease.SkyrimSE);
        HeadPart head = provider.HeadParts.Single(part => part.FormKey.ID == 0x802);
        head.ExtraParts.Clear();
        var parts = ImmutableArray.CreateBuilder<SseFaceGeomCarrierAssemblyPart>();
        byte[] modelBytes = SyntheticProductProviderFixture.WriteHeadpartNif();
        var modelSha = new Sha256Hash(Convert.ToHexString(SHA256.HashData(modelBytes)));
        for (int index = 0; index < 7; index++)
        {
            HeadPart part = index == 0 ? head : provider.HeadParts.DuplicateInAsNewRecord(
                head, new FormKey(provider.ModKey, checked((uint)(0x850 + index))));
            part.EditorID = index == 0 ? "DerivedHead" : "DerivedCarrier" + index;
            part.ExtraParts.Clear();
            if (index > 0)
            {
                part.Type = HeadPart.TypeEnum.Misc;
            }
            string modelPath = $"meshes/Actorwright/Derivation/Head{index}.nif";
            string physical = Path.Combine(root.Value, "Data", modelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
            File.WriteAllBytes(physical, modelBytes);
            part.Model!.File = modelPath[7..];
            parts.Add(new(new FormReference(new PluginName(provider.ModKey.FileName.String),
                new FormId(part.FormKey.ID)), new AssetPath(modelPath), modelSha,
                modelBytes.ToImmutableArray(), part.EditorID, index == 0));
        }
        provider.WriteToBinary(providerPath.Value);
        string providerHash = HashFile(providerPath);
        var presetPath = Child(root, "npc-preflight", "fixture.jslot");
        string oldPresetHash = HashFile(presetPath);
        var preset = JsonNode.Parse(File.ReadAllBytes(presetPath.Value))!;
        var recordPath = Child(root, "npc-preflight", "record-authority.json");
        var recordAuthority = JsonNode.Parse(File.ReadAllBytes(recordPath.Value))!;
        for (int index = 1; index < 7; index++)
        {
            string form = $"ActorwrightBlankNpcProvider.esp|0x{0x850 + index:X8}";
            preset["headParts"]!.AsArray().Add(new JsonObject { ["formIdentifier"] = form, ["type"] = 0 });
            var binding = recordAuthority["formBindings"]!.AsArray()[0]!.DeepClone();
            binding["sourceFormKey"] = form;
            binding["providerFormKey"] = form;
            binding["headPartType"] = "misc";
            recordAuthority["formBindings"]!.AsArray().Add(binding);
            recordAuthority["headPartDispositions"]!.AsArray().Add(new JsonObject
                { ["sourceFormKey"] = form, ["disposition"] = "mapped-record" });
        }
        File.WriteAllText(presetPath.Value, preset.ToJsonString(), new UTF8Encoding(false));
        string presetHash = HashFile(presetPath);
        File.WriteAllText(recordPath.Value, recordAuthority.ToJsonString(), new UTF8Encoding(false));
        var carrier = new SseFaceGeomCarrierAssembler().Assemble(new(parts.ToImmutable(),
            new AssetPath("textures/actors/character/FaceGenData/FaceTint/DerivationCarrier.esp/00000800.dds")));
        Require(carrier.Assembled && carrier.Verified && carrier.Artifact is not null,
            "Owned seven-shape derivation carrier failed: " + string.Join("; ", carrier.Diagnostics.Select(item => item.Message)));
        // The synthetic CharGen uses the same actual geometry and named shape
        // closure, in a distinct source file from the qualified provider graph.
        var sourceFace = Child(root, "npc-preflight", "face.nif");
        string oldFaceHash = HashFile(sourceFace);
        File.WriteAllBytes(sourceFace.Value, carrier.Artifact!.Bytes.ToArray());
        string sourceFaceHash = HashFile(sourceFace);
        byte[] carrierBytes = BuildSyntheticLegacyCarrier(carrier.Artifact!.Bytes.ToArray());
        var initialCarrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
        var headLayout = initialCarrier.Blocks.Single(block => block.Name == "DerivedHead").DynamicGeometry!;
        float initialX = BinaryPrimitives.ReadSingleLittleEndian(carrierBytes.AsSpan(headLayout.VertexDataOffset, 4));
        BinaryPrimitives.WriteSingleLittleEndian(carrierBytes.AsSpan(headLayout.VertexDataOffset, 4), initialX + 0.125f);
        var face = Child(root, "derivation-provider", "complete-carrier.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(face.Value)!);
        File.WriteAllBytes(face.Value, carrierBytes);
        string faceHash = HashFile(face);

        string product = SyntheticProductProviderFixture.Ensure(
            AppContext.BaseDirectory);
        string local = Path.Combine(root.Value, "derivation-provider");
        Directory.CreateDirectory(local);
        foreach (string name in new[] { "facetint-manifest.json", "dependency-manifest.json" })
            File.Copy(Path.Combine(product, name), Path.Combine(local, name));
        string sourceDds = "textures/actors/character/FaceGenData/FaceTint/ActorwrightBlankNpcProvider.esp/00000800.dds";
        string localDds = Path.Combine(local, "Data", sourceDds);
        Directory.CreateDirectory(Path.GetDirectoryName(localDds)!);
        File.Copy(Path.Combine(product, "Data", sourceDds), localDds);
        string template = Path.Combine(local, "ActorwrightBlankNpcProvider.esp");
        File.Copy(Path.Combine(product, "Data", "ActorwrightBlankNpcProvider.esp"), template);
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(product, "provider-manifest.json")))!;
        manifest["template"]!["path"] = "derivation-provider/ActorwrightBlankNpcProvider.esp";
        manifest["faceGeom"]!["path"] = "derivation-provider/complete-carrier.nif";
        manifest["faceGeom"]!["sha256"] = faceHash;
        var structure = SseFaceGeomCarrierCodec.BuildStructure(SseFaceGeomCarrierCodec.Parse(carrierBytes));
        manifest["faceGeom"]!["graphSha256"] = structure.GraphSha256.Value;
        manifest["faceGeom"]!["shapeNames"] = new JsonArray(structure.ReachableShapeNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        manifest["faceTint"]!["manifestPath"] = "derivation-provider/facetint-manifest.json";
        manifest["faceTint"]!["providerRoot"] = "derivation-provider/Data";
        manifest["dependencies"]!["manifestPath"] = "derivation-provider/dependency-manifest.json";
        var manifestPath = new WorkspacePath(Path.Combine(local, "provider-manifest.json"));
        File.WriteAllText(manifestPath.Value, manifest.ToJsonString(), new UTF8Encoding(false));
        request["providerContext"] = new JsonObject
        {
            ["templateNpcFormId"] = "0x00000800",
            ["manifestPath"] = "derivation-provider/provider-manifest.json",
            ["manifestSha256"] = HashFile(manifestPath),
            ["templatePlugin"] = "derivation-provider/ActorwrightBlankNpcProvider.esp",
            ["templateSha256"] = HashFile(new WorkspacePath(template)),
            ["faceGeomCarrier"] = "derivation-provider/complete-carrier.nif", ["faceGeomSha256"] = faceHash,
            ["faceTintManifest"] = "derivation-provider/facetint-manifest.json",
            ["faceTintProviderRoot"] = "derivation-provider/Data",
            ["dependencyManifest"] = "derivation-provider/dependency-manifest.json"
        };
        request["schemaVersion"] = 2;
        request["existingNpcTarget"] = new JsonObject
        {
            ["sourcePlugin"] = "Data/ActorwrightBlankNpcProvider.esp",
            ["sourcePluginSha256"] = providerHash, ["targetFormId"] = "0x00000800"
        };
        foreach (string file in new[] { "record-authority.json", "standalone-assets.json", "preset-bundle.json" })
        {
            string path = Path.Combine(root.Value, "npc-preflight", file);
            string json = File.ReadAllText(path).Replace(oldProviderHash, providerHash,
                StringComparison.OrdinalIgnoreCase).Replace(oldFaceHash, sourceFaceHash, StringComparison.OrdinalIgnoreCase)
                .Replace(oldPresetHash, presetHash, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        var bundlePath = Child(root, "npc-preflight", "preset-bundle.json");
        var bundle = JsonNode.Parse(File.ReadAllBytes(bundlePath.Value))!;
        bundle["schemaVersion"] = 1;
        bundle.AsObject().Remove("providerAuthority");
        bundle["providerContext"] = new JsonObject
        {
            ["manifestPath"] = "derivation-provider/provider-manifest.json",
            ["manifestSha256"] = HashFile(manifestPath),
            ["dependencyManifestPath"] = "derivation-provider/dependency-manifest.json",
            ["dependencyManifestSha256"] = HashFile(new WorkspacePath(Path.Combine(local, "dependency-manifest.json")))
        };
        string recordHash = HashFile(Child(root, "npc-preflight", "record-authority.json"));
        string oldRecordHash = request["presetBundle"]!["recordAuthoritySha256"]!.GetValue<string>();
        File.WriteAllText(bundlePath.Value, bundle.ToJsonString().Replace(oldRecordHash, recordHash,
            StringComparison.OrdinalIgnoreCase), new UTF8Encoding(false));
        request["presetBundle"]!["recordAuthoritySha256"] = recordHash;
        request["presetBundle"]!["presetSha256"] = presetHash;
        request["presetBundle"]!["faceGeomSha256"] = sourceFaceHash;
        request["presetBundle"]!["manifestSha256"] = HashFile(bundlePath);
        var standalonePath = Child(root, "npc-preflight", "standalone-assets.json");
        var standalone = JsonNode.Parse(File.ReadAllBytes(standalonePath.Value))!;
        byte[] sourceTint = File.ReadAllBytes(Child(root, "npc-preflight", "face.dds").Value);
        standalone["faceTint"]!["height"] = BinaryPrimitives.ReadUInt32LittleEndian(sourceTint.AsSpan(12, 4));
        standalone["faceTint"]!["width"] = BinaryPrimitives.ReadUInt32LittleEndian(sourceTint.AsSpan(16, 4));
        standalone["packageAssets"] = new JsonArray(Directory.GetFiles(Path.Combine(root.Value, "Data", "textures"), "*.dds", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => (JsonNode?)new JsonObject
            {
                ["sourcePath"] = Path.GetRelativePath(root.Value, path).Replace('\\', '/'),
                ["sha256"] = HashFile(new WorkspacePath(path)),
                ["destination"] = Path.GetRelativePath(Child(root, "Data").Value, path).Replace('\\', '/')
            }).ToArray());
        File.WriteAllText(standalonePath.Value, standalone.ToJsonString(), new UTF8Encoding(false));
        request["standaloneAssets"]!["manifestSha256"] = HashFile(standalonePath);
        File.WriteAllText(requestPath.Value, request.ToJsonString(), new UTF8Encoding(false));
        string catalog = Path.Combine(root.Value, "Data", "meshes", "actors", "character", "FaceGenMorphs",
            "ActorwrightBlankNpcProvider.esp");
        Directory.CreateDirectory(catalog);
        File.WriteAllText(Path.Combine(catalog, "races.ini"), "NordRace=derived.slider\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(catalog, "derived.slider"), "# Explicit empty synthetic neutral slider catalog\n", new UTF8Encoding(false));
    }
}
