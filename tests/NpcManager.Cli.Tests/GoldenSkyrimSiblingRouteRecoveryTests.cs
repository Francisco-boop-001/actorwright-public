using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunDiagnosticInputEvidenceAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory, "artifacts", "task22", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        Directory.CreateDirectory(Child(root, "Data").Value);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Discovery failed.");
        foreach (string command in new[] { "workspace preflight", "body overlay bake", "face tint patch" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0, "Schema discovery failed.");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(new Diagnostic("legacy", DiagnosticSeverity.Error, "unchanged"), jsonOptions));
        Require(legacy.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(["code", "severity", "message"]),
            "An absent recovery changed legacy diagnostic JSON.");
        using var priorRecovery = JsonDocument.Parse(JsonSerializer.Serialize(new DiagnosticRecovery(RecoveryAction.CorrectInput, null, null, "unchanged", false), jsonOptions));
        Require(!priorRecovery.RootElement.TryGetProperty("alternativeCommand", out _), "An absent alternative changed recovery JSON.");
        var failures = new List<string>();
        var loadOrder = Child(root, "empty-load-order.json");
        File.WriteAllText(loadOrder.Value, "[]");
        var workspace = await RunCliAsync(root, "workspace", "preflight", "--game", "skyrimse", "--workspace-root", root.Value,
            "--data-root", Child(root, "Data").Value, "--load-order", loadOrder.Value, "--output-root", Child(root, "new-output").Value, "--json");
        if (workspace.ExitCode == 0 || workspace.Root.GetProperty("isAccepted").GetBoolean() ||
            !workspace.Root.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("code").GetString() == "load-order-root-invalid" &&
                d.GetProperty("message").GetString()!.Contains("empty-load-order.json", StringComparison.Ordinal)))
            failures.Add("Empty copied workspace refusal needs its exact load-order reason: " + workspace.Root);
        var layers = Child(root, "layers.json");
        double[] pixel = [1d, 1d, 1d, 1d];
        foreach (bool source in new[] { true, false })
        {
            File.WriteAllText(layers.Value, JsonSerializer.Serialize(new { @base = new { width = 1, height = 1, pixels = pixel },
                layers = new[] { new { source = source ? "unsupported-source" : "SkeeMask", blend = source ? "Normal" : "unsupported-blend", color = pixel } } }));
            var result = await RunCliAsync(root, "body", "overlay", "bake", "--game", "skyrimse", "--layers", "@" + layers.Value,
                "--output", Child(root, "overlay.dds").Value, "--json");
            string message = result.Root.ToString();
            string[] admitted = source ? Enum.GetNames<SkyrimOverlaySource>() : Enum.GetNames<SkyrimOverlayBlendMode>();
            if (result.ExitCode == 0 || !message.Contains(source ? "layers[0].source" : "layers[0].blend", StringComparison.Ordinal) ||
                admitted.Any(name => !message.Contains(name, StringComparison.Ordinal)))
                failures.Add("Enum refusal must retain field, rejected value and admitted set: " + message);
        }
        var tintOutput = Child(root, "tint-output.esp");
        foreach (string invalidPath in new[] { "@relative.json", "@" })
        {
            var tint = await RunCliAsync(root, "face", "tint", "patch", "--game", "skyrimse",
                "--plugin", Child(root, "source.esp").Value, "--output", tintOutput.Value,
                "--npc", "00000800", "--layers", invalidPath, "--json");
            Require(tint.ExitCode == 2 && tint.Root.GetProperty("code").GetString() == "usage-error" &&
                    !string.IsNullOrEmpty(tint.Root.GetProperty("message").GetString()) && !File.Exists(tintOutput.Value),
                "Invalid @layers path must return typed usage JSON before opening a plugin: " + tint.Root + tint.StdErr);
        }
        Console.WriteLine("Diagnostic input evidence: " + root);
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static async Task AssertCstyRecoveryAsync(WorkspacePath root, SkyrimNpcFinishCoreRequest request)
    {
        var path = Child(root, "finish-no-seed-request.json");
        var proposal = Child(root, "finish-no-seed-proposal.json");
        var noSeed = request with { CombatPolicy = request.CombatPolicy! with { SeedLocalStyle = false } };
        File.WriteAllBytes(path.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(noSeed, root));
        var result = await RunCliAsync(root, "npc", "finish", "analyze", "--json",
            "--request", path.Value, "--request-sha256", HashFile(path), "--proposal", proposal.Value);
        Require(result.ExitCode != 0, "Master-owned CSTY without seed policy must refuse.");
        var diagnostic = result.Root.GetProperty("diagnostics").EnumerateArray()
            .FirstOrDefault(d => d.GetProperty("code").GetString() == "finish-core-combat-seed-required" && d.TryGetProperty("recovery", out _));
        Require(diagnostic.ValueKind == JsonValueKind.Object && diagnostic.TryGetProperty("recovery", out _),
            "Master-owned CSTY refusal must carry precise supported seed recovery: " + result.Root);
        var recovery = diagnostic.GetProperty("recovery");
        Require(recovery.GetProperty("alternativeCommand").GetString() == "npc finish analyze" &&
            recovery.GetProperty("action").GetString() == "reanalyze" &&
            recovery.GetProperty("option").GetString() == "request" &&
            !recovery.GetProperty("retryUnchangedSafe").GetBoolean() &&
            recovery.GetProperty("constraint").GetString()!.Contains("combatPolicy.seedLocalStyle=true", StringComparison.Ordinal) &&
            recovery.GetProperty("constraint").GetString()!.Contains("hash", StringComparison.OrdinalIgnoreCase),
            "Seed recovery must identify the request change and binding prerequisite.");
        Require(!Directory.Exists((request.Output.Root ?? throw new InvalidOperationException()).Value) &&
            new Sha256Hash(HashFile(request.Source.PluginPath ?? throw new InvalidOperationException())) == request.Source.PluginSha256 &&
            SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(request.Source.PackageRoot ?? throw new InvalidOperationException()) == request.Source.PackageTreeSha256,
            "Recovery refusal changed the source package or created mutation output.");
        Console.WriteLine("PASS precise master CSTY recovery; following original true-policy request proves the route.");
    }
}
