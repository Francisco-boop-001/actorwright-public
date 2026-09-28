using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>The two morph encodings admitted from a Skyrim FRTRI003 file.</summary>
public enum SseTriHeadMorphEncoding
{
    DenseInt16,
    ModifierAbsolutePositions
}

/// <summary>One compact indexed float32 vertex delta from a parsed FRTRI003 morph.</summary>
public readonly record struct SseTriHeadVertexDelta(int VertexIndex, Vector3 Delta);

/// <summary>One case-insensitively named morph from a hash-bound FRTRI003 source.</summary>
public sealed record SseTriHeadMorph(
    string Name,
    SseTriHeadMorphEncoding Encoding,
    float Multiplier,
    ImmutableArray<SseTriHeadVertexDelta> Deltas);

/// <summary>
/// Strictly parsed FRTRI003 data. SourcePath is a canonical Data-relative path
/// such as meshes/actors/character/character assets/femaleheadchargen.tri.
/// </summary>
public sealed record SseTriHeadDocument(
    AssetPath SourcePath,
    Sha256Hash SourceSha256,
    int VertexCount,
    int TriangleCount,
    int UvCount,
    uint Flags,
    ImmutableArray<Vector3> BaseVertices,
    ImmutableArray<SseTriHeadMorph> Morphs);

public sealed record SseTriHeadReadRequest(
    AssetPath SourcePath,
    Sha256Hash ExpectedSourceSha256,
    ImmutableArray<byte> Bytes);

