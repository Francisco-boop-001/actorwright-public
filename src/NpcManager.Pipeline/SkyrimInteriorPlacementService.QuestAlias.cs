using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimInteriorPlacementService
{
    private async ValueTask<Prepared> PrepareQuestAliasAsync(SkyrimInteriorPlacementRequest request, Sha256Hash requestHash,
        CancellationToken cancellationToken)
    {
        if (request.Cell != new SkyrimInteriorPlacementCell() || request.Transform != new SkyrimInteriorPlacementTransform() ||
            request.Location is not null || request.Marker is null || request.SandboxRadius is null or 0)
            throw new InvalidDataException("Quest-alias placement requires marker/radius and cannot mix CELL/transform/location authority.");
        (PluginName corePlugin, FormReference coreNpc) = await ReadCoreBindingAsync(request, cancellationToken, allowExternalManifest: true);
        var providers = ValidateLoadOrder(request.LoadOrder, corePlugin.Value, cancellationToken);
        byte[] manifestBytes = await ReadBoundFileAsync(ResolveWirePath(request.FinishCore.Manifest, "finishCore.manifest"),
            new Sha256Hash(request.FinishCore.ManifestSha256), cancellationToken);
        using JsonDocument coreManifest = JsonDocument.Parse(manifestBytes);
        Sha256Hash coreHash = new(coreManifest.RootElement.GetProperty("pluginSha256").GetString()!);
        FormReference marker = ParseReference(request.Marker, "marker");
        var authority = BethesdaSkyrimInteriorPlacementQuestWriter.Bind(providers.Select(provider => new QuestAliasPlacementProvider(
            new PluginName(provider.Plugin), ResolveWirePath(provider.Path, "loadOrder.path"), new Sha256Hash(provider.Sha256))).ToImmutableArray(),
            coreNpc, coreHash, marker);
        PluginName patch = DerivePatchName(corePlugin, authority.Masters);
        QuestAliasPlacementOutput output = BethesdaSkyrimInteriorPlacementQuestWriter.Write(authority, patch, request.SandboxRadius.Value);
        string seqPath = request.Output.Root + "/Seq/" + Path.GetFileNameWithoutExtension(patch.Value) + ".seq";
        _ = ResolveWirePath(seqPath, "questAlias.seqPath");
        var evidence = new SkyrimQuestAliasPlacementEvidence(marker.ToString(), authority.MarkerProvider.Value,
            authority.SandboxPackage.ToString(), request.SandboxRadius.Value, $"{patch.Value}|0x00000800",
            $"0x{((uint)authority.Masters.Length << 24) | 0x800u:X8}", output.PackageRecordSha256.Value,
            seqPath, Convert.ToHexString(SHA256.HashData(output.Seq)));
        BethesdaSkyrimInteriorPlacementQuestVerifier.Verify(output.Plugin, output.Seq, patch, authority.Masters, coreNpc, evidence);
        return new(new SkyrimInteriorPlacementProposal
        {
            RequestSha256 = requestHash.Value, Status = SkyrimInteriorPlacementStatus.ReadyForReviewedWrite,
            PatchPlugin = patch.Value, MasterOrder = authority.Masters.Select(x => x.Value).ToImmutableArray(),
            NpcOwner = coreNpc.ToString(), NpcRawFormId = $"0x{EncodeRaw(coreNpc, authority.Masters):X8}",
            CorePlugin = corePlugin.Value, CoreNpc = coreNpc.ToString(), ProviderChain = providers.Select(x => x.Plugin).ToImmutableArray(),
            PlacementMode = SkyrimQuestAliasPlacementEvidence.Mode, QuestAlias = evidence,
            ConflictContained = true,
            Diagnostics = ["One script-free StartGameEnabled QUST, two aliases, one core Sandbox PACK override and one SEQ; no world records. Static evidence only."]
        }, output);
    }

    private SkyrimInteriorPlacementApplyResult ApplyQuestAlias(SkyrimInteriorPlacementRequest request, Sha256Hash requestHash,
        SkyrimInteriorPlacementProposal proposal, Sha256Hash proposalHash, QuestAliasPlacementOutput output)
    {
        WorkspacePath outputRoot = ResolveWirePath(request.Output.Root, "output.root");
        WorkspacePath archivePath = ResolveWirePath(request.Output.Archive, "output.archive");
        if (File.Exists(outputRoot.Value) || Directory.Exists(outputRoot.Value) || File.Exists(archivePath.Value) || Directory.Exists(archivePath.Value))
            throw new InvalidDataException("Quest-alias output root/archive already exists.");
        SkyrimQuestAliasPlacementEvidence evidence = proposal.QuestAlias ?? throw new InvalidDataException("Quest-alias evidence is missing.");
        WorkspacePath patchPath = new(Path.Combine(outputRoot.Value, proposal.PatchPlugin));
        WorkspacePath seqPath = ResolveWirePath(evidence.SeqPath, "questAlias.seqPath");
        WorkspacePath stageRoot = ResolveWirePath(request.Output.Root + ".stage-" + Guid.NewGuid().ToString("N"), "output.stage");
        WorkspacePath stagePatch = new(Path.Combine(stageRoot.Value, proposal.PatchPlugin));
        WorkspacePath stageSeq = new(Path.Combine(stageRoot.Value, "Seq", Path.GetFileName(seqPath.Value)));
        bool archiveInOutput = archivePath.IsUnder(outputRoot);
        WorkspacePath stageArchive = archiveInOutput
            ? new(Path.Combine(stageRoot.Value, Path.GetRelativePath(outputRoot.Value, archivePath.Value))) : archivePath;
        bool archiveCreated = false;
        Directory.CreateDirectory(stageRoot.Value);
        try
        {
            WriteNewFile(stagePatch, output.Plugin);
            WriteNewFile(stageSeq, output.Seq);
            Directory.CreateDirectory(Path.GetDirectoryName(stageArchive.Value)!);
            using (var archiveStream = new FileStream(stageArchive.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                archiveCreated = true;
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);
                using (Stream entry = archive.CreateEntry("Data/" + proposal.PatchPlugin).Open()) entry.Write(output.Plugin);
                using (Stream entry = archive.CreateEntry("Data/Seq/" + Path.GetFileName(seqPath.Value)).Open()) entry.Write(output.Seq);
            }
            var manifest = new SkyrimInteriorPlacementManifest
            {
                PatchPlugin = proposal.PatchPlugin, PatchPath = ToWirePath(patchPath), PatchSha256 = HashFile(stagePatch.Value).Value,
                Archive = ToWirePath(archivePath), ArchiveSha256 = HashFile(stageArchive.Value).Value,
                RequestSha256 = requestHash.Value, ProposalSha256 = proposalHash.Value,
                FinishCoreManifestSha256 = request.FinishCore.ManifestSha256,
                CorePlugin = proposal.CorePlugin, CoreNpc = proposal.CoreNpc, NpcRawFormId = proposal.NpcRawFormId,
                MasterOrder = proposal.MasterOrder, PlacementMode = SkyrimQuestAliasPlacementEvidence.Mode, QuestAlias = evidence,
                ConflictContained = true, Status = SkyrimInteriorPlacementStatus.StaticPassRuntimeRequired
            };
            WriteNewFile(new WorkspacePath(Path.Combine(stageRoot.Value, "interior-placement.manifest.json")),
                SkyrimInteriorPlacementDocumentCodec.SerializeManifest(manifest));
            Directory.Move(stageRoot.Value, outputRoot.Value);
            return new(true, manifest, outputRoot, archivePath, []);
        }
        catch
        {
            if (archiveCreated && !archiveInOutput) File.Delete(archivePath.Value);
            Directory.Delete(stageRoot.Value, recursive: true);
            throw;
        }
    }

    private async ValueTask<SkyrimInteriorPlacementVerificationResult> VerifyQuestAliasAsync(SkyrimInteriorPlacementManifest manifest,
        WorkspacePath patchPath, WorkspacePath archivePath, ImmutableArray<PluginName> masters, CancellationToken cancellationToken)
    {
        SkyrimQuestAliasPlacementEvidence evidence = manifest.QuestAlias ?? throw new InvalidDataException("Quest-alias evidence is missing.");
        string expectedSeq = Path.Combine(Path.GetDirectoryName(patchPath.Value)!, "Seq", Path.GetFileNameWithoutExtension(manifest.PatchPlugin) + ".seq");
        WorkspacePath seqPath = ResolveWirePath(evidence.SeqPath, "questAlias.seqPath");
        if (!string.Equals(expectedSeq, seqPath.Value, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(manifest.PlacedReference) ||
            !string.IsNullOrEmpty(manifest.Cell) || !string.IsNullOrEmpty(manifest.CellRawFormId) || manifest.LocationRawFormId is not null)
            throw new InvalidDataException("Quest-alias manifest mixes world placement fields or has a non-derived SEQ destination.");
        byte[] seq = await ReadBoundFileAsync(seqPath, new Sha256Hash(evidence.SeqSha256), cancellationToken);
        byte[] bytes = await ReadBoundFileAsync(patchPath, new Sha256Hash(manifest.PatchSha256), cancellationToken);
        FormReference npc = ParseReference(manifest.CoreNpc, "coreNpc");
        if (npc.Plugin.Value != manifest.CorePlugin || ParseRaw(manifest.NpcRawFormId, "npcRawFormId") != EncodeRaw(npc, masters))
            throw new InvalidDataException("Quest-alias manifest NPC owner/index differs from its core binding.");
        BethesdaSkyrimInteriorPlacementQuestVerifier.Verify(bytes, seq, new PluginName(manifest.PatchPlugin), masters, npc, evidence);
        using (ZipArchive archive = ZipFile.OpenRead(archivePath.Value))
        {
            if (archive.Entries.Count != 2) throw new InvalidDataException("Quest-alias archive must contain exactly the plugin and SEQ.");
            Match("Data/" + manifest.PatchPlugin, bytes);
            Match("Data/Seq/" + Path.GetFileName(seqPath.Value), seq);
            void Match(string path, byte[] expected)
            {
                ZipArchiveEntry entry = archive.Entries.Single(x => x.FullName == path);
                if (entry.Length != expected.Length) throw new InvalidDataException("Quest-alias archive entry length differs.");
                using Stream stream = entry.Open();
                byte[] actual = new byte[expected.Length];
                stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Quest-alias archive bytes differ from loose artifacts.");
            }
        }
        return new(true, new SkyrimInteriorPlacementVerification
        {
            Verified = true, Status = SkyrimInteriorPlacementStatus.StaticPassRuntimeRequired, PatchPlugin = manifest.PatchPlugin,
            Tes4Count = 1, ConflictContained = true, PlacementMode = SkyrimQuestAliasPlacementEvidence.Mode, QuestAlias = evidence
        }, []);
    }
}
