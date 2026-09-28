using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Closes reviewed reference-preset choices over exact copied plugin, asset,
/// NIF, TRI, DDS, RaceMenu catalog, and baseline JSlot authorities.
/// </summary>
public sealed class ReferencePresetResourceSnapshotService(
    IPresetService presetService,
    IRaceMenuPresetCompatibilityEvaluator compatibilityEvaluator,
    IBethesdaReferencePresetCatalogReader catalogReader,
    ISkyrimAssetAuthorityPlanner assetAuthorityPlanner,
    IReferencePresetAssetMaterializer assetMaterializer,
    ISkyrimRaceMenuCatalogAuthorityLoader raceMenuCatalogLoader,
    IRaceMenuSliderCatalogParserCore sliderCatalogParser,
    ISseSelectedHeadpartNifGeometryReader nifReader,
    ISseTriHeadReader triReader,
    IFaceTintTextureContentDecoder textureDecoder)
    : IReferencePresetResourceSnapshotService
{
    private const int SchemaVersion = 1;
    private static readonly (string Positive, string Negative)[]
        NativeNam9MorphNames =
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

    private static readonly string[] NativeNamaPrefixes =
        ["NoseType", "BrowType", "EyesType", "LipType"];

    public async ValueTask<ReferencePresetResourceSnapshotResult> CreateAsync(
        ReferencePresetResourceSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ReferencePresetAuthoringRules.ValidateIntake(request.Intake));
        diagnostics.AddRange(ReferencePresetAuthoringRules
            .ValidateReviewedDesignForResourceSnapshot(
            request.ReviewedDesign,
            request.ReviewedDesign.ProposalSha256));
        if (request.ReviewedDesign.Authority !=
                ReferencePresetAuthorityKind.ReviewedDesign ||
            request.ReviewedDesignSha256 == ZeroHash())
        {
            diagnostics.Add(Error("reference-resource-reviewed-authority",
                "A nonzero reviewed-design hash and reviewed authority are required."));
        }
        if (request.Intake.Target.Race != request.Intake.Race ||
            request.Intake.Target.Sex != request.Intake.Sex)
        {
            diagnostics.Add(Error("reference-resource-target-race",
                "The intake target and reviewed design do not share one target race."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        PresetParseResult baselineResult = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.Intake.BaselineJslot),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(baselineResult.Diagnostics);
        PresetDocument? baseline = baselineResult.Document;
        if (baseline is null || !baseline.IsValid ||
            baseline.SourceHash != request.Intake.BaselineJslotSha256)
        {
            diagnostics.Add(Error("reference-resource-baseline-stale",
                "The compatible baseline JSlot is missing, invalid, or no longer matches its reviewed SHA-256."));
            return Refused(diagnostics);
        }
        ReferencePresetCatalogReadResult catalogResult =
            await catalogReader.ReadAsync(
                new ReferencePresetCatalogReadRequest(
                    request.Intake.Target,
                    request.ReviewedDesign.CatalogSelection),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(catalogResult.Diagnostics);
        if (!catalogResult.Accepted || catalogResult.Authority is null)
            return Refused(diagnostics);
        ReferencePresetCatalogAuthority catalog = catalogResult.Authority;
        PresetDocument effectiveCandidate = baseline with
        {
            Appearance = baseline.Appearance with
            {
                HeadParts = catalog.HeadParts
                    .OrderBy(item => (int)item.Type)
                    .ThenBy(item => item.Reference.Plugin.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Reference.FormId.Value)
                    .Select(item => new PresetHeadPart(
                        PresetIdentifier.Parse(
                            PortableIdentifier(item.Reference)),
                        (int)item.Type))
                    .ToImmutableArray()
            }
        };
        RaceMenuPresetCompatibilityResult compatibility =
            await compatibilityEvaluator.EvaluateAsync(
                effectiveCandidate, request.Intake.Target, cancellationToken)
                .ConfigureAwait(false);
        diagnostics.AddRange(compatibility.Diagnostics);
        if (compatibility.Kind != RaceMenuPresetCompatibilityKind.Compatible)
        {
            diagnostics.Add(Error(
                "reference-resource-effective-preset-incompatible",
                "The baseline appearance with the exact reviewed head parts is not compatible with the reviewed target."));
            return Refused(diagnostics);
        }

        SkyrimRaceMenuCatalogAuthorityResult raceMenuCatalog =
            await raceMenuCatalogLoader.LoadAsync(
                new SkyrimRaceMenuCatalogAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.Intake.Target.DataRoot,
                    request.Intake.Target.PluginOrder
                        .Select(item => item.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(raceMenuCatalog.Diagnostics);
        if (!raceMenuCatalog.Accepted || raceMenuCatalog.CatalogRequest is null)
            return Refused(diagnostics);
        SkyrimRaceMenuCatalogParseResult sliderCatalogResult =
            sliderCatalogParser.Parse(raceMenuCatalog.CatalogRequest);
        diagnostics.AddRange(sliderCatalogResult.Diagnostics);
        if (!sliderCatalogResult.Accepted || sliderCatalogResult.Catalog is null)
            return Refused(diagnostics);
        SkyrimRaceMenuSliderCatalog sliderCatalog = sliderCatalogResult.Catalog;

        ImmutableHashSet<string> selectedChargenTriPaths = catalog.HeadParts
            .SelectMany(item => item.TriRoutes)
            .Where(item => item.Role == SkyrimHdptTriRole.CharGen)
            .Select(item => item.Path.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AssetPath> extendedTriPaths = sliderCatalog.MorphExtensions
            .Where(item => selectedChargenTriPaths.Contains(
                item.BaseChargenTri.Value))
            .SelectMany(item => item.ExtendedTriPaths)
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<AssetPath> primaryPaths = catalog.HeadParts
            .Select(item => item.ModelNif)
            .Concat(catalog.HeadParts.SelectMany(item =>
                item.TriRoutes.Select(route => route.Path)))
            .Concat(catalog.Tints.Select(item => item.MaskPath))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ResolvedBatch primary = await ResolveAsync(
            request.Intake.Target.DataRoot,
            primaryPaths,
            cancellationToken,
            extendedTriPaths)
            .ConfigureAwait(false);
        diagnostics.AddRange(primary.Diagnostics);
        if (!primary.Accepted) return Refused(diagnostics);
        ImmutableHashSet<string> unavailableExtensions =
            (primary.UnavailableOptionalAssets.IsDefault
                    ? []
                    : primary.UnavailableOptionalAssets)
                .Select(item => item.Value)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        if (unavailableExtensions.Count > 0)
        {
            diagnostics.Add(new Diagnostic(
                "reference-resource-optional-extension-unavailable",
                DiagnosticSeverity.Info,
                $"Skipped {unavailableExtensions.Count} unavailable optional RaceMenu morph-extension TRI path(s)."));
        }

        var geometries = new Dictionary<string,
            SseSelectedHeadpartNifGeometryDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (ReferencePresetCatalogHeadPart part in catalog.HeadParts)
        {
            ReferencePresetAssetContent source = primary.Require(part.ModelNif);
            SseSelectedHeadpartNifGeometryReadResult result = nifReader.Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    part.ModelNif,
                    source.Authority.ContentSha256,
                    source.Content));
            diagnostics.AddRange(result.Diagnostics);
            if (!result.Accepted || result.Document is null) continue;
            geometries.Add(part.ModelNif.Value, result.Document);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var triDocuments = new Dictionary<string, SseTriHeadDocument>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ReferencePresetAssetContent source in primary.Assets
                     .Where(item => item.Authority.AssetPath.Value.EndsWith(
                         ".tri", StringComparison.OrdinalIgnoreCase)))
        {
            SseTriHeadReadResult result = triReader.Read(
                new SseTriHeadReadRequest(
                    source.Authority.AssetPath,
                    source.Authority.ContentSha256,
                    source.Content));
            diagnostics.AddRange(result.Diagnostics);
            if (!result.Accepted || result.Document is null) continue;
            triDocuments[source.Authority.AssetPath.Value] = result.Document;
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<AssetPath> renderTexturePaths = geometries.Values
            .SelectMany(item => item.Shapes)
            .SelectMany(item => item.Materials)
            .SelectMany(item => item.TextureSlots)
            .Where(item => item.Slot is 0 or 1 or 2)
            .Select(item => item.Path)
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        HashSet<string> diffuseTexturePaths = geometries.Values
            .SelectMany(item => item.Shapes)
            .SelectMany(item => item.Materials)
            .SelectMany(item => item.TextureSlots)
            .Where(item => item.Slot == 0)
            .Select(item => item.Path.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ResolvedBatch renderTextures = renderTexturePaths.IsDefaultOrEmpty
            ? ResolvedBatch.Empty
            : await ResolveAsync(request.Intake.Target.DataRoot,
                    renderTexturePaths, cancellationToken)
                .ConfigureAwait(false);
        diagnostics.AddRange(renderTextures.Diagnostics);
        if (!renderTextures.Accepted) return Refused(diagnostics);

        ImmutableArray<ReferenceRenderTexture> decodedTextures =
            await DecodeTexturesAsync(
                primary.Assets.Concat(renderTextures.Assets)
                    .Where(item => item.Authority.AssetPath.Value.EndsWith(
                        ".dds", StringComparison.OrdinalIgnoreCase) &&
                        diffuseTexturePaths.Contains(
                            item.Authority.AssetPath.Value))
                    .DistinctBy(item => item.Authority.AssetPath.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray(),
                diagnostics,
                cancellationToken).ConfigureAwait(false);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        Dictionary<string, SkyrimAssetAuthority> authoritiesByPath =
            primary.Assets.Concat(renderTextures.Assets)
                .Select(item => item.Authority)
                .Concat(raceMenuCatalog.Authorities)
                .GroupBy(item => item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => RequireSameWinner(group),
                    StringComparer.OrdinalIgnoreCase);

        ImmutableArray<ReferenceRenderShapeAuthority> renderShapes =
            BuildRenderShapes(catalog, geometries, authoritiesByPath, diagnostics);
        ImmutableArray<ReferenceMorphChannelAuthority> morphChannels =
            BuildMorphChannels(
                catalog, geometries, triDocuments, sliderCatalog,
                unavailableExtensions, diagnostics);
        ImmutableArray<ReferenceFaceMorphShapeBasis> morphBases =
            BuildMorphBases(
                catalog,
                geometries,
                triDocuments,
                sliderCatalog,
                unavailableExtensions,
                request.Intake.Weight,
                diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<SkyrimAssetAuthority> authorities = authoritiesByPath.Values
            .OrderBy(item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        Sha256Hash environmentFingerprint = HashFields(
            request.Intake.Target.AuthorityId,
            request.Intake.Target.Race.ToString(),
            request.Intake.Target.Sex.ToString(),
            request.Intake.Target.DataRoot.Value,
            request.Intake.Target.PluginOrder.SelectMany(item => new[]
            {
                item.Plugin.Value, item.Path.Value, item.ExpectedSha256.Value
            }).ToArray());
        Sha256Hash resourceFingerprint = HashFields(
            request.Intake.BaselineJslotSha256.Value,
            environmentFingerprint.Value,
            authorities.SelectMany(item => new[]
            {
                item.AssetPath.Value, item.ProviderId,
                item.ProviderSha256.Value, item.ContentSha256.Value,
                item.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }).ToArray(),
            unavailableExtensions
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            renderShapes.SelectMany(item => new[]
            {
                item.NifIdentity, item.ShapeIdentity, item.NifSha256.Value,
                item.TopologySha256.Value, item.RestPositionsSha256.Value,
                Float(item.RenderPlacement.M11),
                Float(item.RenderPlacement.M12),
                Float(item.RenderPlacement.M13),
                Float(item.RenderPlacement.M21),
                Float(item.RenderPlacement.M22),
                Float(item.RenderPlacement.M23),
                Float(item.RenderPlacement.M31),
                Float(item.RenderPlacement.M32),
                Float(item.RenderPlacement.M33),
                Float(item.RenderPlacement.TranslationX),
                Float(item.RenderPlacement.TranslationY),
                Float(item.RenderPlacement.TranslationZ)
            }).ToArray(),
            morphChannels.SelectMany(item => new[]
            {
                item.Name, item.TriSha256.Value, item.NifIdentity,
                item.ShapeIdentity,
                item.NegativeTriSha256?.Value ?? string.Empty,
                item.IsDiscrete ? "discrete" : "continuous",
                item.DiscreteFamilyOrdinal?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty,
                item.DiscreteValue?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty
            }).ToArray());
        diagnostics.Add(new Diagnostic(
            "reference-resource-snapshot",
            DiagnosticSeverity.Info,
            $"Closed {catalog.HeadParts.Length} HDPT record(s), {authorities.Length} asset winner(s), {renderShapes.Length} render shape(s), and {morphChannels.Length} legal morph source(s)."));
        var snapshot = new ReferencePresetResourceSnapshot(
            SchemaVersion,
            request.Intake.ProjectId,
            request.ReviewedDesignSha256,
            request.Intake.BaselineJslotSha256,
            baseline,
            request.ReviewedDesign.CatalogSelection,
            authorities,
            morphChannels,
            renderShapes,
            environmentFingerprint,
            resourceFingerprint,
            diagnostics.ToImmutable())
        {
            CatalogAuthority = catalog,
            RaceMenuCatalog = sliderCatalog,
            MorphBases = morphBases,
            RenderTextures = decodedTextures
        };
        return new ReferencePresetResourceSnapshotResult(
            snapshot, diagnostics.ToImmutable());
    }

    private async ValueTask<ResolvedBatch> ResolveAsync(
        WorkspacePath dataRoot,
        ImmutableArray<AssetPath> paths,
        CancellationToken cancellationToken,
        ImmutableArray<AssetPath> optionalPaths = default)
    {
        SkyrimAssetAuthorityPlanResult plan = await assetAuthorityPlanner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                paths,
                optionalPaths.IsDefault ? [] : optionalPaths),
            cancellationToken).ConfigureAwait(false);
        if (!plan.Accepted || HasErrors(plan.Diagnostics))
            return new ResolvedBatch(
                false,
                [],
                plan.Diagnostics,
                plan.UnavailableOptionalAssets);
        ReferencePresetAssetMaterializeResult materialized =
            await assetMaterializer.MaterializeAsync(
                new ReferencePresetAssetMaterializeRequest(
                    dataRoot, plan.Authorities),
                cancellationToken).ConfigureAwait(false);
        return new ResolvedBatch(
            materialized.Accepted && !HasErrors(materialized.Diagnostics),
            materialized.Assets,
            plan.Diagnostics.AddRange(materialized.Diagnostics),
            plan.UnavailableOptionalAssets);
    }

    private async ValueTask<ImmutableArray<ReferenceRenderTexture>>
        DecodeTexturesAsync(
            ImmutableArray<ReferencePresetAssetContent> sources,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        var textures = ImmutableArray.CreateBuilder<ReferenceRenderTexture>(
            sources.Length);
        foreach (ReferencePresetAssetContent source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FaceTintTextureDecodeResult decoded = await textureDecoder.DecodeAsync(
                source.Content, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(decoded.Diagnostics);
            if (!decoded.Decoded || decoded.Bytes is null ||
                decoded.SourceSha256 != source.Authority.ContentSha256)
            {
                diagnostics.Add(Error("reference-resource-texture-decode",
                    $"DDS '{source.Authority.AssetPath}' did not decode against its exact source hash."));
                continue;
            }
            byte[] rgba = new byte[decoded.Bytes.Length];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                rgba[index] = decoded.Bytes[index + 2];
                rgba[index + 1] = decoded.Bytes[index + 1];
                rgba[index + 2] = decoded.Bytes[index];
                rgba[index + 3] = decoded.Bytes[index + 3];
            }
            textures.Add(new ReferenceRenderTexture(
                source.Authority,
                decoded.Width,
                decoded.Height,
                ImmutableArray.CreateRange(rgba),
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(rgba)))));
        }
        return HasErrors(diagnostics) ? [] : textures.ToImmutable();
    }

    private static ImmutableArray<ReferenceRenderShapeAuthority>
        BuildRenderShapes(
            ReferencePresetCatalogAuthority catalog,
            Dictionary<string, SseSelectedHeadpartNifGeometryDocument> geometries,
            Dictionary<string, SkyrimAssetAuthority> assets,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var shapes = ImmutableArray.CreateBuilder<ReferenceRenderShapeAuthority>();
        foreach (ReferencePresetCatalogHeadPart part in catalog.HeadParts)
        {
            if (!geometries.TryGetValue(part.ModelNif.Value,
                    out SseSelectedHeadpartNifGeometryDocument? geometry) ||
                !assets.TryGetValue(part.ModelNif.Value,
                    out SkyrimAssetAuthority? nifAuthority))
            {
                diagnostics.Add(Error("reference-resource-render-nif",
                    $"HDPT {part.Reference} has no exact materialized NIF geometry."));
                continue;
            }
            foreach (SseSelectedHeadpartNifRestShape shape in geometry.Shapes)
            {
                var materials =
                    ImmutableArray.CreateBuilder<ReferenceRenderMaterialAuthority>(
                        shape.Materials.Length);
                foreach (SseSelectedHeadpartMaterial material in shape.Materials)
                {
                    SkyrimAssetAuthority? diffuse = FindSlot(
                        material, 0, assets, required: true, diagnostics);
                    SkyrimAssetAuthority? normal = FindSlot(
                        material, 1, assets, required: false, diagnostics);
                    SkyrimAssetAuthority? specular = FindSlot(
                        material, 2, assets, required: false, diagnostics);
                    if (diffuse is not null)
                    {
                        materials.Add(new ReferenceRenderMaterialAuthority(
                            material.MaterialIdentity,
                            diffuse,
                            normal,
                            specular,
                            0xFFFF_FFFF)
                        {
                            AlphaTestEnabled =
                                material.AlphaTestEnabled,
                            AlphaTestThreshold =
                                material.AlphaTestThreshold
                        });
                    }
                }
                if (materials.Count != shape.Materials.Length) continue;
                var materialOrdinals = ImmutableArray.CreateBuilder<int>(
                    shape.TriangleIndices.Length / 3);
                for (var triangle = 0; triangle < shape.TriangleIndices.Length / 3;
                     triangle++)
                {
                    int ordinal = -1;
                    for (var materialIndex = 0;
                         materialIndex < shape.Materials.Length;
                         materialIndex++)
                    {
                        SseSelectedHeadpartMaterial candidate =
                            shape.Materials[materialIndex];
                        if (triangle >= candidate.FirstTriangleOrdinal &&
                            triangle < candidate.FirstTriangleOrdinal +
                            candidate.TriangleCount)
                        {
                            ordinal = materialIndex;
                            break;
                        }
                    }
                    if (ordinal < 0)
                    {
                        diagnostics.Add(Error(
                            "reference-resource-render-material-range",
                            $"Shape '{shape.Name}' triangle {triangle} has no material."));
                        break;
                    }
                    materialOrdinals.Add(ordinal);
                }
                if (materialOrdinals.Count != shape.TriangleIndices.Length / 3)
                    continue;
                shapes.Add(new ReferenceRenderShapeAuthority(
                    part.ModelNif.Value,
                    shape.Name,
                    nifAuthority.ContentSha256,
                    shape.TopologySha256,
                    shape.PackedPositionSha256,
                    materials.ToImmutable())
                {
                    RestPositions = shape.RestPositions,
                    TriangleIndices = shape.TriangleIndices,
                    TextureCoordinates = shape.TextureCoordinates,
                    Normals = shape.Normals,
                    TriangleMaterialOrdinals = materialOrdinals.ToImmutable(),
                    RenderPlacement = shape.RenderPlacement
                });
            }
        }
        return shapes.ToImmutable();
    }

    private static SkyrimAssetAuthority? FindSlot(
        SseSelectedHeadpartMaterial material,
        int slot,
        Dictionary<string, SkyrimAssetAuthority> assets,
        bool required,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SseSelectedHeadpartTextureSlot? binding = material.TextureSlots
            .FirstOrDefault(item => item.Slot == slot);
        if (binding is null)
        {
            if (required)
            {
                diagnostics.Add(Error("reference-resource-material-slot",
                    $"Material '{material.MaterialIdentity}' has no required texture slot {slot}."));
            }
            return null;
        }
        if (!assets.TryGetValue(binding.Path.Value,
                out SkyrimAssetAuthority? authority))
        {
            diagnostics.Add(Error("reference-resource-material-provider",
                $"Material '{material.MaterialIdentity}' texture '{binding.Path}' has no exact winning provider."));
            return null;
        }
        return authority;
    }

    private static ImmutableArray<ReferenceMorphChannelAuthority>
        BuildMorphChannels(
            ReferencePresetCatalogAuthority catalog,
            Dictionary<string, SseSelectedHeadpartNifGeometryDocument> geometries,
            Dictionary<string, SseTriHeadDocument> tris,
            SkyrimRaceMenuSliderCatalog sliderCatalog,
            ImmutableHashSet<string> unavailableExtensions,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var channels = ImmutableArray.CreateBuilder<ReferenceMorphChannelAuthority>();
        foreach (ReferencePresetCatalogHeadPart part in catalog.HeadParts)
        {
            if (!geometries.TryGetValue(part.ModelNif.Value,
                    out SseSelectedHeadpartNifGeometryDocument? geometry) ||
                geometry.Shapes.Length != 1)
            {
                diagnostics.Add(Error("reference-resource-morph-shape",
                    $"HDPT {part.Reference} must resolve to one unambiguous morph host shape."));
                continue;
            }
            SseSelectedHeadpartNifRestShape shape = geometry.Shapes[0];
            var paths = part.TriRoutes
                .Where(item => item.Role == SkyrimHdptTriRole.RaceMorph)
                .Select(item => item.Path)
                .Concat(part.TriRoutes
                    .Where(item => item.Role == SkyrimHdptTriRole.CharGen)
                    .Select(item => item.Path))
                .Concat(part.TriRoutes
                    .Where(item => item.Role == SkyrimHdptTriRole.Mesh)
                    .Select(item => item.Path))
                .ToList();
            foreach (SkyrimHdptTriRoute chargen in part.TriRoutes.Where(
                         item => item.Role == SkyrimHdptTriRole.CharGen))
            {
                SkyrimRaceMenuMorphExtension? extension =
                    sliderCatalog.MorphExtensions.FirstOrDefault(item =>
                        string.Equals(item.BaseChargenTri.Value,
                            chargen.Path.Value, StringComparison.OrdinalIgnoreCase));
                if (extension is not null)
                    paths.AddRange(extension.ExtendedTriPaths);
            }
            var morphs = new Dictionary<string, MorphSelection>(
                StringComparer.OrdinalIgnoreCase);
            foreach (AssetPath path in paths.DistinctBy(
                         item => item.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (!tris.TryGetValue(path.Value, out SseTriHeadDocument? tri))
                {
                    if (unavailableExtensions.Contains(path.Value))
                        continue;
                    diagnostics.Add(Error("reference-resource-morph-tri-missing",
                        $"Morph TRI '{path}' was not parsed."));
                    continue;
                }
                if (tri.Morphs.IsDefaultOrEmpty) continue;
                if (tri.VertexCount != shape.VertexCount)
                {
                    diagnostics.Add(Error("reference-resource-morph-topology",
                        $"Morph TRI '{path}' vertex count {tri.VertexCount} does not match shape '{shape.Name}' count {shape.VertexCount}."));
                    continue;
                }
                foreach (SseTriHeadMorph morph in tri.Morphs)
                {
                    morphs.TryAdd(
                        morph.Name,
                        new MorphSelection(morph, tri.SourceSha256));
                }
            }

            for (var index = 0;
                 index < NativeNam9MorphNames.Length;
                 index++)
            {
                (string positiveName, string negativeName) =
                    NativeNam9MorphNames[index];
                morphs.TryGetValue(
                    positiveName,
                    out MorphSelection? positive);
                morphs.TryGetValue(
                    negativeName,
                    out MorphSelection? negative);
                if (positive is null && negative is null)
                {
                    continue;
                }
                channels.Add(new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.NativePreset,
                    $"NAM9[{index}]",
                    channels.Count,
                    negative is null ? 0 : -1,
                    positive is null ? 0 : 1,
                    (positive ?? negative!).TriSha256)
                {
                    NifIdentity = part.ModelNif.Value,
                    ShapeIdentity = shape.Name,
                    Deltas = positive?.Morph.Deltas ?? [],
                    NegativeTriSha256 = negative?.TriSha256,
                    NegativeDeltas =
                        negative?.Morph.Deltas ?? []
                });
            }

            for (var family = 0;
                 family < NativeNamaPrefixes.Length;
                 family++)
            {
                string prefix = NativeNamaPrefixes[family];
                foreach ((string name, MorphSelection selection) in
                         morphs.Where(item =>
                                 item.Key.StartsWith(
                                     prefix,
                                     StringComparison.OrdinalIgnoreCase) &&
                                 int.TryParse(
                                     item.Key.AsSpan(prefix.Length),
                                     out int value) &&
                                 value >= 0)
                             .OrderBy(item =>
                                 int.Parse(
                                     item.Key.AsSpan(prefix.Length),
                                     System.Globalization.CultureInfo.InvariantCulture))
                             .ThenBy(item => item.Key,
                                 StringComparer.Ordinal))
                {
                    int value = int.Parse(
                        name.AsSpan(prefix.Length),
                        System.Globalization.CultureInfo.InvariantCulture);
                    channels.Add(new ReferenceMorphChannelAuthority(
                        ReferenceMorphChannelKind.NativePreset,
                        $"NAMA[{family}]={value}",
                        channels.Count,
                        0,
                        1,
                        selection.TriSha256)
                    {
                        NifIdentity = part.ModelNif.Value,
                        ShapeIdentity = shape.Name,
                        Deltas = selection.Morph.Deltas,
                        IsDiscrete = true,
                        DiscreteFamilyOrdinal = family,
                        DiscreteValue = value
                    });
                }
            }

            SkyrimRaceMenuSliderGender targetGender =
                catalog.Sex == NpcSex.Female
                    ? SkyrimRaceMenuSliderGender.Female
                    : SkyrimRaceMenuSliderGender.Male;
            foreach (SkyrimRaceMenuSliderDefinition slider in
                     sliderCatalog.Sliders
                         .Where(item =>
                             item.Gender == targetGender &&
                             string.Equals(
                                 item.RaceEditorId,
                                 catalog.MorphRaceEditorId,
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderBy(item => item.Name,
                             StringComparer.Ordinal)
                         .ThenBy(item => item.SourcePlugin.Value,
                             StringComparer.Ordinal)
                         .ThenBy(item => item.SourceLine))
            {
                if (slider.Type == SkyrimRaceMenuSliderType.HeadPart)
                {
                    continue;
                }
                if (slider.Type == SkyrimRaceMenuSliderType.Preset)
                {
                    for (var value = 1;
                         value <= slider.PresetCount;
                         value++)
                    {
                        string morphName =
                            slider.LowerBound +
                            value.ToString(
                                System.Globalization.CultureInfo.InvariantCulture);
                        if (!morphs.TryGetValue(
                                morphName,
                                out MorphSelection? selection))
                        {
                            diagnostics.Add(new Diagnostic(
                                "reference-resource-slider-morph-missing",
                                DiagnosticSeverity.Warning,
                                $"RaceMenu preset '{slider.Name}' value {value} has no merged TRI morph '{morphName}' and was excluded."));
                            continue;
                        }
                        channels.Add(
                            new ReferenceMorphChannelAuthority(
                                ReferenceMorphChannelKind.Custom,
                                slider.Name,
                                channels.Count,
                                0,
                                1,
                                selection.TriSha256)
                            {
                                NifIdentity = part.ModelNif.Value,
                                ShapeIdentity = shape.Name,
                                Deltas = selection.Morph.Deltas,
                                IsDiscrete = true,
                                DiscreteValue = value
                            });
                    }
                    continue;
                }

                morphs.TryGetValue(
                    slider.UpperBound,
                    out MorphSelection? upper);
                morphs.TryGetValue(
                    slider.LowerBound,
                    out MorphSelection? lower);
                if (upper is null && lower is null)
                {
                    diagnostics.Add(new Diagnostic(
                        "reference-resource-slider-morph-missing",
                        DiagnosticSeverity.Warning,
                        $"RaceMenu slider '{slider.Name}' has no merged lower or upper TRI morph and was excluded."));
                    continue;
                }
                channels.Add(new ReferenceMorphChannelAuthority(
                    ReferenceMorphChannelKind.Custom,
                    slider.Name,
                    channels.Count,
                    lower is null ? 0 : -1,
                    upper is null ? 0 : 1,
                    (upper ?? lower!).TriSha256)
                {
                    NifIdentity = part.ModelNif.Value,
                    ShapeIdentity = shape.Name,
                    Deltas = upper?.Morph.Deltas ?? [],
                    NegativeTriSha256 = lower?.TriSha256,
                    NegativeDeltas = lower?.Morph.Deltas ?? []
                });
            }
        }
        return channels.ToImmutable();
    }

    private static ImmutableArray<ReferenceFaceMorphShapeBasis>
        BuildMorphBases(
        ReferencePresetCatalogAuthority catalog,
        Dictionary<string, SseSelectedHeadpartNifGeometryDocument> geometries,
        Dictionary<string, SseTriHeadDocument> tris,
        SkyrimRaceMenuSliderCatalog sliderCatalog,
        ImmutableHashSet<string> unavailableExtensions,
        float actorWeight,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result =
            ImmutableArray.CreateBuilder<ReferenceFaceMorphShapeBasis>();
        if (string.IsNullOrWhiteSpace(catalog.MorphRaceEditorId) ||
            catalog.RaceKeywordEditorIds.IsDefault)
        {
            diagnostics.Add(Error(
                "reference-resource-morph-race-authority",
                "The exact morph-race EditorID and race-keyword array are required to build face-morph bases."));
            return [];
        }

        foreach (ReferencePresetCatalogHeadPart part in
                 catalog.HeadParts)
        {
            if (!geometries.TryGetValue(
                    part.ModelNif.Value,
                    out SseSelectedHeadpartNifGeometryDocument? geometry) ||
                geometry.Shapes.Length != 1)
            {
                continue;
            }
            SseSelectedHeadpartNifRestShape shape =
                geometry.Shapes[0];
            SkyrimFaceMorphTriSource? race =
                Source(part, SkyrimHdptTriRole.RaceMorph,
                    SkyrimFaceMorphTriRole.Race, tris, diagnostics);
            SkyrimFaceMorphTriSource? chargen =
                Source(part, SkyrimHdptTriRole.CharGen,
                    SkyrimFaceMorphTriRole.Chargen, tris, diagnostics);
            SkyrimFaceMorphTriSource? mesh =
                Source(part, SkyrimHdptTriRole.Mesh,
                    SkyrimFaceMorphTriRole.Mesh, tris, diagnostics);
            var extended =
                ImmutableArray.CreateBuilder<SkyrimFaceMorphTriSource>();
            var unavailableForShape =
                ImmutableArray.CreateBuilder<AssetPath>();
            if (chargen is not null)
            {
                SkyrimRaceMenuMorphExtension[] mappings =
                    sliderCatalog.MorphExtensions.Where(item =>
                            string.Equals(
                                item.BaseChargenTri.Value,
                                chargen.Document.SourcePath.Value,
                                StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                if (mappings.Length > 1)
                {
                    diagnostics.Add(Error(
                        "reference-resource-morph-extension-ambiguous",
                        $"CharGen TRI '{chargen.Document.SourcePath}' has multiple RaceMenu extension mappings."));
                    continue;
                }
                if (mappings.Length == 1)
                {
                    foreach (AssetPath path in
                             mappings[0].ExtendedTriPaths)
                    {
                        if (!tris.TryGetValue(
                                path.Value,
                                out SseTriHeadDocument? document))
                        {
                            if (unavailableExtensions.Contains(path.Value))
                            {
                                unavailableForShape.Add(path);
                                continue;
                            }
                            diagnostics.Add(Error(
                                "reference-resource-morph-extension-missing",
                                $"RaceMenu extension TRI '{path}' was not parsed."));
                            continue;
                        }
                        extended.Add(new SkyrimFaceMorphTriSource(
                            SkyrimFaceMorphTriRole.Extended,
                            document));
                    }
                }
            }

            var template = new SkyrimFaceMorphPlanBuildRequest(
                shape.VertexCount,
                catalog.MorphRaceEditorId,
                catalog.Sex == NpcSex.Female,
                new SkyrimFaceMorphSnapshot(
                    Enumerable.Repeat(0F, 18)
                        .ToImmutableArray(),
                    0,
                    Enumerable.Repeat(uint.MaxValue, 4)
                        .ToImmutableArray(),
                    true,
                    true),
                catalog.RaceKeywordEditorIds,
                [],
                [],
                actorWeight,
                sliderCatalog,
                race,
                chargen,
                mesh,
                extended.ToImmutable(),
                true)
            {
                ExplicitUnavailableExtendedTris =
                    unavailableForShape.ToImmutable()
            };
            result.Add(new ReferenceFaceMorphShapeBasis(
                part.ModelNif.Value,
                shape.Name,
                template));
        }
        return result.ToImmutable();
    }

    private static SkyrimFaceMorphTriSource? Source(
        ReferencePresetCatalogHeadPart part,
        SkyrimHdptTriRole routeRole,
        SkyrimFaceMorphTriRole planRole,
        Dictionary<string, SseTriHeadDocument> tris,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimHdptTriRoute? route = part.TriRoutes
            .SingleOrDefault(item => item.Role == routeRole);
        if (route is null)
        {
            return null;
        }
        if (!tris.TryGetValue(
                route.Path.Value,
                out SseTriHeadDocument? document))
        {
            diagnostics.Add(Error(
                "reference-resource-morph-tri-missing",
                $"Morph basis TRI '{route.Path}' was not parsed."));
            return null;
        }
        return new SkyrimFaceMorphTriSource(
            planRole, document);
    }

    private static SkyrimAssetAuthority RequireSameWinner(
        IGrouping<string, SkyrimAssetAuthority> group)
    {
        SkyrimAssetAuthority first = group.First();
        if (group.Any(item =>
                item.ProviderId != first.ProviderId ||
                item.ProviderKind != first.ProviderKind ||
                item.ProviderPath != first.ProviderPath ||
                item.ProviderSha256 != first.ProviderSha256 ||
                item.ContentLength != first.ContentLength ||
                item.ContentSha256 != first.ContentSha256))
            throw new InvalidDataException(
                $"Asset '{group.Key}' resolved to conflicting winners.");
        return first;
    }

    private static bool CatalogSelectionsEqual(
        ReferencePresetCatalogSelection left,
        ReferencePresetCatalogSelection right) =>
        left.ReviewAccepted == right.ReviewAccepted &&
        left.Face == right.Face &&
        left.Mouth == right.Mouth &&
        left.Eyes == right.Eyes &&
        left.Brows == right.Brows &&
        left.Hair == right.Hair &&
        left.Tints.SequenceEqual(right.Tints);

    private static string PortableIdentifier(
        FormReference reference) =>
        reference.Plugin.Value + "|" +
        reference.FormId.Value.ToString(
            "X6", CultureInfo.InvariantCulture);

    private static string Float(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static Sha256Hash HashFields(params object[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (object field in fields)
        {
            if (field is string value)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(value));
                hash.AppendData([0]);
            }
            else if (field is IEnumerable<string> values)
            {
                foreach (string item in values)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(item));
                    hash.AppendData([0]);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "Reference fingerprint fields must be strings or ordered string sequences.");
            }
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static Sha256Hash ZeroHash() => new(new string('0', 64));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferencePresetResourceSnapshotResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());

    private sealed record ResolvedBatch(
        bool Accepted,
        ImmutableArray<ReferencePresetAssetContent> Assets,
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<AssetPath> UnavailableOptionalAssets = default)
    {
        public static ResolvedBatch Empty { get; } =
            new(true, [], [], []);

        public ReferencePresetAssetContent Require(AssetPath path) =>
            Assets.Single(item => string.Equals(
                item.Authority.AssetPath.Value, path.Value,
                StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MorphSelection(
        SseTriHeadMorph Morph,
        Sha256Hash TriSha256);
}
