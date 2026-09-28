using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunRuntimeArchiveAsync()
    {
        using var scratch = new OwnedScratchDirectory(
            Path.Combine(Environment.CurrentDirectory, "artifacts", "x"),
            "ra-");
        var root = new WorkspacePath(scratch.Root);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Exact binary discovery failed.");
        foreach (string command in new[] { "package archive", "package verify" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0,
                "Package schema discovery failed: " + command);

        foreach (int count in new[] { 71, 112 })
        {
            WorkspacePath package = CreateRuntimeArchiveFixture(root, count, historicalProposal: true);
            await AssertRuntimeArchiveAsync(root, package, $"runtime-{count}.zip", count);
            var legacy = await RunCliAsync(root, "package", "archive", "--source-root", package.Value,
                "--output", Child(root, $"legacy-missing-outfit-{count}.zip").Value, "--json");
            Require(legacy.ExitCode != 0 && legacy.Root.ToString().Contains("package-archive-proposal", StringComparison.Ordinal),
                "Default archive must retain its historical proposal admission.");
        }

        WorkspacePath selectedPackage = CreateRuntimeArchiveFixture(root, 4, historicalProposal: false);
        const string preset = "SKSE/Plugins/CharGen/Presets/RuntimeFace.jslot";
        var selectedZip = Child(root, "selected-preset.zip");
        var selected = await RunCliAsync(root, "package", "archive", "--package", selectedPackage.Value,
            "--payload", "runtime-only", "--include-runtime-preset", preset, "--output", selectedZip.Value, "--json");
        Require(selected.ExitCode == 0 && selected.Root.GetProperty("written").GetBoolean(), "Explicit runtime preset archive failed: " + selected.Root + selected.StdErr);
        using (var zip = ZipFile.OpenRead(selectedZip.Value))
            Require(zip.Entries.Count == 5 && zip.GetEntry(preset) is not null && zip.Entries.All(entry => !entry.FullName.Contains("Other.jslot", StringComparison.Ordinal)),
                "Only the explicitly named declared runtime preset may enter the ZIP.");
        var selectedVerified = await RunCliAsync(root, "package", "verify", "--archive", selectedZip.Value, "--payload", "runtime-only",
            "--include-runtime-preset", preset, "--json");
        Require(selectedVerified.ExitCode == 0 && selectedVerified.Root.GetProperty("verified").GetBoolean(), "Selected preset layout did not verify.");
        var unselectedVerified = await RunCliAsync(root, "package", "verify", "--archive", selectedZip.Value, "--payload", "runtime-only", "--json");
        Require(unselectedVerified.ExitCode != 0, "Unselected preset was accepted as ordinary runtime payload.");

        var omittedZip = Child(root, "default-omitted.zip");
        var fullZip = Child(root, "default-explicit.zip");
        Require((await RunCliAsync(root, "package", "archive", "--source-root", selectedPackage.Value, "--output", omittedZip.Value, "--json")).ExitCode == 0,
            "Legacy archive omission failed.");
        Require((await RunCliAsync(root, "package", "archive", "--source-root", selectedPackage.Value, "--payload", "full", "--output", fullZip.Value, "--json")).ExitCode == 0,
            "Explicit full archive failed.");
        Require(HashFile(omittedZip) == HashFile(fullZip), "Default archive bytes changed when explicitly selecting the existing full payload.");

        foreach (string path in new[] { "../escape.nif", "Data/Fixture.esp", "NPCManager/Evidence/report.json", "meshes/manifest.json", "meshes/run.exe", "textures/../unsafe.dds" })
            await RefuseRuntimeZipAsync(root, "bad-" + Guid.NewGuid().ToString("N") + ".zip", [("Fixture.esp", "plugin"), (path, "bad")]);
        await RefuseRuntimeZipAsync(root, "case-alias.zip", [("Fixture.esp", "plugin"), ("textures/a.dds", "a"), ("textures/A.dds", "b")]);
        await RefuseRuntimeZipAsync(root, "no-plugin.zip", [("textures/a.dds", "a")]);
        var conflict = await RunCliAsync(root, "package", "archive", "--source-root", selectedPackage.Value,
            "--package", Child(root, "different").Value, "--payload", "runtime-only", "--output", Child(root, "conflict.zip").Value, "--json");
        Require(conflict.ExitCode != 0 && !File.Exists(Child(root, "conflict.zip").Value), "Conflicting source aliases were silently accepted.");
        var badPreset = await RunCliAsync(root, "package", "archive", "--package", selectedPackage.Value, "--payload", "runtime-only",
            "--include-runtime-preset", "NPCManager/Evidence/report.json", "--output", Child(root, "bad-preset.zip").Value, "--json");
        Require(badPreset.ExitCode != 0 && !File.Exists(Child(root, "bad-preset.zip").Value), "Preset selector authorized arbitrary evidence.");

        // Even omitted evidence remains part of the source package verification.
        File.AppendAllText(Child(selectedPackage, "Data/NPCManager/Evidence/report.json").Value, "changed");
        var drift = await RunCliAsync(root, "package", "archive", "--package", selectedPackage.Value, "--payload", "runtime-only",
            "--output", Child(root, "source-drift.zip").Value, "--json");
        Require(drift.ExitCode != 0 && !File.Exists(Child(root, "source-drift.zip").Value), "Runtime filtering bypassed source evidence hash verification.");
        Console.WriteLine("Runtime archive synthetic layout controls: 71 and 112 files, exact hashes, selected preset, default bytes and refusal controls PASS.");
        await RunProtocolV2FinishReviewedAsync(runtimeArchive: true);
        Console.WriteLine("PASS package-runtime-archive");
    }

    private static WorkspacePath CreateRuntimeArchiveFixture(WorkspacePath root, int runtimeCount, bool historicalProposal)
    {
        var package = Child(root, "package-" + runtimeCount);
        Directory.CreateDirectory(package.Value);
        var files = new JsonArray();
        void Add(string path, string kind, string text)
        {
            var physical = Child(package, path);
            Directory.CreateDirectory(Path.GetDirectoryName(physical.Value)!);
            File.WriteAllText(physical.Value, text, new UTF8Encoding(false));
            files.Add(new JsonObject { ["kind"] = kind, ["relativePath"] = path,
                ["byteLength"] = new FileInfo(physical.Value).Length, ["sha256"] = HashFile(physical) });
        }
        Add("Data/Fixture.esp", "plugin", "Synthetic archive mechanics only; the later real Finish fixture proves the product path.");
        Add("Data/meshes/fixture/head.nif", "private-headpart-model", "synthetic-nif");
        Add("Data/SEQ/Fixture.seq", "finish-core-evidence", "synthetic-seq");
        for (int i = 3; i < runtimeCount; i++) Add($"Data/textures/fixture/{i}.dds", "transitive-texture-" + i, "synthetic-dds-" + i);
        Add("Data/NPCManager/Evidence/report.json", "evidence", "{}");
        Add("Data/SKSE/Plugins/CharGen/Presets/RuntimeFace.jslot", "runtime-preset", "{}");
        Add("Data/SKSE/Plugins/CharGen/Presets/Other.jslot", "preset", "{}");
        if (historicalProposal)
            Add("NPCManager/Evidence/creation.json", "npc-creation-proposal", "{\"artifactKind\":\"skyrim-npc-creation-proposal\",\"references\":{}}");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["edition"] = "skyrimse", ["presetFormat"] = "racemenu",
            ["sourcePreset"] = "fixture.jslot", ["sourcePresetSha256"] = new string('a', 64),
            ["sourcePlugin"] = "source.esp", ["sourcePluginSha256"] = new string('b', 64),
            ["outputPlugin"] = "Fixture.esp", ["targetFormId"] = "0x00000800", ["artifacts"] = files
        };
        File.WriteAllText(Child(package, "npcmanager-package.json").Value, manifest.ToJsonString());
        return package;
    }

    private static async Task AssertRuntimeArchiveAsync(
        WorkspacePath root,
        WorkspacePath package,
        string filename,
        int? expectedCount = null,
        WorkspacePath? reviewedWorkflow = null)
    {
        var archivePath = Child(root, filename);
        string sourceHash = HashFile(Child(package, "npcmanager-package.json"));
        if (reviewedWorkflow is not null)
        {
            var unreviewedArchive = Child(root, "unreviewed-" + filename);
            var unreviewed = await RunCliAsync(root, "package", "archive", "--package", package.Value,
                "--payload", "runtime-only", "--output", unreviewedArchive.Value, "--json");
            Require(unreviewed.ExitCode != 0 &&
                    (unreviewed.Root.ToString() + unreviewed.StdErr).Contains(
                        "workflow-human-review-required",
                        StringComparison.Ordinal) &&
                    !File.Exists(unreviewedArchive.Value),
                "A finished runtime archive bypassed its exact workflow bundle.");
        }
        string[] workflowOptions = reviewedWorkflow is { } reviewed
            ? ["--workflow-bundle", reviewed.Value, "--workflow-bundle-sha256", HashFile(reviewed)]
            : [];
        string[] archiveArguments =
        [
            "package", "archive", "--package", package.Value,
            "--payload", "runtime-only", "--output", archivePath.Value,
            "--json",
            .. workflowOptions
        ];
        var created = await RunCliAsync(root, archiveArguments);
        Require(created.ExitCode == 0 && created.Root.GetProperty("written").GetBoolean(),
            "Runtime-only archive must be produced from the fully verified source package: " + created.Root + created.StdErr);
        var artifact = created.Root.GetProperty("artifact");
        Require(!artifact.GetProperty("runtimeProof").GetBoolean() && artifact.GetProperty("independentlyReopened").GetBoolean(), "Archive authority was overstated.");
        using (var zip = ZipFile.OpenRead(archivePath.Value))
        {
            if (expectedCount is { } count) Require(zip.Entries.Count == count, "Runtime file count differs from the owned layout fixture.");
            Require(zip.Entries.Any(entry => entry.FullName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) && !entry.FullName.Contains('/')),
                "Install plugin is not at ZIP root.");
            foreach (var entry in zip.Entries)
            {
                Require(!entry.FullName.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) && !entry.FullName.Contains("NPCManager", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith(".jslot", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
                    "Runtime archive contains a wrapper, evidence, manifest or unselected preset: " + entry.FullName);
                using Stream stream = entry.Open();
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                Require(new Sha256Hash(actual) == new Sha256Hash(HashFile(Child(package, "Data/" + entry.FullName))), "ZIP bytes differ from verified source: " + entry.FullName);
            }
        }
        var verified = await RunCliAsync(root, "package", "verify", "--archive", archivePath.Value, "--payload", "runtime-only", "--json");
        Require(verified.ExitCode == 0 && verified.Root.GetProperty("verified").GetBoolean(), "Standalone runtime layout verification failed: " + verified.Root + verified.StdErr);
        var layout = verified.Root.GetProperty("artifact");
        Require(!layout.GetProperty("sourceManifestBound").GetBoolean() && !layout.GetProperty("installDependencyAuthority").GetBoolean() &&
            !layout.GetProperty("runtimeProof").GetBoolean(), "Standalone ZIP layout claimed unavailable source/install/runtime authority.");
        Require(layout.GetProperty("entries").GetArrayLength() == artifact.GetProperty("entries").GetArrayLength(), "Standalone inventory differs from reopened creation evidence.");
        Require(HashFile(Child(package, "npcmanager-package.json")) == sourceHash, "Runtime archive changed source manifest.");
    }

    private static async Task RefuseRuntimeZipAsync(WorkspacePath root, string filename, (string Path, string Content)[] entries)
    {
        var path = Child(root, filename);
        using (var zip = ZipFile.Open(path.Value, ZipArchiveMode.Create))
            foreach (var item in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(item.Path).Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        var result = await RunCliAsync(root, "package", "verify", "--archive", path.Value, "--payload", "runtime-only", "--json");
        bool accepted = result.Root.TryGetProperty("verified", out var value) && value.GetBoolean();
        Require(result.ExitCode != 0 && !accepted, "Unsafe runtime ZIP was accepted: " + filename);
    }
}
