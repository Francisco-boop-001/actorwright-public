using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Rendering;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static void TestEmbeddedBlenderScriptBundle()
    {
        ImmutableDictionary<string, string[]> expected =
            new Dictionary<string, string[]>
            {
                ["export_facegeom_nif"] = ["nif_geometry_readback"],
                ["export_preview_nif"] = ["hair_zap", "nif_geometry_readback"],
                ["hair_zap"] = [],
                ["render_npc_preview_bundle"] = [],
                ["render_preview_scene"] = ["hair_zap"]
            }.ToImmutableDictionary(StringComparer.Ordinal);
        string root = FindActorwrightRoot();

        foreach ((string id, string[] helperIds) in expected)
        {
            EmbeddedBlenderScript script = EmbeddedBlenderScriptBundle.Load(id);
            byte[] source = File.ReadAllBytes(Path.Combine(
                root, "runtime", "rendering", $"{id}.py"));
            byte[] toolSource = File.ReadAllBytes(Path.Combine(
                root, "tools", "rendering", $"{id}.py"));
            Assert(
                script.Id == id &&
                script.EntryFileName == $"{id}.py" &&
                script.SourceBytes.SequenceEqual(source) &&
                source.SequenceEqual(toolSource) &&
                Convert.ToHexString(SHA256.HashData(source)).Equals(
                    script.Sha256.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                script.HelperModules.Keys.Order(StringComparer.Ordinal)
                    .SequenceEqual(helperIds.Order(StringComparer.Ordinal)),
                $"Embedded Blender script bundle drifted for {id}.");
            foreach (string helperId in helperIds)
            {
                byte[] helper = File.ReadAllBytes(Path.Combine(
                    root, "runtime", "rendering", $"{helperId}.py"));
                byte[] toolHelper = File.ReadAllBytes(Path.Combine(
                    root, "tools", "rendering", $"{helperId}.py"));
                Assert(
                    script.HelperModules[helperId].SequenceEqual(helper) &&
                    helper.SequenceEqual(toolHelper),
                    $"Embedded helper bytes drifted for {id}/{helperId}.");
            }
        }

        EmbeddedBlenderInvocation invocation = EmbeddedBlenderInvocationFactory.Create(
            "render_preview_scene",
            ["--request", "request.json", "--status", "status.json"]);
        Assert(
            invocation.Arguments.Length == 8 &&
            invocation.Arguments[0] == "--background" &&
            invocation.Arguments[1] == "--python-expr" &&
            invocation.Arguments[2].Contains("json.loads(sys.stdin.read())", StringComparison.Ordinal) &&
            invocation.Arguments[3] == "--" &&
            invocation.Arguments[4] == "--request" &&
            invocation.Arguments[5] == "request.json" &&
            invocation.Arguments[6] == "--status" &&
            invocation.Arguments[7] == "status.json" &&
            !invocation.Arguments.Any(argument => argument.EndsWith(".py", StringComparison.OrdinalIgnoreCase)),
            "Embedded Blender invocation did not use the closed stdin bootstrap contract.");
        using JsonDocument payload = JsonDocument.Parse(invocation.StandardInput);
        Assert(
            payload.RootElement.GetProperty("entry").GetString() == "render_preview_scene" &&
            payload.RootElement.GetProperty("source").GetString() is { Length: > 0 } &&
            payload.RootElement.GetProperty("modules").EnumerateObject()
                .Select(property => property.Name)
                .SequenceEqual(["hair_zap"]),
            "Embedded Blender invocation payload omitted its exact entry/helper sources.");

        bool refused = false;
        try
        {
            _ = EmbeddedBlenderScriptBundle.Load("unknown");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            refused = exception.ParamName == "id";
        }
        Assert(refused, "Unknown embedded Blender script IDs were not refused.");
    }

    private static string FindActorwrightRoot()
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Actorwright repository root was not found.");
    }
}
