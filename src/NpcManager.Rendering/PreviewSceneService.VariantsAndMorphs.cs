using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Rendering;

public sealed partial class PreviewSceneService
{
    private static ImmutableArray<PreviewSceneInputVariant> ReadVariants(JsonElement root,
        ImmutableArray<PreviewSceneInputAsset> assets, ImmutableArray<PreviewSceneInputMorph> morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "variants", out var variantsElement)) return [];
        if (variantsElement.ValueKind != JsonValueKind.Array || variantsElement.GetArrayLength() > 64)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-variants-invalid", DiagnosticSeverity.Error,
                "variants must be an array with at most 64 entries."));
            return [];
        }

        var assetPaths = assets.Select(item => item.Path.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var morphKeys = morphs.Select(item => MorphKey(item.Category, item.Name))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<PreviewSceneInputVariant>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in variantsElement.EnumerateArray())
        {
            var path = $"$.variants[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "id", out var id) ||
                id.Length > 128 || id != id.Trim() || id.Any(char.IsControl) ||
                !TryGetString(item, "outfit", out var outfitText) ||
                !FormReference.TryParse(outfitText, out var outfit) || !seenIds.Add(id))
            {
                diagnostics.Add(new Diagnostic("preview-manifest-variant-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires a unique id (at most 128 characters) and a qualified outfit reference."));
                continue;
            }

            var selectedAssets = ImmutableArray.CreateBuilder<AssetPath>();
            if (!TryGet(item, "assets", out var assetList) || assetList.ValueKind != JsonValueKind.Array ||
                assetList.GetArrayLength() > MaxAssets)
            {
                diagnostics.Add(new Diagnostic("preview-manifest-variant-assets-invalid", DiagnosticSeverity.Error,
                    $"'{path}.assets' must be an array with at most {MaxAssets} entries."));
            }
            else
            {
                var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var assetIndex = 0;
                foreach (var assetElement in assetList.EnumerateArray())
                {
                    var assetPath = $"{path}.assets[{assetIndex++}]";
                    if (assetElement.ValueKind != JsonValueKind.String ||
                        !TryParseAssetPath(assetElement.GetString(), out var parsedPath) ||
                        !seenAssets.Add(parsedPath.Value))
                    {
                        diagnostics.Add(new Diagnostic("preview-manifest-variant-asset-invalid", DiagnosticSeverity.Error,
                            $"'{assetPath}' must be a unique normalized asset path."));
                        continue;
                    }
                    if (!assetPaths.Contains(parsedPath.Value))
                    {
                        diagnostics.Add(new Diagnostic("preview-manifest-variant-asset-unknown", DiagnosticSeverity.Error,
                            $"'{assetPath}' does not name an asset from the base scene manifest."));
                        continue;
                    }
                    selectedAssets.Add(parsedPath);
                }
            }

            var selectedMorphs = ReadMorphArray(item, "morphs", path, diagnostics);
            foreach (var selectedMorph in selectedMorphs)
            {
                if (!morphKeys.Contains(MorphKey(selectedMorph.Category, selectedMorph.Name)))
                    diagnostics.Add(new Diagnostic("preview-manifest-variant-morph-unknown", DiagnosticSeverity.Error,
                        $"'{path}.morphs' names a morph that is not present in the base scene manifest."));
            }

            result.Add(new PreviewSceneInputVariant(id, outfit, selectedAssets.ToImmutable(), selectedMorphs));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<PreviewSceneInputMorph> ReadMorphs(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics) => ReadMorphArray(root, "morphs", "$", diagnostics);

    private static ImmutableArray<PreviewSceneInputMorph> ReadMorphArray(JsonElement root, string propertyName,
        string parentPath, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, propertyName, out var morphsElement))
            return [];
        if (morphsElement.ValueKind != JsonValueKind.Array || morphsElement.GetArrayLength() > 256)
        {
            diagnostics.Add(new Diagnostic("preview-manifest-morphs-invalid", DiagnosticSeverity.Error,
                $"{parentPath}.{propertyName} must be an array with at most 256 entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<PreviewSceneInputMorph>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in morphsElement.EnumerateArray())
        {
            var path = $"{parentPath}.{propertyName}[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "category", out var categoryText) ||
                !PreviewMorphCategoryExtensions.TryParseWireName(categoryText, out var category) ||
                !TryGetString(item, "name", out var name) || name.Length > 255 ||
                !TryGet(item, "value", out var valueElement) || !valueElement.TryGetSingle(out var value) ||
                !float.IsFinite(value) || value is < -1 or > 1 || !seen.Add($"{category}:{name}"))
            {
                diagnostics.Add(new Diagnostic("preview-manifest-morph-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires unique category/name and a finite value from -1 to 1."));
                continue;
            }
            result.Add(new PreviewSceneInputMorph(category, name, value));
        }
        return result.ToImmutable();
    }

    private static SelectedVariant? ResolveVariant(PreviewSceneRequest request,
        ImmutableArray<PreviewSceneInputVariant> variants, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var hasOutfit = request.Outfit.HasValue;
        var hasVariant = !string.IsNullOrWhiteSpace(request.VariantId);
        if (!hasOutfit && !hasVariant) return null;
        if (hasOutfit != hasVariant)
        {
            diagnostics.Add(new Diagnostic("preview-variant-options-paired", DiagnosticSeverity.Error,
                "--outfit and --variant must be supplied together."));
            return null;
        }
        if (request.VariantId!.Length > 128 || request.VariantId != request.VariantId.Trim() ||
            request.VariantId.Any(char.IsControl))
        {
            diagnostics.Add(new Diagnostic("preview-variant-id-invalid", DiagnosticSeverity.Error,
                "Preview variant identifiers must be at most 128 characters and contain no control characters."));
            return null;
        }
        var selected = variants.FirstOrDefault(item =>
            string.Equals(item.Id, request.VariantId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            diagnostics.Add(new Diagnostic("preview-variant-not-found", DiagnosticSeverity.Error,
                $"Preview variant '{request.VariantId}' is not present in the manifest."));
            return null;
        }
        var requestedOutfit = request.Outfit.GetValueOrDefault();
        if (selected.Outfit != requestedOutfit)
        {
            diagnostics.Add(new Diagnostic("preview-variant-outfit-mismatch", DiagnosticSeverity.Error,
                $"Preview variant '{selected.Id}' is bound to '{selected.Outfit}', not '{requestedOutfit}'."));
            return null;
        }
        return new SelectedVariant(selected.Id, selected.Outfit, selected.AssetPaths, selected.Morphs);
    }

    private static bool TryParseAssetPath(string? value, out AssetPath path)
    {
        try
        {
            if (value is not null)
            {
                path = new AssetPath(value);
                return true;
            }
        }
        catch (ArgumentException) { }
        path = default;
        return false;
    }

    internal static string MorphKey(PreviewMorphCategory category, string name) =>
        $"{category.ToWireName()}:{name}";

    private sealed record SelectedVariant(string Id, FormReference Outfit,
        ImmutableArray<AssetPath> AssetPaths, ImmutableArray<PreviewSceneInputMorph> Morphs);
}
