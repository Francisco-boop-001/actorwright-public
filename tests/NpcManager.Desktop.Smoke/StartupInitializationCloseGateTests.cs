namespace NpcManager.Desktop.Smoke;

internal static class StartupInitializationCloseGateTests
{
    public static void Run()
    {
        var gate = new StartupInitializationCloseGate();

        Assert(!gate.TryDeferClose(),
            "The close gate deferred a request before startup initialization began.");

        gate.Begin();
        Assert(gate.TryDeferClose(),
            "The close gate did not defer a request during startup initialization.");
        Assert(gate.TryDeferClose(),
            "The close gate did not preserve a repeated request during startup initialization.");
        Assert(gate.Complete(),
            "The close gate did not replay the deferred request when initialization completed.");
        Assert(!gate.Complete(),
            "The close gate replayed the same deferred request more than once.");
        Assert(!gate.TryDeferClose(),
            "The close gate remained active after initialization completed.");

        Console.WriteLine(
            "PASS startup close gate: first-render close is deferred once and replayed after initialization.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
