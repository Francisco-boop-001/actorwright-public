using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2FinishVerifyTests
{
    private const string RequestDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static async Task RunAsync()
    {
        using var fixture = new FinishExternalSmpFixture();
        AssertRegistryAndSchema();
        AssertCaseInsensitiveManifestPluginBinding(fixture);

        WorkspacePath root = fixture.Workspace;
        WorkspacePath evidenceRoot = new(Path.Combine(fixture.Root, "evidence"));
        Directory.CreateDirectory(evidenceRoot.Value);
        var policy = new KOnlyWorkspacePolicy(
            root, new WorkspacePath(root.Value + "-protected-live"));
        var store = new SkyrimNpcFinishCoreVerificationArtifactStore(policy, root);
        var workflowLifecycle = new AgentWorkflowBundleTransitionService(
            new AgentWorkflowBundleCodec(policy, root));
        AgentWorkflowBundleTransition workflowSeed =
            workflowLifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "ExternalFixtureNpc",
                    "External Fixture NPC",
                    fixture.OutputPlugin.Value,
                    "00000800"),
                RequestDigest,
                [new WorkflowArtifactBinding(
                    WorkflowArtifactKinds.NpcFinishCoreManifest,
                    SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
                    fixture.ManifestPath,
                    new FileInfo(fixture.ManifestPath.Value).Length,
                    fixture.ManifestHash.Value.ToUpperInvariant(),
                    "npc finish apply",
                    RequestDigest,
                    [])],
                new WorkspacePath(Path.Combine(
                    fixture.Root, "workflow-seed.json")));

        await AssertVerifiedExternalProjection(
            fixture, evidenceRoot, store, workflowLifecycle, workflowSeed);
        await AssertContextFreeRefusal(
            fixture, evidenceRoot, store, workflowLifecycle, workflowSeed);
        foreach (string diagnosticCode in new[]
        {
            ExternalHeadPartDiagnosticCodes.ProviderMissing,
            ExternalHeadPartDiagnosticCodes.ProviderDisabled,
            ExternalHeadPartDiagnosticCodes.AssetDrift
        })
        {
            await AssertSuppliedContextFailure(
                fixture,
                evidenceRoot,
                store,
                workflowLifecycle,
                workflowSeed,
                diagnosticCode);
        }
        await AssertRunnerInvocation(
            fixture, evidenceRoot, store, workflowLifecycle, workflowSeed, includeContext: true);
        await AssertRunnerInvocation(
            fixture, evidenceRoot, store, workflowLifecycle, workflowSeed, includeContext: false);
        await AssertRunnerIncompleteContext(
            fixture, evidenceRoot, store, workflowLifecycle, workflowSeed);
    }

    private static async Task AssertVerifiedExternalProjection(
        FinishExternalSmpFixture fixture,
        WorkspacePath evidenceRoot,
        SkyrimNpcFinishCoreVerificationArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed)
    {
        var service = new ProtocolFinishVerifyService(
            fixture.ContextFreeVerificationResult,
            fixture.ContextfulVerificationResult);
        var adapter = new ProtocolV2FinishVerifyAdapter(
            fixture.Workspace, service, store, workflowLifecycle);
        WorkspacePath output = new(Path.Combine(
            evidenceRoot.Value, "verified.json"));
        WorkspacePath workflowOutput = new(Path.Combine(
            fixture.Root, "workflow-verified.json"));
        ProtocolCommandResult result = await adapter.RunAsync(
            Command(fixture, workflowSeed, output, workflowOutput, includeContext: true),
            RequestDigest,
            CancellationToken.None);

        Assert(result.Diagnostics.IsEmpty &&
               result.Effects.SequenceEqual([
                   ProtocolEffect.Create(
                       AgentEffectKind.ReadWorkspace,
                       ApplicationEffectStatus.Completed,
                       ApplicationEffectScope.Workspace),
                   ProtocolEffect.Create(
                       AgentEffectKind.WriteNewArtifact,
                       ApplicationEffectStatus.Completed,
                       ApplicationEffectScope.KLocalOutput)
               ]) &&
               result.Artifacts.Any(item =>
                   item.Kind == WorkflowArtifactKinds.NpcFinishCoreVerification) &&
               File.Exists(output.Value) &&
               service.ContextVerifyCalls == 1 &&
               service.VerifyCalls == 0,
            "Verified external Protocol Finish Verify did not complete its exact read/write effects.");

        JsonElement response = result.Result ??
            throw new InvalidOperationException("Verified Protocol result was absent.");
        JsonElement verification = response.GetProperty("verification");
        JsonElement external = verification
            .GetProperty("externalHeadParts")
            .GetProperty("verification");
        JsonElement provider = external.GetProperty("providerObservations")[0];
        Assert(response.GetProperty("verified").GetBoolean() &&
               response.GetProperty("status").GetString() == "staticPassRuntimeRequired" &&
               external.GetProperty("packageIntegrity").GetBoolean() &&
               external.GetProperty("descriptorClosureValid").GetBoolean() &&
               external.GetProperty("historicalSnapshotValid").GetBoolean() &&
               external.GetProperty("currentInstallDependencyState").GetString() ==
                   "verified" &&
               external.GetProperty("installReady").GetBoolean() &&
               external.GetProperty("installDependencyAuthority").GetBoolean() &&
               !external.GetProperty("runtimeAuthority").GetBoolean() &&
               !external.GetProperty("visualAuthority").GetBoolean() &&
               provider.GetProperty("providerPlugin").GetString() ==
                   fixture.Descriptor.Provider.Plugin.Value &&
               provider.GetProperty("currentSha256").GetString() ==
                   fixture.Descriptor.Provider.PluginSha256.Value &&
               provider.GetProperty("enabled").GetBoolean() &&
               external.GetProperty("missingPrerequisites").GetArrayLength() == 0 &&
               !response.ToString().Contains(
                   fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "Verified external Protocol result did not expose package, closure, historical, current, provider, and authority facts: " +
            response);
        Assert(result.Authority.Single(item => item.Kind ==
                   AgentAuthorityKind.IndependentStaticVerification).State ==
                   AgentAuthorityState.Established &&
               result.Authority.Single(item => item.Kind ==
                   AgentAuthorityKind.GameRuntimeVerification).State ==
                   AgentAuthorityState.Required &&
               result.Authority.Single(item => item.Kind ==
                   AgentAuthorityKind.HumanVisualAcceptance).State ==
                   AgentAuthorityState.Required,
            "Verified external Protocol projection overstated or omitted runtime/visual gates.");
    }

    private static async Task AssertContextFreeRefusal(
        FinishExternalSmpFixture fixture,
        WorkspacePath evidenceRoot,
        SkyrimNpcFinishCoreVerificationArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed)
    {
        var service = new ProtocolFinishVerifyService(
            fixture.ContextFreeVerificationResult,
            fixture.ContextfulVerificationResult);
        var adapter = new ProtocolV2FinishVerifyAdapter(
            fixture.Workspace, service, store, workflowLifecycle);
        WorkspacePath output = new(Path.Combine(
            evidenceRoot.Value, "context-free-refused.json"));
        WorkspacePath workflowOutput = new(Path.Combine(
            fixture.Root, "workflow-context-free-refused.json"));
        ProtocolCommandResult result = await adapter.RunAsync(
            Command(fixture, workflowSeed, output, workflowOutput, includeContext: false),
            RequestDigest,
            CancellationToken.None);
        JsonElement response = result.Result ??
            throw new InvalidOperationException("Context-free Protocol result was absent.");
        JsonElement external = response.GetProperty("verification")
            .GetProperty("externalHeadParts")
            .GetProperty("verification");
        Assert(!response.GetProperty("verified").GetBoolean() &&
               result.Artifacts.IsEmpty &&
               !File.Exists(output.Value) &&
               !File.Exists(workflowOutput.Value) &&
               service.VerifyCalls == 1 &&
               service.ContextVerifyCalls == 0 &&
               result.Effects.SequenceEqual([ReadCompletedEffect()]) &&
               external.GetProperty("currentInstallDependencyState").GetString() ==
                   "declared-unverified" &&
               result.Diagnostics.Select(item => item.Code).SequenceEqual([
                   ProtocolV2DiagnosticCodes.FinishVerificationFailed]) &&
               result.Diagnostics[0].Message.Contains(
                   ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                   StringComparison.Ordinal) &&
               result.NextActions is [var action] &&
               action.RequiredBindings.Select(item => item.Option).SequenceEqual([
                   "--manifest", "--manifest-sha256"]) &&
               action.MissingPrerequisites.SequenceEqual(["--verification-output"]) &&
               action.RequiredBindings[0].Value == fixture.ManifestPath.Value &&
               string.Equals(
                   action.RequiredBindings[1].Value,
                   fixture.ManifestHash.Value.ToUpperInvariant(),
                   StringComparison.Ordinal),
            "Context-free external Protocol Verify did not refuse with the absent-context recovery action: " +
            response);
    }

    private static async Task AssertSuppliedContextFailure(
        FinishExternalSmpFixture fixture,
        WorkspacePath evidenceRoot,
        SkyrimNpcFinishCoreVerificationArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed,
        string diagnosticCode)
    {
        var contextfulFailure = new SkyrimNpcFinishCoreVerificationResult(
            false,
            null,
            [new Diagnostic(
                diagnosticCode,
                DiagnosticSeverity.Error,
                "The supplied external provider state failed strict verification.")
            {
                Recovery = new DiagnosticRecovery(RecoveryAction.CorrectInput, "plugins", null,
                    "Restore the exact hash-bound copied provider before re-verifying with fresh outputs.", false)
                    { AlternativeCommand = "npc finish verify" }
            }]);
        var service = new ProtocolFinishVerifyService(
            new SkyrimNpcFinishCoreVerificationResult(
                false,
                null,
                [new Diagnostic(
                    "context-free-dispatch-sentinel",
                    DiagnosticSeverity.Error,
                    "The context-free verifier must not handle a supplied context.")]),
            contextfulFailure);
        var adapter = new ProtocolV2FinishVerifyAdapter(
            fixture.Workspace, service, store, workflowLifecycle);
        WorkspacePath output = new(Path.Combine(
            evidenceRoot.Value, "supplied-" + diagnosticCode + ".json"));
        WorkspacePath workflowOutput = new(Path.Combine(
            fixture.Root, "workflow-supplied-" + diagnosticCode + ".json"));
        ProtocolCommandResult result = await adapter.RunAsync(
            Command(fixture, workflowSeed, output, workflowOutput, includeContext: true),
            RequestDigest,
            CancellationToken.None);
        JsonElement response = result.Result ??
            throw new InvalidOperationException("Supplied-context Protocol result was absent.");
        JsonElement external = response.GetProperty("verification")
            .GetProperty("externalHeadParts")
            .GetProperty("verification");
        JsonElement provider = external.GetProperty("providerObservations")[0];
        bool hasTypedFailure = result.Diagnostics.Any(item =>
            item.Message.Contains(diagnosticCode, StringComparison.Ordinal));
        bool hasProviderObservation =
            provider.GetProperty("providerPlugin").GetString() ==
                fixture.Descriptor.Provider.Plugin.Value &&
            !provider.TryGetProperty("currentSha256", out _) &&
            !provider.TryGetProperty("enabled", out _);
        bool hasPortableAction = result.NextActions is [var action] &&
            action.Command == "npc finish verify" &&
            action.RequiredBindings.Select(item => item.Option).SequenceEqual([
                "--manifest", "--manifest-sha256"]) &&
            action.MissingPrerequisites.SequenceEqual(["--verification-output"]) &&
            action.RequiredBindings[0].Value == fixture.ManifestPath.Value &&
            string.Equals(
                action.RequiredBindings[1].Value,
                fixture.ManifestHash.Value.ToUpperInvariant(),
                StringComparison.Ordinal) &&
            action.Reason.Contains(diagnosticCode, StringComparison.Ordinal);
        string[] expectedArtifactCodes = diagnosticCode ==
            ExternalHeadPartDiagnosticCodes.ProviderDisabled
                ? [diagnosticCode, ExternalHeadPartDiagnosticCodes.PrecheckUnavailable]
                : [diagnosticCode];
        Assert(!response.GetProperty("verified").GetBoolean() &&
               response.GetProperty("status").GetString() == "refused" &&
               result.Effects.SequenceEqual([ReadCompletedEffect()]) &&
               !external.GetProperty("packageIntegrity").GetBoolean() &&
               !external.GetProperty("descriptorClosureValid").GetBoolean() &&
               external.GetProperty("currentInstallDependencyState").GetString() ==
                   "declared-unverified" &&
               !external.GetProperty("installReady").GetBoolean() &&
               !external.GetProperty("installDependencyAuthority").GetBoolean() &&
               !external.GetProperty("runtimeAuthority").GetBoolean() &&
               !external.GetProperty("visualAuthority").GetBoolean() &&
               external.GetProperty("missingPrerequisites").EnumerateArray().Any(item =>
                    expectedArtifactCodes.Contains(
                        item.GetProperty("diagnosticCode").GetString())) &&
               result.Diagnostics.Select(item => item.Code).SequenceEqual([
                   ProtocolV2DiagnosticCodes.FinishVerificationFailed]) &&
               result.Diagnostics[0].Recovery == contextfulFailure.Diagnostics[0].Recovery &&
               hasTypedFailure && hasProviderObservation && hasPortableAction &&
               service.ContextVerifyCalls == 1 &&
               service.VerifyCalls == 0 &&
               service.ContextVerifyContext is { } context &&
               context.DataRoot == fixture.DataRoot &&
               context.EnabledPluginOrder.SequenceEqual(fixture.EnabledPluginOrder) &&
               context.TargetRace == fixture.TargetRace &&
               result.Artifacts.IsEmpty &&
               !File.Exists(output.Value) &&
               !File.Exists(workflowOutput.Value) &&
               result.Authority.Single(item => item.Kind ==
                   AgentAuthorityKind.IndependentStaticVerification).State ==
                   AgentAuthorityState.Blocked,
            "Supplied external failure was not projected as a truthful typed refusal for " +
            diagnosticCode + ": " + response);
    }

    private static ParsedCommand Command(
        FinishExternalSmpFixture fixture,
        AgentWorkflowBundleTransition workflowSeed,
        WorkspacePath output,
        WorkspacePath workflowOutput,
        bool includeContext,
        bool protocolV2 = false)
    {
        var args = new List<string>
        {
            "npc", "finish", "verify",
            "--manifest", fixture.ManifestPath.Value,
            "--manifest-sha256", fixture.ManifestHash.Value.ToUpperInvariant(),
            "--verification-output", output.Value,
            "--workflow-bundle", workflowSeed.Document.Path.Value,
            "--workflow-bundle-sha256", workflowSeed.Document.Sha256,
            "--workflow-output", workflowOutput.Value
        };
        if (includeContext)
        {
            args.Add("--data-root");
            args.Add(fixture.DataRoot.Value);
            args.Add("--plugins");
            args.Add(fixture.EnabledPlugins);
        }
        if (protocolV2)
        {
            args.Add("--protocol");
            args.Add("2");
            args.Add("--json");
        }
        return CommandLine.Parse(args);
    }

    private static async Task AssertRunnerInvocation(
        FinishExternalSmpFixture fixture,
        WorkspacePath evidenceRoot,
        SkyrimNpcFinishCoreVerificationArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed,
        bool includeContext)
    {
        var service = new ProtocolFinishVerifyService(
            fixture.ContextFreeVerificationResult,
            fixture.ContextfulVerificationResult);
        var adapter = new ProtocolV2FinishVerifyAdapter(
            fixture.Workspace, service, store, workflowLifecycle);
        using var output = new StringWriter();
        var journal = new NoopJournal();
        var runner = new ProtocolV2Runner(
            output,
            _ => journal,
            [adapter],
            AgentCommandRegistry.All);
        WorkspacePath verificationOutput = new(Path.Combine(
            evidenceRoot.Value, includeContext ? "runner-verified.json" : "runner-context-free-refused.json"));
        WorkspacePath workflowOutput = new(Path.Combine(
            fixture.Root, includeContext ? "runner-workflow-verified.json" : "runner-workflow-context-free-refused.json"));
        CommandExitCode exit = await runner.RunAsync(
            Command(
                fixture,
                workflowSeed,
                verificationOutput,
                workflowOutput,
                includeContext,
                protocolV2: true),
            CancellationToken.None);
        using JsonDocument envelope = JsonDocument.Parse(output.ToString());
        JsonElement diagnostics = envelope.RootElement.GetProperty("diagnostics");
        if (includeContext)
        {
            Assert(exit == CommandExitCode.Success &&
                   diagnostics.GetArrayLength() == 0 &&
                   envelope.RootElement.GetProperty("result").GetProperty("schemaVersion").GetString() == "1" &&
                   envelope.RootElement.GetProperty("result").GetProperty("verified").GetBoolean() &&
                   File.Exists(verificationOutput.Value) && File.Exists(workflowOutput.Value) &&
                   service.ContextVerifyCalls == 1 && service.VerifyCalls == 0,
                "ProtocolV2Runner did not execute Finish Verify and persist verification: " + output);
            byte[] persisted = await File.ReadAllBytesAsync(verificationOutput.Value);
            output.GetStringBuilder().Clear();
            CommandExitCode replay = await runner.RunAsync(
                Command(fixture, workflowSeed, verificationOutput, workflowOutput,
                    includeContext: true, protocolV2: true), CancellationToken.None);
            byte[] replayBytes = await File.ReadAllBytesAsync(verificationOutput.Value);
            Assert(replay != CommandExitCode.Success && service.ContextVerifyCalls == 1 &&
                   persisted.SequenceEqual(replayBytes),
                "Finish Verify replay overwrote a fresh-output artifact or reached verification.");
        }
        else
        {
            Assert(exit == CommandExitCode.ValidationFailure &&
                   diagnostics.GetArrayLength() == 1 &&
                   diagnostics[0].GetProperty("code").GetString() ==
                       ProtocolV2DiagnosticCodes.FinishVerificationFailed &&
                   !File.Exists(verificationOutput.Value) && !File.Exists(workflowOutput.Value) &&
                   service.VerifyCalls == 1 && service.ContextVerifyCalls == 0,
                "ProtocolV2Runner did not preserve the real context-free verification refusal: " + output);
        }
    }

    private static async Task AssertRunnerIncompleteContext(
        FinishExternalSmpFixture fixture,
        WorkspacePath evidenceRoot,
        SkyrimNpcFinishCoreVerificationArtifactStore store,
        AgentWorkflowBundleTransitionService workflowLifecycle,
        AgentWorkflowBundleTransition workflowSeed)
    {
        var service = new ProtocolFinishVerifyService(
            fixture.ContextFreeVerificationResult, fixture.ContextfulVerificationResult);
        var adapter = new ProtocolV2FinishVerifyAdapter(fixture.Workspace, service, store, workflowLifecycle);
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(output, _ => new NoopJournal(), [adapter], AgentCommandRegistry.All);
        WorkspacePath verificationOutput = new(Path.Combine(evidenceRoot.Value, "partial-context.json"));
        WorkspacePath workflowOutput = new(Path.Combine(fixture.Root, "partial-context-workflow.json"));
        ParsedCommand command = Command(fixture, workflowSeed, verificationOutput, workflowOutput,
            includeContext: true, protocolV2: true);
        command = command with { Options = command.Options.Remove("plugins") };
        CommandExitCode exit = await runner.RunAsync(command, CancellationToken.None);
        Assert(exit == CommandExitCode.UsageError && service.VerifyCalls == 0 &&
               service.ContextVerifyCalls == 0 && !File.Exists(verificationOutput.Value) &&
               !File.Exists(workflowOutput.Value) &&
               !output.ToString().Contains(ProtocolV2DiagnosticCodes.ProtocolCommandLegacy, StringComparison.Ordinal),
            "Partial install context was not refused before Finish Verify service dispatch: " + output);
    }

    private static void AssertRegistryAndSchema()
    {
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("npc finish verify");
        Assert(contract.Readiness == ProtocolReadiness.V2,
            "npc finish verify must be protocol-2 ready.");
        Assert(contract.ContractStatus == AgentContractStatus.Complete &&
               contract.Options.Select(item => item.CliName).SequenceEqual([
                   "manifest", "manifest-sha256", "verification-output", "workflow-bundle",
                   "workflow-bundle-sha256", "workflow-output", "data-root", "plugins"
               ]) && contract.ResultSchemaIds.SequenceEqual([
                   "urn:actorwright:protocol-v2:finish-verify-result:v1"]) &&
               contract.OptionRelationships is [var pair] &&
               pair.Kind == AgentOptionRelationshipKind.RequiresTogether &&
               pair.Options.SequenceEqual(["data-root", "plugins"]) &&
               AgentCommandRegistry.Validate().IsEmpty,
            "Finish Verify V2 metadata is not complete and exact.");
        AgentCommandContract legacy = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc finish verify");
        Assert(legacy.Readiness == ProtocolReadiness.Legacy &&
               legacy.Options.Select(item => item.CliName).SequenceEqual([
                   "manifest", "manifest-sha256", "data-root", "plugins"]) &&
               legacy.ResultSchemaIds.IsEmpty,
            "Finish Verify four-option V1 contract changed.");
        AgentCommandContract runtime =
            AgentCommandRegistry.GetRequired("runtime smoke verify");
        Assert(runtime.Readiness == ProtocolReadiness.Legacy &&
               runtime.Options.Select(item => item.CliName).SequenceEqual([
                   "edition", "game", "runtime-report", "package-acceptance"
               ]),
            "The blocked runtime target lacks exact Legacy option metadata.");
        string[] deliberatelyLegacy = [
            "npc patch", "package verify", "package archive"
        ];
        Assert(deliberatelyLegacy.All(name =>
                   AgentCommandRegistry.GetRequired(name) is
                   { Readiness: ProtocolReadiness.Legacy } contract &&
                   contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Authority.Length ==
                       Enum.GetValues<AgentAuthorityKind>().Length),
            "A Task 6 command lost its complete legacy-discovery contract.");
        JsonElement exported = ProtocolV2SchemaService.RenderInline("npc finish verify");
        Assert(exported.GetProperty("contract").GetProperty("readiness")
                   .GetString() == "v2" &&
               exported.GetProperty("resultSchemas").GetArrayLength() == 1,
            "Finish Verify V2 schema export is not exact.");
    }

    private static void AssertCaseInsensitiveManifestPluginBinding(
        FinishExternalSmpFixture fixture)
    {
        SkyrimNpcFinishCoreManifest manifest = fixture.Manifest with
        {
            BaseNpc = new FormReference(
                new PluginName(fixture.OutputPlugin.Value.ToLowerInvariant()),
                new FormId(0x800))
        };
        Assert(
            ExternalHeadPartInstallContextBinder.TryReadTargetRace(
                manifest,
                out FormReference targetRace,
                out string error) &&
            targetRace == fixture.TargetRace,
            "External target-race derivation rejected a case-only base-plugin identity change: " +
            error);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static ProtocolEffect ReadCompletedEffect() =>
        ProtocolEffect.Create(
            AgentEffectKind.ReadWorkspace,
            ApplicationEffectStatus.Completed,
            ApplicationEffectScope.Workspace);

    private sealed class NoopJournal : ILocalOperationJournal
    {
        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OperationJournalAppendResult(true, null, null));
    }

    private sealed class ProtocolFinishVerifyService(
        SkyrimNpcFinishCoreVerificationResult verification,
        SkyrimNpcFinishCoreVerificationResult? contextVerification = null) :
        ISkyrimNpcFinishCoreInstallContextService
    {
        private readonly SkyrimNpcFinishCoreVerificationResult contextResult =
            contextVerification ?? verification;

        public int VerifyCalls { get; private set; }
        public int ContextVerifyCalls { get; private set; }
        public ExternalHeadPartInstallVerificationContext? ContextVerifyContext {
            get;
            private set;
        }

        public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(verification);
        }

        public ValueTask<SkyrimNpcFinishCoreVerificationResult>
            VerifyWithInstallContextAsync(
                WorkspacePath manifestPath,
                Sha256Hash manifestSha256,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContextVerifyCalls++;
            ContextVerifyContext = installContext;
            return ValueTask.FromResult(contextResult);
        }

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreProposalResult>
            AnalyzeWithInstallContextAsync(
                SkyrimNpcFinishCoreRequest request,
                Sha256Hash requestSha256,
                WorkspacePath proposalPath,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreApplyResult>
            ApplyWithInstallContextAsync(
                SkyrimNpcFinishCoreRequest request,
                Sha256Hash requestSha256,
                SkyrimNpcFinishCoreProposal proposal,
                Sha256Hash proposalSha256,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
