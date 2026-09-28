using NpcManager.Application;
using NpcManager.Infrastructure;

namespace NpcManager.FaceGeomOrchestration.Tests;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("seven generated shapes complete analyze-apply-independent-verify", TestSevenShapeSuccess),
            ("record closure refusal writes no FaceGeom or XYZ", TestRecordClosureRefusal),
            ("stale source authority refuses before dependency or output work", TestStaleHashRefusal),
            ("mapped headpart omitted from authority is refused", TestMappedHeadPartAuthorityOmissionRefusal),
            ("explicit record-only mapped headpart is excluded from geometry", TestExplicitRecordOnlyMappedHeadPart),
            ("null ordered custom morph row is a typed refusal", TestNullCustomMorphRowRefusal),
            ("foreign output race is never deleted", TransactionSafetyTests.TestForeignOutputRaceIsNotDeleted),
            ("unexpected apply exception rolls back generated XYZ", TransactionSafetyTests.TestUnexpectedApplyExceptionRollsBackGeneratedXyz),
            ("wrong ordered verification names refuse and roll back", TransactionSafetyTests.TestWrongOrderedVerificationNamesRefuseAndRollBack),
            ("output cleanup failure does not block XYZ cleanup", TransactionSafetyTests.TestOutputCleanupFailureDoesNotBlockXyzCleanup)
        };
        var passed = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }
        Console.WriteLine($"RESULT PASS {passed}/{tests.Length}");
        return 0;
    }

    private static async Task TestSevenShapeSuccess()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        RecordingAuthorityLoader loader = new(fixture.Authority);
        RecordingRouteResolver resolver = new(fixture.Route);
        RecordingFaceBakeService baker = new();
        RecordingMergeService merge = new(fixture.OutputBytes, fixture.Structure);
        var service = new RaceMenuNpcFaceGeomBuildService(loader, resolver,
            new FixtureGeometryReader(fixture.Geometry), baker, merge);

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);
        Assert(result.Written && result.Verified && result.Artifact is not null,
            Format(result.Diagnostics));
        RaceMenuNpcFaceGeomBuildArtifact artifact = result.Artifact!;
        Assert(loader.Calls == 1, "Authority was materialized more than once.");
        Assert(resolver.LastRequest?.SelectedHeadParts.Length == 4 &&
               artifact.GeometrySelectedRootHeadParts.Length == 4,
            "Plan-selected carrier intersection was not the exact four root HDPTs.");
        Assert(baker.LastRequest?.CarrierShapes.Length == 7 &&
               artifact.Shapes.Length == 7,
            "The exact seven-shape bake closure was not preserved.");
        Assert(baker.LastRequest?.CustomMorphs.SequenceEqual(
                   fixture.Request.OrderedCustomMorphs) == true &&
               baker.LastRequest.CustomMorphs.Count(item =>
                   item.Name == "SyntheticNose") == 2,
            "Parser-preserved duplicate custom morph rows did not reach the bake in order.");
        Assert(merge.AnalyzeCalls == 1 && merge.ApplyCalls == 1 && merge.VerifyCalls == 1 &&
               merge.LastAnalyze?.ShapeAuthorities.All(item =>
                   item is RaceMenuCharGenFaceGeomGeneratedXyzAuthority) == true,
            "FaceGeom did not use exactly one generated-XYZ route per carrier shape.");
        Assert(artifact.Shapes.All(item => File.Exists(item.GeneratedXyzFile.Value) &&
                                           FixtureHash.File(item.GeneratedXyzFile) ==
                                           item.GeneratedXyzSha256),
            "Generated XYZ evidence was missing or changed after write.");
        Assert(File.Exists(artifact.OutputNif.Value) &&
               FixtureHash.File(artifact.OutputNif) == artifact.OutputNifSha256 &&
               !artifact.RuntimeAuthority,
            "The independently verified output identity or authority claim drifted.");
    }

    private static async Task TestRecordClosureRefusal()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        SkyrimFaceRecordRoute incomplete = fixture.Route with
        {
            HeadParts = fixture.Route.HeadParts.RemoveAt(fixture.Route.HeadParts.Length - 1)
        };
        RecordingAuthorityLoader loader = new(fixture.Authority);
        var service = new RaceMenuNpcFaceGeomBuildService(loader,
            new RecordingRouteResolver(incomplete),
            new FixtureGeometryReader(fixture.Geometry),
            new RecordingFaceBakeService(),
            new RecordingMergeService(fixture.OutputBytes, fixture.Structure));

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);
        Assert(!result.Written && result.Artifact is null &&
               result.Diagnostics.Any(item => item.Code == "facegeom-carrier-record-closure"),
            "Incomplete routed HDPT closure was not refused.");
        Assert(!File.Exists(fixture.Request.OutputNif.Value) &&
               !Directory.EnumerateDirectories(fixture.Request.OwnedStagingRoot.Value,
                   ".facegeom-generated-*", SearchOption.TopDirectoryOnly).Any(),
            "A pre-write record refusal left FaceGeom or generated XYZ output.");
    }

    private static async Task TestStaleHashRefusal()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        RecordingAuthorityLoader loader = new(fixture.Authority);
        RecordingRouteResolver resolver = new(fixture.Route);
        var service = new RaceMenuNpcFaceGeomBuildService(loader, resolver,
            new FixtureGeometryReader(fixture.Geometry), new RecordingFaceBakeService(),
            new RecordingMergeService(fixture.OutputBytes, fixture.Structure));
        RaceMenuNpcFaceGeomBuildRequest stale = fixture.Request with
        {
            ExpectedCompleteCarrierNifSha256 = FixtureHash.Of([0x44])
        };

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            stale, CancellationToken.None);
        Assert(!result.Written && result.Artifact is null && loader.Calls == 0 &&
               resolver.Calls == 0 &&
               result.Diagnostics.Any(item => item.Code is "facegeom-carrier-plan-drift" or
                                                        "facegeom-carrier-hash-mismatch"),
            "Stale carrier authority reached dependency or output work.");
        Assert(!File.Exists(fixture.Request.OutputNif.Value),
            "Stale source authority emitted a FaceGeom output.");
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
