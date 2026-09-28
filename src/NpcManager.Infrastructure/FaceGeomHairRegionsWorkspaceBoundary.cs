using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// One filesystem boundary for every FaceGeom hair-region input, output,
/// temporary, and preview artifact. Lexical containment alone is never
/// sufficient: every existing ancestor through the configured workspace root
/// is inspected immediately before filesystem access.
/// </summary>
public sealed class FaceGeomHairRegionsWorkspaceBoundary
{
    private readonly WorkspacePath workspaceRoot;
    private readonly IFaceGeomHairRegionsPathInspector inspector;
    private readonly FaceGeomHairRegionsPinnedFileSystem
        pinnedFileSystem;

    public FaceGeomHairRegionsWorkspaceBoundary(
        WorkspacePath workspaceRoot)
        : this(
            workspaceRoot,
            new FaceGeomHairRegionsPathInspector())
    {
    }

    internal FaceGeomHairRegionsWorkspaceBoundary(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsPathInspector inspector)
    {
        this.workspaceRoot = workspaceRoot;
        this.inspector = inspector ??
            throw new ArgumentNullException(nameof(inspector));
        pinnedFileSystem =
            new FaceGeomHairRegionsPinnedFileSystem(
                workspaceRoot);
        RequireSyntax(workspaceRoot, "workspace root");
        RequireNoReparseAncestry(
            workspaceRoot.Value,
            "workspace root");
    }

    internal FaceGeomHairRegionsWorkspaceBoundary(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsPinnedFileSystemHooks hooks)
    {
        this.workspaceRoot = workspaceRoot;
        inspector =
            new FaceGeomHairRegionsPathInspector();
        pinnedFileSystem =
            new FaceGeomHairRegionsPinnedFileSystem(
                workspaceRoot,
                hooks);
        RequireSyntax(workspaceRoot, "workspace root");
        RequireNoReparseAncestry(
            workspaceRoot.Value,
            "workspace root");
    }

    public WorkspacePath WorkspaceRoot => workspaceRoot;

    internal async ValueTask<byte[]> ReadExactFileAsync(
        WorkspacePath path,
        long maximumBytes,
        string role,
        CancellationToken cancellationToken)
    {
        RequireSyntax(path, role);
        await using FaceGeomHairRegionsPinnedReadFile lease =
            pinnedFileSystem.OpenRead(path, role);
        return await lease.ReadExactAsync(
            maximumBytes,
            cancellationToken);
    }

    internal byte[] ReadExactFile(
        WorkspacePath path,
        long maximumBytes,
        string role)
    {
        RequireSyntax(path, role);
        using FaceGeomHairRegionsPinnedReadFile lease =
            pinnedFileSystem.OpenRead(path, role);
        return lease.ReadExact(maximumBytes);
    }

    internal FaceGeomHairRegionsOwnedFile CreateOwnedFile(
        WorkspacePath temporaryPath,
        WorkspacePath destinationPath,
        string role)
    {
        RequireSyntax(temporaryPath, $"{role} temporary");
        RequireSyntax(destinationPath, $"{role} destination");
        return pinnedFileSystem.CreateOwned(
            temporaryPath,
            destinationPath,
            role);
    }

    internal FaceGeomHairRegionsPinnedDirectory
        CreateOwnedDirectory(
            WorkspacePath path,
            string role)
    {
        RequireSyntax(path, role);
        return pinnedFileSystem.CreateOwnedDirectory(
            path,
            role);
    }

    public void RequireExistingFile(
        WorkspacePath path,
        string role)
    {
        RequireSyntax(path, role);
        RequireNoReparseAncestry(path.Value, role);
        if (!inspector.FileExists(path.Value) ||
            inspector.DirectoryExists(path.Value))
            throw Invalid(
                $"The {role} must be an existing ordinary file.");
        FileAttributes attributes =
            inspector.GetAttributes(path.Value);
        if ((attributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw Invalid(
                $"The {role} must be an ordinary non-reparse file.");
    }

    public void RequireExistingDirectory(
        WorkspacePath path,
        string role)
    {
        RequireSyntax(path, role);
        RequireNoReparseAncestry(path.Value, role);
        if (!inspector.DirectoryExists(path.Value) ||
            inspector.FileExists(path.Value))
            throw Invalid(
                $"The {role} must be an existing ordinary directory.");
        FileAttributes attributes =
            inspector.GetAttributes(path.Value);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes &
             (FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw Invalid(
                $"The {role} must be an ordinary non-reparse directory.");
    }

    public void RequireNewFile(
        WorkspacePath path,
        string role)
    {
        RequireNewPath(path, role);
        string parent = Path.GetDirectoryName(path.Value) ??
            throw Invalid($"The {role} has no parent directory.");
        RequireExistingDirectory(
            new WorkspacePath(parent),
            $"{role} parent");
    }

    public void RequireNewDirectory(
        WorkspacePath path,
        string role)
    {
        RequireNewPath(path, role);
        string parent = Path.GetDirectoryName(path.Value) ??
            throw Invalid($"The {role} has no parent directory.");
        RequireExistingDirectory(
            new WorkspacePath(parent),
            $"{role} parent");
    }

    public void RequireExistingFileBeneath(
        WorkspacePath ancestor,
        WorkspacePath path,
        string role)
    {
        RequireExistingDirectory(ancestor, "artifact root");
        if (path == ancestor ||
            !path.IsUnder(ancestor))
            throw new UnauthorizedAccessException(
                $"The {role} must remain beneath the exact artifact root.");
        RequireExistingFile(path, role);
    }

    private void RequireNewPath(
        WorkspacePath path,
        string role)
    {
        RequireSyntax(path, role);
        RequireNoReparseAncestry(path.Value, role);
        if (inspector.FileExists(path.Value) ||
            inspector.DirectoryExists(path.Value))
            throw new IOException(
                $"The {role} target must not already exist.");
    }

    private void RequireSyntax(
        WorkspacePath path,
        string role)
    {
        string value = path.Value;
        if (!string.Equals(
                Path.GetPathRoot(workspaceRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase) ||
            !path.IsUnder(workspaceRoot))
            throw new UnauthorizedAccessException(
                $"The {role} must remain under the K-local workspace.");
        if (value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                $"The {role} may not use UNC or device syntax.");
        if (value.IndexOf(':', 2) >= 0)
            throw new UnauthorizedAccessException(
                $"The {role} may not use an alternate data stream.");
    }

    private void RequireNoReparseAncestry(
        string path,
        string role)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((inspector.FileExists(current) ||
                 inspector.DirectoryExists(current)) &&
                inspector.GetAttributes(current)
                    .HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException(
                    $"The {role} traverses reparse point '{current}'.");
            if (string.Equals(
                    current,
                    workspaceRoot.Value,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null ||
                string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"The {role} ancestry escaped the configured workspace.");
            current = parent;
        }
    }

    private static InvalidDataException Invalid(string message) =>
        new(message);
}

internal interface IFaceGeomHairRegionsPathInspector
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    FileAttributes GetAttributes(string path);
}

internal sealed class FaceGeomHairRegionsPathInspector :
    IFaceGeomHairRegionsPathInspector
{
    public bool FileExists(string path) =>
        File.Exists(path);

    public bool DirectoryExists(string path) =>
        Directory.Exists(path);

    public FileAttributes GetAttributes(string path) =>
        File.GetAttributes(path);
}
