using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>Builds the additive Skyrim face plan used by the pinned offline FaceGen bake.</summary>
public sealed class SseFaceMorphPlanBuilder : ISseFaceMorphPlanBuilder
{
    private const float NativeMorphEpsilon = 0.001F;
    private const float CustomMorphEpsilon = 0.0001F;
    private const float WeightMorphEpsilon = 0.0000001F;
    private const float Nam9SentinelThreshold = 3.0E+38F;

    private static readonly (string Positive, string Negative)[] Nam9MorphNames =
    [
        ("NoseLong", "NoseShort"),
        ("NoseUp", "NoseDown"),
        ("JawDown", "JawUp"),
        ("JawWide", "JawNarrow"),
        ("JawForward", "JawBack"),
        ("CheeksUp", "CheeksDown"),
        ("CheeksOut", "CheeksIn"),
        ("EyesMoveUp", "EyesMoveDown"),
        ("EyesMoveOut", "EyesMoveIn"),
        ("BrowUp", "BrowDown"),
        ("BrowOut", "BrowIn"),
        ("BrowForward", "BrowBack"),
        ("LipMoveUp", "LipMoveDown"),
        ("LipMoveOut", "LipMoveIn"),
        ("ChinWide", "ChinThin"),
        ("ChinMoveDown", "ChinMoveUp"),
        ("Underbite", "Overbite"),
        ("EyesForward", "EyesBack")
    ];

    private static readonly string[] NamaPrefixes =
    [
        "NoseType", "BrowType", "EyesType", "LipType"
    ];

