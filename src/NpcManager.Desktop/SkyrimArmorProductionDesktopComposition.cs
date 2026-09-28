using System.IO;
using System.Windows;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

internal static class SkyrimArmorProductionDesktopComposition
{
    internal static SkyrimArmorProductionWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var pluginReader = new BethesdaPluginReader();
        var armorAddonTransaction =
            new SkyrimArmorAddonProductionTransactionService(
                new ArmorAddonProposalService(pluginReader, policy, labRoot),
                new BethesdaArmorAddonBinaryWriteService(policy, labRoot),
                new BethesdaSkyrimArmorAddonProductionOutputReader(),
                policy,
                labRoot);
        var transaction = new SkyrimArmorProductionTransactionService(
            new ArmorProposalService(pluginReader, policy, labRoot),
            new BethesdaArmorBinaryWriteService(policy, labRoot),
            new BethesdaSkyrimArmorProductionOutputReader(),
            policy,
            labRoot,
            armorAddonTransaction);
        var meshPreviewRenderer = new BlenderPreviewImageRenderer(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external",
                "blender-4.5.1-windows-x64", "blender-4.5.1-windows-x64",
                "blender.exe")),
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external",
                "blender-4.5.1-pynifly-profile")),
            "render_preview_scene",
            policy,
            labRoot,
            new Sha256Hash(
                "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"));
        var meshPreview = new SkyrimMeshPreviewService(
            new SkyrimAssetContentResolver(policy, labRoot),
            meshPreviewRenderer,
            policy,
            labRoot,
            new WorkspacePath(Path.Combine(labRoot.Value, ".actorwright", "work",
                "skyrim-mesh-picker-preview-cache")));
        SkyrimMeshPickerSelection OpenMeshPicker(
            SkyrimMeshTargetField field,
            SkyrimMeshPickerCatalogResult catalog)
        {
            var viewModel = new SkyrimMeshPickerViewModel(catalog, meshPreview);
            var dialog = new SkyrimMeshPickerWindow(viewModel);
            Window? owner = System.Windows.Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive);
            if (owner is not null && owner != dialog) dialog.Owner = owner;
            bool? accepted = dialog.ShowDialog();
            return accepted == true && viewModel.AcceptedSelection is { } selection
                ? selection
                : SkyrimMeshPickerRules.Cancel();
        }
        return new SkyrimArmorProductionWorkspaceViewModel(
            new BethesdaSkyrimArmorProductionReader(),
            transaction,
            labRoot,
            new FormChoiceService(pluginReader, policy, labRoot),
            new AssetChoiceService(
                pluginReader,
                new BethesdaAssetIndexer(),
                policy,
                labRoot),
            OpenMeshPicker);
    }
}
