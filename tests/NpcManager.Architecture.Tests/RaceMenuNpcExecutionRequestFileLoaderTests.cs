using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestRaceMenuExecutionRequestLoader()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var requestFile = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "01-source-copies", "gate2-emi2",
            "execution-request.json"));
        var bytes = await File.ReadAllBytesAsync(requestFile.Value);
        var expected = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        var loader = new RaceMenuNpcExecutionRequestFileLoader(labRoot);

        var loaded = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(requestFile, expected),
            CancellationToken.None);
        Assert(loaded.Loaded && loaded.Request is not null &&
               loaded.ActualSha256 == expected && loaded.ByteLength == bytes.LongLength,
            "The real Gate 2 request did not load with exact hash evidence.");
        var executionRequest = loaded.Request ?? throw new InvalidOperationException(
            "Loaded result omitted its execution request.");
        Assert(executionRequest.Build.Identity.Name.Value == "Emi" &&
               executionRequest.Build.PresetBundle.PresetPath.Value.EndsWith(
                   "emi2-neutral.jslot", StringComparison.OrdinalIgnoreCase) &&
               executionRequest.Build.PresetBundle.CharGenFaceGeom.Value.EndsWith(
                   "emi2-neutral.nif", StringComparison.OrdinalIgnoreCase) &&
               !executionRequest.AllowInheritedMeshEmbeddedSkinTextureRoute,
            "The shared loader did not map the real prepared request correctly.");

        var refused = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(requestFile,
                new Sha256Hash(new string('0', 64))), CancellationToken.None);
        Assert(!refused.Loaded &&
               refused.Status == RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
               refused.Diagnostics.Any(item => item.Code == "preset-npc-request-invalid"),
            "Request hash drift did not fail closed.");

        var sourcePlugin = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "Emi2FreshBuild",
            "03-builds",
            "feasibility-probes",
            "ck-carrier-root",
            "Data",
            "EmiCarrierProbe.esp"));
        var sourceBytes = await File.ReadAllBytesAsync(sourcePlugin.Value);
        var root = JsonNode.Parse(bytes)?.AsObject() ??
            throw new InvalidDataException("The Gate 2 request JSON is invalid.");
        root["schemaVersion"] = 2;
        root["faceGeomSkeletonAuthority"] = "identityFaceGenBones";
        root["allowInheritedMeshEmbeddedSkinTextureRoute"] = true;
        root["existingNpcTarget"] = new JsonObject
        {
            ["sourcePlugin"] = Path.GetRelativePath(labRoot.Value, sourcePlugin.Value)
                .Replace(Path.DirectorySeparatorChar, '/'),
            ["sourcePluginSha256"] = Convert.ToHexString(
                SHA256.HashData(sourceBytes)).ToLowerInvariant(),
            ["targetFormId"] = "0x00000800"
        };
        var scratch = new WorkspacePath(Path.Combine(
            Path.GetDirectoryName(requestFile.Value)!,
            $".execution-request-existing-{Environment.ProcessId}.json"));
        try
        {
            var existingBytes = JsonSerializer.SerializeToUtf8Bytes(root);
            await File.WriteAllBytesAsync(scratch.Value, existingBytes);
            var existingHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(existingBytes)));
            var existing = await loader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    scratch,
                    existingHash),
                CancellationToken.None);
            Assert(existing.Loaded && existing.Request?.Build.ExistingNpcTarget is
            {
                TargetFormId.Value: 0x800
            } target && target.SourcePlugin == sourcePlugin,
                "schemaVersion 2 did not retain the exact existing-NPC source/hash/FormID target.");
            Assert(existing.Request?.FaceGeomSkeletonAuthority ==
                   SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones,
                "The prepared request did not retain explicit identity FaceGen skeleton authority.");
            Assert(existing.Request?.AllowInheritedMeshEmbeddedSkinTextureRoute == true,
                "The prepared request did not retain explicit inherited mesh-embedded skin authority.");
        }
        finally
        {
            if (File.Exists(scratch.Value)) File.Delete(scratch.Value);
        }
    }
}
