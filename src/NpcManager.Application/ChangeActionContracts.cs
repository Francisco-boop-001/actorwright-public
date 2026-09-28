using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ChangeAction
{
    Reset,
    Delete
}

public sealed record ChangeActionRequest(
    GameEdition Edition,
    WorkspacePath Session,
    FormId Record,
    string? Signature,
    ChangeAction Action,
    WorkspacePath Output);

public sealed record ChangeActionArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string Action,
    string Session,
    string SessionSha256,
    string FormId,
    string Signature,
    string? EditorId,
    bool BaselinePresent,
    bool WorkingPresent,
    string Outcome,
    ImmutableDictionary<string, string?> BaselineFields,
    ImmutableDictionary<string, string?> WorkingFields,
    bool Explicit);

public sealed record ChangeActionResult(
    bool Written,
    ChangeActionArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IChangeActionService
{
    ValueTask<ChangeActionResult> UpdateAsync(ChangeActionRequest request, CancellationToken cancellationToken);
}
