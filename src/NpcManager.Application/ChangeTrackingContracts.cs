using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ChangeFieldDifference(string Name, string? Baseline, string? Working);
public sealed record ChangedRecord(FormId FormId, string Signature, string? EditorId, ImmutableArray<ChangeFieldDifference> Fields);
public sealed record ChangeListRequest(GameEdition Edition, WorkspacePath Session);
public sealed record ChangeListResult(bool Succeeded, GameEdition Edition, ImmutableArray<ChangedRecord> Changes, ImmutableArray<Diagnostic> Diagnostics);

public interface IChangeTrackingService
{
    ValueTask<ChangeListResult> ListAsync(ChangeListRequest request, CancellationToken cancellationToken);
}
