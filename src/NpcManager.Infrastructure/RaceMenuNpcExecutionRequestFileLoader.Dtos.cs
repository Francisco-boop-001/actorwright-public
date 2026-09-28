using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcExecutionRequestFileLoader
{
    private sealed record ExecutionRequestDto(
        int SchemaVersion,
        string? Edition,
        PresetBundleDto? PresetBundle,
        ProviderContextDto? ProviderContext,
        StandaloneAssetsDto? StandaloneAssets,
        OutputDto? Output,
        ExistingNpcTargetDto? ExistingNpcTarget,
        bool? ApplyBodySlide,
        bool? AllowInheritedMeshEmbeddedSkinTextureRoute,
        SseFaceGeomCarrierSkeletonAuthority? FaceGeomSkeletonAuthority,
        IdentityDto? Identity,
        TraitsDto? Traits,
        ReferencesDto? References,
        StatsDto? Stats,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        StandaloneAssetsDto? WholeSkinAuthority = null);

    private sealed record PresetBundleDto(
        string? ManifestPath,
        string? ManifestSha256,
        string? PresetPath,
        string? PresetSha256,
        string? FaceGeomPath,
        string? FaceGeomSha256,
        string? FaceTintPath,
        string? FaceTintSha256,
        string? RecordAuthorityPath,
        string? RecordAuthoritySha256,
        string? RuntimeRoutesPath,
        string? RuntimeRoutesSha256);

    private sealed record ProviderContextDto(
        string? ManifestPath,
        string? ManifestSha256,
        string? TemplatePlugin,
        string? TemplateSha256,
        string? TemplateNpcFormId,
        string? FaceGeomCarrier,
        string? FaceGeomSha256,
        string? FaceTintManifest,
        string? FaceTintProviderRoot,
        string? DependencyManifest,
        ProductFixtureBundleDto? ProductFixtureBundle);

    private sealed record ProductFixtureBundleDto(
        string? BundleId,
        string? RegistryManifestSha256);

    private sealed record StandaloneAssetsDto(string? ManifestPath, string? ManifestSha256);
    private sealed record OutputDto(string? Root, string? Plugin, string? PluginType = null);
    private sealed record ExistingNpcTargetDto(
        string? SourcePlugin,
        string? SourcePluginSha256,
        string? TargetFormId);
    private sealed record IdentityDto(string? EditorId, string? Name);
    private sealed record TraitsDto(string? Sex, string? Role, bool Unique, bool Essential,
        bool Protected, bool Respawns, bool AutoCalcStats);
    private sealed record ReferencesDto(string? Race, string? Voice,
        [property: JsonPropertyName("class")] string? ActorClass,
        string? CombatStyle, string? DefaultOutfit);
    private sealed record StatsDto(string? LevelMode, decimal Level, short MagickaOffset,
        short StaminaOffset, short HealthOffset, ushort CalcMinLevel, ushort CalcMaxLevel,
        short SpeedMultiplier, short DispositionBase, short BleedoutOverride,
        ushort BaseHealth, ushort BaseMagicka, ushort BaseStamina, float Height,
        float Weight, ushort FarAwayModelDistance);
}
