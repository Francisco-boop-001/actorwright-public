using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PluginSurfaceAuditRequest(
    GameEdition Edition,
    WorkspacePath Before,
    WorkspacePath After,
    WorkspacePath? PluginsRoot = null,
    WorkspacePath? LoadOrderPath = null,
    bool NormalizeMasterIndex = false);

public sealed record PluginSurfaceAuditRecordChange(
    string FormId,
    string Signature,
    string ChangeKind,
    string? BeforeFingerprint,
    string? AfterFingerprint);

public sealed record PluginSurfaceAuditProviderResolution(
    string FormId,
    string Signature,
    string WinningPlugin,
    ImmutableArray<string> OverrideChain);

public sealed record PluginSurfaceAuditResult(
    bool IsValid,
    int SchemaVersion,
    string Edition,
    string BeforePlugin,
    string AfterPlugin,
    int BeforeRecordCount,
    int AfterRecordCount,
    int AddedRecordCount,
    int RemovedRecordCount,
    int ChangedRecordCount,
    ImmutableArray<string> BeforeMasters,
    ImmutableArray<string> AfterMasters,
    ImmutableArray<string> RiskyRecordSignatures,
    ImmutableArray<PluginSurfaceAuditRecordChange> Changes,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<PluginSurfaceAuditProviderResolution> ProviderResolutions = default);

public interface IPluginSurfaceAuditService
{
    ValueTask<PluginSurfaceAuditResult> AuditAsync(
        PluginSurfaceAuditRequest request,
        CancellationToken cancellationToken);
}
