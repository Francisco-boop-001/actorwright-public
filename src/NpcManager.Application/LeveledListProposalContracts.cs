using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record LeveledListEntryProposal(
    FormReference Item,
    ushort Level,
    ushort Count,
    byte ChanceNone);

public sealed record LeveledListEntryArtifact(
    string Item,
    ushort Level,
    ushort Count,
    byte ChanceNone);

public sealed record LeveledListProposalRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId ListFormId,
    EditorId? EditorId,
    byte ChanceNone,
    byte MaxCount,
    bool CalculateAllLevels,
    bool CalculateEachInCount,
    bool UseAll,
    ImmutableArray<LeveledListEntryProposal> Entries,
    WorkspacePath OutputProposal);

public sealed record LeveledListProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string SourcePlugin,
    string ListFormId,
    string InputSha256,
    string? EditorId,
    byte ChanceNone,
    byte MaxCount,
    bool CalculateAllLevels,
    bool CalculateEachInCount,
    bool UseAll,
    ImmutableArray<LeveledListEntryArtifact> Entries,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords);

public sealed record LeveledListProposalResult(
    bool Written,
    LeveledListProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ILeveledListProposalService
{
    ValueTask<LeveledListProposalResult> ProposeAsync(LeveledListProposalRequest request,
        CancellationToken cancellationToken);
}
