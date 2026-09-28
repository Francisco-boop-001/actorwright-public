using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SseFaceGeomCarrierMaterializationAnalyzeRequest(
    SseFaceGeomCarrierAssemblyRequest Assembly,
    WorkspacePath OutputNif);

/// <summary>
/// Two-pass, exact-byte proposal. Apply must rebuild the carrier from the same
/// hash-bound inputs and match this predicted artifact before it may write.
/// </summary>
public sealed record SseFaceGeomCarrierMaterializationProposal(
    string Operation,
    SseFaceGeomCarrierAssemblyRequest Assembly,
    WorkspacePath OutputNif,
    SseFaceGeomCarrierAssemblyArtifact PredictedArtifact);

public sealed record SseFaceGeomCarrierMaterializationAnalysisResult(
    bool Accepted,
    SseFaceGeomCarrierMaterializationProposal? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SseFaceGeomCarrierMaterializationArtifact(
    SseFaceGeomCarrierMaterializationProposal Proposal,
    WorkspacePath OutputNif,
    Sha256Hash OutputSha256,
    int OutputByteLength,
    int BlockCount,
    int ShapeCount,
    bool RuntimeAuthority);

public sealed record SseFaceGeomCarrierMaterializationVerificationResult(
    bool Verified,
    WorkspacePath OutputNif,
    Sha256Hash? OutputSha256,
    int? OutputByteLength,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SseFaceGeomCarrierMaterializationResult(
    bool Written,
    bool Verified,
    SseFaceGeomCarrierMaterializationArtifact? Artifact,
    SseFaceGeomCarrierMaterializationVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseFaceGeomCarrierMaterializationService
{
    ValueTask<SseFaceGeomCarrierMaterializationAnalysisResult> AnalyzeAsync(
        SseFaceGeomCarrierMaterializationAnalyzeRequest request,
        CancellationToken cancellationToken);

    ValueTask<SseFaceGeomCarrierMaterializationResult> ApplyAsync(
        SseFaceGeomCarrierMaterializationProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SseFaceGeomCarrierMaterializationVerificationResult> VerifyAsync(
        SseFaceGeomCarrierMaterializationProposal proposal,
        CancellationToken cancellationToken);
}
