using System.Text.Json;

namespace NpcManager.Infrastructure;

internal static class SkyrimFaceBakeAuthorityDocumentSchema
{
    public static JsonElement Value { get; } = Create();

    private static JsonElement Create()
    {
        using var document = JsonDocument.Parse("""
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"skyrim-face-bake-authority/1",
          "title":"Derived Skyrim face-bake authority (existing wire version 2)",
          "description":"Canonical UTF-8 JSON uses the existing strict loader DTO order, no indentation or BOM, and hashes its exact file bytes. Derivation binds exact JSlot, copied record-plugin order, and winning loose/BSA content. loadedPlugins is the catalog-provider subsequence; recordPlugins preserves full bound record order. Model position SHA-256 is packed little-endian XYZ float bytes; model and carrier topology hashes are the existing reader's exact NiSkinPartition block hash. Carrier mapping first requires equal topology and vertex count, then a unique exact HDPT EditorID/model-shape name or a unique remaining topology match; ambiguity refuses. Model-less records, reader-admitted shaderless dummy selected roots, and independently discovered/exclusion-verified SMP forests are explicit record-only mapped roots. A dummy default/HNAM child has no existing consumable omission authority and refuses before publication. No runtime/visual authority. After emission, explicitly bind this path/SHA in the standalone faceBakeAuthority reference, rehash the execution request's standalone manifest binding, and run ordinary preflight/build. Emission does not rewrite existing inputs or pass their failed gates.",
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","authorityId","loadedPlugins","recordPlugins","assets","catalogConfigs","shapeTriInputs","carrierShapes","recordOnlyMappedHeadParts","optionalUnavailableExtensions"],
          "properties":{
            "schemaVersion":{"const":2},"authorityId":{"type":"string","minLength":1,"maxLength":128},
            "loadedPlugins":{"type":"array","minItems":1,"maxItems":256,"items":{"$ref":"#/$defs/loadedPlugin"}},
            "recordPlugins":{"type":"array","minItems":1,"maxItems":256,"items":{"$ref":"#/$defs/recordPlugin"}},
            "assets":{"type":"array","minItems":1,"maxItems":256,"items":{"$ref":"#/$defs/asset"}},
            "catalogConfigs":{"type":"array","minItems":1,"maxItems":64,"items":{"$ref":"#/$defs/catalog"}},
            "shapeTriInputs":{"type":"array","minItems":1,"maxItems":128,"items":{"$ref":"#/$defs/tri"}},
            "carrierShapes":{"type":"array","minItems":1,"maxItems":128,"items":{"$ref":"#/$defs/carrier"}},
            "recordOnlyMappedHeadParts":{"type":"array","maxItems":128,"items":{"$ref":"#/$defs/recordOnly"}},
            "optionalUnavailableExtensions":{"type":"array","maxItems":256,"items":{"$ref":"#/$defs/unavailable"}}
          },
          "$defs":{
            "order":{"type":"integer","minimum":0,"description":"Contiguous zero-based row order."},
            "sha":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},
            "geometrySha":{"type":"string","pattern":"^(?!0{64}$)[0-9A-Fa-f]{64}$"},
            "plugin":{"type":"string","minLength":5,"maxLength":255,"pattern":"^[^/\\\\:|]+\\.[eE][sS][mMpPlL]$"},
            "path":{"type":"string","minLength":1,"maxLength":1024,"description":"Canonical forward-slash relative path, with no drive, empty, dot or parent components.","pattern":"^(?!/)(?!.*//)(?!.*(?:^|/)\\.{1,2}(?:/|$))[^\\\\:]+(?<!/)$"},
            "id":{"type":"string","minLength":1,"maxLength":128,"pattern":"^[a-z0-9][a-z0-9._-]*$"},
            "form":{"type":"string","pattern":"^[^|/\\\\]+\\|0x[0-9A-Fa-f]{8}$"},
            "name":{"type":"string","minLength":1,"maxLength":256},
            "loadedPlugin":{"type":"object","additionalProperties":false,"required":["order","plugin"],"properties":{"order":{"$ref":"#/$defs/order"},"plugin":{"$ref":"#/$defs/plugin"}}},
            "recordPlugin":{"type":"object","additionalProperties":false,"required":["order","plugin","path","sha256"],"properties":{"order":{"$ref":"#/$defs/order"},"plugin":{"$ref":"#/$defs/plugin"},"path":{"$ref":"#/$defs/path"},"sha256":{"$ref":"#/$defs/sha"}}},
            "asset":{"type":"object","additionalProperties":false,"required":["order","id","providerId","providerKind","providerPath","providerSha256","assetPath","contentLength","contentSha256"],"properties":{
              "order":{"$ref":"#/$defs/order"},"id":{"$ref":"#/$defs/id"},"providerId":{"type":"string","minLength":1,"maxLength":128},"providerKind":{"enum":["loose","bsa"]},"providerPath":{"$ref":"#/$defs/path"},"providerSha256":{"$ref":"#/$defs/sha"},"assetPath":{"$ref":"#/$defs/path"},"contentLength":{"type":"integer","minimum":1,"maximum":268435456},"contentSha256":{"$ref":"#/$defs/sha"}}},
            "catalog":{"type":"object","additionalProperties":false,"required":["order","plugin","assetId"],"description":"References a declared .ini or .slider asset beneath this plugin's meshes/actors/character/FaceGenMorphs directory. Grouped in loadedPlugins order; all assets must be used.","properties":{"order":{"$ref":"#/$defs/order"},"plugin":{"$ref":"#/$defs/plugin"},"assetId":{"$ref":"#/$defs/id"}}},
            "tri":{"type":"object","additionalProperties":false,"required":["order","carrierShapeName","raceAssetId","chargenAssetId","meshAssetId","extendedAssetIds"],"properties":{
              "order":{"$ref":"#/$defs/order"},"carrierShapeName":{"$ref":"#/$defs/name"},"raceAssetId":{"anyOf":[{"$ref":"#/$defs/id"},{"type":"null"}]},"chargenAssetId":{"anyOf":[{"$ref":"#/$defs/id"},{"type":"null"}]},"meshAssetId":{"anyOf":[{"$ref":"#/$defs/id"},{"type":"null"}]},"extendedAssetIds":{"type":"array","maxItems":128,"items":{"$ref":"#/$defs/id"}}}},
            "carrier":{"type":"object","additionalProperties":false,"required":["order","headPart","modelAssetId","modelShapeName","carrierShapeName","expectedModelPositionSha256","expectedModelTopologySha256","expectedCarrierTopologySha256"],"properties":{
              "order":{"$ref":"#/$defs/order"},"headPart":{"$ref":"#/$defs/form"},"modelAssetId":{"$ref":"#/$defs/id"},"modelShapeName":{"$ref":"#/$defs/name"},"carrierShapeName":{"$ref":"#/$defs/name"},"expectedModelPositionSha256":{"$ref":"#/$defs/geometrySha"},"expectedModelTopologySha256":{"$ref":"#/$defs/geometrySha"},"expectedCarrierTopologySha256":{"$ref":"#/$defs/geometrySha"}}},
            "recordOnly":{"type":"object","additionalProperties":false,"required":["order","headPart"],"properties":{"order":{"$ref":"#/$defs/order"},"headPart":{"$ref":"#/$defs/form"}}},
            "unavailable":{"type":"object","additionalProperties":false,"required":["order","assetPath"],"properties":{"order":{"$ref":"#/$defs/order"},"assetPath":{"$ref":"#/$defs/path"}}}
          }
        }
        """);
        return document.RootElement.Clone();
    }
}
