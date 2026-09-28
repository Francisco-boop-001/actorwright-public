using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reconstructs the upstream bake universe without touching a live profile:
/// every winning, non-deleted, parseable NPC in an explicit copied-Data load
/// order, optionally narrowed to records won by one plugin.
/// </summary>
public sealed class FaceGenBakeTargetDiscoveryService(
    IGameInventoryService inventoryService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenBakeTargetDiscoveryService
{
    public async ValueTask<FaceGenBakeTargetDiscoveryResult> DiscoverAsync(
        FaceGenBakeTargetDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        GameInventory inventory;
        try
        {
            inventory = await inventoryService.ReadAsync(new GameInventoryRequest(
                request.Edition,
                request.DataRoot,
                Search: null,
                FormId: null,
                IncludeAssets: false,
                request.PluginOrder), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("facegen-bake-discovery-failed", exception.Message));
            return Refused(diagnostics);
        }

        diagnostics.AddRange(inventory.Diagnostics);
        ValidateInventoryEnvelope(request, inventory, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var pluginIndexes = request.PluginOrder
            .Select((plugin, index) => (plugin, index))
            .ToDictionary(item => item.plugin.Value, item => item.index,
                StringComparer.OrdinalIgnoreCase);
        var targets = ImmutableArray.CreateBuilder<FaceGenBakeTarget>();
        var identities = new HashSet<(string Owner, uint FormId)>(
            BakeTargetIdentityComparer.Instance);
        foreach (NpcRecordSummary npc in inventory.Npcs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (npc.IsDeleted) continue;
            if (request.WinningPlugin is { } filter &&
                !string.Equals(npc.Plugin.Value, filter.Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (npc.Metadata is not
                {
                    Sex: { } sex, Race: { } race,
                    SkyrimWeight: { } weight
                } metadata ||
                metadata.HeadParts.IsDefault || !float.IsFinite(weight) ||
                weight is < 0F or > 100F)
            {
                diagnostics.Add(new Diagnostic("facegen-bake-target-unparseable",
                    DiagnosticSeverity.Warning,
                    $"Skipped winning NPC {npc.Plugin.Value}|{npc.FormId}: sex, weight, or typed race/headpart ownership is unavailable."));
                continue;
            }

            PluginName? owner = npc.OwnerPlugin ?? npc.Provenance?.SourcePlugin;
            if (owner is null)
            {
                diagnostics.Add(Error("facegen-bake-target-owner-missing",
                    $"Winning NPC {npc.Plugin.Value}|{npc.FormId} has no originating-plugin identity."));
                continue;
            }
            PluginName ownerPlugin = owner.Value;
            if (!pluginIndexes.ContainsKey(npc.Plugin.Value) ||
                !pluginIndexes.ContainsKey(ownerPlugin.Value))
            {
                diagnostics.Add(Error("facegen-bake-target-plugin-outside-order",
                    $"Winning NPC {npc.Plugin.Value}|{npc.FormId} references a provider or origin outside the explicit load order."));
                continue;
            }
            ImmutableArray<PluginName> chain = npc.Provenance?.OverrideChain ??
                                                ImmutableArray.Create(npc.Plugin);
            if (chain.IsDefaultOrEmpty ||
                !string.Equals(chain[0].Value, ownerPlugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(chain[^1].Value, npc.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                chain.Select(item => item.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != chain.Length ||
                chain.Any(item => !pluginIndexes.ContainsKey(item.Value)))
            {
                diagnostics.Add(Error("facegen-bake-target-chain-invalid",
                    $"Winning NPC {ownerPlugin.Value}|{npc.FormId} has an override chain that does not bind origin to winner inside the explicit load order."));
                continue;
            }
            if (!identities.Add((ownerPlugin.Value, npc.FormId.Value)))
            {
                diagnostics.Add(Error("facegen-bake-target-duplicate",
                    $"The discovered winner set repeats {ownerPlugin.Value}|{npc.FormId}."));
                continue;
            }

            targets.Add(new FaceGenBakeTarget(
                npc.FormId,
                ownerPlugin,
                npc.Plugin,
                chain,
                npc.EditorId,
                npc.Name,
                sex,
                race,
                metadata.HeadParts,
                weight));
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        ImmutableArray<FaceGenBakeTarget> ordered = targets
            .OrderBy(item => pluginIndexes[item.WinningPlugin.Value])
            .ThenBy(item => item.FormId.Value)
            .ThenBy(item => item.OriginatingPlugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (ordered.IsEmpty)
        {
            string scope = request.WinningPlugin is { } plugin
                ? $" won by '{plugin.Value}'"
                : string.Empty;
            diagnostics.Add(Error("facegen-bake-targets-empty",
                $"The explicit load order contains no parseable, non-deleted NPC winners{scope}."));
            return Refused(diagnostics);
        }

        diagnostics.Add(new Diagnostic("facegen-bake-targets-discovered",
            DiagnosticSeverity.Info,
            $"Discovered {ordered.Length} ordered NPC winner(s) from the explicit copied-Data load order."));
        return new FaceGenBakeTargetDiscoveryResult(true, ordered,
            diagnostics.ToImmutable());
    }

    private static void ValidateRequest(
        FaceGenBakeTargetDiscoveryRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("facegen-bake-data-root-missing",
                "The copied Data root must already exist."));
        else
        {
            try
            {
                if (File.GetAttributes(request.DataRoot.Value)
                    .HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(Error("facegen-bake-data-root-reparse",
                        "The copied Data root may not be a reparse point."));
            }
            catch (IOException exception)
            {
                diagnostics.Add(Error("facegen-bake-data-root-stat-failed",
                    exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(Error("facegen-bake-data-root-stat-denied",
                    exception.Message));
            }
        }

        if (request.PluginOrder.IsDefaultOrEmpty)
            diagnostics.Add(Error("facegen-bake-plugin-order-required",
                "A real FaceGen batch requires an explicit copied-Data load order."));
        else if (request.PluginOrder.Select(item => item.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                 request.PluginOrder.Length)
            diagnostics.Add(Error("facegen-bake-plugin-order-duplicate",
                "The explicit load order may not repeat a plugin name."));

        if (request.WinningPlugin is { } target &&
            !request.PluginOrder.Any(item => string.Equals(item.Value,
                target.Value, StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error("facegen-bake-filter-not-loaded",
                $"Winning-plugin filter '{target.Value}' is not present in the explicit load order."));
    }

    private static void ValidateInventoryEnvelope(
        FaceGenBakeTargetDiscoveryRequest request,
        GameInventory inventory,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (inventory.Edition != request.Edition)
            diagnostics.Add(Error("facegen-bake-inventory-edition-drift",
                "NPC inventory returned a different game edition than the discovery request."));
        if (!inventory.Plugins.Select(item => item.Value).SequenceEqual(
                request.PluginOrder.Select(item => item.Value),
                StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(Error("facegen-bake-inventory-order-drift",
                "NPC inventory did not retain the exact explicit copied-Data load order."));
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static FaceGenBakeTargetDiscoveryResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, ImmutableArray<FaceGenBakeTarget>.Empty,
            diagnostics.ToImmutable());

    private sealed class BakeTargetIdentityComparer :
        IEqualityComparer<(string Owner, uint FormId)>
    {
        public static BakeTargetIdentityComparer Instance { get; } = new();

        public bool Equals((string Owner, uint FormId) left,
            (string Owner, uint FormId) right) =>
            left.FormId == right.FormId && string.Equals(left.Owner, right.Owner,
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Owner, uint FormId) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Owner),
                value.FormId);
    }
}
