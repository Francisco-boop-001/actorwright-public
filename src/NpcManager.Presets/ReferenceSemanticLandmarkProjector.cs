using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Presets;

public sealed class ReferenceSemanticLandmarkProjector
    : IReferenceSemanticLandmarkProjector
{
    private static readonly ImmutableArray<AnchorMapEntry> AnchorMap =
    [
        Entry(ReferenceSemanticAnchorKind.ForeheadCenter, 10),
        Entry(ReferenceSemanticAnchorKind.Chin, 152),
        Entry(ReferenceSemanticAnchorKind.LeftJawAngle, 172),
        Entry(ReferenceSemanticAnchorKind.RightJawAngle, 397),
        Entry(ReferenceSemanticAnchorKind.LeftWidestCheek, 234),
        Entry(ReferenceSemanticAnchorKind.RightWidestCheek, 454),
        Entry(ReferenceSemanticAnchorKind.LeftTemple, 127),
        Entry(ReferenceSemanticAnchorKind.RightTemple, 356),
        Entry(ReferenceSemanticAnchorKind.LeftBrowInner, 107),
        Entry(ReferenceSemanticAnchorKind.LeftBrowPeak, 105),
        Entry(ReferenceSemanticAnchorKind.LeftBrowOuter, 70),
        Entry(ReferenceSemanticAnchorKind.RightBrowInner, 336),
        Entry(ReferenceSemanticAnchorKind.RightBrowPeak, 334),
        Entry(ReferenceSemanticAnchorKind.RightBrowOuter, 300),
        Entry(ReferenceSemanticAnchorKind.LeftEyeInner, 133),
        Entry(ReferenceSemanticAnchorKind.LeftEyeOuter, 33),
        Entry(ReferenceSemanticAnchorKind.LeftEyeUpper, 159),
        Entry(ReferenceSemanticAnchorKind.LeftEyeLower, 145),
        Entry(ReferenceSemanticAnchorKind.RightEyeInner, 362),
        Entry(ReferenceSemanticAnchorKind.RightEyeOuter, 263),
        Entry(ReferenceSemanticAnchorKind.RightEyeUpper, 386),
        Entry(ReferenceSemanticAnchorKind.RightEyeLower, 374),
        Entry(ReferenceSemanticAnchorKind.NoseBridge, 168),
        Entry(ReferenceSemanticAnchorKind.NoseTip, 1),
        Entry(ReferenceSemanticAnchorKind.LeftNoseWing, 98),
        Entry(ReferenceSemanticAnchorKind.RightNoseWing, 327),
        Entry(ReferenceSemanticAnchorKind.LeftMouthCorner, 61),
        Entry(ReferenceSemanticAnchorKind.RightMouthCorner, 291),
        Entry(ReferenceSemanticAnchorKind.LeftCupidPeak, 37),
        Entry(ReferenceSemanticAnchorKind.RightCupidPeak, 267),
        Entry(ReferenceSemanticAnchorKind.LowerLipCenter, 17)
    ];

    public ValueTask<ReferenceSemanticLandmarkProjectionResult>
        ProjectAsync(
            ReferenceSemanticLandmarkProjectionRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ReferenceImageInference inference = request.Inference;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (inference is null ||
            inference.Landmarks.IsDefault ||
            inference.Landmarks.Length != 478)
        {
            diagnostics.Add(Error(
                "reference-anchor-landmark-count",
                "Semantic projection requires exactly 478 ordered landmarks."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        if (!Enum.IsDefined(inference.ViewRole))
        {
            diagnostics.Add(Error(
                "reference-anchor-view-role",
                "Semantic projection requires a supported view role."));
        }

        if (!double.IsFinite(inference.DetectorScore) ||
            inference.DetectorScore <
                ReferencePresetAuthoringRules.MinimumFaceScore ||
            inference.DetectorScore > 1.0)
        {
            diagnostics.Add(Error(
                "reference-anchor-detector-score",
                "Semantic projection requires an admitted detector score."));
        }

        for (var index = 0;
             index < inference.Landmarks.Length;
             index++)
        {
            ReferenceFaceLandmark landmark =
                inference.Landmarks[index];
            if (landmark is null ||
                landmark.Index != index ||
                !double.IsFinite(landmark.X) ||
                !double.IsFinite(landmark.Y) ||
                !double.IsFinite(landmark.Z) ||
                !IsOptionalUnitInterval(landmark.Presence) ||
                !IsOptionalUnitInterval(landmark.Visibility))
            {
                diagnostics.Add(Error(
                    "reference-anchor-landmark-shape",
                    "Semantic projection requires finite, ordered landmark evidence."));
                break;
            }
        }

        if (diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        var anchors =
            ImmutableArray.CreateBuilder<
                ReferenceSemanticAnchorProposal>(AnchorMap.Length);
        bool lowConfidence =
            inference.DetectorScore <=
            ReferencePresetAuthoringRules.LowConfidenceCeiling;
        foreach (AnchorMapEntry mapping in AnchorMap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReferenceFaceLandmark landmark =
                inference.Landmarks[mapping.LandmarkIndex];
            bool visible =
                IsAdvisoryVisible(
                    mapping.Anchor,
                    inference.ViewRole) &&
                (landmark.Presence ?? 1.0) >= 0.5 &&
                (landmark.Visibility ?? 1.0) >= 0.5;
            double confidence = Math.Min(
                inference.DetectorScore,
                Math.Min(
                    landmark.Presence ?? 1.0,
                    landmark.Visibility ?? 1.0));
            anchors.Add(new ReferenceSemanticAnchorProposal(
                mapping.Anchor,
                mapping.LandmarkIndex,
                landmark.X,
                landmark.Y,
                confidence,
                visible,
                visible && lowConfidence));
        }

        return ValueTask.FromResult(
            new ReferenceSemanticLandmarkProjectionResult(
                anchors.ToImmutable(),
                diagnostics.ToImmutable()));
    }

    private static bool IsAdvisoryVisible(
        ReferenceSemanticAnchorKind anchor,
        ReferenceImageViewRole role)
    {
        if (role == ReferenceImageViewRole.LeftProfile &&
            IsRightAnchor(anchor))
        {
            return false;
        }

        if (role == ReferenceImageViewRole.RightProfile &&
            IsLeftAnchor(anchor))
        {
            return false;
        }

        return true;
    }

    private static bool IsLeftAnchor(
        ReferenceSemanticAnchorKind anchor) => anchor is
        ReferenceSemanticAnchorKind.LeftJawAngle or
        ReferenceSemanticAnchorKind.LeftWidestCheek or
        ReferenceSemanticAnchorKind.LeftTemple or
        ReferenceSemanticAnchorKind.LeftBrowInner or
        ReferenceSemanticAnchorKind.LeftBrowPeak or
        ReferenceSemanticAnchorKind.LeftBrowOuter or
        ReferenceSemanticAnchorKind.LeftEyeInner or
        ReferenceSemanticAnchorKind.LeftEyeOuter or
        ReferenceSemanticAnchorKind.LeftEyeUpper or
        ReferenceSemanticAnchorKind.LeftEyeLower or
        ReferenceSemanticAnchorKind.LeftNoseWing or
        ReferenceSemanticAnchorKind.LeftMouthCorner or
        ReferenceSemanticAnchorKind.LeftCupidPeak;

    private static bool IsRightAnchor(
        ReferenceSemanticAnchorKind anchor) => anchor is
        ReferenceSemanticAnchorKind.RightJawAngle or
        ReferenceSemanticAnchorKind.RightWidestCheek or
        ReferenceSemanticAnchorKind.RightTemple or
        ReferenceSemanticAnchorKind.RightBrowInner or
        ReferenceSemanticAnchorKind.RightBrowPeak or
        ReferenceSemanticAnchorKind.RightBrowOuter or
        ReferenceSemanticAnchorKind.RightEyeInner or
        ReferenceSemanticAnchorKind.RightEyeOuter or
        ReferenceSemanticAnchorKind.RightEyeUpper or
        ReferenceSemanticAnchorKind.RightEyeLower or
        ReferenceSemanticAnchorKind.RightNoseWing or
        ReferenceSemanticAnchorKind.RightMouthCorner or
        ReferenceSemanticAnchorKind.RightCupidPeak;

    private static bool IsOptionalUnitInterval(double? value) =>
        value is null ||
        (double.IsFinite(value.Value) &&
         value.Value >= 0.0 &&
         value.Value <= 1.0);

    private static AnchorMapEntry Entry(
        ReferenceSemanticAnchorKind anchor,
        int landmarkIndex) =>
        new(anchor, landmarkIndex);

    private static ReferenceSemanticLandmarkProjectionResult Rejected(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            ImmutableArray<ReferenceSemanticAnchorProposal>.Empty,
            diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record AnchorMapEntry(
        ReferenceSemanticAnchorKind Anchor,
        int LandmarkIndex);
}
