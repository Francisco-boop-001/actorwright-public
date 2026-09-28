using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;
using SkiaSharp;

namespace NpcManager.Cli.Tests;

internal static class FaceGeomHairRegionsCliTests
{
    private static readonly string[] Names =
    [
        "facegen hair-regions analyze",
        "facegen hair-regions propose",
        "facegen hair-regions preview",
        "facegen hair-regions apply",
        "facegen hair-regions verify"
    ];
    private static readonly JsonSerializerOptions RichIntakeResponseJsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

    public static Task TestCatalogAndParsing()
    {
        Require(
            CommandCatalog.All.Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == CommandCatalog.All.Length,
            "The built command catalog contains duplicate names.");
        foreach (string name in Names)
        {
            CommandDescriptor[] matches = CommandCatalog.All
                .Where(item => string.Equals(
                    item.Name,
                    name,
                    StringComparison.Ordinal))
                .ToArray();
            Require(matches.Length == 1,
                $"Catalog command '{name}' was not exposed exactly once.");
            Require(
                matches[0].SupportedGames.SequenceEqual(
                [
                    GameEdition.SkyrimSpecialEdition
                ]),
                $"Catalog command '{name}' is not closed to Skyrim SE.");
            Require(
                CommandLine.Parse(name.Split(' ')).Name == name,
                $"Command parser did not resolve '{name}'.");
        }

        Require(
            CommandCatalog.All.Single(item =>
                item.Name == "facegen hair-regions analyze").Mutates &&
            CommandCatalog.All.Single(item =>
                item.Name == "facegen hair-regions propose").Mutates &&
            CommandCatalog.All.Single(item =>
                item.Name == "facegen hair-regions preview").Mutates &&
            CommandCatalog.All.Single(item =>
                item.Name == "facegen hair-regions apply").Mutates &&
            !CommandCatalog.All.Single(item =>
                item.Name == "facegen hair-regions verify").Mutates,
            "Hair-region catalog mutation flags changed.");
        return Task.CompletedTask;
    }

