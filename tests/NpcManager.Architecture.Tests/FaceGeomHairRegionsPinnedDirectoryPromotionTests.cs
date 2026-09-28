using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class FaceGeomHairRegionsPinnedDirectoryPromotionTests
{
    private static readonly string[] RepositoryMarkers =
    [
        Path.Combine(
            "src",
            "NpcManager.Application",
            "NpcManager.Application.csproj"),
        Path.Combine(
            "src",
            "NpcManager.Infrastructure",
            "NpcManager.Infrastructure.csproj"),
        Path.Combine(
            "tests",
            "NpcManager.Architecture.Tests",
            "NpcManager.Architecture.Tests.csproj")
    ];

    // Each row is tied to the production mutation it must reject:
    // (1) moving Path before BeforeRename would misreport a refusal;
    // (2) assigning Path only after retained validation loses the destination
    //     boundary on a one-shot post-rename failure;
    // (3) the same late assignment sends persistent cleanup to the stale
    //     private source;
    // (4) disposing a successful lease must not delete its promoted tree;
    // (5) a second native rename must not be permitted after commitment; and
    // (6) replacing the no-overwrite primitive would replace an existing empty
    //     destination and change its retained directory identity.

    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The pinned directory promotion test requires Windows.");

        string workingDirectory =
            FaceGeomHairRegionsPinnedFileSystem.Canonical(
                Directory.GetCurrentDirectory());
        string worktreeRoot = FindRepositoryRoot(workingDirectory);

        string testRoot = Path.Combine(
            worktreeRoot,
            "artifacts",
            "test-work",
            "owned-directory-promotion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            RunPreRenameRefusal(testRoot);
            RunOneShotPostRenameValidationFailure(testRoot);
            RunPersistentPostRenameValidationFailure(testRoot);
            RunSuccessSurvivesDispose(testRoot);
            RunDoublePromotion(testRoot);
            RunOccupiedEmptyDestination(testRoot);
        }
        finally
        {
            DeleteFixtureRoot(testRoot, worktreeRoot);
        }
    }

    private static void RunPreRenameRefusal(string testRoot)
    {
        string root = CreateScenarioRoot(testRoot, "pre-rename-refusal");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: null,
            invalidDestinationResolutions: 0,
            rejectBeforeRename: true,
            persistInvalidDestination: false);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        using FaceGeomHairRegionsPinnedDirectory lease =
            fileSystem.CreateOwnedDirectory(
                new WorkspacePath(source),
                "owned-directory promotion test");
        WriteSentinel(source, "pre-rename");

        AssertThrowsPromotionFailure(
            () => lease.PromoteNoOverwrite(
                new WorkspacePath(destination)));
        Assert(
            lease.Path == Canonical(source),
            "A pre-rename refusal must retain the private source path.");
        Assert(
            !ReadRenameCommitted(lease),
            "A pre-rename refusal must not mark the directory committed.");
        Assert(
            Directory.Exists(source) && !Directory.Exists(destination),
            "A pre-rename refusal changed the source or destination namespace.");
        Assert(
            lease.DeleteTree().IsEmpty,
            "Pre-rename rollback left a source survivor.");
        Assert(
            !Directory.Exists(source),
            "Pre-rename rollback did not remove the private source.");
    }

    private static void RunOneShotPostRenameValidationFailure(
        string testRoot)
    {
        string root = CreateScenarioRoot(
            testRoot,
            "one-shot-post-rename-validation");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: root,
            invalidDestinationResolutions: 1,
            rejectBeforeRename: false,
            persistInvalidDestination: false);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        using FaceGeomHairRegionsPinnedDirectory lease =
            fileSystem.CreateOwnedDirectory(
                new WorkspacePath(source),
                "owned-directory promotion test");
        WriteSentinel(source, "one-shot");

        AssertThrowsPromotionFailure(
            () => lease.PromoteNoOverwrite(
                new WorkspacePath(destination)));
        Assert(
            lease.Path == Canonical(destination),
            "A post-rename validation failure must expose the destination path.");
        Assert(
            ReadRenameCommitted(lease),
            "A post-rename validation failure must expose committed state.");
        Assert(
            !Directory.Exists(source) && Directory.Exists(destination),
            "The native rename boundary was not reflected in the namespace.");
        ImmutableArray<WorkspacePath> survivors = lease.DeleteTree();
        Assert(
            survivors.IsEmpty,
            "Exact destination rollback should remove the one-shot failure tree.");
        Assert(
            !Directory.Exists(source) && !Directory.Exists(destination),
            "One-shot post-rename rollback left a tree survivor.");
    }

    private static void RunPersistentPostRenameValidationFailure(
        string testRoot)
    {
        string root = CreateScenarioRoot(
            testRoot,
            "persistent-post-rename-validation");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        string foreignSentinel = Path.Combine(root, "foreign-sentinel.bin");
        byte[] foreignBytes = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E];
        File.WriteAllBytes(foreignSentinel, foreignBytes);
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: root,
            invalidDestinationResolutions: 0,
            rejectBeforeRename: false,
            persistInvalidDestination: true);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        using FaceGeomHairRegionsPinnedDirectory lease =
            fileSystem.CreateOwnedDirectory(
                new WorkspacePath(source),
                "owned-directory promotion test");
        WriteSentinel(source, "persistent");

        AssertThrowsPromotionFailure(
            () => lease.PromoteNoOverwrite(
                new WorkspacePath(destination)));
        Assert(
            lease.Path == Canonical(destination),
            "Persistent post-rename failure must retain the destination path.");
        Assert(
            ReadRenameCommitted(lease),
            "Persistent post-rename failure must retain committed state.");
        ImmutableArray<WorkspacePath> survivors = lease.DeleteTree();
        Assert(
            survivors.Length == 1 &&
            survivors[0] == new WorkspacePath(Canonical(destination)),
            "Persistent cleanup must report only the canonical destination survivor.");
        Assert(
            !Directory.Exists(source) && Directory.Exists(destination),
            "Persistent cleanup targeted the stale private source.");
        Assert(
            File.ReadAllBytes(foreignSentinel).AsSpan().SequenceEqual(
                foreignBytes),
            "Persistent cleanup touched the foreign sentinel path.");
        Assert(
            hooks.CleanupPaths.Contains(
                Canonical(destination),
                StringComparer.OrdinalIgnoreCase) &&
            !hooks.CleanupPaths.Contains(
                Canonical(source),
                StringComparer.OrdinalIgnoreCase),
            "Cleanup did not receive the committed destination path.");
    }

    private static void RunSuccessSurvivesDispose(string testRoot)
    {
        string root = CreateScenarioRoot(
            testRoot,
            "success-survives-dispose");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        string sentinelPath = Path.Combine(destination, "sentinel.bin");
        byte[] sentinel = [0x53, 0x55, 0x43, 0x43, 0x45, 0x53, 0x53];
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: null,
            invalidDestinationResolutions: 0,
            rejectBeforeRename: false,
            persistInvalidDestination: false);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        FaceGeomHairRegionsWindowsFileIdentity retainedIdentity;
        using (FaceGeomHairRegionsPinnedDirectory lease =
                   fileSystem.CreateOwnedDirectory(
                       new WorkspacePath(source),
                       "owned-directory promotion test"))
        {
            Directory.CreateDirectory(source);
            File.WriteAllBytes(
                Path.Combine(source, "sentinel.bin"),
                sentinel);
            lease.PromoteNoOverwrite(new WorkspacePath(destination));
            Assert(
                lease.Path == Canonical(destination) &&
                ReadRenameCommitted(lease),
                "Successful promotion did not expose destination commit state.");
            retainedIdentity =
                ReadRetainedDirectoryIdentity(lease);
            Assert(
                retainedIdentity.FileId != 0,
                "The retained promoted directory identity was empty.");
        }

        Assert(
            ReadDirectoryIdentity(destination) ==
            retainedIdentity,
            "The promoted directory identity changed after lease disposal.");

        using FaceGeomHairRegionsPinnedDirectory reopened =
            fileSystem.OpenOwnedDirectory(
                new WorkspacePath(destination),
                "owned-directory promotion reopen test");
        byte[] observed = reopened.ReadExactFileAsync(
                new WorkspacePath(sentinelPath),
                maximumBytes: 128,
                "owned-directory promotion sentinel",
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Assert(
            observed.AsSpan().SequenceEqual(sentinel),
            "The promoted destination lost its child sentinel after disposal.");
        reopened.Dispose();
        Assert(
            Directory.Exists(destination) && File.Exists(sentinelPath),
            "Disposing a successful lease removed the promoted destination.");
    }

    private static void RunDoublePromotion(string testRoot)
    {
        string root = CreateScenarioRoot(testRoot, "double-promotion");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        string secondDestination = Path.Combine(root, "published-again");
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: null,
            invalidDestinationResolutions: 0,
            rejectBeforeRename: false,
            persistInvalidDestination: false);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        using FaceGeomHairRegionsPinnedDirectory lease =
            fileSystem.CreateOwnedDirectory(
                new WorkspacePath(source),
                "owned-directory promotion test");
        WriteSentinel(source, "double");
        lease.PromoteNoOverwrite(new WorkspacePath(destination));
        string committedPath = lease.Path;
        int renameCount = hooks.RenameCount;

        AssertThrows<InvalidOperationException>(
            () => lease.PromoteNoOverwrite(
                new WorkspacePath(secondDestination)));
        Assert(
            lease.Path == committedPath && ReadRenameCommitted(lease),
            "A rejected second promotion changed committed directory state.");
        Assert(
            hooks.RenameCount == renameCount &&
            Directory.Exists(destination) &&
            !Directory.Exists(source) &&
            !Directory.Exists(secondDestination),
            "A rejected second promotion attempted another native rename.");
        Assert(
            lease.DeleteTree().IsEmpty,
            "Double-promotion cleanup left a destination survivor.");
    }

    private static void RunOccupiedEmptyDestination(string testRoot)
    {
        string root = CreateScenarioRoot(
            testRoot,
            "occupied-empty-destination");
        string source = Path.Combine(root, "private");
        string destination = Path.Combine(root, "published");
        Directory.CreateDirectory(destination);
        FaceGeomHairRegionsWindowsFileIdentity beforeIdentity =
            ReadDirectoryIdentity(destination);
        var hooks = new PromotionHooks(
            destination,
            invalidDestination: null,
            invalidDestinationResolutions: 0,
            rejectBeforeRename: false,
            persistInvalidDestination: false);
        var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            new WorkspacePath(root),
            hooks);
        using FaceGeomHairRegionsPinnedDirectory lease =
            fileSystem.CreateOwnedDirectory(
                new WorkspacePath(source),
                "owned-directory promotion test");
        WriteSentinel(source, "occupied-source");

        AssertThrowsPromotionFailure(
            () => lease.PromoteNoOverwrite(
                new WorkspacePath(destination)));
        Assert(
            lease.Path == Canonical(source) &&
            !ReadRenameCommitted(lease),
            "An empty occupied destination changed the source lease commit state.");
        Assert(
            Directory.Exists(source) && Directory.Exists(destination),
            "An empty occupied destination changed source/destination existence.");
        Assert(
            lease.DeleteTree().IsEmpty,
            "Empty occupied-destination rollback left a private source survivor.");
        Assert(
            !Directory.EnumerateFileSystemEntries(destination).Any() &&
            ReadDirectoryIdentity(destination) == beforeIdentity,
            "The empty occupied destination was replaced, populated, or mutated.");
    }

    private static string FindRepositoryRoot(string workingDirectory)
    {
        DirectoryInfo? candidate = new(workingDirectory);
        while (candidate is not null)
        {
            if (RepositoryMarkers.All(
                    marker => File.Exists(
                        Path.Combine(candidate.FullName, marker))))
                return Canonical(candidate.FullName);
            candidate = candidate.Parent;
        }

        throw new InvalidOperationException(
            "The focused test working directory is not beneath a valid " +
            "Actorwright checkout with the required repository markers.");
    }

    private static string CreateScenarioRoot(
        string testRoot,
        string name)
    {
        string root = Path.Combine(testRoot, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteSentinel(string directory, string value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "sentinel.bin"),
            value);
    }

    private static FaceGeomHairRegionsWindowsFileIdentity
        ReadDirectoryIdentity(string path)
    {
        using SafeFileHandle handle =
            FaceGeomHairRegionsWindowsHandleApi
                .OpenExistingDirectory(path);
        return FaceGeomHairRegionsWindowsHandleApi.ReadIdentity(handle);
    }

    private static bool ReadRenameCommitted(
        FaceGeomHairRegionsPinnedDirectory lease)
    {
        // The optional reflection keeps the RED buildable against the base
        // API. The production repair supplies this internal property; absent
        // means the pre-repair lease is necessarily uncommitted.
        System.Reflection.PropertyInfo? property =
            typeof(FaceGeomHairRegionsPinnedDirectory).GetProperty(
                "RenameCommitted",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        return property?.GetValue(lease) as bool? ?? false;
    }

    private static FaceGeomHairRegionsWindowsFileIdentity
        ReadRetainedDirectoryIdentity(
            FaceGeomHairRegionsPinnedDirectory lease)
    {
        System.Reflection.FieldInfo? field =
            typeof(FaceGeomHairRegionsPinnedDirectory).GetField(
                "identity",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(lease) is
            FaceGeomHairRegionsWindowsFileIdentity identity)
            return identity;
        throw new InvalidOperationException(
            "The directory lease did not expose its retained identity.");
    }

    private static void AssertThrowsPromotionFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Expected the directory promotion to fail with an I/O or authorization error.");
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }

    private static string Canonical(string path) =>
        FaceGeomHairRegionsPinnedFileSystem.Canonical(path);

    private static void DeleteFixtureRoot(
        string testRoot,
        string worktreeRoot)
    {
        string canonicalRoot = Canonical(testRoot);
        string canonicalWorktree = Canonical(worktreeRoot);
        string allowedParent = Path.Combine(
            canonicalWorktree,
            "artifacts",
            "test-work");
        Assert(
            !string.Equals(canonicalRoot, canonicalWorktree) &&
            IsUnder(canonicalRoot, allowedParent),
            "Refusing to recursively clean a test root outside the exact worktree fixture parent.");
        if (Directory.Exists(canonicalRoot))
        {
            FileAttributes attributes =
                File.GetAttributes(canonicalRoot);
            Assert(
                (attributes & FileAttributes.ReparsePoint) == 0,
                "Refusing to recursively clean a reparse-point test root.");
            Directory.Delete(canonicalRoot, recursive: true);
        }
    }

    private static bool IsUnder(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class PromotionHooks(
        string destination,
        string? invalidDestination,
        int invalidDestinationResolutions,
        bool rejectBeforeRename,
        bool persistInvalidDestination) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private int invalidResolutions = invalidDestinationResolutions;

        public int RenameCount
        {
            get;
            private set;
        }

        public List<string> CleanupPaths { get; } = [];

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath)
        {
            if (invalidDestination is not null &&
                string.Equals(
                    admittedPath,
                    destination,
                    StringComparison.OrdinalIgnoreCase) &&
                (persistInvalidDestination || invalidResolutions > 0))
            {
                if (!persistInvalidDestination)
                    invalidResolutions--;
                return invalidDestination;
            }
            return actualFinalPath;
        }

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            RenameCount++;
            if (rejectBeforeRename)
                throw new IOException("Synthetic pre-rename refusal.");
        }

        public void BeforeCleanup(string admittedPath) =>
            CleanupPaths.Add(admittedPath);
    }
}
