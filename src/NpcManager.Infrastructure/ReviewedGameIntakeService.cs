using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Builds one immutable, read-only Skyrim intake from explicit K-local inputs.
/// Each dependency is reviewed before the intake is exposed to desktop or CLI callers.
/// </summary>
public sealed class ReviewedGameIntakeService(
    IGameRootPreflightService gameRootPreflight,
    IPluginLoadOrderService loadOrderService,
    IBodySidecarInspectionService bodySidecarService,
    IGeneratedArtifactScanService generatedArtifactService,
    IAssetIndexer assetIndexer) : IReviewedGameIntakeService
{
    public ValueTask<ReviewedGameIntakeResult> ReviewAsync(
        ReviewedGameIntakeRequest request, CancellationToken cancellationToken) =>
        ReviewAsync(request, null, cancellationToken);

    public async ValueTask<ReviewedGameIntakeResult> ReviewAsync(
        ReviewedGameIntakeRequest request,
        IProgress<ReviewedGameIntakeProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 0, true,
            "Checking the copied workspace and fresh output boundary...");
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(new Diagnostic("reviewed-intake-edition-unsupported", DiagnosticSeverity.Error,
                "The NPC Studio reviewed-workspace surface accepts Skyrim SE/AE only."));
            return Failed(request.Edition, [], diagnostics);
        }

        var rootResult = await gameRootPreflight.EvaluateAsync(new GameRootPreflightRequest(
            request.Edition, request.WorkspaceRoot, request.DataRoot, request.OutputRoot), cancellationToken);
        diagnostics.AddRange(rootResult.Diagnostics);
        if (!rootResult.IsAllowed)
            return Failed(request.Edition, [], diagnostics);
        string? outputParent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (string.IsNullOrWhiteSpace(outputParent) ||
            !Directory.Exists(outputParent))
        {
            diagnostics.Add(new Diagnostic(
                ProtocolV2DiagnosticCodes.ReviewedIntakeOutputParentMissing,
                DiagnosticSeverity.Error,
                "The reviewed-intake reserved --output-root parent must already exist as an ordinary directory; choose a fresh output root under an existing parent."));
            return Failed(request.Edition, [], diagnostics);
        }
        if (Directory.Exists(request.OutputRoot.Value) || File.Exists(request.OutputRoot.Value))
        {
            diagnostics.Add(new Diagnostic("reviewed-intake-output-exists", DiagnosticSeverity.Error,
                "Choose a fresh output path that does not exist yet."));
            return Failed(request.Edition, [], diagnostics);
        }

        Report(progress, 0, true,
            "Reading the explicit load order and plugin headers...");
        var closure = await loadOrderService.ReviewClosureAsync(new PluginClosureReviewRequest(
            request.Edition, request.DataRoot, request.LoadOrderPath, request.SelectedPlugins), cancellationToken);
        diagnostics.AddRange(closure.Diagnostics);
        if (!closure.IsValid || closure.LoadOrder.SourceHash is null)
            return Failed(request.Edition, closure.Entries, diagnostics);

        var sidecars = await InspectSidecarsAsync(
            request, closure, diagnostics, progress, cancellationToken);
        if (HasErrors(diagnostics))
            return Failed(request.Edition, closure.Entries, diagnostics);

        Report(progress, 60, false,
            "Checking generated plugins and sidecars...");
        var generated = await generatedArtifactService.ScanAsync(
            new GeneratedArtifactScanRequest(request.Edition, request.DataRoot), cancellationToken);
        diagnostics.AddRange(generated.Diagnostics);
        if (!generated.IsValid)
            return Failed(request.Edition, closure.Entries, diagnostics);

        Report(progress, 75, false,
            "Indexing the copied loose and archived assets...");
        var assets = await assetIndexer.IndexAsync(
            new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
        diagnostics.AddRange(assets.Diagnostics);
        if (HasErrors(diagnostics))
            return Failed(request.Edition, closure.Entries, diagnostics);

        var acceptedPlugins = closure.Entries
            .Where(entry => closure.Closure.Contains(entry.Plugin))
            .OrderBy(entry => entry.Order)
            .ToImmutableArray();
        if (acceptedPlugins.Any(entry => !entry.ReadSucceeded || entry.SourceHash is null))
        {
            diagnostics.Add(new Diagnostic("reviewed-intake-plugin-unbound", DiagnosticSeverity.Error,
                "Every selected plugin and required master must be readable and hash-bound."));
            return Failed(request.Edition, closure.Entries, diagnostics);
        }

        var assetFingerprint =
            AssetProviderInventoryAuthority.Fingerprint(
                assets.Providers);
        var intakeFingerprint =
            ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                request.Edition,
                request.WorkspaceRoot,
                request.DataRoot,
                request.LoadOrderPath,
                request.OutputRoot,
                closure.LoadOrder.SourceHash.Value,
                acceptedPlugins,
                sidecars,
                generated.Plugins,
                generated.Sidecars,
                assetFingerprint);
        var intake = new ReviewedGameIntake(
            request.Edition,
            request.WorkspaceRoot,
            request.DataRoot,
            request.LoadOrderPath,
            request.OutputRoot,
            closure.LoadOrder.SourceHash.Value,
            acceptedPlugins,
            sidecars,
            generated.Plugins,
            generated.Sidecars,
            assets.Providers.Length,
            assetFingerprint,
            intakeFingerprint,
            RuntimeAuthority: false);
        Report(progress, 100, false,
            "Copied workspace reviewed and hash-bound.");
        return new ReviewedGameIntakeResult(
            request.Edition, closure.Entries, intake, diagnostics.ToImmutable());
    }

    private async ValueTask<ImmutableArray<ReviewedBodySidecar>> InspectSidecarsAsync(
        ReviewedGameIntakeRequest request,
        PluginClosureReviewResult closure,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IProgress<ReviewedGameIntakeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sidecars = ImmutableArray.CreateBuilder<ReviewedBodySidecar>();
        var pluginCount = closure.Closure.Length;
        for (var index = 0; index < pluginCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = closure.Closure[index];
            var percent = 35 + (20 * (index + 1) / Math.Max(1, pluginCount));
            Report(progress, percent, false,
                $"Inspecting plugin {index + 1} of {pluginCount}: {plugin.Value}");
            var sidecarName = $"{Path.GetFileNameWithoutExtension(plugin.Value)}.bssliders";
            var path = new WorkspacePath(Path.Combine(request.DataRoot.Value, sidecarName));
            if (!File.Exists(path.Value)) continue;

            var result = await bodySidecarService.InspectAsync(
                new BodySidecarInspectRequest(request.Edition, path), cancellationToken);
            diagnostics.AddRange(result.Diagnostics);
            if (!result.IsValid || result.Document is null) continue;
            if (!string.Equals(result.Document.Plugin.Value, plugin.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("reviewed-intake-sidecar-plugin-mismatch", DiagnosticSeverity.Error,
                    $"Sidecar '{sidecarName}' declares plugin '{result.Document.Plugin.Value}' instead of '{plugin.Value}'."));
                continue;
            }
            sidecars.Add(new ReviewedBodySidecar(plugin, path,
                result.Document.SourceSha256, result.Document.CanonicalSha256,
                result.Document.Npcs.Length));
        }
        return sidecars.ToImmutable();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void Report(
        IProgress<ReviewedGameIntakeProgress>? progress,
        int percent,
        bool isIndeterminate,
        string message) =>
        progress?.Report(new ReviewedGameIntakeProgress(
            Math.Clamp(percent, 0, 100), isIndeterminate, message));

    private static ReviewedGameIntakeResult Failed(
        GameEdition edition,
        ImmutableArray<PluginClosureReviewEntry> entries,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(edition, entries, null, diagnostics.ToImmutable());
}
