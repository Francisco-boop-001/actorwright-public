using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimBsaProduction()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-023-bsa-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dataRoot = new WorkspacePath(Path.Combine(
                labRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "01-source-copies",
                "real-modlist",
                "briar-armor-20260723",
                "Data"));
            var firstArchive = new WorkspacePath(
                Path.Combine(root, "Gate23BriarA.bsa"));
            var secondArchive = new WorkspacePath(
                Path.Combine(root, "Gate23BriarB.bsa"));
            var extracted = new WorkspacePath(
                Path.Combine(root, "extracted"));
            ImmutableArray<AssetPath> members =
            [
                new("Meshes/armor/Briar/Briar_0.nif"),
                new("Meshes/armor/Briar/Briar_1.nif"),
                new("Meshes/armor/Briar/Briar_GND.nif"),
                new("Textures/armor/Briar/Briar.dds"),
                new("Textures/armor/Briar/Briar_m.dds"),
                new("Textures/armor/Briar/Briar_n.dds")
            ];
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var service = new BethesdaSkyrimBsaService(policy, labRoot);

            SkyrimBsaBuildResult first = await service.BuildAsync(
                new SkyrimBsaBuildRequest(
                    dataRoot,
                    firstArchive,
                    members),
                CancellationToken.None);
            SkyrimBsaBuildResult second = await service.BuildAsync(
                new SkyrimBsaBuildRequest(
                    dataRoot,
                    secondArchive,
                    members),
                CancellationToken.None);
            Assert(first.Written && first.Artifact is not null &&
                   second.Written && second.Artifact is not null &&
                   first.Artifact.ArchiveSha256 ==
                   second.Artifact.ArchiveSha256 &&
                   first.Artifact.Members.Length == members.Length &&
                   first.Artifact.Members.All(item =>
                       members.Any(expected =>
                           string.Equals(
                               expected.Value,
                               item.Path.Value,
                               StringComparison.OrdinalIgnoreCase))) &&
                   first.Artifact.Members.All(item => item.Matches),
                "Deterministic BSA v105 production did not retain the real copied Briar member inventory.");
            SkyrimBsaBuildArtifact firstArtifact =
                first.Artifact ?? throw new InvalidOperationException(
                    "The first BSA build did not return an artifact.");

            SkyrimBsaVerifyResult verified = await service.VerifyAsync(
                new SkyrimBsaVerifyRequest(
                    firstArchive,
                    firstArtifact.Members),
                CancellationToken.None);
            Assert(verified.Verified &&
                   verified.Members.All(item => item.Matches),
                "Independent Mutagen archive readback did not verify every Briar BSA member.");

            ImmutableArray<SkyrimBsaMemberArtifact> wrong =
                firstArtifact.Members.SetItem(
                    0,
                    firstArtifact.Members[0] with
                    {
                        Sha256 = new Sha256Hash(new string('0', 64))
                    });
            SkyrimBsaVerifyResult rejected = await service.VerifyAsync(
                new SkyrimBsaVerifyRequest(firstArchive, wrong),
                CancellationToken.None);
            Assert(!rejected.Verified &&
                   rejected.Diagnostics.Any(item =>
                       item.Code == "skyrim-bsa-member-mismatch"),
                "The BSA verifier accepted a planted wrong member hash.");

            SkyrimBsaExtractResult extraction = await service.ExtractAsync(
                new SkyrimBsaExtractRequest(firstArchive, extracted),
                CancellationToken.None);
            Assert(extraction.Extracted &&
                   extraction.Artifact is not null &&
                   extraction.Artifact.Members.Length == members.Length,
                "The real copied Briar archive did not extract to one fresh loose root.");
            foreach (SkyrimBsaMemberArtifact member in
                     firstArtifact.Members)
            {
                string source = Path.Combine(
                    dataRoot.Value,
                    member.Path.Value.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
                string output = Path.Combine(
                    extracted.Value,
                    member.Path.Value.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
                Assert(File.Exists(output) &&
                       HashGate23BsaFile(source) ==
                       HashGate23BsaFile(output),
                    $"Extracted BSA member '{member.Path}' did not match its real copied source.");
            }

            var existingOutput = new WorkspacePath(
                Path.Combine(root, "existing-extraction"));
            Directory.CreateDirectory(existingOutput.Value);
            SkyrimBsaExtractResult existingRejected =
                await service.ExtractAsync(
                    new SkyrimBsaExtractRequest(
                        firstArchive,
                        existingOutput),
                    CancellationToken.None);
            Assert(
                !existingRejected.Extracted &&
                existingRejected.Diagnostics.Any(item =>
                    item.Code == "skyrim-bsa-extract-exists") &&
                !Directory.EnumerateFileSystemEntries(
                    root,
                    "existing-extraction.tmp-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "Existing BSA extraction output was not refused without temporary residue.");

            var cancelledOutput = new WorkspacePath(
                Path.Combine(root, "cancelled-extraction"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() =>
                service.ExtractAsync(
                        new SkyrimBsaExtractRequest(
                            firstArchive,
                            cancelledOutput),
                        cancelled.Token)
                    .AsTask());
            Assert(
                !Directory.Exists(cancelledOutput.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root,
                    "cancelled-extraction.tmp-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "Cancelled BSA extraction left committed or temporary residue.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Sha256Hash HashGate23BsaFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }
}
