using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Same-stem RaceMenu export used for an off-engine selection preview. These
/// hashes are staging evidence only; they do not authorize an NPC build or
/// establish runtime appearance.
/// </summary>
public sealed record RaceMenuPresetCompanionExport(
    WorkspacePath Preset,
    Sha256Hash PresetSha256,
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256,
    WorkspacePath FaceTint,
    Sha256Hash FaceTintSha256);

public sealed record RaceMenuPresetPreviewRequest(
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    int Width = 640,
    int Height = 420);

public sealed record RaceMenuPresetPreviewResult(
    bool Rendered,
    RaceMenuPresetCompanionExport? Companion,
    PreviewRenderedImage? Image,
    ImmutableArray<Diagnostic> Diagnostics,
    bool RuntimeAuthority = false);

public interface IRaceMenuPresetPreviewService
{
    ValueTask<RaceMenuPresetPreviewResult> RenderAsync(
        RaceMenuPresetPreviewRequest request,
        CancellationToken cancellationToken);
}
