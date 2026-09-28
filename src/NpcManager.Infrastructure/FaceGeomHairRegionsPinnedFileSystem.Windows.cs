using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NpcManager.Infrastructure;

internal static class FaceGeomHairRegionsWindowsHandleApi
{
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint DeleteAccess = 0x0001_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;
    private const uint ObjectCaseInsensitive = 0x0000_0040;
    private const uint FileDirectoryFile = 0x0000_0001;
    private const uint FileSequentialOnly = 0x0000_0004;
    private const uint FileNonDirectoryFile = 0x0000_0040;
    private const uint NtFileOpenReparsePoint = 0x0020_0000;
    private const uint NtFileOpen = 1;
    private const uint NtFileCreate = 2;
    private const uint FileWriteThrough = 0x0000_0002;

    public static SafeFileHandle OpenExistingDirectory(
        string path) =>
        Open(
            path,
            GenericRead |
            FileReadAttributes,
            FileShare.Read |
            FileShare.Write,
            OpenExisting,
            FileFlagBackupSemantics |
            FileFlagOpenReparsePoint);

    public static SafeFileHandle
        OpenExistingDirectoryRelative(
            SafeFileHandle parent,
            string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            FileReadAttributes |
            Synchronize,
            FileShare.Read |
            FileShare.Write,
            FileDirectoryFile |
            NtFileOpenReparsePoint);

    public static SafeFileHandle
        OpenOwnedDirectoryRelative(
            SafeFileHandle parent,
            string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            DeleteAccess |
            FileReadAttributes |
            Synchronize,
            FileShare.Read |
            FileShare.Write,
            FileDirectoryFile |
            NtFileOpenReparsePoint);

    public static SafeFileHandle OpenExistingFileRelative(
        SafeFileHandle parent,
        string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            FileReadAttributes |
            Synchronize,
            FileShare.Read,
            FileNonDirectoryFile |
            FileSequentialOnly |
            NtFileOpenReparsePoint,
            NtFileOpen);

    public static SafeFileHandle OpenOwnedFileRelative(
        SafeFileHandle parent,
        string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            DeleteAccess |
            FileReadAttributes |
            Synchronize,
            FileShare.Read,
            FileNonDirectoryFile |
            FileSequentialOnly |
            NtFileOpenReparsePoint,
            NtFileOpen);

    public static SafeFileHandle CreateNewFileRelative(
        SafeFileHandle parent,
        string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            GenericWrite |
            DeleteAccess |
            FileReadAttributes |
            Synchronize,
            FileShare.Read,
            FileNonDirectoryFile |
            FileWriteThrough |
            NtFileOpenReparsePoint,
            NtFileCreate);

    public static SafeFileHandle CreateNewDirectoryRelative(
        SafeFileHandle parent,
        string name) =>
        OpenRelative(
            parent,
            name,
            GenericRead |
            DeleteAccess |
            FileReadAttributes |
            Synchronize,
            FileShare.Read |
            FileShare.Write,
            FileDirectoryFile |
            NtFileOpenReparsePoint,
            NtFileCreate);

