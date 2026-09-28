using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class NpcVisualPreviewCliTests
{
    private static readonly WorkspacePath LabRoot =
        new("K:\\ExampleWorkspace");

    public static async Task TestSuccessfulMapping()
    {
        string root = NewRoot("success");
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath intake = WriteIntake(root);
            WorkspacePath package = new(
                Path.Combine(root, "npcmanager-package.json"));
            await File.WriteAllTextAsync(
                package.Value,
                "{\"schemaVersion\":\"fixture\"}");
            Sha256Hash packageHash = Hash(package.Value);
            WorkspacePath outputRoot = new(
                Path.Combine(root, "render"));
            var composer = new FakeComposer();
            var output = new StringWriter();
            var error = new StringWriter();
            var handler = new NpcVisualPreviewCommandHandler(
                composer,
                new Infrastructure.KOnlyWorkspacePolicy(
                    LabRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                LabRoot,
                new Infrastructure.FaceGeomHairRegionsDocumentCodec(LabRoot),
                output,
                error);

            CommandExitCode exit = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intake.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x00000800",
                    "--package-manifest", package.Value,
                    "--expected-package-sha256", packageHash.Value,
                    "--output-root", outputRoot.Value,
                    "--json"
                ]),
                CancellationToken.None);

            using JsonDocument document =
                JsonDocument.Parse(output.ToString());
            Assert(
                exit == CommandExitCode.Success &&
                error.ToString().Length == 0 &&
                composer.Request is not null &&
                composer.Request.Identity.OwnerPlugin.Value ==
                "Fixture.esp" &&
                composer.Request.Identity.FormId.Value == 0x800 &&
                composer.Request.PackageOverlay?.ExpectedManifestSha256 ==
                packageHash &&
                composer.Request.Options.Width == 900 &&
                composer.Request.Options.Height == 900 &&
                document.RootElement.GetProperty("composed").GetBoolean() &&
                document.RootElement.GetProperty("views").GetArrayLength() ==
                6 &&
                document.RootElement.GetProperty("label").GetString() ==
                "High-fidelity off-engine preview — Skyrim runtime remains authoritative",
                "preview npc did not bind the reviewed intake, exact overlay, selected identity, and six-view response.");

            output.GetStringBuilder().Clear();
            CommandExitCode humanExit = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intake.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x00000800",
                    "--package-manifest", package.Value,
                    "--expected-package-sha256", packageHash.Value,
                    "--output-root", outputRoot.Value
                ]),
                CancellationToken.None);
            Assert(
                humanExit == CommandExitCode.Success &&
                output.ToString().Contains(
                    "preview npc: COMPOSED",
                    StringComparison.Ordinal) &&
                output.ToString().Contains(
                    "human visual and Skyrim runtime review required",
                    StringComparison.Ordinal) &&
                !output.ToString().Contains(
                    "preview npc: PASS",
                    StringComparison.Ordinal),
                "preview npc incorrectly presented successful composition as a visual PASS.");
        }
        finally
        {
            Delete(root);
        }
    }

    public static async Task TestRefusals()
    {
        string root = NewRoot("refusals");
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath intake = WriteIntake(root);
            var composer = new FakeComposer();
            var output = new StringWriter();
            var error = new StringWriter();
            var handler = new NpcVisualPreviewCommandHandler(
                composer,
                new Infrastructure.KOnlyWorkspacePolicy(
                    LabRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                LabRoot,
                new Infrastructure.FaceGeomHairRegionsDocumentCodec(LabRoot),
                output,
                error);

            CommandExitCode partial = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intake.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x800",
                    "--package-manifest",
                    Path.Combine(root, "package.json"),
                    "--output-root",
                    Path.Combine(root, "partial"),
                    "--json"
                ]),
                CancellationToken.None);
            Assert(
                partial == CommandExitCode.UsageError &&
                composer.Request is null,
                "preview npc accepted a package manifest without its expected hash.");

            WorkspacePath malformed = new(
                Path.Combine(root, "malformed-intake.json"));
            await File.WriteAllTextAsync(
                malformed.Value,
                "{\"schemaVersion\":\"1\",\"isAccepted\":true}");
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            CommandExitCode invalid = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", malformed.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x800",
                    "--output-root",
                    Path.Combine(root, "invalid"),
                    "--json"
                ]),
                CancellationToken.None);
            using JsonDocument response =
                JsonDocument.Parse(output.ToString());
            Assert(
                invalid == CommandExitCode.ValidationFailure &&
                !response.RootElement.GetProperty("composed")
                    .GetBoolean() &&
                response.RootElement.GetProperty("diagnostics")[0]
                    .GetProperty("code").GetString() ==
                "npc-preview-intake-invalid",
                "preview npc did not fail closed for a malformed reviewed intake.");

            output.GetStringBuilder().Clear();
            CommandExitCode protectedRoot = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake",
                    "F:\\ExampleGame\\reviewed-intake.json",
                    "--plugin", "Fixture.esp",
                    "--form", "0x800",
                    "--output-root",
                    Path.Combine(root, "protected"),
                    "--json"
                ]),
                CancellationToken.None);
            Assert(
                protectedRoot == CommandExitCode.SecurityRefusal,
                "preview npc did not refuse the protected live root before file access.");
        }
        finally
        {
            Delete(root);
        }
    }

    public static async Task TestDispatchAndCancellation()
    {
        Assert(
            CommandCatalog.All.Count(item =>
                item.Name == "preview npc") == 1 &&
            CommandLine.Parse(["preview", "npc"]).Name ==
            "preview npc",
            "preview npc is not uniquely cataloged and parsed.");

        string root = NewRoot("dispatch");
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath intake = WriteIntake(root);
            var composer = new FakeComposer();
            var (runner, output, error) =
                Program.CreateRunnerForNpcVisualPreviewTests(composer);
            CommandExitCode exit = await runner.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intake.Value,
                    "--plugin", "Fixture.esp",
                    "--form", "0x800",
                    "--output-root", Path.Combine(root, "dispatch-output"),
                    "--json"
                ]),
                CancellationToken.None);
            Assert(
                exit == CommandExitCode.Success &&
                error.ToString().Length == 0 &&
                output.ToString().Contains(
                    "\"sceneSchemaVersion\": \"npc-preview-scene/2\"",
                    StringComparison.Ordinal),
                "CliRunner did not dispatch preview npc through the typed composer.");

            var (unavailable, _, unavailableError) =
                Program.CreateRunnerForNpcVisualPreviewTests(null);
            CommandExitCode unavailableExit =
                await unavailable.RunAsync(
                    CommandLine.Parse(["preview", "npc", "--json"]),
                    CancellationToken.None);
            Assert(
                unavailableExit == CommandExitCode.UsageError &&
                unavailableError.ToString().Contains(
                    "unavailable",
                    StringComparison.OrdinalIgnoreCase),
                "CliRunner silently substituted a preview composer.");

            var cancelledComposer = new FakeComposer
            {
                Cancel = true
            };
            var (cancelledRunner, _, _) =
                Program.CreateRunnerForNpcVisualPreviewTests(
                    cancelledComposer);
            using var cancellation =
                new CancellationTokenSource();
            cancellation.Cancel();
            bool propagated = false;
            try
            {
                await cancelledRunner.RunAsync(
                    CommandLine.Parse(
                    [
                        "preview", "npc",
                        "--intake", intake.Value,
                        "--plugin", "Fixture.esp",
                        "--form", "0x800",
                        "--output-root",
                        Path.Combine(root, "cancelled"),
                        "--json"
                    ]),
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                propagated = true;
            }
            Assert(
                propagated,
                "preview npc swallowed normal cancellation.");
        }
        finally
        {
            Delete(root);
        }
    }

    private static WorkspacePath WriteIntake(string root)
    {
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        string plugin = Path.Combine(dataRoot, "Fixture.esp");
        File.WriteAllBytes(plugin, [1, 2, 3, 4]);
        string loadOrder = Path.Combine(root, "loadorder.txt");
        File.WriteAllText(loadOrder, "Fixture.esp");
        WorkspacePath workspaceRoot = new(root);
        WorkspacePath reviewedOutputRoot = new(
            Path.Combine(root, "review-output"));
        Sha256Hash loadOrderHash = Hash(loadOrder);
        Sha256Hash assetFingerprint = new(new string('2', 64));
        var plugins = ImmutableArray.Create(new PluginClosureReviewEntry(
            new PluginName("Fixture.esp"),
            0,
            true,
            true,
            true,
            false,
            true,
            new WorkspacePath(plugin),
            Hash(plugin),
            []));
        Sha256Hash intakeFingerprint =
            ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                GameEdition.SkyrimSpecialEdition,
                workspaceRoot,
                new WorkspacePath(dataRoot),
                new WorkspacePath(loadOrder),
                reviewedOutputRoot,
                loadOrderHash,
                plugins,
                [],
                [],
                [],
                assetFingerprint);
        var path = new WorkspacePath(
            Path.Combine(root, "reviewed-intake.json"));
        var document = new
        {
            schemaVersion = "1",
            edition = "skyrimse",
            isAccepted = true,
            workspaceRoot = workspaceRoot.Value,
            dataRoot,
            loadOrderPath = loadOrder,
            outputRoot = reviewedOutputRoot.Value,
            loadOrderHash = loadOrderHash.Value,
            assetIndexFingerprint = assetFingerprint.Value,
            intakeFingerprint = intakeFingerprint.Value,
            plugins = new[]
            {
                new
                {
                    plugin = "Fixture.esp",
                    order = 0,
                    active = true,
                    requested = true,
                    requiredMaster = false,
                    sourceHash = Hash(plugin).Value,
                    masters = Array.Empty<string>()
                }
            },
            bodySidecarCount = 0,
            generatedPluginCount = 0,
            generatedSidecarCount = 0,
            assetProviderCount = 4,
            runtimeAuthority = false,
            diagnostics = Array.Empty<object>()
        };
        File.WriteAllText(
            path.Value,
            JsonSerializer.Serialize(document));
        return path;
    }

    private static string NewRoot(string name) =>
        Path.Combine(
            LabRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"cli-npc-visual-{name}-{Guid.NewGuid():N}");

    private static Sha256Hash Hash(string path) =>
        new(Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(path))));

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeComposer : INpcVisualPreviewComposer
    {
        public NpcVisualPreviewComposeRequest? Request { get; private set; }

        public bool Cancel { get; init; }

        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (Cancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
            Directory.CreateDirectory(request.OutputRoot.Value);
            string status = Write(
                request.OutputRoot.Value,
                "renderer-status.json",
                "{}");
            string contact = Write(
                request.OutputRoot.Value,
                "contact-sheet.png",
                "contact");
            var views =
                ImmutableArray.CreateBuilder<NpcVisualPreviewView>();
            foreach (string id in new[]
                     {
                         "face-front",
                         "face-left",
                         "face-right",
                         "face-alternate-light",
                         "body-front",
                         "body-back"
                     })
            {
                string image = Write(
                    request.OutputRoot.Value,
                    $"{id}.png",
                    id);
                string mask = Write(
                    request.OutputRoot.Value,
                    $"{id}-roles.png",
                    $"{id}-roles");
                views.Add(new NpcVisualPreviewView(
                    id,
                    new WorkspacePath(image),
                    Hash(image),
                    new WorkspacePath(mask),
                    Hash(mask),
                    900,
                    900));
            }
            string bundlePath = Write(
                request.OutputRoot.Value,
                "npc-preview-bundle.json",
                "{}");
            string manifestPath = Write(
                request.OutputRoot.Value,
                "hash-manifest.json",
                "{}");
            var source = new NpcVisualSourceGraph(
                NpcVisualPreviewRoute.Cotr,
                request.Identity,
                NpcSex.Female,
                50,
                "FixtureRace",
                "#101010",
                "#f0d0c0",
                [],
                [],
                false,
                []);
            var renderEvidence = new NpcVisualPreviewRenderEvidence(
                "4.5.1",
                "BLENDER_EEVEE_NEXT",
                1,
                true,
                1,
                ["NPC Root [Root]"],
                0,
                ImmutableDictionary<string, int>.Empty,
                ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, double>.Empty,
                [],
                [],
                new WorkspacePath(status),
                Hash(status));
            var visualEvidence = new NpcVisualPreviewVisualEvidence(
                1,
                0.99,
                478,
                31,
                true,
                []);
            var bundle = new NpcVisualPreviewBundle(
                "npc-preview-bundle/1",
                "npc-preview-scene/2",
                "High-fidelity off-engine preview — Skyrim runtime remains authoritative",
                false,
                source,
                views.ToImmutable(),
                new WorkspacePath(contact),
                Hash(contact),
                new WorkspacePath(bundlePath),
                Hash(bundlePath),
                new WorkspacePath(manifestPath),
                Hash(manifestPath),
                renderEvidence,
                visualEvidence,
                []);
            return ValueTask.FromResult(
                new NpcVisualPreviewComposeResult(
                    true,
                    bundle,
                    []));
        }

        private static string Write(
            string root,
            string name,
            string content)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
