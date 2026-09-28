using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

internal static partial class ProtocolV2ResultSchemaCatalog
{
    private static JsonElement FinishCoreSchema(bool apply)
    {
        JsonNode schema = JsonNode.Parse("""
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "type":"object", "additionalProperties":false,
          "required":["schemaId","schemaVersion","status","validation"],
          "properties":{
            "schemaVersion":{"const":"1"},
            "status":{"enum":["readyForReviewedWrite","noChanges","refused","staticPassRuntimeRequired","staticPassInstallDependencyRequired"]},
            "validation":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/validation"}]}
          },
          "$defs":{
            "validation":{
              "type":"object","additionalProperties":false,"required":["valid","phases","diagnostics"],
              "properties":{
                "valid":{"type":"boolean"},
                "phases":{"type":"array","items":{"type":"object","additionalProperties":false,
                  "required":["name","state","diagnostics"],"properties":{
                    "name":{"type":"string"},"state":{"enum":["reached","skipped"]},
                    "diagnostics":{"type":"array","items":{"$ref":"#/$defs/diagnostic"}}}}},
                "diagnostics":{"type":"array","items":{"$ref":"#/$defs/diagnostic"}}
              }
            },
            "diagnostic":{
              "type":"object","additionalProperties":false,"required":["code","severity","message"],
              "properties":{
                "code":{"type":"string"},"severity":{"enum":["info","warning","error"]},"message":{"type":"string"},
                "recovery":{"type":"object","additionalProperties":false,
                  "required":["action","option","artifactKind","constraint","retryUnchangedSafe"],
                  "properties":{
                    "action":{"enum":["retryUnchanged","chooseFreshOutput","reanalyze","obtainHumanReview","repairEnvironment","correctInput","none"]},
                    "option":{"type":["string","null"]},"artifactKind":{"type":["string","null"]},
                    "constraint":{"type":"string"},"retryUnchangedSafe":{"type":"boolean"},"alternativeCommand":{"type":"string"}
                  }}
              }
            }
          }
        }
        """)!;
        schema["$id"] = apply ? AgentProtocolSchemaIds.FinishApplyResult : AgentProtocolSchemaIds.FinishAnalyzeResult;
        JsonObject properties = schema["properties"]!.AsObject();
        properties["schemaId"] = new JsonObject { ["const"] = schema["$id"]!.GetValue<string>() };
        string outcome = apply ? "applied" : "proposed";
        properties[outcome] = new JsonObject { ["type"] = "boolean" };
        schema["required"]!.AsArray().Add(outcome);
        string[] fields = apply ? ["outputRoot", "archive"] : ["proposalPath", "proposalSha256"];
        foreach (string field in fields)
        {
            properties[field] = JsonNode.Parse("""{"type":["string","null"]}""");
            schema["required"]!.AsArray().Add(field);
        }
        if (!apply) properties["proposalSha256"]!["pattern"] = "^[0-9A-F]{64}$";
        return JsonSerializer.SerializeToElement(schema);
    }
}
