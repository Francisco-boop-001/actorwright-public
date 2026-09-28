using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

internal static class SkyrimInteriorPlacementDocumentSchemas
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonElement Request = Create(typeof(SkyrimInteriorPlacementRequest), SkyrimInteriorPlacementRequest.SchemaIdentifier);
    private static readonly JsonElement Proposal = Create(typeof(SkyrimInteriorPlacementProposal), SkyrimInteriorPlacementProposal.SchemaIdentifier);
    private static readonly JsonElement Manifest = Create(typeof(SkyrimInteriorPlacementManifest), SkyrimInteriorPlacementManifest.SchemaIdentifier);
    private static readonly JsonElement Verification = Create(typeof(SkyrimInteriorPlacementVerification), SkyrimInteriorPlacementVerification.SchemaIdentifier);

    public static ImmutableArray<CommandDocumentSchemaDefinition> For(string command) => command switch
    {
        "npc placement interior analyze" => [new("placement-request", "input", SkyrimInteriorPlacementRequest.SchemaIdentifier, Request),
            new("placement-proposal", "output", SkyrimInteriorPlacementProposal.SchemaIdentifier, Proposal)],
        "npc placement interior apply" => [new("placement-request", "input", SkyrimInteriorPlacementRequest.SchemaIdentifier, Request),
            new("placement-proposal", "input", SkyrimInteriorPlacementProposal.SchemaIdentifier, Proposal),
            new("placement-manifest", "output", SkyrimInteriorPlacementManifest.SchemaIdentifier, Manifest)],
        "npc placement interior verify" => [new("placement-manifest", "input", SkyrimInteriorPlacementManifest.SchemaIdentifier, Manifest),
            new("placement-verification", "output", SkyrimInteriorPlacementVerification.SchemaIdentifier, Verification)],
        _ => []
    };

    private static JsonElement Create(Type type, string id)
    {
        JsonObject schema = Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform
        }).AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = id;
        schema["description"] = "Optional separate static placement patch. Omitted placementMode preserves CELL/ACHR mode. Quest-alias binds an exact Finish Core NPC and its sole owned editor Sandbox PACK, plus a winning persistent marker; emits one script-free start-game QUST/two aliases, one PACK override and SEQ, with zero world records. Later core NPC/PACK overrides refuse. Runtime, visual and pathing authority remain false. Proposal authority hash excludes only proposalSha256; file hashes remain separate.";
        JsonObject properties = schema["properties"]!.AsObject();
        properties["schema"] = new JsonObject { ["const"] = id };
        properties["placementMode"] = new JsonObject { ["const"] = SkyrimQuestAliasPlacementEvidence.Mode };
        bool request = type == typeof(SkyrimInteriorPlacementRequest);
        string[] questFields = request ? ["placementMode", "marker", "sandboxRadius"] : ["placementMode", "questAlias"];
        string[] legacyFields = request ? ["cell", "transform", "location"] : [];
        schema["required"] = Names(properties.Select(x => x.Key).Except(questFields).Except(legacyFields));
        var then = new JsonObject { ["required"] = Names(questFields) };
        if (request) then["not"] = new JsonObject { ["anyOf"] = new JsonArray(legacyFields.Select(field => (JsonNode)new JsonObject { ["required"] = new JsonArray(field) }).ToArray()) };
        schema["allOf"] = new JsonArray(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("placementMode") },
            ["then"] = then,
            ["else"] = new JsonObject
            {
                ["required"] = Names(legacyFields),
                ["not"] = new JsonObject { ["anyOf"] = new JsonArray(questFields.Skip(1).Select(field => (JsonNode)new JsonObject { ["required"] = new JsonArray(field) }).ToArray()) }
            }
        });
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject value) return schema;
        if (value["properties"] is JsonObject properties)
        {
            value["additionalProperties"] = false;
            value["required"] = Names(properties.Select(x => x.Key));
            if (context.TypeInfo.Type == typeof(SkyrimQuestAliasPlacementEvidence))
            {
                value["type"] = "object";
                properties["questCount"] = new JsonObject { ["const"] = 1 };
                properties["aliasCount"] = new JsonObject { ["const"] = 2 };
                properties["packageCount"] = new JsonObject { ["const"] = 1 };
                properties["startGameEnabled"] = new JsonObject { ["const"] = true };
            }
        }
        string? name = context.PropertyInfo?.Name;
        if (name?.EndsWith("Sha256", StringComparison.Ordinal) == true)
            value["pattern"] = "^[0-9A-Fa-f]{64}$";
        if (name is "marker" or "sandboxPackage" or "quest")
        {
            value["type"] = "string";
            value["pattern"] = @"^[^|/\\:]+\.[Ee][Ss][MmPpLl]\|0x[0-9A-F]{8}$";
        }
        if (name == "sandboxRadius")
        {
            value["type"] = "integer";
            value["minimum"] = 1;
            value["maximum"] = uint.MaxValue;
        }
        return value;
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
}
