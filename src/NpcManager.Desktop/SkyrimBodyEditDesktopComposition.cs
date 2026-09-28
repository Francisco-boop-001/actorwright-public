using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal static class SkyrimBodyEditDesktopComposition
{
    public static SkyrimBodyEditWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var loader = new SkyrimBodyEditLoadService(
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimBodyEditSourceReader());
        return new SkyrimBodyEditWorkspaceViewModel(
            loader,
            new NpcOverrideService(policy, labRoot),
            labRoot);
    }
}
