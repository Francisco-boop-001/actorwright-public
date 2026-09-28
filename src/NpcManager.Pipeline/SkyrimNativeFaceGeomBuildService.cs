using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Resolves and bakes one discovered Skyrim NPC's native record/NIF/TRI
/// closure, then writes a product-owned carrier through the two-pass seam.
/// Optional load-order sidecar custom/sculpt state is an immutable typed input
/// captured before the batch write boundary.
/// </summary>
public sealed class SkyrimNativeFaceGeomBuildService(
    ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
    ISkyrimFaceRecordRouteResolver recordRouteResolver,
    ISkyrimFaceMorphSnapshotService morphSnapshotService,
    ISkyrimAssetAuthorityPlanner assetAuthorityPlanner,
    ISkyrimAssetContentResolver assetContentResolver,
    ISkyrimRaceMenuCatalogAuthorityLoader catalogAuthorityLoader,
    IRaceMenuSliderCatalogParserCore catalogParser,
    ISseSelectedHeadpartNifGeometryReader geometryReader,
    ISkyrimRaceMenuFaceBakeService faceBakeService,
    ISseFaceGeomCarrierMaterializationService materializationService,
    IExternalHeadPartProviderNifReader? externalHeadPartProviderReader = null,
    IExternalHeadPartProviderSidecarAuthorityResolver?
        externalHeadPartProviderSidecarResolver = null,
    IFinalFaceGeomTexturePlanService? finalTexturePlanService = null,
    IExternalHeadPartDependencyDiscovery? externalHeadPartDependencyDiscovery = null,
    IExternalHeadPartFaceGeomExclusionVerifier?
        externalHeadPartFaceGeomExclusionVerifier = null)
    : ISkyrimNativeFaceGeomBuildService
{
    public async ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(
        SkyrimNativeFaceGeomBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        if (request.ExpectedExternalHeadPartDescriptor is not null &&
            externalHeadPartDependencyDiscovery is null)
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-discovery-missing",
                "An expected external head-part descriptor requires the injected discovery dependency."));
            return Refused(diagnostics);
        }

        SkyrimFaceRecordPluginAuthorityResult plugins = await pluginAuthorityLoader.LoadAsync(
            new SkyrimFaceRecordPluginAuthorityRequest(
                request.Edition, request.DataRoot, request.PluginOrder)
            {
                StagedPluginAuthorities = request.StagedPluginAuthorities
            },
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, plugins.Diagnostics);
        if (!plugins.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<SkyrimFaceRecordHeadPartSelection> selections =
            request.Target.HeadParts.Select(item =>
                    new SkyrimFaceRecordHeadPartSelection(item, []))
                .ToImmutableArray();
        SkyrimFaceRecordRouteResult routed = await recordRouteResolver.ResolveAsync(
            new SkyrimFaceRecordRouteRequest(
                request.Edition, request.Target.Race, request.Target.Sex,
                selections, plugins.Authorities),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, routed.Diagnostics);
        if (!routed.Accepted || routed.Route is null || HasErrors(diagnostics))
            return Refused(diagnostics);
        SkyrimFaceRecordRoute route = routed.Route;

        ImmutableHashSet<FormReference> externalHeadPartReferences = [];
        ExternalHeadPartDependencyDescriptor? externalHeadPartDescriptor = null;
        if (externalHeadPartDependencyDiscovery is null)
        {
            // Preserve the legacy Dint-only input path until callers opt into
            // the generic descriptor discovery seam.
            externalHeadPartReferences = ResolveExternalHeadPartReferences(
                request,
                route,
                plugins.Authorities,
                diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);
        }

        SkyrimFaceRecordPluginAuthority[] winners = plugins.Authorities
            .Where(item => string.Equals(item.Plugin.Value,
                request.Target.WinningPlugin.Value,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (winners.Length != 1)
        {
            diagnostics.Add(Error("skyrim-native-facegeom-winning-provider",
                "The target winning plugin must resolve to exactly one hash-bound provider."));
            return Refused(diagnostics);
        }
        SkyrimFaceMorphSnapshotResult snapshot = await morphSnapshotService.ReadAsync(
            new SkyrimFaceMorphSnapshotRequest(
                request.Edition, winners[0].Path, winners[0].ExpectedSha256,
                request.Target.FormId),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, snapshot.Diagnostics);
        if (!snapshot.Resolved || snapshot.Snapshot is null || HasErrors(diagnostics))
            return Refused(diagnostics);

        SkyrimRaceMenuCatalogAuthorityResult catalogAuthority =
            await catalogAuthorityLoader.LoadAsync(
                new SkyrimRaceMenuCatalogAuthorityRequest(
                    request.Edition, request.DataRoot, request.PluginOrder),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, catalogAuthority.Diagnostics);
        if (!catalogAuthority.Accepted ||
            catalogAuthority.CatalogRequest is null || HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }
        SkyrimRaceMenuCatalogParseResult catalog = catalogParser.Parse(
            catalogAuthority.CatalogRequest);
        AddDistinct(diagnostics, catalog.Diagnostics);
        if (!catalog.Accepted || catalog.Catalog is null || HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        ImmutableArray<AssetPath> requiredAssets = route.HeadParts
            .SelectMany(item => new[] { item.ModelNif }
                .Concat(item.TriRoutes
                    .Where(tri => !SkyrimExternalHairTriOmissionPolicy
                        .IsAllowedMissingHairNam0(item, tri))
                    .Select(tri => tri.Path)))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        HashSet<string> requiredPaths = requiredAssets
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AssetPath> optionalExtensions =
            ApplicableExtendedTris(route, catalog.Catalog)
                .Where(item => !requiredPaths.Contains(item.Value))
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        SkyrimAssetAuthorityPlanResult authorityPlan = await assetAuthorityPlanner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                request.Edition, request.DataRoot, requiredAssets,
                optionalExtensions),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, authorityPlan.Diagnostics);
        if (!authorityPlan.Accepted || HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimAssetContentResolutionResult resolvedAssets =
            await assetContentResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    request.DataRoot,
                    authorityPlan.Authorities.Select(ToContentAuthority)
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, resolvedAssets.Diagnostics);
        if (!resolvedAssets.Resolved || HasErrors(diagnostics)) return Refused(diagnostics);
        Dictionary<string, ResolvedSkyrimAssetContent> contentByPath =
            resolvedAssets.Assets.ToDictionary(item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase);

        if (externalHeadPartDependencyDiscovery is not null)
        {
            ExternalHeadPartDependencyDiscoveryResult discovery =
                await externalHeadPartDependencyDiscovery.DiscoverAsync(
                    new ExternalHeadPartDependencyDiscoveryRequest(
                        request.DataRoot,
                        request.PluginOrder,
                        route,
                        request.Target.Sex,
                        request.ExpectedExternalHeadPartDescriptor),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, discovery.Diagnostics);
            if (request.ExpectedExternalHeadPartDescriptor is not null &&
                discovery.Status != ExternalHeadPartDependencyDiscoveryStatus.Accepted)
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-descriptor-status",
                    "An expected external head-part descriptor requires an Accepted discovery result."));
                return Refused(diagnostics);
            }
            if (discovery.Status == ExternalHeadPartDependencyDiscoveryStatus.Refused ||
                HasErrors(diagnostics))
                return Refused(diagnostics);
            if (discovery.Status == ExternalHeadPartDependencyDiscoveryStatus.NotApplicable &&
                discovery.Descriptor is not null)
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-descriptor-status",
                    "A not-applicable external discovery result may not carry a descriptor."));
                return Refused(diagnostics);
            }
            if (discovery.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted)
            {
                if (discovery.Descriptor is null)
                {
                    diagnostics.Add(Error(
                        "skyrim-native-facegeom-external-descriptor-missing",
                        "Accepted external head-part discovery returned no descriptor."));
                    return Refused(diagnostics);
                }
                externalHeadPartDescriptor = discovery.Descriptor;
                try
                {
                    byte[] descriptorBytes =
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeDescriptor(externalHeadPartDescriptor);
                    if (request.ExpectedExternalHeadPartDescriptor is
                            { } expectedDescriptor &&
                        !descriptorBytes.AsSpan().SequenceEqual(
                            ExternalHeadPartDependencyDescriptorCodec
                                .SerializeDescriptor(expectedDescriptor)))
                    {
                        diagnostics.Add(Error(
                            "skyrim-native-facegeom-external-descriptor-mismatch",
                            "The discovered external descriptor does not match the expected descriptor."));
                        return Refused(diagnostics);
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or
                    ArgumentException)
                {
                    diagnostics.Add(Error(
                        "skyrim-native-facegeom-external-descriptor-invalid",
                        exception.Message));
                    return Refused(diagnostics);
                }

                externalHeadPartReferences = externalHeadPartDescriptor.Members
                    .Select(item => item.OriginForm)
                    .ToImmutableHashSet();
                ImmutableHashSet<FormReference> routedReferences = route.HeadParts
                    .Select(item => item.Reference)
                    .ToImmutableHashSet();
                if (externalHeadPartReferences.Count == 0 ||
                    externalHeadPartReferences.Any(item =>
                        !routedReferences.Contains(item)))
                {
                    diagnostics.Add(Error(
                        "skyrim-native-facegeom-external-descriptor-closure",
                        "The discovered external descriptor does not map entirely to the resolved head-part route."));
                    return Refused(diagnostics);
                }
            }
        }

        var externalProviderDependencies =
            ImmutableArray.CreateBuilder<AssetPath>();
        var externalProviderSidecarAuthorities =
            ImmutableArray.CreateBuilder<SkyrimAssetAuthority>();
        if (!externalHeadPartReferences.IsEmpty &&
            externalHeadPartDescriptor is null)
        {
            IExternalHeadPartProviderNifReader providerReader =
                externalHeadPartProviderReader ??
                new ExternalHeadPartProviderNifReader();
            foreach (SkyrimFaceHeadPartRecordRoute headPart in
                     route.HeadParts.Where(item =>
                         externalHeadPartReferences.Contains(item.Reference)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!contentByPath.TryGetValue(
                        headPart.ModelNif.Value,
                        out ResolvedSkyrimAssetContent? model))
                {
                    diagnostics.Add(Error(
                        "skyrim-native-facegeom-external-model-content",
                        $"External headpart '{headPart.Reference}' model '{headPart.ModelNif}' is absent from the resolved provider closure."));
                    continue;
                }

                ExternalHeadPartProviderNifReadResult externalRead =
                    providerReader.Read(
                        new ExternalHeadPartProviderNifReadRequest(
                            model.AssetPath,
                            model.ContentSha256,
                            model.Content));
                AddDistinct(diagnostics, externalRead.Diagnostics);
                if (!externalRead.Accepted || HasErrors(diagnostics))
                    continue;
                foreach (AssetPath dependency in externalRead.Dependencies)
                    externalProviderDependencies.Add(dependency);
                foreach (AssetPath sidecar in externalRead.ProviderSidecars)
                {
                    if (externalHeadPartProviderSidecarResolver is null)
                    {
                        diagnostics.Add(Error(
                            "skyrim-native-facegeom-external-sidecar-resolver",
                            "An external provider sidecar was discovered without an admitted sidecar authority resolver."));
                        continue;
                    }
                    ExternalHeadPartProviderSidecarAuthorityResult sidecarResult =
                        externalHeadPartProviderSidecarResolver.Resolve(
                            new ExternalHeadPartProviderSidecarAuthorityRequest(
                                request.DataRoot,
                                sidecar,
                                headPart.Reference,
                                headPart.Provider.Plugin,
                                headPart.Provider.Sha256));
                    AddDistinct(diagnostics, sidecarResult.Diagnostics);
                    if (sidecarResult.Accepted && sidecarResult.Authority is not null)
                        externalProviderSidecarAuthorities.Add(
                            sidecarResult.Authority);
                }
            }
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
        }

        ImmutableArray<AssetPath> allRequiredAssets = requiredAssets
            .AddRange(externalProviderDependencies)
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        requiredPaths = allRequiredAssets
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        optionalExtensions = optionalExtensions
            .Where(item => !requiredPaths.Contains(item.Value))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (allRequiredAssets.Length != requiredAssets.Length)
        {
            authorityPlan = await assetAuthorityPlanner.PlanAsync(
                new SkyrimAssetAuthorityPlanRequest(
                    request.Edition,
                    request.DataRoot,
                    allRequiredAssets,
                    optionalExtensions),
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, authorityPlan.Diagnostics);
            if (!authorityPlan.Accepted || HasErrors(diagnostics))
                return Refused(diagnostics);

            resolvedAssets = await assetContentResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    request.DataRoot,
                    authorityPlan.Authorities.Select(ToContentAuthority)
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, resolvedAssets.Diagnostics);
            if (!resolvedAssets.Resolved || HasErrors(diagnostics))
                return Refused(diagnostics);
            contentByPath = resolvedAssets.Assets.ToDictionary(
                item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        HashSet<string> unavailableExtensions =
            (authorityPlan.UnavailableOptionalAssets.IsDefault
                ? []
                : authorityPlan.UnavailableOptionalAssets)
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IFinalFaceGeomTexturePlanService texturePlanner =
            finalTexturePlanService ?? new FinalFaceGeomTexturePlanService();
        var parts = ImmutableArray.CreateBuilder<PendingPart>(route.HeadParts.Length);
        var omittedDummies = ImmutableArray.CreateBuilder<SkyrimNativeFaceGeomOmittedShape>();
        var referencedTexturePaths = new Dictionary<string, AssetPath>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimFaceHeadPartRecordRoute headPart in route.HeadParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (externalHeadPartReferences.Contains(headPart.Reference))
                continue;
            if (!contentByPath.TryGetValue(headPart.ModelNif.Value,
                    out ResolvedSkyrimAssetContent? model))
            {
                diagnostics.Add(Error("skyrim-native-facegeom-model-content",
                    $"Resolved asset closure omitted model '{headPart.ModelNif}'."));
                continue;
            }
            SseSelectedHeadpartNifGeometryReadResult geometry = geometryReader.Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    model.AssetPath, model.ContentSha256, model.Content));
            AddDistinct(diagnostics, geometry.Diagnostics);
            if (!geometry.Accepted || geometry.Document is null ||
                geometry.Document.Shapes.Length != 1)
            {
                if (geometry.Accepted && geometry.Document is not null)
                {
                    diagnostics.Add(Error("skyrim-native-facegeom-model-shape-count",
                        $"Model '{headPart.ModelNif}' must expose exactly one admitted dynamic shape; found {geometry.Document.Shapes.Length}."));
                }
                continue;
            }
            SseSelectedHeadpartNifRestShape shape = geometry.Document.Shapes[0];
            if (shape.IsShaderlessDummy)
            {
                omittedDummies.Add(new(headPart.Reference, model.AssetPath, model.ContentSha256, shape.Name));
                continue;
            }
            SseSelectedHeadpartMaterial? material =
                shape.Materials.Length == 1 ? shape.Materials[0] : null;
            if (material is null || material.TextureSlotCount is < 1 or > 32)
            {
                diagnostics.Add(Error("skyrim-native-facegeom-material-shape",
                    $"Model '{headPart.ModelNif}' must expose one bounded texture-set material."));
                continue;
            }
            string[] providerSlots = new string[material.TextureSlotCount];
            foreach (SseSelectedHeadpartTextureSlot slot in material.TextureSlots)
                providerSlots[slot.Slot] = slot.Path.Value;
            bool usesFaceTint = headPart.EffectiveType == NpcHeadPartType.Face;
            FinalFaceGeomTexturePlan finalTexturePlan = texturePlanner.Plan(
                new FinalFaceGeomTexturePlanRequest(
                    providerSlots.ToImmutableArray(),
                    headPart.TextureSet,
                    usesFaceTint,
                    request.FaceTintPath));
            AddDistinct(diagnostics, finalTexturePlan.Diagnostics);
            if (!finalTexturePlan.Accepted) continue;
            foreach (AssetPath texture in finalTexturePlan.RequiredInputTextures)
                referencedTexturePaths.TryAdd(texture.Value, texture);

            if (string.IsNullOrWhiteSpace(headPart.EditorId))
            {
                diagnostics.Add(Error("skyrim-native-facegeom-editorid",
                    $"Headpart '{headPart.Reference}' requires a stable EditorID for its output shape name."));
                continue;
            }
            string carrierShapeName = headPart.EditorId;
            SkyrimRaceMenuFaceBakeShapeTriInputs? triInputs = BuildTriInputs(
                carrierShapeName, headPart, catalog.Catalog,
                contentByPath, unavailableExtensions, diagnostics);
            if (triInputs is null) continue;
            parts.Add(new PendingPart(
                headPart, model, shape, carrierShapeName,
                new SkyrimRaceMenuFaceBakeCarrierShapeBinding(
                    carrierShapeName,
                    triInputs.ChargenMorphTri?.SourcePath,
                    shape.VertexCount,
                    shape.TopologySha256,
                    shape.PackedPositionSha256,
                    shape.RestPositions,
                    shape.PackedNormalSentinels)
                {
                    NativeHeadPart = headPart.Reference,
                    NativeModelNif = headPart.ModelNif
                },
                triInputs,
                finalTexturePlan));
        }
        int expectedRenderablePartCount =
            route.HeadParts.Length - externalHeadPartReferences.Count - omittedDummies.Count;
        if (HasErrors(diagnostics) || parts.Count != expectedRenderablePartCount)
            return Refused(diagnostics);

        ImmutableArray<AssetPath> platformTextures = referencedTexturePaths.Values
            .Where(IsSkyrimPlatformTexture)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (!platformTextures.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                "skyrim-native-facegeom-platform-textures",
                DiagnosticSeverity.Info,
                $"Retained {platformTextures.Length} Skyrim platform-provided texture route(s) without misclassifying them as external mod dependencies: {string.Join(", ", platformTextures)}."));
        }
        ImmutableArray<AssetPath> requiredTextures = referencedTexturePaths.Values
            .Where(item => !IsSkyrimPlatformTexture(item) &&
                           !requiredPaths.Contains(item.Value))
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<SkyrimAssetAuthority> textureAuthorities = [];
        if (!requiredTextures.IsDefaultOrEmpty)
        {
            SkyrimAssetAuthorityPlanResult texturePlan =
                await assetAuthorityPlanner.PlanAsync(
                    new SkyrimAssetAuthorityPlanRequest(
                        request.Edition, request.DataRoot, requiredTextures),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, texturePlan.Diagnostics);
            if (!texturePlan.Accepted || HasErrors(diagnostics))
                return Refused(diagnostics);
            textureAuthorities = texturePlan.Authorities;
        }
        ImmutableArray<SkyrimAssetAuthority> externalDependencies =
            authorityPlan.Authorities.AddRange(textureAuthorities)
                .Where(item =>
                    item.AssetPath.Value.EndsWith(".nif",
                        StringComparison.OrdinalIgnoreCase) ||
                    item.AssetPath.Value.EndsWith(".tri",
                        StringComparison.OrdinalIgnoreCase) ||
                    item.AssetPath.Value.EndsWith(".dds",
                        StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

        ImmutableArray<PendingPart> admittedParts = parts.ToImmutable();
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> customMorphs =
            request.SidecarOverlay?.CustomMorphs.IsDefault == false
                ? request.SidecarOverlay.CustomMorphs
                : [];
        ImmutableArray<RaceMenuSculptPart> sculptParts = ResolveSculptParts(
            request.SidecarOverlay, admittedParts, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimRaceMenuFaceBakeResult baked = faceBakeService.Bake(
            new SkyrimRaceMenuFaceBakeRequest(
                route.Race.MorphRaceEditorId,
                request.Target.Sex == NpcSex.Female,
                snapshot.Snapshot,
                route.Race.Keywords.Select(item => item.EditorId).ToImmutableArray(),
                customMorphs,
                sculptParts,
                request.Target.Weight,
                catalogAuthority.CatalogRequest,
                admittedParts.Select(item => item.Binding).ToImmutableArray(),
                admittedParts.Select(item => item.TriInputs).ToImmutableArray(),
                authorityPlan.UnavailableOptionalAssets.IsDefault
                    ? []
                    : authorityPlan.UnavailableOptionalAssets));
        AddDistinct(diagnostics, baked.Diagnostics);
        if (!baked.Accepted || baked.Shapes.Length != admittedParts.Length || HasErrors(diagnostics))
            return Refused(diagnostics);

        Dictionary<string, SkyrimRaceMenuFaceBakeShapeOutput> bakedByName =
            baked.Shapes.ToDictionary(item => item.CarrierShapeName,
                StringComparer.Ordinal);
        var assemblyParts = ImmutableArray.CreateBuilder<SseFaceGeomCarrierAssemblyPart>(
            admittedParts.Length);
        var shapeEvidence = ImmutableArray.CreateBuilder<SkyrimNativeFaceGeomShapeEvidence>(
            admittedParts.Length);
        foreach (PendingPart part in admittedParts)
        {
            if (!bakedByName.TryGetValue(part.CarrierShapeName,
                    out SkyrimRaceMenuFaceBakeShapeOutput? output))
            {
                diagnostics.Add(Error("skyrim-native-facegeom-bake-closure",
                    $"Bake output omitted carrier shape '{part.CarrierShapeName}'."));
                continue;
            }
            bool usesFaceTint = part.Record.EffectiveType == NpcHeadPartType.Face;
            assemblyParts.Add(new SseFaceGeomCarrierAssemblyPart(
                part.Record.Reference,
                part.Model.AssetPath,
                part.Model.ContentSha256,
                part.Model.Content,
                part.CarrierShapeName,
                usesFaceTint,
                output.FinalPositions,
                part.Shape.PackedNormalBytes)
            {
                TextureSetOverride = part.Record.TextureSet is null
                    ? []
                    : part.FinalTexturePlan.NifSlots.Take(8).ToImmutableArray(),
                HairTintPackedRgb = part.Record.EffectiveType == NpcHeadPartType.Hair
                    ? request.EffectiveHairColorPackedRgb
                    : null
            });
            shapeEvidence.Add(new SkyrimNativeFaceGeomShapeEvidence(
                part.Record.Reference,
                part.Record.EffectiveType,
                part.Model.AssetPath,
                part.Model.ContentSha256,
                part.Shape.Name,
                part.CarrierShapeName,
                output.VertexCount,
                output.TopologySha256,
                output.BasePositionSha256,
                output.FinalPositionSha256,
                externalHeadPartDescriptor is null ? output.TriEvidence : output.TriEvidence
                    .Select(evidence => evidence with
                    {
                        EvidenceId = ExternalHeadPartDependencyDescriptorCodec.ComputeTriEvidenceId(evidence)
                    }).ToImmutableArray(),
                usesFaceTint));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var assembly = new SseFaceGeomCarrierAssemblyRequest(
            assemblyParts.ToImmutable(), request.FaceTintPath,
            request.SkeletonAuthority);
        SseFaceGeomCarrierMaterializationAnalysisResult analysis =
            await materializationService.AnalyzeAsync(
                new SseFaceGeomCarrierMaterializationAnalyzeRequest(
                    assembly, request.OutputNif),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, analysis.Diagnostics);
        if (!analysis.Accepted || analysis.Proposal is null || HasErrors(diagnostics))
            return Refused(diagnostics);
        SseFaceGeomCarrierMaterializationResult materialized =
            await materializationService.ApplyAsync(
                analysis.Proposal, cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, materialized.Diagnostics);
        if (!materialized.Written || !materialized.Verified ||
            materialized.Artifact is null || HasErrors(diagnostics))
            return Refused(diagnostics);

        ExternalHeadPartFaceGeomExclusionAttestation? exclusionAttestation = null;
        if (externalHeadPartDescriptor is not null)
        {
            if (externalHeadPartFaceGeomExclusionVerifier is null)
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-verifier-missing",
                    "An accepted external descriptor requires an injected FaceGeom exclusion verifier."));
                RollbackMaterializedOutput(
                    request.OutputNif,
                    materialized.Artifact.OutputSha256,
                    materialized.Artifact.OutputByteLength,
                    diagnostics);
                return Refused(diagnostics);
            }

            ExternalHeadPartFaceGeomExclusionVerificationResult exclusion =
                await externalHeadPartFaceGeomExclusionVerifier.VerifyAsync(
                    new ExternalHeadPartFaceGeomExclusionVerificationRequest(
                        request.OutputNif,
                        materialized.Artifact.OutputSha256,
                        materialized.Artifact.OutputByteLength,
                        externalHeadPartDescriptor,
                        shapeEvidence.ToImmutable()),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, exclusion.Diagnostics);
            if (!exclusion.Verified || exclusion.Attestation is null ||
                !ValidateExternalExclusionAttestation(
                    exclusion.Attestation,
                    externalHeadPartDescriptor,
                    request.OutputNif,
                    materialized.Artifact,
                    shapeEvidence.ToImmutable(),
                    diagnostics) || HasErrors(diagnostics))
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-exclusion",
                    "The materialized FaceGeom carrier did not produce a bound external exclusion attestation."));
                RollbackMaterializedOutput(
                    request.OutputNif,
                    materialized.Artifact.OutputSha256,
                    materialized.Artifact.OutputByteLength,
                    diagnostics);
                return Refused(diagnostics);
            }
            exclusionAttestation = exclusion.Attestation;
        }

        var artifact = new SkyrimNativeFaceGeomBuildArtifact(
            "1", "skyrim-native-facegeom", request.Target, route,
            snapshot.Snapshot, plugins.Authorities, authorityPlan.Authorities,
            shapeEvidence.ToImmutable(), materialized.Artifact,
            RuntimeAuthority: false,
            CatalogAuthorities: catalogAuthority.Authorities,
            RaceMenuCatalog: catalog.Catalog,
            SidecarOverlay: request.SidecarOverlay)
        {
            ExternalDependencyAuthorities = externalDependencies,
            OmittedShaderlessDummies = omittedDummies.Count == 0 ? null : omittedDummies.ToImmutable(),
            ExternalProviderSidecarAuthorities =
                externalProviderSidecarAuthorities
                    .DistinctBy(item => item.AssetPath.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.AssetPath.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray(),
            ExternalHeadParts =
                request.NativeFaceGeomExternalHeadParts,
            FinalTextureAuthorities = textureAuthorities,
            ExternalHeadPartDependencies = externalHeadPartDescriptor is null
                ? null
                : ImmutableArray.Create(externalHeadPartDescriptor),
            ExternalHeadPartExclusionAttestations = exclusionAttestation is null
                ? null
                : ImmutableArray.Create(exclusionAttestation)
        };
        return new SkyrimNativeFaceGeomBuildResult(
            true, true, artifact, diagnostics.ToImmutable());
    }

    private static SkyrimRaceMenuFaceBakeShapeTriInputs? BuildTriInputs(
        string shapeName,
        SkyrimFaceHeadPartRecordRoute headPart,
        SkyrimRaceMenuSliderCatalog catalog,
        Dictionary<string, ResolvedSkyrimAssetContent> contentByPath,
        IReadOnlySet<string> unavailableExtensions,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var byRole = new Dictionary<SkyrimHdptTriRole, SseTriHeadReadRequest>();
        foreach (SkyrimHdptTriRoute route in headPart.TriRoutes)
        {
            if (!contentByPath.TryGetValue(route.Path.Value,
                    out ResolvedSkyrimAssetContent? asset))
            {
                if (SkyrimExternalHairTriOmissionPolicy
                    .IsAllowedMissingHairNam0(headPart, route))
                    continue;
                diagnostics.Add(Error("skyrim-native-facegeom-tri-content",
                    $"Resolved asset closure omitted TRI '{route.Path}'."));
                continue;
            }
            if (!byRole.TryAdd(route.Role, new SseTriHeadReadRequest(
                    asset.AssetPath, asset.ContentSha256, asset.Content)))
            {
                diagnostics.Add(Error("skyrim-native-facegeom-tri-role",
                    $"Headpart '{headPart.Reference}' repeats TRI role '{route.Role}'."));
            }
        }
        if (HasErrors(diagnostics)) return null;
        byRole.TryGetValue(SkyrimHdptTriRole.RaceMorph, out var race);
        byRole.TryGetValue(SkyrimHdptTriRole.CharGen, out var chargen);
        byRole.TryGetValue(SkyrimHdptTriRole.Mesh, out var mesh);
        ImmutableArray<SseTriHeadReadRequest> extended = chargen is null
            ? []
            : catalog.MorphExtensions
                .Where(item => string.Equals(item.BaseChargenTri.Value,
                    chargen.SourcePath.Value,
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.ExtendedTriPaths)
                .DistinctBy(item => item.Value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(path => ToTriReadRequest(path, contentByPath,
                    unavailableExtensions, headPart, diagnostics))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToImmutableArray();
        if (HasErrors(diagnostics)) return null;
        return new SkyrimRaceMenuFaceBakeShapeTriInputs(
            shapeName, race, chargen, mesh, extended);
    }

    private static SseTriHeadReadRequest? ToTriReadRequest(
        AssetPath path,
        Dictionary<string, ResolvedSkyrimAssetContent> contentByPath,
        IReadOnlySet<string> unavailableExtensions,
        SkyrimFaceHeadPartRecordRoute headPart,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!contentByPath.TryGetValue(path.Value,
                out ResolvedSkyrimAssetContent? asset))
        {
            if (unavailableExtensions.Contains(path.Value)) return null;
            diagnostics.Add(Error("skyrim-native-facegeom-extended-tri-content",
                $"Headpart '{headPart.Reference}' requires unresolved RaceMenu extension TRI '{path}'."));
            return null;
        }
        return new SseTriHeadReadRequest(
            asset.AssetPath, asset.ContentSha256, asset.Content);
    }

    private static IEnumerable<AssetPath> ApplicableExtendedTris(
        SkyrimFaceRecordRoute route,
        SkyrimRaceMenuSliderCatalog catalog)
    {
        HashSet<string> chargenHosts = route.HeadParts
            .SelectMany(item => item.TriRoutes)
            .Where(item => item.Role == SkyrimHdptTriRole.CharGen)
            .Select(item => item.Path.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.MorphExtensions
            .Where(item => chargenHosts.Contains(item.BaseChargenTri.Value))
            .SelectMany(item => item.ExtendedTriPaths);
    }

    internal static bool IsSkyrimPlatformTexture(AssetPath path)
    {
        if (path.Value.StartsWith(
                "textures/cubemaps/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Value.Equals(
                   "textures/actors/character/female/femalehead.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/female/femalehead_msn.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/female/femalehead_s.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/female/femalehead_sk.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/male/malehead.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/male/malehead_msn.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/male/malehead_s.dds",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Value.Equals(
                   "textures/actors/character/male/malehead_sk.dds",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<RaceMenuSculptPart> ResolveSculptParts(
        SkyrimFaceGenSidecarOverlay? overlay,
        ImmutableArray<PendingPart> parts,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (overlay is null) return [];
        ImmutableArray<BodySidecarSculptPart> declared =
            overlay.SculptParts.IsDefault ? [] : overlay.SculptParts;
        if (!declared.IsEmpty)
        {
            var result = ImmutableArray.CreateBuilder<RaceMenuSculptPart>(declared.Length);
            HashSet<string> processedHosts = new(StringComparer.OrdinalIgnoreCase);
            foreach (BodySidecarSculptPart sculpt in declared)
            {
                string wanted = NormalizeTriIdentity(sculpt.Host);
                if (!processedHosts.Add(wanted)) continue;
                BodySidecarSculptPart[] sameHost = declared
                    .Where(item => NormalizeTriIdentity(item.Host).Equals(
                        wanted, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sameHost.Skip(1).Any(item => !SculptEquivalent(sculpt, item)))
                {
                    diagnostics.Add(Error("skyrim-native-facegeom-sidecar-sculpt-duplicate",
                        $"Sidecar sculpt host '{sculpt.Host}' has conflicting duplicate blocks."));
                    continue;
                }

                var matches = parts
                    .Select(item => new
                    {
                        Part = item,
                        MatchingPaths = EnumerateTriInputPaths(item.TriInputs)
                            .Where(path => NormalizeTriIdentity(path.Value).Equals(
                                wanted, StringComparison.OrdinalIgnoreCase))
                            .ToArray()
                    })
                    .Where(item => item.MatchingPaths.Length > 0)
                    .ToArray();
                if (matches.Length == 0)
                {
                    diagnostics.Add(Error("skyrim-native-facegeom-sidecar-sculpt-host",
                        $"Sidecar sculpt host '{sculpt.Host}' matches no admitted carrier shape TRI input."));
                    continue;
                }
                int[] vertexCounts = matches
                    .Select(item => item.Part.Binding.VertexCount)
                    .Distinct()
                    .ToArray();
                if (vertexCounts.Length != 1)
                {
                    string counts = string.Join(", ", vertexCounts.Order());
                    diagnostics.Add(Error("skyrim-native-facegeom-sidecar-sculpt-topology",
                        $"Sidecar sculpt host '{sculpt.Host}' fans out to incompatible carrier vertex counts [{counts}]."));
                    continue;
                }
                result.Add(new RaceMenuSculptPart(
                    matches[0].MatchingPaths[0].Value,
                    vertexCounts[0],
                    sculpt.Vertices.IsDefault ? [] : sculpt.Vertices,
                    HasVertexCount: true,
                    HasData: true));
            }
            return result.ToImmutable();
        }

        ImmutableArray<RaceMenuSculptVertex> legacy =
            overlay.LegacyHeadSculpt.IsDefault ? [] : overlay.LegacyHeadSculpt;
        if (legacy.IsEmpty) return [];
        PendingPart[] faceMatches = parts.Where(item =>
                item.Record.EffectiveType == NpcHeadPartType.Face &&
                item.Binding.ChargenMorphHost is not null)
            .ToArray();
        if (faceMatches.Length != 1)
        {
            diagnostics.Add(Error("skyrim-native-facegeom-sidecar-legacy-sculpt",
                $"Legacy head-only sidecar sculpt requires exactly one admitted Face chargen host; found {faceMatches.Length}."));
            return [];
        }
        PendingPart face = faceMatches[0];
        return
        [
            new RaceMenuSculptPart(
                face.Binding.ChargenMorphHost!.Value.Value,
                face.Binding.VertexCount,
                legacy,
                HasVertexCount: true,
                HasData: true)
        ];
    }

    private static bool SculptEquivalent(
        BodySidecarSculptPart left,
        BodySidecarSculptPart right) =>
        NormalizeTriIdentity(left.Host).Equals(
            NormalizeTriIdentity(right.Host), StringComparison.OrdinalIgnoreCase) &&
        (left.Vertices.IsDefault ? [] : left.Vertices)
        .SequenceEqual(right.Vertices.IsDefault ? [] : right.Vertices);

    private static IEnumerable<AssetPath> EnumerateTriInputPaths(
        SkyrimRaceMenuFaceBakeShapeTriInputs inputs)
    {
        if (inputs.RaceMorphTri is not null) yield return inputs.RaceMorphTri.SourcePath;
        if (inputs.ChargenMorphTri is not null) yield return inputs.ChargenMorphTri.SourcePath;
        if (inputs.MeshMorphTri is not null) yield return inputs.MeshMorphTri.SourcePath;
        if (!inputs.ExtendedMorphTris.IsDefault)
        {
            foreach (SseTriHeadReadRequest tri in inputs.ExtendedMorphTris)
            {
                if (tri is not null) yield return tri.SourcePath;
            }
        }
    }

    private static string NormalizeTriIdentity(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
            ? normalized[7..]
            : normalized;
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
            authority.AssetPath, authority.ContentLength, authority.ContentSha256);

    private static void ValidateRequest(
        SkyrimNativeFaceGeomBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("skyrim-native-facegeom-edition",
                "Native FaceGeom builds support Skyrim Special Edition only."));
        if (request.Target is null || request.Target.FormId.Value == 0 ||
            request.Target.HeadParts.IsDefaultOrEmpty ||
            !float.IsFinite(request.Target.Weight) ||
            request.Target.Weight is < 0F or > 100F)
            diagnostics.Add(Error("skyrim-native-facegeom-target",
                "Target must be a discovered NPC with FormID, headparts, and weight 0-100."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
            diagnostics.Add(Error("skyrim-native-facegeom-plugin-order",
                "PluginOrder must be an explicit distinct ascending array."));
        if (request.Target is not null &&
            (!request.PluginOrder.Any(item => string.Equals(item.Value,
                 request.Target.OriginatingPlugin.Value,
                 StringComparison.OrdinalIgnoreCase)) ||
             !request.PluginOrder.Any(item => string.Equals(item.Value,
                 request.Target.WinningPlugin.Value,
                 StringComparison.OrdinalIgnoreCase))))
            diagnostics.Add(Error("skyrim-native-facegeom-target-order",
                "Target origin and winning plugins must both be present in PluginOrder."));
        if (request.SidecarOverlay is { } overlay && request.Target is { } target &&
            (!string.Equals(overlay.OriginatingPlugin.Value,
                 target.OriginatingPlugin.Value, StringComparison.OrdinalIgnoreCase) ||
             overlay.FormId != target.FormId || overlay.SourceAuthorities.IsDefaultOrEmpty ||
             overlay.CustomMorphs.IsDefault || overlay.LegacyHeadSculpt.IsDefault ||
             overlay.SculptParts.IsDefault || overlay.TintTextureOverrides.IsDefault))
            diagnostics.Add(Error("skyrim-native-facegeom-sidecar-overlay",
                "Sidecar overlay identity, sources, and face-state arrays must be explicit and match the target."));
        if (string.IsNullOrWhiteSpace(request.FaceTintPath.Value) ||
            !request.FaceTintPath.Value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
            !request.FaceTintPath.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("skyrim-native-facegeom-facetint-path",
                "FaceTintPath must be a canonical textures/*.dds path."));
    }

    private static ImmutableHashSet<FormReference>
        ResolveExternalHeadPartReferences(
            SkyrimNativeFaceGeomBuildRequest request,
            SkyrimFaceRecordRoute route,
            ImmutableArray<SkyrimFaceRecordPluginAuthority> pluginAuthorities,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<SkyrimNativeFaceGeomExternalHeadPart> rows =
            request.NativeFaceGeomExternalHeadParts.IsDefault
                ? []
                : request.NativeFaceGeomExternalHeadParts;
        if (rows.IsEmpty)
            return ImmutableHashSet<FormReference>.Empty;
        if (rows.Length != 1)
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-headpart-count",
                "The nativeFaceGeomExternalHeadParts route admits exactly one bounded Dint row."));
            return ImmutableHashSet<FormReference>.Empty;
        }

        SkyrimNativeFaceGeomExternalHeadPart row = rows[0];
        if (row.Order != 0 ||
            !string.Equals(
                row.SourceFormIdentifier,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .DintSourceFormIdentifier,
                StringComparison.Ordinal) ||
            !string.Equals(
                row.ProviderFormKey,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .DintProviderFormKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                row.Disposition,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .RecordOnlyExternalDisposition,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-headpart-identity",
                "The external headpart row is not the exact hash-bound Dint record-only route."));
            return ImmutableHashSet<FormReference>.Empty;
        }
        if (!FormReference.TryParse(
                row.SourceFormIdentifier,
                out FormReference sourceReference) ||
            !FormReference.TryParse(
                row.ProviderFormKey,
                out FormReference providerReference) ||
            sourceReference.FormId != providerReference.FormId ||
            !string.Equals(
                sourceReference.Plugin.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                providerReference.Plugin.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-headpart-provider-key",
                "The external Dint source/provider identities do not normalize to one exact FormID and plugin."));
            return ImmutableHashSet<FormReference>.Empty;
        }

        SkyrimFaceRecordPluginAuthority[] dintAuthorities =
            pluginAuthorities.Where(item =>
                    string.Equals(
                        item.Plugin.Value,
                        SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (dintAuthorities.Length != 1 ||
            !string.Equals(
                dintAuthorities[0].ExpectedSha256.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .DintPluginSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-headpart-plugin",
                "The selected Dint provider plugin is absent or its SHA-256 does not match the exact admitted authority."));
            return ImmutableHashSet<FormReference>.Empty;
        }

        SkyrimFaceHeadPartRecordRoute? root = route.HeadParts.SingleOrDefault(
            item => item.Reference == sourceReference);
        if (root is null ||
            !root.IsSelected ||
            (root.DeclaredType != NpcHeadPartType.Hair &&
             root.EffectiveType != NpcHeadPartType.Hair) ||
            !string.Equals(
                root.Provider.Plugin.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                root.Provider.Sha256.Value,
                SkyrimNativeFaceGeomExternalHeadPartAuthority
                    .DintPluginSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-headpart-root",
                "The exact Dint HDPT root is not one selected Hair record with the admitted provider identity."));
            return ImmutableHashSet<FormReference>.Empty;
        }

        var references = ImmutableHashSet.CreateBuilder<FormReference>();
        references.Add(root.Reference);
        bool changed;
        do
        {
            changed = false;
            foreach (SkyrimFaceHeadPartRecordRoute candidate in route.HeadParts)
            {
                if (candidate.Parent is not FormReference parent ||
                    !references.Contains(parent) ||
                    !references.Add(candidate.Reference))
                    continue;
                changed = true;
            }
        }
        while (changed);

        foreach (SkyrimFaceHeadPartRecordRoute candidate in route.HeadParts.Where(
                     item => references.Contains(item.Reference)))
        {
            if (!string.Equals(
                    candidate.Provider.Plugin.Value,
                    SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    candidate.Provider.Sha256.Value,
                    SkyrimNativeFaceGeomExternalHeadPartAuthority
                        .DintPluginSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-headpart-closure",
                    $"Dint external descendant '{candidate.Reference}' changed provider authority inside the selected HDPT closure."));
            }
        }

        return references.ToImmutable();
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
                target.Add(diagnostic);
        }
    }

    private static bool ValidateExternalExclusionAttestation(
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        ExternalHeadPartDependencyDescriptor descriptor,
        WorkspacePath output,
        SseFaceGeomCarrierMaterializationArtifact materialized,
        ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> ordinaryShapes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool valid = true;
        if (!string.Equals(attestation.SchemaIdentifier,
                ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-schema",
                "FaceGeom exclusion attestation schema identifier is not the accepted contract."));
            valid = false;
        }
        if (attestation.DescriptorId != descriptor.DescriptorId)
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-descriptor-id",
                "FaceGeom exclusion attestation descriptor ID does not match the discovered descriptor."));
            valid = false;
        }
        if (attestation.OutputFaceGeomPath != PortableOutputPath(output) ||
            attestation.OutputFaceGeomSha256 != materialized.OutputSha256 ||
            attestation.OutputFaceGeomByteLength != materialized.OutputByteLength)
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-output-binding",
                "FaceGeom exclusion attestation is not bound to the exact materialized output path, hash, and length."));
            valid = false;
        }
        try
        {
            if (attestation.AttestationSha256 !=
                ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(
                    attestation))
            {
                diagnostics.Add(Error("skyrim-native-facegeom-external-attestation-hash",
                    "FaceGeom exclusion attestation hash does not match its canonical fields."));
                valid = false;
            }
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                attestation);
        }
        catch (Exception exception) when (exception is InvalidDataException or
            ArgumentException)
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-attestation-invalid",
                exception.Message));
            valid = false;
        }

        if (attestation.IncludedOrdinaryShapes.Length != ordinaryShapes.Length ||
            attestation.IncludedOrdinaryShapes.Zip(ordinaryShapes)
                .Any(pair => !ShapeEvidenceEqual(pair.First, pair.Second)) ||
            attestation.IncludedOrdinaryShapes.Any(item =>
                descriptor.Members.Any(member => member.OriginForm == item.HeadPart)))
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-ordinary-inventory",
                "FaceGeom exclusion attestation ordinary-shape evidence does not match this build."));
            valid = false;
        }

        var expectedExcluded = descriptor.Physics.Shapes
            .Select(item => item.ModelNif.Value + "\0" + item.ShapeName)
            .ToHashSet(StringComparer.Ordinal);
        var actualExcluded = attestation.ExcludedShapes
            .Select(item => item.ProviderModel.Value + "\0" + item.ShapeName)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedExcluded.SetEquals(actualExcluded))
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-shape-inventory",
                "FaceGeom exclusion attestation does not contain the exact descriptor shape absence inventory."));
            valid = false;
        }

        var actualMetadata = attestation.ExcludedMetadata
            .Select(item => item.Kind + "\0" + item.PortableValue)
            .ToHashSet(StringComparer.Ordinal);
        var expectedMetadata = ExpectedExclusionMetadata(descriptor)
            .Select(item => item.Kind + "\0" + item.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualMetadata.SetEquals(expectedMetadata))
        {
            diagnostics.Add(Error("skyrim-native-facegeom-external-metadata-inventory",
                "FaceGeom exclusion attestation absence inventory differs from checked descriptor values."));
            valid = false;
        }
        return valid;
    }

    private static bool ShapeEvidenceEqual(
        SkyrimNativeFaceGeomShapeEvidence left,
        SkyrimNativeFaceGeomShapeEvidence right) =>
        left.HeadPart == right.HeadPart &&
        left.EffectiveType == right.EffectiveType &&
        left.ModelNif == right.ModelNif &&
        left.ModelSha256 == right.ModelSha256 &&
        left.ModelShapeName == right.ModelShapeName &&
        left.OutputShapeName == right.OutputShapeName &&
        left.VertexCount == right.VertexCount &&
        left.TopologySha256 == right.TopologySha256 &&
        left.BasePositionSha256 == right.BasePositionSha256 &&
        left.FinalPositionSha256 == right.FinalPositionSha256 &&
        left.TriEvidence.SequenceEqual(right.TriEvidence) &&
        left.UsesFaceTint == right.UsesFaceTint;

    private static IEnumerable<(string Kind, string Value)> ExpectedExclusionMetadata(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        yield return ("physics-locator", "HDT Skinned Mesh Physics Object");
        foreach (ExternalHeadPartPhysicsShapeBinding shape in descriptor.Physics.Shapes)
        {
            yield return ("provider-shape", shape.ShapeName);
            yield return ("physics-xml", shape.XmlPath.Value);
            yield return ("provider-model", shape.ModelNif.Value);
            yield return ("collision-body", shape.ModelNif.Value + "/collision");
        }
        if (descriptor.Physics.MappingAuthority is
                ExternalHeadPartPhysicsMappingAuthority mapping)
            yield return ("physics-mapping", mapping.Path.Value);
        foreach (ExternalHeadPartAssetDependency asset in descriptor.Assets)
            yield return ("provider-asset", asset.Path.Value);
        foreach (ExternalHeadPartRecordDependency member in descriptor.Members)
        {
            if (member.ModelNif is AssetPath model)
                yield return ("provider-member-model", model.Value);
            foreach (SkyrimHdptTriRoute tri in member.TriRoutes)
                yield return ("provider-member-tri", tri.Path.Value);
            yield return ("provider-editor-id", member.EditorId);
        }
        foreach (ExternalHeadPartRuntimePrerequisite prerequisite in
                 descriptor.RuntimePrerequisites)
        {
            yield return ("provider-runtime",
                prerequisite.Kind + "/" + prerequisite.Requirement);
            yield return ("provider-runtime-source", prerequisite.EvidenceSource);
        }
    }

    private static AssetPath PortableOutputPath(WorkspacePath output)
    {
        string fullPath = Path.GetFullPath(output.Value);
        string[] segments = fullPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int dataIndex = Array.FindLastIndex(segments, item =>
            string.Equals(item, "Data", StringComparison.OrdinalIgnoreCase));
        string portable = dataIndex >= 0 && dataIndex < segments.Length - 1
            ? string.Join('/', segments[dataIndex..])
            : Path.GetFileName(fullPath);
        return new AssetPath(portable);
    }

    private static void RollbackMaterializedOutput(
        WorkspacePath output,
        Sha256Hash expectedHash,
        int expectedByteLength,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!File.Exists(output.Value)) return;
            FileAttributes attributes = File.GetAttributes(output.Value);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-rollback-reparse",
                    "Refused to delete a reparse-point FaceGeom output during exclusion rollback."));
                return;
            }
            byte[] bytes = File.ReadAllBytes(output.Value);
            Sha256Hash actualHash = new(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)));
            if (bytes.Length != expectedByteLength || actualHash != expectedHash)
            {
                diagnostics.Add(Error(
                    "skyrim-native-facegeom-external-rollback-mismatch",
                    "Refused to delete an output whose bytes no longer match the materialization result."));
                return;
            }
            File.Delete(output.Value);
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error(
                "skyrim-native-facegeom-external-rollback-failed",
                $"Failed to roll back the refused FaceGeom output: {exception.Message}"));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimNativeFaceGeomBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());

    private sealed record PendingPart(
        SkyrimFaceHeadPartRecordRoute Record,
        ResolvedSkyrimAssetContent Model,
        SseSelectedHeadpartNifRestShape Shape,
        string CarrierShapeName,
        SkyrimRaceMenuFaceBakeCarrierShapeBinding Binding,
        SkyrimRaceMenuFaceBakeShapeTriInputs TriInputs,
        FinalFaceGeomTexturePlan FinalTexturePlan);
}
