using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>Pure parser for RaceMenu races.ini, .slider/.ini, and morphs.ini catalog state.</summary>
public sealed class RaceMenuSliderCatalogParserCore : IRaceMenuSliderCatalogParserCore
{
    private const string FaceGenMorphsRoot = "meshes/actors/character/FaceGenMorphs";
    private const string ExtendedMorphsRoot = FaceGenMorphsRoot + "/morphs";

    private static readonly char[] Windows1252Controls =
    [
        '\u20AC', '\0', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\0', '\u017D', '\0',
        '\0', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\0', '\u017E', '\u0178'
    ];

    public SkyrimRaceMenuCatalogParseResult Parse(SkyrimRaceMenuCatalogParseRequest request)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Dictionary<string, SkyrimRaceMenuCatalogAsset> assets =
            BuildAssetMap(request.Assets, diagnostics);
        if (request.LoadedPlugins.IsDefault)
        {
            diagnostics.Add(Error("racemenu-catalog-plugin-order",
                "LoadedPlugins must be an explicit ordered array."));
        }

        HashSet<string> pluginNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginName plugin in request.LoadedPlugins.IsDefault
                     ? ImmutableArray<PluginName>.Empty
                     : request.LoadedPlugins)
        {
            if (!pluginNames.Add(plugin.Value))
            {
                diagnostics.Add(Error("racemenu-catalog-plugin-order",
                    $"Loaded plugin '{plugin}' occurs more than once."));
            }
        }

        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        Dictionary<string, SkyrimRaceMenuSliderDefinition> sliders =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<AssetPath>> morphMap = new(StringComparer.OrdinalIgnoreCase);
        var observedMorphs = ImmutableArray.CreateBuilder<SkyrimRaceMenuMorphDependencyObservation>();
        Dictionary<string, ImmutableArray<ParsedSlider>> sliderFileCache =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (PluginName plugin in request.LoadedPlugins)
        {
            AssetPath racesPath = CreatePath($"{FaceGenMorphsRoot}/{plugin.Value}/races.ini");
            if (assets.TryGetValue(racesPath.Value, out SkyrimRaceMenuCatalogAsset? racesAsset))
            {
                ParseRaces(racesAsset, plugin, assets, sliderFileCache, sliders, diagnostics);
            }

            AssetPath morphsPath = CreatePath($"{FaceGenMorphsRoot}/{plugin.Value}/morphs.ini");
            if (assets.TryGetValue(morphsPath.Value, out SkyrimRaceMenuCatalogAsset? morphsAsset))
            {
                // A sibling's refusal must not hide declarations in this readable file.
                var morphDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                ParseMorphs(morphsAsset, morphMap, morphDiagnostics, observedMorphs);
                diagnostics.AddRange(morphDiagnostics);
            }
        }

        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics) with { ObservedMorphDependencies = observedMorphs.ToImmutable() };
        }

        ImmutableArray<SkyrimRaceMenuSliderDefinition> orderedSliders = sliders.Values
            .OrderBy(item => item.RaceEditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Gender)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<SkyrimRaceMenuMorphExtension> extensions = morphMap
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SkyrimRaceMenuMorphExtension(
                new AssetPath(item.Key), item.Value.ToImmutableArray()))
            .ToImmutableArray();
        SkyrimRaceMenuSliderCatalog catalog = new(orderedSliders, extensions);
        return new SkyrimRaceMenuCatalogParseResult(true, catalog, diagnostics.ToImmutable())
        {
            ObservedMorphDependencies = observedMorphs.ToImmutable()
        };
    }

    private static Dictionary<string, SkyrimRaceMenuCatalogAsset> BuildAssetMap(
        ImmutableArray<SkyrimRaceMenuCatalogAsset> source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, SkyrimRaceMenuCatalogAsset> result =
            new(StringComparer.OrdinalIgnoreCase);
        if (source.IsDefault)
        {
            diagnostics.Add(Error("racemenu-catalog-assets",
                "Catalog assets must be an explicit winner-resolved array."));
            return result;
        }

        foreach (SkyrimRaceMenuCatalogAsset asset in source)
        {
            if (asset.Bytes.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error("racemenu-catalog-asset-empty",
                    $"Catalog asset '{asset.Path}' is empty."));
                continue;
            }

            Sha256Hash actual = new(Convert.ToHexString(SHA256.HashData(asset.Bytes.AsSpan())));
            if (actual != asset.ExpectedSha256)
            {
                diagnostics.Add(Error("racemenu-catalog-asset-hash",
                    $"Catalog asset '{asset.Path}' hash {actual} does not match {asset.ExpectedSha256}."));
                continue;
            }

            if (!result.TryAdd(asset.Path.Value, asset))
            {
                diagnostics.Add(Error("racemenu-catalog-asset-duplicate",
                    $"Catalog asset '{asset.Path}' has more than one winner."));
            }
        }

        return result;
    }

    private static void ParseRaces(
        SkyrimRaceMenuCatalogAsset racesAsset,
        PluginName plugin,
        Dictionary<string, SkyrimRaceMenuCatalogAsset> assets,
        IDictionary<string, ImmutableArray<ParsedSlider>> sliderFileCache,
        Dictionary<string, SkyrimRaceMenuSliderDefinition> sliders,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string text = Decode(racesAsset, diagnostics);
        if (HasErrors(diagnostics))
        {
            return;
        }

        foreach ((string raw, int lineNumber) in EnumerateLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string raceEditorId = line[..equals].Trim();
            if (!IsSafeToken(raceEditorId))
            {
                diagnostics.Add(Error("racemenu-catalog-race",
                    $"'{racesAsset.Path}' line {lineNumber} has an invalid race EditorID."));
                continue;
            }

            foreach (string rawFile in line[(equals + 1)..].Split(','))
            {
                string file = rawFile.Trim();
                if (file.Length == 0)
                {
                    continue;
                }

                bool sharedRoot = file[0] == ':';
                if (sharedRoot)
                {
                    file = file[1..].Trim();
                }

                AssetPath sliderPath;
                try
                {
                    sliderPath = CreatePath(sharedRoot
                        ? $"{FaceGenMorphsRoot}/{file}"
                        : $"{FaceGenMorphsRoot}/{plugin.Value}/{file}");
                }
                catch (ArgumentException)
                {
                    diagnostics.Add(Error("racemenu-catalog-slider-path",
                        $"'{racesAsset.Path}' line {lineNumber} contains an unsafe slider path."));
                    continue;
                }

                if (!assets.TryGetValue(sliderPath.Value, out SkyrimRaceMenuCatalogAsset? sliderAsset))
                {
                    diagnostics.Add(Error("racemenu-catalog-slider-missing",
                        $"'{racesAsset.Path}' line {lineNumber} references missing '{sliderPath}'."));
                    continue;
                }

                if (!sliderFileCache.TryGetValue(sliderPath.Value,
                        out ImmutableArray<ParsedSlider> parsedSliders))
                {
                    parsedSliders = ParseSliderFile(sliderAsset, diagnostics);
                    sliderFileCache[sliderPath.Value] = parsedSliders;
                }

                foreach (ParsedSlider parsed in parsedSliders)
                {
                    SkyrimRaceMenuSliderDefinition definition = new(raceEditorId, parsed.Gender,
                        parsed.Name, parsed.Category, parsed.Type, parsed.LowerBound,
                        parsed.UpperBound, parsed.PresetCount, plugin, sliderPath, parsed.SourceLine);
                    sliders[SliderKey(raceEditorId, parsed.Gender, parsed.Name)] = definition;
                }
            }
        }
    }

    private static ImmutableArray<ParsedSlider> ParseSliderFile(
        SkyrimRaceMenuCatalogAsset asset,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string text = Decode(asset, diagnostics);
        if (HasErrors(diagnostics))
        {
            return ImmutableArray<ParsedSlider>.Empty;
        }

        ImmutableArray<ParsedSlider>.Builder result = ImmutableArray.CreateBuilder<ParsedSlider>();
        SkyrimRaceMenuSliderGender gender = SkyrimRaceMenuSliderGender.Male;
        foreach ((string raw, int lineNumber) in EnumerateLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                string section = line[1..];
                if (section.StartsWith("Male", StringComparison.OrdinalIgnoreCase))
                {
                    gender = SkyrimRaceMenuSliderGender.Male;
                }
                else if (section.StartsWith("Female", StringComparison.OrdinalIgnoreCase))
                {
                    gender = SkyrimRaceMenuSliderGender.Female;
                }

                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = line[..equals].Trim();
            string[] parameters = line[(equals + 1)..].Split(',')
                .Select(item => item.Trim()).ToArray();
            if (!IsSafeToken(name) || parameters.Length < 3 ||
                !int.TryParse(parameters[0], out int categoryValue))
            {
                continue;
            }

            if (categoryValue == -1)
            {
                categoryValue = (int)SkyrimRaceMenuSliderCategory.Extra;
            }

            if (!Enum.IsDefined(typeof(SkyrimRaceMenuSliderCategory), categoryValue))
            {
                continue;
            }

            SkyrimRaceMenuSliderCategory category = (SkyrimRaceMenuSliderCategory)categoryValue;
            if (parameters[1].Equals("Slider", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters.Length < 4)
                {
                    continue;
                }

                result.Add(new ParsedSlider(gender, name, category,
                    SkyrimRaceMenuSliderType.Slider, NoneToEmpty(parameters[2]),
                    NoneToEmpty(parameters[3]), 0, lineNumber));
            }
            else if (parameters[1].Equals("Preset", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters.Length < 4)
                {
                    continue;
                }

                _ = int.TryParse(parameters[3], out int presetCount);
                result.Add(new ParsedSlider(gender, name, category,
                    SkyrimRaceMenuSliderType.Preset, parameters[2], string.Empty,
                    Math.Min(255, presetCount), lineNumber));
            }
            else if (parameters[1].Equals("HeadPart", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(parameters[2], out int presetCount);
                result.Add(new ParsedSlider(gender, name, category,
                    SkyrimRaceMenuSliderType.HeadPart, string.Empty, string.Empty,
                    presetCount, lineNumber));
            }
        }

        return result.ToImmutable();
    }

    private static void ParseMorphs(
        SkyrimRaceMenuCatalogAsset asset,
        Dictionary<string, List<AssetPath>> morphMap,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<SkyrimRaceMenuMorphDependencyObservation>.Builder observedMorphs)
    {
        string text = Decode(asset, diagnostics);
        if (HasErrors(diagnostics))
        {
            return;
        }

        foreach ((string raw, int lineNumber) in EnumerateLines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim()
                    .StartsWith("extension", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parameters = line[(equals + 1)..].Split(',')
                .Select(item => item.Trim()).ToArray();
            if (parameters.Length < 2 || parameters[0].Length == 0)
            {
                continue;
            }

            AssetPath basePath;
            try
            {
                basePath = ToMeshesPath(parameters[0]);
            }
            catch (ArgumentException)
            {
                diagnostics.Add(Error("racemenu-catalog-morph-path",
                    $"'{asset.Path}' line {lineNumber} has an unsafe base TRI path."));
                continue;
            }

            if (!morphMap.TryGetValue(basePath.Value, out List<AssetPath>? extensions))
            {
                extensions = [];
                morphMap[basePath.Value] = extensions;
            }

            for (int index = 1; index < parameters.Length; index++)
            {
                if (parameters[index].Length == 0)
                {
                    continue;
                }

                string declaredPath = $"{ExtendedMorphsRoot}/{parameters[index]}";
                observedMorphs.Add(new(basePath, declaredPath));
                AssetPath extendedPath;
                try
                {
                    extendedPath = CreatePath(declaredPath);
                }
                catch (ArgumentException)
                {
                    diagnostics.Add(Error("racemenu-catalog-morph-path",
                        $"'{asset.Path}' line {lineNumber} has an unsafe extended TRI path."));
                    continue;
                }

                if (!extensions.Any(path => path.Value.Equals(extendedPath.Value,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    extensions.Add(extendedPath);
                }
            }
        }
    }

    private static string Decode(SkyrimRaceMenuCatalogAsset asset,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        StringBuilder builder = new(asset.Bytes.Length);
        foreach (byte value in asset.Bytes)
        {
            if (value is >= 0x80 and <= 0x9F)
            {
                char mapped = Windows1252Controls[value - 0x80];
                if (mapped == '\0')
                {
                    diagnostics.Add(Error("racemenu-catalog-encoding",
                        $"Catalog asset '{asset.Path}' contains undefined Windows-1252 byte 0x{value:X2}."));
                    return string.Empty;
                }

                builder.Append(mapped);
            }
            else
            {
                builder.Append((char)value);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<(string Line, int Number)> EnumerateLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            yield return (lines[index], index + 1);
        }
    }

    private static AssetPath ToMeshesPath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        return CreatePath(normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"meshes/{normalized}");
    }

    private static AssetPath CreatePath(string value) => new(value.Replace('\\', '/'));

    private static string SliderKey(string raceEditorId, SkyrimRaceMenuSliderGender gender,
        string name) => $"{raceEditorId}\0{(int)gender}\0{name}";

    private static string NoneToEmpty(string value) =>
        value.Equals("None", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;

    private static bool IsSafeToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        !value.Any(character => char.IsControl(character) || character is '=' or ',');

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimRaceMenuCatalogParseResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record ParsedSlider(
        SkyrimRaceMenuSliderGender Gender,
        string Name,
        SkyrimRaceMenuSliderCategory Category,
        SkyrimRaceMenuSliderType Type,
        string LowerBound,
        string UpperBound,
        int PresetCount,
        int SourceLine);
}
