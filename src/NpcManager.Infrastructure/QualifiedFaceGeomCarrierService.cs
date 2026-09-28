using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Materializes the Gate 1 qualified Skyrim SE FaceGeom carrier by changing a
/// bounded set of head texture routes. It never reserializes the NIF graph: the
/// admitted write surface is one BSShaderTextureSet block plus its block-size
/// table entry. A legacy request still changes only FaceTint slot 6.
/// </summary>
public sealed partial class QualifiedFaceGeomCarrierService : IQualifiedFaceGeomCarrierService
{
    private const int MaxNifBytes = 64 * 1024 * 1024;
    private const int MaxTextureSetPreimageBytes = 256 * 1024;
    private const string Operation = "qualified-facegeom-carrier-materialization";
    private const string ResultLabel = "qualified carrier materialization";

    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;

    public QualifiedFaceGeomCarrierService(IWorkspacePolicy policy, WorkspacePath labRoot)
    {
        _policy = policy;
        _labRoot = labRoot;
    }

    public async ValueTask<QualifiedFaceGeomCarrierAnalysisResult> AnalyzeAsync(
        QualifiedFaceGeomCarrierAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        var work = await AnalyzeCoreAsync(request, cancellationToken);
        return work.Result;
    }

    public async ValueTask<QualifiedFaceGeomCarrierMaterializationResult> ApplyAsync(
        QualifiedFaceGeomCarrierProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new QualifiedFaceGeomCarrierAnalyzeRequest(
            proposal.SourceNif,
            proposal.SourceSha256,
            proposal.OutputNif,
            proposal.TargetFaceTintPath)
        {
            TargetHeadTextures = proposal.TargetHeadTextures,
            QualificationProfile = proposal.QualificationProfile
        };
        var work = await AnalyzeCoreAsync(request, cancellationToken);
        var diagnostics = work.Result.Diagnostics.ToBuilder();
        if (!work.Result.Qualified || work.Result.Proposal is null || work.OutputBytes is null ||
            work.SourceTextureSetPreimage is null)
            return RefusedMaterialization(diagnostics);

        if (!ProposalMatches(proposal, work.Result.Proposal))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-proposal-mismatch", DiagnosticSeverity.Error,
                "The supplied proposal does not match a fresh analysis of the hash-bound source."));
            return RefusedMaterialization(diagnostics);
        }

        var outputParent = Path.GetDirectoryName(proposal.OutputNif.Value);
        if (outputParent is null)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-output-parent-invalid", DiagnosticSeverity.Error,
                "The output NIF has no parent directory."));
            return RefusedMaterialization(diagnostics);
        }

        var temporary = Path.Combine(outputParent,
            $".{Path.GetFileName(proposal.OutputNif.Value)}.{Guid.NewGuid():N}.tmp");
        var promoted = false;
        try
        {
            AddReparseDiagnostic(diagnostics, _labRoot, new WorkspacePath(outputParent), "output-parent");
            if (HasErrors(diagnostics)) return RefusedMaterialization(diagnostics);
            if (File.Exists(proposal.OutputNif.Value) || Directory.Exists(proposal.OutputNif.Value))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-output-exists", DiagnosticSeverity.Error,
                    "Qualified carrier materialization never overwrites an existing path."));
                return RefusedMaterialization(diagnostics);
            }

            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(work.OutputBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            AddReparseDiagnostic(diagnostics, _labRoot, new WorkspacePath(outputParent), "output-parent");
            if (HasErrors(diagnostics)) return RefusedMaterialization(diagnostics);
            File.Move(temporary, proposal.OutputNif.Value, overwrite: false);
            promoted = true;

            var verification = await VerifyAsync(proposal, cancellationToken);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.Verified || verification.OutputSha256 is null || verification.Structure is null)
            {
                DeleteFailedOutput(proposal.OutputNif.Value, diagnostics);
                return new QualifiedFaceGeomCarrierMaterializationResult(false, false, null,
                    verification, diagnostics.ToImmutable());
            }

            diagnostics.Add(new Diagnostic("qualified-carrier-materialized", DiagnosticSeverity.Info,
                "The output is a qualified carrier materialization; CK compilation, game loadability, and runtime appearance remain separate authorities."));
            var preimage = work.SourceTextureSetPreimage;
            var artifact = new QualifiedFaceGeomCarrierMaterializationArtifact(
                SchemaVersion: "1",
                Operation,
                ResultLabel,
                proposal.SourceSha256,
                proposal.SourceByteLength,
                new QualifiedFaceGeomCarrierTextureSetPreimage(
                    Encoding: "base64",
                    proposal.TextureSetBlockIndex,
                    BlockType: "BSShaderTextureSet",
                    Convert.ToBase64String(preimage),
                    preimage.Length,
                    Hash(preimage)),
                verification.OutputSha256.Value,
                new FileInfo(proposal.OutputNif.Value).Length,
                proposal.TextureSlotIndex,
                proposal.OriginalFaceTintPath,
                proposal.TargetFaceTintPath,
                proposal.TargetHeadTextures,
                BindTargetTextures(proposal.TargetFaceTintPath, proposal.TargetHeadTextures),
                proposal.Structure,
                verification.ChangedBlocks,
                CreationKitAuthority: false,
                RuntimeAuthority: false)
            {
                QualificationProfile = proposal.QualificationProfile
            };
            return new QualifiedFaceGeomCarrierMaterializationResult(true, true, artifact,
                verification, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (promoted) DeleteFailedOutput(proposal.OutputNif.Value, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (promoted) DeleteFailedOutput(proposal.OutputNif.Value, diagnostics);
            diagnostics.Add(new Diagnostic("qualified-carrier-write-failed", DiagnosticSeverity.Error,
                exception.Message));
            return RefusedMaterialization(diagnostics);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async ValueTask<QualifiedFaceGeomCarrierVerificationResult> VerifyAsync(
        QualifiedFaceGeomCarrierProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateProposalEnvelope(proposal, diagnostics);
        ValidateVerificationPaths(proposal, diagnostics);
        if (HasErrors(diagnostics)) return RefusedVerification(proposal.OutputNif, diagnostics);

        var sourceBytes = await ReadNifAsync(proposal.SourceNif, diagnostics, "source", cancellationToken);
        var outputBytes = await ReadNifAsync(proposal.OutputNif, diagnostics, "output", cancellationToken);
        if (sourceBytes is null || outputBytes is null)
            return RefusedVerification(proposal.OutputNif, diagnostics);

        var sourceHash = Hash(sourceBytes);
        var outputHash = Hash(outputBytes);
        if (sourceHash != proposal.SourceSha256)
            diagnostics.Add(new Diagnostic("qualified-carrier-source-hash-mismatch", DiagnosticSeverity.Error,
                "The source NIF no longer matches the proposal's exact SHA-256 binding."));
        if (sourceBytes.LongLength != proposal.SourceByteLength)
            diagnostics.Add(new Diagnostic("qualified-carrier-source-length-mismatch", DiagnosticSeverity.Error,
                "The source NIF byte length no longer matches the proposal."));
        if (outputHash != proposal.ExpectedOutputSha256)
            diagnostics.Add(new Diagnostic("qualified-carrier-output-hash-mismatch", DiagnosticSeverity.Error,
                "The written NIF does not match the proposal's predicted SHA-256."));
        if (outputBytes.LongLength != proposal.ExpectedOutputByteLength)
            diagnostics.Add(new Diagnostic("qualified-carrier-output-length-mismatch", DiagnosticSeverity.Error,
                "The written NIF byte length does not match the proposal."));
        if (HasErrors(diagnostics))
            return new QualifiedFaceGeomCarrierVerificationResult(false, proposal.OutputNif, outputHash,
                null, [], diagnostics.ToImmutable());

        try
        {
            var source = SseFaceGeomCarrierCodec.Parse(sourceBytes);
            var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
            var sourceStructure = SseFaceGeomCarrierCodec.BuildStructure(source);
            var outputStructure = SseFaceGeomCarrierCodec.BuildStructure(output);
            SseFaceGeomCarrierCodec.Qualify(
                source, sourceStructure, proposal.QualificationProfile, diagnostics);
            SseFaceGeomCarrierCodec.Qualify(
                output, outputStructure, proposal.QualificationProfile, diagnostics);
            if (!StructureMatches(sourceStructure, proposal.Structure))
                diagnostics.Add(new Diagnostic("qualified-carrier-source-structure-drift", DiagnosticSeverity.Error,
                    "The source graph no longer matches the analyzed carrier structure."));
            if (!StructureMatches(sourceStructure, outputStructure))
                diagnostics.Add(new Diagnostic("qualified-carrier-output-graph-drift", DiagnosticSeverity.Error,
                    "The output graph differs from the qualified source graph."));

            var sourceTarget = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                source,
                proposal.QualificationProfile,
                diagnostics);
            if (sourceTarget is null || sourceTarget.BlockIndex != proposal.TextureSetBlockIndex ||
                sourceTarget.SlotIndex != proposal.TextureSlotIndex ||
                !string.Equals(sourceTarget.OriginalPath, proposal.OriginalFaceTintPath,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-source-target-drift", DiagnosticSeverity.Error,
                    "The analyzed FaceTint texture-set target no longer matches the proposal."));
                return new QualifiedFaceGeomCarrierVerificationResult(false, proposal.OutputNif, outputHash,
                    outputStructure, [], diagnostics.ToImmutable());
            }

            var changedBlocks = SseFaceGeomCarrierCodec.VerifyRewrite(
                source,
                output,
                sourceTarget,
                proposal.TargetFaceTintPath,
                proposal.TargetHeadTextures,
                proposal.QualificationProfile,
                diagnostics);
            if (HasErrors(diagnostics))
                return new QualifiedFaceGeomCarrierVerificationResult(false, proposal.OutputNif, outputHash,
                    outputStructure, changedBlocks, diagnostics.ToImmutable());

            diagnostics.Add(new Diagnostic("qualified-carrier-postwrite-verified", DiagnosticSeverity.Info,
                "Post-write hash, NIF envelope, graph, per-block change surface, and exact authorized texture routes are verified."));
            return new QualifiedFaceGeomCarrierVerificationResult(true, proposal.OutputNif, outputHash,
                outputStructure, changedBlocks, diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-verification-parse-failed", DiagnosticSeverity.Error,
                exception.Message));
            return new QualifiedFaceGeomCarrierVerificationResult(false, proposal.OutputNif, outputHash,
                null, [], diagnostics.ToImmutable());
        }
    }

    private async ValueTask<AnalysisWork> AnalyzeCoreAsync(
        QualifiedFaceGeomCarrierAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateAnalyzePaths(request, diagnostics);
        if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

        var sourceBytes = await ReadNifAsync(request.SourceNif, diagnostics, "source", cancellationToken);
        if (sourceBytes is null) return AnalysisWork.Refused(diagnostics);
        var sourceHash = Hash(sourceBytes);
        if (sourceHash != request.ExpectedSourceSha256)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-source-hash-mismatch", DiagnosticSeverity.Error,
                $"Source SHA-256 {sourceHash} does not match the required binding {request.ExpectedSourceSha256}."));
            return AnalysisWork.Refused(diagnostics);
        }

        try
        {
            var source = SseFaceGeomCarrierCodec.Parse(sourceBytes);
            var structure = SseFaceGeomCarrierCodec.BuildStructure(source);
            SseFaceGeomCarrierCodec.Qualify(
                source, structure, request.QualificationProfile, diagnostics);
            var target = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                source,
                request.QualificationProfile,
                diagnostics);
            ValidateTargetBinding(request, target, diagnostics);
            ValidatePrivateHeadTextureTarget(
                source,
                target,
                request.TargetHeadTextures,
                request.QualificationProfile,
                diagnostics);
            if (HasErrors(diagnostics) || target is null) return AnalysisWork.Refused(diagnostics);

            var sourceTextureSetPreimage = SseFaceGeomCarrierCodec.ExtractTextureSetPreimage(
                source, target.BlockIndex);
            if (sourceTextureSetPreimage.Length > MaxTextureSetPreimageBytes)
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-preimage-size-limit",
                    DiagnosticSeverity.Error,
                    $"The source BSShaderTextureSet preimage exceeds {MaxTextureSetPreimageBytes} bytes."));
                return AnalysisWork.Refused(diagnostics);
            }

            var outputBytes = request.TargetHeadTextures is null
                ? SseFaceGeomCarrierCodec.RewriteFaceTintPath(
                    source,
                    target,
                    request.TargetFaceTintPath)
                : SseFaceGeomCarrierCodec.RewriteHeadTexturePaths(
                    source,
                    target,
                    request.TargetFaceTintPath,
                    request.TargetHeadTextures,
                    request.QualificationProfile);
            var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
            var outputStructure = SseFaceGeomCarrierCodec.BuildStructure(output);
            SseFaceGeomCarrierCodec.Qualify(
                output, outputStructure, request.QualificationProfile, diagnostics);
            if (!StructureMatches(structure, outputStructure))
                diagnostics.Add(new Diagnostic("qualified-carrier-predicted-graph-drift", DiagnosticSeverity.Error,
                    "The proposed in-memory rewrite changed the qualified carrier graph."));
            var changedBlocks = SseFaceGeomCarrierCodec.VerifyRewrite(
                source,
                output,
                target,
                request.TargetFaceTintPath,
                request.TargetHeadTextures,
                request.QualificationProfile,
                diagnostics);
            if (!changedBlocks.SequenceEqual([target.BlockIndex]))
                diagnostics.Add(new Diagnostic("qualified-carrier-predicted-change-surface", DiagnosticSeverity.Error,
                    "The proposed rewrite does not isolate exactly one BSShaderTextureSet block."));
            if (HasErrors(diagnostics)) return AnalysisWork.Refused(diagnostics);

            var proposal = new QualifiedFaceGeomCarrierProposal(
                SchemaVersion: "1",
                Operation,
                request.SourceNif,
                sourceHash,
                sourceBytes.LongLength,
                request.OutputNif,
                Hash(outputBytes),
                outputBytes.LongLength,
                target.BlockIndex,
                target.SlotIndex,
                target.OriginalPath,
                request.TargetFaceTintPath,
                structure,
                CreationKitAuthority: false,
                RuntimeAuthority: false)
            {
                TargetHeadTextures = request.TargetHeadTextures,
                TargetHeadTexturesBindingSha256 = BindHeadTextures(request.TargetHeadTextures),
                QualificationProfile = request.QualificationProfile
            };
            diagnostics.Add(new Diagnostic("qualified-carrier-analysis-passed", DiagnosticSeverity.Info,
                request.TargetHeadTextures is null
                    ? "The source is the complete qualified carrier and the proposal is limited to one length-aware FaceTint route rewrite."
                    : "The source is the complete qualified carrier and the proposal is limited to one length-aware head texture-set rewrite."));
            return new AnalysisWork(
                new QualifiedFaceGeomCarrierAnalysisResult(true, proposal, diagnostics.ToImmutable()),
                outputBytes,
                sourceTextureSetPreimage);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-analysis-parse-failed", DiagnosticSeverity.Error,
                exception.Message));
            return AnalysisWork.Refused(diagnostics);
        }
    }
}
