using System.Windows;
using System.Windows.Controls;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimOutfitProductionPanel : UserControl
{
    public SkyrimOutfitProductionPanel()
    {
        InitializeComponent();
    }

    private void OpenOutfitWorkbench_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SkyrimOutfitProductionWorkspaceViewModel workspace ||
            !workspace.CanOpenEditor) return;
        Window? owner = Window.GetWindow(this);
        WorkspaceShellViewModel? shell = owner?.DataContext as WorkspaceShellViewModel;
        SkyrimOutfitEditorWindow? dialog = null;
        SkyrimOutfitEditorViewModel editor = workspace.CreateEditor(
            (kind, existing) =>
            {
                if (kind == SkyrimOutfitEditorItemKind.Armor)
                {
                    if (shell is null)
                        throw new InvalidOperationException(
                            "The production Armor task is unavailable.");
                    return SkyrimArmorProductionDialogCoordinator
                        .TryAuthorOutfitChild(
                            dialog,
                            kind,
                            existing,
                            shell.ArmorProduction);
                }
                if (!workspace.TryGetNewLeveledListChildContext(out
                        SkyrimLeveledListOutfitChildContext? context) || context is null)
                    return null;
                return SkyrimLeveledListProductionDialogCoordinator.TryAuthorOutfitChild(
                    dialog,
                    kind,
                    existing,
                    context.Provider,
                    context.ProvisionalFormId,
                    context.ExistingEditorIds,
                    context.EntryCandidate);
            });
        dialog = new SkyrimOutfitEditorWindow(editor)
        {
            Owner = owner
        };
        if (dialog.ShowDialog() == true)
            workspace.ApplyEditor(editor);
        else
            workspace.ReportEditorCancelled();
    }
}
