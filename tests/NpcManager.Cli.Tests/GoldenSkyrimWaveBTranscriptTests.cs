using System.Text;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunWaveBTranscriptAsync(ICompatibilityJourneySink? compatibilitySink = null)
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var root = new WorkspacePath(
            CompatibilityJourneySupport.CreateWaveBRoot(repository));
        Directory.CreateDirectory(root.Value);
        string operationId = Path.GetFileName(root.Value).Split('-')[^1];
        compatibilitySink ??= CompatibilityJourneyFileSink.FromEnvironment(
            root.Value, Child(root, "finished").Value, operationId);
        var transcript = new WaveBTranscript(root);
        string catalog = Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json");
        MaterializeProbeFixtures(repository, catalog, "npc create-from-jslot", root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        var request = Child(root, "npc-preflight", "request.json");
        PrepareGoldenPresetFixture(preset);
        CorrectNpcRequestFixture(
            repository, root, request, HashFile(preset),
            workspaceProvider: true);
        PrepareWaveBBrowFixture(root);
        var initialInputs = Child(root, "evidence", "initial-inputs");
        Directory.CreateDirectory(initialInputs.Value);
        foreach (string name in new[] { "fixture.jslot", "request.json", "preset-bundle.json", "record-authority.json", "standalone-assets.json" })
            File.Copy(Child(root, "npc-preflight", name).Value, Child(initialInputs, name).Value);
        File.Copy(Child(root, "Data", "ActorwrightBlankNpcProvider.esp").Value, Child(initialInputs, "ActorwrightBlankNpcProvider.esp").Value);

        foreach (string command in new[] { "version", "capabilities" })
            await transcript.Success(command, command, "--protocol", "2", "--json");
        foreach (string command in new[] { "workspace preflight", "preset inspect", "npc create-from-jslot", "records propose",
                     "plugin write", "npc face-patch", "npc finish analyze", "npc finish apply", "npc finish verify",
                     "npc placement interior analyze", "npc placement interior apply", "npc placement interior verify", "package verify", "plugin audit" })
            await transcript.Success("schema " + command, "schema", "export", "--protocol", "2", "--json", "--command", command);
        await transcript.Success("workspace preflight", "workspace", "preflight", "--json", "--workspace-root", root.Value,
            "--output-root", Child(root, "workspace-output").Value);
        await transcript.Success("preset inspect", "preset", "inspect", "--json", "--format", "racemenu-jslot", "--edition", "skyrimse", "--input", preset.Value);
        var preflightPath = Child(root, "evidence", "initial-preflight.json");
        var preflight = await transcript.Success("create preflight", WaveBCreateArguments(root, "--preflight-output", preflightPath.Value));
        Require(preflight.Root.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                row.GetProperty("path").GetString()?.Equals("textures/actors/character/female/femalehead.dds", StringComparison.OrdinalIgnoreCase) == true &&
                row.GetProperty("status").GetString() == "present"),
            "Base-race FemaleHead.dds and hair-model femalehead.dds must share availability while preserving each requested authority spelling.");
        Require(preflight.Root.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                row.GetProperty("path").GetString()?.EndsWith("brow-race-501.tri", StringComparison.OrdinalIgnoreCase) == true),
            "The actual preflight omitted the collision TRI dependency.");
        ProtocolInvocation collision = await transcript.Invoke("create 141/501 refusal", WaveBCreateArguments(root));
        Require(collision.ExitCode != 0 && collision.Root.ToString().Contains("sse-face-bake-tri-topology-mismatch", StringComparison.Ordinal),
            "The actual native build did not refuse the 141/501 brow with its typed topology diagnostic: " + collision.Root);
        Require(!Directory.Exists(Child(root, "planned-npc-output").Value), "Topology refusal published a package.");

        await RepairWaveBBrowAsync(root, transcript);
        RefreshWaveBBindings(root, repaired: true);
        var repairedPreflight = await transcript.Success("repaired create preflight", WaveBCreateArguments(root,
            "--preflight-output", Child(root, "evidence", "repaired-preflight.json").Value));
        Require(repairedPreflight.Root.GetProperty("readyForBuild").GetBoolean(), "Repaired closure is not ready.");
        await using CompatibilityTransientSourceWatcher? transientCapture =
            compatibilitySink is null ? null :
            new CompatibilityTransientSourceWatcher(root.Value);
        await transcript.Success("repaired create build", WaveBCreateArguments(root));
        if (transientCapture is not null) await transientCapture.StopAsync();
        transcript.Note("Actual copied-provider HDPT repair admitted the formerly refused 141/501 create path.");
        await RunWaveBFinishAndPlacementAsync(root, transcript);
        using JsonDocument finishRequest = JsonDocument.Parse(File.ReadAllBytes(
            Child(root, "evidence", "finish-request.json").Value));
        string sourceBeforeSha256 = finishRequest.RootElement.GetProperty("source")
            .GetProperty("pluginSha256").GetString()!;
        var inventory = Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".actorwright" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, Child(root, "transcript.md").Value, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(path, Child(root, "artifact-hashes.json").Value, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Select(path => new { path = Path.GetRelativePath(root.Value, path).Replace('\\', '/'),
                sha256 = HashFile(new WorkspacePath(path)), byteLength = new FileInfo(path).Length }).ToArray();
        var inventoryPath = Child(root, "artifact-hashes.json");
        File.WriteAllBytes(inventoryPath.Value, JsonSerializer.SerializeToUtf8Bytes(inventory, PrettyDocumentOptions));
        transcript.Note("Retained inputs, outputs and responses are inventoried in artifact-hashes.json; journals, this evolving transcript and the inventory itself are excluded to avoid a digest cycle. Inventory SHA256 `" + HashFile(inventoryPath) + "`.");
        if (compatibilitySink is not null)
        {
            string sourceAfterSha256 = HashFile(Child(root, "planned-npc-output", "Data", "PackagedNpc.esp"));
            var finishedPlugin = Child(root, "finished", "Data", "PackagedNpc.esp");
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(
                finishedPlugin.Value, SkyrimRelease.SkyrimSE);
            var npc = plugin.Npcs.Single();
            using var sourcePlugin = SkyrimMod.CreateFromBinaryOverlay(
                Child(root, "planned-npc-output", "Data", "PackagedNpc.esp").Value,
                SkyrimRelease.SkyrimSE);
            var sourceNpc = sourcePlugin.Npcs.Single();
            var semanticInventory = inventory.Select(item =>
                CompatibilityJourneySupport.Artifact(root.Value, item.path, "inventory-member")).ToArray();
            compatibilitySink.Capture(new CompatibilityJourneyObservation(
                "journey.wave-b-v1",
                transcript.Steps,
                [
                    CompatibilityJourneySupport.Artifact(root.Value, "planned-npc-output/npcmanager-package.json", "source-package-manifest"),
                    CompatibilityJourneySupport.Artifact(root.Value, "planned-npc-output/Data/PackagedNpc.esp", "source-plugin"),
                    CompatibilityJourneySupport.Artifact(root.Value, "finished/Data/PackagedNpc.esp", "finished-plugin"),
                    CompatibilityJourneySupport.Artifact(root.Value, "finished/NPCManager/Evidence/finish-core-manifest.json", "finish-manifest"),
                    CompatibilityJourneySupport.Artifact(root.Value, "finished.zip", "finished-archive"),
                    CompatibilityJourneySupport.Artifact(root.Value, "placement/PackagedNpc_InteriorPlacement.esp", "placement-plugin")
                ],
                new JsonObject
                {
                    ["topologyRefusal"] = true,
                    ["privateHdptRepair"] = true,
                    ["readyForReviewedWrite"] = true,
                    ["applied"] = true,
                    ["verified"] = true,
                    ["placementVerified"] = true,
                    ["packageVerification"] = new JsonObject
                    {
                        ["code"] = "workflow-human-review-required",
                        ["verified"] = false
                    },
                    ["pluginAudit"] = true,
                    ["source"] = new JsonObject
                    {
                        ["plugin"] = sourcePlugin.ModKey.ToString(),
                        ["masters"] = new JsonArray(sourcePlugin.ModHeader.MasterReferences
                            .Select(master => JsonValue.Create(master.Master.ToString())).ToArray()),
                        ["actorFormId"] = $"0x{sourceNpc.FormKey.ID:X8}",
                        ["beforeSha256"] = sourceBeforeSha256,
                        ["afterSha256"] = sourceAfterSha256,
                        ["unchanged"] = sourceBeforeSha256 == sourceAfterSha256
                    },
                    ["final"] = new JsonObject
                    {
                        ["plugin"] = plugin.ModKey.ToString(),
                        ["masters"] = new JsonArray(plugin.ModHeader.MasterReferences
                            .Select(master => JsonValue.Create(master.Master.ToString())).ToArray()),
                        ["actorFormId"] = $"0x{npc.FormKey.ID:X8}",
                        ["sha256"] = HashFile(finishedPlugin)
                    },
                    ["artifactInventory"] = new JsonArray(semanticInventory.Select(item =>
                        (JsonNode)CompatibilityJourneySupport.ArtifactJson(item)).ToArray())
                },
                transientCapture?.Seal(
                    Child(root, "planned-npc-output", "Data", "NPCManager", "Evidence", "racemenu-bundle.json").Value,
                    Child(root, "planned-npc-output", "Data", "NPCManager", "Evidence").Value)
                    ?? []));
        }
        Console.WriteLine("Wave B transcript retained: " + root.Value + "; final transcript SHA256 " + HashFile(Child(root, "transcript.md")));
    }

    private static string[] WaveBCreateArguments(WorkspacePath root, params string[] suffix) =>
    [
        "npc", "create-from-jslot", "--json", "--request", Child(root, "npc-preflight", "request.json").Value,
        "--request-sha256", HashFile(Child(root, "npc-preflight", "request.json")),
        "--preset", Child(root, "npc-preflight", "fixture.jslot").Value,
        "--preset-sha256", HashFile(Child(root, "npc-preflight", "fixture.jslot")),
        "--data-root", Child(root, "Data").Value,
        "--plugins", "Skyrim.esm,SyntheticHair.esp,ActorwrightBlankNpcProvider.esp",
        "--companion-root", Child(root, "companion").Value, .. suffix
    ];

    private sealed class WaveBTranscript
    {
        private readonly WorkspacePath root;
        private readonly StringBuilder markdown = new("# Synthetic V1 NPC end-to-end transcript\n\nNo runtime or visual authority. Discovery uses protocol 2; all workflow commands below use V1 without a protocol flag.\n\n");
        private readonly ImmutableArray<CompatibilityStepObservation>.Builder steps =
            ImmutableArray.CreateBuilder<CompatibilityStepObservation>();
        private int sequence;
        internal ImmutableArray<CompatibilityStepObservation> Steps => steps.ToImmutable();
        internal WaveBTranscript(WorkspacePath root)
        {
            this.root = root;
            Directory.CreateDirectory(Child(root, "evidence").Value);
            markdown.Append("Workspace: `").Append(root.Value).AppendLine("`; every child receives ACTORWRIGHT_WORKSPACE_ROOT.\n");
            markdown.Append("Product CLI SHA256: `").Append(HashFile(new WorkspacePath(typeof(CommandLine).Assembly.Location))).AppendLine("`.\n");
        }
        internal async Task<ProtocolInvocation> Invoke(string label, params string[] args)
        {
            ProtocolInvocation result = await RunCliAsync(root, args);
            string command = args is ["schema", "export", ..]
                ? "schema export:" + args.SkipWhile(argument => argument != "--command").Skip(1).Single()
                : string.Join(' ', args.TakeWhile(argument =>
                    argument.Length == 0 || argument[0] != '-'));
            steps.Add(new CompatibilityStepObservation(
                command,
                result.ExitCode,
                CompatibilityJourneySupport.ExitMeaning(result.ExitCode),
                command.StartsWith("schema export:", StringComparison.Ordinal)
                    ? CompatibilityJourneySupport.SchemaIds(result.Root)
                    : []));
            var response = Child(root, "evidence", $"{++sequence:D2}-response.json");
            File.WriteAllText(response.Value, result.Root.GetRawText());
            markdown.Append("## ").Append(sequence).Append(". ").AppendLine(label).AppendLine();
            markdown.Append("```powershell\n& '").Append(Environment.ProcessPath).Append("' '").Append(typeof(CommandLine).Assembly.Location).Append("' ")
                .AppendJoin(' ', args.Select(arg => "'" + arg.Replace("'", "''", StringComparison.Ordinal) + "'"))
                .Append("\n```\n\nExit: ").Append(result.ExitCode).Append(". Response: `").Append(Path.GetRelativePath(root.Value, response.Value))
                .Append("`; SHA256 `").Append(HashFile(response)).AppendLine("`.\n");
            File.WriteAllText(Child(root, "transcript.md").Value, markdown.ToString());
            return result;
        }
        internal async Task<ProtocolInvocation> Success(string label, params string[] args)
        {
            ProtocolInvocation result = await Invoke(label, args);
            Require(result.ExitCode == 0, label + " failed: " + result.Root + result.StdErr);
            return result;
        }
        internal void Note(string value)
        {
            markdown.AppendLine(value).AppendLine();
            File.WriteAllText(Child(root, "transcript.md").Value, markdown.ToString());
        }
    }
}
