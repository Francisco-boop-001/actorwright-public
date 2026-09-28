using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimFaceGenSidecarOverlays()
    {
        string ownedRoot = Path.Combine(AppContext.BaseDirectory,
            "facegen-sidecar-overlay-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(ownedRoot, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Base.bssliders"),
                """
                {
                  "version": 11,
                  "plugin": "Base.esp",
                  "npcs": {
                    "Base.esp|000800": {
                      "editorId": "SidecarNpc",
                      "sseCustomMorphs": [{ "name": "BaseMorph", "value": 0.25 }],
                      "sseSculpt": [{ "index": 2, "dx": 0.1, "dy": 0.2, "dz": 0.3 }],
                      "sseTintTextures": [{ "index": 1, "texture": "textures/base.dds" }]
                    },
                    "Base.esp|000801": {
                      "editorId": "LegacySculptNpc",
                      "sseSculpt": [{ "index": 4, "dx": 0.4, "dy": 0.0, "dz": 0.0 }]
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Patch.bssliders"),
                """
                {
                  "version": 11,
                  "plugin": "Patch.esp",
                  "npcs": {
                    "Base.esp|000800": {
                      "editorId": "SidecarNpc",
                      "sseCustomMorphs": [{ "name": "PatchMorph", "value": -0.5 }],
                      "sseSculptParts": [{
                        "host": "meshes/actors/character/character assets/femaleheadchargen.tri",
                        "verts": [{ "index": 3, "dx": -0.1, "dy": 0.0, "dz": 0.1 }]
                      }],
                      "sseTintTextures": [{ "index": 2, "texture": "textures/patch.dds" }]
                    },
                    "Base.esp|000801": {
                      "editorId": "LegacySculptNpc",
                      "sseSculptParts": [{
                        "host": "meshes/actors/character/character assets/femaleheadchargen.tri",
                        "verts": []
                      }]
                    }
                  }
                }
                """);

            var root = new WorkspacePath("K:\\ExampleWorkspace");
            var basePlugin = new PluginName("Base.esp");
            var patchPlugin = new PluginName("Patch.esp");
            var target = new FaceGenBakeTarget(
                new FormId(0x800), basePlugin, patchPlugin,
                [basePlugin, patchPlugin], "SidecarNpc", "Sidecar NPC",
                NpcSex.Female,
                new FormReference(basePlugin, new FormId(0x19)),
                [new FormReference(basePlugin, new FormId(0x100))], 50F);
            var legacyTarget = target with
            {
                FormId = new FormId(0x801),
                EditorId = "LegacySculptNpc",
                Name = "Legacy Sculpt NPC"
            };
            var policy = new KOnlyWorkspacePolicy(root,
                new WorkspacePath("F:\\ExampleGame"));
            var loader = new SkyrimFaceGenSidecarOverlayLoader(
                new BodySidecarInspectionService(policy, root));

            SkyrimFaceGenSidecarOverlayLoadResult result = await loader.LoadAsync(
                new SkyrimFaceGenSidecarOverlayLoadRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot), [basePlugin, patchPlugin],
                    [target, legacyTarget]),
                CancellationToken.None);

            Assert(result.Accepted && result.Authorities.Length == 2 &&
                   result.Overlays.Length == 2,
                "Typed load-order sidecars were not admitted as two target overlays.");
            SkyrimFaceGenSidecarOverlay overlay = result.Overlays.Single(item =>
                item.FormId == target.FormId);
            Assert(overlay.CustomMorphs is [{ Name: "PatchMorph", Value: -0.5F }] &&
                   overlay.LegacyHeadSculpt.Length == 1 &&
                   overlay.SculptParts is [{ Host: "meshes/actors/character/character assets/femaleheadchargen.tri" }] &&
                   overlay.TintTextureOverrides is [{ Index: 2 }] &&
                   overlay.SourceAuthorities.Length == 2,
                "Sidecar overlay did not preserve field-wise last-loaded-wins semantics.");
            SkyrimFaceGenSidecarOverlay legacyOverlay = result.Overlays.Single(item =>
                item.FormId == legacyTarget.FormId);
            Assert(legacyOverlay.LegacyHeadSculpt is [{ Index: 4 }] &&
                   legacyOverlay.SculptParts.IsEmpty &&
                   legacyOverlay.SourceAuthorities is [{ Plugin.Value: "Base.esp" }],
                "An empty per-shape sculpt block incorrectly suppressed the upstream legacy-sculpt fallback.");

            var badPlugin = new PluginName("Bad.esp");
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Bad.bssliders"),
                """
                {
                  "version": 11,
                  "plugin": "Bad.esp",
                  "npcs": {
                    "Bad.esp|000800": {
                      "sseTintTextures": [
                        { "index": 1, "texture": "meshes/not-a-tint.nif" }
                      ]
                    }
                  }
                }
                """);
            FaceGenBakeTarget badTarget = target with
            {
                OriginatingPlugin = badPlugin,
                WinningPlugin = badPlugin,
                OverrideChain = [badPlugin]
            };
            SkyrimFaceGenSidecarOverlayLoadResult bad = await loader.LoadAsync(
                new SkyrimFaceGenSidecarOverlayLoadRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(dataRoot), [badPlugin], [badTarget]),
                CancellationToken.None);
            Assert(!bad.Accepted && bad.Overlays.IsEmpty &&
                   bad.Diagnostics.Any(item =>
                       item.Code == "facegen-sidecar-tint-path"),
                "A non-texture sidecar tint route escaped the pre-write loader boundary.");
        }
        finally
        {
            DeleteOwnedDirectory(ownedRoot);
        }
    }
}
