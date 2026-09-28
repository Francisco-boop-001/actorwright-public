using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.FaceGeomOrchestration.Tests;

internal static class TransactionSafetyTests
{
    public static async Task TestForeignOutputRaceIsNotDeleted()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        using var merge = new TransactionMergeService(fixture.OutputBytes,
            fixture.Structure, TransactionMergeBehavior.ForeignOutputRace);
        RaceMenuNpcFaceGeomBuildService service = CreateService(fixture, merge);

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);

        Program.Assert(!result.Written && File.Exists(fixture.Request.OutputNif.Value),
            "An Apply refusal deleted the foreign file that won the output-path race.");
        Program.Assert(File.ReadAllText(fixture.Request.OutputNif.Value) ==
                       TransactionMergeService.ForeignOutputText,
            "The foreign output-path winner changed during facade rollback.");
        AssertNoGeneratedStage(fixture);
    }

    public static async Task TestUnexpectedApplyExceptionRollsBackGeneratedXyz()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        using var merge = new TransactionMergeService(fixture.OutputBytes,
            fixture.Structure, TransactionMergeBehavior.ThrowFromApply);
        RaceMenuNpcFaceGeomBuildService service = CreateService(fixture, merge);

        bool threw = false;
        try
        {
            await service.BuildAsync(fixture.Request, CancellationToken.None);
        }
        catch (InvalidOperationException exception) when (
            exception.Message == TransactionMergeService.ExpectedApplyFailure)
        {
            threw = true;
        }

        Program.Assert(threw, "The facade changed unexpected dependency exception semantics.");
        Program.Assert(!File.Exists(fixture.Request.OutputNif.Value),
            "Unexpected Apply failure emitted a FaceGeom output.");
        AssertNoGeneratedStage(fixture);
    }

    public static async Task TestWrongOrderedVerificationNamesRefuseAndRollBack()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        using var merge = new TransactionMergeService(fixture.OutputBytes,
            fixture.Structure, TransactionMergeBehavior.WrongIndependentNames);
        RaceMenuNpcFaceGeomBuildService service = CreateService(fixture, merge);

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);

        Program.Assert(!result.Written && result.Artifact is null &&
                       result.Diagnostics.Any(item =>
                           item.Code == "facegeom-independent-verification"),
            "A same-count, wrong-order independent shape payload was accepted.");
        Program.Assert(!File.Exists(fixture.Request.OutputNif.Value),
            "Exact-verification refusal retained an owned FaceGeom output.");
        AssertNoGeneratedStage(fixture);
    }

    public static async Task TestOutputCleanupFailureDoesNotBlockXyzCleanup()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        using var merge = new TransactionMergeService(fixture.OutputBytes,
            fixture.Structure, TransactionMergeBehavior.LockedOutputVerificationFailure);
        RaceMenuNpcFaceGeomBuildService service = CreateService(fixture, merge);

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);

        Program.Assert(!result.Written &&
                       result.Diagnostics.Any(item =>
                           item.Code == "facegeom-output-cleanup-failed"),
            "The locked output did not produce bounded cleanup evidence.");
        Program.Assert(File.Exists(fixture.Request.OutputNif.Value),
            "The test output lock did not hold the owned output in place.");
        AssertNoGeneratedStage(fixture);
        merge.ReleaseOutputLock();
        File.Delete(fixture.Request.OutputNif.Value);
    }

    private static RaceMenuNpcFaceGeomBuildService CreateService(
        OrchestrationFixture fixture,
        IRaceMenuCharGenFaceGeomMergeService merge) =>
        new(new RecordingAuthorityLoader(fixture.Authority),
            new RecordingRouteResolver(fixture.Route),
            new FixtureGeometryReader(fixture.Geometry),
            new RecordingFaceBakeService(), merge);

    private static void AssertNoGeneratedStage(OrchestrationFixture fixture) =>
        Program.Assert(!Directory.EnumerateDirectories(
                fixture.Request.OwnedStagingRoot.Value,
                ".facegeom-generated-*", SearchOption.TopDirectoryOnly).Any(),
            "Rollback retained generated XYZ staging artifacts.");
}

internal enum TransactionMergeBehavior
{
    ForeignOutputRace,
    ThrowFromApply,
    WrongIndependentNames,
    LockedOutputVerificationFailure
}

