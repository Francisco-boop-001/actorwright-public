using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>The known RaceMenu appearance surfaces that require an explicit preservation route.</summary>
public enum RaceMenuNpcAppearanceField
{
    Race,
    Sex,
    HeadParts,
    HairColor,
    Weight,
    HeadTexture,
    FaceTextures,
    FaceMorphPresets,
    FaceMorphSliders,
    CustomMorphs,
    Sculpt,
    FaceTints,
    BodyMorphs,
    Overlays,
    NodeTransforms,
    SkinOverrides,
    ModNames,
    Mods,
    Version,
    UnknownFields
}

/// <summary>
/// The selected preservation mechanism. This classifies the plan; it does not
/// claim that a game-facing write or runtime application has occurred.
/// </summary>
public enum RaceMenuNpcFieldCoverageKind
{
    Mapped,
    Baked,
    RuntimeDeclared,
    Blocked
}

public static class RaceMenuNpcAppearanceFieldExtensions
{
    public static string ToWireName(this RaceMenuNpcAppearanceField field) => field switch
    {
        RaceMenuNpcAppearanceField.Race => "race",
        RaceMenuNpcAppearanceField.Sex => "sex",
        RaceMenuNpcAppearanceField.HeadParts => "head-parts",
        RaceMenuNpcAppearanceField.HairColor => "hair-color",
        RaceMenuNpcAppearanceField.Weight => "weight",
        RaceMenuNpcAppearanceField.HeadTexture => "head-texture",
        RaceMenuNpcAppearanceField.FaceTextures => "face-textures",
        RaceMenuNpcAppearanceField.FaceMorphPresets => "face-morph-presets",
        RaceMenuNpcAppearanceField.FaceMorphSliders => "face-morph-sliders",
        RaceMenuNpcAppearanceField.CustomMorphs => "custom-morphs",
        RaceMenuNpcAppearanceField.Sculpt => "sculpt",
        RaceMenuNpcAppearanceField.FaceTints => "face-tints",
        RaceMenuNpcAppearanceField.BodyMorphs => "body-morphs",
        RaceMenuNpcAppearanceField.Overlays => "overlays",
        RaceMenuNpcAppearanceField.NodeTransforms => "node-transforms",
        RaceMenuNpcAppearanceField.SkinOverrides => "skin-overrides",
        RaceMenuNpcAppearanceField.ModNames => "mod-names",
        RaceMenuNpcAppearanceField.Mods => "mods",
        RaceMenuNpcAppearanceField.Version => "version",
        RaceMenuNpcAppearanceField.UnknownFields => "unknown-fields",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported RaceMenu field.")
    };

    public static bool TryParseRuntimeWireName(string value, out RaceMenuNpcAppearanceField field)
    {
        foreach (var candidate in RuntimeFields)
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.Ordinal))
            {
                field = candidate;
                return true;
            }
        }

        field = default;
        return false;
    }

    public static ImmutableArray<RaceMenuNpcAppearanceField> RuntimeFields { get; } =
    [
        RaceMenuNpcAppearanceField.Overlays,
        RaceMenuNpcAppearanceField.NodeTransforms,
        RaceMenuNpcAppearanceField.SkinOverrides
    ];
}

/// <summary>One hash-pinned runtime sidecar manifest referenced by the preset bundle.</summary>
public sealed record RaceMenuNpcRuntimeRouteAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

/// <summary>
/// Hash-pinned authority for Skyrim record signatures, copied provider plugins,
/// the winning race/sex tint-index map, and the skin-tint/QNAM derivation rule.
/// </summary>
public sealed record RaceMenuNpcRecordAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

/// <summary>
/// Hash-bound authority for the selected new NPC's complete Skyrim skin route.
/// The referenced manifest must bind the intended WNAM behavior and the
/// winning race/ARMO/ARMA/TXST plus body, hand, and foot texture providers.
/// It is intentionally separate from the JSlot because RaceMenu presets do
/// not own player race or whole-body skin state.
/// </summary>
public sealed record RaceMenuNpcWholeSkinAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

/// <summary>
/// Closed RaceMenu source bundle. The bundle manifest binds the preset,
/// matching CharGen NIF/DDS, provider manifest, dependency manifest, and any
/// optional runtime-route manifest into one identity.
/// </summary>
public sealed record RaceMenuNpcPresetBundle(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256,
    WorkspacePath PresetPath,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath CharGenFaceGeom,
    Sha256Hash ExpectedCharGenFaceGeomSha256,
    WorkspacePath CharGenFaceTint,
    Sha256Hash ExpectedCharGenFaceTintSha256,
    RaceMenuNpcRecordAuthority RecordAuthority,
    RaceMenuNpcRuntimeRouteAuthority? RuntimeRoutes = null);

