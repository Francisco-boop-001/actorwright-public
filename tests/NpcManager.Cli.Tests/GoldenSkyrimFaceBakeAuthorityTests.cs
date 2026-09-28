using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunFaceBakeAuthorityDerivationAsync()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        var root = new WorkspacePath(Path.Combine(repositoryRoot, "artifacts", "test-work",
            $"face-bake-derivation-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root.Value);
        try
        {
            ProtocolInvocation version = await RunCliAsync(root,
                ["version", "--protocol", "2", "--json"]);
            Require(version.ExitCode == 0, "Exact derivation-test CLI version discovery failed.");
            ProtocolInvocation capabilities = await RunCliAsync(root,
                ["capabilities", "--protocol", "2", "--json"]);
            Require(capabilities.ExitCode == 0, "Exact derivation-test CLI capability discovery failed.");
            ProtocolInvocation schema = await RunCliAsync(root,
                ["schema", "export", "--protocol", "2", "--json", "--command", "npc create-from-jslot"]);
            Require(schema.ExitCode == 0, "Face-bake command schema discovery failed.");
            Require(schema.Root.GetRawText().Contains("skyrim-face-bake-authority/1", StringComparison.Ordinal),
                "NPC preflight must publish the closed existing face-bake authority document schema.");

            string fixtureCatalog = Path.Combine(repositoryRoot, "tools", "release", "protocol-v2-workflow-probes", "catalog.json");
            MaterializeProbeFixtures(repositoryRoot, fixtureCatalog, "workspace preflight", root);
            MaterializeProbeFixtures(repositoryRoot, fixtureCatalog, "preset inspect", root);
            MaterializeProbeFixtures(repositoryRoot, fixtureCatalog, "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var request = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(repositoryRoot, root, request, HashFile(preset));
            PrepareFaceBakeDerivationFixture(repositoryRoot, root);
            var carrierBytes = File.ReadAllBytes(Child(root, "derivation-provider", "complete-carrier.nif").Value).ToImmutableArray();
            var carrierReader = new SkyrimFaceBakeCarrierGeometryReader();
            var carrierRead = carrierReader.Read(carrierBytes, new Sha256Hash(HashFile(Child(root, "derivation-provider", "complete-carrier.nif"))));
            Require(carrierRead.Accepted && carrierRead.Shapes.Length == 7,
                "The bound complete carrier did not expose its actual packed geometry: " + string.Join("; ", carrierRead.Diagnostics.Select(item => item.Message)));
            Require(!carrierReader.Read(carrierBytes, new Sha256Hash(new string('0', 64))).Accepted,
                "Carrier geometry accepted a false exact hash.");
            var truncatedCarrier = carrierBytes.AsSpan(0, 100).ToArray().ToImmutableArray();
            Require(!carrierReader.Read(truncatedCarrier, new Sha256Hash(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(truncatedCarrier.AsSpan())))).Accepted,
                "Carrier geometry admitted truncated NIF bytes after a matching hash.");
            var authority = Child(root, "npc-preflight", "derived-face-bake.json");
            BindDerivedFaceBake(root, request, authority, new string('0', 64));
            var preflight = Child(root, "npc-preflight", "derive-preflight.json");
            ProtocolInvocation refused = await RunCliAsync(root,
                ["npc", "create-from-jslot", "--json",
                 "--request", request.Value, "--request-sha256", HashFile(request),
                 "--preset", preset.Value, "--preset-sha256", new string('0', 64),
                 "--data-root", Child(root, "Data").Value,
                 "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                 "--companion-root", Child(root, "companion").Value,
                 "--preflight-output", preflight.Value,
                 "--face-bake-authority-output", authority.Value]);
            Require(refused.ExitCode == 4 && !File.Exists(authority.Value) &&
                    refused.Root.GetRawText().Contains("face-bake-derivation-preset-hash", StringComparison.Ordinal),
                "Derivation must refuse the mismatched exact JSlot hash without publishing an authority: " + refused.Root.GetRawText());

            ProtocolInvocation emitted = await DeriveAsync("emitted-preflight.json", authority);
            Require(File.Exists(authority.Value), "Derivation did not publish the requested authority: " + emitted.Root.GetRawText());
            var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
            var loader = new SkyrimFaceBakeAuthorityLoader(policy, root, new SkyrimAssetContentResolver(policy, root));
            var loaded = await loader.LoadAsync(new(root, authority, new Sha256Hash(HashFile(authority))), CancellationToken.None);
            Require(loaded.Loaded && loaded.Authority is { CarrierShapes.Length: 7 },
                "The emitted authority did not load unchanged: " + string.Join("; ", loaded.Diagnostics.Select(item => item.Message)));
            Require(loaded.Authority!.CatalogConfigs.Any(row => row.Asset.AssetPath.Value.EndsWith(".slider", StringComparison.Ordinal)),
                "Derivation omitted the real referenced .slider authority.");
            using (JsonDocument phaseOne = JsonDocument.Parse(File.ReadAllBytes(Child(root, "npc-preflight", "emitted-preflight.json").Value)))
            {
                Require(!Gate(phaseOne.RootElement, "facegeom-codec"),
                    "Emission falsely repaired the unchanged standalone manifest's zero authority hash.");
                Console.WriteLine("Before explicit binding: appearance-plan=" + Gate(phaseOne.RootElement, "appearance-plan") + ", facegeom-codec=false");
            }
            var repeated = Child(root, "npc-preflight", "derived-face-bake-repeat.json");
            await DeriveAsync("repeated-preflight.json", repeated);
            Require(File.ReadAllBytes(authority.Value).AsSpan().SequenceEqual(File.ReadAllBytes(repeated.Value)),
                "Pinned derivation bytes depend on the fresh output path.");
            await VerifyFaceBakeV2EmissionAsync(root, preset, request, authority);
            string stableHash = HashFile(authority);
            ProtocolInvocation occupied = await DeriveAsync("occupied-preflight.json", authority);
            Require(occupied.ExitCode == 4 && HashFile(authority) == stableHash,
                "Derivation overwrote an existing authority.");
            foreach (string badPath in new[] { "meshes/actors/character/FaceGenMorphs/ActorwrightBlankNpcProvider.esp/derived.txt", "../escape.slider" })
            {
                var invalid = JsonNode.Parse(File.ReadAllBytes(authority.Value))!;
                JsonNode slider = invalid["assets"]!.AsArray().Single(row => row!["assetPath"]!.GetValue<string>().EndsWith(".slider", StringComparison.Ordinal))!;
                slider["assetPath"] = badPath;
                var badFile = Child(root, "npc-preflight", "bad-" + (badPath.StartsWith("..", StringComparison.Ordinal) ? "escape" : "extension") + ".json");
                File.WriteAllText(badFile.Value, invalid.ToJsonString(), new UTF8Encoding(false));
                var invalidLoad = await loader.LoadAsync(new(root, badFile, new Sha256Hash(HashFile(badFile))), CancellationToken.None);
                Require(!invalidLoad.Loaded, "Catalog authority admitted an unsafe path or unsupported extension.");
            }
            BindDerivedFaceBake(root, request, authority, stableHash);
            var followup = Child(root, "npc-preflight", "bound-preflight.json");
            ProtocolInvocation ready = await RunCliAsync(root, "npc", "create-from-jslot", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value, "--preflight-output", followup.Value);
            Require(ready.ExitCode == 0, "Ordinary preflight refused the explicitly bound authority: " + ready.Root.GetRawText());
            using (JsonDocument phaseTwo = JsonDocument.Parse(File.ReadAllBytes(followup.Value)))
                Require(Gate(phaseTwo.RootElement, "appearance-plan") && Gate(phaseTwo.RootElement, "facegeom-codec"),
                    "Ordinary production preflight did not pass both real gates after explicit binding.");
            await VerifyFaceBakeDerivationControlsAsync(root, preset);
            ProtocolInvocation build = await RunCliAsync(root, "npc", "create-from-preset", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request));
            Require(build.ExitCode == 0 && build.Root.GetProperty("completed").GetBoolean(),
                "The ordinary copied-NPC build did not consume the emitted authority: " + build.Root.GetRawText());
            var finalNif = new WorkspacePath(build.Root.GetProperty("faceGeom").GetString()!);
            var finalGeometry = carrierReader.Read(File.ReadAllBytes(finalNif.Value).ToImmutableArray(), new Sha256Hash(HashFile(finalNif)));
            Require(finalGeometry.Accepted && finalGeometry.Shapes.Length == 7, "Final FaceGeom did not independently reopen.");
            foreach (var shape in finalGeometry.Shapes)
            {
                var expected = loaded.Authority.CarrierShapes.Single(row => row.CarrierShapeName == shape.Name);
                Require(shape.PackedPositionSha256 == expected.ExpectedModelPositionSha256 &&
                        shape.TopologySha256 == expected.ExpectedCarrierTopologySha256,
                    "Final FaceGeom did not retain the exact derived neutral-bake XYZ/topology: " + shape.Name);
                var original = carrierRead.Shapes.Single(row => row.Name == shape.Name);
                Require((shape.PackedPositionSha256 != original.PackedPositionSha256) == (shape.Name == "DerivedHead"),
                    "Expected one real head XYZ transfer and unchanged other shapes: " + shape.Name);
            }
            Console.WriteLine("Ordinary build: exact derived XYZ/topology verified for 7 shapes; 1 head changed, 6 shapes unchanged.");

            async Task<ProtocolInvocation> DeriveAsync(string outputName, WorkspacePath destination) =>
                await RunCliAsync(root, "npc", "create-from-jslot", "--json",
                    "--request", request.Value, "--request-sha256", HashFile(request),
                    "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                    "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                    "--companion-root", Child(root, "companion").Value,
                    "--preflight-output", Child(root, "npc-preflight", outputName).Value,
                    "--face-bake-authority-output", destination.Value);
        }
        finally
        {
            Console.WriteLine("Face-bake derivation fixture: " + root.Value);
        }
    }

    private static bool Gate(JsonElement preflight, string id) => preflight.GetProperty("requiredGates")
        .EnumerateArray().Single(row => row.GetProperty("id").GetString() == id).GetProperty("passed").GetBoolean();

    private static async Task VerifyFaceBakeV2EmissionAsync(WorkspacePath root, WorkspacePath preset,
        WorkspacePath request, WorkspacePath firstAuthority)
    {
        Directory.CreateDirectory(Child(root, "evidence").Value);
        Directory.CreateDirectory(Child(root, "workflow").Value);
        var initialWorkflow = Child(root, "workflow", "derive-intake.json");
        var intake = await RunCliAsync(root, "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse", "--workspace-root", root.Value, "--data-root", Child(root, "Data").Value,
            "--output-root", Child(root, "reserved-output").Value, "--load-order", WriteCanonicalLoadOrder(root).Value,
            "--intake-output", Child(root, "evidence", "derive-intake.json").Value,
            "--npc-editor-id", "ActorwrightResumptionNpc", "--workflow-output", initialWorkflow.Value);
        RequireSucceeded(intake, "workspace preflight");
        var presetWorkflow = Child(root, "workflow", "derive-preset.json");
        var inspected = await RunCliAsync(root, "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse", "--input", preset.Value,
            "--input-sha256", HashFile(preset), "--inspection-output", Child(root, "evidence", "derive-preset.json").Value,
            "--workflow-bundle", initialWorkflow.Value, "--workflow-bundle-sha256", RequireArtifact(intake, "workflow-bundle").Sha256,
            "--workflow-output", presetWorkflow.Value);
        RequireSucceeded(inspected, "preset inspect");
        var authority = Child(root, "npc-preflight", "derived-v2.json");
        var outputWorkflow = Child(root, "workflow", "derive-output.json");
        var result = await RunCliAsync(root, "npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request),
            "--preset", preset.Value, "--preset-sha256", HashFile(preset), "--data-root", Child(root, "Data").Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp", "--companion-root", Child(root, "companion").Value,
            "--preflight-output", Child(root, "npc-preflight", "derive-v2-preflight.json").Value,
            "--face-bake-authority-output", authority.Value, "--workflow-bundle", presetWorkflow.Value,
            "--workflow-bundle-sha256", RequireArtifact(inspected, "workflow-bundle").Sha256, "--workflow-output", outputWorkflow.Value);
        Require(result.Root.GetProperty("result").GetProperty("created").GetBoolean() &&
                !result.Root.GetProperty("result").GetProperty("readyForBuild").GetBoolean() && !File.Exists(outputWorkflow.Value),
            "V2 derivation advanced an unbound workflow: " + result.Root.GetRawText());
        RequireBinding(RequireArtifact(result, "skyrim-face-bake-authority"), authority,
            "skyrim-face-bake-authority/1", "npc create-from-jslot");
        Require(File.ReadAllBytes(authority.Value).AsSpan().SequenceEqual(File.ReadAllBytes(firstAuthority.Value)),
            "V1 and V2 derived different authority bytes from the same bound inputs.");
    }

    private static void BindDerivedFaceBake(WorkspacePath root, WorkspacePath requestPath, WorkspacePath authority, string hash)
    {
        var standalonePath = Child(root, "npc-preflight", "standalone-assets.json");
        var standalone = JsonNode.Parse(File.ReadAllBytes(standalonePath.Value))!;
        standalone["schemaVersion"] = 4;
        standalone["faceBakeAuthority"] = new JsonObject
        {
            ["manifestPath"] = Path.GetRelativePath(root.Value, authority.Value).Replace('\\', '/'),
            ["manifestSha256"] = hash
        };
        File.WriteAllText(standalonePath.Value, standalone.ToJsonString(), new UTF8Encoding(false));
        var request = JsonNode.Parse(File.ReadAllBytes(requestPath.Value))!;
        request["standaloneAssets"]!["manifestSha256"] = HashFile(standalonePath);
        File.WriteAllText(requestPath.Value, request.ToJsonString(), new UTF8Encoding(false));
    }
}
