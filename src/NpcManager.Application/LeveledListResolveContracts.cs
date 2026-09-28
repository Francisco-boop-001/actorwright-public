using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record LeveledListResolveRequest(
    GameEdition Edition,
    WorkspacePath ListProposal,
    long Seed,
    WorkspacePath OutputResolution);

public sealed record LeveledListResolutionSelection(
    string Item,
    ushort Level,
    ushort Count,
    byte ChanceNone,
    int? ChanceRoll,
    bool Included,
    int? SelectedIndex,
    int RepeatOrdinal);

public sealed record LeveledListResolveArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string SourceProposal,
    string InputSha256,
    string ListFormId,
    string? EditorId,
    long Seed,
    byte ChanceNone,
    byte MaxCount,
    bool CalculateAllLevels,
    bool CalculateEachInCount,
    bool UseAll,
    bool LevelGateApplied,
    string LevelGateNote,
    int? ListChanceRoll,
    bool ListSuppressed,
    ImmutableArray<LeveledListResolutionSelection> Selections,
    ImmutableArray<string> ResolvedItems);

public sealed record LeveledListResolveResult(
    bool Written,
    LeveledListResolveArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ILeveledListResolveService
{
    ValueTask<LeveledListResolveResult> ResolveAsync(LeveledListResolveRequest request,
        CancellationToken cancellationToken);
}
