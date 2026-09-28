using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimFaceGenSidecarAuthority(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash Sha256);

public sealed record SkyrimFaceGenSidecarOverlay(
    PluginName OriginatingPlugin,
    FormId FormId,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> CustomMorphs,
    ImmutableArray<RaceMenuSculptVertex> LegacyHeadSculpt,
    ImmutableArray<BodySidecarSculptPart> SculptParts,
    ImmutableArray<SkyrimNativeFaceTintMaskOverride> TintTextureOverrides,
    ImmutableArray<SkyrimFaceGenSidecarAuthority> SourceAuthorities);

public sealed record SkyrimFaceGenSidecarOverlayLoadRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    ImmutableArray<FaceGenBakeTarget> Targets);

public sealed record SkyrimFaceGenSidecarOverlayLoadResult(
    bool Accepted,
    ImmutableArray<SkyrimFaceGenSidecarOverlay> Overlays,
    ImmutableArray<SkyrimFaceGenSidecarAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceGenSidecarOverlayLoader
{
    ValueTask<SkyrimFaceGenSidecarOverlayLoadResult> LoadAsync(
        SkyrimFaceGenSidecarOverlayLoadRequest request,
        CancellationToken cancellationToken);
}
