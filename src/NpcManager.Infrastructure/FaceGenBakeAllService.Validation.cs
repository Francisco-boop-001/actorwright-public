using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class FaceGenBakeAllService
{
    private ImmutableArray<Diagnostic> ValidatePreflight(
        FaceGenBakeAllRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputDataRoot));
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("facegen-bake-all-edition",
                "The real loose FaceGen batch currently supports Skyrim SE/AE only."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("facegen-bake-all-data-root",
                "The copied Data root must already exist."));
        if (!Directory.Exists(request.OutputDataRoot.Value))
            diagnostics.Add(Error("facegen-bake-all-output-root",
                "The empty K-local output Data root must already exist."));
        if (request.DataRoot == request.OutputDataRoot ||
            request.DataRoot.IsUnder(request.OutputDataRoot) ||
            request.OutputDataRoot.IsUnder(request.DataRoot))
            diagnostics.Add(Error("facegen-bake-all-root-overlap",
                "Input and output Data roots may not overlap."));
        if (request.PluginOrder.IsDefaultOrEmpty)
            diagnostics.Add(Error("facegen-bake-all-plugin-order",
                "The real batch requires an explicit copied-Data plugin order."));
        AddReparseDiagnostic(request.OutputDataRoot.Value, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static bool ValidateOutputUniverse(
        FaceGenBakeAllRequest request,
        ImmutableArray<FaceGenBakeTarget> targets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (FaceGenBakeTarget target in targets)
        {
            (string nif, string dds) = ExpectedPaths(request.OutputDataRoot,
                target);
            if (File.Exists(nif) != File.Exists(dds))
                diagnostics.Add(Error("facegen-bake-all-half-pair",
                    $"A pre-existing canonical half-pair blocks {TargetLabel(target)}."));
        }
        return !HasErrors(diagnostics);
    }

    private static async ValueTask<FaceGenNpcBakeResult> ValidateOutcomeAsync(
        WorkspacePath outputRoot,
        FaceGenBakeTarget expectedTarget,
        FaceGenNpcBakeResult result)
    {
        var diagnostics = result.Diagnostics.IsDefault
            ? ImmutableArray.CreateBuilder<Diagnostic>()
            : result.Diagnostics.ToBuilder();
        if (!TargetMatches(result.Target, expectedTarget))
            diagnostics.Add(Error("facegen-bake-all-target-drift",
                "The per-NPC baker returned an outcome for a different target."));
        (string expectedNif, string expectedDds) = ExpectedPaths(outputRoot,
            expectedTarget);
        bool nifExists = File.Exists(expectedNif);
        bool ddsExists = File.Exists(expectedDds);

        if (result.Status == FaceGenNpcBakeStatus.Skipped)
        {
            if (result.Artifact is not null)
                diagnostics.Add(Error("facegen-bake-all-unexpected-artifact",
                    "A skipped NPC returned a baked artifact."));
            if (nifExists != ddsExists)
                diagnostics.Add(Error("facegen-bake-all-half-pair",
                    "A skipped NPC retained only one canonical FaceGen sidecar."));
            return result with
            {
                Status = HasErrors(diagnostics)
                    ? FaceGenNpcBakeStatus.Failed
                    : FaceGenNpcBakeStatus.Skipped,
                Target = expectedTarget,
                Artifact = null,
                Diagnostics = diagnostics.ToImmutable()
            };
        }

        if (result.Status == FaceGenNpcBakeStatus.Failed)
        {
            if (result.Artifact is not null)
                diagnostics.Add(Error("facegen-bake-all-unexpected-artifact",
                    "A failed NPC returned a baked artifact."));
            if (nifExists || ddsExists)
                diagnostics.Add(Error("facegen-bake-all-half-pair",
                    "A failed NPC retained a canonical NIF or DDS."));
            return result with
            {
                Status = FaceGenNpcBakeStatus.Failed,
                Target = expectedTarget,
                Artifact = null,
                Diagnostics = diagnostics.ToImmutable()
            };
        }

        FaceGenNpcBakeArtifact? artifact = result.Artifact;
        if (artifact is null || !TargetMatches(artifact.Target, expectedTarget) ||
            artifact.RuntimeAuthority ||
            !string.Equals(artifact.FaceGeomNif.Value, expectedNif,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.FaceTintDds.Value, expectedDds,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("facegen-bake-all-artifact-envelope",
                "A baked NPC did not return its exact static-only canonical pair envelope."));
        if (!nifExists || !ddsExists)
            diagnostics.Add(Error("facegen-bake-all-pair-missing",
                "A baked NPC did not retain both canonical FaceGeom and FaceTint outputs."));

        if (!HasErrors(diagnostics) && artifact is not null)
        {
            await VerifyFileAsync(artifact.FaceGeomNif, artifact.FaceGeomSha256,
                artifact.FaceGeomByteLength, "nif", diagnostics)
                .ConfigureAwait(false);
            await VerifyFileAsync(artifact.FaceTintDds, artifact.FaceTintSha256,
                artifact.FaceTintByteLength, "dds", diagnostics)
                .ConfigureAwait(false);
        }
        return result with
        {
            Status = HasErrors(diagnostics)
                ? FaceGenNpcBakeStatus.Failed
                : FaceGenNpcBakeStatus.Baked,
            Target = expectedTarget,
            Artifact = HasErrors(diagnostics) ? null : artifact,
            Diagnostics = diagnostics.ToImmutable()
        };
    }

    private static bool TargetMatches(
        FaceGenBakeTarget left,
        FaceGenBakeTarget right) =>
        left.FormId == right.FormId &&
        left.OriginatingPlugin == right.OriginatingPlugin &&
        left.WinningPlugin == right.WinningPlugin &&
        left.OverrideChain.SequenceEqual(right.OverrideChain) &&
        left.EditorId == right.EditorId &&
        left.Name == right.Name &&
        left.Sex == right.Sex &&
        left.Race == right.Race &&
        left.HeadParts.SequenceEqual(right.HeadParts) &&
        left.Weight.Equals(right.Weight);

    private static async ValueTask VerifyFileAsync(
        WorkspacePath path,
        Sha256Hash expectedHash,
        int expectedLength,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length != expectedLength ||
                expectedLength <= 0 ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error($"facegen-bake-all-{role}-file",
                    $"The reopened {role.ToUpperInvariant()} has a mismatched length or file kind."));
                return;
            }
            await using var stream = new FileStream(path.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, CancellationToken.None)
                    .ConfigureAwait(false)));
            if (actual != expectedHash)
                diagnostics.Add(Error($"facegen-bake-all-{role}-hash",
                    $"The reopened {role.ToUpperInvariant()} hash differs from the per-NPC artifact."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(Error($"facegen-bake-all-{role}-read",
                exception.Message));
        }
    }

    private static (string Nif, string Dds) ExpectedPaths(
        WorkspacePath outputRoot,
        FaceGenBakeTarget target)
    {
        string id = target.FormId.Value.ToString("X8",
            System.Globalization.CultureInfo.InvariantCulture);
        return (
            Path.Combine(outputRoot.Value, "meshes", "actors", "character",
                "FaceGenData", "FaceGeom", target.OriginatingPlugin.Value,
                id + ".nif"),
            Path.Combine(outputRoot.Value, "textures", "actors", "character",
                "FaceGenData", "FaceTint", target.OriginatingPlugin.Value,
                id + ".dds"));
    }

    private static void AddReparseDiagnostic(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current)
                        .HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("facegen-bake-all-output-reparse",
                        "The output Data root may not traverse a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                diagnostics.Add(Error("facegen-bake-all-output-stat",
                    exception.Message));
                return;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return;
            current = parent ?? string.Empty;
        }
    }

    private static FaceGenBakeAllResult Fatal(
        int discovered,
        ImmutableArray<FaceGenNpcBakeResult>.Builder outcomes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IProgress<FaceGenBakeAllProgress>? progress,
        ref int sequence)
    {
        Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Fatal,
            outcomes.Count, discovered, null, "FaceGen batch preflight failed.");
        return Build(FaceGenBakeAllStatus.Fatal, discovered, outcomes,
            diagnostics);
    }

    private static FaceGenBakeAllResult Cancelled(
        int discovered,
        ImmutableArray<FaceGenNpcBakeResult>.Builder outcomes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IProgress<FaceGenBakeAllProgress>? progress,
        ref int sequence)
    {
        Report(progress, ref sequence, FaceGenBakeAllProgressPhase.Cancelled,
            outcomes.Count, discovered, null,
            "Cancelled between NPCs; completed outputs were retained.");
        return Build(FaceGenBakeAllStatus.Cancelled, discovered, outcomes,
            diagnostics);
    }

    private static void Report(
        IProgress<FaceGenBakeAllProgress>? progress,
        ref int sequence,
        FaceGenBakeAllProgressPhase phase,
        int completed,
        int total,
        FaceGenBakeTarget? target,
        string message) =>
        progress?.Report(new FaceGenBakeAllProgress(++sequence, phase,
            completed, total, target, message));
}
