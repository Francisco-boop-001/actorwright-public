using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Exceptions;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class SkyrimNpcFinishCoreCliTests
{
    public static async Task TestCatalogAndParsing()
    {
        string[] expected = ["npc finish analyze", "npc finish apply", "npc finish verify"];
        foreach (string name in expected)
            Assert(CommandCatalog.All.Count(item => item.Name == name) == 1,
                $"Finish Core command '{name}' is not catalogued exactly once.");
        Assert(CommandCatalog.All.All(item => !item.Name.StartsWith("npc finish-core", StringComparison.Ordinal)),
            "The legacy npc finish-core alias leaked into the command catalogue.");
        Assert(CommandLine.Parse(["npc", "finish", "analyze", "--request", "x"])
                   .Name == "npc finish analyze",
            "Finish Core analyze command parsing is not stable.");
        Assert(CommandLine.Parse(["npc", "finish", "apply", "--request", "x"])
                   .Name == "npc finish apply",
            "Finish Core apply command parsing is not stable.");
        Assert(CommandLine.Parse(["npc", "finish", "verify", "--manifest", "x"])
                   .Name == "npc finish verify",
            "Finish Core verify command parsing is not stable.");

        Sha256Hash inputHash = new(new string('a', 64));
        var mutationService = new FakeMutationService();
        using var mutationOutput = new StringWriter();
        using var mutationError = new StringWriter();
        var mutationHandler = new MutationCommandHandler(
            mutationService, mutationOutput, mutationError);
        CommandExitCode mutationExit = await mutationHandler.RunPatchAsync(
            CommandLine.Parse([
                "npc", "patch",
                "--edition", "skyrimse",
                "--input-plugin", @"K:\ExampleWorkspace\source.esp",
                "--output", @"K:\ExampleWorkspace\output.esp",
                "--form-id", "0x00000800",
                "--set-flag", "protected",
                "--input-sha", inputHash.Value,
                "--json"
            ]),
            CancellationToken.None);
        using JsonDocument mutationResponse = JsonDocument.Parse(
            mutationOutput.ToString());
        Assert(
            mutationExit == CommandExitCode.Success &&
            mutationService.Request?.ExpectedInputHash == inputHash &&
            mutationResponse.RootElement.GetProperty("inputSha256").GetString() ==
                inputHash.Value,
            "npc patch ignored the consumer's --input-sha binding and emitted the all-zero failed-proposal sentinel.");

        var conflictingService = new FakeMutationService();
        using var conflictingOutput = new StringWriter();
        using var conflictingError = new StringWriter();
        var conflictingHandler = new MutationCommandHandler(
            conflictingService, conflictingOutput, conflictingError);
        CommandExitCode conflictingExit = await conflictingHandler.RunPatchAsync(
            CommandLine.Parse([
                "npc", "patch",
                "--edition", "skyrimse",
                "--input-plugin", @"K:\ExampleWorkspace\source.esp",
                "--output", @"K:\ExampleWorkspace\output.esp",
                "--form-id", "0x00000800",
                "--set-flag", "protected",
                "--input-sha", inputHash.Value,
                "--input-sha256", new string('b', 64),
                "--json"
            ]),
            CancellationToken.None);
        Assert(
            conflictingExit == CommandExitCode.UsageError &&
            conflictingService.Request is null &&
            conflictingError.ToString().Contains("must agree", StringComparison.Ordinal),
            "npc patch silently selected one of two conflicting input hash aliases.");
    }

    public static async Task TestHandlerMapsAllModes()
    {
        await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync();
        await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(appendMaster: true);
        string root = Path.Combine(
            Environment.CurrentDirectory, "artifacts", "cli-finish-core-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath workspace = new(root);
            var request = new SkyrimNpcFinishCoreRequest
            {
                Actor = new SkyrimNpcFinishCoreActor
                {
                    EditorId = new EditorId("TestNpc"),
                    FormId = new FormId(0x800)
                },
                AiPolicy = new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                    Energy = 50,
                    Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                    Mood = SkyrimNpcFinishCoreMood.Neutral
                },
                SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                {
                    Template = new FormReference(
                        new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512"
                },
                Output = new SkyrimNpcFinishCoreOutput
                {
                    PluginFileName = "TestNpc.esp"
                }
            };
            byte[] requestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, workspace);
            string requestPath = Path.Combine(root, "request.json");
            await File.WriteAllBytesAsync(requestPath, requestBytes);
            Sha256Hash requestHash = Hash(requestBytes);
            var proposal = new SkyrimNpcFinishCoreProposal
            {
                Request = request,
                RequestSha256 = requestHash,
                Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
            };
            Sha256Hash proposalHash = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(proposal, workspace));
            proposal = proposal with { ProposalSha256 = proposalHash };
            byte[] proposalBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(proposal, workspace);
            string proposalPath = Path.Combine(root, "proposal.json");
            await File.WriteAllBytesAsync(proposalPath, proposalBytes);
            string manifestPath = Path.Combine(root, "manifest.json");
            byte[] manifestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                new SkyrimNpcFinishCoreManifest(), workspace);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes);
            Sha256Hash manifestHash = Hash(manifestBytes);
            var fake = new FakeFinishCoreService(requestHash, proposalHash);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var handler = new SkyrimNpcFinishCoreCommandHandler(fake, workspace, output, error);

            CommandExitCode analyze = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze", "--request", requestPath,
                    "--request-sha256", requestHash.Value, "--proposal", Path.Combine(root, "new.json")]),
                CancellationToken.None);
            Assert(analyze == CommandExitCode.Success && fake.AnalyzeCalls == 1,
                "Finish Core CLI analyze did not route through the typed service: " +
                $"exit={analyze} calls={fake.AnalyzeCalls} out={output} err={error}");

            using var recordOutput = new StringWriter();
            using var recordError = new StringWriter();
            var recordHandler = new SkyrimNpcFinishCoreCommandHandler(
                new FakeFinishCoreService(
                    requestHash,
                    proposalHash,
                    new RecordException(null, typeof(object), null, "NPC_", "typed read failed")),
                workspace,
                recordOutput,
                recordError);
            CommandExitCode recordExit = await recordHandler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze",
                    "--request", requestPath,
                    "--request-sha256", requestHash.Value,
                    "--proposal", Path.Combine(root, "record-exception.json"),
                    "--json"]),
                CancellationToken.None);
            using JsonDocument recordResponse = JsonDocument.Parse(recordOutput.ToString());
            Assert(
                recordExit == CommandExitCode.ValidationFailure &&
                recordResponse.RootElement.GetProperty("code").GetString() == "finish-core-invalid",
                "Finish Core CLI did not classify Mutagen RecordException as invalid source data: " +
                $"exit={recordExit} out={recordOutput} err={recordError}");

            JsonObject legacyNode = JsonNode.Parse(requestBytes)!.AsObject();
            legacyNode["schema"] = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
            legacyNode["authorities"]!.AsObject().Remove("additionalMasters");
            legacyNode["aiPolicy"]!.AsObject().Remove("mood");
            byte[] legacyBytes = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
                JsonSerializer.SerializeToUtf8Bytes(legacyNode), workspace);
            string legacyRequestPath = Path.Combine(root, "legacy-request.json");
            await File.WriteAllBytesAsync(legacyRequestPath, legacyBytes);
            Sha256Hash legacyRequestHash = Hash(legacyBytes);
            string legacyProposalPath = Path.Combine(root, "legacy-proposal.json");
            using var legacyOutput = new StringWriter();
            using var legacyError = new StringWriter();
            var legacyHandler = new SkyrimNpcFinishCoreCommandHandler(
                fake, workspace, legacyOutput, legacyError);
            CommandExitCode legacyAnalyze = await legacyHandler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze",
                    "--request", legacyRequestPath,
                    "--request-sha256", legacyRequestHash.Value,
                    "--proposal", legacyProposalPath, "--json"]),
                CancellationToken.None);
            using JsonDocument legacyResponse = JsonDocument.Parse(
                legacyOutput.ToString());
            JsonElement legacyPayload = legacyResponse.RootElement;
            Assert(
                legacyAnalyze == CommandExitCode.Success &&
                legacyPayload.EnumerateObject().Select(item => item.Name).SequenceEqual([
                    "schema", "command", "proposed", "status", "proposalPath",
                    "proposalSha256", "diagnostics"]) &&
                legacyPayload.GetProperty("schema").GetString() ==
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier &&
                legacyPayload.GetProperty("command").GetString() ==
                    "npc finish analyze" &&
                legacyPayload.GetProperty("proposed").GetBoolean() &&
                legacyPayload.GetProperty("status").GetString() ==
                    SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite.ToString() &&
                legacyPayload.GetProperty("proposalPath").GetString() ==
                    legacyProposalPath &&
                legacyPayload.GetProperty("proposalSha256").GetString() ==
                    proposalHash.Value &&
                legacyPayload.GetProperty("diagnostics").GetArrayLength() == 0,
                "Legacy v1 Analyze changed the exact prior v2 CLI response envelope: " +
                legacyOutput);
            CommandExitCode validateAnalyze = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze", "--request", requestPath,
                    "--request-sha256", requestHash.Value, "--proposal", Path.Combine(root, "validation.json"),
                    "--validate-all"]),
                CancellationToken.None);
            Assert(
                validateAnalyze == CommandExitCode.Success &&
                fake.ValidateAnalyzeCalls == 1 &&
                !File.Exists(Path.Combine(root, "validation.json")),
                "Finish Core CLI did not route analyze --validate-all without publishing a proposal.");
            CommandExitCode apply = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "apply", "--request", requestPath,
                    "--request-sha256", requestHash.Value, "--proposal", proposalPath,
                    "--proposal-sha256", proposalHash.Value]),
                CancellationToken.None);
            Assert(apply == CommandExitCode.Success && fake.ApplyCalls == 1,
                "Finish Core CLI apply did not route through the typed service: " +
                $"exit={apply} calls={fake.ApplyCalls} out={output} err={error}");
            CommandExitCode validateApply = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "apply", "--request", requestPath,
                    "--request-sha256", requestHash.Value, "--proposal", proposalPath,
                    "--proposal-sha256", proposalHash.Value, "--validate-all"]),
                CancellationToken.None);
            Assert(
                validateApply == CommandExitCode.Success && fake.ValidateApplyCalls == 1,
                "Finish Core CLI did not route apply --validate-all.");
            using var invalidValidationOutput = new StringWriter();
            using var invalidValidationError = new StringWriter();
            var invalidValidationHandler = new SkyrimNpcFinishCoreCommandHandler(
                fake, workspace, invalidValidationOutput, invalidValidationError);
            CommandExitCode invalidValidation = await invalidValidationHandler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze", "--request", requestPath,
                    "--request-sha256", new string('f', 64), "--proposal", Path.Combine(root, "invalid-validation.json"),
                    "--validate-all", "--json"]),
                CancellationToken.None);
            using JsonDocument invalidValidationResponse = JsonDocument.Parse(
                invalidValidationOutput.ToString());
            Assert(
                invalidValidation == CommandExitCode.ValidationFailure &&
                !invalidValidationResponse.RootElement.GetProperty("valid").GetBoolean() &&
                invalidValidationResponse.RootElement.GetProperty("phases")
                    .EnumerateArray().Any(phase =>
                        phase.GetProperty("state").GetString() == "skipped") &&
                !File.Exists(Path.Combine(root, "invalid-validation.json")),
                "Finish Core CLI did not return phase-aware skipped diagnostics for a validate-all binding failure.");
            CommandExitCode wrongRequestHash = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "apply", "--request", requestPath,
                    "--request-sha256", new string('f', 64), "--proposal", proposalPath,
                    "--proposal-sha256", proposalHash.Value]),
                CancellationToken.None);
            Assert(
                wrongRequestHash == CommandExitCode.ValidationFailure &&
                fake.ApplyCalls == 1 &&
                error.ToString().Contains(
                    "expectedSha256=" + new string('f', 64), StringComparison.Ordinal) &&
                error.ToString().Contains("receivedSha256=", StringComparison.Ordinal),
                "Finish Core CLI stopped refusing a false request hash before apply or omitted hash evidence.");
            CommandExitCode verify = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "verify", "--manifest", manifestPath,
                    "--manifest-sha256", manifestHash.Value]),
                CancellationToken.None);
            Assert(verify == CommandExitCode.Success && fake.VerifyCalls == 1,
                "Finish Core CLI verify did not route through the typed service.");
            CommandExitCode unknown = await handler.RunAsync(
                CommandLine.Parse(["npc", "finish", "analyze", "--request", requestPath,
                    "--request-sha256", requestHash.Value, "--proposal", Path.Combine(root, "x.json"),
                    "--placement", "false"]), CancellationToken.None);
            Assert(unknown == CommandExitCode.UsageError,
                "Finish Core CLI accepted an undeclared placement option.");

            await FinishCoreExternalSmpCliTests.RunAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    public static async Task TestSchemaExportPublishesDocumentContracts()
    {
        string workspaceRoot = RepositoryRoot();
        string testRoot = Path.Combine(
            workspaceRoot,
            "artifacts",
            "finish-core-schema-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var workspace = new WorkspacePath(workspaceRoot);
            var analyzeOutputPath = new WorkspacePath(Path.Combine(
                testRoot,
                "npc-finish-analyze.schema.json"));
            var applyOutputPath = new WorkspacePath(Path.Combine(
                testRoot,
                "npc-finish-apply.schema.json"));
            var patchOutputPath = new WorkspacePath(Path.Combine(
                testRoot,
                "npc-patch.schema.json"));
            var policy = new KOnlyWorkspacePolicy(
                workspace,
                new WorkspacePath(@"F:\ExampleGame"));
            var service = new SchemaExportService(policy, workspace);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var handler = new SchemaExportCommandHandler(service, output, error);

            CommandExitCode analyzeExit = await handler.RunAsync(
                CommandLine.Parse([
                    "schema", "export",
                    "--command", "npc finish analyze",
                    "--output", analyzeOutputPath.Value,
                    "--json"
                ]),
                CancellationToken.None);
            CommandExitCode applyExit = await handler.RunAsync(
                CommandLine.Parse([
                    "schema", "export",
                    "--command", "npc finish apply",
                    "--output", applyOutputPath.Value,
                    "--json"
                ]),
                CancellationToken.None);
            CommandExitCode patchExit = await handler.RunAsync(
                CommandLine.Parse([
                    "schema", "export",
                    "--command", "npc patch",
                    "--output", patchOutputPath.Value,
                    "--json"
                ]),
                CancellationToken.None);

            Assert(
                analyzeExit == CommandExitCode.Success &&
                applyExit == CommandExitCode.Success &&
                patchExit == CommandExitCode.Success &&
                File.Exists(analyzeOutputPath.Value) &&
                File.Exists(applyOutputPath.Value) &&
                File.Exists(patchOutputPath.Value),
                "Finish Core and npc patch schema exports did not write all artifacts: " +
                $"analyze={analyzeExit} apply={applyExit} patch={patchExit} out={output} err={error}");
            using JsonDocument analyzeDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(analyzeOutputPath.Value));
            using JsonDocument applyDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(applyOutputPath.Value));
            using JsonDocument patchDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(patchOutputPath.Value));
            JsonElement analyzeCommand = analyzeDocument.RootElement
                .GetProperty("commands")[0];
            JsonElement applyCommand = applyDocument.RootElement
                .GetProperty("commands")[0];
            JsonElement patchCommand = patchDocument.RootElement
                .GetProperty("commands")[0];
            Assert(
                analyzeCommand.GetProperty("name").GetString() ==
                    "npc finish analyze" &&
                applyCommand.GetProperty("name").GetString() ==
                    "npc finish apply",
                "Finish Core schema exports mislabeled their commands.");
            JsonElement patchSchemas = patchCommand.GetProperty("documentSchemas");
            JsonElement patchProposal = patchSchemas.EnumerateArray().Single(item =>
                item.GetProperty("schemaIdentifier").GetString() == NpcMutationProposal.SchemaIdentifier);
            JsonElement patchProposalSchema = patchProposal.GetProperty("jsonSchema");
            Assert(
                patchCommand.GetProperty("name").GetString() == "npc patch" &&
                patchSchemas.EnumerateArray().Select(item => item.GetProperty("schemaIdentifier").GetString())
                    .SequenceEqual(["npc.whole-skin.request.v1", "npc.patch.proposal.v2", NpcMutationProposal.SchemaIdentifier]) &&
                patchProposal.GetProperty("name").GetString() == "proposal" &&
                patchProposal.GetProperty("direction").GetString() == "output" &&
                patchProposal.GetProperty("schemaIdentifier").GetString() ==
                    NpcMutationProposal.SchemaIdentifier &&
                RequiredMembers(patchProposalSchema).SequenceEqual([
                    "schemaVersion", "edition", "inputPlugin", "outputPlugin",
                    "targetFormId", "inputSha256", "changes", "preservedFields"
                ]) &&
                patchProposalSchema.GetProperty("properties")
                    .GetProperty("inputSha256")
                    .GetProperty("description").GetString()!
                    .Contains("--input-sha", StringComparison.Ordinal),
                "npc patch schema export did not publish its hash-bound proposal contract.");
            Assert(
                analyzeCommand.TryGetProperty(
                    "documentSchemas",
                    out JsonElement analyzeSchemas),
                "Finish Core analyze schema export omitted documentSchemas.");
            Assert(
                applyCommand.TryGetProperty(
                    "documentSchemas",
                    out JsonElement applySchemas),
                "Finish Core apply schema export omitted documentSchemas.");
            Assert(
                analyzeSchemas.GetArrayLength() == 9 &&
                applySchemas.GetArrayLength() == 9 &&
                analyzeSchemas.EnumerateArray()
                    .Select(item => (
                        Name: item.GetProperty("name").GetString()!,
                        Direction: item.GetProperty("direction").GetString()!,
                        SchemaIdentifier: item.GetProperty("schemaIdentifier").GetString()!))
                    .SequenceEqual([
                        ("request-legacy", "input", "npc.finish-core.request.v1"),
                        ("request", "input", "npc.finish-core.request.v2"),
                        ("request-external", "input", "npc.finish-core.request.v3"),
                        ("request-policy", "input", "npc.finish-core.request.v4"),
                        ("proposal-legacy", "output", "npc.finish-core.proposal.v1"),
                        ("proposal", "output", "npc.finish-core.proposal.v2"),
                        ("proposal-external", "output", "npc.finish-core.proposal.v3"),
                        ("proposal-policy", "output", "npc.finish-core.proposal.v4"),
                        ("validation", "output", "npc.finish-core.validation.v1")
                    ]) &&
                applySchemas.EnumerateArray()
                    .Select(item => (
                        Name: item.GetProperty("name").GetString()!,
                        Direction: item.GetProperty("direction").GetString()!,
                        SchemaIdentifier: item.GetProperty("schemaIdentifier").GetString()!))
                    .SequenceEqual([
                        ("request-legacy", "input", "npc.finish-core.request.v1"),
                        ("request", "input", "npc.finish-core.request.v2"),
                        ("request-external", "input", "npc.finish-core.request.v3"),
                        ("request-policy", "input", "npc.finish-core.request.v4"),
                        ("proposal-legacy", "input", "npc.finish-core.proposal.v1"),
                        ("proposal", "input", "npc.finish-core.proposal.v2"),
                        ("proposal-external", "input", "npc.finish-core.proposal.v3"),
                        ("proposal-policy", "input", "npc.finish-core.proposal.v4"),
                        ("validation", "output", "npc.finish-core.validation.v1")
                    ]),
                "Finish Core commands must export the exact nine-row versioned schema catalog.");

            JsonElement request = analyzeSchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "request");
            JsonElement proposal = analyzeSchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "proposal");
            JsonElement applyRequest = applySchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "request");
            JsonElement applyProposal = applySchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "proposal");
            JsonElement validation = analyzeSchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "validation");
            JsonElement applyValidation = applySchemas.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "validation");
            Assert(
                request.GetProperty("direction").GetString() == "input" &&
                request.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreRequest.SchemaIdentifier &&
                proposal.GetProperty("direction").GetString() == "output" &&
                proposal.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier &&
                applyRequest.GetProperty("direction").GetString() == "input" &&
                applyRequest.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreRequest.SchemaIdentifier &&
                applyProposal.GetProperty("direction").GetString() == "input" &&
                applyProposal.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier &&
                validation.GetProperty("direction").GetString() == "output" &&
                validation.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreValidationResult.SchemaIdentifier &&
                applyValidation.GetProperty("direction").GetString() == "output" &&
                applyValidation.GetProperty("schemaIdentifier").GetString() ==
                    SkyrimNpcFinishCoreValidationResult.SchemaIdentifier,
                "Finish Core schema export mislabeled request or proposal authority.");
            Assert(
                JsonElement.DeepEquals(
                    request.GetProperty("jsonSchema"),
                    applyRequest.GetProperty("jsonSchema")) &&
                JsonElement.DeepEquals(
                    proposal.GetProperty("jsonSchema"),
                    applyProposal.GetProperty("jsonSchema")) &&
                JsonElement.DeepEquals(
                    validation.GetProperty("jsonSchema"),
                    applyValidation.GetProperty("jsonSchema")),
                "Finish Core analyze/apply schemas drifted from one another.");
            Assert(
                EnumValues(validation.GetProperty("jsonSchema")
                    .GetProperty("properties")
                    .GetProperty("phases")
                    .GetProperty("items")
                    .GetProperty("properties")
                    .GetProperty("state"))
                    .SequenceEqual(["reached", "skipped"]),
                "Finish Core validate-all schema omitted reached/skipped phase state.");

            JsonElement requestSchema = request.GetProperty("jsonSchema");
            string[] topLevelRequired = requestSchema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            Assert(
                topLevelRequired.SequenceEqual([
                    "schema",
                    "source",
                    "actor",
                    "authorities",
                    "followerPolicy",
                    "outfitPolicy",
                    "inventoryPolicy",
                    "sandboxAuthority",
                    "output",
                    "aiPolicy"
                ]),
                "Finish Core request schema did not publish the ten required v2 top-level members.");
            JsonElement properties = requestSchema.GetProperty("properties");
            JsonElement aiPolicy = properties.GetProperty("aiPolicy");
            JsonElement aiProperties = aiPolicy.GetProperty("properties");
            Assert(
                RequiredMembers(aiPolicy).SequenceEqual([
                    "aggression", "confidence", "energy", "morality", "assistance", "mood"
                ]) &&
                EnumValues(aiProperties.GetProperty("aggression")).SequenceEqual([
                    "Unaggressive", "Aggressive", "VeryAggressive", "Frenzied"
                ]) &&
                EnumValues(aiProperties.GetProperty("confidence")).SequenceEqual([
                    "Cowardly", "Cautious", "Average", "Brave", "Foolhardy"
                ]) &&
                aiProperties.GetProperty("energy").GetProperty("minimum").GetInt32() == 0 &&
                aiProperties.GetProperty("energy").GetProperty("maximum").GetInt32() == 100 &&
                EnumValues(aiProperties.GetProperty("morality")).SequenceEqual([
                    "AnyCrime", "ViolenceAgainstEnemies", "PropertyCrimeOnly", "NoCrime"
                ]) &&
                EnumValues(aiProperties.GetProperty("assistance")).SequenceEqual([
                    "HelpsNobody", "HelpsAllies", "HelpsFriendsAndAllies"
                ]) &&
                EnumValues(aiProperties.GetProperty("mood")).SequenceEqual([
                    "Neutral", "Angry", "Fear", "Happy", "Sad", "Surprise",
                    "Puzzled", "Disgusted"
                ]),
                "Finish Core request schema did not publish the complete v2 AIDT authoring contract.");
            JsonElement source = properties.GetProperty("source");
            JsonElement sourceProperties = source.GetProperty("properties");
            JsonElement actor = properties.GetProperty("actor");
            JsonElement actorProperties = actor.GetProperty("properties");
            Assert(
                RequiredMembers(source).SequenceEqual([
                    "packageRoot",
                    "packageManifest",
                    "packageManifestSha256",
                    "packageTreeSha256",
                    "pluginPath",
                    "plugin",
                    "pluginSha256"
                ]) &&
                RequiredMembers(actor).SequenceEqual(["editorId", "formId"]),
                "Finish Core request schema did not require the source and actor bindings used by analyze.");
            Assert(
                Reference(sourceProperties, "packageRoot") == "#/$defs/path" &&
                Reference(sourceProperties, "packageManifest") == "#/$defs/path" &&
                Reference(sourceProperties, "packageManifestSha256") == "#/$defs/sha256" &&
                Reference(sourceProperties, "packageTreeSha256") == "#/$defs/sha256" &&
                Reference(sourceProperties, "pluginPath") == "#/$defs/path" &&
                Reference(sourceProperties, "plugin") == "#/$defs/plugin" &&
                Reference(sourceProperties, "pluginSha256") == "#/$defs/sha256" &&
                Reference(actorProperties, "editorId") == "#/$defs/editorId" &&
                Reference(actorProperties, "formId") == "#/$defs/formId",
                "Finish Core request schema still permits null for a runtime-required source or actor binding.");
            JsonElement authorityProperties = properties.GetProperty("authorities")
                .GetProperty("properties");
            JsonElement providers = authorityProperties.GetProperty("providers");
            JsonElement provider = providers.GetProperty("items");
            JsonElement providerProperties = provider.GetProperty("properties");
            Assert(
                Reference(authorityProperties, "actorAssemblySha256") ==
                    "#/$defs/nullableSha256" &&
                Reference(authorityProperties, "bodyOwnerSha256") ==
                    "#/$defs/nullableSha256" &&
                Reference(authorityProperties, "protectedAppearanceTreeSha256") ==
                    "#/$defs/nullableSha256",
                "Finish Core schema incorrectly made optional evidence hashes mandatory.");
            Assert(
                sourceProperties.GetProperty("packageRoot")
                    .GetProperty("description").GetString()!
                    .Contains("workspace-root-relative", StringComparison.OrdinalIgnoreCase) &&
                sourceProperties.GetProperty("packageManifest")
                    .GetProperty("description").GetString()!
                    .Contains("direct child of packageRoot", StringComparison.Ordinal) &&
                sourceProperties.GetProperty("pluginPath")
                    .GetProperty("description").GetString()!
                    .Contains("inside packageRoot", StringComparison.Ordinal) &&
                sourceProperties.GetProperty("plugin")
                    .GetProperty("description").GetString()!
                    .Contains("bare safe filename", StringComparison.OrdinalIgnoreCase),
                "Finish Core schema did not publish the workspace-relative source path conventions.");
            Assert(
                providers.GetProperty("minItems").GetInt32() == 1 &&
                RequiredMembers(provider).SequenceEqual([
                    "plugin",
                    "path",
                    "sha256",
                    "byteLength"
                ]) &&
                Reference(providerProperties, "plugin") == "#/$defs/plugin" &&
                Reference(providerProperties, "path") == "#/$defs/path" &&
                Reference(providerProperties, "sha256") == "#/$defs/sha256" &&
                providerProperties.GetProperty("byteLength")
                    .GetProperty("minimum").GetInt64() == 1 &&
                providerProperties.GetProperty("path")
                    .GetProperty("description").GetString()!
                    .Contains("inside source.packageRoot", StringComparison.Ordinal) &&
                providerProperties.GetProperty("plugin")
                    .GetProperty("description").GetString()!
                    .Contains("bare safe filename", StringComparison.OrdinalIgnoreCase),
                "Finish Core schema did not publish the nonempty, fully bound provider contract.");
            Assert(
                sourceProperties.GetProperty("packageTreeSha256")
                    .GetProperty("description").GetString() ==
                "SHA-256 of UTF-8 bytes formed from every recursively enumerated file under packageRoot, including packageManifest. For each file, replace '\\' with '/' in its relative path and emit <relativePath>|<byteLength>|<uppercaseFileSha256> followed by LF. Sort rows ordinally by relativePath before concatenation. Reparse-point files are refused.",
                "Finish Core schema did not publish the reproducible package-tree hash derivation.");
            Assert(requestSchema.GetProperty("canonicalization").GetString()!
                    .Contains("raw", StringComparison.Ordinal) &&
                requestSchema.GetProperty("canonicalization").GetString()!
                    .Contains("lowercase", StringComparison.Ordinal) &&
                requestSchema.GetProperty("digestRule").GetProperty("source.packageTreeSha256")
                    .GetString()!.Contains("including packageManifest", StringComparison.Ordinal),
                "Finish Core schema must export canonical document admission and the named source digest domain.");
            Assert(requestSchema.GetProperty("digestRule").GetProperty("manifest.packageTreeSha256")
                    .GetString()!.Contains("npc.finish-core.output-tree.v2", StringComparison.Ordinal),
                "Finish output digest discovery must publish its domain prefix to distinguish legacy packages.");
            Assert(
                EnumValues(properties.GetProperty("authorities")
                    .GetProperty("properties")
                    .GetProperty("bodyRoute"))
                    .SequenceEqual(["Cbbe3Ba", "Cotr", "Ube"]) &&
                EnumValues(properties.GetProperty("outfitPolicy")
                    .GetProperty("properties")
                    .GetProperty("policy"))
                    .SequenceEqual(["ExistingOutfit", "PrivateOutfit"]) &&
                EnumValues(properties.GetProperty("inventoryPolicy")
                    .GetProperty("properties")
                    .GetProperty("policy"))
                    .SequenceEqual(["PreserveInventory", "ReplaceExactInventory"]),
                "Finish Core request schema omitted or drifted a closed enum set.");
            JsonElement defensiveOnly = properties
                .GetProperty("followerPolicy")
                .GetProperty("properties")
                .GetProperty("defensiveOnly");
            Assert(
                defensiveOnly.GetProperty("const").GetBoolean() &&
                defensiveOnly.GetProperty("description").GetString()!
                    .Contains("zero-offense defensive combat style", StringComparison.Ordinal),
                "Finish Core request schema did not publish defensiveOnly=true as a closed contract.");
            Assert(
                EnumValues(proposal.GetProperty("jsonSchema")
                    .GetProperty("properties")
                    .GetProperty("status"))
                    .SequenceEqual([
                        "ReadyForReviewedWrite",
                        "NoChanges",
                        "Refused",
                        "StaticPassRuntimeRequired"
                ]),
                "Finish Core proposal schema omitted or drifted the status enum set.");
            await TestCurrentAndLegacyFinishCoreFixtures();
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static Task TestCurrentAndLegacyFinishCoreFixtures()
    {
        WorkspacePath workspace = new(@"K:\ExampleWorkspace");
        var request = new SkyrimNpcFinishCoreRequest
        {
            Source = new SkyrimNpcFinishCoreSource
            {
                Plugin = new PluginName("Fixture.esp")
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("FixtureActor"),
                FormId = new FormId(0x800)
            },
            AiPolicy = new SkyrimNpcFinishCoreAiPolicy
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                Template = new FormReference(
                    new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                PluginFileName = "Fixture.esp"
            }
        };
        byte[] currentBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            request, workspace);
        JsonObject current = JsonNode.Parse(currentBytes)!.AsObject();
        Assert(
            current["schema"]!.GetValue<string>() ==
                SkyrimNpcFinishCoreRequest.SchemaIdentifier &&
            current["authorities"]!.AsObject().ContainsKey("additionalMasters") &&
            current["aiPolicy"]!.AsObject()["mood"]!.GetValue<string>() == "Neutral",
            "CLI current-document fixture did not publish v2 authority and mood members.");

        JsonObject legacy = current.DeepClone().AsObject();
        legacy["schema"] = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        legacy["authorities"]!.AsObject().Remove("additionalMasters");
        legacy["aiPolicy"]!.AsObject().Remove("mood");
        byte[] legacyCanonical = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
            JsonSerializer.SerializeToUtf8Bytes(legacy), workspace);
        SkyrimNpcFinishCoreRequest parsedLegacy =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(legacyCanonical, workspace);
        byte[] replay = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            parsedLegacy, workspace);
        Assert(
            parsedLegacy.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            replay.SequenceEqual(legacyCanonical),
            "CLI legacy fixture did not replay to the exact retained v1 canonical bytes.");

        var proposal = new SkyrimNpcFinishCoreProposal
        {
            Schema = SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
            Request = parsedLegacy,
            Status = SkyrimNpcFinishCoreStatus.NoChanges
        };
        JsonObject proposalJson = JsonNode.Parse(
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(proposal, workspace))!
            .AsObject();
        Assert(
            proposalJson["schema"]!.GetValue<string>() ==
                SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
            proposalJson["request"]!["schema"]!.GetValue<string>() ==
                SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            !proposalJson["request"]!["authorities"]!.AsObject()
                .ContainsKey("additionalMasters") &&
            !proposalJson["request"]!["aiPolicy"]!.AsObject().ContainsKey("mood"),
            "CLI legacy proposal fixture injected v2 request members.");
        return Task.CompletedTask;
    }

    public static async Task TestEnumRefusalPublishesAdmittedValues()
    {
        string workspaceRoot = RepositoryRoot();
        string testRoot = Path.Combine(
            workspaceRoot,
            "artifacts",
            "finish-core-enum-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                source = new
                {
                    packageRoot = "source",
                    packageManifest = "source/manifest.json",
                    packageManifestSha256 = new string('a', 64),
                    packageTreeSha256 = new string('b', 64),
                    pluginPath = "source/Test.esp",
                    plugin = "Test.esp",
                    pluginSha256 = new string('c', 64)
                },
                actor = new
                {
                    editorId = "TestActor",
                    formId = "0x00000800"
                },
                authorities = new
                {
                    bodyRoute = "Cbbe3Ba",
                    providers = Array.Empty<object>(),
                    additionalMasters = Array.Empty<object>()
                },
                followerPolicy = new
                {
                    recruitable = true,
                    defensiveOnly = true,
                    relationshipRank = "Ally"
                },
                aiPolicy = new
                {
                    aggression = "Unaggressive",
                    confidence = "Brave",
                    energy = 50,
                    morality = "NoCrime",
                    assistance = "HelpsFriendsAndAllies",
                    mood = "Neutral"
                },
                outfitPolicy = new
                {
                    policy = "PrivateOutfit",
                    armorItems = Array.Empty<string>()
                },
                inventoryPolicy = new
                {
                    policy = "Unknown",
                    expectedSourceItems = Array.Empty<string>(),
                    desiredItems = Array.Empty<string>()
                },
                sandboxAuthority = new
                {
                    template = "Skyrim.esm|0x0001B217",
                    templateEditorId = "DefaultSandboxEditorLocation512"
                },
                output = new
                {
                    pluginFileName = "Test.esp"
                }
            });
            string requestPath = Path.Combine(testRoot, "request.json");
            await File.WriteAllBytesAsync(requestPath, requestBytes);
            Sha256Hash requestHash = Hash(requestBytes);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var handler = new SkyrimNpcFinishCoreCommandHandler(
                new FakeFinishCoreService(requestHash, requestHash),
                new WorkspacePath(workspaceRoot),
                output,
                error);

            CommandExitCode exit = await handler.RunAsync(
                CommandLine.Parse([
                    "npc", "finish", "analyze",
                    "--request", requestPath,
                    "--request-sha256", requestHash.Value,
                    "--proposal", Path.Combine(testRoot, "proposal.json"),
                    "--json"
                ]),
                CancellationToken.None);

            using JsonDocument response = JsonDocument.Parse(output.ToString());
            Assert(
                exit == CommandExitCode.ValidationFailure &&
                response.RootElement.GetProperty("code").GetString() ==
                    "finish-core-invalid" &&
                response.RootElement.GetProperty("message").GetString() ==
                    "Finish Core enum 'policy' is invalid; expected one of [PreserveInventory, ReplaceExactInventory].",
                "Finish Core CLI enum refusal did not publish the admitted values: " +
                $"exit={exit} out={output} err={error}");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string[] EnumValues(JsonElement schema) =>
        schema.GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

    private static string[] RequiredMembers(JsonElement schema) =>
        schema.TryGetProperty("required", out JsonElement required)
            ? required.EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray()
            : [];

    private static string Reference(JsonElement properties, string name) =>
        properties.GetProperty(name).GetProperty("$ref").GetString()!;

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Actorwright.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Actorwright repository root for schema tests.");
    }

    private sealed class FakeFinishCoreService(
        Sha256Hash requestHash,
        Sha256Hash proposalHash,
        Exception? analyzeException = null)
        : ISkyrimNpcFinishCoreService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int ValidateAnalyzeCalls { get; private set; }
        public int ValidateApplyCalls { get; private set; }

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAnalyzeCalls++;
            return ValueTask.FromResult(ValidValidation());
        }

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateApplyCalls++;
            return ValueTask.FromResult(ValidValidation());
        }

        public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            WorkspacePath proposalPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (analyzeException is not null)
                throw analyzeException;
            AnalyzeCalls++;
            return ValueTask.FromResult(new SkyrimNpcFinishCoreProposalResult(
                true,
                new SkyrimNpcFinishCoreProposal
                {
                    Schema = request.Schema ==
                        SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier
                        ? SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier
                        : SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    Request = request,
                    RequestSha256 = requestHash,
                    ProposalSha256 = proposalHash,
                    Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
                },
                proposalPath,
                proposalHash,
                ImmutableArray<Diagnostic>.Empty));
        }

        public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            return ValueTask.FromResult(new SkyrimNpcFinishCoreApplyResult(
                true, new SkyrimNpcFinishCoreManifest(), null, null,
                ImmutableArray<Diagnostic>.Empty));
        }

        public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
            WorkspacePath manifestPath, Sha256Hash manifestSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(new SkyrimNpcFinishCoreVerificationResult(
                true,
                new SkyrimNpcFinishCoreVerification
                {
                    Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
                    Verified = true
                },
                ImmutableArray<Diagnostic>.Empty));
        }

        private static SkyrimNpcFinishCoreValidationResult ValidValidation() =>
            new(
                true,
                [new SkyrimNpcFinishCoreValidationPhase(
                    "request-binding",
                    SkyrimNpcFinishCoreValidationPhaseState.Reached,
                    ImmutableArray<Diagnostic>.Empty)],
                ImmutableArray<Diagnostic>.Empty);
    }

    private sealed class FakeMutationService : INpcMutationService
    {
        public NpcMutationRequest? Request { get; private set; }

        public ValueTask<NpcMutationProposal> AnalyzeAsync(
            NpcMutationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(new NpcMutationProposal(
                request.Edition,
                request.InputPlugin,
                request.OutputPlugin,
                request.TargetFormId,
                request.ExpectedInputHash ?? new Sha256Hash(new string('0', 64)),
                [new MutationChange("Flags", null, "protected")],
                ["AIDT"],
                ImmutableArray<Diagnostic>.Empty));
        }

        public ValueTask<NpcMutationResult> ApplyAsync(
            NpcMutationRequest request,
            NpcMutationProposal proposal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PluginVerificationResult> VerifyAsync(
            PluginVerificationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
