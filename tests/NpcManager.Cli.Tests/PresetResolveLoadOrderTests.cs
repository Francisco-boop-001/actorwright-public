using System.Buffers.Binary;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunPresetResolveLoadOrderAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "task33", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        foreach (string[] args in new[] {
            new[] { "version", "--protocol", "2", "--json" },
            new[] { "capabilities", "--protocol", "2", "--json" },
            new[] { "schema", "export", "--protocol", "2", "--command", "preset resolve", "--json" } })
        {
            var discovery = await RunCliAsync(root, args);
            Require(discovery.ExitCode == 0, "Preset resolve discovery failed: " + discovery.Root);
        }
        var order = Child(root, "load-order.json");
        void WriteOrder(object rows) => File.WriteAllText(order.Value, JsonSerializer.Serialize(new
        { schemaVersion = 1, edition = "skyrimse", plugins = rows }));
        WriteOrder(new[] { new { name = "Base.esm", order = 0, enabled = true },
            new { name = "Full.esp", order = 1, enabled = true } });
        var resolved = await RunCliAsync(root, "preset", "resolve", "--identifier", "Full.esp|0x800", "--load-order", order.Value, "--json");
        Require(resolved.ExitCode == 0 && resolved.Root.GetProperty("resolvedFormId").GetString() == "0x01000800",
            "Schema1 load order did not resolve through the actual CLI: " + resolved.Root + resolved.StdErr);
        File.WriteAllText(order.Value, "{\"Full.esp\":4}");
        resolved = await RunCliAsync(root, "preset", "resolve", "--identifier", "Full.esp|0x800", "--load-order", order.Value, "--json");
        Require(resolved.ExitCode == 0 && resolved.Root.GetProperty("resolvedFormId").GetString() == "0x04000800", "Legacy map changed.");
        foreach (string invalid in new[] { "{\"Full.esp\":[]}", "{\"Full.esp\":1,\"Other.esp\":[]}", "{\"Full.esp\":1,\"Full.esp\":2}",
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Full.esp\",\"order\":0,\"enabled\":false}]}",
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Full.esp\",\"order\":\"bad\",\"enabled\":true}]}" })
        {
            File.WriteAllText(order.Value, invalid);
            var refused = await RunCliAsync(root, "preset", "resolve", "--identifier", "Full.esp|0x800", "--load-order", order.Value, "--json");
            Require(refused.ExitCode != 0 && refused.Root.GetProperty("resolvedFormId").ValueKind == JsonValueKind.Null &&
                refused.Root.GetProperty("diagnostics").GetArrayLength() > 0 &&
                !refused.StdErr.Contains("Unhandled exception", StringComparison.Ordinal), "Malformed load order escaped typed refusal: " + refused.Root);
        }
        var data = Child(root, "Data"); Directory.CreateDirectory(data.Value);
        foreach (string name in new[] { "Base.esm", "LightA.esp", "LightB.esp", "Full.esp" })
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            string path = Child(data, name).Value;
            WriteConsolidationFixture(mod, path);
            if (name.StartsWith("Light", StringComparison.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(path);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) | 0x200);
                File.WriteAllBytes(path, bytes);
            }
        }
        WriteOrder(new[] { new { name = "Base.esm", order = 0, enabled = true },
            new { name = "LightA.esp", order = 1, enabled = true }, new { name = "LightB.esp", order = 2, enabled = true },
            new { name = "Full.esp", order = 3, enabled = true }, new { name = "Absent.esp", order = 4, enabled = false } });
        foreach ((string identifier, string expected) in new[] { ("LightB.esp|0x12ABC", "0xFE001ABC"), ("Full.esp|0x800", "0x01000800") })
        {
            resolved = await RunCliAsync(root, "preset", "resolve", "--identifier", identifier, "--load-order", order.Value, "--data-root", data.Value, "--json");
            Require(resolved.ExitCode == 0 && resolved.Root.GetProperty("resolvedFormId").GetString() == expected,
                "Copied TES4 full/light indexes or mask differ: " + resolved.Root);
        }
        File.WriteAllBytes(Child(data, "LightA.esp").Value, "TES4"u8.ToArray());
        var malformed = await RunCliAsync(root, "preset", "resolve", "--identifier", "LightB.esp|0x800", "--load-order", order.Value, "--data-root", data.Value, "--json");
        Require(malformed.ExitCode != 0 && malformed.Root.GetProperty("diagnostics").GetArrayLength() > 0,
            "Malformed copied header was accepted.");
        Console.WriteLine("Preset schema1/legacy and copied TES4 light resolution verified: " + root.Value);
    }
}
