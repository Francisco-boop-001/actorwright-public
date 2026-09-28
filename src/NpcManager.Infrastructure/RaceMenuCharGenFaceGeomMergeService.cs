using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Transfers only float4 XYZ lanes from explicitly authorized sources into a
/// qualified complete carrier. Fourth lanes, topology, shader/skin routes, and
/// every other carrier byte remain exact, except a necessary bounds-radius
/// expansion for a transplanted position set.
/// </summary>
public sealed partial class RaceMenuCharGenFaceGeomMergeService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IRaceMenuCharGenFaceGeomMergeService
{
    private const int MaximumNifBytes = 64 * 1024 * 1024;
    private const int MaximumXyzBytes = 16 * 1024 * 1024;
    private const string Operation = "racemenu-chargen-complete-carrier-xyz-merge";
    private const string ResultLabel = "RaceMenu CharGen XYZ merged into complete carrier";

    public async ValueTask<RaceMenuCharGenFaceGeomMergeAnalysisResult> AnalyzeAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        var work = await AnalyzeCoreAsync(request, outputMustBeAbsent: true, cancellationToken);
        return work.Result;
    }

    public async ValueTask<RaceMenuCharGenFaceGeomMergeResult> ApplyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var envelopeDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateProposalEnvelope(proposal, envelopeDiagnostics);
        if (HasErrors(envelopeDiagnostics)) return RefusedApply(envelopeDiagnostics);
        var work = await AnalyzeCoreAsync(RequestFrom(proposal), outputMustBeAbsent: true,
            cancellationToken);
        var diagnostics = work.Result.Diagnostics.ToBuilder();
        if (!work.Result.Accepted || work.Result.Proposal is null || work.OutputBytes is null)
            return RefusedApply(diagnostics);
        if (!ProposalMatches(proposal, work.Result.Proposal))
        {
            diagnostics.Add(Error("chargen-carrier-proposal-mismatch",
                "The supplied proposal does not match a fresh analysis of every hash-bound authority."));
            return RefusedApply(diagnostics);
        }

        var outputParent = Path.GetDirectoryName(proposal.OutputNif.Value);
        if (outputParent is null)
        {
            diagnostics.Add(Error("chargen-carrier-output-parent-invalid",
                "The output NIF has no parent directory."));
            return RefusedApply(diagnostics);
        }
        var temporary = Path.Combine(outputParent,
            $".{Path.GetFileName(proposal.OutputNif.Value)}.{Guid.NewGuid():N}.tmp");
        var promoted = false;
        try
        {
            AddReparseDiagnostics(new WorkspacePath(outputParent), "output-parent", diagnostics);
            if (HasErrors(diagnostics)) return RefusedApply(diagnostics);
            if (File.Exists(proposal.OutputNif.Value) || Directory.Exists(proposal.OutputNif.Value))
            {
                diagnostics.Add(Error("chargen-carrier-output-exists",
                    "CharGen carrier merge never overwrites an existing output path."));
                return RefusedApply(diagnostics);
            }

            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(work.OutputBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            AddReparseDiagnostics(new WorkspacePath(outputParent), "output-parent", diagnostics);
            if (HasErrors(diagnostics)) return RefusedApply(diagnostics);
            File.Move(temporary, proposal.OutputNif.Value, overwrite: false);
            promoted = true;

            var verification = await VerifyAsync(proposal, cancellationToken);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.Verified || verification.OutputSha256 is null ||
                verification.OutputByteLength is null || verification.OutputStructure is null)
            {
                DeleteFailedOutput(proposal.OutputNif, diagnostics);
                return new RaceMenuCharGenFaceGeomMergeResult(false, false, null,
                    verification, diagnostics.ToImmutable());
            }

            diagnostics.Add(new Diagnostic("chargen-carrier-materialized",
                DiagnosticSeverity.Info,
                "The complete carrier was materialized and independently reopened; game loadability and runtime appearance remain separate authorities."));
            var artifact = new RaceMenuCharGenFaceGeomMergeArtifact(
                ResultLabel,
                proposal,
                verification.OutputSha256.Value,
                verification.OutputByteLength.Value,
                verification.OutputStructure,
                proposal.ShapeDispositions.Length,
                proposal.ChangedPositionShapeCount,
                proposal.ExpandedRadiusCount,
                CreationKitAuthority: false,
                RuntimeAuthority: false);
            return new RaceMenuCharGenFaceGeomMergeResult(true, true, artifact,
                verification, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (promoted) DeleteFailedOutput(proposal.OutputNif, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (promoted) DeleteFailedOutput(proposal.OutputNif, diagnostics);
            diagnostics.Add(Error("chargen-carrier-write-failed", exception.Message));
            return RefusedApply(diagnostics);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async ValueTask<RaceMenuCharGenFaceGeomMergeVerificationResult> VerifyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateProposalEnvelope(proposal, diagnostics);
        ValidateVerificationOutput(proposal.OutputNif, diagnostics);
        if (HasErrors(diagnostics)) return RefusedVerification(proposal.OutputNif, diagnostics);

        var fresh = await AnalyzeCoreAsync(RequestFrom(proposal), outputMustBeAbsent: false,
            cancellationToken);
        diagnostics.AddRange(fresh.Result.Diagnostics);
        if (!fresh.Result.Accepted || fresh.Result.Proposal is null || fresh.OutputBytes is null ||
            !ProposalMatches(proposal, fresh.Result.Proposal))
        {
            diagnostics.Add(Error("chargen-carrier-verification-proposal-drift",
                "Fresh analysis of the hash-bound authorities no longer matches the supplied proposal."));
            return RefusedVerification(proposal.OutputNif, diagnostics);
        }

        var carrierBytes = await ReadBoundedFileAsync(proposal.CarrierNif, MaximumNifBytes,
            "carrier", diagnostics, cancellationToken);
        var outputBytes = await ReadBoundedFileAsync(proposal.OutputNif, MaximumNifBytes,
            "output", diagnostics, cancellationToken);
        if (carrierBytes is null || outputBytes is null)
            return RefusedVerification(proposal.OutputNif, diagnostics);
        var carrierHash = Hash(carrierBytes);
        var outputHash = Hash(outputBytes);
        if (carrierHash != proposal.CarrierSha256)
            diagnostics.Add(Error("chargen-carrier-verification-carrier-hash",
                "The independently reopened carrier no longer matches the proposal."));
        if (outputHash != proposal.ExpectedOutputSha256 ||
            outputBytes.LongLength != proposal.ExpectedOutputByteLength ||
            !outputBytes.AsSpan().SequenceEqual(fresh.OutputBytes))
            diagnostics.Add(Error("chargen-carrier-verification-output-hash",
                "The independently reopened output differs from the freshly reconstructed expected bytes."));
        if (HasErrors(diagnostics))
            return FailedVerification(proposal.OutputNif, proposal.CharGenSha256,
                carrierHash, outputHash, outputBytes.LongLength, null, diagnostics);

        try
        {
            var carrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
            var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
            var carrierStructure = SseFaceGeomCarrierCodec.BuildStructure(carrier);
            var outputStructure = SseFaceGeomCarrierCodec.BuildStructure(output);
            SseFaceGeomCarrierCodec.Qualify(
                carrier,
                carrierStructure,
                proposal.QualificationProfile,
                diagnostics);
            _ = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                carrier,
                proposal.QualificationProfile,
                diagnostics);
            SseFaceGeomCarrierCodec.Qualify(
                output,
                outputStructure,
                proposal.QualificationProfile,
                diagnostics);
            _ = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                output,
                proposal.QualificationProfile,
                diagnostics);
            if (!StructureMatches(carrierStructure, proposal.CarrierStructure) ||
                !StructureMatches(carrierStructure, outputStructure))
                diagnostics.Add(Error("chargen-carrier-verification-structure",
                    "The complete carrier structure changed during XYZ materialization."));

            var verifiedNames = VerifyOutputSurface(carrier, output,
                proposal.ShapeDispositions, diagnostics);
            if (HasErrors(diagnostics))
                return new RaceMenuCharGenFaceGeomMergeVerificationResult(false,
                    proposal.OutputNif, proposal.CharGenSha256, carrierHash, outputHash,
                    outputBytes.LongLength, outputStructure, verifiedNames, 0, 0,
                    diagnostics.ToImmutable());

            var laneCount = proposal.ShapeDispositions.Sum(item => item.VertexCount);
            diagnostics.Add(new Diagnostic("chargen-carrier-postwrite-verified",
                DiagnosticSeverity.Info,
                "Post-write hashes, carrier qualification, exact XYZ lanes, preserved fourth lanes, bounded radii, and every non-authorized byte are verified."));
            return new RaceMenuCharGenFaceGeomMergeVerificationResult(true,
                proposal.OutputNif, proposal.CharGenSha256, carrierHash, outputHash,
                outputBytes.LongLength, outputStructure, verifiedNames, laneCount,
                proposal.ExpandedRadiusCount, diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("chargen-carrier-verification-parse", exception.Message));
            return FailedVerification(proposal.OutputNif, proposal.CharGenSha256,
                carrierHash, outputHash, outputBytes.LongLength, null, diagnostics);
        }
    }

    private async ValueTask<AnalysisWork> AnalyzeCoreAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        bool outputMustBeAbsent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateAnalyzeRequest(request, outputMustBeAbsent, diagnostics);
        if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

        var charGenBytes = await ReadBoundedFileAsync(request.CharGenNif, MaximumNifBytes,
            "CharGen", diagnostics, cancellationToken);
        var carrierBytes = await ReadBoundedFileAsync(request.CarrierNif, MaximumNifBytes,
            "carrier", diagnostics, cancellationToken);
        if (charGenBytes is null || carrierBytes is null)
            return AnalysisWork.Refused(diagnostics);
        var charGenHash = Hash(charGenBytes);
        var carrierHash = Hash(carrierBytes);
        if (charGenHash != request.ExpectedCharGenSha256)
            diagnostics.Add(Error("chargen-carrier-chargen-hash",
                $"CharGen SHA256 {charGenHash} does not match {request.ExpectedCharGenSha256}."));
        if (carrierHash != request.ExpectedCarrierSha256)
            diagnostics.Add(Error("chargen-carrier-carrier-hash",
                $"Carrier SHA256 {carrierHash} does not match {request.ExpectedCarrierSha256}."));
        if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

        try
        {
            var charGen = SseFaceGeomCarrierCodec.Parse(charGenBytes);
            var carrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
            var charGenStructure = SseFaceGeomCarrierCodec.BuildStructure(charGen);
            var carrierStructure = SseFaceGeomCarrierCodec.BuildStructure(carrier);
            ValidateIncompleteCharGen(charGen, charGenStructure, diagnostics);
            SseFaceGeomCarrierCodec.Qualify(
                carrier,
                carrierStructure,
                request.QualificationProfile,
                diagnostics);
            _ = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                carrier,
                request.QualificationProfile,
                diagnostics);
            if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

            var routed = await BuildDispositionWorkAsync(request, charGen, carrier,
                diagnostics, cancellationToken);
            if (HasErrors(diagnostics) || routed.IsDefaultOrEmpty)
                return AnalysisWork.Refused(diagnostics);
            var changedCount = routed.Count(item => item.Disposition.PositionChanged);
            if (changedCount == 0)
            {
                diagnostics.Add(new Diagnostic(
                    "chargen-carrier-noop-preserved",
                    DiagnosticSeverity.Info,
                    "Every explicitly routed CharGen XYZ set already matches the qualified carrier; the exact carrier bytes remain the predicted output."));
            }

            var outputBytes = ApplyPositions(carrier, routed);
            var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
            var outputStructure = SseFaceGeomCarrierCodec.BuildStructure(output);
            SseFaceGeomCarrierCodec.Qualify(
                output,
                outputStructure,
                request.QualificationProfile,
                diagnostics);
            _ = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                output,
                request.QualificationProfile,
                diagnostics);
            if (!StructureMatches(carrierStructure, outputStructure))
                diagnostics.Add(Error("chargen-carrier-predicted-structure",
                    "The predicted XYZ merge changed the complete carrier structure."));
            VerifyOutputSurface(carrier, output,
                routed.Select(item => item.Disposition).ToImmutableArray(), diagnostics);
            if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

            var dispositions = routed.Select(item => item.Disposition).ToImmutableArray();
            var proposal = new RaceMenuCharGenFaceGeomMergeProposal(
                SchemaVersion: "1",
                Operation,
                request.Edition,
                request.CharGenNif,
                charGenHash,
                charGenBytes.LongLength,
                charGen.Blocks.Length,
                charGenStructure.DynamicShapeCount,
                request.CarrierNif,
                carrierHash,
                carrierBytes.LongLength,
                carrierStructure,
                request.OutputNif,
                Hash(outputBytes),
                outputBytes.LongLength,
                dispositions,
                changedCount,
                dispositions.Count(item => item.RadiusChanged),
                CreationKitAuthority: false,
                RuntimeAuthority: false)
            {
                QualificationProfile = request.QualificationProfile
            };
            diagnostics.Add(new Diagnostic("chargen-carrier-analysis-passed",
                DiagnosticSeverity.Info,
                $"All {dispositions.Length} complete-carrier shapes have explicit authority; {changedCount} XYZ sets change and {proposal.ExpandedRadiusCount} bounds expand."));
            return new AnalysisWork(
                new RaceMenuCharGenFaceGeomMergeAnalysisResult(true, proposal,
                    diagnostics.ToImmutable()),
                outputBytes);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("chargen-carrier-analysis-parse", exception.Message));
            return AnalysisWork.Refused(diagnostics);
        }
    }

    private sealed record AnalysisWork(
        RaceMenuCharGenFaceGeomMergeAnalysisResult Result,
        byte[]? OutputBytes)
    {
        internal static AnalysisWork Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
            new(new RaceMenuCharGenFaceGeomMergeAnalysisResult(false, null,
                diagnostics.ToImmutable()), null);
    }

    private sealed record DispositionWork(
        RaceMenuCharGenFaceGeomShapeDisposition Disposition,
        byte[] PositionBytes);
}
