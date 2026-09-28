using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Applies an accepted reference-authoring proposal to a compatible RaceMenu
/// baseline, writes a new canonical JSlot, and independently reopens it through
/// the public preset service before returning evidence.
/// </summary>
public sealed class ReferenceRaceMenuPresetWriter(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IPresetService presetService)
    : IReferenceRaceMenuPresetWriter
{
    public async ValueTask<ReferencePresetWriteArtifact> WriteAsync(
        ReferenceRaceMenuPresetWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        PresetAppearance merged = MergeAppearance(
            request, diagnostics);
        diagnostics.AddRange(
            RaceMenuJslotCodec.ValidateForWrite(merged));
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        byte[] bytes = RaceMenuJslotCodec.Write(merged);
        Sha256Hash outputHash = Hash(bytes);
        string destination = request.DestinationPath.Value;
        string temporary = destination + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        bool promoted = false;
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                    bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            promoted = true;

            byte[] reopenedBytes = await File.ReadAllBytesAsync(
                destination, cancellationToken)
                .ConfigureAwait(false);
            if (Hash(reopenedBytes) != outputHash ||
                !reopenedBytes.AsSpan().SequenceEqual(bytes))
            {
                diagnostics.Add(Error(
                    "reference-preset-binary-readback-mismatch",
                    "Reopened JSlot bytes do not match the flushed candidate."));
                TryDelete(destination);
                promoted = false;
                return Refused(request, diagnostics);
            }

            PresetParseResult parsed =
                await presetService.InspectAsync(
                    new PresetParseRequest(
                        PresetFormat.RaceMenuJslot,
                        GameEdition.SkyrimSpecialEdition,
                        request.DestinationPath),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(parsed.Diagnostics);
            if (parsed.Document is null ||
                HasErrors(parsed.Diagnostics))
            {
                diagnostics.Add(Error(
                    "reference-preset-readback-refused",
                    "The public preset service could not reopen the written JSlot."));
                TryDelete(destination);
                promoted = false;
                return Refused(request, diagnostics);
            }

            byte[] canonicalReadback =
                RaceMenuJslotCodec.Write(
                    parsed.Document.Appearance);
            if (!canonicalReadback.AsSpan().SequenceEqual(bytes))
            {
                diagnostics.Add(Error(
                    "reference-preset-readback-mismatch",
                    "The reopened typed RaceMenu appearance is not canonically identical to the candidate."));
                TryDelete(destination);
                promoted = false;
                return Refused(request, diagnostics);
            }

            Sha256Hash evidence = Hash(
                outputHash.Value,
                Hash(canonicalReadback).Value,
                request.ProposalSha256.Value,
                request.ResourceSnapshotDocumentSha256.Value);
            diagnostics.Add(new Diagnostic(
                "reference-preset-written-verified",
                DiagnosticSeverity.Info,
                "Wrote, flushed, atomically promoted, reopened, and canonically verified the new RaceMenu JSlot."));
            return new ReferencePresetWriteArtifact(
                request.DestinationPath,
                outputHash,
                parsed.Document,
                evidence,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            if (promoted)
                TryDelete(destination);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            TryDelete(temporary);
            if (promoted)
                TryDelete(destination);
            diagnostics.Add(Error(
                "reference-preset-write-failed",
                exception.Message));
            return Refused(request, diagnostics);
        }
    }

    private void ValidateRequest(
        ReferenceRaceMenuPresetWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Snapshot is null)
        {
            diagnostics.Add(Error(
                "reference-preset-snapshot",
                "The exact resource snapshot is required."));
            return;
        }
        ReferencePresetResourceSnapshot snapshot =
            request.Snapshot;
        diagnostics.AddRange(
            ReferencePresetAuthoringRules.CanWritePreset(
                request.Proposal,
                request.ProposalSha256,
                request.ProposalSha256,
                snapshot.ReviewedDesignSha256,
                request.ResourceSnapshotDocumentSha256));
        if (request.Baseline.Format !=
                PresetFormat.RaceMenuJslot ||
            request.Baseline.Edition !=
                GameEdition.SkyrimSpecialEdition ||
            !request.Baseline.IsValid ||
            request.Baseline.SourceHash !=
                snapshot.BaselineJslotSha256 ||
            snapshot.Baseline.SourceHash !=
                snapshot.BaselineJslotSha256)
        {
            diagnostics.Add(Error(
                "reference-preset-baseline",
                "The write baseline is not the valid Skyrim JSlot bound by the resource snapshot."));
        }
        if (snapshot.SchemaVersion != 1 ||
            snapshot.ProjectId != request.Proposal.ProjectId ||
            snapshot.CatalogAuthority is null ||
            snapshot.CatalogAuthority.Race !=
                request.Proposal.Race ||
            snapshot.CatalogAuthority.Sex !=
                request.Proposal.Sex)
        {
            diagnostics.Add(Error(
                "reference-preset-catalog",
                "The schema-1 catalog authority does not match the proposal target."));
        }
        if (request.Proposal.SolverResult.ResultSha256 is null ||
            !request.Proposal.SolverResult.Accepted ||
            !float.IsFinite(request.Proposal.Weight) ||
            request.Proposal.Weight is < 0 or > 100)
        {
            diagnostics.Add(Error(
                "reference-preset-solution",
                "An accepted finite solved target is required."));
        }
        foreach (string name in
                 request.Proposal.SolverResult.NativeMorphs.Keys)
        {
            if (!name.StartsWith(
                    "NAM9[", StringComparison.Ordinal) &&
                !name.StartsWith(
                    "NAMA[", StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "reference-preset-native-control",
                    $"Solved native control '{name}' is unsupported."));
            }
        }
        if (!request.DestinationPath.Value.EndsWith(
                ".jslot", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "reference-preset-extension",
                "Reference preset output must use the .jslot extension."));
        }
        if (File.Exists(request.DestinationPath.Value) ||
            Directory.Exists(request.DestinationPath.Value))
        {
            diagnostics.Add(Error(
                "reference-preset-output-exists",
                "Reference preset output must be an absent path."));
        }
        diagnostics.AddRange(policy.Evaluate(
            labRoot, request.DestinationPath));
        string? parent =
            Path.GetDirectoryName(request.DestinationPath.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "reference-preset-output-parent",
                "The JSlot destination parent must already exist."));
        }
    }

    private static PresetAppearance MergeAppearance(
        ReferenceRaceMenuPresetWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ReferencePresetResourceSnapshot snapshot =
            request.Snapshot!;
        ReferencePresetCatalogAuthority catalog =
            snapshot.CatalogAuthority!;
        ValidateSelectedHeadparts(
            snapshot.CatalogSelection, catalog, diagnostics);
        ImmutableArray<PresetHeadPart> headParts =
            catalog.HeadParts
                .OrderBy(item => (int)item.Type)
                .ThenBy(item => item.Reference.Plugin.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Reference.FormId.Value)
                .Select(item => new PresetHeadPart(
                    PresetIdentifier.Parse(
                        PortableIdentifier(item.Reference)),
                    (int)item.Type))
                .ToImmutableArray();
        if (headParts.Select(item => item.Identifier.Raw)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != headParts.Length)
        {
            diagnostics.Add(Error(
                "reference-preset-headpart-duplicate",
                "The exact catalog contains duplicate portable headpart identities."));
        }

        ImmutableArray<PresetTint> tints =
            BuildTints(snapshot, diagnostics);
        ImmutableArray<float> sliders =
            BuildNam9(request, diagnostics);
        RaceMenuPresetData raceMenu =
            request.Baseline.Appearance.RaceMenu ??
            EmptyRaceMenu();
        ImmutableArray<uint> presets =
            BuildNama(raceMenu.FaceMorphPresets,
                request.Proposal.SolverResult.NativeMorphs,
                diagnostics);
        (ImmutableDictionary<string, float> custom,
            ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedCustom) =
            BuildCustom(request, diagnostics);
        ImmutableArray<RaceMenuSculptPart> sculpt =
            BuildSculpt(request, raceMenu, diagnostics);

        RaceMenuPresetData updatedRaceMenu = raceMenu with
        {
            FaceMorphPresets = presets,
            SculptParts = sculpt
        };
        PresetWeight? baselineWeight =
            request.Baseline.Appearance.Weight;
        var weight = new PresetWeight(
            request.Proposal.Weight,
            baselineWeight?.Thin,
            baselineWeight?.Muscular,
            baselineWeight?.Fat);
        return request.Baseline.Appearance with
        {
            HeadParts = headParts,
            Weight = weight,
            SliderMorphs = sliders,
            CustomMorphs = custom,
            OrderedCustomMorphs = orderedCustom,
            Tints = tints,
            RaceMenu = updatedRaceMenu
        };
    }

    private static void ValidateSelectedHeadparts(
        ReferencePresetCatalogSelection selection,
        ReferencePresetCatalogAuthority catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!selection.ReviewAccepted)
        {
            diagnostics.Add(Error(
                "reference-preset-headpart-review",
                "The selected headpart catalog was not accepted."));
            return;
        }
        (FormReference Reference, NpcHeadPartType Type,
            string Label)[] required =
        [
            (selection.Face, NpcHeadPartType.Face, "face"),
            (selection.Mouth, NpcHeadPartType.Misc, "mouth"),
            (selection.Eyes, NpcHeadPartType.Eyes, "eyes"),
            (selection.Brows, NpcHeadPartType.Eyebrows, "brows"),
            (selection.Hair, NpcHeadPartType.Hair, "hair")
        ];
        foreach ((FormReference reference, NpcHeadPartType type,
                     string label) in required)
        {
            int matches = catalog.HeadParts.Count(item =>
                item.IsRootSelection &&
                item.Reference == reference &&
                item.Type == type);
            if (matches != 1)
            {
                diagnostics.Add(Error(
                    "reference-preset-headpart-authority",
                    $"Reviewed {label} identity does not resolve to exactly one catalog root."));
            }
        }
    }

    private static ImmutableArray<PresetTint> BuildTints(
        ReferencePresetResourceSnapshot snapshot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var output = ImmutableArray.CreateBuilder<PresetTint>();
        foreach (ReferenceTintSelection selected in
                 snapshot.CatalogSelection.Tints)
        {
            ReferencePresetCatalogTint[] exact =
                snapshot.CatalogAuthority!.Tints
                    .Where(item => item.Selection == selected)
                    .ToArray();
            if (exact.Length != 1)
            {
                diagnostics.Add(Error(
                    "reference-preset-tint-authority",
                    $"Reviewed tint index {selected.TintIndex} has no unique target-race catalog authority."));
                continue;
            }
            ReferencePresetCatalogTint tint = exact[0];
            output.Add(new PresetTint(
                selected.TintIndex,
                selected.Argb,
                tint.MaskPath.Value,
                selected.TintType,
                Math.Clamp(
                    (int)Math.Round(
                        selected.Strength * 100,
                        MidpointRounding.AwayFromZero),
                    0, 100)));
        }
        return output.OrderBy(item => item.Index)
            .ToImmutableArray();
    }

    private static ImmutableArray<float> BuildNam9(
        ReferenceRaceMenuPresetWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int length = Math.Max(
            18, request.Baseline.Appearance.SliderMorphs.Length);
        float[] values = new float[length];
        request.Baseline.Appearance.SliderMorphs.CopyTo(values);
        Array.Clear(values, 0, 18);
        foreach ((string name, double value) in
                 request.Proposal.SolverResult.NativeMorphs)
        {
            if (!name.StartsWith(
                    "NAM9", StringComparison.Ordinal))
                continue;
            if (!TryParseIndexed(name, "NAM9", 18,
                    out int index) ||
                !double.IsFinite(value) ||
                value is < -1 or > 1)
            {
                diagnostics.Add(Error(
                    "reference-preset-nam9",
                    $"Solved native control '{name}' is not a legal NAM9 value."));
                continue;
            }
            values[index] = checked((float)value);
        }
        return ImmutableArray.CreateRange(values);
    }

    private static ImmutableArray<uint> BuildNama(
        ImmutableArray<uint> baseline,
        ImmutableDictionary<string, double> solved,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int length = Math.Max(4, baseline.Length);
        uint[] values = new uint[length];
        baseline.CopyTo(values);
        for (var index = 0; index < 4; index++)
            values[index] = uint.MaxValue;
        foreach ((string name, double value) in solved)
        {
            if (!name.StartsWith(
                    "NAMA", StringComparison.Ordinal))
                continue;
            if (!TryParseIndexed(name, "NAMA", 4,
                    out int index) ||
                !double.IsFinite(value) ||
                value < 0 ||
                value > uint.MaxValue ||
                value != Math.Truncate(value))
            {
                diagnostics.Add(Error(
                    "reference-preset-nama",
                    $"Solved native control '{name}' is not a legal NAMA value."));
                continue;
            }
            values[index] = checked((uint)value);
        }
        return ImmutableArray.CreateRange(values);
    }

    private static (
        ImmutableDictionary<string, float> Custom,
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> Ordered)
        BuildCustom(
            ReferenceRaceMenuPresetWriteRequest request,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ReferencePresetResourceSnapshot snapshot =
            request.Snapshot!;
        HashSet<string> legal = snapshot.MorphChannels
            .Where(item =>
                item.Kind == ReferenceMorphChannelKind.Custom)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, float> values =
            request.Baseline.Appearance.CustomMorphs
                .Where(item => !legal.Contains(item.Key))
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
        IEnumerable<SkyrimRaceMenuCustomMorphValue> baselineOrder =
            request.Baseline.Appearance.OrderedCustomMorphs
                .IsDefaultOrEmpty
                ? request.Baseline.Appearance.CustomMorphs
                    .OrderBy(item => item.Key,
                        StringComparer.Ordinal)
                    .Select(item =>
                        new SkyrimRaceMenuCustomMorphValue(
                            item.Key, item.Value))
                : request.Baseline.Appearance.OrderedCustomMorphs;
        var ordered = baselineOrder
            .Where(item => !legal.Contains(item.Name))
            .ToList();
        Dictionary<string, ReferenceMorphChannelAuthority> authority =
            snapshot.MorphChannels
                .Where(item =>
                    item.Kind == ReferenceMorphChannelKind.Custom)
                .ToDictionary(
                    item => item.Name,
                    StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in
                 request.Proposal.SolverResult.CustomMorphs
                     .OrderBy(item =>
                         authority.TryGetValue(
                             item.Key,
                             out ReferenceMorphChannelAuthority? channel)
                             ? channel.Ordinal
                             : int.MaxValue)
                     .ThenBy(item => item.Key,
                         StringComparer.Ordinal))
        {
            if (!authority.ContainsKey(name) ||
                !double.IsFinite(value) ||
                value is < -1 or > 1)
            {
                diagnostics.Add(Error(
                    "reference-preset-custom",
                    $"Solved custom control '{name}' is not present in the exact legal catalog."));
                continue;
            }
            float converted = checked((float)value);
            values[name] = converted;
            ordered.Add(new SkyrimRaceMenuCustomMorphValue(
                name, converted));
        }
        return (
            values.ToImmutableDictionary(
                StringComparer.Ordinal),
            ordered.ToImmutableArray());
    }

    private static ImmutableArray<RaceMenuSculptPart> BuildSculpt(
        ReferenceRaceMenuPresetWriteRequest request,
        RaceMenuPresetData raceMenu,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ReferencePresetResourceSnapshot snapshot =
            request.Snapshot!;
        HashSet<string> knownHosts = snapshot.MorphBases
            .Select(item => ResolveSculptHost(
                item.PlanTemplate))
            .Where(item => item.Length > 0)
            .Select(NormalizeAssetIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = raceMenu.SculptParts
            .Where(item => !knownHosts.Contains(
                NormalizeAssetIdentity(item.Host)))
            .ToList();
        ReferenceRaceMenuPresetSolverResult solution =
            request.Proposal.SolverResult;
        if (solution.Sculpt.IsEmpty)
            return output.ToImmutableArray();

        ReferenceFaceMorphShapeBasis[] candidates =
            snapshot.MorphBases.Where(item =>
                    (solution.SculptNifIdentity.Length == 0 ||
                     item.NifIdentity ==
                     solution.SculptNifIdentity) &&
                    (solution.SculptShapeIdentity.Length == 0 ||
                     item.ShapeIdentity ==
                     solution.SculptShapeIdentity))
                .ToArray();
        if (candidates.Length != 1)
        {
            diagnostics.Add(Error(
                "reference-preset-sculpt-authority",
                "Solved sculpt does not resolve to exactly one snapshot face basis."));
            return output.ToImmutableArray();
        }
        ReferenceFaceMorphShapeBasis basis = candidates[0];
        string host = solution.SculptHost.Length > 0
            ? solution.SculptHost
            : ResolveSculptHost(basis.PlanTemplate);
        if (host.Length == 0 ||
            solution.Sculpt.Any(item =>
                item.VertexIndex < 0 ||
                item.VertexIndex >=
                    basis.PlanTemplate.VertexCount ||
                !double.IsFinite(item.X) ||
                !double.IsFinite(item.Y) ||
                !double.IsFinite(item.Z)))
        {
            diagnostics.Add(Error(
                "reference-preset-sculpt",
                "Solved sculpt host or vertex data is invalid."));
            return output.ToImmutableArray();
        }
        output.Add(new RaceMenuSculptPart(
            host,
            basis.PlanTemplate.VertexCount,
            solution.Sculpt
                .OrderBy(item => item.VertexIndex)
                .Select(item => new RaceMenuSculptVertex(
                    item.VertexIndex,
                    checked((float)item.X),
                    checked((float)item.Y),
                    checked((float)item.Z)))
                .ToImmutableArray(),
            true,
            true));
        return output.ToImmutableArray();
    }

    private static RaceMenuPresetData EmptyRaceMenu() =>
        new(
            null,
            [],
            10_000,
            [],
            ImmutableDictionary<string,
                ImmutableDictionary<string, float>>.Empty,
            [],
            [],
            []);

    private static string ResolveSculptHost(
        SkyrimFaceMorphPlanBuildRequest template) =>
        template.ChargenMorphTri?.Document.SourcePath.Value ??
        template.RaceMorphTri?.Document.SourcePath.Value ??
        template.MeshMorphTri?.Document.SourcePath.Value ??
        string.Empty;

    private static string NormalizeAssetIdentity(string value)
    {
        string normalized = value.Trim()
            .Replace('\\', '/')
            .TrimStart('/');
        if (normalized.StartsWith(
                "meshes/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..];
        return normalized;
    }

    private static string PortableIdentifier(
        FormReference reference) =>
        reference.Plugin.Value + "|" +
        reference.FormId.Value.ToString(
            "X6", CultureInfo.InvariantCulture);

    private static bool TryParseIndexed(
        string name,
        string prefix,
        int count,
        out int index)
    {
        index = -1;
        if (!name.StartsWith(
                prefix + "[", StringComparison.Ordinal) ||
            !name.EndsWith(']'))
            return false;
        return int.TryParse(
                   name.AsSpan(
                       prefix.Length + 1,
                       name.Length - prefix.Length - 2),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out index) &&
               index >= 0 &&
               index < count;
    }

    private static ReferencePresetWriteArtifact Refused(
        ReferenceRaceMenuPresetWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            request.DestinationPath,
            new Sha256Hash(new string('0', 64)),
            request.Baseline,
            new Sha256Hash(new string('0', 64)),
            diagnostics.ToImmutable());

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));

    private static Sha256Hash Hash(params string[] values)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            // The caller receives the primary refusal; uniquely named
            // temporary artifacts are never returned as successful output.
        }
    }
}
