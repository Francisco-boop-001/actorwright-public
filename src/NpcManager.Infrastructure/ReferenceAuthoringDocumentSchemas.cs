using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static class ReferenceAuthoringDocumentSchemas
{
    private static readonly string[] Names = ["intake", "inference-proposal", "reviewed-design", "resource-snapshot", "authoring-proposal", "verified-preset", "verified-npc-handoff"];
    private static readonly string[] Payloads = ["intake", "inferenceProposal", "reviewedDesign", "resourceSnapshot", "authoringProposal", "verifiedPreset", "verifiedNpcHandoff"];
    private static readonly Type[] PayloadTypes = [typeof(ReferencePresetIntake), typeof(LandmarkInferenceProposal), typeof(ReviewedReferencePresetDesign),
        typeof(ReferencePresetResourceSnapshot), typeof(ReferencePresetAuthoringProposal), typeof(VerifiedReferencePreset), typeof(VerifiedReferenceNpcHandoff)];
    private static readonly Lazy<JsonObject> Envelope = new(CreateEnvelope);

    public static ImmutableArray<CommandDocumentSchemaDefinition> For(string command) => command switch
    {
        "preset design-propose" => [Definition(0, "input"), Definition(0, "output"), Definition(1, "output")],
        "preset create-from-reference" => [Definition(0, "input"), Definition(1, "input"), Definition(2, "input"), Definition(3, "input"),
            Definition(4, "output"), Definition(5, "output")],
        "npc create-from-reference" => [Definition(0, "input"), Definition(1, "input"), Definition(2, "input"), Definition(3, "input"),
            Definition(4, "output"), Definition(5, "output"), Definition(6, "output")],
        _ => []
    };

    private static CommandDocumentSchemaDefinition Definition(int kind, string direction)
    {
        JsonObject schema = Envelope.Value.DeepClone().AsObject();
        string id = "reference-authoring." + Names[kind] + ".v1";
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = id;
        schema["title"] = "Reference authoring " + Names[kind] + " session";
        schema["description"] = "Existing numeric-kind session envelope. Exactly one matching payload uses schemaVersion 1; no schema wire member. Object keys are sorted ordinally and JSON is minified for canonical identity, preserving array order and numeric enums. Reads admit either exact raw or canonical SHA-256 and return canonical identity. Duplicate/unknown members and semantic authority mismatches are refused. Intake templates contain placeholders which must be filled before validation. Compact geometry arrays retain their existing base64 binary encodings; their decoded strides and finite values remain codec-validated.";
        if (kind >= 5)
            schema["description"] = schema["description"]!.GetValue<string>() + " This is the format for explicit session-service persistence of the returned verified value. Current CLI transactions do not automatically write this envelope; their JSlot and generic readback evidence are different artifacts.";
        // Retain every exported definition: some $refs point into another payload property.
        var properties = new JsonObject { ["kind"] = new JsonObject { ["const"] = kind } };
        for (int index = 0; index < Payloads.Length; index++)
            properties[Payloads[index]] = new JsonObject { ["type"] = index == kind ? "object" : "null" };
        schema["allOf"] = new JsonArray(new JsonObject
        {
            ["required"] = new JsonArray("kind", Payloads[kind]), ["properties"] = properties
        });
        return new("reference-" + Names[kind] + "-session", direction, id, JsonSerializer.SerializeToElement(schema));
    }

    private static JsonObject CreateEnvelope()
    {
        JsonSerializerOptions options = ReferencePresetSessionService.CreateJsonOptions();
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            for (int index = 0; index < typeInfo.Properties.Count; index++)
            {
                JsonPropertyInfo property = typeInfo.Properties[index];
                if (property.AssociatedParameter is not { HasDefaultValue: true, DefaultValue: null } ||
                    !property.PropertyType.IsGenericType ||
                    property.PropertyType.GetGenericTypeDefinition() != typeof(ImmutableArray<>)) continue;
                // The exporter serializes a reflected null default for `ImmutableArray<T> = default`.
                // Arrays have the same JSON shape without that invalid constructor annotation.
                JsonPropertyInfo replacement = typeInfo.CreateJsonPropertyInfo(
                    property.PropertyType.GetGenericArguments()[0].MakeArrayType(), property.Name);
                replacement.Get = property.Get;
                replacement.Set = property.Set;
                replacement.AttributeProvider = property.AttributeProvider;
                replacement.IsGetNullable = false;
                replacement.IsSetNullable = false;
                replacement.IsRequired = property.IsRequired;
                replacement.NumberHandling = property.NumberHandling;
                typeInfo.Properties[index] = replacement;
            }
        });
        options.TypeInfoResolver = resolver;
        return options.GetJsonSchemaAsNode(typeof(ReferencePresetSessionDocument), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform
        }).AsObject();
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        Type original = (context.PropertyInfo?.AttributeProvider as PropertyInfo)?.PropertyType ?? context.TypeInfo.Type;
        Type type = Nullable.GetUnderlyingType(original) ?? original;
        JsonObject? leaf = null;
        if (type == typeof(WorkspacePath)) leaf = Text(@"^[Kk]:[\\/]");
        else if (type == typeof(Sha256Hash)) leaf = Text("^[0-9A-Fa-f]{64}$");
        else if (type == typeof(AssetPath)) leaf = Text(@"^(?![\\/])(?![A-Za-z]:)[^\u0000]+$");
        else if (type == typeof(PluginName)) leaf = Text(@"^[^/\\:]+\.[Ee][Ss][MmPpLl]$");
        else if (type == typeof(FormId)) leaf = Text("^(?:0[xX])?[0-9A-Fa-f]{1,8}$");
        else if (type == typeof(FormReference)) leaf = Text(@"^[^|/\\:]+\.[Ee][Ss][MmPpLl]\|(?:0[xX])?[0-9A-Fa-f]{1,8}$");
        else if (type == typeof(Vector2) || type == typeof(Vector3))
        {
            int length = type == typeof(Vector2) ? 2 : 3;
            leaf = new() { ["type"] = "array", ["minItems"] = length, ["maxItems"] = length,
                ["items"] = new JsonObject { ["type"] = "number" } };
        }
        else if (type == typeof(ImmutableArray<int>) || type == typeof(ImmutableArray<Vector2>) ||
                 type == typeof(ImmutableArray<Vector3>) || type == typeof(ImmutableArray<SseTriHeadVertexDelta>))
        {
            int stride = type == typeof(ImmutableArray<int>) ? 4 : type == typeof(ImmutableArray<Vector2>) ? 8 :
                type == typeof(ImmutableArray<Vector3>) ? 12 : 16;
            leaf = Text("^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$");
            leaf["contentEncoding"] = "base64";
            leaf["description"] = $"Existing compact little-endian array encoding; decoded byte length must be divisible by {stride}.";
        }
        else if (type.IsEnum)
        {
            var values = new JsonArray(Enum.GetValues(type).Cast<object>().Select(value => (JsonNode?)JsonValue.Create(Convert.ToInt64(value,
                System.Globalization.CultureInfo.InvariantCulture))).ToArray());
            leaf = new() { ["type"] = "integer", ["enum"] = values };
        }
        if (leaf is not null)
        {
            if (Nullable.GetUnderlyingType(original) is not null)
                return new JsonObject { ["anyOf"] = new JsonArray(leaf, new JsonObject { ["type"] = "null" }) };
            return leaf;
        }
        if (PayloadTypes.Contains(type) && schema is JsonObject body && body["properties"] is JsonObject fields)
            fields["schemaVersion"] = new JsonObject { ["const"] = 1 };
        return schema;
    }

    private static JsonObject Text(string pattern) => new() { ["type"] = "string", ["pattern"] = pattern };
}
