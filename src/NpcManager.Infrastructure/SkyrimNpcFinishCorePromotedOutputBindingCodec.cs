using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public static class SkyrimNpcFinishCorePromotedOutputBindingCodec
{
    private const int MaximumBindingBytes = 8 * 1024 * 1024;
    private const int MaximumGroups = 64;
    private const int MaximumMasters = 64;
    private const int MaximumPnam = 256;
    public static byte[] Serialize(
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding binding)
    {
        Validate(binding);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaIdentifier", binding.SchemaIdentifier);
            writer.WriteString("sourceSelectedManifestPath",
                binding.SourceSelectedManifestPath.Value);
            writer.WriteString("sourceSelectedManifestSha256",
                binding.SourceSelectedManifestSha256.Value);
            writer.WriteStartArray("groups");
            foreach (var group in binding.Groups)
            {
                writer.WriteStartObject();
                writer.WriteString("descriptorId", group.DescriptorId.Value);
                writer.WriteString("attestationSha256", group.AttestationSha256.Value);
                WriteOutput(writer, group.OutputPlugin, group.OutputPluginSha256,
                    group.OutputPluginByteLength, group.MasterOrder,
                    group.DeclaredExternalPnam);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("outputPlugin", binding.OutputPlugin.Value);
            writer.WriteString("outputPluginSha256", binding.OutputPluginSha256.Value);
            writer.WriteNumber("outputPluginByteLength", binding.OutputPluginByteLength);
            writer.WriteStartArray("masterOrder");
            foreach (PluginName master in binding.MasterOrder)
                writer.WriteStringValue(master.Value);
            writer.WriteEndArray();
            writer.WriteStartArray("declaredExternalPnam");
            foreach (FormReference reference in binding.DeclaredExternalPnam)
                writer.WriteStringValue(reference.ToString());
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding Parse(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBindingBytes)
            throw new InvalidDataException(
                "Promoted output binding exceeds the bounded document size.");
        try
        {
            return ParseCore(bytes);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException(
                "Promoted output binding has an invalid shape or value.",
                exception);
        }
    }

    private static SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding ParseCore(
        ReadOnlySpan<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
        JsonElement root = document.RootElement;
        RequireMembers(root, "schemaIdentifier", "sourceSelectedManifestPath",
            "sourceSelectedManifestSha256", "groups", "outputPlugin",
            "outputPluginSha256", "outputPluginByteLength", "masterOrder",
            "declaredExternalPnam");
        var groups = ImmutableArray.CreateBuilder<
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding>();
        if (root.GetProperty("groups").GetArrayLength() == 0 ||
            root.GetProperty("groups").GetArrayLength() > MaximumGroups)
            throw new InvalidDataException("Promoted output binding group count is out of bounds.");
        foreach (JsonElement group in root.GetProperty("groups").EnumerateArray())
        {
            RequireMembers(group, "descriptorId", "attestationSha256", "outputPlugin",
                "outputPluginSha256", "outputPluginByteLength", "masterOrder",
                "declaredExternalPnam");
            ParseOutput(group, out PluginName plugin, out Sha256Hash hash,
                out long length, out ImmutableArray<PluginName> masters,
                out ImmutableArray<FormReference> pnam);
            groups.Add(new(
                ParseHash(group, "descriptorId"),
                ParseHash(group, "attestationSha256"), plugin, hash, length,
                masters, pnam));
        }
        var binding = new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding(
            RequiredString(root, "schemaIdentifier"),
            new AssetPath(RequiredString(root, "sourceSelectedManifestPath")),
            ParseHash(root, "sourceSelectedManifestSha256"),
            groups.ToImmutable(),
            new PluginName(RequiredString(root, "outputPlugin")),
            ParseHash(root, "outputPluginSha256"),
            RequiredPositiveLength(root, "outputPluginByteLength"),
            ParsePlugins(root.GetProperty("masterOrder")),
            ParseForms(root.GetProperty("declaredExternalPnam")));
        Validate(binding);
        if (!Serialize(binding).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException(
                "Promoted output binding is not canonical JSON.");
        return binding;
    }

    public static Sha256Hash Hash(
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding binding) =>
        new(Convert.ToHexString(SHA256.HashData(Serialize(binding))));

    private static void WriteOutput(
        Utf8JsonWriter writer, PluginName plugin, Sha256Hash hash, long length,
        ImmutableArray<PluginName> masters, ImmutableArray<FormReference> pnam)
    {
        writer.WriteString("outputPlugin", plugin.Value);
        writer.WriteString("outputPluginSha256", hash.Value);
        writer.WriteNumber("outputPluginByteLength", length);
        writer.WriteStartArray("masterOrder");
        foreach (PluginName master in masters)
            writer.WriteStringValue(master.Value);
        writer.WriteEndArray();
        writer.WriteStartArray("declaredExternalPnam");
        foreach (FormReference reference in pnam)
            writer.WriteStringValue(reference.ToString());
        writer.WriteEndArray();
    }

    private static void ParseOutput(JsonElement element, out PluginName plugin,
        out Sha256Hash hash, out long length, out ImmutableArray<PluginName> masters,
        out ImmutableArray<FormReference> pnam)
    {
        plugin = new PluginName(RequiredString(element, "outputPlugin"));
        hash = ParseHash(element, "outputPluginSha256");
        length = RequiredPositiveLength(element, "outputPluginByteLength");
        masters = ParsePlugins(element.GetProperty("masterOrder"));
        pnam = ParseForms(element.GetProperty("declaredExternalPnam"));
    }

    private static ImmutableArray<PluginName> ParsePlugins(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Promoted output master order must be an array.");
        if (element.GetArrayLength() == 0 || element.GetArrayLength() > MaximumMasters)
            throw new InvalidDataException("Promoted output master count is out of bounds.");
        return element.EnumerateArray().Select(item => new PluginName(
            item.GetString() ?? throw new InvalidDataException(
                "Promoted output master order contains a null value.")))
            .ToImmutableArray();
    }

    private static ImmutableArray<FormReference> ParseForms(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Promoted output PNAM must be an array.");
        if (element.GetArrayLength() > MaximumPnam)
            throw new InvalidDataException("Promoted output PNAM count is out of bounds.");
        var result = ImmutableArray.CreateBuilder<FormReference>();
        foreach (JsonElement item in element.EnumerateArray())
            if (!FormReference.TryParse(item.GetString() ?? string.Empty,
                    out FormReference reference))
                throw new InvalidDataException("Promoted output PNAM contains an invalid form reference.");
            else result.Add(reference);
        return result.ToImmutable();
    }

    private static void Validate(
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding binding)
    {
        if (binding.SchemaIdentifier !=
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding.SchemaIdentifierValue ||
            !string.Equals(binding.SourceSelectedManifestPath.Value,
                SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath,
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(binding.SourceSelectedManifestSha256.Value) ||
            binding.Groups.IsDefaultOrEmpty || binding.OutputPluginByteLength <= 0 ||
            binding.MasterOrder.IsDefaultOrEmpty)
            throw new InvalidDataException("Promoted output binding is incomplete.");
        if (binding.SchemaIdentifier?.Length > 128 ||
            binding.SourceSelectedManifestPath.Value?.Length > 256 ||
            binding.OutputPlugin.Value?.Length > 128 ||
            binding.Groups.Length > MaximumGroups)
            throw new InvalidDataException("Promoted output binding contains oversized fields.");
        var ids = binding.Groups.Select(item => item.DescriptorId.Value).ToArray();
        if (!ids.SequenceEqual(ids.OrderBy(item => item, StringComparer.Ordinal)) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("Promoted output binding groups are not sorted and unique.");
        ImmutableArray<FormReference> aggregate = binding.Groups
            .SelectMany(item => item.DeclaredExternalPnam)
            .ToImmutableArray();
        if (!aggregate.SequenceEqual(binding.DeclaredExternalPnam))
            throw new InvalidDataException(
                "Promoted output PNAM does not equal the ordered group-authorized aggregate.");
        foreach (var group in binding.Groups)
        {
            if (group.OutputPlugin != binding.OutputPlugin ||
                group.OutputPluginSha256 != binding.OutputPluginSha256 ||
                group.OutputPluginByteLength != binding.OutputPluginByteLength ||
                !group.MasterOrder.SequenceEqual(binding.MasterOrder))
                throw new InvalidDataException("Promoted output group fields disagree.");
            if (!group.DeclaredExternalPnam.SequenceEqual(
                    group.DeclaredExternalPnam.Distinct()))
                throw new InvalidDataException("Promoted output PNAM contains duplicates.");
        }
    }

    private static void RequireMembers(JsonElement element, params string[] names)
    {
        var actual = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (!actual.SequenceEqual(names, StringComparer.Ordinal))
            throw new InvalidDataException("Promoted output binding members are not canonical.");
    }

    private static string RequiredString(JsonElement parent, string property) =>
        parent.GetProperty(property).GetString() ?? throw new InvalidDataException(
            $"Promoted output binding '{property}' is null.");

    private static Sha256Hash ParseHash(JsonElement parent, string property)
    {
        string value = RequiredString(parent, property);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"Promoted output binding '{property}' is not SHA-256.");
        return new Sha256Hash(value);
    }

    private static long RequiredPositiveLength(JsonElement parent, string property)
    {
        if (!parent.GetProperty(property).TryGetInt64(out long value) || value <= 0)
            throw new InvalidDataException($"Promoted output binding '{property}' is invalid.");
        return value;
    }
}
