using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.Infrastructure;

/// <summary>Composition boundary shared by desktop hosts that do not reference rendering directly.</summary>
public static class PreviewAnimationComposition
{
    public static IPreviewAnimationTreeService CreateTreeService(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var list = new PreviewAnimationListService(policy, labRoot);
        return new PreviewAnimationTreeService(list);
    }

    public static IPreviewAnimationPickerService CreatePickerService(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var list = new PreviewAnimationListService(policy, labRoot);
        return new PreviewAnimationPickerService(list);
    }
}
