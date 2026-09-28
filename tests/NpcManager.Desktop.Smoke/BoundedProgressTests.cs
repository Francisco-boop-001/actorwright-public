using System.Collections.Concurrent;
using System.Globalization;
using NpcManager.Application;

namespace NpcManager.Desktop.Smoke;

internal static class BoundedProgressTests
{
    private const string OmittedMarker = "... earlier progress entries omitted ...";

    public static void Run()
    {
        var failures = new List<Exception>();
        RunTest(ContextProgressPostsInOrder, failures);
        RunTest(BoundedCollectionRetainsMarkerAndLatestEntry, failures);
        RunTest(OffContextViewModelProgressReturnsBeforeUiDelivery, failures);
        RunTest(ViewModelProgressRetentionIsBounded, failures);
        if (failures.Count > 0)
            throw new AggregateException(failures);
    }

    private static void ContextProgressPostsInOrder()
    {
        var context = new QueuedSynchronizationContext();
        var received = new List<int>();
        var progress = new ContextProgress<int>(context, received.Add);

        Task.Run(() =>
        {
            progress.Report(7);
            progress.Report(8);
            progress.Report(9);
        }).GetAwaiter().GetResult();

        Assert(received.Count == 0,
            "Report synchronously entered the UI callback.");
        context.Drain();
        Assert(received.SequenceEqual([7, 8, 9]),
            "Posted progress was not delivered in report order.");
    }

    private static void BoundedCollectionRetainsMarkerAndLatestEntry()
    {
        AssertThrows<ArgumentOutOfRangeException>(
            () => _ = new BoundedProgressCollection(1, OmittedMarker),
            "A capacity below two was accepted.");

        var entries = new BoundedProgressCollection(512, OmittedMarker);
        for (var index = 0; index < 600; index++)
            entries.Add(index.ToString(CultureInfo.InvariantCulture));

        Assert(entries.Count == 512,
            "The bounded collection grew beyond its exact capacity.");
        Assert(entries[0] == OmittedMarker,
            "The bounded collection hid truncation.");
        Assert(entries[^1] == "599",
            "The bounded collection discarded the latest entry.");
        Assert(entries.Count(item => item == OmittedMarker) == 1,
            "The bounded collection exposed more than one truncation marker.");
    }

    private static void OffContextViewModelProgressReturnsBeforeUiDelivery()
    {
        var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var service = new SingleProgressService();
            using var viewModel = CreateViewModel(service);
            Task run = viewModel.RunAsync();
            service.ReportEntered.Task.WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();

            bool returnedBeforeDrain = service.ReportReturned.Task.Wait(
                TimeSpan.FromMilliseconds(250));
            context.DrainUntil(run, TimeSpan.FromSeconds(10));
            run.GetAwaiter().GetResult();

            Assert(returnedBeforeDrain,
                "Report synchronously entered the captured UI context.");
            Assert(viewModel.ProgressEvents.Count == 1,
                "Posted progress was not delivered after the UI context drained.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void ViewModelProgressRetentionIsBounded()
    {
        using var viewModel = CreateViewModel(new ManyProgressService(600));
        viewModel.RunAsync().GetAwaiter().GetResult();

        Assert(viewModel.ProgressEvents.Count == 512,
            "Progress retention grew beyond 512 entries.");
        Assert(viewModel.ProgressEvents[0] == OmittedMarker,
            "Progress truncation was not exposed by the required marker.");
        Assert(viewModel.ProgressEvents[^1].StartsWith("599", StringComparison.Ordinal),
            "The latest progress entry was not retained.");
    }

    private static NativeFaceGenBatchViewModel CreateViewModel(
        IFaceGenBakeAllService service) => new(service)
        {
            DataRoot = @"K:\Actorwright\artifacts\tests\bounded-progress-input",
            PluginOrderText = "Skyrim.esm",
            OutputRoot = @"K:\Actorwright\artifacts\tests\bounded-progress-output"
        };

    private static void RunTest(Action test, List<Exception> failures)
    {
        try
        {
            test();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"{test.Method.Name}: {exception.Message}", exception));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class SingleProgressService : IFaceGenBakeAllService
    {
        public TaskCompletionSource ReportEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReportReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportEntered.SetResult();
            progress?.Report(Progress(7));
            ReportReturned.SetResult();
            return ValueTask.FromResult(Succeeded());
        }
    }

    private sealed class ManyProgressService(int count) : IFaceGenBakeAllService
    {
        public ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(Progress(index));
            }
            return ValueTask.FromResult(Succeeded());
        }
    }

    private static FaceGenBakeAllProgress Progress(int sequence) => new(
        sequence,
        FaceGenBakeAllProgressPhase.Baking,
        sequence,
        600,
        null,
        $"Progress {sequence}.");

    private static FaceGenBakeAllResult Succeeded() => new(
        FaceGenBakeAllStatus.Succeeded, 0, 0, 0, 0, [], []);

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)>
            callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            callbacks.Enqueue((callback, state));

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (ReferenceEquals(Current, this))
            {
                callback(state);
                return;
            }

            using var completed = new ManualResetEventSlim();
            Exception? failure = null;
            callbacks.Enqueue((queuedState =>
            {
                try
                {
                    callback(queuedState);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    completed.Set();
                }
            }, state));
            if (!completed.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Synchronous UI dispatch did not drain.");
            if (failure is not null)
                throw new InvalidOperationException(
                    "Synchronous UI dispatch failed.", failure);
        }

        public void DrainUntil(Task task, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                Drain();
                if (task.IsCompleted) break;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Queued UI work did not complete.");
                Thread.Sleep(1);
            }
            Drain();
        }

        public void Drain()
        {
            while (callbacks.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }
}
