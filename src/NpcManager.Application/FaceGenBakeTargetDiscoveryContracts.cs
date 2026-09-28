using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One winning, parseable NPC record admitted to a real loose FaceGen batch.
/// Identity is the originating plugin plus FormID; <see cref="WinningPlugin"/>
/// is the last provider in the explicit copied-Data load order.
/// </summary>
public sealed record FaceGenBakeTarget(
    FormId FormId,
    PluginName OriginatingPlugin,
    PluginName WinningPlugin,
    ImmutableArray<PluginName> OverrideChain,
    string? EditorId,
    string? Name,
    NpcSex Sex,
    FormReference Race,
    ImmutableArray<FormReference> HeadParts,
    float Weight);

/// <summary>
/// Read-only discovery request. The load order is always explicit; the product
/// never infers or mutates a live mod-manager profile.
/// </summary>
public sealed record FaceGenBakeTargetDiscoveryRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    PluginName? WinningPlugin = null);

public sealed record FaceGenBakeTargetDiscoveryResult(
    bool Accepted,
    ImmutableArray<FaceGenBakeTarget> Targets,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenBakeTargetDiscoveryService
{
    ValueTask<FaceGenBakeTargetDiscoveryResult> DiscoverAsync(
        FaceGenBakeTargetDiscoveryRequest request,
        CancellationToken cancellationToken);
}
