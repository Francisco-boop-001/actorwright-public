using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Upstream-compatible sequential batch orchestration. Cancellation is observed
/// only before an NPC starts and after its independently verified pair finishes.
/// Successful earlier NPCs remain when a later NPC fails or cancellation wins.
/// </summary>
public sealed partial class FaceGenBakeAllService(
    IFaceGenBakeTargetDiscoveryService discovery,
    IFaceGenNpcBakeService npcBaker,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IAssetIndexSnapshotScopeFactory? assetIndexSnapshots = null,
    ISkyrimFaceGenSidecarOverlayLoader? sidecarOverlays = null)
    : IFaceGenBakeAllService
{
    public async ValueTask<FaceGenBakeAllResult> RunAsync(
        FaceGenBakeAllRequest request,
        IProgress<FaceGenBakeAllProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ValidatePreflight(request).ToBuilder();
        var outcomes = ImmutableArray.CreateBuilder<FaceGenNpcBakeResult>();
        var sequence = 0;
        Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Discovering,
            0, 0, null, "Discovering winning NPC records.");
        if (HasErrors(diagnostics))
            return Fatal(0, outcomes, diagnostics, progress, ref sequence);

        IDisposable? indexSnapshot;
        try
        {
            indexSnapshot = assetIndexSnapshots?.BeginSnapshot();
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(Error("facegen-bake-all-index-snapshot",
                exception.Message));
            return Fatal(0, outcomes, diagnostics, progress, ref sequence);
        }
        using (indexSnapshot)
        {
            return await RunWithinSnapshotAsync(request, progress,
                diagnostics, outcomes, sequence, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<FaceGenBakeAllResult> RunWithinSnapshotAsync(
        FaceGenBakeAllRequest request,
        IProgress<FaceGenBakeAllProgress>? progress,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<FaceGenNpcBakeResult>.Builder outcomes,
        int sequence,
        CancellationToken cancellationToken)
    {

        FaceGenBakeTargetDiscoveryResult discovered;
        try
        {
            discovered = await discovery.DiscoverAsync(
                new FaceGenBakeTargetDiscoveryRequest(request.Edition,
                    request.DataRoot, request.PluginOrder,
                    request.WinningPlugin), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(0, outcomes, diagnostics, progress, ref sequence);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("facegen-bake-all-discovery-failed",
                exception.Message));
            return Fatal(0, outcomes, diagnostics, progress, ref sequence);
        }

        diagnostics.AddRange(discovered.Diagnostics);
        if (!discovered.Accepted || discovered.Targets.IsDefaultOrEmpty ||
            HasErrors(diagnostics))
            return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                progress, ref sequence);
        if (!ValidateOutputUniverse(request, discovered.Targets, diagnostics))
            return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                progress, ref sequence);

        Dictionary<string, SkyrimFaceGenSidecarOverlay> overlayByTarget =
            new(StringComparer.OrdinalIgnoreCase);
        if (sidecarOverlays is not null)
        {
            SkyrimFaceGenSidecarOverlayLoadResult loaded;
            try
            {
                loaded = await sidecarOverlays.LoadAsync(
                    new SkyrimFaceGenSidecarOverlayLoadRequest(
                        request.Edition, request.DataRoot, request.PluginOrder,
                        discovered.Targets), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled(discovered.Targets.Length, outcomes,
                    diagnostics, progress, ref sequence);
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                diagnostics.Add(Error("facegen-bake-all-sidecar-failed",
                    exception.Message));
                return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                    progress, ref sequence);
            }
            diagnostics.AddRange(loaded.Diagnostics);
            if (!loaded.Accepted || loaded.Overlays.IsDefault || HasErrors(diagnostics))
                return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                    progress, ref sequence);
            if (loaded.Overlays.Any(item => item is null))
            {
                diagnostics.Add(Error("facegen-bake-all-sidecar-shape",
                    "The accepted sidecar result contains a null overlay."));
                return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                    progress, ref sequence);
            }
            HashSet<string> targetKeys = discovered.Targets.Select(TargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] overlayKeys = loaded.Overlays.Select(OverlayKey).ToArray();
            if (overlayKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    overlayKeys.Length || overlayKeys.Any(key => !targetKeys.Contains(key)))
            {
                diagnostics.Add(Error("facegen-bake-all-sidecar-closure",
                    "Sidecar overlays must be unique and belong to the discovered target set."));
                return Fatal(discovered.Targets.Length, outcomes, diagnostics,
                    progress, ref sequence);
            }
            overlayByTarget = loaded.Overlays.ToDictionary(
                OverlayKey, StringComparer.OrdinalIgnoreCase);
        }

        int completed = 0;
        foreach (FaceGenBakeTarget target in discovered.Targets)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled(discovered.Targets.Length, outcomes,
                    diagnostics, progress, ref sequence);

            Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Baking,
                completed, discovered.Targets.Length, target,
                $"Baking {TargetLabel(target)}.");
            overlayByTarget.TryGetValue(TargetKey(target),
                out SkyrimFaceGenSidecarOverlay? overlay);
            FaceGenNpcBakeResult outcome = await BakeOneAsync(
                request, target, overlay).ConfigureAwait(false);
            outcome = await ValidateOutcomeAsync(request.OutputDataRoot,
                target, outcome).ConfigureAwait(false);
            outcomes.Add(outcome);
            diagnostics.AddRange(outcome.Diagnostics);
            completed++;
            Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Baking,
                completed, discovered.Targets.Length, target,
                OutcomeMessage(outcome));
        }

        int failed = outcomes.Count(item => item.Status ==
            FaceGenNpcBakeStatus.Failed);
        FaceGenBakeAllStatus status = failed == 0
            ? FaceGenBakeAllStatus.Succeeded
            : FaceGenBakeAllStatus.SomeFailed;
        Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Completed,
            completed, discovered.Targets.Length, null,
            Summary(outcomes));
        return Build(status, discovered.Targets.Length, outcomes, diagnostics);
    }

    private async ValueTask<FaceGenNpcBakeResult> BakeOneAsync(
        FaceGenBakeAllRequest request,
        FaceGenBakeTarget target,
        SkyrimFaceGenSidecarOverlay? overlay)
    {
        try
        {
            return await npcBaker.BakeAsync(new FaceGenNpcBakeRequest(
                    request.Edition, request.DataRoot, request.PluginOrder,
                    target, request.OutputDataRoot, overlay), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            return new FaceGenNpcBakeResult(FaceGenNpcBakeStatus.Failed,
                target, null, [Error("facegen-bake-all-npc-exception",
                    exception.Message)]);
        }
    }

    private static FaceGenBakeAllResult Build(
        FaceGenBakeAllStatus status,
        int discovered,
        ImmutableArray<FaceGenNpcBakeResult>.Builder outcomes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<FaceGenNpcBakeResult> items = outcomes.ToImmutable();
        return new FaceGenBakeAllResult(status, discovered,
            items.Count(item => item.Status == FaceGenNpcBakeStatus.Baked),
            items.Count(item => item.Status == FaceGenNpcBakeStatus.Skipped),
            items.Count(item => item.Status == FaceGenNpcBakeStatus.Failed),
            items, diagnostics.ToImmutable());
    }

    private static string TargetLabel(FaceGenBakeTarget target) =>
        $"{target.OriginatingPlugin.Value}|{target.FormId}";

    private static string TargetKey(FaceGenBakeTarget target) =>
        $"{target.OriginatingPlugin.Value}|{target.FormId.Value & 0x00FF_FFFF:X6}";

    private static string OverlayKey(SkyrimFaceGenSidecarOverlay overlay) =>
        $"{overlay.OriginatingPlugin.Value}|{overlay.FormId.Value & 0x00FF_FFFF:X6}";

    private static string OutcomeMessage(FaceGenNpcBakeResult result) =>
        $"{result.Status}: {TargetLabel(result.Target)}.";

    private static string Summary(
        IEnumerable<FaceGenNpcBakeResult> outcomes) =>
        $"Finished: {outcomes.Count(item => item.Status == FaceGenNpcBakeStatus.Baked)} baked, " +
        $"{outcomes.Count(item => item.Status == FaceGenNpcBakeStatus.Skipped)} skipped, " +
        $"{outcomes.Count(item => item.Status == FaceGenNpcBakeStatus.Failed)} failed.";

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
