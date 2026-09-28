using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

internal static class SkyrimArmorAddonEditorValidation
{
    public static ImmutableArray<Diagnostic> Validate(
        SkyrimArmorAddonEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (document.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-addon-editor-edition",
                "The Skyrim Armor-addon editor accepts Skyrim Special Edition only."));
        if (!Enum.IsDefined(document.Intent))
            diagnostics.Add(Error("armor-addon-editor-intent",
                "The Armor-addon editor intent is invalid."));
        if (!Enum.IsDefined(document.Mode))
            diagnostics.Add(Error("armor-addon-editor-mode",
                "The Armor-addon proposal mode is invalid."));
        ValidateIntent(document, diagnostics);
        ValidateIdentity(document, existingEditorIds, diagnostics);
        ValidateModel(document.MaleModel, "male third-person", diagnostics);
        ValidateModel(document.FemaleModel, "female third-person", diagnostics);
        ValidateModel(document.MaleFirstPersonModel,
            "male first-person", diagnostics);
        ValidateModel(document.FemaleFirstPersonModel,
            "female first-person", diagnostics);
        ValidateReference(document.Race, "primary race", diagnostics);
        ValidateReference(document.MaleSkinTexture,
            "male skin texture set", diagnostics);
        ValidateReference(document.FemaleSkinTexture,
            "female skin texture set", diagnostics);
        ValidateReference(document.MaleSkinTextureSwapList,
            "male skin-swap form list", diagnostics);
        ValidateReference(document.FemaleSkinTextureSwapList,
            "female skin-swap form list", diagnostics);
        ValidateReference(document.FootstepSet, "footstep set", diagnostics);
        ValidateReference(document.ArtObject, "art object", diagnostics);
        ValidateAdditionalRaces(document.AdditionalRaces, diagnostics);
        if (!double.IsFinite(document.WeaponAdjust) ||
            document.WeaponAdjust <
                SkyrimArmorAddonEditorRules.MinimumWeaponAdjust ||
            document.WeaponAdjust >
                SkyrimArmorAddonEditorRules.MaximumWeaponAdjust)
            diagnostics.Add(Error("armor-addon-editor-weapon-adjust",
                "Weapon adjustment must be finite and within -100000..100000."));
        return diagnostics.ToImmutable();
    }

    private static void ValidateIntent(
        SkyrimArmorAddonEditorDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool valid = document.Intent switch
        {
            SkyrimArmorAddonEditorIntent.BlankNew =>
                document.Mode == ArmorAddonProposalMode.New &&
                !document.SeedFromSource,
            SkyrimArmorAddonEditorIntent.NewFromTemplate =>
                document.Mode == ArmorAddonProposalMode.New &&
                document.SeedFromSource,
            SkyrimArmorAddonEditorIntent.OverrideExisting =>
                document.Mode == ArmorAddonProposalMode.Override &&
                !document.SeedFromSource,
            SkyrimArmorAddonEditorIntent.EditAuthored =>
                document.Mode == ArmorAddonProposalMode.New ||
                (document.Mode == ArmorAddonProposalMode.Override &&
                 !document.SeedFromSource),
            _ => false
        };
        if (!valid)
            diagnostics.Add(Error("armor-addon-editor-intent-mode",
                "The Armor-addon intent, proposal mode, and source-seeding state disagree."));
    }

    private static void ValidateIdentity(
        SkyrimArmorAddonEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document.SourceFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("armor-addon-editor-source-form",
                "The source ARMA requires a nonzero plugin-local 24-bit FormID."));
        if (document.Mode == ArmorAddonProposalMode.New)
        {
            if (!document.EditorId.Value.StartsWith(
                    SkyrimArmorAddonEditorRules.EditorIdPrefix,
                    StringComparison.Ordinal))
                diagnostics.Add(Error("armor-addon-editor-id-prefix",
                    $"New Armor-addon EditorIDs must begin with {SkyrimArmorAddonEditorRules.EditorIdPrefix}."));
            if (document.Intent != SkyrimArmorAddonEditorIntent.EditAuthored &&
                existingEditorIds.Any(item => string.Equals(
                    item.Value, document.EditorId.Value,
                    StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(Error("armor-addon-editor-id-duplicate",
                    "The new Armor-addon EditorID already exists."));
            if (document.TargetFormId is not { Value: > 0 and <= 0x00FF_FFFF })
                diagnostics.Add(Error("armor-addon-editor-target-form",
                    "A new Armor-addon requires a nonzero plugin-local 24-bit target FormID."));
            if (document.TargetPlugin is null)
                diagnostics.Add(Error("armor-addon-editor-target-plugin",
                    "A new Armor-addon requires its target plugin identity."));
        }
        else if (document.TargetFormId is not null ||
                 document.TargetPlugin is not null)
            diagnostics.Add(Error("armor-addon-editor-override-target",
                "An Armor-addon override retains its source identity and may not declare a new target."));
    }

    private static void ValidateModel(
        string? value,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value is null) return;
        string normalized = value.Replace('\\', '/');
        bool invalid = string.IsNullOrWhiteSpace(value) ||
                       value.Length > 512 ||
                       value.Any(char.IsControl) ||
                       Path.IsPathRooted(value) ||
                       normalized.StartsWith("meshes/",
                           StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains(':') ||
                       normalized.Split('/',
                               StringSplitOptions.RemoveEmptyEntries)
                           .Any(segment => segment is "." or "..") ||
                       !normalized.EndsWith(".nif",
                           StringComparison.OrdinalIgnoreCase);
        if (invalid)
            diagnostics.Add(Error("armor-addon-editor-model-path",
                $"The {role} model must be a safe .nif path relative to Meshes without a visible meshes/ prefix."));
    }

    private static void ValidateAdditionalRaces(
        ImmutableArray<FormReference> races,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (races.IsDefault)
        {
            diagnostics.Add(Error("armor-addon-editor-races-default",
                "Additional races must be initialized, even when empty."));
            return;
        }
        foreach (FormReference race in races)
            ValidateReference(race, "additional race", diagnostics);
        if (races.Select(ReferenceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            races.Length)
            diagnostics.Add(Error("armor-addon-editor-races-duplicate",
                "Additional races must be ordered and unique."));
    }

    private static void ValidateReference(
        FormReference? reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference is null) return;
        if (reference.Value.FormId.Value is > 0 and <= 0x00FF_FFFF)
            return;
        diagnostics.Add(Error("armor-addon-editor-reference",
            $"The {role} requires a nonzero plugin-local 24-bit FormID."));
    }

    private static string ReferenceKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
