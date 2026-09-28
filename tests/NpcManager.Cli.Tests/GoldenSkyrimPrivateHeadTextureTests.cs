using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static Task RunPrivateHeadTextureRefusalAsync() => RunPrivateHeadTexturesAsync(directFaceTextures: true);

    internal static async Task RunPrivateHeadTexturesAsync(bool directFaceTextures = false)
    {
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var root = new WorkspacePath(Path.Combine(repository, "artifacts", "task36", "private-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        MaterializeProbeFixtures(repository, Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json"),
            "npc create-from-jslot", root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        var request = Child(root, "npc-preflight", "request.json");
        PrepareGoldenPresetFixture(preset);
        if (directFaceTextures)
        {
            var selected = JsonNode.Parse(File.ReadAllBytes(preset.Value))!;
            selected["actor"]!.AsObject().Remove("headTexture");
            selected["faceTextures"] = new JsonArray(
                new JsonObject { ["index"] = 0, ["texture"] = "Actors/Character/Task36/diffuse.dds" },
                new JsonObject { ["index"] = 1, ["texture"] = "Actors/Character/Task36/normalOrGloss.dds" },
                new JsonObject { ["index"] = 2, ["texture"] = "Actors/Character/Task36/glowOrDetailMap.dds" });
            File.WriteAllBytes(preset.Value, JsonSerializer.SerializeToUtf8Bytes(selected));
        }
        CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
        var standalone = Child(root, "npc-preflight", "standalone-assets.json");
        var document = JsonNode.Parse(File.ReadAllBytes(standalone.Value))!.AsObject();
        string[] slots = ["diffuse", "normalOrGloss", "glowOrDetailMap", "height", "backlightMaskOrSpecular",
            "environmentMaskOrSubsurfaceTint", "environment", "multilayer"];
        var packageRows = new JsonArray();
        var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in slots)
        {
            string requested = $"Actors/Character/Task36/{slot}.dds";
            document["privateHeadTextures"]![slot] = requested;
            var source = Child(root, "private-source", slot + ".dds");
            Directory.CreateDirectory(Path.GetDirectoryName(source.Value)!);
            File.Copy(Child(root, "npc-preflight", "face.dds").Value, source.Value);
            sourceHashes.Add(source.Value, HashFile(source));
            if (directFaceTextures)
            {
                var data = Child(root, "Data", "Textures", requested);
                Directory.CreateDirectory(Path.GetDirectoryName(data.Value)!);
                File.Copy(source.Value, data.Value);
            }
            packageRows.Add(new JsonObject { ["sourcePath"] = "private-source/" + slot + ".dds",
                ["sha256"] = HashFile(source), ["destination"] = "Textures/" + requested });
        }
        document["packageAssets"] = packageRows;
        File.WriteAllBytes(standalone.Value, JsonSerializer.SerializeToUtf8Bytes(document));
        var requestDocument = JsonNode.Parse(File.ReadAllBytes(request.Value))!;
        requestDocument["standaloneAssets"]!["manifestSha256"] = HashFile(standalone);
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(requestDocument));
        foreach (var path in new[] { preset, request, standalone, Child(root, "Data", "ActorwrightBlankNpcProvider.esp") })
            sourceHashes.Add(path.Value, HashFile(path));

        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0,
                "Private-texture exact binary discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", "npc create-from-jslot")).ExitCode == 0,
            "Private-texture schema discovery failed.");
        ProtocolInvocation created = await RunCliAsync(root, "npc", "create-from-jslot", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request),
            "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value);
        if (directFaceTextures)
        {
            Require(created.ExitCode != 0 && created.Root.ToString().Contains("racemenu-private-head-textures-refused", StringComparison.Ordinal) &&
                    created.Root.ToString().Contains("faceTextures", StringComparison.Ordinal) && !File.Exists(Child(root, "planned-npc-output", "Data", "PackagedNpc.esp").Value),
                "Direct private faceTextures without a truthful matching TXST must receive a field-naming refusal: " + created.Root + created.StdErr);
            foreach (var pair in sourceHashes)
                Require(HashFile(new WorkspacePath(pair.Key)) == pair.Value, "Refused private texture creation changed source bytes.");
            return;
        }
        Require(created.ExitCode == 0, "Private-texture actual create failed: " + created.Root + created.StdErr);
        var output = Child(root, "planned-npc-output", "Data", "PackagedNpc.esp");
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(output.Value, SkyrimRelease.SkyrimSE);
        INpcGetter npc = plugin.Npcs.Single();
        ITextureSetGetter texture = plugin.TextureSets.Single(row => row.FormKey == npc.HeadTexture.FormKey);
        var failures = new List<string>();
        if (texture.FormKey.ModKey != ModKey.FromNameAndExtension("PackagedNpc.esp")) failures.Add("NPC TXST is not output-owned");
        if (!plugin.HeadParts.Any(part => part.FormKey.ModKey == plugin.ModKey && part.Type == HeadPart.TypeEnum.Face &&
                part.TextureSet.FormKey == texture.FormKey && npc.HeadParts.Any(link => link.FormKey == part.FormKey)))
            failures.Add("output-owned face HDPT does not bind the private TXST");
        string?[] actual = [texture.Diffuse?.ToString(), texture.NormalOrGloss?.ToString(), texture.GlowOrDetailMap?.ToString(),
            texture.Height?.ToString(), texture.BacklightMaskOrSpecular?.ToString(), texture.EnvironmentMaskOrSubsurfaceTint?.ToString(),
            texture.Environment?.ToString(), texture.Multilayer?.ToString()];
        for (int index = 0; index < slots.Length; index++)
        {
            string expected = $"Actors/Character/Task36/{slots[index]}.dds";
            string? normalized = actual[index]?.Replace('\\', '/');
            if (normalized?.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase) == true) normalized = normalized[9..];
            if (!string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"TXST {slots[index]} expected {expected}, got {actual[index]}");
            var copied = Child(root, "planned-npc-output", "Data", "Textures", expected);
            if (!File.Exists(copied.Value) || HashFile(copied) != sourceHashes[Child(root, "private-source", slots[index] + ".dds").Value])
                failures.Add($"requested package asset missing or changed: {expected}");
        }
        foreach (var pair in sourceHashes)
            Require(HashFile(new WorkspacePath(pair.Key)) == pair.Value, "Private-texture creation changed source bytes: " + pair.Key);
        Require(failures.Count == 0, "Selection discarded standaloneAssets.privateHeadTextures or its copied assets: " + string.Join("; ", failures));
        Console.WriteLine("Private texture TXST, NPC/HDPT links, all eight copied DDS files, and unchanged sources verified: " + root.Value);
    }
}
