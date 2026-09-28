using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record AgentReviewReceiptDocument(
    AgentReviewReceipt Receipt,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json,
    WorkflowArtifactBinding Artifact);

internal sealed class AgentReviewReceiptDocumentLease(
    AgentReviewReceiptDocument document,
    FaceGeomHairRegionsOwnedFile artifactLease) : IDisposable
{
    private FaceGeomHairRegionsOwnedFile? artifactLease = artifactLease;

    internal AgentReviewReceiptDocument Document { get; } = document;

    internal byte[] ReadbackExact(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FaceGeomHairRegionsOwnedFile current = artifactLease ??
            throw new ObjectDisposedException(GetType().Name);
        return current.ReadbackExactAsync(maximumBytes, cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose() => Interlocked.Exchange(
        ref artifactLease,
        null)?.Dispose();
}

internal sealed class AgentReviewReceiptPublishedException(
    string code,
    string message,
    AgentReviewReceiptDocumentLease publishedReceipt,
    Exception innerException) : IOException(message, innerException)
{
    internal string Code { get; } = code;

    internal AgentReviewReceiptDocumentLease PublishedReceipt { get; } =
        publishedReceipt;
}

public sealed class AgentReviewReceiptException : IOException
{
    public AgentReviewReceiptException(
        string code,
        string message,
        Exception? innerException = null) :
        base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Persists exact operator-attested review receipts for the two guarded Finish
/// transitions. The existing <c>gui</c> command is the truthful producer
/// identity; this service does not invent or dispatch a CLI command.
/// </summary>
public sealed class AgentReviewReceiptService
{
    private const int MaximumReceiptBytes = 64 * 1024;
    private const string ProducerCommand = "gui";
    private const string Role = "agent review receipt";

    private sealed record ReviewPurpose(
        string ProposalKind,
        string Scope,
        string AuthorityNoticeSha256,
        ImmutableHashSet<string> DisplayableKinds);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private static readonly ImmutableHashSet<string> ReceiptProperties =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "schema",
            "bundleSha256",
            "proposalSha256",
            "displayedArtifactHashes",
            "authorityNoticeSha256",
            "outcome",
            "scope",
            "reviewerNote",
            "attestationKind");

    private readonly IWorkspacePolicy workspacePolicy;
    private readonly WorkspacePath labRoot;
    private readonly AgentWorkflowBundleCodec bundleCodec;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;

    public AgentReviewReceiptService(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        this.workspacePolicy = workspacePolicy;
        this.labRoot = labRoot;
        if (!string.Equals(
                Path.GetPathRoot(labRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
            throw Refused(
                "review-path-outside-lab",
                "The review receipt lab root must remain on K:.");
        RequirePolicyAllowed(labRoot, write: true);
        bundleCodec = new AgentWorkflowBundleCodec(workspacePolicy, labRoot);
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(labRoot);
    }

    public static byte[] ComputeCanonicalBytes(AgentReviewReceipt receipt)
    {
        try
        {
            AgentWorkflowContractValidation.Validate(receipt);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                receipt,
                JsonOptions);
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                bytes = Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes)
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
            if (bytes.Length is <= 0 or > MaximumReceiptBytes)
                throw Refused(
                    "review-file-size-invalid",
                    "The canonical review receipt is outside the admitted 1..64 KiB range.");
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                throw Refused(
                    "review-json-noncanonical",
                    "Canonical review receipt JSON must use LF line endings.");
            return bytes;
        }
        catch (AgentReviewReceiptException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                NotSupportedException)
        {
            throw Refused(
                "review-contract-invalid",
                "The review receipt contract is invalid.",
                exception);
        }
    }

    public AgentReviewReceiptDocument Create(
        AgentWorkflowBundleDocument verifiedBundle,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output)
    {
        try
        {
            using AgentReviewReceiptDocumentLease retained = CreateRetained(
                verifiedBundle,
                verifiedBundle.Bundle.RequestDigest,
                proposalSha256,
                displayedArtifacts,
                outcome,
                reviewerNote,
                output,
                validateBeforePublication: null,
                validateAfterPublication: null);
            return retained.Document;
        }
        catch (AgentReviewReceiptPublishedException exception)
        {
            exception.PublishedReceipt.Dispose();
            throw;
        }
    }

    internal AgentReviewReceiptDocument CreateWithPublicationValidation(
        AgentWorkflowBundleDocument verifiedBundle,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output,
        Action validateBeforePublication,
        Action validateAfterPublication)
    {
        try
        {
            using AgentReviewReceiptDocumentLease retained = CreateRetained(
                verifiedBundle,
                verifiedBundle.Bundle.RequestDigest,
                proposalSha256,
                displayedArtifacts,
                outcome,
                reviewerNote,
                output,
                validateBeforePublication ?? throw new ArgumentNullException(
                    nameof(validateBeforePublication)),
                validateAfterPublication ?? throw new ArgumentNullException(
                    nameof(validateAfterPublication)));
            return retained.Document;
        }
        catch (AgentReviewReceiptPublishedException exception)
        {
            exception.PublishedReceipt.Dispose();
            throw;
        }
    }

    internal AgentReviewReceiptDocumentLease CreateRetainedForCommand(
        AgentWorkflowBundleDocument verifiedBundle,
        string commandRequestDigest,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output) => CreateRetained(
            verifiedBundle,
            commandRequestDigest,
            proposalSha256,
            displayedArtifacts,
            outcome,
            reviewerNote,
            output,
            validateBeforePublication: null,
            validateAfterPublication: null);

    internal AgentReviewReceiptDocumentLease CreateRetainedForCommand(
        AgentWorkflowBundleDocument verifiedBundle,
        string commandRequestDigest,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output,
        Action validateBeforePublication,
        CancellationToken cancellationToken) => CreateRetained(
            verifiedBundle,
            commandRequestDigest,
            proposalSha256,
            displayedArtifacts,
            outcome,
            reviewerNote,
            output,
            validateBeforePublication ?? throw new ArgumentNullException(
                nameof(validateBeforePublication)),
            validateAfterPublication: null,
            cancellationToken);

    internal AgentReviewReceiptDocumentLease CreateRetained(
        AgentWorkflowBundleDocument verifiedBundle,
        string commandRequestDigest,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output,
        Action? validateBeforePublication,
        Action? validateAfterPublication,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireHash(
            commandRequestDigest,
            "review-request-digest-invalid",
            "The current GUI request digest must be one uppercase SHA-256.");
        AgentWorkflowBundleDocument currentBundle =
            RequireCurrentBundle(verifiedBundle);
        ReviewPurpose purpose = RequireReviewPurpose(currentBundle.Bundle);
        ImmutableArray<WorkflowArtifactBinding> exactDisplayed =
            RequireExactReviewBindings(
                currentBundle,
                proposalSha256,
                displayedArtifacts,
                purpose);
        RequireFreshOutput(output);

        AgentReviewReceipt receipt;
        try
        {
            receipt = AgentWorkflowContractValidation.CreateReviewReceipt(
                currentBundle.Sha256,
                proposalSha256,
                exactDisplayed.Select(artifact => artifact.Sha256)
                    .ToImmutableArray(),
                purpose.AuthorityNoticeSha256,
                outcome,
                purpose.Scope,
                reviewerNote);
        }
        catch (ArgumentException exception)
        {
            throw Refused(
                "review-contract-invalid",
                "The requested review receipt violates its closed contract.",
                exception);
        }

        byte[] canonical = ComputeCanonicalBytes(receipt);
        string expectedSha256 = Hash(canonical);
        var temporary = new WorkspacePath(
            output.Value + ".tmp-" + Guid.NewGuid().ToString("N"));
        FaceGeomHairRegionsOwnedFile? staged = null;
        bool published = false;
        AgentReviewReceiptDocument? preparedDocument = null;
        try
        {
            staged = fileSystem.CreateOwned(temporary, output, Role);
            byte[] readback = staged.WriteAndReadbackAsync(
                    canonical,
                    MaximumReceiptBytes,
                    cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!readback.AsSpan().SequenceEqual(canonical) ||
                !string.Equals(
                    Hash(readback),
                    expectedSha256,
                    StringComparison.Ordinal))
                throw Refused(
                    "review-staged-readback-mismatch",
                    "The staged review receipt failed exact readback.");
            try
            {
                validateBeforePublication?.Invoke();
            }
            catch (Exception exception) when (
                exception is AgentWorkflowCodecException or ArgumentException or
                    InvalidDataException or IOException or
                    UnauthorizedAccessException or InvalidOperationException or
                    JsonException)
            {
                throw Refused(
                    "review-pre-publication-validation-failed",
                    "The review predecessor changed before receipt publication.",
                    exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AgentWorkflowBundleDocument preparedBundle =
                RequireCurrentBundle(currentBundle);
            AgentReviewReceipt preparedReceipt = ParseStrict(readback);
            RequireReceiptBindings(
                preparedReceipt,
                preparedBundle,
                proposalSha256,
                exactDisplayed,
                purpose);
            preparedDocument = Document(
                preparedReceipt,
                output,
                readback,
                expectedSha256,
                preparedBundle,
                proposalSha256,
                exactDisplayed,
                purpose,
                commandRequestDigest);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                staged.PromoteNoOverwrite();
            }
            catch (Exception exception) when (staged.RenameCommitted)
            {
                var retainedAfterRename =
                    new AgentReviewReceiptDocumentLease(
                        preparedDocument,
                        staged);
                staged = null;
                published = true;
                throw new AgentReviewReceiptPublishedException(
                    PostPublicationCode(exception),
                    "The committed review receipt was preserved, but physical post-publication verification failed.",
                    retainedAfterRename,
                    exception);
            }
            published = true;
            var retained = new AgentReviewReceiptDocumentLease(
                preparedDocument,
                staged);
            staged = null;
            try
            {
                byte[] promotedReadback = retained.ReadbackExact(
                    MaximumReceiptBytes,
                    CancellationToken.None);
                if (!promotedReadback.AsSpan().SequenceEqual(canonical) ||
                    !string.Equals(
                        Hash(promotedReadback),
                        expectedSha256,
                        StringComparison.Ordinal))
                    throw Refused(
                        "review-promoted-readback-mismatch",
                        "The promoted review receipt changed during publication.");
            }
            catch (Exception exception)
            {
                throw new AgentReviewReceiptPublishedException(
                    PostPublicationCode(exception),
                    "The committed review receipt was preserved, but physical post-publication verification failed.",
                    retained,
                    exception);
            }
            try
            {
                validateAfterPublication?.Invoke();
            }
            catch (Exception exception)
            {
                throw new AgentReviewReceiptPublishedException(
                    "review-post-publication-validation-failed",
                    "The committed review receipt was preserved, but its predecessor evidence changed before workflow advancement.",
                    retained,
                    exception);
            }
            return retained;
        }
        catch (AgentReviewReceiptPublishedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!published && staged is not null &&
                !staged.TryDelete(out string? cleanupFailure))
                throw Refused(
                    "review-write-cancelled",
                    $"Review receipt publication was cancelled. Cleanup also failed: {cleanupFailure}");
            throw;
        }
        catch (Exception exception)
        {
            AgentReviewReceiptException primary = NormalizeWriteFailure(
                exception,
                output);
            if (!published && staged is not null &&
                !staged.TryDelete(out string? cleanupFailure))
                throw Refused(
                    primary.Code,
                    $"{primary.Message} Cleanup also failed: {cleanupFailure}",
                    primary);
            throw primary;
        }
        finally
        {
            staged?.Dispose();
        }
    }

    public AgentReviewReceiptDocument Load(
        AgentWorkflowBundleDocument verifiedBundle,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        WorkspacePath path,
        string expectedSha256)
    {
        RequireHash(expectedSha256, "review-hash-invalid",
            "The expected review receipt digest must be one uppercase SHA-256.");
        AgentWorkflowBundleDocument currentBundle =
            RequireCurrentBundle(verifiedBundle);
        ReviewPurpose purpose = RequireReviewPurpose(currentBundle.Bundle);
        ImmutableArray<WorkflowArtifactBinding> exactDisplayed =
            RequireExactReviewBindings(
                currentBundle,
                proposalSha256,
                displayedArtifacts,
                purpose);
        RequireWithinLab(path);
        RequirePolicyAllowed(path, write: false);

        byte[] bytes;
        try
        {
            using FaceGeomHairRegionsPinnedReadFile source =
                fileSystem.OpenRead(path, Role);
            bytes = source.ReadExact(MaximumReceiptBytes);
        }
        catch (AgentReviewReceiptException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("1..", StringComparison.Ordinal))
        {
            throw Refused(
                "review-file-size-invalid",
                "The review receipt is outside the admitted 1..64 KiB range.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "review-file-not-ordinary",
                "The review receipt must be one existing ordinary file.",
                exception);
        }

        string observedSha256 = Hash(bytes);
        if (!string.Equals(
                observedSha256,
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "review-hash-mismatch",
                "The review receipt SHA-256 does not match the expected digest.");

        AgentReviewReceipt receipt = ParseStrict(bytes);
        byte[] canonical = ComputeCanonicalBytes(receipt);
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw Refused(
                "review-json-noncanonical",
                "The review receipt is not canonical Actorwright JSON.");
        RequireReceiptBindings(
            receipt,
            currentBundle,
            proposalSha256,
            exactDisplayed,
            purpose);
        return Document(
            receipt,
            path,
            bytes,
            observedSha256,
            currentBundle,
            proposalSha256,
            exactDisplayed,
            purpose);
    }

    public AgentReviewReceiptDocument LoadForSuccessor(
        AgentWorkflowBundleDocument successor,
        WorkspacePath receipt,
        string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(successor);
        RequireHash(
            expectedSha256,
            "review-hash-invalid",
            "The expected review receipt digest must be one uppercase SHA-256.");
        WorkflowArtifactBinding suppliedBinding = RequireSuccessorReceiptBinding(
            successor.Bundle,
            receipt,
            expectedSha256);
        byte[] bytes = ReadReceiptBytes(receipt);
        string observedSha256 = Hash(bytes);
        if (!string.Equals(
                observedSha256,
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "review-hash-mismatch",
                "The review receipt SHA-256 does not match the expected digest.");
        AgentReviewReceipt parsedReceipt = ParseStrict(bytes);
        byte[] canonical = ComputeCanonicalBytes(parsedReceipt);
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw Refused(
                "review-json-noncanonical",
                "The review receipt is not canonical Actorwright JSON.");

        AgentWorkflowBundleDocument currentSuccessor =
            RequireCurrentBundle(successor);
        WorkflowArtifactBinding currentBinding =
            RequireSuccessorReceiptBinding(
                currentSuccessor.Bundle,
                receipt,
                expectedSha256);
        if (!ExactBinding(suppliedBinding, currentBinding) ||
            currentBinding.Size != bytes.LongLength ||
            !string.Equals(
                currentBinding.Sha256,
                observedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "review-receipt-binding-stale",
                "The physical review receipt does not match its exact successor artifact binding.");

        var document = new AgentReviewReceiptDocument(
            parsedReceipt,
            receipt,
            bytes.LongLength,
            observedSha256,
            bytes.ToImmutableArray(),
            currentBinding);
        ValidateForSuccessor(currentSuccessor, document);
        return document;
    }

    internal static void ValidateForSuccessor(
        AgentWorkflowBundleDocument successor,
        AgentReviewReceiptDocument receipt)
    {
        ArgumentNullException.ThrowIfNull(successor);
        ArgumentNullException.ThrowIfNull(receipt);
        WorkflowArtifactBinding binding = RequireSuccessorReceiptBinding(
            successor.Bundle,
            receipt.Path,
            receipt.Sha256);
        byte[] canonical = ComputeCanonicalBytes(receipt.Receipt);
        if (!ExactBinding(binding, receipt.Artifact) ||
            !string.Equals(
                binding.Path.Value,
                receipt.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            binding.Size != receipt.Size ||
            receipt.Size != receipt.Utf8Json.Length ||
            !string.Equals(
                binding.Sha256,
                receipt.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Sha256,
                Hash(receipt.Utf8Json.AsSpan()),
                StringComparison.Ordinal) ||
            !receipt.Utf8Json.AsSpan().SequenceEqual(canonical))
            throw Refused(
                "review-receipt-binding-stale",
                "The callback receipt document does not match its canonical bytes and exact successor artifact binding.");

        (WorkflowArtifactBinding proposal, WorkflowArtifactBinding preview,
            ReviewPurpose purpose) = RequireReviewedSuccessorSignature(
                successor.Bundle);
        ImmutableArray<string> exactDisplayedHashes =
            [proposal.Sha256, preview.Sha256];
        exactDisplayedHashes = exactDisplayedHashes
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (!string.Equals(
                receipt.Receipt.ProposalSha256,
                proposal.Sha256,
                StringComparison.Ordinal) ||
            !receipt.Receipt.DisplayedArtifactHashes.SequenceEqual(
                exactDisplayedHashes,
                StringComparer.Ordinal) ||
            !string.Equals(
                receipt.Receipt.Scope,
                purpose.Scope,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Receipt.AuthorityNoticeSha256,
                purpose.AuthorityNoticeSha256,
                StringComparison.Ordinal))
            throw Refused(
                "review-receipt-binding-stale",
                "The review receipt does not bind the successor proposal, preview display, scope, and authority notice.");
        if (receipt.Receipt.Outcome != ReviewOutcome.Accepted)
            throw Refused(
                "review-receipt-not-accepted",
                "A rejected or revision-requested receipt is durable evidence but cannot authorize a successor command.");

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
        if (!binding.InputArtifactHashes.SequenceEqual(
                expectedInputs,
                StringComparer.Ordinal))
            throw Refused(
                "review-receipt-binding-stale",
                "The review receipt artifact does not contain the exact closed predecessor, proposal, display, and notice input-hash closure.");
    }

    private AgentWorkflowBundleDocument RequireCurrentBundle(
        AgentWorkflowBundleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            AgentWorkflowBundleDocument reopened = bundleCodec.Load(
                document.Path,
                document.Sha256);
            if (reopened.Size != document.Size ||
                !reopened.Utf8Json.AsSpan().SequenceEqual(
                    document.Utf8Json.AsSpan()))
                throw Refused(
                    "review-bundle-stale",
                    "The supplied verified workflow bundle document changed.");
            return reopened;
        }
        catch (AgentReviewReceiptException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "review-bundle-stale",
                "The supplied workflow bundle is no longer exact and physically verified.",
                exception);
        }
    }

    private static ReviewPurpose RequireReviewPurpose(
        AgentWorkflowBundle bundle)
    {
        bool hasPackageProposal = bundle.Artifacts.Any(artifact =>
            artifact.Kind == WorkflowArtifactKinds.NpcPackageManifest);
        bool hasFinishProposal = bundle.Artifacts.Any(artifact =>
            artifact.Kind == WorkflowArtifactKinds.NpcFinishCoreProposal);
        bool finishContinuation = AgentWorkflowService.IsFinishReviewContinuation(bundle, reviewed: false);
        if (hasPackageProposal && hasFinishProposal && !finishContinuation)
            throw Refused(
                "review-purpose-ambiguous",
                "The workflow bundle contains both review proposal authorities.");
        if (!hasPackageProposal && !hasFinishProposal)
            throw Refused(
                "review-purpose-missing",
                "The workflow bundle contains no review proposal authority.");

        string proposalKind = hasPackageProposal && !finishContinuation
            ? WorkflowArtifactKinds.NpcPackageManifest
            : WorkflowArtifactKinds.NpcFinishCoreProposal;
        if (!AgentReviewContract.TryGetPurpose(
                proposalKind,
                out string scope,
                out string authorityNoticeSha256))
            throw Refused(
                "review-purpose-missing",
                "The workflow bundle proposal has no closed review purpose.");
        return new ReviewPurpose(
            proposalKind,
            scope,
            authorityNoticeSha256,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                proposalKind,
                WorkflowArtifactKinds.NpcPreviewManifest));
    }

    private static ImmutableArray<WorkflowArtifactBinding>
        RequireExactReviewBindings(
            AgentWorkflowBundleDocument bundle,
            string proposalSha256,
            ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
            ReviewPurpose purpose)
    {
        RequireHash(
            proposalSha256,
            "review-proposal-stale",
            "The current lifecycle proposal digest must be one uppercase SHA-256.");
        WorkflowArtifactBinding[] proposals = bundle.Bundle.Artifacts
            .Where(artifact =>
                artifact.Kind == purpose.ProposalKind)
            .ToArray();
        if (proposals.Length != 1 ||
            !string.Equals(
                proposals[0].Sha256,
                proposalSha256,
                StringComparison.Ordinal))
            throw Refused(
                "review-proposal-stale",
                "The review must bind exactly its current lifecycle proposal authority.");
        if (displayedArtifacts.IsDefaultOrEmpty)
            throw Refused(
                "review-displayed-empty",
                "At least one exact displayed artifact is required.");

        var seenKinds = new HashSet<string>(StringComparer.Ordinal);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var exact = ImmutableArray.CreateBuilder<WorkflowArtifactBinding>();
        foreach (WorkflowArtifactBinding displayed in displayedArtifacts)
        {
            if (displayed is null ||
                !seenKinds.Add(displayed.Kind) ||
                !seenHashes.Add(displayed.Sha256))
                throw Refused(
                    "review-displayed-duplicate",
                    "Displayed review artifacts must be unique by kind and SHA-256.");
            if (!purpose.DisplayableKinds.Contains(displayed.Kind))
                throw Refused(
                    "review-displayed-kind-invalid",
                    $"Artifact kind '{displayed.Kind}' is not displayable for this review purpose.");
            WorkflowArtifactBinding? bound = bundle.Bundle.Artifacts
                .SingleOrDefault(artifact => artifact.Kind == displayed.Kind);
            if (bound is null || !ExactBinding(bound, displayed))
                throw Refused(
                    "review-displayed-unbound",
                    $"Displayed artifact '{displayed.Kind}' is not the exact current bundle member.");
            exact.Add(bound);
        }
        if (!seenKinds.Contains(WorkflowArtifactKinds.NpcPreviewManifest))
            throw Refused(
                "review-preview-required",
                "Lifecycle review must display the current preview manifest.");
        if (!seenKinds.Contains(purpose.ProposalKind))
            throw Refused(
                "review-proposal-required",
                "Lifecycle review must display the current proposal artifact.");
        return exact.OrderBy(artifact => artifact.Sha256, StringComparer.Ordinal)
            .ToImmutableArray();
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

    private static WorkflowArtifactBinding RequireSuccessorReceiptBinding(
        AgentWorkflowBundle successor,
        WorkspacePath receipt,
        string expectedSha256)
    {
        WorkflowArtifactBinding[] bindings = successor.Artifacts
            .Where(artifact => artifact.Kind ==
                WorkflowArtifactKinds.ReviewReceipt)
            .ToArray();
        if (bindings.Length != 1)
            throw Refused(
                "review-receipt-binding-stale",
                "A reviewed successor must bind exactly one review receipt artifact.");
        WorkflowArtifactBinding binding = bindings[0];
        if (!string.Equals(
                binding.Path.Value,
                receipt.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                binding.Sha256,
                expectedSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.SchemaOrMediaType,
                AgentWorkflowSchemas.ReviewReceiptV1,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.ProducerCommand,
                ProducerCommand,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.RequestDigest,
                successor.RequestDigest,
                StringComparison.Ordinal) ||
            binding.SemanticSha256 is not null)
            throw Refused(
                "review-receipt-binding-stale",
                "The reviewed successor does not contain the exact receipt path, hash, schema, producer, and request binding.");
        return binding;
    }

    private static (
        WorkflowArtifactBinding Proposal,
        WorkflowArtifactBinding Preview,
        ReviewPurpose Purpose) RequireReviewedSuccessorSignature(
        AgentWorkflowBundle successor)
    {
        string[] signature = successor.Artifacts
            .Select(artifact => artifact.Kind)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] packageReview =
        [
            WorkflowArtifactKinds.NpcPackageManifest,
            WorkflowArtifactKinds.NpcPreviewManifest,
            WorkflowArtifactKinds.ReviewReceipt
        ];
        string[] finishReview =
        [
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            WorkflowArtifactKinds.NpcPreviewManifest,
            WorkflowArtifactKinds.ReviewReceipt
        ];
        Array.Sort(packageReview, StringComparer.Ordinal);
        Array.Sort(finishReview, StringComparer.Ordinal);
        string proposalKind;
        if (signature.SequenceEqual(packageReview, StringComparer.Ordinal))
            proposalKind = WorkflowArtifactKinds.NpcPackageManifest;
        else if (signature.SequenceEqual(finishReview, StringComparer.Ordinal) ||
                 AgentWorkflowService.IsFinishReviewContinuation(successor, reviewed: true))
            proposalKind = WorkflowArtifactKinds.NpcFinishCoreProposal;
        else
            throw Refused(
                "review-successor-signature-invalid",
                "The reviewed successor artifact signature is not one closed review transition.");
        if (!AgentReviewContract.TryGetPurpose(
                proposalKind,
                out string scope,
                out string authorityNoticeSha256))
            throw Refused(
                "review-purpose-missing",
                "The reviewed successor proposal has no closed review purpose.");
        WorkflowArtifactBinding proposal = successor.Artifacts.Single(
            artifact => artifact.Kind == proposalKind);
        WorkflowArtifactBinding preview = successor.Artifacts.Single(
            artifact => artifact.Kind ==
                WorkflowArtifactKinds.NpcPreviewManifest);
        return (
            proposal,
            preview,
            new ReviewPurpose(
                proposalKind,
                scope,
                authorityNoticeSha256,
                ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    proposalKind,
                    WorkflowArtifactKinds.NpcPreviewManifest)));
    }

    private byte[] ReadReceiptBytes(WorkspacePath path)
    {
        RequireWithinLab(path);
        RequirePolicyAllowed(path, write: false);
        try
        {
            using FaceGeomHairRegionsPinnedReadFile source =
                fileSystem.OpenRead(path, Role);
            return source.ReadExact(MaximumReceiptBytes);
        }
        catch (AgentReviewReceiptException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("1..", StringComparison.Ordinal))
        {
            throw Refused(
                "review-file-size-invalid",
                "The review receipt is outside the admitted 1..64 KiB range.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "review-file-not-ordinary",
                "The review receipt must be one existing ordinary file.",
                exception);
        }
    }

    private static void RequireReceiptBindings(
        AgentReviewReceipt receipt,
        AgentWorkflowBundleDocument bundle,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewPurpose purpose,
        string? requestDigest = null)
    {
        ImmutableArray<string> displayedHashes = displayedArtifacts
            .Select(artifact => artifact.Sha256)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (!string.Equals(
                receipt.BundleSha256,
                bundle.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ProposalSha256,
                proposalSha256,
                StringComparison.Ordinal) ||
            !receipt.DisplayedArtifactHashes.SequenceEqual(displayedHashes) ||
            !string.Equals(
                receipt.AuthorityNoticeSha256,
                purpose.AuthorityNoticeSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.Scope,
                purpose.Scope,
                StringComparison.Ordinal))
            throw Refused(
                "review-receipt-binding-stale",
                "The review receipt does not bind the current bundle, proposal, display set, scope, and authority notice.");
    }

    private static AgentReviewReceipt ParseStrict(byte[] bytes)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw Refused(
                    "review-json-invalid",
                    "The review receipt root must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in
                     parsed.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Refused(
                        "review-json-duplicate-property",
                        $"Duplicate review receipt JSON property '{property.Name}'.");
                if (!ReceiptProperties.Contains(property.Name))
                    throw Refused(
                        "review-json-unknown-property",
                        $"Unknown review receipt JSON property '{property.Name}'.");
            }
            if (!seen.SetEquals(ReceiptProperties))
                throw Refused(
                    "review-json-invalid",
                    "The review receipt JSON is missing a required property.");
            AgentReviewReceipt receipt =
                JsonSerializer.Deserialize<AgentReviewReceipt>(
                    bytes,
                    JsonOptions) ??
                throw Refused(
                    "review-json-invalid",
                    "The review receipt JSON is empty.");
            AgentWorkflowContractValidation.Validate(receipt);
            return receipt;
        }
        catch (AgentReviewReceiptException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Refused(
                "review-json-invalid",
                "The review receipt is not valid strict JSON.",
                exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw Refused(
                "review-contract-invalid",
                "The decoded review receipt violates its closed contract.",
                exception);
        }
    }

    private static AgentReviewReceiptDocument Document(
        AgentReviewReceipt receipt,
        WorkspacePath path,
        byte[] bytes,
        string sha256,
        AgentWorkflowBundleDocument bundle,
        string proposalSha256,
        ImmutableArray<WorkflowArtifactBinding> displayedArtifacts,
        ReviewPurpose purpose,
        string? requestDigest = null)
    {
        ImmutableArray<string> inputs =
        [
            bundle.Sha256,
            proposalSha256,
            purpose.AuthorityNoticeSha256,
            .. displayedArtifacts.Select(artifact => artifact.Sha256)
        ];
        inputs = inputs.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var artifact = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.ReviewReceipt,
            AgentWorkflowSchemas.ReviewReceiptV1,
            path,
            bytes.LongLength,
            sha256,
            ProducerCommand,
            requestDigest ?? bundle.Bundle.RequestDigest,
            inputs);
        AgentWorkflowContractValidation.Validate(artifact);
        return new AgentReviewReceiptDocument(
            receipt,
            path,
            bytes.LongLength,
            sha256,
            bytes.ToImmutableArray(),
            artifact);
    }

    private void RequireFreshOutput(WorkspacePath output)
    {
        RequireWithinLab(output);
        string? parentValue = Path.GetDirectoryName(output.Value);
        if (string.IsNullOrWhiteSpace(parentValue) ||
            !Directory.Exists(parentValue))
            throw Refused(
                "review-parent-missing",
                "The review receipt output parent must already exist.");
        RequirePolicyAllowed(new WorkspacePath(parentValue), write: true);
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            throw Refused(
                "review-output-exists",
                "The review receipt output must be a fresh path.");
    }

    private void RequireWithinLab(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot))
            throw Refused(
                "review-path-outside-lab",
                "Review receipt paths must remain under the exact K-local lab root.");
    }

    private void RequirePolicyAllowed(WorkspacePath path, bool write)
    {
        ImmutableArray<Diagnostic> diagnostics = write
            ? workspacePolicy.Evaluate(labRoot, path)
            : workspacePolicy.EvaluateReadRoot(labRoot, path);
        Diagnostic? refusal = diagnostics.FirstOrDefault(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (refusal is null)
            return;
        string code = string.Equals(
            refusal.Code,
            "reparse-point-refused",
            StringComparison.Ordinal)
            ? "review-reparse-refused"
            : "review-path-refused";
        throw Refused(code, refusal.Message);
    }

    private static void RequireHash(
        string value,
        string code,
        string message)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F'))))
            throw Refused(code, message);
    }

    private static AgentReviewReceiptException NormalizeWriteFailure(
        Exception exception,
        WorkspacePath output)
    {
        if (exception is AgentReviewReceiptException receiptException)
            return receiptException;
        if (exception is FaceGeomHairRegionsPathSubstitutionException)
            return Refused(
                "review-reparse-refused",
                "The review receipt output path changed identity or traversed a non-ordinary filesystem object.",
                exception);
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            return Refused(
                "review-output-exists",
                "The review receipt output became occupied before no-overwrite promotion.",
                exception);
        return Refused(
            "review-write-failed",
            "The review receipt fresh-write transaction failed.",
            exception);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string PostPublicationCode(Exception exception)
    {
        if (exception is AgentReviewReceiptException receipt &&
            (receipt.Code.Contains("readback", StringComparison.Ordinal) ||
             receipt.Code.Contains("write", StringComparison.Ordinal)))
            return "review-post-publication-operation-failed";
        return exception is UnauthorizedAccessException ||
            exception is IOException and not AgentReviewReceiptException
            ? "review-post-publication-operation-failed"
            : "review-post-publication-validation-failed";
    }

    private static AgentReviewReceiptException Refused(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
