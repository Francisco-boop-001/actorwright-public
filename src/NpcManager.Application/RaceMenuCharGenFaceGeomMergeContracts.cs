using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum RaceMenuCharGenFaceGeomShapeRoute
{
    CharGenXyz,
    ExternalXyzOracle,
    CarrierPreserved,
    GeneratedXyz
}

/// <summary>Explicit authority for one required complete-carrier shape.</summary>
public abstract record RaceMenuCharGenFaceGeomShapeAuthority(
    string CarrierShapeName,
    string Reason);

/// <summary>The name-matched CharGen shape owns the carrier's XYZ lanes.</summary>
public sealed record RaceMenuCharGenFaceGeomCharGenXyzAuthority(
    string CarrierShapeName,
    Sha256Hash ExpectedTopologySha256,
    string Reason) : RaceMenuCharGenFaceGeomShapeAuthority(CarrierShapeName, Reason);

/// <summary>A hash-bound packed float32-le XYZ array owns the carrier's XYZ lanes.</summary>
public sealed record RaceMenuCharGenFaceGeomExternalXyzAuthority(
    string CarrierShapeName,
    WorkspacePath XyzFile,
    Sha256Hash ExpectedXyzSha256,
    int ExpectedVertexCount,
    Sha256Hash ExpectedTopologySha256,
    string Reason) : RaceMenuCharGenFaceGeomShapeAuthority(CarrierShapeName, Reason);

/// <summary>
/// A product-compiled, hash-bound packed float32-le XYZ array owns the
/// carrier's XYZ lanes. This route is deliberately distinct from an external
/// or finished-state oracle.
/// </summary>
public sealed record RaceMenuCharGenFaceGeomGeneratedXyzAuthority(
    string CarrierShapeName,
    WorkspacePath GeneratedXyzFile,
    Sha256Hash ExpectedGeneratedXyzSha256,
    int ExpectedVertexCount,
    Sha256Hash ExpectedTopologySha256,
    string Reason) : RaceMenuCharGenFaceGeomShapeAuthority(CarrierShapeName, Reason);

/// <summary>
/// Explicit fallback that preserves the carrier XYZ lanes. It is never selected
/// automatically and binds the exact pre-merge position hash.
/// </summary>
public sealed record RaceMenuCharGenFaceGeomCarrierPreservedAuthority(
    string CarrierShapeName,
    Sha256Hash ExpectedCarrierPositionSha256,
    Sha256Hash ExpectedTopologySha256,
    string Reason) : RaceMenuCharGenFaceGeomShapeAuthority(CarrierShapeName, Reason);

