using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed class FaceGeomHairRegionsPathSubstitutionException(
    string message) : UnauthorizedAccessException(message);

internal interface IFaceGeomHairRegionsPinnedFileSystemHooks
{
    void BeforeFinalOpen(string admittedPath);

    void AfterPinnedReadback(
        string admittedPath,
        byte[] observed)
    {
    }

    string ResolveFinalPath(
        string admittedPath,
        string actualFinalPath);

    void BeforeCreate(string admittedPath);

    void BeforeRename(
        string sourcePath,
        string destinationPath);

    void BeforeCleanup(string admittedPath);

    void AfterOwnedReadback(
        string admittedPath,
        byte[] observed)
    {
    }
}

internal sealed class FaceGeomHairRegionsPinnedFileSystemHooks :
    IFaceGeomHairRegionsPinnedFileSystemHooks
{
    public static FaceGeomHairRegionsPinnedFileSystemHooks Instance
    {
        get;
    } = new();

    public void BeforeFinalOpen(string admittedPath)
    {
    }

    public string ResolveFinalPath(
        string admittedPath,
        string actualFinalPath) =>
        actualFinalPath;

    public void BeforeCreate(string admittedPath)
    {
    }

    public void BeforeRename(
        string sourcePath,
        string destinationPath)
    {
    }

    public void BeforeCleanup(string admittedPath)
    {
    }
}

internal sealed class FaceGeomHairRegionsPinnedFileSystem
{
    private readonly string workspaceRoot;
    private readonly IFaceGeomHairRegionsPinnedFileSystemHooks hooks;

