using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Resolves source-shaped FO4 LooksMenu and Skyrim RaceMenu body-overlay layers.
/// It deliberately stops before texture decoding, compositing, plugin mutation, or runtime rendering.</summary>
public sealed class BodyOverlayPatchService : IBodyOverlayPatchService
{
    private const int MaxLayers = 256;
    private const int MaxSlotsPerTemplate = 31;
    private const int MaxPathLength = 260;
    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    public BodyOverlayPatchResult Resolve(BodyOverlayPatchRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Layers.Length > MaxLayers)
            diagnostics.Add(new Diagnostic("body-overlay-layer-limit", DiagnosticSeverity.Error,
                $"A body-overlay request may contain at most {MaxLayers} layers."));

        if (request.Edition is not (GameEdition.Fallout4 or GameEdition.SkyrimSpecialEdition))
            diagnostics.Add(new Diagnostic("body-overlay-game-unsupported", DiagnosticSeverity.Error,
                "Body overlays support Fallout 4 and Skyrim SE only."));

        var resolved = request.Edition == GameEdition.Fallout4
            ? ResolveFallout4(request.Layers, diagnostics)
            : ResolveSkyrim(request.Layers, diagnostics);

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) resolved = [];
        var canonical = CanonicalJson(request.Edition, request.NpcFormId, resolved);
        var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(canonical)));
        return new BodyOverlayPatchResult(request.Edition, request.NpcFormId, request.SourceSha256,
            hash, resolved, diagnostics.ToImmutable());
    }

    private static ImmutableArray<BodyOverlayResolvedLayer> ResolveFallout4(
        ImmutableArray<BodyOverlayLayerInput> layers, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var resolved = ImmutableArray.CreateBuilder<BodyOverlayResolvedLayer>();
        foreach (var layer in layers)
        {
            var path = $"$.layers[{layer.SourceIndex}]";
            if (string.IsNullOrWhiteSpace(layer.Template))
                diagnostics.Add(new Diagnostic("body-overlay-template-required", DiagnosticSeverity.Error,
                    $"{path}.template is required for Fallout 4."));
            if (layer.Node is not null || layer.Diffuse is not null || layer.Normal is not null ||
                layer.Alpha is not null || !layer.SseTint.IsDefaultOrEmpty)
                diagnostics.Add(new Diagnostic("body-overlay-cross-game-fields", DiagnosticSeverity.Error,
                    $"{path} contains Skyrim-only overlay fields."));
            ValidateVector(layer.Tint, 4, path, "tint", diagnostics, unitRange: true);
            ValidateVector(layer.OffsetUv, 2, path, "offsetUV", diagnostics, unitRange: false);
            ValidateVector(layer.ScaleUv, 2, path, "scaleUV", diagnostics, unitRange: false);
            ValidateSlots(layer.Slots, path, diagnostics);
            resolved.Add(new BodyOverlayResolvedLayer(
                0, layer.SourceIndex, layer.Priority, layer.Template?.Trim(), layer.Slots,
                null, null, null, null, null, layer.Tint, layer.OffsetUv, layer.ScaleUv, null));
        }

        return resolved.OrderBy(item => item.Priority).ThenBy(item => item.SourceIndex)
            .Select((item, index) => item with { Order = index }).ToImmutableArray();
    }

    private static ImmutableArray<BodyOverlayResolvedLayer> ResolveSkyrim(
        ImmutableArray<BodyOverlayLayerInput> layers, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var resolved = ImmutableArray.CreateBuilder<BodyOverlayResolvedLayer>();
        var seenNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            var path = $"$.layers[{layer.SourceIndex}]";
            var target = BodyOverlayTarget.Body;
            var nodeIndex = -1;
            if (string.IsNullOrWhiteSpace(layer.Node) ||
                !TryParseNode(layer.Node, out target, out nodeIndex))
                diagnostics.Add(new Diagnostic("body-overlay-node-invalid", DiagnosticSeverity.Error,
                    $"{path}.node must be Body, Hands, or Feet [OvlN] with a non-negative index."));
            else if (!seenNodes.Add(layer.Node.Trim()))
                diagnostics.Add(new Diagnostic("body-overlay-node-duplicate", DiagnosticSeverity.Error,
                    $"{path}.node duplicates another overlay node."));

            if (string.IsNullOrWhiteSpace(layer.Diffuse) || !ValidGamePath(layer.Diffuse, ".dds"))
                diagnostics.Add(new Diagnostic("body-overlay-diffuse-invalid", DiagnosticSeverity.Error,
                    $"{path}.diffuse must be a relative .dds texture path without traversal."));
            if (layer.Normal is not null && !ValidGamePath(layer.Normal, ".dds"))
                diagnostics.Add(new Diagnostic("body-overlay-normal-invalid", DiagnosticSeverity.Error,
                    $"{path}.normal must be a relative .dds texture path without traversal."));
            if (layer.Template is not null || layer.Priority != 0 || !layer.Slots.IsDefaultOrEmpty ||
                !layer.Tint.IsDefaultOrEmpty || !layer.OffsetUv.IsDefaultOrEmpty || !layer.ScaleUv.IsDefaultOrEmpty)
                diagnostics.Add(new Diagnostic("body-overlay-cross-game-fields", DiagnosticSeverity.Error,
                    $"{path} contains Fallout 4-only overlay fields."));
            ValidateVector(layer.SseTint, 4, path, "tint", diagnostics, unitRange: true);
            if (layer.Alpha is not null && (!float.IsFinite(layer.Alpha.Value) || layer.Alpha is < 0 or > 1))
                diagnostics.Add(new Diagnostic("body-overlay-alpha-invalid", DiagnosticSeverity.Error,
                    $"{path}.alpha must be a finite number from 0 to 1."));

            resolved.Add(new BodyOverlayResolvedLayer(
                0, layer.SourceIndex, null, null, [], target, nodeIndex, layer.Node?.Trim(),
                layer.Diffuse?.Trim(), layer.Normal?.Trim(), layer.SseTint,
                [], [], layer.Alpha ?? 1F));
        }

        return resolved.OrderBy(item => item.NodeIndex).ThenBy(item => item.SourceIndex)
            .Select((item, index) => item with { Order = index }).ToImmutableArray();
    }

    private static void ValidateSlots(ImmutableArray<Fo4OverlaySlot> slots, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (slots.Length > MaxSlotsPerTemplate)
            diagnostics.Add(new Diagnostic("body-overlay-slot-limit", DiagnosticSeverity.Error,
                $"{path}.slots contains more than {MaxSlotsPerTemplate} entries."));
        var seen = new HashSet<int>();
        foreach (var slot in slots)
        {
            if (slot.Slot is < 0 or > 30 || !seen.Add(slot.Slot))
                diagnostics.Add(new Diagnostic("body-overlay-slot-invalid", DiagnosticSeverity.Error,
                    $"{path}.slots contains a duplicate or out-of-range slot."));
            if (!ValidGamePath(slot.Material, ".bgem", ".bgsm"))
                diagnostics.Add(new Diagnostic("body-overlay-material-invalid", DiagnosticSeverity.Error,
                    $"{path}.slots material must be a relative .bgem or .bgsm path without traversal."));
        }
    }

    private static void ValidateVector(ImmutableArray<float> values, int expected, string path, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool unitRange)
    {
        if (values.IsDefaultOrEmpty) return;
        if (values.Length != expected)
        {
            diagnostics.Add(new Diagnostic("body-overlay-vector-shape", DiagnosticSeverity.Error,
                $"{path}.{name} must contain exactly {expected} numbers."));
            return;
        }
        foreach (var value in values)
        {
            if (!float.IsFinite(value) || (unitRange && (value < 0 || value > 1)))
                diagnostics.Add(new Diagnostic("body-overlay-vector-invalid", DiagnosticSeverity.Error,
                    $"{path}.{name} contains a non-finite or out-of-range value."));
        }
    }

    private static bool TryParseNode(string raw, out BodyOverlayTarget target, out int index)
    {
        target = default;
        index = -1;
        var value = raw.Trim();
        var open = value.IndexOf(" [Ovl", StringComparison.OrdinalIgnoreCase);
        if (open <= 0 || !value.EndsWith(']')) return false;
        var prefix = value[..open];
        if (prefix.Equals(nameof(BodyOverlayTarget.Body), StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Body;
        else if (prefix.Equals(nameof(BodyOverlayTarget.Hands), StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Hands;
        else if (prefix.Equals(nameof(BodyOverlayTarget.Feet), StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Feet;
        else return false;
        var digits = value[(open + 5)..^1];
        return digits.Length is > 0 and <= 3 && int.TryParse(digits, NumberStyles.None,
            CultureInfo.InvariantCulture, out index) && index >= 0 && index <= 127;
    }

    private static bool ValidGamePath(string? value, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxPathLength || Path.IsPathRooted(value) ||
            value.StartsWith('/') || value.StartsWith('\\') ||
            value.Contains(':') || value.Contains('\0')) return false;
        var normalized = value.Replace('/', '\\');
        if (normalized.Split('\\').Any(part => part is "." or "..")) return false;
        return extensions.Any(extension => normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CanonicalJson(GameEdition edition, FormId npc, ImmutableArray<BodyOverlayResolvedLayer> layers)
    {
        var payload = new
        {
            schemaVersion = 1,
            game = edition.ToWireName(),
            npcFormId = npc.ToString(),
            layers = layers.Select(layer => new
            {
                order = layer.Order,
                sourceIndex = layer.SourceIndex,
                priority = layer.Priority,
                template = layer.Template,
                slots = layer.Slots.Select(slot => new { slot = slot.Slot, material = slot.Material }).ToArray(),
                target = layer.Target?.ToString().ToLowerInvariant(),
                nodeIndex = layer.NodeIndex,
                node = layer.Node,
                diffuse = layer.Diffuse,
                normal = layer.Normal,
                tint = layer.Tint.ToArray(),
                offsetUV = layer.OffsetUv.ToArray(),
                scaleUV = layer.ScaleUv.ToArray(),
                alpha = layer.Alpha
            }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalOptions);
    }
}
