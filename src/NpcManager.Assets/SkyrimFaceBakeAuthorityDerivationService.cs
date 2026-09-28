using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>Reuses the production record, catalog, asset and geometry authorities.</summary>
public sealed class SkyrimFaceBakeAuthorityDerivationService(
    IPresetService presetService,
    ISkyrimFaceRecordPluginAuthorityLoader pluginLoader,
    ISkyrimFaceRecordRouteResolver recordResolver,
    ISkyrimAssetAuthorityPlanner assetPlanner,
    ISkyrimAssetContentResolver contentResolver,
    ISkyrimRaceMenuCatalogAuthorityLoader catalogLoader,
    IRaceMenuSliderCatalogParserCore catalogParser,
    ISseSelectedHeadpartNifGeometryReader geometryReader,
    ISkyrimFaceBakeCarrierGeometryReader carrierGeometryReader,
    ISkyrimFaceBakeAuthorityLoader authorityLoader,
    IWorkspacePolicy policy,
    IExternalHeadPartDependencyDiscovery externalDiscovery,
    IExternalHeadPartFaceGeomExclusionVerifier exclusionVerifier) : ISkyrimFaceBakeAuthorityDerivationService
{
    public async ValueTask<SkyrimFaceBakeAuthorityDerivationResult> DeriveAsync(
        SkyrimFaceBakeAuthorityDerivationRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            diagnostics.AddRange(policy.Evaluate(request.AllowedRoot, request.Output));
            diagnostics.AddRange(policy.EvaluateReadRoot(request.AllowedRoot, request.DataRoot));
            diagnostics.AddRange(policy.EvaluateReadRoot(request.AllowedRoot, request.Preset));
            if (HasErrors()) return Refused();
            if (!request.Output.IsUnder(request.AllowedRoot) || File.Exists(request.Output.Value) ||
                Directory.Exists(request.Output.Value) || !Directory.Exists(Path.GetDirectoryName(request.Output.Value)))
                throw new InvalidDataException("The authority output must be a fresh file beneath an existing ordinary workspace directory.");
            PresetParseResult preset = await presetService.InspectAsync(new(PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition, request.Preset), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(preset.Diagnostics);
            if (preset.Document is not { IsValid: true } parsed || parsed.SourceHash != request.ExpectedPresetSha256)
            {
                diagnostics.Add(Error("face-bake-derivation-preset-hash", "The exact selected JSlot did not parse with its declared SHA-256."));
                return Refused();
            }
            var selected = ImmutableArray.CreateBuilder<SkyrimFaceRecordHeadPartSelection>();
            foreach (var part in parsed.Appearance.HeadParts)
            {
                if (part.Identifier.Plugin is not { } plugin || part.Identifier.FormId is not { } form)
                    throw new InvalidDataException("Every selected HDPT needs an explicit portable plugin-local identity.");
                selected.Add(new(new FormReference(plugin, form), []));
            }
            var plugins = await pluginLoader.LoadAsync(new(GameEdition.SkyrimSpecialEdition,
                request.DataRoot, request.PluginOrder), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(plugins.Diagnostics);
            if (!plugins.Accepted || HasErrors()) return Refused();
            var routed = await recordResolver.ResolveAsync(new(GameEdition.SkyrimSpecialEdition,
                request.Race, request.Sex, selected.ToImmutable(), plugins.Authorities), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(routed.Diagnostics);
            if (!routed.Accepted || routed.Route is not { } route || HasErrors()) return Refused();
            var discovery = await externalDiscovery.DiscoverAsync(new(request.DataRoot, request.PluginOrder,
                route, request.Sex), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(discovery.Diagnostics);
            if (discovery.Status == ExternalHeadPartDependencyDiscoveryStatus.Refused || HasErrors()) return Refused();
            if ((discovery.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted) != (discovery.Descriptor is not null))
                throw new InvalidDataException("External provider discovery did not retain its exact accepted descriptor.");
            var excluded = (discovery.Descriptor?.Members.Select(member => member.OriginForm) ?? [])
                .ToHashSet();
            var ordinary = route.HeadParts.Where(part => !excluded.Contains(part.Reference)).ToImmutableArray();
            var catalog = await catalogLoader.LoadAsync(new(GameEdition.SkyrimSpecialEdition,
                request.DataRoot, request.PluginOrder), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(catalog.Diagnostics);
            if (!catalog.Accepted || catalog.CatalogRequest is null || HasErrors()) return Refused();
            var parsedCatalog = catalogParser.Parse(catalog.CatalogRequest);
            diagnostics.AddRange(parsedCatalog.Diagnostics);
            if (!parsedCatalog.Accepted || parsedCatalog.Catalog is null || HasErrors()) return Refused();
            var extensions = parsedCatalog.Catalog.MorphExtensions.ToDictionary(row => row.BaseChargenTri.Value,
                row => row.ExtendedTriPaths, StringComparer.OrdinalIgnoreCase);
            var required = ordinary.Select(part => part.ModelNif)
                .Concat(ordinary.SelectMany(part => part.TriRoutes.Select(tri => tri.Path)))
                .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            var optional = ordinary.SelectMany(part => part.TriRoutes.Where(tri => tri.Role == SkyrimHdptTriRole.CharGen))
                .SelectMany(tri => extensions.GetValueOrDefault(tri.Path.Value, []))
                .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            var planned = await assetPlanner.PlanAsync(new(GameEdition.SkyrimSpecialEdition,
                request.DataRoot, required, optional), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(planned.Diagnostics);
            if (!planned.Accepted || HasErrors()) return Refused();
            var authorities = catalog.Authorities.Concat(planned.Authorities)
                .DistinctBy(asset => asset.AssetPath.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            var content = await contentResolver.ResolveAsync(new(request.AllowedRoot,
                authorities.Select(ToContent).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(content.Diagnostics);
            if (!content.Resolved || HasErrors()) return Refused();
            var bytesByPath = content.Assets.ToDictionary(asset => asset.AssetPath.Value, StringComparer.OrdinalIgnoreCase);
            var carrierPath = new WorkspacePath(request.Carrier.PhysicalPath);
            if (request.Carrier is ApplicationProviderResourceAuthority)
            {
                if (!carrierPath.IsUnder(new WorkspacePath(AppContext.BaseDirectory)))
                    throw new InvalidDataException("An application-owned carrier must remain beneath the executing product directory.");
            }
            else
                diagnostics.AddRange(policy.EvaluateReadRoot(request.AllowedRoot, carrierPath));
            if (HasErrors()) return Refused();
            byte[] carrierBytes = await ReadCarrierAsync(request.Carrier, cancellationToken).ConfigureAwait(false);
            var carrier = carrierGeometryReader.Read(carrierBytes.ToImmutableArray(), request.Carrier.ExpectedSha256);
            diagnostics.AddRange(carrier.Diagnostics);
            if (!carrier.Accepted || HasErrors()) return Refused();
            var document = new SkyrimFaceBakeAuthorityDocumentDto
            {
                SchemaVersion = 2, AuthorityId = "derived-" + parsed.SourceHash.Value[..24],
                LoadedPlugins = [], RecordPlugins = [], Assets = [], CatalogConfigs = [],
                CarrierShapes = [], ShapeTriInputs = [], RecordOnlyMappedHeadParts = [], OptionalUnavailableExtensions = []
            };
            foreach (var plugin in plugins.Authorities)
                document.RecordPlugins.Add(new() { Order = document.RecordPlugins.Count, Plugin = plugin.Plugin.Value,
                    Path = Relative(request.AllowedRoot, plugin.Path), Sha256 = plugin.ExpectedSha256.Value });
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byPath = authorities.ToDictionary(asset => asset.AssetPath.Value, StringComparer.OrdinalIgnoreCase);
            string AssetId(AssetPath path)
            {
                if (ids.TryGetValue(path.Value, out string? existing)) return existing;
                var asset = byPath[path.Value];
                string id = "asset-" + document.Assets.Count;
                ids.Add(asset.AssetPath.Value, id);
                document.Assets.Add(new() { Order = document.Assets.Count, Id = id, ProviderId = asset.ProviderId,
                    ProviderKind = asset.ProviderKind == AssetProviderKind.Loose ? "loose" : "bsa",
                    ProviderPath = Relative(request.AllowedRoot, asset.ProviderPath), ProviderSha256 = asset.ProviderSha256.Value,
                    AssetPath = asset.AssetPath.Value, ContentLength = asset.ContentLength, ContentSha256 = asset.ContentSha256.Value });
                return id;
            }
            foreach (PluginName plugin in request.PluginOrder)
            {
                string prefix = $"meshes/actors/character/facegenmorphs/{plugin.Value}/";
                var configs = catalog.Authorities.Where(asset => asset.AssetPath.Value.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                if (configs.Length == 0) continue;
                document.LoadedPlugins.Add(new() { Order = document.LoadedPlugins.Count, Plugin = plugin.Value });
                foreach (var config in configs)
                    document.CatalogConfigs.Add(new() { Order = document.CatalogConfigs.Count,
                        Plugin = plugin.Value, AssetId = AssetId(config.AssetPath) });
            }
            var recordOnly = new HashSet<FormReference>();
            foreach (var part in selected)
                if (excluded.Contains(part.Reference) || route.HeadPartGraph.Any(node => node.OriginForm == part.Reference && node.ModelNif is null))
                    recordOnly.Add(part.Reference);
            var included = ImmutableArray.CreateBuilder<SkyrimNativeFaceGeomShapeEvidence>();
            var usedCarriers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in ordinary)
            {
                var model = bytesByPath[part.ModelNif.Value];
                var geometry = geometryReader.Read(new(model.AssetPath, model.ContentSha256, model.Content));
                diagnostics.AddRange(geometry.Diagnostics);
                if (!geometry.Accepted || geometry.Document is null || HasErrors()) return Refused();
                if (geometry.Document.Shapes.All(shape => shape.IsShaderlessDummy))
                {
                    if (!part.IsSelected || part.Parent is not null)
                    {
                        diagnostics.Add(Error("face-bake-derivation-dummy-hnam-unsupported",
                            $"Shaderless dummy '{part.Reference}' is a default/HNAM child without an existing mapped-root record-only authority; derivation cannot publish a consumable carrier closure."));
                        return Refused();
                    }
                    recordOnly.Add(part.Reference);
                }
                foreach (var shape in geometry.Document.Shapes)
                {
                    if (shape.IsShaderlessDummy) continue;
                    var candidates = carrier.Shapes.Where(target => target.TopologySha256 == shape.TopologySha256 &&
                        target.VertexCount == shape.VertexCount).ToArray();
                    var named = candidates.Where(target => target.Name == part.EditorId || target.Name == shape.Name).ToArray();
                    if (named.Length == 1) candidates = named;
                    if (candidates.Length != 1 || !usedCarriers.Add(candidates[0].Name))
                        throw new InvalidDataException($"Model '{part.ModelNif}' shape '{shape.Name}' has no unique unused carrier mapping.");
                    var target = candidates[0];
                    document.CarrierShapes.Add(new() { Order = document.CarrierShapes.Count, HeadPart = part.Reference.ToString(),
                        ModelAssetId = AssetId(part.ModelNif), ModelShapeName = shape.Name, CarrierShapeName = target.Name,
                        ExpectedModelPositionSha256 = shape.PackedPositionSha256.Value, ExpectedModelTopologySha256 = shape.TopologySha256.Value,
                        ExpectedCarrierTopologySha256 = target.TopologySha256.Value });
                    string? Tri(SkyrimHdptTriRole role) => part.TriRoutes.SingleOrDefault(tri => tri.Role == role) is { } tri ? AssetId(tri.Path) : null;
                    var chargen = part.TriRoutes.SingleOrDefault(tri => tri.Role == SkyrimHdptTriRole.CharGen);
                    document.ShapeTriInputs.Add(new() { Order = document.ShapeTriInputs.Count, CarrierShapeName = target.Name,
                        RaceAssetId = Tri(SkyrimHdptTriRole.RaceMorph), ChargenAssetId = Tri(SkyrimHdptTriRole.CharGen),
                        MeshAssetId = Tri(SkyrimHdptTriRole.Mesh), ExtendedAssetIds = chargen is null ? [] :
                            extensions.GetValueOrDefault(chargen.Path.Value, []).Where(path => byPath.ContainsKey(path.Value)).Select(path => (string?)AssetId(path)).ToList() });
                    included.Add(new(part.Reference, part.EffectiveType, model.AssetPath, model.ContentSha256, shape.Name,
                        target.Name, shape.VertexCount, shape.TopologySha256, shape.PackedPositionSha256,
                        target.PackedPositionSha256, [], part.EffectiveType == NpcHeadPartType.Face));
                }
            }
            foreach (var part in selected)
                if (recordOnly.Contains(part.Reference))
                    document.RecordOnlyMappedHeadParts.Add(new() { Order = document.RecordOnlyMappedHeadParts.Count, HeadPart = part.Reference.ToString() });
            foreach (var unavailable in planned.UnavailableOptionalAssets.IsDefault ? [] : planned.UnavailableOptionalAssets)
                document.OptionalUnavailableExtensions.Add(new() { Order = document.OptionalUnavailableExtensions.Count, AssetPath = unavailable.Value });
            if (discovery.Descriptor is { } descriptor)
            {
                var verified = await exclusionVerifier.VerifyAsync(new(carrierPath, request.Carrier.ExpectedSha256,
                    carrierBytes.LongLength, descriptor, included.ToImmutable()), cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(verified.Diagnostics);
                if (!verified.Verified || verified.Attestation is null || HasErrors()) return Refused();
            }
            byte[] bytes = SkyrimFaceBakeAuthorityLoader.SerializeDerived(document, request.AllowedRoot);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            var temporary = new WorkspacePath(request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N"));
            bool ownsTemporary = false;
            try
            {
                await using (var output = new FileStream(temporary.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                var loaded = await authorityLoader.LoadAsync(new(request.AllowedRoot, temporary, hash), cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(loaded.Diagnostics);
                if (!loaded.Loaded || HasErrors()) return Refused();
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary.Value, request.Output.Value, overwrite: false);
                ownsTemporary = false;
                return new(new(request.Output, hash), diagnostics.ToImmutable());
            }
            finally
            {
                if (ownsTemporary) File.Delete(temporary.Value);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            diagnostics.Add(Error("face-bake-derivation-refused", exception.Message));
            return Refused();
        }
        bool HasErrors() => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        SkyrimFaceBakeAuthorityDerivationResult Refused() => new(null, diagnostics.ToImmutable());
    }

    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);
    private static string Relative(WorkspacePath root, WorkspacePath path) => path.IsUnder(root)
        ? Path.GetRelativePath(root.Value, path.Value).Replace('\\', '/')
        : throw new InvalidDataException("A derived authority provider escaped the allowed workspace.");
    private static SkyrimAssetContentAuthority ToContent(SkyrimAssetAuthority asset) => new(asset.ProviderId,
        asset.ProviderKind == AssetProviderKind.Loose ? SkyrimAssetContentProviderKind.Loose : SkyrimAssetContentProviderKind.Bsa,
        asset.ProviderPath, asset.ProviderSha256, asset.AssetPath, asset.ContentLength, asset.ContentSha256);
    private static async Task<byte[]> ReadCarrierAsync(ProviderResourceAuthority carrier, CancellationToken cancellationToken)
    {
        for (string? path = carrier.PhysicalPath; path is not null; path = Path.GetDirectoryName(path))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The carrier path must contain no reparse points.");
        await using var stream = new FileStream(carrier.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (carrier.IsDirectory || File.GetAttributes(carrier.PhysicalPath).HasFlag(FileAttributes.ReparsePoint) ||
            stream.Length is <= 0 or > 64L * 1024 * 1024)
            throw new InvalidDataException("The exact carrier must be one ordinary bounded NIF file.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))) != carrier.ExpectedSha256)
            throw new InvalidDataException("The carrier bytes do not match the bound SHA-256.");
        return bytes;
    }
}