/// <summary>
/// Requests a two-pass XYZ merge of one incomplete, hash-bound RaceMenu CharGen
/// NIF into a complete, hash-bound Skyrim SE FaceGeom carrier. Every carrier
/// shape must have exactly one explicit authority row.
/// </summary>
public sealed record RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
    GameEdition Edition,
    WorkspacePath CharGenNif,
    Sha256Hash ExpectedCharGenSha256,
    WorkspacePath CarrierNif,
    Sha256Hash ExpectedCarrierSha256,
    WorkspacePath OutputNif,
    ImmutableArray<RaceMenuCharGenFaceGeomShapeAuthority> ShapeAuthorities)
{
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

public abstract record RaceMenuCharGenFaceGeomSourceEvidence(
    RaceMenuCharGenFaceGeomShapeRoute Route,
    Sha256Hash PositionSha256,
    Sha256Hash TopologySha256);

public sealed record RaceMenuCharGenFaceGeomCharGenSourceEvidence(
    int SourceBlockIndex,
    int SourceBlockSize,
    int SourceGeometryPayloadLength,
    int SourceVertexDataOffset,
    Sha256Hash SourceTopologySha256,
    Sha256Hash PositionSha256) : RaceMenuCharGenFaceGeomSourceEvidence(
        RaceMenuCharGenFaceGeomShapeRoute.CharGenXyz, PositionSha256,
        SourceTopologySha256);

public sealed record RaceMenuCharGenFaceGeomExternalXyzSourceEvidence(
    WorkspacePath XyzFile,
    Sha256Hash XyzFileSha256,
    int VertexCount,
    Sha256Hash TopologySha256,
    Sha256Hash PositionSha256) : RaceMenuCharGenFaceGeomSourceEvidence(
        RaceMenuCharGenFaceGeomShapeRoute.ExternalXyzOracle, PositionSha256,
        TopologySha256);

public sealed record RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence(
    WorkspacePath GeneratedXyzFile,
    Sha256Hash GeneratedXyzFileSha256,
    int VertexCount,
    Sha256Hash TopologySha256,
    Sha256Hash PositionSha256) : RaceMenuCharGenFaceGeomSourceEvidence(
        RaceMenuCharGenFaceGeomShapeRoute.GeneratedXyz, PositionSha256,
        TopologySha256);

public sealed record RaceMenuCharGenFaceGeomCarrierPreservedSourceEvidence(
    Sha256Hash PositionSha256,
    Sha256Hash TopologySha256) : RaceMenuCharGenFaceGeomSourceEvidence(
        RaceMenuCharGenFaceGeomShapeRoute.CarrierPreserved, PositionSha256,
        TopologySha256);

/// <summary>
/// Exact route and byte-surface binding for one required complete-carrier
/// BSDynamicTriShape. Only the first three float lanes of each 16-byte vertex
/// and an optional four-byte bounds radius are writable.
/// </summary>
public sealed record RaceMenuCharGenFaceGeomShapeDisposition(
    string CarrierShapeName,
    string Reason,
    RaceMenuCharGenFaceGeomSourceEvidence Source,
    int CarrierBlockIndex,
    int CarrierBlockSize,
    int CarrierGeometryPayloadLength,
    int CarrierVertexDataOffset,
    int VertexCount,
    int VertexStride,
    int PositionLaneLength,
    Sha256Hash CarrierTopologySha256,
    Sha256Hash CarrierPositionSha256,
    Sha256Hash CarrierFourthLaneSha256,
    int CarrierRadiusOffset,
    float CarrierRadius,
    float RequiredRadius,
    float OutputRadius,
    bool PositionChanged,
    bool RadiusChanged);

/// <summary>
/// Deterministic proposal produced before any output is written. Apply performs
/// a fresh analysis and requires deep equality with every shape disposition.
/// </summary>
public sealed record RaceMenuCharGenFaceGeomMergeProposal(
    string SchemaVersion,
    string Operation,
    GameEdition Edition,
    WorkspacePath CharGenNif,
    Sha256Hash CharGenSha256,
    long CharGenByteLength,
    int CharGenBlockCount,
    int CharGenDynamicShapeCount,
    WorkspacePath CarrierNif,
    Sha256Hash CarrierSha256,
    long CarrierByteLength,
    QualifiedFaceGeomCarrierStructure CarrierStructure,
    WorkspacePath OutputNif,
    Sha256Hash ExpectedOutputSha256,
    long ExpectedOutputByteLength,
    ImmutableArray<RaceMenuCharGenFaceGeomShapeDisposition> ShapeDispositions,
    int ChangedPositionShapeCount,
    int ExpandedRadiusCount,
    bool CreationKitAuthority,
    bool RuntimeAuthority)
{
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

public sealed record RaceMenuCharGenFaceGeomMergeAnalysisResult(
    bool Accepted,
    RaceMenuCharGenFaceGeomMergeProposal? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Independent post-write hash, structure, lane, and byte-surface evidence.</summary>
public sealed record RaceMenuCharGenFaceGeomMergeVerificationResult(
    bool Verified,
    WorkspacePath OutputNif,
    Sha256Hash? CharGenSha256,
    Sha256Hash? CarrierSha256,
    Sha256Hash? OutputSha256,
    long? OutputByteLength,
    QualifiedFaceGeomCarrierStructure? OutputStructure,
    ImmutableArray<string> VerifiedShapeNames,
    int VerifiedPositionLaneCount,
    int VerifiedRadiusCount,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record RaceMenuCharGenFaceGeomMergeArtifact(
    string ResultLabel,
    RaceMenuCharGenFaceGeomMergeProposal Proposal,
    Sha256Hash OutputSha256,
    long OutputByteLength,
    QualifiedFaceGeomCarrierStructure Structure,
    int RoutedShapeCount,
    int ChangedPositionShapeCount,
    int ExpandedRadiusCount,
    bool CreationKitAuthority,
    bool RuntimeAuthority);

public sealed record RaceMenuCharGenFaceGeomMergeResult(
    bool Written,
    bool Verified,
    RaceMenuCharGenFaceGeomMergeArtifact? Artifact,
    RaceMenuCharGenFaceGeomMergeVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuCharGenFaceGeomMergeService
{
    ValueTask<RaceMenuCharGenFaceGeomMergeAnalysisResult> AnalyzeAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        CancellationToken cancellationToken);

    ValueTask<RaceMenuCharGenFaceGeomMergeResult> ApplyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<RaceMenuCharGenFaceGeomMergeVerificationResult> VerifyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken);
}
