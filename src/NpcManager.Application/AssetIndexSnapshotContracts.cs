namespace NpcManager.Application;

/// <summary>
/// Creates a bounded immutable view of copied game assets for one operation.
/// Index consumers sharing the same scoped indexer observe the same snapshot,
/// and the snapshot is discarded when the operation finishes.
/// </summary>
public interface IAssetIndexSnapshotScopeFactory
{
    IDisposable BeginSnapshot();
}
