using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.FaceGeomOrchestration.Tests;

internal static partial class Program
{
    private static async Task TestMappedHeadPartAuthorityOmissionRefusal()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        SkyrimFaceBakeAuthority incomplete = fixture.Authority with
        {
            CarrierShapes = fixture.Authority.CarrierShapes.RemoveAt(0)
        };
        RecordingAuthorityLoader loader = new(incomplete);
        RecordingRouteResolver resolver = new(fixture.Route);
        var service = new RaceMenuNpcFaceGeomBuildService(loader, resolver,
            new FixtureGeometryReader(fixture.Geometry),
            new RecordingFaceBakeService(),
            new RecordingMergeService(fixture.OutputBytes, fixture.Structure));

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request, CancellationToken.None);

        Assert(!result.Written && result.Artifact is null && resolver.Calls == 0 &&
               result.Diagnostics.Any(item =>
                   item.Code == "facegeom-selected-root-authority-missing"),
            "A plan-resolved mapped HDPT omitted from carrier authority was not refused before record routing.");
        Assert(!File.Exists(fixture.Request.OutputNif.Value) &&
               !Directory.EnumerateDirectories(fixture.Request.OwnedStagingRoot.Value,
                   ".facegeom-generated-*", SearchOption.TopDirectoryOnly).Any(),
            "A mapped-headpart authority refusal left FaceGeom or generated XYZ output.");
    }

    private static async Task TestNullCustomMorphRowRefusal()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        RecordingAuthorityLoader loader = new(fixture.Authority);
        RecordingRouteResolver resolver = new(fixture.Route);
        var service = new RaceMenuNpcFaceGeomBuildService(loader, resolver,
            new FixtureGeometryReader(fixture.Geometry),
            new RecordingFaceBakeService(),
            new RecordingMergeService(fixture.OutputBytes, fixture.Structure));
        RaceMenuNpcFaceGeomBuildRequest malformed = fixture.Request with
        {
            OrderedCustomMorphs =
                ImmutableArray.CreateRange(
                    new SkyrimRaceMenuCustomMorphValue[] { null! })
        };

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            malformed, CancellationToken.None);

        Assert(!result.Written && result.Artifact is null && loader.Calls == 0 &&
               resolver.Calls == 0 && result.Diagnostics.Any(item =>
                   item.Code == "facegeom-custom-order-row-absent"),
            "A null custom-morph row did not produce a typed preflight refusal.");
        Assert(!File.Exists(fixture.Request.OutputNif.Value),
            "A null custom-morph row emitted a FaceGeom output.");
    }

    private static async Task TestExplicitRecordOnlyMappedHeadPart()
    {
        using OrchestrationFixture fixture = OrchestrationFixture.Create();
        var reference = new FormReference(new PluginName("Synthetic.esm"),
            new FormId(0x99));
        var source = new PresetHeadPart(new PresetIdentifier(reference.ToString(),
            reference.Plugin, reference.FormId), 0);
        RaceMenuNpcFormBinding provider =
            fixture.Request.AppearancePlan.ResolvedHeadParts[0].Binding;
        var mapped = new RaceMenuResolvedHeadPart(source,
            new RaceMenuNpcFormBinding(new RecordSignature("HDPT"), reference,
                reference, provider.ProviderPluginName, provider.ProviderPlugin,
                provider.ProviderPluginSha256,
                NpcHeadPartType.Misc));
        RaceMenuNpcAppearancePlan plan = fixture.Request.AppearancePlan with
        {
            HeadPartDispositions = fixture.Request.AppearancePlan.HeadPartDispositions.Add(
                new RaceMenuNpcHeadPartDisposition(source,
                    RaceMenuNpcHeadPartDispositionKind.MappedRecord, mapped, null)),
            ResolvedHeadParts = fixture.Request.AppearancePlan.ResolvedHeadParts.Add(mapped)
        };
        SkyrimFaceBakeAuthority authority = fixture.Authority with
        {
            RecordOnlyMappedHeadParts = [reference]
        };
        RecordingRouteResolver resolver = new(fixture.Route);
        var service = new RaceMenuNpcFaceGeomBuildService(
            new RecordingAuthorityLoader(authority), resolver,
            new FixtureGeometryReader(fixture.Geometry),
            new RecordingFaceBakeService(),
            new RecordingMergeService(fixture.OutputBytes, fixture.Structure));

        RaceMenuNpcFaceGeomBuildResult result = await service.BuildAsync(
            fixture.Request with { AppearancePlan = plan }, CancellationToken.None);

        Assert(result.Written && result.Verified && result.Artifact is not null,
            "An explicitly authorized record-only mapped HDPT was refused: " +
            Format(result.Diagnostics));
        Assert(resolver.LastRequest?.SelectedHeadParts.Length == 4 &&
               resolver.LastRequest!.SelectedHeadParts.All(item => item.Reference != reference) &&
               result.Artifact!.GeometrySelectedRootHeadParts.Length == 4,
            "Record-only mapped HDPT leaked into the FaceGeom record-selection route.");
    }
}
