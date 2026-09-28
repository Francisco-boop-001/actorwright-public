using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Writes a renamed Skyrim plugin by copying the source byte tree and
/// replacing only the selected NPC record. The source-mastered writer remains
/// a separate operation; this adapter never adds the source plugin as a
/// master.
/// </summary>
public static class BethesdaNpcStandaloneCopyAdapter
{
    private const int RecordHeaderSize = 24;
    private const uint CompressedRecordFlag = 0x0004_0000u;
    private const uint LocalizedTes4Flag = 0x0000_0080u;
    private static readonly CountCompanionDefinition[] CountCompanionDefinitions =
    [
        new("KSIZ", "KWDA", 4),
        new("COCT", "CNTO", 8),
        new("PRKZ", "PRKR", 8),
        new("SPCT", "SPLO", 4)
    ];

    public sealed record Sidecar(
        string Kind,
        string RelativeDataPath,
        int ByteLength,
        Sha256Hash Sha256);

    public static ImmutableArray<PluginName> ReadMasters(WorkspacePath source) =>
        BethesdaNpcOverrideAdapter.ReadRawMasters(source);

    /// <summary>
    /// Performs the raw standalone preflight before a typed Mutagen reader is
    /// allowed to reinterpret the source. Keeping this check at the raw
    /// boundary preserves the specific refusal code for malformed trees and
    /// unsupported packaging.
    /// </summary>
    public static void ValidateSource(
        WorkspacePath source,
        FormId targetFormId)
    {
        if (!File.Exists(source.Value))
            throw new FileNotFoundException("The source plugin does not exist.", source.Value);

        var bytes = File.ReadAllBytes(source.Value);
        ValidateSourcePackaging(source, bytes);
        // Read the complete tree, not only the path needed to find the NPC, so
        // malformed bounds in an unrelated group cannot be hidden by a typed
        // reader failure later in analysis.
        _ = BethesdaRawPluginInventory.Read(bytes);
        var target = FindTarget(bytes, targetFormId);
        if ((target.Flags & CompressedRecordFlag) != 0)
            throw new InvalidDataException(
                "npc-standalone-target-compressed: compressed target NPC records are not supported.");
    }

    public static void Write(
        NpcOverrideRequest request,
        WorkspacePath destination,
        ImmutableArray<MutationChange> requestedChanges)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            throw new InvalidDataException(
                "The standalone NPC copy writer supports Skyrim SE/AE only.");
        if (request.TargetFormId.Value > 0x00FF_FFFFu)
            throw new InvalidDataException(
                "npc-standalone-target-formid: standalone copies require a plugin-local 24-bit FormID.");
        if (!File.Exists(request.SourcePlugin.Value))
            throw new FileNotFoundException("The source plugin does not exist.",
                request.SourcePlugin.Value);
        if (File.Exists(destination.Value))
            throw new IOException("The standalone output plugin already exists.");

        var authorizedSignatures = AuthorizedRawSignatures(requestedChanges);

        var sourceBytes = File.ReadAllBytes(request.SourcePlugin.Value);
        ValidateSource(request.SourcePlugin, request.TargetFormId);
        var sourceTarget = FindTarget(sourceBytes, request.TargetFormId);

