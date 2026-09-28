using System.Text.Json;
using System.Text.Json.Nodes;

namespace NpcManager.Infrastructure;

internal static partial class CommandDocumentSchemaCatalog
{
    private static JsonElement NpcWholeSkinSchema => Parse("""
    {
      "$schema":"https://json-schema.org/draft/2020-12/schema","$id":"npc.whole-skin.request.v1",
      "title":"Female Skyrim SE whole-skin patch authority",
      "description":"Exact UTF-8 bytes are bound by --whole-skin-sha256. Providers are ordered with input plugin last. The source must already be female and its race must be preserved. With headPolicy preserve, head must be absent, preservedAssets binds accepted FaceGeom/FaceTint/hair/TRI files, exactly six body/hands TXST, body/hands/feet ARMA and skin ARMO records are allocated, and WNAM is the only NPC change. Without headPolicy, legacy replacement requires head and also allocates private head TXST/HDPT (eight records total). Feet reuse body textures. Schema-7 existingNpcTarget remains a separate full-appearance/body-mesh route and is not a head-preservation claim.",
      "type":"object","additionalProperties":false,"required":["schemaVersion","dataRoot","pluginAuthorities","body","hands"],
      "oneOf":[
        {"required":["head"],"not":{"anyOf":[{"required":["headPolicy"]},{"required":["preservedAssets"]}]}},
        {"required":["headPolicy","preservedAssets"],"not":{"required":["head"]}}
      ],
      "properties":{
        "schemaVersion":{"const":1},"dataRoot":{"type":"string","minLength":1},
        "pluginAuthorities":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","additionalProperties":false,"required":["plugin","path","sha256"],"properties":{"plugin":{"type":"string","minLength":1},"path":{"type":"string","minLength":1},"sha256":{"$ref":"#/$defs/hash"}}}},
        "headPolicy":{"const":"preserve"},
        "preservedAssets":{"type":"array","minItems":4,"maxItems":256,"items":{"$ref":"#/$defs/preservedAsset"}},
        "head":{"allOf":[{"$ref":"#/$defs/textures"},{"required":["height"]}]},"body":{"$ref":"#/$defs/textures"},"hands":{"$ref":"#/$defs/textures"}
      },
      "$defs":{
        "hash":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},
        "texture":{"type":"object","additionalProperties":false,"required":["path","sha256"],"properties":{"path":{"type":"string","minLength":1,"description":"Canonical Data-relative Textures/*.dds path."},"sha256":{"$ref":"#/$defs/hash"}}},
        "preservedAsset":{"type":"object","additionalProperties":false,"required":["path","sha256"],"properties":{"path":{"type":"string","minLength":1,"description":"Unique canonical Data-relative accepted FaceGeom, FaceTint, hair, or TRI path."},"sha256":{"$ref":"#/$defs/hash"}}},
        "textures":{"type":"object","additionalProperties":false,"required":["diffuse","normalOrGloss","glowOrDetailMap","backlightMaskOrSpecular"],"properties":{
          "diffuse":{"$ref":"#/$defs/texture"},"normalOrGloss":{"$ref":"#/$defs/texture"},"glowOrDetailMap":{"$ref":"#/$defs/texture"},"height":{"$ref":"#/$defs/texture"},"backlightMaskOrSpecular":{"$ref":"#/$defs/texture"},"environmentMaskOrSubsurfaceTint":{"$ref":"#/$defs/texture"},"environment":{"$ref":"#/$defs/texture"},"multilayer":{"$ref":"#/$defs/texture"}}}
      }
    }
    """);

    private static JsonElement WholeSkinProposalSchema()
    {
        var document = JsonNode.Parse(NpcPatchProposalSchema.GetRawText())!.AsObject();
        document["$id"] = "npc.patch.proposal.v2";
        document["properties"]!["schemaVersion"] = new JsonObject { ["const"] = 2 };
        document["required"]!.AsArray().Add("wholeSkin");
        document["properties"]!["wholeSkin"] = JsonNode.Parse("""
        {"type":"object","additionalProperties":false,"required":["request","requestSha256","firstAllocatedLocalFormId","allocatedRecordCount","changedNpcSubrecords","faceGenRebuilt"],"properties":{
          "request":{"$ref":"npc.whole-skin.request.v1"},"requestSha256":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"firstAllocatedLocalFormId":{"type":"string","pattern":"^0x[0-9A-Fa-f]{8}$"},"allocatedRecordCount":{"enum":[6,8]},"changedNpcSubrecords":{"oneOf":[{"const":["WNAM"]},{"const":["WNAM","FTST","PNAM"]}]},"faceGenRebuilt":{"const":false}},
          "oneOf":[
            {"properties":{"request":{"allOf":[{"$ref":"npc.whole-skin.request.v1"},{"required":["headPolicy"]}]},"allocatedRecordCount":{"const":6},"changedNpcSubrecords":{"const":["WNAM"]}}},
            {"properties":{"request":{"allOf":[{"$ref":"npc.whole-skin.request.v1"},{"not":{"required":["headPolicy"]}}]},"allocatedRecordCount":{"const":8},"changedNpcSubrecords":{"const":["WNAM","FTST","PNAM"]}}}
          ]}
        """);
        return JsonSerializer.SerializeToElement(document);
    }
}
