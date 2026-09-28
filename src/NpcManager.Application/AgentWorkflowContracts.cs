using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class AgentWorkflowSchemas
{
    public const string BundleV1 = "actorwright.agent-workflow-bundle.v1";
    public const string ReviewReceiptV1 = "actorwright.review-receipt.v1";
    public const string SkyrimJslotFollowerWorkflowV1 =
        "skyrim-jslot-follower-v1";
}

public static class AgentReviewContract
{
    public const string FinishAnalyzeScope = "npc-finish-analyze";
    public const string FinishAnalyzeAuthorityNotice =
        "This operator attestation binds the exact current package proposal and displayed preview artifacts, permits only Finish Analyze, and grants neither human-visual authority, game-runtime authority, nor promotion authority.";
    public const string FinishApplyScope = "npc-finish-apply";
    public const string FinishApplyAuthorityNotice =
        "This operator attestation binds the exact current Finish proposal and displayed preview artifacts, permits only Finish Apply, and grants neither human-visual authority, game-runtime authority, nor promotion authority.";

    public static string FinishAnalyzeAuthorityNoticeSha256 { get; } =
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(FinishAnalyzeAuthorityNotice)));

    public static string FinishApplyAuthorityNoticeSha256 { get; } =
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(FinishApplyAuthorityNotice)));

    public static bool TryGetPurpose(
        string proposalArtifactKind,
        out string scope,
        out string authorityNoticeSha256)
    {
        switch (proposalArtifactKind)
        {
            case WorkflowArtifactKinds.NpcPackageManifest:
                scope = FinishAnalyzeScope;
                authorityNoticeSha256 =
                    FinishAnalyzeAuthorityNoticeSha256;
                return true;
            case WorkflowArtifactKinds.NpcFinishCoreProposal:
                scope = FinishApplyScope;
                authorityNoticeSha256 =
                    FinishApplyAuthorityNoticeSha256;
                return true;
            default:
                scope = string.Empty;
                authorityNoticeSha256 = string.Empty;
                return false;
        }
    }
}

public static class WorkflowArtifactKinds
{
    public const string ReviewedWorkspaceIntake = "reviewed-workspace-intake";
    public const string NpcBuildPreflight = "npc-build-preflight";
    public const string RaceMenuJslot = "racemenu-jslot";
    public const string NpcPackageManifest = "npc-package-manifest";
    public const string NpcPreviewManifest = "npc-preview-manifest";
    public const string NpcFinishCoreRequest = "npc-finish-core-request";
    public const string NpcFinishCoreProposal = "npc-finish-core-proposal";
    public const string NpcFinishCoreManifest = "npc-finish-core-manifest";
    public const string NpcFinishCoreVerification =
        "npc-finish-core-verification";
    public const string PackageArchive = "package-archive";
    public const string ReviewReceipt = "review-receipt";
    public const string RuntimeEvidence = "runtime-evidence";

    public static ImmutableArray<string> All { get; } =
    [
        NpcBuildPreflight,
        NpcFinishCoreManifest,
        NpcFinishCoreProposal,
        NpcFinishCoreRequest,
        NpcFinishCoreVerification,
        NpcPackageManifest,
        NpcPreviewManifest,
        PackageArchive,
        RaceMenuJslot,
        ReviewReceipt,
        ReviewedWorkspaceIntake,
        RuntimeEvidence
    ];

    public static ImmutableArray<string> Singular { get; } = All;
}

public sealed record WorkflowNpcIdentity(
    string EditorId,
    string? DisplayName,
    string? Plugin,
    string? LocalFormId);

public sealed record WorkflowArtifactBinding(
    string Kind,
    string SchemaOrMediaType,
    WorkspacePath Path,
    long Size,
    string Sha256,
    string ProducerCommand,
    string RequestDigest,
    ImmutableArray<string> InputArtifactHashes,
    string? SemanticSha256 = null);

public sealed record WorkflowAuthorityEvidence(
    AgentAuthorityKind Kind,
    AgentAuthorityState State,
    string Reason,
    ImmutableArray<string> ArtifactHashes);