    public FaceGeomHairRegionsPinnedFileSystem(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsPinnedFileSystemHooks? hooks = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "FaceGeom pinned filesystem authority requires Windows.");
        this.workspaceRoot = Canonical(workspaceRoot.Value);
        this.hooks = hooks ??
            FaceGeomHairRegionsPinnedFileSystemHooks.Instance;
    }

    public FaceGeomHairRegionsPinnedReadFile OpenRead(
        WorkspacePath path,
        string role)
    {
        string admitted = Canonical(path.Value);
        RequireUnderWorkspace(admitted, role);
        FaceGeomHairRegionsPinnedDirectoryChain parents =
            PinParent(admitted, role);
        try
        {
            hooks.BeforeFinalOpen(admitted);
            SafeFileHandle handle =
                FaceGeomHairRegionsWindowsHandleApi
                    .OpenExistingFileRelative(
                        parents.ParentHandle,
                        Path.GetFileName(admitted));
            try
            {
                FaceGeomHairRegionsWindowsFileIdentity identity =
                    ValidateOrdinaryFile(
                        handle,
                        admitted,
                        role);
                var stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    64 * 1024,
                    isAsync: true);
                return new FaceGeomHairRegionsPinnedReadFile(
                    this,
                    admitted,
                    identity,
                    stream,
                    parents);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            parents.Dispose();
            throw;
        }
    }

    public FaceGeomHairRegionsOwnedFile CreateOwned(
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string role)
    {
        string temporary =
            Canonical(temporaryPath.Value);
        string destination =
            Canonical(destinationPath.Value);
        RequireUnderWorkspace(temporary, role);
        RequireUnderWorkspace(destination, role);
        string temporaryParent =
            Path.GetDirectoryName(temporary) ??
            throw new InvalidDataException(
                $"The {role} temporary has no parent.");
        string destinationParent =
            Path.GetDirectoryName(destination) ??
            throw new InvalidDataException(
                $"The {role} destination has no parent.");
        if (!string.Equals(
                temporaryParent,
                destinationParent,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The {role} temporary must be beside its destination.");
        FaceGeomHairRegionsPinnedDirectoryChain parents =
            PinParent(destination, role);
        try
        {
            hooks.BeforeCreate(temporary);
            SafeFileHandle handle =
                FaceGeomHairRegionsWindowsHandleApi
                    .CreateNewFileRelative(
                        parents.ParentHandle,
                        Path.GetFileName(temporary));
            try
            {
                FaceGeomHairRegionsWindowsFileIdentity identity =
                    ValidateOrdinaryFile(
                        handle,
                        temporary,
                        role);
                var stream = new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    64 * 1024,
                    isAsync: true);
                return new FaceGeomHairRegionsOwnedFile(
                    this,
                    temporary,
                    destination,
                    identity,
                    stream,
                    parents,
                    role);
            }
            catch
            {
                TryDeleteCreatedHandle(handle);
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            parents.Dispose();
            throw;
        }
    }

    public FaceGeomHairRegionsPinnedDirectory
        CreateOwnedDirectory(
            WorkspacePath path,
            string role)
    {
        string admitted = Canonical(path.Value);
        RequireUnderWorkspace(admitted, role);
        FaceGeomHairRegionsPinnedDirectoryChain parents =
            PinParent(admitted, role);
        try
        {
            hooks.BeforeCreate(admitted);
            SafeFileHandle handle =
                FaceGeomHairRegionsWindowsHandleApi
                    .CreateNewDirectoryRelative(
                        parents.ParentHandle,
                        Path.GetFileName(admitted));
            try
            {
                FaceGeomHairRegionsWindowsFileIdentity identity =
                    ValidateOrdinaryDirectory(
                        handle,
                        admitted,
                        role);
                return new FaceGeomHairRegionsPinnedDirectory(
                    this,
                    admitted,
                    identity,
                    handle,
                    parents,
                    role);
            }
            catch
            {
                TryDeleteCreatedHandle(handle);
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            parents.Dispose();
            throw;
        }
    }

    public FaceGeomHairRegionsPinnedDirectory OpenDirectory(
        WorkspacePath path,
        string role)
    {
        return OpenDirectory(path, role, owned: false);
    }

    public FaceGeomHairRegionsPinnedDirectory OpenOwnedDirectory(
        WorkspacePath path,
        string role)
    {
        return OpenDirectory(path, role, owned: true);
    }

    private FaceGeomHairRegionsPinnedDirectory OpenDirectory(
        WorkspacePath path,
        string role,
        bool owned)
    {
        string admitted = Canonical(path.Value);
        RequireUnderWorkspace(admitted, role);
        FaceGeomHairRegionsPinnedDirectoryChain parents =
            PinParent(admitted, role);
        try
        {
            hooks.BeforeFinalOpen(admitted);
            SafeFileHandle handle = owned
                ? FaceGeomHairRegionsWindowsHandleApi
                    .OpenOwnedDirectoryRelative(
                        parents.ParentHandle,
                        Path.GetFileName(admitted))
                : FaceGeomHairRegionsWindowsHandleApi
                    .OpenExistingDirectoryRelative(
                        parents.ParentHandle,
                        Path.GetFileName(admitted));
            try
            {
                FaceGeomHairRegionsWindowsFileIdentity identity =
                    ValidateOrdinaryDirectory(handle, admitted, role);
                return new FaceGeomHairRegionsPinnedDirectory(
                    this,
                    admitted,
                    identity,
                    handle,
                    parents,
                    role);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            parents.Dispose();
            throw;
        }
    }

    internal FaceGeomHairRegionsPinnedDirectory
        CreateOwnedDirectoryBeneath(
            SafeFileHandle rootHandle,
            string rootPath,
            WorkspacePath path,
            string role)
    {
        string admitted = Canonical(path.Value);
        RequireDirectChild(rootPath, admitted, role);
        ValidateOrdinaryDirectory(
            rootHandle,
            rootPath,
            $"{role} root");
        hooks.BeforeCreate(admitted);
        SafeFileHandle handle =
            FaceGeomHairRegionsWindowsHandleApi
                .CreateNewDirectoryRelative(
                    rootHandle,
                    Path.GetFileName(admitted));
        try
        {
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidateOrdinaryDirectory(
                    handle,
                    admitted,
                    role);
            return new FaceGeomHairRegionsPinnedDirectory(
                this,
                admitted,
                identity,
                handle,
                new FaceGeomHairRegionsPinnedDirectoryChain([]),
                role);
        }
        catch
        {
            TryDeleteCreatedHandle(handle);
            handle.Dispose();
            throw;
        }
    }

    internal FaceGeomHairRegionsOwnedFile
        CreateOwnedFileBeneath(
            SafeFileHandle rootHandle,
            string rootPath,
            WorkspacePath temporaryPath,
            WorkspacePath destinationPath,
            string role)
    {
        string temporary = Canonical(temporaryPath.Value);
        string destination = Canonical(destinationPath.Value);
        RequireDirectChild(rootPath, temporary, role);
        RequireDirectChild(rootPath, destination, role);
        ValidateOrdinaryDirectory(
            rootHandle,
            rootPath,
            $"{role} root");
        hooks.BeforeCreate(temporary);
        SafeFileHandle handle =
            FaceGeomHairRegionsWindowsHandleApi
                .CreateNewFileRelative(
                    rootHandle,
                    Path.GetFileName(temporary));
        try
        {
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidateOrdinaryFile(
                    handle,
                    temporary,
                    role);
            var stream = new FileStream(
                handle,
                FileAccess.ReadWrite,
                64 * 1024,
                isAsync: true);
            return new FaceGeomHairRegionsOwnedFile(
                this,
                temporary,
                destination,
                identity,
                stream,
                new FaceGeomHairRegionsPinnedDirectoryChain([]),
                role,
                rootHandle);
        }
        catch
        {
            TryDeleteCreatedHandle(handle);
            handle.Dispose();
            throw;
        }
    }

    private static void TryDeleteCreatedHandle(SafeFileHandle handle)
    {
        try
        {
            if (!handle.IsClosed && !handle.IsInvalid)
                FaceGeomHairRegionsWindowsHandleApi
                    .DeleteExactHandle(handle);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or Win32Exception)
        {
            _ = exception;
        }
    }

    internal ImmutableArray<WorkspacePath> DeleteOwnedTree(
        SafeFileHandle rootHandle,
        string rootPath,
        string role)
    {
        var survivors =
            ImmutableArray.CreateBuilder<WorkspacePath>();
        DeleteOwnedDirectoryContents(
            rootHandle,
            rootPath,
            role,
            survivors,
            depth: 0);
        try
        {
            hooks.BeforeCleanup(rootPath);
            ValidateOrdinaryDirectory(
                rootHandle,
                rootPath,
                $"{role} cleanup");
            FaceGeomHairRegionsWindowsHandleApi
                .DeleteExactHandle(rootHandle);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            _ = exception;
            survivors.Add(new WorkspacePath(rootPath));
        }
        return survivors
            .Distinct()
            .ToImmutableArray();
    }

    private void DeleteOwnedDirectoryContents(
        SafeFileHandle directory,
        string directoryPath,
        string role,
        ImmutableArray<WorkspacePath>.Builder survivors,
        int depth)
    {
        if (depth > 16)
        {
            survivors.Add(new WorkspacePath(directoryPath));
            return;
        }
        ImmutableArray<
            FaceGeomHairRegionsWindowsDirectoryEntry> entries;
        try
        {
            ValidateOrdinaryDirectory(
                directory,
                directoryPath,
                $"{role} cleanup");
            entries =
                FaceGeomHairRegionsWindowsHandleApi
                    .EnumerateDirectory(directory);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            _ = exception;
            survivors.Add(new WorkspacePath(directoryPath));
            return;
        }
        foreach (FaceGeomHairRegionsWindowsDirectoryEntry entry in
                 entries)
        {
            string path = Path.Combine(
                directoryPath,
                entry.Name);
            if ((entry.Attributes &
                 (FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0)
            {
                survivors.Add(new WorkspacePath(path));
                continue;
            }
            try
            {
                if ((entry.Attributes &
                     FileAttributes.Directory) != 0)
                {
                    using SafeFileHandle child =
                        FaceGeomHairRegionsWindowsHandleApi
                            .OpenOwnedDirectoryRelative(
                                directory,
                                entry.Name);
                    ValidateOrdinaryDirectory(
                        child,
                        path,
                        $"{role} cleanup child");
                    DeleteOwnedDirectoryContents(
                        child,
                        path,
                        role,
                        survivors,
                        depth + 1);
                    if (!survivors.Any(item =>
                            item == new WorkspacePath(path) ||
                            item.IsUnder(new WorkspacePath(path))))
                        FaceGeomHairRegionsWindowsHandleApi
                            .DeleteExactHandle(child);
                }
                else
                {
                    using SafeFileHandle file =
                        FaceGeomHairRegionsWindowsHandleApi
                            .OpenOwnedFileRelative(
                                directory,
                                entry.Name);
                    ValidateOrdinaryFile(
                        file,
                        path,
                        $"{role} cleanup file");
                    FaceGeomHairRegionsWindowsHandleApi
                        .DeleteExactHandle(file);
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or
                    IOException or
                    UnauthorizedAccessException or
                    Win32Exception)
            {
                _ = exception;
                survivors.Add(new WorkspacePath(path));
            }
        }
    }

    private void RequireDirectChild(
        string rootPath,
        string admitted,
        string role)
    {
        string root = Canonical(rootPath);
        RequireUnderWorkspace(root, $"{role} root");
        RequireUnderWorkspace(admitted, role);
        if (!string.Equals(
                Path.GetDirectoryName(admitted),
                root,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"The {role} must be a direct child of its retained root.");
    }

    private FaceGeomHairRegionsPinnedDirectoryChain PinParent(
        string admittedPath,
        string role)
    {
        string parent = Path.GetDirectoryName(admittedPath) ??
            throw new InvalidDataException(
                $"The {role} has no parent directory.");
        RequireUnderWorkspace(parent, $"{role} parent");
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach ((string segment, int index) in
                     ExpandPath(
                             workspaceRoot,
                             parent)
                         .Select((
                             segment,
                             index) =>
                             (segment, index)))
            {
                SafeFileHandle handle =
                    index == 0
                        ? FaceGeomHairRegionsWindowsHandleApi
                            .OpenExistingDirectory(
                                segment)
                        : FaceGeomHairRegionsWindowsHandleApi
                            .OpenExistingDirectoryRelative(
                                handles[^1],
                                Path.GetFileName(segment));
                try
                {
                    FaceGeomHairRegionsWindowsFileIdentity
                        identity =
                            FaceGeomHairRegionsWindowsHandleApi
                                .ReadIdentity(handle);
                    if ((identity.Attributes &
                         FileAttributes.Directory) == 0 ||
                        (identity.Attributes &
                         (FileAttributes.ReparsePoint |
                          FileAttributes.Device)) != 0)
                        throw new FaceGeomHairRegionsPathSubstitutionException(
                            $"The {role} parent ancestry contains a non-ordinary directory.");
                    string finalPath = ResolveFinalPath(
                        handle,
                        segment);
                    if (!string.Equals(
                            finalPath,
                            Canonical(segment),
                            StringComparison.OrdinalIgnoreCase))
                        throw new FaceGeomHairRegionsPathSubstitutionException(
                            $"The {role} parent handle resolved as '{finalPath}' instead of '{segment}'.");
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            return new FaceGeomHairRegionsPinnedDirectoryChain(
                handles);
        }
        catch
        {
            for (int index = handles.Count - 1;
                 index >= 0;
                 index--)
                handles[index].Dispose();
            throw;
        }
    }

    private FaceGeomHairRegionsWindowsFileIdentity
        ValidateOrdinaryFile(
            SafeFileHandle handle,
            string admitted,
            string role)
    {
        FaceGeomHairRegionsWindowsFileIdentity identity =
            FaceGeomHairRegionsWindowsHandleApi
                .ReadIdentity(handle);
        if ((identity.Attributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} handle is not an ordinary file.");
        string finalPath = ResolveFinalPath(
            handle,
            admitted);
        if (!string.Equals(
                finalPath,
                admitted,
                StringComparison.OrdinalIgnoreCase))
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} handle resolved as '{finalPath}' instead of '{admitted}'.");
        return identity;
    }

    private FaceGeomHairRegionsWindowsFileIdentity
        ValidateOrdinaryDirectory(
            SafeFileHandle handle,
            string admitted,
            string role)
    {
        FaceGeomHairRegionsWindowsFileIdentity identity =
            FaceGeomHairRegionsWindowsHandleApi
                .ReadIdentity(handle);
        if ((identity.Attributes &
             FileAttributes.Directory) == 0 ||
            (identity.Attributes &
             (FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} handle is not an ordinary directory.");
        string finalPath = ResolveFinalPath(
            handle,
            admitted);
        if (!string.Equals(
                finalPath,
                admitted,
                StringComparison.OrdinalIgnoreCase))
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} handle resolved as '{finalPath}' instead of '{admitted}'.");
        return identity;
    }

    internal void ValidateSameDirectoryHandle(
        SafeFileHandle handle,
        FaceGeomHairRegionsWindowsFileIdentity expected,
        string admitted,
        string role)
    {
        FaceGeomHairRegionsWindowsFileIdentity actual =
            ValidateOrdinaryDirectory(
                handle,
                admitted,
                role);
        if (actual.VolumeSerialNumber !=
                expected.VolumeSerialNumber ||
            actual.FileId != expected.FileId)
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} reopened a different directory identity.");
    }

    internal void ValidateOwnedDirectoryPromotion(
            SafeFileHandle handle,
            FaceGeomHairRegionsWindowsFileIdentity expected,
            string source,
            WorkspacePath destination,
            string role)
    {
        string admittedSource = Canonical(source);
        string admittedDestination =
            Canonical(destination.Value);
        RequireUnderWorkspace(
            admittedDestination,
            role);
        if (!string.Equals(
                Path.GetDirectoryName(admittedSource),
                Path.GetDirectoryName(admittedDestination),
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"The {role} directory promotion must remain beneath the same pinned parent.");
        ValidateSameDirectoryHandle(
            handle,
            expected,
            admittedSource,
            role);
        BeforeRename(
            admittedSource,
            admittedDestination);
    }

    internal static void RenameOwnedDirectoryNoOverwrite(
        SafeFileHandle handle,
        SafeFileHandle destinationParent,
        WorkspacePath destination)
    {
        string admittedDestination =
            Canonical(destination.Value);
        FaceGeomHairRegionsWindowsHandleApi
            .RenameNoOverwrite(
                handle,
                destinationParent,
                Path.GetFileName(
                    admittedDestination));
    }

    internal FaceGeomHairRegionsWindowsFileIdentity
        ValidateRetainedOwnedDirectory(
            SafeFileHandle handle,
            string admitted,
            string role) =>
        ValidateOrdinaryDirectory(
            handle,
            admitted,
            role);

    internal FaceGeomHairRegionsPinnedReadFile
        OpenReadBeneath(
            SafeFileHandle rootHandle,
            string rootPath,
            WorkspacePath path,
            string role)
    {
        string admitted = Canonical(path.Value);
        RequireUnderWorkspace(admitted, role);
        ValidateOrdinaryDirectory(
            rootHandle,
            rootPath,
            $"{role} root");
        string relative =
            Path.GetRelativePath(rootPath, admitted);
        if (relative is "." or ".." ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException(
                $"The {role} escaped its pinned root.");
        string[] segments = relative.Split(
            [
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidDataException(
                $"The {role} must name a file beneath its pinned root.");
        var handles = new List<SafeFileHandle>();
        try
        {
            SafeFileHandle parent = rootHandle;
            string current = rootPath;
            for (int index = 0;
                 index < segments.Length - 1;
                 index++)
            {
                current = Path.Combine(
                    current,
                    segments[index]);
                SafeFileHandle directory =
                    FaceGeomHairRegionsWindowsHandleApi
                        .OpenExistingDirectoryRelative(
                            parent,
                            segments[index]);
                ValidateOrdinaryDirectory(
                    directory,
                    current,
                    role);
                handles.Add(directory);
                parent = directory;
            }
            SafeFileHandle file =
                FaceGeomHairRegionsWindowsHandleApi
                    .OpenExistingFileRelative(
                        parent,
                        segments[^1]);
            try
            {
                FaceGeomHairRegionsWindowsFileIdentity identity =
                    ValidateOrdinaryFile(
                        file,
                        admitted,
                        role);
                var stream = new FileStream(
                    file,
                    FileAccess.Read,
                    64 * 1024,
                    isAsync: true);
                return new FaceGeomHairRegionsPinnedReadFile(
                    this,
                    admitted,
                    identity,
                    stream,
                    new FaceGeomHairRegionsPinnedDirectoryChain(
                        handles));
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch
        {
            for (int index = handles.Count - 1;
                 index >= 0;
                 index--)
                handles[index].Dispose();
            throw;
        }
    }

    internal void DeleteOwnedFileBeneath(
        SafeFileHandle rootHandle,
        string rootPath,
        WorkspacePath path,
        string role)
    {
        string admitted = Canonical(path.Value);
        string admittedRoot = Canonical(rootPath);
        RequireUnderWorkspace(admitted, role);
        ValidateOrdinaryDirectory(
            rootHandle,
            admittedRoot,
            $"{role} root");
        if (!string.Equals(
                Path.GetDirectoryName(admitted),
                admittedRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"The {role} cleanup target must be a direct child of its pinned directory.");

        hooks.BeforeCleanup(admitted);
        using SafeFileHandle file =
            FaceGeomHairRegionsWindowsHandleApi
                .OpenOwnedFileRelative(
                    rootHandle,
                    Path.GetFileName(admitted));
        ValidateOrdinaryFile(
            file,
            admitted,
            $"{role} cleanup file");
        FaceGeomHairRegionsWindowsHandleApi.DeleteExactHandle(file);
    }

    internal ImmutableArray<
        FaceGeomHairRegionsPinnedTreeEntry>
        EnumerateTree(
            SafeFileHandle rootHandle,
            string rootPath,
            string role)
    {
        var entries = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsPinnedTreeEntry>();
        EnumerateDirectory(
            rootHandle,
            rootPath,
            role,
            entries,
            depth: 0);
        return entries.ToImmutable();
    }

    private void EnumerateDirectory(
        SafeFileHandle directory,
        string directoryPath,
        string role,
        ImmutableArray<
            FaceGeomHairRegionsPinnedTreeEntry>.Builder entries,
        int depth)
    {
        if (depth > 16)
            throw new InvalidDataException(
                "The preview output tree exceeds its admitted depth.");
        ValidateOrdinaryDirectory(
            directory,
            directoryPath,
            role);
        foreach (FaceGeomHairRegionsWindowsDirectoryEntry item in
                 FaceGeomHairRegionsWindowsHandleApi
                     .EnumerateDirectory(directory))
        {
            if (entries.Count >= 512)
                throw new InvalidDataException(
                    "The preview output tree exceeds its admitted entry count.");
            if ((item.Attributes &
                 (FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0)
                throw new FaceGeomHairRegionsPathSubstitutionException(
                    "The preview output tree contains a reparse or device entry.");
            string path = Path.Combine(
                directoryPath,
                item.Name);
            bool isDirectory =
                (item.Attributes &
                 FileAttributes.Directory) != 0;
            entries.Add(
                new FaceGeomHairRegionsPinnedTreeEntry(
                    new WorkspacePath(path),
                    isDirectory));
            if (isDirectory)
            {
                using SafeFileHandle child =
                    FaceGeomHairRegionsWindowsHandleApi
                        .OpenExistingDirectoryRelative(
                            directory,
                            item.Name);
                EnumerateDirectory(
                    child,
                    path,
                    role,
                    entries,
                    depth + 1);
            }
            else
            {
                using SafeFileHandle file =
                    FaceGeomHairRegionsWindowsHandleApi
                        .OpenExistingFileRelative(
                            directory,
                            item.Name);
                ValidateOrdinaryFile(
                    file,
                    path,
                    role);
            }
        }
    }

    internal void ValidateSameOwnedHandle(
        SafeFileHandle handle,
        FaceGeomHairRegionsWindowsFileIdentity expected,
        string admitted,
        string role)
    {
        FaceGeomHairRegionsWindowsFileIdentity actual =
            ValidateOrdinaryFile(
                handle,
                admitted,
                role);
        if (actual.VolumeSerialNumber !=
                expected.VolumeSerialNumber ||
            actual.FileId != expected.FileId)
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} reopened a different filesystem identity.");
    }

    internal FaceGeomHairRegionsWindowsFileIdentity
        ValidateRetainedOwnedHandle(
            SafeFileHandle handle,
            string admitted,
            string role) =>
        ValidateOrdinaryFile(
            handle,
            admitted,
            role);

    internal void BeforeRename(
        string source,
        string destination) =>
        hooks.BeforeRename(source, destination);

    internal void BeforeCleanup(string path) =>
        hooks.BeforeCleanup(path);

    internal void AfterOwnedReadback(
        string path,
        byte[] observed) =>
        hooks.AfterOwnedReadback(path, observed);

    internal void AfterPinnedReadback(
        string path,
        byte[] observed) =>
        hooks.AfterPinnedReadback(path, observed);

    private string ResolveFinalPath(
        SafeFileHandle handle,
        string admitted)
    {
        string actual =
            FaceGeomHairRegionsWindowsHandleApi
                .GetFinalDosPath(handle);
        string resolved = Canonical(
            hooks.ResolveFinalPath(
                admitted,
                actual));
        RequireUnderWorkspace(
            resolved,
            "opened filesystem handle");
        return resolved;
    }

    private void RequireUnderWorkspace(
        string path,
        string role)
    {
        if (!IsSameOrUnder(path, workspaceRoot))
            throw new FaceGeomHairRegionsPathSubstitutionException(
                $"The {role} resolved outside the exact K-local workspace.");
    }

    private static IEnumerable<string> ExpandPath(
        string root,
        string descendant)
    {
        yield return root;
        if (string.Equals(
                root,
                descendant,
                StringComparison.OrdinalIgnoreCase))
            yield break;
        string relative =
            Path.GetRelativePath(root, descendant);
        string current = root;
        foreach (string segment in relative.Split(
                     [
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     ],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static bool IsSameOrUnder(
        string path,
        string root)
        => new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    internal static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
}
