using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Pure product-owned seam from already-materialized RaceMenu catalog/TRI inputs to
/// deterministic carrier-shape XYZ arrays. Provider discovery and NIF mutation remain upstream.
/// </summary>
public sealed class SseRaceMenuFaceBakeService : ISkyrimRaceMenuFaceBakeService
{
    private const int MaxCarrierShapes = 256;
    private const int MaxVerticesPerShape = 1_000_000;
    private const float CustomMorphEpsilon = 0.0001F;

    private readonly ISseTriHeadReader _triReader;
    private readonly IRaceMenuSliderCatalogParserCore _catalogParser;
    private readonly ISseFaceMorphPlanBuilder _planBuilder;
    private readonly ISseFaceMorphEvaluator _evaluator;

    public SseRaceMenuFaceBakeService()
        : this(new SseTriHeadReader(), new RaceMenuSliderCatalogParserCore(),
            new SseFaceMorphPlanBuilder(), new SseFaceMorphEvaluator())
    {
    }

    public SseRaceMenuFaceBakeService(
        ISseTriHeadReader triReader,
        IRaceMenuSliderCatalogParserCore catalogParser,
        ISseFaceMorphPlanBuilder planBuilder,
        ISseFaceMorphEvaluator evaluator)
    {
        _triReader = triReader ?? throw new ArgumentNullException(nameof(triReader));
        _catalogParser = catalogParser ?? throw new ArgumentNullException(nameof(catalogParser));
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public SkyrimRaceMenuFaceBakeResult Bake(SkyrimRaceMenuFaceBakeRequest request)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request is null)
        {
            diagnostics.Add(Error("sse-face-bake-request", "The face-bake request is absent."));
            return Refused(diagnostics);
        }

