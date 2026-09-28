using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed partial class RaceMenuSculptPatchService
{
    private static ImmutableArray<StoredSculptPart> ReadStoredSculpt(byte[] source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryParse(source, out var document, out var parseDiagnostics) || document is null)
        {
            diagnostics.AddRange(parseDiagnostics);
            throw new InvalidDataException("RaceMenu preset JSON is invalid.");
        }
        diagnostics.AddRange(parseDiagnostics);
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("RaceMenu preset root must be an object.");
            if (!TryGet(document.RootElement, "morphs", out var morphs))
                return ImmutableArray<StoredSculptPart>.Empty;
            if (morphs.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("morphs must be an object.");
            var divisor = 10_000;
            if (TryGet(morphs, "sculptDivisor", out var divisorValue))
            {
                if (!TryReadInt32(divisorValue, out divisor) || divisor is < 1 or > MaxDivisor)
                    throw new InvalidDataException($"morphs.sculptDivisor must be between 1 and {MaxDivisor}.");
            }
            if (!TryGet(morphs, "sculpt", out var sculpt))
                return ImmutableArray<StoredSculptPart>.Empty;
            if (sculpt.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("morphs.sculpt must be an array.");
            var parts = ImmutableArray.CreateBuilder<StoredSculptPart>();
            var partIndex = 0;
            foreach (var item in sculpt.EnumerateArray())
            {
                var path = $"$.morphs.sculpt[{partIndex}]";
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{path} must be an object.");
                RejectUnknown(item, ["host", "vertices", "data"], path);
                var host = string.Empty;
                if (TryGet(item, "host", out var hostValue))
                {
                    if (hostValue.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"{path}.host must be a string.");
                    host = hostValue.GetString()!;
                }
                long? vertexCount = null;
                if (TryGet(item, "vertices", out var vertexValue))
                {
                    if (!TryReadInt64(vertexValue, out var parsed) || parsed < 0)
                        throw new InvalidDataException($"{path}.vertices must be a non-negative integer.");
                    vertexCount = parsed;
                }
                var hadData = TryGet(item, "data", out var data);
                var rows = ImmutableArray.CreateBuilder<StoredSculptVertex>();
                var indices = new HashSet<int>();
                if (hadData)
                {
                    if (data.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException($"{path}.data must be an array.");
                    var rowIndex = 0;
                    foreach (var row in data.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4)
                            throw new InvalidDataException($"{path}.data[{rowIndex}] must contain exactly four integers.");
                        if (!TryReadInt32(row[0], out var index) || index < 0 ||
                            !TryReadInt32(row[1], out var dx) || !TryReadInt32(row[2], out var dy) ||
                            !TryReadInt32(row[3], out var dz))
                            throw new InvalidDataException($"{path}.data[{rowIndex}] contains an invalid integer.");
                        if (vertexCount is { } count && index >= count)
                            throw new InvalidDataException($"{path}.data[{rowIndex}] index {index} exceeds vertex count {count}.");
                        if (!indices.Add(index))
                            throw new InvalidDataException($"{path}.data contains duplicate vertex index {index}.");
                        rows.Add(new StoredSculptVertex(index, dx, dy, dz));
                        rowIndex++;
                        if (rowIndex > MaxVerticesPerPart)
                            throw new InvalidDataException($"{path}.data exceeds {MaxVerticesPerPart} rows.");
                    }
                }
                parts.Add(new StoredSculptPart(host, vertexCount, rows.ToImmutable(), hadData, divisor));
                partIndex++;
                if (partIndex > MaxParts)
                    throw new InvalidDataException($"morphs.sculpt exceeds {MaxParts} shape blocks.");
            }
            return parts.ToImmutable();
        }
    }

    private static ImmutableArray<StoredSculptPart> BuildDesired(RaceMenuSculptPatch patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidatePatch(patch, diagnostics);
        var result = ImmutableArray.CreateBuilder<StoredSculptPart>(patch.Parts.Length);
        foreach (var part in patch.Parts)
        {
            var rows = ImmutableArray.CreateBuilder<StoredSculptVertex>(part.Vertices.Length);
            foreach (var vertex in part.Vertices)
            {
                rows.Add(new StoredSculptVertex(vertex.Index,
                    ToRaw(vertex.Dx, patch.SculptDivisor),
                    ToRaw(vertex.Dy, patch.SculptDivisor),
                    ToRaw(vertex.Dz, patch.SculptDivisor)));
            }
            result.Add(new StoredSculptPart(part.Host, part.VertexCount, rows.ToImmutable(), true, patch.SculptDivisor));
        }
        return result.ToImmutable();
    }
}
