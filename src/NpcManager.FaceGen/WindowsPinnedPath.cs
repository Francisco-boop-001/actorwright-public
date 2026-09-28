using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

internal enum PinnedPathFailure
{
    None,
    OpenFailed,
    ReparseOrWrongType,
    IdentityMismatch
}

/// <summary>
/// Opens Windows files and directories without following the final reparse
/// point and keeps their identities pinned for the lifetime of the lease.
/// </summary>
internal static class WindowsPinnedPath
{
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint DeleteAccess = 0x0001_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;
    internal const int ErrorAlreadyExists = 183;

    public static bool TryOpenDirectory(
        string path,
        bool owned,
        out PinnedWindowsDirectory? lease,
        out PinnedPathFailure failure,
        out string error)
    {
        lease = null;
        failure = PinnedPathFailure.None;
        error = string.Empty;
        var canonical = CanonicalPath(path);
        var desiredAccess = FileReadAttributes | (owned ? DeleteAccess : 0u);
        var handle = CreateFile(ToNativePath(canonical), desiredAccess,
            (uint)(FileShare.Read | FileShare.Write),
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            handle.Dispose();
            failure = PinnedPathFailure.OpenFailed;
            error = new Win32Exception(nativeError).Message;
            return false;
        }

        if (!TryReadIdentity(handle, out var identity, out error))
        {
            handle.Dispose();
            failure = PinnedPathFailure.OpenFailed;
            return false;
        }
        if (!identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            handle.Dispose();
            failure = PinnedPathFailure.ReparseOrWrongType;
            error = $"'{canonical}' is not an ordinary directory.";
            return false;
        }
        if (!TryVerifyFinalPath(handle, canonical, out error))
        {
            handle.Dispose();
            failure = PinnedPathFailure.IdentityMismatch;
            return false;
        }

        lease = new PinnedWindowsDirectory(canonical, owned, identity, handle);
        return true;
    }

