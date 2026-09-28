using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Pure record-graph semantics kept separate from file and hash intake.</summary>
internal static class BethesdaSkyrimFaceRecordGraphResolver
{
    private const int MaxRootHeadParts = 128;
    private const int MaxReachableHeadParts = 512;
    private const int MaxHnamEntriesPerRecord = 128;
    private const int MaxHnamDepth = 32;
    private const int MaxRaceKeywords = 256;

    public static SkyrimFaceRecordRouteResult Resolve(
        SkyrimFaceRecordRouteRequest request,
        SkyrimFaceRecordCatalog catalog,
        ImmutableArray<Diagnostic> intakeDiagnostics = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!intakeDiagnostics.IsDefault) diagnostics.AddRange(intakeDiagnostics);
        var cache = new Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart>();

        var race = GetRace(request.Race, catalog, diagnostics, "selected RACE");
        if (race is null) return Refused(diagnostics);
        var raceRoute = BuildRaceRoute(request, race, catalog, diagnostics);
        if (raceRoute is null) return Refused(diagnostics);

        var roots = MergeRoots(request, raceRoute, catalog, cache, diagnostics);
        if (roots.Length == 0)
        {
            diagnostics.Add(Error("skyrim-face-record-roots-missing",
                "The selected NPC and its RACE resolve no FaceGen head-part roots."));
        }
        if (roots.Length > MaxRootHeadParts)
        {
            diagnostics.Add(Error("skyrim-face-record-roots-limit",
                $"The merged root set exceeds the {MaxRootHeadParts}-record bound."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ValidateReachableGraph(roots, catalog, cache, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var expanded = ExpandRoots(request, raceRoute, roots, catalog, cache, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        return new SkyrimFaceRecordRouteResult(
            true,
            new SkyrimFaceRecordRoute(
                raceRoute,
                roots.Select(item => item.Reference).ToImmutableArray(),
                expanded.Legacy)
            {
                HeadPartGraph = expanded.Graph
            },
            diagnostics.ToImmutable());
    }

    private static SkyrimRaceFaceRecordRoute? BuildRaceRoute(
        SkyrimFaceRecordRouteRequest request,
        SkyrimFaceDecodedRace race,
        SkyrimFaceRecordCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var editorId = RequireEditorId(race.EditorId, $"RACE {race.Reference}", diagnostics);
        var morphEditorId = editorId;
        if (race.MorphRace is { } redirect)
        {
            var target = GetRace(redirect, catalog, diagnostics,
                $"RACE {race.Reference} NAM8 morph redirect");
            if (target is not null)
            {
                morphEditorId = RequireEditorId(target.EditorId,
                    $"morph RACE {target.Reference}", diagnostics);
            }
        }

        if (race.Keywords.Length > MaxRaceKeywords)
        {
            diagnostics.Add(Error("skyrim-face-record-keyword-limit",
                $"RACE {race.Reference} exceeds the {MaxRaceKeywords}-keyword bound."));
        }
        var keywords = ImmutableArray.CreateBuilder<SkyrimRaceKeywordRoute>(race.Keywords.Length);
        foreach (var reference in race.Keywords)
        {
            if (!catalog.Keywords.TryGetValue(SkyrimFaceRecordKey.From(reference), out var keyword) ||
                keyword.IsDeleted)
            {
                diagnostics.Add(Error("skyrim-face-record-keyword-missing",
                    $"RACE {race.Reference} references unavailable KYWD {reference}."));
                continue;
            }
            var keywordEditorId = RequireEditorId(keyword.EditorId,
                $"KYWD {keyword.Reference}", diagnostics);
            if (keywordEditorId is not null)
            {
                keywords.Add(new SkyrimRaceKeywordRoute(
                    keyword.Reference, keywordEditorId, keyword.Provider));
            }
        }

        ValidateDistinctReferences(race.MaleDefaultHeadParts,
            "male RACE default head parts", diagnostics);
        ValidateDistinctReferences(race.FemaleDefaultHeadParts,
            "female RACE default head parts", diagnostics);
        if (editorId is null || morphEditorId is null || HasErrors(diagnostics)) return null;

        var selectedDefaults = request.Sex == NpcSex.Female
            ? race.FemaleDefaultHeadParts
            : race.MaleDefaultHeadParts;
        return new SkyrimRaceFaceRecordRoute(
            race.Reference,
            race.Provider,
            editorId,
            race.MorphRace,
            morphEditorId,
            keywords.ToImmutable(),
            race.MaleDefaultHeadParts,
            race.FemaleDefaultHeadParts,
            selectedDefaults);
    }

    private static ImmutableArray<RootHeadPart> MergeRoots(
        SkyrimFaceRecordRouteRequest request,
        SkyrimRaceFaceRecordRoute race,
        SkyrimFaceRecordCatalog catalog,
        Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart> cache,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var main = new Dictionary<NpcHeadPartType, RootHeadPart>();
        var misc = new List<RootHeadPart>();
        var miscIndexes = new Dictionary<SkyrimFaceRecordKey, int>();
        var selected = new Dictionary<SkyrimFaceRecordKey, SkyrimFaceRecordHeadPartSelection>();

        if (request.SelectedHeadParts.IsDefault || request.SelectedHeadParts.Length > MaxRootHeadParts)
        {
            diagnostics.Add(Error("skyrim-face-record-selections-shape",
                $"SelectedHeadParts must be explicit and contain at most {MaxRootHeadParts} rows."));
            return [];
        }
        foreach (var selection in request.SelectedHeadParts)
        {
            var key = SkyrimFaceRecordKey.From(selection.Reference);
            if (!selected.TryAdd(key, selection))
            {
                diagnostics.Add(Error("skyrim-face-record-selection-duplicate",
                    $"Selected HDPT {selection.Reference} occurs more than once."));
            }
            ValidateRequiredRoles(selection, diagnostics);
        }

        foreach (var reference in race.SelectedGenderDefaultHeadParts)
        {
            AddRoot(reference, isSelected: false, isRaceDefault: true,
                ImmutableArray<SkyrimHdptTriRole>.Empty);
        }
        foreach (var selection in request.SelectedHeadParts)
        {
            AddRoot(selection.Reference, isSelected: true, isRaceDefault:
                race.SelectedGenderDefaultHeadParts.Any(item => SameReference(item, selection.Reference)),
                selection.RequiredTriRoles);
        }

        if (HasErrors(diagnostics)) return [];
        return main.OrderBy(item => (int)item.Key).Select(item => item.Value)
            .Concat(misc).ToImmutableArray();

        void AddRoot(FormReference reference, bool isSelected, bool isRaceDefault,
            ImmutableArray<SkyrimHdptTriRole> requiredRoles)
        {
            var part = GetHeadPart(reference, catalog, cache, diagnostics);
            if (part is null) return;
            var root = new RootHeadPart(reference, part.DeclaredType,
                isSelected, isRaceDefault, requiredRoles);
            if (part.DeclaredType != NpcHeadPartType.Misc)
            {
                if (main.TryGetValue(part.DeclaredType, out var existing) &&
                    SameReference(existing.Reference, reference))
                {
                    main[part.DeclaredType] = MergeRootFlags(existing, root);
                }
                else
                {
                    // RACE defaults are seeded first; an explicit NPC selection wins the type.
                    main[part.DeclaredType] = root;
                }
                return;
            }

            var key = SkyrimFaceRecordKey.From(reference);
            if (miscIndexes.TryGetValue(key, out var index))
            {
                misc[index] = MergeRootFlags(misc[index], root);
            }
            else
            {
                miscIndexes.Add(key, misc.Count);
                misc.Add(root);
            }
        }
    }

    private static RootHeadPart MergeRootFlags(RootHeadPart left, RootHeadPart right) =>
        left with
        {
            IsSelected = left.IsSelected || right.IsSelected,
            IsRaceDefault = left.IsRaceDefault || right.IsRaceDefault,
            RequiredRoles = right.RequiredRoles.IsEmpty ? left.RequiredRoles : right.RequiredRoles
        };

    private static void ValidateReachableGraph(
        ImmutableArray<RootHeadPart> roots,
        SkyrimFaceRecordCatalog catalog,
        Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart> cache,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var states = new Dictionary<SkyrimFaceRecordKey, VisitState>();
        var reachable = new HashSet<SkyrimFaceRecordKey>();
        foreach (var root in roots)
        {
            Visit(root.Reference, 0);
        }

        void Visit(FormReference reference, int depth)
        {
            var key = SkyrimFaceRecordKey.From(reference);
            if (states.TryGetValue(key, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    diagnostics.Add(Error("skyrim-face-record-hnam-cycle",
                        $"HNAM expansion contains a cycle at HDPT {reference}."));
                }
                return;
            }
            if (depth > MaxHnamDepth)
            {
                diagnostics.Add(Error("skyrim-face-record-hnam-depth",
                    $"HNAM expansion exceeds the {MaxHnamDepth}-edge depth bound at {reference}."));
                return;
            }
            if (!reachable.Add(key) || reachable.Count > MaxReachableHeadParts)
            {
                if (reachable.Count > MaxReachableHeadParts)
                {
                    diagnostics.Add(Error("skyrim-face-record-hnam-node-limit",
                        $"HNAM expansion exceeds the {MaxReachableHeadParts}-record bound."));
                }
                return;
            }

            states[key] = VisitState.Visiting;
            var part = GetHeadPart(reference, catalog, cache, diagnostics);
            if (part is not null)
            {
                foreach (var extra in part.ExtraParts) Visit(extra, depth + 1);
            }
            states[key] = VisitState.Visited;
        }
    }

    private static ExpansionResult ExpandRoots(
        SkyrimFaceRecordRouteRequest request,
        SkyrimRaceFaceRecordRoute race,
        ImmutableArray<RootHeadPart> roots,
        SkyrimFaceRecordCatalog catalog,
        Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart> cache,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var selected = request.SelectedHeadParts.ToDictionary(
            item => SkyrimFaceRecordKey.From(item.Reference), item => item);
        var defaults = race.SelectedGenderDefaultHeadParts
            .Select(SkyrimFaceRecordKey.From).ToHashSet();
        var rootKeys = roots.Select(item => SkyrimFaceRecordKey.From(item.Reference)).ToHashSet();
        var promotion = BuildTopLevelMiscPromotion(roots, catalog, cache, diagnostics);
        var claimedTypes = new Dictionary<SkyrimFaceRecordKey, NpcHeadPartType>();
        var queue = new Queue<ExpansionEntry>();

        foreach (var root in roots)
        {
            var key = SkyrimFaceRecordKey.From(root.Reference);
            var effective = root.DeclaredType == NpcHeadPartType.Misc && promotion.TryGetValue(key, out var promoted)
                ? promoted
                : root.DeclaredType;
            claimedTypes[key] = effective;
            queue.Enqueue(new ExpansionEntry(root.Reference, null, 0, effective));
        }

        var output = ImmutableArray.CreateBuilder<SkyrimFaceHeadPartRecordRoute>();
        var graph = ImmutableArray.CreateBuilder<SkyrimFaceHeadPartGraphRoute>();
        var emitted = new HashSet<SkyrimFaceRecordKey>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var key = SkyrimFaceRecordKey.From(current.Reference);
            if (!emitted.Add(key)) continue;
            var part = GetHeadPart(current.Reference, catalog, cache, diagnostics);
            if (part is null) continue;

            var required = selected.TryGetValue(key, out var selection)
                ? selection.RequiredTriRoles
                : ImmutableArray<SkyrimHdptTriRole>.Empty;
            foreach (var role in required)
            {
                if (!part.TriRoutes.Any(item => item.Role == role))
                {
                    diagnostics.Add(Error("skyrim-face-record-required-role-missing",
                        $"HDPT {part.Reference} does not declare required NAM0 role {(int)role} ({role})."));
                }
            }

            if (part.WinningRecordSha256 is { } winningRecordSha256 &&
                part.WinningPluginByteLength is { } winningPluginByteLength)
            {
                graph.Add(new SkyrimFaceHeadPartGraphRoute(
                    current.Reference,
                    part.Reference.Plugin,
                    part.Reference,
                    part.Provider.Plugin,
                    part.Provider.Sha256,
                    winningPluginByteLength,
                    winningRecordSha256,
                    part.EditorId,
                    part.DeclaredType,
                    current.EffectiveType,
                    part.ModelNif,
                    part.TriRoutes,
                    part.ExtraParts,
                    current.Parent,
                    current.Depth,
                    selected.ContainsKey(key),
                    defaults.Contains(key),
                    graph.Count,
                    part.AppliesToSex,
                    part.ValidRace)
                {
                    ValidRaceList = part.ValidRaceList
                });
            }

            if (part.ModelNif is { } model)
            {
                output.Add(new SkyrimFaceHeadPartRecordRoute(
                    part.Reference,
                    part.Provider,
                    part.EditorId,
                    part.DeclaredType,
                    current.EffectiveType,
                    model,
                    part.TriRoutes,
                    part.ExtraParts,
                    current.Parent,
                    current.Depth,
                    selected.ContainsKey(key),
                    defaults.Contains(key))
                {
                    TextureSet = part.TextureSet
                });
            }

            foreach (var extra in part.ExtraParts)
            {
                var child = GetHeadPart(extra, catalog, cache, diagnostics);
                if (child is null) continue;
                var effective = child.DeclaredType == NpcHeadPartType.Misc
                    ? current.EffectiveType
                    : child.DeclaredType;
                var childKey = SkyrimFaceRecordKey.From(extra);
                if (claimedTypes.TryGetValue(childKey, out var earlier))
                {
                    if (earlier != effective)
                    {
                        diagnostics.Add(Error("skyrim-face-record-hnam-type-ambiguous",
                            $"HDPT {extra} is reached with conflicting effective types " +
                            $"{earlier} and {effective}."));
                    }
                    continue;
                }
                claimedTypes[childKey] = effective;
                queue.Enqueue(new ExpansionEntry(extra,
                    rootKeys.Contains(childKey) ? null : current.Reference,
                    rootKeys.Contains(childKey) ? 0 : current.Depth + 1,
                    effective));
            }
        }

        return new ExpansionResult(output.ToImmutable(), graph.ToImmutable());
    }

    private static Dictionary<SkyrimFaceRecordKey, NpcHeadPartType> BuildTopLevelMiscPromotion(
        ImmutableArray<RootHeadPart> roots,
        SkyrimFaceRecordCatalog catalog,
        Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart> cache,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var rootKeys = roots.Select(item => SkyrimFaceRecordKey.From(item.Reference)).ToHashSet();
        var promotion = new Dictionary<SkyrimFaceRecordKey, NpcHeadPartType>();
        foreach (var root in roots.Where(item => item.DeclaredType != NpcHeadPartType.Misc))
        {
            var parent = GetHeadPart(root.Reference, catalog, cache, diagnostics);
            if (parent is null) continue;
            foreach (var extra in parent.ExtraParts)
            {
                var key = SkyrimFaceRecordKey.From(extra);
                if (!rootKeys.Contains(key)) continue;
                var child = GetHeadPart(extra, catalog, cache, diagnostics);
                if (child is null || child.DeclaredType != NpcHeadPartType.Misc) continue;
                if (promotion.TryGetValue(key, out var prior) && prior != root.DeclaredType)
                {
                    diagnostics.Add(Error("skyrim-face-record-root-type-ambiguous",
                        $"Top-level Misc HDPT {extra} is claimed by roots of types " +
                        $"{prior} and {root.DeclaredType}."));
                }
                else
                {
                    promotion.TryAdd(key, root.DeclaredType);
                }
            }
        }
        return promotion;
    }

    private static ValidatedHeadPart? GetHeadPart(
        FormReference reference,
        SkyrimFaceRecordCatalog catalog,
        Dictionary<SkyrimFaceRecordKey, ValidatedHeadPart> cache,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var errorCountBeforeValidation = CountErrors(diagnostics);
        var key = SkyrimFaceRecordKey.From(reference);
        if (cache.TryGetValue(key, out var cached)) return cached;
        if (!IsPluginLocal(reference))
        {
            diagnostics.Add(Error("skyrim-face-record-hdpt-formid",
                $"HDPT reference {reference} is not a nonzero plugin-local FormID."));
            return null;
        }
        if (!catalog.HeadParts.TryGetValue(key, out var source) || source.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-face-record-hdpt-missing",
                $"HDPT {reference} is unavailable in the explicit copied-plugin order."));
            return null;
        }

        var editorId = RequireEditorId(source.EditorId, $"HDPT {reference}", diagnostics);
        NpcHeadPartType? declaredType = source.DeclaredType is null or < 0
            ? null
            : source.DeclaredType is <= 9
                ? (NpcHeadPartType)source.DeclaredType.Value
                : NpcHeadPartType.Misc;
        if (declaredType is null)
        {
            diagnostics.Add(Error("skyrim-face-record-hdpt-type",
                $"HDPT {reference} has a missing or negative PNAM type."));
        }
        AssetPath? model = null;
        if (!string.IsNullOrWhiteSpace(source.ModelPath))
        {
            model = ToMeshAssetPath(source.ModelPath, ".nif",
                $"HDPT {reference} MODL", diagnostics);
        }
        else if (!source.TriParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("skyrim-face-record-logical-tri",
                $"Model-less logical HDPT {reference} may not declare NAM0 TRI routes."));
        }

        if (source.ExtraParts.Length > MaxHnamEntriesPerRecord)
        {
            diagnostics.Add(Error("skyrim-face-record-hnam-entry-limit",
                $"HDPT {reference} exceeds the {MaxHnamEntriesPerRecord}-entry HNAM bound."));
        }
        ValidateDistinctReferences(source.ExtraParts, $"HDPT {reference} HNAM", diagnostics);
        foreach (var extra in source.ExtraParts)
        {
            if (!IsPluginLocal(extra))
            {
                diagnostics.Add(Error("skyrim-face-record-hnam-formid",
                    $"HDPT {reference} contains invalid HNAM reference {extra}."));
            }
        }

        var triRoutes = ImmutableArray.CreateBuilder<SkyrimHdptTriRoute>();
        var seenRoles = new HashSet<SkyrimHdptTriRole>();
        foreach (var part in source.TriParts)
        {
            if (part.Role is not (>= 0 and <= 2))
            {
                diagnostics.Add(Error("skyrim-face-record-hdpt-role",
                    $"HDPT {reference} contains a missing or unsupported NAM0 role."));
                continue;
            }
            var role = (SkyrimHdptTriRole)part.Role.Value;
            if (!seenRoles.Add(role))
            {
                diagnostics.Add(Error("skyrim-face-record-hdpt-role-duplicate",
                    $"HDPT {reference} declares NAM0 role {(int)role} more than once."));
                continue;
            }
            var path = ToMeshAssetPath(part.Path, ".tri",
                $"HDPT {reference} NAM0 role {(int)role}", diagnostics);
            if (path is { } triPath) triRoutes.Add(new SkyrimHdptTriRoute(role, triPath));
        }

        if (editorId is null || declaredType is null ||
            CountErrors(diagnostics) != errorCountBeforeValidation)
        {
            return null;
        }
        var result = new ValidatedHeadPart(
            source.Reference,
            source.Provider,
            editorId,
            declaredType.Value,
            model,
            triRoutes.OrderBy(item => (int)item.Role).ToImmutableArray(),
            source.ExtraParts,
            ResolveTextureSet(source, catalog, diagnostics),
            source.WinningRecordSha256,
            source.WinningPluginByteLength,
            source.SupportsMale && !source.SupportsFemale
                ? NpcSex.Male
                : source.SupportsFemale && !source.SupportsMale
                    ? NpcSex.Female
                    : null,
            source.ValidRaces,
            ResolveValidRaceList(source.ValidRaces, catalog));
        if (catalog.RequireWinningRecordEvidence &&
            (result.WinningRecordSha256 is null ||
             result.WinningPluginByteLength is not { } length || length <= 0))
        {
            diagnostics.Add(Error("skyrim-face-record-hdpt-digest",
                $"HDPT {reference} has no winning raw record digest evidence."));
        }
        if (CountErrors(diagnostics) != errorCountBeforeValidation)
            return null;
        cache.Add(key, result);
        return result;
    }

