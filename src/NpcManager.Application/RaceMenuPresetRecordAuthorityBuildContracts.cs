using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RaceMenuPresetHeadPartAuthority(
    PresetHeadPart Source,
    RaceMenuNpcFormBinding Binding);

/// <summary>
/// Fully derived record-authority content before it is serialized and hashed.
/// No output file or active build request has changed at this stage.
/// </summary>
public sealed record RaceMenuPresetRecordAuthorityDraft(
    string AuthorityId,
    PresetDocument Preset,
    RaceMenuPresetTarget Target,
    RaceMenuNpcFormBinding RaceBinding,
    ImmutableArray<RaceMenuPresetHeadPartAuthority> HeadParts,
    RaceMenuNpcFormBinding HeadTextureBinding,
    SkyrimFaceTextureSetAuthority HeadTextureAuthority,
    uint HairColorPackedRgb,
    FormId OutputOwnedHairColorFormId,
    RaceMenuPresetTintAuthorityPlan TintPlan,
    bool RuntimeAuthority);

public sealed record RaceMenuPresetRecordAuthorityBuildResult(
    bool Accepted,
    RaceMenuPresetRecordAuthorityDraft? Draft,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetRecordAuthorityBuilder
{
    ValueTask<RaceMenuPresetRecordAuthorityBuildResult> BuildAsync(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken);
}
