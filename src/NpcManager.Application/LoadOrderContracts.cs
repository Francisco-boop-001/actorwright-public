using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PluginLoadOrderEntry(PluginName Plugin, int Order, bool Enabled);

public sealed record PluginLoadOrderResolvedEntry(
    PluginName Plugin,
    int Order,
    bool Enabled,
    bool Exists,
    ImmutableArray<PluginName> Masters);

public sealed record PluginLoadOrderRequest(
    GameEdition Edition,
    WorkspacePath PluginsRoot,
    WorkspacePath LoadOrderPath);

public sealed record PluginLoadOrderResult(
    GameEdition Edition,
    ImmutableArray<PluginLoadOrderResolvedEntry> Entries,
    Sha256Hash? SourceHash,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record PluginCompatibilityRequest(
    GameEdition Edition,
    WorkspacePath PluginPath,
    WorkspacePath LoadOrderPath);

public sealed record PluginCompatibilityResult(
    GameEdition Edition,
    PluginName? Plugin,
    bool IsCompatible,
    ImmutableArray<PluginName> Masters,
    PluginLoadOrderResult LoadOrder,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record PluginClosureReviewRequest(
    GameEdition Edition,
    WorkspacePath PluginsRoot,
    WorkspacePath LoadOrderPath,
    ImmutableArray<PluginName> SelectedPlugins = default);

public sealed record PluginClosureReviewEntry(
    PluginName Plugin,
    int Order,
    bool Enabled,
    bool Exists,
    bool Requested,
    bool RequiredMaster,
    bool ReadSucceeded,
    WorkspacePath Path,
    Sha256Hash? SourceHash,
    ImmutableArray<PluginName> Masters);

public sealed record PluginClosureReviewResult(
    GameEdition Edition,
    PluginLoadOrderResult LoadOrder,
    ImmutableArray<PluginClosureReviewEntry> Entries,
    ImmutableArray<PluginName> Closure,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => Closure.Length > 0 &&
                           !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IPluginLoadOrderService
{
    ValueTask<PluginLoadOrderResult> ResolveAsync(PluginLoadOrderRequest request, CancellationToken cancellationToken);

    ValueTask<PluginClosureReviewResult> ReviewClosureAsync(
        PluginClosureReviewRequest request, CancellationToken cancellationToken);

    ValueTask<PluginCompatibilityResult> ValidateAsync(PluginCompatibilityRequest request, CancellationToken cancellationToken);
}
