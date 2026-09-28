using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>The product intent that drives an NPC's later gameplay workflow.</summary>
public enum NpcCreationRole
{
    Civilian,
    Combatant,
    Follower,
    Merchant,
    StaticValidation
}

/// <summary>Identity fields owned by a newly allocated NPC record.</summary>
public sealed record NpcCreationIdentity(EditorId EditorId, NpcName Name);

/// <summary>Required Skyrim NPC traits and explicitly selected actor flags.</summary>
public sealed record SkyrimNpcCreationTraits(
    NpcSex Sex,
    NpcCreationRole Role,
    bool IsUnique,
    bool IsEssential,
    bool IsProtected,
    bool Respawns,
    bool AutoCalcStats);

/// <summary>Required external records used by a complete standalone Skyrim NPC.</summary>
public sealed record SkyrimNpcCreationReferences(
    FormReference Race,
    FormReference Voice,
    FormReference Class,
    FormReference CombatStyle,
    FormReference? DefaultOutfit);

/// <summary>Explicit Skyrim actor values and level bounds for a newly created NPC.</summary>
public sealed record SkyrimNpcCreationStats(
    NpcLevelValue Level,
    short MagickaOffset,
    short StaminaOffset,
    short HealthOffset,
    ushort CalcMinLevel,
    ushort CalcMaxLevel,
    short SpeedMultiplier,
    short DispositionBase,
    short BleedoutOverride,
    ushort BaseHealth,
    ushort BaseMagicka,
    ushort BaseStamina,
    float Height,
    float Weight,
    ushort FarAwayModelDistance);

/// <summary>
/// Closed appearance authority used by new-NPC creation. Callers must choose
/// either the admitted template carrier or a complete authored Skyrim payload.
/// </summary>
public abstract record NpcCreationAppearanceSource
{
    private protected NpcCreationAppearanceSource()
    {
    }
}

/// <summary>
/// Uses the qualified template NPC as the complete appearance carrier. This is
/// the explicit Gate 1 baseline and does not claim preset-derived authorship.
/// </summary>
public sealed record TemplateCarrierNpcAppearanceSource : NpcCreationAppearanceSource
{
    public static TemplateCarrierNpcAppearanceSource Instance { get; } = new();
}

/// <summary>A packed 24-bit RGB value used to author an output-owned CLFM record.</summary>
public readonly record struct SkyrimPackedRgb(uint Value);

/// <summary>Normalized RGB channels written to the Skyrim NPC QNAM field.</summary>
public readonly record struct SkyrimQnamRgb(float Red, float Green, float Blue);

/// <summary>Closed source for the NPC's Skyrim hair-color record.</summary>
public abstract record SkyrimNpcHairColorSource
{
    private protected SkyrimNpcHairColorSource()
    {
    }
}

/// <summary>Uses an existing CLFM record from an external plugin provider.</summary>
public sealed record ExternalSkyrimNpcHairColor(FormReference Clfm) : SkyrimNpcHairColorSource;

/// <summary>
/// Allocates a new output-owned CLFM record at the specified plugin-local
/// FormID and authors its color from a packed 24-bit RGB value.
/// </summary>
public sealed record OutputOwnedSkyrimNpcHairColor(
    FormId AllocatedLocalFormId,
    SkyrimPackedRgb PackedRgb) : SkyrimNpcHairColorSource;

/// <summary>Closed source for one ordered Skyrim PNAM headpart.</summary>
public abstract record SkyrimNpcHeadPartSource
{
    private protected SkyrimNpcHeadPartSource()
    {
    }

    /// <summary>The Skyrim HDPT type represented by this ordered source.</summary>
    public abstract NpcHeadPartType Type { get; }
}

/// <summary>Uses an existing HDPT record from an external plugin provider.</summary>
public sealed record ExternalSkyrimNpcHeadPart(
    FormReference Hdpt,
    NpcHeadPartType HeadPartType) : SkyrimNpcHeadPartSource
{
    public ExternalSkyrimNpcHeadPart(NpcHeadPartSelection selection)
        : this(selection.Reference, selection.Type)
    {
    }

    public override NpcHeadPartType Type => HeadPartType;
}

/// <summary>
/// Allocates one output-owned Face HDPT and copies its head-model semantics
/// from a separately qualified external Face HDPT before binding the private
/// output-owned texture set.
/// </summary>
public sealed record OutputOwnedSkyrimNpcFaceHeadPart(
    FormId AllocatedLocalFormId,
    FormReference QualifiedExternalFaceHdpt) : SkyrimNpcHeadPartSource
{
    public override NpcHeadPartType Type => NpcHeadPartType.Face;

    /// <summary>
    /// Retains the qualified source HDPT EditorID when a complete external
    /// RaceMenu export keeps that identity as its face-shape name.
    /// </summary>
    public bool PreserveQualifiedEditorId { get; init; }
}

