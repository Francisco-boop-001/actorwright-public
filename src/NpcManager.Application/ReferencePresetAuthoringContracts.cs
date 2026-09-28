using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ReferencePresetAuthorityKind
{
    Intake,
    InferenceProposal,
    ReviewedDesign,
    AuthoringProposal,
    VerifiedPreset,
    VerifiedNpcHandoff
}

public static class ReferencePresetAuthorityKindExtensions
{
    public static string ToWireName(
        this ReferencePresetAuthorityKind authority) => authority switch
    {
        ReferencePresetAuthorityKind.Intake => "intake",
        ReferencePresetAuthorityKind.InferenceProposal =>
            "inference-proposal",
        ReferencePresetAuthorityKind.ReviewedDesign => "reviewed-design",
        ReferencePresetAuthorityKind.AuthoringProposal =>
            "authoring-proposal",
        ReferencePresetAuthorityKind.VerifiedPreset => "verified-preset",
        ReferencePresetAuthorityKind.VerifiedNpcHandoff =>
            "verified-npc-handoff",
        _ => throw new ArgumentOutOfRangeException(
            nameof(authority), authority, "Unsupported reference authority.")
    };

    public static bool TryParseWireName(
        string value,
        out ReferencePresetAuthorityKind authority)
    {
        foreach (ReferencePresetAuthorityKind candidate
                 in Enum.GetValues<ReferencePresetAuthorityKind>())
        {
            if (string.Equals(
                    candidate.ToWireName(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                authority = candidate;
                return true;
            }
        }

        authority = default;
        return false;
    }
}

public enum ReferenceImageViewRole
{
    Front,
    LeftThreeQuarter,
    RightThreeQuarter,
    LeftProfile,
    RightProfile
}

public static class ReferenceImageViewRoleExtensions
{
    public static string ToWireName(this ReferenceImageViewRole role) =>
        role switch
        {
            ReferenceImageViewRole.Front => "front",
            ReferenceImageViewRole.LeftThreeQuarter =>
                "left-three-quarter",
            ReferenceImageViewRole.RightThreeQuarter =>
                "right-three-quarter",
            ReferenceImageViewRole.LeftProfile => "left-profile",
            ReferenceImageViewRole.RightProfile => "right-profile",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role), role, "Unsupported reference image role.")
        };

