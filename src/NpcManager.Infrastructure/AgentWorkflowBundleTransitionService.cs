using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record AgentWorkflowBundleTransition(
    AgentWorkflowBundleDocument Document,
    WorkflowEvaluation Evaluation);

internal sealed class AgentWorkflowBundleTransitionLease(
    AgentWorkflowBundleTransition transition,
    IDisposable documentLease) : IDisposable
{
    private IDisposable? documentLease = documentLease;

    internal AgentWorkflowBundleTransition Transition { get; } = transition;

    public void Dispose() => Interlocked.Exchange(
        ref documentLease,
        null)?.Dispose();
}

public sealed class AgentWorkflowBundleTransitionService(
    AgentWorkflowBundleCodec codec)
{
    public AgentWorkflowBundleTransition LoadForFinishCommand(
        WorkspacePath path, string expectedSha256, string expectedCommand,
        Func<AgentWorkflowBundleDocument, AgentReviewReceiptDocument>? receiptLoader = null)
    {
        if (string.IsNullOrWhiteSpace(expectedCommand))
            throw new ArgumentException("The expected workflow command must be nonempty.", nameof(expectedCommand));
        AgentWorkflowBundleDocument document = codec.Load(path, expectedSha256);
        AgentReviewReceiptDocument? receipt = null;
        if (receiptLoader is not null && document.Bundle.Artifacts.Any(artifact => artifact.Kind == WorkflowArtifactKinds.ReviewReceipt))
        {
            receipt = receiptLoader(document);
            AgentReviewReceiptService.ValidateForSuccessor(document, receipt);
        }
        WorkflowEvaluation evaluation = receipt is null ? Evaluate(document.Bundle) :
            Evaluate(document.Bundle, receipt, receipt.Receipt.BundleSha256);
        RequireDerivedState(document.Bundle, evaluation);
        string[] kinds = document.Bundle.Artifacts.Select(artifact => artifact.Kind).Order(StringComparer.Ordinal).ToArray();
        bool initialPackage = expectedCommand == "npc finish analyze" &&
            (kinds.SequenceEqual(new[] { WorkflowArtifactKinds.ReviewedWorkspaceIntake, WorkflowArtifactKinds.NpcPackageManifest }.Order(StringComparer.Ordinal)) ||
             kinds.SequenceEqual(new[] { WorkflowArtifactKinds.NpcPackageManifest, WorkflowArtifactKinds.NpcPreviewManifest }.Order(StringComparer.Ordinal)));
        bool unreviewedFinish = evaluation.Phase == AgentWorkflowPhase.ReviewRequired &&
            (expectedCommand.StartsWith("npc finish ", StringComparison.Ordinal) || expectedCommand == "gui") &&
            evaluation.NextActions.Any(action => string.Equals(action.Command, expectedCommand, StringComparison.Ordinal));
        if (!initialPackage && !unreviewedFinish) RequireExpectedCommand(evaluation, expectedCommand);
        return new(document, evaluation);
    }

    public AgentWorkflowBundleTransition LoadForCommand(
        WorkspacePath path,
        string expectedSha256,
        string expectedCommand,
        Func<AgentWorkflowBundleDocument, AgentReviewReceiptDocument>? receiptLoader = null)
    {
        if (string.IsNullOrWhiteSpace(expectedCommand))
            throw new ArgumentException(
                "The expected workflow command must be nonempty.",
                nameof(expectedCommand));
        AgentWorkflowBundleDocument document = codec.Load(
            path,
            expectedSha256);
        AgentReviewReceiptDocument? receipt = null;
        if (receiptLoader is not null && document.Bundle.Artifacts.Any(artifact => artifact.Kind == WorkflowArtifactKinds.ReviewReceipt))
        {
            receipt = receiptLoader(document);
            AgentReviewReceiptService.ValidateForSuccessor(document, receipt);
        }
        WorkflowEvaluation evaluation = receipt is null ? Evaluate(document.Bundle) :
            Evaluate(document.Bundle, receipt, receipt.Receipt.BundleSha256);
        RequireDerivedState(document.Bundle, evaluation);
        RequireExpectedCommand(evaluation, expectedCommand);
        return new AgentWorkflowBundleTransition(document, evaluation);
    }

    public AgentWorkflowBundleTransition LoadForReviewedCommand(
        WorkspacePath path,
        string expectedSha256,
        string expectedCommand,
        Func<AgentWorkflowBundleDocument, AgentReviewReceiptDocument>
            receiptLoader)
    {
        if (string.IsNullOrWhiteSpace(expectedCommand))
            throw new ArgumentException(
                "The expected workflow command must be nonempty.",
                nameof(expectedCommand));
        ArgumentNullException.ThrowIfNull(receiptLoader);
        AgentWorkflowBundleDocument document = codec.Load(
            path,
            expectedSha256);
        AgentReviewReceiptDocument receipt = receiptLoader(document) ??
            throw new ArgumentException(
                "The reviewed workflow receipt loader returned no document.",
                nameof(receiptLoader));
        AgentReviewReceiptService.ValidateForSuccessor(document, receipt);
        WorkflowEvaluation evaluation = Evaluate(
            document.Bundle,
            receipt,
            receipt.Receipt.BundleSha256);
        RequireAdmittedEvaluation(evaluation, reviewed: true);
        RequireDerivedState(document.Bundle, evaluation);
        RequireExpectedCommand(evaluation, expectedCommand);
        return new AgentWorkflowBundleTransition(document, evaluation);
    }

    public AgentWorkflowBundleTransition LoadReviewedFinishForPackaging(
        WorkspacePath path,
        string expectedSha256,
        Func<AgentWorkflowBundleDocument, AgentReviewReceiptDocument> receiptLoader)
    {
        ArgumentNullException.ThrowIfNull(receiptLoader);
        AgentWorkflowBundleDocument document = codec.Load(path, expectedSha256);
        AgentReviewReceiptDocument receipt = receiptLoader(document) ??
            throw new ArgumentException("The reviewed workflow receipt loader returned no document.", nameof(receiptLoader));
        AgentReviewReceiptService.ValidateForSuccessor(document, receipt);
        WorkflowEvaluation evaluation = Evaluate(document.Bundle, receipt, receipt.Receipt.BundleSha256);
        RequireAdmittedEvaluation(evaluation, reviewed: true);
        RequireDerivedState(document.Bundle, evaluation);
        if (!AgentWorkflowService.IsFinishReviewContinuation(document.Bundle, reviewed: true))
            throw Refused("workflow-human-review-required",
                "Finish package archive and verification require the exact reviewed Finish workflow receipt binding.");
        return new(document, evaluation);
    }

    public AgentWorkflowBundleTransition WriteInitial(
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        ActorwrightObservabilityEventSource? observer =
            GetEnabledWorkflowObserver();
        const ActorwrightObservabilityEventSource.WorkflowOperationId operation =
            ActorwrightObservabilityEventSource.WorkflowOperationId.Initial;
        try
        {
            ArgumentNullException.ThrowIfNull(npc);
            AgentWorkflowBundleTransition transition = WriteDerived(
                npc,
                requestDigest,
                artifacts,
                output);
            TryRecordWorkflowSuccess(observer, operation, transition);
            return transition;
        }
        catch (Exception exception)
        {
            TryRecordWorkflowFailure(observer, operation, exception);
            throw;
        }
    }

    public AgentWorkflowBundleTransition Advance(
        AgentWorkflowBundleTransition input,
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        using AgentWorkflowBundleTransitionLease retained =
            AdvanceRetained(
                input,
                npc,
                requestDigest,
                artifacts,
                output);
        return retained.Transition;
    }

    public AgentWorkflowBundleTransition AdvanceReviewed(
        AgentWorkflowBundleTransition input,
        WorkflowNpcIdentity npc,
        string requestDigest,
        AgentReviewReceiptDocument receipt,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        using AgentWorkflowBundleTransitionLease retained =
            AdvanceReviewedRetained(
                input,
                npc,
                requestDigest,
                receipt,
                artifacts,
                output);
        return retained.Transition;
    }

    internal AgentWorkflowBundleTransitionLease AdvanceReviewedRetained(
        AgentWorkflowBundleTransition input,
        WorkflowNpcIdentity npc,
        string requestDigest,
        AgentReviewReceiptDocument receipt,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        ActorwrightObservabilityEventSource? observer =
            GetEnabledWorkflowObserver();
        const ActorwrightObservabilityEventSource.WorkflowOperationId operation =
            ActorwrightObservabilityEventSource.WorkflowOperationId.AdvanceReviewed;
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(npc);
            ArgumentNullException.ThrowIfNull(receipt);
            AgentWorkflowBundleDocument currentInput = codec.Load(
                input.Document.Path,
                input.Document.Sha256);
            WorkflowEvaluation currentInputEvaluation = Evaluate(
                currentInput.Bundle);
            RequireDerivedState(currentInput.Bundle, currentInputEvaluation);
            RequireIdentityEvolution(currentInput.Bundle.Npc, npc);
            RequireReviewedCarryover(
                currentInput.Bundle,
                artifacts,
                receipt.Artifact);
            RequireReceiptArtifact(artifacts, receipt, requestDigest);
            if (string.Equals(
                    currentInput.Path.Value,
                    output.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw Refused(
                    "workflow-output-overlap",
                    "The workflow output must differ from its input bundle.");
            AgentWorkflowBundleTransitionLease lease = WriteDerivedRetained(
                npc,
                requestDigest,
                artifacts,
                output,
                receipt,
                currentInput.Sha256);
            TryRecordWorkflowSuccess(observer, operation, lease.Transition);
            return lease;
        }
        catch (Exception exception)
        {
            TryRecordWorkflowFailure(observer, operation, exception);
            throw;
        }
    }

    internal AgentWorkflowBundleTransitionLease AdvanceRetained(
        AgentWorkflowBundleTransition input,
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        ActorwrightObservabilityEventSource? observer =
            GetEnabledWorkflowObserver();
        const ActorwrightObservabilityEventSource.WorkflowOperationId operation =
            ActorwrightObservabilityEventSource.WorkflowOperationId.Advance;
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(npc);
            RequireIdentityEvolution(input.Document.Bundle.Npc, npc);
            if (string.Equals(
                    input.Document.Path.Value,
                    output.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw Refused(
                    "workflow-output-overlap",
                    "The workflow output must differ from its input bundle.");
            AgentWorkflowBundleTransitionLease lease = WriteDerivedRetained(
                npc,
                requestDigest,
                artifacts,
                output);
            TryRecordWorkflowSuccess(observer, operation, lease.Transition);
            return lease;
        }
        catch (Exception exception)
        {
            TryRecordWorkflowFailure(observer, operation, exception);
            throw;
        }
    }

    internal AgentWorkflowBundleTransitionLease AdvanceWithReviewedReceiptRetained(
        AgentWorkflowBundleTransition input,
        WorkflowNpcIdentity npc,
        string requestDigest,
        AgentReviewReceiptDocument receipt,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        ActorwrightObservabilityEventSource? observer =
            GetEnabledWorkflowObserver();
        const ActorwrightObservabilityEventSource.WorkflowOperationId operation =
            ActorwrightObservabilityEventSource.WorkflowOperationId.AdvanceReviewedReceipt;
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(npc);
            ArgumentNullException.ThrowIfNull(receipt);
            AgentWorkflowBundleDocument currentInput = codec.Load(
                input.Document.Path,
                input.Document.Sha256);
            AgentReviewReceiptService.ValidateForSuccessor(currentInput, receipt);
            RequireIdentityEvolution(currentInput.Bundle.Npc, npc);
            WorkflowArtifactBinding expectedReceipt = receipt.Artifact with
            {
                RequestDigest = requestDigest
            };
            if (artifacts.Length != currentInput.Bundle.Artifacts.Length + 1 ||
                currentInput.Bundle.Artifacts
                    .Where(predecessor => predecessor.Kind != WorkflowArtifactKinds.ReviewReceipt)
                    .Any(predecessor =>
                        artifacts.Count(successor => ExactBinding(predecessor, successor)) != 1) ||
                artifacts.Count(successor => ExactBinding(expectedReceipt, successor)) != 1)
                throw Refused(
                    "workflow-artifact-binding-mismatch",
                    "A reviewed workflow successor must retain every exact reviewed artifact binding.");
            if (string.Equals(
                    currentInput.Path.Value,
                    output.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw Refused(
                    "workflow-output-overlap",
                    "The workflow output must differ from its input bundle.");
            AgentWorkflowBundleTransitionLease lease = WriteDerivedRetained(
                npc,
                requestDigest,
                artifacts,
                output,
                receipt,
                receipt.Receipt.BundleSha256);
            TryRecordWorkflowSuccess(observer, operation, lease.Transition);
            return lease;
        }
        catch (Exception exception)
        {
            TryRecordWorkflowFailure(observer, operation, exception);
            throw;
        }
    }

    public void AdmitFreshOutput(
        WorkspacePath output,
        params WorkspacePath[] distinctFrom)
    {
        ArgumentNullException.ThrowIfNull(distinctFrom);
        foreach (WorkspacePath path in distinctFrom)
        {
            if (string.Equals(
                    output.Value,
                    path.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw Refused(
                    "workflow-output-overlap",
                    "The workflow output must differ from every command input and output path.");
        }
        codec.AdmitFreshOutput(output);
    }

    private AgentWorkflowBundleTransition WriteDerived(
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output)
    {
        using AgentWorkflowBundleTransitionLease retained =
            WriteDerivedRetained(
                npc,
                requestDigest,
                artifacts,
                output);
        return retained.Transition;
    }

    private AgentWorkflowBundleTransitionLease WriteDerivedRetained(
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkspacePath output,
        AgentReviewReceiptDocument? receipt = null,
        string? predecessorBundleSha256 = null)
    {
        codec.AdmitFreshOutput(output);
        var provisional = new AgentWorkflowBundle(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            npc,
            AgentWorkflowPhase.Discover,
            requestDigest,
            artifacts,
            [],
            []);
        WorkflowEvaluation evaluation = Evaluate(
            provisional,
            receipt,
            predecessorBundleSha256);
        RequireAdmittedEvaluation(evaluation, reviewed: receipt is not null);
        AgentWorkflowBundle bundle = provisional with
        {
            Phase = evaluation.Phase,
            Authority = evaluation.Authority,
            NextActions = evaluation.NextActions
        };
        AgentWorkflowBundleDocumentLease? written = null;
        try
        {
            written = codec.WriteNewRetained(bundle, output);
            WorkflowEvaluation reopenedEvaluation = Evaluate(
                written.Document.Bundle,
                receipt,
                predecessorBundleSha256);
            RequireAdmittedEvaluation(
                reopenedEvaluation,
                reviewed: receipt is not null);
            RequireDerivedState(
                written.Document.Bundle,
                reopenedEvaluation);
            var transition = new AgentWorkflowBundleTransition(
                written.Document,
                reopenedEvaluation);
            var retained = new AgentWorkflowBundleTransitionLease(
                transition,
                written);
            written = null;
            return retained;
        }
        finally
        {
            written?.Dispose();
        }
    }

    private static ActorwrightObservabilityEventSource?
        GetEnabledWorkflowObserver()
    {
        try
        {
            ActorwrightObservabilityEventSource observer =
                ActorwrightObservabilityEventSource.Log;
            return observer.IsWorkflowEnabled ? observer : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryRecordWorkflowSuccess(
        ActorwrightObservabilityEventSource? observer,
        ActorwrightObservabilityEventSource.WorkflowOperationId operation,
        AgentWorkflowBundleTransition transition)
    {
        if (observer is null) return;
        try
        {
            observer.RecordWorkflowOutcome(
                operation,
                (int)transition.Document.Bundle.Phase,
                ActorwrightObservabilityEventSource.WorkflowOutcomeId.Succeeded,
                ActorwrightObservabilityEventSource.WorkflowFailureKindId.None);
        }
        catch (Exception)
        {
        }
    }

    private static void TryRecordWorkflowFailure(
        ActorwrightObservabilityEventSource? observer,
        ActorwrightObservabilityEventSource.WorkflowOperationId operation,
        Exception exception)
    {
        if (observer is null) return;
        try
        {
            (ActorwrightObservabilityEventSource.WorkflowOutcomeId outcome,
                ActorwrightObservabilityEventSource.WorkflowFailureKindId failureKind) =
                ClassifyWorkflowFailure(exception);
            observer.RecordWorkflowOutcome(
                operation,
                ActorwrightObservabilityEventSource.UnknownWorkflowPhase,
                outcome,
                failureKind);
        }
        catch (Exception)
        {
        }
    }

    private static (
        ActorwrightObservabilityEventSource.WorkflowOutcomeId Outcome,
        ActorwrightObservabilityEventSource.WorkflowFailureKindId FailureKind)
        ClassifyWorkflowFailure(Exception exception)
    {
        if (exception is AgentWorkflowCodecException
            {
                Code: "workflow-write-failed"
            } writeFailure)
        {
            ActorwrightObservabilityEventSource.WorkflowFailureKindId writeFailureKind =
                writeFailure.InnerException switch
                {
                    UnauthorizedAccessException =>
                        ActorwrightObservabilityEventSource.WorkflowFailureKindId.Access,
                    OperationCanceledException =>
                        ActorwrightObservabilityEventSource.WorkflowFailureKindId.Cancellation,
                    IOException =>
                        ActorwrightObservabilityEventSource.WorkflowFailureKindId.IO,
                    null =>
                        ActorwrightObservabilityEventSource.WorkflowFailureKindId.Unknown,
                    _ =>
                        ActorwrightObservabilityEventSource.WorkflowFailureKindId.Unexpected
                };
            return (
                ActorwrightObservabilityEventSource.WorkflowOutcomeId.Failed,
                writeFailureKind);
        }

        if (exception is AgentWorkflowCodecException or
            AgentReviewReceiptException or
            ArgumentException)
            return (
                ActorwrightObservabilityEventSource.WorkflowOutcomeId.Refused,
                ActorwrightObservabilityEventSource.WorkflowFailureKindId.Refusal);

        ActorwrightObservabilityEventSource.WorkflowFailureKindId failureKind =
            exception switch
            {
                UnauthorizedAccessException =>
                    ActorwrightObservabilityEventSource.WorkflowFailureKindId.Access,
                OperationCanceledException =>
                    ActorwrightObservabilityEventSource.WorkflowFailureKindId.Cancellation,
                IOException =>
                    ActorwrightObservabilityEventSource.WorkflowFailureKindId.IO,
                _ =>
                    ActorwrightObservabilityEventSource.WorkflowFailureKindId.Unexpected
            };
        return (
            ActorwrightObservabilityEventSource.WorkflowOutcomeId.Failed,
            failureKind);
    }

    private static WorkflowEvaluation Evaluate(
        AgentWorkflowBundle bundle,
        AgentReviewReceiptDocument? receipt = null,
        string? predecessorBundleSha256 = null) =>
        AgentWorkflowService.Evaluate(
            new WorkflowEvaluationInput(
                bundle,
                bundle.Artifacts.ToImmutableDictionary(
                artifact => artifact.Kind,
                artifact => new VerifiedWorkflowArtifact(
                    artifact.Kind,
                    artifact.Path,
                    artifact.Size,
                    artifact.Sha256,
                    true),
                StringComparer.Ordinal),
                receipt?.Receipt,
                receipt?.Sha256,
                predecessorBundleSha256));

    private static void RequireAdmittedEvaluation(
        WorkflowEvaluation evaluation,
        bool reviewed = false)
    {
        ProtocolDiagnostic? invalid = evaluation.Diagnostics.FirstOrDefault(
            diagnostic =>
                diagnostic.Code is
                    "workflow-input-invalid" or
                    "workflow-transition-invalid" or
                    "workflow-artifact-binding-mismatch" ||
                (reviewed && diagnostic.Code.StartsWith(
                    "workflow-review-",
                    StringComparison.Ordinal)));
        if (invalid is not null)
            throw Refused(invalid.Code, invalid.Message);
    }

    private static void RequireExpectedCommand(
        WorkflowEvaluation evaluation,
        string expectedCommand)
    {
        if (evaluation.NextActions is not [var action] || !string.Equals(
                action.Command, expectedCommand, StringComparison.Ordinal))
            throw Refused(
                "workflow-transition-command-mismatch",
                $"The workflow bundle does not admit the exact '{expectedCommand}' command.");
    }

    private static void RequireReceiptArtifact(
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        AgentReviewReceiptDocument receipt,
        string requestDigest)
    {
        if (artifacts.IsDefault)
            throw Refused(
                "workflow-input-invalid",
                "Reviewed successor artifacts must be initialized.");
        WorkflowArtifactBinding[] bindings = artifacts
            .Where(artifact => artifact is not null &&
                artifact.Kind == WorkflowArtifactKinds.ReviewReceipt)
            .ToArray();
        ImmutableArray<string> expectedInputs =
        [
            receipt.Receipt.BundleSha256,
            receipt.Receipt.ProposalSha256,
            receipt.Receipt.AuthorityNoticeSha256,
            .. receipt.Receipt.DisplayedArtifactHashes
        ];
        expectedInputs = expectedInputs.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (bindings.Length != 1 ||
            !ExactBinding(bindings[0], receipt.Artifact) ||
            !string.Equals(
                receipt.Path.Value,
                receipt.Artifact.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            receipt.Size != receipt.Artifact.Size ||
            receipt.Size != receipt.Utf8Json.Length ||
            !string.Equals(
                receipt.Sha256,
                receipt.Artifact.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Sha256,
                Convert.ToHexString(SHA256.HashData(
                    receipt.Utf8Json.AsSpan())),
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Artifact.SchemaOrMediaType,
                AgentWorkflowSchemas.ReviewReceiptV1,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Artifact.ProducerCommand,
                "gui",
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Artifact.RequestDigest,
                requestDigest,
                StringComparison.Ordinal) ||
            receipt.Artifact.SemanticSha256 is not null ||
            !receipt.Artifact.InputArtifactHashes.SequenceEqual(
                expectedInputs,
                StringComparer.Ordinal) ||
            !AgentReviewReceiptService.ComputeCanonicalBytes(receipt.Receipt)
                .AsSpan()
                .SequenceEqual(receipt.Utf8Json.AsSpan()))
            throw Refused(
                "workflow-review-artifact-binding-stale",
                "The reviewed successor must contain the exact canonical physical receipt artifact.");
    }

    private static void RequireReviewedCarryover(
        AgentWorkflowBundle predecessorBundle,
        ImmutableArray<WorkflowArtifactBinding> successor,
        WorkflowArtifactBinding receipt)
    {
        ImmutableArray<WorkflowArtifactBinding> predecessor = predecessorBundle.Artifacts;
        string[] packageReview =
        [
            WorkflowArtifactKinds.NpcPackageManifest,
            WorkflowArtifactKinds.NpcPreviewManifest
        ];
        string[] finishReview =
        [
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            WorkflowArtifactKinds.NpcPreviewManifest
        ];
        bool admittedPredecessor =
            predecessor.Select(artifact => artifact.Kind)
                .SequenceEqual(packageReview, StringComparer.Ordinal) ||
            predecessor.Select(artifact => artifact.Kind)
                .SequenceEqual(finishReview, StringComparer.Ordinal) ||
            AgentWorkflowService.IsFinishReviewContinuation(predecessorBundle, reviewed: false);
        if (!admittedPredecessor ||
            successor.Length != predecessor.Length + 1 ||
            !ExactBinding(successor[^1], receipt) ||
            !predecessor.Zip(successor.Take(predecessor.Length)).All(pair =>
                ExactBinding(pair.First, pair.Second)))
            throw Refused(
                "workflow-reviewed-carryover-mismatch",
                "A reviewed successor must preserve the exact ordered predecessor artifact bindings and append only its exact receipt.");
    }

    private static bool ExactBinding(
        WorkflowArtifactBinding left,
        WorkflowArtifactBinding right) =>
        left.Kind == right.Kind &&
        left.SchemaOrMediaType == right.SchemaOrMediaType &&
        left.Path == right.Path &&
        left.Size == right.Size &&
        left.Sha256 == right.Sha256 &&
        left.ProducerCommand == right.ProducerCommand &&
        left.RequestDigest == right.RequestDigest &&
        left.InputArtifactHashes.SequenceEqual(right.InputArtifactHashes) &&
        left.SemanticSha256 == right.SemanticSha256;

    private static void RequireDerivedState(
        AgentWorkflowBundle bundle,
        WorkflowEvaluation evaluation)
    {
        if (bundle.Phase != evaluation.Phase ||
            !AuthorityEquals(bundle.Authority, evaluation.Authority) ||
            !NextActionsEqual(bundle.NextActions, evaluation.NextActions))
            throw Refused(
                "workflow-derived-state-mismatch",
                "The persisted workflow phase, authority, or next actions differ from the current evaluator.");
    }

    private static bool AuthorityEquals(
        ImmutableArray<WorkflowAuthorityEvidence> left,
        ImmutableArray<WorkflowAuthorityEvidence> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind &&
            pair.First.State == pair.Second.State &&
            string.Equals(
                pair.First.Reason,
                pair.Second.Reason,
                StringComparison.Ordinal) &&
            pair.First.ArtifactHashes.SequenceEqual(
                pair.Second.ArtifactHashes,
                StringComparer.Ordinal));

    private static bool NextActionsEqual(
        ImmutableArray<ProtocolNextAction> left,
        ImmutableArray<ProtocolNextAction> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            string.Equals(
                pair.First.Command,
                pair.Second.Command,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.Reason,
                pair.Second.Reason,
                StringComparison.Ordinal) &&
            pair.First.RequiresHumanAction ==
                pair.Second.RequiresHumanAction &&
            pair.First.MissingPrerequisites.SequenceEqual(
                pair.Second.MissingPrerequisites,
                StringComparer.Ordinal) &&
            BindingsEqual(
                pair.First.RequiredBindings,
                pair.Second.RequiredBindings));

    private static bool BindingsEqual(
        ImmutableArray<ProtocolNextActionBinding> left,
        ImmutableArray<ProtocolNextActionBinding> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            string.Equals(
                pair.First.Option,
                pair.Second.Option,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.Value,
                pair.Second.Value,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.ArtifactSha256,
                pair.Second.ArtifactSha256,
                StringComparison.Ordinal));

    private static void RequireIdentityEvolution(
        WorkflowNpcIdentity input,
        WorkflowNpcIdentity output)
    {
        if (!string.Equals(
                input.EditorId,
                output.EditorId,
                StringComparison.Ordinal) ||
            Conflicts(input.DisplayName, output.DisplayName) ||
            Conflicts(input.Plugin, output.Plugin) ||
            Conflicts(input.LocalFormId, output.LocalFormId))
            throw Refused(
                "workflow-npc-identity-mismatch",
                "A workflow transition cannot replace established NPC identity fields.");
    }

    private static bool Conflicts(string? current, string? proposed) =>
        current is not null &&
        !string.Equals(current, proposed, StringComparison.Ordinal);

    private static AgentWorkflowCodecException Refused(
        string code,
        string message) => new(code, message);
}
