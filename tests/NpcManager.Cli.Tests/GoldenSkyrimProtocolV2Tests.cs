using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class GoldenSkyrimProtocolV2Tests
{
    public static async Task TestReviewedIntakePersistence()
    {
        var failures = new List<string>();
        JsonElement? resultSchema = null;
        try
        {
            await ProtocolV2AdapterTests.TestOutcomeAuthorityAdmission();
        }
        catch (Exception exception)
        {
            failures.Add("generic authority admission: " + exception.Message);
        }

        try
        {
            resultSchema = await AssertWorkspacePreflightSchemaExport();
        }
        catch (Exception exception)
        {
            failures.Add("workspace result schema: " + exception.Message);
        }

        try
        {
            await TestReviewedIntakePersistenceCore(resultSchema);
        }
        catch (Exception exception)
        {
            failures.Add("reviewed intake projection: " + exception.Message);
        }

        Assert(failures.Count == 0,
            "Round 1 contract failures: " + string.Join(" | ", failures));
    }

    private static async Task TestReviewedIntakePersistenceCore(
        JsonElement? resultSchema)
    {
        Assert(ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                   ProtocolV2DiagnosticCodes.ReviewedIntakeOutputOverlap,
                   out ProtocolDiagnosticSemantics overlapSemantics) &&
               overlapSemantics.Class == DiagnosticClass.Security &&
               ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                   ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                   out ProtocolDiagnosticSemantics persistenceSemantics) &&
               persistenceSemantics.Class == DiagnosticClass.Operation,
            "reviewed-intake persistence diagnostics are absent from the typed catalog");
        string rootText = Path.Combine(Environment.CurrentDirectory, "artifacts",
            $"protocol-v2-reviewed-intake-{Guid.NewGuid():N}");
        string dataText = Path.Combine(rootText, "Data");
        string loadOrderText = Path.Combine(rootText, "loadorder.txt");
        string outputRootText = Path.Combine(rootText, "reserved-output");
        string intakeOutputText = Path.Combine(rootText, "reviewed-intake.json");
        string workflowOutputText = Path.Combine(rootText, "workflow.json");
        Directory.CreateDirectory(dataText);
        try
        {
            byte[] pluginBytes = [1, 2, 3, 4, 5];
            byte[] loadOrderBytes = Encoding.UTF8.GetBytes("*Fixture.esp\r\n");
            string pluginText = Path.Combine(dataText, "Fixture.esp");
            await File.WriteAllBytesAsync(pluginText, pluginBytes);
            await File.WriteAllBytesAsync(loadOrderText, loadOrderBytes);

            var root = new WorkspacePath(rootText);
            var data = new WorkspacePath(dataText);
            var loadOrder = new WorkspacePath(loadOrderText);
            var outputRoot = new WorkspacePath(outputRootText);
            var plugin = new PluginName("Fixture.esp");
            var provisional = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition, root, data, loadOrder, outputRoot,
                Sha(loadOrderBytes),
                [new PluginClosureReviewEntry(plugin, 0, true, true, true, false,
                    true, new WorkspacePath(pluginText), Sha(pluginBytes), [])],
                [], [], [], 1, new Sha256Hash(new string('A', 64)),
                new Sha256Hash(new string('0', 64)), false);
            ReviewedGameIntake intake = provisional with
            {
                IntakeFingerprint =
                    ReviewedGameIntakeFingerprintAuthority.Fingerprint(provisional)
            };
            var service = new FixedReviewedIntakeService(
                intake,
                [new Diagnostic(
                    "reviewed-intake-fixture-warning",
                    DiagnosticSeverity.Warning,
                    "The accepted fixture carries one typed warning.")]);
            var codec = new FaceGeomHairRegionsDocumentCodec(root);
            var store = new ReviewedGameIntakeArtifactStore(root, codec);
            var policy = new KOnlyWorkspacePolicy(
                root,
                ActorwrightWorkspace.ResolveProtectedRoot(root));
            var workflowLifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            var adapter = new ProtocolV2GoldenWorkflowAdapter(
                root, service, store, workflowLifecycle);
            IProtocolV2CommandAdapter[] productionAdapters =
                ProtocolV2GoldenWorkflowComposition.Create(root).ToArray();
            Assert(productionAdapters.Length == 8 &&
                   productionAdapters.SelectMany(item => item.Commands)
                       .OrderBy(item => item, StringComparer.Ordinal)
                       .SequenceEqual([
                            "gui", "npc assembly preflight",
                            "npc create-from-jslot", "npc finish analyze",
                            "npc finish apply", "npc finish verify",
                            "preset inspect", "preview npc", "workspace preflight"
                        ]),
                "production composition did not register exactly the nine admitted workflow command names");
            Assert(AgentCommandRegistry.All.Where(item =>
                        item.Readiness == ProtocolReadiness.V2)
                    .Select(item => item.Name)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .SequenceEqual([
                        "capabilities", "npc assembly preflight",
                        "npc create-from-jslot", "npc finish analyze",
                        "npc finish apply", "npc finish verify", "preset inspect",
                        "preview npc", "schema export", "version", "workspace preflight"
                    ]) &&
                   AgentCommandRegistry.All.Where(item =>
                           item.Readiness == ProtocolReadiness.Legacy)
                       .All(item =>
                           item.ContractStatus == AgentContractStatus.Complete &&
                           item.Authority.Length ==
                               Enum.GetValues<AgentAuthorityKind>().Length),
                "Current readiness or complete Legacy discovery metadata drifted");
            var journal = new CapturingJournal();
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(output, _ => journal, [adapter],
                AgentCommandRegistry.All);

            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
            [
                "workspace", "preflight", "--protocol", "2", "--json",
                "--game", "skyrimse", "--workspace-root", rootText,
                "--data-root", dataText, "--output-root", outputRootText,
                "--load-order", loadOrderText, "--intake-output", intakeOutputText,
                "--npc-editor-id", "FixtureNpc",
                "--workflow-output", workflowOutputText
            ]), CancellationToken.None);

            Assert(exit == CommandExitCode.Success,
                $"reviewed intake adapter exited {exit}: {output}");
            Assert(File.Exists(intakeOutputText),
                "reviewed intake artifact was not promoted");
            ReviewedGameIntakeDocumentAuthority reloaded =
                await codec.LoadReviewedIntakeAsync(new WorkspacePath(intakeOutputText),
                    CancellationToken.None);
            string[] lines = output.ToString().Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
            Assert(lines.Length == 1,
                "protocol-v2 workspace preflight wrote more than one stdout record");
            using JsonDocument envelope = JsonDocument.Parse(lines[0]);
            JsonElement result = envelope.RootElement.GetProperty("result");
            string[] actualResultProperties = result.EnumerateObject()
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            string[] declaredResultProperties = resultSchema is { } schema
                ? schema.GetProperty("oneOf")[1].GetProperty("properties")
                    .EnumerateObject().Select(item => item.Name)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()
                : [];
            Assert(resultSchema is null || actualResultProperties.SequenceEqual(
                    declaredResultProperties,
                    StringComparer.Ordinal),
                "workspace-preflight result schema does not match the actual full result projection");
            JsonElement warning = envelope.RootElement
                .GetProperty("diagnostics").EnumerateArray().Single(item =>
                    item.GetProperty("severity").GetString() == "warning");
            Assert(warning.GetProperty("code").GetString() ==
                       "reviewed-intake-warning" &&
                   warning.GetProperty("class").GetString() == "validation" &&
                   warning.GetProperty("message").GetString()!.Contains(
                       "reviewed-intake-fixture-warning",
                       StringComparison.Ordinal),
                "accepted reviewed-intake warning was not projected through typed semantics");
            JsonElement artifact = envelope.RootElement.GetProperty("artifacts")
                .EnumerateArray().Single(item => item.GetProperty("kind").GetString() ==
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake);
            JsonElement workflowArtifact = envelope.RootElement.GetProperty("artifacts")
                .EnumerateArray().Single(item => item.GetProperty("kind").GetString() ==
                    "workflow-bundle");
            string[] bindings = artifact.GetProperty("inputBindings")
                .EnumerateArray().Select(item => item.GetString()!).ToArray();
            string artifactSha = artifact.GetProperty("sha256").GetString()!;
            Assert(artifact.GetProperty("kind").GetString() ==
                       WorkflowArtifactKinds.ReviewedWorkspaceIntake &&
                   artifact.GetProperty("schemaOrMediaType").GetString() ==
                       "npcmanager-reviewed-game-intake/2" &&
                   artifact.GetProperty("path").GetString() == intakeOutputText &&
                   artifact.GetProperty("size").GetInt64() ==
                       reloaded.Document.ByteLength &&
                   string.Equals(
                       artifactSha,
                       reloaded.Document.Sha256.Value,
                       StringComparison.OrdinalIgnoreCase) &&
                   artifactSha.All(character =>
                       !char.IsLetter(character) || char.IsUpper(character)) &&
                   artifact.GetProperty("state").GetString() ==
                       "independentlyVerified" &&
                    bindings.SequenceEqual(bindings.Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)),
                "reviewed intake artifact metadata was not exact and independently verified");
            Assert(File.Exists(workflowOutputText) &&
                   workflowArtifact.GetProperty("path").GetString() == workflowOutputText,
                "workspace preflight did not persist and project its workflow bundle");
            string[] effects = envelope.RootElement.GetProperty("effects")
                .EnumerateArray()
                .Select(item =>
                    $"{item.GetProperty("kind").GetString()}|" +
                    $"{item.GetProperty("status").GetString()}|" +
                    item.GetProperty("scope").GetString())
                .ToArray();
            Assert(effects.Contains("readWorkspace|completed|workspace") &&
                   effects.Contains("writeNewArtifact|completed|k-local-output") &&
                   effects.Contains(
                       "appendLocalOperationJournal|attempted|workspace-local-journal") &&
                   effects.Contains(
                       "appendLocalOperationJournal|completed|workspace-local-journal"),
                "workspace preflight effects did not use the exact protocol vocabulary");
            AssertAuthority(envelope, "inputAdmission", "established");
            AssertAuthority(envelope, "deterministicMaterialization", "established");
            AssertAuthority(envelope, "independentStaticVerification", "established");
            AssertAuthority(envelope, "offEnginePreview", "notApplicable");
            AssertAuthority(envelope, "humanVisualAcceptance", "notApplicable");
            AssertAuthority(envelope, "gameRuntimeVerification", "required");
            AssertAuthority(envelope, "promotionApproval", "notApplicable");
            JsonElement next = envelope.RootElement.GetProperty("nextActions")
                .EnumerateArray().Single();
            string[] missing = next.GetProperty("missingPrerequisites")
                .EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert(next.GetProperty("command").GetString() ==
                       "preset inspect" &&
                   missing.SequenceEqual([
                       "--input", "--input-sha256", "--inspection-output",
                       "--workflow-output"
                   ]) &&
                   !lines[0].Contains("\"--intake\"", StringComparison.Ordinal),
                "workspace preflight invented a downstream intake option or unblocked the real contract gate");
            Assert(journal.Records.Count == 1,
                "protocol runner did not journal the reviewed intake transaction once");
            string[] journalEffects = journal.Records.Single().Effects
                .Select(item =>
                    $"{item.Kind}|{item.Status}|{item.Scope}")
                .ToArray();
            Assert(journalEffects.SequenceEqual(
                [
                    "ReadWorkspace|completed|workspace",
                    "WriteNewArtifact|completed|k-local-output",
                    "AppendLocalOperationJournal|attempted|workspace-local-journal"
                ],
                StringComparer.Ordinal),
                "workspace preflight journal record did not retain exact effect scopes");

            ProtocolCommandResult bindingFailure = await adapter.RunAsync(
                CommandLine.Parse(
                [
                    "workspace", "preflight", "--game", "skyrimse",
                    "--workspace-root", rootText, "--data-root", dataText,
                    "--output-root", outputRootText,
                    "--load-order", loadOrderText,
                    "--selected", "bad/path",
                    "--intake-output", Path.Combine(rootText, "binding.json"),
                    "--npc-editor-id", "FixtureNpc",
                    "--workflow-output", Path.Combine(rootText, "binding-workflow.json")
                ]),
                new string('B', 64),
                CancellationToken.None);
            AssertAuthority(bindingFailure,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Blocked);
            AssertAuthority(bindingFailure,
                AgentAuthorityKind.DeterministicMaterialization,
                AgentAuthorityState.Required);

            var admissionAdapter = new ProtocolV2GoldenWorkflowAdapter(
                root,
                new FixedReviewedIntakeService(
                    null,
                    [new Diagnostic(
                        "reviewed-intake-fixture-admission-error",
                        DiagnosticSeverity.Error,
                        "The fixture admission is refused.")]),
                store,
                workflowLifecycle);
            ProtocolCommandResult admissionFailure = await admissionAdapter.RunAsync(
                CommandLine.Parse(
                [
                    "workspace", "preflight", "--game", "skyrimse",
                    "--workspace-root", rootText, "--data-root", dataText,
                    "--output-root", outputRootText,
                    "--load-order", loadOrderText,
                    "--intake-output", Path.Combine(rootText, "admission.json"),
                    "--npc-editor-id", "FixtureNpc",
                    "--workflow-output", Path.Combine(rootText, "admission-workflow.json")
                ]),
                new string('C', 64),
                CancellationToken.None);
            AssertAuthority(admissionFailure,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Blocked);
            AssertAuthority(admissionFailure,
                AgentAuthorityKind.SourceProviderIdentity,
                AgentAuthorityState.Required);

            var outputRootParentFailureAdapter =
                new ProtocolV2GoldenWorkflowAdapter(
                    root,
                    new FixedReviewedIntakeService(
                        null,
                        [new Diagnostic(
                            "reviewed-intake-output-parent-missing",
                            DiagnosticSeverity.Error,
                            "The requested --output-root parent must already exist.")]),
                    store,
                    workflowLifecycle);
            ProtocolCommandResult outputRootParentFailure =
                await outputRootParentFailureAdapter.RunAsync(
                    CommandLine.Parse(
                    [
                        "workspace", "preflight", "--game", "skyrimse",
                        "--workspace-root", rootText, "--data-root", dataText,
                        "--output-root", Path.Combine(
                            rootText, "missing-output-parent", "reserved-output"),
                        "--load-order", loadOrderText,
                        "--intake-output", Path.Combine(
                            rootText, "output-root-parent-intake.json"),
                        "--npc-editor-id", "FixtureNpc",
                        "--workflow-output", Path.Combine(
                            rootText, "output-root-parent-workflow.json")
                    ]),
                    new string('E', 64),
                    CancellationToken.None);
            ProtocolDiagnostic outputRootParentDiagnostic =
                outputRootParentFailure.Diagnostics.Single();
            Assert(outputRootParentDiagnostic.Code ==
                       "reviewed-intake-output-parent-missing" &&
                   outputRootParentDiagnostic.Recovery is
                   {
                       Action: RecoveryAction.ChooseFreshOutput,
                       Option: "output-root"
                   } &&
                   outputRootParentDiagnostic.Recovery.ArtifactKind ==
                       WorkflowArtifactKinds.ReviewedWorkspaceIntake &&
                   outputRootParentDiagnostic.Code !=
                       ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists &&
                   outputRootParentDiagnostic.Recovery.Option != "intake-output",
                "A missing --output-root parent was projected as an intake-output collision instead of a typed output-root recovery.");

            ProtocolCommandResult persistenceFailure = await adapter.RunAsync(
                CommandLine.Parse(
                [
                    "workspace", "preflight", "--game", "skyrimse",
                    "--workspace-root", rootText, "--data-root", dataText,
                    "--output-root", outputRootText,
                    "--load-order", loadOrderText,
                    "--intake-output", intakeOutputText,
                    "--npc-editor-id", "FixtureNpc",
                    "--workflow-output", Path.Combine(rootText, "persistence-workflow.json")
                ]),
                new string('D', 64),
                CancellationToken.None);
            AssertAuthority(persistenceFailure,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established);
            AssertAuthority(persistenceFailure,
                AgentAuthorityKind.SourceProviderIdentity,
                AgentAuthorityState.Established);
            AssertAuthority(persistenceFailure,
                AgentAuthorityKind.DeterministicMaterialization,
                AgentAuthorityState.Blocked);
            AssertAuthority(persistenceFailure,
                AgentAuthorityKind.IndependentStaticVerification,
                AgentAuthorityState.Blocked);

            await AssertArtifactRefused(
                store,
                intake,
                new WorkspacePath(intakeOutputText),
                ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists);
            await AssertArtifactRefused(
                store,
                intake,
                new WorkspacePath(Path.Combine(dataText, "overlap.json")),
                ProtocolV2DiagnosticCodes.ReviewedIntakeOutputOverlap);
            await AssertArtifactRefused(
                store,
                intake,
                new WorkspacePath(intakeOutputText + ":stream"),
                ProtocolV2DiagnosticCodes.AlternateDataStreamRefused);

            ParsedCommand parsed = CommandLine.Parse(
            [
                "workspace", "preflight", "--game", "skyrimse",
                "--workspace-root", rootText, "--data-root", dataText,
                "--output-root", outputRootText, "--load-order", loadOrderText
            ]);
            ReviewedWorkspacePreflightBinding legacy =
                ReviewedWorkspacePreflightBinder.Bind(parsed, root,
                    requireExplicitWorkspace: false);
            Assert(legacy.Request == service.LastRequest,
                "protocol v1 and v2 did not share the exact reviewed preflight binder");
        }
        finally
        {
            if (Directory.Exists(rootText))
                Directory.Delete(rootText, recursive: true);
        }
    }

    private static async Task<JsonElement> AssertWorkspacePreflightSchemaExport()
    {
        CliBoundaryResult response = await ProtocolV2TestHost.RunAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--command", "workspace preflight"
        ]);
        Assert(response.ExitCode == 0 && response.StandardError == string.Empty,
            $"workspace-preflight schema export failed: {response.StandardOutput}");
        string[] lines = response.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length == 1,
            "workspace-preflight schema export emitted multiple records");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement definition = envelope.RootElement.GetProperty("result")
            .GetProperty("resultSchemas").EnumerateArray().Single();
        JsonElement schema = definition.GetProperty("jsonSchema");
        string[] required = schema.GetProperty("oneOf")[1]
            .GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        string[] expectedRequired =
        [
            "schemaVersion", "edition", "isAccepted", "workspaceRoot",
            "dataRoot", "loadOrderPath", "outputRoot", "loadOrderHash",
            "assetIndexFingerprint", "intakeFingerprint", "plugins",
            "bodySidecarCount", "generatedPluginCount",
            "generatedSidecarCount", "bodySidecars", "generatedPlugins",
            "generatedSidecars", "assetProviderCount", "runtimeAuthority",
            "diagnostics"
        ];
        Array.Sort(expectedRequired, StringComparer.Ordinal);
        Assert(definition.GetProperty("schemaIdentifier").GetString() ==
                   AgentProtocolSchemaIds.WorkspacePreflightResult &&
               schema.GetProperty("$id").GetString() ==
                   AgentProtocolSchemaIds.WorkspacePreflightResult &&
               schema.GetProperty("oneOf").GetArrayLength() == 2 &&
               schema.GetProperty("oneOf")[0].GetProperty("properties")
                   .GetProperty("isAccepted").GetProperty("const")
                   .ValueKind == JsonValueKind.False &&
               required.SequenceEqual(expectedRequired,
                   StringComparer.Ordinal) &&
               schema.GetProperty("$defs").GetProperty("diagnostic")
                   .GetProperty("properties").GetProperty("severity")
                   .GetProperty("enum").GetArrayLength() == 3,
            "workspace-preflight schema export was missing the exact result projection");
        return schema.Clone();
    }

    private static void AssertAuthority(JsonDocument envelope, string kind,
        string state) =>
        Assert(envelope.RootElement.GetProperty("authority").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == kind &&
                             item.GetProperty("state").GetString() == state),
            $"authority {kind} did not remain {state}");

    private static void AssertAuthority(
        ProtocolCommandResult result,
        AgentAuthorityKind kind,
        AgentAuthorityState state) =>
        Assert(result.Authority.Single(item => item.Kind == kind).State == state,
            $"refused intake authority {kind} did not remain {state}");

    private static async Task AssertArtifactRefused(
        ReviewedGameIntakeArtifactStore store,
        ReviewedGameIntake intake,
        WorkspacePath destination,
        string expectedCode)
    {
        try
        {
            await store.WriteNewAsync(
                intake,
                destination,
                CancellationToken.None);
            throw new InvalidOperationException(
                $"artifact destination was accepted instead of '{expectedCode}'");
        }
        catch (ReviewedGameIntakeArtifactException exception)
        {
            Assert(exception.Code == expectedCode,
                $"artifact refusal was '{exception.Code}' instead of '{expectedCode}'");
        }
    }

    private static Sha256Hash Sha(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedReviewedIntakeService(
        ReviewedGameIntake? intake,
        ImmutableArray<Diagnostic> diagnostics)
        : IReviewedGameIntakeService
    {
        public ReviewedGameIntakeRequest? LastRequest { get; private set; }

        public ValueTask<ReviewedGameIntakeResult> ReviewAsync(
            ReviewedGameIntakeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(new ReviewedGameIntakeResult(
                request.Edition, intake?.Plugins ?? [], intake, diagnostics));
        }
    }

    private sealed class CapturingJournal : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }
}
