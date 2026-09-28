using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// The record route used by a newly authored Skyrim NPC for naked skin.
/// InheritedRace resolves the selected race's winning WNAM. ExplicitNpcSkin
/// means the output plugin owns a private ARMO/ARMA skin chain and the NPC WNAM
/// points at that private ARMO.
/// </summary>
public enum SkyrimNpcSkinRouteKind
{
    InheritedRace,
    ExplicitNpcSkin
}

/// <summary>The visible female skin regions that must all have authority.</summary>
public enum SkyrimNpcSkinRegion
{
    Body,
    Hands,
    Feet
}

/// <summary>
/// Four material channels required for one Skyrim skin region. Paths are
/// canonical Data-relative textures/*.dds values.
/// </summary>
public sealed record SkyrimNpcSkinTexturePaths(
    AssetPath Diffuse,
    AssetPath Normal,
    AssetPath Subsurface,
    AssetPath Specular);

public enum SkyrimNpcSkinTextureRouteKind
{
    TextureSet,
    MeshEmbedded
}

/// <summary>
/// One winning ARMA/TXST route and the exact loose/archive winners for all
/// required female texture channels.
/// </summary>
public sealed record SkyrimNpcSkinRegionAuthority(
    SkyrimNpcSkinRegion Region,
    uint SlotMask,
    RaceMenuNpcFormBinding ArmorAddon,
    RaceMenuNpcFormBinding? TextureSet,
    SkyrimNpcSkinTexturePaths? Textures,
    ImmutableArray<SkyrimAssetAuthority> TextureAssets)
{
    public SkyrimNpcSkinTextureRouteKind TextureRoute =>
        TextureSet is null
            ? SkyrimNpcSkinTextureRouteKind.MeshEmbedded
            : SkyrimNpcSkinTextureRouteKind.TextureSet;
}

/// <summary>
/// One selected outfit route whose exposed female torso would otherwise bypass
/// the accepted body TXST. The new-NPC writer uses this source graph to create
/// an output-owned ARMA/ARMO/OTFT chain; source records remain untouched.
/// </summary>
public sealed record SkyrimNpcExposedOutfitSkinBinding(
    RaceMenuNpcFormBinding Outfit,
    RaceMenuNpcFormBinding Armor,
    RaceMenuNpcFormBinding ArmorAddon,
    RaceMenuNpcFormBinding TargetFemaleSkinTextureSet,
    uint ExposedSlotMask);

/// <summary>
/// Closed static authority for a new NPC's whole female skin. RuntimeAuthority
/// remains false until the active providers and rendered actor are proven in
/// Skyrim.
/// </summary>
public sealed record SkyrimNpcWholeSkinAuthority(
    SkyrimNpcSkinRouteKind Route,
    FormReference Race,
    RaceMenuNpcFormBinding RaceBinding,
    RaceMenuNpcFormBinding SkinArmor,
    ImmutableArray<SkyrimNpcSkinRegionAuthority> Regions,
    bool RuntimeAuthority)
{
    /// <summary>
    /// Optional actor-local repair required when the selected outfit exposes a
    /// torso through an ARMA that does not already use the accepted body TXST.
    /// </summary>
    public SkyrimNpcExposedOutfitSkinBinding? ExposedOutfitSkinBinding
    {
        get;
        init;
    }
}

public sealed record SkyrimNpcWholeSkinAuthorityRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormReference Race,
    NpcSex Sex,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder)
{
    /// <summary>
    /// Optional selected OTFT whose exposed-body route must be checked against
    /// the accepted body texture set.
    /// </summary>
    public FormReference? DefaultOutfit { get; init; }

    /// <summary>
    /// Explicit callers may consume naked ARMAs that deliberately omit female
    /// TXST and rely on a hash-bound NIF shader texture set. Legacy new-NPC
    /// flows leave this false and keep the old TXST-only guard.
    /// </summary>
    public bool AllowMeshEmbeddedSkinTextureRoute { get; init; }

    /// <summary>
    /// Optional already-verified schema 7 BodySlide mesh authority used to
    /// extract embedded DDS routes when the source naked ARMA has no female
    /// TXST and points at generated BodySlide output rather than shipped NIFs.
    /// </summary>
    public RaceMenuNpcBodyMeshAuthority? BodyMeshAuthority { get; init; }
}

public sealed record SkyrimNpcWholeSkinAuthorityResult(
    bool Accepted,
    SkyrimNpcWholeSkinAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Reopens the reviewed plugin order and copied Data root to resolve one exact
/// race WNAM -> ARMO -> ARMA -> TXST -> DDS authority graph.
/// </summary>
public interface ISkyrimNpcWholeSkinAuthorityResolver
{
    ValueTask<SkyrimNpcWholeSkinAuthorityResult> ResolveAsync(
        SkyrimNpcWholeSkinAuthorityRequest request,
        CancellationToken cancellationToken);
}
