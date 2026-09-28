using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimLeveledListProductionLoadRequest(
    ReviewedGameIntake Intake,
    long InitialPreviewSeed);

public sealed record SkyrimLeveledListProductionState(
    ReviewedGameIntake Intake,
    ImmutableArray<PluginName> PluginOrder,
    ImmutableArray<SkyrimOutfitEditorItem> EntryCandidates,
    ImmutableArray<EditorId> ExistingEditorIds);

public sealed record SkyrimLeveledListProductionLoadResult(
    bool Accepted,
    SkyrimLeveledListProductionState? State,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimLeveledListProductionLoadService
{
    ValueTask<SkyrimLeveledListProductionLoadResult> LoadAsync(
        SkyrimLeveledListProductionLoadRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimLeveledListProductionRequest(
    SkyrimLeveledListProductionState State,
    FormId TargetFormId,
    SkyrimLeveledListEditorDocument Document,
    WorkspacePath OutputProposal,
    WorkspacePath OutputPlugin);

public sealed record SkyrimLeveledListReviewedInputArtifact(
    string Plugin,
    string Path,
    string Sha256);

public sealed record SkyrimLeveledListProductionEntryArtifact(
    string Item,
    ushort Level,
    ushort Count,
    byte ChanceNone);

public sealed record SkyrimLeveledListProductionArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string OutputPlugin,
    string TargetFormId,
    string EditorId,
    byte ChanceNone,
    byte MaxCount,
    bool CalculateAllLevels,
    bool CalculateEachInCount,
    bool UseAll,
    ImmutableArray<SkyrimLeveledListProductionEntryArtifact> Entries,
    ImmutableArray<string> MasterDependencies,
    ImmutableArray<SkyrimLeveledListReviewedInputArtifact> ReviewedInputs,
    bool NewSelfOwnedRecord,
    bool NoUnrelatedRecords);

public sealed record SkyrimLeveledListProductionProposal(
    SkyrimLeveledListProductionRequest Request,
    SkyrimLeveledListProductionArtifact? Artifact,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => Artifact is not null && ProposalSha256 is not null &&
                                !Diagnostics.Any(item =>
                                    item.Severity == DiagnosticSeverity.Error);
}

public sealed record SkyrimLeveledListProductionWriteResult(
    bool Written,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimLeveledListProductionBinaryWriter
{
    ValueTask<SkyrimLeveledListProductionWriteResult> WriteAsync(
        SkyrimLeveledListProductionArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);
}

public interface ISkyrimLeveledListProductionOutputReader
{
    ValueTask<SkyrimLeveledListProductionVerification> ReadAsync(
        SkyrimLeveledListProductionArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);
}

public sealed record SkyrimLeveledListProductionVerification(
    bool IsValid,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    int LeveledListRecordCount,
    int OtherRecordCount,
    PluginName? OwnerPlugin,
    FormId? TargetFormId,
    bool SelfOwned,
    bool HeaderMatches,
    bool EntriesMatch,
    bool MasterSetMatches,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimLeveledListProductionResult(
    bool Applied,
    SkyrimLeveledListProductionProposal Proposal,
    SkyrimLeveledListProductionWriteResult? Write,
    SkyrimLeveledListProductionVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimLeveledListProductionTransactionService
{
    ValueTask<SkyrimLeveledListProductionProposal> AnalyzeAsync(
        SkyrimLeveledListProductionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimLeveledListProductionResult> ApplyAsync(
        SkyrimLeveledListProductionRequest request,
        SkyrimLeveledListProductionProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SkyrimLeveledListProductionVerification> VerifyAsync(
        SkyrimLeveledListProductionRequest request,
        SkyrimLeveledListProductionProposal proposal,
        CancellationToken cancellationToken);
}
