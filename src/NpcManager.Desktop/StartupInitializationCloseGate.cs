namespace NpcManager.Desktop;

internal sealed class StartupInitializationCloseGate
{
    private bool initializationInProgress;
    private bool closeRequested;

    public void Begin()
    {
        if (initializationInProgress)
            throw new InvalidOperationException(
                "Startup initialization is already in progress.");

        initializationInProgress = true;
        closeRequested = false;
    }

    public bool TryDeferClose()
    {
        if (!initializationInProgress)
            return false;

        closeRequested = true;
        return true;
    }

    public bool Complete()
    {
        initializationInProgress = false;
        bool replayClose = closeRequested;
        closeRequested = false;
        return replayClose;
    }
}