/// <summary>Closed source for the NPC's Skyrim FTST texture-set record.</summary>
public abstract record SkyrimNpcFaceTextureSetSource
{
    private protected SkyrimNpcFaceTextureSetSource()
    {
    }
}

/// <summary>Uses an existing TXST record from an external plugin provider.</summary>
public sealed record ExternalSkyrimNpcFaceTextureSet(FormReference Txst)
    : SkyrimNpcFaceTextureSetSource;

/// <summary>
/// Exact private head textures authored into an output-owned Skyrim TXST.
/// The five required paths cover the accepted face chain; the remaining
/// engine slots are explicit nullable values rather than inferred defaults.
/// </summary>
public sealed record SkyrimPrivateHeadTexturePaths(
    AssetPath Diffuse,
    AssetPath NormalOrGloss,
    AssetPath GlowOrDetailMap,
    AssetPath Height,
    AssetPath BacklightMaskOrSpecular,
    AssetPath? EnvironmentMaskOrSubsurfaceTint = null,
    AssetPath? Environment = null,
    AssetPath? Multilayer = null);

/// <summary>
/// Allocates an output-owned FaceGen TXST with a model-space normal map and
/// the exact private head texture paths supplied by the caller.
/// </summary>
public sealed record OutputOwnedSkyrimNpcFaceTextureSet(
    FormId AllocatedLocalFormId,
    SkyrimPrivateHeadTexturePaths Paths) : SkyrimNpcFaceTextureSetSource;

/// <summary>
/// Creates one NPC-private exposed-torso outfit chain. The source ARMA, ARMO,
/// and OTFT are cloned into the output plugin; only the cloned ARMA female skin
/// TXST and the two containing links are changed.
/// </summary>
public sealed record OutputOwnedSkyrimNpcExposedOutfitSkinBinding(
    FormId AllocatedArmorAddonLocalFormId,
    FormId AllocatedArmorLocalFormId,
    FormId AllocatedOutfitLocalFormId,
    RaceMenuNpcFormBinding SourceOutfit,
    RaceMenuNpcFormBinding SourceArmor,
    RaceMenuNpcFormBinding SourceArmorAddon,
    RaceMenuNpcFormBinding TargetFemaleSkinTextureSet);

/// <summary>
/// One output-owned naked-skin ARMA cloned from the accepted whole-skin route.
/// The female model is replaced with a package-owned BodySlide-generated mesh;
/// texture authority remains the accepted whole-skin TXST route.
/// </summary>
public sealed record OutputOwnedSkyrimNpcNakedSkinRegionBinding(
    SkyrimNpcSkinRegion Region,
    FormId AllocatedArmorAddonLocalFormId,
    RaceMenuNpcFormBinding SourceArmorAddon,
    RaceMenuNpcFormBinding? TargetFemaleSkinTextureSet,
    AssetPath FemaleModel)
{
    /// <summary>Already materialized output-local TXST, used by an explicit whole-skin patch.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FormId? AllocatedFemaleTextureSetLocalFormId { get; init; }
}

/// <summary>
/// Creates one NPC-private naked ARMO and ARMA set for WNAM routing. Source
/// records remain untouched; only output-owned links and female model paths
/// are authored in the new plugin.
/// </summary>
public sealed record OutputOwnedSkyrimNpcNakedSkinBinding(
    FormId AllocatedArmorLocalFormId,
    RaceMenuNpcFormBinding SourceSkinArmor,
    ImmutableArray<OutputOwnedSkyrimNpcNakedSkinRegionBinding> Regions);

