using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Two-pass K-only persistence for product-owned carrier bytes. Analyze predicts
/// the exact NIF, Apply rebuilds and matches that prediction before an atomic
/// no-overwrite move, and Verify reopens both bytes and NIF structure.
/// </summary>
public sealed class SseFaceGeomCarrierMaterializationService(
    ISseFaceGeomCarrierAssembler assembler,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISseFaceGeomCarrierMaterializationService
{
    private const string Operation = "sse-facegeom-carrier-materialization";
    private const int MaximumOutputBytes = 600 * 1024 * 1024;

    public ValueTask<SseFaceGeomCarrierMaterializationAnalysisResult> AnalyzeAsync(
        SseFaceGeomCarrierMaterializationAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateOutput(request.OutputNif, requireNew: true)
            .ToBuilder();
        if (HasErrors(diagnostics)) return ValueTask.FromResult(Refused(diagnostics));

        SseFaceGeomCarrierAssemblyResult assembled =
            assembler.Assemble(request.Assembly);
        diagnostics.AddRange(assembled.Diagnostics);
        if (!assembled.Assembled || !assembled.Verified ||
            assembled.Artifact is null || HasErrors(diagnostics))
            return ValueTask.FromResult(Refused(diagnostics));
        ValidateManagerCarrierArtifact(assembled.Artifact, diagnostics);
        if (HasErrors(diagnostics))
            return ValueTask.FromResult(Refused(diagnostics));
        if (assembled.Artifact.ByteLength > MaximumOutputBytes)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-output-budget",
                $"Predicted carrier exceeds the {MaximumOutputBytes}-byte materialization budget."));
            return ValueTask.FromResult(Refused(diagnostics));
        }

        var proposal = new SseFaceGeomCarrierMaterializationProposal(
            Operation, request.Assembly, request.OutputNif, assembled.Artifact);
        diagnostics.Add(new Diagnostic("sse-facegeom-carrier-proposed",
            DiagnosticSeverity.Info,
            $"Proposed exact {assembled.Artifact.ByteLength}-byte carrier {assembled.Artifact.Sha256} without writing."));
        return ValueTask.FromResult(
            new SseFaceGeomCarrierMaterializationAnalysisResult(true, proposal,
                diagnostics.ToImmutable()));
    }

    public async ValueTask<SseFaceGeomCarrierMaterializationResult> ApplyAsync(
        SseFaceGeomCarrierMaterializationProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateOutput(proposal.OutputNif, requireNew: true)
            .ToBuilder();
        if (!string.Equals(proposal.Operation, Operation, StringComparison.Ordinal))
            diagnostics.Add(Error("sse-facegeom-carrier-operation",
                "Carrier proposal operation is not recognized."));
        if (HasErrors(diagnostics)) return RefusedApply(diagnostics);

        SseFaceGeomCarrierAssemblyResult rebuilt =
            assembler.Assemble(proposal.Assembly);
        diagnostics.AddRange(rebuilt.Diagnostics);
        if (!rebuilt.Assembled || !rebuilt.Verified || rebuilt.Artifact is null ||
            !ArtifactMatches(rebuilt.Artifact, proposal.PredictedArtifact))
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error("sse-facegeom-carrier-proposal-drift",
                    "Rebuilt carrier bytes or evidence do not match the analyzed proposal."));
            return RefusedApply(diagnostics);
        }
        ValidateManagerCarrierArtifact(rebuilt.Artifact, diagnostics);
        if (HasErrors(diagnostics)) return RefusedApply(diagnostics);

        string parent = Path.GetDirectoryName(proposal.OutputNif.Value)!;
        string temporary = Path.Combine(parent,
            ".npcm-facegeom-carrier-" + Guid.NewGuid().ToString("N") + ".nif");
        bool promoted = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(rebuilt.Artifact.Bytes.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, proposal.OutputNif.Value, overwrite: false);
            promoted = true;

            SseFaceGeomCarrierMaterializationVerificationResult verification =
                await VerifyAsync(proposal, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.Verified || verification.OutputSha256 !=
                proposal.PredictedArtifact.Sha256 ||
                verification.OutputByteLength !=
                proposal.PredictedArtifact.ByteLength)
            {
                TryDeleteOwned(proposal.OutputNif, proposal.PredictedArtifact);
                promoted = false;
                return new SseFaceGeomCarrierMaterializationResult(false, false,
                    null, verification, diagnostics.ToImmutable());
            }

            var artifact = new SseFaceGeomCarrierMaterializationArtifact(
                proposal,
                proposal.OutputNif,
                verification.OutputSha256.Value,
                verification.OutputByteLength.Value,
                proposal.PredictedArtifact.BlockCount,
                proposal.PredictedArtifact.Shapes.Length,
                RuntimeAuthority: false);
            diagnostics.Add(new Diagnostic("sse-facegeom-carrier-materialized",
                DiagnosticSeverity.Info,
                "The exact proposed carrier was atomically written and reopened; runtime authority remains false."));
            return new SseFaceGeomCarrierMaterializationResult(true, true,
                artifact, verification, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (promoted)
                TryDeleteOwned(proposal.OutputNif, proposal.PredictedArtifact);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            if (promoted)
                TryDeleteOwned(proposal.OutputNif, proposal.PredictedArtifact);
            diagnostics.Add(Error("sse-facegeom-carrier-write-failed",
                exception.Message));
            return RefusedApply(diagnostics);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async ValueTask<SseFaceGeomCarrierMaterializationVerificationResult>
        VerifyAsync(
            SseFaceGeomCarrierMaterializationProposal proposal,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateOutput(proposal.OutputNif, requireNew: false)
            .ToBuilder();
        if (!string.Equals(proposal.Operation, Operation, StringComparison.Ordinal))
            diagnostics.Add(Error("sse-facegeom-carrier-operation",
                "Carrier proposal operation is not recognized."));
        SseFaceGeomCarrierAssemblyResult rebuilt =
            assembler.Assemble(proposal.Assembly);
        diagnostics.AddRange(rebuilt.Diagnostics);
        if (!rebuilt.Assembled || !rebuilt.Verified || rebuilt.Artifact is null ||
            !ArtifactMatches(rebuilt.Artifact, proposal.PredictedArtifact))
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error("sse-facegeom-carrier-proposal-drift",
                    "Rebuilt carrier bytes or evidence do not match the analyzed proposal."));
        }
        if (rebuilt.Artifact is not null)
            ValidateManagerCarrierArtifact(rebuilt.Artifact, diagnostics);
        if (!File.Exists(proposal.OutputNif.Value))
            diagnostics.Add(Error("sse-facegeom-carrier-output-missing",
                "The proposed carrier output does not exist."));
        if (HasErrors(diagnostics))
            return FailedVerification(proposal.OutputNif, null, null, diagnostics);

        try
        {
            var info = new FileInfo(proposal.OutputNif.Value);
            if (info.Length != proposal.PredictedArtifact.ByteLength ||
                info.Length is <= 0 or > MaximumOutputBytes)
            {
                diagnostics.Add(Error("sse-facegeom-carrier-output-length",
                    "The reopened carrier length does not match the proposal."));
                int? observedLength = info.Length <= int.MaxValue
                    ? checked((int)info.Length)
                    : null;
                return FailedVerification(proposal.OutputNif, null,
                    observedLength, diagnostics);
            }
            byte[] bytes = await File.ReadAllBytesAsync(proposal.OutputNif.Value,
                cancellationToken).ConfigureAwait(false);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash != proposal.PredictedArtifact.Sha256 ||
                !bytes.AsSpan().SequenceEqual(
                    proposal.PredictedArtifact.Bytes.AsSpan()))
                diagnostics.Add(Error("sse-facegeom-carrier-output-bytes",
                    "The reopened carrier bytes do not match the exact analyzed proposal."));

            SseNifDocument reparsed = SseFaceGeomCarrierCodec.Parse(bytes);
            ValidateManagerCarrierDocument(
                reparsed,
                proposal.PredictedArtifact.FaceTintPath,
                diagnostics);
            if (reparsed.Blocks.Length != proposal.PredictedArtifact.BlockCount ||
                reparsed.Blocks.Count(block => block.Type == "BSDynamicTriShape") !=
                proposal.PredictedArtifact.Shapes.Length ||
                SseFaceGeomCarrierCodec.FindReachableBlockIndexes(reparsed).Count !=
                reparsed.Blocks.Length)
                diagnostics.Add(Error("sse-facegeom-carrier-output-structure",
                    "The reopened carrier block, shape, or reachability envelope does not match the proposal."));
            if (HasErrors(diagnostics))
                return FailedVerification(proposal.OutputNif, hash, bytes.Length,
                    diagnostics);
            diagnostics.Add(new Diagnostic("sse-facegeom-carrier-output-verified",
                DiagnosticSeverity.Info,
                "Reopened carrier bytes, hash, block graph, shape count, and reachability match the proposal."));
            return new SseFaceGeomCarrierMaterializationVerificationResult(true,
                proposal.OutputNif, hash, bytes.Length, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           OverflowException)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-output-read-failed",
                exception.Message));
            return FailedVerification(proposal.OutputNif, null, null,
                diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> ValidateOutput(
        WorkspacePath output,
        bool requireNew)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.Evaluate(labRoot, output));
        if (!output.IsUnder(labRoot) ||
            !output.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("sse-facegeom-carrier-output-path",
                "Carrier output must be a K-local .nif path."));
        string? parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error("sse-facegeom-carrier-output-parent",
                "Carrier output parent must already exist."));
        if (requireNew && File.Exists(output.Value))
            diagnostics.Add(Error("sse-facegeom-carrier-output-exists",
                "Carrier materialization never overwrites an existing file."));
        AddReparseDiagnostic(output.Value, diagnostics);
        return diagnostics.ToImmutable();
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
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("sse-facegeom-carrier-output-reparse",
                        "Carrier output path may not traverse a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(Error("sse-facegeom-carrier-output-stat-failed",
                    exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(Error("sse-facegeom-carrier-output-stat-denied",
                    exception.Message));
                return;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent ?? string.Empty;
        }
    }

    private static bool ArtifactMatches(
        SseFaceGeomCarrierAssemblyArtifact actual,
        SseFaceGeomCarrierAssemblyArtifact expected) =>
        actual.Sha256 == expected.Sha256 &&
        actual.ByteLength == expected.ByteLength &&
        actual.BlockCount == expected.BlockCount &&
        actual.FaceTintPath == expected.FaceTintPath &&
        actual.Shapes.SequenceEqual(expected.Shapes) &&
        actual.Bytes.AsSpan().SequenceEqual(expected.Bytes.AsSpan()) &&
        !actual.RuntimeAuthority && !expected.RuntimeAuthority;

    private static void ValidateManagerCarrierArtifact(
        SseFaceGeomCarrierAssemblyArtifact artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            SseNifDocument document =
                SseFaceGeomCarrierCodec.Parse(artifact.Bytes.ToArray());
            ValidateManagerCarrierDocument(document, artifact.FaceTintPath,
                diagnostics);
            if (document.Blocks.Length != artifact.BlockCount ||
                document.Blocks.Count(block =>
                    block.Type == "BSDynamicTriShape") !=
                artifact.Shapes.Length)
                diagnostics.Add(Error(
                    "sse-facegeom-carrier-artifact-structure",
                    "The assembler artifact metadata does not match its independently parsed Manager carrier."));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("sse-facegeom-carrier-artifact-parse",
                exception.Message));
        }
    }

    private static void ValidateManagerCarrierDocument(
        SseNifDocument document,
        AssetPath expectedFaceTintPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        QualifiedFaceGeomCarrierStructure structure =
            SseFaceGeomCarrierCodec.BuildStructure(document);
        SseFaceGeomCarrierCodec.Qualify(
            document,
            structure,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
            diagnostics);
        NifTextureTarget? target =
            SseFaceGeomCarrierCodec.FindFaceTintTarget(
                document,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                diagnostics);
        string expectedWirePath =
            expectedFaceTintPath.Value.Replace('/', '\\');
        if (target is not null &&
            !string.Equals(target.OriginalPath, expectedWirePath,
                StringComparison.Ordinal))
            diagnostics.Add(Error("sse-facegeom-carrier-facetint-binding",
                "The independently parsed Manager carrier FaceTint route does not match the exact requested path."));
    }

    private static void TryDeleteOwned(
        WorkspacePath output,
        SseFaceGeomCarrierAssemblyArtifact expected)
    {
        try
        {
            if (!File.Exists(output.Value)) return;
            var info = new FileInfo(output.Value);
            if (info.Length != expected.ByteLength) return;
            byte[] bytes = File.ReadAllBytes(output.Value);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash == expected.Sha256) File.Delete(output.Value);
        }
        catch
        {
            // A cleanup failure is reported by the missing/retained output gate.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary cleanup is best effort; the random path is never retained.
        }
    }

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SseFaceGeomCarrierMaterializationAnalysisResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static SseFaceGeomCarrierMaterializationResult RefusedApply(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, null, diagnostics.ToImmutable());

    private static SseFaceGeomCarrierMaterializationVerificationResult
        FailedVerification(
            WorkspacePath output,
            Sha256Hash? hash,
            int? length,
            ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, hash, length, diagnostics.ToImmutable());
}
