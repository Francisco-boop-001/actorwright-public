using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NpcManager.Infrastructure;

internal static class CanonicalJsonWriter
{
    internal static byte[] Serialize<T>(
        T value,
        JsonSerializerOptions serializerOptions,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            value,
            serializerOptions);
        RequireBounded(serialized, maximumBytes);

        using JsonDocument document = JsonDocument.Parse(
            serialized,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = serializerOptions.MaxDepth
            });
        var buffer = new ArrayBufferWriter<byte>(serialized.Length);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = serializerOptions.Encoder,
                       Indented = true,
                       MaxDepth = serializerOptions.MaxDepth
                   }))
        {
            WriteElement(writer, document.RootElement);
        }

        byte[] canonical = buffer.WrittenSpan.ToArray();
        if (Array.IndexOf(canonical, (byte)'\r') >= 0)
            canonical = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(canonical)
                    .Replace("\r\n", "\n", StringComparison.Ordinal));
        RequireBounded(canonical, maximumBytes);
        return canonical;
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void RequireBounded(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumBytes)
            throw new JsonException(
                "Canonical JSON exceeds its admitted byte boundary.");
    }
}
