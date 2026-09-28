using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Discovers only the RaceMenu catalog surface selected by a copied plugin
/// order. Exact loose/BSA winners and bytes remain delegated to the shared
/// authority planner and resolver.
/// </summary>
public sealed class SkyrimRaceMenuCatalogAuthorityLoader(
    IAssetIndexer assetIndexer,
    ISkyrimAssetAuthorityPlanner authorityPlanner,
    ISkyrimAssetContentResolver contentResolver,
    Action<string>? observeDependencyPath = null)
    : ISkyrimRaceMenuCatalogAuthorityLoader
{
    private const string CatalogRoot =
        "meshes/actors/character/FaceGenMorphs";
    private const int MaximumCatalogAssets = 256;

    private static readonly char[] Windows1252Controls =
    [
        '\u20AC', '\0', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\0', '\u017D', '\0',
        '\0', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\0', '\u017E', '\u0178'
    ];

    public async ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
        SkyrimRaceMenuCatalogAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        AssetIndex index;
        try
        {
            index = await assetIndexer.IndexAsync(
                new AssetIndexRequest(request.Edition, request.DataRoot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-catalog-authority-index",
                exception.Message));
            return Refused(diagnostics);
        }

        AddDistinct(diagnostics, index.Diagnostics);
        if (index.Edition != request.Edition)
        {
            diagnostics.Add(Error("racemenu-catalog-authority-edition",
                "The copied-Data index returned a different game edition."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        HashSet<string> indexedPaths = index.Providers
            .Select(item => item.Path.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configPaths = ImmutableArray.CreateBuilder<AssetPath>();
        foreach (PluginName plugin in request.LoadedPlugins)
        {
            AddIfPresent(configPaths, indexedPaths,
                $"{CatalogRoot}/{plugin.Value}/races.ini");
            AddIfPresent(configPaths, indexedPaths,
                $"{CatalogRoot}/{plugin.Value}/morphs.ini");
        }

        if (configPaths.Count == 0)
        {
            return Accepted(request.LoadedPlugins, [], [], diagnostics);
        }

        ResolvedCatalogSet configs = await ResolveAsync(
            request, configPaths.ToImmutable(), cancellationToken)
            .ConfigureAwait(false);
        AddDistinct(diagnostics, configs.Diagnostics);
        if (!configs.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        var sliderPaths = ImmutableArray.CreateBuilder<AssetPath>();
        var seenSliders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedSkyrimAssetContent config in configs.Assets)
        {
            if (!config.AssetPath.Value.EndsWith("/races.ini",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var scanDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            ScanSliderPaths(config, sliderPaths, seenSliders, scanDiagnostics, observeDependencyPath);
            AddDistinct(diagnostics, scanDiagnostics);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        if (configs.Assets.Length + sliderPaths.Count > MaximumCatalogAssets)
        {
            diagnostics.Add(Error("racemenu-catalog-authority-count",
                $"RaceMenu catalog closure exceeds {MaximumCatalogAssets} assets."));
            return Refused(diagnostics);
        }

        ResolvedCatalogSet sliders = sliderPaths.Count == 0
            ? ResolvedCatalogSet.Empty
            : await ResolveAsync(request, sliderPaths.ToImmutable(),
                    cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, sliders.Diagnostics);
        if (!sliders.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<SkyrimAssetAuthority> authorities = configs.Authorities
            .AddRange(sliders.Authorities)
            .OrderBy(item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<ResolvedSkyrimAssetContent> assets = configs.Assets
            .AddRange(sliders.Assets)
            .OrderBy(item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return Accepted(request.LoadedPlugins, authorities, assets, diagnostics);
    }

    private async ValueTask<ResolvedCatalogSet> ResolveAsync(
        SkyrimRaceMenuCatalogAuthorityRequest request,
        ImmutableArray<AssetPath> paths,
        CancellationToken cancellationToken)
    {
        SkyrimAssetAuthorityPlanResult plan = await authorityPlanner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                request.Edition, request.DataRoot, paths),
            cancellationToken).ConfigureAwait(false);
        if (!plan.Accepted || HasErrors(plan.Diagnostics))
        {
            return new ResolvedCatalogSet(false, plan.Authorities, [],
                plan.Diagnostics);
        }

        SkyrimAssetContentResolutionResult resolved = await contentResolver.ResolveAsync(
            new SkyrimAssetContentResolutionRequest(
                request.DataRoot,
                plan.Authorities.Select(ToContentAuthority)
                    .ToImmutableArray()),
            cancellationToken).ConfigureAwait(false);
        return new ResolvedCatalogSet(
            resolved.Resolved && !HasErrors(resolved.Diagnostics),
            plan.Authorities,
            resolved.Assets,
            plan.Diagnostics.AddRange(resolved.Diagnostics));
    }

    private static void ScanSliderPaths(
        ResolvedSkyrimAssetContent races,
        ImmutableArray<AssetPath>.Builder paths,
        HashSet<string> seen,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        Action<string>? observeDependencyPath)
    {
        string text = Decode(races, diagnostics);
        if (HasErrors(diagnostics)) return;
        string directory = races.AssetPath.Value[..races.AssetPath.Value.LastIndexOf('/')];
        foreach (string raw in text.Replace("\r\n", "\n",
                     StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            foreach (string candidate in line[(equals + 1)..].Split(','))
            {
                string file = candidate.Trim();
                if (file.Length == 0) continue;
                bool shared = file[0] == ':';
                if (shared) file = file[1..].Trim();
                string requestedPath = $"{(shared ? CatalogRoot : directory)}/{file}";
                observeDependencyPath?.Invoke(requestedPath);
                try
                {
                    var path = new AssetPath(requestedPath);
                    if (!path.Value.EndsWith(".slider",
                            StringComparison.OrdinalIgnoreCase) &&
                        !path.Value.EndsWith(".ini",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(Error(
                            "racemenu-catalog-authority-slider-extension",
                            $"'{races.AssetPath}' references a slider asset that is neither .slider nor .ini: '{path}'."));
                        continue;
                    }
                    if (seen.Add(path.Value)) paths.Add(path);
                }
                catch (ArgumentException)
                {
                    diagnostics.Add(Error(
                        "racemenu-catalog-authority-slider-path",
                        $"'{races.AssetPath}' contains an unsafe slider path."));
                }
            }
        }
    }

    private static string Decode(
        ResolvedSkyrimAssetContent asset,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var builder = new StringBuilder(asset.Content.Length);
        foreach (byte value in asset.Content)
        {
            if (value is >= 0x80 and <= 0x9F)
            {
                char mapped = Windows1252Controls[value - 0x80];
                if (mapped == '\0')
                {
                    diagnostics.Add(Error("racemenu-catalog-authority-encoding",
                        $"Catalog asset '{asset.AssetPath}' contains undefined Windows-1252 byte 0x{value:X2}."));
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

    private static void ValidateRequest(
        SkyrimRaceMenuCatalogAuthorityRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("racemenu-catalog-authority-edition",
                "RaceMenu catalog authority supports Skyrim SE/AE only."));
        }
        if (request.LoadedPlugins.IsDefaultOrEmpty ||
            request.LoadedPlugins.Length > 512 ||
            request.LoadedPlugins.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.LoadedPlugins.Length)
        {
            diagnostics.Add(Error("racemenu-catalog-authority-plugin-order",
                "LoadedPlugins must be an explicit distinct ordered array of at most 512 plugins."));
        }
    }

    private static void AddIfPresent(
        ImmutableArray<AssetPath>.Builder paths,
        HashSet<string> indexed,
        string value)
    {
        var path = new AssetPath(value);
        if (indexed.Contains(path.Value)) paths.Add(path);
    }

    private static SkyrimAssetContentAuthority ToContentAuthority(
        SkyrimAssetAuthority authority) =>
        new(authority.ProviderId,
            authority.ProviderKind switch
            {
                AssetProviderKind.Loose => SkyrimAssetContentProviderKind.Loose,
                AssetProviderKind.Archive => SkyrimAssetContentProviderKind.Bsa,
                _ => throw new InvalidDataException(
                    $"Unsupported provider kind '{authority.ProviderKind}'.")
            },
            authority.ProviderPath, authority.ProviderSha256,
            authority.AssetPath, authority.ContentLength,
            authority.ContentSha256);

    private static SkyrimRaceMenuCatalogAuthorityResult Accepted(
        ImmutableArray<PluginName> plugins,
        ImmutableArray<SkyrimAssetAuthority> authorities,
        ImmutableArray<ResolvedSkyrimAssetContent> resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<SkyrimRaceMenuCatalogAsset> assets = resolved
            .Select(item => new SkyrimRaceMenuCatalogAsset(
                item.AssetPath, item.ContentSha256, item.Content))
            .ToImmutableArray();
        return new SkyrimRaceMenuCatalogAuthorityResult(
            true, new SkyrimRaceMenuCatalogParseRequest(plugins, assets),
            authorities, diagnostics.ToImmutable());
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
            {
                target.Add(diagnostic);
            }
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimRaceMenuCatalogAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, [], diagnostics.ToImmutable());

    private sealed record ResolvedCatalogSet(
        bool Accepted,
        ImmutableArray<SkyrimAssetAuthority> Authorities,
        ImmutableArray<ResolvedSkyrimAssetContent> Assets,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ResolvedCatalogSet Empty { get; } =
            new(true, [], [], []);
    }
}
