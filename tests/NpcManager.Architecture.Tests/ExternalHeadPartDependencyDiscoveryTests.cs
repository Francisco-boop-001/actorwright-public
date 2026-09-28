using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpDiscoveryScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-external-headpart-discovery";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        ExternalHeadPartDependencyDiscoveryTests.RunAsync(cancellationToken);
}

internal static class ExternalHeadPartDependencyDiscoveryTests
{
    private static readonly WorkspacePath ProtectedRoot =
        new(@"F:\ExampleGame");

    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string repositoryRoot = FindRepositoryRoot();
        var labRoot = new WorkspacePath(repositoryRoot);
        string scratchPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "test-work",
            "preview254-external-smp-discovery",
            $"{Environment.ProcessId:D10}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchPath);
        try
        {
            await AssertGenericProviderIsDiscoveredAsync(
                labRoot, new WorkspacePath(scratchPath), cancellationToken);
            await AssertValidRaceFormListMatrixAsync(
                labRoot, new WorkspacePath(scratchPath), cancellationToken);
        }
        finally
        {
            if (Directory.Exists(scratchPath))
                Directory.Delete(scratchPath, recursive: true);
        }
    }

    private static async ValueTask AssertGenericProviderIsDiscoveredAsync(
        WorkspacePath labRoot,
        WorkspacePath scratchRoot,
        CancellationToken cancellationToken)
    {
        string dataPath = Path.Combine(scratchRoot.Value, "Data");
        Directory.CreateDirectory(dataPath);
        var providerPlugin = new PluginName("OrchidAdornment.esp");
        string providerPath = Path.Combine(dataPath, providerPlugin.Value);
        byte[] providerBytes = Encoding.UTF8.GetBytes(
            "generic provider bytes; no Dint, SMP, or HDT naming");
        await File.WriteAllBytesAsync(providerPath, providerBytes, cancellationToken);
        var providerHash = Hash(providerBytes);
        var rootForm = new FormReference(providerPlugin, new FormId(0x800));
        var childForm = new FormReference(providerPlugin, new FormId(0x801));
        var model = new AssetPath(
            "meshes/actors/character/character assets/hair/orchid-child.nif");

        var physicsFactory = new Preview254ExternalSmpPhysicsFixtureFactory(
            labRoot);
        Preview254ExternalSmpPhysicsFixture physicsFixture =
            await physicsFactory.CreateAsync(
                scratchRoot,
                childForm,
                model,
                Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                cancellationToken);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            physicsFixture.DataRoot,
            model,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                physicsFixture.PhysicsXml.Value,
                physicsFixture.Tri.Value,
                textureSlots: [physicsFixture.DiffuseTexture.Value]),
            labRoot);

        var provider = new SkyrimFaceRecordProvider(
            providerPlugin,
            new WorkspacePath(providerPath),
            providerHash);
        SkyrimFaceRecordRoute route = CreateRoute(
            provider,
            rootForm,
            childForm,
            model,
            physicsFixture.Tri,
            physicsFixture.DiffuseTexture,
            providerBytes.LongLength,
            physicsFixture.DataRoot);

        var policy = new KOnlyWorkspacePolicy(labRoot, ProtectedRoot);
        var service = new ExternalHeadPartDependencyDiscoveryService(
            new SkyrimAssetAuthorityPlanner(
                new BethesdaAssetIndexer(), policy, labRoot),
            new SkyrimAssetContentResolver(policy, labRoot),
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));

        var request = new ExternalHeadPartDependencyDiscoveryRequest(
            physicsFixture.DataRoot,
            [providerPlugin],
            route,
            NpcSex.Male);
        ExternalHeadPartDependencyDiscoveryResult result =
            await service.DiscoverAsync(request, cancellationToken);

        Require(
            result.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
            result.Descriptor is not null &&
            !HasErrors(result.Diagnostics),
            "A generic provider-named external SMP graph was not admitted: " +
            FormatDiagnostics(result.Diagnostics));
        ExternalHeadPartDependencyDescriptor descriptor = result.Descriptor!;
        Require(descriptor.Provider.Plugin == providerPlugin &&
                descriptor.Provider.PluginSha256 == providerHash &&
                descriptor.Members.Length == 2 &&
                descriptor.Members[0].OriginForm == rootForm &&
                descriptor.Members[1].OriginForm == childForm &&
                descriptor.Members[0].HnamEdges.SequenceEqual([childForm]) &&
                descriptor.Members[1].Parent == rootForm &&
                descriptor.Physics.Mode ==
                    ExternalHeadPartPhysicsBindingMode.DirectNifExtraData &&
                descriptor.Physics.Shapes.Length == 2 &&
                descriptor.Assets.Any(item => item.Path == model) &&
                descriptor.Assets.Any(item => item.Path == physicsFixture.Tri) &&
                descriptor.Assets.Any(item =>
                    item.Path == physicsFixture.DiffuseTexture) &&
                descriptor.Assets.Any(item =>
                    item.Path == physicsFixture.PhysicsXml) &&
                descriptor.DescriptorId ==
                    ExternalHeadPartDependencyDescriptorCodec
                        .ComputeDescriptorId(descriptor),
            "The accepted generic descriptor did not preserve the closed route, " +
            "physics binding, assets, and canonical identity.");

        AssertSharedVanillaAssetOwnersRoundTrip(descriptor);
        await AssertInheritedDescriptorRediscoveryAsync(service, request, physicsFixture, labRoot, cancellationToken);

        await AssertResumeAndRefusalMatrixAsync(
            service,
            request,
            descriptor,
            physicsFixture,
            provider,
            rootForm,
            childForm,
            providerBytes,
            cancellationToken);

        await AssertOfficialVanillaMemberAcceptedAsync(
            service,
            request,
            provider,
            rootForm,
            childForm,
            model,
            physicsFixture,
            cancellationToken);
    }

    private static async Task AssertInheritedDescriptorRediscoveryAsync(
        ExternalHeadPartDependencyDiscoveryService service,
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        WorkspacePath labRoot,
        CancellationToken cancellationToken)
    {
        var rootModel = new AssetPath("meshes/hair/task7-discovery-root.nif");
        string childPath = Physical(fixture.DataRoot, fixture.ModelNif);
        byte[] original = File.ReadAllBytes(childPath);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, rootModel, original, labRoot);
        try
        {
            File.WriteAllBytes(childPath, Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null, fixture.Tri.Value, textureSlots: [fixture.DiffuseTexture.Value]));
            var route = request.RecordRoute with
            {
                HeadPartGraph = request.RecordRoute.HeadPartGraph.Select(member => member.Parent is null
                    ? member with { ModelNif = rootModel } : member).ToImmutableArray()
            };
            var reviewedRequest = request with
            { RecordRoute = route, PhysicsBinding = ExternalHeadPartPhysicsBindingDisposition.InheritRoot };
            var discovered = await service.DiscoverAsync(reviewedRequest, cancellationToken);
            Require(discovered.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                    discovered.Descriptor is { } accepted &&
                    accepted.PhysicsBinding == ExternalHeadPartPhysicsBindingDisposition.InheritRoot &&
                    accepted.Physics.MappingAuthority is null && accepted.Physics.Shapes.Length == 4 &&
                    accepted.Physics.Shapes.Count(shape => shape.Origin == ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot) == 2,
                "Real root/strand discovery lost reviewed inheritance: " + FormatDiagnostics(discovered.Diagnostics));
            var descriptor = ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(discovered.Descriptor!));
            var replay = await service.DiscoverAsync(request with { RecordRoute = route, ExpectedDescriptor = descriptor }, cancellationToken);
            Require(replay.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted && replay.Descriptor?.DescriptorId == descriptor.DescriptorId,
                "Expected-descriptor rediscovery did not forward and revalidate inherit-root: " + FormatDiagnostics(replay.Diagnostics));
            File.WriteAllBytes(Physical(fixture.DataRoot, rootModel), File.ReadAllBytes(childPath));
            var noRoot = await service.DiscoverAsync(reviewedRequest, cancellationToken);
            Require(noRoot.Status == ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(noRoot, ExternalHeadPartDiagnosticCodes.PhysicsMissing),
                "Explicit inherit-root without any locator must refuse, not become ordinary NotApplicable: " + FormatDiagnostics(noRoot.Diagnostics));
        }
        finally
        {
            File.WriteAllBytes(childPath, original);
            File.Delete(Physical(fixture.DataRoot, rootModel));
        }
    }

    private static void AssertSharedVanillaAssetOwnersRoundTrip(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var vanillaPlugin = new PluginName("Skyrim.esm");
        Sha256Hash vanillaHash = Hash("shared-vanilla-master");
        var firstForm = new FormReference(vanillaPlugin, new FormId(0x120));
        var secondForm = new FormReference(vanillaPlugin, new FormId(0x121));
        ExternalHeadPartRecordDependency template = descriptor.Members[1];
        ExternalHeadPartRecordDependency first = template with
        {
            OriginForm = firstForm,
            RequiredOutputMaster = vanillaPlugin,
            WinningForm = firstForm,
            WinningPlugin = vanillaPlugin,
            WinningPluginSha256 = vanillaHash,
            WinningPluginByteLength = 42,
            Parent = descriptor.RootSourceForm,
            RouteOrder = 1
        };
        ExternalHeadPartRecordDependency second = first with
        {
            OriginForm = secondForm,
            WinningForm = secondForm,
            RouteOrder = 2
        };
        ExternalHeadPartRecordDependency root = descriptor.Members[0] with
        {
            HnamEdges = [firstForm, secondForm]
        };
        AssetPath sharedModel = first.ModelNif!.Value;
        ImmutableArray<ExternalHeadPartAssetDependency> assets =
            descriptor.Assets
                .Where(asset => asset.Path == sharedModel ||
                    first.TriRoutes.Any(route => route.Path == asset.Path))
                .Select(asset => asset with
                {
                    ProviderPlugin = vanillaPlugin,
                    ProviderPluginSha256 = vanillaHash
                })
                .ToImmutableArray();
        ExternalHeadPartPhysicsBinding physics = descriptor.Physics with
        {
            Shapes = descriptor.Physics.Shapes.Select(shape => shape with
            {
                MemberForm = firstForm
            }).ToImmutableArray()
        };
        ExternalHeadPartDependencyDescriptor shared = descriptor with
        {
            Members = [root, first, second],
            Assets = assets,
            Physics = physics,
            DescriptorId = default
        };
        shared = shared with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(shared)
        };
        _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(shared);

        ExternalHeadPartDependencyDescriptor relabeled = shared with
        {
            Assets = shared.Assets.Select(asset => asset with
            {
                ProviderPlugin = new PluginName("Dawnguard.esm"),
                ProviderPluginSha256 = Hash("relabeled-asset-owner")
            }).ToImmutableArray(),
            DescriptorId = default
        };
        bool relabelRefused = false;
        try
        {
            relabeled = relabeled with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(relabeled)
            };
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(relabeled);
        }
        catch (InvalidDataException)
        {
            relabelRefused = true;
        }
        Require(relabelRefused,
            "A vanilla asset relabeled to an arbitrary plugin was accepted.");

        ExternalHeadPartRecordDependency inconsistent = second with
        {
            OriginForm = new FormReference(
                new PluginName("Dawnguard.esm"), new FormId(0x122)),
            RequiredOutputMaster = new PluginName("Dawnguard.esm"),
            WinningForm = new FormReference(
                new PluginName("Dawnguard.esm"), new FormId(0x122)),
            WinningPlugin = new PluginName("Dawnguard.esm"),
            WinningPluginSha256 = Hash("different-vanilla-master")
        };
        ExternalHeadPartDependencyDescriptor mixed = shared with
        {
            Members = [
                root with { HnamEdges = [firstForm, inconsistent.OriginForm] },
                first,
                inconsistent with { Parent = descriptor.RootSourceForm }
            ],
            DescriptorId = default
        };
        bool refused = false;
        try
        {
            mixed = mixed with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(mixed)
            };
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(mixed);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Require(refused,
            "Conflicting official-vanilla owners for one asset were accepted.");
    }

    private static async ValueTask AssertOfficialVanillaMemberAcceptedAsync(
        ExternalHeadPartDependencyDiscoveryService service,
        ExternalHeadPartDependencyDiscoveryRequest request,
        SkyrimFaceRecordProvider provider,
        FormReference rootForm,
        FormReference childForm,
        AssetPath model,
        Preview254ExternalSmpPhysicsFixture fixture,
        CancellationToken cancellationToken)
    {
        var vanillaPlugin = new PluginName("Skyrim.esm");
        byte[] vanillaBytes = Encoding.UTF8.GetBytes("official vanilla master bytes");
        string vanillaPath = Path.Combine(fixture.DataRoot.Value, vanillaPlugin.Value);
        await File.WriteAllBytesAsync(vanillaPath, vanillaBytes, cancellationToken);
        var vanillaHash = Hash(vanillaBytes);
        var vanillaForm = new FormReference(vanillaPlugin, new FormId(0x100));
        var vanillaModel = new AssetPath(
            "meshes/actors/character/character assets/hair/vanilla-hair.nif");
        var vanillaTri = new AssetPath(
            "meshes/actors/character/character assets/hair/vanilla-hair.tri");
        var vanillaDiffuse = new AssetPath(
            "textures/actors/character/hair/vanilla-hair.dds");
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            fixture.DataRoot,
            vanillaModel,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null,
                vanillaTri.Value,
                textureSlots: [vanillaDiffuse.Value]),
            fixture.LabRoot);
        File.Copy(
            Physical(fixture.DataRoot, fixture.Tri),
            Physical(fixture.DataRoot, vanillaTri),
            overwrite: true);
        File.Copy(
            Physical(fixture.DataRoot, fixture.DiffuseTexture),
            Physical(fixture.DataRoot, vanillaDiffuse),
            overwrite: true);
        byte[] vanillaModelBytes = File.ReadAllBytes(
            Physical(fixture.DataRoot, vanillaModel));
        byte[] vanillaTriBytes = File.ReadAllBytes(
            Physical(fixture.DataRoot, vanillaTri));
        byte[] vanillaDiffuseBytes = File.ReadAllBytes(
            Physical(fixture.DataRoot, vanillaDiffuse));
        var race = new FormReference(provider.Plugin, new FormId(0x900));
        var vanillaProvider = new SkyrimFaceRecordProvider(
            vanillaPlugin,
            new WorkspacePath(vanillaPath),
            vanillaHash);
        var vanillaValidRaces = new FormReference(
            vanillaPlugin, new FormId(0x101));
        var vanillaValidRaceList = new SkyrimFaceFormListRoute(
            vanillaValidRaces,
            vanillaProvider,
            [race],
            false);
        var vanillaGraph = new SkyrimFaceHeadPartGraphRoute(
            vanillaForm,
            vanillaPlugin,
            vanillaForm,
            vanillaPlugin,
            vanillaHash,
            vanillaBytes.LongLength,
            Hash("vanilla-record"),
            "VanillaHairMember",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            vanillaModel,
            [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, vanillaTri)],
            [],
            rootForm,
            1,
            false,
            false,
            2,
            NpcSex.Male,
            vanillaValidRaces)
        {
            ValidRaceList = vanillaValidRaceList
        };
        SkyrimFaceHeadPartRecordRoute vanillaLegacy =
            new(
                vanillaForm,
                vanillaProvider,
                "VanillaHairMember",
                NpcHeadPartType.Hair,
                NpcHeadPartType.Hair,
                vanillaModel,
                [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, vanillaTri)],
                [],
                rootForm,
                1,
                false,
                false)
            {
                TextureSet = new SkyrimFaceTextureSetRecordRoute(
                    new FormReference(vanillaPlugin, new FormId(0x110)),
                    new SkyrimFaceRecordProvider(
                        vanillaPlugin,
                        new WorkspacePath(vanillaPath),
                        vanillaHash),
                    [vanillaDiffuse.Value])
            };
        SkyrimFaceHeadPartGraphRoute root = request.RecordRoute.HeadPartGraph[0] with
        {
            HnamEdges = [childForm, vanillaForm]
        };
        SkyrimFaceRecordRoute integratedRoute = request.RecordRoute with
        {
            HeadPartGraph = [root, request.RecordRoute.HeadPartGraph[1], vanillaGraph],
            HeadParts = request.RecordRoute.HeadParts.Add(vanillaLegacy)
        };
        ExternalHeadPartDependencyDiscoveryResult result =
            await service.DiscoverAsync(
                request with
                {
                    PluginOrder = [provider.Plugin, vanillaPlugin],
                    RecordRoute = integratedRoute
                },
                cancellationToken);

        Require(
            result.Status == ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
            result.Descriptor is not null &&
            !HasErrors(result.Diagnostics),
            "An external root with an official vanilla HNAM member was refused: " +
            FormatDiagnostics(result.Diagnostics));
        ExternalHeadPartDependencyDescriptor descriptor = result.Descriptor!;
        ExternalHeadPartRecordDependency vanilla = descriptor.Members.Single(
            member => member.OriginForm == vanillaForm);
        Require(
            vanilla.RequiredOutputMaster == vanillaPlugin &&
            vanilla.WinningForm == vanillaForm &&
            vanilla.WinningPlugin == vanillaPlugin &&
            vanilla.WinningPluginSha256 == vanillaHash &&
            vanilla.WinningPluginByteLength == vanillaBytes.LongLength &&
            descriptor.Assets.Any(asset => asset.Path == vanillaModel &&
                asset.ProviderPlugin == vanillaPlugin &&
                asset.ProviderPluginSha256 == vanillaHash &&
                asset.ByteLength == vanillaModelBytes.LongLength) &&
            descriptor.Assets.Any(asset => asset.Path == vanillaTri &&
                asset.ProviderPlugin == vanillaPlugin &&
                asset.ProviderPluginSha256 == vanillaHash &&
                asset.ByteLength == vanillaTriBytes.LongLength) &&
            descriptor.Assets.Any(asset => asset.Path == vanillaDiffuse &&
                asset.ProviderPlugin == vanillaPlugin &&
                asset.ProviderPluginSha256 == vanillaHash &&
                asset.ByteLength == vanillaDiffuseBytes.LongLength) &&
            descriptor.Physics.Shapes.All(shape => shape.MemberForm != vanillaForm) &&
            descriptor.Physics.Shapes.All(shape => shape.ModelNif != vanillaModel),
            "The accepted descriptor lost vanilla asset ownership or passed a " +
            "vanilla member into the external physics envelope.");

        AssertVanillaCodecAuthoritySensitivity(descriptor, vanillaForm);

        byte[] originalVanillaModel = File.ReadAllBytes(
            Physical(fixture.DataRoot, vanillaModel));
        byte[] driftedVanillaModel = originalVanillaModel.ToArray();
        driftedVanillaModel[0] ^= 0x01;
        File.WriteAllBytes(
            Physical(fixture.DataRoot, vanillaModel), driftedVanillaModel);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult assetDrift =
                await service.DiscoverAsync(
                    request with
                    {
                        RecordRoute = integratedRoute,
                        PluginOrder = [provider.Plugin, vanillaPlugin],
                        ExpectedDescriptor = descriptor
                    },
                    cancellationToken);
            Require(assetDrift.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasErrors(assetDrift.Diagnostics),
                "Vanilla model byte drift was admitted.");
        }
        finally
        {
            File.WriteAllBytes(
                Physical(fixture.DataRoot, vanillaModel), originalVanillaModel);
        }

        SkyrimFaceHeadPartGraphRoute hashDriftGraph = vanillaGraph with
        {
            WinningPluginSha256 = Hash("drifted-vanilla-plugin")
        };
        ExternalHeadPartDependencyDiscoveryResult pluginDrift =
            await service.DiscoverAsync(
                request with
                {
                    RecordRoute = integratedRoute with
                    {
                        HeadPartGraph = [
                            root,
                            request.RecordRoute.HeadPartGraph[1],
                            hashDriftGraph
                        ]
                    },
                    PluginOrder = [provider.Plugin, vanillaPlugin]
                },
                cancellationToken);
        Require(pluginDrift.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
            HasDiagnostic(pluginDrift, ExternalHeadPartDiagnosticCodes.RecordDrift),
            "Vanilla plugin hash drift was admitted or lacked RecordDrift evidence.");
    }

    private static void AssertVanillaCodecAuthoritySensitivity(
        ExternalHeadPartDependencyDescriptor descriptor,
        FormReference vanillaForm)
    {
        int vanillaIndex = descriptor.Members
            .Select((member, index) => (member, index))
            .Single(item => item.member.OriginForm == vanillaForm)
            .index;
        ExternalHeadPartRecordDependency vanilla = descriptor.Members[vanillaIndex];
        ExternalHeadPartRecordDependency hashDrift = vanilla with
        {
            WinningPluginSha256 = Hash("different-vanilla-master")
        };
        ExternalHeadPartDependencyDescriptor hashDriftDescriptor =
            descriptor with
            {
                Members = descriptor.Members.SetItem(vanillaIndex, hashDrift)
            };
        Sha256Hash originalId =
            ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(descriptor);
        Sha256Hash driftedId =
            ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(
                hashDriftDescriptor);
        Require(originalId != driftedId,
            "Changing an official vanilla plugin hash did not change descriptor identity.");

        ExternalHeadPartRecordDependency pluginDrift = vanilla with
        {
            WinningPlugin = new PluginName("ArbitraryOther.esp")
        };
        ExternalHeadPartDependencyDescriptor pluginDriftDescriptor =
            descriptor with
            {
                Members = descriptor.Members.SetItem(vanillaIndex, pluginDrift),
                DescriptorId = default
            };
        bool refused = false;
        try
        {
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                pluginDriftDescriptor with
                {
                    DescriptorId =
                        ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(
                            pluginDriftDescriptor)
                });
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Require(refused,
            "An arbitrary non-vanilla member plugin was accepted as vanilla authority.");
    }

    private static async ValueTask AssertResumeAndRefusalMatrixAsync(
        ExternalHeadPartDependencyDiscoveryService service,
        ExternalHeadPartDependencyDiscoveryRequest request,
        ExternalHeadPartDependencyDescriptor descriptor,
        Preview254ExternalSmpPhysicsFixture fixture,
        SkyrimFaceRecordProvider provider,
        FormReference rootForm,
        FormReference childForm,
        byte[] providerBytes,
        CancellationToken cancellationToken)
    {
        ExternalHeadPartDependencyDiscoveryResult exactResume =
            await service.DiscoverAsync(
                request with { ExpectedDescriptor = descriptor },
                cancellationToken);
        Require(exactResume.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                exactResume.Descriptor is not null &&
                exactResume.Descriptor.DescriptorId == descriptor.DescriptorId,
            "An exact expected descriptor was not accepted after independent rediscovery.");

        ExternalHeadPartDependencyDescriptor driftedExpected =
            descriptor with { GraphSha256 = Hash("drifted-graph") };
        driftedExpected = driftedExpected with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(driftedExpected)
        };
        ExternalHeadPartDependencyDiscoveryResult driftedResume =
            await service.DiscoverAsync(
                request with { ExpectedDescriptor = driftedExpected },
                cancellationToken);
        Require(driftedResume.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(driftedResume, ExternalHeadPartDiagnosticCodes.DescriptorLost),
            "A drifted expected descriptor was accepted or lacked DescriptorLost evidence.");

        SkyrimFaceHeadPartGraphRoute root =
            request.RecordRoute.HeadPartGraph.Single(item =>
                item.OriginForm == rootForm);
        SkyrimFaceHeadPartGraphRoute child =
            request.RecordRoute.HeadPartGraph.Single(item =>
                item.OriginForm == childForm);

        ExternalHeadPartDependencyDiscoveryResult childWinnerMismatch =
            await service.DiscoverAsync(
                request with
                {
                    RecordRoute = request.RecordRoute with
                    {
                        HeadPartGraph = [
                            root,
                            child with
                            {
                                WinningForm = new FormReference(
                                    new PluginName("Patch.esp"),
                                    childForm.FormId)
                            }
                        ]
                    }
                },
                cancellationToken);
        Require(childWinnerMismatch.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(childWinnerMismatch,
                    ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported),
            "A child WinningForm/plugin mismatch was admitted or lacked " +
            "WinningProviderOverrideUnsupported evidence: " +
            FormatDiagnostics(childWinnerMismatch.Diagnostics));

        FormReference unrelatedForm = new(provider.Plugin, new FormId(0x802));
        SkyrimFaceHeadPartGraphRoute unrelatedRoot = root with
        {
            OriginForm = unrelatedForm,
            WinningForm = unrelatedForm,
            EditorId = "OrchidEyes",
            DeclaredType = NpcHeadPartType.Eyes,
            EffectiveType = NpcHeadPartType.Eyes,
            HnamEdges = [],
            Parent = null,
            Depth = 0,
            IsSelected = false,
            RouteOrder = 2
        };
        ExternalHeadPartDependencyDiscoveryResult unrelatedRoots =
            await service.DiscoverAsync(
                request with
                {
                    RecordRoute = request.RecordRoute with
                    {
                        HeadPartGraph = [root, child, unrelatedRoot]
                    }
                },
                cancellationToken);
        Require(unrelatedRoots.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                unrelatedRoots.Descriptor is not null &&
                unrelatedRoots.Descriptor.Members.Length == 2,
            "An unrelated Eyes root poisoned the selected Hair closure: " +
            FormatDiagnostics(unrelatedRoots.Diagnostics));

        string racePluginPath = Path.Combine(
            request.DataRoot.Value, "Skyrim.esm");
        byte[] racePluginBytes = Encoding.UTF8.GetBytes("race authority");
        File.WriteAllBytes(racePluginPath, racePluginBytes);
        try
        {
            var raceProvider = new SkyrimFaceRecordProvider(
                new PluginName("Skyrim.esm"),
                new WorkspacePath(racePluginPath),
                Hash(racePluginBytes));
            ExternalHeadPartDependencyDiscoveryResult splitProvider =
                await service.DiscoverAsync(
                    request with
                    {
                        RecordRoute = request.RecordRoute with
                        {
                            Race = request.RecordRoute.Race with
                            {
                                Provider = raceProvider
                            }
                        }
                    },
                    cancellationToken);
            Require(splitProvider.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                    splitProvider.Descriptor is not null,
                "A model-less Hair root with an external child/provider was refused " +
                "when the race provider was Skyrim.esm: " +
                FormatDiagnostics(splitProvider.Diagnostics));
        }
        finally
        {
            File.Delete(racePluginPath);
        }

        string modelPath = Physical(fixture.DataRoot, fixture.ModelNif);
        byte[] originalModel = File.ReadAllBytes(modelPath);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            fixture.DataRoot,
            fixture.ModelNif,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null,
                fixture.Tri.Value,
                textureSlots: [fixture.DiffuseTexture.Value]),
            fixture.LabRoot);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult ordinaryModel =
                await service.DiscoverAsync(request, cancellationToken);
            Require(ordinaryModel.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.NotApplicable,
                "A real model-bearing ordinary Hair NIF without direct/fallback " +
                "physics was not NotApplicable: " +
                FormatDiagnostics(ordinaryModel.Diagnostics));
        }
        finally
        {
            File.WriteAllBytes(modelPath, originalModel);
        }

        string fallbackDirectory = Physical(
            fixture.DataRoot, fixture.DefaultBbpXml);
        Directory.CreateDirectory(fallbackDirectory);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult invalidFallback =
                await service.DiscoverAsync(request, cancellationToken);
            Require(invalidFallback.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused,
                "A present fallback-map directory was treated as cleanly " +
                "absent and returned NotApplicable: " +
                FormatDiagnostics(invalidFallback.Diagnostics));
        }
        finally
        {
            Directory.Delete(fallbackDirectory, recursive: true);
        }

        byte[] modelBeforeMalformedFallback = File.ReadAllBytes(modelPath);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            fixture.DataRoot,
            fixture.ModelNif,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null,
                fixture.Tri.Value,
                textureSlots: [fixture.DiffuseTexture.Value]),
            fixture.LabRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackDirectory)!);
            File.WriteAllText(fallbackDirectory, "<defaultBBPs>");
            ExternalHeadPartDependencyDiscoveryResult malformedFallback =
                await service.DiscoverAsync(request, cancellationToken);
            Require(malformedFallback.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused,
                "A malformed present fallback-map authority was not refused: " +
                FormatDiagnostics(malformedFallback.Diagnostics));
        }
        finally
        {
            if (File.Exists(fallbackDirectory))
                File.Delete(fallbackDirectory);
            File.WriteAllBytes(modelPath, modelBeforeMalformedFallback);
        }

        SkyrimFaceHeadPartGraphRoute faceRoot = root with
        {
            EditorId = "OrchidFace",
            DeclaredType = NpcHeadPartType.Face,
            EffectiveType = NpcHeadPartType.Face,
            ModelNif = null,
            TriRoutes = [],
            HnamEdges = [],
            Parent = null,
            Depth = 0,
            IsSelected = true,
            RouteOrder = 0
        };
        SkyrimFaceRecordRoute faceOnlyRoute = request.RecordRoute with
        {
            HeadParts = [],
            HeadPartGraph = [faceRoot]
        };
        ExternalHeadPartDependencyDiscoveryResult faceOnly =
            await service.DiscoverAsync(
                request with { RecordRoute = faceOnlyRoute },
                cancellationToken);
        Require(faceOnly.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.NotApplicable &&
                !HasErrors(faceOnly.Diagnostics),
            "A fully resolved selected Face-only route was refused or emitted " +
            "errors: " + FormatDiagnostics(faceOnly.Diagnostics));

        SkyrimFaceRecordRoute ordinaryRoute = request.RecordRoute with
        {
            HeadParts = [],
            HeadPartGraph = [root with { HnamEdges = [] }]
        };
        ExternalHeadPartDependencyDiscoveryResult ordinary =
            await service.DiscoverAsync(
                request with { RecordRoute = ordinaryRoute },
                cancellationToken);
        Require(ordinary.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.NotApplicable &&
                !HasErrors(ordinary.Diagnostics),
            "An ordinary Hair route without an admitted external binding was not NotApplicable.");

        SkyrimFaceRecordRoute wigRoute = request.RecordRoute with
        {
            HeadParts = [],
            HeadPartGraph = [
                root with
                {
                    DeclaredType = NpcHeadPartType.FacialHair,
                    EffectiveType = NpcHeadPartType.FacialHair,
                    HnamEdges = []
                }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult wig =
            await service.DiscoverAsync(
                request with { RecordRoute = wigRoute },
                cancellationToken);
        Require(wig.Status == ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(wig, ExternalHeadPartDiagnosticCodes.RootWigOnly),
            "A wig-only root was not refused with RootWigOnly.");

        SkyrimFaceRecordRoute mixedProviderRoute = request.RecordRoute with
        {
            HeadPartGraph = [
                root,
                child with
                {
                    WinningPlugin = new PluginName("OtherProvider.esp"),
                    WinningPluginSha256 = Hash("other-provider"),
                    WinningPluginByteLength = 17
                }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult mixedProvider =
            await service.DiscoverAsync(
                request with { RecordRoute = mixedProviderRoute },
                cancellationToken);
        Require(mixedProvider.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(mixedProvider,
                    ExternalHeadPartDiagnosticCodes.GraphMixedProvider),
            "A mixed-provider graph was admitted or lacked GraphMixedProvider evidence.");

        SkyrimFaceRecordRoute cycleRoute = request.RecordRoute with
        {
            HeadPartGraph = [
                root,
                child with { HnamEdges = [rootForm] }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult cycle =
            await service.DiscoverAsync(
                request with { RecordRoute = cycleRoute },
                cancellationToken);
        Require(cycle.Status == ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(cycle, ExternalHeadPartDiagnosticCodes.GraphCycle),
            "A cyclic bounded graph was admitted or lacked GraphCycle evidence.");

        SkyrimFaceRecordRoute missingMemberRoute = request.RecordRoute with
        {
            HeadPartGraph = [root]
        };
        ExternalHeadPartDependencyDiscoveryResult missingMember =
            await service.DiscoverAsync(
                request with { RecordRoute = missingMemberRoute },
                cancellationToken);
        Require(missingMember.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(missingMember,
                    ExternalHeadPartDiagnosticCodes.RecordUnresolved),
            "A graph with a missing HNAM member was admitted or lacked RecordUnresolved evidence.");

        AssetPath missingModel = new(
            "meshes/actors/character/character assets/hair/missing-model.nif");
        SkyrimFaceRecordRoute missingModelRoute = request.RecordRoute with
        {
            HeadPartGraph = [
                root,
                child with { ModelNif = missingModel }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult missingModelResult =
            await service.DiscoverAsync(
                request with { RecordRoute = missingModelRoute },
                cancellationToken);
        Require(missingModelResult.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(missingModelResult,
                    ExternalHeadPartDiagnosticCodes.AssetMissing),
            "A missing model asset was admitted or lacked AssetMissing evidence.");

        AssetPath missingTri = new(
            "meshes/actors/character/character assets/hair/missing-model.tri");
        SkyrimFaceRecordRoute missingTriRoute = request.RecordRoute with
        {
            HeadPartGraph = [
                root,
                child with
                {
                    TriRoutes = [
                        new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, missingTri)
                    ]
                }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult missingTriResult =
            await service.DiscoverAsync(
                request with { RecordRoute = missingTriRoute },
                cancellationToken);
        Require(missingTriResult.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(missingTriResult,
                    ExternalHeadPartDiagnosticCodes.AssetMissing),
            "A missing TRI asset was admitted or lacked AssetMissing evidence.");

        SkyrimFaceRecordRoute partialModelRoute = request.RecordRoute with
        {
            HeadPartGraph = [
                root,
                child with { ModelNif = null }
            ]
        };
        ExternalHeadPartDependencyDiscoveryResult partialModel =
            await service.DiscoverAsync(
                request with { RecordRoute = partialModelRoute },
                cancellationToken);
        Require(partialModel.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(partialModel,
                    ExternalHeadPartDiagnosticCodes.AssetMissing),
            "A model-less member with declared TRI assets was swallowed as " +
            "NotApplicable: " + FormatDiagnostics(partialModel.Diagnostics));

        FormReference collisionForm = new(provider.Plugin, new FormId(0x803));
        AssetPath collisionModel = new(
            "MESHES/actors/character/character assets/hair/orchid-child.nif");
        SkyrimFaceHeadPartGraphRoute collisionMember = child with
        {
            OriginForm = collisionForm,
            WinningForm = collisionForm,
            EditorId = "OrchidCollisionHair",
            ModelNif = collisionModel,
            HnamEdges = [],
            Parent = rootForm,
            Depth = 1,
            RouteOrder = 2
        };
        SkyrimFaceRecordRoute collisionRoute = request.RecordRoute with
        {
            HeadPartGraph = [root with { HnamEdges = [childForm, collisionForm] },
                child, collisionMember]
        };
        ExternalHeadPartDependencyDiscoveryResult collision =
            await service.DiscoverAsync(
                request with { RecordRoute = collisionRoute },
                cancellationToken);
        Require(collision.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(collision, ExternalHeadPartDiagnosticCodes.AssetDrift),
            "Case-colliding declared asset paths were silently deduplicated: " +
            FormatDiagnostics(collision.Diagnostics));

        string physicsPath = Physical(fixture.DataRoot, fixture.PhysicsXml);
        byte[] originalPhysicsXml = File.ReadAllBytes(physicsPath);
        File.Delete(physicsPath);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult missingXml =
                await service.DiscoverAsync(request, cancellationToken);
            Require(missingXml.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(missingXml,
                        ExternalHeadPartDiagnosticCodes.AssetMissing),
                "A missing direct physics XML was admitted or lacked AssetMissing evidence: " +
                FormatDiagnostics(missingXml.Diagnostics));
        }
        finally
        {
            File.WriteAllBytes(physicsPath, originalPhysicsXml);
        }

        string providerPath = provider.Path.Value;
        byte[] driftedProvider = providerBytes.ToArray();
        driftedProvider[0] ^= 0x01;
        File.WriteAllBytes(providerPath, driftedProvider);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult recordDrift =
                await service.DiscoverAsync(request, cancellationToken);
            Require(recordDrift.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(recordDrift, ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A changed provider plugin was admitted or lacked RecordDrift evidence.");
        }
        finally
        {
            File.WriteAllBytes(providerPath, providerBytes);
        }

        await AssertCrossPluginWinningOverrideRefusedAsync(
            service, request, root, child, provider, rootForm, cancellationToken);
        await AssertAssetDriftRefusedAsync(
            request, fixture, provider, root, child, cancellationToken);
        await AssertProviderDriftAfterPhysicsRefusedAsync(
            request, fixture, provider, cancellationToken);
        await AssertDeterministicAssetOrderAsync(
            request, fixture, provider, descriptor, cancellationToken);
        await AssertAuthorityClosureRefusedAsync(
            request, fixture, cancellationToken);
        await AssertReparseProviderRefusedAsync(
            service, request, provider, fixture, cancellationToken);
        await AssertVanillaMemberTask3CharacterizationAsync(
            request, rootForm, childForm, provider, cancellationToken);
        await AssertRealArchiveClosureAsync(
            request, fixture, provider, fixture.LabRoot, cancellationToken);
    }

    private static async ValueTask AssertCrossPluginWinningOverrideRefusedAsync(
        ExternalHeadPartDependencyDiscoveryService service,
        ExternalHeadPartDependencyDiscoveryRequest request,
        SkyrimFaceHeadPartGraphRoute root,
        SkyrimFaceHeadPartGraphRoute child,
        SkyrimFaceRecordProvider provider,
        FormReference rootForm,
        CancellationToken cancellationToken)
    {
        var overridePlugin = new PluginName("OrchidAdornmentPatch.esp");
        string overridePath = Path.Combine(
            request.DataRoot.Value, overridePlugin.Value);
        byte[] overrideBytes = Encoding.UTF8.GetBytes("winning override bytes");
        File.WriteAllBytes(overridePath, overrideBytes);
        try
        {
            var overrideProvider = new SkyrimFaceRecordProvider(
                overridePlugin,
                new WorkspacePath(overridePath),
                Hash(overrideBytes));
            SkyrimFaceRecordRoute overrideRoute = request.RecordRoute with
            {
                Race = request.RecordRoute.Race with
                {
                    Provider = overrideProvider
                },
                HeadPartGraph = [
                    root with
                    {
                        RequiredOutputMaster = overridePlugin,
                        WinningForm = new FormReference(
                            overridePlugin, rootForm.FormId),
                        WinningPlugin = overridePlugin,
                        WinningPluginSha256 = overrideProvider.Sha256,
                        WinningPluginByteLength = overrideBytes.LongLength
                    },
                    child
                ]
            };
            ExternalHeadPartDependencyDiscoveryResult result =
                await service.DiscoverAsync(
                    request with
                    {
                        PluginOrder = [provider.Plugin, overridePlugin],
                        RecordRoute = overrideRoute
                    },
                    cancellationToken);
            Require(result.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(result,
                        ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported),
                "A cross-plugin winning override was admitted or lacked " +
                "WinningProviderOverrideUnsupported evidence.");
        }
        finally
        {
            if (File.Exists(overridePath))
                File.Delete(overridePath);
        }
    }

    private static async ValueTask AssertAssetDriftRefusedAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        SkyrimFaceRecordProvider provider,
        SkyrimFaceHeadPartGraphRoute root,
        SkyrimFaceHeadPartGraphRoute child,
        CancellationToken cancellationToken)
    {
        string colliderPath = Physical(fixture.DataRoot, fixture.ColliderNif);
        byte[] originalCollider = File.ReadAllBytes(colliderPath);
        bool mutated = false;
        var policy = new KOnlyWorkspacePolicy(
            fixture.LabRoot, ProtectedRoot);
        var driftResolver = new ExternalHeadPartPhysicsBindingResolver(
            policy,
            fixture.LabRoot,
            _ =>
            {
                if (!mutated)
                {
                    mutated = true;
                    File.WriteAllBytes(
                        colliderPath, Encoding.UTF8.GetBytes("drifted-collider"));
                }
            });
        var service = new ExternalHeadPartDependencyDiscoveryService(
            new SkyrimAssetAuthorityPlanner(
                new BethesdaAssetIndexer(), policy, fixture.LabRoot),
            new SkyrimAssetContentResolver(policy, fixture.LabRoot),
            driftResolver);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult result =
                await service.DiscoverAsync(request, cancellationToken);
            Require(result.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(result, ExternalHeadPartDiagnosticCodes.AssetDrift),
                "A post-resolution collider mutation was admitted or lacked AssetDrift evidence.");
        }
        finally
        {
            File.WriteAllBytes(colliderPath, originalCollider);
        }
    }

    private static async ValueTask AssertProviderDriftAfterPhysicsRefusedAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        SkyrimFaceRecordProvider provider,
        CancellationToken cancellationToken)
    {
        byte[] original = File.ReadAllBytes(provider.Path.Value);
        byte[] mutated = original.ToArray();
        mutated[0] ^= 0x01;
        var policy = new KOnlyWorkspacePolicy(
            fixture.LabRoot, ProtectedRoot);
        var resolver = new ExternalHeadPartPhysicsBindingResolver(
            policy,
            fixture.LabRoot,
            _ => File.WriteAllBytes(provider.Path.Value, mutated));
        var service = new ExternalHeadPartDependencyDiscoveryService(
            new SkyrimAssetAuthorityPlanner(
                new BethesdaAssetIndexer(), policy, fixture.LabRoot),
            new SkyrimAssetContentResolver(policy, fixture.LabRoot),
            resolver);
        try
        {
            ExternalHeadPartDependencyDiscoveryResult result =
                await service.DiscoverAsync(request, cancellationToken);
            Require(result.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(result, ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A provider mutation after physics resolution was admitted: " +
                FormatDiagnostics(result.Diagnostics));
        }
        finally
        {
            File.WriteAllBytes(provider.Path.Value, original);
        }
    }

    private static async ValueTask AssertDeterministicAssetOrderAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        SkyrimFaceRecordProvider provider,
        ExternalHeadPartDependencyDescriptor expected,
        CancellationToken cancellationToken)
    {
        AssetPath[] expectedOrder =
        [
            fixture.ColliderNif,
            fixture.Tri,
            fixture.ModelNif,
            fixture.PhysicsXml,
            fixture.DiffuseTexture
        ];
        Require(expected.Assets.Select(item => item.Path)
                    .SequenceEqual(expectedOrder),
            "Accepted asset authorities did not use the exact deterministic order.");

        var policy = new KOnlyWorkspacePolicy(
            fixture.LabRoot, ProtectedRoot);
        var service = new ExternalHeadPartDependencyDiscoveryService(
            new PermutingAuthorityPlanner(
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, fixture.LabRoot)),
            new PermutingContentResolver(
                new SkyrimAssetContentResolver(policy, fixture.LabRoot)),
            new ExternalHeadPartPhysicsBindingResolver(
                policy, fixture.LabRoot));
        ExternalHeadPartDependencyDiscoveryResult permuted =
            await service.DiscoverAsync(request, cancellationToken);
        Require(permuted.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                permuted.Descriptor is not null &&
                permuted.Descriptor.Assets.Select(item => item.Path)
                    .SequenceEqual(expectedOrder) &&
                permuted.Descriptor.DescriptorId == expected.DescriptorId,
            "Permuted planner/content enumeration changed deterministic asset order or ID: " +
            FormatDiagnostics(permuted.Diagnostics));
    }

    private static async ValueTask AssertAuthorityClosureRefusedAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        CancellationToken cancellationToken)
    {
        AssetPath extraPath = new(
            "meshes/actors/character/character assets/hair/unrequested-extra.nif");
        var policy = new KOnlyWorkspacePolicy(
            fixture.LabRoot, ProtectedRoot);
        var extraService = new ExternalHeadPartDependencyDiscoveryService(
            new ExtraAuthorityPlanner(
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, fixture.LabRoot),
                extraPath),
            new SkyrimAssetContentResolver(policy, fixture.LabRoot),
            new ExternalHeadPartPhysicsBindingResolver(
                policy, fixture.LabRoot));
        ExternalHeadPartDependencyDiscoveryResult extra =
            await extraService.DiscoverAsync(request, cancellationToken);
        Require(extra.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(extra, ExternalHeadPartDiagnosticCodes.AssetDrift),
            "An unrequested planner authority was admitted: " +
            FormatDiagnostics(extra.Diagnostics));

        var metadataService = new ExternalHeadPartDependencyDiscoveryService(
            new SkyrimAssetAuthorityPlanner(
                new BethesdaAssetIndexer(), policy, fixture.LabRoot),
            new MetadataTamperingContentResolver(
                new SkyrimAssetContentResolver(policy, fixture.LabRoot)),
            new ExternalHeadPartPhysicsBindingResolver(
                policy, fixture.LabRoot));
        ExternalHeadPartDependencyDiscoveryResult metadata =
            await metadataService.DiscoverAsync(request, cancellationToken);
        Require(metadata.Status ==
                ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(metadata, ExternalHeadPartDiagnosticCodes.AssetDrift),
            "Content provider identity drift was admitted: " +
            FormatDiagnostics(metadata.Diagnostics));
    }

    private static async ValueTask AssertVanillaMemberTask3CharacterizationAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        FormReference rootForm,
        FormReference childForm,
        SkyrimFaceRecordProvider provider,
        CancellationToken cancellationToken)
    {
        SkyrimFaceHeadPartGraphRoute child = request.RecordRoute.HeadPartGraph
            .Single(item => item.OriginForm == childForm);
        var vanillaPlugin = new PluginName("Skyrim.esm");
        var vanilla = child with
        {
            WinningPlugin = vanillaPlugin,
            WinningForm = new FormReference(vanillaPlugin, childForm.FormId),
            RequiredOutputMaster = vanillaPlugin,
            WinningPluginSha256 = Hash("vanilla-provider"),
            WinningPluginByteLength = 16
        };
        long providerLength = new FileInfo(provider.Path.Value).Length;
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(FindRepositoryRoot()), ProtectedRoot);
        var service = new ExternalHeadPartPhysicsBindingResolver(
            policy,
            new WorkspacePath(FindRepositoryRoot()));
        ExternalHeadPartPhysicsBindingResult result =
            await service.ResolveAsync(
                new ExternalHeadPartPhysicsBindingRequest(
                    request.DataRoot,
                    request.PluginOrder,
                    new ExternalHeadPartProviderIdentity(
                        provider.Plugin,
                        provider.Sha256,
                        providerLength,
                        ExternalHeadPartRedistributionMode.ExternalProviderRequired),
                    [
                        new ExternalHeadPartRecordDependency(
                            rootForm,
                            request.RecordRoute.HeadPartGraph[0].RequiredOutputMaster,
                            rootForm,
                            provider.Plugin,
                            provider.Sha256,
                            providerLength,
                            request.RecordRoute.HeadPartGraph[0].WinningRecordSha256,
                            "root",
                            NpcHeadPartType.Hair,
                            NpcHeadPartType.Hair,
                            null,
                            [],
                            [childForm],
                            null,
                            0,
                            0,
                            NpcSex.Male,
                            null),
                        new ExternalHeadPartRecordDependency(
                            vanilla.OriginForm,
                            vanilla.RequiredOutputMaster,
                            vanilla.WinningForm,
                            vanilla.WinningPlugin,
                            vanilla.WinningPluginSha256,
                            vanilla.WinningPluginByteLength,
                            vanilla.WinningRecordSha256,
                            vanilla.EditorId,
                            vanilla.DeclaredType,
                            vanilla.EffectiveType,
                            vanilla.ModelNif,
                            vanilla.TriRoutes,
                            vanilla.HnamEdges,
                            vanilla.Parent,
                            vanilla.Depth,
                            vanilla.RouteOrder,
                            vanilla.AppliesToSex,
                            vanilla.ValidRace)
                    ]),
                cancellationToken);
        Require(!result.Accepted,
            "CHARACTERIZATION RED: Task3 unexpectedly accepted a vanilla " +
            "member in the external provider binding envelope.");
    }

    private static async ValueTask AssertReparseProviderRefusedAsync(
        ExternalHeadPartDependencyDiscoveryService service,
        ExternalHeadPartDependencyDiscoveryRequest request,
        SkyrimFaceRecordProvider provider,
        Preview254ExternalSmpPhysicsFixture fixture,
        CancellationToken cancellationToken)
    {
        string target = Path.Combine(
            fixture.ScratchRoot.Value, "provider-reparse-target");
        string link = Path.Combine(
            fixture.DataRoot.Value, "provider-reparse-link");
        Directory.CreateDirectory(target);
        File.Copy(provider.Path.Value,
            Path.Combine(target, provider.Plugin.Value));
        bool created = false;
        try
        {
            created = PhysicalReparseFixture.TryCreateDirectoryLink(
                link, target, fixture.DataRoot.Value);
            if (!created)
                return;
            SkyrimFaceRecordProvider linkedProvider = provider with
            {
                Path = new WorkspacePath(Path.Combine(
                    link, provider.Plugin.Value))
            };
            SkyrimFaceRecordRoute linkedRoute = request.RecordRoute with
            {
                Race = request.RecordRoute.Race with
                {
                    Provider = linkedProvider
                },
                HeadParts = request.RecordRoute.HeadParts
                    .Select(item => item with { Provider = linkedProvider })
                    .ToImmutableArray()
            };
            ExternalHeadPartDependencyDiscoveryResult result =
                await service.DiscoverAsync(
                    request with { RecordRoute = linkedRoute },
                    cancellationToken);
            Require(result.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused,
                "A provider under a reparse directory was admitted: " +
                FormatDiagnostics(result.Diagnostics));
        }
        finally
        {
            if (created && Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    private static async ValueTask AssertRealArchiveClosureAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        Preview254ExternalSmpPhysicsFixture fixture,
        SkyrimFaceRecordProvider provider,
        WorkspacePath labRoot,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.Combine(
            fixture.ScratchRoot.Value, "archive-source");
        string mutatedSourcePath = Path.Combine(
            fixture.ScratchRoot.Value, "archive-source-mutated");
        string archivePath = Path.Combine(
            fixture.DataRoot.Value, "OrchidAdornmentAssets.bsa");
        string mutatedArchivePath = Path.Combine(
            fixture.ScratchRoot.Value, "OrchidAdornmentAssets.mutated.bsa");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(mutatedSourcePath);
        AssetPath[] members =
        [
            fixture.Tri,
            fixture.DiffuseTexture
        ];
        foreach (AssetPath member in members)
        {
            string relative = member.Value.Replace(
                '/', Path.DirectorySeparatorChar);
            string source = Path.Combine(sourcePath, relative);
            string mutated = Path.Combine(mutatedSourcePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(mutated)!);
            byte[] bytes = File.ReadAllBytes(Physical(fixture.DataRoot, member));
            File.WriteAllBytes(source, bytes);
            byte[] mutatedBytes = bytes.ToArray();
            mutatedBytes[0] ^= 0x01;
            File.WriteAllBytes(mutated, mutatedBytes);
        }

        var policy = new KOnlyWorkspacePolicy(labRoot, ProtectedRoot);
        var bsaService = new BethesdaSkyrimBsaService(policy, labRoot);
        SkyrimBsaBuildResult built = await bsaService.BuildAsync(
                new SkyrimBsaBuildRequest(
                    new WorkspacePath(sourcePath),
                    new WorkspacePath(archivePath),
                    members.ToImmutableArray()),
            cancellationToken);
        Require(built.Written && built.Artifact is not null,
            "The real BSA fixture could not be built: " +
            FormatDiagnostics(built.Diagnostics));

        var looseBackups = members.ToDictionary(
            item => item.Value,
            item => File.ReadAllBytes(Physical(fixture.DataRoot, item)),
            StringComparer.Ordinal);
        foreach (AssetPath member in members)
            File.Delete(Physical(fixture.DataRoot, member));
        try
        {
            var ordinaryService = new ExternalHeadPartDependencyDiscoveryService(
                new SkyrimAssetAuthorityPlanner(
                    new BethesdaAssetIndexer(), policy, labRoot),
                new SkyrimAssetContentResolver(policy, labRoot),
                new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));
            ExternalHeadPartDependencyDiscoveryResult archived =
                await ordinaryService.DiscoverAsync(
                    request, cancellationToken);
            Require(archived.Status ==
            ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
            archived.Descriptor is not null &&
            archived.Descriptor.Assets
                        .Count(item => item.ArchiveMember is not null) == 2,
                "The real BSA/member closure was not independently represented: " +
                FormatDiagnostics(archived.Diagnostics));

            string missingSourcePath = Path.Combine(
                fixture.ScratchRoot.Value, "archive-source-missing");
            string missingArchivePath = Path.Combine(
                fixture.DataRoot.Value, "OrchidAdornmentAssets.missing.bsa");
            Directory.CreateDirectory(missingSourcePath);
            string diffuseRelative = fixture.DiffuseTexture.Value.Replace(
                '/', Path.DirectorySeparatorChar);
            string missingDiffuse = Path.Combine(missingSourcePath, diffuseRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(missingDiffuse)!);
            byte[] diffuseBytes = looseBackups[fixture.DiffuseTexture.Value];
            File.WriteAllBytes(missingDiffuse, diffuseBytes);
            SkyrimBsaBuildResult missingBuild = await bsaService.BuildAsync(
                new SkyrimBsaBuildRequest(
                    new WorkspacePath(missingSourcePath),
                    new WorkspacePath(missingArchivePath),
                    [fixture.DiffuseTexture]),
                cancellationToken);
            Require(missingBuild.Written && missingBuild.Artifact is not null,
                "The real missing-member BSA fixture could not be built: " +
                FormatDiagnostics(missingBuild.Diagnostics));
            var plannedBytes = new Dictionary<string, byte[]>(
                StringComparer.Ordinal)
            {
                [fixture.ModelNif.Value] = File.ReadAllBytes(
                    Physical(fixture.DataRoot, fixture.ModelNif)),
                [fixture.Tri.Value] = looseBackups[fixture.Tri.Value],
                [fixture.DiffuseTexture.Value] = diffuseBytes
            };
            var missingService = new ExternalHeadPartDependencyDiscoveryService(
                new RealArchiveAuthorityPlanner(
                    new WorkspacePath(missingArchivePath),
                    fixture.ModelNif,
                    plannedBytes),
                new RealArchiveContentResolver(plannedBytes),
                new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));
            ExternalHeadPartDependencyDiscoveryResult missingMember =
                await missingService.DiscoverAsync(request, cancellationToken);
            Require(missingMember.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                HasDiagnostic(missingMember,
                    ExternalHeadPartDiagnosticCodes.AssetMissing),
                "A real BSA missing a claimed member was admitted or did not " +
                "reach production archive-member verification: " +
                FormatDiagnostics(missingMember.Diagnostics));
            if (File.Exists(missingArchivePath))
                File.Delete(missingArchivePath);
            if (Directory.Exists(missingSourcePath))
                Directory.Delete(missingSourcePath, recursive: true);

            SkyrimBsaBuildResult mutatedBuild = await bsaService.BuildAsync(
                new SkyrimBsaBuildRequest(
                    new WorkspacePath(mutatedSourcePath),
                    new WorkspacePath(mutatedArchivePath),
                    members.ToImmutableArray()),
                cancellationToken);
            Require(mutatedBuild.Written,
                "The mutated real BSA fixture could not be built: " +
                FormatDiagnostics(mutatedBuild.Diagnostics));
            bool archiveMutated = false;
            var mutatingResolver = new MutatingContentResolver(
                new SkyrimAssetContentResolver(policy, labRoot),
                (() =>
                {
                    if (archiveMutated)
                        return;
                    archiveMutated = true;
                    File.Delete(archivePath);
                    File.Move(mutatedArchivePath, archivePath);
                }));
            var mutatingService =
                new ExternalHeadPartDependencyDiscoveryService(
                    new SkyrimAssetAuthorityPlanner(
                        new BethesdaAssetIndexer(), policy, labRoot),
                    mutatingResolver,
                    new ExternalHeadPartPhysicsBindingResolver(
                        policy, labRoot));
            ExternalHeadPartDependencyDiscoveryResult mutated =
                await mutatingService.DiscoverAsync(
                    request, cancellationToken);
            Require(mutated.Status ==
                    ExternalHeadPartDependencyDiscoveryStatus.Refused &&
                    HasDiagnostic(mutated,
                        ExternalHeadPartDiagnosticCodes.AssetDrift),
                "A real BSA/member mutation after content resolution was admitted: " +
                FormatDiagnostics(mutated.Diagnostics));
        }
        finally
        {
            foreach ((string relative, byte[] bytes) in looseBackups)
                File.WriteAllBytes(
                    Path.Combine(
                        fixture.DataRoot.Value,
                        relative.Replace('/', Path.DirectorySeparatorChar)),
                    bytes);
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            if (File.Exists(mutatedArchivePath))
                File.Delete(mutatedArchivePath);
            string missingArchivePath = Path.Combine(
                fixture.DataRoot.Value, "OrchidAdornmentAssets.missing.bsa");
            if (File.Exists(missingArchivePath))
                File.Delete(missingArchivePath);
            string missingSourcePath = Path.Combine(
                fixture.ScratchRoot.Value, "archive-source-missing");
            if (Directory.Exists(missingSourcePath))
                Directory.Delete(missingSourcePath, recursive: true);
            if (Directory.Exists(sourcePath))
                Directory.Delete(sourcePath, recursive: true);
            if (Directory.Exists(mutatedSourcePath))
                Directory.Delete(mutatedSourcePath, recursive: true);
        }
    }

    private static readonly ImmutableArray<Preview257ValidRaceMatrixCase>
        Preview257ValidRaceMatrix =
        [
            new("HeadPartsHumansandVampires", "Breton", 0x900, true),
            new("HeadPartsHumansandVampires", "Imperial", 0x901, true),
            new("HeadPartsHumansandVampires", "Nord", 0x902, true),
            new("HeadPartsHumansandVampires", "Redguard", 0x903, true),
            new("HeadPartsHumansandVampires", "Altmer", 0x906, true),
            new("HeadPartsHumansandVampires", "Bosmer", 0x907, true),
            new("HeadPartsHumansandVampires", "Dunmer", 0x908, true),
            new("HeadPartsHumansandVampires", "Orc", 0x909, true),
            new("HeadPartsHumansandVampires", "Argonian", 0x904, false),
            new("HeadPartsHumansandVampires", "Khajiit", 0x905, false),
            new("HeadPartsAllRacesMinusBeast", "Breton", 0x900, true),
            new("HeadPartsAllRacesMinusBeast", "Imperial", 0x901, true),
            new("HeadPartsAllRacesMinusBeast", "Nord", 0x902, true),
            new("HeadPartsAllRacesMinusBeast", "Redguard", 0x903, true),
            new("HeadPartsAllRacesMinusBeast", "Altmer", 0x906, true),
            new("HeadPartsAllRacesMinusBeast", "Bosmer", 0x907, true),
            new("HeadPartsAllRacesMinusBeast", "Dunmer", 0x908, true),
            new("HeadPartsAllRacesMinusBeast", "Orc", 0x909, true),
            new("HeadPartsAllRacesMinusBeast", "Argonian", 0x904, false),
            new("HeadPartsAllRacesMinusBeast", "Khajiit", 0x905, false),
            new("HeadPartsAllRaces", "Breton", 0x900, true),
            new("HeadPartsAllRaces", "Imperial", 0x901, true),
            new("HeadPartsAllRaces", "Nord", 0x902, true),
            new("HeadPartsAllRaces", "Redguard", 0x903, true),
            new("HeadPartsAllRaces", "Altmer", 0x906, true),
            new("HeadPartsAllRaces", "Bosmer", 0x907, true),
            new("HeadPartsAllRaces", "Dunmer", 0x908, true),
            new("HeadPartsAllRaces", "Orc", 0x909, true),
            new("HeadPartsAllRaces", "Argonian", 0x904, true),
            new("HeadPartsAllRaces", "Khajiit", 0x905, true)
        ];

    private static readonly ImmutableArray<Preview257HairMatrixCase>
        Preview257HairMatrix =
        [
            new(
                "HeadPartsHumansandVampires",
                "vanilla-style Hair root (zero HNAM/ExtraParts)",
                0x920,
                null,
                0x910,
                "Preview257HumansAndVampiresVanillaHair",
                null),
            new(
                "HeadPartsHumansandVampires",
                "SMP-style Hair root (HNAM/ExtraParts child)",
                0x921,
                0x924,
                0x910,
                "Preview257HumansAndVampiresSmpHair",
                "Preview257HumansAndVampiresSmpChildHair"),
            new(
                "HeadPartsAllRacesMinusBeast",
                "vanilla-style Hair root (zero HNAM/ExtraParts)",
                0x922,
                null,
                0x911,
                "Preview257AllRacesMinusBeastVanillaHair",
                null),
            new(
                "HeadPartsAllRacesMinusBeast",
                "SMP-style Hair root (HNAM/ExtraParts child)",
                0x923,
                0x925,
                0x911,
                "Preview257AllRacesMinusBeastSmpHair",
                "Preview257AllRacesMinusBeastSmpChildHair"),
            new(
                "HeadPartsAllRaces",
                "vanilla-style Hair root (zero HNAM/ExtraParts)",
                0x926,
                null,
                0x915,
                "Preview257AllRacesVanillaHair",
                null),
            new(
                "HeadPartsAllRaces",
                "SMP-style Hair root (HNAM/ExtraParts child)",
                0x927,
                0x928,
                0x915,
                "Preview257AllRacesSmpHair",
                "Preview257AllRacesSmpChildHair")
        ];

    private static async ValueTask AssertValidRaceFormListMatrixAsync(
        WorkspacePath labRoot,
        WorkspacePath scratchRoot,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        Preview257ValidRaceFixture fixture =
            await CreateValidRaceFixtureAsync(
                labRoot,
                new WorkspacePath(Path.Combine(
                    scratchRoot.Value, "preview257-valid-race-flst")),
                cancellationToken);

        foreach (Preview257ValidRaceMatrixCase raceCase in
                 Preview257ValidRaceMatrix)
        {
            FormReference race = fixture.Races.Single(item =>
                item.FormId.Value == raceCase.RaceFormId);
            foreach (Preview257HairMatrixCase hairCase in
                     Preview257HairMatrix.Where(item =>
                         item.ListSemantics == raceCase.ListSemantics))
            {
                Preview257HairRecordFixture hair = fixture.HairRecords.Single(
                    item => item.Root.FormId.Value == hairCase.RootFormId);
                FormReference expectedList = hair.ValidRaceList;

                SkyrimFaceRecordRouteResult routeResult =
                    await ResolveValidRaceRouteAsync(
                        labRoot, fixture, race, hair.Root,
                        cancellationToken);
                if (!routeResult.Accepted || routeResult.Route is null)
                {
                    failures.Add(
                        $"route boundary rejected {raceCase.ListSemantics}/" +
                        $"{hairCase.Shape}/{raceCase.RaceName}: " +
                        FormatDiagnostics(routeResult.Diagnostics));
                    continue;
                }

                SkyrimFaceRecordRoute route = routeResult.Route;
                if (route.Race.Reference != race)
                {
                    failures.Add(
                        $"route boundary returned race {route.Race.Reference} " +
                        $"instead of the requested {race} for " +
                        $"{raceCase.ListSemantics}/{hairCase.Shape}/" +
                        $"{raceCase.RaceName}.");
                    continue;
                }
                int expectedMemberCount = hair.Child is null ? 1 : 2;
                if (route.HeadPartGraph.Length != expectedMemberCount ||
                    route.HeadPartGraph[0].OriginForm != hair.Root ||
                    !route.HeadPartGraph[0].HnamEdges.SequenceEqual(
                        hair.Child is { } child ? [child] : []) ||
                    route.HeadPartGraph.Skip(1).Any(item =>
                        item.OriginForm != hair.Child ||
                        item.Parent != hair.Root))
                {
                    failures.Add(
                        $"real Mutagen HNAM route shape was not preserved for " +
                        $"{raceCase.ListSemantics}/{hairCase.Shape}/" +
                        $"{raceCase.RaceName}: " +
                        FormatDiagnostics(routeResult.Diagnostics));
                    continue;
                }
                if (route.HeadPartGraph.Any(item =>
                        item.ValidRace != expectedList ||
                        item.ValidRaceList?.Reference != expectedList))
                {
                    failures.Add(
                        $"real Mutagen route lost the {expectedList} FLST pointer " +
                        $"for {raceCase.ListSemantics}/{hairCase.Shape}/" +
                        $"{raceCase.RaceName}.");
                }

                ExternalHeadPartDependencyDiscoveryResult discovery =
                    await DiscoverValidRaceRouteAsync(
                        labRoot, fixture, route, cancellationToken);
                if (raceCase.ExpectedAccepted)
                {
                    if (discovery.Status !=
                        ExternalHeadPartDependencyDiscoveryStatus.Accepted ||
                        discovery.Descriptor is not { } descriptor ||
                        HasErrors(discovery.Diagnostics))
                    {
                        failures.Add(
                            $"included {raceCase.RaceName} was not admitted for " +
                            $"{raceCase.ListSemantics}/{hairCase.Shape}: " +
                            FormatDiagnostics(discovery.Diagnostics));
                        continue;
                    }
                    bool descriptorGraphMatches =
                        descriptor.Members.Length == expectedMemberCount &&
                        descriptor.Members.Select(item => item.OriginForm)
                            .SequenceEqual(route.HeadPartGraph.Select(
                                item => item.OriginForm)) &&
                        descriptor.Members.Zip(route.HeadPartGraph).All(pair =>
                            pair.First.OriginForm == pair.Second.OriginForm &&
                            pair.First.HnamEdges.SequenceEqual(
                                pair.Second.HnamEdges) &&
                            pair.First.Parent == pair.Second.Parent &&
                            pair.First.Depth == pair.Second.Depth &&
                            pair.First.RouteOrder == pair.Second.RouteOrder);
                    if (!descriptorGraphMatches ||
                        descriptor.Members.Any(item =>
                            item.ValidRace != expectedList))
                    {
                        failures.Add(
                            $"admitted descriptor did not preserve the " +
                            $"{expectedList} FLST pointer or graph identity/" +
                            $"relationship shape for " +
                            $"{raceCase.ListSemantics}/{hairCase.Shape}/" +
                            $"{raceCase.RaceName}.");
                    }
                }
                else
                {
                    if (discovery.Status !=
                        ExternalHeadPartDependencyDiscoveryStatus.Refused ||
                        !HasDiagnostic(
                            discovery,
                            ExternalHeadPartDiagnosticCodes.RecordDrift))
                    {
                        failures.Add(
                            $"excluded {raceCase.RaceName} was not refused with " +
                            $"external-headpart-record-drift for " +
                            $"{raceCase.ListSemantics}/{hairCase.Shape}: " +
                            FormatDiagnostics(discovery.Diagnostics));
                    }
                    foreach (SkyrimFaceHeadPartGraphRoute member in
                             route.HeadPartGraph)
                    {
                        string label = member.Parent is null
                            ? "selected Hair root"
                            : "HNAM member";
                        bool specific = discovery.Diagnostics.Any(item =>
                            item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift &&
                            item.Message.Contains(label, StringComparison.Ordinal) &&
                            item.Message.Contains(member.OriginForm.ToString(),
                                StringComparison.Ordinal) &&
                            item.Message.Contains(expectedList.ToString(),
                                StringComparison.Ordinal) &&
                            item.Message.Contains(race.ToString(),
                                StringComparison.Ordinal));
                        if (!specific)
                        {
                            failures.Add(
                                $"excluded {raceCase.RaceName} lacked " +
                                $"{label}/FLST/race-specific drift evidence for " +
                                $"{raceCase.ListSemantics}/{hairCase.Shape}: " +
                                FormatDiagnostics(discovery.Diagnostics));
                        }
                    }
                }
            }
        }

        // Keep a chain-specific attribution check: an admitted root must not
        // hide a child-only ValidRaces exclusion behind a generic root error.
        Preview257ValidRaceMatrixCase admittedCase =
            Preview257ValidRaceMatrix.First(item =>
                item.ListSemantics == "HeadPartsHumansandVampires" &&
                item.RaceName == "Breton");
        Preview257HairRecordFixture admittedHair = fixture.HairRecords.Single(
            item => item.Root.FormId.Value == 0x921);
        FormReference admittedRace = fixture.Races.Single(item =>
            item.FormId.Value == admittedCase.RaceFormId);
        SkyrimFaceRecordRouteResult admittedRouteResult =
            await ResolveValidRaceRouteAsync(
                labRoot, fixture, admittedRace, admittedHair.Root,
                cancellationToken);
        if (!admittedRouteResult.Accepted || admittedRouteResult.Route is null)
        {
            failures.Add(
                "the chain-specific child-only ValidRaces attribution " +
                "could not start because route resolution was refused: " +
                FormatDiagnostics(admittedRouteResult.Diagnostics));
        }
        else
        {
            SkyrimFaceRecordProvider listProvider = new(
                fixture.ProviderPlugin,
                fixture.ProviderPath,
                fixture.ProviderSha256);
            SkyrimFaceFormListRoute excludingProjection = new(
                fixture.ExcludedList,
                listProvider,
                [fixture.OtherRace],
                false);
            SkyrimFaceRecordRoute childOnlyExclusionRoute =
                admittedRouteResult.Route with
                {
                    HeadPartGraph = admittedRouteResult.Route.HeadPartGraph
                        .Select(item => item.Parent is null
                            ? item
                            : item with
                            {
                                ValidRace = fixture.ExcludedList,
                                ValidRaceList = excludingProjection
                            })
                        .ToImmutableArray()
                };
            ExternalHeadPartDependencyDiscoveryResult childOnlyExclusion =
                await DiscoverValidRaceRouteAsync(
                    labRoot, fixture, childOnlyExclusionRoute,
                    cancellationToken);
            string childOnlyDiagnostics = FormatDiagnostics(
                childOnlyExclusion.Diagnostics);
            SkyrimFaceHeadPartGraphRoute child =
                childOnlyExclusionRoute.HeadPartGraph.Single(item =>
                    item.Parent is not null);
            if (childOnlyExclusion.Status !=
                    ExternalHeadPartDependencyDiscoveryStatus.Refused ||
                !childOnlyExclusion.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift &&
                    item.Message.Contains("HNAM member",
                        StringComparison.Ordinal) &&
                    item.Message.Contains(child.OriginForm.ToString(),
                        StringComparison.Ordinal) &&
                    item.Message.Contains(fixture.ExcludedList.ToString(),
                        StringComparison.Ordinal) &&
                    item.Message.Contains(admittedRace.ToString(),
                        StringComparison.Ordinal)))
            {
                failures.Add(
                    "a child-only ValidRaces exclusion was not attributed " +
                    "to the HNAM member with FLST and race evidence: " +
                    childOnlyDiagnostics);
            }
        }

        // Preserve malformed-authority coverage from Preview.257.
        Preview257HairRecordFixture malformedHair = fixture.HairRecords.Single(
            item => item.Root.FormId.Value == 0x921);
        FormReference malformedRace = fixture.Races[0];
        SkyrimFaceRecordRouteResult malformedRouteResult =
            await ResolveValidRaceRouteAsync(
                labRoot, fixture, malformedRace, malformedHair.Root,
                cancellationToken);
        if (!malformedRouteResult.Accepted || malformedRouteResult.Route is null)
        {
            failures.Add(
                "malformed ValidRaces authority coverage could not start " +
                "because route resolution was refused: " +
                FormatDiagnostics(malformedRouteResult.Diagnostics));
        }
        else
        {
            SkyrimFaceRecordProvider listProvider = new(
                fixture.ProviderPlugin,
                fixture.ProviderPath,
                fixture.ProviderSha256);
            SkyrimFaceFormListRoute Projection(
                FormReference reference,
                ImmutableArray<FormReference> items,
                bool isDeleted) => new(
                    reference,
                    listProvider,
                    items,
                    isDeleted);
            foreach ((string label, FormReference pointer,
                      SkyrimFaceFormListRoute? projection,
                      string expectedCode) in new[]
                     {
                         (
                             "excluding routed race",
                             fixture.ExcludedList,
                             (SkyrimFaceFormListRoute?)Projection(
                                 fixture.ExcludedList, [fixture.OtherRace], false),
                             ExternalHeadPartDiagnosticCodes.RecordDrift),
                         (
                             "missing list",
                             fixture.MissingList,
                             (SkyrimFaceFormListRoute?)null,
                             ExternalHeadPartDiagnosticCodes.RecordUnresolved),
                         (
                             "deleted list",
                             fixture.DeletedList,
                             (SkyrimFaceFormListRoute?)Projection(
                                 fixture.DeletedList, [malformedRace], true),
                             ExternalHeadPartDiagnosticCodes.RecordUnresolved),
                         (
                             "wrong-record-type authority",
                             fixture.OtherRace,
                             (SkyrimFaceFormListRoute?)null,
                             ExternalHeadPartDiagnosticCodes.RecordUnresolved)
                     })
            {
                SkyrimFaceRecordRoute malformedRoute =
                    malformedRouteResult.Route with
                    {
                        HeadPartGraph = malformedRouteResult.Route.HeadPartGraph
                            .Select(item => item with
                            {
                                ValidRace = pointer,
                                ValidRaceList = projection
                            })
                            .ToImmutableArray()
                    };
                ExternalHeadPartDependencyDiscoveryResult malformed =
                    await DiscoverValidRaceRouteAsync(
                        labRoot, fixture, malformedRoute,
                        cancellationToken);
                string malformedDiagnostics = FormatDiagnostics(
                    malformed.Diagnostics);
                if (malformed.Status !=
                        ExternalHeadPartDependencyDiscoveryStatus.Refused ||
                    !HasDiagnostic(malformed, expectedCode) ||
                    !malformedDiagnostics.Contains(pointer.ToString(),
                        StringComparison.Ordinal) ||
                    !malformedDiagnostics.Contains(malformedRace.ToString(),
                        StringComparison.Ordinal))
                {
                    failures.Add(
                        $"malformed ValidRaces authority ({label}) was not " +
                        $"refused with {expectedCode} and pointer/race evidence: " +
                        malformedDiagnostics);
                }
            }

            SkyrimFaceHeadPartGraphRoute root =
                malformedRouteResult.Route.HeadPartGraph[0] with
                {
                    HnamEdges = [],
                    ValidRace = fixture.MissingList,
                    ValidRaceList = null
                };
            ExternalHeadPartDependencyDiscoveryResult invalidRoot =
                await DiscoverValidRaceRouteAsync(
                    labRoot,
                    fixture,
                    malformedRouteResult.Route with
                    {
                        HeadPartGraph = [root]
                    },
                    cancellationToken);
            string rootDiagnostics = FormatDiagnostics(invalidRoot.Diagnostics);
            if (invalidRoot.Status !=
                    ExternalHeadPartDependencyDiscoveryStatus.Refused ||
                !rootDiagnostics.Contains(
                    "selected Hair root", StringComparison.Ordinal) ||
                !rootDiagnostics.Contains(
                    root.OriginForm.ToString(), StringComparison.Ordinal) ||
                !rootDiagnostics.Contains(
                    fixture.MissingList.ToString(), StringComparison.Ordinal) ||
                !rootDiagnostics.Contains(
                    malformedRace.ToString(), StringComparison.Ordinal) ||
                rootDiagnostics.Contains("HNAM member", StringComparison.Ordinal))
            {
                failures.Add(
                    "a root with no HNAM children was not described as " +
                    "the selected Hair root with pointer/race evidence: " +
                    rootDiagnostics);
            }
        }

        Require(failures.Count == 0,
            "Preview.257 valid-race FLST regression matrix failed:\n" +
            string.Join("\n", failures.Select(item => "- " + item)));
    }

    private static async ValueTask<SkyrimFaceRecordRouteResult>
        ResolveValidRaceRouteAsync(
            WorkspacePath labRoot,
            Preview257ValidRaceFixture fixture,
            FormReference race,
            FormReference rootForm,
            CancellationToken cancellationToken)
    {
        var policy = new KOnlyWorkspacePolicy(labRoot, ProtectedRoot);
        var resolver = new BethesdaSkyrimFaceRecordRouteResolver(
            policy, labRoot);
        var request = new SkyrimFaceRecordRouteRequest(
            GameEdition.SkyrimSpecialEdition,
            race,
            NpcSex.Male,
            [new SkyrimFaceRecordHeadPartSelection(rootForm, [])],
            [new SkyrimFaceRecordPluginAuthority(
                fixture.ProviderPlugin,
                fixture.ProviderPath,
                fixture.ProviderSha256)]);
        return await resolver.ResolveAsync(request, cancellationToken);
    }

    private static async ValueTask<ExternalHeadPartDependencyDiscoveryResult>
        DiscoverValidRaceRouteAsync(
            WorkspacePath labRoot,
            Preview257ValidRaceFixture fixture,
            SkyrimFaceRecordRoute route,
            CancellationToken cancellationToken)
    {
        var policy = new KOnlyWorkspacePolicy(labRoot, ProtectedRoot);
        var service = new ExternalHeadPartDependencyDiscoveryService(
            new SkyrimAssetAuthorityPlanner(
                new BethesdaAssetIndexer(), policy, labRoot),
            new SkyrimAssetContentResolver(policy, labRoot),
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));
        var request = new ExternalHeadPartDependencyDiscoveryRequest(
            fixture.DataRoot,
            [fixture.ProviderPlugin],
            route,
            NpcSex.Male);
        return await service.DiscoverAsync(request, cancellationToken);
    }

    private static ValueTask<Preview257ValidRaceFixture>
        CreateValidRaceFixtureAsync(
            WorkspacePath labRoot,
            WorkspacePath scratchRoot,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(scratchRoot.Value);
        var dataRoot = new WorkspacePath(
            Path.Combine(scratchRoot.Value, "Data"));
        Directory.CreateDirectory(dataRoot.Value);
        var providerPlugin = new PluginName("Preview257ValidRaces.esp");
        ImmutableArray<FormReference> races =
        [
            new(providerPlugin, new FormId(0x900)),
            new(providerPlugin, new FormId(0x901)),
            new(providerPlugin, new FormId(0x902)),
            new(providerPlugin, new FormId(0x903)),
            new(providerPlugin, new FormId(0x904)),
            new(providerPlugin, new FormId(0x905)),
            new(providerPlugin, new FormId(0x906)),
            new(providerPlugin, new FormId(0x907)),
            new(providerPlugin, new FormId(0x908)),
            new(providerPlugin, new FormId(0x909))
        ];
        var vampireRace = new FormReference(
            providerPlugin, new FormId(0x90A));
        var humansAndVampires = new FormReference(
            providerPlugin, new FormId(0x910));
        var allRacesMinusBeast = new FormReference(
            providerPlugin, new FormId(0x911));
        var allRaces = new FormReference(
            providerPlugin, new FormId(0x915));
        var excludedList = new FormReference(
            providerPlugin, new FormId(0x912));
        var deletedList = new FormReference(
            providerPlugin, new FormId(0x913));
        var missingList = new FormReference(
            providerPlugin, new FormId(0x914));
        var model = new AssetPath(
            "meshes/p257/hair.nif");
        var tri = new AssetPath(
            "meshes/p257/hair.tri");
        var diffuse = new AssetPath(
            "textures/p257/hair.dds");
        var physics = new AssetPath(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/p257.xml");
        var collider = new AssetPath(
            "meshes/p257/collider.nif");
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            dataRoot,
            model,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                physics.Value,
                tri.Value,
                textureSlots: [diffuse.Value]),
            labRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            dataRoot, tri, Encoding.UTF8.GetBytes("preview257-tri"), labRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            dataRoot, diffuse, Encoding.UTF8.GetBytes("preview257-diffuse"), labRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            dataRoot,
            collider,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null,
                tri.Value),
            labRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            dataRoot,
            physics,
            Encoding.UTF8.GetBytes(
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(collider)),
            labRoot);

        ImmutableArray<Preview257HairRecordFixture> hairRecords =
            Preview257HairMatrix.Select(item =>
                new Preview257HairRecordFixture(
                    item.ListSemantics,
                    item.Shape,
                    new FormReference(
                        providerPlugin, new FormId(item.RootFormId)),
                    item.ChildFormId is { } childFormId
                        ? new FormReference(
                            providerPlugin, new FormId(childFormId))
                        : null,
                    new FormReference(
                        providerPlugin, new FormId(item.ListFormId)),
                    item.RootEditorId,
                    item.ChildEditorId))
                .ToImmutableArray();
        string providerPath = Path.Combine(dataRoot.Value, providerPlugin.Value);
        WriteValidRacePlugin(
            providerPath,
            providerPlugin,
            races,
            vampireRace,
            humansAndVampires,
            allRacesMinusBeast,
            allRaces,
            excludedList,
            deletedList,
            hairRecords,
            model,
            tri);
        byte[] providerBytes = File.ReadAllBytes(providerPath);
        return ValueTask.FromResult(new Preview257ValidRaceFixture(
            dataRoot,
            new WorkspacePath(providerPath),
            providerPlugin,
            Hash(providerBytes),
            races,
            races[1],
            humansAndVampires,
            allRacesMinusBeast,
            excludedList,
            deletedList,
            missingList,
            hairRecords));
    }

    private static void WriteValidRacePlugin(
        string path,
        PluginName providerPlugin,
        ImmutableArray<FormReference> races,
        FormReference vampireRace,
        FormReference humansAndVampires,
        FormReference allRacesMinusBeast,
        FormReference allRaces,
        FormReference excludedList,
        FormReference deletedList,
        ImmutableArray<Preview257HairRecordFixture> hairRecords,
        AssetPath model,
        AssetPath tri)
    {
        var key = ModKey.FromNameAndExtension(providerPlugin.Value);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var raceRows = new[]
        {
            (Reference: races[0], EditorId: "BretonRace"),
            (Reference: races[1], EditorId: "ImperialRace"),
            (Reference: races[2], EditorId: "NordRace"),
            (Reference: races[3], EditorId: "RedguardRace"),
            (Reference: races[4], EditorId: "ArgonianRace"),
            (Reference: races[5], EditorId: "KhajiitRace"),
            (Reference: races[6], EditorId: "HighElfRace"),
            (Reference: races[7], EditorId: "WoodElfRace"),
            (Reference: races[8], EditorId: "DarkElfRace"),
            (Reference: races[9], EditorId: "OrcRace"),
            (Reference: vampireRace, EditorId: "VampireRace")
        };
        foreach (var row in raceRows)
        {
            mod.Races.Add(new Race(
                new FormKey(key, row.Reference.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = row.EditorId,
                HeadData = new GenderedItem<HeadData?>(
                    new HeadData(), new HeadData())
            });
        }

        HeadPart CreateHeadPart(
            Preview257HairRecordFixture hair,
            bool child)
        {
            FormReference reference = child
                ? hair.Child!.Value
                : hair.Root;
            string editorId = child
                ? hair.ChildEditorId!
                : hair.RootEditorId;
            var part = new HeadPart(
                new FormKey(key, reference.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = editorId,
                Name = $"Preview.257 {hair.Shape} {editorId}",
                Flags = HeadPart.Flag.Male,
                Type = HeadPart.TypeEnum.Hair,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(model.Value)
                },
                ValidRaces = new FormLinkNullable<IFormListGetter>(
                    new FormKey(key, hair.ValidRaceList.FormId.Value))
            };
            part.Parts.Add(new Part
            {
                PartType = Part.PartTypeEnum.Tri,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                    tri.Value)
            });
            if (!child && hair.Child is { } childReference)
            {
                part.ExtraParts.Add(
                    new FormLink<IHeadPartGetter>(
                        new FormKey(key, childReference.FormId.Value)));
            }
            return part;
        }

        foreach (Preview257HairRecordFixture hair in hairRecords)
        {
            mod.HeadParts.Add(CreateHeadPart(hair, child: false));
            if (hair.Child is not null)
                mod.HeadParts.Add(CreateHeadPart(hair, child: true));
        }

        FormList CreateRaceList(
            FormReference reference,
            string editorId,
            IEnumerable<FormReference> members,
            bool isDeleted = false)
        {
            var list = new FormList(
                new FormKey(key, reference.FormId.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = editorId,
                IsDeleted = isDeleted
            };
            foreach (FormReference member in members)
            {
                list.Items.Add(new FormLink<IRaceGetter>(
                    new FormKey(key, member.FormId.Value)));
            }
            return list;
        }

        FormReference otherRace = races[1];
        mod.FormLists.Add(CreateRaceList(
            humansAndVampires,
            "HeadPartsHumansandVampires",
            [races[0], races[1], races[2], races[3],
                races[6], races[7], races[8], races[9], vampireRace]));
        mod.FormLists.Add(CreateRaceList(
            allRacesMinusBeast,
            "HeadPartsAllRacesMinusBeast",
            [races[0], races[1], races[2], races[3],
                races[6], races[7], races[8], races[9]]));
        mod.FormLists.Add(CreateRaceList(
            allRaces,
            "HeadPartsAllRaces",
            [races[0], races[1], races[2], races[3], races[4], races[5],
                races[6], races[7], races[8], races[9]]));
        mod.FormLists.Add(CreateRaceList(
            excludedList,
            "Preview257ExcludedRaces",
            [otherRace]));
        mod.FormLists.Add(CreateRaceList(
            deletedList,
            "Preview257DeletedRaces",
            [races[0]],
            isDeleted: true));

        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private sealed record Preview257ValidRaceFixture(
        WorkspacePath DataRoot,
        WorkspacePath ProviderPath,
        PluginName ProviderPlugin,
        Sha256Hash ProviderSha256,
        ImmutableArray<FormReference> Races,
        FormReference OtherRace,
        FormReference HeadPartsHumansandVampires,
        FormReference HeadPartsAllRacesMinusBeast,
        FormReference ExcludedList,
        FormReference DeletedList,
        FormReference MissingList,
        ImmutableArray<Preview257HairRecordFixture> HairRecords);

    private sealed record Preview257ValidRaceMatrixCase(
        string ListSemantics,
        string RaceName,
        uint RaceFormId,
        bool ExpectedAccepted);

    private sealed record Preview257HairMatrixCase(
        string ListSemantics,
        string Shape,
        uint RootFormId,
        uint? ChildFormId,
        uint ListFormId,
        string RootEditorId,
        string? ChildEditorId);

    private sealed record Preview257HairRecordFixture(
        string ListSemantics,
        string Shape,
        FormReference Root,
        FormReference? Child,
        FormReference ValidRaceList,
        string RootEditorId,
        string? ChildEditorId);

    private static SkyrimFaceRecordRoute CreateRoute(
        SkyrimFaceRecordProvider provider,
        FormReference rootForm,
        FormReference childForm,
        AssetPath model,
        AssetPath tri,
        AssetPath diffuse,
        long providerLength,
        WorkspacePath dataRoot)
    {
        var race = new FormReference(provider.Plugin, new FormId(0x900));
        var validRaceListReference = new FormReference(
            provider.Plugin, new FormId(0x920));
        var validRaceList = new SkyrimFaceFormListRoute(
            validRaceListReference,
            provider,
            [race],
            false);
        var root = new SkyrimFaceHeadPartGraphRoute(
            rootForm,
            provider.Plugin,
            rootForm,
            provider.Plugin,
            provider.Sha256,
            providerLength,
            Hash("root-record"),
            "OrchidRootHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            null,
            [],
            [childForm],
            null,
            0,
            true,
            false,
            0,
            NpcSex.Male,
            validRaceListReference)
        {
            ValidRaceList = validRaceList
        };
        var child = new SkyrimFaceHeadPartGraphRoute(
            childForm,
            provider.Plugin,
            childForm,
            provider.Plugin,
            provider.Sha256,
            providerLength,
            Hash("child-record"),
            "OrchidChildHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            model,
            [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, tri)],
            [],
            rootForm,
            1,
            false,
            false,
            1,
            NpcSex.Male,
            validRaceListReference)
        {
            ValidRaceList = validRaceList
        };
        SkyrimFaceHeadPartRecordRoute legacyChild =
            new(
                childForm,
                provider,
                "OrchidChildHair",
                NpcHeadPartType.Hair,
                NpcHeadPartType.Hair,
                model,
                [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, tri)],
                [],
                rootForm,
                1,
                false,
                false)
            {
                TextureSet = new SkyrimFaceTextureSetRecordRoute(
                    new FormReference(provider.Plugin, new FormId(0x910)),
                    provider,
                    [diffuse.Value])
            };
        return new SkyrimFaceRecordRoute(
            new SkyrimRaceFaceRecordRoute(
                race,
                provider,
                "OrchidRace",
                null,
                "OrchidRace",
                [],
                [],
                [],
                []),
            [rootForm],
            [legacyChild])
        {
            HeadPartGraph = [root, child]
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")) &&
                (File.Exists(Path.Combine(current.FullName, ".git")) ||
                 Directory.Exists(Path.Combine(current.FullName, ".git"))))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not find the Actorwright repository root.");
    }

    private static string Physical(
        WorkspacePath dataRoot,
        AssetPath path) =>
        Path.Combine(
            dataRoot.Value,
            path.Value.Replace('/', Path.DirectorySeparatorChar));

    private static Sha256Hash Hash(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static bool HasDiagnostic(
        ExternalHeadPartDependencyDiscoveryResult result,
        string code) =>
        result.Diagnostics.Any(item => string.Equals(
            item.Code, code, StringComparison.Ordinal));

    private static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(
            " | ",
            diagnostics.Select(item =>
                $"{item.Code}:{item.Severity}:{item.Message}"));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RealArchiveAuthorityPlanner(
        WorkspacePath archivePath,
        AssetPath modelPath,
        IReadOnlyDictionary<string, byte[]> contentByAsset) :
        ISkyrimAssetAuthorityPlanner
    {
        public ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
            SkyrimAssetAuthorityPlanRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] archiveBytes = File.ReadAllBytes(archivePath.Value);
            Sha256Hash archiveHash = Hash(archiveBytes);
            ImmutableArray<SkyrimAssetAuthority> authorities =
                request.RequiredAssets.Select(path =>
                {
                    if (!contentByAsset.TryGetValue(path.Value, out byte[]? content))
                        throw new InvalidDataException(
                            $"Missing planned content for '{path}'.");
                    bool isModel = path == modelPath;
                    return new SkyrimAssetAuthority(
                        "real-missing-bsa",
                        isModel ? AssetProviderKind.Loose : AssetProviderKind.Archive,
                        isModel
                            ? new WorkspacePath(Path.Combine(
                                request.DataRoot.Value,
                                path.Value.Replace('/', Path.DirectorySeparatorChar)))
                            : archivePath,
                        isModel ? Hash(content) : archiveHash,
                        path,
                        content.LongLength,
                        Hash(content));
                })
                    .ToImmutableArray();
            return ValueTask.FromResult(new SkyrimAssetAuthorityPlanResult(
                true, authorities, []));
        }
    }

    private sealed class RealArchiveContentResolver(
        IReadOnlyDictionary<string, byte[]> contentByAsset) :
        ISkyrimAssetContentResolver
    {
        public ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assets = ImmutableArray.CreateBuilder<ResolvedSkyrimAssetContent>(
                request.Authorities.Length);
            foreach (SkyrimAssetContentAuthority authority in request.Authorities)
            {
                if (!contentByAsset.TryGetValue(
                        authority.AssetPath.Value, out byte[]? content))
                    return ValueTask.FromResult(
                        new SkyrimAssetContentResolutionResult(false, [], [
                            new Diagnostic(
                                "external-headpart-content-missing",
                                DiagnosticSeverity.Error,
                                $"No planned bytes for '{authority.AssetPath}'.")
                        ]));
                assets.Add(new ResolvedSkyrimAssetContent(
                    authority.ProviderId,
                    authority.Kind,
                    authority.ProviderPath,
                    authority.ProviderSha256,
                    authority.AssetPath,
                    content.LongLength,
                    Hash(content),
                    content.ToImmutableArray()));
            }
            return ValueTask.FromResult(
                new SkyrimAssetContentResolutionResult(
                    true, assets.ToImmutable(), []));
        }
    }

    private sealed class ExtraAuthorityPlanner(
        ISkyrimAssetAuthorityPlanner inner,
        AssetPath extraPath) : ISkyrimAssetAuthorityPlanner
    {
        public async ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
            SkyrimAssetAuthorityPlanRequest request,
            CancellationToken cancellationToken)
        {
            SkyrimAssetAuthorityPlanResult result =
                await inner.PlanAsync(request, cancellationToken);
            if (!result.Accepted || result.Authorities.IsDefaultOrEmpty)
                return result;
            SkyrimAssetAuthority template = result.Authorities[0];
            return result with
            {
                Authorities = result.Authorities.Add(
                    template with { AssetPath = extraPath })
            };
        }
    }

    private sealed class MetadataTamperingContentResolver(
        ISkyrimAssetContentResolver inner) : ISkyrimAssetContentResolver
    {
        public async ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            SkyrimAssetContentResolutionResult result =
                await inner.ResolveAsync(request, cancellationToken);
            if (!result.Resolved || result.Assets.IsDefaultOrEmpty)
                return result;
            ResolvedSkyrimAssetContent tampered = result.Assets[0] with
            {
                ProviderId = "tampered-provider-id"
            };
            return result with
            {
                Assets = result.Assets.SetItem(0, tampered)
            };
        }
    }

    private sealed class PermutingAuthorityPlanner(
        ISkyrimAssetAuthorityPlanner inner) : ISkyrimAssetAuthorityPlanner
    {
        public async ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
            SkyrimAssetAuthorityPlanRequest request,
            CancellationToken cancellationToken)
        {
            SkyrimAssetAuthorityPlanResult result =
                await inner.PlanAsync(request, cancellationToken);
            return result with
            {
                Authorities = result.Authorities.Reverse().ToImmutableArray()
            };
        }
    }

    private sealed class PermutingContentResolver(
        ISkyrimAssetContentResolver inner) : ISkyrimAssetContentResolver
    {
        public async ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            SkyrimAssetContentResolutionResult result =
                await inner.ResolveAsync(request, cancellationToken);
            return result with
            {
                Assets = result.Assets.Reverse().ToImmutableArray()
            };
        }
    }

    private sealed class MutatingContentResolver(
        ISkyrimAssetContentResolver inner,
        Action mutateAfterResolution) : ISkyrimAssetContentResolver
    {
        public async ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            SkyrimAssetContentResolutionResult result =
                await inner.ResolveAsync(request, cancellationToken);
            if (result.Resolved)
                mutateAfterResolution();
            return result;
        }
    }
}
