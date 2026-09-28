using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class FaceGeomHairRegionSchemas
{
    public const string Analysis =
        "npcmanager-facegeom-hair-regions-analysis/1";
    public const string Request =
        "npcmanager-facegeom-hair-regions-request/1";
    public const string Proposal =
        "npcmanager-facegeom-hair-regions-proposal/1";
    public const string Manifest =
        "npcmanager-facegeom-hair-regions-manifest/1";
    public const string PreviewEvidence =
        "npcmanager-facegeom-hair-regions-preview-evidence/1";
}

public enum FaceGeomHairRegionRole
{
    Preserve,
    Primary,
    Accent
}

public sealed record StrictJsonDocumentAuthority<T>(
    T Value,
    ImmutableArray<byte> Utf8Json,
    Sha256Hash Sha256)
{
    public ImmutableArray<byte> CanonicalUtf8Json { get; init; } =
        Utf8Json;

    public Sha256Hash CanonicalSha256 { get; init; } = Sha256;
}

public sealed record ExactJsonFileAuthority(
    WorkspacePath Path,
    ImmutableArray<byte> Utf8Json,
    long ByteLength,
    Sha256Hash Sha256);

public sealed record ReviewedGameIntakeDocumentAuthority(
    ReviewedGameIntake Value,
    ExactJsonFileAuthority Document);

public sealed record FaceGeomHairRegionsFile(
    WorkspacePath Path,
    long ByteLength,
    Sha256Hash Sha256);

public sealed record FaceGeomHairRegionPluginColorContext(
    string Plugin,
    string? HairColor,
    string? HairColorHex);

public sealed record FaceGeomHairRegionsFingerprints(
    Sha256Hash Topology,
    Sha256Hash Geometry,
    Sha256Hash Skinning,
    Sha256Hash Textures,
    Sha256Hash Shaders);

public sealed record FaceGeomHairRegionsRegion(
    string StructuralId,
    string Name,
    int DuplicateNameOrdinal,
    string ShapeBlockType,
    int ShapeBlockId,
    long ShapeShaderReferenceByteOffset,
    string ShaderBlockType,
    int ShaderBlockId,
    string SharedShaderGroupId,
    ImmutableArray<string> ShaderOwnerStructuralIds,
    ImmutableArray<string> SharedStructuralIds,
    int? TextureSetBlockId,
    ImmutableArray<string> TextureRoutes,
    string CurrentColor,
    ImmutableArray<uint> ColorFloatBits,
    long TintByteOffset,
    int TintByteLength,
    FaceGeomHairRegionRole DefaultRole);

public sealed record FaceGeomHairRegionsAnalysis(
    string Schema,
    FaceGeomHairRegionsFile Source,
    ImmutableArray<FaceGeomHairRegionsRegion> Regions,
    FaceGeomHairRegionsFingerprints Fingerprints,
    FaceGeomHairRegionPluginColorContext? PluginColorContext);

public sealed record FaceGeomHairRegionAssignment(
    string StructuralId,
    FaceGeomHairRegionRole Role);

public sealed record FaceGeomHairRegionsRequest(
    string Schema,
    Sha256Hash AnalysisSha256,
    FaceGeomHairRegionsFile Source,
    string PrimaryColor,
    string AccentColor,
    ImmutableArray<FaceGeomHairRegionAssignment> Assignments,
    WorkspacePath Output,
    WorkspacePath Manifest);

public sealed record FaceGeomHairRegionsAuthorizedEnvelope(
    string SharedShaderGroupId,
    ImmutableArray<string> StructuralIds,
    FaceGeomHairRegionRole Role,
    long ByteOffset,
    int ByteLength,
    ImmutableArray<uint> OldFloatBits,
    ImmutableArray<uint> NewFloatBits);

public sealed record FaceGeomHairRegionsProposal(
    string Schema,
    Sha256Hash AnalysisSha256,
    Sha256Hash RequestSha256,
    FaceGeomHairRegionsFile Source,
    WorkspacePath Output,
    WorkspacePath Manifest,
    string PrimaryColor,
    string AccentColor,
    ImmutableArray<FaceGeomHairRegionAssignment> Assignments,
    ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope>
        AuthorizedEnvelopes,
    ImmutableArray<int> PredictedChangedByteOffsets,
    FaceGeomHairRegionsFingerprints SourceFingerprints,
    FaceGeomHairRegionsFingerprints ExpectedOutputFingerprints,
    FaceGeomHairRegionsFile ExpectedOutput,
    FaceGeomHairRegionPluginColorContext? PluginColorContext);

