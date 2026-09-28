using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum MaterialSwapProposalMode
{
    New,
    Override
}

public sealed record MaterialSwapEntryProposal(string OriginalMaterial, string ReplacementMaterial, double? ColorRemapIndex, string? TreeFolder);

public sealed record MaterialSwapProposalPatch(EditorId? EditorId, string? TreeFolder, ImmutableArray<MaterialSwapEntryProposal> Entries);

public sealed record MaterialSwapProposalRequest(GameEdition Edition, WorkspacePath SourcePlugin, FormId SourceFormId,
    MaterialSwapProposalMode Mode, MaterialSwapProposalPatch Patch, WorkspacePath OutputProposal,
    FormId? TargetFormId = null);

public sealed record MaterialSwapEntryArtifact(string OriginalMaterial, string ReplacementMaterial, double? ColorRemapIndex, string? TreeFolder);

public sealed record MaterialSwapProposalArtifact(string SchemaVersion, string ArtifactKind, string Edition,
    MaterialSwapProposalMode Mode, string SourcePlugin, string SourceFormId, string EditorId, string InputSha256,
    string PatchSha256, string? TreeFolder, ImmutableArray<MaterialSwapEntryArtifact> Entries,
    bool NoUnrelatedRecords, string? TargetFormId = null);

public sealed record MaterialSwapProposalResult(bool Written, MaterialSwapProposalArtifact? Artifact,
    Sha256Hash? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

public interface IMaterialSwapProposalService
{
    ValueTask<MaterialSwapProposalResult> ProposeAsync(MaterialSwapProposalRequest request,
        CancellationToken cancellationToken);
}
