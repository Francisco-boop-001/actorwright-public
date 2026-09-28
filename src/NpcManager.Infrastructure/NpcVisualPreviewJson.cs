using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpcManager.Infrastructure;

internal static class NpcVisualPreviewJson
{
    public static void AddCanonicalDictionaryConverters(
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(
            new OrdinalImmutableDictionaryConverter<string>());
        options.Converters.Add(
            new OrdinalImmutableDictionaryConverter<int>());
        options.Converters.Add(
            new OrdinalImmutableDictionaryConverter<long>());
        options.Converters.Add(
            new OrdinalImmutableDictionaryConverter<double>());
    }

    private sealed class OrdinalImmutableDictionaryConverter<TValue> :
        JsonConverter<ImmutableDictionary<string, TValue>>
    {
        public override ImmutableDictionary<string, TValue> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException(
                    "Expected an immutable dictionary JSON object.");
            var values = ImmutableDictionary.CreateBuilder<string, TValue>(
                StringComparer.Ordinal);
            while (reader.Read() &&
                   reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException(
                        "Expected an immutable dictionary property.");
                string name = reader.GetString() ??
                    throw new JsonException(
                        "Immutable dictionary property names may not be null.");
                if (!reader.Read())
                    throw new JsonException(
                        "Immutable dictionary property has no value.");
                TValue value = JsonSerializer.Deserialize<TValue>(
                    ref reader,
                    options) ?? throw new JsonException(
                    "Immutable dictionary values may not be null.");
                if (!values.TryAdd(name, value))
                    throw new JsonException(
                        $"Immutable dictionary property '{name}' is duplicate.");
            }
            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException(
                    "Immutable dictionary JSON object is incomplete.");
            return values.ToImmutable();
        }

        public override void Write(
            Utf8JsonWriter writer,
            ImmutableDictionary<string, TValue> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach ((string key, TValue item) in
                     value.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndObject();
        }
    }
}
