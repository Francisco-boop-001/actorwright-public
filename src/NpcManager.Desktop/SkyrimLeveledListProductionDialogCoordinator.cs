using System.Windows;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal static class SkyrimLeveledListProductionDialogCoordinator
{
    internal static bool TryAuthor(
        Window? owner,
        SkyrimLeveledListProductionWorkspaceViewModel workspace)
    {
        SkyrimLeveledListEditorViewModel header = workspace.CreateHeaderEditor();
        var headerWindow = new SkyrimLeveledListEditorWindow(header) { Owner = owner };
        if (headerWindow.ShowDialog() != true)
        {
            workspace.ReportEditorCancelled();
            return false;
        }

        SkyrimLeveledEntryEditorViewModel entry = workspace.CreateEntryEditor();
        var entryWindow = new SkyrimLeveledEntryEditorWindow(entry) { Owner = owner };
        if (entryWindow.ShowDialog() != true)
        {
            workspace.ReportEditorCancelled();
            return false;
        }
        return workspace.ApplyEditors(header, entry);
    }

    internal static bool TryAddEntry(
        Window? owner,
        SkyrimLeveledListProductionWorkspaceViewModel workspace)
    {
        SkyrimLeveledEntryEditorViewModel entry =
            workspace.CreateAdditionalEntryEditor();
        var entryWindow = new SkyrimLeveledEntryEditorWindow(entry) { Owner = owner };
        if (entryWindow.ShowDialog() == true)
            return workspace.ApplyAddedEntry(entry);
        workspace.ReportEditorCancelled();
        return false;
    }

    internal static bool TryEditEntry(
        Window? owner,
        SkyrimLeveledListProductionWorkspaceViewModel workspace)
    {
        SkyrimLeveledEntryEditorViewModel entry =
            workspace.CreateSelectedEntryEditor();
        var entryWindow = new SkyrimLeveledEntryEditorWindow(entry) { Owner = owner };
        if (entryWindow.ShowDialog() == true)
            return workspace.ApplyEditedEntry(entry);
        workspace.ReportEditorCancelled();
        return false;
    }

    internal static SkyrimOutfitEditorItem? TryAuthorOutfitChild(
        Window? owner,
        SkyrimOutfitEditorItemKind kind,
        SkyrimOutfitEditorItem? existing,
        PluginName provider,
        FormId provisionalFormId,
        IReadOnlyCollection<EditorId> existingEditorIds,
        SkyrimOutfitEditorItem entryCandidate)
    {
        if (kind != SkyrimOutfitEditorItemKind.LeveledList || existing is not null)
            return null;

        var header = new SkyrimLeveledListEditorViewModel([.. existingEditorIds]);
        var headerWindow = new SkyrimLeveledListEditorWindow(header) { Owner = owner };
        if (headerWindow.ShowDialog() != true || header.AcceptedDocument is null)
            return null;

        var entry = new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Add,
            new SkyrimLeveledEntryCandidate(
                entryCandidate.Reference,
                entryCandidate.Kind,
                entryCandidate.DisplayName));
        var entryWindow = new SkyrimLeveledEntryEditorWindow(entry) { Owner = owner };
        if (entryWindow.ShowDialog() != true || entry.AcceptedEntry is null)
            return null;

        FormReference target = new(provider, provisionalFormId);
        SkyrimLeveledListDocumentEditResult document = SkyrimLeveledEntryEditorRules.Add(
            header.AcceptedDocument,
            target,
            entryCandidate.Kind,
            entry.AcceptedEntry,
            null);
        if (!document.Accepted || document.Document is null) return null;

        return new SkyrimOutfitEditorItem(
            target,
            SkyrimOutfitEditorItemKind.LeveledList,
            document.Document.EditorId.Value,
            0,
            null,
            [],
            document.Document);
    }
}
