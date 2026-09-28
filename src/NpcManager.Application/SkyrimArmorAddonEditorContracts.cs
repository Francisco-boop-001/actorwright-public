using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimArmorAddonEditorIntent
{
    BlankNew,
    NewFromTemplate,
    OverrideExisting,
    EditAuthored
}

public sealed record SkyrimArmorAddonEditorDocument(
    GameEdition Edition,
    SkyrimArmorAddonEditorIntent Intent,
    ArmorAddonProposalMode Mode,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    EditorId EditorId,
    FormId? TargetFormId,
    PluginName? TargetPlugin,
    bool SeedFromSource,
    string? MaleModel,
    string? FemaleModel,
    string? MaleFirstPersonModel,
    string? FemaleFirstPersonModel,
    uint SlotMask,
    FormReference? Race,
    ImmutableArray<FormReference> AdditionalRaces,
    FormReference? MaleSkinTexture,
    FormReference? FemaleSkinTexture,
    FormReference? MaleSkinTextureSwapList,
    FormReference? FemaleSkinTextureSwapList,
    FormReference? FootstepSet,
    FormReference? ArtObject,
    byte MalePriority,
    byte FemalePriority,
    bool MaleWeightSliderEnabled,
    bool FemaleWeightSliderEnabled,
    byte DetectionSound,
    double WeaponAdjust);

public sealed record SkyrimArmorAddonEditorResult(
    bool Accepted,
    SkyrimArmorAddonEditorDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimArmorAddonProposalAdapterResult(
    bool Accepted,
    ArmorAddonProposalRequest? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

public static class SkyrimArmorAddonEditorRules
{
    public const string EditorIdPrefix = "npcm_ARMA_";
    public const double MinimumWeaponAdjust = -100000d;
    public const double MaximumWeaponAdjust = 100000d;

    public static SkyrimArmorAddonEditorResult Save(
        SkyrimArmorAddonEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorAddonEditorValidation.Validate(document, existingEditorIds);
        return diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? new SkyrimArmorAddonEditorResult(false, null, diagnostics)
            : new SkyrimArmorAddonEditorResult(true, document, diagnostics);
    }

    public static ImmutableArray<Diagnostic> ValidateDocument(
        SkyrimArmorAddonEditorDocument document,
        ImmutableArray<EditorId> existingEditorIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SkyrimArmorAddonEditorValidation.Validate(
            document, existingEditorIds);
    }

    public static SkyrimArmorAddonEditorResult Cancel() =>
        new(false, null, []);

    public static SkyrimArmorAddonProposalAdapterResult ToProposal(
        SkyrimArmorAddonEditorDocument document,
        WorkspacePath outputProposal)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorAddonEditorValidation.Validate(document, []);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimArmorAddonProposalAdapterResult(
                false, null, diagnostics);

        ArmorAddonProposalPatch patch = new(
            document.Mode == ArmorAddonProposalMode.New
                ? document.EditorId
                : null,
            document.SlotMask,
            document.Race,
            document.FootstepSet,
            document.MalePriority,
            document.FemalePriority,
            document.MaleWeightSliderEnabled ? (byte)1 : (byte)0,
            document.FemaleWeightSliderEnabled ? (byte)1 : (byte)0,
            document.DetectionSound,
            document.WeaponAdjust,
            document.MaleModel,
            document.FemaleModel,
            document.MaleFirstPersonModel,
            document.FemaleFirstPersonModel,
            null,
            null,
            null,
            null,
            document.MaleSkinTexture,
            document.FemaleSkinTexture,
            document.MaleSkinTextureSwapList,
            document.FemaleSkinTextureSwapList,
            null,
            null,
            null,
            null,
            document.ArtObject,
            document.AdditionalRaces,
            null,
            null,
            null,
            null);
        ArmorAddonProposalRequest proposal = new(
            document.Edition,
            document.SourcePlugin,
            document.SourceFormId,
            document.Mode,
            patch,
            outputProposal,
            document.Mode == ArmorAddonProposalMode.New
                ? document.TargetFormId
                : null,
            CompleteDocument: true,
            SeedFromSource: document.SeedFromSource,
            TargetPlugin: document.Mode == ArmorAddonProposalMode.New
                ? document.TargetPlugin
                : null);
        return new SkyrimArmorAddonProposalAdapterResult(
            true, proposal, diagnostics);
    }

    public static SkyrimArmorAddonReferenceTransition ToReferenceRow(
        SkyrimArmorAddonEditorDocument document,
        FormReference owningArmorRace,
        WorkspacePath outputProposal)
    {
        ArgumentNullException.ThrowIfNull(document);
        SkyrimArmorAddonProposalAdapterResult adapted =
            ToProposal(document, outputProposal);
        if (!adapted.Accepted || adapted.Proposal is null)
            return new SkyrimArmorAddonReferenceTransition(
                false, false, null, adapted.Diagnostics);
        if (document.Race is not { } primaryRace)
            return Refused("armor-addon-editor-primary-race",
                "An Armor-addon must have a primary race before it can be used by an Armor.");

        PluginName owner = document.Mode == ArmorAddonProposalMode.New
            ? document.TargetPlugin ?? throw new InvalidOperationException(
                "A validated new Armor-addon lost its target plugin identity.")
            : new PluginName(Path.GetFileName(document.SourcePlugin.Value));
        FormId localId = document.Mode == ArmorAddonProposalMode.New
            ? document.TargetFormId!.Value
            : document.SourceFormId;
        SkyrimArmorAddonReferenceRow row = new(
            new FormReference(owner, localId),
            document.EditorId.Value,
            new SkyrimArmorAddonRaceEvidence(
                true,
                owningArmorRace,
                primaryRace,
                document.AdditionalRaces),
            adapted.Proposal);
        return SkyrimArmorAddonReferenceEditorRules.Accept(
            owningArmorRace, row);
    }

    private static SkyrimArmorAddonReferenceTransition Refused(
        string code,
        string message) =>
        new(false, false, null,
        [
            new Diagnostic(code, DiagnosticSeverity.Error, message)
        ]);
}
