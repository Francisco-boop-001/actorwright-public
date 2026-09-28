using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpJslotSelectionScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    private enum VerificationArtifactVariant
    {
        ValidCreate,
        MalformedFingerprint,
        HistoricalFalse
    }

    public string Selector => "--test-jslot-external-smp-selection";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        RunScenarioAsync(firstUse: false, cancellationToken);

    internal static ValueTask RunFirstUseAsync(
        CancellationToken cancellationToken) =>
        RunScenarioAsync(firstUse: true, cancellationToken);

    private static async ValueTask RunScenarioAsync(
        bool firstUse,
        CancellationToken cancellationToken)
    {
        string rootValue = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "preview254-task11-" + Guid.NewGuid().ToString("N"));
        var root = new WorkspacePath(rootValue);
        Directory.CreateDirectory(root.Value);
        try
        {
            var presetPath = new WorkspacePath(Path.Combine(root.Value, "Hair.jslot"));
            byte[] presetBytes = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(presetPath.Value, presetBytes,
                cancellationToken);
            Sha256Hash presetHash = Hash(presetBytes);
            var dataRoot = new WorkspacePath(Path.Combine(root.Value, "data"));
            Directory.CreateDirectory(dataRoot.Value);
            var provider = new PluginName("ExternalHair.esp");
            var providerPath = new WorkspacePath(Path.Combine(dataRoot.Value,
                provider.Value));
            await File.WriteAllBytesAsync(providerPath.Value, [9, 8, 7],
                cancellationToken);
            var target = new RaceMenuPresetTarget(
                "reviewed-intake-fingerprint:external-hair",
                new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746)),
                NpcSex.Female,
                dataRoot,
                [new SkyrimFaceRecordPluginAuthority(
                    provider, providerPath, Hash([9, 8, 7]))]);
            var source = new PresetDocument(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                new PresetAppearance(
                    1,
                    [
                        new PresetHeadPart(
                            PresetIdentifier.Parse(provider.Value + "|0x00000801"),
                            1),
                        new PresetHeadPart(
                            PresetIdentifier.Parse(provider.Value + "|0x00000800"),
                            2)
                    ],
                    PresetHairColor.FromPackedRgb(0x112233),
                    new PresetWeight(50, null, null, null),
                    ImmutableDictionary<string, float>.Empty,
                    ImmutableDictionary<string, float>.Empty,
                     ImmutableDictionary<string, float>.Empty,
                    Enumerable.Repeat(0F, 19).ToImmutableArray(), [], [],
                     null,
                     new PresetFieldPresence(
                        true, true, true, true, true, true, true, true, true),
                    [],
                    RaceMenu: new RaceMenuPresetData(
                        null, [0U, 0U, 0U, 0U], 1, [],
                        ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty,
                        [], [], [])),
                presetHash,
                []);
            var rootForm = new FormReference(provider, new FormId(0x800));
            var route = new SkyrimFaceRecordRoute(
                new SkyrimRaceFaceRecordRoute(
                    target.Race,
                    new SkyrimFaceRecordProvider(provider, providerPath,
                        target.PluginOrder[0].ExpectedSha256),
                    "Skyrim",
                    null,
                    "Skyrim",
                    [], [], [], []),
                [rootForm],
                [new SkyrimFaceHeadPartRecordRoute(
                    rootForm,
                    new SkyrimFaceRecordProvider(provider, providerPath,
                        target.PluginOrder[0].ExpectedSha256),
                    "ExternalHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
                    new AssetPath("meshes/external/hair.nif"), [], [], null, 0,
                    true, false)]);
            route = route with
            {
                HeadPartGraph = [new SkyrimFaceHeadPartGraphRoute(
                    rootForm, provider, rootForm, provider,
                    target.PluginOrder[0].ExpectedSha256, 3, Hash([1]),
                    "ExternalHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair,
                    new AssetPath("meshes/external/hair.nif"), [], [], null, 0,
                    true, false, 0, NpcSex.Female, null)]
            };
            ExternalHeadPartDependencyDescriptor descriptor = CreateDescriptor(
                provider, rootForm);
            var presetService = new CapturingPresetService(source);
            var discovery = new AcceptedDiscovery(descriptor);
            var service = new RaceMenuJslotExternalHeadPartPrecheckService(
                presetService,
                new FixedPluginAuthorityLoader(target.PluginOrder),
                new FixedRouteResolver(route),
                discovery);

            if (firstUse)
            {
                JslotRouteRun firstUseRun = await RunJslotBuildAsync(
                    root, presetPath, presetHash, source, target, providerPath,
                    descriptor, null, new CapturingAcceptancePrecheck(descriptor),
                    failManifestReadback: false, cancellationToken,
                    supplyExternalInstallDependencies: false,
                    verificationArtifactVariant: VerificationArtifactVariant.ValidCreate);
                Require(firstUseRun.Result.Completed &&
                        Directory.Exists(firstUseRun.OutputRoot.Value) &&
                        firstUseRun.Result.Execution?.ExternalInstallPrepublication is not null &&
                        firstUseRun.Selection.LastRequest is
                        {
                            JslotOutputBindingProbe: null,
                            ExternalInstallDependencies.Length: 1
                        } && firstUseRun.NpcBuild.Calls == 2 &&
                        firstUseRun.NpcBuild.LastProbeResult is
                        {
                            Completed: false,
                            Build: null,
                            ExternalInstallOutputPluginProbe: not null,
                            ExternalInstallPrepublication: null
                        } && firstUseRun.OutputBindingReader.LastRead is
                        {
                            Accepted: true,
                            FullPnam.Length: 2,
                            Binding.PnamBindings.Length: 2
                        } && firstUseRun.Selection.LastRequest
                            .ExternalInstallDependencies[0].OutputPlugin
                            .PnamBindings.Length == 1 &&
                        firstUseRun.Verifier.Calls == 1 &&
                        firstUseRun.Verifier.LastRequest is
                        {
                            SelectedManifestPath: var selectedManifest,
                            PackageRoot: var packageRoot,
                            Context: not null,
                            CreatePrepublication: true,
                            TargetActorFormId: not null
                        } && selectedManifest.IsUnder(packageRoot) &&
                        firstUseRun.Result.Execution is
                        {
                            ExternalInstallPrepublication: {
                                Verification: {
                                    SchemaIdentifier:
                                        ExternalHeadPartSchemaIdentifiers.InstallVerification,
                                    PackageIntegrity: true,
                                    DescriptorClosureValid: true,
                                    CurrentInstallDependencyState:
                                        ExternalInstallDependencyState.Verified,
                                    InstallReady: true,
                                    InstallDependencyAuthority: true,
                                    RuntimeAuthority: false,
                                    VisualAuthority: false,
                                    VerifiedInstallSnapshot: not null,
                                    HistoricalSnapshotValid: null
                                }
                            }
                        },
                    "A raw first-use JSlot could not complete without a pre-bound ExternalInstallDependencies group: " +
                        Diagnostics(firstUseRun.Result.Diagnostics));

                JslotRouteRun malformedFingerprint = await RunJslotBuildAsync(
                    root, presetPath, presetHash, source, target, providerPath,
                    descriptor, null, new CapturingAcceptancePrecheck(descriptor),
                    failManifestReadback: false, cancellationToken,
                    supplyExternalInstallDependencies: false,
                    verificationArtifactVariant:
                        VerificationArtifactVariant.MalformedFingerprint);
                Require(!malformedFingerprint.Result.Completed &&
                        malformedFingerprint.Result.Diagnostics.Any(item =>
                            item.Code == "jslot-npc-prepublication-verification"),
                    "A malformed install context fingerprint was accepted by the JSlot prepublication gate.");

                JslotRouteRun historicalFalse = await RunJslotBuildAsync(
                    root, presetPath, presetHash, source, target, providerPath,
                    descriptor, null, new CapturingAcceptancePrecheck(descriptor),
                    failManifestReadback: false, cancellationToken,
                    supplyExternalInstallDependencies: false,
                    verificationArtifactVariant:
                        VerificationArtifactVariant.HistoricalFalse);
                Require(!historicalFalse.Result.Completed &&
                        historicalFalse.Result.Diagnostics.Any(item =>
                            item.Code == "jslot-npc-prepublication-verification"),
                    "A false historical snapshot validity marker was accepted for create prepublication.");
                return;
            }

            await AssertCausalCompanionFailureAsync(
                root, presetPath, presetHash, source, target, providerPath,
                descriptor, cancellationToken);

            RaceMenuJslotExternalHeadPartPrecheckResult result =
                await service.PrecheckAsync(
                    new RaceMenuJslotExternalHeadPartPrecheckRequest(
                        presetPath, presetHash, target, null),
                    cancellationToken);

            Require(result.Status == RaceMenuJslotExternalHeadPartPrecheckStatus.Accepted,
                "Task 11 precheck did not accept the exact external Hair route: " +
                string.Join(" | ", result.Diagnostics.Select(item => item.Message)));
            Require(result.Acceptance is
            {
                PresetSha256: var acceptedHash,
                ReviewedTarget: var reviewedTarget,
                Descriptor: var acceptedDescriptor
            } && acceptedHash == presetHash && reviewedTarget == target &&
                acceptedDescriptor.DescriptorId == descriptor.DescriptorId,
                "Task 11 precheck did not return a hash-bound receipt for the reviewed target and descriptor.");
            Require(presetService.Calls == 1 && discovery.Calls == 1,
                "Task 11 precheck did not perform exactly one preset read and one discovery call.");

            await RunJslotBuildRoutesAsync(
                root, presetPath, presetHash, source, target, providerPath,
                descriptor, cancellationToken);
            await AssertErrorBearingNotApplicableRefusalAsync(
                presetPath, presetHash, source, target, cancellationToken);
            await AssertResumedDriftRefusalAsync(
                root, presetPath, presetHash, source, target, providerPath,
                descriptor, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(root.Value))
                Directory.Delete(root.Value, recursive: true);
        }
    }

    private static async ValueTask RunJslotBuildRoutesAsync(
        WorkspacePath root,
        WorkspacePath presetPath,
        Sha256Hash presetHash,
        PresetDocument preset,
        RaceMenuPresetTarget target,
        WorkspacePath providerPath,
        ExternalHeadPartDependencyDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var directPrecheck = new CapturingAcceptancePrecheck(descriptor);
        JslotRouteRun direct = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null, directPrecheck, failManifestReadback: false,
            cancellationToken);
        Require(direct.Result.Completed,
            "Direct JSlot route did not complete through companion, selection, and package promotion: " +
            Diagnostics(direct.Result.Diagnostics));
        Require(directPrecheck.Calls == 1 && direct.PrecheckAcceptance is not null,
            "Direct JSlot route did not perform exactly one precheck and retain its receipt.");
        Require(direct.Baker.TypedCalls == 1 && direct.Baker.LastExpectedDescriptor is not null,
            "Direct JSlot route did not pass the accepted descriptor to the typed native baker.");
        Require(direct.Selection.SelectedManifestReadCalls == 1 &&
                direct.Selection.LastSchema8Readback,
            "Direct JSlot route did not reopen canonical schema-3 evidence and schema-8 standalone authority.");
        Require(direct.Selection.LastRequest is
            {
                ExternalHeadPartDependencies.Length: 1,
                ExternalHeadPartExclusionAttestations.Length: 1,
                ExternalInstallDependencies.Length: 1,
                ManagerOwnedFaceGeomCarrier: not null
            },
            "Direct JSlot route did not carry descriptor, attestation, output-plugin group, and carrier evidence into selection.");
        var selectedDescriptor = direct.Selection.LastRequest!
            .ExternalHeadPartDependencies[0];
        var selectedAttestation = direct.Selection.LastRequest!
            .ExternalHeadPartExclusionAttestations[0];
        var selectedGroup = direct.Selection.LastRequest!
            .ExternalInstallDependencies[0];
        Require(selectedDescriptor.Provider.Plugin == target.PluginOrder[0].Plugin &&
                selectedDescriptor.Members[0].WinningPlugin ==
                    target.PluginOrder[0].Plugin &&
                selectedDescriptor.Members[0].EffectiveType == NpcHeadPartType.Hair &&
                selectedGroup.OutputPlugin.Masters.Contains(
                    target.PluginOrder[0].Plugin) &&
                selectedGroup.OutputPlugin.PnamBindings.Contains(
                    selectedDescriptor.Members[0].WinningForm) &&
                selectedAttestation.AttestationSha256 ==
                    ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(
                        selectedAttestation),
            "Selection evidence lost the external provider master, PNAM/HDPT binding, or canonical attestation hash.");

        var suppliedAcceptance = direct.PrecheckAcceptance! with
        {
            ReviewedTarget = target
        };
        var suppliedPrecheck = new CapturingAcceptancePrecheck(descriptor);
        JslotRouteRun supplied = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, suppliedAcceptance, suppliedPrecheck,
            failManifestReadback: false, cancellationToken);
        Require(supplied.Result.Completed,
            "Supplied-acceptance JSlot route did not complete: " +
            Diagnostics(supplied.Result.Diagnostics));
        Require(suppliedPrecheck.Calls == 0,
            "Supplied-acceptance JSlot route resumed by rediscovering the precheck instead of using the receipt.");
        Require(supplied.Baker.TypedCalls == 1 &&
                supplied.Selection.LastRequest?.Target.AuthorityId ==
                    target.AuthorityId &&
                direct.Selection.LastRequest?.Target.AuthorityId !=
                    target.AuthorityId,
            "Supplied-acceptance JSlot route did not retain its opaque intake-fingerprint target while independently rediscovering native evidence.");

        JslotRouteRun rollback = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null, new CapturingAcceptancePrecheck(descriptor),
            failManifestReadback: true, cancellationToken);
        Require(!rollback.Result.Completed &&
                !Directory.Exists(rollback.CompanionRoot.Value) &&
                !Directory.Exists(rollback.OutputRoot.Value),
            "Selection rollback did not remove the incomplete companion transaction and leave final output absent.");

        var outsideCarrier = new WorkspacePath(Path.Combine(
            Environment.CurrentDirectory, "artifacts",
            "preview254-task11-outside-carrier-" + Guid.NewGuid().ToString("N") +
            ".nif"));
        byte[] outsideCarrierBytes = [61, 62, 63, 64];
        await File.WriteAllBytesAsync(
            outsideCarrier.Value, outsideCarrierBytes, cancellationToken);
        try
        {
            JslotRouteRun containment = await RunJslotBuildAsync(
                root, presetPath, presetHash, preset, target, providerPath,
                descriptor, null, new CapturingAcceptancePrecheck(descriptor),
                failManifestReadback: false,
                cancellationToken,
                new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                    outsideCarrier, Hash(outsideCarrierBytes)));
            Require(!containment.Result.Completed &&
                    containment.Result.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.DescriptorLost) &&
                    !Directory.Exists(containment.CompanionRoot.Value) &&
                    !Directory.Exists(containment.OutputRoot.Value),
                "An external Manager carrier outside the lab was admitted instead of refusing before selection.");
        }
        finally
        {
            if (File.Exists(outsideCarrier.Value))
                File.Delete(outsideCarrier.Value);
        }

        JslotRouteRun unknownStatus = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null,
            new CapturingAcceptancePrecheck(
                descriptor,
                (RaceMenuJslotExternalHeadPartPrecheckStatus)99),
            failManifestReadback: false, cancellationToken);
        Require(!unknownStatus.Result.Completed &&
                unknownStatus.Result.Diagnostics.Any(item =>
                    item.Code == "external-headpart-precheck-refused") &&
                unknownStatus.RecordBuilder.Calls == 0,
            "An unknown precheck status was not closed before companion creation.");

        JslotRouteRun cleanRefused = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null,
            new CapturingAcceptancePrecheck(
                descriptor,
                RaceMenuJslotExternalHeadPartPrecheckStatus.Refused),
            failManifestReadback: false, cancellationToken);
        Require(!cleanRefused.Result.Completed &&
                cleanRefused.Result.Diagnostics.Any(item =>
                    item.Code == "external-headpart-precheck-refused") &&
                cleanRefused.RecordBuilder.Calls == 0,
            "A clean Refused precheck was admitted before companion creation.");
    }

    private static async ValueTask AssertCausalCompanionFailureAsync(
        WorkspacePath root,
        WorkspacePath presetPath,
        Sha256Hash presetHash,
        PresetDocument preset,
        RaceMenuPresetTarget target,
        WorkspacePath providerPath,
        ExternalHeadPartDependencyDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        JslotRouteRun snapshotFailure = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null, new CapturingAcceptancePrecheck(descriptor),
            failManifestReadback: false, cancellationToken,
            failFaceMorphSnapshot: true);
        Require(!snapshotFailure.Result.Completed &&
                snapshotFailure.Result.Diagnostics.Any(item =>
                    item.Code == "face-morph-snapshot-read-failed") &&
                !snapshotFailure.Result.Diagnostics.Any(item =>
                    item.Code == "jslot-npc-dependency-authority") &&
                snapshotFailure.Result.CompanionBuild is
                {
                    Completed: false,
                    Companion: null,
                    FaceGen: null
                } &&
                !Directory.Exists(snapshotFailure.CompanionRoot.Value),
            "A failed companion snapshot read invented a dependency-closure diagnostic or retained companion output: " +
            Diagnostics(snapshotFailure.Result.Diagnostics));

        JslotRouteRun malformedArtifact = await RunJslotBuildAsync(
            root, presetPath, presetHash, preset, target, providerPath,
            descriptor, null, new CapturingAcceptancePrecheck(descriptor),
            failManifestReadback: false, cancellationToken,
            malformedFaceGenArtifact: true);
        Require(!malformedArtifact.Result.Completed &&
                malformedArtifact.Result.Diagnostics.Any(item =>
                    item.Code == "jslot-npc-dependency-authority") &&
                malformedArtifact.Result.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.DescriptorLost) &&
                malformedArtifact.Result.CompanionBuild is
                {
                    Completed: true,
                    Companion: not null,
                    FaceGen: { Artifact: not null }
                } &&
                !Directory.Exists(malformedArtifact.CompanionRoot.Value),
            "A completed companion with malformed dependency evidence did not retain the real closure refusal: " +
            Diagnostics(malformedArtifact.Result.Diagnostics));
    }

    private static async ValueTask<JslotRouteRun> RunJslotBuildAsync(
        WorkspacePath root,
        WorkspacePath presetPath,
        Sha256Hash presetHash,
        PresetDocument preset,
        RaceMenuPresetTarget target,
        WorkspacePath providerPath,
        ExternalHeadPartDependencyDescriptor descriptor,
        RaceMenuJslotExternalHeadPartPrecheckAcceptance? suppliedAcceptance,
        CapturingAcceptancePrecheck precheck,
        bool failManifestReadback,
        CancellationToken cancellationToken,
        RaceMenuManagerOwnedFaceGeomCarrierAuthority? carrierOverride = null,
        bool supplyExternalInstallDependencies = true,
        VerificationArtifactVariant verificationArtifactVariant =
            VerificationArtifactVariant.ValidCreate,
        bool failFaceMorphSnapshot = false,
        bool malformedFaceGenArtifact = false)
    {
        string runId = Guid.NewGuid().ToString("N");
        var companionRoot = new WorkspacePath(Path.Combine(
            root.Value, "jslot-companion-" + runId));
        var outputRoot = new WorkspacePath(Path.Combine(
            root.Value, "jslot-output-" + runId));
        var currentAssetManifest = new WorkspacePath(Path.Combine(
            root.Value, "current-assets-" + runId + ".json"));
        await File.WriteAllBytesAsync(currentAssetManifest.Value, [1, 2, 3],
            cancellationToken);
        Sha256Hash currentAssetHash = Hash([1, 2, 3]);
        var providerHash = target.PluginOrder[0].ExpectedSha256;
        var provider = target.PluginOrder[0].Plugin;
        var providerRecord = new SkyrimFaceRecordProvider(
            provider, providerPath, providerHash);
        var currentAssets = new RaceMenuNpcStandaloneAssets(
            "task11-current",
            new RaceMenuNpcNam9TrailingAuthority(
                providerPath, providerHash, new FormId(0x800), 0.5F),
            new SkyrimPrivateHeadTexturePaths(
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds")),
            1, 1, [], null, [], null)
        {
            SchemaVersion = 8,
            ExternalHeadPartDependencies = [descriptor],
            ExternalHeadPartExclusionAttestations = []
        };
        var providerContext = new BlankNpcProviderBindingRequest(
            providerPath, providerHash, GameEdition.SkyrimSpecialEdition,
            NpcSex.Female, providerPath, providerHash, new FormId(0x800),
            providerPath, providerHash, providerPath, root, providerPath);
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new RaceMenuNpcPresetBundle(
                providerPath, providerHash, presetPath, presetHash,
                providerPath, providerHash, providerPath, providerHash,
                new RaceMenuNpcRecordAuthority(providerPath, providerHash)),
            providerContext,
            outputRoot,
            new PluginName("Task11Output.esp"),
            new NpcCreationIdentity(new EditorId("Task11Npc"),
                new NpcName("Task 11 NPC")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female, NpcCreationRole.Follower, true, false,
                true, false, true),
            new SkyrimNpcCreationReferences(
                target.Race, target.Race, target.Race, target.Race, null),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 0, 0, 50, 50, 50,
                1F, 50F, 255));
        var currentRequest = new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(
                currentAssetManifest, currentAssetHash));
        var routePrecheck = suppliedAcceptance is null ? precheck : null;
        var bake = new CapturingExternalFaceGenBaker(
            descriptor, providerPath, providerHash,
            malformedFaceGenArtifact);
        var recordBuilder = new FixedRecordAuthorityBuilder(providerPath);
        var companion = new RaceMenuJslotCompanionBuildService(
            recordBuilder,
            new FixedStandaloneAuthorityReader(currentAssets),
            new FixedFaceMorphSnapshotService(failFaceMorphSnapshot),
            new FakeNpcCreationService(),
            bake,
            new PermissiveWorkspacePolicy(), root);
        var selection = new SelectionHarness(
            preset, currentAssets, providerRecord,
            failManifestReadback, carrierOverride);
        var selectionService = selection.CreateService(root);
        ImmutableArray<FormReference> fullPnam = descriptor.Members
            .Select(item => item.WinningForm)
            .Append(new FormReference(
                new PluginName("Skyrim.esm"), new FormId(0x1000)))
            .ToImmutableArray();
        RaceMenuSelectedDependencyManifestOutputPluginBinding expectedBinding =
            supplyExternalInstallDependencies
                ? CreateExternalInstallDependency(
                    descriptor, bake.Attestation,
                    new PluginName("Task11Output.esp")).OutputPlugin
                : new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                    new PluginName("Task11Output.esp"),
                    Hash([81, 82, 83]),
                    3,
                    [new PluginName("ExternalHair.esp")],
                    descriptor.Members.Select(item => item.WinningForm)
                        .ToImmutableArray());
        var outputBindingReader = supplyExternalInstallDependencies
            ? new CapturingOutputPluginBindingReader(expectedBinding)
            : new CapturingOutputPluginBindingReader(
                new RaceMenuJslotOutputPluginBindingReader());
        var verifier = new CapturingExternalInstallVerifier(
            descriptor, verificationArtifactVariant);
        var npcBuild = new FakeNpcBuildService(
            currentAssets,
            writeBethesdaPlugin: !supplyExternalInstallDependencies,
            fullPnam);
        var service = new RaceMenuJslotNpcBuildService(
            new CapturingPresetService(preset),
            new FixedPluginAuthorityLoader(target.PluginOrder),
            companion,
            selectionService,
            npcBuild,
            new PermissiveWorkspacePolicy(), root,
            null, routePrecheck, outputBindingReader, verifier);
        var request = new RaceMenuJslotNpcBuildRequest(
            currentRequest, presetPath, presetHash, target.DataRoot,
            target.PluginOrder.Select(item => item.Plugin).ToImmutableArray(),
            companionRoot)
        {
            AcceptedExternalHeadPartPrecheck = suppliedAcceptance,
            ExternalInstallDependencies = supplyExternalInstallDependencies
                ? [CreateExternalInstallDependency(
                    descriptor, bake.Attestation, new PluginName("Task11Output.esp"))]
                : []
        };
        RaceMenuJslotNpcBuildResult result = await service.ExecuteAsync(
            request, null, cancellationToken);
        return new JslotRouteRun(
            result, companionRoot, outputRoot, precheck,
            suppliedAcceptance ?? precheck.Acceptance, bake, selection,
            recordBuilder, npcBuild, verifier, outputBindingReader);
    }

    private static async ValueTask AssertErrorBearingNotApplicableRefusalAsync(
        WorkspacePath presetPath,
        Sha256Hash presetHash,
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken)
    {
        var service = new RaceMenuJslotExternalHeadPartPrecheckService(
            new CapturingPresetService(preset),
            new FixedPluginAuthorityLoader(target.PluginOrder),
            new FixedRouteResolver(new SkyrimFaceRecordRoute(null!, [], [])),
            new ErrorNotApplicableDiscovery());
        RaceMenuJslotExternalHeadPartPrecheckResult result =
            await service.PrecheckAsync(
                new RaceMenuJslotExternalHeadPartPrecheckRequest(
                    presetPath, presetHash, target, null), cancellationToken);
        Require(result.Status == RaceMenuJslotExternalHeadPartPrecheckStatus.Refused &&
                result.Acceptance is null &&
                result.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
            "Error-bearing NotApplicable discovery was not converted to Refused.");
    }

    private static async ValueTask AssertResumedDriftRefusalAsync(
        WorkspacePath root,
        WorkspacePath presetPath,
        Sha256Hash presetHash,
        PresetDocument preset,
        RaceMenuPresetTarget target,
        WorkspacePath providerPath,
        ExternalHeadPartDependencyDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var providerDrift = target.PluginOrder[0] with
        {
            ExpectedSha256 = Hash([99, 98, 97])
        };
        var providerDriftTarget = target with { PluginOrder = [providerDrift] };
        var providerDriftService = new RaceMenuJslotExternalHeadPartPrecheckService(
            new CapturingPresetService(preset),
            new FixedPluginAuthorityLoader(providerDriftTarget.PluginOrder),
            new FixedRouteResolver(new SkyrimFaceRecordRoute(null!, [], [])),
            new AcceptedDiscovery(descriptor));
        RaceMenuJslotExternalHeadPartPrecheckResult providerResult =
            await providerDriftService.PrecheckAsync(
                new RaceMenuJslotExternalHeadPartPrecheckRequest(
                    presetPath, presetHash, target, descriptor), cancellationToken);
        Require(providerResult.Status == RaceMenuJslotExternalHeadPartPrecheckStatus.Refused &&
                providerResult.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.RecordDrift),
            "Resumed provider/record drift was not refused before companion creation.");

        ExternalHeadPartDependencyDescriptor descriptorDrift = descriptor with
        {
            GraphSha256 = Hash([201, 202, 203])
        };
        descriptorDrift = descriptorDrift with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptorDrift)
        };
        var descriptorDriftService = new RaceMenuJslotExternalHeadPartPrecheckService(
            new CapturingPresetService(preset),
            new FixedPluginAuthorityLoader(target.PluginOrder),
            new FixedRouteResolver(new SkyrimFaceRecordRoute(null!, [], [])),
            new AcceptedDiscovery(descriptorDrift));
        RaceMenuJslotExternalHeadPartPrecheckResult descriptorResult =
            await descriptorDriftService.PrecheckAsync(
                new RaceMenuJslotExternalHeadPartPrecheckRequest(
                    presetPath, presetHash, target, descriptor), cancellationToken);
        Require(descriptorResult.Status == RaceMenuJslotExternalHeadPartPrecheckStatus.Refused &&
                descriptorResult.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.DescriptorLost),
            "Resumed external record/asset descriptor drift was not refused before native rediscovery.");

        byte[] originalPreset = await File.ReadAllBytesAsync(
            presetPath.Value, cancellationToken);
        await File.WriteAllBytesAsync(presetPath.Value, [7, 7, 7], cancellationToken);
        try
        {
            var assetDriftService = new RaceMenuJslotExternalHeadPartPrecheckService(
                new CapturingPresetService(preset),
                new FixedPluginAuthorityLoader(target.PluginOrder),
                new FixedRouteResolver(new SkyrimFaceRecordRoute(null!, [], [])),
                new AcceptedDiscovery(descriptor));
            RaceMenuJslotExternalHeadPartPrecheckResult assetResult =
                await assetDriftService.PrecheckAsync(
                    new RaceMenuJslotExternalHeadPartPrecheckRequest(
                        presetPath, presetHash, target, descriptor), cancellationToken);
            Require(assetResult.Status == RaceMenuJslotExternalHeadPartPrecheckStatus.Refused &&
                    assetResult.Diagnostics.Any(item =>
                        item.Code == "external-headpart-precheck-preset-hash"),
                "Resumed preset asset drift was not refused before rediscovery.");
        }
        finally
        {
            await File.WriteAllBytesAsync(presetPath.Value, originalPreset,
                cancellationToken);
        }
    }

    private static string Diagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => item.Code + ":" + item.Message));

    private static RaceMenuPresetRecordAuthorityDraft CreateDraft(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        WorkspacePath providerPath)
    {
        SkyrimFaceRecordPluginAuthority authority = target.PluginOrder[0];
        var provider = new SkyrimFaceRecordProvider(
            authority.Plugin, providerPath, authority.ExpectedSha256);
        PresetHeadPart face = preset.Appearance.HeadParts[0];
        PresetHeadPart hair = preset.Appearance.HeadParts[1];
        var faceReference = new FormReference(authority.Plugin, new FormId(0x801));
        var hairReference = new FormReference(authority.Plugin, new FormId(0x800));
        var faceBinding = new RaceMenuNpcFormBinding(
            new RecordSignature("HDPT"), faceReference, faceReference,
            authority.Plugin, providerPath, authority.ExpectedSha256,
            NpcHeadPartType.Face);
        var hairBinding = new RaceMenuNpcFormBinding(
            new RecordSignature("HDPT"), hairReference, hairReference,
            authority.Plugin, providerPath, authority.ExpectedSha256,
            NpcHeadPartType.Hair);
        var raceBinding = faceBinding with
        {
            Signature = new RecordSignature("RACE"),
            SourceReference = target.Race,
            Reference = target.Race,
            HeadPartType = null
        };
        var textureAuthority = new SkyrimFaceTextureSetAuthority(
            new FormReference(authority.Plugin, new FormId(0x900)), provider,
            new SkyrimPrivateHeadTexturePaths(
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds"),
                new AssetPath("textures/task11.dds")), false);
        var tintPlan = new RaceMenuPresetTintAuthorityPlan(
            preset,
            new SkyrimRaceTintAuthority(
                target.Race, provider, target.Sex, [], false),
            [], new RaceMenuPresetQnamPlan(0, 0F, 0F, 0F));
        return new RaceMenuPresetRecordAuthorityDraft(
            "task11-record-authority", preset, target, raceBinding,
            [
                new RaceMenuPresetHeadPartAuthority(face, faceBinding),
                new RaceMenuPresetHeadPartAuthority(hair, hairBinding)
            ],
            faceBinding, textureAuthority, 0x112233, new FormId(0x801),
            tintPlan, false);
    }

    private static ExternalHeadPartFaceGeomExclusionAttestation
        CreateAttestation(
            ExternalHeadPartDependencyDescriptor descriptor,
            Sha256Hash outputHash,
            int outputLength) =>
        new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash([11, 12, 13]), descriptor.DescriptorId,
            new AssetPath("meshes/actors/character/facegeom/task11.nif"),
            outputHash, outputLength, [],
            [new ExternalHeadPartExcludedShapeEvidence(
                descriptor.Members[0].ModelNif!.Value, "ExternalHair")],
            [new ExternalHeadPartExcludedMetadataEvidence(
                "physics-locator", "Task11 external physics")],
            "preview254-task11") with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(new ExternalHeadPartFaceGeomExclusionAttestation(
                    ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                    Hash([11, 12, 13]), descriptor.DescriptorId,
                    new AssetPath("meshes/actors/character/facegeom/task11.nif"),
                    outputHash, outputLength, [],
                    [new ExternalHeadPartExcludedShapeEvidence(
                        descriptor.Members[0].ModelNif!.Value, "ExternalHair")],
                    [new ExternalHeadPartExcludedMetadataEvidence(
                        "physics-locator", "Task11 external physics")],
                    "preview254-task11"))
        };

    private static RaceMenuSelectedDependencyManifestExternalInstallDependency
        CreateExternalInstallDependency(
            ExternalHeadPartDependencyDescriptor descriptor,
            ExternalHeadPartFaceGeomExclusionAttestation attestation,
            PluginName outputPlugin) =>
        new RaceMenuSelectedDependencyManifestExternalInstallDependency(
            descriptor, attestation,
            new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                outputPlugin, Hash([21, 22, 23]), 128,
                [new PluginName("ExternalHair.esp")],
                descriptor.Members.Select(item => item.WinningForm)
                    .ToImmutableArray()),
            attestation.OutputFaceGeomPath,
            attestation.OutputFaceGeomSha256,
            attestation.OutputFaceGeomByteLength);

    private sealed record JslotRouteRun(
        RaceMenuJslotNpcBuildResult Result,
        WorkspacePath CompanionRoot,
        WorkspacePath OutputRoot,
        CapturingAcceptancePrecheck Precheck,
        RaceMenuJslotExternalHeadPartPrecheckAcceptance? PrecheckAcceptance,
        CapturingExternalFaceGenBaker Baker,
        SelectionHarness Selection,
        FixedRecordAuthorityBuilder RecordBuilder,
        FakeNpcBuildService NpcBuild,
        CapturingExternalInstallVerifier Verifier,
        CapturingOutputPluginBindingReader OutputBindingReader);

    private sealed class CapturingAcceptancePrecheck :
        IRaceMenuJslotExternalHeadPartPrecheckService
    {
        private readonly ExternalHeadPartDependencyDescriptor _descriptor;
        private readonly RaceMenuJslotExternalHeadPartPrecheckStatus _status;

        public CapturingAcceptancePrecheck(
            ExternalHeadPartDependencyDescriptor descriptor,
            RaceMenuJslotExternalHeadPartPrecheckStatus status =
                RaceMenuJslotExternalHeadPartPrecheckStatus.Accepted)
        {
            _descriptor = descriptor;
            _status = status;
        }

        public int Calls { get; private set; }

        public RaceMenuJslotExternalHeadPartPrecheckAcceptance? Acceptance
        { get; private set; }

        public ValueTask<RaceMenuJslotExternalHeadPartPrecheckResult>
            PrecheckAsync(
                RaceMenuJslotExternalHeadPartPrecheckRequest request,
                CancellationToken cancellationToken)
        {
            Calls++;
            Acceptance = _status ==
                RaceMenuJslotExternalHeadPartPrecheckStatus.Accepted
                ? new RaceMenuJslotExternalHeadPartPrecheckAcceptance(
                    request.ExpectedPresetSha256, request.Target, _descriptor)
                : null;
            return ValueTask.FromResult(
                new RaceMenuJslotExternalHeadPartPrecheckResult(
                    _status, Acceptance, []));
        }
    }

    private sealed class FixedRecordAuthorityBuilder(
        WorkspacePath providerPath) : IRaceMenuPresetRecordAuthorityBuilder
    {
        public int Calls { get; private set; }

        public ValueTask<RaceMenuPresetRecordAuthorityBuildResult> BuildAsync(
            PresetDocument requestPreset,
            RaceMenuPresetTarget target,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new RaceMenuPresetRecordAuthorityBuildResult(
                true, CreateDraft(requestPreset, target, providerPath), []));
        }
    }

    private sealed class FixedStandaloneAuthorityReader(
        RaceMenuNpcStandaloneAssets assets) :
        IRaceMenuNpcStandaloneAuthorityReader
    {
        public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
            RaceMenuNpcStandaloneAssetAuthority authority,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuNpcStandaloneAuthorityReadResult(
                true, assets, []));
    }

    private sealed class FixedFaceMorphSnapshotService(bool failRead) :
        ISkyrimFaceMorphSnapshotService
    {
        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request,
            CancellationToken cancellationToken) => failRead
            ? ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(
                false, request.ExpectedPluginSha256, null,
                [new Diagnostic(
                    "face-morph-snapshot-read-failed",
                    DiagnosticSeverity.Error,
                    "The synthetic NAM9 authority could not be reopened.")]))
            : ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(
                true, request.ExpectedPluginSha256,
                new SkyrimFaceMorphSnapshot(
                    Enumerable.Repeat(0F, 18).ToImmutableArray(),
                    0.5F, [0U, 0U, 0U, 0U], true, true), []));
    }

    private sealed class FakeNpcCreationService : INpcCreationService
    {
        public ValueTask<NpcCreationProposal> AnalyzeAsync(
            NpcCreationRequest request,
            CancellationToken cancellationToken)
        {
            var proposal = new NpcCreationProposal(
                request.Edition, request.TemplatePlugin,
                request.ExpectedTemplateHash, request.TemplateNpcFormId,
                request.Proposal, Hash([41, 42]), request.Output,
                new PluginName("NPCM_Task11_Temporary.esp"),
                new FormId(0x800), [], request.Identity, request.Traits,
                request.References, request.Appearance, request.Stats,
                request.RuntimeAppearance, []);
            return ValueTask.FromResult(proposal);
        }

        public async ValueTask<NpcCreationResult> ApplyAsync(
            NpcCreationRequest request,
            NpcCreationProposal proposal,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                request.Output.Value)!);
            byte[] bytes = [51, 52, 53];
            await File.WriteAllBytesAsync(request.Output.Value, bytes,
                cancellationToken);
            Sha256Hash hash = Hash(bytes);
            var verification = new NpcCreationVerificationResult(
                true, proposal.Proposal, proposal.ProposalHash,
                proposal.Output, hash, proposal.AllocatedFormId,
                proposal.Masters, 1F, new FormId(0x801), 1, 1, true, []);
            return new NpcCreationResult(
                true, proposal.Proposal, proposal.ProposalHash,
                proposal.Output, hash, proposal.AllocatedFormId,
                proposal.Masters, verification, []);
        }

        public ValueTask<NpcCreationVerificationResult> VerifyAsync(
            NpcCreationRequest request,
            NpcCreationProposal proposal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingExternalFaceGenBaker :
        IFaceGenNpcBakeService, IRaceMenuExternalDescriptorFaceGenNpcBakeService
    {
        private static readonly byte[] FaceGeomBytes = [61, 62, 63, 64];
        private static readonly byte[] FaceTintBytes = [71, 72, 73];

        public int TypedCalls { get; private set; }

        public ExternalHeadPartDependencyDescriptor? LastExpectedDescriptor
        { get; private set; }

        public ExternalHeadPartFaceGeomExclusionAttestation Attestation { get; }

        public CapturingExternalFaceGenBaker(
            ExternalHeadPartDependencyDescriptor descriptor,
            WorkspacePath providerPath,
            Sha256Hash providerHash,
            bool malformedArtifactEvidence = false) : this(
                descriptor, providerPath, providerHash,
                Hash(FaceGeomBytes), malformedArtifactEvidence)
        {
        }

        private CapturingExternalFaceGenBaker(
            ExternalHeadPartDependencyDescriptor descriptor,
            WorkspacePath providerPath,
            Sha256Hash providerHash,
            Sha256Hash faceGeomHash,
            bool malformedArtifactEvidence)
        {
            Attestation = CreateAttestation(
                descriptor, faceGeomHash, FaceGeomBytes.Length);
            Descriptor = descriptor;
            ProviderPath = providerPath;
            ProviderHash = providerHash;
            MalformedArtifactEvidence = malformedArtifactEvidence;
        }

        private ExternalHeadPartDependencyDescriptor Descriptor { get; }
        private WorkspacePath ProviderPath { get; }
        private Sha256Hash ProviderHash { get; }
        private bool MalformedArtifactEvidence { get; }

        public ValueTask<FaceGenNpcBakeResult> BakeAsync(
            FaceGenNpcBakeRequest request,
            CancellationToken cancellationToken) =>
            BakeCoreAsync(request, null, cancellationToken);

        public ValueTask<FaceGenNpcBakeResult> BakeAsync(
            FaceGenNpcBakeRequest request,
            ExternalHeadPartDependencyDescriptor? expectedDescriptor,
            CancellationToken cancellationToken)
        {
            TypedCalls++;
            LastExpectedDescriptor = expectedDescriptor;
            return BakeCoreAsync(request, expectedDescriptor, cancellationToken);
        }

        private async ValueTask<FaceGenNpcBakeResult> BakeCoreAsync(
            FaceGenNpcBakeRequest request,
            ExternalHeadPartDependencyDescriptor? expectedDescriptor,
            CancellationToken cancellationToken)
        {
            if (expectedDescriptor is not null &&
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    expectedDescriptor).AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    Descriptor)) is false)
                throw new InvalidDataException("Task 11 test baker received a drifted descriptor.");
            Directory.CreateDirectory(request.OutputDataRoot.Value);
            var nif = new WorkspacePath(Path.Combine(
                request.OutputDataRoot.Value, "task11-facegeom.nif"));
            var dds = new WorkspacePath(Path.Combine(
                request.OutputDataRoot.Value, "task11-facetint.dds"));
            await File.WriteAllBytesAsync(nif.Value, FaceGeomBytes,
                cancellationToken);
            await File.WriteAllBytesAsync(dds.Value, FaceTintBytes,
                cancellationToken);
            Sha256Hash nifHash = Hash(FaceGeomBytes);
            Sha256Hash ddsHash = Hash(FaceTintBytes);
            var dependency = new SkyrimAssetAuthority(
                "task11-external-provider", AssetProviderKind.Loose,
                ProviderPath, ProviderHash,
                new AssetPath("meshes/external/hair.nif"), 3,
                Hash([9, 8, 7]));
            var artifact = new FaceGenNpcBakeArtifact(
                request.Target, nif, nifHash, FaceGeomBytes.Length,
                dds, ddsHash, FaceTintBytes.Length, false)
            {
                ExternalDependencyAuthorities = [dependency],
                ExternalProviderSidecarAuthorities = [],
                ExternalHeadPartDependencies = [Descriptor],
                ExternalHeadPartExclusionAttestations =
                    MalformedArtifactEvidence
                        ? ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>.Empty
                        : [Attestation]
            };
            return new FaceGenNpcBakeResult(
                FaceGenNpcBakeStatus.Baked, request.Target, artifact, []);
        }
    }

    private sealed class PermissiveWorkspacePolicy : IWorkspacePolicy
    {
        public ImmutableArray<Diagnostic> Evaluate(
            WorkspacePath workspaceRoot, WorkspacePath outputRoot) => [];

        public ImmutableArray<Diagnostic> EvaluateReadRoot(
            WorkspacePath workspaceRoot, WorkspacePath readRoot) => [];
    }

    private sealed class FakeNpcBuildService(
        RaceMenuNpcStandaloneAssets assets,
        bool writeBethesdaPlugin,
        ImmutableArray<FormReference> fullPnam) :
        IRaceMenuNpcBuildService, IRaceMenuNpcStandaloneAuthorityReader
    {
        private readonly bool _writeBethesdaPlugin = writeBethesdaPlugin;
        private readonly ImmutableArray<FormReference> _fullPnam = fullPnam;

        public RaceMenuNpcExecutionResult? LastProbeResult { get; private set; }

        public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
            RaceMenuNpcStandaloneAssetAuthority authority,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuNpcStandaloneAuthorityReadResult(
                true, assets, []));

        public async ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
            RaceMenuNpcExecutionRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            string data = Path.Combine(request.Build.OutputRoot.Value, "Data",
                "NPCManager");
            Directory.CreateDirectory(data);
            var plugin = new WorkspacePath(Path.Combine(
                data, request.Build.OutputPlugin.Value));
            var faceGeom = new WorkspacePath(Path.Combine(
                data, "task11-facegeom.nif"));
            var faceTint = new WorkspacePath(Path.Combine(
                data, "task11-facetint.dds"));
            var manifest = new WorkspacePath(Path.Combine(
                data, "task11-package.json"));
            byte[] pluginBytes;
            if (_writeBethesdaPlugin)
            {
                await WriteBethesdaOutputPluginAsync(
                    plugin.Value,
                    request.Build.OutputPlugin.Value,
                    _fullPnam,
                    cancellationToken);
                pluginBytes = await File.ReadAllBytesAsync(
                    plugin.Value, cancellationToken);
            }
            else
            {
                pluginBytes = [81, 82, 83];
                await File.WriteAllBytesAsync(plugin.Value, pluginBytes,
                    cancellationToken);
            }
            byte[] faceGeomBytes = [84, 85];
            byte[] faceTintBytes = [86, 87];
            byte[] manifestBytes = [88, 89];
            await File.WriteAllBytesAsync(faceGeom.Value, faceGeomBytes,
                cancellationToken);
            await File.WriteAllBytesAsync(faceTint.Value, faceTintBytes,
                cancellationToken);
            await File.WriteAllBytesAsync(manifest.Value, manifestBytes,
                cancellationToken);
            if (request.SelectedDependencyManifest is not null)
            {
                var selectedManifest = new WorkspacePath(Path.Combine(
                    request.Build.OutputRoot.Value,
                    RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                        .Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(
                    selectedManifest.Value)!);
                await File.WriteAllBytesAsync(
                    selectedManifest.Value, [121, 122, 123],
                    cancellationToken);
            }
            var artifact = new BlankNpcBuildArtifact(
                "task11", "task11-static-package", "STATIC_PASS",
                request.Build.OutputRoot, plugin, Hash(pluginBytes),
                new FormId(0x800), faceGeom, Hash(faceGeomBytes), faceTint,
                Hash(faceTintBytes), manifest, Hash(manifestBytes), false);
            var packageVerification = new PackageVerifyResult(
                true,
                new PackageVerificationArtifact(
                    "1", "task11-package", "SkyrimSpecialEdition",
                    PresetFormat.RaceMenuJslot.ToString(),
                    request.Build.OutputPlugin.Value,
                    new FormId(0x800), manifest, Hash(manifestBytes), [],
                    true, true, false),
                []);
            RaceMenuNpcExecutionResult completed = new RaceMenuNpcExecutionResult(
                true, null, assets, null, null, null,
                new BlankNpcBuildResult(
                    true, artifact, null, null, null, null,
                    packageVerification, []), []);
            if (request.JslotOutputBindingProbe is not null)
            {
                LastProbeResult = new RaceMenuNpcExecutionResult(
                    Completed: false,
                    Plan: null,
                    Assets: null,
                    RuntimeAppearance: null,
                    RuntimeVmad: null,
                    BodyGen: null,
                    Build: null,
                    Diagnostics: [])
                {
                    ExternalInstallOutputPluginProbe =
                        new RaceMenuNpcExternalInstallOutputPluginProbeArtifact(
                            request.Build.OutputPlugin,
                            plugin,
                            artifact.PluginSha256,
                            artifact.AllocatedFormId)
                };
                return LastProbeResult;
            }
            return completed;
        }

        public int Calls { get; private set; }
    }

    private static async ValueTask WriteBethesdaOutputPluginAsync(
        string path,
        string plugin,
        ImmutableArray<FormReference> fullPnam,
        CancellationToken cancellationToken)
    {
        ModKey pluginKey = ModKey.FromNameAndExtension(plugin);
        var mod = new SkyrimMod(pluginKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("ExternalHair.esp")
        });
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        var npc = new Mutagen.Bethesda.Skyrim.Npc(
            new FormKey(pluginKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "Task11OutputNpc"
        };
        foreach (FormReference reference in fullPnam)
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(
                    ModKey.FromNameAndExtension(reference.Plugin.Value),
                    reference.FormId.Value)));
        mod.Npcs.Add(npc);
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, encoded.ToArray(),
            cancellationToken);
    }

    private sealed class CapturingOutputPluginBindingReader(
        IRaceMenuJslotOutputPluginBindingReader reader) :
        IRaceMenuJslotOutputPluginBindingReader
    {
        private readonly IRaceMenuJslotOutputPluginBindingReader _reader = reader;

        public CapturingOutputPluginBindingReader(
            RaceMenuSelectedDependencyManifestOutputPluginBinding binding) :
            this(new FixedOutputPluginBindingReader(binding))
        {
        }

        public RaceMenuJslotOutputPluginBindingReadResult? LastRead
        { get; private set; }

        public ValueTask<RaceMenuJslotOutputPluginBindingReadResult> ReadAsync(
            RaceMenuJslotOutputPluginBindingReadRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ReadCoreAsync(request, cancellationToken);
        }

        private async ValueTask<RaceMenuJslotOutputPluginBindingReadResult>
            ReadCoreAsync(
                RaceMenuJslotOutputPluginBindingReadRequest request,
                CancellationToken cancellationToken)
        {
            LastRead = await _reader.ReadAsync(request, cancellationToken);
            return LastRead;
        }

        public int Calls { get; private set; }
    }

    private sealed class FixedOutputPluginBindingReader(
        RaceMenuSelectedDependencyManifestOutputPluginBinding binding) :
        IRaceMenuJslotOutputPluginBindingReader
    {
        public ValueTask<RaceMenuJslotOutputPluginBindingReadResult> ReadAsync(
            RaceMenuJslotOutputPluginBindingReadRequest request,
            CancellationToken cancellationToken)
        {
            var effective = binding with
            {
                Plugin = request.ExpectedPlugin
            };
            return ValueTask.FromResult(
                new RaceMenuJslotOutputPluginBindingReadResult(
                    true, effective, [])
                {
                    FullPnam = effective.PnamBindings
                });
        }
    }

    private sealed class CapturingExternalInstallVerifier(
        ExternalHeadPartDependencyDescriptor descriptor,
        VerificationArtifactVariant variant) :
        IExternalHeadPartInstallVerifier
    {
        private readonly VerificationArtifactVariant _variant = variant;

        public int Calls { get; private set; }

        public ExternalHeadPartInstallVerificationRequest? LastRequest
        { get; private set; }

        public ValueTask<ExternalHeadPartInstallVerificationResult> VerifyAsync(
            ExternalHeadPartInstallVerificationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            ImmutableArray<ExternalHeadPartInstallObservation> observations =
                request.Context!.EnabledPluginOrder
                    .Select((plugin, index) =>
                        new ExternalHeadPartInstallObservation(
                            "enabled-plugin", plugin.Value,
                            Hash([144, 145]), 2, index))
                    .ToImmutableArray();
            var fingerprint = new ExternalHeadPartInstallContextFingerprint(
                _variant == VerificationArtifactVariant.MalformedFingerprint
                    ? Hash([141, 142, 143])
                    : ExternalHeadPartDependencyDescriptorCodec
                        .ComputeInstallContextFingerprintHash(observations),
                observations);
            var artifact = new ExternalHeadPartInstallVerificationArtifact(
                ExternalHeadPartSchemaIdentifiers.InstallVerification,
                PackageIntegrity: true,
                DescriptorClosureValid: true,
                HistoricalSnapshotValid: _variant ==
                    VerificationArtifactVariant.HistoricalFalse
                    ? false
                    : null,
                [descriptor.DescriptorId],
                new ExternalHeadPartVerifiedInstallSnapshot(
                    request.ExpectedSelectedManifestSha256,
                    [descriptor.DescriptorId], fingerprint),
                ExternalInstallDependencyState.Verified,
                InstallReady: true,
                InstallDependencyAuthority: true,
                RuntimeAuthority: false,
                VisualAuthority: false,
                [new ExternalHeadPartInstallProviderObservation(
                    descriptor.Provider.Plugin,
                    descriptor.Provider.PluginSha256,
                    descriptor.Provider.PluginSha256,
                    true)],
                []);
            return ValueTask.FromResult(
                new ExternalHeadPartInstallVerificationResult(
                    true, artifact, []));
        }
    }

    private sealed class SelectionHarness(
        PresetDocument preset,
        RaceMenuNpcStandaloneAssets currentAssets,
        SkyrimFaceRecordProvider provider,
        bool failManifestReadback,
        RaceMenuManagerOwnedFaceGeomCarrierAuthority? carrierOverride = null)
    {
        private readonly PresetDocument _preset = preset;
        private readonly RaceMenuNpcStandaloneAssets _currentAssets = currentAssets;
        private readonly SkyrimFaceRecordProvider _provider = provider;
        private readonly bool _failManifestReadback = failManifestReadback;
        private readonly RaceMenuManagerOwnedFaceGeomCarrierAuthority?
            _carrierOverride = carrierOverride;
        private ImmutableArray<ExternalHeadPartDependencyDescriptor> _candidateDescriptors = [];
        private ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
            _candidateAttestations = [];

        public RaceMenuPresetSelectionTransactionRequest? LastRequest { get; private set; }

        public int SelectedManifestReadCalls { get; private set; }

        public bool LastSchema8Readback { get; private set; }

        public IRaceMenuPresetSelectionTransactionService CreateService(
            WorkspacePath labRoot)
        {
            var inner = new RaceMenuPresetSelectionTransactionService(
                new FixedRecordAuthorityBuilder(_provider.Path),
                new SelectionRecordWriter(),
                new SelectionBundleWriter(),
                new SelectionPlanService(),
                new SelectionStandaloneReader(this),
                new SelectionStandaloneWriter(this),
                new SelectionDependencyWriter(),
                new SelectionWholeSkinResolver(),
                new SelectionWholeSkinWriter(),
                new PermissiveWorkspacePolicy(), labRoot,
                new SelectionDependencyReader(this));
            return new CapturingSelectionService(this, inner);
        }

        private sealed class CapturingSelectionService(
            SelectionHarness owner,
            IRaceMenuPresetSelectionTransactionService inner) :
            IRaceMenuPresetSelectionTransactionService
        {
            public ValueTask<RaceMenuPresetSelectionTransactionResult> RebindAsync(
                RaceMenuPresetSelectionTransactionRequest request,
                CancellationToken cancellationToken)
            {
                var effectiveRequest = owner._carrierOverride is null
                    ? request
                    : request with
                    {
                        ManagerOwnedFaceGeomCarrier = owner._carrierOverride
                    };
                owner.LastRequest = effectiveRequest;
                return inner.RebindAsync(effectiveRequest, cancellationToken);
            }
        }

        private sealed class SelectionRecordWriter :
            IRaceMenuPresetRecordAuthorityWriter
        {
            public async ValueTask<RaceMenuPresetRecordAuthorityWriteResult>
                WriteAsync(
                    RaceMenuPresetRecordAuthorityWriteRequest request,
                    CancellationToken cancellationToken)
            {
                byte[] bytes = [91, 92, 93];
                Directory.CreateDirectory(Path.GetDirectoryName(
                    request.Destination.Value)!);
                await File.WriteAllBytesAsync(
                    request.Destination.Value, bytes, cancellationToken);
                Sha256Hash hash = Hash(bytes);
                return new RaceMenuPresetRecordAuthorityWriteResult(
                    true,
                    new RaceMenuPresetRecordAuthorityArtifact(
                        request.Destination, hash,
                        new RaceMenuNpcRecordAuthority(
                            request.Destination, hash)), []);
            }
        }

        private sealed class SelectionBundleWriter :
            IRaceMenuPresetBundleAuthorityWriter
        {
            public ValueTask<RaceMenuPresetBundleAuthorityWriteResult> WriteAsync(
                RaceMenuPresetBundleAuthorityWriteRequest request,
                CancellationToken cancellationToken)
            {
                var bundlePath = new WorkspacePath(Path.Combine(
                    request.CandidateRoot.Value, "bundle-authority.json"));
                Sha256Hash bundleHash = Hash([101, 102, 103]);
                var bundle = new RaceMenuNpcPresetBundle(
                    bundlePath, bundleHash,
                    request.Companion.Preset,
                    request.Preset.SourceHash,
                    request.Companion.FaceGeom,
                    request.Companion.FaceGeomSha256,
                    request.Companion.FaceTint,
                    request.Companion.FaceTintSha256,
                    request.RecordAuthority.Authority);
                return ValueTask.FromResult(
                    new RaceMenuPresetBundleAuthorityWriteResult(
                        true,
                        new RaceMenuPresetBundleAuthorityArtifact(
                            "task11-bundle", bundle,
                            new RaceMenuNpcRuntimeRouteAuthority(
                                bundlePath, bundleHash), []), []));
            }
        }

        private sealed class SelectionPlanService :
            IRaceMenuNpcAppearancePlanService
        {
            public ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
                RaceMenuNpcBuildRequest request,
                CancellationToken cancellationToken)
            {
                var plan = new RaceMenuNpcAppearancePlan(
                    "1", "task11-plan", request,
                    CreatePresetForPlan(request), null!,
                    request.PresetBundle.ExpectedManifestSha256,
                    request.PresetBundle.RecordAuthority.ManifestPath.Value,
                    request.PresetBundle.RecordAuthority.ExpectedManifestSha256,
                    request.PresetBundle.ExpectedCharGenFaceGeomSha256,
                    request.PresetBundle.ExpectedCharGenFaceTintSha256,
                    null!, [], [], null, null, [], [], null, [], [], [], [], false)
                {
                    PluginAuthorities = request.PluginAuthorities
                };
                return ValueTask.FromResult(
                    new RaceMenuNpcAppearancePlanResult(true, plan, []));
            }

            private static PresetDocument CreatePresetForPlan(
                RaceMenuNpcBuildRequest request) =>
                new PresetDocument(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    new PresetAppearance(
                        1, [], null, new PresetWeight(
                            request.Stats.Weight, null, null, null),
                        ImmutableDictionary<string, float>.Empty,
                        ImmutableDictionary<string, float>.Empty,
                        ImmutableDictionary<string, float>.Empty,
                        Enumerable.Repeat(0F, 19).ToImmutableArray(),
                        [], [], null,
                        new PresetFieldPresence(
                            true, true, true, true, true, true, true,
                            true, true), [],
                        RaceMenu: new RaceMenuPresetData(
                            null, [0U, 0U, 0U, 0U], 1, [],
                            ImmutableDictionary<string,
                                ImmutableDictionary<string, float>>.Empty,
                            [], [], [])),
                    request.PresetBundle.ExpectedPresetSha256, []);
        }

        private sealed class SelectionStandaloneWriter(
            SelectionHarness owner) : IRaceMenuPresetStandaloneAuthorityWriter
        {
            public async ValueTask<
                RaceMenuPresetStandaloneAuthorityWriteResult> WriteAsync(
                    RaceMenuPresetStandaloneAuthorityWriteRequest request,
                    CancellationToken cancellationToken)
            {
                owner._candidateDescriptors = request.ExternalHeadPartDependencies;
                owner._candidateAttestations =
                    request.ExternalHeadPartExclusionAttestations;
                byte[] bytes = [111, 112, 113];
                Directory.CreateDirectory(Path.GetDirectoryName(
                    request.Destination.Value)!);
                await File.WriteAllBytesAsync(
                    request.Destination.Value, bytes, cancellationToken);
                Sha256Hash hash = Hash(bytes);
                var artifact = new RaceMenuPresetStandaloneAuthorityArtifact(
                    "task11-standalone", request.Destination, hash, 1, 1,
                    [], false)
                {
                    ExternalHeadPartDependencies =
                        request.ExternalHeadPartDependencies,
                    ExternalHeadPartExclusionAttestations =
                        request.ExternalHeadPartExclusionAttestations
                };
                return new RaceMenuPresetStandaloneAuthorityWriteResult(
                    true, artifact, []);
            }
        }

        private sealed class SelectionStandaloneReader(
            SelectionHarness owner) : IRaceMenuNpcStandaloneAuthorityReader
        {
            public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
                RaceMenuNpcStandaloneAssetAuthority authority,
                CancellationToken cancellationToken)
            {
                bool candidate = authority.ManifestPath.Value.EndsWith(
                    "standalone-assets.json", StringComparison.OrdinalIgnoreCase);
                if (candidate) owner.LastSchema8Readback = true;
                RaceMenuNpcStandaloneAssets assets = candidate
                    ? owner._currentAssets with
                    {
                        SchemaVersion = 8,
                        FaceTintWidth = 1,
                        FaceTintHeight = 1,
                        ExternalHeadPartDependencies =
                            owner._candidateDescriptors,
                        ExternalHeadPartExclusionAttestations =
                            owner._candidateAttestations,
                        ExternalCharGenExportAuthority = null
                    }
                    : owner._currentAssets;
                return ValueTask.FromResult(
                    new RaceMenuNpcStandaloneAuthorityReadResult(
                        true, assets, []));
            }
        }

        private sealed class SelectionDependencyWriter :
            IRaceMenuSelectedDependencyManifestWriter
        {
            public async ValueTask<
                RaceMenuSelectedDependencyManifestWriteResult> WriteAsync(
                    RaceMenuSelectedDependencyManifestWriteRequest request,
                    CancellationToken cancellationToken)
            {
                byte[] bytes = [121, 122, 123];
                Directory.CreateDirectory(Path.GetDirectoryName(
                    request.Destination.Value)!);
                await File.WriteAllBytesAsync(
                    request.Destination.Value, bytes, cancellationToken);
                Sha256Hash hash = Hash(bytes);
                var artifact = new RaceMenuSelectedDependencyManifestArtifact(
                    "task11-schema3", request.Destination, hash, 1, 0, 0,
                    false)
                {
                    SchemaVersion = 3,
                    PresetSha256 = request.PresetSha256,
                    ExternalInstallDependencies =
                        request.ExternalInstallDependencies
                };
                return new RaceMenuSelectedDependencyManifestWriteResult(
                    true, artifact, []);
            }
        }

        private sealed class SelectionDependencyReader(
            SelectionHarness owner) : IRaceMenuSelectedDependencyManifestReader
        {
            public ValueTask<RaceMenuSelectedDependencyManifestReadResult> ReadAsync(
                WorkspacePath manifestPath,
                Sha256Hash expectedHash,
                WorkspacePath packageRoot,
                CancellationToken cancellationToken)
            {
                owner.SelectedManifestReadCalls++;
                var artifact = new RaceMenuSelectedDependencyManifestArtifact(
                    "task11-schema3", manifestPath, expectedHash, 1, 0, 0,
                    false)
                {
                    SchemaVersion = owner._failManifestReadback ? 2 : 3,
                    PresetSha256 = owner.LastRequest?.SelectedPreset.SourceHash ??
                        default,
                    ExternalInstallDependencies =
                        owner.LastRequest?.ExternalInstallDependencies ?? []
                };
                return ValueTask.FromResult(
                    new RaceMenuSelectedDependencyManifestReadResult(
                        artifact, []));
            }
        }

        private sealed class SelectionWholeSkinResolver :
            ISkyrimNpcWholeSkinAuthorityResolver
        {
            public ValueTask<SkyrimNpcWholeSkinAuthorityResult> ResolveAsync(
                SkyrimNpcWholeSkinAuthorityRequest request,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(new SkyrimNpcWholeSkinAuthorityResult(
                    true,
                    new SkyrimNpcWholeSkinAuthority(
                        SkyrimNpcSkinRouteKind.InheritedRace,
                        request.Race, null!, null!, [], false), []));
        }

        private sealed class SelectionWholeSkinWriter :
            IRaceMenuNpcWholeSkinAuthorityWriter
        {
            public ValueTask<RaceMenuNpcWholeSkinAuthorityWriteResult> WriteAsync(
                RaceMenuNpcWholeSkinAuthorityWriteRequest request,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(
                    new RaceMenuNpcWholeSkinAuthorityWriteResult(
                        true,
                        new RaceMenuNpcWholeSkinAuthorityArtifact(
                            new RaceMenuNpcWholeSkinAuthority(
                                request.Destination,
                                Hash([131, 132])),
                            request.Snapshot), []));
        }
    }

    private static ExternalHeadPartDependencyDescriptor CreateDescriptor(
        PluginName provider, FormReference root)
    {
        var model = new AssetPath("meshes/external/hair.nif");
        var xml = new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/hair.xml");
        var member = new ExternalHeadPartRecordDependency(
            root, provider, root, provider, Hash([9, 8, 7]), 3, Hash([1]),
            "ExternalHair", NpcHeadPartType.Hair, NpcHeadPartType.Hair, model,
            [new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, model)], [], null, 0,
            0, NpcSex.Female, null);
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash([0]),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            root, root, NpcHeadPartType.Hair, Hash([2]),
            new ExternalHeadPartProviderIdentity(
                provider, Hash([9, 8, 7]), 3,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired),
            [member],
            new ExternalHeadPartPhysicsBinding(
                ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
                [new ExternalHeadPartPhysicsShapeBinding(
                    root, model, "Hair", xml, Hash([3]), 4)], null),
            [new ExternalHeadPartAssetDependency(
                model, Hash([4]), 5, provider, Hash([9, 8, 7]), null)],
            []);
        return descriptor with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptor)
        };
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class CapturingPresetService(PresetDocument document) :
        IPresetService
    {
        public int Calls { get; private set; }

        public ValueTask<PresetParseResult> InspectAsync(
            PresetParseRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new PresetParseResult(
                document, document.Diagnostics));
        }

        public ValueTask<PresetExportResult> ExportAsync(
            PresetExportRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PresetDiffResult> DiffAsync(
            PresetDiffRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedPluginAuthorityLoader(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> authorities) :
        ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimFaceRecordPluginAuthorityResult(
                true, authorities, []));
    }

    private sealed class FixedRouteResolver(SkyrimFaceRecordRoute route) :
        ISkyrimFaceRecordRouteResolver
    {
        public ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
            SkyrimFaceRecordRouteRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimFaceRecordRouteResult(true, route, []));
    }

    private sealed class AcceptedDiscovery(
        ExternalHeadPartDependencyDescriptor descriptor) :
        IExternalHeadPartDependencyDiscovery
    {
        public int Calls { get; private set; }

        public ValueTask<ExternalHeadPartDependencyDiscoveryResult> DiscoverAsync(
            ExternalHeadPartDependencyDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(
                new ExternalHeadPartDependencyDiscoveryResult(
                    ExternalHeadPartDependencyDiscoveryStatus.Accepted,
                    descriptor, []));
        }
    }

    private sealed class ErrorNotApplicableDiscovery :
        IExternalHeadPartDependencyDiscovery
    {
        public ValueTask<ExternalHeadPartDependencyDiscoveryResult> DiscoverAsync(
            ExternalHeadPartDependencyDiscoveryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ExternalHeadPartDependencyDiscoveryResult(
                    ExternalHeadPartDependencyDiscoveryStatus.NotApplicable,
                    null,
                    [new Diagnostic(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        DiagnosticSeverity.Error,
                        "Task 11 synthetic discovery drift.")]));
    }
}
