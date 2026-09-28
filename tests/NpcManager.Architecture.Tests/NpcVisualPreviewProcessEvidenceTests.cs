using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;
using SkiaSharp;

namespace NpcManager.Architecture.Tests;

internal static partial class NpcVisualPreviewProcessEvidenceTests
{
    private static readonly JsonSerializerOptions ExposureStatusJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

    public static async Task RunAsync()
    {
        await RequireBoundedSystemStreamDrainAsync();
        string root = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            $"npc-preview-process-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath labRoot = new(root);
            WorkspacePath blender = WriteFile(
                root,
                "tools/blender.exe",
                "controlled Blender fixture");
            WorkspacePath texconv = WriteFile(
                root,
                "tools/texconv.exe",
                "controlled Texconv fixture");
            WorkspacePath profile = new(Path.Combine(
                root,
                "profile"));
            Directory.CreateDirectory(profile.Value);
            ApplicationResourcePath profileManifest = new(Path.Combine(
                root,
                "application-resources",
                "npc-preview-profile-manifest.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(
                profileManifest.Value)!);
            await File.WriteAllTextAsync(
                profileManifest.Value,
                "{\"schemaVersion\":1,\"fixture\":true}",
                new UTF8Encoding(false));

            EmbeddedBlenderScript script =
                EmbeddedBlenderScriptBundle.Load(
                    "render_npc_preview_bundle");
            Require(
                NpcVisualPreviewRendererAuthority.ScriptSha256 ==
                script.Sha256);
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));

            WorkspacePath hashMismatchOutput = CreateOutput(
                root,
                "hash-mismatch",
                out NpcVisualSourceGraph hashMismatchSource);
            Sha256Hash staleScriptSha256 = new(
                "C407AD2CFBB921F83399A252E2D9F1B2D2D4890878303B42EDF20975CF764B01");
            PreviewDependencyPreflightResult stalePreflight =
                PreviewDependencyPreflightService.AdmitNpcPreview(
                    new ApplicationResourceRuntimeAdmissionResult(
                        null,
                        []),
                    NpcVisualPreviewRendererAuthority.ScriptId,
                    staleScriptSha256,
                    blender,
                    Hash(blender.Value),
                    profile,
                    profileManifest,
                    texconv,
                    Hash(texconv.Value));
            Require(stalePreflight.Diagnostics.Any(d =>
                d.Code == "npc-preview-render-script-hash" &&
                d.Message.Contains(
                    staleScriptSha256.Value,
                    StringComparison.Ordinal) &&
                d.Message.Contains(
                    script.Sha256.Value,
                    StringComparison.Ordinal)));
            var hashMismatchRenderer =
                new BlenderNpcVisualPreviewRenderer(
                    blender,
                    profile,
                    profileManifest,
                    Hash(profileManifest.Value),
                    script.Id,
                    texconv,
                    policy,
                    labRoot,
                    Hash(blender.Value),
                    staleScriptSha256,
                    Hash(texconv.Value),
                    new ControlledProcessRunner(
                        writeValidStatus: false));
            NpcVisualPreviewRenderResult hashMismatchResult =
                await hashMismatchRenderer.RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        hashMismatchSource,
                        hashMismatchOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
            Require(!hashMismatchResult.Rendered);
            Require(hashMismatchResult.Diagnostics.Any(d =>
                d.Code == "npc-preview-render-script-hash" &&
                d.Message.Contains(
                    staleScriptSha256.Value,
                    StringComparison.Ordinal) &&
                d.Message.Contains(
                    script.Sha256.Value,
                    StringComparison.Ordinal)));

            WorkspacePath missingStatusOutput = CreateOutput(
                root,
                "missing-status",
                out NpcVisualSourceGraph missingStatusSource);
            WorkspacePath missingStatusPath = new(Path.Combine(
                missingStatusOutput.Value,
                "npc-preview-render-evidence.json"));
            WorkspacePath evidencePath = new(Path.Combine(
                missingStatusOutput.Value,
                "npc-preview-blender-process-evidence.json"));
            Require(!File.Exists(evidencePath.Value));

            var missingStatusRenderer =
                new BlenderNpcVisualPreviewRenderer(
                    blender,
                    profile,
                    profileManifest,
                    Hash(profileManifest.Value),
                    script.Id,
                    texconv,
                    policy,
                    labRoot,
                    Hash(blender.Value),
                    NpcVisualPreviewRendererAuthority.ScriptSha256,
                    Hash(texconv.Value),
                    new ControlledProcessRunner(
                        writeValidStatus: false));
            NpcVisualPreviewRenderResult result =
                await missingStatusRenderer.RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        missingStatusSource,
                        missingStatusOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);

            Require(!result.Rendered);
            Require(File.Exists(evidencePath.Value));
            Require(result.Diagnostics.Any(d =>
                d.Code == "npc-preview-render-status-missing" &&
                d.Message.Contains(
                    missingStatusPath.Value,
                    StringComparison.Ordinal) &&
                d.Message.Contains(
                    evidencePath.Value,
                    StringComparison.Ordinal)));
            using JsonDocument evidence = JsonDocument.Parse(
                await File.ReadAllBytesAsync(evidencePath.Value));
            JsonElement evidenceRoot = evidence.RootElement;
            string[] exactProperties =
            [
                "schemaVersion",
                "runtimeAuthority",
                "visualAuthority",
                "blenderPath",
                "blenderSha256",
                "profileRoot",
                "profileManifestPath",
                "profileManifestSha256",
                "rendererScriptId",
                "rendererScriptSha256",
                "expectedStatusPath",
                "exitCode",
                "standardOutput",
                "standardOutputTruncated",
                "standardError",
                "standardErrorTruncated"
            ];
            string[] actualProperties = evidenceRoot
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Require(actualProperties.SequenceEqual(
                exactProperties.Order(StringComparer.Ordinal),
                StringComparer.Ordinal));
            Require(!evidenceRoot.GetProperty(
                "runtimeAuthority").GetBoolean());
            Require(!evidenceRoot.GetProperty(
                "visualAuthority").GetBoolean());
            Require(evidenceRoot.GetProperty(
                "exitCode").GetInt32() == 0);
            Require(evidenceRoot.GetProperty(
                "standardErrorTruncated").GetBoolean());
            Require(new FileInfo(evidencePath.Value).Length <=
                64 * 1024);
            string evidenceJson = await File.ReadAllTextAsync(
                evidencePath.Value);
            Require(!evidenceJson.Contains(
                ".npc-preview-render-request-",
                StringComparison.Ordinal));
            Require(!evidenceJson.Contains(
                "UNRELATED-FILE-CONTENT",
                StringComparison.Ordinal));

            WorkspacePath validStatusOutput = CreateOutput(
                root,
                "valid-status",
                out NpcVisualSourceGraph validStatusSource);
            WorkspacePath validStatusEvidence = new(Path.Combine(
                validStatusOutput.Value,
                "npc-preview-blender-process-evidence.json"));
            var validStatusRenderer =
                new BlenderNpcVisualPreviewRenderer(
                    blender,
                    profile,
                    profileManifest,
                    Hash(profileManifest.Value),
                    script.Id,
                    texconv,
                    policy,
                    labRoot,
                    Hash(blender.Value),
                    NpcVisualPreviewRendererAuthority.ScriptSha256,
                    Hash(texconv.Value),
                    new ControlledProcessRunner(
                        writeValidStatus: true));
            NpcVisualPreviewRenderResult validStatusResult =
                await validStatusRenderer.RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        validStatusSource,
                        validStatusOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);

            Require(!validStatusResult.Rendered);
            Require(validStatusResult.Diagnostics.Any(d =>
                d.Code == "npc-preview-render-view-set"));
            Require(!File.Exists(validStatusEvidence.Value));

            await RequireExposureAdvisoriesAsync(
                root,
                blender,
                profile,
                profileManifest,
                texconv,
                policy,
                labRoot);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RequireExposureAdvisoriesAsync(
        string root,
        WorkspacePath blender,
        WorkspacePath profile,
        ApplicationResourcePath profileManifest,
        WorkspacePath texconv,
        KOnlyWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        WorkspacePath warningOutput = CreateExposureOutput(
            root,
            "exposure-warning",
            out NpcVisualSourceGraph warningSource);
        BlenderNpcVisualPreviewRenderer CreateRenderer(
            ExposureMeasurementMode mode) =>
            new(
                blender,
                profile,
                profileManifest,
                Hash(profileManifest.Value),
                NpcVisualPreviewRendererAuthority.ScriptId,
                texconv,
                policy,
                labRoot,
                Hash(blender.Value),
                NpcVisualPreviewRendererAuthority.ScriptSha256,
                Hash(texconv.Value),
                new ControlledProcessRunner(
                    writeValidStatus: true,
                    measurementMode: mode));

        NpcVisualPreviewRenderResult warningResult =
            await CreateRenderer(ExposureMeasurementMode.FiniteBelowFloor)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        warningSource,
                        warningOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Diagnostic bodyWarning = warningResult.Diagnostics.Single(item =>
            item.Code == "npc-preview-body-underexposed");
        Diagnostic handsWarning = warningResult.Diagnostics.Single(item =>
            item.Code == "npc-preview-hands-underexposed");
        Require(warningResult.Rendered &&
                bodyWarning.Severity == DiagnosticSeverity.Warning &&
                bodyWarning.Message.Contains(
                    54.25d.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) &&
                bodyWarning.Message.Contains(
                    48.32d.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) &&
                bodyWarning.Message.Contains("55", StringComparison.Ordinal) &&
                handsWarning.Severity == DiagnosticSeverity.Warning &&
                handsWarning.Message.Contains(
                    39.75d.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) &&
                handsWarning.Message.Contains("40", StringComparison.Ordinal) &&
                warningResult.ContactSheetPath is { } contactSheet &&
                File.Exists(contactSheet.Value));

        WorkspacePath missingBodyOutput = CreateExposureOutput(
            root,
            "exposure-missing-body-back",
            out NpcVisualSourceGraph missingBodySource);
        NpcVisualPreviewRenderResult missingBodyResult =
            await CreateRenderer(ExposureMeasurementMode.MissingBodyBack)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        missingBodySource,
                        missingBodyOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Require(!missingBodyResult.Rendered &&
                missingBodyResult.Diagnostics.Any(item =>
                    item.Code == "npc-preview-body-underexposed" &&
                    item.Severity == DiagnosticSeverity.Error) &&
                !File.Exists(Path.Combine(
                    missingBodyOutput.Value,
                    "contact-sheet.png")));

        WorkspacePath missingHandsOutput = CreateExposureOutput(
            root,
            "exposure-missing-hands",
            out NpcVisualSourceGraph missingHandsSource);
        NpcVisualPreviewRenderResult missingHandsResult =
            await CreateRenderer(ExposureMeasurementMode.MissingHands)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        missingHandsSource,
                        missingHandsOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Require(!missingHandsResult.Rendered &&
                missingHandsResult.Diagnostics.Any(item =>
                    item.Code == "npc-preview-hands-underexposed" &&
                    item.Severity == DiagnosticSeverity.Error) &&
                !File.Exists(Path.Combine(
                    missingHandsOutput.Value,
                    "contact-sheet.png")));

        WorkspacePath emptyBodyBackOutput = CreateExposureOutput(
            root,
            "exposure-empty-body-back",
            out NpcVisualSourceGraph emptyBodyBackSource);
        NpcVisualPreviewRenderResult emptyBodyBackResult =
            await CreateRenderer(ExposureMeasurementMode.EmptyBodyBackMask)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        emptyBodyBackSource,
                        emptyBodyBackOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Require(!emptyBodyBackResult.Rendered &&
                emptyBodyBackResult.Diagnostics.Any(item =>
                    item.Code == "npc-preview-body-underexposed" &&
                    item.Severity == DiagnosticSeverity.Error) &&
                !File.Exists(Path.Combine(
                    emptyBodyBackOutput.Value,
                    "contact-sheet.png")));

        WorkspacePath emptyHandsOutput = CreateExposureOutput(
            root,
            "exposure-empty-hands",
            out NpcVisualSourceGraph emptyHandsSource);
        NpcVisualPreviewRenderResult emptyHandsResult =
            await CreateRenderer(ExposureMeasurementMode.EmptyHandsMask)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        emptyHandsSource,
                        emptyHandsOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Require(!emptyHandsResult.Rendered &&
                emptyHandsResult.Diagnostics.Any(item =>
                    item.Code == "npc-preview-hands-underexposed" &&
                    item.Severity == DiagnosticSeverity.Error) &&
                !File.Exists(Path.Combine(
                    emptyHandsOutput.Value,
                    "contact-sheet.png")));

        WorkspacePath nonFiniteOutput = CreateExposureOutput(
            root,
            "exposure-nonfinite-json",
            out NpcVisualSourceGraph nonFiniteSource);
        NpcVisualPreviewRenderResult nonFiniteResult =
            await CreateRenderer(ExposureMeasurementMode.InvalidNonFiniteJson)
                .RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        "npc-preview-scene/2",
                        nonFiniteSource,
                        nonFiniteOutput,
                        new NpcVisualPreviewOptions()),
                    CancellationToken.None);
        Require(!nonFiniteResult.Rendered &&
                nonFiniteResult.Diagnostics.Any(item =>
                    item.Code == "npc-preview-render-status-invalid" &&
                    item.Severity == DiagnosticSeverity.Error) &&
                !File.Exists(Path.Combine(
                    nonFiniteOutput.Value,
                    "contact-sheet.png")));
    }

    private static async Task RequireBoundedSystemStreamDrainAsync()
    {
        string processPath = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The architecture test process path is unavailable.");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        Require(BuildChildProcessArguments(
                @"C:\Program Files\dotnet\dotnet.exe",
                assemblyPath)
            .SequenceEqual(
                [assemblyPath, "--emit-npc-preview-process-streams"]));
        Require(BuildChildProcessArguments(
                @"K:\controlled\NpcManager.Architecture.Tests.exe",
                assemblyPath)
            .SequenceEqual(
                ["--emit-npc-preview-process-streams"]));
        NpcVisualPreviewProcessResult result =
            await new SystemNpcVisualPreviewProcessRunner().RunAsync(
                new WorkspacePath(processPath),
                BuildChildProcessArguments(
                    processPath,
                    assemblyPath),
                null,
                null,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
        Require(result.ExitCode == 0);
        Require(result.Diagnostics.IsEmpty);
        Require(result.StandardOutput is
        {
            Text.Length: 16_384,
            Truncated: true
        });
        Require(result.StandardError is
        {
            Text.Length: 16_384,
            Truncated: true
        });
        Require(result.StandardOutput.Text.All(character =>
            character == 'O'));
        Require(result.StandardError.Text.All(character =>
            character == 'E'));
    }

    private static ImmutableArray<string> BuildChildProcessArguments(
        string processPath,
        string assemblyPath) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase)
            ? [assemblyPath, "--emit-npc-preview-process-streams"]
            : ["--emit-npc-preview-process-streams"];

    private static WorkspacePath CreateOutput(
        string root,
        string name,
        out NpcVisualSourceGraph source)
    {
        WorkspacePath output = new(Path.Combine(root, name));
        string faceGeom = Path.Combine(
            output.Value,
            "assets",
            "Data",
            "meshes",
            "actors",
            "character",
            "FaceGenData",
            "FaceGeom",
            "Fixture.esp",
            "00000800.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeom)!);
        File.WriteAllText(
            faceGeom,
            "UNRELATED-FILE-CONTENT",
            new UTF8Encoding(false));
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName("Fixture.esp"),
            new PluginName("Fixture.esp"),
            new FormId(0x800),
            "NPC_");
        source = new NpcVisualSourceGraph(
            NpcVisualPreviewRoute.Cotr,
            identity,
            NpcSex.Female,
            50,
            "FixtureRace",
            "#101010",
            "#F0D0C0",
            [
                new NpcVisualAsset(
                    NpcVisualAssetRole.FaceGeom,
                    new AssetPath(
                        "meshes/actors/character/FaceGenData/FaceGeom/Fixture.esp/00000800.nif"),
                    "Fixture.esp",
                    Hash(faceGeom),
                    new FileInfo(faceGeom).Length,
                    new WorkspacePath(faceGeom),
                    false,
                    [])
            ],
            [],
            false,
            []);
        return output;
    }

    private static WorkspacePath CreateExposureOutput(
        string root,
        string name,
        out NpcVisualSourceGraph source)
    {
        WorkspacePath output = CreateOutput(root, name, out source);
        source = source with
        {
            Assets = source.Assets
                .Add(CreateExposureAsset(
                    NpcVisualAssetRole.Body,
                    output.Value,
                    "assets/meshes/body.nif"))
                .Add(CreateExposureAsset(
                    NpcVisualAssetRole.Hands,
                    output.Value,
                    "assets/meshes/hands.nif"))
        };
        return output;
    }

    private static NpcVisualAsset CreateExposureAsset(
        NpcVisualAssetRole role,
        string outputRoot,
        string relative)
    {
        WorkspacePath path = WriteFile(
            outputRoot,
            relative,
            $"controlled {role} fixture");
        return new NpcVisualAsset(
            role,
            new AssetPath(relative.Replace(
                Path.DirectorySeparatorChar,
                '/')),
            "Fixture.esp",
            Hash(path.Value),
            new FileInfo(path.Value).Length,
            path,
            false,
            []);
    }

    private static void WriteExposureStatus(
        string statusPath,
        ExposureMeasurementMode measurementMode)
    {
        string outputRoot = Path.GetDirectoryName(statusPath) ??
            throw new InvalidOperationException(
                "Exposure fixture status path has no output root.");
        var views = new List<object>();
        foreach (string id in
                 NpcVisualPreviewPersistenceContract.RequiredViewIds)
        {
            string image = Path.Combine(outputRoot, $"{id}.png");
            string roleMask = Path.Combine(outputRoot, $"{id}-roles.png");
            WritePng(image, SKColors.Bisque);
            WritePng(roleMask, SKColors.White);
            views.Add(new
            {
                id,
                image,
                imageSha256 = Hash(image).Value,
                roleMask,
                roleMaskSha256 = Hash(roleMask).Value,
                width = 900,
                height = 900
            });
        }

        var roleLuminance = new Dictionary<string, double>(
            StringComparer.Ordinal)
        {
            ["body-front:Body"] = 54.25d,
            ["body-back:Body"] = 48.32d,
            ["body-front:Hands"] = 39.75d
        };
        var rolePixelCounts = new Dictionary<string, long>(
            StringComparer.Ordinal)
        {
            ["face-front:FaceGeom"] = 4,
            ["body-front:Body"] = 4,
            ["body-back:Body"] = 4,
            ["body-front:Hands"] = 4
        };
        if (measurementMode == ExposureMeasurementMode.MissingBodyBack)
            roleLuminance.Remove("body-back:Body");
        if (measurementMode == ExposureMeasurementMode.MissingHands)
            roleLuminance.Remove("body-front:Hands");
        if (measurementMode == ExposureMeasurementMode.EmptyBodyBackMask)
        {
            rolePixelCounts["body-back:Body"] = 0;
            roleLuminance["body-back:Body"] = 0d;
        }
        if (measurementMode == ExposureMeasurementMode.EmptyHandsMask)
        {
            rolePixelCounts["body-front:Hands"] = 0;
            roleLuminance["body-front:Hands"] = 0d;
        }
        if (measurementMode == ExposureMeasurementMode.InvalidNonFiniteJson)
            roleLuminance["body-front:Hands"] = double.NaN;

        var status = new
        {
            rendered = true,
            blenderVersion = "4.5.1",
            renderEngine = "BLENDER_EEVEE_NEXT",
            faceGeomImportCount = 1,
            faceCameraUsedAuthoritativeGeometry = true,
            armatureCount = 1,
            skeletons = new[] { "NPC Root [Root]" },
            fallbackMaterialCount = 0,
            tints = new
            {
                faceTintMaterialCount = 1,
                faceDiffuseTimesFaceTintCount = 1,
                resolvedMaterialCount = 1,
                resolvedTextureBindingCount = 1
            },
            roleMaskPixelCounts = rolePixelCounts,
            roleMeanLuminance = roleLuminance,
            meshes = new[]
            {
                new
                {
                    role = "FaceGeom",
                    assetPath = "meshes/face.nif",
                    objectName = "FaceGeom",
                    vertexCount = 3,
                    materials = new[] { "Face" },
                    worldTransform = new[]
                    {
                        1d, 0d, 0d, 0d,
                        0d, 1d, 0d, 0d,
                        0d, 0d, 1d, 0d,
                        0d, 0d, 0d, 1d
                    }
                }
            },
            materials = new[]
            {
                new
                {
                    objectName = "FaceGeom",
                    materialName = "Face",
                    textureBindings = new Dictionary<string, string>(),
                    loadedImages = Array.Empty<string>(),
                    blendMethod = "OPAQUE",
                    hasAlpha = false
                }
            },
            views
        };
        File.WriteAllBytes(
            statusPath,
            JsonSerializer.SerializeToUtf8Bytes(
                status,
                ExposureStatusJsonOptions));
    }

    private static void WritePng(string path, SKColor color)
    {
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(
                900,
                900,
                SKColorType.Rgba8888,
                SKAlphaType.Premul))
            ?? throw new InvalidOperationException(
                "Controlled exposure PNG surface allocation failed.");
        surface.Canvas.Clear(color);
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(
            SKEncodedImageFormat.Png,
            100)
            ?? throw new InvalidOperationException(
                "Controlled exposure PNG encoding failed.");
        using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        data.SaveTo(stream);
    }

    private static WorkspacePath WriteFile(
        string root,
        string relative,
        string contents)
    {
        string path = Path.Combine(
            root,
            relative.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            contents,
            new UTF8Encoding(false));
        return new WorkspacePath(path);
    }

    private static Sha256Hash Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException(
                "NPC visual preview process evidence regression failed.");
    }

    private enum ExposureMeasurementMode
    {
        None,
        FiniteBelowFloor,
        MissingBodyBack,
        MissingHands,
        EmptyBodyBackMask,
        EmptyHandsMask,
        InvalidNonFiniteJson
    }

    private sealed class ControlledProcessRunner(
        bool writeValidStatus,
        ExposureMeasurementMode measurementMode = ExposureMeasurementMode.None) :
        INpcVisualPreviewProcessRunner
    {
        public ValueTask<NpcVisualPreviewProcessResult> RunAsync(
            WorkspacePath executable,
            ImmutableArray<string> arguments,
            WorkspacePath? blenderProfile,
            string? standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (writeValidStatus)
            {
                int statusIndex = arguments.IndexOf("--status");
                Require(statusIndex >= 0 &&
                    statusIndex + 1 < arguments.Length);
                if (measurementMode == ExposureMeasurementMode.None)
                    File.WriteAllText(
                        arguments[statusIndex + 1],
                        "{\"rendered\":true}",
                        new UTF8Encoding(false));
                else
                    WriteExposureStatus(
                        arguments[statusIndex + 1],
                        measurementMode);
            }
            return ValueTask.FromResult(
                new NpcVisualPreviewProcessResult(
                    0,
                    new NpcVisualPreviewCapturedStream(
                        "controlled standard output",
                        false),
                    new NpcVisualPreviewCapturedStream(
                        new string('E', 16_384),
                        true),
                    []));
        }
    }
}
