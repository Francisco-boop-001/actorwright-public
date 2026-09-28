using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum RecordProposalMode
{
    New,
    Template,
    Override
}

public sealed record RecordProposalRequest(
    GameEdition Edition,
    RecordSignature Signature,
    RecordProposalMode Mode,
    FormId FormId,
    FormId? SourceFormId,
    EditorId EditorId,
    string? Name,
    ImmutableArray<PluginName> MasterDependencies,
    WorkspacePath Output,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HeadPartComposition? HeadPart = null);

public sealed record HeadPartComposition(
    string? Model, string? TriRace, string? TriChargen, string? TriDialogue,
    string? ValidRaces, ImmutableArray<string> ExtraParts, byte Flags,
    NpcHeadPartType PartType, string? CloneFrom = null);

public sealed record RecordProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    RecordProposalMode Mode,
    string Signature,
    string FormId,
    string? SourceFormId,
    string EditorId,
    string? Name,
    ImmutableArray<string> MasterDependencies,
    string AllocationStrategy,
    string RequestSha256,
    bool NoUnrelatedRecords,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HeadPartComposition? HeadPart = null);

public sealed record RecordProposalResult(
    bool Written,
    RecordProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRecordProposalService
{
    ValueTask<RecordProposalResult> ProposeAsync(RecordProposalRequest request, CancellationToken cancellationToken);
}
