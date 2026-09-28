using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One already-materialized external headpart-provider NIF. This reader is a
/// dependency parser only: it never emits geometry for the FaceGeom carrier.
/// </summary>
public sealed record ExternalHeadPartProviderNifReadRequest(
    AssetPath SourcePath,
    Sha256Hash ExpectedSourceSha256,
    ImmutableArray<byte> Bytes,
    bool AllowPhysicsBinding = false);

public sealed record ExternalHeadPartProviderNifReadResult(
    bool Accepted,
    ImmutableArray<AssetPath> Dependencies,
    ImmutableArray<AssetPath> ProviderSidecars,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ImmutableArray<string> ShapeNames { get; init; } = [];

    public ImmutableArray<string> PhysicsObjectLocators { get; init; } = [];
}

public interface IExternalHeadPartProviderNifReader
{
    ExternalHeadPartProviderNifReadResult Read(
        ExternalHeadPartProviderNifReadRequest request);
}

/// <summary>
/// Request for the one admitted external provider-sidecar route. The
/// provider identity is carried explicitly so a sidecar cannot be admitted
/// merely because a loose XML file happens to exist at the expected path.
/// </summary>
public sealed record ExternalHeadPartProviderSidecarAuthorityRequest(
    WorkspacePath DataRoot,
    AssetPath SidecarPath,
    FormReference ExternalHeadPartReference,
    PluginName ProviderPlugin,
    Sha256Hash ProviderPluginSha256);

public sealed record ExternalHeadPartProviderSidecarAuthorityResult(
    bool Accepted,
    SkyrimAssetAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IExternalHeadPartProviderSidecarAuthorityResolver
{
    ExternalHeadPartProviderSidecarAuthorityResult Resolve(
        ExternalHeadPartProviderSidecarAuthorityRequest request);
}
