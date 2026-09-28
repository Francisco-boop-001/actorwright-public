using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class ProgramDispatchObservabilityTests
{
    private const string ProviderName = "Actorwright-Observability";

    public static async Task RunAsync()
    {
        await DispatchEventsPreserveOutputExitAndJournalBehavior();
        await UnknownCommandsAreClosedAndObserverFailuresAreIsolated();
        await CancellationAndThrownOutputPreserveFailureSemantics();
    }

    private static async Task DispatchEventsPreserveOutputExitAndJournalBehavior()
    {
        var cases = new[]
        {
            new DispatchCase(
                ["version", "--json"], 1, "version", 0, 0),
            new DispatchCase(
                ["version", "--protocol", "1", "--json"], 1,
                "version", 0, 0),
            new DispatchCase(
                ["version", "--protocol", "2", "--json", "--correlation",
                    "correlation-secret-sentinel"],
                2, "version", 0, 1),
            new DispatchCase(
                ["version", "--protocol", "unsupported-protocol-sentinel",
                    "--json"],
                3, "version", 2, 1)
        };

        var withoutListener = new List<CliResult>();
        foreach (DispatchCase testCase in cases)
            withoutListener.Add(await InvokeAsync(testCase.Arguments));

        using var listener = new CapturingListener();
        var withListener = new List<CliResult>();
        foreach (DispatchCase testCase in cases)
            withListener.Add(await InvokeAsync(testCase.Arguments));

        for (var index = 0; index < cases.Length; index++)
        {
            CliResult expected = withoutListener[index];
            CliResult actual = withListener[index];
            DispatchCase testCase = cases[index];
            Assert(actual.ExitCode == expected.ExitCode &&
                   actual.StandardOutput == expected.StandardOutput &&
                   actual.StandardError == expected.StandardError,
                "enabling the observability listener changed CLI output or exit");
            Assert(actual.JournalAppendCount == expected.JournalAppendCount &&
                   actual.JournalAppendCount == testCase.ExpectedJournalAppendCount,
                "dispatch observability changed local journal writes");
        }

        CapturedEvent[] events = listener.Events.ToArray();
        Assert(events.Length == cases.Length * 2,
            $"the opt-in listener received {events.Length} events; " +
            $"provider matched={listener.ProviderMatched}, " +
            $"enabled={listener.ProviderEnabled}; diagnostics=" +
            string.Join(";", events.Select(captured =>
                $"id={captured.EventId}, payload=" +
                string.Join(",", captured.Payload.Select(value =>
                    value?.ToString() ?? "null")))));
        var invocationIds = new HashSet<Guid>();
        for (var index = 0; index < cases.Length; index++)
        {
            DispatchCase testCase = cases[index];
            CapturedEvent start = events[index * 2];
            CapturedEvent stop = events[index * 2 + 1];
            Assert(start.EventId == 1 && stop.EventId == 2,
                "dispatch event ids were not start followed by stop");
            AssertPayloadNames(start, "invocationId", "routeId", "commandName");
            AssertPayloadNames(stop, "invocationId", "routeId", "commandName",
                "exitCode", "elapsedMilliseconds");
            AssertPayloadTypes(start,
                ("invocationId", typeof(Guid)),
                ("routeId", typeof(int)),
                ("commandName", typeof(string)));
            AssertPayloadTypes(stop,
                ("invocationId", typeof(Guid)),
                ("routeId", typeof(int)),
                ("commandName", typeof(string)),
                ("exitCode", typeof(int)),
                ("elapsedMilliseconds", typeof(long)));
            Assert(Payload(start, "routeId")?.ToString() ==
                       testCase.ExpectedRouteId.ToString() &&
                   Payload(start, "commandName") as string ==
                       testCase.ExpectedCommandName,
                "dispatch start did not expose the closed route and command");
            Assert(Payload(stop, "exitCode")?.ToString() ==
                       testCase.ExpectedExitCode.ToString(),
                "dispatch stop did not preserve the command exit code");
            Guid invocationId = (Guid)Payload(start, "invocationId")!;
            Assert(invocationId != Guid.Empty &&
                   invocationIds.Add(invocationId) &&
                   invocationId == (Guid)Payload(stop, "invocationId")!,
                "dispatch start and stop did not share their invocation id");
        }

        Assert(!EventsContain(events, "correlation-secret-sentinel") &&
               !EventsContain(events, "unsupported-protocol-sentinel"),
            "dispatch events exposed raw correlation or protocol input");
    }

    private static async Task UnknownCommandsAreClosedAndObserverFailuresAreIsolated()
    {
        const string secretCommand = "unknown-command-secret-sentinel";
        var baseline = await InvokeAsync(
            [secretCommand, "--protocol", "2", "--json"]);
        var legacyBaseline = await InvokeAsync(["version", "--json"]);

        using (var listener = new CapturingListener())
        {
            var observed = await InvokeAsync(
                [secretCommand, "--protocol", "2", "--json"]);
            Assert(observed.ExitCode == baseline.ExitCode &&
                   observed.StandardOutput == baseline.StandardOutput &&
                   observed.StandardError == baseline.StandardError,
                "unknown-command event capture changed the protocol response");
            Assert(listener.Events.Length == 2 &&
                   Payload(listener.Events.First(), "commandName") as string ==
                       "unknown" &&
                   !EventsContain(listener.Events.ToArray(), secretCommand),
                "an unrecognized command escaped the fixed telemetry sentinel");
            foreach (CapturedEvent captured in listener.Events)
            {
                if (captured.EventId == 1)
                    AssertPayloadNames(
                        captured, "invocationId", "routeId", "commandName");
                else
                    AssertPayloadNames(
                        captured, "invocationId", "routeId", "commandName",
                        "exitCode", "elapsedMilliseconds");
            }
        }

        using var throwingListener = new ThrowingListener();
        var withThrowingListener = await InvokeAsync(["version", "--json"]);
        Assert(withThrowingListener.ExitCode == legacyBaseline.ExitCode &&
               withThrowingListener.StandardOutput ==
                   legacyBaseline.StandardOutput &&
               withThrowingListener.StandardError ==
                   legacyBaseline.StandardError,
            "an observer failure changed the command result");
        Assert(throwingListener.Attempts > 0,
            "the throwing listener was not reached by dispatch events");
    }

    private static async Task CancellationAndThrownOutputPreserveFailureSemantics()
    {
        string[] cancellationArguments =
            ["capabilities", "--protocol", "2", "--json"];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CliResult cancellationBaseline = await InvokeAsync(
            cancellationArguments,
            cancelled.Token);

        using (var listener = new CapturingListener())
        {
            CliResult cancellationObserved = await InvokeAsync(
                cancellationArguments,
                cancelled.Token);
            Assert(cancellationObserved.ExitCode == 5 &&
                   cancellationObserved.ExitCode == cancellationBaseline.ExitCode &&
                   cancellationObserved.StandardOutput ==
                       cancellationBaseline.StandardOutput &&
                   cancellationObserved.StandardError ==
                       cancellationBaseline.StandardError &&
                   cancellationObserved.JournalAppendCount ==
                       cancellationBaseline.JournalAppendCount,
                "dispatch observation changed the cancelled protocol outcome");
            CapturedEvent[] events = listener.Events.ToArray();
            Assert(events.Length == 2 && events[0].EventId == 1 &&
                   events[1].EventId == 2 &&
                   Payload(events[1], "exitCode")?.ToString() == "5",
                "the returned cancellation outcome was not observed");
        }

        var beforeListenerException = new InvalidOperationException(
            "original writer failure");
        Exception? beforeListenerThrown = await InvokeWithThrowingOutputAsync(
            beforeListenerException);
        Assert(ReferenceEquals(beforeListenerException, beforeListenerThrown),
            "the legacy CLI changed the original writer exception");

        using var faultListener = new CapturingListener();
        var originalException = new InvalidOperationException(
            "original writer failure");
        Exception? observedThrown = await InvokeWithThrowingOutputAsync(
            originalException);
        Assert(ReferenceEquals(originalException, observedThrown),
            "dispatch instrumentation replaced the original exception");
        CapturedEvent[] faultEvents = faultListener.Events.ToArray();
        Assert(faultEvents.Length == 2 && faultEvents[0].EventId == 1 &&
               faultEvents[1].EventId == 3 &&
               Payload(faultEvents[1], "faultKindId")?.ToString() == "2",
            "a thrown dispatch fault was not reported with its closed kind");
        Assert(Payload(faultEvents[0], "invocationId") is Guid startedId &&
               startedId != Guid.Empty &&
               startedId == (Guid)Payload(faultEvents[1], "invocationId")!,
            "dispatch fault did not share its start invocation id");
        AssertPayloadNames(
            faultEvents[1], "invocationId", "routeId", "commandName",
            "faultKindId");
        AssertPayloadTypes(faultEvents[1],
            ("invocationId", typeof(Guid)),
            ("routeId", typeof(int)),
            ("commandName", typeof(string)),
            ("faultKindId", typeof(int)));

        var originalCancellation = new OperationCanceledException(
            "original writer cancellation");
        using var cancellationListener = new CapturingListener();
        Exception? observedCancellation = await InvokeWithThrowingOutputAsync(
            originalCancellation);
        Assert(ReferenceEquals(originalCancellation, observedCancellation),
            "dispatch instrumentation replaced the original cancellation");
        CapturedEvent[] cancellationEvents = cancellationListener.Events.ToArray();
        Assert(cancellationEvents.Length == 2 &&
               cancellationEvents[0].EventId == 1 &&
               cancellationEvents[1].EventId == 3 &&
               Payload(cancellationEvents[1], "faultKindId")?.ToString() == "1" &&
               Equals(Payload(cancellationEvents[0], "invocationId"),
                      Payload(cancellationEvents[1], "invocationId")),
            "an escaping cancellation was not observed with its closed kind");
    }

    private static async Task<CliResult> InvokeAsync(
        string[] arguments,
        CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var journal = new InMemoryJournal();
        CommandExitCode exitCode = await NpcManager.Cli.Program.RunAsync(
            arguments,
            output,
            error,
            _ => journal,
            cancellationToken);
        return new CliResult(
            (int)exitCode,
            output.ToString(),
            error.ToString(),
            journal.AppendCount);
    }

    private static async Task<Exception?> InvokeWithThrowingOutputAsync(
        Exception exception)
    {
        using var error = new StringWriter();
        var journal = new InMemoryJournal();
        try
        {
            await NpcManager.Cli.Program.RunAsync(
                ["version", "--json"],
                new ThrowingTextWriter(exception),
                error,
                _ => journal,
                CancellationToken.None);
            return null;
        }
        catch (Exception actual)
        {
            return actual;
        }
    }

    private static object? Payload(CapturedEvent captured, string name)
    {
        int index = Array.IndexOf(captured.PayloadNames, name);
        return index < 0 ? null : captured.Payload[index];
    }

    private static bool EventsContain(
        IEnumerable<CapturedEvent> events,
        string value) => events.Any(captured =>
        captured.Payload.Any(item =>
            item?.ToString()?.Contains(value, StringComparison.Ordinal) == true));

    private static void AssertPayloadNames(
        CapturedEvent captured,
        params string[] expectedNames)
    {
        string[] actual = captured.PayloadNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] expected = expectedNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert(actual.SequenceEqual(expected, StringComparer.Ordinal),
            "dispatch event payload included fields outside its closed schema");
    }

    private static void AssertPayloadTypes(
        CapturedEvent captured,
        params (string Name, Type Type)[] expectedTypes)
    {
        foreach ((string name, Type type) in expectedTypes)
            Assert(Payload(captured, name)?.GetType() == type,
                $"dispatch payload {name} did not use {type.Name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record DispatchCase(
        string[] Arguments,
        int ExpectedRouteId,
        string ExpectedCommandName,
        int ExpectedExitCode,
        int ExpectedJournalAppendCount);

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        int JournalAppendCount);

    private sealed record CapturedEvent(
        int EventId,
        string[] PayloadNames,
        object?[] Payload);

    private sealed class ThrowingTextWriter(Exception exception) : TextWriter
    {
        public override System.Text.Encoding Encoding =>
            System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => throw exception;
    }

    private sealed class CapturingListener : EventListener
    {
        private readonly ConcurrentQueue<CapturedEvent> events = new();

        public CapturedEvent[] Events => events.ToArray();
        public bool ProviderMatched { get; private set; }
        public bool ProviderEnabled { get; private set; }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, ProviderName,
                    StringComparison.Ordinal))
            {
                ProviderMatched = true;
                EnableEvents(
                    eventSource,
                    EventLevel.Informational,
                    EventKeywords.All);
                ProviderEnabled = eventSource.IsEnabled(
                    EventLevel.Informational,
                    EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            events.Enqueue(new CapturedEvent(
                eventData.EventId,
                eventData.PayloadNames?.ToArray() ?? [],
                eventData.Payload?.ToArray() ?? []));
        }
    }

    private sealed class ThrowingListener : EventListener
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, ProviderName,
                    StringComparison.Ordinal))
                EnableEvents(
                    eventSource,
                    EventLevel.Informational,
                    EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("observer failure sentinel");
        }
    }

    private sealed class InMemoryJournal : ILocalOperationJournal
    {
        public int AppendCount { get; private set; }

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            AppendCount++;
            return ValueTask.FromResult(new OperationJournalAppendResult(
                true, null, null));
        }
    }
}
