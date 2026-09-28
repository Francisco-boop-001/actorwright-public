using System.Security;

namespace NpcManager.Architecture.Tests;

internal static class PhysicalReparseFixture
{
    private const string RequireNtfsFixturesVariable =
        "ACTORWRIGHT_REQUIRE_NTFS_FIXTURES";

    internal static bool TryCreateDirectoryLink(
        string link,
        string target,
        string fixtureRoot) =>
        TryCreateLink(
            "directory",
            link,
            target,
            fixtureRoot,
            () => Directory.CreateSymbolicLink(link, target));

    internal static bool TryCreateFileLink(
        string link,
        string target,
        string fixtureRoot) =>
        TryCreateLink(
            "file",
            link,
            target,
            fixtureRoot,
            () => File.CreateSymbolicLink(link, target));

    private static bool TryCreateLink(
        string kind,
        string link,
        string target,
        string fixtureRoot,
        Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            string fullLinkPath = link;
            string volumeRoot = "unknown";
            string filesystem = "unknown";
            string? filesystemDetectionFailure = null;
            try
            {
                fullLinkPath = Path.GetFullPath(link);
                volumeRoot = Path.GetPathRoot(fullLinkPath) ?? "unknown";
                if (volumeRoot != "unknown")
                    filesystem = new DriveInfo(volumeRoot).DriveFormat;
                else
                    filesystemDetectionFailure = "link path has no volume root";
            }
            catch (Exception detectionException) when (
                detectionException is ArgumentException or IOException or
                    InvalidOperationException or NotSupportedException or
                    SecurityException or UnauthorizedAccessException)
            {
                filesystemDetectionFailure =
                    $"{detectionException.GetType().Name}: {detectionException.Message}";
            }

            bool knownFilesystem = !string.IsNullOrWhiteSpace(filesystem) &&
                !filesystem.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            bool strict = Environment.GetEnvironmentVariable(
                RequireNtfsFixturesVariable) == "1";
            string reason =
                $"{exception.GetType().Name}: {exception.Message}";
            string context =
                $"link={fullLinkPath}; target={target}; fixtureRoot={fixtureRoot}; " +
                $"volumeRoot={volumeRoot}; filesystem={filesystem}; " +
                $"filesystemDetectionFailure={filesystemDetectionFailure ?? "none"}; " +
                $"reason={reason}";

            if (!knownFilesystem || strict ||
                filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Required physical {kind} reparse fixture could not be created: {context}",
                    exception);

            Console.WriteLine(
                $"SKIP physical {kind} reparse fixture unavailable: {context}");
            return false;
        }
    }
}
