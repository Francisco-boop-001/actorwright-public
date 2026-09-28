using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGeomOrchestration.Tests;

internal sealed class RecordingAuthorityLoader : ISkyrimFaceBakeAuthorityLoader
{
    private readonly SkyrimFaceBakeAuthority _authority;

    public RecordingAuthorityLoader(SkyrimFaceBakeAuthority authority) =>
        _authority = authority;

    public int Calls { get; private set; }

    public ValueTask<SkyrimFaceBakeAuthorityLoadResult> LoadAsync(
        SkyrimFaceBakeAuthorityLoadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (request.ExpectedManifestSha256 != _authority.ManifestSha256)
        {
            return ValueTask.FromResult(new SkyrimFaceBakeAuthorityLoadResult(
                SkyrimFaceBakeAuthorityLoadStatus.ContentRefused, null,
                _authority.ManifestSha256,
                [new Diagnostic("fixture-authority-hash", DiagnosticSeverity.Error,
                    "Fixture authority hash differs.")]));
        }
        return ValueTask.FromResult(new SkyrimFaceBakeAuthorityLoadResult(
            SkyrimFaceBakeAuthorityLoadStatus.Loaded, _authority,
            _authority.ManifestSha256, ImmutableArray<Diagnostic>.Empty));
    }
}

internal sealed class RecordingRouteResolver : ISkyrimFaceRecordRouteResolver
{
    private readonly SkyrimFaceRecordRoute _route;

    public RecordingRouteResolver(SkyrimFaceRecordRoute route) => _route = route;

    public int Calls { get; private set; }
    public SkyrimFaceRecordRouteRequest? LastRequest { get; private set; }

    public ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
        SkyrimFaceRecordRouteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastRequest = request;
        return ValueTask.FromResult(new SkyrimFaceRecordRouteResult(true, _route,
            ImmutableArray<Diagnostic>.Empty));
    }
}

internal sealed class FixtureGeometryReader : ISseSelectedHeadpartNifGeometryReader
{
    private readonly ImmutableDictionary<string, FixtureGeometry> _geometry;

    public FixtureGeometryReader(ImmutableDictionary<string, FixtureGeometry> geometry) =>
        _geometry = geometry;

    public SseSelectedHeadpartNifGeometryReadResult Read(
        SseSelectedHeadpartNifGeometryReadRequest request)
    {
        if (!_geometry.TryGetValue(request.SourcePath.Value, out FixtureGeometry? fixture))
        {
            return new SseSelectedHeadpartNifGeometryReadResult(false, null,
                [new Diagnostic("fixture-geometry-missing", DiagnosticSeverity.Error,
                    "Fixture model path is absent.")]);
        }
        var shape = new SseSelectedHeadpartNifRestShape(
            fixture.ShapeName, 1, fixture.Positions.Length, fixture.Positions,
            fixture.PositionSha256, fixture.TopologySha256);
        return new SseSelectedHeadpartNifGeometryReadResult(true,
            new SseSelectedHeadpartNifGeometryDocument(
                request.SourcePath, request.ExpectedSourceSha256,
                request.Bytes.Length, 2, 2, [shape]),
            ImmutableArray<Diagnostic>.Empty);
    }
}

internal sealed class RecordingFaceBakeService : ISkyrimRaceMenuFaceBakeService
{
    public SkyrimRaceMenuFaceBakeRequest? LastRequest { get; private set; }

    public SkyrimRaceMenuFaceBakeResult Bake(SkyrimRaceMenuFaceBakeRequest request)
    {
        LastRequest = request;
        var outputs = ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeShapeOutput>();
        foreach (SkyrimRaceMenuFaceBakeCarrierShapeBinding binding in request.CarrierShapes)
        {
            SkyrimRaceMenuFaceBakeShapeTriInputs input = request.ShapeTriInputs.Single(item =>
                item.CarrierShapeName == binding.CarrierShapeName);
            ImmutableArray<Vector3> final = binding.BasePositions
                .Select(item => item + new Vector3(0.125F, -0.25F, 0.5F))
                .ToImmutableArray();
            ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> evidence =
                input.MeshMorphTri is null
                    ? ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence>.Empty
                    : [new SkyrimRaceMenuFaceBakeTriEvidence(
                        SkyrimFaceMorphTriRole.Mesh,
                        input.MeshMorphTri.SourcePath,
                        input.MeshMorphTri.ExpectedSourceSha256,
                        binding.VertexCount,
                        1,
                        SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource)];
            outputs.Add(new SkyrimRaceMenuFaceBakeShapeOutput(
                binding.CarrierShapeName,
                binding.ChargenMorphHost,
                binding.VertexCount,
                binding.ExpectedTopologySha256,
                binding.ExpectedBasePositionSha256,
                FixtureHash.Positions(final),
                final,
                input.MeshMorphTri is null
                    ? ImmutableArray<AssetPath>.Empty
                    : [input.MeshMorphTri.SourcePath],
                evidence));
        }
        return new SkyrimRaceMenuFaceBakeResult(true, outputs.ToImmutable(),
            ImmutableArray<Diagnostic>.Empty);
    }
}

internal sealed class RecordingMergeService : IRaceMenuCharGenFaceGeomMergeService
{
    private readonly byte[] _outputBytes;
    private readonly QualifiedFaceGeomCarrierStructure _structure;
    private RaceMenuCharGenFaceGeomMergeProposal? _proposal;

    public RecordingMergeService(byte[] outputBytes,
        QualifiedFaceGeomCarrierStructure structure)
    {
        _outputBytes = outputBytes;
        _structure = structure;
    }

