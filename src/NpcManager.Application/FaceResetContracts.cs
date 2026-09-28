using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Face-editor tabs that the pinned upstream form can reset independently.</summary>
public enum FaceResetSection
{
    FaceParts,
    Tints,
    VertexMorphs,
    BoneRegions,
    SkyrimMorphs,
    SkyrimTints
}

public static class FaceResetSectionExtensions
{
    public static string ToWireName(this FaceResetSection section) => section switch
    {
        FaceResetSection.FaceParts => "face-parts",
        FaceResetSection.Tints => "tints",
        FaceResetSection.VertexMorphs => "vertex-morphs",
        FaceResetSection.BoneRegions => "bone-regions",
        FaceResetSection.SkyrimMorphs => "skyrim-morphs",
        FaceResetSection.SkyrimTints => "skyrim-tints",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported face reset section.")
    };

    public static bool TryParseWireName(string value, out FaceResetSection section)
    {
        foreach (var candidate in Enum.GetValues<FaceResetSection>())
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
/// A hash-bound reset over a K-local face-editor snapshot. The snapshot is an explicit
/// interchange artifact because the upstream reset operates on an in-memory overlay.
/// Section payloads remain JSON values so unknown upstream fields survive untouched.
/// </summary>
public sealed record FaceResetRequest(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath CurrentSnapshot,
    WorkspacePath BaselineSnapshot,
    WorkspacePath OutputSnapshot,
    FaceResetSection Section,
    Sha256Hash? ExpectedCurrentHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record FaceResetProposal(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath CurrentSnapshot,
    WorkspacePath BaselineSnapshot,
    WorkspacePath OutputSnapshot,
    FaceResetSection Section,
    Sha256Hash CurrentHash,
    Sha256Hash BaselineHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable =>
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record FaceResetResult(
    bool Applied,
    FaceResetProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceResetService
{
    ValueTask<FaceResetProposal> AnalyzeAsync(FaceResetRequest request, CancellationToken cancellationToken);

    ValueTask<FaceResetResult> ApplyAsync(FaceResetRequest request, FaceResetProposal proposal,
        CancellationToken cancellationToken);
}
