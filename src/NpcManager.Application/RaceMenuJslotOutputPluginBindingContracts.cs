using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Read-only request for binding the exact output plugin emitted by a private
/// JSlot probe. The reader must inspect the Bethesda file itself; callers may
/// not provide a synthetic master or PNAM list.
/// </summary>
public sealed record RaceMenuJslotOutputPluginBindingReadRequest(
    GameEdition Edition,
    WorkspacePath PluginPath,
    PluginName ExpectedPlugin,
    FormId TargetNpcFormId);

public sealed record RaceMenuJslotOutputPluginBindingReadResult(
    bool Accepted,
    RaceMenuSelectedDependencyManifestOutputPluginBinding? Binding,
    ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>
    /// Complete PNAM order read from the target NPC. The selected schema-3
    /// group stores only the descriptor/provider projection; keeping this
    /// separate prevents provider-only members from being mistaken for the
    /// full output-NPC PNAM closure.
    /// </summary>
    public ImmutableArray<FormReference> FullPnam { get; init; } = [];
}

public interface IRaceMenuJslotOutputPluginBindingReader
{
    ValueTask<RaceMenuJslotOutputPluginBindingReadResult> ReadAsync(
        RaceMenuJslotOutputPluginBindingReadRequest request,
        CancellationToken cancellationToken);
}
