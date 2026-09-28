using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.BodyGen;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.TestInfrastructure;
using NpcManager.Presets;
using Noggog;

namespace NpcManager.Gate2.PipelineIntegration.Tests;

internal static partial class Program
{
    private static readonly Sha256Hash AcceptedFaceTintHash = new(
        "18C5D0996929C3AAC0AC18F18E794E643341F2BE4D9914CAA4A990BA1DD76C46");
    private static readonly Sha256Hash AcceptedPrivateDiffuseHash = new(
        "8CE2339356CE9950CC5CCF734D804669EB168AAAE776B47E3E1B126BECE3A0C1");

    private static async Task TestSchema5RealPipelineAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync("success", failOuterPostcheck: false);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(result.Completed && result.Build is
        {
            Completed: true,
            Artifact: not null,
            PackageVerification: { Verified: true, Artifact.NoUndeclaredFiles: true }
        }, "The real schema-5 request did not complete: " + Format(result.Diagnostics));
        BlankNpcBuildArtifact artifact = result.Build!.Artifact!;
        Assert(artifact.FaceTintSha256 == AcceptedFaceTintHash &&
               HashFile(artifact.FaceTint.Value) == AcceptedFaceTintHash,
            "The schema-5 package did not retain the accepted conventional FaceTint.");
        string privateDiffuse = Path.Combine(fixture.OutputRoot.Value, "Data", "textures",
            "Actors", "Character", "Emi2", "Skin", "femalehead.dds");
        Assert(File.Exists(privateDiffuse) &&
               HashFile(privateDiffuse) == AcceptedPrivateDiffuseHash,
            "The schema-5 package did not retain the accepted private diffuse.");
        string wholeSkinEvidence = Path.Combine(
            fixture.OutputRoot.Value,
            "Data",
            "NPCManager",
            "Evidence",
            "whole-skin-authority.json");
        Assert(File.Exists(wholeSkinEvidence) &&
               JsonDocument.Parse(await File.ReadAllBytesAsync(wholeSkinEvidence))
                   .RootElement.GetProperty("schemaVersion").GetInt32() == 2,
            "The schema-5 package did not retain its verified whole-skin authority.");
        var outputKey = ModKey.FromNameAndExtension(
            Path.GetFileName(artifact.Plugin.Value));
        using (var output = SkyrimMod.CreateFromBinaryOverlay(
                   new ModPath(
                       outputKey,
                       new FilePath(artifact.Plugin.Value)),
                   SkyrimRelease.SkyrimSE))
        {
            ModKey retainedOwner = ModKey.FromNameAndExtension(
                "Schema5WholeSkinProvider.esp");
            Assert(output.ModHeader.MasterReferences.Any(item =>
                       item.Master == retainedOwner) &&
                   output.ArmorAddons.Single().AdditionalRaces.Any(
                       item => item.FormKey ==
                               new FormKey(retainedOwner, 0x900)),
                "The schema-5 output did not append and preserve the outfit carrier's provider-bound retained master.");
        }
        Assert(result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-durable-facegeom-verified") &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-durable-facetint-verified"),
            "The real schema-5 request omitted durable post-staging verification.");
        Assert(result.BodyGen is { Written: true, Files.Length: 2 } &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-bodygen-retained-verified"),
            "The real schema-5 request did not retain and verify both BodyGen files.");
        string finalDataRoot = Path.Combine(artifact.OutputRoot.Value, "Data");
        string[] expectedBodyGenPaths =
        [
            "meshes/actors/character/BodyGenData/Gate2Emi2Generated.esp/templates.ini",
            "meshes/actors/character/BodyGenData/Gate2Emi2Generated.esp/morphs.ini"
        ];
        Assert(result.BodyGen!.Files.Select(item => item.RelativePath.Value)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(expectedBodyGenPaths.Order(StringComparer.Ordinal)),
            "The real schema-5 request returned an unexpected BodyGen file set.");
        foreach (BodyGenFileArtifact file in result.BodyGen.Files)
        {
            string expectedPath = Path.GetFullPath(Path.Combine(finalDataRoot,
                file.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            Assert(string.Equals(file.AbsolutePath.Value, expectedPath,
                       StringComparison.OrdinalIgnoreCase) &&
                   !file.AbsolutePath.Value.Contains(".racemenu-stage-",
                       StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(file.AbsolutePath.Value) &&
                   new FileInfo(file.AbsolutePath.Value).Length == file.ByteLength &&
                   HashFile(file.AbsolutePath.Value) == file.Sha256,
                $"BodyGen result '{file.RelativePath.Value}' did not bind to its exact retained final file.");
        }

        string exactEvidence = Path.Combine(fixture.OutputRoot.Value, "evidence",
            "facetint-exact-source.json");
        string evidenceText = await File.ReadAllTextAsync(exactEvidence);
        using JsonDocument evidence = JsonDocument.Parse(evidenceText);
        JsonElement root = evidence.RootElement;
        Assert(!root.TryGetProperty("source", out _) &&
               !evidenceText.Contains(".racemenu-stage-", StringComparison.OrdinalIgnoreCase) &&
               root.GetProperty("sourceSha256").GetString() == AcceptedFaceTintHash.Value &&
               root.GetProperty("outputSha256").GetString() == AcceptedFaceTintHash.Value &&
               !Path.IsPathRooted(root.GetProperty("outputDds").GetString() ?? string.Empty),
            "Durable FaceTint evidence retained an operational staging path or lost its exact pair.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestSchema5NoDefaultOutfitPipelineAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync(
            "no-default-outfit",
            failOuterPostcheck: false,
            noDefaultOutfit: true);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(result.Completed && result.Build is
        {
            Completed: true,
            Artifact: not null,
            PackageVerification: { Verified: true }
        }, "The no-default-outfit preset pipeline did not complete: " +
           Format(result.Diagnostics));

        BlankNpcBuildArtifact artifact = result.Build!.Artifact!;
        ModKey outputKey = ModKey.FromNameAndExtension(
            Path.GetFileName(artifact.Plugin.Value));
        using var output = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(outputKey, new FilePath(artifact.Plugin.Value)),
            SkyrimRelease.SkyrimSE);
        Assert(output.Npcs.Single().DefaultOutfit.IsNull &&
               output.Outfits.Count == 0 &&
               output.Armors.Count == 0 &&
               output.ArmorAddons.Count == 0,
            "The full preset transaction retained or manufactured an outfit route for an intentionally naked NPC.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestSchema5PostcheckQuarantineAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync("postcheck-failure",
            failOuterPostcheck: true);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(!result.Completed && result.Build is { Completed: true, Artifact: not null },
            "The injected outer postcheck did not occur after the inner package transaction.");
        Assert(result.Diagnostics.Any(item =>
                   item.Code == "test-durable-facegeom-postcheck-failure") &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-output-quarantined"),
            "The injected durable postcheck failure was not reported with quarantine evidence: " +
            Format(result.Diagnostics));
        Assert(!Directory.Exists(fixture.OutputRoot.Value) &&
               !File.Exists(fixture.OutputRoot.Value),
            "A failed outer postcheck left a package at the requested deployable output path.");
        Assert(fixture.NewQuarantineRoots.Length == 1 &&
               Directory.Exists(fixture.NewQuarantineRoots.Single()),
            "The failed package was not retained at exactly one non-deployable quarantine path.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestSchema5BodySlideOmissionAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync(
            "body-slide-omitted", failOuterPostcheck: false,
            applyBodySlide: false);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(result.Completed && result.Build is { Completed: true, Artifact: not null } &&
               result.BodyGen is null && result.RuntimeAppearance is not null &&
               result.RuntimeAppearance.Dispositions.Any(item =>
                   item.Surface == SkyrimNpcRuntimeAppearanceSurface.BodyMorphs &&
                   item.Kind == SkyrimNpcRuntimeDispositionKind.UserOmitted),
            "The BodySlide opt-out did not complete as an explicit no-BodyGen transaction: " +
            Format(result.Diagnostics));
        string bodyGenRoot = Path.Combine(fixture.OutputRoot.Value, "Data", "meshes",
            "actors", "character", "BodyGenData");
        Assert(!Directory.Exists(bodyGenRoot),
            "The BodySlide opt-out retained a BodyGen directory or sidecar.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestMissingWholeSkinAuthorityAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync(
            "missing-whole-skin-authority", failOuterPostcheck: false,
            includeWholeSkinAuthority: false);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(!result.Completed &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-build-whole-skin-authority-required"),
            "A preset build without explicit whole-skin authority did not fail closed: " +
            Format(result.Diagnostics));
        Assert(!Directory.Exists(fixture.OutputRoot.Value) &&
               !File.Exists(fixture.OutputRoot.Value),
            "A preset build without whole-skin authority created its output root.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestInvalidWholeSkinAuthorityAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync(
            "invalid-whole-skin-authority", failOuterPostcheck: false,
            validWholeSkinAuthority: false);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(!result.Completed &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-whole-skin-authority-invalid"),
            "A record-authority manifest was accepted as whole-skin authority: " +
            Format(result.Diagnostics));
        Assert(!Directory.Exists(fixture.OutputRoot.Value) &&
               !File.Exists(fixture.OutputRoot.Value),
            "An invalid whole-skin authority created its output root.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestSchema5ExistingNpcPipelineAsync()
    {
        using var fixture = await Schema5Fixture.CreateAsync(
            "existing-npc",
            failOuterPostcheck: false,
            existingNpcTarget: true);
        var sourceHashBefore = HashFile(fixture.SourcePlugin.Value);
        RaceMenuNpcExecutionResult result = await fixture.ExecuteAsync();

        Assert(result.Completed && result.Build is null &&
               result.ExistingNpcBuild is
               {
                   Completed: true,
                   Artifact:
                   {
                       SourceOwnerPlugin.Value: "EmiCarrierProbe.esp",
                       SourceOwnerFormId.Value: 0x800,
                       RuntimeAuthority: false
                   },
                   AppearanceOverride.Verification:
                   {
                       IsValid: true,
                       SourceOwnedTargetCount: 1,
                       SelfOwnedTargetCount: 0
                   },
                   PackageVerification:
                   {
                       Verified: true,
                       Artifact.NoUndeclaredFiles: true,
                       Artifact.RuntimeProof: false
                   }
               },
            "The schema-5 existing-NPC request did not complete as a true source-owned package: " +
            Format(result.Diagnostics));
        var artifact = result.ExistingNpcBuild!.Artifact!;
        Assert(artifact.FaceGeom.Value.EndsWith(
                   Path.Combine("EmiCarrierProbe.esp", "00000800.nif"),
                   StringComparison.OrdinalIgnoreCase) &&
               artifact.FaceTint.Value.EndsWith(
                   Path.Combine("EmiCarrierProbe.esp", "00000800.dds"),
                   StringComparison.OrdinalIgnoreCase),
            "Existing-NPC FaceGen assets were keyed to the override ESP instead of the source owner.");
        Assert(result.BodyGen is
        {
            Written: true,
            Plugin.Value: "EmiCarrierProbe.esp",
            NpcFormId.Value: 0x800,
            Files.Length: 2
        } && result.BodyGen.Files.All(item => item.RelativePath.Value.Contains(
            "BodyGenData/EmiCarrierProbe.esp/", StringComparison.OrdinalIgnoreCase)),
            "Existing-NPC BodyGen files were not keyed to the source owner and local FormID.");
        Assert(HashFile(fixture.SourcePlugin.Value) == sourceHashBefore,
            "The schema-5 existing-NPC transaction changed its copied source plugin.");
        fixture.AssertNoNewStagingRoots();
    }

    private static async Task TestSchema5DependencyPreflightAsync()
    {
        WorkspacePath labRoot = FindLabRootForSchema5();
        string projectRoot = Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation");
        var requestFile = new WorkspacePath(Path.Combine(projectRoot,
            "01-source-copies", "gate2-emi2", "execution-request-v5b.json"));
        var loader = new RaceMenuNpcExecutionRequestFileLoader(labRoot);
        RaceMenuNpcExecutionRequestFileLoadResult loaded = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(
                requestFile, HashFile(requestFile.Value)), CancellationToken.None);
        Assert(loaded.Loaded && loaded.Request is not null,
            "The schema-5 dependency-preflight fixture did not load.");
        var wholeSkin = await CreateWholeSkinFixtureAsync(
            labRoot,
            projectRoot,
            "dependency-preflight",
            loaded.Request!.Build.References.Race,
            loaded.Request.Build.Traits.Sex,
            loaded.Request.Build.References.DefaultOutfit);

        string outputParent = Path.Combine(projectRoot, "03-builds", "work");
        var stagingBefore = Directory.EnumerateDirectories(outputParent,
                ".racemenu-stage-*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputRoot = new WorkspacePath(Path.Combine(outputParent,
            $"schema5-dependency-preflight-{Environment.ProcessId}-{Guid.NewGuid():N}"));
        RaceMenuNpcExecutionRequest request = loaded.Request! with
        {
            Build = loaded.Request.Build with
            {
                OutputRoot = outputRoot,
                WholeSkinAuthority = wholeSkin.Authority
            }
        };
        var policy = new KOnlyWorkspacePolicy(labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var indexer = new BethesdaAssetIndexer();
        var provider = new BlankNpcProviderService(policy, labRoot, indexer);
        var pluginLoader = new SkyrimFaceRecordPluginAuthorityLoader(
            policy, labRoot);
        var wholeSkinReader = new RaceMenuNpcWholeSkinAuthorityReader(
            new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                pluginLoader,
                new SkyrimAssetAuthorityPlanner(indexer, policy, labRoot)),
            policy,
            labRoot);
        var service = new RaceMenuNpcBuildService(
            new RaceMenuNpcAppearancePlanService(
                new PresetService(policy, labRoot), provider, policy, labRoot),
            null!, null!, null!, indexer, policy, labRoot,
            faceGeomBuildService: null,
            faceTextureBuildService: null,
            qualifiedFaceGeomCarrierService: null,
            exactFaceTintEvidenceDecoder: null,
            wholeSkinAuthorityReader: wholeSkinReader);
        RaceMenuNpcExecutionResult result = await service.ExecuteAsync(
            request, null, CancellationToken.None);

        string[] expected =
        [
            "racemenu-build-facegeom-service-unavailable",
            "racemenu-build-face-texture-service-unavailable",
            "racemenu-build-durable-facegeom-service-unavailable",
            "racemenu-build-durable-facetint-service-unavailable"
        ];
        Assert(!result.Completed && expected.All(code =>
                   result.Diagnostics.Any(item => item.Code == code)),
            "Schema-5 did not report its complete missing-service preflight: " +
            Format(result.Diagnostics));
        Assert(!Directory.Exists(outputRoot.Value) && !File.Exists(outputRoot.Value),
            "Schema-5 missing-service preflight created an output root.");
        string[] newStages = Directory.EnumerateDirectories(outputParent,
                ".racemenu-stage-*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Except(stagingBefore, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert(newStages.Length == 0,
            "Schema-5 missing-service preflight created a staging root.");
        if (Directory.Exists(wholeSkin.Root))
            Directory.Delete(wholeSkin.Root, recursive: true);
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static WorkspacePath FindLabRootForSchema5() =>
        new(TestAuthorityWorkspace.ResolveLabRoot(
            AppContext.BaseDirectory));

    private sealed class Schema5Fixture : IDisposable
    {
        private readonly RaceMenuNpcExecutionRequest _request;
        private readonly RaceMenuNpcBuildService _service;
        private readonly string? _wholeSkinRoot;
        private readonly string _outputParent;
        private readonly HashSet<string> _stagingBefore;
        private readonly HashSet<string> _quarantineBefore;

        private Schema5Fixture(
            RaceMenuNpcExecutionRequest request,
            RaceMenuNpcBuildService service,
            WorkspacePath outputRoot,
            string? wholeSkinRoot)
        {
            _request = request;
            _service = service;
            OutputRoot = outputRoot;
            _wholeSkinRoot = wholeSkinRoot;
            _outputParent = Path.GetDirectoryName(outputRoot.Value)!;
            _stagingBefore = Enumerate(".racemenu-stage-*");
            _quarantineBefore = Enumerate(".npcmanager-rejected-*");
        }

        public WorkspacePath OutputRoot { get; }
        public WorkspacePath SourcePlugin =>
            _request.Build.ExistingNpcTarget?.SourcePlugin ??
            throw new InvalidOperationException("The fixture has no existing-NPC source target.");

        public string[] NewQuarantineRoots =>
            Enumerate(".npcmanager-rejected-*")
                .Except(_quarantineBefore, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public static async Task<Schema5Fixture> CreateAsync(
            string name,
            bool failOuterPostcheck,
            bool existingNpcTarget = false,
            bool applyBodySlide = true,
            bool includeWholeSkinAuthority = true,
            bool validWholeSkinAuthority = true,
            bool noDefaultOutfit = false)
        {
            WorkspacePath labRoot = FindLabRootForSchema5();
            string projectRoot = Path.Combine(labRoot.Value, "projects",
                "NpcManagerReimplementation");
            var requestFile = new WorkspacePath(Path.Combine(projectRoot,
                "01-source-copies", "gate2-emi2", "execution-request-v5b.json"));
            var loader = new RaceMenuNpcExecutionRequestFileLoader(labRoot);
            RaceMenuNpcExecutionRequestFileLoadResult loaded = await loader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    requestFile, HashFile(requestFile.Value)), CancellationToken.None);
            Assert(loaded.Loaded && loaded.Request is not null,
                "The real schema-5 request did not load: " + Format(loaded.Diagnostics));

            var outputRoot = new WorkspacePath(Path.Combine(projectRoot, "03-builds", "work",
                $"schema5-integration-{name}-{Environment.ProcessId}-{Guid.NewGuid():N}"));
            Assert(!Directory.Exists(outputRoot.Value) && !File.Exists(outputRoot.Value),
                "The schema-5 integration output was not fresh.");
            var loadedBuild = loaded.Request!.Build;
            if (noDefaultOutfit)
            {
                loadedBuild = loadedBuild with
                {
                    References = loadedBuild.References with
                    {
                        DefaultOutfit = null
                    }
                };
            }
            (RaceMenuNpcWholeSkinAuthority Authority, string Root)? wholeSkin = null;
            if (includeWholeSkinAuthority && !existingNpcTarget &&
                validWholeSkinAuthority)
            {
                wholeSkin = await CreateWholeSkinFixtureAsync(
                    labRoot,
                    projectRoot,
                    name,
                    loadedBuild.References.Race,
                    loadedBuild.Traits.Sex,
                    loadedBuild.References.DefaultOutfit);
            }
            var existingTarget = existingNpcTarget
                ? new RaceMenuExistingNpcTarget(
                    loadedBuild.ProviderContext.TemplatePlugin,
                    loadedBuild.ProviderContext.ExpectedTemplatePluginSha256,
                    loadedBuild.ProviderContext.TemplateNpcFormId)
                : null;
            RaceMenuNpcExecutionRequest request = loaded.Request! with
            {
                ApplyBodySlide = applyBodySlide,
                Build = loadedBuild with
                {
                    OutputRoot = outputRoot,
                    ExistingNpcTarget = existingTarget,
                    WholeSkinAuthority = includeWholeSkinAuthority
                        ? wholeSkin?.Authority ??
                          new RaceMenuNpcWholeSkinAuthority(
                              loadedBuild.PresetBundle.RecordAuthority.ManifestPath,
                              loadedBuild.PresetBundle.RecordAuthority.ExpectedManifestSha256)
                        : null
                }
            };
            return new Schema5Fixture(request,
                CreateSchema5Service(labRoot, failOuterPostcheck), outputRoot,
                wholeSkin?.Root);
        }

        public ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync() =>
            _service.ExecuteAsync(_request, null, CancellationToken.None);

        public void AssertNoNewStagingRoots()
        {
            string[] remaining = Enumerate(".racemenu-stage-*")
                .Except(_stagingBefore, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert(remaining.Length == 0,
                "Schema-5 execution retained owned staging roots: " +
                string.Join(", ", remaining));
        }

        public void Dispose()
        {
            DeleteOwned(OutputRoot.Value);
            foreach (string quarantine in NewQuarantineRoots) DeleteOwned(quarantine);
            if (_wholeSkinRoot is not null) DeleteOwned(_wholeSkinRoot);
        }

        private HashSet<string> Enumerate(string pattern) =>
            Directory.EnumerateFileSystemEntries(_outputParent, pattern,
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private void DeleteOwned(string path)
        {
            string full = Path.GetFullPath(path);
            string parent = Path.GetFullPath(_outputParent) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a non-owned schema-5 path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
        }
    }

    private static async Task<(
        RaceMenuNpcWholeSkinAuthority Authority,
        string Root)> CreateWholeSkinFixtureAsync(
        WorkspacePath labRoot,
        string projectRoot,
        string name,
        FormReference race,
        NpcSex sex,
        FormReference? defaultOutfit)
    {
        string root = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            $".schema5-whole-skin-{name}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);

        ModKey ownerKey =
            ModKey.FromNameAndExtension(race.Plugin.Value);
        ModKey providerKey = ModKey.FromNameAndExtension(
            "Schema5WholeSkinProvider.esp");
        string pluginPath = Path.Combine(
            dataRoot, providerKey.FileName.String);
        var mod = new SkyrimMod(
            providerKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = ownerKey });
        var raceKey = new FormKey(
            ownerKey, race.FormId.Value);
        var skinKey = new FormKey(ownerKey, 0x800);
        var bodyAddonKey = new FormKey(ownerKey, 0x801);
        var handAddonKey = new FormKey(ownerKey, 0x802);
        var footAddonKey = new FormKey(ownerKey, 0x803);
        var bodyTextureKey = new FormKey(ownerKey, 0x804);
        var handTextureKey = new FormKey(ownerKey, 0x805);
        var footTextureKey = new FormKey(ownerKey, 0x806);
        var retainedRaceKey =
            new FormKey(providerKey, 0x900);
        bool hasDefaultOutfit = defaultOutfit is not null;
        ModKey outfitOwner = defaultOutfit is { } selectedOutfit
            ? ModKey.FromNameAndExtension(selectedOutfit.Plugin.Value)
            : ownerKey;
        var outfitKey = new FormKey(
            outfitOwner,
            defaultOutfit?.FormId.Value ?? 0xA00);
        var outfitArmorKey = new FormKey(
            outfitOwner,
            0x1BE1A);
        var outfitAddonKey = new FormKey(
            outfitOwner,
            0x1BE18);
        if (outfitOwner != ownerKey)
        {
            mod.ModHeader.MasterReferences.Add(
                new MasterReference { Master = outfitOwner });
        }

        mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Schema5WholeSkinRace",
            Skin = new FormLinkNullable<IArmorGetter>(skinKey)
        });
        mod.Races.Add(new Race(
            retainedRaceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID =
                "Schema5OutfitRetainedProviderRace"
        });
        var skin = new Armor(skinKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Schema5WholeSkinNaked",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)(0x04 | 0x08 | 0x80)
            }
        };
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(bodyAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(handAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(footAddonKey));
        mod.Armors.Add(skin);

        mod.ArmorAddons.Add(BuildWholeSkinAddon(
            bodyAddonKey, raceKey, bodyTextureKey, 0x04, "Schema5SkinBody"));
        mod.ArmorAddons.Add(BuildWholeSkinAddon(
            handAddonKey, raceKey, handTextureKey, 0x08, "Schema5SkinHands"));
        mod.ArmorAddons.Add(BuildWholeSkinAddon(
            footAddonKey, raceKey, footTextureKey, 0x80, "Schema5SkinFeet"));
        mod.TextureSets.Add(BuildWholeSkinTextureSet(
            bodyTextureKey, "schema5/body"));
        mod.TextureSets.Add(BuildWholeSkinTextureSet(
            handTextureKey, "schema5/hands"));
        mod.TextureSets.Add(BuildWholeSkinTextureSet(
            footTextureKey, "schema5/feet"));
        var outfitAddon = new ArmorAddon(
            outfitAddonKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Schema5WinningExposedBodyAddon",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x04
            },
            SkinTexture =
                new GenderedItem<
                    IFormLinkNullableGetter<ITextureSetGetter>>(
                    new FormLinkNullable<ITextureSetGetter>(),
                    new FormLinkNullable<ITextureSetGetter>())
        };
        outfitAddon.AdditionalRaces.Add(
            new FormLink<IRaceGetter>(retainedRaceKey));
        if (hasDefaultOutfit)
            mod.ArmorAddons.Add(outfitAddon);
        var outfitArmor = new Armor(
            outfitArmorKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Schema5WinningExposedBodyArmor"
        };
        outfitArmor.Armature.Add(
            new FormLink<IArmorAddonGetter>(outfitAddonKey));
        if (hasDefaultOutfit)
            mod.Armors.Add(outfitArmor);
        var outfit = new Outfit(
            outfitKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Schema5WinningExposedBodyOutfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(
                    outfitArmorKey)
            ]
        };
        if (hasDefaultOutfit)
            mod.Outfits.Add(outfit);
        mod.WriteToBinary(new FilePath(pluginPath),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });

        ModKey winnerKey =
            ModKey.FromNameAndExtension("OutfitWinner.esp");
        string winnerPath = Path.Combine(
            dataRoot, winnerKey.FileName.String);
        var winnerMod =
            new SkyrimMod(winnerKey, SkyrimRelease.SkyrimSE);
        if (outfitOwner != winnerKey)
        {
            winnerMod.ModHeader.MasterReferences.Add(
                new MasterReference { Master = outfitOwner });
        }
        if (ownerKey != winnerKey &&
            ownerKey != outfitOwner)
        {
            winnerMod.ModHeader.MasterReferences.Add(
                new MasterReference { Master = ownerKey });
        }
        winnerMod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = providerKey });
        if (hasDefaultOutfit)
        {
            winnerMod.ArmorAddons.Add(outfitAddon.DeepCopy());
            winnerMod.Armors.Add(outfitArmor.DeepCopy());
            winnerMod.Outfits.Add(outfit.DeepCopy());
        }
        winnerMod.WriteToBinary(new FilePath(winnerPath),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });

        foreach (string stem in new[] { "body", "hands", "feet" })
        {
            foreach (string suffix in new[] { "_d", "_n", "_sk", "_s" })
            {
                string texturePath = Path.Combine(
                    dataRoot, "textures", "schema5", stem + suffix + ".dds");
                Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
                File.WriteAllBytes(texturePath,
                    Encoding.ASCII.GetBytes("DDS schema5 " + stem + suffix));
            }
        }

        var plugin = new SkyrimFaceRecordPluginAuthority(
            new PluginName(providerKey.FileName.String),
            new WorkspacePath(pluginPath),
            HashFile(pluginPath));
        var winnerPlugin = new SkyrimFaceRecordPluginAuthority(
            new PluginName(winnerKey.FileName.String),
            new WorkspacePath(winnerPath),
            HashFile(winnerPath));
        ImmutableArray<SkyrimFaceRecordPluginAuthority> pluginOrder =
            [plugin, winnerPlugin];
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        var indexer = new BethesdaAssetIndexer();
        var resolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
            new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot),
            new SkyrimAssetAuthorityPlanner(indexer, policy, labRoot));
        SkyrimNpcWholeSkinAuthorityResult resolved = await resolver.ResolveAsync(
            new SkyrimNpcWholeSkinAuthorityRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(dataRoot),
                race,
                sex,
                pluginOrder)
            {
                DefaultOutfit = defaultOutfit
            },
            CancellationToken.None);
        Assert(resolved.Accepted && resolved.Authority is not null,
            "The schema-5 whole-skin fixture did not resolve: " +
            Format(resolved.Diagnostics));

        var writer = new RaceMenuNpcWholeSkinAuthorityWriter(policy, labRoot);
        RaceMenuNpcWholeSkinAuthorityWriteResult written = await writer.WriteAsync(
            new RaceMenuNpcWholeSkinAuthorityWriteRequest(
                resolved.Authority!,
                new WorkspacePath(dataRoot),
                pluginOrder,
                new WorkspacePath(Path.Combine(root, "whole-skin-authority.json"))),
            CancellationToken.None);
        Assert(written.Written && written.Artifact is not null,
            "The schema-5 whole-skin fixture did not persist: " +
            Format(written.Diagnostics));
        return (written.Artifact!.Authority, root);
    }

    private static ArmorAddon BuildWholeSkinAddon(
        FormKey addon,
        FormKey race,
        FormKey textureSet,
        uint slotMask,
        string editorId) => new(addon, SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)slotMask
            },
            Race = new FormLinkNullable<IRaceGetter>(race),
            SkinTexture = new GenderedItem<
            IFormLinkNullableGetter<ITextureSetGetter>>(
            new FormLinkNullable<ITextureSetGetter>(textureSet),
            new FormLinkNullable<ITextureSetGetter>(textureSet))
        };

    private static TextureSet BuildWholeSkinTextureSet(
        FormKey key,
        string stem) => new(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = Path.GetFileName(stem) + "Skin",
            Diffuse = stem + "_d.dds",
            NormalOrGloss = stem + "_n.dds",
            GlowOrDetailMap = stem + "_sk.dds",
            BacklightMaskOrSpecular = stem + "_s.dds"
        };

    private static RaceMenuNpcBuildService CreateSchema5Service(
        WorkspacePath labRoot,
        bool failOuterPostcheck)
    {
        var policy = new KOnlyWorkspacePolicy(labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var assetIndexer = new BethesdaAssetIndexer();
        var provider = new BlankNpcProviderService(policy, labRoot, assetIndexer);
        IQualifiedFaceGeomCarrierService realCarrier =
            new QualifiedFaceGeomCarrierService(policy, labRoot);
        IQualifiedFaceGeomCarrierService carrier = failOuterPostcheck
            ? new FailThirdEvidenceVerification(realCarrier)
            : realCarrier;
        var decoder = new Bgra8FaceTintTextureDecoder(labRoot);
        var packageVerifier = new PackageVerifyService(
            new PackageManifestReader(policy, labRoot));
        var bodyGen = new BodyGenService(policy, labRoot);
        var blankBuild = new BlankNpcBuildService(
            NpcCreationComposition.Create(policy, labRoot),
            provider,
            carrier,
            new NpcManager.FaceGen.ExactOnlyFaceTintBuildService(),
            decoder,
            packageVerifier,
            policy,
            labRoot);
        var resolver = new SkyrimAssetContentResolver(policy, labRoot);
        var faceGeom = new RaceMenuNpcFaceGeomBuildService(
            new SkyrimFaceBakeAuthorityLoader(policy, labRoot, resolver),
            new BethesdaSkyrimFaceRecordRouteResolver(policy, labRoot),
            new SseSelectedHeadpartNifGeometryReader(),
            new SseRaceMenuFaceBakeService(),
            new RaceMenuCharGenFaceGeomMergeService(policy, labRoot));
        var faceTextures = new RaceMenuNpcFaceTextureBuildService(
            policy, labRoot, new InProcessDdsTextureDecoder(labRoot), decoder);
        var existingBuild = new ExistingNpcAppearanceBuildService(
            new NpcAppearanceOverrideService(policy, labRoot),
            bodyGen,
            carrier,
            decoder,
            packageVerifier,
            policy,
            labRoot);
        var pluginLoader = new SkyrimFaceRecordPluginAuthorityLoader(
            policy, labRoot);
        var wholeSkinReader = new RaceMenuNpcWholeSkinAuthorityReader(
            new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                pluginLoader,
                new SkyrimAssetAuthorityPlanner(
                    assetIndexer, policy, labRoot)),
            policy,
            labRoot);
        return new RaceMenuNpcBuildService(
            new RaceMenuNpcAppearancePlanService(
                new PresetService(policy, labRoot), provider, policy, labRoot),
            new BethesdaSkyrimFaceMorphSnapshotService(policy, labRoot),
            bodyGen,
            blankBuild,
            assetIndexer,
            policy,
            labRoot,
            faceGeom,
            faceTextures,
            carrier,
            decoder,
            existingBuild,
            wholeSkinAuthorityReader: wholeSkinReader);
    }

    private sealed class FailThirdEvidenceVerification(
        IQualifiedFaceGeomCarrierService inner) : IQualifiedFaceGeomCarrierService
    {
        private int _fileVerificationCalls;

        public ValueTask<QualifiedFaceGeomCarrierAnalysisResult> AnalyzeAsync(
            QualifiedFaceGeomCarrierAnalyzeRequest request,
            CancellationToken cancellationToken) =>
            inner.AnalyzeAsync(request, cancellationToken);

        public ValueTask<QualifiedFaceGeomCarrierMaterializationResult> ApplyAsync(
            QualifiedFaceGeomCarrierProposal proposal,
            CancellationToken cancellationToken) =>
            inner.ApplyAsync(proposal, cancellationToken);

        public ValueTask<QualifiedFaceGeomCarrierVerificationResult> VerifyAsync(
            QualifiedFaceGeomCarrierProposal proposal,
            CancellationToken cancellationToken) =>
            inner.VerifyAsync(proposal, cancellationToken);

        public ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyEvidenceAsync(
            WorkspacePath packageRoot,
            QualifiedFaceGeomCarrierMaterializationEvidence evidence,
            CancellationToken cancellationToken) =>
            inner.VerifyEvidenceAsync(packageRoot, evidence, cancellationToken);

        public ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult>
            VerifyEvidenceFileAsync(
                WorkspacePath packageRoot,
                AssetPath evidenceFile,
                CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _fileVerificationCalls) != 3)
                return inner.VerifyEvidenceFileAsync(packageRoot, evidenceFile,
                    cancellationToken);
            return ValueTask.FromResult(
                new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                    false, null, null, null, null, ImmutableArray<int>.Empty,
                    [new Diagnostic("test-durable-facegeom-postcheck-failure",
                        DiagnosticSeverity.Error,
                        "Injected failure at the outer post-staging durable FaceGeom check.")]));
        }
    }
}
