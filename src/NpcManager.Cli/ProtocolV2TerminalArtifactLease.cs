namespace NpcManager.Cli;

internal sealed class ProtocolV2TerminalArtifactLease : IDisposable
{
    private List<IDisposable>? children = [];

    internal T Own<T>(T child)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(child);
        List<IDisposable>? owned = children;
        if (owned is null)
        {
            DisposeSafely(child);
            throw new ObjectDisposedException(
                nameof(ProtocolV2TerminalArtifactLease));
        }

        owned.Add(child);
        return child;
    }

    public void Dispose()
    {
        List<IDisposable>? owned = Interlocked.Exchange(
            ref children,
            null);
        if (owned is null)
            return;

        for (int index = owned.Count - 1; index >= 0; index--)
            DisposeSafely(owned[index]);
    }

    private static void DisposeSafely(IDisposable child)
    {
        try
        {
            child.Dispose();
        }
        catch
        {
            // Terminal release is best effort and must never mask output errors.
        }
    }
}

internal sealed class ProtocolV2TerminalArtifactLeaseScope : IDisposable
{
    private ProtocolV2TerminalArtifactLease? lease = new();

    internal T Own<T>(T child)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(child);
        ProtocolV2TerminalArtifactLease? current = lease;
        if (current is null)
        {
            try
            {
                child.Dispose();
            }
            catch
            {
                // A late child is still released without obscuring misuse.
            }
            throw new ObjectDisposedException(
                nameof(ProtocolV2TerminalArtifactLeaseScope));
        }

        return current.Own(child);
    }

    internal IDisposable? Transfer() => Interlocked.Exchange(
        ref lease,
        null);

    public void Dispose() => Interlocked.Exchange(
        ref lease,
        null)?.Dispose();
}
