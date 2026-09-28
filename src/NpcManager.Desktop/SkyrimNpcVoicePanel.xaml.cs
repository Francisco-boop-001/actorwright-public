using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NpcManager.Desktop;

public partial class SkyrimNpcVoicePanel : UserControl
{
    private bool serverSetupLoadStarted;

    public SkyrimNpcVoicePanel()
    {
        InitializeComponent();
    }

    private async void VoicePanelLoaded(object sender, RoutedEventArgs e) =>
        await HandleLoadedAsync();

    internal async Task HandleLoadedAsync()
    {
        if (serverSetupLoadStarted ||
            DataContext is not SkyrimNpcVoiceViewModel viewModel)
            return;
        serverSetupLoadStarted = true;
        await viewModel.InitializeServerSetupAsync();
    }

    private void VoiceSampleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleFile(e.Data, out string? path) &&
                    path is not null && IsWav(path)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void VoiceSampleDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await HandleDropAsync(e.Data);
    }

    internal async Task HandleDropAsync(IDataObject data)
    {
        if (DataContext is not SkyrimNpcVoiceViewModel viewModel)
            return;
        if (!TryGetSingleFile(data, out string? path) || path is null)
        {
            viewModel.ReportValidation(
                "voice-sample-invalid: Drop exactly one .wav file.");
            return;
        }
        await viewModel.ImportSampleAsync(path);
    }

    private async void BrowseVoiceSampleClick(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not SkyrimNpcVoiceViewModel viewModel)
            return;
        OpenFileDialog dialog = CreateBrowseDialog();
        if (dialog.ShowDialog() == true)
            await viewModel.ImportSampleAsync(dialog.FileName);
    }

    internal static OpenFileDialog CreateBrowseDialog() => new()
    {
        Title = "Choose one WAV voice sample",
        Filter = "WAV audio (*.wav)|*.wav",
        CheckFileExists = true,
        Multiselect = false
    };

    private static bool TryGetSingleFile(
        IDataObject data,
        out string? path)
    {
        path = data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
            ? files[0]
            : null;
        return path is not null;
    }

    private static bool IsWav(string path) =>
        string.Equals(
            Path.GetExtension(path),
            ".wav",
            StringComparison.OrdinalIgnoreCase);
}
