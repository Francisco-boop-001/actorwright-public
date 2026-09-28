using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestReviewedGameIntake()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var projectRoot = new WorkspacePath(Path.Combine(labRoot.Value,
            "projects", "NpcManagerReimplementation"));
        var dataRoot = new WorkspacePath(Path.Combine(projectRoot.Value,
            "01-source-copies", "m2-fixtures", "sse", "Data"));
        var loadOrder = new WorkspacePath(Path.Combine(projectRoot.Value,
            "01-source-copies", "m2-fixtures", "load-order-sse.json"));
        var outputRoot = new WorkspacePath(Path.Combine(projectRoot.Value,
            "03-builds", "work", "reviewed-intake-architecture-output"));
        var riskRoot = new WorkspacePath(Path.Combine(projectRoot.Value,
            "03-builds", "work", $"reviewed-intake-risk-{Guid.NewGuid():N}"));
        var policy = new KOnlyWorkspacePolicy(labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var pluginReader = new BethesdaPluginReader();
        var service = new ReviewedGameIntakeService(
            new GameRootPreflightService(policy),
            new PluginLoadOrderService(pluginReader, policy, labRoot),
            new BodySidecarInspectionService(policy, labRoot),
            new GeneratedArtifactScanService(policy, pluginReader, labRoot),
            new BethesdaAssetIndexer());
        if (Directory.Exists(outputRoot.Value)) Directory.Delete(outputRoot.Value, recursive: true);
        Directory.CreateDirectory(riskRoot.Value);

        try
        {
            var progressEvents = new List<ReviewedGameIntakeProgress>();
            var result = await service.ReviewAsync(new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                loadOrder, outputRoot), new SynchronousProgress<ReviewedGameIntakeProgress>(
                    progressEvents.Add), CancellationToken.None);

            Assert(result.IsAccepted && result.Intake is not null &&
                   result.Intake.Plugins.Length == 1 &&
                   result.Intake.Plugins[0].ReadSucceeded &&
                   result.Intake.Plugins[0].SourceHash is not null &&
                   result.Intake.LoadOrderHash.Value.Length == 64 &&
                   result.Intake.IntakeFingerprint.Value.Length == 64 &&
                   !result.Intake.RuntimeAuthority,
                "The reviewed intake did not retain one hash-bound non-runtime Skyrim closure.");
            Assert(progressEvents.FirstOrDefault() is { IsIndeterminate: true } &&
                   progressEvents.Any(item => !item.IsIndeterminate &&
                                               item.Message.Contains("Inspecting plugin 1 of 1",
                                                   StringComparison.Ordinal)) &&
                   progressEvents.LastOrDefault() is { Percent: 100, IsIndeterminate: false },
                "The reviewed intake did not transition from discovery to typed determinate plugin progress.");

            var emptySelection = await service.ReviewAsync(new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                loadOrder, outputRoot, ImmutableArray<PluginName>.Empty),
                CancellationToken.None);
            Assert(!emptySelection.IsAccepted && emptySelection.Intake is null &&
                   emptySelection.Diagnostics.Any(item => item.Code == "plugin-selection-empty"),
                "An explicitly empty plugin selection retained a reviewed intake.");

            var missingParentOutput = new WorkspacePath(Path.Combine(
                riskRoot.Value, "missing-output-parent", "reserved-output"));
            var missingParent = await service.ReviewAsync(
                new ReviewedGameIntakeRequest(
                    GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                    loadOrder, missingParentOutput), CancellationToken.None);
            Assert(!missingParent.IsAccepted && missingParent.Intake is null &&
                   missingParent.Diagnostics.Any(item =>
                       item.Code == "reviewed-intake-output-parent-missing") &&
                   !missingParent.Diagnostics.Any(item =>
                       item.Code == "reviewed-intake-output-exists"),
                "A missing --output-root parent was not refused with its output-root diagnostic.");

            Directory.CreateDirectory(outputRoot.Value);
            var occupiedOutput = await service.ReviewAsync(new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                loadOrder, outputRoot), CancellationToken.None);
            Assert(!occupiedOutput.IsAccepted && occupiedOutput.Intake is null &&
                   occupiedOutput.Diagnostics.Any(item =>
                       item.Code == "reviewed-intake-output-exists"),
                "An occupied output path retained a reviewed intake.");
            Directory.Delete(outputRoot.Value);

            var protectedRoot = await service.ReviewAsync(new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition, projectRoot,
                new WorkspacePath(@"F:\ExampleGame\Data"), loadOrder, outputRoot),
                CancellationToken.None);
            Assert(!protectedRoot.IsAccepted && protectedRoot.Intake is null &&
                   protectedRoot.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
                "A protected live-root input retained a reviewed intake.");

            var duplicateOrderPath = new WorkspacePath(Path.Combine(riskRoot.Value,
                "duplicate-order.json"));
            await File.WriteAllTextAsync(duplicateOrderPath.Value,
                "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[" +
                "{\"name\":\"M2FixtureSSE.esp\",\"order\":0,\"enabled\":true}," +
                "{\"name\":\"M2FixtureSSE.esp\",\"order\":1,\"enabled\":true}]}");
            var duplicateOrder = await service.ReviewAsync(new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                duplicateOrderPath, outputRoot), CancellationToken.None);
            Assert(!duplicateOrder.IsAccepted && duplicateOrder.Intake is null &&
                   duplicateOrder.Diagnostics.Any(item => item.Code == "load-order-duplicate-plugin"),
                "A duplicate load-order entry retained a reviewed intake.");

            var missingMasterReader = new MissingMasterPluginReader();
            var missingMasterService = new ReviewedGameIntakeService(
                new GameRootPreflightService(policy),
                new PluginLoadOrderService(missingMasterReader, policy, labRoot),
                new BodySidecarInspectionService(policy, labRoot),
                new GeneratedArtifactScanService(policy, missingMasterReader, labRoot),
                new BethesdaAssetIndexer());
            var missingMaster = await missingMasterService.ReviewAsync(
                new ReviewedGameIntakeRequest(
                    GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                    loadOrder, outputRoot), CancellationToken.None);
            Assert(!missingMaster.IsAccepted && missingMaster.Intake is null &&
                   missingMaster.Diagnostics.Any(item =>
                       item.Code == "plugin-master-missing-from-load-order"),
                "A selected plugin with a missing master retained a reviewed intake.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => service.ReviewAsync(
                new ReviewedGameIntakeRequest(
                    GameEdition.SkyrimSpecialEdition, projectRoot, dataRoot,
                    loadOrder, outputRoot), cancellation.Token).AsTask());
        }
        finally
        {
            if (Directory.Exists(outputRoot.Value)) Directory.Delete(outputRoot.Value, recursive: true);
            if (Directory.Exists(riskRoot.Value)) Directory.Delete(riskRoot.Value, recursive: true);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MissingMasterPluginReader : IPluginReader
    {
        public ValueTask<PluginInspection> ReadAsync(
            PluginReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PluginInspection(
                request.Edition,
                new PluginName(Path.GetFileName(request.PluginPath.Value)),
                [new PluginName("MissingMaster.esm")],
                [],
                []));
        }
    }
}
