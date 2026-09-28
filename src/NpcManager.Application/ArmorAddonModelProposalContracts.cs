using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ArmorAddonModelEntryProposal(ushort Index, FormReference Addon);

public sealed record ArmorAddonModelProposalRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    ImmutableArray<ArmorAddonModelEntryProposal> Entries,
    WorkspacePath OutputProposal);

public sealed record ArmorAddonModelProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string SourcePlugin,
    string SourceFormId,
    string InputSha256,
    ImmutableArray<ArmorAddonModelEntryArtifact> Entries,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords);

public sealed record ArmorAddonModelEntryArtifact(ushort Index, string Addon);

public sealed record ArmorAddonModelProposalResult(
    bool Written,
    ArmorAddonModelProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorAddonModelProposalService
{
    ValueTask<ArmorAddonModelProposalResult> ProposeAsync(ArmorAddonModelProposalRequest request,
        CancellationToken cancellationToken);
}
