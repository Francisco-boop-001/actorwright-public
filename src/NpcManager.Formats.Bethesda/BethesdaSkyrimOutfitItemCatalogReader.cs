using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads the exact copied Skyrim plugin order and exposes winning ARMO/LVLI
/// records to the outfit editor. LVLI terminal realization is deterministic,
/// bounded, and advisory only; the saved OTFT continues to reference the LVLI.
/// </summary>
public sealed class BethesdaSkyrimOutfitItemCatalogReader :
    ISkyrimOutfitItemCatalogReader
{
    private const int MaximumDepth = 32;
    private const int MaximumVisitedNodes = 4096;
    private const int MaximumTerminalArmors = 128;

    public SkyrimOutfitItemCatalogResult Read(
        SkyrimOutfitItemCatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        CatalogSnapshot? snapshot = ReadSnapshot(
            request.DataRoot,
            request.PluginOrder,
            diagnostics);
        if (snapshot is null || HasErrors(diagnostics))
            return new(false, [], diagnostics.ToImmutable());

        var items = ImmutableArray.CreateBuilder<SkyrimOutfitEditorItem>();
        foreach (ArmorSnapshot armor in snapshot.Armors.Values
                     .Where(item => !item.IsDeleted)
                     .OrderBy(item => item.Reference.Plugin.Value,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Reference.FormId.Value))
        {
            items.Add(new SkyrimOutfitEditorItem(
                armor.Reference,
                SkyrimOutfitEditorItemKind.Armor,
                armor.DisplayName,
                armor.SlotMask));
        }

        foreach (LeveledSnapshot leveled in snapshot.LeveledItems.Values
                     .Where(item => !item.IsDeleted)
                     .OrderBy(item => item.Reference.Plugin.Value,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Reference.FormId.Value))
        {
            ImmutableArray<SkyrimOutfitPreviewArmor> realization = Resolve(
                snapshot,
                leveled.Reference,
                request.InitialSeed,
                diagnostics);
            uint mask = realization.Aggregate(0U,
                (current, armor) => current | armor.SlotMask);
            items.Add(new SkyrimOutfitEditorItem(
                leveled.Reference,
                SkyrimOutfitEditorItemKind.LeveledList,
                leveled.DisplayName,
                mask,
                request.InitialSeed,
                realization));
        }

        return new(!HasErrors(diagnostics), items.ToImmutable(),
            diagnostics.ToImmutable());
    }

    public ImmutableArray<SkyrimOutfitPreviewArmor> Resolve(
        SkyrimOutfitPreviewResolveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        CatalogSnapshot? snapshot = ReadSnapshot(
            request.DataRoot,
            request.PluginOrder,
            diagnostics);
        if (snapshot is null || HasErrors(diagnostics)) return [];
        return Resolve(snapshot, request.LeveledList, request.Seed, diagnostics);
    }

    private static CatalogSnapshot? ReadSnapshot(
        WorkspacePath dataRoot,
        ImmutableArray<PluginName> pluginOrder,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(dataRoot.Value))
        {
            diagnostics.Add(Error("outfit-catalog-data-root-missing",
                "The copied Skyrim Data root does not exist."));
            return null;
        }
        if (pluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("outfit-catalog-order-empty",
                "The reviewed Skyrim plugin order is empty."));
            return null;
        }
        if (pluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            pluginOrder.Length)
        {
            diagnostics.Add(Error("outfit-catalog-order-duplicate",
                "The reviewed Skyrim plugin order contains duplicate names."));
            return null;
        }

        var armors = new Dictionary<FormKey, ArmorSnapshot>();
        var leveledItems = new Dictionary<FormKey, LeveledSnapshot>();
        foreach (PluginName plugin in pluginOrder)
        {
            string path = Path.Combine(dataRoot.Value, plugin.Value);
            if (!File.Exists(path))
            {
                diagnostics.Add(Error("outfit-catalog-plugin-missing",
                    $"Reviewed plugin '{plugin}' is missing from the copied Data root."));
                continue;
            }
            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    new ModPath(ModKey.FromNameAndExtension(plugin.Value),
                        new FilePath(path)),
                    SkyrimRelease.SkyrimSE);
                foreach (IArmorGetter armor in mod.Armors)
                {
                    var reference = Reference(armor.FormKey);
                    armors[armor.FormKey] = new ArmorSnapshot(
                        reference,
                        DisplayName(armor.Name?.String, armor.EditorID, reference),
                        armor.BodyTemplate is null
                            ? 0U
                            : (uint)armor.BodyTemplate.FirstPersonFlags,
                        armor.IsDeleted);
                }
                foreach (ILeveledItemGetter leveled in mod.LeveledItems)
                {
                    var reference = Reference(leveled.FormKey);
                    ImmutableArray<LeveledEntrySnapshot> entries =
                        (leveled.Entries ?? [])
                        .Where(entry => entry.Data is not null &&
                                        !entry.Data.Reference.FormKey.IsNull)
                        .Select(entry => new LeveledEntrySnapshot(
                            entry.Data!.Reference.FormKey,
                            entry.Data.Level,
                            entry.Data.Count))
                        .ToImmutableArray();
                    leveledItems[leveled.FormKey] = new LeveledSnapshot(
                        reference,
                        DisplayName(null, leveled.EditorID, reference),
                        leveled.Flags.HasFlag(LeveledItem.Flag.UseAll),
                        checked((byte)Math.Round(
                            leveled.ChanceNone.Value * 100D,
                            MidpointRounding.AwayFromZero)),
                        entries,
                        leveled.IsDeleted);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException)
            {
                diagnostics.Add(Error("outfit-catalog-plugin-read-failed",
                    $"Reviewed plugin '{plugin}' could not be read: {exception.Message}"));
            }
        }
        return HasErrors(diagnostics) ? null : new(armors, leveledItems);
    }

    private static ImmutableArray<SkyrimOutfitPreviewArmor> Resolve(
        CatalogSnapshot snapshot,
        FormReference reference,
        long seed,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        FormKey root;
        try { root = FormKeyOf(reference); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("outfit-catalog-lvli-reference-invalid",
                exception.Message));
            return [];
        }
        if (!snapshot.LeveledItems.TryGetValue(root, out LeveledSnapshot? list) ||
            list.IsDeleted)
        {
            diagnostics.Add(Error("outfit-catalog-lvli-unresolved",
                $"LVLI {reference} did not resolve to one winning live record."));
            return [];
        }

        ulong randomState = unchecked((ulong)seed) ^ 0x9E3779B97F4A7C15UL;
        int visited = 0;
        var stack = new HashSet<FormKey>();
        var terminal = new Dictionary<FormKey, SkyrimOutfitPreviewArmor>();
        ResolveList(snapshot, list, 0, stack, terminal, ref visited,
            ref randomState, diagnostics);
        return terminal.Values
            .OrderBy(item => item.Reference.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reference.FormId.Value)
            .ToImmutableArray();
    }

    private static void ResolveList(
        CatalogSnapshot snapshot,
        LeveledSnapshot list,
        int depth,
        HashSet<FormKey> stack,
        Dictionary<FormKey, SkyrimOutfitPreviewArmor> terminal,
        ref int visited,
        ref ulong randomState,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        FormKey key = FormKeyOf(list.Reference);
        if (depth >= MaximumDepth)
        {
            diagnostics.Add(Error("outfit-catalog-lvli-depth-limit",
                $"LVLI {list.Reference} exceeded the {MaximumDepth}-level preview limit."));
            return;
        }
        if (++visited > MaximumVisitedNodes)
        {
            diagnostics.Add(Error("outfit-catalog-lvli-node-limit",
                $"LVLI preview exceeded the {MaximumVisitedNodes}-node limit."));
            return;
        }
        if (!stack.Add(key))
        {
            diagnostics.Add(Error("outfit-catalog-lvli-cycle",
                $"LVLI preview detected a cycle at {list.Reference}."));
            return;
        }
        try
        {
            if (list.Entries.IsDefaultOrEmpty) return;
            if (list.ChanceNone > 0 &&
                NextBounded(ref randomState, 100) < list.ChanceNone)
                return;

            IEnumerable<LeveledEntrySnapshot> selected = list.UseAll
                ? list.Entries
                : [list.Entries[NextBounded(ref randomState, list.Entries.Length)]];
            foreach (LeveledEntrySnapshot entry in selected)
            {
                if (terminal.Count >= MaximumTerminalArmors)
                {
                    diagnostics.Add(Error("outfit-catalog-lvli-terminal-limit",
                        $"LVLI preview exceeded the {MaximumTerminalArmors}-armor limit."));
                    return;
                }
                if (snapshot.Armors.TryGetValue(entry.Reference,
                        out ArmorSnapshot? armor) && !armor.IsDeleted)
                {
                    terminal[entry.Reference] = new SkyrimOutfitPreviewArmor(
                        armor.Reference,
                        armor.SlotMask);
                    continue;
                }
                if (snapshot.LeveledItems.TryGetValue(entry.Reference,
                        out LeveledSnapshot? nested) && !nested.IsDeleted)
                {
                    ResolveList(snapshot, nested, depth + 1, stack, terminal,
                        ref visited, ref randomState, diagnostics);
                    continue;
                }
                diagnostics.Add(Error("outfit-catalog-lvli-terminal-invalid",
                    $"LVLI {list.Reference} entry {Reference(entry.Reference)} does not resolve to a live ARMO or LVLI."));
            }
        }
        finally
        {
            stack.Remove(key);
        }
    }

    private static int NextBounded(ref ulong state, int bound)
    {
        if (bound <= 0) return 0;
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (int)(value % (uint)bound);
    }

    private static string DisplayName(
        string? name,
        string? editorId,
        FormReference fallback) =>
        !string.IsNullOrWhiteSpace(name) ? name :
        !string.IsNullOrWhiteSpace(editorId) ? editorId :
        fallback.ToString();

    private static FormReference Reference(FormKey key) => new(
        new PluginName(key.ModKey.ToString()),
        new FormId(key.ID));

    private static FormKey FormKeyOf(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value),
        reference.FormId.Value);

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record CatalogSnapshot(
        Dictionary<FormKey, ArmorSnapshot> Armors,
        Dictionary<FormKey, LeveledSnapshot> LeveledItems);

    private sealed record ArmorSnapshot(
        FormReference Reference,
        string DisplayName,
        uint SlotMask,
        bool IsDeleted);

    private sealed record LeveledSnapshot(
        FormReference Reference,
        string DisplayName,
        bool UseAll,
        byte ChanceNone,
        ImmutableArray<LeveledEntrySnapshot> Entries,
        bool IsDeleted);

    private sealed record LeveledEntrySnapshot(
        FormKey Reference,
        short Level,
        short Count);
}
