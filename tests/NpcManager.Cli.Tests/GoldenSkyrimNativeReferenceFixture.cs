using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static ReferencePresetCatalogSelection PrepareNativeReferenceFixture(
        WorkspacePath repository, WorkspacePath root)
    {
        MaterializeProbeFixtures(repository.Value, Child(repository, "tools", "release",
            "protocol-v2-workflow-probes", "catalog.json").Value, "npc create-from-jslot", root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        PrepareGoldenPresetFixture(preset);
        CorrectNpcRequestFixture(repository.Value, root, Child(root, "npc-preflight", "request.json"), HashFile(preset));
        var providerPath = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        SkyrimTestPluginFactory.CreateGoldenNpcProviderWithHeadTexture(
            Child(root, "provider-source", "ActorwrightBlankNpcProvider.esp"), providerPath, includeChargenMorph: true);
        var provider = SkyrimMod.CreateFromBinary(providerPath.Value, SkyrimRelease.SkyrimSE);
        // The nonzero source is one CharGen TRI, not one file assigned to two roles.
        provider.HeadParts.Single().Parts.RemoveAt(0);
        var validRaces = provider.FormLists.AddNew(new FormKey(provider.ModKey, 0x824));
        validRaces.EditorID = "NativeReferenceNordRaces";
        validRaces.Items.Add(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x13746));
        provider.HeadParts.Single().ValidRaces.SetTo(validRaces.FormKey);
        var headPath = Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.nif");
        var head = ReadNativeReferenceShape(headPath,
            new AssetPath("meshes/Actors/Character/Actorwright/Head.nif"));
        byte[] headBytes = File.ReadAllBytes(headPath.Value);
        var skeleton = SseFaceGeomCarrierCodec.Parse(headBytes);
        Vector3 Translation(string name)
        {
            var bone = skeleton.Blocks.Single(b => b.Name == name);
            Require(skeleton.Blocks[skeleton.Roots.Single()].References.Any(r => r.Kind == "child" && r.Target == bone.Index),
                "Fixture skeleton bone must be a direct root child.");
            return new Vector3(BitConverter.ToSingle(headBytes, bone.Offset + 16),
                BitConverter.ToSingle(headBytes, bone.Offset + 20), BitConverter.ToSingle(headBytes, bone.Offset + 24));
        }
        var headTranslation = Translation("NPC Head [Head]");
        var spineTranslation = Translation("NPC Spine2 [Spn2]");
        Require(Hash(WriteHdptTopologyModel(141)) == "85811BA859D4833CC60D72098406D810E6BFC3A3BAE40928AE0979EB119A41B5",
            "Opt-in native geometry changed the original golden helper bytes.");
        foreach (string texture in new[] { "FemaleHead.dds", "FemaleHead_msn.dds", "FemaleHead_sk.dds" })
        {
            var destination = Child(root, "Data", "textures", "actors", "character", "female", texture);
            Directory.CreateDirectory(Path.GetDirectoryName(destination.Value)!);
            File.Copy(Child(root, "npc-preflight", "face.dds").Value, destination.Value);
        }
        File.WriteAllText(Child(root, "head-bounds.json").Value, JsonSerializer.Serialize(new
        {
            Count = head.VertexCount, X = new[] { head.RestPositions.Min(p => p.X), head.RestPositions.Max(p => p.X) },
            Y = new[] { head.RestPositions.Min(p => p.Y), head.RestPositions.Max(p => p.Y) },
            Z = new[] { head.RestPositions.Min(p => p.Z), head.RestPositions.Max(p => p.Z) }
            , DepthExtremes = head.RestPositions.OrderBy(p => p.Y).Take(3).Concat(
                head.RestPositions.OrderByDescending(p => p.Y).Take(3)).Select(p => new { p.X, p.Y, p.Z })
        }, NativeEvidenceJson));
        File.WriteAllBytes(Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.tri").Value,
            WriteNativeReferenceTri(head.RestPositions));
        const string diffuse = "textures/Actors/Character/Actorwright/Head.dds";
        var roles = new[] { (HeadPart.TypeEnum.Misc, "Mouth", 0x820U),
            (HeadPart.TypeEnum.Eyes, "Eyes", 0x821U), (HeadPart.TypeEnum.Eyebrows, "Brows", 0x822U),
            (HeadPart.TypeEnum.Hair, "Hair", 0x823U) };
        float midX = (head.RestPositions.Min(p => p.X) + head.RestPositions.Max(p => p.X)) / 2;
        float frontY = head.RestPositions.Min(p => p.Y);
        float lowZ = head.RestPositions.Min(p => p.Z);
        float height = head.RestPositions.Max(p => p.Z) - lowZ;
        for (int index = 0; index < roles.Length; index++)
        {
            var (type, name, id) = roles[index];
            var part = provider.HeadParts.AddNew(new FormKey(provider.ModKey, id));
            part.EditorID = "NativeReference" + name; part.Type = type;
            part.Flags = HeadPart.Flag.Female | HeadPart.Flag.Playable;
            part.ValidRaces.SetTo(validRaces.FormKey);
            string route = $"Actors/Character/Actorwright/{name}.nif";
            part.Model = new Model { File = route };
            // Product-owned, deliberately simple role markers. These are not likeness assets.
            float z = lowZ + height * (0.35F + index * 0.18F);
            Vector3[] positions = [new(midX - 0.6F, frontY, z),
                new(midX + 0.6F, frontY, z), new(midX, frontY, z + 0.35F)];
            var path = Child(root, "Data", "meshes", route);
            File.WriteAllBytes(path.Value, WriteHdptTopologyModel(3, diffuse,
                hairTint: type == HeadPart.TypeEnum.Hair ? 0xFFFFFFU : null, positions: positions, shapeName: name,
                headTranslation: headTranslation, spineTranslation: spineTranslation));
            var shape = ReadNativeReferenceShape(path, new AssetPath("meshes/" + route));
            Require(shape.VertexCount == 3 && shape.TextureCoordinates.Length == 3,
                "Real companion NIF has no complete UV topology: " + name);
        }
        provider.Npcs.Single().HeadParts.Clear();
        foreach (uint id in new uint[] { 0x802, 0x820, 0x821, 0x822, 0x823 })
            provider.Npcs.Single().HeadParts.Add(new FormKey(provider.ModKey, id));
        provider.WriteToBinary(providerPath.Value);
        JsonNode document = JsonNode.Parse(File.ReadAllBytes(preset.Value))!;
        document["headParts"] = new JsonArray(new[] { (0x802, 1), (0x820, 0), (0x821, 2), (0x822, 6), (0x823, 3) }
            .Select(part => (JsonNode)new JsonObject { ["formIdentifier"] = $"ActorwrightBlankNpcProvider.esp|0x{part.Item1:X8}",
                ["type"] = part.Item2 }).ToArray());
        File.WriteAllBytes(preset.Value, JsonSerializer.SerializeToUtf8Bytes(document));
        RefreshNativeReferenceBindings(root);
        FormReference Reference(uint id) => new(new PluginName(provider.ModKey.ToString()), new FormId(id));
        return new ReferencePresetCatalogSelection(true, Reference(0x802), Reference(0x820), Reference(0x821),
            Reference(0x822), Reference(0x823),
            [new ReferenceTintSelection(0, (int)TintAssets.TintMaskType.SkinTone, uint.MaxValue, 1)]);
    }

    private static void RefreshNativeReferenceBindings(WorkspacePath root)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllBytes(Child(root, "npc-preflight", name).Value))!.AsObject();
        void Write(string name, JsonObject value) => File.WriteAllBytes(Child(root, "npc-preflight", name).Value,
            JsonSerializer.SerializeToUtf8Bytes(value));
        string providerHash = HashFile(Child(root, "Data", "ActorwrightBlankNpcProvider.esp"));
        var authority = Read("record-authority.json");
        var bindings = authority["formBindings"]!.AsArray();
        var texture = bindings.Single(row => row!["signature"]!.GetValue<string>() == "TXST")!.DeepClone();
        bindings.Clear(); bindings.Add(texture);
        var dispositions = authority["headPartDispositions"]!.AsArray(); dispositions.Clear();
        foreach ((uint id, string type) in new[] { (0x802U, "face"), (0x820U, "misc"), (0x821U, "eyes"),
            (0x822U, "eyebrows"), (0x823U, "hair") })
        {
            string form = $"ActorwrightBlankNpcProvider.esp|0x{id:X8}";
            bindings.Add(new JsonObject { ["signature"] = "HDPT", ["sourceFormKey"] = form,
                ["providerFormKey"] = form, ["providerPluginName"] = "ActorwrightBlankNpcProvider.esp",
                ["providerPluginPath"] = "Data/ActorwrightBlankNpcProvider.esp", ["headPartType"] = type });
            dispositions.Add(new JsonObject { ["sourceFormKey"] = form, ["disposition"] = "mapped-record" });
        }
        foreach (JsonNode? binding in bindings) binding!["providerPluginSha256"] = providerHash;
        Write("record-authority.json", authority);
        RebindNpcPreflightFixtureHashes(root);
    }

    /// <summary>
    /// Rebinds the NAM9 authority, preset bundle, and request hashes after the
    /// provider plugin, preset, or record authority changed.
    /// </summary>
    private static void RebindNpcPreflightFixtureHashes(WorkspacePath root)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllBytes(Child(root, "npc-preflight", name).Value))!.AsObject();
        void Write(string name, JsonObject value) => File.WriteAllBytes(Child(root, "npc-preflight", name).Value,
            JsonSerializer.SerializeToUtf8Bytes(value));
        var standalone = Read("standalone-assets.json");
        standalone["nam9Authority"]!["pluginSha256"] = HashFile(Child(root, "Data", "ActorwrightBlankNpcProvider.esp"));
        Write("standalone-assets.json", standalone);
        var bundle = Read("preset-bundle.json");
        bundle["preset"]!["sha256"] = HashFile(Child(root, "npc-preflight", "fixture.jslot"));
        bundle["recordAuthority"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "record-authority.json"));
        Write("preset-bundle.json", bundle);
        var request = Read("request.json");
        request["presetBundle"]!["presetSha256"] = bundle["preset"]!["sha256"]!.DeepClone();
        request["presetBundle"]!["recordAuthoritySha256"] = bundle["recordAuthority"]!["manifestSha256"]!.DeepClone();
        request["presetBundle"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "preset-bundle.json"));
        request["standaloneAssets"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "standalone-assets.json"));
        Write("request.json", request);
    }

    private static SseSelectedHeadpartNifRestShape ReadNativeReferenceShape(WorkspacePath path, AssetPath asset)
    {
        byte[] bytes = File.ReadAllBytes(path.Value);
        var result = new SseSelectedHeadpartNifGeometryReader().Read(new SseSelectedHeadpartNifGeometryReadRequest(
            asset, new Sha256Hash(Hash(bytes)), bytes.ToImmutableArray()));
        Require(result.Accepted && result.Document!.Shapes.Length == 1,
            "Real selected NIF admission: " + JsonSerializer.Serialize(result.Diagnostics));
        return result.Document!.Shapes[0];
    }

    private static byte[] WriteNativeReferenceTri(ImmutableArray<Vector3> positions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("FRTRI003"u8);
        foreach (uint value in new uint[] { (uint)positions.Length, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0 }) writer.Write(value);
        foreach (Vector3 position in positions) { writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z); }
        // Synthetic nonzero native NoseLong/NoseShort channels on actual head topology.
        // Moving the upper face vertically exercises the native control, preserving the lower neck ring.
        float middle = (positions.Min(p => p.Z) + positions.Max(p => p.Z)) / 2;
        foreach ((string name, short direction) in new[] { ("NoseLong", (short)1), ("NoseShort", (short)-1) })
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name + '\0'); writer.Write(nameBytes.Length); writer.Write(nameBytes);
            writer.Write(0.01F);
            foreach (Vector3 position in positions)
            {
                writer.Write((short)0); writer.Write((short)0);
                writer.Write(position.Z > middle ? (short)(direction * 100) : (short)0);
            }
        }
        return stream.ToArray();
    }
}
