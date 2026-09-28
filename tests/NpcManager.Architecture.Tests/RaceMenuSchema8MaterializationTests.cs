using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254RaceMenuSchema8MaterializationScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    private const string ExternalHeadPartDependenciesKind = "external-headpart-dependencies";

    public string Selector => "--test-racemenu-schema8-materialization";

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        string rootValue = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "preview254-schema8-materialization-" + Guid.NewGuid().ToString("N"));
        var root = new WorkspacePath(rootValue);
        Directory.CreateDirectory(root.Value);
        try
        {
            Schema8Fixture fixture = await Schema8Fixture.CreateAsync(
                root, cancellationToken);
            RaceMenuNpcExecutionRequest existingRequest = fixture.Request with
            {
                Build = fixture.Request.Build with
                {
                    ExistingNpcTarget = fixture.ExistingTarget
                }
            };
            RaceMenuNpcExecutionResult existingResult =
                await fixture.Service.ExecuteAsync(
                    existingRequest, null, cancellationToken);
            Require(!existingResult.Completed &&
                    existingResult.Diagnostics.Any(item =>
                        item.Code == "racemenu-build-schema8-new-npc-only") &&
                    fixture.ExistingBuild.LastRequest is null,
                "Schema-8 accepted ExistingNpcTarget or reached the existing-NPC " +
                "package service. Actual diagnostics: " +
                FormatDiagnostics(existingResult.Diagnostics));

            RaceMenuNpcExecutionResult result = await fixture.Service.ExecuteAsync(
                fixture.Request, null, cancellationToken);
            Require(result.Completed && result.Build is
                { Completed: true, Artifact: not null },
                "Schema-8 new-follower execution did not complete through the real " +
                "BlankNpcBuildService. Actual diagnostics: " +
                FormatDiagnostics(result.Diagnostics));
            BlankNpcBuildResult completedBuild = result.Build!;
            BlankNpcBuildArtifact completedArtifact = completedBuild.Artifact!;
            Require(fixture.Carrier.LastAnalyze is
                { QualificationProfile: QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete },
                "Schema-8 did not pass the Manager-assembled qualification profile to " +
                "the real blank-NPC package writer.");
            Require(fixture.Carrier.LastAnalyze!.SourceNif.Value
                        .EndsWith("manager-qualified-complete-carrier.nif",
                            StringComparison.OrdinalIgnoreCase),
                "Schema-8 did not pass the independently reopened Manager carrier to " +
                "the real blank-NPC package writer.");
            Require(completedBuild.FaceTintReadback is
                { Decoded: true, SourceSha256: var faceTintHash } &&
                    faceTintHash == fixture.CharGenFaceTintHash,
                "Schema-8 did not use and independently decode the exact preset-bundle " +
                "FaceTint DDS.");
            Require(fixture.DirectFaceGeom.Calls == 0,
                "Schema-8 called the schema-6/7 direct CharGen composition service.");

            PackageManifestReadResult manifestRead = await fixture.ManifestReader.ReadAsync(
                completedArtifact.Manifest, cancellationToken);
            Require(manifestRead.Identity is not null &&
                    !manifestRead.Diagnostics.Any(item =>
                        item.Severity == DiagnosticSeverity.Error),
                "Schema-8 final package manifest could not be independently reopened: " +
                FormatDiagnostics(manifestRead.Diagnostics));
            PackageManifestFile[] selectedRows = manifestRead.Identity!.Files
                .Where(item => item.Kind == ExternalHeadPartDependenciesKind)
                .ToArray();
            Require(selectedRows.Length == 1 &&
                    selectedRows[0].Sha256 == fixture.SelectedDependenciesHash &&
                    selectedRows[0].RelativePath.Value ==
                        "Data/NPCManager/Evidence/selected-preset-dependencies.json",
                "Schema-8 final manifest did not contain exactly one explicit " +
                "external-headpart-dependencies row at the canonical Data-relative path.");
            HashSet<Sha256Hash> finalHashes = Directory
                .EnumerateFiles(completedArtifact.OutputRoot.Value, "*",
                    SearchOption.AllDirectories)
                .Select(path => HashFile(new WorkspacePath(path)))
                .ToHashSet();
            Require(fixture.ProviderAssetHashes.All(hash => !finalHashes.Contains(hash)),
                "Schema-8 final package copied provider NIF/TRI/DDS/XML bytes.");
        }
        finally
        {
            if (Directory.Exists(root.Value))
                Directory.Delete(root.Value, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            item.Code + ":" + item.Message));

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash HashFile(WorkspacePath path) =>
        Hash(File.ReadAllBytes(path.Value));

    private sealed class Schema8Fixture
    {
        private Schema8Fixture(
            RaceMenuNpcExecutionRequest request,
            RaceMenuNpcBuildService service,
            CapturingExistingBuild existingBuild,
            ThrowingDirectFaceGeom directFaceGeom,
            CapturingQualifiedCarrier carrier,
            PackageManifestReader manifestReader,
            RaceMenuExistingNpcTarget existingTarget,
            WorkspacePath charGenFaceTint,
            Sha256Hash charGenFaceTintHash,
            WorkspacePath selectedDependencies,
            Sha256Hash selectedDependenciesHash,
            HashSet<Sha256Hash> providerAssetHashes)
        {
            Request = request;
            Service = service;
            ExistingBuild = existingBuild;
            DirectFaceGeom = directFaceGeom;
            Carrier = carrier;
            ManifestReader = manifestReader;
            ExistingTarget = existingTarget;
            CharGenFaceTint = charGenFaceTint;
            CharGenFaceTintHash = charGenFaceTintHash;
            SelectedDependencies = selectedDependencies;
            SelectedDependenciesHash = selectedDependenciesHash;
            ProviderAssetHashes = providerAssetHashes;
        }

        public RaceMenuNpcExecutionRequest Request { get; }

        public RaceMenuNpcBuildService Service { get; }

        public CapturingExistingBuild ExistingBuild { get; }

        public ThrowingDirectFaceGeom DirectFaceGeom { get; }

        public CapturingQualifiedCarrier Carrier { get; }

        public PackageManifestReader ManifestReader { get; }

        public RaceMenuExistingNpcTarget ExistingTarget { get; }

        public WorkspacePath CharGenFaceTint { get; }

        public Sha256Hash CharGenFaceTintHash { get; }

        public WorkspacePath SelectedDependencies { get; }

        public Sha256Hash SelectedDependenciesHash { get; }

        public HashSet<Sha256Hash> ProviderAssetHashes { get; }

        public static async ValueTask<Schema8Fixture> CreateAsync(
            WorkspacePath root,
            CancellationToken cancellationToken)
        {
            var providerPlugin = new PluginName("ExternalHair.esp");
            var vanillaPlugin = new PluginName("Skyrim.esm");

            WorkspacePath template = await WriteAsync(
                root, "template.esp", [1, 2, 3], cancellationToken);
            WorkspacePath providerCarrier = await WriteAsync(
                root, "provider-carrier.nif", [4, 5, 6], cancellationToken);
            WorkspacePath managerCarrier = await WriteAsync(
                root, "manager-carrier.nif", [7, 8, 9, 10], cancellationToken);
            WorkspacePath charGenNif = await WriteAsync(
                root, "preset.nif", [11, 12, 13], cancellationToken);
            WorkspacePath charGenFaceTint = await WriteAsync(
                root, "preset.dds", CreateBgra8Dds(14, 15, 16), cancellationToken);
            WorkspacePath headDds = await WriteAsync(
                root, "Data/Textures/head.dds", CreateBgra8Dds(17, 18, 19),
                cancellationToken);
            WorkspacePath providerNif = await WriteAsync(
                root,
                "meshes/actors/character/character assets/hair/orchid-root.nif",
                [101, 102, 103],
                cancellationToken);
            WorkspacePath providerTri = await WriteAsync(
                root,
                "meshes/actors/character/character assets/hair/orchid-root.tri",
                [104, 105, 106],
                cancellationToken);
            WorkspacePath providerDds = await WriteAsync(
                root, "textures/actors/character/hair/orchid-root.dds",
                [107, 108, 109], cancellationToken);
            WorkspacePath providerXml = await WriteAsync(
                root,
                "SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml",
                [110, 111, 112],
                cancellationToken);
            WorkspacePath preset = await WriteAsync(
                root, "preset.jslot", [20, 21, 22], cancellationToken);
            WorkspacePath bundle = await WriteAsync(
                root, "bundle.json", [23, 24, 25], cancellationToken);
            WorkspacePath record = await WriteAsync(
                root, "record.json", [26, 27, 28], cancellationToken);
            WorkspacePath providerManifest = await WriteAsync(
                root, "provider.json", [29, 30, 31], cancellationToken);
            WorkspacePath tintManifest = await WriteAsync(
                root, "tint.json", [32, 33, 34], cancellationToken);
            WorkspacePath dependencies = await WriteAsync(
                root, "dependencies.json", [35, 36, 37], cancellationToken);
            WorkspacePath selectedDependencies = await WriteAsync(
                root, "selected-dependencies.json", [38, 39, 40], cancellationToken);
            WorkspacePath standaloneManifest = new(Path.Combine(
                root.Value, "standalone.json"));

            Sha256Hash templateHash = HashFile(template);
            Sha256Hash providerCarrierHash = HashFile(providerCarrier);
            Sha256Hash managerCarrierHash = HashFile(managerCarrier);
            Sha256Hash charGenNifHash = HashFile(charGenNif);
            Sha256Hash charGenFaceTintHash = HashFile(charGenFaceTint);
            Sha256Hash headDdsHash = HashFile(headDds);
            Sha256Hash presetHash = HashFile(preset);
            Sha256Hash bundleHash = HashFile(bundle);
            Sha256Hash recordHash = HashFile(record);
            Sha256Hash providerManifestHash = HashFile(providerManifest);
            Sha256Hash tintManifestHash = HashFile(tintManifest);
            Sha256Hash dependenciesHash = HashFile(dependencies);
            Sha256Hash selectedDependenciesHash = HashFile(selectedDependencies);
            var providerAssetHashes = new HashSet<Sha256Hash>
            {
                providerCarrierHash,
                HashFile(providerNif),
                HashFile(providerTri),
                HashFile(providerDds),
                HashFile(providerXml)
            };

            ExternalHeadPartDependencyDescriptor descriptor =
                CreateDescriptor(providerPlugin, Hash([41, 42, 43]));
            ExternalHeadPartFaceGeomExclusionAttestation attestation =
                CreateAttestation(descriptor, managerCarrierHash, 4);
            byte[] standaloneBytes = BuildStandaloneManifest(
                template, templateHash, headDds, headDdsHash,
                descriptor, attestation);
            await File.WriteAllBytesAsync(
                standaloneManifest.Value, standaloneBytes, cancellationToken);
            Sha256Hash standaloneHash = Hash(standaloneBytes);

            var presetBundle = new RaceMenuNpcPresetBundle(
                bundle, bundleHash,
                preset, presetHash,
                charGenNif, charGenNifHash,
                charGenFaceTint, charGenFaceTintHash,
                new RaceMenuNpcRecordAuthority(record, recordHash));
            var providerContext = new BlankNpcProviderBindingRequest(
                providerManifest, providerManifestHash,
                GameEdition.SkyrimSpecialEdition, NpcSex.Female,
                template, templateHash, new FormId(0x800),
                providerCarrier, providerCarrierHash,
                tintManifest, root, dependencies);
            var outputRoot = new WorkspacePath(Path.Combine(
                root.Value, "output"));
            var build = new RaceMenuNpcBuildRequest(
                GameEdition.SkyrimSpecialEdition,
                presetBundle,
                providerContext,
                outputRoot,
                new PluginName("Schema8Output.esp"),
                new NpcCreationIdentity(new EditorId("Schema8Output"),
                    new NpcName("Schema 8 Output")),
                new SkyrimNpcCreationTraits(
                    NpcSex.Female, NpcCreationRole.Follower,
                    true, false, true, false, true),
                new SkyrimNpcCreationReferences(
                    new FormReference(vanillaPlugin, new FormId(0x13746)),
                    new FormReference(vanillaPlugin, new FormId(0x13746)),
                    new FormReference(vanillaPlugin, new FormId(0x13746)),
                    new FormReference(vanillaPlugin, new FormId(0x13746)),
                    null),
                new SkyrimNpcCreationStats(
                    new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                    0, 0, 0, 1, 1, 100, 0, 0,
                    50, 50, 50, 1F, 50F, 255))
            {
                WholeSkinAuthority = new RaceMenuNpcWholeSkinAuthority(
                    dependencies, dependenciesHash)
            };
            var existingTarget = new RaceMenuExistingNpcTarget(
                template, templateHash, new FormId(0x800));

            PresetDocument presetDocument = CreatePreset(presetHash,
                providerPlugin);
            BlankNpcProviderArtifact provider = new(
                "1", "blank-npc-provider-binding", "schema8-provider",
                providerManifest, providerManifestHash,
                GameEdition.SkyrimSpecialEdition, NpcSex.Female,
                [providerPlugin], Hash([44, 45, 46]), ["Head"],
                new AssetPath("Textures/head.dds"), headDdsHash,
                tintManifestHash, "schema8-dependencies", dependenciesHash,
                1, 0, 0);
            var faceBinding = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"),
                new FormReference(providerPlugin, new FormId(0x801)),
                new FormReference(providerPlugin, new FormId(0x801)),
                providerPlugin, template, templateHash,
                NpcHeadPartType.Face);
            var hairBinding = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"),
                new FormReference(providerPlugin, new FormId(0x800)),
                new FormReference(providerPlugin, new FormId(0x800)),
                providerPlugin, template, templateHash,
                NpcHeadPartType.Hair);
            var hairColorBinding = hairBinding with
            {
                Signature = new RecordSignature("CLFM"),
                HeadPartType = null
            };
            var headTextureBinding = hairBinding with
            {
                Signature = new RecordSignature("TXST"),
                Reference = new FormReference(providerPlugin,
                    new FormId(0x900)),
                SourceReference = new FormReference(providerPlugin,
                    new FormId(0x900)),
                HeadPartType = null
            };
            PresetHeadPart face = presetDocument.Appearance.HeadParts[0];
            PresetHeadPart hair = presetDocument.Appearance.HeadParts[1];
            var resolvedFace = new RaceMenuResolvedHeadPart(face, faceBinding);
            var resolvedHair = new RaceMenuResolvedHeadPart(hair, hairBinding);
            var raceBinding = faceBinding with
            {
                Signature = new RecordSignature("RACE"),
                SourceReference = new FormReference(vanillaPlugin,
                    new FormId(0x13746)),
                Reference = new FormReference(vanillaPlugin,
                    new FormId(0x13746)),
                HeadPartType = null
            };
            var plan = new RaceMenuNpcAppearancePlan(
                "1", "schema8-bundle", build, presetDocument, provider,
                bundleHash, "schema8-record", recordHash,
                charGenNifHash, charGenFaceTintHash,
                raceBinding,
                [
                    new RaceMenuNpcHeadPartDisposition(
                        face, RaceMenuNpcHeadPartDispositionKind.MappedRecord,
                        resolvedFace, null),
                    new RaceMenuNpcHeadPartDisposition(
                        hair, RaceMenuNpcHeadPartDispositionKind.MappedRecord,
                        resolvedHair, null)
                ],
                [resolvedFace, resolvedHair],
                headTextureBinding,
                new RaceMenuNpcExternalHairColorAuthority(
                    0x112233, hairColorBinding),
                [], [],
                new RaceMenuNpcQnamDerivation(
                    RaceMenuNpcQnamSourceKind.MappedSkinTint,
                    0,
                    17F / 255F,
                    34F / 255F,
                    51F / 255F,
                    Hash([60, 61, 62])),
                [providerPlugin], [providerPlugin],
                [
                    new RaceMenuNpcFieldCoverage(
                        RaceMenuNpcAppearanceField.Overlays, false,
                        RaceMenuNpcFieldCoverageKind.Blocked, "none"),
                    new RaceMenuNpcFieldCoverage(
                        RaceMenuNpcAppearanceField.NodeTransforms, false,
                        RaceMenuNpcFieldCoverageKind.Blocked, "none"),
                    new RaceMenuNpcFieldCoverage(
                        RaceMenuNpcAppearanceField.SkinOverrides, false,
                        RaceMenuNpcFieldCoverageKind.Blocked, "none")
                ],
                [], false);
            var existingBuild = new CapturingExistingBuild();
            var directFaceGeom = new ThrowingDirectFaceGeom();
            var policy = new KOnlyWorkspacePolicy(root,
                new WorkspacePath("F:\\ExampleGame"));
            var carrier = new CapturingQualifiedCarrier(managerCarrierHash);
            var manifestReader = new PackageManifestReader(policy, root);
            var ddsDecoder = new InProcessDdsTextureDecoder(root);
            var blankBuild = new BlankNpcBuildService(
                new FakeNpcCreationService(),
                new FixedProviderService(provider),
                carrier,
                new ThrowingFaceTintBuildService(),
                ddsDecoder,
                new ManifestInventoryVerifier(manifestReader),
                policy,
                root);
            var wholeSkinReader = new FixedWholeSkinReader(
                new SkyrimNpcWholeSkinAuthority(
                    SkyrimNpcSkinRouteKind.InheritedRace,
                    raceBinding.Reference,
                    raceBinding,
                    faceBinding,
                    [],
                    false));
            var service = new RaceMenuNpcBuildService(
                new FixedPlanService(plan),
                new FixedFaceMorphSnapshotService(),
                null!, blankBuild, new LooseHeadTextureIndexer(
                    headDds, headDdsHash),
                policy, root,
                null, null, carrier, ddsDecoder,
                existingBuild, directFaceGeom, wholeSkinReader);
            var request = new RaceMenuNpcExecutionRequest(
                build,
                new RaceMenuNpcStandaloneAssetAuthority(
                    standaloneManifest, standaloneHash))
            {
                ManagerOwnedFaceGeomCarrier =
                    new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                        managerCarrier, managerCarrierHash),
                SelectedDependencyManifest =
                    new RaceMenuSelectedDependencyManifestAuthority(
                        "schema8-selected", selectedDependencies,
                        selectedDependenciesHash),
                ExternalHeadPartDependencies = [descriptor],
                ExternalHeadPartExclusionAttestations = [attestation]
            };
            return new Schema8Fixture(
                request, service, existingBuild, directFaceGeom, carrier,
                manifestReader, existingTarget,
                charGenFaceTint, charGenFaceTintHash,
                selectedDependencies, selectedDependenciesHash,
                providerAssetHashes);
        }

        private static async ValueTask<WorkspacePath> WriteAsync(
            WorkspacePath root,
            string relative,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            WorkspacePath path = new(Path.Combine(root.Value,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            string? parent = Path.GetDirectoryName(path.Value);
            if (parent is not null) Directory.CreateDirectory(parent);
            await File.WriteAllBytesAsync(path.Value, bytes, cancellationToken);
            return path;
        }

        private static byte[] CreateBgra8Dds(byte blue, byte green, byte red)
        {
            var bytes = new byte[132];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4),
                0x2053_4444);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4),
                0x0000_100F);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4),
                0x00FF_0000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4),
                0x0000_FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4),
                0x0000_00FF);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4),
                0xFF00_0000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4),
                0x0000_1000);
            bytes[128] = blue;
            bytes[129] = green;
            bytes[130] = red;
            bytes[131] = 255;
            return bytes;
        }

        private static Sha256Hash HashFile(WorkspacePath path) =>
            Hash(File.ReadAllBytes(path.Value));

        private static Sha256Hash Hash(byte[] bytes) =>
            new(Convert.ToHexString(SHA256.HashData(bytes)));

        private static PresetDocument CreatePreset(
            Sha256Hash sourceHash,
            PluginName provider)
        {
            var face = new PresetHeadPart(
                PresetIdentifier.Parse(provider.Value + "|0x00000801"), 1);
            var hair = new PresetHeadPart(
                PresetIdentifier.Parse(provider.Value + "|0x00000800"), 2);
            var appearance = new PresetAppearance(
                1, [face, hair], PresetHairColor.FromPackedRgb(0x112233),
                new PresetWeight(50F, null, null, null),
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                Enumerable.Repeat(0F, 19).ToImmutableArray(),
                ImmutableArray<PresetTint>.Empty,
                ImmutableArray<PresetOverlay>.Empty,
                null,
                new PresetFieldPresence(true, true, true, true,
                    false, false, false, false, false),
                ImmutableArray<PresetUnknownField>.Empty,
                RaceMenu: new RaceMenuPresetData(
                    null, [0U, 0U, 0U, 0U], 10000,
                    ImmutableArray<RaceMenuSculptPart>.Empty,
                    ImmutableDictionary<string,
                        ImmutableDictionary<string, float>>.Empty,
                    ImmutableArray<RaceMenuBodyOverlay>.Empty,
                    ImmutableArray<SkyrimNodeTransform>.Empty,
                    ImmutableArray<SkyrimSkinOverride>.Empty));
            return new PresetDocument(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                appearance, sourceHash, []);
        }

        private static byte[] BuildStandaloneManifest(
            WorkspacePath template,
            Sha256Hash templateHash,
            WorkspacePath headDds,
            Sha256Hash headDdsHash,
            ExternalHeadPartDependencyDescriptor descriptor,
            ExternalHeadPartFaceGeomExclusionAttestation attestation)
        {
            string sourcePath = "Data/Textures/" +
                Path.GetFileName(headDds.Value);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer,
                       new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 8);
                writer.WriteString("assetSetId", "schema8-materialization");
                writer.WriteString("edition", "skyrimse");
                writer.WriteStartObject("nam9Authority");
                writer.WriteString("pluginPath", Path.GetFileName(template.Value));
                writer.WriteString("pluginSha256", templateHash.Value);
                writer.WriteString("npcFormId", "0x00000800");
                writer.WriteNumber("trailingValue", 0F);
                writer.WriteEndObject();
                writer.WriteStartObject("faceTint");
                writer.WriteNumber("width", 1);
                writer.WriteNumber("height", 1);
                writer.WriteEndObject();
                writer.WriteStartObject("privateHeadTextures");
                writer.WriteString("diffuse", "Textures/head.dds");
                writer.WriteString("normalOrGloss", "Textures/head.dds");
                writer.WriteString("glowOrDetailMap", "Textures/head.dds");
                writer.WriteString("height", "Textures/head.dds");
                writer.WriteString("backlightMaskOrSpecular", "Textures/head.dds");
                writer.WriteNull("environmentMaskOrSubsurfaceTint");
                writer.WriteNull("environment");
                writer.WriteNull("multilayer");
                writer.WriteEndObject();
                writer.WriteStartArray("packageAssets");
                writer.WriteEndArray();
                writer.WriteNull("overlayDecisions");
                writer.WriteStartArray("externalTextureAuthorities");
                writer.WriteStartObject();
                writer.WriteString("dataRelativePath", "Textures/head.dds");
                writer.WriteString("provider", "loose");
                writer.WriteString("sourcePath", sourcePath);
                writer.WriteString("sourceSha256", headDdsHash.Value);
                writer.WriteString("memberSha256", headDdsHash.Value);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteStartArray("externalHeadPartDependencies");
                writer.WriteRawValue(
                    Encoding.UTF8.GetString(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeDescriptor(descriptor)),
                    skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray(
                    "externalHeadPartFaceGeomExclusionAttestations");
                writer.WriteRawValue(
                    Encoding.UTF8.GetString(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeAttestation(attestation)),
                    skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteNull("bodySlidePresetAuthority");
                writer.WriteNull("bodyMeshAuthority");
                writer.WriteNull("finalOutputAuthority");
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        private static ExternalHeadPartDependencyDescriptor CreateDescriptor(
            PluginName providerPlugin,
            Sha256Hash providerHash)
        {
            var model = new AssetPath(
                "meshes/actors/character/character assets/hair/orchid-root.nif");
            var xml = new AssetPath(
                "SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml");
            var provider = new ExternalHeadPartProviderIdentity(
                providerPlugin, providerHash, 3,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired);
            var rootForm = new FormReference(providerPlugin, new FormId(0x800));
            var member = new ExternalHeadPartRecordDependency(
                rootForm, providerPlugin, rootForm, providerPlugin,
                providerHash, 3, Hash([47, 48, 49]), "OrchidRootHair",
                NpcHeadPartType.Hair, NpcHeadPartType.Hair, model,
                [], [], null, 0, 0, NpcSex.Female, null);
            var descriptor = new ExternalHeadPartDependencyDescriptor(
                ExternalHeadPartSchemaIdentifiers.Descriptor,
                Hash([50, 51, 52]),
                ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
                rootForm, rootForm, NpcHeadPartType.Hair,
                Hash([53, 54, 55]), provider, [member],
                new ExternalHeadPartPhysicsBinding(
                    ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
                    [new ExternalHeadPartPhysicsShapeBinding(
                        rootForm, model, "OrchidRoot", xml,
                        Hash([56, 57, 58]), 231)], null),
                [new ExternalHeadPartAssetDependency(
                    model, Hash([59, 60, 61]), 4096,
                    providerPlugin, providerHash, null)], []);
            return descriptor with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptor)
            };
        }

        private static ExternalHeadPartFaceGeomExclusionAttestation
            CreateAttestation(
                ExternalHeadPartDependencyDescriptor descriptor,
                Sha256Hash carrierHash,
                int carrierLength)
        {
            var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
                ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                Hash([62, 63, 64]), descriptor.DescriptorId,
                new AssetPath(
                    "meshes/actors/character/facegeom/Schema8Output.esp/00000800.nif"),
                carrierHash, carrierLength, [],
                [new ExternalHeadPartExcludedShapeEvidence(
                    descriptor.Members[0].ModelNif!.Value, "OrchidRoot")],
                [new ExternalHeadPartExcludedMetadataEvidence(
                    "physics-locator", "HDT Skinned Mesh Physics Object")],
                "preview254-task1");
            return attestation with
            {
                AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeAttestationHash(attestation)
            };
        }
    }

    private sealed class FixedPlanService(
        RaceMenuNpcAppearancePlan plan) : IRaceMenuNpcAppearancePlanService
    {
        public ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
            RaceMenuNpcBuildRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuNpcAppearancePlanResult(
                true, plan, []));
    }

    private sealed class FixedFaceMorphSnapshotService :
        ISkyrimFaceMorphSnapshotService
    {
        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(
                true, request.ExpectedPluginSha256,
                new SkyrimFaceMorphSnapshot(
                    Enumerable.Repeat(0F, 18).ToImmutableArray(),
                    0F, [0U, 0U, 0U, 0U], true, true), []));
    }

    private sealed class LooseHeadTextureIndexer(
        WorkspacePath source,
        Sha256Hash hash) : IAssetIndexer
    {
        public ValueTask<AssetIndex> IndexAsync(
            AssetIndexRequest request,
            CancellationToken cancellationToken)
        {
            var relative = new AssetPath("Textures/head.dds");
            return ValueTask.FromResult(new AssetIndex(
                request.Edition,
                [new AssetProvider(
                    relative, AssetProviderKind.Loose, "loose",
                    new FileInfo(source.Value).Length, hash.Value)], []));
        }
    }

    private sealed class FixedProviderService(
        BlankNpcProviderArtifact artifact) : IBlankNpcProviderService
    {
        public ValueTask<BlankNpcProviderBindingResult> QualifyAsync(
            BlankNpcProviderBindingRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BlankNpcProviderBindingResult(
                true, artifact, []));
    }

    private sealed class ThrowingFaceTintBuildService : IFaceTintBuildService
    {
        public ValueTask<FaceTintBuildResult> BuildAsync(
            FaceTintBuildRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Schema-8 exact FaceTint route must not invoke the generated FaceTint builder.");
    }

    private sealed class FakeNpcCreationService : INpcCreationService
    {
        public async ValueTask<NpcCreationProposal> AnalyzeAsync(
            NpcCreationRequest request,
            CancellationToken cancellationToken)
        {
            byte[] proposalBytes = Encoding.UTF8.GetBytes("schema8-proposal");
            await File.WriteAllBytesAsync(
                request.Proposal.Value, proposalBytes, cancellationToken);
            var masters = new[] { new PluginName("ExternalHair.esp") }
                .ToImmutableArray();
            var proposal = new NpcCreationProposal(
                request.Edition, request.TemplatePlugin,
                request.ExpectedTemplateHash, request.TemplateNpcFormId,
                request.Proposal, Hash(proposalBytes), request.Output,
                new PluginName(Path.GetFileName(request.Output.Value)),
                new FormId(0x800), masters, request.Identity, request.Traits,
                request.References, request.Appearance, request.Stats,
                request.RuntimeAppearance, [])
            {
                PluginAuthorities = request.PluginAuthorities
            };
            return proposal;
        }

        public async ValueTask<NpcCreationResult> ApplyAsync(
            NpcCreationRequest request,
            NpcCreationProposal proposal,
            CancellationToken cancellationToken)
        {
            byte[] outputBytes = [51, 52, 53];
            await File.WriteAllBytesAsync(
                request.Output.Value, outputBytes, cancellationToken);
            Sha256Hash outputHash = Hash(outputBytes);
            var verification = CreateVerification(
                proposal, outputHash);
            return new NpcCreationResult(
                true, proposal.Proposal, proposal.ProposalHash,
                proposal.Output, outputHash, proposal.AllocatedFormId,
                proposal.Masters, verification, []);
        }

        public ValueTask<NpcCreationVerificationResult> VerifyAsync(
            NpcCreationRequest request,
            NpcCreationProposal proposal,
            CancellationToken cancellationToken)
        {
            Sha256Hash outputHash = Hash(File.ReadAllBytes(request.Output.Value));
            return ValueTask.FromResult(CreateVerification(
                proposal, outputHash));
        }

        private static NpcCreationVerificationResult CreateVerification(
            NpcCreationProposal proposal,
            Sha256Hash outputHash) =>
            new(
                true, proposal.Proposal, proposal.ProposalHash,
                proposal.Output, outputHash, proposal.AllocatedFormId,
                proposal.Masters, 1F, new FormId(0x801), 1, 1, true, []);
    }

    private sealed class CapturingQualifiedCarrier(
        Sha256Hash expectedSourceHash) : IQualifiedFaceGeomCarrierService
    {
        public QualifiedFaceGeomCarrierAnalyzeRequest? LastAnalyze { get; private set; }

        private QualifiedFaceGeomCarrierProposal? LastProposal { get; set; }

        private QualifiedFaceGeomCarrierMaterializationArtifact? LastArtifact
        { get; set; }

        public async ValueTask<QualifiedFaceGeomCarrierAnalysisResult> AnalyzeAsync(
            QualifiedFaceGeomCarrierAnalyzeRequest request,
            CancellationToken cancellationToken)
        {
            LastAnalyze = request;
            byte[] sourceBytes = await File.ReadAllBytesAsync(
                request.SourceNif.Value, cancellationToken);
            Sha256Hash sourceHash = Hash(sourceBytes);
            if (sourceHash != expectedSourceHash ||
                sourceHash != request.ExpectedSourceSha256)
            {
                return new QualifiedFaceGeomCarrierAnalysisResult(
                    false, null,
                    [new Diagnostic("schema8-test-carrier-source", DiagnosticSeverity.Error,
                        "The test carrier source hash drifted before package materialization.")]);
            }

            var structure = new QualifiedFaceGeomCarrierStructure(
                1, 1, 1, 1, 0, 1, 0,
                ["Head"],
                [new QualifiedFaceGeomCarrierCensusEntry("NiTriShape", 1)],
                Hash([1, 2, 3]));
            var proposal = new QualifiedFaceGeomCarrierProposal(
                "1", "schema8-test", request.SourceNif, sourceHash,
                sourceBytes.LongLength, request.OutputNif, sourceHash,
                sourceBytes.LongLength, 0, 0, "Textures/head.dds",
                request.TargetFaceTintPath, structure, false, false)
            {
                QualificationProfile = request.QualificationProfile,
                TargetHeadTextures = request.TargetHeadTextures
            };
            LastProposal = proposal;
            return new QualifiedFaceGeomCarrierAnalysisResult(
                true, proposal, []);
        }

        public async ValueTask<QualifiedFaceGeomCarrierMaterializationResult>
            ApplyAsync(
                QualifiedFaceGeomCarrierProposal proposal,
                CancellationToken cancellationToken)
        {
            byte[] sourceBytes = await File.ReadAllBytesAsync(
                proposal.SourceNif.Value, cancellationToken);
            await File.WriteAllBytesAsync(
                proposal.OutputNif.Value, sourceBytes, cancellationToken);
            Sha256Hash outputHash = Hash(sourceBytes);
            var artifact = new QualifiedFaceGeomCarrierMaterializationArtifact(
                proposal.SchemaVersion, proposal.Operation, "ok",
                proposal.SourceSha256, proposal.SourceByteLength,
                new QualifiedFaceGeomCarrierTextureSetPreimage(
                    "base64", 0, "BSShaderTextureSet", "", 0,
                    Hash([])),
                outputHash, sourceBytes.LongLength, proposal.TextureSlotIndex,
                proposal.OriginalFaceTintPath, proposal.TargetFaceTintPath,
                proposal.TargetHeadTextures, Hash([4, 5, 6]),
                proposal.Structure, [0], proposal.CreationKitAuthority,
                proposal.RuntimeAuthority)
            {
                QualificationProfile = proposal.QualificationProfile,
                TargetTextureBindingSha256 = proposal.TargetHeadTexturesBindingSha256
                    ?? Hash([4, 5, 6])
            };
            LastArtifact = artifact;
            var verification = new QualifiedFaceGeomCarrierVerificationResult(
                true, proposal.OutputNif, outputHash, proposal.Structure, [0], []);
            return new QualifiedFaceGeomCarrierMaterializationResult(
                true, true, artifact, verification, []);
        }

        public ValueTask<QualifiedFaceGeomCarrierVerificationResult> VerifyAsync(
            QualifiedFaceGeomCarrierProposal proposal,
            CancellationToken cancellationToken)
        {
            Sha256Hash outputHash = Hash(File.ReadAllBytes(proposal.OutputNif.Value));
            return ValueTask.FromResult(
                new QualifiedFaceGeomCarrierVerificationResult(
                    outputHash == proposal.ExpectedOutputSha256,
                    proposal.OutputNif, outputHash, proposal.Structure, [0], []));
        }

        public ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult>
            VerifyEvidenceAsync(
                WorkspacePath packageRoot,
                QualifiedFaceGeomCarrierMaterializationEvidence evidence,
                CancellationToken cancellationToken) =>
            VerifyEvidenceFileAsync(packageRoot,
                new AssetPath("evidence/facegeom-carrier-materialization.json"),
                cancellationToken);

        public async ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult>
            VerifyEvidenceFileAsync(
                WorkspacePath packageRoot,
                AssetPath evidenceFile,
                CancellationToken cancellationToken)
        {
            if (LastProposal is null || LastArtifact is null)
            {
                return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                    false, null, null, null, null, [],
                    [new Diagnostic("schema8-test-carrier-evidence", DiagnosticSeverity.Error,
                        "The test carrier was asked for evidence before materialization.")]);
            }
            WorkspacePath output = LastProposal.OutputNif.Value.StartsWith(
                    packageRoot.Value, StringComparison.OrdinalIgnoreCase)
                ? LastProposal.OutputNif
                : new WorkspacePath(Path.Combine(packageRoot.Value,
                    "Data", "meshes", "actors", "character", "FaceGenData",
                    "FaceGeom", "Schema8Output.esp", "00000800.nif"));
            if (!File.Exists(output.Value))
            {
                return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                    false, output, null, null, null, [],
                    [new Diagnostic("schema8-test-carrier-output", DiagnosticSeverity.Error,
                        "The test carrier output is missing after package materialization.")]);
            }
            byte[] bytes = await File.ReadAllBytesAsync(output.Value,
                cancellationToken);
            Sha256Hash outputHash = Hash(bytes);
            return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                outputHash == LastArtifact.OutputSha256,
                output, outputHash, LastArtifact.SourceSha256,
                LastArtifact.SourceStructure, LastArtifact.ChangedBlocks, []);
        }
    }

    private sealed class ManifestInventoryVerifier(
        PackageManifestReader reader) : IPackageVerifyService
    {
        public async ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            PackageManifestReadResult read = await reader.ReadAsync(
                request.ManifestPath, cancellationToken);
            var diagnostics = read.Diagnostics.ToBuilder();
            if (read.Identity is null || diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error))
                return new PackageVerifyResult(false, null,
                    diagnostics.ToImmutable());

            var identity = read.Identity;
            var packageRoot = new WorkspacePath(
                Path.GetDirectoryName(identity.ManifestPath.Value)!);
            var rows = ImmutableArray.CreateBuilder<PackageFileVerification>();
            foreach (PackageManifestFile item in identity.Files)
            {
                WorkspacePath path = new(Path.Combine(
                    packageRoot.Value,
                    item.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(path.Value))
                {
                    diagnostics.Add(new Diagnostic("schema8-test-package-missing",
                        DiagnosticSeverity.Error,
                        $"Package artifact '{item.RelativePath.Value}' is missing."));
                    continue;
                }
                byte[] bytes = await File.ReadAllBytesAsync(path.Value,
                    cancellationToken);
                Sha256Hash actualHash = Hash(bytes);
                bool matches = bytes.LongLength == item.ByteLength &&
                    actualHash == item.Sha256;
                rows.Add(new PackageFileVerification(
                    item.Kind, item.RelativePath, item.ByteLength,
                    bytes.LongLength, item.Sha256, actualHash, matches));
                if (!matches)
                    diagnostics.Add(new Diagnostic("schema8-test-package-drift",
                        DiagnosticSeverity.Error,
                        $"Package artifact '{item.RelativePath.Value}' drifted."));
            }
            var artifact = new PackageVerificationArtifact(
                "1", "schema8-test-package-verification", identity.Edition,
                identity.PresetFormat, identity.OutputPlugin,
                identity.TargetFormId, identity.ManifestPath,
                identity.ManifestSha256, rows.ToImmutable(), true, true, false);
            bool verified = !diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error);
            return new PackageVerifyResult(verified, artifact,
                diagnostics.ToImmutable());
        }
    }

    private sealed class FixedWholeSkinReader(
        SkyrimNpcWholeSkinAuthority snapshot) : IRaceMenuNpcWholeSkinAuthorityReader
    {
        public ValueTask<RaceMenuNpcWholeSkinAuthorityReadResult> ReadAsync(
            RaceMenuNpcWholeSkinAuthorityReadRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RaceMenuNpcWholeSkinAuthorityReadResult(
                true, snapshot, []));
    }

    private sealed class CapturingExistingBuild :
        IExistingNpcAppearanceBuildService
    {
        public ExistingNpcAppearanceBuildRequest? LastRequest { get; private set; }

        public ValueTask<ExistingNpcAppearanceBuildResult> ExecuteAsync(
            ExistingNpcAppearanceBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(new ExistingNpcAppearanceBuildResult(
                false, null, null, null, null, null, null, []));
        }
    }

    private sealed class ThrowingDirectFaceGeom :
        IRaceMenuDirectCharGenFaceGeomBuildService
    {
        public int Calls { get; private set; }

        public ValueTask<RaceMenuDirectCharGenFaceGeomBuildResult> BuildAsync(
            RaceMenuDirectCharGenFaceGeomBuildRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException(
                "Schema-8 must not call direct CharGen FaceGeom composition.");
        }
    }

}
