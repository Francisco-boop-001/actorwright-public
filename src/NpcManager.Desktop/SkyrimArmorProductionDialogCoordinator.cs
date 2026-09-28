using System.Windows;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal static class SkyrimArmorProductionDialogCoordinator
{
    internal static bool TryAuthor(
        Window? owner,
        SkyrimArmorProductionWorkspaceViewModel workspace)
    {
        var dialog = new SkyrimArmorEditorWindow();
        SkyrimArmorEditorViewModel editor = workspace.CreateEditor(
            (document, selectedIndex) => TryChooseArmorAddon(
                dialog, document, selectedIndex, workspace));
        dialog.DataContext = editor;
        if (owner is not null) dialog.Owner = owner;
        if (dialog.ShowDialog() == true)
            return workspace.ApplyEditor(editor);
        workspace.ReportEditorCancelled();
        return false;
    }

    internal static SkyrimOutfitEditorItem? TryAuthorOutfitChild(
        Window? owner,
        SkyrimOutfitEditorItemKind kind,
        SkyrimOutfitEditorItem? basis,
        SkyrimArmorProductionWorkspaceViewModel workspace)
    {
        if (kind != SkyrimOutfitEditorItemKind.Armor) return null;
        if (basis is null)
            throw new InvalidOperationException(
                "Select one reviewed ARMO explicitly before opening New ARMO.");
        var dialog = new SkyrimArmorEditorWindow();
        SkyrimArmorEditorViewModel editor =
            workspace.CreateOutfitChildEditor(
                basis,
                (document, selectedIndex) => TryChooseArmorAddon(
                    dialog, document, selectedIndex, workspace));
        dialog.DataContext = editor;
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true
            ? workspace.CreateOutfitChildItem(editor, basis)
            : null;
    }

    private static SkyrimArmorAddonReferenceRow? TryChooseArmorAddon(
        Window owner,
        SkyrimArmorEditorDocument document,
        int? selectedIndex,
        SkyrimArmorProductionWorkspaceViewModel workspace)
    {
        SkyrimArmorAddonReferenceEditorWindow? dialog = null;
        SkyrimArmorAddonReferenceEditorViewModel editor =
            workspace.CreateArmorAddonReferenceEditor(
                document,
                selectedIndex,
                current => TryDeepEditArmorAddon(
                    dialog ?? owner,
                    document.Race,
                    current,
                    workspace));
        dialog = new SkyrimArmorAddonReferenceEditorWindow(editor)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true && editor.IsAccepted
            ? editor.AcceptedRow
            : null;
    }

    private static SkyrimArmorAddonDeepEditResult? TryDeepEditArmorAddon(
        Window owner,
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow current,
        SkyrimArmorProductionWorkspaceViewModel workspace)
    {
        SkyrimArmorAddonEditorViewModel editor =
            workspace.CreateArmorAddonEditor(owningArmorRace, current);
        var dialog = new SkyrimArmorAddonEditorWindow(editor)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true && editor.IsAccepted &&
               editor.AcceptedRow is { } row
            ? new SkyrimArmorAddonDeepEditResult(true, row)
            : new SkyrimArmorAddonDeepEditResult(false, null);
    }
}