    public int AnalyzeCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public RaceMenuCharGenFaceGeomMergeAnalyzeRequest? LastAnalyze { get; private set; }

    public ValueTask<RaceMenuCharGenFaceGeomMergeAnalysisResult> AnalyzeAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalyzeCalls++;
        LastAnalyze = request;
        foreach (RaceMenuCharGenFaceGeomShapeAuthority authority in request.ShapeAuthorities)
        {
            if (authority is not RaceMenuCharGenFaceGeomGeneratedXyzAuthority generated ||
                !File.Exists(generated.GeneratedXyzFile.Value) ||
                FixtureHash.File(generated.GeneratedXyzFile) !=
                generated.ExpectedGeneratedXyzSha256)
            {
                return ValueTask.FromResult(new RaceMenuCharGenFaceGeomMergeAnalysisResult(
                    false, null,
                    [new Diagnostic("fixture-generated-route", DiagnosticSeverity.Error,
                        "Analyze did not receive exact generated XYZ authority.")]));
            }
        }
        Sha256Hash outputHash = FixtureHash.Of(_outputBytes);
        ImmutableArray<RaceMenuCharGenFaceGeomShapeDisposition> dispositions =
            request.ShapeAuthorities.Select((authority, index) =>
            {
                var generated = (RaceMenuCharGenFaceGeomGeneratedXyzAuthority)authority;
                return new RaceMenuCharGenFaceGeomShapeDisposition(
                    generated.CarrierShapeName,
                    generated.Reason,
                    new RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence(
                        generated.GeneratedXyzFile,
                        generated.ExpectedGeneratedXyzSha256,
                        generated.ExpectedVertexCount,
                        generated.ExpectedTopologySha256,
                        generated.ExpectedGeneratedXyzSha256),
                    CarrierBlockIndex: index,
                    CarrierBlockSize: 1,
                    CarrierGeometryPayloadLength: generated.ExpectedVertexCount * 16,
                    CarrierVertexDataOffset: 0,
                    VertexCount: generated.ExpectedVertexCount,
                    VertexStride: 16,
                    PositionLaneLength: 12,
                    generated.ExpectedTopologySha256,
                    FixtureHash.Text($"carrier-position-{index}"),
                    FixtureHash.Text($"fourth-lane-{index}"),
                    CarrierRadiusOffset: 0,
                    CarrierRadius: 1F,
                    RequiredRadius: 1F,
                    OutputRadius: 1F,
                    PositionChanged: true,
                    RadiusChanged: false);
            }).ToImmutableArray();
        _proposal = new RaceMenuCharGenFaceGeomMergeProposal(
            "1", "fixture-generated-xyz-merge", request.Edition,
            request.CharGenNif, request.ExpectedCharGenSha256,
            new FileInfo(request.CharGenNif.Value).Length,
            1, 1,
            request.CarrierNif, request.ExpectedCarrierSha256,
            new FileInfo(request.CarrierNif.Value).Length,
            _structure,
            request.OutputNif,
            outputHash,
            _outputBytes.LongLength,
            dispositions,
            dispositions.Length,
            0,
            CreationKitAuthority: false,
            RuntimeAuthority: false);
        return ValueTask.FromResult(new RaceMenuCharGenFaceGeomMergeAnalysisResult(
            true, _proposal, ImmutableArray<Diagnostic>.Empty));
    }

    public ValueTask<RaceMenuCharGenFaceGeomMergeResult> ApplyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCalls++;
        Program.Assert(_proposal == proposal, "Apply received a substituted proposal.");
        using (FileStream stream = new(proposal.OutputNif.Value, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            stream.Write(_outputBytes);
            stream.Flush(flushToDisk: true);
        }
        Sha256Hash outputHash = FixtureHash.File(proposal.OutputNif);
        ImmutableArray<string> names = LastAnalyze!.ShapeAuthorities
            .Select(item => item.CarrierShapeName).ToImmutableArray();
        var verification = Verification(proposal, outputHash, names);
        var artifact = new RaceMenuCharGenFaceGeomMergeArtifact(
            "FIXTURE_GENERATED_XYZ_MERGED", proposal, outputHash,
            _outputBytes.LongLength, _structure, names.Length,
            proposal.ChangedPositionShapeCount, proposal.ExpandedRadiusCount,
            CreationKitAuthority: false, RuntimeAuthority: false);
        return ValueTask.FromResult(new RaceMenuCharGenFaceGeomMergeResult(
            true, true, artifact, verification, ImmutableArray<Diagnostic>.Empty));
    }

    public ValueTask<RaceMenuCharGenFaceGeomMergeVerificationResult> VerifyAsync(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCalls++;
        Sha256Hash outputHash = FixtureHash.File(proposal.OutputNif);
        ImmutableArray<string> names = LastAnalyze!.ShapeAuthorities
            .Select(item => item.CarrierShapeName).ToImmutableArray();
        return ValueTask.FromResult(Verification(proposal, outputHash, names));
    }

    private RaceMenuCharGenFaceGeomMergeVerificationResult Verification(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        Sha256Hash outputHash,
        ImmutableArray<string> names) =>
        new(true, proposal.OutputNif, proposal.CharGenSha256,
            proposal.CarrierSha256, outputHash, _outputBytes.LongLength,
            _structure, names,
            proposal.ShapeDispositions.Sum(item => item.VertexCount),
            proposal.ExpandedRadiusCount, ImmutableArray<Diagnostic>.Empty);
}
