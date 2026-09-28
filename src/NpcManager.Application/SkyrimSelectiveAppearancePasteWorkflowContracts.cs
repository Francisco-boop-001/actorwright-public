using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One exact endpoint in a selective-paste transaction. The NPC plugin and
/// RaceMenu preset are separate, hash-bound carriers; the combined document is
/// the modal's complete immutable view over both.
/// </summary>
public sealed record SkyrimSelectiveAppearancePasteEndpoint(
    PluginName Plugin,
    WorkspacePath PluginPath,
    Sha256Hash PluginSha256,
    FormId NpcFormId,
    EditorId EditorId,
    FormReference Race,
    NpcSex Sex,
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    WorkspacePath PresetPath,
    PresetDocument Preset,
    SkyrimSelectiveAppearancePasteDocument Document);

public sealed record SkyrimSelectiveAppearancePasteLoadedState(
    SkyrimSelectiveAppearancePasteEndpoint Source,
    SkyrimSelectiveAppearancePasteEndpoint Target);

public sealed record SkyrimSelectiveAppearancePasteLoadRequest(
    ReviewedGameIntake Intake,
    PluginName SourcePlugin,
    FormId SourceNpcFormId,
    WorkspacePath SourcePreset,
    PluginName TargetPlugin,
    FormId TargetNpcFormId,
    WorkspacePath TargetPreset);

