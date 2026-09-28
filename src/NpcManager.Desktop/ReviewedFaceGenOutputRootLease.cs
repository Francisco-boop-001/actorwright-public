using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal sealed class ReviewedFaceGenOutputRootLease : IDisposable
{
    private readonly bool created;
    private bool preserve;
    private bool disposed;

    private ReviewedFaceGenOutputRootLease(WorkspacePath root, bool created)
    {
        Root = root;
        this.created = created;
    }

    internal WorkspacePath Root { get; }

    internal static bool TryCreate(
        ReviewedGameIntake intake,
        WorkspacePath requestedRoot,
        out ReviewedFaceGenOutputRootLease? lease,
        out string error)
    {
        lease = null;
        error = string.Empty;
        if (requestedRoot != intake.OutputRoot)
        {
            error = "The FaceGen output no longer matches the reviewed future output.";
            return false;
        }
        if (requestedRoot == intake.WorkspaceRoot ||
            !requestedRoot.IsUnder(intake.WorkspaceRoot))
        {
            error = "The FaceGen output must be a child of the reviewed K-local workspace.";
            return false;
        }
        if (Directory.Exists(requestedRoot.Value) || File.Exists(requestedRoot.Value))
        {
            error = "The reviewed FaceGen output must still be absent before the bake starts.";
            return false;
        }

        string? parent = Directory.GetParent(requestedRoot.Value)?.FullName;
        if (parent is null || !Directory.Exists(parent))
        {
            error = "The reviewed FaceGen output parent must already exist.";
            return false;
        }
        if (!HasSafeExistingAncestry(parent, intake.WorkspaceRoot.Value, out error))
            return false;

        try
        {
            Directory.CreateDirectory(requestedRoot.Value);
            if (!Directory.Exists(requestedRoot.Value) ||
                File.GetAttributes(requestedRoot.Value).HasFlag(FileAttributes.ReparsePoint) ||
                Directory.EnumerateFileSystemEntries(requestedRoot.Value).Any())
            {
                TryDeleteEmpty(requestedRoot.Value);
                error = "The FaceGen output could not be admitted as a new empty ordinary directory.";
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            TryDeleteEmpty(requestedRoot.Value);
            error = $"The reviewed FaceGen output could not be created: {exception.Message}";
            return false;
        }

        lease = new ReviewedFaceGenOutputRootLease(requestedRoot, created: true);
        return true;
    }

    internal void Preserve() => preserve = true;

    private static bool HasSafeExistingAncestry(
        string start,
        string workspaceRoot,
        out string error)
    {
        string current = Path.GetFullPath(start);
        string boundary = Path.GetFullPath(workspaceRoot);
        while (true)
        {
            try
            {
                if (!Directory.Exists(current) ||
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    error = "The reviewed FaceGen output ancestry must contain only existing ordinary directories.";
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                error = $"The reviewed FaceGen output ancestry could not be verified: {exception.Message}";
                return false;
            }

            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }
            string? next = Directory.GetParent(current)?.FullName;
            if (next is null || string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
            {
                error = "The reviewed FaceGen output ancestry escaped the workspace boundary.";
                return false;
            }
            current = next;
        }
    }

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            // Best-effort cleanup only; never recurse or remove unclaimed files.
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (created && !preserve) TryDeleteEmpty(Root.Value);
    }
}
