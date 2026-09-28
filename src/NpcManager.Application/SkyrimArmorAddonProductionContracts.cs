using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimArmorAddonProductionRequest(
    ArmorAddonProposalRequest ProposalRequest,
    Sha256Hash ReviewedSourceSha256,
    WorkspacePath OutputPlugin);

public sealed record SkyrimArmorAddonProductionProposal(
    SkyrimArmorAddonProductionRequest Request,
    ArmorAddonProposalArtifact? Artifact,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => Artifact is not null &&
                                ProposalSha256 is not null &&
                                !Diagnostics.Any(item =>
                                    item.Severity == DiagnosticSeverity.Error);
}

public sealed record SkyrimArmorAddonProductionVerification(
    bool IsValid,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    int ArmorAddonRecordCount,
    int OtherRecordCount,
    PluginName? ProviderPlugin,
    PluginName? OwnerPlugin,
    FormId? TargetFormId,
    bool OwnerMatches,
    bool EditorIdMatches,
    bool DocumentMatches,
    bool MasterSetMatches,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimArmorAddonProductionOutputReader
{
    ValueTask<SkyrimArmorAddonProductionVerification> ReadAsync(
        ArmorAddonProposalArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);
}

public sealed record SkyrimArmorAddonProductionResult(
    bool Applied,
    SkyrimArmorAddonProductionProposal Proposal,
    ArmorAddonBinaryWriteResult? Write,
    SkyrimArmorAddonProductionVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimArmorAddonProductionTransactionService
{
    ValueTask<SkyrimArmorAddonProductionProposal> AnalyzeAsync(
        SkyrimArmorAddonProductionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimArmorAddonProductionResult> ApplyAsync(
        SkyrimArmorAddonProductionRequest request,
        SkyrimArmorAddonProductionProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SkyrimArmorAddonProductionVerification> VerifyAsync(
        SkyrimArmorAddonProductionRequest request,
        SkyrimArmorAddonProductionProposal proposal,
        CancellationToken cancellationToken);
}
