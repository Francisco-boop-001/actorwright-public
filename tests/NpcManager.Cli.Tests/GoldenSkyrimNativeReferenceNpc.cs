using System.Text.Json;
using System.Collections.Immutable;
using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.FaceGen;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static async Task CompleteNativeNpcAsync(WorkspacePath repository, WorkspacePath root,
        ReferencePresetWriteRequest authoring, Sha256Hash accepted, VerifiedReferencePreset verified)
    {
        foreach (string command in new[] { "version", "capabilities" })
        {
            var discovery = await RunCliAsync(root, command, "--protocol", "2", "--json");
            File.WriteAllText(Child(root, command + ".json").Value, discovery.Root.GetRawText());
            Require(discovery.ExitCode == 0, "Exact native-workflow CLI discovery failed: " + command);
        }
        foreach (string command in new[] { "npc create-from-reference", "preview npc", "workspace preflight" })
        {
            var schema = await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command);
            File.WriteAllText(Child(root, "schema-" + command.Replace(' ', '-') + ".json").Value, schema.Root.GetRawText());
            Require(schema.ExitCode == 0, "Native-workflow CLI schema discovery failed: " + command);
        }
        CopyRendererAuthorities(repository.Value, root);
        var sourceHashes = Directory.EnumerateFiles(Child(root, "Data").Value, "*", SearchOption.AllDirectories)
            .Concat(new[] { Child(root, "portrait.jpg").Value, Child(root, "npc-preflight", "fixture.jslot").Value,
                authoring.InferenceProposalPath.Value, authoring.ReviewedDesignPath.Value, authoring.ResourceSnapshotPath.Value })
            .ToDictionary(path => path, path => HashFile(new WorkspacePath(path)), StringComparer.OrdinalIgnoreCase);
        var request = Child(root, "npc-preflight", "request.json");
        var result = await RunCliAsync(root, PreviewChildDeadline,
            "npc", "create-from-reference", "--json", "--apply",
            "--proposal", authoring.InferenceProposalPath.Value, "--proposal-sha256", authoring.InferenceProposalSha256.Value,
            "--review", authoring.ReviewedDesignPath.Value, "--review-sha256", authoring.ReviewedDesignSha256.Value,
            "--resource", authoring.ResourceSnapshotPath.Value, "--resource-sha256", authoring.ResourceSnapshotSha256.Value,
            "--accepted-proposal-sha256", accepted.Value,
            "--request", request.Value, "--request-sha256", HashFile(request),
            "--data-root", Child(root, "Data").Value, "--plugins", string.Join(',', NativeReferencePlugins),
            "--transaction-root", Child(root, "npc-transaction").Value);
        File.WriteAllText(Child(root, "npc-result.json").Value, result.Root.GetRawText());
        File.WriteAllText(Child(root, "npc-stderr.log").Value, result.StdErr);
        Require(result.ExitCode == 0, "Actual native reference NPC materialization: " + result.Root + result.StdErr);
        Require(new Sha256Hash(HashFile(verified.PresetPath)) == verified.PresetSha256,
            "NPC materialization changed the independently applied preset.");
        Console.WriteLine("NPC materialization admitted: " + Child(root, "planned-npc-output").Value);
        var emittedPreset = Child(root, "npc-transaction", "preset", "NativeReference.jslot");
        Require(new Sha256Hash(HashFile(emittedPreset)) == verified.PresetSha256 &&
                result.Root.GetProperty("presetSha256").GetString() == verified.PresetSha256.Value,
            "Actual NPC reference handoff lost the exact solved JSlot hash.");
        var package = Child(root, "planned-npc-output");
        var manifest = Child(package, "npcmanager-package.json");
        var pluginPath = Child(package, "Data", "PackagedNpc.esp");
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath.Value, SkyrimRelease.SkyrimSE);
        var npc = plugin.Npcs.Single();
        var policy = new KOnlyWorkspacePolicy(root, Child(root, "protected"));
        var morph = await new BethesdaSkyrimFaceMorphSnapshotService(policy, root).ReadAsync(
            new SkyrimFaceMorphSnapshotRequest(GameEdition.SkyrimSpecialEdition, pluginPath,
                new Sha256Hash(HashFile(pluginPath)), new FormId(npc.FormKey.ID)), CancellationToken.None);
        using var emittedPresetDocument = JsonDocument.Parse(
            File.ReadAllBytes(emittedPreset.Value));
        float solvedNam9 = emittedPresetDocument.RootElement
            .GetProperty("morphs")
            .GetProperty("default")
            .GetProperty("morphs")[0]
            .GetSingle();
        Require(Math.Abs(solvedNam9) > 0.000001F &&
                morph.Resolved &&
                Math.Abs(morph.Snapshot!.Nam9Sliders[0] - solvedNam9) < 0.000001F,
            "Independent NPC record readback lost the exact solved nonzero NAM9[0].");
        File.WriteAllText(Child(root, "npc-morph-readback.json").Value, JsonSerializer.Serialize(morph, NativeEvidenceJson));
        var faceGeom = Child(package, "Data", "meshes", "actors", "character", "FaceGenData", "FaceGeom",
            "PackagedNpc.esp", $"{npc.FormKey.ID:X8}.nif");
        var geometry = await new BethesdaNifGeometryReadbackService().ReadAsync(
            new NifGeometryReadbackRequest(GameEdition.SkyrimSpecialEdition, faceGeom,
                new Sha256Hash(HashFile(faceGeom))), CancellationToken.None);
        Require(geometry.Accepted, "Actual generated FaceGeom could not be independently reopened.");
        var triPath = Child(root, "Data", "meshes", "Actors", "Character", "Actorwright", "Head.tri");
        var tri = new SseTriHeadReader().Read(new SseTriHeadReadRequest(
            new AssetPath("meshes/Actors/Character/Actorwright/Head.tri"), new Sha256Hash(HashFile(triPath)),
            File.ReadAllBytes(triPath.Value).ToImmutableArray()));
        Require(tri.Accepted, "Native resource TRI failed independent downstream readback.");
        var head = geometry.Document!.Shapes.Single(s => s.VertexCount == tri.Document!.VertexCount);
        byte[] outputNif = File.ReadAllBytes(faceGeom.Value);
        var deltas = tri.Document!.Morphs.Single(m => m.Name == "NoseLong").Deltas.ToDictionary(d => d.VertexIndex, d => d.Delta);
        int displaced = 0;
        for (int i = 0; i < head.VertexCount; i++)
        {
            int offset = checked((int)head.VertexPayloadOffset + i * head.VertexStride);
            var actual = new Vector3(BitConverter.ToSingle(outputNif, offset), BitConverter.ToSingle(outputNif, offset + 4),
                BitConverter.ToSingle(outputNif, offset + 8));
            var delta = deltas.GetValueOrDefault(i) * morph.Snapshot!.Nam9Sliders[0];
            Require(Vector3.Distance(actual, tri.Document.BaseVertices[i] + delta) < 0.0001F,
                "Generated FaceGeom did not consume the solved native morph at vertex " + i);
            if (delta.LengthSquared() > 0) displaced++;
        }
        Require(displaced > 0, "Generated FaceGeom remained a static baseline.");
        File.WriteAllText(Child(root, "facegeom-morph-readback.json").Value, JsonSerializer.Serialize(new
        { PresetSha256 = verified.PresetSha256, TriSha256 = tri.Document.SourceSha256,
            Morph = morph.Snapshot!.Nam9Sliders[0], DisplacedVertices = displaced, Geometry = geometry.Document }, NativeEvidenceJson));
        Console.WriteLine($"FACEGEOM admitted: {displaced} actual displaced vertices, NAM9[0]={morph.Snapshot.Nam9Sliders[0]}");
        var loadOrder = Child(root, "load-order.json");
        File.WriteAllText(loadOrder.Value,
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Skyrim.esm\",\"order\":0,\"enabled\":true},{\"name\":\"ActorwrightBlankNpcProvider.esp\",\"order\":1,\"enabled\":true}]}");
        var intake = Child(root, "reviewed-game-intake.json");
        var preflight = await RunCliAsync(root, "workspace", "preflight", "--protocol", "2", "--json", "--edition", "skyrimse",
            "--workspace-root", root.Value, "--data-root", Child(root, "Data").Value,
            "--output-root", Child(root, "reserved-output").Value, "--load-order", loadOrder.Value,
            "--intake-output", intake.Value, "--npc-editor-id", "ActorwrightResumptionNpc",
            "--workflow-output", Child(root, "workspace-workflow.json").Value);
        File.WriteAllText(Child(root, "workspace-preflight.json").Value, preflight.Root.GetRawText());
        Require(preflight.ExitCode == 0, "Actual reviewed preview intake: " + preflight.Root);
        var preview = await RunCliAsync(root, PreviewChildDeadline, "preview", "npc", "--json",
            "--intake", intake.Value, "--plugin", "PackagedNpc.esp", "--form", $"0x{npc.FormKey.ID:X8}",
            "--package-manifest", manifest.Value, "--expected-package-sha256", HashFile(manifest),
            "--output-root", Child(root, "npc-preview").Value);
        File.WriteAllText(Child(root, "preview-result.json").Value, preview.Root.GetRawText());
        File.WriteAllText(Child(root, "preview-stderr.log").Value, preview.StdErr);
        Require(preview.ExitCode == 0, "Actual materialized NPC preview: " + preview.Root + preview.StdErr);
        var previewPath = Child(root, "npc-preview", "npc-preview-bundle.json");
        string previewHash = HashFile(previewPath);
        using var previewDocument = new NpcVisualPreviewArtifactReader(root).Load(previewPath, previewHash);
        previewDocument.Revalidate();
        Require(!previewDocument.Value.RuntimeAuthority && previewDocument.Value.Source.Assets.Single(a =>
                a.Role == NpcVisualAssetRole.FaceGeom).Sha256 == geometry.Document.NifSha256,
            "Preview did not consume the independently verified, morphed package FaceGeom.");
        Require(previewDocument.Value.Views.Length == 6 && previewDocument.Artifacts.All(a => a.Size > 0),
            "Actual preview artifacts did not independently reopen.");
        Require(new Sha256Hash(preview.Root.GetProperty("contactSheetSha256").GetString()!) ==
                new Sha256Hash(HashFile(new WorkspacePath(preview.Root.GetProperty("contactSheetPath").GetString()!))),
            "Actual CLI contact-sheet hash differs from the rendered pixels.");
        foreach (var source in sourceHashes)
            Require(HashFile(new WorkspacePath(source.Key)) == source.Value,
                "NPC/preview mutated a source provider asset: " + source.Key);
        Console.WriteLine("PREVIEW admitted: " + previewPath.Value);
    }
}
