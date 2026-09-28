using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static async Task AssertFinishPackagePublicationAsync(
        WorkspacePath root, SkyrimNpcFinishCoreRequest request)
    {
        if (request.Source.PluginPath is not { } sourcePlugin ||
            request.Source.PackageManifest is not { } sourceManifest ||
            request.Source.PackageRoot is not { } sourceRoot ||
            request.Output.Root is not { } outputRoot)
            throw new InvalidOperationException("The golden Finish request must bind its source and output package paths.");
        void AssertSourceUnchanged() => Require(
            new Sha256Hash(HashFile(sourcePlugin)) == request.Source.PluginSha256 &&
            new Sha256Hash(HashFile(sourceManifest)) == request.Source.PackageManifestSha256 &&
            SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(sourceRoot) ==
                request.Source.PackageTreeSha256,
            "Finish/package verification changed the original create plugin, manifest, or package tree.");

        AssertSourceUnchanged();
        var schema = await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json",
            "--command", "package verify");
        Require(schema.ExitCode == 0 && schema.Root.ToString().Contains("manifest", StringComparison.Ordinal),
            "Exact binary package verify schema must publish its manifest input: " + schema.Root + schema.StdErr);
        var manifestPath = Child(outputRoot, "npcmanager-package.json");
        var verified = await RunCliAsync(root, "package", "verify", "--json", "--manifest", manifestPath.Value);
        AssertSourceUnchanged();
        Require(verified.ExitCode != 0 &&
            (verified.Root.ToString() + verified.StdErr).Contains(
                "workflow-human-review-required",
                StringComparison.Ordinal),
            "Actual generic package verify accepted a finished package without its exact workflow bundle: " +
            verified.Root + verified.StdErr);

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath.Value));
        JsonElement document = manifest.RootElement;
        Require(document.GetProperty("outputPlugin").GetString() == request.Output.PluginFileName &&
            FormId.TryParse(document.GetProperty("targetFormId").GetString()!, out var actor) &&
            actor == request.Actor.FormId,
            "Published generic manifest lost the finished output plugin or actor identity.");
        JsonElement[] rows = document.GetProperty("artifacts").EnumerateArray().ToArray();
        string[] declared = rows.Select(row => new AssetPath(row.GetProperty("relativePath").GetString()!).Value).ToArray();
        Require(declared.Distinct(StringComparer.OrdinalIgnoreCase).Count() == declared.Length,
            "Published generic manifest contains duplicate or case-aliased artifact paths.");
        string[] actual = Directory.EnumerateFiles(outputRoot.Value, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), manifestPath.Value, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(outputRoot.Value, path).Replace('\\', '/')).ToArray();
        Require(declared.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "Published generic manifest must inventory every final file exactly once, excluding itself.");
        string pluginRelative = Path.GetRelativePath(sourceRoot.Value, sourcePlugin.Value).Replace('\\', '/');
        Require(rows.Count(row => row.GetProperty("kind").GetString() == "plugin" &&
            row.GetProperty("relativePath").GetString() == pluginRelative) == 1,
            "Published generic manifest must contain exactly one finished plugin row.");
        foreach (JsonElement row in rows)
        {
            var path = Child(outputRoot, new AssetPath(row.GetProperty("relativePath").GetString()!).Value);
            Require(row.GetProperty("byteLength").GetInt64() == new FileInfo(path.Value).Length &&
                string.Equals(row.GetProperty("sha256").GetString(), HashFile(path), StringComparison.OrdinalIgnoreCase),
                "Published generic manifest contains stale file bytes: " + row.GetProperty("relativePath").GetString());
        }
        await AssertFinishPackageManifestBindingsAsync(root, request, sourceManifest, outputRoot, manifestPath, pluginRelative);
        AssertSourceUnchanged();
    }

    private static async Task AssertFinishPackageManifestBindingsAsync(
        WorkspacePath root, SkyrimNpcFinishCoreRequest request, WorkspacePath sourceManifest,
        WorkspacePath outputRoot, WorkspacePath genericPath, string pluginRelative)
    {
        var finishPath = Child(outputRoot, "NPCManager/Evidence/finish-core-manifest.json");
        var verificationPath = Child(outputRoot, "NPCManager/Evidence/finish-core-verification.json");
        var retainedSourcePath = Child(outputRoot, "NPCManager/finish-core-source-package-manifest.json");
        WorkspacePath archivePath = request.Output.Archive!.Value;
        byte[] originalGeneric = File.ReadAllBytes(genericPath.Value);
        byte[] originalFinish = File.ReadAllBytes(finishPath.Value);
        byte[] originalVerification = File.ReadAllBytes(verificationPath.Value);
        byte[] originalArchive = File.ReadAllBytes(archivePath.Value);
        byte[] retainedSource = File.ReadAllBytes(retainedSourcePath.Value);
        Require(retainedSource.AsSpan().SequenceEqual(File.ReadAllBytes(sourceManifest.Value)),
            "Published provenance must retain the exact original source manifest bytes.");
        string finishHash = HashFile(finishPath);
        void Restore()
        {
            File.WriteAllBytes(genericPath.Value, originalGeneric);
            File.WriteAllBytes(finishPath.Value, originalFinish);
            File.WriteAllBytes(verificationPath.Value, originalVerification);
            File.WriteAllBytes(archivePath.Value, originalArchive);
            File.WriteAllBytes(retainedSourcePath.Value, retainedSource);
        }
        try
        {
            JsonNode tampered = JsonNode.Parse(originalGeneric)!;
            tampered["sourcePreset"] = "fabricated-source-provenance";
            File.WriteAllText(genericPath.Value, tampered.ToJsonString());
            UpdateFinishArchiveEntry(archivePath, "Data/npcmanager-package.json", File.ReadAllBytes(genericPath.Value));
            var policy = new KOnlyWorkspacePolicy(root, ActorwrightWorkspace.ResolveProtectedRoot(root));
            PackageVerifyResult genericVerify = await new PackageVerifyService(
                    new PackageManifestReader(policy, root))
                .VerifyAsync(new PackageVerifyRequest(genericPath), CancellationToken.None);
            Require(genericVerify.Verified,
                "The provenance tamper fixture must retain a valid generic artifact inventory.");
            var refused = await RunCliAsync(root, "npc", "finish", "verify", "--json",
                "--manifest", finishPath.Value, "--manifest-sha256", finishHash);
            Require(refused.ExitCode != 0 && refused.Root.ToString().Contains("finish-core-verify-package-manifest", StringComparison.Ordinal),
                "Finish verify accepted forged generic provenance with a synchronized archive: " + refused.Root + refused.StdErr);
            Restore();

            File.Delete(genericPath.Value);
            UpdateFinishArchiveEntry(archivePath, "Data/npcmanager-package.json", null);
            var missing = await RunCliAsync(root, "npc", "finish", "verify", "--json",
                "--manifest", finishPath.Value, "--manifest-sha256", finishHash);
            Require(missing.ExitCode != 0 && !missing.Root.GetProperty("verified").GetBoolean(),
                "Deleting the generated manifest and its archive member must not enter legacy digest verification.");
            Restore();

            File.WriteAllBytes(genericPath.Value, File.ReadAllBytes(sourceManifest.Value));
            File.Delete(retainedSourcePath.Value);
            UpdateFinishArchiveEntry(archivePath, "Data/NPCManager/finish-core-source-package-manifest.json", null);
            using IncrementalHash legacyDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string file in Directory.EnumerateFiles(outputRoot.Value, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(outputRoot.Value, path).Replace('\\', '/'), StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(outputRoot.Value, file).Replace('\\', '/');
                if (string.Equals(relative, pluginRelative, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("NPCManager/Evidence/", StringComparison.Ordinal) ||
                    relative.StartsWith("Data/NPCManager/Evidence/", StringComparison.Ordinal) ||
                    relative == "README-Finish-Core.txt") continue;
                legacyDigest.AppendData(Encoding.UTF8.GetBytes(relative));
                legacyDigest.AppendData([0]);
                legacyDigest.AppendData(File.ReadAllBytes(file));
                legacyDigest.AppendData([0]);
            }
            var legacyHash = new Sha256Hash(Convert.ToHexString(legacyDigest.GetHashAndReset()));
            SkyrimNpcFinishCoreManifest finished = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(originalFinish, root);
            File.WriteAllBytes(finishPath.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(finished with
            {
                PackageTreeSha256 = legacyHash,
                Evidence = finished.Evidence with { PackageTreeSha256 = legacyHash }
            }, root));
            JsonNode legacyVerification = JsonNode.Parse(originalVerification)!;
            legacyVerification["packageTreeSha256"] = legacyHash.Value;
            File.WriteAllText(verificationPath.Value, legacyVerification.ToJsonString());
            UpdateFinishArchiveEntry(archivePath, "Data/npcmanager-package.json", File.ReadAllBytes(genericPath.Value));
            UpdateFinishArchiveEntry(archivePath, "NPCManager/Evidence/finish-core-manifest.json", File.ReadAllBytes(finishPath.Value));
            UpdateFinishArchiveEntry(archivePath, "NPCManager/Evidence/finish-core-verification.json", File.ReadAllBytes(verificationPath.Value));
            var legacy = await RunCliAsync(root, "npc", "finish", "verify", "--json",
                "--manifest", finishPath.Value, "--manifest-sha256", HashFile(finishPath));
            Require(legacy.ExitCode == 0 && legacy.Root.GetProperty("verified").GetBoolean(),
                "Legacy copied-manifest output failed independent old-domain verification: " + legacy.Root + legacy.StdErr);
        }
        finally { Restore(); }
    }

    private static void UpdateFinishArchiveEntry(WorkspacePath archivePath, string name, byte[]? bytes)
    {
        using ZipArchive archive = ZipFile.Open(archivePath.Value, ZipArchiveMode.Update);
        ZipArchiveEntry existing = archive.GetEntry(name) ?? throw new InvalidOperationException("Missing test archive entry: " + name);
        existing.Delete();
        if (bytes is null) return;
        ZipArchiveEntry replacement = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream output = replacement.Open();
        output.Write(bytes);
    }
}
