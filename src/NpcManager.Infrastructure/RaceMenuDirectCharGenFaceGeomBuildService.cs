using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Builds a complete FaceGeom from a same-stem RaceMenu CharGen export without
/// pretending that the export is itself a qualified carrier. An incomplete
/// shape set is completed from the carrier; a complete compatible shape set is
/// also valid and transfers every dynamic XYZ route.
/// </summary>
public sealed class RaceMenuDirectCharGenFaceGeomBuildService(
    IRaceMenuCharGenFaceGeomMergeService mergeService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IRaceMenuDirectCharGenFaceGeomBuildService
{
    private const int MaximumNifBytes = 64 * 1024 * 1024;

    public async ValueTask<RaceMenuDirectCharGenFaceGeomBuildResult> BuildAsync(
        RaceMenuDirectCharGenFaceGeomBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        RaceMenuCharGenFaceGeomMergeArtifact? ownedArtifact = null;
        try
        {
            byte[]? charGenBytes = await ReadBoundFileAsync(
                request.CharGenNif, request.ExpectedCharGenSha256,
                "CharGen", diagnostics, cancellationToken).ConfigureAwait(false);
            byte[]? carrierBytes = await ReadBoundFileAsync(
                request.CompleteCarrierNif, request.ExpectedCompleteCarrierSha256,
                "complete carrier", diagnostics, cancellationToken).ConfigureAwait(false);
            if (charGenBytes is null || carrierBytes is null || HasErrors(diagnostics))
                return Refused(diagnostics);

            ImmutableArray<RaceMenuCharGenFaceGeomShapeAuthority> authorities =
                BuildAuthorities(charGenBytes, carrierBytes, diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);

            RaceMenuCharGenFaceGeomMergeAnalysisResult analysis =
                await mergeService.AnalyzeAsync(
                    new RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
                        GameEdition.SkyrimSpecialEdition,
                        request.CharGenNif,
                        request.ExpectedCharGenSha256,
                        request.CompleteCarrierNif,
                        request.ExpectedCompleteCarrierSha256,
                        request.OutputNif,
                        authorities)
                    {
                        QualificationProfile = request.QualificationProfile
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(analysis.Diagnostics);
            if (!analysis.Accepted || analysis.Proposal is null || HasErrors(diagnostics))
                return Refused(diagnostics);

            RaceMenuCharGenFaceGeomMergeResult applied = await mergeService.ApplyAsync(
                analysis.Proposal, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(applied.Diagnostics);
            if (applied.Written && applied.Artifact is not null)
                ownedArtifact = applied.Artifact;
            if (!applied.Written || !applied.Verified || applied.Artifact is null ||
                applied.Verification is not { Verified: true } || HasErrors(diagnostics))
            {
                if (ownedArtifact is not null) DeleteOwnedOutput(ownedArtifact, diagnostics);
                return Refused(diagnostics);
            }

            RaceMenuCharGenFaceGeomMergeVerificationResult independent =
                await mergeService.VerifyAsync(analysis.Proposal, cancellationToken)
                    .ConfigureAwait(false);
            diagnostics.AddRange(independent.Diagnostics);
            if (!independent.Verified || independent.OutputSha256 !=
                applied.Artifact.OutputSha256 || independent.OutputByteLength !=
                applied.Artifact.OutputByteLength ||
                !StructuresEqual(independent.OutputStructure, applied.Artifact.Structure) ||
                HasErrors(diagnostics))
            {
                diagnostics.Add(Error("direct-chargen-independent-verification",
                    "The independently reopened direct CharGen merge does not match the applied output."));
                DeleteOwnedOutput(applied.Artifact, diagnostics);
                return Refused(diagnostics);
            }

            diagnostics.Add(new Diagnostic(
                "direct-chargen-facegeom-complete",
                DiagnosticSeverity.Info,
                $"Transferred {analysis.Proposal.ChangedPositionShapeCount} exact CharGen XYZ sets into " +
                $"a {analysis.Proposal.ShapeDispositions.Length}-shape qualified carrier; runtime authority remains false."));
            return new RaceMenuDirectCharGenFaceGeomBuildResult(
                true, true, applied.Artifact, independent, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (ownedArtifact is not null) DeleteOwnedOutput(ownedArtifact, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or OverflowException or
                                           CryptographicException)
        {
            diagnostics.Add(Error("direct-chargen-facegeom-failed", exception.Message));
            if (ownedArtifact is not null) DeleteOwnedOutput(ownedArtifact, diagnostics);
            return Refused(diagnostics);
        }
    }

    private void ValidatePaths(
        RaceMenuDirectCharGenFaceGeomBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(request.QualificationProfile))
            diagnostics.Add(Error(
                "direct-chargen-profile-unsupported",
                "The carrier qualification profile is unsupported."));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.CharGenNif));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.CompleteCarrierNif));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputNif));
        foreach (WorkspacePath input in new[] { request.CharGenNif, request.CompleteCarrierNif })
        {
            if (!input.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(input.Value) || Directory.Exists(input.Value))
            {
                diagnostics.Add(Error("direct-chargen-input-invalid",
                    $"Input '{input.Value}' must be an existing ordinary .nif file."));
            }
            AddReparseDiagnostic(input.Value, "input", diagnostics);
        }
        if (!request.OutputNif.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(request.OutputNif.Value) || Directory.Exists(request.OutputNif.Value))
        {
            diagnostics.Add(Error("direct-chargen-output-invalid",
                "The direct CharGen output must be a new .nif path."));
        }
        string? parent = Path.GetDirectoryName(request.OutputNif.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("direct-chargen-output-parent",
                "The direct CharGen output parent must already exist."));
        }
        else
        {
            AddReparseDiagnostic(parent, "output parent", diagnostics);
        }
        if (string.Equals(request.CharGenNif.Value, request.CompleteCarrierNif.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.CharGenNif.Value, request.OutputNif.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.CompleteCarrierNif.Value, request.OutputNif.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("direct-chargen-path-alias",
                "CharGen, carrier, and output paths must be distinct."));
        }
    }

    private static ImmutableArray<RaceMenuCharGenFaceGeomShapeAuthority> BuildAuthorities(
        byte[] charGenBytes,
        byte[] carrierBytes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SseNifDocument charGen = SseFaceGeomCarrierCodec.Parse(charGenBytes);
        SseNifDocument carrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
        Dictionary<string, SseNifBlock> charGenShapes = DynamicShapes(
            charGen, "CharGen", diagnostics);
        Dictionary<string, SseNifBlock> carrierShapes = DynamicShapes(
            carrier, "carrier", diagnostics);
        if (HasErrors(diagnostics)) return [];
        if (charGenShapes.Count == 0 || charGenShapes.Count > carrierShapes.Count ||
            charGenShapes.Keys.Any(name => !carrierShapes.ContainsKey(name)))
        {
            diagnostics.Add(Error("direct-chargen-shape-subset",
                "CharGen dynamic shapes must be a non-empty subset of the qualified carrier shapes."));
            return [];
        }

        var authorities = ImmutableArray.CreateBuilder<RaceMenuCharGenFaceGeomShapeAuthority>(
            carrierShapes.Count);
        foreach (SseNifBlock carrierShape in carrierShapes.Values.OrderBy(item => item.Index))
        {
            string name = carrierShape.Name!;
            Sha256Hash carrierTopology = RaceMenuCharGenFaceGeomMergeService.TopologyHash(
                carrier, carrierShape);
            if (charGenShapes.TryGetValue(name, out SseNifBlock? charGenShape))
            {
                Sha256Hash charGenTopology = RaceMenuCharGenFaceGeomMergeService.TopologyHash(
                    charGen, charGenShape);
                SseNifDynamicGeometryLayout carrierLayout =
                    RaceMenuCharGenFaceGeomMergeService.RequireDynamicLayout(carrierShape);
                SseNifDynamicGeometryLayout charGenLayout =
                    RaceMenuCharGenFaceGeomMergeService.RequireDynamicLayout(charGenShape);
                if (!string.Equals(charGenShape.Type, carrierShape.Type, StringComparison.Ordinal) ||
                    charGenShape.Size != carrierShape.Size ||
                    charGenShape.GeometryPayload?.Length != carrierShape.GeometryPayload?.Length ||
                    charGenLayout.VertexCount != carrierLayout.VertexCount ||
                    charGenTopology != carrierTopology)
                {
                    diagnostics.Add(Error("direct-chargen-shape-incompatible",
                        $"CharGen shape '{name}' does not match the carrier type, size, vertex count, payload, and topology."));
                    continue;
                }
                authorities.Add(new RaceMenuCharGenFaceGeomCharGenXyzAuthority(
                    name, carrierTopology,
                    "Exact same-name, same-topology RaceMenu CharGen XYZ authority."));
            }
            else
            {
                SseNifDynamicGeometryLayout layout =
                    RaceMenuCharGenFaceGeomMergeService.RequireDynamicLayout(carrierShape);
                Sha256Hash positions = Hash(
                    RaceMenuCharGenFaceGeomMergeService.ExtractPositions(carrier.Data, layout));
                authorities.Add(new RaceMenuCharGenFaceGeomCarrierPreservedAuthority(
                    name, positions, carrierTopology,
                    "Shape is absent from the CharGen export; preserve the exact qualified carrier XYZ."));
            }
        }
        return HasErrors(diagnostics) ? [] : authorities.ToImmutable();
    }

    private static Dictionary<string, SseNifBlock> DynamicShapes(
        SseNifDocument document,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = new Dictionary<string, SseNifBlock>(StringComparer.Ordinal);
        foreach (SseNifBlock shape in document.Blocks.Where(item =>
                     string.Equals(item.Type, "BSDynamicTriShape", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(shape.Name) || !result.TryAdd(shape.Name, shape))
            {
                diagnostics.Add(Error("direct-chargen-shape-name",
                    $"Every {role} dynamic shape requires a unique non-empty name."));
            }
        }
        return result;
    }

    private static async ValueTask<byte[]?> ReadBoundFileAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (info.Length is <= 0 or > MaximumNifBytes)
        {
            diagnostics.Add(Error("direct-chargen-input-size",
                $"The {role} NIF must contain 1-{MaximumNifBytes} bytes."));
            return null;
        }
        byte[] bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            diagnostics.Add(Error("direct-chargen-input-length-drift",
                $"The {role} NIF changed length while it was read."));
            return null;
        }
        Sha256Hash actual = Hash(bytes);
        if (actual != expected)
        {
            diagnostics.Add(Error("direct-chargen-input-hash",
                $"The {role} NIF hash {actual} does not match {expected}."));
            return null;
        }
        return bytes;
    }

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("direct-chargen-reparse",
                        $"The {role} traverses reparse point '{current}'."));
                    return;
                }
            }
            catch (FileNotFoundException)
            {
                // A new output leaf is inspected through its existing parents.
            }
            catch (DirectoryNotFoundException)
            {
                // A new output leaf is inspected through its existing parents.
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) return;
            current = parent;
        }
    }

    private void DeleteOwnedOutput(
        RaceMenuCharGenFaceGeomMergeArtifact artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            WorkspacePath output = artifact.Proposal.OutputNif;
            if (!output.IsUnder(labRoot) || !File.Exists(output.Value))
            {
                diagnostics.Add(Error("direct-chargen-rollback-ownership",
                    "Refused to delete a failed direct CharGen output without exact path/hash ownership."));
                return;
            }
            int diagnosticCount = diagnostics.Count;
            AddReparseDiagnostic(output.Value, "rollback output", diagnostics);
            if (diagnostics.Count != diagnosticCount) return;
            Sha256Hash currentHash;
            using (var stream = new FileStream(output.Value, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                currentHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
            }
            if (currentHash != artifact.OutputSha256)
            {
                diagnostics.Add(Error("direct-chargen-rollback-ownership",
                    "Refused to delete a failed direct CharGen output whose hash changed."));
                return;
            }
            File.Delete(output.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("direct-chargen-rollback-failed", exception.Message));
        }
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool StructuresEqual(
        QualifiedFaceGeomCarrierStructure? left,
        QualifiedFaceGeomCarrierStructure right) =>
        left is not null &&
        left.BlockCount == right.BlockCount &&
        left.ReachableBlockCount == right.ReachableBlockCount &&
        left.RootCount == right.RootCount &&
        left.NiNodeCount == right.NiNodeCount &&
        left.FadeNodeCount == right.FadeNodeCount &&
        left.DynamicShapeCount == right.DynamicShapeCount &&
        left.NullChildReferenceCount == right.NullChildReferenceCount &&
        left.GraphSha256 == right.GraphSha256 &&
        left.ReachableShapeNames.SequenceEqual(right.ReachableShapeNames) &&
        left.ReachableCensus.SequenceEqual(right.ReachableCensus);

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuDirectCharGenFaceGeomBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, null, diagnostics.ToImmutable());
}
