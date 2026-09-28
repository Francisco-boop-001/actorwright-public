namespace NpcManager.Infrastructure;

internal readonly record struct FileArtifactCleanupResult(
    bool Succeeded,
    bool ArtifactExisted,
    string? ErrorMessage);

internal interface IFileArtifactCleanup
{
    FileArtifactCleanupResult DeleteIfPresent(string path);
}

internal sealed class FileArtifactCleanup : IFileArtifactCleanup
{
    public FileArtifactCleanupResult DeleteIfPresent(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new FileArtifactCleanupResult(true, false, null);

            File.Delete(path);
            return new FileArtifactCleanupResult(true, true, null);
        }
        catch (IOException exception)
        {
            return new FileArtifactCleanupResult(false, true, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new FileArtifactCleanupResult(false, true, exception.Message);
        }
    }
}
