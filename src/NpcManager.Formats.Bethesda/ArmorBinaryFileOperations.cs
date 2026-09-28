using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal readonly record struct ArmorBinaryCleanupResult(
    bool Succeeded,
    bool ArtifactExisted,
    string? ErrorMessage);

internal interface IArmorBinaryFileOperations
{
    void CommitNoOverwrite(string temporary, string output);

    ValueTask<Sha256Hash> HashAsync(
        string path,
        CancellationToken cancellationToken);

    ArmorBinaryCleanupResult DeleteIfPresent(string path);
}

internal sealed class ArmorBinaryFileOperations : IArmorBinaryFileOperations
{
    public void CommitNoOverwrite(string temporary, string output) =>
        File.Move(temporary, output, overwrite: false);

    public async ValueTask<Sha256Hash> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    public ArmorBinaryCleanupResult DeleteIfPresent(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ArmorBinaryCleanupResult(true, false, null);

            File.Delete(path);
            return new ArmorBinaryCleanupResult(true, true, null);
        }
        catch (IOException exception)
        {
            return new ArmorBinaryCleanupResult(false, true, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new ArmorBinaryCleanupResult(false, true, exception.Message);
        }
    }
}
