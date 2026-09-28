using System.Globalization;
using NpcManager.Domain;

namespace NpcManager.Rendering;

internal static class PreviewArtifactFileWriter
{
    internal static async Task WriteAsync(
        WorkspacePath destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = destination.Value + ".tmp-" +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination.Value, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
