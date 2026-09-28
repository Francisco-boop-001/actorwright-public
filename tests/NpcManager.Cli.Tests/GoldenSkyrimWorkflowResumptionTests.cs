using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private const string SeedRequestDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PreviewChildDeadline =
        TimeSpan.FromSeconds(180);
    private static readonly string[] RendererAuthorityNames =
    [
        "blender-4.5.1-windows-x64",
        "blender-4.5.1-pynifly-profile",
        "directxtex-texconv-2026.5.7"
    ];
    private static readonly string[] SensitiveAuthorityKinds =
    [
        "humanVisualAcceptance",
        "gameRuntimeVerification",
        "promotionApproval"
    ];

    public static async Task RunAsync()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        ImmutableArray<string> contractErrors =
            AgentCommandRegistry.Validate(AgentCommandRegistry.All);
        Require(contractErrors.IsEmpty,
            "The protocol registry is not dispatchable: " +
            string.Join(" | ", contractErrors));
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string fixtureCatalog = Path.Combine(repositoryRoot, "tools", "release",
            "protocol-v2-workflow-probes", "catalog.json");
        Require(File.Exists(fixtureCatalog),
            "The Task 6 workflow probe catalog is not available from the repository root.");
        string ownedParent = Path.Combine(repositoryRoot, "artifacts", "test-work");
        Directory.CreateDirectory(ownedParent);
        string ownedRoot = Path.Combine(
            ownedParent,
            $"task-7-resumption-{Guid.NewGuid():N}");
        string copiedRoot = ownedRoot + "-copy";
        Directory.CreateDirectory(ownedRoot);
        bool completed = false;
        try
        {
            var root = new WorkspacePath(ownedRoot);
            TranscriptA transcriptA = await RunTranscriptAAsync(
                repositoryRoot, fixtureCatalog, root);
            await RunCriticalRefusalsAsync(
                repositoryRoot, fixtureCatalog, transcriptA,
                new WorkspacePath(copiedRoot));
            await RunTranscriptBAsync(root, transcriptA);
            await RunPluginTypeBuildAsync();
            completed = true;
            Console.WriteLine(
                $"EVIDENCE npc-preview-contact-sheet={transcriptA.ContactSheetEvidence.Value}");
        }
        finally
        {
            if (completed)
            {
                DeleteOwnedRoot(copiedRoot, ownedParent);
                DeleteOwnedRoot(ownedRoot, ownedParent);
            }
            else
            {
                Console.Error.WriteLine(
                    $"EVIDENCE failed-test-workspace={ownedRoot}; copied-workspace={copiedRoot}");
            }
        }
    }

    internal static void RunOwnedScratchCleanupRegression()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string parent = Path.Combine(repositoryRoot, "artifacts", "x");
        string successfulRoot;
        using (var scratch = new OwnedScratchDirectory(
                   parent, "cleanup-"))
        {
            successfulRoot = scratch.Root;
            File.WriteAllText(Path.Combine(successfulRoot, "sentinel.txt"), "owned");
        }
        Require(!Directory.Exists(successfulRoot),
            "A successful owned scratch operation retained its directory.");

        string exceptionalRoot = string.Empty;
        try
        {
            using var scratch = new OwnedScratchDirectory(
                parent, "cleanup-");
            exceptionalRoot = scratch.Root;
            File.WriteAllText(Path.Combine(exceptionalRoot, "sentinel.txt"), "owned");
            throw new InvalidOperationException("expected scratch cleanup regression failure");
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "expected scratch cleanup regression failure")
        {
        }
        Require(!Directory.Exists(exceptionalRoot),
            "An exceptional owned scratch operation retained its directory.");

        bool malformedPrefixRefused = false;
        try
        {
            using var rejected = new OwnedScratchDirectory(parent, $"nested{Path.DirectorySeparatorChar}bad-");
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "Owned scratch cleanup requires a simple directory prefix.")
        {
            malformedPrefixRefused = true;
        }
        Require(malformedPrefixRefused,
            "Owned scratch cleanup accepted a directory-bearing prefix.");

        bool outsideParentRefused = false;
        try
        {
            using var rejected = new OwnedScratchDirectory(
                Path.Combine(Path.GetTempPath(), "actorwright-outside-scratch"),
                "bad-");
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "Owned scratch cleanup requires a repository-local parent.")
        {
            outsideParentRefused = true;
        }
        Require(outsideParentRefused,
            "Owned scratch cleanup accepted a parent outside the repository.");

        string reparseTarget = Path.Combine(parent, $"target-{Guid.NewGuid():N}");
        string reparseParent = Path.Combine(parent, $"link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reparseTarget);
        bool reparseCreated = false;
        try
        {
            reparseCreated = PhysicalReparseFixture.TryCreateDirectoryLink(
                reparseParent,
                reparseTarget,
                parent);

            if (reparseCreated)
            {
                bool reparseParentRefused = false;
                try
                {
                    using var rejected = new OwnedScratchDirectory(reparseParent, "bad-");
                }
                catch (InvalidOperationException exception) when (
                    exception.Message == "Owned scratch cleanup refused a non-ordinary parent path.")
                {
                    reparseParentRefused = true;
                }
                Require(reparseParentRefused,
                    "Owned scratch cleanup accepted a reparse parent.");
            }
        }
        finally
        {
            if (reparseCreated && Directory.Exists(reparseParent))
                Directory.Delete(reparseParent);
            if (Directory.Exists(reparseTarget))
                Directory.Delete(reparseTarget, recursive: true);
        }
    }

    internal static async Task RunPreflightDependencyClosureAsync()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string fixtureCatalog = Path.Combine(repositoryRoot, "tools", "release",
            "protocol-v2-workflow-probes", "catalog.json");
        string ownedRoot = Path.Combine(repositoryRoot, "artifacts", "test-work",
            $"preflight-closure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var root = new WorkspacePath(ownedRoot);
            MaterializeProbeFixtures(repositoryRoot, fixtureCatalog,
                "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var request = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(repositoryRoot, root, request, HashFile(preset));
            await AssertPreflightMasterAndWholeSkinClosureAsync(root, request, preset);
            const string tint = "textures/actors/character/character assets/tintmasks/femalehead.dds";
            File.Delete(Path.Combine(root.Value, "Data", tint));
            const string tri = "meshes/Actors/Character/Actorwright/Head.tri";
            File.Delete(Path.Combine(root.Value, "Data", tri));
            var output = Child(root, "npc-preflight", "closure.json");
            ProtocolInvocation preflight = await RunCliAsync(root,
                "npc", "create-from-jslot", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value,
                "--preflight-output", output.Value);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(output.Value));
            Require(!document.RootElement.GetProperty("readyForBuild").GetBoolean(),
                "Real JSlot preflight reported ready after the selected tint mask was removed.");
            Require(document.RootElement.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                    row.GetProperty("kind").GetString() == "texture" &&
                    row.GetProperty("path").GetString() == tint &&
                    row.GetProperty("status").GetString() == "missing"),
                "Real JSlot preflight did not enumerate the missing tint mask.");
            Require(document.RootElement.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                    row.GetProperty("kind").GetString() == "tri" &&
                    string.Equals(row.GetProperty("path").GetString(), tri,
                        StringComparison.OrdinalIgnoreCase) &&
                    row.GetProperty("status").GetString() == "missing"),
                "Preflight stopped before enumerating the missing head-part TRI alongside the tint mask.");
            Require(JsonNode.DeepEquals(JsonNode.Parse(preflight.Root.GetProperty("dependencyClosure").GetRawText()),
                    JsonNode.Parse(document.RootElement.GetProperty("dependencyClosure").GetRawText())),
                "V1 response omitted the persisted dependency closure.");
            var codec = new NpcBuildPreflightDocumentCodec(root);
            NpcBuildPreflightDocument reopened = await codec.ReadExactAsync(output,
                new Sha256Hash(HashFile(output)), CancellationToken.None);
            JsonObject retained = JsonNode.Parse(File.ReadAllBytes(output.Value))!.AsObject();
            retained.Remove("dependencyClosure");
            var retainedPath = Child(root, "npc-preflight", "retained-without-closure.json");
            byte[] retainedBytes = Encoding.UTF8.GetBytes(retained.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(retainedPath.Value, retainedBytes);
            NpcBuildPreflightDocument old = await codec.ReadExactAsync(retainedPath,
                new Sha256Hash(Hash(retainedBytes)), CancellationToken.None);
            Require(old.Utf8Json.AsSpan().SequenceEqual(retainedBytes) && old.Value.DependencyClosure is null &&
                    reopened.Value.DependencyClosure is { Length: > 0 },
                "Optional dependencyClosure changed retained canonical document bytes.");
            string catalogDirectory = Path.Combine(root.Value, "Data", "meshes", "actors", "character",
                "FaceGenMorphs", "ActorwrightBlankNpcProvider.esp");
            SkyrimTestPluginFactory.CreateGoldenNpcProviderWithHeadTexture(
                Child(root, "provider-source", "ActorwrightBlankNpcProvider.esp"),
                Child(root, "Data", "ActorwrightBlankNpcProvider.esp"),
                includeChargenMorph: true);
            Directory.CreateDirectory(catalogDirectory);
            File.WriteAllText(Path.Combine(catalogDirectory, "races.ini"), "NordRace=missing.slider,../unsafe.slider\n");
            File.WriteAllText(Path.Combine(catalogDirectory, "morphs.ini"),
                "extension=actors/character/Actorwright/Head.tri,missing-extension.tri\n");
            File.WriteAllText(preset.Value, File.ReadAllText(preset.Value).Replace(tint,
                "textures/../escaped.dds", StringComparison.Ordinal));
            var additionalPath = Child(root, "npc-preflight", "additional-closure.json");
            ProtocolInvocation additional = await RunCliAsync(root,
                "npc", "create-from-jslot", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value,
                "--preflight-output", additionalPath.Value);
            JsonElement closure = additional.Root.GetProperty("dependencyClosure");
            foreach ((string path, string status) in new[]
                     {
                         ("textures/../escaped.dds", "nonCanonicalPath"),
                         ("meshes/actors/character/FaceGenMorphs/ActorwrightBlankNpcProvider.esp/../unsafe.slider", "nonCanonicalPath"),
                         ("meshes/actors/character/FaceGenMorphs/ActorwrightBlankNpcProvider.esp/missing.slider", "missing"),
                         ("meshes/actors/character/FaceGenMorphs/morphs/missing-extension.tri", "missing")
                     })
                Require(closure.EnumerateArray().Any(row =>
                        string.Equals(row.GetProperty("path").GetString(), path, StringComparison.OrdinalIgnoreCase) &&
                        row.GetProperty("status").GetString() == status),
                    $"Preflight omitted '{path}' ({status}) while another dependency failed: {closure.GetRawText()}");
            File.WriteAllText(Path.Combine(catalogDirectory, "races.ini"), "NordRace=present.slider\n");
            File.WriteAllBytes(Path.Combine(catalogDirectory, "present.slider"), [0x81]);
            foreach (bool unsafeMorph in new[] { false, true })
            {
                if (unsafeMorph)
                {
                    File.WriteAllText(Path.Combine(catalogDirectory, "present.slider"), "# valid empty slider\n");
                    string earlierCatalog = Path.Combine(Path.GetDirectoryName(catalogDirectory)!, "Skyrim.esm");
                    Directory.CreateDirectory(earlierCatalog);
                    File.WriteAllText(Path.Combine(earlierCatalog, "morphs.ini"),
                        "extension=actors/character/Actorwright/Head.tri,../unsafe-extension.tri,sibling-missing.tri\n");
                }
                var malformedOutput = Child(root, "npc-preflight", unsafeMorph ? "unsafe-morph-closure.json" : "malformed-slider-closure.json");
                ProtocolInvocation malformed = await RunCliAsync(root,
                    "npc", "create-from-jslot", "--json",
                    "--request", request.Value, "--request-sha256", HashFile(request),
                    "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                    "--data-root", Child(root, "Data").Value,
                    "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                    "--companion-root", Child(root, "companion").Value,
                    "--preflight-output", malformedOutput.Value);
                using JsonDocument malformedDocument = JsonDocument.Parse(File.ReadAllBytes(malformedOutput.Value));
                Require(!malformedDocument.RootElement.GetProperty("readyForBuild").GetBoolean(),
                    "Malformed catalog must remain refused while dependencies are observed.");
                Require(malformed.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                        row.GetProperty("code").GetString() == (unsafeMorph ? "racemenu-catalog-morph-path" : "racemenu-catalog-encoding")),
                    "The real parser did not refuse the malformed catalog fixture.");
                JsonElement malformedClosure = malformed.Root.GetProperty("dependencyClosure");
                foreach ((string path, string status) in unsafeMorph
                    ? new[] { ("missing-extension.tri", "missing"), ("../unsafe-extension.tri", "nonCanonicalPath"), ("sibling-missing.tri", "missing") }
                    : new[] { ("missing-extension.tri", "missing") })
                    Require(malformedClosure.EnumerateArray().Any(row =>
                            string.Equals(row.GetProperty("path").GetString(),
                                "meshes/actors/character/FaceGenMorphs/morphs/" + path, StringComparison.OrdinalIgnoreCase) &&
                            row.GetProperty("status").GetString() == status),
                        $"Malformed {(unsafeMorph ? "morph" : "slider")} catalog hid declared dependency '{path}' ({status}): {malformedClosure.GetRawText()}");
            }
            await AssertPreflightPhysicsAggregationAsync(root);
            Require(!Directory.Exists(Child(root, "companion").Value) &&
                    !Directory.Exists(Child(root, "planned-npc-output").Value),
                "Dependency discovery crossed the build write boundary.");
        }
        finally
        {
            string canonical = Path.GetFullPath(ownedRoot);
            Require(canonical.StartsWith(Path.Combine(repositoryRoot,
                    "artifacts", "test-work") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(canonical).StartsWith("preflight-closure-",
                        StringComparison.Ordinal),
                "Preflight cleanup refused a non-owned root.");
            RequireOrdinaryTree(canonical);
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static async Task AssertPreflightMasterAndWholeSkinClosureAsync(
        WorkspacePath root,
        WorkspacePath request,
        WorkspacePath preset)
    {
        string originalRequest = File.ReadAllText(request.Value);
        const string skyrimVoice = "\"voice\":\"Skyrim.esm|0x00013ADC\"";
        const string missingMasterVoice = "\"voice\":\"Dawnguard.esm|0x00013ADC\"";
        File.WriteAllText(request.Value,
            ReplaceExactly(originalRequest, skyrimVoice, missingMasterVoice),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var missingMasterOutput = Child(root, "npc-preflight", "missing-master-closure.json");
        ProtocolInvocation missingMaster = await RunCliAsync(root,
            "npc", "create-from-jslot", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request),
            "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value,
            "--preflight-output", missingMasterOutput.Value);
        AssertDependencyClosureRefusal(missingMaster, "npc-create-master-provider-missing",
            "plugin", "Dawnguard.esm");

        File.WriteAllText(request.Value, originalRequest,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        const string skinTexture = "textures/Actorwright/Skin/body_sk.dds";
        string skinTexturePath = Path.Combine(root.Value, "Data",
            skinTexture.Replace('/', Path.DirectorySeparatorChar));
        byte[] skinTextureBytes = File.ReadAllBytes(skinTexturePath);
        File.Delete(skinTexturePath);
        try
        {
            var missingSkinOutput = Child(root, "npc-preflight", "missing-skin-closure.json");
            ProtocolInvocation missingSkin = await RunCliAsync(root,
                "npc", "create-from-jslot", "--json",
                "--request", request.Value, "--request-sha256", HashFile(request),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value,
                "--preflight-output", missingSkinOutput.Value);
            AssertDependencyClosureRefusal(missingSkin, "skyrim-asset-authority-missing",
                "texture", skinTexture);
        }
        finally
        {
            File.WriteAllBytes(skinTexturePath, skinTextureBytes);
        }
    }

    private static void AssertDependencyClosureRefusal(
        ProtocolInvocation invocation,
        string code,
        string kind,
        string path)
    {
        JsonElement closure = invocation.Root.GetProperty("dependencyClosure");
        Require(invocation.Root.GetProperty("requiredGates").EnumerateArray().Any(row =>
                row.GetProperty("id").GetString() == "dependency-closure" &&
                !row.GetProperty("passed").GetBoolean()),
            $"Preflight did not fail dependency-closure for '{path}': {invocation.Root}");
        Require(invocation.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                row.GetProperty("code").GetString() == code),
            $"Preflight did not report build diagnostic '{code}' for '{path}': {invocation.Root}");
        Require(closure.EnumerateArray().Any(row =>
                row.GetProperty("kind").GetString() == kind &&
                string.Equals(row.GetProperty("path").GetString(), path,
                    StringComparison.OrdinalIgnoreCase) &&
                row.GetProperty("status").GetString() == "missing"),
            $"Preflight did not enumerate missing {kind} '{path}': {closure.GetRawText()}");
    }

    private static async Task AssertPreflightPhysicsAggregationAsync(WorkspacePath root)
    {
        var data = Child(root, "physics-Data");
        const string xml = "SKSE/Plugins/hdtSkinnedMeshConfigs/present.xml";
        string xmlFile = Path.Combine(data.Value, xml);
        Directory.CreateDirectory(Path.GetDirectoryName(xmlFile)!);
        File.WriteAllText(xmlFile,
            "<hdtSmp><bones><bone name=\"HairBone\" /></bones><colliders><collider path=\"meshes/missing-a.nif\" /><collider path=\"meshes/missing-b.nif\" /></colliders></hdtSmp>");
        var policy = new KOnlyWorkspacePolicy(root, Child(root, "protected"));
        var resolver = new ExternalHeadPartPhysicsBindingResolver(policy, root);
        // Typed NIF observations are the input boundary of the XML observer;
        // the real bounded reader must aggregate all their declared files.
        var observed = new ExternalHeadPartProviderNifReadResult(true, [], [], [])
        {
            ShapeNames = ["HairShape"], PhysicsObjectLocators = [xml]
        };
        const string absentXml = "SKSE/Plugins/hdtSkinnedMeshConfigs/absent.xml";
        var result = await resolver.ObservePreflightDependenciesAsync(data,
            [(new AssetPath("meshes/hair-a.nif"), observed),
             (new AssetPath("meshes/hair-b.nif"), observed with { PhysicsObjectLocators = [absentXml] })],
            CancellationToken.None);
        foreach (string path in new[] { "meshes/missing-a.nif", "meshes/missing-b.nif", absentXml })
            Require(result.Dependencies.Any(row => row.Path == path && row.Status == "missing"),
                $"Physics preflight stopped before reporting '{path}'.");
        Require(result.Dependencies.Any(row => row.Path == xml && row.Status == "present"),
            "Physics preflight lost the available XML when its colliders were absent.");
    }

    private static async Task<TranscriptA> RunTranscriptAAsync(
        string repositoryRoot,
        string fixtureCatalog,
        WorkspacePath root)
    {
        MaterializeProbeFixtures(
            repositoryRoot, fixtureCatalog, "workspace preflight", root);
        MaterializeProbeFixtures(
            repositoryRoot, fixtureCatalog, "preset inspect", root);
        MaterializeProbeFixtures(
            repositoryRoot, fixtureCatalog, "npc create-from-jslot", root);
        var presetPath = Child(root, "npc-preflight", "fixture.jslot");
        PrepareGoldenPresetFixture(presetPath);
        string presetSha256 = HashFile(presetPath);
        var requestPath = Child(root, "npc-preflight", "request.json");
        CorrectNpcRequestFixture(
            repositoryRoot, root, requestPath, presetSha256);
        string requestSha256 = HashFile(requestPath);
        WorkspacePath loadOrderPath = WriteCanonicalLoadOrder(root);
        Directory.CreateDirectory(Path.Combine(root.Value, "evidence"));
        Directory.CreateDirectory(Path.Combine(root.Value, "workflow"));
        var intakePath = Child(root, "evidence", "reviewed-intake.json");
        var workspaceWorkflowPath = Child(root, "workflow", "workspace.json");

        string[] workspaceArguments =
        [
            "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse",
            "--workspace-root", root.Value,
            "--data-root", Child(root, "Data").Value,
            "--output-root", Child(root, "reserved-output").Value,
            "--load-order", loadOrderPath.Value,
            "--intake-output", intakePath.Value,
            "--npc-editor-id", "ActorwrightResumptionNpc",
            "--workflow-output", workspaceWorkflowPath.Value
        ];
        ProtocolInvocation workspace = await RunCliAsync(
            root, workspaceArguments);
        RequireSucceeded(workspace, "workspace preflight");
        RequireEffects(workspace);
        EnvelopeArtifact intakeArtifact = RequireArtifact(
            workspace, WorkflowArtifactKinds.ReviewedWorkspaceIntake);
        EnvelopeArtifact workspaceWorkflowArtifact = RequireArtifact(
            workspace, "workflow-bundle");
        RequireBinding(intakeArtifact, intakePath,
            "npcmanager-reviewed-game-intake/2",
            "workspace preflight");
        RequireBinding(workspaceWorkflowArtifact, workspaceWorkflowPath,
            AgentWorkflowSchemas.BundleV1,
            "workspace preflight");

        var intakeCodec = new FaceGeomHairRegionsDocumentCodec(root);
        ReviewedGameIntakeDocumentAuthority intake =
            await intakeCodec.LoadReviewedIntakeAsync(
                intakePath, CancellationToken.None);
        Require(intake.Document.ByteLength == intakeArtifact.Size &&
                string.Equals(
                    intake.Document.Sha256.Value,
                    intakeArtifact.Sha256,
                    StringComparison.OrdinalIgnoreCase),
            "Workspace Preflight envelope did not bind the strict reviewed intake.");
        AgentWorkflowBundleDocument workspaceBundle = LoadWorkflow(
            root,
            workspaceWorkflowPath,
            workspaceWorkflowArtifact.Sha256,
            AgentWorkflowPhase.Analyze,
            [WorkflowArtifactKinds.ReviewedWorkspaceIntake],
            "preset inspect");

        RemovePriorJournal(root, required: true);
        var inspectionPath = Child(root, "evidence", "preset-inspection.json");
        var presetWorkflowPath = Child(root, "workflow", "preset.json");
        ProtocolInvocation preset = await RunCliAsync(root,
            "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse",
            "--input", presetPath.Value,
            "--input-sha256", presetSha256,
            "--inspection-output", inspectionPath.Value,
            "--workflow-bundle", workspaceWorkflowPath.Value,
            "--workflow-bundle-sha256", workspaceWorkflowArtifact.Sha256,
            "--workflow-output", presetWorkflowPath.Value);
        RequireSucceeded(preset, "preset inspect");
        RequireEffects(preset, expectedCompletedWrites: 2);
        EnvelopeArtifact sourceArtifact = RequireArtifact(
            preset, WorkflowArtifactKinds.RaceMenuJslot);
        EnvelopeArtifact inspectionArtifact = RequireArtifact(
            preset, "preset-inspection");
        EnvelopeArtifact presetWorkflowArtifact = RequireArtifact(
            preset, "workflow-bundle");
        RequireBinding(sourceArtifact, presetPath, "application/json",
            "preset inspect");
        RequireBinding(inspectionArtifact, inspectionPath,
            PresetInspectionSchemas.CurrentDocument, "preset inspect");
        var presetStore = new PresetInspectionArtifactStore(root);
        PresetExactInputDocument source = await presetStore.ReadExactAsync(
            presetPath, presetSha256, CancellationToken.None);
        PresetInspectionArtifactDocument inspection = presetStore.Load(
            inspectionPath, inspectionArtifact.Sha256);
        Require(source.Size == sourceArtifact.Size &&
                source.Sha256 == sourceArtifact.Sha256 &&
                inspection.Size == inspectionArtifact.Size &&
                inspection.Sha256 == inspectionArtifact.Sha256,
            "Preset source or inspection receipt did not survive strict reload.");
        AgentWorkflowBundleDocument presetBundle = LoadWorkflow(
            root,
            presetWorkflowPath,
            presetWorkflowArtifact.Sha256,
            AgentWorkflowPhase.Analyze,
            [
                WorkflowArtifactKinds.RaceMenuJslot,
                WorkflowArtifactKinds.ReviewedWorkspaceIntake
            ],
            "npc create-from-jslot");

        RemovePriorJournal(root, required: true);
        var preflightPath = Child(root, "evidence", "npc-preflight.json");
        var preflightWorkflowPath = Child(root, "workflow", "preflight.json");
        var tintPath = Child(root, "Data", "textures", "actors", "character",
            "character assets", "tintmasks", "femalehead.dds");
        byte[] tintBytes = File.ReadAllBytes(tintPath.Value);
        var blockedPath = Child(root, "evidence", "blocked-preflight.json");
        var blockedWorkflow = Child(root, "workflow", "blocked-preflight.json");
        try
        {
            File.Delete(tintPath.Value);
            ProtocolInvocation blocked = await RunCliAsync(root,
                "npc", "create-from-jslot", "--protocol", "2", "--json",
                "--request", requestPath.Value, "--request-sha256", requestSha256,
                "--preset", presetPath.Value, "--preset-sha256", presetSha256,
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value,
                "--preflight-output", blockedPath.Value,
                "--workflow-bundle", presetWorkflowPath.Value,
                "--workflow-bundle-sha256", presetWorkflowArtifact.Sha256,
                "--workflow-output", blockedWorkflow.Value);
            JsonElement result = blocked.Root.GetProperty("result");
            Require(blocked.ExitCode != 0 && result.GetProperty("created").GetBoolean() &&
                    !result.GetProperty("readyForBuild").GetBoolean() &&
                    result.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                        row.GetProperty("path").GetString() == "textures/actors/character/character assets/tintmasks/femalehead.dds" &&
                        row.GetProperty("status").GetString() == "missing") &&
                    !File.Exists(blockedWorkflow.Value),
                "Blocked V2 preflight discarded its dependency evidence or advanced the workflow.");
            _ = RequireArtifact(blocked, WorkflowArtifactKinds.NpcBuildPreflight);
        }
        finally
        {
            File.WriteAllBytes(tintPath.Value, tintBytes);
        }
        RemovePriorJournal(root, required: true);
        ProtocolInvocation preflight = await RunCliAsync(root,
            "npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", requestPath.Value,
            "--request-sha256", requestSha256,
            "--preset", presetPath.Value,
            "--preset-sha256", presetSha256,
            "--data-root", Child(root, "Data").Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value,
            "--preflight-output", preflightPath.Value,
            "--workflow-bundle", presetWorkflowPath.Value,
            "--workflow-bundle-sha256", presetWorkflowArtifact.Sha256,
            "--workflow-output", preflightWorkflowPath.Value);
        RequireSucceeded(preflight, "npc create-from-jslot");
        RequireEffects(preflight);
        EnvelopeArtifact preflightArtifact = RequireArtifact(
            preflight, WorkflowArtifactKinds.NpcBuildPreflight);
        EnvelopeArtifact preflightWorkflowArtifact = RequireArtifact(
            preflight, "workflow-bundle");
        RequireBinding(preflightArtifact, preflightPath,
            NpcBuildPreflightSchemas.Artifact, "npc create-from-jslot");
        var preflightCodec = new NpcBuildPreflightDocumentCodec(root);
        NpcBuildPreflightDocument preflightDocument =
            await preflightCodec.ReadExactAsync(
                preflightPath,
                new Sha256Hash(preflightArtifact.Sha256),
                CancellationToken.None);
        Require(preflightDocument.Utf8Json.Length == preflightArtifact.Size,
            "NPC preflight did not survive strict independent reload.");
        AgentWorkflowBundleDocument preflightBundle = LoadWorkflow(
            root,
            preflightWorkflowPath,
            preflightWorkflowArtifact.Sha256,
            AgentWorkflowPhase.Apply,
            [
                WorkflowArtifactKinds.NpcBuildPreflight,
                WorkflowArtifactKinds.RaceMenuJslot,
                WorkflowArtifactKinds.ReviewedWorkspaceIntake
            ],
            "npc create-from-jslot");
        Require(!Directory.Exists(Child(root, "planned-npc-output").Value) &&
                !Directory.Exists(Child(root, "companion").Value),
            "The preflight-only transcript created an NPC or companion output root.");

        WorkspacePath contactSheetEvidence = await RunBuildAndPreviewAsync(
            repositoryRoot,
            root,
            intakePath,
            requestPath,
            requestSha256,
            presetPath,
            presetSha256,
            preflightPath,
            preflightArtifact.Sha256,
            preflightWorkflowPath,
            preflightWorkflowArtifact.Sha256);

        return new TranscriptA(
            root,
            intakePath,
            intakeArtifact,
            workspaceWorkflowPath,
            workspaceWorkflowArtifact,
            workspaceBundle,
            presetPath,
            presetSha256,
            inspectionPath,
            presetWorkflowPath,
            presetWorkflowArtifact,
            presetBundle,
            preflightBundle,
            contactSheetEvidence);
    }

    private static async Task RunTranscriptBAsync(
        WorkspacePath root,
        TranscriptA transcript)
    {
        Require(transcript.Root == root,
            "Finish resumption did not use Transcript A's generated package workspace.");
        Directory.CreateDirectory(Path.Combine(root.Value, "finish-evidence"));
        Directory.CreateDirectory(Path.Combine(root.Value, "workflow"));
        var policy = new KOnlyWorkspacePolicy(
            root, new WorkspacePath(@"F:\ExampleGame"));
        var store = new SkyrimNpcFinishCoreVerificationArtifactStore(
            policy, root);
        WorkspacePath manifestPath = await CreateCurrentFinishResumptionFixtureAsync(
            root, transcript);
        string manifestSha256 = HashFile(manifestPath);
        SkyrimNpcFinishCoreVerifiedManifestDocument manifest =
            store.LoadManifest(manifestPath, manifestSha256);
        Require(File.Exists(manifest.ArchivePath.Value) &&
                new FileInfo(manifest.ArchivePath.Value).Length ==
                    manifest.ArchiveSize &&
                HashFile(manifest.ArchivePath) == manifest.ArchiveSha256,
            "Finish archive did not independently match the strict manifest binding.");

        var codec = new AgentWorkflowBundleCodec(policy, root);
        var lifecycle = new AgentWorkflowBundleTransitionService(codec);
        var finishSeedPath = Child(root, "workflow", "finish-seed.json");
        var manifestBinding = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcFinishCoreManifest,
            SkyrimNpcFinishCoreManifest.SchemaIdentifier,
            manifest.Path,
            manifest.Size,
            manifest.Sha256,
            "npc finish apply",
            SeedRequestDigest,
            []);
        AgentWorkflowBundleTransition seed = lifecycle.WriteInitial(
            new WorkflowNpcIdentity(
                "ActorwrightFinishResumptionNpc",
                "Actorwright Finish Resumption NPC",
                manifest.Manifest.Plugin?.Value ??
                    throw new InvalidOperationException(
                        "The verified Finish manifest omitted its plugin identity."),
                "00000800"),
            SeedRequestDigest,
            [manifestBinding],
            finishSeedPath);
        _ = LoadWorkflow(
            root,
            finishSeedPath,
            seed.Document.Sha256,
            AgentWorkflowPhase.Verify,
            [WorkflowArtifactKinds.NpcFinishCoreManifest],
            "npc finish verify");

        RemovePriorJournal(root);
        ProtocolInvocation finish = await RunCliAsync(root,
            "npc", "finish", "verify", "--json",
            "--manifest", manifestPath.Value,
            "--manifest-sha256", manifestSha256);
        JsonElement verification = finish.Root.GetProperty("verification");
        Require(finish.ExitCode == 0 &&
                finish.Root.GetProperty("command").GetString() ==
                    "npc finish verify" &&
                finish.Root.GetProperty("verified").GetBoolean() &&
                finish.Root.GetProperty("status").GetString() ==
                    "StaticPassRuntimeRequired" &&
                string.Equals(
                    verification.GetProperty("archiveSha256")
                        .GetProperty("value").GetString(),
                    manifest.ArchiveSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                !verification.GetProperty("runtimeAuthority").GetBoolean() &&
                !verification.GetProperty("visualAuthority").GetBoolean() &&
                finish.Root.GetProperty("diagnostics").EnumerateArray()
                    .All(item => item.GetProperty("severity").GetString() != "error"),
            "npc finish verify did not produce one successful legacy result: " +
            finish.Root.GetRawText() + " stderr=" + finish.StdErr);
        AgentWorkflowBundleDocument resumable = LoadWorkflow(
            root,
            finishSeedPath,
            seed.Document.Sha256,
            AgentWorkflowPhase.Verify,
            [WorkflowArtifactKinds.NpcFinishCoreManifest],
            "npc finish verify");
        Require(resumable.Bundle.RequestDigest == SeedRequestDigest &&
                HashFile(finishSeedPath) == seed.Document.Sha256,
            "Legacy Finish Verify changed or invalidated the resumable workflow seed.");
    }

    private static async Task<WorkspacePath> RunBuildAndPreviewAsync(
        string repositoryRoot,
        WorkspacePath root,
        WorkspacePath intakePath,
        WorkspacePath requestPath,
        string requestSha256,
        WorkspacePath presetPath,
        string presetSha256,
        WorkspacePath preflightPath,
        string preflightSha256,
        WorkspacePath preflightWorkflowPath,
        string preflightWorkflowSha256)
    {
        RemovePriorJournal(root, required: true);
        var packageWorkflowPath = Child(root, "workflow", "package.json");
        ProtocolInvocation build = await RunCliAsync(root,
            "npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", requestPath.Value,
            "--request-sha256", requestSha256,
            "--preset", presetPath.Value,
            "--preset-sha256", presetSha256,
            "--data-root", Child(root, "Data").Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value,
            "--reviewed-preflight", preflightPath.Value,
            "--reviewed-preflight-sha256", preflightSha256,
            "--workflow-bundle", preflightWorkflowPath.Value,
            "--workflow-bundle-sha256", preflightWorkflowSha256,
            "--workflow-output", packageWorkflowPath.Value);
        RequireSucceeded(build, "npc create-from-jslot");
        RequireEffects(build);
        JsonElement buildResult = build.Root.GetProperty("result");
        var manifestPath = new WorkspacePath(
            buildResult.GetProperty("manifestPath").GetString() ?? "");
        string manifestSha256 =
            buildResult.GetProperty("manifestSha256").GetString() ?? "";
        string outputPlugin =
            buildResult.GetProperty("outputPlugin").GetString() ?? "";
        string targetFormId =
            buildResult.GetProperty("targetFormId").GetString() ?? "";
        EnvelopeArtifact packageArtifact = RequireArtifact(
            build, WorkflowArtifactKinds.NpcPackageManifest);
        EnvelopeArtifact packageWorkflowArtifact = RequireArtifact(
            build, "workflow-bundle");
        RequireBinding(packageArtifact, manifestPath, "application/json",
            "npc create-from-jslot");
        Require(packageArtifact.Sha256 == manifestSha256 &&
                outputPlugin == "PackagedNpc.esp" &&
                targetFormId == "00000800",
            "The actual build result changed package or NPC identity.");

        var policy = new KOnlyWorkspacePolicy(
            root, new WorkspacePath(@"F:\ExampleGame"));
        PackageVerifyResult package = await new PackageVerifyService(
                new PackageManifestReader(policy, root))
            .VerifyAsync(
                new PackageVerifyRequest(manifestPath),
                CancellationToken.None);
        PackageVerificationArtifact verified = package.Artifact ??
            throw new InvalidOperationException(
                "The actual build did not return package verification evidence.");
        Require(package.Verified &&
                verified is
                {
                    NoUndeclaredFiles: true,
                    NoWrite: true,
                    RuntimeProof: false
                } &&
                verified.Files.All(item => item.Matches),
            "The actual build did not produce the exact verified plugin/FaceGeom/FaceTint closure.");
        RequirePackageFile(
            verified,
            "plugin",
            $"Data/{outputPlugin}");
        RequirePackageFile(
            verified,
            "facegeom",
            $"Data/meshes/actors/character/FaceGenData/FaceGeom/{outputPlugin}/{targetFormId}.nif");
        RequirePackageFile(
            verified,
            "facetint",
            $"Data/textures/actors/character/FaceGenData/FaceTint/{outputPlugin}/{targetFormId}.dds");
        AgentWorkflowBundleDocument packageWorkflow = LoadWorkflow(
            root,
            packageWorkflowPath,
            packageWorkflowArtifact.Sha256,
            AgentWorkflowPhase.Verify,
            [
                WorkflowArtifactKinds.NpcPackageManifest,
                WorkflowArtifactKinds.ReviewedWorkspaceIntake
            ],
            "preview npc");
        WorkflowArtifactBinding retainedIntake = packageWorkflow.Bundle.Artifacts
            .Single(item => item.Kind ==
                WorkflowArtifactKinds.ReviewedWorkspaceIntake);
        WorkflowArtifactBinding retainedPackage = packageWorkflow.Bundle.Artifacts
            .Single(item => item.Kind ==
                WorkflowArtifactKinds.NpcPackageManifest);
        Require(retainedIntake.Path == intakePath &&
                retainedIntake.Sha256 == HashFile(intakePath) &&
                retainedPackage.InputArtifactHashes.Contains(
                    retainedIntake.Sha256,
                    StringComparer.Ordinal) &&
                packageWorkflow.Bundle.Npc.Plugin == outputPlugin &&
                packageWorkflow.Bundle.Npc.LocalFormId == targetFormId,
            "The package workflow did not retain its exact intake/package/NPC lineage.");

        CopyRendererAuthorities(repositoryRoot, root);
        RemovePriorJournal(root, required: true);
        var previewRoot = Child(root, "npc-preview");
        var previewWorkflowPath = Child(root, "workflow", "preview.json");
        ProtocolInvocation preview = await RunCliAsync(
            root,
            PreviewChildDeadline,
            "preview", "npc", "--protocol", "2", "--json",
            "--intake", intakePath.Value,
            "--plugin", outputPlugin,
            "--form", targetFormId,
            "--package-manifest", manifestPath.Value,
            "--expected-package-sha256", manifestSha256,
            "--output-root", previewRoot.Value,
            "--workflow-bundle", packageWorkflowPath.Value,
            "--workflow-bundle-sha256", packageWorkflowArtifact.Sha256,
            "--workflow-output", previewWorkflowPath.Value);
        RequireSucceeded(preview, "preview npc");
        RequireEffects(preview);
        RequireEnvelopeSensitiveAuthorityRequired(preview);
        JsonElement previewResult = preview.Root.GetProperty("result");
        var previewBundlePath = new WorkspacePath(
            previewResult.GetProperty("bundlePath").GetString() ?? "");
        string previewBundleSha256 =
            previewResult.GetProperty("bundleSha256").GetString() ?? "";
        EnvelopeArtifact previewArtifact = RequireArtifact(
            preview, WorkflowArtifactKinds.NpcPreviewManifest);
        EnvelopeArtifact previewWorkflowArtifact = RequireArtifact(
            preview, "workflow-bundle");
        RequireBinding(
            previewArtifact,
            previewBundlePath,
            NpcVisualPreviewPersistenceContract.BundleSchema,
            "preview npc");
        Require(previewArtifact.Sha256 == previewBundleSha256 &&
                previewResult.GetProperty("composed").GetBoolean() &&
                !previewResult.GetProperty("runtimeAuthority").GetBoolean() &&
                previewResult.GetProperty("views").GetArrayLength() == 6,
            "The authentic preview result omitted its exact static evidence.");

        var previewReader = new NpcVisualPreviewArtifactReader(root);
        using NpcVisualPreviewArtifactDocument previewDocument =
            previewReader.Load(previewBundlePath, previewBundleSha256);
        previewDocument.Revalidate();
        Require(previewDocument.Value.Views.Length == 6 &&
                previewDocument.Value.Views.All(view =>
                    view.Width > 0 && view.Height > 0) &&
                previewDocument.Artifacts.All(item =>
                    item.Size > 0 && IsUpperSha256(item.Sha256)),
            "The authentic preview files did not survive independent reopen.");
        LoadWorkflow(
            root,
            previewWorkflowPath,
            previewWorkflowArtifact.Sha256,
            AgentWorkflowPhase.Review,
            [
                WorkflowArtifactKinds.NpcPackageManifest,
                WorkflowArtifactKinds.NpcPreviewManifest
            ],
            "gui");

        string evidenceDirectory = Path.Combine(
            repositoryRoot, "artifacts", "static-npc-build-preview");
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = new WorkspacePath(Path.Combine(
            evidenceDirectory,
            $"{Path.GetFileName(root.Value)}-contact-sheet.png"));
        Require(!File.Exists(evidence.Value),
            "The authentic contact-sheet evidence destination is occupied.");
        File.Copy(
            previewDocument.Value.ContactSheetPath.Value,
            evidence.Value,
            overwrite: false);
        Require(string.Equals(
                HashFile(evidence),
                previewDocument.Value.ContactSheetSha256.Value,
                StringComparison.OrdinalIgnoreCase),
            "The preserved contact sheet drifted after independent reopen.");
        return evidence;
    }

    private static void RequirePackageFile(
        PackageVerificationArtifact package,
        string kind,
        string relativePath)
    {
        PackageFileVerification[] matches = package.Files.Where(item =>
                string.Equals(item.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(
                    item.RelativePath.Value,
                    relativePath,
                    StringComparison.Ordinal))
            .ToArray();
        Require(matches is [{ Matches: true }],
            $"The verified package omitted exact {kind} artifact '{relativePath}'.");
    }

    private static async Task RunCriticalRefusalsAsync(
        string repositoryRoot,
        string fixtureCatalog,
        TranscriptA transcript,
        WorkspacePath copiedRoot)
    {
        RemovePriorJournal(transcript.Root, required: true);
        string staleHash = FlipNibble(transcript.WorkspaceWorkflowArtifact.Sha256);
        WorkspacePath staleInspection = Child(
            transcript.Root, "evidence", "stale-hash-inspection.json");
        WorkspacePath staleWorkflow = Child(
            transcript.Root, "workflow", "stale-hash.json");
        ProtocolInvocation stale = await RunPresetRefusalAsync(
            transcript.Root,
            transcript.PresetPath,
            transcript.PresetSha256,
            transcript.WorkspaceWorkflowPath,
            staleHash,
            staleInspection,
            staleWorkflow);
        RequireRefused(stale, staleInspection, staleWorkflow);

        byte[] intakeBytes = await File.ReadAllBytesAsync(
            transcript.IntakePath.Value);
        try
        {
            byte[] tampered = intakeBytes.ToArray();
            tampered[^1] ^= 0x01;
            await File.WriteAllBytesAsync(transcript.IntakePath.Value, tampered);
            WorkspacePath tamperInspection = Child(
                transcript.Root, "evidence", "tampered-inspection.json");
            WorkspacePath tamperWorkflow = Child(
                transcript.Root, "workflow", "tampered.json");
            ProtocolInvocation refused = await RunPresetRefusalAsync(
                transcript.Root,
                transcript.PresetPath,
                transcript.PresetSha256,
                transcript.WorkspaceWorkflowPath,
                transcript.WorkspaceWorkflowArtifact.Sha256,
                tamperInspection,
                tamperWorkflow);
            RequireRefused(refused, tamperInspection, tamperWorkflow);
        }
        finally
        {
            await File.WriteAllBytesAsync(transcript.IntakePath.Value, intakeBytes);
        }

        string quarantine = transcript.IntakePath.Value + ".missing";
        File.Move(transcript.IntakePath.Value, quarantine);
        try
        {
            WorkspacePath missingInspection = Child(
                transcript.Root, "evidence", "missing-inspection.json");
            WorkspacePath missingWorkflow = Child(
                transcript.Root, "workflow", "missing.json");
            ProtocolInvocation refused = await RunPresetRefusalAsync(
                transcript.Root,
                transcript.PresetPath,
                transcript.PresetSha256,
                transcript.WorkspaceWorkflowPath,
                transcript.WorkspaceWorkflowArtifact.Sha256,
                missingInspection,
                missingWorkflow);
            RequireRefused(refused, missingInspection, missingWorkflow);
        }
        finally
        {
            File.Move(quarantine, transcript.IntakePath.Value);
        }

        Directory.CreateDirectory(copiedRoot.Value);
        MaterializeProbeFixtures(
            repositoryRoot, fixtureCatalog, "workspace preflight", copiedRoot);
        foreach (string plugin in new[]
                 {
                     "Skyrim.esm",
                     "ActorwrightBlankNpcProvider.esp"
                 })
        {
            File.Copy(
                Child(transcript.Root, "Data", plugin).Value,
                Child(copiedRoot, "Data", plugin).Value,
                overwrite: false);
        }
        WorkspacePath copiedLoadOrder = WriteCanonicalLoadOrder(copiedRoot);
        MaterializeProbeFixtures(
            repositoryRoot, fixtureCatalog, "preset inspect", copiedRoot);
        Directory.CreateDirectory(Path.Combine(copiedRoot.Value, "evidence"));
        Directory.CreateDirectory(Path.Combine(copiedRoot.Value, "workflow"));
        var copiedIntake = Child(copiedRoot, "evidence", "reviewed-intake.json");
        var copiedBundle = Child(copiedRoot, "workflow", "workspace.json");
        File.Copy(transcript.IntakePath.Value, copiedIntake.Value);
        File.Copy(transcript.WorkspaceWorkflowPath.Value, copiedBundle.Value);
        WorkspacePath copiedPreset = Child(
            copiedRoot, "npc-preflight", "fixture.jslot");
        WorkspacePath copyInspection = Child(
            copiedRoot, "evidence", "copied-inspection.json");
        WorkspacePath copyWorkflow = Child(
            copiedRoot, "workflow", "copied-preset.json");
        ProtocolInvocation crossRoot = await RunPresetRefusalAsync(
            copiedRoot,
            copiedPreset,
            HashFile(copiedPreset),
            copiedBundle,
            HashFile(copiedBundle),
            copyInspection,
            copyWorkflow);
        RequireRefused(crossRoot, copyInspection, copyWorkflow);

        var rerunIntake = Child(copiedRoot, "evidence", "rerun-intake.json");
        var rerunWorkflow = Child(copiedRoot, "workflow", "rerun-workspace.json");
        RemovePriorJournal(copiedRoot);
        ProtocolInvocation rerun = await RunCliAsync(copiedRoot,
            "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse",
            "--workspace-root", copiedRoot.Value,
            "--data-root", Child(copiedRoot, "Data").Value,
            "--output-root", Child(copiedRoot, "reserved-output").Value,
            "--load-order", copiedLoadOrder.Value,
            "--intake-output", rerunIntake.Value,
            "--npc-editor-id", "ActorwrightResumptionNpc",
            "--workflow-output", rerunWorkflow.Value);
        RequireSucceeded(rerun, "workspace preflight");
        Require(HashFile(rerunWorkflow) != HashFile(copiedBundle),
            "Root-B rerun reused the byte-identical root-A workflow bundle.");

        AgentWorkflowBundleCodec codec = CreateWorkflowCodec(transcript.Root);
        AgentWorkflowBundle original = transcript.WorkspaceBundle.Bundle;
        AgentWorkflowBundle[] drifted =
        [
            original with { Phase = AgentWorkflowPhase.Apply },
            original with
            {
                Authority = original.Authority.Select(item =>
                        item.Kind == AgentAuthorityKind.PromotionApproval
                            ? item with
                            {
                                State = AgentAuthorityState.NotApplicable
                            }
                            : item)
                    .ToImmutableArray()
            },
            original with
            {
                NextActions = original.NextActions.SetItem(
                    0,
                    original.NextActions[0] with
                    {
                        Reason = original.NextActions[0].Reason + " drift"
                    })
            }
        ];
        string[] names = ["phase", "authority", "next-action"];
        for (var index = 0; index < drifted.Length; index++)
        {
            WorkspacePath driftBundle = Child(
                transcript.Root, "workflow", $"drift-{names[index]}.json");
            byte[] bytes = codec.ComputeCanonicalBytes(drifted[index]);
            await File.WriteAllBytesAsync(driftBundle.Value, bytes);
            RequireWorkflowFailure(
                () => new AgentWorkflowBundleTransitionService(codec)
                    .LoadForCommand(
                        driftBundle,
                        Hash(bytes),
                        "preset inspect"),
                "workflow-derived-state-mismatch",
                $"Hash-valid {names[index]} drift was not evaluator-refused.");
            WorkspacePath output = Child(
                transcript.Root, "evidence", $"drift-{names[index]}-inspection.json");
            WorkspacePath workflow = Child(
                transcript.Root, "workflow", $"drift-{names[index]}-output.json");
            ProtocolInvocation refused = await RunPresetRefusalAsync(
                transcript.Root,
                transcript.PresetPath,
                transcript.PresetSha256,
                driftBundle,
                Hash(bytes),
                output,
                workflow);
            RequireRefused(refused, output, workflow);
        }
    }

    private static async Task<ProtocolInvocation> RunPresetRefusalAsync(
        WorkspacePath root,
        WorkspacePath preset,
        string presetSha256,
        WorkspacePath workflowInput,
        string workflowSha256,
        WorkspacePath inspectionOutput,
        WorkspacePath workflowOutput)
    {
        RemovePriorJournal(root);
        return await RunCliAsync(root,
            "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse",
            "--input", preset.Value,
            "--input-sha256", presetSha256,
            "--inspection-output", inspectionOutput.Value,
            "--workflow-bundle", workflowInput.Value,
            "--workflow-bundle-sha256", workflowSha256,
            "--workflow-output", workflowOutput.Value);
    }

    private static async Task<ProtocolInvocation> RunCliAsync(
        WorkspacePath root,
        params string[] arguments) => await RunCliAsync(
            root, ChildDeadline, arguments);

    private static async Task<ProtocolInvocation> RunCliAsync(
        WorkspacePath root,
        TimeSpan deadline,
        params string[] arguments)
    {
        string dotnet = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The focused selector has no dotnet host.");
        Require(string.Equals(
                Path.GetFileNameWithoutExtension(dotnet),
                "dotnet",
                StringComparison.OrdinalIgnoreCase),
            "The focused selector is not hosted by the pinned dotnet runtime.");
        string defaultActorwright = typeof(CommandLine).Assembly.Location;
        (string actorwright, bool useDotNet) =
            CompatibilityJourneySupport.ResolveCli(defaultActorwright);
        Require(File.Exists(actorwright) &&
                (!string.Equals(actorwright, defaultActorwright,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(actorwright), "actorwright.dll",
                    StringComparison.OrdinalIgnoreCase)),
            "The product CLI assembly could not be resolved exactly.");
        var start = new ProcessStartInfo
        {
            FileName = useDotNet ? dotnet : actorwright,
            WorkingDirectory = root.Value,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (useDotNet)
            start.ArgumentList.Add(actorwright);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["ACTORWRIGHT_WORKSPACE_ROOT"] = root.Value;

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "The product CLI child process did not start.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(), stdoutTask, stderrTask)
                .WaitAsync(deadline);
        }
        catch (TimeoutException exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw new TimeoutException(
                $"The product CLI child exceeded {deadline.TotalSeconds:0} seconds: " +
                string.Join(' ', arguments),
                exception);
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        Require(stderr.Length <= 8192 &&
                !stderr.Contains("Unhandled exception", StringComparison.Ordinal) &&
                !stderr.Contains(" at NpcManager.", StringComparison.Ordinal),
            "The product CLI child emitted an unbounded or unsanitized stderr payload: " +
            stderr);
        bool protocolV2 = arguments.Contains("--protocol", StringComparer.Ordinal) &&
            arguments.Contains("2", StringComparer.Ordinal);
        string json = stdout;
        if (!protocolV2 && string.IsNullOrWhiteSpace(json) && stderr.TrimStart().StartsWith('{'))
            json = stderr;
        if (protocolV2)
        {
            string[] lines = stdout.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 1,
                "The product CLI child did not emit exactly one protocol envelope: " +
                stdout);
            json = lines[0];
        }
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement envelope = parsed.RootElement.Clone();
        Require(!protocolV2 ||
                (envelope.GetProperty("protocolVersion").GetString() == "2" &&
                 envelope.GetProperty("schemaVersion").GetString() == "1" &&
                 envelope.GetProperty("exitCode").GetInt32() == process.ExitCode),
            "The protocol envelope did not bind the child process exit.");
        return new ProtocolInvocation(process.ExitCode, envelope, stderr);
    }

    private static AgentWorkflowBundleDocument LoadWorkflow(
        WorkspacePath root,
        WorkspacePath path,
        string sha256,
        AgentWorkflowPhase phase,
        string[] artifactKinds,
        string? nextCommand)
    {
        AgentWorkflowBundleDocument document =
            CreateWorkflowCodec(root).Load(path, sha256);
        Require(document.Path == path && document.Size > 0 &&
                document.Sha256 == sha256 &&
                document.Bundle.Phase == phase &&
                document.Bundle.Artifacts.Select(item => item.Kind)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        artifactKinds.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal) &&
                (nextCommand is null
                    ? document.Bundle.NextActions.IsEmpty
                    : document.Bundle.NextActions is [var action] &&
                      action.Command == nextCommand),
            "The strict workflow bundle shape did not match its transcript step.");

        ImmutableDictionary<string, VerifiedWorkflowArtifact> verified =
            document.Bundle.Artifacts.ToImmutableDictionary(
                item => item.Kind,
                item => new VerifiedWorkflowArtifact(
                    item.Kind,
                    item.Path,
                    item.Size,
                    item.Sha256,
                    IndependentlyVerified: true),
                StringComparer.Ordinal);
        WorkflowEvaluation evaluation = AgentWorkflowService.Evaluate(
            document.Bundle, verified);
        Require(document.Bundle.Phase == evaluation.Phase &&
                AuthorityEquals(
                    document.Bundle.Authority, evaluation.Authority) &&
                NextActionsEqual(
                    document.Bundle.NextActions, evaluation.NextActions),
            "Persisted workflow phase, authority, or actions were not evaluator-derived.");
        foreach (AgentAuthorityKind kind in new[]
                 {
                     AgentAuthorityKind.HumanVisualAcceptance,
                     AgentAuthorityKind.GameRuntimeVerification,
                     AgentAuthorityKind.PromotionApproval
                 })
            Require(evaluation.Authority.Single(item => item.Kind == kind).State ==
                    AgentAuthorityState.Required,
                $"Workflow evaluation overclaimed {kind}.");
        return document;
    }

    private static AgentWorkflowBundleCodec CreateWorkflowCodec(
        WorkspacePath root) => new(
        new KOnlyWorkspacePolicy(
            root, new WorkspacePath(@"F:\ExampleGame")),
        root);

    private static bool AuthorityEquals(
        ImmutableArray<WorkflowAuthorityEvidence> left,
        ImmutableArray<WorkflowAuthorityEvidence> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind &&
            pair.First.State == pair.Second.State &&
            pair.First.Reason == pair.Second.Reason &&
            pair.First.ArtifactHashes.SequenceEqual(
                pair.Second.ArtifactHashes, StringComparer.Ordinal));

    private static bool NextActionsEqual(
        ImmutableArray<ProtocolNextAction> left,
        ImmutableArray<ProtocolNextAction> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Command == pair.Second.Command &&
            pair.First.Reason == pair.Second.Reason &&
            pair.First.RequiresHumanAction == pair.Second.RequiresHumanAction &&
            pair.First.MissingPrerequisites.SequenceEqual(
                pair.Second.MissingPrerequisites, StringComparer.Ordinal) &&
            pair.First.RequiredBindings.Length ==
                pair.Second.RequiredBindings.Length &&
            pair.First.RequiredBindings.Zip(pair.Second.RequiredBindings)
                .All(binding =>
                    binding.First.Option == binding.Second.Option &&
                    binding.First.Value == binding.Second.Value &&
                    binding.First.ArtifactSha256 ==
                        binding.Second.ArtifactSha256));

    private static void MaterializeProbeFixtures(
        string repositoryRoot,
        string catalogPath,
        string command,
        WorkspacePath destinationRoot)
    {
        using JsonDocument catalog = JsonDocument.Parse(
            File.ReadAllBytes(catalogPath));
        JsonElement probe = catalog.RootElement.GetProperty("probes")
            .EnumerateArray().Single(item =>
                item.GetProperty("command").GetString() == command);
        string sourceRoot = Path.Combine(
            repositoryRoot, "tools", "release",
            "protocol-v2-workflow-probes");
        foreach (JsonElement fixture in probe.GetProperty("fixtures")
                     .EnumerateArray())
        {
            string sourceRelative = RequireSafeRelative(
                fixture.GetProperty("source").GetString(), "source");
            string destinationRelative = RequireSafeRelative(
                fixture.GetProperty("destination").GetString(),
                "destination");
            string source = Path.GetFullPath(
                Path.Combine(sourceRoot, sourceRelative));
            string destination = Path.GetFullPath(
                Path.Combine(destinationRoot.Value, destinationRelative));
            Require(source.StartsWith(
                        sourceRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) &&
                    destination.StartsWith(
                        destinationRoot.Value + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase),
                "A protocol probe fixture escaped its owned root.");
            byte[] bytes = fixture.GetProperty("encoding").GetString() switch
            {
                "raw" => File.ReadAllBytes(source),
                "base64" => Convert.FromBase64String(
                    File.ReadAllText(source).Trim()),
                _ => throw new InvalidOperationException(
                    "The protocol probe fixture encoding is not closed.")
            };
            Require(bytes.LongLength == fixture.GetProperty("size").GetInt64() &&
                    Hash(bytes) == fixture.GetProperty("sha256").GetString(),
                $"The checked-in {command} fixture bytes drifted: {sourceRelative}");
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination) ??
                throw new InvalidOperationException(
                    "A fixture destination has no parent."));
            if (File.Exists(destination))
            {
                Require(File.ReadAllBytes(destination).AsSpan()
                        .SequenceEqual(bytes),
                    "Two protocol fixtures disagreed on shared bytes.");
            }
            else
            {
                File.WriteAllBytes(destination, bytes);
            }
        }
    }

    private static WorkspacePath WriteCanonicalLoadOrder(WorkspacePath root)
    {
        var path = Child(root, "load-order.json");
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Skyrim.esm\",\"order\":0,\"enabled\":true},{\"name\":\"ActorwrightBlankNpcProvider.esp\",\"order\":1,\"enabled\":true},{\"name\":\"Probe.esp\",\"order\":2,\"enabled\":true}]}");
        File.WriteAllBytes(path.Value, bytes);
        return path;
    }

    private static void CorrectNpcRequestFixture(
        string repositoryRoot,
        WorkspacePath root,
        WorkspacePath requestPath,
        string presetSha256,
        bool workspaceProvider = false)
    {
        const string packagedPresetSha256 =
            "D7B7274263FA120B1C92D896F4AB8ED2582F562A7C5B39224F86B3645AAAFE58";
        const string packagedRegistrySha256 =
            "D52E5BCC5070C8F41D814F0FBAAF843A1256B7B402C90F44319ECCFA43BC9FFB";
        string productBundleRoot = SyntheticProductProviderFixture.Ensure(
            AppContext.BaseDirectory);
        string? currentRegistrySha256 = null;
        if (!workspaceProvider)
        {
            var applicationResources = new ApplicationProviderResourceRegistry(
                new ApplicationResourcePath(AppContext.BaseDirectory));
            Require(applicationResources.TryGetDefaultBlankNpcFixture(
                        out ProductFixtureBundleReference? currentRegistry) &&
                    currentRegistry is not null,
                "The synthetic test provider bundle is unavailable.");
            currentRegistrySha256 =
                currentRegistry!.RegistryManifestSha256.Value;
        }
        const string packagedFaceGeomSha256 =
            "89478637C5F31E673CB955622DA9A30ECFDC39E90EB41B97017C7E9F1EFB3692";
        const string packagedFaceTintSha256 =
            "0C6865E50494702944D8B5F519D79912B04DCC623225E4893B5111BE810B16FE";
        const string packagedBundleSha256 =
            "E26540246B2F17D7BB44381E545682EB275C6E9E17AE01F7B1F8EC8C785F097E";
        const string packagedStandaloneSha256 =
            "9DF6E8D1F05149A3A1D8032FB860A4F8A0933DFDB0DA29FC6960CF970CE8B8FE";
        const string packagedRecordAuthoritySha256 =
            "7C5045B98395B633EF78E3ECF4AB6B109618429316DB1E9A5217D47621219862";
        const string packagedSkyrimSha256 =
            "D022A2171B5CBD984B90764D49DC98ECC26A31AF616CDC008AA7FE152E5B7CD0";
        const string admitted = "\"role\":\"static-validation\"";
        const string packagedIdentity = "\"editorId\":\"ActorwrightPackagedNpc\"";
        const string transcriptIdentity = "\"editorId\":\"ActorwrightResumptionNpc\"";

        WorkspacePath faceGeom = Child(root, "npc-preflight", "face.nif");
        WorkspacePath faceTint = Child(root, "npc-preflight", "face.dds");
        string productRoot = Path.Combine(productBundleRoot, "Data");
        string productFaceGeom = Path.Combine(
            productRoot, "meshes", "actors", "character", "FaceGenData",
            "FaceGeom", "ActorwrightBlankNpcProvider.esp", "00000800.nif");
        string productFaceTint = Path.Combine(
            productRoot, "textures", "actors", "character", "FaceGenData",
            "FaceTint", "ActorwrightBlankNpcProvider.esp", "00000800.dds");
        string productPlugin = Path.Combine(
            productRoot, "ActorwrightBlankNpcProvider.esp");
        Require(File.Exists(productFaceGeom) && File.Exists(productFaceTint) &&
                File.Exists(productPlugin) &&
                (File.GetAttributes(productFaceGeom) &
                    FileAttributes.ReparsePoint) == 0 &&
                (File.GetAttributes(productFaceTint) &
                    FileAttributes.ReparsePoint) == 0 &&
                (File.GetAttributes(productPlugin) &
                    FileAttributes.ReparsePoint) == 0 &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime",
                    "product-fixtures", "blank-npc-v1", "registry.json")),
            "The generated synthetic NPC/FaceGeom/FaceTint provider fixtures are unavailable.");
        File.Copy(productFaceGeom, faceGeom.Value, overwrite: true);
        File.Copy(productFaceTint, faceTint.Value, overwrite: true);
        WorkspacePath syntheticHeadpart = Child(
            root, "npc-preflight", "synthetic-headpart.nif");
        File.WriteAllBytes(syntheticHeadpart.Value,
            SyntheticProductProviderFixture.WriteHeadpartNif());
        PrepareGoldenHeadPartAssets(
            root, syntheticHeadpart, new WorkspacePath(productFaceTint));
        WorkspacePath admittedProductPlugin = Child(
            root, "Data", "ActorwrightBlankNpcProvider.esp");
        WorkspacePath providerSource = Child(
            root, "provider-source", "ActorwrightBlankNpcProvider.esp");
        Directory.CreateDirectory(
            Path.GetDirectoryName(providerSource.Value) ??
            throw new InvalidOperationException(
                "The golden provider source has no parent."));
        WorkspacePath goldenSkyrimSource = Child(
            root, "provider-source", "Skyrim.esm");
        SkyrimTestPluginFactory.CreateGoldenSkyrimMaster(
            goldenSkyrimSource);
        WorkspacePath admittedSkyrim = Child(root, "Data", "Skyrim.esm");
        File.Copy(
            goldenSkyrimSource.Value,
            admittedSkyrim.Value,
            overwrite: true);
        string admittedSkyrimSha256 = HashFile(admittedSkyrim);
        var nativeMorphs = new SkyrimFaceMorphPatch(
            Enumerable.Repeat(0F, 18).ToImmutableArray(),
            0F,
            Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray());
        BethesdaSkyrimFaceMorphAdapter.Write(
            new SkyrimFaceMorphPatchRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(productPlugin),
                providerSource,
                new FormId(0x00000800),
                nativeMorphs,
                new Sha256Hash(HashFile(new WorkspacePath(productPlugin))),
                DryRun: false,
                ProposalPath: null),
            providerSource);
        SkyrimTestPluginFactory.CreateGoldenNpcProviderWithHeadTexture(
            providerSource,
            admittedProductPlugin);
        string productPluginSha256 = HashFile(admittedProductPlugin);
        string faceGeomSha256 = HashFile(faceGeom);
        string faceTintSha256 = HashFile(faceTint);
        WorkspaceProviderRequestContexts? workspaceProviderContexts =
            workspaceProvider
                ? CreateWorkspaceProviderFixture(
                    root, productBundleRoot, admittedProductPlugin)
                : null;

        WorkspacePath recordAuthority = Child(
            root, "npc-preflight", "record-authority.json");
        string recordSource = File.ReadAllText(recordAuthority.Value);
        recordSource = ReplaceExactly(
            recordSource,
            "\"formBindings\":[]",
            "\"formBindings\":[{\"signature\":\"HDPT\",\"sourceFormKey\":\"ActorwrightBlankNpcProvider.esp|0x00000802\",\"providerFormKey\":\"ActorwrightBlankNpcProvider.esp|0x00000802\",\"providerPluginName\":\"ActorwrightBlankNpcProvider.esp\",\"providerPluginPath\":\"Data/ActorwrightBlankNpcProvider.esp\",\"providerPluginSha256\":\"" +
            productPluginSha256 + "\",\"headPartType\":\"face\"},{\"signature\":\"TXST\",\"sourceFormKey\":\"ActorwrightBlankNpcProvider.esp|0x00000801\",\"providerFormKey\":\"ActorwrightBlankNpcProvider.esp|0x00000801\",\"providerPluginName\":\"ActorwrightBlankNpcProvider.esp\",\"providerPluginPath\":\"Data/ActorwrightBlankNpcProvider.esp\",\"providerPluginSha256\":\"" +
            productPluginSha256 + "\"}]");
        recordSource = ReplaceExactly(
            recordSource,
            "\"headPartDispositions\":[]",
            "\"headPartDispositions\":[{\"sourceFormKey\":\"ActorwrightBlankNpcProvider.esp|0x00000802\",\"disposition\":\"mapped-record\"}]");
        recordSource = ReplaceExactly(
            recordSource,
            "\"hairColorAuthority\":null",
            "\"hairColorAuthority\":{\"kind\":\"output-owned-clfm\",\"packedRgb\":16777215,\"allocatedLocalFormId\":\"0x00000801\"}");
        recordSource = ReplaceExactly(
            recordSource,
            packagedSkyrimSha256,
            admittedSkyrimSha256);
        recordSource = ReplaceExactly(
            recordSource,
            "\"tintMappings\":[]",
            "\"tintMappings\":[{\"jslotIndex\":0,\"disposition\":\"mapped-record\",\"tiniIndex\":0,\"tiasPresetIndex\":-1,\"skinTint\":true}]");
        recordSource = ReplaceExactly(
            recordSource,
            "\"qnam\":null",
            "\"qnam\":{\"source\":\"mapped-skin-tint\",\"jslotIndex\":0,\"red\":1,\"green\":1,\"blue\":1}");
        File.WriteAllText(
            recordAuthority.Value,
            recordSource,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string recordAuthoritySha256 = HashFile(recordAuthority);

        WorkspacePath presetBundle = Child(
            root, "npc-preflight", "preset-bundle.json");
        string presetSource = File.ReadAllText(presetBundle.Value);
        if (!workspaceProvider)
            presetSource = ReplaceExactly(
                presetSource, packagedRegistrySha256,
                currentRegistrySha256!);
        presetSource = ReplaceExactly(
            presetSource, packagedPresetSha256, presetSha256);
        presetSource = ReplaceExactly(
            presetSource, packagedFaceGeomSha256, faceGeomSha256);
        presetSource = ReplaceExactly(
            presetSource, packagedFaceTintSha256, faceTintSha256);
        presetSource = ReplaceExactly(
            presetSource,
            packagedRecordAuthoritySha256,
            recordAuthoritySha256);
        if (workspaceProviderContexts is not null)
        {
            JsonObject bundle = JsonNode.Parse(presetSource)!.AsObject();
            bundle["schemaVersion"] = 1;
            bundle.Remove("providerAuthority");
            bundle["providerContext"] =
                workspaceProviderContexts.BundleContext;
            presetSource = bundle.ToJsonString();
        }
        File.WriteAllText(
            presetBundle.Value,
            presetSource,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string presetBundleSha256 = HashFile(presetBundle);

        WorkspacePath standaloneAssets = Child(
            root, "npc-preflight", "standalone-assets.json");
        string standaloneSource = File.ReadAllText(standaloneAssets.Value);
        standaloneSource = ReplaceExactly(
            standaloneSource,
            "\"pluginPath\":\"Data/Skyrim.esm\"",
            "\"pluginPath\":\"Data/ActorwrightBlankNpcProvider.esp\"");
        standaloneSource = ReplaceExactly(
            standaloneSource,
            packagedSkyrimSha256,
            productPluginSha256);
        standaloneSource = ReplaceExactly(
            standaloneSource,
            "\"npcFormId\":\"0x00000800\"",
            "\"npcFormId\":\"0x00000800\"");
        File.WriteAllText(
            standaloneAssets.Value,
            standaloneSource,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string standaloneSha256 = HashFile(standaloneAssets);

        string source = File.ReadAllText(requestPath.Value);
        if (!workspaceProvider)
            source = ReplaceExactly(
                source, packagedRegistrySha256,
                currentRegistrySha256!);
        const string raceReference = "\"race\":\"Skyrim.esm|0x00013746\"";
        Require(source.Contains(raceReference, StringComparison.Ordinal) &&
                source.IndexOf(raceReference, StringComparison.Ordinal) ==
                    source.LastIndexOf(raceReference, StringComparison.Ordinal),
            "The owned workflow fixture did not retain exactly one Skyrim race reference.");
        Require(source.Contains(admitted, StringComparison.Ordinal) &&
                source.IndexOf(admitted, StringComparison.Ordinal) ==
                    source.LastIndexOf(admitted, StringComparison.Ordinal) &&
                source.Contains(packagedIdentity, StringComparison.Ordinal) &&
                source.IndexOf(packagedIdentity, StringComparison.Ordinal) ==
                    source.LastIndexOf(packagedIdentity, StringComparison.Ordinal),
            "The Task 8 NPC request fixture no longer has its canonical role and known transcript identity.");
        source = ReplaceExactly(source, packagedIdentity, transcriptIdentity);
        source = ReplaceExactly(
            source, packagedFaceGeomSha256, faceGeomSha256);
        source = ReplaceExactly(
            source, packagedFaceTintSha256, faceTintSha256);
        source = ReplaceExactly(
            source, packagedPresetSha256, presetSha256);
        source = ReplaceExactly(
            source,
            packagedRecordAuthoritySha256,
            recordAuthoritySha256);
        source = ReplaceExactly(
            source, packagedBundleSha256, presetBundleSha256);
        source = ReplaceExactly(
            source, packagedStandaloneSha256, standaloneSha256);
        if (workspaceProviderContexts is not null)
        {
            JsonObject request = JsonNode.Parse(source)!.AsObject();
            request["providerContext"] =
                workspaceProviderContexts.RequestContext;
            source = request.ToJsonString();
        }
        File.WriteAllText(
            requestPath.Value,
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static WorkspaceProviderRequestContexts
        CreateWorkspaceProviderFixture(
            WorkspacePath journeyRoot,
            string productBundleRoot,
            WorkspacePath admittedTemplatePlugin)
    {
        const string pluginName = "ActorwrightBlankNpcProvider.esp";
        string sourceDataRoot = Path.Combine(productBundleRoot, "Data");
        string sourceFaceGeom = Path.Combine(
            sourceDataRoot, "meshes", "actors", "character",
            "FaceGenData", "FaceGeom", pluginName, "00000800.nif");
        string sourceFaceTint = Path.Combine(
            sourceDataRoot, "textures", "actors", "character",
            "FaceGenData", "FaceTint", pluginName, "00000800.dds");
        string sourceTintManifest = Path.Combine(
            productBundleRoot, "facetint-manifest.json");
        string sourceDependencyManifest = Path.Combine(
            productBundleRoot, "dependency-manifest.json");
        string sourceProviderManifest = Path.Combine(
            productBundleRoot, "provider-manifest.json");

        WorkspacePath workspaceBundle = Child(
            journeyRoot, "npc-preflight", "workspace-provider");
        Require(!Directory.Exists(workspaceBundle.Value) &&
                !File.Exists(workspaceBundle.Value),
            "The owned workspace-provider fixture destination is occupied.");
        WorkspacePath dataRoot = Child(
            workspaceBundle, "Data");
        WorkspacePath templatePlugin = Child(dataRoot, pluginName);
        WorkspacePath faceGeom = Child(
            dataRoot, "meshes", "actors", "character", "FaceGenData",
            "FaceGeom", pluginName, "00000800.nif");
        WorkspacePath faceTint = Child(
            dataRoot, "textures", "actors", "character", "FaceGenData",
            "FaceTint", pluginName, "00000800.dds");
        WorkspacePath tintManifest = Child(
            workspaceBundle, "facetint-manifest.json");
        WorkspacePath dependencyManifest = Child(
            workspaceBundle, "dependency-manifest.json");
        WorkspacePath providerManifest = Child(
            workspaceBundle, "provider-manifest.json");

        Directory.CreateDirectory(dataRoot.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeom.Value)!);
        Directory.CreateDirectory(Path.GetDirectoryName(faceTint.Value)!);
        File.Copy(admittedTemplatePlugin.Value, templatePlugin.Value);
        File.Copy(sourceFaceGeom, faceGeom.Value);
        File.Copy(sourceFaceTint, faceTint.Value);
        File.Copy(sourceTintManifest, tintManifest.Value);
        File.Copy(sourceDependencyManifest, dependencyManifest.Value);

        JsonObject manifest = JsonNode.Parse(
            File.ReadAllBytes(sourceProviderManifest))!.AsObject();
        JsonObject template = manifest["template"]!.AsObject();
        template["path"] = RelativeWorkspacePath(
            journeyRoot, templatePlugin);
        template["sha256"] = HashFile(templatePlugin);
        JsonObject faceGeomManifest = manifest["faceGeom"]!.AsObject();
        faceGeomManifest["path"] = RelativeWorkspacePath(
            journeyRoot, faceGeom);
        faceGeomManifest["sha256"] = HashFile(faceGeom);
        JsonObject faceTintAuthority = manifest["faceTint"]!.AsObject();
        faceTintAuthority["manifestPath"] = RelativeWorkspacePath(
            journeyRoot, tintManifest);
        faceTintAuthority["providerRoot"] = RelativeWorkspacePath(
            journeyRoot, dataRoot);
        JsonObject dependencyAuthority = manifest["dependencies"]!.AsObject();
        dependencyAuthority["manifestPath"] = RelativeWorkspacePath(
            journeyRoot, dependencyManifest);
        File.WriteAllText(
            providerManifest.Value,
            manifest.ToJsonString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string manifestPath = RelativeWorkspacePath(
            journeyRoot, providerManifest);
        string manifestSha256 = HashFile(providerManifest);
        string dependencyManifestPath = RelativeWorkspacePath(
            journeyRoot, dependencyManifest);
        string dependencyManifestSha256 = HashFile(dependencyManifest);
        string templatePath = RelativeWorkspacePath(journeyRoot, templatePlugin);
        string faceGeomPath = RelativeWorkspacePath(journeyRoot, faceGeom);
        string tintManifestPath = RelativeWorkspacePath(
            journeyRoot, tintManifest);
        string providerRootPath = RelativeWorkspacePath(
            journeyRoot, dataRoot);

        return new WorkspaceProviderRequestContexts(
            new JsonObject
            {
                ["manifestPath"] = manifestPath,
                ["manifestSha256"] = manifestSha256,
                ["templatePlugin"] = templatePath,
                ["templateSha256"] = HashFile(templatePlugin),
                ["templateNpcFormId"] = "0x00000800",
                ["faceGeomCarrier"] = faceGeomPath,
                ["faceGeomSha256"] = HashFile(faceGeom),
                ["faceTintManifest"] = tintManifestPath,
                ["faceTintProviderRoot"] = providerRootPath,
                ["dependencyManifest"] = dependencyManifestPath
            },
            new JsonObject
            {
                ["manifestPath"] = manifestPath,
                ["manifestSha256"] = manifestSha256,
                ["dependencyManifestPath"] = dependencyManifestPath,
                ["dependencyManifestSha256"] = dependencyManifestSha256
            });
    }

    private static string RelativeWorkspacePath(
        WorkspacePath root,
        WorkspacePath path)
    {
        string relative = Path.GetRelativePath(root.Value, path.Value);
        Require(!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal),
            "A synthetic workspace-provider path escaped its journey root.");
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private sealed record WorkspaceProviderRequestContexts(
        JsonObject RequestContext,
        JsonObject BundleContext);

    private static void PrepareGoldenPresetFixture(WorkspacePath presetPath)
    {
        string source = File.ReadAllText(presetPath.Value);
        source = ReplaceExactly(
            source,
            "{\"version\":",
            "{\"headParts\":[{\"formIdentifier\":\"ActorwrightBlankNpcProvider.esp|0x00000802\",\"type\":1}],\"version\":");
        source = ReplaceExactly(
            source,
            "\"actor\":{\"weight\":50}",
            "\"actor\":{\"hairColor\":16777215,\"headTexture\":\"ActorwrightBlankNpcProvider.esp|0x00000801\",\"weight\":50},\"morphs\":{\"default\":{\"morphs\":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],\"presets\":[0,0,0,0]}},\"tintInfo\":[{\"index\":0,\"color\":4294967295,\"texture\":\"textures/actors/character/character assets/tintmasks/femalehead.dds\"}]");
        File.WriteAllText(
            presetPath.Value,
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void PrepareGoldenHeadPartAssets(
        WorkspacePath root,
        WorkspacePath sourceNif,
        WorkspacePath sourceDds)
    {
        WorkspacePath model = Child(
            root, "Data", "meshes", "Actors", "Character", "Actorwright",
            "Head.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(model.Value)!);
        File.Copy(sourceNif.Value, model.Value, overwrite: true);
        byte[] modelBytes = File.ReadAllBytes(model.Value);
        var modelPath = new AssetPath(
            "meshes/Actors/Character/Actorwright/Head.nif");
        var modelHash = new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(modelBytes)));
        SseSelectedHeadpartNifGeometryReadResult geometry =
            new SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    modelPath,
                    modelHash,
                    modelBytes.ToImmutableArray()));
        Require(geometry.Accepted && geometry.Document is not null &&
                geometry.Document.Shapes.Length == 1,
            "The reviewed blank-NPC FaceGeom must expose exactly one admitted headpart shape.");
        WorkspacePath tri = Child(
            root, "Data", "meshes", "Actors", "Character", "Actorwright",
            "Head.tri");
        File.WriteAllBytes(
            tri.Value,
            WriteDenseTri(geometry.Document!.Shapes[0].VertexCount));

        string textureRoot = Path.Combine(
            root.Value, "Data", "textures", "Actors", "Character");
        foreach (string relative in new[]
                 {
                     Path.Combine("Actorwright", "Head.dds"),
                     Path.Combine("Actorwright", "Head_msn.dds"),
                     Path.Combine("Actorwright", "Head_sk.dds"),
                     Path.Combine("Male", "BlankDetailmap.dds"),
                     Path.Combine("Actorwright", "Head_s.dds"),
                     Path.Combine(
                         "character assets", "tintmasks", "femalehead.dds")
                 })
        {
            string destination = Path.Combine(textureRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceDds.Value, destination, overwrite: true);
        }
        foreach (string stem in new[] { "body", "hands", "feet" })
        {
            foreach (string suffix in new[] { "_d", "_n", "_sk", "_s" })
            {
                string destination = Path.Combine(
                    root.Value,
                    "Data",
                    "textures",
                    "Actorwright",
                    "Skin",
                    stem + suffix + ".dds");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceDds.Value, destination, overwrite: true);
            }
        }
    }

    private static byte[] WriteDenseTri(int vertexCount)
    {
        Require(vertexCount > 0, "The golden headpart has no vertices.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        writer.Write((uint)vertexCount);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        for (int index = 0; index < vertexCount; index++)
        {
            writer.Write(0F);
            writer.Write(0F);
            writer.Write(0F);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static string ReplaceExactly(
        string source,
        string expected,
        string replacement)
    {
        Require(source.Contains(expected, StringComparison.Ordinal) &&
                source.IndexOf(expected, StringComparison.Ordinal) ==
                    source.LastIndexOf(expected, StringComparison.Ordinal),
            $"The owned workflow fixture did not contain exactly one '{expected}'.");
        return source.Replace(
            expected, replacement, StringComparison.Ordinal);
    }

    private static void CopyRendererAuthorities(
        string repositoryRoot,
        WorkspacePath root)
    {
        const string authorityRootVariable =
            "ACTORWRIGHT_TEST_RENDERER_AUTHORITY_ROOT";
        string? configuredAuthorityRoot =
            Environment.GetEnvironmentVariable(authorityRootVariable);
        string sourceExternal = string.IsNullOrWhiteSpace(
                configuredAuthorityRoot)
            ? Path.Combine(repositoryRoot, "tools", "external")
            : Path.GetFullPath(configuredAuthorityRoot);
        Require(string.IsNullOrWhiteSpace(configuredAuthorityRoot) ||
                Path.IsPathFullyQualified(configuredAuthorityRoot),
            $"{authorityRootVariable} must be one absolute directory.");
        string destinationExternal = Path.Combine(
            root.Value, "tools", "external");
        Require(!Directory.Exists(destinationExternal) &&
                !File.Exists(destinationExternal),
            "The owned renderer-authority destination is occupied.");
        Directory.CreateDirectory(destinationExternal);
        foreach (string name in RendererAuthorityNames)
        {
            CopyAuthorityDirectory(
                Path.Combine(sourceExternal, name),
                Path.Combine(destinationExternal, name));
        }
    }

    private static void CopyAuthorityDirectory(
        string source,
        string destination)
    {
        const int maximumFiles = 20_000;
        const long maximumBytes = 4L * 1024 * 1024 * 1024;
        Require(Directory.Exists(source) &&
                (File.GetAttributes(source) &
                    FileAttributes.ReparsePoint) == 0,
            $"Renderer authority is absent or reparsed: {source}");
        Require(!Directory.Exists(destination) && !File.Exists(destination),
            $"Renderer authority destination is occupied: {destination}");
        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        var count = 0;
        long bytes = 0;
        while (pending.Count > 0)
        {
            (string currentSource, string currentDestination) = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         currentSource, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0,
                    $"Renderer authority contains a reparse point: {entry}");
                string target = Path.Combine(
                    currentDestination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }
                var info = new FileInfo(entry);
                count = checked(count + 1);
                bytes = checked(bytes + info.Length);
                Require(count <= maximumFiles && bytes <= maximumBytes,
                    "Renderer authority copy exceeded its bounded inventory.");
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static string RequireSafeRelative(string? value, string role)
    {
        Require(!string.IsNullOrWhiteSpace(value) &&
                !Path.IsPathFullyQualified(value) &&
                !value.Contains(':') &&
                value.Split(['/', '\\']).All(part =>
                    part is not "" and not "." and not ".."),
            $"A protocol probe {role} path is unsafe.");
        return value!;
    }

    private static EnvelopeArtifact RequireArtifact(
        ProtocolInvocation invocation,
        string kind)
    {
        JsonElement element = invocation.Root.GetProperty("artifacts")
            .EnumerateArray().Single(item =>
                item.GetProperty("kind").GetString() == kind);
        return new EnvelopeArtifact(
            kind,
            element.GetProperty("schemaOrMediaType").GetString() ?? "",
            new WorkspacePath(element.GetProperty("path").GetString() ?? ""),
            element.GetProperty("size").GetInt64(),
            element.GetProperty("sha256").GetString() ?? "",
            element.GetProperty("producerCommand").GetString() ?? "",
            element.GetProperty("state").GetString() ?? "");
    }

    private static void RequireBinding(
        EnvelopeArtifact artifact,
        WorkspacePath path,
        string schema,
        string producer)
    {
        Require(artifact.Path == path && artifact.Size > 0 &&
                artifact.Schema == schema && artifact.Producer == producer &&
                artifact.State == "independentlyVerified" &&
                IsUpperSha256(artifact.Sha256) &&
                HashFile(path) == artifact.Sha256,
            $"The {artifact.Kind} envelope binding is not exact.");
    }

    private static void RequireSucceeded(
        ProtocolInvocation invocation,
        string command)
    {
        Require(invocation.ExitCode == 0 &&
                invocation.Root.GetProperty("outcome").GetString() ==
                    "succeeded" &&
                invocation.Root.GetProperty("command").GetString() == command &&
                invocation.Root.GetProperty("diagnostics").EnumerateArray()
                    .All(item => item.GetProperty("severity").GetString() != "error"),
            $"{command} did not produce one successful protocol envelope: " +
            invocation.Root.GetRawText() + " stderr=" + invocation.StdErr);
    }

    private static void RequireEffects(
        ProtocolInvocation invocation,
        int expectedCompletedWrites = 1)
    {
        string[] effects = invocation.Root.GetProperty("effects")
            .EnumerateArray().Select(item => string.Join('|',
                item.GetProperty("kind").GetString(),
                item.GetProperty("status").GetString(),
                item.GetProperty("scope").GetString())).ToArray();
        string[] expected =
        [
            "readWorkspace|completed|workspace",
            .. Enumerable.Repeat(
                "writeNewArtifact|completed|k-local-output",
                expectedCompletedWrites),
            "appendLocalOperationJournal|attempted|workspace-local-journal",
            "appendLocalOperationJournal|completed|workspace-local-journal"
        ];
        Require(expectedCompletedWrites > 0 &&
                effects.SequenceEqual(expected, StringComparer.Ordinal),
            "A resumed command changed its exact effect transcript.");
    }

    private static void RequireEnvelopeSensitiveAuthorityRequired(
        ProtocolInvocation invocation)
    {
        Dictionary<string, string> authority = invocation.Root
            .GetProperty("authority").EnumerateArray().ToDictionary(
                item => item.GetProperty("kind").GetString() ?? "",
                item => item.GetProperty("state").GetString() ?? "",
                StringComparer.Ordinal);
        foreach (string kind in SensitiveAuthorityKinds)
            Require(authority.GetValueOrDefault(kind) == "required",
                $"Finish Verify envelope overclaimed {kind}.");
    }

    private static void RequireRefused(
        ProtocolInvocation invocation,
        params WorkspacePath[] outputs)
    {
        Require(invocation.ExitCode != 0 &&
                invocation.Root.GetProperty("outcome").GetString() !=
                    "succeeded" &&
                invocation.Root.GetProperty("diagnostics").GetArrayLength() > 0 &&
                invocation.Root.GetProperty("artifacts").GetArrayLength() == 0 &&
                outputs.All(path => !File.Exists(path.Value) &&
                    !Directory.Exists(path.Value)),
            "A stale, tampered, missing, cross-root, or drifted input was not refused before output.");
    }

    private static void RequireWorkflowFailure(
        Action action,
        string code,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentWorkflowCodecException exception) when (
            exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void RemovePriorJournal(
        WorkspacePath root,
        bool required = false)
    {
        string operations = Path.Combine(
            root.Value, ".actorwright", "operations");
        if (!Directory.Exists(operations))
        {
            Require(!required,
                "The successful command did not create its fixture-local journal.");
            return;
        }
        RequireOrdinaryTree(operations);
        Directory.Delete(operations, recursive: true);
        Require(!Directory.Exists(operations),
            "The fixture-local prior journal remained available to the next process.");
    }

    private static void DeleteOwnedRoot(string root, string ownedParent)
    {
        if (!Directory.Exists(root))
            return;
        string canonical = Path.GetFullPath(root);
        string parent = Path.GetFullPath(ownedParent) +
            Path.DirectorySeparatorChar;
        Require(canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(canonical).StartsWith(
                    "task-7-resumption-", StringComparison.Ordinal),
            "Task 7 cleanup refused a non-owned root.");
        RequireOrdinaryTree(canonical);
        Directory.Delete(canonical, recursive: true);
    }

    private sealed class OwnedScratchDirectory : IDisposable
    {
        private readonly string _parent;
        private bool _disposed;

        internal OwnedScratchDirectory(string parent, string prefix)
        {
            _parent = Path.GetFullPath(parent);
            Require(!string.IsNullOrWhiteSpace(prefix) &&
                    !Path.IsPathRooted(prefix) &&
                    prefix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0,
                "Owned scratch cleanup requires a simple directory prefix.");
            string repository = Path.GetFullPath(Directory.GetCurrentDirectory());
            string relativeParent = Path.GetRelativePath(repository, _parent);
            Require(!Path.IsPathRooted(relativeParent) &&
                    relativeParent != ".." &&
                    !relativeParent.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !relativeParent.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal),
                "Owned scratch cleanup requires a repository-local parent.");
            RequireOrdinaryDirectoryPath(_parent, createMissing: true);
            Root = Path.Combine(
                _parent, $"{prefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!Directory.Exists(Root))
                return;
            RequireOrdinaryDirectoryPath(_parent, createMissing: false);
            string canonical = Path.GetFullPath(Root);
            Require(string.Equals(
                    Directory.GetParent(canonical)?.FullName,
                    _parent,
                    StringComparison.OrdinalIgnoreCase),
                "Owned scratch cleanup refused a non-child directory.");
            RequireOrdinaryTree(canonical);
            Directory.Delete(canonical, recursive: true);
        }

        private static void RequireOrdinaryDirectoryPath(
            string directory,
            bool createMissing)
        {
            var ancestors = new Stack<string>();
            for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
                ancestors.Push(current.FullName);

            while (ancestors.Count > 0)
            {
                string ancestor = ancestors.Pop();
                if (!Directory.Exists(ancestor))
                {
                    Require(createMissing,
                        "Owned scratch cleanup refused a missing parent path.");
                    Directory.CreateDirectory(ancestor);
                }
                Require(Directory.Exists(ancestor) &&
                        (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0,
                    "Owned scratch cleanup refused a non-ordinary parent path.");
            }
        }
    }

    private static void RequireOrdinaryTree(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0,
            "Owned cleanup refused a reparse root.");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0,
                    "Owned cleanup refused a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static WorkspacePath Child(
        WorkspacePath root,
        params string[] components) => new(
        components.Aggregate(
            root.Value,
            (current, component) => Path.Combine(current, component)));

    private static string HashFile(WorkspacePath path) =>
        Hash(File.ReadAllBytes(path.Value));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string FlipNibble(string hash) =>
        (hash[0] == '0' ? '1' : '0') + hash[1..];

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record ProtocolInvocation(
        int ExitCode,
        JsonElement Root,
        string StdErr);

    private sealed record EnvelopeArtifact(
        string Kind,
        string Schema,
        WorkspacePath Path,
        long Size,
        string Sha256,
        string Producer,
        string State);

    private sealed record TranscriptA(
        WorkspacePath Root,
        WorkspacePath IntakePath,
        EnvelopeArtifact IntakeArtifact,
        WorkspacePath WorkspaceWorkflowPath,
        EnvelopeArtifact WorkspaceWorkflowArtifact,
        AgentWorkflowBundleDocument WorkspaceBundle,
        WorkspacePath PresetPath,
        string PresetSha256,
        WorkspacePath InspectionPath,
        WorkspacePath PresetWorkflowPath,
        EnvelopeArtifact PresetWorkflowArtifact,
        AgentWorkflowBundleDocument PresetBundle,
        AgentWorkflowBundleDocument PreflightBundle,
        WorkspacePath ContactSheetEvidence);
}
