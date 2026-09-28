using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Resolves the canonical FaceGeom associated with one exact NPC selection
/// from a reviewed Skyrim provider closure. Archive materialization is
/// preview/authoring staging only; the winning provider remains authoritative.
/// </summary>
public sealed record FaceGeomHairRegionsSelectedSourceRequest(
    ReviewedGameIntake Intake,
    SkyrimMainWorkspaceIdentity Identity,
    WorkspacePath ArchiveStagingRoot);

public sealed record FaceGeomHairRegionsSelectedSource(
    FaceGeomHairRegionsFile Source,
    AssetPath AssetPath,
    AssetProviderKind ProviderKind,
    string Provider,
    bool MaterializedFromArchive,
    FaceGeomHairRegionPluginColorContext? PluginColorContext);

public sealed record FaceGeomHairRegionsSelectedSourceResult(
    bool Resolved,
    FaceGeomHairRegionsSelectedSource? Source,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGeomHairRegionsSelectedSourceResolver
{
    ValueTask<FaceGeomHairRegionsSelectedSourceResult> ResolveAsync(
        FaceGeomHairRegionsSelectedSourceRequest request,
        CancellationToken cancellationToken);
}
