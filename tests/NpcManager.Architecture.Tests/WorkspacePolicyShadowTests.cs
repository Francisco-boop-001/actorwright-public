using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestWorkspacePolicyShadowObservability()
    {
        string privatePath = $"workspace-policy-shadow-secret-{Guid.NewGuid():N}";
        var labRoot = new WorkspacePath($@"K:\{privatePath}\lab");
        var allowedWorkspaceRoot = new WorkspacePath(Path.Combine(labRoot.Value, "workspace"));
        var refusedWorkspaceRoot = new WorkspacePath($@"K:\{privatePath}\outside-workspace");
        var protectedLiveRoot = new WorkspacePath(@"F:\ExampleGame");
        var outputRoot = new WorkspacePath(Path.Combine(allowedWorkspaceRoot.Value, "output"));
        var outsideRoot = new WorkspacePath($@"K:\{privatePath}\outside-output");
        var alternateStreamRoot = new WorkspacePath(Path.Combine(allowedWorkspaceRoot.Value, "output:stream"));
        var protectedTarget = new WorkspacePath(@"F:\ExampleGame\Data");
        var shadowEvaluationCount = 0;
        var policy = new KOnlyWorkspacePolicy(labRoot, protectedLiveRoot, request =>
        {
            shadowEvaluationCount++;
            return WorkspacePolicyShadow.Evaluate(request);
        });

        ImmutableArray<ImmutableArray<Diagnostic>> EvaluateCases() =>
        [
            policy.Evaluate(allowedWorkspaceRoot, outputRoot),
            policy.Evaluate(refusedWorkspaceRoot, outsideRoot),
            policy.EvaluateReadRoot(allowedWorkspaceRoot, outputRoot),
            policy.EvaluateReadRoot(refusedWorkspaceRoot, outsideRoot),
            policy.Evaluate(allowedWorkspaceRoot, alternateStreamRoot),
            policy.Evaluate(allowedWorkspaceRoot, protectedTarget),
            policy.EvaluateReadRoot(allowedWorkspaceRoot, alternateStreamRoot),
            policy.EvaluateReadRoot(allowedWorkspaceRoot, protectedTarget)
        ];

        ImmutableArray<ImmutableArray<Diagnostic>> baseline;
        using (var disabledListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.Dispatch))
        {
            baseline = EvaluateCases();
            Assert(disabledListener.Events.Length == 0,
                "disabled policy-shadow observation emitted an event");
            Assert(shadowEvaluationCount == 0,
                "disabled policy-shadow observation evaluated the shadow model");
            Assert(!Directory.Exists(outputRoot.Value),
                "the planned output root must remain absent in this fixture");
        }

        Assert(baseline[0].All(item => item.Severity != DiagnosticSeverity.Error) &&
               baseline[2].All(item => item.Severity != DiagnosticSeverity.Error),
            "allowed output/read roots acquired refusal diagnostics");
        AssertDiagnosticCodes(baseline[1],
            ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
            ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace);
        AssertDiagnosticCodes(baseline[3],
            ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
            "data-root-outside-workspace");
        AssertDiagnosticCodesIncludeInOrder(baseline[4],
            ProtocolV2DiagnosticCodes.AlternateDataStreamRefused);
        AssertDiagnosticCodesIncludeInOrder(baseline[5],
            ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace,
            ProtocolV2DiagnosticCodes.ProtectedRootRefused);
        AssertDiagnosticCodesIncludeInOrder(baseline[6],
            ProtocolV2DiagnosticCodes.AlternateDataStreamRefused);
        AssertDiagnosticCodesIncludeInOrder(baseline[7],
            "data-root-outside-workspace",
            ProtocolV2DiagnosticCodes.ProtectedRootRefused);

        using (var enabledListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.Policy |
                   ActorwrightObservabilityEventSource.Keywords.PolicyShadow))
        {
            ImmutableArray<ImmutableArray<Diagnostic>> observed = EvaluateCases();
            Assert(baseline.Zip(observed).All(pair => pair.First.SequenceEqual(pair.Second)),
                "enabling shadow observation changed authority diagnostics or order");
            Assert(shadowEvaluationCount == 8,
                "enabled shadow observation did not evaluate once per policy result");

            PolicyDecisionEvent[] events = enabledListener.Events.ToArray();
            PolicyDecisionEvent[] shadowEvents = events.Where(item => item.EventId == 6).ToArray();
            PolicyDecisionEvent[] legacyEvents = events.Where(item => item.EventId == 4).ToArray();
            Assert(shadowEvents.Length == 8,
                "the shadow listener did not receive one comparison per completed evaluation");
            int[] policyKinds = [1, 1, 2, 2, 1, 1, 2, 2];
            for (var index = 0; index < shadowEvents.Length; index++)
                AssertPolicyShadowComparison(shadowEvents[index], policyKinds[index], statusId: 1);

            Assert(legacyEvents.Length == 8,
                "enabling shadow comparison changed the legacy policy event count");
            AssertPolicyDecision(legacyEvents[0], 1, 1, 0);
            AssertPolicyDecision(legacyEvents[1], 1, 2,
                (int)(ActorwrightObservabilityEventSource.PolicyReason.WorkspaceRootOutsideLab |
                      ActorwrightObservabilityEventSource.PolicyReason.OutputRootOutsideWorkspace));
            AssertPolicyDecision(legacyEvents[2], 2, 1, 0);
            AssertPolicyDecision(legacyEvents[3], 2, 2,
                (int)(ActorwrightObservabilityEventSource.PolicyReason.WorkspaceRootOutsideLab |
                      ActorwrightObservabilityEventSource.PolicyReason.DataRootOutsideWorkspace));
            AssertPolicyDecisionIncludesReasons(legacyEvents[4], 1,
                ActorwrightObservabilityEventSource.PolicyReason.AlternateDataStreamRefused);
            AssertPolicyDecisionIncludesReasons(legacyEvents[5], 1,
                ActorwrightObservabilityEventSource.PolicyReason.OutputRootOutsideWorkspace |
                ActorwrightObservabilityEventSource.PolicyReason.ProtectedRootRefused);
            AssertPolicyDecisionIncludesReasons(legacyEvents[6], 2,
                ActorwrightObservabilityEventSource.PolicyReason.AlternateDataStreamRefused);
            AssertPolicyDecisionIncludesReasons(legacyEvents[7], 2,
                ActorwrightObservabilityEventSource.PolicyReason.DataRootOutsideWorkspace |
                ActorwrightObservabilityEventSource.PolicyReason.ProtectedRootRefused);
            Assert(!EventsContain(events, privatePath),
                "policy observation exposed a workspace path component");
        }

        TestWorkspacePolicyShadowOrderedComparison();
        TestWorkspacePolicyShadowMapsInspectionFacts();
        TestWorkspacePolicyShadowFailureIsolation();
        TestWorkspacePolicyProtectedTargetOverlap();
        return Task.CompletedTask;
    }

    private static void TestWorkspacePolicyProtectedTargetOverlap()
    {
        var labRoot = new WorkspacePath(@"K:\policy-protected-overlap\lab");
        var workspaceRoot = new WorkspacePath(
            Path.Combine(labRoot.Value, "workspace"));
        var protectedRoot = new WorkspacePath(
            Path.Combine(workspaceRoot.Value, "game"));
        var siblingRoot = new WorkspacePath(
            Path.Combine(workspaceRoot.Value, "output"));
        var policy = new KOnlyWorkspacePolicy(labRoot, protectedRoot);

        AssertTarget(WorkspacePolicyShadowKind.Output, workspaceRoot,
            workspaceRoot, expectedProtectedRefusal: true,
            "output-root-containing-protected-subtree-is-refused");
        AssertTarget(WorkspacePolicyShadowKind.ReadRoot, workspaceRoot,
            workspaceRoot, expectedProtectedRefusal: true,
            "read-root-containing-protected-subtree-is-refused");
        AssertTarget(WorkspacePolicyShadowKind.Output, workspaceRoot,
            protectedRoot, expectedProtectedRefusal: true,
            "output-root-equal-to-protected-root-is-refused");
        AssertTarget(WorkspacePolicyShadowKind.ReadRoot, workspaceRoot,
            protectedRoot, expectedProtectedRefusal: true,
            "read-root-equal-to-protected-root-is-refused");
        AssertTarget(WorkspacePolicyShadowKind.Output, workspaceRoot,
            new WorkspacePath(Path.Combine(protectedRoot.Value, "Data")),
            expectedProtectedRefusal: true,
            "output-root-beneath-protected-root-is-refused");
        AssertTarget(WorkspacePolicyShadowKind.ReadRoot, workspaceRoot,
            new WorkspacePath(Path.Combine(protectedRoot.Value, "Data")),
            expectedProtectedRefusal: true,
            "read-root-beneath-protected-root-is-refused");

        // A broad workspace is only context; a safe sibling target remains admitted.
        AssertTarget(WorkspacePolicyShadowKind.Output, workspaceRoot,
            siblingRoot, expectedProtectedRefusal: false,
            "sibling-output-remains-admitted-under-broad-workspace");
        AssertTarget(WorkspacePolicyShadowKind.ReadRoot, workspaceRoot,
            siblingRoot, expectedProtectedRefusal: false,
            "sibling-read-remains-admitted-under-broad-workspace");

        void AssertTarget(
            WorkspacePolicyShadowKind kind,
            WorkspacePath contextRoot,
            WorkspacePath targetRoot,
            bool expectedProtectedRefusal,
            string assertion)
        {
            ImmutableArray<Diagnostic> diagnostics = kind switch
            {
                WorkspacePolicyShadowKind.Output =>
                    policy.Evaluate(contextRoot, targetRoot),
                WorkspacePolicyShadowKind.ReadRoot =>
                    policy.EvaluateReadRoot(contextRoot, targetRoot),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            WorkspacePolicyShadowDecision shadow =
                WorkspacePolicyShadow.Evaluate(new WorkspacePolicyShadowRequest(
                    kind,
                    labRoot,
                    protectedRoot,
                    contextRoot,
                    targetRoot,
                    WorkspacePolicyPathInspection.Clear,
                    WorkspacePolicyPathInspection.Clear));
            bool hasProtectedRefusal = diagnostics.Any(item =>
                item.Code == ProtocolV2DiagnosticCodes.ProtectedRootRefused &&
                item.Severity == DiagnosticSeverity.Error);
            Assert(hasProtectedRefusal == expectedProtectedRefusal &&
                   WorkspacePolicyShadow.Matches(diagnostics, shadow),
                assertion);
            if (!expectedProtectedRefusal)
                Assert(diagnostics.All(item =>
                        item.Severity != DiagnosticSeverity.Error),
                    assertion + "-no-other-refusal");
        }
    }

    private static void TestWorkspacePolicyShadowOrderedComparison()
    {
        var authoritative = ImmutableArray.Create(
            new Diagnostic(ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
                DiagnosticSeverity.Error, "authority message one"),
            new Diagnostic(ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace,
                DiagnosticSeverity.Error, "authority message two"),
            new Diagnostic(ProtocolV2DiagnosticCodes.ReparsePointRefused,
                DiagnosticSeverity.Error, "authority message three"),
            new Diagnostic(ProtocolV2DiagnosticCodes.ReparsePointRefused,
                DiagnosticSeverity.Error, "authority message four"));
        var exactFindings = ImmutableArray.Create(
            new WorkspacePolicyShadowFinding(
                ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab, DiagnosticSeverity.Error),
            new WorkspacePolicyShadowFinding(
                ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace, DiagnosticSeverity.Error),
            new WorkspacePolicyShadowFinding(
                ProtocolV2DiagnosticCodes.ReparsePointRefused, DiagnosticSeverity.Error),
            new WorkspacePolicyShadowFinding(
                ProtocolV2DiagnosticCodes.ReparsePointRefused, DiagnosticSeverity.Error));
        var exact = new WorkspacePolicyShadowDecision(false, exactFindings);

        Assert(WorkspacePolicyShadow.Matches(authoritative, exact),
            "the shadow comparison rejected an identical ordered sequence with duplicate codes");
        Assert(!WorkspacePolicyShadow.Matches(authoritative,
                exact with { Findings = exactFindings.Reverse().ToImmutableArray() }),
            "the shadow comparison ignored diagnostic order");
        Assert(!WorkspacePolicyShadow.Matches(authoritative,
                exact with { Findings = exactFindings.RemoveAt(3) }),
            "the shadow comparison ignored a duplicate diagnostic");
        Assert(!WorkspacePolicyShadow.Matches(authoritative,
                exact with
                {
                    Findings = exactFindings.SetItem(0,
                        exactFindings[0] with { Severity = DiagnosticSeverity.Warning })
                }),
            "the shadow comparison ignored diagnostic severity");
        Assert(!WorkspacePolicyShadow.Matches(authoritative,
                exact with { IsAllowed = true }),
            "the shadow comparison ignored the allow/refuse result");
    }

    private static void TestWorkspacePolicyShadowMapsInspectionFacts()
    {
        // Injected facts exercise model mapping only; they do not prove real link detection.
        var request = new WorkspacePolicyShadowRequest(
            WorkspacePolicyShadowKind.Output,
            new WorkspacePath(@"K:\policy-shadow-model\lab"),
            new WorkspacePath(@"F:\ExampleGame"),
            new WorkspacePath(@"K:\policy-shadow-model\outside"),
            new WorkspacePath(@"K:\policy-shadow-model\target"),
            WorkspacePolicyPathInspection.ReparsePoint,
            WorkspacePolicyPathInspection.Failed);
        WorkspacePolicyShadowDecision decision = WorkspacePolicyShadow.Evaluate(request);
        string[] codes = decision.Findings.Select(finding => finding.Code).ToArray();

        Assert(codes.SequenceEqual(
                [
                    ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
                    ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace,
                    ProtocolV2DiagnosticCodes.ReparsePointRefused,
                    ProtocolV2DiagnosticCodes.PathInspectionFailed
                ], StringComparer.Ordinal),
            "the pure model did not preserve ordered reparse and inspection-failure facts");
        Assert(decision.Findings.All(finding => finding.Severity == DiagnosticSeverity.Error) &&
               !decision.IsAllowed,
            "the pure model did not retain refusal severity for inspection facts");

        var deviceLab = new WorkspacePath(@"\\?\K:\policy-shadow-model\lab");
        var deviceWorkspace = new WorkspacePath(@"\\?\K:\policy-shadow-model\lab\workspace");
        var deviceTarget = new WorkspacePath(@"\\?\K:\policy-shadow-model\lab\workspace\output:stream");
        var deviceRequest = new WorkspacePolicyShadowRequest(
            WorkspacePolicyShadowKind.Output,
            deviceLab,
            new WorkspacePath(@"F:\ExampleGame"),
            deviceWorkspace,
            deviceTarget,
            WorkspacePolicyPathInspection.Clear,
            WorkspacePolicyPathInspection.Clear);
        string[] deviceCodes = WorkspacePolicyShadow.Evaluate(deviceRequest)
            .Findings.Select(finding => finding.Code).ToArray();
        Assert(deviceCodes.SequenceEqual(
                [
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    ProtocolV2DiagnosticCodes.AlternateDataStreamRefused,
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    ProtocolV2DiagnosticCodes.AlternateDataStreamRefused
                ], StringComparer.Ordinal),
            "the pure model did not retain ordered device-path and ADS refusals");
    }

    private static void TestWorkspacePolicyShadowFailureIsolation()
    {
        const string privatePath = "workspace-policy-shadow-failure-secret";
        var labRoot = new WorkspacePath($@"K:\{privatePath}\lab");
        var workspaceRoot = new WorkspacePath(Path.Combine(labRoot.Value, "workspace"));
        var outputRoot = new WorkspacePath(Path.Combine(workspaceRoot.Value, "output"));
        var refusedWorkspaceRoot = new WorkspacePath($@"K:\{privatePath}\outside-workspace");
        var refusedOutputRoot = new WorkspacePath($@"K:\{privatePath}\outside-output");
        var protectedLiveRoot = new WorkspacePath(@"F:\ExampleGame");
        ImmutableArray<Diagnostic> expectedAllowed =
            new KOnlyWorkspacePolicy(labRoot, protectedLiveRoot).Evaluate(workspaceRoot, outputRoot);

        using (var mismatchListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.PolicyShadow))
        {
            var mismatchingPolicy = new KOnlyWorkspacePolicy(
                labRoot,
                protectedLiveRoot,
                _ => new WorkspacePolicyShadowDecision(
                    false,
                    [new WorkspacePolicyShadowFinding("synthetic-shadow-only", DiagnosticSeverity.Error)]));
            ImmutableArray<Diagnostic> actual = mismatchingPolicy.Evaluate(workspaceRoot, outputRoot);

            Assert(actual.SequenceEqual(expectedAllowed),
                "a shadow mismatch changed the authoritative diagnostics");
            PolicyDecisionEvent comparison = mismatchListener.Events.Single(item => item.EventId == 6);
            AssertPolicyShadowComparison(comparison, policyKindId: 1, statusId: 2);
            Assert(!EventsContain(mismatchListener.Events, privatePath),
                "a mismatch event exposed a path or model detail");
        }

        using (var failedModelListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.PolicyShadow))
        {
            var failingPolicy = new KOnlyWorkspacePolicy(
                labRoot,
                protectedLiveRoot,
                _ => throw new InvalidOperationException(privatePath));
            ImmutableArray<Diagnostic> actual = failingPolicy.Evaluate(workspaceRoot, outputRoot);

            Assert(actual.SequenceEqual(expectedAllowed),
                "a shadow-model exception changed the authoritative diagnostics");
            PolicyDecisionEvent comparison = failedModelListener.Events.Single(item => item.EventId == 6);
            AssertPolicyShadowComparison(comparison, policyKindId: 1, statusId: 3);
            Assert(!EventsContain(failedModelListener.Events, privatePath),
                "a failed shadow event exposed an exception detail");
        }

        ImmutableArray<Diagnostic> expectedRefusal =
            new KOnlyWorkspacePolicy(labRoot, protectedLiveRoot)
                .Evaluate(refusedWorkspaceRoot, refusedOutputRoot);
        using (var refuseListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.PolicyShadow))
        {
            var shadowThatWouldAllow = new KOnlyWorkspacePolicy(
                labRoot, protectedLiveRoot,
                _ => new WorkspacePolicyShadowDecision(true,
                    ImmutableArray<WorkspacePolicyShadowFinding>.Empty));
            ImmutableArray<Diagnostic> actual =
                shadowThatWouldAllow.Evaluate(refusedWorkspaceRoot, refusedOutputRoot);

            Assert(actual.SequenceEqual(expectedRefusal) &&
                   actual.Any(item => item.Severity == DiagnosticSeverity.Error),
                "a shadow allow changed an authoritative refusal");
            PolicyDecisionEvent comparison = refuseListener.Events.Single(item => item.EventId == 6);
            AssertPolicyShadowComparison(comparison, policyKindId: 1, statusId: 2);
        }

        using var throwingListener = new ThrowingPolicyShadowListener();
        var policyWithThrowingObserver = new KOnlyWorkspacePolicy(labRoot, protectedLiveRoot);
        ImmutableArray<Diagnostic> observed =
            policyWithThrowingObserver.Evaluate(workspaceRoot, outputRoot);
        Assert(throwingListener.EventCount > 0,
            "the throwing observer was not invoked for a shadow event");
        Assert(observed.SequenceEqual(expectedAllowed),
            "a throwing EventListener changed the authoritative diagnostics");
    }

    private static void AssertDiagnosticCodes(
        ImmutableArray<Diagnostic> diagnostics,
        params string[] expectedCodes)
    {
        Assert(diagnostics.Select(item => item.Code).SequenceEqual(expectedCodes, StringComparer.Ordinal) &&
               diagnostics.All(item => item.Severity == DiagnosticSeverity.Error),
            "the policy diagnostics did not retain the expected ordered refusal codes");
    }

    private static void AssertDiagnosticCodesIncludeInOrder(
        ImmutableArray<Diagnostic> diagnostics,
        params string[] expectedCodes)
    {
        int nextExpected = 0;
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (nextExpected < expectedCodes.Length &&
                string.Equals(diagnostic.Code, expectedCodes[nextExpected], StringComparison.Ordinal))
                nextExpected++;
        }

        Assert(nextExpected == expectedCodes.Length &&
               diagnostics.All(item => item.Severity == DiagnosticSeverity.Error),
            "the policy diagnostics omitted an ordered refusal code");
    }

    private static void AssertPolicyDecisionIncludesReasons(
        PolicyDecisionEvent item,
        int policyKindId,
        ActorwrightObservabilityEventSource.PolicyReason requiredReasons)
    {
        Assert(item.EventId == 4 &&
               item.PayloadNames.SequenceEqual(
                   ["policyKindId", "decisionId", "reasonFlags"], StringComparer.Ordinal) &&
               item.Payload.Length == 3 &&
               Equals(item.Payload[0], policyKindId) &&
               Equals(item.Payload[1], (int)ActorwrightObservabilityEventSource.PolicyDecisionId.Refused) &&
               item.Payload[2] is int reasons && (((ActorwrightObservabilityEventSource.PolicyReason)reasons & requiredReasons) == requiredReasons),
            "the legacy policy event omitted a required refusal reason or changed its schema");
    }

    private static void AssertPolicyShadowComparison(
        PolicyDecisionEvent item,
        int policyKindId,
        int statusId)
    {
        Assert(item.EventId == 6 &&
               item.PayloadNames.SequenceEqual(["policyKindId", "statusId"], StringComparer.Ordinal),
            "the policy-shadow event payload contained fields outside its closed schema");
        Assert(item.Payload.Length == 2 &&
               Equals(item.Payload[0], policyKindId) &&
               Equals(item.Payload[1], statusId) &&
               item.Payload.All(value => value is int),
            "the policy-shadow event did not contain the expected closed comparison result");
    }

    private sealed class ThrowingPolicyShadowListener : EventListener
    {
        public int EventCount { get; private set; }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, "Actorwright-Observability", StringComparison.Ordinal))
                EnableEvents(eventSource, EventLevel.Informational,
                    ActorwrightObservabilityEventSource.Keywords.PolicyShadow);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId != 6)
                return;
            EventCount++;
            throw new InvalidOperationException("synthetic observer failure");
        }
    }
}
