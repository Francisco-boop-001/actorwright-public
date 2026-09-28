using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Resolves a winning TXST whose declared slots cover the direct faceTextures
/// rows stored by a RaceMenu JSlot. PreferredTextureSet binds an explicit
/// headTexture identity when the JSlot carries both representations.
/// </summary>
public sealed record SkyrimFaceTextureSetMatchRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<RaceMenuFaceTexture> FaceTextures,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder,
    FormReference? PreferredTextureSet = null);

public sealed record SkyrimFaceTextureSetMatchResult(
    bool Accepted,
    SkyrimFaceTextureSetAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceTextureSetMatchResolver
{
    ValueTask<SkyrimFaceTextureSetMatchResult> ResolveAsync(
        SkyrimFaceTextureSetMatchRequest request,
        CancellationToken cancellationToken);
}
