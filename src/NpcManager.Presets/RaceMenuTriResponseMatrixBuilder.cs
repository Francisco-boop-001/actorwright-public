using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Builds deterministic 2D response columns by routing every admitted channel
/// through the same face-plan builder and evaluator used by FaceGeom baking.
/// </summary>
public sealed class RaceMenuTriResponseMatrixBuilder(
    ISseFaceMorphPlanBuilder planBuilder,
    ISseFaceMorphEvaluator evaluator)
    : IRaceMenuTriResponseMatrixBuilder
{
    public ValueTask<RaceMenuTriResponseMatrixBuildResult> BuildAsync(
        RaceMenuTriResponseMatrixBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Sha256Hash reviewedDesignSha256 =
            IsHashBound(request.ReviewedDesignSha256)
                ? request.ReviewedDesignSha256
                : request.Snapshot.ReviewedDesignSha256;
        ImmutableArray<ReferenceFaceMorphShapeBasis> morphBases =
            request.MorphBases.IsDefaultOrEmpty
                ? request.Snapshot.MorphBases
                : request.MorphBases;

        diagnostics.AddRange(ReferencePresetAuthoringRules.CanSolve(
            request.ReviewedDesign,
            request.ReviewedDesign.ProposalSha256));
        if (request.Snapshot.SchemaVersion != 1 ||
            request.Snapshot.ReviewedDesignSha256 !=
            reviewedDesignSha256 ||
            !IsHashBound(request.Snapshot.ResourceFingerprint))
        {
            diagnostics.Add(Error(
                "reference-response-snapshot",
                "The response matrix requires the exact reviewed schema-1 resource snapshot."));
        }
        if (request.RenderInput is null ||
            !IsHashBound(request.RenderInput.InputSha256) ||
            request.ReviewedDesign.MeshBindings.Any(binding =>
                binding.RenderSha256 != request.RenderInput.InputSha256))
        {
            diagnostics.Add(Error(
                "reference-response-render-drift",
                "The response render input no longer matches every reviewed mesh binding."));
        }
        if (morphBases.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "reference-response-basis",
                "The per-shape face-morph basis array must be explicit."));
        }
        if (morphBases
                .GroupBy(item =>
                    $"{item.NifIdentity}\0{item.ShapeIdentity}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            diagnostics.Add(Error(
                "reference-response-basis-duplicate",
                "A NIF/shape may have only one exact face-morph basis."));
        }
        if (request.Snapshot.MorphChannels
                .GroupBy(item => item.Ordinal)
                .Any(group => group.Count() != 1))
        {
            diagnostics.Add(Error(
                "reference-response-channel-ordinal",
                "Resource morph-channel ordinals must be globally unique."));
        }
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(Refused(diagnostics));
        }
        ReferencePresetRenderInput renderInput = request.RenderInput!;

        var baselines =
            ImmutableArray.CreateBuilder<ReferenceAnchorProjectionBaseline>();
        var responses =
            ImmutableArray.CreateBuilder<RaceMenuMorphResponse>();
        HashSet<string> completedBindings = new(StringComparer.Ordinal);
        HashSet<int> completedChannels = [];

        foreach (ReferenceFaceMorphShapeBasis basis in
                 morphBases.OrderBy(item => item.NifIdentity,
                         StringComparer.Ordinal)
                     .ThenBy(item => item.ShapeIdentity,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (basis is null || basis.PlanTemplate is null)
            {
                diagnostics.Add(Error(
                    "reference-response-basis",
                    "Every response basis requires exact NIF, shape, and face-plan input."));
                continue;
            }

            ReferenceRenderShape[] shapes = renderInput.Shapes
                .Where(item =>
                    string.Equals(item.NifIdentity, basis.NifIdentity,
                        StringComparison.Ordinal) &&
                    string.Equals(item.ShapeIdentity, basis.ShapeIdentity,
                        StringComparison.Ordinal))
                .ToArray();
            if (shapes.Length != 1)
            {
                diagnostics.Add(Error(
                    "reference-response-shape",
                    $"Morph basis '{basis.NifIdentity}/{basis.ShapeIdentity}' does not resolve to one render shape."));
                continue;
            }
            ReferenceRenderShape shape = shapes[0];
            ImmutableArray<Vector3> sourcePositions =
                SourcePositions(shape);
            if (basis.PlanTemplate.VertexCount != sourcePositions.Length)
            {
                diagnostics.Add(Error(
                    "reference-response-topology",
                    $"Morph basis '{basis.ShapeIdentity}' has {basis.PlanTemplate.VertexCount} vertices but the reviewed shape has {sourcePositions.Length}."));
                continue;
            }

            ReferenceMeshAnchorBinding[] bindings =
                request.ReviewedDesign.MeshBindings
                    .Where(item =>
                        string.Equals(item.NifIdentity,
                            basis.NifIdentity,
                            StringComparison.Ordinal) &&
                        string.Equals(item.ShapeIdentity,
                            basis.ShapeIdentity,
                            StringComparison.Ordinal))
                    .OrderBy(item => (int)item.ViewRole)
                    .ThenBy(item => (int)item.Anchor)
                    .ToArray();
            if (bindings.Length == 0)
            {
                // Selected eyes, mouth, brows, hair, and extra parts may have
                // valid morph bases, but reviewed semantic bindings are
                // intentionally restricted to the selected face/head surface.
                continue;
            }
            if (!ValidateBindings(
                    bindings,
                    shape,
                    renderInput,
                    diagnostics))
            {
                continue;
            }

            SkyrimFaceMorphPlanBuildRequest neutral =
                Neutralize(basis.PlanTemplate);
            SkyrimFaceMorphPlanBuildResult baselinePlan =
                planBuilder.Build(neutral);
            diagnostics.AddRange(baselinePlan.Diagnostics);
            if (!baselinePlan.Accepted ||
                baselinePlan.Plan is null)
            {
                diagnostics.Add(Error(
                    "reference-response-baseline-plan",
                    $"The exact baseline face plan for '{basis.ShapeIdentity}' was refused."));
                continue;
            }
            SkyrimFaceMorphEvaluationResult baselineEvaluation =
                evaluator.Evaluate(new SkyrimFaceMorphEvaluationRequest(
                    sourcePositions, baselinePlan.Plan));
            diagnostics.AddRange(baselineEvaluation.Diagnostics);
            if (!baselineEvaluation.Accepted)
            {
                diagnostics.Add(Error(
                    "reference-response-baseline-evaluation",
                    $"The exact baseline face evaluation for '{basis.ShapeIdentity}' was refused."));
                continue;
            }
            ImmutableArray<Vector3> baselineRenderPositions =
                ToRenderPositions(shape, baselineEvaluation.Positions);

            foreach (ReferenceMeshAnchorBinding binding in bindings)
            {
                if (!TryProjectBinding(
                        binding,
                        baselineRenderPositions,
                        renderInput,
                        out double x,
                        out double y))
                {
                    diagnostics.Add(Error(
                        "reference-response-baseline-projection",
                        $"Baseline anchor '{binding.Anchor.ToWireName()}' could not be projected."));
                    continue;
                }
                ReferenceSemanticAnchor? target = FindAnchor(
                    request.ReviewedDesign, binding);
                double weight = TargetWeight(target);
                baselines.Add(new ReferenceAnchorProjectionBaseline(
                    binding.ViewRole,
                    binding.Anchor,
                    x,
                    y,
                    weight));
                completedBindings.Add(BindingKey(binding));
            }

            foreach (ReferenceMorphChannelAuthority channel in
                     request.Snapshot.MorphChannels
                         .Where(item =>
                             string.Equals(item.NifIdentity,
                                 basis.NifIdentity,
                                 StringComparison.Ordinal) &&
                             string.Equals(item.ShapeIdentity,
                                 basis.ShapeIdentity,
                                 StringComparison.Ordinal))
                         .OrderBy(item => item.Ordinal)
                         .ThenBy(item => item.Name,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidateChannel(channel, shape, diagnostics))
                {
                    continue;
                }

                ImmutableArray<ReferenceAnchorDisplacement> positive =
                    BuildProbeDisplacements(
                        neutral,
                        channel,
                        negative: false,
                        shape,
                        bindings,
                        baselineRenderPositions,
                        renderInput,
                        diagnostics);
                ImmutableArray<ReferenceAnchorDisplacement> negative =
                    channel.IsDiscrete || channel.Minimum >= 0
                        ? []
                        : BuildProbeDisplacements(
                            neutral,
                            channel,
                            negative: true,
                            shape,
                            bindings,
                            baselineRenderPositions,
                            renderInput,
                            diagnostics);
                if (HasErrors(diagnostics))
                {
                    continue;
                }
                if (((channel.IsDiscrete || channel.Maximum > 0) &&
                     positive.IsEmpty) ||
                    (!channel.IsDiscrete &&
                     channel.Minimum < 0 &&
                     negative.IsEmpty))
                {
                    diagnostics.Add(new Diagnostic(
                        "reference-response-channel-unsupported",
                        DiagnosticSeverity.Warning,
                        $"Morph channel '{channel.Name}' does not have every declared positive/negative face-plan response and was excluded."));
                    continue;
                }

                Sha256Hash morphHash = HashMorph(channel);
                responses.Add(new RaceMenuMorphResponse(
                    channel,
                    positive,
                    shape.TopologySha256,
                    HashCameraSet(bindings, renderInput),
                    request.Snapshot.ResourceFingerprint)
                {
                    MorphSha256 = morphHash,
                    NegativeDisplacements = negative
                });
                completedChannels.Add(channel.Ordinal);
            }
        }

        foreach (ReferenceMeshAnchorBinding binding in
                 request.ReviewedDesign.MeshBindings)
        {
            if (!completedBindings.Contains(BindingKey(binding)))
            {
                diagnostics.Add(Error(
                    "reference-response-binding-unresolved",
                    $"Reviewed binding '{binding.ViewRole.ToWireName()}/{binding.Anchor.ToWireName()}' was not evaluated by any exact shape basis."));
            }
        }
        HashSet<string> boundShapeKeys =
            request.ReviewedDesign.MeshBindings.Select(binding =>
                    $"{binding.NifIdentity}\0{binding.ShapeIdentity}")
                .ToHashSet(StringComparer.Ordinal);
        foreach (ReferenceMorphChannelAuthority channel in
                 request.Snapshot.MorphChannels.Where(item =>
                     boundShapeKeys.Contains(
                         $"{item.NifIdentity}\0{item.ShapeIdentity}")))
        {
            if (!completedChannels.Contains(channel.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    "reference-response-channel-excluded",
                    DiagnosticSeverity.Warning,
                    $"Morph channel '{channel.Name}' is absent from the legal response matrix."));
            }
        }
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(Refused(diagnostics));
        }

        ImmutableArray<ReferenceAnchorProjectionBaseline> baselineRows =
            baselines
                .OrderBy(item => (int)item.ViewRole)
                .ThenBy(item => (int)item.Anchor)
                .ToImmutableArray();
        ImmutableArray<RaceMenuMorphResponse> responseRows =
            responses
                .OrderBy(item => item.Channel.Ordinal)
                .ThenBy(item => item.Channel.Name,
                    StringComparer.Ordinal)
                .ToImmutableArray();
        Sha256Hash matrixHash = HashMatrix(
            request.Snapshot.ResourceFingerprint,
            reviewedDesignSha256,
            renderInput.InputSha256,
            baselineRows,
            responseRows);
        diagnostics.Add(new Diagnostic(
            "reference-response-built",
            DiagnosticSeverity.Info,
            $"Built {responseRows.Length} exact morph response column(s) over {baselineRows.Length} reviewed mesh anchor(s)."));
        return ValueTask.FromResult(
            new RaceMenuTriResponseMatrixBuildResult(
                responseRows,
                matrixHash,
                diagnostics.ToImmutable())
            {
                BaselineProjections = baselineRows,
                RenderInputSha256 = renderInput.InputSha256,
                ReviewedDesignSha256 = reviewedDesignSha256
            });
    }

    private static SkyrimFaceMorphPlanBuildRequest Neutralize(
        SkyrimFaceMorphPlanBuildRequest template) =>
        template with
        {
            NativeMorphs = new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, 18).ToImmutableArray(),
                template.NativeMorphs?.Nam9Trailing ?? 0,
                Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray(),
                true,
                true),
            CustomMorphs = [],
            SculptParts = [],
            RequireAllCustomMorphs = true
        };

    private ImmutableArray<ReferenceAnchorDisplacement>
        BuildProbeDisplacements(
        SkyrimFaceMorphPlanBuildRequest neutral,
        ReferenceMorphChannelAuthority channel,
        bool negative,
        ReferenceRenderShape shape,
        ReferenceMeshAnchorBinding[] bindings,
        ImmutableArray<Vector3> baselinePositions,
        ReferencePresetRenderInput renderInput,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryBuildProbeRequest(
                neutral,
                channel,
                negative,
                out SkyrimFaceMorphPlanBuildRequest probeRequest,
                out double probeMagnitude,
                out ImmutableArray<SseTriHeadVertexDelta>
                    expectedDeltas))
        {
            return [];
        }
        SkyrimFaceMorphPlanBuildResult probePlan =
            planBuilder.Build(probeRequest);
        diagnostics.AddRange(probePlan.Diagnostics);
        if (!probePlan.Accepted || probePlan.Plan is null ||
            !PlanContainsChannel(
                probePlan.Plan, channel, expectedDeltas))
        {
            diagnostics.Add(Error(
                "reference-response-channel-plan",
                $"Face-plan construction did not prove the {(negative ? "negative" : "positive")} side of morph channel '{channel.Name}'."));
            return [];
        }
        SkyrimFaceMorphEvaluationResult probeEvaluation =
            evaluator.Evaluate(new SkyrimFaceMorphEvaluationRequest(
                SourcePositions(shape), probePlan.Plan));
        diagnostics.AddRange(probeEvaluation.Diagnostics);
        if (!probeEvaluation.Accepted)
        {
            diagnostics.Add(Error(
                "reference-response-channel-evaluation",
                $"Face evaluation refused the {(negative ? "negative" : "positive")} side of morph channel '{channel.Name}'."));
            return [];
        }
        ImmutableArray<Vector3> probeRenderPositions =
            ToRenderPositions(shape, probeEvaluation.Positions);

        var displacements =
            ImmutableArray.CreateBuilder<ReferenceAnchorDisplacement>(
                bindings.Length);
        foreach (ReferenceMeshAnchorBinding binding in bindings)
        {
            if (!TryProjectBinding(
                    binding,
                    baselinePositions,
                    renderInput,
                    out double baselineX,
                    out double baselineY) ||
                !TryProjectBinding(
                    binding,
                    probeRenderPositions,
                    renderInput,
                    out double probeX,
                    out double probeY))
            {
                diagnostics.Add(Error(
                    "reference-response-channel-projection",
                    $"Morph channel '{channel.Name}' could not project anchor '{binding.Anchor.ToWireName()}'."));
                continue;
            }
            double divisor = channel.IsDiscrete
                ? 1.0
                : Math.Abs(probeMagnitude);
            displacements.Add(new ReferenceAnchorDisplacement(
                binding.ViewRole,
                binding.Anchor,
                (probeX - baselineX) / divisor,
                (probeY - baselineY) / divisor));
        }
        return displacements.ToImmutable();
    }

    private static bool TryBuildProbeRequest(
        SkyrimFaceMorphPlanBuildRequest neutral,
        ReferenceMorphChannelAuthority channel,
        bool negative,
        out SkyrimFaceMorphPlanBuildRequest request,
        out double probeMagnitude,
        out ImmutableArray<SseTriHeadVertexDelta> expectedDeltas)
    {
        request = neutral;
        probeMagnitude = 1.0;
        expectedDeltas = channel.Deltas;
        if (channel.IsDiscrete)
        {
            if (negative ||
                channel.DiscreteValue is null or < 0)
            {
                return false;
            }
            if (channel.Kind == ReferenceMorphChannelKind.NativePreset)
            {
                if (channel.DiscreteFamilyOrdinal is not >= 0 or > 3)
                {
                    return false;
                }
                uint[] values =
                    Enumerable.Repeat(uint.MaxValue, 4).ToArray();
                values[channel.DiscreteFamilyOrdinal.Value] =
                    checked((uint)channel.DiscreteValue.Value);
                request = neutral with
                {
                    NativeMorphs = neutral.NativeMorphs with
                    {
                        NamaValues = values.ToImmutableArray(),
                        HasNama = true
                    }
                };
            }
            else
            {
                request = neutral with
                {
                    CustomMorphs =
                    [
                        new SkyrimRaceMenuCustomMorphValue(
                            channel.Name,
                            channel.DiscreteValue.Value)
                    ]
                };
            }
            return true;
        }

        probeMagnitude = ChooseProbe(
            channel.Minimum, channel.Maximum, negative);
        if (!double.IsFinite(probeMagnitude) ||
            Math.Abs(probeMagnitude) <= 0.000000001)
        {
            return false;
        }
        if (channel.Kind == ReferenceMorphChannelKind.Custom)
        {
            request = neutral with
            {
                CustomMorphs =
                [
                    new SkyrimRaceMenuCustomMorphValue(
                        channel.Name,
                        checked((float)probeMagnitude))
                ]
            };
            expectedDeltas =
                negative && !channel.NegativeDeltas.IsEmpty
                    ? channel.NegativeDeltas
                    : channel.Deltas;
            return true;
        }
        if (channel.Kind != ReferenceMorphChannelKind.NativePreset ||
            !TryParseNam9(channel.Name, out int index))
        {
            return false;
        }

        float[] sliders = Enumerable.Repeat(0F, 18).ToArray();
        sliders[index] = checked((float)probeMagnitude);
        request = neutral with
        {
            NativeMorphs = neutral.NativeMorphs with
            {
                Nam9Sliders = sliders.ToImmutableArray(),
                HasNam9 = true
            }
        };
        expectedDeltas =
            negative && !channel.NegativeDeltas.IsEmpty
                ? channel.NegativeDeltas
                : channel.Deltas;
        return true;
    }

    private static double ChooseProbe(
        double minimum,
        double maximum,
        bool negative)
    {
        double boundedMinimum = Math.Max(-1.0, minimum);
        double boundedMaximum = Math.Min(1.0, maximum);
        if (!double.IsFinite(boundedMinimum) ||
            !double.IsFinite(boundedMaximum) ||
            boundedMinimum > boundedMaximum)
        {
            return double.NaN;
        }
        if (negative)
        {
            return boundedMinimum < 0
                ? boundedMinimum
                : double.NaN;
        }
        if (boundedMaximum > 0)
        {
            return boundedMaximum;
        }
        return double.NaN;
    }

    private static bool TryParseNam9(string value, out int index)
    {
        index = -1;
        if (!value.StartsWith("NAM9[", StringComparison.Ordinal) ||
            !value.EndsWith(']') ||
            !int.TryParse(
                value.AsSpan(5, value.Length - 6),
                out index))
        {
            return false;
        }
        return index is >= 0 and < 18;
    }

    private static bool PlanContainsChannel(
        SkyrimFaceMorphPlan plan,
        ReferenceMorphChannelAuthority channel,
        ImmutableArray<SseTriHeadVertexDelta> expectedDeltas)
    {
        if (channel.IsDiscrete)
        {
            if (channel.Kind ==
                ReferenceMorphChannelKind.NativePreset)
            {
                string detail =
                    $"NAMA[{channel.DiscreteFamilyOrdinal!.Value}]";
                return plan.Channels.Any(item =>
                    item.Contributions.Any(contribution =>
                        contribution.Kind ==
                            SkyrimFaceMorphContributionKind.Nama &&
                        contribution.Detail == detail) &&
                    DeltasEqual(item.Deltas, expectedDeltas));
            }
            return plan.ResolvedCustomMorphNames.Any(name =>
                       string.Equals(name, channel.Name,
                           StringComparison.OrdinalIgnoreCase)) &&
                   plan.Channels.Any(item =>
                       item.Contributions.Any(contribution =>
                           contribution.Kind ==
                               SkyrimFaceMorphContributionKind
                                   .RaceMenuCustom) &&
                       DeltasEqual(item.Deltas, expectedDeltas));
        }
        if (channel.Kind == ReferenceMorphChannelKind.NativePreset)
        {
            return TryParseNam9(channel.Name, out int index) &&
                   plan.Channels.Any(item =>
                       item.Contributions.Any(contribution =>
                           contribution.Kind ==
                               SkyrimFaceMorphContributionKind.Nam9 &&
                           contribution.Detail == $"NAM9[{index}]") &&
                       DeltasEqual(item.Deltas, expectedDeltas));
        }
        return plan.ResolvedCustomMorphNames.Any(name =>
                   string.Equals(name, channel.Name,
                       StringComparison.OrdinalIgnoreCase)) &&
               plan.Channels.Any(item =>
                   item.Contributions.Any(contribution =>
                       contribution.Kind ==
                           SkyrimFaceMorphContributionKind.RaceMenuCustom) &&
                   DeltasEqual(item.Deltas, expectedDeltas));
    }

    private static bool DeltasEqual(
        ImmutableArray<SseTriHeadVertexDelta> left,
        ImmutableArray<SseTriHeadVertexDelta> right)
    {
        if (left.IsDefault || right.IsDefault ||
            left.Length != right.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].VertexIndex != right[index].VertexIndex ||
                left[index].Delta != right[index].Delta)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateBindings(
        IEnumerable<ReferenceMeshAnchorBinding> bindings,
        ReferenceRenderShape shape,
        ReferencePresetRenderInput input,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool valid = true;
        foreach (ReferenceMeshAnchorBinding binding in bindings)
        {
            ReferenceOrthographicCamera[] cameras = input.Cameras
                .Where(item => item.ViewRole == binding.ViewRole)
                .ToArray();
            if (binding.NifSha256 != shape.NifSha256 ||
                binding.TopologySha256 != shape.TopologySha256 ||
                binding.RestPositionsSha256 != shape.RestPositionsSha256 ||
                binding.RenderSha256 != input.InputSha256 ||
                cameras.Length != 1 ||
                cameras[0].CameraSha256 != binding.CameraSha256 ||
                !TriangleMatches(binding, shape))
            {
                diagnostics.Add(Error(
                    "reference-response-binding-drift",
                    $"Reviewed binding '{binding.ViewRole.ToWireName()}/{binding.Anchor.ToWireName()}' no longer matches exact geometry or camera authority."));
                valid = false;
            }
        }
        return valid;
    }

    private static bool ValidateChannel(
        ReferenceMorphChannelAuthority channel,
        ReferenceRenderShape shape,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (channel is null ||
            string.IsNullOrWhiteSpace(channel.Name) ||
            channel.Ordinal < 0 ||
            !double.IsFinite(channel.Minimum) ||
            !double.IsFinite(channel.Maximum) ||
            channel.Minimum > channel.Maximum ||
            channel.Minimum < -1 ||
            channel.Maximum > 1 ||
            !IsHashBound(channel.TriSha256) ||
            channel.Deltas.IsDefault ||
            channel.Deltas.Any(item =>
                item.VertexIndex < 0 ||
                item.VertexIndex >= shape.Positions.Length ||
                !IsFinite(item.Delta)) ||
            channel.Deltas.Select(item => item.VertexIndex)
                .Distinct().Count() != channel.Deltas.Length ||
            channel.NegativeDeltas.IsDefault ||
            channel.NegativeDeltas.Any(item =>
                item.VertexIndex < 0 ||
                item.VertexIndex >= shape.Positions.Length ||
                !IsFinite(item.Delta)) ||
            channel.NegativeDeltas.Select(item => item.VertexIndex)
                .Distinct().Count() !=
                channel.NegativeDeltas.Length ||
            channel.Kind ==
                ReferenceMorphChannelKind.NativePreset &&
            !channel.IsDiscrete &&
            channel.Minimum < 0 &&
            (channel.NegativeDeltas.IsEmpty ||
             channel.NegativeTriSha256 is not { } negativeHash ||
             !IsHashBound(negativeHash)))
        {
            diagnostics.Add(Error(
                "reference-response-channel",
                $"Morph channel '{channel?.Name}' has invalid range, TRI, or delta authority."));
            return false;
        }
        if (channel.IsDiscrete &&
            (channel.DiscreteValue is null or < 0 ||
             channel.Kind ==
                 ReferenceMorphChannelKind.NativePreset &&
             channel.DiscreteFamilyOrdinal is not >= 0 or > 3))
        {
            diagnostics.Add(Error(
                "reference-response-discrete",
                $"Discrete channel '{channel.Name}' has no legal native/custom preset identity."));
            return false;
        }
        return true;
    }

    private static bool TryProjectBinding(
        ReferenceMeshAnchorBinding binding,
        ImmutableArray<Vector3> positions,
        ReferencePresetRenderInput input,
        out double x,
        out double y)
    {
        x = 0;
        y = 0;
        if (binding.VertexIndex0 >= positions.Length ||
            binding.VertexIndex1 >= positions.Length ||
            binding.VertexIndex2 >= positions.Length)
        {
            return false;
        }
        ReferenceOrthographicCamera[] cameras = input.Cameras
            .Where(item => item.ViewRole == binding.ViewRole)
            .ToArray();
        if (cameras.Length != 1)
        {
            return false;
        }
        Vector3 point =
            positions[binding.VertexIndex0] *
                (float)binding.Barycentric0 +
            positions[binding.VertexIndex1] *
                (float)binding.Barycentric1 +
            positions[binding.VertexIndex2] *
                (float)binding.Barycentric2;
        return ReferenceOrthographicProjection.TryProject(
            cameras[0], point, out x, out y, out _);
    }

    private static bool TriangleMatches(
        ReferenceMeshAnchorBinding binding,
        ReferenceRenderShape shape)
    {
        int offset = binding.TriangleOrdinal * 3;
        return binding.TriangleOrdinal >= 0 &&
               offset + 2 < shape.TriangleIndices.Length &&
               shape.TriangleIndices[offset] == binding.VertexIndex0 &&
               shape.TriangleIndices[offset + 1] == binding.VertexIndex1 &&
               shape.TriangleIndices[offset + 2] == binding.VertexIndex2;
    }

    private static ReferenceSemanticAnchor? FindAnchor(
        ReviewedReferencePresetDesign design,
        ReferenceMeshAnchorBinding binding) =>
        design.Views
            .Where(item => item.ViewRole == binding.ViewRole)
            .SelectMany(item => item.Anchors)
            .SingleOrDefault(item => item.Anchor == binding.Anchor);

    private static double TargetWeight(ReferenceSemanticAnchor? anchor)
    {
        if (anchor is null ||
            anchor.ReviewState is not
                (ReferenceAnchorReviewState.Accepted or
                 ReferenceAnchorReviewState.Corrected) ||
            !double.IsFinite(anchor.Confidence))
        {
            return 0;
        }
        return Math.Clamp(anchor.Confidence, 0, 1);
    }

    private static string BindingKey(ReferenceMeshAnchorBinding binding) =>
        $"{binding.ViewRole.ToWireName()}\0{binding.Anchor.ToWireName()}";

    private static Sha256Hash HashMorph(
        ReferenceMorphChannelAuthority channel)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ((int)channel.Kind).ToString());
        Append(hash, channel.Name);
        AppendInt32(hash, channel.Ordinal);
        AppendDouble(hash, channel.Minimum);
        AppendDouble(hash, channel.Maximum);
        Append(hash, channel.TriSha256.Value);
        Append(hash, channel.NegativeTriSha256?.Value ?? string.Empty);
        Append(hash, channel.NifIdentity);
        Append(hash, channel.ShapeIdentity);
        AppendInt32(hash, channel.IsDiscrete ? 1 : 0);
        AppendInt32(hash, channel.DiscreteFamilyOrdinal ?? -1);
        AppendInt32(hash, channel.DiscreteValue ?? -1);
        foreach (SseTriHeadVertexDelta delta in channel.Deltas)
        {
            AppendInt32(hash, delta.VertexIndex);
            AppendSingle(hash, delta.Delta.X);
            AppendSingle(hash, delta.Delta.Y);
            AppendSingle(hash, delta.Delta.Z);
        }
        foreach (SseTriHeadVertexDelta delta in
                 channel.NegativeDeltas)
        {
            AppendInt32(hash, delta.VertexIndex);
            AppendSingle(hash, delta.Delta.X);
            AppendSingle(hash, delta.Delta.Y);
            AppendSingle(hash, delta.Delta.Z);
        }
        return Finish(hash);
    }

    private static Sha256Hash HashCameraSet(
        IEnumerable<ReferenceMeshAnchorBinding> bindings,
        ReferencePresetRenderInput input)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ReferenceImageViewRole role in bindings
                     .Select(item => item.ViewRole)
                     .Distinct()
                     .OrderBy(item => (int)item))
        {
            ReferenceOrthographicCamera camera =
                input.Cameras.Single(item => item.ViewRole == role);
            Append(hash, role.ToWireName());
            Append(hash, camera.CameraSha256.Value);
        }
        return Finish(hash);
    }

    private static ImmutableArray<Vector3> SourcePositions(
        ReferenceRenderShape shape) =>
        shape.SourceRestPositions.IsDefaultOrEmpty
            ? shape.Positions
            : shape.SourceRestPositions;

    private static ImmutableArray<Vector3> ToRenderPositions(
        ReferenceRenderShape shape,
        ImmutableArray<Vector3> sourcePositions) =>
        shape.SourceRestPositions.IsDefaultOrEmpty
            ? sourcePositions
            : sourcePositions
                .Select(shape.RenderPlacement.TransformPoint)
                .ToImmutableArray();

    private static Sha256Hash HashMatrix(
        Sha256Hash resource,
        Sha256Hash reviewedDesign,
        Sha256Hash render,
        ImmutableArray<ReferenceAnchorProjectionBaseline> baselines,
        ImmutableArray<RaceMenuMorphResponse> responses)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, resource.Value);
        Append(hash, reviewedDesign.Value);
        Append(hash, render.Value);
        foreach (ReferenceAnchorProjectionBaseline baseline in baselines)
        {
            AppendInt32(hash, (int)baseline.ViewRole);
            AppendInt32(hash, (int)baseline.Anchor);
            AppendDouble(hash, baseline.X);
            AppendDouble(hash, baseline.Y);
            AppendDouble(hash, baseline.Weight);
        }
        foreach (RaceMenuMorphResponse response in responses)
        {
            Append(hash, response.MorphSha256.Value);
            Append(hash, response.TopologySha256.Value);
            Append(hash, response.CameraSetSha256.Value);
            Append(hash, response.ResourceSnapshotSha256.Value);
            foreach (ReferenceAnchorDisplacement displacement in
                     response.Displacements)
            {
                AppendInt32(hash, (int)displacement.ViewRole);
                AppendInt32(hash, (int)displacement.Anchor);
                AppendDouble(hash, displacement.DeltaX);
                AppendDouble(hash, displacement.DeltaY);
            }
            foreach (ReferenceAnchorDisplacement displacement in
                     response.NegativeDisplacements)
            {
                AppendInt32(hash, (int)displacement.ViewRole);
                AppendInt32(hash, (int)displacement.Anchor);
                AppendDouble(hash, displacement.DeltaX);
                AppendDouble(hash, displacement.DeltaY);
            }
        }
        return Finish(hash);
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

    private static void AppendSingle(
        IncrementalHash hash,
        float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes, BitConverter.SingleToInt32Bits(value));
        hash.AppendData(bytes);
    }

    private static Sha256Hash Finish(IncrementalHash hash) =>
        new(Convert.ToHexString(hash.GetHashAndReset()));

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

    private static RaceMenuTriResponseMatrixBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new([], null, diagnostics.ToImmutable());
}