/// <summary>
/// Complete typed intent for a future preset-derived NPC build. Appearance
/// planning is read-only and must succeed before any output root is created.
/// </summary>
public sealed record RaceMenuNpcBuildRequest(
    GameEdition Edition,
    RaceMenuNpcPresetBundle PresetBundle,
    BlankNpcProviderBindingRequest ProviderContext,
    WorkspacePath OutputRoot,
    PluginName OutputPlugin,
    NpcCreationIdentity Identity,
    SkyrimNpcCreationTraits Traits,
    SkyrimNpcCreationReferences References,
    SkyrimNpcCreationStats Stats)
{
    /// <summary>
    /// Required for a newly allocated NPC. Existing-NPC appearance overrides
    /// preserve their source actor's skin route and therefore do not consume
    /// this authority.
    /// </summary>
    public RaceMenuNpcWholeSkinAuthority? WholeSkinAuthority { get; init; }

    /// <summary>
    /// Output plugin kind for a newly allocated NPC (request `output.pluginType`,
    /// default `esp`). Existing-NPC targets keep their source plugin kind and
    /// do not consume this value.
    /// </summary>
    public BlankNpcPluginType PluginType { get; init; } = BlankNpcPluginType.Esp;

    /// <summary>
    /// Optional complete copied plugin-order authority supplied by a
    /// Manager-owned preset-selection transaction. This includes indirect
    /// master providers that may not own a selected race/head record.
    /// Prepared execution-request JSON cannot populate this field.
    /// </summary>
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];

    /// <summary>
    /// Optional original-app target. Null allocates the requested new NPC;
    /// a value applies the same admitted preset to one source-owned NPC and
    /// keys FaceGen/BodyGen artifacts to that source plugin and local FormID.
    /// </summary>
    public RaceMenuExistingNpcTarget? ExistingNpcTarget { get; init; }
}

public sealed record RaceMenuExistingNpcTarget(
    WorkspacePath SourcePlugin,
    Sha256Hash ExpectedSourcePluginSha256,
    FormId TargetFormId);

public sealed record RaceMenuNpcFieldCoverage(
    RaceMenuNpcAppearanceField Field,
    bool Present,
    RaceMenuNpcFieldCoverageKind Classification,
    string Authority,
    Sha256Hash? AuthoritySha256 = null);

/// <summary>
/// One plugin-local form reference whose declared signature has been checked
/// against the hash-bound copied winning provider plugin. Reference retains
/// the record's originating FormKey; ProviderPluginName identifies the file
/// whose winning override bytes were inspected.
/// </summary>
public sealed record RaceMenuNpcFormBinding(
    RecordSignature Signature,
    FormReference SourceReference,
    FormReference Reference,
    PluginName ProviderPluginName,
    WorkspacePath ProviderPlugin,
    Sha256Hash ProviderPluginSha256,
    NpcHeadPartType? HeadPartType)
{
    /// <summary>
    /// The HDPT PNAM value exactly as declared, kept beside the projected
    /// <see cref="HeadPartType" /> so mod-defined types beyond the closed
    /// 0..9 buckets stay reportable. Null when the binding was declared by
    /// wire name or is not an HDPT.
    /// </summary>
    public uint? HeadPartRawType { get; init; }

    /// <summary>True when the declared PNAM lies outside the closed editor buckets.</summary>
    public bool HasExtendedHeadPartType => HeadPartRawType > (uint)NpcHeadPartType.HeadRear;
}

/// <summary>Closed, hash-bound route for the NPC's authored Skyrim hair color.</summary>
public abstract record RaceMenuNpcHairColorAuthority
{
    private protected RaceMenuNpcHairColorAuthority()
    {
    }
}

public sealed record RaceMenuNpcExternalHairColorAuthority(
    uint PackedRgb,
    RaceMenuNpcFormBinding Binding) : RaceMenuNpcHairColorAuthority;

public sealed record RaceMenuNpcOutputOwnedHairColorAuthority(
    uint PackedRgb,
    FormId AllocatedLocalFormId,
    Sha256Hash AuthoritySha256) : RaceMenuNpcHairColorAuthority;

public sealed record RaceMenuResolvedHeadPart(
    PresetHeadPart Source,
    RaceMenuNpcFormBinding Binding);

