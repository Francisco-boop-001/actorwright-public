using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimArmorEditorIntent
{
    BlankNew,
    NewFromTemplate,
    OverrideExisting,
    EditAuthored
}

public sealed record SkyrimArmorEditorDocument(
    GameEdition Edition,
    SkyrimArmorEditorIntent Intent,
    ArmorProposalMode Mode,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    EditorId EditorId,
    FormId? TargetFormId,
    string Name,
    FormReference Race,
    FormReference? Enchantment,
    bool NonPlayable,
    string Description,
    uint Value,
    double Weight,
    double ArmorRating,
    uint SlotMask,
    FormReference? PickupSound,
    FormReference? DropSound,
    FormReference? EquipmentType,
    FormReference? AlternateBlockMaterial,
    ArmorObjectBounds ObjectBounds,
    string? MaleWorldModel,
    string? FemaleWorldModel,
    FormReference? TemplateArmor,
    ImmutableArray<FormReference> ArmorAddons,
    ImmutableArray<FormReference> Keywords,
    ImmutableArray<SkyrimArmorAddonReferenceRow> AuthoredArmorAddons = default);

public sealed record SkyrimArmorEditorResult(
    bool Accepted,
    SkyrimArmorEditorDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimArmorProposalAdapterResult(
    bool Accepted,
    ArmorProposalRequest? Proposal,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<ArmorAddonProposalRequest> AuthoredArmorAddons = default);

public sealed record SkyrimArmorAddonSlotEvidence(
    FormReference ArmorAddon,
    uint SlotMask);

public static class SkyrimArmorEditorRules
{
    public const string EditorIdPrefix = "npcm_ARMO_";

    public static SkyrimArmorEditorResult Save(
        SkyrimArmorEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorEditorValidation.Validate(document, existingEditorIds);
        return diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? new SkyrimArmorEditorResult(false, null, diagnostics)
            : new SkyrimArmorEditorResult(true, document, diagnostics);
    }

    public static ImmutableArray<Diagnostic> ValidateDocument(
        SkyrimArmorEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SkyrimArmorEditorValidation.Validate(document, existingEditorIds);
    }

    public static SkyrimArmorEditorResult Cancel() => new(false, null, []);

    public static SkyrimArmorProposalAdapterResult ToProposal(
        SkyrimArmorEditorDocument document,
        WorkspacePath outputProposal)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorEditorValidation.Validate(document, []);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimArmorProposalAdapterResult(false, null, diagnostics);

        var proposal = new ArmorProposalRequest(
            document.Edition,
            document.SourcePlugin,
            document.SourceFormId,
            document.Mode,
            document.Mode == ArmorProposalMode.New ? document.EditorId : null,
            document.Name,
            document.SlotMask,
            document.Race,
            document.MaleWorldModel,
            document.FemaleWorldModel,
            document.Value,
            document.Weight,
            null,
            document.ArmorRating,
            document.Keywords,
            document.ArmorAddons.Select(reference =>
                    new ArmorAddonProposal(0, reference))
                .ToImmutableArray(),
            outputProposal,
            document.Mode == ArmorProposalMode.New
                ? document.TargetFormId
                : null,
            document.Description,
            document.NonPlayable,
            document.Enchantment,
            document.PickupSound,
            document.DropSound,
            document.EquipmentType,
            document.AlternateBlockMaterial,
            document.TemplateArmor,
            document.ObjectBounds,
            CompleteDocument: true);
        ImmutableArray<ArmorAddonProposalRequest> authoredArmorAddons =
            document.AuthoredArmorAddons.IsDefault
                ? []
                : document.AuthoredArmorAddons
                    .Where(row => row.AuthoredProposal is not null)
                    .Select(row => row.AuthoredProposal!)
                    .ToImmutableArray();
        return new SkyrimArmorProposalAdapterResult(
            true, proposal, diagnostics, authoredArmorAddons);
    }
}
