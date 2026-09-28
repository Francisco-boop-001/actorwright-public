using System.Buffers.Binary;
using System.Text.Json;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly string[] GoldenInheritedDefaults =
        ["class", "combatStyle", "aidt", "packages", "defaultPackageList", "voice"];
    private const string GoldenAidt = """{"aggression":"aggressive","confidence":"brave","assistance":"helpsFriendsAndAllies","morality":"noCrime","energy":73}""";

    internal static async Task RunInheritedDefaultsAsync()
    {
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var root = new WorkspacePath(Path.Combine(repository, "artifacts", "test-work", $"inherited-defaults-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root.Value);
        Console.WriteLine("Task37 fixture: " + root.Value);
        MaterializeProbeFixtures(repository, Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json"),
            "npc create-from-jslot", root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        PrepareGoldenPresetFixture(preset);
        var request = Child(root, "npc-preflight", "request.json");
        CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Task37 binary discovery failed.");
        foreach (string command in new[] { "npc create-from-jslot", "npc inspect", "npc patch", "plugin verify" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0,
                "Task37 schema discovery failed: " + command);
        var created = await RunCliAsync(root, "npc", "create-from-jslot", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request), "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value);
        Require(created.ExitCode == 0, "Task37 real create failed: " + created.Root + created.StdErr);
        var data = Child(root, "planned-npc-output", "Data");
        var source = Child(data, "PackagedNpc.esp");
        string sourceHash = HashFile(source);
        string formId;
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(source.Value, SkyrimRelease.SkyrimSE))
        {
            INpcGetter npc = mod.Npcs.Single();
            formId = new FormId(npc.FormKey.ID).ToString();
            Require(npc.Class.FormKey.ID == 0x13181 && npc.CombatStyle.FormKey.ID == 0x3BE1D &&
                    npc.Voice.FormKey.ID == 0x13ADC && npc.Packages.Count == 0 && npc.AIData?.EnergyLevel == 0,
                "Task37 copied golden defaults drifted before the audit assertion.");
        }
        var inspectionData = Child(root, "inspection", "Data");
        Directory.CreateDirectory(inspectionData.Value);
        foreach (string master in new[] { "Skyrim.esm", "ActorwrightBlankNpcProvider.esp" })
            File.Copy(Child(root, "Data", master).Value, Child(inspectionData, master).Value);
        File.Copy(source.Value, Child(inspectionData, "PackagedNpc.esp").Value);
        var inspected = await RunCliAsync(root, "npc", "inspect", "--edition", "skyrimse", "--data-root", inspectionData.Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp,PackagedNpc.esp", "--npc", formId,
            "--search", "ActorwrightResumptionNpc", "--json");
        Require(inspected.ExitCode == 0, "Task37 real inspect failed: " + inspected.Root + inspected.StdErr);
        var failures = new List<string>();
        CheckDefaults(created.Root, "create", failures);
        CheckDefaults(inspected.Root, "inspect", failures);
        var output = Child(root, "AidtPatched.esp");
        var proposal = Child(root, "aidt-proposal.json");
        var patched = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", output.Value, "--form-id", formId, "--expected-sha256", sourceHash,
            "--aidt", GoldenAidt, "--proposal", proposal.Value, "--apply", "--json");
        if (patched.ExitCode != 0 || !File.Exists(output.Value))
            failures.Add("AIDT patch did not apply: " + patched.Root + patched.StdErr);
        else
        {
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(proposal.Value));
            Require(persisted.RootElement.GetProperty("aidt").GetProperty("energy").GetInt32() == 73,
                "The persisted proposal lost explicit AIDT intent.");
            using var actual = SkyrimMod.CreateFromBinaryOverlay(output.Value, SkyrimRelease.SkyrimSE);
            var ai = actual.Npcs.Single().AIData ?? throw new InvalidOperationException("Patched NPC has no AIDT.");
            Require(ai.Aggression == Aggression.Aggressive && ai.Confidence == Confidence.Brave &&
                    ai.Assistance == Assistance.HelpsFriendsAndAllies && ai.Responsibility == Responsibility.NoCrime && ai.EnergyLevel == 73,
                "Independent typed readback did not find the requested AIDT values.");
            var verified = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
                "--output-plugin", output.Value, "--form-id", formId, "--aidt", GoldenAidt, "--json");
            Require(verified.ExitCode == 0 && verified.Root.GetProperty("isValid").GetBoolean(),
                "Independent plugin verify failed: " + verified.Root + verified.StdErr);
            var patchedData = Child(root, "patched-inspection", "Data");
            Directory.CreateDirectory(patchedData.Value);
            foreach (string master in new[] { "Skyrim.esm", "ActorwrightBlankNpcProvider.esp" })
                File.Copy(Child(root, "Data", master).Value, Child(patchedData, master).Value);
            File.Copy(output.Value, Child(patchedData, "AidtPatched.esp").Value);
            var inspectedPatch = await RunCliAsync(root, "npc", "inspect", "--edition", "skyrimse", "--data-root", patchedData.Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp,AidtPatched.esp", "--npc", formId,
                "--search", "ActorwrightResumptionNpc", "--json");
            Require(inspectedPatch.ExitCode == 0, "Patched NPC inspection failed.");
            CheckDefaults(inspectedPatch.Root, "patched inspect", failures, "aidt");
            await AssertAidtControlsAsync(root, source, output, formId);
        }
        Require(HashFile(source) == sourceHash, "Task37 mutated the source plugin.");
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS inherited-defaults-aidt");
    }

    private static void CheckDefaults(JsonElement response, string route, List<string> failures, string? omitted = null)
    {
        if (!response.TryGetProperty("inheritedDefaults", out var fields))
            failures.Add(route + " omitted inheritedDefaults.");
        else if (!fields.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal)
                     .SequenceEqual(GoldenInheritedDefaults.Where(field => field != omitted).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            failures.Add(route + " did not report the exact authenticated fixture-equal subset: " + fields);
    }

    private static async Task AssertAidtControlsAsync(WorkspacePath root, WorkspacePath source, WorkspacePath patched, string formId)
    {
        byte[] before = File.ReadAllBytes(source.Value), after = File.ReadAllBytes(patched.Value);
        int beforeAidt = SingleAidtOffset(before), afterAidt = SingleAidtOffset(after);
        Require(before.AsSpan(beforeAidt + 4, 1).SequenceEqual(after.AsSpan(afterAidt + 4, 1)) &&
                before.AsSpan(beforeAidt + 6, 14).SequenceEqual(after.AsSpan(afterAidt + 6, 14)),
            "AIDT patch changed Mood or untouched flags/distances.");
        var tampered = Child(root, "MoodTampered.esp");
        after[afterAidt + 4] ^= 1;
        File.WriteAllBytes(tampered.Value, after);
        var refusedTamper = await RunCliAsync(root, "plugin", "verify", "--edition", "skyrimse", "--source-plugin", source.Value,
            "--output-plugin", tampered.Value, "--form-id", formId, "--aidt", GoldenAidt, "--json");
        Require(refusedTamper.ExitCode != 0 && refusedTamper.Root.ToString().Contains("aidt-preserved-field-drift", StringComparison.Ordinal),
            "Independent verifier admitted collateral AIDT Mood drift: " + refusedTamper.Root + refusedTamper.StdErr);

        var follower = Child(root, "Follower.esp");
        var makeFollower = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", follower.Value, "--form-id", formId, "--expected-sha256", HashFile(source),
            "--aidt", GoldenAidt, "--add-faction", "Skyrim.esm|0x0005C84D=0", "--apply", "--json");
        Require(makeFollower.ExitCode == 0, "Product follower fixture patch failed: " + makeFollower.Root + makeFollower.StdErr);
        foreach (bool combined in new[] { false, true })
        {
            WorkspacePath input = combined ? source : follower;
            var refusedOutput = Child(root, combined ? "CombinedRefused.esp" : "FollowerRefused.esp");
            var args = new List<string> { "npc", "patch", "--edition", "skyrimse", "--input-plugin", input.Value,
                "--output", refusedOutput.Value, "--form-id", formId, "--expected-sha256", HashFile(input),
                "--aidt", """{"assistance":"helpsNobody","energy":74}""", "--apply", "--json" };
            if (combined) { args.Add("--add-faction"); args.Add("Skyrim.esm|0x0005C84E=-1"); }
            var refusal = await RunCliAsync(root, args.ToArray());
            Require(refusal.ExitCode != 0 && !File.Exists(refusedOutput.Value) &&
                    refusal.Root.ToString().Contains("aidt-follower-assistance", StringComparison.Ordinal),
                "Effective follower faction state admitted HelpsNobody: " + refusal.Root + refusal.StdErr);
        }
        var fileIntent = Child(root, "nonfollower-aidt.json");
        File.WriteAllText(fileIntent.Value, """{"assistance":"helpsNobody","energy":74}""");
        var nonFollower = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", Child(root, "NonFollower.esp").Value, "--form-id", formId, "--expected-sha256", HashFile(source),
            "--aidt", "@" + fileIntent.Value, "--apply", "--json");
        Require(nonFollower.ExitCode == 0, "HelpsNobody was incorrectly forbidden for a non-follower.");
        var stalePath = Child(root, "StaleAidt.esp");
        var stale = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", stalePath.Value, "--form-id", formId, "--expected-sha256", new string('0', 64),
            "--aidt", GoldenAidt, "--apply", "--json");
        Require(stale.ExitCode != 0 && !File.Exists(stalePath.Value) && stale.Root.ToString().Contains("input-hash-mismatch", StringComparison.Ordinal),
            "AIDT bypassed the existing source hash binding.");
        string[] invalid = ["{}", """{"energy":256}""", """{"aggression":"unknown"}""",
            """{"mood":"happy"}""", """{"energy":1,"energy":2}""", """{"energy":null}"""];
        for (int index = 0; index < invalid.Length; index++)
        {
            var badOutput = Child(root, $"InvalidAidt{index}.esp");
            var result = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
                "--output", badOutput.Value, "--form-id", formId, "--expected-sha256", HashFile(source),
                "--aidt", invalid[index], "--apply", "--json");
            Require(result.ExitCode != 0 && !File.Exists(badOutput.Value), "Invalid AIDT intent was published: " + invalid[index]);
        }
        var legacyOutput = Child(root, "LegacyName.esp");
        var legacyProposal = Child(root, "legacy-proposal.json");
        var legacy = await RunCliAsync(root, "npc", "patch", "--edition", "skyrimse", "--input-plugin", source.Value,
            "--output", legacyOutput.Value, "--form-id", formId, "--expected-sha256", HashFile(source),
            "--name", "Reviewed NPC name", "--proposal", legacyProposal.Value, "--apply", "--json");
        Require(legacy.ExitCode == 0, "Omitted AIDT broke the legacy scalar patch.");
        using var legacyDocument = JsonDocument.Parse(File.ReadAllBytes(legacyProposal.Value));
        byte[] legacyBytes = File.ReadAllBytes(legacyOutput.Value);
        Require(!legacyDocument.RootElement.TryGetProperty("aidt", out _) &&
                before.AsSpan(beforeAidt, 20).SequenceEqual(legacyBytes.AsSpan(SingleAidtOffset(legacyBytes), 20)),
            "Omitted AIDT changed legacy proposal shape or AIDT bytes.");
    }

    private static int SingleAidtOffset(byte[] bytes)
    {
        int offset = bytes.AsSpan().IndexOf("AIDT"u8);
        Require(offset >= 0 && bytes.AsSpan(offset + 4).IndexOf("AIDT"u8) < 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2)) == 20,
            "Synthetic single-NPC AIDT layout drifted.");
        return offset + 6;
    }
}
