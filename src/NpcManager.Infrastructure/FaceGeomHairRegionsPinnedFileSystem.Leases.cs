using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed class FaceGeomHairRegionsPinnedDirectoryChain(
    List<SafeFileHandle> handles) : IDisposable
{
    private bool disposed;

    public SafeFileHandle ParentHandle =>
        !disposed && handles.Count > 0
            ? handles[^1]
            : throw new ObjectDisposedException(
                nameof(FaceGeomHairRegionsPinnedDirectoryChain));

    public void Dispose()
    {
        if (disposed)
            return;
        for (int index = handles.Count - 1;
             index >= 0;
             index--)
            handles[index].Dispose();
        disposed = true;
    }
}

internal sealed class FaceGeomHairRegionsPinnedDirectory :
    IDisposable
{
    private readonly FaceGeomHairRegionsPinnedFileSystem owner;
    private FaceGeomHairRegionsWindowsFileIdentity
        identity;
    private readonly SafeFileHandle handle;
    private readonly FaceGeomHairRegionsPinnedDirectoryChain
        parents;
    private readonly string role;
    private bool disposed;

    public FaceGeomHairRegionsPinnedDirectory(
        FaceGeomHairRegionsPinnedFileSystem owner,
        string path,
        FaceGeomHairRegionsWindowsFileIdentity identity,
        SafeFileHandle handle,
        FaceGeomHairRegionsPinnedDirectoryChain parents,
        string role)
    {
        this.owner = owner;
        Path = path;
        this.identity = identity;
        this.handle = handle;
        this.parents = parents;
        this.role = role;
    }

    public string Path
    {
        get;
        private set;
    }

    internal bool RenameCommitted
    {
        get;
        private set;
    }

    public async ValueTask<byte[]> ReadExactFileAsync(
        WorkspacePath path,
        long maximumBytes,
        string readRole,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            role);
        await using FaceGeomHairRegionsPinnedReadFile file =
            owner.OpenReadBeneath(
                handle,
                Path,
                path,
                readRole);
        return await file.ReadExactAsync(
            maximumBytes,
            cancellationToken);
    }

    public byte[] ReadExactFile(
        WorkspacePath path,
        long maximumBytes,
        string readRole)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            role);
        using FaceGeomHairRegionsPinnedReadFile file =
            owner.OpenReadBeneath(
                handle,
                Path,
                path,
                readRole);
        return file.ReadExact(maximumBytes);
    }

    public ImmutableArray<
        FaceGeomHairRegionsPinnedTreeEntry>
        EnumerateTree()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            role);
        return owner.EnumerateTree(
            handle,
            Path,
            role);
    }

    public bool DeleteOwnedFile(
        WorkspacePath path,
        out string? failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            owner.ValidateSameDirectoryHandle(
                handle,
                identity,
                Path,
                role);
            owner.DeleteOwnedFileBeneath(
                handle,
                Path,
                path,
                role);
            failure = null;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    public FaceGeomHairRegionsPinnedDirectory
        CreateOwnedDirectory(
            WorkspacePath path,
            string childRole)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            role);
        return owner.CreateOwnedDirectoryBeneath(
            handle,
            Path,
            path,
            childRole);
    }

    public FaceGeomHairRegionsOwnedFile CreateOwnedFile(
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string childRole)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            role);
        return owner.CreateOwnedFileBeneath(
            handle,
            Path,
            temporaryPath,
            destinationPath,
            childRole);
    }

    public ImmutableArray<WorkspacePath> DeleteTree()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ImmutableArray<WorkspacePath> survivors =
            owner.DeleteOwnedTree(
                handle,
                Path,
                role);
        Dispose();
        return survivors;
    }

    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameDirectoryHandle(
            handle,
            identity,
            Path,
            $"{role} current identity");
    }

    public void PromoteNoOverwrite(
        WorkspacePath destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RenameCommitted)
            throw new InvalidOperationException(
                $"The {role} was already promoted.");
        EnsureCurrent();
        owner.ValidateOwnedDirectoryPromotion(
            handle,
            identity,
            Path,
            destination,
            role);
        FaceGeomHairRegionsPinnedFileSystem
            .RenameOwnedDirectoryNoOverwrite(
            handle,
            parents.ParentHandle,
            destination);
        Path = FaceGeomHairRegionsPinnedFileSystem
            .Canonical(destination.Value);
        RenameCommitted = true;
        identity = owner.ValidateRetainedOwnedDirectory(
            handle,
            Path,
            $"{role} promotion");
    }

    public bool TryDeleteIfEmpty(out string? failure)
    {
        failure = null;
        if (disposed)
            return true;
        try
        {
            owner.BeforeCleanup(Path);
            EnsureCurrent();
            FaceGeomHairRegionsWindowsHandleApi
                .DeleteExactHandle(handle);
            Dispose();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            failure = exception.Message;
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        handle.Dispose();
        parents.Dispose();
        disposed = true;
    }
}

