using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Stable document identifiers exported for RaceMenu NPC requests.</summary>
public static class RaceMenuNpcExecutionRequestSchemas
{
    public const string Request = "npc.create-from-jslot.request.v1";
}

/// <summary>Outcome of loading one exact prepared preset-to-NPC request file.</summary>
public enum RaceMenuNpcExecutionRequestFileLoadStatus
{
    Loaded,
    MigrationRequired,
    ValidationRefused,
    SecurityRefused
}

/// <summary>
/// Binds a K-local JSON request to the exact bytes selected by the caller.
/// </summary>
public sealed record RaceMenuNpcExecutionRequestFileLoadRequest(
    WorkspacePath RequestFile,
    Sha256Hash ExpectedSha256)
{
    public WorkspacePath? PlannedMigratedRequestRoot { get; init; }
}

/// <summary>
/// Typed load evidence shared by CLI and desktop surfaces. A loaded result
/// contains the same immutable execution request consumed by the build service.
/// </summary>
public sealed record RaceMenuNpcExecutionRequestFileLoadResult(
    RaceMenuNpcExecutionRequestFileLoadStatus Status,
    WorkspacePath RequestFile,
    Sha256Hash ExpectedSha256,
    Sha256Hash? ActualSha256,
    long? ByteLength,
    RaceMenuNpcExecutionRequest? Request,
    ImmutableArray<Diagnostic> Diagnostics,
    ProviderMigrationReviewArtifact? MigrationReview = null)
{
    public bool Loaded =>
        Status == RaceMenuNpcExecutionRequestFileLoadStatus.Loaded && Request is not null;
}

public interface IRaceMenuNpcExecutionRequestFileLoader
{
    ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
        RaceMenuNpcExecutionRequestFileLoadRequest request,
        CancellationToken cancellationToken);
}
