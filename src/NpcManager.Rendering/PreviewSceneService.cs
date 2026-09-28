using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>Writes deterministic scene-input evidence without claiming image rendering.</summary>
public sealed partial class PreviewSceneService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IPreviewImageRenderer? imageRenderer = null) : IPreviewSceneService
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxAssets = 512;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<PreviewSceneResult> RenderAsync(PreviewSceneRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        diagnostics.AddRange(ValidateImageRequest(request));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ManifestPath));
        if (!string.Equals(Path.GetExtension(request.ManifestPath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-manifest-extension", DiagnosticSeverity.Error,
                "Preview scene manifests must use the .json extension."));
        if (!File.Exists(request.ManifestPath.Value))
            diagnostics.Add(new Diagnostic("preview-manifest-missing", DiagnosticSeverity.Error,
                "The preview scene manifest does not exist."));
        else if (File.GetAttributes(request.ManifestPath.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("preview-manifest-reparse-refused", DiagnosticSeverity.Error,
                "Preview scene manifest files may not be reparse points."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ManifestPath.Value);
            if (info.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("preview-manifest-size-limit", DiagnosticSeverity.Error,
                    $"Preview scene manifests may not exceed {MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        string npcFormId;
        ImmutableArray<PreviewSceneInputAsset> inputs;
        ImmutableArray<PreviewSceneInputMorph> morphInputs;
        ImmutableArray<PreviewSceneInputVariant> variants;
        ImmutableArray<PreviewCameraPreset> cameraPresets = [];
        ImmutableArray<PreviewLightingPreset> lightingPresets = [];
        PreviewCameraPreset? camera = null;
        PreviewLightingPreset? lighting = null;
        ImmutableArray<PreviewAnimationClip> animations = [];
        PreviewSceneAnimation? animation = null;
        PreviewHairZapPlan? hairZap = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            (npcFormId, inputs, morphInputs, variants) = ReadManifest(document.RootElement, request.Edition, diagnostics);
            (cameraPresets, lightingPresets) = PreviewPresetCatalog.Read(document.RootElement, diagnostics);
            animations = PreviewAnimationCatalog.Read(document.RootElement, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        PreviewPresetCatalog.TryResolveCamera(cameraPresets, request.CameraId, out camera, diagnostics);
        if (request.LightingOverride is { } lightingOverride)
        {
            diagnostics.AddRange(
                SkyrimLightingRules.Validate(lightingOverride));
            lighting = lightingOverride;
        }
        else
        {
            PreviewPresetCatalog.TryResolveLighting(
                lightingPresets,
                request.LightingId,
                out lighting,
                diagnostics);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);
        animation = PreviewAnimationCatalog.Resolve(animations, request.AnimationId, request.AnimationFrame,
            request.AnimationTimeSeconds, request.AnimationPlaybackRate, request.AnimationPlaying, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        hairZap = PreviewHairZapRules.Resolve(request.Edition, request.RenderHeadwear,
            request.CoveredHairSlots, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var selectedVariant = ResolveVariant(request, variants, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var visible = request.VisibleCategories ?? Enum.GetValues<PreviewAssetCategory>().ToImmutableHashSet();
        var selectedAssetPaths = selectedVariant?.AssetPaths.Select(item => item.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = inputs.Select(input =>
        {
            var included = selectedAssetPaths is null || selectedAssetPaths.Contains(input.Path.Value);
            return new PreviewSceneAsset(input.Category.ToWireName(), input.Path.Value,
                input.Provider, input.Sha256.Value, included && visible.Contains(input.Category), included);
        }).ToImmutableArray();
        var morphCategories = request.MorphCategories ?? Enum.GetValues<PreviewMorphCategory>().ToImmutableHashSet();
        var selectedMorphs = selectedVariant?.Morphs.ToDictionary(input => MorphKey(input.Category, input.Name),
            input => input.Value, StringComparer.OrdinalIgnoreCase);
        var morphs = morphInputs.Select(input =>
        {
            var included = selectedMorphs is null || selectedMorphs.ContainsKey(MorphKey(input.Category, input.Name));
            var value = selectedMorphs is not null && selectedMorphs.TryGetValue(MorphKey(input.Category, input.Name), out var selectedValue)
                ? selectedValue : input.Value;
            return new PreviewSceneMorph(input.Category.ToWireName(), input.Name, value,
                included && morphCategories.Contains(input.Category), included);
        }).ToImmutableArray();
        var categoryCounts = Enum.GetValues<PreviewAssetCategory>()
            .Select(category => new PreviewSceneCategoryCount(category.ToWireName(),
                inputs.Count(item => item.Category == category)))
            .Where(item => item.Count > 0).ToImmutableArray();
        var scenePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            edition = request.Edition.ToWireName(),
            npcFormId,
            assets,
            morphs,
            variant = selectedVariant is null ? null : new
            {
                id = selectedVariant.Id,
                outfit = selectedVariant.Outfit.ToString()
            },
            camera,
            lighting,
            animation,
            hairZap
        }, JsonOptions);
        var sceneHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(scenePayload)));
        var artifact = new PreviewSceneArtifact("1", "preview-scene-semantic-build",
            request.Edition.ToWireName(), npcFormId, inputHash.Value, sceneHash.Value, assets.Length,
            assets.Count(item => item.Included), assets.Count(item => item.Visible), categoryCounts, assets,
            morphs.Count(item => item.Applied), morphs,
            selectedVariant is null ? null : new PreviewSceneVariant(selectedVariant.Id,
                selectedVariant.Outfit.ToString(), assets.Count(item => item.Included),
                morphs.Count(item => item.Included)), camera, lighting, animation, hairZap);
        if (request.ImageOutputPath is { } imageOutput)
        {
            if (imageRenderer is null)
            {
                diagnostics.Add(new Diagnostic("preview-renderer-unavailable", DiagnosticSeverity.Error,
                    "Pixel rendering requires the admitted K-local Blender/PyNifly renderer."));
                return new PreviewSceneResult(false, artifact, null, diagnostics.ToImmutable());
            }
            var imageResult = await imageRenderer.RenderAsync(new PreviewImageRenderRequest(
                request.Edition, request.AssetRoot!.Value, imageOutput, assets,
                request.ImageWidth, request.ImageHeight,
                morphs.Where(item => item.Applied && item.Included).ToImmutableArray(),
                animation, hairZap), cancellationToken);
            diagnostics.AddRange(imageResult.Diagnostics);
            if (!imageResult.Rendered || imageResult.Image is null || HasErrors(diagnostics))
                return new PreviewSceneResult(false, artifact, null, diagnostics.ToImmutable());
            artifact = artifact with { RenderedImage = imageResult.Image };
        }
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        try
        {
            await PreviewArtifactFileWriter.WriteAsync(request.OutputPath, outputBytes, cancellationToken);
            return new PreviewSceneResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-output-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PreviewSceneResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    internal static (string NpcFormId, ImmutableArray<PreviewSceneInputAsset> Assets,
        ImmutableArray<PreviewSceneInputMorph> Morphs, ImmutableArray<PreviewSceneInputVariant> Variants) ReadManifest(
        JsonElement root, GameEdition edition, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-root", DiagnosticSeverity.Error,
                "Preview scene manifests must contain an object."));
            return (string.Empty, [], [], []);
        }
        if (!TryGet(root, "schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
            diagnostics.Add(new Diagnostic("preview-manifest-schema", DiagnosticSeverity.Error,
                "Preview scene schemaVersion must be 1."));
        if (!TryGet(root, "edition", out var editionElement) || editionElement.ValueKind != JsonValueKind.String ||
            !GameEditionExtensions.TryParseWireName(editionElement.GetString() ?? string.Empty, out var manifestEdition) ||
            manifestEdition != edition)
            diagnostics.Add(new Diagnostic("preview-manifest-edition-mismatch", DiagnosticSeverity.Error,
                "Preview scene edition must match --edition|--game."));
        var form = string.Empty;
        if (!TryGet(root, "npcFormId", out var formElement) || formElement.ValueKind != JsonValueKind.String ||
            !FormId.TryParse(formElement.GetString() ?? string.Empty, out var formId))
            diagnostics.Add(new Diagnostic("preview-manifest-formid-invalid", DiagnosticSeverity.Error,
                "Preview scene npcFormId must be a hexadecimal FormID string."));
        else form = formId.ToString();
        if (!TryGet(root, "assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array ||
            assetsElement.GetArrayLength() is 0 or > MaxAssets)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-assets-invalid", DiagnosticSeverity.Error,
                $"assets must be a non-empty array with at most {MaxAssets} entries."));
            return (form, [], ReadMorphs(root, diagnostics), ReadVariants(root, [], [], diagnostics));
        }

        var result = ImmutableArray.CreateBuilder<PreviewSceneInputAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in assetsElement.EnumerateArray())
        {
            var path = $"$.assets[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "category", out var categoryText) ||
                !PreviewAssetCategoryExtensions.TryParseWireName(categoryText, out var category) ||
                !TryGetString(item, "path", out var assetText) || !TryGetString(item, "provider", out var provider) ||
                !TryGetString(item, "sha256", out var hashText))
            {
                diagnostics.Add(new Diagnostic("preview-manifest-asset-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires category, path, provider, and sha256 strings."));
                continue;
            }
            try
            {
                var assetPath = new AssetPath(assetText);
                var hash = new Sha256Hash(hashText);
                if (provider.Length > 512 || provider.Any(char.IsControl) || !seen.Add(assetPath.Value))
                {
                    diagnostics.Add(new Diagnostic("preview-manifest-asset-duplicate", DiagnosticSeverity.Error,
                        $"'{path}' provider/path or asset path is invalid or duplicated."));
                    continue;
                }
                result.Add(new PreviewSceneInputAsset(category, assetPath, provider.Trim(), hash));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preview-manifest-asset-invalid", DiagnosticSeverity.Error,
                    $"'{path}' is invalid: {exception.Message}"));
            }
        }
        var assets = result.ToImmutable();
        var morphs = ReadMorphs(root, diagnostics);
        return (form, assets, morphs, ReadVariants(root, assets, morphs, diagnostics));
    }

}
