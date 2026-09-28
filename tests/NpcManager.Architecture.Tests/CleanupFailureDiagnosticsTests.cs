using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestProposalCleanupFailureDiagnostics()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "proposal-cleanup-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));

            await VerifyOutfitCleanupFailure(root, policy, labRoot);
            await VerifyLeveledListCleanupFailure(root, policy, labRoot);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyOutfitCleanupFailure(
        string root,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        string source = Path.Combine(root, "OutfitSource.esp");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var sourcePlugin = new PluginName("OutfitSource.esp");
        WorkspacePath output = new(Path.Combine(root, "failure.outfit-proposal.json"));
        var inspection = new PluginInspection(
            GameEdition.SkyrimSpecialEdition,
            sourcePlugin,
            [],
            [
                new PluginRecordSummary(
                    new FormId(0x900), "OTFT", "SourceOutfit", null,
                    false, false, OwnerPlugin: sourcePlugin),
                new PluginRecordSummary(
                    new FormId(0x800), "ARMO", "SourceArmor", "Source armor",
                    false, false, OwnerPlugin: sourcePlugin)
            ],
            []);
        var cleanup = new LockingFileArtifactCleanup();
        var service = new OutfitProposalService(
            new OutputCollisionPluginReader(output.Value, inspection),
            policy,
            labRoot,
            cleanup);

        OutfitProposalResult result = await service.ProposeAsync(
            new OutfitProposalRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(source),
                new FormId(0x900),
                OutfitProposalMode.Override,
                null,
                [new FormReference(sourcePlugin, new FormId(0x800))],
                output),
            CancellationToken.None);

        Assert(!result.Written &&
               result.Diagnostics.Any(item =>
                   item.Code == "outfit-proposal-write-failed" &&
                   item.Severity == DiagnosticSeverity.Error) &&
               result.Diagnostics.Any(item =>
                   item.Code == "outfit-proposal-cleanup-failed" &&
                   item.Severity == DiagnosticSeverity.Warning &&
                   item.Message.Contains(cleanup.AttemptedPath!, StringComparison.Ordinal)) &&
               cleanup.AttemptedPath is not null &&
               File.Exists(cleanup.AttemptedPath),
            "A locked outfit proposal temporary artifact was not reported with its surviving path.");

        cleanup.Dispose();
        File.Delete(cleanup.AttemptedPath!);
        File.Delete(output.Value);
    }

    private static async Task VerifyLeveledListCleanupFailure(
        string root,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        string source = Path.Combine(root, "LeveledSource.esp");
        await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
        var sourcePlugin = new PluginName("LeveledSource.esp");
        WorkspacePath output = new(Path.Combine(root, "failure.leveled-list-proposal.json"));
        var inspection = new PluginInspection(
            GameEdition.SkyrimSpecialEdition,
            sourcePlugin,
            [],
            [
                new PluginRecordSummary(
                    new FormId(0x900), "LVLI", "SourceList", null,
                    false, false, OwnerPlugin: sourcePlugin)
            ],
            []);
        var cleanup = new LockingFileArtifactCleanup();
        var service = new LeveledListProposalService(
            new OutputCollisionPluginReader(output.Value, inspection),
            policy,
            labRoot,
            cleanup);

        LeveledListProposalResult result = await service.ProposeAsync(
            new LeveledListProposalRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(source),
                new FormId(0x900),
                new EditorId("SourceList"),
                0,
                1,
                false,
                false,
                true,
                [new LeveledListEntryProposal(
                    new FormReference(sourcePlugin, new FormId(0x800)),
                    1,
                    1,
                    0)],
                output),
            CancellationToken.None);

        Assert(!result.Written &&
               result.Diagnostics.Any(item =>
                   item.Code == "leveled-list-write-failed" &&
                   item.Severity == DiagnosticSeverity.Error) &&
               result.Diagnostics.Any(item =>
                   item.Code == "leveled-list-cleanup-failed" &&
                   item.Severity == DiagnosticSeverity.Warning &&
                   item.Message.Contains(cleanup.AttemptedPath!, StringComparison.Ordinal)) &&
               cleanup.AttemptedPath is not null &&
               File.Exists(cleanup.AttemptedPath),
            "A locked leveled-list proposal temporary artifact was not reported with its surviving path.");

        cleanup.Dispose();
        File.Delete(cleanup.AttemptedPath!);
        File.Delete(output.Value);
    }

    private sealed class OutputCollisionPluginReader(
        string output,
        PluginInspection inspection) : IPluginReader
    {
        public ValueTask<PluginInspection> ReadAsync(
            PluginReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(output, "injected no-overwrite collision");
            return ValueTask.FromResult(inspection);
        }
    }

    private sealed class LockingFileArtifactCleanup :
        IFileArtifactCleanup,
        IDisposable
    {
        private FileStream? _lock;

        public string? AttemptedPath { get; private set; }

        public FileArtifactCleanupResult DeleteIfPresent(string path)
        {
            AttemptedPath = path;
            _lock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return new FileArtifactCleanup().DeleteIfPresent(path);
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
