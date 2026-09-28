using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves one immutable Skyrim HDPT catalog from explicit hash-bound copied
/// plugin authorities. The service never opens the live game or writes files.
/// </summary>
public sealed class BethesdaSkyrimHeadPartChoiceService(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader) : ISkyrimHeadPartChoiceService
{
    private const int MaximumSearchLength = 256;

    public async ValueTask<SkyrimHeadPartChoiceResult> SearchAsync(
        SkyrimHeadPartChoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.PluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "headpart-choice-plugin-order-empty",
                "An explicit hash-bound copied-plugin order is required."));
            return Refused(diagnostics);
        }
        if (!Enum.IsDefined(request.Sex) || !Enum.IsDefined(request.Type))
        {
            diagnostics.Add(Error(
                "headpart-choice-type-invalid",
                "NPC sex and requested head-part type must be recognized typed values."));
            return Refused(diagnostics);
        }
        string search = request.Search?.Trim() ?? string.Empty;
        if (search.Length > MaximumSearchLength)
        {
            diagnostics.Add(Error(
                "headpart-choice-search-length",
                $"Head-part search text may contain at most {MaximumSearchLength} characters."));
            return Refused(diagnostics);
        }

        SkyrimFaceRecordPluginAuthorityResult authority =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.DataRoot,
                    request.PluginOrder),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(authority.Diagnostics);
        if (!authority.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordCatalog catalog;
        try
        {
            catalog = BethesdaSkyrimFaceRecordCatalogLoader.Load(
                authority.Authorities,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           KeyNotFoundException)
        {
            diagnostics.Add(Error(
                "headpart-choice-catalog-read-failed",
                $"The copied Skyrim head-part authority could not be read: {exception.Message}"));
            return Refused(diagnostics);
        }

        if (!catalog.Races.TryGetValue(
                SkyrimFaceRecordKey.From(request.Race),
                out SkyrimFaceDecodedRace? race) || race.IsDeleted)
        {
            diagnostics.Add(Error(
                "headpart-choice-race-missing",
                $"Target RACE {request.Race} is unavailable in the reviewed plugin order."));
            return Refused(diagnostics);
        }

        ImmutableArray<FormReference> selectedDefaults = request.Sex == NpcSex.Female
            ? race.FemaleDefaultHeadParts
            : race.MaleDefaultHeadParts;
        bool raceHasAnyHeadParts = race.MaleDefaultHeadParts.Length > 0 ||
                                   race.FemaleDefaultHeadParts.Length > 0;
        ImmutableArray<SkyrimHeadPartChoiceCandidate> candidates = catalog.HeadParts.Values
            .Where(item => IsCandidate(item, request.Sex, request.Type))
            .Select(item => ToCompatibleCandidate(
                item,
                request.Race,
                selectedDefaults,
                raceHasAnyHeadParts,
                catalog))
            .OfType<SkyrimHeadPartChoiceCandidate>()
            .Where(item => Matches(item, search))
            .OrderBy(item => item.EditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reference.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reference.FormId.Value)
            .ToImmutableArray();

        return new SkyrimHeadPartChoiceResult(
            true,
            candidates,
            diagnostics.ToImmutable());
    }

    private static bool IsCandidate(
        SkyrimFaceDecodedHeadPart candidate,
        NpcSex sex,
        NpcHeadPartType type)
    {
        if (candidate.IsDeleted || candidate.DeclaredType != (int)type)
            return false;
        if (type != NpcHeadPartType.Misc && candidate.IsExtra)
            return false;
        if (!candidate.SupportsMale && !candidate.SupportsFemale)
            return true;
        return sex == NpcSex.Female
            ? candidate.SupportsFemale
            : candidate.SupportsMale;
    }

    private static SkyrimHeadPartChoiceCandidate? ToCompatibleCandidate(
        SkyrimFaceDecodedHeadPart candidate,
        FormReference race,
        ImmutableArray<FormReference> selectedDefaults,
        bool raceHasAnyHeadParts,
        SkyrimFaceRecordCatalog catalog)
    {
        SkyrimHeadPartRaceMatchKind? match = null;
        if (selectedDefaults.Any(item => SameReference(item, candidate.Reference)))
        {
            match = SkyrimHeadPartRaceMatchKind.RaceDefault;
        }
        else if (candidate.ValidRaces is { } validRaces &&
                 catalog.FormLists.TryGetValue(
                     SkyrimFaceRecordKey.From(validRaces),
                     out SkyrimFaceDecodedFormList? formList) &&
                 !formList.IsDeleted &&
                 formList.Items.Any(item => SameReference(item, race)))
        {
            match = SkyrimHeadPartRaceMatchKind.ValidRaceList;
        }
        else if (candidate.ValidRaces is null && raceHasAnyHeadParts)
        {
            match = SkyrimHeadPartRaceMatchKind.HumanoidUnrestricted;
        }

        if (match is null) return null;
        AssetPath? model = null;
        if (!string.IsNullOrWhiteSpace(candidate.ModelPath))
        {
            try { model = new AssetPath(candidate.ModelPath); }
            catch (ArgumentException) { }
        }

        return new SkyrimHeadPartChoiceCandidate(
            candidate.Reference,
            candidate.EditorId ?? string.Empty,
            candidate.Name,
            (NpcHeadPartType)candidate.DeclaredType!.Value,
            candidate.IsExtra,
            candidate.SupportsMale,
            candidate.SupportsFemale,
            model,
            candidate.ExtraParts,
            BuildPreviewModels(candidate, catalog),
            candidate.Provider,
            match.Value);
    }

    private static ImmutableArray<SkyrimHeadPartPreviewModel> BuildPreviewModels(
        SkyrimFaceDecodedHeadPart root,
        SkyrimFaceRecordCatalog catalog)
    {
        const int maximumChainLength = 64;
        var result = ImmutableArray.CreateBuilder<SkyrimHeadPartPreviewModel>();
        var pending = new Queue<(SkyrimFaceDecodedHeadPart Part, bool IsRoot)>();
        var visited = new HashSet<SkyrimFaceRecordKey>();
        pending.Enqueue((root, true));
        while (pending.Count > 0 && visited.Count < maximumChainLength)
        {
            var (part, isRoot) = pending.Dequeue();
            if (!visited.Add(SkyrimFaceRecordKey.From(part.Reference)))
                continue;
            if (part.IsDeleted || string.IsNullOrWhiteSpace(part.ModelPath))
                return [];
            try
            {
                result.Add(new SkyrimHeadPartPreviewModel(
                    part.Reference,
                    new AssetPath(part.ModelPath),
                    part.Provider,
                    isRoot));
            }
            catch (ArgumentException)
            {
                return [];
            }
            foreach (FormReference extra in part.ExtraParts)
            {
                if (catalog.HeadParts.TryGetValue(
                        SkyrimFaceRecordKey.From(extra),
                        out SkyrimFaceDecodedHeadPart? child))
                {
                    pending.Enqueue((child, false));
                }
                else
                {
                    return [];
                }
            }
        }
        return pending.Count == 0 ? result.ToImmutable() : [];
    }

    private static bool Matches(SkyrimHeadPartChoiceCandidate candidate, string search) =>
        search.Length == 0 ||
        candidate.EditorId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (candidate.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        candidate.Reference.FormId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId &&
        string.Equals(
            left.Plugin.Value,
            right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimHeadPartChoiceResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