internal sealed class TransactionMergeService : IRaceMenuCharGenFaceGeomMergeService,
    IDisposable
{
    internal const string ForeignOutputText = "foreign-output-winner";
    internal const string ExpectedApplyFailure = "synthetic unexpected Apply failure";

    private readonly byte[] _outputBytes;
    private readonly RecordingMergeService _analysis;
    private readonly TransactionMergeBehavior _behavior;
    private ImmutableArray<string> _shapeNames;
    private FileStream? _outputLock;

    public TransactionMergeService(
        byte[] outputBytes,
        QualifiedFaceGeomCarrierStructure structure,
        TransactionMergeBehavior behavior)
    {
        _outputBytes = outputBytes;
        _analysis = new RecordingMergeService(outputBytes, structure);
        _behavior = behavior;
    }

    public async ValueTask<RaceMenuCharGenFaceGeomMergeAnalysisResult> AnalyzeAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        _shapeNames = request.ShapeAuthorities.Select(item => item.CarrierShapeName)
            .ToImmutableArray();
        return await _analysis.AnalyzeAsync(request, cancellationToken);
    }

    public ValueTask<RaceMenuCharGenFaceGeomMergeResult> ApplyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_behavior == TransactionMergeBehavior.ForeignOutputRace)
        {
            File.WriteAllText(proposal.OutputNif.Value, ForeignOutputText);
            return ValueTask.FromResult(new RaceMenuCharGenFaceGeomMergeResult(
                false, false, null, null,
                [new Diagnostic("synthetic-output-race", DiagnosticSeverity.Error,
                    "A foreign writer won the output path before Apply.")]));
        }
        if (_behavior == TransactionMergeBehavior.ThrowFromApply)
        {
            throw new InvalidOperationException(ExpectedApplyFailure);
        }

        using (FileStream stream = new(proposal.OutputNif.Value, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            stream.Write(_outputBytes);
            stream.Flush(flushToDisk: true);
        }
        Sha256Hash outputHash = FixtureHash.File(proposal.OutputNif);
        RaceMenuCharGenFaceGeomMergeVerificationResult verification =
            Verification(proposal, _shapeNames, verified: true);
        var artifact = new RaceMenuCharGenFaceGeomMergeArtifact(
            "SYNTHETIC_TRANSACTION_OUTPUT", proposal, outputHash,
            _outputBytes.LongLength, proposal.CarrierStructure,
            proposal.ShapeDispositions.Length, proposal.ChangedPositionShapeCount,
            proposal.ExpandedRadiusCount, CreationKitAuthority: false,
            RuntimeAuthority: false);
        if (_behavior == TransactionMergeBehavior.LockedOutputVerificationFailure)
        {
            _outputLock = new FileStream(proposal.OutputNif.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read);
        }
        return ValueTask.FromResult(new RaceMenuCharGenFaceGeomMergeResult(
            true, true, artifact, verification, ImmutableArray<Diagnostic>.Empty));
    }

    public ValueTask<RaceMenuCharGenFaceGeomMergeVerificationResult> VerifyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_behavior == TransactionMergeBehavior.WrongIndependentNames)
        {
            return ValueTask.FromResult(Verification(proposal,
                _shapeNames.Reverse().ToImmutableArray(), verified: true));
        }
        if (_behavior == TransactionMergeBehavior.LockedOutputVerificationFailure)
        {
            return ValueTask.FromResult(Verification(proposal, _shapeNames,
                verified: false));
        }
        return ValueTask.FromResult(Verification(proposal, _shapeNames, verified: true));
    }

    private RaceMenuCharGenFaceGeomMergeVerificationResult Verification(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        ImmutableArray<string> names,
        bool verified) =>
        new(verified, proposal.OutputNif, proposal.CharGenSha256,
            proposal.CarrierSha256, FixtureHash.File(proposal.OutputNif),
            _outputBytes.LongLength, proposal.CarrierStructure, names,
            proposal.ShapeDispositions.Sum(item => item.VertexCount),
            proposal.ExpandedRadiusCount, ImmutableArray<Diagnostic>.Empty);

    public void ReleaseOutputLock()
    {
        _outputLock?.Dispose();
        _outputLock = null;
    }

    public void Dispose() => ReleaseOutputLock();
}