public sealed record FaceGeomHairRegionsProposalResult(
    StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal> Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceGeomHairRegionsVerification(
    bool Succeeded,
    FaceGeomHairRegionsFile? ObservedOutput,
    ImmutableArray<int> ChangedByteOffsets,
    FaceGeomHairRegionsFingerprints SourceFingerprints,
    FaceGeomHairRegionsFingerprints OutputFingerprints,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceGeomHairRegionsManifest(
    string Schema,
    Sha256Hash ProposalSha256,
    FaceGeomHairRegionsFile Source,
    FaceGeomHairRegionsFile Output,
    ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope>
        AuthorizedEnvelopes,
    ImmutableArray<int> ChangedByteOffsets,
    FaceGeomHairRegionsFingerprints Fingerprints,
    ImmutableArray<WorkspacePath> SurvivingArtifacts,
    bool RuntimeAuthority);

public sealed record FaceGeomHairRegionsApplyResult(
    bool Succeeded,
    FaceGeomHairRegionsManifest? Manifest,
    StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>?
        ManifestDocument,
    FaceGeomHairRegionsVerification? Verification,
    ImmutableArray<WorkspacePath> SurvivingArtifacts,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed class FaceGeomHairRegionsOperationCanceledException :
    OperationCanceledException
{
    public FaceGeomHairRegionsOperationCanceledException(
        string message,
        ImmutableArray<WorkspacePath> survivingArtifacts,
        Exception innerException,
        ImmutableArray<int> survivingProcessIds = default,
        string? processTerminationFailure = null)
        : base(message, innerException)
    {
        SurvivingArtifacts = survivingArtifacts;
        SurvivingProcessIds =
            survivingProcessIds.IsDefault
                ? []
                : survivingProcessIds;
        ProcessTerminationFailure =
            processTerminationFailure;
    }

    public ImmutableArray<WorkspacePath> SurvivingArtifacts
    {
        get;
    }

    public ImmutableArray<int> SurvivingProcessIds
    {
        get;
    }

    public string? ProcessTerminationFailure
    {
        get;
    }
}

public sealed class FaceGeomHairRegionsOperationalException :
    IOException
{
    public FaceGeomHairRegionsOperationalException(
        string message,
        ImmutableArray<WorkspacePath> survivingArtifacts,
        Exception innerException,
        ImmutableArray<int> survivingProcessIds = default,
        string? processTerminationFailure = null)
        : base(message, innerException)
    {
        SurvivingArtifacts =
            survivingArtifacts;
        SurvivingProcessIds =
            survivingProcessIds.IsDefault
                ? []
                : survivingProcessIds;
        ProcessTerminationFailure =
            processTerminationFailure;
    }

    public ImmutableArray<WorkspacePath> SurvivingArtifacts
    {
        get;
    }

    public ImmutableArray<int> SurvivingProcessIds
    {
        get;
    }

    public string? ProcessTerminationFailure
    {
        get;
    }
}

public interface IFaceGeomHairRegionsIndependentVerifier
{
    FaceGeomHairRegionsVerification Verify(
        ReadOnlyMemory<byte> source,
        ReadOnlyMemory<byte> output,
        FaceGeomHairRegionsProposal proposal);
}

public sealed record FaceGeomHairRegionsProposalMaterialization(
    ImmutableArray<byte> SourceBytes,
    ImmutableArray<byte> CandidateBytes,
    FaceGeomHairRegionsFile Candidate,
    ImmutableArray<int> ChangedByteOffsets,
    FaceGeomHairRegionsFingerprints SourceFingerprints,
    FaceGeomHairRegionsFingerprints CandidateFingerprints);

public sealed record FaceGeomHairRegionsPreviewRequest(
    StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
        RequestDocument,
    StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
        ProposalDocument,
    FaceGeomHairRegionsProposalMaterialization Materialization,
    ReviewedGameIntakeDocumentAuthority IntakeAuthority,
    WorkspacePath OutputRoot)
{
    public ReviewedGameIntake Intake => IntakeAuthority.Value;

    public ExactJsonFileAuthority IntakeDocument =>
        IntakeAuthority.Document;
}

public enum FaceGeomHairRegionsPreviewArtifactKind
{
    CombinedFace,
    RegionThumbnail,
    RegionMask,
    ContactSheet,
    EvidenceDocument
}

public sealed record FaceGeomHairRegionsPreviewArtifact(
    FaceGeomHairRegionsPreviewArtifactKind Kind,
    string? StructuralId,
    WorkspacePath Path,
    long ByteLength,
    Sha256Hash Sha256,
    long? NonEmptyPixelCount,
    ImmutableArray<byte> Content = default);

public sealed record FaceGeomHairRegionsPreviewEvidence(
    Sha256Hash StagedFaceGeomSha256,
    Sha256Hash ProposalSha256,
    Sha256Hash IntakeSha256,
    Sha256Hash RendererSha256,
    Sha256Hash TextureFingerprintSha256,
    Sha256Hash EvidenceDocumentSha256,
    int DetectedFaceCount,
    int LandmarkCount,
    int SemanticAnchorCount,
    FaceGeomHairRegionsRenderAuthority RenderAuthority);

public sealed record FaceGeomHairRegionsPreviewEvidenceDocument(
    string Schema,
    Sha256Hash StagedFaceGeomSha256,
    Sha256Hash ProposalSha256,
    Sha256Hash IntakeSha256,
    Sha256Hash RendererSha256,
    Sha256Hash TextureFingerprintSha256,
    int DetectedFaceCount,
    int LandmarkCount,
    int SemanticAnchorCount,
    bool VisualAuthority,
    bool RuntimeAuthority,
    FaceGeomHairRegionsRenderAuthority RenderAuthority,
    ImmutableArray<FaceGeomHairArtifactEvidence>
        Artifacts);

public sealed record FaceGeomHairArtifactEvidence(
    FaceGeomHairRegionsPreviewArtifactKind Kind,
    string? StructuralId,
    string RelativePath,
    Sha256Hash Sha256,
    long Bytes,
    int Width,
    int Height,
    long NonEmptyPixelCount);

public enum FaceGeomHairTextureBindingMode
{
    SampledImage,
    BoundUnmodeled
}

public sealed record FaceGeomHairTextureBindingEvidence(
    FaceGeomHairTextureBindingMode Mode,
    string BindingSemantic,
    string ObjectName,
    string MaterialName,
    string NodeName);

public sealed record FaceGeomHairTextureEvidence(
    AssetPath AssetPath,
    AssetProviderKind ProviderKind,
    string Provider,
    Sha256Hash SourceSha256,
    long SourceBytes,
    Sha256Hash DecodedPreviewSha256,
    long DecodedPreviewBytes,
    string DecodeKind,
    ImmutableArray<FaceGeomHairTextureBindingEvidence>
        Bindings);

public sealed record FaceGeomHairRegionsRenderAuthority(
    Sha256Hash BlenderSha256,
    Sha256Hash PyniflyArchiveSha256,
    Sha256Hash PyniflySourceProfileSha256,
    Sha256Hash RendererScriptSha256,
    Sha256Hash TexconvSha256,
    Sha256Hash TextureSourceFingerprintSha256,
    Sha256Hash LoadedTextureObservationFingerprintSha256,
    Sha256Hash PyniflyModuleSourceFingerprintSha256,
    int PyniflyModuleCount,
    Sha256Hash OriginalProfileFingerprintSha256,
    int OriginalProfileFileCount,
    ImmutableArray<FaceGeomHairTextureEvidence> Textures,
    ImmutableArray<FaceGeomHairPyniflyModuleEvidence>
        PyniflyModules);

public enum FaceGeomHairPyniflyModuleKind
{
    SourceFile,
    NamespaceDirectory
}

public sealed record FaceGeomHairPyniflyModuleEvidence(
    string ModuleName,
    FaceGeomHairPyniflyModuleKind Kind,
    string RelativeSource,
    Sha256Hash SourceSha256);

public sealed record FaceGeomHairRegionsPreviewResult(
    bool Succeeded,
    FaceGeomHairRegionsPreviewEvidence? Evidence,
    ImmutableArray<FaceGeomHairRegionsPreviewArtifact> Artifacts,
    ImmutableArray<Diagnostic> Diagnostics,
    bool VisualAuthority,
    bool RuntimeAuthority);

public interface IFaceGeomHairRegionsPreviewService
{
    ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
        FaceGeomHairRegionsPreviewRequest request,
        CancellationToken cancellationToken);
}

public sealed record FaceGeomHairRegionsPreviewSourceRequest(
    FaceGeomHairRegionsFile Candidate,
    ImmutableArray<byte> CandidateBytes,
    ReviewedGameIntake Intake,
    WorkspacePath OutputDataRoot);

public sealed record FaceGeomHairRegionsPreviewSource(
    NpcVisualAsset Candidate,
    ImmutableArray<FaceGeomHairTextureAuthority> Textures,
    Sha256Hash TextureFingerprintSha256);

public sealed record FaceGeomHairRegionsPreviewSourceResult(
    bool Composed,
    FaceGeomHairRegionsPreviewSource? Source,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGeomHairRegionsPreviewSourceService
{
    ValueTask<FaceGeomHairRegionsPreviewSourceResult> ComposeAsync(
        FaceGeomHairRegionsPreviewSourceRequest request,
        CancellationToken cancellationToken);
}

public sealed record FaceGeomHairRegionsRenderRequest(
    FaceGeomHairRegionsPreviewSource Source,
    ImmutableArray<byte> CandidateBytes,
    ImmutableArray<FaceGeomHairRegionsRegion> Regions,
    WorkspacePath OutputRoot);

public sealed record FaceGeomHairRegionsRenderResult(
    bool Rendered,
    ImmutableArray<FaceGeomHairRegionsPreviewArtifact> Artifacts,
    Sha256Hash? RendererSha256,
    Sha256Hash? TextureFingerprintSha256,
    int FaceGeomImportCount,
    int NifImportInvocationCount,
    ImmutableArray<Diagnostic> Diagnostics,
    FaceGeomHairRegionsRenderAuthority? Authority = null);

public interface IFaceGeomHairRegionsRenderer
{
    ValueTask<FaceGeomHairRegionsRenderResult> RenderAsync(
        FaceGeomHairRegionsRenderRequest request,
        CancellationToken cancellationToken);
}
