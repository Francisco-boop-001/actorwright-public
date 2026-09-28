using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuCharGenFaceGeomMergeService
{
    private static RaceMenuCharGenFaceGeomMergeAnalyzeRequest RequestFrom(
        RaceMenuCharGenFaceGeomMergeProposal proposal)
    {
        var authorities = proposal.ShapeDispositions.Select(disposition =>
                AuthorityFrom(disposition ?? throw new InvalidDataException(
                    "A proposal shape disposition is null.")))
            .ToImmutableArray();
        return new RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
            proposal.Edition,
            proposal.CharGenNif,
            proposal.CharGenSha256,
            proposal.CarrierNif,
            proposal.CarrierSha256,
            proposal.OutputNif,
            authorities)
        {
            QualificationProfile = proposal.QualificationProfile
        };
    }

    private static RaceMenuCharGenFaceGeomShapeAuthority AuthorityFrom(
        RaceMenuCharGenFaceGeomShapeDisposition disposition) => disposition.Source switch
        {
            RaceMenuCharGenFaceGeomCharGenSourceEvidence source =>
                new RaceMenuCharGenFaceGeomCharGenXyzAuthority(
                    disposition.CarrierShapeName,
                    source.TopologySha256,
                    disposition.Reason),
            RaceMenuCharGenFaceGeomExternalXyzSourceEvidence source =>
                new RaceMenuCharGenFaceGeomExternalXyzAuthority(
                    disposition.CarrierShapeName,
                    source.XyzFile,
                    source.XyzFileSha256,
                    source.VertexCount,
                    source.TopologySha256,
                    disposition.Reason),
            RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence source =>
                new RaceMenuCharGenFaceGeomGeneratedXyzAuthority(
                    disposition.CarrierShapeName,
                    source.GeneratedXyzFile,
                    source.GeneratedXyzFileSha256,
                    source.VertexCount,
                    source.TopologySha256,
                    disposition.Reason),
            RaceMenuCharGenFaceGeomCarrierPreservedSourceEvidence source =>
                new RaceMenuCharGenFaceGeomCarrierPreservedAuthority(
                    disposition.CarrierShapeName,
                    source.PositionSha256,
                    source.TopologySha256,
                    disposition.Reason),
            null => throw new InvalidDataException(
                $"Shape disposition '{disposition.CarrierShapeName}' has no source evidence."),
            _ => throw new InvalidDataException(
                $"Shape disposition '{disposition.CarrierShapeName}' uses unsupported source evidence.")
        };

    private static bool ProposalMatches(
        RaceMenuCharGenFaceGeomMergeProposal supplied,
        RaceMenuCharGenFaceGeomMergeProposal analyzed) =>
        string.Equals(supplied.SchemaVersion, analyzed.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(supplied.Operation, analyzed.Operation, StringComparison.Ordinal) &&
        supplied.Edition == analyzed.Edition &&
        supplied.CharGenNif == analyzed.CharGenNif &&
        supplied.CharGenSha256 == analyzed.CharGenSha256 &&
        supplied.CharGenByteLength == analyzed.CharGenByteLength &&
        supplied.CharGenBlockCount == analyzed.CharGenBlockCount &&
        supplied.CharGenDynamicShapeCount == analyzed.CharGenDynamicShapeCount &&
        supplied.CarrierNif == analyzed.CarrierNif &&
        supplied.CarrierSha256 == analyzed.CarrierSha256 &&
        supplied.CarrierByteLength == analyzed.CarrierByteLength &&
        StructureMatches(supplied.CarrierStructure, analyzed.CarrierStructure) &&
        supplied.OutputNif == analyzed.OutputNif &&
        supplied.ExpectedOutputSha256 == analyzed.ExpectedOutputSha256 &&
        supplied.ExpectedOutputByteLength == analyzed.ExpectedOutputByteLength &&
        supplied.ShapeDispositions.SequenceEqual(analyzed.ShapeDispositions) &&
        supplied.ChangedPositionShapeCount == analyzed.ChangedPositionShapeCount &&
        supplied.ExpandedRadiusCount == analyzed.ExpandedRadiusCount &&
        supplied.QualificationProfile == analyzed.QualificationProfile &&
        !supplied.CreationKitAuthority && !supplied.RuntimeAuthority &&
        !analyzed.CreationKitAuthority && !analyzed.RuntimeAuthority;

    private static bool StructureMatches(
        QualifiedFaceGeomCarrierStructure left,
        QualifiedFaceGeomCarrierStructure right) =>
        left.BlockCount == right.BlockCount &&
        left.ReachableBlockCount == right.ReachableBlockCount &&
        left.RootCount == right.RootCount &&
        left.NiNodeCount == right.NiNodeCount &&
        left.FadeNodeCount == right.FadeNodeCount &&
        left.DynamicShapeCount == right.DynamicShapeCount &&
        left.NullChildReferenceCount == right.NullChildReferenceCount &&
        left.GraphSha256 == right.GraphSha256 &&
        left.ReachableShapeNames.SequenceEqual(right.ReachableShapeNames,
            StringComparer.Ordinal) &&
        left.ReachableCensus.SequenceEqual(right.ReachableCensus);

    private static async ValueTask<byte[]?> ReadBoundedFileAsync(
        WorkspacePath path,
        int maximumBytes,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("chargen-carrier-reparse-refused",
                    $"The {role} file is a reparse point."));
                return null;
            }

            await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 || stream.Length > maximumBytes)
            {
                diagnostics.Add(Error("chargen-carrier-size-limit",
                    $"The {role} file must contain 1 to {maximumBytes} bytes."));
                return null;
            }
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("chargen-carrier-read-failed",
                $"The {role} file could not be read: {exception.Message}"));
            return null;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuCharGenFaceGeomMergeResult RefusedApply(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, null, diagnostics.ToImmutable());

    private static RaceMenuCharGenFaceGeomMergeVerificationResult RefusedVerification(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, null, null, null, null, null, [], 0, 0,
            diagnostics.ToImmutable());

    private static RaceMenuCharGenFaceGeomMergeVerificationResult FailedVerification(
        WorkspacePath output,
        Sha256Hash? charGenHash,
        Sha256Hash? carrierHash,
        Sha256Hash? outputHash,
        long? outputByteLength,
        QualifiedFaceGeomCarrierStructure? outputStructure,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, charGenHash, carrierHash, outputHash, outputByteLength,
            outputStructure, [], 0, 0, diagnostics.ToImmutable());

    private static void DeleteFailedOutput(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(path.Value)) File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("chargen-carrier-failed-output-cleanup",
                $"The failed output could not be removed: {exception.Message}"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
