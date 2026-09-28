using System.Windows;

namespace NpcManager.Desktop;

public partial class ExistingNpcIdentityWindow : Window
{
    public ExistingNpcIdentityWindow(ExistingNpcIdentityEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private ExistingNpcIdentityEditorViewModel ViewModel =>
        (ExistingNpcIdentityEditorViewModel)DataContext;

    private void Accept_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.TryAccept()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) =>
        DialogResult = false;

    private void ChooseRace_Click(object sender, RoutedEventArgs eventArgs) =>
        Choose(ExistingNpcArchetypeField.Race);

    private void ChooseVoice_Click(object sender, RoutedEventArgs eventArgs) =>
        Choose(ExistingNpcArchetypeField.Voice);

    private void ChooseClass_Click(object sender, RoutedEventArgs eventArgs) =>
        Choose(ExistingNpcArchetypeField.Class);

    private void ChooseCombatStyle_Click(object sender, RoutedEventArgs eventArgs) =>
        Choose(ExistingNpcArchetypeField.CombatStyle);

    private void Choose(ExistingNpcArchetypeField field)
    {
        var picker = ViewModel.CreatePicker(field);
        var dialog = new TypedFormIdPickerWindow(picker) { Owner = this };
        if (dialog.ShowDialog() == true) ViewModel.ApplyPicker(field, picker);
    }
}
