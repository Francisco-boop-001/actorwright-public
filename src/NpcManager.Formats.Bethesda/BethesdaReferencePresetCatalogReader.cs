using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reopens an exact reviewed plugin order and binds one user-reviewed
/// reference-preset catalog selection to winning Skyrim RACE/HDPT/tint facts.
/// It performs no headpart selection and never consults a live install.
/// </summary>
public sealed class BethesdaReferencePresetCatalogReader(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader,
    ISkyrimRaceTintAuthorityReader tintAuthorityReader)
    : IBethesdaReferencePresetCatalogReader
{
    private const int MaximumHeadPartClosure = 64;

    public async ValueTask<ReferencePresetCatalogReadResult> ReadAsync(
        ReferencePresetCatalogReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Selection.ReviewAccepted)
        {
            diagnostics.Add(Error("reference-catalog-review",
                "The headpart and tint catalog selection has not been reviewed."));
            return Refused(diagnostics);
        }
        if (request.Target.Sex is not (NpcSex.Male or NpcSex.Female) ||
            request.Target.Race.FormId.Value is 0 or > 0x00FF_FFFF ||
            string.IsNullOrWhiteSpace(request.Target.Race.Plugin.Value))
        {
            diagnostics.Add(Error("reference-catalog-target",
                "The target must identify one supported Skyrim race and sex."));
            return Refused(diagnostics);
        }

        SkyrimFaceRecordPluginAuthorityResult current =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.Target.DataRoot,
                    request.Target.PluginOrder.Select(item => item.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(current.Diagnostics);
        if (!current.Accepted ||
            !MatchesReviewedAuthorities(current.Authorities, request.Target.PluginOrder))
        {
            if (current.Accepted)
            {
                diagnostics.Add(Error("reference-catalog-plugin-order-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        SkyrimFaceRecordCatalog catalog;
        try
        {
            catalog = BethesdaSkyrimFaceRecordCatalogLoader.Load(
                current.Authorities, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("reference-catalog-plugin-malformed",
                $"The reviewed Skyrim catalog could not be decoded: {exception.Message}"));
            return Refused(diagnostics);
        }

        if (!catalog.Races.TryGetValue(
                SkyrimFaceRecordKey.From(request.Target.Race),
                out SkyrimFaceDecodedRace? race) || race.IsDeleted)
        {
            diagnostics.Add(Error("reference-catalog-race-missing",
                $"Winning RACE {request.Target.Race} is unavailable."));
            return Refused(diagnostics);
        }
        if (string.IsNullOrWhiteSpace(race.EditorId))
        {
            diagnostics.Add(Error("reference-catalog-race-editor-id",
                $"Winning RACE {request.Target.Race} has no EditorID."));
        }
        SkyrimFaceDecodedRace morphRace = race;
        if (race.MorphRace is { } morphReference)
        {
            if (!catalog.Races.TryGetValue(
                    SkyrimFaceRecordKey.From(morphReference),
                    out SkyrimFaceDecodedRace? resolvedMorphRace) ||
                resolvedMorphRace.IsDeleted ||
                string.IsNullOrWhiteSpace(resolvedMorphRace.EditorId))
            {
                diagnostics.Add(Error("reference-catalog-morph-race",
                    $"RACE {request.Target.Race} has an unresolved NAM8 morph-race authority."));
            }
            else
            {
                morphRace = resolvedMorphRace;
            }
        }
        var raceKeywords = ImmutableArray.CreateBuilder<string>(
            race.Keywords.Length);
        foreach (FormReference keywordReference in race.Keywords)
        {
            if (!catalog.Keywords.TryGetValue(
                    SkyrimFaceRecordKey.From(keywordReference),
                    out SkyrimFaceDecodedKeyword? keyword) ||
                keyword.IsDeleted ||
                string.IsNullOrWhiteSpace(keyword.EditorId))
            {
                diagnostics.Add(Error("reference-catalog-race-keyword",
                    $"RACE {request.Target.Race} has an unresolved keyword {keywordReference}."));
                continue;
            }
            raceKeywords.Add(keyword.EditorId.Trim());
        }

        var selected = new[]
        {
            (request.Selection.Face, NpcHeadPartType.Face, "face"),
            (request.Selection.Mouth, NpcHeadPartType.Misc, "mouth"),
            (request.Selection.Eyes, NpcHeadPartType.Eyes, "eyes"),
            (request.Selection.Brows, NpcHeadPartType.Eyebrows, "brows"),
            (request.Selection.Hair, NpcHeadPartType.Hair, "hair")
        };
        if (selected.Select(item => SkyrimFaceRecordKey.From(item.Item1))
                .Distinct().Count() != selected.Length)
        {
            diagnostics.Add(Error("reference-catalog-headpart-duplicate",
                "Face, mouth, eyes, brows, and hair must be five distinct HDPT records."));
            return Refused(diagnostics);
        }

        var headParts = ImmutableArray.CreateBuilder<ReferencePresetCatalogHeadPart>();
        var pending = new Queue<(FormReference Reference, NpcHeadPartType? RequiredType,
            string Role, bool IsRoot)>();
        foreach (var item in selected)
            pending.Enqueue((item.Item1, item.Item2, item.Item3, true));
        var visited = new HashSet<SkyrimFaceRecordKey>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pending.Dequeue();
            var key = SkyrimFaceRecordKey.From(item.Reference);
            if (!visited.Add(key)) continue;
            if (visited.Count > MaximumHeadPartClosure)
            {
                diagnostics.Add(Error("reference-catalog-headpart-limit",
                    $"The selected HDPT closure exceeds {MaximumHeadPartClosure} records."));
                break;
            }
            if (!catalog.HeadParts.TryGetValue(key, out SkyrimFaceDecodedHeadPart? part) ||
                part.IsDeleted)
            {
                diagnostics.Add(Error("reference-catalog-headpart-missing",
                    $"Selected {item.Role} HDPT {item.Reference} is unavailable."));
                continue;
            }

            NpcHeadPartType? type = part.DeclaredType is null or < 0
                ? null
                : part.DeclaredType <= 9
                    ? (NpcHeadPartType)part.DeclaredType.Value
                    : NpcHeadPartType.Misc;
            if (type is null || item.RequiredType is { } required && type != required)
            {
                diagnostics.Add(Error("reference-catalog-headpart-type",
                    $"Selected {item.Role} HDPT {item.Reference} has incompatible PNAM type."));
                continue;
            }
            if (!SupportsSex(part, request.Target.Sex) ||
                !IsRaceCompatible(part, race, request.Target.Race,
                    request.Target.Sex, catalog))
            {
                diagnostics.Add(Error("reference-catalog-headpart-compatibility",
                    $"Selected {item.Role} HDPT {item.Reference} is incompatible with {request.Target.Race} {request.Target.Sex}."));
                continue;
            }

            AssetPath model;
            try
            {
                model = CanonicalMeshPath(part.ModelPath, ".nif",
                    $"HDPT {item.Reference} model");
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("reference-catalog-headpart-model",
                    exception.Message));
                continue;
            }

            ImmutableArray<SkyrimHdptTriRoute> triRoutes;
            try
            {
                triRoutes = ReadTriRoutes(part, item.Reference);
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("reference-catalog-headpart-tri",
                    exception.Message));
                continue;
            }
            headParts.Add(new ReferencePresetCatalogHeadPart(
                part.Reference,
                part.Provider,
                part.EditorId?.Trim() ?? string.Empty,
                type.Value,
                model,
                triRoutes,
                part.ExtraParts,
                part.TextureSet,
                item.IsRoot));
            if (string.IsNullOrWhiteSpace(part.EditorId))
            {
                diagnostics.Add(Error("reference-catalog-headpart-editor-id",
                    $"HDPT {item.Reference} has no EditorID."));
            }
            foreach (FormReference extra in part.ExtraParts)
                pending.Enqueue((extra, null, $"{item.Role} extra", false));
        }

        SkyrimRaceTintAuthorityResult tintResult =
            await tintAuthorityReader.ReadAsync(
                new SkyrimRaceTintAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.Target.DataRoot,
                    request.Target.Race,
                    request.Target.Sex,
                    current.Authorities),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(tintResult.Diagnostics);
        var tints = ImmutableArray.CreateBuilder<ReferencePresetCatalogTint>(
            request.Selection.Tints.Length);
        if (tintResult.Accepted && tintResult.Authority is { } tintAuthority)
        {
            foreach (ReferenceTintSelection selection in request.Selection.Tints)
            {
                SkyrimRaceTintLayerAuthority[] matches = tintAuthority.Layers
                    .Where(layer => layer.Index == selection.TintIndex &&
                                    layer.SourceMaskType == selection.TintType)
                    .ToArray();
                if (matches.Length != 1)
                {
                    diagnostics.Add(Error("reference-catalog-tint-unavailable",
                        $"Selected tint index {selection.TintIndex} type {selection.TintType} does not identify one exact target-race row."));
                    continue;
                }
                SkyrimRaceTintLayerAuthority match = matches[0];
                tints.Add(new ReferencePresetCatalogTint(
                    selection, match.RaceOrder, match.Kind, match.MaskPath));
            }
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        return new ReferencePresetCatalogReadResult(
            new ReferencePresetCatalogAuthority(
                race.Reference,
                race.Provider,
                request.Target.Sex,
                headParts.ToImmutable(),
                tints.ToImmutable())
            {
                RaceEditorId = race.EditorId?.Trim() ?? string.Empty,
                MorphRaceEditorId =
                    morphRace.EditorId?.Trim() ?? string.Empty,
                RaceKeywordEditorIds = raceKeywords.ToImmutable()
            },
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<SkyrimHdptTriRoute> ReadTriRoutes(
        SkyrimFaceDecodedHeadPart part,
        FormReference reference)
    {
        var result = ImmutableArray.CreateBuilder<SkyrimHdptTriRoute>();
        var seen = new HashSet<SkyrimHdptTriRole>();
        foreach (SkyrimFaceDecodedTriPart source in part.TriParts)
        {
            if (source.Role is not (>= 0 and <= 2))
                throw new ArgumentException(
                    $"HDPT {reference} contains a missing or unsupported NAM0 role.");
            var role = (SkyrimHdptTriRole)source.Role.Value;
            if (!seen.Add(role))
                throw new ArgumentException(
                    $"HDPT {reference} declares NAM0 role {(int)role} more than once.");
            result.Add(new SkyrimHdptTriRoute(
                role,
                CanonicalMeshPath(source.Path, ".tri",
                    $"HDPT {reference} NAM0 role {(int)role}")));
        }
        return result.OrderBy(item => (int)item.Role).ToImmutableArray();
    }

    private static AssetPath CanonicalMeshPath(
        string? value,
        string extension,
        string label)
    {
        string normalized = (value ?? string.Empty).Trim().Replace('\\', '/')
            .TrimStart('/');
        if (!normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
            normalized = "meshes/" + normalized;
        if (!normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{label} is not a {extension} asset.");
        return new AssetPath(normalized);
    }

    private static bool SupportsSex(SkyrimFaceDecodedHeadPart part, NpcSex sex) =>
        (!part.SupportsMale && !part.SupportsFemale) ||
        sex == NpcSex.Female && part.SupportsFemale ||
        sex == NpcSex.Male && part.SupportsMale;

    private static bool IsRaceCompatible(
        SkyrimFaceDecodedHeadPart part,
        SkyrimFaceDecodedRace race,
        FormReference targetRace,
        NpcSex sex,
        SkyrimFaceRecordCatalog catalog)
    {
        ImmutableArray<FormReference> defaults = sex == NpcSex.Female
            ? race.FemaleDefaultHeadParts
            : race.MaleDefaultHeadParts;
        if (defaults.Any(item => SameReference(item, part.Reference))) return true;
        if (part.ValidRaces is { } validRaces &&
            catalog.FormLists.TryGetValue(SkyrimFaceRecordKey.From(validRaces),
                out SkyrimFaceDecodedFormList? list) &&
            !list.IsDeleted &&
            list.Items.Any(item => SameReference(item, targetRace)))
            return true;
        return part.ValidRaces is null &&
               (race.FemaleDefaultHeadParts.Length > 0 ||
                race.MaleDefaultHeadParts.Length > 0);
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
                return false;
        }
        return true;
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId &&
        string.Equals(left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferencePresetCatalogReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());
}