internal sealed record FaceGeomHairRegionsPinnedTreeEntry(
    WorkspacePath Path,
    bool IsDirectory);

internal sealed record FaceGeomHairRegionsWindowsDirectoryEntry(
    string Name,
    FileAttributes Attributes);

internal sealed class FaceGeomHairRegionsPinnedReadFile(
    FaceGeomHairRegionsPinnedFileSystem owner,
    string path,
    FaceGeomHairRegionsWindowsFileIdentity identity,
    FileStream stream,
    FaceGeomHairRegionsPinnedDirectoryChain parents) :
    IAsyncDisposable,
    IDisposable
{
    private bool disposed;

    public long Length => stream.Length;

    public string Path
    {
        get;
    } = path;

    public FaceGeomHairRegionsWindowsFileIdentity Identity
    {
        get;
    } = identity;

    public async ValueTask<byte[]> ReadExactAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long length = stream.Length;
        if (length is <= 0 ||
            length > maximumBytes ||
            length > int.MaxValue)
            throw new InvalidDataException(
                $"Pinned file length {length} is outside the admitted 1..{maximumBytes} byte range.");
        stream.Position = 0;
        byte[] bytes =
            GC.AllocateUninitializedArray<byte>(
                checked((int)length));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = await stream.ReadAsync(
                bytes.AsMemory(offset),
                cancellationToken);
            if (count == 0)
                throw new EndOfStreamException(
                    "The pinned file ended before its admitted length.");
            offset += count;
        }
        if (stream.ReadByte() != -1)
            throw new IOException(
                "The pinned file grew during its exact read.");
        owner.AfterPinnedReadback(Path, bytes);
        stream.Position = 0;
        return bytes;
    }

    public byte[] ReadExact(long maximumBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long length = stream.Length;
        if (length is <= 0 ||
            length > maximumBytes ||
            length > int.MaxValue)
            throw new InvalidDataException(
                $"Pinned file length {length} is outside the admitted 1..{maximumBytes} byte range.");
        stream.Position = 0;
        byte[] bytes =
            GC.AllocateUninitializedArray<byte>(
                checked((int)length));
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException(
                "The pinned file grew during its exact read.");
        owner.AfterPinnedReadback(Path, bytes);
        stream.Position = 0;
        return bytes;
    }

    public string ComputeSha256()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        stream.Position = 0;
        string sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream));
        owner.ValidateSameOwnedHandle(
            stream.SafeFileHandle,
            Identity,
            Path,
            "pinned file SHA-256 readback");
        stream.Position = 0;
        return sha256;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        stream.Dispose();
        parents.Dispose();
        disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FaceGeomHairRegionsOwnedFile :
    IAsyncDisposable,
    IDisposable
{
    private readonly FaceGeomHairRegionsPinnedFileSystem owner;
    private readonly string destinationPath;
    private
        FaceGeomHairRegionsWindowsFileIdentity identity;
    private readonly FileStream stream;
    private readonly FaceGeomHairRegionsPinnedDirectoryChain
        parents;
    private readonly SafeFileHandle destinationParentHandle;
    private readonly string role;
    private bool disposed;

    public FaceGeomHairRegionsOwnedFile(
        FaceGeomHairRegionsPinnedFileSystem owner,
        string temporaryPath,
        string destinationPath,
        FaceGeomHairRegionsWindowsFileIdentity identity,
        FileStream stream,
        FaceGeomHairRegionsPinnedDirectoryChain parents,
        string role,
        SafeFileHandle? destinationParentHandle = null)
    {
        this.owner = owner;
        Path = temporaryPath;
        this.destinationPath = destinationPath;
        this.identity = identity;
        this.stream = stream;
        this.parents = parents;
        this.role = role;
        this.destinationParentHandle =
            destinationParentHandle ??
            parents.ParentHandle;
    }

    public string Path
    {
        get;
        private set;
    }

    internal bool RenameCommitted { get; private set; }

    public async ValueTask<byte[]> WriteAndReadbackAsync(
        ReadOnlyMemory<byte> bytes,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (bytes.IsEmpty ||
            bytes.Length > maximumBytes)
            throw new InvalidDataException(
                $"The {role} bytes are outside the admitted write range.");
        stream.Position = 0;
        await stream.WriteAsync(
            bytes,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (stream.Length != bytes.Length)
            throw new IOException(
                $"The {role} length changed during its durable write.");

        return await ReadbackExactAsync(maximumBytes, cancellationToken);
    }

    public byte[] WriteAndReadback(
        ReadOnlyMemory<byte> bytes,
        long maximumBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (bytes.IsEmpty ||
            bytes.Length > maximumBytes)
            throw new InvalidDataException(
                $"The {role} bytes are outside the admitted write range.");
        owner.ValidateSameOwnedHandle(
            stream.SafeFileHandle,
            identity,
            Path,
            $"{role} write");
        stream.Position = 0;
        stream.Write(bytes.Span);
        stream.Flush(flushToDisk: true);
        if (stream.Length != bytes.Length)
            throw new IOException(
                $"The {role} length changed during its durable write.");

        using SafeFileHandle reopened =
            FaceGeomHairRegionsWindowsHandleApi
                .ReopenRead(stream.SafeFileHandle);
        owner.ValidateSameOwnedHandle(
            reopened,
            identity,
            Path,
            $"{role} readback");
        using var readback = new FileStream(
            reopened,
            FileAccess.Read,
            64 * 1024,
            isAsync: true);
        long length = readback.Length;
        if (length is <= 0 ||
            length > maximumBytes ||
            length > int.MaxValue)
            throw new InvalidDataException(
                $"The {role} readback exceeds its admitted length.");
        byte[] observed =
            GC.AllocateUninitializedArray<byte>(
                checked((int)length));
        readback.ReadExactly(observed);
        if (readback.ReadByte() != -1)
            throw new IOException(
                $"The {role} readback grew during verification.");
        owner.AfterOwnedReadback(Path, observed);
        return observed;
    }

    public async ValueTask<byte[]> ReadbackExactAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using SafeFileHandle reopened =
            FaceGeomHairRegionsWindowsHandleApi
                .ReopenRead(stream.SafeFileHandle);
        owner.ValidateSameOwnedHandle(
            reopened,
            identity,
            Path,
            $"{role} readback");
        await using var readback = new FileStream(
            reopened,
            FileAccess.Read,
            64 * 1024,
            isAsync: true);
        long length = readback.Length;
        if (length is <= 0 ||
            length > maximumBytes ||
            length > int.MaxValue)
            throw new InvalidDataException(
                $"The {role} readback exceeds its admitted length.");
        byte[] observed =
            GC.AllocateUninitializedArray<byte>(
                checked((int)length));
        int offset = 0;
        while (offset < observed.Length)
        {
            int count = await readback.ReadAsync(
                observed.AsMemory(offset),
                cancellationToken);
            if (count == 0)
                throw new EndOfStreamException(
                    $"The {role} readback ended early.");
            offset += count;
        }
        if (readback.ReadByte() != -1)
            throw new IOException(
                $"The {role} readback grew during verification.");
        owner.AfterOwnedReadback(Path, observed);
        return observed;
    }

    public async ValueTask WriteAsync(
        Func<Stream, CancellationToken, ValueTask> writer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(writer);
        owner.ValidateSameOwnedHandle(
            stream.SafeFileHandle,
            identity,
            Path,
            $"{role} write");
        await writer(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        owner.ValidateSameOwnedHandle(
            stream.SafeFileHandle,
            identity,
            Path,
            $"{role} write readback");
    }

    public Stream OpenReadbackStream()
    {
        EnsureCurrent();
        SafeFileHandle reopened =
            FaceGeomHairRegionsWindowsHandleApi
                .ReopenRead(stream.SafeFileHandle);
        return new FileStream(
            reopened,
            FileAccess.Read,
            64 * 1024,
            isAsync: true);
    }

    public void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.ValidateSameOwnedHandle(
            stream.SafeFileHandle,
            identity,
            Path,
            $"{role} current identity");
    }

    public void PromoteNoOverwrite()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (RenameCommitted)
            throw new InvalidOperationException(
                $"The {role} was already promoted.");
        EnsureCurrent();
        owner.BeforeRename(Path, destinationPath);
        FaceGeomHairRegionsWindowsHandleApi
            .RenameNoOverwrite(
                stream.SafeFileHandle,
                destinationParentHandle,
                System.IO.Path.GetFileName(destinationPath));
        Path = destinationPath;
        RenameCommitted = true;
        identity = owner.ValidateRetainedOwnedHandle(
            stream.SafeFileHandle,
            destinationPath,
            $"{role} promotion");
    }

    public bool TryDelete(out string? failure)
    {
        failure = null;
        if (disposed)
            return true;
        try
        {
            owner.BeforeCleanup(Path);
            EnsureCurrent();
            FaceGeomHairRegionsWindowsHandleApi
                .DeleteExactHandle(
                    stream.SafeFileHandle);
            Dispose();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            failure = exception.Message;
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        stream.Dispose();
        parents.Dispose();
        disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal readonly record struct
    FaceGeomHairRegionsWindowsFileIdentity(
        FileAttributes Attributes,
        uint VolumeSerialNumber,
        ulong FileId);