    public static ImmutableArray<
        FaceGeomHairRegionsWindowsDirectoryEntry>
        EnumerateDirectory(SafeFileHandle directory)
    {
        const int bufferSize = 64 * 1024;
        const int fileAttributesOffset = 56;
        const int fileNameLengthOffset = 60;
        const int fileNameOffset = 68;
        var entries = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsWindowsDirectoryEntry>();
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bool restart = true;
            while (true)
            {
                bool succeeded =
                    GetFileInformationByHandleEx(
                        directory,
                        restart
                            ? FileInfoByHandleClass
                                .FileFullDirectoryRestartInfo
                            : FileInfoByHandleClass
                                .FileFullDirectoryInfo,
                        buffer,
                        bufferSize);
                restart = false;
                if (!succeeded)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 18)
                        break;
                    ThrowLastIoError(
                        "Pinned directory enumeration failed");
                }
                int offset = 0;
                while (true)
                {
                    int next = Marshal.ReadInt32(
                        buffer,
                        offset);
                    int nameBytes = Marshal.ReadInt32(
                        buffer,
                        checked(
                            offset +
                            fileNameLengthOffset));
                    if (nameBytes < 0 ||
                        (nameBytes & 1) != 0 ||
                        nameBytes >
                        bufferSize -
                        offset -
                        fileNameOffset)
                        throw new IOException(
                            "Pinned directory enumeration returned a malformed name.");
                    string name =
                        Marshal.PtrToStringUni(
                            IntPtr.Add(
                                buffer,
                                checked(
                                    offset +
                                    fileNameOffset)),
                            nameBytes / 2) ??
                        throw new IOException(
                            "Pinned directory enumeration returned a null name.");
                    if (name is not ("." or ".."))
                    {
                        var attributes =
                            (FileAttributes)(uint)
                            Marshal.ReadInt32(
                                buffer,
                                checked(
                                    offset +
                                    fileAttributesOffset));
                        entries.Add(
                            new FaceGeomHairRegionsWindowsDirectoryEntry(
                                name,
                                attributes));
                    }
                    if (next == 0)
                        break;
                    if (next < fileNameOffset ||
                        next > bufferSize - offset)
                        throw new IOException(
                            "Pinned directory enumeration returned a malformed offset.");
                    offset = checked(offset + next);
                }
            }
            return entries.ToImmutable();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static SafeFileHandle ReopenRead(
        SafeFileHandle source)
    {
        SafeFileHandle handle = ReOpenFile(
            source,
            GenericRead |
            FileReadAttributes |
            Synchronize,
            (uint)(
                FileShare.Read |
                FileShare.Write |
                FileShare.Delete),
            FileFlagOpenReparsePoint |
            FileFlagOverlapped |
            FileFlagSequentialScan);
        if (!handle.IsInvalid)
            return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            new Win32Exception(error).Message);
    }

    public static void RenameNoOverwrite(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationName)
    {
        if (string.IsNullOrWhiteSpace(destinationName) ||
            destinationName.IndexOfAny(
                [
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ]) >= 0)
            throw new InvalidDataException(
                "A handle-relative rename destination must be one segment.");
        int nameBytes = checked(
            destinationName.Length * 2);
        int nameOffset = checked(
            (int)Marshal.OffsetOf<FileRenameInformation>(
                nameof(
                    FileRenameInformation.FileName)));
        int bufferSize = checked(
            nameOffset + nameBytes);
        IntPtr buffer =
            Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int index = 0; index < bufferSize; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(
                buffer,
                checked(
                    (int)Marshal.OffsetOf<
                        FileRenameInformation>(
                        nameof(
                            FileRenameInformation
                                .RootDirectory))),
                destinationParent.DangerousGetHandle());
            Marshal.WriteInt32(
                buffer,
                checked(
                    (int)Marshal.OffsetOf<
                        FileRenameInformation>(
                        nameof(
                            FileRenameInformation
                                .FileNameLength))),
                nameBytes);
            char[] characters =
                destinationName.ToCharArray();
            Marshal.Copy(
                characters,
                0,
                IntPtr.Add(buffer, nameOffset),
                characters.Length);
            int status = NtSetInformationFile(
                source,
                out _,
                buffer,
                (uint)bufferSize,
                FileRenameInformationClass);
            if (status < 0)
                ThrowNtIoError(
                    status,
                    "Handle-relative no-overwrite rename failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void DeleteExactHandle(
        SafeFileHandle handle)
    {
        var disposition =
            new FileDispositionInformation
            {
                DeleteFile = true
            };
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<
                    FileDispositionInformation>()))
            ThrowLastIoError(
                "Identity-bound cleanup failed");
    }

    public static FaceGeomHairRegionsWindowsFileIdentity
        ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(
                handle,
                out ByHandleFileInformation information))
            throw new IOException(
                new Win32Exception(
                    Marshal.GetLastWin32Error()).Message);
        return new FaceGeomHairRegionsWindowsFileIdentity(
            (FileAttributes)information.FileAttributes,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);
    }

    public static string GetFinalDosPath(
        SafeFileHandle handle)
    {
        uint required = GetFinalPathNameByHandle(
            handle,
            null,
            0,
            FileNameNormalized | VolumeNameDos);
        if (required == 0)
            throw new IOException(
                new Win32Exception(
                    Marshal.GetLastWin32Error()).Message);
        var buffer =
            new char[checked((int)required + 1)];
        uint written = GetFinalPathNameByHandle(
            handle,
            buffer,
            (uint)buffer.Length,
            FileNameNormalized | VolumeNameDos);
        if (written == 0 ||
            written >= buffer.Length)
            throw new IOException(
                new Win32Exception(
                    Marshal.GetLastWin32Error()).Message);
        string path = new(
            buffer,
            0,
            checked((int)written));
        return path.StartsWith(
                @"\\?\UNC\",
                StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
    }

    private static SafeFileHandle Open(
        string path,
        uint desiredAccess,
        FileShare share,
        uint disposition,
        uint flags)
    {
        SafeFileHandle handle = CreateFileW(
            ToNativePath(path),
            desiredAccess,
            (uint)share,
            IntPtr.Zero,
            disposition,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is 5 or 32)
            throw new UnauthorizedAccessException(
                new Win32Exception(error).Message);
        throw new IOException(
            new Win32Exception(error).Message);
    }

    private static SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        FileShare share,
        uint createOptions,
        uint createDisposition = NtFileOpen)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(
                [
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ]) >= 0)
            throw new InvalidDataException(
                "A handle-relative name must be one ordinary path segment.");
        IntPtr nameBuffer =
            Marshal.StringToHGlobalUni(name);
        IntPtr unicodePointer = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked(
                    (ushort)(name.Length * 2)),
                MaximumLength = checked(
                    (ushort)((name.Length + 1) * 2)),
                Buffer = nameBuffer
            };
            unicodePointer =
                Marshal.AllocHGlobal(
                    Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                unicode,
                unicodePointer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory =
                    parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            int status = NtCreateFile(
                out SafeFileHandle handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                (uint)FileAttributes.Normal,
                (uint)share,
                createDisposition,
                createOptions,
                IntPtr.Zero,
                0);
            if (status >= 0 &&
                !handle.IsInvalid)
                return handle;
            int error = unchecked(
                (int)RtlNtStatusToDosError(status));
            handle?.Dispose();
            if (error is 5 or 32)
                throw new UnauthorizedAccessException(
                    new Win32Exception(error).Message);
            throw new IOException(
                new Win32Exception(error).Message);
        }
        finally
        {
            if (unicodePointer != IntPtr.Zero)
                Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static string ToNativePath(string path)
    {
        string canonical =
            FaceGeomHairRegionsPinnedFileSystem
                .Canonical(path);
        return canonical.StartsWith(
                @"\\?\",
                StringComparison.Ordinal)
            ? canonical
                : @"\\?\" + canonical;
    }

    private static void ThrowLastIoError(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        if (error is 5 or 32)
            throw new UnauthorizedAccessException(
                $"{operation}: {new Win32Exception(error).Message}");
        throw new IOException(
            $"{operation}: {new Win32Exception(error).Message}");
    }

    private static void ThrowNtIoError(
        int status,
        string operation)
    {
        int error = unchecked(
            (int)RtlNtStatusToDosError(status));
        if (error is 5 or 32)
            throw new UnauthorizedAccessException(
                $"{operation}: {new Win32Exception(error).Message}");
        throw new IOException(
            $"{operation}: {new Win32Exception(error).Message}");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    private const int FileRenameInformationClass = 10;

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(
        int status);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(
        SafeFileHandle originalFile,
        uint desiredAccess,
        uint shareMode,
        uint flagsAndAttributes);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        IntPtr information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        IntPtr information,
        int bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
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
        out ByHandleFileInformation information);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME
            LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileFullDirectoryInfo = 14,
        FileFullDirectoryRestartInfo = 15
    }
}
