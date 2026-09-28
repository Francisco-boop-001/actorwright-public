using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Cli.Tests;

internal static class ProviderMigrationCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task RunAsync()
    {
        await AssertApplicationProviderRefusesNestedReparseAsync();
        string root = Path.Combine(Path.GetFullPath("artifacts"),
            $"provider migration cli {Environment.ProcessId} {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = new WorkspacePath(Path.GetFullPath("."));
        SyntheticProductProviderFixture.Ensure(AppContext.BaseDirectory);
        var registry = new ApplicationProviderResourceRegistry(
            new ApplicationResourcePath(AppContext.BaseDirectory));
        var loader = new RaceMenuNpcExecutionRequestFileLoader(workspace, registry);
        var migrationService = new ProviderMigrationService(workspace, registry);
        try
        {
            BoundFile source = await WriteLegacyRequestAsync(root, workspace);
            var sourceRequest = new WorkspacePath(Path.Combine(workspace.Value,
                source.Relative.Replace('/', Path.DirectorySeparatorChar)));
            WorkspacePath aliasRoot = new(Path.Combine(root, "alias-migrated"));
            WorkspacePath reviewPath = new(Path.Combine(root, "review.json"));

            Invocation review = await InvokeAsync(workspace, loader,
                migrationService,
                ["npc", "create-from-jslot", "--request", sourceRequest.Value,
                    "--request-sha256", source.Hash.Value,
                    "--migrated-request-root", aliasRoot.Value,
                    "--provider-migration-output", reviewPath.Value, "--json"]);
            Require(review.Exit == CommandExitCode.Success,
                "provider migration review did not reach the real migration service: " +
                review.Error);
            using JsonDocument reviewJson = JsonDocument.Parse(review.Output);
            JsonElement reviewResponse = reviewJson.RootElement;
            Require(reviewResponse.TryGetProperty("reviewPath", out JsonElement reviewPathJson) &&
                    reviewPathJson.GetString() == reviewPath.Value,
                "provider migration review JSON omitted its reviewPath.");
            Require(reviewResponse.TryGetProperty("reviewSha256", out JsonElement reviewHashJson) &&
                    reviewHashJson.GetString() == Hash(reviewPath.Value).Value,
                "provider migration review JSON omitted its reviewSha256.");
            string expectedCommand =
                $"actorwright npc create-from-jslot --request \"{sourceRequest.Value}\" " +
                $"--request-sha256 {source.Hash.Value} --migrated-request-root \"{aliasRoot.Value}\" " +
                $"--reviewed-provider-migration \"{reviewPath.Value}\" " +
                $"--reviewed-provider-migration-sha256 {Hash(reviewPath.Value).Value}";
            Require(reviewResponse.TryGetProperty("acceptanceCommand", out JsonElement commandJson) &&
                    commandJson.GetString() == expectedCommand,
                "provider migration review JSON omitted the canonical next command.");

            Invocation aliasAcceptance = await InvokeAsync(workspace, loader,
                migrationService,
                ["npc", "create-from-jslot", "--request", sourceRequest.Value,
                    "--request-sha256", source.Hash.Value,
                    "--migrated-request-root", aliasRoot.Value,
                    "--provider-migration", reviewPath.Value,
                    "--provider-migration-sha256", Hash(reviewPath.Value).Value,
                    "--json"]);
            Require(aliasAcceptance.Exit == CommandExitCode.Success,
                "provider-migration alias pair did not reach AcceptAsync: " +
                aliasAcceptance.Error);
            AssertExactMigratedFiles(aliasRoot);
            await AssertMigratedRequestLoadsAsync(loader, aliasRoot);

            WorkspacePath canonicalRoot = new(Path.Combine(root,
                "canonical-migrated"));
            WorkspacePath canonicalReview = new(Path.Combine(root,
                "canonical-review.json"));
            Invocation canonicalReviewWrite = await InvokeAsync(workspace, loader,
                migrationService,
                ["npc", "create-from-jslot", "--request", sourceRequest.Value,
                    "--request-sha256", source.Hash.Value,
                    "--migrated-request-root", canonicalRoot.Value,
                    "--provider-migration-output", canonicalReview.Value, "--json"]);
            Require(canonicalReviewWrite.Exit == CommandExitCode.Success,
                "second provider migration review failed: " + canonicalReviewWrite.Error);
            using JsonDocument canonicalReviewJson = JsonDocument.Parse(
                canonicalReviewWrite.Output);
            string canonicalCommand = canonicalReviewJson.RootElement
                .GetProperty("acceptanceCommand").GetString() ??
                throw new InvalidOperationException(
                    "second migration review omitted its acceptance command.");
            string[] canonicalCommandLine = ParseWindowsCommandLine(
                canonicalCommand);
            Require(canonicalCommandLine.Length > 3 &&
                    canonicalCommandLine[0] == "actorwright",
                "canonical acceptance command was not a usable actorwright invocation.");
            Invocation canonicalAcceptance = await InvokeAsync(workspace, loader,
                migrationService,
                canonicalCommandLine[1..]);
            Require(canonicalAcceptance.Exit == CommandExitCode.Success,
                "canonical provider migration acceptance failed: " +
                canonicalAcceptance.Error);
            AssertExactMigratedFiles(canonicalRoot);
            Invocation replay = await InvokeAsync(workspace, loader, migrationService,
                ["npc", "create-from-jslot", "--request", sourceRequest.Value,
                    "--request-sha256", source.Hash.Value,
                    "--migrated-request-root", canonicalRoot.Value,
                    "--reviewed-provider-migration", canonicalReview.Value,
                    "--reviewed-provider-migration-sha256", Hash(canonicalReview.Value).Value,
                    "--json"]);
            Require(replay.Exit != CommandExitCode.Success,
                "provider migration replay overwrote its existing root.");

            Invocation mixed = await InvokeAsync(workspace, loader, migrationService,
                ["npc", "create-from-jslot", "--request", sourceRequest.Value,
                    "--request-sha256", source.Hash.Value,
                    "--migrated-request-root", Path.Combine(root, "mixed"),
                    "--reviewed-provider-migration", canonicalReview.Value,
                    "--reviewed-provider-migration-sha256", Hash(canonicalReview.Value).Value,
                    "--provider-migration", canonicalReview.Value,
                    "--provider-migration-sha256", Hash(canonicalReview.Value).Value,
                    "--json"]);
            Require(mixed.Exit == CommandExitCode.UsageError &&
                    mixed.Error.Contains("jslot-npc-provider-migration-usage",
                        StringComparison.Ordinal),
                "mixed provider migration acceptance pairs did not refuse usage.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertApplicationProviderRefusesNestedReparseAsync()
    {
        string fixtureRoot = Path.Combine(Path.GetTempPath(),
            $"actorwright-provider-reparse-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        string bundleRoot = "";
        string meshesPath = "";
        string movedMeshesPath = "";
        string dataEmptyLinkPath = "";
        string bundleEmptyLinkPath = "";
        bool moved = false;
        bool linkCreated = false;
        bool dataEmptyLinkCreated = false;
        bool bundleEmptyLinkCreated = false;
        try
        {
            bundleRoot = SyntheticProductProviderFixture.Ensure(fixtureRoot);
            var resourceBase = new ApplicationResourcePath(fixtureRoot);
            var registry = new ApplicationProviderResourceRegistry(resourceBase);
            Require(registry.TryGetDefaultBlankNpcFixture(out
                    ProductFixtureBundleReference? reference) &&
                    reference is not null,
                "Ordinary synthetic application-provider fixture was unavailable.");

            FormId templateNpc = new(0x800);
            var admitted = registry.Admit(reference!, templateNpc,
                GameEdition.SkyrimSpecialEdition, NpcSex.Female);
            Require(admitted.Accepted && admitted.Authority is not null,
                "Ordinary synthetic application-provider fixture did not admit: " +
                string.Join("; ", admitted.Diagnostics.Select(item => item.Message)));

            var workspace = new WorkspacePath(Path.GetFullPath("."));
            var provider = new BlankNpcProviderService(
                new KOnlyWorkspacePolicy(workspace,
                    new WorkspacePath(@"F:\ExampleGame")),
                workspace);
            ProviderResourceAuthoritySet resources = admitted.Authority!;
            var request = new BlankNpcProviderBindingRequest(
                default,
                resources.Manifest.ExpectedSha256,
                resources.Edition,
                resources.Sex,
                default,
                resources.TemplatePlugin.ExpectedSha256,
                templateNpc,
                default,
                resources.FaceGeomCarrier.ExpectedSha256,
                default,
                default,
                default)
            {
                ProviderResources = resources
            };
            BlankNpcProviderBindingResult ordinaryRead =
                await provider.QualifyAsync(request, CancellationToken.None);
            Require(ordinaryRead.Qualified,
                "Ordinary synthetic application-provider resources did not qualify: " +
                string.Join("; ", ordinaryRead.Diagnostics.Select(item =>
                    item.Message)));

            string emptyTargetPath = Path.Combine(fixtureRoot, "empty-link-target");
            Directory.CreateDirectory(emptyTargetPath);
            dataEmptyLinkPath = Path.Combine(bundleRoot, "Data", "empty-link");
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    dataEmptyLinkPath, emptyTargetPath, fixtureRoot))
                return;
            dataEmptyLinkCreated = true;
            ApplicationProviderResourceAdmissionResult dataLinkAdmission =
                registry.Admit(reference!, templateNpc,
                    GameEdition.SkyrimSpecialEdition, NpcSex.Female);
            Require(!dataLinkAdmission.Accepted,
                "A nested Data directory reparse point was admitted by the data-root hash scan.");
            Directory.Delete(dataEmptyLinkPath);
            dataEmptyLinkCreated = false;

            bundleEmptyLinkPath = Path.Combine(bundleRoot, "empty-link");
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    bundleEmptyLinkPath, emptyTargetPath, fixtureRoot))
                return;
            bundleEmptyLinkCreated = true;
            ApplicationProviderResourceAdmissionResult bundleLinkAdmission =
                registry.Admit(reference!, templateNpc,
                    GameEdition.SkyrimSpecialEdition, NpcSex.Female);
            Require(!bundleLinkAdmission.Accepted,
                "A nested bundle directory reparse point was admitted by the exact-file inventory scan.");
            Directory.Delete(bundleEmptyLinkPath);
            bundleEmptyLinkCreated = false;

            meshesPath = Path.Combine(bundleRoot, "Data", "meshes");
            movedMeshesPath = Path.Combine(fixtureRoot, "moved-meshes");
            Directory.Move(meshesPath, movedMeshesPath);
            moved = true;
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    meshesPath, movedMeshesPath, fixtureRoot))
            {
                Directory.Move(movedMeshesPath, meshesPath);
                moved = false;
                return;
            }
            linkCreated = true;

            ApplicationProviderResourceAdmissionResult linkedAdmission =
                registry.Admit(reference!, templateNpc,
                    GameEdition.SkyrimSpecialEdition, NpcSex.Female);
            BlankNpcProviderBindingResult linkedRead =
                await provider.QualifyAsync(request, CancellationToken.None);
            Require(!linkedAdmission.Accepted && !linkedRead.Qualified,
                $"Nested directory reparse point was accepted: admission={linkedAdmission.Accepted}, readQualified={linkedRead.Qualified}.");
        }
        finally
        {
            if (linkCreated && Directory.Exists(meshesPath))
                Directory.Delete(meshesPath);
            if (moved && Directory.Exists(movedMeshesPath) &&
                !Directory.Exists(meshesPath))
                Directory.Move(movedMeshesPath, meshesPath);
            if (bundleEmptyLinkCreated && Directory.Exists(bundleEmptyLinkPath))
                Directory.Delete(bundleEmptyLinkPath);
            if (dataEmptyLinkCreated && Directory.Exists(dataEmptyLinkPath))
                Directory.Delete(dataEmptyLinkPath);
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async Task<Invocation> InvokeAsync(
        WorkspacePath workspace,
        RaceMenuNpcExecutionRequestFileLoader loader,
        IProviderMigrationService migrationService,
        string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var handler = new RaceMenuJslotNpcBuildCommandHandler(
            new NeverBuildService(), workspace, output, error, loader,
            migrationService);
        CommandExitCode exit = await handler.RunAsync(CommandLine.Parse(args),
            CancellationToken.None);
        return new Invocation(exit, output.ToString(), error.ToString());
    }

    private static void AssertExactMigratedFiles(WorkspacePath root)
    {
        string[] files = Directory.EnumerateFiles(root.Value, "*",
                SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(files.SequenceEqual(["migration-receipt.json", "npc-request.json",
                "preset-bundle.json"], StringComparer.Ordinal),
            "provider migration did not write exactly its reviewed three-file inventory.");
    }

    private static async Task AssertMigratedRequestLoadsAsync(
        RaceMenuNpcExecutionRequestFileLoader loader,
        WorkspacePath root)
    {
        string path = Path.Combine(root.Value, "npc-request.json");
        RaceMenuNpcExecutionRequestFileLoadResult result = await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(new WorkspacePath(path),
                Hash(path)), CancellationToken.None);
        Require(result.Loaded && result.Request?.Build.ProviderContext
                    .ProviderResources is not null,
            "accepted provider migration request did not reload through schema-3 product provider.");
    }

    private static async Task<BoundFile> WriteLegacyRequestAsync(
        string root,
        WorkspacePath workspace)
    {
        BoundFile preset = await WriteFileAsync(root, workspace, "preset.jslot");
        BoundFile faceGeom = await WriteFileAsync(root, workspace, "face.nif");
        BoundFile faceTint = await WriteFileAsync(root, workspace, "face.dds");
        BoundFile record = await WriteFileAsync(root, workspace, "record.json");
        BoundFile routes = await WriteFileAsync(root, workspace, "routes.json");
        BoundFile standalone = await WriteFileAsync(root, workspace, "standalone.json");
        BoundFile dependency = await WriteFileAsync(root, workspace,
            "workspace-dependency.json");
        BoundFile providerManifest = await WriteFileAsync(root, workspace,
            "workspace-provider.json");
        BoundFile bundle = await WriteJsonAsync(root, workspace,
            "source-bundle.json", new JsonObject
            {
                ["schemaVersion"] = 1,
                ["providerContext"] = new JsonObject
                {
                    ["manifestPath"] = providerManifest.Relative,
                    ["manifestSha256"] = providerManifest.Hash.Value,
                    ["dependencyManifestPath"] = dependency.Relative,
                    ["dependencyManifestSha256"] = dependency.Hash.Value
                },
                ["retained"] = "non-provider-authority"
            });
        BoundFile sourcePlugin = await WriteFileAsync(root, workspace, "Existing.esp");
        string providerRoot = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(providerRoot);
        JsonObject request = new()
        {
            ["schemaVersion"] = 2,
            ["edition"] = "skyrimse",
            ["presetBundle"] = new JsonObject
            {
                ["manifestPath"] = bundle.Relative,
                ["manifestSha256"] = bundle.Hash.Value,
                ["presetPath"] = preset.Relative,
                ["presetSha256"] = preset.Hash.Value,
                ["faceGeomPath"] = faceGeom.Relative,
                ["faceGeomSha256"] = faceGeom.Hash.Value,
                ["faceTintPath"] = faceTint.Relative,
                ["faceTintSha256"] = faceTint.Hash.Value,
                ["recordAuthorityPath"] = record.Relative,
                ["recordAuthoritySha256"] = record.Hash.Value,
                ["runtimeRoutesPath"] = routes.Relative,
                ["runtimeRoutesSha256"] = routes.Hash.Value
            },
            ["providerContext"] = new JsonObject
            {
                ["manifestPath"] = "missing/gate1-blank-provider-bundle.json",
                ["manifestSha256"] = "EACB7112F3D0177F1C8C1B14604B3F36F413185C52973BC6027059EAE1D7D06E",
                ["templatePlugin"] = "missing/EmiCarrierProbe.esp",
                ["templateSha256"] = "421C3A902A87F343D50F8791CC4B9CAFB2B71BF3B7D94F7C2F7BE2DB6535BBF0",
                ["templateNpcFormId"] = "0x00000800",
                ["faceGeomCarrier"] = "missing/00000800.NIF",
                ["faceGeomSha256"] = "4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9",
                ["faceTintManifest"] = "missing/gate1-qualified-facetint.json",
                ["faceTintProviderRoot"] = Relative(workspace, providerRoot),
                ["dependencyManifest"] = dependency.Relative
            },
            ["standaloneAssets"] = new JsonObject
            {
                ["manifestPath"] = standalone.Relative,
                ["manifestSha256"] = standalone.Hash.Value
            },
            ["output"] = new JsonObject { ["root"] = "artifacts/provider-test-output", ["plugin"] = "ProviderTest.esp" },
            ["existingNpcTarget"] = new JsonObject
            {
                ["sourcePlugin"] = sourcePlugin.Relative,
                ["sourcePluginSha256"] = sourcePlugin.Hash.Value,
                ["targetFormId"] = "0x00000800"
            },
            ["identity"] = new JsonObject { ["editorId"] = "ActorwrightProviderTest", ["name"] = "Actorwright Provider Test" },
            ["traits"] = new JsonObject { ["sex"] = "female", ["role"] = "static-validation", ["unique"] = true, ["essential"] = false, ["protected"] = false, ["respawns"] = false, ["autoCalcStats"] = true },
            ["references"] = new JsonObject { ["race"] = "Skyrim.esm|0x00013746", ["voice"] = "Skyrim.esm|0x00013ADC", ["class"] = "Skyrim.esm|0x00013181", ["combatStyle"] = "Skyrim.esm|0x0003BE1D", ["defaultOutfit"] = "Skyrim.esm|0x0001DC10" },
            ["stats"] = new JsonObject { ["levelMode"] = "fixed", ["level"] = 1, ["magickaOffset"] = 0, ["staminaOffset"] = 0, ["healthOffset"] = 0, ["calcMinLevel"] = 1, ["calcMaxLevel"] = 1, ["speedMultiplier"] = 100, ["dispositionBase"] = 35, ["bleedoutOverride"] = 0, ["baseHealth"] = 50, ["baseMagicka"] = 50, ["baseStamina"] = 50, ["height"] = 1, ["weight"] = 0, ["farAwayModelDistance"] = 255 }
        };
        return await WriteJsonAsync(root, workspace, "legacy-request.json", request);
    }

    private static async Task<BoundFile> WriteFileAsync(string root,
        WorkspacePath workspace, string name)
    {
        string path = Path.Combine(root, name);
        byte[] bytes = Encoding.UTF8.GetBytes("fixture:" + name);
        await File.WriteAllBytesAsync(path, bytes);
        return new BoundFile(Relative(workspace, path), Hash(bytes));
    }

    private static async Task<BoundFile> WriteJsonAsync(string root,
        WorkspacePath workspace, string name, JsonObject value)
    {
        string path = Path.Combine(root, name);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await File.WriteAllBytesAsync(path, bytes);
        return new BoundFile(Relative(workspace, path), Hash(bytes));
    }

    private static string Relative(WorkspacePath workspace, string path) =>
        Path.GetRelativePath(workspace.Value, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static Sha256Hash Hash(string path) => Hash(File.ReadAllBytes(path));

    private static Sha256Hash Hash(byte[] bytes) => new(
        Convert.ToHexString(SHA256.HashData(bytes)));

    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        nint argv = CommandLineToArgvW(commandLine, out int count);
        Require(argv != 0, "Windows refused to parse the acceptance command.");
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
            {
                nint value = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(value) ??
                    throw new InvalidOperationException(
                        "Windows returned a null acceptance-command argument.");
            }
            return arguments;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record BoundFile(string Relative, Sha256Hash Hash);

    private sealed record Invocation(CommandExitCode Exit, string Output, string Error);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);

    private sealed class NeverBuildService : IRaceMenuJslotNpcBuildService
    {
        public ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
            RaceMenuJslotNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Provider migration must not enter the ordinary build service.");
    }
}
