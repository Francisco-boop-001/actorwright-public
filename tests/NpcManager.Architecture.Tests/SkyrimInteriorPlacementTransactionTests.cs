using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimInteriorPlacementTransaction()
    {
        await TestPlacementExtension("CoreNpc.esp", "CoreNpc_InteriorPlacement.esp");
        await TestPlacementExtension("CoreNpc.esm", "CoreNpc_InteriorPlacement.esl");
        await TestPlacementExtension("CoreNpc.esl", "CoreNpc_InteriorPlacement.esl");
    }

    private static async Task TestPlacementExtension(string corePlugin, string expectedPatch)
    {
        string root = Path.Combine(
            Environment.CurrentDirectory, "artifacts", "interior-placement-tests",
            "preview227-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] providerBytes = new BethesdaSkyrimInteriorPlacementTopologyWriter().Write(
                new InteriorPlacementTopologyInput(
                    new PluginName("ProviderFixture.esl"),
                    [new PluginName("Skyrim.esm"), new PluginName(corePlugin)],
                    0x00001485,
                    "Fixture_Cell",
                    0x10,
                    0x20,
                    0x01000800,
                    new InteriorPlacementTopologyTransform(1, 2, 3, 0.1f, 0.2f, 0.3f),
                    null));
            string[] providerNames = ["Skyrim.esm", corePlugin, "ProviderFixture.esp"];
            foreach (string provider in providerNames)
                File.WriteAllBytes(Path.Combine(root, provider), providerBytes);

            string coreManifest = Path.Combine(root, "core.manifest.json");
            byte[] coreBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                plugin = corePlugin,
                pluginSha256 = Convert.ToHexString(SHA256.HashData(providerBytes)),
                baseNpc = corePlugin + "|0x00000800",
                placementIncluded = false,
                runtimeAuthority = false,
                visualAuthority = false
            });
            File.WriteAllBytes(coreManifest, coreBytes);

            string Rel(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var request = new SkyrimInteriorPlacementRequest
            {
                FinishCore = new() { Manifest = Rel(coreManifest), ManifestSha256 = Convert.ToHexString(SHA256.HashData(coreBytes)) },
                LoadOrder = providerNames.Select((plugin, order) => new SkyrimInteriorPlacementProvider
                {
                    Plugin = plugin,
                    Path = Rel(Path.Combine(root, plugin)),
                    Sha256 = Convert.ToHexString(SHA256.HashData(providerBytes)),
                    Order = order
                }).ToImmutableArray(),
                Cell = new()
                {
                    ProviderPlugin = "ProviderFixture.esp",
                    Owner = "Skyrim.esm|0x00001485",
                    RawFormId = "0x00001485",
                    EditorId = "Fixture_Cell",
                    InteriorBlock = 0x10,
                    InteriorSubBlock = 0x20
                },
                Transform = new() { Mode = SkyrimInteriorPlacementTransformMode.ExplicitValues, X = 1, Y = 2, Z = 3, RotationX = 0.1, RotationY = 0.2, RotationZ = 0.3 },
                Patch = new() { Optional = true },
                Output = new()
                {
                    Root = Rel(Path.Combine(root, "output")),
                    Archive = Rel(Path.Combine(root, "placement.zip"))
                }
            };
            string requestPath = Path.Combine(root, "request.json");
            byte[] requestBytes = SkyrimInteriorPlacementDocumentCodec.SerializeRequest(request);
            File.WriteAllBytes(requestPath, requestBytes);
            Sha256Hash requestHash = new(Convert.ToHexString(SHA256.HashData(requestBytes)));
            string proposalPath = Path.Combine(root, "proposal.json");
            var service = new SkyrimInteriorPlacementService(new WorkspacePath(root));
            SkyrimInteriorPlacementProposalResult analyzed = await service.AnalyzeAsync(request, requestHash, new WorkspacePath(proposalPath), CancellationToken.None);
            Assert(analyzed.Proposed && analyzed.Proposal is not null && analyzed.ProposalSha256 is not null, "interior placement analyze did not produce a proposal");
            SkyrimInteriorPlacementApplyResult applied = await service.ApplyAsync(request, requestHash, analyzed.Proposal!, analyzed.ProposalSha256!.Value, CancellationToken.None);
            if (!applied.Applied)
                Console.WriteLine(string.Join(" | ", applied.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
            Assert(applied.Applied && applied.Manifest is not null, "interior placement apply did not produce a manifest");
            Assert(applied.Manifest!.PatchPlugin == expectedPatch,
                $"Placement mastering {corePlugin} must produce {expectedPatch}; got {applied.Manifest.PatchPlugin}.");
            string patchPath = Path.Combine(root, applied.Manifest.PatchPath.Replace('/', Path.DirectorySeparatorChar));
            byte[] patchBytes = File.ReadAllBytes(patchPath);
            Assert((BinaryPrimitives.ReadUInt32LittleEndian(patchBytes.AsSpan(8, 4)) & 0x200) != 0,
                "Placement output lost its ESL flag.");
            WorkspacePath manifestPath = new(Path.Combine(root, "output", "interior-placement.manifest.json"));
            Sha256Hash manifestHash = new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath.Value))));
            SkyrimInteriorPlacementVerificationResult verified = await service.VerifyAsync(manifestPath, manifestHash, CancellationToken.None);
            Assert(verified.Verified && verified.Verification?.CellCount == 1 && verified.Verification.AchrCount == 1, "interior placement verify did not pass the independent topology census");
            Assert(!verified.Verification!.RuntimeAuthority && verified.Verification.ConflictContained, "interior placement runtime/conflict boundary changed");
            if (corePlugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            {
                string renamed = Path.ChangeExtension(expectedPatch, ".esl");
                string renamedPath = Path.Combine(root, "output", renamed);
                File.Move(patchPath, renamedPath);
                string renamedArchive = Path.Combine(root, "renamed.zip");
                using (ZipArchive archive = ZipFile.Open(renamedArchive, ZipArchiveMode.Create))
                {
                    using Stream entry = archive.CreateEntry("Data/" + renamed).Open();
                    entry.Write(patchBytes);
                }
                var renamedManifest = applied.Manifest with
                {
                    PatchPlugin = renamed,
                    PatchPath = Rel(renamedPath),
                    Archive = Rel(renamedArchive),
                    ArchiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(renamedArchive))),
                    PlacedReference = renamed + "|0x00000800",
                    // The verifier must inspect TES4 even if a rebound manifest conceals the ESP master.
                    MasterOrder = ["Skyrim.esm", "CoreNpc.esm"]
                };
                byte[] renamedBytes = SkyrimInteriorPlacementDocumentCodec.SerializeManifest(renamedManifest);
                File.WriteAllBytes(manifestPath.Value, renamedBytes);
                var refused = await service.VerifyAsync(manifestPath,
                    new Sha256Hash(Convert.ToHexString(SHA256.HashData(renamedBytes))), CancellationToken.None);
                Assert(!refused.Verified && refused.Diagnostics.Any(item => item.Code == "placement-output-extension-unsatisfiable"),
                    "A hash-bound renamed ESL mastering an ESP must receive the extension refusal: " +
                    string.Join(" | ", refused.Diagnostics.Select(item => item.Code + ":" + item.Message)));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
