using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public interface IPreviewServiceFactory<T>
{
    ValueTask<PreviewServiceLease<T>> CreateAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns all native/service state created for one optional preview invocation.
/// </summary>
public sealed class PreviewServiceLease<T> : IAsyncDisposable
{
    private readonly IReadOnlyList<object> _lifetimes;
    private readonly WorkspacePath? _ownedWorkRoot;
    private bool _disposed;

    public PreviewServiceLease(
        T service,
        IReadOnlyList<object>? lifetimes = null,
        WorkspacePath? ownedWorkRoot = null)
    {
        Service = service ??
            throw new ArgumentNullException(nameof(service));
        _lifetimes = lifetimes ?? [];
        _ownedWorkRoot = ownedWorkRoot;
    }

    public T Service { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        Exception? failure = null;
        for (int index = _lifetimes.Count - 1; index >= 0; index--)
        {
            try
            {
                switch (_lifetimes[index])
                {
                    case IAsyncDisposable asynchronous:
                        await asynchronous.DisposeAsync()
                            .ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidOperationException)
            {
                failure ??= exception;
            }
        }

        if (_ownedWorkRoot is { } root &&
            Directory.Exists(root.Value))
        {
            try
            {
                var info = new DirectoryInfo(root.Value);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"Owned preview work root is a reparse point: '{root.Value}'.");
                Directory.Delete(root.Value, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            throw new InvalidOperationException(
                "Preview lease cleanup did not complete.",
                failure);
    }
}
