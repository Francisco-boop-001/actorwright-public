using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Bounded second-tier placement transaction.  It consumes a hash-bound
/// Finish Core manifest and a complete copied provider snapshot, then writes a
/// fresh optional light plugin containing only one EDID-only CELL override and one
/// persistent ACHR.  It never edits or republishes the core package.
/// </summary>
public sealed partial class SkyrimInteriorPlacementService(WorkspacePath labRoot) : ISkyrimInteriorPlacementService
{
    private readonly BethesdaSkyrimInteriorPlacementTopologyWriter _writer = new();

    public async ValueTask<SkyrimInteriorPlacementProposalResult> AnalyzeAsync(
        SkyrimInteriorPlacementRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prepared prepared = await PrepareAsync(request, requestSha256, cancellationToken);
            SkyrimInteriorPlacementProposal proposal = prepared.Proposal;
            byte[] withoutSelf = SkyrimInteriorPlacementDocumentCodec.RemoveProposalHash(
                SkyrimInteriorPlacementDocumentCodec.SerializeProposal(proposal));
            Sha256Hash proposalHash = new(Convert.ToHexString(SHA256.HashData(withoutSelf)));
            proposal = proposal with { ProposalSha256 = proposalHash.Value };
            byte[] bytes = SkyrimInteriorPlacementDocumentCodec.SerializeProposal(proposal);
            WriteNewFile(proposalPath, bytes);
            return new(true, proposal, proposalPath, proposalHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            diagnostics.Add(Error("interior-placement-refused", exception.Message));
            return new(false, null, null, null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<SkyrimInteriorPlacementApplyResult> ApplyAsync(
        SkyrimInteriorPlacementRequest request,
        Sha256Hash requestSha256,
        SkyrimInteriorPlacementProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(proposal.RequestSha256, requestSha256.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(proposal.ProposalSha256, proposalSha256.Value, StringComparison.OrdinalIgnoreCase) ||
                proposal.Status != SkyrimInteriorPlacementStatus.ReadyForReviewedWrite)
                throw new InvalidDataException("The placement proposal does not bind the exact request/hash or is not ready for write.");

            Prepared prepared = await PrepareAsync(request, requestSha256, cancellationToken);
            if (!ProposalEquivalent(prepared.Proposal, proposal))
                throw new InvalidDataException("The placement proposal is stale or differs from the current provider authority.");
            if (prepared.QuestOutput is not null)
                return ApplyQuestAlias(request, requestSha256, proposal, proposalSha256, prepared.QuestOutput);

            PluginName patch = new(proposal.PatchPlugin);
            WorkspacePath outputRoot = ResolveWirePath(request.Output.Root, "output.root");
            WorkspacePath archivePath = ResolveWirePath(request.Output.Archive, "output.archive");
            if (File.Exists(outputRoot.Value) || Directory.Exists(outputRoot.Value))
                throw new InvalidDataException("The placement output root already exists.");
            if (File.Exists(archivePath.Value) || Directory.Exists(archivePath.Value))
                throw new InvalidDataException("The placement archive path already exists.");
            Directory.CreateDirectory(outputRoot.Value);
            WorkspacePath patchPath = new(Path.Combine(outputRoot.Value, patch.Value));
            byte[] pluginBytes = _writer.Write(ToWriterInput(proposal));
            InteriorPlacementTopologyVerificationResult topology =
                BethesdaSkyrimInteriorPlacementTopologyVerifier.Verify(
                    pluginBytes,
                    ParseRaw(proposal.CellRawFormId, "cellRawFormId"),
                    ParseRaw(proposal.NpcRawFormId, "npcRawFormId"),
                    proposal.InteriorBlock,
                    proposal.InteriorSubBlock,
                    proposal.LocationRawFormId is null ? null : ParseRaw(proposal.LocationRawFormId, "locationRawFormId"));
            if (!topology.HasOnlyEdidCell || !topology.HasPersistentActor)
                throw new InvalidDataException("Independent raw topology verification did not pass.");
            WriteNewFile(patchPath, pluginBytes);
            CreateArchive(archivePath, patch, pluginBytes);
            string patchHash = HashFile(patchPath.Value).Value;
            string archiveHash = HashFile(archivePath.Value).Value;
            SkyrimInteriorPlacementManifest manifest = new()
            {
                PatchPlugin = patch.Value,
                PatchPath = ToWirePath(patchPath),
                PatchSha256 = patchHash,
                Archive = ToWirePath(archivePath),
                ArchiveSha256 = archiveHash,
                RequestSha256 = requestSha256.Value,
                ProposalSha256 = proposalSha256.Value,
                FinishCoreManifestSha256 = request.FinishCore.ManifestSha256,
                CorePlugin = proposal.CorePlugin,
                CoreNpc = proposal.CoreNpc,
                Cell = proposal.CellOwner,
                CellRawFormId = proposal.CellRawFormId,
                NpcRawFormId = proposal.NpcRawFormId,
                InteriorBlock = proposal.InteriorBlock,
                InteriorSubBlock = proposal.InteriorSubBlock,
                LocationRawFormId = proposal.LocationRawFormId,
                MasterOrder = proposal.MasterOrder,
                PlacedReference = $"{patch.Value}|0x00000800",
                ConflictContained = true,
                ConflictFree = false,
                PathingAuthority = false,
                RuntimeAuthority = false,
                VisualAuthority = false,
                Status = SkyrimInteriorPlacementStatus.StaticPassRuntimeRequired
            };
            WriteNewFile(new WorkspacePath(Path.Combine(outputRoot.Value, "interior-placement.manifest.json")), SkyrimInteriorPlacementDocumentCodec.SerializeManifest(manifest));
            return new(true, manifest, outputRoot, archivePath, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            diagnostics.Add(Error("interior-placement-refused", exception.Message));
            return new(false, null, null, null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<SkyrimInteriorPlacementVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] manifestBytes = await ReadBoundFileAsync(manifestPath, manifestSha256, cancellationToken);
            SkyrimInteriorPlacementManifest manifest = SkyrimInteriorPlacementDocumentCodec.ParseManifest(manifestBytes, labRoot);
            if (!string.Equals(manifest.Status.ToString(), nameof(SkyrimInteriorPlacementStatus.StaticPassRuntimeRequired), StringComparison.Ordinal) ||
                !manifest.ConflictContained || manifest.ConflictFree || manifest.PathingAuthority || manifest.RuntimeAuthority || manifest.VisualAuthority)
                throw new InvalidDataException("The placement manifest overstates authority or conflict status.");
            WorkspacePath patchPath = ResolveWirePath(manifest.PatchPath, "manifest.patchPath");
            WorkspacePath archivePath = ResolveWirePath(manifest.Archive, "manifest.archive");
            if (!string.Equals(HashFile(patchPath.Value).Value, manifest.PatchSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HashFile(archivePath.Value).Value, manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Placement artifact hash does not match the manifest.");
            ImmutableArray<PluginName> masters = BethesdaNpcStandaloneCopyAdapter.ReadMasters(patchPath);
            if (patchPath.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) &&
                masters.Any(master => master.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(Error("placement-output-extension-unsatisfiable",
                    "A placement patch with an .esp master must use the .esp extension and retain its ESL flag."));
                return new(false, null, diagnostics.ToImmutable());
            }
            if (!string.Equals(Path.GetFileName(patchPath.Value), manifest.PatchPlugin, StringComparison.OrdinalIgnoreCase) ||
                !masters.Select(master => master.Value).SequenceEqual(manifest.MasterOrder, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Placement plugin identity or TES4 master order differs from the manifest.");
            if (manifest.PlacementMode == SkyrimQuestAliasPlacementEvidence.Mode)
                return await VerifyQuestAliasAsync(manifest, patchPath, archivePath, masters, cancellationToken);
            uint? location = manifest.LocationRawFormId is null ? null : ParseRaw(manifest.LocationRawFormId, "locationRawFormId");
            InteriorPlacementTopologyVerificationResult topology = BethesdaSkyrimInteriorPlacementTopologyVerifier.Verify(
                await File.ReadAllBytesAsync(patchPath.Value, cancellationToken),
                ParseRaw(manifest.CellRawFormId, "cellRawFormId"),
                ParseRaw(manifest.NpcRawFormId, "npcRawFormId"),
                manifest.InteriorBlock,
                manifest.InteriorSubBlock,
                location);
            VerifyArchive(archivePath.Value, manifest.PatchPlugin);
            SkyrimInteriorPlacementVerification verification = new()
            {
                Verified = true,
                Status = SkyrimInteriorPlacementStatus.StaticPassRuntimeRequired,
                PatchPlugin = manifest.PatchPlugin,
                Tes4Count = topology.Tes4Count,
                CellCount = topology.CellCount,
                AchrCount = topology.AchrCount,
                HasOnlyEdidCell = topology.HasOnlyEdidCell,
                HasPersistentActor = topology.HasPersistentActor,
                ConflictContained = true,
                RuntimeAuthority = false
            };
            return new(true, verification, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            diagnostics.Add(Error("interior-placement-verification-failed", exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
    }

    private async ValueTask<Prepared> PrepareAsync(
        SkyrimInteriorPlacementRequest request,
        Sha256Hash requestSha256,
        CancellationToken cancellationToken)
    {
        if (!request.Patch.Optional)
            throw new InvalidDataException("Interior placement is an optional second-tier patch; patch.optional must be true.");
        if (request.PlacementMode is not null)
        {
            if (request.PlacementMode != SkyrimQuestAliasPlacementEvidence.Mode)
                throw new InvalidDataException("Unsupported explicit placement mode.");
            return await PrepareQuestAliasAsync(request, requestSha256, cancellationToken);
        }
        if (request.Marker is not null || request.SandboxRadius is not null)
            throw new InvalidDataException("Marker/radius require explicit quest-alias mode.");
        (PluginName corePlugin, FormReference coreNpc) = await ReadCoreBindingAsync(request, cancellationToken);
        ImmutableArray<SkyrimInteriorPlacementProvider> providers = ValidateLoadOrder(request.LoadOrder, request.Cell.ProviderPlugin, cancellationToken);
        FormReference cellOwner = ParseReference(request.Cell.Owner, "cell.owner");
        uint requestedCellRaw = ParseRaw(request.Cell.RawFormId, "cell.rawFormId");
        FormReference? location = request.Location is null ? null : ParseReference(request.Location.Reference, "location.reference");
        uint? requestedLocationRaw = request.Location is null ? null : ParseRaw(request.Location.RawFormId, "location.rawFormId");
        ImmutableArray<PluginName> masters = BuildMasters(cellOwner.Plugin, corePlugin, location?.Plugin);
        uint cellRaw = EncodeRaw(cellOwner, masters);
        if (cellRaw != requestedCellRaw)
            throw new InvalidDataException("cell.rawFormId does not match the declared owner and first-use master order.");
        uint npcRaw = EncodeRaw(coreNpc, masters);
        uint? locationRaw = location is null ? null : EncodeRaw(location.Value, masters);
        if (requestedLocationRaw != locationRaw)
            throw new InvalidDataException("location.rawFormId does not match the declared location and first-use master order.");
        if (providers.All(item => !string.Equals(item.Plugin, request.Cell.ProviderPlugin, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected CELL provider is not in the complete copied load order.");
        if (providers.Max(item => item.Order) != providers.Single(item => string.Equals(item.Plugin, request.Cell.ProviderPlugin, StringComparison.OrdinalIgnoreCase)).Order)
            throw new InvalidDataException("The selected CELL provider is not the terminal provider in the supplied load order.");
        float[] transform = NarrowTransform(request.Transform);
        PluginName patch = DerivePatchName(corePlugin, masters);
        SkyrimInteriorPlacementProposal proposal = new()
        {
            RequestSha256 = requestSha256.Value,
            Status = SkyrimInteriorPlacementStatus.ReadyForReviewedWrite,
            PatchPlugin = patch.Value,
            MasterOrder = masters.Select(item => item.Value).ToImmutableArray(),
            CellOwner = cellOwner.ToString(),
            CellRawFormId = ToHex(cellRaw),
            NpcOwner = coreNpc.Plugin.Value,
            NpcRawFormId = ToHex(npcRaw),
            LocationOwner = location?.Plugin.Value,
            LocationRawFormId = locationRaw is null ? null : ToHex(locationRaw.Value),
            CellEditorId = request.Cell.EditorId,
            InteriorBlock = request.Cell.InteriorBlock,
            InteriorSubBlock = request.Cell.InteriorSubBlock,
            X = transform[0], Y = transform[1], Z = transform[2],
            RotationX = transform[3], RotationY = transform[4], RotationZ = transform[5],
            ConflictContained = true,
            ConflictFree = false,
            PathingAuthority = false,
            RuntimeAuthority = false,
            CorePlugin = corePlugin.Value,
            CoreNpc = coreNpc.ToString(),
            ProviderChain = providers.OrderBy(item => item.Order).Select(item => item.Plugin).ToImmutableArray(),
            Diagnostics = ImmutableArray.Create("The optional patch is conflict-contained, not conflict-free.", "The CELL payload is EDID-only; no WRLD, exterior CELL, NAVM, ownership, or script records are authored.")
        };
        return new(proposal);
    }

    private async ValueTask<(PluginName Plugin, FormReference Npc)> ReadCoreBindingAsync(
        SkyrimInteriorPlacementRequest request,
        CancellationToken cancellationToken,
        bool allowExternalManifest = false)
    {
        WorkspacePath manifestPath = ResolveWirePath(request.FinishCore.Manifest, "finishCore.manifest");
        byte[] bytes = await ReadBoundFileAsync(manifestPath, new Sha256Hash(request.FinishCore.ManifestSha256), cancellationToken);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        string? schema = root.GetProperty("schema").GetString();
        if (allowExternalManifest && schema == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier)
            _ = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(bytes, labRoot);
        if ((schema != SkyrimNpcFinishCoreManifest.SchemaIdentifier &&
             !(allowExternalManifest && schema == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier)) ||
            root.GetProperty("placementIncluded").GetBoolean() ||
            root.GetProperty("runtimeAuthority").GetBoolean() ||
            root.GetProperty("visualAuthority").GetBoolean())
            throw new InvalidDataException("The bound Finish Core manifest is not an exact world-clean static package.");
        string pluginText = root.GetProperty("plugin").GetString() ?? throw new InvalidDataException("Finish Core manifest plugin is missing.");
        string npcText = root.GetProperty("baseNpc").GetString() ?? throw new InvalidDataException("Finish Core manifest base NPC is missing.");
        PluginName plugin = new(pluginText);
        FormReference npc = ParseReference(npcText, "Finish Core baseNpc");
        if (!string.Equals(npc.Plugin.Value, plugin.Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Finish Core baseNpc must be owned by the exact Finish Core plugin.");
        return (plugin, npc);
    }

    private ImmutableArray<SkyrimInteriorPlacementProvider> ValidateLoadOrder(
        ImmutableArray<SkyrimInteriorPlacementProvider> providers,
        string selected,
        CancellationToken cancellationToken)
    {
        if (providers.IsDefaultOrEmpty)
            throw new InvalidDataException("A complete copied load order is required.");
        ImmutableArray<SkyrimInteriorPlacementProvider> ordered = providers.OrderBy(item => item.Order).ToImmutableArray();
        if (!ordered.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, ordered.Length)) || ordered.Select(item => item.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
            throw new InvalidDataException("Load-order entries must have contiguous unique order and plugin identities.");
        foreach (SkyrimInteriorPlacementProvider provider in ordered)
        {
            PluginName plugin = new(provider.Plugin);
            WorkspacePath path = ResolveWirePath(provider.Path, "loadOrder.path");
            if (!File.Exists(path.Value) || IsReparse(path.Value))
                throw new InvalidDataException($"Copied provider is missing or a reparse point: {provider.Plugin}.");
            Sha256Hash expected = new(provider.Sha256);
            if (HashFile(path.Value) != expected)
                throw new InvalidDataException($"Copied provider hash mismatch: {provider.Plugin}.");
            if (!string.Equals(plugin.Value, provider.Plugin, StringComparison.Ordinal))
                throw new InvalidDataException("Provider plugin identity is not canonical.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (ordered.All(item => !string.Equals(item.Plugin, selected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Selected CELL provider is not present in the load order.");
        return ordered;
    }

    private static ImmutableArray<PluginName> BuildMasters(PluginName cellOwner, PluginName core, PluginName? location)
    {
        List<PluginName> masters = [cellOwner, core];
        if (location is { } selected)
            masters.Add(selected);
        if (masters.Distinct().Count() != masters.Count)
            throw new InvalidDataException("First-use master order contains a duplicate plugin identity.");
        return masters.ToImmutableArray();
    }

    private static uint EncodeRaw(FormReference reference, ImmutableArray<PluginName> masters)
    {
        if (reference.FormId.Value == 0 || reference.FormId.Value > 0x00FF_FFFF)
            throw new InvalidDataException($"{reference} has an invalid local FormID.");
        // Plugin bytes use TES4 master ordinals, including for light masters; FE is a runtime index.
        int index = masters.IndexOf(reference.Plugin);
        if (index < 0 || index > byte.MaxValue)
            throw new InvalidDataException($"{reference.Plugin} is not in the first-use master order.");
        return (uint)(index << 24) | reference.FormId.Value;
    }

    private static FormReference ParseReference(string value, string field) =>
        FormReference.TryParse(value, out FormReference reference) && reference.ToString() == value
            ? reference
            : throw new InvalidDataException($"{field} must be a canonical plugin|FormID reference.");

    private static uint ParseRaw(string value, string field) =>
        FormId.TryParse(value, out FormId id) && id.Value != 0
            ? id.Value
            : throw new InvalidDataException($"{field} must be a non-zero hexadecimal raw FormID.");

    private static float[] NarrowTransform(SkyrimInteriorPlacementTransform transform)
    {
        if (transform.Mode == SkyrimInteriorPlacementTransformMode.ExistingReference && string.IsNullOrWhiteSpace(transform.Reference))
            throw new InvalidDataException("ExistingReference transform mode requires an exact reference identity.");
        if (transform.Mode == SkyrimInteriorPlacementTransformMode.ExistingReference)
            _ = ParseReference(transform.Reference!, "transform.reference");
        double[] values = [transform.X, transform.Y, transform.Z, transform.RotationX, transform.RotationY, transform.RotationZ];
        float[] narrowed = values.Select(value => (float)value).ToArray();
        if (values.Any(value => !double.IsFinite(value)) || narrowed.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Transform values must be finite and representable as float32.");
        return narrowed;
    }

    private static PluginName DerivePatchName(PluginName core, ImmutableArray<PluginName> masters)
    {
        string stem = Path.GetFileNameWithoutExtension(core.Value);
        var builder = new System.Text.StringBuilder();
        bool separator = false;
        foreach (char character in stem)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator)
            {
                builder.Append('_');
                separator = true;
            }
        }
        string safe = builder.ToString().Trim('_');
        string suffix = masters.Any(master => master.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            ? "_InteriorPlacement.esp" : "_InteriorPlacement.esl";
        int maxStem = 255 - suffix.Length;
        if (safe.Length == 0)
            throw new InvalidDataException("Finish Core plugin stem cannot derive an interior placement patch name.");
        if (safe.Length > maxStem)
            safe = safe[..maxStem].TrimEnd('_');
        return new PluginName(safe + suffix);
    }

    private static InteriorPlacementTopologyInput ToWriterInput(SkyrimInteriorPlacementProposal proposal)
    {
        return new(
            new PluginName(proposal.PatchPlugin),
            proposal.MasterOrder.Select(item => new PluginName(item)).ToImmutableArray(),
            ParseRaw(proposal.CellRawFormId, "cellRawFormId"),
            proposal.CellEditorId,
            proposal.InteriorBlock,
            proposal.InteriorSubBlock,
            ParseRaw(proposal.NpcRawFormId, "npcRawFormId"),
            new InteriorPlacementTopologyTransform(proposal.X, proposal.Y, proposal.Z, proposal.RotationX, proposal.RotationY, proposal.RotationZ),
            proposal.LocationRawFormId is null ? null : ParseRaw(proposal.LocationRawFormId, "locationRawFormId"));
    }

    private static bool ProposalEquivalent(SkyrimInteriorPlacementProposal expected, SkyrimInteriorPlacementProposal actual) =>
        string.Equals(expected.RequestSha256, actual.RequestSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.PatchPlugin, actual.PatchPlugin, StringComparison.Ordinal) &&
        expected.MasterOrder.SequenceEqual(actual.MasterOrder, StringComparer.Ordinal) &&
        expected.CellOwner == actual.CellOwner && expected.CellRawFormId == actual.CellRawFormId &&
        expected.NpcOwner == actual.NpcOwner && expected.NpcRawFormId == actual.NpcRawFormId &&
        expected.LocationOwner == actual.LocationOwner && expected.LocationRawFormId == actual.LocationRawFormId &&
        expected.CellEditorId == actual.CellEditorId && expected.InteriorBlock == actual.InteriorBlock &&
        expected.InteriorSubBlock == actual.InteriorSubBlock && expected.X == actual.X && expected.Y == actual.Y &&
        expected.Z == actual.Z && expected.RotationX == actual.RotationX && expected.RotationY == actual.RotationY &&
        expected.RotationZ == actual.RotationZ && expected.ConflictContained == actual.ConflictContained &&
        expected.ConflictFree == actual.ConflictFree && expected.PathingAuthority == actual.PathingAuthority &&
        expected.RuntimeAuthority == actual.RuntimeAuthority && expected.CorePlugin == actual.CorePlugin &&
        expected.CoreNpc == actual.CoreNpc && expected.ProviderChain.SequenceEqual(actual.ProviderChain, StringComparer.Ordinal) &&
        expected.Diagnostics.SequenceEqual(actual.Diagnostics, StringComparer.Ordinal) &&
        expected.PlacementMode == actual.PlacementMode && expected.QuestAlias == actual.QuestAlias;

    private WorkspacePath ResolveWirePath(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"{field} is not a canonical project-relative path.");
        WorkspacePath resolved = new(Path.GetFullPath(Path.Combine(labRoot.Value, value.Replace('/', Path.DirectorySeparatorChar))));
        if (!resolved.IsUnder(labRoot))
            throw new InvalidDataException($"{field} escapes the workspace.");
        return resolved;
    }

    private string ToWirePath(WorkspacePath path)
    {
        string relative = Path.GetRelativePath(labRoot.Value, path.Value).Replace(Path.DirectorySeparatorChar, '/');
        return relative;
    }

    private static async ValueTask<byte[]> ReadBoundFileAsync(WorkspacePath path, Sha256Hash expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value) || IsReparse(path.Value))
            throw new InvalidDataException("Bound documents must be ordinary files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        if (new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))) != expected)
            throw new InvalidDataException("Hash-bound document verification failed.");
        return bytes;
    }

    private static void WriteNewFile(WorkspacePath path, byte[] bytes)
    {
        string parent = Path.GetDirectoryName(path.Value) ?? throw new InvalidDataException("Output file has no parent.");
        Directory.CreateDirectory(parent);
        if (File.Exists(path.Value) || Directory.Exists(path.Value))
            throw new InvalidDataException($"Refusing to overwrite existing output: {path.Value}");
        using FileStream stream = new(path.Value, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CreateArchive(WorkspacePath archivePath, PluginName patch, byte[] bytes)
    {
        string parent = Path.GetDirectoryName(archivePath.Value) ?? throw new InvalidDataException("Archive has no parent.");
        Directory.CreateDirectory(parent);
        using FileStream stream = new(archivePath.Value, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        ZipArchiveEntry entry = archive.CreateEntry($"Data/{patch.Value}", CompressionLevel.NoCompression);
        using Stream target = entry.Open();
        target.Write(bytes);
    }

    private static void VerifyArchive(string archivePath, string patchPlugin)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string expected = $"Data/{patchPlugin}";
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != expected)
            throw new InvalidDataException("Placement archive must contain exactly one Data patch entry and no core package files.");
    }

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string ToHex(uint value) => $"0x{value:X8}";

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);

    private sealed record Prepared(SkyrimInteriorPlacementProposal Proposal, QuestAliasPlacementOutput? QuestOutput = null);
}