    public static bool TryParseWireName(
        string value,
        out ReferenceImageViewRole role)
    {
        foreach (ReferenceImageViewRole candidate
                 in Enum.GetValues<ReferenceImageViewRole>())
        {
            if (string.Equals(
                    candidate.ToWireName(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}

public enum ReferenceAnchorReviewState
{
    Accepted,
    Corrected,
    UnknownHidden,
    UnknownObstructed,
    UnknownLowConfidence
}

public static class ReferenceAnchorReviewStateExtensions
{
    public static string ToWireName(
        this ReferenceAnchorReviewState state) => state switch
    {
        ReferenceAnchorReviewState.Accepted => "accepted",
        ReferenceAnchorReviewState.Corrected => "corrected",
        ReferenceAnchorReviewState.UnknownHidden => "unknown-hidden",
        ReferenceAnchorReviewState.UnknownObstructed =>
            "unknown-obstructed",
        ReferenceAnchorReviewState.UnknownLowConfidence =>
            "unknown-low-confidence",
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Unsupported anchor review state.")
    };

    public static bool TryParseWireName(
        string value,
        out ReferenceAnchorReviewState state)
    {
        foreach (ReferenceAnchorReviewState candidate
                 in Enum.GetValues<ReferenceAnchorReviewState>())
        {
            if (string.Equals(
                    candidate.ToWireName(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                state = candidate;
                return true;
            }
        }

        state = default;
        return false;
    }
}

public enum ReferenceSemanticAnchorKind
{
    ForeheadCenter,
    Chin,
    LeftJawAngle,
    RightJawAngle,
    LeftWidestCheek,
    RightWidestCheek,
    LeftTemple,
    RightTemple,
    LeftBrowInner,
    LeftBrowPeak,
    LeftBrowOuter,
    RightBrowInner,
    RightBrowPeak,
    RightBrowOuter,
    LeftEyeInner,
    LeftEyeOuter,
    LeftEyeUpper,
    LeftEyeLower,
    RightEyeInner,
    RightEyeOuter,
    RightEyeUpper,
    RightEyeLower,
    NoseBridge,
    NoseTip,
    LeftNoseWing,
    RightNoseWing,
    LeftMouthCorner,
    RightMouthCorner,
    LeftCupidPeak,
    RightCupidPeak,
    LowerLipCenter
}

public static class ReferenceSemanticAnchorKindExtensions
{
    public static string ToWireName(
        this ReferenceSemanticAnchorKind anchor) => anchor switch
    {
        ReferenceSemanticAnchorKind.ForeheadCenter => "forehead-center",
        ReferenceSemanticAnchorKind.Chin => "chin",
        ReferenceSemanticAnchorKind.LeftJawAngle => "left-jaw-angle",
        ReferenceSemanticAnchorKind.RightJawAngle => "right-jaw-angle",
        ReferenceSemanticAnchorKind.LeftWidestCheek =>
            "left-widest-cheek",
        ReferenceSemanticAnchorKind.RightWidestCheek =>
            "right-widest-cheek",
        ReferenceSemanticAnchorKind.LeftTemple => "left-temple",
        ReferenceSemanticAnchorKind.RightTemple => "right-temple",
        ReferenceSemanticAnchorKind.LeftBrowInner => "left-brow-inner",
        ReferenceSemanticAnchorKind.LeftBrowPeak => "left-brow-peak",
        ReferenceSemanticAnchorKind.LeftBrowOuter => "left-brow-outer",
        ReferenceSemanticAnchorKind.RightBrowInner =>
            "right-brow-inner",
        ReferenceSemanticAnchorKind.RightBrowPeak => "right-brow-peak",
        ReferenceSemanticAnchorKind.RightBrowOuter =>
            "right-brow-outer",
        ReferenceSemanticAnchorKind.LeftEyeInner => "left-eye-inner",
        ReferenceSemanticAnchorKind.LeftEyeOuter => "left-eye-outer",
        ReferenceSemanticAnchorKind.LeftEyeUpper => "left-eye-upper",
        ReferenceSemanticAnchorKind.LeftEyeLower => "left-eye-lower",
        ReferenceSemanticAnchorKind.RightEyeInner => "right-eye-inner",
        ReferenceSemanticAnchorKind.RightEyeOuter => "right-eye-outer",
        ReferenceSemanticAnchorKind.RightEyeUpper => "right-eye-upper",
        ReferenceSemanticAnchorKind.RightEyeLower => "right-eye-lower",
        ReferenceSemanticAnchorKind.NoseBridge => "nose-bridge",
        ReferenceSemanticAnchorKind.NoseTip => "nose-tip",
        ReferenceSemanticAnchorKind.LeftNoseWing => "left-nose-wing",
        ReferenceSemanticAnchorKind.RightNoseWing => "right-nose-wing",
        ReferenceSemanticAnchorKind.LeftMouthCorner =>
            "left-mouth-corner",
        ReferenceSemanticAnchorKind.RightMouthCorner =>
            "right-mouth-corner",
        ReferenceSemanticAnchorKind.LeftCupidPeak => "left-cupid-peak",
        ReferenceSemanticAnchorKind.RightCupidPeak =>
            "right-cupid-peak",
        ReferenceSemanticAnchorKind.LowerLipCenter => "lower-lip-center",
        _ => throw new ArgumentOutOfRangeException(
            nameof(anchor), anchor, "Unsupported semantic anchor.")
    };

    public static bool TryParseWireName(
        string value,
        out ReferenceSemanticAnchorKind anchor)
    {
        foreach (ReferenceSemanticAnchorKind candidate
                 in Enum.GetValues<ReferenceSemanticAnchorKind>())
        {
            if (string.Equals(
                    candidate.ToWireName(),
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                anchor = candidate;
                return true;
            }
        }

        anchor = default;
        return false;
    }
}

public enum ReferenceDescriptionTraitKind
{
    FaceLength,
    FaceWidth,
    JawWidth,
    JawTaper,
    JawShape,
    ChinWidth,
    ChinHeight,
    ChinProjection,
    CheekWidth,
    CheekProminence,
    EyeSize,
    EyeSpacing,
    EyeCant,
    EyeDepth,
    BrowHeight,
    BrowArch,
    BrowThickness,
    NoseBridgeWidth,
    NoseBridgeHeight,
    NoseBridgeSlope,
    NoseLength,
    NoseWingWidth,
    NoseTipDirection,
    LipWidth,
    LipFullness,
    Complexion,
    HairColor,
    EyeColor,
    MakeupIntent,
    HeadpartPreference
}

public enum ReferenceTraitChannel
{
    Geometry,
    Headpart,
    Tint,
    Color
}

public enum ReferenceTraitReviewState
{
    Proposed,
    Accepted,
    Corrected,
    Rejected
}

public enum ReferenceUnknownReviewState
{
    Unreviewed,
    Acknowledged,
    Rejected
}

public enum ReferenceImageFormat
{
    Png,
    Jpeg,
    WebP
}

public enum ReferenceImageOrientation
{
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 3,
    BottomLeft = 4,
    LeftTop = 5,
    RightTop = 6,
    RightBottom = 7,
    LeftBottom = 8
}

public enum ReferenceMorphChannelKind
{
    NativePreset,
    Custom
}

public enum ReferencePresetLossKind
{
    UnsupportedDescription,
    ImageDescriptionConflict,
    UnknownAnchor,
    UnsupportedMorph,
    ResidualGeometry,
    SculptUnavailable,
    HeadpartUnavailable,
    TintUnavailable,
    RenderUnavailable
}

public enum ReferencePresetProgressStage
{
    Validate,
    DecodeImages,
    InferLandmarks,
    InterpretDescription,
    ProjectAnchors,
    ResolveResources,
    BindMeshAnchors,
    BuildResponseMatrix,
    Solve,
    Render,
    WriteProposal,
    WritePreset,
    VerifyPreset,
    BuildNpc,
    Completed
}

public enum ReferencePresetSessionDocumentKind
{
    Intake,
    InferenceProposal,
    ReviewedDesign,
    ResourceSnapshot,
    AuthoringProposal,
    VerifiedPreset,
    VerifiedNpcHandoff
}

public sealed record ReferenceImageAuthority(
    string ImageId,
    WorkspacePath SourcePath,
    Sha256Hash SourceSha256,
    long EncodedLength,
    ReferenceImageViewRole ViewRole);

public sealed record ReferencePresetIntake(
    int SchemaVersion,
    string ProjectId,
    string TargetName,
    FormReference Race,
    NpcSex Sex,
    float Weight,
    string HeadSystemId,
    WorkspacePath BaselineJslot,
    Sha256Hash BaselineJslotSha256,
    string Description,
    ImmutableArray<ReferenceImageAuthority> Images,
    RaceMenuPresetTarget Target);

public sealed record ReferenceImageDecodeRequest(
    ReferenceImageAuthority Image,
    long RemainingDecodedByteBudget);

public sealed record DecodedReferenceImage(
    string ImageId,
    ReferenceImageViewRole ViewRole,
    WorkspacePath SourcePath,
    Sha256Hash SourceSha256,
    long EncodedLength,
    ReferenceImageFormat Format,
    ReferenceImageOrientation SourceOrientation,
    int Width,
    int Height,
    int Stride,
    ImmutableArray<byte> CanonicalRgba,
    Sha256Hash CanonicalRgbaSha256);

public sealed record ReferenceImageDecodeResult(
    DecodedReferenceImage? Image,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Image is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferenceFaceLandmark(
    int Index,
    double X,
    double Y,
    double Z,
    double? Presence,
    double? Visibility);

public sealed record ReferenceFaceInferenceRequest(
    DecodedReferenceImage Image,
    Sha256Hash RuntimeManifestSha256);

public sealed record ReferencePresetRuntimeAdmissionResult(
    Sha256Hash? ManifestSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => ManifestSha256 is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferenceImageInference(
    string ImageId,
    ReferenceImageViewRole ViewRole,
    Sha256Hash SourceSha256,
    Sha256Hash CanonicalRgbaSha256,
    int Width,
    int Height,
    double DetectorScore,
    ImmutableArray<ReferenceFaceLandmark> Landmarks,
    ImmutableArray<double> TransformationMatrix,
    double AdvisoryYawDegrees,
    Sha256Hash NativeLibrarySha256,
    Sha256Hash DetectorModelSha256,
    Sha256Hash LandmarkerModelSha256);

public sealed record ReferenceFaceInferenceResult(
    ReferenceImageInference? Inference,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Inference is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferenceFaceNativeInferenceResult(
    int DetectedFaceCount,
    double DetectorScore,
    ImmutableArray<ReferenceFaceLandmark> Landmarks,
    ImmutableArray<double> TransformationMatrix,
    double AdvisoryYawDegrees,
    Sha256Hash NativeLibrarySha256,
    Sha256Hash DetectorModelSha256,
    Sha256Hash LandmarkerModelSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferenceDescriptionVocabularyEntry(
    string Phrase,
    ReferenceDescriptionTraitKind Kind,
    double Strength,
    ReferenceTraitChannel Channel);

public sealed record ReferenceDescriptionTrait(
    string SourcePhrase,
    ReferenceDescriptionTraitKind Kind,
    double Strength,
    ReferenceTraitChannel Channel,
    double Confidence,
    ReferenceTraitReviewState ReviewState,
    bool ConflictAcknowledged);

public sealed record ReferenceUnknown(
    string Text,
    string Reason,
    ReferenceUnknownReviewState ReviewState);

public sealed record ReferenceDescriptionInterpretRequest(
    string Description,
    Sha256Hash IntakeSha256);

public sealed record ReferenceDescriptionInterpretResult(
    ImmutableArray<ReferenceDescriptionTrait> Traits,
    ImmutableArray<ReferenceUnknown> Unknowns,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferenceSemanticAnchorProposal(
    ReferenceSemanticAnchorKind Anchor,
    int SourceLandmarkIndex,
    double X,
    double Y,
    double Confidence,
    bool AdvisoryVisible,
    bool ManualConfirmationRequired);

public sealed record ReferenceSemanticAnchor(
    ReferenceSemanticAnchorKind Anchor,
    int SourceLandmarkIndex,
    double X,
    double Y,
    double Confidence,
    bool Required,
    ReferenceAnchorReviewState ReviewState);

public sealed record ReferenceSemanticLandmarkProjectionRequest(
    ReferenceImageInference Inference);

public sealed record ReferenceSemanticLandmarkProjectionResult(
    ImmutableArray<ReferenceSemanticAnchorProposal> Anchors,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReviewedReferenceView(
    string ImageId,
    ReferenceImageViewRole ViewRole,
    Sha256Hash InferenceSha256,
    double DetectorScore,
    double ReviewedYawDegrees,
    bool ReviewAccepted,
    ImmutableArray<ReferenceSemanticAnchor> Anchors);

public sealed record ReferenceTintSelection(
    int TintIndex,
    int TintType,
    uint Argb,
    double Strength);

public sealed record ReferencePresetCatalogSelection(
    bool ReviewAccepted,
    FormReference Face,
    FormReference Mouth,
    FormReference Eyes,
    FormReference Brows,
    FormReference Hair,
    ImmutableArray<ReferenceTintSelection> Tints);

public sealed record ReferencePresetCatalogReadRequest(
    RaceMenuPresetTarget Target,
    ReferencePresetCatalogSelection Selection);

public sealed record ReferencePresetCatalogHeadPart(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string EditorId,
    NpcHeadPartType Type,
    AssetPath ModelNif,
    ImmutableArray<SkyrimHdptTriRoute> TriRoutes,
    ImmutableArray<FormReference> ExtraParts,
    FormReference? TextureSet,
    bool IsRootSelection);

public sealed record ReferencePresetCatalogTint(
    ReferenceTintSelection Selection,
    int RaceOrder,
    SkyrimRaceTintMaskKind Kind,
    AssetPath MaskPath);

public sealed record ReferencePresetCatalogAuthority(
    FormReference Race,
    SkyrimFaceRecordProvider RaceProvider,
    NpcSex Sex,
    ImmutableArray<ReferencePresetCatalogHeadPart> HeadParts,
    ImmutableArray<ReferencePresetCatalogTint> Tints)
{
    public string RaceEditorId { get; init; } = string.Empty;
    public string MorphRaceEditorId { get; init; } = string.Empty;
    public ImmutableArray<string> RaceKeywordEditorIds { get; init; } = [];
}

public sealed record ReferencePresetCatalogReadResult(
    ReferencePresetCatalogAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Authority is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBethesdaReferencePresetCatalogReader
{
    ValueTask<ReferencePresetCatalogReadResult> ReadAsync(
        ReferencePresetCatalogReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReferencePresetAssetContent(
    SkyrimAssetAuthority Authority,
    ImmutableArray<byte> Content);

public sealed record ReferencePresetAssetMaterializeRequest(
    WorkspacePath AllowedRoot,
    ImmutableArray<SkyrimAssetAuthority> Authorities);

public sealed record ReferencePresetAssetMaterializeResult(
    ImmutableArray<ReferencePresetAssetContent> Assets,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Assets.Length > 0 &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Materializes only already-planned provider winners. Search and precedence
/// remain the responsibility of <see cref="ISkyrimAssetAuthorityPlanner"/>.
/// </summary>
public interface IReferencePresetAssetMaterializer
{
    ValueTask<ReferencePresetAssetMaterializeResult> MaterializeAsync(
        ReferencePresetAssetMaterializeRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReferenceMeshAnchorBinding(
    ReferenceImageViewRole ViewRole,
    ReferenceSemanticAnchorKind Anchor,
    string NifIdentity,
    string ShapeIdentity,
    Sha256Hash NifSha256,
    Sha256Hash TopologySha256,
    Sha256Hash RestPositionsSha256,
    Sha256Hash CameraSha256,
    Sha256Hash RenderSha256,
    int TriangleOrdinal,
    int VertexIndex0,
    int VertexIndex1,
    int VertexIndex2,
    double Barycentric0,
    double Barycentric1,
    double Barycentric2);

public sealed record LandmarkInferenceProposal(
    int SchemaVersion,
    ReferencePresetAuthorityKind Authority,
    Sha256Hash IntakeSha256,
    Sha256Hash RuntimeManifestSha256,
    ImmutableArray<ReferenceImageInference> Images,
    ImmutableArray<ReferenceDescriptionTrait> Traits,
    ImmutableArray<ReferenceUnknown> Unknowns);

public sealed record ReviewedReferencePresetDesign(
    int SchemaVersion,
    ReferencePresetAuthorityKind Authority,
    Sha256Hash ProposalSha256,
    bool ReviewAccepted,
    ImmutableArray<ReviewedReferenceView> Views,
    ImmutableArray<ReferenceDescriptionTrait> Traits,
    ImmutableArray<ReferenceUnknown> Unknowns,
    ReferencePresetCatalogSelection CatalogSelection,
    ImmutableArray<ReferenceMeshAnchorBinding> MeshBindings);

public sealed record ReferenceMorphChannelAuthority(
    ReferenceMorphChannelKind Kind,
    string Name,
    int Ordinal,
    double Minimum,
    double Maximum,
    Sha256Hash TriSha256)
{
    public string NifIdentity { get; init; } = string.Empty;
    public string ShapeIdentity { get; init; } = string.Empty;
    public ImmutableArray<SseTriHeadVertexDelta> Deltas { get; init; } = [];
    public Sha256Hash? NegativeTriSha256 { get; init; }
    public ImmutableArray<SseTriHeadVertexDelta> NegativeDeltas
    {
        get;
        init;
    } = [];
    public bool IsDiscrete { get; init; }
    public int? DiscreteFamilyOrdinal { get; init; }
    public int? DiscreteValue { get; init; }
}

public sealed record ReferenceRenderMaterialAuthority(
    string MaterialIdentity,
    SkyrimAssetAuthority Diffuse,
    SkyrimAssetAuthority? Normal,
    SkyrimAssetAuthority? Specular,
    uint TintArgb)
{
    public bool AlphaTestEnabled { get; init; }
    public byte AlphaTestThreshold { get; init; }
}

public sealed record ReferenceRenderShapeAuthority(
    string NifIdentity,
    string ShapeIdentity,
    Sha256Hash NifSha256,
    Sha256Hash TopologySha256,
    Sha256Hash RestPositionsSha256,
    ImmutableArray<ReferenceRenderMaterialAuthority> Materials)
{
    public ImmutableArray<Vector3> RestPositions { get; init; } = [];
    public ImmutableArray<int> TriangleIndices { get; init; } = [];
    public ImmutableArray<Vector2> TextureCoordinates { get; init; } = [];
    public ImmutableArray<Vector3> Normals { get; init; } = [];
    public ImmutableArray<int> TriangleMaterialOrdinals { get; init; } = [];
    public SseSelectedHeadpartNifPlacement RenderPlacement { get; init; } =
        SseSelectedHeadpartNifPlacement.Identity;
}

public sealed record ReferencePresetResourceSnapshotRequest(
    ReferencePresetIntake Intake,
    ReviewedReferencePresetDesign ReviewedDesign,
    Sha256Hash ReviewedDesignSha256);

public sealed record ReferencePresetResourceSnapshot(
    int SchemaVersion,
    string ProjectId,
    Sha256Hash ReviewedDesignSha256,
    Sha256Hash BaselineJslotSha256,
    PresetDocument Baseline,
    ReferencePresetCatalogSelection CatalogSelection,
    ImmutableArray<SkyrimAssetAuthority> AssetAuthorities,
    ImmutableArray<ReferenceMorphChannelAuthority> MorphChannels,
    ImmutableArray<ReferenceRenderShapeAuthority> RenderShapes,
    Sha256Hash EnvironmentFingerprint,
    Sha256Hash ResourceFingerprint,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ReferencePresetCatalogAuthority? CatalogAuthority { get; init; }
    public SkyrimRaceMenuSliderCatalog? RaceMenuCatalog { get; init; }
    public ImmutableArray<ReferenceFaceMorphShapeBasis> MorphBases
    {
        get;
        init;
    } = [];
    public ImmutableArray<ReferenceRenderTexture> RenderTextures { get; init; } = [];
    public ImmutableArray<int> ProtectedNeckRingVertexIndices
    {
        get;
        init;
    } = [];
}

public sealed record ReferencePresetResourceSnapshotResult(
    ReferencePresetResourceSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Snapshot is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferenceOrthographicCamera(
    ReferenceImageViewRole ViewRole,
    double YawDegrees,
    double PitchDegrees,
    double Left,
    double Right,
    double Bottom,
    double Top,
    double Near,
    double Far,
    Sha256Hash CameraSha256);

public sealed record ReferenceRenderShape(
    string NifIdentity,
    string ShapeIdentity,
    ImmutableArray<Vector3> Positions,
    ImmutableArray<int> TriangleIndices,
    ImmutableArray<Vector2> TextureCoordinates,
    ImmutableArray<Vector3> Normals,
    ImmutableArray<int> TriangleMaterialOrdinals,
    Sha256Hash GeometrySha256)
{
    public Sha256Hash NifSha256 { get; init; }
    public Sha256Hash TopologySha256 { get; init; }
    public Sha256Hash RestPositionsSha256 { get; init; }
    public ImmutableArray<Vector3> SourceRestPositions { get; init; } = [];
    public SseSelectedHeadpartNifPlacement RenderPlacement { get; init; } =
        SseSelectedHeadpartNifPlacement.Identity;
    public ImmutableArray<ReferenceRenderMaterialAuthority> Materials
    {
        get;
        init;
    } = [];
}

public sealed record ReferenceRenderTexture(
    SkyrimAssetAuthority Authority,
    int Width,
    int Height,
    [property: JsonIgnore]
    ImmutableArray<byte> CanonicalRgba,
    Sha256Hash CanonicalRgbaSha256)
{
    public int CanonicalRgbaLength { get; init; } =
        CanonicalRgba.IsDefault
            ? 0
            : CanonicalRgba.Length;

    public string CanonicalRgbaEncoding { get; init; } =
        ReferenceRenderTexturePayload.EncodingName;

    public string CanonicalRgbaPayloadBase64 { get; init; } =
        ReferenceRenderTexturePayload.Encode(CanonicalRgba);

    public bool TryGetCanonicalRgba(
        out ImmutableArray<byte> rgba,
        out string error) =>
        ReferenceRenderTexturePayload.TryDecode(
            this,
            out rgba,
            out error);
}

public sealed record ReferencePresetRenderInputRequest(
    ReferencePresetResourceSnapshot Snapshot,
    ReviewedReferencePresetDesign ReviewedDesign);

public sealed record ReferencePresetRenderInput(
    ImmutableArray<ReferenceRenderShape> Shapes,
    ImmutableArray<ReferenceRenderTexture> Textures,
    ImmutableArray<ReferenceOrthographicCamera> Cameras,
    Sha256Hash InputSha256);

public sealed record ReferencePresetRenderInputResult(
    ReferencePresetRenderInput? Input,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetCpuRenderRequest(
    ImmutableArray<ReferenceRenderShape> Shapes,
    ImmutableArray<ReferenceRenderTexture> Textures,
    ReferenceOrthographicCamera Camera,
    int Width,
    int Height);

public sealed record ReferencePresetCpuRenderResult(
    bool Accepted,
    int Width,
    int Height,
    ImmutableArray<byte> CanonicalRgba,
    ImmutableArray<byte> PngBytes,
    Sha256Hash? PngSha256,
    Sha256Hash? RendererSha256,
    Sha256Hash? GeometrySha256,
    Sha256Hash? TextureSetSha256,
    Sha256Hash? TintSetSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferenceAnchorDisplacement(
    ReferenceImageViewRole ViewRole,
    ReferenceSemanticAnchorKind Anchor,
    double DeltaX,
    double DeltaY);

public sealed record ReferenceAnchorProjectionBaseline(
    ReferenceImageViewRole ViewRole,
    ReferenceSemanticAnchorKind Anchor,
    double X,
    double Y,
    double Weight);

public sealed record RaceMenuMorphResponse(
    ReferenceMorphChannelAuthority Channel,
    ImmutableArray<ReferenceAnchorDisplacement> Displacements,
    Sha256Hash TopologySha256,
    Sha256Hash CameraSetSha256,
    Sha256Hash ResourceSnapshotSha256)
{
    public Sha256Hash MorphSha256 { get; init; }
    public ImmutableArray<ReferenceAnchorDisplacement>
        NegativeDisplacements { get; init; } = [];
}

public sealed record ReferenceFaceMorphShapeBasis(
    string NifIdentity,
    string ShapeIdentity,
    SkyrimFaceMorphPlanBuildRequest PlanTemplate);

public sealed record RaceMenuTriResponseMatrixBuildRequest(
    ReviewedReferencePresetDesign ReviewedDesign,
    ReferencePresetResourceSnapshot Snapshot,
    ReferencePresetRenderInput RenderInput)
{
    public ImmutableArray<ReferenceFaceMorphShapeBasis> MorphBases
    {
        get;
        init;
    } = [];

    public Sha256Hash ReviewedDesignSha256 { get; init; }
}

public sealed record RaceMenuTriResponseMatrixBuildResult(
    ImmutableArray<RaceMenuMorphResponse> Responses,
    Sha256Hash? MatrixSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ImmutableArray<ReferenceAnchorProjectionBaseline>
        BaselineProjections { get; init; } = [];

    public Sha256Hash RenderInputSha256 { get; init; }
    public Sha256Hash ReviewedDesignSha256 { get; init; }

    public bool Accepted =>
        MatrixSha256 is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferencePresetResidual(
    ReferenceImageViewRole ViewRole,
    ReferenceSemanticAnchorKind Anchor,
    double TargetX,
    double TargetY,
    double ActualX,
    double ActualY,
    double Weight,
    double SquaredLoss);

public sealed record ReferencePresetLoss(
    string Code,
    ReferencePresetLossKind Kind,
    string Message,
    bool Acknowledged);

public sealed record ReferenceSculptVertexDelta(
    int VertexIndex,
    double X,
    double Y,
    double Z);

public sealed record ReferenceSculptEligibilityRequest(
    ImmutableArray<ReviewedReferenceView> Views,
    ImmutableArray<ReferenceMeshAnchorBinding> MeshBindings,
    Sha256Hash TopologySha256,
    double HeadBoundingBoxDiagonal,
    ImmutableArray<ReferenceSculptVertexDelta> Deltas,
    ImmutableArray<int> ProtectedNeckRingVertexIndices,
    double ObjectiveBefore,
    double ObjectiveAfter);

public sealed record ReferenceRaceMenuPresetSolverRequest(
    ReviewedReferencePresetDesign ReviewedDesign,
    ReferencePresetResourceSnapshot Snapshot,
    RaceMenuTriResponseMatrixBuildResult ResponseMatrix)
{
    public ReferencePresetRenderInput? RenderInput { get; init; }
    public Sha256Hash ReviewedDesignSha256 { get; init; }
    public ImmutableArray<int> ProtectedNeckRingVertexIndices
    {
        get;
        init;
    } = [];
}

public sealed record ReferenceRaceMenuPresetSolverResult(
    ImmutableDictionary<string, double> NativeMorphs,
    ImmutableDictionary<string, double> CustomMorphs,
    ImmutableArray<ReferenceSculptVertexDelta> Sculpt,
    ImmutableArray<ReferencePresetResidual> Residuals,
    ImmutableArray<ReferencePresetLoss> Losses,
    int Iterations,
    double Objective,
    Sha256Hash? ResultSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public string SculptNifIdentity { get; init; } = string.Empty;
    public string SculptShapeIdentity { get; init; } = string.Empty;
    public string SculptHost { get; init; } = string.Empty;

    public bool Accepted =>
        ResultSha256 is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferenceMeshAnchorBindRequest(
    ReviewedReferenceView View,
    ReferenceSemanticAnchor Anchor,
    ReferencePresetRenderInput RenderInput,
    string HeadNifIdentity,
    string HeadShapeIdentity,
    Sha256Hash ExpectedGeometrySha256,
    Sha256Hash ExpectedCameraSha256,
    Sha256Hash ExpectedRenderSha256,
    double RenderX,
    double RenderY)
{
    public Sha256Hash? ExpectedNifSha256 { get; init; }
    public Sha256Hash? ExpectedTopologySha256 { get; init; }
    public Sha256Hash? ExpectedRestPositionsSha256 { get; init; }
}

public sealed record ReferenceMeshAnchorBindResult(
    ReferenceMeshAnchorBinding? Binding,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted =>
        Binding is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReferencePresetComparisonRequest(
    ReferencePresetRenderInput RenderInput,
    ReviewedReferencePresetDesign ReviewedDesign,
    ReferenceRaceMenuPresetSolverResult SolverResult,
    WorkspacePath OutputRoot)
{
    public ReferencePresetResourceSnapshot? Snapshot { get; init; }
    public ReferencePresetIntake? Intake { get; init; }
}

public sealed record ReferencePresetComparisonArtifact(
    ReferenceImageViewRole? ViewRole,
    AssetPath Path,
    Sha256Hash ContentSha256,
    Sha256Hash RendererSha256,
    Sha256Hash CameraSha256,
    Sha256Hash GeometrySha256,
    Sha256Hash TextureSetSha256,
    Sha256Hash TintSetSha256,
    Sha256Hash? SourceReferenceSha256);

public sealed record ReferencePresetComparisonResult(
    ImmutableArray<ReferencePresetComparisonArtifact> Artifacts,
    ImmutableArray<ReferencePresetResidual> Residuals,
    ImmutableArray<ReferencePresetLoss> Losses,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetAuthoringProposal(
    int SchemaVersion,
    ReferencePresetAuthorityKind Authority,
    string ProjectId,
    FormReference Race,
    NpcSex Sex,
    float Weight,
    Sha256Hash ReviewedDesignSha256,
    Sha256Hash ResourceSnapshotSha256,
    ReferenceRaceMenuPresetSolverResult SolverResult,
    ReferencePresetComparisonResult Comparison,
    ImmutableArray<ReferencePresetLoss> Losses,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferenceRaceMenuPresetWriteRequest(
    PresetDocument Baseline,
    ReferencePresetAuthoringProposal Proposal,
    Sha256Hash ProposalSha256,
    WorkspacePath DestinationPath)
{
    public ReferencePresetResourceSnapshot? Snapshot { get; init; }
    public Sha256Hash ResourceSnapshotDocumentSha256 { get; init; }
}

public sealed record ReferencePresetWriteArtifact(
    WorkspacePath PresetPath,
    Sha256Hash PresetSha256,
    PresetDocument Readback,
    Sha256Hash ReadbackEvidenceSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record VerifiedReferencePreset(
    int SchemaVersion,
    ReferencePresetAuthorityKind Authority,
    string ProjectId,
    FormReference Race,
    NpcSex Sex,
    float Weight,
    Sha256Hash AuthoringProposalSha256,
    WorkspacePath PresetPath,
    Sha256Hash PresetSha256,
    Sha256Hash ReadbackEvidenceSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record VerifiedReferenceNpcHandoff(
    int SchemaVersion,
    ReferencePresetAuthorityKind Authority,
    string ProjectId,
    Sha256Hash VerifiedPresetDocumentSha256,
    WorkspacePath PresetPath,
    Sha256Hash PresetSha256,
    FormReference Race,
    NpcSex Sex,
    float Weight,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetDesignProposalRequest(
    ReferencePresetIntake Intake,
    Sha256Hash ExpectedIntakeSha256,
    WorkspacePath OutputRoot);

public sealed record ReferencePresetDesignProposalResult(
    bool Completed,
    LandmarkInferenceProposal? Proposal,
    Sha256Hash? ProposalSha256,
    ReferencePresetResourceSnapshot? ResourceSnapshot,
    Sha256Hash? ResourceSnapshotSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetWriteRequest(
    WorkspacePath IntakePath,
    Sha256Hash IntakeSha256,
    WorkspacePath InferenceProposalPath,
    Sha256Hash InferenceProposalSha256,
    WorkspacePath ReviewedDesignPath,
    Sha256Hash ReviewedDesignSha256,
    WorkspacePath ResourceSnapshotPath,
    Sha256Hash ResourceSnapshotSha256,
    WorkspacePath OutputRoot,
    bool Apply,
    Sha256Hash? AcceptedAuthoringProposalSha256);

public sealed record ReferencePresetWriteResult(
    bool Completed,
    ReferencePresetAuthoringProposal? Proposal,
    Sha256Hash? ProposalSha256,
    VerifiedReferencePreset? VerifiedPreset,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetNpcBuildRequest(
    ReferencePresetWriteRequest Authoring,
    RaceMenuJslotNpcBuildRequest JslotBuildRequest);

public sealed record ReferencePresetNpcBuildResult(
    bool Completed,
    VerifiedReferenceNpcHandoff? Handoff,
    RaceMenuJslotNpcBuildResult? Build,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetProgress(
    ReferencePresetProgressStage Stage,
    int CompletedUnits,
    int TotalUnits,
    string Message);

public sealed record ReferencePresetSessionDocument(
    ReferencePresetSessionDocumentKind Kind,
    ReferencePresetIntake? Intake = null,
    LandmarkInferenceProposal? InferenceProposal = null,
    ReviewedReferencePresetDesign? ReviewedDesign = null,
    ReferencePresetResourceSnapshot? ResourceSnapshot = null,
    ReferencePresetAuthoringProposal? AuthoringProposal = null,
    VerifiedReferencePreset? VerifiedPreset = null,
    VerifiedReferenceNpcHandoff? VerifiedNpcHandoff = null);

public sealed record ReferencePresetSessionWriteRequest(
    WorkspacePath DestinationPath,
    ReferencePresetSessionDocument Document);

public sealed record ReferencePresetSessionWriteResult(
    bool Written,
    Sha256Hash? ContentSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ReferencePresetSessionReadRequest(
    WorkspacePath SourcePath,
    Sha256Hash ExpectedSha256,
    ReferencePresetSessionDocumentKind ExpectedKind);

public sealed record ReferencePresetSessionReadResult(
    ReferencePresetSessionDocument? Document,
    Sha256Hash? ContentSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IReferenceImageDecoder
{
    ValueTask<ReferenceImageDecodeResult> DecodeAsync(
        ReferenceImageDecodeRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceImageBytesDecoder
{
    ValueTask<ReferenceImageDecodeResult> DecodeBytesAsync(
        ReferenceImageDecodeRequest request,
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken);
}

public interface IReferenceFaceInferenceService
{
    ValueTask<ReferenceFaceInferenceResult> InferAsync(
        ReferenceFaceInferenceRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceFaceInferenceNativeApi
{
    ValueTask<ReferenceFaceNativeInferenceResult> InferAsync(
        ReferenceFaceInferenceRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceDescriptionInterpreter
{
    ValueTask<ReferenceDescriptionInterpretResult> InterpretAsync(
        ReferenceDescriptionInterpretRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceSemanticLandmarkProjector
{
    ValueTask<ReferenceSemanticLandmarkProjectionResult> ProjectAsync(
        ReferenceSemanticLandmarkProjectionRequest request,
        CancellationToken cancellationToken);
}

public interface IReferencePresetMeshAnchorBinder
{
    ReferenceMeshAnchorBindResult Bind(
        ReferenceMeshAnchorBindRequest request);
}

public interface IReferencePresetResourceSnapshotService
{
    ValueTask<ReferencePresetResourceSnapshotResult> CreateAsync(
        ReferencePresetResourceSnapshotRequest request,
        CancellationToken cancellationToken);
}

public interface IRaceMenuTriResponseMatrixBuilder
{
    ValueTask<RaceMenuTriResponseMatrixBuildResult> BuildAsync(
        RaceMenuTriResponseMatrixBuildRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceRaceMenuPresetSolver
{
    ValueTask<ReferenceRaceMenuPresetSolverResult> SolveAsync(
        ReferenceRaceMenuPresetSolverRequest request,
        CancellationToken cancellationToken);
}

public interface IReferencePresetRenderInputBuilder
{
    ValueTask<ReferencePresetRenderInputResult> BuildAsync(
        ReferencePresetRenderInputRequest request,
        CancellationToken cancellationToken);
}

public interface IReferencePresetComparisonService
{
    ValueTask<ReferencePresetComparisonResult> RenderAsync(
        ReferencePresetComparisonRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceRaceMenuPresetWriter
{
    ValueTask<ReferencePresetWriteArtifact> WriteAsync(
        ReferenceRaceMenuPresetWriteRequest request,
        CancellationToken cancellationToken);
}

public interface IReferencePresetSessionService
{
    ValueTask<ReferencePresetSessionWriteResult> WriteIntakeTemplateAsync(
        WorkspacePath destinationPath,
        CancellationToken cancellationToken) => ValueTask.FromResult(
            new ReferencePresetSessionWriteResult(false, null,
                [new Diagnostic("reference-template-unavailable", DiagnosticSeverity.Error,
                    "This session service does not support intake templates.")]));

    ValueTask<ReferencePresetSessionWriteResult> WriteAsync(
        ReferencePresetSessionWriteRequest request,
        CancellationToken cancellationToken);

    ValueTask<ReferencePresetSessionReadResult> ReadAsync(
        ReferencePresetSessionReadRequest request,
        CancellationToken cancellationToken);
}

public interface IReferencePresetAuthoringTransaction
{
    ValueTask<ReferencePresetDesignProposalResult> ProposeDesignAsync(
        ReferencePresetDesignProposalRequest request,
        IProgress<ReferencePresetProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ReferencePresetWriteResult> WritePresetAsync(
        ReferencePresetWriteRequest request,
        IProgress<ReferencePresetProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ReferencePresetNpcBuildResult> WritePresetAndBuildNpcAsync(
        ReferencePresetNpcBuildRequest request,
        IProgress<ReferencePresetProgress>? progress,
        CancellationToken cancellationToken);
}
