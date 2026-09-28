using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Selects the independently verified structural envelope for a complete
/// FaceGeom carrier. The provider-pinned profile preserves the historical
/// seven-shape Gate-1 contract. The Manager-assembled profile is reserved for
/// carriers created and hash-reopened by the product's headpart assembler. The
/// RaceMenu-exported profile admits a hash-bound, user-confirmed complete
/// CharGen export while preserving its bounded shaderless dummy-lens shapes.
/// </summary>
public enum QualifiedFaceGeomCarrierProfile
{
    ProviderPinnedSevenShape,
    ManagerAssembledComplete,
    RaceMenuExportedComplete
}

/// <summary>
/// Requests analysis of one hash-pinned, K-local Skyrim SE FaceGeom carrier and
/// a distinct no-overwrite output path. The requested texture route is embedded
/// in the NIF; it is not a filesystem destination.
/// </summary>
public sealed record QualifiedFaceGeomCarrierAnalyzeRequest(
    WorkspacePath SourceNif,
    Sha256Hash ExpectedSourceSha256,
    WorkspacePath OutputNif,
    AssetPath TargetFaceTintPath)
{
    /// <summary>
    /// Structural qualification envelope. Existing callers remain on the exact
    /// provider-pinned seven-shape contract unless they explicitly select the
    /// Manager-assembled route.
    /// </summary>
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;

    /// <summary>
    /// Optional output-owned head-texture authority. When present, the carrier
    /// rewrite also binds the NIF-embedded diffuse, normal/detail, glow/detail,
    /// and backlight/specular slots. Height and optional TXST-only routes remain
    /// proposal-bound but are deliberately not embedded into the FaceGeom NIF.
    /// </summary>
    public SkyrimPrivateHeadTexturePaths? TargetHeadTextures { get; init; }
}

/// <summary>One reachable NIF block-type count used by carrier qualification.</summary>
public sealed record QualifiedFaceGeomCarrierCensusEntry(string BlockType, int Count);

/// <summary>
/// Graph evidence for a complete carrier. The graph hash excludes byte offsets
/// and texture strings so a length-aware FaceTint route rewrite can preserve it.
/// </summary>
public sealed record QualifiedFaceGeomCarrierStructure(
    int BlockCount,
    int ReachableBlockCount,
    int RootCount,
    int NiNodeCount,
    int FadeNodeCount,
    int DynamicShapeCount,
    int NullChildReferenceCount,
    ImmutableArray<string> ReachableShapeNames,
    ImmutableArray<QualifiedFaceGeomCarrierCensusEntry> ReachableCensus,
    Sha256Hash GraphSha256);

/// <summary>
/// Deterministic two-pass proposal. Apply re-analyzes the source and requires
/// every binding below to match before it writes anything.
/// </summary>
public sealed record QualifiedFaceGeomCarrierProposal(
    string SchemaVersion,
    string Operation,
    WorkspacePath SourceNif,
    Sha256Hash SourceSha256,
    long SourceByteLength,
    WorkspacePath OutputNif,
    Sha256Hash ExpectedOutputSha256,
    long ExpectedOutputByteLength,
    int TextureSetBlockIndex,
    int TextureSlotIndex,
    string OriginalFaceTintPath,
    AssetPath TargetFaceTintPath,
    QualifiedFaceGeomCarrierStructure Structure,
    bool CreationKitAuthority,
    bool RuntimeAuthority)
{
    /// <summary>The exact structural envelope used during both passes.</summary>
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;

    /// <summary>
    /// Optional exact private-head route included in the two-pass binding. A
    /// missing value denotes the legacy slot-6-only FaceTint rewrite.
    /// </summary>
    public SkyrimPrivateHeadTexturePaths? TargetHeadTextures { get; init; }

    /// <summary>
    /// Canonical binding over every required and optional private-head path,
    /// including TXST-only paths that intentionally do not enter the NIF.
    /// </summary>
    public Sha256Hash? TargetHeadTexturesBindingSha256 { get; init; }
}

