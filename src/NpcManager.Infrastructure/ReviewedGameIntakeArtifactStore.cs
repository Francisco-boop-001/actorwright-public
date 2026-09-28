using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Publishes one canonical reviewed-intake document through the existing
/// retained-handle, no-follow, no-overwrite transaction.
/// </summary>
public sealed class ReviewedGameIntakeArtifactStore
{
    private const long MaximumDocumentBytes = 128L * 1024L * 1024L;
    private readonly FaceGeomHairRegionsDocumentCodec documents;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public ReviewedGameIntakeArtifactStore(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsDocumentCodec documents)
    {
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
        boundary = documents.WorkspaceBoundary;
        if (boundary.WorkspaceRoot != workspaceRoot)
            throw new ArgumentException(
                "The reviewed-intake artifact store and codec must share one exact workspace root.",
                nameof(documents));
    }

    public async ValueTask<ReviewedGameIntakeDocumentAuthority> WriteNewAsync(
        ReviewedGameIntake intake,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ValidateDestination(intake, destination);

        string parent = Path.GetDirectoryName(destination.Value) ??
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The reviewed-intake output has no parent directory.");
        var temporary = new WorkspacePath(Path.Combine(
            parent,
            $".reviewed-intake-{Guid.NewGuid():N}.tmp"));
        FaceGeomHairRegionsOwnedFile? owned = null;
        try
        {
            ReviewedGameIntakeDocumentAuthority bound =
                documents.BindReviewedIntake(intake, destination);
            if (bound.Document.ByteLength is <= 0 or > MaximumDocumentBytes)
                throw Failure(
                    ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                    "The canonical reviewed-intake JSON exceeds its admitted size.");

            owned = boundary.CreateOwnedFile(
                temporary,
                destination,
                "protocol-v2 reviewed-intake artifact");
            byte[] stagedReadback = await owned.WriteAndReadbackAsync(
                bound.Document.Utf8Json.AsMemory(),
                MaximumDocumentBytes,
                cancellationToken);
            if (!stagedReadback.AsSpan().SequenceEqual(
                    bound.Document.Utf8Json.AsSpan()))
                throw Failure(
                    ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                    "The staged reviewed-intake readback differs from its canonical bytes.");

            cancellationToken.ThrowIfCancellationRequested();
            owned.PromoteNoOverwrite();

            // Promotion is the commit point. Cancellation is intentionally no
            // longer observed; retained-handle readback and parsing must finish.
            byte[] promotedReadback = await owned.ReadbackExactAsync(
                MaximumDocumentBytes,
                CancellationToken.None);
            if (!promotedReadback.AsSpan().SequenceEqual(
                    bound.Document.Utf8Json.AsSpan()))
                throw Failure(
                    ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                    "The promoted reviewed-intake readback differs from its canonical bytes.");
            ReviewedGameIntakeDocumentAuthority reloaded =
                documents.ParseReviewedIntake(
                    new ExactJsonFileAuthority(
                        destination,
                        promotedReadback.ToImmutableArray(),
                        promotedReadback.LongLength,
                        bound.Document.Sha256));
            if (reloaded.Document.Path != destination ||
                reloaded.Document.ByteLength != bound.Document.ByteLength ||
                reloaded.Document.Sha256 != bound.Document.Sha256 ||
                !reloaded.Document.Utf8Json.AsSpan().SequenceEqual(
                    bound.Document.Utf8Json.AsSpan()) ||
                reloaded.Value.IntakeFingerprint != intake.IntakeFingerprint ||
                ReviewedGameIntakeFingerprintAuthority.Fingerprint(reloaded.Value) !=
                reloaded.Value.IntakeFingerprint)
                throw Failure(
                    ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                    "The promoted reviewed-intake failed retained-handle typed verification.");

            owned.Dispose();
            owned = null;
            return reloaded;
        }
        catch (OperationCanceledException exception)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        catch (ReviewedGameIntakeArtifactException exception)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidDataException or IOException or
                                           UnauthorizedAccessException)
        {
            CleanupOrThrow(owned, exception);
            throw Failure(
                ClassifyFilesystemFailure(exception),
                "The reviewed-intake artifact transaction was refused.",
                exception);
        }
    }

    private void ValidateDestination(
        ReviewedGameIntake intake,
        WorkspacePath destination)
    {
        if (intake.WorkspaceRoot != boundary.WorkspaceRoot)
            throw Failure(
                ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
                "The reviewed intake does not belong to the exact configured workspace.");
        if (destination.Value.IndexOf(':', 2) >= 0)
            throw Failure(
                ProtocolV2DiagnosticCodes.AlternateDataStreamRefused,
                "The reviewed-intake output may not use an alternate data stream.");
        if (!destination.IsUnder(boundary.WorkspaceRoot))
            throw Failure(
                ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace,
                "The reviewed-intake output must remain beneath the exact workspace.");
        if (Overlaps(destination, intake.DataRoot) ||
            Overlaps(destination, intake.OutputRoot))
            throw Failure(
                ProtocolV2DiagnosticCodes.ReviewedIntakeOutputOverlap,
                "The reviewed-intake output must remain outside DataRoot and the reserved OutputRoot.");
        if (File.Exists(destination.Value) || Directory.Exists(destination.Value))
            throw Failure(
                ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists,
                "The reviewed-intake output must be fresh and must not already exist.");
        string? parent = Path.GetDirectoryName(destination.Value);
        if (parent is null || !Directory.Exists(parent) || File.Exists(parent))
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The reviewed-intake output parent must be an existing ordinary directory.");
        try
        {
            boundary.RequireNewFile(destination, "protocol-v2 reviewed-intake output");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Failure(
                exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
                    ? ProtocolV2DiagnosticCodes.ReparsePointRefused
                    : ProtocolV2DiagnosticCodes.PathInspectionFailed,
                "The reviewed-intake output path failed exact ancestry admission.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.PathInspectionFailed,
                "The reviewed-intake output path is not an ordinary fresh file path.",
                exception);
        }
    }

    private static bool Overlaps(WorkspacePath file, WorkspacePath root) =>
        file == root || file.IsUnder(root) || root.IsUnder(file);

    private static string ClassifyFilesystemFailure(Exception exception) =>
        exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
            ? ProtocolV2DiagnosticCodes.ReparsePointRefused
            : exception.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                ? ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists
                : ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed;

    private static void CleanupOrThrow(
        FaceGeomHairRegionsOwnedFile? owned,
        Exception primaryFailure)
    {
        if (owned is null)
            return;
        if (!owned.TryDelete(out string? cleanupFailure))
            throw Failure(
                ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed,
                "The reviewed-intake transaction failed and its exact owned file could not be removed: " +
                cleanupFailure,
                primaryFailure);
    }

    private static ReviewedGameIntakeArtifactException Failure(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}

public sealed class ReviewedGameIntakeArtifactException : IOException
{
    public ReviewedGameIntakeArtifactException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("A diagnostic code is required.", nameof(code))
            : code;
    }

    public string Code { get; }
}
