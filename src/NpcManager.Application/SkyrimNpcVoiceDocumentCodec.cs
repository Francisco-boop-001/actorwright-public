using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Shared JSON codec for the voice and dialogue documents. Documents use
/// camelCase members, camelCase enum names, absolute K-local paths as strings,
/// and lower-case SHA-256 text. Serialization is deterministic for a given
/// document so the bytes can be hash-bound between stages.
/// </summary>
public static class SkyrimNpcVoiceDocumentCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }

    public static T Parse<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        return JsonSerializer.Deserialize<T>(bytes, Options) ??
            throw new JsonException("The document deserialized to null.");
    }

    public static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    public static string UtcNow() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new PluginNameConverter());
        options.Converters.Add(new FormIdConverter());
        options.Converters.Add(new EditorIdConverter());
        options.Converters.Add(new Sha256HashConverter());
        options.Converters.Add(new WorkspacePathConverter());
        return options;
    }

    private sealed class PluginNameConverter : JsonConverter<PluginName>
    {
        public override PluginName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("Plugin name must be a string."));

        public override void Write(Utf8JsonWriter writer, PluginName value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class FormIdConverter : JsonConverter<FormId>
    {
        public override FormId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string text = reader.GetString() ?? throw new JsonException("FormID must be a string.");
            return FormId.TryParse(text, out FormId parsed)
                ? parsed
                : throw new JsonException($"FormID '{text}' is not hexadecimal.");
        }

        public override void Write(Utf8JsonWriter writer, FormId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class EditorIdConverter : JsonConverter<EditorId>
    {
        public override EditorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("EditorID must be a string."));

        public override void Write(Utf8JsonWriter writer, EditorId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashConverter : JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("SHA-256 must be a string."));

        public override void Write(Utf8JsonWriter writer, Sha256Hash value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class WorkspacePathConverter : JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("Path must be a string."));

        public override void Write(Utf8JsonWriter writer, WorkspacePath value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
