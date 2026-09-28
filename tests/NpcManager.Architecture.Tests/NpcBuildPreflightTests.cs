using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static class NpcBuildPreflightTests
{
    public static async Task RunAsync()
    {
        string rootPath = Path.Combine(
            AppContext.BaseDirectory,
            "npc-build-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var root = new WorkspacePath(rootPath);
        try
        {
            WorkspacePath sourceRequest = Write(root, "request.json", "request");
            WorkspacePath presetPath = Write(root, "selected.jslot", "preset");
            WorkspacePath dataRoot = Child(root, "Data");
            Directory.CreateDirectory(dataRoot.Value);
            WorkspacePath pluginPath = Write(dataRoot, "Skyrim.esm", "plugin");
            WorkspacePath finalRoot = Child(root, "final-output");
            WorkspacePath companionRoot = Child(root, "companion-staging");
            WorkspacePath preflightPath = Child(root, "reviewed-preflight.json");
            RaceMenuNpcExecutionRequest execution = BuildExecution(root, finalRoot);
            PresetDocument preset = BuildPreset(HashFile(presetPath));
            await AssertExportMorphDisposition(execution, root);
            await AssertStandaloneReaderIsIndependent(root);
            var planner = new FakePlanner(execution.Build, preset);
            var morphSnapshotService = new FakeMorphSnapshotService(
                new SkyrimFaceMorphSnapshotResult(
                    true,
                    HashFile(Child(root, "template.esp")),
                    new SkyrimFaceMorphSnapshot(
                        ImmutableArray<float>.Empty,
                        0f,
                        ImmutableArray<uint>.Empty,
                        true,
                        false),
                    [new Diagnostic(
                        "typed-snapshot-read",
                        DiagnosticSeverity.Info,
                        "Synthetic snapshot read.")]));
            var service = new NpcBuildPreflightService(
                new FakeLoader(execution, HashFile(sourceRequest)),
                planner,
                new FakeStandaloneReader(),
                morphSnapshotService,
                new FakePresetService(preset),
                new FakePluginLoader(pluginPath),
                () => new PreviewDependencyPreflightResult(
                    [new PreviewDependencyAuthority(
                        "MediaPipe runtime", "application-runtime", 1,
                        Hash("runtime"), true)],
                    [new Diagnostic(
                        "npc-preview-blender-hash",
                        DiagnosticSeverity.Error,
                        "Blender is not installed in the copied workspace.")]),
                new NpcBuildPreflightDocumentCodec(root),
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath(@"F:\ExampleGame")),
                root);
            var request = new NpcBuildPreflightRequest(
                execution,
                sourceRequest,
                HashFile(sourceRequest),
                presetPath,
                preset.SourceHash,
                dataRoot,
                [new PluginName("Skyrim.esm")],
                companionRoot,
                preflightPath);
            string[] filesBeforePreflight = EnumerateFiles(root);

            NpcBuildPreflightResult written = await service.CreateAsync(
                request, CancellationToken.None);
            Require(written.Created && written.ReadyForBuild &&
                    written.Document?.Path == preflightPath,
                "Preflight did not publish its sole reviewed JSON artifact: " +
                string.Join(" | ", written.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));
            NpcBuildPreflightDocument writtenDocument = written.Document ??
                throw new InvalidOperationException("Preflight document is absent.");
            Require(writtenDocument.Value.RequiredGates.All(item =>
                        item.Required && item.Passed) &&
                    !writtenDocument.Value.PreviewReady &&
                    writtenDocument.Value.OptionalPreview.Any(item =>
                        !item.Required && !item.Passed),
                "Required build gates were not separated from optional preview readiness.");
            NpcBuildPreflightGate morphGate = writtenDocument.Value.RequiredGates
                .Single(item => item.Id == "native-morph-authority");
            WorkspacePath expectedNam9Plugin = Child(root, "template.esp");
            Require(morphGate.Required && morphGate.Passed &&
                    morphSnapshotService.LastRequest is
                    {
                        Edition: GameEdition.SkyrimSpecialEdition,
                        Plugin: var plugin,
                        ExpectedPluginSha256: var pluginHash,
                        NpcFormId.Value: 0x800
                    } && plugin == expectedNam9Plugin &&
                    pluginHash == HashFile(expectedNam9Plugin),
                "Native morph preflight did not reopen the exact standalone NAM9 authority.");
            Require(writtenDocument.Value.HeadParts.Length == 1 &&
                    writtenDocument.Value.Appearance.Any(item =>
                        item.Field == "hair-color") &&
                    writtenDocument.Value.FinalDependencyClosure.Any(item =>
                        item.Role == "final-CharGen-NIF") &&
                    writtenDocument.Value.RequiredGates.Any(item =>
                        item.Id == "facegeom-codec" &&
                        item.Detail.Contains(
                            SseSelectedHeadpartPackedNormalPolicy.Version,
                            StringComparison.Ordinal)) &&
                    writtenDocument.Value.PlannedOutputs.Count(item =>
                        item.Role.StartsWith("preview:",
                            StringComparison.Ordinal)) == 6,
                "Preflight omitted appearance, packed-normal, closure, or six-view output evidence.");
            Require(!Directory.Exists(finalRoot.Value) &&
                    !Directory.Exists(companionRoot.Value),
                "Read-only preflight created a build or staging root.");
            string[] created = EnumerateFiles(root)
                .Except(filesBeforePreflight, StringComparer.Ordinal)
                .ToArray();
            Require(created.SequenceEqual(
                    new[] { "reviewed-preflight.json" },
                    StringComparer.Ordinal),
                "Preflight created files other than its one requested JSON output: " +
                string.Join(", ", created));

            morphSnapshotService.Result = new SkyrimFaceMorphSnapshotResult(
                false,
                null,
                null,
                [new Diagnostic(
                    "typed-snapshot-refusal",
                    DiagnosticSeverity.Error,
                    "Synthetic snapshot refusal.")]);
            NpcBuildPreflightResult snapshotRefused = await service.CreateAsync(
                request with { Output = Child(root, "snapshot-refused.json") },
                CancellationToken.None);
            Require(snapshotRefused.Created && !snapshotRefused.ReadyForBuild &&
                    snapshotRefused.Document is not null &&
                    snapshotRefused.Document.Value.RequiredGates.Any(item =>
                        item.Id == "native-morph-authority" && !item.Passed) &&
                    snapshotRefused.Diagnostics.Any(item =>
                        item.Code == "typed-snapshot-refusal") &&
                    !Directory.Exists(finalRoot.Value) &&
                    !Directory.Exists(companionRoot.Value),
                "Snapshot refusal did not preserve its typed diagnostic or stay read-only.");

            float oneBitMismatch = BitConverter.Int32BitsToSingle(
                BitConverter.SingleToInt32Bits(0f) ^ 1);
            morphSnapshotService.Result = new SkyrimFaceMorphSnapshotResult(
                true,
                HashFile(expectedNam9Plugin),
                new SkyrimFaceMorphSnapshot(
                    ImmutableArray<float>.Empty,
                    oneBitMismatch,
                    ImmutableArray<uint>.Empty,
                    true,
                    false),
                [new Diagnostic(
                    "typed-snapshot-read",
                    DiagnosticSeverity.Info,
                    "Synthetic snapshot read.")]);
            NpcBuildPreflightResult bitMismatch = await service.CreateAsync(
                request with { Output = Child(root, "snapshot-bit-mismatch.json") },
                CancellationToken.None);
            Require(bitMismatch.Created && !bitMismatch.ReadyForBuild &&
                    bitMismatch.Document is not null &&
                    bitMismatch.Document.Value.RequiredGates.Any(item =>
                        item.Id == "native-morph-authority" && !item.Passed) &&
                    bitMismatch.Diagnostics.Any(item =>
                        item.Code == "typed-snapshot-read") &&
                    !Directory.Exists(finalRoot.Value) &&
                    !Directory.Exists(companionRoot.Value),
                "A one-bit NAM9 trailing mismatch was treated as exact authority.");
            morphSnapshotService.Result = new SkyrimFaceMorphSnapshotResult(
                true,
                HashFile(expectedNam9Plugin),
                new SkyrimFaceMorphSnapshot(
                    ImmutableArray<float>.Empty,
                    0f,
                    ImmutableArray<uint>.Empty,
                    true,
                    false),
                [new Diagnostic(
                    "typed-snapshot-read",
                    DiagnosticSeverity.Info,
                    "Synthetic snapshot read.")]);

            await AssertPublicationRacePreservesOccupant(
                root,
                writtenDocument.Value);

            NpcBuildPreflightResult reviewed =
                await service.VerifyReviewedAsync(
                    request with { Output = null },
                    new NpcBuildPreflightReviewAuthority(
                        preflightPath,
                        writtenDocument.Sha256),
                    CancellationToken.None);
            Require(reviewed.Created && reviewed.ReadyForBuild,
                "Unchanged reviewed preflight did not regenerate exactly.");

            planner.Revision = "derived-drift";
            NpcBuildPreflightResult stale =
                await service.VerifyReviewedAsync(
                    request with { Output = null },
                    new NpcBuildPreflightReviewAuthority(
                        preflightPath,
                        writtenDocument.Sha256),
                    CancellationToken.None);
            Require(!stale.Created && stale.Diagnostics.Any(item =>
                        item.Code == "preflight-derived-plan-stale") &&
                    !Directory.Exists(finalRoot.Value) &&
                    !Directory.Exists(companionRoot.Value),
                "Derived-plan drift did not refuse before staging.");

            var inMemoryPreflight = new RefusingPreflightService();
            var jslot = new RaceMenuJslotNpcBuildService(
                null!, null!, null!, null!, null!,
                new KOnlyWorkspacePolicy(root,
                    new WorkspacePath(@"F:\ExampleGame")),
                root,
                inMemoryPreflight);
            RaceMenuJslotNpcBuildResult refused = await jslot.ExecuteAsync(
                new RaceMenuJslotNpcBuildRequest(
                    execution,
                    presetPath,
                    preset.SourceHash,
                    dataRoot,
                    [new PluginName("Skyrim.esm")],
                    companionRoot)
                {
                    SourceRequest = sourceRequest,
                    SourceRequestSha256 = HashFile(sourceRequest)
                },
                null,
                CancellationToken.None);
            Require(!refused.Completed &&
                    inMemoryPreflight.CreateCalls == 1 &&
                    refused.Diagnostics.Any(item =>
                        item.Code == "npc-build-preflight-refused") &&
                    !Directory.Exists(companionRoot.Value) &&
                    !Directory.Exists(finalRoot.Value),
                "Execution without an explicit review did not run the same preflight in memory before staging.");

            Console.WriteLine("PASS NPC build preflight");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task AssertStandaloneReaderIsIndependent(
        WorkspacePath root)
    {
        WorkspacePath malformed = Write(
            root,
            "malformed-standalone.json",
            "{\"schemaVersion\":99}");
        var reader =
            new RaceMenuNpcStandaloneAuthorityReader(
                new BatchScopedAssetIndexer(
                    new BethesdaAssetIndexer()),
                new KOnlyWorkspacePolicy(
                    root,
                    new WorkspacePath(@"F:\ExampleGame")),
                root);

        RaceMenuNpcStandaloneAuthorityReadResult result =
            await reader.ReadAsync(
                new RaceMenuNpcStandaloneAssetAuthority(
                    malformed,
                    HashFile(malformed)),
                CancellationToken.None);

        Require(!result.Accepted && result.Assets is null &&
                result.Diagnostics.Any(item =>
                    item.Code == "racemenu-assets-manifest-invalid"),
            "Independent standalone authority reader changed its malformed-manifest refusal.");
    }

    private static async Task AssertPublicationRacePreservesOccupant(
        WorkspacePath root,
        NpcBuildPreflightArtifact artifact)
    {
        WorkspacePath destination = Child(root, "occupied-race.json");
        var hooks = new OccupyingPreflightHooks(destination.Value);
        var codec = new NpcBuildPreflightDocumentCodec(root, hooks);
        NpcBuildPreflightDocument document = codec.Encode(artifact);

        try
        {
            await codec.WriteNewAsync(
                document,
                destination,
                CancellationToken.None);
            throw new InvalidOperationException(
                "Preflight publication overwrote a destination occupied during rename.");
        }
        catch (IOException)
        {
        }

        Require(File.ReadAllText(destination.Value) == "occupied" &&
                !Directory.EnumerateFiles(
                        root.Value,
                        ".npc-build-preflight-*.tmp")
                    .Any(),
            "Preflight publication changed the occupying file or leaked its owned temporary.");
    }

    private static RaceMenuNpcExecutionRequest BuildExecution(
        WorkspacePath root,
        WorkspacePath outputRoot)
    {
        WorkspacePath preset = Write(root, "bundle-source.jslot", "bundle-preset");
        WorkspacePath faceGeom = Write(root, "char-gen.nif", "nif");
        WorkspacePath faceTint = Write(root, "char-gen.dds", "dds");
        WorkspacePath bundle = Write(root, "bundle.json", "bundle");
        WorkspacePath record = Write(root, "record.json", "record");
        WorkspacePath provider = Write(root, "provider.json", "provider");
        WorkspacePath template = Write(root, "template.esp", "template");
        WorkspacePath carrier = Write(root, "carrier.nif", "carrier");
        WorkspacePath tintManifest = Write(root, "tint.json", "tint");
        WorkspacePath providerData = Child(root, "ProviderData");
        Directory.CreateDirectory(providerData.Value);
        WorkspacePath dependencies = Write(root, "dependencies.json", "dependencies");
        WorkspacePath standalone = Write(root, "standalone.json", "standalone");
        var skyrim = new PluginName("Skyrim.esm");
        var reference = new FormReference(skyrim, new FormId(0x13746));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new RaceMenuNpcPresetBundle(
                bundle, HashFile(bundle), preset, HashFile(preset),
                faceGeom, HashFile(faceGeom), faceTint, HashFile(faceTint),
                new RaceMenuNpcRecordAuthority(record, HashFile(record))),
            new BlankNpcProviderBindingRequest(
                provider, HashFile(provider),
                GameEdition.SkyrimSpecialEdition, NpcSex.Female,
                template, HashFile(template), new FormId(0x800),
                carrier, HashFile(carrier), tintManifest,
                providerData, dependencies),
            outputRoot,
            new PluginName("PreflightNpc.esp"),
            new NpcCreationIdentity(new EditorId("PreflightNpc"),
                new NpcName("Preflight NPC")),
            new SkyrimNpcCreationTraits(NpcSex.Female,
                NpcCreationRole.Follower, true, false, true, false, true),
            new SkyrimNpcCreationReferences(reference, reference, reference,
                reference, reference),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 0, 0, 50, 50, 50, 1f, 0f, 255));
        return new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(
                standalone, HashFile(standalone)));
    }

    private static async Task AssertExportMorphDisposition(RaceMenuNpcExecutionRequest execution, WorkspacePath root)
    {
        var build = execution.Build;
        var bundle = build.PresetBundle;
        var assets = (await new FakeStandaloneReader().ReadAsync(execution.AssetAuthority, default)).Assets!;
        var export = new RaceMenuNpcExternalCharGenExportAuthority(
            Child(root, "accepted-export.json"), Hash("authority"), "accepted-export",
            bundle.PresetPath, bundle.ExpectedPresetSha256, bundle.CharGenFaceGeom,
            bundle.ExpectedCharGenFaceGeomSha256, bundle.CharGenFaceTint,
            bundle.ExpectedCharGenFaceTintSha256, build.References.Race, build.Traits.Sex, true, false);
        var preset = BuildPreset(bundle.ExpectedPresetSha256);
        assets = assets with { SchemaVersion = 7, ExternalCharGenExportAuthority = export };
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Require(NpcBuildPreflightDependencyClosureService.CustomMorphsAreBaked(build, preset, assets, diagnostics),
            "Exact admitted external export still requires custom-morph reconstruction.");
        Require(diagnostics.Any(x => x.Code == "npc-build-preflight-custom-morphs-baked"), "Baked custom morph disposition must be explicit.");
        foreach (var drift in new[]
        {
            export with { PresetSha256 = Hash("wrong-preset") },
            export with { FaceGeomSha256 = Hash("wrong-nif") },
            export with { FaceTintSha256 = Hash("wrong-dds") },
            export with { Race = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13747)) },
            export with { Sex = NpcSex.Male },
            export with { UserConfirmedVisualMatch = false },
            export with { RuntimeAuthority = true }
        })
        {
            diagnostics.Clear();
            Require(!NpcBuildPreflightDependencyClosureService.CustomMorphsAreBaked(build, preset,
                    assets with { ExternalCharGenExportAuthority = drift }, diagnostics) &&
                    diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error),
                "Mismatched export must not waive provider reconstruction.");
        }
        diagnostics.Clear();
        Require(!NpcBuildPreflightDependencyClosureService.CustomMorphsAreBaked(build,
                BuildPreset(Hash("other-selected-preset")), assets, diagnostics) &&
                diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error), "Actual selected preset hash must match export.");
        diagnostics.Clear();
        Require(!NpcBuildPreflightDependencyClosureService.CustomMorphsAreBaked(build, preset,
                assets with { SchemaVersion = 6 }, diagnostics) && diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error),
            "An unadmitted schema must not gain export authority.");
        diagnostics.Clear();
        Require(!NpcBuildPreflightDependencyClosureService.CustomMorphsAreBaked(build, preset,
                assets with { ExternalCharGenExportAuthority = null }, diagnostics) && diagnostics.Count == 0,
            "Generic provider route must retain reconstruction.");
    }
    private static PresetDocument BuildPreset(Sha256Hash sourceHash)
    {
        var headPart = new PresetHeadPart(
            PresetIdentifier.Parse("Skyrim.esm|0x00012345"), 1);
        var appearance = new PresetAppearance(
            1,
            [headPart],
            PresetHairColor.FromPackedRgb(0x112233),
            new PresetWeight(50f, null, null, null),
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableArray<float>.Empty,
            ImmutableArray<PresetTint>.Empty,
            ImmutableArray<PresetOverlay>.Empty,
            null,
            new PresetFieldPresence(true, true, true, true,
                false, false, false, false, false),
            ImmutableArray<PresetUnknownField>.Empty,
            RaceMenu: new RaceMenuPresetData(
                null,
                ImmutableArray<uint>.Empty,
                10000,
                ImmutableArray<RaceMenuSculptPart>.Empty,
                ImmutableDictionary<string,
                    ImmutableDictionary<string, float>>.Empty,
                ImmutableArray<RaceMenuBodyOverlay>.Empty,
                ImmutableArray<SkyrimNodeTransform>.Empty,
                ImmutableArray<SkyrimSkinOverride>.Empty));
        return new PresetDocument(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            appearance,
            sourceHash,
            []);
    }

    private sealed class FakeLoader(
        RaceMenuNpcExecutionRequest execution,
        Sha256Hash hash) : IRaceMenuNpcExecutionRequestFileLoader
    {
        public ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
            RaceMenuNpcExecutionRequestFileLoadRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new RaceMenuNpcExecutionRequestFileLoadResult(
                RaceMenuNpcExecutionRequestFileLoadStatus.Loaded,
                request.RequestFile, request.ExpectedSha256, hash, 7,
                execution, []));
    }

    private sealed class FakePlanner(
        RaceMenuNpcBuildRequest build,
        PresetDocument preset) : IRaceMenuNpcAppearancePlanService
    {
        public string Revision { get; set; } = "reviewed";

        public ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
            RaceMenuNpcBuildRequest request,
            CancellationToken cancellationToken)
        {
            var skyrim = new PluginName("Skyrim.esm");
            var reference = new FormReference(skyrim, new FormId(0x12345));
            var binding = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"), reference, reference, skyrim,
                build.ProviderContext.TemplatePlugin,
                build.ProviderContext.ExpectedTemplatePluginSha256,
                NpcHeadPartType.Hair);
            var resolved = new RaceMenuResolvedHeadPart(
                preset.Appearance.HeadParts[0], binding);
            var provider = new BlankNpcProviderArtifact(
                "1", "blank-npc-provider-binding", Revision,
                build.ProviderContext.ManifestPath,
                build.ProviderContext.ExpectedManifestSha256,
                GameEdition.SkyrimSpecialEdition, NpcSex.Female,
                [skyrim], Hash("graph"), ["Head"],
                new AssetPath("textures/provider/facetint.dds"),
                Hash("source-tint"), Hash("tint-manifest"),
                "dependencies", Hash("dependencies"), 1, 0, 0);
            var raceBinding = binding with
            {
                Signature = new RecordSignature("RACE"),
                HeadPartType = null
            };
            var plan = new RaceMenuNpcAppearancePlan(
                "1", "bundle", request, preset, provider,
                build.PresetBundle.ExpectedManifestSha256,
                "record", build.PresetBundle.RecordAuthority.ExpectedManifestSha256,
                build.PresetBundle.ExpectedCharGenFaceGeomSha256,
                build.PresetBundle.ExpectedCharGenFaceTintSha256,
                raceBinding,
                [new RaceMenuNpcHeadPartDisposition(
                    preset.Appearance.HeadParts[0],
                    RaceMenuNpcHeadPartDispositionKind.MappedRecord,
                    resolved, null)],
                [resolved], null,
                new RaceMenuNpcExternalHairColorAuthority(0x112233, binding),
                [], [], null, [skyrim], [skyrim], [], [], false);
            return ValueTask.FromResult(new RaceMenuNpcAppearancePlanResult(
                true, plan, []));
        }
    }

    private sealed class FakeStandaloneReader :
        IRaceMenuNpcStandaloneAuthorityReader
    {
        public ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
            RaceMenuNpcStandaloneAssetAuthority authority,
            CancellationToken cancellationToken)
        {
            var texture = new AssetPath("textures/head.dds");
            var paths = new SkyrimPrivateHeadTexturePaths(
                texture, texture, texture, texture, texture);
            var plugin = new WorkspacePath(
                Path.Combine(Path.GetDirectoryName(authority.ManifestPath.Value)!,
                    "template.esp"));
            return ValueTask.FromResult(
                new RaceMenuNpcStandaloneAuthorityReadResult(
                    true,
                    new RaceMenuNpcStandaloneAssets(
                        "assets",
                        new RaceMenuNpcNam9TrailingAuthority(
                            plugin, HashFile(plugin), new FormId(0x800), 0f),
                        paths, 1024, 1024, [], null, [], null)
                    {
                        SchemaVersion = 6,
                        NativeFaceGeomExternalHeadParts = []
                    },
                    []));
        }
    }

    private sealed class FakeMorphSnapshotService :
        ISkyrimFaceMorphSnapshotService
    {
        public FakeMorphSnapshotService(SkyrimFaceMorphSnapshotResult result)
        {
            Result = result;
        }

        public SkyrimFaceMorphSnapshotResult Result { get; set; }

        public SkyrimFaceMorphSnapshotRequest? LastRequest { get; private set; }

        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakePresetService(PresetDocument preset) : IPresetService
    {
        public ValueTask<PresetParseResult> InspectAsync(
            PresetParseRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new PresetParseResult(preset, []));

        public ValueTask<PresetExportResult> ExportAsync(
            PresetExportRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PresetDiffResult> DiffAsync(
            PresetDiffRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePluginLoader(WorkspacePath plugin) :
        ISkyrimFaceRecordPluginAuthorityLoader
    {
        public ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
            SkyrimFaceRecordPluginAuthorityRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new SkyrimFaceRecordPluginAuthorityResult(
                true,
                [new SkyrimFaceRecordPluginAuthority(
                    request.PluginOrder[0], plugin, HashFile(plugin))],
                []));
    }

    private sealed class RefusingPreflightService :
        INpcBuildPreflightService
    {
        public int CreateCalls { get; private set; }

        public ValueTask<NpcBuildPreflightResult> CreateAsync(
            NpcBuildPreflightRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return ValueTask.FromResult(new NpcBuildPreflightResult(
                false,
                false,
                null,
                [new Diagnostic(
                    "synthetic-preflight-refusal",
                    DiagnosticSeverity.Error,
                    "Synthetic in-memory refusal.")]));
        }

        public ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
            NpcBuildPreflightRequest request,
            NpcBuildPreflightReviewAuthority reviewed,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OccupyingPreflightHooks(string destination) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            if (string.Equals(
                    destinationPath,
                    destination,
                    StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(destinationPath, "occupied");
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private static WorkspacePath Write(
        WorkspacePath root,
        string relative,
        string value)
    {
        WorkspacePath path = Child(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
        File.WriteAllText(path.Value, value);
        return path;
    }

    private static WorkspacePath Child(WorkspacePath root, string relative) =>
        new(Path.Combine(root.Value, relative));

    private static Sha256Hash Hash(string value) => new(Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))));

    private static Sha256Hash HashFile(WorkspacePath path)
    {
        using FileStream stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string[] EnumerateFiles(WorkspacePath root) =>
        Directory.EnumerateFiles(root.Value, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root.Value, path)
                .Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