    private static SkyrimFaceFormListRoute? ResolveValidRaceList(
        FormReference? reference,
        SkyrimFaceRecordCatalog catalog)
    {
        if (reference is not { } listReference ||
            !catalog.FormLists.TryGetValue(
                SkyrimFaceRecordKey.From(listReference),
                out var list))
            return null;

        return new SkyrimFaceFormListRoute(
            list.Reference,
            list.Provider,
            list.Items,
            list.IsDeleted);
    }

    private static SkyrimFaceTextureSetRecordRoute? ResolveTextureSet(
        SkyrimFaceDecodedHeadPart headPart,
        SkyrimFaceRecordCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (headPart.TextureSet is not { } reference) return null;
        if (!IsPluginLocal(reference) ||
            catalog.TextureSets is null ||
            !catalog.TextureSets.TryGetValue(
                SkyrimFaceRecordKey.From(reference), out var textureSet) ||
            textureSet.IsDeleted)
        {
            diagnostics.Add(Error("headpart-texture-set-unavailable",
                $"HDPT {headPart.Reference} declares TNAM {reference}, but its winning TXST is unavailable."));
            return null;
        }
        if (textureSet.RawTxSlots.Length != 8)
        {
            diagnostics.Add(Error("headpart-texture-set-shape",
                $"Winning TXST {reference} does not expose exactly eight TX00-TX07 slots."));
            return null;
        }
        return new SkyrimFaceTextureSetRecordRoute(
            textureSet.Reference, textureSet.Provider, textureSet.RawTxSlots);
    }

