using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2NpcCreateBuildTests
{
    private const string NpcCreateRequestSchemaIdentifier =
        "npc.create-from-jslot.request.v1";

    private const string RequestDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string[] PreviewOptionNames =
    [
        "intake", "plugin", "form", "package-manifest",
        "expected-package-sha256", "output-root"
    ];
    private static readonly string[] AuthenticRequestJsonFiles =
    [
        "preset-bundle.json", "record-authority.json",
        "runtime-routes.json", "standalone-assets.json"
    ];

    public static async Task RunAsync()
    {
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            $"protocol-v2-npc-create-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var preflightDocuments = new NpcBuildPreflightDocumentCodec(root);
            var lifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            BuildFixture fixture = await BuildFixture.CreateAsync(
                root,
                preflightDocuments,
                lifecycle);

            AssertPreflightWireUnchanged();
            await AssertSuccessfulReviewedBuildAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertPostPublicationWorkflowFailureAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertBuildRefusalsAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertProtectedWorkflowOutputRefusalsAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertReviewedIntakeOutputRootReuseRefusalAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertStageCancellationsAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            await AssertPostBuildRefusalsAsync(
                root,
                policy,
                preflightDocuments,
                lifecycle,
                fixture);
            AssertRegistryAndSchemaContract();
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    internal static async Task RunExternalPublicationPathAsync()
    {
        await RunExternalRefusalPathAsync();
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            "actorwright-protocol-v2-external-publication-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var preflightDocuments = new NpcBuildPreflightDocumentCodec(root);
            var lifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            BuildFixture fixture = await BuildFixture.CreateAsync(
                root,
                preflightDocuments,
                lifecycle);
            var standaloneReader = new Schema8StandaloneReader();
            var packageManifestReader = new PackageManifestReader(policy, root);
            var innerPackageVerifier = new PackageVerifyService(
                packageManifestReader);
            var packageVerifier = new CountingPackageVerifyService(
                innerPackageVerifier);
            var typedService = new TypedFixtureBuildService(
                fixture.OutputRoot,
                innerPackageVerifier,
                fixture.PresetSha256);
            var typedBridge = new RaceMenuJslotNpcBuildCommandExecutionBridge(
                typedService);
            var ordinaryExecutor = new FixtureBuildService(fixture.OutputRoot);
            var adapter = new ProtocolV2NpcCreateFromJslotAdapter(
                root,
                new FaceGeomHairRegionsDocumentCodec(root),
                RequestLoader(root),
                new UnusedPreflightService(),
                preflightDocuments,
                ordinaryExecutor,
                packageVerifier,
                packageManifestReader,
                lifecycle,
                typedBuildBridge: typedBridge,
                standaloneAuthorityReader: standaloneReader);
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [adapter],
                AgentCommandRegistry.All);

            CommandExitCode exit = await runner.RunAsync(
                fixture.Command(fixture.WorkflowOutput),
                CancellationToken.None);
            Require(exit == CommandExitCode.Success,
                "The typed external Protocol path did not succeed.");
            using JsonDocument envelope = JsonDocument.Parse(output.ToString());
            JsonElement result = envelope.RootElement.GetProperty("result");
            JsonElement external = result.GetProperty(
                "externalInstallPrepublication");
            Require(envelope.RootElement.GetProperty("outcome").GetString() ==
                    "succeeded" &&
                external.GetProperty("verification")
                    .GetProperty("currentInstallDependencyState")
                    .GetString() == "verified" &&
                external.GetProperty("verification")
                    .GetProperty("providerObservations")[0]
                    .GetProperty("providerPlugin").GetString() ==
                    "HairPack.esp" &&
                Directory.Exists(fixture.OutputRoot.Value) &&
                File.Exists(fixture.WorkflowOutput.Value),
                "The successful Protocol path did not publish its package/workflow result.");
            Require(standaloneReader.Calls == 1 &&
                    typedService.Calls == 1 &&
                    ordinaryExecutor.Calls == 0 &&
                    packageVerifier.Calls == 1,
                "The Protocol path duplicated precheck/typed execution or used stdout execution.");
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static async Task RunExternalRefusalPathAsync()
    {
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            "actorwright-protocol-v2-external-refusal-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var preflightDocuments = new NpcBuildPreflightDocumentCodec(root);
            var lifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            BuildFixture fixture = await BuildFixture.CreateAsync(
                root,
                preflightDocuments,
                lifecycle);
            var standaloneReader = new Schema8StandaloneReader();
            var packageManifestReader = new PackageManifestReader(policy, root);
            var packageVerifier = new CountingPackageVerifyService(
                new PackageVerifyService(packageManifestReader));
            var typedService = new RefusingTypedFixtureBuildService();
            var ordinaryExecutor = new FixtureBuildService(fixture.OutputRoot);
            var adapter = new ProtocolV2NpcCreateFromJslotAdapter(
                root,
                new FaceGeomHairRegionsDocumentCodec(root),
                RequestLoader(root),
                new UnusedPreflightService(),
                preflightDocuments,
                ordinaryExecutor,
                packageVerifier,
                packageManifestReader,
                lifecycle,
                typedBuildBridge:
                    new RaceMenuJslotNpcBuildCommandExecutionBridge(
                        typedService),
                standaloneAuthorityReader: standaloneReader);
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [adapter],
                AgentCommandRegistry.All);

            CommandExitCode exit = await runner.RunAsync(
                fixture.Command(fixture.WorkflowOutput),
                CancellationToken.None);
            Require(exit != CommandExitCode.Success &&
                    !Directory.Exists(fixture.OutputRoot.Value) &&
                    !File.Exists(fixture.WorkflowOutput.Value) &&
                    packageVerifier.Calls == 0 &&
                    typedService.Calls == 1 &&
                    ordinaryExecutor.Calls == 0,
                "A missing typed prepublication artifact reached publication or independent verification.");
            using JsonDocument envelope = JsonDocument.Parse(output.ToString());
            AssertProviderRefusalAuthority(envelope.RootElement);
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static void AssertPreflightWireUnchanged()
    {
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("npc create-from-jslot");
        Require(contract.ResultSchemaIds.Contains(
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                StringComparer.Ordinal),
            "The established preflight result schema was removed.");
        Require(contract.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.HumanVisualAcceptance).State ==
                    AgentAuthorityState.NotApplicable &&
                contract.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.PromotionApproval).State ==
                    AgentAuthorityState.NotApplicable,
            "The established preflight visual/promotion authority changed.");
        JsonElement export = ProtocolV2SchemaService.RenderInline(
            "npc create-from-jslot");
        JsonElement preflight = export.GetProperty("resultSchemas")
            .EnumerateArray().Single(item => string.Equals(
                item.GetProperty("schemaIdentifier").GetString(),
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                StringComparison.Ordinal));
        Require(!preflight.GetProperty("jsonSchema")
                .GetProperty("additionalProperties").GetBoolean(),
            "The established preflight schema is no longer closed.");
    }

    private static async Task AssertSuccessfulReviewedBuildAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        var build = new FixtureBuildService(fixture.OutputRoot, realNpc: true);
        var terminalProbe = new TerminalLockProbe(
            Child(fixture.OutputRoot, "Data", "BuildNpc.esp"),
            fixture.WorkflowOutput);
        var journal = new CapturingJournal(
            () => terminalProbe.Observe("journal"));
        using var sink = new StringWriter();
        using var output = new LockProbeTextWriter(
            sink,
            () => terminalProbe.Observe("writer"));
        var adapter = new ProtocolV2NpcCreateFromJslotAdapter(
            root,
            new FaceGeomHairRegionsDocumentCodec(root),
            RequestLoader(root),
            new UnusedPreflightService(),
            preflightDocuments,
            build,
            new PackageVerifyService(new PackageManifestReader(policy, root)),
            new PackageManifestReader(policy, root),
            lifecycle,
                standaloneAuthorityReader: OrdinaryStandaloneReader.Instance);
        var runner = new ProtocolV2Runner(
            output,
            _ => journal,
            [adapter],
            AgentCommandRegistry.All);

        ReviewedGameIntakeDocumentAuthority reviewedIntake =
            await new FaceGeomHairRegionsDocumentCodec(root)
                .LoadReviewedIntakeAsync(
                    fixture.IntakePath,
                    CancellationToken.None);
        Require(reviewedIntake.Value.Plugins
                    .Select(item => item.Plugin.Value)
                    .SequenceEqual([
                        "Skyrim.esm",
                        "Probe.esp",
                        "ActorwrightBlankNpcProvider.esp"
                    ], StringComparer.Ordinal) &&
                reviewedIntake.Value.Plugins.Select(item => item.Order)
                    .SequenceEqual([0, 2, 5]),
            "The successful build fixture is not a broader canonical intake with gapped global order.");

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(fixture.WorkflowOutput),
            CancellationToken.None);
        string[] lines = sink.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit == CommandExitCode.Success && lines.Length == 1,
            "Reviewed build did not emit exactly one successful envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement rootElement = envelope.RootElement;
        Require(rootElement.GetProperty("outcome").GetString() == "succeeded",
            "Reviewed build did not succeed.");
        JsonElement result = rootElement.GetProperty("result");
        Require(result.TryGetProperty("inheritedDefaults", out var defaults) && defaults.GetArrayLength() == 9 &&
                defaults.EnumerateArray().Any(item => item.GetString() == "aidt"),
            "V2 create omitted the authenticated fixture comparison on its real copied NPC output.");
        Require(result.GetProperty("completed").GetBoolean() &&
                result.GetProperty("status").GetString() ==
                    "staticPassRuntimeRequired",
            "Reviewed build did not emit the static-build result shape.");
        Require(rootElement.GetProperty("diagnostics").GetArrayLength() == 0,
            "Reviewed build emitted unexpected diagnostics.");
        Require(rootElement.GetProperty("effects").EnumerateArray()
                .Select(Effect)
                .SequenceEqual([
                    "readWorkspace|completed|workspace",
                    "writeNewArtifact|completed|k-local-output",
                    "appendLocalOperationJournal|attempted|workspace-local-journal",
                    "appendLocalOperationJournal|completed|workspace-local-journal"
                ], StringComparer.Ordinal),
            "Reviewed build effects changed.");

        JsonElement[] artifacts = rootElement.GetProperty("artifacts")
            .EnumerateArray().ToArray();
        JsonElement package = artifacts.Single(item =>
            item.GetProperty("kind").GetString() ==
                WorkflowArtifactKinds.NpcPackageManifest);
        JsonElement workflow = artifacts.Single(item =>
            item.GetProperty("kind").GetString() == "workflow-bundle");
        Require(artifacts.Length == 2 &&
                package.GetProperty("state").GetString() ==
                    "independentlyVerified" &&
                IsUpperSha256(package.GetProperty("sha256").GetString()) &&
                IsUpperSha256(workflow.GetProperty("sha256").GetString()),
            "Reviewed build did not bind the exact package and workflow.");
        Require(rootElement.GetProperty("authority").GetArrayLength() == 8,
            "Reviewed build omitted authority domains.");
        Require(AuthorityState("humanVisualAcceptance") == "notApplicable" &&
                AuthorityState("gameRuntimeVerification") == "required" &&
                AuthorityState("promotionApproval") == "notApplicable",
            "Reviewed build changed visual/runtime/promotion authority.");

        JsonElement next = rootElement.GetProperty("nextActions")
            .EnumerateArray().Single();
        Require(next.GetProperty("command").GetString() == "preview npc" &&
                next.GetProperty("missingPrerequisites").EnumerateArray()
                    .Select(item => item.GetString())
                    .SequenceEqual(["--output-root"],
                        StringComparer.Ordinal) &&
                next.GetProperty("requiredBindings").EnumerateArray()
                    .Any(item =>
                        item.GetProperty("option").GetString() == "--intake" &&
                        item.GetProperty("value").GetString() ==
                            fixture.IntakePath.Value &&
                        item.GetProperty("artifactSha256").GetString() ==
                            fixture.IntakeSha256) &&
                !next.GetProperty("requiresHumanAction").GetBoolean(),
            "Reviewed build did not yield the exact resumable preview action.");

        string workflowSha = workflow.GetProperty("sha256").GetString()!;
        AgentWorkflowBundleDocument reopened = new AgentWorkflowBundleCodec(
                policy,
                root)
            .Load(fixture.WorkflowOutput, workflowSha);
        Require(reopened.Bundle.Artifacts.Select(item => item.Kind)
                .SequenceEqual([
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake
                ], StringComparer.Ordinal),
            "Persisted build workflow did not contain the exact ordered intake/package signature.");
        WorkflowArtifactBinding binding = reopened.Bundle.Artifacts.Single(
            item => item.Kind == WorkflowArtifactKinds.NpcPackageManifest);
        WorkflowArtifactBinding intake = reopened.Bundle.Artifacts.Single(
            item => item.Kind == WorkflowArtifactKinds.ReviewedWorkspaceIntake);
        Require(intake.Path == fixture.IntakePath &&
                intake.Sha256 == fixture.IntakeSha256 &&
                binding.InputArtifactHashes.Contains(
                    fixture.IntakeSha256,
                    StringComparer.Ordinal) &&
                reopened.Bundle.Npc.Plugin == "BuildNpc.esp" &&
                reopened.Bundle.Npc.LocalFormId == "00000800",
            "Persisted build workflow did not retain intake/package/NPC identity.");
        Require(reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.InputAdmission).State ==
                    AgentAuthorityState.Established &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.SourceProviderIdentity).State ==
                    AgentAuthorityState.Established &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.DeterministicMaterialization).State ==
                    AgentAuthorityState.Established &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.IndependentStaticVerification).State ==
                    AgentAuthorityState.Established &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.HumanVisualAcceptance).State ==
                    AgentAuthorityState.Required &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.GameRuntimeVerification).State ==
                    AgentAuthorityState.Required &&
                reopened.Bundle.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.PromotionApproval).State ==
                    AgentAuthorityState.Required,
            "Persisted build workflow overclaimed or omitted lifecycle authority.");
        PackageVerifyResult verified = await new PackageVerifyService(
            new PackageManifestReader(policy, root)).VerifyAsync(
            new PackageVerifyRequest(binding.Path),
            CancellationToken.None);
        Require(verified.Verified &&
                verified.Artifact is { RuntimeProof: false },
            "Persisted build package failed independent verification.");
        Require(build.Calls == 1 && journal.Records.Count == 1,
            "Reviewed build dispatch or journal count changed.");
        string semanticSha = verified.Artifact!.Files.Single(item =>
                string.Equals(item.Kind, "plugin", StringComparison.Ordinal) &&
                string.Equals(
                    item.RelativePath.Value,
                    "Data/BuildNpc.esp",
                    StringComparison.Ordinal))
            .ActualSha256.Value.ToUpperInvariant();
        terminalProbe.RequireReleased(semanticSha, workflowSha);

        string? AuthorityState(string kind) => rootElement
            .GetProperty("authority")
            .EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == kind)
            .GetProperty("state")
            .GetString();
    }

    private static async Task AssertPostPublicationWorkflowFailureAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        WorkspacePath workflowOutput = Child(
            root, "workflow", "package-published-workflow-collision.json");
        var build = new FixtureBuildService(
            fixture.OutputRoot,
            FixtureBuildMode.Complete,
            workflowOutput);
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [new ProtocolV2NpcCreateFromJslotAdapter(
                root,
                new FaceGeomHairRegionsDocumentCodec(root),
                RequestLoader(root),
                new UnusedPreflightService(),
                preflightDocuments,
                build,
                new PackageVerifyService(
                    new PackageManifestReader(policy, root)),
                new PackageManifestReader(policy, root),
                lifecycle,
                standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(workflowOutput),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(exit != CommandExitCode.Success && lines.Length == 1,
            "Post-publication workflow collision did not emit one refusal envelope.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement[] artifacts = value.GetProperty("artifacts")
            .EnumerateArray().ToArray();
        JsonElement package = artifacts.Single(item =>
            item.GetProperty("kind").GetString() ==
                WorkflowArtifactKinds.NpcPackageManifest);
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        string packagePath = package.GetProperty("path").GetString()!;
        Require(artifacts.Length == 1 &&
                !artifacts.Any(item =>
                    item.GetProperty("kind").GetString() ==
                        "workflow-bundle") &&
                File.Exists(packagePath) &&
                package.GetProperty("sha256").GetString() ==
                    HashFile(new WorkspacePath(packagePath)) &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|completed|k-local-output",
                        "writeNewArtifact|failed|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal),
            "Published package/workflow split commit was not projected truthfully.");
        byte[] occupiedBytes = File.ReadAllBytes(workflowOutput.Value);
        Require(occupiedBytes.AsSpan().SequenceEqual(
                    FixtureBuildService.WorkflowCollisionBytes) &&
                new FileInfo(workflowOutput.Value).Length ==
                    FixtureBuildService.WorkflowCollisionBytes.LongLength &&
                HashFile(workflowOutput) == Convert.ToHexString(
                    SHA256.HashData(
                        FixtureBuildService.WorkflowCollisionBytes)) &&
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
                    "established",
            "Published package workflow collision changed occupied bytes, diagnostic recovery, or stage authority.");
        PackageVerifyResult verified = await new PackageVerifyService(
            new PackageManifestReader(policy, root)).VerifyAsync(
            new PackageVerifyRequest(new WorkspacePath(packagePath)),
            CancellationToken.None);
        Require(verified.Verified &&
                verified.Artifact is { RuntimeProof: false } &&
                build.Calls == 1,
            "Published package did not remain independently verifiable after workflow failure.");

        static string Authority(JsonElement envelope, string kind) =>
            envelope.GetProperty("authority").EnumerateArray()
                .Single(item => item.GetProperty("kind").GetString() == kind)
                .GetProperty("state").GetString()!;
    }

    private static async Task AssertBuildRefusalsAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        ParsedCommand wrongWinningOrder =
            await CreateWrongWinningOrderCommandAsync(
                root,
                preflightDocuments,
                lifecycle,
                fixture);
        (string Name, Func<ParsedCommand> Command,
            string? RecoveryAction, string? RecoveryOption,
            string? RecoveryArtifactKind)[] cases =
        [
            ("conflicting preflight modes", () => fixture.Command(
                Child(root, "workflow", "conflict.json"),
                "--preflight-output", Child(root, "evidence", "other.json").Value),
                null, null, null),
            ("missing reviewed hash", () => fixture.CommandWithout(
                Child(root, "workflow", "missing-pair.json"),
                "reviewed-preflight-sha256"), null, null, null),
            ("stale reviewed hash", () => fixture.Command(
                Child(root, "workflow", "stale-preflight.json"),
                "--reviewed-preflight-sha256", new string('F', 64)),
                null, null, null),
            ("stale workflow hash", () => fixture.Command(
                Child(root, "workflow", "stale-workflow.json"),
                "--workflow-bundle-sha256", new string('E', 64)),
                "correctInput", "workflow-bundle-sha256", "workflow-bundle"),
            ("reviewed intake DataRoot mismatch", () => fixture.Command(
                Child(root, "workflow", "intake-data-root-refused.json"),
                "--workflow-bundle",
                fixture.MismatchedIntakeWorkflowInput.Value,
                "--workflow-bundle-sha256",
                fixture.MismatchedIntakeWorkflowInputSha256),
                "correctInput", "data-root",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake),
            ("reversed reviewed intake plugin subset", () => fixture.Command(
                Child(root, "workflow", "intake-order-refused.json"),
                "--plugins",
                "ActorwrightBlankNpcProvider.esp,Skyrim.esm"),
                "correctInput", "plugins",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake),
            ("unknown reviewed intake plugin subset", () => fixture.Command(
                Child(root, "workflow", "intake-unknown-refused.json"),
                "--plugins",
                "Skyrim.esm,Unknown.esp"),
                "correctInput", "plugins",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake),
            ("reviewed preflight winning order mismatch",
                () => wrongWinningOrder,
                "correctInput", "reviewed-preflight",
                WorkflowArtifactKinds.NpcBuildPreflight),
            ("request/workflow identity mismatch", () => fixture.Command(
                Child(root, "workflow", "identity-refused.json"),
                "--workflow-bundle", fixture.MismatchedWorkflowInput.Value,
                "--workflow-bundle-sha256",
                fixture.MismatchedWorkflowInputSha256),
                "correctInput", "request",
                WorkflowArtifactKinds.NpcBuildPreflight)
        ];

        foreach ((string name, Func<ParsedCommand> command,
                     string? recoveryAction, string? recoveryOption,
                     string? recoveryArtifactKind) in cases)
        {
            using var output = new StringWriter();
            var build = new FixtureBuildService(fixture.OutputRoot);
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [new ProtocolV2NpcCreateFromJslotAdapter(
                    root,
                    new FaceGeomHairRegionsDocumentCodec(root),
                    RequestLoader(root),
                    new UnusedPreflightService(),
                    preflightDocuments,
                    build,
                    new PackageVerifyService(
                        new PackageManifestReader(policy, root)),
                    new PackageManifestReader(policy, root),
                    lifecycle,
                    standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
                AgentCommandRegistry.All);
            CommandExitCode exit = await runner.RunAsync(
                command(),
                CancellationToken.None);
            string[] lines = output.ToString().Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 1,
                $"Reviewed build emitted an invalid envelope count for {name}.");
            using JsonDocument envelope = JsonDocument.Parse(lines[0]);
            JsonElement value = envelope.RootElement;
            JsonElement[] diagnostics = value.GetProperty("diagnostics")
                .EnumerateArray().ToArray();
            JsonElement diagnostic = diagnostics.Length == 1
                ? diagnostics[0]
                : default;
            JsonElement recovery = diagnostics.Length == 1 &&
                diagnostic.TryGetProperty("recovery", out JsonElement exactRecovery)
                    ? exactRecovery
                    : default;
            Require(exit != CommandExitCode.Success &&
                    value.GetProperty("outcome").GetString() !=
                        "succeeded" && build.Calls == 0 &&
                    (recoveryAction is null ||
                     diagnostics.Length == 1 &&
                     recovery.GetProperty("action").GetString() ==
                        recoveryAction) &&
                    (recoveryOption is null ||
                     diagnostics.Length == 1 &&
                     recovery.GetProperty("option").GetString() ==
                        recoveryOption) &&
                    (recoveryArtifactKind is null ||
                     diagnostics.Length == 1 &&
                     recovery.GetProperty("artifactKind").GetString() ==
                        recoveryArtifactKind),
                $"Reviewed build admitted {name}.");
            if (name is "reviewed intake DataRoot mismatch" or
                "reversed reviewed intake plugin subset" or
                "unknown reviewed intake plugin subset" or
                "reviewed preflight winning order mismatch")
                Require(value.GetProperty("artifacts").GetArrayLength() == 0 &&
                        diagnostic.GetProperty("code").GetString() ==
                            ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed &&
                        value.GetProperty("effects").EnumerateArray()
                            .Select(Effect).SequenceEqual([
                                "readWorkspace|completed|workspace",
                                "writeNewArtifact|refused|k-local-output",
                                "appendLocalOperationJournal|attempted|workspace-local-journal",
                                "appendLocalOperationJournal|completed|workspace-local-journal"
                            ], StringComparer.Ordinal),
                    $"Reviewed intake mismatch '{name}' did not refuse before build with truthful effects.");
        }
    }

    private static async Task<ParsedCommand>
        CreateWrongWinningOrderCommandAsync(
            WorkspacePath root,
            NpcBuildPreflightDocumentCodec preflightDocuments,
            AgentWorkflowBundleTransitionService lifecycle,
            BuildFixture fixture)
    {
        NpcBuildPreflightDocument original =
            await preflightDocuments.ReadExactAsync(
                fixture.PreflightPath,
                fixture.PreflightSha256,
                CancellationToken.None);
        NpcBuildPreflightArtifact wrongArtifact = original.Value with
        {
            WinningPluginOrder =
            ["ActorwrightBlankNpcProvider.esp", "Skyrim.esm"]
        };
        WorkspacePath wrongPath = Child(
            root,
            "evidence",
            "npc-preflight-wrong-winning-order.json");
        NpcBuildPreflightDocument wrongDocument =
            await preflightDocuments.WriteNewAsync(
                preflightDocuments.Encode(wrongArtifact),
                wrongPath,
                CancellationToken.None);

        AgentWorkflowBundleTransition source =
            lifecycle.LoadForCommand(
                fixture.WorkflowInput,
                fixture.WorkflowInputSha256,
                "npc create-from-jslot");
        WorkflowArtifactBinding wrongBinding =
            source.Document.Bundle.Artifacts.Single(item =>
                item.Kind == WorkflowArtifactKinds.NpcBuildPreflight) with
            {
                Path = wrongPath,
                Size = wrongDocument.Utf8Json.Length,
                Sha256 = wrongDocument.Sha256.Value.ToUpperInvariant()
            };
        ImmutableArray<WorkflowArtifactBinding> artifacts =
            source.Document.Bundle.Artifacts.Select(item =>
                    item.Kind == WorkflowArtifactKinds.NpcBuildPreflight
                        ? wrongBinding
                        : item)
                .ToImmutableArray();
        WorkspacePath wrongWorkflowPath = Child(
            root,
            "workflow",
            "preflight-wrong-winning-order.json");
        AgentWorkflowBundleTransition wrongWorkflow =
            lifecycle.WriteInitial(
                source.Document.Bundle.Npc,
                source.Document.Bundle.RequestDigest,
                artifacts,
                wrongWorkflowPath);

        return fixture.Command(
            Child(root, "workflow", "winning-order-refused.json"),
            "--reviewed-preflight", wrongPath.Value,
            "--reviewed-preflight-sha256",
            wrongDocument.Sha256.Value.ToUpperInvariant(),
            "--workflow-bundle", wrongWorkflowPath.Value,
            "--workflow-bundle-sha256",
            wrongWorkflow.Document.Sha256);
    }

    private static async Task AssertProtectedWorkflowOutputRefusalsAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        WorkspacePath enclosingWorkflow = Child(
            root,
            "workflow",
            "encloses-package-root.json");
        (string Name, WorkspacePath WorkflowOutput,
            IRaceMenuNpcExecutionRequestFileLoader Loader)[] cases =
        [
            (
                "package root nested beneath workflow output",
                enclosingWorkflow,
                new OutputRootRequestLoader(
                    RequestLoader(root),
                    Child(enclosingWorkflow, "package"))
            ),
            (
                "workflow output nested beneath package root",
                Child(fixture.OutputRoot, "unsafe-workflow.json"),
                RequestLoader(root)
            ),
            (
                "workflow output nested beneath admitted DataRoot",
                Child(fixture.DataRoot, "unsafe-workflow.json"),
                RequestLoader(root)
            )
        ];

        foreach ((string name, WorkspacePath workflowOutput,
                     IRaceMenuNpcExecutionRequestFileLoader loader) in cases)
        {
            var build = new FixtureBuildService(fixture.OutputRoot);
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [new ProtocolV2NpcCreateFromJslotAdapter(
                    root,
                    new FaceGeomHairRegionsDocumentCodec(root),
                    loader,
                    new UnusedPreflightService(),
                    preflightDocuments,
                    build,
                    new PackageVerifyService(
                        new PackageManifestReader(policy, root)),
                    new PackageManifestReader(policy, root),
                    lifecycle,
                    standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
                AgentCommandRegistry.All);

            CommandExitCode exit = await runner.RunAsync(
                fixture.Command(workflowOutput),
                CancellationToken.None);
            string[] lines = output.ToString().Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 1,
                $"Protected build output case '{name}' emitted an invalid envelope count.");
            using JsonDocument envelope = JsonDocument.Parse(lines[0]);
            JsonElement value = envelope.RootElement;
            JsonElement diagnostic = value.GetProperty("diagnostics")
                .EnumerateArray().Single();
            JsonElement recovery = diagnostic.GetProperty("recovery");
            Require(exit == CommandExitCode.SecurityRefusal &&
                    value.GetProperty("outcome").GetString() == "refused" &&
                    diagnostic.GetProperty("code").GetString() ==
                        ProtocolV2DiagnosticCodes.ProtectedRootRefused &&
                    diagnostic.GetProperty("class").GetString() == "security" &&
                    recovery.GetProperty("action").GetString() ==
                        "chooseFreshOutput" &&
                    recovery.GetProperty("option").GetString() ==
                        "workflow-output" &&
                    recovery.GetProperty("artifactKind").GetString() ==
                        "workflow-bundle" &&
                    value.GetProperty("artifacts").GetArrayLength() == 0 &&
                    value.GetProperty("effects").EnumerateArray()
                        .Select(Effect).SequenceEqual([
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|refused|k-local-output",
                            "appendLocalOperationJournal|attempted|workspace-local-journal",
                            "appendLocalOperationJournal|completed|workspace-local-journal"
                        ], StringComparer.Ordinal) &&
                    AuthorityState(value, "inputAdmission") == "established" &&
                    AuthorityState(value, "sourceProviderIdentity") ==
                        "established" &&
                    AuthorityState(value, "deterministicMaterialization") ==
                        "required" &&
                    AuthorityState(value, "independentStaticVerification") ==
                        "required" &&
                    build.Calls == 0 &&
                    !File.Exists(workflowOutput.Value),
                    $"Protected build output case '{name}' was not refused before executor dispatch: {output}.");
        }
    }

    private static async Task AssertReviewedIntakeOutputRootReuseRefusalAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        var build = new FixtureBuildService(fixture.OutputRoot);
        WorkspacePath workflowOutput = Child(
            root, "workflow", "reserved-intake-output-reuse.json");
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => new CapturingJournal(),
            [new ProtocolV2NpcCreateFromJslotAdapter(
                root,
                new FaceGeomHairRegionsDocumentCodec(root),
                new OutputRootRequestLoader(
                    RequestLoader(root),
                    Child(root, "reserved-output")),
                new UnusedPreflightService(),
                preflightDocuments,
                build,
                new PackageVerifyService(
                    new PackageManifestReader(policy, root)),
                new PackageManifestReader(policy, root),
                lifecycle,
                standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
            AgentCommandRegistry.All);

        CommandExitCode exit = await runner.RunAsync(
            fixture.Command(workflowOutput), CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Require(lines.Length == 1,
            "Same-root reviewed-intake reuse emitted an invalid envelope count.");
        using JsonDocument envelope = JsonDocument.Parse(lines[0]);
        JsonElement value = envelope.RootElement;
        JsonElement diagnostic = value.GetProperty("diagnostics")
            .EnumerateArray().Single();
        JsonElement recovery = diagnostic.GetProperty("recovery");
        Require(exit == CommandExitCode.SecurityRefusal &&
                value.GetProperty("outcome").GetString() == "refused" &&
                diagnostic.GetProperty("code").GetString() ==
                    "reviewed-intake-output-reuse" &&
                diagnostic.GetProperty("class").GetString() == "security" &&
                recovery.GetProperty("action").GetString() ==
                    "chooseFreshOutput" &&
                recovery.GetProperty("option").GetString() == "output-root" &&
                recovery.GetProperty("artifactKind").GetString() ==
                    WorkflowArtifactKinds.NpcBuildPreflight &&
                value.GetProperty("artifacts").GetArrayLength() == 0 &&
                value.GetProperty("effects").EnumerateArray()
                    .Select(Effect).SequenceEqual([
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output",
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ], StringComparer.Ordinal) &&
                AuthorityState(value, "inputAdmission") == "established" &&
                AuthorityState(value, "sourceProviderIdentity") ==
                    "established" &&
                AuthorityState(value, "deterministicMaterialization") ==
                    "required" &&
                AuthorityState(value, "independentStaticVerification") ==
                    "required" &&
                build.Calls == 0 &&
                !File.Exists(workflowOutput.Value),
            "Same-root reviewed-intake/package reuse was not refused before executor dispatch: " +
            output);
    }

    private static async Task AssertStageCancellationsAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        var packageReader = new PackageManifestReader(policy, root);
        var verifier = new PackageVerifyService(packageReader);
        await RequireCancellationAsync(
            "reviewed preflight admission cancellation",
            new CancellingPreflightDocumentCodec(preflightDocuments),
            new FixtureBuildService(fixture.OutputRoot),
            verifier,
            packageAdmission: null,
            expectedBuildCalls: 0,
            expectedWriteStatus: "refused",
            expectedRecoveryAction: "retryUnchanged",
            expectedRecoveryOption: "reviewed-preflight",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcBuildPreflight,
            retryUnchangedSafe: true,
            expectPostAdmissionAuthority: false);
        await RequireCancellationAsync(
            "executor thrown cancellation",
            preflightDocuments,
            new FixtureBuildService(
                fixture.OutputRoot,
                FixtureBuildMode.ThrowsCancellation),
            verifier,
            packageAdmission: null,
            expectedBuildCalls: 1,
            expectedWriteStatus: "failed",
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "request",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcBuildPreflight,
            retryUnchangedSafe: false,
            expectPostAdmissionAuthority: true);
        await RequireCancellationAsync(
            "executor reported cancellation",
            preflightDocuments,
            new FixtureBuildService(
                fixture.OutputRoot,
                FixtureBuildMode.Cancelled),
            verifier,
            packageAdmission: null,
            expectedBuildCalls: 1,
            expectedWriteStatus: "failed",
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "request",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcBuildPreflight,
            retryUnchangedSafe: false,
            expectPostAdmissionAuthority: true);
        await RequireCancellationAsync(
            "package verification cancellation",
            preflightDocuments,
            new FixtureBuildService(fixture.OutputRoot),
            new CancellingPackageVerifyService(),
            packageAdmission: null,
            expectedBuildCalls: 1,
            expectedWriteStatus: "failed",
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "request",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcBuildPreflight,
            retryUnchangedSafe: false,
            expectPostAdmissionAuthority: true);
        await RequireCancellationAsync(
            "package admission cancellation",
            preflightDocuments,
            new FixtureBuildService(fixture.OutputRoot),
            verifier,
            new CancellingPackageAdmission(),
            expectedBuildCalls: 1,
            expectedWriteStatus: "failed",
            expectedRecoveryAction: "chooseFreshOutput",
            expectedRecoveryOption: "request",
            expectedRecoveryArtifactKind:
                WorkflowArtifactKinds.NpcBuildPreflight,
            retryUnchangedSafe: false,
            expectPostAdmissionAuthority: true);

        async Task RequireCancellationAsync(
            string name,
            INpcBuildPreflightDocumentCodec documents,
            FixtureBuildService build,
            IPackageVerifyService packageVerifier,
            INpcStaticBuildPackageAdmission? packageAdmission,
            int expectedBuildCalls,
            string expectedWriteStatus,
            string expectedRecoveryAction,
            string expectedRecoveryOption,
            string expectedRecoveryArtifactKind,
            bool retryUnchangedSafe,
            bool expectPostAdmissionAuthority)
        {
            WorkspacePath workflowOutput = Child(
                root,
                "workflow",
                $"cancellation-{name.Replace(' ', '-')}.json");
            using var output = new StringWriter();
            var runner = new ProtocolV2Runner(
                output,
                _ => new CapturingJournal(),
                [new ProtocolV2NpcCreateFromJslotAdapter(
                    root,
                    new FaceGeomHairRegionsDocumentCodec(root),
                    RequestLoader(root),
                    new UnusedPreflightService(),
                    documents,
                    build,
                    packageVerifier,
                    packageReader,
                    lifecycle,
                    packageAdmission: packageAdmission,
                    standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
                AgentCommandRegistry.All);

            CommandExitCode exit = await runner.RunAsync(
                fixture.Command(workflowOutput),
                CancellationToken.None);
            string[] lines = output.ToString().Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 1,
                $"Build cancellation case '{name}' emitted an invalid envelope count.");
            using JsonDocument envelope = JsonDocument.Parse(lines[0]);
            JsonElement value = envelope.RootElement;
            JsonElement diagnostic = value.GetProperty("diagnostics")
                .EnumerateArray().Single();
            JsonElement recovery = diagnostic.GetProperty("recovery");
            Require(exit == CommandExitCode.Cancelled &&
                    value.GetProperty("outcome").GetString() == "cancelled" &&
                    diagnostic.GetProperty("code").GetString() ==
                        ProtocolV2DiagnosticCodes.ProtocolOperationCancelled &&
                    diagnostic.GetProperty("class").GetString() ==
                        "cancellation" &&
                    recovery.GetProperty("action").GetString() ==
                        expectedRecoveryAction &&
                    recovery.GetProperty("option").GetString() ==
                        expectedRecoveryOption &&
                    recovery.GetProperty("artifactKind").GetString() ==
                        expectedRecoveryArtifactKind &&
                    recovery.GetProperty("retryUnchangedSafe").GetBoolean() ==
                        retryUnchangedSafe &&
                    value.GetProperty("artifacts").GetArrayLength() == 0 &&
                    value.GetProperty("effects").EnumerateArray()
                        .Select(Effect).SequenceEqual([
                            "readWorkspace|completed|workspace",
                            $"writeNewArtifact|{expectedWriteStatus}|k-local-output",
                            "appendLocalOperationJournal|attempted|workspace-local-journal",
                            "appendLocalOperationJournal|completed|workspace-local-journal"
                        ], StringComparer.Ordinal) &&
                    build.Calls == expectedBuildCalls &&
                    !File.Exists(workflowOutput.Value) &&
                    AuthorityState(value, "inputAdmission") ==
                        (expectPostAdmissionAuthority
                            ? "established"
                            : "blocked") &&
                    AuthorityState(value, "sourceProviderIdentity") ==
                        (expectPostAdmissionAuthority
                            ? "established"
                            : "required") &&
                    AuthorityState(value, "deterministicMaterialization") ==
                        "required" &&
                    AuthorityState(value, "independentStaticVerification") ==
                        "required",
                $"Build cancellation case '{name}' projected the wrong stage, recovery, effects, or authority: {output}.");
        }
    }

    private static async Task AssertPostBuildRefusalsAsync(
        WorkspacePath root,
        KOnlyWorkspacePolicy policy,
        NpcBuildPreflightDocumentCodec preflightDocuments,
        AgentWorkflowBundleTransitionService lifecycle,
        BuildFixture fixture)
    {
        var realVerifier = new PackageVerifyService(
            new PackageManifestReader(policy, root));
        await AssertRefusedAsync(
            "occupied build destination",
            FixtureBuildMode.Occupied,
            Child(root, "workflow", "occupied.json"));
        var verboseFailure = await ExecuteAsync(
            new FixtureBuildService(
                fixture.OutputRoot,
                FixtureBuildMode.VerboseFailure),
            Child(root, "workflow", "verbose-failure.json"));
        using (JsonDocument verboseEnvelope = JsonDocument.Parse(
                   verboseFailure.Json))
        {
            string? message = verboseEnvelope.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() ==
                    ProtocolV2DiagnosticCodes.NpcBuildOperationFailed)
                .GetProperty("message")
                .GetString();
            Require(message is not null &&
                    message.Contains(
                        "terminal-build-failure",
                        StringComparison.Ordinal),
                "Verbose legacy build output hid its terminal error diagnostic.");
            RequirePostAdmissionRefusal(
                verboseEnvelope.RootElement,
                "verbose legacy build failure");
        }
        await AssertRefusedAsync(
            "incomplete package closure",
            FixtureBuildMode.Incomplete,
            Child(root, "workflow", "incomplete.json"));
        await AssertRefusedAsync(
            "package admission drift",
            FixtureBuildMode.AdmissionDrift,
            Child(root, "workflow", "admission-drift.json"));
        await AssertRefusedAsync(
            "cancelled build",
            FixtureBuildMode.Cancelled,
            Child(root, "workflow", "cancelled.json"),
            expectedOutcome: "cancelled");
        await AssertPackageAdmissionClosureAsync();

        WorkspacePath diagnosticOutput = Child(
            root, "workflow", "diagnostics.json");
        var diagnosticRun = await ExecuteAsync(
            new FixtureBuildService(
                fixture.OutputRoot,
                FixtureBuildMode.InvalidPackage),
            diagnosticOutput);
        using JsonDocument diagnosticEnvelope = JsonDocument.Parse(
            diagnosticRun.Json);
        JsonElement projected = diagnosticEnvelope.RootElement
            .GetProperty("result")
            .GetProperty("diagnostics");
        Require(projected.GetArrayLength() == 2 &&
                projected[0].GetProperty("code").GetString() ==
                    "package-artifact-hash-mismatch" &&
                projected[1].GetProperty("code").GetString() ==
                    "package-undeclared-file",
            "Package verifier diagnostics were not projected losslessly.");
        Require(diagnosticRun.Journal.Records is [var diagnosticRecord] &&
                diagnosticRecord.DiagnosticCodes.SequenceEqual(
                    [ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed],
                    StringComparer.Ordinal),
            "Package verification refusal was not journaled with stable vocabulary.");
        RequirePostAdmissionRefusal(
            diagnosticEnvelope.RootElement,
            "invalid package verification");

        async Task AssertRefusedAsync(
            string name,
            FixtureBuildMode mode,
            WorkspacePath workflowOutput,
            string expectedOutcome = "failed")
        {
            (CommandExitCode exit, string json, FixtureBuildService build,
                CapturingJournal journal) =
                await ExecuteAsync(
                    new FixtureBuildService(fixture.OutputRoot, mode),
                    workflowOutput);
            using JsonDocument envelope = JsonDocument.Parse(json);
            string expectedCode = mode switch
            {
                FixtureBuildMode.Occupied =>
                    ProtocolV2DiagnosticCodes.NpcBuildOperationFailed,
                FixtureBuildMode.Cancelled =>
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                _ => ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed
            };
            Require(exit != CommandExitCode.Success &&
                    envelope.RootElement.GetProperty("outcome").GetString() ==
                        expectedOutcome &&
                    envelope.RootElement.GetProperty("diagnostics")
                        .EnumerateArray().Single()
                        .GetProperty("code").GetString() == expectedCode &&
                    !File.Exists(workflowOutput.Value) &&
                    build.Calls == 1 &&
                    journal.Records is [var record] &&
                    record.DiagnosticCodes.SequenceEqual(
                        [expectedCode],
                        StringComparer.Ordinal),
                $"Reviewed build admitted or persisted {name}.");
            RequirePostAdmissionRefusal(envelope.RootElement, name);
        }

        static void RequirePostAdmissionRefusal(
            JsonElement envelope,
            string name)
        {
            Require(envelope.GetProperty("artifacts").GetArrayLength() == 0 &&
                    envelope.GetProperty("effects").EnumerateArray()
                        .Select(Effect).SequenceEqual([
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|failed|k-local-output",
                            "appendLocalOperationJournal|attempted|workspace-local-journal",
                            "appendLocalOperationJournal|completed|workspace-local-journal"
                        ], StringComparer.Ordinal) &&
                    AuthorityState(envelope, "inputAdmission") ==
                        "established" &&
                    AuthorityState(envelope, "sourceProviderIdentity") ==
                        "established" &&
                    AuthorityState(envelope, "deterministicMaterialization") ==
                        "required" &&
                    AuthorityState(envelope, "independentStaticVerification") ==
                        "required",
                $"Post-admission build refusal '{name}' projected false effects, artifacts, or authority.");
        }

        async Task<(CommandExitCode Exit, string Json,
            FixtureBuildService Build, CapturingJournal Journal)>
            ExecuteAsync(
                FixtureBuildService build,
                WorkspacePath workflowOutput)
        {
            using var output = new StringWriter();
            var journal = new CapturingJournal();
            var runner = new ProtocolV2Runner(
                output,
                _ => journal,
                [new ProtocolV2NpcCreateFromJslotAdapter(
                    root,
                    new FaceGeomHairRegionsDocumentCodec(root),
                    RequestLoader(root),
                    new UnusedPreflightService(),
                    preflightDocuments,
                    build,
                    build.Mode == FixtureBuildMode.AdmissionDrift
                        ? new DriftingPackageVerifyService(
                            realVerifier,
                            Child(
                                fixture.OutputRoot,
                                "Data",
                                "BuildNpc.esp"))
                        : realVerifier,
                    new PackageManifestReader(policy, root),
                    lifecycle,
                    standaloneAuthorityReader: OrdinaryStandaloneReader.Instance)],
                AgentCommandRegistry.All);
            CommandExitCode exit = await runner.RunAsync(
                fixture.Command(workflowOutput),
                CancellationToken.None);
            string[] lines = output.ToString().Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 1,
                "Post-build refusal emitted an invalid envelope count.");
            return (exit, lines[0], build, journal);
        }

        async Task AssertPackageAdmissionClosureAsync()
        {
            ParsedCommand command = fixture.Command(
                Child(root, "workflow", "lease-proof.json"));
            RaceMenuJslotNpcBuildCommandBinding binding =
                RaceMenuJslotNpcBuildCommandBinder.Bind(
                    command,
                    strict: true,
                    requireReviewedPreflight: true).Binding ??
                throw new InvalidOperationException(
                    "Package admission fixture binding failed.");
            RaceMenuNpcExecutionRequestFileLoadResult loaded =
                await RequestLoader(root).LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        binding.SourceRequest,
                        binding.SourceRequestSha256),
                    CancellationToken.None);
            RaceMenuNpcExecutionRequest request = loaded.Request ??
                throw new InvalidOperationException(
                    "Package admission fixture request failed to load.");
            var admission = new NpcStaticBuildPackageAdmission(
                root,
                new PackageManifestReader(policy, root));

            await RequireDriftRefusalAsync(
                Child(fixture.OutputRoot, "Data", "BuildNpc.esp"),
                "asset drift");
            await RequireDriftRefusalAsync(
                Child(fixture.OutputRoot, "npcmanager-package.json"),
                "manifest drift");

            PackageVerificationArtifact retainedVerification =
                await BuildAndVerifyAsync();
            await using NpcStaticBuildPackageLease lease =
                await admission.AdmitAsync(
                    retainedVerification,
                    binding,
                    request,
                    CancellationToken.None);
            try
            {
                File.WriteAllText(
                    Child(fixture.OutputRoot, "Data", "BuildNpc.esp").Value,
                    "mutation-during-publication",
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));
                throw new InvalidOperationException(
                    "Retained package admission allowed asset mutation.");
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                // The retained no-follow lease protects workflow publication.
            }

            async Task RequireDriftRefusalAsync(
                WorkspacePath path,
                string role)
            {
                PackageVerificationArtifact verified =
                    await BuildAndVerifyAsync();
                File.WriteAllText(
                    path.Value,
                    "post-verification-drift",
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));
                NpcStaticBuildPackageLease? unexpected = null;
                try
                {
                    unexpected = await admission.AdmitAsync(
                        verified,
                        binding,
                        request,
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        $"Package admission accepted {role}.");
                }
                catch (InvalidDataException)
                {
                    // The package must be unchanged after verification.
                }
                finally
                {
                    if (unexpected is not null)
                        await unexpected.DisposeAsync();
                }
            }

            async Task<PackageVerificationArtifact> BuildAndVerifyAsync()
            {
                RaceMenuJslotNpcBuildCommandExecution execution =
                    await new FixtureBuildService(fixture.OutputRoot)
                        .ExecuteAsync(command, CancellationToken.None);
                Require(execution.ExitCode == CommandExitCode.Success,
                    "Package admission fixture did not build.");
                PackageVerifyResult verified = await realVerifier.VerifyAsync(
                    new PackageVerifyRequest(Child(
                        fixture.OutputRoot,
                        "npcmanager-package.json")),
                    CancellationToken.None);
                return verified is { Verified: true, Artifact: { } artifact }
                    ? artifact
                    : throw new InvalidOperationException(
                        "Package admission fixture did not verify.");
            }
        }
    }

    private static void AssertRegistryAndSchemaContract()
    {
        AgentCommandContract build =
            AgentCommandRegistry.GetRequired("npc create-from-jslot");
        Require(build.Readiness == ProtocolReadiness.V2 &&
                build.ResultSchemaIds.SequenceEqual([
                    AgentProtocolSchemaIds.NpcCreatePreflightResult,
                    AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult
                ], StringComparer.Ordinal),
            "NPC create did not advertise distinct preflight/build results.");
        foreach (string option in new[]
                 {
                     "reviewed-preflight",
                     "reviewed-preflight-sha256",
                     "workflow-bundle",
                     "workflow-bundle-sha256",
                     "workflow-output"
                 })
        {
            Require(build.Options.Any(item => item.CliName == option),
                $"NPC create omitted --{option}.");
        }
        AgentCommandContract preview = AgentCommandRegistry.GetRequired(
            "preview npc");
        Require(preview.Readiness == ProtocolReadiness.V2 &&
                PreviewOptionNames.All(option => preview.Options.Any(item =>
                    item.CliName == option)),
            "Protocol-v2 preview target lacks truthful next-action metadata.");

        JsonElement export = ProtocolV2SchemaService.RenderInline(
            "npc create-from-jslot");
        JsonElement buildSchema = export.GetProperty("resultSchemas")
            .EnumerateArray().Single(item => item
                .GetProperty("schemaIdentifier").GetString() ==
                AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult);
        JsonElement jsonSchema = buildSchema.GetProperty("jsonSchema");
        Require(!jsonSchema.GetProperty("additionalProperties").GetBoolean() &&
                jsonSchema.GetProperty("required").EnumerateArray()
                    .Any(item => item.GetString() == "diagnostics") &&
                jsonSchema.GetProperty("properties")
                    .GetProperty("diagnostics")
                    .GetProperty("items")
                .GetProperty("additionalProperties").GetBoolean() == false,
            "NPC build result schema is not closed.");
        AssertExternalPrepublicationSchema(jsonSchema);

        JsonElement[] documentSchemas = export.GetProperty("documentSchemas")
            .EnumerateArray().ToArray();
        JsonElement requestDocument = documentSchemas.FirstOrDefault(item =>
            item.GetProperty("schemaIdentifier").GetString() ==
                NpcCreateRequestSchemaIdentifier);
        Require(documentSchemas.Length == 3 &&
                documentSchemas.Any(item => item.GetProperty("schemaIdentifier").GetString() == "skyrim-face-bake-authority/1" &&
                    item.GetProperty("name").GetString() == "face-bake-authority" &&
                    item.GetProperty("direction").GetString() == "output") &&
                requestDocument.ValueKind != JsonValueKind.Undefined &&
                requestDocument.GetProperty("name").GetString() == "request" &&
                requestDocument.GetProperty("direction").GetString() == "input",
            "NPC create did not export one stable closed request document schema.");
        AssertNpcCreateRequestSchema(requestDocument.GetProperty("jsonSchema"));
    }

    private static void AssertExternalPrepublicationSchema(JsonElement schema)
    {
        JsonElement properties = schema.GetProperty("properties");
        JsonElement external = properties.GetProperty(
            "externalInstallPrepublication");
        Require(external.GetProperty("type").EnumerateArray()
                    .Select(item => item.GetString())
                    .SequenceEqual(["object", "null"], StringComparer.Ordinal) &&
                !external.GetProperty("additionalProperties").GetBoolean(),
            "NPC build schema did not admit the closed nullable external SMP artifact.");
        string[] required = external.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Require(required.SequenceEqual([
                    "packageManifestSha256", "selectedManifestSha256",
                    "bindings", "verification"
                ], StringComparer.Ordinal),
            "External SMP wrapper required fields drifted.");
        JsonElement wrapperProperties = external.GetProperty("properties");
        Require(wrapperProperties.GetProperty("packageManifestSha256")
                    .GetProperty("pattern").GetString() ==
                    "^[0-9a-f]{64}$" &&
                wrapperProperties.GetProperty("selectedManifestSha256")
                    .GetProperty("pattern").GetString() ==
                    "^[0-9a-f]{64}$" &&
                wrapperProperties.GetProperty("bindings")
                    .GetProperty("items")
                    .GetProperty("additionalProperties").GetBoolean() == false,
            "External SMP wrapper did not preserve lowercase hash and closed binding contracts.");
        JsonElement verification = wrapperProperties.GetProperty("verification");
        Require(!verification.GetProperty("additionalProperties").GetBoolean() &&
                verification.GetProperty("properties")
                    .GetProperty("currentInstallDependencyState")
                    .GetProperty("enum").EnumerateArray()
                    .Select(item => item.GetString())
                    .SequenceEqual(["not-required", "declared-unverified", "verified"],
                        StringComparer.Ordinal),
            "External SMP verification schema did not admit canonical state tokens as a closed object.");
    }

    private static void AssertProviderRefusalAuthority(JsonElement envelope)
    {
        JsonElement[] authority = envelope.GetProperty("authority")
            .EnumerateArray().ToArray();
        Require(authority.Length == 8 &&
                authority.Single(item => item.GetProperty("kind").GetString() ==
                    "sourceProviderIdentity").GetProperty("state").GetString() ==
                    "required" &&
                authority.Single(item => item.GetProperty("kind").GetString() ==
                    "deterministicMaterialization").GetProperty("state").GetString() ==
                    "required" &&
                authority.Single(item => item.GetProperty("kind").GetString() ==
                    "independentStaticVerification").GetProperty("state").GetString() ==
                    "required",
            "Typed SMP refusal claimed provider or downstream authority was established.");
    }

    private static void AssertNpcCreateRequestSchema(JsonElement schema)
    {
        Require(schema.GetProperty("$schema").GetString() ==
                    "https://json-schema.org/draft/2020-12/schema" &&
                schema.GetProperty("type").GetString() == "object" &&
                !schema.GetProperty("additionalProperties").GetBoolean(),
            "NPC create request schema is not a closed draft-2020-12 object.");

        string[] required = RequiredMembers(schema);
        Require(required.SequenceEqual([
                    "schemaVersion", "edition", "presetBundle",
                    "providerContext", "standaloneAssets", "output",
                    "identity", "traits", "references", "stats"
                ]),
            "NPC create request schema did not publish its exact required top-level members.");

        JsonElement properties = schema.GetProperty("properties");
        Require(properties.EnumerateObject().Select(item => item.Name)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual([
                        "allowInheritedMeshEmbeddedSkinTextureRoute",
                        "applyBodySlide", "edition", "existingNpcTarget",
                        "faceGeomSkeletonAuthority", "identity", "output",
                        "presetBundle", "providerContext", "references",
                        "schemaVersion", "standaloneAssets", "stats", "traits",
                        "wholeSkinAuthority"
                    ]),
            "NPC create request schema drifted from the private request DTO fields.");
        Require(properties.GetProperty("edition").GetProperty("const")
                    .GetString() == "skyrimse" &&
                IntEnum(properties.GetProperty("schemaVersion"))
                    .SequenceEqual([1, 2, 3]),
            "NPC create request schema omitted the supported edition or schema versions.");

        JsonElement presetBundle = ObjectSchema(schema, properties, "presetBundle");
        JsonElement presetProperties = presetBundle.GetProperty("properties");
        Require(!presetBundle.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(presetBundle).SequenceEqual([
                    "manifestPath", "manifestSha256", "presetPath",
                    "presetSha256", "faceGeomPath", "faceGeomSha256",
                    "faceTintPath", "faceTintSha256", "recordAuthorityPath",
                    "recordAuthoritySha256", "runtimeRoutesPath",
                    "runtimeRoutesSha256"
                ]) &&
                Reference(presetProperties, "manifestPath") == "#/$defs/path" &&
                Reference(presetProperties, "manifestSha256") == "#/$defs/sha256" &&
                Reference(presetProperties, "presetPath") == "#/$defs/path" &&
                Reference(presetProperties, "presetSha256") == "#/$defs/sha256" &&
                Reference(presetProperties, "faceGeomPath") == "#/$defs/path" &&
                Reference(presetProperties, "faceGeomSha256") == "#/$defs/sha256" &&
                Reference(presetProperties, "faceTintPath") == "#/$defs/path" &&
                Reference(presetProperties, "faceTintSha256") == "#/$defs/sha256" &&
                Reference(presetProperties, "recordAuthorityPath") == "#/$defs/path" &&
                Reference(presetProperties, "recordAuthoritySha256") == "#/$defs/sha256" &&
                Reference(presetProperties, "runtimeRoutesPath") == "#/$defs/path" &&
                Reference(presetProperties, "runtimeRoutesSha256") == "#/$defs/sha256",
            "NPC create request schema did not bind every preset-bundle path/hash authority pair.");

        JsonElement standalone = ObjectSchema(schema, properties, "standaloneAssets");
        Require(!standalone.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(standalone).SequenceEqual([
                    "manifestPath", "manifestSha256"
                ]) &&
                Reference(standalone.GetProperty("properties"), "manifestPath") ==
                    "#/$defs/path" &&
                Reference(standalone.GetProperty("properties"), "manifestSha256") ==
                    "#/$defs/sha256",
            "NPC create request schema did not close standalone asset authority.");

        JsonElement provider = ObjectSchema(schema, properties, "providerContext");
        JsonElement[] providerArms = provider.GetProperty("oneOf")
            .EnumerateArray().ToArray();
        JsonElement workspaceArm = providerArms.FirstOrDefault(item =>
            RequiredMembers(item).Contains("manifestPath", StringComparer.Ordinal));
        JsonElement productArm = providerArms.FirstOrDefault(item =>
            RequiredMembers(item).Contains("productFixtureBundle",
                StringComparer.Ordinal));
        Require(!provider.GetProperty("additionalProperties").GetBoolean() &&
                providerArms.Length == 2 &&
                workspaceArm.ValueKind != JsonValueKind.Undefined &&
                productArm.ValueKind != JsonValueKind.Undefined &&
                RequiredMembers(workspaceArm).SequenceEqual([
                    "manifestPath", "manifestSha256", "templatePlugin",
                    "templateSha256", "templateNpcFormId", "faceGeomCarrier",
                    "faceGeomSha256", "faceTintManifest",
                    "faceTintProviderRoot", "dependencyManifest"
                ]) &&
                RequiredMembers(productArm).SequenceEqual([
                    "templateNpcFormId", "productFixtureBundle"
                ]),
            "NPC create request schema did not publish exclusive provider arms.");

        JsonElement productBundle = ObjectSchema(
            schema,
            productArm.GetProperty("properties"),
            "productFixtureBundle");
        Require(!productBundle.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(productBundle).SequenceEqual([
                    "bundleId", "registryManifestSha256"
                ]) &&
                Reference(productBundle.GetProperty("properties"),
                    "registryManifestSha256") == "#/$defs/sha256",
            "NPC create request schema did not close product fixture authority.");

        JsonElement target = ObjectSchema(schema, properties, "existingNpcTarget");
        Require(!target.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(target).SequenceEqual([
                    "sourcePlugin", "sourcePluginSha256", "targetFormId"
                ]) &&
                Reference(target.GetProperty("properties"), "sourcePlugin") ==
                    "#/$defs/path" &&
                Reference(target.GetProperty("properties"), "sourcePluginSha256") ==
                    "#/$defs/sha256" &&
                Reference(target.GetProperty("properties"), "targetFormId") ==
                    "#/$defs/formId",
            "NPC create request schema did not close existing-NPC target authority.");

        JsonElement[] versionArms = schema.GetProperty("oneOf")
            .EnumerateArray().ToArray();
        JsonElement version1 = VersionArm(versionArms, 1);
        JsonElement version2 = VersionArm(versionArms, 2);
        JsonElement version3 = VersionArm(versionArms, 3);
        Require(versionArms.Length == 3 &&
                !Requires(version1, "existingNpcTarget") &&
                Requires(version2, "existingNpcTarget") &&
                !Requires(version3, "existingNpcTarget"),
            "NPC create request schema did not publish existing-target rules for versions 1/2/3.");

        JsonElement traits = ObjectSchema(schema, properties, "traits");
        Require(!traits.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(traits).SequenceEqual([
                    "sex", "role", "unique", "essential", "protected",
                    "respawns", "autoCalcStats"
                ]) &&
                EnumValues(traits.GetProperty("properties").GetProperty("sex"))
                    .SequenceEqual(["female", "male"]) &&
                EnumValues(traits.GetProperty("properties").GetProperty("role"))
                    .SequenceEqual([
                        "civilian", "combatant", "follower", "merchant",
                        "static-validation"
                    ]),
            "NPC create request schema omitted the mapper's trait enums.");

        JsonElement references = ObjectSchema(schema, properties, "references");
        Require(!references.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(references).SequenceEqual([
                    "race", "voice", "class", "combatStyle"
                ]) &&
                Reference(references.GetProperty("properties"), "race") ==
                    "#/$defs/formReference" &&
                Reference(references.GetProperty("properties"), "voice") ==
                    "#/$defs/formReference" &&
                Reference(references.GetProperty("properties"), "class") ==
                    "#/$defs/formReference" &&
                Reference(references.GetProperty("properties"), "combatStyle") ==
                    "#/$defs/formReference" &&
                Reference(references.GetProperty("properties"), "defaultOutfit") ==
                    "#/$defs/nullableFormReference",
            "NPC create request schema omitted the mapper's form-reference contract.");

        JsonElement stats = ObjectSchema(schema, properties, "stats");
        Require(!stats.GetProperty("additionalProperties").GetBoolean() &&
                RequiredMembers(stats).SequenceEqual([
                    "levelMode", "level", "magickaOffset", "staminaOffset",
                    "healthOffset", "calcMinLevel", "calcMaxLevel",
                    "speedMultiplier", "dispositionBase", "bleedoutOverride",
                    "baseHealth", "baseMagicka", "baseStamina", "height",
                    "weight", "farAwayModelDistance"
                ]) &&
                EnumValues(stats.GetProperty("properties").GetProperty("levelMode"))
                    .SequenceEqual(["fixed", "multiplier"]),
            "NPC create request schema omitted the mapper's stats fields or enum.");

        JsonElement identity = ObjectSchema(schema, properties, "identity");
        JsonElement output = ObjectSchema(schema, properties, "output");
        Require(RequiredMembers(identity).SequenceEqual(["editorId", "name"]) &&
                RequiredMembers(output).SequenceEqual(["root", "plugin"]),
            "NPC create request schema omitted identity or output members.");
        Require(properties.GetProperty("applyBodySlide").GetProperty("type")
                    .GetString() == "boolean" &&
                properties.GetProperty("allowInheritedMeshEmbeddedSkinTextureRoute")
                    .GetProperty("type").GetString() == "boolean" &&
                EnumValues(properties.GetProperty("faceGeomSkeletonAuthority"))
                    .SequenceEqual([
                        "sourceModelWorldTranslations", "identityFaceGenBones"
                    ]),
            "NPC create request schema omitted current optional request fields.");
    }

    private static JsonElement ObjectSchema(
        JsonElement root,
        JsonElement properties,
        string name)
    {
        JsonElement value = properties.GetProperty(name);
        if (value.TryGetProperty("$ref", out JsonElement reference) &&
            reference.GetString() is string referenceValue &&
            referenceValue.StartsWith("#/$defs/", StringComparison.Ordinal))
            return root.GetProperty("$defs").GetProperty(
                referenceValue["#/$defs/".Length..]);
        return value;
    }

    private static string[] RequiredMembers(JsonElement schema) =>
        schema.TryGetProperty("required", out JsonElement required)
            ? required.EnumerateArray().Select(item => item.GetString()!)
                .ToArray()
            : [];

    private static string Reference(JsonElement properties, string name) =>
        properties.GetProperty(name).GetProperty("$ref").GetString()!;

    private static string[] EnumValues(JsonElement schema) =>
        schema.GetProperty("enum").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();

    private static int[] IntEnum(JsonElement schema) =>
        schema.GetProperty("enum").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();

    private static JsonElement VersionArm(JsonElement[] arms, int version) =>
        arms.FirstOrDefault(item =>
            item.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const").GetInt32() == version);

    private static bool Requires(JsonElement schema, string name) =>
        RequiredMembers(schema).Contains(name, StringComparer.Ordinal);

    private static string Effect(JsonElement effect) => string.Join('|',
        effect.GetProperty("kind").GetString(),
        effect.GetProperty("status").GetString(),
        effect.GetProperty("scope").GetString());

    private static string AuthorityState(JsonElement envelope, string kind) =>
        envelope.GetProperty("authority").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == kind)
            .GetProperty("state").GetString()!;

    private static bool IsUpperSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static WorkspacePath Child(
        WorkspacePath root,
        params string[] parts) => new(
        parts.Aggregate(root.Value, (current, part) =>
            Path.Combine(current, part)));

    private static string HashFile(WorkspacePath path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path.Value)));

    private static RaceMenuNpcExecutionRequestFileLoader RequestLoader(
        WorkspacePath root) => new RaceMenuNpcExecutionRequestFileLoader(
        root,
        new ApplicationProviderResourceRegistry(
            new ApplicationResourcePath(AppContext.BaseDirectory)));

    private static WorkspacePath CreateAuthenticRequestFixture(
        WorkspacePath root)
    {
        string repository = FindRepositoryRoot();
        string probeRoot = Path.Combine(
            repository,
            "tools",
            "release",
            "protocol-v2-workflow-probes");
        string source = Path.Combine(probeRoot, "npc-create-preflight");
        WorkspacePath target = Child(root, "npc-preflight");
        Directory.CreateDirectory(target.Value);
        foreach (string name in AuthenticRequestJsonFiles)
            File.Copy(
                Path.Combine(source, name),
                Path.Combine(target.Value, name));
        string syntheticProvider = SyntheticProductProviderFixture.Ensure(
            AppContext.BaseDirectory);
        File.Copy(Path.Combine(syntheticProvider, "Data", "meshes",
                "actors", "character", "FaceGenData", "FaceGeom",
                "ActorwrightBlankNpcProvider.esp", "00000800.nif"),
            Path.Combine(target.Value, "face.nif"));
        File.Copy(Path.Combine(syntheticProvider, "Data", "textures",
                "actors", "character", "FaceGenData", "FaceTint",
                "ActorwrightBlankNpcProvider.esp", "00000800.dds"),
            Path.Combine(target.Value, "face.dds"));
        Decode("Skyrim.esm.base64", "Skyrim.esm");
        File.Copy(
            Path.Combine(probeRoot, "preset-inspect", "fixture.jslot"),
            Path.Combine(target.Value, "fixture.jslot"));

        JsonObject request = JsonNode.Parse(File.ReadAllText(
                Path.Combine(source, "request.json")))?.AsObject() ??
            throw new InvalidDataException(
                "The authentic NPC request fixture is invalid JSON.");
        var providerRegistry = new ApplicationProviderResourceRegistry(
            new ApplicationResourcePath(AppContext.BaseDirectory));
        Require(providerRegistry.TryGetDefaultBlankNpcFixture(
                    out ProductFixtureBundleReference? productBundle) &&
                productBundle is not null,
            "The synthetic blank-NPC test provider registry is unavailable.");
        request["providerContext"]!["productFixtureBundle"]!["registryManifestSha256"] =
            productBundle!.RegistryManifestSha256.Value;
        WorkspacePath bundlePath = Child(target, "preset-bundle.json");
        JsonObject bundle = JsonNode.Parse(File.ReadAllText(bundlePath.Value))!.AsObject();
        bundle["providerAuthority"]!["registryManifestSha256"] =
            productBundle!.RegistryManifestSha256.Value;
        string faceGeomSha256 = HashFile(new WorkspacePath(
            Path.Combine(target.Value, "face.nif")));
        string faceTintSha256 = HashFile(new WorkspacePath(
            Path.Combine(target.Value, "face.dds")));
        bundle["charGen"]!["faceGeomSha256"] = faceGeomSha256;
        bundle["charGen"]!["faceTintSha256"] = faceTintSha256;
        JsonObject standalone = JsonNode.Parse(File.ReadAllText(
            Path.Combine(target.Value, "standalone-assets.json")))!.AsObject();
        standalone["faceTint"]!["width"] = 1024;
        standalone["faceTint"]!["height"] = 1024;
        File.WriteAllText(Path.Combine(target.Value, "standalone-assets.json"),
            standalone.ToJsonString(), new UTF8Encoding(false));
        request["presetBundle"]!["faceGeomSha256"] = faceGeomSha256;
        request["presetBundle"]!["faceTintSha256"] = faceTintSha256;
        request["standaloneAssets"]!["manifestSha256"] = HashFile(
            new WorkspacePath(Path.Combine(target.Value,
                "standalone-assets.json")));
        File.WriteAllText(bundlePath.Value, bundle.ToJsonString(), new UTF8Encoding(false));
        request["presetBundle"]!["manifestSha256"] = HashFile(bundlePath);
        JsonObject output = request["output"]?.AsObject() ??
            throw new InvalidDataException("The fixture output is missing.");
        output["root"] = "package";
        output["plugin"] = "BuildNpc.esp";
        JsonObject identity = request["identity"]?.AsObject() ??
            throw new InvalidDataException("The fixture identity is missing.");
        identity["editorId"] = "BuildNpc";
        identity["name"] = "Build NPC";
        JsonObject traits = request["traits"]?.AsObject() ??
            throw new InvalidDataException("The fixture traits are missing.");
        traits["role"] = "static-validation";
        WorkspacePath requestPath = Child(target, "request.json");
        File.WriteAllText(
            requestPath.Value,
            request.ToJsonString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return requestPath;

        void Decode(string encodedName, string outputName)
        {
            string encoded = File.ReadAllText(
                Path.Combine(source, encodedName)).Trim();
            File.WriteAllBytes(
                Path.Combine(target.Value, outputName),
                Convert.FromBase64String(encoded));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "tools",
                    "release",
                    "protocol-v2-workflow-probes",
                    "npc-create-preflight",
                    "request.json")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "The repository protocol-v2 workflow probes were not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record BuildFixture(
        WorkspacePath Root,
        WorkspacePath RequestPath,
        Sha256Hash RequestSha256,
        WorkspacePath PresetPath,
        Sha256Hash PresetSha256,
        WorkspacePath PreflightPath,
        Sha256Hash PreflightSha256,
        WorkspacePath IntakePath,
        string IntakeSha256,
        WorkspacePath DataRoot,
        WorkspacePath WorkflowInput,
        string WorkflowInputSha256,
        WorkspacePath MismatchedIntakeWorkflowInput,
        string MismatchedIntakeWorkflowInputSha256,
        WorkspacePath MismatchedWorkflowInput,
        string MismatchedWorkflowInputSha256,
        WorkspacePath WorkflowOutput,
        WorkspacePath OutputRoot)
    {
        public static async Task<BuildFixture> CreateAsync(
            WorkspacePath root,
            NpcBuildPreflightDocumentCodec preflightDocuments,
            AgentWorkflowBundleTransitionService lifecycle)
        {
            Directory.CreateDirectory(Child(root, "evidence").Value);
            Directory.CreateDirectory(Child(root, "workflow").Value);
            Directory.CreateDirectory(Child(root, "companion").Value);
            WorkspacePath requestPath = CreateAuthenticRequestFixture(root);
            WorkspacePath presetPath = Child(
                root, "npc-preflight", "fixture.jslot");
            Directory.CreateDirectory(Child(root, "inputs").Value);
            WorkspacePath dataRoot = Child(root, "Data");
            WorkflowArtifactBinding intake = WriteReviewedIntake(
                root,
                dataRoot,
                Child(root, "inputs", "intake.json"),
                Child(root, "reserved-output"),
                [
                    "Skyrim.esm",
                    "Probe.esp",
                    "ActorwrightBlankNpcProvider.esp"
                ],
                [0, 2, 5]);
            WorkflowArtifactBinding mismatchedIntake = WriteReviewedIntake(
                root,
                Child(root, "OtherData"),
                Child(root, "inputs", "mismatched-data-root-intake.json"),
                Child(root, "other-reserved-output"),
                ["Skyrim.esm"]);
            var requestSha = new Sha256Hash(HashFile(requestPath));
            var presetSha = new Sha256Hash(HashFile(presetPath));
            WorkspacePath outputRoot = Child(root, "package");
            var preflightArtifact = new NpcBuildPreflightArtifact(
                NpcBuildPreflightSchemas.Artifact,
                "Actorwright",
                "fixture",
                "fixture",
                null,
                NpcBuildPreflightSchemas.DerivationVersion,
                requestPath,
                requestSha,
                presetSha,
                "Skyrim.esm|0x00013746",
                "female",
                ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp"],
                [],
                [],
                [],
                [],
                [new NpcBuildPreflightGate(
                    "request-authority", true, true, "accepted")],
                [],
                [],
                true,
                true,
                false);
            WorkspacePath preflightPath = Child(
                root, "evidence", "npc-preflight.json");
            NpcBuildPreflightDocument preflight =
                await preflightDocuments.WriteNewAsync(
                    preflightDocuments.Encode(preflightArtifact),
                    preflightPath,
                    CancellationToken.None);
            WorkflowArtifactBinding preset = Artifact(
                WorkflowArtifactKinds.RaceMenuJslot,
                "application/json",
                presetPath,
                "preset inspect");
            WorkflowArtifactBinding preflightBinding = new(
                WorkflowArtifactKinds.NpcBuildPreflight,
                NpcBuildPreflightSchemas.Artifact,
                preflightPath,
                preflight.Utf8Json.Length,
                preflight.Sha256.Value.ToUpperInvariant(),
                "npc create-from-jslot",
                RequestDigest,
                new[] { requestSha.Value.ToUpperInvariant(), presetSha.Value.ToUpperInvariant() }
                    .Order(StringComparer.Ordinal).ToImmutableArray());
            WorkspacePath workflowInput = Child(
                root, "workflow", "preflight.json");
            AgentWorkflowBundleTransition workflow = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "BuildNpc", "Build NPC", null, null),
                RequestDigest,
                [intake, preset, preflightBinding],
                workflowInput);
            WorkspacePath mismatchedIntakeWorkflowInput = Child(
                root, "workflow", "mismatched-intake.json");
            AgentWorkflowBundleTransition mismatchedIntakeWorkflow =
                lifecycle.WriteInitial(
                    new WorkflowNpcIdentity(
                        "BuildNpc", "Build NPC", null, null),
                    RequestDigest,
                    [mismatchedIntake, preset, preflightBinding],
                    mismatchedIntakeWorkflowInput);
            WorkspacePath mismatchedWorkflowInput = Child(
                root, "workflow", "identity-mismatch.json");
            AgentWorkflowBundleTransition mismatchedWorkflow =
                lifecycle.WriteInitial(
                    new WorkflowNpcIdentity(
                        "WrongBuildNpc", "Wrong Build NPC", null, null),
                    RequestDigest,
                    [intake, preset, preflightBinding],
                    mismatchedWorkflowInput);
            return new BuildFixture(
                root,
                requestPath,
                requestSha,
                presetPath,
                presetSha,
                preflightPath,
                preflight.Sha256,
                intake.Path,
                intake.Sha256,
                dataRoot,
                workflowInput,
                workflow.Document.Sha256,
                mismatchedIntakeWorkflowInput,
                mismatchedIntakeWorkflow.Document.Sha256,
                mismatchedWorkflowInput,
                mismatchedWorkflow.Document.Sha256,
                Child(root, "workflow", "package.json"),
                outputRoot);
        }

        public ParsedCommand Command(
            WorkspacePath workflowOutput,
            params string[] replacements)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["request"] = RequestPath.Value,
                ["request-sha256"] = RequestSha256.Value.ToUpperInvariant(),
                ["preset"] = PresetPath.Value,
                ["preset-sha256"] = PresetSha256.Value.ToUpperInvariant(),
                ["data-root"] = DataRoot.Value,
                ["plugins"] =
                    "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                ["companion-root"] = Child(Root, "companion").Value,
                ["reviewed-preflight"] = PreflightPath.Value,
                ["reviewed-preflight-sha256"] =
                    PreflightSha256.Value.ToUpperInvariant(),
                ["workflow-bundle"] = WorkflowInput.Value,
                ["workflow-bundle-sha256"] = WorkflowInputSha256,
                ["workflow-output"] = workflowOutput.Value
            };
            for (int index = 0; index < replacements.Length; index += 2)
                options[replacements[index].TrimStart('-')] =
                    replacements[index + 1];
            return Parse(options);
        }

        public ParsedCommand CommandWithout(
            WorkspacePath workflowOutput,
            string omitted)
        {
            ParsedCommand command = Command(workflowOutput);
            var options = command.Options
                .Where(item => !string.Equals(
                    item.Key, omitted, StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.Ordinal);
            return Parse(options);
        }

        private static ParsedCommand Parse(
            IReadOnlyDictionary<string, string> options)
        {
            var arguments = new List<string>
            {
                "npc", "create-from-jslot", "--protocol", "2", "--json"
            };
            foreach ((string key, string value) in options)
            {
                arguments.Add("--" + key);
                arguments.Add(value);
            }
            return CommandLine.Parse(arguments);
        }

        private static WorkflowArtifactBinding Artifact(
            string kind,
            string schema,
            WorkspacePath path,
            string producer) => new(
            kind,
            schema,
            path,
            new FileInfo(path.Value).Length,
            HashFile(path),
            producer,
            RequestDigest,
            []);

        private static WorkflowArtifactBinding WriteReviewedIntake(
            WorkspacePath root,
            WorkspacePath dataRoot,
            WorkspacePath intakePath,
            WorkspacePath reviewedOutput,
            ImmutableArray<string> pluginNames,
            ImmutableArray<int> pluginOrders = default)
        {
            if (!pluginOrders.IsDefault &&
                pluginOrders.Length != pluginNames.Length)
                throw new InvalidOperationException(
                    "Reviewed intake fixture names and orders differ in length.");
            Directory.CreateDirectory(dataRoot.Value);
            ImmutableArray<PluginClosureReviewEntry> plugins = pluginNames
                .Select((name, index) =>
                {
                    WorkspacePath pluginPath = Child(dataRoot, name);
                    File.WriteAllText(
                        pluginPath.Value,
                        $"reviewed-{name}",
                        new UTF8Encoding(false));
                    return new PluginClosureReviewEntry(
                        new PluginName(name),
                        pluginOrders.IsDefault ? index : pluginOrders[index],
                        true,
                        true,
                        true,
                        false,
                        true,
                        pluginPath,
                        new Sha256Hash(HashFile(pluginPath)),
                        []);
                })
                .ToImmutableArray();
            WorkspacePath loadOrder = Child(
                root,
                $"load-order-{Path.GetFileName(dataRoot.Value)}.txt");
            File.WriteAllText(
                loadOrder.Value,
                string.Join('\n', pluginNames),
                new UTF8Encoding(false));
            Sha256Hash assetFingerprint = new(new string('2', 64));
            var draft = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                root,
                dataRoot,
                loadOrder,
                reviewedOutput,
                new Sha256Hash(HashFile(loadOrder)),
                plugins,
                [],
                [],
                [],
                plugins.Length,
                assetFingerprint,
                new Sha256Hash(new string('0', 64)),
                false);
            ReviewedGameIntake intake = draft with
            {
                IntakeFingerprint =
                    ReviewedGameIntakeFingerprintAuthority.Fingerprint(draft)
            };
            ReviewedGameIntakeDocumentAuthority document =
                new FaceGeomHairRegionsDocumentCodec(root)
                    .BindReviewedIntake(intake, intakePath);
            File.WriteAllBytes(
                intakePath.Value,
                document.Document.Utf8Json.ToArray());
            return new WorkflowArtifactBinding(
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "npcmanager-reviewed-game-intake/2",
                intakePath,
                document.Document.ByteLength,
                document.Document.Sha256.Value.ToUpperInvariant(),
                "workspace preflight",
                RequestDigest,
                []);
        }
    }

    private enum FixtureBuildMode
    {
        Complete,
        Incomplete,
        InvalidPackage,
        AdmissionDrift,
        Occupied,
        VerboseFailure,
        ThrowsCancellation,
        Cancelled
    }

    private sealed class FixtureBuildService(
        WorkspacePath outputRoot,
        FixtureBuildMode mode = FixtureBuildMode.Complete,
        WorkspacePath? workflowCollision = null,
        bool realNpc = false) :
        IRaceMenuJslotNpcBuildCommandExecutor
    {
        public static readonly byte[] WorkflowCollisionBytes =
            "occupied after package publication"u8.ToArray();

        public int Calls { get; private set; }

        public FixtureBuildMode Mode => mode;

        public ValueTask<RaceMenuJslotNpcBuildCommandExecution> ExecuteAsync(
            ParsedCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (mode == FixtureBuildMode.ThrowsCancellation)
                throw new OperationCanceledException(
                    "The fixture build executor was cancelled.",
                    cancellationToken);
            if (mode == FixtureBuildMode.Cancelled)
                return ValueTask.FromResult(
                    new RaceMenuJslotNpcBuildCommandExecution(
                        CommandExitCode.Cancelled,
                        string.Empty,
                        "The fixture build was cancelled."));
            if (mode == FixtureBuildMode.Occupied)
                return ValueTask.FromResult(
                    new RaceMenuJslotNpcBuildCommandExecution(
                        CommandExitCode.ValidationFailure,
                        string.Empty,
                        "The package output is occupied."));
            if (mode == FixtureBuildMode.VerboseFailure)
            {
                object[] diagnostics = Enumerable.Range(0, 40)
                    .Select(index => (object)new
                    {
                        code = $"informational-prelude-{index:D2}",
                        severity = "info",
                        message = new string('x', 96)
                    })
                    .Append(new
                    {
                        code = "terminal-build-failure",
                        severity = "error",
                        message = "The terminal build failure must remain visible."
                    })
                    .ToArray();
                return ValueTask.FromResult(
                    new RaceMenuJslotNpcBuildCommandExecution(
                        CommandExitCode.ValidationFailure,
                        JsonSerializer.Serialize(new
                        {
                            completed = false,
                            diagnostics
                        }),
                        string.Empty));
            }
            Require(command.Options.ContainsKey("reviewed-preflight") &&
                    command.Options.ContainsKey("reviewed-preflight-sha256"),
                "Build service did not receive reviewed preflight authority.");
            if (Directory.Exists(outputRoot.Value))
                Directory.Delete(outputRoot.Value, recursive: true);
            Directory.CreateDirectory(outputRoot.Value);
            WorkspacePath plugin = Write(
                Path.Combine("Data", "BuildNpc.esp"),
                "plugin");
            if (realNpc)
            {
                SyntheticProductProviderFixture.Ensure(AppContext.BaseDirectory);
                File.WriteAllBytes(plugin.Value, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                    "runtime", "product-fixtures", "blank-npc-v1", "Data", "ActorwrightBlankNpcProvider.esp")));
            }
            bool hasRequiredClosure = mode != FixtureBuildMode.Incomplete;
            WorkspacePath? nif = hasRequiredClosure
                ? Write(
                    Path.Combine("Data", "meshes", "actors", "character",
                        "FaceGenData", "FaceGeom", "BuildNpc.esp",
                        "00000800.nif"),
                    "nif")
                : null;
            WorkspacePath? dds = hasRequiredClosure
                ? Write(
                    Path.Combine("Data", "textures", "actors", "character",
                        "FaceGenData", "FaceTint", "BuildNpc.esp",
                        "00000800.dds"),
                    "dds")
                : null;
            WorkspacePath proposal = Write(
                Path.Combine("evidence", "npc-creation-proposal.json"),
                "{\"schemaVersion\":1,\"artifactKind\":\"npc-creation-proposal\"}");
            WorkspacePath manifest = new(Path.Combine(
                outputRoot.Value, "npcmanager-package.json"));
            object[] rows = mode == FixtureBuildMode.Incomplete
                ?
                [
                    Row("plugin", plugin),
                    Row("npc-creation-proposal", proposal)
                ]
                :
                [
                    Row(
                        "plugin",
                        plugin,
                        wrongHash: mode == FixtureBuildMode.InvalidPackage),
                    Row("faceGeom", nif!.Value),
                    Row("faceTint", dds!.Value),
                    Row("npc-creation-proposal", proposal)
                ];
            if (mode == FixtureBuildMode.InvalidPackage)
                Write("undeclared.bin", "undeclared");
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                presetFormat = "blank-npc-creation-proposal",
                sourcePreset = Path.GetRelativePath(
                        outputRoot.Value,
                        proposal.Value)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                sourcePresetSha256 = HashFile(proposal),
                sourcePlugin = "Skyrim.esm",
                sourcePluginSha256 = new string('1', 64),
                outputPlugin = "BuildNpc.esp",
                targetFormId = "00000800",
                artifacts = rows
            });
            File.WriteAllBytes(manifest.Value, manifestBytes);
            if (workflowCollision is { } occupiedWorkflow)
                File.WriteAllBytes(
                    occupiedWorkflow.Value,
                    WorkflowCollisionBytes);
            return ValueTask.FromResult(
                new RaceMenuJslotNpcBuildCommandExecution(
                    CommandExitCode.Success,
                    "fixture build succeeded",
                    string.Empty));

            WorkspacePath Write(string relative, string value)
            {
                string path = Path.Combine(outputRoot.Value, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value, Encoding.UTF8);
                return new WorkspacePath(path);
            }

            object Row(
                string kind,
                WorkspacePath path,
                bool wrongHash = false)
            {
                string relative = Path.GetRelativePath(
                        outputRoot.Value, path.Value)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return new
                {
                    kind,
                    relativePath = relative,
                    byteLength = new FileInfo(path.Value).Length,
                    sha256 = wrongHash ? new string('0', 64) : HashFile(path)
                };
            }
        }
    }

    private sealed class Schema8StandaloneReader :
        IRaceMenuNpcStandaloneAuthorityReader
    {
        public int Calls { get; private set; }

        public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
            RaceMenuNpcStandaloneAssetAuthority authority,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(
                new RaceMenuNpcStandaloneAuthorityReadResult(
                    true,
                    new RaceMenuNpcStandaloneAssets(
                        "external-smp",
                        null!,
                        null!,
                        1,
                        1,
                        [],
                        null,
                        [],
                        null)
                    {
                        SchemaVersion = 8
                    },
                    []));
        }
    }

    private sealed class OrdinaryStandaloneReader :
        IRaceMenuNpcStandaloneAuthorityReader
    {
        public static OrdinaryStandaloneReader Instance { get; } = new();

        public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
            RaceMenuNpcStandaloneAssetAuthority authority,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new RaceMenuNpcStandaloneAuthorityReadResult(
                    true,
                    new RaceMenuNpcStandaloneAssets(
                        "ordinary",
                        null!,
                        null!,
                        1,
                        1,
                        [],
                        null,
                        [],
                        null)
                    {
                        SchemaVersion = 7
                    },
                    []));
        }
    }

    private sealed class CountingPackageVerifyService(
        IPackageVerifyService inner) : IPackageVerifyService
    {
        public int Calls { get; private set; }

        public async ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return await inner.VerifyAsync(request, cancellationToken);
        }
    }

    private sealed class TypedFixtureBuildService(
        WorkspacePath outputRoot,
        IPackageVerifyService packageVerifier,
        Sha256Hash presetSha256) : IRaceMenuJslotNpcBuildService
    {
        public int Calls { get; private set; }

        public async ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
            RaceMenuJslotNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            var fixture = new FixtureBuildService(outputRoot);
            RaceMenuJslotNpcBuildCommandExecution build =
                await fixture.ExecuteAsync(
                    CommandLine.Parse([
                        "npc", "create-from-jslot",
                        "--reviewed-preflight", "fixture",
                        "--reviewed-preflight-sha256", presetSha256.Value
                    ]),
                    cancellationToken);
            if (build.ExitCode != CommandExitCode.Success)
                return new RaceMenuJslotNpcBuildResult(
                    false, null, null, null, null, null,
                    [new Diagnostic(
                        "typed-fixture-build-failed",
                        DiagnosticSeverity.Error,
                        build.Error)]);

            WorkspacePath manifest = new(Path.Combine(
                outputRoot.Value,
                "npcmanager-package.json"));
            PackageVerifyResult verified = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(manifest), cancellationToken);
            Require(verified.Verified && verified.Artifact is not null,
                "The typed fixture package did not verify before the Protocol seam.");
            PackageVerificationArtifact package = verified.Artifact!;
            PackageFileVerification plugin = package.Files.Single(item =>
                item.Kind == "plugin");
            PackageFileVerification faceGeom = package.Files.Single(item =>
                item.Kind == "faceGeom");
            PackageFileVerification faceTint = package.Files.Single(item =>
                item.Kind == "faceTint");
            var blankArtifact = new BlankNpcBuildArtifact(
                "1",
                "npc-manager-blank-build",
                "static-pass-runtime-required",
                outputRoot,
                new WorkspacePath(Path.Combine(
                    outputRoot.Value, plugin.RelativePath.Value)),
                plugin.ActualSha256,
                package.TargetFormId,
                new WorkspacePath(Path.Combine(
                    outputRoot.Value, faceGeom.RelativePath.Value)),
                faceGeom.ActualSha256,
                new WorkspacePath(Path.Combine(
                    outputRoot.Value, faceTint.RelativePath.Value)),
                faceTint.ActualSha256,
                manifest,
                package.ManifestSha256,
                false);
            var buildResult = new BlankNpcBuildResult(
                true,
                blankArtifact,
                null,
                null,
                null,
                null,
                verified,
                []);
            RaceMenuNpcExternalInstallPrepublicationArtifact prepublication =
                CreatePrepublication(package.ManifestSha256);
            var execution = new RaceMenuNpcExecutionResult(
                true,
                null,
                CreateSchema8Assets(),
                null,
                null,
                null,
                buildResult,
                [])
            {
                ExternalInstallPrepublication = prepublication
            };
            return new RaceMenuJslotNpcBuildResult(
                true,
                null,
                null,
                null,
                null,
                execution,
                []);
        }

        private static RaceMenuNpcStandaloneAssets CreateSchema8Assets() =>
            new(
                "external-smp",
                null!,
                null!,
                1,
                1,
                [],
                null,
                [],
                null)
            {
                SchemaVersion = 8
            };

        private static RaceMenuNpcExternalInstallPrepublicationArtifact
            CreatePrepublication(Sha256Hash packageManifestSha256)
        {
            var descriptor = new Sha256Hash(new string('c', 64));
            var selectedManifest = new Sha256Hash(new string('b', 64));
            var attestation = new Sha256Hash(new string('d', 64));
            var providerHash = new Sha256Hash(new string('e', 64));
            var provider = new ExternalHeadPartInstallProviderObservation(
                new PluginName("HairPack.esp"),
                providerHash,
                providerHash,
                true);
            var verification = new ExternalHeadPartInstallVerificationArtifact(
                ExternalHeadPartSchemaIdentifiers.InstallVerification,
                true,
                true,
                null,
                [descriptor],
                null,
                ExternalInstallDependencyState.Verified,
                true,
                true,
                false,
                false,
                [provider],
                []);
            return new RaceMenuNpcExternalInstallPrepublicationArtifact(
                packageManifestSha256,
                selectedManifest,
                [new RaceMenuNpcExternalInstallPrepublicationBinding(
                    descriptor,
                    attestation)],
                verification);
        }
    }

    private sealed class RefusingTypedFixtureBuildService :
        IRaceMenuJslotNpcBuildService
    {
        public int Calls { get; private set; }

        public ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
            RaceMenuJslotNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(
                new RaceMenuJslotNpcBuildResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [new Diagnostic(
                        ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                        DiagnosticSeverity.Error,
                        "provider disabled before prepublication")]));
        }
    }

    private sealed class DriftingPackageVerifyService(
        IPackageVerifyService inner,
        WorkspacePath driftTarget) : IPackageVerifyService
    {
        public async ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            PackageVerifyResult result = await inner.VerifyAsync(
                request,
                cancellationToken);
            if (result.Verified)
                File.AppendAllText(
                    driftTarget.Value,
                    "post-verification-drift",
                    Encoding.UTF8);
            return result;
        }
    }

    private sealed class CancellingPackageVerifyService : IPackageVerifyService
    {
        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(
                "The fixture package verifier was cancelled.",
                cancellationToken);
    }

    private sealed class CancellingPackageAdmission :
        INpcStaticBuildPackageAdmission
    {
        public ValueTask<NpcStaticBuildPackageLease> AdmitAsync(
            PackageVerificationArtifact verification,
            RaceMenuJslotNpcBuildCommandBinding binding,
            RaceMenuNpcExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(
                "The fixture package admission was cancelled.",
                cancellationToken);

        public ValueTask<NpcStaticBuildPackageLease> AdmitPreviewAsync(
            PackageVerificationArtifact verification,
            WorkflowArtifactBinding packageBinding,
            WorkflowNpcIdentity npc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "The build cancellation fixture does not admit previews.");

        public ValueTask<NpcStaticBuildPackageLease> AdmitFinishAsync(
            PackageVerificationArtifact verification,
            SkyrimNpcFinishCoreRequestDocument request,
            WorkflowNpcIdentity npc,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(
                "The fixture Finish package admission was cancelled.",
                cancellationToken);
    }

    private sealed class CancellingPreflightDocumentCodec(
        INpcBuildPreflightDocumentCodec inner) :
        INpcBuildPreflightDocumentCodec
    {
        public NpcBuildPreflightDocument Encode(
            NpcBuildPreflightArtifact artifact) => inner.Encode(artifact);

        public ValueTask<NpcBuildPreflightDocument> WriteNewAsync(
            NpcBuildPreflightDocument document,
            WorkspacePath output,
            CancellationToken cancellationToken) => inner.WriteNewAsync(
            document,
            output,
            cancellationToken);

        public ValueTask<NpcBuildPreflightDocument> ReadExactAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(
                "The reviewed preflight read was cancelled.",
                cancellationToken);
    }

    private sealed class OutputRootRequestLoader(
        IRaceMenuNpcExecutionRequestFileLoader inner,
        WorkspacePath outputRoot) : IRaceMenuNpcExecutionRequestFileLoader
    {
        public async ValueTask<RaceMenuNpcExecutionRequestFileLoadResult>
            LoadAsync(
                RaceMenuNpcExecutionRequestFileLoadRequest request,
                CancellationToken cancellationToken)
        {
            RaceMenuNpcExecutionRequestFileLoadResult result =
                await inner.LoadAsync(request, cancellationToken);
            return result.Request is not { } value
                ? result
                : result with
                {
                    Request = value with
                    {
                        Build = value.Build with { OutputRoot = outputRoot }
                    }
                };
        }
    }

    private sealed class UnusedPreflightService : INpcBuildPreflightService
    {
        public ValueTask<NpcBuildPreflightResult> CreateAsync(
            NpcBuildPreflightRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Build-mode fixture dispatched the preflight service.");

        public ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
            NpcBuildPreflightRequest request,
            NpcBuildPreflightReviewAuthority reviewed,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Adapter duplicated the build service's reviewed-preflight verification.");
    }

    private sealed class TerminalLockProbe(
        WorkspacePath semantic,
        WorkspacePath workflow)
    {
        public int Observations { get; private set; }

        public void Observe(string stage)
        {
            Require(File.Exists(semantic.Value) && File.Exists(workflow.Value),
                $"Terminal build lease could not find its artifacts during {stage}.");
            RequireWriteRefused(semantic, "primary package asset", stage);
            RequireWriteRefused(workflow, "workflow output", stage);
            Observations++;
        }

        public void RequireReleased(
            string semanticSha256,
            string workflowSha256)
        {
            Require(Observations == 2,
                "Terminal build lease was not observed at journal and writer boundaries.");
            byte[] semanticBefore = File.ReadAllBytes(semantic.Value);
            byte[] workflowBefore = File.ReadAllBytes(workflow.Value);
            string retainedSemanticSha256 = Convert.ToHexString(
                SHA256.HashData(semanticBefore));
            string retainedWorkflowSha256 = Convert.ToHexString(
                SHA256.HashData(workflowBefore));
            Require(retainedSemanticSha256 == semanticSha256 &&
                    retainedWorkflowSha256 == workflowSha256,
                "Terminal build artifacts did not retain their exact published bytes and hashes.");
            RequireWriteOpen(semantic, "primary package asset");
            RequireWriteOpen(workflow, "workflow output");
            Require(File.ReadAllBytes(semantic.Value).AsSpan()
                        .SequenceEqual(semanticBefore) &&
                    File.ReadAllBytes(workflow.Value).AsSpan()
                        .SequenceEqual(workflowBefore) &&
                    HashFile(semantic) == retainedSemanticSha256 &&
                    HashFile(workflow) == retainedWorkflowSha256,
                "Terminal build artifacts changed while proving their leases were released.");
        }

        private static void RequireWriteRefused(
            WorkspacePath path,
            string role,
            string stage)
        {
            try
            {
                using FileStream _ = File.Open(
                    path.Value,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                return;
            }
            throw new InvalidOperationException(
                $"The {role} was write-openable during {stage}.");
        }

        private static void RequireWriteOpen(
            WorkspacePath path,
            string role)
        {
            try
            {
                using FileStream _ = File.Open(
                    path.Value,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The {role} lease remained held after runner completion.",
                    exception);
            }
        }
    }

    private sealed class LockProbeTextWriter(
        TextWriter inner,
        Action probe) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            try
            {
                probe();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"{exception.Message} Pending protocol envelope: {value}", exception);
            }
            inner.WriteLine(value);
        }
    }

    private sealed class CapturingJournal(Action? probe = null) :
        ILocalOperationJournal
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
}
