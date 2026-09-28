using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Immutable header for a new Skyrim LVLI. Entry authoring is deliberately
/// separate so this document can be owned and rolled back by an outer outfit
/// transaction without registering or writing anything.
/// </summary>
public sealed record SkyrimLeveledListEditorDocument(
    string NameSuffix,
    EditorId EditorId,
    byte ChanceNone,
    byte MaxCount,
    bool CalculateAllLevels,
    bool CalculateEachInCount,
    bool UseAll,
    ImmutableArray<LeveledListEntryProposal> Entries)
{
    public byte PackedFlags => (byte)(
        (CalculateAllLevels ? 0x01 : 0) |
        (CalculateEachInCount ? 0x02 : 0) |
        (UseAll ? 0x04 : 0));
}

public sealed record SkyrimLeveledListEditorResult(
    bool Accepted,
    SkyrimLeveledListEditorDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimLeveledListOutfitItemResult(
    bool Accepted,
    SkyrimOutfitEditorItem? Item,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Pure rules for the small Skyrim leveled-list creation dialog. These rules
/// allocate no FormIDs and perform no file, plugin, preview, or runtime work.
/// </summary>
public static class SkyrimLeveledListEditorRules
{
    public const string EditorIdPrefix = "npcm_LVLI_";

    public static SkyrimLeveledListEditorResult Create(
        GameEdition edition,
        string? nameSuffix,
        int chanceNone,
        int maxCount,
        bool calculateAllLevels,
        bool calculateEachInCount,
        bool useAll,
        ImmutableArray<EditorId> existingEditorIds)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("leveled-list-editor-edition-invalid",
                "The Skyrim leveled-list editor accepts only Skyrim Special Edition."));
        if (existingEditorIds.IsDefault)
            diagnostics.Add(Error("leveled-list-editor-existing-ids-uninitialized",
                "The existing leveled-list EditorID catalog must be initialized."));

        string suffix = nameSuffix?.Trim() ?? string.Empty;
        if (suffix.Length == 0)
            diagnostics.Add(Error("leveled-list-editor-name-required",
                "Enter a name for the leveled list."));
        if (suffix.Any(char.IsControl) || suffix.IndexOfAny(['\\', '/']) >= 0)
            diagnostics.Add(Error("leveled-list-editor-name-invalid",
                "The leveled-list name may not contain control characters or path separators."));

        EditorId? editorId = null;
        if (suffix.Length > 0)
        {
            try { editorId = new EditorId(EditorIdPrefix + suffix); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("leveled-list-editor-id-invalid", exception.Message));
            }
        }
        if (editorId is { } complete && !existingEditorIds.IsDefault &&
            existingEditorIds.Any(existing => string.Equals(
                existing.Value, complete.Value, StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error("leveled-list-editor-id-duplicate",
                $"EditorID '{complete.Value}' is already in use."));
        if (chanceNone is < 0 or > 100)
            diagnostics.Add(Error("leveled-list-editor-chance-range",
                "Chance None must be from 0 through 100 percent."));
        if (maxCount is < 0 or > byte.MaxValue)
            diagnostics.Add(Error("leveled-list-editor-max-count-range",
                "Max Count must be from 0 through 255."));

        if (HasErrors(diagnostics) || editorId is null)
            return Refused(diagnostics);

        var document = new SkyrimLeveledListEditorDocument(
            suffix,
            editorId.Value,
            (byte)chanceNone,
            (byte)maxCount,
            calculateAllLevels,
            calculateEachInCount,
            useAll,
            []);
        return new(true, document, diagnostics.ToImmutable());
    }

    public static SkyrimLeveledListEditorResult Cancel() =>
        new(false, null, []);

    public static ImmutableArray<Diagnostic> ValidateDocument(
        SkyrimLeveledListEditorDocument? document)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (document is null)
        {
            diagnostics.Add(Error("leveled-list-editor-document-missing",
                "The leveled-list document is missing."));
            return diagnostics.ToImmutable();
        }

        if (document.NameSuffix.Length == 0 ||
            !string.Equals(
                document.EditorId.Value,
                EditorIdPrefix + document.NameSuffix,
                StringComparison.Ordinal))
            diagnostics.Add(Error("leveled-list-editor-document-identity-invalid",
                "The complete EditorID must exactly match the leveled-list prefix and name."));
        if (document.ChanceNone > 100)
            diagnostics.Add(Error("leveled-list-editor-chance-range",
                "Chance None must be from 0 through 100 percent."));
        if (document.Entries.IsDefault)
            diagnostics.Add(Error("leveled-list-editor-entries-uninitialized",
                "The authored leveled-list entry sequence must be initialized."));
        else
        {
            if (document.Entries.Length > SkyrimLeveledEntryEditorRules.MaximumEntries)
                diagnostics.Add(Error("leveled-list-editor-entries-capacity",
                    $"A Skyrim authored leveled list may contain at most {SkyrimLeveledEntryEditorRules.MaximumEntries} entries."));
            foreach (LeveledListEntryProposal entry in document.Entries)
                diagnostics.AddRange(
                    SkyrimLeveledEntryEditorRules.ValidateEntry(entry));
        }
        return diagnostics.ToImmutable();
    }

    public static SkyrimLeveledListOutfitItemResult AttachToOutfit(
        SkyrimLeveledListEditorDocument? document,
        FormReference provisionalReference)
    {
        var diagnostics = ValidateDocument(document).ToBuilder();
        if (document is not null && !document.Entries.IsDefaultOrEmpty)
            diagnostics.Add(Error("leveled-list-editor-attach-not-empty",
                "The creation editor attaches a new LVLI before entry authoring begins."));
        if (provisionalReference.FormId.Value is 0 or > 0x00FF_FFFF ||
            string.IsNullOrWhiteSpace(provisionalReference.Plugin.Value))
            diagnostics.Add(Error("leveled-list-editor-reference-invalid",
                "The new LVLI requires a provider and a nonzero plugin-local 24-bit provisional FormID."));
        if (HasErrors(diagnostics) || document is null)
            return new(false, null, diagnostics.ToImmutable());

        var item = new SkyrimOutfitEditorItem(
            provisionalReference,
            SkyrimOutfitEditorItemKind.LeveledList,
            document.EditorId.Value,
            0,
            null,
            [],
            document);
        return new(true, item, diagnostics.ToImmutable());
    }

    private static SkyrimLeveledListEditorResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
