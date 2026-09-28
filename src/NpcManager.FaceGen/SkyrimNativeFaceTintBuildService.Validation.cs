using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class SkyrimNativeFaceTintBuildService
{
    private void ValidateRequest(
        SkyrimNativeFaceTintBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.RecordRoute is null)
        {
            diagnostics.Add(Error("skyrim-native-tint-route", "The record route is absent."));
            return;
        }
        if (request.RecordRoute.Layers.IsDefault ||
            request.RecordRoute.Layers.Length > MaximumLayers)
        {
            diagnostics.Add(Error("skyrim-native-tint-layer-count",
                $"The record route must explicitly contain at most {MaximumLayers} layers."));
            return;
        }
        if (request.Masks.IsDefault)
        {
            diagnostics.Add(Error("skyrim-native-tint-masks",
                "The exact resolved mask array must be explicit, even when empty."));
            return;
        }

        Dictionary<ushort, int> indexCounts = request.RecordRoute.Layers
            .Where(item => item is not null)
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.Count());
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var order = 0; order < request.RecordRoute.Layers.Length; order++)
        {
            SkyrimNativeFaceTintLayerRoute layer = request.RecordRoute.Layers[order];
            if (layer is null || layer.RaceOrder != order ||
                !Enum.IsDefined(layer.ColorSource) ||
                (indexCounts[layer.Index] > 1 &&
                 layer.ColorSource != SkyrimNativeFaceTintColorSource.RaceDefault) ||
                !float.IsFinite(layer.Coverage) || layer.Coverage is < 0F or > 1F ||
                string.IsNullOrWhiteSpace(layer.MaskPath.Value) ||
                !layer.MaskPath.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("skyrim-native-tint-layer-shape",
                    $"Layer {order} has invalid race order, tint identity/source, coverage, or mask path."));
                continue;
            }
            if (layer.Coverage > 0F)
            {
                activePaths.Add(layer.MaskPath.Value);
            }
        }

        var providedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimNativeFaceTintMaskInput mask in request.Masks)
        {
            if (mask is null || string.IsNullOrWhiteSpace(mask.AssetPath.Value) ||
                !providedPaths.Add(mask.AssetPath.Value) ||
                !activePaths.Contains(mask.AssetPath.Value) ||
                mask.Content.IsDefaultOrEmpty || mask.Content.Length > MaximumMaskBytes ||
                string.IsNullOrWhiteSpace(mask.ContentSha256.Value))
            {
                diagnostics.Add(Error("skyrim-native-tint-mask-shape",
                    "Every mask must be one unique active RACE-declared DDS with bounded exact content."));
                continue;
            }

            byte[] bytes = ImmutableCollectionsMarshal.AsArray(mask.Content) ?? mask.Content.ToArray();
            var actual = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (actual != mask.ContentSha256)
            {
                diagnostics.Add(Error("skyrim-native-tint-mask-hash",
                    $"Mask '{mask.AssetPath}' does not match its declared SHA-256."));
            }
        }
        if (!activePaths.SetEquals(providedPaths))
        {
            diagnostics.Add(Error("skyrim-native-tint-mask-closure",
                "Resolved masks must exactly cover every active RACE layer and no inactive or unrelated path."));
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath.Value) ||
            !request.OutputPath.IsUnder(labRoot) || request.OutputPath == labRoot ||
            HasAlternateDataStream(request.OutputPath.Value) ||
            !request.OutputPath.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("skyrim-native-tint-output-path",
                "The output must be a fresh ordinary DDS path under the K-local lab root."));
            return;
        }
        string? parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("skyrim-native-tint-output-parent",
                "The output parent must already exist."));
            return;
        }
        diagnostics.AddRange(workspacePolicy.Evaluate(
            labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPath.Value) || Directory.Exists(request.OutputPath.Value))
        {
            diagnostics.Add(Error("skyrim-native-tint-output-exists",
                "Native FaceTint output is no-overwrite."));
        }
        try
        {
            if (TraversesReparsePoint(parent))
            {
                diagnostics.Add(Error("skyrim-native-tint-output-reparse",
                    "The output parent traverses a reparse point."));
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("skyrim-native-tint-output-inspection",
                exception.Message));
        }
    }

    private bool TraversesReparsePoint(string path)
    {
        var current = path;
        while (!string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = parent;
        }
        return false;
    }

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }
}
