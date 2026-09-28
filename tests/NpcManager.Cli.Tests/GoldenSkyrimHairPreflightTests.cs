using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunHairRacePreflightAsync()
    {
        string repository = Path.GetFullPath(Directory.GetCurrentDirectory());
        var run = new WorkspacePath(Path.Combine(repository, "artifacts", "test-work",
            $"hair-race-preflight-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(run.Value);
        Console.WriteLine("EVIDENCE hair-preflight=" + run.Value);
        foreach (string command in new[] { "version", "capabilities" })
        {
            var discovery = await RunCliAsync(run, command, "--protocol", "2", "--json");
            Require(discovery.ExitCode == 0, "Hair preflight CLI discovery failed.");
            File.WriteAllText(Child(run, command + ".json").Value, discovery.Root.GetRawText());
        }
        var schema = await RunCliAsync(run, "schema", "export", "--protocol", "2", "--json",
            "--command", "npc create-from-jslot");
        Require(schema.ExitCode == 0, "Hair preflight schema discovery failed.");
        File.WriteAllText(Child(run, "schema.json").Value, schema.Root.GetRawText());
        foreach (string scenario in new[] { "compatible", "excluded-root", "excluded-child" })
        {
            var root = Child(run, scenario);
            Directory.CreateDirectory(root.Value);
            MaterializeProbeFixtures(repository, Path.Combine(repository, "tools", "release",
                "protocol-v2-workflow-probes", "catalog.json"), "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var presetJson = JsonNode.Parse(File.ReadAllBytes(preset.Value))!.AsObject();
            presetJson["headParts"]!.AsArray().Add(new JsonObject
            {
                ["formIdentifier"] = "HairRaceProvider.esp|0x00014195", ["type"] = 3
            });
            File.WriteAllText(preset.Value, presetJson.ToJsonString());
            var request = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
            WriteHairRacePreflightProvider(Child(root, "Data", "HairRaceProvider.esp"), scenario);
            BindHairRacePreflightFixture(root);
            var sourceHashes = Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => HashFile(new WorkspacePath(path)), StringComparer.OrdinalIgnoreCase);
            var output = Child(root, "npc-preflight", "hair-preflight.json");
            var response = await RunCliAsync(root, "npc", "create-from-jslot", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp,HairRaceProvider.esp",
                "--companion-root", Child(root, "companion").Value,
                "--preflight-output", output.Value);
            File.WriteAllText(Child(root, "response.json").Value, response.Root.GetRawText());
            bool compatible = scenario == "compatible";
            Require((compatible ? response.ExitCode == 0 : response.ExitCode != 0) &&
                    response.Root.GetProperty("created").GetBoolean(),
                "Hair preflight did not create its actual artifact: " + response.Root);
            var reopened = await new NpcBuildPreflightDocumentCodec(root).ReadExactAsync(output,
                new Sha256Hash(HashFile(output)), CancellationToken.None);
            using var document = JsonDocument.Parse(reopened.Utf8Json.AsMemory());
            JsonElement actual = document.RootElement;
            Require(actual.GetProperty("readyForBuild").GetBoolean() == compatible &&
                    response.Root.GetProperty("readyForBuild").GetBoolean() == compatible,
                "Wrong physical hair preflight readiness for " + scenario + ": " + actual);
            JsonElement gate = actual.GetProperty("requiredGates").EnumerateArray()
                .Single(row => row.GetProperty("id").GetString() == "dependency-closure");
            Require(gate.GetProperty("passed").GetBoolean() == compatible,
                "Hair race exclusion did not control the dependency gate: " + gate);
            if (compatible)
                Require(actual.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                        row.GetProperty("path").GetString() ==
                            "meshes/actors/character/FaceGenMorphs/morphs/optional-hair.tri" &&
                        row.GetProperty("status").GetString() == "missing" &&
                        row.GetProperty("disposition").GetString() == "optionalUnavailable"),
                    "Missing optional extended TRI did not retain an explicit non-blocking disposition: " + actual);
            if (!compatible)
            {
                string offendingPart = scenario == "excluded-root" ? "0x00014195" : "0x00015C9F";
                string role = scenario == "excluded-root" ? "selected Hair root" : "HNAM member";
                Require(response.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                            row.GetProperty("code").GetString() == "external-headpart-record-drift" &&
                            row.GetProperty("severity").GetString() == "error" &&
                            row.GetProperty("message").GetString() is { } message &&
                            message.Contains(role, StringComparison.Ordinal) &&
                            message.Contains(offendingPart, StringComparison.OrdinalIgnoreCase) &&
                            message.Contains("0x00016001", StringComparison.OrdinalIgnoreCase) &&
                            message.Contains("Skyrim.esm|0x00013746", StringComparison.OrdinalIgnoreCase)),
                        "Hair response lost the physical part/list/race attribution: " + response.Root);
                string detail = gate.GetProperty("detail").GetString()!;
                Require(detail.Contains("external-headpart-record-drift", StringComparison.Ordinal) &&
                        detail.Contains(role, StringComparison.Ordinal) &&
                        detail.Contains(offendingPart, StringComparison.OrdinalIgnoreCase) &&
                        detail.Contains("0x00016001", StringComparison.OrdinalIgnoreCase) &&
                        detail.Contains("Skyrim.esm|0x00013746", StringComparison.OrdinalIgnoreCase),
                    "Persisted preflight gate lost the physical part/list/race refusal: " + detail);
            }
            foreach (var source in sourceHashes)
                Require(File.Exists(source.Key) && HashFile(new WorkspacePath(source.Key)) == source.Value,
                    "Preflight modified source evidence: " + source.Key);
            foreach (string directory in new[] { "companion", "planned-npc-output", "npc-preview" })
                Require(!Directory.Exists(Child(root, directory).Value),
                    "Read-only preflight created build output: " + directory);
            Require(!Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories)
                    .Any(path => new[] { ".esp", ".esm", ".esl", ".nif", ".dds", ".tri" }
                        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !sourceHashes.ContainsKey(path)),
                "Preflight published a game-facing file.");
            Console.WriteLine($"PASS hair preflight {scenario}: ready={compatible}, sourceFiles={sourceHashes.Count}");
        }
    }

    private static void WriteHairRacePreflightProvider(WorkspacePath path, string scenario)
    {
        var key = ModKey.FromNameAndExtension(Path.GetFileName(path.Value));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var actorRace = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x13746);
        var otherRace = new Race(new FormKey(key, 0x17000), SkyrimRelease.SkyrimSE) { EditorID = "OtherRace" };
        mod.Races.Add(otherRace);
        foreach ((uint id, FormKey race) in new[] { (0x16000U, actorRace), (0x16001U, otherRace.FormKey) })
        {
            var list = new FormList(new FormKey(key, id), SkyrimRelease.SkyrimSE) { EditorID = $"RaceList{id:X}" };
            list.Items.Add(new FormLink<IRaceGetter>(race));
            mod.FormLists.Add(list);
        }
        foreach (bool child in new[] { false, true })
        {
            bool excluded = scenario == (child ? "excluded-child" : "excluded-root");
            var part = new HeadPart(new FormKey(key, child ? 0x15C9FU : 0x14195U), SkyrimRelease.SkyrimSE)
            {
                EditorID = child ? "HairRaceChild" : "HairRaceRoot",
                Flags = HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair,
                Model = new Model { File = new AssetLink<SkyrimModelAssetType>("Actors/Character/Actorwright/Head.nif") },
                ValidRaces = new FormLinkNullable<IFormListGetter>(new FormKey(key, excluded ? 0x16001U : 0x16000U))
            };
            part.Parts.Add(new Part { PartType = Part.PartTypeEnum.Tri,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>("Actors/Character/Actorwright/Head.tri") });
            part.Parts.Add(new Part { PartType = (Part.PartTypeEnum)2,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>("Actors/Character/Actorwright/HeadCharGen.tri") });
            if (!child) part.ExtraParts.Add(new FormLink<IHeadPartGetter>(new FormKey(key, 0x15C9F)));
            mod.HeadParts.Add(part);
        }
        mod.WriteToBinary(path.Value);
    }

    private static void BindHairRacePreflightFixture(WorkspacePath root)
    {
        JsonObject Read(string name) => JsonNode.Parse(File.ReadAllBytes(Child(root, "npc-preflight", name).Value))!.AsObject();
        void Write(string name, JsonObject value) => File.WriteAllText(Child(root, "npc-preflight", name).Value, value.ToJsonString());
        const string form = "HairRaceProvider.esp|0x00014195";
        var authority = Read("record-authority.json");
        authority["formBindings"]!.AsArray().Add(new JsonObject
        {
            ["signature"] = "HDPT", ["sourceFormKey"] = form, ["providerFormKey"] = form,
            ["providerPluginName"] = "HairRaceProvider.esp", ["providerPluginPath"] = "Data/HairRaceProvider.esp",
            ["providerPluginSha256"] = HashFile(Child(root, "Data", "HairRaceProvider.esp")), ["headPartType"] = "hair"
        });
        authority["headPartDispositions"]!.AsArray().Add(new JsonObject
            { ["sourceFormKey"] = form, ["disposition"] = "mapped-record" });
        Write("record-authority.json", authority);
        var bundle = Read("preset-bundle.json");
        bundle["recordAuthority"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "record-authority.json"));
        Write("preset-bundle.json", bundle);
        var request = Read("request.json");
        request["presetBundle"]!["recordAuthoritySha256"] = bundle["recordAuthority"]!["manifestSha256"]!.DeepClone();
        request["presetBundle"]!["manifestSha256"] = HashFile(Child(root, "npc-preflight", "preset-bundle.json"));
        Write("request.json", request);
        var chargen = Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "HeadCharGen.tri");
        Directory.CreateDirectory(Path.GetDirectoryName(chargen.Value)!);
        File.Copy(Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.tri").Value,
            chargen.Value);
        var morphs = Child(root, "Data", "meshes", "actors", "character", "FaceGenMorphs",
            "HairRaceProvider.esp", "morphs.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(morphs.Value)!);
        File.WriteAllText(morphs.Value,
            "extension=Actors/Character/Actorwright/HeadCharGen.tri,optional-hair.tri\n");
        // Real ordinary DDS bytes close the selected NIF's four texture dependencies.
        foreach (string name in new[] { "FemaleHead.dds", "FemaleHead_msn.dds", "FemaleHead_sk.dds", "FemaleHead_S.dds" })
        {
            var texture = Child(root, "Data", "textures", "actors", "character", "female", name);
            Directory.CreateDirectory(Path.GetDirectoryName(texture.Value)!);
            File.Copy(Child(root, "npc-preflight", "face.dds").Value, texture.Value);
        }
    }
}
