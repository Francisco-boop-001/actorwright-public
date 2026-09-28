using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimMajorRecordBindingSelection(
    FormReference Reference,
    RecordSignature ExpectedSignature);

public sealed record SkyrimMajorRecordBindingRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<SkyrimMajorRecordBindingSelection> Selections,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder);

/// <summary>One winning supported record and the copied plugin that supplied it.</summary>
public sealed record SkyrimMajorRecordBinding(
    FormReference Reference,
    RecordSignature Signature,
    SkyrimFaceRecordProvider Provider,
    NpcHeadPartType? HeadPartType);

public sealed record SkyrimMajorRecordBindingResult(
    bool Accepted,
    ImmutableArray<SkyrimMajorRecordBinding> Bindings,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMajorRecordBindingReader
{
    ValueTask<SkyrimMajorRecordBindingResult> ReadAsync(
        SkyrimMajorRecordBindingRequest request,
        CancellationToken cancellationToken);
}
