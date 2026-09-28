using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

public sealed partial class SkyrimFaceBakeAuthorityLoader
{
    private const int MaximumLoadedPlugins = 256;
    private const int MaximumRecordPlugins = 256;
    private const int MaximumAssets = 256;
    private const int MaximumCatalogConfigs = 64;
    private const int MaximumCarrierShapes = 128;
    private const int MaximumRecordOnlyMappedHeadParts = 128;
    private const int MaximumExtensionsPerShape = 128;
    private const int MaximumUnavailableExtensions = 256;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const long MaximumAssetBatchBytes = 512L * 1024 * 1024;

    private static ParsedAuthority? ValidateAndMap(
        WorkspacePath allowedRoot,
        SkyrimFaceBakeAuthorityDocumentDto dto,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (dto.SchemaVersion != 2)
        {
            diagnostics.Add(Error("face-bake-authority-schema-version-unsupported",
                "Authority manifest schemaVersion must be exactly 2."));
        }
        if (!IsSafeText(dto.AuthorityId, 128))
        {
            diagnostics.Add(Error("face-bake-authority-id-invalid",
                "authorityId must contain 1 to 128 printable characters without outer whitespace."));
        }

        bool rowsValid =
            ValidateOrderedRows(dto.LoadedPlugins, "loadedPlugins", 1, MaximumLoadedPlugins,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.RecordPlugins, "recordPlugins", 1, MaximumRecordPlugins,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.Assets, "assets", 1, MaximumAssets,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.CatalogConfigs, "catalogConfigs", 1, MaximumCatalogConfigs,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.ShapeTriInputs, "shapeTriInputs", 1, MaximumCarrierShapes,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.CarrierShapes, "carrierShapes", 1, MaximumCarrierShapes,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.RecordOnlyMappedHeadParts,
                "recordOnlyMappedHeadParts", 0, MaximumRecordOnlyMappedHeadParts,
                item => item.Order, diagnostics) &
            ValidateOrderedRows(dto.OptionalUnavailableExtensions,
                "optionalUnavailableExtensions", 0, MaximumUnavailableExtensions,
                item => item.Order, diagnostics);
        if (!rowsValid || HasErrors(diagnostics))
        {
            return null;
        }

        ImmutableArray<PluginName> loadedPlugins = MapLoadedPlugins(dto.LoadedPlugins!, diagnostics);
        ImmutableArray<SkyrimFaceRecordPluginAuthority> recordPlugins =
            MapRecordPlugins(allowedRoot, dto.RecordPlugins!, diagnostics);
        ImmutableArray<DeclaredAsset> assets = MapAssets(allowedRoot, dto.Assets!, diagnostics);
        if (HasErrors(diagnostics))
        {
            return null;
        }

        Dictionary<string, DeclaredAsset> assetsById = assets.ToDictionary(
            item => item.Id, StringComparer.Ordinal);
        HashSet<string> usedAssetIds = new(StringComparer.Ordinal);
        Dictionary<string, string> assetCategories = new(StringComparer.Ordinal);

        ImmutableArray<CatalogLink> catalog = MapCatalog(
            dto.CatalogConfigs!, loadedPlugins, assetsById, usedAssetIds,
            assetCategories, diagnostics);
        ImmutableArray<CarrierLink> carriers = MapCarriers(
            dto.CarrierShapes!, recordPlugins, assetsById, usedAssetIds,
            assetCategories, diagnostics);
        ImmutableArray<FormReference> recordOnlyMappedHeadParts =
            MapRecordOnlyMappedHeadParts(dto.RecordOnlyMappedHeadParts!,
                recordPlugins, carriers, diagnostics);
        ImmutableArray<TriLink> triInputs = MapTriInputs(
            dto.ShapeTriInputs!, carriers, assetsById, usedAssetIds,
            assetCategories, diagnostics);
        ImmutableArray<AssetPath> unavailable = MapUnavailableExtensions(
            dto.OptionalUnavailableExtensions!, assets, diagnostics);

        foreach (DeclaredAsset asset in assets)
        {
            if (!usedAssetIds.Contains(asset.Id))
            {
                diagnostics.Add(Error("face-bake-authority-asset-unreferenced",
                    $"Declared asset '{asset.Id}' is not assigned to a catalog, model, or TRI role."));
            }
        }

        if (HasErrors(diagnostics))
        {
            return null;
        }

        return new ParsedAuthority(
            dto.AuthorityId!, loadedPlugins, recordPlugins, assets,
            catalog, triInputs, carriers, recordOnlyMappedHeadParts, unavailable);
    }

