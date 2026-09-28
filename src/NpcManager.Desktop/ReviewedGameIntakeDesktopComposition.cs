using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal sealed record ReviewedGameIntakeDesktopContext(
    IReviewedGameIntakeService Service,
    WorkspacePath WorkspaceRoot,
    WorkspacePath DataRoot,
    WorkspacePath LoadOrderPath,
    WorkspacePath OutputRoot);

internal static class ReviewedGameIntakeDesktopComposition
{
    internal static ReviewedGameIntakeDesktopContext Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var projectRoot = new WorkspacePath(Path.Combine(
            labRoot.Value, ".actorwright"));
        var dataRoot = new WorkspacePath(Path.Combine(projectRoot.Value,
            "01-source-copies", "m2-fixtures", "sse", "Data"));
        var loadOrder = new WorkspacePath(Path.Combine(projectRoot.Value,
            "01-source-copies", "m2-fixtures", "load-order-sse.json"));
        var outputRoot = new WorkspacePath(Path.Combine(projectRoot.Value,
            "03-builds", "work", "sky-gui-001-desktop-output"));
        var pluginReader = new BethesdaPluginReader();
        var service = new ReviewedGameIntakeService(
            new GameRootPreflightService(policy),
            new PluginLoadOrderService(pluginReader, policy, labRoot),
            new BodySidecarInspectionService(policy, labRoot),
            new GeneratedArtifactScanService(policy, pluginReader, labRoot),
            new BethesdaAssetIndexer());
        return new ReviewedGameIntakeDesktopContext(
            service, projectRoot, dataRoot, loadOrder, outputRoot);
    }
}
