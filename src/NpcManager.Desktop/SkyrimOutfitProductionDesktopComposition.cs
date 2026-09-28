using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal static class SkyrimOutfitProductionDesktopComposition
{
    internal static SkyrimOutfitProductionWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var pluginReader = new BethesdaPluginReader();
        var itemCatalog = new BethesdaSkyrimOutfitItemCatalogReader();
        var loader = new SkyrimOutfitProductionLoadService(
            new OutfitChoiceService(pluginReader, policy, labRoot),
            itemCatalog);
        var transaction = new SkyrimOutfitProductionTransactionService(
            new OutfitProposalService(pluginReader, policy, labRoot),
            new BethesdaOutfitBinaryWriteService(policy, labRoot),
            pluginReader,
            policy,
            labRoot);
        return new SkyrimOutfitProductionWorkspaceViewModel(
            loader,
            transaction,
            itemCatalog,
            labRoot);
    }
}
