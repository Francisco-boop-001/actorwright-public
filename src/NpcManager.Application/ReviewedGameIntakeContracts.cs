using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ReviewedGameIntakeRequest(
    GameEdition Edition,
    WorkspacePath WorkspaceRoot,
    WorkspacePath DataRoot,
    WorkspacePath LoadOrderPath,
    WorkspacePath OutputRoot,
    ImmutableArray<PluginName> SelectedPlugins = default);

public sealed record ReviewedBodySidecar(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash SourceHash,
    Sha256Hash CanonicalHash,
    int NpcCount);

public sealed record ReviewedGameIntake(
    GameEdition Edition,
    WorkspacePath WorkspaceRoot,
    WorkspacePath DataRoot,
    WorkspacePath LoadOrderPath,
    WorkspacePath OutputRoot,
    Sha256Hash LoadOrderHash,
    ImmutableArray<PluginClosureReviewEntry> Plugins,
    ImmutableArray<ReviewedBodySidecar> BodySidecars,
    ImmutableArray<GeneratedPluginScanEntry> GeneratedPlugins,
    ImmutableArray<GeneratedSidecarEntry> GeneratedSidecars,
    int AssetProviderCount,
    Sha256Hash AssetIndexFingerprint,
    Sha256Hash IntakeFingerprint,
    bool RuntimeAuthority);

public sealed record ReviewedGameIntakeResult(
    GameEdition Edition,
    ImmutableArray<PluginClosureReviewEntry> AvailablePlugins,
    ReviewedGameIntake? Intake,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsAccepted => Intake is not null &&
                              !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record ReviewedGameIntakeProgress(
    int Percent,
    bool IsIndeterminate,
    string Message);

public interface IReviewedGameIntakeService
{
    ValueTask<ReviewedGameIntakeResult> ReviewAsync(
        ReviewedGameIntakeRequest request, CancellationToken cancellationToken);

    ValueTask<ReviewedGameIntakeResult> ReviewAsync(
        ReviewedGameIntakeRequest request,
        IProgress<ReviewedGameIntakeProgress>? progress,
        CancellationToken cancellationToken) => ReviewAsync(request, cancellationToken);
}
