using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class Preview261HairIntakeDiscoveryTests
{
    private const string ReviewedIntakeSchema =
        "npcmanager-reviewed-game-intake/2";

    public static async Task RunAsync()
    {
        await AssertIntakeRecoveryAsync();
        await AssertSchemaDiscoveryAsync();
    }

    private static async Task AssertSchemaDiscoveryAsync()
    {
        JsonElement workspace = await ExportAsync("workspace preflight");
        JsonElement hairPreview = await ExportAsync(
            "facegen hair-regions preview");
        JsonElement registered = workspace.GetProperty("resultSchemas")
            .EnumerateArray().Single().GetProperty("jsonSchema");

        JsonElement workspaceSchema = AssertReviewedIntakeSchema(
            workspace,
            "output",
            "workspace preflight");
        JsonElement previewSchema = AssertReviewedIntakeSchema(
            hairPreview,
            "input",
            "facegen hair-regions preview");
        Assert(
            JsonElement.DeepEquals(workspaceSchema, previewSchema) &&
            !JsonElement.DeepEquals(workspaceSchema, registered),
            "Reviewed-intake document discovery still aliases the protocol-2 " +
            "workspace result envelope.");
        AssertClosedContract(workspaceSchema);
        await AssertSchemaInstancesAsync(workspaceSchema);
    }

    private static async Task<JsonElement> ExportAsync(string command)
    {
        CliBoundaryResult response = await ProtocolV2TestHost.RunAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--command", command
        ]);
        Assert(
            response.ExitCode == 0 &&
            response.StandardError == string.Empty,
            $"Protocol-2 schema export failed for {command}: " +
            response.StandardOutput);
        using JsonDocument envelope = JsonDocument.Parse(
            response.StandardOutput);
        return envelope.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement AssertReviewedIntakeSchema(
        JsonElement export,
        string direction,
        string command)
    {
        JsonElement[] schemas = export.GetProperty("documentSchemas")
            .EnumerateArray().Where(item =>
                item.GetProperty("schemaIdentifier").GetString() ==
                ReviewedIntakeSchema).ToArray();
        Assert(
            schemas.Length == 1 &&
            schemas[0].GetProperty("direction").GetString() == direction,
            $"{command} did not publish the reviewed-intake " +
            $"schema as an {direction} document.");
        return schemas[0].GetProperty("jsonSchema").Clone();
    }

    private static void AssertClosedContract(JsonElement schema)
    {
        string[] required = schema.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!)
            .ToArray();
        JsonElement properties = schema.GetProperty("properties");
        JsonElement generatedPlugin = schema.GetProperty("$defs")
            .GetProperty("generatedPlugin").GetProperty("properties");
        JsonElement generatedSidecar = schema.GetProperty("$defs")
            .GetProperty("generatedSidecar");
        JsonElement generatedSidecarProperties = generatedSidecar
            .GetProperty("properties");
        JsonElement diagnostic = schema.GetProperty("$defs")
            .GetProperty("diagnostic");
        Assert(
            schema.GetProperty("additionalProperties").ValueKind ==
                JsonValueKind.False &&
            required.Length == 20 &&
            required.Distinct(StringComparer.Ordinal).Count() == 20 &&
            properties.GetProperty("schemaVersion").GetProperty("const")
                .GetString() == "2" &&
            properties.GetProperty("edition").GetProperty("const")
                .GetString() == "skyrimse" &&
            properties.GetProperty("isAccepted").GetProperty("const")
                .GetBoolean() &&
            !properties.GetProperty("runtimeAuthority").GetProperty("const")
                .GetBoolean() &&
            properties.GetProperty("plugins").GetProperty("minItems")
                .GetInt32() == 1 &&
            properties.GetProperty("plugins").GetProperty("maxItems")
                .GetInt32() == 512 &&
            properties.GetProperty("bodySidecars").GetProperty("maxItems")
                .GetInt32() == 512 &&
            properties.GetProperty("generatedPlugins").GetProperty("maxItems")
                .GetInt32() == 512 &&
            properties.GetProperty("generatedSidecars").GetProperty("maxItems")
                .GetInt32() == 16384 &&
            generatedPlugin.GetProperty("sha256").GetProperty("type")
                .GetString() == "string" &&
            generatedPlugin.GetProperty("readSucceeded").GetProperty("const")
                .GetBoolean() &&
            generatedSidecar.GetProperty("additionalProperties").ValueKind ==
                JsonValueKind.False &&
            generatedSidecarProperties.GetProperty("kind")
                .GetProperty("enum").GetArrayLength() == 8 &&
            generatedSidecarProperties.GetProperty("formId")
                .GetProperty("anyOf").GetArrayLength() == 2 &&
            diagnostic.GetProperty("additionalProperties").ValueKind ==
                JsonValueKind.False &&
            diagnostic.GetProperty("properties").GetProperty("severity")
                .GetProperty("enum").EnumerateArray()
                .Select(item => item.GetString())
                .SequenceEqual(["info", "warning"], StringComparer.Ordinal),
            "Reviewed-intake JSON Schema is not the closed persisted codec contract.");
    }

    private static async Task AssertSchemaInstancesAsync(JsonElement schema)
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "preview261-reviewed-intake-schema",
            Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            byte[] pluginBytes = [1, 2, 3, 4];
            byte[] generatedPluginBytes = [5, 6, 7, 8];
            byte[] generatedSidecarBytes = [9, 10, 11, 12];
            byte[] loadOrderBytes = "Skyrim.esm\n"u8.ToArray();
            string pluginPath = Path.Combine(dataRoot, "Skyrim.esm");
            string generatedPluginPath = Path.Combine(dataRoot, "Generated.esp");
            string generatedRelativePath = "meshes/generated/facegeom.nif";
            string generatedSidecarPath = Path.Combine(
                dataRoot,
                generatedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string loadOrderPath = Path.Combine(root, "loadorder.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(generatedSidecarPath)!);
            await File.WriteAllBytesAsync(pluginPath, pluginBytes);
            await File.WriteAllBytesAsync(
                generatedPluginPath, generatedPluginBytes);
            await File.WriteAllBytesAsync(
                generatedSidecarPath, generatedSidecarBytes);
            await File.WriteAllBytesAsync(loadOrderPath, loadOrderBytes);
            var workspace = new WorkspacePath(root);
            var codec = new FaceGeomHairRegionsDocumentCodec(workspace);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                workspace,
                new WorkspacePath(dataRoot),
                new WorkspacePath(loadOrderPath),
                new WorkspacePath(Path.Combine(root, "review-output")),
                Sha(loadOrderBytes),
                [
                    new PluginClosureReviewEntry(
                        new PluginName("Skyrim.esm"),
                        0,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(pluginPath),
                        Sha(pluginBytes),
                        [])
                ],
                [],
                [
                    new GeneratedPluginScanEntry(
                        new PluginName("Generated.esp"),
                        new WorkspacePath(generatedPluginPath),
                        "NPC Manager",
                        Sha(generatedPluginBytes),
                        true,
                        [new FormId(15)])
                ],
                [
                    new GeneratedSidecarEntry(
                        new PluginName("Generated.esp"),
                        GeneratedSidecarKind.FaceGeom,
                        GeneratedSidecarVariant.Canonical,
                        new AssetPath(generatedRelativePath),
                        new WorkspacePath(generatedSidecarPath),
                        null,
                        generatedSidecarBytes.LongLength,
                        Sha(generatedSidecarBytes))
                ],
                1,
                Hash('D'),
                Hash('E'),
                false);
            intake = intake with
            {
                IntakeFingerprint =
                    ReviewedGameIntakeFingerprintAuthority.Fingerprint(intake)
            };
            ReviewedGameIntakeDocumentAuthority bound =
                codec.BindReviewedIntake(
                    intake,
                    new WorkspacePath(Path.Combine(root, "intake.json")));
            JsonNode canonical = JsonNode.Parse(
                bound.Document.Utf8Json.ToArray())!;
            Assert(MatchesSchema(schema, canonical),
                "A codec-bound schema-2 reviewed intake failed its exported schema.");

            JsonObject admittedDiagnostic = canonical.DeepClone().AsObject();
            admittedDiagnostic["diagnostics"] = new JsonArray(
                new JsonObject
                {
                    ["code"] = "reviewed-intake-warning",
                    ["severity"] = "warning",
                    ["message"] = "Reviewed warning evidence."
                });
            Assert(MatchesSchema(schema, admittedDiagnostic),
                "A codec-admitted nonempty warning diagnostic was excluded.");
            AssertCodecAccepts(codec, admittedDiagnostic);

            JsonObject normalizedHash = canonical.DeepClone().AsObject();
            normalizedHash["loadOrderHash"] = " \t" +
                normalizedHash["loadOrderHash"]!.GetValue<string>()
                    .ToUpperInvariant() + " \r\n";
            AssertCodecAndSchemaAccept(
                codec, schema, normalizedHash, "trimmed uppercase SHA-256");

            JsonObject flexibleFormId = canonical.DeepClone().AsObject();
            flexibleFormId["generatedPlugins"]![0]!["npcFormIds"]![0] =
                " 0Xf ";
            AssertCodecAndSchemaAccept(
                codec, schema, flexibleFormId, "flexible FormID spelling");

            JsonObject leadingZeroFormId = canonical.DeepClone().AsObject();
            leadingZeroFormId["generatedPlugins"]![0]!["npcFormIds"]![0] =
                "000000000";
            AssertCodecAndSchemaAccept(
                codec,
                schema,
                leadingZeroFormId,
                "FormID with arbitrarily many leading zeroes");

            string dotPluginPath = Path.Combine(dataRoot, ".esp");
            await File.WriteAllBytesAsync(dotPluginPath, pluginBytes);
            ReviewedGameIntake dotPluginIntake = intake with
            {
                Plugins =
                [
                    intake.Plugins[0] with
                    {
                        Plugin = new PluginName(".esp"),
                        Path = new WorkspacePath(dotPluginPath)
                    }
                ]
            };
            dotPluginIntake = dotPluginIntake with
            {
                IntakeFingerprint =
                    ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                        dotPluginIntake)
            };
            ReviewedGameIntakeDocumentAuthority dotPluginBound =
                codec.BindReviewedIntake(
                    dotPluginIntake,
                    new WorkspacePath(Path.Combine(root, "dot-plugin.json")));
            JsonNode dotPluginDocument = JsonNode.Parse(
                dotPluginBound.Document.Utf8Json.ToArray())!;
            AssertCodecAndSchemaAccept(
                codec, schema, dotPluginDocument, "extension-only plugin name");

            Assert(
                canonical["generatedSidecars"]![0]!["formId"] is null &&
                MatchesSchema(schema, canonical),
                "A codec-bound generated sidecar with a null FormID was excluded.");

            JsonObject extra = canonical.DeepClone().AsObject();
            extra["unexpected"] = true;
            AssertCodecAndSchemaRefuse(codec, schema, extra, "extra root member");

            JsonObject noPlugins = canonical.DeepClone().AsObject();
            noPlugins["plugins"] = new JsonArray();
            AssertCodecAndSchemaRefuse(codec, schema, noPlugins, "empty plugins");

            JsonObject errorDiagnostic = canonical.DeepClone().AsObject();
            errorDiagnostic["diagnostics"] = new JsonArray(
                new JsonObject
                {
                    ["code"] = "reviewed-intake-error",
                    ["severity"] = "error",
                    ["message"] = "Rejected error evidence."
                });
            AssertCodecAndSchemaRefuse(
                codec, schema, errorDiagnostic, "error diagnostic");

            JsonObject whitespaceDiagnostic = canonical.DeepClone().AsObject();
            whitespaceDiagnostic["diagnostics"] = new JsonArray(
                new JsonObject
                {
                    ["code"] = "reviewed-intake-warning",
                    ["severity"] = "warning",
                    ["message"] = "   "
                });
            AssertCodecAndSchemaRefuse(
                codec, schema, whitespaceDiagnostic, "whitespace diagnostic");

            JsonObject unsafePlugin = canonical.DeepClone().AsObject();
            unsafePlugin["plugins"]![0]!["plugin"] = "../Skyrim.esm";
            AssertCodecAndSchemaRefuse(
                codec, schema, unsafePlugin, "unsafe plugin name");

            JsonObject invalidWorkspacePath = canonical.DeepClone().AsObject();
            invalidWorkspacePath["workspaceRoot"] = "   ";
            AssertCodecAndSchemaRefuse(
                codec, schema, invalidWorkspacePath, "whitespace workspace path");

            JsonObject invalidRelativePath = canonical.DeepClone().AsObject();
            invalidRelativePath["generatedSidecars"]![0]!["relativePath"] =
                " ../facegeom.nif ";
            AssertCodecAndSchemaRefuse(
                codec, schema, invalidRelativePath, "traversing relative path");

            JsonObject nulRelativePath = canonical.DeepClone().AsObject();
            nulRelativePath["generatedSidecars"]![0]!["relativePath"] =
                "meshes/generated/face\0geom.nif";
            AssertCodecAndSchemaRefuse(
                codec, schema, nulRelativePath, "NUL-bearing relative path");

            AssertGeneratedRows(codec, schema, canonical);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertGeneratedRows(
        FaceGeomHairRegionsDocumentCodec codec,
        JsonElement schema,
        JsonNode canonical)
    {
        JsonObject invalidPlugin = canonical.DeepClone().AsObject();
        invalidPlugin["generatedPlugins"]![0]!["sha256"] = null;
        invalidPlugin["generatedPlugins"]![0]!["readSucceeded"] = false;
        AssertCodecAndSchemaRefuse(
            codec,
            schema,
            invalidPlugin,
            "generated-plugin nullable SHA and failed read");

        JsonObject invalidSidecar = canonical.DeepClone().AsObject();
        invalidSidecar["generatedSidecars"]![0]!["size"] = 0;
        AssertCodecAndSchemaRefuse(
            codec,
            schema,
            invalidSidecar,
            "generated-sidecar non-positive size");
    }

    private static async Task AssertIntakeRecoveryAsync()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "preview261-hair-intake-discovery",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspacePath(root);
            var codec = new FaceGeomHairRegionsDocumentCodec(workspace);
            (string requestPath, Sha256Hash requestHash,
                string proposalPath, Sha256Hash proposalHash) =
                await WriteDocumentsAsync(root, codec);

            string intakeDirectory = Path.Combine(root, "intake-directory");
            Directory.CreateDirectory(intakeDirectory);
            await AssertRefusalAsync(
                workspace,
                requestPath,
                requestHash,
                proposalPath,
                proposalHash,
                intakeDirectory,
                Path.Combine(root, "directory-preview"),
                CommandExitCode.SecurityRefusal,
                "facegeom-hair-regions-security-refused");

            string invalidIntake = Path.Combine(root, "invalid-intake.json");
            await File.WriteAllTextAsync(invalidIntake, "{}");
            await AssertRefusalAsync(
                workspace,
                requestPath,
                requestHash,
                proposalPath,
                proposalHash,
                invalidIntake,
                Path.Combine(root, "invalid-preview"),
                CommandExitCode.ValidationFailure,
                "facegeom-hair-regions-document-invalid");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool MatchesSchema(JsonElement schema, JsonNode instance)
    {
        using JsonDocument document = JsonDocument.Parse(
            instance.ToJsonString());
        return MatchesSchema(schema, document.RootElement, schema);
    }

    private static bool MatchesSchema(
        JsonElement schema,
        JsonElement instance,
        JsonElement root)
    {
        // This is a deliberately small evaluator for the keywords emitted by
        // this focused schema. It is not a general Draft 2020-12 validator.
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            const string prefix = "#/$defs/";
            string value = reference.GetString()!;
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Unsupported test schema reference '{value}'.");
            return MatchesSchema(
                root.GetProperty("$defs").GetProperty(value[prefix.Length..]),
                instance,
                root);
        }
        if (schema.TryGetProperty("oneOf", out JsonElement oneOf) &&
            oneOf.EnumerateArray().Count(candidate =>
                MatchesSchema(candidate, instance, root)) != 1)
            return false;
        if (schema.TryGetProperty("anyOf", out JsonElement anyOf) &&
            !anyOf.EnumerateArray().Any(candidate =>
                MatchesSchema(candidate, instance, root)))
            return false;
        if (schema.TryGetProperty("const", out JsonElement constant) &&
            !JsonElement.DeepEquals(constant, instance))
            return false;
        if (schema.TryGetProperty("enum", out JsonElement enumeration) &&
            !enumeration.EnumerateArray().Any(value =>
                JsonElement.DeepEquals(value, instance)))
            return false;
        if (schema.TryGetProperty("type", out JsonElement type) &&
            !MatchesType(type.GetString()!, instance))
            return false;

        if (instance.ValueKind == JsonValueKind.Object)
        {
            JsonElement properties = schema.TryGetProperty(
                    "properties", out JsonElement declared)
                ? declared
                : default;
            if (schema.TryGetProperty("required", out JsonElement required) &&
                required.EnumerateArray().Any(name =>
                    !instance.TryGetProperty(name.GetString()!, out _)))
                return false;
            if (schema.TryGetProperty(
                    "additionalProperties", out JsonElement additional) &&
                additional.ValueKind == JsonValueKind.False &&
                instance.EnumerateObject().Any(property =>
                    properties.ValueKind != JsonValueKind.Object ||
                    !properties.TryGetProperty(property.Name, out _)))
                return false;
            if (properties.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in properties.EnumerateObject())
                {
                    if (instance.TryGetProperty(
                            property.Name, out JsonElement value) &&
                        !MatchesSchema(property.Value, value, root))
                        return false;
                }
            }
        }
        if (instance.ValueKind == JsonValueKind.Array)
        {
            int length = instance.GetArrayLength();
            if (schema.TryGetProperty("minItems", out JsonElement minimumItems) &&
                length < minimumItems.GetInt32())
                return false;
            if (schema.TryGetProperty("maxItems", out JsonElement maximumItems) &&
                length > maximumItems.GetInt32())
                return false;
            if (schema.TryGetProperty("uniqueItems", out JsonElement unique) &&
                unique.GetBoolean() &&
                instance.EnumerateArray().Select(item => item.GetRawText())
                    .Distinct(StringComparer.Ordinal).Count() != length)
                return false;
            if (schema.TryGetProperty("items", out JsonElement items) &&
                instance.EnumerateArray().Any(item =>
                    !MatchesSchema(items, item, root)))
                return false;
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            string value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out JsonElement minimumLength) &&
                value.Length < minimumLength.GetInt32())
                return false;
            if (schema.TryGetProperty("pattern", out JsonElement pattern) &&
                !Regex.IsMatch(
                    value,
                    pattern.GetString()!,
                    RegexOptions.CultureInvariant))
                return false;
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            if (!instance.TryGetInt64(out long value))
                return false;
            if (schema.TryGetProperty("minimum", out JsonElement minimum) &&
                value < minimum.GetInt64())
                return false;
            if (schema.TryGetProperty("maximum", out JsonElement maximum) &&
                value > maximum.GetInt64())
                return false;
        }
        return true;
    }

    private static bool MatchesType(string type, JsonElement instance) =>
        type switch
        {
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "string" => instance.ValueKind == JsonValueKind.String,
            "integer" => instance.ValueKind == JsonValueKind.Number &&
                         instance.TryGetInt64(out _),
            "boolean" => instance.ValueKind is
                JsonValueKind.True or JsonValueKind.False,
            "null" => instance.ValueKind == JsonValueKind.Null,
            _ => throw new InvalidOperationException(
                $"Unsupported test schema type '{type}'.")
        };

    private static void AssertCodecAndSchemaRefuse(
        FaceGeomHairRegionsDocumentCodec codec,
        JsonElement schema,
        JsonNode instance,
        string name)
    {
        Assert(!MatchesSchema(schema, instance),
            $"The exported reviewed-intake schema accepted {name}.");
        bool refused = false;
        try
        {
            Parse(codec, instance);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                ArgumentException or
                UnauthorizedAccessException)
        {
            refused = true;
        }
        Assert(refused, $"The strict reviewed-intake codec accepted {name}.");
    }

    private static void AssertCodecAndSchemaAccept(
        FaceGeomHairRegionsDocumentCodec codec,
        JsonElement schema,
        JsonNode instance,
        string name)
    {
        Assert(MatchesSchema(schema, instance),
            $"The exported reviewed-intake schema rejected {name}.");
        try
        {
            Parse(codec, instance);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The strict reviewed-intake codec rejected {name}.",
                exception);
        }
    }

    private static void AssertCodecAccepts(
        FaceGeomHairRegionsDocumentCodec codec,
        JsonNode instance)
    {
        ReviewedGameIntakeDocumentAuthority authority = Parse(codec, instance);
        Assert(authority.Value.Edition == GameEdition.SkyrimSpecialEdition,
            "The strict codec did not accept its warning-diagnostic fixture.");
    }

    private static ReviewedGameIntakeDocumentAuthority Parse(
        FaceGeomHairRegionsDocumentCodec codec,
        JsonNode instance)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(instance.ToJsonString());
        return codec.ParseReviewedIntake(
            new ExactJsonFileAuthority(
                new WorkspacePath("K:\\reviewed-intake-schema-test.json"),
                bytes.ToImmutableArray(),
                bytes.LongLength,
                Sha(bytes)));
    }

    private static async Task AssertRefusalAsync(
        WorkspacePath workspace,
        string requestPath,
        Sha256Hash requestHash,
        string proposalPath,
        Sha256Hash proposalHash,
        string intakePath,
        string outputRoot,
        CommandExitCode expectedExit,
        string expectedCode)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        FaceGeomHairRegionsCommandHandler handler =
            FaceGeomHairRegionsCliComposition.Create(
                workspace,
                previewFactory: null,
                output,
                error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "preview",
                "--request", requestPath,
                "--request-sha256", requestHash.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposalHash.Value,
                "--intake", intakePath,
                "--output-root", outputRoot,
                "--json"
            ]),
            CancellationToken.None);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        JsonElement diagnostic = response.RootElement
            .GetProperty("diagnostics")[0];
        string message = diagnostic.GetProperty("message").GetString()!;
        Assert(
            exit == expectedExit &&
            error.ToString() == string.Empty &&
            diagnostic.GetProperty("code").GetString() == expectedCode &&
            message.Contains(
                "workspace preflight --protocol 2",
                StringComparison.Ordinal) &&
            message.Contains("--intake-output", StringComparison.Ordinal),
            "Reviewed-intake refusal did not preserve its diagnostic and " +
            "exit/security classification or identify the real producer: " +
            output);
    }

    private static async Task<(
        string RequestPath,
        Sha256Hash RequestHash,
        string ProposalPath,
        Sha256Hash ProposalHash)> WriteDocumentsAsync(
        string root,
        FaceGeomHairRegionsDocumentCodec codec)
    {
        var request = new FaceGeomHairRegionsRequest(
            FaceGeomHairRegionSchemas.Request,
            Hash('A'),
            new FaceGeomHairRegionsFile(
                new WorkspacePath(Path.Combine(root, "source.nif")),
                64,
                Hash('B')),
            "#D6BE83",
            "#F4E3B2",
            [
                new FaceGeomHairRegionAssignment(
                    "shape:0",
                    FaceGeomHairRegionRole.Preserve)
            ],
            new WorkspacePath(Path.Combine(root, "candidate.nif")),
            new WorkspacePath(Path.Combine(root, "candidate.manifest.json")));
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument = codec.BindRequest(request);
        var fingerprints = new FaceGeomHairRegionsFingerprints(
            Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'));
        var proposal = new FaceGeomHairRegionsProposal(
            FaceGeomHairRegionSchemas.Proposal,
            request.AnalysisSha256,
            requestDocument.Sha256,
            request.Source,
            request.Output,
            request.Manifest,
            request.PrimaryColor,
            request.AccentColor,
            request.Assignments,
            [
                new FaceGeomHairRegionsAuthorizedEnvelope(
                    "shader:0",
                    ["shape:0"],
                    FaceGeomHairRegionRole.Primary,
                    1,
                    12,
                    [1U, 2U, 3U],
                    [4U, 5U, 6U])
            ],
            [1],
            fingerprints,
            fingerprints,
            new FaceGeomHairRegionsFile(
                request.Output,
                request.Source.ByteLength,
                Hash('C')),
            null);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument = codec.BindProposal(proposal);
        string requestPath = Path.Combine(root, "request.json");
        string proposalPath = Path.Combine(root, "proposal.json");
        await File.WriteAllBytesAsync(
            requestPath,
            requestDocument.Utf8Json.ToArray());
        await File.WriteAllBytesAsync(
            proposalPath,
            proposalDocument.Utf8Json.ToArray());
        return (
            requestPath,
            requestDocument.Sha256,
            proposalPath,
            proposalDocument.Sha256);
    }

    private static Sha256Hash Hash(char value) => new(
        Convert.ToHexString(SHA256.HashData(
            Enumerable.Repeat((byte)value, 16).ToArray())));

    private static Sha256Hash Sha(byte[] bytes) => new(
        Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