public sealed record QualifiedFaceGeomCarrierAnalysisResult(
    bool Qualified,
    QualifiedFaceGeomCarrierProposal? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Evidence emitted only after atomic promotion and post-write verification.
/// The result deliberately claims carrier materialization, not CK compilation,
/// provider authority, game loadability, or runtime appearance. It deliberately
/// contains no operational source or output path, so the two-pass proposal can
/// remain transaction-local.
/// </summary>
public sealed record QualifiedFaceGeomCarrierMaterializationArtifact(
    string SchemaVersion,
    string Operation,
    string ResultLabel,
    Sha256Hash SourceSha256,
    long SourceByteLength,
    QualifiedFaceGeomCarrierTextureSetPreimage SourceTextureSetPreimage,
    Sha256Hash OutputSha256,
    long OutputByteLength,
    int TextureSlotIndex,
    string OriginalFaceTintPath,
    AssetPath TargetFaceTintPath,
    SkyrimPrivateHeadTexturePaths? TargetHeadTextures,
    Sha256Hash TargetTextureBindingSha256,
    QualifiedFaceGeomCarrierStructure SourceStructure,
    ImmutableArray<int> ChangedBlocks,
    bool CreationKitAuthority,
    bool RuntimeAuthority)
{
    /// <summary>
    /// Structural envelope independently reapplied when durable package
    /// evidence reconstructs the pre-write source.
    /// </summary>
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

/// <summary>
/// Exact bounded preimage of the sole BSShaderTextureSet block authorized to
/// change. Together with the final NIF it reconstructs the complete source NIF
/// without retaining an otherwise redundant staging copy.
/// </summary>
public sealed record QualifiedFaceGeomCarrierTextureSetPreimage(
    string Encoding,
    int BlockIndex,
    string BlockType,
    string BytesBase64,
    int ByteLength,
    Sha256Hash Sha256);

/// <summary>
/// Relocatable package evidence. OutputNif is relative to the package root; all
/// source provenance is content-addressed and reconstructable from the compact
/// texture-set preimage in Materialization.
/// </summary>
public sealed record QualifiedFaceGeomCarrierMaterializationEvidence(
    AssetPath OutputNif,
    QualifiedFaceGeomCarrierMaterializationArtifact Materialization);

public sealed record QualifiedFaceGeomCarrierEvidenceVerificationResult(
    bool Verified,
    WorkspacePath? OutputNif,
    Sha256Hash? OutputSha256,
    Sha256Hash? ReconstructedSourceSha256,
    QualifiedFaceGeomCarrierStructure? Structure,
    ImmutableArray<int> ChangedBlocks,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record QualifiedFaceGeomCarrierVerificationResult(
    bool Verified,
    WorkspacePath OutputNif,
    Sha256Hash? OutputSha256,
    QualifiedFaceGeomCarrierStructure? Structure,
    ImmutableArray<int> ChangedBlocks,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record QualifiedFaceGeomCarrierMaterializationResult(
    bool Written,
    bool Verified,
    QualifiedFaceGeomCarrierMaterializationArtifact? Artifact,
    QualifiedFaceGeomCarrierVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Performs a bounded Analyze -&gt; Apply -&gt; Verify workflow for the qualified
/// complete FaceGeom carrier admitted by Gate 1.
/// </summary>
public interface IQualifiedFaceGeomCarrierService
{
    ValueTask<QualifiedFaceGeomCarrierAnalysisResult> AnalyzeAsync(
        QualifiedFaceGeomCarrierAnalyzeRequest request,
        CancellationToken cancellationToken);

    ValueTask<QualifiedFaceGeomCarrierMaterializationResult> ApplyAsync(
        QualifiedFaceGeomCarrierProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<QualifiedFaceGeomCarrierVerificationResult> VerifyAsync(
        QualifiedFaceGeomCarrierProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyEvidenceAsync(
        WorkspacePath packageRoot,
        QualifiedFaceGeomCarrierMaterializationEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyEvidenceFileAsync(
        WorkspacePath packageRoot,
        AssetPath evidenceFile,
        CancellationToken cancellationToken);
}
