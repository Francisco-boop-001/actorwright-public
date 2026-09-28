using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunPathAdmissionAsync()
    {
        await AssertProviderManifestRejectsStringSchemaVersionAsync();
        await RunPathAdmissionCaseAsync(@"\textures\foo.dds", "Textures/Bar.dds", refused: false);
        await RunPathAdmissionCaseAsync("/textures/foo.dds", "/Textures/Bar.dds", refused: false);
        foreach (string invalid in new[] { @"\\server\share\foo.dds", "K:/outside.dds", @"\textures\..\foo.dds" })
            await RunPathAdmissionCaseAsync(invalid, "Textures/Bar.dds", refused: true);
        foreach (string invalid in new[] { "//server/share/bar.dds", "K:/outside.dds", "/textures/../bar.dds" })
            await RunPathAdmissionCaseAsync(@"\textures\foo.dds", invalid, refused: true);
        await RunPathAdmissionCaseAsync(@"\textures\foo.dds", "Textures/Bar.dds", refused: true, alpha: 1);
    }

    private static async Task AssertProviderManifestRejectsStringSchemaVersionAsync()
    {
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        string parent = Path.Combine(repository, "artifacts", "test-work");
        Directory.CreateDirectory(parent);
        string manifestPath = Path.Combine(parent,
            $"provider-manifest-malformed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "schemaVersion": "1",
              "providerId": "malformed-schema-version",
              "edition": "skyrimse",
              "sex": "female",
              "template": { "path": "missing-template.esp", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "npcFormId": "00000800", "masters": ["Skyrim.esm"] },
              "faceGeom": { "path": "missing-face.nif", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "graphSha256": "0000000000000000000000000000000000000000000000000000000000000000", "shapeNames": ["Face", "EyeLeft", "EyeRight", "BrowLeft", "BrowRight", "Lip", "Hair"] },
              "faceTint": { "manifestPath": "missing-tint.json", "manifestSha256": "0000000000000000000000000000000000000000000000000000000000000000", "providerRoot": "provider", "sourceAssetPath": "textures/face.dds", "sourceAssetSha256": "0000000000000000000000000000000000000000000000000000000000000000" },
              "dependencies": { "manifestPath": "missing-dependencies.json", "manifestSha256": "0000000000000000000000000000000000000000000000000000000000000000", "dependencyId": "missing", "headPartCount": 0, "looseAssetCount": 0, "archiveCount": 0 }
            }
            """);

        try
        {
            var labRoot = new WorkspacePath(repository);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, ActorwrightWorkspace.ResolveProtectedRoot(labRoot));
            var service = new BlankNpcProviderService(policy, labRoot);
            bool refused = false;
            try
            {
                await service.SelectWorkspaceManifestAsync(
                    new WorkspacePath(manifestPath),
                    GameEdition.SkyrimSpecialEdition,
                    NpcSex.Female,
                    CancellationToken.None);
            }
            catch (InvalidDataException exception) when (
                exception.ToString().Contains(
                    "'schemaVersion' must be an integer.",
                    StringComparison.Ordinal))
            {
                refused = true;
            }

            Require(refused,
                "Provider manifest selection did not refuse a string schemaVersion as malformed input.");
        }
        finally
        {
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
        }
    }

    private static async Task RunPathAdmissionCaseAsync(string overlay, string destination, bool refused, float alpha = 0)
    {
        string repository = Path.GetFullPath(Environment.CurrentDirectory);
        var root = new WorkspacePath(Path.Combine(repository, "artifacts", "task26", "paths-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        MaterializeProbeFixtures(repository, Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json"),
            "npc create-from-jslot", root);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Path test binary discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", "npc create-from-jslot")).ExitCode == 0,
            "Path test schema discovery failed.");
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        var request = Child(root, "npc-preflight", "request.json");
        PrepareGoldenPresetFixture(preset);
        var presetJson = JsonNode.Parse(File.ReadAllBytes(preset.Value))!;
        presetJson["overrides"] = new JsonArray(new JsonObject
        {
            ["node"] = "Body [Ovl0]",
            ["values"] = new JsonArray(
                new JsonObject { ["key"] = 9, ["type"] = 2, ["index"] = 0, ["data"] = overlay },
                new JsonObject { ["key"] = 8, ["type"] = 4, ["index"] = -1, ["data"] = alpha })
        });
        File.WriteAllBytes(preset.Value, JsonSerializer.SerializeToUtf8Bytes(presetJson));
        CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
        var runtimeArtifact = Child(root, "overlay-runtime.json");
        File.WriteAllText(runtimeArtifact.Value, "{\"schemaVersion\":1,\"field\":\"overlays\"}");
        var runtimeRoutes = Child(root, "npc-preflight", "runtime-routes.json");
        var routes = JsonNode.Parse(File.ReadAllBytes(runtimeRoutes.Value))!;
        routes["routes"] = new JsonArray(new JsonObject { ["field"] = "overlays", ["classification"] = "runtime-declared",
            ["artifactPath"] = "overlay-runtime.json", ["artifactSha256"] = HashFile(runtimeArtifact) });
        File.WriteAllBytes(runtimeRoutes.Value, JsonSerializer.SerializeToUtf8Bytes(routes));
        var bundle = Child(root, "npc-preflight", "preset-bundle.json");
        var bundleJson = JsonNode.Parse(File.ReadAllBytes(bundle.Value))!;
        bundleJson["runtimeRoutes"]!["manifestSha256"] = HashFile(runtimeRoutes);
        File.WriteAllBytes(bundle.Value, JsonSerializer.SerializeToUtf8Bytes(bundleJson));
        var evidence = Child(root, "omit-evidence.txt");
        File.WriteAllText(evidence.Value, "Omit the exact alpha-zero Body [Ovl0] row for this synthetic path test.");
        var decisions = Child(root, "overlay-decisions.json");
        File.WriteAllBytes(decisions.Value, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, edition = "skyrimse", presetSha256 = HashFile(preset),
            decisions = new[] { new { sourceIndex = 0, node = "Body [Ovl0]", expectedTexture = "textures/foo.dds",
                action = "omit", reason = "Synthetic exact alpha-zero omission", userDecisionEvidence = new { path = "omit-evidence.txt", sha256 = HashFile(evidence) } } }
        }));
        var standalone = Child(root, "npc-preflight", "standalone-assets.json");
        var assets = JsonNode.Parse(File.ReadAllBytes(standalone.Value))!;
        assets["privateHeadTextures"]!["diffuse"] = "Bar.dds";
        assets["packageAssets"] = new JsonArray(new JsonObject
        {
            ["sourcePath"] = "npc-preflight/face.dds", ["sha256"] = HashFile(Child(root, "npc-preflight", "face.dds")),
            ["destination"] = destination
        });
        assets["overlayDecisions"] = new JsonObject { ["manifestPath"] = "overlay-decisions.json", ["manifestSha256"] = HashFile(decisions) };
        File.WriteAllBytes(standalone.Value, JsonSerializer.SerializeToUtf8Bytes(assets));
        var requestJson = JsonNode.Parse(File.ReadAllBytes(request.Value))!;
        requestJson["standaloneAssets"]!["manifestSha256"] = HashFile(standalone);
        requestJson["presetBundle"]!["manifestSha256"] = HashFile(bundle);
        requestJson["presetBundle"]!["runtimeRoutesSha256"] = HashFile(runtimeRoutes);
        File.WriteAllBytes(request.Value, JsonSerializer.SerializeToUtf8Bytes(requestJson));
        var sources = new[] { preset, request, standalone, decisions, evidence, bundle, runtimeRoutes, runtimeArtifact, Child(root, "npc-preflight", "face.dds"),
            Child(root, "Data", "ActorwrightBlankNpcProvider.esp") }.ToDictionary(path => path, HashFile);
        ProtocolInvocation created = await RunCliAsync(root, "npc", "create-from-jslot", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request), "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value);
        File.WriteAllText(Child(root, "create-result.json").Value, created.Root.ToString() + created.StdErr);
        foreach (var source in sources) Require(HashFile(source.Key) == source.Value, "Path admission changed immutable source: " + source.Key.Value);
        var output = Child(root, "planned-npc-output", "Data", "PackagedNpc.esp");
        if (refused)
        {
            Require(created.ExitCode != 0 && !File.Exists(output.Value), "Unsafe path was admitted: " + overlay + " / " + destination);
            string bad = destination == "Textures/Bar.dds" ? overlay : destination;
            Require(PathDiagnosticText(created.Root).Contains(bad, StringComparison.Ordinal), "Path refusal omitted the original offending value: " + created.Root);
            Console.WriteLine("Unsafe path refused before output: " + bad);
            return;
        }
        Require(created.ExitCode == 0 && File.Exists(output.Value), "Normalized omission/destination create failed: " + created.Root + created.StdErr);
        Require(created.Root.ToString().Contains("textures/foo.dds", StringComparison.Ordinal) &&
                created.Root.ToString().Contains("textures/bar.dds", StringComparison.Ordinal),
            "Actual create must report both normalized admitted paths: " + created.Root);
        var copied = Child(root, "planned-npc-output", "Data", "textures", "bar.dds");
        Require(File.Exists(copied.Value) && HashFile(copied) == sources[Child(root, "npc-preflight", "face.dds")],
            "Normalized destination did not preserve the actual copied texture bytes.");
        Console.WriteLine("Path roundtrip, copied DDS and unchanged bound inputs verified: " + root.Value);
    }

    internal static async Task RunPathConventionDiscoveryAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory, "artifacts", "task26", "conventions-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        Directory.CreateDirectory(Child(root, "Data").Value);
        var failures = new List<string>();
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Path convention discovery failed.");
        foreach (string? command in new string?[] { null, "workspace preflight", "npc create-from-jslot", "npc finish analyze" })
        {
            using var legacy = JsonDocument.Parse(SchemaExportService.SerializeDocument(command));
            foreach (JsonElement document in new[] { legacy.RootElement, ProtocolV2SchemaService.RenderInline(command) })
            {
                if (!document.TryGetProperty("pathConventions", out JsonElement conventions) ||
                    !conventions.TryGetProperty("cli", out var cli) || !cli.GetString()!.Contains("absolute K-local", StringComparison.Ordinal) ||
                    !conventions.TryGetProperty("documents", out var docs) || !docs.GetString()!.Contains("workspace-relative", StringComparison.Ordinal) ||
                    !docs.GetString()!.Contains("forward-slash", StringComparison.Ordinal))
                    failures.Add("Missing exact shared path conventions: " + (command ?? "all"));
            }
        }
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", "workspace preflight")).ExitCode == 0,
            "Preflight schema discovery failed.");
        foreach (string input in new[] { "Skyrim.esm,Update.esm", Child(root, "missing.json").Value, root.Value })
        {
            ProtocolInvocation result = await RunCliAsync(root, "workspace", "preflight", "--protocol", "2", "--json", "--game", "skyrimse",
                "--workspace-root", root.Value, "--data-root", Child(root, "Data").Value, "--output-root", Child(root, "output").Value, "--load-order", input,
                "--intake-output", Child(root, "intake.json").Value, "--workflow-output", Child(root, "workflow.json").Value, "--npc-editor-id", "PathTestNpc");
            string message = PathDiagnosticText(result.Root) + result.StdErr;
            if (result.ExitCode == 0 || !message.Contains("expected a schema-1 load-order JSON file; use --plugins for a comma list", StringComparison.Ordinal) ||
                !message.Contains(input, StringComparison.Ordinal)) failures.Add("Wrong load-order file admission diagnostic: " + message);
        }
        var existingLoadOrder = Child(root, "existing-load-order.json");
        File.WriteAllText(existingLoadOrder.Value, "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[]}");
        var parsed = CommandLine.Parse(["workspace", "preflight", "--game", "skyrimse",
            "--workspace-root", root.Value, "--data-root", root.Value, "--output-root", Child(root, "output").Value,
            "--load-order", existingLoadOrder.Value]);
        if (!ReviewedWorkspacePreflightBinder.Bind(parsed, root, requireExplicitWorkspace: true).IsValid)
            failures.Add("Existing load-order file was rejected by the binder before its normal schema validation.");
        try { _ = new WorkspacePath("relative/request.json"); failures.Add("Relative CLI path accepted"); }
        catch (ArgumentException error)
        {
            if (!error.Message.Contains("relative/request.json", StringComparison.Ordinal) || !error.Message.Contains("CLI", StringComparison.Ordinal))
                failures.Add("CLI path diagnostic omitted value/convention");
        }
        try { _ = new AssetPath("K:/absolute.json"); failures.Add("Absolute document path accepted"); }
        catch (ArgumentException error)
        {
            if (!error.Message.Contains("K:/absolute.json", StringComparison.Ordinal) || !error.Message.Contains("relative", StringComparison.Ordinal))
                failures.Add("Document path diagnostic omitted value/convention");
        }
        Require(new AssetPath("Textures/OldHash.dds").Value == "Textures/OldHash.dds", "Global AssetPath casing changed.");
        var finish = JsonNode.Parse(SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(new SkyrimNpcFinishCoreRequest
            { AiPolicy = new() { Mood = SkyrimNpcFinishCoreMood.Neutral } }, root))!;
        foreach (string invalid in new[] { "K:/outside/package", @"source\package", "../escape", "source/./package" })
        {
            finish["source"]!["packageRoot"] = invalid;
            try
            {
                _ = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(finish), root);
                failures.Add("Finish accepted invalid document path: " + invalid);
            }
            catch (InvalidDataException error)
            {
                if (!error.Message.Contains(invalid, StringComparison.Ordinal) ||
                    !error.Message.Contains("project-relative forward-slash", StringComparison.Ordinal))
                    failures.Add("Finish path diagnostic omitted original value/convention: " + error.Message);
            }
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Console.WriteLine("V1/V2 path conventions, actual load-order file/list refusals and strict path diagnostics verified.");
    }

    private static string PathDiagnosticText(JsonElement root) =>
        string.Join(Environment.NewLine, root.GetProperty("diagnostics").EnumerateArray().Select(row => row.GetProperty("message").GetString()));
}
