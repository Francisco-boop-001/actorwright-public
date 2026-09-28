using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceLandmarkProjectionTests
{
    private static readonly (
        ReferenceSemanticAnchorKind Anchor,
        int Index)[] ExpectedMap =
    [
        (ReferenceSemanticAnchorKind.ForeheadCenter, 10),
        (ReferenceSemanticAnchorKind.Chin, 152),
        (ReferenceSemanticAnchorKind.LeftJawAngle, 172),
        (ReferenceSemanticAnchorKind.RightJawAngle, 397),
        (ReferenceSemanticAnchorKind.LeftWidestCheek, 234),
        (ReferenceSemanticAnchorKind.RightWidestCheek, 454),
        (ReferenceSemanticAnchorKind.LeftTemple, 127),
        (ReferenceSemanticAnchorKind.RightTemple, 356),
        (ReferenceSemanticAnchorKind.LeftBrowInner, 107),
        (ReferenceSemanticAnchorKind.LeftBrowPeak, 105),
        (ReferenceSemanticAnchorKind.LeftBrowOuter, 70),
        (ReferenceSemanticAnchorKind.RightBrowInner, 336),
        (ReferenceSemanticAnchorKind.RightBrowPeak, 334),
        (ReferenceSemanticAnchorKind.RightBrowOuter, 300),
        (ReferenceSemanticAnchorKind.LeftEyeInner, 133),
        (ReferenceSemanticAnchorKind.LeftEyeOuter, 33),
        (ReferenceSemanticAnchorKind.LeftEyeUpper, 159),
        (ReferenceSemanticAnchorKind.LeftEyeLower, 145),
        (ReferenceSemanticAnchorKind.RightEyeInner, 362),
        (ReferenceSemanticAnchorKind.RightEyeOuter, 263),
        (ReferenceSemanticAnchorKind.RightEyeUpper, 386),
        (ReferenceSemanticAnchorKind.RightEyeLower, 374),
        (ReferenceSemanticAnchorKind.NoseBridge, 168),
        (ReferenceSemanticAnchorKind.NoseTip, 1),
        (ReferenceSemanticAnchorKind.LeftNoseWing, 98),
        (ReferenceSemanticAnchorKind.RightNoseWing, 327),
        (ReferenceSemanticAnchorKind.LeftMouthCorner, 61),
        (ReferenceSemanticAnchorKind.RightMouthCorner, 291),
        (ReferenceSemanticAnchorKind.LeftCupidPeak, 37),
        (ReferenceSemanticAnchorKind.RightCupidPeak, 267),
        (ReferenceSemanticAnchorKind.LowerLipCenter, 17)
    ];

    public static async Task TestExactAnchorMap()
    {
        var projector =
            new ReferenceSemanticLandmarkProjector();
        ReferenceSemanticLandmarkProjectionResult result =
            await projector.ProjectAsync(
                new ReferenceSemanticLandmarkProjectionRequest(
                    Inference(
                        ReferenceImageViewRole.Front,
                        0.9,
                        index => (index, index))),
                CancellationToken.None);

        Require(
            result.Diagnostics.All(item =>
                item.Severity != DiagnosticSeverity.Error),
            "reference-anchor-map-diagnostics");
        Require(
            result.Anchors.Length == ExpectedMap.Length,
            "reference-anchor-map-count");
        for (var ordinal = 0;
             ordinal < ExpectedMap.Length;
             ordinal++)
        {
            ReferenceSemanticAnchorProposal anchor =
                result.Anchors[ordinal];
            Require(
                anchor.Anchor == ExpectedMap[ordinal].Anchor &&
                anchor.SourceLandmarkIndex ==
                    ExpectedMap[ordinal].Index &&
                anchor.X == ExpectedMap[ordinal].Index &&
                anchor.Y == ExpectedMap[ordinal].Index,
                $"reference-anchor-map:{ordinal}");
        }
    }

    public static async Task TestVisibilityAndConfidence()
    {
        var projector =
            new ReferenceSemanticLandmarkProjector();
        ReferenceSemanticLandmarkProjectionResult profile =
            await projector.ProjectAsync(
                new ReferenceSemanticLandmarkProjectionRequest(
                    Inference(
                        ReferenceImageViewRole.LeftProfile,
                        0.75,
                        _ => (0.5, 0.5))),
                CancellationToken.None);

        ReferenceSemanticAnchorProposal visible =
            profile.Anchors.Single(item =>
                item.Anchor ==
                ReferenceSemanticAnchorKind.LeftEyeOuter);
        ReferenceSemanticAnchorProposal hidden =
            profile.Anchors.Single(item =>
                item.Anchor ==
                ReferenceSemanticAnchorKind.RightEyeOuter);
        Require(
            visible.AdvisoryVisible &&
            visible.ManualConfirmationRequired,
            "reference-anchor-low-confidence-visible");
        Require(
            !hidden.AdvisoryVisible &&
            !hidden.ManualConfirmationRequired,
            "reference-anchor-profile-hidden");

        ReferenceSemanticLandmarkProjectionResult highConfidence =
            await projector.ProjectAsync(
                new ReferenceSemanticLandmarkProjectionRequest(
                    Inference(
                        ReferenceImageViewRole.Front,
                        0.76,
                        _ => (0.5, 0.5))),
                CancellationToken.None);
        Require(
            highConfidence.Anchors.All(item =>
                !item.ManualConfirmationRequired),
            "reference-anchor-high-confidence");
    }

    private static ReferenceImageInference Inference(
        ReferenceImageViewRole role,
        double detectorScore,
        Func<int, (double X, double Y)> coordinates)
    {
        ImmutableArray<ReferenceFaceLandmark> landmarks =
            Enumerable.Range(0, 478)
                .Select(index =>
                {
                    (double x, double y) = coordinates(index);
                    return new ReferenceFaceLandmark(
                        index,
                        x,
                        y,
                        0.0,
                        1.0,
                        1.0);
                })
                .ToImmutableArray();
        ImmutableArray<double> matrix = Enumerable.Range(0, 16)
            .Select(index => index % 5 == 0 ? 1.0 : 0.0)
            .ToImmutableArray();
        return new ReferenceImageInference(
            "view",
            role,
            Hash('a'),
            Hash('b'),
            1024,
            1024,
            detectorScore,
            landmarks,
            matrix,
            0.0,
            Hash('c'),
            Hash('d'),
            Hash('e'));
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }
}
