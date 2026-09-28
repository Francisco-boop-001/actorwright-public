using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record BodySidecarWriteRequest(GameEdition Edition, PluginName Plugin, FormId NpcFormId,
    EditorId? EditorId, ImmutableDictionary<string, float> BodyMorphs, WorkspacePath Output);

public sealed record BodySidecarWriteResult(bool Written, GameEdition Edition, PluginName Plugin, FormId NpcFormId,
    WorkspacePath Output, Sha256Hash? OutputHash, ImmutableArray<Diagnostic> Diagnostics);

public interface IBodySidecarWriteService
{
    ValueTask<BodySidecarWriteResult> WriteAsync(BodySidecarWriteRequest request, CancellationToken cancellationToken);
}
