using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Public lifetime wrapper over the retained-handle directory ownership used
/// by the HairTint transaction. It creates only new K-local directories and
/// deletes only the exact tree it created.
/// </summary>
public sealed class FaceGeomHairRegionsOwnedDirectoryLease :
    IDisposable
{
    private FaceGeomHairRegionsPinnedDirectory? owned;
    private readonly string role;
    private readonly WorkspacePath initialPath;

    public FaceGeomHairRegionsOwnedDirectoryLease(
        WorkspacePath workspaceRoot,
        WorkspacePath path,
        string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException(
                "Owned directory role is required.",
                nameof(role));
        }
        this.role = role;
        initialPath = path;
        var boundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot);
        owned = boundary.CreateOwnedDirectory(
            path,
            role);
    }

    private FaceGeomHairRegionsOwnedDirectoryLease(
        FaceGeomHairRegionsPinnedDirectory owned,
        string role)
    {
        this.owned = owned;
        this.role = role;
        initialPath = new WorkspacePath(owned.Path);
    }

    public WorkspacePath Path => new(
        owned?.Path ?? initialPath.Value);

    public bool IsPromoted => owned?.RenameCommitted ?? false;

    public FaceGeomHairRegionsOwnedDirectoryLease CreateChild(
        WorkspacePath child,
        string childRole)
    {
        ObjectDisposedException.ThrowIf(
            owned is null,
            this);
        if (string.IsNullOrWhiteSpace(childRole))
        {
            throw new ArgumentException(
                "Owned child role is required.",
                nameof(childRole));
        }
        FaceGeomHairRegionsPinnedDirectory result =
            owned!.CreateOwnedDirectory(
                child,
                childRole);
        return new(
            result,
            childRole);
    }

    public ImmutableArray<WorkspacePath> DeleteTree()
    {
        if (owned is null)
        {
            return [];
        }
        ImmutableArray<WorkspacePath> survivors =
            owned.DeleteTree();
        owned = null;
        return survivors;
    }

    /// <summary>
    /// Validates that the retained directory handle still represents the
    /// originally created ordinary directory.
    /// </summary>
    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        owned!.EnsureCurrent();
    }

    /// <summary>
    /// Promotes the retained directory handle to a fresh destination without
    /// resolving the source path again.
    /// </summary>
    public void PromoteNoOverwrite(WorkspacePath destination)
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        owned!.PromoteNoOverwrite(destination);
    }

    /// <summary>
    /// Releases the retained handle without deleting the owned directory.
    /// </summary>
    public void Release()
    {
        if (owned is null)
            return;
        owned.Dispose();
        owned = null;
    }

    public FaceGeomHairRegionsOwnedFileLease CreateOwnedFile(
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string childRole)
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        EnsureCurrent();
        return new FaceGeomHairRegionsOwnedFileLease(
            owned!.CreateOwnedFile(
                temporaryPath,
                destinationPath,
                childRole));
    }

    public void Dispose()
    {
        ImmutableArray<WorkspacePath> survivors =
            DeleteTree();
        if (!survivors.IsDefaultOrEmpty)
        {
            throw new FaceGeomHairRegionsOperationalException(
                $"The owned {role} cleanup left exact survivors.",
                survivors,
                new IOException(
                    "Retained-handle directory cleanup was incomplete."));
        }
    }
}

/// <summary>
/// Public narrow facade over the retained-handle file ownership used by
/// transaction writers. Disposal releases the handle; cleanup and promotion
/// are explicit so callers cannot accidentally delete a successful artifact.
/// </summary>
public sealed class FaceGeomHairRegionsOwnedFileLease :
    IDisposable,
    IAsyncDisposable
{
    private FaceGeomHairRegionsOwnedFile? owned;

    public FaceGeomHairRegionsOwnedFileLease(
        WorkspacePath workspaceRoot,
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string role)
        : this(
            new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot)
                .CreateOwnedFile(temporaryPath, destinationPath, role))
    {
    }

    internal FaceGeomHairRegionsOwnedFileLease(
        FaceGeomHairRegionsOwnedFile owned)
    {
        this.owned = owned ?? throw new ArgumentNullException(nameof(owned));
    }

    public WorkspacePath Path => new(
        owned?.Path ?? throw new ObjectDisposedException(
            nameof(FaceGeomHairRegionsOwnedFileLease)));

    public bool IsPromoted => owned?.RenameCommitted ?? false;

    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        owned!.EnsureCurrent();
    }

    public ValueTask WriteAsync(
        Func<Stream, CancellationToken, ValueTask> writer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        return owned!.WriteAsync(writer, cancellationToken);
    }

    public Stream OpenReadbackStream()
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        return owned!.OpenReadbackStream();
    }

    public ValueTask<byte[]> ReadbackExactAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        return owned!.ReadbackExactAsync(maximumBytes, cancellationToken);
    }

    public void PromoteNoOverwrite()
    {
        ObjectDisposedException.ThrowIf(owned is null, this);
        owned!.PromoteNoOverwrite();
    }

    public bool TryDelete(out string? failure)
    {
        if (owned is null)
        {
            failure = null;
            return true;
        }
        bool deleted = owned.TryDelete(out failure);
        owned = null;
        return deleted;
    }

    public void Dispose()
    {
        if (owned is null)
            return;
        owned.Dispose();
        owned = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