        Dictionary<string, SkyrimRaceMenuFaceBakeCarrierShapeBinding> bindings =
            ValidateCarrierBindings(request.CarrierShapes, diagnostics);
        Dictionary<string, SkyrimRaceMenuFaceBakeShapeTriInputs> inputs =
            ValidateShapeInputs(request.ShapeTriInputs, diagnostics);
        ValidateOneToOneShapeClosure(bindings, inputs, diagnostics);
        ValidateGlobalArrays(request, diagnostics);
        ValidateSculptHostClosure(request.SculptParts, inputs.Values, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        SkyrimRaceMenuCatalogParseResult catalogResult = _catalogParser.Parse(request.CatalogRequest);
        AppendDiagnostics(diagnostics, catalogResult.Diagnostics);
        if (!catalogResult.Accepted || catalogResult.Catalog is null || HasErrors(diagnostics))
        {
            if (catalogResult.Accepted && catalogResult.Catalog is null)
            {
                diagnostics.Add(Error("sse-face-bake-catalog",
                    "The accepted RaceMenu catalog result omitted its catalog."));
            }

            return Refused(diagnostics);
        }

        ValidateExplicitUnavailableExtensionClosure(
            request, bindings, inputs, catalogResult.Catalog, diagnostics);

        HashSet<string> resolvedCustomNames = new(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeOutput>.Builder outputs =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeShapeOutput>(
                request.CarrierShapes.Length);

        foreach (SkyrimRaceMenuFaceBakeCarrierShapeBinding binding in request.CarrierShapes)
        {
            SkyrimRaceMenuFaceBakeShapeTriInputs shapeInputs = inputs[binding.CarrierShapeName];
            int errorCountBefore = ErrorCount(diagnostics);
            ImmutableArray<AssetPath> availableExtensions = binding.ChargenMorphHost is AssetPath
                ? ValidateAvailableExtensionSubset(
                    binding, shapeInputs.ExtendedMorphTris, catalogResult.Catalog, diagnostics)
                : ImmutableArray<AssetPath>.Empty;

            SkyrimFaceMorphTriSource? race = ReadOptional(
                shapeInputs.RaceMorphTri, SkyrimFaceMorphTriRole.Race,
                binding.CarrierShapeName, diagnostics);
            SkyrimFaceMorphTriSource? chargen = ReadOptional(
                shapeInputs.ChargenMorphTri, SkyrimFaceMorphTriRole.Chargen,
                binding.CarrierShapeName, diagnostics);
            SkyrimFaceMorphTriSource? mesh = ReadOptional(
                shapeInputs.MeshMorphTri, SkyrimFaceMorphTriRole.Mesh,
                binding.CarrierShapeName, diagnostics);
            ImmutableArray<SkyrimFaceMorphTriSource> extended = ReadExtended(
                shapeInputs.ExtendedMorphTris, binding.CarrierShapeName, diagnostics);

            ValidateNativeTriTopology(binding, race, diagnostics);
            ValidateNativeTriTopology(binding, chargen, diagnostics);
            ValidateNativeTriTopology(binding, mesh, diagnostics);
            foreach (SkyrimFaceMorphTriSource source in extended)
                ValidateNativeTriTopology(binding, source, diagnostics);

            if (ErrorCount(diagnostics) != errorCountBefore ||
                (binding.ChargenMorphHost is not null && chargen is null))
            {
                continue;
            }

            ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> triEvidence = BuildTriEvidence(
                race, chargen, mesh, extended);
            ImmutableArray<SkyrimRaceMenuMorphExtension> effectiveExtensions =
                binding.ChargenMorphHost is AssetPath chargenHost
                    ? [new SkyrimRaceMenuMorphExtension(chargenHost, availableExtensions)]
                    : ImmutableArray<SkyrimRaceMenuMorphExtension>.Empty;
            SkyrimRaceMenuSliderCatalog effectiveCatalog = new(
                catalogResult.Catalog.Sliders,
                effectiveExtensions);
            SkyrimFaceMorphPlanBuildRequest planRequest = new(
                binding.VertexCount,
                request.MorphRaceEditorId,
                request.IsFemale,
                request.NativeMorphs,
                request.RaceKeywordEditorIds,
                request.CustomMorphs,
                request.SculptParts,
                request.ActorWeight,
                effectiveCatalog,
                race,
                chargen,
                mesh,
                extended,
                RequireAllCustomMorphs: false);
            SkyrimFaceMorphPlanBuildResult planResult = _planBuilder.Build(planRequest);
            AppendDiagnostics(diagnostics, planResult.Diagnostics, binding.CarrierShapeName);
            if (!planResult.Accepted || planResult.Plan is null)
            {
                if (planResult.Accepted && planResult.Plan is null)
                {
                    diagnostics.Add(ShapeError("sse-face-bake-plan", binding.CarrierShapeName,
                        "The accepted plan result omitted its plan."));
                }

                continue;
            }

            foreach (string customName in planResult.Plan.ResolvedCustomMorphNames.IsDefault
                         ? ImmutableArray<string>.Empty
                         : planResult.Plan.ResolvedCustomMorphNames)
            {
                resolvedCustomNames.Add(customName);
            }

            SkyrimFaceMorphEvaluationResult evaluation = _evaluator.Evaluate(
                new SkyrimFaceMorphEvaluationRequest(binding.BasePositions, planResult.Plan));
            AppendDiagnostics(diagnostics, evaluation.Diagnostics, binding.CarrierShapeName);
            if (!evaluation.Accepted)
            {
                continue;
            }

            if (evaluation.Positions.Length != binding.VertexCount ||
                evaluation.Positions.Any(position => !IsFinite(position)))
            {
                diagnostics.Add(ShapeError("sse-face-bake-output", binding.CarrierShapeName,
                    "The evaluator returned topology-drifted or non-finite XYZ output."));
                continue;
            }

            if (!ValidateSentinelTriangles(
                    binding,
                    evaluation.Positions,
                    diagnostics,
                    "final"))
            {
                continue;
            }

            outputs.Add(new SkyrimRaceMenuFaceBakeShapeOutput(
                binding.CarrierShapeName,
                binding.ChargenMorphHost,
                binding.VertexCount,
                binding.ExpectedTopologySha256,
                binding.ExpectedBasePositionSha256,
                HashPositions(evaluation.Positions),
                evaluation.Positions,
                planResult.Plan.MergedTriOrder,
                triEvidence)
            {
                PackedNormalSentinels = binding.PackedNormalSentinels
            });
        }

        ValidateResolvedCustomMorphs(request, resolvedCustomNames, diagnostics);
        if (HasErrors(diagnostics) || outputs.Count != request.CarrierShapes.Length)
        {
            if (!HasErrors(diagnostics))
            {
                diagnostics.Add(Error("sse-face-bake-shape-output",
                    $"Only {outputs.Count} of {request.CarrierShapes.Length} shapes produced output."));
            }

            return Refused(diagnostics);
        }

        return new SkyrimRaceMenuFaceBakeResult(true, outputs.MoveToImmutable(),
            diagnostics.ToImmutable());
    }

    private static Dictionary<string, SkyrimRaceMenuFaceBakeCarrierShapeBinding>
        ValidateCarrierBindings(
            ImmutableArray<SkyrimRaceMenuFaceBakeCarrierShapeBinding> source,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, SkyrimRaceMenuFaceBakeCarrierShapeBinding> result =
            new(StringComparer.OrdinalIgnoreCase);
        if (source.IsDefault || source.IsEmpty || source.Length > MaxCarrierShapes)
        {
            diagnostics.Add(Error("sse-face-bake-carrier-shapes",
                $"CarrierShapes must explicitly contain 1 to {MaxCarrierShapes} bindings."));
            return result;
        }

        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimRaceMenuFaceBakeCarrierShapeBinding? binding in source)
        {
            if (binding is null || !IsSafeShapeName(binding.CarrierShapeName))
            {
                diagnostics.Add(Error("sse-face-bake-shape-name",
                    "Every carrier binding requires a safe non-empty shape name."));
                continue;
            }

            if (!result.TryAdd(binding.CarrierShapeName, binding))
            {
                diagnostics.Add(ShapeError("sse-face-bake-shape-duplicate",
                    binding.CarrierShapeName, "The carrier shape occurs more than once."));
                continue;
            }

            if (binding.ChargenMorphHost is AssetPath chargenHost)
            {
                string hostPath = chargenHost.Value ?? string.Empty;
                string host = NormalizeTriIdentity(hostPath);
                if (host.Length == 0 || !hostPath.EndsWith(
                        ".tri", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(ShapeError("sse-face-bake-host", binding.CarrierShapeName,
                        "A present chargen morph host must be an explicit TRI asset path."));
                }
                else if (!hosts.Add(host))
                {
                    diagnostics.Add(ShapeError("sse-face-bake-host-duplicate",
                        binding.CarrierShapeName,
                        "More than one carrier shape binds the same chargen morph host."));
                }
            }

            if (binding.VertexCount <= 0 || binding.VertexCount > MaxVerticesPerShape ||
                binding.BasePositions.IsDefault ||
                binding.BasePositions.Length != binding.VertexCount)
            {
                diagnostics.Add(ShapeError("sse-face-bake-carrier-topology",
                    binding.CarrierShapeName,
                    "Carrier vertex count and explicit base-position length do not agree."));
                continue;
            }

            if (binding.BasePositions.Any(position => !IsFinite(position)))
            {
                diagnostics.Add(ShapeError("sse-face-bake-carrier-nonfinite",
                    binding.CarrierShapeName, "Carrier base positions contain non-finite XYZ."));
                continue;
            }

            if (!IsBoundHash(binding.ExpectedTopologySha256) ||
                !IsBoundHash(binding.ExpectedBasePositionSha256))
            {
                diagnostics.Add(ShapeError("sse-face-bake-carrier-hash",
                    binding.CarrierShapeName,
                    "Carrier topology and base-position SHA-256 identities must be explicit."));
                continue;
            }

            Sha256Hash actualPositionHash = HashPositions(binding.BasePositions);
            if (actualPositionHash != binding.ExpectedBasePositionSha256)
            {
                diagnostics.Add(ShapeError("sse-face-bake-carrier-position-hash",
                    binding.CarrierShapeName,
                    $"Carrier base-position hash {actualPositionHash} does not match {binding.ExpectedBasePositionSha256}."));
            }

            ValidateSentinelTriangles(
                binding,
                binding.BasePositions,
                diagnostics,
                "rest");
        }

        return result;
    }

    private static bool ValidateSentinelTriangles(
        SkyrimRaceMenuFaceBakeCarrierShapeBinding binding,
        ImmutableArray<Vector3> positions,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string phase)
    {
        if (binding.PackedNormalSentinels.IsDefaultOrEmpty)
            return true;

        bool accepted = true;
        var seen = new HashSet<(int Partition, int Vertex)>();
        foreach (SseSelectedHeadpartPackedNormalSentinel sentinel in
                 binding.PackedNormalSentinels)
        {
            if (sentinel.SourceBlockIndex < 0 ||
                sentinel.PartitionIndex < 0 ||
                sentinel.VertexIndex < 0 ||
                sentinel.VertexIndex >= positions.Length ||
                sentinel.RawOffset < 0 ||
                sentinel.TopologySha256 != binding.ExpectedTopologySha256 ||
                sentinel.RawBytes.Length != 4 ||
                !sentinel.RawBytes.SequenceEqual(
                    new byte[] { 0x80, 0x80, 0x80, 0x80 }) ||
                sentinel.IncidentTriangleIndices.IsDefault ||
                sentinel.IncidentTriangleIndices.Length == 0 ||
                sentinel.IncidentTriangleIndices.Length % 3 != 0 ||
                !seen.Add((sentinel.PartitionIndex, sentinel.VertexIndex)))
            {
                diagnostics.Add(ShapeError(
                    "sse-face-bake-sentinel-evidence",
                    binding.CarrierShapeName,
                    $"The {phase} CVEO sentinel evidence is incomplete, inconsistent, or duplicated."));
                accepted = false;
                continue;
            }

            ImmutableArray<int> triangles = sentinel.IncidentTriangleIndices;
            for (var index = 0; index < triangles.Length; index += 3)
            {
                int first = triangles[index];
                int second = triangles[index + 1];
                int third = triangles[index + 2];
                if (first < 0 || first >= positions.Length ||
                    second < 0 || second >= positions.Length ||
                    third < 0 || third >= positions.Length ||
                    (first != sentinel.VertexIndex &&
                     second != sentinel.VertexIndex &&
                     third != sentinel.VertexIndex) ||
                    !SseSelectedHeadpartPackedNormalPolicy.IsExactDegenerate(
                        positions[first], positions[second], positions[third]))
                {
                    diagnostics.Add(ShapeError(
                        "sse-face-bake-sentinel-open",
                        binding.CarrierShapeName,
                        $"The {phase} CVEO sentinel vertex {sentinel.VertexIndex} opens an incident triangle under policy '{SseSelectedHeadpartPackedNormalPolicy.Version}'."));
                    accepted = false;
                }
            }
        }
        return accepted;
    }

    private static Dictionary<string, SkyrimRaceMenuFaceBakeShapeTriInputs> ValidateShapeInputs(
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeTriInputs> source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, SkyrimRaceMenuFaceBakeShapeTriInputs> result =
            new(StringComparer.OrdinalIgnoreCase);
        if (source.IsDefault || source.Length > MaxCarrierShapes)
        {
            diagnostics.Add(Error("sse-face-bake-shape-inputs",
                $"ShapeTriInputs must be an explicit array with at most {MaxCarrierShapes} rows."));
            return result;
        }

        foreach (SkyrimRaceMenuFaceBakeShapeTriInputs? input in source)
        {
            if (input is null || !IsSafeShapeName(input.CarrierShapeName))
            {
                diagnostics.Add(Error("sse-face-bake-shape-name",
                    "Every TRI input row requires a safe non-empty carrier shape name."));
                continue;
            }

            if (!result.TryAdd(input.CarrierShapeName, input))
            {
                diagnostics.Add(ShapeError("sse-face-bake-shape-duplicate",
                    input.CarrierShapeName, "The TRI input shape occurs more than once."));
                continue;
            }

            if (input.ExtendedMorphTris.IsDefault)
            {
                diagnostics.Add(ShapeError("sse-face-bake-extension-order",
                    input.CarrierShapeName,
                    "ExtendedMorphTris must be explicit, even when none are available."));
                continue;
            }

            ValidateTriInputRequest(input.RaceMorphTri, "race", input.CarrierShapeName,
                required: false, diagnostics);
            ValidateTriInputRequest(input.ChargenMorphTri, "chargen", input.CarrierShapeName,
                required: false, diagnostics);
            ValidateTriInputRequest(input.MeshMorphTri, "mesh", input.CarrierShapeName,
                required: false, diagnostics);
            foreach (SseTriHeadReadRequest? extended in input.ExtendedMorphTris)
            {
                ValidateTriInputRequest(extended, "extended", input.CarrierShapeName,
                    required: true, diagnostics);
            }

            ValidateDistinctSourcePaths(input, diagnostics);
        }

        return result;
    }

    private static void ValidateDistinctSourcePaths(SkyrimRaceMenuFaceBakeShapeTriInputs input,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> sourcePaths = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<SseTriHeadReadRequest?> all = new SseTriHeadReadRequest?[]
        {
            input.RaceMorphTri,
            input.ChargenMorphTri,
            input.MeshMorphTri
        }.Concat(input.ExtendedMorphTris.Cast<SseTriHeadReadRequest?>());
        foreach (SseTriHeadReadRequest? tri in all)
        {
            if (tri is null)
            {
                continue;
            }

            if (!sourcePaths.Add(tri.SourcePath.Value ?? string.Empty))
            {
                diagnostics.Add(ShapeError("sse-face-bake-tri-duplicate",
                    input.CarrierShapeName,
                    $"TRI source '{tri.SourcePath}' is assigned more than once."));
            }
        }
    }

    private static void ValidateTriInputRequest(SseTriHeadReadRequest? tri, string role,
        string shapeName, bool required, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (tri is null)
        {
            if (required)
            {
                diagnostics.Add(ShapeError("sse-face-bake-tri-input", shapeName,
                    $"The required {role} TRI input is absent."));
            }

            return;
        }

        string path = tri.SourcePath.Value ?? string.Empty;
        if (path.Length == 0 || !path.EndsWith(".tri", StringComparison.OrdinalIgnoreCase) ||
            !IsBoundHash(tri.ExpectedSourceSha256))
        {
            diagnostics.Add(ShapeError("sse-face-bake-tri-input", shapeName,
                $"The {role} TRI requires a safe path and explicit SHA-256 identity."));
        }
    }

    private static void ValidateOneToOneShapeClosure(
        IReadOnlyDictionary<string, SkyrimRaceMenuFaceBakeCarrierShapeBinding> bindings,
        IReadOnlyDictionary<string, SkyrimRaceMenuFaceBakeShapeTriInputs> inputs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (string name in bindings.Keys)
        {
            if (!inputs.ContainsKey(name))
            {
                diagnostics.Add(ShapeError("sse-face-bake-shape-missing", name,
                    "The carrier shape has no TRI input row."));
            }
        }

        foreach (string name in inputs.Keys)
        {
            if (!bindings.ContainsKey(name))
            {
                diagnostics.Add(ShapeError("sse-face-bake-shape-unexpected", name,
                    "The TRI input row has no carrier binding."));
            }
        }

        foreach ((string name, SkyrimRaceMenuFaceBakeCarrierShapeBinding binding) in bindings)
        {
            if (!inputs.TryGetValue(name, out SkyrimRaceMenuFaceBakeShapeTriInputs? input))
            {
                continue;
            }

            if (binding.ChargenMorphHost is AssetPath host)
            {
                if (input.ChargenMorphTri is null)
                {
                    diagnostics.Add(ShapeError("sse-face-bake-chargen-missing", name,
                        "The explicit chargen morph host has no chargen TRI input."));
                }
                else if (!NormalizeTriIdentity(input.ChargenMorphTri.SourcePath.Value).Equals(
                             NormalizeTriIdentity(host.Value),
                             StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(ShapeError("sse-face-bake-host-mismatch", name,
                        $"Chargen TRI '{input.ChargenMorphTri.SourcePath}' does not match carrier host '{host}'."));
                }

                continue;
            }

            if (input.ChargenMorphTri is not null)
            {
                diagnostics.Add(ShapeError("sse-face-bake-host-unexpected", name,
                    "A chargen TRI was supplied without an explicit chargen morph host."));
            }

            if (input.RaceMorphTri is not null || !input.ExtendedMorphTris.IsDefaultOrEmpty)
            {
                diagnostics.Add(ShapeError("sse-face-bake-hostless-source", name,
                    "Race and extended TRIs require an explicit chargen morph host; hostless shapes admit only their record-declared mesh TRI."));
            }
        }
    }

    private static void ValidateGlobalArrays(SkyrimRaceMenuFaceBakeRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CatalogRequest is null)
        {
            diagnostics.Add(Error("sse-face-bake-catalog", "The catalog request is absent."));
        }
        else
        {
            if (request.CatalogRequest.LoadedPlugins.IsDefault ||
                request.CatalogRequest.Assets.IsDefault)
            {
                diagnostics.Add(Error("sse-face-bake-catalog",
                    "Catalog plugin and winner-asset arrays must be explicit."));
            }
            else
            {
                foreach (PluginName plugin in request.CatalogRequest.LoadedPlugins)
                {
                    if (string.IsNullOrWhiteSpace(plugin.Value))
                    {
                        diagnostics.Add(Error("sse-face-bake-catalog-plugin",
                            "Catalog plugin identities must not contain default values."));
                    }
                }

                foreach (SkyrimRaceMenuCatalogAsset? asset in request.CatalogRequest.Assets)
                {
                    if (asset is null || string.IsNullOrWhiteSpace(asset.Path.Value) ||
                        !IsBoundHash(asset.ExpectedSha256))
                    {
                        diagnostics.Add(Error("sse-face-bake-catalog-asset",
                            "Every catalog asset requires a safe path and explicit SHA-256 identity."));
                    }
                }
            }
        }

        if (request.NativeMorphs is null)
        {
            diagnostics.Add(Error("sse-face-bake-native", "The native morph snapshot is absent."));
        }
        else if ((request.NativeMorphs.HasNam9 && request.NativeMorphs.Nam9Sliders.IsDefault) ||
                 (request.NativeMorphs.HasNama && request.NativeMorphs.NamaValues.IsDefault))
        {
            diagnostics.Add(Error("sse-face-bake-native",
                "Present NAM9 and NAMA snapshots require explicit value arrays."));
        }

        if (request.RaceKeywordEditorIds.IsDefault || request.CustomMorphs.IsDefault ||
            request.SculptParts.IsDefault || request.ExplicitUnavailableExtendedTris.IsDefault)
        {
            diagnostics.Add(Error("sse-face-bake-global-arrays",
                "Keyword, custom-morph, sculpt, and unavailable-extension arrays must be explicit."));
            return;
        }

        foreach (SkyrimRaceMenuCustomMorphValue? custom in request.CustomMorphs)
        {
            if (custom is null)
            {
                diagnostics.Add(Error("sse-face-bake-custom",
                    "Custom morph rows must not contain null values."));
            }
        }

        foreach (RaceMenuSculptPart? sculpt in request.SculptParts)
        {
            if (sculpt is null)
            {
                diagnostics.Add(Error("sse-face-bake-sculpt",
                    "Sculpt rows must not contain null values."));
            }
        }
    }

    private static void ValidateExplicitUnavailableExtensionClosure(
        SkyrimRaceMenuFaceBakeRequest request,
        IReadOnlyDictionary<string, SkyrimRaceMenuFaceBakeCarrierShapeBinding> bindings,
        Dictionary<string, SkyrimRaceMenuFaceBakeShapeTriInputs> inputs,
        SkyrimRaceMenuSliderCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var declaredMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimRaceMenuFaceBakeCarrierShapeBinding binding in bindings.Values)
        {
            if (binding.ChargenMorphHost is not AssetPath host ||
                !inputs.TryGetValue(binding.CarrierShapeName,
                    out SkyrimRaceMenuFaceBakeShapeTriInputs? shapeInputs))
            {
                continue;
            }

            HashSet<string> provided = shapeInputs.ExtendedMorphTris
                .Where(item => item is not null)
                .Select(item => item.SourcePath.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (AssetPath declared in ResolveDeclaredExtensions(
                         host, catalog, binding.CarrierShapeName, diagnostics))
            {
                if (!provided.Contains(declared.Value))
                {
                    declaredMissing.Add(declared.Value);
                }
            }
        }

        var explicitlyUnavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AssetPath path in request.ExplicitUnavailableExtendedTris)
        {
            if (string.IsNullOrWhiteSpace(path.Value) ||
                !explicitlyUnavailable.Add(path.Value))
            {
                diagnostics.Add(Error("sse-face-bake-unavailable-extension-invalid",
                    "Explicit unavailable extension paths must be non-default and unique."));
            }
        }

        if (!declaredMissing.SetEquals(explicitlyUnavailable))
        {
            string missingAuthority = string.Join(", ", declaredMissing
                .Where(item => !explicitlyUnavailable.Contains(item))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            string noLongerMissing = string.Join(", ", explicitlyUnavailable
                .Where(item => !declaredMissing.Contains(item))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            diagnostics.Add(Error("sse-face-bake-unavailable-extension-closure",
                $"Catalog-declared missing extensions and the explicit unavailable authority differ. " +
                $"Undeclared missing: [{missingAuthority}]. Declared but available/unused: [{noLongerMissing}]."));
        }
    }

    private static void ValidateSculptHostClosure(
        ImmutableArray<RaceMenuSculptPart> sculptParts,
        IEnumerable<SkyrimRaceMenuFaceBakeShapeTriInputs> shapeInputs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sculptParts.IsDefault)
        {
            return;
        }

        (string Shape, HashSet<string> Hosts)[] declaredHosts = shapeInputs
            .Select(input => (
                input.CarrierShapeName,
                EnumerateTriInputs(input)
                    .Select(tri => NormalizeTriIdentity(tri.SourcePath.Value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        foreach (RaceMenuSculptPart? sculpt in sculptParts)
        {
            if (sculpt is null || sculpt.Vertices.IsDefaultOrEmpty)
            {
                continue;
            }

            string wanted = NormalizeTriIdentity(sculpt.Host);
            int count = declaredHosts.Count(shape => shape.Hosts.Contains(wanted));
            if (count == 0)
            {
                diagnostics.Add(Error("sse-face-bake-sculpt-unresolved",
                    $"Nonempty sculpt host '{sculpt.Host}' matches no carrier shape TRI input."));
            }
        }
    }

    private static IEnumerable<SseTriHeadReadRequest> EnumerateTriInputs(
        SkyrimRaceMenuFaceBakeShapeTriInputs input)
    {
        if (input.RaceMorphTri is not null) yield return input.RaceMorphTri;
        if (input.ChargenMorphTri is not null) yield return input.ChargenMorphTri;
        if (input.MeshMorphTri is not null) yield return input.MeshMorphTri;
        if (!input.ExtendedMorphTris.IsDefault)
        {
            foreach (SseTriHeadReadRequest tri in input.ExtendedMorphTris)
            {
                if (tri is not null) yield return tri;
            }
        }
    }

    private static ImmutableArray<AssetPath> ValidateAvailableExtensionSubset(
        SkyrimRaceMenuFaceBakeCarrierShapeBinding binding,
        ImmutableArray<SseTriHeadReadRequest> provided,
        SkyrimRaceMenuSliderCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (binding.ChargenMorphHost is not AssetPath host)
        {
            if (!provided.IsDefaultOrEmpty)
            {
                diagnostics.Add(ShapeError("sse-face-bake-hostless-source",
                    binding.CarrierShapeName,
                    "Extended TRIs cannot be supplied without an explicit chargen morph host."));
            }

            return ImmutableArray<AssetPath>.Empty;
        }

        ImmutableArray<AssetPath> declared = ResolveDeclaredExtensions(
            host, catalog, binding.CarrierShapeName, diagnostics);
        if (provided.IsDefault)
        {
            return ImmutableArray<AssetPath>.Empty;
        }

        ImmutableArray<AssetPath>.Builder available =
            ImmutableArray.CreateBuilder<AssetPath>(provided.Length);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int lastDeclaredIndex = -1;
        foreach (SseTriHeadReadRequest? tri in provided)
        {
            if (tri is null)
            {
                diagnostics.Add(ShapeError("sse-face-bake-extension-null",
                    binding.CarrierShapeName, "An available extended TRI entry is absent."));
                continue;
            }

            if (!seen.Add(tri.SourcePath.Value))
            {
                diagnostics.Add(ShapeError("sse-face-bake-extension-duplicate",
                    binding.CarrierShapeName,
                    $"Available extended TRI '{tri.SourcePath}' occurs more than once."));
                continue;
            }

            int declaredIndex = IndexOfPath(declared, tri.SourcePath);
            if (declaredIndex < 0)
            {
                diagnostics.Add(ShapeError("sse-face-bake-extension-unexpected",
                    binding.CarrierShapeName,
                    $"Available extended TRI '{tri.SourcePath}' is not declared for '{host}'."));
                continue;
            }

            if (declaredIndex <= lastDeclaredIndex)
            {
                diagnostics.Add(ShapeError("sse-face-bake-extension-order",
                    binding.CarrierShapeName,
                    $"Available extended TRI '{tri.SourcePath}' is out of catalog order."));
                continue;
            }

            lastDeclaredIndex = declaredIndex;
            available.Add(tri.SourcePath);
        }

        if (declared.Length > available.Count)
        {
            diagnostics.Add(new Diagnostic("sse-face-bake-extension-unavailable",
                DiagnosticSeverity.Warning,
                $"Shape '{binding.CarrierShapeName}': {declared.Length - available.Count} of {declared.Length} declared extended TRIs were not materialized; the ordered available subset will be applied."));
        }

        return available.ToImmutable();
    }

    private static ImmutableArray<AssetPath> ResolveDeclaredExtensions(
        AssetPath host,
        SkyrimRaceMenuSliderCatalog catalog,
        string shapeName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string normalizedHost = NormalizeTriIdentity(host.Value);
        SkyrimRaceMenuMorphExtension[] exact = catalog.MorphExtensions
            .Where(entry => NormalizeTriIdentity(entry.BaseChargenTri.Value).Equals(
                normalizedHost, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length > 1)
        {
            diagnostics.Add(ShapeError("sse-face-bake-extension-ambiguous", shapeName,
                $"Catalog has duplicate full-path mappings for '{host}'."));
            return ImmutableArray<AssetPath>.Empty;
        }

        if (exact.Length == 1)
        {
            return exact[0].ExtendedTriPaths;
        }

        string bareName = GetFileName(normalizedHost);
        SkyrimRaceMenuMorphExtension[] bare = catalog.MorphExtensions
            .Where(entry => IsBareCatalogKey(entry.BaseChargenTri) &&
                            GetFileName(NormalizeTriIdentity(entry.BaseChargenTri.Value)).Equals(
                                bareName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (bare.Length > 1)
        {
            diagnostics.Add(ShapeError("sse-face-bake-extension-ambiguous", shapeName,
                $"Catalog has multiple bare-name mappings for '{bareName}'."));
            return ImmutableArray<AssetPath>.Empty;
        }

        return bare.Length == 1
            ? bare[0].ExtendedTriPaths
            : ImmutableArray<AssetPath>.Empty;
    }

    private static void ValidateNativeTriTopology(
        SkyrimRaceMenuFaceBakeCarrierShapeBinding binding,
        SkyrimFaceMorphTriSource? source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (source is null || source.Document.VertexCount == binding.VertexCount)
            return;

        string role = source.Role == SkyrimFaceMorphTriRole.Mesh
            ? "Mesh (dialogue)" : source.Role.ToString();
        bool native = binding.NativeHeadPart is { } && binding.NativeModelNif is { };
        string owner = binding.NativeHeadPart is { } headPart && binding.NativeModelNif is { } model
            ? $"Headpart '{headPart}' model '{model}'"
            : $"Dynamic carrier '{binding.CarrierShapeName}'";
        diagnostics.Add(ShapeError(native
                ? "sse-face-bake-tri-topology-mismatch"
                : "sse-face-plan-source-topology",
            binding.CarrierShapeName,
            $"{owner} has {binding.VertexCount} vertices; " +
            $"resolved {role} TRI '{source.Document.SourcePath}' has {source.Document.VertexCount}."));
    }

    private SkyrimFaceMorphTriSource? ReadOptional(
        SseTriHeadReadRequest? request,
        SkyrimFaceMorphTriRole role,
        string shapeName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request is null)
        {
            return null;
        }

        SseTriHeadReadResult result = _triReader.Read(request);
        AppendDiagnostics(diagnostics, result.Diagnostics, shapeName);
        if (!result.Accepted || result.Document is null)
        {
            if (result.Accepted && result.Document is null)
            {
                diagnostics.Add(ShapeError("sse-face-bake-tri-document", shapeName,
                    $"Accepted TRI '{request.SourcePath}' omitted its document."));
            }

            return null;
        }

        return new SkyrimFaceMorphTriSource(role, result.Document);
    }

    private ImmutableArray<SkyrimFaceMorphTriSource> ReadExtended(
        ImmutableArray<SseTriHeadReadRequest> requests,
        string shapeName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (requests.IsDefault)
        {
            return ImmutableArray<SkyrimFaceMorphTriSource>.Empty;
        }

        ImmutableArray<SkyrimFaceMorphTriSource>.Builder result =
            ImmutableArray.CreateBuilder<SkyrimFaceMorphTriSource>(requests.Length);
        foreach (SseTriHeadReadRequest? request in requests)
        {
            SkyrimFaceMorphTriSource? source = ReadOptional(request,
                SkyrimFaceMorphTriRole.Extended, shapeName, diagnostics);
            if (source is not null)
            {
                result.Add(source);
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> BuildTriEvidence(
        SkyrimFaceMorphTriSource? race,
        SkyrimFaceMorphTriSource? chargen,
        SkyrimFaceMorphTriSource? mesh,
        ImmutableArray<SkyrimFaceMorphTriSource> extended)
    {
        ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence>.Builder evidence =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeTriEvidence>();
        AddTriEvidence(race, evidence);
        AddTriEvidence(chargen, evidence);
        AddTriEvidence(mesh, evidence);
        foreach (SkyrimFaceMorphTriSource source in extended)
        {
            AddTriEvidence(source, evidence);
        }

        return evidence.ToImmutable();
    }

    private static void AddTriEvidence(
        SkyrimFaceMorphTriSource? source,
        ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence>.Builder evidence)
    {
        if (source is null)
        {
            return;
        }

        SseTriHeadDocument document = source.Document;
        SkyrimRaceMenuFaceBakeTriDisposition disposition = document.Morphs.IsDefaultOrEmpty
            ? SkyrimRaceMenuFaceBakeTriDisposition.EmptyMorphNoOp
            : SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource;
        evidence.Add(new SkyrimRaceMenuFaceBakeTriEvidence(
            source.Role,
            document.SourcePath,
            document.SourceSha256,
            document.VertexCount,
            document.Morphs.IsDefault ? 0 : document.Morphs.Length,
            disposition));
    }

    private static void ValidateResolvedCustomMorphs(
        SkyrimRaceMenuFaceBakeRequest request,
        HashSet<string> resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CustomMorphs.IsDefault)
        {
            return;
        }

        HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimRaceMenuCustomMorphValue? custom in request.CustomMorphs)
        {
            if (custom is null || !float.IsFinite(custom.Value) ||
                MathF.Abs(custom.Value) < CustomMorphEpsilon ||
                resolved.Contains(custom.Name) || !reported.Add(custom.Name))
            {
                continue;
            }

            diagnostics.Add(Error("sse-face-bake-custom-unresolved",
                $"Nonzero RaceMenu custom morph '{custom.Name}' has no resolved catalog/TRI binding for race '{request.MorphRaceEditorId}' and sex '{(request.IsFemale ? "female" : "male")}'. Inspected catalog files: {string.Join(", ", request.CatalogRequest.Assets.Select(asset => asset.Path.Value))}. Supply the declared provider's races.ini/morphs.ini, slider catalog and matching TRI tree; an unresolved provider path is not permission to invent one."));
        }
    }

    private static int IndexOfPath(ImmutableArray<AssetPath> paths, AssetPath wanted)
    {
        for (int index = 0; index < paths.Length; index++)
        {
            if (paths[index].Value.Equals(wanted.Value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static Sha256Hash HashPositions(ImmutableArray<Vector3> positions)
    {
        byte[] packed = new byte[checked(positions.Length * 3 * sizeof(float))];
        int offset = 0;
        foreach (Vector3 position in positions)
        {
            WriteSingle(packed, ref offset, position.X);
            WriteSingle(packed, ref offset, position.Y);
            WriteSingle(packed, ref offset, position.Z);
        }

        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(packed)));
    }

    private static void WriteSingle(Span<byte> target, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[offset..],
            BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
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

    private static string NormalizeTriIdentity(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
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

    private static bool IsSafeShapeName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        !value.Any(char.IsControl);

    private static bool IsBoundHash(Sha256Hash hash) =>
        !string.IsNullOrEmpty(hash.Value);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static int ErrorCount(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void AppendDiagnostics(ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source, string? shapeName = null)
    {
        foreach (Diagnostic diagnostic in source)
        {
            target.Add(shapeName is null
                ? diagnostic
                : diagnostic with { Message = $"Shape '{shapeName}': {diagnostic.Message}" });
        }
    }

    private static SkyrimRaceMenuFaceBakeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, ImmutableArray<SkyrimRaceMenuFaceBakeShapeOutput>.Empty,
            diagnostics.ToImmutable());

    private static Diagnostic ShapeError(string code, string shapeName, string message) =>
        Error(code, $"Shape '{shapeName}': {message}");

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
