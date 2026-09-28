using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Fixed-order projected-coordinate solver for exact reviewed response
/// matrices. No random seed, wall clock, process state, or platform solver is
/// consulted.
/// </summary>
public sealed class ReferenceRaceMenuPresetSolver
    : IReferenceRaceMenuPresetSolver
{
    private const double RegularizationWeight = 0.0001;
    private const double TextWeightScale = 0.25;
    private const double ImprovementTolerance = 0.000000000001;
    private const double ResidualLossThreshold = 0.000001;

    public ValueTask<ReferenceRaceMenuPresetSolverResult> SolveAsync(
        ReferenceRaceMenuPresetSolverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ReferencePresetAuthoringRules.CanSolve(
            request.ReviewedDesign,
            request.ReviewedDesign.ProposalSha256));
        ValidateAuthority(request, diagnostics);
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(Refused(diagnostics));
        }

        AnchorRow[] anchors = BuildAnchorRows(request, diagnostics);
        RaceMenuMorphResponse[] allResponses =
            request.ResponseMatrix.Responses
                .OrderBy(item => item.Channel.Ordinal)
                .ThenBy(item => item.Channel.Name,
                    StringComparer.Ordinal)
                .ToArray();
        ValidateRowsAndResponses(
            request, anchors, allResponses, diagnostics);
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(Refused(diagnostics));
        }

        var predictedX = anchors.Select(item => item.BaselineX).ToArray();
        var predictedY = anchors.Select(item => item.BaselineY).ToArray();
        var native = new SortedDictionary<string, double>(
            StringComparer.Ordinal);
        var custom = new SortedDictionary<string, double>(
            StringComparer.Ordinal);

        SelectDiscreteCandidates(
            allResponses.Where(item => item.Channel.IsDiscrete).ToArray(),
            anchors,
            predictedX,
            predictedY,
            native,
            custom,
            cancellationToken);

        RaceMenuMorphResponse[] continuous = allResponses
            .Where(item => !item.Channel.IsDiscrete)
            .ToArray();
        double[,] positiveX = BuildResponseTable(
            continuous, anchors, horizontal: true, negative: false);
        double[,] positiveY = BuildResponseTable(
            continuous, anchors, horizontal: false, negative: false);
        double[,] negativeX = BuildResponseTable(
            continuous, anchors, horizontal: true, negative: true);
        double[,] negativeY = BuildResponseTable(
            continuous, anchors, horizontal: false, negative: true);
        TextGoal[] textGoals = BuildTextGoals(
            request.ReviewedDesign,
            continuous);
        double[,] textCoefficients = BuildTextCoefficientTable(
            continuous, textGoals);
        var textPredicted = new double[textGoals.Length];
        var values = new double[continuous.Length];

        double objective = Objective(
            anchors,
            predictedX,
            predictedY,
            textGoals,
            textPredicted,
            values);
        for (var iteration = 0;
             iteration < ReferencePresetAuthoringRules.SolverIterations;
             iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double step = Math.Max(
                0.000001,
                ReferencePresetAuthoringRules.GeneralMorphMagnitudeCap *
                Math.Pow(0.95, iteration));
            for (var column = 0;
                 column < continuous.Length;
                 column++)
            {
                double current = values[column];
                double minimum = Math.Max(
                    -ReferencePresetAuthoringRules
                        .GeneralMorphMagnitudeCap,
                    Math.Max(-1.0,
                        continuous[column].Channel.Minimum));
                double maximum = Math.Min(
                    ReferencePresetAuthoringRules
                        .GeneralMorphMagnitudeCap,
                    Math.Min(1.0,
                        continuous[column].Channel.Maximum));
                double negative = Math.Clamp(
                    current - step, minimum, maximum);
                double positive = Math.Clamp(
                    current + step, minimum, maximum);

                double selected = current;
                double selectedObjective = objective;
                EvaluateCandidate(
                    column,
                    negative,
                    current,
                    anchors,
                    predictedX,
                    predictedY,
                    positiveX,
                    positiveY,
                    negativeX,
                    negativeY,
                    textGoals,
                    textPredicted,
                    textCoefficients,
                    values,
                    ref selected,
                    ref selectedObjective);
                EvaluateCandidate(
                    column,
                    positive,
                    current,
                    anchors,
                    predictedX,
                    predictedY,
                    positiveX,
                    positiveY,
                    negativeX,
                    negativeY,
                    textGoals,
                    textPredicted,
                    textCoefficients,
                    values,
                    ref selected,
                    ref selectedObjective);
                if (selected == current)
                {
                    continue;
                }

                values[column] = selected;
                for (var row = 0; row < anchors.Length; row++)
                {
                    predictedX[row] += ContributionDifference(
                        current,
                        selected,
                        positiveX[column, row],
                        negativeX[column, row]);
                    predictedY[row] += ContributionDifference(
                        current,
                        selected,
                        positiveY[column, row],
                        negativeY[column, row]);
                }
                double change = selected - current;
                for (var trait = 0;
                     trait < textGoals.Length;
                     trait++)
                {
                    textPredicted[trait] +=
                        change * textCoefficients[column, trait];
                }
                objective = selectedObjective;
            }
        }

        for (var column = 0; column < continuous.Length; column++)
        {
            double quantized = Quantize(values[column]);
            double change = quantized - values[column];
            if (change != 0)
            {
                for (var row = 0; row < anchors.Length; row++)
                {
                    predictedX[row] += ContributionDifference(
                        values[column],
                        quantized,
                        positiveX[column, row],
                        negativeX[column, row]);
                    predictedY[row] += ContributionDifference(
                        values[column],
                        quantized,
                        positiveY[column, row],
                        negativeY[column, row]);
                }
                for (var trait = 0;
                     trait < textGoals.Length;
                     trait++)
                {
                    textPredicted[trait] +=
                        change * textCoefficients[column, trait];
                }
            }
            values[column] = quantized;
            if (Math.Abs(quantized) < 0.0000005)
            {
                continue;
            }
            if (continuous[column].Channel.Kind ==
                ReferenceMorphChannelKind.NativePreset)
            {
                native[continuous[column].Channel.Name] = quantized;
            }
            else
            {
                custom[continuous[column].Channel.Name] = quantized;
            }
        }
        objective = Objective(
            anchors,
            predictedX,
            predictedY,
            textGoals,
            textPredicted,
            values);

        ImmutableArray<ReferenceSculptVertexDelta> sculpt =
            TryBuildSculpt(
                request,
                anchors,
                predictedX,
                predictedY,
                objective,
                out double sculptObjective,
                diagnostics);
        if (!sculpt.IsEmpty)
        {
            ApplySculptProjection(
                request.RenderInput!,
                request.ReviewedDesign.MeshBindings,
                sculpt,
                anchors,
                predictedX,
                predictedY);
            objective = sculptObjective;
        }

        ImmutableArray<ReferencePresetResidual> residuals =
            BuildResiduals(
                anchors, predictedX, predictedY);
        ImmutableArray<ReferencePresetLoss> losses = BuildLosses(
            request.ReviewedDesign,
            residuals,
            textGoals,
            continuous,
            sculpt);
        ImmutableDictionary<string, double> nativeResult =
            native.ToImmutableDictionary(
                item => item.Key,
                item => Quantize(item.Value),
                StringComparer.Ordinal);
        ImmutableDictionary<string, double> customResult =
            custom.ToImmutableDictionary(
                item => item.Key,
                item => Quantize(item.Value),
                StringComparer.Ordinal);
        double finalObjective = QuantizeObjective(objective);
        Sha256Hash resultHash = HashResult(
            request,
            nativeResult,
            customResult,
            sculpt,
            residuals,
            losses,
            finalObjective);
        diagnostics.Add(new Diagnostic(
            "reference-solver-completed",
            DiagnosticSeverity.Info,
            $"Completed exactly {ReferencePresetAuthoringRules.SolverIterations} deterministic projected-coordinate iterations."));
        (string sculptNif, string sculptShape, string sculptHost) =
            ResolveSculptAuthority(request, sculpt);
        return ValueTask.FromResult(
            new ReferenceRaceMenuPresetSolverResult(
                nativeResult,
                customResult,
                sculpt,
                residuals,
                losses,
                ReferencePresetAuthoringRules.SolverIterations,
                finalObjective,
                resultHash,
                diagnostics.ToImmutable())
            {
                SculptNifIdentity = sculptNif,
                SculptShapeIdentity = sculptShape,
                SculptHost = sculptHost
            });
    }

    private static (string Nif, string Shape, string Host)
        ResolveSculptAuthority(
            ReferenceRaceMenuPresetSolverRequest request,
            ImmutableArray<ReferenceSculptVertexDelta> sculpt)
    {
        if (sculpt.IsEmpty)
            return (string.Empty, string.Empty, string.Empty);
        (string Nif, string Shape)[] identities =
            request.ReviewedDesign.MeshBindings
                .Select(item =>
                    (item.NifIdentity, item.ShapeIdentity))
                .Distinct()
                .ToArray();
        if (identities.Length != 1)
            return (string.Empty, string.Empty, string.Empty);
        ReferenceFaceMorphShapeBasis[] bases =
            request.Snapshot.MorphBases.Where(item =>
                    item.NifIdentity == identities[0].Nif &&
                    item.ShapeIdentity == identities[0].Shape)
                .ToArray();
        string host = bases.Length == 1
            ? bases[0].PlanTemplate.ChargenMorphTri
                  ?.Document.SourcePath.Value ??
              bases[0].PlanTemplate.RaceMorphTri
                  ?.Document.SourcePath.Value ??
              bases[0].PlanTemplate.MeshMorphTri
                  ?.Document.SourcePath.Value ??
              string.Empty
            : string.Empty;
        return (identities[0].Nif, identities[0].Shape, host);
    }

    private static void ValidateAuthority(
        ReferenceRaceMenuPresetSolverRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Sha256Hash reviewedDesignSha256 =
            IsHashBound(request.ReviewedDesignSha256)
                ? request.ReviewedDesignSha256
                : IsHashBound(
                    request.ResponseMatrix.ReviewedDesignSha256)
                    ? request.ResponseMatrix.ReviewedDesignSha256
                    : request.Snapshot.ReviewedDesignSha256;
        if (request.Snapshot.SchemaVersion != 1 ||
            request.Snapshot.ReviewedDesignSha256 !=
                reviewedDesignSha256 ||
            (IsHashBound(
                 request.ResponseMatrix.ReviewedDesignSha256) &&
             request.ResponseMatrix.ReviewedDesignSha256 !=
                 reviewedDesignSha256) ||
            !IsHashBound(request.Snapshot.ResourceFingerprint))
        {
            diagnostics.Add(Error(
                "reference-solver-snapshot",
                "The solver requires the exact reviewed schema-1 resource snapshot."));
        }
        if (!request.ResponseMatrix.Accepted ||
            request.ResponseMatrix.MatrixSha256 is null ||
            !IsHashBound(request.ResponseMatrix.RenderInputSha256) ||
            request.ResponseMatrix.BaselineProjections.IsDefault)
        {
            diagnostics.Add(Error(
                "reference-solver-matrix",
                "The solver requires an accepted hash-bound response matrix with explicit baseline projections."));
        }
        if (request.RenderInput is not null &&
            request.RenderInput.InputSha256 !=
            request.ResponseMatrix.RenderInputSha256)
        {
            diagnostics.Add(Error(
                "reference-solver-render-drift",
                "The optional sculpt render input no longer matches the response matrix."));
        }
        if (request.ProtectedNeckRingVertexIndices.IsDefault)
        {
            diagnostics.Add(Error(
                "reference-solver-neck-ring",
                "The protected neck-ring index array must be explicit, even when sculpt is unavailable."));
        }
    }

    private static AnchorRow[] BuildAnchorRows(
        ReferenceRaceMenuPresetSolverRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = new List<AnchorRow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReferenceAnchorProjectionBaseline baseline in
                 request.ResponseMatrix.BaselineProjections
                     .OrderBy(item => (int)item.ViewRole)
                     .ThenBy(item => (int)item.Anchor))
        {
            string key = Key(baseline.ViewRole, baseline.Anchor);
            if (!keys.Add(key))
            {
                diagnostics.Add(Error(
                    "reference-solver-baseline-duplicate",
                    $"Baseline projection '{key}' is duplicated."));
                continue;
            }
            ReferenceSemanticAnchor? target = request.ReviewedDesign.Views
                .Where(item => item.ViewRole == baseline.ViewRole)
                .SelectMany(item => item.Anchors)
                .SingleOrDefault(item =>
                    item.Anchor == baseline.Anchor);
            if (target is null ||
                !double.IsFinite(baseline.X) ||
                !double.IsFinite(baseline.Y) ||
                !double.IsFinite(target.X) ||
                !double.IsFinite(target.Y))
            {
                diagnostics.Add(Error(
                    "reference-solver-baseline",
                    $"Baseline projection '{key}' has no finite reviewed target."));
                continue;
            }
            double weight =
                target.ReviewState is
                    (ReferenceAnchorReviewState.Accepted or
                     ReferenceAnchorReviewState.Corrected)
                    ? Math.Clamp(target.Confidence, 0, 1)
                    : 0;
            result.Add(new AnchorRow(
                baseline.ViewRole,
                baseline.Anchor,
                baseline.X,
                baseline.Y,
                target.X,
                target.Y,
                weight));
        }
        return result.ToArray();
    }

    private static void ValidateRowsAndResponses(
        ReferenceRaceMenuPresetSolverRequest request,
        AnchorRow[] anchors,
        RaceMenuMorphResponse[] responses,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> expectedBindings =
            request.ReviewedDesign.MeshBindings
                .Select(item => Key(item.ViewRole, item.Anchor))
                .ToHashSet(StringComparer.Ordinal);
        HashSet<string> actualRows = anchors
            .Select(item => Key(item.ViewRole, item.Anchor))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedBindings.SetEquals(actualRows))
        {
            diagnostics.Add(Error(
                "reference-solver-baseline-closure",
                "Baseline projections do not exactly cover the reviewed mesh bindings."));
        }

        var responseOrdinals = new HashSet<int>();
        foreach (RaceMenuMorphResponse response in responses)
        {
            ReferenceMorphChannelAuthority[] authorities =
                request.Snapshot.MorphChannels
                    .Where(item =>
                        item.Ordinal == response.Channel.Ordinal &&
                        item.Name == response.Channel.Name &&
                        item.TriSha256 == response.Channel.TriSha256 &&
                        item.NifIdentity == response.Channel.NifIdentity &&
                        item.ShapeIdentity ==
                            response.Channel.ShapeIdentity)
                    .ToArray();
            if (!responseOrdinals.Add(response.Channel.Ordinal) ||
                authorities.Length != 1 ||
                response.ResourceSnapshotSha256 !=
                    request.Snapshot.ResourceFingerprint ||
                !IsHashBound(response.MorphSha256) ||
                !IsHashBound(response.TopologySha256) ||
                !IsHashBound(response.CameraSetSha256) ||
                response.Displacements.IsDefault ||
                response.Displacements.Any(item =>
                    !double.IsFinite(item.DeltaX) ||
                    !double.IsFinite(item.DeltaY)) ||
                response.NegativeDisplacements.IsDefault ||
                response.NegativeDisplacements.Any(item =>
                    !double.IsFinite(item.DeltaX) ||
                    !double.IsFinite(item.DeltaY)))
            {
                diagnostics.Add(Error(
                    "reference-solver-response-authority",
                    $"Response channel '{response.Channel.Name}' has stale or incomplete authority."));
            }
        }
    }

    private static void SelectDiscreteCandidates(
        RaceMenuMorphResponse[] discrete,
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY,
        SortedDictionary<string, double> native,
        SortedDictionary<string, double> custom,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<string, RaceMenuMorphResponse> family in
                 discrete
                     .GroupBy(item =>
                         item.Channel.Kind ==
                             ReferenceMorphChannelKind.NativePreset
                             ? $"0:N:{item.Channel.DiscreteFamilyOrdinal}"
                             : $"1:C:{item.Channel.Name}",
                         StringComparer.Ordinal)
                     .OrderBy(item => item.Key,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaceMenuMorphResponse? selected = null;
            double selectedObjective = AnchorObjective(
                anchors, predictedX, predictedY);
            foreach (RaceMenuMorphResponse candidate in family
                         .OrderBy(item => item.Channel.Ordinal)
                         .ThenBy(item => item.Channel.Name,
                             StringComparer.Ordinal))
            {
                double candidateObjective = AnchorObjectiveWithResponse(
                    anchors,
                    predictedX,
                    predictedY,
                    candidate);
                if (candidateObjective <
                    selectedObjective - ImprovementTolerance)
                {
                    selected = candidate;
                    selectedObjective = candidateObjective;
                }
            }
            if (selected is null)
            {
                continue;
            }
            ApplyResponse(selected, anchors, predictedX, predictedY);
            if (selected.Channel.Kind ==
                ReferenceMorphChannelKind.NativePreset)
            {
                native[
                    $"NAMA[{selected.Channel.DiscreteFamilyOrdinal}]"] =
                    selected.Channel.DiscreteValue!.Value;
            }
            else
            {
                custom[selected.Channel.Name] =
                    selected.Channel.DiscreteValue!.Value;
            }
        }
    }

    private static double[,] BuildResponseTable(
        RaceMenuMorphResponse[] responses,
        AnchorRow[] anchors,
        bool horizontal,
        bool negative)
    {
        var result = new double[responses.Length, anchors.Length];
        for (var column = 0; column < responses.Length; column++)
        {
            Dictionary<string, ReferenceAnchorDisplacement> byAnchor =
                (negative
                    ? responses[column].NegativeDisplacements
                    : responses[column].Displacements).ToDictionary(
                    item => Key(item.ViewRole, item.Anchor),
                    StringComparer.Ordinal);
            for (var row = 0; row < anchors.Length; row++)
            {
                if (byAnchor.TryGetValue(
                        Key(anchors[row].ViewRole, anchors[row].Anchor),
                        out ReferenceAnchorDisplacement? displacement))
                {
                    result[column, row] = horizontal
                        ? displacement.DeltaX
                        : displacement.DeltaY;
                }
            }
        }
        return result;
    }

    private static TextGoal[] BuildTextGoals(
        ReviewedReferencePresetDesign design,
        RaceMenuMorphResponse[] continuous) =>
        design.Traits
            .Where(item =>
                item.Channel == ReferenceTraitChannel.Geometry &&
                item.ReviewState is
                    (ReferenceTraitReviewState.Accepted or
                     ReferenceTraitReviewState.Corrected))
            .OrderBy(item => (int)item.Kind)
            .ThenBy(item => item.SourcePhrase,
                StringComparer.Ordinal)
            .Select(item => new TextGoal(
                item,
                item.Strength,
                Math.Clamp(item.Confidence, 0, 1) *
                    TextWeightScale,
                continuous.Any(response =>
                    TraitCoefficient(
                        item.Kind,
                        response.Channel.Name) != 0)))
            .ToArray();

    private static double[,] BuildTextCoefficientTable(
        RaceMenuMorphResponse[] responses,
        TextGoal[] goals)
    {
        var result = new double[responses.Length, goals.Length];
        for (var column = 0; column < responses.Length; column++)
        {
            for (var trait = 0; trait < goals.Length; trait++)
            {
                result[column, trait] = TraitCoefficient(
                    goals[trait].Trait.Kind,
                    responses[column].Channel.Name);
            }
        }
        return result;
    }

    private static double TraitCoefficient(
        ReferenceDescriptionTraitKind kind,
        string channelName)
    {
        string normalized = new string(channelName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        (string[] Positive, string[] Negative) tokens = kind switch
        {
            ReferenceDescriptionTraitKind.FaceLength =>
                (["facelong"], ["faceshort"]),
            ReferenceDescriptionTraitKind.FaceWidth =>
                (["facewide"], ["facenarrow"]),
            ReferenceDescriptionTraitKind.JawWidth =>
                (["jawwide"], ["jawnarrow"]),
            ReferenceDescriptionTraitKind.JawTaper =>
                (["jawtaper"], []),
            ReferenceDescriptionTraitKind.JawShape =>
                (["jawsquare"], []),
            ReferenceDescriptionTraitKind.ChinWidth =>
                (["chinwide"], ["chinthin", "chinpoint"]),
            ReferenceDescriptionTraitKind.ChinHeight =>
                (["chinmovedown", "chinlong"],
                    ["chinmoveup", "chinshort"]),
            ReferenceDescriptionTraitKind.ChinProjection =>
                (["jawforward", "chinforward"],
                    ["jawback", "chinback"]),
            ReferenceDescriptionTraitKind.CheekWidth =>
                (["cheeksout", "cheekwide"],
                    ["cheeksin", "cheeknarrow"]),
            ReferenceDescriptionTraitKind.CheekProminence =>
                (["cheeksup", "cheekprominent"], ["cheeksdown"]),
            ReferenceDescriptionTraitKind.EyeSize =>
                (["eyesize", "eyesbig"], ["eyessmall"]),
            ReferenceDescriptionTraitKind.EyeSpacing =>
                (["eyesmoveout", "eyespacing", "eyeswide"],
                    ["eyesmovein", "eyesclose"]),
            ReferenceDescriptionTraitKind.EyeCant =>
                (["eyecant", "eyesupturn"], ["eyesdownturn"]),
            ReferenceDescriptionTraitKind.EyeDepth =>
                (["eyesback", "eyedepth"], ["eyesforward"]),
            ReferenceDescriptionTraitKind.BrowHeight =>
                (["browup"], ["browdown"]),
            ReferenceDescriptionTraitKind.BrowArch =>
                (["browarch"], ["browstraight"]),
            ReferenceDescriptionTraitKind.BrowThickness =>
                (["browthick"], ["browthin"]),
            ReferenceDescriptionTraitKind.NoseBridgeWidth =>
                (["nosebridgewide"], ["nosebridgenarrow"]),
            ReferenceDescriptionTraitKind.NoseBridgeHeight =>
                (["nosebridgehigh"], ["nosebridgelow"]),
            ReferenceDescriptionTraitKind.NoseBridgeSlope =>
                (["nosebridgeslope"], []),
            ReferenceDescriptionTraitKind.NoseLength =>
                (["noselong"], ["noseshort"]),
            ReferenceDescriptionTraitKind.NoseWingWidth =>
                (["nosewide", "nosewingwide"],
                    ["nosenarrow", "nosewingnarrow"]),
            ReferenceDescriptionTraitKind.NoseTipDirection =>
                (["noseup"], ["nosedown"]),
            ReferenceDescriptionTraitKind.LipWidth =>
                (["lipmoveout", "lipwide"],
                    ["lipmovein", "lipnarrow"]),
            ReferenceDescriptionTraitKind.LipFullness =>
                (["lipfull"], ["lipthin"]),
            _ => ([], [])
        };
        if (tokens.Positive.Any(normalized.Contains))
        {
            return 1;
        }
        return tokens.Negative.Any(normalized.Contains) ? -1 : 0;
    }

    private static void EvaluateCandidate(
        int column,
        double candidate,
        double current,
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY,
        double[,] positiveX,
        double[,] positiveY,
        double[,] negativeX,
        double[,] negativeY,
        TextGoal[] textGoals,
        double[] textPredicted,
        double[,] textCoefficients,
        double[] values,
        ref double selected,
        ref double selectedObjective)
    {
        if (candidate == current || candidate == selected)
        {
            return;
        }
        double change = candidate - current;
        double objective = 0;
        for (var row = 0; row < anchors.Length; row++)
        {
            double x = predictedX[row] +
                       ContributionDifference(
                           current,
                           candidate,
                           positiveX[column, row],
                           negativeX[column, row]);
            double y = predictedY[row] +
                       ContributionDifference(
                           current,
                           candidate,
                           positiveY[column, row],
                           negativeY[column, row]);
            double dx = anchors[row].TargetX - x;
            double dy = anchors[row].TargetY - y;
            objective += anchors[row].Weight *
                         (dx * dx + dy * dy);
        }
        for (var trait = 0; trait < textGoals.Length; trait++)
        {
            double actual = textPredicted[trait] +
                            change *
                            textCoefficients[column, trait];
            double residual = textGoals[trait].Target - actual;
            objective += textGoals[trait].Weight *
                         residual * residual;
        }
        double magnitude = 0;
        for (var index = 0; index < values.Length; index++)
        {
            double value = index == column
                ? candidate
                : values[index];
            magnitude += value * value;
        }
        objective += RegularizationWeight * magnitude;
        if (objective <
            selectedObjective - ImprovementTolerance)
        {
            selected = candidate;
            selectedObjective = objective;
        }
    }

    private static double ContributionDifference(
        double current,
        double candidate,
        double positiveResponse,
        double negativeResponse) =>
        Contribution(candidate, positiveResponse, negativeResponse) -
        Contribution(current, positiveResponse, negativeResponse);

    private static double Contribution(
        double value,
        double positiveResponse,
        double negativeResponse) =>
        value >= 0
            ? value * positiveResponse
            : -value * negativeResponse;

    private static ImmutableArray<ReferenceSculptVertexDelta>
        TryBuildSculpt(
            ReferenceRaceMenuPresetSolverRequest request,
            AnchorRow[] anchors,
            double[] predictedX,
            double[] predictedY,
            double objectiveBefore,
            out double objectiveAfter,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        objectiveAfter = objectiveBefore;
        if (request.RenderInput is null ||
            request.ProtectedNeckRingVertexIndices.IsEmpty)
        {
            return [];
        }

        ReviewedReferenceView[] acceptedViews =
            request.ReviewedDesign.Views
                .Where(item => item.ReviewAccepted)
                .ToArray();
        double maximumSeparation = 0;
        for (var left = 0; left < acceptedViews.Length; left++)
        {
            for (var right = left + 1;
                 right < acceptedViews.Length;
                 right++)
            {
                maximumSeparation = Math.Max(
                    maximumSeparation,
                    Math.Abs(
                        acceptedViews[left].ReviewedYawDegrees -
                        acceptedViews[right].ReviewedYawDegrees));
            }
        }
        if (maximumSeparation <
            ReferencePresetAuthoringRules.MinimumSculptYawSeparation)
        {
            return [];
        }

        ReferenceMeshAnchorBinding[] bindings =
            request.ReviewedDesign.MeshBindings.ToArray();
        if (bindings.Length == 0 ||
            bindings.Select(item =>
                    $"{item.NifIdentity}\0{item.ShapeIdentity}\0{item.TopologySha256.Value}")
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return [];
        }
        ReferenceRenderShape[] shapes =
            request.RenderInput.Shapes.Where(item =>
                    item.NifIdentity == bindings[0].NifIdentity &&
                    item.ShapeIdentity == bindings[0].ShapeIdentity &&
                    item.TopologySha256 ==
                        bindings[0].TopologySha256)
                .ToArray();
        if (shapes.Length != 1)
        {
            return [];
        }
        ReferenceRenderShape shape = shapes[0];
        double diagonal = BoundingDiagonal(shape.Positions);
        if (!double.IsFinite(diagonal) || diagonal <= 0)
        {
            return [];
        }

        HashSet<ReferenceSemanticAnchorKind> multiViewAnchors =
            bindings.GroupBy(item => item.Anchor)
                .Where(group =>
                    HasSeparatedPair(group, acceptedViews))
                .Select(group => group.Key)
                .ToHashSet();
        if (multiViewAnchors.Count == 0)
        {
            return [];
        }

        var accumulated = new Dictionary<int, Vector3>();
        var weights = new Dictionary<int, double>();
        HashSet<int> neck =
            request.ProtectedNeckRingVertexIndices.ToHashSet();
        Dictionary<string, ReferenceMeshAnchorBinding> bindingByKey =
            bindings.ToDictionary(
                item => Key(item.ViewRole, item.Anchor),
                StringComparer.Ordinal);
        for (var row = 0; row < anchors.Length; row++)
        {
            AnchorRow anchor = anchors[row];
            if (anchor.Weight <= 0 ||
                !multiViewAnchors.Contains(anchor.Anchor) ||
                !bindingByKey.TryGetValue(
                    Key(anchor.ViewRole, anchor.Anchor),
                    out ReferenceMeshAnchorBinding? binding))
            {
                continue;
            }
            ReferenceOrthographicCamera[] cameras =
                request.RenderInput.Cameras.Where(item =>
                        item.ViewRole == anchor.ViewRole)
                    .ToArray();
            if (cameras.Length != 1 ||
                !ReferenceOrthographicProjection.TryGetBasis(
                    cameras[0],
                    out Vector3 right,
                    out Vector3 up,
                    out _))
            {
                continue;
            }

            double residualX = anchor.TargetX - predictedX[row];
            double residualY = anchor.TargetY - predictedY[row];
            Vector3 desired =
                right * (float)(residualX *
                    (cameras[0].Right - cameras[0].Left)) +
                up * (float)(-residualY *
                    (cameras[0].Top - cameras[0].Bottom));
            Vector3 normal = BindingNormal(binding, shape);
            if (!IsFinite(normal) ||
                normal.LengthSquared() <= 0.000000001F)
            {
                continue;
            }
            normal = Vector3.Normalize(normal);
            Vector3 surfaceNormalResidual =
                normal * Vector3.Dot(desired, normal);
            AddVertex(
                binding.VertexIndex0,
                binding.Barycentric0,
                surfaceNormalResidual,
                anchor.Weight,
                neck,
                accumulated,
                weights);
            AddVertex(
                binding.VertexIndex1,
                binding.Barycentric1,
                surfaceNormalResidual,
                anchor.Weight,
                neck,
                accumulated,
                weights);
            AddVertex(
                binding.VertexIndex2,
                binding.Barycentric2,
                surfaceNormalResidual,
                anchor.Weight,
                neck,
                accumulated,
                weights);
        }

        double cap = diagonal *
                     ReferencePresetAuthoringRules
                         .MaximumSculptDisplacementRatio;
        var deltas =
            ImmutableArray.CreateBuilder<ReferenceSculptVertexDelta>();
        foreach ((int vertex, Vector3 sum) in accumulated
                     .OrderBy(item => item.Key))
        {
            double divisor = weights[vertex];
            if (!double.IsFinite(divisor) || divisor <= 0)
            {
                continue;
            }
            Vector3 delta = sum / (float)divisor;
            double magnitude = delta.Length();
            if (magnitude > cap)
            {
                delta *= (float)(cap / magnitude);
            }
            if (!IsFinite(delta) ||
                delta.LengthSquared() <= 0.000000000001F)
            {
                continue;
            }
            deltas.Add(new ReferenceSculptVertexDelta(
                vertex,
                delta.X,
                delta.Y,
                delta.Z));
        }
        if (deltas.Count == 0)
        {
            return [];
        }

        ImmutableArray<ReferenceSculptVertexDelta> candidate =
            deltas.ToImmutable();
        var candidateX = predictedX.ToArray();
        var candidateY = predictedY.ToArray();
        ApplySculptProjection(
            request.RenderInput,
            bindings,
            candidate,
            anchors,
            candidateX,
            candidateY);
        double candidateObjective =
            AnchorObjective(anchors, candidateX, candidateY);
        double currentAnchorObjective =
            AnchorObjective(anchors, predictedX, predictedY);
        objectiveAfter = objectiveBefore -
                         currentAnchorObjective +
                         candidateObjective;
        var eligibility = new ReferenceSculptEligibilityRequest(
            request.ReviewedDesign.Views,
            request.ReviewedDesign.MeshBindings,
            bindings[0].TopologySha256,
            diagonal,
            candidate,
            request.ProtectedNeckRingVertexIndices,
            objectiveBefore,
            objectiveAfter);
        ImmutableArray<Diagnostic> eligibilityDiagnostics =
            ReferencePresetAuthoringRules.CanSculpt(eligibility);
        if (eligibilityDiagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.AddRange(eligibilityDiagnostics.Where(item =>
                item.Severity != DiagnosticSeverity.Error));
            objectiveAfter = objectiveBefore;
            return [];
        }
        return candidate;
    }

    private static bool HasSeparatedPair(
        IEnumerable<ReferenceMeshAnchorBinding> group,
        IEnumerable<ReviewedReferenceView> views)
    {
        Dictionary<ReferenceImageViewRole, double> yaw = views
            .ToDictionary(item => item.ViewRole,
                item => item.ReviewedYawDegrees);
        ReferenceMeshAnchorBinding[] rows = group
            .Where(item => yaw.ContainsKey(item.ViewRole))
            .ToArray();
        for (var left = 0; left < rows.Length; left++)
        {
            for (var right = left + 1;
                 right < rows.Length;
                 right++)
            {
                if (Math.Abs(yaw[rows[left].ViewRole] -
                             yaw[rows[right].ViewRole]) >=
                    ReferencePresetAuthoringRules
                        .MinimumSculptYawSeparation)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void AddVertex(
        int vertex,
        double barycentric,
        Vector3 residual,
        double anchorWeight,
        HashSet<int> neck,
        Dictionary<int, Vector3> accumulated,
        Dictionary<int, double> weights)
    {
        if (neck.Contains(vertex) ||
            barycentric <= 0 ||
            !double.IsFinite(barycentric))
        {
            return;
        }
        float multiplier =
            (float)(barycentric * anchorWeight);
        accumulated[vertex] =
            accumulated.GetValueOrDefault(vertex) +
            residual * multiplier;
        weights[vertex] =
            weights.GetValueOrDefault(vertex) +
            barycentric * barycentric * anchorWeight;
    }

    private static Vector3 BindingNormal(
        ReferenceMeshAnchorBinding binding,
        ReferenceRenderShape shape)
    {
        if (shape.Normals.Length == shape.Positions.Length)
        {
            return shape.Normals[binding.VertexIndex0] *
                       (float)binding.Barycentric0 +
                   shape.Normals[binding.VertexIndex1] *
                       (float)binding.Barycentric1 +
                   shape.Normals[binding.VertexIndex2] *
                       (float)binding.Barycentric2;
        }
        Vector3 a = shape.Positions[binding.VertexIndex0];
        Vector3 b = shape.Positions[binding.VertexIndex1];
        Vector3 c = shape.Positions[binding.VertexIndex2];
        return Vector3.Cross(b - a, c - a);
    }

    private static void ApplySculptProjection(
        ReferencePresetRenderInput renderInput,
        IEnumerable<ReferenceMeshAnchorBinding> bindings,
        ImmutableArray<ReferenceSculptVertexDelta> sculpt,
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY)
    {
        Dictionary<int, Vector3> deltas = sculpt.ToDictionary(
            item => item.VertexIndex,
            item => new Vector3(
                (float)item.X,
                (float)item.Y,
                (float)item.Z));
        Dictionary<string, ReferenceMeshAnchorBinding> byKey =
            bindings.ToDictionary(
                item => Key(item.ViewRole, item.Anchor),
                StringComparer.Ordinal);
        for (var row = 0; row < anchors.Length; row++)
        {
            if (!byKey.TryGetValue(
                    Key(anchors[row].ViewRole, anchors[row].Anchor),
                    out ReferenceMeshAnchorBinding? binding))
            {
                continue;
            }
            Vector3 delta =
                deltas.GetValueOrDefault(binding.VertexIndex0) *
                    (float)binding.Barycentric0 +
                deltas.GetValueOrDefault(binding.VertexIndex1) *
                    (float)binding.Barycentric1 +
                deltas.GetValueOrDefault(binding.VertexIndex2) *
                    (float)binding.Barycentric2;
            ReferenceOrthographicCamera camera =
                renderInput.Cameras.Single(item =>
                    item.ViewRole == binding.ViewRole);
            if (!ReferenceOrthographicProjection.TryGetBasis(
                    camera,
                    out Vector3 right,
                    out Vector3 up,
                    out _))
            {
                continue;
            }
            predictedX[row] +=
                Vector3.Dot(delta, right) /
                (camera.Right - camera.Left);
            predictedY[row] -=
                Vector3.Dot(delta, up) /
                (camera.Top - camera.Bottom);
        }
    }

    private static double BoundingDiagonal(
        ImmutableArray<Vector3> positions)
    {
        if (positions.IsDefaultOrEmpty)
        {
            return double.NaN;
        }
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        return (maximum - minimum).Length();
    }

    private static ImmutableArray<ReferencePresetResidual>
        BuildResiduals(
            AnchorRow[] anchors,
            double[] predictedX,
            double[] predictedY)
    {
        var result =
            ImmutableArray.CreateBuilder<ReferencePresetResidual>(
                anchors.Length);
        for (var index = 0; index < anchors.Length; index++)
        {
            double dx = anchors[index].TargetX - predictedX[index];
            double dy = anchors[index].TargetY - predictedY[index];
            result.Add(new ReferencePresetResidual(
                anchors[index].ViewRole,
                anchors[index].Anchor,
                anchors[index].TargetX,
                anchors[index].TargetY,
                predictedX[index],
                predictedY[index],
                anchors[index].Weight,
                anchors[index].Weight *
                    (dx * dx + dy * dy)));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<ReferencePresetLoss> BuildLosses(
        ReviewedReferencePresetDesign design,
        ImmutableArray<ReferencePresetResidual> residuals,
        TextGoal[] textGoals,
        RaceMenuMorphResponse[] continuous,
        ImmutableArray<ReferenceSculptVertexDelta> sculpt)
    {
        var losses =
            ImmutableArray.CreateBuilder<ReferencePresetLoss>();
        foreach (ReferencePresetResidual residual in residuals)
        {
            if (residual.Weight == 0)
            {
                losses.Add(new ReferencePresetLoss(
                    $"unknown-{residual.ViewRole.ToWireName()}-{residual.Anchor.ToWireName()}",
                    ReferencePresetLossKind.UnknownAnchor,
                    $"Anchor '{residual.Anchor.ToWireName()}' in '{residual.ViewRole.ToWireName()}' was explicitly unknown and had zero solver weight.",
                    true));
            }
            else if (residual.SquaredLoss >
                     ResidualLossThreshold)
            {
                losses.Add(new ReferencePresetLoss(
                    $"residual-{residual.ViewRole.ToWireName()}-{residual.Anchor.ToWireName()}",
                    ReferencePresetLossKind.ResidualGeometry,
                    FormattableString.Invariant($"Anchor '{residual.Anchor.ToWireName()}' retains normalized squared loss {residual.SquaredLoss:R}."),
                    false));
            }
        }
        foreach (TextGoal goal in textGoals)
        {
            if (!goal.Supported)
            {
                losses.Add(new ReferencePresetLoss(
                    $"unsupported-trait-{goal.Trait.Kind}",
                    ReferencePresetLossKind.UnsupportedMorph,
                    $"Accepted geometry trait '{goal.Trait.Kind}' has no legal response channel.",
                    false));
            }
            if (goal.Trait.ConflictAcknowledged)
            {
                losses.Add(new ReferencePresetLoss(
                    $"trait-conflict-{goal.Trait.Kind}",
                    ReferencePresetLossKind.ImageDescriptionConflict,
                    $"Description trait '{goal.Trait.Kind}' conflicts with reviewed image geometry; image authority retained priority.",
                    true));
            }
        }
        if (sculpt.IsEmpty)
        {
            losses.Add(new ReferencePresetLoss(
                "sculpt-unavailable",
                ReferencePresetLossKind.SculptUnavailable,
                "Multi-view sculpt was unavailable, ineligible, empty, or did not strictly improve the objective.",
                false));
        }
        _ = design;
        _ = continuous;
        return losses.ToImmutable();
    }

    private static double Objective(
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY,
        TextGoal[] textGoals,
        double[] textPredicted,
        double[] values)
    {
        double objective =
            AnchorObjective(anchors, predictedX, predictedY);
        for (var index = 0; index < textGoals.Length; index++)
        {
            double residual =
                textGoals[index].Target - textPredicted[index];
            objective += textGoals[index].Weight *
                         residual * residual;
        }
        objective += RegularizationWeight *
                     values.Sum(value => value * value);
        return objective;
    }

    private static double AnchorObjective(
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY)
    {
        double objective = 0;
        for (var row = 0; row < anchors.Length; row++)
        {
            double dx = anchors[row].TargetX - predictedX[row];
            double dy = anchors[row].TargetY - predictedY[row];
            objective += anchors[row].Weight *
                         (dx * dx + dy * dy);
        }
        return objective;
    }

    private static double AnchorObjectiveWithResponse(
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY,
        RaceMenuMorphResponse response)
    {
        Dictionary<string, ReferenceAnchorDisplacement> displacements =
            response.Displacements.ToDictionary(
                item => Key(item.ViewRole, item.Anchor),
                StringComparer.Ordinal);
        double objective = 0;
        for (var row = 0; row < anchors.Length; row++)
        {
            displacements.TryGetValue(
                Key(anchors[row].ViewRole, anchors[row].Anchor),
                out ReferenceAnchorDisplacement? displacement);
            double x = predictedX[row] +
                       (displacement?.DeltaX ?? 0);
            double y = predictedY[row] +
                       (displacement?.DeltaY ?? 0);
            double dx = anchors[row].TargetX - x;
            double dy = anchors[row].TargetY - y;
            objective += anchors[row].Weight *
                         (dx * dx + dy * dy);
        }
        return objective;
    }

    private static void ApplyResponse(
        RaceMenuMorphResponse response,
        AnchorRow[] anchors,
        double[] predictedX,
        double[] predictedY)
    {
        Dictionary<string, ReferenceAnchorDisplacement> displacements =
            response.Displacements.ToDictionary(
                item => Key(item.ViewRole, item.Anchor),
                StringComparer.Ordinal);
        for (var row = 0; row < anchors.Length; row++)
        {
            if (displacements.TryGetValue(
                    Key(anchors[row].ViewRole, anchors[row].Anchor),
                    out ReferenceAnchorDisplacement? displacement))
            {
                predictedX[row] += displacement.DeltaX;
                predictedY[row] += displacement.DeltaY;
            }
        }
    }

    private static Sha256Hash HashResult(
        ReferenceRaceMenuPresetSolverRequest request,
        ImmutableDictionary<string, double> native,
        ImmutableDictionary<string, double> custom,
        ImmutableArray<ReferenceSculptVertexDelta> sculpt,
        ImmutableArray<ReferencePresetResidual> residuals,
        ImmutableArray<ReferencePresetLoss> losses,
        double objective)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash,
            request.ResponseMatrix.MatrixSha256!.Value.Value);
        Append(hash, request.Snapshot.ResourceFingerprint.Value);
        AppendInt32(hash,
            ReferencePresetAuthoringRules.SolverIterations);
        AppendDouble(hash, objective);
        foreach ((string name, double value) in native
                     .OrderBy(item => item.Key,
                         StringComparer.Ordinal))
        {
            Append(hash, "native");
            Append(hash, name);
            AppendDouble(hash, value);
        }
        foreach ((string name, double value) in custom
                     .OrderBy(item => item.Key,
                         StringComparer.Ordinal))
        {
            Append(hash, "custom");
            Append(hash, name);
            AppendDouble(hash, value);
        }
        foreach (ReferenceSculptVertexDelta delta in sculpt)
        {
            AppendInt32(hash, delta.VertexIndex);
            AppendDouble(hash, delta.X);
            AppendDouble(hash, delta.Y);
            AppendDouble(hash, delta.Z);
        }
        foreach (ReferencePresetResidual residual in residuals)
        {
            AppendInt32(hash, (int)residual.ViewRole);
            AppendInt32(hash, (int)residual.Anchor);
            AppendDouble(hash, residual.TargetX);
            AppendDouble(hash, residual.TargetY);
            AppendDouble(hash, residual.ActualX);
            AppendDouble(hash, residual.ActualY);
            AppendDouble(hash, residual.Weight);
            AppendDouble(hash, residual.SquaredLoss);
        }
        foreach (ReferencePresetLoss loss in losses)
        {
            Append(hash, loss.Code);
            AppendInt32(hash, (int)loss.Kind);
            Append(hash, loss.Message);
            AppendInt32(hash, loss.Acknowledged ? 1 : 0);
        }
        return new Sha256Hash(
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void Append(
        IncrementalHash hash,
        string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendDouble(
        IncrementalHash hash,
        double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes, BitConverter.DoubleToInt64Bits(value));
        hash.AppendData(bytes);
    }

    private static double Quantize(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static double QuantizeObjective(double value) =>
        Math.Round(value, 12, MidpointRounding.AwayFromZero);

    private static string Key(
        ReferenceImageViewRole role,
        ReferenceSemanticAnchorKind anchor) =>
        $"{role.ToWireName()}\0{anchor.ToWireName()}";

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsHashBound(Sha256Hash hash) =>
        hash.Value is { Length: 64 } &&
        hash.Value.Any(character => character != '0');

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferenceRaceMenuPresetSolverResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            [],
            [],
            [],
            0,
            double.NaN,
            null,
            diagnostics.ToImmutable());

    private sealed record AnchorRow(
        ReferenceImageViewRole ViewRole,
        ReferenceSemanticAnchorKind Anchor,
        double BaselineX,
        double BaselineY,
        double TargetX,
        double TargetY,
        double Weight);

    private sealed record TextGoal(
        ReferenceDescriptionTrait Trait,
        double Target,
        double Weight,
        bool Supported);
}
