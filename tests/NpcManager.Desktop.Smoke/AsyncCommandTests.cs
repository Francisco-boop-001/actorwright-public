using System.Windows.Input;
using NpcManager.Desktop;

namespace NpcManager.Desktop.Smoke;

internal static class AsyncCommandTests
{
    public static void Run()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        var releaseExecution = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canExecute = false;
        var predicateCalls = 0;
        var executions = 0;
        var started = false;
        var notified = false;
        var senderMatches = false;
        EventArgs? notificationArgs = null;
        var command = new AsyncCommand(
            async () =>
            {
                started = true;
                await releaseExecution.Task;
                executions++;
                executionCompleted.SetResult();
            },
            () =>
            {
                predicateCalls++;
                return canExecute;
            });

        command.CanExecuteChanged += (sender, args) =>
        {
            notified = true;
            senderMatches = ReferenceEquals(sender, command);
            notificationArgs = args;
        };

        Assert(!command.CanExecute(null) && predicateCalls == 1,
            "CanExecute did not evaluate the supplied predicate.");
        canExecute = true;
        Assert(command.CanExecute(new object()) && predicateCalls == 2,
            "CanExecute did not reevaluate the supplied predicate.");

        command.RaiseCanExecuteChanged();
        Assert(notified && senderMatches &&
               ReferenceEquals(notificationArgs, EventArgs.Empty),
            "CanExecuteChanged did not preserve its sender and event args.");

        canExecute = false;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            command.Execute(new object());
            Assert(predicateCalls == 2 && started && executions == 0,
                "Execute checked the predicate or did not begin async forwarding.");

            releaseExecution.SetResult();
            executionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            Assert(executions == 1,
                "The asynchronous delegate did not complete.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
