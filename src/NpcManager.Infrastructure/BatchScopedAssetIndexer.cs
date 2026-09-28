using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Memoizes exact asset indexes only while an owning operation holds a scope.
/// This avoids rescanning and rehashing copied Data for each NPC without
/// allowing one later GUI run to reuse stale filesystem state.
/// </summary>
public sealed class BatchScopedAssetIndexer(IAssetIndexer inner)
    : IAssetIndexer, IAssetIndexSnapshotScopeFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<SnapshotKey, Task<AssetIndex>> _snapshots = [];
    private bool _active;

    public IDisposable BeginSnapshot()
    {
        lock (_gate)
        {
            if (_active)
            {
                throw new InvalidOperationException(
                    "An asset-index snapshot is already active.");
            }

            _active = true;
            _snapshots.Clear();
            return new SnapshotLease(this);
        }
    }

    public ValueTask<AssetIndex> IndexAsync(
        AssetIndexRequest request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return inner.IndexAsync(request, cancellationToken);
            }

            var key = new SnapshotKey(request.Edition,
                Path.GetFullPath(request.DataRoot.Value));
            if (!_snapshots.TryGetValue(key, out Task<AssetIndex>? snapshot))
            {
                snapshot = LoadAsync(request, cancellationToken);
                _snapshots.Add(key, snapshot);
            }

            return new ValueTask<AssetIndex>(snapshot.WaitAsync(cancellationToken));
        }
    }

    private async Task<AssetIndex> LoadAsync(
        AssetIndexRequest request,
        CancellationToken cancellationToken) =>
        await inner.IndexAsync(request, cancellationToken).ConfigureAwait(false);

    private void EndSnapshot()
    {
        lock (_gate)
        {
            _snapshots.Clear();
            _active = false;
        }
    }

    private sealed class SnapshotLease(BatchScopedAssetIndexer owner) : IDisposable
    {
        private BatchScopedAssetIndexer? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndSnapshot();
    }

    private readonly record struct SnapshotKey(
        GameEdition Edition,
        string DataRoot);
}
