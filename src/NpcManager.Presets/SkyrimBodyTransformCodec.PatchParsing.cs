using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class SkyrimBodyTransformCodec
{
    internal static bool TryParseTransformReplacement(ReadOnlySpan<byte> bytes,
        out ImmutableArray<SkyrimNodeTransformPatch> patches,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        patches = [];
        if (!TryParseJson(bytes, "transform replacement", out var document, diagnostics)) return false;
        if (document!.RootElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-transform-patch-shape", DiagnosticSeverity.Error,
                "Transform replacement must be a JSON array."));
            return false;
        }
        if (document.RootElement.GetArrayLength() > MaxEntries)
        {
            diagnostics.Add(new Diagnostic("sse-transform-entry-limit", DiagnosticSeverity.Error,
                $"At most {MaxEntries} node transforms are supported."));
            return false;
        }

        var result = ImmutableArray.CreateBuilder<SkyrimNodeTransformPatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
        {
            var path = $"$[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("sse-transform-patch-item", DiagnosticSeverity.Error,
                    $"{path} must be an object."));
                continue;
            }
            CheckAllowed(item, TransformPatchFields, path, diagnostics);
            var node = RequiredString(item, "node", path, diagnostics, MaxNodeLength);
            var firstPerson = OptionalBool(item, "firstPerson", false, path, diagnostics);
            var scale = OptionalFloat(item, "scale", path, diagnostics);
            if (scale is < MinScale or > MaxScale)
                diagnostics.Add(new Diagnostic("sse-transform-scale", DiagnosticSeverity.Error,
                    $"{path}.scale must be between {MinScale.ToString(CultureInfo.InvariantCulture)} and {MaxScale.ToString(CultureInfo.InvariantCulture)}."));
            var scaleMode = OptionalInt(item, "scaleMode", path, diagnostics);
            if (scaleMode is < 0 or > 3)
                diagnostics.Add(new Diagnostic("sse-transform-scale-mode", DiagnosticSeverity.Error,
                    $"{path}.scaleMode must be 0, 1, 2, or 3."));
            var position = OptionalVector(item, "position", 3, path, diagnostics, 10000F);
            var rotation = OptionalVector(item, "rotation", 9, path, diagnostics, 1.5F);
            if (!rotation.IsDefaultOrEmpty && !IsRotationMatrix(rotation))
                diagnostics.Add(new Diagnostic("sse-transform-rotation", DiagnosticSeverity.Error,
                    $"{path}.rotation must be a finite right-handed 3x3 rotation matrix."));
            if (node is null || !seen.Add(node))
                diagnostics.Add(new Diagnostic("sse-transform-node-duplicate", DiagnosticSeverity.Error,
                    $"{path}.node must be unique (case-insensitive) and non-empty."));
            if (scale is null && scaleMode is null && position.IsDefaultOrEmpty && rotation.IsDefaultOrEmpty)
                diagnostics.Add(new Diagnostic("sse-transform-patch-empty", DiagnosticSeverity.Error,
                    $"{path} must provide scale, scaleMode, position, or rotation."));
            if (node is not null)
                result.Add(new SkyrimNodeTransformPatch(node, firstPerson, scale, scaleMode, position, rotation));
        }
        patches = result.ToImmutable();
        return !HasErrors(diagnostics);
    }

    internal static bool TryParseSkinReplacement(ReadOnlySpan<byte> bytes,
        out ImmutableArray<SkyrimSkinOverridePatch> patches,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        patches = [];
        if (!TryParseJson(bytes, "skin replacement", out var document, diagnostics)) return false;
        if (document!.RootElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-skin-patch-shape", DiagnosticSeverity.Error,
                "Skin replacement must be a JSON array."));
            return false;
        }
        if (document.RootElement.GetArrayLength() > MaxEntries)
        {
            diagnostics.Add(new Diagnostic("sse-skin-entry-limit", DiagnosticSeverity.Error,
                $"At most {MaxEntries} skin overrides are supported."));
            return false;
        }

        var result = ImmutableArray.CreateBuilder<SkyrimSkinOverridePatch>();
        var seen = new HashSet<(bool FirstPerson, uint SlotMask)>();
        foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
        {
            var path = $"$[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("sse-skin-patch-item", DiagnosticSeverity.Error,
                    $"{path} must be an object."));
                continue;
            }
            CheckAllowed(item, SkinPatchFields, path, diagnostics);
            var slotMask = RequiredUInt(item, "slotMask", path, diagnostics);
            var firstPerson = OptionalBool(item, "firstPerson", false, path, diagnostics);
            var textures = ReadPatchTextures(item, path, diagnostics);
            var tint = OptionalVector(item, "tint", 4, path, diagnostics, 1F);
            var alpha = OptionalFloat(item, "alpha", path, diagnostics);
            if (alpha is < 0 or > 1)
                diagnostics.Add(new Diagnostic("sse-skin-alpha", DiagnosticSeverity.Error,
                    $"{path}.alpha must be between 0 and 1."));
            if (!tint.IsDefaultOrEmpty && tint.Any(value => value is < 0 or > 1))
                diagnostics.Add(new Diagnostic("sse-skin-tint", DiagnosticSeverity.Error,
                    $"{path}.tint values must be between 0 and 1."));
            if (!seen.Add((firstPerson, slotMask)))
                diagnostics.Add(new Diagnostic("sse-skin-duplicate", DiagnosticSeverity.Error,
                    $"{path} duplicates a firstPerson/slotMask identity."));
            if (textures.Count == 0 && tint.IsDefaultOrEmpty && alpha is null)
                diagnostics.Add(new Diagnostic("sse-skin-patch-empty", DiagnosticSeverity.Error,
                    $"{path} must provide a texture, tint, or alpha value."));
            result.Add(new SkyrimSkinOverridePatch(slotMask, firstPerson, textures, tint, alpha));
        }
        patches = result.ToImmutable();
        return !HasErrors(diagnostics);
    }

}
