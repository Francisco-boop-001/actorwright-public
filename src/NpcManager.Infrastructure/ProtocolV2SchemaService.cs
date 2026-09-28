using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed record ProtocolResultSchemaDefinition(
    string SchemaIdentifier,
    JsonElement JsonSchema);

internal static partial class ProtocolV2ResultSchemaCatalog
{
    private static readonly JsonElement CapabilitiesSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:capabilities-result:v1",
          "title": "Actorwright protocol v2 capabilities result",
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaId", "protocolVersion", "commands"],
          "properties": {
            "schemaId": {
              "const": "urn:actorwright:protocol-v2:capabilities-result:v1"
            },
            "protocolVersion": { "const": "2" },
            "commands": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "required": ["name", "readiness", "resultSchemaIds"]
              }
            }
          }
        }
        """);

    private static readonly JsonElement VersionSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:version-result:v1",
          "title": "Actorwright protocol v2 version result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaId",
            "productName",
            "productVersion",
            "sourceLine",
            "targetFramework",
            "protocolVersion",
            "supportedProtocolVersions"
          ],
          "properties": {
            "schemaId": {
              "const": "urn:actorwright:protocol-v2:version-result:v1"
            },
            "productName": { "type": "string", "minLength": 1 },
            "productVersion": { "type": "string", "minLength": 1 },
            "sourceLine": { "type": "string", "minLength": 1 },
            "targetFramework": { "type": "string", "minLength": 1 },
            "protocolVersion": { "const": "2" },
            "supportedProtocolVersions": {
              "type": "array",
              "minItems": 1,
              "uniqueItems": true,
              "items": { "type": "string", "minLength": 1 }
            }
          }
        }
        """);

    private static readonly JsonElement SchemaExportSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:schema-export-result:v1",
          "title": "Actorwright protocol v2 schema export result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaId",
            "protocolVersion",
            "scopedHelpResultSchema",
            "pathConventions"
          ],
          "properties": {
            "schemaId": {
              "const": "urn:actorwright:protocol-v2:schema-export-result:v1"
            },
            "protocolVersion": { "const": "2" },
            "scopedHelpResultSchema": { "$ref": "#/$defs/schemaDefinition" },
            "pathConventions": {
              "type": "object",
              "additionalProperties": false,
              "required": ["cli", "documents", "assets"],
              "properties": {
                "cli": { "type": "string" },
                "documents": { "type": "string" },
                "assets": { "type": "string" }
              }
            },
            "commands": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["contract", "resultSchemas", "documentSchemas"],
                "properties": {
                  "contract": { "type": "object" },
                  "resultSchemas": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/schemaDefinition" }
                  },
                  "documentSchemas": {
                    "type": "array",
                    "items": { "type": "object" }
                  }
                }
              }
            },
            "contract": { "type": "object" },
            "resultSchemas": {
              "type": "array",
              "items": { "$ref": "#/$defs/schemaDefinition" }
            },
            "documentSchemas": {
              "type": "array",
              "items": { "type": "object" }
            }
          },
          "oneOf": [
            { "required": ["commands"] },
            { "required": ["contract", "resultSchemas", "documentSchemas"] }
          ],
          "$defs": {
            "schemaDefinition": {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaIdentifier", "jsonSchema"],
              "properties": {
                "schemaIdentifier": { "type": "string", "minLength": 1 },
                "jsonSchema": { "type": "object" }
              }
            }
          }
        }
        """);

    private static readonly JsonElement ScopedHelpSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:scoped-help-result:v1",
          "title": "Actorwright protocol v2 scoped help result",
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaId", "contract"],
          "properties": {
            "schemaId": {
              "const": "urn:actorwright:protocol-v2:scoped-help-result:v1"
            },
            "contract": { "type": "object" }
          }
        }
        """);

    private static readonly JsonElement WorkspacePreflightSchema = Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:workspace-preflight-result:v1",
          "title": "Actorwright protocol v2 reviewed workspace preflight result",
          "type": "object",
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaVersion", "isAccepted"],
              "properties": {
                "schemaVersion": { "const": "2" },
                "isAccepted": { "const": false }
              }
            },
            {
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
                "isAccepted": { "type": "boolean" },
                "workspaceRoot": { "type": "string", "minLength": 1 },
                "dataRoot": { "type": "string", "minLength": 1 },
                "loadOrderPath": { "type": "string", "minLength": 1 },
                "outputRoot": { "type": "string", "minLength": 1 },
                "loadOrderHash": { "$ref": "#/$defs/nullableSha256" },
                "assetIndexFingerprint": { "$ref": "#/$defs/nullableSha256" },
                "intakeFingerprint": { "$ref": "#/$defs/nullableSha256" },
                "plugins": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/plugin" }
                },
                "bodySidecarCount": { "type": "integer", "minimum": 0 },
                "generatedPluginCount": { "type": "integer", "minimum": 0 },
                "generatedSidecarCount": { "type": "integer", "minimum": 0 },
                "bodySidecars": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/bodySidecar" }
                },
                "generatedPlugins": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/generatedPlugin" }
                },
                "generatedSidecars": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/generatedSidecar" }
                },
                "assetProviderCount": { "type": "integer", "minimum": 0 },
                "runtimeAuthority": { "const": false },
                "diagnostics": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/diagnostic" }
                }
              }
            }
          ],
          "$defs": {
            "nullableSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "plugin": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "plugin", "order", "active", "requested",
                "requiredMaster", "sourceHash", "masters"
              ],
              "properties": {
                "plugin": { "type": "string", "minLength": 1 },
                "order": { "type": "integer", "minimum": 0 },
                "active": { "type": "boolean" },
                "requested": { "type": "boolean" },
                "requiredMaster": { "type": "boolean" },
                "sourceHash": { "type": "string" },
                "masters": {
                  "type": "array",
                  "items": { "type": "string", "minLength": 1 }
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
                "plugin": { "type": "string", "minLength": 1 },
                "path": { "type": "string", "minLength": 1 },
                "sourceHash": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "canonicalHash": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
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
                "plugin": { "type": "string", "minLength": 1 },
                "path": { "type": "string", "minLength": 1 },
                "author": { "type": "string" },
                "sha256": { "$ref": "#/$defs/nullableSha256" },
                "readSucceeded": { "type": "boolean" },
                "npcFormIds": {
                  "type": "array",
                  "items": { "type": "string", "minLength": 1 }
                }
              }
            },
            "generatedSidecar": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "originPlugin", "kind", "variant", "relativePath", "path",
                "formId", "size", "sha256"
              ],
              "properties": {
                "originPlugin": { "type": "string", "minLength": 1 },
                "kind": { "type": "string", "minLength": 1 },
                "variant": { "type": "string", "minLength": 1 },
                "relativePath": { "type": "string", "minLength": 1 },
                "path": { "type": "string", "minLength": 1 },
                "formId": { "type": ["string", "null"] },
                "size": { "type": "integer", "minimum": 0 },
                "sha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" }
              }
            },
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "severity": { "type": "integer", "enum": [0, 1, 2] },
                "message": { "type": "string", "minLength": 1 }
              }
            }
          }
        }
        """);

    private static readonly ImmutableDictionary<
        string,
        ProtocolResultSchemaDefinition> SchemasByIdentifier =
        new[]
        {
            Definition(
                AgentProtocolSchemaIds.CapabilitiesResult,
                CapabilitiesSchema),
            Definition(AgentProtocolSchemaIds.VersionResult, VersionSchema),
            Definition(
                AgentProtocolSchemaIds.SchemaExportResult,
                SchemaExportSchema),
            Definition(
                AgentProtocolSchemaIds.WorkspacePreflightResult,
                WorkspacePreflightSchema),
            Definition(
                ActorAssemblyPreflightSchemas.ProtocolResultSchema,
                ActorAssemblyPreflightProtocolResultSchema),
            Definition(
                AgentProtocolSchemaIds.PresetInspectResult,
                PresetInspectSchema),
            Definition(
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                NpcCreatePreflightSchema),
            Definition(
                AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult,
                NpcCreateFromJslotBuildSchema),
            Definition(
                AgentProtocolSchemaIds.NpcVisualPreviewResult,
                NpcVisualPreviewSchema),
            Definition(
                AgentProtocolSchemaIds.FinishVerifyResult,
                FinishVerifySchema),
            Definition(AgentProtocolSchemaIds.FinishAnalyzeResult, FinishCoreSchema(apply: false)),
            Definition(AgentProtocolSchemaIds.FinishApplyResult, FinishCoreSchema(apply: true))
        }.ToImmutableDictionary(
            item => item.SchemaIdentifier,
            StringComparer.Ordinal);

    private static JsonElement NpcVisualPreviewSchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:npc-visual-preview-result:v1",
          "title": "Actorwright protocol v2 NPC visual preview result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "composed", "status", "runtimeAuthority", "bundlePath",
            "bundleSize", "bundleSha256", "hashManifestPath",
            "hashManifestSize", "hashManifestSha256", "contactSheetPath",
            "contactSheetSha256", "views", "diagnostics"
          ],
          "properties": {
            "composed": { "type": "boolean" },
            "status": {
              "type": "string",
              "enum": ["offEnginePreviewRuntimeRequired", "refused"]
            },
            "runtimeAuthority": { "const": false },
            "bundlePath": { "type": ["string", "null"] },
            "bundleSize": { "type": ["integer", "null"], "minimum": 1 },
            "bundleSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "hashManifestPath": { "type": ["string", "null"] },
            "hashManifestSize": { "type": ["integer", "null"], "minimum": 1 },
            "hashManifestSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "contactSheetPath": { "type": ["string", "null"] },
            "contactSheetSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "views": {
              "type": "array",
              "maxItems": 6,
              "items": { "$ref": "#/$defs/view" }
            },
            "diagnostics": {
              "type": "array",
              "items": { "$ref": "#/$defs/diagnostic" }
            }
          },
          "oneOf": [
            {
              "properties": {
                "composed": { "const": false },
                "status": { "const": "refused" },
                "bundlePath": { "type": "null" },
                "bundleSize": { "type": "null" },
                "bundleSha256": { "type": "null" },
                "hashManifestPath": { "type": "null" },
                "hashManifestSize": { "type": "null" },
                "hashManifestSha256": { "type": "null" },
                "contactSheetPath": { "type": "null" },
                "contactSheetSha256": { "type": "null" },
                "views": { "maxItems": 0 }
              }
            },
            {
              "properties": {
                "composed": { "const": true },
                "status": { "const": "offEnginePreviewRuntimeRequired" },
                "bundlePath": { "type": "string", "minLength": 1 },
                "bundleSize": { "type": "integer", "minimum": 1 },
                "bundleSha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "hashManifestPath": { "type": "string", "minLength": 1 },
                "hashManifestSize": { "type": "integer", "minimum": 1 },
                "hashManifestSha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "contactSheetPath": { "type": "string", "minLength": 1 },
                "contactSheetSha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "views": { "minItems": 6, "maxItems": 6 }
              }
            }
          ],
          "$defs": {
            "view": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "id", "imagePath", "imageSha256", "roleMaskPath",
                "roleMaskSha256", "width", "height"
              ],
              "properties": {
                "id": { "type": "string", "minLength": 1 },
                "imagePath": { "type": "string", "minLength": 1 },
                "imageSha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "roleMaskPath": { "type": "string", "minLength": 1 },
                "roleMaskSha256": { "type": "string", "pattern": "^[0-9A-F]{64}$" },
                "width": { "const": 900 },
                "height": { "const": 900 }
              }
            },
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "severity": { "type": "string", "enum": ["info", "warning", "error"] },
                "message": { "type": "string", "minLength": 1 }
              }
            }
          }
        }
        """);

    private static JsonElement FinishVerifySchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:finish-verify-result:v1",
          "title": "Actorwright protocol v2 Finish Verify result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "verified", "status", "manifestPath",
            "manifestSha256", "verificationPath", "verificationSize",
            "verificationSha256", "verification", "diagnostics"
          ],
          "properties": {
            "schemaVersion": { "const": "1" },
            "verified": { "type": "boolean" },
            "status": {
              "type": ["string", "null"],
              "enum": ["staticPassRuntimeRequired", "refused", null]
            },
            "manifestPath": { "type": "string", "minLength": 1 },
            "manifestSha256": {
              "type": "string",
              "pattern": "^[0-9A-F]{64}$"
            },
            "verificationPath": { "type": ["string", "null"] },
            "verificationSize": { "type": ["integer", "null"], "minimum": 1 },
            "verificationSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "verification": {
              "oneOf": [
                { "type": "null" },
                { "$ref": "#/$defs/verification" }
              ]
            },
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
          },
          "oneOf": [
            {
              "properties": {
                "verified": { "const": false },
                "verificationPath": { "type": "null" },
                "verificationSize": { "type": "null" },
                "verificationSha256": { "type": "null" },
                "verification": { "type": "null" }
              }
            },
            {
              "properties": {
                "verified": { "const": true },
                "status": { "const": "staticPassRuntimeRequired" },
                "verificationPath": { "type": "string", "minLength": 1 },
                "verificationSize": { "type": "integer", "minimum": 1 },
                "verificationSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                },
                "verification": { "$ref": "#/$defs/verification" }
              }
            }
          ],
          "$defs": {
            "hash": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value"],
              "properties": {
                "value": { "type": "string", "pattern": "^[0-9a-f]{64}$" }
              }
            },
            "plugin": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value"],
              "properties": { "value": { "type": "string", "minLength": 1 } }
            },
            "formId": {
              "type": "object",
              "additionalProperties": false,
              "required": ["value"],
              "properties": {
                "value": { "type": "integer", "minimum": 0, "maximum": 4294967295 }
              }
            },
            "formReference": {
              "type": "object",
              "additionalProperties": false,
              "required": ["plugin", "formId"],
              "properties": {
                "plugin": { "$ref": "#/$defs/plugin" },
                "formId": { "$ref": "#/$defs/formId" }
              }
            },
            "runtimeIdentity": {
              "type": "object",
              "additionalProperties": false,
              "required": ["baseNpc", "placedReference", "placementIncluded"],
              "properties": {
                "baseNpc": { "$ref": "#/$defs/formReference" },
                "placedReference": {
                  "oneOf": [
                    { "type": "null" },
                    { "$ref": "#/$defs/formReference" }
                  ]
                },
                "placementIncluded": { "type": "boolean" }
              }
            },
            "verification": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "schema", "status", "verified", "placementIncluded",
                "runtimeAuthority", "visualAuthority", "typedForbiddenCounts",
                "rawForbiddenCounts", "pluginSha256", "packageTreeSha256",
                "sourcePackageTreeSha256", "archiveSha256", "runtimeIdentity"
              ],
              "properties": {
                "schema": { "const": "npc.finish-core.verification.v1" },
                "status": { "const": "staticPassRuntimeRequired" },
                "verified": { "const": true },
                "placementIncluded": { "const": false },
                "runtimeAuthority": { "const": false },
                "visualAuthority": { "const": false },
                "typedForbiddenCounts": {
                  "type": "object",
                  "additionalProperties": { "const": 0 }
                },
                "rawForbiddenCounts": {
                  "type": "object",
                  "additionalProperties": { "const": 0 }
                },
                "pluginSha256": { "$ref": "#/$defs/hash" },
                "packageTreeSha256": { "$ref": "#/$defs/hash" },
                "sourcePackageTreeSha256": { "$ref": "#/$defs/hash" },
                "archiveSha256": { "$ref": "#/$defs/hash" },
                "runtimeIdentity": { "$ref": "#/$defs/runtimeIdentity" }
              }
            }
          }
        }
        """);

    public static ProtocolResultSchemaDefinition ScopedHelp { get; } =
        Definition(AgentProtocolSchemaIds.ScopedHelpResult, ScopedHelpSchema);

    public static ImmutableArray<ProtocolResultSchemaDefinition> For(
        AgentCommandContract contract) =>
        contract.ResultSchemaIds.Select(GetRequired).ToImmutableArray();

    private static ProtocolResultSchemaDefinition GetRequired(
        string schemaIdentifier) =>
        SchemasByIdentifier.TryGetValue(schemaIdentifier, out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"No result schema is registered for '{schemaIdentifier}'.");

    private static ProtocolResultSchemaDefinition Definition(
        string schemaIdentifier,
        JsonElement jsonSchema) =>
        new(schemaIdentifier, jsonSchema);

    private static JsonElement Parse(string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    private static JsonElement ActorAssemblyPreflightProtocolResultSchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1",
          "title": "Actorwright protocol v2 Actor Assembly preflight result",
          "oneOf": [
            { "$ref": "#/$defs/success" },
            { "$ref": "#/$defs/error" }
          ],
          "$defs": {
            "success": {
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
                "outcome": { "$ref": "#/$defs/outcome" },
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
              }
            },
            "error": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"
              ],
              "properties": {
                "schemaVersion": { "const": 1 },
                "artifactKind": { "const": "actor-assembly-preflight-error" },
                "contractAdmitted": { "type": "boolean" },
                "diagnostics": {
                  "type": "array",
                  "maxItems": 4096,
                  "items": { "$ref": "#/$defs/diagnostic" }
                }
              }
            },
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
              "oneOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["status", "signature", "formId"],
                  "properties": {
                    "status": { "const": "found" },
                    "signature": { "$ref": "#/$defs/nonEmptyString" },
                    "formId": { "$ref": "#/$defs/nonEmptyString" }
                  }
                },
                {
                  "type": "object",
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
              "oneOf": [
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["status", "plugin", "formId"],
                  "properties": {
                    "status": { "const": "found" },
                    "plugin": { "$ref": "#/$defs/nonEmptyString" },
                    "formId": { "$ref": "#/$defs/nonEmptyString" }
                  }
                },
                {
                  "type": "object",
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
              "required": [
                "plugin", "declaredFormId", "typedRecord", "rawRecord", "outcome"
              ],
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
              "required": [
                "plugin", "declaredFormId", "typedRecord", "rawRecord",
                "typedBase", "rawNameBase", "outcome"
              ],
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
                  "enum": [
                    "contract", "manifest", "file", "packageFile", "record", "decision"
                  ]
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
            },
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

    private static JsonElement PresetInspectSchema => Parse(
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:preset-inspect-result:v1",
          "title": "Actorwright protocol v2 preset inspect result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "schemaVersion", "format", "edition", "sourcePath",
            "sourceSha256", "isValid", "appearance", "diagnostics",
            "inspectionPath", "inspectionSize", "inspectionSha256"
          ],
          "properties": {
            "schemaVersion": { "const": "1" },
            "format": { "const": "racemenu-jslot" },
            "edition": { "const": "skyrimse" },
            "sourcePath": { "type": ["string", "null"] },
            "sourceSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "isValid": { "type": "boolean" },
            "appearance": {
              "oneOf": [
                { "type": "null" },
                {{PresetInspectionSchemaFragments.Appearance}}
              ]
            },
            "diagnostics": {
              "type": "array",
              "items": { "$ref": "#/$defs/diagnostic" }
            },
            "inspectionPath": { "type": ["string", "null"] },
            "inspectionSize": {
              "type": ["integer", "null"],
              "minimum": 1
            },
            "inspectionSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            }
          },
          "oneOf": [
            {
              "properties": {
                "sourcePath": { "type": "null" },
                "sourceSha256": { "type": "null" },
                "isValid": { "const": false },
                "appearance": { "type": "null" },
                "inspectionPath": { "type": "null" },
                "inspectionSize": { "type": "null" },
                "inspectionSha256": { "type": "null" }
              }
            },
            {
              "properties": {
                "sourcePath": { "type": "string", "minLength": 1 },
                "sourceSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                },
                "isValid": { "const": false },
                "appearance": { "type": "null" },
                "inspectionPath": { "type": "null" },
                "inspectionSize": { "type": "null" },
                "inspectionSha256": { "type": "null" }
              }
            },
            {
              "properties": {
                "sourcePath": { "type": "string", "minLength": 1 },
                "sourceSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                },
                "appearance": {{PresetInspectionSchemaFragments.Appearance}},
                "inspectionPath": { "type": "null" },
                "inspectionSize": { "type": "null" },
                "inspectionSha256": { "type": "null" }
              }
            },
            {
              "properties": {
                "sourcePath": { "type": "string", "minLength": 1 },
                "sourceSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                },
                "appearance": {{PresetInspectionSchemaFragments.Appearance}},
                "inspectionPath": { "type": "string", "minLength": 1 },
                "inspectionSize": { "type": "integer", "minimum": 1 },
                "inspectionSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                }
              }
            }
          ],
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

    private static JsonElement NpcCreateFromJslotBuildSchema =>
        WithExternalInstallPrepublication(Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:npc-create-from-jslot-build-result:v1",
          "title": "Actorwright protocol v2 NPC create-from-JSlot build result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "completed", "status", "manifestPath", "manifestSize",
            "manifestSha256", "outputPlugin", "targetFormId",
            "runtimeAuthority", "diagnostics"
          ],
          "properties": {
            "completed": { "type": "boolean" },
            "status": {
              "type": "string",
              "enum": ["staticPassRuntimeRequired", "refused"]
            },
            "manifestPath": { "type": ["string", "null"] },
            "manifestSize": { "type": ["integer", "null"], "minimum": 1 },
            "manifestSha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "outputPlugin": { "type": ["string", "null"] },
            "targetFormId": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{8}$"
            },
            "inheritedDefaults": {
              "type": "array", "uniqueItems": true,
              "description": "Observed equality to the authenticated blank-npc-v1 template, not proof of historical inheritance. Unavailable comparison is explicitly diagnosed.",
              "items": { "enum": ["class", "combatStyle", "aidt", "packages", "defaultPackageList", "defaultOutfit", "voice", "level", "namePlaceholder"] }
            },
            "runtimeAuthority": { "const": false },
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
          },
          "oneOf": [
            {
              "properties": {
                "completed": { "const": false },
                "status": { "const": "refused" },
                "manifestPath": { "type": "null" },
                "manifestSize": { "type": "null" },
                "manifestSha256": { "type": "null" },
                "outputPlugin": { "type": "null" },
                "targetFormId": { "type": "null" }
              }
            },
            {
              "properties": {
                "completed": { "const": true },
                "status": { "const": "staticPassRuntimeRequired" },
                "manifestPath": { "type": "string", "minLength": 1 },
                "manifestSize": { "type": "integer", "minimum": 1 },
                "manifestSha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                },
                "outputPlugin": { "type": "string", "minLength": 1 },
                "targetFormId": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{8}$"
                }
              }
            }
          ]
        }
        """));

    private static JsonElement WithExternalInstallPrepublication(
        JsonElement schema)
    {
        JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        root["properties"]!.AsObject()["externalInstallPrepublication"] =
            JsonNode.Parse(ExternalInstallPrepublicationSchema.GetRawText());
        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement ExternalInstallPrepublicationSchema => Parse(
        """
        {
          "type": ["object", "null"],
          "additionalProperties": false,
          "required": ["packageManifestSha256", "selectedManifestSha256", "bindings", "verification"],
          "properties": {
            "packageManifestSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
            "selectedManifestSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
            "bindings": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["descriptorId", "faceGeomExclusionAttestationSha256"],
                "properties": {
                  "descriptorId": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                  "faceGeomExclusionAttestationSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" }
                }
              }
            },
            "verification": {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaIdentifier", "packageIntegrity", "descriptorClosureValid", "descriptorIds", "currentInstallDependencyState", "installReady", "installDependencyAuthority", "runtimeAuthority", "visualAuthority", "providerObservations", "missingPrerequisites"],
              "properties": {
                "schemaIdentifier": { "const": "npc.external-install-dependency-verification.v1" },
                "packageIntegrity": { "type": "boolean" },
                "descriptorClosureValid": { "type": "boolean" },
                "historicalSnapshotValid": { "type": "boolean" },
                "descriptorIds": { "type": "array", "items": { "type": "string", "pattern": "^[0-9a-f]{64}$" } },
                "verifiedInstallSnapshot": { "type": "object" },
                "currentInstallDependencyState": { "type": "string", "enum": ["not-required", "declared-unverified", "verified"] },
                "installReady": { "type": "boolean" },
                "installDependencyAuthority": { "type": "boolean" },
                "runtimeAuthority": { "const": false },
                "visualAuthority": { "const": false },
                "providerObservations": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["providerPlugin", "expectedSha256"],
                    "properties": {
                      "providerPlugin": { "type": "string", "minLength": 1 },
                      "expectedSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                      "currentSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                      "enabled": { "type": "boolean" }
                    }
                  }
                },
                "missingPrerequisites": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["diagnosticCode", "portableIdentity", "nextAction"],
                    "properties": {
                      "diagnosticCode": { "type": "string", "minLength": 1 },
                      "portableIdentity": { "type": "string", "minLength": 1 },
                      "expectedSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                      "currentSha256": { "type": "string", "pattern": "^[0-9a-f]{64}$" },
                      "enabled": { "type": "boolean" },
                      "nextAction": { "type": "string", "minLength": 1 }
                    }
                  }
                }
              }
            }
          }
        }
        """);

    private static JsonElement NpcCreatePreflightSchema => Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:actorwright:protocol-v2:npc-create-preflight-result:v1",
          "title": "Actorwright protocol v2 NPC create preflight result",
          "type": "object",
          "additionalProperties": false,
          "required": [
            "created", "readyForBuild", "previewReady", "path", "sha256",
            "requiredGates", "optionalPreview", "diagnostics"
          ],
          "properties": {
            "created": { "type": "boolean" },
            "readyForBuild": { "type": "boolean" },
            "previewReady": { "type": "boolean" },
            "path": { "type": ["string", "null"] },
            "sha256": {
              "type": ["string", "null"],
              "pattern": "^[0-9A-F]{64}$"
            },
            "requiredGates": {
              "type": "array",
              "items": { "$ref": "#/$defs/gate" }
            },
            "optionalPreview": {
              "type": "array",
              "items": { "$ref": "#/$defs/gate" }
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
            "diagnostics": {
              "type": "array",
              "items": { "$ref": "#/$defs/diagnostic" }
            }
          },
          "oneOf": [
            {
              "properties": {
                "created": { "const": false },
                "readyForBuild": { "const": false },
                "previewReady": { "const": false },
                "path": { "type": "null" },
                "sha256": { "type": "null" }
              }
            },
            {
              "properties": {
                "created": { "const": true },
                "readyForBuild": { "type": "boolean" },
                "path": { "type": "string", "minLength": 1 },
                "sha256": {
                  "type": "string",
                  "pattern": "^[0-9A-F]{64}$"
                }
              }
            }
          ],
          "$defs": {
            "gate": {
              "type": "object",
              "additionalProperties": false,
              "required": ["id", "required", "passed", "detail"],
              "properties": {
                "id": { "type": "string", "minLength": 1 },
                "required": { "type": "boolean" },
                "passed": { "type": "boolean" },
                "detail": { "type": "string", "minLength": 1 }
              }
            },
            "diagnostic": {
              "type": "object",
              "additionalProperties": false,
              "required": ["code", "severity", "message"],
              "properties": {
                "code": { "type": "string", "minLength": 1 },
                "severity": { "type": "integer", "enum": [0, 1, 2] },
                "message": { "type": "string", "minLength": 1 }
              }
            }
          }
        }
        """);
}

public sealed class ProtocolV2SchemaException(
    ImmutableArray<ProtocolDiagnostic> diagnostics) : Exception
{
    public ImmutableArray<ProtocolDiagnostic> Diagnostics { get; } =
        diagnostics;
}

public sealed class ProtocolV2SchemaService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot)
{
    public const string ResultSchemaId =
        AgentProtocolSchemaIds.SchemaExportResult;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The configured service pairs inline rendering with policy-bound optional writes.")]
    public JsonElement Render(string? commandName) => RenderInline(commandName);

    public static JsonElement RenderInline(string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return JsonSerializer.SerializeToElement(
                new
                {
                    SchemaId = ResultSchemaId,
                    ProtocolVersion = BuildInfo.LatestProtocolVersion,
                    ScopedHelpResultSchema =
                        ProtocolV2ResultSchemaCatalog.ScopedHelp,
                    PathConventions = SchemaExportService.PathConventions,
                    Commands = AgentCommandRegistry.All.Select(contract =>
                        new
                        {
                            Contract = contract,
                            ResultSchemas =
                                ProtocolV2ResultSchemaCatalog.For(contract),
                            DocumentSchemas =
                                DocumentSchemasFor(contract.Name)
                        })
                },
                JsonOptions);
        }

        var contract = AgentCommandRegistry.All.FirstOrDefault(item =>
            string.Equals(
                item.Name,
                commandName,
                StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Contains(
                commandName,
                StringComparer.OrdinalIgnoreCase));
        if (contract is null)
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaCommandUnknown,
                $"Unknown command '{commandName}'.",
                DiagnosticClass.Usage,
                RecoveryAction.CorrectInput,
                "command");
        }

        return JsonSerializer.SerializeToElement(
            new
            {
                SchemaId = ResultSchemaId,
                ProtocolVersion = BuildInfo.LatestProtocolVersion,
                ScopedHelpResultSchema =
                    ProtocolV2ResultSchemaCatalog.ScopedHelp,
                PathConventions = SchemaExportService.PathConventions,
                Contract = contract,
                ResultSchemas = ProtocolV2ResultSchemaCatalog.For(contract),
                DocumentSchemas =
                    DocumentSchemasFor(contract.Name)
            },
            JsonOptions);
    }

    private static ImmutableArray<CommandDocumentSchemaDefinition> DocumentSchemasFor(
        string commandName)
    {
        ImmutableArray<CommandDocumentSchemaDefinition> schemas =
            CommandDocumentSchemaCatalog.For(commandName);
        if (!commandName.Equals("npc finish analyze", StringComparison.OrdinalIgnoreCase) &&
            !commandName.Equals("npc finish apply", StringComparison.OrdinalIgnoreCase) &&
            !commandName.Equals("npc finish verify", StringComparison.OrdinalIgnoreCase))
            return schemas;

        var identifiers = new HashSet<string>(
            schemas.Select(schema => schema.SchemaIdentifier),
            StringComparer.Ordinal);
        string[] required = commandName.Equals(
                "npc finish verify", StringComparison.OrdinalIgnoreCase)
            ? [
                SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
                SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier
            ]
            : [
                SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
                SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
                SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier
            ];
        if (identifiers.Count != schemas.Length || required.Any(
                identifier => !identifiers.Contains(identifier)))
            throw new InvalidOperationException(
                $"Finish Core schema export for '{commandName}' is missing a required version or contains duplicate identifiers.");
        return schemas;
    }

    public ProtocolArtifact WriteNew(
        WorkspacePath output,
        JsonElement document,
        string producerCommand = "schema export",
        string requestDigest = "")
    {
        ValidateOutputDrive(output);

        var parentValue = Path.GetDirectoryName(output.Value);
        if (string.IsNullOrWhiteSpace(parentValue) ||
            !Directory.Exists(parentValue))
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The schema output directory must already exist.",
                DiagnosticClass.Security,
                RecoveryAction.ChooseFreshOutput,
                "output");
        }

        var parent = new WorkspacePath(Path.GetFullPath(parentValue));
        var policyDiagnostics = policy.Evaluate(labRoot, output)
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => new ProtocolDiagnostic(
                item.Code,
                item.Severity,
                item.Message,
                DiagnosticClass.Security,
                new DiagnosticRecovery(
                    RecoveryAction.ChooseFreshOutput,
                    "output",
                    null,
                    "Choose a fresh ordinary K-local file under an existing admitted directory.",
                    false)))
            .ToImmutableArray();
        if (!policyDiagnostics.IsEmpty)
            throw new ProtocolV2SchemaException(policyDiagnostics);

        if (File.Exists(output.Value) || Directory.Exists(output.Value))
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputExists,
                "Schema export refuses to overwrite an existing path.",
                DiagnosticClass.Security,
                RecoveryAction.ChooseFreshOutput,
                "output");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var temporary = Path.Combine(
            parent.Value,
            $".{Path.GetFileName(output.Value)}.tmp-{Guid.NewGuid():N}");
        var moved = false;
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, output.Value);
            moved = true;
            using var reopened = new FileStream(
                output.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var sha256 = Convert.ToHexString(SHA256.HashData(reopened));
            return new ProtocolArtifact(
                "schema",
                ResultSchemaId,
                output.Value,
                reopened.Length,
                sha256,
                producerCommand,
                requestDigest,
                [],
                "written");
        }
        catch (ProtocolV2SchemaException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporary(temporary);
            if (moved)
                TryDeleteTemporary(output.Value);
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "Schema export could not write and verify the requested output.",
                DiagnosticClass.Operation,
                RecoveryAction.RepairEnvironment,
                "output");
        }
    }

    public static void ValidateOutputDrive(WorkspacePath output)
    {
        if (!string.Equals(
                Path.GetPathRoot(output.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputOutsideKDrive,
                "Protocol v2 schema output must be a fresh ordinary file on the K drive.",
                DiagnosticClass.Security,
                RecoveryAction.ChooseFreshOutput,
                "output");
        }
    }

    private static ProtocolV2SchemaException Failure(
        string code,
        string message,
        DiagnosticClass diagnosticClass,
        RecoveryAction recovery,
        string? option) =>
        new(
        [
            new ProtocolDiagnostic(
                code,
                DiagnosticSeverity.Error,
                message,
                diagnosticClass,
                new DiagnosticRecovery(
                    recovery,
                    option,
                    null,
                    message,
                    false))
        ]);

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
