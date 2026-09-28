using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Plans the additional-master closure without reading or mutating workspace state.
/// </summary>
public static class SkyrimNpcFinishCoreMasterPlanner
{
    public static IEnumerable<string> RequiredMasterNames(
        SkyrimNpcFinishCoreRequest request)
    {
        yield return "Skyrim.esm";
        if (request.OutfitPolicy.ExistingOutfit is { } existing)
            yield return existing.Plugin.Value;
        foreach (FormReference item in request.OutfitPolicy.ArmorItems)
            yield return item.Plugin.Value;
        if (!request.PerkPolicy.IsDefault)
            foreach (SkyrimNpcFinishCorePerk perk in request.PerkPolicy)
                yield return perk.Form.Plugin.Value;
        foreach (string item in request.InventoryPolicy.DesiredItems)
        {
            int separator = item.IndexOf('|');
            if (separator > 0)
                yield return item[..separator];
        }
        yield return request.SandboxAuthority.Template.Plugin.Value;
    }

    public static SkyrimNpcFinishCoreMasterPlan Plan(
        SkyrimNpcFinishCoreRequest request,
        ImmutableArray<PluginName> sourceMasters,
        ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster> verifiedAdditionalMasters)
    {
        if (!TryBuildRequiredReferenceOwners(
                request,
                out ImmutableArray<PluginName> requiredReferenceOwners,
                out string? invalidOwner))
        {
            return new SkyrimNpcFinishCoreMasterPlan(
                false,
                ImmutableArray<PluginName>.Empty,
                ImmutableArray<PluginName>.Empty,
                [Error(
                    "finish-core-master-owner-invalid",
                    $"The required reference owner is not a valid plugin identity: value={invalidOwner}.")]);
        }

        if (request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier)
            return BuildLegacyMasterPlan(sourceMasters, requiredReferenceOwners);

        return SkyrimNpcFinishCoreMasterPlanner.Plan(
            new SkyrimNpcFinishCoreMasterPlanRequest(
                request.Source.Plugin!.Value,
                new PluginName(request.Output.PluginFileName),
                sourceMasters,
                requiredReferenceOwners,
                verifiedAdditionalMasters));
    }

    private static SkyrimNpcFinishCoreMasterPlan BuildLegacyMasterPlan(
        ImmutableArray<PluginName> sourceMasters,
        ImmutableArray<PluginName> requiredReferenceOwners)
    {
        var seen = new HashSet<string>(
            sourceMasters.Select(value => value.Value),
            StringComparer.OrdinalIgnoreCase);
        var appended = ImmutableArray.CreateBuilder<PluginName>();
        foreach (PluginName owner in requiredReferenceOwners)
        {
            if (seen.Add(owner.Value))
                appended.Add(owner);
        }

        ImmutableArray<PluginName> appendedMasters = appended.ToImmutable();
        return new SkyrimNpcFinishCoreMasterPlan(
            true,
            sourceMasters.Concat(appendedMasters).ToImmutableArray(),
            appendedMasters,
            ImmutableArray<Diagnostic>.Empty)
        {
            SourceMasterPrefix = sourceMasters,
            MasterLoadOrderIndexes = Enumerable.Range(
                    0, sourceMasters.Length + appendedMasters.Length)
                .ToImmutableArray()
        };
    }

