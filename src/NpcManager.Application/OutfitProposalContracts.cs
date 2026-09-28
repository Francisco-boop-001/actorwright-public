using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum OutfitProposalMode
{
    New,
    Override
}

public sealed record OutfitProposalRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    OutfitProposalMode Mode,
    EditorId? EditorId,
    ImmutableArray<FormReference> Items,
    WorkspacePath OutputProposal,
    FormId? TargetFormId = null,
    FormReference? ActorRace = null);

public sealed record OutfitProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    OutfitProposalMode Mode,
    string SourcePlugin,
    string SourceFormId,
    string EditorId,
    string InputSha256,
    ImmutableArray<string> Items,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords,
    string? TargetFormId = null,
    string? SourceOwnerPlugin = null);

public sealed record OutfitProposalResult(
    bool Written,
    OutfitProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IOutfitProposalService
{
    ValueTask<OutfitProposalResult> ProposeAsync(OutfitProposalRequest request,
        CancellationToken cancellationToken);
}
