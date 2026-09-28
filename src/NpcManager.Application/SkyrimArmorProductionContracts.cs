using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimArmorProductionReadRequest(
    ReviewedGameIntake Intake,
    PluginName SourcePlugin,
    FormId SourceFormId,
    FormId NewTargetFormId);

public sealed record SkyrimArmorAddonProductionCatalogEntry(
    SkyrimArmorAddonReferenceCandidate Candidate,
    PluginName Provider,
    SkyrimArmorAddonEditorDocument? EditableDocument);

public sealed record SkyrimArmorProductionSource(
    ReviewedGameIntake Intake,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath SourcePluginPath,
    Sha256Hash SourcePluginSha256,
    FormReference SourceReference,
    SkyrimArmorEditorDocument BlankNew,
    SkyrimArmorEditorDocument NewFromTemplate,
    SkyrimArmorEditorDocument OverrideExisting,
    ImmutableArray<EditorId> ExistingEditorIds,
    ImmutableArray<SkyrimArmorAddonSlotEvidence> ArmorAddonSlotEvidence,
    ImmutableArray<SkyrimArmorAddonProductionCatalogEntry> ArmorAddonCatalog,
    ImmutableArray<EditorId> ExistingArmorAddonEditorIds);

public sealed record SkyrimArmorProductionReadResult(
    bool Accepted,
    SkyrimArmorProductionSource? Source,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimArmorProductionReader
{
    SkyrimArmorProductionReadResult Read(
        SkyrimArmorProductionReadRequest request);
}

public sealed record SkyrimArmorProductionRequest(
    SkyrimArmorProductionSource Source,
    SkyrimArmorEditorDocument Document,
    WorkspacePath OutputProposal,
    WorkspacePath OutputPlugin,
    ImmutableArray<SkyrimArmorAddonProductionRequest> ArmorAddonOutputs = default);

public sealed record SkyrimArmorProductionProposal(
    SkyrimArmorProductionRequest Request,
    ArmorProposalArtifact? Artifact,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<SkyrimArmorAddonProductionProposal> ArmorAddonProposals = default)
{
    public bool IsApplicable => Artifact is not null && ProposalSha256 is not null &&
                                !Diagnostics.Any(item =>
                                    item.Severity == DiagnosticSeverity.Error) &&
                                (ArmorAddonProposals.IsDefaultOrEmpty ||
                                 ArmorAddonProposals.All(item =>
                                     item.IsApplicable));
}

public sealed record SkyrimArmorProductionVerification(
    bool IsValid,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    int ArmorRecordCount,
    int OtherRecordCount,
    PluginName? OwnerPlugin,
    FormId? TargetFormId,
    bool OwnerMatches,
    bool EditorIdMatches,
    bool DocumentMatches,
    bool MasterSetMatches,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<SkyrimArmorAddonProductionVerification> ArmorAddonVerifications = default);

public interface ISkyrimArmorProductionOutputReader
{
    ValueTask<SkyrimArmorProductionVerification> ReadAsync(
        ArmorProposalArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);
}

public sealed record SkyrimArmorProductionResult(
    bool Applied,
    SkyrimArmorProductionProposal Proposal,
    ArmorBinaryWriteResult? Write,
    SkyrimArmorProductionVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimArmorProductionTransactionService
{
    ValueTask<SkyrimArmorProductionProposal> AnalyzeAsync(
        SkyrimArmorProductionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimArmorProductionResult> ApplyAsync(
        SkyrimArmorProductionRequest request,
        SkyrimArmorProductionProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SkyrimArmorProductionVerification> VerifyAsync(
        SkyrimArmorProductionRequest request,
        SkyrimArmorProductionProposal proposal,
        CancellationToken cancellationToken);
}
