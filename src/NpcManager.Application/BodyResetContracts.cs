using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Independent body-editor sections that the pinned EditBody form can reset.</summary>
public enum BodyResetSection
{
    Weight,
    Morphs,
    Sliders,
    Skin,
    Overlays,
    Transforms,
    SkinOverrides
}

public static class BodyResetSectionExtensions
{
    public static string ToWireName(this BodyResetSection section) => section switch
    {
        BodyResetSection.Weight => "weight",
        BodyResetSection.Morphs => "morphs",
        BodyResetSection.Sliders => "sliders",
        BodyResetSection.Skin => "skin",
        BodyResetSection.Overlays => "overlays",
        BodyResetSection.Transforms => "transforms",
        BodyResetSection.SkinOverrides => "skin-overrides",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported body reset section.")
    };

    public static bool TryParseWireName(string value, out BodyResetSection section)
    {
        foreach (var candidate in Enum.GetValues<BodyResetSection>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                section = candidate;
                return true;
            }
        }

        section = default;
        return false;
    }
}

/// <summary>
/// Hash-bound reset over an explicit K-local body-editor snapshot. The upstream reset operates
/// on an in-memory preset overlay, so this interchange artifact deliberately makes that baseline
/// explicit without claiming to be a plugin, .jslot, or runtime state file.
/// </summary>
public sealed record BodyResetRequest(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath CurrentSnapshot,
    WorkspacePath BaselineSnapshot,
    WorkspacePath OutputSnapshot,
    BodyResetSection Section,
    Sha256Hash? ExpectedCurrentHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record BodyResetProposal(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath CurrentSnapshot,
    WorkspacePath BaselineSnapshot,
    WorkspacePath OutputSnapshot,
    BodyResetSection Section,
    Sha256Hash CurrentHash,
    Sha256Hash BaselineHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable =>
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record BodyResetResult(
    bool Applied,
    BodyResetProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IBodyResetService
{
    ValueTask<BodyResetProposal> AnalyzeAsync(BodyResetRequest request, CancellationToken cancellationToken);

    ValueTask<BodyResetResult> ApplyAsync(BodyResetRequest request, BodyResetProposal proposal,
        CancellationToken cancellationToken);
}
