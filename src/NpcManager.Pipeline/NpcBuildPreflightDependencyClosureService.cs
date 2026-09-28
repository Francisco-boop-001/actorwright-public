using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using System.Security.Cryptography;

namespace NpcManager.Pipeline;

/// <summary>Observes inputs only. It never produces an external-provider receipt.</summary>
public sealed class NpcBuildPreflightDependencyClosureService(
    IAssetIndexer assetIndexer,
    ISkyrimFaceRecordRouteResolver routeResolver,
    ISkyrimAssetAuthorityPlanner assetPlanner,
    ISkyrimAssetContentResolver contentResolver,
    ISkyrimRaceTintAuthorityReader tintReader,
    IRaceMenuSliderCatalogParserCore catalogParser,
    ISseSelectedHeadpartNifGeometryReader geometryReader,
    ISseTriHeadReader triReader,
    ISseFaceMorphPlanBuilder morphPlanner,
    ExternalHeadPartPhysicsBindingResolver physicsResolver,
    ISkyrimNpcWholeSkinAuthorityResolver wholeSkinResolver)
{
    internal static bool CustomMorphsAreBaked(RaceMenuNpcBuildRequest request, PresetDocument preset,
        RaceMenuNpcStandaloneAssets? assets, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (assets?.ExternalCharGenExportAuthority is not { } export) return false;
        if (assets.SchemaVersion != 7 || export.PresetSha256 != preset.SourceHash)
        {
            diagnostics.Add(Error("racemenu-external-chargen-character-drift",
                "Baked custom morphs require schema-7 export authority bound to the actual selected preset bytes."));
            return false;
        }
        if (!RaceMenuNpcStandaloneAuthorityReader.ValidateExternalCharGenExportBinding(request, assets, diagnostics))
            return false;
        // The standalone reader has already reopened the hash-bound export.
        // Only shape reconstruction is satisfied here; runtime/provider assets
        // are still observed by the rest of this dependency collection.
        diagnostics.Add(new Diagnostic("npc-build-preflight-custom-morphs-baked", DiagnosticSeverity.Info,
            $"Custom morphs are baked in accepted FaceGeom {export.FaceGeomSha256.Value}, bound to preset {export.PresetSha256.Value}; no second reconstruction is required."));
        return true;
    }

    public async ValueTask<(ImmutableArray<NpcBuildPreflightDependency> Dependencies,
        ImmutableArray<Diagnostic> Diagnostics)> CollectAsync(
        NpcBuildPreflightRequest request,
        PresetDocument preset,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> plugins,
        RaceMenuNpcAppearancePlan? appearancePlan,
        RaceMenuNpcStandaloneAssets? standaloneAssets,
        CancellationToken cancellationToken)
    {
        using IDisposable? snapshot = (assetIndexer as IAssetIndexSnapshotScopeFactory)?.BeginSnapshot();
        WorkspacePath dataRoot = request.DataRoot!.Value;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var rows = new Dictionary<string, NpcBuildPreflightDependency>(StringComparer.OrdinalIgnoreCase);
        var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planner = new ObservingPlanner(assetPlanner, rows, diagnostics, platforms, optionalInputs);
        var content = new Dictionary<string, ResolvedSkyrimAssetContent>(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, AssetPath>(StringComparer.OrdinalIgnoreCase);

        if (appearancePlan is not null)
        {
            var masterProviders = CollectMasterProviders(
                dataRoot,
                appearancePlan.RequiredOutputMasters,
                appearancePlan.PluginAuthorities);
            foreach (NpcBuildPreflightDependency dependency in masterProviders.Dependencies)
                rows[dependency.Path] = dependency;
            diagnostics.AddRange(masterProviders.Diagnostics);
        }

        void Add(string? raw, bool texture = false, bool platformProvided = false,
            bool optional = false)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string normalized = raw.Trim().Replace('\\', '/');
            if (texture && !normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
                normalized = "textures/" + normalized;
            try
            {
                var path = new AssetPath(normalized);
                if (texture && !path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Texture dependencies must be DDS paths.");
                if (!platformProvided)
                {
                    if (optional && !explicitInputs.Contains(path.Value))
                        optionalInputs.Add(path.Value);
                    else
                    {
                        explicitInputs.Add(path.Value);
                        optionalInputs.Remove(path.Value);
                    }
                    platforms.Remove(path.Value);
                    if (rows.TryGetValue(path.Value, out var prior) && prior.Disposition == "platformProvided")
                        rows.Remove(path.Value);
                }
                else if (!explicitInputs.Contains(path.Value) && SkyrimNativeFaceGeomBuildService.IsSkyrimPlatformTexture(path))
                    platforms.Add(path.Value);
                candidates.TryAdd(path.Value, path);
            }
            catch (ArgumentException exception)
            {
                rows[raw] = new(texture ? "texture" :
                    raw.EndsWith(".slider", StringComparison.OrdinalIgnoreCase) || raw.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                        ? "catalog" : "asset", raw, "nonCanonicalPath");
                diagnostics.Add(Error("skyrim-asset-authority-path", $"Dependency '{raw}' is not a canonical Data-relative path: {exception.Message}"));
            }
        }

        foreach (PresetTint tint in preset.Appearance.Tints) Add(tint.Texture, texture: true);
        if (preset.Appearance.RaceMenu is { } raceMenu)
        {
            foreach (RaceMenuFaceTexture texture in raceMenu.FaceTextures) Add(texture.Texture, texture: true);
            foreach (RaceMenuBodyOverlay overlay in raceMenu.BodyOverlays)
            {
                if (RaceMenuNpcRuntimeAppearanceMapper.IsInactiveLegacyOverlay(overlay)) continue;
                Add(overlay.Diffuse, texture: true);
                Add(overlay.Normal, texture: true);
            }
            foreach (SkyrimSkinOverride skin in raceMenu.SkinOverrides)
                foreach (string texture in skin.Textures.Values) Add(texture, texture: true);
        }

        var selections = ImmutableArray.CreateBuilder<SkyrimFaceRecordHeadPartSelection>();
        foreach (PresetHeadPart part in preset.Appearance.HeadParts)
        {
            if (part.Identifier.Plugin is { } plugin && part.Identifier.FormId is { } form)
                selections.Add(new(new FormReference(plugin, form), []));
            else
                diagnostics.Add(Error("npc-build-preflight-headpart-binding",
                    $"Selected head part '{part.Identifier.Raw}' has no portable plugin-local binding."));
        }
        SkyrimFaceRecordRouteResult routed = await routeResolver.ResolveAsync(
            new(GameEdition.SkyrimSpecialEdition, request.EffectiveRequest.Build.References.Race,
                request.EffectiveRequest.Build.Traits.Sex, selections.ToImmutable(), plugins),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(routed.Diagnostics);
        SkyrimFaceRecordRoute? route = routed.Route;
        if (route is not null)
            foreach (SkyrimFaceHeadPartRecordRoute part in route.HeadParts)
            {
                Add(part.ModelNif.Value);
                foreach (SkyrimHdptTriRoute tri in part.TriRoutes.Where(tri =>
                             !SkyrimExternalHairTriOmissionPolicy.IsAllowedMissingHairNam0(part, tri)))
                    Add(tri.Path.Value);
                if (part.TextureSet is { } textures)
                    foreach (string texture in textures.RawTxSlots) Add(texture, texture: true, platformProvided: true);
            }

        // The mapper is also used by the later record-authority producer. This
        // observation leaves its receipt and hash-bound admission unchanged.
        SkyrimRaceTintAuthorityResult tintAuthority = await tintReader.ReadAsync(
            new(GameEdition.SkyrimSpecialEdition, dataRoot,
                request.EffectiveRequest.Build.References.Race,
                request.EffectiveRequest.Build.Traits.Sex, plugins), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(tintAuthority.Diagnostics);
        if (tintAuthority.Authority is { } raceTint)
        {
            RaceMenuPresetTintAuthorityPlanResult tintPlan = new RaceMenuPresetTintAuthorityMapper().Map(preset, raceTint);
            diagnostics.AddRange(tintPlan.Diagnostics);
            if (tintPlan.Plan is { } mapped)
                foreach (RaceMenuPresetTintAuthorityDisposition disposition in mapped.Dispositions)
                    diagnostics.Add(new Diagnostic("npc-build-preflight-tint-disposition", DiagnosticSeverity.Info,
                        $"Tint {disposition.Source.Index} '{disposition.Source.Texture}': {disposition.Kind}; TINI={disposition.TiniIndex?.ToString() ?? "none"}."));
        }

        var catalogLoader = new SkyrimRaceMenuCatalogAuthorityLoader(assetIndexer, planner, contentResolver, path => Add(path));
        SkyrimRaceMenuCatalogAuthorityResult catalogAuthority = await catalogLoader.LoadAsync(
            new(GameEdition.SkyrimSpecialEdition, dataRoot, request.PluginOrder), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(catalogAuthority.Diagnostics);
        SkyrimRaceMenuSliderCatalog catalog = new([], []);
        SkyrimRaceMenuCatalogParseResult parsedCatalog;
        if (catalogAuthority.CatalogRequest is { } catalogRequest)
        {
            parsedCatalog = catalogParser.Parse(catalogRequest);
        }
        else
        {
            // A missing .slider must not hide the independently declared TRI
            // tree in a present morphs.ini. Reuse the pure parser on those files.
            var morphConfigs = ImmutableArray.CreateBuilder<SkyrimRaceMenuCatalogAsset>();
            foreach (SkyrimAssetAuthority authority in planner.Authorities.Values.Where(item =>
                         item.AssetPath.Value.EndsWith("/morphs.ini", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                ResolvedSkyrimAssetContent? config = await ReadContentAsync(authority).ConfigureAwait(false);
                if (config is not null) morphConfigs.Add(new(config.AssetPath, config.ContentSha256, config.Content));
            }
            parsedCatalog = catalogParser.Parse(new(request.PluginOrder, morphConfigs.ToImmutable()));
        }
        diagnostics.AddRange(parsedCatalog.Diagnostics);
        if (parsedCatalog.Catalog is { } available) catalog = available;
        if (route is not null)
        {
            HashSet<string> hosts = route.HeadParts.SelectMany(part => part.TriRoutes)
                .Where(tri => tri.Role == SkyrimHdptTriRole.CharGen).Select(tri => tri.Path.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (AssetPath extension in catalog.MorphExtensions
                         .Where(item => hosts.Contains(item.BaseChargenTri.Value))
                         .SelectMany(item => item.ExtendedTriPaths)) Add(extension.Value, optional: true);
            foreach (SkyrimRaceMenuMorphDependencyObservation observed in parsedCatalog.ObservedMorphDependencies
                         .Where(item => hosts.Contains(item.BaseChargenTri.Value))) Add(observed.ExtendedTriPath, optional: true);
        }
        await ObserveCandidatesAsync().ConfigureAwait(false);

        // Missing siblings never prevent us from reading every available NIF.
        var physicsModels = ImmutableArray.CreateBuilder<(AssetPath Path, ExternalHeadPartProviderNifReadResult Read)>();
        var vertices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimAssetAuthority authority in planner.Authorities.Values
                     .Where(item => item.AssetPath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            ResolvedSkyrimAssetContent? model = await ReadContentAsync(authority).ConfigureAwait(false);
            if (model is null) continue;
            SseSelectedHeadpartNifGeometryReadResult geometry = geometryReader.Read(
                new(model.AssetPath, model.ContentSha256, model.Content));
            bool externalHair = route?.HeadParts.Any(part => part.ModelNif == model.AssetPath &&
                part.EffectiveType == NpcHeadPartType.Hair &&
                !ExternalHeadPartMemberAuthority.IsOfficialVanillaMaster(part.Provider.Plugin)) == true;
            if (geometry.Accepted && geometry.Document is { } document)
            {
                if (document.Shapes.Length == 1) vertices[model.AssetPath.Value] = document.Shapes[0].VertexCount;
                foreach (SseSelectedHeadpartTextureSlot texture in document.Shapes
                             .SelectMany(shape => shape.Materials).SelectMany(material => material.TextureSlots))
                    Add(texture.Path.Value, texture: true, platformProvided: true);
            }
            if (!geometry.Accepted || externalHair)
            {
                ExternalHeadPartProviderNifReadResult external = new ExternalHeadPartProviderNifReader().Read(
                    new(model.AssetPath, model.ContentSha256, model.Content, AllowPhysicsBinding: true));
                diagnostics.AddRange(external.Diagnostics);
                if (external.Accepted)
                {
                    foreach (AssetPath dependency in external.Dependencies) Add(dependency.Value);
                    physicsModels.Add((model.AssetPath, external));
                }
            }
        }
        await ObserveCandidatesAsync().ConfigureAwait(false);
        var boundPhysicsModels = new HashSet<AssetPath>();
        if (route is not null)
        {
            // Rediscovery is read-only. The build must still create/admit and
            // independently revalidate its exact descriptor and receipt later.
            var discovery = new ExternalHeadPartDependencyDiscoveryService(planner, contentResolver, physicsResolver);
            ExternalHeadPartDependencyDiscoveryResult external = await discovery.DiscoverAsync(
                new(dataRoot, request.PluginOrder, route, request.EffectiveRequest.Build.Traits.Sex),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(external.Diagnostics);
            if (external.Descriptor is { } descriptor)
            {
                foreach (ExternalHeadPartAssetDependency asset in descriptor.Assets)
                    rows[asset.Path.Value] = new(Kind(asset.Path), asset.Path.Value, "present");
                foreach (ExternalHeadPartRecordDependency member in descriptor.Members)
                    if (member.ModelNif is { } model)
                    {
                        vertices.Remove(model.Value);
                        boundPhysicsModels.Add(model);
                    }
                if (descriptor.Physics.MappingAuthority is { } mapping)
                    rows[mapping.Path.Value] = new("physics", mapping.Path.Value, "present");
            }
        }
        var physics = await physicsResolver.ObservePreflightDependenciesAsync(
            dataRoot, physicsModels.Where(model => !boundPhysicsModels.Contains(model.Path)).ToImmutableArray(),
            cancellationToken).ConfigureAwait(false);
        foreach (NpcBuildPreflightDependency row in physics.Dependencies) rows[row.Path] = row;
        diagnostics.AddRange(physics.Diagnostics);

        if ((request.EffectiveRequest.Build.ExistingNpcTarget is null ||
             standaloneAssets?.BodyMeshAuthority is not null) &&
            plugins.Length == request.PluginOrder.Length)
        {
            SkyrimNpcWholeSkinAuthorityResult skin =
                await wholeSkinResolver.ResolveAsync(
                    new SkyrimNpcWholeSkinAuthorityRequest(
                        request.EffectiveRequest.Build.Edition,
                        dataRoot,
                        request.EffectiveRequest.Build.References.Race,
                        request.EffectiveRequest.Build.Traits.Sex,
                        plugins)
                    {
                        DefaultOutfit = request.EffectiveRequest.Build.References.DefaultOutfit,
                        AllowMeshEmbeddedSkinTextureRoute =
                            standaloneAssets?.BodyMeshAuthority is not null ||
                            request.EffectiveRequest.AllowInheritedMeshEmbeddedSkinTextureRoute,
                        BodyMeshAuthority = standaloneAssets?.BodyMeshAuthority
                    }, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(skin.Diagnostics);
            if (skin.Authority is { } authority)
            {
                foreach (SkyrimAssetAuthority asset in authority.Regions
                             .SelectMany(region => region.TextureAssets))
                    rows[asset.AssetPath.Value] = new(
                        Kind(asset.AssetPath), asset.AssetPath.Value, "present");
            }
            else
            {
                foreach (Diagnostic diagnostic in skin.Diagnostics.Where(item =>
                             item.Code == "skyrim-asset-authority-missing"))
                {
                    string? path = MissingAssetPath(diagnostic.Message);
                    if (path is not null)
                        rows[path] = new(Kind(new AssetPath(path)), path, "missing");
                }
            }
        }

        ImmutableArray<SkyrimRaceMenuCustomMorphValue> custom = preset.Appearance.OrderedCustomMorphs;
        if (custom.IsDefaultOrEmpty && preset.Appearance.CustomMorphs.Count > 0)
            custom = preset.Appearance.CustomMorphs.Select(item => new SkyrimRaceMenuCustomMorphValue(item.Key, item.Value)).ToImmutableArray();
        if (route is not null && custom.Any(item => MathF.Abs(item.Value) >= 0.0001F) &&
            !CustomMorphsAreBaked(request.EffectiveRequest.Build, preset, standaloneAssets, diagnostics))
        {
            var tris = new Dictionary<string, SseTriHeadDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (SkyrimAssetAuthority authority in planner.Authorities.Values
                         .Where(item => item.AssetPath.Value.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                ResolvedSkyrimAssetContent? tri = await ReadContentAsync(authority).ConfigureAwait(false);
                if (tri is null) continue;
                SseTriHeadReadResult parsed = triReader.Read(new(tri.AssetPath, tri.ContentSha256, tri.Content));
                diagnostics.AddRange(parsed.Diagnostics);
                if (parsed.Document is { } document) tris[tri.AssetPath.Value] = document;
            }
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SkyrimFaceHeadPartRecordRoute part in route.HeadParts)
            {
                if (!vertices.TryGetValue(part.ModelNif.Value, out int count)) continue;
                SkyrimFaceMorphTriSource? Tri(SkyrimHdptTriRole role, SkyrimFaceMorphTriRole bakeRole) =>
                    part.TriRoutes.FirstOrDefault(item => item.Role == role) is { } tri && tris.TryGetValue(tri.Path.Value, out var document)
                        ? new(bakeRole, document) : null;
                SkyrimFaceMorphTriSource? chargen = Tri(SkyrimHdptTriRole.CharGen, SkyrimFaceMorphTriRole.Chargen);
                AssetPath[] extensions = catalog.MorphExtensions.Where(item =>
                        chargen is not null && item.BaseChargenTri == chargen.Document.SourcePath)
                    .SelectMany(item => item.ExtendedTriPaths).ToArray();
                SkyrimFaceMorphPlanBuildResult planned = morphPlanner.Build(new(
                    count, route.Race.MorphRaceEditorId, request.EffectiveRequest.Build.Traits.Sex == NpcSex.Female,
                    new SkyrimFaceMorphSnapshot([], 0, [], false, false), [], custom, [], 1, catalog,
                    Tri(SkyrimHdptTriRole.RaceMorph, SkyrimFaceMorphTriRole.Race), chargen,
                    Tri(SkyrimHdptTriRole.Mesh, SkyrimFaceMorphTriRole.Mesh),
                    extensions.Where(path => tris.ContainsKey(path.Value)).Select(path =>
                        new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Extended, tris[path.Value])).ToImmutableArray(),
                    RequireAllCustomMorphs: false)
                {
                    ExplicitUnavailableExtendedTris = extensions.Where(path => !tris.ContainsKey(path.Value)).ToImmutableArray()
                });
                diagnostics.AddRange(planned.Diagnostics);
                if (planned.Plan is { } plan) resolved.UnionWith(plan.ResolvedCustomMorphNames);
            }
            foreach (SkyrimRaceMenuCustomMorphValue morph in custom.Where(item => MathF.Abs(item.Value) >= 0.0001F)
                         .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                if (!resolved.Contains(morph.Name)) diagnostics.Add(Error("sse-face-bake-custom-unresolved",
                    $"Custom morph '{morph.Name}' has no binding in the staged TRI/catalog closure for race '{route.Race.MorphRaceEditorId}' and sex '{request.EffectiveRequest.Build.Traits.Sex}'. Its provider catalog path is unresolved; stage the declared provider's races.ini/morphs.ini and TRI tree."));
        }
        return (rows.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            diagnostics.Distinct().ToImmutableArray());

        async ValueTask ObserveCandidatesAsync()
        {
            AssetPath[] paths = candidates.Values.Where(path => !rows.ContainsKey(path.Value)).ToArray();
            if (paths.Length > 0) await planner.PlanAsync(new(GameEdition.SkyrimSpecialEdition, dataRoot,
                paths.ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        }

        async ValueTask<ResolvedSkyrimAssetContent?> ReadContentAsync(SkyrimAssetAuthority authority)
        {
            if (content.TryGetValue(authority.AssetPath.Value, out var existing)) return existing;
            SkyrimAssetContentResolutionResult read = await contentResolver.ResolveAsync(new(dataRoot,
                [new SkyrimAssetContentAuthority(authority.ProviderId,
                    authority.ProviderKind == AssetProviderKind.Loose ? SkyrimAssetContentProviderKind.Loose : SkyrimAssetContentProviderKind.Bsa,
                    authority.ProviderPath, authority.ProviderSha256, authority.AssetPath, authority.ContentLength, authority.ContentSha256)]),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(read.Diagnostics);
            if (!read.Resolved || read.Assets.Length != 1) return null;
            content[authority.AssetPath.Value] = read.Assets[0];
            return read.Assets[0];
        }
    }

    private static string Kind(AssetPath path) => Path.GetExtension(path.Value).ToLowerInvariant() switch
    {
        ".dds" => "texture", ".tri" => "tri", ".nif" => "mesh", ".xml" => "physics", _ => "catalog"
    };

    private static string? MissingAssetPath(string message)
    {
        const string prefix = "Copied Data contains no provider for '";
        const string suffix = "'.";
        return message.StartsWith(prefix, StringComparison.Ordinal) &&
               message.EndsWith(suffix, StringComparison.Ordinal)
            ? message[prefix.Length..^suffix.Length]
            : null;
    }

    internal static (ImmutableArray<NpcBuildPreflightDependency> Dependencies,
        ImmutableArray<Diagnostic> Diagnostics) CollectMasterProviders(
        WorkspacePath dataRoot,
        ImmutableArray<PluginName> requiredMasters,
        ImmutableArray<NpcCreationPluginAuthority> authorities)
    {
        var dependencies = ImmutableArray.CreateBuilder<NpcBuildPreflightDependency>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableArray<NpcCreationPluginAuthority> reviewed = authorities.IsDefault
            ? []
            : authorities;
        foreach (PluginName master in requiredMasters)
        {
            string provider;
            try
            {
                provider = BethesdaNpcCreationProviderResolver.Resolve(
                    dataRoot, master, reviewed);
            }
            catch (InvalidDataException exception)
            {
                dependencies.Add(new("plugin", master.Value, "missing"));
                diagnostics.Add(Error("npc-create-plugin-authority-duplicate",
                    exception.Message));
                continue;
            }

            NpcCreationPluginAuthority? authority = reviewed.FirstOrDefault(item =>
                string.Equals(item.Plugin.Value, master.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(provider))
            {
                dependencies.Add(new("plugin", master.Value, "missing"));
                diagnostics.Add(Error("npc-create-master-provider-missing",
                    authority is null
                        ? $"Master provider {master} is missing from the copied K-local Data root."
                        : $"Explicit master provider {master} is missing at its reviewed path."));
                continue;
            }

            if (authority is not null)
            {
                Sha256Hash actual;
                try
                {
                    using var stream = new FileStream(provider, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 64 * 1024,
                        FileOptions.SequentialScan);
                    actual = new Sha256Hash(Convert.ToHexString(
                        SHA256.HashData(stream)));
                }
                catch (Exception exception) when (exception is IOException or
                                                    UnauthorizedAccessException)
                {
                    dependencies.Add(new("plugin", master.Value, "missing"));
                    diagnostics.Add(Error("npc-create-plugin-authority-hash-failed",
                        $"Plugin authority '{master}' could not be hashed: {exception.Message}"));
                    continue;
                }
                if (actual != authority.ExpectedSha256)
                {
                    dependencies.Add(new("plugin", master.Value, "drift"));
                    diagnostics.Add(Error("npc-create-plugin-authority-hash-mismatch",
                        $"Plugin authority '{master}' hash {actual} does not match " +
                        $"the reviewed hash {authority.ExpectedSha256}."));
                    continue;
                }
            }

            dependencies.Add(new("plugin", master.Value, "present"));
        }
        return (dependencies.ToImmutable(), diagnostics.ToImmutable());
    }

    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);

    private sealed class ObservingPlanner(ISkyrimAssetAuthorityPlanner inner,
        Dictionary<string, NpcBuildPreflightDependency> rows,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        HashSet<string> platforms,
        HashSet<string> optionalInputs) : ISkyrimAssetAuthorityPlanner
    {
        public Dictionary<string, SkyrimAssetAuthority> Authorities { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(SkyrimAssetAuthorityPlanRequest request, CancellationToken cancellationToken)
        {
            var found = ImmutableArray.CreateBuilder<SkyrimAssetAuthority>();
            var localDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (AssetPath path in request.RequiredAssets.AddRange(request.OptionalAssets.IsDefault ? [] : request.OptionalAssets))
            {
                if (!rows.TryGetValue(path.Value, out var row) ||
                    (Authorities.TryGetValue(path.Value, out var cached) &&
                     !string.Equals(cached.AssetPath.Value, path.Value, StringComparison.Ordinal)))
                {
                    Authorities.Remove(path.Value);
                    bool optional = optionalInputs.Contains(path.Value);
                    SkyrimAssetAuthorityPlanResult result = await inner.PlanAsync(
                        request with
                        {
                            RequiredAssets = optional ? [] : [path],
                            OptionalAssets = optional ? [path] : []
                        }, cancellationToken).ConfigureAwait(false);
                    ImmutableArray<Diagnostic> observedDiagnostics = result.Diagnostics.Select(item =>
                        platforms.Contains(path.Value) && item.Code == "skyrim-asset-authority-missing"
                            ? new Diagnostic("npc-build-preflight-platform-texture", DiagnosticSeverity.Info,
                                $"'{path}' is absent from copied Data and is exempt under the native Skyrim platform-texture rule; runtime supply is not verified.")
                            : item).ToImmutableArray();
                    localDiagnostics.AddRange(observedDiagnostics);
                    diagnostics.AddRange(observedDiagnostics);
                    row = new(Kind(path), path.Value, result.Accepted && result.Authorities.Length == 1 ? "present" : "missing",
                        platforms.Contains(path.Value) ? "platformProvided" :
                        optional && result.UnavailableOptionalAssets.Any(item =>
                            string.Equals(item.Value, path.Value, StringComparison.OrdinalIgnoreCase))
                            ? "optionalUnavailable" : null);
                    rows[path.Value] = row;
                    if (result.Accepted && result.Authorities.Length == 1) Authorities[path.Value] = result.Authorities[0];
                }
                if (Authorities.TryGetValue(path.Value, out var authority)) found.Add(authority);
                else if (row.Status != "present" &&
                         row.Disposition is not ("platformProvided" or "optionalUnavailable"))
                    localDiagnostics.Add(Error("skyrim-asset-authority-missing", $"Copied Data contains no admitted provider for '{path}'."));
            }
            return new(!localDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error), found.ToImmutable(), localDiagnostics.ToImmutable(), []);
        }
    }
}
