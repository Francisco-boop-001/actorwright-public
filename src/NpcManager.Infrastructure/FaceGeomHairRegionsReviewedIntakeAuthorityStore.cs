using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Persists the desktop's typed reviewed intake as one canonical, no-overwrite
/// JSON authority, reloads it through the strict codec, and retains the exact
/// file handle so the ephemeral document can be removed without path races.
/// </summary>
public sealed class
    FaceGeomHairRegionsReviewedIntakeAuthorityStore
{
    private const long MaximumDocumentBytes =
        128L * 1024L * 1024L;
    private readonly FaceGeomHairRegionsDocumentCodec documents;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsReviewedIntakeAuthorityStore(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsDocumentCodec documents)
    {
        this.documents = documents ??
            throw new ArgumentNullException(
                nameof(documents));
        boundary =
            this.documents.WorkspaceBoundary;
        if (boundary.WorkspaceRoot !=
            workspaceRoot)
        {
            throw new ArgumentException(
                "The reviewed-intake authority store and document codec must share one workspace root.",
                nameof(documents));
        }
    }

    public async ValueTask<
        FaceGeomHairRegionsReviewedIntakeAuthorityLease>
        CreateAndReloadAsync(
            ReviewedGameIntake intake,
            WorkspacePath ownedRoot,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intake);
        boundary.RequireExistingDirectory(
            ownedRoot,
            "desktop HairTint transaction root");
        string destinationText = Path.Combine(
            ownedRoot.Value,
            $"reviewed-intake-{Guid.NewGuid():N}.json");
        string temporaryText = Path.Combine(
            ownedRoot.Value,
            $".reviewed-intake-{Guid.NewGuid():N}.tmp");
        var destination =
            new WorkspacePath(destinationText);
        FaceGeomHairRegionsOwnedFile? owned = null;
        try
        {
            ReviewedGameIntakeDocumentAuthority bound =
                documents.BindReviewedIntake(
                    intake,
                    destination);
            if (bound.Document.ByteLength is <= 0 or
                > MaximumDocumentBytes)
            {
                throw new InvalidDataException(
                    "The canonical reviewed-intake JSON exceeds its admitted size.");
            }
            owned = boundary.CreateOwnedFile(
                new WorkspacePath(temporaryText),
                destination,
                "desktop reviewed-intake authority");
            byte[] readback =
                await owned.WriteAndReadbackAsync(
                    bound.Document.Utf8Json.AsMemory(),
                    MaximumDocumentBytes,
                    cancellationToken);
            if (!readback.AsSpan().SequenceEqual(
                    bound.Document.Utf8Json.AsSpan()))
            {
                throw new InvalidDataException(
                    "The retained reviewed-intake JSON readback differs from its canonical bytes.");
            }
            owned.PromoteNoOverwrite();
            ReviewedGameIntakeDocumentAuthority reloaded =
                documents.ParseReviewedIntake(
                    new ExactJsonFileAuthority(
                        destination,
                        readback.ToImmutableArray(),
                        readback.LongLength,
                        bound.Document.Sha256));
            if (reloaded.Document.Path !=
                    bound.Document.Path ||
                reloaded.Document.ByteLength !=
                    bound.Document.ByteLength ||
                reloaded.Document.Sha256 !=
                    bound.Document.Sha256 ||
                !reloaded.Document.Utf8Json.AsSpan()
                    .SequenceEqual(
                        bound.Document.Utf8Json.AsSpan()) ||
                reloaded.Value.IntakeFingerprint !=
                    bound.Value.IntakeFingerprint ||
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(reloaded.Value) !=
                    reloaded.Value.IntakeFingerprint)
            {
                throw new InvalidDataException(
                    "Reloaded reviewed-intake authority differs from its canonical typed and byte authority.");
            }
            var lease =
                new FaceGeomHairRegionsReviewedIntakeAuthorityLease(
                    reloaded,
                    owned);
            owned = null;
            return lease;
        }
        catch (Exception exception)
        {
            if (owned is not null &&
                !owned.TryDelete(
                    out string? cleanupFailure))
            {
                throw new FaceGeomHairRegionsOperationalException(
                    "Reviewed-intake authority creation failed and exact cleanup also failed: " +
                    cleanupFailure,
                    [new WorkspacePath(owned.Path)],
                    exception);
            }
            throw;
        }
    }
}

public sealed class
    FaceGeomHairRegionsReviewedIntakeAuthorityLease :
    IAsyncDisposable,
    IDisposable
{
    private FaceGeomHairRegionsOwnedFile? owned;

    internal FaceGeomHairRegionsReviewedIntakeAuthorityLease(
        ReviewedGameIntakeDocumentAuthority authority,
        FaceGeomHairRegionsOwnedFile owned)
    {
        Authority = authority ??
            throw new ArgumentNullException(
                nameof(authority));
        this.owned = owned ??
            throw new ArgumentNullException(
                nameof(owned));
    }

    public ReviewedGameIntakeDocumentAuthority Authority
    {
        get;
    }

    public void Dispose()
    {
        if (owned is null)
        {
            return;
        }
        string path = owned.Path;
        if (!owned.TryDelete(
                out string? failure))
        {
            owned = null;
            throw new FaceGeomHairRegionsOperationalException(
                "The ephemeral reviewed-intake authority could not be removed by its retained exact handle: " +
                failure,
                [new WorkspacePath(path)],
                new IOException(
                    failure ??
                    "Exact reviewed-intake authority cleanup failed."));
        }
        owned = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