    public static async Task TestStrictDocumentLoading()
    {
        string root = NewRoot("strict");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> canonical =
            codec.BindRequest(Request(root));
        string canonicalPath = Path.Combine(root, "request.json");
        await File.WriteAllBytesAsync(
            canonicalPath,
            canonical.Utf8Json.ToArray());

        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> loaded =
            await codec.LoadRequestAsync(
                new WorkspacePath(canonicalPath),
                canonical.Sha256,
                CancellationToken.None);
        Require(
            loaded.Sha256 == canonical.Sha256 &&
            loaded.Utf8Json.SequenceEqual(canonical.Utf8Json) &&
            loaded.CanonicalSha256 == canonical.CanonicalSha256 &&
            loaded.CanonicalUtf8Json.SequenceEqual(
                canonical.CanonicalUtf8Json),
            "Strict loader did not retain the exact canonical file bytes.");

        string canonicalText = Encoding.UTF8.GetString(
            canonical.Utf8Json.AsSpan());
        byte[] whitespaceBytes = Encoding.UTF8.GetBytes(
            "\r\n  " + canonicalText.Replace("\n", "\r\n",
                StringComparison.Ordinal) + "\r\n");
        JsonObject sourceObject = JsonNode.Parse(canonicalText)!.AsObject();
        var reorderedObject = new JsonObject();
        foreach ((string name, JsonNode? value) in sourceObject.Reverse())
            reorderedObject.Add(name, value?.DeepClone());
        byte[] reorderedBytes = Encoding.UTF8.GetBytes(
            reorderedObject.ToJsonString());

        foreach ((string name, byte[] bytes) in new[]
                 {
                     ("whitespace", whitespaceBytes),
                     ("reordered", reorderedBytes)
                 })
        {
            string path = Path.Combine(root, name + ".json");
            await File.WriteAllBytesAsync(path, bytes);
            var sourceHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(bytes)));
            StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
                equivalent = await codec.LoadRequestAsync(
                    new WorkspacePath(path),
                    sourceHash,
                    CancellationToken.None);
            Require(
                equivalent.Sha256 == sourceHash &&
                equivalent.Utf8Json.SequenceEqual(bytes) &&
                equivalent.Sha256 != canonical.Sha256 &&
                equivalent.CanonicalSha256 ==
                    canonical.CanonicalSha256 &&
                equivalent.CanonicalUtf8Json.SequenceEqual(
                    canonical.CanonicalUtf8Json),
                $"Strict loader did not separate {name} source identity from canonical semantics.");
        }

        await AssertInvalidAsync(codec, root, canonical, "duplicate",
            text => text.Replace(
                "\"schema\":",
                "\"schema\":\"npcmanager-facegeom-hair-regions-request/1\",\"schema\":",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "unknown",
            text => text.Replace(
                "\"analysisSha256\":",
                "\"surprise\":true,\"analysisSha256\":",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "numeric-enum",
            text => text.Replace(
                "\"role\": \"preserve\"",
                "\"role\": 0",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "pascal-enum",
            text => text.Replace(
                "\"role\": \"preserve\"",
                "\"role\": \"Preserve\"",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "pascal-property",
            text => text.Replace(
                "\"schema\":",
                "\"Schema\":",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "numeric-path",
            text => text.Replace(
                $"\"output\": \"{JsonEncodedText.Encode(canonical.Value.Output.Value)}\"",
                "\"output\": 7",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "boolean-hash",
            text => text.Replace(
                $"\"analysisSha256\": \"{canonical.Value.AnalysisSha256.Value}\"",
                "\"analysisSha256\": false",
                StringComparison.Ordinal));
        await AssertInvalidAsync(codec, root, canonical, "null-source",
            text =>
            {
                JsonObject parsed =
                    JsonNode.Parse(text)!.AsObject();
                parsed["source"] = null;
                return parsed.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
            });
        await AssertInvalidAsync(codec, root, canonical, "null-assignment",
            text =>
            {
                JsonObject parsed =
                    JsonNode.Parse(text)!.AsObject();
                parsed["assignments"]!.AsArray()[0] = null;
                return parsed.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
            });

        bool wrongHashRefused = false;
        try
        {
            await codec.LoadRequestAsync(
                new WorkspacePath(canonicalPath),
                Hash('f'),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            wrongHashRefused = true;
        }
        Require(wrongHashRefused,
            "Strict loader accepted a CLI hash that did not match file bytes.");
    }

    public static Task TestStrictPreviewEvidenceLoading()
    {
        string root = NewRoot("strict-preview-evidence");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        var texture = new FaceGeomHairTextureAuthority(
            new AssetPath(
                "textures/actors/character/hair/testhair.dds"),
            AssetProviderKind.Loose,
            "TestProvider",
            Hash('b'),
            64,
            new WorkspacePath(
                Path.Combine(root, "testhair.dds")));
        FaceGeomHairRegionsRenderAuthority renderAuthority =
            TestRenderAuthority(
                Hash('d'),
                Hash('c'),
                [texture]);
        var evidence =
            new FaceGeomHairRegionsPreviewEvidenceDocument(
                FaceGeomHairRegionSchemas.PreviewEvidence,
                Hash('1'),
                Hash('2'),
                Hash('3'),
                Hash('d'),
                Hash('c'),
                DetectedFaceCount: 1,
                LandmarkCount: 478,
                SemanticAnchorCount: 31,
                VisualAuthority: false,
                RuntimeAuthority: false,
                renderAuthority,
                [
                    new FaceGeomHairArtifactEvidence(
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace,
                        null,
                        "combined-face.png",
                        Hash('e'),
                        64,
                        900,
                        900,
                        1)
                ]);
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsPreviewEvidenceDocument> canonical =
                codec.BindPreviewEvidence(evidence);
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsPreviewEvidenceDocument> decoded =
                codec.DecodePreviewEvidence(
                    canonical.Utf8Json.AsMemory(),
                    canonical.Sha256);
        Require(
            decoded.Utf8Json.SequenceEqual(canonical.Utf8Json),
            "Strict preview evidence did not round-trip its exact canonical bytes.");

        JsonObject canonicalNode = JsonNode.Parse(
            canonical.Utf8Json.ToArray())!.AsObject();
        Require(
            canonicalNode["artifacts"]![0]!["kind"]!
                .GetValue<string>() == "combinedFace" &&
            canonicalNode["renderAuthority"]!["textures"]![0]!
                ["providerKind"]!.GetValue<string>() == "loose" &&
            canonicalNode["renderAuthority"]!["textures"]![0]!
                ["bindings"]![0]!["mode"]!
                .GetValue<string>() == "sampledImage" &&
            canonicalNode["renderAuthority"]!["pyniflyModules"]![0]!
                ["kind"]!.GetValue<string>() == "sourceFile",
            "Preview evidence enums were not encoded as exact camelCase closed-enum values.");

        AssertPreviewEvidenceInvalid(
            codec,
            canonical,
            "null-module-row",
            rootNode =>
                rootNode["renderAuthority"]!["pyniflyModules"]![0] =
                    null);
        AssertPreviewEvidenceInvalid(
            codec,
            canonical,
            "null-texture-row",
            rootNode =>
                rootNode["renderAuthority"]!["textures"]![0] =
                    null);
        AssertPreviewEvidenceInvalid(
            codec,
            canonical,
            "null-binding-row",
            rootNode =>
                rootNode["renderAuthority"]!["textures"]![0]!
                    ["bindings"]![0] = null);
        AssertPreviewEvidenceInvalid(
            codec,
            canonical,
            "null-asset-path",
            rootNode =>
                rootNode["renderAuthority"]!["textures"]![0]!
                    ["assetPath"] = null);
        AssertPreviewEvidenceInvalid(
            codec,
            canonical,
            "noncanonical-asset-path",
            rootNode =>
                rootNode["renderAuthority"]!["textures"]![0]!
                    ["assetPath"] =
                        "textures\\actors\\character\\hair\\testhair.dds");
        foreach (string unmodeledSemantic in
                 new[]
                 {
                     "BSShaderTextureSet_EnvMap",
                     "BSShaderTextureSet_EnvMask"
                 })
            AssertPreviewEvidenceInvalid(
                codec,
                canonical,
                "sampled-" + unmodeledSemantic,
                rootNode =>
                    rootNode["renderAuthority"]!["textures"]![0]!
                        ["bindings"]![0]!["bindingSemantic"] =
                            unmodeledSemantic);

        var enumCases =
            new (string Name, Action<JsonObject, JsonNode?> SetValue,
                string CaseDrift)[]
            {
                (
                    "artifact-kind",
                    (rootNode, value) =>
                        rootNode["artifacts"]![0]!["kind"] = value,
                    "CombinedFace"),
                (
                    "provider-kind",
                    (rootNode, value) =>
                        rootNode["renderAuthority"]!["textures"]![0]!
                            ["providerKind"] = value,
                    "Loose"),
                (
                    "binding-mode",
                    (rootNode, value) =>
                        rootNode["renderAuthority"]!["textures"]![0]!
                            ["bindings"]![0]!["mode"] = value,
                    "SampledImage"),
                (
                    "module-kind",
                    (rootNode, value) =>
                        rootNode["renderAuthority"]!["pyniflyModules"]![0]!
                            ["kind"] = value,
                    "SourceFile")
            };
        foreach ((string name,
                  Action<JsonObject, JsonNode?> setValue,
                  string caseDrift) in enumCases)
        {
            AssertPreviewEvidenceInvalid(
                codec,
                canonical,
                name + "-null",
                rootNode => setValue(rootNode, null));
            AssertPreviewEvidenceInvalid(
                codec,
                canonical,
                name + "-numeric",
                rootNode => setValue(
                    rootNode,
                    JsonValue.Create(0)));
            AssertPreviewEvidenceInvalid(
                codec,
                canonical,
                name + "-unknown",
                rootNode => setValue(
                    rootNode,
                    JsonValue.Create("unknown")));
            AssertPreviewEvidenceInvalid(
                codec,
                canonical,
                name + "-case-drift",
                rootNode => setValue(
                    rootNode,
                    JsonValue.Create(caseDrift)));
        }

        bool defaultAssetPathRefused = false;
        try
        {
            FaceGeomHairTextureEvidence invalidTexture =
                renderAuthority.Textures[0] with
                {
                    AssetPath = default
                };
            codec.BindPreviewEvidence(
                evidence with
                {
                    RenderAuthority =
                        renderAuthority with
                        {
                            Textures = [invalidTexture]
                        }
                });
        }
        catch (InvalidDataException)
        {
            defaultAssetPathRefused = true;
        }
        Require(
            defaultAssetPathRefused,
            "Preview evidence accepted a default AssetPath before grouping and serialization.");

        return Task.CompletedTask;
    }

    public static async Task TestPathIntakeAndInvocationRefusals()
    {
        string sourceHash = SyntheticFaceGeomHairRegionsFixture.Load(
            ActorwrightWorkspace.ResolveRoot().Value).Sha256;
        string root = NewRoot("path-intake-invocation");
        string source = Path.Combine(root, "source.nif");
        File.Copy(
            SyntheticFaceGeomHairRegionsFixture.Load(
                ActorwrightWorkspace.ResolveRoot().Value).Path,
            source);
        var codec = new FaceGeomHairRegionsDocumentCodec();
        var handler = CreateHandler(
            codec,
            new UnconfiguredFaceGeomHairRegionsPreviewService(),
            out StringWriter output,
            out _);

        CommandExitCode trailingExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze", "unexpected",
                "--source", source,
                "--expected-source-sha256", sourceHash,
                "--analysis", Path.Combine(root, "trailing-analysis.json"),
                "--assignment-template", Path.Combine(root, "trailing-request.json"),
                "--json"
            ]),
            CancellationToken.None);
        Require(
            trailingExit == CommandExitCode.UsageError &&
            output.ToString().Contains(
                "\"code\": \"usage-error\"",
                StringComparison.Ordinal),
            "A trailing positional was ignored by a hair-region command.");

        output.GetStringBuilder().Clear();
        string duplicateAnalysis =
            Path.Combine(root, "duplicate-analysis.json");
        CommandExitCode duplicateExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze",
                "--source", source,
                "--source", source,
                "--expected-source-sha256", sourceHash,
                "--analysis", duplicateAnalysis,
                "--assignment-template", Path.Combine(root, "duplicate-request.json"),
                "--json"
            ]),
            CancellationToken.None);
        Require(
            duplicateExit == CommandExitCode.UsageError &&
            !File.Exists(duplicateAnalysis),
            "A duplicate option was silently resolved by last-wins parsing.");

        output.GetStringBuilder().Clear();
        string collisionTemplate = Path.Combine(root, "collision.json");
        string collisionAnalysis =
            Path.Combine(root, "collision.output.nif");
        CommandExitCode topologyExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze",
                "--source", source,
                "--expected-source-sha256", sourceHash,
                "--analysis", collisionAnalysis,
                "--assignment-template", collisionTemplate,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            topologyExit == CommandExitCode.ValidationFailure &&
            !File.Exists(collisionAnalysis) &&
            !File.Exists(collisionTemplate),
            "Analyze allowed its JSON path to alias the derived future NIF.");

        string missingRoot = Path.Combine(root, "missing-intake");
        Directory.CreateDirectory(Path.Combine(missingRoot, "Data"));
        await File.WriteAllTextAsync(
            Path.Combine(missingRoot, "loadorder.txt"),
            "Skyrim.esm");
        bool missingPluginRefused = false;
        try
        {
            codec.BindReviewedIntake(
                ReviewedIntake(missingRoot),
                new WorkspacePath(Path.Combine(
                    missingRoot,
                    "intake.json")));
        }
        catch (InvalidDataException)
        {
            missingPluginRefused = true;
        }
        Require(
            missingPluginRefused,
            "Reviewed intake fabricated Exists/ReadSucceeded for a missing plugin.");

        ReviewedGameIntake accepted =
            await CreateReviewedIntakeFixtureAsync(root);
        ReviewedGameIntake richAuthority =
            await CreateRichReviewedIntakeFixtureAsync(root);
        string richIntakePath =
            Path.Combine(root, "rich-intake.json");
        ReviewedGameIntakeDocumentAuthority richDocument =
            codec.BindReviewedIntake(
                richAuthority,
                new WorkspacePath(richIntakePath));
        await File.WriteAllBytesAsync(
            richIntakePath,
            richDocument.Document.Utf8Json.ToArray());
        ReviewedGameIntakeDocumentAuthority richReloaded =
            await codec.LoadReviewedIntakeAsync(
                new WorkspacePath(richIntakePath),
                CancellationToken.None);
        ReviewedGameIntakeDocumentAuthority richRebound =
            codec.BindReviewedIntake(
                richReloaded.Value,
                new WorkspacePath(
                    Path.Combine(
                        root,
                        "rich-intake-rebound.json")));
        RequireRichIntakeRoundTrip(
            richAuthority,
            richReloaded.Value);
        Require(
            richDocument.Document.Utf8Json
                .SequenceEqual(
                    richRebound.Document.Utf8Json) &&
            richReloaded.Value.IntakeFingerprint ==
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(
                        richReloaded.Value),
            "Reviewed intake did not round-trip to identical canonical bytes and fingerprint.");
        string intakePath = Path.Combine(root, "intake-with-error.json");
        ReviewedGameIntakeDocumentAuthority intake =
            codec.BindReviewedIntake(
                accepted,
                new WorkspacePath(intakePath));
        string canonicalIntakeText = Encoding.UTF8.GetString(
            intake.Document.Utf8Json.AsSpan());
        string intakeText = canonicalIntakeText.Replace(
                "\"diagnostics\": []",
                "\"diagnostics\": [{\"code\":\"bad\",\"severity\":\"error\",\"message\":\"rejected\"}]",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(intakePath, intakeText);
        bool intakeErrorRefused = false;
        try
        {
            await codec.LoadReviewedIntakeAsync(
                new WorkspacePath(intakePath),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            intakeErrorRefused = true;
        }
        Require(
            intakeErrorRefused,
            "Reviewed intake ignored an embedded error diagnostic.");

        string missingDiagnosticsPath =
            Path.Combine(root, "intake-missing-diagnostics.json");
        const string diagnosticsMarker = "\"diagnostics\": []";
        int diagnosticsIndex = canonicalIntakeText.LastIndexOf(
            diagnosticsMarker,
            StringComparison.Ordinal);
        int diagnosticsComma = diagnosticsIndex < 0
            ? -1
            : canonicalIntakeText.LastIndexOf(
                ',',
                diagnosticsIndex);
        Require(
            diagnosticsComma >= 0,
            "The intake fixture did not contain the expected diagnostics field.");
        string missingDiagnosticsText = canonicalIntakeText.Remove(
            diagnosticsComma,
            checked(
                diagnosticsIndex +
                diagnosticsMarker.Length -
                diagnosticsComma));
        await File.WriteAllTextAsync(
            missingDiagnosticsPath,
            missingDiagnosticsText);
        bool missingDiagnosticsRefused = false;
        try
        {
            await codec.LoadReviewedIntakeAsync(
                new WorkspacePath(missingDiagnosticsPath),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            missingDiagnosticsRefused = true;
        }
        Require(
            missingDiagnosticsRefused,
            "Reviewed intake did not stably refuse a missing diagnostics collection.");

        JsonObject nullPluginDocument =
            JsonNode.Parse(canonicalIntakeText)!.AsObject();
        nullPluginDocument["plugins"]!.AsArray()[0] = null;
        string nullPluginPath =
            Path.Combine(root, "intake-null-plugin.json");
        await File.WriteAllTextAsync(
            nullPluginPath,
            nullPluginDocument.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        bool nullPluginRefused = false;
        try
        {
            await codec.LoadReviewedIntakeAsync(
                new WorkspacePath(nullPluginPath),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            nullPluginRefused = true;
        }
        Require(
            nullPluginRefused,
            "Reviewed intake did not stably refuse a null plugin entry.");

        JsonObject nullHashDocument =
            JsonNode.Parse(canonicalIntakeText)!.AsObject();
        nullHashDocument["loadOrderHash"] = null;
        string nullHashPath =
            Path.Combine(root, "intake-null-hash.json");
        await File.WriteAllTextAsync(
            nullHashPath,
            nullHashDocument.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        bool nullHashRefused = false;
        try
        {
            await codec.LoadReviewedIntakeAsync(
                new WorkspacePath(nullHashPath),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            nullHashRefused = true;
        }
        Require(
            nullHashRefused,
            "Reviewed intake did not map a null SHA-256 field to the stable invalid-document refusal.");

        var redirectHooks =
            new AdversarialPinnedFileSystemHooks(
                source,
                redirectFinalPath:
                    @"F:\ExampleGame\redirected-source.nif");
        var redirectBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                redirectHooks);
        bool finalHandleRefused = false;
        try
        {
            await redirectBoundary.ReadExactFileAsync(
                new WorkspacePath(source),
                128L * 1024L * 1024L,
                "redirected source",
                CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
            finalHandleRefused = true;
        }
        Require(
            finalHandleRefused,
            "A final file handle resolving outside K was read instead of refused.");

        string pinnedParent =
            Path.Combine(root, "pinned-parent");
        Directory.CreateDirectory(pinnedParent);
        string pinnedFile =
            Path.Combine(pinnedParent, "authority.bin");
        byte[] pinnedBytes = [5, 4, 3, 2, 1];
        await File.WriteAllBytesAsync(
            pinnedFile,
            pinnedBytes);
        var swapHooks =
            new AdversarialPinnedFileSystemHooks(
                pinnedFile,
                swapParent: pinnedParent);
        var swapBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                swapHooks);
        byte[]? pinnedRead = null;
        bool parentSwapRefused = false;
        try
        {
            pinnedRead =
                await swapBoundary.ReadExactFileAsync(
                    new WorkspacePath(pinnedFile),
                    1024,
                    "pinned race source",
                    CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
            parentSwapRefused = true;
        }
        finally
        {
            swapHooks.RestoreParent();
        }
        Require(
            swapHooks.ParentSwapAttempted &&
            (!swapHooks.ParentSwapSucceeded &&
             pinnedRead is not null &&
             pinnedRead.SequenceEqual(pinnedBytes) ||
             swapHooks.ParentSwapSucceeded &&
             parentSwapRefused) &&
            (await File.ReadAllBytesAsync(pinnedFile))
                .SequenceEqual(pinnedBytes),
            "A parent replacement redirected a supposedly handle-relative pinned file read.");

        var createRaceHooks =
            new AdversarialPinnedFileSystemHooks(
                source,
                raceCreate: true);
        var createRaceBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                createRaceHooks);
        var createRaceCodec =
            new FaceGeomHairRegionsDocumentCodec(
                createRaceBoundary);
        var createRaceHandler = CreateHandler(
            createRaceCodec,
            new UnconfiguredFaceGeomHairRegionsPreviewService(),
            out output,
            out _);
        CommandExitCode createRaceExit =
            await createRaceHandler.RunAsync(
                CommandLine.Parse(
                [
                    "facegen", "hair-regions", "analyze",
                    "--source", source,
                    "--expected-source-sha256", sourceHash,
                    "--analysis",
                    Path.Combine(
                        root,
                        "create-race-analysis.json"),
                    "--assignment-template",
                    Path.Combine(
                        root,
                        "create-race-request.json"),
                    "--json"
                ]),
                CancellationToken.None);
        Require(
            createRaceExit ==
                CommandExitCode.ValidationFailure &&
            createRaceHooks.CreateRacePath is not null &&
            File.ReadAllBytes(
                    createRaceHooks.CreateRacePath)
                .SequenceEqual(
                    AdversarialPinnedFileSystemHooks
                        .RaceSentinel),
            "A CREATE_NEW race was overwritten or claimed as a transaction-owned temporary.");

        var nestedRoot = new WorkspacePath(
            Path.Combine(root, "pinned-nested-tree"));
        using (
            FaceGeomHairRegionsPinnedDirectory nestedLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    nestedRoot,
                    "nested preview proof"))
        {
            string nestedDirectory =
                Path.Combine(
                    nestedRoot.Value,
                    "regions");
            Directory.CreateDirectory(nestedDirectory);
            string nestedArtifact =
                Path.Combine(
                    nestedDirectory,
                    "mask.png");
            await File.WriteAllBytesAsync(
                nestedArtifact,
                PreviewPng());
            ImmutableArray<
                FaceGeomHairRegionsPinnedTreeEntry>
                nestedEntries =
                    nestedLease.EnumerateTree();
            Require(
                nestedEntries.Any(entry =>
                    entry.IsDirectory &&
                    entry.Path.Value ==
                    nestedDirectory) &&
                nestedEntries.Any(entry =>
                    !entry.IsDirectory &&
                    entry.Path.Value ==
                    nestedArtifact),
                "Pinned preview-tree enumeration lacked directory-list access for nested artifacts.");
        }

        string syntheticLink = Path.Combine(root, "synthetic-link");
        var boundary = new FaceGeomHairRegionsWorkspaceBoundary(
            ActorwrightWorkspace.ResolveRoot(),
            new PredicateReparsePathInspector(syntheticLink));
        bool predicateRefused = false;
        try
        {
            boundary.RequireNewFile(
                new WorkspacePath(Path.Combine(
                    syntheticLink,
                    "result.json")),
                "test-output");
        }
        catch (UnauthorizedAccessException)
        {
            predicateRefused = true;
        }
        Require(
            predicateRefused,
            "The shared path boundary did not exercise its reparse predicate.");

        string reparseTarget = Path.Combine(root, "reparse-target");
        string reparseLink = Path.Combine(root, "reparse-link");
        Directory.CreateDirectory(reparseTarget);
        if (PhysicalReparseFixture.TryCreateDirectoryLink(
                reparseLink,
                reparseTarget,
                root))
        {
            try
            {
                output.GetStringBuilder().Clear();
                CommandExitCode reparseExit = await handler.RunAsync(
                    CommandLine.Parse(
                    [
                        "facegen", "hair-regions", "analyze",
                        "--source", source,
                        "--expected-source-sha256", sourceHash,
                        "--analysis", Path.Combine(reparseLink, "analysis.json"),
                        "--assignment-template", Path.Combine(reparseLink, "request.json"),
                        "--json"
                    ]),
                    CancellationToken.None);
                Require(
                    reparseExit == CommandExitCode.SecurityRefusal &&
                    !File.Exists(Path.Combine(
                        reparseTarget,
                        "analysis.json")),
                    "A physical K-local reparse redirect reached its target.");
            }
            finally
            {
                Directory.Delete(reparseLink);
            }
        }

        Require(
            FaceGeomHairRegionsOwnedFileCleanup
                .OpenReparsePointFlag == 0x00200000 &&
            !FaceGeomHairRegionsOwnedFileCleanup
                .IsOrdinaryFileAttributes(
                    FileAttributes.ReparsePoint),
            "Owned cleanup does not pin and refuse a final reparse object.");
        string cleanupTarget =
            Path.Combine(root, "cleanup-target.bin");
        string cleanupLink =
            Path.Combine(root, "cleanup-link.bin");
        byte[] cleanupBytes = [41, 42, 43, 44];
        await File.WriteAllBytesAsync(
            cleanupTarget,
            cleanupBytes);
        if (PhysicalReparseFixture.TryCreateFileLink(
                cleanupLink,
                cleanupTarget,
                root))
        {
            try
            {
                bool deleted =
                    FaceGeomHairRegionsOwnedFileCleanup
                        .TryDeleteExactFile(
                            cleanupLink,
                            cleanupBytes,
                            out _);
                Require(
                    !deleted &&
                    File.Exists(cleanupTarget) &&
                    (File.GetAttributes(cleanupLink) &
                     FileAttributes.ReparsePoint) != 0,
                    "Owned cleanup followed or deleted a final reparse replacement.");
            }
            finally
            {
                if (File.Exists(cleanupLink))
                    File.Delete(cleanupLink);
            }
        }
    }

    public static async Task TestPreviewHandlerContract()
    {
        string root = NewRoot("preview");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(root, codec);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> request =
            transaction.Request;
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal> proposal =
            transaction.Proposal;
        string requestPath = transaction.RequestPath;
        string proposalPath = transaction.ProposalPath;
        string intakePath = Path.Combine(root, "reviewed-intake.json");
        ReviewedGameIntake reviewedIntake =
            await CreateReviewedIntakeFixtureAsync(root);
        ReviewedGameIntakeDocumentAuthority intakeAuthority =
            codec.BindReviewedIntake(
                reviewedIntake,
                new WorkspacePath(intakePath));
        byte[] capturedIntakeBytes = intakeAuthority.Document.Utf8Json
            .AddRange(Encoding.UTF8.GetBytes(Environment.NewLine))
            .ToArray();
        await File.WriteAllBytesAsync(
            intakePath,
            capturedIntakeBytes);

        var fake = new RecordingPreviewService();
        var output = new StringWriter();
        var error = new StringWriter();
        var handler = new FaceGeomHairRegionsCommandHandler(
            ActorwrightWorkspace.ResolveRoot(),
            new FaceGeomHairRegionsAnalyzer(
                ActorwrightWorkspace.ResolveRoot()),
            new FaceGeomHairRegionsProposer(
                ActorwrightWorkspace.ResolveRoot(),
                codec),
            new FaceGeomHairRegionsApplyService(
                ActorwrightWorkspace.ResolveRoot(),
                new BethesdaFaceGeomHairRegionsVerifier(),
                codec),
            codec,
            fake,
            new AcceptedPreviewVisualValidator(),
            output,
            error);
        string outputRoot = Path.Combine(root, "render");
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "preview",
                "--request", requestPath,
                "--request-sha256", request.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposal.Sha256.Value,
                "--intake", intakePath,
                "--output-root", outputRoot,
                "--json"
            ]),
            CancellationToken.None);

        using JsonDocument response =
            JsonDocument.Parse(output.ToString());
        Require(
            exit == CommandExitCode.Success &&
            fake.Request is not null &&
            fake.Request.RequestDocument.Sha256 == request.Sha256 &&
            fake.Request.ProposalDocument.Sha256 == proposal.Sha256 &&
            fake.Request.IntakeDocument.Path.Value == intakePath &&
            fake.Request.Intake.Edition ==
                GameEdition.SkyrimSpecialEdition &&
            fake.Request.Intake.IntakeFingerprint ==
                reviewedIntake.IntakeFingerprint &&
            !string.Equals(
                fake.Request.OutputRoot.Value,
                outputRoot,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetDirectoryName(
                    fake.Request.OutputRoot.Value),
                Path.GetDirectoryName(outputRoot),
                StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(
                fake.Request.OutputRoot.Value) &&
            Directory.Exists(outputRoot) &&
            fake.Request.Materialization.Candidate.Sha256 ==
                proposal.Value.ExpectedOutput.Sha256 &&
            response.RootElement.GetProperty("command").GetString() ==
                "facegen hair-regions preview" &&
            response.RootElement.GetProperty("phaseVerdict").GetString() ==
                "PASS" &&
            response.RootElement.GetProperty("visualAuthority")
                .ValueKind == JsonValueKind.False &&
            response.RootElement.GetProperty("runtimeAuthority")
                .ValueKind == JsonValueKind.False &&
            response.RootElement.GetProperty("artifacts")[0]
                .GetProperty("sha256").GetString() ==
                fake.Artifacts[0].Sha256.Value &&
            string.Equals(
                Path.GetDirectoryName(
                    response.RootElement
                        .GetProperty("artifacts")[0]
                        .GetProperty("path")
                        .GetString()),
                outputRoot,
                StringComparison.OrdinalIgnoreCase) &&
            response.RootElement.GetProperty("artifacts")[0]
                .GetProperty("nonEmptyPixelCount").GetInt64() > 0 &&
            response.RootElement.GetProperty("previewEvidence")
                .GetProperty("proposalSha256").GetString() ==
                proposal.Sha256.Value &&
            response.RootElement.GetProperty("previewEvidence")
                .GetProperty("intakeSha256").GetString() ==
                Sha(capturedIntakeBytes).Value &&
            response.RootElement.GetProperty("previewEvidence")
                .GetProperty("detectedFaceCount").GetInt32() == 1 &&
            response.RootElement.GetProperty("previewEvidence")
                .GetProperty("landmarkCount").GetInt32() == 478 &&
            response.RootElement.GetProperty("previewEvidence")
                .GetProperty("semanticAnchorCount").GetInt32() == 31 &&
            error.ToString().Length == 0,
            "Preview handler did not preserve exact authorities or emit the required envelope.");
    }

    public static async Task TestPreviewAuthorityAndProofRefusals()
    {
        string root = NewRoot("preview-proof");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        ReviewedGameIntake intake =
            await CreateReviewedIntakeFixtureAsync(root);
        string intakePath = Path.Combine(root, "intake.json");
        ReviewedGameIntakeDocumentAuthority intakeDocument =
            codec.BindReviewedIntake(
                intake,
                new WorkspacePath(intakePath));
        await File.WriteAllBytesAsync(
            intakePath,
            intakeDocument.Document.Utf8Json.ToArray());

        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            invalidRequest = codec.BindRequest(Request(root));
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            invalidProposal = codec.BindProposal(
                Proposal(
                    invalidRequest.Value,
                    invalidRequest.Sha256));
        string invalidRequestPath =
            Path.Combine(root, "invalid-request.json");
        string invalidProposalPath =
            Path.Combine(root, "invalid-proposal.json");
        await File.WriteAllBytesAsync(
            invalidRequestPath,
            invalidRequest.Utf8Json.ToArray());
        await File.WriteAllBytesAsync(
            invalidProposalPath,
            invalidProposal.Utf8Json.ToArray());
        var shouldNotRender = new WeakPreviewService();
        var invalidHandler = CreateHandler(
            codec,
            shouldNotRender,
            out StringWriter output,
            out _);
        CommandExitCode invalidExit = await invalidHandler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "preview",
                "--request", invalidRequestPath,
                "--request-sha256", invalidRequest.Sha256.Value,
                "--proposal", invalidProposalPath,
                "--proposal-sha256", invalidProposal.Sha256.Value,
                "--intake", intakePath,
                "--output-root", Path.Combine(root, "invalid-render"),
                "--json"
            ]),
            CancellationToken.None);
        Require(
            invalidExit == CommandExitCode.ValidationFailure &&
            !shouldNotRender.Called,
            "Preview invoked the renderer before full Apply-equivalent materialization.");

        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(
                Path.Combine(root, "authentic"),
                codec);
        string existingOutputRoot =
            Path.Combine(root, "existing-output");
        Directory.CreateDirectory(existingOutputRoot);
        string existingSentinel =
            Path.Combine(existingOutputRoot, "external.bin");
        byte[] existingSentinelBytes = [41, 42, 43, 44];
        await File.WriteAllBytesAsync(
            existingSentinel,
            existingSentinelBytes);
        var existingShouldNotRender =
            new WeakPreviewService();
        var existingHandler = CreateHandler(
            codec,
            existingShouldNotRender,
            out output,
            out _);
        CommandExitCode existingExit =
            await RunPreviewAsync(
                existingHandler,
                transaction,
                intakePath,
                existingOutputRoot);
        Require(
            existingExit == CommandExitCode.ValidationFailure &&
            !existingShouldNotRender.Called &&
            (await File.ReadAllBytesAsync(existingSentinel))
                .SequenceEqual(existingSentinelBytes) &&
            !Directory.EnumerateFileSystemEntries(
                    root,
                    ".existing-output-preview-*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "An existing output-root collision was touched or allowed preview work to begin.");

        FaceGeomHairRegionsAuthorizedEnvelope firstEnvelope =
            transaction.Proposal.Value.AuthorizedEnvelopes[0];
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            forgedProposal = codec.BindProposal(
                transaction.Proposal.Value with
                {
                    AuthorizedEnvelopes =
                        transaction.Proposal.Value
                            .AuthorizedEnvelopes.SetItem(
                                0,
                                firstEnvelope with
                                {
                                    SharedShaderGroupId =
                                        firstEnvelope
                                            .SharedShaderGroupId +
                                        ":forged"
                                })
                });
        string forgedProposalPath =
            Path.Combine(root, "forged-group-proposal.json");
        await File.WriteAllBytesAsync(
            forgedProposalPath,
            forgedProposal.Utf8Json.ToArray());
        var forgedShouldNotRender = new WeakPreviewService();
        var forgedHandler = CreateHandler(
            codec,
            forgedShouldNotRender,
            out output,
            out _);
        CommandExitCode forgedExit = await RunPreviewAsync(
            forgedHandler,
            transaction with
            {
                Proposal = forgedProposal,
                ProposalPath = forgedProposalPath
            },
            intakePath,
            Path.Combine(root, "forged-group-render"));
        Require(
            forgedExit == CommandExitCode.ValidationFailure &&
            !forgedShouldNotRender.Called,
            "Preview trusted a proposal-supplied shader group instead of re-deriving source structure.");

        var weakProof = new WeakPreviewService();
        var weakHandler = CreateHandler(
            codec,
            weakProof,
            out output,
            out _);
        string weakOutputRoot =
            Path.Combine(root, "weak-render");
        CommandExitCode weakExit = await RunPreviewAsync(
            weakHandler,
            transaction,
            intakePath,
            weakOutputRoot);
        Require(
            weakExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "facegeom-hair-regions-preview-evidence-invalid",
                StringComparison.Ordinal),
            "Preview accepted a nonexistent one-artifact proof.");
        RequirePreviewBundleRolledBack(
            weakOutputRoot,
            "Proof failure retained a public or private preview bundle.");

        string directoryRaceRoot =
            Path.Combine(root, "directory-create-race");
        var directoryRaceHooks =
            new DirectoryCreateRacePinnedFileSystemHooks(
                directoryRaceRoot);
        var directoryRaceBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                directoryRaceHooks);
        var directoryRaceCodec =
            new FaceGeomHairRegionsDocumentCodec(
                directoryRaceBoundary);
        var directoryRaceHandler = CreateHandler(
            directoryRaceCodec,
            new RecordingPreviewService(),
            out output,
            out _);
        CommandExitCode directoryRaceExit =
            await RunPreviewAsync(
                directoryRaceHandler,
                transaction,
                intakePath,
                directoryRaceRoot);
        Require(
            directoryRaceExit ==
                CommandExitCode.ValidationFailure &&
            directoryRaceHooks.RaceCreated &&
            File.ReadAllBytes(
                    Path.Combine(
                        directoryRaceRoot,
                        "external-sentinel.bin"))
                .SequenceEqual(
                    DirectoryCreateRacePinnedFileSystemHooks
                        .RaceSentinel),
            "A preview output-root CREATE_NEW race was overwritten or claimed.");

        var selfAsserted = new RecordingPreviewService();
        var selfAssertedHandler = CreateHandler(
            codec,
            selfAsserted,
            out output,
            out _,
            visualValidator:
                new RejectingPreviewVisualValidator());
        string selfAssertedOutputRoot =
            Path.Combine(root, "self-asserted-render");
        CommandExitCode selfAssertedExit =
            await RunPreviewAsync(
                selfAssertedHandler,
                transaction,
                intakePath,
                selfAssertedOutputRoot);
        Require(
            selfAssertedExit ==
                CommandExitCode.ValidationFailure,
            "Preview trusted renderer-supplied face, landmark, and semantic-anchor counts instead of the visual validator.");
        RequirePreviewBundleRolledBack(
            selfAssertedOutputRoot,
            "Visual proof failure retained a public or private preview bundle.");

        foreach ((PreviewProofMutation Mutation, string Message) in
                 new[]
                 {
                     (
                         PreviewProofMutation.OversizedArtifact,
                         "per-artifact encoded byte limit"),
                     (
                         PreviewProofMutation.CumulativeOversize,
                         "cumulative encoded byte limit")
                 })
        {
            var oversized =
                new RecordingPreviewService(Mutation);
            var oversizedHandler = CreateHandler(
                codec,
                oversized,
                out output,
                out _);
            string budgetOutputRoot = Path.Combine(
                root,
                "budget-" + Mutation);
            CommandExitCode oversizedExit =
                await RunPreviewAsync(
                    oversizedHandler,
                    transaction,
                    intakePath,
                    budgetOutputRoot);
            Require(
                oversizedExit ==
                    CommandExitCode.ValidationFailure &&
                output.ToString().Contains(
                    Message,
                    StringComparison.OrdinalIgnoreCase),
                $"Preview did not reject {Mutation} before artifact allocation with the stable encoded-budget refusal.");
            RequirePreviewBundleRolledBack(
                budgetOutputRoot,
                $"Budget proof failure {Mutation} retained a public or private preview bundle.");
        }

        foreach (PreviewProofMutation mutation in
                 new[]
                 {
                      PreviewProofMutation.ExtraArtifact,
                      PreviewProofMutation.DuplicateMask,
                      PreviewProofMutation.WrongReadbackHash,
                      PreviewProofMutation.InvalidImageBytes,
                      PreviewProofMutation.WrongPixelCount,
                      PreviewProofMutation.InvalidEvidenceDocument,
                      PreviewProofMutation.EvidenceClaimsPixels,
                      PreviewProofMutation.UnlistedFilesystemEntry,
                      PreviewProofMutation.WrongProductionDimensions
                 })
        {
            var mutated = new RecordingPreviewService(mutation);
            var mutatedHandler = CreateHandler(
                codec,
                mutated,
                out output,
                out _);
            string mutatedOutputRoot = Path.Combine(
                root,
                "mutated-" + mutation);
            CommandExitCode mutatedExit = await RunPreviewAsync(
                mutatedHandler,
                transaction,
                intakePath,
                mutatedOutputRoot);
            Require(
                mutatedExit == CommandExitCode.ValidationFailure,
                $"Preview accepted the closed-proof mutation {mutation}.");
            RequirePreviewBundleRolledBack(
                mutatedOutputRoot,
                $"Closed-proof mutation {mutation} retained a public or private preview bundle.");
        }
    }

    public static async Task TestResultInvariantCancellationAndRollback()
    {
        string root = NewRoot("result-invariants");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(
                Path.Combine(root, "transaction"),
                codec);
        ReviewedGameIntake intake =
            await CreateReviewedIntakeFixtureAsync(root);
        string intakePath = Path.Combine(root, "intake.json");
        ReviewedGameIntakeDocumentAuthority intakeDocument =
            codec.BindReviewedIntake(
                intake,
                new WorkspacePath(intakePath));
        await File.WriteAllBytesAsync(
            intakePath,
            intakeDocument.Document.Utf8Json.ToArray());

        var warningOnly = new InconsistentPreviewService(
            succeeded: false,
            new Diagnostic(
                "warning-only",
                DiagnosticSeverity.Warning,
                "A failed result without an Error diagnostic."));
        var warningHandler = CreateHandler(
            codec,
            warningOnly,
            out StringWriter output,
            out _);
        CommandExitCode warningExit = await RunPreviewAsync(
            warningHandler,
            transaction,
            intakePath,
            Path.Combine(root, "warning-render"));
        Require(
            warningExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "\"phaseVerdict\": \"REFUSED\"",
                StringComparison.Ordinal),
            "Succeeded=false with no Error diagnostic failed open.");

        var successWithError = new InconsistentPreviewService(
            succeeded: true,
            new Diagnostic(
                "renderer-error",
                DiagnosticSeverity.Error,
                "A successful result may not carry an Error diagnostic."));
        var errorHandler = CreateHandler(
            codec,
            successWithError,
            out output,
            out _);
        CommandExitCode errorExit = await RunPreviewAsync(
            errorHandler,
            transaction,
            intakePath,
            Path.Combine(root, "error-render"));
        Require(
            errorExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "\"phaseVerdict\": \"REFUSED\"",
                StringComparison.Ordinal),
            "Succeeded=true with an Error diagnostic failed open.");

        var defaultDiagnosticsHandler = CreateHandler(
            codec,
            new DefaultDiagnosticsPreviewService(),
            out output,
            out _);
        CommandExitCode defaultDiagnosticsExit =
            await RunPreviewAsync(
                defaultDiagnosticsHandler,
                transaction,
                intakePath,
                Path.Combine(root, "default-diagnostics-render"));
        Require(
            defaultDiagnosticsExit ==
                CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "\"phaseVerdict\": \"REFUSED\"",
                StringComparison.Ordinal),
            "A default preview diagnostics collection escaped the fail-closed result invariant.");

        var nullArtifactHandler = CreateHandler(
            codec,
            new NullArtifactPreviewService(),
            out output,
            out _);
        CommandExitCode nullArtifactExit =
            await RunPreviewAsync(
                nullArtifactHandler,
                transaction,
                intakePath,
                Path.Combine(root, "null-artifact-render"));
        Require(
            nullArtifactExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "\"phaseVerdict\": \"REFUSED\"",
                StringComparison.Ordinal),
            "A null preview artifact escaped the fail-closed result invariant.");

        var cleanupRefusalCases =
            new (
                string Name,
                IFaceGeomHairRegionsPreviewService Service)[]
            {
                (
                    "malformed-result-cleanup",
                    new DefaultDiagnosticsPreviewService()),
                (
                    "contradictory-result-cleanup",
                    new InconsistentPreviewService(
                        succeeded: true,
                        new Diagnostic(
                            "controlled-contradiction",
                            DiagnosticSeverity.Error,
                            "Controlled successful result with an error.")))
            };
        foreach ((string name,
                  IFaceGeomHairRegionsPreviewService service)
                 in cleanupRefusalCases)
        {
            string cleanupRefusalOutput =
                Path.Combine(root, name);
            var cleanupRefusalCodec =
                new FaceGeomHairRegionsDocumentCodec(
                    new FaceGeomHairRegionsWorkspaceBoundary(
                        new WorkspacePath(
                            ActorwrightWorkspace.ResolveRoot().Value),
                        new CleanupRefusingPinnedFileSystemHooks()));
            var cleanupRefusalHandler = CreateHandler(
                cleanupRefusalCodec,
                service,
                out output,
                out _);
            try
            {
                CommandExitCode cleanupRefusalExit =
                    await RunPreviewAsync(
                        cleanupRefusalHandler,
                        transaction,
                        intakePath,
                        cleanupRefusalOutput);
                using JsonDocument cleanupRefusalResponse =
                    JsonDocument.Parse(output.ToString());
                string[] cleanupSurvivors =
                    cleanupRefusalResponse.RootElement
                        .GetProperty("artifacts")
                        .EnumerateArray()
                        .Select(item =>
                            item.GetProperty("path").GetString()!)
                        .ToArray();
                Require(
                    cleanupRefusalExit ==
                        CommandExitCode.ValidationFailure &&
                    cleanupSurvivors.Length == 1 &&
                    Directory.Exists(
                        cleanupSurvivors[0]) &&
                    !Directory.Exists(
                        cleanupRefusalOutput),
                    $"{name} discarded the exact surviving private root.");
            }
            finally
            {
                foreach (string path in
                         Directory.EnumerateDirectories(
                             root,
                             $".{name}-preview-*.tmp",
                             SearchOption.TopDirectoryOnly))
                    Directory.Delete(
                        path,
                        recursive: true);
            }
        }

        var securityRefusal = new InconsistentPreviewService(
            succeeded: false,
            new Diagnostic(
                "facegeom-hair-regions-security-refusal",
                DiagnosticSeverity.Error,
                "Synthetic security refusal."));
        var securityHandler = CreateHandler(
            codec,
            securityRefusal,
            out output,
            out _);
        CommandExitCode securityExit = await RunPreviewAsync(
            securityHandler,
            transaction,
            intakePath,
            Path.Combine(root, "security-render"));
        Require(
            securityExit == CommandExitCode.SecurityRefusal,
            "A typed security refusal did not map to exit 3.");

        var protectedOutput =
            new WorkspacePath(
                @"F:\ExampleGame\facegeom-hair-regions-test.nif");
        var protectedManifest =
            new WorkspacePath(
                @"F:\ExampleGame\facegeom-hair-regions-test.manifest.json");
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            protectedRequest = codec.BindRequest(
                transaction.Request.Value with
                {
                    Output = protectedOutput,
                    Manifest = protectedManifest
                });
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            protectedProposal = codec.BindProposal(
                transaction.Proposal.Value with
                {
                    RequestSha256 = protectedRequest.Sha256,
                    Output = protectedOutput,
                    Manifest = protectedManifest,
                    ExpectedOutput =
                        transaction.Proposal.Value.ExpectedOutput with
                        {
                            Path = protectedOutput
                        }
                });
        string protectedRequestPath =
            Path.Combine(root, "protected-request.json");
        string protectedProposalPath =
            Path.Combine(root, "protected-proposal.json");
        await File.WriteAllBytesAsync(
            protectedRequestPath,
            protectedRequest.Utf8Json.ToArray());
        await File.WriteAllBytesAsync(
            protectedProposalPath,
            protectedProposal.Utf8Json.ToArray());
        var protectedHandler = CreateHandler(
            codec,
            new UnconfiguredFaceGeomHairRegionsPreviewService(),
            out output,
            out _);
        CommandExitCode protectedExit =
            await protectedHandler.RunAsync(
                CommandLine.Parse(
                [
                    "facegen", "hair-regions", "apply",
                    "--request", protectedRequestPath,
                    "--request-sha256",
                    protectedRequest.Sha256.Value,
                    "--proposal", protectedProposalPath,
                    "--proposal-sha256",
                    protectedProposal.Sha256.Value,
                    "--output", protectedOutput.Value,
                    "--manifest", protectedManifest.Value,
                    "--json"
                ]),
                CancellationToken.None);
        Require(
            protectedExit == CommandExitCode.SecurityRefusal,
            "Apply's typed outside/protected refusal mapped to validation instead of security.");

        string cancellationSurvivor =
            Path.Combine(root, "cancel-survivor.tmp");
        await File.WriteAllBytesAsync(
            cancellationSurvivor,
            [1, 2, 3, 4]);
        var cancelHandler = CreateHandler(
            codec,
            new CancellingPreviewService(cancellationSurvivor),
            out output,
            out _);
        CommandExitCode cancelExit = await RunPreviewAsync(
            cancelHandler,
            transaction,
            intakePath,
            Path.Combine(root, "cancel-render"));
        using (JsonDocument cancellation =
               JsonDocument.Parse(output.ToString()))
        {
            Require(
                cancelExit == CommandExitCode.Cancelled &&
                cancellation.RootElement.GetProperty("artifacts")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("path").GetString() ==
                        cancellationSurvivor) &&
                cancellation.RootElement.GetProperty("diagnostics")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("message").GetString()!
                            .Contains(
                                "PID 4242",
                                StringComparison.Ordinal) &&
                        item.GetProperty("message").GetString()!
                            .Contains(
                                "Controlled access denial",
                                StringComparison.Ordinal)),
                "Typed cancellation lost a surviving artifact, PID, or termination failure detail.");
        }

        string operationalOutput =
            Path.Combine(root, "operational-render");
        var operationalHandler = CreateHandler(
            codec,
            new OperationallyFailingPreviewService(),
            out output,
            out _);
        CommandExitCode operationalExit =
            await RunPreviewAsync(
                operationalHandler,
                transaction,
                intakePath,
                operationalOutput);
        Require(
            operationalExit ==
                CommandExitCode.ValidationFailure,
            "Expected preview I/O failure did not map to a typed validation refusal.");
        RequirePreviewBundleRolledBack(
            operationalOutput,
            "Expected preview I/O failure retained a public or private transaction tree.");

        string processOperationalOutput =
            Path.Combine(
                root,
                "process-operational-render");
        var processOperationalHandler = CreateHandler(
            codec,
            new ProcessOperationallyFailingPreviewService(),
            out output,
            out _);
        CommandExitCode processOperationalExit =
            await RunPreviewAsync(
                processOperationalHandler,
                transaction,
                intakePath,
                processOperationalOutput);
        using (JsonDocument processOperationalResponse =
               JsonDocument.Parse(output.ToString()))
        {
            Require(
                processOperationalExit ==
                    CommandExitCode.ValidationFailure &&
                processOperationalResponse.RootElement
                    .GetProperty("diagnostics")
                    .EnumerateArray()
                    .Any(item =>
                        item.GetProperty("message").GetString()!
                            .Contains(
                                "PID 4646",
                                StringComparison.Ordinal) &&
                        item.GetProperty("message").GetString()!
                            .Contains(
                                "Controlled execution-failure termination denial",
                                StringComparison.Ordinal)),
                "Execution-failure refusal lost the exact surviving PID or termination failure.");
        }
        RequirePreviewBundleRolledBack(
            processOperationalOutput,
            "Execution-failure refusal retained a public or private transaction tree.");

        foreach (bool nestedSecurity in new[] { false, true })
        {
            string securityOutput = Path.Combine(
                root,
                nestedSecurity
                    ? "nested-security-render"
                    : "direct-security-render");
            var expectedSecurityHandler = CreateHandler(
                codec,
                new SecurityFailingPreviewService(
                    nestedSecurity),
                out output,
                out _);
            CommandExitCode expectedSecurityExit =
                await RunPreviewAsync(
                    expectedSecurityHandler,
                    transaction,
                    intakePath,
                    securityOutput);
            using JsonDocument securityResponse =
                JsonDocument.Parse(output.ToString());
            Require(
                expectedSecurityExit ==
                    CommandExitCode.SecurityRefusal &&
                securityResponse.RootElement
                    .GetProperty("diagnostics")
                    .EnumerateArray()
                    .Any(item =>
                        item.GetProperty("code").GetString() ==
                        "facegeom-hair-regions-security-refused"),
                $"{(nestedSecurity ? "Nested" : "Direct")} SecurityException was not classified as a fail-closed security refusal.");
            RequirePreviewBundleRolledBack(
                securityOutput,
                $"{(nestedSecurity ? "Nested" : "Direct")} SecurityException retained a public or private transaction tree.");
        }

        string survivorOutput =
            Path.Combine(root, "operational-survivor-render");
        var survivorHooks =
            new CleanupRefusingPinnedFileSystemHooks();
        var survivorCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    survivorHooks));
        var survivorHandler = CreateHandler(
            survivorCodec,
            new OperationallyFailingPreviewService(),
            out output,
            out _);
        try
        {
            CommandExitCode survivorExit =
                await RunPreviewAsync(
                    survivorHandler,
                    transaction,
                    intakePath,
                    survivorOutput);
            using JsonDocument survivorResponse =
                JsonDocument.Parse(output.ToString());
            string[] reportedSurvivors =
                survivorResponse.RootElement
                    .GetProperty("artifacts")
                    .EnumerateArray()
                    .Select(item =>
                        item.GetProperty("path").GetString()!)
                    .ToArray();
            Require(
                survivorExit ==
                    CommandExitCode.ValidationFailure &&
                reportedSurvivors.Length == 1 &&
                Directory.Exists(
                    reportedSurvivors[0]) &&
                !Directory.Exists(survivorOutput),
                "Expected preview I/O rollback did not report the exact surviving private root.");
        }
        finally
        {
            foreach (string path in Directory.EnumerateDirectories(
                         root,
                         ".operational-survivor-render-preview-*.tmp",
                         SearchOption.TopDirectoryOnly))
                Directory.Delete(path, recursive: true);
        }

        string rendererCancelOutput =
            Path.Combine(root, "renderer-cancel-render");
        string rendererCancelWork =
            Path.Combine(root, "renderer-cancel-work");
        var rendererCancelService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(rendererCancelWork),
                new RecordingHairRegionsSourceService(),
                new CancellingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                codec,
                createOwnedWorkRoot: true);
        var rendererCancelHandler = CreateHandler(
            codec,
            rendererCancelService,
            out output,
            out _);
        CommandExitCode rendererCancelExit =
            await RunPreviewAsync(
                rendererCancelHandler,
                transaction,
                intakePath,
                rendererCancelOutput);
        rendererCancelService.Dispose();
        Require(
            rendererCancelExit == CommandExitCode.Cancelled &&
            !Directory.Exists(rendererCancelWork),
            "Cancellation during renderer execution did not map to typed cancellation or clean its owned work root.");
        RequirePreviewBundleRolledBack(
            rendererCancelOutput,
            "Cancellation during renderer execution retained a public or private preview bundle.");

        string publicationCancelOutput =
            Path.Combine(root, "publication-cancel-render");
        string publicationCancelWork =
            Path.Combine(root, "publication-cancel-work");
        var publicationHooks =
            new PublicationCancellingPinnedFileSystemHooks();
        var publicationBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                publicationHooks);
        var publicationCodec =
            new FaceGeomHairRegionsDocumentCodec(
                publicationBoundary);
        var publicationCancelService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(publicationCancelWork),
                new RecordingHairRegionsSourceService(),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                publicationCodec,
                createOwnedWorkRoot: true);
        var publicationCancelHandler = CreateHandler(
            publicationCodec,
            publicationCancelService,
            out output,
            out _);
        CommandExitCode publicationCancelExit =
            await RunPreviewAsync(
                publicationCancelHandler,
                transaction,
                intakePath,
                publicationCancelOutput);
        publicationCancelService.Dispose();
        Require(
            publicationCancelExit == CommandExitCode.Cancelled &&
            publicationHooks.CancellationTriggered &&
            !Directory.Exists(publicationCancelWork),
            "Cancellation during pinned image publication did not occur at the controlled boundary or clean its owned work root.");
        RequirePreviewBundleRolledBack(
            publicationCancelOutput,
            "Cancellation during image publication retained a public or private preview bundle.");

        string source = Path.Combine(root, "rollback-source.nif");
        File.Copy(
            SyntheticFaceGeomHairRegionsFixture.Load(
                ActorwrightWorkspace.ResolveRoot().Value).Path,
            source);
        string refusedAnalysis =
            Path.Combine(root, "security-analysis.json");
        var refusingBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new SecurityRefusingPinnedFileSystemHooks());
        var refusingCodec =
            new FaceGeomHairRegionsDocumentCodec(
                refusingBoundary);
        var refusingHandler = CreateHandler(
            refusingCodec,
            new UnconfiguredFaceGeomHairRegionsPreviewService(),
            out output,
            out _);
        CommandExitCode wrappedSecurityExit =
            await refusingHandler.RunAsync(
                CommandLine.Parse(
                [
                    "facegen", "hair-regions", "analyze",
                    "--source", source,
                    "--expected-source-sha256",
                    Sha(await File.ReadAllBytesAsync(source)).Value,
                    "--analysis", refusedAnalysis,
                    "--assignment-template",
                    Path.Combine(root, "security-request.json"),
                    "--json"
                ]),
                CancellationToken.None);
        Require(
            wrappedSecurityExit == CommandExitCode.SecurityRefusal,
            "A transaction-wrapped UnauthorizedAccessException did not map to the security exit.");

        string firstDestination =
            Path.Combine(root, "rollback-analysis.json");
        string secondDestination =
            Path.Combine(root, "rollback-request.json");
        byte[] replacement = Encoding.UTF8.GetBytes(
            "concurrent replacement");
        var replacingHooks =
            new ReplacingPinnedFileSystemHooks(
                firstDestination,
                secondDestination,
                replacement);
        var rollbackBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                replacingHooks);
        var rollbackCodec =
            new FaceGeomHairRegionsDocumentCodec(
                rollbackBoundary);
        var rollbackHandler = CreateHandler(
            rollbackCodec,
            new UnconfiguredFaceGeomHairRegionsPreviewService(),
            out output,
            out _);
        CommandExitCode rollbackExit = await rollbackHandler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze",
                "--source", source,
                "--expected-source-sha256",
                Sha(await File.ReadAllBytesAsync(source)).Value,
                "--analysis", firstDestination,
                "--assignment-template", secondDestination,
                "--json"
            ]),
            CancellationToken.None);
        using JsonDocument rollback =
            JsonDocument.Parse(output.ToString());
        Require(
            rollbackExit == CommandExitCode.ValidationFailure &&
            replacingHooks.FirstReplacementRefused &&
            !File.Exists(firstDestination) &&
            (await File.ReadAllBytesAsync(secondDestination))
                .SequenceEqual(replacement) &&
            !rollback.RootElement.GetProperty("artifacts")
                .EnumerateArray().Any(),
            "Paired rollback failed to keep the promoted handle pinned or touched the unowned collision. " +
            $"Exit={rollbackExit}; replacementRefused={replacingHooks.FirstReplacementRefused}; " +
            $"firstExists={File.Exists(firstDestination)}; secondExists={File.Exists(secondDestination)}; " +
            $"response={output}");
    }

    public static async Task
        TestRichReviewedIntakeResponseRoundTrip()
    {
        string root =
            NewRoot("rich-reviewed-response");
        ReviewedGameIntake rich =
            await CreateRichReviewedIntakeFixtureAsync(
                root);
        var request =
            new ReviewedGameIntakeRequest(
                rich.Edition,
                rich.WorkspaceRoot,
                rich.DataRoot,
                rich.LoadOrderPath,
                rich.OutputRoot,
                rich.Plugins.Select(item =>
                        item.Plugin)
                    .ToImmutableArray());
        var result =
            new ReviewedGameIntakeResult(
                rich.Edition,
                rich.Plugins,
                rich,
                []);
        ReviewedGameIntakeResponse response =
            ReviewedGameIntakeResponse.From(
                request,
                result);
        byte[] bytes =
            JsonSerializer.SerializeToUtf8Bytes(
                response,
                RichIntakeResponseJsonOptions);
        string intakePath =
            Path.Combine(root, "reviewed-intake.json");
        await File.WriteAllBytesAsync(
            intakePath,
            bytes);
        ReviewedGameIntakeDocumentAuthority reloaded =
            await new FaceGeomHairRegionsDocumentCodec()
                .LoadReviewedIntakeAsync(
                    new WorkspacePath(intakePath),
                    CancellationToken.None);
        using JsonDocument document =
            JsonDocument.Parse(bytes);
        JsonElement responseRoot =
            document.RootElement;
        Require(
            response.SchemaVersion == "2" &&
            response.BodySidecars.Length == 1 &&
            response.GeneratedPlugins.Length == 1 &&
            response.GeneratedSidecars.Length == 1 &&
            responseRoot.GetProperty("bodySidecars")[0]
                .GetProperty("plugin").GetString() ==
                "RichGenerated.esp" &&
            responseRoot.GetProperty("generatedPlugins")[0]
                .GetProperty("npcFormIds")
                .GetArrayLength() == 2 &&
            responseRoot.GetProperty("generatedSidecars")[0]
                .GetProperty("relativePath").GetString() ==
                "meshes/actors/character/FaceGenData/FaceGeom/RichGenerated.esp/00000800.nif",
            "Reviewed-intake response mapping dropped a nonempty auxiliary authority field.");
        RequireRichIntakeRoundTrip(
            rich,
            reloaded.Value);
    }

    public static async Task
        TestPublicPreviewOwnsWholeBundleTransaction()
    {
        string root =
            NewRoot("public-preview-bundle-transaction");
        var transactionCodec =
            new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(
                Path.Combine(root, "transaction"),
                transactionCodec);
        ReviewedGameIntake intake =
            await CreateReviewedIntakeFixtureAsync(root);
        ReviewedGameIntakeDocumentAuthority intakeAuthority =
            transactionCodec.BindReviewedIntake(
                intake,
                new WorkspacePath(
                    Path.Combine(root, "intake.json")));
        FaceGeomHairRegionsProposalMaterialization
            materialization =
                await new FaceGeomHairRegionsApplyService(
                        new WorkspacePath(
                            ActorwrightWorkspace.ResolveRoot().Value),
                        new BethesdaFaceGeomHairRegionsVerifier(),
                        transactionCodec)
                    .MaterializeAsync(
                        transaction.Request,
                        transaction.Proposal,
                        CancellationToken.None);

        string successfulOutput =
            Path.Combine(root, "successful-public-preview");
        string successfulWork =
            Path.Combine(root, "successful-public-work");
        var successHooks =
            new PostFirstPublicationFailureHooks(
                successfulOutput,
                failAfterFirst: false,
                cancel: false,
                refuseCleanup: false);
        var successCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    successHooks));
        var successService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(successfulWork),
                new RecordingHairRegionsSourceService(),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                successCodec,
                createOwnedWorkRoot: true);
        FaceGeomHairRegionsPreviewResult succeeded =
            await successService.PreviewAsync(
                new FaceGeomHairRegionsPreviewRequest(
                    transaction.Request,
                    transaction.Proposal,
                    materialization,
                    intakeAuthority,
                    new WorkspacePath(successfulOutput)),
                CancellationToken.None);
        successService.Dispose();
        Require(
            succeeded.Succeeded &&
            successHooks.BundlePromotionObserved &&
            successHooks
                .BundleSourceWasPrivateAndDestinationHidden &&
            !successHooks.PublicRootExposedDuringPublication &&
            Directory.Exists(successfulOutput) &&
            succeeded.Artifacts.All(item =>
                item.Path.IsUnder(
                    new WorkspacePath(successfulOutput)) &&
                File.Exists(item.Path.Value)) &&
            !Directory.EnumerateDirectories(
                    root,
                    $".{Path.GetFileName(successfulOutput)}-preview-*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any() &&
            !Directory.Exists(successfulWork),
            "Public preview success did not publish one complete sibling-private bundle with final-root artifact paths.");

        string failedOutput =
            Path.Combine(root, "failed-public-preview");
        string failedWork =
            Path.Combine(root, "failed-public-work");
        var failureHooks =
            new PostFirstPublicationFailureHooks(
                failedOutput,
                failAfterFirst: true,
                cancel: false,
                refuseCleanup: false);
        var failureCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    failureHooks));
        var failureService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(failedWork),
                new RecordingHairRegionsSourceService(),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                failureCodec,
                createOwnedWorkRoot: true);
        FaceGeomHairRegionsPreviewResult failed =
            await failureService.PreviewAsync(
                new FaceGeomHairRegionsPreviewRequest(
                    transaction.Request,
                    transaction.Proposal,
                    materialization,
                    intakeAuthority,
                    new WorkspacePath(failedOutput)),
                CancellationToken.None);
        failureService.Dispose();
        Require(
            !failed.Succeeded &&
            failureHooks.FirstPromotionObserved &&
            failureHooks.PublicationAttemptCount == 2 &&
            !failureHooks.PublicRootExposedDuringPublication &&
            failed.Diagnostics.Any(item =>
                item.Code ==
                "facegeom-hair-regions-image-publication-invalid") &&
            !Directory.Exists(failedOutput) &&
            failureHooks.PrivatePublicationRoot is not null &&
            !Directory.Exists(
                failureHooks.PrivatePublicationRoot) &&
            !Directory.Exists(failedWork),
            "Public preview failure after its first promotion retained a partial bundle or escaped the Infrastructure-owned rollback.");

        string survivorOutput =
            Path.Combine(root, "surviving-public-preview");
        string survivorWork =
            Path.Combine(root, "surviving-public-work");
        var survivorHooks =
            new PostFirstPublicationFailureHooks(
                survivorOutput,
                failAfterFirst: true,
                cancel: false,
                refuseCleanup: true);
        var survivorCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    survivorHooks));
        var survivorService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(survivorWork),
                new RecordingHairRegionsSourceService(),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                survivorCodec,
                createOwnedWorkRoot: true);
        try
        {
            FaceGeomHairRegionsPreviewResult survivorResult =
                await survivorService.PreviewAsync(
                    new FaceGeomHairRegionsPreviewRequest(
                        transaction.Request,
                        transaction.Proposal,
                        materialization,
                        intakeAuthority,
                        new WorkspacePath(survivorOutput)),
                    CancellationToken.None);
            throw new InvalidOperationException(
                "Ordinary refusal with a cleanup survivor returned an unstructured result. " +
                $"succeeded={survivorResult.Succeeded}; " +
                $"diagnostics={string.Join(",", survivorResult.Diagnostics.Select(item => item.Code))}");
        }
        catch (FaceGeomHairRegionsOperationalException exception)
        {
            Require(
                survivorHooks.FirstPromotionObserved &&
                !survivorHooks
                    .PublicRootExposedDuringPublication &&
                survivorHooks.PrivatePublicationRoot is
                    { } privateRoot &&
                exception.SurvivingArtifacts.SequenceEqual(
                    [new WorkspacePath(privateRoot)]) &&
                !Directory.Exists(survivorOutput) &&
                Directory.Exists(privateRoot) &&
                !Directory.EnumerateFileSystemEntries(
                        privateRoot)
                    .Any(),
                "Ordinary public-preview refusal did not surface exact cleanup survivors through typed operational evidence.");
        }
        finally
        {
            survivorService.Dispose();
            if (survivorHooks.PrivatePublicationRoot is
                    { } privateRoot &&
                Directory.Exists(privateRoot))
                Directory.Delete(
                    privateRoot,
                    recursive: true);
        }
        Require(
            !Directory.Exists(survivorWork),
            "Ordinary refusal with a cleanup survivor retained its owned preview work root.");

        string cancelledOutput =
            Path.Combine(root, "cancelled-public-preview");
        string cancelledWork =
            Path.Combine(root, "cancelled-public-work");
        var cancellationHooks =
            new PostFirstPublicationFailureHooks(
                cancelledOutput,
                failAfterFirst: true,
                cancel: true,
                refuseCleanup: true);
        var cancellationCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    cancellationHooks));
        var cancellationService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(cancelledWork),
                new RecordingHairRegionsSourceService(),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                cancellationCodec,
                createOwnedWorkRoot: true);
        try
        {
            await cancellationService.PreviewAsync(
                new FaceGeomHairRegionsPreviewRequest(
                    transaction.Request,
                    transaction.Proposal,
                    materialization,
                    intakeAuthority,
                    new WorkspacePath(cancelledOutput)),
                CancellationToken.None);
            throw new InvalidOperationException(
                "Controlled post-first-promotion cancellation was not surfaced.");
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            Require(
                cancellationHooks.FirstPromotionObserved &&
                cancellationHooks.PublicationAttemptCount == 2 &&
                !cancellationHooks
                    .PublicRootExposedDuringPublication &&
                cancellationHooks.PrivatePublicationRoot is
                    { } privateRoot &&
                exception.SurvivingArtifacts.SequenceEqual(
                    [new WorkspacePath(privateRoot)]) &&
                !Directory.Exists(cancelledOutput) &&
                Directory.Exists(privateRoot) &&
                !Directory.EnumerateFileSystemEntries(
                        privateRoot)
                    .Any(),
                "Public preview cancellation did not roll back promoted files or report the exact cleanup survivor.");
        }
        finally
        {
            cancellationService.Dispose();
            if (cancellationHooks.PrivatePublicationRoot is
                    { } privateRoot &&
                Directory.Exists(privateRoot))
                Directory.Delete(
                    privateRoot,
                    recursive: true);
        }
        Require(
            !Directory.Exists(cancelledWork),
            "Public cancellation retained its owned preview work root.");
    }

    public static async Task
        TestSourceWarningReachesPreviewCliJson()
    {
        string root =
            NewRoot("source-warning-propagation");
        var codec =
            new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(
                Path.Combine(root, "transaction"),
                codec);
        ReviewedGameIntake intake =
            await CreateReviewedIntakeFixtureAsync(root);
        string intakePath =
            Path.Combine(root, "intake.json");
        ReviewedGameIntakeDocumentAuthority intakeAuthority =
            codec.BindReviewedIntake(
                intake,
                new WorkspacePath(intakePath));
        await File.WriteAllBytesAsync(
            intakePath,
            intakeAuthority.Document.Utf8Json.ToArray());
        string workRoot =
            Path.Combine(root, "preview-work");
        var warning =
            new Diagnostic(
                "facegeom-hair-regions-source-controlled-warning",
                DiagnosticSeverity.Warning,
                "Controlled source composition warning.");
        var service =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(workRoot),
                new RecordingHairRegionsSourceService(
                    [warning]),
                new ProvingHairRegionsRenderer(),
                new ProvingHairRegionsVisualValidator(),
                codec,
                createOwnedWorkRoot: true);
        var handler = CreateHandler(
            codec,
            service,
            out StringWriter output,
            out _);
        CommandExitCode exit =
            await RunPreviewAsync(
                handler,
                transaction,
                intakePath,
                Path.Combine(root, "preview"));
        service.Dispose();
        using JsonDocument response =
            JsonDocument.Parse(output.ToString());
        Require(
            exit == CommandExitCode.Success &&
            response.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Any(item =>
                    item.GetProperty("code").GetString() ==
                        warning.Code &&
                    item.GetProperty("severity").GetString() ==
                        "warning" &&
                    item.GetProperty("message").GetString() ==
                        warning.Message) &&
            !Directory.Exists(workRoot),
            "A successful source-composition warning was dropped from the final preview result or CLI JSON.");

        string contradictoryWork =
            Path.Combine(root, "contradictory-source-work");
        string contradictoryOutput =
            Path.Combine(root, "contradictory-source-preview");
        var sourceError =
            new Diagnostic(
                "facegeom-hair-regions-source-controlled-error",
                DiagnosticSeverity.Error,
                "Controlled contradictory composed source error.");
        var contradictoryRenderer =
            new ProvingHairRegionsRenderer();
        var contradictoryService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(contradictoryWork),
                new RecordingHairRegionsSourceService(
                    [sourceError]),
                contradictoryRenderer,
                new ProvingHairRegionsVisualValidator(),
                codec,
                createOwnedWorkRoot: true);
        var contradictoryHandler = CreateHandler(
            codec,
            contradictoryService,
            out StringWriter contradictoryJson,
            out _);
        CommandExitCode contradictoryExit =
            await RunPreviewAsync(
                contradictoryHandler,
                transaction,
                intakePath,
                contradictoryOutput);
        contradictoryService.Dispose();
        using JsonDocument contradictoryResponse =
            JsonDocument.Parse(
                contradictoryJson.ToString());
        Require(
            contradictoryExit ==
                CommandExitCode.ValidationFailure &&
            contradictoryRenderer.ProcessCount == 0 &&
            contradictoryResponse.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Any(item =>
                    item.GetProperty("code").GetString() ==
                        sourceError.Code &&
                    item.GetProperty("severity").GetString() ==
                        "error" &&
                    item.GetProperty("message").GetString() ==
                        sourceError.Message) &&
            !Directory.Exists(contradictoryOutput) &&
            !Directory.Exists(contradictoryWork),
            "Composed=true with an Error diagnostic did not fail closed before renderer invocation while preserving the exact source error.");
    }

    public static async Task TestTransactionLifecycleAndRefusals()
    {
        string sourceHash = SyntheticFaceGeomHairRegionsFixture.Load(
            ActorwrightWorkspace.ResolveRoot().Value).Sha256;
        string root = NewRoot("transaction");
        string fixture = SyntheticFaceGeomHairRegionsFixture.Load(
                ActorwrightWorkspace.ResolveRoot().Value).Path;
        string source = Path.Combine(root, "source.nif");
        File.Copy(fixture, source);
        string analysisPath = Path.Combine(root, "analysis.json");
        string requestPath = Path.Combine(root, "assignment.json");
        string proposalPath = Path.Combine(root, "proposal.json");
        string expectedOutput = Path.Combine(root, "assignment.output.nif");
        string expectedManifest = Path.Combine(
            root,
            "assignment.output.manifest.json");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        var handler = CreateHandler(codec, new RecordingPreviewService(),
            out StringWriter output, out StringWriter error);

        CommandExitCode analyzeExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze",
                "--source", source,
                "--expected-source-sha256", sourceHash,
                "--analysis", analysisPath,
                "--assignment-template", requestPath,
                "--json"
            ]),
            CancellationToken.None);
        using (JsonDocument analyzeJson =
               JsonDocument.Parse(output.ToString()))
        {
            Require(
                analyzeExit == CommandExitCode.Success &&
                File.Exists(analysisPath) &&
                File.Exists(requestPath) &&
                analyzeJson.RootElement.GetProperty("phaseVerdict")
                    .GetString() == "PASS" &&
                analyzeJson.RootElement.GetProperty("artifacts")
                    .GetArrayLength() == 4 &&
                analyzeJson.RootElement.GetProperty("artifacts")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("path").GetString() ==
                        expectedOutput) &&
                analyzeJson.RootElement.GetProperty("artifacts")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("path").GetString() ==
                        expectedManifest),
                "Analyze did not atomically write canonical documents and report deterministic suggested paths.");
        }

        byte[] analysisBytes = await File.ReadAllBytesAsync(analysisPath);
        byte[] requestBytes = await File.ReadAllBytesAsync(requestPath);
        Sha256Hash analysisHash = Sha(analysisBytes);
        Sha256Hash templateHash = Sha(requestBytes);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis> analysis =
            await codec.LoadAnalysisAsync(
                new WorkspacePath(analysisPath),
                analysisHash,
                CancellationToken.None);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> template =
            await codec.LoadRequestAsync(
                new WorkspacePath(requestPath),
                templateHash,
                CancellationToken.None);
        string primary = analysis.Value.Regions[0].SharedShaderGroupId;
        string accent = analysis.Value.Regions
            .Select(item => item.SharedShaderGroupId)
            .First(item => item != primary);
        FaceGeomHairRegionsRequest completed = template.Value with
        {
            PrimaryColor = "#D6BE83",
            AccentColor = "#F4E3B2",
            Assignments = template.Value.Assignments.Select(assignment =>
            {
                FaceGeomHairRegionsRegion region =
                    analysis.Value.Regions.Single(item =>
                        item.StructuralId == assignment.StructuralId);
                return assignment with
                {
                    Role = region.SharedShaderGroupId == primary
                        ? FaceGeomHairRegionRole.Primary
                        : region.SharedShaderGroupId == accent
                            ? FaceGeomHairRegionRole.Accent
                            : FaceGeomHairRegionRole.Preserve
                };
            }).ToImmutableArray()
        };
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            completedDocument = codec.BindRequest(completed);
        await File.WriteAllBytesAsync(
            requestPath,
            completedDocument.Utf8Json.ToArray());

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode wrongHashExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "propose",
                "--analysis", analysisPath,
                "--analysis-sha256", Hash('f').Value,
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            wrongHashExit == CommandExitCode.ValidationFailure &&
            JsonDocument.Parse(output.ToString()).RootElement
                .GetProperty("diagnostics")[0]
                .GetProperty("code").GetString() ==
                "facegeom-hair-regions-document-hash-mismatch",
            "Propose did not map a wrong CLI document hash to the stable validation diagnostic.");

        output.GetStringBuilder().Clear();
        CommandExitCode proposalOutputCollisionExit =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "facegen", "hair-regions", "propose",
                    "--analysis", analysisPath,
                    "--analysis-sha256", analysisHash.Value,
                    "--request", requestPath,
                    "--request-sha256",
                    completedDocument.Sha256.Value,
                    "--proposal", expectedOutput,
                    "--json"
                ]),
                CancellationToken.None);
        Require(
            proposalOutputCollisionExit ==
                CommandExitCode.ValidationFailure &&
            !File.Exists(expectedOutput),
            "Propose allowed its JSON destination to collide with the request's future NIF output.");

        output.GetStringBuilder().Clear();
        CommandExitCode proposeExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "propose",
                "--analysis", analysisPath,
                "--analysis-sha256", analysisHash.Value,
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            proposeExit == CommandExitCode.Success &&
            File.Exists(proposalPath),
            "Propose did not write the canonical proposal.");
        Sha256Hash proposalHash =
            Sha(await File.ReadAllBytesAsync(proposalPath));

        output.GetStringBuilder().Clear();
        CommandExitCode mismatchExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "apply",
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposalHash.Value,
                "--output", Path.Combine(root, "wrong.nif"),
                "--manifest", expectedManifest,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            mismatchExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "facegeom-hair-regions-output-mismatch",
                StringComparison.Ordinal),
            "Apply accepted an output path that differed from the request/proposal.");

        output.GetStringBuilder().Clear();
        CommandExitCode collisionExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "analyze",
                "--source", source,
                "--expected-source-sha256", sourceHash,
                "--analysis", analysisPath,
                "--assignment-template", requestPath,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            collisionExit == CommandExitCode.ValidationFailure &&
            output.ToString().Contains(
                "facegeom-hair-regions-output-exists",
                StringComparison.Ordinal),
            "Analyze did not refuse a no-overwrite collision.");

        output.GetStringBuilder().Clear();
        CommandExitCode applyExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "apply",
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposalHash.Value,
                "--output", expectedOutput,
                "--manifest", expectedManifest,
                "--json"
            ]),
            CancellationToken.None);
        Require(
            applyExit == CommandExitCode.Success &&
            File.Exists(expectedOutput) &&
            File.Exists(expectedManifest),
            "Apply did not write and verify the exact proposed artifacts.");

        output.GetStringBuilder().Clear();
        CommandExitCode verifyExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "verify",
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposalHash.Value,
                "--output", expectedOutput,
                "--manifest", expectedManifest,
                "--json"
            ]),
            CancellationToken.None);
        using JsonDocument verifyJson =
            JsonDocument.Parse(output.ToString());
        Require(
            verifyExit == CommandExitCode.Success &&
            verifyJson.RootElement.GetProperty("phaseVerdict")
                .GetString() == "PASS" &&
            verifyJson.RootElement.GetProperty("artifacts")
                .GetArrayLength() == 4 &&
            verifyJson.RootElement.GetProperty("visualAuthority")
                .ValueKind == JsonValueKind.False &&
            verifyJson.RootElement.GetProperty("runtimeAuthority")
                .ValueKind == JsonValueKind.False &&
            error.ToString().Length == 0,
            "Verify did not emit the complete read-only proof envelope.");

        byte[] observedCorruption = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(
            expectedOutput,
            observedCorruption);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            observedProposal = await codec.LoadProposalAsync(
                new WorkspacePath(proposalPath),
                proposalHash,
                CancellationToken.None);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
            observedManifest = await codec.LoadManifestAsync(
                new WorkspacePath(expectedManifest),
                CancellationToken.None);
        var observedApplier =
            new FaceGeomHairRegionsApplyService(
                ActorwrightWorkspace.ResolveRoot(),
                new BethesdaFaceGeomHairRegionsVerifier(),
                codec);
        FaceGeomHairRegionsVerification observedVerification =
            await observedApplier.VerifyAsync(
                completedDocument,
                observedProposal,
                observedManifest,
                CancellationToken.None);
        Require(
            !observedVerification.Succeeded &&
            observedVerification.ObservedOutput is
            {
                ByteLength: 5
            } observedAuthority &&
            observedAuthority.Sha256 ==
                Sha(observedCorruption),
            "Verify did not return the exact observed output authority derived from the bytes it verified.");
        output.GetStringBuilder().Clear();
        CommandExitCode observedExit = await handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "verify",
                "--request", requestPath,
                "--request-sha256", completedDocument.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposalHash.Value,
                "--output", expectedOutput,
                "--manifest", expectedManifest,
                "--json"
            ]),
            CancellationToken.None);
        using JsonDocument observed =
            JsonDocument.Parse(output.ToString());
        JsonElement observedOutput = observed.RootElement
            .GetProperty("artifacts")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("role").GetString() == "output");
        Require(
            observedExit == CommandExitCode.ValidationFailure &&
            observedOutput.GetProperty("byteLength").GetInt64() ==
                observedCorruption.LongLength &&
            observedOutput.GetProperty("sha256").GetString() ==
                Sha(observedCorruption).Value,
            "Failed Verify reported proposal-expected output evidence instead of observed readable bytes. " +
            $"exit={observedExit}; length={observedOutput.GetProperty("byteLength").GetInt64()}; " +
            $"hash={observedOutput.GetProperty("sha256").GetString()}; expected={Sha(observedCorruption).Value}");
    }

    public static async Task TestPreviewSessionLifecycle()
    {
        string root = NewRoot("preview-session-lifecycle");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(
                Path.Combine(root, "transaction"),
                codec);
        ReviewedGameIntake intake =
            await CreateReviewedIntakeFixtureAsync(root);
        ReviewedGameIntakeDocumentAuthority intakeAuthority =
            codec.BindReviewedIntake(
                intake,
                new WorkspacePath(
                    Path.Combine(root, "intake.json")));
        FaceGeomHairRegionsProposalMaterialization
            materialization =
                await new FaceGeomHairRegionsApplyService(
                        new WorkspacePath(
                            ActorwrightWorkspace.ResolveRoot().Value),
                        new BethesdaFaceGeomHairRegionsVerifier(),
                        codec)
                    .MaterializeAsync(
                        transaction.Request,
                        transaction.Proposal,
                        CancellationToken.None);

        FaceGeomHairRegionsPreviewRequest RequestFor(
            string outputRoot) =>
            new(
                transaction.Request,
                transaction.Proposal,
                materialization,
                intakeAuthority,
                new WorkspacePath(outputRoot));

        static bool IsEmpty(string path) =>
            Directory.Exists(path) &&
            !Directory.EnumerateFileSystemEntries(path).Any();

        string successWork =
            Path.Combine(root, "success-work");
        string successOutput =
            Path.Combine(root, "success-output");
        using FaceGeomHairRegionsPinnedDirectory
            successOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(successOutput),
                    "session-lifecycle success output");
        var successSource =
            new SessionLifecycleSourceService(
                composed: true);
        var successRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.Success);
        var successValidator =
            new SessionLifecycleVisualValidator(
                successSource);
        var successService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(successWork),
                successSource,
                successRenderer,
                successValidator,
                codec,
                createOwnedWorkRoot: true);
        FaceGeomHairRegionsPreviewResult success =
            await successService.PreviewAsync(
                RequestFor(successOutput),
                successOutputLease,
                CancellationToken.None);
        Require(
            success.Succeeded &&
            successSource.CandidateWasLiveDuringComposition &&
            successRenderer.SourceWasLiveDuringRender &&
            successValidator.SourceWasLiveDuringProof &&
            successSource.SessionRoot is { } successSession &&
            !Directory.Exists(successSession.Value) &&
            IsEmpty(successWork),
            "A successful preview did not retain its candidate and materialized textures through proof or clean its service-owned session before Dispose.");
        successService.Dispose();
        Require(
            !Directory.Exists(successWork),
            "Disposing the clean service-owned work root did not remove it.");

        string sourceRefusalWork =
            Path.Combine(root, "source-refusal-work");
        Directory.CreateDirectory(sourceRefusalWork);
        string sourceRefusalOutput =
            Path.Combine(root, "source-refusal-output");
        using FaceGeomHairRegionsPinnedDirectory
            sourceRefusalOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(sourceRefusalOutput),
                    "session-lifecycle source-refusal output");
        var refusingSource =
            new SessionLifecycleSourceService(
                composed: false);
        var sourceRefusalRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.Success);
        var sourceRefusalService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(sourceRefusalWork),
                refusingSource,
                sourceRefusalRenderer,
                new SessionLifecycleVisualValidator(
                    refusingSource),
                codec);
        FaceGeomHairRegionsPreviewResult sourceRefusal =
            await sourceRefusalService.PreviewAsync(
                RequestFor(sourceRefusalOutput),
                sourceRefusalOutputLease,
                CancellationToken.None);
        Require(
            !sourceRefusal.Succeeded &&
            refusingSource.CandidateWasLiveDuringComposition &&
            !sourceRefusalRenderer.Called &&
            refusingSource.SessionRoot is
                { } sourceRefusalSession &&
            !Directory.Exists(sourceRefusalSession.Value) &&
            IsEmpty(sourceRefusalWork),
            "Source refusal retained a caller-owned preview session or invoked the renderer.");
        sourceRefusalService.Dispose();
        Require(
            IsEmpty(sourceRefusalWork),
            "Disposing a caller-owned source-refusal work root changed its clean ownership boundary.");

        string rendererRefusalWork =
            Path.Combine(root, "renderer-refusal-work");
        Directory.CreateDirectory(rendererRefusalWork);
        string rendererRefusalOutput =
            Path.Combine(root, "renderer-refusal-output");
        using FaceGeomHairRegionsPinnedDirectory
            rendererRefusalOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(rendererRefusalOutput),
                    "session-lifecycle renderer-refusal output");
        var rendererRefusalSource =
            new SessionLifecycleSourceService(
                composed: true);
        var refusingRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.Refusal);
        var rendererRefusalService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(rendererRefusalWork),
                rendererRefusalSource,
                refusingRenderer,
                new SessionLifecycleVisualValidator(
                    rendererRefusalSource),
                codec);
        FaceGeomHairRegionsPreviewResult rendererRefusal =
            await rendererRefusalService.PreviewAsync(
                RequestFor(rendererRefusalOutput),
                rendererRefusalOutputLease,
                CancellationToken.None);
        Require(
            !rendererRefusal.Succeeded &&
            refusingRenderer.SourceWasLiveDuringRender &&
            rendererRefusalSource.SessionRoot is
                { } rendererRefusalSession &&
            !Directory.Exists(
                rendererRefusalSession.Value) &&
            IsEmpty(rendererRefusalWork),
            "Renderer refusal did not clean its caller-owned preview session.");
        rendererRefusalService.Dispose();

        string cancellationWork =
            Path.Combine(root, "cancellation-work");
        Directory.CreateDirectory(cancellationWork);
        string cancellationOutput =
            Path.Combine(root, "cancellation-output");
        string cancellationSurvivor =
            Path.Combine(root, "renderer-cancellation-survivor.bin");
        await File.WriteAllBytesAsync(
            cancellationSurvivor,
            [1, 2, 3, 4]);
        using FaceGeomHairRegionsPinnedDirectory
            cancellationOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(cancellationOutput),
                    "session-lifecycle cancellation output");
        var cancellationSource =
            new SessionLifecycleSourceService(
                composed: true);
        var cancellationRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.Cancellation,
                new WorkspacePath(cancellationSurvivor));
        var cancellationService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(cancellationWork),
                cancellationSource,
                cancellationRenderer,
                new SessionLifecycleVisualValidator(
                    cancellationSource),
                codec);
        try
        {
            await cancellationService.PreviewAsync(
                RequestFor(cancellationOutput),
                cancellationOutputLease,
                CancellationToken.None);
            throw new InvalidOperationException(
                "Typed renderer cancellation unexpectedly returned.");
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            Require(
                exception.SurvivingArtifacts.SequenceEqual(
                    [
                        new WorkspacePath(
                            cancellationSurvivor)
                    ]) &&
                exception.SurvivingProcessIds.SequenceEqual(
                    [5151]) &&
                exception.ProcessTerminationFailure ==
                    SessionLifecycleRenderer
                        .TerminationFailure &&
                cancellationRenderer.SourceWasLiveDuringRender &&
                cancellationSource.SessionRoot is
                    { } cancellationSession &&
                !Directory.Exists(
                    cancellationSession.Value) &&
                IsEmpty(cancellationWork),
                "Cancellation did not merge lower typed survivor/process evidence after exact session cleanup.");
        }
        cancellationService.Dispose();

        string operationalWork =
            Path.Combine(root, "operational-work");
        Directory.CreateDirectory(operationalWork);
        string operationalOutput =
            Path.Combine(root, "operational-output");
        string operationalSurvivor =
            Path.Combine(root, "renderer-operational-survivor.bin");
        await File.WriteAllBytesAsync(
            operationalSurvivor,
            [5, 6, 7, 8]);
        using FaceGeomHairRegionsPinnedDirectory
            operationalOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(operationalOutput),
                    "session-lifecycle operational output");
        var operationalSource =
            new SessionLifecycleSourceService(
                composed: true);
        var operationalRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.OperationalFailure,
                new WorkspacePath(operationalSurvivor));
        var operationalService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(operationalWork),
                operationalSource,
                operationalRenderer,
                new SessionLifecycleVisualValidator(
                    operationalSource),
                codec);
        try
        {
            await operationalService.PreviewAsync(
                RequestFor(operationalOutput),
                operationalOutputLease,
                CancellationToken.None);
            throw new InvalidOperationException(
                "Typed renderer operational failure unexpectedly returned.");
        }
        catch (FaceGeomHairRegionsOperationalException exception)
        {
            Require(
                exception.SurvivingArtifacts.SequenceEqual(
                    [
                        new WorkspacePath(
                            operationalSurvivor)
                    ]) &&
                exception.SurvivingProcessIds.SequenceEqual(
                    [5252]) &&
                exception.ProcessTerminationFailure ==
                    SessionLifecycleRenderer
                        .TerminationFailure &&
                operationalRenderer.SourceWasLiveDuringRender &&
                operationalSource.SessionRoot is
                    { } operationalSession &&
                !Directory.Exists(
                    operationalSession.Value) &&
                IsEmpty(operationalWork),
                "Operational failure did not merge lower typed survivor/process evidence after exact session cleanup.");
        }
        operationalService.Dispose();

        string cleanupWork =
            Path.Combine(root, "cleanup-refusal-work");
        Directory.CreateDirectory(cleanupWork);
        string cleanupOutput =
            Path.Combine(root, "cleanup-refusal-output");
        var cleanupHooks =
            new PreviewSessionCleanupRefusingHooks();
        var cleanupCodec =
            new FaceGeomHairRegionsDocumentCodec(
                new FaceGeomHairRegionsWorkspaceBoundary(
                    new WorkspacePath(
                        ActorwrightWorkspace.ResolveRoot().Value),
                    cleanupHooks));
        using FaceGeomHairRegionsPinnedDirectory
            cleanupOutputLease =
                cleanupCodec.WorkspaceBoundary
                    .CreateOwnedDirectory(
                        new WorkspacePath(cleanupOutput),
                        "session-lifecycle cleanup-refusal output");
        var cleanupSource =
            new SessionLifecycleSourceService(
                composed: true);
        var cleanupRenderer =
            new SessionLifecycleRenderer(
                SessionRendererOutcome.Refusal);
        var cleanupService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(cleanupWork),
                cleanupSource,
                cleanupRenderer,
                new SessionLifecycleVisualValidator(
                    cleanupSource),
                cleanupCodec);
        try
        {
            await cleanupService.PreviewAsync(
                RequestFor(cleanupOutput),
                cleanupOutputLease,
                CancellationToken.None);
            throw new InvalidOperationException(
                "A preview session cleanup refusal returned an ordinary renderer refusal.");
        }
        catch (FaceGeomHairRegionsOperationalException exception)
        {
            string? refusedSession =
                cleanupHooks.SessionRoot;
            Require(
                refusedSession is not null &&
                exception.SurvivingArtifacts.SequenceEqual(
                    [new WorkspacePath(refusedSession)]) &&
                Directory.Exists(refusedSession) &&
                !Directory.EnumerateFileSystemEntries(
                        refusedSession)
                    .Any() &&
                Directory.EnumerateFileSystemEntries(
                        cleanupWork)
                    .SequenceEqual(
                        [refusedSession],
                        StringComparer.OrdinalIgnoreCase),
                "Session cleanup refusal did not fail closed with the exact empty session-root survivor.");
            Directory.Delete(refusedSession!);
        }
        cleanupService.Dispose();
        Require(
            IsEmpty(cleanupWork),
            "Manual recovery from the exact cleanup survivor did not restore a clean caller-owned work root.");
    }

    public static async Task TestRunnerDispatchAndFailClosedPreview()
    {
        string root = NewRoot("runner-preview");
        var codec = new FaceGeomHairRegionsDocumentCodec();
        PreparedTransaction transaction =
            await PrepareAuthenticTransactionAsync(root, codec);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> request =
            transaction.Request;
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal> proposal =
            transaction.Proposal;
        string requestPath = transaction.RequestPath;
        string proposalPath = transaction.ProposalPath;
        string intakePath = Path.Combine(root, "intake.json");
        ReviewedGameIntake reviewed =
            await CreateReviewedIntakeFixtureAsync(root);
        ReviewedGameIntakeDocumentAuthority intakeAuthority =
            codec.BindReviewedIntake(
                reviewed,
                new WorkspacePath(intakePath));
        await File.WriteAllBytesAsync(
            intakePath,
            intakeAuthority.Document.Utf8Json.ToArray());
        var fake = new RecordingPreviewService();
        (CliRunner runner, StringWriter output, StringWriter error) =
            Program.CreateRunnerForFaceGeomHairRegionsTests(
                fake,
                new AcceptedPreviewVisualValidator());
        CommandExitCode exit = await runner.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "preview",
                "--request", requestPath,
                "--request-sha256", request.Sha256.Value,
                "--proposal", proposalPath,
                "--proposal-sha256", proposal.Sha256.Value,
                "--intake", intakePath,
                "--output-root", Path.Combine(root, "preview"),
                "--json"
            ]),
            CancellationToken.None);
        Require(
            exit == CommandExitCode.Success &&
            fake.Request is not null &&
            output.ToString().Contains(
                "\"phaseVerdict\": \"PASS\"",
                StringComparison.Ordinal) &&
            error.ToString().Length == 0,
            "CliRunner did not dispatch preview through the typed service bundle.");

        var failClosed =
            new UnconfiguredFaceGeomHairRegionsPreviewService();
        FaceGeomHairRegionsPreviewResult refusal =
            await failClosed.PreviewAsync(
                fake.Request!,
                CancellationToken.None);
        Require(
            !refusal.Succeeded &&
            refusal.Diagnostics.Single().Code ==
                "preview-not-configured",
            "Production preview did not fail closed with its stable Task 2 diagnostic.");

        string serviceOutputRoot =
            Path.Combine(root, "real-service-preview");
        Directory.CreateDirectory(serviceOutputRoot);
        string serviceWorkRoot =
            Path.Combine(root, "real-service-work");
        Directory.CreateDirectory(serviceWorkRoot);
        var recordingSource =
            new RecordingHairRegionsSourceService();
        var recordingRenderer =
            new RecordingHairRegionsRenderer();
        var realService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(serviceWorkRoot),
                recordingSource,
                recordingRenderer);
        FaceGeomHairRegionsPreviewRequest serviceRequest =
            fake.Request! with
            {
                OutputRoot =
                    new WorkspacePath(serviceOutputRoot)
            };
        FaceGeomHairRegionsPreviewResult serviceResult =
            await realService.PreviewAsync(
                serviceRequest,
                CancellationToken.None);
        Require(
            !serviceResult.Succeeded &&
            recordingSource.Request is not null &&
            recordingRenderer.Request is not null &&
            recordingSource.Request.Candidate.Path.IsUnder(
                new WorkspacePath(serviceWorkRoot)) &&
            recordingSource.CandidateWasLiveDuringComposition &&
            !File.Exists(
                recordingSource.Request.Candidate.Path.Value) &&
            !Directory.Exists(
                recordingSource.Request.OutputDataRoot.Value) &&
            !Directory.EnumerateFileSystemEntries(
                    serviceWorkRoot)
                .Any() &&
            recordingRenderer.Request.CandidateBytes
                .SequenceEqual(
                    serviceRequest.Materialization
                        .CandidateBytes) &&
            recordingRenderer.Request.Source.Candidate.Sha256 ==
                serviceRequest.Materialization.Candidate
                    .Sha256 &&
            recordingRenderer.Request.Source
                .TextureFingerprintSha256 ==
                RecordingHairRegionsSourceService
                    .TextureFingerprint &&
            !File.Exists(
                serviceRequest.RequestDocument.Value
                    .Output.Value) &&
            !File.Exists(
                serviceRequest.RequestDocument.Value
                    .Manifest.Value),
            "The real preview service did not hand the exact in-memory proposal candidate to its renderer and then clean its caller-owned session without writing final outputs.");
        realService.Dispose();

        string proofOutputRoot =
            Path.Combine(root, "closed-service-preview");
        string proofWorkRoot =
            Path.Combine(root, "closed-service-work");
        using FaceGeomHairRegionsPinnedDirectory
            proofOutputLease =
                codec.WorkspaceBoundary.CreateOwnedDirectory(
                    new WorkspacePath(proofOutputRoot),
                    "closed preview proof output");
        var proofSource =
            new RecordingHairRegionsSourceService();
        var proofRenderer =
            new ProvingHairRegionsRenderer();
        var proofValidator =
            new ProvingHairRegionsVisualValidator();
        var proofService =
            new FaceGeomHairRegionsPreviewService(
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(proofWorkRoot),
                proofSource,
                proofRenderer,
                proofValidator,
                codec,
                createOwnedWorkRoot: true);
        FaceGeomHairRegionsPreviewResult proofResult =
            await proofService.PreviewAsync(
                serviceRequest with
                {
                    OutputRoot =
                        new WorkspacePath(proofOutputRoot)
                },
                proofOutputLease,
                CancellationToken.None);
        Require(
            proofResult.Succeeded &&
            proofResult.Evidence is
            {
                DetectedFaceCount: 1,
                LandmarkCount: 478,
                SemanticAnchorCount: 31
            } &&
            !proofResult.VisualAuthority &&
            !proofResult.RuntimeAuthority &&
            proofResult.Artifacts.Length ==
                3 +
                serviceRequest.RequestDocument.Value
                    .Assignments.Length * 2 &&
            proofResult.Artifacts.Count(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .EvidenceDocument) == 1 &&
            proofValidator.EncodedValidationCount == 1 &&
            proofRenderer.ProcessCount == 1 &&
            Directory.Exists(proofWorkRoot) &&
            !Directory.EnumerateFileSystemEntries(
                    proofWorkRoot)
                .Any(),
            "The real preview service did not independently close renderer bytes into authority-false evidence: " +
            string.Join(
                "; ",
                proofResult.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        proofService.Dispose();
        Require(
            !Directory.Exists(proofWorkRoot),
            "Retained-handle preview cleanup left its owned work tree.");

        (CliRunner productionRunner, StringWriter productionOutput, _) =
            Program.CreateRunnerForFaceGeomHairRegionsTests(null);
        CommandExitCode productionExit =
            await productionRunner.RunAsync(
                CommandLine.Parse(
                [
                    "facegen", "hair-regions", "preview",
                    "--request", requestPath,
                    "--request-sha256", request.Sha256.Value,
                    "--proposal", proposalPath,
                    "--proposal-sha256", proposal.Sha256.Value,
                    "--intake", intakePath,
                    "--output-root", Path.Combine(root, "production-preview"),
                    "--json"
                ]),
                CancellationToken.None);
        Require(
            !productionOutput.ToString().Contains(
                "\"code\": \"preview-not-configured\"",
                StringComparison.Ordinal),
            "Production CliRunner still composes the Task 2 preview-not-configured placeholder. " +
            $"exit={productionExit}; output={productionOutput}");
    }

    public static async Task TestDiscoveryHelpAndSchema()
    {
        string projectRoot = FindProjectRoot();
        string compositionSource = await File.ReadAllTextAsync(
            Path.Combine(
                projectRoot,
                "src",
                "NpcManager.Cli",
                "FaceGeomHairRegionsPreviewCliComposition.cs"));
        string renderingProject = await File.ReadAllTextAsync(
            Path.Combine(
                projectRoot,
                "src",
                "NpcManager.Rendering",
                "NpcManager.Rendering.csproj"));
        Require(
            !compositionSource.Contains(
                "Directory.GetCurrentDirectory",
                StringComparison.Ordinal) &&
            compositionSource.Contains(
                "AppContext.BaseDirectory",
                StringComparison.Ordinal) &&
            renderingProject.Contains(
                "render_npc_preview_bundle.py",
                StringComparison.Ordinal) &&
            renderingProject.Contains(
                "EmbeddedResource",
                StringComparison.Ordinal),
            "Hair preview renderer discovery is not packaged and CWD-independent.");

        (CliRunner runner, StringWriter output, StringWriter error) =
            Program.CreateRunnerForFaceGeomHairRegionsTests(null);
        CommandExitCode capabilitiesExit = await runner.RunAsync(
            CommandLine.Parse(["capabilities", "--json"]),
            CancellationToken.None);
        using JsonDocument capabilities =
            JsonDocument.Parse(output.ToString());
        JsonElement.ArrayEnumerator commandRows =
            capabilities.RootElement.GetProperty("commands")
                .EnumerateArray();
        string[] builtNames = commandRows
            .Select(row => row.GetProperty("name").GetString()!)
            .ToArray();
        JsonElement previewCapability =
            capabilities.RootElement.GetProperty("commands")
                .EnumerateArray()
                .Single(row =>
                    row.GetProperty("name").GetString() ==
                    "facegen hair-regions preview");
        string previewCapabilityDetail =
            previewCapability.GetProperty("limitations")[0]
                .GetString()!;
        JsonElement pairAnalyzeCapability =
            capabilities.RootElement.GetProperty("commands")
                .EnumerateArray()
                .Single(row =>
                    row.GetProperty("name").GetString() ==
                    "npc follower-finish pair-analyze");
        JsonElement pairApplyCapability =
            capabilities.RootElement.GetProperty("commands")
                .EnumerateArray()
                .Single(row =>
                    row.GetProperty("name").GetString() ==
                    "npc follower-finish pair-apply");
        JsonElement pairVerifyCapability =
            capabilities.RootElement.GetProperty("commands")
                .EnumerateArray()
                .Single(row =>
                    row.GetProperty("name").GetString() ==
                    "npc follower-finish pair-verify");
        Require(
            capabilitiesExit == CommandExitCode.Success &&
            builtNames.Length == CommandCatalog.All.Length &&
            builtNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == builtNames.Length &&
            Names.All(name => builtNames.Count(item =>
                item == name) == 1) &&
            previewCapabilityDetail.Contains(
                "packaged pinned Blender/PyNifly renderer",
                StringComparison.Ordinal) &&
            previewCapabilityDetail.Contains(
                "complete verified private bundle",
                StringComparison.Ordinal) &&
            !previewCapabilityDetail.Contains(
                "Task 2",
                StringComparison.Ordinal) &&
            !previewCapabilityDetail.Contains(
                "not configured",
                StringComparison.OrdinalIgnoreCase) &&
            pairAnalyzeCapability.GetProperty(
                    "description").GetString() ==
                "Analyze one hash-bound schema-3 paired Skyrim follower finish request." &&
            pairAnalyzeCapability.GetProperty(
                    "schemaVersion").GetString() == "3" &&
            pairAnalyzeCapability.GetProperty(
                    "limitations")[0].GetString() ==
                "Current schema 3 reviews bounded companion FaceGeom HairTint and private outfit/inventory finish plus the accepted subject/provider route, preserves companion fields outside that reviewed slice, and writes only a proposal; legacy schema 2 remains accepted for compatibility." &&
            pairApplyCapability.GetProperty(
                    "description").GetString() ==
                "Apply one reviewed schema-3 paired follower finish proposal." &&
            pairApplyCapability.GetProperty(
                    "schemaVersion").GetString() == "3" &&
            pairApplyCapability.GetProperty(
                    "limitations")[0].GetString() ==
                "Applies only the reviewed subject records and bounded companion FaceGeom HairTint/private outfit-inventory fields, preserves companion fields outside that reviewed slice, and writes one deterministic two-plugin package; legacy schema 2 remains accepted for compatibility and runtime authority remains false." &&
            pairVerifyCapability.GetProperty(
                    "description").GetString() ==
                "Reopen and verify one schema-3 paired follower package." &&
            pairVerifyCapability.GetProperty(
                    "schemaVersion").GetString() == "3" &&
            pairVerifyCapability.GetProperty(
                    "limitations")[0].GetString() ==
                "Read-only static verification covers both actor payloads, bounded companion FaceGeom HairTint/private outfit-inventory fields, subject plugin semantics, package inventory, and archive hashes; legacy schema 2 remains accepted for compatibility and runtime authority remains false.",
            "Built capabilities did not expose the real packaged preview descriptor and exact current/legacy paired-follower contracts.");

        output.GetStringBuilder().Clear();
        CommandExitCode helpExit = await runner.RunAsync(
            CommandLine.Parse(["--help"]),
            CancellationToken.None);
        string help = output.ToString();
        Require(
            helpExit == CommandExitCode.Success &&
            Names.All(name => help.Contains(
                name,
                StringComparison.Ordinal)) &&
            help.Contains(
                "current hash-bound schema 3",
                StringComparison.Ordinal) &&
            help.Contains(
                "bounded reviewed companion FaceGeom HairTint and private outfit/inventory finish",
                StringComparison.Ordinal) &&
            help.Contains(
                "legacy schema 2 remains accepted for compatibility",
                StringComparison.Ordinal) &&
            help.Contains(
                "load-order-bound materialized texture providers",
                StringComparison.Ordinal) &&
            help.Contains(
                "packaged pinned Blender/PyNifly renderer",
                StringComparison.Ordinal) &&
            help.Contains(
                "complete verified private bundle",
                StringComparison.Ordinal) &&
            !help.Contains(
                "Task 2 production returns preview-not-configured until Task 3 supplies it",
                StringComparison.Ordinal) &&
            !help.Contains(
                "preview-not-configured",
                StringComparison.Ordinal),
            "CLI help omitted or misstated the current pair schema or real packaged HairTint preview.");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        string schemaPath = Path.Combine(
            NewRoot("schema"),
            "hair-regions.schema.json");
        CommandExitCode schemaExit = await runner.RunAsync(
            CommandLine.Parse(
            [
                "schema", "export",
                "--command", "facegen hair-regions analyze",
                "--output", schemaPath,
                "--json"
            ]),
            CancellationToken.None);
        string schemaError = error.ToString();
        Require(
            schemaExit == CommandExitCode.Success &&
            schemaError.Length == 0,
            $"Schema export failed before writing output (exit={schemaExit}): {schemaError}");
        using JsonDocument schema =
            JsonDocument.Parse(await File.ReadAllTextAsync(schemaPath));
        JsonElement schemaCommand =
            schema.RootElement.GetProperty("commands")[0];
        Require(
            schemaCommand.GetProperty("name").GetString() ==
                "facegen hair-regions analyze" &&
            schemaCommand.GetProperty("mutates").GetBoolean() &&
            schema.RootElement.GetProperty("exitCodes")
                .GetArrayLength() ==
                Enum.GetValues<CommandExitCode>().Length,
            "Schema export did not expose the exact cataloged command and existing exit-code contract.");
    }

    private static async Task AssertInvalidAsync(
        FaceGeomHairRegionsDocumentCodec codec,
        string root,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest> canonical,
        string name,
        Func<string, string> mutate)
    {
        string path = Path.Combine(root, name + ".json");
        byte[] bytes = Encoding.UTF8.GetBytes(
            mutate(Encoding.UTF8.GetString(
                canonical.Utf8Json.AsSpan())));
        await File.WriteAllBytesAsync(path, bytes);
        bool refused = false;
        try
        {
            await codec.LoadRequestAsync(
                new WorkspacePath(path),
                new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(bytes))),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Require(refused, $"Strict loader accepted {name} JSON.");
    }

    private static FaceGeomHairRegionsCommandHandler CreateHandler(
        FaceGeomHairRegionsDocumentCodec codec,
        IFaceGeomHairRegionsPreviewService preview,
        out StringWriter output,
        out StringWriter error,
        INpcVisualPreviewVisualValidator? visualValidator = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        var workspace = ActorwrightWorkspace.ResolveRoot();
        var analyzer = new FaceGeomHairRegionsAnalyzer(
            workspace,
            codec.WorkspaceBoundary);
        var proposer = new FaceGeomHairRegionsProposer(
            workspace,
            codec);
        var applier = new FaceGeomHairRegionsApplyService(
            workspace,
            new BethesdaFaceGeomHairRegionsVerifier(),
            codec);
        return new FaceGeomHairRegionsCommandHandler(
            workspace,
            analyzer,
            proposer,
            applier,
            codec,
            preview,
            visualValidator ??
                new AcceptedPreviewVisualValidator(),
            output,
            error);
    }

    private static async Task<PreparedTransaction>
        PrepareAuthenticTransactionAsync(
            string root,
            FaceGeomHairRegionsDocumentCodec codec)
    {
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.nif");
        File.Copy(
            SyntheticFaceGeomHairRegionsFixture.Load(
                ActorwrightWorkspace.ResolveRoot().Value).Path,
            sourcePath);
        byte[] sourceBytes =
            await File.ReadAllBytesAsync(sourcePath);
        var workspace =
            ActorwrightWorkspace.ResolveRoot();
        var analyzer =
            new FaceGeomHairRegionsAnalyzer(workspace);
        FaceGeomHairRegionsAnalysis analysis =
            await analyzer.AnalyzeAsync(
                new WorkspacePath(sourcePath),
                Sha(sourceBytes),
                pluginColorContext: null,
                CancellationToken.None);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument = codec.BindAnalysis(analysis);
        string outputPath = Path.Combine(root, "result.nif");
        string manifestPath =
            Path.Combine(root, "result.manifest.json");
        FaceGeomHairRegionsRequest template =
            analyzer.CreateAssignmentTemplate(
                analysisDocument,
                new WorkspacePath(outputPath),
                new WorkspacePath(manifestPath));
        string primaryGroup =
            analysis.Regions[0].SharedShaderGroupId;
        string accentGroup = analysis.Regions
            .Select(item => item.SharedShaderGroupId)
            .First(item => !string.Equals(
                item,
                primaryGroup,
                StringComparison.Ordinal));
        FaceGeomHairRegionsRequest completed = template with
        {
            PrimaryColor = "#D6BE83",
            AccentColor = "#F4E3B2",
            Assignments = template.Assignments.Select(
                    assignment =>
                    {
                        FaceGeomHairRegionsRegion region =
                            analysis.Regions.Single(item =>
                                item.StructuralId ==
                                assignment.StructuralId);
                        return assignment with
                        {
                            Role = region.SharedShaderGroupId ==
                                primaryGroup
                                    ? FaceGeomHairRegionRole.Primary
                                    : region.SharedShaderGroupId ==
                                      accentGroup
                                        ? FaceGeomHairRegionRole.Accent
                                        : FaceGeomHairRegionRole.Preserve
                        };
                    })
                .ToImmutableArray()
        };
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument = codec.BindRequest(completed);
        var proposer = new FaceGeomHairRegionsProposer(
            workspace,
            codec);
        FaceGeomHairRegionsProposalResult proposal =
            await proposer.ProposeAsync(
                analysisDocument,
                requestDocument,
                CancellationToken.None);
        string requestPath = Path.Combine(root, "request.json");
        string proposalPath = Path.Combine(root, "proposal.json");
        await File.WriteAllBytesAsync(
            requestPath,
            requestDocument.Utf8Json.ToArray());
        await File.WriteAllBytesAsync(
            proposalPath,
            proposal.Proposal.Utf8Json.ToArray());
        return new PreparedTransaction(
            requestDocument,
            proposal.Proposal,
            requestPath,
            proposalPath);
    }

    private static async Task<ReviewedGameIntake>
        CreateReviewedIntakeFixtureAsync(string root)
    {
        string intakeRoot = Path.Combine(root, "reviewed-authority");
        string dataPath = Path.Combine(intakeRoot, "Data");
        Directory.CreateDirectory(dataPath);
        string pluginPath = Path.Combine(dataPath, "Skyrim.esm");
        byte[] pluginBytes = [1, 3, 3, 7];
        await File.WriteAllBytesAsync(pluginPath, pluginBytes);
        string loadOrderPath =
            Path.Combine(intakeRoot, "loadorder.txt");
        byte[] loadOrderBytes =
            Encoding.UTF8.GetBytes("Skyrim.esm\r\n");
        await File.WriteAllBytesAsync(
            loadOrderPath,
            loadOrderBytes);
        var plugin = new PluginName("Skyrim.esm");
        var provisional = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            ActorwrightWorkspace.ResolveRoot(),
            new WorkspacePath(dataPath),
            new WorkspacePath(loadOrderPath),
            new WorkspacePath(Path.Combine(
                intakeRoot,
                "fresh-output")),
            Sha(loadOrderBytes),
            [
                new PluginClosureReviewEntry(
                    plugin,
                    0,
                    true,
                    true,
                    true,
                    false,
                    true,
                    new WorkspacePath(pluginPath),
                    Sha(pluginBytes),
                    [])
            ],
            [],
            [],
            [],
            1,
            Hash('7'),
            Hash('9'),
            false);
        return provisional with
        {
            IntakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(provisional)
        };
    }

    private static async Task<ReviewedGameIntake>
        CreateRichReviewedIntakeFixtureAsync(string root)
    {
        string intakeRoot =
            Path.Combine(root, "rich-reviewed-authority");
        string dataPath =
            Path.Combine(intakeRoot, "Data");
        Directory.CreateDirectory(dataPath);
        byte[] masterBytes = [11, 12, 13, 14];
        byte[] pluginBytes = [21, 22, 23, 24];
        string masterPath =
            Path.Combine(dataPath, "RichMaster.esm");
        string pluginPath =
            Path.Combine(dataPath, "RichGenerated.esp");
        await File.WriteAllBytesAsync(
            masterPath,
            masterBytes);
        await File.WriteAllBytesAsync(
            pluginPath,
            pluginBytes);
        byte[] loadOrderBytes =
            Encoding.UTF8.GetBytes(
                "*RichMaster.esm\r\n" +
                "*RichGenerated.esp\r\n");
        string loadOrderPath =
            Path.Combine(intakeRoot, "loadorder.txt");
        await File.WriteAllBytesAsync(
            loadOrderPath,
            loadOrderBytes);
        byte[] sidecarBytes = [31, 32, 33, 34];
        string bodySidecarPath =
            Path.Combine(
                dataPath,
                "RichGenerated.bssliders");
        await File.WriteAllBytesAsync(
            bodySidecarPath,
            sidecarBytes);
        byte[] generatedFaceGeom =
            [41, 42, 43, 44, 45];
        string generatedFaceGeomPath =
            Path.Combine(
                dataPath,
                "meshes",
                "actors",
                "character",
                "FaceGenData",
                "FaceGeom",
                "RichGenerated.esp",
                "00000800.nif");
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                generatedFaceGeomPath)!);
        await File.WriteAllBytesAsync(
            generatedFaceGeomPath,
            generatedFaceGeom);
        var master =
            new PluginName("RichMaster.esm");
        var generated =
            new PluginName("RichGenerated.esp");
        var provisional =
            new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(
                    ActorwrightWorkspace.ResolveRoot().Value),
                new WorkspacePath(dataPath),
                new WorkspacePath(loadOrderPath),
                new WorkspacePath(
                    Path.Combine(
                        intakeRoot,
                        "fresh-output")),
                Sha(loadOrderBytes),
                [
                    new PluginClosureReviewEntry(
                        master,
                        2,
                        true,
                        true,
                        false,
                        true,
                        true,
                        new WorkspacePath(masterPath),
                        Sha(masterBytes),
                        []),
                    new PluginClosureReviewEntry(
                        generated,
                        7,
                        true,
                        true,
                        true,
                        false,
                        true,
                        new WorkspacePath(pluginPath),
                        Sha(pluginBytes),
                        [master])
                ],
                [
                    new ReviewedBodySidecar(
                        generated,
                        new WorkspacePath(
                            bodySidecarPath),
                        Sha(sidecarBytes),
                        Hash('b'),
                        2)
                ],
                [
                    new GeneratedPluginScanEntry(
                        generated,
                        new WorkspacePath(pluginPath),
                        GeneratedArtifactMarkers
                            .ReferenceAuthor,
                        Sha(pluginBytes),
                        true,
                        [
                            new FormId(0x800),
                            new FormId(0x801)
                        ])
                ],
                [
                    new GeneratedSidecarEntry(
                        generated,
                        GeneratedSidecarKind.FaceGeom,
                        GeneratedSidecarVariant.Canonical,
                        new AssetPath(
                            "meshes/actors/character/FaceGenData/FaceGeom/RichGenerated.esp/00000800.nif"),
                        new WorkspacePath(
                            generatedFaceGeomPath),
                        new FormId(0x800),
                        generatedFaceGeom.LongLength,
                        Sha(generatedFaceGeom))
                ],
                4,
                Hash('a'),
                Hash('0'),
                false);
        return provisional with
        {
            IntakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority
                    .Fingerprint(provisional)
        };
    }

    private static void RequireRichIntakeRoundTrip(
        ReviewedGameIntake expected,
        ReviewedGameIntake actual)
    {
        Require(
            actual.Plugins.Length ==
                expected.Plugins.Length &&
            actual.Plugins.Zip(
                    expected.Plugins)
                .All(pair =>
                    pair.First.Plugin ==
                        pair.Second.Plugin &&
                    pair.First.Order ==
                        pair.Second.Order &&
                    pair.First.Enabled ==
                        pair.Second.Enabled &&
                    pair.First.Requested ==
                        pair.Second.Requested &&
                    pair.First.RequiredMaster ==
                        pair.Second.RequiredMaster &&
                    pair.First.Path ==
                        pair.Second.Path &&
                    pair.First.SourceHash ==
                        pair.Second.SourceHash &&
                    pair.First.Masters.SequenceEqual(
                        pair.Second.Masters)),
            "Reviewed intake lost a gapped global plugin order or plugin authority field.");
        Require(
            actual.BodySidecars.SequenceEqual(
                expected.BodySidecars),
            "Reviewed intake lost exact body-sidecar authority.");
        Require(
            actual.GeneratedPlugins.Length == 1 &&
            expected.GeneratedPlugins.Length == 1 &&
            actual.GeneratedPlugins[0] with
                {
                    NpcFormIds = []
                } ==
            expected.GeneratedPlugins[0] with
                {
                    NpcFormIds = []
                } &&
            actual.GeneratedPlugins[0].NpcFormIds
                .SequenceEqual(
                    expected.GeneratedPlugins[0]
                        .NpcFormIds),
            "Reviewed intake lost exact generated-plugin authority.");
        Require(
            actual.GeneratedSidecars.SequenceEqual(
                expected.GeneratedSidecars),
            "Reviewed intake lost exact generated-sidecar authority.");
    }

    private static ValueTask<CommandExitCode> RunPreviewAsync(
        FaceGeomHairRegionsCommandHandler handler,
        PreparedTransaction transaction,
        string intakePath,
        string outputRoot) =>
        handler.RunAsync(
            CommandLine.Parse(
            [
                "facegen", "hair-regions", "preview",
                "--request", transaction.RequestPath,
                "--request-sha256",
                transaction.Request.Sha256.Value,
                "--proposal", transaction.ProposalPath,
                "--proposal-sha256",
                transaction.Proposal.Sha256.Value,
                "--intake", intakePath,
                "--output-root", outputRoot,
                "--json"
            ]),
            CancellationToken.None);

    private static void RequirePreviewBundleRolledBack(
        string outputRoot,
        string message)
    {
        string parent =
            Path.GetDirectoryName(outputRoot) ??
            throw new InvalidOperationException(
                "Preview rollback assertion requires a parent.");
        string privatePattern =
            $".{Path.GetFileName(outputRoot)}-preview-*.tmp";
        Require(
            !Directory.Exists(outputRoot) &&
            !File.Exists(outputRoot) &&
            !Directory.EnumerateFileSystemEntries(
                    parent,
                    privatePattern,
                    SearchOption.TopDirectoryOnly)
                .Any(),
            message);
    }

    private static string FindProjectRoot()
    {
        foreach (string start in
                 new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            DirectoryInfo? current = new(start);
            while (current is not null)
            {
                string candidate = Path.Combine(
                    current.FullName,
                    "projects",
                    "NpcManagerReimplementation");
                if (File.Exists(Path.Combine(
                        candidate,
                        "NpcManager.sln")))
                    return candidate;
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "NpcManager.sln")) &&
                    Directory.Exists(Path.Combine(
                        current.FullName,
                        "tests",
                        "fixtures")))
                    return current.FullName;
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "Actorwright.sln")) &&
                    Directory.Exists(Path.Combine(
                        current.FullName,
                        "tests",
                        "fixtures")))
                    return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            "NpcManagerReimplementation project root was not found.");
    }

    private static FaceGeomHairRegionsRequest Request(string root) =>
        new(
            FaceGeomHairRegionSchemas.Request,
            Hash('a'),
            new FaceGeomHairRegionsFile(
                new WorkspacePath(Path.Combine(root, "source.nif")),
                64,
                Hash('b')),
            "#D6BE83",
            "#F4E3B2",
            [
                new FaceGeomHairRegionAssignment(
                    "shape:0",
                    FaceGeomHairRegionRole.Preserve)
            ],
            new WorkspacePath(Path.Combine(root, "request.output.nif")),
            new WorkspacePath(
                Path.Combine(root, "request.output.manifest.json")));

    private static FaceGeomHairRegionsProposal Proposal(
        FaceGeomHairRegionsRequest request,
        Sha256Hash requestHash)
    {
        var fingerprints = new FaceGeomHairRegionsFingerprints(
            Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'));
        return new FaceGeomHairRegionsProposal(
            FaceGeomHairRegionSchemas.Proposal,
            request.AnalysisSha256,
            requestHash,
            request.Source,
            request.Output,
            request.Manifest,
            request.PrimaryColor,
            request.AccentColor,
            request.Assignments,
            [
                new FaceGeomHairRegionsAuthorizedEnvelope(
                    "shader:0",
                    ["shape:0"],
                    FaceGeomHairRegionRole.Primary,
                    1,
                    12,
                    [1U, 2U, 3U],
                    [4U, 5U, 6U])
            ],
            [1],
            fingerprints,
            fingerprints,
            new FaceGeomHairRegionsFile(
                request.Output,
                request.Source.ByteLength,
                Hash('c')),
            null);
    }

    private static ReviewedGameIntake ReviewedIntake(string root)
    {
        var dataRoot = new WorkspacePath(Path.Combine(root, "Data"));
        var plugin = new PluginName("Skyrim.esm");
        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            ActorwrightWorkspace.ResolveRoot(),
            dataRoot,
            new WorkspacePath(Path.Combine(root, "loadorder.txt")),
            new WorkspacePath(Path.Combine(root, "review-output")),
            Hash('6'),
            [
                new PluginClosureReviewEntry(
                    plugin,
                    0,
                    true,
                    true,
                    true,
                    false,
                    true,
                    new WorkspacePath(Path.Combine(
                        dataRoot.Value,
                        plugin.Value)),
                    Hash('8'),
                    [])
            ],
            [],
            [],
            [],
            1,
            Hash('7'),
            Hash('9'),
            false);
    }

    private static string NewRoot(string name)
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "facegeom-hair-regions-cli-" + name + "-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static Sha256Hash Sha(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void AssertPreviewEvidenceInvalid(
        FaceGeomHairRegionsDocumentCodec codec,
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsPreviewEvidenceDocument> canonical,
        string name,
        Action<JsonObject> mutate)
    {
        JsonObject parsed = JsonNode.Parse(
            canonical.Utf8Json.ToArray())!.AsObject();
        mutate(parsed);
        byte[] bytes = Encoding.UTF8.GetBytes(
            parsed.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        bool refused = false;
        try
        {
            codec.DecodePreviewEvidence(
                bytes,
                Sha(bytes));
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        Require(
            refused,
            $"Strict preview evidence accepted adversarial case '{name}'.");
    }

    private static FaceGeomHairRegionsRenderAuthority
        TestRenderAuthority(
            Sha256Hash renderer,
            Sha256Hash textures,
            ImmutableArray<FaceGeomHairTextureAuthority>
                sourceTextures = default)
    {
        var module =
            new FaceGeomHairPyniflyModuleEvidence(
                "io_scene_nifly",
                FaceGeomHairPyniflyModuleKind.SourceFile,
                "__init__.py",
                Hash('a'));
        Sha256Hash moduleFingerprint = Sha(
            Encoding.UTF8.GetBytes(
                $"{module.ModuleName}\0" +
                $"{module.Kind}\0" +
                $"{module.RelativeSource}\0" +
                $"{module.SourceSha256.Value}\n"));
        ImmutableArray<FaceGeomHairTextureEvidence>
            textureEvidence =
                sourceTextures.IsDefault
                    ? []
                    : sourceTextures.Select(item =>
                        new FaceGeomHairTextureEvidence(
                            item.AssetPath,
                            item.ProviderKind,
                            item.Provider,
                            item.Sha256,
                            item.Bytes,
                            item.Sha256,
                            item.Bytes,
                            "texconv-dds-to-png",
                            [
                                new FaceGeomHairTextureBindingEvidence(
                                    FaceGeomHairTextureBindingMode
                                        .SampledImage,
                                    "BSShaderTextureSet_Diffuse",
                                    "Hair",
                                    "HairMaterial",
                                    "HairTexture")
                            ])).ToImmutableArray();
        return new FaceGeomHairRegionsRenderAuthority(
            Hash('1'),
            Hash('2'),
            Hash('3'),
            renderer,
            Hash('4'),
            textures,
            Hash('5'),
            moduleFingerprint,
            1,
            Hash('7'),
            1,
            textureEvidence,
            [module]);
    }

    private static ImmutableArray<FaceGeomHairArtifactEvidence>
        TestArtifactEvidence(
            IEnumerable<FaceGeomHairRegionsPreviewArtifact>
                artifacts) =>
        artifacts
            .Where(item =>
                item.Kind !=
                FaceGeomHairRegionsPreviewArtifactKind
                    .EvidenceDocument)
            .OrderBy(item => item.Kind switch
            {
                FaceGeomHairRegionsPreviewArtifactKind
                    .CombinedFace => 0,
                FaceGeomHairRegionsPreviewArtifactKind
                    .RegionThumbnail => 1,
                FaceGeomHairRegionsPreviewArtifactKind
                    .RegionMask => 2,
                FaceGeomHairRegionsPreviewArtifactKind
                    .ContactSheet => 3,
                _ => 4
            })
            .ThenBy(
                item => item.StructuralId ?? "",
                StringComparer.Ordinal)
            .Select(item =>
                new FaceGeomHairArtifactEvidence(
                    item.Kind,
                    item.StructuralId,
                    Path.GetFileName(item.Path.Value),
                    item.Sha256,
                    item.ByteLength,
                    900,
                    900,
                    Math.Max(
                        1,
                        item.NonEmptyPixelCount ?? 1)))
            .ToImmutableArray();

    private static byte[] PreviewPng(
        int width = 900,
        int height = 900)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(
                width,
                height,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(210, 170, 90, 255));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(
            SKEncodedImageFormat.Png,
            100);
        return data.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record PreparedTransaction(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            Request,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            Proposal,
        string RequestPath,
        string ProposalPath);

    private enum PreviewProofMutation
    {
        None,
        ExtraArtifact,
        DuplicateMask,
        WrongReadbackHash,
        InvalidImageBytes,
        WrongPixelCount,
        InvalidEvidenceDocument,
        EvidenceClaimsPixels,
        UnlistedFilesystemEntry,
        WrongProductionDimensions,
        OversizedArtifact,
        CumulativeOversize
    }

    private sealed class RecordingPreviewService(
        PreviewProofMutation mutation = PreviewProofMutation.None) :
        IFaceGeomHairRegionsPreviewService
    {
        public FaceGeomHairRegionsPreviewRequest? Request
        {
            get;
            private set;
        }

        public ImmutableArray<FaceGeomHairRegionsPreviewArtifact>
            Artifacts
        {
            get;
            private set;
        } = [];

        public async ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            Directory.CreateDirectory(request.OutputRoot.Value);
            var artifacts = ImmutableArray.CreateBuilder<
                FaceGeomHairRegionsPreviewArtifact>();
            byte[] validPng = PreviewPng();

            async ValueTask<FaceGeomHairRegionsPreviewArtifact>
                WriteAsync(
                    FaceGeomHairRegionsPreviewArtifactKind kind,
                    string? structuralId,
                    string name,
                    byte[] bytes,
                    long? nonEmptyPixels)
            {
                string path = Path.Combine(
                    request.OutputRoot.Value,
                    name);
                await File.WriteAllBytesAsync(
                    path,
                    bytes,
                    cancellationToken);
                return new FaceGeomHairRegionsPreviewArtifact(
                    kind,
                    structuralId,
                    new WorkspacePath(path),
                    bytes.LongLength,
                    Sha(bytes),
                    nonEmptyPixels);
            }

            artifacts.Add(await WriteAsync(
                FaceGeomHairRegionsPreviewArtifactKind.CombinedFace,
                null,
                "combined-face.png",
                validPng,
                810_000));
            artifacts.Add(await WriteAsync(
                FaceGeomHairRegionsPreviewArtifactKind.ContactSheet,
                null,
                "contact-sheet.png",
                validPng,
                810_000));
            foreach (FaceGeomHairRegionAssignment assignment in
                     request.RequestDocument.Value.Assignments)
            {
                string safe = assignment.StructuralId.Replace(
                    ':',
                    '-');
                artifacts.Add(await WriteAsync(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail,
                    assignment.StructuralId,
                    safe + "-thumbnail.png",
                    validPng,
                    810_000));
                artifacts.Add(await WriteAsync(
                    FaceGeomHairRegionsPreviewArtifactKind.RegionMask,
                    assignment.StructuralId,
                    safe + "-mask.png",
                    validPng,
                    810_000));
            }

            Sha256Hash rendererHash = Hash('d');
            Sha256Hash textureHash = Hash('c');
            byte[] evidenceBytes =
                new FaceGeomHairRegionsDocumentCodec()
                    .BindPreviewEvidence(
                        new FaceGeomHairRegionsPreviewEvidenceDocument(
                            FaceGeomHairRegionSchemas.PreviewEvidence,
                            request.Materialization.Candidate.Sha256,
                            request.ProposalDocument.Sha256,
                            request.IntakeDocument.Sha256,
                            mutation ==
                            PreviewProofMutation
                                .InvalidEvidenceDocument
                                ? Hash('e')
                                : rendererHash,
                            textureHash,
                            DetectedFaceCount: 1,
                            LandmarkCount: 478,
                            SemanticAnchorCount: 31,
                            VisualAuthority: false,
                            RuntimeAuthority: false,
                            TestRenderAuthority(
                                rendererHash,
                                textureHash),
                            TestArtifactEvidence(
                                artifacts)))
                    .Utf8Json.ToArray();
            FaceGeomHairRegionsPreviewArtifact evidenceArtifact =
                await WriteAsync(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument,
                    null,
                    "evidence.json",
                    evidenceBytes,
                    null);
            artifacts.Add(evidenceArtifact);
            if (mutation ==
                PreviewProofMutation.EvidenceClaimsPixels)
            {
                artifacts[^1] = artifacts[^1] with
                {
                    NonEmptyPixelCount = 0
                };
            }
            else if (
                mutation ==
                PreviewProofMutation.OversizedArtifact)
            {
                artifacts[0] = artifacts[0] with
                {
                    ByteLength = 33_554_433
                };
            }
            else if (
                mutation ==
                PreviewProofMutation.CumulativeOversize)
            {
                for (int index = 0;
                     index < artifacts.Count;
                     index++)
                {
                    if (artifacts[index].Kind !=
                        FaceGeomHairRegionsPreviewArtifactKind
                            .EvidenceDocument)
                    {
                        artifacts[index] =
                            artifacts[index] with
                            {
                                ByteLength = 33_554_432
                            };
                    }
                }
            }

            if (mutation == PreviewProofMutation.InvalidImageBytes)
            {
                byte[] invalidImage = [1, 2, 3, 4];
                await File.WriteAllBytesAsync(
                    artifacts[0].Path.Value,
                    invalidImage,
                    cancellationToken);
                artifacts[0] = artifacts[0] with
                {
                    ByteLength = invalidImage.LongLength,
                    Sha256 = Sha(invalidImage),
                    NonEmptyPixelCount = 4
                };
            }
            else if (mutation == PreviewProofMutation.WrongPixelCount)
            {
                artifacts[0] = artifacts[0] with
                {
                    NonEmptyPixelCount = 4
                };
            }
            else if (
                mutation ==
                PreviewProofMutation.WrongProductionDimensions)
            {
                byte[] wrongDimensions = PreviewPng(2, 2);
                await File.WriteAllBytesAsync(
                    artifacts[0].Path.Value,
                    wrongDimensions,
                    cancellationToken);
                artifacts[0] = artifacts[0] with
                {
                    ByteLength = wrongDimensions.LongLength,
                    Sha256 = Sha(wrongDimensions),
                    NonEmptyPixelCount = 4
                };
            }
            else if (
                mutation ==
                PreviewProofMutation.UnlistedFilesystemEntry)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(
                        request.OutputRoot.Value,
                        "unlisted.tmp"),
                    [90],
                    cancellationToken);
            }

            if (mutation == PreviewProofMutation.ExtraArtifact)
            {
                artifacts.Add(await WriteAsync(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace,
                    null,
                    "extra-face.png",
                    validPng,
                    3));
            }
            else if (
                mutation == PreviewProofMutation.DuplicateMask)
            {
                int lastMask = artifacts
                    .Select((item, index) => (item, index))
                    .Last(pair =>
                        pair.item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask)
                    .index;
                FaceGeomHairRegionsPreviewArtifact firstMask =
                    artifacts.First(item =>
                        item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask);
                artifacts[lastMask] = firstMask;
            }
            else if (
                mutation == PreviewProofMutation.WrongReadbackHash)
            {
                artifacts[0] = artifacts[0] with
                {
                    Sha256 = Hash('f')
                };
            }

            Artifacts = artifacts.ToImmutable();
            return new FaceGeomHairRegionsPreviewResult(
                true,
                new FaceGeomHairRegionsPreviewEvidence(
                    request.Materialization.Candidate.Sha256,
                    request.ProposalDocument.Sha256,
                    request.IntakeDocument.Sha256,
                    rendererHash,
                    textureHash,
                    evidenceArtifact.Sha256,
                    DetectedFaceCount: 1,
                    LandmarkCount: 478,
                    SemanticAnchorCount: 31,
                    TestRenderAuthority(
                        rendererHash,
                        textureHash)),
                Artifacts,
                [
                    new Diagnostic(
                        "facegeom-hair-regions-preview-created",
                        DiagnosticSeverity.Info,
                        "Controlled preview fixture.")
                ],
                VisualAuthority: false,
                RuntimeAuthority: false);
        }
    }

    private enum SessionRendererOutcome
    {
        Success,
        Refusal,
        Cancellation,
        OperationalFailure
    }

    private sealed class SessionLifecycleSourceService(
        bool composed) :
        IFaceGeomHairRegionsPreviewSourceService
    {
        private static readonly byte[] TextureBytes =
            [11, 12, 13, 14, 15, 16];

        public WorkspacePath? SessionRoot
        {
            get;
            private set;
        }

        public WorkspacePath? CandidatePath
        {
            get;
            private set;
        }

        public WorkspacePath? TexturePath
        {
            get;
            private set;
        }

        public bool CandidateWasLiveDuringComposition
        {
            get;
            private set;
        }

        public bool SourceFilesAreLive =>
            CandidatePath is { } candidate &&
            TexturePath is { } texture &&
            File.Exists(candidate.Value) &&
            File.Exists(texture.Value);

        public ValueTask<FaceGeomHairRegionsPreviewSourceResult>
            ComposeAsync(
                FaceGeomHairRegionsPreviewSourceRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionRoot = request.OutputDataRoot;
            CandidatePath = request.Candidate.Path;
            CandidateWasLiveDuringComposition =
                File.Exists(request.Candidate.Path.Value) &&
                File.ReadAllBytes(
                        request.Candidate.Path.Value)
                    .SequenceEqual(
                        request.CandidateBytes);
            string textureDirectory = Path.Combine(
                request.OutputDataRoot.Value,
                "textures",
                "actors",
                "character",
                "hair");
            Directory.CreateDirectory(textureDirectory);
            byte[] textureBytes = TextureBytes.ToArray();
            TexturePath = new WorkspacePath(Path.Combine(
                textureDirectory,
                "session-hair.dds"));
            File.WriteAllBytes(
                TexturePath.Value.Value,
                textureBytes);
            if (!composed)
                return ValueTask.FromResult(
                    new FaceGeomHairRegionsPreviewSourceResult(
                        false,
                        null,
                        [
                            new Diagnostic(
                                "session-source-refusal",
                                DiagnosticSeverity.Error,
                                "Controlled source refusal after materialization.")
                        ]));

            var texture = new FaceGeomHairTextureAuthority(
                new AssetPath(
                    "textures/actors/character/hair/session-hair.dds"),
                AssetProviderKind.Loose,
                "session-provider",
                Sha(textureBytes),
                textureBytes.LongLength,
                TexturePath.Value);
            ImmutableArray<FaceGeomHairTextureAuthority>
                textures = [texture];
            var candidate = new NpcVisualAsset(
                NpcVisualAssetRole.FaceGeom,
                new AssetPath(
                    "meshes/npcmanager/hair-regions/candidate.nif"),
                "session-source",
                request.Candidate.Sha256,
                request.Candidate.ByteLength,
                request.Candidate.Path,
                false,
                []);
            return ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewSourceResult(
                    true,
                    new FaceGeomHairRegionsPreviewSource(
                        candidate,
                        textures,
                        FaceGeomHairTextureAuthorityCanonical
                            .Fingerprint(textures)),
                    []));
        }
    }

    private sealed class SessionLifecycleRenderer(
        SessionRendererOutcome outcome,
        WorkspacePath? lowerSurvivor = null) :
        IFaceGeomHairRegionsRenderer
    {
        public const string TerminationFailure =
            "Controlled renderer termination refusal.";

        public bool Called
        {
            get;
            private set;
        }

        public bool SourceWasLiveDuringRender
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsRenderResult>
            RenderAsync(
                FaceGeomHairRegionsRenderRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Called = true;
            SourceWasLiveDuringRender =
                File.Exists(
                    request.Source.Candidate
                        .MaterializedPath.Value) &&
                request.Source.Textures.All(item =>
                    File.Exists(
                        item.MaterializedPath.Value));
            if (outcome ==
                SessionRendererOutcome.Cancellation)
                throw new FaceGeomHairRegionsOperationCanceledException(
                    "Controlled typed renderer cancellation.",
                    lowerSurvivor is { } cancellationSurvivor
                        ? [cancellationSurvivor]
                        : [],
                    new OperationCanceledException(
                        "Controlled lower cancellation."),
                    [5151],
                    TerminationFailure);
            if (outcome ==
                SessionRendererOutcome.OperationalFailure)
                throw new FaceGeomHairRegionsOperationalException(
                    "Controlled typed renderer operational failure.",
                    lowerSurvivor is { } operationalSurvivor
                        ? [operationalSurvivor]
                        : [],
                    new IOException(
                        "Controlled lower operational failure."),
                    [5252],
                    TerminationFailure);
            if (outcome ==
                SessionRendererOutcome.Refusal)
                return ValueTask.FromResult(
                    new FaceGeomHairRegionsRenderResult(
                        false,
                        [],
                        null,
                        null,
                        0,
                        0,
                        [
                            new Diagnostic(
                                "session-renderer-refusal",
                                DiagnosticSeverity.Error,
                                "Controlled renderer refusal.")
                        ]));

            var artifacts = ImmutableArray.CreateBuilder<
                FaceGeomHairRegionsPreviewArtifact>();
            byte[] png = PreviewPng();
            void Add(
                FaceGeomHairRegionsPreviewArtifactKind kind,
                string? structuralId,
                string fileName) =>
                artifacts.Add(
                    new FaceGeomHairRegionsPreviewArtifact(
                        kind,
                        structuralId,
                        new WorkspacePath(Path.Combine(
                            request.OutputRoot.Value,
                            fileName)),
                        png.LongLength,
                        Sha(png),
                        810_000,
                        png.ToImmutableArray()));
            Add(
                FaceGeomHairRegionsPreviewArtifactKind
                    .CombinedFace,
                null,
                "combined-face.png");
            Add(
                FaceGeomHairRegionsPreviewArtifactKind
                    .ContactSheet,
                null,
                "contact-sheet.png");
            foreach (FaceGeomHairRegionsRegion region in
                     request.Regions)
            {
                string safe = region.StructuralId.Replace(
                    ':',
                    '-');
                Add(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail,
                    region.StructuralId,
                    safe + "-thumbnail.png");
                Add(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask,
                    region.StructuralId,
                    safe + "-mask.png");
            }
            Sha256Hash rendererHash = Hash('D');
            return ValueTask.FromResult(
                new FaceGeomHairRegionsRenderResult(
                    true,
                    artifacts.ToImmutable(),
                    rendererHash,
                    request.Source.TextureFingerprintSha256,
                    FaceGeomImportCount: 1,
                    NifImportInvocationCount: 1,
                    [],
                    TestRenderAuthority(
                        rendererHash,
                        request.Source
                            .TextureFingerprintSha256,
                        request.Source.Textures)));
        }
    }

    private sealed class SessionLifecycleVisualValidator(
        SessionLifecycleSourceService source) :
        INpcVisualPreviewVisualValidator
    {
        public bool SourceWasLiveDuringProof
        {
            get;
            private set;
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateAsync(
                NpcVisualPreviewView faceFront,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Session-lifecycle proof requires encoded bytes.");

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceWasLiveDuringProof =
                source.SourceFilesAreLive;
            return ValueTask.FromResult(
                new NpcVisualPreviewVisualEvidence(
                    1,
                    0.99,
                    478,
                    31,
                    true,
                    []));
        }
    }

    private sealed class PreviewSessionCleanupRefusingHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public string? SessionRoot
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
            if (Path.GetFileName(admittedPath).StartsWith(
                    "preview-",
                    StringComparison.Ordinal))
                SessionRoot = admittedPath;
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
            if (SessionRoot is not null &&
                string.Equals(
                    admittedPath,
                    SessionRoot,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "Controlled preview-session root cleanup refusal.");
        }
    }

    private sealed class RecordingHairRegionsRenderer :
        IFaceGeomHairRegionsRenderer
    {
        public FaceGeomHairRegionsRenderRequest? Request
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsRenderResult>
            RenderAsync(
                FaceGeomHairRegionsRenderRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(
                new FaceGeomHairRegionsRenderResult(
                    false,
                    [],
                    null,
                    null,
                    0,
                    0,
                    [
                        new Diagnostic(
                            "hair-regions-renderer-test-refusal",
                            DiagnosticSeverity.Error,
                            "The recording renderer intentionally refuses after observing the exact candidate.")
                    ]));
        }
    }

    private sealed class RecordingHairRegionsSourceService(
        ImmutableArray<Diagnostic> diagnostics = default) :
        IFaceGeomHairRegionsPreviewSourceService
    {
        public static Sha256Hash TextureFingerprint
        {
            get;
        } = new(new string('A', 64));

        public FaceGeomHairRegionsPreviewSourceRequest? Request
        {
            get;
            private set;
        }

        public bool CandidateWasLiveDuringComposition
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsPreviewSourceResult>
            ComposeAsync(
                FaceGeomHairRegionsPreviewSourceRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            CandidateWasLiveDuringComposition =
                File.Exists(request.Candidate.Path.Value) &&
                File.ReadAllBytes(
                        request.Candidate.Path.Value)
                    .SequenceEqual(
                        request.CandidateBytes);
            var candidate = new NpcVisualAsset(
                NpcVisualAssetRole.FaceGeom,
                new AssetPath(
                    "meshes/npcmanager/hair-regions/candidate.nif"),
                "recording-source",
                request.Candidate.Sha256,
                request.Candidate.ByteLength,
                request.Candidate.Path,
                false,
                []);
            return ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewSourceResult(
                    true,
                    new FaceGeomHairRegionsPreviewSource(
                        candidate,
                        [],
                        TextureFingerprint),
                    diagnostics.IsDefault
                        ? []
                        : diagnostics));
        }
    }

    private sealed class ProvingHairRegionsRenderer :
        IFaceGeomHairRegionsRenderer
    {
        public int ProcessCount
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsRenderResult>
            RenderAsync(
                FaceGeomHairRegionsRenderRequest request,
                CancellationToken cancellationToken)
        {
            ProcessCount++;
            var artifacts = ImmutableArray.CreateBuilder<
                FaceGeomHairRegionsPreviewArtifact>();
            byte[] png = PreviewPng();
            void Add(
                FaceGeomHairRegionsPreviewArtifactKind kind,
                string? structuralId,
                string name)
            {
                string path = Path.Combine(
                    request.OutputRoot.Value,
                    name);
                artifacts.Add(
                    new FaceGeomHairRegionsPreviewArtifact(
                        kind,
                        structuralId,
                        new WorkspacePath(path),
                        png.LongLength,
                        Sha(png),
                        810_000,
                        png.ToImmutableArray()));
            }

            Add(
                FaceGeomHairRegionsPreviewArtifactKind
                    .CombinedFace,
                null,
                "combined-face.png");
            Add(
                FaceGeomHairRegionsPreviewArtifactKind
                    .ContactSheet,
                null,
                "contact-sheet.png");
            foreach (FaceGeomHairRegionsRegion region in
                     request.Regions)
            {
                string safe = region.StructuralId.Replace(
                    ':',
                    '-');
                Add(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail,
                    region.StructuralId,
                    safe + "-thumbnail.png");
                Add(
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask,
                    region.StructuralId,
                    safe + "-mask.png");
            }
            return ValueTask.FromResult(
                new FaceGeomHairRegionsRenderResult(
                true,
                artifacts.ToImmutable(),
                Hash('D'),
                request.Source.TextureFingerprintSha256,
                FaceGeomImportCount: 1,
                NifImportInvocationCount: 1,
                [],
                TestRenderAuthority(
                    Hash('D'),
                    request.Source
                        .TextureFingerprintSha256)));
        }
    }

    private sealed class ProvingHairRegionsVisualValidator :
        INpcVisualPreviewVisualValidator
    {
        public int EncodedValidationCount
        {
            get;
            private set;
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateAsync(
                NpcVisualPreviewView faceFront,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Path validation is not accepted for exact preview proof.");

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EncodedValidationCount++;
            return ValueTask.FromResult(
                new NpcVisualPreviewVisualEvidence(
                    DetectedFaceCount: 1,
                    DetectorScore: 0.99,
                    LandmarkCount: 478,
                    SemanticAnchorCount: 31,
                    EyesNoseAndMouthBounded: true,
                    []));
        }
    }

    private sealed class WeakPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        public bool Called
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Called = true;
            return ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewResult(
                    true,
                    new FaceGeomHairRegionsPreviewEvidence(
                        request.ProposalDocument.Value
                            .ExpectedOutput.Sha256,
                        request.ProposalDocument.Sha256,
                        request.IntakeDocument.Sha256,
                        Hash('d'),
                        Hash('c'),
                        Hash('b'),
                        1,
                        478,
                        31,
                        TestRenderAuthority(
                            Hash('d'),
                            Hash('c'))),
                    [
                        new FaceGeomHairRegionsPreviewArtifact(
                            FaceGeomHairRegionsPreviewArtifactKind
                                .ContactSheet,
                            null,
                            new WorkspacePath(Path.Combine(
                                request.OutputRoot.Value,
                                "does-not-exist.png")),
                            4,
                            Hash('e'),
                            4)
                    ],
                    [
                        new Diagnostic(
                            "weak-proof",
                            DiagnosticSeverity.Info,
                            "Intentionally incomplete proof.")
                    ],
                    false,
                    false));
        }
    }

    private sealed class InconsistentPreviewService(
        bool succeeded,
        Diagnostic diagnostic) :
        IFaceGeomHairRegionsPreviewService
    {
        public async ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            if (!succeeded)
            {
                return new FaceGeomHairRegionsPreviewResult(
                    false,
                    null,
                    [],
                    [diagnostic],
                    false,
                    false);
            }

            FaceGeomHairRegionsPreviewResult complete =
                await new RecordingPreviewService().PreviewAsync(
                    request,
                    cancellationToken);
            return complete with
            {
                Diagnostics = [diagnostic]
            };
        }
    }

    private sealed class AcceptedPreviewVisualValidator :
        INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateAsync(
                NpcVisualPreviewView faceFront,
                CancellationToken cancellationToken)
        {
            byte[] bytes =
                File.ReadAllBytes(faceFront.ImagePath.Value);
            return ValidateEncodedAsync(
                faceFront,
                bytes,
                cancellationToken);
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = encodedImage.ToArray();
            using SKData data =
                SKData.CreateCopy(bytes);
            using SKCodec? codec = SKCodec.Create(data);
            bool accepted =
                codec is not null &&
                codec.Info.Width == 900 &&
                codec.Info.Height == 900 &&
                faceFront.Width == 900 &&
                faceFront.Height == 900 &&
                Sha(bytes) == faceFront.ImageSha256;
            return ValueTask.FromResult(
                accepted
                    ? new NpcVisualPreviewVisualEvidence(
                        1,
                        0.99,
                        478,
                        31,
                        true,
                        [])
                    : new NpcVisualPreviewVisualEvidence(
                        0,
                        0,
                        0,
                        0,
                        false,
                        [
                            new Diagnostic(
                                "test-preview-visual-refused",
                                DiagnosticSeverity.Error,
                                "The actual combined-face bytes are not a 900x900 bound image.")
                        ]));
        }
    }

    private sealed class RejectingPreviewVisualValidator :
        INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateAsync(
                NpcVisualPreviewView faceFront,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualPreviewVisualEvidence(
                    0,
                    0,
                    0,
                    0,
                    false,
                    [
                        new Diagnostic(
                            "test-preview-no-face",
                            DiagnosticSeverity.Error,
                            "The actual combined-face bytes exposed no accepted face.")
                        ]));
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken) =>
            ValidateAsync(
                faceFront,
                cancellationToken);
    }

    private sealed class CancellingPreviewService(
        string survivor) :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken) =>
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Synthetic preview cancellation.",
                [new WorkspacePath(survivor)],
                new OperationCanceledException(),
                [4242],
                "FaceGeomHairRegionsProcessTerminationException: Controlled access denial.");
    }

    private sealed class OperationallyFailingPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(
                Path.Combine(
                    request.OutputRoot.Value,
                    "operational-failure.tmp"),
                [81, 82, 83, 84]);
            throw new IOException(
                "Controlled preview I/O failure.");
        }
    }

    private sealed class
        ProcessOperationallyFailingPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(
                Path.Combine(
                    request.OutputRoot.Value,
                    "process-operational-failure.tmp"),
                [85, 86, 87, 88]);
            throw new FaceGeomHairRegionsOperationalException(
                "Controlled renderer execution failure.",
                [],
                new IOException(
                    "Controlled renderer capture failure."),
                [4646],
                "FaceGeomHairRegionsProcessTerminationException: Controlled execution-failure termination denial.");
        }
    }

    private sealed class SecurityFailingPreviewService(
        bool nested) :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult>
            PreviewAsync(
                FaceGeomHairRegionsPreviewRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(
                Path.Combine(
                    request.OutputRoot.Value,
                    "security-failure.tmp"),
                [91, 92, 93, 94]);
            var security =
                new SecurityException(
                    "Controlled process security failure.");
            if (!nested)
                throw security;
            throw new FaceGeomHairRegionsOperationalException(
                "Opaque renderer operational failure.",
                [],
                new IOException(
                    "Opaque process wrapper.",
                    security));
        }
    }

    private sealed class CancellingHairRegionsRenderer :
        IFaceGeomHairRegionsRenderer
    {
        public ValueTask<FaceGeomHairRegionsRenderResult>
            RenderAsync(
                FaceGeomHairRegionsRenderRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(
                Path.Combine(
                    request.OutputRoot.Value,
                    "renderer-cancel.tmp"),
                [61, 62, 63, 64]);
            throw new OperationCanceledException(
                "Controlled cancellation during renderer execution.");
        }
    }

    private sealed class DefaultDiagnosticsPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewResult(
                    false,
                    null,
                    [],
                    default,
                    false,
                    false));
    }

    private sealed class NullArtifactPreviewService :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewResult(
                    true,
                    null,
                    [null!],
                    [],
                    false,
                    false));
    }

    private sealed class PredicateReparsePathInspector(
        string reparsePath) :
        IFaceGeomHairRegionsPathInspector
    {
        public bool FileExists(string path) =>
            File.Exists(path);

        public bool DirectoryExists(string path) =>
            string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(reparsePath),
                StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(path);

        public FileAttributes GetAttributes(string path) =>
            string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(reparsePath),
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory |
                  FileAttributes.ReparsePoint
                : File.GetAttributes(path);
    }

    private sealed class AdversarialPinnedFileSystemHooks(
        string targetPath,
        string? redirectFinalPath = null,
        string? swapParent = null,
        bool raceCreate = false) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public static byte[] RaceSentinel =>
            [73, 74, 75, 76];

        public bool ParentSwapAttempted
        {
            get;
            private set;
        }

        public bool ParentSwapSucceeded
        {
            get;
            private set;
        }

        private string? movedParent;

        public string? CreateRacePath
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(
            string admittedPath)
        {
            if (!string.Equals(
                    Path.GetFullPath(admittedPath),
                    Path.GetFullPath(targetPath),
                    StringComparison.OrdinalIgnoreCase) ||
                swapParent is null)
                return;
            ParentSwapAttempted = true;
            string moved = swapParent + ".moved";
            try
            {
                Directory.Move(swapParent, moved);
                ParentSwapSucceeded = true;
                movedParent = moved;
                Directory.CreateDirectory(swapParent);
                File.WriteAllBytes(
                    Path.Combine(
                        swapParent,
                        Path.GetFileName(targetPath)),
                    [90, 91, 92]);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                ParentSwapSucceeded = false;
            }
        }

        public void RestoreParent()
        {
            if (!ParentSwapSucceeded ||
                swapParent is null ||
                movedParent is null)
                return;
            if (Directory.Exists(swapParent))
                Directory.Delete(
                    swapParent,
                    recursive: true);
            Directory.Move(
                movedParent,
                swapParent);
            movedParent = null;
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            string.Equals(
                Path.GetFullPath(admittedPath),
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase) &&
            redirectFinalPath is not null
                ? redirectFinalPath
                : actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
            if (!raceCreate ||
                CreateRacePath is not null)
                return;
            CreateRacePath = admittedPath;
            File.WriteAllBytes(
                admittedPath,
                RaceSentinel);
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class DirectoryCreateRacePinnedFileSystemHooks(
        string targetPath) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public static byte[] RaceSentinel =>
            [21, 22, 23, 24];

        public bool RaceCreated
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
            CreateRace(admittedPath);
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            CreateRace(destinationPath);
        }

        public void BeforeCleanup(string admittedPath)
        {
        }

        private void CreateRace(string admittedPath)
        {
            if (RaceCreated ||
                !string.Equals(
                    Path.GetFullPath(admittedPath),
                    Path.GetFullPath(targetPath),
                    StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(admittedPath);
            File.WriteAllBytes(
                Path.Combine(
                    admittedPath,
                    "external-sentinel.bin"),
                RaceSentinel);
            RaceCreated = true;
        }
    }

    private sealed class ReplacingPinnedFileSystemHooks(
        string firstDestination,
        string secondDestination,
        byte[] replacement) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public bool FirstReplacementRefused
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            if (!string.Equals(
                    destinationPath,
                    secondDestination,
                    StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                File.WriteAllBytes(
                    firstDestination,
                    replacement);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                FirstReplacementRefused = true;
            }
            File.WriteAllBytes(
                secondDestination,
                replacement);
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class SecurityRefusingPinnedFileSystemHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath) =>
            throw new UnauthorizedAccessException(
                "Synthetic protected transaction refusal.");

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class CleanupRefusingPinnedFileSystemHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath) =>
            throw new IOException(
                "Controlled private-root cleanup refusal.");
    }

    private sealed class
        PublicationCancellingPinnedFileSystemHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public bool CancellationTriggered
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            if (CancellationTriggered ||
                !Path.GetExtension(destinationPath).Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase))
                return;
            CancellationTriggered = true;
            throw new OperationCanceledException(
                "Controlled cancellation during pinned preview publication.");
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class PostFirstPublicationFailureHooks(
        string outputRoot,
        bool failAfterFirst,
        bool cancel,
        bool refuseCleanup) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private string? firstDestination;

        public int PublicationAttemptCount
        {
            get;
            private set;
        }

        public bool FirstPromotionObserved
        {
            get;
            private set;
        }

        public bool PublicRootExposedDuringPublication
        {
            get;
            private set;
        }

        public bool BundlePromotionObserved
        {
            get;
            private set;
        }

        public bool BundleSourceWasPrivateAndDestinationHidden
        {
            get;
            private set;
        }

        public string? PrivatePublicationRoot
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            if (string.Equals(
                    Path.GetFullPath(destinationPath),
                    Path.GetFullPath(outputRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                BundlePromotionObserved = true;
                PrivatePublicationRoot = sourcePath;
                BundleSourceWasPrivateAndDestinationHidden =
                    Directory.Exists(sourcePath) &&
                    !Directory.Exists(outputRoot) &&
                    !File.Exists(outputRoot);
                return;
            }
            if (!Path.GetExtension(destinationPath).Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase))
                return;
            PrivatePublicationRoot ??=
                Path.GetDirectoryName(destinationPath);
            PublicRootExposedDuringPublication |=
                Directory.Exists(outputRoot) ||
                File.Exists(outputRoot);
            PublicationAttemptCount++;
            if (PublicationAttemptCount == 1)
            {
                firstDestination = destinationPath;
                return;
            }
            FirstPromotionObserved =
                firstDestination is not null &&
                File.Exists(firstDestination);
            if (!failAfterFirst)
                return;
            if (cancel)
                throw new OperationCanceledException(
                    "Controlled cancellation after the first public preview image promotion.");
            throw new IOException(
                "Controlled failure after the first public preview image promotion.");
        }

        public void BeforeCleanup(string admittedPath)
        {
            if (refuseCleanup &&
                PrivatePublicationRoot is not null &&
                string.Equals(
                    Path.GetFullPath(admittedPath),
                    Path.GetFullPath(
                        PrivatePublicationRoot),
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "Controlled public preview root cleanup refusal.");
        }
    }
}
