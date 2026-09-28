using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record NpcWeightPatch(float? SkyrimValue, float? Thin, float? Muscular, float? Fat)
{
    public bool IsEmpty => SkyrimValue is null && Thin is null && Muscular is null && Fat is null;
}

public readonly record struct OptionalFormReference
{
    private OptionalFormReference(bool isSpecified, FormReference? value)
    {
        IsSpecified = isSpecified;
        Value = value;
    }

    public bool IsSpecified { get; }

    public FormReference? Value { get; }

    public static OptionalFormReference Set(FormReference value) => new(true, value);

    public static OptionalFormReference Clear() => new(true, null);
}

/// <summary>
/// Explicit translated NPC text intent. An unspecified value preserves the
/// source subrecord; a specified null or empty value removes it.
/// </summary>
public readonly record struct OptionalNpcText
{
    private OptionalNpcText(bool isSpecified, string? value)
    {
        if (value is { Length: > 255 } || value?.Any(char.IsControl) == true)
            throw new ArgumentException(
                "NPC text must be at most 255 characters and contain no control characters.",
                nameof(value));
        IsSpecified = isSpecified;
        Value = value;
    }

    public bool IsSpecified { get; }

    public string? Value { get; }

    public static OptionalNpcText Set(string value) => new(true, value);

    public static OptionalNpcText Clear() => new(true, null);
}

public sealed record NpcEditableNamesPatch(
    OptionalNpcText FullName,
    OptionalNpcText ShortName)
{
    public bool IsEmpty => !FullName.IsSpecified && !ShortName.IsSpecified;
}

public sealed record NpcArchetypeReferences(
    FormReference? Race,
    FormReference? Voice,
    FormReference? Class,
    FormReference? CombatStyle)
{
    public bool IsEmpty => Race is null && Voice is null && Class is null && CombatStyle is null;
}

public sealed record NpcArchetypePatch(
    OptionalFormReference Race,
    OptionalFormReference Voice,
    OptionalFormReference Class,
    OptionalFormReference CombatStyle)
{
    public bool IsEmpty => !Race.IsSpecified && !Voice.IsSpecified && !Class.IsSpecified && !CombatStyle.IsSpecified;
}

public sealed record NpcMutationRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    EditorId? EditorId,
    NpcName? Name,
    NpcWeightPatch? Weight,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath,
    NpcSex? Sex = null,
    NpcArchetypePatch? Archetype = null,
    NpcStatsPatch? Stats = null,
    NpcKeywordPatch? KeywordPatch = null,
    NpcFactionPatch? FactionPatch = null,
    NpcInventoryPatch? InventoryPatch = null,
    NpcOutfitPatch? OutfitPatch = null,
    NpcPerkPatch? PerkPatch = null,
    NpcActorEffectPatch? ActorEffectPatch = null,
    NpcPropertyPatch? PropertyPatch = null,
    NpcSkinPatch? Skin = null,
    NpcBodyMorphPatch? BodyMorphs = null,
    NpcEditableNamesPatch? Names = null,
    NpcWholeSkinPatch? WholeSkin = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NpcAidtPatch? Aidt = null);

public sealed record MutationChange(string Field, string? Before, string? After);

public sealed record NpcMutationProposal(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    Sha256Hash InputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NpcWholeSkinPlan? WholeSkin { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NpcAidtPatch? Aidt { get; init; }
    public const string SchemaIdentifier = "npc.patch.proposal.v1";

    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record NpcMutationResult(
    bool Applied,
    NpcMutationProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record PluginVerificationRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    ImmutableArray<MutationChange> ExpectedChanges,
    ImmutableArray<string> PreservedFields)
{
    public NpcWholeSkinPatch? WholeSkin { get; init; }
}

public sealed record PluginVerificationResult(
    bool IsValid,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<MutationChange> ObservedChanges);

public interface INpcMutationService
{
    ValueTask<NpcMutationProposal> AnalyzeAsync(NpcMutationRequest request, CancellationToken cancellationToken);

    ValueTask<NpcMutationResult> ApplyAsync(NpcMutationRequest request, NpcMutationProposal proposal, CancellationToken cancellationToken);

    ValueTask<PluginVerificationResult> VerifyAsync(PluginVerificationRequest request, CancellationToken cancellationToken);
}
