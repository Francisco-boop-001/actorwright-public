using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

internal static class SkyrimLeveledListCycleValidator
{
    public static ImmutableArray<Diagnostic> Validate(
        FormReference target,
        FormReference candidate,
        SkyrimLeveledListCycleEvidence? evidence)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (SameReference(target, candidate))
        {
            diagnostics.Add(Error("leveled-entry-self-cycle",
                "A leveled list cannot contain itself."));
            return diagnostics.ToImmutable();
        }
        if (evidence is null || !evidence.IsComplete || evidence.Edges is null)
        {
            diagnostics.Add(Error("leveled-entry-cycle-evidence-incomplete",
                "Complete typed leveled-list adjacency evidence is required before adding an LVLI reference."));
            return diagnostics.ToImmutable();
        }
        ImmutableDictionary<FormReference, ImmutableArray<FormReference>> edgesByList =
            evidence.Edges;
        if (edgesByList.Count > 4096 || edgesByList.Any(pair =>
                pair.Value.IsDefault || pair.Value.Length > 4096) ||
            edgesByList.Sum(pair => (long)pair.Value.Length) > 16384)
        {
            diagnostics.Add(Error("leveled-entry-cycle-evidence-unbounded",
                "Leveled-list adjacency evidence exceeds the bounded authoring limit."));
            return diagnostics.ToImmutable();
        }
        if (edgesByList.Keys.Select(Canonical)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            edgesByList.Count)
        {
            diagnostics.Add(Error("leveled-entry-cycle-evidence-duplicate-node",
                "Leveled-list adjacency evidence contains a duplicate qualified node."));
            return diagnostics.ToImmutable();
        }
        if (!TryGetEdges(edgesByList, candidate, out _))
        {
            diagnostics.Add(Error("leveled-entry-cycle-candidate-missing",
                "Complete adjacency evidence must include the candidate LVLI, even when it has no entries."));
            return diagnostics.ToImmutable();
        }
        foreach ((FormReference node, ImmutableArray<FormReference> edges) in edgesByList)
        {
            SkyrimLeveledEntryEditorRules.ValidateReference(
                node, diagnostics, "adjacency node");
            foreach (FormReference edge in edges)
                SkyrimLeveledEntryEditorRules.ValidateReference(
                    edge, diagnostics, "adjacency edge");
        }
        if (SkyrimLeveledEntryEditorRules.HasErrors(diagnostics))
            return diagnostics.ToImmutable();

        var pending = new Stack<FormReference>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(candidate);
        while (pending.Count > 0)
        {
            FormReference current = pending.Pop();
            if (SameReference(current, target))
            {
                diagnostics.Add(Error("leveled-entry-transitive-cycle",
                    "That LVLI reference would create a transitive leveled-list cycle."));
                return diagnostics.ToImmutable();
            }
            if (!visited.Add(Canonical(current))) continue;
            if (!TryGetEdges(edgesByList, current,
                    out ImmutableArray<FormReference> next)) continue;
            for (int index = next.Length - 1; index >= 0; index--)
                pending.Push(next[index]);
        }
        return diagnostics.ToImmutable();
    }

    private static bool TryGetEdges(
        ImmutableDictionary<FormReference, ImmutableArray<FormReference>> edges,
        FormReference reference,
        out ImmutableArray<FormReference> value)
    {
        foreach ((FormReference key, ImmutableArray<FormReference> current) in edges)
        {
            if (!SameReference(key, reference)) continue;
            value = current;
            return true;
        }
        value = [];
        return false;
    }

    private static string Canonical(FormReference reference) =>
        reference.Plugin.Value + "|" + reference.FormId;

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static Diagnostic Error(string code, string message) =>
        SkyrimLeveledEntryEditorRules.Error(code, message);
}
