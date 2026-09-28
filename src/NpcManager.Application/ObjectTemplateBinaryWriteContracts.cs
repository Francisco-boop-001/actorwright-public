using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ObjectTemplateBinaryWriteRequest(GameEdition Edition, WorkspacePath Proposal, WorkspacePath Output,
    WorkspacePath? Properties = null);

public sealed record ObjectTemplateBinaryWriteResult(bool Written, WorkspacePath Proposal, WorkspacePath Output,
    FormId? TargetFormId, Sha256Hash? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

public interface IObjectTemplateBinaryWriteService
{
    ValueTask<ObjectTemplateBinaryWriteResult> WriteAsync(ObjectTemplateBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
