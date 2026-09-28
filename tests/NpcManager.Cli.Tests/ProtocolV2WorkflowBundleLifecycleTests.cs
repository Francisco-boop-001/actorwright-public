using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2WorkflowBundleLifecycleTests
{
    private const string RequestDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static Task RunAsync()
    {
        string root = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            $"protocol-v2-workflow-bundles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath(root);
            var codec = new AgentWorkflowBundleCodec(
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath(@"F:\ExampleGame")),
                labRoot);
            var lifecycle = new AgentWorkflowBundleTransitionService(codec);
            AssertRegistryContract();

            WorkflowNpcIdentity npc = new(
                "ProbeNpc",
                null,
                null,
                null);
            WorkflowArtifactBinding intake = Artifact(
                root,
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                "npcmanager-reviewed-game-intake/2",
                "intake.json",
                "workspace preflight");
            AgentWorkflowBundleTransition workspace = lifecycle.WriteInitial(
                npc,
                RequestDigest,
                [intake],
                Output(root, "workflow-workspace.json"));
            AssertTransition(
                workspace,
                [WorkflowArtifactKinds.ReviewedWorkspaceIntake],
                AgentWorkflowPhase.Analyze,
                "preset inspect");
            _ = lifecycle.LoadForCommand(
                workspace.Document.Path,
                workspace.Document.Sha256,
                "preset inspect");

            WorkflowArtifactBinding preset = Artifact(
                root,
                WorkflowArtifactKinds.RaceMenuJslot,
                "application/json",
                "fixture.jslot",
                "preset inspect");
            AgentWorkflowBundleTransition inspected = lifecycle.Advance(
                workspace,
                npc,
                RequestDigest,
                [intake, preset],
                Output(root, "workflow-preset.json"));
            AssertTransition(
                inspected,
                [
                    WorkflowArtifactKinds.RaceMenuJslot,
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake
                ],
                AgentWorkflowPhase.Analyze,
                "npc create-from-jslot");

            WorkflowArtifactBinding preflight = Artifact(
                root,
                WorkflowArtifactKinds.NpcBuildPreflight,
                NpcBuildPreflightSchemas.Artifact,
                "preflight.json",
                "npc create-from-jslot");
            AgentWorkflowBundleTransition preflighted = lifecycle.Advance(
                inspected,
                npc with
                {
                    DisplayName = "Probe NPC",
                    Plugin = "ProbeNpc.esp"
                },
                RequestDigest,
                [intake, preset, preflight],
                Output(root, "workflow-preflight.json"));
            AssertTransition(
                preflighted,
                [
                    WorkflowArtifactKinds.NpcBuildPreflight,
                    WorkflowArtifactKinds.RaceMenuJslot,
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake
                ],
                AgentWorkflowPhase.Apply,
                "npc create-from-jslot");

            WorkflowArtifactBinding manifest = Artifact(
                root,
                WorkflowArtifactKinds.NpcFinishCoreManifest,
                SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                "finish-manifest.json",
                "npc finish apply");
            AgentWorkflowBundleTransition finishSeed = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "FinishProbeNpc",
                    "Finish Probe NPC",
                    "FinishProbe.esp",
                    "00000800"),
                RequestDigest,
                [manifest],
                Output(root, "workflow-finish-seed.json"));
            _ = lifecycle.LoadForCommand(
                finishSeed.Document.Path,
                finishSeed.Document.Sha256,
                "npc finish verify");

            WorkflowArtifactBinding verification = Artifact(
                root,
                WorkflowArtifactKinds.NpcFinishCoreVerification,
                SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                "finish-verification.json",
                "npc finish verify");
            WorkflowArtifactBinding archive = Artifact(
                root,
                WorkflowArtifactKinds.PackageArchive,
                "application/zip",
                "output.zip",
                "npc finish apply");
            AgentWorkflowBundleTransition runtimeGate = lifecycle.Advance(
                finishSeed,
                finishSeed.Document.Bundle.Npc,
                RequestDigest,
                [verification, archive],
                Output(root, "workflow-runtime.json"));
            AssertTransition(
                runtimeGate,
                [
                    WorkflowArtifactKinds.NpcFinishCoreVerification,
                    WorkflowArtifactKinds.PackageArchive
                ],
                AgentWorkflowPhase.RuntimeAcceptance,
                "runtime smoke verify");
            AssertRuntimeAuthorityRemainsRequired(runtimeGate.Evaluation);
            AssertWorkflowObservability(
                lifecycle,
                npc,
                intake,
                preset,
                root);
            AssertCriticalRefusals(lifecycle, workspace, intake, root);
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertWorkflowObservability(
        AgentWorkflowBundleTransitionService lifecycle,
        WorkflowNpcIdentity npc,
        WorkflowArtifactBinding intake,
        WorkflowArtifactBinding preset,
        string root)
    {
        WorkspacePath disabledOutput = Output(
            root,
            "workflow-observability-disabled.json");
        int filesBeforeDisabled = Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories).Count();
        AgentWorkflowBundleTransition disabled = lifecycle.WriteInitial(
            npc,
            RequestDigest,
            [intake],
            disabledOutput);
        int filesAfterDisabled = Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories).Count();
        Assert(filesAfterDisabled == filesBeforeDisabled + 1,
            "disabled workflow observation added a file beyond the requested bundle");

        using var listener = new CapturingWorkflowListener();
        WorkspacePath observedOutput = Output(
            root,
            "workflow-observability-enabled.json");
        int filesBeforeObserved = Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories).Count();
        AgentWorkflowBundleTransition observed = lifecycle.WriteInitial(
            npc,
            RequestDigest,
            [intake],
            observedOutput);
        Assert(Directory.EnumerateFiles(
                   root,
                   "*",
                   SearchOption.AllDirectories).Count() == filesBeforeObserved + 1,
            "enabled workflow observation added a file beyond the requested bundle");
        Assert(disabled.Document.Sha256 == observed.Document.Sha256 &&
               File.ReadAllBytes(disabledOutput.Value).SequenceEqual(
                   File.ReadAllBytes(observedOutput.Value)),
            "enabling workflow observation changed the persisted bundle bytes");
        AssertOneWorkflowEvent(
            listener,
            0,
            ActorwrightObservabilityEventSource.WorkflowOperationId.Initial,
            (int)observed.Document.Bundle.Phase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Succeeded,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.None);

        int beforeAdvance = listener.Events.Length;
        AgentWorkflowBundleTransition advanced = lifecycle.Advance(
            observed,
            npc,
            RequestDigest,
            [intake, preset],
            Output(root, "workflow-observability-advance.json"));
        AssertOneWorkflowEvent(
            listener,
            beforeAdvance,
            ActorwrightObservabilityEventSource.WorkflowOperationId.Advance,
            (int)advanced.Document.Bundle.Phase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Succeeded,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.None);

        int beforeLoad = listener.Events.Length;
        _ = lifecycle.LoadForCommand(
            advanced.Document.Path,
            advanced.Document.Sha256,
            "npc create-from-jslot");
        Assert(listener.Events.Length == beforeLoad,
            "a read-only workflow load emitted a transition outcome");

        string[] filesBeforeReviewedFailure = WorkflowFiles(root);
        int beforeReviewedRefusal = listener.Events.Length;
        Exception reviewedFailure = CaptureException(() => lifecycle.AdvanceReviewed(
            advanced,
            npc,
            RequestDigest,
            null!,
            [intake, preset],
            Output(root, "workflow-observability-reviewed-failure.json")));
        Assert(reviewedFailure is ArgumentNullException,
            "reviewed advance did not preserve its pre-write input failure");
        Assert(WorkflowFiles(root).SequenceEqual(
                   filesBeforeReviewedFailure,
                   StringComparer.OrdinalIgnoreCase),
            "reviewed advance failure created an output or temporary sibling");
        AssertOneWorkflowEvent(
            listener,
            beforeReviewedRefusal,
            ActorwrightObservabilityEventSource.WorkflowOperationId.AdvanceReviewed,
            ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Refused,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.Refusal);

        string[] filesBeforeReceiptFailure = WorkflowFiles(root);
        int beforeReceiptRefusal = listener.Events.Length;
        Exception receiptFailure = CaptureException(() =>
        {
            using AgentWorkflowBundleTransitionLease lease =
                lifecycle.AdvanceWithReviewedReceiptRetained(
                advanced,
                npc,
                RequestDigest,
                null!,
                [intake, preset],
                Output(root, "workflow-observability-receipt-failure.json"));
        });
        Assert(receiptFailure is ArgumentNullException,
            "reviewed-receipt advance did not preserve its pre-write input failure");
        Assert(WorkflowFiles(root).SequenceEqual(
                   filesBeforeReceiptFailure,
                   StringComparer.OrdinalIgnoreCase),
            "reviewed-receipt failure created an output or temporary sibling");
        AssertOneWorkflowEvent(
            listener,
            beforeReceiptRefusal,
            ActorwrightObservabilityEventSource.WorkflowOperationId.AdvanceReviewedReceipt,
            ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Refused,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.Refusal);

        WorkspacePath failedOutput = Output(
            root,
            "workflow-observability-unexpected-failure.json");
        int beforeUnexpectedFailure = listener.Events.Length;
        Exception unexpectedFailure = CaptureException(() => lifecycle.Advance(
            new AgentWorkflowBundleTransition(null!, null!),
            npc,
            RequestDigest,
            [intake, preset],
            failedOutput));
        Assert(unexpectedFailure is NullReferenceException &&
               !File.Exists(failedOutput.Value),
            "unexpected transition failure was replaced or published an output");
        AssertOneWorkflowEvent(
            listener,
            beforeUnexpectedFailure,
            ActorwrightObservabilityEventSource.WorkflowOperationId.Advance,
            ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Failed,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.Unexpected);

        WorkspacePath writeFailedOutput = Output(
            root,
            new string('w', 220));
        string[] filesBeforeWriteFailure = WorkflowFiles(root);
        int beforeWriteFailure = listener.Events.Length;
        Exception writeFailure = CaptureException(() => lifecycle.WriteInitial(
            npc,
            RequestDigest,
            [intake],
            writeFailedOutput));
        Assert(writeFailure is AgentWorkflowCodecException
               {
                   Code: "workflow-write-failed",
                   InnerException: IOException
               },
            "the long temporary filename did not produce a normalized write failure");
        Assert(WorkflowFiles(root).SequenceEqual(
                   filesBeforeWriteFailure,
                   StringComparer.OrdinalIgnoreCase),
            "workflow write failure created an output or temporary sibling");
        AssertOneWorkflowEvent(
            listener,
            beforeWriteFailure,
            ActorwrightObservabilityEventSource.WorkflowOperationId.Initial,
            ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Failed,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.IO);

        const string occupiedContents = "workflow-output-secret-sentinel";
        WorkspacePath occupied = Output(
            root,
            "workflow-observability-refused-secret-sentinel.json");
        File.WriteAllText(occupied.Value, occupiedContents);
        int beforeRefusal = listener.Events.Length;
        Exception refusal = CaptureException(() => lifecycle.WriteInitial(
            npc,
            RequestDigest,
            [intake],
            occupied));
        Assert(refusal is AgentWorkflowCodecException &&
               File.ReadAllText(occupied.Value) == occupiedContents,
            "workflow output refusal was replaced or changed the occupied file");
        CapturedWorkflowEvent refusedEvent = AssertOneWorkflowEvent(
            listener,
            beforeRefusal,
            ActorwrightObservabilityEventSource.WorkflowOperationId.Initial,
            ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Refused,
            ActorwrightObservabilityEventSource.WorkflowFailureKindId.Refusal);
        Assert(!refusedEvent.Payload.Any(value =>
                   value?.ToString()?.Contains(
                       "secret-sentinel",
                       StringComparison.OrdinalIgnoreCase) == true),
            "workflow event exposed output path or exception text");

        using var throwingListener = new ThrowingWorkflowListener();
        AgentWorkflowBundleTransition afterObserverFailure = lifecycle.WriteInitial(
            npc,
            RequestDigest,
            [intake],
            Output(root, "workflow-observability-listener-failure.json"));
        Assert(File.Exists(afterObserverFailure.Document.Path.Value) &&
               throwingListener.Attempts > 0,
            "observer failure changed a successful workflow transition");
        Exception refusalAfterObserverFailure = CaptureException(() =>
            lifecycle.WriteInitial(npc, RequestDigest, [intake], occupied));
        Assert(refusalAfterObserverFailure is AgentWorkflowCodecException &&
               ((AgentWorkflowCodecException)refusalAfterObserverFailure).Code ==
                   ((AgentWorkflowCodecException)refusal).Code &&
               refusalAfterObserverFailure.Message == refusal.Message &&
               File.ReadAllText(occupied.Value) == occupiedContents,
            "observer failure replaced the original workflow refusal");
    }

    private static string[] WorkflowFiles(string root) =>
        Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static CapturedWorkflowEvent AssertOneWorkflowEvent(
        CapturingWorkflowListener listener,
        int priorCount,
        ActorwrightObservabilityEventSource.WorkflowOperationId operation,
        int phase,
        ActorwrightObservabilityEventSource.WorkflowOutcomeId outcome,
        ActorwrightObservabilityEventSource.WorkflowFailureKindId failureKind)
    {
        CapturedWorkflowEvent[] events = listener.Events;
        Assert(events.Length == priorCount + 1,
            $"expected one workflow event, observed {events.Length - priorCount}");
        CapturedWorkflowEvent captured = events[^1];
        Assert(captured.EventId == 5 &&
               captured.PayloadNames.SequenceEqual(
                   ["operationKindId", "phaseId", "outcomeId", "failureKindId"],
                   StringComparer.Ordinal) &&
               captured.Payload.Length == 4 &&
               captured.Payload.All(value => value is int),
            "workflow event payload was not the closed four-integer contract");
        Assert((int)captured.Payload[0]! == (int)operation &&
               (int)captured.Payload[1]! == phase &&
               (int)captured.Payload[2]! == (int)outcome &&
               (int)captured.Payload[3]! == (int)failureKind,
            "workflow event carried an incorrect operation or result");
        return captured;
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a workflow transition failure.");
    }

    private static void AssertRegistryContract()
    {
        ImmutableDictionary<string, AgentCommandContract> contracts =
            AgentCommandRegistry.All
                .ToImmutableDictionary(item => item.Name, StringComparer.Ordinal);
        AssertOptionsPresent(
            contracts["workspace preflight"],
            "npc-editor-id",
            "workflow-output");
        foreach (string command in new[]
                 {
                     "preset inspect",
                     "npc create-from-jslot"
                 })
        {
            AssertOptionsPresent(
                contracts[command],
                "workflow-bundle",
                "workflow-bundle-sha256",
                "workflow-output");
        }
    }

    private static void AssertOptionsPresent(
        AgentCommandContract contract,
        params string[] expected)
    {
        foreach (string option in expected)
        {
            AgentOptionContract? found = contract.Options.FirstOrDefault(item =>
                string.Equals(item.CliName, option, StringComparison.Ordinal));
            Assert(found is not null,
                $"{contract.Name} does not discover --{option}");
        }
    }

    private static void AssertTransition(
        AgentWorkflowBundleTransition transition,
        ImmutableArray<string> expectedKinds,
        AgentWorkflowPhase expectedPhase,
        string? expectedNextCommand)
    {
        AgentWorkflowBundleDocument document = transition.Document;
        Assert(File.Exists(document.Path.Value),
            "workflow bundle was not persisted");
        Assert(document.Size > 0 && IsUpperSha256(document.Sha256),
            "workflow bundle physical binding is invalid");
        Assert(document.Bundle.Phase == expectedPhase &&
               transition.Evaluation.Phase == expectedPhase,
            "workflow phase was not evaluator-derived");
        Assert(document.Bundle.Artifacts.Select(item => item.Kind)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(expectedKinds.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "workflow artifact signature changed");
        Assert(document.Bundle.Authority.Length ==
               transition.Evaluation.Authority.Length &&
               document.Bundle.Authority.Zip(
                       transition.Evaluation.Authority)
                   .All(pair => pair.First.Kind == pair.Second.Kind &&
                       pair.First.State == pair.Second.State &&
                       string.Equals(
                           pair.First.Reason,
                           pair.Second.Reason,
                           StringComparison.Ordinal) &&
                       pair.First.ArtifactHashes.SequenceEqual(
                           pair.Second.ArtifactHashes,
                           StringComparer.Ordinal)),
            "persisted workflow authority differs from evaluation");
        Assert(document.Bundle.NextActions.Length ==
               transition.Evaluation.NextActions.Length &&
               document.Bundle.NextActions.Zip(
                       transition.Evaluation.NextActions)
                   .All(pair => string.Equals(
                           pair.First.Command,
                           pair.Second.Command,
                           StringComparison.Ordinal) &&
                       pair.First.MissingPrerequisites.SequenceEqual(
                           pair.Second.MissingPrerequisites,
                           StringComparer.Ordinal)),
            "persisted workflow next actions differ from evaluation");
        Assert(expectedNextCommand is null
                ? document.Bundle.NextActions.IsEmpty
                : document.Bundle.NextActions is [var action] &&
                  string.Equals(
                      action.Command,
                      expectedNextCommand,
                      StringComparison.Ordinal),
            "workflow next action is not exact");
    }

    private static void AssertRuntimeAuthorityRemainsRequired(
        WorkflowEvaluation evaluation)
    {
        foreach (AgentAuthorityKind kind in new[]
                 {
                     AgentAuthorityKind.HumanVisualAcceptance,
                     AgentAuthorityKind.GameRuntimeVerification,
                     AgentAuthorityKind.PromotionApproval
                 })
        {
            Assert(evaluation.Authority.Single(item => item.Kind == kind).State ==
                   AgentAuthorityState.Required,
                $"{kind} was overclaimed by static workflow completion");
        }
    }

    private static void AssertCriticalRefusals(
        AgentWorkflowBundleTransitionService lifecycle,
        AgentWorkflowBundleTransition workspace,
        WorkflowArtifactBinding intake,
        string root)
    {
        AssertCodecFailure(
            () => lifecycle.LoadForCommand(
                workspace.Document.Path,
                new string('F', 64),
                "preset inspect"),
            "stale workflow hash was admitted");
        AssertCodecFailure(
            () => lifecycle.LoadForCommand(
                workspace.Document.Path,
                workspace.Document.Sha256,
                "npc finish verify"),
            "wrong workflow transition was admitted");

        WorkspacePath occupied = Output(root, "occupied.json");
        File.WriteAllText(occupied.Value, "occupied");
        AssertCodecFailure(
            () => lifecycle.WriteInitial(
                workspace.Document.Bundle.Npc,
                RequestDigest,
                [intake],
                occupied),
            "occupied workflow output was overwritten");
        Assert(File.ReadAllText(occupied.Value) == "occupied",
            "occupied workflow output changed after refusal");
    }

    private static void AssertCodecFailure(Action action, string failure)
    {
        try
        {
            action();
        }
        catch (AgentWorkflowCodecException)
        {
            return;
        }
        throw new InvalidOperationException(failure);
    }

    private static WorkflowArtifactBinding Artifact(
        string root,
        string kind,
        string schema,
        string name,
        string producer)
    {
        string path = Path.Combine(root, name);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
            $"artifact:{kind}:{name}");
        File.WriteAllBytes(path, bytes);
        return new WorkflowArtifactBinding(
            kind,
            schema,
            new WorkspacePath(path),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            producer,
            RequestDigest,
            []);
    }

    private static WorkspacePath Output(string root, string name) =>
        new(Path.Combine(root, name));

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record CapturedWorkflowEvent(
        int EventId,
        string[] PayloadNames,
        object?[] Payload);

    private sealed class CapturingWorkflowListener : EventListener
    {
        private const string ProviderName = "Actorwright-Observability";
        private readonly ConcurrentQueue<CapturedWorkflowEvent> events = new();

        public CapturedWorkflowEvent[] Events => events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(
                    eventSource.Name,
                    ProviderName,
                    StringComparison.Ordinal))
                EnableEvents(
                    eventSource,
                    EventLevel.Informational,
                    ActorwrightObservabilityEventSource.Keywords.Workflow);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != ProviderName ||
                eventData.EventId != 5)
                return;
            events.Enqueue(new CapturedWorkflowEvent(
                eventData.EventId,
                eventData.PayloadNames?.ToArray() ?? [],
                eventData.Payload?.ToArray() ?? []));
        }
    }

    private sealed class ThrowingWorkflowListener : EventListener
    {
        private const string ProviderName = "Actorwright-Observability";
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(
                    eventSource.Name,
                    ProviderName,
                    StringComparison.Ordinal))
                EnableEvents(
                    eventSource,
                    EventLevel.Informational,
                    ActorwrightObservabilityEventSource.Keywords.Workflow);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != ProviderName ||
                eventData.EventId != 5)
                return;
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException(
                "workflow observer failure sentinel");
        }
    }
}