public sealed record VerifiedWorkflowArtifact(
    string Kind,
    WorkspacePath Path,
    long Size,
    string Sha256,
    bool IndependentlyVerified);

public sealed record AgentWorkflowBundle(
    string Schema,
    string WorkflowKind,
    GameEdition Game,
    WorkflowNpcIdentity Npc,
    AgentWorkflowPhase Phase,
    string RequestDigest,
    ImmutableArray<WorkflowArtifactBinding> Artifacts,
    ImmutableArray<WorkflowAuthorityEvidence> Authority,
    ImmutableArray<ProtocolNextAction> NextActions);

public enum ReviewOutcome
{
    Accepted,
    Rejected,
    RevisionRequested
}

public sealed record AgentReviewReceipt(
    string Schema,
    string BundleSha256,
    string ProposalSha256,
    ImmutableArray<string> DisplayedArtifactHashes,
    string AuthorityNoticeSha256,
    ReviewOutcome Outcome,
    string Scope,
    string? ReviewerNote,
    string AttestationKind);

/// <summary>
/// Enforces the closed, hash-bound invariants shared by workflow codecs,
/// receipt persistence, and the future pure evaluator.
/// </summary>
public static class AgentWorkflowContractValidation
{
    public const string OperatorAttestationKind = "operator-attested";
    public const int MaximumReviewerNoteScalars = 1024;

    private static readonly ImmutableHashSet<string> KnownArtifactKinds =
        WorkflowArtifactKinds.All.ToImmutableHashSet(StringComparer.Ordinal);
    private static readonly ImmutableHashSet<string> SingularArtifactKinds =
        WorkflowArtifactKinds.Singular.ToImmutableHashSet(
            StringComparer.Ordinal);

    public static AgentReviewReceipt CreateReviewReceipt(
        string bundleSha256,
        string proposalSha256,
        ImmutableArray<string> displayedArtifactHashes,
        string authorityNoticeSha256,
        ReviewOutcome outcome,
        string scope,
        string? reviewerNote)
    {
        RequireHash(bundleSha256, nameof(bundleSha256));
        RequireHash(proposalSha256, nameof(proposalSha256));
        RequireHash(authorityNoticeSha256, nameof(authorityNoticeSha256));
        RequireHashSet(
            displayedArtifactHashes,
            nameof(displayedArtifactHashes),
            requireNonempty: true,
            requireSorted: false);

        var receipt = new AgentReviewReceipt(
            AgentWorkflowSchemas.ReviewReceiptV1,
            bundleSha256,
            proposalSha256,
            displayedArtifactHashes.Sort(StringComparer.Ordinal),
            authorityNoticeSha256,
            outcome,
            scope,
            reviewerNote,
            OperatorAttestationKind);
        Validate(receipt);
        return receipt;
    }

    public static void Validate(AgentWorkflowBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        RequireExact(
            bundle.Schema,
            AgentWorkflowSchemas.BundleV1,
            nameof(bundle.Schema));
        RequireExact(
            bundle.WorkflowKind,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            nameof(bundle.WorkflowKind));
        if (bundle.Game != GameEdition.SkyrimSpecialEdition)
        {
            throw new ArgumentException(
                "The golden workflow game must be Skyrim Special Edition.",
                nameof(bundle));
        }

        ArgumentNullException.ThrowIfNull(bundle.Npc);
        RequireNonempty(bundle.Npc.EditorId, nameof(bundle.Npc.EditorId));
        if (!Enum.IsDefined(bundle.Phase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bundle),
                bundle.Phase,
                "Workflow phase must be defined.");
        }
        RequireHash(bundle.RequestDigest, nameof(bundle.RequestDigest));
        RequireInitialized(bundle.Artifacts, nameof(bundle.Artifacts));
        RequireInitialized(bundle.Authority, nameof(bundle.Authority));
        RequireInitialized(bundle.NextActions, nameof(bundle.NextActions));