    private static ImmutableArray<PluginName> MapLoadedPlugins(
        List<OrderedPluginDto?> rows,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<PluginName>.Builder result =
            ImmutableArray.CreateBuilder<PluginName>(rows.Count);
        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);
        foreach (OrderedPluginDto row in rows.Cast<OrderedPluginDto>())
        {
            if (!TryPlugin(row.Plugin, out PluginName plugin))
            {
                diagnostics.Add(Error("face-bake-authority-loaded-plugin-invalid",
                    $"loadedPlugins[{row.Order}].plugin is not a canonical plugin name."));
                continue;
            }
            if (!distinct.Add(plugin.Value))
            {
                diagnostics.Add(Error("face-bake-authority-loaded-plugin-duplicate",
                    $"Loaded plugin '{plugin}' occurs more than once."));
                continue;
            }
            result.Add(plugin);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimFaceRecordPluginAuthority> MapRecordPlugins(
        WorkspacePath allowedRoot,
        List<RecordPluginDto?> rows,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<SkyrimFaceRecordPluginAuthority>.Builder result =
            ImmutableArray.CreateBuilder<SkyrimFaceRecordPluginAuthority>(rows.Count);
        HashSet<string> plugins = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecordPluginDto row in rows.Cast<RecordPluginDto>())
        {
            if (!TryPlugin(row.Plugin, out PluginName plugin) ||
                !TryWorkspaceRelativePath(allowedRoot, row.Path, out WorkspacePath path) ||
                !TryHash(row.Sha256, out Sha256Hash hash))
            {
                diagnostics.Add(Error("face-bake-authority-record-plugin-invalid",
                    $"recordPlugins[{row.Order}] must contain a canonical plugin, K-relative path, and SHA-256."));
                continue;
            }
            if (!Path.GetFileName(path.Value).Equals(plugin.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("face-bake-authority-record-plugin-path-mismatch",
                    $"Record authority path for '{plugin}' must end in that exact plugin filename."));
            }
            if (!plugins.Add(plugin.Value) || !paths.Add(path.Value))
            {
                diagnostics.Add(Error("face-bake-authority-record-plugin-duplicate",
                    $"Record plugin authority '{plugin}' has a duplicate plugin or path."));
                continue;
            }
            result.Add(new SkyrimFaceRecordPluginAuthority(plugin, path, hash));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<DeclaredAsset> MapAssets(
        WorkspacePath allowedRoot,
        List<AuthorityAssetDto?> rows,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<DeclaredAsset>.Builder result =
            ImmutableArray.CreateBuilder<DeclaredAsset>(rows.Count);
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> assetPaths = new(StringComparer.OrdinalIgnoreCase);
        long batchBytes = 0;

        foreach (AuthorityAssetDto row in rows.Cast<AuthorityAssetDto>())
        {
            if (!IsCanonicalId(row.Id) || !ids.Add(row.Id!))
            {
                diagnostics.Add(Error("face-bake-authority-asset-id-invalid",
                    $"assets[{row.Order}].id must be a unique lowercase authority ID."));
                continue;
            }
            if (!IsSafeText(row.ProviderId, 128) ||
                !TryProviderKind(row.ProviderKind, out SkyrimAssetContentProviderKind kind) ||
                !TryWorkspaceRelativePath(allowedRoot, row.ProviderPath, out WorkspacePath providerPath) ||
                !TryHash(row.ProviderSha256, out Sha256Hash providerHash) ||
                !TryAssetPath(row.AssetPath, out AssetPath assetPath) ||
                !TryHash(row.ContentSha256, out Sha256Hash contentHash))
            {
                diagnostics.Add(Error("face-bake-authority-asset-invalid",
                    $"assets[{row.Order}] contains an invalid provider, canonical path, or SHA-256."));
                continue;
            }
            if (!assetPaths.Add(assetPath.Value))
            {
                diagnostics.Add(Error("face-bake-authority-asset-path-duplicate",
                    $"Canonical asset path '{assetPath}' occurs more than once."));
            }
            if (row.ContentLength <= 0 || row.ContentLength > MaximumAssetBytes)
            {
                diagnostics.Add(Error("face-bake-authority-asset-size-invalid",
                    $"Asset '{row.Id}' must contain 1 to {MaximumAssetBytes} bytes."));
            }
            try
            {
                batchBytes = checked(batchBytes + row.ContentLength);
            }
            catch (OverflowException)
            {
                batchBytes = long.MaxValue;
            }
            if (kind == SkyrimAssetContentProviderKind.Loose && providerHash != contentHash)
            {
                diagnostics.Add(Error("face-bake-authority-loose-hash-inconsistent",
                    $"Loose asset '{row.Id}' must use the same provider and content SHA-256."));
            }
            if (kind == SkyrimAssetContentProviderKind.Bsa &&
                !Path.GetExtension(providerPath.Value).Equals(".bsa", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("face-bake-authority-bsa-extension-invalid",
                    $"BSA provider for '{row.Id}' must end in .bsa."));
            }

            result.Add(new DeclaredAsset(row.Id!, new SkyrimAssetContentAuthority(
                row.ProviderId!, kind, providerPath, providerHash, assetPath,
                row.ContentLength, contentHash)));
        }

        if (batchBytes > MaximumAssetBatchBytes)
        {
            diagnostics.Add(Error("face-bake-authority-asset-batch-too-large",
                $"Declared asset content exceeds {MaximumAssetBatchBytes} bytes."));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<CatalogLink> MapCatalog(
        List<CatalogConfigDto?> rows,
        ImmutableArray<PluginName> loadedPlugins,
        IReadOnlyDictionary<string, DeclaredAsset> assets,
        HashSet<string> usedAssetIds,
        Dictionary<string, string> assetCategories,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<CatalogLink>.Builder result =
            ImmutableArray.CreateBuilder<CatalogLink>(rows.Count);
        Dictionary<string, (PluginName Plugin, int Index)> loaded = loadedPlugins
            .Select((plugin, index) => (plugin, index))
            .ToDictionary(item => item.plugin.Value,
                item => (item.plugin, item.index), StringComparer.OrdinalIgnoreCase);
        HashSet<string> catalogIds = new(StringComparer.Ordinal);
        HashSet<string> representedPlugins = new(StringComparer.OrdinalIgnoreCase);
        int previousPluginIndex = -1;

        foreach (CatalogConfigDto row in rows.Cast<CatalogConfigDto>())
        {
            if (!TryPlugin(row.Plugin, out PluginName declaredPlugin) ||
                !loaded.TryGetValue(declaredPlugin.Value, out var loadedPlugin))
            {
                diagnostics.Add(Error("face-bake-authority-catalog-plugin-unloaded",
                    $"catalogConfigs[{row.Order}] names a plugin outside loadedPlugins."));
                continue;
            }
            if (loadedPlugin.Index < previousPluginIndex)
            {
                diagnostics.Add(Error("face-bake-authority-catalog-order-drift",
                    "Catalog configs must be grouped in exact ascending loaded-plugin order."));
            }
            previousPluginIndex = loadedPlugin.Index;
            representedPlugins.Add(loadedPlugin.Plugin.Value);

            string extension = row.AssetId is not null && assets.TryGetValue(row.AssetId, out DeclaredAsset? config) &&
                config.Authority.AssetPath.Value.EndsWith(".slider", StringComparison.OrdinalIgnoreCase) ? ".slider" : ".ini";
            if (!TryAssetReference(row.AssetId, assets, extension, "catalog", usedAssetIds,
                    assetCategories, diagnostics, out DeclaredAsset asset) ||
                !catalogIds.Add(asset.Id))
            {
                if (row.AssetId is not null && catalogIds.Contains(row.AssetId))
                {
                    diagnostics.Add(Error("face-bake-authority-catalog-asset-duplicate",
                        $"Catalog asset '{row.AssetId}' occurs more than once."));
                }
                continue;
            }
            string prefix = $"meshes/actors/character/facegenmorphs/{loadedPlugin.Plugin.Value}/";
            if (!asset.Authority.AssetPath.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("face-bake-authority-catalog-path-mismatch",
                    $"Catalog asset '{asset.Id}' is not beneath the declared plugin's FaceGenMorphs path."));
            }
            result.Add(new CatalogLink(loadedPlugin.Plugin, asset.Id));
        }

        foreach (PluginName plugin in loadedPlugins)
        {
            if (!representedPlugins.Contains(plugin.Value))
            {
                diagnostics.Add(Error("face-bake-authority-catalog-plugin-missing",
                    $"Loaded plugin '{plugin}' has no declared catalog config."));
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<CarrierLink> MapCarriers(
        List<CarrierShapeDto?> rows,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> recordPlugins,
        IReadOnlyDictionary<string, DeclaredAsset> assets,
        HashSet<string> usedAssetIds,
        Dictionary<string, string> assetCategories,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<CarrierLink>.Builder result =
            ImmutableArray.CreateBuilder<CarrierLink>(rows.Count);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> modelShapeBindings = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> recordPluginNames = recordPlugins
            .Select(item => item.Plugin.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (CarrierShapeDto row in rows.Cast<CarrierShapeDto>())
        {
            if (!TryFormReference(row.HeadPart, out FormReference headPart) ||
                !recordPluginNames.Contains(headPart.Plugin.Value))
            {
                diagnostics.Add(Error("face-bake-authority-carrier-headpart-invalid",
                    $"carrierShapes[{row.Order}] must name a canonical FormReference backed by recordPlugins."));
                continue;
            }
            if (!IsSafeText(row.ModelShapeName, 256) ||
                !IsSafeText(row.CarrierShapeName, 256) ||
                !names.Add(row.CarrierShapeName!))
            {
                diagnostics.Add(Error("face-bake-authority-carrier-shape-invalid",
                    $"carrierShapes[{row.Order}] has an invalid or duplicate carrier shape name."));
                continue;
            }
            if (!TryAssetReference(row.ModelAssetId, assets, ".nif", "model", usedAssetIds,
                    assetCategories, diagnostics, out DeclaredAsset model))
            {
                continue;
            }
            if (model.Authority.AssetPath.Value.Contains("/facegendata/facegeom/",
                    StringComparison.OrdinalIgnoreCase) ||
                model.Authority.AssetPath.Value.Contains("/facegeom/",
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("face-bake-authority-finished-facegeom-refused",
                    $"Model authority '{model.Id}' points at finished FaceGeom instead of a headpart model."));
            }
            string modelBinding = $"{model.Id}\0{row.ModelShapeName}";
            if (!modelShapeBindings.Add(modelBinding))
            {
                diagnostics.Add(Error("face-bake-authority-model-shape-duplicate",
                    $"Model shape '{row.ModelShapeName}' in '{model.Id}' occurs more than once."));
            }
            if (!TryHash(row.ExpectedModelPositionSha256, out Sha256Hash positionHash) ||
                !TryHash(row.ExpectedModelTopologySha256, out Sha256Hash modelTopologyHash) ||
                !TryHash(row.ExpectedCarrierTopologySha256, out Sha256Hash carrierTopologyHash) ||
                IsZeroHash(positionHash) || IsZeroHash(modelTopologyHash) ||
                IsZeroHash(carrierTopologyHash))
            {
                diagnostics.Add(Error("face-bake-authority-carrier-hash-invalid",
                    $"carrierShapes[{row.Order}] requires three non-zero SHA-256 bindings."));
                continue;
            }
            if (modelTopologyHash != carrierTopologyHash)
            {
                diagnostics.Add(Error("face-bake-authority-carrier-topology-mismatch",
                    $"Carrier '{row.CarrierShapeName}' must bind equal model and carrier topology hashes."));
            }
            result.Add(new CarrierLink(headPart, model.Id, row.ModelShapeName!,
                row.CarrierShapeName!, positionHash, modelTopologyHash, carrierTopologyHash));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<TriLink> MapTriInputs(
        List<ShapeTriInputsDto?> rows,
        ImmutableArray<CarrierLink> carriers,
        IReadOnlyDictionary<string, DeclaredAsset> assets,
        HashSet<string> usedAssetIds,
        Dictionary<string, string> assetCategories,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<TriLink>.Builder result =
            ImmutableArray.CreateBuilder<TriLink>(rows.Count);
        if (rows.Count != carriers.Length)
        {
            diagnostics.Add(Error("face-bake-authority-shape-tri-count-drift",
                "shapeTriInputs must contain exactly one ordered row per carrier shape."));
            return result.ToImmutable();
        }

        for (int index = 0; index < rows.Count; index++)
        {
            ShapeTriInputsDto row = rows[index]!;
            CarrierLink carrier = carriers[index];
            if (!string.Equals(row.CarrierShapeName, carrier.CarrierShapeName,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error("face-bake-authority-shape-order-drift",
                    $"shapeTriInputs[{index}] must match carrierShapes[{index}] exactly."));
                continue;
            }

            string? race = MapOptionalTri(row.RaceAssetId, "race", assets,
                usedAssetIds, assetCategories, diagnostics);
            string? chargen = MapOptionalTri(row.ChargenAssetId, "chargen", assets,
                usedAssetIds, assetCategories, diagnostics);
            string? mesh = MapOptionalTri(row.MeshAssetId, "mesh", assets,
                usedAssetIds, assetCategories, diagnostics);
            if (row.ExtendedAssetIds is null ||
                row.ExtendedAssetIds.Count > MaximumExtensionsPerShape)
            {
                diagnostics.Add(Error("face-bake-authority-extension-count-invalid",
                    $"shapeTriInputs[{index}].extendedAssetIds must be an explicit array with at most {MaximumExtensionsPerShape} entries."));
                continue;
            }

            ImmutableArray<string>.Builder extensions =
                ImmutableArray.CreateBuilder<string>(row.ExtendedAssetIds.Count);
            HashSet<string> rowAssets = new(StringComparer.Ordinal);
            foreach (string? id in new[] { race, chargen, mesh })
            {
                if (id is not null && !rowAssets.Add(id))
                {
                    diagnostics.Add(Error("face-bake-authority-shape-tri-duplicate",
                        $"Carrier '{row.CarrierShapeName}' assigns one asset to multiple base TRI roles."));
                }
            }
            foreach (string? extensionId in row.ExtendedAssetIds)
            {
                if (!TryAssetReference(extensionId, assets, ".tri", "tri", usedAssetIds,
                        assetCategories, diagnostics, out DeclaredAsset extension))
                {
                    continue;
                }
                if (!extension.Authority.AssetPath.Value.StartsWith(
                        "meshes/actors/character/facegenmorphs/morphs/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error("face-bake-authority-extension-path-invalid",
                        $"Extended TRI '{extension.Id}' is outside the canonical FaceGenMorphs/morphs path."));
                }
                if (!rowAssets.Add(extension.Id))
                {
                    diagnostics.Add(Error("face-bake-authority-shape-tri-duplicate",
                        $"Carrier '{row.CarrierShapeName}' repeats TRI asset '{extension.Id}'."));
                    continue;
                }
                extensions.Add(extension.Id);
            }
            if (race is null && chargen is null && mesh is null && extensions.Count == 0)
            {
                diagnostics.Add(Error("face-bake-authority-shape-tri-empty",
                    $"Carrier '{row.CarrierShapeName}' declares no TRI role."));
            }
            if (chargen is null && (race is not null || extensions.Count > 0))
            {
                diagnostics.Add(Error("face-bake-authority-hostless-tri-invalid",
                    $"Carrier '{row.CarrierShapeName}' may not declare race/extended TRIs without a chargen host."));
            }
            result.Add(new TriLink(row.CarrierShapeName!, race, chargen, mesh,
                extensions.ToImmutable()));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<FormReference> MapRecordOnlyMappedHeadParts(
        List<RecordOnlyMappedHeadPartDto?> rows,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> recordPlugins,
        ImmutableArray<CarrierLink> carriers,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<FormReference>.Builder result =
            ImmutableArray.CreateBuilder<FormReference>(rows.Count);
        HashSet<string> recordPluginNames = recordPlugins
            .Select(item => item.Plugin.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> carrierHeadParts = carriers.Select(item => FormKey(item.HeadPart))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecordOnlyMappedHeadPartDto row in rows.Cast<RecordOnlyMappedHeadPartDto>())
        {
            if (!TryFormReference(row.HeadPart, out FormReference headPart) ||
                !recordPluginNames.Contains(headPart.Plugin.Value))
            {
                diagnostics.Add(Error("face-bake-authority-record-only-headpart-invalid",
                    $"recordOnlyMappedHeadParts[{row.Order}] must name a canonical FormReference backed by recordPlugins."));
                continue;
            }

            string key = FormKey(headPart);
            if (!distinct.Add(key))
            {
                diagnostics.Add(Error("face-bake-authority-record-only-headpart-duplicate",
                    $"Record-only mapped headpart '{headPart}' occurs more than once."));
                continue;
            }
            if (carrierHeadParts.Contains(key))
            {
                diagnostics.Add(Error("face-bake-authority-record-only-headpart-carrier-overlap",
                    $"Record-only mapped headpart '{headPart}' also has FaceGeom carrier authority."));
                continue;
            }
            result.Add(headPart);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<AssetPath> MapUnavailableExtensions(
        List<OptionalUnavailableExtensionDto?> rows,
        ImmutableArray<DeclaredAsset> assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<AssetPath>.Builder result =
            ImmutableArray.CreateBuilder<AssetPath>(rows.Count);
        HashSet<string> available = assets.Select(item => item.Authority.AssetPath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);
        foreach (OptionalUnavailableExtensionDto row in rows.Cast<OptionalUnavailableExtensionDto>())
        {
            if (!TryAssetPath(row.AssetPath, out AssetPath path) ||
                !Path.GetExtension(path.Value).Equals(".tri", StringComparison.OrdinalIgnoreCase) ||
                !path.Value.StartsWith("meshes/actors/character/facegenmorphs/morphs/",
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("face-bake-authority-unavailable-extension-invalid",
                    $"optionalUnavailableExtensions[{row.Order}] is not a canonical extension TRI path."));
                continue;
            }
            if (!distinct.Add(path.Value) || available.Contains(path.Value))
            {
                diagnostics.Add(Error("face-bake-authority-unavailable-extension-conflict",
                    $"Unavailable extension '{path}' is duplicated or also declared available."));
                continue;
            }
            result.Add(path);
        }
        return result.ToImmutable();
    }

    private static string? MapOptionalTri(
        string? id,
        string role,
        IReadOnlyDictionary<string, DeclaredAsset> assets,
        HashSet<string> usedAssetIds,
        Dictionary<string, string> assetCategories,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (id is null)
        {
            return null;
        }
        return TryAssetReference(id, assets, ".tri", "tri", usedAssetIds,
            assetCategories, diagnostics, out DeclaredAsset asset)
            ? asset.Id
            : null;
    }

    private static bool TryAssetReference(
        string? id,
        IReadOnlyDictionary<string, DeclaredAsset> assets,
        string extension,
        string category,
        HashSet<string> usedAssetIds,
        Dictionary<string, string> assetCategories,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out DeclaredAsset asset)
    {
        asset = default!;
        if (!IsCanonicalId(id) || !assets.TryGetValue(id!, out DeclaredAsset? found))
        {
            diagnostics.Add(Error("face-bake-authority-asset-reference-invalid",
                $"Asset reference '{id ?? "<null>"}' is absent or noncanonical."));
            return false;
        }
        asset = found;
        if (!Path.GetExtension(asset.Authority.AssetPath.Value)
                .Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("face-bake-authority-asset-role-extension-mismatch",
                $"Asset '{id}' assigned to {category} must end in {extension}."));
            return false;
        }
        if (assetCategories.TryGetValue(asset.Id, out string? priorCategory) &&
            !string.Equals(priorCategory, category, StringComparison.Ordinal))
        {
            diagnostics.Add(Error("face-bake-authority-asset-role-conflict",
                $"Asset '{id}' is assigned to both {priorCategory} and {category} roles."));
            return false;
        }
        assetCategories[asset.Id] = category;
        usedAssetIds.Add(asset.Id);
        return true;
    }

    private static bool ValidateOrderedRows<T>(
        IReadOnlyList<T?>? rows,
        string name,
        int minimum,
        int maximum,
        Func<T, int> getOrder,
        ImmutableArray<Diagnostic>.Builder diagnostics) where T : class
    {
        if (rows is null || rows.Count < minimum || rows.Count > maximum)
        {
            diagnostics.Add(Error("face-bake-authority-row-count-invalid",
                $"{name} must contain {minimum} to {maximum} rows."));
            return false;
        }
        bool valid = true;
        for (int index = 0; index < rows.Count; index++)
        {
            T? row = rows[index];
            if (row is null)
            {
                diagnostics.Add(Error("face-bake-authority-row-null",
                    $"{name}[{index}] may not be null."));
                valid = false;
            }
            else if (getOrder(row) != index)
            {
                diagnostics.Add(Error("face-bake-authority-order-drift",
                    $"{name}[{index}].order must equal its zero-based array position."));
                valid = false;
            }
        }
        return valid;
    }

    private static bool TryProviderKind(
        string? value,
        out SkyrimAssetContentProviderKind kind)
    {
        kind = default;
        if (value == "loose")
        {
            kind = SkyrimAssetContentProviderKind.Loose;
            return true;
        }
        if (value == "bsa")
        {
            kind = SkyrimAssetContentProviderKind.Bsa;
            return true;
        }
        return false;
    }

    private static bool TryPlugin(string? value, out PluginName plugin)
    {
        plugin = default;
        if (!IsSafeText(value, 255))
        {
            return false;
        }
        try
        {
            plugin = new PluginName(value!);
            return plugin.Value == value;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryFormReference(string? value, out FormReference reference)
    {
        reference = default;
        return value is not null && FormReference.TryParse(value, out reference) &&
               reference.ToString() == value;
    }

    private static string FormKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X8}";

    private static bool TryHash(string? value, out Sha256Hash hash)
    {
        hash = default;
        if (value is null || value.Length != 64 || value != value.Trim() ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }
        hash = new Sha256Hash(value);
        return true;
    }

    private static bool TryAssetPath(string? value, out AssetPath path)
    {
        path = default;
        if (value is null || value.Length > 1024 || value.Contains('\\') ||
            value != value.Trim())
        {
            return false;
        }
        try
        {
            path = new AssetPath(value);
            return path.Value == value;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryWorkspaceRelativePath(
        WorkspacePath root,
        string? value,
        out WorkspacePath path)
    {
        path = default;
        if (!TryAssetPath(value, out AssetPath relative))
        {
            return false;
        }
        try
        {
            path = new WorkspacePath(Path.Combine(root.Value,
                relative.Value.Replace('/', Path.DirectorySeparatorChar)));
            string roundTrip = Path.GetRelativePath(root.Value, path.Value)
                .Replace(Path.DirectorySeparatorChar, '/');
            return path.IsUnder(root) && roundTrip == value;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCanonicalId(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value == value.Trim() && value.All(character => !char.IsControl(character));

    private static bool IsZeroHash(Sha256Hash hash) =>
        hash.Value.All(character => character == '0');

    private sealed record DeclaredAsset(string Id, SkyrimAssetContentAuthority Authority);

    private sealed record CatalogLink(PluginName Plugin, string AssetId);

    private sealed record TriLink(
        string CarrierShapeName,
        string? RaceAssetId,
        string? ChargenAssetId,
        string? MeshAssetId,
        ImmutableArray<string> ExtendedAssetIds);

    private sealed record CarrierLink(
        FormReference HeadPart,
        string ModelAssetId,
        string ModelShapeName,
        string CarrierShapeName,
        Sha256Hash ExpectedModelPositionSha256,
        Sha256Hash ExpectedModelTopologySha256,
        Sha256Hash ExpectedCarrierTopologySha256);

    private sealed record ParsedAuthority(
        string AuthorityId,
        ImmutableArray<PluginName> LoadedPlugins,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> RecordPlugins,
        ImmutableArray<DeclaredAsset> Assets,
        ImmutableArray<CatalogLink> Catalog,
        ImmutableArray<TriLink> TriInputs,
        ImmutableArray<CarrierLink> Carriers,
        ImmutableArray<FormReference> RecordOnlyMappedHeadParts,
        ImmutableArray<AssetPath> OptionalUnavailableExtensions);
}
