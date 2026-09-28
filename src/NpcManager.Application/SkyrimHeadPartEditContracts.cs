using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Loads the exact PNAM baseline and compatible typed catalogs for one NPC from
/// an already reviewed copied Skyrim intake. It never writes an artifact.
/// </summary>
public sealed record SkyrimHeadPartEditLoadRequest(
    ReviewedGameIntake Intake,
    PluginName SourcePlugin,
    FormId TargetFormId);

public sealed record SkyrimHeadPartEditLoadResult(
    bool Accepted,
    WorkspacePath SourcePluginPath,
    Sha256Hash SourcePluginSha256,
    NpcFaceSnapshot? Snapshot,
    ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult> Catalogs,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimHeadPartEditLoadService
{
    ValueTask<SkyrimHeadPartEditLoadResult> LoadAsync(
        SkyrimHeadPartEditLoadRequest request,
        CancellationToken cancellationToken);
}
