using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

internal static class SkyrimArmorEditorValidation
{
    private const int MaximumCollectionItems = 255;

    public static ImmutableArray<Diagnostic> Validate(
        SkyrimArmorEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (document.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-editor-edition",
                "The Skyrim Armor editor accepts Skyrim Special Edition documents only."));
        if (!Enum.IsDefined(document.Intent) || !Enum.IsDefined(document.Mode))
            diagnostics.Add(Error("armor-editor-intent",
                "Armor intent and proposal mode must be recognized values."));
        ValidateIntent(document, diagnostics);
        ValidateIdentity(document, existingEditorIds, diagnostics);
        ValidateText(document.Name, 512, "name", allowLineBreaks: false, diagnostics);
        ValidateText(document.Description, 4096, "description", allowLineBreaks: true, diagnostics);
        ValidateReference(document.Race, "race", diagnostics);
        ValidateOptionalReference(document.Enchantment, "enchantment", diagnostics);
        ValidateOptionalReference(document.PickupSound, "pickup sound", diagnostics);
        ValidateOptionalReference(document.DropSound, "drop sound", diagnostics);
        ValidateOptionalReference(document.EquipmentType, "equipment type", diagnostics);
        ValidateOptionalReference(document.AlternateBlockMaterial, "alternate block material", diagnostics);
        ValidateOptionalReference(document.TemplateArmor, "template armor", diagnostics);
        if (!double.IsFinite(document.Weight) || document.Weight is < 0 or > 1_000_000)
            diagnostics.Add(Error("armor-editor-weight",
                "Weight must be finite and within 0 through 1000000."));
        if (!double.IsFinite(document.ArmorRating) || document.ArmorRating is < 0 or > 65_535)
            diagnostics.Add(Error("armor-editor-rating",
                "Armor rating must be finite and within 0 through 65535."));
        ValidateBounds(document.ObjectBounds, diagnostics);
        ValidateModel(document.MaleWorldModel, "male", diagnostics);
        ValidateModel(document.FemaleWorldModel, "female", diagnostics);
        ValidateReferences(document.ArmorAddons, "armor addon", allowDuplicates: true, diagnostics);
        ValidateReferences(document.Keywords, "keyword", allowDuplicates: false, diagnostics);
        ValidateAuthoredArmorAddons(document, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void ValidateAuthoredArmorAddons(
        SkyrimArmorEditorDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document.AuthoredArmorAddons.IsDefault) return;
        foreach (SkyrimArmorAddonReferenceRow row in document.AuthoredArmorAddons)
        {
            diagnostics.AddRange(
                SkyrimArmorAddonReferenceEditorRules.ValidateRow(
                    document.Race, row));
            if (!document.ArmorAddons.Any(reference =>
                    string.Equals(ReferenceKey(reference), ReferenceKey(row.Reference),
                        StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(Error("armor-editor-authored-addon-orphan",
                    "A nested authored ARMA row is not present in the Armor armature."));
        }
        if (document.AuthoredArmorAddons.Select(row => ReferenceKey(row.Reference))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            document.AuthoredArmorAddons.Length)
            diagnostics.Add(Error("armor-editor-authored-addon-duplicate",
                "Each qualified ARMA may carry at most one nested authored proposal."));
    }

    private static void ValidateIntent(
        SkyrimArmorEditorDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool newIntent = document.Intent is SkyrimArmorEditorIntent.BlankNew or
            SkyrimArmorEditorIntent.NewFromTemplate;
        if (newIntent && document.Mode != ArmorProposalMode.New)
            diagnostics.Add(Error("armor-editor-new-intent-mode",
                "Blank and template-copy intents must create a new ARMO."));
        if (document.Intent == SkyrimArmorEditorIntent.OverrideExisting &&
            document.Mode != ArmorProposalMode.Override)
            diagnostics.Add(Error("armor-editor-override-intent-mode",
                "Override intent must retain the existing ARMO identity."));
    }

    private static void ValidateIdentity(
        SkyrimArmorEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document.SourceFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("armor-editor-source-form",
                "The source ARMO requires a nonzero plugin-local 24-bit FormID."));
        if (document.Mode == ArmorProposalMode.New)
        {
            string editorId = document.EditorId.Value;
            if (!editorId.StartsWith(SkyrimArmorEditorRules.EditorIdPrefix,
                    StringComparison.Ordinal) ||
                editorId.Length == SkyrimArmorEditorRules.EditorIdPrefix.Length)
                diagnostics.Add(Error("armor-editor-new-editor-id",
                    $"A new ARMO EditorID must use {SkyrimArmorEditorRules.EditorIdPrefix}<name>."));
            if (document.TargetFormId is not { Value: > 0 and <= 0x00FF_FFFF })
                diagnostics.Add(Error("armor-editor-target-form",
                    "A new ARMO requires a nonzero plugin-local 24-bit target FormID."));
            if (!existingEditorIds.IsDefault && existingEditorIds.Any(item =>
                    string.Equals(item.Value, editorId, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(Error("armor-editor-editor-id-duplicate",
                    $"EditorID '{editorId}' is already in use."));
        }
        else
        {
            if (document.TargetFormId is not null &&
                document.TargetFormId != document.SourceFormId)
                diagnostics.Add(Error("armor-editor-override-target",
                    "An override must retain the source ARMO FormID."));
            if (document.Intent is SkyrimArmorEditorIntent.BlankNew or
                SkyrimArmorEditorIntent.NewFromTemplate)
                diagnostics.Add(Error("armor-editor-override-new-intent",
                    "An override cannot use a new-record intent."));
        }
    }

    private static void ValidateText(
        string? value,
        int maximumLength,
        string role,
        bool allowLineBreaks,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value is null || value.Length > maximumLength || value.Any(character =>
                char.IsControl(character) &&
                !(allowLineBreaks && character is '\r' or '\n' or '\t')))
            diagnostics.Add(Error($"armor-editor-{role}",
                $"Armor {role} must be initialized, contain no unsupported controls, and not exceed {maximumLength} characters."));
    }

    private static void ValidateBounds(
        ArmorObjectBounds bounds,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.MinimumX > bounds.MaximumX ||
            bounds.MinimumY > bounds.MaximumY ||
            bounds.MinimumZ > bounds.MaximumZ)
            diagnostics.Add(Error("armor-editor-bounds-order",
                "Each object-bounds minimum must be less than or equal to its corresponding maximum."));
    }

    private static void ValidateModel(
        string? value,
        string gender,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var path = new AssetPath(value);
            if (!path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ||
                path.Value.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "World models must be .nif paths relative to Meshes.",
                    nameof(value));
        }
        catch (ArgumentException)
        {
            diagnostics.Add(Error($"armor-editor-{gender}-model",
                $"The {gender} world model must be a safe .nif path relative to Meshes."));
        }
    }

    private static void ValidateReferences(
        ImmutableArray<FormReference> references,
        string role,
        bool allowDuplicates,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (references.IsDefault)
        {
            diagnostics.Add(Error($"armor-editor-{role.Replace(' ', '-')}-uninitialized",
                $"The {role} collection must be initialized."));
            return;
        }
        if (references.Length > MaximumCollectionItems)
            diagnostics.Add(Error($"armor-editor-{role.Replace(' ', '-')}-limit",
                $"The {role} collection may not exceed {MaximumCollectionItems} rows."));
        foreach (FormReference reference in references)
            ValidateReference(reference, role, diagnostics);
        if (!allowDuplicates && references.Select(ReferenceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Length)
            diagnostics.Add(Error($"armor-editor-{role.Replace(' ', '-')}-duplicate",
                $"The {role} collection may not contain duplicates."));
    }

    internal static void ValidateReference(
        FormReference reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.FormId.Value is > 0 and <= 0x00FF_FFFF &&
            !string.IsNullOrWhiteSpace(reference.Plugin.Value)) return;
        diagnostics.Add(Error("armor-editor-reference",
            $"The {role} requires a provider and a nonzero plugin-local 24-bit FormID."));
    }

    private static void ValidateOptionalReference(
        FormReference? reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference is { } value) ValidateReference(value, role, diagnostics);
    }

    internal static string ReferenceKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    internal static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
