using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Architecture.Tests;

internal static class PreviewArtifactFileWriterTests
{
    public static async Task TestPreviewArtifactFileWriterAsync()
    {
        string workspacePath = Path.Combine(
            "K:\\Actorwright",
            "artifacts",
            "phases4-8-work",
            "preview-artifact-file-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        try
        {
            var labRoot = new WorkspacePath(workspacePath);
            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath("K:\\Actorwright"),
                new WorkspacePath("F:\\ExampleGame"));
            string manifestPath = Path.Combine(workspacePath, "scene-manifest.json");
            string scenePath = Path.Combine(workspacePath, "scene.json");
            string rerollPath = Path.Combine(workspacePath, "reroll.json");
            string nifPlanPath = Path.Combine(workspacePath, "npc.nif.plan.json");
            string hash = new('a', 64);
            string manifest =
                "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x900\",\"assets\":[" +
                "{\"category\":\"face\",\"path\":\"meshes/face.nif\",\"provider\":\"fixture\",\"sha256\":\"" + hash + "\"}]," +
                "\"variants\":[" +
                "{\"id\":\"battle\",\"outfit\":\"M2FixtureFO4.esp|0x801\",\"assets\":[\"meshes/face.nif\"]}," +
                "{\"id\":\"casual\",\"outfit\":\"M2FixtureFO4.esp|0x802\",\"assets\":[\"meshes/face.nif\"]}]}";
            await File.WriteAllTextAsync(manifestPath, manifest);

            var sceneService = new PreviewSceneService(policy, labRoot);
            var sceneRequest = new PreviewSceneRequest(
                GameEdition.Fallout4,
                new WorkspacePath(manifestPath),
                new WorkspacePath(scenePath));
            var sceneResult = await sceneService.RenderAsync(sceneRequest, CancellationToken.None);
            var rerollService = new PreviewRerollService(policy, labRoot);
            var rerollRequest = new PreviewRerollRequest(
                GameEdition.Fallout4,
                new WorkspacePath(manifestPath),
                new WorkspacePath(rerollPath),
                new FormId(0x900),
                42);
            var rerollResult = await rerollService.RerollAsync(rerollRequest, CancellationToken.None);
            var nifService = new PreviewNifExportService(policy, labRoot);
            var nifRequest = new PreviewNifExportRequest(
                GameEdition.Fallout4,
                new WorkspacePath(scenePath),
                new WorkspacePath(nifPlanPath));
            var nifResult = await nifService.ExportPlanAsync(nifRequest, CancellationToken.None);

            Assert(sceneResult.Written && sceneResult.Artifact is not null &&
                sceneResult.OutputSha256 is not null && sceneResult.Diagnostics.IsEmpty,
                "Preview scene writer did not complete cleanly.");
            Assert(rerollResult.Written && rerollResult.Artifact is not null &&
                rerollResult.OutputSha256 is not null && rerollResult.Diagnostics.IsEmpty,
                "Preview reroll writer did not complete cleanly.");
            Assert(nifResult.Written && nifResult.Artifact is not null &&
                nifResult.OutputSha256 is not null && nifResult.Diagnostics.IsEmpty,
                "Preview NIF plan writer did not complete cleanly.");

            byte[] sceneBytes = await File.ReadAllBytesAsync(scenePath);
            byte[] rerollBytes = await File.ReadAllBytesAsync(rerollPath);
            byte[] nifPlanBytes = await File.ReadAllBytesAsync(nifPlanPath);
            Assert(sceneResult.OutputSha256!.Value == new Sha256Hash(Hash(sceneBytes)) &&
                rerollResult.OutputSha256!.Value == new Sha256Hash(Hash(rerollBytes)) &&
                nifResult.OutputSha256!.Value == new Sha256Hash(Hash(nifPlanBytes)),
                "A preview service output hash did not match the bytes written to disk.");
            Assert(nifResult.Artifact!.InputSceneSha256 == Hash(sceneBytes),
                "The NIF plan did not bind the bytes written by the scene service.");

            var refusedScene = await sceneService.RenderAsync(sceneRequest, CancellationToken.None);
            var refusedReroll = await rerollService.RerollAsync(rerollRequest, CancellationToken.None);
            var refusedNif = await nifService.ExportPlanAsync(nifRequest, CancellationToken.None);
            Assert(!refusedScene.Written &&
                refusedScene.Diagnostics.Any(item => item.Code == "preview-output-exists") &&
                (await File.ReadAllBytesAsync(scenePath)).SequenceEqual(sceneBytes),
                "Preview scene retry did not preserve its existing destination.");
            Assert(!refusedReroll.Written &&
                refusedReroll.Diagnostics.Any(item => item.Code == "preview-reroll-output-exists") &&
                (await File.ReadAllBytesAsync(rerollPath)).SequenceEqual(rerollBytes),
                "Preview reroll retry did not preserve its existing destination.");
            Assert(!refusedNif.Written &&
                refusedNif.Diagnostics.Any(item => item.Code == "preview-nif-output-exists") &&
                (await File.ReadAllBytesAsync(nifPlanPath)).SequenceEqual(nifPlanBytes),
                "Preview NIF plan retry did not preserve its existing destination.");

            await TestSharedWriterAsync(workspacePath);
            Assert(!HasTemporarySiblings(workspacePath),
                "A preview service or helper left a temporary sibling.");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static async Task TestSharedWriterAsync(string workspacePath)
    {
        byte[] artifactBytes = Encoding.UTF8.GetBytes("preview artifact bytes");
        string successPath = Path.Combine(workspacePath, "writer-success.json");
        await PreviewArtifactFileWriter.WriteAsync(
            new WorkspacePath(successPath), artifactBytes, CancellationToken.None);
        Assert((await File.ReadAllBytesAsync(successPath)).SequenceEqual(artifactBytes),
            "The shared writer changed the artifact bytes.");

        string existingPath = Path.Combine(workspacePath, "writer-existing.json");
        byte[] existingBytes = Encoding.UTF8.GetBytes("existing destination");
        await File.WriteAllBytesAsync(existingPath, existingBytes);
        bool noClobberThrown = false;
        try
        {
            await PreviewArtifactFileWriter.WriteAsync(
                new WorkspacePath(existingPath), artifactBytes, CancellationToken.None);
        }
        catch (IOException)
        {
            noClobberThrown = true;
        }
        Assert(noClobberThrown &&
            (await File.ReadAllBytesAsync(existingPath)).SequenceEqual(existingBytes) &&
            !HasTemporarySiblings(workspacePath),
            "The shared writer did not preserve an existing destination and clean its temporary sibling.");

        string canceledPath = Path.Combine(workspacePath, "writer-canceled.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancellationThrown = false;
        try
        {
            await PreviewArtifactFileWriter.WriteAsync(
                new WorkspacePath(canceledPath), artifactBytes, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationThrown = true;
        }
        Assert(cancellationThrown && !File.Exists(canceledPath) &&
            !HasTemporarySiblings(workspacePath),
            "The shared writer did not preserve cancellation without output residue.");
    }

    private static bool HasTemporarySiblings(string directory) =>
        Directory.EnumerateFiles(directory)
            .Any(path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
