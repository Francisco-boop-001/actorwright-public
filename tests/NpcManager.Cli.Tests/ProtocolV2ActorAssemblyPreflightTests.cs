using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2ActorAssemblyPreflightTests
{
    private const string ContractSchema =
        "npc.actor-assembly-preflight.contract.v1";
    private const string ResultSchema =
        "npc.actor-assembly-preflight.result.v1";
    private const string ErrorSchema =
        "npc.actor-assembly-preflight.error.v1";
    private const string ProtocolResultSchema =
        "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1";
    private const string ArtifactKind = "actor-assembly-preflight";
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task RunAsync()
    {
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            $"protocol-v2-actor-assembly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            AssertRegistryAndCommandCompatibility();
            AssertSchemaExport();
            ContractFixture fixture = WriteContract(root, "valid");
            await AssertExternalContractFixtures(root, fixture);
            await AssertStoreDocumentAndNestedUnknown(root, fixture);
            await AssertOutcomePersistence(root, fixture);
            await AssertHashDriftRefusal(root, fixture);
            await AssertOutputCollisionRefusal(root, fixture);
            await AssertSecurityRefusal(root, fixture);
            await AssertServiceRefusal(root, fixture);
            await AssertPersistenceRefusal(root, fixture);
            await AssertPackageManifestMismatchRefusal(root, fixture);
            await AssertLegacyRouteAndBytes(root, fixture);
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static void AssertRegistryAndCommandCompatibility()
    {
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("npc assembly preflight");
        Assert(contract.Readiness == ProtocolReadiness.V2,
            "Actor Assembly preflight was not promoted to protocol v2.");
        Assert(
            contract.Options.Select(item => item.CliName).SequenceEqual(
                ["contract", "contract-sha256", "output"],
                StringComparer.Ordinal) &&
            contract.Options.All(item => item.Required),
            "Actor Assembly v2 options or requiredness changed.");
        Assert(contract.ResultSchemaIds.SequenceEqual(
                [ProtocolResultSchema], StringComparer.Ordinal),
            "Actor Assembly v2 result schema metadata changed.");
        Assert(AgentCommandRegistry.Validate().IsEmpty,
            "The promoted registry is not internally valid.");

        string[] names = CommandCatalog.All
            .Select(item => item.Name)
            .ToArray();
        Assert(names.Length == 142 &&
               names.Distinct(StringComparer.Ordinal).Count() == 142 &&
               names.Count(item => item == "npc assembly preflight") == 1,
            "The exact 142-name command surface changed.");
    }

    private static void AssertSchemaExport()
    {
        JsonElement export =
            ProtocolV2SchemaService.RenderInline("npc assembly preflight");
        JsonElement[] documents = export.GetProperty("documentSchemas")
            .EnumerateArray()
            .ToArray();
        Assert(documents.Length == 3,
            "Actor Assembly did not export contract, result, and error schemas.");
        Assert(documents.Select(item => (
                    item.GetProperty("name").GetString(),
                    item.GetProperty("direction").GetString(),
                    item.GetProperty("schemaIdentifier").GetString()))
                .SequenceEqual([
                    ("contract", "input", ContractSchema),
                    ("result", "output", ResultSchema),
                    ("error", "output", ErrorSchema)
                ]),
            "Actor Assembly schema document metadata changed.");
        AssertClosedSchema(
            documents,
            ContractSchema,
            [
                "schemaVersion", "operation", "edition", "packageManifest",
                "baseNpc", "placement", "bodyMorph", "outfitScope",
                "reviewedCompositePolicy"
            ],
            [
                "schemaVersion", "operation", "edition", "packageManifest",
                "baseNpc", "placement", "bodyMorph", "outfitScope"
            ]);
        AssertClosedSchema(
            documents,
            ResultSchema,
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "placedReferenceEvidence", "diagnosticTarget", "checks",
                "noWrite", "runtimeAuthority"
            ],
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "diagnosticTarget", "checks", "noWrite", "runtimeAuthority"
            ]);
        AssertClosedSchema(
            documents,
            ErrorSchema,
            ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"],
            ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"]);
        AssertProtocolResultSchema(export);
        AssertContractDiscriminatorSchemas(documents);
    }

    private static void AssertProtocolResultSchema(JsonElement export)
    {
        JsonElement[] definitions = export.GetProperty("resultSchemas")
            .EnumerateArray()
            .Where(item => item.GetProperty("schemaIdentifier").GetString() ==
                ProtocolResultSchema)
            .ToArray();
        Assert(definitions.Length == 1,
            "Actor Assembly did not export exactly one protocol result schema.");
        JsonElement schema = definitions[0].GetProperty("jsonSchema");
        Assert(schema.GetProperty("$schema").GetString() ==
                   "https://json-schema.org/draft/2020-12/schema" &&
               schema.GetProperty("$id").GetString() == ProtocolResultSchema,
            "Actor Assembly protocol result schema metadata changed.");
        if (!schema.TryGetProperty("oneOf", out JsonElement oneOf) ||
            oneOf.ValueKind != JsonValueKind.Array ||
            oneOf.GetArrayLength() != 2)
        {
            Assert(false,
                "Actor Assembly protocol result schema must be a closed two-branch oneOf.");
            return;
        }

        JsonElement[] branches = oneOf.EnumerateArray().ToArray();
        string[] branchReferences = branches
            .Select(item => item.GetProperty("$ref").GetString()!)
            .ToArray();
        Assert(branchReferences.SequenceEqual(
                ["#/$defs/success", "#/$defs/error"],
                StringComparer.Ordinal),
            "Actor Assembly protocol result oneOf branch references changed.");
        JsonElement definitionsObject = schema.GetProperty("$defs");
        AssertClosedObjectSchemas(schema, ProtocolResultSchema);
        AssertLocalSchemaReferencesResolve(
            schema,
            definitionsObject,
            ProtocolResultSchema);
        JsonElement successBranch = definitionsObject.GetProperty("success");
        JsonElement errorBranch = definitionsObject.GetProperty("error");
        AssertProtocolResultBranch(
            successBranch,
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "placedReferenceEvidence", "diagnosticTarget", "checks",
                "noWrite", "runtimeAuthority"
            ],
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "diagnosticTarget", "checks", "noWrite", "runtimeAuthority"
            ],
            "admitted success");
        AssertProtocolResultBranch(
            errorBranch,
            ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"],
            ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"],
            "legacy error/refusal");

        JsonElement successProperties = successBranch.GetProperty("properties");
        JsonElement errorProperties = errorBranch.GetProperty("properties");
        Assert(successProperties.GetProperty("schemaVersion").GetProperty("const")
                   .GetInt32() == 1 &&
               successProperties.GetProperty("artifactKind").GetProperty("const")
                   .GetString() == ActorAssemblyPreflightSchemas.LegacyResultArtifactKind &&
               successProperties.GetProperty("admitted").GetProperty("const")
                   .GetBoolean() &&
               errorProperties.GetProperty("schemaVersion").GetProperty("const")
                   .GetInt32() == 1 &&
               errorProperties.GetProperty("artifactKind").GetProperty("const")
                   .GetString() == ActorAssemblyPreflightSchemas.LegacyErrorArtifactKind &&
               errorProperties.GetProperty("contractAdmitted").GetProperty("type")
                   .GetString() == "boolean" &&
               errorProperties.GetProperty("diagnostics").GetProperty("type")
                   .GetString() == "array",
            "Actor Assembly protocol result oneOf branches changed their admitted/error shapes.");
    }

    private static void AssertProtocolResultBranch(
        JsonElement branch,
        IReadOnlyList<string> expectedProperties,
        IReadOnlyList<string> expectedRequired,
        string role)
    {
        Assert(branch.GetProperty("type").GetString() == "object" &&
               branch.GetProperty("additionalProperties").GetBoolean() == false,
            $"Actor Assembly {role} result branch is not closed.");
        string[] properties = branch.GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        Assert(properties.SequenceEqual(expectedProperties, StringComparer.Ordinal),
            $"Actor Assembly {role} result branch property set changed.");
        string[] required = branch.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert(required.SequenceEqual(expectedRequired, StringComparer.Ordinal),
            $"Actor Assembly {role} result branch required shape changed.");
        Assert(!branch.GetProperty("properties").EnumerateObject()
                .Any(item => item.Name == "schema"),
            $"Actor Assembly {role} result branch invented a serialized schema field.");
    }

    private static void AssertLocalSchemaReferencesResolve(
        JsonElement node,
        JsonElement definitions,
        string identifier)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("$ref", out JsonElement reference))
            {
                Assert(reference.ValueKind == JsonValueKind.String,
                    $"Schema '{identifier}' exported a non-string local $ref.");
                string referenceValue = reference.GetString()!;
                const string prefix = "#/$defs/";
                if (referenceValue.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string definitionName = referenceValue[prefix.Length..];
                    Assert(definitions.TryGetProperty(definitionName, out _),
                        $"Schema '{identifier}' exported an unresolved local $ref '{referenceValue}'.");
                }
                else if (referenceValue.StartsWith("#/", StringComparison.Ordinal))
                {
                    Assert(false,
                        $"Schema '{identifier}' exported an unsupported local $ref '{referenceValue}'.");
                }
            }

            foreach (JsonProperty property in node.EnumerateObject())
                AssertLocalSchemaReferencesResolve(
                    property.Value,
                    definitions,
                    identifier);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in node.EnumerateArray())
                AssertLocalSchemaReferencesResolve(child, definitions, identifier);
        }
    }

    private static void AssertContractDiscriminatorSchemas(
        IReadOnlyList<JsonElement> documents)
    {
        JsonElement contract = documents.Single(item =>
            item.GetProperty("schemaIdentifier").GetString() == ContractSchema)
            .GetProperty("jsonSchema");
        JsonElement definitions = contract.GetProperty("$defs");
        AssertExclusiveSchemaBranches(
            definitions.GetProperty("evidence"),
            ["status", "path", "sha256"],
            ["status", "reason"],
            ["reason"],
            ["path", "sha256"],
            "evidence");
        AssertStatusDiscriminator(
            definitions.GetProperty("evidence"),
            "available",
            "evidence");
        AssertExclusiveSchemaBranches(
            definitions.GetProperty("outfitScope"),
            ["status", "inventory"],
            ["status", "reason"],
            ["reason"],
            ["inventory"],
            "outfitScope");
        AssertStatusDiscriminator(
            definitions.GetProperty("outfitScope"),
            "complete",
            "outfitScope");
    }

    private static void AssertStatusDiscriminator(
        JsonElement schema,
        string admittedStatus,
        string role)
    {
        JsonElement[] branches = schema.GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        JsonElement admitted = branches[0].GetProperty("properties")
            .GetProperty("status");
        JsonElement unavailable = branches[1].GetProperty("properties")
            .GetProperty("status");
        Assert(admitted.GetProperty("const").GetString() == admittedStatus &&
               unavailable.GetProperty("enum").EnumerateArray()
                   .Select(item => item.GetString()!)
                   .SequenceEqual(
                       ["unavailable", "notApplicable"],
                       StringComparer.Ordinal),
            $"Contract {role} discriminator status values changed.");
    }

    private static void AssertExclusiveSchemaBranches(
        JsonElement schema,
        IReadOnlyList<string> availableRequired,
        IReadOnlyList<string> unavailableRequired,
        IReadOnlyList<string> availableForbidden,
        IReadOnlyList<string> unavailableForbidden,
        string role)
    {
        Assert(schema.TryGetProperty("oneOf", out JsonElement oneOf) &&
               oneOf.ValueKind == JsonValueKind.Array &&
               oneOf.GetArrayLength() == 2,
            $"Contract {role} schema lost its strict two-branch discriminator.");
        JsonElement[] branches = oneOf.EnumerateArray().ToArray();
        AssertSchemaBranchRequired(branches[0], availableRequired, role + " available");
        AssertSchemaBranchRequired(branches[1], unavailableRequired, role + " unavailable");
        foreach (string field in availableForbidden)
            Assert(HasExplicitForbiddenField(branches[0], field),
                $"Contract {role} available branch admits contradictory '{field}'.");
        foreach (string field in unavailableForbidden)
            Assert(HasExplicitForbiddenField(branches[1], field),
                $"Contract {role} unavailable branch admits contradictory '{field}'.");
    }

    private static void AssertSchemaBranchRequired(
        JsonElement branch,
        IReadOnlyList<string> expected,
        string role)
    {
        string[] actual = branch.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"Contract {role} branch required shape changed.");
    }

    private static bool HasExplicitForbiddenField(
        JsonElement node,
        string field)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return false;

        if (node.TryGetProperty("not", out JsonElement not) &&
            HasRequiredFieldOrAlternative(not, field))
            return true;

        if (node.TryGetProperty("additionalProperties", out JsonElement additional) &&
            additional.ValueKind == JsonValueKind.False &&
            node.TryGetProperty("properties", out JsonElement properties) &&
            !properties.EnumerateObject().Any(item => item.Name == field))
            return true;

        foreach (string keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (node.TryGetProperty(keyword, out JsonElement branches) &&
                branches.ValueKind == JsonValueKind.Array &&
                branches.EnumerateArray().Any(item =>
                    HasExplicitForbiddenField(item, field)))
                return true;
        }

        return false;
    }

    private static bool HasRequiredField(JsonElement node, string field) =>
        node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty("required", out JsonElement required) &&
        required.ValueKind == JsonValueKind.Array &&
        required.EnumerateArray().Any(item => item.GetString() == field);

    private static bool HasRequiredFieldOrAlternative(
        JsonElement node,
        string field) =>
        HasRequiredField(node, field) ||
        (node.ValueKind == JsonValueKind.Object &&
         node.TryGetProperty("anyOf", out JsonElement alternatives) &&
         alternatives.ValueKind == JsonValueKind.Array &&
         alternatives.EnumerateArray().Any(item =>
             HasRequiredField(item, field)));

    private static void AssertClosedSchema(
        IReadOnlyList<JsonElement> documents,
        string identifier,
        IReadOnlyList<string> expectedProperties,
        IReadOnlyList<string> expectedRequired)
    {
        JsonElement definition = documents.Single(item =>
            item.GetProperty("schemaIdentifier").GetString() == identifier);
        JsonElement schema = definition.GetProperty("jsonSchema");
        Assert(schema.GetProperty("$schema").GetString() ==
                   "https://json-schema.org/draft/2020-12/schema" &&
               schema.GetProperty("type").GetString() == "object",
            $"Schema '{identifier}' lost its external object contract.");
        Assert(schema.GetProperty("$id").GetString() == identifier,
            $"Schema '{identifier}' has the wrong external $id.");
        Assert(schema.GetProperty("additionalProperties").GetBoolean() == false,
            $"Schema '{identifier}' is not closed.");
        string[] actualProperties = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        Assert(actualProperties.SequenceEqual(expectedProperties,
                StringComparer.Ordinal),
            $"Schema '{identifier}' exported an unexpected property set.");
        string[] actualRequired = schema.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert(actualRequired.SequenceEqual(expectedRequired,
                StringComparer.Ordinal),
            $"Schema '{identifier}' exported an unexpected required shape.");
        Assert(!schema.GetProperty("properties").EnumerateObject()
                .Any(item => item.Name == "schema"),
            $"Schema '{identifier}' invented a serialized schema field.");
        AssertClosedObjectSchemas(schema, identifier);
    }

    private static void AssertClosedObjectSchemas(
        JsonElement node,
        string identifier)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out JsonElement type) &&
                type.GetString() == "object")
            {
                Assert(
                    node.TryGetProperty("additionalProperties", out JsonElement additional)
                        ? additional.ValueKind == JsonValueKind.False
                        : node.TryGetProperty("oneOf", out _),
                    $"Schema '{identifier}' exposed an open nested object.");
            }

            if (node.TryGetProperty("properties", out JsonElement properties))
            {
                Assert(!properties.EnumerateObject()
                        .Any(item => item.Name == "schema"),
                    $"Schema '{identifier}' invented a nested serialized schema field.");
            }

            foreach (JsonProperty property in node.EnumerateObject())
                AssertClosedObjectSchemas(property.Value, identifier);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in node.EnumerateArray())
                AssertClosedObjectSchemas(child, identifier);
        }
    }

    private static async Task AssertExternalContractFixtures(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var loader = new ActorAssemblyPreflightDocumentLoader(root);
        var cases = new[]
        {
            ("valid", fixture.Json, ActorAssemblyDocumentDisposition.Loaded),
            ("unknown", AddRootProperty(fixture.Json, "\"unknown\":true"),
                ActorAssemblyDocumentDisposition.Invalid),
            ("missing", fixture.Json.Replace(
                "\"operation\":\"npc-assembly-preflight\",", string.Empty,
                StringComparison.Ordinal), ActorAssemblyDocumentDisposition.Invalid),
            ("duplicate", AddRootProperty(
                fixture.Json, "\"operation\":\"npc-assembly-preflight\""),
                ActorAssemblyDocumentDisposition.Invalid),
            ("contradictory", AddOutfitContradiction(fixture.Json, root),
                ActorAssemblyDocumentDisposition.Invalid)
        };

        foreach ((string name, string json, ActorAssemblyDocumentDisposition expected)
                 in cases)
        {
            string pathValue = Path.Combine(root.Value, $"fixture-{name}.json");
            var path = new WorkspacePath(pathValue);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await File.WriteAllBytesAsync(pathValue, bytes);
            var result = await loader.LoadAsync(
                new ActorAssemblyPreflightRequest(path, Hash(bytes)),
                CancellationToken.None);
            Assert(result.Disposition == expected,
                $"External {name} contract fixture was not classified strictly.");
            if (expected == ActorAssemblyDocumentDisposition.Loaded)
            {
                Assert(result.Document is not null && result.Diagnostics.IsEmpty,
                    "The valid external contract fixture did not load cleanly.");
                ActorAssemblyContract document = result.Document!;
                Assert(document.SchemaVersion == 1 &&
                       document.Operation == "npc-assembly-preflight" &&
                       document.Edition == GameEdition.SkyrimSpecialEdition &&
                       document.PackageManifest.Path ==
                           new WorkspacePath(Path.Combine(
                               root.Value, "valid-package-manifest.json")) &&
                       document.BaseNpc.Plugin.Value == "Probe.esp" &&
                       document.BaseNpc.FormId == new FormId(0x800) &&
                       document.Placement.Mode == ActorAssemblyPlacementMode.None &&
                       document.BodyMorph.Owner == ActorAssemblyMorphOwner.None &&
                       document.OutfitScope.Status ==
                           ActorAssemblyOutfitScopeStatus.NotApplicable,
                    "The valid external contract did not round-trip to its typed document.");
            }
            else
                Assert(result.Document is null && !result.Diagnostics.IsEmpty,
                    $"External {name} fixture did not retain a typed refusal.");
        }
    }

    private static string AddOutfitContradiction(
        string json,
        WorkspacePath root)
    {
        const string marker =
            "\"outfitScope\":{\"status\":\"notApplicable\",\"reason\":\"The fixture does not claim outfit provenance.\"}";
        string inventoryPath = Child(root, "contradictory-inventory.json")
            .Value.Replace("\\", "\\\\", StringComparison.Ordinal);
        string replacement = marker[..^1] +
            ",\"inventory\":{\"path\":\"" + inventoryPath +
            "\",\"sha256\":\"" + new string('B', 64) + "\"}}";
        Assert(json.Contains(marker, StringComparison.Ordinal),
            "The outfit contradiction fixture marker was not found.");
        return json.Replace(marker, replacement, StringComparison.Ordinal);
    }

    private static async Task AssertStoreDocumentAndNestedUnknown(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var policy = new KOnlyWorkspacePolicy(
            root,
            new WorkspacePath(@"F:\ExampleGame"));
        var store = new ActorAssemblyPreflightResultStore(policy, root);
        WorkspacePath output = Child(root, "direct-store-unknown.json");
        ActorAssemblyPreflightResultDocument document;
        ActorAssemblyPreflightArtifact artifact = Artifact(
            ActorAssemblyOutcome.Unknown,
            fixture.ContractSha256);
        await using (ActorAssemblyPreflightResultLease lease =
            await store.WriteNewAsync(artifact, output, CancellationToken.None))
        {
            document = lease.Document;
            Assert(document.Result == artifact &&
                   document.Path == output &&
                   document.Size == document.Utf8Json.Length &&
                   document.Sha256 == Convert.ToHexString(
                       SHA256.HashData(document.Utf8Json.ToArray())),
                "The result store did not return its canonical retained document.");
            using JsonDocument retained = JsonDocument.Parse(
                document.Utf8Json.ToArray());
            AssertResultDocumentShape(
                retained.RootElement,
                ActorAssemblyOutcome.Unknown);
        }

        byte[] reopenedBytes = await File.ReadAllBytesAsync(output.Value);
        Assert(reopenedBytes.AsSpan().SequenceEqual(document.Utf8Json.AsSpan()),
            "The result store did not reopen the exact canonical bytes.");
        using JsonDocument reopened = JsonDocument.Parse(reopenedBytes);
        AssertResultDocumentShape(reopened.RootElement, ActorAssemblyOutcome.Unknown);
    }

    private static void AssertResultDocumentShape(
        JsonElement document,
        ActorAssemblyOutcome expectedOutcome)
    {
        AssertExactProperties(
            document,
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "diagnosticTarget", "checks", "noWrite", "runtimeAuthority"
            ],
            "persisted Actor Assembly result");
        Assert(document.GetProperty("schemaVersion").GetInt32() == 1 &&
               document.GetProperty("artifactKind").GetString() ==
                   ActorAssemblyPreflightSchemas.LegacyResultArtifactKind &&
               document.GetProperty("admitted").GetBoolean() &&
               document.GetProperty("outcome").GetString() ==
                   WireOutcome(expectedOutcome) &&
               document.GetProperty("contractSha256").GetString() is
                   { Length: 64 } contractSha256 &&
                string.Equals(contractSha256, contractSha256.ToUpperInvariant(),
                    StringComparison.Ordinal) &&
               document.GetProperty("packageManifestSha256").GetString() is
                   { Length: 64 } packageManifestSha256 &&
                string.Equals(packageManifestSha256,
                    packageManifestSha256.ToUpperInvariant(),
                    StringComparison.Ordinal) &&
               document.GetProperty("noWrite").GetBoolean() &&
               !document.GetProperty("runtimeAuthority").GetBoolean(),
            "Persisted Actor Assembly result scalar fields changed.");

        JsonElement baseEvidence = document.GetProperty("baseNpcEvidence");
        AssertExactProperties(
            baseEvidence,
            ["plugin", "declaredFormId", "typedRecord", "rawRecord", "outcome"],
            "persisted base NPC evidence");
        Assert(baseEvidence.GetProperty("outcome").GetString() ==
                   WireOutcome(expectedOutcome),
            "Persisted base NPC evidence lost the typed outcome.");
        foreach (string observationName in new[] { "typedRecord", "rawRecord" })
        {
            JsonElement observation = baseEvidence.GetProperty(observationName);
            AssertExactProperties(
                observation,
                ["status", "reason"],
                $"persisted {observationName} observation");
            Assert(observation.GetProperty("status").GetString() == "unknown" &&
                   !string.IsNullOrWhiteSpace(observation.GetProperty("reason").GetString()),
                $"Persisted {observationName} observation was not Unknown evidence.");
        }

        JsonElement[] checks = document.GetProperty("checks")
            .EnumerateArray()
            .ToArray();
        Assert(checks.Length == 1,
            "Persisted Actor Assembly result changed its check count.");
        JsonElement check = checks[0];
        AssertExactProperties(
            check,
            ["code", "outcome", "message", "evidence"],
            "persisted Actor Assembly check");
        Assert(check.GetProperty("outcome").GetString() ==
                   WireOutcome(expectedOutcome) &&
               check.GetProperty("evidence").GetArrayLength() == 0,
            "Persisted Actor Assembly check did not retain the typed outcome.");
    }

    private static void AssertExactProperties(
        JsonElement element,
        IReadOnlyList<string> expected,
        string role)
    {
        string[] actual = element.EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        Assert(actual.SequenceEqual(expected, StringComparer.Ordinal),
            $"{role} changed its canonical property set.");
    }

    private static async Task AssertOutcomePersistence(
        WorkspacePath root,
        ContractFixture fixture)
    {
        foreach (ActorAssemblyOutcome outcome in Enum.GetValues<ActorAssemblyOutcome>())
        {
            ActorAssemblyPreflightArtifact artifact = Artifact(
                outcome,
                fixture.ContractSha256);
            if (outcome is ActorAssemblyOutcome.Blocked or
                ActorAssemblyOutcome.Unknown)
            {
                artifact = artifact with
                {
                    PackageManifestSha256 = new Sha256Hash(new string('B', 64))
                };
            }

            var service = new CountingPreflightService(
                new ActorAssemblyPreflightExecutionResult(
                    true,
                    false,
                    artifact,
                    null));
            WorkspacePath output = Child(root, $"{WireOutcome(outcome)}.json");
            RunResult run = await RunV2Async(
                root, service, fixture.Path,
                fixture.ContractSha256.Value.ToUpperInvariant(), output);
            using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
            JsonElement value = envelope.RootElement;
            JsonElement persistedArtifact = value.GetProperty("artifacts")
                .EnumerateArray()
                .Single();
            CommandExitCode expectedExit = outcome is ActorAssemblyOutcome.Pass or
                ActorAssemblyOutcome.NotApplicable
                ? CommandExitCode.Success
                : CommandExitCode.ValidationFailure;
            Assert(run.ExitCode == expectedExit &&
                   value.GetProperty("outcome").GetString() ==
                        (expectedExit == CommandExitCode.Success
                            ? "succeeded"
                            : "failed"),
                $"{outcome} did not project its typed terminal outcome.");
            Assert(service.Calls == 1 && File.Exists(output.Value),
                $"{outcome} did not invoke the service and publish one artifact.");
            Assert(persistedArtifact.GetProperty("kind").GetString() == ArtifactKind &&
                   persistedArtifact.GetProperty("schemaOrMediaType").GetString() ==
                       ResultSchema &&
                   persistedArtifact.GetProperty("inputBindings")
                       .EnumerateArray()
                       .Select(item => item.GetString())
                       .SequenceEqual(
                           [fixture.ContractSha256.Value.ToUpperInvariant()],
                           StringComparer.Ordinal) &&
                   persistedArtifact.GetProperty("state").GetString() ==
                       "independentlyVerified",
                $"{outcome} protocol artifact metadata is not exact.");
            Assert(value.GetProperty("nextActions").GetArrayLength() == 0 &&
                   !value.GetProperty("artifacts").EnumerateArray().Any(item =>
                       item.GetProperty("kind").GetString() == "workflow-bundle"),
                $"{outcome} created a workflow successor or next action.");

            byte[] bytes = await File.ReadAllBytesAsync(output.Value);
            string physicalSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            Assert(physicalSha256 ==
                   persistedArtifact.GetProperty("sha256").GetString() &&
                   bytes.LongLength == persistedArtifact.GetProperty("size").GetInt64(),
                $"{outcome} artifact did not reopen with its physical hash/size.");
            using JsonDocument reopened = JsonDocument.Parse(bytes);
            JsonElement persisted = reopened.RootElement;
            AssertResultDocumentShape(persisted, outcome);
            if (outcome is ActorAssemblyOutcome.Blocked or
                ActorAssemblyOutcome.Unknown)
            {
                string expectedMismatchedPackageSha256 = new string('B', 64);
                Assert(value.GetProperty("result")
                           .GetProperty("packageManifestSha256").GetString() ==
                           expectedMismatchedPackageSha256 &&
                       persisted.GetProperty("packageManifestSha256").GetString() ==
                           expectedMismatchedPackageSha256,
                    $"{outcome} did not retain the exact mismatched package SHA-256 in its published and reopened results.");
            }
            Assert(run.Journal.Records.Count == 1 &&
                   run.Journal.Records[0].Effects.Select(Effect)
                       .SequenceEqual([
                           "readWorkspace|completed|workspace",
                           "writeNewArtifact|completed|k-local-output",
                           "appendLocalOperationJournal|attempted|workspace-local-journal"
                       ], StringComparer.Ordinal),
                $"{outcome} did not retain the exact terminal journal effects.");
            run.Probe!.RequireReleased(physicalSha256, bytes);
        }
    }

    private static async Task AssertHashDriftRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true, false, Artifact(ActorAssemblyOutcome.Pass,
                    fixture.ContractSha256), null));
        WorkspacePath output = Child(root, "hash-drift.json");
        RunResult run = await RunV2Async(
            root,
            service,
            fixture.Path,
            new string('B', 64),
            output,
            expectPublishedArtifact: false);
        using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
        AssertCanonicalErrorResult(envelope.RootElement);
        Assert(service.Calls == 0 && !File.Exists(output.Value) &&
               envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0,
            "Contract hash drift reached the service or published an artifact.");
    }

    private static async Task AssertOutputCollisionRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        WorkspacePath output = Child(root, "collision.json");
        byte[] sentinel = Encoding.UTF8.GetBytes("existing-output");
        await File.WriteAllBytesAsync(output.Value, sentinel);
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true, false, Artifact(ActorAssemblyOutcome.Pass,
                    fixture.ContractSha256), null));
        RunResult run = await RunV2Async(
            root, service, fixture.Path,
            fixture.ContractSha256.Value.ToUpperInvariant(), output,
            expectPublishedArtifact: false);
        using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
        AssertCanonicalErrorResult(envelope.RootElement);
        Assert(service.Calls == 0 &&
               (await File.ReadAllBytesAsync(output.Value)).AsSpan()
                   .SequenceEqual(sentinel) &&
               envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0,
            "Output collision was not refused before service execution.");
    }

    private static async Task AssertSecurityRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true, false, Artifact(ActorAssemblyOutcome.Pass,
                    fixture.ContractSha256), null));
        WorkspacePath output = Child(root, "security.json");
        RunResult run = await RunV2Async(
            root,
            service,
            fixture.Path,
            fixture.ContractSha256.Value,
            output,
            expectPublishedArtifact: false);
        using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
        AssertCanonicalErrorResult(envelope.RootElement);
        Assert(service.Calls == 0 && !File.Exists(output.Value) &&
               envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0,
            "A noncanonical contract hash escaped typed security admission.");
    }

    private static async Task AssertServiceRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true,
                false,
                null,
                new ActorAssemblyPreflightErrorArtifact(
                    1,
                    ActorAssemblyPreflightSchemas.LegacyErrorArtifactKind,
                    true,
                    [new Diagnostic(
                        "fixture-service-refusal",
                        DiagnosticSeverity.Error,
                        "The fixture service returned a typed refusal.")] )));
        WorkspacePath output = Child(root, "service-refusal.json");
        RunResult run = await RunV2Async(
            root,
            service,
            fixture.Path,
            fixture.ContractSha256.Value.ToUpperInvariant(),
            output,
            expectPublishedArtifact: false);
        using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
        AssertCanonicalErrorResult(envelope.RootElement);
        Assert(run.ExitCode == CommandExitCode.ValidationFailure &&
               service.Calls == 1 && !File.Exists(output.Value) &&
               envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0 &&
               !envelope.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() ==
                       ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            "A typed service refusal was normalized or published as an artifact.");
    }

    private static async Task AssertPersistenceRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        ActorAssemblyPreflightArtifact invalidArtifact = Artifact(
            ActorAssemblyOutcome.Pass,
            fixture.ContractSha256) with
        {
            SchemaVersion = 2
        };
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true,
                false,
                invalidArtifact,
                null));
        WorkspacePath output = Child(root, "persistence-refusal.json");
        RunResult run = await RunV2Async(
            root,
            service,
            fixture.Path,
            fixture.ContractSha256.Value.ToUpperInvariant(),
            output,
            expectPublishedArtifact: false);
        using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
        AssertCanonicalErrorResult(
            envelope.RootElement,
            ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed);
        Assert(run.ExitCode == CommandExitCode.GeneralFailure &&
               service.Calls == 1 && !File.Exists(output.Value) &&
               envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0 &&
               !envelope.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() ==
                       ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            "A typed persistence refusal was normalized or published as an artifact.");
    }

    private static async Task AssertPackageManifestMismatchRefusal(
        WorkspacePath root,
        ContractFixture fixture)
    {
        foreach (ActorAssemblyOutcome outcome in new[]
        {
            ActorAssemblyOutcome.Pass,
            ActorAssemblyOutcome.NotApplicable
        })
        {
            ActorAssemblyPreflightArtifact mismatched = Artifact(
                outcome,
                fixture.ContractSha256) with
            {
                PackageManifestSha256 = new Sha256Hash(new string('B', 64))
            };
            var service = new CountingPreflightService(
                new ActorAssemblyPreflightExecutionResult(
                    true,
                    false,
                    mismatched,
                    null));
            WorkspacePath output = Child(
                root,
                $"package-manifest-mismatch-{WireOutcome(outcome)}.json");
            RunResult run = await RunV2Async(
                root,
                service,
                fixture.Path,
                fixture.ContractSha256.Value.ToUpperInvariant(),
                output,
                expectPublishedArtifact: false);
            using JsonDocument envelope = JsonDocument.Parse(run.Envelope);
            AssertCanonicalErrorResult(
                envelope.RootElement,
                ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed);
            JsonElement packageDiagnostic = envelope.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed);
            JsonElement recovery = packageDiagnostic.GetProperty("recovery");
            Assert(packageDiagnostic.GetProperty("severity").GetString() == "error" &&
                   packageDiagnostic.GetProperty("class").GetString() == "validation" &&
                   recovery.GetProperty("action").GetString() == "correctInput" &&
                   !recovery.TryGetProperty("option", out _) &&
                   !recovery.TryGetProperty("artifactKind", out _) &&
                   recovery.GetProperty("constraint").GetString() ==
                       "Correct the exact Actor Assembly contract binding and retry." &&
                   !recovery.GetProperty("retryUnchangedSafe").GetBoolean(),
                $"{outcome} package-manifest mismatch diagnostic semantics changed.");
            Assert(run.ExitCode == CommandExitCode.ValidationFailure &&
                   service.Calls == 1 && !File.Exists(output.Value) &&
                   envelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0 &&
                   envelope.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() ==
                           ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed) &&
                   !envelope.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() ==
                           ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
                $"{outcome} package-manifest mismatch was not refused as a typed result validation error.");
        }
    }

    private static void AssertCanonicalErrorResult(
        JsonElement envelope,
        string? expectedDiagnosticCode = null)
    {
        Assert(envelope.TryGetProperty("result", out JsonElement result) &&
               result.ValueKind == JsonValueKind.Object,
            "A protocol refusal did not retain its canonical non-null error result.");
        AssertExactProperties(
            result,
            ["schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"],
            "protocol refusal result");
        Assert(result.GetProperty("schemaVersion").GetInt32() == 1 &&
               result.GetProperty("artifactKind").GetString() ==
                   ActorAssemblyPreflightSchemas.LegacyErrorArtifactKind &&
               result.GetProperty("contractAdmitted").ValueKind is
                   JsonValueKind.True or JsonValueKind.False,
            "The protocol refusal result was not the canonical legacy error object.");
        JsonElement[] diagnostics = result.GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert(diagnostics.Length > 0 && diagnostics.All(item =>
        {
            AssertExactProperties(
                item,
                ["code", "severity", "message"],
                "protocol refusal diagnostic");
            return !string.IsNullOrWhiteSpace(item.GetProperty("code").GetString()) &&
                   !string.IsNullOrWhiteSpace(item.GetProperty("message").GetString());
        }), "The protocol refusal error result lost its diagnostics.");
        Assert(!diagnostics.Any(item => item.GetProperty("code").GetString() ==
                   ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            "The canonical refusal result retained protocol-adapter-result-invalid.");
        if (expectedDiagnosticCode is not null)
            Assert(diagnostics.Any(item => item.GetProperty("code").GetString() ==
                       expectedDiagnosticCode),
                $"The protocol refusal did not retain diagnostic '{expectedDiagnosticCode}'.");
        Assert(!envelope.GetProperty("diagnostics").EnumerateArray().Any(item =>
            item.GetProperty("code").GetString() ==
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            "The runner replaced a typed refusal with protocol-adapter-result-invalid.");
    }

    private static async Task AssertLegacyRouteAndBytes(
        WorkspacePath root,
        ContractFixture fixture)
    {
        var service = new CountingPreflightService(
            new ActorAssemblyPreflightExecutionResult(
                true,
                false,
                null,
                new ActorAssemblyPreflightErrorArtifact(
                    1,
                    "actor-assembly-preflight-error",
                    true,
                    [new Diagnostic(
                        "fixture-blocked",
                        DiagnosticSeverity.Error,
                        "fixture")] )));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new ActorAssemblyPreflightCommandHandler(
            service, output, error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse([
                "npc", "assembly", "preflight",
                "--contract", fixture.Path.Value,
                "--contract-sha256", fixture.ContractSha256.Value.ToUpperInvariant(),
                "--json"]),
            CancellationToken.None);
        Assert(exit == CommandExitCode.ValidationFailure &&
               error.ToString().Length == 0 &&
               output.ToString().Contains(
                   "\"artifactKind\": \"actor-assembly-preflight-error\"",
                   StringComparison.Ordinal),
            "The legacy Actor Assembly handler projection changed.");

        CliBoundaryResult legacy = await ProtocolV2TestHost.RunAsync([
            "npc", "assembly", "preflight", "--json"]);
        const string legacyUsageMessage =
            "npc assembly preflight requires --contract <absolute-K-local.json> " +
            "and --contract-sha256 <SHA256>.";
        string expectedLegacyError = JsonSerializer.Serialize(
            new { code = "usage-error", message = legacyUsageMessage },
            LegacyJsonOptions) + Environment.NewLine;
        Assert(legacy.ExitCode == (int)CommandExitCode.UsageError &&
               legacy.StandardOutput == string.Empty &&
               legacy.StandardError == expectedLegacyError,
            "The legacy Actor Assembly stdout/stderr route changed.");
    }

    private static async Task<RunResult> RunV2Async(
        WorkspacePath root,
        CountingPreflightService service,
        WorkspacePath contract,
        string contractSha256,
        WorkspacePath output,
        bool expectPublishedArtifact = true)
    {
        var policy = new KOnlyWorkspacePolicy(
            root,
            new WorkspacePath(@"F:\ExampleGame"));
        var store = new ActorAssemblyPreflightResultStore(policy, root);
        var adapter = new ProtocolV2ActorAssemblyPreflightAdapter(
            root,
            service,
            store);
        var buffer = new StringWriter();
        TerminalLockProbe? probe = expectPublishedArtifact
            ? new TerminalLockProbe(output)
            : null;
        using var text = new LockProbeTextWriter(
            buffer,
            () => probe?.Observe("envelope writer"));
        var journal = new RecordingJournal(
            () => probe?.Observe("operation journal"));
        var runner = new ProtocolV2Runner(
            text,
            _ => journal,
            [adapter],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(
            CommandLine.Parse([
                "npc", "assembly", "preflight",
                "--protocol", "2",
                "--contract", contract.Value,
                "--contract-sha256", contractSha256,
                "--output", output.Value,
                "--json"]),
            CancellationToken.None);
        string envelope = buffer.ToString().Trim();
        Assert(envelope.Length > 0, "The protocol runner emitted no envelope.");
        return new(exit, envelope, journal, probe);
    }

    private static ContractFixture WriteContract(
        WorkspacePath root,
        string name)
    {
        string manifestPath = Child(root, $"{name}-package-manifest.json").Value;
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            operation = "npc-assembly-preflight",
            edition = "skyrimse",
            packageManifest = new
            {
                path = manifestPath,
                sha256 = new string('A', 64)
            },
            baseNpc = new { plugin = "Probe.esp", formId = "0x00000800" },
            placement = new { mode = "none" },
            bodyMorph = new
            {
                owner = "none",
                evidence = new
                {
                    status = "notApplicable",
                    reason = "The fixture has no runtime morph owner."
                }
            },
            outfitScope = new
            {
                status = "notApplicable",
                reason = "The fixture does not claim outfit provenance."
            },
            reviewedCompositePolicy = new
            {
                status = "notApplicable",
                reason = "The fixture has no reviewed composite policy."
            }
        });
        string pathValue = Child(root, $"{name}-contract.json").Value;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(pathValue, bytes);
        return new(new WorkspacePath(pathValue), Hash(bytes), json);
    }

    private static ActorAssemblyPreflightArtifact Artifact(
        ActorAssemblyOutcome outcome,
        Sha256Hash contractSha256)
    {
        var observation = new ActorAssemblyRecordObservation(
            ActorAssemblyObservationStatus.Unknown,
            null,
            null,
            "Fixture identity is deliberately not a runtime claim.");
        var evidence = new ActorAssemblyBaseNpcEvidence(
            new PluginName("Probe.esp"),
            new FormId(0x800),
            observation,
            observation,
            outcome);
        return new ActorAssemblyPreflightArtifact(
            1,
            "actor-assembly-preflight-result",
            outcome,
            contractSha256,
            new Sha256Hash(new string('A', 64)),
            evidence,
            null,
            "baseNpc",
            [new ActorAssemblyCheck(
                "FIXTURE",
                outcome,
                "Fixture outcome is persisted without authority inflation.",
                [])]);
    }

    private static string WireOutcome(ActorAssemblyOutcome outcome) =>
        outcome switch
        {
            ActorAssemblyOutcome.Pass => "pass",
            ActorAssemblyOutcome.Blocked => "blocked",
            ActorAssemblyOutcome.Unknown => "unknown",
            _ => "notApplicable"
        };

    private static string Effect(ProtocolEffect effect) => string.Join(
        '|', JsonNamingPolicy.CamelCase.ConvertName(effect.Kind.ToString()),
        effect.Status, effect.Scope);

    private static WorkspacePath Child(
        WorkspacePath root,
        string name) =>
        new(Path.Combine(root.Value, name));

    private static string AddRootProperty(string json, string property)
    {
        Assert(json.EndsWith('}'), "The fixture root is not a JSON object.");
        return json[..^1] + "," + property + "}";
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record ContractFixture(
        WorkspacePath Path,
        Sha256Hash ContractSha256,
        string Json);

    private sealed record RunResult(
        CommandExitCode ExitCode,
        string Envelope,
        RecordingJournal Journal,
        TerminalLockProbe? Probe);

    private sealed class CountingPreflightService(
        ActorAssemblyPreflightExecutionResult result) :
        IActorAssemblyPreflightService
    {
        public int Calls { get; private set; }

        public ValueTask<ActorAssemblyPreflightExecutionResult> PreflightAsync(
            ActorAssemblyPreflightRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingJournal(Action? probe = null) : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            probe?.Invoke();
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private sealed class LockProbeTextWriter(
        TextWriter inner,
        Action probe) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            probe();
            inner.WriteLine(value);
        }
    }

    private sealed class TerminalLockProbe(WorkspacePath output)
    {
        private int observations;

        public void Observe(string stage)
        {
            Assert(File.Exists(output.Value),
                $"Terminal result was not present at {stage}.");
            RequireWriteRefused(stage);
            observations++;
        }

        public void RequireReleased(string expectedSha256, byte[] expectedBytes)
        {
            Assert(observations == 2,
                "Terminal result lease was not probed at journal and writer boundaries.");
            byte[] actual = File.ReadAllBytes(output.Value);
            Assert(actual.AsSpan().SequenceEqual(expectedBytes) &&
                   Convert.ToHexString(SHA256.HashData(actual)) == expectedSha256,
                "Terminal result bytes changed after retained verification.");
            try
            {
                using FileStream _ = File.Open(
                    output.Value,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Terminal result lease remained held after runner completion.",
                    exception);
            }
        }

        private void RequireWriteRefused(string stage)
        {
            try
            {
                using FileStream _ = File.Open(
                    output.Value,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Terminal result lease was not held during {stage}.");
        }
    }
}