    public static bool TryOpenFile(
        string path,
        bool deletable,
        out PinnedWindowsFile? lease,
        out PinnedPathFailure failure,
        out string error)
    {
        lease = null;
        failure = PinnedPathFailure.None;
        error = string.Empty;
        var canonical = CanonicalPath(path);
        var desiredAccess = GenericRead | FileReadAttributes | (deletable ? DeleteAccess : 0u);
        var handle = CreateFile(ToNativePath(canonical), desiredAccess,
            (uint)FileShare.Read, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            handle.Dispose();
            failure = PinnedPathFailure.OpenFailed;
            error = new Win32Exception(nativeError).Message;
            return false;
        }

        if (!TryReadIdentity(handle, out var identity, out error))
        {
            handle.Dispose();
            failure = PinnedPathFailure.OpenFailed;
            return false;
        }
        if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            handle.Dispose();
            failure = PinnedPathFailure.ReparseOrWrongType;
            error = $"'{canonical}' is not an ordinary file.";
            return false;
        }
        if (!TryVerifyFinalPath(handle, canonical, out error))
        {
            handle.Dispose();
            failure = PinnedPathFailure.IdentityMismatch;
            return false;
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: true);
            lease = new PinnedWindowsFile(canonical, deletable, identity, stream);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static bool TryCreateOutputFileExclusive(
        string path,
        out PinnedWindowsOutputFile? lease,
        out PinnedPathFailure failure,
        out string error)
    {
        lease = null;
        failure = PinnedPathFailure.None;
        error = string.Empty;
        var canonical = CanonicalPath(path);
        var handle = CreateFile(ToNativePath(canonical), GenericRead | GenericWrite,
            (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero, CreateNew,
            FileFlagOpenReparsePoint | FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            handle.Dispose();
            failure = PinnedPathFailure.OpenFailed;
            error = new Win32Exception(nativeError).Message;
            return false;
        }

        if (!TryReadIdentity(handle, out var identity, out error))
        {
            TryDeleteCreatedHandle(handle, ref error);
            failure = PinnedPathFailure.OpenFailed;
            return false;
        }
        if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            error = $"'{canonical}' is not an ordinary file.";
            TryDeleteCreatedHandle(handle, ref error);
            failure = PinnedPathFailure.ReparseOrWrongType;
            return false;
        }
        if (!TryVerifyFinalPath(handle, canonical, out error))
        {
            TryDeleteCreatedHandle(handle, ref error);
            failure = PinnedPathFailure.IdentityMismatch;
            return false;
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: true);
            lease = new PinnedWindowsOutputFile(canonical, identity, stream);
            return true;
        }
        catch
        {
            TryDeleteCreatedHandle(handle, ref error);
            throw;
        }
    }

    public static bool TryCreateDirectoryExclusive(string path, out int nativeError)
    {
        if (CreateDirectory(ToNativePath(path), IntPtr.Zero))
        {
            nativeError = 0;
            return true;
        }
        nativeError = Marshal.GetLastWin32Error();
        return false;
    }

    public static string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ToNativePath(string path)
    {
        string canonical = CanonicalPath(path);
        if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) return canonical;
        return canonical.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonical[2..]
            : @"\\?\" + canonical;
    }

    public static bool IsSameOrUnder(string path, string root)
    {
        var canonicalPath = CanonicalPath(path);
        var canonicalRoot = CanonicalPath(root);
        if (string.Equals(canonicalPath, canonicalRoot, StringComparison.OrdinalIgnoreCase)) return true;
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static IEnumerable<string> ExpandDirectoryPath(string directory)
    {
        var canonical = CanonicalPath(directory);
        var root = Path.GetPathRoot(canonical) ??
            throw new ArgumentException("The path does not have a filesystem root.", nameof(directory));
        var current = CanonicalPath(root);
        yield return current;
        if (string.Equals(current, canonical, StringComparison.OrdinalIgnoreCase)) yield break;

        var relative = Path.GetRelativePath(current, canonical);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    internal static bool TryDeleteByHandle(SafeFileHandle handle, out string error)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (SetFileInformationByHandle(handle, FileInfoByHandleClass.FileDispositionInfo,
                ref disposition, (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            error = string.Empty;
            return true;
        }
        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    internal static bool TryReopenReadBridge(
        SafeFileHandle source,
        bool allowDeleteSharing,
        out SafeFileHandle reopened,
        out string error) =>
        TryReopen(source, GenericRead,
            allowDeleteSharing
                ? FileShare.Read | FileShare.Write | FileShare.Delete
                : FileShare.Read | FileShare.Write,
            out reopened, out error);

    internal static bool TryReopenReadGuard(
        SafeFileHandle source,
        out SafeFileHandle reopened,
        out string error) =>
        TryReopen(source, GenericRead, FileShare.Read, out reopened, out error);

    internal static bool TryReopenDeleteHandle(
        SafeFileHandle source,
        out SafeFileHandle reopened,
        out string error) =>
        TryReopen(source, GenericRead | DeleteAccess,
            FileShare.Read | FileShare.Delete, out reopened, out error);

    internal static bool TryVerifyIdentityAndPath(
        SafeFileHandle handle,
        WindowsFileIdentity expectedIdentity,
        string expectedPath,
        out string error)
    {
        if (!TryReadIdentity(handle, out var actualIdentity, out error)) return false;
        if (actualIdentity != expectedIdentity)
        {
            error = "The reopened filesystem object changed identity.";
            return false;
        }
        return TryVerifyFinalPath(handle, expectedPath, out error);
    }

    private static bool TryReopen(
        SafeFileHandle source,
        uint desiredAccess,
        FileShare shareMode,
        out SafeFileHandle reopened,
        out string error)
    {
        reopened = ReOpenFile(source, desiredAccess, (uint)shareMode,
            FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan);
        if (!reopened.IsInvalid)
        {
            error = string.Empty;
            return true;
        }

        var nativeError = Marshal.GetLastWin32Error();
        reopened.Dispose();
        error = new Win32Exception(nativeError).Message;
        return false;
    }

    private static void TryDeleteCreatedHandle(SafeFileHandle handle, ref string error)
    {
        if (!handle.IsInvalid)
        {
            if (!TryReopenReadBridge(handle, allowDeleteSharing: true,
                    out var bridge, out var bridgeError))
            {
                error += $" Cleanup could not acquire the created file identity: {bridgeError}";
            }
            else
            {
                handle.Dispose();
                handle = bridge;
                if (!TryReopenDeleteHandle(handle, out var deletable, out var deleteOpenError))
                {
                    error += $" Cleanup could not reopen the created file for deletion: {deleteOpenError}";
                }
                else
                {
                    handle.Dispose();
                    handle = deletable;
                    if (!TryDeleteByHandle(handle, out var deleteError))
                        error += $" Cleanup could not delete the created file: {deleteError}";
                }
            }
        }
        handle.Dispose();
    }

    private static bool TryReadIdentity(
        SafeFileHandle handle,
        out WindowsFileIdentity identity,
        out string error)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            identity = default;
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        identity = new WindowsFileIdentity(
            (FileAttributes)information.FileAttributes,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        error = string.Empty;
        return true;
    }

    private static bool TryVerifyFinalPath(SafeFileHandle handle, string expected, out string error)
    {
        try
        {
            var actual = CanonicalPath(GetFinalDosPath(handle));
            if (string.Equals(actual, CanonicalPath(expected), StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }
            error = $"The opened object resolved as '{actual}' instead of '{expected}'.";
            return false;
        }
        catch (Win32Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string GetFinalDosPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandle(handle, null, 0, FileNameNormalized | VolumeNameDos);
        if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[checked((int)required + 1)];
        var written = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length,
            FileNameNormalized | VolumeNameDos);
        if (written == 0 || written >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new string(buffer, 0, checked((int)written));
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(
        SafeFileHandle originalFile,
        uint desiredAccess,
        uint shareMode,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }
}

internal readonly record struct WindowsFileIdentity(
    FileAttributes Attributes,
    uint VolumeSerialNumber,
    ulong FileId);

internal sealed class PinnedWindowsDirectory(
    string path,
    bool owned,
    WindowsFileIdentity identity,
    SafeFileHandle handle) : IDisposable
{
    private bool disposed;

    public string Path { get; } = path;
    public bool Owned { get; } = owned;
    public WindowsFileIdentity Identity { get; } = identity;

    public bool TryVerifyIdentityAndPath(out string error)
    {
        if (disposed)
        {
            error = "The directory identity lease is already closed.";
            return false;
        }
        return WindowsPinnedPath.TryVerifyIdentityAndPath(handle, Identity, Path, out error);
    }

    public bool TryDeleteIfEmpty(out string error)
    {
        if (!Owned)
        {
            error = "The directory was not created by this codec session.";
            return false;
        }
        if (disposed)
        {
            error = "The directory identity lease is already closed.";
            return false;
        }
        if (Directory.EnumerateFileSystemEntries(Path).Any())
        {
            error = "The directory is not empty.";
            return false;
        }
        if (!WindowsPinnedPath.TryDeleteByHandle(handle, out error)) return false;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        handle.Dispose();
        disposed = true;
    }
}

internal sealed class PinnedWindowsFile(
    string path,
    bool deletable,
    WindowsFileIdentity identity,
    FileStream stream) : IDisposable
{
    private bool disposed;

    public string Path { get; } = path;
    public WindowsFileIdentity Identity { get; } = identity;

    public async ValueTask<byte[]> ReadAllBytesAsync(long maximumBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var length = stream.Length;
        if (length <= 0 || length > maximumBytes || length > int.MaxValue)
            throw new InvalidDataException($"File size {length} is outside the admitted range 1..{maximumBytes} bytes.");

        stream.Position = 0;
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The pinned file ended before its admitted length.");
            offset += read;
        }
        stream.Position = 0;
        return bytes;
    }

    public async ValueTask<byte[]> ReadPrefixAsync(
        int byteCount,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if (stream.Length < byteCount)
            throw new InvalidDataException(
                $"File size {stream.Length} is shorter than the required {byteCount}-byte prefix.");

        stream.Position = 0;
        var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException(
                    "The pinned file ended before its admitted prefix was read.");
            offset += read;
        }
        stream.Position = 0;
        return bytes;
    }

    public async ValueTask<Sha256Hash> HashAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        stream.Position = 0;
        var hash = new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
        stream.Position = 0;
        return hash;
    }

    public bool TryDelete(out string error)
    {
        if (!deletable)
        {
            error = "The file was not opened as an owned deletable output.";
            return false;
        }
        if (disposed)
        {
            error = "The file identity lease is already closed.";
            return false;
        }
        if (!WindowsPinnedPath.TryDeleteByHandle(stream.SafeFileHandle, out error)) return false;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        stream.Dispose();
        disposed = true;
    }
}

/// <summary>
/// Owns one exclusively created output identity from its first byte through
/// verification or identity-bound deletion.
/// </summary>
internal sealed class PinnedWindowsOutputFile(
    string path,
    WindowsFileIdentity identity,
    FileStream stream) : IDisposable
{
    private FileStream? currentStream = stream;
    private SafeFileHandle? bridgeHandle;
    private bool sealedForRead;
    private bool disposed;

    public string Path { get; } = path;
    public WindowsFileIdentity Identity { get; } = identity;

    public async ValueTask<PinnedOutputWriteResult> WriteAndSealAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sealedForRead)
            throw new InvalidOperationException("The output identity is already sealed for readback.");
        if (currentStream is null)
            throw new InvalidOperationException("The output writer identity is unavailable.");

        await currentStream.WriteAsync(bytes, cancellationToken);
        await currentStream.FlushAsync(cancellationToken);
        currentStream.Flush(flushToDisk: true);
        if (currentStream.Length != bytes.Length)
        {
            return new PinnedOutputWriteResult(false,
                "The exclusive output length changed during its durable write.");
        }

        if (!WindowsPinnedPath.TryReopenReadBridge(currentStream.SafeFileHandle,
                allowDeleteSharing: false, out var bridge, out var error))
            return new PinnedOutputWriteResult(false, error);
        currentStream.Dispose();
        currentStream = null;
        bridgeHandle = bridge;
        if (!WindowsPinnedPath.TryReopenReadGuard(bridgeHandle, out var guard, out error))
            return new PinnedOutputWriteResult(false, error);
        bridgeHandle.Dispose();
        bridgeHandle = null;
        currentStream = new FileStream(guard, FileAccess.Read, 64 * 1024, isAsync: true);
        sealedForRead = true;
        if (WindowsPinnedPath.TryVerifyIdentityAndPath(currentStream.SafeFileHandle,
                Identity, Path, out error)) return new PinnedOutputWriteResult(true, string.Empty);
        return new PinnedOutputWriteResult(false, error);
    }

    public async ValueTask<byte[]> ReadAllBytesAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!sealedForRead || currentStream is null)
            throw new InvalidOperationException("The output identity is not sealed for readback.");
        var length = currentStream.Length;
        if (length <= 0 || length > maximumBytes || length > int.MaxValue)
            throw new InvalidDataException(
                $"File size {length} is outside the admitted range 1..{maximumBytes} bytes.");

        currentStream.Position = 0;
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await currentStream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException(
                    "The pinned output ended before its admitted length was read.");
            offset += read;
        }
        currentStream.Position = 0;
        return bytes;
    }

    public bool TryDelete(out string error)
    {
        if (disposed)
        {
            error = "The output identity lease is already closed.";
            return false;
        }

        SafeFileHandle source;
        if (currentStream is not null)
        {
            source = currentStream.SafeFileHandle;
        }
        else if (bridgeHandle is not null)
        {
            source = bridgeHandle;
        }
        else
        {
            error = "The output identity handle is unavailable.";
            return false;
        }

        if (!WindowsPinnedPath.TryReopenReadBridge(source, allowDeleteSharing: true,
                out var cleanupBridge, out error))
        {
            Dispose();
            return false;
        }

        currentStream?.Dispose();
        currentStream = null;
        bridgeHandle?.Dispose();
        bridgeHandle = cleanupBridge;
        if (!WindowsPinnedPath.TryReopenDeleteHandle(bridgeHandle,
                out var deletable, out error))
        {
            Dispose();
            return false;
        }

        bridgeHandle.Dispose();
        bridgeHandle = deletable;
        var deleted = WindowsPinnedPath.TryDeleteByHandle(bridgeHandle, out error);
        Dispose();
        return deleted;
    }

    public void Dispose()
    {
        if (disposed) return;
        currentStream?.Dispose();
        currentStream = null;
        bridgeHandle?.Dispose();
        bridgeHandle = null;
        disposed = true;
    }
}

internal readonly record struct PinnedOutputWriteResult(bool Completed, string Error);
