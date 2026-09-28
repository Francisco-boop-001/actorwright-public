using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record DesktopWorkflowLaunchBinding(
    WorkspacePath Bundle,
    string BundleSha256);

public sealed record DesktopWorkflowArtifactPresentation(
    string Kind,
    string SchemaOrMediaType,
    WorkspacePath Path,
    long Size,
    string Sha256,
    string ProducerCommand);

public sealed record DesktopWorkflowProposalPresentation(
    string Kind,
    WorkspacePath Path,
    string Sha256,
    string Title,
    ImmutableArray<string> Changes);

public sealed record DesktopWorkflowPreviewViewPresentation(
    string Id,
    WorkspacePath ImagePath,
    string ImageSha256,
    WorkspacePath RoleMaskPath,
    string RoleMaskSha256,
    int Width,
    int Height);

public sealed record DesktopWorkflowPreviewPresentation(
    WorkspacePath ManifestPath,
    string ManifestSha256,
    string Label,
    WorkspacePath ContactSheetPath,
    string ContactSheetSha256,
    ImmutableArray<byte> ContactSheetBytes,
    ImmutableArray<DesktopWorkflowPreviewViewPresentation> Views);

public sealed record DesktopWorkflowReviewSnapshot(
    string NpcEditorId,
    string? NpcDisplayName,
    string Game,
    AgentWorkflowPhase Phase,
    WorkspacePath BundlePath,
    string BundleSha256,
    ImmutableArray<DesktopWorkflowArtifactPresentation> Artifacts,
    ImmutableArray<WorkflowAuthorityEvidence> Authority,
    ImmutableArray<ProtocolDiagnostic> Diagnostics,
    ImmutableArray<ProtocolNextAction> NextActions,
    DesktopWorkflowProposalPresentation Proposal,
    DesktopWorkflowPreviewPresentation Preview,
    string ReviewScope,
    string AuthorityNotice,
    string AuthorityNoticeSha256);

public sealed record DesktopWorkflowReviewLoadResult(
    bool Loaded,
    DesktopWorkflowReviewSnapshot? Snapshot,
    ImmutableArray<ProtocolDiagnostic> Diagnostics);

public sealed record DesktopWorkflowReviewReceiptResult(
    bool Created,
    ReviewOutcome? Outcome,
    WorkspacePath? Path,
    string? Sha256,
    WorkflowArtifactBinding? Artifact,
    ImmutableArray<ProtocolDiagnostic> Diagnostics);

public interface IDesktopWorkflowReviewService
{
    ValueTask<DesktopWorkflowReviewLoadResult> LoadAsync(
        DesktopWorkflowLaunchBinding binding,
        CancellationToken cancellationToken);

    ValueTask<DesktopWorkflowReviewReceiptResult> CreateReceiptAsync(
        DesktopWorkflowLaunchBinding binding,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output,
        CancellationToken cancellationToken);

    ValueTask<DesktopWorkflowReviewReceiptResult> ReopenReceiptAsync(
        DesktopWorkflowLaunchBinding binding,
        WorkspacePath receipt,
        string receiptSha256,
        CancellationToken cancellationToken);
}
