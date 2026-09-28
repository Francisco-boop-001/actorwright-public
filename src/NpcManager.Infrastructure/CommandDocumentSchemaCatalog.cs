using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

internal sealed record CommandDocumentSchemaDefinition(
    string Name,
    string Direction,
    string SchemaIdentifier,
    JsonElement JsonSchema);

internal static partial class CommandDocumentSchemaCatalog
{
    private static readonly JsonElement HeadPartRecordProposalSchema = Parse(
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"record-proposal.hdpt.v1",
          "title":"Output-owned Skyrim HDPT proposal",
          "description":"Explicit local allocation. The writer preserves master order and existing record bytes, refuses occupied IDs, and independently reopens the new HDPT. Model and TRI paths are relative to meshes; --private-root copies exact assets supplied by --data-root. A clone preserves all fields except EditorID and optional ValidRaces.",
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","artifactKind","edition","mode","signature","formId","sourceFormId","editorId","name","masterDependencies","allocationStrategy","requestSha256","noUnrelatedRecords","headPart"],
          "properties":{
            "schemaVersion":{"const":"1"},"artifactKind":{"const":"record-proposal"},
            "edition":{"const":"skyrimse"},"mode":{"const":"new"},"signature":{"const":"HDPT"},
            "formId":{"type":"string","pattern":"^0x[0-9A-Fa-f]{8}$"},"sourceFormId":{"type":"null"},
            "editorId":{"type":"string","minLength":1,"maxLength":128},"name":{"type":["string","null"],"maxLength":4096},
            "masterDependencies":{"type":"array","items":{"type":"string"},"uniqueItems":true},
            "allocationStrategy":{"const":"explicit-local-form-id"},"requestSha256":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},
            "noUnrelatedRecords":{"const":true},
            "headPart":{
              "type":"object","additionalProperties":false,
              "required":["model","triRace","triChargen","triDialogue","validRaces","extraParts","flags","partType","cloneFrom"],
              "properties":{
                "model":{"type":["string","null"]},"triRace":{"type":["string","null"]},
                "triChargen":{"type":["string","null"]},"triDialogue":{"type":["string","null"]},
                "validRaces":{"type":["string","null"]},"extraParts":{"type":"array","items":{"type":"string"}},
                "flags":{"type":"integer","minimum":0,"maximum":63},
                "partType":{"enum":["misc","face","eyes","hair","facialHair","scar","eyebrows"]},
                "cloneFrom":{"type":["string","null"]}
              },
              "allOf":[{"if":{"properties":{"cloneFrom":{"type":"null"}}},"then":{"properties":{"model":{"type":"string","minLength":1},"triRace":{"type":"string","minLength":1},"triChargen":{"type":"string","minLength":1},"triDialogue":{"type":"string","minLength":1}}}}]
            }
          }
        }
        """);
    private static readonly JsonElement ReviewedGameIntakeSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npcmanager-reviewed-game-intake:2",
          "title": "Actorwright reviewed game intake schema 2",
          "description": "Canonical persisted reviewed-intake document. Filesystem containment, exact file hashes, count equality, plugin/FormID ordering, path derivation, and the intake fingerprint remain codec-enforced constraints.",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "edition", "isAccepted", "workspaceRoot",
            "dataRoot", "loadOrderPath", "outputRoot", "loadOrderHash",
            "assetIndexFingerprint", "intakeFingerprint", "plugins",
            "bodySidecarCount", "generatedPluginCount",
            "generatedSidecarCount", "bodySidecars", "generatedPlugins",
            "generatedSidecars", "assetProviderCount", "runtimeAuthority",
            "diagnostics"
          ],
          "properties": {
            "schemaVersion": { "const": "2" },
            "edition": { "const": "skyrimse" },
            "isAccepted": { "const": true },
            "workspaceRoot": { "$ref": "#/$defs/workspacePath" },
            "dataRoot": { "$ref": "#/$defs/workspacePath" },
            "loadOrderPath": { "$ref": "#/$defs/workspacePath" },
            "outputRoot": { "$ref": "#/$defs/workspacePath" },
            "loadOrderHash": { "$ref": "#/$defs/sha256" },
            "assetIndexFingerprint": { "$ref": "#/$defs/sha256" },
            "intakeFingerprint": { "$ref": "#/$defs/sha256" },
            "plugins": {
              "type": "array",
              "minItems": 1,
              "maxItems": 512,
              "items": { "$ref": "#/$defs/plugin" },
              "description": "Codec requires unique plugin names and strictly ascending global order indexes."
            },
            "bodySidecarCount": {
              "type": "integer", "minimum": 0, "maximum": 512,
              "description": "Codec requires equality with bodySidecars length."
            },
            "generatedPluginCount": {
              "type": "integer", "minimum": 0, "maximum": 512,
              "description": "Codec requires equality with generatedPlugins length."
            },
            "generatedSidecarCount": {
              "type": "integer", "minimum": 0, "maximum": 16384,
              "description": "Codec requires equality with generatedSidecars length."
            },
            "bodySidecars": {
              "type": "array",
              "maxItems": 512,
              "items": { "$ref": "#/$defs/bodySidecar" }
            },
            "generatedPlugins": {
              "type": "array",
              "maxItems": 512,
              "items": { "$ref": "#/$defs/generatedPlugin" }
            },
            "generatedSidecars": {
              "type": "array",
              "maxItems": 16384,
              "items": { "$ref": "#/$defs/generatedSidecar" }
            },
            "assetProviderCount": { "type": "integer", "minimum": 0 },
            "runtimeAuthority": { "const": false },
            "diagnostics": {
              "type": "array",
              "items": { "$ref": "#/$defs/diagnostic" }
            }
          },
          "$defs": {
            "nonWhitespaceString": {
              "type": "string",
              "minLength": 1,
              "pattern": "\\S"
            },
            "pluginName": {
              "type": "string",
              "pattern": "^(?=.{1,255}$)(?=.*\\S)(?!.*[\\u0000-\\u001F\\u007F-\\u009F/\\\\:]).*\\.(?:[eE][sS][pP]|[eE][sS][mM]|[eE][sS][lL])$"
            },
            "workspacePath": {
              "type": "string",
              "pattern": "^(?=.*\\S)(?!.*\\u0000)(?:[A-Za-z]:[\\\\/]|\\\\\\\\[^\\\\/]+[\\\\/][^\\\\/]+(?:[\\\\/]|$))[\\s\\S]*$",
              "description": "Fully-qualified no-NUL path. Codec additionally canonicalizes and validates K-local containment, ordinary-file/directory status, and exact derived paths."
            },
            "assetPath": {
              "type": "string",
              "pattern": "^(?=.*\\S)(?!.*\\u0000)(?!\\s*[\\\\/])(?!.*:)(?!\\s*\\.{1,2}(?:[\\\\/]|\\s*$))(?!.*[\\\\/]\\.{1,2}(?:[\\\\/]|\\s*$))(?!.*[\\\\/]{2})(?!.*[\\\\/]\\s*$)[\\s\\S]+$",
              "description": "Trim-normalizable relative AssetPath with slash normalization and no empty, dot, dot-dot, rooted, or colon-bearing segment."
            },
            "sha256": {
              "type": "string",
              "pattern": "^\\s*[0-9A-Fa-f]{64}\\s*$"
            },
            "formId": {
              "type": "string",
              "pattern": "^\\s*(?:0[xX])?0*[0-9A-Fa-f]{1,8}\\s*$"
            },
            "plugin": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "plugin", "order", "active", "requested",
                "requiredMaster", "sourceHash", "masters"
              ],
              "properties": {
                "plugin": { "$ref": "#/$defs/pluginName" },
                "order": { "type": "integer", "minimum": 0 },
                "active": { "type": "boolean" },
                "requested": { "type": "boolean" },
                "requiredMaster": { "type": "boolean" },
                "sourceHash": { "$ref": "#/$defs/sha256" },
                "masters": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/pluginName" }
                }
              }
            },
            "bodySidecar": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "plugin", "path", "sourceHash", "canonicalHash", "npcCount"
              ],
              "properties": {
                "plugin": { "$ref": "#/$defs/pluginName" },
                "path": { "$ref": "#/$defs/workspacePath" },
                "sourceHash": { "$ref": "#/$defs/sha256" },
                "canonicalHash": { "$ref": "#/$defs/sha256" },
                "npcCount": { "type": "integer", "minimum": 0 }
              }
            },
            "generatedPlugin": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "plugin", "path", "author", "sha256", "readSucceeded",
                "npcFormIds"
              ],
              "properties": {
                "plugin": { "$ref": "#/$defs/pluginName" },
                "path": { "$ref": "#/$defs/workspacePath" },
                "author": { "$ref": "#/$defs/nonWhitespaceString" },
                "sha256": {
                  "type": "string",
                  "pattern": "^\\s*[0-9A-Fa-f]{64}\\s*$"
                },
                "readSucceeded": { "const": true },
                "npcFormIds": {
                  "type": "array",
                  "uniqueItems": true,
                  "items": { "$ref": "#/$defs/formId" },
                  "description": "Codec additionally requires strictly ascending numeric FormIDs."
                }
              }
            },
            "generatedSidecar": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "originPlugin", "kind", "variant", "relativePath",
                "path", "formId", "size", "sha256"
              ],
              "properties": {
                "originPlugin": { "$ref": "#/$defs/pluginName" },
                "kind": {
                  "type": "string",
                  "enum": [
                    "faceGeom", "faceCustomizationDiffuse",
                    "faceCustomizationNormal",
                    "faceCustomizationSpecular", "faceTint",
                    "faceDiffuse", "faceNormal", "faceDetailNeutral"
                  ]
                },
                "variant": {
                  "type": "string",
                  "enum": ["canonical", "debugSandbox"]
                },
                "relativePath": { "$ref": "#/$defs/assetPath" },
                "path": { "$ref": "#/$defs/workspacePath" },
                "formId": {
                  "anyOf": [
                    { "type": "null" },
                    { "$ref": "#/$defs/formId" }
                  ]
                },
                "size": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 536870912
                },
                "sha256": { "$ref": "#/$defs/sha256" }
              },
              "description": "Codec additionally validates origin-plugin membership, unique relative paths, derived materialized paths, exact size/hash bytes, and canonical kind-specific naming."
            },
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "$ref": "#/$defs/nonWhitespaceString" },
                "severity": {
                  "type": "string",
                  "enum": ["info", "warning"]
                },
                "message": { "$ref": "#/$defs/nonWhitespaceString" }
              }
            }
          }
        }
        """);

    private static readonly JsonElement ActorAssemblyContractSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "npc.actor-assembly-preflight.contract.v1",
          "title": "Actorwright Actor Assembly preflight contract v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "operation", "edition", "packageManifest",
            "baseNpc", "placement", "bodyMorph", "outfitScope"
          ],
          "properties": {
            "schemaVersion": { "const": 1 },
            "operation": { "const": "npc-assembly-preflight" },
            "edition": { "const": "skyrimse" },
            "packageManifest": { "$ref": "#/$defs/boundFile" },
            "baseNpc": { "$ref": "#/$defs/actorIdentity" },
            "placement": { "$ref": "#/$defs/placement" },
            "bodyMorph": { "$ref": "#/$defs/bodyMorph" },
            "outfitScope": { "$ref": "#/$defs/outfitScope" },
            "reviewedCompositePolicy": { "$ref": "#/$defs/evidence" }
          },
          "$defs": {
            "nonEmptyString": {
              "type": "string",
              "minLength": 1,
              "maxLength": 1024
            },
            "sha256": {
              "type": "string",
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "path": {
              "type": "string",
              "minLength": 1,
              "description": "Absolute ordinary K-local path with no dot segments or alternate data stream."
            },
            "formId": {
              "type": "string",
              "pattern": "^0x00[0-9A-Fa-f]{6}$"
            },
            "boundFile": {
              "type": "object",
              "additionalProperties": false,
              "required": ["path", "sha256"],
              "properties": {
                "path": { "$ref": "#/$defs/path" },
                "sha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "actorIdentity": {
              "type": "object",
              "additionalProperties": false,
              "required": ["plugin", "formId"],
              "properties": {
                "plugin": { "$ref": "#/$defs/nonEmptyString" },
                "formId": { "$ref": "#/$defs/formId" }
              }
            },
            "placement": {
              "type": "object",
              "additionalProperties": false,
              "required": ["mode"],
              "properties": {
                "mode": {
                  "type": "string",
                  "enum": ["persistentReference", "questAlias", "dynamicSpawn", "none"]
                },
                "placedReferenceFormId": { "$ref": "#/$defs/formId" }
              }
            },
            "evidence": {
              "type": "object",
              "additionalProperties": false,
              "required": ["status"],
              "properties": {
                "status": {
                  "type": "string",
                  "enum": ["available", "unavailable", "notApplicable"]
                },
                "path": { "$ref": "#/$defs/path" },
                "sha256": { "$ref": "#/$defs/sha256" },
                "reason": { "$ref": "#/$defs/nonEmptyString" }
              },
              "oneOf": [
                {
                  "required": ["status", "path", "sha256"],
                  "properties": { "status": { "const": "available" } },
                  "not": { "required": ["reason"] }
                },
                {
                  "required": ["status", "reason"],
                  "properties": {
                    "status": { "enum": ["unavailable", "notApplicable"] }
                  },
                  "not": {
                    "anyOf": [
                      { "required": ["path"] },
                      { "required": ["sha256"] }
                    ]
                  }
                }
              ]
            },
            "bodyMorph": {
              "type": "object",
              "additionalProperties": false,
              "required": ["owner", "evidence"],
              "properties": {
                "owner": {
                  "type": "string",
                  "enum": ["none", "obody", "bodygen", "bakedBodySlide", "runtimeScript", "reviewedComposite", "unknown"]
                },
                "evidence": { "$ref": "#/$defs/evidence" }
              }
            },
            "outfitScope": {
              "type": "object",
              "additionalProperties": false,
              "required": ["status"],
              "properties": {
                "status": {
                  "type": "string",
                  "enum": ["complete", "unavailable", "notApplicable"]
                },
                "inventory": { "$ref": "#/$defs/boundFile" },
                "reason": { "$ref": "#/$defs/nonEmptyString" }
              },
              "oneOf": [
                {
                  "required": ["status", "inventory"],
                  "properties": { "status": { "const": "complete" } },
                  "not": { "required": ["reason"] }
                },
                {
                  "required": ["status", "reason"],
                  "properties": {
                    "status": { "enum": ["unavailable", "notApplicable"] }
                  },
                  "not": { "required": ["inventory"] }
                }
              ]
            }
          }
        }
        """);

    private static readonly JsonElement ActorAssemblyResultSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "npc.actor-assembly-preflight.result.v1",
          "title": "Actorwright Actor Assembly preflight result v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "artifactKind", "admitted", "outcome",
            "contractSha256", "packageManifestSha256", "baseNpcEvidence",
            "diagnosticTarget", "checks", "noWrite", "runtimeAuthority"
          ],
          "properties": {
            "schemaVersion": { "const": 1 },
            "artifactKind": { "const": "actor-assembly-preflight-result" },
            "admitted": { "const": true },
            "outcome": {
              "type": "string",
              "enum": ["pass", "blocked", "unknown", "notApplicable"]
            },
            "contractSha256": { "$ref": "#/$defs/sha256" },
            "packageManifestSha256": { "$ref": "#/$defs/sha256" },
            "baseNpcEvidence": { "$ref": "#/$defs/baseNpcEvidence" },
            "placedReferenceEvidence": { "$ref": "#/$defs/placedReferenceEvidence" },
            "diagnosticTarget": { "$ref": "#/$defs/nonEmptyString" },
            "checks": {
              "type": "array",
              "maxItems": 4096,
              "items": { "$ref": "#/$defs/check" }
            },
            "noWrite": { "const": true },
            "runtimeAuthority": { "const": false }
          },
          "$defs": {
            "nonEmptyString": {
              "type": "string",
              "minLength": 1,
              "maxLength": 4096
            },
            "sha256": {
              "type": "string",
              "pattern": "^[0-9A-F]{64}$"
            },
            "outcome": {
              "type": "string",
              "enum": ["pass", "blocked", "unknown", "notApplicable"]
            },
            "recordObservation": {
              "type": "object",
              "oneOf": [
                {
                  "additionalProperties": false,
                  "required": ["status", "signature", "formId"],
                  "properties": {
                    "status": { "const": "found" },
                    "signature": { "$ref": "#/$defs/nonEmptyString" },
                    "formId": { "$ref": "#/$defs/nonEmptyString" }
                  }
                },
                {
                  "additionalProperties": false,
                  "required": ["status", "reason"],
                  "properties": {
                    "status": { "enum": ["missing", "unknown"] },
                    "reason": { "$ref": "#/$defs/nonEmptyString" }
                  }
                }
              ]
            },
            "referenceObservation": {
              "type": "object",
              "oneOf": [
                {
                  "additionalProperties": false,
                  "required": ["status", "plugin", "formId"],
                  "properties": {
                    "status": { "const": "found" },
                    "plugin": { "$ref": "#/$defs/nonEmptyString" },
                    "formId": { "$ref": "#/$defs/nonEmptyString" }
                  }
                },
                {
                  "additionalProperties": false,
                  "required": ["status", "reason"],
                  "properties": {
                    "status": { "enum": ["missing", "unknown"] },
                    "reason": { "$ref": "#/$defs/nonEmptyString" }
                  }
                }
              ]
            },
            "baseNpcEvidence": {
              "type": "object",
              "additionalProperties": false,
              "required": ["plugin", "declaredFormId", "typedRecord", "rawRecord", "outcome"],
              "properties": {
                "plugin": { "$ref": "#/$defs/nonEmptyString" },
                "declaredFormId": { "$ref": "#/$defs/nonEmptyString" },
                "typedRecord": { "$ref": "#/$defs/recordObservation" },
                "rawRecord": { "$ref": "#/$defs/recordObservation" },
                "outcome": { "$ref": "#/$defs/outcome" }
              }
            },
            "placedReferenceEvidence": {
              "type": "object",
              "additionalProperties": false,
              "required": ["plugin", "declaredFormId", "typedRecord", "rawRecord", "typedBase", "rawNameBase", "outcome"],
              "properties": {
                "plugin": { "$ref": "#/$defs/nonEmptyString" },
                "declaredFormId": { "$ref": "#/$defs/nonEmptyString" },
                "typedRecord": { "$ref": "#/$defs/recordObservation" },
                "rawRecord": { "$ref": "#/$defs/recordObservation" },
                "typedBase": { "$ref": "#/$defs/referenceObservation" },
                "rawNameBase": { "$ref": "#/$defs/referenceObservation" },
                "outcome": { "$ref": "#/$defs/outcome" }
              }
            },
            "evidence": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind", "value"],
              "properties": {
                "kind": {
                  "type": "string",
                  "enum": ["contract", "manifest", "file", "packageFile", "record", "decision"]
                },
                "value": { "$ref": "#/$defs/nonEmptyString" },
                "sha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "check": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "outcome", "message", "evidence"],
              "properties": {
                "code": { "$ref": "#/$defs/nonEmptyString" },
                "outcome": { "$ref": "#/$defs/outcome" },
                "message": { "$ref": "#/$defs/nonEmptyString" },
                "evidence": {
                  "type": "array",
                  "maxItems": 4096,
                  "items": { "$ref": "#/$defs/evidence" }
                }
              }
            }
          }
        }
        """);

    private static readonly JsonElement ActorAssemblyErrorSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "npc.actor-assembly-preflight.error.v1",
          "title": "Actorwright Actor Assembly preflight error v1",
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"],
          "properties": {
            "schemaVersion": { "const": 1 },
            "artifactKind": { "const": "actor-assembly-preflight-error" },
            "contractAdmitted": { "type": "boolean" },
            "diagnostics": {
              "type": "array",
              "maxItems": 4096,
              "items": { "$ref": "#/$defs/diagnostic" }
            }
          },
          "$defs": {
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "severity": {
                  "type": "string",
                  "enum": ["info", "warning", "error"]
                },
                "message": { "type": "string", "minLength": 1 }
              }
            }
          }
        }
        """);

    private static readonly JsonElement FinishCoreRequestLegacySchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.finish-core.request.v1",
          "title": "Actorwright Finish Core request v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema",
            "source",
            "actor",
            "authorities",
            "followerPolicy",
            "outfitPolicy",
            "inventoryPolicy",
            "sandboxAuthority",
            "output"
          ],
          "properties": {
            "schema": {
              "const": "npc.finish-core.request.v1"
            },
            "source": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "packageRoot",
                "packageManifest",
                "packageManifestSha256",
                "packageTreeSha256",
                "pluginPath",
                "plugin",
                "pluginSha256"
              ],
              "properties": {
                "packageRoot": {
                  "$ref": "#/$defs/path",
                  "description": "Workspace-root-relative path (repository-relative in the standard invocation) to the full source package root, not its Data subdirectory."
                },
                "packageManifest": {
                  "$ref": "#/$defs/path",
                  "description": "Workspace-root-relative path to the verified package manifest; it must be a direct child of packageRoot."
                },
                "packageManifestSha256": { "$ref": "#/$defs/sha256" },
                "packageTreeSha256": {
                  "$ref": "#/$defs/sha256",
                  "description": "SHA-256 of UTF-8 bytes formed from every recursively enumerated file under packageRoot, including packageManifest. For each file, replace '\\' with '/' in its relative path and emit <relativePath>|<byteLength>|<uppercaseFileSha256> followed by LF. Sort rows ordinally by relativePath before concatenation. Reparse-point files are refused."
                },
                "pluginPath": {
                  "$ref": "#/$defs/path",
                  "description": "Workspace-root-relative path to the source plugin; it must resolve inside packageRoot and its final segment must equal plugin."
                },
                "plugin": {
                  "$ref": "#/$defs/plugin",
                  "description": "Bare safe filename for the source plugin, with no directory components."
                },
                "pluginSha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "actor": {
              "type": "object",
              "additionalProperties": false,
              "required": ["editorId", "formId"],
              "properties": {
                "editorId": { "$ref": "#/$defs/editorId" },
                "formId": { "$ref": "#/$defs/formId" }
              }
            },
            "authorities": {
              "type": "object",
              "additionalProperties": false,
              "required": ["bodyRoute", "providers"],
              "properties": {
                "bodyRoute": {
                  "type": "string",
                  "enum": ["Cbbe3Ba", "Cotr", "Ube"]
                },
                "providers": {
                  "type": "array",
                  "minItems": 1,
                  "description": "Provider plugin identities and paths must each be unique, and every provider file must be declared by the source package.",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["plugin", "path", "sha256", "byteLength"],
                    "properties": {
                      "plugin": {
                        "$ref": "#/$defs/plugin",
                        "description": "Bare safe filename for the provider plugin, with no directory components."
                      },
                      "path": {
                        "$ref": "#/$defs/path",
                        "description": "Workspace-root-relative path that must resolve inside source.packageRoot and whose final segment must equal plugin."
                      },
                      "sha256": {
                        "$ref": "#/$defs/sha256",
                        "description": "SHA-256 of the exact provider file bytes."
                      },
                      "byteLength": {
                        "type": "integer",
                        "minimum": 1,
                        "description": "Positive byte length of the exact provider file."
                      }
                    }
                  }
                },
                "actorAssemblySha256": { "$ref": "#/$defs/nullableSha256" },
                "bodyOwnerSha256": { "$ref": "#/$defs/nullableSha256" },
                "protectedAppearanceTreeSha256": { "$ref": "#/$defs/nullableSha256" }
              }
            },
            "followerPolicy": {
              "type": "object",
              "additionalProperties": false,
              "required": ["recruitable", "defensiveOnly", "relationshipRank"],
              "properties": {
                "recruitable": { "type": "boolean" },
                "defensiveOnly": {
                  "const": true,
                  "description": "Must be true. Finish Core authors only the closed zero-offense defensive combat style."
                },
                "potentialFollowerFaction": { "$ref": "#/$defs/nullableFormReference" },
                "currentFollowerFaction": { "$ref": "#/$defs/nullableFormReference" },
                "relationshipRank": { "$ref": "#/$defs/nonEmptyString" }
              }
            },
            "aiPolicy": {
              "type": "object",
              "additionalProperties": false,
              "description": "Optional complete Skyrim NPC AIDT authoring policy. When omitted, the source AIDT bytes are preserved. A recruitable NPC may not remain HelpsNobody.",
              "required": ["aggression", "confidence", "energy", "morality", "assistance"],
              "properties": {
                "aggression": {
                  "type": "string",
                  "enum": ["Unaggressive", "Aggressive", "VeryAggressive", "Frenzied"]
                },
                "confidence": {
                  "type": "string",
                  "enum": ["Cowardly", "Cautious", "Average", "Brave", "Foolhardy"]
                },
                "energy": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 100
                },
                "morality": {
                  "type": "string",
                  "enum": ["AnyCrime", "ViolenceAgainstEnemies", "PropertyCrimeOnly", "NoCrime"]
                },
                "assistance": {
                  "type": "string",
                  "enum": ["HelpsNobody", "HelpsAllies", "HelpsFriendsAndAllies"]
                }
              }
            },
            "outfitRacePolicy": {
              "type": "string", "enum": ["refuse", "clone"], "default": "refuse",
              "description": "Every selected ARMO armature must admit the actor race through its primary or additional race list. clone allocates output-owned ARMA with the race appended, affected ARMO with retargeted armatures, and a private OTFT. Only hash-bound copied providers are read."
            },
            "outfitPolicy": {
              "type": "object",
              "additionalProperties": false,
              "required": ["policy", "armorItems"],
              "properties": {
                "policy": {
                  "type": "string",
                  "enum": ["ExistingOutfit", "PrivateOutfit"]
                },
                "existingOutfit": { "$ref": "#/$defs/nullableFormReference" },
                "armorItems": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/formReference" }
                }
              }
            },
            "inventoryPolicy": {
              "type": "object",
              "additionalProperties": false,
              "required": ["policy", "expectedSourceItems", "desiredItems"],
              "properties": {
                "policy": {
                  "type": "string",
                  "enum": ["PreserveInventory", "ReplaceExactInventory"]
                },
                "expectedSourceItems": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/nonEmptyString" }
                },
                "desiredItems": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/nonEmptyString" }
                }
              }
            },
            "sandboxAuthority": {
              "type": "object",
              "additionalProperties": false,
              "required": ["template", "templateEditorId"],
              "properties": {
                "copiedMaster": { "$ref": "#/$defs/nullablePath" },
                "copiedMasterSha256": { "$ref": "#/$defs/nullableSha256" },
                "template": { "$ref": "#/$defs/formReference" },
                "templateEditorId": { "$ref": "#/$defs/nonEmptyString" },
                "rawRecordDigest": { "$ref": "#/$defs/nullableSha256" }
              }
            },
            "output": {
              "type": "object",
              "additionalProperties": false,
              "required": ["pluginFileName"],
              "properties": {
                "root": { "$ref": "#/$defs/nullablePath" },
                "archive": { "$ref": "#/$defs/nullablePath" },
                "pluginFileName": { "$ref": "#/$defs/nonEmptyString" }
              }
            }
          },
          "$defs": {
            "nonEmptyString": {
              "type": "string",
              "minLength": 1
            },
            "path": {
              "type": "string",
              "minLength": 1,
              "pattern": "^(?!/)(?!.*\\\\)(?!.*(?:^|/)(?:\\.|\\.\\.)(?:/|$)).+$"
            },
            "nullablePath": {
              "type": ["string", "null"],
              "minLength": 1,
              "pattern": "^(?!/)(?!.*\\\\)(?!.*(?:^|/)(?:\\.|\\.\\.)(?:/|$)).+$"
            },
            "sha256": {
              "type": "string",
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "nullableSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "plugin": {
              "type": "string",
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])$"
            },
            "nullablePlugin": {
              "type": ["string", "null"],
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])$"
            },
            "editorId": {
              "type": "string",
              "pattern": "^[A-Za-z][A-Za-z0-9_]{0,63}$"
            },
            "nullableEditorId": {
              "type": ["string", "null"],
              "pattern": "^[A-Za-z][A-Za-z0-9_]{0,63}$"
            },
            "formId": {
              "type": "string",
              "minLength": 1,
              "description": "A hexadecimal FormID accepted by Actorwright; canonical emission uses 0xXXXXXXXX."
            },
            "nullableFormId": {
              "type": ["string", "null"],
              "minLength": 1,
              "description": "A hexadecimal FormID accepted by Actorwright; canonical emission uses 0xXXXXXXXX."
            },
            "formReference": {
              "type": "string",
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])\\|0x[0-9A-F]{8}$"
            },
            "nullableFormReference": {
              "type": ["string", "null"],
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])\\|0x[0-9A-F]{8}$"
            }
          }
        }
        """);

    private static readonly JsonElement FinishCoreProposalLegacySchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.finish-core.proposal.v1",
          "title": "Actorwright Finish Core proposal v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema",
            "status",
            "existingRecordChanges",
            "newRecords",
            "appendedMasters",
            "forbiddenRecordCounts",
            "nextFormId",
            "sourceTes4Flags",
            "masterOrder",
            "packageFiles",
            "runtimeAuthority",
            "request"
          ],
          "properties": {
            "schema": {
              "const": "npc.finish-core.proposal.v1"
            },
            "requestSha256": { "$ref": "#/$defs/nullableSha256" },
            "proposalSha256": { "$ref": "#/$defs/nullableSha256" },
            "status": {
              "type": "string",
              "enum": [
                "ReadyForReviewedWrite",
                "NoChanges",
                "Refused",
                "StaticPassRuntimeRequired"
              ]
            },
            "existingRecordChanges": { "$ref": "#/$defs/stringArray" },
            "newRecords": { "$ref": "#/$defs/stringArray" },
            "appendedMasters": { "$ref": "#/$defs/stringArray" },
            "forbiddenRecordCounts": { "$ref": "#/$defs/stringArray" },
            "nextFormId": {
              "type": "string",
              "minLength": 1,
              "description": "A hexadecimal FormID accepted by Actorwright; canonical emission uses 0xXXXXXXXX."
            },
            "sourceTes4Flags": {
              "type": "integer",
              "minimum": 0,
              "maximum": 4294967295
            },
            "masterOrder": { "$ref": "#/$defs/stringArray" },
            "packageFiles": { "$ref": "#/$defs/stringArray" },
            "runtimeAuthority": { "type": "boolean" },
            "request": {
              "$ref": "urn:actorwright:schema:npc.finish-core.request.v1"
            }
          },
          "$defs": {
            "nullableSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "stringArray": {
              "type": "array",
              "items": {
                "type": "string",
                "minLength": 1
              }
            }
          }
        }
        """);

    private static readonly JsonElement FinishCoreRequestSchema =
        CreateFinishCoreRequestV2Schema(FinishCoreRequestLegacySchema);

    private static readonly JsonElement FinishCoreProposalSchema =
        CreateFinishCoreProposalV2Schema(FinishCoreProposalLegacySchema);

    private static readonly JsonElement FinishCoreRequestExternalSchema =
        CreateFinishCoreRequestV3Schema(FinishCoreRequestSchema);

    private static readonly JsonElement FinishCoreRequestPolicySchema =
        CreateFinishCoreRequestV4Schema(FinishCoreRequestExternalSchema);

    private static readonly JsonElement FinishCoreProposalExternalSchema =
        CreateFinishCoreProposalV3Schema(FinishCoreProposalSchema);

    private static readonly JsonElement FinishCoreProposalPolicySchema =
        CreateFinishCoreProposalV4Schema(FinishCoreProposalSchema);

    private static readonly JsonElement FinishCoreManifestSchema =
        CreateFinishCoreManifestV1Schema();

    private static readonly JsonElement FinishCoreManifestExternalSchema =
        CreateFinishCoreManifestV2Schema(FinishCoreManifestSchema);

    private static readonly JsonElement FinishCoreVerificationSchema =
        CreateFinishCoreVerificationV1Schema();

    private static readonly JsonElement FinishCoreVerificationExternalSchema =
        CreateFinishCoreVerificationV2Schema(FinishCoreVerificationSchema);

    private static readonly JsonElement NpcPatchProposalSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.patch.proposal.v1",
          "title": "Actorwright NPC patch proposal v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion",
            "edition",
            "inputPlugin",
            "outputPlugin",
            "targetFormId",
            "inputSha256",
            "changes",
            "preservedFields"
          ],
          "properties": {
            "schemaVersion": { "const": 1 },
            "aidt": {
              "type": "object", "additionalProperties": false, "minProperties": 1,
              "properties": {
                "aggression": { "enum": ["unaggressive", "aggressive", "veryAggressive", "frenzied"] },
                "confidence": { "enum": ["cowardly", "cautious", "average", "brave", "foolhardy"] },
                "morality": { "enum": ["anyCrime", "violenceAgainstEnemies", "propertyCrimeOnly", "noCrime"] },
                "assistance": { "enum": ["helpsNobody", "helpsAllies", "helpsFriendsAndAllies"] },
                "energy": { "type": "integer", "minimum": 0, "maximum": 255 }
              }
            },
            "edition": {
              "type": "string",
              "enum": ["fallout4", "skyrimse"]
            },
            "inputPlugin": {
              "type": "string",
              "minLength": 1,
              "description": "Exact K-local source plugin path whose bytes are bound by inputSha256."
            },
            "outputPlugin": {
              "type": "string",
              "minLength": 1,
              "description": "Fresh K-local output plugin path."
            },
            "targetFormId": {
              "type": "string",
              "pattern": "^0x[0-9A-Fa-f]{8}$"
            },
            "inputSha256": {
              "type": "string",
              "pattern": "^[0-9A-Fa-f]{64}$",
              "description": "SHA-256 of the exact inputPlugin bytes; bind it with --input-sha256 (aliases --input-sha and --expected-sha256)."
            },
            "changes": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["field", "before", "after"],
                "properties": {
                  "field": { "type": "string", "minLength": 1 },
                  "before": { "type": ["string", "null"] },
                  "after": { "type": ["string", "null"] }
                }
              }
            },
            "preservedFields": {
              "type": "array",
              "items": { "type": "string", "minLength": 4, "maxLength": 4 }
            }
          }
        }
        """);

    private static readonly JsonElement FinishCoreValidationSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.finish-core.validation.v1",
          "title": "Actorwright Finish Core validate-all result v1",
          "type": "object",
          "additionalProperties": false,
          "required": ["schema", "command", "valid", "phases", "diagnostics"],
          "properties": {
            "schema": { "const": "npc.finish-core.validation.v1" },
            "command": {
              "type": "string",
              "enum": ["npc finish analyze", "npc finish apply"]
            },
            "valid": { "type": "boolean" },
            "phases": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "state", "diagnostics"],
                "properties": {
                  "name": { "type": "string", "minLength": 1 },
                  "state": {
                    "type": "string",
                    "enum": ["reached", "skipped"]
                  },
                  "diagnostics": { "$ref": "#/$defs/diagnostics" }
                }
              }
            },
            "diagnostics": { "$ref": "#/$defs/diagnostics" }
          },
          "$defs": {
            "diagnostics": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["code", "severity", "message"],
                "properties": {
                  "code": { "type": "string", "minLength": 1 },
                  "severity": {
                    "type": "string",
                    "enum": ["info", "warning", "error"]
                  },
                  "message": { "type": "string", "minLength": 1 }
                }
              }
            }
          }
        }
        """);

    private static readonly ImmutableDictionary<
        string,
        ImmutableArray<CommandDocumentSchemaDefinition>> SchemasByCommand =
        new Dictionary<string, ImmutableArray<CommandDocumentSchemaDefinition>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["records propose"] = [new("hdpt-record-proposal", "output", "record-proposal.hdpt.v1", HeadPartRecordProposalSchema)],
            ["plugin write"] = [new("hdpt-record-proposal", "input", "record-proposal.hdpt.v1", HeadPartRecordProposalSchema)],
            ["workspace preflight"] =
            [
                new(
                    "reviewed-intake",
                    "output",
                    "npcmanager-reviewed-game-intake/2",
                    ReviewedGameIntakeSchema)
            ],
            ["facegen hair-regions preview"] =
            [
                new(
                    "reviewed-intake",
                    "input",
                    "npcmanager-reviewed-game-intake/2",
                    ReviewedGameIntakeSchema)
            ],
            ["preset inspect"] =
            [
                new(
                    "inspection-current",
                    "output",
                    PresetInspectionSchemas.CurrentDocument,
                    PresetInspectionReceiptSchema(
                        PresetInspectionSchemas.CurrentDocument,
                        2)),
                new(
                    "inspection-legacy-read",
                    "input",
                    PresetInspectionSchemas.LegacyDocument,
                    PresetInspectionReceiptSchema(
                        PresetInspectionSchemas.LegacyDocument,
                        1))
            ],
            ["npc create-from-jslot"] =
            [
                new(
                    "request",
                    "input",
                    RaceMenuNpcExecutionRequestSchemas.Request,
                    NpcCreateFromJslotRequestSchema),
                new(
                    "preflight",
                    "output",
                    NpcBuildPreflightSchemas.Artifact,
                    NpcBuildPreflightArtifactSchema),
                new("face-bake-authority", "output", "skyrim-face-bake-authority/1",
                    SkyrimFaceBakeAuthorityDocumentSchema.Value)
            ],
            ["npc create-from-preset"] =
            [
                new("request", "input", RaceMenuNpcExecutionRequestSchemas.Request,
                    NpcCreateFromJslotRequestSchema)
            ],
            ["npc assembly preflight"] =
            [
                new(
                    "contract",
                    "input",
                    ActorAssemblyPreflightSchemas.ContractSchema,
                    ActorAssemblyContractSchema),
                new(
                    "result",
                    "output",
                    ActorAssemblyPreflightSchemas.ResultSchema,
                    ActorAssemblyResultSchema),
                new(
                    "error",
                    "output",
                    ActorAssemblyPreflightSchemas.ErrorSchema,
                    ActorAssemblyErrorSchema)
            ],
            ["npc finish analyze"] =
            [
                new(
                    "request-legacy",
                    "input",
                    SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                    FinishCoreRequestLegacySchema),
                new(
                    "request",
                    "input",
                    SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                    FinishCoreRequestSchema),
                new(
                    "request-external",
                    "input",
                    SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                    FinishCoreRequestExternalSchema),
                new(
                    "request-policy",
                    "input",
                    SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
                    FinishCoreRequestPolicySchema),
                new(
                    "proposal-legacy",
                    "output",
                    SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
                    FinishCoreProposalLegacySchema),
                new(
                    "proposal",
                    "output",
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    FinishCoreProposalSchema),
                new(
                    "proposal-external",
                    "output",
                    SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                    FinishCoreProposalExternalSchema),
                new(
                    "proposal-policy",
                    "output",
                    SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier,
                    FinishCoreProposalPolicySchema),
                new(
                    "validation",
                    "output",
                    SkyrimNpcFinishCoreValidationResult.SchemaIdentifier,
                    FinishCoreValidationSchema)
            ],
            ["npc finish apply"] =
            [
                new(
                    "request-legacy",
                    "input",
                    SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                    FinishCoreRequestLegacySchema),
                new(
                    "request",
                    "input",
                    SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                    FinishCoreRequestSchema),
                new(
                    "request-external",
                    "input",
                    SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                    FinishCoreRequestExternalSchema),
                new(
                    "request-policy",
                    "input",
                    SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
                    FinishCoreRequestPolicySchema),
                new(
                    "proposal-legacy",
                    "input",
                    SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
                    FinishCoreProposalLegacySchema),
                new(
                    "proposal",
                    "input",
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    FinishCoreProposalSchema),
                new(
                    "proposal-external",
                    "input",
                    SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                    FinishCoreProposalExternalSchema),
                new(
                    "proposal-policy",
                    "input",
                    SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier,
                    FinishCoreProposalPolicySchema),
                new(
                    "validation",
                    "output",
                    SkyrimNpcFinishCoreValidationResult.SchemaIdentifier,
                    FinishCoreValidationSchema)
            ],
            ["npc finish verify"] =
            [
                new(
                    "manifest-legacy",
                    "input",
                    SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                    FinishCoreManifestSchema),
                new(
                    "manifest-external",
                    "input",
                    SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
                    FinishCoreManifestExternalSchema),
                new(
                    "verification-legacy",
                    "output",
                    SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                    FinishCoreVerificationSchema),
                new(
                    "verification-external",
                    "output",
                    SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
                    FinishCoreVerificationExternalSchema)
            ],
            ["npc patch"] =
            [
                new("whole-skin", "input", "npc.whole-skin.request.v1", NpcWholeSkinSchema),
                new("proposal", "output", "npc.patch.proposal.v2", WholeSkinProposalSchema()),
                new(
                    "proposal",
                    "output",
                    NpcMutationProposal.SchemaIdentifier,
                    NpcPatchProposalSchema)
            ],
            ["body patch"] = [new("whole-skin", "input", "npc.whole-skin.request.v1", NpcWholeSkinSchema)],
            ["plugin verify"] = [new("whole-skin", "input", "npc.whole-skin.request.v1", NpcWholeSkinSchema)]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase).AddRange(CreateVoiceDialogueSchemas());

    private static JsonElement NpcCreateFromJslotRequestSchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.create-from-jslot.request.v1",
          "title": "Actorwright npc create-from-jslot request v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "edition", "presetBundle", "providerContext",
            "standaloneAssets", "output", "identity", "traits", "references",
            "stats"
          ],
          "properties": {
            "schemaVersion": {
              "type": "integer",
              "enum": [1, 2, 3]
            },
            "edition": { "const": "skyrimse" },
            "presetBundle": { "$ref": "#/$defs/presetBundle" },
            "providerContext": { "$ref": "#/$defs/providerContext" },
            "standaloneAssets": { "$ref": "#/$defs/standaloneAssets" },
            "wholeSkinAuthority": {
              "$ref": "#/$defs/standaloneAssets",
              "description": "Schema 2 existingNpcTarget only: hash-bound whole-skin authority required for schema 7 body meshes. The existing appearance writer clones private ARMO/ARMA records and preserves the source NPC identity."
            },
            "output": { "$ref": "#/$defs/output" },
            "existingNpcTarget": { "$ref": "#/$defs/existingNpcTarget" },
            "applyBodySlide": {
              "type": "boolean",
              "default": true,
              "description": "Defaults to true when omitted; the loader still performs the runtime authority checks."
            },
            "allowInheritedMeshEmbeddedSkinTextureRoute": {
              "type": "boolean",
              "default": false,
              "description": "Defaults to false when omitted; this opt-in does not replace runtime provider validation."
            },
            "faceGeomSkeletonAuthority": {
              "type": "string",
              "enum": [
                "sourceModelWorldTranslations",
                "identityFaceGenBones"
              ],
              "default": "sourceModelWorldTranslations",
              "description": "Canonical JSON uses enum names; the private loader also retains legacy numeric enum compatibility."
            },
            "identity": { "$ref": "#/$defs/identity" },
            "traits": { "$ref": "#/$defs/traits" },
            "references": { "$ref": "#/$defs/references" },
            "stats": { "$ref": "#/$defs/stats" }
          },
          "allOf": [
            {
              "if": { "required": ["wholeSkinAuthority"] },
              "then": { "properties": { "schemaVersion": { "const": 2 } }, "required": ["existingNpcTarget"] }
            }
          ],
          "oneOf": [
            {
              "properties": {
                "schemaVersion": { "const": 1 }
              },
              "not": {
                "anyOf": [
                  {
                    "properties": {
                      "existingNpcTarget": { "type": "object" }
                    },
                    "required": ["existingNpcTarget"]
                  },
                  {
                    "properties": {
                      "providerContext": {
                        "properties": {
                          "productFixtureBundle": { "type": "object" }
                        },
                        "required": ["productFixtureBundle"]
                      }
                    },
                    "required": ["providerContext"]
                  }
                ]
              }
            },
            {
              "properties": {
                "schemaVersion": { "const": 2 },
                "existingNpcTarget": {
                  "$ref": "#/$defs/existingNpcTarget",
                  "type": "object"
                }
              },
              "required": ["existingNpcTarget"],
              "not": {
                "properties": {
                  "providerContext": {
                    "properties": {
                      "productFixtureBundle": { "type": "object" }
                    },
                    "required": ["productFixtureBundle"]
                  }
                },
                "required": ["providerContext"]
              }
            },
            {
              "properties": {
                "schemaVersion": { "const": 3 }
              },
              "not": {
                "anyOf": [
                  {
                    "properties": {
                      "existingNpcTarget": { "type": "object" }
                    },
                    "required": ["existingNpcTarget"]
                  }
                ]
              }
            }
          ],
          "$defs": {
            "nonEmptyString": {
              "type": "string",
              "minLength": 1,
              "maxLength": 4096
            },
            "path": {
              "type": "string",
              "minLength": 1,
              "maxLength": 4096,
              "description": "Workspace-relative asset path. Workspace containment, reparse points, physical existence, and byte authority remain runtime checks."
            },
            "sha256": {
              "type": "string",
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "plugin": {
              "type": "string",
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])$"
            },
            "formId": {
              "type": "string",
              "pattern": "^0x[0-9A-Fa-f]{8}$",
              "description": "Canonical eight-digit hexadecimal FormID. Plugin-local nonzero and load-order rules remain runtime checks."
            },
            "formReference": {
              "type": "string",
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])\\|0x[0-9A-Fa-f]{8}$"
            },
            "nullableFormReference": {
              "type": ["string", "null"],
              "pattern": "^[^|/\\\\:]+\\.(?:[Ee][Ss][Pp]|[Ee][Ss][Mm]|[Ee][Ss][Ll])\\|0x[0-9A-Fa-f]{8}$"
            },
            "presetBundle": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "manifestPath", "manifestSha256", "presetPath", "presetSha256",
                "faceGeomPath", "faceGeomSha256", "faceTintPath", "faceTintSha256",
                "recordAuthorityPath", "recordAuthoritySha256",
                "runtimeRoutesPath", "runtimeRoutesSha256"
              ],
              "properties": {
                "manifestPath": { "$ref": "#/$defs/path" },
                "manifestSha256": { "$ref": "#/$defs/sha256" },
                "presetPath": { "$ref": "#/$defs/path" },
                "presetSha256": { "$ref": "#/$defs/sha256" },
                "faceGeomPath": { "$ref": "#/$defs/path" },
                "faceGeomSha256": { "$ref": "#/$defs/sha256" },
                "faceTintPath": { "$ref": "#/$defs/path" },
                "faceTintSha256": { "$ref": "#/$defs/sha256" },
                "recordAuthorityPath": { "$ref": "#/$defs/path" },
                "recordAuthoritySha256": { "$ref": "#/$defs/sha256" },
                "runtimeRoutesPath": { "$ref": "#/$defs/path" },
                "runtimeRoutesSha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "productFixtureBundle": {
              "type": "object",
              "additionalProperties": false,
              "required": ["bundleId", "registryManifestSha256"],
              "properties": {
                "bundleId": { "$ref": "#/$defs/nonEmptyString" },
                "registryManifestSha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "providerContext": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "manifestPath": { "$ref": "#/$defs/path" },
                "manifestSha256": { "$ref": "#/$defs/sha256" },
                "templatePlugin": { "$ref": "#/$defs/path" },
                "templateSha256": { "$ref": "#/$defs/sha256" },
                "templateNpcFormId": { "$ref": "#/$defs/formId" },
                "faceGeomCarrier": { "$ref": "#/$defs/path" },
                "faceGeomSha256": { "$ref": "#/$defs/sha256" },
                "faceTintManifest": { "$ref": "#/$defs/path" },
                "faceTintProviderRoot": { "$ref": "#/$defs/path" },
                "dependencyManifest": { "$ref": "#/$defs/path" },
                "productFixtureBundle": {
                  "$ref": "#/$defs/productFixtureBundle"
                }
              },
              "oneOf": [
                {
                  "required": [
                    "manifestPath", "manifestSha256", "templatePlugin",
                    "templateSha256", "templateNpcFormId", "faceGeomCarrier",
                    "faceGeomSha256", "faceTintManifest", "faceTintProviderRoot",
                    "dependencyManifest"
                  ],
                  "not": { "required": ["productFixtureBundle"] }
                },
                {
                  "properties": {
                    "templateNpcFormId": { "$ref": "#/$defs/formId" },
                    "productFixtureBundle": {
                      "$ref": "#/$defs/productFixtureBundle"
                    }
                  },
                  "required": ["templateNpcFormId", "productFixtureBundle"],
                  "not": {
                    "anyOf": [
                      { "required": ["manifestPath"] },
                      { "required": ["manifestSha256"] },
                      { "required": ["templatePlugin"] },
                      { "required": ["templateSha256"] },
                      { "required": ["faceGeomCarrier"] },
                      { "required": ["faceGeomSha256"] },
                      { "required": ["faceTintManifest"] },
                      { "required": ["faceTintProviderRoot"] },
                      { "required": ["dependencyManifest"] }
                    ]
                  }
                }
              ]
            },
            "standaloneAssets": {
              "type": "object",
              "additionalProperties": false,
              "required": ["manifestPath", "manifestSha256"],
              "properties": {
                "manifestPath": { "$ref": "#/$defs/path" },
                "manifestSha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "output": {
              "type": "object",
              "additionalProperties": false,
              "required": ["root", "plugin"],
              "properties": {
                "root": { "$ref": "#/$defs/path" },
                "plugin": { "$ref": "#/$defs/plugin" },
                "pluginType": {
                  "type": "string",
                  "default": "esp",
                  "enum": ["esp", "espfe"],
                  "description": "Newly allocated NPCs only. Default esp. espfe keeps the .esp file name and sets the TES4 light flag at creation without compacting owned FormIDs (0x800..0xFFF)."
                }
              }
            },
            "existingNpcTarget": {
              "type": ["object", "null"],
              "additionalProperties": false,
              "required": ["sourcePlugin", "sourcePluginSha256", "targetFormId"],
              "properties": {
                "sourcePlugin": { "$ref": "#/$defs/path" },
                "sourcePluginSha256": { "$ref": "#/$defs/sha256" },
                "targetFormId": { "$ref": "#/$defs/formId" }
              },
              "description": "Schema version 2 requires a non-null target; null is retained for the existing v1/v3 fixture wire shape."
            },
            "identity": {
              "type": "object",
              "additionalProperties": false,
              "required": ["editorId", "name"],
              "properties": {
                "editorId": { "$ref": "#/$defs/editorId" },
                "name": { "$ref": "#/$defs/npcName" }
              }
            },
            "editorId": {
              "type": "string",
              "pattern": "^[A-Za-z][A-Za-z0-9_]{0,63}$"
            },
            "npcName": {
              "type": "string",
              "minLength": 1,
              "maxLength": 255
            },
            "traits": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "sex", "role", "unique", "essential", "protected",
                "respawns", "autoCalcStats"
              ],
              "properties": {
                "sex": {
                  "type": "string",
                  "enum": ["female", "male"]
                },
                "role": {
                  "type": "string",
                  "enum": [
                    "civilian", "combatant", "follower", "merchant",
                    "static-validation"
                  ]
                },
                "unique": { "type": "boolean" },
                "essential": { "type": "boolean" },
                "protected": { "type": "boolean" },
                "respawns": { "type": "boolean" },
                "autoCalcStats": { "type": "boolean" }
              }
            },
            "references": {
              "type": "object",
              "additionalProperties": false,
              "required": ["race", "voice", "class", "combatStyle"],
              "properties": {
                "race": { "$ref": "#/$defs/formReference" },
                "voice": { "$ref": "#/$defs/formReference" },
                "class": { "$ref": "#/$defs/formReference" },
                "combatStyle": { "$ref": "#/$defs/formReference" },
                "defaultOutfit": {
                  "$ref": "#/$defs/nullableFormReference"
                }
              }
            },
            "stats": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "levelMode", "level", "magickaOffset", "staminaOffset",
                "healthOffset", "calcMinLevel", "calcMaxLevel",
                "speedMultiplier", "dispositionBase", "bleedoutOverride",
                "baseHealth", "baseMagicka", "baseStamina", "height",
                "weight", "farAwayModelDistance"
              ],
              "properties": {
                "levelMode": {
                  "type": "string",
                  "enum": ["fixed", "multiplier"]
                },
                "level": {
                  "type": "number",
                  "minimum": 0
                },
                "magickaOffset": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "staminaOffset": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "healthOffset": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "calcMinLevel": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                },
                "calcMaxLevel": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                },
                "speedMultiplier": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "dispositionBase": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "bleedoutOverride": {
                  "type": "integer",
                  "minimum": -32768,
                  "maximum": 32767
                },
                "baseHealth": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                },
                "baseMagicka": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                },
                "baseStamina": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                },
                "height": { "type": "number" },
                "weight": { "type": "number" },
                "farAwayModelDistance": {
                  "type": "integer",
                  "minimum": 0,
                  "maximum": 65535
                }
              },
              "allOf": [
                {
                  "if": {
                    "properties": { "levelMode": { "const": "fixed" } }
                  },
                  "then": {
                    "properties": {
                      "level": {
                        "type": "integer",
                        "minimum": 0,
                        "maximum": 65535
                      }
                    }
                  }
                },
                {
                  "if": {
                    "properties": { "levelMode": { "const": "multiplier" } }
                  },
                  "then": {
                    "properties": {
                      "level": {
                        "type": "number",
                        "minimum": 0,
                        "maximum": 65.535,
                        "multipleOf": 0.001
                      }
                    }
                  }
                }
              ]
            }
          }
        }
        """);

    private static JsonElement NpcBuildPreflightArtifactSchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:actorwright-npc-build-preflight:1",
          "title": "Actorwright NPC build preflight artifact v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema", "product", "productVersion", "sourceLine",
            "executableSha256", "derivationVersion", "sourceRequest",
            "sourceRequestSha256", "presetSha256", "race", "sex",
            "winningPluginOrder", "authorities", "headParts", "appearance",
            "finalDependencyClosure", "requiredGates", "optionalPreview",
            "plannedOutputs", "readyForBuild", "previewReady",
            "runtimeAuthority"
          ],
          "properties": {
            "schema": { "const": "actorwright-npc-build-preflight/1" },
            "product": { "$ref": "#/$defs/nonEmptyString" },
            "productVersion": { "$ref": "#/$defs/nonEmptyString" },
            "sourceLine": { "$ref": "#/$defs/nonEmptyString" },
            "executableSha256": { "$ref": "#/$defs/nullableSha256" },
            "derivationVersion": { "const": 1 },
            "sourceRequest": { "$ref": "#/$defs/path" },
            "sourceRequestSha256": { "$ref": "#/$defs/sha256" },
            "presetSha256": { "$ref": "#/$defs/sha256" },
            "race": { "$ref": "#/$defs/nonEmptyString" },
            "sex": { "$ref": "#/$defs/nonEmptyString" },
            "winningPluginOrder": {
              "type": "array",
              "minItems": 1,
              "items": { "$ref": "#/$defs/nonEmptyString" }
            },
            "authorities": {
              "type": "array",
              "items": { "$ref": "#/$defs/authority" }
            },
            "headParts": {
              "type": "array",
              "items": { "$ref": "#/$defs/headPart" }
            },
            "appearance": {
              "type": "array",
              "items": { "$ref": "#/$defs/appearanceFact" }
            },
            "finalDependencyClosure": {
              "type": "array",
              "items": { "$ref": "#/$defs/authority" }
            },
            "dependencyClosure": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "path", "status"],
                "properties": {
                  "kind": { "type": "string", "minLength": 1 },
                  "path": { "type": "string", "minLength": 1 },
                  "status": { "enum": ["present", "missing", "nonCanonicalPath"] },
                  "disposition": { "enum": ["platformProvided", "optionalUnavailable"] }
                }
              }
            },
            "requiredGates": {
              "type": "array",
              "items": { "$ref": "#/$defs/gate" }
            },
            "optionalPreview": {
              "type": "array",
              "items": { "$ref": "#/$defs/gate" }
            },
            "plannedOutputs": {
              "type": "array",
              "items": { "$ref": "#/$defs/plannedOutput" }
            },
            "readyForBuild": { "type": "boolean" },
            "previewReady": { "type": "boolean" },
            "runtimeAuthority": { "const": false }
          },
          "$defs": {
            "nonEmptyString": { "type": "string", "minLength": 1 },
            "path": { "type": "string", "minLength": 1 },
            "sha256": {
              "type": "string",
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "nullableSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-Fa-f]{64}$"
            },
            "authority": {
              "type": "object",
              "additionalProperties": false,
              "required": ["role", "origin", "identity", "sha256"],
              "properties": {
                "role": { "$ref": "#/$defs/nonEmptyString" },
                "origin": { "$ref": "#/$defs/nonEmptyString" },
                "identity": { "$ref": "#/$defs/nonEmptyString" },
                "sha256": { "$ref": "#/$defs/sha256" }
              }
            },
            "gate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "required", "passed", "detail"],
              "properties": {
                "id": { "$ref": "#/$defs/nonEmptyString" },
                "required": { "type": "boolean" },
                "passed": { "type": "boolean" },
                "detail": { "$ref": "#/$defs/nonEmptyString" }
              }
            },
            "headPart": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "sourceIndex", "sourceType", "effectiveType", "disposition",
                "provider", "geometryStatus", "vertexLayoutStatus",
                "topologyStatus", "packedNormalStatus"
              ],
              "properties": {
                "sourceIndex": { "type": "integer", "minimum": 0 },
                "sourceType": { "$ref": "#/$defs/nonEmptyString" },
                "effectiveType": { "$ref": "#/$defs/nonEmptyString" },
                "disposition": { "$ref": "#/$defs/nonEmptyString" },
                "provider": { "$ref": "#/$defs/nonEmptyString" },
                "geometryStatus": { "$ref": "#/$defs/nonEmptyString" },
                "vertexLayoutStatus": { "$ref": "#/$defs/nonEmptyString" },
                "topologyStatus": { "$ref": "#/$defs/nonEmptyString" },
                "packedNormalStatus": { "$ref": "#/$defs/nonEmptyString" }
              }
            },
            "appearanceFact": {
              "type": "object",
              "additionalProperties": false,
              "required": ["field", "providerValue", "effectiveValue", "authority"],
              "properties": {
                "field": { "$ref": "#/$defs/nonEmptyString" },
                "providerValue": { "$ref": "#/$defs/nonEmptyString" },
                "effectiveValue": { "$ref": "#/$defs/nonEmptyString" },
                "authority": { "$ref": "#/$defs/nonEmptyString" }
              }
            },
            "plannedOutput": {
              "type": "object",
              "additionalProperties": false,
              "required": ["role", "path"],
              "properties": {
                "role": { "$ref": "#/$defs/nonEmptyString" },
                "path": { "$ref": "#/$defs/path" }
              }
            }
          }
        }
        """);

    private static JsonElement PresetInspectionReceiptSchema(
        string schemaIdentifier,
        int version) => Parse(
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:actorwright-preset-inspection:{{version}}",
          "title": "Actorwright preset inspection receipt v{{version}}",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema", "sourcePath", "sourceSha256", "format", "edition",
            "isValid", "appearance", "diagnostics"
          ],
          "properties": {
            "schema": { "const": "{{schemaIdentifier}}" },
            "sourcePath": { "type": "string", "minLength": 1 },
            "sourceSha256": {
              "type": "string",
              "pattern": "^[0-9A-F]{64}$"
            },
            "format": { "const": "racemenu-jslot" },
            "edition": { "const": "skyrimse" },
            "isValid": { "type": "boolean" },
            "appearance": {{PresetInspectionSchemaFragments.Appearance}},
            "diagnostics": {
              "type": "array",
              "items": { "$ref": "#/$defs/diagnostic" }
            }
          },
          "$defs": {
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "severity": {
                  "type": "string",
                  "enum": ["info", "warning", "error"]
                },
                "message": { "type": "string", "minLength": 1 }
              }
            },
            {{PresetInspectionSchemaFragments.Definitions}}
          }
        }
        """);

    public static ImmutableArray<CommandDocumentSchemaDefinition> For(
        string commandName) =>
        commandName is "npc placement interior analyze" or "npc placement interior apply" or "npc placement interior verify"
            ? SkyrimInteriorPlacementDocumentSchemas.For(commandName)
            :
        commandName is "preset design-propose" or "preset create-from-reference" or "npc create-from-reference"
            ? ReferenceAuthoringDocumentSchemas.For(commandName)
            :
        SchemasByCommand.TryGetValue(commandName, out var schemas)
            ? schemas.Select(AddFinishCoreDocumentRules).ToImmutableArray()
            : [];

    private static CommandDocumentSchemaDefinition AddFinishCoreDocumentRules(
        CommandDocumentSchemaDefinition definition)
    {
        if (!definition.SchemaIdentifier.StartsWith("npc.finish-core.", StringComparison.Ordinal))
            return definition;
        JsonObject schema = JsonNode.Parse(definition.JsonSchema.GetRawText())!.AsObject();
        schema["canonicalization"] = "Editable Finish request, proposal and manifest reads accept SHA-256 of raw UTF-8 bytes or the typed canonical serialization. Canonical JSON has ordinally sorted object keys, unchanged array order, no insignificant whitespace or BOM, and lowercase typed SHA-256 values. Unknown and duplicate members are refused. Internal document bindings use canonical identity. Proposal identity omits proposalSha256; the legacy self-excluding digest remains accepted in addition to raw and full canonical hashes. Embedded proposal identity is independently checked. Binary files, package inventory, receipts and external authority artifacts retain their exact byte bindings.";
        schema["digestRule"] = new JsonObject
        {
            ["source.packageTreeSha256"] = FinishCoreRequestLegacySchema.GetProperty("properties")
                .GetProperty("source").GetProperty("properties").GetProperty("packageTreeSha256")
                .GetProperty("description").GetString(),
            ["manifest.packageTreeSha256"] = "For new outputs begin the SHA-256 input with UTF-8 npc.finish-core.output-tree.v2 followed by NUL, then ordinally sorted forward-slash relative paths. For each included file append UTF-8 relativePath, NUL, raw file bytes, NUL. Exclude the output plugin path (ordinal-ignore-case), paths beginning NPCManager/Evidence/ or Data/NPCManager/Evidence/ (ordinal), README-Finish-Core.txt (ordinal), and the generated root npcmanager-package.json (ordinal). The generated generic manifest is independently reconstructed from the retained NPCManager/finish-core-source-package-manifest.json bytes bound to the request source manifest hash, plus exact final file hashes. The retained member is included in the new output digest, so external verification needs no original source folder. Legacy output manifests retain the unprefixed historical digest including npcmanager-package.json. Ordinary files only; no reparse points or devices.",
            ["manifest.sourcePackageTreeSha256"] = "Ordinary v1 manifest: the unprefixed path-NUL-bytes-NUL digest over the source package, excluding only its plugin path (ordinal-ignore-case). External v2 manifest: the source.packageTreeSha256 inventory digest including packageManifest. These are distinct existing hash domains."
        };
        return definition with { JsonSchema = Parse(schema.ToJsonString()) };
    }

    private static JsonElement CreateFinishCoreRequestV3Schema(
        JsonElement v2)
    {
        JsonObject root = JsonNode.Parse(v2.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.request.v3";
        root["title"] = "Actorwright Finish Core request v3";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        JsonObject authorities = root["properties"]!["authorities"]!.AsObject();
        authorities["properties"]!["externalHeadParts"] =
            ExternalHeadPartAuthoritySchema();
        authorities["required"]!.AsArray().Add("externalHeadParts");
        root["properties"]!["combatPolicy"] = JsonNode.Parse("""
            {
              "type": "object", "additionalProperties": false, "required": ["seedLocalStyle"],
              "description": "Clone the NPC's referenced combat style into an allocated output-owned CSTY and repoint ZNAM. All fields outside the selected profile are preserved. Omission retains legacy defensive behavior.",
              "properties": {
                "seedLocalStyle": {"type": "boolean", "description": "Must be true to resolve a master CSTY from a hash-bound copied master/provider authority."},
                "profile": {"type": "string", "enum": ["defensive", "rangedFirst", "meleeFirst"], "description": "Omitted: exact clone. defensive: halve source offense, defense=max(source,1). rangedFirst/meleeFirst: selected equipment score=max(all six source equipment scores,1)+1. Source inputs and the resulting preference must be finite and representable; all remaining fields are copied."}
              }
            }
            """);
        root["properties"]!["perkPolicy"] = JsonNode.Parse("""
            {
              "type": "array", "description": "Exact ordered NPC perk replacement. Omitted preserves PRKR; [] clears it. Form identities must be unique.",
              "items": {"type": "object", "additionalProperties": false, "required": ["form", "rank"],
                "properties": {
                  "form": {"type": "string", "pattern": "^[^|]+\\|0x[0-9A-Fa-f]{8}$"},
                  "rank": {"type": "integer", "minimum": 1, "maximum": 255}
                }
              }
            }
            """);
        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreRequestV4Schema(JsonElement v3)
    {
        JsonObject root = JsonNode.Parse(v3.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.request.v4";
        root["title"] = "Actorwright Finish Core policy request v4";
        root["properties"]!["schema"]!["const"] = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
        JsonObject authorities = root["properties"]!["authorities"]!.AsObject();
        authorities["properties"]!.AsObject().Remove("externalHeadParts");
        JsonArray required = authorities["required"]!.AsArray();
        required.Remove(required.Single(node => node?.GetValue<string>() == "externalHeadParts"));
        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreProposalV3Schema(
        JsonElement v2)
    {
        JsonObject root = JsonNode.Parse(v2.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.proposal.v3";
        root["title"] = "Actorwright Finish Core proposal v3";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier;
        root["properties"]!["runtimeAuthority"] = new JsonObject
        {
            ["const"] = false
        };
        root["properties"]!["request"]!["$ref"] =
            "urn:actorwright:schema:npc.finish-core.request.v3";
        root["allOf"] = JsonNode.Parse("""
            [
              {"if": {"properties": {"request": {"properties": {"authorities": {"required": ["externalHeadParts"]}}}}}, "then": {"required": ["externalHeadParts"]}},
              {"if": {"required": ["externalHeadParts"]}, "then": {"properties": {"request": {"properties": {"authorities": {"required": ["externalHeadParts"]}}}}}}
            ]
            """);
        root["properties"]!["status"]!["enum"]!.AsArray().Add(
            "StaticPassInstallDependencyRequired");
        root["properties"]!["externalHeadParts"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(
                "selectedManifestPath", "selectedManifestSha256", "bindings",
                "verification"),
            ["properties"] = new JsonObject
            {
                ["selectedManifestPath"] = new JsonObject
                {
                    ["const"] = SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath
                },
                ["selectedManifestSha256"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9A-Fa-f]{64}$"
                },
                ["bindings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = ExternalHeadPartBindingSchema()
                },
                ["verification"] = new JsonObject
                {
                    ["$ref"] = "#/$defs/externalInstallVerification"
                },
                ["contextFingerprint"] = new JsonObject
                {
                    ["$ref"] = "#/$defs/externalContextFingerprint"
                }
            }
        };
        JsonObject definitions = root["$defs"]?.AsObject() ?? new JsonObject();
        definitions["externalInstallVerification"] =
            ExternalInstallVerificationSchema(requireHistoricalSnapshot: false);
        definitions["externalContextFingerprint"] =
            ExternalContextFingerprintSchema(allowNull: true);
        definitions["externalInstallSnapshot"] = ExternalInstallSnapshotSchema();
        root["$defs"] = definitions;
        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreProposalV4Schema(JsonElement v2)
    {
        JsonObject root = JsonNode.Parse(v2.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.proposal.v4";
        root["title"] = "Actorwright Finish Core policy proposal v4";
        root["properties"]!["schema"]!["const"] = SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier;
        root["properties"]!["request"]!["$ref"] = "urn:actorwright:schema:npc.finish-core.request.v4";
        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreManifestV1Schema() => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.finish-core.manifest.v1",
          "title": "Actorwright Finish Core manifest v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema", "plugin", "pluginSha256", "baseNpc", "requestSha256",
            "proposalSha256", "placementIncluded", "runtimeAuthority",
            "visualAuthority", "packageRoot", "archive", "archiveSha256",
            "sourcePackageTreeSha256", "packageTreeSha256", "runtimeIdentity",
            "evidence"
          ],
          "properties": {
            "schema": { "const": "npc.finish-core.manifest.v1" },
            "plugin": { "type": ["string", "null"] },
            "pluginSha256": { "type": ["string", "null"] },
            "baseNpc": { "type": ["string", "null"] },
            "requestSha256": { "type": ["string", "null"] },
            "proposalSha256": { "type": ["string", "null"] },
            "placementIncluded": { "type": "boolean" },
            "runtimeAuthority": { "type": "boolean" },
            "visualAuthority": { "type": "boolean" },
            "packageRoot": { "type": ["string", "null"] },
            "archive": { "type": ["string", "null"] },
            "archiveSha256": { "type": ["string", "null"] },
            "sourcePackageTreeSha256": { "type": ["string", "null"] },
            "packageTreeSha256": { "type": ["string", "null"] },
            "runtimeIdentity": { "type": "object" },
            "evidence": { "type": "object" }
          }
        }
        """);

    private static JsonElement CreateFinishCoreManifestV2Schema(
        JsonElement v1)
    {
        JsonObject root = JsonNode.Parse(v1.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.manifest.v2";
        root["title"] = "Actorwright Finish Core manifest v2";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier;
        root["properties"]!["runtimeAuthority"] = new JsonObject
        {
            ["const"] = false
        };
        root["properties"]!["visualAuthority"] = new JsonObject
        {
            ["const"] = false
        };
        root["required"]!.AsArray().Add("externalHeadParts");
        root["properties"]!["externalHeadParts"] = ExternalManifestAuthoritySchema();
        JsonObject definitions = root["$defs"]?.AsObject() ?? new JsonObject();
        definitions["externalInstallSnapshot"] = ExternalInstallSnapshotSchema();
        definitions["externalContextFingerprint"] =
            ExternalContextFingerprintSchema();
        root["$defs"] = definitions;
        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreVerificationV1Schema() => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:schema:npc.finish-core.verification.v1",
          "title": "Actorwright Finish Core verification v1",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schema", "status", "verified", "placementIncluded",
            "runtimeAuthority", "visualAuthority", "pluginSha256",
            "packageTreeSha256", "sourcePackageTreeSha256", "archiveSha256",
            "typedForbiddenCounts", "rawForbiddenCounts", "runtimeIdentity"
          ],
          "properties": {
            "schema": { "const": "npc.finish-core.verification.v1" },
            "status": { "type": "string" },
            "verified": { "type": "boolean" },
            "placementIncluded": { "type": "boolean" },
            "runtimeAuthority": { "type": "boolean" },
            "visualAuthority": { "type": "boolean" },
            "pluginSha256": { "type": ["string", "null"] },
            "packageTreeSha256": { "type": ["string", "null"] },
            "sourcePackageTreeSha256": { "type": ["string", "null"] },
            "archiveSha256": { "type": ["string", "null"] },
            "typedForbiddenCounts": { "type": "object" },
            "rawForbiddenCounts": { "type": "object" },
            "runtimeIdentity": { "type": "object" }
          }
        }
        """);

    private static JsonElement CreateFinishCoreVerificationV2Schema(
        JsonElement v1)
    {
        JsonObject root = JsonNode.Parse(v1.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.verification.v2";
        root["title"] = "Actorwright Finish Core verification v2";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier;
        root["required"]!.AsArray().Add("externalHeadParts");
        root["properties"]!["externalHeadParts"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("verification"),
            ["properties"] = new JsonObject
            {
                ["verification"] = new JsonObject
                {
                    ["$ref"] = "#/$defs/externalInstallVerification"
                }
            }
        };
        JsonObject definitions = root["$defs"]?.AsObject() ?? new JsonObject();
        definitions["externalInstallVerification"] =
            ExternalInstallVerificationSchema(requireHistoricalSnapshot: true);
        definitions["externalInstallSnapshot"] = ExternalInstallSnapshotSchema();
        definitions["externalContextFingerprint"] =
            ExternalContextFingerprintSchema();
        root["$defs"] = definitions;
        return Parse(root.ToJsonString());
    }

    private static JsonObject ExternalHeadPartAuthoritySchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "selectedManifestPath", "selectedManifestSha256", "bindings"),
        ["properties"] = new JsonObject
        {
            ["selectedManifestPath"] = new JsonObject
            {
                ["const"] = SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath
            },
            ["selectedManifestSha256"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["bindings"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = ExternalHeadPartBindingSchema()
            }
        }
    };

    private static JsonObject ExternalHeadPartBindingSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "descriptorId", "faceGeomExclusionAttestationSha256"),
        ["properties"] = new JsonObject
        {
            ["descriptorId"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["faceGeomExclusionAttestationSha256"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            }
        }
    };

    private static JsonObject ExternalManifestAuthoritySchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "selectedManifestPath", "selectedManifestSha256", "descriptors",
            "attestations", "verifiedInstallSnapshot"),
        ["properties"] = new JsonObject
        {
            ["selectedManifestPath"] = new JsonObject
            {
                ["const"] = SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath
            },
            ["selectedManifestSha256"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["descriptors"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JsonObject { ["type"] = "object" }
            },
            ["attestations"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JsonObject { ["type"] = "object" }
            },
            ["verifiedInstallSnapshot"] = new JsonObject
            {
                ["$ref"] = "#/$defs/externalInstallSnapshot"
            }
        }
    };

    private static JsonObject ExternalInstallVerificationSchema(
        bool requireHistoricalSnapshot) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = InstallVerificationRequiredMembers(requireHistoricalSnapshot),
        ["properties"] = new JsonObject
        {
            ["schemaIdentifier"] = new JsonObject
            {
                ["const"] = ExternalHeadPartSchemaIdentifiers.InstallVerification
            },
            ["packageIntegrity"] = new JsonObject { ["type"] = "boolean" },
            ["descriptorClosureValid"] = new JsonObject { ["type"] = "boolean" },
            ["historicalSnapshotValid"] = new JsonObject { ["type"] = "boolean" },
            ["descriptorIds"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = Sha256Schema()
            },
            ["verifiedInstallSnapshot"] = new JsonObject
            {
                ["$ref"] = "#/$defs/externalInstallSnapshot"
            },
            ["currentInstallDependencyState"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    "not-required", "declared-unverified", "verified")
            },
            ["installReady"] = new JsonObject { ["type"] = "boolean" },
            ["installDependencyAuthority"] = new JsonObject { ["type"] = "boolean" },
            ["runtimeAuthority"] = new JsonObject { ["type"] = "boolean" },
            ["visualAuthority"] = new JsonObject { ["type"] = "boolean" },
            ["providerObservations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = ExternalProviderObservationSchema()
            },
            ["missingPrerequisites"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = ExternalPrerequisiteSchema()
            }
        }
    };

    private static JsonArray InstallVerificationRequiredMembers(
        bool requireHistoricalSnapshot)
    {
        var required = new JsonArray(
            "schemaIdentifier", "packageIntegrity", "descriptorClosureValid",
            "descriptorIds", "currentInstallDependencyState", "installReady",
            "installDependencyAuthority", "runtimeAuthority", "visualAuthority",
            "providerObservations", "missingPrerequisites");
        if (requireHistoricalSnapshot)
        {
            required.Add("historicalSnapshotValid");
            required.Add("verifiedInstallSnapshot");
        }
        return required;
    }

    private static JsonObject ExternalInstallSnapshotSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "selectedManifestSha256", "descriptorIds", "contextFingerprint"),
        ["properties"] = new JsonObject
        {
            ["selectedManifestSha256"] = Sha256Schema(),
            ["descriptorIds"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = Sha256Schema()
            },
            ["contextFingerprint"] = ExternalContextFingerprintSchema()
        }
    };

    private static JsonObject ExternalContextFingerprintSchema(bool allowNull = false) => new()
    {
        ["type"] = allowNull ? new JsonArray("object", "null") : "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("sha256", "observations"),
        ["properties"] = new JsonObject
        {
            ["sha256"] = Sha256Schema(),
            ["observations"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray(
                        "kind", "portableIdentity", "sha256", "byteLength", "order"),
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1
                        },
                        ["portableIdentity"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1
                        },
                        ["sha256"] = Sha256Schema(),
                        ["byteLength"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0
                        },
                        ["order"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0
                        }
                    }
                }
            }
        }
    };

    private static JsonObject ExternalProviderObservationSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("providerPlugin", "expectedSha256"),
        ["properties"] = new JsonObject
        {
            ["providerPlugin"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1
            },
            ["expectedSha256"] = Sha256Schema(),
            ["currentSha256"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["enabled"] = new JsonObject
            {
                ["type"] = new JsonArray("boolean", "null")
            }
        }
    };

    private static JsonObject ExternalPrerequisiteSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(
            "diagnosticCode", "portableIdentity", "nextAction"),
        ["properties"] = new JsonObject
        {
            ["diagnosticCode"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1
            },
            ["portableIdentity"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1
            },
            ["expectedSha256"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["currentSha256"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["pattern"] = "^[0-9A-Fa-f]{64}$"
            },
            ["enabled"] = new JsonObject
            {
                ["type"] = new JsonArray("boolean", "null")
            },
            ["nextAction"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1
            }
        }
    };

    private static JsonObject Sha256Schema() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^[0-9A-Fa-f]{64}$"
    };

    private static JsonElement CreateFinishCoreRequestV2Schema(
        JsonElement legacy)
    {
        JsonObject root = JsonNode.Parse(legacy.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.request.v2";
        root["title"] = "Actorwright Finish Core request v2";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreRequest.SchemaIdentifier;

        JsonArray required = root["required"]!.AsArray();
        if (!required.Any(item => item?.GetValue<string>() == "aiPolicy"))
            required.Add("aiPolicy");

        JsonObject authorities = root["properties"]!["authorities"]!.AsObject();
        JsonArray authorityRequired = authorities["required"]!.AsArray();
        authorityRequired.Add("additionalMasters");
        authorities["properties"]!["additionalMasters"] =
            new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray(
                        "plugin", "path", "sha256", "byteLength", "loadOrderIndex"),
                    ["properties"] = new JsonObject
                    {
                        ["plugin"] = new JsonObject
                        {
                            ["$ref"] = "#/$defs/plugin"
                        },
                        ["path"] = new JsonObject
                        {
                            ["$ref"] = "#/$defs/path"
                        },
                        ["sha256"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["pattern"] = "^[0-9A-F]{64}$"
                        },
                        ["byteLength"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1
                        },
                        ["loadOrderIndex"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0
                        }
                    }
                }
            };

        JsonObject aiPolicy = root["properties"]!["aiPolicy"]!.AsObject();
        aiPolicy["description"] =
            "Required complete Skyrim NPC AIDT authoring policy for v2; mood is one of the eight admitted values.";
        JsonArray aiRequired = aiPolicy["required"]!.AsArray();
        aiRequired.Add("mood");
        aiPolicy["properties"]!["mood"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(
                "Neutral", "Angry", "Fear", "Happy", "Sad", "Surprise",
                "Puzzled", "Disgusted")
        };

        return Parse(root.ToJsonString());
    }

    private static JsonElement CreateFinishCoreProposalV2Schema(
        JsonElement legacy)
    {
        JsonObject root = JsonNode.Parse(legacy.GetRawText())!.AsObject();
        root["$id"] = "urn:actorwright:schema:npc.finish-core.proposal.v2";
        root["title"] = "Actorwright Finish Core proposal v2";
        root["properties"]!["schema"]!["const"] =
            SkyrimNpcFinishCoreProposal.SchemaIdentifier;
        root["properties"]!["request"]!["$ref"] =
            "urn:actorwright:schema:npc.finish-core.request.v2";
        return Parse(root.ToJsonString());
    }

    private static JsonElement Parse(string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