public sealed record SkyrimSelectiveAppearancePasteLoadResult(
    bool Accepted,
    SkyrimSelectiveAppearancePasteLoadedState? State,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimSelectiveAppearancePasteLoadService
{
    ValueTask<SkyrimSelectiveAppearancePasteLoadResult> LoadAsync(
        SkyrimSelectiveAppearancePasteLoadRequest request,
        CancellationToken cancellationToken);
}

public interface ISkyrimSelectiveAppearanceNpcStateReader
{
    NpcOutfitSnapshot ReadOutfits(
        WorkspacePath pluginPath,
        FormId npcFormId);
}

public sealed record SkyrimSelectiveAppearancePasteProjectionRequest(
    SkyrimSelectiveAppearancePasteLoadedState State,
    SkyrimSelectiveAppearancePasteSelection Selection,
    WorkspacePath PluginProposalPath,
    WorkspacePath OutputPlugin);

public sealed record SkyrimSelectiveAppearancePasteProjectionResult(
    bool Accepted,
    SkyrimSelectiveAppearancePasteDocument? AcceptedDocument,
    NpcAppearanceOverrideRequest? PluginRequest,
    ImmutableArray<PresetCopySection> PresetSections,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimSelectiveAppearancePasteTransactionRequest(
    SkyrimSelectiveAppearancePasteLoadedState State,
    SkyrimSelectiveAppearancePasteSelection Selection,
    WorkspacePath TransactionProposalPath,
    WorkspacePath PluginProposalPath,
    WorkspacePath OutputPlugin,
    WorkspacePath OutputPreset);

public sealed record SkyrimSelectiveAppearancePasteProposal(
    int SchemaVersion,
    WorkspacePath TransactionProposalPath,
    Sha256Hash? TransactionProposalSha256,
    WorkspacePath OutputPreset,
    SkyrimSelectiveAppearancePasteSelection Selection,
    NpcAppearanceOverrideProposal PluginProposal,
    ImmutableArray<PresetCopySection> PresetSections,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable =>
        TransactionProposalSha256 is not null &&
        PluginProposal.IsApplicable &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record SkyrimSelectiveAppearancePasteVerificationResult(
    bool IsValid,
    NpcAppearanceOverrideVerificationResult? PluginVerification,
    WorkspacePath OutputPreset,
    Sha256Hash? OutputPresetSha256,
    bool SelectedPresetSectionsMatchSource,
    bool UncheckedPresetSectionsPreserveTarget,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimSelectiveAppearancePasteTransactionResult(
    bool Applied,
    SkyrimSelectiveAppearancePasteProposal Proposal,
    SkyrimSelectiveAppearancePasteVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimSelectiveAppearancePasteTransactionService
{
    ValueTask<SkyrimSelectiveAppearancePasteProposal> AnalyzeAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimSelectiveAppearancePasteTransactionResult> ApplyAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<SkyrimSelectiveAppearancePasteVerificationResult> VerifyAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed bridge from the ten-category modal to the two concrete carrier
/// writers. Target race, sex, QNAM, and runtime payload remain target-owned;
/// every unchecked category is supplied from the target document.
/// </summary>
public static class SkyrimSelectiveAppearancePasteProjector
{
    public static SkyrimSelectiveAppearancePasteProjectionResult Project(
        SkyrimSelectiveAppearancePasteProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.State);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Selection.Categories.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "selective-paste-projection-no-selection",
                "Select at least one appearance category before creating a write transaction."));
            return Refused(diagnostics);
        }

        SkyrimSelectiveAppearancePasteResult merged =
            SkyrimSelectiveAppearancePasteRules.Merge(
                request.State.Source.Document,
                request.State.Target.Document,
                request.Selection);
        diagnostics.AddRange(merged.Diagnostics);
        if (!merged.Accepted || merged.Document is null)
            return Refused(diagnostics);

        SkyrimSelectiveAppearancePasteDocument accepted = merged.Document;
        SkyrimFaceEditorParts parts = accepted.Face.Parts;
        if (!parts.HairColor.IsSpecified || parts.HairColor.Value is null)
            diagnostics.Add(Error(
                "selective-paste-projection-hair-required",
                "The complete appearance override requires one typed HCLF reference."));
        if (parts.HeadTexture is null)
            diagnostics.Add(Error(
                "selective-paste-projection-head-texture-required",
                "The complete appearance override requires one typed FTST reference."));
        ImmutableArray<SkyrimFaceTintLayer> authoredTints = accepted.Face.Tints
            .Where(item => item.IsAuthored)
            .Select(item => item.Value)
            .ToImmutableArray();
        if (authoredTints.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                "selective-paste-projection-tints-required",
                "The complete appearance override requires at least one authored native tint layer."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        FullyAuthoredSkyrimNpcAppearanceSource targetAppearance =
            request.State.Target.Appearance;
        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            parts.OrderedHeadParts.Select(item =>
                    (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
                .ToImmutableArray(),
            new ExternalSkyrimNpcHairColor(parts.HairColor.Value!.Value),
            new ExternalSkyrimNpcFaceTextureSet(parts.HeadTexture!.Value),
            accepted.Body.Weight,
            accepted.Face.NativeMorphs,
            new SkyrimFaceTintPatch(authoredTints),
            targetAppearance.Qnam);

        NpcOutfitPatch? outfits = request.Selection.Includes(
                SkyrimAppearancePasteCategory.Outfits)
            ? new NpcOutfitPatch(
                ToOptional(accepted.Outfits.DefaultOutfit),
                ToOptional(accepted.Outfits.SleepingOutfit))
            : null;
        bool? charGen = request.Selection.Includes(
                SkyrimAppearancePasteCategory.CharGenFlag)
            ? accepted.Face.Parts.IsCharGenFacePreset
            : null;
        var pluginRequest = new NpcAppearanceOverrideRequest(
            GameEdition.SkyrimSpecialEdition,
            request.State.Target.PluginPath,
            request.State.Target.PluginSha256,
            request.State.Target.NpcFormId,
            request.PluginProposalPath,
            request.OutputPlugin,
            request.State.Target.Race,
            request.State.Target.Sex,
            appearance,
            request.State.Target.RuntimeAppearance,
            outfits,
            charGen);
        ImmutableArray<PresetCopySection> sections = MapPresetSections(
            request.Selection);
        return new SkyrimSelectiveAppearancePasteProjectionResult(
            true,
            accepted,
            pluginRequest,
            sections,
            diagnostics.ToImmutable());
    }

    public static ImmutableArray<PresetCopySection> MapPresetSections(
        SkyrimSelectiveAppearancePasteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var sections = ImmutableArray.CreateBuilder<PresetCopySection>();
        foreach (SkyrimAppearancePasteCategory category in
                 Enum.GetValues<SkyrimAppearancePasteCategory>())
        {
            if (!selection.Includes(category)) continue;
            switch (category)
            {
                case SkyrimAppearancePasteCategory.BodyWeight:
                    sections.Add(PresetCopySection.BodyWeight);
                    break;
                case SkyrimAppearancePasteCategory.BodyShape:
                    sections.Add(PresetCopySection.BodySliders);
                    break;
                case SkyrimAppearancePasteCategory.RaceMenuPaintsAndSkin:
                    sections.Add(PresetCopySection.Overlays);
                    break;
                case SkyrimAppearancePasteCategory.FaceParts:
                    sections.Add(PresetCopySection.FaceParts);
                    break;
                case SkyrimAppearancePasteCategory.HairColor:
                    sections.Add(PresetCopySection.HairColor);
                    break;
                case SkyrimAppearancePasteCategory.FaceTints:
                    sections.Add(PresetCopySection.FaceTints);
                    break;
                case SkyrimAppearancePasteCategory.FaceMorphs:
                    sections.Add(PresetCopySection.FaceVertexMorphs);
                    break;
                case SkyrimAppearancePasteCategory.Sculpt:
                    sections.Add(PresetCopySection.Sculpt);
                    break;
                case SkyrimAppearancePasteCategory.Outfits:
                case SkyrimAppearancePasteCategory.CharGenFlag:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(selection), category, null);
            }
        }
        return sections.ToImmutable();
    }

    private static OptionalFormReference ToOptional(FormReference? value) =>
        value is { } reference
            ? OptionalFormReference.Set(reference)
            : OptionalFormReference.Clear();

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimSelectiveAppearancePasteProjectionResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, [], diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
