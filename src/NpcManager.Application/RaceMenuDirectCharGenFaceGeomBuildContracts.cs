using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Binds a RaceMenu CharGen export to a qualified complete carrier. Name- and
/// topology-matched CharGen shapes own XYZ; every absent shape is explicitly
/// preserved from the exact carrier. A complete all-dynamic CharGen export is
/// admitted when every shape matches the carrier.
/// </summary>
public sealed record RaceMenuDirectCharGenFaceGeomBuildRequest(
    WorkspacePath CharGenNif,
    Sha256Hash ExpectedCharGenSha256,
    WorkspacePath CompleteCarrierNif,
    Sha256Hash ExpectedCompleteCarrierSha256,
    WorkspacePath OutputNif)
{
    /// <summary>
    /// Explicit carrier envelope. The default preserves the pinned seven-shape
    /// provider behavior; only Manager-created, hash-reopened companions select
    /// the Manager-assembled profile.
    /// </summary>
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

public sealed record RaceMenuDirectCharGenFaceGeomBuildResult(
    bool Written,
    bool Verified,
    RaceMenuCharGenFaceGeomMergeArtifact? Artifact,
    RaceMenuCharGenFaceGeomMergeVerificationResult? IndependentVerification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuDirectCharGenFaceGeomBuildService
{
    ValueTask<RaceMenuDirectCharGenFaceGeomBuildResult> BuildAsync(
        RaceMenuDirectCharGenFaceGeomBuildRequest request,
        CancellationToken cancellationToken);
}
