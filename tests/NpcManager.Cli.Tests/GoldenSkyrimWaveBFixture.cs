using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static void PrepareWaveBBrowFixture(WorkspacePath root)
    {
        var providerPath = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        var provider = SkyrimMod.CreateFromBinary(providerPath.Value, SkyrimRelease.SkyrimSE);
        var hair = new SkyrimMod(ModKey.FromNameAndExtension("SyntheticHair.esp"), SkyrimRelease.SkyrimSE);
        using var master = SkyrimMod.CreateFromBinaryOverlay(Child(root, "Data", "Skyrim.esm").Value, SkyrimRelease.SkyrimSE);
        var customRace = hair.Races.DuplicateInAsNewRecord(master.Races.Single(row => row.FormKey.ID == 0x13746), new FormKey(hair.ModKey, 0x900));
        customRace.EditorID = "SyntheticCotrRace";
        var race = customRace.FormKey;
        IArmorGetter baseSkin = master.Armors.Single(armor => armor.FormKey == customRace.Skin.FormKey);
        var skin = hair.Armors.DuplicateInAsNewRecord(baseSkin, new FormKey(hair.ModKey, 0x910));
        skin.EditorID = "SyntheticCotrNakedSkin";
        skin.Race.SetTo(race);
        skin.Armature.Clear();
        uint addonId = 0x911;
        foreach (var sourceAddon in baseSkin.Armature)
        {
            var addon = hair.ArmorAddons.DuplicateInAsNewRecord(master.ArmorAddons.Single(row => row.FormKey == sourceAddon.FormKey),
                new FormKey(hair.ModKey, addonId++));
            addon.Race.SetTo(race);
            skin.Armature.Add(addon.FormKey);
        }
        customRace.Skin.SetTo(skin.FormKey);
        var races = provider.FormLists.AddNew(new FormKey(provider.ModKey, 0x824));
        races.EditorID = "SyntheticCotrValidRaces";
        races.Items.Add(race);
        var eyesTexture = provider.TextureSets.AddNew(new FormKey(provider.ModKey, 0x825));
        eyesTexture.EditorID = "SyntheticRecordGreenEyes";
        eyesTexture.Diffuse = "eyes/WaveBGreen.dds";
        var brow = provider.HeadParts.AddNew(new FormKey(provider.ModKey, 0x820));
        brow.EditorID = "SyntheticCotrBrow141";
        brow.Type = HeadPart.TypeEnum.Eyebrows;
        brow.Flags = HeadPart.Flag.Female | HeadPart.Flag.Playable;
        brow.Model = new Model { File = "cotr-brow-collision/brow.nif" };
        brow.ValidRaces.SetTo(races.FormKey);
        foreach ((SkyrimHdptTriRole role, string name) in new[]
                 { (SkyrimHdptTriRole.RaceMorph, "race"), (SkyrimHdptTriRole.Mesh, "dialogue"), (SkyrimHdptTriRole.CharGen, "chargen") })
            brow.Parts.Add(new Part { PartType = (Part.PartTypeEnum)role, FileName = $"cotr-brow-collision/brow-{name}-501.tri" });
        var eyes = provider.HeadParts.AddNew(new FormKey(provider.ModKey, 0x821));
        eyes.EditorID = "SyntheticRecordEyes"; eyes.Type = HeadPart.TypeEnum.Eyes;
        eyes.Flags = HeadPart.Flag.Female | HeadPart.Flag.Playable;
        eyes.Model = new Model { File = "cotr-brow-collision/eyes.nif" };
        eyes.ValidRaces.SetTo(races.FormKey); eyes.TextureSet.SetTo(eyesTexture.FormKey);
        var hairRaces = hair.FormLists.AddNew(new FormKey(hair.ModKey, 0x801)); hairRaces.Items.Add(race);
        var hairPart = hair.HeadParts.AddNew(new FormKey(hair.ModKey, 0x800));
        hairPart.EditorID = "SyntheticExternalHair"; hairPart.Type = HeadPart.TypeEnum.Hair;
        hairPart.Flags = HeadPart.Flag.Female | HeadPart.Flag.Playable;
        hairPart.Model = new Model { File = "cotr-brow-collision/hair.nif" }; hairPart.ValidRaces.SetTo(hairRaces.FormKey);
        hair.WriteToBinary(Child(root, "Data", "SyntheticHair.esp").Value);
        Npc npc = provider.Npcs.Single();
        npc.Race.SetTo(race);
        npc.HeadParts.Clear();
        foreach (FormKey part in new[] { new FormKey(provider.ModKey, 0x802), brow.FormKey, eyes.FormKey, hairPart.FormKey })
            npc.HeadParts.Add(part);
        provider.WriteToBinary(providerPath.Value);

        string assetRoot = Child(root, "Data", "meshes", "cotr-brow-collision").Value;
        Directory.CreateDirectory(assetRoot);
        byte[] browBytes = WriteHdptTopologyModel(141);
        Require(Hash(browBytes) == "85811BA859D4833CC60D72098406D810E6BFC3A3BAE40928AE0979EB119A41B5",
            "The optional topology helper parameters changed default model bytes.");
        File.WriteAllBytes(Path.Combine(assetRoot, "brow.nif"), browBytes);
        // All synthetic head-part models share one coherent source skeleton.
        File.WriteAllBytes(Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.nif").Value, browBytes);
        File.WriteAllBytes(Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.tri").Value, WriteDenseTri(141));
        File.WriteAllBytes(Path.Combine(assetRoot, "eyes.nif"), WriteHdptTopologyModel(141, "textures/eyes/WaveBBrown.dds"));
        File.WriteAllBytes(Path.Combine(assetRoot, "hair.nif"), WriteHdptTopologyModel(141, hairTint: 0x222222));
        foreach (string role in new[] { "race", "dialogue", "chargen" })
        {
            File.WriteAllBytes(Path.Combine(assetRoot, $"brow-{role}-501.tri"), WriteDenseTri(501));
            File.WriteAllBytes(Path.Combine(assetRoot, $"brow-{role}-141.tri"), WriteDenseTri(141));
        }
        foreach (string texture in new[] { "textures/eyes/WaveBGreen.dds", "textures/eyes/WaveBBrown.dds", "textures/actors/character/female/femalehead.dds" })
        {
            string path = Path.Combine(root.Value, "Data", texture);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Child(root, "npc-preflight", "face.dds").Value, path);
        }
        RefreshWaveBBindings(root, repaired: false);
    }

    private static void RefreshWaveBBindings(WorkspacePath root, bool repaired)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllBytes(Child(root, "npc-preflight", name).Value))!.AsObject();
        void Write(string name, JsonObject value) => File.WriteAllBytes(Child(root, "npc-preflight", name).Value, JsonSerializer.SerializeToUtf8Bytes(value));
        string providerName = "ActorwrightBlankNpcProvider.esp";
        var preset = Read("fixture.jslot");
        uint browId = repaired ? 0x826u : 0x820u;
        (string Plugin, uint Id, string Type, int Number)[] parts =
        [ (providerName, 0x802, "face", 1), (providerName, browId, "eyebrows", 6),
          (providerName, 0x821, "eyes", 2), ("SyntheticHair.esp", 0x800, "hair", 3) ];
        preset["headParts"] = new JsonArray(parts.Select(part => (JsonNode)new JsonObject
            { ["formIdentifier"] = $"{part.Plugin}|0x{part.Id:X8}", ["type"] = part.Number }).ToArray());
        preset["actor"]!["hairColor"] = 0x5C5850;
        Write("fixture.jslot", preset);
        JsonObject authority = Read("record-authority.json");
        const string customRace = "SyntheticHair.esp|0x00000900";
        authority["race"]!["sourceFormKey"] = customRace;
        authority["race"]!["providerFormKey"] = customRace;
        authority["race"]!["providerPluginName"] = "SyntheticHair.esp";
        authority["race"]!["providerPluginPath"] = "Data/SyntheticHair.esp";
        authority["race"]!["providerPluginSha256"] = HashFile(Child(root, "Data", "SyntheticHair.esp"));
        var bindings = authority["formBindings"]!.AsArray();
        var oldTexture = bindings.Single(row => row!["signature"]!.GetValue<string>() == "TXST")!.DeepClone();
        bindings.Clear(); bindings.Add(oldTexture);
        var dispositions = authority["headPartDispositions"]!.AsArray(); dispositions.Clear();
        foreach (var part in parts)
        {
            string form = $"{part.Plugin}|0x{part.Id:X8}";
            bindings.Add(new JsonObject { ["signature"] = "HDPT", ["sourceFormKey"] = form, ["providerFormKey"] = form,
                ["providerPluginName"] = part.Plugin, ["providerPluginPath"] = "Data/" + part.Plugin,
                ["providerPluginSha256"] = HashFile(Child(root, "Data", part.Plugin)), ["headPartType"] = part.Type });
            dispositions.Add(new JsonObject { ["sourceFormKey"] = form, ["disposition"] = "mapped-record" });
        }
        foreach (JsonNode? binding in bindings)
            binding!["providerPluginSha256"] = HashFile(Child(root, "Data", binding["providerPluginName"]!.GetValue<string>()));
        authority["hairColorAuthority"]!["packedRgb"] = 0x5C5850;
        Write("record-authority.json", authority);
        JsonObject standalone = Read("standalone-assets.json");
        standalone["nam9Authority"]!["pluginSha256"] = HashFile(Child(root, "Data", providerName));
        Write("standalone-assets.json", standalone);
        JsonObject bundle = Read("preset-bundle.json");
        bundle["preset"]!["sha256"] = HashFile(Child(root, "npc-preflight", "fixture.jslot"));
        bundle["recordAuthority"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "record-authority.json"));
        Write("preset-bundle.json", bundle);
        JsonObject request = Read("request.json");
        request["references"]!["race"] = customRace;
        request["presetBundle"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "preset-bundle.json"));
        request["presetBundle"]!["presetSha256"] = HashFile(Child(root, "npc-preflight", "fixture.jslot"));
        request["presetBundle"]!["recordAuthoritySha256"] = HashFile(Child(root, "npc-preflight", "record-authority.json"));
        request["standaloneAssets"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "standalone-assets.json"));
        Write("request.json", request);
    }

    private static async Task RepairWaveBBrowAsync(WorkspacePath root, WaveBTranscript transcript)
    {
        var source = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        var composed = Child(root, "hdpt-composed", "ActorwrightBlankNpcProvider.esp");
        var patched = Child(root, "hdpt-patched", "ActorwrightBlankNpcProvider.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(composed.Value)!); Directory.CreateDirectory(Path.GetDirectoryName(patched.Value)!);
        var proposal = Child(root, "evidence", "brow.record-proposal.json");
        await transcript.Success("private brow propose", "records", "propose", "--json", "--edition", "skyrimse", "--type", "HDPT", "--mode", "new",
            "--form-id", "0x826", "--editor-id", "WaveBPrivateBrow", "--model", "meshes/cotr-brow-collision/brow.nif",
            "--tri-race", "meshes/cotr-brow-collision/brow-race-141.tri", "--tri-chargen", "meshes/cotr-brow-collision/brow-chargen-141.tri",
            "--tri-dialogue", "meshes/cotr-brow-collision/brow-dialogue-141.tri", "--valid-races", "ActorwrightBlankNpcProvider.esp|0x00000824",
            "--flags", "playable,female", "--part-type", "eyebrows", "--output", proposal.Value);
        await transcript.Success("private brow write", "plugin", "write", "--json", "--edition", "skyrimse", "--proposal", proposal.Value,
            "--plugin", source.Value, "--expected-sha256", HashFile(source), "--output", composed.Value,
            "--data-root", Child(root, "Data").Value, "--private-root", Child(root, "Data").Value);
        await transcript.Success("private brow PNAM patch", "npc", "face-patch", "--json", "--edition", "skyrimse", "--plugin", composed.Value,
            "--output", patched.Value, "--data-root", Child(root, "Data").Value, "--npc", "0x800", "--apply", "true",
            "--expected-sha256", HashFile(composed), "--headpart-replace", "ActorwrightBlankNpcProvider.esp|0x00000820=0x826");
        File.Copy(patched.Value, source.Value, overwrite: true);
    }
}
