using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PreviewRerollRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    FormId NpcFormId,
    long Seed);

public sealed record PreviewRerollCandidate(
    string Id,
    string Outfit,
    int ManifestIndex,
    bool Eligible);

public sealed record PreviewRerollArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    long Seed,
    string InputManifestSha256,
    int CandidateCount,
    int EligibleCount,
    int? SelectedIndex,
    string? SelectedVariantId,
    string? SelectedOutfit,
    ImmutableArray<PreviewRerollCandidate> Candidates);

public sealed record PreviewRerollResult(
    bool Written,
    PreviewRerollArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewRerollService
{
    ValueTask<PreviewRerollResult> RerollAsync(PreviewRerollRequest request,
        CancellationToken cancellationToken);
}
