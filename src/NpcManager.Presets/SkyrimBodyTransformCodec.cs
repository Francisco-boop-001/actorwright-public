using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Independent, bounded codec for the two RaceMenu body metadata arrays.
/// It intentionally models only the value keys the pinned app understands. Any other
/// key is a blocking diagnostic so a later writer cannot silently discard co-save data.</summary>
internal static partial class SkyrimBodyTransformCodec
{
    internal const long MaxBytes = 16L * 1024 * 1024;
    internal const int MaxEntries = 256;
    internal const int MaxValues = 512;
    private const int MaxNodeLength = 256;
    private const int MaxTexturePathLength = 260;
    private const float MinScale = 0.01F;
    private const float MaxScale = 1000F;

    private static readonly ImmutableHashSet<string> TransformFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "firstPerson", "node", "keys");
    private static readonly ImmutableHashSet<string> KeyFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "name", "values");
    private static readonly ImmutableHashSet<string> ValueFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "key", "type", "index", "data");
    private static readonly ImmutableHashSet<string> SkinFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "firstPerson", "slotMask", "values");

    internal sealed record SourceSections(
        JsonObject Root,
        bool HadTransforms,
        bool HadSkinOverrides,
        ImmutableArray<SkyrimNodeTransform> Transforms,
        ImmutableArray<SkyrimSkinOverride> SkinOverrides);

    internal static bool TryParseSource(byte[] bytes, out SourceSections? sections,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        sections = null;
        if (bytes.LongLength > MaxBytes)
        {
            diagnostics.Add(new Diagnostic("sse-transform-size-limit", DiagnosticSeverity.Error,
                $"RaceMenu preset exceeds the {MaxBytes} byte safety limit."));
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("sse-transform-root-shape", DiagnosticSeverity.Error,
                    "RaceMenu preset root must be a JSON object."));
                return false;
            }

            CheckDuplicateProperties(document.RootElement, "$", diagnostics);
            CheckCaseVariants(document.RootElement, "transforms", diagnostics);
            CheckCaseVariants(document.RootElement, "skinOverrides", diagnostics);
            var hadTransforms = document.RootElement.TryGetProperty("transforms", out var transformElement);
            var hadSkinOverrides = document.RootElement.TryGetProperty("skinOverrides", out var skinElement);
            var transforms = hadTransforms
                ? ParseTransforms(transformElement, diagnostics)
                : ImmutableArray<SkyrimNodeTransform>.Empty;
            var skins = hadSkinOverrides
                ? ParseSkinOverrides(skinElement, diagnostics)
                : ImmutableArray<SkyrimSkinOverride>.Empty;
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root is null)
            {
                diagnostics.Add(new Diagnostic("sse-transform-root-shape", DiagnosticSeverity.Error,
                    "RaceMenu preset root could not be materialized safely."));
                return false;
            }
            sections = new SourceSections(root, hadTransforms, hadSkinOverrides, transforms, skins);
            return !HasErrors(diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("sse-transform-json-invalid", DiagnosticSeverity.Error,
                $"RaceMenu preset JSON is invalid: {exception.Message}"));
            return false;
        }
    }

    internal static void ReplaceSections(JsonObject root, bool replaceTransforms,
        ImmutableArray<SkyrimNodeTransformPatch> transforms, bool replaceSkins,
        ImmutableArray<SkyrimSkinOverridePatch> skins,
        SourceSections source)
    {
        // Canonicalize the modeled sections even when no replacement was supplied. This makes
        // the typed mapping observable and prevents a later writer from depending on raw JSON shape.
        if (replaceTransforms || source.HadTransforms)
            root["transforms"] = new JsonArray((replaceTransforms ? transforms.Select(BuildTransform) : source.Transforms.Select(BuildTransform)).ToArray());
        if (replaceSkins || source.HadSkinOverrides)
            root["skinOverrides"] = new JsonArray((replaceSkins ? skins.Select(BuildSkin) : source.SkinOverrides.Select(BuildSkin)).ToArray());
    }

    internal static void ValidateReplacements(
        ImmutableArray<SkyrimNodeTransformPatch>? transforms,
        ImmutableArray<SkyrimSkinOverridePatch>? skins,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (transforms is { } transformArray)
        {
            if (transformArray.Length > MaxEntries)
                diagnostics.Add(new Diagnostic("sse-transform-entry-limit", DiagnosticSeverity.Error,
                    $"At most {MaxEntries} node transforms are supported."));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (patch, index) in transformArray.Select((value, position) => (value, position)))
            {
                var path = $"$.transforms[{index}]";
                var identity = $"{(patch.FirstPerson ? 1 : 0)}\0{patch.Node}";
                if (string.IsNullOrWhiteSpace(patch.Node) || patch.Node.Length > MaxNodeLength || patch.Node.Contains('\0') || !seen.Add(identity))
                    diagnostics.Add(new Diagnostic("sse-transform-node-duplicate", DiagnosticSeverity.Error,
                        $"{path}.node must be unique for its first-person state and use a safe non-empty name."));
                if (string.IsNullOrWhiteSpace(patch.KeyName) || patch.KeyName.Length > 64 || patch.KeyName.Contains('\0'))
                    diagnostics.Add(new Diagnostic("sse-transform-key-name", DiagnosticSeverity.Error,
                        $"{path}.keyName must be a safe non-empty name of at most 64 characters."));
                if (patch.Scale is < MinScale or > MaxScale || (patch.Scale is { } scale && !float.IsFinite(scale)))
                    diagnostics.Add(new Diagnostic("sse-transform-scale", DiagnosticSeverity.Error, $"{path}.scale is outside the safe range."));
                if (patch.ScaleMode is < 0 or > 3)
                    diagnostics.Add(new Diagnostic("sse-transform-scale-mode", DiagnosticSeverity.Error, $"{path}.scaleMode must be 0-3."));
                if (patch.Position.Length is not (0 or 3) || patch.Position.Any(value => !float.IsFinite(value) || Math.Abs(value) > 10000F))
                    diagnostics.Add(new Diagnostic("sse-transform-position", DiagnosticSeverity.Error, $"{path}.position must be empty or three finite safe values."));
                if (patch.RotationMatrix.Length is not (0 or 9) || (!patch.RotationMatrix.IsDefaultOrEmpty && !IsRotationMatrix(patch.RotationMatrix)))
                    diagnostics.Add(new Diagnostic("sse-transform-rotation", DiagnosticSeverity.Error, $"{path}.rotation must be empty or a valid 3x3 matrix."));
                if (patch.Scale is null && patch.ScaleMode is null && patch.Position.IsDefaultOrEmpty && patch.RotationMatrix.IsDefaultOrEmpty)
                    diagnostics.Add(new Diagnostic("sse-transform-patch-empty", DiagnosticSeverity.Error, $"{path} has no editable transform component."));
            }
        }
        if (skins is { } skinArray)
        {
            if (skinArray.Length > MaxEntries)
                diagnostics.Add(new Diagnostic("sse-skin-entry-limit", DiagnosticSeverity.Error,
                    $"At most {MaxEntries} skin overrides are supported."));
            var seen = new HashSet<(bool FirstPerson, uint SlotMask)>();
            foreach (var (patch, index) in skinArray.Select((value, position) => (value, position)))
            {
                var path = $"$.skinOverrides[{index}]";
                if (!seen.Add((patch.FirstPerson, patch.SlotMask)))
                    diagnostics.Add(new Diagnostic("sse-skin-duplicate", DiagnosticSeverity.Error, $"{path} duplicates a slot identity."));
                if (patch.Textures.Count == 0 && patch.Tint.IsDefaultOrEmpty && patch.Alpha is null)
                    diagnostics.Add(new Diagnostic("sse-skin-patch-empty", DiagnosticSeverity.Error, $"{path} has no editable value."));
                foreach (var (slot, texture) in patch.Textures)
                    if (slot is < 0 or >= MaxValues || !ValidTexturePath(texture))
                        diagnostics.Add(new Diagnostic("sse-skin-texture-path", DiagnosticSeverity.Error, $"{path} contains an unsafe texture slot."));
                if (patch.Tint.Length is not (0 or 4) || patch.Tint.Any(value => !float.IsFinite(value) || value is < 0 or > 1))
                    diagnostics.Add(new Diagnostic("sse-skin-tint", DiagnosticSeverity.Error, $"{path}.tint must be empty or four unit values."));
                if (patch.Alpha is < 0 or > 1 || (patch.Alpha is { } alpha && !float.IsFinite(alpha)))
                    diagnostics.Add(new Diagnostic("sse-skin-alpha", DiagnosticSeverity.Error, $"{path}.alpha must be a unit value."));
            }
        }
    }

}