/// <summary>
/// Complete Skyrim NPC-record appearance payload. Ordered headparts map to
/// PNAM, <see cref="FaceTextureSet"/> maps to FTST, and the native morph/tint
/// patches map to NAM9/NAMA and TINI/TINC/TINV/TIAS respectively.
/// </summary>
public sealed record FullyAuthoredSkyrimNpcAppearanceSource(
    ImmutableArray<SkyrimNpcHeadPartSource> OrderedHeadParts,
    SkyrimNpcHairColorSource HairColor,
    SkyrimNpcFaceTextureSetSource FaceTextureSet,
    float Weight,
    SkyrimFaceMorphPatch FaceMorphs,
    SkyrimFaceTintPatch FaceTints,
    SkyrimQnamRgb Qnam) : NpcCreationAppearanceSource
{
    /// <summary>
    /// Optional output-owned outfit route required to keep exposed torso skin
    /// in the same accepted texture family as the NPC's naked body.
    /// </summary>
    public OutputOwnedSkyrimNpcExposedOutfitSkinBinding?
        ExposedOutfitSkinBinding
    {
        get;
        init;
    }

    /// <summary>
    /// Optional output-owned naked body/hands/feet skin route. When present,
    /// NPC WNAM points at this private skin instead of inheriting race WNAM.
    /// </summary>
    public OutputOwnedSkyrimNpcNakedSkinBinding? NakedSkinBinding
    {
        get;
        init;
    }

    /// <summary>Compatibility constructor for the original all-external appearance contract.</summary>
    public FullyAuthoredSkyrimNpcAppearanceSource(
        ImmutableArray<NpcHeadPartSelection> orderedHeadParts,
        SkyrimNpcHairColorSource hairColor,
        FormReference faceTextureSet,
        float weight,
        SkyrimFaceMorphPatch faceMorphs,
        SkyrimFaceTintPatch faceTints,
        SkyrimQnamRgb qnam)
        : this(
            orderedHeadParts.IsDefault
                ? default
                : orderedHeadParts.Select(item =>
                        (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
                    .ToImmutableArray(),
            hairColor,
            new ExternalSkyrimNpcFaceTextureSet(faceTextureSet),
            weight,
            faceMorphs,
            faceTints,
            qnam)
    {
    }
}

/// <summary>
/// One exact copied plugin admitted as read-only authority while a new NPC is
/// created. The filename must equal <see cref="Plugin"/> and the current bytes
/// must retain <see cref="ExpectedSha256"/> throughout both passes.
/// </summary>
public sealed record NpcCreationPluginAuthority(
    PluginName Plugin,
    WorkspacePath PluginPath,
    Sha256Hash ExpectedSha256);

/// <summary>
/// Hash-bound request for a fresh ordinary Skyrim ESP. Analyze writes only the
/// proposal; Apply may write the output only after that proposal is supplied.
/// </summary>
public sealed record NpcCreationRequest(
    GameEdition Edition,
    WorkspacePath TemplatePlugin,
    Sha256Hash ExpectedTemplateHash,
    FormId TemplateNpcFormId,
    WorkspacePath Proposal,
    WorkspacePath Output,
    NpcCreationIdentity Identity,
    SkyrimNpcCreationTraits Traits,
    SkyrimNpcCreationReferences References,
    NpcCreationAppearanceSource Appearance,
    SkyrimNpcCreationStats Stats,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance = null)
{
    /// <summary>
    /// Optional exact provider paths for referenced or transitively required
    /// plugins that are not necessarily beside the template carrier. Missing
    /// entries retain the legacy copied-Data lookup.
    /// </summary>
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];

    /// <summary>
    /// Output plugin kind written and verified by the same transaction. The
    /// output file name stays '.esp' for both kinds; Espfe only sets the TES4
    /// light flag and requires every owned local FormID to stay at or below
    /// 0xFFF without compaction.
    /// </summary>
    public BlankNpcPluginType PluginType { get; init; } = BlankNpcPluginType.Esp;
}

/// <summary>Persisted, hash-bound plan for one output-owned NPC at a fixed local FormID.</summary>
public sealed record NpcCreationProposal(
    GameEdition Edition,
    WorkspacePath TemplatePlugin,
    Sha256Hash TemplateHash,
    FormId TemplateNpcFormId,
    WorkspacePath Proposal,
    Sha256Hash? ProposalHash,
    WorkspacePath Output,
    PluginName OutputPlugin,
    FormId AllocatedFormId,
    ImmutableArray<PluginName> Masters,
    NpcCreationIdentity Identity,
    SkyrimNpcCreationTraits Traits,
    SkyrimNpcCreationReferences References,
    NpcCreationAppearanceSource Appearance,
    SkyrimNpcCreationStats Stats,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>Plugin authorities persisted into and bound by this proposal.</summary>
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];

    /// <summary>Output plugin kind persisted into and bound by this proposal.</summary>
    public BlankNpcPluginType PluginType { get; init; } = BlankNpcPluginType.Esp;

    public bool IsApplicable => ProposalHash is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

/// <summary>Independent typed and raw read-back evidence for a created plugin.</summary>
public sealed record NpcCreationVerificationResult(
    bool IsValid,
    WorkspacePath Proposal,
    Sha256Hash? ProposalHash,
    WorkspacePath Output,
    Sha256Hash? OutputHash,
    FormId AllocatedFormId,
    ImmutableArray<PluginName> Masters,
    float? HeaderVersion,
    FormId? NextFormId,
    int MajorRecordCount,
    int NpcRecordCount,
    bool IsOrdinaryEsp,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Outcome of an atomic blank-to-new-NPC write attempt.</summary>
public sealed record NpcCreationResult(
    bool Applied,
    WorkspacePath Proposal,
    Sha256Hash? ProposalHash,
    WorkspacePath Output,
    Sha256Hash? OutputHash,
    FormId AllocatedFormId,
    ImmutableArray<PluginName> Masters,
    NpcCreationVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcCreationService
{
    ValueTask<NpcCreationProposal> AnalyzeAsync(
        NpcCreationRequest request,
        CancellationToken cancellationToken);

    ValueTask<NpcCreationResult> ApplyAsync(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<NpcCreationVerificationResult> VerifyAsync(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        CancellationToken cancellationToken);
}
