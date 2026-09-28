using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

internal static partial class CommandDocumentSchemaCatalog
{
    private static JsonElement VoiceServicesSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.voice-services.v1","type":"object","additionalProperties":false,
        "required":["schema","selectedEndpoint","services","diagnostics","probedUtc","requestTextLoggedByService"],"properties":{"schema":{"const":"npc.voice-services.v1"},"selectedEndpoint":{"type":["string","null"]},"probedUtc":{"type":"string"},"requestTextLoggedByService":{"type":"boolean","const":true},"diagnostics":{"$ref":"#/$defs/diagnostics"},"services":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["endpoint","source","platform","compatible","apiTitle","apiVersion","models","languages","speakerCount","speakerFolder","outputFolder","modelFolder","settingsJson","storageVerdict","storageDetail","fingerprint","diagnostics"],"properties":{"endpoint":{"type":"string","format":"uri"},"source":{"enum":["override","config","default"]},"platform":{"enum":["windows","wsl"]},"compatible":{"type":"boolean"},"apiTitle":{"type":["string","null"]},"apiVersion":{"type":["string","null"]},"models":{"$ref":"#/$defs/strings"},"languages":{"$ref":"#/$defs/strings"},"speakerCount":{"type":"integer","minimum":0},"speakerFolder":{"type":["string","null"]},"outputFolder":{"type":["string","null"]},"modelFolder":{"type":["string","null"]},"settingsJson":{"type":["string","null"]},"storageVerdict":{"enum":["safe","refused","unknown"]},"storageDetail":{"type":["string","null"]},"fingerprint":{"$ref":"#/$defs/nullableSha"},"diagnostics":{"$ref":"#/$defs/diagnostics"}}}}},
        "$defs":{"strings":{"type":"array","items":{"type":"string"}},"nullableSha":{"type":["string","null"],"pattern":"^[0-9A-Fa-f]{64}$"},"diagnostics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["code","severity","message"],"properties":{"code":{"type":"string"},"severity":{"enum":["info","warning","error"]},"message":{"type":"string"}}}}}}
        """);

    private static JsonElement VoiceSampleSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.voice-sample.v1","type":"object","additionalProperties":false,
        "required":["schema","plugin","formId","editorId","voicePrefix","originalPath","originalSha256","original","normalizedPath","normalizedSha256","normalized","createdUtc"],
        "properties":{"schema":{"const":"npc.voice-sample.v1"},"plugin":{"type":"string","pattern":"(?i)\\.(esp|esm|esl)$"},"formId":{"type":"string","pattern":"^0x[0-9A-Fa-f]{1,8}$"},"editorId":{"type":["string","null"]},"voicePrefix":{"type":"string","minLength":1},"originalPath":{"type":"string","minLength":3},"originalSha256":{"$ref":"#/$defs/sha"},"original":{"$ref":"#/$defs/audio"},"normalizedPath":{"type":"string","minLength":3},"normalizedSha256":{"$ref":"#/$defs/sha"},"normalized":{"$ref":"#/$defs/audio"},"createdUtc":{"type":"string"}},
        "$defs":{"sha":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"audio":{"type":"object","additionalProperties":false,"required":["sampleRate","channels","bitsPerSample","format","sampleFrames","durationSeconds","peakDbfs","rmsDbfs"],"properties":{"sampleRate":{"type":"integer","minimum":1},"channels":{"type":"integer","minimum":1},"bitsPerSample":{"enum":[8,16,24,32]},"format":{"enum":["pcm","float"]},"sampleFrames":{"type":"integer","minimum":1},"durationSeconds":{"type":"number","exclusiveMinimum":0},"peakDbfs":{"type":"number"},"rmsDbfs":{"type":"number"}}}}}
        """);

    private static JsonElement VoiceSynthesisSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.voice-synthesis.v1","type":"object","additionalProperties":false,
        "required":["schema","dialogueManifestPath","dialogueManifestSha256","sampleAuthorityPath","sampleAuthoritySha256","sampleSha256","endpoint","backendIdentity","settingsSha256","speakerReference","lines","updatedUtc"],
        "properties":{"schema":{"const":"npc.voice-synthesis.v1"},"dialogueManifestPath":{"type":"string"},"dialogueManifestSha256":{"$ref":"#/$defs/sha"},"sampleAuthorityPath":{"type":"string"},"sampleAuthoritySha256":{"$ref":"#/$defs/sha"},"sampleSha256":{"$ref":"#/$defs/sha"},"endpoint":{"type":"string","format":"uri"},"backendIdentity":{"$ref":"#/$defs/sha"},"settingsSha256":{"$ref":"#/$defs/sha"},"speakerReference":{"type":"string","pattern":"^(?:/mnt/[a-z]/|[A-Za-z]:\\\\).+"},"updatedUtc":{"type":"string"},"lines":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["lineId","text","language","textSha256","status","outputFile","outputSha256","durationSeconds","sampleRate","elapsedSeconds","failureCode","failureMessage"],"properties":{"lineId":{"type":"string","minLength":1},"text":{"type":"string","minLength":1},"language":{"type":"string","minLength":1},"textSha256":{"$ref":"#/$defs/sha"},"status":{"enum":["pending","succeeded","failed","skipped"]},"outputFile":{"type":["string","null"]},"outputSha256":{"$ref":"#/$defs/nullableSha"},"durationSeconds":{"type":["number","null"]},"sampleRate":{"type":["integer","null"]},"elapsedSeconds":{"type":["number","null"]},"failureCode":{"type":["string","null"]},"failureMessage":{"type":["string","null"]}}}}},"$defs":{"sha":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"nullableSha":{"type":["string","null"],"pattern":"^[0-9A-Fa-f]{64}$"}}}
        """);

    private static JsonElement DialogueManifestSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.dialogue.manifest.v1","type":"object","additionalProperties":false,
        "required":["schema","npc","language","profile","questEditorId","questPriority","template","lines"],"properties":{"schema":{"const":"npc.dialogue.manifest.v1"},"npc":{"$ref":"#/$defs/npc"},"language":{"type":"string","minLength":1},"profile":{"$ref":"#/$defs/profile"},"questEditorId":{"type":"string","minLength":1},"questPriority":{"type":"integer","minimum":0,"maximum":255},"template":{"type":["string","null"]},"lines":{"type":"array","items":{"$ref":"#/$defs/line"}}},
        "$defs":{"npc":{"type":"object","additionalProperties":false,"required":["plugin","formId","editorId","voicePrefix","female"],"properties":{"plugin":{"type":"string"},"formId":{"type":"string"},"editorId":{"type":["string","null"]},"voicePrefix":{"type":"string","minLength":1},"female":{"type":"boolean"}}},"profile":{"type":"object","additionalProperties":false,"required":["name","tone","exclusions","home","role"],"properties":{"name":{"type":"string","minLength":1},"tone":{"type":"array","items":{"type":"string"}},"exclusions":{"type":"array","items":{"type":"string"}},"exclusionReasons":{"type":"object","additionalProperties":{"type":"string","minLength":1}},"home":{"type":["string","null"]},"role":{"type":["string","null"]}}},"condition":{"type":"object","additionalProperties":false,"required":["function","parameter","secondParameter","operator","value","or","runOn"],"properties":{"function":{"type":"string","minLength":1},"parameter":{"type":["string","null"]},"secondParameter":{"type":["string","null"]},"operator":{"enum":["==","!=",">",">=","<","<="]},"value":{"type":"number"},"or":{"type":"boolean"},"runOn":{"enum":["subject","target","player"]}}},"line":{"type":"object","additionalProperties":false,"required":["id","category","topic","subtype","text","emotion","emotionValue","conditions","priority","repeat","randomPercent","cooldownHours","action","prompt","status","positiveTest","negativeTest"],"properties":{"id":{"type":"string","minLength":1},"category":{"type":"string","minLength":1},"topic":{"type":"string","minLength":1},"subtype":{"type":"string","minLength":1},"text":{"type":"string","minLength":1},"emotion":{"type":"string"},"emotionValue":{"type":"integer","minimum":0,"maximum":100},"conditions":{"type":"array","items":{"$ref":"#/$defs/condition"}},"priority":{"type":"number"},"repeat":{"enum":["once","random","always"]},"randomPercent":{"type":"integer","minimum":0,"maximum":100},"cooldownHours":{"type":"integer","minimum":0},"action":{"enum":["none","recruit","dismiss","wait","follow","trade"]},"prompt":{"type":["string","null"]},"status":{"type":"string"},"positiveTest":{"type":["string","null"]},"negativeTest":{"type":["string","null"]}}}}}
        """);

    private static JsonElement DialogueProposalBaseSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.dialogue.proposal.v1","description":"Hash-bound dialogue-only overlay plan. The source must own no INFO records; existing dialogue/audio import is unsupported. Apply preserves existing NPC package assets in their source package and emits only the rewritten plugin, new voice assets, PEX, and preserved-plus-appended DataRoot SEQ.","type":"object","additionalProperties":false,
        "required":["schema","manifestPath","manifestSha256","manifest","sourcePluginPath","sourcePlugin","sourcePluginSha256","dataRoot","loadOrder","sampleAuthorityPath","sampleAuthoritySha256","lightPlugin","masterOrder","copiedMasterSha256","nextFormId","records","assets","budget","status","diagnostics"],
        "properties":{"schema":{"const":"npc.dialogue.proposal.v1"},"manifestPath":{"type":"string"},"manifestSha256":{"$ref":"#/$defs/sha"},"manifest":{"type":"object","required":["schema","npc","language","profile","questEditorId","questPriority","template","lines"]},"sourcePluginPath":{"type":"string"},"sourcePlugin":{"type":"string"},"sourcePluginSha256":{"$ref":"#/$defs/sha"},"dataRoot":{"type":"string"},"loadOrder":{"type":"array","items":{"type":"string"}},"sampleAuthorityPath":{"type":"string"},"sampleAuthoritySha256":{"$ref":"#/$defs/sha"},"lightPlugin":{"type":"boolean"},"masterOrder":{"type":"array","items":{"type":"string"}},"copiedMasterSha256":{"type":"object","additionalProperties":{"$ref":"#/$defs/sha"}},"nextFormId":{"type":"integer","minimum":0},"records":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["signature","localFormId","editorId","lineId"],"properties":{"signature":{"type":"string"},"localFormId":{"type":"integer"},"editorId":{"type":"string"},"lineId":{"type":["string","null"]}}}},"assets":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["lineId","infoLocalFormId","voiceTypeEditorId","relativeDirectory","fileStem"],"properties":{"lineId":{"type":"string"},"infoLocalFormId":{"type":"integer"},"voiceTypeEditorId":{"type":"string"},"relativeDirectory":{"type":"string"},"fileStem":{"type":"string"}}}},"budget":{"$ref":"#/$defs/budget"},"status":{"enum":["readyForReviewedWrite","refused"]},"diagnostics":{"$ref":"#/$defs/diagnostics"}},
        "$defs":{"sha":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"budget":{"type":"object","additionalProperties":false,"required":["ownedRecords","plannedRecords","headroom","limit","fits"],"properties":{"ownedRecords":{"type":"integer"},"plannedRecords":{"type":"integer"},"headroom":{"type":"integer"},"limit":{"type":"integer"},"fits":{"type":"boolean"}}},"diagnostics":{"type":"array","items":{"type":"object","required":["code","severity","message"]}}}}
        """);

    private static JsonElement DialogueProposalSchema => AddDialogueSeqBinding(DialogueProposalBaseSchema);

    private static JsonElement DialogueOutputManifestSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.dialogue.output-manifest.v1","type":"object","additionalProperties":false,
        "required":["schema","proposalPath","proposalSha256","synthesisPath","synthesisSha256","packageRoot","plugin","pluginSha256","lightPlugin","voiceTypeEditorId","voiceTypeLocalFormId","questEditorId","questLocalFormId","records","assets","seqFile","seqSha256","scriptFile","scriptSha256","lipTool","diagnostics"],
        "properties":{"schema":{"const":"npc.dialogue.output-manifest.v1"},"proposalPath":{"type":"string"},"proposalSha256":{"$ref":"#/$defs/sha"},"synthesisPath":{"type":"string"},"synthesisSha256":{"$ref":"#/$defs/sha"},"packageRoot":{"type":"string"},"plugin":{"type":"string"},"pluginSha256":{"$ref":"#/$defs/sha"},"lightPlugin":{"type":"boolean"},"voiceTypeEditorId":{"type":"string"},"voiceTypeLocalFormId":{"type":"integer"},"questEditorId":{"type":"string"},"questLocalFormId":{"type":"integer"},"records":{"type":"array","items":{"type":"object","required":["signature","localFormId","editorId","lineId"]}},"assets":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["lineId","infoLocalFormId","wav","wavSha256","lip","lipSha256","fuz","fuzSha256"],"properties":{"lineId":{"type":"string"},"infoLocalFormId":{"type":"integer"},"wav":{"type":"string"},"wavSha256":{"$ref":"#/$defs/sha"},"lip":{"type":["string","null"]},"lipSha256":{"$ref":"#/$defs/nullableSha"},"fuz":{"type":["string","null"]},"fuzSha256":{"$ref":"#/$defs/nullableSha"}}}},"seqFile":{"type":["string","null"]},"seqSha256":{"$ref":"#/$defs/nullableSha"},"scriptFile":{"type":["string","null"]},"scriptSha256":{"$ref":"#/$defs/nullableSha"},"lipTool":{"type":"string"},"diagnostics":{"type":"array","items":{"type":"object","required":["code","severity","message"]}}},"$defs":{"sha":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"nullableSha":{"type":["string","null"],"pattern":"^[0-9A-Fa-f]{64}$"}}}
        """);

    private static JsonElement DialogueVerificationSchema => Parse(
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"urn:actorwright:schema:npc.dialogue.verification.v1","type":"object","additionalProperties":false,
        "required":["schema","manifestPath","manifestSha256","verified","recordCount","infoCount","audioCount","lipCount","budget","diagnostics"],"properties":{"schema":{"const":"npc.dialogue.verification.v1"},"manifestPath":{"type":"string"},"manifestSha256":{"type":"string","pattern":"^[0-9A-Fa-f]{64}$"},"verified":{"type":"boolean"},"recordCount":{"type":"integer","minimum":0},"infoCount":{"type":"integer","minimum":0},"audioCount":{"type":"integer","minimum":0},"lipCount":{"type":"integer","minimum":0},"budget":{"type":"object","additionalProperties":false,"required":["ownedRecords","plannedRecords","headroom","limit","fits"],"properties":{"ownedRecords":{"type":"integer"},"plannedRecords":{"type":"integer"},"headroom":{"type":"integer"},"limit":{"type":"integer"},"fits":{"type":"boolean"}}},"diagnostics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["code","severity","message"],"properties":{"code":{"type":"string"},"severity":{"enum":["info","warning","error"]},"message":{"type":"string"}}}}}}
        """);

    private static ImmutableDictionary<string, ImmutableArray<CommandDocumentSchemaDefinition>> CreateVoiceDialogueSchemas() =>
        new Dictionary<string, ImmutableArray<CommandDocumentSchemaDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            ["npc voice discover"] = [new("service-inventory", "output", SkyrimNpcVoiceSchemas.Services, VoiceServicesSchema)],
            ["npc voice import"] = [new("sample-authority", "output", SkyrimNpcVoiceSchemas.Sample, VoiceSampleSchema)],
            ["npc voice synthesize"] = [new("dialogue-manifest", "input", SkyrimNpcDialogueSchemas.Manifest, DialogueManifestSchema), new("sample-authority", "input", SkyrimNpcVoiceSchemas.Sample, VoiceSampleSchema), new("synthesis", "output", SkyrimNpcVoiceSchemas.Synthesis, VoiceSynthesisSchema)],
            ["npc dialogue analyze"] = [new("manifest", "input-output", SkyrimNpcDialogueSchemas.Manifest, DialogueManifestSchema), new("sample-authority", "input", SkyrimNpcVoiceSchemas.Sample, VoiceSampleSchema), new("proposal", "output", SkyrimNpcDialogueSchemas.Proposal, DialogueProposalSchema)],
            ["npc dialogue apply"] = [new("proposal", "input", SkyrimNpcDialogueSchemas.Proposal, DialogueProposalSchema), new("synthesis", "input", SkyrimNpcVoiceSchemas.Synthesis, VoiceSynthesisSchema), new("output-manifest", "output", SkyrimNpcDialogueSchemas.OutputManifest, DialogueOutputManifestSchema)],
            ["npc dialogue verify"] = [new("output-manifest", "input", SkyrimNpcDialogueSchemas.OutputManifest, DialogueOutputManifestSchema), new("verification", "output", SkyrimNpcDialogueSchemas.Verification, DialogueVerificationSchema)]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private static JsonElement AddDialogueSeqBinding(JsonElement schema)
    {
        System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        root["required"]!.AsArray().Insert(15, "sourceSeqPath");
        root["required"]!.AsArray().Insert(16, "sourceSeqSha256");
        root["properties"]!["sourceSeqPath"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = new System.Text.Json.Nodes.JsonArray("string", "null"),
            ["description"] = "Absolute source-plugin-adjacent or copied-DataRoot SEQ candidate selected by analyze; null binds absence."
        };
        root["properties"]!["sourceSeqSha256"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = new System.Text.Json.Nodes.JsonArray("string", "null"),
            ["pattern"] = "^[0-9A-Fa-f]{64}$",
            ["description"] = "Exact analyzed source SEQ bytes, or null when absence is bound."
        };
        return Parse(root.ToJsonString());
    }
}
