using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record OperationJournalRecord(
    string Command,
    string RequestDigest,
    ImmutableArray<ProtocolEffect> Effects,
    ImmutableArray<string> DiagnosticCodes,
    ImmutableArray<string> DiagnosticClasses,
    ImmutableArray<string> ArtifactHashes,
    long DurationMilliseconds,
    string Outcome,
    int ExitCode);

public sealed record OperationJournalAppendResult(
    bool Appended,
    WorkspacePath? Path,
    Diagnostic? Warning);

public interface ILocalOperationJournal
{
    ValueTask<OperationJournalAppendResult> AppendAsync(
        OperationJournalRecord record,
        CancellationToken cancellationToken);
}