    private static bool TryBuildRequiredReferenceOwners(
        SkyrimNpcFinishCoreRequest request,
        out ImmutableArray<PluginName> owners,
        out string? invalidOwner)
    {
        var values = ImmutableArray.CreateBuilder<PluginName>();
        foreach (string owner in RequiredMasterNames(request))
        {
            if ((request.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier ||
                 request.Schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier) &&
                (string.Equals(owner, request.Source.Plugin?.Value, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(owner, request.Output.PluginFileName, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                values.Add(new PluginName(owner));
            }
            catch (ArgumentException)
            {
                owners = ImmutableArray<PluginName>.Empty;
                invalidOwner = owner;
                return false;
            }
        }

        owners = values
            .DistinctBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        invalidOwner = null;
        return true;
    }

    public static SkyrimNpcFinishCoreMasterPlan Plan(
        SkyrimNpcFinishCoreMasterPlanRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableArray<PluginName> sourceMasters = request.SourceMasters.IsDefault
            ? ImmutableArray<PluginName>.Empty
            : request.SourceMasters;
        ImmutableArray<PluginName> owners = request.RequiredReferenceOwners.IsDefault
            ? ImmutableArray<PluginName>.Empty
            : request.RequiredReferenceOwners;
        ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster> rows =
            request.AdditionalMasters.IsDefault
                ? ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster>.Empty
                : request.AdditionalMasters;

        AddDuplicateDiagnostics(
            sourceMasters,
            value => Error(
                "finish-core-master-duplicate-source",
                $"The source master list contains duplicate plugin identity={value}."),
            diagnostics);
        foreach (PluginName sourceMaster in sourceMasters)
        {
            if (SamePlugin(sourceMaster, request.SourcePlugin) ||
                SamePlugin(sourceMaster, request.OutputPlugin))
            {
                diagnostics.Add(Error(
                    "finish-core-master-source-output-collision",
                    $"The source master prefix may not contain the source or output plugin identity={sourceMaster.Value}."));
            }
        }

        var rowsByPlugin = new Dictionary<string, SkyrimNpcFinishCoreVerifiedAdditionalMaster>(
            StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Additional indices identify the copied global load order. Source
        // masters supply only TES4 slots, so those two index domains cannot collide.
        var indexes = new HashSet<int>();
        foreach (SkyrimNpcFinishCoreVerifiedAdditionalMaster row in rows)
        {
            string plugin = row.Plugin.Value ?? string.Empty;
            string path = row.Path.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(row.Sha256.Value) || row.ByteLength <= 0 ||
                row.LoadOrderIndex < 0)
            {
                diagnostics.Add(Error(
                    "finish-core-master-identity-missing",
                    "Every verified additional master must have plugin, path, hash, byte length, and non-negative load-order identity."));
                continue;
            }

            if (!string.Equals(
                    Path.GetFileName(path), plugin, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "finish-core-master-identity-mismatch",
                    $"The additional-master path identity does not match the plugin identity={plugin}."));
            }

            if (SamePlugin(row.Plugin, request.SourcePlugin) ||
                SamePlugin(row.Plugin, request.OutputPlugin))
            {
                diagnostics.Add(Error(
                    "finish-core-master-self-dependency",
                    $"Additional-master identity={plugin} may not be the source or output plugin."));
            }

            if (sourceMasters.Any(master => SamePlugin(master, row.Plugin)))
            {
                diagnostics.Add(Error(
                    "finish-core-master-source-collision",
                    $"Additional-master identity={plugin} collides with the immutable source master prefix."));
            }

            if (!rowsByPlugin.TryAdd(plugin, row))
            {
                diagnostics.Add(Error(
                    "finish-core-master-duplicate-plugin",
                    $"The additional-master authority contains duplicate plugin identity={plugin}."));
            }

            if (!paths.Add(path))
            {
                diagnostics.Add(Error(
                    "finish-core-master-duplicate-path",
                    $"The additional-master authority contains duplicate path={path}."));
            }

            if (!indexes.Add(row.LoadOrderIndex))
            {
                diagnostics.Add(Error(
                    "finish-core-master-duplicate-index",
                    $"The additional-master authority contains duplicate load-order index={row.LoadOrderIndex}."));
            }
        }

        var sourceSet = new HashSet<string>(
            sourceMasters.Select(master => master.Value),
            StringComparer.OrdinalIgnoreCase);
        foreach (PluginName owner in owners)
        {
            if (sourceSet.Contains(owner.Value))
                continue;

            if (!rowsByPlugin.ContainsKey(owner.Value))
            {
                diagnostics.Add(Error(
                    "finish-core-master-owner-unresolved",
                    $"The required reference owner is not present in SourceMasters or admitted additional-master authority: identity={owner.Value}."));
            }
        }

        foreach ((string plugin, SkyrimNpcFinishCoreVerifiedAdditionalMaster row) in rowsByPlugin)
        {
            ImmutableArray<PluginName> dependencies = row.MasterDependencies.IsDefault
                ? ImmutableArray<PluginName>.Empty
                : row.MasterDependencies;
            foreach (PluginName dependency in dependencies)
            {
                if (SamePlugin(dependency, request.OutputPlugin) ||
                    SamePlugin(dependency, request.SourcePlugin))
                {
                    diagnostics.Add(Error(
                        "finish-core-master-self-dependency",
                        $"Additional master identity={plugin} may not depend on the source or output plugin identity={dependency.Value}."));
                }

                if (!sourceSet.Contains(dependency.Value) &&
                    !rowsByPlugin.ContainsKey(dependency.Value))
                {
                    diagnostics.Add(Error(
                        "finish-core-master-unresolved",
                        $"Additional master identity={plugin} declares an unresolved dependency identity={dependency.Value}."));
                }
            }
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginName owner in owners)
        {
            if (!sourceSet.Contains(owner.Value) && rowsByPlugin.ContainsKey(owner.Value))
                roots.Add(owner.Value);
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
            CollectReachable(root, rowsByPlugin, sourceSet, reachable, diagnostics);

        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        foreach (string plugin in rowsByPlugin.Keys.OrderBy(
                     value => value, StringComparer.OrdinalIgnoreCase))
            DetectCycles(plugin, rowsByPlugin, sourceSet, state, diagnostics);

        foreach (string plugin in rowsByPlugin.Keys)
        {
            SkyrimNpcFinishCoreVerifiedAdditionalMaster row = rowsByPlugin[plugin];
            foreach (PluginName dependency in row.MasterDependencies)
            {
                if (!rowsByPlugin.TryGetValue(dependency.Value, out var dependencyRow))
                    continue;

                if (dependencyRow.LoadOrderIndex >= row.LoadOrderIndex)
                {
                    diagnostics.Add(Error(
                        "finish-core-master-index-order",
                        $"Additional master identity={plugin} must have a higher load-order index than dependency identity={dependency.Value}."));
                }
            }
        }

        if (diagnostics.Count > 0)
        {
            return new SkyrimNpcFinishCoreMasterPlan(
                false,
                ImmutableArray<PluginName>.Empty,
                ImmutableArray<PluginName>.Empty,
                diagnostics.ToImmutable());
        }

        var indegree = reachable.ToDictionary(
            plugin => plugin,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var dependents = reachable.ToDictionary(
            plugin => plugin,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (string plugin in reachable)
        {
            foreach (PluginName dependency in rowsByPlugin[plugin].MasterDependencies)
            {
                if (!reachable.Contains(dependency.Value))
                    continue;

                indegree[plugin]++;
                dependents[dependency.Value].Add(plugin);
            }
        }

        var available = new PriorityQueue<string, (int Index, string Plugin)>(
            Comparer<(int Index, string Plugin)>.Create((left, right) =>
            {
                int index = left.Index.CompareTo(right.Index);
                return index != 0
                    ? index
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Plugin, right.Plugin);
            }));
        foreach (string plugin in reachable)
        {
            if (indegree[plugin] == 0)
            {
                available.Enqueue(
                    plugin,
                    (rowsByPlugin[plugin].LoadOrderIndex, plugin));
            }
        }

        var appended = ImmutableArray.CreateBuilder<PluginName>();
        while (available.TryDequeue(out string? plugin, out _))
        {
            appended.Add(rowsByPlugin[plugin].Plugin);
            foreach (string dependent in dependents[plugin]
                         .OrderBy(value => rowsByPlugin[value].LoadOrderIndex)
                         .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    available.Enqueue(
                        dependent,
                        (rowsByPlugin[dependent].LoadOrderIndex, dependent));
                }
            }
        }

        if (appended.Count != reachable.Count)
        {
            diagnostics.Add(Error(
                "finish-core-master-cycle",
                "The additional-master dependency graph contains a cycle."));
            return new SkyrimNpcFinishCoreMasterPlan(
                false,
                ImmutableArray<PluginName>.Empty,
                ImmutableArray<PluginName>.Empty,
                diagnostics.ToImmutable());
        }

        ImmutableArray<PluginName> appendedMasters = appended.ToImmutable();
        ImmutableArray<int> masterLoadOrderIndexes =
            Enumerable.Range(0, sourceMasters.Length)
                .Concat(appendedMasters.Select(plugin =>
                    rowsByPlugin[plugin.Value].LoadOrderIndex))
                .ToImmutableArray();
        return new SkyrimNpcFinishCoreMasterPlan(
            true,
            sourceMasters.Concat(appendedMasters).ToImmutableArray(),
            appendedMasters,
            ImmutableArray<Diagnostic>.Empty)
        {
            SourceMasterPrefix = sourceMasters,
            MasterLoadOrderIndexes = masterLoadOrderIndexes
        };
    }

    private static void CollectReachable(
        string plugin,
        IReadOnlyDictionary<string, SkyrimNpcFinishCoreVerifiedAdditionalMaster> rows,
        ISet<string> sourceMasters,
        ISet<string> reachable,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sourceMasters.Contains(plugin) || !reachable.Add(plugin))
            return;

        if (!rows.TryGetValue(plugin, out var row))
            return;

        foreach (PluginName dependency in row.MasterDependencies)
        {
            if (!sourceMasters.Contains(dependency.Value) &&
                !rows.ContainsKey(dependency.Value))
            {
                diagnostics.Add(Error(
                    "finish-core-master-unresolved",
                    $"Additional master identity={plugin} declares an unresolved dependency identity={dependency.Value}."));
                continue;
            }

            CollectReachable(dependency.Value, rows, sourceMasters, reachable, diagnostics);
        }
    }

    private static void DetectCycles(
        string plugin,
        IReadOnlyDictionary<string, SkyrimNpcFinishCoreVerifiedAdditionalMaster> rows,
        ISet<string> sourceMasters,
        IDictionary<string, VisitState> state,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sourceMasters.Contains(plugin))
            return;

        if (state.TryGetValue(plugin, out VisitState existing))
        {
            if (existing == VisitState.Active)
            {
                diagnostics.Add(Error(
                    "finish-core-master-cycle",
                    $"The additional-master dependency graph contains a cycle at identity={plugin}."));
            }

            return;
        }

        if (!rows.TryGetValue(plugin, out var row))
            return;

        state[plugin] = VisitState.Active;
        foreach (PluginName dependency in row.MasterDependencies)
        {
            if (!sourceMasters.Contains(dependency.Value))
                DetectCycles(dependency.Value, rows, sourceMasters, state, diagnostics);
        }

        state[plugin] = VisitState.Complete;
    }

    private static void AddDuplicateDiagnostics(
        IEnumerable<PluginName> values,
        Func<string, Diagnostic> factory,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginName value in values)
        {
            if (!seen.Add(value.Value))
                diagnostics.Add(factory(value.Value));
        }
    }

    private static bool SamePlugin(PluginName left, PluginName right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private enum VisitState
    {
        Active,
        Complete
    }
}
