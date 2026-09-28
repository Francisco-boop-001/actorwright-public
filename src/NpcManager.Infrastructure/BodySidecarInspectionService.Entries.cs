using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class BodySidecarInspectionService
{
    private static ImmutableArray<BodySidecarNpcSummary> ReadEntries(
        JsonElement root,
        GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("npcs", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-npcs", DiagnosticSeverity.Error,
                "BodySlide sidecar npcs must be an object."));
            return [];
        }
        if (value.EnumerateObject().Count() > MaxNpcEntries)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-npc-count-limit", DiagnosticSeverity.Error,
                $"BodySlide sidecar contains more than {MaxNpcEntries} NPC entries."));
            return [];
        }

        var entries = ImmutableArray.CreateBuilder<BodySidecarNpcSummary>();
        foreach (var property in value.EnumerateObject()
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateIdentifier(property.Name, out var identifierError))
                diagnostics.Add(new Diagnostic("body-sidecar-identifier", DiagnosticSeverity.Error,
                    $"NPC sidecar key '{property.Name}' is invalid: {identifierError}"));
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-entry-shape", DiagnosticSeverity.Error,
                    $"NPC sidecar entry '{property.Name}' must be an object."));
                continue;
            }
            CheckKnownFields(property.Value, EntryFields, $"$.npcs.{property.Name}", diagnostics);
            entries.Add(ReadEntry(property.Name, property.Value, edition, diagnostics, cancellationToken));
        }
        return entries.ToImmutable();
    }

    private static BodySidecarNpcSummary ReadEntry(
        string identifier,
        JsonElement entry,
        GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var path = $"$.npcs.{identifier}";
        var editorId = ReadOptionalString(entry, "editorId", path, diagnostics);
        var skinTemplate = ReadOptionalString(entry, "skinTemplateId", path, diagnostics);
        var gender = ReadOptionalString(entry, "gender", path, diagnostics);
        if (gender is not null && gender is not ("" or "male" or "female"))
            diagnostics.Add(new Diagnostic("body-sidecar-gender", DiagnosticSeverity.Error,
                $"'{path}.gender' must be empty, male, or female."));

        var bodyMorphs = ReadFloatMap(entry, "bodyMorphs", path, diagnostics);
        var keyed = ReadKeyedFloatMap(entry, "bodyMorphsKeyed", path, edition, diagnostics);
        var overlays = ReadOverlays(entry, "overlays", path, diagnostics, cancellationToken);
        var sseOverlays = ReadSseOverlays(entry, "sseBodyOverlays", path, edition, diagnostics, cancellationToken);
        var nodeTransforms = ReadNodeTransforms(entry, "sseNodeTransforms", path, edition, diagnostics, cancellationToken);
        if (entry.TryGetProperty("sseNodeScales", out var scales))
        {
            AddSseOnlyDiagnostic(edition, "sseNodeScales", path, diagnostics);
            ValidateLegacyNodeScales(scales, path, diagnostics);
        }
        var hairColor = ReadHairColor(entry, path, edition, diagnostics);
        var skinOverrides = ReadSkinOverrides(entry, "sseSkinOverrides", path, edition, diagnostics, cancellationToken);
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> customMorphs = ReadCustomMorphs(
            entry, "sseCustomMorphs", path, edition, diagnostics, cancellationToken);
        ImmutableArray<RaceMenuSculptVertex> sculptVertices = ReadSculpt(
            entry, "sseSculpt", path, edition, diagnostics, cancellationToken);
        ImmutableArray<BodySidecarSculptPart> sculptParts = ReadSculptParts(
            entry, "sseSculptParts", path, edition, diagnostics, cancellationToken);
        ImmutableArray<BodySidecarTintTexture> tintTextures = ReadTintTextures(
            entry, "sseTintTextures", path, edition, diagnostics, cancellationToken);

        return new BodySidecarNpcSummary(identifier, editorId, bodyMorphs, keyed, skinTemplate, gender,
            overlays, sseOverlays, nodeTransforms, skinOverrides, customMorphs.Length,
            sculptVertices.Length, sculptParts.Length, tintTextures.Length,
            customMorphs, sculptVertices, sculptParts, tintTextures);
    }

    private static ImmutableDictionary<string, float> ReadFloatMap(
        JsonElement entry, string name, string path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!entry.TryGetProperty(name, out var value)) return ImmutableDictionary<string, float>.Empty;
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-map-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an object of finite numbers."));
            return ImmutableDictionary<string, float>.Empty;
        }
        if (value.EnumerateObject().Count() > MaxMapEntries)
            diagnostics.Add(new Diagnostic("body-sidecar-map-count-limit", DiagnosticSeverity.Error,
                $"'{path}.{name}' exceeds the map entry limit."));
        var builder = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !TryFinite(property.Value, out var number))
                diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                    $"'{path}.{name}.{property.Name}' must be a finite number."));
            else builder[property.Name] = number;
        }
        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, float>> ReadKeyedFloatMap(
        JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!entry.TryGetProperty(name, out var value)) return ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty;
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-keyed-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an object of keyed finite-number maps."));
            return ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty;
        }
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, float>>(StringComparer.Ordinal);
        foreach (var morph in value.EnumerateObject())
        {
            if (morph.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-keyed-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}.{morph.Name}' must be an object."));
                continue;
            }
            var inner = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.Ordinal);
            foreach (var key in morph.Value.EnumerateObject())
            {
                if (!TryFinite(key.Value, out var number))
                    diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                        $"'{path}.{name}.{morph.Name}.{key.Name}' must be a finite number."));
                else inner[key.Name] = number;
            }
            result[morph.Name] = inner.ToImmutable();
        }
        return result.ToImmutable();
    }

    private static int ReadOverlays(JsonElement entry, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-overlay-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return 0;
        }
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-overlay-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}]' must be an object."));
                continue;
            }
            CheckKnownFields(item, OverlayFields, $"{path}.{name}[{count}]", diagnostics);
            var template = ReadRequiredString(item, "template", $"{path}.{name}[{count}]", diagnostics);
            if (string.IsNullOrEmpty(template))
                diagnostics.Add(new Diagnostic("body-sidecar-overlay-template", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].template' must be non-empty."));
            ReadOptionalInt(item, "priority", $"{path}.{name}[{count}]", diagnostics);
            ValidateFloatArray(item, "tint", 4, $"{path}.{name}[{count}]", diagnostics);
            ValidateFloatArray(item, "offsetUV", 2, $"{path}.{name}[{count}]", diagnostics);
            ValidateFloatArray(item, "scaleUV", 2, $"{path}.{name}[{count}]", diagnostics);
            count++;
        }
        return count;
    }

    private static int ReadSseOverlays(JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return 0;
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-sse-overlay-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return 0;
        }
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-sse-overlay-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}]' must be an object."));
                continue;
            }
            CheckKnownFields(item, SseOverlayFields, $"{path}.{name}[{count}]", diagnostics);
            var node = ReadRequiredString(item, "node", $"{path}.{name}[{count}]", diagnostics);
            if (string.IsNullOrWhiteSpace(node))
                diagnostics.Add(new Diagnostic("body-sidecar-sse-overlay-node", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].node' must be non-empty."));
            ReadOptionalPath(item, "diffuse", $"{path}.{name}[{count}]", diagnostics, required: false);
            ReadOptionalPath(item, "normal", $"{path}.{name}[{count}]", diagnostics, required: false);
            ValidateFloatArray(item, "tint", 4, $"{path}.{name}[{count}]", diagnostics);
            if (item.TryGetProperty("alpha", out var alpha) &&
                (!TryFinite(alpha, out var valueAlpha) || valueAlpha is < 0 or > 1))
                diagnostics.Add(new Diagnostic("body-sidecar-alpha", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].alpha' must be a finite number from 0 to 1."));
            count++;
        }
        return count;
    }

    private static int ReadNodeTransforms(JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return 0;
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-transform-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return 0;
        }
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-transform-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}]' must be an object."));
                continue;
            }
            CheckKnownFields(item, NodeTransformFields, $"{path}.{name}[{count}]", diagnostics);
            if (string.IsNullOrWhiteSpace(ReadRequiredString(item, "node", $"{path}.{name}[{count}]", diagnostics)))
                diagnostics.Add(new Diagnostic("body-sidecar-transform-node", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].node' must be non-empty."));
            ValidateOptionalFinite(item, "s", $"{path}.{name}[{count}]", diagnostics);
            ReadOptionalInt(item, "sm", $"{path}.{name}[{count}]", diagnostics);
            ValidateFloatArray(item, "p", 3, $"{path}.{name}[{count}]", diagnostics);
            ValidateFloatArray(item, "r", 3, $"{path}.{name}[{count}]", diagnostics);
            count++;
        }
        return count;
    }

    private static void ValidateLegacyNodeScales(JsonElement value, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-transform-shape", DiagnosticSeverity.Error,
                $"'{path}.sseNodeScales' must be an object."));
            return;
        }
        foreach (var item in value.EnumerateObject())
            if (!TryFinite(item.Value, out _))
                diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                    $"'{path}.sseNodeScales.{item.Name}' must be a finite number."));
    }

    private static int ReadHairColor(JsonElement entry, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!entry.TryGetProperty("sseHairColor", out var value)) return 0;
        AddSseOnlyDiagnostic(edition, "sseHairColor", path, diagnostics);
        if (!value.TryGetInt32(out var color) || color is < 0 or > 0xFFFFFF)
            diagnostics.Add(new Diagnostic("body-sidecar-hair-color", DiagnosticSeverity.Error,
                $"'{path}.sseHairColor' must be a packed RGB integer from 0 through 0xFFFFFF."));
        return 1;
    }

    private static int ReadSkinOverrides(JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return 0;
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-skin-override-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return 0;
        }
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-skin-override-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}]' must be an object."));
                continue;
            }
            CheckKnownFields(item, SkinOverrideFields, $"{path}.{name}[{count}]", diagnostics);
            ReadOptionalUInt(item, "slotMask", $"{path}.{name}[{count}]", diagnostics);
            ReadOptionalPath(item, "diffuse", $"{path}.{name}[{count}]", diagnostics, required: false);
            ReadOptionalPath(item, "normal", $"{path}.{name}[{count}]", diagnostics, required: false);
            ValidateFloatArray(item, "tint", 4, $"{path}.{name}[{count}]", diagnostics);
            count++;
        }
        return count;
    }

}
