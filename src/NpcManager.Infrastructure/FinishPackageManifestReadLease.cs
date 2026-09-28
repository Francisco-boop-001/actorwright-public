using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class FinishPackageManifestReadLease : IDisposable
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private FaceGeomHairRegionsPinnedReadFile? source;
    private readonly byte[] initialBytes;

    public FinishPackageManifestReadLease(
        WorkspacePath workspaceRoot,
        WorkspacePath manifest)
    {
        Manifest = manifest;
        source = new FaceGeomHairRegionsPinnedFileSystem(workspaceRoot)
            .OpenRead(manifest, "Finish package manifest");
        try
        {
            initialBytes = source.ReadExact(MaximumBytes);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ReadOnlyMemory<byte> InitialBytes => initialBytes;

    public WorkspacePath Manifest { get; }

    public bool HasExactReadback() =>
        (source ?? throw new ObjectDisposedException(GetType().Name))
        .ReadExact(MaximumBytes)
        .AsSpan()
        .SequenceEqual(initialBytes);

    public void Dispose() => Interlocked.Exchange(ref source, null)?.Dispose();
}
