using System.Collections.Immutable;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Narrow transaction seam over the retained-handle ownership used by Finish
/// Core. The production implementation is backed by the FaceGeom pinned
/// filesystem; tests may supply a controlled identity implementation without
/// changing native handle sharing or production cleanup behavior.
/// </summary>
internal interface ISkyrimNpcFinishCoreOwnedDirectoryLease : IDisposable
{
    WorkspacePath Path { get; }

    bool IsPromoted { get; }

    void EnsureCurrent();

    void PromoteNoOverwrite(WorkspacePath destination);

    ImmutableArray<WorkspacePath> DeleteTree();

    void Release();
}

internal interface ISkyrimNpcFinishCoreOwnedFileLease : IDisposable
{
    WorkspacePath Path { get; }

    bool IsPromoted { get; }

    void EnsureCurrent();

    ValueTask WriteAsync(
        Func<Stream, CancellationToken, ValueTask> writer,
        CancellationToken cancellationToken);

    Stream OpenReadbackStream();

    void PromoteNoOverwrite();

    bool TryDelete(out string? failure);
}

internal interface ISkyrimNpcFinishCoreOwnedLeaseFactory
{
    ISkyrimNpcFinishCoreOwnedDirectoryLease CreateDirectory(
        WorkspacePath workspaceRoot,
        WorkspacePath path,
        string role);

    ISkyrimNpcFinishCoreOwnedFileLease CreateFile(
        WorkspacePath workspaceRoot,
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string role);
}

internal sealed class FaceGeomSkyrimNpcFinishCoreOwnedLeaseFactory :
    ISkyrimNpcFinishCoreOwnedLeaseFactory
{
    public ISkyrimNpcFinishCoreOwnedDirectoryLease CreateDirectory(
        WorkspacePath workspaceRoot,
        WorkspacePath path,
        string role) =>
        new FaceGeomDirectoryLeaseAdapter(
            new FaceGeomHairRegionsOwnedDirectoryLease(
                workspaceRoot,
                path,
                role));

    public ISkyrimNpcFinishCoreOwnedFileLease CreateFile(
        WorkspacePath workspaceRoot,
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string role) =>
        new FaceGeomFileLeaseAdapter(
            new FaceGeomHairRegionsOwnedFileLease(
                workspaceRoot,
                temporaryPath,
                destinationPath,
                role));

    private sealed class FaceGeomDirectoryLeaseAdapter(
        FaceGeomHairRegionsOwnedDirectoryLease lease) :
        ISkyrimNpcFinishCoreOwnedDirectoryLease
    {
        public WorkspacePath Path => lease.Path;

        public bool IsPromoted => lease.IsPromoted;

        public void EnsureCurrent() => lease.EnsureCurrent();

        public void PromoteNoOverwrite(WorkspacePath destination) =>
            lease.PromoteNoOverwrite(destination);

        public ImmutableArray<WorkspacePath> DeleteTree() =>
            lease.DeleteTree();

        public void Release() => lease.Release();

        public void Dispose() => lease.Dispose();
    }

    private sealed class FaceGeomFileLeaseAdapter(
        FaceGeomHairRegionsOwnedFileLease lease) :
        ISkyrimNpcFinishCoreOwnedFileLease
    {
        public WorkspacePath Path => lease.Path;

        public bool IsPromoted => lease.IsPromoted;

        public void EnsureCurrent() => lease.EnsureCurrent();

        public ValueTask WriteAsync(
            Func<Stream, CancellationToken, ValueTask> writer,
            CancellationToken cancellationToken) =>
            lease.WriteAsync(writer, cancellationToken);

        public Stream OpenReadbackStream() => lease.OpenReadbackStream();

        public void PromoteNoOverwrite() => lease.PromoteNoOverwrite();

        public bool TryDelete(out string? failure) =>
            lease.TryDelete(out failure);

        public void Dispose() => lease.Dispose();
    }
}
