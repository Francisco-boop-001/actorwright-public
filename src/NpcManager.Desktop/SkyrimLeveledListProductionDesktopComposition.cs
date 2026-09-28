using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal static class SkyrimLeveledListProductionDesktopComposition
{
    internal static SkyrimLeveledListProductionWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var catalog = new BethesdaSkyrimOutfitItemCatalogReader();
        var transaction = new SkyrimLeveledListProductionTransactionService(
            new BethesdaSkyrimLeveledListProductionBinaryWriter(policy, labRoot),
            new BethesdaSkyrimLeveledListProductionOutputReader(),
            policy,
            labRoot);
        return new SkyrimLeveledListProductionWorkspaceViewModel(
            new SkyrimLeveledListProductionLoadService(catalog),
            transaction,
            labRoot);
    }
}
