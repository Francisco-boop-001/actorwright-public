using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimFaceEditorDocumentRules
{
    public static SkyrimFaceEditorDocument ReplaceHeadPart(
        SkyrimFaceEditorDocument document,
        NpcHeadPartSelection selection)
    {
        RequireValid(document);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Type == NpcHeadPartType.Misc)
            return AddMiscHeadPart(document, selection.Reference);
        RequireReference(selection.Reference, nameof(selection));
        var parts = document.Parts.OrderedHeadParts.ToBuilder();
        int existing = FindIndex(parts, item => item.Type == selection.Type);
        int duplicate = FindIndex(parts, item => SameReference(item.Reference, selection.Reference));
        if (duplicate >= 0 && duplicate != existing)
            throw new ArgumentException("A head-part FormReference may appear only once.", nameof(selection));
        if (existing >= 0) parts[existing] = selection;
        else parts.Add(selection);
        return WithParts(document, document.Parts with
        {
            OrderedHeadParts = parts.ToImmutable()
        });
    }

    public static SkyrimFaceEditorDocument AddMiscHeadPart(
        SkyrimFaceEditorDocument document,
        FormReference reference)
    {
        RequireValid(document);
        RequireReference(reference, nameof(reference));
        if (document.Parts.OrderedHeadParts.Any(item =>
                SameReference(item.Reference, reference)))
        {
            throw new ArgumentException(
                "The same Misc head-part FormReference cannot be added twice.",
                nameof(reference));
        }
        if (document.Parts.OrderedHeadParts.Length >= MaximumHeadParts)
            throw new InvalidOperationException("The face document reached its head-part limit.");
        return WithParts(document, document.Parts with
        {
            OrderedHeadParts = document.Parts.OrderedHeadParts.Add(
                new NpcHeadPartSelection(reference, NpcHeadPartType.Misc))
        });
    }

    public static SkyrimFaceEditorDocument RemoveHeadPart(
        SkyrimFaceEditorDocument document,
        FormReference reference,
        ImmutableArray<FormReference> provenOrphanedMisc)
    {
        RequireValid(document);
        RequireReference(reference, nameof(reference));
        if (provenOrphanedMisc.IsDefault)
            throw new ArgumentException("Orphan evidence must be initialized.", nameof(provenOrphanedMisc));
        var removed = document.Parts.OrderedHeadParts
            .Where(item => !SameReference(item.Reference, reference) &&
                           !provenOrphanedMisc.Any(orphan =>
                               item.Type == NpcHeadPartType.Misc &&
                               SameReference(item.Reference, orphan)))
            .ToImmutableArray();
        return removed.Length == document.Parts.OrderedHeadParts.Length
            ? document
            : WithParts(document, document.Parts with { OrderedHeadParts = removed });
    }

    public static SkyrimFaceEditorDocument SetHairColor(
        SkyrimFaceEditorDocument document,
        OptionalFormReference value)
    {
        RequireValid(document);
        if (value.Value is { } reference) RequireReference(reference, nameof(value));
        return WithParts(document, document.Parts with { HairColor = value });
    }

    public static SkyrimFaceEditorDocument SetHeadTexture(
        SkyrimFaceEditorDocument document,
        FormReference? value)
    {
        RequireValid(document);
        if (value is { } reference) RequireReference(reference, nameof(value));
        return WithParts(document, document.Parts with { HeadTexture = value });
    }

    public static SkyrimFaceEditorDocument SetCharGenFlag(
        SkyrimFaceEditorDocument document,
        bool value)
    {
        RequireValid(document);
        return WithParts(document, document.Parts with { IsCharGenFacePreset = value });
    }

    private static void ValidateParts(
        SkyrimFaceEditorParts parts,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parts is null || parts.OrderedHeadParts.IsDefault ||
            parts.OrderedHeadParts.Length > MaximumHeadParts)
        {
            diagnostics.Add(Error("face-editor-parts-shape",
                $"Head parts must be initialized and contain at most {MaximumHeadParts} rows."));
            return;
        }

        ImmutableArray<NpcHeadPartSelection> validRows = parts.OrderedHeadParts
            .Where(item => item is not null)
            .ToImmutableArray();
        if (validRows.Length != parts.OrderedHeadParts.Length ||
            validRows.Any(item => !ValidReference(item.Reference) || !Enum.IsDefined(item.Type)))
        {
            diagnostics.Add(Error("face-editor-headpart-reference", "A head-part selection is invalid."));
        }
        if (validRows.GroupBy(item =>
                $"{item.Reference.Plugin.Value.ToUpperInvariant()}|{item.Reference.FormId.Value:X8}",
                StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            diagnostics.Add(Error("face-editor-headpart-duplicate", "Head-part references must be unique."));
        }
        if (validRows.Where(item => item.Type != NpcHeadPartType.Misc)
            .GroupBy(item => item.Type).Any(group => group.Count() > 1))
        {
            diagnostics.Add(Error("face-editor-headpart-type-duplicate",
                "At most one NPC-owned head part may be selected per non-Misc type."));
        }
        if (parts.HairColor.Value is { } hair && !ValidReference(hair))
            diagnostics.Add(Error("face-editor-hair-color", "The hair-color reference is invalid."));
        if (parts.HeadTexture is { } texture && !ValidReference(texture))
            diagnostics.Add(Error("face-editor-head-texture", "The head-texture reference is invalid."));
    }

    private static SkyrimFaceEditorDocument WithParts(
        SkyrimFaceEditorDocument document,
        SkyrimFaceEditorParts parts) => document with { Parts = parts };
}