    private static SkyrimFaceDecodedRace? GetRace(
        FormReference reference,
        SkyrimFaceRecordCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        if (!IsPluginLocal(reference))
        {
            diagnostics.Add(Error("skyrim-face-record-race-formid",
                $"The {role} reference {reference} is not a nonzero plugin-local FormID."));
            return null;
        }
        if (!catalog.Races.TryGetValue(SkyrimFaceRecordKey.From(reference), out var race) ||
            race.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-face-record-race-missing",
                $"The {role} {reference} is unavailable in the explicit copied-plugin order."));
            return null;
        }
        return race;
    }

    private static AssetPath? ToMeshAssetPath(
        string? raw,
        string expectedExtension,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024 || raw.Any(char.IsControl))
        {
            diagnostics.Add(Error("skyrim-face-record-asset-path",
                $"{role} has no safe record-declared asset path."));
            return null;
        }
        var normalized = raw.Trim().Replace('\\', '/');
        if (!normalized.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("skyrim-face-record-asset-extension",
                $"{role} must declare a '{expectedExtension}' asset."));
            return null;
        }
        if (!normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "meshes/" + normalized;
        }
        try
        {
            return new AssetPath(normalized);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("skyrim-face-record-asset-path",
                $"{role} is invalid: {exception.Message}"));
            return null;
        }
    }

    private static void ValidateRequiredRoles(
        SkyrimFaceRecordHeadPartSelection selection,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (selection.RequiredTriRoles.IsDefault)
        {
            diagnostics.Add(Error("skyrim-face-record-required-roles-shape",
                $"HDPT {selection.Reference} required roles must be explicit, even when empty."));
            return;
        }
        var seen = new HashSet<SkyrimHdptTriRole>();
        foreach (var role in selection.RequiredTriRoles)
        {
            if (!Enum.IsDefined(role) || !seen.Add(role))
            {
                diagnostics.Add(Error("skyrim-face-record-required-role-invalid",
                    $"HDPT {selection.Reference} has an unsupported or duplicate required role '{role}'."));
            }
        }
    }

    private static void ValidateDistinctReferences(
        ImmutableArray<FormReference> references,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<SkyrimFaceRecordKey>();
        foreach (var reference in references)
        {
            if (!seen.Add(SkyrimFaceRecordKey.From(reference)))
            {
                diagnostics.Add(Error("skyrim-face-record-reference-duplicate",
                    $"{role} contains duplicate reference {reference}."));
            }
        }
    }

    private static string? RequireEditorId(
        string? value,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value != value.Trim() ||
            value.Any(char.IsControl))
        {
            diagnostics.Add(Error("skyrim-face-record-editorid",
                $"{role} has no safe EditorID."));
            return null;
        }
        return value;
    }

    private static bool IsPluginLocal(FormReference reference) =>
        !string.IsNullOrWhiteSpace(reference.Plugin.Value) &&
        reference.FormId.Value is > 0 and <= 0x00FF_FFFF;

    private static bool SameReference(FormReference left, FormReference right) =>
        SkyrimFaceRecordKey.From(left) == SkyrimFaceRecordKey.From(right);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static int CountErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimFaceRecordRouteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private sealed record RootHeadPart(
        FormReference Reference,
        NpcHeadPartType DeclaredType,
        bool IsSelected,
        bool IsRaceDefault,
        ImmutableArray<SkyrimHdptTriRole> RequiredRoles);

    private sealed record ExpansionEntry(
        FormReference Reference,
        FormReference? Parent,
        int Depth,
        NpcHeadPartType EffectiveType);

    private sealed record ExpansionResult(
        ImmutableArray<SkyrimFaceHeadPartRecordRoute> Legacy,
        ImmutableArray<SkyrimFaceHeadPartGraphRoute> Graph);

    private sealed record ValidatedHeadPart(
        FormReference Reference,
        SkyrimFaceRecordProvider Provider,
        string EditorId,
        NpcHeadPartType DeclaredType,
        AssetPath? ModelNif,
        ImmutableArray<SkyrimHdptTriRoute> TriRoutes,
        ImmutableArray<FormReference> ExtraParts,
        SkyrimFaceTextureSetRecordRoute? TextureSet,
        Sha256Hash? WinningRecordSha256,
        long? WinningPluginByteLength,
        NpcSex? AppliesToSex,
        FormReference? ValidRace,
        SkyrimFaceFormListRoute? ValidRaceList);
}
