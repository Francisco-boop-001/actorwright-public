using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimCharGenOptionsProductionReviewRequest(
    WorkspacePath SourceOptions,
    Sha256Hash SourceOptionsSha256,
    CharGenOptions AcceptedOptions,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference Npc,
    NpcSex ExpectedSex,
    FormReference ExpectedRace,
    WorkspacePath OutputRoot,
    WorkspacePath OutputProposal);

public sealed record SkyrimCharGenOptionsProductionProposal(
    string SchemaVersion,
    string ArtifactKind,
    string SourceOptions,
    string SourceOptionsSha256,
    CharGenOptions AcceptedOptions,
    string DataRoot,
    ImmutableArray<string> PluginOrder,
    string Npc,
    string ExpectedSex,
    string ExpectedRace,
    string OutputRoot,
    bool RuntimeAuthority);

public sealed record SkyrimCharGenOptionsProductionReviewResult(
    bool Reviewed,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    SkyrimCharGenOptionsProductionProposal? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimCharGenOptionsProductionApplyRequest(
    WorkspacePath ProposalPath,
    Sha256Hash ExpectedProposalSha256);

public sealed record SkyrimCharGenOptionsProductionApplyResult(
    bool Applied,
    Sha256Hash? ProposalSha256,
    FaceGenOptionsDocumentWriteResult? OptionsWrite,
    SkyrimCharGenFaceTintBakeResult? FaceTintBake,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimCharGenOptionsProductionService
{
    ValueTask<SkyrimCharGenOptionsProductionReviewResult> ReviewAsync(
        SkyrimCharGenOptionsProductionReviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimCharGenOptionsProductionApplyResult> ApplyAsync(
        SkyrimCharGenOptionsProductionApplyRequest request,
        CancellationToken cancellationToken);
}
