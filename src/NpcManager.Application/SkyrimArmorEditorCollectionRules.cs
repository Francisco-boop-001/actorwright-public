using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class SkyrimArmorEditorCollectionRules
{
    private const int MaximumRows = 255;

    public static SkyrimArmorEditorResult AddArmorAddon(
        SkyrimArmorEditorDocument document,
        FormReference reference) =>
        ReplaceArmorAddons(document, document.ArmorAddons.Add(reference));

    public static SkyrimArmorEditorResult ReplaceArmorAddon(
        SkyrimArmorEditorDocument document,
        int index,
        FormReference reference)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (index < 0 || index >= document.ArmorAddons.Length)
            return Refused("armor-editor-addon-index",
                "The selected armor-addon row no longer exists.");
        FormReference previous = document.ArmorAddons[index];
        SkyrimArmorEditorResult result = ReplaceArmorAddons(document,
            document.ArmorAddons.SetItem(index, reference));
        return result.Document is not { } replaced ||
               replaced.ArmorAddons.Any(item => SameReference(item, previous))
            ? result
            : Accepted(replaced with
            {
                AuthoredArmorAddons = RemoveAuthored(
                    replaced.AuthoredArmorAddons, previous)
            });
    }

    public static SkyrimArmorEditorResult RemoveArmorAddon(
        SkyrimArmorEditorDocument document,
        int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (index < 0 || index >= document.ArmorAddons.Length)
            return Refused("armor-editor-addon-index",
                "The selected armor-addon row no longer exists.");
        FormReference removed = document.ArmorAddons[index];
        SkyrimArmorEditorDocument result = document with
        {
            ArmorAddons = document.ArmorAddons.RemoveAt(index)
        };
        if (!result.ArmorAddons.Any(item => SameReference(item, removed)))
            result = result with
            {
                AuthoredArmorAddons = RemoveAuthored(
                    result.AuthoredArmorAddons, removed)
            };
        return Accepted(result);
    }

    public static SkyrimArmorEditorResult ApplyArmorAddonReferenceRow(
        SkyrimArmorEditorDocument document,
        int index,
        SkyrimArmorAddonReferenceRow row)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(row);
        ImmutableArray<Diagnostic> rowDiagnostics =
            SkyrimArmorAddonReferenceEditorRules.ValidateRow(
                document.Race, row);
        if (rowDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimArmorEditorResult(false, null, rowDiagnostics);
        SkyrimArmorEditorResult replaced = ReplaceArmorAddon(
            document, index, row.Reference);
        if (!replaced.Accepted || replaced.Document is null) return replaced;
        ImmutableArray<SkyrimArmorAddonReferenceRow> authored =
            RemoveAuthored(replaced.Document.AuthoredArmorAddons, row.Reference);
        if (row.AuthoredProposal is not null) authored = authored.Add(row);
        return Accepted(replaced.Document with { AuthoredArmorAddons = authored });
    }

    public static SkyrimArmorEditorResult AddArmorAddonReferenceRow(
        SkyrimArmorEditorDocument document,
        SkyrimArmorAddonReferenceRow row)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(row);
        ImmutableArray<Diagnostic> rowDiagnostics =
            SkyrimArmorAddonReferenceEditorRules.ValidateRow(
                document.Race, row);
        if (rowDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimArmorEditorResult(false, null, rowDiagnostics);
        SkyrimArmorEditorResult added = AddArmorAddon(document, row.Reference);
        if (!added.Accepted || added.Document is null) return added;
        ImmutableArray<SkyrimArmorAddonReferenceRow> authored =
            RemoveAuthored(added.Document.AuthoredArmorAddons, row.Reference);
        if (row.AuthoredProposal is not null) authored = authored.Add(row);
        return Accepted(added.Document with { AuthoredArmorAddons = authored });
    }

    public static SkyrimArmorEditorResult MoveArmorAddon(
        SkyrimArmorEditorDocument document,
        int index,
        int destination)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (index < 0 || index >= document.ArmorAddons.Length ||
            destination < 0 || destination >= document.ArmorAddons.Length)
            return Refused("armor-editor-addon-move",
                "The armor-addon move is outside the current ordered rows.");
        if (index == destination) return Accepted(document);
        FormReference moved = document.ArmorAddons[index];
        ImmutableArray<FormReference> rows = document.ArmorAddons.RemoveAt(index);
        return Accepted(document with
        {
            ArmorAddons = rows.Insert(destination, moved)
        });
    }

    public static SkyrimArmorEditorResult AddKeyword(
        SkyrimArmorEditorDocument document,
        FormReference reference)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Keywords.Any(item => SameReference(item, reference)))
            return Refused("armor-editor-keyword-duplicate",
                "That qualified keyword is already present.");
        if (document.Keywords.Length >= MaximumRows)
            return Refused("armor-editor-keyword-limit",
                $"An armor may not contain more than {MaximumRows} keywords in this editor.");
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SkyrimArmorEditorValidation.ValidateReference(reference, "keyword", diagnostics);
        if (diagnostics.Count > 0)
            return new SkyrimArmorEditorResult(false, null, diagnostics.ToImmutable());
        return Accepted(document with { Keywords = document.Keywords.Add(reference) });
    }

    public static SkyrimArmorEditorResult RemoveKeyword(
        SkyrimArmorEditorDocument document,
        int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (index < 0 || index >= document.Keywords.Length)
            return Refused("armor-editor-keyword-index",
                "The selected keyword row no longer exists.");
        return Accepted(document with { Keywords = document.Keywords.RemoveAt(index) });
    }

    public static SkyrimArmorEditorResult RecalculateSlots(
        SkyrimArmorEditorDocument document,
        ImmutableArray<SkyrimArmorAddonSlotEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ArmorAddons.IsDefaultOrEmpty)
            return Refused("armor-editor-recalculate-empty",
                "This armor has no ARMA addons; the current BOD2 mask was preserved.");
        if (evidence.IsDefault)
            return Refused("armor-editor-recalculate-evidence-uninitialized",
                "Complete typed ARMA slot evidence is required before recalculation.");

        string[] required = document.ArmorAddons
            .Select(SkyrimArmorEditorValidation.ReferenceKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = evidence.GroupBy(item =>
                SkyrimArmorEditorValidation.ReferenceKey(item.ArmorAddon),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Any(group => group.Count() != 1) ||
            groups.Length != required.Length ||
            required.Any(key => groups.All(group =>
                !string.Equals(group.Key, key, StringComparison.OrdinalIgnoreCase))))
            return Refused("armor-editor-recalculate-evidence-incomplete",
                "Slot recalculation requires exactly one evidence row for every distinct referenced ARMA and no unrelated rows.");

        uint mask = groups.Aggregate(0U, (current, group) =>
            current | group.Single().SlotMask);
        return Accepted(document with { SlotMask = mask });
    }

    private static SkyrimArmorEditorResult ReplaceArmorAddons(
        SkyrimArmorEditorDocument document,
        ImmutableArray<FormReference> rows)
    {
        if (rows.Length > MaximumRows)
            return Refused("armor-editor-addon-limit",
                $"An armor may not contain more than {MaximumRows} ARMA rows in this editor.");
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (FormReference row in rows)
            SkyrimArmorEditorValidation.ValidateReference(
                row, "armor addon", diagnostics);
        if (diagnostics.Count > 0)
            return new SkyrimArmorEditorResult(false, null, diagnostics.ToImmutable());
        return Accepted(document with { ArmorAddons = rows });
    }

    private static bool SameReference(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static ImmutableArray<SkyrimArmorAddonReferenceRow> RemoveAuthored(
        ImmutableArray<SkyrimArmorAddonReferenceRow> authored,
        FormReference reference) =>
        authored.IsDefault
            ? []
            : authored.Where(row => !SameReference(row.Reference, reference))
                .ToImmutableArray();

    private static SkyrimArmorEditorResult Accepted(
        SkyrimArmorEditorDocument document) => new(true, document, []);

    private static SkyrimArmorEditorResult Refused(
        string code,
        string message) => new(false, null,
        [SkyrimArmorEditorValidation.Error(code, message)]);
}
