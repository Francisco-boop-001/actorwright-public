using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2PresetInspectTests
{
    private const string SeedRequestDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly JsonSerializerOptions CompactReceiptOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    public static async Task RunAsync()
    {
        string rootText = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            $"protocol-v2-preset-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootText);
        try
        {
            byte[] sourceBytes = Encoding.UTF8.GetBytes(
                "{\"actor\":{\"weight\":50},\"fixtureWarning\":true}");
            string inputText = Path.Combine(rootText, "fixture.jslot");
            string receiptText = Path.Combine(rootText, "inspection.json");
            await File.WriteAllBytesAsync(inputText, sourceBytes);
            string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            var root = new WorkspacePath(rootText);
            var service = new PresetService(
                new KOnlyWorkspacePolicy(
                    root,
                    new WorkspacePath(rootText + "-protected-live")),
                root);
            var store = new PresetInspectionArtifactStore(root);
            var workflowLifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(
                    new KOnlyWorkspacePolicy(
                        root,
                        new WorkspacePath(rootText + "-protected-live")),
                    root));
            AgentWorkflowBundleTransition workflowSeed = CreateWorkflowSeed(
                rootText,
                workflowLifecycle);
            var adapter = new ProtocolV2PresetInspectAdapter(
                root,
                service,
                store,
                workflowLifecycle);

            AssertRegistryAndSchema();
            AssertVersionedReceiptPersistence(rootText);
            await AssertCanonicalFailuresStayTyped(
                root,
                rootText,
                store);
            await AssertV1InspectUnchanged(service, inputText, sourceHash);
            await AssertExactServiceHashBinding(
                service,
                inputText,
                sourceHash);
            await AssertPathAndStageAdmission(
                root,
                rootText,
                inputText,
                sourceHash,
                service,
                store,
                workflowLifecycle,
                workflowSeed);
            await AssertSuccess(
                inputText,
                sourceHash,
                receiptText,
                store,
                adapter,
                workflowLifecycle,
                workflowSeed,
                new WorkspacePath(Path.Combine(rootText, "workflow-preset.json")));
            await AssertStaleHashRefused(
                rootText,
                inputText,
                adapter,
                workflowSeed);
            await AssertOutputRefusals(
                root,
                rootText,
                inputText,
                sourceHash,
                receiptText,
                service,
                adapter,
                workflowLifecycle,
                workflowSeed);
            await AssertCancellationCleansTemporary(
                root,
                inputText,
                sourceHash,
                service,
                store);
        }
        finally
        {
            if (Directory.Exists(rootText))
                Directory.Delete(rootText, recursive: true);
        }
    }

    private static async Task AssertExactServiceHashBinding(
        PresetService service,
        string inputText,
        string admittedSha256)
    {
        byte[] mismatchedBytes = Encoding.UTF8.GetBytes(
            "{\"actor\":{\"weight\":51}}");
        PresetParseResult result = await service.InspectExactAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(inputText)),
            mismatchedBytes.AsMemory(),
            new Sha256Hash(admittedSha256),
            CancellationToken.None);

        Assert(result.Document is null &&
               result.Diagnostics.Any(item =>
                   item.Code == "preset-exact-bytes-hash-mismatch" &&
                   item.Severity == DiagnosticSeverity.Error),
            "exact preset inspection admitted bytes with a different digest");
    }

    private static async Task AssertPathAndStageAdmission(
        WorkspacePath root,
        string rootText,
        string inputText,
        string sourceHash,
        IPresetExactInspectionService service,
        PresetInspectionArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed)
    {
        string occupiedReceipt = Path.Combine(
            rootText,
            "binding-occupied-receipt.json");
        string occupiedWorkflow = Path.Combine(
            rootText,
            "binding-occupied-workflow.json");
        File.WriteAllText(occupiedReceipt, "occupied");
        File.WriteAllText(occupiedWorkflow, "occupied");

        string reparseInput = Path.Combine(rootText, "reparse-input.jslot");
        File.Copy(inputText, reparseInput);
        var cases = new[]
        {
            new PathAdmissionCase(
                "malformed-input", "input", inputText + '\0',
                ProtocolV2DiagnosticCodes.UnsafePathForm, null),
            new PathAdmissionCase(
                "malformed-inspection-output", "inspection-output",
                Path.Combine(rootText, "malformed-receipt.json") + '\0',
                ProtocolV2DiagnosticCodes.UnsafePathForm, null),
            new PathAdmissionCase(
                "malformed-workflow-bundle", "workflow-bundle",
                workflowSeed.Document.Path.Value + '\0',
                ProtocolV2DiagnosticCodes.UnsafePathForm, null),
            new PathAdmissionCase(
                "malformed-workflow-output", "workflow-output",
                Path.Combine(rootText, "malformed-workflow.json") + '\0',
                ProtocolV2DiagnosticCodes.UnsafePathForm, null),
            new PathAdmissionCase(
                "input-pair-missing-hash", "input-sha256", null,
                ProtocolV2DiagnosticCodes.OptionRequired, null),
            new PathAdmissionCase(
                "workflow-pair-missing-hash", "workflow-bundle-sha256", null,
                ProtocolV2DiagnosticCodes.OptionRequired, null),
            new PathAdmissionCase(
                "input-hash-not-uppercase", "input-sha256",
                sourceHash.ToLowerInvariant(),
                ProtocolV2DiagnosticCodes.OptionSha256Value, null),
            new PathAdmissionCase(
                "workflow-hash-not-uppercase", "workflow-bundle-sha256",
                workflowSeed.Document.Sha256.ToLowerInvariant(),
                ProtocolV2DiagnosticCodes.OptionSha256Value, null),
            new PathAdmissionCase(
                "input-ads", "input", inputText + ":stream",
                ProtocolV2DiagnosticCodes.AlternateDataStreamRefused, null),
            new PathAdmissionCase(
                "input-reparse", "input", reparseInput,
                ProtocolV2DiagnosticCodes.ReparsePointRefused,
                new ReparseInspector(reparseInput)),
            new PathAdmissionCase(
                "occupied-inspection-output", "inspection-output",
                occupiedReceipt,
                ProtocolV2DiagnosticCodes.PresetInspectionOutputExists, null),
            new PathAdmissionCase(
                "occupied-workflow-output", "workflow-output",
                occupiedWorkflow,
                ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused, null)
        };

        foreach (PathAdmissionCase item in cases)
        {
            var counting = new CountingInspectionService(service);
            ParsedCommand command = ParseV2(
                rootText,
                inputText,
                sourceHash,
                Path.Combine(rootText, $"{item.Name}-receipt.json"),
                workflowSeed,
                Path.Combine(rootText, $"{item.Name}-workflow.json"));
            command = item.Value is null
                ? command with
                {
                    Options = command.Options.Remove(item.Option)
                }
                : command with
                {
                    Options = command.Options.SetItem(item.Option, item.Value)
                };
            FaceGeomHairRegionsWorkspaceBoundary? boundary =
                item.Inspector is null
                    ? null
                    : new FaceGeomHairRegionsWorkspaceBoundary(
                        root,
                        item.Inspector);
            ProtocolV2PresetInspectAdapter adapter = CreateAdapter(
                root,
                counting,
                store,
                workflowLifecycle: null,
                boundary);
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [adapter],
                AgentCommandRegistry.All);
            CommandExitCode exit = await runner.RunAsync(
                command with
                {
                    Json = true,
                    RequestedProtocol = "2"
                },
                CancellationToken.None);
            using JsonDocument envelope = JsonDocument.Parse(
                output.ToString().Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries).Single());
            JsonElement[] diagnostics = envelope.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .ToArray();
            Assert(exit != CommandExitCode.Success &&
                   diagnostics.Length == 1 &&
                   diagnostics[0].GetProperty("code").GetString() ==
                       item.ExpectedCode &&
                   diagnostics[0].GetProperty("class").GetString() is
                       "usage" or "security" &&
                   diagnostics[0].GetProperty("message").GetString()!
                       .Contains($"--{item.Option}", StringComparison.Ordinal) &&
                   diagnostics.All(diagnostic =>
                       diagnostic.GetProperty("code").GetString() !=
                       ProtocolV2DiagnosticCodes.ProtocolOperationFailed) &&
                   envelope.RootElement.GetProperty("artifacts")
                       .GetArrayLength() == 0 &&
                   counting.Calls == 0,
                $"{item.Name} did not fail at the typed pre-service path boundary: {output}");
        }

        string workflowEqualInput = Path.Combine(
            rootText,
            "WorkflowEqual.JSON");
        string workflowEqualOutput = Path.Combine(
            rootText,
            "workflowequal.json");
        string workflowOuter = Path.Combine(
            rootText,
            "workflow-outer");
        string workflowInnerInput = Path.Combine(
            workflowOuter,
            "bundle.json");
        string workflowInputOuter = Path.Combine(
            rootText,
            "Workflow-Input-Outer");
        string workflowOutputInner = Path.Combine(
            rootText,
            "workflow-input-outer",
            "bundle.json");
        string inspectionEqualInput = Path.Combine(
            rootText,
            "InspectionEqual.JSLOT");
        string inspectionEqualOutput = Path.Combine(
            rootText,
            "inspectionequal.jslot");
        string inspectionOuter = Path.Combine(
            rootText,
            "inspection-outer");
        string inspectionInnerInput = Path.Combine(
            inspectionOuter,
            "fixture.jslot");
        string inspectionInputOuter = Path.Combine(
            rootText,
            "Inspection-Input-Outer");
        string inspectionOutputInner = Path.Combine(
            rootText,
            "inspection-input-outer",
            "receipt.json");
        var overlapCases = new[]
        {
            new OverlapAdmissionCase(
                "workflow-case-insensitive-equality",
                "workflow-bundle",
                "workflow-output",
                [
                    ("workflow-bundle", workflowEqualInput),
                    ("workflow-output", workflowEqualOutput)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [workflowEqualInput],
                    [])),
            new OverlapAdmissionCase(
                "workflow-input-under-output",
                "workflow-bundle",
                "workflow-output",
                [
                    ("workflow-bundle", workflowInnerInput),
                    ("workflow-output", workflowOuter)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [workflowInnerInput],
                    [])),
            new OverlapAdmissionCase(
                "workflow-output-under-input",
                "workflow-bundle",
                "workflow-output",
                [
                    ("workflow-bundle", workflowInputOuter),
                    ("workflow-output", workflowOutputInner)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [workflowInputOuter],
                    [Path.GetDirectoryName(workflowOutputInner)!])),
            new OverlapAdmissionCase(
                "inspection-case-insensitive-equality",
                "input",
                "inspection-output",
                [
                    ("input", inspectionEqualInput),
                    ("inspection-output", inspectionEqualOutput)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [inspectionEqualInput],
                    [])),
            new OverlapAdmissionCase(
                "inspection-input-under-output",
                "input",
                "inspection-output",
                [
                    ("input", inspectionInnerInput),
                    ("inspection-output", inspectionOuter)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [inspectionInnerInput],
                    [])),
            new OverlapAdmissionCase(
                "inspection-output-under-input",
                "input",
                "inspection-output",
                [
                    ("input", inspectionInputOuter),
                    ("inspection-output", inspectionOutputInner)
                ],
                new ExactSyntheticInspector(
                    root.Value,
                    [inspectionInputOuter],
                    [Path.GetDirectoryName(inspectionOutputInner)!]))
        };

        foreach (OverlapAdmissionCase item in overlapCases)
        {
            var counting = new CountingInspectionService(service);
            ParsedCommand command = ParseV2(
                rootText,
                inputText,
                sourceHash,
                Path.Combine(rootText, $"{item.Name}-receipt.json"),
                workflowSeed,
                Path.Combine(rootText, $"{item.Name}-workflow.json"));
            foreach ((string option, string value) in item.Overrides)
            {
                command = command with
                {
                    Options = command.Options.SetItem(option, value)
                };
            }

            var boundary = new FaceGeomHairRegionsWorkspaceBoundary(
                root,
                item.Inspector);
            ProtocolV2PresetInspectAdapter adapter = CreateAdapter(
                root,
                counting,
                store,
                workflowLifecycle: null,
                boundary);
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [adapter],
                AgentCommandRegistry.All);
            CommandExitCode exit = await runner.RunAsync(
                command with
                {
                    Json = true,
                    RequestedProtocol = "2"
                },
                CancellationToken.None);
            using JsonDocument envelope = JsonDocument.Parse(
                output.ToString().Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries).Single());
            JsonElement[] diagnostics = envelope.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .ToArray();
            Assert(exit == CommandExitCode.SecurityRefusal &&
                   diagnostics.Length == 1 &&
                   diagnostics[0].GetProperty("code").GetString() ==
                       ProtocolV2DiagnosticCodes.PresetInspectionPathRefused &&
                   diagnostics[0].GetProperty("class").GetString() ==
                       "security" &&
                   diagnostics[0].GetProperty("message").GetString() ==
                       $"Options '--{item.InputOption}' and '--{item.OutputOption}' must not overlap." &&
                   diagnostics[0].GetProperty("recovery")
                       .GetProperty("option").GetString() == item.OutputOption &&
                   diagnostics.All(diagnostic =>
                       diagnostic.GetProperty("code").GetString() !=
                       ProtocolV2DiagnosticCodes.ProtocolOperationFailed) &&
                   envelope.RootElement.GetProperty("artifacts")
                       .GetArrayLength() == 0 &&
                   counting.Calls == 0,
                $"{item.Name} did not preserve the matched overlap option identities: {output}");
        }

        var stages = new[]
        {
            new CommitStageCase(
                "receipt-write-failed",
                [
                    ApplicationEffectVocabulary.Completed,
                    ApplicationEffectVocabulary.Failed
                ],
                [],
                (receipt, _) => File.WriteAllText(receipt, "occupied")),
            new CommitStageCase(
                "workflow-write-failed",
                [
                    ApplicationEffectVocabulary.Completed,
                    ApplicationEffectVocabulary.Completed,
                    ApplicationEffectVocabulary.Failed
                ],
                [
                    WorkflowArtifactKinds.RaceMenuJslot,
                    PresetInspectionSchemas.ArtifactKind
                ],
                (_, workflow) => File.WriteAllText(workflow, "occupied"))
        };

        foreach (CommitStageCase item in stages)
        {
            string receipt = Path.Combine(rootText, $"{item.Name}-receipt.json");
            string workflow = Path.Combine(rootText, $"{item.Name}-workflow.json");
            var counting = new CountingInspectionService(
                service,
                () => item.Race(receipt, workflow));
            var adapter = new ProtocolV2PresetInspectAdapter(
                root,
                counting,
                store,
                workflowLifecycle);
            ProtocolCommandResult result = await adapter.RunAsync(
                ParseV2(
                    rootText,
                    inputText,
                    sourceHash,
                    receipt,
                    workflowSeed,
                    workflow),
                new string('D', 64),
                CancellationToken.None);
            Assert(result.Effects.Select(effect => effect.Status)
                       .SequenceEqual(item.ExpectedStatuses) &&
                   result.Effects[0].Kind == AgentEffectKind.ReadWorkspace &&
                   result.Effects.Skip(1).All(effect =>
                       effect.Kind == AgentEffectKind.WriteNewArtifact) &&
                   result.Artifacts.Select(artifact => artifact.Kind)
                       .SequenceEqual(item.ExpectedArtifacts) &&
                   result.Diagnostics.All(diagnostic =>
                       diagnostic.Code !=
                       ProtocolV2DiagnosticCodes.ProtocolOperationFailed) &&
                   result.Artifacts.All(artifact =>
                       artifact.Kind != "workflow-bundle") &&
                   counting.Calls == 1,
                $"{item.Name} did not report its exact reached commit stages");

            if (item.Name == "receipt-write-failed")
            {
                Assert(result.Artifacts.IsEmpty,
                    "pre-promotion receipt failure advertised an artifact");
                continue;
            }

            ProtocolArtifact sourceArtifact = result.Artifacts.Single(
                artifact => artifact.Kind ==
                    WorkflowArtifactKinds.RaceMenuJslot);
            ProtocolArtifact receiptArtifact = result.Artifacts.Single(
                artifact => artifact.Kind ==
                    PresetInspectionSchemas.ArtifactKind);
            PresetExactInputDocument reopenedSource =
                await store.ReadExactAsync(
                    new WorkspacePath(sourceArtifact.Path),
                    sourceArtifact.Sha256!,
                    CancellationToken.None);
            PresetInspectionArtifactDocument reopenedReceipt = store.Load(
                new WorkspacePath(receiptArtifact.Path),
                receiptArtifact.Sha256!);
            Assert(reopenedSource.Size == sourceArtifact.Size &&
                   reopenedSource.Sha256 == sourceArtifact.Sha256 &&
                   reopenedReceipt.Size == receiptArtifact.Size &&
                   reopenedReceipt.Sha256 == receiptArtifact.Sha256,
                "workflow-stage failure advertised bytes that did not physically reopen and hash-match");
        }
    }

    private static ProtocolV2PresetInspectAdapter CreateAdapter(
        WorkspacePath root,
        IPresetExactInspectionService service,
        PresetInspectionArtifactStore store,
        AgentWorkflowBundleTransitionService? workflowLifecycle,
        FaceGeomHairRegionsWorkspaceBoundary? boundary)
    {
        if (boundary is null)
            return new ProtocolV2PresetInspectAdapter(
                root,
                service,
                store,
                workflowLifecycle);
        return (ProtocolV2PresetInspectAdapter)(Activator.CreateInstance(
            typeof(ProtocolV2PresetInspectAdapter),
            [root, service, store, workflowLifecycle, boundary]) ??
            throw new InvalidOperationException(
                "The preset adapter did not expose the typed path boundary."));
    }

    private static void AssertRegistryAndSchema()
    {
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("preset inspect");
        Assert(contract.Readiness == ProtocolReadiness.V2,
            "preset inspect is not an admitted protocol-v2 command");
        Assert(contract.Options.Select(item =>
                    (item.CliName, item.ValueKind, item.Required,
                        Values: string.Join(",", item.AllowedValues)))
                .SequenceEqual([
                    ("format", AgentValueKind.Enum, true, "racemenu-jslot"),
                    ("edition", AgentValueKind.Enum, true, "skyrimse"),
                    ("input", AgentValueKind.Path, true, ""),
                    ("input-sha256", AgentValueKind.Sha256, true, ""),
                    ("inspection-output", AgentValueKind.Path, true, ""),
                    ("workflow-bundle", AgentValueKind.Path, true, ""),
                    ("workflow-bundle-sha256", AgentValueKind.Sha256, true, ""),
                    ("workflow-output", AgentValueKind.Path, true, "")
                ]),
            "preset inspect v2 options are not the exact approved set");
        Assert(contract.ResultSchemaIds.SequenceEqual(
                   [AgentProtocolSchemaIds.PresetInspectResult]) &&
               contract.Effects.Select(item => item.Kind).SequenceEqual([
                   AgentEffectKind.ReadWorkspace,
                   AgentEffectKind.WriteNewArtifact,
                   AgentEffectKind.AppendLocalOperationJournal
               ]) &&
               contract.InputArtifactKinds.SequenceEqual([
                   WorkflowArtifactKinds.RaceMenuJslot,
                   "workflow-bundle"
               ]),
            "preset inspect rich metadata is incomplete or lost its exact input artifacts");
        Assert(AgentCommandRegistry.All.Where(item =>
                    item.Readiness == ProtocolReadiness.V2)
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual([
                    "capabilities", "npc assembly preflight",
                    "npc create-from-jslot", "npc finish analyze",
                    "npc finish apply", "npc finish verify",
                    "preset inspect", "preview npc", "schema export", "version",
                    "workspace preflight"
                ]),
            "the v2 readiness set drifted from the admitted workflow slices");
        AgentCommandContract createTarget =
            AgentCommandRegistry.GetRequired("npc create-from-jslot");
        Assert(createTarget.Readiness == ProtocolReadiness.V2 &&
               createTarget.Options.Select(item => item.CliName)
                   .SequenceEqual([
                       "request", "request-sha256", "preset",
                       "preset-sha256", "data-root", "plugins",
                       "companion-root", "preflight-output",
                       "face-bake-authority-output",
                       "reviewed-preflight",
                       "reviewed-preflight-sha256",
                       "workflow-bundle",
                       "workflow-bundle-sha256",
                       "workflow-output"
                   ]) &&
               createTarget.ResultSchemaIds.SequenceEqual(
                   [
                       AgentProtocolSchemaIds.NpcCreatePreflightResult,
                       AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult
                   ]),
            "the real create-from-jslot target lacks its exact preflight/build v2 descriptors");

        JsonElement exported = ProtocolV2SchemaService.RenderInline(
            "preset inspect");
        JsonElement definition = exported.GetProperty("resultSchemas")
            .EnumerateArray().Single();
        JsonElement schema = definition.GetProperty("jsonSchema");
        Assert(definition.GetProperty("schemaIdentifier").GetString() ==
                   AgentProtocolSchemaIds.PresetInspectResult &&
               schema.GetProperty("$id").GetString() ==
                   AgentProtocolSchemaIds.PresetInspectResult &&
               schema.GetProperty("oneOf").GetArrayLength() == 4 &&
               schema.GetProperty("$defs").GetProperty("raceMenu")
                   .GetProperty("additionalProperties").GetBoolean() == false,
            "preset inspect result schema is absent from schema export");
        JsonElement[] documents = exported.GetProperty("documentSchemas")
            .EnumerateArray().ToArray();
        Assert(documents.Select(item => string.Join("|",
                    item.GetProperty("name").GetString(),
                    item.GetProperty("direction").GetString(),
                    item.GetProperty("schemaIdentifier").GetString()))
                .SequenceEqual([
                    $"inspection-current|output|{PresetInspectionSchemas.CurrentDocument}",
                    $"inspection-legacy-read|input|{PresetInspectionSchemas.LegacyDocument}"
                ]) &&
               documents.All(item =>
                   item.GetProperty("jsonSchema").GetProperty("properties")
                       .GetProperty("schema").GetProperty("const").GetString() ==
                   item.GetProperty("schemaIdentifier").GetString()) &&
               documents.All(item =>
                   item.GetProperty("jsonSchema").GetProperty("$defs")
                       .GetProperty("raceMenu")
                       .GetProperty("additionalProperties").GetBoolean() == false),
            "preset inspection current/legacy schema directions are absent from command export");
    }

    private static void AssertVersionedReceiptPersistence(string rootText)
    {
        byte[] first = PresetInspectionArtifactStore.ComputeCanonicalBytes(
            ReceiptFixture.CreateNested(reverseInsertion: false));
        byte[] second = PresetInspectionArtifactStore.ComputeCanonicalBytes(
            ReceiptFixture.CreateNested(reverseInsertion: true));
        Assert(first.AsSpan().SequenceEqual(second),
            "schema-2 receipt bytes depend on dictionary insertion order");

        using JsonDocument parsed = JsonDocument.Parse(first);
        Assert(parsed.RootElement.GetProperty("schema").GetString() ==
               PresetInspectionSchemas.CurrentDocument,
            "new receipt did not use schema 2");
        Assert(parsed.RootElement
                .GetProperty("appearance")
                .GetProperty("headParts")[0]
                .GetProperty("type").GetInt32() == 1,
            "canonicalization changed array order");

        var store = new PresetInspectionArtifactStore(
            new WorkspacePath(@"K:\Actorwright"));
        // Raw string literals inherit the checkout's source line endings. The
        // schema-1 contract is strict Actorwright UTF-8/LF, so materialize the
        // checked-in fixture explicitly rather than letting a clean Windows
        // checkout turn it into a different receipt.
        byte[] legacy = Encoding.UTF8.GetBytes(
            LegacySchema1Fixture.ReplaceLineEndings("\n"));
        byte[] legacyCrLf = Encoding.UTF8.GetBytes(
            LegacySchema1Fixture.ReplaceLineEndings("\r\n"));
        string duplicateText = Encoding.UTF8.GetString(first).Replace(
            "{\n  \"appearance\":",
            "{\n  \"appearance\": null,\n  \"appearance\":",
            StringComparison.Ordinal);
        string unknownText = Encoding.UTF8.GetString(first).Replace(
            "{\n  \"appearance\":",
            "{\n  \"unexpected\": true,\n  \"appearance\":",
            StringComparison.Ordinal);
        string noncanonicalText = Encoding.UTF8.GetString(first).Replace(
            "{\n  \"appearance\":",
            "{ \n  \"appearance\":",
            StringComparison.Ordinal);
        Assert(!Encoding.UTF8.GetBytes(duplicateText).AsSpan()
                   .SequenceEqual(first) &&
               !Encoding.UTF8.GetBytes(unknownText).AsSpan()
                   .SequenceEqual(first) &&
               !Encoding.UTF8.GetBytes(noncanonicalText).AsSpan()
                   .SequenceEqual(first),
            "receipt refusal fixtures did not mutate the canonical bytes");
        var cases = new[]
        {
            new ReceiptLoadCase("legacy", legacy, null),
            new ReceiptLoadCase(
                "legacy-crlf",
                legacyCrLf,
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed),
            new ReceiptLoadCase(
                "schema-2-duplicate",
                Encoding.UTF8.GetBytes(duplicateText),
                ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed),
            new ReceiptLoadCase(
                "schema-2-unknown",
                Encoding.UTF8.GetBytes(unknownText),
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed),
            new ReceiptLoadCase(
                "schema-2-noncanonical",
                Encoding.UTF8.GetBytes(noncanonicalText),
                ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed)
        };

        foreach (ReceiptLoadCase item in cases)
        {
            string pathText = Path.Combine(rootText, $"{item.Name}.json");
            File.WriteAllBytes(pathText, item.Bytes);
            string sha256 = Convert.ToHexString(SHA256.HashData(item.Bytes));
            if (item.ExpectedCode is null)
            {
                PresetInspectionArtifactDocument loaded = store.Load(
                    new WorkspacePath(pathText), sha256);
                Assert(loaded.Receipt.Schema ==
                           PresetInspectionSchemas.LegacyDocument &&
                       loaded.Receipt.SourcePath ==
                           @"K:\Actorwright\legacy-preset.jslot" &&
                       loaded.Receipt.SourceSha256 == new string('A', 64) &&
                       loaded.Receipt.IsValid &&
                       loaded.Receipt.Appearance.HeadParts.IsEmpty &&
                       loaded.Receipt.Diagnostics.IsEmpty &&
                       loaded.Utf8Json.AsSpan().SequenceEqual(item.Bytes) &&
                       File.ReadAllBytes(pathText).AsSpan()
                           .SequenceEqual(item.Bytes),
                    "schema-1 receipt did not load as the same typed receipt and exact bytes");
                continue;
            }

            try
            {
                _ = store.Load(new WorkspacePath(pathText), sha256);
                throw new InvalidOperationException(
                    $"{item.Name} receipt unexpectedly loaded");
            }
            catch (PresetInspectionArtifactException exception)
            {
                Assert(exception.Code == item.ExpectedCode,
                    $"{item.Name} receipt used '{exception.Code}' instead of '{item.ExpectedCode}'");
            }
        }

        Assert(ProtocolV2DiagnosticCodes.TryGetAuthoritativeSemantics(
                   ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed,
                   out ProtocolDiagnosticSemantics semantics) &&
               semantics.Class == DiagnosticClass.Verification,
            "receipt canonicality failure is not an internal product consistency failure");
        DiagnosticRecovery recovery =
            ProtocolV2PresetInspectAdapter.RecoveryFor(
                ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed,
                semantics.Class);
        Assert(recovery.Action == RecoveryAction.None &&
               recovery.Constraint.Contains("Retain", StringComparison.Ordinal) &&
               recovery.Constraint.Contains("report", StringComparison.Ordinal) &&
               recovery.Constraint.Contains("upgrade", StringComparison.Ordinal) &&
               !recovery.Constraint.Contains("Repair", StringComparison.Ordinal),
            "receipt canonicality failure incorrectly recommends disk repair");
    }

    private static async Task AssertCanonicalFailuresStayTyped(
        WorkspacePath root,
        string rootText,
        PresetInspectionArtifactStore store)
    {
        const int maximumReceiptBytes = 8 * 1024 * 1024;
        string sourcePath = Path.Combine(rootText, "canonical-limit.jslot");
        string writeOutput = Path.Combine(
            rootText,
            "canonical-limit-write.json");
        PresetInspectionReceipt oversizedWrite = ReceiptFixture.CreateMinimal(
            sourcePath,
            [],
            [new Diagnostic(
                "fixture-warning",
                DiagnosticSeverity.Warning,
                new string('W', maximumReceiptBytes))]);
        try
        {
            await store.WriteNewAsync(
                oversizedWrite,
                new WorkspacePath(writeOutput),
                CancellationToken.None);
            throw new InvalidOperationException(
                "oversized receipt write unexpectedly succeeded");
        }
        catch (PresetInspectionArtifactException exception)
        {
            Assert(exception.Code ==
                       ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed &&
                   !exception.Promoted,
                "oversized receipt write did not use the typed storage diagnostic");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"oversized receipt write leaked {exception.GetType().Name}",
                exception);
        }
        Assert(!File.Exists(writeOutput) &&
               !Directory.EnumerateFiles(
                   root.Value,
                   ".preset-inspection-*.tmp").Any(),
            "failed canonical receipt write began or retained a disk transaction");

        PresetInspectionReceipt compactReceipt = ReceiptFixture.CreateMinimal(
            sourcePath,
            Enumerable.Repeat(0F, 1_000_000).ToImmutableArray(),
            []);
        byte[] compactBytes = JsonSerializer.SerializeToUtf8Bytes(
            compactReceipt,
            CompactReceiptOptions);
        Assert(compactBytes.Length < maximumReceiptBytes,
            "compact schema-2 fixture exceeded the admitted read boundary");
        string loadPath = Path.Combine(
            rootText,
            "canonical-limit-load.json");
        File.WriteAllBytes(loadPath, compactBytes);
        string loadSha256 = Convert.ToHexString(
            SHA256.HashData(compactBytes));
        try
        {
            _ = store.Load(new WorkspacePath(loadPath), loadSha256);
            throw new InvalidOperationException(
                "schema-2 oversized canonical reload unexpectedly succeeded");
        }
        catch (PresetInspectionArtifactException exception)
        {
            Assert(exception.Code ==
                       ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed &&
                   !exception.Promoted,
                "schema-2 canonical reload did not use the product canonicality diagnostic");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"schema-2 canonical reload leaked {exception.GetType().Name}",
                exception);
        }
        Assert(File.ReadAllBytes(loadPath).AsSpan()
                   .SequenceEqual(compactBytes),
            "schema-2 canonical reload changed the admitted physical bytes");
    }

    private static async Task AssertV1InspectUnchanged(
        PresetService service,
        string inputText,
        string sourceHash)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PresetCommandHandler(
            service,
            new NullResolver(),
            null,
            null,
            output,
            error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse([
                "preset", "inspect", "--json",
                "--format", "racemenu-jslot",
                "--edition", "skyrimse",
                "--input", inputText
            ]),
            CancellationToken.None);
        Assert(exit == CommandExitCode.Success && error.ToString() == string.Empty,
            $"legacy preset inspect changed its exit/stderr: {error}");
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        string[] properties = result.RootElement.EnumerateObject()
            .Select(item => item.Name).ToArray();
        Assert(properties.SequenceEqual([
                   "format", "edition", "sourceSha256", "isValid",
                   "appearance", "diagnostics"
               ]) &&
               result.RootElement.GetProperty("format").GetString() ==
                   "racemenu-jslot" &&
               result.RootElement.GetProperty("edition").GetString() ==
                   "skyrimse" &&
               string.Equals(
                   result.RootElement.GetProperty("sourceSha256").GetString(),
                   sourceHash,
                   StringComparison.OrdinalIgnoreCase) &&
               result.RootElement.GetProperty("isValid").GetBoolean() &&
               !output.ToString().Contains("inspectionOutput",
                   StringComparison.Ordinal),
            "legacy preset inspect wire projection/options changed");
    }

    private static async Task AssertSuccess(
        string inputText,
        string sourceHash,
        string receiptText,
        PresetInspectionArtifactStore store,
        ProtocolV2PresetInspectAdapter adapter,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed,
        WorkspacePath workflowOutput)
    {
        var journal = new CapturingJournal();
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => journal,
            [adapter],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse([
            "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse",
            "--input", inputText, "--input-sha256", sourceHash,
            "--inspection-output", receiptText,
            "--workflow-bundle", workflowSeed.Document.Path.Value,
            "--workflow-bundle-sha256", workflowSeed.Document.Sha256,
            "--workflow-output", workflowOutput.Value
        ]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success,
            $"protocol-v2 preset inspect failed: {output}");
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length == 1, "preset inspect emitted multiple stdout records");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement root = envelope.RootElement;
        JsonElement result = root.GetProperty("result");
        JsonElement resultSchema = ProtocolV2SchemaService.RenderInline(
                "preset inspect")
            .GetProperty("resultSchemas")[0]
            .GetProperty("jsonSchema");
        Assert(result.EnumerateObject().Select(item => item.Name)
                   .OrderBy(item => item, StringComparer.Ordinal)
                   .SequenceEqual(resultSchema.GetProperty("properties")
                       .EnumerateObject().Select(item => item.Name)
                       .OrderBy(item => item, StringComparer.Ordinal)),
            "preset inspect result JSON does not match its exported property set");
        Assert(result.GetProperty("schemaVersion").GetString() == "1" &&
               result.GetProperty("format").GetString() == "racemenu-jslot" &&
               result.GetProperty("edition").GetString() == "skyrimse" &&
               result.GetProperty("sourcePath").GetString() == inputText &&
               result.GetProperty("sourceSha256").GetString() == sourceHash &&
               result.GetProperty("isValid").GetBoolean() &&
               result.GetProperty("inspectionPath").GetString() == receiptText,
            "preset inspect result projection is not exact");
        Assert(root.GetProperty("effects").EnumerateArray()
                   .Select(item => string.Join("|",
                       item.GetProperty("kind").GetString(),
                       item.GetProperty("status").GetString(),
                       item.GetProperty("scope").GetString()))
                   .SequenceEqual([
                       "readWorkspace|completed|workspace",
                       "writeNewArtifact|completed|k-local-output",
                       "writeNewArtifact|completed|k-local-output",
                       "appendLocalOperationJournal|attempted|workspace-local-journal",
                       "appendLocalOperationJournal|completed|workspace-local-journal"
                   ]),
            "preset inspect emitted noncanonical effect scopes or a failed journal");

        ProtocolArtifact[] artifacts = root.GetProperty("artifacts")
            .EnumerateArray().Select(item => new ProtocolArtifact(
                item.GetProperty("kind").GetString()!,
                item.GetProperty("schemaOrMediaType").GetString()!,
                item.GetProperty("path").GetString()!,
                item.GetProperty("size").GetInt64(),
                item.GetProperty("sha256").GetString(),
                item.GetProperty("producerCommand").GetString()!,
                item.GetProperty("requestDigest").GetString()!,
                item.GetProperty("inputBindings").EnumerateArray()
                    .Select(value => value.GetString()!).ToImmutableArray(),
                item.GetProperty("state").GetString()!)).ToArray();
        ProtocolArtifact jslot = artifacts.Single(item =>
            item.Kind == WorkflowArtifactKinds.RaceMenuJslot);
        ProtocolArtifact receipt = artifacts.Single(item =>
            item.Kind == PresetInspectionSchemas.ArtifactKind);
        ProtocolArtifact workflow = artifacts.Single(item =>
            item.Kind == "workflow-bundle");
        PresetInspectionArtifactDocument loaded = store.Load(
            new WorkspacePath(receiptText), receipt.Sha256!);
        AgentWorkflowBundleTransition resumed = workflowLifecycle.LoadForCommand(
            new WorkspacePath(workflow.Path),
            workflow.Sha256!,
            "npc create-from-jslot");
        Assert(jslot.Path == inputText && jslot.Sha256 == sourceHash &&
               jslot.Size == new FileInfo(inputText).Length &&
               jslot.SchemaOrMediaType == "application/json" &&
               receipt.SchemaOrMediaType ==
                   PresetInspectionSchemas.CurrentDocument &&
               receipt.Path == receiptText && receipt.Size == loaded.Size &&
               receipt.Sha256 == loaded.Sha256 &&
               receipt.InputBindings.SequenceEqual([sourceHash]) &&
               loaded.Receipt.SourcePath == inputText &&
               loaded.Receipt.SourceSha256 == sourceHash &&
               loaded.Receipt.IsValid &&
               loaded.Receipt.Diagnostics.Any(item =>
                   item.Code == "preset-unknown-field" &&
                   item.Severity == DiagnosticSeverity.Warning) &&
               workflow.Path == workflowOutput.Value &&
               workflow.Size == resumed.Document.Size &&
               workflow.Sha256 == resumed.Document.Sha256 &&
               resumed.Document.Bundle.Artifacts.Select(item => item.Kind)
                   .OrderBy(item => item, StringComparer.Ordinal)
                   .SequenceEqual([
                       WorkflowArtifactKinds.RaceMenuJslot,
                       WorkflowArtifactKinds.ReviewedWorkspaceIntake
                   ]),
            "source/receipt artifacts were mislabeled or not pinned and exact");
        JsonElement warning = root.GetProperty("diagnostics")
            .EnumerateArray().Single(item =>
                item.GetProperty("severity").GetString() == "warning");
        Assert(warning.GetProperty("code").GetString() ==
                   ProtocolV2DiagnosticCodes.PresetInspectionWarning &&
               warning.GetProperty("class").GetString() == "validation" &&
               warning.GetProperty("message").GetString()!.Contains(
                   "preset-unknown-field", StringComparison.Ordinal),
            "accepted parser warning was not projected through typed semantics");
        AssertAuthority(root, "inputAdmission", "established");
        AssertAuthority(root, "sourceProviderIdentity", "required");
        AssertAuthority(root, "deterministicMaterialization", "established");
        AssertAuthority(root, "independentStaticVerification", "established");
        AssertAuthority(root, "offEnginePreview", "notApplicable");
        AssertAuthority(root, "humanVisualAcceptance", "notApplicable");
        AssertAuthority(root, "gameRuntimeVerification", "required");
        AssertAuthority(root, "promotionApproval", "notApplicable");
        JsonElement next = root.GetProperty("nextActions")
            .EnumerateArray().Single();
        string[] bindings = next.GetProperty("requiredBindings")
            .EnumerateArray().Select(item => item.GetRawText()).ToArray();
        string[] missing = next.GetProperty("missingPrerequisites")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert(next.GetProperty("command").GetString() ==
                   "npc create-from-jslot" &&
               bindings.Any(item => item.Contains("--preset", StringComparison.Ordinal)) &&
               bindings.Any(item => item.Contains("--preset-sha256", StringComparison.Ordinal)) &&
               missing.SequenceEqual([
                   "--request", "--request-sha256", "--data-root",
                   "--plugins", "--companion-root", "--preflight-output",
                   "--workflow-output"
               ]) &&
               !lines[0].Contains("npc assembly preflight",
                   StringComparison.Ordinal),
            "preset inspect did not leave the real create-from-jslot preflight gate blocked");
        Assert(journal.Records.Count == 1,
            "preset inspect was not journaled exactly once");
    }

    private static async Task AssertStaleHashRefused(
        string rootText,
        string inputText,
        ProtocolV2PresetInspectAdapter adapter,
        AgentWorkflowBundleTransition workflowSeed)
    {
        string receipt = Path.Combine(rootText, "stale.json");
        string workflowOutput = Path.Combine(
            rootText,
            "workflow-stale-refused.json");
        ProtocolCommandResult result = await adapter.RunAsync(
            ParseV2(rootText, inputText, new string('A', 64),
                receipt,
                workflowSeed,
                workflowOutput),
            new string('B', 64),
            CancellationToken.None);
        Assert(result.Diagnostics.Any(item => item.Code ==
                   ProtocolV2DiagnosticCodes.PresetInspectionInputHashMismatch) &&
               result.Artifacts.IsEmpty &&
               !File.Exists(receipt) &&
               !File.Exists(workflowOutput),
            "stale preset hash was not refused before publication");
        AssertAuthority(result, AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Blocked);
    }

    private static async Task AssertOutputRefusals(
        WorkspacePath root,
        string rootText,
        string inputText,
        string sourceHash,
        string existingReceipt,
        PresetService service,
        ProtocolV2PresetInspectAdapter adapter,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed)
    {
        await AssertRefused(adapter,
            ParseV2(rootText, inputText, sourceHash, existingReceipt,
                workflowSeed,
                Path.Combine(rootText, "workflow-existing-refused.json")),
            ProtocolV2DiagnosticCodes.PresetInspectionOutputExists);
        await AssertRefused(adapter,
            ParseV2(rootText, inputText, sourceHash,
                Path.Combine(rootText, "ads.json") + ":stream",
                workflowSeed,
                Path.Combine(rootText, "workflow-ads-refused.json")),
            ProtocolV2DiagnosticCodes.AlternateDataStreamRefused);

        string reparseParent = Path.Combine(rootText, "reparse-parent");
        Directory.CreateDirectory(reparseParent);
        var reparseStore = new PresetInspectionArtifactStore(
            root,
            new ReparseInspector(reparseParent));
        var reparseAdapter = new ProtocolV2PresetInspectAdapter(
            root,
            service,
            reparseStore,
            workflowLifecycle);
        await AssertRefused(reparseAdapter,
            ParseV2(rootText, inputText, sourceHash,
                Path.Combine(reparseParent, "receipt.json"),
                workflowSeed,
                Path.Combine(rootText, "workflow-reparse-refused.json")),
            ProtocolV2DiagnosticCodes.ReparsePointRefused);
    }

    private static async Task AssertCancellationCleansTemporary(
        WorkspacePath root,
        string inputText,
        string sourceHash,
        PresetService service,
        PresetInspectionArtifactStore store)
    {
        PresetExactInputDocument source = await store.ReadExactAsync(
            new WorkspacePath(inputText),
            sourceHash,
            CancellationToken.None);
        PresetParseResult inspection = await service.InspectExactAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(inputText)),
            source.Utf8Json.AsMemory(),
            new Sha256Hash(source.Sha256),
            CancellationToken.None);
        var receipt = PresetInspectionReceipt.From(
            inspection.Document!, source.Path, source.Sha256);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await store.WriteNewAsync(
                receipt,
                new WorkspacePath(Path.Combine(root.Value, "cancelled.json")),
                cancelled.Token);
            throw new InvalidOperationException(
                "cancelled preset inspection write unexpectedly succeeded");
        }
        catch (OperationCanceledException)
        {
        }
        Assert(!Directory.EnumerateFiles(root.Value,
                   ".preset-inspection-*.tmp").Any() &&
               !File.Exists(Path.Combine(root.Value, "cancelled.json")),
            "pre-promotion cancellation left an owned temporary or destination");
    }

    private static ParsedCommand ParseV2(
        string rootText,
        string inputText,
        string sourceHash,
        string outputText,
        AgentWorkflowBundleTransition workflowSeed,
        string workflowOutput)
    {
        List<string> arguments =
        [
            "preset", "inspect", "--format", "racemenu-jslot",
            "--edition", "skyrimse", "--input", inputText,
            "--input-sha256", sourceHash,
            "--inspection-output", outputText
        ];
        arguments.AddRange([
            "--workflow-bundle", workflowSeed.Document.Path.Value,
            "--workflow-bundle-sha256", workflowSeed.Document.Sha256,
            "--workflow-output", workflowOutput
        ]);
        return CommandLine.Parse([.. arguments]);
    }

    private static AgentWorkflowBundleTransition CreateWorkflowSeed(
        string rootText,
        AgentWorkflowBundleTransitionService lifecycle)
    {
        string intakePath = Path.Combine(rootText, "reviewed-intake.json");
        byte[] intakeBytes = Encoding.UTF8.GetBytes("reviewed-intake-fixture");
        File.WriteAllBytes(intakePath, intakeBytes);
        var intake = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            "npcmanager-reviewed-game-intake/2",
            new WorkspacePath(intakePath),
            intakeBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(intakeBytes)),
            "workspace preflight",
            SeedRequestDigest,
            []);
        return lifecycle.WriteInitial(
            new WorkflowNpcIdentity("PresetInspectNpc", null, null, null),
            SeedRequestDigest,
            [intake],
            new WorkspacePath(Path.Combine(rootText, "workflow-workspace.json")));
    }

    private static async Task AssertRefused(
        ProtocolV2PresetInspectAdapter adapter,
        ParsedCommand command,
        string code)
    {
        ProtocolCommandResult result = await adapter.RunAsync(
            command, new string('C', 64), CancellationToken.None);
        Assert(result.Diagnostics.Any(item => item.Code == code) &&
               result.Artifacts.IsEmpty,
            $"preset inspection output was not refused as '{code}'");
    }

    private static void AssertAuthority(
        JsonElement envelope,
        string kind,
        string state) => Assert(
        envelope.GetProperty("authority").EnumerateArray().Any(item =>
            item.GetProperty("kind").GetString() == kind &&
            item.GetProperty("state").GetString() == state),
        $"authority {kind} did not remain {state}");

    private static void AssertAuthority(
        ProtocolCommandResult result,
        AgentAuthorityKind kind,
        AgentAuthorityState state) => Assert(
        result.Authority.Single(item => item.Kind == kind).State == state,
        $"refused preset authority {kind} did not remain {state}");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const string LegacySchema1Fixture =
        """
        {
          "schema": "actorwright-preset-inspection/1",
          "sourcePath": "K:\\Actorwright\\legacy-preset.jslot",
          "sourceSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "format": "racemenu-jslot",
          "edition": "skyrimse",
          "isValid": true,
          "appearance": {
            "gender": null,
            "headParts": [],
            "hairColor": null,
            "weight": null,
            "morphs": {},
            "bodyMorphs": {},
            "customMorphs": {},
            "sliderMorphs": [],
            "tints": [],
            "overlays": [],
            "skin": null,
            "presence": {
              "gender": false,
              "headParts": false,
              "hairColor": false,
              "weight": false,
              "morphs": false,
              "bodyMorphs": false,
              "tints": false,
              "overlays": false,
              "skin": false,
              "fallout4BodyMorphs": false,
              "chargenFaceMorphs": false,
              "faceBoneRegions": false,
              "facialMorphIntensity": false
            },
            "unknownFields": [],
            "fallout4BodyMorphs": null,
            "chargenFaceMorphs": null,
            "faceBoneRegions": null,
            "facialMorphIntensity": 1,
            "raceMenu": null,
            "orderedCustomMorphs": []
          },
          "diagnostics": []
        }
        """;

    private sealed record ReceiptLoadCase(
        string Name,
        byte[] Bytes,
        string? ExpectedCode);

    private sealed record PathAdmissionCase(
        string Name,
        string Option,
        string? Value,
        string ExpectedCode,
        IFaceGeomHairRegionsPathInspector? Inspector);

    private sealed record OverlapAdmissionCase(
        string Name,
        string InputOption,
        string OutputOption,
        ImmutableArray<(string Option, string Value)> Overrides,
        IFaceGeomHairRegionsPathInspector Inspector);

    private sealed record CommitStageCase(
        string Name,
        ImmutableArray<string> ExpectedStatuses,
        ImmutableArray<string> ExpectedArtifacts,
        Action<string, string> Race);

    private static class ReceiptFixture
    {
        public static PresetInspectionReceipt CreateMinimal(
            string sourcePath,
            ImmutableArray<float> sliderMorphs,
            ImmutableArray<Diagnostic> diagnostics) =>
            new(
                PresetInspectionSchemas.CurrentDocument,
                sourcePath,
                new string('A', 64),
                "racemenu-jslot",
                "skyrimse",
                !diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error),
                new PresetAppearance(
                    null,
                    [],
                    null,
                    null,
                    ImmutableDictionary<string, float>.Empty,
                    ImmutableDictionary<string, float>.Empty,
                    ImmutableDictionary<string, float>.Empty,
                    sliderMorphs,
                    [],
                    [],
                    null,
                    new PresetFieldPresence(
                        false, false, false, false, false, false,
                        false, false, false),
                    []),
                diagnostics);

        public static PresetInspectionReceipt CreateNested(
            bool reverseInsertion)
        {
            ImmutableDictionary<string, float> morphs = Map(
                reverseInsertion,
                ("JawWidth", 0.25F),
                ("NoseLength", -0.5F));
            ImmutableDictionary<string, float> bodyMorphs = Map(
                reverseInsertion,
                ("7B Lower", 0.75F),
                ("7B Upper", -0.25F));
            ImmutableDictionary<string, float> customMorphs = Map(
                reverseInsertion,
                ("Aelva Cheek", 0.4F),
                ("Aelva Jaw", -0.2F));
            ImmutableDictionary<string, ImmutableDictionary<string, float>>
                keyedMorphs = Map(
                    reverseInsertion,
                    ("Aelva", Map(
                        reverseInsertion,
                        ("CBBE", 0.65F),
                        ("RaceMenuMorphsCBBE", 0.35F))),
                    ("AelvaOutfit", Map(
                        reverseInsertion,
                        ("CBBE", -0.1F),
                        ("RaceMenuMorphsCBBE", 0.2F))));
            ImmutableDictionary<int, string> skinTextures = Map(
                reverseInsertion,
                (0, @"textures\actors\character\female\femalebody_1.dds"),
                (1, @"textures\actors\character\female\femalebody_1_msn.dds"));
            ImmutableDictionary<uint, float> chargenMorphs = Map(
                reverseInsertion,
                (0U, 0.125F),
                (1U, -0.375F));
            ImmutableDictionary<uint, ImmutableArray<float>> faceRegions = Map(
                reverseInsertion,
                (0U, ImmutableArray.Create(0.1F, 0.2F, 0.3F)),
                (1U, ImmutableArray.Create(-0.1F, -0.2F, -0.3F)));

            var appearance = new PresetAppearance(
                1,
                [
                    new PresetHeadPart(
                        PresetIdentifier.Parse("Skyrim.esm|0x0005161E"), 1),
                    new PresetHeadPart(
                        PresetIdentifier.Parse("Skyrim.esm|0x0001DA82"), 3)
                ],
                PresetHairColor.FromPackedRgb(0x00332211),
                new PresetWeight(47.5F, 0.2F, 0.5F, 0.3F),
                morphs,
                bodyMorphs,
                customMorphs,
                [0.1F, -0.2F, 0.3F],
                [
                    new PresetTint(
                        0, 0x00D8B7A4, @"textures\actors\character\character assets\tintmask.dds"),
                    new PresetTint(
                        1, 0x00604030, @"textures\actors\character\character assets\warpaint.dds")
                ],
                [
                    new PresetOverlay(
                        "AelvaFreckles", 1,
                        [0.8F, 0.6F, 0.4F, 1F],
                        [0F, 0F],
                        [1F, 1F])
                ],
                @"textures\actors\character\female\femalebody_1.dds",
                new PresetFieldPresence(
                    true, true, true, true, true, true, true, true, true,
                    ChargenFaceMorphs: true,
                    FaceBoneRegions: true,
                    FacialMorphIntensity: true),
                [new PresetUnknownField("$.aelvaExtension", "Object")],
                ChargenFaceMorphs: chargenMorphs,
                FaceBoneRegions: faceRegions,
                FacialMorphIntensity: 0.85F,
                RaceMenu: new RaceMenuPresetData(
                    @"textures\actors\character\female\femalehead.dds",
                    [0U, 1U, 2U],
                    1000,
                    [],
                    keyedMorphs,
                    [
                        new RaceMenuBodyOverlay(
                            "NPC Root [Root]", "aelva_diffuse.dds",
                            "aelva_normal.dds", [1F, 0.8F, 0.7F, 1F], 0.9F,
                            [new RaceMenuValue(
                                7, 1, 0, RaceMenuScalar.FromNumber(0.5))])
                    ],
                    [
                        new SkyrimNodeTransform(
                            "NPC L Breast", false,
                            [
                                new RaceMenuTransformKeySet(
                                    "RSMTransform",
                                    [
                                        new RaceMenuValue(
                                            0, 1, 0,
                                            RaceMenuScalar.FromNumber(1.05)),
                                        new RaceMenuValue(
                                            1, 1, 0,
                                            RaceMenuScalar.FromNumber(0.02))
                                    ])
                            ],
                            1.05F, 0,
                            [0.02F, 0F, 0F],
                            [1F, 0F, 0F, 0F, 1F, 0F, 0F, 0F, 1F])
                    ],
                    [
                        new SkyrimSkinOverride(
                            0x00400004U, false,
                            [
                                new RaceMenuValue(
                                    9, 2, 0,
                                    RaceMenuScalar.FromText("aelva_diffuse.dds")),
                                new RaceMenuValue(
                                    9, 2, 1,
                                    RaceMenuScalar.FromText("aelva_normal.dds"))
                            ],
                            skinTextures,
                            [0.9F, 0.8F, 0.7F, 1F],
                            0.95F)
                    ],
                    [
                        new RaceMenuFaceTexture(0, "aelva_face.dds"),
                        new RaceMenuFaceTexture(1, "aelva_face_msn.dds")
                    ],
                    [new PluginName("High Poly Head.esm"), new PluginName("Aelva.esp")],
                    [
                        new RaceMenuModEntry(0, new PluginName("Skyrim.esm")),
                        new RaceMenuModEntry(1, new PluginName("Aelva.esp"))
                    ],
                    new RaceMenuVersion(4, 0x01060600, 0x534B4545, 2)),
                OrderedCustomMorphs:
                [
                    new SkyrimRaceMenuCustomMorphValue("Aelva Cheek", 0.4F),
                    new SkyrimRaceMenuCustomMorphValue("Aelva Jaw", -0.2F)
                ]);
            return new PresetInspectionReceipt(
                PresetInspectionSchemas.CurrentDocument,
                @"K:\Actorwright\Aelva.jslot",
                new string('B', 64),
                "racemenu-jslot",
                "skyrimse",
                true,
                appearance,
                []);
        }

        private static ImmutableDictionary<TKey, TValue> Map<TKey, TValue>(
            bool reverseInsertion,
            params (TKey Key, TValue Value)[] entries)
            where TKey : notnull
        {
            var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>(
                ConstantHashComparer<TKey>.Instance);
            IEnumerable<(TKey Key, TValue Value)> ordered = reverseInsertion
                ? entries.Reverse()
                : entries;
            foreach ((TKey key, TValue value) in ordered)
                builder.Add(key, value);
            return builder.ToImmutable();
        }
    }

    private sealed class ConstantHashComparer<T> : IEqualityComparer<T>
    {
        public static ConstantHashComparer<T> Instance { get; } = new();

        public bool Equals(T? left, T? right) =>
            EqualityComparer<T>.Default.Equals(left, right);

        public int GetHashCode(T value) => 0;
    }

    private sealed class NullResolver : IPresetFormResolver
    {
        public ValueTask<PresetResolutionResult> ResolveAsync(
            PresetResolutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingInspectionService(
        IPresetExactInspectionService inner,
        Action? afterInspection = null) : IPresetExactInspectionService
    {
        public int Calls { get; private set; }

        public async ValueTask<PresetParseResult> InspectExactAsync(
            PresetParseRequest request,
            ReadOnlyMemory<byte> exactUtf8Json,
            Sha256Hash admittedSha256,
            CancellationToken cancellationToken)
        {
            Calls++;
            PresetParseResult result = await inner.InspectExactAsync(
                request,
                exactUtf8Json,
                admittedSha256,
                cancellationToken);
            afterInspection?.Invoke();
            return result;
        }
    }

    private sealed class CapturingJournal : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private sealed class ReparseInspector(string reparsePath) :
        IFaceGeomHairRegionsPathInspector
    {
        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public FileAttributes GetAttributes(string path) =>
            string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(reparsePath),
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path);
    }

    private sealed class ExactSyntheticInspector(
        string root,
        IReadOnlyList<string> files,
        IReadOnlyList<string> directories) :
        IFaceGeomHairRegionsPathInspector
    {
        public bool FileExists(string path) =>
            files.Any(file => IsExact(path, file)) ||
            (!directories.Any(directory => IsExact(path, directory)) &&
             File.Exists(path));

        public bool DirectoryExists(string path) =>
            directories.Any(directory => IsExact(path, directory)) ||
            (!files.Any(file => IsExact(path, file)) &&
             Directory.Exists(path));

        public FileAttributes GetAttributes(string path) =>
            files.Any(file => IsExact(path, file))
                ? FileAttributes.Normal
                : directories.Any(directory => IsExact(path, directory)) ||
                    IsExact(path, root)
                    ? FileAttributes.Directory
                    : File.GetAttributes(path);

        private static bool IsExact(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.Ordinal);
    }
}
