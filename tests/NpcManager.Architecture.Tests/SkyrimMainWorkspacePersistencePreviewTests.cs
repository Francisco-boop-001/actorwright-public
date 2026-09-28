using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task
        TestSkyrimMainWorkspacePersistenceAndPreview()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-002-persistence-preview-tests",
            Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        try
        {
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var projectRoot = new WorkspacePath(project);
            var settingsService =
                new SkyrimMainWorkspaceSettingsService(
                    policy, labRoot, projectRoot);
            var settings = new SkyrimMainWorkspaceSettings(
                "1",
                SkyrimMainWorkspaceFilter.Default with
                {
                    Search = "female",
                    ShowLeveledNpcs = false,
                    Categories =
                        ImmutableHashSet.Create(NpcCategory.Unique),
                    Gender = SkyrimMainWorkspaceGender.Female,
                    ChangedOnly = true,
                    IncludeDeleted = false
                },
                SkyrimMainWorkspacePreviewOptions.Default with
                {
                    Mode = SkyrimMainWorkspacePreviewMode.FaceOnly,
                    RenderArmor = false,
                    RenderHeadwear = false,
                    ApplySculpt = false
                });
            SkyrimMainWorkspaceSettingsSaveResult savedSettings =
                await settingsService.SaveAsync(
                    settings, CancellationToken.None);
            SkyrimMainWorkspaceSettingsLoadResult loadedSettings =
                await settingsService.LoadAsync(CancellationToken.None);
            string settingsPath = Path.Combine(
                project,
                "03-builds",
                "work",
                "npc-studio-settings",
                "main-workspace.json");
            Assert(
                savedSettings.Saved &&
                loadedSettings.LoadedFromDisk &&
                savedSettings.Sha256 == loadedSettings.Sha256 &&
                SettingsEqual(settings, loadedSettings.Settings) &&
                loadedSettings.Sha256 == HashWorkspaceFile(settingsPath),
                "Main-workspace settings did not reopen exactly.");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(
                    () => settingsService.SaveAsync(
                        settings, cancelled.Token).AsTask());
            }
            Assert(
                !Directory.EnumerateFiles(
                        Path.GetDirectoryName(settingsPath)!,
                        "main-workspace.json.tmp-*")
                    .Any(),
                "Cancelled settings save left a temporary sibling.");
            SkyrimMainWorkspaceSettingsSaveResult existingSettings =
                await settingsService.SaveAsync(
                    settings, CancellationToken.None);
            Assert(
                !existingSettings.Saved &&
                existingSettings.Diagnostics.Any(item =>
                    item.Code ==
                    "main-workspace-settings-exists"),
                "Settings overwrote an existing destination.");

            string unsupportedProject =
                Path.Combine(root, "unsupported-project");
            string unsupportedSettingsPath = Path.Combine(
                unsupportedProject,
                "03-builds",
                "work",
                "npc-studio-settings",
                "main-workspace.json");
            Directory.CreateDirectory(
                Path.GetDirectoryName(unsupportedSettingsPath)!);
            await File.WriteAllTextAsync(
                unsupportedSettingsPath,
                JsonSerializer.Serialize(
                    settings with { SchemaVersion = "2" }));
            SkyrimMainWorkspaceSettingsLoadResult unsupportedSettings =
                await new SkyrimMainWorkspaceSettingsService(
                        policy,
                        labRoot,
                        new WorkspacePath(unsupportedProject))
                    .LoadAsync(CancellationToken.None);
            Assert(
                !unsupportedSettings.LoadedFromDisk &&
                unsupportedSettings.Diagnostics.Any(item =>
                    item.Code ==
                    "main-workspace-settings-schema"),
                "Settings accepted an unknown schema version.");

            SkyrimMainWorkspaceIdentity first = new(
                new PluginName("Actors.esp"),
                new PluginName("ActorsPatch.esp"),
                new FormId(0x800),
                "NPC_");
            SkyrimMainWorkspaceIdentity second = new(
                new PluginName("Actors.esp"),
                new PluginName("Actors.esp"),
                new FormId(0x801),
                "NPC_");
            string childPath =
                Path.Combine(project, "verified-child.json");
            await File.WriteAllTextAsync(
                childPath,
                "{\"artifactKind\":\"verified-child\"}");
            Sha256Hash childHash = HashWorkspaceFile(childPath);
            var handoff = new SkyrimMainWorkspaceArtifactHandoff(
                first,
                "verified-child",
                new WorkspacePath(childPath),
                childHash,
                null,
                null,
                false);
            var session = new SkyrimMainWorkspaceSession(
                "1",
                new Sha256Hash(new string('8', 64)),
                [first, second],
                [
                    new SkyrimMainWorkspaceDraft(first, true, false),
                    new SkyrimMainWorkspaceDraft(second, false, true)
                ],
                [handoff],
                false);
            var sessionService =
                new SkyrimMainWorkspaceSessionService(
                    policy, labRoot, projectRoot);
            string sessionDirectory = Path.Combine(
                project,
                "03-builds",
                "work",
                "main-workspace-sessions");
            Directory.CreateDirectory(sessionDirectory);
            var sessionPath = new WorkspacePath(Path.Combine(
                sessionDirectory,
                "state.npc-workspace.json"));
            SkyrimMainWorkspaceSessionResult savedSession =
                await sessionService.SaveAsync(
                    new SkyrimMainWorkspaceSessionSaveRequest(
                        session, sessionPath),
                    CancellationToken.None);
            Assert(
                savedSession.Accepted &&
                savedSession.Sha256 is not null,
                "Session save was refused: " +
                string.Join(
                    " | ",
                    savedSession.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
            SkyrimMainWorkspaceSessionResult reopenedSession =
                await sessionService.ReadAsync(
                    new SkyrimMainWorkspaceSessionReadRequest(
                        sessionPath,
                        savedSession.Sha256 ??
                        throw new InvalidOperationException(
                            "Saved session returned no SHA-256.")),
                    CancellationToken.None);
            Assert(
                savedSession.Accepted &&
                reopenedSession.Accepted &&
                savedSession.Sha256 == reopenedSession.Sha256 &&
                SessionEqual(session, reopenedSession.Session) &&
                reopenedSession.Sha256 ==
                    HashWorkspaceFile(sessionPath.Value),
                "Main-workspace session did not reopen exactly.");

            var stalePath = new WorkspacePath(Path.Combine(
                sessionDirectory,
                "stale.npc-workspace.json"));
            SkyrimMainWorkspaceSession staleSession = session with
            {
                Artifacts =
                [
                    handoff with
                    {
                        Sha256 =
                            new Sha256Hash(new string('9', 64))
                    }
                ]
            };
            SkyrimMainWorkspaceSessionResult stale =
                await sessionService.SaveAsync(
                    new SkyrimMainWorkspaceSessionSaveRequest(
                        staleSession, stalePath),
                    CancellationToken.None);
            Assert(
                !stale.Accepted &&
                !File.Exists(stalePath.Value) &&
                stale.Diagnostics.Any(item =>
                    item.Code ==
                    "main-workspace-artifact-hash-changed"),
                "Session accepted a stale child-artifact handoff.");
            var runtimeClaimPath = new WorkspacePath(Path.Combine(
                sessionDirectory,
                "runtime-claim.npc-workspace.json"));
            SkyrimMainWorkspaceSessionResult runtimeClaim =
                await sessionService.SaveAsync(
                    new SkyrimMainWorkspaceSessionSaveRequest(
                        session with { RuntimeAuthority = true },
                        runtimeClaimPath),
                    CancellationToken.None);
            Assert(
                !runtimeClaim.Accepted &&
                !File.Exists(runtimeClaimPath.Value) &&
                runtimeClaim.Diagnostics.Any(item =>
                    item.Code ==
                    "main-workspace-session-runtime-authority"),
                "A static session claimed runtime authority.");

            string assetRoot = Path.Combine(
                labRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "01-source-copies",
                "real-modlist",
                "briar-armor-20260723",
                "Data");
            const string assetRelative =
                "Meshes/armor/Briar/Briar_0.nif";
            string assetPath = Path.Combine(
                assetRoot,
                assetRelative.Replace(
                    '/', Path.DirectorySeparatorChar));
            Sha256Hash assetHash = HashWorkspaceFile(assetPath);
            string previewDirectory =
                Path.Combine(root, "preview");
            Directory.CreateDirectory(previewDirectory);
            string manifestPath =
                Path.Combine(previewDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    edition = "skyrimse",
                    npcFormId = "0x00000800",
                    assets = new[]
                    {
                        new
                        {
                            category = "armor",
                            path = assetRelative,
                            provider = "copied:Briar.esp",
                            sha256 = assetHash.Value
                        }
                    }
                }));
            Sha256Hash manifestHash =
                HashWorkspaceFile(manifestPath);

            var imageRenderer = new BlenderPreviewImageRenderer(
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "external",
                    "blender-4.5.1-windows-x64",
                    "blender-4.5.1-windows-x64",
                    "blender.exe")),
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "external",
                    "blender-4.5.1-pynifly-profile")),
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "rendering",
                    "render_preview_scene.py")),
                policy,
                labRoot,
                new Sha256Hash(
                    "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"));
            var nifExporter = new BlenderPreviewNifExporter(
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "external",
                    "blender-4.5.1-windows-x64",
                    "blender-4.5.1-windows-x64",
                    "blender.exe")),
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "external",
                    "blender-4.5.1-pynifly-profile")),
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    "tools",
                    "rendering",
                    "export_preview_nif.py")),
                policy,
                labRoot,
                new Sha256Hash(
                    "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"),
                new BethesdaNifGeometryReadbackService());
            var previewService =
                new SkyrimMainWorkspacePreviewService(
                    new PreviewSceneService(
                        policy, labRoot, imageRenderer),
                    new PreviewRerollService(policy, labRoot),
                    nifExporter,
                    policy,
                    labRoot);
            var selected = new SkyrimMainWorkspaceRecord(
                first,
                SkyrimMainWorkspaceRecordKind.Npc,
                "CatalogFemale",
                "Catalog Female",
                false,
                false,
                NpcSex.Female,
                [NpcCategory.Unique],
                NpcChangeState.Changed,
                [
                    new PluginName("Actors.esp"),
                    new PluginName("ActorsPatch.esp")
                ],
                [],
                new string('A', 64));
            var lighting = new PreviewLightingPreset(
                "workbench",
                1,
                0.3f,
                [
                    new PreviewLightSource(
                        "key", -30, 25, 1.2f, 1, 0.9f, 0.8f)
                ]);
            var scenePath = new WorkspacePath(
                Path.Combine(previewDirectory, "scene.json"));
            var imagePath = new WorkspacePath(
                Path.Combine(previewDirectory, "scene.png"));
            var previewRequest =
                new SkyrimMainWorkspacePreviewRequest(
                    selected,
                    new WorkspacePath(manifestPath),
                    manifestHash,
                    SkyrimMainWorkspacePreviewOptions.Default with
                    {
                        RenderHeadwear = false
                    },
                    new WorkspacePath(assetRoot),
                    scenePath,
                    imagePath,
                    Lighting: lighting);
            SkyrimMainWorkspacePreviewResult preview =
                await previewService.RenderAsync(
                    previewRequest,
                    CancellationToken.None);
            byte[] png = await File.ReadAllBytesAsync(imagePath.Value);
            Assert(
                preview.Written &&
                preview.Artifact is not null &&
                preview.Artifact.NpcFormId == "0x00000800" &&
                preview.Artifact.Lighting == lighting &&
                preview.Artifact.Assets.Single().Sha256 ==
                    assetHash.Value &&
                preview.SceneSha256 ==
                    HashWorkspaceFile(scenePath.Value) &&
                preview.ImageSha256 ==
                    HashWorkspaceFile(imagePath.Value) &&
                preview.Artifact.RenderedImage is
                {
                    Width: 512,
                    Height: 512,
                    MeshCount: > 0
                } &&
                png.Length > 8 &&
                png.AsSpan(0, 8).SequenceEqual(
                    new byte[]
                    {
                        0x89, 0x50, 0x4E, 0x47,
                        0x0D, 0x0A, 0x1A, 0x0A
                    }) &&
                !preview.RuntimeAuthority,
                "Main-workspace preview did not produce verified copied-asset PNG evidence.");

            SkyrimMainWorkspaceRecord wrongSelection = selected with
            {
                Identity = selected.Identity with
                {
                    FormId = new FormId(0x801)
                }
            };
            SkyrimMainWorkspacePreviewResult mismatch =
                await previewService.RenderAsync(
                    previewRequest with
                    {
                        SelectedRecord = wrongSelection,
                        ScenePath = new WorkspacePath(Path.Combine(
                            previewDirectory, "wrong-scene.json")),
                        ImagePath = new WorkspacePath(Path.Combine(
                            previewDirectory, "wrong-scene.png"))
                    },
                    CancellationToken.None);
            Assert(
                !mismatch.Written &&
                mismatch.Diagnostics.Any(item =>
                    item.Code ==
                    "main-workspace-preview-npc-mismatch"),
                "Preview accepted a manifest for another selected NPC.");

            var nifPath = new WorkspacePath(
                Path.Combine(previewDirectory, "scene.nif"));
            SkyrimMainWorkspaceNifExportResult nif =
                await previewService.ExportNifAsync(
                    new SkyrimMainWorkspaceNifExportRequest(
                        selected,
                        scenePath,
                        preview.SceneSha256 ??
                        throw new InvalidOperationException(
                            "Accepted preview returned no scene hash."),
                        new WorkspacePath(assetRoot),
                        nifPath),
                    CancellationToken.None);
            byte[] nifBytes =
                await File.ReadAllBytesAsync(nifPath.Value);
            Assert(
                nif.Written &&
                nif.Artifact is
                {
                    MeshCount: > 0,
                    ByteLength: > 32
                } &&
                nif.Sha256 == HashWorkspaceFile(nifPath.Value) &&
                Encoding.ASCII.GetString(
                        nifBytes,
                        0,
                        Math.Min(nifBytes.Length, 64))
                    .StartsWith(
                        "Gamebryo File Format, Version 20.2.0.7",
                        StringComparison.Ordinal) &&
                !nif.RuntimeAuthority,
                "Main-workspace NIF export did not reopen as one verified NIF.");

            SkyrimMainWorkspaceNifExportResult existing =
                await previewService.ExportNifAsync(
                    new SkyrimMainWorkspaceNifExportRequest(
                        selected,
                        scenePath,
                        preview.SceneSha256.Value,
                        new WorkspacePath(assetRoot),
                        nifPath),
                    CancellationToken.None);
            Assert(
                !existing.Written &&
                existing.Diagnostics.Any(item =>
                    item.Code ==
                    "preview-nif-binary-output-exists"),
                "Main-workspace NIF export overwrote an existing destination.");

            Console.WriteLine(
                "EVIDENCE MAIN-WORKSPACE-PERSISTENCE " +
                $"settings={savedSettings.Sha256!.Value.Value} " +
                $"session={savedSession.Sha256!.Value.Value} " +
                $"asset={assetHash.Value} " +
                $"scene={preview.SceneSha256!.Value.Value} " +
                $"png={preview.ImageSha256!.Value.Value} " +
                $"nif={nif.Sha256!.Value.Value}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool SettingsEqual(
        SkyrimMainWorkspaceSettings left,
        SkyrimMainWorkspaceSettings right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.Filter.Search == right.Filter.Search &&
        left.Filter.ShowNpcs == right.Filter.ShowNpcs &&
        left.Filter.ShowLeveledNpcs ==
            right.Filter.ShowLeveledNpcs &&
        left.Filter.Categories.SetEquals(
            right.Filter.Categories) &&
        left.Filter.Gender == right.Filter.Gender &&
        left.Filter.ChangedOnly == right.Filter.ChangedOnly &&
        left.Filter.IncludeDeleted == right.Filter.IncludeDeleted &&
        left.Preview == right.Preview;

    private static bool SessionEqual(
        SkyrimMainWorkspaceSession expected,
        SkyrimMainWorkspaceSession? actual) =>
        actual is not null &&
        expected.SchemaVersion == actual.SchemaVersion &&
        expected.IntakeFingerprint == actual.IntakeFingerprint &&
        expected.Selection.SequenceEqual(actual.Selection) &&
        expected.Drafts.SequenceEqual(actual.Drafts) &&
        expected.Artifacts.SequenceEqual(actual.Artifacts) &&
        expected.RuntimeAuthority == actual.RuntimeAuthority;

    private static Sha256Hash HashWorkspaceFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }
}
