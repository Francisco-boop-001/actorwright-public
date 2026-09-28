using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpPackageInstallScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-package-external-install";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        PackageExternalInstallVerificationTests.RunAsync(cancellationToken);
}

internal sealed class ThrowingExternalInstallVerifier :
    IExternalHeadPartInstallVerifier
{
    public ValueTask<ExternalHeadPartInstallVerificationResult> VerifyAsync(
        ExternalHeadPartInstallVerificationRequest request,
        CancellationToken cancellationToken) =>
        throw new ArgumentException(
            "Injected external identity failure for package composition proof.");
}

internal sealed class RecordingPhysicsBindingResolver(
    ExternalHeadPartPhysicsBinding binding) :
    IExternalHeadPartPhysicsBindingResolver
{
    public ImmutableArray<ExternalHeadPartRecordDependency> LastMembers { get; private set; } = [];

    public ValueTask<ExternalHeadPartPhysicsBindingResult> ResolveAsync(
        ExternalHeadPartPhysicsBindingRequest request,
        CancellationToken cancellationToken)
    {
        LastMembers = request.ProviderMembers;
        return ValueTask.FromResult(new ExternalHeadPartPhysicsBindingResult(
            true, binding, [], []));
    }
}

internal static class PackageExternalInstallVerificationTests
{
    private static readonly Sha256Hash ZeroHashForTests =
        new(new string('0', 64));

    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string repositoryRoot = FindRepositoryRoot();
        string root = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-external-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string selectedRelative =
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath;
            string selectedPath = Path.Combine(
                root, selectedRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
            byte[] selectedBytes = LoadSchema3GoldenBytes();
            await File.WriteAllBytesAsync(selectedPath, selectedBytes, cancellationToken);

            string manifestPath = Path.Combine(root, "npcmanager-package.json");
            string selectedHash = Convert.ToHexString(
                SHA256.HashData(selectedBytes));
            string manifest = $$"""
                {
                  "schemaVersion": 1,
                  "edition": "skyrimse",
                  "presetFormat": "racemenu-jslot",
                  "sourcePreset": "preset.jslot",
                  "sourcePresetSha256": "{{new string('0', 64)}}",
                  "sourcePlugin": "Provider.esp",
                  "sourcePluginSha256": "{{new string('0', 64)}}",
                  "outputPlugin": "NpcManagerOutput.esp",
                  "targetFormId": "0x00000800",
                  "artifacts": [
                    {
                      "kind": "external-headpart-dependencies",
                      "relativePath": "{{selectedRelative}}",
                      "byteLength": {{selectedBytes.LongLength}},
                      "sha256": "{{selectedHash}}"
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(manifestPath, manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var labRoot = new WorkspacePath(repositoryRoot);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var reader = new PackageManifestReader(policy, labRoot);
            PackageVerifyResult result = await new PackageVerifyService(reader)
                .VerifyAsync(new PackageVerifyRequest(new WorkspacePath(manifestPath)),
                    cancellationToken);

            Require(result.Verified,
                "Package-byte verification should retain its existing true result: " +
                string.Join(" | ", result.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
            ExternalHeadPartInstallVerificationArtifact? externalArtifact =
                result.ExternalInstallDependencyVerification;
            Require(externalArtifact is not null,
                "An external package row must produce an install verification projection.");
            Require(externalArtifact!.CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.DeclaredUnverified &&
                    externalArtifact.DescriptorClosureValid == false &&
                    !externalArtifact.InstallReady &&
                    !externalArtifact.InstallDependencyAuthority &&
                    !externalArtifact.RuntimeAuthority &&
                    !externalArtifact.VisualAuthority &&
                    externalArtifact.HistoricalSnapshotValid is null,
                "Context-free external verification must not claim current, runtime, or visual authority.");
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeInstallVerificationArtifact(
                externalArtifact!);

            var ordinaryProjection = new PackageVerifyResult(
                true,
                result.Artifact,
                result.Diagnostics);
            byte[] legacyBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    ordinaryProjection.Verified,
                    ordinaryProjection.Artifact,
                    ordinaryProjection.Diagnostics
                });
            byte[] currentBytes = JsonSerializer.SerializeToUtf8Bytes(
                new PackageVerifyResult(
                    ordinaryProjection.Verified,
                    ordinaryProjection.Artifact,
                    ordinaryProjection.Diagnostics));
            Require(currentBytes.SequenceEqual(legacyBytes),
                "The nullable external projection changed the legacy package JSON shape.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        await VerifyOrdinaryPackageRemainsUnprojectedAsync(
            repositoryRoot, cancellationToken);

        await VerifyPackageRowControlsAsync(
            repositoryRoot, cancellationToken);

        await VerifyFreshContextAndMutationsAsync(
            repositoryRoot, cancellationToken);

        await VerifyCombinedStrictOfficialVanillaAsync(
            repositoryRoot, cancellationToken);
    }

    private static async ValueTask VerifyCombinedStrictOfficialVanillaAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-external-install-combined-" +
            Guid.NewGuid().ToString("N"));
        string providerRootPath = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-external-install-combined-provider-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(providerRootPath);
        try
        {
            var labRoot = new WorkspacePath(repositoryRoot);
            var providerFactory = new Preview254ExternalSmpBethesdaFixtureFactory();
            Preview254ExternalSmpBethesdaFixture bethesda =
                await providerFactory.CreateAsync(
                    new WorkspacePath(providerRootPath),
                    Preview254ExternalSmpBethesdaFixtureMode.ModelLessRootWithChild,
                    cancellationToken);
            WritePlugin(
                Path.Combine(bethesda.DataRoot.Value, "Skyrim.esm"),
                "Skyrim.esm");

            Preview254ExternalSmpPhysicsFixture physics =
                await new Preview254ExternalSmpPhysicsFixtureFactory(labRoot)
                    .CreateAsync(
                        new WorkspacePath(providerRootPath),
                        bethesda.HairChild,
                        new AssetPath(
                            "meshes/actors/character/character assets/hair/orchid-child.nif"),
                        Preview254ExternalSmpPhysicsFixtureMode.DefaultBbp,
                        cancellationToken);
            AssetPath vanillaModel = new(
                "meshes/actors/character/character assets/hair/vanilla-hair.nif");
            AssetPath vanillaTri = new(
                "meshes/actors/character/character assets/hair/vanilla-hair.tri");
            WriteCombinedOfficialVanillaFixture(
                bethesda,
                physics,
                vanillaModel,
                vanillaTri);

            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            byte[] providerBytes = await File.ReadAllBytesAsync(
                bethesda.ProviderPluginPath.Value, cancellationToken);
            Sha256Hash providerHash = HashBytes(providerBytes);
            var providerAuthority = new SkyrimFaceRecordPluginAuthority(
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginPath,
                providerHash);
            string skyrimPath = Path.Combine(
                bethesda.DataRoot.Value, "Skyrim.esm");
            byte[] skyrimBytes = await File.ReadAllBytesAsync(
                skyrimPath, cancellationToken);
            var vanillaPlugin = new PluginName("Skyrim.esm");
            Sha256Hash vanillaHash = HashBytes(skyrimBytes);
            var routeResolver = new BethesdaSkyrimFaceRecordRouteResolver(
                policy, labRoot);
            SkyrimFaceRecordRouteResult route = await routeResolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new FormReference(bethesda.ProviderPlugin, new FormId(0x900)),
                    NpcSex.Male,
                    [new SkyrimFaceRecordHeadPartSelection(
                        bethesda.HairRoot, [])],
                    [new SkyrimFaceRecordPluginAuthority(
                        vanillaPlugin,
                        new WorkspacePath(skyrimPath),
                        vanillaHash),
                     providerAuthority]),
                cancellationToken);
            Require(route.Accepted && route.Route is not null,
                "The combined fixture route could not be reopened: " +
                FormatDiagnostics(route.Diagnostics));
            SkyrimFaceRecordRoute verifiedRoute = route.Route!;
            ImmutableArray<ExternalHeadPartRecordDependency> members =
                verifiedRoute.HeadPartGraph.Select(ToDependency).ToImmutableArray();
            ExternalHeadPartRecordDependency official = members.Single(item =>
                item.OriginForm.Plugin == vanillaPlugin);
            Require(official.ModelNif == vanillaModel &&
                    official.TriRoutes.Any(item => item.Path == vanillaTri) &&
                    official.WinningPlugin == vanillaPlugin &&
                    official.WinningPluginSha256 == vanillaHash &&
                    official.WinningPluginByteLength == skyrimBytes.LongLength,
                "The combined route did not preserve the authentic official-vanilla model/TRI authority.");

            ExternalHeadPartDependencyDiscoveryService discovery =
                new(
                    new SkyrimAssetAuthorityPlanner(
                        new BethesdaAssetIndexer(), policy, labRoot),
                    new SkyrimAssetContentResolver(policy, labRoot),
                    new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));
            ExternalHeadPartDependencyDiscoveryResult discovered =
                await discovery.DiscoverAsync(
                    new ExternalHeadPartDependencyDiscoveryRequest(
                        bethesda.DataRoot,
                        [vanillaPlugin, bethesda.ProviderPlugin],
                        verifiedRoute,
                        NpcSex.Male),
                    cancellationToken);
            Require(discovered.Status ==
                        ExternalHeadPartDependencyDiscoveryStatus.Accepted &&
                    discovered.Descriptor is not null &&
                    discovered.Diagnostics.All(item =>
                        item.Severity != DiagnosticSeverity.Error),
                "The combined descriptor discovery was refused: " +
                FormatDiagnostics(discovered.Diagnostics));
            ExternalHeadPartDependencyDescriptor discoveredDescriptor =
                discovered.Descriptor!;
            // Schema 3 carries the NIF/TRI/DDS selected dependency envelope;
            // physics XML/defaultBBP remain independently reopened through
            // descriptor.Physics during strict install verification.
            ExternalHeadPartDependencyDescriptor descriptorDraft =
                discoveredDescriptor with
                {
                    Assets = discoveredDescriptor.Assets
                        .Where(item => RaceMenuSelectedDependencyManifestAssetRules
                            .IsSupported(item.Path))
                        .ToImmutableArray(),
                    DescriptorId = default
                };
            ExternalHeadPartDependencyDescriptor descriptor =
                descriptorDraft with
                {
                    DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeDescriptorId(descriptorDraft)
                };
            Require(descriptor.Members.Length == members.Length &&
                    descriptor.Members.Any(item =>
                        item.OriginForm == official.OriginForm &&
                        item.ModelNif == vanillaModel &&
                        item.TriRoutes.Any(route => route.Path == vanillaTri)) &&
                    descriptor.Assets.Any(item =>
                        item.Path == vanillaModel &&
                        item.ProviderPlugin == vanillaPlugin &&
                        item.ProviderPluginSha256 == vanillaHash &&
                        item.ByteLength ==
                            new FileInfo(Path.Combine(
                                bethesda.DataRoot.Value,
                                vanillaModel.Value.Replace('/',
                                    Path.DirectorySeparatorChar))).Length) &&
                    descriptor.Assets.Any(item =>
                        item.Path == vanillaTri &&
                        item.ProviderPlugin == vanillaPlugin &&
                        item.ProviderPluginSha256 == vanillaHash),
                "The combined descriptor lost official-vanilla asset ownership.");
            ImmutableArray<ExternalHeadPartRecordDependency> providerPhysicsMembers =
                BethesdaExternalHeadPartInstallVerifier
                    .SelectPrimaryProviderPhysicsMembers(descriptor);
            Require(providerPhysicsMembers.Length == 1 &&
                    providerPhysicsMembers[0].WinningPlugin == bethesda.ProviderPlugin &&
                    providerPhysicsMembers[0].OriginForm != official.OriginForm &&
                    BethesdaExternalHeadPartInstallVerifier
                        .IsProviderPhysicsSubsetValid(
                            descriptor, providerPhysicsMembers),
                "The combined descriptor did not isolate provider-only physics members.");

            string packageOutputPath = Path.Combine(
                root, "NpcManagerOutput.esp");
            byte[] faceGeomBytes = [0x46, 0x41, 0x43, 0x45, 0x47, 0x45, 0x4F, 0x4D];
            string faceGeomPath = Path.Combine(
                root, "Data", "NPCManager", "FaceGeom", "combined.nif");
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
            await File.WriteAllBytesAsync(
                faceGeomPath, faceGeomBytes, cancellationToken);
            WriteOutputPlugin(
                packageOutputPath,
                "NpcManagerOutput.esp",
                [vanillaPlugin.Value, bethesda.ProviderPlugin.Value],
                members.Select(item => item.WinningForm).ToImmutableArray(),
                includeOrdinaryPnamInterleave: true);
            byte[] outputBytes = await File.ReadAllBytesAsync(
                packageOutputPath, cancellationToken);
            ImmutableArray<PluginName> expectedMasters =
                [vanillaPlugin, bethesda.ProviderPlugin];
            ImmutableArray<FormReference> expectedPnam =
                members.Select(item => item.WinningForm).ToImmutableArray();
            ImmutableArray<FormReference> expectedOutputPnam =
                expectedPnam.Take(1)
                    .Concat([
                        new FormReference(
                            new PluginName("Skyrim.esm"), new FormId(0x1000)),
                        new FormReference(
                            new PluginName("Skyrim.esm"), new FormId(0x1001))])
                    .Concat(expectedPnam.Skip(1))
                    .ToImmutableArray();
            using (var output = SkyrimMod.CreateFromBinaryOverlay(
                       packageOutputPath, SkyrimRelease.SkyrimSE))
            {
                Require(
                    output.MasterReferences.Select(item =>
                            new PluginName(item.Master.ToString()))
                        .SequenceEqual(expectedMasters) &&
                    output.Npcs.SelectMany(item => item.HeadParts.Select(link =>
                            new FormReference(
                                new PluginName(link.FormKey.ModKey.ToString()),
                                new FormId(link.FormKey.ID))))
                        .SequenceEqual(expectedOutputPnam),
                    "The combined package output did not retain vanilla/provider masters and PNAM closure.");
            }

            byte[] outputFaceGeomHash = SHA256.HashData(faceGeomBytes);
            var attestationDraft = new ExternalHeadPartFaceGeomExclusionAttestation(
                ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                HashBytes(Encoding.UTF8.GetBytes("combined-attestation-placeholder")),
                descriptor.DescriptorId,
                new AssetPath("Data/NPCManager/FaceGeom/combined.nif"),
                new Sha256Hash(Convert.ToHexString(outputFaceGeomHash)),
                faceGeomBytes.LongLength,
                [],
                [],
                [],
                "preview254-package-combined-test");
            var attestation = attestationDraft with
            {
                AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeAttestationHash(attestationDraft)
            };
            var outputBinding =
                new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                    new PluginName("NpcManagerOutput.esp"),
                    HashBytes(outputBytes),
                    outputBytes.LongLength,
                    expectedMasters,
                    expectedPnam);
            var externalGroup =
                new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                    descriptor,
                    attestation,
                    outputBinding,
                    new AssetPath("Data/NPCManager/FaceGeom/combined.nif"),
                    new Sha256Hash(Convert.ToHexString(outputFaceGeomHash)),
                    faceGeomBytes.LongLength);

            string selectedPath = Path.Combine(
                root,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                    .Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
            RaceMenuNpcFormBinding headPartBinding = new(
                new RecordSignature("HDPT"),
                members[0].OriginForm,
                members[0].WinningForm,
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginPath,
                providerHash,
                NpcHeadPartType.Hair);
            var sourceHeadPart = new PresetHeadPart(
                PresetIdentifier.Parse(members[0].WinningForm.ToString()), 0);
            var draft = new RaceMenuPresetRecordAuthorityDraft(
                "preview254-package-combined-test",
                new PresetDocument(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    null!,
                    HashBytes(Encoding.UTF8.GetBytes(
                        "preview254-package-combined-preset")),
                    []),
                new RaceMenuPresetTarget(
                    "preview254-package-combined-test",
                    verifiedRoute.Race.Reference,
                    NpcSex.Male,
                    bethesda.DataRoot,
                    [providerAuthority]),
                headPartBinding,
                [new RaceMenuPresetHeadPartAuthority(sourceHeadPart,
                    headPartBinding)],
                headPartBinding,
                null!,
                0,
                new FormId(0x900),
                null!,
                false);
            ImmutableArray<SkyrimAssetAuthority> selectedAssets = descriptor.Assets
                .Where(item => RaceMenuSelectedDependencyManifestAssetRules
                    .IsSupported(item.Path))
                .Select(item => ToSelectedAssetAuthority(
                    bethesda.DataRoot, item))
                .ToImmutableArray();
            RaceMenuSelectedDependencyManifestWriteResult selectedWrite =
                await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            draft.Preset.SourceHash,
                            draft,
                            selectedAssets,
                            [],
                            new WorkspacePath(selectedPath))
                        {
                            ExternalInstallDependencies = [externalGroup]
                        },
                        cancellationToken);
            Require(selectedWrite.Written,
                "The combined selected schema-3 manifest was refused: " +
                FormatDiagnostics(selectedWrite.Diagnostics));
            byte[] selectedBytes = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);
            Sha256Hash selectedHash = HashBytes(selectedBytes);
            RaceMenuSelectedDependencyManifestReadResult selectedRead =
                await new RaceMenuSelectedDependencyManifestReader().ReadAsync(
                    new WorkspacePath(selectedPath),
                    selectedHash,
                    new WorkspacePath(root),
                    cancellationToken);
            RaceMenuSelectedDependencyManifestExternalInstallDependency selectedGroup =
                selectedRead.Artifact?.ExternalInstallDependencies.SingleOrDefault() ??
                throw new InvalidOperationException(
                    "The combined selected schema-3 manifest lost its external group.");
            Require(selectedGroup.Descriptor.DescriptorId == descriptor.DescriptorId &&
                    selectedGroup.Descriptor.Members.Any(item =>
                        item.OriginForm == official.OriginForm &&
                        item.WinningPlugin == vanillaPlugin &&
                        item.WinningPluginSha256 == vanillaHash &&
                        item.WinningPluginByteLength == skyrimBytes.LongLength &&
                        item.ModelNif == vanillaModel &&
                        item.TriRoutes.Any(route => route.Path == vanillaTri)) &&
                    selectedGroup.Descriptor.Assets.Any(item =>
                        item.Path == vanillaModel &&
                        item.ProviderPlugin == vanillaPlugin &&
                        item.ProviderPluginSha256 == vanillaHash &&
                        item.ByteLength == new FileInfo(Path.Combine(
                            bethesda.DataRoot.Value,
                            vanillaModel.Value.Replace('/',
                                Path.DirectorySeparatorChar))).Length) &&
                    selectedGroup.Descriptor.Assets.Any(item =>
                        item.Path == vanillaTri &&
                        item.ProviderPlugin == vanillaPlugin &&
                        item.ProviderPluginSha256 == vanillaHash),
                "The selected schema-3 evidence did not preserve the vanilla graph/asset authority.");

            string packageManifestPath = Path.Combine(
                root, "npcmanager-package.json");
            string outputHash = Convert.ToHexString(SHA256.HashData(outputBytes));
            string faceGeomHash = Convert.ToHexString(outputFaceGeomHash);
            string packageManifest = $$"""
                {
                  "schemaVersion": 1,
                  "edition": "skyrimse",
                  "presetFormat": "racemenu-jslot",
                  "sourcePreset": "preset.jslot",
                  "sourcePresetSha256": "{{new string('0', 64)}}",
                  "sourcePlugin": "{{bethesda.ProviderPlugin.Value}}",
                  "sourcePluginSha256": "{{providerHash.Value}}",
                  "outputPlugin": "NpcManagerOutput.esp",
                  "targetFormId": "0x00000900",
                  "artifacts": [
                    {
                      "kind": "plugin",
                      "relativePath": "NpcManagerOutput.esp",
                      "byteLength": {{outputBytes.LongLength}},
                      "sha256": "{{outputHash}}"
                    },
                    {
                      "kind": "external-headpart-dependencies",
                      "relativePath": "{{RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath}}",
                      "byteLength": {{selectedBytes.LongLength}},
                      "sha256": "{{selectedHash.Value}}"
                    },
                    {
                      "kind": "facegeom",
                      "relativePath": "Data/NPCManager/FaceGeom/combined.nif",
                      "byteLength": {{faceGeomBytes.LongLength}},
                      "sha256": "{{faceGeomHash}}"
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var reader = new PackageManifestReader(policy, labRoot);
            var service = new PackageVerifyService(reader);
            async ValueTask<PackageVerifyResult> VerifyStrictAsync(
                ImmutableArray<PluginName> enabledOrder,
                IExternalHeadPartPhysicsBindingResolver? physicsResolver = null)
            {
                var packageService = physicsResolver is null
                    ? service
                    : new PackageVerifyService(
                        reader,
                        new BethesdaExternalHeadPartInstallVerifier(
                            new RaceMenuSelectedDependencyManifestReader(),
                            physicsResolver));
                return await packageService.VerifyAsync(
                    new PackageVerifyRequest(
                        new WorkspacePath(packageManifestPath))
                    {
                        InstallContext =
                            new ExternalHeadPartInstallVerificationContext(
                                bethesda.DataRoot,
                                enabledOrder,
                                verifiedRoute.Race.Reference),
                        RequireInstallDependencyAuthority = true
                    }, cancellationToken);
            }

            PackageVerifyResult fresh = await VerifyStrictAsync(
                bethesda.EnabledPluginOrder);
            ExternalHeadPartInstallVerificationArtifact freshArtifact =
                fresh.ExternalInstallDependencyVerification ??
                throw new InvalidOperationException(
                    "The combined package verification produced no external artifact.");
            Require(fresh.Verified &&
                    freshArtifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.Verified &&
                    freshArtifact.InstallReady &&
                    freshArtifact.InstallDependencyAuthority &&
                    !freshArtifact.RuntimeAuthority &&
                    !freshArtifact.VisualAuthority,
                "The combined strict package did not establish install-only authority: " +
                FormatDiagnostics(fresh.Diagnostics));
            var fingerprint = freshArtifact.VerifiedInstallSnapshot?.ContextFingerprint ??
                throw new InvalidOperationException(
                    "The combined strict package produced no context fingerprint.");
            Require(fingerprint.Observations.Any(item =>
                        item.Kind == "enabled-plugin" &&
                        item.PortableIdentity == vanillaPlugin.Value &&
                        item.Sha256 == vanillaHash &&
                        item.ByteLength == skyrimBytes.LongLength) &&
                    fingerprint.Observations.Any(item =>
                        item.Kind == "winning-record" &&
                        item.PortableIdentity == official.OriginForm.ToString()) &&
                    fingerprint.Observations.Any(item =>
                        item.Kind == "asset" &&
                        item.PortableIdentity == vanillaModel.Value) &&
                    freshArtifact.ProviderObservations.Length == 1 &&
                    freshArtifact.ProviderObservations[0].ProviderPlugin ==
                        bethesda.ProviderPlugin,
                "The combined fingerprint/provider observations lost official evidence or admitted a non-provider physics owner.");

            var recordingPhysicsResolver = new RecordingPhysicsBindingResolver(
                descriptor.Physics);
            PackageVerifyResult injectedPhysics = await VerifyStrictAsync(
                bethesda.EnabledPluginOrder, recordingPhysicsResolver);
            Require(injectedPhysics.Verified &&
                    recordingPhysicsResolver.LastMembers.Length == 1 &&
                    recordingPhysicsResolver.LastMembers.All(item =>
                        item.WinningPlugin == bethesda.ProviderPlugin) &&
                    recordingPhysicsResolver.LastMembers.All(item =>
                        item.OriginForm != official.OriginForm),
                "The combined strict verifier passed an official-vanilla model member to physics.");

            string vanillaModelPath = Path.Combine(
                bethesda.DataRoot.Value,
                vanillaModel.Value.Replace('/', Path.DirectorySeparatorChar));
            byte[] vanillaModelBytes = await File.ReadAllBytesAsync(
                vanillaModelPath, cancellationToken);
            await File.WriteAllBytesAsync(
                vanillaModelPath,
                vanillaModelBytes.Concat(new byte[] { 0x01 }).ToArray(),
                cancellationToken);
            try
            {
                PackageVerifyResult modelDrift = await VerifyStrictAsync(
                    bethesda.EnabledPluginOrder);
                Require(modelDrift.ExternalInstallDependencyVerification!
                            .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    modelDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.AssetDrift),
                    "Vanilla model drift was not refused as asset drift: " +
                    FormatDiagnostics(modelDrift.Diagnostics));
            }
            finally
            {
                await File.WriteAllBytesAsync(
                    vanillaModelPath, vanillaModelBytes, cancellationToken);
            }

            string vanillaPluginPath = Path.Combine(
                bethesda.DataRoot.Value, vanillaPlugin.Value);
            byte[] vanillaPluginBytes = await File.ReadAllBytesAsync(
                vanillaPluginPath, cancellationToken);
            byte[] vanillaPluginIdentityDrift = vanillaPluginBytes.ToArray();
            vanillaPluginIdentityDrift[8] ^= 0x01;
            await File.WriteAllBytesAsync(
                vanillaPluginPath,
                vanillaPluginIdentityDrift,
                cancellationToken);
            try
            {
                PackageVerifyResult pluginDrift = await VerifyStrictAsync(
                    bethesda.EnabledPluginOrder);
                Require(pluginDrift.ExternalInstallDependencyVerification!
                            .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    pluginDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                    "Vanilla plugin identity drift was not refused as record drift: " +
                    FormatDiagnostics(pluginDrift.Diagnostics));
            }
            finally
            {
                await File.WriteAllBytesAsync(
                    vanillaPluginPath, vanillaPluginBytes, cancellationToken);
            }

            var overridePlugin = new PluginName("VanillaHairPatch.esp");
            string overridePath = Path.Combine(
                bethesda.DataRoot.Value, overridePlugin.Value);
            WriteOfficialWinningOverride(
                overridePath, vanillaModel, vanillaTri);
            try
            {
                PackageVerifyResult winningRecordDrift = await VerifyStrictAsync(
                    bethesda.EnabledPluginOrder.Add(overridePlugin));
                Require(winningRecordDrift.ExternalInstallDependencyVerification!
                            .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    winningRecordDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                    "Vanilla winning-record drift was not refused as record drift: " +
                    FormatDiagnostics(winningRecordDrift.Diagnostics));
            }
            finally
            {
                if (File.Exists(overridePath))
                    File.Delete(overridePath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(providerRootPath))
                Directory.Delete(providerRootPath, recursive: true);
        }
    }

    private static void WriteCombinedOfficialVanillaFixture(
        Preview254ExternalSmpBethesdaFixture bethesda,
        Preview254ExternalSmpPhysicsFixture physics,
        AssetPath vanillaModel,
        AssetPath vanillaTri)
    {
        var vanillaKey = ModKey.FromNameAndExtension("Skyrim.esm");
        var vanilla = new SkyrimMod(vanillaKey, SkyrimRelease.SkyrimSE);
        var vanillaHeadPart = new HeadPart(
            new FormKey(vanillaKey, 0x1000), SkyrimRelease.SkyrimSE)
        {
            EditorID = "VanillaOrchidHair",
            Name = "Vanilla Orchid hair",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(vanillaModel.Value)
            }
        };
        vanillaHeadPart.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                vanillaTri.Value)
        });
        vanilla.HeadParts.Add(vanillaHeadPart);
        WriteSkyrimMod(
            vanilla,
            Path.Combine(bethesda.DataRoot.Value, "Skyrim.esm"));

        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            bethesda.DataRoot,
            vanillaModel,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null, vanillaTri.Value),
            bethesda.ScratchRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(
            bethesda.DataRoot,
            vanillaTri,
            Encoding.UTF8.GetBytes("official-vanilla-tri"),
            bethesda.ScratchRoot);

        var providerKey = ModKey.FromNameAndExtension(
            bethesda.ProviderPlugin.Value);
        var provider = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
        provider.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = vanillaKey
        });
        var root = new HeadPart(
            new FormKey(providerKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRootHairCombined",
            Name = "Orchid root hair combined",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair
        };
        root.ExtraParts.Add(new FormLink<IHeadPartGetter>(
            new FormKey(providerKey, 0x801)));
        root.ExtraParts.Add(new FormLink<IHeadPartGetter>(
            new FormKey(vanillaKey, 0x1000)));
        provider.HeadParts.Add(root);
        var child = new HeadPart(
            new FormKey(providerKey, 0x801), SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidChildHairCombined",
            Name = "Orchid child hair combined",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    physics.ModelNif.Value)
            }
        };
        child.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                physics.Tri.Value)
        });
        provider.HeadParts.Add(child);
        var raceKey = new FormKey(providerKey, 0x900);
        provider.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRaceCombined",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
        });
        WriteSkyrimMod(
            provider,
            bethesda.ProviderPluginPath.Value);
    }

    private static async ValueTask VerifyPackageRowControlsAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-row-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] selectedBytes = LoadSchema3GoldenBytes();
            string selectedHash = Convert.ToHexString(
                SHA256.HashData(selectedBytes));
            string canonical =
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath;
            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath(repositoryRoot),
                new WorkspacePath(@"F:\ExampleGame"));
            var reader = new PackageManifestReader(
                policy, new WorkspacePath(repositoryRoot));
            var service = new PackageVerifyService(reader);

            async ValueTask<PackageVerifyResult> VerifyAsync(
                string name,
                (string Kind, string RelativePath, string Sha256)[] rows,
                byte[]? contents = null)
            {
                byte[] payload = contents ?? selectedBytes;
                string packageRoot = Path.Combine(root, name);
                Directory.CreateDirectory(packageRoot);
                foreach ((string _, string relativePath, string _) in rows)
                {
                    string physical = Path.Combine(packageRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
                    await File.WriteAllBytesAsync(
                        physical, payload, cancellationToken);
                }

                string artifacts = string.Join(",\n", rows.Select(row => $$"""
                    {
                      "kind": "{{row.Kind}}",
                      "relativePath": "{{row.RelativePath}}",
                      "byteLength": {{payload.LongLength}},
                      "sha256": "{{row.Sha256}}"
                    }
                    """));
                string manifest = $$"""
                    {
                      "schemaVersion": 1,
                      "edition": "skyrimse",
                      "presetFormat": "racemenu-jslot",
                      "sourcePreset": "preset.jslot",
                      "sourcePresetSha256": "{{new string('0', 64)}}",
                      "sourcePlugin": "Provider.esp",
                      "sourcePluginSha256": "{{new string('0', 64)}}",
                      "outputPlugin": "NpcManagerOutput.esp",
                      "targetFormId": "0x00000800",
                      "artifacts": [
                    {{artifacts}}
                      ]
                    }
                    """;
                string manifestPath = Path.Combine(
                    packageRoot, "npcmanager-package.json");
                await File.WriteAllTextAsync(
                    manifestPath, manifest,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
                return await service.VerifyAsync(
                    new PackageVerifyRequest(new WorkspacePath(manifestPath)),
                    cancellationToken);
            }

            PackageVerifyResult wrongKind = await VerifyAsync(
                "wrong-kind", [("plugin", canonical, selectedHash)]);
            Require(!wrongKind.Verified && wrongKind.Diagnostics.Any(item =>
                        item.Code == "external-headpart-package-row-kind"),
                "A schema-3 external manifest under the canonical path accepted the wrong package row kind.");

            string alternate = canonical.Replace(
                "selected-preset-dependencies.json",
                "selected-preset-dependencies-alt.json",
                StringComparison.Ordinal);
            PackageVerifyResult duplicate = await VerifyAsync(
                "duplicate",
                [("external-headpart-dependencies", canonical, selectedHash),
                 ("external-headpart-dependencies", alternate, selectedHash)]);
            Require(!duplicate.Verified && duplicate.Diagnostics.Any(item =>
                        item.Code == "external-headpart-package-row-cardinality"),
                "Duplicate external package rows were not refused by exact cardinality.");

            PackageVerifyResult alternateOnly = await VerifyAsync(
                "alternate-path",
                [("external-headpart-dependencies", alternate, selectedHash)]);
            Require(!alternateOnly.Verified && alternateOnly.Diagnostics.Any(item =>
                        item.Code == "external-headpart-package-row-cardinality"),
                "An external dependency row at an alternate path was accepted.");

            PackageVerifyResult hashUnbound = await VerifyAsync(
                "hash-unbound",
                [("external-headpart-dependencies", canonical,
                    new string('0', 64))]);
            Require(!hashUnbound.Verified && hashUnbound.Diagnostics.Any(item =>
                        item.Code == "package-artifact-hash-mismatch"),
                "An external row with an unbound package hash was accepted.");

            PackageVerifyResult stale = await VerifyAsync(
                "stale-no-output",
                [("external-headpart-dependencies", canonical, selectedHash)]);
            Require(stale.ExternalInstallDependencyVerification is not null &&
                    stale.ExternalInstallDependencyVerification
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    stale.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.OutputReferenceMissing),
                "A descriptor without a package output reference was not refused.");

            byte[] malformedSchema3 = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(selectedBytes)
                    .Replace(
                        "\"externalInstallDependencies\": [",
                        "\"externalInstallDependencies\": []",
                        StringComparison.Ordinal)
                    .Replace(
                        "\"externalInstallDependencies\":[",
                        "\"externalInstallDependencies\":[]",
                        StringComparison.Ordinal));
            Require(!malformedSchema3.SequenceEqual(selectedBytes),
                "The malformed schema-3 fixture mutation did not change bytes.");
            string malformedHash = Convert.ToHexString(
                SHA256.HashData(malformedSchema3));
            PackageVerifyResult malformed = await VerifyAsync(
                "malformed-wrong-kind",
                [("plugin", canonical, malformedHash)],
                malformedSchema3);
            Require(!malformed.Verified &&
                    malformed.ExternalInstallDependencyVerification is null &&
                    malformed.Diagnostics.Any(item =>
                        item.Code == "racemenu-selected-dependencies-read"),
                "A malformed schema-3 wrong-kind row did not fail closed through the strict reader.");
            byte[] legacyBytes = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"id\":\"legacy-selected-dependencies\",\"headParts\":[],\"looseAssets\":[],\"archives\":[]}");
            string legacyHash = Convert.ToHexString(
                SHA256.HashData(legacyBytes));
            PackageVerifyResult legacy = await VerifyAsync(
                "legacy-no-external",
                [("plugin", canonical, legacyHash)],
                legacyBytes);
            Require(legacy.Verified &&
                    legacy.ExternalInstallDependencyVerification is null &&
                legacy.Diagnostics.All(item =>
                        item.Severity != DiagnosticSeverity.Error),
                "A valid legacy/no-external canonical row was not preserved: " +
                FormatDiagnostics(legacy.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask VerifyFreshContextAndMutationsAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string packageRootPath = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-external-install-package-" +
            Guid.NewGuid().ToString("N"));
        string providerRootPath = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-external-install-provider-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRootPath);
        Directory.CreateDirectory(providerRootPath);
        try
        {
            var labRoot = new WorkspacePath(repositoryRoot);
            var providerFactory = new Preview254ExternalSmpBethesdaFixtureFactory();
            Preview254ExternalSmpBethesdaFixture bethesda =
                await providerFactory.CreateAsync(
                    new WorkspacePath(providerRootPath),
                    Preview254ExternalSmpBethesdaFixtureMode.ModelLessRootWithChild,
                    cancellationToken);
            AddUnusedProviderHeadPart(bethesda.ProviderPluginPath.Value);
            AddUnusedProviderHeadPart(
                bethesda.ProviderPluginPath.Value,
                0x905,
                HeadPart.TypeEnum.Eyes);
            byte[] providerFixtureBytes = await File.ReadAllBytesAsync(
                bethesda.ProviderPluginPath.Value, cancellationToken);
            bethesda = bethesda with
            {
                ProviderPluginSha256 = HashBytes(providerFixtureBytes),
                ProviderPluginByteLength = providerFixtureBytes.LongLength
            };
            string skyrimPath = Path.Combine(
                bethesda.DataRoot.Value, "Skyrim.esm");
            WritePlugin(skyrimPath, "Skyrim.esm");

            AssetPath modelPath = new(
                "meshes/actors/character/character assets/hair/orchid-child.nif");
            Preview254ExternalSmpPhysicsFixture physics =
                await new Preview254ExternalSmpPhysicsFixtureFactory(labRoot)
                    .CreateAsync(
                        new WorkspacePath(providerRootPath),
                        bethesda.HairChild,
                        modelPath,
                        Preview254ExternalSmpPhysicsFixtureMode.DefaultBbp,
                        cancellationToken);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));

            string archiveMemberRelative =
                "meshes/actors/character/character assets/hair/orchid-archive.nif";
            string archiveSourceRoot = Path.Combine(
                providerRootPath, "archive-source");
            string archiveMemberPath = Path.Combine(
                archiveSourceRoot,
                archiveMemberRelative.Replace('/', Path.DirectorySeparatorChar));
            WriteBytes(archiveMemberPath, [0x41, 0x52, 0x43, 0x48]);
            WorkspacePath archivePath = new(Path.Combine(
                bethesda.DataRoot.Value, "OrchidAdornment.bsa"));
            SkyrimBsaBuildResult archiveBuild =
                await new BethesdaSkyrimBsaService(policy, labRoot).BuildAsync(
                    new SkyrimBsaBuildRequest(
                        new WorkspacePath(archiveSourceRoot),
                        archivePath,
                        [new AssetPath(archiveMemberRelative)]),
                    cancellationToken);
            Require(archiveBuild.Written && archiveBuild.Artifact is not null,
                "The authentic archive provider could not be built.");
            byte[] archiveMemberBytes = await File.ReadAllBytesAsync(
                archiveMemberPath, cancellationToken);
            byte[] archiveBytes = await File.ReadAllBytesAsync(
                archivePath.Value, cancellationToken);
            var archiveAsset = new ExternalHeadPartAssetDependency(
                new AssetPath(archiveMemberRelative),
                HashBytes(archiveMemberBytes),
                archiveMemberBytes.LongLength,
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginSha256,
                new ExternalHeadPartArchiveMemberAuthority(
                    new AssetPath("OrchidAdornment.bsa"),
                    HashBytes(archiveBytes),
                    archiveBytes.LongLength,
                    new AssetPath(archiveMemberRelative),
                    HashBytes(archiveMemberBytes),
                    archiveMemberBytes.LongLength));

            var packageOutputPath = new WorkspacePath(Path.Combine(
                packageRootPath, "NpcManagerOutput.esp"));
            byte[] faceGeomBytes = [0x46, 0x41, 0x43, 0x45, 0x47, 0x45, 0x4F, 0x4D];
            string faceGeomPath = Path.Combine(
                packageRootPath, "Data", "NPCManager", "FaceGeom", "external.nif");
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
            await File.WriteAllBytesAsync(faceGeomPath, faceGeomBytes,
                cancellationToken);

            var providerAuthority = new SkyrimFaceRecordPluginAuthority(
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginPath,
                bethesda.ProviderPluginSha256);
            var routeResolver = new BethesdaSkyrimFaceRecordRouteResolver(
                policy, labRoot);
            SkyrimFaceRecordRouteResult route = await routeResolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new FormReference(bethesda.ProviderPlugin, new FormId(0x900)),
                    NpcSex.Male,
                    [new SkyrimFaceRecordHeadPartSelection(bethesda.HairRoot, [])],
                    [providerAuthority]),
                cancellationToken);
            Require(route.Accepted && route.Route is not null,
                "The authentic provider route could not be reopened: " +
                FormatDiagnostics(route.Diagnostics));

            ImmutableArray<ExternalHeadPartRecordDependency> members =
                route.Route!.HeadPartGraph.Select(ToDependency).ToImmutableArray();
            WriteOutputPlugin(
                packageOutputPath.Value,
                "NpcManagerOutput.esp",
                ["Skyrim.esm", bethesda.ProviderPlugin.Value],
                members.Select(item => item.WinningForm).ToImmutableArray(),
                includeLegitimateOutputOwnedFace: true);
            var provider = new ExternalHeadPartProviderIdentity(
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginSha256,
                bethesda.ProviderPluginByteLength,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired);
            ExternalHeadPartPhysicsBindingResult physicsResult =
                await new ExternalHeadPartPhysicsBindingResolver(policy, labRoot)
                    .ResolveAsync(
                        new ExternalHeadPartPhysicsBindingRequest(
                            bethesda.DataRoot,
                            bethesda.EnabledPluginOrder,
                            provider,
                            [members[1]]),
                        cancellationToken);
            Require(physicsResult.Accepted && physicsResult.Binding is not null,
                "The authentic physics provider could not be reopened: " +
                FormatDiagnostics(physicsResult.Diagnostics));

            ImmutableArray<ExternalHeadPartAssetDependency> assets =
                [CreateAsset(bethesda.DataRoot, modelPath, provider),
                 CreateAsset(bethesda.DataRoot, physics.Tri, provider),
                 CreateAsset(bethesda.DataRoot, physics.ColliderNif, provider),
                 archiveAsset];
            var descriptorDraft = new ExternalHeadPartDependencyDescriptor(
                ExternalHeadPartSchemaIdentifiers.Descriptor,
                HashBytes(Encoding.UTF8.GetBytes("descriptor-placeholder")),
                ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
                members[0].OriginForm,
                members[0].WinningForm,
                members[0].DeclaredType,
                HashBytes(Encoding.UTF8.GetBytes(string.Join(
                    "|", members.Select(item => item.WinningRecordSha256.Value)))),
                provider,
                members,
                physicsResult.Binding!,
                assets,
                []);
            var descriptor = descriptorDraft with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptorDraft)
            };
            ExternalHeadPartRecordDependency officialModelMember = members[1] with
            {
                OriginForm = new FormReference(
                    new PluginName("Skyrim.esm"), new FormId(0x100)),
                RequiredOutputMaster = new PluginName("Skyrim.esm"),
                WinningForm = new FormReference(
                    new PluginName("Skyrim.esm"), new FormId(0x100)),
                WinningPlugin = new PluginName("Skyrim.esm"),
                WinningPluginSha256 = ZeroHashForTests,
                WinningPluginByteLength = 1
            };
            ExternalHeadPartDependencyDescriptor mixedPhysicsDescriptor =
                descriptor with
                {
                    Members = descriptor.Members.Add(officialModelMember)
                };
            ImmutableArray<ExternalHeadPartRecordDependency> providerPhysicsMembers =
                BethesdaExternalHeadPartInstallVerifier
                    .SelectPrimaryProviderPhysicsMembers(mixedPhysicsDescriptor);
            Require(mixedPhysicsDescriptor.Members.Length == members.Length + 1 &&
                    providerPhysicsMembers.Length == 1 &&
                    providerPhysicsMembers[0].WinningPlugin == bethesda.ProviderPlugin,
                "Provider physics selection discarded the full graph or admitted an official model member.");
            ExternalHeadPartRecordDependency overriddenProviderMember = members[1] with
            {
                WinningForm = new FormReference(
                    bethesda.ProviderPlugin, new FormId(0x8FF))
            };
            Require(BethesdaExternalHeadPartInstallVerifier
                        .SelectPrimaryProviderPhysicsMembers(
                            descriptor with
                            {
                                Members = [overriddenProviderMember]
                            }).IsEmpty,
                "A provider member with a winning-form override was admitted to primary physics.");
            ExternalHeadPartDependencyDescriptor officialOnlyPhysicsDescriptor =
                mixedPhysicsDescriptor with
                {
                    Members = [officialModelMember]
                };
            Require(!BethesdaExternalHeadPartInstallVerifier
                        .IsProviderPhysicsSubsetValid(
                            officialOnlyPhysicsDescriptor, []),
                "A non-empty physics contract with no provider-owned model member was accepted.");
            byte[] outputFaceGeomHash = SHA256.HashData(faceGeomBytes);
            var attestationDraft = new ExternalHeadPartFaceGeomExclusionAttestation(
                ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                HashBytes(Encoding.UTF8.GetBytes("attestation-placeholder")),
                descriptor.DescriptorId,
                new AssetPath("Data/NPCManager/FaceGeom/external.nif"),
                new Sha256Hash(Convert.ToHexString(outputFaceGeomHash)),
                faceGeomBytes.LongLength,
                [],
                [],
                [],
                "preview254-package-test");
            var attestation = attestationDraft with
            {
                AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeAttestationHash(attestationDraft)
            };
            byte[] outputBytes = await File.ReadAllBytesAsync(
                packageOutputPath.Value, cancellationToken);
            var externalGroup =
                new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                    descriptor,
                    attestation,
                    new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                        new PluginName("NpcManagerOutput.esp"),
                        new Sha256Hash(Convert.ToHexString(
                            SHA256.HashData(outputBytes))),
                        outputBytes.LongLength,
                        [new PluginName("Skyrim.esm"), bethesda.ProviderPlugin],
                        members.Select(item => item.WinningForm).ToImmutableArray()),
                    new AssetPath("Data/NPCManager/FaceGeom/external.nif"),
                    new Sha256Hash(Convert.ToHexString(outputFaceGeomHash)),
                    faceGeomBytes.LongLength);

            string selectedPath = Path.Combine(
                packageRootPath,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                    .Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
            Sha256Hash presetHash = HashBytes(
                Encoding.UTF8.GetBytes("preview254-package-preset"));
            RaceMenuNpcFormBinding headPartBinding = new(
                new RecordSignature("HDPT"),
                members[0].OriginForm,
                members[0].WinningForm,
                bethesda.ProviderPlugin,
                bethesda.ProviderPluginPath,
                bethesda.ProviderPluginSha256,
                NpcHeadPartType.Hair);
            var sourceHeadPart = new PresetHeadPart(
                PresetIdentifier.Parse(members[0].WinningForm.ToString()), 0);
            var draft = new RaceMenuPresetRecordAuthorityDraft(
                "preview254-package-test",
                new PresetDocument(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    null!,
                    presetHash,
                    []),
                new RaceMenuPresetTarget(
                    "preview254-package-test",
                    new FormReference(bethesda.ProviderPlugin, new FormId(0x900)),
                    NpcSex.Male,
                    bethesda.DataRoot,
                    [providerAuthority]),
                headPartBinding,
                [new RaceMenuPresetHeadPartAuthority(sourceHeadPart, headPartBinding)],
                headPartBinding,
                null!,
                0,
                new FormId(0x900),
                null!,
                false);
            ImmutableArray<SkyrimAssetAuthority> selectedAssets = assets
                .Select(asset => ToSelectedAssetAuthority(
                    bethesda.DataRoot, asset))
                .ToImmutableArray();
            RaceMenuSelectedDependencyManifestWriteResult selectedWrite =
                await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            presetHash,
                            draft,
                            selectedAssets,
                            [],
                            new WorkspacePath(selectedPath))
                        {
                            ExternalInstallDependencies = [externalGroup]
                        },
                        cancellationToken);
            Require(selectedWrite.Written,
                "The authentic package selected-dependency manifest was refused: " +
                FormatDiagnostics(selectedWrite.Diagnostics));
            byte[] selectedBytes = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);

            string packageManifestPath = Path.Combine(
                packageRootPath, "npcmanager-package.json");
            string selectedHash = Convert.ToHexString(
                SHA256.HashData(selectedBytes));
            string outputHash = Convert.ToHexString(SHA256.HashData(outputBytes));
            string faceGeomHash = Convert.ToHexString(outputFaceGeomHash);
            string packageManifest = $$"""
                {
                  "schemaVersion": 1,
                  "edition": "skyrimse",
                  "presetFormat": "racemenu-jslot",
                  "sourcePreset": "preset.jslot",
                  "sourcePresetSha256": "{{new string('0', 64)}}",
                  "sourcePlugin": "{{bethesda.ProviderPlugin.Value}}",
                  "sourcePluginSha256": "{{bethesda.ProviderPluginSha256.Value}}",
                  "outputPlugin": "NpcManagerOutput.esp",
                  "targetFormId": "0x00000900",
                  "artifacts": [
                    {
                      "kind": "plugin",
                      "relativePath": "NpcManagerOutput.esp",
                      "byteLength": {{outputBytes.LongLength}},
                      "sha256": "{{outputHash}}"
                    },
                    {
                      "kind": "external-headpart-dependencies",
                      "relativePath": "{{RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath}}",
                      "byteLength": {{selectedBytes.LongLength}},
                      "sha256": "{{selectedHash}}"
                    },
                    {
                      "kind": "facegeom",
                      "relativePath": "Data/NPCManager/FaceGeom/external.nif",
                      "byteLength": {{faceGeomBytes.LongLength}},
                      "sha256": "{{faceGeomHash}}"
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var reader = new PackageManifestReader(policy, labRoot);
            var service = new PackageVerifyService(reader);
            var context = new ExternalHeadPartInstallVerificationContext(
                bethesda.DataRoot,
                bethesda.EnabledPluginOrder,
                new FormReference(bethesda.ProviderPlugin, new FormId(0x900)));
            PackageVerifyResult fresh = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                },
                cancellationToken);
            Require(fresh.Verified && fresh.Diagnostics.All(item =>
                        item.Severity != DiagnosticSeverity.Error),
                "Fresh package bytes were not verified: " +
                FormatDiagnostics(fresh.Diagnostics));
            ExternalHeadPartInstallVerificationArtifact freshArtifact =
                fresh.ExternalInstallDependencyVerification ??
                throw new InvalidOperationException(
                    "Fresh package verification produced no external artifact.");
            Require(freshArtifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.Verified &&
                    freshArtifact.DescriptorClosureValid &&
                    freshArtifact.InstallReady &&
                    freshArtifact.InstallDependencyAuthority &&
                    !freshArtifact.RuntimeAuthority &&
                    !freshArtifact.VisualAuthority &&
                    freshArtifact.HistoricalSnapshotValid == true &&
                    freshArtifact.VerifiedInstallSnapshot is not null,
                "Fresh package verification did not establish current install authority: " +
                FormatDiagnostics(fresh.Diagnostics));
            var directVerifier = new BethesdaExternalHeadPartInstallVerifier();
            ExternalHeadPartInstallVerificationResult directTarget =
                await directVerifier.VerifyAsync(
                    new ExternalHeadPartInstallVerificationRequest(
                        new WorkspacePath(packageRootPath),
                        packageOutputPath,
                        new PluginName("NpcManagerOutput.esp"),
                        HashBytes(outputBytes),
                        new WorkspacePath(selectedPath),
                        HashBytes(selectedBytes),
                        context,
                        true,
                        new FormId(0x900)),
                    cancellationToken);
            Require(directTarget.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.Verified,
                "The direct verifier did not accept the exact target actor binding: " +
                FormatDiagnostics(directTarget.Diagnostics));
            ExternalHeadPartInstallVerificationResult wrongTarget =
                await directVerifier.VerifyAsync(
                    new ExternalHeadPartInstallVerificationRequest(
                        new WorkspacePath(packageRootPath),
                        packageOutputPath,
                        new PluginName("NpcManagerOutput.esp"),
                        HashBytes(outputBytes),
                        new WorkspacePath(selectedPath),
                        HashBytes(selectedBytes),
                        context,
                        true,
                        new FormId(0x901)),
                    cancellationToken);
            Require(wrongTarget.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    wrongTarget.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A mismatched target actor FormID was not refused as record drift.");
            ExternalHeadPartInstallVerificationResult wrongPlugin =
                await directVerifier.VerifyAsync(
                    new ExternalHeadPartInstallVerificationRequest(
                        new WorkspacePath(packageRootPath),
                        packageOutputPath,
                        new PluginName("OtherOutput.esp"),
                        HashBytes(outputBytes),
                        new WorkspacePath(selectedPath),
                        HashBytes(selectedBytes),
                        context,
                        true,
                        new FormId(0x900)),
                    cancellationToken);
            Require(wrongPlugin.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    wrongPlugin.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift ||
                        item.Code == ExternalHeadPartDiagnosticCodes.OutputReferenceMissing),
                "An output plugin-name drift was not refused as record drift.");
            async ValueTask<ExternalHeadPartInstallVerificationResult>
                VerifyOutputMutationAsync(
                    bool includeLegitimateFace = false,
                    bool includeExtraTextureSet = false,
                    bool includeSecondFacePair = false,
                    bool includeHeadTextureOnly = false,
                    bool includeFaceTextureMismatch = false,
                    bool includeAdditionalNpc = false,
                    bool includeNonTargetHeadTexture = false,
                    bool includeProviderOwnedFaceTextureBinding = false,
                    bool includeProviderOwnedExtraTextureSet = false,
                    bool includeDuplicateDeclaredHeadPart = false,
                    bool includeProviderOwnedDeclaredOverride = false,
                    bool includeUndeclaredPnamLink = false,
                    bool includeSoleProviderOwnedNpc = false,
                    bool includeOrdinaryPnamInterleave = false,
                    bool includeRaceOrdinaryPnamInterleave = false,
                    bool includeCaseVariantPnam = false,
                    bool includeExtraDuplicatePnam = false,
                    bool omitLastPnam = false,
                    bool reversePnam = false,
                    FormId? target = null,
                    bool requireAuthority = true,
                    PluginName? expectedOutputPluginName = null)
            {
                PluginName mutationOutputPluginName = expectedOutputPluginName ??
                    new PluginName("NpcManagerOutput.esp");
                ImmutableArray<FormReference> mutationPnam = members
                    .Select(item => item.WinningForm)
                    .ToImmutableArray();
                if (omitLastPnam)
                    mutationPnam = mutationPnam.RemoveAt(mutationPnam.Length - 1);
                if (reversePnam)
                    mutationPnam = mutationPnam.Reverse().ToImmutableArray();
                if (includeCaseVariantPnam)
                {
                    mutationPnam = mutationPnam.Select(item =>
                        string.Equals(item.Plugin.Value,
                            bethesda.ProviderPlugin.Value,
                            StringComparison.OrdinalIgnoreCase)
                            ? new FormReference(
                                new PluginName("ORCHIDADORNMENT.ESP"), item.FormId)
                            : item).ToImmutableArray();
                }
                if (includeExtraDuplicatePnam)
                {
                    FormReference duplicate = mutationPnam.First(item =>
                        string.Equals(item.Plugin.Value,
                            bethesda.ProviderPlugin.Value,
                            StringComparison.OrdinalIgnoreCase));
                    mutationPnam = mutationPnam
                        .Concat([duplicate])
                        .ToImmutableArray();
                }
                ImmutableArray<PluginName> mutationMasters =
                    includeRaceOrdinaryPnamInterleave
                        ? [new PluginName("Skyrim.esm"), new PluginName("Race.esp"),
                            includeCaseVariantPnam
                                ? new PluginName("ORCHIDADORNMENT.ESP")
                                : bethesda.ProviderPlugin]
                        : [new PluginName("Skyrim.esm"), bethesda.ProviderPlugin];
                if (includeCaseVariantPnam && !includeRaceOrdinaryPnamInterleave)
                    mutationMasters = [new PluginName("Skyrim.esm"),
                        new PluginName("ORCHIDADORNMENT.ESP")];
                IReadOnlyList<string> mutationMasterNames = mutationMasters
                    .Select(item => item.Value)
                    .ToArray();
                if (includeRaceOrdinaryPnamInterleave)
                    WritePlugin(
                        Path.Combine(bethesda.DataRoot.Value, "Race.esp"),
                        "Race.esp",
                        "Skyrim.esm");
                WriteOutputPlugin(
                    packageOutputPath.Value,
                    "NpcManagerOutput.esp",
                    mutationMasterNames,
                    mutationPnam,
                    includeLegitimateOutputOwnedFace: includeLegitimateFace,
                    includeExtraOutputOwnedTextureSet: includeExtraTextureSet,
                    includeSecondOutputOwnedFacePair: includeSecondFacePair,
                    includeHeadTextureOnly: includeHeadTextureOnly,
                    includeFaceTextureMismatch: includeFaceTextureMismatch,
                    includeAdditionalNpc: includeAdditionalNpc,
                    includeNonTargetHeadTexture: includeNonTargetHeadTexture,
                    includeProviderOwnedFaceTextureBinding: includeProviderOwnedFaceTextureBinding,
                    includeProviderOwnedExtraTextureSet: includeProviderOwnedExtraTextureSet,
                    includeDuplicateDeclaredHeadPart: includeDuplicateDeclaredHeadPart,
                    includeProviderOwnedDeclaredOverride: includeProviderOwnedDeclaredOverride,
                    includeUndeclaredPnamLink: includeUndeclaredPnamLink,
                    includeSoleProviderOwnedNpc: includeSoleProviderOwnedNpc,
                    includeOrdinaryPnamInterleave: includeOrdinaryPnamInterleave,
                    includeRaceOrdinaryPnamInterleave: includeRaceOrdinaryPnamInterleave);
                byte[] mutationOutput = await File.ReadAllBytesAsync(
                    packageOutputPath.Value, cancellationToken);
                File.Delete(selectedPath);
                RaceMenuSelectedDependencyManifestWriteResult mutationWrite =
                    await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                        .WriteAsync(
                            new RaceMenuSelectedDependencyManifestWriteRequest(
                                presetHash,
                                draft,
                                selectedAssets,
                                [],
                                new WorkspacePath(selectedPath))
                            {
                                ExternalInstallDependencies =
                                [externalGroup with
                                {
                                    OutputPlugin = externalGroup.OutputPlugin with
                                    {
                                        Plugin = mutationOutputPluginName,
                                        Sha256 = HashBytes(mutationOutput),
                                        ByteLength = mutationOutput.LongLength,
                                        Masters = mutationMasters
                                    }
                                }]
                            },
                            cancellationToken);
                Require(mutationWrite.Written,
                    "The mutation selected-dependency manifest was refused: " +
                    FormatDiagnostics(mutationWrite.Diagnostics));
                byte[] mutationSelected = await File.ReadAllBytesAsync(
                    selectedPath, cancellationToken);
                try
                {
                    return await directVerifier.VerifyAsync(
                        new ExternalHeadPartInstallVerificationRequest(
                            new WorkspacePath(packageRootPath),
                            packageOutputPath,
                            mutationOutputPluginName,
                            HashBytes(mutationOutput),
                            new WorkspacePath(selectedPath),
                            HashBytes(mutationSelected),
                            context,
                            requireAuthority,
                            target),
                        cancellationToken);
                }
                finally
                {
                    await File.WriteAllBytesAsync(
                        packageOutputPath.Value, outputBytes, cancellationToken);
                    await File.WriteAllBytesAsync(
                        selectedPath, selectedBytes, cancellationToken);
                }
            }
            ExternalHeadPartInstallVerificationResult ambiguousOutput =
                await VerifyOutputMutationAsync(
                    includeAdditionalNpc: true,
                    target: null,
                    requireAuthority: false);
            Require(ambiguousOutput.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    ambiguousOutput.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "An ambiguous output-NPC closure was not refused: " +
                FormatDiagnostics(ambiguousOutput.Diagnostics));
            ExternalHeadPartInstallVerificationResult nullableSoleOutput =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeOrdinaryPnamInterleave: true,
                    includeRaceOrdinaryPnamInterleave: true,
                    target: null,
                    requireAuthority: false);
            Require(nullableSoleOutput.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.Verified,
                "A sole output-owned NPC with a nullable target was not accepted: " +
                FormatDiagnostics(nullableSoleOutput.Diagnostics));
            ExternalHeadPartInstallVerificationResult missingSelectedProvider =
                await VerifyOutputMutationAsync(
                    omitLastPnam: true,
                    target: new FormId(0x900),
                    requireAuthority: false);
            Require(missingSelectedProvider.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    missingSelectedProvider.Diagnostics.Any(item =>
                        item.Message.Contains("selected-provider PNAM sequence",
                            StringComparison.Ordinal)),
                "A missing selected-provider PNAM link was not refused: " +
                FormatDiagnostics(missingSelectedProvider.Diagnostics));
            ExternalHeadPartInstallVerificationResult reorderedSelectedProvider =
                await VerifyOutputMutationAsync(
                    reversePnam: true,
                    target: new FormId(0x900),
                    requireAuthority: false);
            Require(reorderedSelectedProvider.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    reorderedSelectedProvider.Diagnostics.Any(item =>
                        item.Message.Contains("selected-provider PNAM sequence",
                            StringComparison.Ordinal)),
                "A reordered selected-provider PNAM sequence was not refused: " +
                FormatDiagnostics(reorderedSelectedProvider.Diagnostics));
            ExternalHeadPartInstallVerificationResult caseVariantPnam =
                await VerifyOutputMutationAsync(
                    includeCaseVariantPnam: true,
                    target: new FormId(0x900),
                    requireAuthority: false);
            Require(caseVariantPnam.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.Verified,
                "A case-variant selected-provider PNAM identity was not accepted: " +
                FormatDiagnostics(caseVariantPnam.Diagnostics));
            ExternalHeadPartInstallVerificationResult extraDuplicatePnam =
                await VerifyOutputMutationAsync(
                    includeExtraDuplicatePnam: true,
                    target: new FormId(0x900),
                    requireAuthority: false);
            Require(extraDuplicatePnam.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    extraDuplicatePnam.Diagnostics.Any(item =>
                        item.Message.Contains("selected-provider PNAM sequence",
                            StringComparison.Ordinal)),
                "An undeclared duplicate selected-provider PNAM was not refused: " +
                FormatDiagnostics(extraDuplicatePnam.Diagnostics));
            ExternalHeadPartInstallVerificationResult sharedPrivateTexture =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeAdditionalNpc: true,
                    includeNonTargetHeadTexture: true,
                    target: new FormId(0x900));
            Require(sharedPrivateTexture.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    sharedPrivateTexture.Diagnostics.Any(item =>
                        item.Message.Contains("non-target NPC references the target private output TXST",
                            StringComparison.Ordinal)),
                "A non-target NPC sharing the private TXST was not refused: " +
                FormatDiagnostics(sharedPrivateTexture.Diagnostics));
            ExternalHeadPartInstallVerificationResult extraTextureSet =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeExtraTextureSet: true,
                    target: new FormId(0x900));
            Require(extraTextureSet.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    extraTextureSet.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "An extra output-owned TXST was not refused.");
            ExternalHeadPartInstallVerificationResult multipleFacePairs =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeSecondFacePair: true,
                    target: new FormId(0x900));
            Require(multipleFacePairs.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    multipleFacePairs.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "Multiple output-owned Face/TXST pairs were not refused.");
            ExternalHeadPartInstallVerificationResult headTextureOnly =
                await VerifyOutputMutationAsync(
                    includeHeadTextureOnly: true,
                    target: new FormId(0x900));
            Require(headTextureOnly.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    headTextureOnly.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "An NPC HeadTexture-only output TXST was not refused.");
            ExternalHeadPartInstallVerificationResult mismatchedTexture =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeFaceTextureMismatch: true,
                    target: new FormId(0x900));
            Require(mismatchedTexture.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    mismatchedTexture.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A mismatched NPC HeadTexture/private TXST binding was not refused.");
            ExternalHeadPartInstallVerificationResult internalPluginDrift =
                await VerifyOutputMutationAsync(
                    expectedOutputPluginName: new PluginName("OtherOutput.esp"),
                    target: new FormId(0x900));
            Require(internalPluginDrift.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    internalPluginDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A decoded output-plugin identity mismatch was not refused.");
            ExternalHeadPartInstallVerificationResult duplicateDeclaredHeadPart =
                await VerifyOutputMutationAsync(
                    includeDuplicateDeclaredHeadPart: true,
                    target: new FormId(0x900));
            Require(duplicateDeclaredHeadPart.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    duplicateDeclaredHeadPart.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A duplicate declared provider-owned HDPT was not refused.");
            ExternalHeadPartInstallVerificationResult declaredProviderOverride =
                await VerifyOutputMutationAsync(
                    includeProviderOwnedDeclaredOverride: true,
                    target: new FormId(0x900));
            Require(declaredProviderOverride.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    declaredProviderOverride.Diagnostics.Any(item =>
                        item.Message.Contains("provider-owned HDPT override",
                            StringComparison.Ordinal)),
                "A declared provider-owned HDPT override was not refused at the override seam.");
            ExternalHeadPartInstallVerificationResult undeclaredPnam =
                await VerifyOutputMutationAsync(
                    includeUndeclaredPnamLink: true,
                    target: new FormId(0x900));
            Require(undeclaredPnam.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    undeclaredPnam.Diagnostics.Any(item =>
                        item.Message.Contains("undeclared selected-provider PNAM",
                            StringComparison.Ordinal)),
                "An undeclared non-output PNAM link was not refused.");
            ExternalHeadPartInstallVerificationResult providerNpc =
                await VerifyOutputMutationAsync(
                    includeSoleProviderOwnedNpc: true,
                    target: null,
                    requireAuthority: false);
            Require(providerNpc.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    providerNpc.Diagnostics.Any(item =>
                        item.Message.Contains("sole output NPC is not owned by the output plugin",
                            StringComparison.Ordinal)),
                "A sole provider-owned NPC override was not refused.");
            ExternalHeadPartInstallVerificationResult providerFaceTexture =
                await VerifyOutputMutationAsync(
                    includeLegitimateFace: true,
                    includeProviderOwnedFaceTextureBinding: true,
                    target: new FormId(0x900));
            Require(providerFaceTexture.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    providerFaceTexture.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A provider-owned HDPT bound to the private TXST was not refused.");
            ExternalHeadPartInstallVerificationResult providerExtraTexture =
                await VerifyOutputMutationAsync(
                    includeProviderOwnedExtraTextureSet: true,
                    target: new FormId(0x900));
            Require(providerExtraTexture.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    providerExtraTexture.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A provider-owned extra TXST was not refused.");
            foreach (string invalidTarget in new[] { "0x00000000", "0x01000000" })
            {
                string invalidTargetManifest = packageManifest.Replace(
                    "\"targetFormId\": \"0x00000900\"",
                    $"\"targetFormId\": \"{invalidTarget}\"",
                    StringComparison.Ordinal);
                Require(invalidTargetManifest != packageManifest,
                    "The target FormID range mutation did not change the manifest.");
                await File.WriteAllTextAsync(
                    packageManifestPath,
                    invalidTargetManifest,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
                PackageVerifyResult invalidTargetResult = await service.VerifyAsync(
                    new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                    {
                        InstallContext = context,
                        RequireInstallDependencyAuthority = true
                    },
                    cancellationToken);
                    Require(invalidTargetResult.ExternalInstallDependencyVerification is not null &&
                        invalidTargetResult.ExternalInstallDependencyVerification.CurrentInstallDependencyState ==
                            ExternalInstallDependencyState.DeclaredUnverified &&
                        invalidTargetResult.Diagnostics.Any(item =>
                            item.Message.Contains("outside Skyrim's local plugin range",
                                StringComparison.Ordinal)),
                    $"Invalid target FormID '{invalidTarget}' was not refused: " +
                    FormatDiagnostics(invalidTargetResult.Diagnostics));
            }
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            var recordingPhysicsResolver = new RecordingPhysicsBindingResolver(
                physicsResult.Binding!);
            PackageVerifyResult injectedPhysics = await new PackageVerifyService(
                    reader,
                    new BethesdaExternalHeadPartInstallVerifier(
                        new RaceMenuSelectedDependencyManifestReader(),
                        recordingPhysicsResolver))
                .VerifyAsync(
                    new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                    {
                        InstallContext = context,
                        RequireInstallDependencyAuthority = true
                    },
                    cancellationToken);
            Require(injectedPhysics.Verified &&
                    recordingPhysicsResolver.LastMembers.Length == 1 &&
                    recordingPhysicsResolver.LastMembers.All(item =>
                        item.WinningPlugin == bethesda.ProviderPlugin),
                "Injected physics verification did not receive only provider-owned model members.");
            PackageVerifyResult argumentFailure = await new PackageVerifyService(
                    reader, new ThrowingExternalInstallVerifier())
                .VerifyAsync(
                    new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                    {
                        InstallContext = context,
                        RequireInstallDependencyAuthority = true
                    },
                    cancellationToken);
            Require(!argumentFailure.Verified &&
                    argumentFailure.ExternalInstallDependencyVerification is null &&
                    argumentFailure.Diagnostics.Any(item =>
                        item.Code == "external-headpart-package-identity"),
                "An external verifier identity ArgumentException did not fail closed.");
            PackageVerifyResult wrongTargetRace = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context with
                    {
                        TargetRace = new FormReference(
                            bethesda.ProviderPlugin, new FormId(0x901))
                    },
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(wrongTargetRace.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    !wrongTargetRace.ExternalInstallDependencyVerification.InstallReady &&
                    wrongTargetRace.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "Fresh verification did not bind the exact target race identity.");
            PackageVerifyResult contextFree = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath)),
                cancellationToken);
            ExternalHeadPartInstallVerificationArtifact contextFreeArtifact =
                contextFree.ExternalInstallDependencyVerification ??
                throw new InvalidOperationException(
                    "Context-free valid descriptor produced no external artifact.");
            Require(contextFree.Verified &&
                    contextFreeArtifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    !contextFreeArtifact.InstallReady &&
                    !contextFreeArtifact.InstallDependencyAuthority &&
                    contextFreeArtifact.VerifiedInstallSnapshot is null &&
                    contextFreeArtifact.HistoricalSnapshotValid is null,
                "A descriptor-valid context-free invocation upgraded historical/current install authority.");
            ExternalHeadPartInstallVerificationArtifact repeat =
                (await service.VerifyAsync(
                    new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                    {
                        InstallContext = context,
                        RequireInstallDependencyAuthority = true
                    },
                    cancellationToken)).ExternalInstallDependencyVerification!;
            var firstFingerprint =
                freshArtifact.VerifiedInstallSnapshot!.ContextFingerprint;
            var secondFingerprint =
                repeat.VerifiedInstallSnapshot!.ContextFingerprint;
            Require(firstFingerprint.Sha256 == secondFingerprint.Sha256 &&
                    firstFingerprint.Observations.SequenceEqual(
                        secondFingerprint.Observations),
                "Fresh install context fingerprints were not deterministic: " +
                firstFingerprint.Sha256 + " vs " + secondFingerprint.Sha256);
            string packageManifestWithOutputLengthDrift = packageManifest.Replace(
                $"\"byteLength\": {outputBytes.LongLength}",
                $"\"byteLength\": {outputBytes.LongLength + 1}",
                StringComparison.Ordinal);
            Require(packageManifestWithOutputLengthDrift != packageManifest,
                "The package-integrity contradiction mutation did not change the manifest.");
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifestWithOutputLengthDrift,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            PackageVerifyResult packageIntegrityDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            ExternalHeadPartInstallVerificationArtifact packageIntegrityArtifact =
                packageIntegrityDrift.ExternalInstallDependencyVerification ??
                throw new InvalidOperationException(
                    "Package-integrity drift produced no external artifact.");
            Require(!packageIntegrityDrift.Verified &&
                    !packageIntegrityArtifact.PackageIntegrity &&
                    packageIntegrityArtifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    !packageIntegrityArtifact.InstallReady &&
                    !packageIntegrityArtifact.InstallDependencyAuthority &&
                    packageIntegrityArtifact.VerifiedInstallSnapshot is null,
                "Package-byte failure left current external install authority verified: " +
                FormatDiagnostics(packageIntegrityDrift.Diagnostics));
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            string[] observedKinds = firstFingerprint.Observations
                .Select(item => item.Kind)
                .ToArray();
            Require(observedKinds.Contains("enabled-plugin") &&
                    observedKinds.Contains("winning-record") &&
                    observedKinds.Contains("asset") &&
                    observedKinds.Contains("physics-xml") &&
                    observedKinds.Contains("physics-default-bbp"),
                "Fresh fingerprint omitted a plugin, record, asset, XML, or defaultBBP winner observation.");
            ExternalHeadPartInstallObservation[] enabledObservations =
                firstFingerprint.Observations
                    .Where(item => item.Kind == "enabled-plugin")
                    .OrderBy(item => item.Order)
                    .ToArray();
            Require(enabledObservations.Length == bethesda.EnabledPluginOrder.Length &&
                    enabledObservations.Select(item => item.PortableIdentity)
                        .SequenceEqual(bethesda.EnabledPluginOrder.Select(item => item.Value)) &&
                    enabledObservations.All(item => item.ByteLength > 0 &&
                        item.Sha256.Value.Length == 64),
                "Fresh fingerprint did not preserve the complete ordered enabled-plugin identity set.");

            byte[] providerBytes = await File.ReadAllBytesAsync(
                bethesda.ProviderPluginPath.Value, cancellationToken);
            await File.WriteAllBytesAsync(
                bethesda.ProviderPluginPath.Value,
                providerBytes.Concat(new byte[] { 0x01 }).ToArray(),
                cancellationToken);
            PackageVerifyResult providerDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(providerDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    providerDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch),
                "Provider hash/length drift was not typed as unverified.");
            await File.WriteAllBytesAsync(
                bethesda.ProviderPluginPath.Value, providerBytes,
                cancellationToken);

            string winningPatchPath = Path.Combine(
                bethesda.DataRoot.Value, "OrchidAdornmentPatch.esp");
            WriteWinningOverride(
                winningPatchPath,
                bethesda.ProviderPlugin.Value);
            PackageVerifyResult recordDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context with
                    {
                        EnabledPluginOrder =
                        [
                            new PluginName("Skyrim.esm"),
                            bethesda.ProviderPlugin,
                            new PluginName("OrchidAdornmentPatch.esp")
                        ]
                    },
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(recordDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    recordDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "A winning-record override was not typed as record drift.");
            File.Delete(winningPatchPath);

            PackageVerifyResult disabled = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context with
                    {
                        EnabledPluginOrder = [new PluginName("Skyrim.esm")]
                    },
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(disabled.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    disabled.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.ProviderDisabled),
                "A present but disabled provider was not typed as disabled.");

            string missingProviderPath = bethesda.ProviderPluginPath.Value;
            File.Delete(missingProviderPath);
            PackageVerifyResult missing = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(missing.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    missing.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.ProviderMissing),
                "An absent provider was not typed as missing.");
            WriteBytes(missingProviderPath, providerBytes);

            string physicsXmlPath = Path.Combine(
                bethesda.DataRoot.Value,
                physics.PhysicsXml.Value.Replace('/', Path.DirectorySeparatorChar));
            byte[] physicsXmlBytes = await File.ReadAllBytesAsync(
                physicsXmlPath, cancellationToken);
            await File.WriteAllBytesAsync(
                physicsXmlPath,
                physicsXmlBytes.Concat(Encoding.UTF8.GetBytes(" ")).ToArray(),
                cancellationToken);
            PackageVerifyResult physicsDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(physicsDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    physicsDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.PhysicsMissing),
                "Physics XML drift was not typed as a physics refusal.");
            await File.WriteAllBytesAsync(
                physicsXmlPath, physicsXmlBytes, cancellationToken);

            string defaultBbpPath = Path.Combine(
                bethesda.DataRoot.Value,
                physics.DefaultBbpXml.Value.Replace('/', Path.DirectorySeparatorChar));
            byte[] defaultBbpBytes = await File.ReadAllBytesAsync(
                defaultBbpPath, cancellationToken);
            await File.WriteAllBytesAsync(
                defaultBbpPath,
                defaultBbpBytes.Concat(Encoding.UTF8.GetBytes(" ")).ToArray(),
                cancellationToken);
            PackageVerifyResult defaultBbpDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(defaultBbpDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    defaultBbpDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.PhysicsMissing),
                "defaultBBP drift was not typed as a physics refusal.");
            await File.WriteAllBytesAsync(
                defaultBbpPath, defaultBbpBytes, cancellationToken);

            string triPath = Path.Combine(
                bethesda.DataRoot.Value,
                physics.Tri.Value.Replace('/', Path.DirectorySeparatorChar));
            byte[] triBytes = await File.ReadAllBytesAsync(triPath, cancellationToken);
            await File.WriteAllBytesAsync(
                triPath, triBytes.Concat(new byte[] { 0x01 }).ToArray(),
                cancellationToken);
            PackageVerifyResult assetDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(assetDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    assetDrift.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.AssetDrift),
                "Provider asset drift was not typed as an asset refusal.");
            await File.WriteAllBytesAsync(triPath, triBytes, cancellationToken);

            await File.WriteAllBytesAsync(
                archivePath.Value,
                archiveBytes.Concat(new byte[] { 0x01 }).ToArray(),
                cancellationToken);
            PackageVerifyResult archiveDrift = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(archiveDrift.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    archiveDrift.Diagnostics.Any(item =>
                        item.Code is ExternalHeadPartDiagnosticCodes.AssetDrift or
                            ExternalHeadPartDiagnosticCodes.AssetMissing),
                "Provider archive drift was not typed as an asset refusal: " +
                FormatDiagnostics(archiveDrift.Diagnostics));
            await File.WriteAllBytesAsync(
                archivePath.Value, archiveBytes, cancellationToken);

            byte[] collisionArchiveBytes = BuildCaseCollisionBsa(
                "meshes\\actors\\character\\character assets\\hair",
                "orchid-archive.nif",
                "ORCHID-ARCHIVE.NIF",
                [0x41, 0x52, 0x43, 0x48],
                [0x43, 0x4F, 0x4C, 0x4C]);
            await File.WriteAllBytesAsync(
                archivePath.Value, collisionArchiveBytes, cancellationToken);
            var collisionArchiveAsset = archiveAsset with
            {
                ArchiveMember = archiveAsset.ArchiveMember! with
                {
                    ArchiveSha256 = HashBytes(collisionArchiveBytes),
                    ArchiveByteLength = collisionArchiveBytes.LongLength
                }
            };
            ExternalHeadPartDependencyDescriptor collisionDescriptorDraft =
                descriptor with
                {
                    DescriptorId = ZeroHashForTests,
                    Assets = descriptor.Assets
                        .Select(item => item.Path == archiveAsset.Path
                            ? collisionArchiveAsset
                            : item)
                        .ToImmutableArray()
                };
            ExternalHeadPartDependencyDescriptor collisionDescriptor =
                collisionDescriptorDraft with
                {
                    DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeDescriptorId(collisionDescriptorDraft)
                };
            ExternalHeadPartFaceGeomExclusionAttestation collisionAttestationDraft =
                attestation with
                {
                    DescriptorId = collisionDescriptor.DescriptorId,
                    AttestationSha256 = ZeroHashForTests
                };
            ExternalHeadPartFaceGeomExclusionAttestation collisionAttestation =
                collisionAttestationDraft with
                {
                    AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeAttestationHash(collisionAttestationDraft)
                };
            ImmutableArray<SkyrimAssetAuthority> collisionSelectedAssets =
                selectedAssets
                    .Select(item => item.AssetPath == archiveAsset.Path
                        ? ToSelectedAssetAuthority(
                            bethesda.DataRoot, collisionArchiveAsset)
                        : item)
                    .ToImmutableArray();
            File.Delete(selectedPath);
            RaceMenuSelectedDependencyManifestWriteResult collisionWrite =
                await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            presetHash,
                            draft,
                            collisionSelectedAssets,
                            [],
                            new WorkspacePath(selectedPath))
                        {
                            ExternalInstallDependencies =
                            [externalGroup with
                            {
                                Descriptor = collisionDescriptor,
                                Attestation = collisionAttestation
                            }]
                        },
                        cancellationToken);
            Require(collisionWrite.Written,
                "The case-collision selected-dependency fixture could not be written: " +
                FormatDiagnostics(collisionWrite.Diagnostics));
            byte[] collisionSelectedBytes = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);
            ExternalHeadPartInstallVerificationResult collisionResult =
                await new BethesdaExternalHeadPartInstallVerifier().VerifyAsync(
                    new ExternalHeadPartInstallVerificationRequest(
                        new WorkspacePath(packageRootPath),
                        packageOutputPath,
                        new PluginName("NpcManagerOutput.esp"),
                        HashBytes(outputBytes),
                        new WorkspacePath(selectedPath),
                        HashBytes(collisionSelectedBytes),
                        context,
                        true,
                        new FormId(0x900)),
                    cancellationToken);
            Require(collisionResult.Artifact.CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    collisionResult.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.AssetMissing),
                "A case-insensitive archive member collision was not refused.");
            await File.WriteAllBytesAsync(
                archivePath.Value, archiveBytes, cancellationToken);
            await File.WriteAllBytesAsync(
                selectedPath, selectedBytes, cancellationToken);

            WriteOutputPlugin(
                packageOutputPath.Value,
                "NpcManagerOutput.esp",
                ["Skyrim.esm", bethesda.ProviderPlugin.Value],
                members.Select(item => item.WinningForm).ToImmutableArray(),
                includeUndeclaredHeadPart: true);
            byte[] undeclaredOutputBytes = await File.ReadAllBytesAsync(
                packageOutputPath.Value, cancellationToken);
            var undeclaredOutputGroup = externalGroup with
            {
                OutputPlugin = externalGroup.OutputPlugin with
                {
                    Sha256 = HashBytes(undeclaredOutputBytes),
                    ByteLength = undeclaredOutputBytes.LongLength
                }
            };
            File.Delete(selectedPath);
            RaceMenuSelectedDependencyManifestWriteResult undeclaredWrite =
                await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            presetHash,
                            draft,
                            selectedAssets,
                            [],
                            new WorkspacePath(selectedPath))
                        {
                            ExternalInstallDependencies = [undeclaredOutputGroup]
                        },
                        cancellationToken);
            Require(undeclaredWrite.Written,
                "The undeclared-HDPT selected-dependency mutation could not be written: " +
                FormatDiagnostics(undeclaredWrite.Diagnostics));
            long previousSelectedLength = selectedBytes.LongLength;
            string previousSelectedHash = selectedHash;
            selectedBytes = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);
            string undeclaredSelectedHash = Convert.ToHexString(
                SHA256.HashData(selectedBytes));
            string undeclaredOutputHash = Convert.ToHexString(
                SHA256.HashData(undeclaredOutputBytes));
            packageManifest = packageManifest
                .Replace(outputHash, undeclaredOutputHash,
                    StringComparison.Ordinal)
                .Replace(previousSelectedHash, undeclaredSelectedHash,
                    StringComparison.Ordinal)
                .Replace(
                    $"\"byteLength\": {outputBytes.LongLength}",
                    $"\"byteLength\": {undeclaredOutputBytes.LongLength}",
                    StringComparison.Ordinal)
                .Replace(
                    $"\"byteLength\": {previousSelectedLength}",
                    $"\"byteLength\": {selectedBytes.LongLength}",
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            PackageVerifyResult undeclaredHdpt = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(undeclaredHdpt.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    undeclaredHdpt.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "An undeclared output HDPT was not refused as record drift: " +
                FormatDiagnostics(undeclaredHdpt.Diagnostics));

            WriteOutputPlugin(
                packageOutputPath.Value,
                "NpcManagerOutput.esp",
                ["Skyrim.esm", bethesda.ProviderPlugin.Value],
                members.Select(item => item.WinningForm).ToImmutableArray(),
                includeUndeclaredHeadPart: true,
                undeclaredHeadPartOwner: bethesda.ProviderPlugin);
            byte[] providerUndeclaredOutputBytes = await File.ReadAllBytesAsync(
                packageOutputPath.Value, cancellationToken);
            var providerUndeclaredOutputGroup = externalGroup with
            {
                OutputPlugin = externalGroup.OutputPlugin with
                {
                    Sha256 = HashBytes(providerUndeclaredOutputBytes),
                    ByteLength = providerUndeclaredOutputBytes.LongLength
                }
            };
            File.Delete(selectedPath);
            RaceMenuSelectedDependencyManifestWriteResult providerUndeclaredWrite =
                await new RaceMenuSelectedDependencyManifestWriter(policy, labRoot)
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            presetHash,
                            draft,
                            selectedAssets,
                            [],
                            new WorkspacePath(selectedPath))
                        {
                            ExternalInstallDependencies = [providerUndeclaredOutputGroup]
                        },
                        cancellationToken);
            Require(providerUndeclaredWrite.Written,
                "The provider-owned undeclared-HDPT selected-dependency mutation could not be written: " +
                FormatDiagnostics(providerUndeclaredWrite.Diagnostics));
            long previousProviderSelectedLength = selectedBytes.LongLength;
            string previousProviderSelectedHash = undeclaredSelectedHash;
            selectedBytes = await File.ReadAllBytesAsync(
                selectedPath, cancellationToken);
            string providerUndeclaredSelectedHash = Convert.ToHexString(
                SHA256.HashData(selectedBytes));
            string providerUndeclaredOutputHash = Convert.ToHexString(
                SHA256.HashData(providerUndeclaredOutputBytes));
            packageManifest = packageManifest
                .Replace(undeclaredOutputHash, providerUndeclaredOutputHash,
                    StringComparison.Ordinal)
                .Replace(previousProviderSelectedHash, providerUndeclaredSelectedHash,
                    StringComparison.Ordinal)
                .Replace(
                    $"\"byteLength\": {undeclaredOutputBytes.LongLength}",
                    $"\"byteLength\": {providerUndeclaredOutputBytes.LongLength}",
                    StringComparison.Ordinal)
                .Replace(
                    $"\"byteLength\": {previousProviderSelectedLength}",
                    $"\"byteLength\": {selectedBytes.LongLength}",
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                packageManifestPath,
                packageManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            PackageVerifyResult providerUndeclaredHdpt = await service.VerifyAsync(
                new PackageVerifyRequest(new WorkspacePath(packageManifestPath))
                {
                    InstallContext = context,
                    RequireInstallDependencyAuthority = true
                }, cancellationToken);
            Require(providerUndeclaredHdpt.ExternalInstallDependencyVerification!
                        .CurrentInstallDependencyState ==
                        ExternalInstallDependencyState.DeclaredUnverified &&
                    providerUndeclaredHdpt.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
                "An undeclared provider-owned HDPT was not refused as record drift: " +
                FormatDiagnostics(providerUndeclaredHdpt.Diagnostics));

            File.Delete(packageManifestPath);
            Require(!File.Exists(packageManifestPath),
                "The package fixture manifest cleanup escaped its owned root.");
        }
        finally
        {
            if (Directory.Exists(packageRootPath))
                Directory.Delete(packageRootPath, recursive: true);
            if (Directory.Exists(providerRootPath))
                Directory.Delete(providerRootPath, recursive: true);
        }
    }

    private static async ValueTask VerifyOrdinaryPackageRemainsUnprojectedAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            repositoryRoot, "artifacts", "test-work",
            "preview254-package-ordinary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string outputPath = Path.Combine(root, "NpcManagerOutput.esp");
            byte[] outputBytes = [0x4E, 0x50, 0x43, 0x4D];
            await File.WriteAllBytesAsync(outputPath, outputBytes,
                cancellationToken);
            string manifestPath = Path.Combine(root, "npcmanager-package.json");
            string hash = Convert.ToHexString(SHA256.HashData(outputBytes));
            string manifest = $$"""
                {
                  "schemaVersion": 1,
                  "edition": "skyrimse",
                  "presetFormat": "racemenu-jslot",
                  "sourcePreset": "preset.jslot",
                  "sourcePresetSha256": "{{new string('0', 64)}}",
                  "sourcePlugin": "Provider.esp",
                  "sourcePluginSha256": "{{new string('0', 64)}}",
                  "outputPlugin": "NpcManagerOutput.esp",
                  "targetFormId": "0x00000800",
                  "artifacts": [
                    {
                      "kind": "plugin",
                      "relativePath": "NpcManagerOutput.esp",
                      "byteLength": {{outputBytes.LongLength}},
                      "sha256": "{{hash}}"
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(manifestPath, manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var labRoot = new WorkspacePath(repositoryRoot);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var reader = new PackageManifestReader(policy, labRoot);
            PackageVerifyResult result = await new PackageVerifyService(reader)
                .VerifyAsync(new PackageVerifyRequest(new WorkspacePath(manifestPath)),
                    cancellationToken);
            Require(result.Verified &&
                    result.ExternalInstallDependencyVerification is null,
                "An ordinary package must retain package-byte verification without an external projection.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ExternalHeadPartRecordDependency ToDependency(
        SkyrimFaceHeadPartGraphRoute route) =>
        new(
            route.OriginForm,
            route.RequiredOutputMaster,
            route.WinningForm,
            route.WinningPlugin,
            route.WinningPluginSha256,
            route.WinningPluginByteLength,
            route.WinningRecordSha256,
            route.EditorId,
            route.DeclaredType,
            route.EffectiveType,
            route.ModelNif,
            route.TriRoutes,
            route.HnamEdges,
            route.Parent,
            route.Depth,
            route.RouteOrder,
            route.AppliesToSex,
            route.ValidRace);

    private static ExternalHeadPartAssetDependency CreateAsset(
        WorkspacePath dataRoot,
        AssetPath path,
        ExternalHeadPartProviderIdentity provider)
    {
        string physical = Path.Combine(
            dataRoot.Value, path.Value.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = File.ReadAllBytes(physical);
        return new ExternalHeadPartAssetDependency(
            path,
            HashBytes(bytes),
            bytes.LongLength,
            provider.Plugin,
            provider.PluginSha256,
            null);
    }

    private static SkyrimAssetAuthority ToSelectedAssetAuthority(
        WorkspacePath dataRoot,
        ExternalHeadPartAssetDependency asset)
    {
        if (asset.ArchiveMember is { } archive)
        {
            WorkspacePath archivePath = new(Path.Combine(
                dataRoot.Value,
                archive.ArchivePath.Value.Replace('/',
                    Path.DirectorySeparatorChar)));
            return new SkyrimAssetAuthority(
                archive.ArchivePath.Value,
                AssetProviderKind.Archive,
                archivePath,
                archive.ArchiveSha256,
                asset.Path,
                asset.ByteLength,
                asset.Sha256);
        }

        return new SkyrimAssetAuthority(
            "loose",
            AssetProviderKind.Loose,
            new WorkspacePath(Path.Combine(
                dataRoot.Value,
                asset.Path.Value.Replace('/', Path.DirectorySeparatorChar))),
            asset.Sha256,
            asset.Path,
            asset.ByteLength,
            asset.Sha256);
    }

    private static byte[] BuildCaseCollisionBsa(
        string folder,
        string firstName,
        string secondName,
        byte[] firstBytes,
        byte[] secondBytes)
    {
        byte[] folderBytes = Encoding.ASCII.GetBytes(folder);
        byte[] firstNameBytes = Encoding.ASCII.GetBytes(firstName);
        byte[] secondNameBytes = Encoding.ASCII.GetBytes(secondName);
        uint folderNameBytes = checked((uint)(folderBytes.Length + 1));
        uint fileNameBytes = checked((uint)(firstNameBytes.Length +
            secondNameBytes.Length + 2));
        long folderBlockBytes = 1L + folderBytes.Length + 1L + 32L;
        long dataStart = 36L + 24L + folderBlockBytes + fileNameBytes;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("BSA\0"));
        writer.Write(105U);
        writer.Write(36U);
        writer.Write(3U);
        writer.Write(1U);
        writer.Write(2U);
        writer.Write(folderNameBytes);
        writer.Write(fileNameBytes);
        writer.Write(0U);
        writer.Write(0UL);
        writer.Write(2U);
        writer.Write(0U);
        writer.Write(checked((ulong)dataStart));
        writer.Write(checked((byte)(folderBytes.Length + 1)));
        writer.Write(folderBytes);
        writer.Write((byte)0);
        writer.Write(0UL);
        writer.Write(checked((uint)firstBytes.Length));
        writer.Write(checked((uint)dataStart));
        writer.Write(0UL);
        writer.Write(checked((uint)secondBytes.Length));
        writer.Write(checked((uint)(dataStart + firstBytes.LongLength)));
        writer.Write(firstNameBytes);
        writer.Write((byte)0);
        writer.Write(secondNameBytes);
        writer.Write((byte)0);
        writer.Write(firstBytes);
        writer.Write(secondBytes);
        writer.Flush();
        return output.ToArray();
    }

    private static void WritePlugin(
        string path,
        string plugin,
        params string[] masters)
    {
        var mod = new SkyrimMod(
            ModKey.FromNameAndExtension(plugin),
            SkyrimRelease.SkyrimSE);
        foreach (string master in masters)
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master)
            });
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    private static void WriteSkyrimMod(
        SkyrimMod mod,
        string path)
    {
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    private static void WriteOfficialWinningOverride(
        string path,
        AssetPath model,
        AssetPath tri)
    {
        var patchKey = ModKey.FromNameAndExtension("VanillaHairPatch.esp");
        var vanillaKey = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = vanillaKey
        });
        var overridePart = new HeadPart(
            new FormKey(vanillaKey, 0x1000), SkyrimRelease.SkyrimSE)
        {
            EditorID = "VanillaOrchidHairWinningOverride",
            Name = "Vanilla Orchid hair winning override",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(model.Value)
            }
        };
        overridePart.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(tri.Value)
        });
        mod.HeadParts.Add(overridePart);
        WriteSkyrimMod(mod, path);
    }

    private static void WriteOutputPlugin(
        string path,
        string plugin,
        IReadOnlyList<string> masters,
        ImmutableArray<FormReference> pnam,
        bool includeUndeclaredHeadPart = false,
        PluginName? undeclaredHeadPartOwner = null,
        bool includeLegitimateOutputOwnedFace = false,
        bool includeExtraOutputOwnedTextureSet = false,
        bool includeSecondOutputOwnedFacePair = false,
        bool includeHeadTextureOnly = false,
        bool includeFaceTextureMismatch = false,
        bool includeAdditionalNpc = false,
        bool includeNonTargetHeadTexture = false,
        bool includeProviderOwnedFaceTextureBinding = false,
        bool includeProviderOwnedExtraTextureSet = false,
        bool includeDuplicateDeclaredHeadPart = false,
        bool includeProviderOwnedDeclaredOverride = false,
        bool includeUndeclaredPnamLink = false,
        bool includeSoleProviderOwnedNpc = false,
        bool includeOrdinaryPnamInterleave = false,
        bool includeRaceOrdinaryPnamInterleave = false)
    {
        ModKey pluginKey = ModKey.FromNameAndExtension(plugin);
        var mod = new SkyrimMod(
            pluginKey,
            SkyrimRelease.SkyrimSE);
        foreach (string master in masters)
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master)
            });
        ModKey npcOwner = includeSoleProviderOwnedNpc
            ? ModKey.FromNameAndExtension("OrchidAdornment.esp")
            : pluginKey;
        var npc = new Npc(
            new FormKey(npcOwner, 0x900),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "NpcManagerExternalHairNpc"
        };
        if (includeLegitimateOutputOwnedFace || includeHeadTextureOnly)
        {
            if (includeLegitimateOutputOwnedFace)
            {
                var privateFace = new HeadPart(
                    new FormKey(pluginKey, 0x902), SkyrimRelease.SkyrimSE)
                {
                    EditorID = "NpcManagerExternalHairNpc_PrivateFaceHead",
                    Flags = HeadPart.Flag.Male | HeadPart.Flag.Playable,
                    Type = HeadPart.TypeEnum.Face,
                    Model = new Model
                    {
                        File = new AssetLink<SkyrimModelAssetType>(
                            "meshes/actors/character/character assets/hair/orchid-child.nif")
                    },
                    TextureSet = new FormLinkNullable<ITextureSetGetter>(
                        new FormKey(pluginKey, 0x903))
                };
                privateFace.Parts.Add(new Part
                {
                    PartType = Part.PartTypeEnum.Tri,
                    FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                        "meshes/actors/character/character assets/hair/orchid-child.tri")
                });
                mod.HeadParts.Add(privateFace);
            }
            mod.TextureSets.Add(new TextureSet(
                new FormKey(pluginKey, 0x903), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NpcManagerExternalHairNpc_PrivateHeadTXST",
                Flags = TextureSet.Flag.FaceGenTextures |
                    TextureSet.Flag.HasModelSpaceNormalMap,
                Diffuse = "textures/actors/character/character assets/face.dds",
                NormalOrGloss = "textures/actors/character/character assets/face_n.dds"
            });
            npc.HeadTexture = new FormLinkNullable<ITextureSetGetter>(
                new FormKey(pluginKey, 0x903));
        }
        if (includeSecondOutputOwnedFacePair)
        {
            var secondFace = new HeadPart(
                new FormKey(pluginKey, 0x906), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NpcManagerExternalHairNpc_PrivateFaceHead2",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Playable,
                Type = HeadPart.TypeEnum.Face,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(
                        "meshes/actors/character/character assets/hair/orchid-child.nif")
                },
                TextureSet = new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(pluginKey, 0x907))
            };
            secondFace.Parts.Add(new Part
            {
                PartType = Part.PartTypeEnum.Tri,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                    "meshes/actors/character/character assets/hair/orchid-child.tri")
            });
            mod.HeadParts.Add(secondFace);
            mod.TextureSets.Add(new TextureSet(
                new FormKey(pluginKey, 0x907), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NpcManagerExternalHairNpc_PrivateHeadTXST2",
                Flags = TextureSet.Flag.FaceGenTextures |
                    TextureSet.Flag.HasModelSpaceNormalMap,
                Diffuse = "textures/actors/character/character assets/face.dds",
                NormalOrGloss = "textures/actors/character/character assets/face_n.dds"
            });
        }
        if (includeExtraOutputOwnedTextureSet)
        {
            mod.TextureSets.Add(new TextureSet(
                new FormKey(pluginKey, 0x904), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NpcManagerExternalHairNpc_ExtraHeadTXST",
                Flags = TextureSet.Flag.FaceGenTextures |
                    TextureSet.Flag.HasModelSpaceNormalMap,
                Diffuse = "textures/actors/character/character assets/face.dds",
                NormalOrGloss = "textures/actors/character/character assets/face_n.dds"
            });
        }
        int pnamIndex = 0;
        foreach (FormReference reference in pnam)
        {
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(
                    ModKey.FromNameAndExtension(reference.Plugin.Value),
                    reference.FormId.Value)));
            if (includeOrdinaryPnamInterleave && pnamIndex++ == 0)
            {
                npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                    new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        0x1000)));
                npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                    new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        0x1001)));
                if (includeRaceOrdinaryPnamInterleave)
                    npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                        new FormKey(
                            ModKey.FromNameAndExtension("Race.esp"),
                            0x1000)));
            }
        }
        if (includeUndeclaredPnamLink)
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(
                    ModKey.FromNameAndExtension("OrchidAdornment.esp"),
                    0x906)));
        mod.Npcs.Add(npc);
        if (includeLegitimateOutputOwnedFace)
        {
            npc.HeadParts.Insert(
                0,
                new FormLink<IHeadPartGetter>(new FormKey(pluginKey, 0x902)));
            if (includeSecondOutputOwnedFacePair)
                npc.HeadParts.Insert(
                    1,
                    new FormLink<IHeadPartGetter>(new FormKey(pluginKey, 0x906)));
            if (includeFaceTextureMismatch)
                npc.HeadTexture =
                    new FormLinkNullable<ITextureSetGetter>();
        }
        if (includeHeadTextureOnly)
            npc.HeadTexture =
                new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(pluginKey, 0x903));
        if (includeAdditionalNpc)
        {
            mod.Npcs.Add(new Npc(
                new FormKey(pluginKey, 0x901), SkyrimRelease.SkyrimSE)
            {
                EditorID = "NpcManagerExternalHairNpc_Extra"
            });
            if (includeNonTargetHeadTexture)
                mod.Npcs[new FormKey(pluginKey, 0x901)].HeadTexture =
                    new FormLinkNullable<ITextureSetGetter>(
                        new FormKey(pluginKey, 0x903));
        }
        if (includeProviderOwnedDeclaredOverride)
            mod.HeadParts.Add(new HeadPart(
                new FormKey(
                    ModKey.FromNameAndExtension("OrchidAdornment.esp"),
                    0x800),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "DeclaredProviderOverride",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair
            });
        if (includeProviderOwnedFaceTextureBinding)
            mod.HeadParts.Add(new HeadPart(
                new FormKey(
                    ModKey.FromNameAndExtension("OrchidAdornment.esp"),
                    0x800),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "ProviderFaceTextureBinding",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair,
                TextureSet = new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(pluginKey, 0x903))
            });
        if (includeProviderOwnedExtraTextureSet)
            mod.TextureSets.Add(new TextureSet(
                new FormKey(
                    ModKey.FromNameAndExtension("OrchidAdornment.esp"),
                    0x904),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "ProviderExtraHeadTXST",
                Flags = TextureSet.Flag.FaceGenTextures |
                    TextureSet.Flag.HasModelSpaceNormalMap,
                Diffuse = "textures/actors/character/character assets/face.dds",
                NormalOrGloss = "textures/actors/character/character assets/face_n.dds"
            });
        if (includeDuplicateDeclaredHeadPart)
            mod.HeadParts.Add(new HeadPart(
                new FormKey(
                    ModKey.FromNameAndExtension("OrchidAdornment.esp"),
                    0x800),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "DuplicateDeclaredProviderHair",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair
            });
        if (includeUndeclaredHeadPart)
        {
            ModKey owner = ModKey.FromNameAndExtension(
                (undeclaredHeadPartOwner ?? new PluginName(plugin)).Value);
            mod.HeadParts.Add(new HeadPart(
                new FormKey(owner, 0x901), SkyrimRelease.SkyrimSE)
            {
                EditorID = "UndeclaredOutputHair",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair
            });
        }
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    private static void AddUnusedProviderHeadPart(
        string path,
        uint formId = 0x901,
        HeadPart.TypeEnum type = HeadPart.TypeEnum.Hair)
    {
        SkyrimMod mod;
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(
                   path, SkyrimRelease.SkyrimSE))
        {
            mod = (SkyrimMod)overlay.DeepCopy();
        }
        ModKey providerKey = ModKey.FromNameAndExtension("OrchidAdornment.esp");
        var unused = new HeadPart(
            new FormKey(providerKey, formId), SkyrimRelease.SkyrimSE)
        {
            EditorID = type == HeadPart.TypeEnum.Eyes
                ? "OrchidUnusedEyes"
                : "OrchidUnusedHair",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = type,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "meshes/actors/character/character assets/hair/orchid-child.nif")
            }
        };
        unused.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                "meshes/actors/character/character assets/hair/orchid-child.tri")
        });
        mod.HeadParts.Add(unused);
        WriteSkyrimMod(mod, path);
    }

    private static void WriteWinningOverride(
        string path,
        string providerPlugin)
    {
        var mod = new SkyrimMod(
            ModKey.FromNameAndExtension("OrchidAdornmentPatch.esp"),
            SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        ModKey providerKey = ModKey.FromNameAndExtension(providerPlugin);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = providerKey
        });
        var root = new HeadPart(
            new FormKey(providerKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRootHairOverride",
            Name = "Orchid root hair override",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair
        };
        root.ExtraParts.Add(new FormLink<IHeadPartGetter>(
            new FormKey(providerKey, 0x801)));
        mod.HeadParts.Add(root);
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            item.Code + ":" + item.Message));

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static byte[] LoadSchema3GoldenBytes()
    {
        FieldInfo? field = typeof(Program).GetField(
            "ExpectedSchema3CanonicalBase64",
            BindingFlags.Static | BindingFlags.NonPublic);
        string? value = field?.GetRawConstantValue() as string;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "The schema-3 golden fixture is unavailable.");
        return Convert.FromBase64String(value);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                (File.Exists(Path.Combine(current.FullName, ".git")) ||
                 Directory.Exists(Path.Combine(current.FullName, ".git"))))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the lane repository root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
