using System.ComponentModel;
using System.Windows;

namespace NpcManager.Desktop;

public partial class RaceMenuNpcBuildProgressWindow : Window
{
    private readonly Func<Task> workAsync;
    private bool allowClose;

    public RaceMenuNpcBuildProgressWindow(RaceMenuNpcBuildViewModel viewModel)
        : this(viewModel, viewModel.RunAsync)
    {
    }

    internal RaceMenuNpcBuildProgressWindow(object dataContext, Func<Task> workAsync)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        ArgumentNullException.ThrowIfNull(workAsync);
        this.workAsync = workAsync;
        DataContext = dataContext;
        InitializeComponent();
    }

    internal bool WorkStarted { get; private set; }
    internal bool WorkCompleted { get; private set; }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (WorkStarted)
            return;

        WorkStarted = true;
        try
        {
            await workAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (DataContext is RaceMenuNpcBuildViewModel viewModel)
                viewModel.PresentProgressDialogError(exception);
        }
        finally
        {
            WorkCompleted = true;
            allowClose = true;
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            if (DataContext is RaceMenuNpcBuildViewModel viewModel &&
                viewModel.CancelCommand.CanExecute(null))
                viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
