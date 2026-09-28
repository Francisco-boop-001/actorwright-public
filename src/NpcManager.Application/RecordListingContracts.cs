using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RecordListRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    string? Signature = null,
    string? Search = null);

public sealed record ListedRecord(
    PluginName Plugin,
    FormId FormId,
    string Signature,
    string? EditorId,
    string? Name,
    bool IsNpc,
    bool IsDeleted,
    string? RawRecordSha256);

public sealed record RecordListResult(
    GameEdition Edition,
    ImmutableArray<PluginName> Plugins,
    ImmutableArray<ListedRecord> Records,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IRecordListingService
{
    ValueTask<RecordListResult> ListAsync(RecordListRequest request, CancellationToken cancellationToken);
}