public enum RaceMenuNpcHeadPartDispositionKind
{
    MappedRecord,
    Baked
}

/// <summary>
/// Complete disposition for one RaceMenu head-part row. A mapped row becomes
/// PNAM; a baked row is preserved only through the admitted CharGen geometry.
/// </summary>
public sealed record RaceMenuNpcHeadPartDisposition(
    PresetHeadPart Source,
    RaceMenuNpcHeadPartDispositionKind Kind,
    RaceMenuResolvedHeadPart? MappedHeadPart,
    Sha256Hash? FaceGeomSha256);

/// <summary>
/// One RaceMenu tint entry mapped to the NPC record's TINI/TINC/TINV/TIAS
/// representation. The source ARGB supplies RGB; TINC alpha remains zero.
/// </summary>
public sealed record RaceMenuNpcTintLayerBinding(
    PresetTint Source,
    SkyrimFaceTintLayer Layer,
    bool IsSkinTint);

public enum RaceMenuNpcTintDispositionKind
{
    MappedRecord,
    Baked,
    Inactive
}

/// <summary>
/// Complete disposition for one source tint row. Only mapped rows become NPC
/// TINI layers; baked rows are bound to the admitted CharGen pair, and inactive
/// rows are permitted only when their source alpha is zero.
/// </summary>
public sealed record RaceMenuNpcTintDisposition(
    PresetTint Source,
    RaceMenuNpcTintDispositionKind Kind,
    RaceMenuNpcTintLayerBinding? MappedLayer,
    Sha256Hash? FaceGeomSha256,
    Sha256Hash? FaceTintSha256);

public enum RaceMenuNpcQnamSourceKind
{
    MappedSkinTint
}

/// <summary>
/// Deterministic QNAM derivation from the explicitly selected skin tint. The
/// normalized channels must equal the source tint RGB channels divided by 255.
/// </summary>
public sealed record RaceMenuNpcQnamDerivation(
    RaceMenuNpcQnamSourceKind SourceKind,
    int SourceJslotTintIndex,
    float Red,
    float Green,
    float Blue,
    Sha256Hash AuthoritySha256);

/// <summary>
/// Hash-bound, read-only appearance plan. Source dependencies preserve preset
/// provenance, while required output masters contain only the template and
/// emitted external references. <see cref="IsReady"/> means every present
/// field has a selected authority; it is not plugin, runtime, or visual proof.
/// </summary>
public sealed record RaceMenuNpcAppearancePlan(
    string SchemaVersion,
    string BundleId,
    RaceMenuNpcBuildRequest Request,
    PresetDocument Preset,
    BlankNpcProviderArtifact Provider,
    Sha256Hash BundleManifestSha256,
    string RecordAuthorityId,
    Sha256Hash RecordAuthoritySha256,
    Sha256Hash CharGenFaceGeomSha256,
    Sha256Hash CharGenFaceTintSha256,
    RaceMenuNpcFormBinding RaceBinding,
    ImmutableArray<RaceMenuNpcHeadPartDisposition> HeadPartDispositions,
    ImmutableArray<RaceMenuResolvedHeadPart> ResolvedHeadParts,
    RaceMenuNpcFormBinding? ResolvedHeadTexture,
    RaceMenuNpcHairColorAuthority? ResolvedHairColor,
    ImmutableArray<RaceMenuNpcTintDisposition> TintDispositions,
    ImmutableArray<RaceMenuNpcTintLayerBinding> ResolvedTintLayers,
    RaceMenuNpcQnamDerivation? QnamDerivation,
    ImmutableArray<PluginName> SourceDependencies,
    ImmutableArray<PluginName> RequiredOutputMasters,
    ImmutableArray<RaceMenuNpcFieldCoverage> FieldCoverage,
    ImmutableArray<Diagnostic> Diagnostics,
    bool RuntimeAuthority)
{
    /// <summary>
    /// Deduplicated provider plugins reopened from the record authority. These
    /// paths are explicit build inputs; they are not output-master inference.
    /// </summary>
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];

    public bool IsReady =>
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) &&
        !FieldCoverage.Any(item => item.Present && item.Classification == RaceMenuNpcFieldCoverageKind.Blocked);
}

public sealed record RaceMenuNpcAppearancePlanResult(
    bool Accepted,
    RaceMenuNpcAppearancePlan? Plan,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuNpcAppearancePlanService
{
    ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
        RaceMenuNpcBuildRequest request,
        CancellationToken cancellationToken);
}
