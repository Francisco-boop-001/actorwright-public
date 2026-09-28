using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private static async ValueTask<WorkspacePath?> MaterializeProductProviderCarrierAsync(
        ProviderResourceAuthoritySet resources,
        WorkspacePath stagingRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (resources.FaceGeomCarrier is not
                    ApplicationProviderResourceAuthority carrier ||
                carrier.IsDirectory)
                throw new InvalidDataException(
                    "The product FaceGeom carrier is not one application-owned file.");
            var destination = new WorkspacePath(Path.Combine(
                stagingRoot.Value, "product-provider-inputs", "facegeom-carrier.nif"));
            string parent = Path.GetDirectoryName(destination.Value)!;
            Directory.CreateDirectory(parent);
            if (!destination.IsUnder(stagingRoot) || File.Exists(destination.Value) ||
                Directory.Exists(destination.Value) ||
                File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The staged product FaceGeom carrier destination is unsafe.");

            File.Copy(carrier.Path.Value, destination.Value, overwrite: false);
            await using var stream = new FileStream(destination.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
            if (actual != carrier.ExpectedSha256)
                throw new InvalidDataException(
                    "The product FaceGeom carrier changed during staging.");
            return destination;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "racemenu-build-product-provider-carrier",
                DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
    }
}