    public SkyrimFaceMorphPlanBuildResult Build(SkyrimFaceMorphPlanBuildRequest request)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);

        ImmutableArray<AssetPath> expectedExtended = ResolveExpectedExtendedPaths(
            request.Catalog, request.ChargenMorphTri?.Document.SourcePath, diagnostics);
        ImmutableArray<AssetPath> availableExtended =
            ValidateUnavailableExtendedClosure(
                request.ExplicitUnavailableExtendedTris,
                expectedExtended,
                diagnostics);
        ValidateExtendedClosure(
            request.ExtendedMorphTris,
            availableExtended,
            diagnostics);

        List<SkyrimFaceMorphTriSource> orderedSources = [];
        AddSource(request.RaceMorphTri, SkyrimFaceMorphTriRole.Race, request.VertexCount,
            orderedSources, diagnostics);
        AddSource(request.ChargenMorphTri, SkyrimFaceMorphTriRole.Chargen, request.VertexCount,
            orderedSources, diagnostics);
        AddSource(request.MeshMorphTri, SkyrimFaceMorphTriRole.Mesh, request.VertexCount,
            orderedSources, diagnostics);
        foreach (SkyrimFaceMorphTriSource source in request.ExtendedMorphTris.IsDefault
                     ? ImmutableArray<SkyrimFaceMorphTriSource>.Empty
                     : request.ExtendedMorphTris)
        {
            AddSource(source, SkyrimFaceMorphTriRole.Extended, request.VertexCount,
                orderedSources, diagnostics);
        }

        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        Dictionary<string, MorphSelection> morphTable = BuildMorphTable(orderedSources);
        Dictionary<string, SkyrimRaceMenuSliderDefinition> sliderTable =
            BuildSliderTable(request.Catalog, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        List<PendingChannel> channels = [];
        List<string> resolvedCustomNames = [];
        HashSet<string> resolvedCustomNameSet = new(StringComparer.OrdinalIgnoreCase);
        AddNamedChannel(request.MorphRaceEditorId, 1F, SkyrimFaceMorphContributionKind.Race,
            "RACE.NAM8-resolved morph race", requireResolution: false, morphTable, channels,
            diagnostics);

        AddNativeMorphChannels(request, morphTable, channels, diagnostics);
        AddCustomMorphChannels(request, sliderTable, morphTable, channels, resolvedCustomNames,
            resolvedCustomNameSet, diagnostics);
        AddSculptChannel(request, channels, diagnostics);
        AddWeightChannel(request.ActorWeight, morphTable, channels, diagnostics);

        ImmutableArray<SkyrimFaceMorphChannel> deduplicated =
            DeduplicateChannels(channels, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        SkyrimFaceMorphPlan plan = new(request.VertexCount,
            orderedSources.Select(source => source.Document.SourcePath).ToImmutableArray(),
            deduplicated, resolvedCustomNames.ToImmutableArray());
        return new SkyrimFaceMorphPlanBuildResult(true, plan, diagnostics.ToImmutable());
    }

    private static void ValidateRequest(SkyrimFaceMorphPlanBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.VertexCount <= 0)
        {
            diagnostics.Add(Error("sse-face-plan-vertex-count",
                "A face morph plan requires a positive provider vertex count."));
        }

        if (!IsSafeName(request.MorphRaceEditorId))
        {
            diagnostics.Add(Error("sse-face-plan-morph-race",
                "The NAM8-resolved morph-race EditorID is absent or unsafe."));
        }

        if (request.NativeMorphs is null)
        {
            diagnostics.Add(Error("sse-face-plan-native",
                "The typed native NAM9/NAMA snapshot is absent."));
        }
        else
        {
            if (request.NativeMorphs.HasNam9 && request.NativeMorphs.Nam9Sliders.Length != 18)
            {
                diagnostics.Add(Error("sse-face-plan-nam9-count",
                    "A present NAM9 snapshot must contain exactly 18 editable sliders."));
            }

            if (request.NativeMorphs.HasNama && request.NativeMorphs.NamaValues.Length != 4)
            {
                diagnostics.Add(Error("sse-face-plan-nama-count",
                    "A present NAMA snapshot must contain exactly four type values."));
            }

            if (request.NativeMorphs.HasNam9 &&
                (request.NativeMorphs.Nam9Sliders.Any(value => !float.IsFinite(value)) ||
                 !float.IsFinite(request.NativeMorphs.Nam9Trailing)))
            {
                diagnostics.Add(Error("sse-face-plan-nam9-nonfinite",
                    "NAM9 values must be finite; float.MaxValue remains the supported sentinel."));
            }
        }

        if (!float.IsFinite(request.ActorWeight))
        {
            diagnostics.Add(Error("sse-face-plan-weight",
                "Actor weight must be finite."));
        }

        if (request.Catalog is null || request.Catalog.Sliders.IsDefault ||
            request.Catalog.MorphExtensions.IsDefault)
        {
            diagnostics.Add(Error("sse-face-plan-catalog",
                "The RaceMenu catalog must contain explicit slider and extension arrays."));
        }

        ValidateExplicitArray(request.RaceKeywordEditorIds, "race keyword EditorID",
            "sse-face-plan-keywords", diagnostics);
        ValidateExplicitArray(request.CustomMorphs, "custom morph",
            "sse-face-plan-custom-order", diagnostics);
        ValidateExplicitArray(request.SculptParts, "sculpt part",
            "sse-face-plan-sculpt", diagnostics);
        ValidateExplicitArray(request.ExtendedMorphTris, "extended TRI",
            "sse-face-plan-extended-order", diagnostics);
        ValidateExplicitArray(
            request.ExplicitUnavailableExtendedTris,
            "explicit unavailable extended TRI",
            "sse-face-plan-unavailable-extension-order",
            diagnostics);

        foreach (string keyword in request.RaceKeywordEditorIds.IsDefault
                     ? ImmutableArray<string>.Empty
                     : request.RaceKeywordEditorIds)
        {
            if (!IsSafeName(keyword))
            {
                diagnostics.Add(Error("sse-face-plan-keyword",
                    "Race keyword EditorIDs must be non-empty, bounded, and control-free."));
            }
        }

        foreach (SkyrimRaceMenuCustomMorphValue custom in request.CustomMorphs.IsDefault
                     ? ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty
                     : request.CustomMorphs)
        {
            if (!IsSafeName(custom.Name) || !float.IsFinite(custom.Value))
            {
                diagnostics.Add(Error("sse-face-plan-custom-value",
                    "Custom morph names and values must be safe and finite."));
            }
        }
    }

    private static void ValidateExplicitArray<T>(ImmutableArray<T> values, string label,
        string code, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (values.IsDefault)
        {
            diagnostics.Add(Error(code, $"The {label} array must be explicit, even when empty."));
        }
    }

    private static ImmutableArray<AssetPath> ResolveExpectedExtendedPaths(
        SkyrimRaceMenuSliderCatalog catalog,
        AssetPath? chargenPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (catalog is null || catalog.MorphExtensions.IsDefault || chargenPath is null)
        {
            return ImmutableArray<AssetPath>.Empty;
        }

        string normalizedChargen = NormalizeTriIdentity(chargenPath.Value.Value);
        SkyrimRaceMenuMorphExtension[] exact = catalog.MorphExtensions
            .Where(entry => NormalizeTriIdentity(entry.BaseChargenTri.Value)
                .Equals(normalizedChargen, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length > 1)
        {
            diagnostics.Add(Error("sse-face-plan-extension-ambiguous",
                $"Catalog contains duplicate full-path mappings for '{chargenPath}'."));
            return ImmutableArray<AssetPath>.Empty;
        }

        if (exact.Length == 1)
        {
            return exact[0].ExtendedTriPaths;
        }

        string bareName = GetFileName(normalizedChargen);
        SkyrimRaceMenuMorphExtension[] bare = catalog.MorphExtensions
            .Where(entry => IsBareCatalogKey(entry.BaseChargenTri) &&
                            GetFileName(NormalizeTriIdentity(entry.BaseChargenTri.Value))
                                .Equals(bareName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (bare.Length > 1)
        {
            diagnostics.Add(Error("sse-face-plan-extension-ambiguous",
                $"Catalog contains multiple bare-name mappings for '{bareName}'."));
            return ImmutableArray<AssetPath>.Empty;
        }

        return bare.Length == 1 ? bare[0].ExtendedTriPaths : ImmutableArray<AssetPath>.Empty;
    }

    private static bool IsBareCatalogKey(AssetPath path)
    {
        string normalized = path.Value.Replace('\\', '/');
        if (normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return !normalized.Contains('/');
    }

    private static void ValidateExtendedClosure(
        ImmutableArray<SkyrimFaceMorphTriSource> actual,
        ImmutableArray<AssetPath> expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (actual.IsDefault)
        {
            return;
        }

        if (actual.Length != expected.Length)
        {
            diagnostics.Add(Error("sse-face-plan-extension-closure",
                $"Catalog requires {expected.Length} ordered extended TRIs but {actual.Length} were supplied."));
            return;
        }

        for (int index = 0; index < expected.Length; index++)
        {
            if (!actual[index].Document.SourcePath.Value.Equals(expected[index].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("sse-face-plan-extension-closure",
                    $"Extended TRI {index} is '{actual[index].Document.SourcePath}', expected '{expected[index]}'."));
            }
        }
    }

    private static ImmutableArray<AssetPath>
        ValidateUnavailableExtendedClosure(
            ImmutableArray<AssetPath> unavailable,
            ImmutableArray<AssetPath> expected,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (unavailable.IsDefault)
            return expected;

        var unavailableSet =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        var expectedSet = expected
            .Select(item => item.Value)
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        foreach (AssetPath path in unavailable)
        {
            if (!unavailableSet.Add(path.Value) ||
                !expectedSet.Contains(path.Value))
            {
                diagnostics.Add(Error(
                    "sse-face-plan-unavailable-extension-closure",
                    $"Unavailable extended TRI '{path}' is duplicated or is not required by the exact catalog mapping."));
            }
        }

        return expected
            .Where(item =>
                !unavailableSet.Contains(item.Value))
            .ToImmutableArray();
    }

    private static void AddSource(
        SkyrimFaceMorphTriSource? source,
        SkyrimFaceMorphTriRole expectedRole,
        int expectedVertexCount,
        List<SkyrimFaceMorphTriSource> orderedSources,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (source is null)
        {
            return;
        }

        if (source.Role != expectedRole)
        {
            diagnostics.Add(Error("sse-face-plan-source-role",
                $"TRI '{source.Document.SourcePath}' has role {source.Role}, expected {expectedRole}."));
        }

        ValidateDocument(source.Document, expectedRole, expectedVertexCount, diagnostics);
        orderedSources.Add(source);
    }

    private static void ValidateDocument(SseTriHeadDocument document,
        SkyrimFaceMorphTriRole expectedRole,
        int expectedVertexCount,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!document.SourcePath.Value.EndsWith(".tri", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("sse-face-plan-source-path",
                $"Morph source '{document.SourcePath}' is not a TRI asset path."));
        }

        if (document.BaseVertices.IsDefault ||
            document.BaseVertices.Length != document.VertexCount)
        {
            diagnostics.Add(Error("sse-face-plan-source-topology",
                $"TRI '{document.SourcePath}' declares {document.VertexCount} vertices but exposes {document.BaseVertices.Length} base vertices."));
        }

        if (document.Morphs.IsDefault)
        {
            diagnostics.Add(Error("sse-face-plan-source-morph",
                $"TRI '{document.SourcePath}' has no explicit morph array."));
        }
        else if (document.VertexCount != expectedVertexCount)
        {
            if (expectedRole == SkyrimFaceMorphTriRole.Mesh && document.Morphs.IsEmpty)
            {
                diagnostics.Add(new Diagnostic("sse-face-plan-empty-source-noop",
                    DiagnosticSeverity.Warning,
                    $"Record-declared mesh TRI '{document.SourcePath}' declares zero morphs and {document.VertexCount} base vertices versus the selected-NIF {expectedVertexCount}; its BaseVertices are ignored and the selected-NIF rest positions remain authoritative."));
            }
            else
            {
                diagnostics.Add(Error("sse-face-plan-source-topology",
                    $"{expectedRole} TRI '{document.SourcePath}' has {document.VertexCount} vertices; expected {expectedVertexCount}."));
            }
        }

        if (!document.BaseVertices.IsDefault &&
            document.BaseVertices.Any(value => !IsFinite(value)))
        {
            diagnostics.Add(Error("sse-face-plan-source-nonfinite",
                $"TRI '{document.SourcePath}' has non-finite base positions."));
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (SseTriHeadMorph morph in document.Morphs.IsDefault
                     ? ImmutableArray<SseTriHeadMorph>.Empty
                     : document.Morphs)
        {
            if (!IsSafeName(morph.Name) || !names.Add(morph.Name))
            {
                diagnostics.Add(Error("sse-face-plan-source-morph",
                    $"TRI '{document.SourcePath}' has an unsafe or duplicate morph name '{morph.Name}'."));
                continue;
            }

            HashSet<int> indices = [];
            foreach (SseTriHeadVertexDelta delta in morph.Deltas)
            {
                if (delta.VertexIndex < 0 || delta.VertexIndex >= expectedVertexCount ||
                    !indices.Add(delta.VertexIndex) || !IsFinite(delta.Delta))
                {
                    diagnostics.Add(Error("sse-face-plan-source-delta",
                        $"TRI '{document.SourcePath}' morph '{morph.Name}' has an invalid vertex delta."));
                    break;
                }
            }
        }
    }

    private static Dictionary<string, MorphSelection> BuildMorphTable(
        IEnumerable<SkyrimFaceMorphTriSource> sources)
    {
        Dictionary<string, MorphSelection> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimFaceMorphTriSource source in sources)
        {
            foreach (SseTriHeadMorph morph in source.Document.Morphs)
            {
                result.TryAdd(morph.Name, new MorphSelection(morph, source));
            }
        }

        return result;
    }

    private static Dictionary<string, SkyrimRaceMenuSliderDefinition> BuildSliderTable(
        SkyrimRaceMenuSliderCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, SkyrimRaceMenuSliderDefinition> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimRaceMenuSliderDefinition slider in catalog.Sliders)
        {
            string key = SliderKey(slider.RaceEditorId, slider.Gender, slider.Name);
            if (!result.TryAdd(key, slider))
            {
                diagnostics.Add(Error("sse-face-plan-slider-ambiguous",
                    $"Catalog has duplicate slider '{slider.Name}' for {slider.RaceEditorId}/{slider.Gender}."));
            }
        }

        return result;
    }

    private static void AddNativeMorphChannels(
        SkyrimFaceMorphPlanBuildRequest request,
        IReadOnlyDictionary<string, MorphSelection> morphTable,
        ICollection<PendingChannel> channels,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimFaceMorphSnapshot native = request.NativeMorphs;
        if (native.HasNam9)
        {
            for (int index = 0; index < Nam9MorphNames.Length; index++)
            {
                float value = native.Nam9Sliders[index];
                if (MathF.Abs(value) < NativeMorphEpsilon)
                {
                    continue;
                }

                string name = value >= 0
                    ? Nam9MorphNames[index].Positive
                    : Nam9MorphNames[index].Negative;
                AddNamedChannel(name, MathF.Abs(value), SkyrimFaceMorphContributionKind.Nam9,
                    $"NAM9[{index}]", requireResolution: false, morphTable, channels, diagnostics);
            }

            float vampire = native.Nam9Trailing;
            if (MathF.Abs(vampire) >= NativeMorphEpsilon &&
                MathF.Abs(vampire) < Nam9SentinelThreshold)
            {
                AddNamedChannel("VampireMorph", MathF.Abs(vampire),
                    SkyrimFaceMorphContributionKind.Vampire, "NAM9[18]", requireResolution: false,
                    morphTable, channels, diagnostics);
            }
            else
            {
                foreach (string keyword in request.RaceKeywordEditorIds)
                {
                    AddNamedChannel(keyword + "Morph", 1F,
                        SkyrimFaceMorphContributionKind.RaceKeyword, $"RACE.KWDA:{keyword}",
                        requireResolution: false, morphTable, channels, diagnostics);
                }
            }
        }

        if (!native.HasNama)
        {
            return;
        }

        for (int index = 0; index < NamaPrefixes.Length; index++)
        {
            uint value = native.NamaValues[index];
            if (value == uint.MaxValue)
            {
                continue;
            }

            string name = value == 0
                ? "Default"
                : NamaPrefixes[index] + value.ToString(CultureInfo.InvariantCulture);
            AddNamedChannel(name, 1F, SkyrimFaceMorphContributionKind.Nama,
                $"NAMA[{index}]", requireResolution: false, morphTable, channels, diagnostics);
        }
    }

    private static void AddCustomMorphChannels(
        SkyrimFaceMorphPlanBuildRequest request,
        Dictionary<string, SkyrimRaceMenuSliderDefinition> sliderTable,
        IReadOnlyDictionary<string, MorphSelection> morphTable,
        ICollection<PendingChannel> channels,
        ICollection<string> resolvedCustomNames,
        ISet<string> resolvedCustomNameSet,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimRaceMenuSliderGender gender = request.IsFemale
            ? SkyrimRaceMenuSliderGender.Female
            : SkyrimRaceMenuSliderGender.Male;
        foreach (SkyrimRaceMenuCustomMorphValue custom in request.CustomMorphs)
        {
            if (MathF.Abs(custom.Value) < CustomMorphEpsilon)
            {
                continue;
            }

            string key = SliderKey(request.MorphRaceEditorId, gender, custom.Name);
            if (!sliderTable.TryGetValue(key, out SkyrimRaceMenuSliderDefinition? slider))
            {
                if (AddNamedChannel(custom.Name, custom.Value,
                    SkyrimFaceMorphContributionKind.RaceMenuCustom,
                    $"RaceMenu direct:{custom.Name}",
                    requireResolution: request.RequireAllCustomMorphs, morphTable,
                    channels, diagnostics))
                {
                    MarkCustomResolved(custom.Name, resolvedCustomNames, resolvedCustomNameSet);
                }

                continue;
            }

            switch (slider.Type)
            {
                case SkyrimRaceMenuSliderType.Slider:
                    {
                        string name = custom.Value < 0 ? slider.LowerBound : slider.UpperBound;
                        if (name.Length == 0 || AddNamedChannel(name, MathF.Abs(custom.Value),
                                SkyrimFaceMorphContributionKind.RaceMenuCustom,
                                $"RaceMenu slider:{custom.Name}",
                                requireResolution: request.RequireAllCustomMorphs, morphTable,
                                channels, diagnostics))
                        {
                            MarkCustomResolved(custom.Name, resolvedCustomNames, resolvedCustomNameSet);
                        }

                        break;
                    }
                case SkyrimRaceMenuSliderType.Preset:
                    {
                        double truncated = Math.Truncate((double)custom.Value);
                        if (truncated > int.MaxValue || truncated < int.MinValue)
                        {
                            diagnostics.Add(Error("sse-face-plan-custom-preset",
                                $"Preset slider '{custom.Name}' is outside the Int32 ordinal range."));
                            break;
                        }

                        int ordinal = (int)truncated;
                        if (ordinal <= 0 || slider.LowerBound.Length == 0)
                        {
                            MarkCustomResolved(custom.Name, resolvedCustomNames, resolvedCustomNameSet);
                        }
                        else
                        {
                            string name = slider.LowerBound + ordinal.ToString(CultureInfo.InvariantCulture);
                            if (AddNamedChannel(name, 1F,
                                SkyrimFaceMorphContributionKind.RaceMenuCustom,
                                $"RaceMenu preset:{custom.Name}",
                                requireResolution: request.RequireAllCustomMorphs, morphTable,
                                channels, diagnostics))
                            {
                                MarkCustomResolved(custom.Name, resolvedCustomNames,
                                    resolvedCustomNameSet);
                            }
                        }

                        break;
                    }
                case SkyrimRaceMenuSliderType.HeadPart:
                    MarkCustomResolved(custom.Name, resolvedCustomNames, resolvedCustomNameSet);
                    break;
                default:
                    diagnostics.Add(Error("sse-face-plan-slider-type",
                        $"Slider '{custom.Name}' has unsupported type {slider.Type}."));
                    break;
            }
        }
    }

    private static void MarkCustomResolved(string name, ICollection<string> ordered,
        ISet<string> seen)
    {
        if (seen.Add(name))
        {
            ordered.Add(name);
        }
    }

    private static void AddSculptChannel(
        SkyrimFaceMorphPlanBuildRequest request,
        ICollection<PendingChannel> channels,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> declaredHosts = EnumerateTriSources(request)
            .Select(source => NormalizeTriIdentity(source.Document.SourcePath.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RaceMenuSculptPart[] matching = request.SculptParts
            .Where(part => !string.IsNullOrWhiteSpace(part.Host) &&
                           declaredHosts.Contains(NormalizeTriIdentity(part.Host)))
            .ToArray();
        if (matching.Length > 1)
        {
            diagnostics.Add(Error("sse-face-plan-sculpt-ambiguous",
                "More than one sculpt block matches the shape's declared TRI inputs."));
            return;
        }

        if (matching.Length == 0 || matching[0].Vertices.IsDefaultOrEmpty)
        {
            return;
        }

        RaceMenuSculptPart sculpt = matching[0];
        if (!sculpt.HasVertexCount || !sculpt.HasData || sculpt.VertexCount != request.VertexCount)
        {
            diagnostics.Add(Error("sse-face-plan-sculpt-topology",
                $"Sculpt host '{sculpt.Host}' does not bind explicit {request.VertexCount}-vertex data."));
            return;
        }

        HashSet<int> indices = [];
        ImmutableArray<SseTriHeadVertexDelta>.Builder deltas =
            ImmutableArray.CreateBuilder<SseTriHeadVertexDelta>(sculpt.Vertices.Length);
        foreach (RaceMenuSculptVertex vertex in sculpt.Vertices)
        {
            Vector3 delta = new(vertex.Dx, vertex.Dy, vertex.Dz);
            if (vertex.Index < 0 || vertex.Index >= request.VertexCount ||
                !indices.Add(vertex.Index) || !IsFinite(delta))
            {
                diagnostics.Add(Error("sse-face-plan-sculpt-delta",
                    $"Sculpt host '{sculpt.Host}' has an invalid or duplicate vertex delta."));
                return;
            }

            deltas.Add(new SseTriHeadVertexDelta(vertex.Index, delta));
        }

        channels.Add(new PendingChannel("RaceMenuSculpt", 1F, deltas.MoveToImmutable(),
            null, null, new SkyrimFaceMorphContribution(
                SkyrimFaceMorphContributionKind.RaceMenuSculpt,
                $"RaceMenu sculpt:{sculpt.Host}", 1F)));
    }

    private static IEnumerable<SkyrimFaceMorphTriSource> EnumerateTriSources(
        SkyrimFaceMorphPlanBuildRequest request)
    {
        if (request.RaceMorphTri is not null) yield return request.RaceMorphTri;
        if (request.ChargenMorphTri is not null) yield return request.ChargenMorphTri;
        if (request.MeshMorphTri is not null) yield return request.MeshMorphTri;
        if (!request.ExtendedMorphTris.IsDefault)
        {
            foreach (SkyrimFaceMorphTriSource source in request.ExtendedMorphTris)
            {
                if (source is not null) yield return source;
            }
        }
    }

    private static void AddWeightChannel(
        float actorWeight,
        IReadOnlyDictionary<string, MorphSelection> morphTable,
        ICollection<PendingChannel> channels,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        float skinnyFraction = 1F - Math.Clamp(actorWeight, 0F, 100F) / 100F;
        if (skinnyFraction > WeightMorphEpsilon)
        {
            AddNamedChannel("SkinnyMorph", skinnyFraction,
                SkyrimFaceMorphContributionKind.SkinnyWeight, "NAM7 actor weight",
                requireResolution: false, morphTable, channels, diagnostics);
        }
    }

    private static bool AddNamedChannel(
        string name,
        float weight,
        SkyrimFaceMorphContributionKind contributionKind,
        string detail,
        bool requireResolution,
        IReadOnlyDictionary<string, MorphSelection> morphTable,
        ICollection<PendingChannel> channels,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!float.IsFinite(weight))
        {
            diagnostics.Add(Error("sse-face-plan-channel-weight",
                $"Morph channel '{name}' has a non-finite weight."));
            return false;
        }

        if (!morphTable.TryGetValue(name, out MorphSelection? selection) ||
            selection.Morph.Deltas.IsDefaultOrEmpty)
        {
            if (requireResolution)
            {
                diagnostics.Add(Error("sse-face-plan-custom-unresolved",
                    $"Nonzero RaceMenu morph '{name}' has no merged TRI geometry."));
            }

            return false;
        }

        channels.Add(new PendingChannel(selection.Morph.Name, weight,
            selection.Morph.Deltas, selection.Source.Document.SourcePath,
            selection.Source.Role, new SkyrimFaceMorphContribution(contributionKind, detail, weight)));
        return true;
    }

    private static ImmutableArray<SkyrimFaceMorphChannel> DeduplicateChannels(
        IEnumerable<PendingChannel> source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        List<MutableChannel> ordered = [];
        Dictionary<string, MutableChannel> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (PendingChannel pending in source)
        {
            if (byName.TryGetValue(pending.Name, out MutableChannel? existing))
            {
                float sum = existing.Weight + pending.Weight;
                if (!float.IsFinite(sum))
                {
                    diagnostics.Add(Error("sse-face-plan-channel-weight",
                        $"Summed morph channel '{pending.Name}' is non-finite."));
                    continue;
                }

                existing.Weight = sum;
                existing.Contributions.Add(pending.Contribution);
            }
            else
            {
                MutableChannel created = new(pending);
                byName[pending.Name] = created;
                ordered.Add(created);
            }
        }

        return ordered.Select(item => item.ToImmutable()).ToImmutableArray();
    }

    private static string SliderKey(string raceEditorId, SkyrimRaceMenuSliderGender gender,
        string name) => $"{raceEditorId}\0{(int)gender}\0{name}";

    private static string NormalizeTriIdentity(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized;
    }

    private static string GetFileName(string value)
    {
        int slash = value.LastIndexOf('/');
        return slash < 0 ? value : value[(slash + 1)..];
    }

    private static bool IsSafeName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        !value.Any(char.IsControl);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimFaceMorphPlanBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record MorphSelection(SseTriHeadMorph Morph, SkyrimFaceMorphTriSource Source);

    private sealed record PendingChannel(
        string Name,
        float Weight,
        ImmutableArray<SseTriHeadVertexDelta> Deltas,
        AssetPath? SourcePath,
        SkyrimFaceMorphTriRole? SourceRole,
        SkyrimFaceMorphContribution Contribution);

    private sealed class MutableChannel(PendingChannel channel)
    {
        public string Name { get; } = channel.Name;

        public float Weight { get; set; } = channel.Weight;

        public ImmutableArray<SseTriHeadVertexDelta> Deltas { get; } = channel.Deltas;

        public AssetPath? SourcePath { get; } = channel.SourcePath;

        public SkyrimFaceMorphTriRole? SourceRole { get; } = channel.SourceRole;

        public List<SkyrimFaceMorphContribution> Contributions { get; } = [channel.Contribution];

        public SkyrimFaceMorphChannel ToImmutable() => new(Name, Weight, Deltas,
            SourcePath, SourceRole, Contributions.ToImmutableArray());
    }
}
