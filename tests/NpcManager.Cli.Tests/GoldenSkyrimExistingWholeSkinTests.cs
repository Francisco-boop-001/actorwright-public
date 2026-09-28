using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly string[] Task27BodyGroups = ["Task27"];
    internal static async Task RunExistingWholeSkinAsync()
    {
        string repository = Environment.CurrentDirectory;
        var root = Child(new WorkspacePath(repository), "artifacts", "task27", "existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root.Value);
        MaterializeProbeFixtures(repository, Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json"), "npc create-from-jslot", root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        var request = Child(root, "npc-preflight", "request.json");
        PrepareGoldenPresetFixture(preset);
        CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
        var xml = Child(root, "body.xml");
        File.WriteAllText(xml.Value, "<SliderPresets><Preset name=\"Task27Body\" set=\"Task27Set\"><Group name=\"Task27\"/><SetSlider name=\"Waist\" size=\"big\" value=\"25\"/></Preset></SliderPresets>");
        var presetAuthority = Child(root, "body-preset-authority.json");
        File.WriteAllBytes(presetAuthority.Value, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, authorityId = "task27-body", edition = "skyrimse", sourceKind = "bodyslide-sliderpreset-xml",
            presetXmlPath = "body.xml", presetXmlSha256 = HashFile(xml), presetName = "Task27Body", sliderSet = "Task27Set",
            groups = Task27BodyGroups, sliderCount = 1, runtimeAuthority = false
        }));
        JsonObject Reference(WorkspacePath path) => new() { ["manifestPath"] = Path.GetRelativePath(root.Value, path.Value).Replace('\\', '/'), ["manifestSha256"] = HashFile(path) };
        var meshes = new JsonArray();
        foreach (string region in new[] { "body", "hands", "feet" })
        foreach (int weight in new[] { 0, 1 })
        {
            string fileName = region + "_" + weight + ".nif";
            var mesh = Child(root, "body-source", fileName); Directory.CreateDirectory(Path.GetDirectoryName(mesh.Value)!);
            File.WriteAllBytes(mesh.Value, WriteHdptTopologyModel(141));
            meshes.Add(new JsonObject { ["role"] = region + weight, ["sourcePath"] = "body-source/" + fileName,
                ["sha256"] = HashFile(mesh), ["destination"] = "Meshes/Actors/Character/Task27/" + fileName });
        }
        var meshAuthority = Child(root, "body-mesh-authority.json");
        File.WriteAllBytes(meshAuthority.Value, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schemaVersion"] = 1, ["authorityId"] = "task27-meshes", ["edition"] = "skyrimse",
            ["sourceKind"] = "external-bodyslide-generated-meshes", ["bodySlidePresetAuthority"] = Reference(presetAuthority),
            ["meshes"] = meshes, ["runtimeAuthority"] = false
        }));
        var standalone = Child(root, "npc-preflight", "standalone-assets.json");
        JsonObject assets = JsonNode.Parse(File.ReadAllBytes(standalone.Value))!.AsObject();
        assets["schemaVersion"] = 7; assets["bodySlidePresetAuthority"] = Reference(presetAuthority);
        assets["bodyMeshAuthority"] = Reference(meshAuthority);
        assets["faceTint"] = new JsonObject { ["width"] = 1024, ["height"] = 1024 };
        assets.Remove("externalCharGenExportAuthority");
        assets["packageAssets"] = new JsonArray(assets["privateHeadTextures"]!.AsObject()
            .Where(row => row.Value is not null).Select(row =>
            {
                string texture = "Textures/" + row.Value!.GetValue<string>();
                return (JsonNode?)new JsonObject { ["sourcePath"] = "Data/" + texture,
                    ["sha256"] = HashFile(Child(root, "Data", texture)), ["destination"] = texture };
            }).ToArray());
        File.WriteAllBytes(standalone.Value, JsonSerializer.SerializeToUtf8Bytes(assets));
        var source = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        string originalHash = HashFile(source);
        JsonObject execution = JsonNode.Parse(File.ReadAllBytes(request.Value))!.AsObject();
        execution["schemaVersion"] = 2;
        string providerRoot = SyntheticProductProviderFixture.Ensure(
            AppContext.BaseDirectory);
        foreach (string file in Directory.EnumerateFiles(providerRoot, "*", SearchOption.AllDirectories))
        {
            var copied = Child(root, "provider-fixture", Path.GetRelativePath(providerRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copied.Value)!);
            File.Copy(file, copied.Value);
        }
        const string providerPlugin = "provider-fixture/Data/ActorwrightBlankNpcProvider.esp";
        const string providerFace = "provider-fixture/Data/meshes/actors/character/FaceGenData/FaceGeom/ActorwrightBlankNpcProvider.esp/00000800.nif";
        var providerCarrier = Child(root, providerFace);
        var geometryReader = new BethesdaNifGeometryReadbackService();
        var originalGeometry = await geometryReader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, providerCarrier), CancellationToken.None);
        byte[] legacyCarrier = BuildSyntheticLegacyCarrier(File.ReadAllBytes(providerCarrier.Value));
        File.WriteAllBytes(providerCarrier.Value, legacyCarrier);
        var legacyGeometry = await geometryReader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, providerCarrier), CancellationToken.None);
        Require(originalGeometry.Accepted && originalGeometry.Document is not null && legacyGeometry.Accepted && legacyGeometry.Document is not null &&
            originalGeometry.Document.Shapes.Select(s => (s.Name, s.VertexCount, s.VertexPayloadSha256, s.TriangleTopologySha256))
                .SequenceEqual(legacyGeometry.Document.Shapes.Select(s => (s.Name, s.VertexCount, s.VertexPayloadSha256, s.TriangleTopologySha256))),
            "Independent readback did not preserve the real synthetic51 geometry: " + string.Join(";", legacyGeometry.Diagnostics));
        var providerManifest = Child(root, "provider-fixture/provider-manifest.json");
        JsonNode boundProvider = JsonNode.Parse(File.ReadAllBytes(providerManifest.Value))!;
        boundProvider["template"]!["path"] = providerPlugin;
        boundProvider["faceGeom"]!["path"] = providerFace;
        boundProvider["faceGeom"]!["sha256"] = HashFile(providerCarrier);
        boundProvider["faceGeom"]!["graphSha256"] = SseFaceGeomCarrierCodec.BuildStructure(SseFaceGeomCarrierCodec.Parse(legacyCarrier)).GraphSha256.Value;
        boundProvider["faceTint"]!["manifestPath"] = "provider-fixture/facetint-manifest.json";
        boundProvider["faceTint"]!["providerRoot"] = "provider-fixture/Data";
        boundProvider["dependencies"]!["manifestPath"] = "provider-fixture/dependency-manifest.json";
        File.WriteAllBytes(providerManifest.Value, JsonSerializer.SerializeToUtf8Bytes(boundProvider));
        var presetBundle = Child(root, "npc-preflight/preset-bundle.json");
        JsonNode bundle = JsonNode.Parse(File.ReadAllBytes(presetBundle.Value))!;
        bundle["schemaVersion"] = 1;
        bundle.AsObject().Remove("providerAuthority");
        bundle["providerContext"] = new JsonObject { ["manifestPath"] = "provider-fixture/provider-manifest.json", ["manifestSha256"] = HashFile(providerManifest),
            ["dependencyManifestPath"] = "provider-fixture/dependency-manifest.json", ["dependencyManifestSha256"] = HashFile(Child(root, "provider-fixture/dependency-manifest.json")) };
        File.WriteAllBytes(presetBundle.Value, JsonSerializer.SerializeToUtf8Bytes(bundle));
        execution["presetBundle"]!["manifestSha256"] = HashFile(presetBundle);
        execution["providerContext"] = new JsonObject
        {
            ["manifestPath"] = "provider-fixture/provider-manifest.json", ["manifestSha256"] = HashFile(Child(root, "provider-fixture/provider-manifest.json")),
            ["templatePlugin"] = providerPlugin, ["templateSha256"] = HashFile(Child(root, providerPlugin)), ["templateNpcFormId"] = "0x00000800",
            ["faceGeomCarrier"] = providerFace, ["faceGeomSha256"] = HashFile(Child(root, providerFace)),
            ["faceTintManifest"] = "provider-fixture/facetint-manifest.json", ["faceTintProviderRoot"] = "provider-fixture/Data",
            ["dependencyManifest"] = "provider-fixture/dependency-manifest.json"
        };
        execution["existingNpcTarget"] = new JsonObject { ["sourcePlugin"] = "Data/ActorwrightBlankNpcProvider.esp", ["sourcePluginSha256"] = originalHash, ["targetFormId"] = "0x00000800" };
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath("F:/ExampleGame"));
        var loader = new SkyrimFaceRecordPluginAuthorityLoader(policy, root);
        var data = Child(root, "Data");
        var order = await loader.LoadAsync(new(GameEdition.SkyrimSpecialEdition, data,
            [new PluginName("Skyrim.esm"), new PluginName("ActorwrightBlankNpcProvider.esp")]), CancellationToken.None);
        Require(order.Accepted, "Task27 skin plugin authority fixture was refused: " + string.Join(";", order.Diagnostics));
        var resolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(loader,
            new SkyrimAssetAuthorityPlanner(new BethesdaAssetIndexer(), policy, root));
        var resolved = await resolver.ResolveAsync(new(GameEdition.SkyrimSpecialEdition, data,
            new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746)), NpcSex.Female,
            order.Authorities), CancellationToken.None);
        Require(resolved.Accepted && resolved.Authority is not null, "Task27 skin resolver fixture was refused: " + string.Join(";", resolved.Diagnostics));
        var wholeSkin = Child(root, "whole-skin-authority.json");
        var written = await new RaceMenuNpcWholeSkinAuthorityWriter(policy, root).WriteAsync(
            new(resolved.Authority!, data, order.Authorities, wholeSkin), CancellationToken.None);
        Require(written.Written, "Task27 skin snapshot fixture was refused: " + string.Join(";", written.Diagnostics));
        execution["wholeSkinAuthority"] = Reference(wholeSkin);
        execution["standaloneAssets"]!["manifestSha256"] = HashFile(standalone);
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(execution));
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Task27 schema7 exact discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", "npc create-from-preset")).ExitCode == 0, "Task27 schema7 request schema discovery failed.");
        var created = await RunCliAsync(root, "npc", "create-from-preset", "--json", "--request", request.Value, "--request-sha256", HashFile(request));
        Require(created.ExitCode == 0, "Schema7 existing-target body authority did not complete: " + created.Root + created.StdErr);
        var pluginPath = Child(root, "planned-npc-output", "Data", "PackagedNpc.esp");
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath.Value, SkyrimRelease.SkyrimSE);
        var npc = plugin.Npcs.Single();
        Require(npc.FormKey == new FormKey(ModKey.FromNameAndExtension("ActorwrightBlankNpcProvider.esp"), 0x800), "Existing actor became an output-owned new NPC.");
        var armor = plugin.Armors.Single(a => a.FormKey == npc.WornArmor.FormKey);
        Require(armor.FormKey.ModKey == plugin.ModKey && armor.Armature.Count == 3, "Existing actor lacks private WNAM armature.");
        foreach (var link in armor.Armature)
        {
            var addon = plugin.ArmorAddons.Single(a => a.FormKey == link.FormKey);
            Require(addon.FormKey.ModKey == plugin.ModKey && addon.WorldModel!.Female!.File.ToString().Contains("Task27", StringComparison.OrdinalIgnoreCase), "Existing actor did not bind private BodySlide model authority.");
        }
        foreach (JsonNode row in meshes.OfType<JsonNode>())
            Require(HashFile(Child(root, "planned-npc-output", "Data", row["destination"]!.GetValue<string>())) == row["sha256"]!.GetValue<string>(), "Retained body model bytes differ from authority.");
        Require(HashFile(source) == originalHash, "Schema7 existing-target execution changed the source plugin.");
        var proposal = await NpcAppearanceOverrideProposalReader.ReadAsync(
            Child(root, "planned-npc-output", "evidence", "npc-appearance-override-proposal.json"), CancellationToken.None);
        var overrideRequest = new NpcAppearanceOverrideRequest(proposal.Edition, source, proposal.SourceSha256,
            proposal.TargetFormId, proposal.ProposalPath, pluginPath, proposal.Race, proposal.Sex,
            proposal.Appearance, proposal.RuntimeAppearance, proposal.OutfitPatch, proposal.IsCharGenFacePreset)
        { PluginAuthorities = order.Authorities.Select(a => new NpcCreationPluginAuthority(a.Plugin, a.Path, a.ExpectedSha256)).ToImmutableArray() };
        var disconnected = (SkyrimMod)plugin.DeepCopy();
        disconnected.Npcs.Single().WornArmor.SetTo(FormKey.Null);
        var disconnectedPath = Child(root, "disconnected-skin.esp");
        disconnected.WriteToBinary(disconnectedPath.Value, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
        var disconnectedReadback = BethesdaNpcAppearanceOverrideVerifier.Verify(overrideRequest, proposal, disconnectedPath, CancellationToken.None);
        Require(!disconnectedReadback.IsValid && disconnectedReadback.Diagnostics.Any(d => d.Code == "npc-appearance-override-skin-binding"),
            "Independent existing-target readback accepted a disconnected private WNAM graph.");
        execution["wholeSkinAuthority"]!["manifestSha256"] = new string('0', 64);
        execution["output"]!["root"] = "stale-skin-output";
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(execution));
        var stale = await RunCliAsync(root, "npc", "create-from-preset", "--json", "--request", request.Value, "--request-sha256", HashFile(request));
        Require(stale.ExitCode != 0 && stale.Root.ToString().Contains("whole-skin", StringComparison.Ordinal) && !Directory.Exists(Child(root, "stale-skin-output").Value),
            "Stale whole-skin authority did not refuse before creating an existing-target package.");
        execution["schemaVersion"] = 1;
        execution.Remove("existingNpcTarget");
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(execution));
        var wrongArm = await RunCliAsync(root, "npc", "create-from-preset", "--json", "--request", request.Value, "--request-sha256", HashFile(request));
        Require(wrongArm.ExitCode != 0 && wrongArm.Root.ToString().Contains("wholeSkinAuthority is supported only", StringComparison.Ordinal),
            "wholeSkinAuthority escaped the closed schema2 existing-target arm.");
        execution["wholeSkinAuthority"] = null;
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(execution));
        var nullAuthority = await RunCliAsync(root, "npc", "create-from-preset", "--json", "--request", request.Value, "--request-sha256", HashFile(request));
        Require(nullAuthority.ExitCode != 0 && nullAuthority.Root.ToString().Contains("wholeSkinAuthority must be an object", StringComparison.Ordinal),
            "Explicit null wholeSkinAuthority bypassed its closed document shape.");
        Console.WriteLine("Schema7 actual existing-target private skin and six mesh authorities verified: " + root.Value);
    }
}