        var artifactKinds = new HashSet<string>(StringComparer.Ordinal);
        var artifactHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkflowArtifactBinding artifact in bundle.Artifacts)
        {
            Validate(artifact);
            if (!artifactKinds.Add(artifact.Kind) &&
                SingularArtifactKinds.Contains(artifact.Kind))
            {
                throw new ArgumentException(
                    $"Duplicate singular workflow artifact kind '{artifact.Kind}'.",
                    nameof(bundle));
            }
            if (!artifactHashes.Add(artifact.Sha256))
            {
                throw new ArgumentException(
                    $"Duplicate workflow artifact SHA-256 '{artifact.Sha256}'.",
                    nameof(bundle));
            }
        }

        foreach (WorkflowAuthorityEvidence evidence in bundle.Authority)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (!Enum.IsDefined(evidence.Kind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bundle),
                    evidence.Kind,
                    "Workflow authority kind must be defined.");
            }
            if (!Enum.IsDefined(evidence.State))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bundle),
                    evidence.State,
                    "Workflow authority state must be defined.");
            }
            RequireNonempty(evidence.Reason, nameof(evidence.Reason));
            RequireHashSet(
                evidence.ArtifactHashes,
                nameof(evidence.ArtifactHashes),
                requireNonempty: false,
                requireSorted: true);
        }

        foreach (ProtocolNextAction action in bundle.NextActions)
        {
            ArgumentNullException.ThrowIfNull(action);
            RequireNonempty(action.Command, nameof(action.Command));
            RequireNonempty(action.Reason, nameof(action.Reason));
            RequireInitialized(
                action.RequiredBindings,
                nameof(action.RequiredBindings));
            RequireInitialized(
                action.MissingPrerequisites,
                nameof(action.MissingPrerequisites));
            foreach (ProtocolNextActionBinding binding in
                     action.RequiredBindings)
            {
                ArgumentNullException.ThrowIfNull(binding);
                RequireNonempty(binding.Option, nameof(binding.Option));
                RequireNonempty(binding.Value, nameof(binding.Value));
                if (binding.ArtifactSha256 is not null)
                    RequireHash(
                        binding.ArtifactSha256,
                        nameof(binding.ArtifactSha256));
            }
        }
    }

    public static void Validate(WorkflowArtifactBinding artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireNonempty(artifact.Kind, nameof(artifact.Kind));
        if (!KnownArtifactKinds.Contains(artifact.Kind))
        {
            throw new ArgumentException(
                $"Unknown workflow artifact kind '{artifact.Kind}'.",
                nameof(artifact));
        }

        RequireNonempty(
            artifact.SchemaOrMediaType,
            nameof(artifact.SchemaOrMediaType));
        RequireWorkspacePath(artifact.Path, nameof(artifact));
        if (artifact.Size < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(artifact),
                artifact.Size,
                "Workflow artifact size cannot be negative.");
        }
        RequireHash(artifact.Sha256, nameof(artifact.Sha256));
        RequireNonempty(
            artifact.ProducerCommand,
            nameof(artifact.ProducerCommand));
        RequireHash(artifact.RequestDigest, nameof(artifact.RequestDigest));
        RequireHashSet(
            artifact.InputArtifactHashes,
            nameof(artifact.InputArtifactHashes),
            requireNonempty: false,
            requireSorted: true);
        if (string.Equals(
                artifact.Kind,
                WorkflowArtifactKinds.NpcFinishCoreProposal,
                StringComparison.Ordinal))
        {
            if (artifact.SemanticSha256 is null)
            {
                throw new ArgumentException(
                    "A Finish Core proposal requires a semantic SHA-256.",
                    nameof(artifact));
            }
            RequireHash(
                artifact.SemanticSha256,
                nameof(artifact.SemanticSha256));
        }
        else if (artifact.SemanticSha256 is not null)
        {
            throw new ArgumentException(
                "Only a Finish Core proposal may carry a semantic SHA-256.",
                nameof(artifact));
        }
    }

    public static void Validate(VerifiedWorkflowArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireNonempty(artifact.Kind, nameof(artifact.Kind));
        if (!KnownArtifactKinds.Contains(artifact.Kind))
        {
            throw new ArgumentException(
                $"Unknown verified workflow artifact kind '{artifact.Kind}'.",
                nameof(artifact));
        }
        RequireWorkspacePath(artifact.Path, nameof(artifact));
        if (artifact.Size < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(artifact),
                artifact.Size,
                "Verified workflow artifact size cannot be negative.");
        }
        RequireHash(artifact.Sha256, nameof(artifact.Sha256));
    }

    public static void Validate(AgentReviewReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireExact(
            receipt.Schema,
            AgentWorkflowSchemas.ReviewReceiptV1,
            nameof(receipt.Schema));
        RequireHash(receipt.BundleSha256, nameof(receipt.BundleSha256));
        RequireHash(receipt.ProposalSha256, nameof(receipt.ProposalSha256));
        RequireHashSet(
            receipt.DisplayedArtifactHashes,
            nameof(receipt.DisplayedArtifactHashes),
            requireNonempty: true,
            requireSorted: true);
        RequireHash(
            receipt.AuthorityNoticeSha256,
            nameof(receipt.AuthorityNoticeSha256));
        if (!Enum.IsDefined(receipt.Outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(receipt),
                receipt.Outcome,
                "Review outcome must be defined.");
        }
        RequireNonempty(receipt.Scope, nameof(receipt.Scope));
        RequireExact(
            receipt.AttestationKind,
            OperatorAttestationKind,
            nameof(receipt.AttestationKind));
        if (receipt.ReviewerNote is not null)
        {
            if (receipt.ReviewerNote.Contains('\r'))
            {
                throw new ArgumentException(
                    "Reviewer note cannot contain carriage returns.",
                    nameof(receipt));
            }
            if (CountUnicodeScalars(
                    receipt.ReviewerNote,
                    nameof(receipt)) >
                MaximumReviewerNoteScalars)
            {
                throw new ArgumentException(
                    "Reviewer note exceeds 1024 Unicode scalar values.",
                    nameof(receipt));
            }
        }
    }

    private static void RequireHashSet(
        ImmutableArray<string> hashes,
        string parameterName,
        bool requireNonempty,
        bool requireSorted)
    {
        RequireInitialized(hashes, parameterName);
        if (requireNonempty && hashes.IsEmpty)
        {
            throw new ArgumentException(
                "At least one SHA-256 is required.",
                parameterName);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (string hash in hashes)
        {
            RequireHash(hash, parameterName);
            if (!seen.Add(hash))
            {
                throw new ArgumentException(
                    $"Duplicate SHA-256 '{hash}'.",
                    parameterName);
            }
            if (requireSorted &&
                previous is not null &&
                StringComparer.Ordinal.Compare(previous, hash) >= 0)
            {
                throw new ArgumentException(
                    "SHA-256 values must be ordinally sorted.",
                    parameterName);
            }
            previous = hash;
        }
    }

    private static void RequireHash(string value, string parameterName)
    {
        if (value is null || value.Length != 64)
        {
            throw new ArgumentException(
                "Value must be an uppercase SHA-256.",
                parameterName);
        }
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F')))
            {
                throw new ArgumentException(
                    "Value must be an uppercase SHA-256.",
                    parameterName);
            }
        }
    }

    private static int CountUnicodeScalars(
        string value,
        string parameterName)
    {
        int scalarCount = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length ||
                    !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException(
                        "Reviewer note must contain well-formed UTF-16.",
                        parameterName);
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new ArgumentException(
                    "Reviewer note must contain well-formed UTF-16.",
                    parameterName);
            }

            scalarCount++;
        }

        return scalarCount;
    }

    private static void RequireWorkspacePath(
        WorkspacePath path,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            throw new ArgumentException(
                "Workspace path must be structurally initialized.",
                parameterName);
        }
    }

    private static void RequireExact(
        string value,
        string expected,
        string parameterName)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Value must be exactly '{expected}'.",
                parameterName);
        }
    }

    private static void RequireNonempty(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Value must be nonempty.",
                parameterName);
        }
    }

    private static void RequireInitialized<T>(
        ImmutableArray<T> values,
        string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException(
                "Immutable array must be initialized.",
                parameterName);
        }
    }
}
