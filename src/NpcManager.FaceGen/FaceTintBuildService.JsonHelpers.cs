using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class FaceTintBuildService
{
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default; return false;
    }

    private static string? String(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static int? Integer(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0d;
        return TryGet(element, name, out var property) && property.TryGetDouble(out value);
    }

    private static bool Unit(double value) => double.IsFinite(value) && value is >= 0d and <= 1d;

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("facetint-build-duplicate-key",
                    DiagnosticSeverity.Error, $"Duplicate JSON property '{path}.{property.Name}'."));
                ValidateDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) ValidateDuplicateProperties(item, $"{path}[{index++}]", diagnostics);
        }
    }

}
