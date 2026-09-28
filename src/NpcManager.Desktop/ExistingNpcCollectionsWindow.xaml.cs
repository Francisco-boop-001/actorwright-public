using System.Windows;

namespace NpcManager.Desktop;

public partial class ExistingNpcCollectionsWindow : Window
{
    public ExistingNpcCollectionsWindow(ExistingNpcCollectionEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    private ExistingNpcCollectionEditorViewModel Editor =>
        (ExistingNpcCollectionEditorViewModel)DataContext;

    private void Accept_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor.TryAccept()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) =>
        DialogResult = false;
}