public sealed record SseTriHeadReadResult(
    bool Accepted,
    SseTriHeadDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseTriHeadReader
{
    SseTriHeadReadResult Read(SseTriHeadReadRequest request);
}

public enum SkyrimRaceMenuSliderGender
{
    Male,
    Female
}

public enum SkyrimRaceMenuSliderType
{
    Slider,
    Preset,
    HeadPart
}

/// <summary>RaceMenu/skee64 face-slider category bit values.</summary>
public enum SkyrimRaceMenuSliderCategory
{
    Body = 4,
    Head = 8,
    Face = 16,
    Eyes = 32,
    Brow = 64,
    Mouth = 128,
    Hair = 256,
    Extra = 512,
    Expressions = 1024
}

/// <summary>One winning race/sex slider definition after loaded-plugin precedence.</summary>
public sealed record SkyrimRaceMenuSliderDefinition(
    string RaceEditorId,
    SkyrimRaceMenuSliderGender Gender,
    string Name,
    SkyrimRaceMenuSliderCategory Category,
    SkyrimRaceMenuSliderType Type,
    string LowerBound,
    string UpperBound,
    int PresetCount,
    PluginName SourcePlugin,
    AssetPath SourceAsset,
    int SourceLine);

/// <summary>
/// Ordered extended TRI files mapped from one chargen TRI. Paths are canonical
/// Data-relative paths under meshes/actors/character/FaceGenMorphs/morphs.
/// </summary>
public sealed record SkyrimRaceMenuMorphExtension(
    AssetPath BaseChargenTri,
    ImmutableArray<AssetPath> ExtendedTriPaths);

/// <summary>Winner-resolved config bytes supplied by a later provider layer.</summary>
public sealed record SkyrimRaceMenuCatalogAsset(
    AssetPath Path,
    Sha256Hash ExpectedSha256,
    ImmutableArray<byte> Bytes);

/// <summary>
/// Pure catalog input. LoadedPlugins must be in ascending runtime load order;
/// Assets must already represent one winner per virtual path.
/// </summary>
public sealed record SkyrimRaceMenuCatalogParseRequest(
    ImmutableArray<PluginName> LoadedPlugins,
    ImmutableArray<SkyrimRaceMenuCatalogAsset> Assets);

public sealed record SkyrimRaceMenuSliderCatalog(
    ImmutableArray<SkyrimRaceMenuSliderDefinition> Sliders,
    ImmutableArray<SkyrimRaceMenuMorphExtension> MorphExtensions);

/// <summary>A readable declaration, not admitted authority; the extended path may be unsafe.</summary>
public sealed record SkyrimRaceMenuMorphDependencyObservation(
    AssetPath BaseChargenTri,
    string ExtendedTriPath);

public sealed record SkyrimRaceMenuCatalogParseResult(
    bool Accepted,
    SkyrimRaceMenuSliderCatalog? Catalog,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ImmutableArray<SkyrimRaceMenuMorphDependencyObservation> ObservedMorphDependencies { get; init; } = [];
}

public interface IRaceMenuSliderCatalogParserCore
{
    SkyrimRaceMenuCatalogParseResult Parse(SkyrimRaceMenuCatalogParseRequest request);
}

/// <summary>FaceGeom bake merge order is Race, Chargen, Mesh, then Extended.</summary>
public enum SkyrimFaceMorphTriRole
{
    Race,
    Chargen,
    Mesh,
    Extended
}

public sealed record SkyrimFaceMorphTriSource(
    SkyrimFaceMorphTriRole Role,
    SseTriHeadDocument Document);

/// <summary>
/// One RaceMenu custom ValueSet entry in source document order. The order is
/// explicit because upstream applies channels sequentially before float32 output.
/// </summary>
public sealed record SkyrimRaceMenuCustomMorphValue(string Name, float Value);

public enum SkyrimFaceMorphContributionKind
{
    Race,
    Nam9,
    Vampire,
    RaceKeyword,
    Nama,
    RaceMenuCustom,
    RaceMenuSculpt,
    SkinnyWeight
}

public sealed record SkyrimFaceMorphContribution(
    SkyrimFaceMorphContributionKind Kind,
    string Detail,
    float Weight);

/// <summary>
/// One de-duplicated additive channel. Repeated named contributions sum their
/// weights while retaining the first selected TRI deltas, matching upstream.
/// </summary>
public sealed record SkyrimFaceMorphChannel(
    string Name,
    float Weight,
    ImmutableArray<SseTriHeadVertexDelta> Deltas,
    AssetPath? MorphSourcePath,
    SkyrimFaceMorphTriRole? MorphSourceRole,
    ImmutableArray<SkyrimFaceMorphContribution> Contributions);

/// <summary>
/// All typed, already-materialized inputs required to build one HDPT shape's
/// upstream-compatible morph plan. ExtendedMorphTris must exactly match the
/// catalog mapping for ChargenMorphTri after subtracting the exact optional
/// paths declared in ExplicitUnavailableExtendedTris; undeclared omissions and
/// unexpected extras refuse.
/// </summary>
public sealed record SkyrimFaceMorphPlanBuildRequest(
    int VertexCount,
    string MorphRaceEditorId,
    bool IsFemale,
    SkyrimFaceMorphSnapshot NativeMorphs,
    ImmutableArray<string> RaceKeywordEditorIds,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> CustomMorphs,
    ImmutableArray<RaceMenuSculptPart> SculptParts,
    float ActorWeight,
    SkyrimRaceMenuSliderCatalog Catalog,
    SkyrimFaceMorphTriSource? RaceMorphTri,
    SkyrimFaceMorphTriSource? ChargenMorphTri,
    SkyrimFaceMorphTriSource? MeshMorphTri,
    ImmutableArray<SkyrimFaceMorphTriSource> ExtendedMorphTris,
    bool RequireAllCustomMorphs = true)
{
    public ImmutableArray<AssetPath> ExplicitUnavailableExtendedTris
    {
        get;
        init;
    } = [];
}

public sealed record SkyrimFaceMorphPlan(
    int VertexCount,
    ImmutableArray<AssetPath> MergedTriOrder,
    ImmutableArray<SkyrimFaceMorphChannel> Channels,
    ImmutableArray<string> ResolvedCustomMorphNames = default);

public sealed record SkyrimFaceMorphPlanBuildResult(
    bool Accepted,
    SkyrimFaceMorphPlan? Plan,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseFaceMorphPlanBuilder
{
    SkyrimFaceMorphPlanBuildResult Build(SkyrimFaceMorphPlanBuildRequest request);
}

public sealed record SkyrimFaceMorphEvaluationRequest(
    ImmutableArray<Vector3> BasePositions,
    SkyrimFaceMorphPlan Plan);

public sealed record SkyrimFaceMorphEvaluationResult(
    bool Accepted,
    ImmutableArray<Vector3> Positions,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseFaceMorphEvaluator
{
    SkyrimFaceMorphEvaluationResult Evaluate(SkyrimFaceMorphEvaluationRequest request);
}

/// <summary>
/// Explicit complete-carrier binding for one requested selected-headpart shape.
/// BasePositions are exact selected-NIF rest positions and use packed float32 XYZ
/// hashing compatible with the bounded carrier merge seam. ChargenMorphHost is
/// present only when the shape has an explicit CharGen TRI host.
/// </summary>
public sealed record SkyrimRaceMenuFaceBakeCarrierShapeBinding(
    string CarrierShapeName,
    AssetPath? ChargenMorphHost,
    int VertexCount,
    Sha256Hash ExpectedTopologySha256,
    Sha256Hash ExpectedBasePositionSha256,
    ImmutableArray<Vector3> BasePositions,
    ImmutableArray<SseSelectedHeadpartPackedNormalSentinel>
        PackedNormalSentinels = default)
{
    /// <summary>Native record/model provenance for blocking effective TRI topology checks.</summary>
    public FormReference? NativeHeadPart { get; init; }
    public AssetPath? NativeModelNif { get; init; }
}

/// <summary>
/// Hash-bound TRI bytes assigned to one carrier shape. Field identity fixes the
/// Race, Chargen, Mesh, Extended merge roles; extended order remains authoritative.
/// Chargen is optional for mesh-only/rest-only selected headparts. Race and
/// Extended inputs are admitted only when an explicit ChargenMorphHost exists;
/// Mesh is the record-declared NAM0=1 morph source.
/// </summary>
public sealed record SkyrimRaceMenuFaceBakeShapeTriInputs(
    string CarrierShapeName,
    SseTriHeadReadRequest? RaceMorphTri,
    SseTriHeadReadRequest? ChargenMorphTri,
    SseTriHeadReadRequest? MeshMorphTri,
    ImmutableArray<SseTriHeadReadRequest> ExtendedMorphTris);

/// <summary>
/// Pure multi-shape bake input. Catalog assets and every TRI are already
/// winner-resolved and hash-bound; this service performs no provider discovery.
/// Any catalog-declared extension that could not be materialized must appear in
/// <see cref="ExplicitUnavailableExtendedTris"/> so omissions fail closed.
/// </summary>
public sealed record SkyrimRaceMenuFaceBakeRequest(
    string MorphRaceEditorId,
    bool IsFemale,
    SkyrimFaceMorphSnapshot NativeMorphs,
    ImmutableArray<string> RaceKeywordEditorIds,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> CustomMorphs,
    ImmutableArray<RaceMenuSculptPart> SculptParts,
    float ActorWeight,
    SkyrimRaceMenuCatalogParseRequest CatalogRequest,
    ImmutableArray<SkyrimRaceMenuFaceBakeCarrierShapeBinding> CarrierShapes,
    ImmutableArray<SkyrimRaceMenuFaceBakeShapeTriInputs> ShapeTriInputs,
    ImmutableArray<AssetPath> ExplicitUnavailableExtendedTris);

/// <summary>How one parsed TRI participated in a selected-headpart bake.</summary>
public enum SkyrimRaceMenuFaceBakeTriDisposition
{
    /// <summary>The TRI declared morph geometry eligible for channel resolution.</summary>
    EligibleMorphSource,

    /// <summary>
    /// The TRI declared no morphs. Its BaseVertices are evidence only and never
    /// replace, pad, or otherwise modify selected-NIF rest positions.
    /// </summary>
    EmptyMorphNoOp
}

/// <summary>Hash-bound evidence for one parsed TRI assigned to a carrier shape.</summary>
public sealed record SkyrimRaceMenuFaceBakeTriEvidence(
    SkyrimFaceMorphTriRole Role,
    AssetPath SourcePath,
    Sha256Hash SourceSha256,
    int DeclaredVertexCount,
    int MorphCount,
    SkyrimRaceMenuFaceBakeTriDisposition Disposition,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Sha256Hash? EvidenceId = null);

/// <summary>Deterministic final XYZ output for one explicitly bound carrier shape.</summary>
public sealed record SkyrimRaceMenuFaceBakeShapeOutput(
    string CarrierShapeName,
    AssetPath? ChargenMorphHost,
    int VertexCount,
    Sha256Hash TopologySha256,
    Sha256Hash BasePositionSha256,
    Sha256Hash FinalPositionSha256,
    ImmutableArray<Vector3> FinalPositions,
    ImmutableArray<AssetPath> MergedTriOrder,
    ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> TriEvidence)
{
    public ImmutableArray<SseSelectedHeadpartPackedNormalSentinel>
        PackedNormalSentinels { get; init; } = [];
}

public sealed record SkyrimRaceMenuFaceBakeResult(
    bool Accepted,
    ImmutableArray<SkyrimRaceMenuFaceBakeShapeOutput> Shapes,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimRaceMenuFaceBakeService
{
    SkyrimRaceMenuFaceBakeResult Bake(SkyrimRaceMenuFaceBakeRequest request);
}
