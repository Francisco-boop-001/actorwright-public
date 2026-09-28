using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileNameNormalized = 0;
    private const uint VolumeNameDos = 0;
    private const int ErrorAlreadyExists = 183;

    internal static PinnedOutputTree? PinOutputAncestry(
        WorkspacePath workspaceRoot,
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!OperatingSystem.IsWindows())
        {
            diagnostics.Add(new Diagnostic("blank-npc-output-platform", DiagnosticSeverity.Error,
                "Pinned blank-NPC output ownership is supported only on Windows."));
            return null;
        }

        var parent = Directory.GetParent(outputRoot.Value)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic("blank-npc-output-parent-missing", DiagnosticSeverity.Error,
                "The output root's admitted parent directory must already exist."));
            return null;
        }

        var canonicalRoot = CanonicalPath(workspaceRoot.Value);
        var canonicalParent = CanonicalPath(parent);
        if (!IsSameOrUnder(canonicalParent, canonicalRoot))
        {
            diagnostics.Add(new Diagnostic("blank-npc-output-parent-outside-lab", DiagnosticSeverity.Error,
                "The output parent must be an existing directory under the workspace root."));
            return null;
        }

        var tree = new PinnedOutputTree(canonicalRoot, CanonicalPath(outputRoot.Value));
        foreach (var path in ExpandExistingPath(canonicalRoot, canonicalParent))
        {
            if (!TryOpenPinnedDirectory(path, owned: false, diagnostics, out var lease))
            {
                tree.Dispose();
                return null;
            }
            tree.Add(lease!);
        }
        return tree;
    }

    private static IEnumerable<string> ExpandExistingPath(string root, string descendant)
    {
        yield return root;
        if (string.Equals(root, descendant, StringComparison.OrdinalIgnoreCase)) yield break;

        var relative = Path.GetRelativePath(root, descendant);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static bool TryOpenPinnedDirectory(
        string path,
        bool owned,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out PinnedDirectory? lease)
    {
        lease = null;
        var handle = OpenFileHandle(path, FileReadAttributes, FileShare.Read | FileShare.Write,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-directory-pin-failed", DiagnosticSeverity.Error,
                $"Could not pin directory '{path}': {new Win32Exception(error).Message}"));
            return false;
        }

        if (!TryReadHandleIdentity(handle, out var identity, out var identityError))
        {
            handle.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-directory-identity-failed", DiagnosticSeverity.Error,
                $"Could not identify pinned directory '{path}': {identityError}"));
            return false;
        }
        if (!identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            handle.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-directory-reparse-refused", DiagnosticSeverity.Error,
                $"Pinned path '{path}' is not an ordinary directory."));
            return false;
        }

        string expected;
        string actual;
        try
        {
            expected = CanonicalPath(path);
            actual = CanonicalPath(GetFinalDosPath(handle));
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or ArgumentException)
        {
            handle.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-directory-final-path-failed", DiagnosticSeverity.Error,
                $"Could not resolve pinned directory '{path}': {exception.Message}"));
            return false;
        }
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            diagnostics.Add(new Diagnostic("blank-npc-directory-identity-mismatch", DiagnosticSeverity.Error,
                $"Pinned directory resolved as '{actual}' instead of admitted path '{expected}'."));
            return false;
        }

        lease = new PinnedDirectory(expected, owned, identity, handle);
        return true;
    }

    private static SafeFileHandle OpenFileHandle(
        string path,
        uint desiredAccess,
        FileShare share,
        uint flags) =>
        CreateFile(path, desiredAccess, (uint)share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

    private static bool TryReadHandleIdentity(
        SafeFileHandle handle,
        out FileHandleIdentity identity,
        out string error)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            identity = default;
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        identity = new FileHandleIdentity(
            (FileAttributes)info.FileAttributes,
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        error = string.Empty;
        return true;
    }

    private static string GetFinalDosPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandle(handle, null, 0, FileNameNormalized | VolumeNameDos);
        if (required == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
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

    private static string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, IntPtr securityAttributes);

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

    internal readonly record struct FileHandleIdentity(
        FileAttributes Attributes,
        uint VolumeSerialNumber,
        ulong FileId);

    internal sealed class PinnedDirectory(
        string path,
        bool owned,
        FileHandleIdentity identity,
        SafeFileHandle handle) : IDisposable
    {
        public string Path { get; } = path;
        public bool Owned { get; } = owned;
        public FileHandleIdentity Identity { get; } = identity;
        public SafeFileHandle Handle { get; } = handle;

        public void Dispose() => Handle.Dispose();
    }

    internal sealed class PinnedOutputTree(string workspaceRoot, string outputRoot) : IDisposable
    {
        private readonly List<PinnedDirectory> directories = [];
        private bool disposed;

        internal void Add(PinnedDirectory directory) => directories.Add(directory);

        public bool TryCreateOutputRoot(
            ImmutableArray<Diagnostic>.Builder diagnostics) =>
            TryCreateOwnedDirectory(outputRoot, diagnostics);

        public bool TryCreateRequiredDirectories(
            IEnumerable<string> requiredDirectories,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            foreach (var requested in requiredDirectories)
            {
                var canonical = CanonicalPath(requested);
                if (!IsSameOrUnder(canonical, outputRoot))
                {
                    diagnostics.Add(new Diagnostic("blank-npc-directory-outside-output", DiagnosticSeverity.Error,
                        $"Required package directory '{canonical}' escaped the pinned output root."));
                    return false;
                }
                foreach (var path in ExpandExistingPath(outputRoot, canonical).Skip(1))
                {
                    if (directories.Any(item => string.Equals(item.Path, path,
                            StringComparison.OrdinalIgnoreCase))) continue;
                    if (!TryCreateOwnedDirectory(path, diagnostics)) return false;
                }
            }
            return true;
        }

        public ImmutableArray<Diagnostic> RollbackOwnedDirectories()
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var directory in directories.Where(item => item.Owned).Reverse())
            {
                if (!TryReadHandleIdentity(directory.Handle, out var current, out var identityError) ||
                    current != directory.Identity)
                {
                    diagnostics.Add(new Diagnostic("blank-npc-rollback-directory-identity", DiagnosticSeverity.Error,
                        $"Refused to remove pinned directory '{directory.Path}': {identityError}"));
                    directory.Dispose();
                    continue;
                }

                directory.Dispose();
                var deleteHandle = OpenFileHandle(directory.Path,
                    FileReadAttributes | DeleteAccess,
                    FileShare.Read | FileShare.Write,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint);
                if (deleteHandle.IsInvalid)
                {
                    var nativeError = Marshal.GetLastWin32Error();
                    deleteHandle.Dispose();
                    diagnostics.Add(new Diagnostic("blank-npc-rollback-directory-reopen",
                        DiagnosticSeverity.Error,
                        $"Owned directory '{directory.Path}' could not be reopened for identity-bound deletion: " +
                        new Win32Exception(nativeError).Message));
                    continue;
                }

                using (deleteHandle)
                {
                    if (!TryReadHandleIdentity(deleteHandle, out var deleteIdentity, out identityError) ||
                        deleteIdentity != directory.Identity ||
                        !deleteIdentity.Attributes.HasFlag(FileAttributes.Directory) ||
                        deleteIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        !string.Equals(CanonicalPath(GetFinalDosPath(deleteHandle)), directory.Path,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-directory-identity",
                            DiagnosticSeverity.Error,
                            $"Owned directory '{directory.Path}' changed identity before deletion and was left untouched. " +
                            identityError));
                        continue;
                    }

                    var disposition = new FileDispositionInformation { DeleteFile = true };
                    if (!SetFileInformationByHandle(deleteHandle,
                        FileInfoByHandleClass.FileDispositionInfo,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInformation>()))
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-directory-not-empty",
                            DiagnosticSeverity.Error,
                            $"Owned directory '{directory.Path}' was not removed: " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message));
                    }
                }
            }
            foreach (var directory in directories.Where(item => !item.Owned).Reverse()) directory.Dispose();
            disposed = true;
            return diagnostics.ToImmutable();
        }

        private bool TryCreateOwnedDirectory(
            string path,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (!IsSameOrUnder(path, workspaceRoot) || !IsSameOrUnder(path, outputRoot))
            {
                diagnostics.Add(new Diagnostic("blank-npc-directory-outside-output", DiagnosticSeverity.Error,
                    $"Refused to create package directory outside the pinned output root: '{path}'."));
                return false;
            }
            if (!CreateDirectoryExclusive(path, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                diagnostics.Add(error == ErrorAlreadyExists
                    ? new Diagnostic("blank-npc-output-raced", DiagnosticSeverity.Error,
                        $"Package path '{path}' appeared before exclusive ownership was acquired; it was left untouched.")
                    : new Diagnostic("blank-npc-output-create-failed", DiagnosticSeverity.Error,
                        $"Exclusive directory creation failed for '{path}': {new Win32Exception(error).Message}"));
                return false;
            }
            if (!TryOpenPinnedDirectory(path, owned: true, diagnostics, out var lease))
            {
                diagnostics.Add(new Diagnostic("blank-npc-output-create-unclaimed",
                    DiagnosticSeverity.Error,
                    $"The newly created directory '{path}' could not be identity-pinned and was left untouched."));
                return false;
            }
            directories.Add(lease!);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (var directory in directories.AsEnumerable().Reverse()) directory.Dispose();
            disposed = true;
        }
    }
}