        var parent = Path.GetDirectoryName(destination.Value) ??
                     throw new InvalidDataException("The standalone output parent is missing.");
        Directory.CreateDirectory(parent);
        var scratch = Path.Combine(parent,
            $".{Path.GetFileNameWithoutExtension(request.OutputPlugin.Value)}-target-{Guid.NewGuid():N}.esp");
        try
        {
            BethesdaNpcOverrideAdapter.Write(request, new WorkspacePath(scratch));
            var replacement = FindTarget(File.ReadAllBytes(scratch), request.TargetFormId);
            var normalized = SpliceAuthorizedTargetAtoms(
                sourceTarget.Bytes,
                replacement.Bytes,
                authorizedSignatures);
            // The source-mastered writer is used only to produce the typed
            // mutation bytes in scratch. Its source-master index is never
            // allowed to become an undeclared dependency of the standalone
            // result; target FormLinks are rebound to the copied plugin owner
            // before the raw source tree is rewritten.
            var scratchMasters = BethesdaNpcOverrideAdapter.ReadRawMasters(
                new WorkspacePath(scratch));
            var sourceMasterIndex = scratchMasters.IndexOf(
                new PluginName(Path.GetFileName(request.SourcePlugin.Value)));
            if (sourceMasterIndex < 0)
                throw new InvalidDataException(
                    "npc-standalone-source-master-missing: the source plugin was not declared by the mutation writer.");
            var sourceOwnedFormIds = BethesdaRawPluginInventory.Read(request.SourcePlugin)
                .Select(row => row.RawFormId & 0x00FF_FFFFu)
                .ToImmutableHashSet();
            var sourceTargetOwnerIndex = sourceTarget.RawFormId >> 24;
            normalized = RemapSourceMasterReferences(
                normalized,
                checked((byte)sourceMasterIndex),
                checked((byte)sourceTargetOwnerIndex),
                sourceOwnedFormIds);
            var output = Rewrite(
                sourceBytes,
                0,
                sourceBytes.Length,
                sourceTarget.Offset,
                normalized,
                out var replaced);
            if (!replaced)
                throw new InvalidDataException(
                    "npc-standalone-target-missing: the target NPC was not found in the source tree.");
            File.WriteAllBytes(destination.Value, output);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    public static BethesdaRawPluginInventoryVerification Verify(
        WorkspacePath source,
        WorkspacePath output,
        FormId targetFormId) =>
        Verify(source, output, targetFormId,
            ImmutableArray<MutationChange>.Empty);

    public static BethesdaRawPluginInventoryVerification Verify(
        WorkspacePath source,
        WorkspacePath output,
        FormId targetFormId,
        ImmutableArray<MutationChange> requestedChanges)
    {
        var inventory = BethesdaRawPluginInventory.Verify(
            source, output, targetFormId);
        var rawDiagnostics = VerifyTargetRawPreservation(
            source, output, targetFormId, requestedChanges);
        return inventory with
        {
            Verified = inventory.Verified && rawDiagnostics.Length == 0,
            Diagnostics = inventory.Diagnostics.AddRange(rawDiagnostics)
        };
    }

    public static ImmutableArray<Diagnostic> VerifyTargetRawPreservation(
        WorkspacePath source,
        WorkspacePath output,
        FormId targetFormId,
        ImmutableArray<MutationChange> requestedChanges)
    {
        try
        {
            var authorizedSignatures = AuthorizedRawSignatures(requestedChanges);
            var sourceBytes = File.ReadAllBytes(source.Value);
            var outputBytes = File.ReadAllBytes(output.Value);
            var sourceTarget = FindTarget(sourceBytes, targetFormId);
            var outputTarget = FindTarget(outputBytes, targetFormId);
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!SameTargetHeaderOutsideSize(sourceTarget.Bytes, outputTarget.Bytes))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-target-subrecord-drift",
                    DiagnosticSeverity.Error,
                    "Standalone target header bytes changed outside the data-size field."));
            }

            var sourceAtoms = ReadRawAtoms(sourceTarget.Bytes);
            var outputAtoms = ReadRawAtoms(outputTarget.Bytes);
            if (!AuthorizedGapModelMatches(
                    sourceAtoms, outputAtoms, authorizedSignatures))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-target-subrecord-drift",
                    DiagnosticSeverity.Error,
                    "Standalone target subrecord atoms changed outside the proposal-authorized regions or moved across an unauthorized atom."));
            }
            ValidateAuthorizedCountCompanions(
                sourceAtoms, authorizedSignatures, diagnostics);
            ValidateAuthorizedCountCompanions(
                outputAtoms, authorizedSignatures, diagnostics);
            return diagnostics.ToImmutable();
        }
        catch (Exception exception) when (exception is IOException or
                                           InvalidDataException or
                                           OverflowException)
        {
            return [new Diagnostic(
                "npc-standalone-target-subrecord-drift",
                DiagnosticSeverity.Error,
                $"Standalone target raw preservation could not be verified: {exception.Message}")];
        }
    }

    /// <summary>
    /// Independently checks every known FormID-bearing target subrecord after
    /// the standalone rewrite. A copied source record must resolve through
    /// the output owner index; a declared-master reference must remain bound
    /// to the same declared master unless its requested mutation owns that
    /// subrecord. This is deliberately separate from proposal field
    /// verification so unrequested private links cannot be silently retained
    /// or redirected.
    /// </summary>
    public static ImmutableArray<Diagnostic> VerifyTargetReferenceOwnership(
        WorkspacePath source,
        WorkspacePath output,
        FormId targetFormId,
        ImmutableArray<MutationChange> requestedChanges)
    {
        var sourceBytes = File.ReadAllBytes(source.Value);
        var outputBytes = File.ReadAllBytes(output.Value);
        var sourceTarget = FindTarget(sourceBytes, targetFormId);
        var outputTarget = FindTarget(outputBytes, targetFormId);
        var sourceMasters = ReadMasters(source);
        var outputMasters = ReadMasters(output);
        var sourceRows = BethesdaRawPluginInventory.Read(source);
        var outputRows = BethesdaRawPluginInventory.Read(output);
        var sourceOwnerIndex = checked((byte)(sourceTarget.RawFormId >> 24));
        var outputOwnerIndex = checked((byte)(outputTarget.RawFormId >> 24));
        var sourceOwnedLocalIds = sourceRows
            .Where(row => (row.RawFormId >> 24) == sourceOwnerIndex)
            .Select(row => row.RawFormId & 0x00FF_FFFFu)
            .ToImmutableHashSet();
        var outputOwnedRows = outputRows
            .Where(row => (row.RawFormId >> 24) == outputOwnerIndex)
            .ToImmutableArray();
        var sourceLinks = ReadTargetFormLinks(sourceTarget.Bytes);
        var outputLinks = ReadTargetFormLinks(outputTarget.Bytes);
        var sourceLinksByKey = sourceLinks.ToDictionary(
            item => (item.Signature, item.Ordinal));
        var outputLinksByKey = outputLinks.ToDictionary(
            item => (item.Signature, item.Ordinal));
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var authorizedSignatures = requestedChanges
            .SelectMany(change => RawFormSignatures(change.Field))
            .ToImmutableHashSet(StringComparer.Ordinal);

        if (outputOwnerIndex != outputMasters.Length)
            diagnostics.Add(new Diagnostic(
                "npc-standalone-reference-ownership",
                DiagnosticSeverity.Error,
                $"The target FormID owner index {outputOwnerIndex} is not the standalone output index {outputMasters.Length}."));

        foreach (var link in outputLinks)
        {
            if (link.RawFormId == 0) continue;
            var pluginIndex = link.RawFormId >> 24;
            var localFormId = link.RawFormId & 0x00FF_FFFFu;
            if (pluginIndex == outputOwnerIndex)
            {
                if (!sourceOwnedLocalIds.Contains(localFormId) ||
                    !outputOwnedRows.Any(row =>
                        (row.RawFormId & 0x00FF_FFFFu) == localFormId))
                {
                    diagnostics.Add(new Diagnostic(
                        "npc-standalone-reference-ownership",
                        DiagnosticSeverity.Error,
                        $"Private target link {link.Signature}[{link.Ordinal}] FormID 0x{link.RawFormId:X8} does not resolve to a copied output record."));
                }
                else if (!authorizedSignatures.Contains(link.Signature) &&
                         !sourceLinksByKey.ContainsKey((link.Signature, link.Ordinal)))
                {
                    diagnostics.Add(new Diagnostic(
                        "npc-standalone-reference-ownership",
                        DiagnosticSeverity.Error,
                        $"New unrequested private target link {link.Signature}[{link.Ordinal}] FormID 0x{link.RawFormId:X8} is not present in the source target."));
                }
                continue;
            }

            if (pluginIndex >= outputMasters.Length ||
                pluginIndex >= sourceMasters.Length ||
                !string.Equals(sourceMasters[(int)pluginIndex].Value,
                    outputMasters[(int)pluginIndex].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-reference-ownership",
                    DiagnosticSeverity.Error,
                    $"Target link {link.Signature}[{link.Ordinal}] FormID 0x{link.RawFormId:X8} uses an undeclared or changed master index."));
                continue;
            }

            if (!sourceLinksByKey.TryGetValue(
                    (link.Signature, link.Ordinal), out var sourceLink) ||
                (sourceLink.RawFormId != link.RawFormId &&
                 !authorizedSignatures.Contains(link.Signature)))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-declared-reference-drift",
                    DiagnosticSeverity.Error,
                    $"Unrequested declared-master link {link.Signature}[{link.Ordinal}] changed from the source target."));
            }
        }

        foreach (var sourceLink in sourceLinks.Where(item =>
                     !authorizedSignatures.Contains(item.Signature)))
        {
            if (!outputLinksByKey.TryGetValue(
                    (sourceLink.Signature, sourceLink.Ordinal), out var outputLink) ||
                outputLink.RawFormId != sourceLink.RawFormId)
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-reference-drift",
                    DiagnosticSeverity.Error,
                    $"Unrequested target FormLink {sourceLink.Signature}[{sourceLink.Ordinal}] was not preserved."));
            }
        }

        foreach (var signature in sourceLinks.Select(item => item.Signature)
                     .Concat(outputLinks.Select(item => item.Signature))
                     .Distinct(StringComparer.Ordinal))
        {
            if (authorizedSignatures.Contains(signature)) continue;
            var sourceSequence = sourceLinks
                .Where(item => item.Signature == signature)
                .Select(item => item.RawFormId);
            var outputSequence = outputLinks
                .Where(item => item.Signature == signature)
                .Select(item => item.RawFormId);
            if (!sourceSequence.SequenceEqual(outputSequence))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-reference-drift",
                    DiagnosticSeverity.Error,
                    $"Complete unrequested target FormLink sequence {signature} changed in order, count, or identity."));
            }
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Sidecar> CopyLooseTargetSidecars(
        WorkspacePath sourcePlugin,
        WorkspacePath destinationDataRoot,
        PluginName outputPlugin,
        FormId targetFormId)
    {
        if (!File.Exists(sourcePlugin.Value))
            throw new FileNotFoundException("The source plugin does not exist.", sourcePlugin.Value);
        if (targetFormId.Value > 0x00FF_FFFFu)
            throw new InvalidDataException(
                "npc-standalone-target-formid: standalone sidecars require a plugin-local 24-bit FormID.");
        ValidateSourcePackaging(sourcePlugin, File.ReadAllBytes(sourcePlugin.Value));

        var sourceDataRoot = Path.GetDirectoryName(sourcePlugin.Value) ??
            throw new InvalidDataException("The source plugin data root is missing.");
        var sourcePluginName = Path.GetFileName(sourcePlugin.Value);
        var outputPluginName = outputPlugin.Value;
        var formFileName = targetFormId.Value.ToString("X8").ToLowerInvariant();
        var candidates = new[]
        {
            ("face-geom", "meshes/actors/character/FaceGenData/FaceGeom", ".nif"),
            ("face-tint", "textures/actors/character/FaceGenData/FaceTint", ".dds"),
            ("face-diffuse", "textures/actors/character/FaceGenData/FaceDiffuse", ".dds"),
            ("face-normal", "textures/actors/character/FaceGenData/FaceNormal", ".dds")
        };
        var copied = ImmutableArray.CreateBuilder<Sidecar>();
        foreach (var candidate in candidates)
        {
            var sourceRelative = $"{candidate.Item2}/{sourcePluginName}/{formFileName}{candidate.Item3}";
            var destinationRelative = $"{candidate.Item2}/{outputPluginName}/{formFileName}{candidate.Item3}";
            var sourcePath = Path.Combine(sourceDataRoot,
                sourceRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath)) continue;
            if (ContainsReparsePointBetween(sourceDataRoot, sourcePath))
                throw new InvalidDataException(
                    $"npc-standalone-sidecar-reparse: source sidecar '{sourceRelative}' traverses a reparse point.");

            var destinationPath = Path.Combine(destinationDataRoot.Value,
                destinationRelative.Replace('/', Path.DirectorySeparatorChar));
            var destinationParent = Path.GetDirectoryName(destinationPath) ??
                throw new InvalidDataException("The standalone sidecar destination parent is missing.");
            if (ContainsReparsePointBetween(destinationDataRoot.Value, destinationPath))
                throw new InvalidDataException(
                    $"npc-standalone-sidecar-reparse: destination sidecar '{destinationRelative}' traverses a reparse point.");
            Directory.CreateDirectory(destinationParent);
            if (ContainsReparsePointBetween(destinationDataRoot.Value, destinationPath))
                throw new InvalidDataException(
                    $"npc-standalone-sidecar-reparse: destination sidecar '{destinationRelative}' traverses a reparse point.");
            File.Copy(sourcePath, destinationPath, overwrite: false);
            var info = new FileInfo(destinationPath);
            if (info.Length <= 0 || info.Length > int.MaxValue)
                throw new InvalidDataException(
                    $"npc-standalone-sidecar-size: sidecar '{destinationRelative}' is empty or too large.");
            string destinationHash;
            using (var stream = File.OpenRead(destinationPath))
                destinationHash = Convert.ToHexString(SHA256.HashData(stream));
            copied.Add(new Sidecar(
                candidate.Item1,
                destinationRelative,
                checked((int)info.Length),
                new Sha256Hash(destinationHash)));
        }
        return copied.ToImmutable();
    }

    private static RecordSlice FindTarget(byte[] bytes, FormId targetFormId)
    {
        RecordSlice? match = null;
        var local = targetFormId.Value & 0x00FF_FFFFu;
        Walk(bytes, 0, bytes.Length, slice =>
        {
            if (slice.Signature == "NPC_" &&
                (slice.RawFormId & 0x00FF_FFFFu) == local)
            {
                if (match is not null)
                    throw new InvalidDataException(
                        "npc-standalone-target-ambiguous: more than one target NPC was found.");
                match = slice;
            }
        });
        return match ?? throw new InvalidDataException(
            $"npc-standalone-target-missing: NPC {targetFormId} was not found.");
    }

    private static ImmutableHashSet<string> AuthorizedRawSignatures(
        ImmutableArray<MutationChange> requestedChanges)
    {
        var signatures = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var change in requestedChanges)
        {
            if (!BethesdaPluginVerifier.TryGetRawSignatures(
                    change.Field, out var changeSignatures))
                throw new InvalidDataException(
                    $"npc-standalone-change-unsupported: proposal field '{change.Field}' has no closed raw target mapping.");
            signatures.UnionWith(changeSignatures);
        }
        return signatures.ToImmutable();
    }

    private static byte[] SpliceAuthorizedTargetAtoms(
        byte[] sourceTarget,
        byte[] replacementTarget,
        ImmutableHashSet<string> authorizedSignatures)
    {
        var sourceAtoms = ReadRawAtoms(sourceTarget);
        var sourceRegions = ReadAuthorizedRegions(sourceAtoms, authorizedSignatures);
        var replacementAtoms = ExpandRepeatedReplacementAtoms(
            sourceRegions,
            ReadRawAtoms(replacementTarget),
            authorizedSignatures);
        replacementAtoms = EnsureAuthorizedCountCompanions(
            replacementAtoms, authorizedSignatures);
        var replacementRegions = SplitRepeatedReplacementRegions(
            sourceRegions,
            ReadAuthorizedRegions(replacementAtoms, authorizedSignatures));
        var mappedRegions = new Dictionary<int, ImmutableArray<RawAtom>>();
        var insertions = new List<RawAtomInsertion>();
        var replacementIndex = 0;
        for (var sourceRegionIndex = 0;
             sourceRegionIndex < sourceRegions.Length;
             sourceRegionIndex++)
        {
            var sourceRegion = sourceRegions[sourceRegionIndex];
            RawAtomRegion? match = null;
            while (replacementIndex < replacementRegions.Length)
            {
                var candidate = replacementRegions[replacementIndex];
                if (RegionsCompatible(sourceRegion, candidate))
                {
                    match = candidate;
                    break;
                }

                // A replacement region that occurs in a later source region
                // must not be consumed as a new insertion for this one. This
                // keeps source-region occurrence ordering stable when a
                // collection field has been cleared or reordered.
                if (sourceRegions.Skip(sourceRegionIndex)
                        .Any(region => RegionsCompatible(region, candidate)))
                    break;

                insertions.Add(CreateRegionInsertion(
                    sourceAtoms,
                    replacementAtoms,
                    authorizedSignatures,
                    candidate,
                    replacementIndex));
                replacementIndex++;
            }

            if (match is not null)
            {
                mappedRegions[sourceRegion.StartIndex] = match.Atoms;
                replacementIndex++;
            }
        }

        while (replacementIndex < replacementRegions.Length)
        {
            var candidate = replacementRegions[replacementIndex];
            insertions.Add(CreateRegionInsertion(
                sourceAtoms,
                replacementAtoms,
                authorizedSignatures,
                candidate,
                replacementIndex));
            replacementIndex++;
        }

        var outputAtoms = new List<RawAtom>();
        for (var index = 0; index < sourceAtoms.Length; index++)
        {
            foreach (var insertion in insertions
                         .Where(item => item.SourceIndex == index && item.Before)
                         .OrderBy(item => item.ReplacementIndex))
                outputAtoms.AddRange(insertion.Atoms);

            if (sourceRegions.FirstOrDefault(region =>
                    region.StartIndex == index) is { } sourceRegion)
            {
                if (mappedRegions.TryGetValue(sourceRegion.StartIndex,
                        out var replacementRegion))
                    outputAtoms.AddRange(replacementRegion);
                index = sourceRegion.EndIndexExclusive - 1;
            }
            else
                outputAtoms.Add(sourceAtoms[index]);

            foreach (var insertion in insertions
                         .Where(item => item.SourceIndex == index && !item.Before)
                         .OrderBy(item => item.ReplacementIndex))
                outputAtoms.AddRange(insertion.Atoms);
        }

        var outputAtomArray = outputAtoms.ToImmutableArray();
        if (!AuthorizedGapModelMatches(
                sourceAtoms, outputAtomArray, authorizedSignatures))
            throw new InvalidDataException(
                "npc-standalone-authorized-drift: standalone splice changed an unauthorized target gap.");
        return BuildTargetFromSourceHeader(sourceTarget, outputAtomArray);
    }

    private static RawAtomInsertion CreateRegionInsertion(
        ImmutableArray<RawAtom> sourceAtoms,
        ImmutableArray<RawAtom> replacementAtoms,
        ImmutableHashSet<string> authorizedSignatures,
        RawAtomRegion replacementRegion,
        int replacementIndex)
    {
        for (var index = replacementRegion.StartIndex - 1; index >= 0; index--)
        {
            var candidate = replacementAtoms[index];
            if (authorizedSignatures.Contains(candidate.Signature)) continue;
            var sourceIndex = FindMatchingSourceAtomIndex(
                sourceAtoms, replacementAtoms, index);
            if (sourceIndex >= 0)
                return new RawAtomInsertion(
                    sourceIndex,
                    false,
                    replacementIndex,
                    replacementRegion.Atoms);
        }
        for (var index = replacementRegion.EndIndexExclusive;
             index < replacementAtoms.Length;
             index++)
        {
            var candidate = replacementAtoms[index];
            if (authorizedSignatures.Contains(candidate.Signature)) continue;
            var sourceIndex = FindMatchingSourceAtomIndex(
                sourceAtoms, replacementAtoms, index);
            if (sourceIndex >= 0)
                return new RawAtomInsertion(
                    sourceIndex,
                    true,
                    replacementIndex,
                    replacementRegion.Atoms);
        }
        throw new InvalidDataException(
            $"npc-standalone-authorized-anchor: no deterministic source/replacement anchor exists for authorized region starting with '{replacementRegion.Atoms[0].Signature}'.");
    }

    private static ImmutableArray<RawAtomRegion> ReadAuthorizedRegions(
        ImmutableArray<RawAtom> atoms,
        ImmutableHashSet<string> authorizedSignatures)
    {
        var regions = ImmutableArray.CreateBuilder<RawAtomRegion>();
        var index = 0;
        while (index < atoms.Length)
        {
            if (!authorizedSignatures.Contains(atoms[index].Signature))
            {
                index++;
                continue;
            }

            var start = index;
            var signature = atoms[index].Signature;
            while (index < atoms.Length &&
                   atoms[index].Signature == signature)
                index++;
            regions.Add(new RawAtomRegion(
                start,
                index,
                atoms.Skip(start).Take(index - start).ToImmutableArray()));
        }
        return regions.ToImmutable();
    }

    private static ImmutableArray<RawAtomRegion> SplitRepeatedReplacementRegions(
        ImmutableArray<RawAtomRegion> sourceRegions,
        ImmutableArray<RawAtomRegion> replacementRegions)
    {
        var replacementUniformCounts = replacementRegions
            .Select(region => (Region: region, Signature: UniformSignature(region)))
            .Where(item => item.Signature is not null)
            .GroupBy(item => item.Signature!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<RawAtomRegion>();
        foreach (var replacementRegion in replacementRegions)
        {
            var signature = UniformSignature(replacementRegion);
            if (signature is null ||
                replacementUniformCounts[signature] != 1)
            {
                result.Add(replacementRegion);
                continue;
            }

            var matchingSourceRegions = sourceRegions
                .Where(region => UniformSignature(region) == signature)
                .ToArray();
            if (matchingSourceRegions.Length <= 1)
            {
                result.Add(replacementRegion);
                continue;
            }

            var sourceAtomCount = matchingSourceRegions.Sum(
                region => region.Atoms.Length);
            if (sourceAtomCount != replacementRegion.Atoms.Length)
                throw new InvalidDataException(
                    $"npc-standalone-authorized-region: repeated '{signature}' regions cannot be mapped without collapsing an unauthorized gap.");

            var offset = 0;
            foreach (var sourceRegion in matchingSourceRegions)
            {
                result.Add(new RawAtomRegion(
                    replacementRegion.StartIndex + offset,
                    replacementRegion.StartIndex + offset + sourceRegion.Atoms.Length,
                    replacementRegion.Atoms.Skip(offset)
                        .Take(sourceRegion.Atoms.Length)
                        .ToImmutableArray()));
                offset += sourceRegion.Atoms.Length;
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<RawAtom> ExpandRepeatedReplacementAtoms(
        ImmutableArray<RawAtomRegion> sourceRegions,
        ImmutableArray<RawAtom> replacementAtoms,
        ImmutableHashSet<string> authorizedSignatures)
    {
        var replacementRegions = ReadAuthorizedRegions(
            replacementAtoms, authorizedSignatures);
        var replacementUniformCounts = replacementRegions
            .Select(region => (Region: region, Signature: UniformSignature(region)))
            .Where(item => item.Signature is not null)
            .GroupBy(item => item.Signature!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);
        var expansions = new Dictionary<int, ImmutableArray<RawAtom>>();
        foreach (var replacementRegion in replacementRegions)
        {
            var signature = UniformSignature(replacementRegion);
            if (signature is null ||
                replacementUniformCounts[signature] != 1)
                continue;
            var matchingSourceRegions = sourceRegions
                .Where(region => UniformSignature(region) == signature)
                .ToArray();
            if (matchingSourceRegions.Length <= 1)
                continue;
            var stride = CollectionEntryStride(signature);
            if (stride == 0)
                throw new InvalidDataException(
                    $"npc-standalone-authorized-region: repeated '{signature}' regions cannot be split safely.");

            var sourceDataAtoms = matchingSourceRegions
                .SelectMany(region => region.Atoms)
                .ToArray();
            var sourceEntryCounts = sourceDataAtoms
                .Select(atom => checked((int)CountCollectionEntries(
                    [atom], stride)))
                .ToArray();
            var replacementPayload = replacementRegion.Atoms
                .SelectMany(ReadRawAtomPayload)
                .ToArray();
            if (replacementPayload.Length % stride != 0 ||
                sourceEntryCounts.Sum() != replacementPayload.Length / stride)
            {
                if (replacementPayload.Length == 0)
                {
                    expansions[replacementRegion.StartIndex] = [];
                    continue;
                }
                throw new InvalidDataException(
                    $"npc-standalone-authorized-region: repeated '{signature}' entries cannot be mapped without collapsing an unauthorized gap.");
            }

            var expanded = ImmutableArray.CreateBuilder<RawAtom>();
            var payloadOffset = 0;
            foreach (var entryCount in sourceEntryCounts)
            {
                var payloadLength = checked(entryCount * stride);
                expanded.Add(CreateRawAtom(
                    signature,
                    replacementPayload.AsSpan(payloadOffset, payloadLength)
                        .ToArray(),
                    sourceDataAtoms[expanded.Count]));
                payloadOffset += payloadLength;
            }
            expansions[replacementRegion.StartIndex] = expanded.ToImmutable();
        }

        if (expansions.Count == 0)
            return replacementAtoms;
        var normalized = ImmutableArray.CreateBuilder<RawAtom>();
        var index = 0;
        while (index < replacementAtoms.Length)
        {
            if (expansions.TryGetValue(index, out var expanded))
            {
                var region = replacementRegions.Single(item =>
                    item.StartIndex == index);
                normalized.AddRange(expanded);
                index = region.EndIndexExclusive;
            }
            else
            {
                normalized.Add(replacementAtoms[index]);
                index++;
            }
        }
        return normalized.ToImmutable();
    }

    private static RawAtom CreateRawAtom(
        string signature,
        byte[] payload,
        RawAtom? sourceAtom = null)
    {
        var extended = payload.Length > ushort.MaxValue ||
            (sourceAtom is not null && sourceAtom.Bytes.AsSpan(0, 4)
                .SequenceEqual("XXXX"u8));
        if (extended)
        {
            var extendedBytes = new byte[checked(16 + payload.Length)];
            "XXXX"u8.CopyTo(extendedBytes.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(
                extendedBytes.AsSpan(4, 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(
                extendedBytes.AsSpan(6, 4), checked((uint)payload.Length));
            Encoding.ASCII.GetBytes(signature).CopyTo(
                extendedBytes.AsSpan(10, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(
                extendedBytes.AsSpan(14, 2), 0);
            payload.CopyTo(extendedBytes.AsSpan(16));
            return new RawAtom(signature, 0, extendedBytes.Length,
                extendedBytes);
        }
        var bytes = new byte[checked(6 + payload.Length)];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(4, 2), checked((ushort)payload.Length));
        payload.CopyTo(bytes.AsSpan(6));
        return new RawAtom(signature, 0, bytes.Length, bytes);
    }

    private static uint CountCollectionEntries(
        IReadOnlyCollection<RawAtom> atoms,
        int entryStride)
    {
        uint count = 0;
        foreach (var atom in atoms)
        {
            var payload = ReadRawAtomPayload(atom);
            if (payload.Length % entryStride != 0)
                throw new InvalidDataException(
                    $"npc-standalone-authorized-region: {atom.Signature} payload is not a whole-entry sequence.");
            count = checked(count + checked((uint)(payload.Length / entryStride)));
        }
        return count;
    }

    private static int CollectionEntryStride(string signature) => signature switch
    {
        "SNAM" or "CNTO" or "PRKR" => 8,
        "KWDA" or "SPLO" or "APPR" => 4,
        _ => 0
    };

    private static string? UniformSignature(RawAtomRegion region)
    {
        if (region.Atoms.IsEmpty) return null;
        var signature = region.Atoms[0].Signature;
        return region.Atoms.All(atom => atom.Signature == signature)
            ? signature
            : null;
    }

    private static bool RegionsCompatible(
        RawAtomRegion sourceRegion,
        RawAtomRegion replacementRegion)
    {
        return UniformSignature(sourceRegion) is { } sourceSignature &&
               sourceSignature == UniformSignature(replacementRegion);
    }

    private static int FindMatchingSourceAtomIndex(
        ImmutableArray<RawAtom> sourceAtoms,
        ImmutableArray<RawAtom> replacementAtoms,
        int replacementIndex)
    {
        var signature = replacementAtoms[replacementIndex].Signature;
        var ordinal = 0;
        for (var index = 0; index <= replacementIndex; index++)
            if (replacementAtoms[index].Signature == signature)
                ordinal++;
        ordinal--;
        var sourceCount = sourceAtoms.Count(atom => atom.Signature == signature);
        var replacementCount = replacementAtoms.Count(atom => atom.Signature == signature);
        if (sourceCount != replacementCount)
            return -1;
        var seen = 0;
        for (var index = 0; index < sourceAtoms.Length; index++)
        {
            if (sourceAtoms[index].Signature != signature) continue;
            if (seen == ordinal) return index;
            seen++;
        }
        return -1;
    }

    private static ImmutableArray<RawAtom> EnsureAuthorizedCountCompanions(
        ImmutableArray<RawAtom> replacementAtoms,
        ImmutableHashSet<string> authorizedSignatures)
    {
        var atoms = replacementAtoms.ToList();
        foreach (var companion in CountCompanionDefinitions)
        {
            if (!authorizedSignatures.Contains(companion.CountSignature) ||
                !authorizedSignatures.Contains(companion.DataSignature))
                continue;

            var dataAtoms = atoms.Where(atom =>
                    atom.Signature == companion.DataSignature).ToArray();
            if (dataAtoms.Length == 0) continue;
            var entryCount = CountDataEntries(dataAtoms, companion);
            var countAtoms = atoms.Where(atom =>
                    atom.Signature == companion.CountSignature).ToArray();
            if (countAtoms.Length == 0)
            {
                var firstDataIndex = atoms.FindIndex(atom =>
                    atom.Signature == companion.DataSignature);
                atoms.Insert(firstDataIndex,
                    CreateCountAtom(companion.CountSignature, entryCount));
            }
            else if (countAtoms.Length != 1 ||
                     ReadCountValue(countAtoms[0], companion.CountSignature) != entryCount)
            {
                throw new InvalidDataException(
                    $"npc-standalone-target-count-drift: replacement {companion.CountSignature} does not match its {companion.DataSignature} entries.");
            }
        }
        return atoms.ToImmutableArray();
    }

    private static RawAtom CreateCountAtom(string signature, uint value)
    {
        var bytes = new byte[10];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), value);
        return new RawAtom(signature, 0, bytes.Length, bytes);
    }

    private static uint CountDataEntries(
        IReadOnlyCollection<RawAtom> dataAtoms,
        CountCompanionDefinition companion)
    {
        uint count = 0;
        foreach (var atom in dataAtoms)
        {
            var payload = ReadRawAtomPayload(atom);
            if (payload.Length % companion.EntryStride != 0)
                throw new InvalidDataException(
                    $"npc-standalone-target-count-drift: {atom.Signature} payload is not a whole-entry sequence.");
            count = checked(count + checked((uint)(payload.Length /
                companion.EntryStride)));
        }
        return count;
    }

    private static byte[] ReadRawAtomPayload(RawAtom atom)
    {
        if (atom.Bytes.Length < 6)
            throw new InvalidDataException(
                "npc-standalone-target-subrecord: raw atom header is truncated.");
        var signature = Encoding.ASCII.GetString(atom.Bytes, 0, 4);
        var payloadOffset = signature == "XXXX" ? 16 : 6;
        if (payloadOffset > atom.Bytes.Length)
            throw new InvalidDataException(
                "npc-standalone-target-subrecord-extended: raw atom payload is truncated.");
        return atom.Bytes.AsSpan(payloadOffset).ToArray();
    }

    private static uint ReadCountValue(RawAtom atom, string signature)
    {
        var payload = ReadRawAtomPayload(atom);
        if (payload.Length != 4)
            throw new InvalidDataException(
                $"npc-standalone-target-count-drift: {signature} must contain one 4-byte count.");
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    private static void ValidateAuthorizedCountCompanions(
        ImmutableArray<RawAtom> atoms,
        ImmutableHashSet<string> authorizedSignatures,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var companion in CountCompanionDefinitions)
        {
            if (!authorizedSignatures.Contains(companion.CountSignature) &&
                !authorizedSignatures.Contains(companion.DataSignature))
                continue;

            try
            {
                var dataAtoms = atoms.Where(atom =>
                        atom.Signature == companion.DataSignature).ToArray();
                var entryCount = CountDataEntries(dataAtoms, companion);
                var countAtoms = atoms.Where(atom =>
                        atom.Signature == companion.CountSignature).ToArray();
                if (entryCount > 0 && countAtoms.Length != 1)
                    throw new InvalidDataException(
                        $"{companion.CountSignature} must appear exactly once when {companion.DataSignature} contains entries.");
                if (entryCount == 0 && countAtoms.Length > 1)
                    throw new InvalidDataException(
                        $"{companion.CountSignature} appears more than once.");

                if (countAtoms.Length == 1)
                {
                    var count = ReadCountValue(
                        countAtoms[0], companion.CountSignature);
                    if (entryCount == 0 && count != 0 ||
                        entryCount > 0 && count != entryCount)
                        throw new InvalidDataException(
                            $"{companion.CountSignature} value {count} does not match {entryCount} {companion.DataSignature} entries.");
                }
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-target-count-drift",
                    DiagnosticSeverity.Error,
                    $"Standalone target count companion validation failed: {exception.Message}"));
            }
        }
    }

    private static bool AuthorizedGapModelMatches(
        ImmutableArray<RawAtom> sourceAtoms,
        ImmutableArray<RawAtom> outputAtoms,
        ImmutableHashSet<string> authorizedSignatures)
    {
        var sourceBackbone = BuildUnauthorizedBackbone(
            sourceAtoms, authorizedSignatures);
        var outputBackbone = BuildUnauthorizedBackbone(
            outputAtoms, authorizedSignatures);
        if (sourceBackbone.Length != outputBackbone.Length ||
            sourceBackbone.Zip(outputBackbone).Any(pair =>
                pair.First.Signature != pair.Second.Signature ||
                !pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes)))
            return false;

        var sourceGaps = BuildAuthorizedGapMap(
            sourceAtoms, authorizedSignatures);
        var outputGaps = BuildAuthorizedGapMap(
            outputAtoms, authorizedSignatures);
        foreach (var signature in authorizedSignatures)
        {
            var sourceHas = sourceGaps.TryGetValue(
                signature, out var sourceSignatureGaps);
            var outputHas = outputGaps.TryGetValue(
                signature, out var outputSignatureGaps);
            if (sourceHas && outputHas &&
                !sourceSignatureGaps.SequenceEqual(outputSignatureGaps))
                return false;
            if (!sourceHas && outputHas && outputSignatureGaps.Length != 1)
                return false;
        }
        return true;
    }

    private static ImmutableArray<RawAtom> BuildUnauthorizedBackbone(
        ImmutableArray<RawAtom> atoms,
        ImmutableHashSet<string> authorizedSignatures)
    {
        return atoms.Where(atom =>
                !authorizedSignatures.Contains(atom.Signature))
            .ToImmutableArray();
    }

    private static ImmutableDictionary<string, ImmutableArray<int>>
        BuildAuthorizedGapMap(
            ImmutableArray<RawAtom> atoms,
            ImmutableHashSet<string> authorizedSignatures)
    {
        var gaps = new Dictionary<string, List<int>>(
            StringComparer.Ordinal);
        var backboneIndex = 0;
        foreach (var atom in atoms)
        {
            if (authorizedSignatures.Contains(atom.Signature))
            {
                if (!gaps.TryGetValue(atom.Signature, out var signatureGaps))
                {
                    signatureGaps = [];
                    gaps.Add(atom.Signature, signatureGaps);
                }
                if (signatureGaps.Count == 0 ||
                    signatureGaps[^1] != backboneIndex)
                    signatureGaps.Add(backboneIndex);
            }
            else
                backboneIndex++;
        }
        return gaps.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.Ordinal);
    }

    private static byte[] BuildTargetFromSourceHeader(
        byte[] sourceTarget,
        IEnumerable<RawAtom> atoms)
    {
        if (sourceTarget.Length < RecordHeaderSize ||
            !sourceTarget.AsSpan(0, 4).SequenceEqual("NPC_"u8))
            throw new InvalidDataException(
                "npc-standalone-target-header: source target record is invalid.");
        using var stream = new MemoryStream();
        stream.Write(sourceTarget, 0, RecordHeaderSize);
        foreach (var atom in atoms)
            stream.Write(atom.Bytes);
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(4, 4), checked((uint)(result.Length - RecordHeaderSize)));
        return result;
    }

    private static bool SameTargetHeaderOutsideSize(
        byte[] sourceTarget,
        byte[] outputTarget)
    {
        return sourceTarget.Length >= RecordHeaderSize &&
               outputTarget.Length >= RecordHeaderSize &&
               sourceTarget.AsSpan(0, 4).SequenceEqual(outputTarget.AsSpan(0, 4)) &&
               sourceTarget.AsSpan(8, 16).SequenceEqual(outputTarget.AsSpan(8, 16));
    }

    private static ImmutableArray<RawAtom> ReadRawAtoms(byte[] targetRecord)
    {
        if (targetRecord.Length < RecordHeaderSize ||
            !targetRecord.AsSpan(0, 4).SequenceEqual("NPC_"u8))
            throw new InvalidDataException(
                "npc-standalone-target-subrecord: target record header is invalid.");
        var atoms = ImmutableArray.CreateBuilder<RawAtom>();
        var position = RecordHeaderSize;
        while (position < targetRecord.Length)
        {
            var start = position;
            if (targetRecord.Length - position < 6)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord: target subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(targetRecord, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                targetRecord.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            var atomSignature = signature;
            int end;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || targetRecord.Length - payload < 4)
                    throw new InvalidDataException(
                        "npc-standalone-target-subrecord-extended: malformed XXXX size marker.");
                var extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    targetRecord.AsSpan(payload, 4));
                if (extendedSize > int.MaxValue)
                    throw new InvalidDataException(
                        "npc-standalone-target-subrecord-size: extended subrecord exceeds the supported range.");
                var following = checked(payload + 4);
                if (targetRecord.Length - following < 6)
                    throw new InvalidDataException(
                        "npc-standalone-target-subrecord-extended: XXXX marker has no following subrecord.");
                atomSignature = Encoding.ASCII.GetString(
                    targetRecord, following, 4);
                var followingSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    targetRecord.AsSpan(following + 4, 2));
                if (followingSize != 0)
                    throw new InvalidDataException(
                        "npc-standalone-target-subrecord-extended: extended subrecord has a nonzero short size.");
                end = checked(following + 6 + (int)extendedSize);
            }
            else
                end = checked(payload + shortSize);
            if (end > targetRecord.Length)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord-bounds: target subrecord exceeds the record.");
            atoms.Add(new RawAtom(
                atomSignature,
                start,
                end,
                targetRecord.AsSpan(start, end - start).ToArray()));
            position = end;
        }
        if (position != targetRecord.Length)
            throw new InvalidDataException(
                "npc-standalone-target-subrecord: target atom parser ended out of bounds.");
        return atoms.ToImmutable();
    }

    private static byte[] RemapSourceMasterReferences(
        byte[] replacement,
        byte sourceMasterIndex,
        byte outputOwnerIndex,
        ImmutableHashSet<uint> sourceOwnedFormIds)
    {
        var remapped = replacement.ToArray();
        foreach (var subrecord in ReadSubrecords(remapped))
        {
            if (IsSingleFormIdSubrecord(subrecord.Signature) &&
                subrecord.PayloadSize >= 4)
                RemapAt(subrecord.PayloadOffset);
            else if (subrecord.Signature is "KWDA" or "SPLO" or "APPR" or "PNAM")
            {
                for (var offset = subrecord.PayloadOffset;
                     offset + 4 <= subrecord.End;
                     offset += 4)
                    RemapAt(offset);
            }
            else if (subrecord.Signature is "SNAM" or "CNTO" or "PRKR" or "PRPS")
            {
                for (var offset = subrecord.PayloadOffset;
                     offset + 4 <= subrecord.End;
                     offset += 8)
                    RemapAt(offset);
            }
        }
        return remapped;

        void RemapAt(int offset)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(
                remapped.AsSpan(offset, 4));
            if ((value >> 24) == sourceMasterIndex &&
                sourceOwnedFormIds.Contains(value & 0x00FF_FFFFu))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    remapped.AsSpan(offset, 4),
                    (uint)(outputOwnerIndex << 24) | (value & 0x00FF_FFFFu));
            }
        }
    }

    private static bool IsSingleFormIdSubrecord(string signature) => signature is
        "RNAM" or "VTCK" or "CNAM" or "HCLF" or "DOFT" or
        "SOFT" or "WNAM" or "TPLT" or "SCRI" or "EITM" or "FTST" or "INAM" or
        "UNAM" or "XNAM" or "YNAM" or "ZNAM";

    private static IEnumerable<string> RawFormSignatures(string field) => field switch
    {
        "Race" => ["RNAM"],
        "Voice" => ["VTCK"],
        "Class" => ["CNAM"],
        "CombatStyle" => ["ZNAM"],
        "Keywords" => ["KWDA"],
        "AttachParentSlots" => ["APPR"],
        "Factions" => ["SNAM"],
        "Inventory" => ["CNTO"],
        "DefaultOutfit" => ["DOFT"],
        "SleepingOutfit" => ["SOFT"],
        "Perks" => ["PRKR"],
        "ActorEffects" => ["SPLO"],
        "Properties" => ["PRPS"],
        "Skin" => ["WNAM"],
        _ => []
    };

    private static ImmutableArray<TargetFormLink> ReadTargetFormLinks(
        byte[] targetRecord)
    {
        var links = ImmutableArray.CreateBuilder<TargetFormLink>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subrecord in ReadSubrecords(targetRecord))
        {
            if (IsSingleFormIdSubrecord(subrecord.Signature))
            {
                if (subrecord.PayloadSize < 4) continue;
                Add(subrecord.Signature, subrecord.PayloadOffset);
                continue;
            }

            var stride = subrecord.Signature is "SNAM" or "CNTO" or "PRKR" or "PRPS"
                ? 8
                : subrecord.Signature is "KWDA" or "SPLO" or "APPR" or "PNAM"
                    ? 4
                    : 0;
            if (stride == 0) continue;
            for (var offset = subrecord.PayloadOffset;
                 offset + 4 <= subrecord.End;
                 offset += stride)
                Add(subrecord.Signature, offset);
        }
        return links.ToImmutable();

        void Add(string signature, int offset)
        {
            var ordinal = ordinals.GetValueOrDefault(signature);
            ordinals[signature] = ordinal + 1;
            links.Add(new TargetFormLink(
                signature,
                ordinal,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    targetRecord.AsSpan(offset, 4))));
        }
    }

    private static ImmutableArray<SubrecordSlice> ReadSubrecords(
        byte[] targetRecord)
    {
        if (targetRecord.Length < RecordHeaderSize)
            throw new InvalidDataException(
                "npc-standalone-target-subrecord: target record header is truncated.");
        var subrecords = ImmutableArray.CreateBuilder<SubrecordSlice>();
        var position = RecordHeaderSize;
        uint? extendedSize = null;
        while (position < targetRecord.Length)
        {
            if (targetRecord.Length - position < 6)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord: target subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(targetRecord, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                targetRecord.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            if (signature == "XXXX")
            {
                if (shortSize != 4 || targetRecord.Length - payload < 4 ||
                    extendedSize is not null)
                    throw new InvalidDataException(
                        "npc-standalone-target-subrecord-extended: malformed XXXX size marker.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    targetRecord.AsSpan(payload, 4));
                position = checked(payload + 4);
                continue;
            }

            var size = extendedSize ?? shortSize;
            if (extendedSize is not null && shortSize != 0)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord-extended: extended subrecord has a nonzero short size.");
            extendedSize = null;
            if (size > int.MaxValue)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord-size: extended subrecord exceeds the supported range.");
            var end = checked(payload + (int)size);
            if (end > targetRecord.Length)
                throw new InvalidDataException(
                    "npc-standalone-target-subrecord-bounds: target subrecord exceeds the record.");
            subrecords.Add(new SubrecordSlice(
                signature,
                payload,
                checked((int)size),
                end));
            position = end;
        }
        if (extendedSize is not null)
            throw new InvalidDataException(
                "npc-standalone-target-subrecord-extended: XXXX marker has no following subrecord.");
        return subrecords.ToImmutable();
    }

    private sealed record TargetFormLink(
        string Signature,
        int Ordinal,
        uint RawFormId);

    private sealed record SubrecordSlice(
        string Signature,
        int PayloadOffset,
        int PayloadSize,
        int End);

    private sealed record RawAtom(
        string Signature,
        int Start,
        int End,
        byte[] Bytes);

    private sealed record RawAtomRegion(
        int StartIndex,
        int EndIndexExclusive,
        ImmutableArray<RawAtom> Atoms);

    private sealed record CountCompanionDefinition(
        string CountSignature,
        string DataSignature,
        int EntryStride);

    private sealed record RawAtomInsertion(
        int SourceIndex,
        bool Before,
        int ReplacementIndex,
        ImmutableArray<RawAtom> Atoms);

    private static bool ContainsReparsePointBetween(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            current = parent;
        }
        return false;
    }

    private static byte[] Rewrite(
        byte[] bytes,
        int start,
        int end,
        int targetOffset,
        byte[] replacement,
        out bool replaced)
    {
        using var output = new MemoryStream();
        replaced = false;
        var position = start;
        while (position < end)
        {
            var slice = ReadSlice(bytes, position, end);
            if (slice.Signature == "GRUP")
            {
                var children = Rewrite(bytes, position + RecordHeaderSize,
                    slice.End, targetOffset, replacement, out var childReplaced);
                if (!childReplaced)
                {
                    output.Write(bytes, position, slice.Length);
                    position = slice.End;
                    continue;
                }
                var header = bytes.AsSpan(position, RecordHeaderSize).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(
                    header.AsSpan(4, 4), checked((uint)(RecordHeaderSize + children.Length)));
                output.Write(header);
                output.Write(children);
                replaced |= childReplaced;
            }
            else if (position == targetOffset)
            {
                output.Write(replacement);
                replaced = true;
            }
            else
            {
                output.Write(bytes, position, slice.Length);
            }
            position = slice.End;
        }
        if (position != end)
            throw new InvalidDataException("npc-standalone-tree-trailing-bytes: malformed TES4 container.");
        return output.ToArray();
    }

    private static void Walk(
        byte[] bytes,
        int start,
        int end,
        Action<RecordSlice> visitor)
    {
        var position = start;
        while (position < end)
        {
            var slice = ReadSlice(bytes, position, end);
            if (slice.Signature == "GRUP")
                Walk(bytes, position + RecordHeaderSize, slice.End, visitor);
            else if (slice.Signature != "TES4")
                visitor(slice);
            position = slice.End;
        }
        if (position != end)
            throw new InvalidDataException("npc-standalone-tree-trailing-bytes: malformed TES4 container.");
    }

    private static RecordSlice ReadSlice(byte[] bytes, int position, int limit)
    {
        if (position + RecordHeaderSize > limit)
            throw new InvalidDataException(
                "npc-standalone-tree-header: a TES4 record header is truncated.");
        var signature = Encoding.ASCII.GetString(bytes, position, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(position + 4, 4));
        int length;
        try
        {
            length = signature == "GRUP"
                ? checked((int)size)
                : checked(RecordHeaderSize + (int)size);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "npc-standalone-tree-size: a TES4 record exceeds the supported range.",
                exception);
        }
        if (length < RecordHeaderSize)
            throw new InvalidDataException(
                "npc-standalone-tree-size: a TES4 record is smaller than its header.");
        int end;
        try
        {
            end = checked(position + length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "npc-standalone-tree-bounds: a TES4 record end exceeds the supported range.",
                exception);
        }
        if (end > limit)
            throw new InvalidDataException(
                "npc-standalone-tree-bounds: a TES4 record exceeds its container.");
        return new RecordSlice(
            position,
            end,
            length,
            signature,
            signature == "GRUP"
                ? 0
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)),
            signature == "GRUP"
                ? 0
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)),
            bytes.AsSpan(position, length).ToArray());
    }

    private static void ValidateSourcePackaging(
        WorkspacePath sourcePlugin,
        byte[] sourceBytes)
    {
        if (sourceBytes.Length < RecordHeaderSize ||
            !sourceBytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException(
                "npc-standalone-tree-header: the source must begin with a TES4 record.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(
            sourceBytes.AsSpan(8, 4));
        if ((flags & LocalizedTes4Flag) != 0)
            throw new InvalidDataException(
                "npc-standalone-localized-source: localized source plugins are not supported.");

        var dataRoot = Path.GetDirectoryName(sourcePlugin.Value) ?? string.Empty;
        IEnumerable<string> archives;
        try
        {
            archives = Directory.EnumerateFiles(dataRoot, "*.bsa",
                SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"npc-standalone-archive-read: matching source archives could not be inspected: {exception.Message}",
                exception);
        }

        var sourceStem = Path.GetFileNameWithoutExtension(sourcePlugin.Value);
        var matchingArchive = archives.FirstOrDefault(path =>
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            return string.Equals(stem, sourceStem, StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith(sourceStem + " - ", StringComparison.OrdinalIgnoreCase);
        });
        if (matchingArchive is not null)
            throw new InvalidDataException(
                $"npc-standalone-archive-unsupported: matching plugin-keyed BSA '{Path.GetFileName(matchingArchive)}' requires archive relocation support.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record RecordSlice(
        int Offset,
        int End,
        int Length,
        string Signature,
        uint RawFormId,
        uint Flags,
        byte[] Bytes);
}
