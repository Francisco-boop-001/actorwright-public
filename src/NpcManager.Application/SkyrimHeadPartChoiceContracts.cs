using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimHeadPartRaceMatchKind
{
    HumanoidUnrestricted,
    ValidRaceList,
    RaceDefault
}

public sealed record SkyrimHeadPartChoiceRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference Race,
    NpcSex Sex,
    NpcHeadPartType Type,
    string? Search);

public sealed record SkyrimHeadPartChoiceCandidate(
    FormReference Reference,
    string EditorId,
    string? Name,
    NpcHeadPartType Type,
    bool IsExtra,
    bool SupportsMale,
    bool SupportsFemale,
    AssetPath? ModelNif,
    ImmutableArray<FormReference> ExtraParts,
    ImmutableArray<SkyrimHeadPartPreviewModel> PreviewModels,
    SkyrimFaceRecordProvider Provider,
    SkyrimHeadPartRaceMatchKind RaceMatch);

public sealed record SkyrimHeadPartPreviewModel(
    FormReference Reference,
    AssetPath ModelNif,
    SkyrimFaceRecordProvider Provider,
    bool IsRoot);

public sealed record SkyrimHeadPartChoiceResult(
    bool Accepted,
    ImmutableArray<SkyrimHeadPartChoiceCandidate> Candidates,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimHeadPartChoiceService
{
    ValueTask<SkyrimHeadPartChoiceResult> SearchAsync(
        SkyrimHeadPartChoiceRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimHeadPartPreviewRequest(
    WorkspacePath DataRoot,
    SkyrimHeadPartChoiceCandidate Candidate,
    int Width = 512,
    int Height = 512);

public sealed record SkyrimHeadPartPreviewResult(
    bool Rendered,
    PreviewRenderedImage? Image,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimHeadPartPreviewService
{
    ValueTask<SkyrimHeadPartPreviewResult> RenderAsync(
        SkyrimHeadPartPreviewRequest request,
        CancellationToken cancellationToken);
}
