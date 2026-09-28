using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunWholeSkinPatchAsync()
    {
        var root = Child(new WorkspacePath(Environment.CurrentDirectory), "artifacts", "task27", "patch-" + Guid.NewGuid().ToString("N"));
        var data = Child(root, "Data");
        Directory.CreateDirectory(data.Value);
        var source = Child(data, "SharedSkin.esp");
        var output = Child(root, "PrivateSkin.esp");
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("SharedSkin.esp"), SkyrimRelease.SkyrimSE);
        FormKey Key(uint id) => new(mod.ModKey, id);
        var race = mod.Races.AddNew(Key(0x810)); race.EditorID = "Task27Race";
        mod.Races.AddNew(Key(0x811)).EditorID = "Task27OtherRace";
        var texture = mod.TextureSets.AddNew(Key(0x820)); texture.EditorID = "SharedTexture";
        texture.Diffuse = "actors/character/shared.dds";
        var head = mod.HeadParts.AddNew(Key(0x821)); head.EditorID = "SharedHead";
        head.Type = HeadPart.TypeEnum.Face; head.Flags = HeadPart.Flag.Female;
        head.Model = new Model { File = "actors/character/head.nif" }; head.TextureSet.SetTo(texture.FormKey);
        var hair = mod.HeadParts.AddNew(Key(0x822)); hair.EditorID = "SharedHair";
        hair.Type = HeadPart.TypeEnum.Hair; hair.Flags = HeadPart.Flag.Female;
        hair.Model = new Model { File = "actors/character/task6/hair.nif" }; hair.TextureSet.SetTo(texture.FormKey);
        var armor = mod.Armors.AddNew(Key(0x830)); armor.EditorID = "SharedSkin"; armor.Race.SetTo(race.FormKey);
        string[] regions = ["body", "hands", "feet"];
        BipedObjectFlag[] slots = [BipedObjectFlag.Body, BipedObjectFlag.Hands, BipedObjectFlag.Feet];
        for (int i = 0; i < regions.Length; i++)
        {
            var addon = mod.ArmorAddons.AddNew(Key((uint)(0x831 + i))); addon.EditorID = "Shared" + regions[i];
            addon.Race.SetTo(race.FormKey); addon.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots[i] };
            addon.WorldModel = new GenderedItem<Model?>(new Model { File = "actors/character/male.nif" }, new Model { File = "actors/character/" + regions[i] + ".nif" });
            addon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(new FormLinkNullable<ITextureSetGetter>(texture.FormKey), new FormLinkNullable<ITextureSetGetter>(texture.FormKey));
            armor.Armature.Add(addon.FormKey);
        }
        race.Skin.SetTo(armor.FormKey);
        foreach (uint id in new uint[] { 0x800, 0x801 })
        {
            var npc = mod.Npcs.AddNew(Key(id)); npc.EditorID = "Task27Npc" + id; npc.Name = "Preserved actor";
            npc.Configuration.Flags = NpcConfiguration.Flag.Female; npc.Race.SetTo(race.FormKey);
            npc.WornArmor.SetTo(armor.FormKey); npc.HeadTexture.SetTo(texture.FormKey); npc.HeadParts.Add(head.FormKey);
            npc.HeadParts.Add(hair.FormKey); npc.HeadParts.Add(hair.FormKey);
            npc.Height = 1.07F; npc.Weight = 63;
        }
        mod.Colors.AddNew(Key(0xFA0)).EditorID = "OccupiedHighLocalId";
        mod.WriteToBinary(source.Value);
        InsertTask6UnknownNpcSubrecord(source.Value, 0x800);
        byte[] original = File.ReadAllBytes(source.Value);
        var request = new JsonObject { ["schemaVersion"] = 1, ["dataRoot"] = data.Value,
            ["pluginAuthorities"] = new JsonArray(new JsonObject { ["plugin"] = "SharedSkin.esp", ["path"] = source.Value, ["sha256"] = HashFile(source) }) };
        foreach (string region in new[] { "head", "body", "hands" })
        {
            var set = new JsonObject();
            foreach (string slot in new[] { "diffuse", "normalOrGloss", "glowOrDetailMap", "backlightMaskOrSpecular", "height" })
            {
                if (region != "head" && slot == "height") continue;
                string relative = "Textures/Actors/Character/Task27/" + region + "-" + slot + ".dds";
                var file = Child(data, relative); Directory.CreateDirectory(Path.GetDirectoryName(file.Value)!);
                byte[] dds = new byte[132]; "DDS "u8.CopyTo(dds);
                foreach ((int offset, uint value) in new (int, uint)[] { (4,124),(8,0x100F),(12,1),(16,1),(20,4),(76,32),(80,0x41),(88,32),(92,0xFF),(96,0xFF00),(100,0xFF0000),(104,0xFF000000),(108,0x1000),(128,0xFF806040) })
                    BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
                File.WriteAllBytes(file.Value, dds);
                set[slot] = new JsonObject { ["path"] = relative, ["sha256"] = HashFile(file) };
            }
            request[region] = set;
        }
        var document = Child(root, "whole-skin.json"); File.WriteAllBytes(document.Value, JsonSerializer.SerializeToUtf8Bytes(request));
        JsonNode preserveRequest = request.DeepClone();
        preserveRequest["headPolicy"] = "preserve";
        preserveRequest.AsObject().Remove("head");
        var preservedAssets = new JsonArray();
        var preservedAssetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string relative, byte[] bytes) in new[]
        {
            ("Meshes/Actors/Character/FaceGenData/FaceGeom/SharedSkin.esp/00000800.nif", "task6-facegeom"u8.ToArray()),
            ("Textures/Actors/Character/FaceGenData/FaceTint/SharedSkin.esp/00000800.dds", "DDS task6-facetint"u8.ToArray()),
            ("Meshes/Actors/Character/Task6/hair.nif", "task6-hair"u8.ToArray()),
            ("Meshes/Actors/Character/Task6/hair.tri", "task6-tri"u8.ToArray())
        })
        {
            var asset = Child(data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(asset.Value)!);
            File.WriteAllBytes(asset.Value, bytes);
            string hash = HashFile(asset);
            preservedAssetHashes.Add(relative, hash);
            preservedAssets.Add(new JsonObject { ["path"] = relative, ["sha256"] = hash });
        }
        preserveRequest["preservedAssets"] = preservedAssets;
        var preserveDocument = Child(root, "whole-skin-preserve.json");
        File.WriteAllBytes(preserveDocument.Value, JsonSerializer.SerializeToUtf8Bytes(preserveRequest));
        var headAgnosticSource = Child(data, "head-agnostic", "SharedSkin.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(headAgnosticSource.Value)!);
        var headAgnosticMod = (SkyrimMod)mod.DeepCopy();
        Npc headAgnosticNpc = headAgnosticMod.Npcs[Key(0x800)];
        headAgnosticNpc.HeadParts.Clear();
        headAgnosticNpc.HeadParts.Add(hair.FormKey);
        headAgnosticNpc.HeadParts.Add(hair.FormKey);
        headAgnosticMod.WriteToBinary(headAgnosticSource.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        byte[] headAgnosticOriginal = File.ReadAllBytes(headAgnosticSource.Value);
        JsonNode headAgnosticRequest = preserveRequest.DeepClone();
        headAgnosticRequest["pluginAuthorities"]![0] = new JsonObject
        {
            ["plugin"] = "SharedSkin.esp", ["path"] = headAgnosticSource.Value, ["sha256"] = HashFile(headAgnosticSource)
        };
        var headAgnosticDocument = Child(root, "head-agnostic-preserve.json");
        File.WriteAllBytes(headAgnosticDocument.Value, JsonSerializer.SerializeToUtf8Bytes(headAgnosticRequest));
        var headAgnosticOutputRoot = Child(root, "head-agnostic-output");
        Directory.CreateDirectory(headAgnosticOutputRoot.Value);
        var headAgnosticOutput = Child(headAgnosticOutputRoot, "SharedSkin.esp");
        var preserveOutputRoot = Child(root, "preserved-output");
        Directory.CreateDirectory(preserveOutputRoot.Value);
        var preserveOutput = Child(preserveOutputRoot, "SharedSkin.esp");
        var preserveProposal = Child(root, "preserve-proposal.json");
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Task27 exact binary discovery failed.");
        foreach (string command in new[] { "npc patch", "plugin verify" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0, "Task27 schema discovery failed.");
        var headAgnosticApplied = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", headAgnosticSource.Value,
            "--output", headAgnosticOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(headAgnosticSource),
            "--whole-skin", "@" + headAgnosticDocument.Value, "--whole-skin-sha256", HashFile(headAgnosticDocument), "--apply", "--json");
        Require(headAgnosticApplied.ExitCode == 0,
            "Preserve mode required legacy face/model state: " + headAgnosticApplied.Root + headAgnosticApplied.StdErr);
        using (var headAgnosticBefore = SkyrimMod.CreateFromBinaryOverlay(headAgnosticSource.Value, SkyrimRelease.SkyrimSE))
        using (var headAgnosticAfter = SkyrimMod.CreateFromBinaryOverlay(headAgnosticOutput.Value, SkyrimRelease.SkyrimSE))
        {
            INpcGetter before = headAgnosticBefore.Npcs.Single(row => row.FormKey.ID == 0x800);
            INpcGetter after = headAgnosticAfter.Npcs.Single(row => row.FormKey.ID == 0x800);
            Require(before.HeadParts.Select(row => row.FormKey.ID).SequenceEqual([0x822U, 0x822U]) &&
                    after.HeadParts.Select(row => row.FormKey.ID).SequenceEqual(before.HeadParts.Select(row => row.FormKey.ID)),
                "Head-agnostic preserve mode changed exact duplicate PNAM identity/order.");
        }
        byte[][] headAgnosticPnamBefore = ReadTask6NpcSubrecords(headAgnosticSource.Value, 0x800, "PNAM");
        byte[][] headAgnosticPnamAfter = ReadTask6NpcSubrecords(headAgnosticOutput.Value, 0x800, "PNAM");
        Require(headAgnosticPnamBefore.Length == 2 && headAgnosticPnamBefore.Length == headAgnosticPnamAfter.Length &&
                headAgnosticPnamBefore.Zip(headAgnosticPnamAfter).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
            "Head-agnostic preserve mode changed raw PNAM bytes or duplicates.");
        Require(headAgnosticOriginal.AsSpan().SequenceEqual(File.ReadAllBytes(headAgnosticSource.Value)),
            "Head-agnostic preserve mode changed source bytes.");
        var inheritedSource = Child(data, "inherited-skin", "SharedSkin.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(inheritedSource.Value)!);
        var inheritedMod = (SkyrimMod)mod.DeepCopy();
        inheritedMod.Npcs[Key(0x800)].WornArmor.SetTo(FormKey.Null);
        inheritedMod.WriteToBinary(inheritedSource.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        RemoveTask6NpcSubrecord(inheritedSource.Value, 0x800, "WNAM");
        InsertTask6UnknownNpcSubrecord(inheritedSource.Value, 0x800);
        using (var inheritedBefore = SkyrimMod.CreateFromBinaryOverlay(inheritedSource.Value, SkyrimRelease.SkyrimSE))
            Require(inheritedBefore.Npcs[Key(0x800)].WornArmor.IsNull,
                "Inherited-skin fixture unexpectedly contains a local WNAM.");
        JsonNode inheritedRequest = preserveRequest.DeepClone();
        inheritedRequest["pluginAuthorities"]![0] = new JsonObject
        {
            ["plugin"] = "SharedSkin.esp", ["path"] = inheritedSource.Value,
            ["sha256"] = HashFile(inheritedSource)
        };
        var inheritedDocument = Child(root, "inherited-skin-preserve.json");
        File.WriteAllBytes(inheritedDocument.Value, JsonSerializer.SerializeToUtf8Bytes(inheritedRequest));
        var inheritedOutput = Child(Child(root, "inherited-skin-output"), "SharedSkin.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(inheritedOutput.Value)!);
        var inheritedApplied = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse",
            "--input-plugin", inheritedSource.Value, "--output", inheritedOutput.Value, "--form-id", "0x800",
            "--expected-sha256", HashFile(inheritedSource), "--whole-skin", "@" + inheritedDocument.Value,
            "--whole-skin-sha256", HashFile(inheritedDocument), "--apply", "--json");
        Require(inheritedApplied.ExitCode == 0,
            "Preserve mode could not materialize WNAM for an NPC inheriting race skin: " +
            inheritedApplied.Root + inheritedApplied.StdErr);
        using (var inheritedAfter = SkyrimMod.CreateFromBinaryOverlay(inheritedOutput.Value, SkyrimRelease.SkyrimSE))
            Require(!inheritedAfter.Npcs[Key(0x800)].WornArmor.IsNull &&
                    inheritedAfter.Npcs[Key(0x800)].WornArmor.FormKey.ModKey == inheritedAfter.ModKey,
                "Inherited race skin did not become one typed output-owned NPC WNAM binding.");
        string[] inheritedSignatures = ReadTask6NpcSubrecordSignatures(inheritedOutput.Value, 0x800);
        Require(Array.IndexOf(inheritedSignatures, "WNAM") == Array.IndexOf(inheritedSignatures, "RNAM") + 1,
            "Inherited WNAM was not inserted immediately after RNAM.");
        CompressTask6NpcRecord(inheritedSource.Value, 0x800);
        JsonNode compressedRequest = inheritedRequest.DeepClone();
        compressedRequest["pluginAuthorities"]![0]!["sha256"] = HashFile(inheritedSource);
        var compressedDocument = Child(root, "compressed-source.json");
        File.WriteAllBytes(compressedDocument.Value, JsonSerializer.SerializeToUtf8Bytes(compressedRequest));
        var compressedOutput = Child(Child(root, "compressed-output"), "SharedSkin.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(compressedOutput.Value)!);
        var compressedResult = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse",
            "--input-plugin", inheritedSource.Value, "--output", compressedOutput.Value, "--form-id", "0x800",
            "--expected-sha256", HashFile(inheritedSource), "--whole-skin", "@" + compressedDocument.Value,
            "--whole-skin-sha256", HashFile(compressedDocument), "--apply", "--json");
        Require(compressedResult.ExitCode != 0 && !File.Exists(compressedOutput.Value) &&
                compressedResult.Root.ToString().Contains("compressed NPC_", StringComparison.Ordinal),
            "Whole-skin preserve did not explicitly refuse a compressed source NPC_: " +
            compressedResult.Root + compressedResult.StdErr);
        var preserveApplied = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", preserveOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
            "--whole-skin", "@" + preserveDocument.Value, "--whole-skin-sha256", HashFile(preserveDocument),
            "--proposal", preserveProposal.Value, "--apply", "--json");
        Require(preserveApplied.ExitCode == 0,
            "Head-preserving whole-skin patch was refused: " + preserveApplied.Root + preserveApplied.StdErr);
        using (var persistedPreserve = JsonDocument.Parse(File.ReadAllBytes(preserveProposal.Value)))
        {
            JsonElement wholeSkin = persistedPreserve.RootElement.GetProperty("wholeSkin");
            Require(wholeSkin.GetProperty("allocatedRecordCount").GetInt32() == 6 &&
                    wholeSkin.GetProperty("changedNpcSubrecords").EnumerateArray().Select(row => row.GetString()).SequenceEqual(["WNAM"]),
                "Preserve proposal does not publish its exact six-record/WNAM-only plan.");
        }
        using (var preserved = SkyrimMod.CreateFromBinaryOverlay(preserveOutput.Value, SkyrimRelease.SkyrimSE))
        {
            INpcGetter target = preserved.Npcs.Single(row => row.FormKey.ID == 0x800);
            INpcGetter sourceTarget = mod.Npcs[Key(0x800)];
            Require(target.HeadTexture.FormKey.ID == sourceTarget.HeadTexture.FormKey.ID &&
                    target.HeadParts.Select(row => row.FormKey.ID).SequenceEqual(sourceTarget.HeadParts.Select(row => row.FormKey.ID)),
                "Preserve mode changed exact FTST or full PNAM order.");
            Require(preserved.HeadParts.Count == mod.HeadParts.Count &&
                    !preserved.HeadParts.Any(row => row.FormKey.ID > 0xFA0),
                "Preserve mode allocated a private HDPT.");
            IArmorGetter privateSkin = preserved.Armors.Single(row => row.FormKey == target.WornArmor.FormKey);
            Require(privateSkin.FormKey.ID == 0xFA6 &&
                    privateSkin.Armature.Select(row => row.FormKey.ID).SequenceEqual([0xFA3U, 0xFA4U, 0xFA5U]) &&
                    preserved.TextureSets.Count(row => row.FormKey.ID > 0xFA0) == 2 &&
                    preserved.ArmorAddons.Count(row => row.FormKey.ID > 0xFA0) == 3 &&
                    preserved.ModHeader.Stats.NextFormID == 0xFA7,
                "Preserve mode did not emit its exact two TXST/three ARMA/one ARMO closure.");
        }
        var preserveVerified = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
            "--output-plugin", preserveOutput.Value, "--form-id", "0x800", "--whole-skin", "@" + preserveDocument.Value,
            "--whole-skin-sha256", HashFile(preserveDocument), "--json");
        Require(preserveVerified.ExitCode == 0,
            "Independent preserve-mode verification failed: " + preserveVerified.Root + preserveVerified.StdErr);
        var trailingWnamRoot = Child(root, "trailing-wnam"); Directory.CreateDirectory(trailingWnamRoot.Value);
        var trailingWnam = Child(trailingWnamRoot, "SharedSkin.esp");
        File.Copy(preserveOutput.Value, trailingWnam.Value);
        MoveTask6NpcSubrecordToEnd(trailingWnam.Value, 0x800, "WNAM");
        var trailingWnamResult = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse",
            "--source-plugin", source.Value, "--output-plugin", trailingWnam.Value, "--form-id", "0x800",
            "--whole-skin", "@" + preserveDocument.Value, "--whole-skin-sha256", HashFile(preserveDocument), "--json");
        Require(trailingWnamResult.ExitCode != 0 &&
                trailingWnamResult.Root.ToString().Contains("WNAM position", StringComparison.Ordinal),
            "Independent preserve verifier admitted a trailing WNAM: " + trailingWnamResult.Root + trailingWnamResult.StdErr);
        foreach ((string name, int offset) in new[] { ("hedr-count", 34), ("hedr-next-form-id", 38) })
        {
            var tamperRoot = Child(root, name); Directory.CreateDirectory(tamperRoot.Value);
            var tamperPath = Child(tamperRoot, "SharedSkin.esp");
            byte[] tamperedHedr = File.ReadAllBytes(preserveOutput.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(tamperedHedr.AsSpan(offset),
                checked(BinaryPrimitives.ReadUInt32LittleEndian(tamperedHedr.AsSpan(offset)) + 1));
            File.WriteAllBytes(tamperPath.Value, tamperedHedr);
            var result = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
                "--output-plugin", tamperPath.Value, "--form-id", "0x800", "--whole-skin", "@" + preserveDocument.Value,
                "--whole-skin-sha256", HashFile(preserveDocument), "--json");
            Require(result.ExitCode != 0 && result.Root.ToString().Contains("HEDR", StringComparison.Ordinal),
                $"Independent preserve verifier admitted {name} tampering: {result.Root}{result.StdErr}");
        }
        foreach (uint headPartId in new[] { 0x821U, 0x822U })
            Require(ReadTask6Record(source.Value, "HDPT", headPartId).AsSpan().SequenceEqual(
                    ReadTask6Record(preserveOutput.Value, "HDPT", headPartId)),
                $"Preserve mode changed linked HDPT 0x{headPartId:X8} raw bytes.");
        foreach ((string relative, string hash) in preservedAssetHashes)
            Require(HashFile(Child(data, relative)) == hash, "Preserve mode changed declared accepted asset: " + relative);

        async Task RefusePreserveDocumentAsync(string name, JsonNode candidate, string expected)
        {
            var candidateDocument = Child(root, name + ".json");
            File.WriteAllBytes(candidateDocument.Value, JsonSerializer.SerializeToUtf8Bytes(candidate));
            var candidateRoot = Child(root, name + "-output");
            Directory.CreateDirectory(candidateRoot.Value);
            var candidateOutput = Child(candidateRoot, "SharedSkin.esp");
            var result = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
                "--output", candidateOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
                "--whole-skin", "@" + candidateDocument.Value, "--whole-skin-sha256", HashFile(candidateDocument), "--apply", "--json");
            Require(result.ExitCode != 0 && result.Root.ToString().Contains(expected, StringComparison.Ordinal) && !File.Exists(candidateOutput.Value),
                $"Preserve refusal {name} was not fail-closed: {result.Root}{result.StdErr}");
        }

        foreach ((string name, JsonNode? headValue) in new[]
        {
            ("preserve-head-null", (JsonNode?)null),
            ("preserve-head-empty", JsonValue.Create(string.Empty)),
            ("preserve-head-object", (JsonNode)new JsonObject())
        })
        {
            JsonNode conflict = preserveRequest.DeepClone();
            conflict["head"] = headValue;
            await RefusePreserveDocumentAsync(name, conflict, "head");
        }
        JsonNode unknownPolicy = preserveRequest.DeepClone(); unknownPolicy["headPolicy"] = "future";
        await RefusePreserveDocumentAsync("preserve-unknown-policy", unknownPolicy, "admitted value is preserve");
        JsonNode staleAsset = preserveRequest.DeepClone(); staleAsset["preservedAssets"]![0]!["sha256"] = new string('0', 64);
        await RefusePreserveDocumentAsync("preserve-stale-asset", staleAsset, "preserved asset SHA-256");
        JsonNode duplicateAsset = preserveRequest.DeepClone(); duplicateAsset["preservedAssets"]!.AsArray().Add(duplicateAsset["preservedAssets"]![0]!.DeepClone());
        await RefusePreserveDocumentAsync("preserve-duplicate-asset", duplicateAsset, "unique canonical paths");
        JsonNode wrongNpcAssets = preserveRequest.DeepClone();
        foreach (JsonNode? item in wrongNpcAssets["preservedAssets"]!.AsArray())
        {
            string relative = item!["path"]!.GetValue<string>();
            if (!relative.Contains("/FaceGeom/", StringComparison.OrdinalIgnoreCase) &&
                !relative.Contains("/FaceTint/", StringComparison.OrdinalIgnoreCase)) continue;
            string wrongRelative = relative.Replace("00000800", "00000801", StringComparison.Ordinal);
            WorkspacePath wrongPath = Child(data, wrongRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(wrongPath.Value)!);
            File.Copy(Child(data, relative).Value, wrongPath.Value);
            item["path"] = wrongRelative;
            item["sha256"] = HashFile(wrongPath);
        }
        await RefusePreserveDocumentAsync("preserve-wrong-npc-assets", wrongNpcAssets,
            "target NPC FaceGen identity");

        var renamed = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", Child(root, "RenamedSkin.esp").Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
            "--whole-skin", "@" + preserveDocument.Value, "--whole-skin-sha256", HashFile(preserveDocument), "--apply", "--json");
        Require(renamed.ExitCode != 0 && renamed.Root.ToString().Contains("same logical plugin filename", StringComparison.Ordinal) &&
                !File.Exists(Child(root, "RenamedSkin.esp").Value),
            "Preserve mode admitted a renamed logical plugin output.");
        var scalarConflictRoot = Child(root, "preserve-scalar-output"); Directory.CreateDirectory(scalarConflictRoot.Value);
        var scalarConflictOutput = Child(scalarConflictRoot, "SharedSkin.esp");
        var scalarConflict = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", scalarConflictOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(source), "--name", "conflict",
            "--whole-skin", "@" + preserveDocument.Value, "--whole-skin-sha256", HashFile(preserveDocument), "--apply", "--json");
        Require(scalarConflict.ExitCode != 0 && scalarConflict.Root.ToString().Contains("WNAM-only", StringComparison.Ordinal) && !File.Exists(scalarConflictOutput.Value),
            "Preserve mode admitted a simultaneous scalar edit.");
        var appearanceConflictRoot = Child(root, "preserve-appearance-output"); Directory.CreateDirectory(appearanceConflictRoot.Value);
        var appearanceConflictOutput = Child(appearanceConflictRoot, "SharedSkin.esp");
        var appearanceConflict = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", appearanceConflictOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
            "--race", "SharedSkin.esp|0x00000810", "--whole-skin", "@" + preserveDocument.Value,
            "--whole-skin-sha256", HashFile(preserveDocument), "--apply", "--json");
        Require(appearanceConflict.ExitCode != 0 && appearanceConflict.Root.ToString().Contains("WNAM-only", StringComparison.Ordinal) && !File.Exists(appearanceConflictOutput.Value),
            "Preserve mode admitted a simultaneous appearance edit.");

        ImmutableDictionary<string, NpcWholeSkinTexture> TypedTextures(JsonNode node, string property) =>
            node[property]!.AsObject().ToImmutableDictionary(row => row.Key, row =>
                new NpcWholeSkinTexture(new AssetPath(row.Value!["path"]!.GetValue<string>()), new Sha256Hash(row.Value!["sha256"]!.GetValue<string>())), StringComparer.Ordinal);
        var typedPatch = new NpcWholeSkinPatch(data,
            [new NpcCreationPluginAuthority(new PluginName("SharedSkin.esp"), source, new Sha256Hash(HashFile(source)))],
            ImmutableDictionary<string, NpcWholeSkinTexture>.Empty, TypedTextures(preserveRequest, "body"), TypedTextures(preserveRequest, "hands"),
            File.ReadAllText(preserveDocument.Value), new Sha256Hash(HashFile(preserveDocument)))
        {
            HeadPolicy = NpcWholeSkinHeadPolicy.Preserve,
            PreservedAssets = preserveRequest["preservedAssets"]!.AsArray().Select(row => new NpcWholeSkinTexture(
                new AssetPath(row!["path"]!.GetValue<string>()), new Sha256Hash(row["sha256"]!.GetValue<string>()))).ToImmutableArray()
        };
        var directRoot = Child(root, "direct-rollback"); Directory.CreateDirectory(directRoot.Value);
        var directOutput = Child(directRoot, "SharedSkin.esp");
        var directRequest = new NpcMutationRequest(GameEdition.SkyrimSpecialEdition, source, directOutput, new FormId(0x800),
            null, null, null, new Sha256Hash(HashFile(source)), false, null) { WholeSkin = typedPatch };
        var policy = new KOnlyWorkspacePolicy(root, Child(root, "protected"));
        var rejectingService = new NpcMutationService(policy, root, stagedCandidate =>
        {
            SkyrimMod altered;
            using (var staged = SkyrimMod.CreateFromBinaryOverlay(stagedCandidate.Value, SkyrimRelease.SkyrimSE))
                altered = (SkyrimMod)staged.DeepCopy();
            altered.Npcs.Single(row => row.FormKey.ID == 0x800).Weight = 64;
            altered.WriteToBinary(stagedCandidate.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        });
        NpcMutationProposal directProposal = await rejectingService.AnalyzeAsync(directRequest, CancellationToken.None);
        NpcMutationResult rolledBack = await rejectingService.ApplyAsync(directRequest, directProposal, CancellationToken.None);
        Require(!rolledBack.Applied && rolledBack.Diagnostics.Any(row => row.Code == "npc-whole-skin-readback" &&
                    row.Message.Contains("outside WNAM", StringComparison.Ordinal)) &&
                !File.Exists(directOutput.Value) && !Directory.EnumerateDirectories(directRoot.Value, ".actorwright-npc-patch-*").Any(),
            "Actual verification of a corrupted staged candidate left a published output or staging residue.");
        var undefinedPolicyRoot = Child(root, "direct-undefined-policy"); Directory.CreateDirectory(undefinedPolicyRoot.Value);
        var undefinedPolicyRequest = directRequest with
        {
            OutputPlugin = Child(undefinedPolicyRoot, "SharedSkin.esp"),
            WholeSkin = typedPatch with { HeadPolicy = (NpcWholeSkinHeadPolicy)42 }
        };
        NpcMutationProposal undefinedPolicyProposal = await new NpcMutationService(policy, root).AnalyzeAsync(undefinedPolicyRequest, CancellationToken.None);
        Require(undefinedPolicyProposal.Diagnostics.Any(row => row.Code == "npc-whole-skin-authority" &&
                row.Message.Contains("undefined typed head policy", StringComparison.Ordinal)),
            "Direct service admission derived document semantics from an undefined typed head policy.");
        var directPublishRoot = Child(root, "direct-publish"); Directory.CreateDirectory(directPublishRoot.Value);
        var directPublishRequest = directRequest with { OutputPlugin = Child(directPublishRoot, "SharedSkin.esp") };
        var directPublishService = new NpcMutationService(policy, root);
        NpcMutationProposal directPublishProposal = await directPublishService.AnalyzeAsync(directPublishRequest, CancellationToken.None);
        NpcMutationResult directlyPublished = await directPublishService.ApplyAsync(directPublishRequest, directPublishProposal, CancellationToken.None);
        Require(directlyPublished.Applied && directlyPublished.OutputHash == new Sha256Hash(HashFile(directPublishRequest.OutputPlugin)) &&
                File.Exists(directPublishRequest.OutputPlugin.Value) &&
                !Directory.EnumerateDirectories(directPublishRoot.Value, ".actorwright-npc-patch-*").Any(),
            "Verified staged candidate did not publish once with its exact precomputed output hash.");
        var driftedRequest = directRequest with { OutputPlugin = Child(Child(root, "direct-drift"), "SharedSkin.esp"), WholeSkin = typedPatch with { HeadPolicy = NpcWholeSkinHeadPolicy.Replace } };
        Directory.CreateDirectory(Path.GetDirectoryName(driftedRequest.OutputPlugin.Value)!);
        NpcMutationProposal drifted = await new NpcMutationService(policy, root).AnalyzeAsync(driftedRequest, CancellationToken.None);
        Require(drifted.Diagnostics.Any(row => row.Code == "npc-whole-skin-authority" && row.Message.Contains("bound document", StringComparison.Ordinal)),
            "Direct service admission accepted typed/document head-policy drift.");

        async Task RefusePreservePluginTamperAsync(string name, Action<SkyrimMod> mutate)
        {
            var tamperRoot = Child(root, name); Directory.CreateDirectory(tamperRoot.Value);
            var tamperPath = Child(tamperRoot, "SharedSkin.esp");
            using (var opened = SkyrimMod.CreateFromBinaryOverlay(preserveOutput.Value, SkyrimRelease.SkyrimSE))
            {
                var altered = (SkyrimMod)opened.DeepCopy();
                mutate(altered);
                altered.WriteToBinary(tamperPath.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
            }
            var result = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
                "--output-plugin", tamperPath.Value, "--form-id", "0x800", "--whole-skin", "@" + preserveDocument.Value,
                "--whole-skin-sha256", HashFile(preserveDocument), "--json");
            Require(result.ExitCode != 0, "Independent preserve verifier admitted " + name + ": " + result.Root + result.StdErr);
        }
        await RefusePreservePluginTamperAsync("preserve-pnam-tamper", altered =>
        {
            Npc target = altered.Npcs.Single(row => row.FormKey.ID == 0x800);
            FormKey[] reversed = target.HeadParts.Select(row => row.FormKey).Reverse().ToArray();
            target.HeadParts.Clear();
            foreach (FormKey key in reversed) target.HeadParts.Add(key);
        });
        await RefusePreservePluginTamperAsync("preserve-ftst-tamper", altered =>
            altered.Npcs.Single(row => row.FormKey.ID == 0x800).HeadTexture.SetTo(new FormKey(altered.ModKey, 0xFA1)));
        await RefusePreservePluginTamperAsync("preserve-hair-hdpt-tamper", altered =>
            altered.HeadParts.Single(row => row.FormKey.ID == 0x822).Model!.File = "actors/character/task6/tampered-hair.nif");
        await RefusePreservePluginTamperAsync("preserve-tint-field-tamper", altered =>
            altered.Npcs.Single(row => row.FormKey.ID == 0x800).HairColor.SetTo(new FormKey(altered.ModKey, 0xFA0)));
        await RefusePreservePluginTamperAsync("preserve-unrelated-field-tamper", altered =>
            altered.Npcs.Single(row => row.FormKey.ID == 0x800).Weight = 64);
        string faceGeomRelative = preservedAssetHashes.Keys.Single(path => path.Contains("/FaceGeom/", StringComparison.OrdinalIgnoreCase));
        WorkspacePath faceGeomPath = Child(data, faceGeomRelative);
        byte[] faceGeomBytes = File.ReadAllBytes(faceGeomPath.Value);
        File.WriteAllBytes(faceGeomPath.Value, faceGeomBytes.Concat(new byte[] { 0xFF }).ToArray());
        var assetTamper = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
            "--output-plugin", preserveOutput.Value, "--form-id", "0x800", "--whole-skin", "@" + preserveDocument.Value,
            "--whole-skin-sha256", HashFile(preserveDocument), "--json");
        File.WriteAllBytes(faceGeomPath.Value, faceGeomBytes);
        Require(assetTamper.ExitCode != 0 && assetTamper.Root.ToString().Contains("preserved asset SHA-256", StringComparison.Ordinal),
            "Post-transaction verification admitted changed declared FaceGeom bytes.");
        var applied = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", output.Value, "--form-id", "0x800", "--expected-sha256", HashFile(source), "--whole-skin", "@" + document.Value,
            "--whole-skin-sha256", HashFile(document), "--proposal", Child(root, "proposal.json").Value, "--apply", "--json");
        Require(applied.ExitCode == 0, "Whole-skin patch did not materialize the requested private graph: " + applied.Root + applied.StdErr);
        using var persisted = JsonDocument.Parse(File.ReadAllBytes(Child(root, "proposal.json").Value));
        Require(persisted.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
                new Sha256Hash(persisted.RootElement.GetProperty("wholeSkin").GetProperty("requestSha256").GetString()!) == new Sha256Hash(HashFile(document)) &&
                !persisted.RootElement.GetProperty("wholeSkin").GetProperty("faceGenRebuilt").GetBoolean(),
            "Whole-skin proposal lost its v2 exact intent binding or misclaimed a FaceGen rebuild.");
        using (var actual = SkyrimMod.CreateFromBinaryOverlay(output.Value, SkyrimRelease.SkyrimSE))
        {
            INpcGetter target = actual.Npcs.Single(n => n.FormKey.ID == 0x800);
            INpcGetter untouched = actual.Npcs.Single(n => n.FormKey.ID == 0x801);
            IArmorGetter privateSkin = actual.Armors.Single(a => a.FormKey == target.WornArmor.FormKey);
            Require(privateSkin.FormKey.ModKey == actual.ModKey && privateSkin.FormKey.ID > 0xFA0 && privateSkin.Armature.Count == 3,
                "Private WNAM ownership, allocation or exact armature closure is wrong.");
            Require(untouched.WornArmor.FormKey.ID == 0x830 && untouched.HeadTexture.FormKey.ID == 0x820 &&
                    untouched.HeadParts.Select(row => row.FormKey.ID).SequenceEqual([0x821U, 0x822U, 0x822U]),
                "The second shared-skin NPC changed.");
            FormKey privateFace = target.HeadParts.Single(row => row.FormKey.ID > 0xFA0).FormKey;
            Require(target.HeadTexture.FormKey.ID > 0xFA0 && actual.HeadParts.Single(p => p.FormKey == privateFace).TextureSet.FormKey == target.HeadTexture.FormKey &&
                    target.HeadParts.Count(row => row.FormKey.ID == 0x822) == 2,
                "Private head TXST is not bound to target FTST and its private face HDPT.");
            foreach (var link in privateSkin.Armature)
            {
                var addon = actual.ArmorAddons.Single(a => a.FormKey == link.FormKey);
                Require(addon.FormKey.ModKey == actual.ModKey && addon.FormKey.ID > 0xFA0 && addon.SkinTexture!.Female.FormKey.ID > 0xFA0,
                    "A private armature still reaches a shared ARMA/TXST.");
            }
            Require(target.Height == 1.07F && target.Weight == 63 && target.Name!.String == "Preserved actor", "Protected target fields changed.");
        }
        var verified = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
            "--output-plugin", output.Value, "--form-id", "0x800", "--whole-skin", "@" + document.Value, "--whole-skin-sha256", HashFile(document), "--json");
        Require(verified.ExitCode == 0, "Independent whole-skin CLI verification failed: " + verified.Root + verified.StdErr);
        JsonNode stale = request.DeepClone(); stale["body"]!["diffuse"]!["sha256"] = new string('0', 64);
        var stalePath = Child(root, "stale.json"); File.WriteAllBytes(stalePath.Value, JsonSerializer.SerializeToUtf8Bytes(stale));
        var refused = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", Child(root, "stale.esp").Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
            "--whole-skin", "@" + stalePath.Value, "--whole-skin-sha256", HashFile(stalePath), "--apply", "--json");
        Require(refused.ExitCode != 0 && refused.Root.ToString().Contains("npc-whole-skin-authority", StringComparison.Ordinal) && !File.Exists(Child(root, "stale.esp").Value), "Stale DDS authority did not refuse before output.");
        var malePath = Child(data, "MaleSkin.esp");
        var male = (SkyrimMod)mod.DeepCopy(); male.Npcs[Key(0x800)].Configuration.Flags &= ~NpcConfiguration.Flag.Female;
        male.WriteToBinary(malePath.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        JsonNode maleDocument = request.DeepClone();
        maleDocument["pluginAuthorities"]![0] = new JsonObject { ["plugin"] = "MaleSkin.esp", ["path"] = malePath.Value, ["sha256"] = HashFile(malePath) };
        var maleAuthority = Child(root, "male.json"); File.WriteAllBytes(maleAuthority.Value, JsonSerializer.SerializeToUtf8Bytes(maleDocument));
        var refusedMale = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", malePath.Value,
            "--output", Child(root, "male-output.esp").Value, "--form-id", "0x800", "--expected-sha256", HashFile(malePath),
            "--whole-skin", "@" + maleAuthority.Value, "--whole-skin-sha256", HashFile(maleAuthority), "--apply", "--json");
        Require(refusedMale.ExitCode != 0 && refusedMale.Root.ToString().Contains("female Skyrim SE", StringComparison.Ordinal) && !File.Exists(Child(root, "male-output.esp").Value), "Male whole-skin request lacks the explicit bounded refusal.");
        var changedSex = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", malePath.Value,
            "--output", Child(root, "sex-output.esp").Value, "--form-id", "0x800", "--expected-sha256", HashFile(malePath),
            "--sex", "female", "--whole-skin", "@" + maleAuthority.Value, "--whole-skin-sha256", HashFile(maleAuthority), "--apply", "--json");
        Require(changedSex.ExitCode != 0 && changedSex.Root.ToString().Contains("female Skyrim SE", StringComparison.Ordinal) && !File.Exists(Child(root, "sex-output.esp").Value), "Combined sex edit bypassed whole-skin source admission.");
        var changedRace = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", Child(root, "race-output.esp").Value, "--form-id", "0x800", "--expected-sha256", HashFile(source),
            "--race", "SharedSkin.esp|0x00000811", "--whole-skin", "@" + document.Value, "--whole-skin-sha256", HashFile(document), "--apply", "--json");
        Require(changedRace.ExitCode != 0 && changedRace.Root.ToString().Contains("preserves the source race", StringComparison.Ordinal) && !File.Exists(Child(root, "race-output.esp").Value), "Combined race edit bypassed whole-skin source admission.");
        var masterSource = Child(data, "SharedSkin.esm");
        mod.WriteToBinary(masterSource.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        JsonNode masterDocument = request.DeepClone();
        masterDocument["pluginAuthorities"]![0] = new JsonObject { ["plugin"] = "SharedSkin.esm", ["path"] = masterSource.Value, ["sha256"] = HashFile(masterSource) };
        var masterAuthority = Child(root, "master.json"); File.WriteAllBytes(masterAuthority.Value, JsonSerializer.SerializeToUtf8Bytes(masterDocument));
        var masterPatched = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", masterSource.Value,
            "--output", Child(root, "master-output.esm").Value, "--form-id", "0x800", "--expected-sha256", HashFile(masterSource),
            "--whole-skin", "@" + masterAuthority.Value, "--whole-skin-sha256", HashFile(masterAuthority), "--apply", "--json");
        Require(masterPatched.ExitCode == 0, "Whole-skin .esm source owner did not remain coherent: " + masterPatched.Root + masterPatched.StdErr);
        var tampered = Child(root, "Tampered.esp");
        using (var opened = SkyrimMod.CreateFromBinaryOverlay(output.Value, SkyrimRelease.SkyrimSE))
        {
            var altered = (SkyrimMod)opened.DeepCopy();
            altered.Armors.Single(row => row.FormKey == altered.Npcs.Single(n => n.FormKey.ID == 0x800).WornArmor.FormKey).Armature.Add(new FormKey(altered.ModKey, 0x831));
            altered.WriteToBinary(tampered.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        }
        var refusedTamper = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
            "--output-plugin", tampered.Value, "--form-id", "0x800", "--whole-skin", "@" + document.Value, "--whole-skin-sha256", HashFile(document), "--json");
        Require(refusedTamper.ExitCode != 0 && refusedTamper.Root.ToString().Contains("exclusive allocated body/hands/feet", StringComparison.Ordinal), "Independent verifier accepted an extraneous ARMA in private skin.");
        var reviewFailures = new List<string>();
        foreach (string caseName in new[] { "NullRace", "LightBoundary", "LightValid" })
        {
            var reviewedSource = Child(data, caseName + ".esp");
            var reviewed = (SkyrimMod)mod.DeepCopy();
            if (caseName == "NullRace") reviewed.ArmorAddons[Key(0x831)].Race.SetTo(FormKey.Null);
            reviewed.WriteToBinary(reviewedSource.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
            if (caseName.StartsWith("Light", StringComparison.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(reviewedSource.Value);
                Require(System.Text.Encoding.ASCII.GetString(bytes, 24, 4) == "HEDR", "Synthetic light fixture HEDR moved.");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) | 0x200);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(38), caseName == "LightValid" ? 0xFF7U : 0xFF8U);
                File.WriteAllBytes(reviewedSource.Value, bytes);
            }
            JsonNode reviewedDocument = request.DeepClone();
            reviewedDocument["pluginAuthorities"]![0] = new JsonObject { ["plugin"] = caseName + ".esp", ["path"] = reviewedSource.Value, ["sha256"] = HashFile(reviewedSource) };
            var reviewedAuthority = Child(root, caseName + ".json"); File.WriteAllBytes(reviewedAuthority.Value, JsonSerializer.SerializeToUtf8Bytes(reviewedDocument));
            var reviewedOutput = Child(root, caseName + "-output.esp");
            var result = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", reviewedSource.Value,
                "--output", reviewedOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(reviewedSource),
                "--whole-skin", "@" + reviewedAuthority.Value, "--whole-skin-sha256", HashFile(reviewedAuthority), "--apply", "--json");
            Console.WriteLine(caseName + " review control: " + result.Root);
            if (caseName is "LightValid" or "LightBoundary")
            {
                Require(result.ExitCode == 0, "A valid light whole-skin allocation was refused.");
                using var light = SkyrimMod.CreateFromBinaryOverlay(reviewedOutput.Value, SkyrimRelease.SkyrimSE);
                uint expectedNext = caseName == "LightBoundary" ? 0x1000U : 0xFFFU;
                Require(light.ModHeader.Stats.NextFormID == expectedNext,
                    "Light allocation did not retain the admitted next-ID boundary.");
                continue;
            }
            if (result.ExitCode == 0 || File.Exists(reviewedOutput.Value)) reviewFailures.Add(caseName);
            else Require(result.Root.ToString().Contains(caseName == "NullRace" ? "compatible female Body ARMA" : "allocation exceeds", StringComparison.Ordinal),
                "Whole-skin review control failed for an unrelated reason.");
        }
        Require(reviewFailures.Count == 0, "Whole-skin review controls were admitted: " + string.Join(", ", reviewFailures));
        foreach ((string name, uint nextFormId, bool accepted) in new[]
        {
            ("LightPreserveValid", 0xFF9U, true),
            ("LightPreserveBoundary", 0xFFAU, true),
            ("LightPreserveOverflow", 0xFFBU, false)
        })
        {
            var lightSource = Child(data, name + ".esp");
            mod.WriteToBinary(lightSource.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
            byte[] bytes = File.ReadAllBytes(lightSource.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) | 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(38), nextFormId);
            File.WriteAllBytes(lightSource.Value, bytes);
            JsonNode lightDocumentNode = preserveRequest.DeepClone();
            lightDocumentNode["pluginAuthorities"]![0] = new JsonObject
            {
                ["plugin"] = name + ".esp", ["path"] = lightSource.Value, ["sha256"] = HashFile(lightSource)
            };
            foreach ((string sourceRelative, string targetRelative) in new[]
            {
                ("Meshes/Actors/Character/FaceGenData/FaceGeom/SharedSkin.esp/00000800.nif",
                    $"Meshes/Actors/Character/FaceGenData/FaceGeom/{name}.esp/00000800.nif"),
                ("Textures/Actors/Character/FaceGenData/FaceTint/SharedSkin.esp/00000800.dds",
                    $"Textures/Actors/Character/FaceGenData/FaceTint/{name}.esp/00000800.dds")
            })
            {
                var targetAsset = Child(data, targetRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetAsset.Value)!);
                File.Copy(Child(data, sourceRelative).Value, targetAsset.Value, overwrite: true);
                JsonNode assetRow = lightDocumentNode["preservedAssets"]!.AsArray().Single(row =>
                    string.Equals(row!["path"]!.GetValue<string>(), sourceRelative, StringComparison.OrdinalIgnoreCase))!;
                assetRow["path"] = targetRelative;
                assetRow["sha256"] = HashFile(targetAsset);
            }
            var lightDocument = Child(root, name + "-preserve.json");
            File.WriteAllBytes(lightDocument.Value, JsonSerializer.SerializeToUtf8Bytes(lightDocumentNode));
            var lightOutputRoot = Child(root, name + "-output"); Directory.CreateDirectory(lightOutputRoot.Value);
            var lightOutput = Child(lightOutputRoot, name + ".esp");
            var result = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", lightSource.Value,
                "--output", lightOutput.Value, "--form-id", "0x800", "--expected-sha256", HashFile(lightSource),
                "--whole-skin", "@" + lightDocument.Value, "--whole-skin-sha256", HashFile(lightDocument), "--apply", "--json");
            if (accepted)
            {
                Require(result.ExitCode == 0, $"Preserve mode refused legal first=0x{nextFormId:X} light allocation: " + result.Root + result.StdErr);
                using var opened = SkyrimMod.CreateFromBinaryOverlay(lightOutput.Value, SkyrimRelease.SkyrimSE);
                uint expectedNext = nextFormId == 0xFFAU ? 0x1000U : 0xFFFU;
                Require(opened.ModHeader.Stats.NextFormID == expectedNext,
                    "Preserve light boundary did not retain the exact next FormID.");
            }
            else
                Require(result.ExitCode != 0 && result.Root.ToString().Contains("allocation exceeds", StringComparison.Ordinal) && !File.Exists(lightOutput.Value),
                    "Preserve mode admitted a six-record light allocation above first=0xFF9.");
        }
        Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(source.Value)), "Whole-skin patch changed source bytes.");
        Console.WriteLine("Whole-skin actual private closure and second-NPC preservation verified: " + root.Value);
    }

    private static byte[] ReadTask6Record(string path, string signature, uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var matches = new List<byte[]>();
        Scan(0, bytes.Length);
        Require(matches.Count == 1, $"Expected one {signature}/0x{localFormId:X8} record in {path}.");
        return matches[0];

        void Scan(int start, int end)
        {
            int position = start;
            while (position < end)
            {
                Require(position + 24 <= end, "Task6 raw record header is truncated.");
                string actualSignature = Encoding.ASCII.GetString(bytes, position, 4);
                if (actualSignature == "GRUP")
                {
                    int groupLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)));
                    Require(groupLength >= 24 && position + groupLength <= end, "Task6 raw group is malformed.");
                    Scan(position + 24, position + groupLength);
                    position += groupLength;
                    continue;
                }
                int recordLength = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)));
                Require(recordLength >= 24 && position + recordLength <= end, "Task6 raw record is malformed.");
                uint rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                if (actualSignature == signature && (rawFormId & 0x00FF_FFFFU) == localFormId)
                    matches.Add(bytes.AsSpan(position, recordLength).ToArray());
                position += recordLength;
            }
        }
    }

    private static byte[][] ReadTask6NpcSubrecords(string path, uint localFormId, string signature)
    {
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        var matches = new List<byte[]>();
        int end = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4)));
        for (int position = 24; position < end;)
        {
            Require(position + 6 <= end, "Task6 NPC subrecord header is truncated.");
            int length = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 4, 2));
            int next = checked(position + 6 + length);
            Require(next <= end, "Task6 NPC subrecord payload is truncated.");
            if (Encoding.ASCII.GetString(record, position, 4) == signature)
                matches.Add(record.AsSpan(position, 6 + length).ToArray());
            position = next;
        }
        return matches.ToArray();
    }

    private static string[] ReadTask6NpcSubrecordSignatures(string path, uint localFormId)
    {
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        var signatures = new List<string>();
        int end = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4)));
        for (int position = 24; position < end;)
        {
            signatures.Add(Encoding.ASCII.GetString(record, position, 4));
            position = checked(position + 6 + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 4, 2)));
        }
        return signatures.ToArray();
    }

    private static void MoveTask6NpcSubrecordToEnd(string path, uint localFormId, string signature)
    {
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        int end = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4)));
        int fieldOffset = -1;
        int fieldLength = 0;
        for (int position = 24; position < end;)
        {
            int length = checked(6 + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 4, 2)));
            if (Encoding.ASCII.GetString(record, position, 4) == signature)
            {
                Require(fieldOffset < 0, "Task6 NPC contains duplicate subrecords for reordering.");
                fieldOffset = position; fieldLength = length;
            }
            position += length;
        }
        Require(fieldOffset >= 0, "Task6 NPC is missing the subrecord selected for reordering.");
        byte[] reordered = [.. record.AsSpan(0, fieldOffset), .. record.AsSpan(fieldOffset + fieldLength), .. record.AsSpan(fieldOffset, fieldLength)];
        ReplaceTask6NpcRecord(path, localFormId, reordered);
    }

    private static void RemoveTask6NpcSubrecord(string path, uint localFormId, string signature)
    {
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        int fieldOffset = -1;
        int fieldLength = 0;
        for (int position = 24; position < record.Length;)
        {
            int length = checked(6 + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 4, 2)));
            if (Encoding.ASCII.GetString(record, position, 4) == signature)
            {
                Require(fieldOffset < 0, "Task6 NPC contains duplicate subrecords for removal.");
                fieldOffset = position; fieldLength = length;
            }
            position += length;
        }
        Require(fieldOffset >= 0, "Task6 NPC is missing the subrecord selected for removal.");
        byte[] replacement = [.. record.AsSpan(0, fieldOffset), .. record.AsSpan(fieldOffset + fieldLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(4, 4),
            checked((uint)(replacement.Length - 24)));
        ReplaceTask6NpcRecord(path, localFormId, replacement);
    }

    private static void CompressTask6NpcRecord(string path, uint localFormId)
    {
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        int payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4)));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(record, 24, payloadLength);
        byte[] encoded = new byte[checked(4 + (int)compressed.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, checked((uint)payloadLength));
        compressed.ToArray().CopyTo(encoded, 4);
        byte[] replacement = [.. record.AsSpan(0, 24), .. encoded];
        BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(4, 4), checked((uint)encoded.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(8, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(replacement.AsSpan(8, 4)) | 0x0004_0000U);
        ReplaceTask6NpcRecord(path, localFormId, replacement);
    }

    private static void ReplaceTask6NpcRecord(string path, uint localFormId, byte[] replacement)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] record = ReadTask6Record(path, "NPC_", localFormId);
        int recordOffset = bytes.AsSpan().IndexOf(record);
        Require(recordOffset >= 0, "Task6 NPC bytes were not found for replacement.");
        int delta = replacement.Length - record.Length;
        byte[] output = [.. bytes.AsSpan(0, recordOffset), .. replacement, .. bytes.AsSpan(recordOffset + record.Length)];
        for (int position = 0; position < recordOffset;)
        {
            string current = Encoding.ASCII.GetString(output, position, 4);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(position + 4, 4)));
            if (current == "GRUP" && position + length >= recordOffset + replacement.Length)
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(position + 4, 4), checked((uint)(length + delta)));
            position += current == "GRUP" ? 24 : checked(24 + length);
        }
        File.WriteAllBytes(path, output);
    }

    private static void InsertTask6UnknownNpcSubrecord(string path, uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var groups = new List<int>();
        int recordOffset = Find(0, bytes.Length, groups);
        Require(recordOffset >= 0, "Task6 target NPC was not found for unknown-subrecord insertion.");
        byte[] unknown = new byte[10];
        "T6UK"u8.CopyTo(unknown);
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4), 4);
        new byte[] { 1, 2, 3, 4 }.CopyTo(unknown, 6);
        int oldPayloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordOffset + 4, 4)));
        int insert = recordOffset + 24 + oldPayloadLength;
        byte[] output = new byte[bytes.Length + unknown.Length];
        bytes.AsSpan(0, insert).CopyTo(output);
        unknown.CopyTo(output, insert);
        bytes.AsSpan(insert).CopyTo(output.AsSpan(insert + unknown.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(recordOffset + 4), checked((uint)(oldPayloadLength + unknown.Length)));
        foreach (int groupOffset in groups)
        {
            uint oldLength = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(groupOffset + 4, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(groupOffset + 4), checked(oldLength + (uint)unknown.Length));
        }
        File.WriteAllBytes(path, output);

        int Find(int start, int end, List<int> ancestors)
        {
            int position = start;
            while (position < end)
            {
                string signature = Encoding.ASCII.GetString(bytes, position, 4);
                if (signature == "GRUP")
                {
                    int groupLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)));
                    ancestors.Add(position);
                    int found = Find(position + 24, position + groupLength, ancestors);
                    if (found >= 0) return found;
                    ancestors.RemoveAt(ancestors.Count - 1);
                    position += groupLength;
                    continue;
                }
                int recordLength = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)));
                uint rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                if (signature == "NPC_" && (rawFormId & 0x00FF_FFFFU) == localFormId) return position;
                position += recordLength;
            }
            return -1;
        }
    }
}
