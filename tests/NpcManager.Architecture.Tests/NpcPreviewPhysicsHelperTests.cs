using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Architecture.Tests;

internal static partial class NpcVisualPreviewProcessEvidenceTests
{
    public static async Task RunPhysicsHelpersAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "task34", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WorkspacePath lab = new(root);
        WorkspacePath blender = WriteFile(root, "tools/blender.exe", "controlled Blender fixture");
        WorkspacePath texconv = WriteFile(root, "tools/texconv.exe", "controlled Texconv fixture");
        WorkspacePath profile = new(Path.Combine(root, "profile"));
        Directory.CreateDirectory(profile.Value);
        ApplicationResourcePath manifest = new(WriteFile(root, "profile-manifest.json", "{}").Value);
        Require(EmbeddedBlenderScriptBundle.Load(NpcVisualPreviewRendererAuthority.ScriptId).Sha256 == NpcVisualPreviewRendererAuthority.ScriptSha256);
        for (int mode = 0; mode < 4; mode++)
        {
            WorkspacePath output = CreateExposureOutput(root, "case-" + mode, out var source);
            source = source with { Assets = source.Assets.Add(CreateExposureAsset(NpcVisualAssetRole.Outfit, output.Value, "meshes/outfit.nif")) };
            var renderer = new BlenderNpcVisualPreviewRenderer(blender, profile, manifest, Hash(manifest.Value),
                NpcVisualPreviewRendererAuthority.ScriptId, texconv, new KOnlyWorkspacePolicy(lab, new WorkspacePath("F:/ExampleGame")),
                lab, Hash(blender.Value), NpcVisualPreviewRendererAuthority.ScriptSha256, Hash(texconv.Value), new PhysicsHelperStatusRunner(mode));
            var result = await renderer.RenderAsync(new("npc-preview-scene/2", source, output, new NpcVisualPreviewOptions()), CancellationToken.None);
            Console.WriteLine($"Helper status case {mode}: rendered={result.Rendered}; " + string.Join(';', result.Diagnostics.Select(d => d.Code)));
            if (mode == 3)
                Require(!result.Rendered && result.Diagnostics.Any(d => d.Code == "npc-preview-material-route-omitted-invalid"));
            else if (mode != 1)
                Require(!result.Rendered && result.Diagnostics.Any(d => d.Code == "npc-preview-material-route-incomplete" && d.Severity == DiagnosticSeverity.Error));
            else
                Require(result.Rendered && result.Diagnostics.Any(d => d.Code == "npc-preview-material-route-omitted" &&
                    d.Severity != DiagnosticSeverity.Error && d.Message.Contains("VirtualGround", StringComparison.Ordinal) &&
                    d.Message.Contains("meshes/outfit.nif", StringComparison.Ordinal)) &&
                    !result.Diagnostics.Any(d => d.Code == "npc-preview-material-route-incomplete"));
        }
        Console.WriteLine("PASS physics helper advisory and ordinary material refusal: " + root);
    }

    private sealed class PhysicsHelperStatusRunner(int mode) : INpcVisualPreviewProcessRunner
    {
        public ValueTask<NpcVisualPreviewProcessResult> RunAsync(WorkspacePath executable,
            ImmutableArray<string> arguments, WorkspacePath? blenderProfile, string? standardInput,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = arguments[arguments.IndexOf("--status") + 1];
            WriteExposureStatus(path, ExposureMeasurementMode.FiniteBelowFloor);
            var status = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            status["tints"]!["unresolvedTextureBindingCount"] = mode is 1 or 3 ? 0 : 1;
            if (mode != 0)
                status["omittedMaterialRoutes"] = new JsonArray(new JsonObject
                {
                    ["role"] = "Outfit", ["assetPath"] = mode == 3 ? "unbound.nif" : "meshes/outfit.nif", ["objectName"] = "VirtualGround"
                });
            File.WriteAllText(path, status.ToJsonString());
            return ValueTask.FromResult(new NpcVisualPreviewProcessResult(0,
                new NpcVisualPreviewCapturedStream("controlled helper status", false),
                new NpcVisualPreviewCapturedStream("", false), []));
        }
    }
}
