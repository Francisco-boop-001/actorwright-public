using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ExistingNpcEditStage
{
    Validate,
    Analyze,
    WritePlugin,
    VerifyPlugin,
    Package,
    Complete
}

public enum ExistingNpcEditOutputKind
{
    SourceMasteredOverride,
    StandaloneCopy
}

public sealed record ExistingNpcEditRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    Sha256Hash ExpectedInputSha256,
    FormId TargetFormId,
    WorkspacePath OutputRoot,
    PluginName OutputPlugin,
    EditorId? EditorId,
    NpcName? Name,
    NpcStatsPatch? Stats = null,
    NpcKeywordPatch? Keywords = null,
    NpcFactionPatch? Factions = null,
    NpcInventoryPatch? Inventory = null,
    NpcOutfitPatch? Outfits = null,
    NpcPerkPatch? Perks = null,
    NpcActorEffectPatch? ActorEffects = null,
    NpcEditableNamesPatch? Names = null,
    NpcArchetypePatch? Archetype = null,
    ExistingNpcEditOutputKind OutputKind = ExistingNpcEditOutputKind.SourceMasteredOverride)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspacePath? ConsolidationOutput { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<WorkspacePath> ConsolidationProviders { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EslFlag { get; init; }
}

public sealed record ExistingNpcEditInspection(
    bool Available,
    Sha256Hash? InputSha256,
    NpcOverrideSourceSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ExistingNpcEditProgress(
    ExistingNpcEditStage Stage,
    int Percent,
    string Message);

public sealed record ExistingNpcEditReview(
    bool Applicable,
    Sha256Hash? InputSha256,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ExistingNpcEditResult(
    bool Completed,
    string Verdict,
    WorkspacePath? OutputPluginPath,
    Sha256Hash? OutputPluginSha256,
    WorkspacePath? ManifestPath,
    Sha256Hash? ManifestSha256,
    ImmutableArray<MutationChange> Changes,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspacePath? ConsolidationEvidencePath { get; init; }
}

public interface IExistingNpcEditService
{
    ValueTask<ExistingNpcEditInspection> InspectAsync(
        GameEdition edition,
        WorkspacePath inputPlugin,
        Sha256Hash expectedInputSha256,
        FormId targetFormId,
        CancellationToken cancellationToken);

    ValueTask<ExistingNpcEditReview> ReviewAsync(
        ExistingNpcEditRequest request,
        CancellationToken cancellationToken);

    ValueTask<ExistingNpcEditResult> ExecuteAsync(
        ExistingNpcEditRequest request,
        IProgress<ExistingNpcEditProgress>? progress,
        CancellationToken cancellationToken);
}
