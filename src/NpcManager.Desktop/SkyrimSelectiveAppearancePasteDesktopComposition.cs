using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Desktop;

internal static class SkyrimSelectiveAppearancePasteDesktopComposition
{
    public static SkyrimSelectiveAppearancePasteWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var faceLoader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(
                new BethesdaSkyrimHeadPartChoiceService(authorityLoader)),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        var presets = new PresetService(policy, labRoot);
        var loader = new SkyrimSelectiveAppearancePasteLoadService(
            faceLoader,
            presets,
            new BethesdaSkyrimSelectiveAppearanceNpcStateReader());
        var transaction = new SkyrimSelectiveAppearancePasteTransactionService(
            new NpcAppearanceOverrideService(policy, labRoot),
            presets,
            presets,
            policy,
            labRoot);
        return new SkyrimSelectiveAppearancePasteWorkspaceViewModel(
            loader,
            transaction,
            labRoot);
    }
}
