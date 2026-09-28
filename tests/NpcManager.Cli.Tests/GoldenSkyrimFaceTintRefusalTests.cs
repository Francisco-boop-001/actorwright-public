using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunFaceTintRefusalJournalAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "test-work", "face-tint-refusal-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        foreach (string command in new[] { "version", "capabilities" })
        {
            var discovery = await RunCliAsync(root, command, "--protocol", "2", "--json");
            Require(discovery.ExitCode == 0, "Exact binary discovery failed.");
            if (command == "capabilities")
                Require(discovery.Root.GetProperty("result").GetProperty("commands").EnumerateArray()
                    .Any(item => item.GetProperty("name").GetString() == "face tint patch" &&
                        item.GetProperty("readiness").GetString() == "legacy"),
                    "Face tint patch must remain a legacy command.");
        }
        foreach (string command in new[] { "face tint patch", "face morph patch" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json",
                "--command", command)).ExitCode == 0, "Schema discovery failed.");

        var failures = new List<string>();
        foreach (string layers in new[] { "@relative.json", "@" })
        {
            WorkspacePath scenario = Child(root, layers.Length == 1 ? "empty" : "relative");
            Directory.CreateDirectory(scenario.Value);
            string[] arguments = FaceTintArguments(scenario, layers);
            var refusal = await RunCliAsync(scenario, arguments);
            AssertFaceTintRefusal(refusal.ExitCode, refusal.Root, scenario, failures);
            Require(refusal.Root.GetProperty("message").GetString()!.Contains(
                layers.Length == 1 ? "must be non-empty" : "must be fully qualified", StringComparison.Ordinal),
                "The layers refusal lost the original path constraint.");
            string journalRoot = Child(scenario, ".actorwright/operations").Value;
            string[] records = Directory.Exists(journalRoot) ? Directory.GetFiles(journalRoot, "*.json") : [];
            if (records.Length != 1)
                failures.Add($"{layers}: expected one persisted refusal, found {records.Length}.");
            else
            {
                Require(refusal.Root.GetProperty("journal").GetProperty("appended").GetBoolean() &&
                    refusal.Root.GetProperty("journal").GetProperty("warning").ValueKind == JsonValueKind.Null,
                    "A successful append was not reported honestly.");
                using var record = JsonDocument.Parse(await File.ReadAllTextAsync(records[0]));
                JsonElement item = record.RootElement;
                Require(item.GetProperty("command").GetString() == "face tint patch" &&
                    item.GetProperty("requestDigest").GetString() == ProtocolRequestDigest.Compute(CommandLine.Parse(arguments)) &&
                    item.GetProperty("outcome").GetString() == "refused" &&
                    item.GetProperty("exitCode").GetInt32() == 2 &&
                    item.GetProperty("diagnosticCodes").EnumerateArray().Single().GetString() == "usage-error" &&
                    item.GetProperty("diagnosticClasses").EnumerateArray().Single().GetString() == "usage" &&
                    item.GetProperty("artifactHashes").GetArrayLength() == 0,
                    "Persisted refusal lost its exact request, diagnostic, or outcome binding.");
                Require(item.GetProperty("effects").EnumerateArray().All(effect =>
                    effect.GetProperty("kind").GetString() == "appendLocalOperationJournal"),
                    "Input refusal recorded plugin work before validation.");
                Require(!item.ToString().Contains(scenario.Value, StringComparison.Ordinal) &&
                    !item.ToString().Contains(layers, StringComparison.Ordinal), "Journal leaked raw input.");
            }
            Console.WriteLine($"EVIDENCE {layers}: {refusal.Root.GetRawText()} journal-count={records.Length}");
        }

        WorkspacePath blocked = Child(root, "journal-blocked");
        Directory.CreateDirectory(blocked.Value);
        await File.WriteAllTextAsync(Child(blocked, ".actorwright").Value, "blocked journal storage");
        var writeFailure = await RunCliAsync(blocked, FaceTintArguments(blocked, "@relative.json"));
        AssertFaceTintRefusal(writeFailure.ExitCode, writeFailure.Root, blocked, failures);
        if (!writeFailure.Root.TryGetProperty("journal", out JsonElement journal) ||
            journal.GetProperty("appended").GetBoolean() ||
            !journal.TryGetProperty("warning", out JsonElement warning) ||
            warning.GetProperty("severity").GetString() != "warning" ||
            !warning.GetProperty("code").GetString()!.StartsWith("operation-journal-", StringComparison.Ordinal))
            failures.Add("Journal write failure must retain the usage refusal and report its unpersisted record.");
        Require(await File.ReadAllTextAsync(Child(blocked, ".actorwright").Value) == "blocked journal storage",
            "A failed journal append replaced the obstructing file.");
        Console.WriteLine("EVIDENCE journal-blocked: " + writeFailure.Root.GetRawText());

        WorkspacePath cancelled = Child(root, "journal-cancelled");
        Directory.CreateDirectory(cancelled.Value);
        string? previousRoot = Environment.GetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT", cancelled.Value);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var exit = await NpcManager.Cli.Program.RunAsync(FaceTintArguments(cancelled, "@"), stdout, stderr,
                path => new LocalOperationJournal(path), cancellation.Token);
            using var response = JsonDocument.Parse(stderr.ToString());
            AssertFaceTintRefusal((int)exit, response.RootElement, cancelled, failures);
            if (!response.RootElement.TryGetProperty("journal", out JsonElement cancelledJournal) ||
                cancelledJournal.GetProperty("appended").GetBoolean())
                failures.Add("Cancelled journal append must not claim a persisted record.");
            Require(!Directory.Exists(Child(cancelled, ".actorwright").Value),
                "Cancelled append created journal storage.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT", previousRoot);
        }

        WorkspacePath neighbor = Child(root, "neighbors");
        Directory.CreateDirectory(Child(neighbor, "input").Value);
        Directory.CreateDirectory(Child(neighbor, "output").Value);
        WorkspacePath source = Child(neighbor, "input/Tint.esp");
        WorkspacePath output = Child(neighbor, "output/Tint.esp");
        byte[] sourceBytes = Program.BuildSseFaceTintPlugin();
        await File.WriteAllBytesAsync(source.Value, sourceBytes);
        WorkspacePath validLayers = Child(neighbor, "layers.json");
        await File.WriteAllTextAsync(validLayers.Value,
            "[{\"index\":4,\"red\":12,\"green\":34,\"blue\":56,\"alpha\":255,\"coverage\":25,\"presetIndex\":0}]");
        var valid = await RunCliAsync(neighbor, "face", "tint", "patch", "--edition", "skyrimse",
            "--plugin", source.Value, "--output", output.Value, "--npc", "00000800",
            "--layers", "@" + validLayers.Value, "--expected-sha256", Hash(sourceBytes), "--apply", "--json");
        Require(valid.ExitCode == 0 && valid.Root.GetProperty("applied").GetBoolean() &&
            BethesdaSkyrimFaceTintAdapter.Read(GameEdition.SkyrimSpecialEdition, output, new FormId(0x800))
                .Layers.SequenceEqual([new SkyrimFaceTintLayer(4, 12, 34, 56, 255, 25, 0)]) &&
            (await File.ReadAllBytesAsync(source.Value)).SequenceEqual(sourceBytes),
            "Valid fully qualified layers no longer apply with independent readback and source preservation: " + valid.Root);
        var unrelated = await RunCliAsync(neighbor, "face", "morph", "patch", "--edition", "skyrimse", "--json");
        Require(unrelated.ExitCode == 2 && unrelated.Root.GetProperty("code").GetString() == "usage-error" &&
            unrelated.Root.EnumerateObject().Select(item => item.Name).SequenceEqual(["code", "message"]) &&
            !Directory.Exists(Child(neighbor, ".actorwright").Value),
            "An unrelated legacy command acquired journaling or changed its usage response.");
        Console.WriteLine("EVIDENCE valid-input and unrelated V1 controls passed; root=" + root.Value);
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string[] FaceTintArguments(WorkspacePath root, string layers) =>
        ["face", "tint", "patch", "--edition", "skyrimse", "--plugin", Child(root, "missing.esp").Value,
            "--output", Child(root, "output/missing.esp").Value, "--npc", "00000800", "--layers", layers, "--json"];

    private static void AssertFaceTintRefusal(int exit, JsonElement response, WorkspacePath root, List<string> failures)
    {
        Require(exit == 2 && response.GetProperty("code").GetString() == "usage-error" &&
            !string.IsNullOrWhiteSpace(response.GetProperty("message").GetString()) &&
            !Directory.Exists(Child(root, "output").Value) && !File.Exists(Child(root, "missing.esp").Value),
            "Invalid layers must retain the legacy usage refusal before plugin access/output creation.");
        if (!response.TryGetProperty("severity", out JsonElement severity) || severity.GetString() != "error" ||
            !response.TryGetProperty("recovery", out JsonElement recovery) ||
            recovery.GetProperty("option").GetString() != "layers" ||
            recovery.GetProperty("action").GetString() != "correctInput" ||
            string.IsNullOrWhiteSpace(recovery.GetProperty("constraint").GetString()) ||
            recovery.GetProperty("retryUnchangedSafe").GetBoolean())
            failures.Add("Invalid layers need error severity and correctInput recovery naming layers: " + response);
    }
}
