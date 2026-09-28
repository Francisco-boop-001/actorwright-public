using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed partial class RaceMenuSculptPatchService
{
    private static byte[] WritePatchedDocument(byte[] source, ImmutableArray<StoredSculptPart> desired,
        int sculptDivisor,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryParse(source, out var document, out var parseDiagnostics) || document is null)
        {
            diagnostics.AddRange(parseDiagnostics);
            throw new InvalidDataException("RaceMenu preset JSON is invalid.");
        }
        diagnostics.AddRange(parseDiagnostics);
        using (document)
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                var wroteMorphs = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (string.Equals(property.Name, "morphs", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMorphs(writer, property.Value, desired, sculptDivisor);
                        wroteMorphs = true;
                    }
                    else property.Value.WriteTo(writer);
                }
                if (!wroteMorphs)
                {
                    writer.WritePropertyName("morphs");
                    WriteMorphs(writer, default, desired, sculptDivisor);
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            return stream.ToArray();
        }
    }

    private static void WriteMorphs(Utf8JsonWriter writer, JsonElement existing,
        ImmutableArray<StoredSculptPart> desired, int sculptDivisor)
    {
        writer.WriteStartObject();
        var wroteSculpt = false;
        if (existing.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in existing.EnumerateObject())
            {
                if (string.Equals(property.Name, "sculpt", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSculpt(writer, desired);
                    wroteSculpt = true;
                }
                else if (string.Equals(property.Name, "sculptDivisor", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteNumber(property.Name, sculptDivisor);
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }
        }
        if (!wroteSculpt)
        {
            writer.WritePropertyName("sculpt");
            WriteSculpt(writer, desired);
        }
        if (existing.ValueKind != JsonValueKind.Object || !existing.EnumerateObject().Any(item =>
                string.Equals(item.Name, "sculptDivisor", StringComparison.OrdinalIgnoreCase)))
            writer.WriteNumber("sculptDivisor", sculptDivisor);
        writer.WriteEndObject();
    }

    private static void WriteSculpt(Utf8JsonWriter writer, ImmutableArray<StoredSculptPart> parts)
    {
        writer.WriteStartArray();
        foreach (var part in parts)
        {
            writer.WriteStartObject();
            writer.WriteString("host", part.Host);
            writer.WriteNumber("vertices", part.VertexCount ?? 0);
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            foreach (var vertex in part.Vertices)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(vertex.Index);
                writer.WriteNumberValue(vertex.Dx);
                writer.WriteNumberValue(vertex.Dy);
                writer.WriteNumberValue(vertex.Dz);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void ValidatePatch(RaceMenuSculptPatch patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.SculptDivisor is < 1 or > MaxDivisor)
            diagnostics.Add(new Diagnostic("sculpt-divisor-range", DiagnosticSeverity.Error,
                $"sculptDivisor must be between 1 and {MaxDivisor}."));
        if (patch.Parts.Length > MaxParts)
            diagnostics.Add(new Diagnostic("sculpt-part-count", DiagnosticSeverity.Error,
                $"A patch may contain at most {MaxParts} shape blocks."));
        foreach (var part in patch.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Host) || part.Host.Length > MaxHostLength || part.Host.Any(char.IsControl))
                diagnostics.Add(new Diagnostic("sculpt-host", DiagnosticSeverity.Error,
                    "Each sculpt host must be non-empty, printable, and at most 512 characters."));
            if (part.VertexCount < 1 || part.VertexCount > int.MaxValue)
                diagnostics.Add(new Diagnostic("sculpt-vertex-count", DiagnosticSeverity.Error,
                    "Each sculpt vertex count must be between 1 and Int32.MaxValue."));
            if (part.Vertices.Length == 0 || part.Vertices.Length > MaxVerticesPerPart)
                diagnostics.Add(new Diagnostic("sculpt-vertex-count", DiagnosticSeverity.Error,
                    $"Each sculpt block must contain 1..{MaxVerticesPerPart} vertices."));
            var indices = new HashSet<int>();
            foreach (var vertex in part.Vertices)
            {
                if (vertex.Index < 0 || vertex.Index >= part.VertexCount)
                    diagnostics.Add(new Diagnostic("sculpt-index-range", DiagnosticSeverity.Error,
                        $"Sculpt vertex index {vertex.Index} is outside host vertex count {part.VertexCount}."));
                else if (!indices.Add(vertex.Index))
                    diagnostics.Add(new Diagnostic("sculpt-duplicate-index", DiagnosticSeverity.Error,
                        $"Sculpt host '{part.Host}' contains duplicate vertex index {vertex.Index}."));
                ValidateCoordinate(vertex.Dx, "dx", diagnostics);
                ValidateCoordinate(vertex.Dy, "dy", diagnostics);
                ValidateCoordinate(vertex.Dz, "dz", diagnostics);
                if (patch.SculptDivisor >= 1)
                {
                    ValidateRawRange(vertex.Dx, patch.SculptDivisor, "dx", diagnostics);
                    ValidateRawRange(vertex.Dy, patch.SculptDivisor, "dy", diagnostics);
                    ValidateRawRange(vertex.Dz, patch.SculptDivisor, "dz", diagnostics);
                }
            }
        }
    }

    private static void ValidateCoordinate(float value, string axis, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!float.IsFinite(value))
            diagnostics.Add(new Diagnostic("sculpt-nonfinite", DiagnosticSeverity.Error, $"Sculpt {axis} must be finite."));
        else if (value < -MaxCoordinate || value > MaxCoordinate)
            diagnostics.Add(new Diagnostic("sculpt-coordinate-range", DiagnosticSeverity.Error,
                $"Sculpt {axis} must be between -{MaxCoordinate} and {MaxCoordinate}."));
    }

    private static void ValidateRawRange(float value, int divisor, string axis,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!float.IsFinite(value)) return;
        var scaled = (double)value * divisor;
        if (scaled < int.MinValue || scaled > int.MaxValue)
            diagnostics.Add(new Diagnostic("sculpt-raw-range", DiagnosticSeverity.Error,
                $"Sculpt {axis} overflows the signed 32-bit RaceMenu delta after divisor scaling."));
    }
}
