using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Application;

namespace NpcManager.FaceGen;

/// <summary>Pure sequential evaluator for the pinned SSE face-morph plan.</summary>
public sealed class SseFaceMorphEvaluator : ISseFaceMorphEvaluator
{
    private const float DeltaSquaredEpsilon = 0.000001F;

    public SkyrimFaceMorphEvaluationResult Evaluate(SkyrimFaceMorphEvaluationRequest request)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        int count = request.BasePositions.Length;
        double[] x = new double[count];
        double[] y = new double[count];
        double[] z = new double[count];
        for (int index = 0; index < count; index++)
        {
            Vector3 value = request.BasePositions[index];
            x[index] = value.X;
            y[index] = value.Y;
            z[index] = value.Z;
        }

        foreach (SkyrimFaceMorphChannel channel in request.Plan.Channels)
        {
            foreach (SseTriHeadVertexDelta vertex in channel.Deltas)
            {
                float dx = vertex.Delta.X * channel.Weight;
                float dy = vertex.Delta.Y * channel.Weight;
                float dz = vertex.Delta.Z * channel.Weight;
                if (!float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(dz))
                {
                    diagnostics.Add(Error("sse-face-evaluate-overflow",
                        $"Channel '{channel.Name}' overflowed at vertex {vertex.VertexIndex}."));
                    return Refused(diagnostics);
                }

                float squaredLength = dx * dx + dy * dy + dz * dz;
                if (squaredLength < DeltaSquaredEpsilon)
                {
                    continue;
                }

                int index = vertex.VertexIndex;
                x[index] += dx;
                y[index] += dy;
                z[index] += dz;
                if (!double.IsFinite(x[index]) || !double.IsFinite(y[index]) ||
                    !double.IsFinite(z[index]))
                {
                    diagnostics.Add(Error("sse-face-evaluate-overflow",
                        $"Channel '{channel.Name}' produced a non-finite vertex {index}."));
                    return Refused(diagnostics);
                }
            }
        }

        ImmutableArray<Vector3>.Builder result = ImmutableArray.CreateBuilder<Vector3>(count);
        for (int index = 0; index < count; index++)
        {
            Vector3 value = new((float)x[index], (float)y[index], (float)z[index]);
            if (!IsFinite(value))
            {
                diagnostics.Add(Error("sse-face-evaluate-float32-overflow",
                    $"Final vertex {index} cannot be represented as finite float32 XYZ."));
                return Refused(diagnostics);
            }

            result.Add(value);
        }

        return new SkyrimFaceMorphEvaluationResult(true, result.MoveToImmutable(),
            diagnostics.ToImmutable());
    }

    private static void Validate(SkyrimFaceMorphEvaluationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Plan is null)
        {
            diagnostics.Add(Error("sse-face-evaluate-plan", "The morph plan is absent."));
            return;
        }

        if (request.BasePositions.IsDefault || request.Plan.Channels.IsDefault)
        {
            diagnostics.Add(Error("sse-face-evaluate-input",
                "Base positions and plan channels must be explicit arrays."));
            return;
        }

        if (request.Plan.VertexCount <= 0 ||
            request.BasePositions.Length != request.Plan.VertexCount)
        {
            diagnostics.Add(Error("sse-face-evaluate-topology",
                $"Base positions contain {request.BasePositions.Length} vertices; plan requires {request.Plan.VertexCount}."));
        }

        if (request.BasePositions.Any(value => !IsFinite(value)))
        {
            diagnostics.Add(Error("sse-face-evaluate-nonfinite",
                "Base positions contain a non-finite coordinate."));
        }

        foreach (SkyrimFaceMorphChannel channel in request.Plan.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name) || !float.IsFinite(channel.Weight) ||
                channel.Deltas.IsDefault || channel.Contributions.IsDefault)
            {
                diagnostics.Add(Error("sse-face-evaluate-channel",
                    "Every channel requires a name, finite weight, deltas, and contributions."));
                continue;
            }

            HashSet<int> indices = [];
            foreach (SseTriHeadVertexDelta vertex in channel.Deltas)
            {
                if (vertex.VertexIndex < 0 || vertex.VertexIndex >= request.Plan.VertexCount ||
                    !indices.Add(vertex.VertexIndex) || !IsFinite(vertex.Delta))
                {
                    diagnostics.Add(Error("sse-face-evaluate-delta",
                        $"Channel '{channel.Name}' has an invalid or duplicate vertex delta."));
                    break;
                }
            }
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimFaceMorphEvaluationResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, ImmutableArray<Vector3>.Empty, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
