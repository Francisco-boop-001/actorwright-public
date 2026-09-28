using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class BodySidecarInspectionService
{
    private static ImmutableArray<SkyrimRaceMenuCustomMorphValue> ReadCustomMorphs(
        JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return [];
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-custom-morph-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return [];
        }
        var rows = ImmutableArray.CreateBuilder<SkyrimRaceMenuCustomMorphValue>();
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-custom-morph-shape", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}]' must be an object."));
                count++;
                continue;
            }
            CheckKnownFields(item, CustomMorphFields, $"{path}.{name}[{count}]", diagnostics);
            string? morphName = ReadRequiredString(item, "name", $"{path}.{name}[{count}]", diagnostics);
            if (string.IsNullOrWhiteSpace(morphName))
                diagnostics.Add(new Diagnostic("body-sidecar-custom-morph-name", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].name' must be non-empty."));
            if (!item.TryGetProperty("value", out JsonElement morphValue) ||
                !TryFinite(morphValue, out float parsedValue))
            {
                diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                    $"'{path}.{name}[{count}].value' must be a finite number."));
            }
            else if (!string.IsNullOrWhiteSpace(morphName))
            {
                rows.Add(new SkyrimRaceMenuCustomMorphValue(morphName, parsedValue));
            }
            count++;
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<RaceMenuSculptVertex> ReadSculpt(
        JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return [];
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-sculpt-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return [];
        }
        var rows = ImmutableArray.CreateBuilder<RaceMenuSculptVertex>();
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaceMenuSculptVertex? vertex = ReadSculptVertex(
                item, $"{path}.{name}[{count}]", diagnostics);
            if (vertex is not null) rows.Add(vertex);
            count++;
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<BodySidecarSculptPart> ReadSculptParts(
        JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return [];
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-sculpt-parts-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return [];
        }
        var rows = ImmutableArray.CreateBuilder<BodySidecarSculptPart>();
        var partCount = 0;
        foreach (var part in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partPath = $"{path}.{name}[{partCount}]";
            if (part.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-sculpt-parts-shape", DiagnosticSeverity.Error,
                    $"'{partPath}' must be an object."));
                partCount++;
                continue;
            }
            CheckKnownFields(part, SculptPartFields, partPath, diagnostics);
            string? host = ReadRequiredString(part, "host", partPath, diagnostics);
            if (string.IsNullOrWhiteSpace(host))
                diagnostics.Add(new Diagnostic("body-sidecar-sculpt-host", DiagnosticSeverity.Error,
                    $"'{partPath}.host' must be non-empty."));
            if (!part.TryGetProperty("verts", out var verts) || verts.ValueKind != JsonValueKind.Array)
                diagnostics.Add(new Diagnostic("body-sidecar-sculpt-verts", DiagnosticSeverity.Error,
                    $"'{partPath}.verts' must be an array."));
            else
            {
                var vertices = ImmutableArray.CreateBuilder<RaceMenuSculptVertex>();
                var vertexIndex = 0;
                foreach (var vertex in verts.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RaceMenuSculptVertex? parsed = ReadSculptVertex(
                        vertex, $"{partPath}.verts[{vertexIndex}]", diagnostics);
                    if (parsed is not null) vertices.Add(parsed);
                    vertexIndex++;
                }
                if (!string.IsNullOrWhiteSpace(host) && vertices.Count > 0)
                    rows.Add(new BodySidecarSculptPart(host, vertices.ToImmutable()));
            }
            partCount++;
        }
        return rows.ToImmutable();
    }

    private static RaceMenuSculptVertex? ReadSculptVertex(JsonElement value, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-sculpt-shape", DiagnosticSeverity.Error,
                $"'{path}' must be an object."));
            return null;
        }
        CheckKnownFields(value, SculptFields, path, diagnostics);
        bool valid = true;
        uint index = 0;
        float dx = 0F;
        float dy = 0F;
        float dz = 0F;
        if (!value.TryGetProperty("index", out JsonElement indexElement) ||
            !indexElement.TryGetUInt32(out index) || index > int.MaxValue)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-integer", DiagnosticSeverity.Error,
                $"'{path}.index' must be an unsigned integer within the supported vertex range."));
            valid = false;
        }
        if (!value.TryGetProperty("dx", out JsonElement dxElement) ||
            !TryFinite(dxElement, out dx))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                $"'{path}.dx' must be a finite number."));
            valid = false;
        }
        if (!value.TryGetProperty("dy", out JsonElement dyElement) ||
            !TryFinite(dyElement, out dy))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                $"'{path}.dy' must be a finite number."));
            valid = false;
        }
        if (!value.TryGetProperty("dz", out JsonElement dzElement) ||
            !TryFinite(dzElement, out dz))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                $"'{path}.dz' must be a finite number."));
            valid = false;
        }
        return valid
            ? new RaceMenuSculptVertex(checked((int)index), dx, dy, dz)
            : null;
    }

    private static ImmutableArray<BodySidecarTintTexture> ReadTintTextures(
        JsonElement entry, string name, string path, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!entry.TryGetProperty(name, out var value)) return [];
        AddSseOnlyDiagnostic(edition, name, path, diagnostics);
        if (value.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-tint-texture-shape", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an array."));
            return [];
        }
        var rows = ImmutableArray.CreateBuilder<BodySidecarTintTexture>();
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemPath = $"{path}.{name}[{count}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-tint-texture-shape", DiagnosticSeverity.Error,
                    $"'{itemPath}' must be an object."));
                count++;
                continue;
            }
            CheckKnownFields(item, TintTextureFields, itemPath, diagnostics);
            bool valid = true;
            int index = 0;
            if (!item.TryGetProperty("index", out JsonElement indexElement) ||
                !indexElement.TryGetInt32(out index))
            {
                diagnostics.Add(new Diagnostic("body-sidecar-integer", DiagnosticSeverity.Error,
                    $"'{itemPath}.index' must be an integer."));
                valid = false;
            }
            AssetPath? texture = null;
            if (!item.TryGetProperty("texture", out JsonElement textureElement) ||
                textureElement.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("body-sidecar-path", DiagnosticSeverity.Error,
                    $"'{itemPath}.texture' must be a safe relative asset path."));
                valid = false;
            }
            else
            {
                try { texture = new AssetPath(textureElement.GetString() ?? string.Empty); }
                catch (ArgumentException)
                {
                    diagnostics.Add(new Diagnostic("body-sidecar-path", DiagnosticSeverity.Error,
                        $"'{itemPath}.texture' must be a safe relative asset path."));
                    valid = false;
                }
            }
            if (valid && texture is not null)
                rows.Add(new BodySidecarTintTexture(index, texture.Value));
            count++;
        }
        return rows.ToImmutable();
    }
}
