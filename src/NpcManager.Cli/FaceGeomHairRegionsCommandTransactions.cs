using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed partial class FaceGeomHairRegionsCommandHandler
{
    private const long MaximumMetadataBytes =
        128L * 1024L * 1024L;

    private async ValueTask WritePairNoOverwriteAsync(
        WorkspacePath firstPath,
        ImmutableArray<byte> firstBytes,
        WorkspacePath secondPath,
        ImmutableArray<byte> secondBytes,
        CancellationToken cancellationToken)
    {
        string firstTemporary = TemporaryBeside(firstPath.Value);
        string secondTemporary = TemporaryBeside(secondPath.Value);
        FaceGeomHairRegionsOwnedFile? firstOwned = null;
        FaceGeomHairRegionsOwnedFile? secondOwned = null;
        try
        {
            firstOwned = boundary.CreateOwnedFile(
                new WorkspacePath(firstTemporary),
                firstPath,
                "analysis");
            await WriteAndVerifyAsync(
                firstOwned,
                firstBytes,
                cancellationToken);
            secondOwned = boundary.CreateOwnedFile(
                new WorkspacePath(secondTemporary),
                secondPath,
                "assignment-template");
            await WriteAndVerifyAsync(
                secondOwned,
                secondBytes,
                cancellationToken);
            firstOwned.PromoteNoOverwrite();
            secondOwned.PromoteNoOverwrite();
            firstOwned.Dispose();
            firstOwned = null;
            secondOwned.Dispose();
            secondOwned = null;
        }
        catch (Exception exception)
        {
            var survivors =
                ImmutableArray.CreateBuilder<WorkspacePath>();
            var failures = new List<string>();
            CleanupOwnedHandle(
                firstOwned,
                survivors,
                failures);
            CleanupOwnedHandle(
                secondOwned,
                survivors,
                failures);
            string suffix = failures.Count == 0
                ? string.Empty
                : " Cleanup failures: " +
                  string.Join(" | ", failures);
            if (exception is OperationCanceledException)
                throw new FaceGeomHairRegionsOperationCanceledException(
                    "The FaceGeom metadata transaction was canceled." +
                    suffix,
                    survivors.ToImmutable(),
                    exception);
            throw new FaceGeomHairRegionsCommandFileException(
                exception.Message + suffix,
                survivors.ToImmutable(),
                exception);
        }
    }

    private async ValueTask WriteOneNoOverwriteAsync(
        WorkspacePath path,
        ImmutableArray<byte> bytes,
        CancellationToken cancellationToken)
    {
        string temporary = TemporaryBeside(path.Value);
        FaceGeomHairRegionsOwnedFile? owned = null;
        try
        {
            owned = boundary.CreateOwnedFile(
                new WorkspacePath(temporary),
                path,
                "proposal");
            await WriteAndVerifyAsync(
                owned,
                bytes,
                cancellationToken);
            owned.PromoteNoOverwrite();
            owned.Dispose();
            owned = null;
        }
        catch (Exception exception)
        {
            var survivors =
                ImmutableArray.CreateBuilder<WorkspacePath>();
            var failures = new List<string>();
            CleanupOwnedHandle(
                owned,
                survivors,
                failures);
            string suffix = failures.Count == 0
                ? string.Empty
                : " Cleanup failures: " +
                  string.Join(" | ", failures);
            if (exception is OperationCanceledException)
                throw new FaceGeomHairRegionsOperationCanceledException(
                    "The FaceGeom proposal transaction was canceled." +
                    suffix,
                    survivors.ToImmutable(),
                    exception);
            throw new FaceGeomHairRegionsCommandFileException(
                exception.Message + suffix,
                survivors.ToImmutable(),
                exception);
        }
    }

    private static async ValueTask WriteAndVerifyAsync(
        FaceGeomHairRegionsOwnedFile owned,
        ImmutableArray<byte> bytes,
        CancellationToken cancellationToken)
    {
        byte[] observed = await owned.WriteAndReadbackAsync(
            bytes.AsMemory(),
            MaximumMetadataBytes,
            cancellationToken);
        if (!observed.AsSpan().SequenceEqual(bytes.AsSpan()))
            throw new IOException(
                "The retained-handle metadata readback did not match the exact bytes written.");
    }

    private static string TemporaryBeside(string path) =>
        Path.Combine(
            Path.GetDirectoryName(path) ??
            throw new InvalidDataException(
                "Output path has no parent."),
            $".{Path.GetFileName(path)}." +
            $"{Guid.NewGuid():N}.npcmanager.tmp");

    private static void CleanupOwnedHandle(
        FaceGeomHairRegionsOwnedFile? owned,
        ImmutableArray<WorkspacePath>.Builder survivors,
        List<string> failures)
    {
        if (owned is null)
            return;
        string path = owned.Path;
        if (!owned.TryDelete(out string? failure))
        {
            survivors.Add(new WorkspacePath(path));
            failures.Add(
                $"{path}: {failure ?? "Identity-bound cleanup failed and the exact retained artifact was left untouched."}");
        }
    }
}

internal sealed class FaceGeomHairRegionsCommandFileException :
    IOException
{
    public FaceGeomHairRegionsCommandFileException(
        string message,
        ImmutableArray<WorkspacePath> survivingArtifacts,
        Exception innerException)
        : base(message, innerException)
    {
        SurvivingArtifacts = survivingArtifacts;
    }

    public ImmutableArray<WorkspacePath> SurvivingArtifacts
    {
        get;
    }
}

internal sealed class
    FaceGeomHairRegionsPreviewOperationalException :
    IOException
{
    public FaceGeomHairRegionsPreviewOperationalException(
        string message,
        ImmutableArray<WorkspacePath> survivingArtifacts,
        Exception innerException,
        ImmutableArray<int> survivingProcessIds = default,
        string? processTerminationFailure = null)
        : base(message, innerException)
    {
        SurvivingArtifacts = survivingArtifacts;
        SurvivingProcessIds =
            survivingProcessIds.IsDefault
                ? []
                : survivingProcessIds;
        ProcessTerminationFailure =
            processTerminationFailure;
    }

    public ImmutableArray<WorkspacePath> SurvivingArtifacts
    {
        get;
    }

    public ImmutableArray<int> SurvivingProcessIds
    {
        get;
    }

    public string? ProcessTerminationFailure
    {
        get;
    }
}
