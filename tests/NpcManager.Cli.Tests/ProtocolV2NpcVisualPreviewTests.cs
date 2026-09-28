using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2NpcVisualPreviewTests
{
    private const string RequestDigest =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly string[] RequiredWorkflowOptions =
    [
        "workflow-bundle", "workflow-bundle-sha256", "workflow-output"
    ];
    private static readonly string[] GuiWorkflowOptions =
    [
        "launch", "executable", "workflow-bundle", "workflow-bundle-sha256"
    ];
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly byte[] Png = CreatePng(900, 900);
    private static readonly ImmutableArray<Diagnostic> ExposureAdvisories =
    [
        new Diagnostic(
            "npc-preview-body-underexposed",
            DiagnosticSeverity.Warning,
            "Body luminance measured front 54.25 and back 48.32; threshold 55."),
        new Diagnostic(
            "npc-preview-hands-underexposed",
            DiagnosticSeverity.Warning,
            "Hand luminance measured 39.75; threshold 40.")
    ];

    public static async Task RunAsync()
    {
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            $"protocol-v2-npc-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var policy = new KOnlyWorkspacePolicy(
                root, new WorkspacePath(@"F:\ExampleGame"));
            var lifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            PreviewFixture fixture = PreviewFixture.Create(root, lifecycle);

            AssertRegistryAndSchema(root);
            await AssertSuccessAsync(root, policy, lifecycle, fixture);
            await AssertExposureAdvisoriesAsync(
                root, policy, lifecycle, fixture);
            await AssertPackageAdmissionCancellationAsync(
                root, policy, lifecycle, fixture);
            await AssertLateSemanticIntakeFailureAsync(
                root, policy, lifecycle, fixture);
            await AssertForeignIntakeRefusedBeforeCompositionAsync(
                root, policy, lifecycle, fixture);
            await AssertPostPublicationWorkflowFailureAsync(
                root, policy, lifecycle, fixture);
            await AssertPostPublicationWorkflowPersistenceFailureAsync(
                root, policy, lifecycle, fixture);
            await AssertRefusalsAsync(root, policy, lifecycle, fixture);
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static void AssertRegistryAndSchema(WorkspacePath root)
    {
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("preview npc");
        Require(contract.Readiness == ProtocolReadiness.V2 &&
                contract.ResultSchemaIds.SequenceEqual(
                    [AgentProtocolSchemaIds.NpcVisualPreviewResult],
                    StringComparer.Ordinal) &&
                RequiredWorkflowOptions.All(name => contract.Options.Single(option =>
                    option.CliName == name).Required),
            "Preview is not one exact resumable protocol-v2 command.");
        JsonElement export = ProtocolV2SchemaService.RenderInline("preview npc");
        JsonElement schema = export.GetProperty("resultSchemas")
            .EnumerateArray().Single(item => string.Equals(
                item.GetProperty("schemaIdentifier").GetString(),
                AgentProtocolSchemaIds.NpcVisualPreviewResult,
                StringComparison.Ordinal));
        Require(!schema.GetProperty("jsonSchema")
                .GetProperty("additionalProperties").GetBoolean(),
            "Preview result schema is not closed.");
        Require(ProtocolV2GoldenWorkflowComposition.Create(root)
                .Count(adapter => adapter.Commands.Contains(
                    "preview npc", StringComparer.Ordinal)) == 1,
            "Production protocol-v2 composition does not register exactly one preview adapter.");
        AgentCommandContract gui = AgentCommandRegistry.GetRequired("gui");
        Require(gui.Readiness == ProtocolReadiness.Legacy &&
                gui.Options.Select(option => option.CliName).SequenceEqual(
                    GuiWorkflowOptions, StringComparer.Ordinal),
            "The deferred Desktop target lacks exact metadata for the blocked review action.");
    }

    private static async Task AssertSuccessAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        var composer = new FixtureComposer();
        var reader = new NpcVisualPreviewArtifactReader(root);
        WorkspacePath previewOutput = Child(root, "preview", "success");
        WorkspacePath workflowOutput = Child(
            root, "workflow", "preview.json");
        WorkspacePath primarySemantic = Child(
            previewOutput, "face-front.png");
        var terminalProbe = new TerminalArtifactProbe(
            primarySemantic,
            workflowOutput);
        var journal = new CallbackJournal(
            () => terminalProbe.Probe("journal append"));
        using var outputBuffer = new StringWriter();
        using var output = new WriteLineCallbackWriter(
            outputBuffer,
            () => terminalProbe.Probe("envelope write"));
        var runner = new ProtocolV2Runner(
            output, _ => journal,
            [Adapter(root, policy, lifecycle, composer, reader)],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(
                previewOutput,
                workflowOutput),
            CancellationToken.None);

        string[] lines = outputBuffer.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit == CommandExitCode.Success && lines.Length == 1,
            "Preview did not emit one successful envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement result = value.GetProperty("result");
        Require(value.GetProperty("outcome").GetString() == "succeeded" &&
                value.GetProperty("diagnostics").GetArrayLength() == 0 &&
                result.GetProperty("composed").GetBoolean() &&
                !result.GetProperty("runtimeAuthority").GetBoolean() &&
                IsUpperSha(result.GetProperty("bundleSha256").GetString()) &&
                IsUpperSha(result.GetProperty("hashManifestSha256").GetString()) &&
                result.GetProperty("views").GetArrayLength() == 6,
            "Preview result omitted independently readable outputs.");
        Require(value.GetProperty("effects").EnumerateArray()
                .Select(Effect).SequenceEqual([
                    "readWorkspace|completed|workspace",
                    "writeNewArtifact|completed|k-local-output",
                    "appendLocalOperationJournal|attempted|workspace-local-journal",
                    "appendLocalOperationJournal|completed|workspace-local-journal"
                ], StringComparer.Ordinal),
            "Preview effects changed.");
        JsonElement[] artifacts = value.GetProperty("artifacts")
            .EnumerateArray().ToArray();
        JsonElement preview = artifacts.Single(item =>
            item.GetProperty("kind").GetString() ==
                WorkflowArtifactKinds.NpcPreviewManifest);
        JsonElement workflow = artifacts.Single(item =>
            item.GetProperty("kind").GetString() == "workflow-bundle");
        Require(artifacts.Length == 2 &&
                preview.GetProperty("state").GetString() ==
                    "independentlyVerified" &&
                IsUpperSha(preview.GetProperty("sha256").GetString()) &&
                IsUpperSha(workflow.GetProperty("sha256").GetString()) &&
                Authority(value, "offEnginePreview") == "established" &&
                Authority(value, "humanVisualAcceptance") == "required" &&
                Authority(value, "gameRuntimeVerification") == "required" &&
                Authority(value, "promotionApproval") == "required",
            "Preview overclaimed authority or omitted its resumable evidence.");
        terminalProbe.RequireReleasedAndUnchanged(
            workflow.GetProperty("sha256").GetString()!);

        AgentWorkflowBundleDocument reopened = new AgentWorkflowBundleCodec(
                policy, root)
            .Load(
                new WorkspacePath(workflow.GetProperty("path").GetString()!),
                workflow.GetProperty("sha256").GetString()!);
        Require(reopened.Bundle.Artifacts.Select(item => item.Kind)
                .SequenceEqual([
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest
                ], StringComparer.Ordinal) &&
                reopened.Bundle.Artifacts.Single(item => item.Kind ==
                        WorkflowArtifactKinds.NpcPreviewManifest)
                    .InputArtifactHashes.SequenceEqual(
                        new[] { fixture.IntakeSha256, fixture.PackageSha256 }
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal),
                        StringComparer.Ordinal) &&
                reopened.Bundle.Npc.Plugin == "BuildNpc.esp" &&
                reopened.Bundle.Npc.LocalFormId == "00000800",
            "Preview workflow lost exact package or NPC identity.");
        using NpcVisualPreviewArtifactDocument loaded = reader.Load(
            new WorkspacePath(result.GetProperty("bundlePath").GetString()!),
            result.GetProperty("bundleSha256").GetString()!);
        Require(loaded.Value.Views.Length == 6 &&
                loaded.Artifacts.All(item => IsUpperSha(item.Sha256)) &&
                composer.Calls == 1 && journal.Records.Count == 1 &&
                journal.Probes == 1 && output.Probes == 1,
            "Preview evidence was not independently reopened exactly once.");
    }

    private static async Task AssertExposureAdvisoriesAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        WorkspacePath previewOutput = Child(
            root, "preview", "exposure-advisory");
        WorkspacePath workflowOutput = Child(
            root, "workflow", "exposure-advisory.json");
        var composer = new FixtureComposer(
            FixtureMode.ExposureAdvisory);
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(previewOutput, workflowOutput),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit == CommandExitCode.Success && lines.Length == 1,
            "Exposure advisories did not preserve a successful protocol envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement result = value.GetProperty("result");
        JsonElement[] diagnostics = result.GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Require(value.GetProperty("outcome").GetString() == "succeeded" &&
                value.GetProperty("diagnostics").GetArrayLength() == 0 &&
                result.GetProperty("composed").GetBoolean() &&
                !result.GetProperty("runtimeAuthority").GetBoolean() &&
                result.GetProperty("status").GetString() ==
                    "offEnginePreviewRuntimeRequired" &&
                IsUpperSha(result.GetProperty("bundleSha256").GetString()) &&
                IsUpperSha(result.GetProperty("hashManifestSha256").GetString()) &&
                result.GetProperty("contactSheetPath").ValueKind ==
                    JsonValueKind.String &&
                File.Exists(workflowOutput.Value) &&
                diagnostics.Any(item =>
                    item.GetProperty("code").GetString() ==
                        "npc-preview-body-underexposed" &&
                    item.GetProperty("severity").GetString() == "warning" &&
                    item.GetProperty("message").GetString()!.Contains(
                        "54.25", StringComparison.Ordinal) &&
                    item.GetProperty("message").GetString()!.Contains(
                        "48.32", StringComparison.Ordinal) &&
                    item.GetProperty("message").GetString()!.Contains(
                        "55", StringComparison.Ordinal)) &&
                diagnostics.Any(item =>
                    item.GetProperty("code").GetString() ==
                        "npc-preview-hands-underexposed" &&
                    item.GetProperty("severity").GetString() == "warning" &&
                    item.GetProperty("message").GetString()!.Contains(
                        "39.75", StringComparison.Ordinal) &&
                    item.GetProperty("message").GetString()!.Contains(
                        "40", StringComparison.Ordinal)) &&
                Authority(value, "offEnginePreview") == "established" &&
                Authority(value, "humanVisualAcceptance") == "required" &&
                Authority(value, "gameRuntimeVerification") == "required" &&
                Authority(value, "promotionApproval") == "required" &&
                composer.Calls == 1,
            "Exposure advisories were lost or overclaimed by protocol-v2 preview.");
    }

    private static async Task AssertPackageAdmissionCancellationAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        WorkspacePath previewOutput = Child(
            root, "preview", "package-admission-cancelled");
        WorkspacePath workflowOutput = Child(
            root, "workflow", "package-admission-cancelled.json");
        var composer = new FixtureComposer();
        var admission = new CancellingPackageAdmission();
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root),
                admission)],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(previewOutput, workflowOutput),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit == CommandExitCode.Cancelled && lines.Length == 1,
            "Package-admission cancellation did not emit one cancelled envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        JsonElement result = value.GetProperty("result");
        Require(value.GetProperty("outcome").GetString() == "cancelled" &&
                diagnostic.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled &&
                diagnostic.GetProperty("class").GetString() ==
                    "cancellation" &&
                recovery.GetProperty("action").GetString() ==
                    "retryUnchanged" &&
                recovery.GetProperty("option").GetString() ==
                    "package-manifest" &&
                recovery.GetProperty("artifactKind").GetString() ==
                    WorkflowArtifactKinds.NpcPackageManifest &&
                recovery.GetProperty("constraint").GetString() ==
                    "Retry the exact verified package admission unchanged." &&
                recovery.GetProperty("retryUnchangedSafe").GetBoolean() &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                HasConservativeRefusalAuthority(value) &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                HasNoPreviewOutputs(result) &&
                admission.PreviewCalls == 1 &&
                composer.Calls == 0 &&
                !File.Exists(workflowOutput.Value) &&
                !Directory.Exists(previewOutput.Value) &&
                !File.Exists(previewOutput.Value),
            "Package-admission cancellation overstated effects, authority, or outputs.");
    }

    private static async Task AssertLateSemanticIntakeFailureAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        (WorkspacePath intake, WorkspacePath workflow, string workflowSha256) =
            fixture.CreateSemanticIntakeFailureWorkflow(
                lifecycle,
                "semantic-intake-failure.json");
        WorkspacePath previewOutput = Child(
            root, "preview", "semantic-intake-failure");
        WorkspacePath workflowOutput = Child(
            root, "workflow", "semantic-intake-failure-output.json");
        Dictionary<string, string> options = fixture.Options(
            previewOutput,
            workflowOutput);
        options["intake"] = intake.Value;
        options["workflow-bundle"] = workflow.Value;
        options["workflow-bundle-sha256"] = workflowSha256;
        var composer = new FixtureComposer();
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            PreviewFixture.Parse(options),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit == CommandExitCode.ValidationFailure &&
                lines.Length == 1,
            "Late semantic intake failure did not emit one validation envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        JsonElement result = value.GetProperty("result");
        Require(value.GetProperty("outcome").GetString() == "failed" &&
                diagnostic.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed &&
                recovery.GetProperty("action").GetString() ==
                    "correctInput" &&
                recovery.GetProperty("option").GetString() == "intake" &&
                recovery.GetProperty("artifactKind").GetString() ==
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                HasConservativeRefusalAuthority(value) &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                HasNoPreviewOutputs(result) &&
                composer.Calls == 0 &&
                !File.Exists(workflowOutput.Value) &&
                !Directory.Exists(previewOutput.Value) &&
                !File.Exists(previewOutput.Value),
            "Late semantic intake failure did not preserve the exact refusal boundary.");
    }

    private static async Task AssertForeignIntakeRefusedBeforeCompositionAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        WorkspacePath foreignIntake = PreviewFixture.CreateForeignIntake(root);
        WorkspacePath workflowOutput = Child(
            root, "workflow", "foreign-intake.json");
        WorkspacePath previewOutput = Child(
            root, "preview", "foreign-intake");
        Dictionary<string, string> options = fixture.Options(
            previewOutput,
            workflowOutput);
        options["intake"] = foreignIntake.Value;
        var composer = new FixtureComposer();

        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(
            PreviewFixture.Parse(options),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit != CommandExitCode.Success && lines.Length == 1,
            "Foreign preview intake did not emit one refusal envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        Require(value.GetProperty("diagnostics")
                    .EnumerateArray().Single()
                    .GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                composer.Calls == 0 &&
                !Directory.Exists(previewOutput.Value) &&
                !File.Exists(workflowOutput.Value),
            "Foreign reviewed intake reached preview composition or used the wrong diagnostic.");
    }

    private static async Task AssertPostPublicationWorkflowFailureAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        WorkspacePath workflowOutput = Child(
            root, "workflow", "preview-published-workflow-collision.json");
        WorkspacePath previewOutput = Child(
            root, "preview", "published-workflow-collision");
        var composer = new FixtureComposer(
            FixtureMode.Success,
            workflowOutput);
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(previewOutput, workflowOutput),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit != CommandExitCode.Success && lines.Length == 1,
            "Post-preview workflow collision did not emit one refusal envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement[] artifacts = value.GetProperty("artifacts")
            .EnumerateArray().ToArray();
        JsonElement preview = artifacts.Single(item =>
            item.GetProperty("kind").GetString() ==
                WorkflowArtifactKinds.NpcPreviewManifest);
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        string previewPath = preview.GetProperty("path").GetString()!;
        Require(artifacts.Length == 1 &&
                !artifacts.Any(item => item.GetProperty("kind").GetString() ==
                    "workflow-bundle") &&
                Directory.Exists(previewOutput.Value) &&
                File.Exists(previewPath) &&
                preview.GetProperty("sha256").GetString() ==
                    HashFile(previewPath) &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|completed|k-local-output",
                        "writeNewArtifact|failed|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                composer.Calls == 1,
            "Published preview/workflow split commit was not projected truthfully.");
        byte[] occupiedBytes = File.ReadAllBytes(workflowOutput.Value);
        Require(occupiedBytes.AsSpan().SequenceEqual(
                    FixtureComposer.WorkflowCollisionBytes) &&
                new FileInfo(workflowOutput.Value).Length ==
                    FixtureComposer.WorkflowCollisionBytes.LongLength &&
                HashFile(workflowOutput.Value) == Convert.ToHexString(
                    SHA256.HashData(FixtureComposer.WorkflowCollisionBytes)) &&
                diagnostic.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused &&
                diagnostic.GetProperty("class").GetString() == "security" &&
                recovery.GetProperty("action").GetString() ==
                    "chooseFreshOutput" &&
                recovery.GetProperty("option").GetString() ==
                    "workflow-output" &&
                Authority(value, "inputAdmission") == "established" &&
                Authority(value, "sourceProviderIdentity") == "established" &&
                Authority(value, "deterministicMaterialization") ==
                    "established" &&
                Authority(value, "independentStaticVerification") ==
                    "established" &&
                Authority(value, "offEnginePreview") == "established",
            "Published preview collision changed occupied bytes, diagnostic recovery, or stage authority.");
        using NpcVisualPreviewArtifactDocument reopened =
            new NpcVisualPreviewArtifactReader(root).Load(
                new WorkspacePath(previewPath),
                preview.GetProperty("sha256").GetString()!);
        Require(reopened.Artifacts.All(item =>
                    File.Exists(item.Path.Value) &&
                    item.Sha256 == HashFile(item.Path.Value)),
            "Advertised preview closure did not remain physically hash-valid.");
    }

    private static async Task AssertPostPublicationWorkflowPersistenceFailureAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        WorkspacePath workflowOutput = Child(
            root,
            "workflow",
            new string('p', 300) + ".json");
        WorkspacePath previewOutput = Child(
            root,
            "preview",
            "published-workflow-persistence-failure");
        var composer = new FixtureComposer();
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(previewOutput, workflowOutput),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit != CommandExitCode.Success && lines.Length == 1,
            "Post-preview workflow persistence failure did not emit one refusal envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement[] artifacts = value.GetProperty("artifacts")
            .EnumerateArray().ToArray();
        JsonElement preview = artifacts.Single(item =>
            item.GetProperty("kind").GetString() ==
                WorkflowArtifactKinds.NpcPreviewManifest);
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        string previewPath = preview.GetProperty("path").GetString()!;
        Require(artifacts.Length == 1 &&
                !File.Exists(workflowOutput.Value) &&
                File.Exists(previewPath) &&
                preview.GetProperty("sha256").GetString() ==
                    HashFile(previewPath) &&
                diagnostic.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.WorkflowBundlePersistenceFailed &&
                diagnostic.GetProperty("class").GetString() == "operation" &&
                recovery.GetProperty("action").GetString() ==
                    "repairEnvironment" &&
                recovery.GetProperty("option").GetString() ==
                    "workflow-output" &&
                recovery.GetProperty("artifactKind").GetString() ==
                    "workflow-bundle" &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|completed|k-local-output",
                        "writeNewArtifact|failed|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                Authority(value, "inputAdmission") == "established" &&
                Authority(value, "offEnginePreview") == "established" &&
                composer.Calls == 1,
            "Published preview persistence failure changed recovery, effects, artifact, or authority.");
    }

    private static async Task AssertRefusalsAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        PreviewFixture fixture)
    {
        (string Name, FixtureMode Mode,
            Action<Dictionary<string, string>> Mutate,
            int ComposerCalls, string ReadStatus, string WriteStatus,
            string DiagnosticCode, bool ExpectOutputAbsent,
            string? RecoveryAction, string? RecoveryOption,
            string? RecoveryArtifactKind)[] cases =
        [
            ("stale workflow", FixtureMode.Success,
                options => options["workflow-bundle-sha256"] = new string('A', 64),
                0, "refused", "refused",
                ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed,
                true, "correctInput", "workflow-bundle-sha256",
                "workflow-bundle"),
            ("stale package", FixtureMode.Success,
                options => options["expected-package-sha256"] = new string('C', 64),
                0, "completed", "refused",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                true, null, null, null),
            ("wrong NPC identity", FixtureMode.Success,
                options => options["form"] = "00000801",
                0, "completed", "refused",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                true, null, null, null),
            ("renderer unavailable", FixtureMode.RendererUnavailable, _ => { },
                1, "completed", "failed",
                ProtocolV2DiagnosticCodes.NpcPreviewOperationFailed,
                true, null, null, null),
            ("malformed preview", FixtureMode.MalformedBundle, _ => { },
                1, "completed", "failed",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                false, "chooseFreshOutput", "output-root",
                WorkflowArtifactKinds.NpcPreviewManifest),
            ("view drift", FixtureMode.ViewDrift, _ => { },
                1, "completed", "failed",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                false, "chooseFreshOutput", "output-root",
                WorkflowArtifactKinds.NpcPreviewManifest),
            ("source binding drift", FixtureMode.SourceBindingDrift, _ => { },
                1, "completed", "failed",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                false, "chooseFreshOutput", "output-root",
                WorkflowArtifactKinds.NpcPreviewManifest)
        ];
        var index = 0;
        foreach ((string name, FixtureMode mode,
                     Action<Dictionary<string, string>> mutate,
                     int composerCalls, string readStatus,
                     string writeStatus, string diagnosticCode,
                     bool expectOutputAbsent, string? recoveryAction,
                     string? recoveryOption,
                     string? recoveryArtifactKind) in cases)
        {
            index++;
            WorkspacePath workflowOutput = Child(
                root, "workflow", $"refusal-{index}.json");
            WorkspacePath previewOutput = Child(
                root, "preview", $"refusal-{index}");
            Dictionary<string, string> options = fixture.Options(
                previewOutput, workflowOutput);
            mutate(options);
            await RequireRefusalAsync(
                root, policy, lifecycle,
                PreviewFixture.Parse(options), workflowOutput,
                previewOutput, new FixtureComposer(mode), name,
                composerCalls,
                readStatus,
                writeStatus,
                diagnosticCode,
                expectOutputAbsent,
                recoveryAction,
                recoveryOption,
                recoveryArtifactKind,
                cancellationToken: CancellationToken.None);
            if (mode == FixtureMode.ViewDrift)
            {
                WorkspacePath drifted = Child(
                    previewOutput,
                    "face-front.png");
                byte[] bytes = File.ReadAllBytes(drifted.Value);
                Require(bytes.AsSpan().EndsWith("drift"u8),
                    "Preview revalidation failure did not preserve the unadvertised drifted bytes.");
            }
        }

        index++;
        WorkspacePath racedWorkflow = Child(
            root, "workflow", $"refusal-{index}.json");
        WorkspacePath racedOutput = Child(
            root, "preview", $"refusal-{index}");
        var racedComposer = new FixtureComposer(
            FixtureMode.ConcurrentOutputCollision);
        await RequireRefusalAsync(
            root,
            policy,
            lifecycle,
            fixture.Command(racedOutput, racedWorkflow),
            racedWorkflow,
            racedOutput,
            racedComposer,
            "post-admission foreign output collision",
            1,
            "completed",
            "failed",
            ProtocolV2DiagnosticCodes.NpcPreviewOperationFailed,
            expectOutputAbsent: false,
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "output-root",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcPreviewManifest,
            cancellationToken: CancellationToken.None);
        WorkspacePath sentinel = Child(
            racedOutput,
            FixtureComposer.ForeignCollisionName);
        byte[] preserved = File.ReadAllBytes(sentinel.Value);
        Require(preserved.AsSpan().SequenceEqual(
                    FixtureComposer.ForeignCollisionBytes) &&
                new FileInfo(sentinel.Value).Length ==
                    FixtureComposer.ForeignCollisionBytes.LongLength &&
                HashFile(sentinel.Value) == Convert.ToHexString(
                    SHA256.HashData(FixtureComposer.ForeignCollisionBytes)),
            "Post-admission preview collision changed or deleted foreign sentinel bytes.");

        foreach ((string name, WorkspacePath previewOutput,
                     WorkspacePath workflowOutput, string recoveryOption,
                     string recoveryArtifactKind) in new[]
                 {
                     (
                         "preview output beneath package root",
                         Child(root, "package", "unsafe-preview-output"),
                         Child(root, "workflow", "unsafe-preview-package.json"),
                         "output-root",
                         WorkflowArtifactKinds.NpcPreviewManifest
                     ),
                     (
                         "preview output beneath intake DataRoot",
                         Child(root, "inputs", "Data", "unsafe-preview-output"),
                         Child(root, "workflow", "unsafe-preview-intake.json"),
                         "output-root",
                         WorkflowArtifactKinds.NpcPreviewManifest
                     ),
                     (
                         "workflow output beneath package root",
                         Child(root, "preview", "unsafe-workflow-package"),
                         Child(root, "package", "unsafe-workflow.json"),
                         "workflow-output",
                         "workflow-bundle"
                     ),
                     (
                         "workflow output beneath intake DataRoot",
                         Child(root, "preview", "unsafe-workflow-intake"),
                         Child(root, "inputs", "Data", "unsafe-workflow.json"),
                         "workflow-output",
                         "workflow-bundle"
                     )
                 })
        {
            await RequireRefusalAsync(
                root,
                policy,
                lifecycle,
                fixture.Command(previewOutput, workflowOutput),
                workflowOutput,
                previewOutput,
                new FixtureComposer(FixtureMode.RendererUnavailable),
                name,
                0,
                "completed",
                "refused",
                ProtocolV2DiagnosticCodes.ProtectedRootRefused,
                expectedRecoveryAction: "chooseFreshOutput",
                expectedRecoveryOption: recoveryOption,
                expectedRecoveryArtifactKind: recoveryArtifactKind,
                expectAdmittedInputAuthority: true,
                expectedOutcome: "refused",
                cancellationToken: CancellationToken.None);
        }

        WorkspacePath overlappingOutputs = Child(
            root, "preview", "workflow-output-overlap");
        await RequireRefusalAsync(
            root,
            policy,
            lifecycle,
            fixture.Command(overlappingOutputs, overlappingOutputs),
            overlappingOutputs,
            overlappingOutputs,
            new FixtureComposer(),
            "preview and workflow output exact overlap",
            0,
            "refused",
            "refused",
            ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "workflow-output",
            expectedRecoveryArtifactKind: "workflow-bundle",
            expectedOutcome: "refused",
            cancellationToken: CancellationToken.None);

        WorkspacePath nestedPreviewOutput = Child(
            root, "preview", "nested-output-overlap");
        WorkspacePath nestedWorkflowOutput = Child(
            nestedPreviewOutput, "workflow.json");
        await RequireRefusalAsync(
            root,
            policy,
            lifecycle,
            fixture.Command(nestedPreviewOutput, nestedWorkflowOutput),
            nestedWorkflowOutput,
            nestedPreviewOutput,
            new FixtureComposer(),
            "workflow output nested beneath preview output",
            0,
            "refused",
            "refused",
            ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "workflow-output",
            expectedRecoveryArtifactKind: "workflow-bundle",
            expectedOutcome: "refused",
            cancellationToken: CancellationToken.None);

        index++;
        WorkspacePath adapterCancelledWorkflow = Child(
            root, "workflow", $"refusal-{index}.json");
        WorkspacePath adapterCancelledOutput = Child(
            root, "preview", $"refusal-{index}");
        await RequireRefusalAsync(
            root,
            policy,
            lifecycle,
            fixture.Command(
                adapterCancelledOutput,
                adapterCancelledWorkflow),
            adapterCancelledWorkflow,
            adapterCancelledOutput,
            new FixtureComposer(FixtureMode.Cancelled),
            "renderer cancellation",
            1,
            "completed",
            "failed",
            ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
            expectOutputAbsent: false,
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "output-root",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcPreviewManifest,
            expectedRetryUnchangedSafe: false,
            expectedOutcome: "cancelled",
            cancellationToken: CancellationToken.None);
        WorkspacePath cancelledSentinel = Child(
            adapterCancelledOutput,
            FixtureComposer.ForeignCollisionName);
        Require(File.ReadAllBytes(cancelledSentinel.Value).AsSpan()
                    .SequenceEqual(FixtureComposer.ForeignCollisionBytes) &&
                new FileInfo(cancelledSentinel.Value).Length ==
                    FixtureComposer.ForeignCollisionBytes.LongLength &&
                HashFile(cancelledSentinel.Value) == Convert.ToHexString(
                    SHA256.HashData(FixtureComposer.ForeignCollisionBytes)),
            "Renderer cancellation changed or deleted its unverified partial output.");

        foreach ((string name, ImmutableArray<string> inputHashes) in new[]
                 {
                     ("missing package intake hash", ImmutableArray<string>.Empty),
                     ("wrong package intake hash", ImmutableArray.Create(
                         new string('D', 64)))
                 })
        {
            index++;
            (WorkspacePath workflow, string workflowSha256) =
                fixture.CreatePackageInputWorkflow(
                    lifecycle,
                    $"package-input-{index}.json",
                    inputHashes);
            WorkspacePath workflowOutput = Child(
                root, "workflow", $"package-input-refusal-{index}.json");
            WorkspacePath previewOutput = Child(
                root, "preview", $"package-input-refusal-{index}");
            Dictionary<string, string> options = fixture.Options(
                previewOutput, workflowOutput);
            options["workflow-bundle"] = workflow.Value;
            options["workflow-bundle-sha256"] = workflowSha256;
            await RequireRefusalAsync(
                root, policy, lifecycle,
                PreviewFixture.Parse(options), workflowOutput,
                previewOutput, new FixtureComposer(), name,
                0,
                "completed",
                "refused",
                ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed,
                cancellationToken: CancellationToken.None);
        }

        WorkspacePath occupied = Child(root, "preview", "occupied");
        Directory.CreateDirectory(occupied.Value);
        WorkspacePath occupiedWorkflow = Child(
            root, "workflow", "occupied.json");
        await RequireRefusalAsync(
            root, policy, lifecycle,
            fixture.Command(occupied, occupiedWorkflow), occupiedWorkflow,
            occupied, new FixtureComposer(), "occupied output",
            0,
            "refused",
            "refused",
            ProtocolV2DiagnosticCodes.NpcPreviewValidationFailed,
            expectOutputAbsent: false,
            cancellationToken: CancellationToken.None);
        Require(Directory.Exists(occupied.Value),
            "Preview refusal deleted a destination it did not own.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        WorkspacePath cancelledOutput = Child(
            root, "preview", "cancelled");
        WorkspacePath cancelledWorkflow = Child(
            root, "workflow", "cancelled.json");
        await RequireRunnerBoundaryCancellationAsync(
            root, policy, lifecycle,
            fixture.Command(cancelledOutput, cancelledWorkflow),
            cancelledWorkflow,
            cancelledOutput,
            new FixtureComposer(FixtureMode.Cancelled),
            cancellation.Token);

        File.AppendAllText(fixture.Intake.Value, "same-path-drift");
        WorkspacePath staleIntakeOutput = Child(
            root, "preview", "same-path-stale-intake");
        WorkspacePath staleIntakeWorkflow = Child(
            root, "workflow", "same-path-stale-intake.json");
        await RequireRefusalAsync(
            root, policy, lifecycle,
            fixture.Command(staleIntakeOutput, staleIntakeWorkflow),
            staleIntakeWorkflow,
            staleIntakeOutput,
            new FixtureComposer(),
            "same-path stale intake bytes",
            0,
            "refused",
            "refused",
            ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed,
            expectedRecoveryAction: "correctInput",
            expectedRecoveryOption: "workflow-bundle",
            expectedRecoveryArtifactKind: "workflow-bundle",
            cancellationToken: CancellationToken.None);
    }

    private static async Task RequireRefusalAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        ParsedCommand command,
        WorkspacePath workflowOutput,
        WorkspacePath previewOutput,
        FixtureComposer composer,
        string name,
        int expectedComposerCalls,
        string expectedReadStatus,
        string expectedWriteStatus,
        string expectedDiagnosticCode,
        bool expectOutputAbsent = true,
        string? expectedRecoveryAction = null,
        string? expectedRecoveryOption = null,
        string? expectedRecoveryArtifactKind = null,
        bool expectAdmittedInputAuthority = false,
        bool? expectedRetryUnchangedSafe = null,
        string expectedOutcome = "failed",
        CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output, _ => new CapturingJournal(),
            [Adapter(
                root, policy, lifecycle, composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(command, cancellationToken);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        bool workflowExists = File.Exists(workflowOutput.Value);
        bool previewDirectoryExists = Directory.Exists(previewOutput.Value);
        bool previewFileExists = File.Exists(previewOutput.Value);
        using JsonDocument? envelope = lines.Length == 1
            ? JsonDocument.Parse(lines[0])
            : null;
        JsonElement value = envelope?.RootElement ?? default;
        string outcome = lines.Length == 1
            ? value.GetProperty("outcome").GetString() ?? "<null>"
            : $"<envelopes:{lines.Length}>";
        JsonElement diagnostic = lines.Length == 1
            ? value.GetProperty("diagnostics").EnumerateArray().Single()
            : default;
        JsonElement recovery = lines.Length == 1
            ? diagnostic.GetProperty("recovery")
            : default;
        Require(exit != CommandExitCode.Success && lines.Length == 1 &&
                outcome == expectedOutcome && !workflowExists &&
                composer.Calls == expectedComposerCalls &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                diagnostic.GetProperty("code").GetString() ==
                    expectedDiagnosticCode &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        $"readWorkspace|{expectedReadStatus}|workspace",
                        $"writeNewArtifact|{expectedWriteStatus}|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                (!expectOutputAbsent ||
                 (!previewDirectoryExists && !previewFileExists)) &&
                (expectedRecoveryAction is null ||
                 recovery.GetProperty("action").GetString() ==
                    expectedRecoveryAction) &&
                (expectedRecoveryOption is null ||
                 recovery.GetProperty("option").GetString() ==
                    expectedRecoveryOption) &&
                (expectedRecoveryArtifactKind is null ||
                 recovery.GetProperty("artifactKind").GetString() ==
                    expectedRecoveryArtifactKind) &&
                (expectedRetryUnchangedSafe is null ||
                 recovery.GetProperty("retryUnchangedSafe").GetBoolean() ==
                    expectedRetryUnchangedSafe) &&
                (expectedComposerCalls != 1 &&
                 !expectAdmittedInputAuthority ||
                 Authority(value, "inputAdmission") == "established" &&
                 Authority(value, "sourceProviderIdentity") == "established" &&
                 Authority(value, "deterministicMaterialization") ==
                    "established" &&
                 Authority(value, "independentStaticVerification") ==
                    "established" &&
                 Authority(value, "offEnginePreview") == "required"),
            $"Preview refusal '{name}' was not contained: " +
            $"exit={exit}, outcome={outcome}, workflowExists={workflowExists}, " +
            $"previewDirectoryExists={previewDirectoryExists}, " +
            $"previewFileExists={previewFileExists}, envelope={output}.");
    }

    private static async Task RequireRunnerBoundaryCancellationAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        ParsedCommand command,
        WorkspacePath workflowOutput,
        WorkspacePath previewOutput,
        FixtureComposer composer,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [Adapter(
                root,
                policy,
                lifecycle,
                composer,
                new NpcVisualPreviewArtifactReader(root))],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(
            command,
            cancellationToken);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(lines.Length == 1,
            "Runner-boundary cancellation did not emit one envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        bool resultAbsentOrNull =
            !value.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind == JsonValueKind.Null;
        Require(exit == CommandExitCode.Cancelled &&
                value.GetProperty("outcome").GetString() == "cancelled" &&
                diagnostic.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled &&
                diagnostic.GetProperty("class").GetString() ==
                    "cancellation" &&
                recovery.GetProperty("action").GetString() ==
                    "retryUnchanged" &&
                recovery.GetProperty("constraint").GetString() ==
                    "The protocol operation was cancelled." &&
                recovery.GetProperty("retryUnchangedSafe").GetBoolean() &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                value.GetProperty("authority").GetArrayLength() == 0 &&
                value.GetProperty("nextActions").GetArrayLength() == 0 &&
                resultAbsentOrNull &&
                composer.Calls == 0 &&
                !File.Exists(workflowOutput.Value) &&
                !Directory.Exists(previewOutput.Value) &&
                !File.Exists(previewOutput.Value),
            "Pre-cancelled preview did not remain at the exact runner boundary: " +
            output);
    }

    private static ProtocolV2NpcVisualPreviewAdapter Adapter(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleTransitionService lifecycle,
        FixtureComposer composer,
        NpcVisualPreviewArtifactReader reader,
        INpcStaticBuildPackageAdmission? packageAdmission = null) => new(
        root,
        policy,
        new FaceGeomHairRegionsDocumentCodec(root),
        new PackageVerifyService(new PackageManifestReader(policy, root)),
        new PackageManifestReader(policy, root),
        new FixedComposerFactory(composer),
        reader,
        lifecycle,
        packageAdmission);

    private static string Authority(JsonElement envelope, string kind) =>
        envelope.GetProperty("authority").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == kind)
            .GetProperty("state").GetString()!;

    private static string Effect(JsonElement effect) => string.Join('|',
        effect.GetProperty("kind").GetString(),
        effect.GetProperty("status").GetString(),
        effect.GetProperty("scope").GetString());

    private static bool HasConservativeRefusalAuthority(JsonElement envelope) =>
        envelope.GetProperty("authority").EnumerateArray()
            .Select(item => string.Join(':',
                item.GetProperty("kind").GetString(),
                item.GetProperty("state").GetString()))
            .SequenceEqual([
                "inputAdmission:blocked",
                "sourceProviderIdentity:required",
                "deterministicMaterialization:required",
                "independentStaticVerification:required",
                "offEnginePreview:required",
                "humanVisualAcceptance:required",
                "gameRuntimeVerification:required",
                "promotionApproval:required"
            ], StringComparer.Ordinal);

    private static bool HasNoPreviewOutputs(JsonElement result) =>
        !result.GetProperty("composed").GetBoolean() &&
        result.GetProperty("bundlePath").ValueKind == JsonValueKind.Null &&
        result.GetProperty("hashManifestPath").ValueKind == JsonValueKind.Null &&
        result.GetProperty("contactSheetPath").ValueKind == JsonValueKind.Null &&
        result.GetProperty("views").GetArrayLength() == 0;

    private static bool IsUpperSha(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static WorkspacePath Child(
        WorkspacePath root,
        params string[] parts) =>
        new(parts.Aggregate(root.Value, Path.Combine));

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] CreatePng(int width, int height)
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(4), checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        WriteChunk(result, "IHDR", header);
        using var raw = new MemoryStream();
        byte[] row = new byte[checked((width * 4) + 1)];
        for (int index = 0; index < height; index++)
            raw.Write(row);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw.GetBuffer().AsSpan(0, checked((int)raw.Length)));
        WriteChunk(result, "IDAT", compressed.ToArray());
        WriteChunk(result, "IEND", ReadOnlySpan<byte>.Empty);
        return result.ToArray();
    }

    private static void WriteChunk(
        MemoryStream destination,
        string type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in typeBytes.Concat(data.ToArray()))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^
                    (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record PreviewFixture(
        WorkspacePath Root,
        WorkspacePath Intake,
        string IntakeSha256,
        WorkspacePath PackageManifest,
        string PackageSha256,
        WorkspacePath Workflow,
        string WorkflowSha256)
    {
        public static PreviewFixture Create(
            WorkspacePath root,
            AgentWorkflowBundleTransitionService lifecycle)
        {
            Directory.CreateDirectory(Child(root, "inputs").Value);
            Directory.CreateDirectory(Child(root, "package").Value);
            Directory.CreateDirectory(Child(root, "preview").Value);
            Directory.CreateDirectory(Child(root, "workflow").Value);
            WorkspacePath intake = WriteIntake(root, "inputs");
            string intakeSha256 = HashFile(intake.Value);
            WorkspacePath preset = Child(root, "inputs", "BuildNpc.jslot");
            File.WriteAllText(preset.Value, "{}", Encoding.UTF8);
            WorkspacePath plugin = WritePackageFile(
                root,
                Path.Combine("Data", "BuildNpc.esp"),
                "plugin");
            WorkspacePath nif = WritePackageFile(root,
                Path.Combine("Data", "meshes", "actors", "character", "FaceGenData",
                    "FaceGeom", "BuildNpc.esp", "00000800.nif"), "nif");
            WorkspacePath dds = WritePackageFile(root,
                Path.Combine("Data", "textures", "actors", "character", "FaceGenData",
                    "FaceTint", "BuildNpc.esp", "00000800.dds"), "dds");
            WorkspacePath proposal = WritePackageFile(
                root,
                Path.Combine("evidence", "npc-creation-proposal.json"),
                "{\"schemaVersion\":1,\"artifactKind\":\"npc-creation-proposal\"}");
            WorkspacePath manifest = Child(
                root, "package", "npcmanager-package.json");
            File.WriteAllBytes(manifest.Value,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    edition = "skyrimse",
                    presetFormat = "blank-npc-creation-proposal",
                    sourcePreset = "evidence/npc-creation-proposal.json",
                    sourcePresetSha256 = HashFile(proposal.Value),
                    sourcePlugin = "Skyrim.esm",
                    sourcePluginSha256 = new string('1', 64),
                    outputPlugin = "BuildNpc.esp",
                    targetFormId = "00000800",
                    artifacts = new[]
                    {
                        Row(root, "plugin", plugin),
                        Row(root, "faceGeom", nif),
                        Row(root, "faceTint", dds),
                        Row(root, "npc-creation-proposal", proposal)
                    }
                }));
            string manifestSha = HashFile(manifest.Value);
            var package = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPackageManifest,
                "application/json", manifest,
                new FileInfo(manifest.Value).Length, manifestSha,
                "npc create-from-jslot", RequestDigest,
                [intakeSha256]);
            var intakeBinding = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "npcmanager-reviewed-game-intake/2",
                intake,
                new FileInfo(intake.Value).Length,
                intakeSha256,
                "workspace preflight",
                RequestDigest,
                []);
            WorkspacePath workflow = Child(root, "workflow", "package.json");
            AgentWorkflowBundleTransition transition = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "BuildNpc", "Build NPC", "BuildNpc.esp", "00000800"),
                RequestDigest, [package, intakeBinding], workflow);
            return new PreviewFixture(
                root, intake, intakeSha256, manifest, manifestSha,
                workflow, transition.Document.Sha256);
        }

        public static WorkspacePath CreateForeignIntake(WorkspacePath root) =>
            WriteIntake(root, "foreign-inputs");

        public (WorkspacePath Path, string Sha256)
            CreatePackageInputWorkflow(
                AgentWorkflowBundleTransitionService lifecycle,
                string fileName,
                ImmutableArray<string> packageInputs)
        {
            var package = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPackageManifest,
                "application/json",
                PackageManifest,
                new FileInfo(PackageManifest.Value).Length,
                PackageSha256,
                "npc create-from-jslot",
                RequestDigest,
                packageInputs);
            var intake = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "npcmanager-reviewed-game-intake/2",
                Intake,
                new FileInfo(Intake.Value).Length,
                IntakeSha256,
                "workspace preflight",
                RequestDigest,
                []);
            WorkspacePath workflow = Child(Root, "workflow", fileName);
            AgentWorkflowBundleTransition transition = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "BuildNpc", "Build NPC", "BuildNpc.esp", "00000800"),
                RequestDigest,
                [package, intake],
                workflow);
            return (workflow, transition.Document.Sha256);
        }

        public (WorkspacePath Intake, WorkspacePath Workflow,
            string WorkflowSha256) CreateSemanticIntakeFailureWorkflow(
                AgentWorkflowBundleTransitionService lifecycle,
                string fileName)
        {
            WorkspacePath intake = Child(
                Root, "semantic-inputs", "reviewed-intake.json");
            Directory.CreateDirectory(
                Path.GetDirectoryName(intake.Value)!);
            File.WriteAllText(
                intake.Value,
                "{}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string intakeSha256 = HashFile(intake.Value);
            var package = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPackageManifest,
                "application/json",
                PackageManifest,
                new FileInfo(PackageManifest.Value).Length,
                PackageSha256,
                "npc create-from-jslot",
                RequestDigest,
                [intakeSha256]);
            var intakeBinding = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "npcmanager-reviewed-game-intake/2",
                intake,
                new FileInfo(intake.Value).Length,
                intakeSha256,
                "workspace preflight",
                RequestDigest,
                []);
            WorkspacePath workflow = Child(Root, "workflow", fileName);
            AgentWorkflowBundleTransition transition = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "BuildNpc", "Build NPC", "BuildNpc.esp", "00000800"),
                RequestDigest,
                [package, intakeBinding],
                workflow);
            return (intake, workflow, transition.Document.Sha256);
        }

        public ParsedCommand Command(
            WorkspacePath outputRoot,
            WorkspacePath workflowOutput) =>
            Parse(Options(outputRoot, workflowOutput));

        public Dictionary<string, string> Options(
            WorkspacePath outputRoot,
            WorkspacePath workflowOutput) => new(StringComparer.Ordinal)
        {
            ["intake"] = Intake.Value,
            ["plugin"] = "BuildNpc.esp",
            ["form"] = "00000800",
            ["package-manifest"] = PackageManifest.Value,
            ["expected-package-sha256"] = PackageSha256,
            ["output-root"] = outputRoot.Value,
            ["workflow-bundle"] = Workflow.Value,
            ["workflow-bundle-sha256"] = WorkflowSha256,
            ["workflow-output"] = workflowOutput.Value
        };

        public static ParsedCommand Parse(
            IReadOnlyDictionary<string, string> options)
        {
            var arguments = new List<string>
            {
                "preview", "npc", "--protocol", "2", "--json"
            };
            foreach ((string key, string value) in options)
            {
                arguments.Add("--" + key);
                arguments.Add(value);
            }
            return CommandLine.Parse(arguments);
        }

        private static WorkspacePath WritePackageFile(
            WorkspacePath root,
            string relative,
            string text)
        {
            WorkspacePath path = Child(root, "package", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
            File.WriteAllText(path.Value, text, Encoding.UTF8);
            return path;
        }

        private static object Row(
            WorkspacePath root,
            string kind,
            WorkspacePath path) => new
        {
            kind,
            relativePath = Path.GetRelativePath(
                    Child(root, "package").Value, path.Value)
                .Replace(Path.DirectorySeparatorChar, '/'),
            byteLength = new FileInfo(path.Value).Length,
            sha256 = HashFile(path.Value)
        };

        private static WorkspacePath WriteIntake(
            WorkspacePath root,
            string directory)
        {
            WorkspacePath data = Child(root, directory, "Data");
            Directory.CreateDirectory(data.Value);
            WorkspacePath plugin = Child(data, "BuildNpc.esp");
            File.WriteAllText(plugin.Value, "copied-plugin", Encoding.UTF8);
            WorkspacePath loadOrder = Child(root, directory, "load-order.txt");
            File.WriteAllText(loadOrder.Value, "BuildNpc.esp", Encoding.UTF8);
            WorkspacePath reviewedOutput = Child(
                root, $"reserved-output-{directory}");
            Sha256Hash loadHash = new(HashFile(loadOrder.Value));
            Sha256Hash asset = new(new string('2', 64));
            var plugins = ImmutableArray.Create(new PluginClosureReviewEntry(
                new PluginName("BuildNpc.esp"), 0, true, true, true, false,
                true, plugin, new Sha256Hash(HashFile(plugin.Value)), []));
            Sha256Hash fingerprint =
                ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                    GameEdition.SkyrimSpecialEdition, root, data, loadOrder,
                    reviewedOutput, loadHash, plugins, [], [], [], asset);
            WorkspacePath intake = Child(
                root, directory, "reviewed-intake.json");
            File.WriteAllBytes(intake.Value,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = "1",
                    edition = "skyrimse",
                    isAccepted = true,
                    workspaceRoot = root.Value,
                    dataRoot = data.Value,
                    loadOrderPath = loadOrder.Value,
                    outputRoot = reviewedOutput.Value,
                    loadOrderHash = loadHash.Value,
                    assetIndexFingerprint = asset.Value,
                    intakeFingerprint = fingerprint.Value,
                    plugins = new[]
                    {
                        new
                        {
                            plugin = "BuildNpc.esp", order = 0,
                            active = true, requested = true,
                            requiredMaster = false,
                            sourceHash = HashFile(plugin.Value),
                            masters = Array.Empty<string>()
                        }
                    },
                    bodySidecarCount = 0,
                    generatedPluginCount = 0,
                    generatedSidecarCount = 0,
                    assetProviderCount = 4,
                    runtimeAuthority = false,
                    diagnostics = Array.Empty<object>()
                }));
            return intake;
        }
    }

    private enum FixtureMode
    {
        Success,
        ExposureAdvisory,
        RendererUnavailable,
        MalformedBundle,
        ViewDrift,
        SourceBindingDrift,
        ConcurrentOutputCollision,
        Cancelled
    }

    private sealed class FixtureComposer(
        FixtureMode mode = FixtureMode.Success,
        WorkspacePath? workflowCollision = null) : INpcVisualPreviewComposer
    {
        public static readonly byte[] WorkflowCollisionBytes =
            "occupied after preview publication"u8.ToArray();
        public const string ForeignCollisionName = "foreign-sentinel.bin";
        public static readonly byte[] ForeignCollisionBytes =
            "foreign bytes created after preview admission"u8.ToArray();

        public int Calls { get; private set; }

        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (mode == FixtureMode.Cancelled)
            {
                Directory.CreateDirectory(request.OutputRoot.Value);
                File.WriteAllBytes(
                    Child(request.OutputRoot, ForeignCollisionName).Value,
                    ForeignCollisionBytes);
                throw new OperationCanceledException(cancellationToken);
            }
            if (mode == FixtureMode.RendererUnavailable)
                return ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                    false, null,
                    [new Diagnostic(
                        "npc-preview-renderer-unavailable",
                        DiagnosticSeverity.Error,
                        "The reviewed renderer dependency is unavailable.")]));
            if (mode == FixtureMode.ConcurrentOutputCollision)
            {
                Directory.CreateDirectory(request.OutputRoot.Value);
                File.WriteAllBytes(
                    Child(request.OutputRoot, ForeignCollisionName).Value,
                    ForeignCollisionBytes);
                return ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                    false,
                    null,
                    [new Diagnostic(
                        "npc-preview-output-became-occupied",
                        DiagnosticSeverity.Error,
                        "The preview output became occupied after admission.")]));
            }

            Directory.CreateDirectory(request.OutputRoot.Value);
            WorkspacePath status = WriteText(
                request.OutputRoot, "renderer-status.json", "{}");
            WorkspacePath contact = WritePng(
                request.OutputRoot, "contact-sheet.png");
            ImmutableArray<NpcVisualPreviewView>.Builder views =
                ImmutableArray.CreateBuilder<NpcVisualPreviewView>();
            foreach (string id in
                     NpcVisualPreviewPersistenceContract.RequiredViewIds)
            {
                WorkspacePath image = WritePng(
                    request.OutputRoot, $"{id}.png");
                WorkspacePath mask = WritePng(
                    request.OutputRoot, $"{id}-roles.png");
                views.Add(new NpcVisualPreviewView(
                    id, image, new Sha256Hash(HashFile(image.Value)),
                    mask, new Sha256Hash(HashFile(mask.Value)), 900, 900));
            }
            string packageRoot = Path.GetDirectoryName(
                request.PackageOverlay!.ManifestPath.Value)!;
            WorkspacePath packageFaceGeom = new(Path.Combine(
                packageRoot, "Data", "meshes", "actors", "character", "FaceGenData",
                "FaceGeom", request.Identity.OwnerPlugin.Value,
                $"{request.Identity.FormId.Value:X8}.nif"));
            WorkspacePath packageFaceTint = new(Path.Combine(
                packageRoot, "Data", "textures", "actors", "character", "FaceGenData",
                "FaceTint", request.Identity.OwnerPlugin.Value,
                $"{request.Identity.FormId.Value:X8}.dds"));
            WorkspacePath faceGeom = MaterializePackageAsset(
                request.OutputRoot,
                packageRoot,
                packageFaceGeom);
            WorkspacePath faceTint = MaterializePackageAsset(
                request.OutputRoot,
                packageRoot,
                packageFaceTint);
            ImmutableArray<NpcVisualAsset> assets =
            [
                Asset(
                    NpcVisualAssetRole.FaceGeom,
                    $"meshes/actors/character/FaceGenData/FaceGeom/{request.Identity.OwnerPlugin.Value}/{request.Identity.FormId.Value:X8}.nif",
                    faceGeom,
                    baked: true),
                Asset(
                    NpcVisualAssetRole.FaceTint,
                    $"textures/actors/character/FaceGenData/FaceTint/{request.Identity.OwnerPlugin.Value}/{request.Identity.FormId.Value:X8}.dds",
                    faceTint,
                    baked: false)
            ];
            var source = new NpcVisualSourceGraph(
                NpcVisualPreviewRoute.Cotr, request.Identity,
                NpcSex.Female, 50, "FixtureRace", "#101010", "#F0D0C0",
                assets, [], false, []);
            if (mode == FixtureMode.SourceBindingDrift)
            {
                NpcVisualAsset drifted = source.Assets[0] with
                {
                    AssetPath = new AssetPath(
                        "meshes/actors/character/FaceGenData/FaceGeom/BuildNpc.esp/00000801.nif")
                };
                source = source with
                {
                    Assets = source.Assets.SetItem(0, drifted)
                };
            }
            var render = new NpcVisualPreviewRenderEvidence(
                "4.5.1", "BLENDER_EEVEE_NEXT", 1, true, 1,
                ["NPC Root [Root]"], 0,
                ImmutableDictionary<string, int>.Empty.Add("Face", 1),
                ImmutableDictionary<string, long>.Empty
                    .Add("face-front:FaceGeom", 4),
                ImmutableDictionary<string, double>.Empty,
                [new NpcVisualPreviewImportedMesh(
                    NpcVisualAssetRole.FaceGeom,
                    source.Assets[0].AssetPath,
                    "FaceGeom",
                    3,
                    ["Face"],
                    [1, 0, 0, 0, 0, 1, 0, 0,
                     0, 0, 1, 0, 0, 0, 0, 1])],
                [new NpcVisualPreviewImportedMaterial(
                    "FaceGeom",
                    "Face",
                    ImmutableDictionary<string, string>.Empty,
                    [],
                    "OPAQUE",
                    false)],
                status,
                new Sha256Hash(HashFile(status.Value)));
            var visual = new NpcVisualPreviewVisualEvidence(
                1, 0.99, 478, 31, true, []);
            ImmutableArray<Diagnostic> diagnostics =
                mode == FixtureMode.ExposureAdvisory
                    ? ExposureAdvisories
                    : [];
            var payload = new NpcVisualPreviewPersistenceDocument(
                NpcVisualPreviewPersistenceContract.BundleSchema,
                NpcVisualPreviewPersistenceContract.SceneSchema,
                NpcVisualPreviewPersistenceContract.OffEngineLabel,
                false, source, views.ToImmutable(), contact,
                new Sha256Hash(HashFile(contact.Value)), render, visual,
                diagnostics);
            WorkspacePath bundlePath = Child(
                request.OutputRoot, "npc-preview-bundle.json");
            byte[] bytes = mode == FixtureMode.MalformedBundle
                ? Encoding.UTF8.GetBytes("{\"schemaVersion\":")
                : JsonSerializer.SerializeToUtf8Bytes(
                    payload, PreviewJsonOptions);
            File.WriteAllBytes(bundlePath.Value, bytes);
            WorkspacePath hashes = Child(
                request.OutputRoot, "npc-preview.hashes.sha256");
            File.WriteAllText(hashes.Value,
                string.Join("\n", Directory.EnumerateFiles(
                        request.OutputRoot.Value, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(
                        path, hashes.Value, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .Select(path =>
                        $"{HashFile(path).ToLowerInvariant()}  {Path.GetRelativePath(request.OutputRoot.Value, path).Replace('\\', '/')}")) + "\n",
                new UTF8Encoding(false));
            if (mode == FixtureMode.ViewDrift)
                File.AppendAllText(views[0].ImagePath.Value, "drift");
            if (workflowCollision is { } occupiedWorkflow)
                File.WriteAllBytes(
                    occupiedWorkflow.Value,
                    WorkflowCollisionBytes);
            return ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                true,
                new NpcVisualPreviewBundle(
                    payload.SchemaVersion, payload.SceneSchemaVersion,
                    payload.Label, false, source, payload.Views,
                    contact, payload.ContactSheetSha256,
                    bundlePath, new Sha256Hash(HashFile(bundlePath.Value)),
                    hashes, new Sha256Hash(HashFile(hashes.Value)),
                     render, visual, diagnostics),
                 diagnostics));

            static NpcVisualAsset Asset(
                NpcVisualAssetRole role,
                string relative,
                WorkspacePath path,
                bool baked) => new(
                role,
                new AssetPath(relative),
                "BuildNpc.esp",
                new Sha256Hash(HashFile(path.Value)),
                new FileInfo(path.Value).Length,
                path,
                baked,
                []);
        }

        private static WorkspacePath WritePng(
            WorkspacePath root,
            string name)
        {
            WorkspacePath path = Child(root, name);
            File.WriteAllBytes(path.Value, Png);
            return path;
        }

        private static WorkspacePath WriteText(
            WorkspacePath root,
            string name,
            string text)
        {
            WorkspacePath path = Child(root, name);
            File.WriteAllText(path.Value, text, Encoding.UTF8);
            return path;
        }

        private static WorkspacePath MaterializePackageAsset(
            WorkspacePath outputRoot,
            string packageRoot,
            WorkspacePath packagePath)
        {
            string relative = Path.GetRelativePath(
                packageRoot, packagePath.Value);
            WorkspacePath materialized = Child(
                outputRoot,
                "assets",
                relative);
            Directory.CreateDirectory(
                Path.GetDirectoryName(materialized.Value)!);
            File.Copy(packagePath.Value, materialized.Value);
            return materialized;
        }
    }

    private sealed class FixedComposerFactory(
        INpcVisualPreviewComposer composer) :
        IPreviewServiceFactory<INpcVisualPreviewComposer>
    {
        public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(composer));
        }
    }

    private sealed class CancellingPackageAdmission :
        INpcStaticBuildPackageAdmission
    {
        public int PreviewCalls { get; private set; }

        public ValueTask<NpcStaticBuildPackageLease> AdmitAsync(
            PackageVerificationArtifact verification,
            RaceMenuJslotNpcBuildCommandBinding binding,
            RaceMenuNpcExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The preview cancellation fixture must not admit a build package.");

        public ValueTask<NpcStaticBuildPackageLease> AdmitPreviewAsync(
            PackageVerificationArtifact verification,
            WorkflowArtifactBinding packageBinding,
            WorkflowNpcIdentity npc,
            CancellationToken cancellationToken)
        {
            PreviewCalls++;
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask<NpcStaticBuildPackageLease> AdmitFinishAsync(
            PackageVerificationArtifact verification,
            SkyrimNpcFinishCoreRequestDocument request,
            WorkflowNpcIdentity npc,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class TerminalArtifactProbe(
        WorkspacePath semanticPath,
        WorkspacePath workflowPath)
    {
        private int observations;

        public void Probe(string stage)
        {
            Require(File.Exists(semanticPath.Value),
                $"{stage} could not find the primary semantic artifact.");
            Require(File.Exists(workflowPath.Value),
                $"{stage} could not find the workflow output.");
            RequireWriteOpenRefused(
                stage, "primary semantic artifact", semanticPath.Value);
            RequireWriteOpenRefused(
                stage, "workflow output", workflowPath.Value);
            observations++;
        }

        public void RequireReleasedAndUnchanged(string workflowSha256)
        {
            Require(observations == 2,
                "The terminal artifact lifetime was not probed at both boundaries.");
            byte[] semanticBytes = File.ReadAllBytes(semanticPath.Value);
            byte[] workflowBytes = File.ReadAllBytes(workflowPath.Value);
            string semanticSha256 = Convert.ToHexString(
                SHA256.HashData(semanticBytes));
            string retainedWorkflowSha256 = Convert.ToHexString(
                SHA256.HashData(workflowBytes));
            Require(semanticBytes.AsSpan().SequenceEqual(Png) &&
                    semanticSha256 == Convert.ToHexString(
                        SHA256.HashData(Png)) &&
                    retainedWorkflowSha256 == workflowSha256,
                "Terminal preview artifacts did not retain their exact published bytes and hashes.");
            RequireWriteOpenSucceeds(semanticPath.Value);
            RequireWriteOpenSucceeds(workflowPath.Value);
            Require(File.ReadAllBytes(semanticPath.Value).AsSpan()
                        .SequenceEqual(semanticBytes) &&
                    File.ReadAllBytes(workflowPath.Value).AsSpan()
                        .SequenceEqual(workflowBytes) &&
                    HashFile(semanticPath.Value) == semanticSha256 &&
                    HashFile(workflowPath.Value) == retainedWorkflowSha256,
                "Terminal preview artifacts changed while proving their leases were released.");
        }

        private static void RequireWriteOpenRefused(
            string stage,
            string role,
            string path)
        {
            bool refused = false;
            try
            {
                using FileStream _ = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (IOException)
            {
                refused = true;
            }
            catch (UnauthorizedAccessException)
            {
                refused = true;
            }

            Require(refused, $"{stage} could write-open the retained {role}.");
        }

        private static void RequireWriteOpenSucceeds(string path)
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
    }

    private sealed class CallbackJournal(Action callback) :
        ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public int Probes { get; private set; }

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Probes++;
            callback();
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private sealed class WriteLineCallbackWriter(
        TextWriter inner,
        Action callback) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public int Probes { get; private set; }

        public override void WriteLine(string? value)
        {
            Probes++;
            callback();
            inner.WriteLine(value);
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
}
