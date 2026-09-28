using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Resolves one winning Skyrim TXST from an exact reviewed plugin order and
/// exposes the texture paths that a preset-derived NPC must retain.
/// </summary>
public sealed record SkyrimFaceTextureSetAuthorityRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormReference TextureSet,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder);

public sealed record SkyrimFaceTextureSetAuthority(
    FormReference TextureSet,
    SkyrimFaceRecordProvider Provider,
    SkyrimPrivateHeadTexturePaths Paths,
    bool RuntimeAuthority);

public sealed record SkyrimFaceTextureSetAuthorityResult(
    bool Accepted,
    SkyrimFaceTextureSetAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceTextureSetAuthorityReader
{
    ValueTask<SkyrimFaceTextureSetAuthorityResult> ReadAsync(
        SkyrimFaceTextureSetAuthorityRequest request,
        CancellationToken cancellationToken);
}
