using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NpcManager.Infrastructure;

/// <summary>
/// Deletes a file only through the same exclusive Windows handle used to
/// verify its exact transaction-owned bytes. A replacement is never deleted.
/// </summary>
public static class FaceGeomHairRegionsOwnedFileCleanup
{
    internal const uint OpenReparsePointFlag = 0x00200000;
    private const uint NormalAttributes = 0x00000080;

    public static bool TryDeleteExactFile(
        string path,
        ReadOnlyMemory<byte> expectedBytes,
        out string? failure)
    {
        failure = null;
        if (!OperatingSystem.IsWindows())
        {
            failure =
                "Atomic handle-bound owned cleanup is available only on Windows.";
            return false;
        }
        const uint genericRead = 0x80000000;
        const uint deleteAccess = 0x00010000;
        const uint openExisting = 3;
        using SafeFileHandle handle = CreateFileW(
            path,
            genericRead | deleteAccess,
            (uint)FileShare.Read,
            IntPtr.Zero,
            openExisting,
            NormalAttributes | OpenReparsePointFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
                return true;
            failure = new Win32Exception(error).Message;
            return false;
        }
        try
        {
            if (!GetFileInformationByHandle(
                    handle,
                    out ByHandleFileInformation information))
            {
                failure = new Win32Exception(
                    Marshal.GetLastWin32Error()).Message;
                return false;
            }
            if (!IsOrdinaryFileAttributes(
                    (FileAttributes)information.FileAttributes))
            {
                failure =
                    "Owned cleanup refused a reparse, directory, or device object.";
                return false;
            }
            long length = RandomAccess.GetLength(handle);
            if (length != expectedBytes.Length)
            {
                failure =
                    "Artifact length no longer matches the exact transaction-owned bytes.";
                return false;
            }
            byte[] observed = new byte[checked((int)length)];
            int read = 0;
            while (read < observed.Length)
            {
                int count = RandomAccess.Read(
                    handle,
                    observed.AsSpan(read),
                    read);
                if (count == 0)
                {
                    failure =
                        "Owned cleanup readback ended before the exact byte length.";
                    return false;
                }
                read += count;
            }
            if (!observed.AsSpan().SequenceEqual(
                    expectedBytes.Span))
            {
                failure =
                    "Artifact bytes no longer match the exact transaction-owned bytes.";
                return false;
            }
            var disposition = new FileDispositionInfo
            {
                DeleteFile = true
            };
            if (!SetFileInformationByHandle(
                    handle,
                    FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                failure = new Win32Exception(
                    Marshal.GetLastWin32Error()).Message;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                OverflowException)
        {
            failure = exception.Message;
            return false;
        }
    }

    internal static bool IsOrdinaryFileAttributes(
        FileAttributes attributes) =>
        (attributes &
         (FileAttributes.Directory |
          FileAttributes.ReparsePoint |
          FileAttributes.Device)) == 0;

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}
