using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ObjectTemplateProposalMode
{
    New,
    Override
}

public sealed record ObjectTemplateIncludeProposal(FormReference Mod, byte AttachPointIndex, bool IsOptional, bool DontUseAll);

public sealed record ObjectTemplateCombinationProposal(string? DisplayName, bool IsDefault, bool IsEditorOnly,
    short? ParentCombinationIndex, byte LevelMin, byte LevelMax, byte MinLevelForRanks, byte AltLevelsPerTier,
    ImmutableArray<FormReference> Keywords, ImmutableArray<ObjectTemplateIncludeProposal> Includes);

public sealed record ObjectTemplateProposalPatch(EditorId? EditorId, ImmutableArray<ObjectTemplateCombinationProposal> Combinations,
    FormId? TargetFormId = null);

public sealed record ObjectTemplateProposalRequest(GameEdition Edition, WorkspacePath SourcePlugin, FormId SourceFormId,
    ObjectTemplateProposalMode Mode, ObjectTemplateProposalPatch Patch, WorkspacePath OutputProposal);

public sealed record ObjectTemplateIncludeArtifact(string Mod, byte AttachPointIndex, bool IsOptional, bool DontUseAll);

public sealed record ObjectTemplateCombinationArtifact(string? DisplayName, bool IsDefault, bool IsEditorOnly,
    short? ParentCombinationIndex, byte LevelMin, byte LevelMax, byte MinLevelForRanks, byte AltLevelsPerTier,
    ImmutableArray<string> Keywords, ImmutableArray<ObjectTemplateIncludeArtifact> Includes);

public sealed record ObjectTemplateProposalArtifact(string SchemaVersion, string ArtifactKind, string Edition,
    ObjectTemplateProposalMode Mode, string SourcePlugin, string SourceFormId, string EditorId, string InputSha256,
    string PatchSha256, ImmutableArray<ObjectTemplateCombinationArtifact> Combinations,
    ImmutableArray<string> MasterDependencies, bool NoUnrelatedRecords, string? TargetFormId = null);

public sealed record ObjectTemplateProposalResult(bool Written, ObjectTemplateProposalArtifact? Artifact,
    Sha256Hash? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

public interface IObjectTemplateProposalService
{
    ValueTask<ObjectTemplateProposalResult> ProposeAsync(ObjectTemplateProposalRequest request,
        CancellationToken cancellationToken);
}
