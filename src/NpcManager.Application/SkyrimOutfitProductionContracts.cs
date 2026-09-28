using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimOutfitItemCatalogRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    long InitialSeed);

public sealed record SkyrimOutfitPreviewResolveRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference LeveledList,
    long Seed);

public sealed record SkyrimOutfitItemCatalogResult(
    bool Accepted,
    ImmutableArray<SkyrimOutfitEditorItem> Items,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimOutfitItemCatalogReader
{
    SkyrimOutfitItemCatalogResult Read(SkyrimOutfitItemCatalogRequest request);

    ImmutableArray<SkyrimOutfitPreviewArmor> Resolve(
        SkyrimOutfitPreviewResolveRequest request);
}

public sealed record SkyrimOutfitProductionLoadRequest(
    ReviewedGameIntake Intake,
    PluginName TemplatePlugin,
    FormId TemplateFormId,
    FormId NewTargetFormId,
    long InitialPreviewSeed);

public sealed record SkyrimOutfitProductionState(
    ReviewedGameIntake Intake,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath TemplatePluginPath,
    Sha256Hash TemplatePluginSha256,
    FormId TemplateFormId,
    FormId NewTargetFormId,
    long InitialPreviewSeed,
    ImmutableArray<OutfitChoiceCandidate> Outfits,
    ImmutableArray<SkyrimOutfitEditorItem> Items);

public sealed record SkyrimOutfitProductionLoadResult(
    bool Accepted,
    SkyrimOutfitProductionState? State,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimOutfitProductionLoadService
{
    ValueTask<SkyrimOutfitProductionLoadResult> LoadAsync(
        SkyrimOutfitProductionLoadRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimOutfitProductionTransactionRequest(
    OutfitProposalRequest OutfitProposal,
    WorkspacePath OutputPlugin);

public sealed record SkyrimOutfitProductionProposal(
    SkyrimOutfitProductionTransactionRequest Request,
    OutfitProposalArtifact? Artifact,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => Artifact is not null && ProposalSha256 is not null &&
                                !Diagnostics.Any(item =>
                                    item.Severity == DiagnosticSeverity.Error);
}

public sealed record SkyrimOutfitProductionVerification(
    bool IsValid,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    int RecordCount,
    PluginName? OwnerPlugin,
    FormId? TargetFormId,
    bool OwnerMatches,
    bool EditorIdMatches,
    bool OrderedItemsMatch,
    bool MasterSetMatches,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimOutfitProductionResult(
    bool Applied,
    SkyrimOutfitProductionProposal Proposal,
    OutfitBinaryWriteResult? Write,
    SkyrimOutfitProductionVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimOutfitProductionTransactionService
{
    ValueTask<SkyrimOutfitProductionProposal> AnalyzeAsync(
        SkyrimOutfitProductionTransactionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimOutfitProductionResult> ApplyAsync(
        SkyrimOutfitProductionTransactionRequest request,
        SkyrimOutfitProductionProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SkyrimOutfitProductionVerification> VerifyAsync(
        SkyrimOutfitProductionTransactionRequest request,
        SkyrimOutfitProductionProposal proposal,
        CancellationToken cancellationToken);
}
