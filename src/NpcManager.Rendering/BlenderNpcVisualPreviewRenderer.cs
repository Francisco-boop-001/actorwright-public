using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;
using DesktopProcessIdentityId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopProcessIdentityId;

namespace NpcManager.Rendering;

/// <summary>
/// Textured schema-2 renderer. It operates only on the source composer's
/// materialized K-local overlay and retains PyNifly's imported Skyrim
/// materials. The schema-1 clay renderer is intentionally unchanged.
/// </summary>
public sealed partial class BlenderNpcVisualPreviewRenderer(
    WorkspacePath blenderPath,
    WorkspacePath profileRoot,
    ApplicationResourcePath profileManifestPath,
    Sha256Hash expectedProfileManifestSha256,
    EmbeddedBlenderScriptId scriptId,
    WorkspacePath texconvPath,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash expectedBlenderSha256,
    Sha256Hash expectedScriptSha256,
    Sha256Hash expectedTexconvSha256) : INpcVisualPreviewRenderer
{
    private readonly INpcVisualPreviewProcessRunner processRunner =
        new SystemNpcVisualPreviewProcessRunner();

    internal BlenderNpcVisualPreviewRenderer(
        WorkspacePath blenderPath,
        WorkspacePath profileRoot,
        ApplicationResourcePath profileManifestPath,
        Sha256Hash expectedProfileManifestSha256,
        EmbeddedBlenderScriptId scriptId,
        WorkspacePath texconvPath,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        Sha256Hash expectedBlenderSha256,
        Sha256Hash expectedScriptSha256,
        Sha256Hash expectedTexconvSha256,
        INpcVisualPreviewProcessRunner processRunner)
        : this(
            blenderPath,
            profileRoot,
            profileManifestPath,
            expectedProfileManifestSha256,
            scriptId,
            texconvPath,
            policy,
            labRoot,
            expectedBlenderSha256,
            expectedScriptSha256,
            expectedTexconvSha256)
    {
        this.processRunner = processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
    }

    public BlenderNpcVisualPreviewRenderer(
        WorkspacePath blenderPath,
        WorkspacePath profileRoot,
        EmbeddedBlenderScriptId scriptId,
        WorkspacePath texconvPath,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        Sha256Hash expectedBlenderSha256,
        Sha256Hash expectedScriptSha256,
        Sha256Hash expectedTexconvSha256)
        : this(
            blenderPath,
            profileRoot,
            LegacyProfileManifestPath(),
            LegacyProfileManifestHash(),
            scriptId,
            texconvPath,
            policy,
            labRoot,
            expectedBlenderSha256,
            expectedScriptSha256,
            expectedTexconvSha256)
    {
    }

    private const long MaximumMeshBytes = 512L * 1024 * 1024;
    private const long MaximumStatusBytes = 4L * 1024 * 1024;
    private const double BodyLuminanceFloor = 55d;
    private const double HandsLuminanceFloor = 40d;
    private static readonly ImmutableArray<string> ViewIds =
    [
        "face-front",
        "face-left",
        "face-right",
        "face-alternate-light",
        "body-front",
        "body-back"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<NpcVisualPreviewRenderResult> RenderAsync(
        NpcVisualPreviewRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        try
        {
            await VerifyToolAsync(
                blenderPath, expectedBlenderSha256,
                "npc-preview-blender-hash", diagnostics,
                cancellationToken);
            EmbeddedBlenderScript embeddedScript =
                EmbeddedBlenderScriptBundle.Load(scriptId.Value);
            if (embeddedScript.Sha256 != expectedScriptSha256)
                diagnostics.Add(Error(
                    "npc-preview-render-script-hash",
                    "The embedded renderer script does not match its admitted SHA-256. " +
                    $"Expected {expectedScriptSha256.Value}; actual {embeddedScript.Sha256.Value}."));
            await VerifyToolAsync(
                texconvPath, expectedTexconvSha256,
                "npc-preview-texconv-hash", diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            ImmutableArray<NpcVisualAsset> meshAssets =
                request.Source.Assets
                    .Where(item => item.Role is
                        NpcVisualAssetRole.FaceGeom or
                        NpcVisualAssetRole.Body or
                        NpcVisualAssetRole.Hands or
                        NpcVisualAssetRole.Feet or
                        NpcVisualAssetRole.Outfit)
                    .ToImmutableArray();
            foreach (NpcVisualAsset asset in request.Source.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ValidateSourceAssetAsync(
                    asset, request.OutputRoot,
                    diagnostics, cancellationToken);
                await ValidateLowWeightCompanionAsync(
                    asset,
                    request.OutputRoot,
                    diagnostics,
                    cancellationToken);
            }
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            await ConvertTexturesAsync(
                request.Source.Assets
                    .Where(item => item.Role is
                        NpcVisualAssetRole.Texture or
                        NpcVisualAssetRole.FaceTint)
                    .ToImmutableArray(),
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            WorkspacePath statusPath = new(Path.Combine(
                request.OutputRoot.Value,
                "npc-preview-render-evidence.json"));
            WorkspacePath processEvidencePath = new(Path.Combine(
                request.OutputRoot.Value,
                "npc-preview-blender-process-evidence.json"));
            string requestPath = Path.Combine(
                request.OutputRoot.Value,
                $".npc-preview-render-request-{Guid.NewGuid():N}.json");
            if (File.Exists(statusPath.Value))
            {
                diagnostics.Add(Error(
                    "npc-preview-render-output-exists",
                    "Renderer evidence never overwrites an existing file."));
                return Refused(diagnostics);
            }

            try
            {
                object payload = new
                {
                    schemaVersion = request.SceneSchemaVersion,
                    root = labRoot.Value,
                    dataRoot = Path.Combine(
                        request.OutputRoot.Value, "assets", "Data"),
                    outputRoot = request.OutputRoot.Value,
                    width = request.Options.Width,
                    height = request.Options.Height,
                    hairColor = request.Source.HairColorHex,
                    skinTint = request.Source.SkinTintHex,
                    skinTintAlpha =
                        request.Source.SkinTintAlpha,
                    weight = request.Source.Weight,
                    morphs = request.Source.Morphs,
                    triAssets = request.Source.Assets
                        .Where(item =>
                            item.Role ==
                            NpcVisualAssetRole.Tri)
                        .Select(item => new
                        {
                            assetPath =
                                item.AssetPath.Value,
                            path =
                                item.MaterializedPath.Value,
                            sha256 =
                                item.Sha256.Value,
                            provider =
                                item.Provider
                        })
                        .ToImmutableArray(),
                    assets = meshAssets.Select(item => new
                    {
                        role = item.Role.ToString(),
                        assetPath = item.AssetPath.Value,
                        path = item.MaterializedPath.Value,
                        sha256 = item.Sha256.Value,
                        provider = item.Provider,
                        bipedSlotMask = item.BipedSlotMask,
                        lowWeightAssetPath =
                            item.LowWeightAssetPath?.Value,
                        lowWeightPath =
                            item.LowWeightMaterializedPath?.Value,
                        lowWeightSha256 =
                            item.LowWeightSha256?.Value,
                        materials = item.Materials.Select(
                            material => new
                            {
                                shape = material.Shape,
                                shaderType =
                                    material.ShaderType,
                                shaderFlags1 =
                                    material.ShaderFlags1,
                                shaderFlags2 =
                                    material.ShaderFlags2,
                                hasAlpha =
                                    material.HasAlpha,
                                alpha = material.Alpha,
                                tintHex =
                                    material.TintHex,
                                textureSlots =
                                    material.TextureSlots.Select(
                                        slot => new
                                        {
                                            slot = slot.Slot,
                                            semantic =
                                                slot.Semantic,
                                            assetPath =
                                                slot.AssetPath.Value,
                                            provider =
                                                slot.Provider,
                                            sha256 =
                                                slot.Sha256.Value
                                        })
                            })
                    }).ToImmutableArray()
                };
                await WriteNewAsync(
                    new WorkspacePath(requestPath),
                    JsonSerializer.SerializeToUtf8Bytes(
                        payload, JsonOptions),
                    cancellationToken);

                EmbeddedBlenderInvocation invocation = EmbeddedBlenderInvocationFactory.Create(
                    scriptId.Value, ["--request", requestPath, "--status", statusPath.Value]);
                NpcVisualPreviewProcessResult process =
                    await processRunner.RunAsync(
                    blenderPath,
                    invocation.Arguments,
                    profileRoot,
                    invocation.StandardInput,
                    TimeSpan.FromMinutes(12),
                    cancellationToken);
                if (process.ExitCode != 0)
                    ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                        Activity.Current,
                        DesktopFailureOperationId.BlenderNpcVisualPreview,
                        DesktopProcessIdentityId.Blender,
                        expectedBlenderSha256.Value,
                        process.ExitCode,
                        "npc-preview-process-failed");
                diagnostics.AddRange(process.Diagnostics);
                if (HasErrors(diagnostics))
                {
                    await WriteProcessEvidenceAsync(
                        processEvidencePath,
                        statusPath,
                        process,
                        cancellationToken);
                    return Refused(diagnostics);
                }

                RenderStatus? status = await ReadStatusAsync(
                    statusPath,
                    processEvidencePath,
                    diagnostics,
                    cancellationToken);
                if (status is null)
                    await WriteProcessEvidenceAsync(
                        processEvidencePath,
                        statusPath,
                        process,
                        cancellationToken);
                if (status is null || !status.Rendered)
                {
                    diagnostics.Add(Error(
                        "npc-preview-render-failed",
                        status?.Error ??
                        "Blender did not return successful structured evidence."));
                    return Refused(diagnostics);
                }

                ImmutableArray<NpcVisualPreviewView> views =
                    await ValidateViewsAsync(
                        status,
                        request,
                        diagnostics,
                        cancellationToken);
                if (HasErrors(diagnostics))
                    return Refused(diagnostics);

                NpcVisualPreviewRenderEvidence evidence =
                    await BuildEvidenceAsync(
                        status,
                        statusPath,
                        request,
                        diagnostics,
                        cancellationToken);
                if (HasErrors(diagnostics))
                    return Refused(diagnostics);

                WorkspacePath contactSheet = new(Path.Combine(
                    request.OutputRoot.Value, "contact-sheet.png"));
                await WriteContactSheetAsync(
                    views, contactSheet, cancellationToken);
                Sha256Hash contactHash =
                    await HashFileAsync(
                        contactSheet, cancellationToken);

                await VerifyToolAsync(
                    blenderPath, expectedBlenderSha256,
                    "npc-preview-blender-changed", diagnostics,
                    cancellationToken);
                await VerifyToolAsync(
                    texconvPath, expectedTexconvSha256,
                    "npc-preview-texconv-changed", diagnostics,
                    cancellationToken);
                if (HasErrors(diagnostics))
                    return Refused(diagnostics);

                return new NpcVisualPreviewRenderResult(
                    true,
                    views,
                    contactSheet,
                    contactHash,
                    evidence,
                    diagnostics.ToImmutable());
            }
            finally
            {
                TryDelete(requestPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException)
        {
            diagnostics.Add(Error(
                "npc-preview-render-io-failed",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(
        NpcVisualPreviewRenderRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.SceneSchemaVersion != "npc-preview-scene/2")
            diagnostics.Add(Error(
                "npc-preview-render-schema",
                "The textured renderer accepts only npc-preview-scene/2."));
        foreach ((WorkspacePath path, bool directory, string role) in
                 new[]
                 {
                     (blenderPath, false, "Blender"),
                     (profileRoot, true, "Blender profile"),
                     (texconvPath, false, "Texconv")
                 })
        {
            if (!path.IsUnder(labRoot) ||
                (directory
                    ? !Directory.Exists(path.Value)
                    : !File.Exists(path.Value)))
                diagnostics.Add(Error(
                    "npc-preview-render-tool-invalid",
                    $"{role} must be an existing K-local admitted path."));
            AddReparseDiagnostic(
                diagnostics, path.Value, role);
        }
        if (!request.OutputRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(Error(
                "npc-preview-render-output-root",
                "The renderer requires the composer's existing K-local output root."));
        else
        {
            diagnostics.AddRange(
                policy.Evaluate(
                    labRoot, request.OutputRoot));
            AddReparseDiagnostic(
                diagnostics,
                request.OutputRoot.Value,
                "preview output root");
        }
        if (request.Options.Width != 900 ||
            request.Options.Height != 900)
            diagnostics.Add(Error(
                "npc-preview-render-dimensions",
                "Schema-2 NPC preview renders are fixed at 900x900."));
        if (request.Source.Route == NpcVisualPreviewRoute.Ube)
            diagnostics.Add(Error(
                "npc-preview-runtime-composed-route-unsupported",
                "UBE is not admitted for off-engine runtime composition."));
        if (request.Source.Assets.Count(item =>
                item.Role == NpcVisualAssetRole.FaceGeom) != 1)
            diagnostics.Add(Error(
                "npc-preview-facegeom-import-count",
                "Exactly one authoritative FaceGeom asset is required."));
        return diagnostics.ToImmutable();
    }

    private static async ValueTask ValidateSourceAssetAsync(
        NpcVisualAsset asset,
        WorkspacePath root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!asset.MaterializedPath.IsUnder(root) ||
            !File.Exists(asset.MaterializedPath.Value) ||
            Directory.Exists(asset.MaterializedPath.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-render-asset-missing",
                $"Materialized asset '{asset.AssetPath}' is missing or escapes the preview root."));
            return;
        }
        FileInfo info = new(asset.MaterializedPath.Value);
        if (info.Length != asset.Bytes ||
            info.Length is <= 0 or > MaximumMeshBytes)
        {
            diagnostics.Add(Error(
                "npc-preview-render-asset-size",
                $"Materialized asset '{asset.AssetPath}' has an invalid length."));
            return;
        }
        if (await HashFileAsync(
                asset.MaterializedPath,
                cancellationToken) != asset.Sha256)
            diagnostics.Add(Error(
                "npc-preview-render-asset-hash",
                $"Materialized asset '{asset.AssetPath}' changed before rendering."));
    }

    private static async ValueTask ValidateLowWeightCompanionAsync(
        NpcVisualAsset asset,
        WorkspacePath root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (asset.LowWeightAssetPath is null &&
            asset.LowWeightSha256 is null &&
            asset.LowWeightMaterializedPath is null)
            return;
        if (asset.LowWeightAssetPath is null ||
            asset.LowWeightSha256 is null ||
            asset.LowWeightMaterializedPath is null ||
            !asset.LowWeightMaterializedPath.Value.IsUnder(root) ||
            !File.Exists(
                asset.LowWeightMaterializedPath.Value.Value) ||
            Directory.Exists(
                asset.LowWeightMaterializedPath.Value.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-weight-companion-invalid",
                $"The _0.nif companion for '{asset.AssetPath}' is incomplete, missing, or escapes the preview root."));
            return;
        }
        FileInfo info = new(
            asset.LowWeightMaterializedPath.Value.Value);
        if (info.Length is <= 0 or > MaximumMeshBytes ||
            await HashFileAsync(
                asset.LowWeightMaterializedPath.Value,
                cancellationToken) !=
            asset.LowWeightSha256.Value)
            diagnostics.Add(Error(
                "npc-preview-weight-companion-invalid",
                $"The _0.nif companion for '{asset.AssetPath}' changed before rendering."));
    }

    private async ValueTask ConvertTexturesAsync(
        ImmutableArray<NpcVisualAsset> textures,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (NpcVisualAsset texture in textures
                     .DistinctBy(item =>
                         item.MaterializedPath.Value,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item =>
                         item.AssetPath.Value,
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!texture.MaterializedPath.Value.EndsWith(
                    ".dds", StringComparison.OrdinalIgnoreCase))
                continue;
            string png = Path.ChangeExtension(
                texture.MaterializedPath.Value, ".png");
            if (File.Exists(png))
            {
                diagnostics.Add(Error(
                    "npc-preview-texture-output-exists",
                    $"Preview texture '{png}' already exists."));
                return;
            }
            string directory =
                Path.GetDirectoryName(texture.MaterializedPath.Value)!;
            NpcVisualPreviewProcessResult result =
                await processRunner.RunAsync(
                texconvPath,
                [
                    "-ft", "png",
                    "-o", directory,
                    texture.MaterializedPath.Value
                ],
                null,
                null,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            if (result.ExitCode != 0)
                ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                    Activity.Current,
                    DesktopFailureOperationId.BlenderNpcVisualPreview,
                    DesktopProcessIdentityId.Texconv,
                    expectedTexconvSha256.Value,
                    result.ExitCode,
                    "npc-preview-texture-decode-failed");
            diagnostics.AddRange(result.Diagnostics.Select(item =>
                item with
                {
                    Code = item.Code == "npc-preview-process-failed"
                        ? "npc-preview-texture-decode-failed"
                        : item.Code
                }));
            if (HasErrors(diagnostics))
                return;
            if (!File.Exists(png) ||
                new FileInfo(png).Length <= 32)
            {
                diagnostics.Add(Error(
                    "npc-preview-texture-decode-missing",
                    $"Texconv did not create '{png}'."));
                return;
            }
        }
    }

    private static async ValueTask<ImmutableArray<NpcVisualPreviewView>>
        ValidateViewsAsync(
            RenderStatus status,
            NpcVisualPreviewRenderRequest request,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (status.Views is null ||
            !status.Views.Select(item => item.Id)
                .SequenceEqual(
                    ViewIds, StringComparer.Ordinal))
        {
            diagnostics.Add(Error(
                "npc-preview-render-view-set",
                "Blender did not return the exact six deterministic views."));
            return [];
        }
        var views =
            ImmutableArray.CreateBuilder<NpcVisualPreviewView>(
                ViewIds.Length);
        foreach (RenderView row in status.Views)
        {
            WorkspacePath image = new(row.Image ?? "");
            WorkspacePath mask = new(row.RoleMask ?? "");
            if (!image.IsUnder(request.OutputRoot) ||
                !mask.IsUnder(request.OutputRoot) ||
                !File.Exists(image.Value) ||
                !File.Exists(mask.Value) ||
                row.Width != 900 ||
                row.Height != 900)
            {
                diagnostics.Add(Error(
                    "npc-preview-render-view-invalid",
                    $"View '{row.Id}' is missing, escapes its root, or has invalid dimensions."));
                continue;
            }
            Sha256Hash imageHash =
                await HashFileAsync(image, cancellationToken);
            Sha256Hash maskHash =
                await HashFileAsync(mask, cancellationToken);
            if (!string.Equals(
                    imageHash.Value,
                    row.ImageSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    maskHash.Value,
                    row.RoleMaskSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "npc-preview-render-view-hash",
                    $"View '{row.Id}' differs from Blender's structured evidence."));
                continue;
            }
            views.Add(new(
                row.Id ?? "",
                image,
                imageHash,
                mask,
                maskHash,
                row.Width,
                row.Height));
        }
        return views.ToImmutable();
    }

    private static async ValueTask<NpcVisualPreviewRenderEvidence>
        BuildEvidenceAsync(
            RenderStatus status,
            WorkspacePath statusPath,
            NpcVisualPreviewRenderRequest request,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (status.FaceGeomImportCount != 1 ||
            !status.FaceCameraUsedAuthoritativeGeometry)
            diagnostics.Add(Error(
                "npc-preview-facegeom-render-authority",
                "Blender did not prove one authoritative FaceGeom import and face-derived framing."));

        var roleCounts =
            (status.RoleMaskPixelCounts ??
             new Dictionary<string, long>())
            .ToImmutableDictionary(
                StringComparer.Ordinal);
        RequireRolePixels(
            roleCounts, "face-front:FaceGeom",
            request.Source.Assets.Any(item =>
                item.Role == NpcVisualAssetRole.FaceGeom),
            diagnostics);
        RequireRolePixels(
            roleCounts, "body-front:Body",
            request.Source.Assets.Any(item =>
                item.Role == NpcVisualAssetRole.Body),
            diagnostics);
        RequireRolePixels(
            roleCounts, "body-front:Outfit",
            request.Source.HasDeclaredOutfit &&
            request.Source.Assets.Any(item =>
                item.Role == NpcVisualAssetRole.Outfit),
            diagnostics);

        ImmutableArray<NpcVisualPreviewImportedMesh> meshes =
            (status.Meshes ?? [])
            .Select(item => new NpcVisualPreviewImportedMesh(
                Enum.TryParse(
                    item.Role, out NpcVisualAssetRole role)
                    ? role
                    : NpcVisualAssetRole.FaceGeom,
                new AssetPath(item.AssetPath ?? "unknown"),
                item.ObjectName ?? "",
                item.VertexCount,
                (item.Materials ?? []).ToImmutableArray(),
                (item.WorldTransform ?? []).ToImmutableArray()))
            .ToImmutableArray();
        if (meshes.IsDefaultOrEmpty ||
            meshes.Any(item =>
                item.VertexCount <= 0 ||
                item.WorldTransform.Length != 16))
            diagnostics.Add(Error(
                "npc-preview-render-mesh-evidence",
                "Imported mesh identity, topology, or transform evidence is incomplete."));

        ImmutableArray<NpcVisualPreviewImportedMaterial> materials =
            (status.Materials ?? [])
            .Select(item =>
                new NpcVisualPreviewImportedMaterial(
                    item.ObjectName ?? "",
                    item.MaterialName ?? "",
                    (item.TextureBindings ??
                     new Dictionary<string, string>())
                    .ToImmutableDictionary(
                        StringComparer.Ordinal),
                    (item.LoadedImages ?? []).ToImmutableArray(),
                    item.BlendMethod ?? "",
                    item.HasAlpha))
            .ToImmutableArray();
        if (materials.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                "npc-preview-render-material-evidence",
                "PyNifly returned no retained material evidence."));
        if (status.FallbackMaterialCount > 0)
            diagnostics.Add(Warning(
                "npc-preview-render-material-fallback",
                $"{status.FallbackMaterialCount} imported mesh object(s) had no Skyrim material and use the visible diagnostic fallback."));
        foreach (RenderMesh omitted in status.OmittedMaterialRoutes ?? [])
        {
            if (string.IsNullOrWhiteSpace(omitted.ObjectName) ||
                !request.Source.Assets.Any(asset =>
                    asset.Role.ToString() == omitted.Role && asset.AssetPath.Value == omitted.AssetPath) ||
                meshes.Any(mesh => mesh.AssetPath.Value == omitted.AssetPath && mesh.ObjectName == omitted.ObjectName))
                diagnostics.Add(Error(
                    "npc-preview-material-route-omitted-invalid",
                    "Omitted helper identity is unbound or still present in the rendered mesh evidence."));
            else
                diagnostics.Add(Warning(
                    "npc-preview-material-route-omitted",
                    $"Omitted material-less physics helper '{omitted.ObjectName}' from '{omitted.AssetPath}'."));
        }

        ImmutableDictionary<string, double> roleLuminance =
            (status.RoleMeanLuminance ??
             new Dictionary<string, double?>())
            .Where(item => item.Value is not null)
            .ToImmutableDictionary(
                item => item.Key,
                item => item.Value!.Value,
                StringComparer.Ordinal);
        bool bodyDeclared = request.Source.Assets.Any(item =>
            item.Role == NpcVisualAssetRole.Body);
        if (bodyDeclared)
        {
            bool bodyFrontHasPixels =
                roleCounts.TryGetValue(
                    "body-front:Body", out long bodyFrontPixels) &&
                bodyFrontPixels > 0;
            bool bodyBackHasPixels =
                roleCounts.TryGetValue(
                    "body-back:Body", out long bodyBackPixels) &&
                bodyBackPixels > 0;
            if (!bodyFrontHasPixels ||
                !bodyBackHasPixels ||
                !roleLuminance.TryGetValue(
                    "body-front:Body",
                    out double bodyFrontLuminance) ||
                !roleLuminance.TryGetValue(
                    "body-back:Body",
                    out double bodyBackLuminance) ||
                !double.IsFinite(bodyFrontLuminance) ||
                !double.IsFinite(bodyBackLuminance))
                diagnostics.Add(Error(
                    "npc-preview-body-underexposed",
                    "Required body role mask pixel and luminance evidence is missing, empty, or nonfinite for body-front:Body and body-back:Body."));
            else if (bodyFrontLuminance < BodyLuminanceFloor ||
                     bodyBackLuminance < BodyLuminanceFloor)
                diagnostics.Add(Warning(
                    "npc-preview-body-underexposed",
                    $"The neutral studio body views measured front {bodyFrontLuminance.ToString(CultureInfo.InvariantCulture)} and back {bodyBackLuminance.ToString(CultureInfo.InvariantCulture)}; threshold {BodyLuminanceFloor.ToString(CultureInfo.InvariantCulture)}."));
        }
        bool handsDeclared = request.Source.Assets.Any(item =>
            item.Role == NpcVisualAssetRole.Hands);
        if (handsDeclared)
        {
            bool handsHavePixels =
                roleCounts.TryGetValue(
                    "body-front:Hands", out long handsPixels) &&
                handsPixels > 0;
            if (!handsHavePixels ||
                !roleLuminance.TryGetValue(
                    "body-front:Hands",
                    out double handLuminance) ||
                !double.IsFinite(handLuminance))
                diagnostics.Add(Error(
                    "npc-preview-hands-underexposed",
                    "Required hand role mask pixel and luminance evidence is missing, empty, or nonfinite for body-front:Hands."));
            else if (handLuminance < HandsLuminanceFloor)
                diagnostics.Add(Warning(
                    "npc-preview-hands-underexposed",
                    $"The neutral studio body view measured hand luminance {handLuminance.ToString(CultureInfo.InvariantCulture)}; threshold {HandsLuminanceFloor.ToString(CultureInfo.InvariantCulture)}."));
        }

        var applications = new Dictionary<string, int>
        {
            ["faceTintMaterialCount"] =
                status.Tints?.FaceTintMaterialCount ?? 0,
            ["faceDiffuseTimesFaceTintCount"] =
                status.Tints?.FaceDiffuseTimesFaceTintCount ?? 0,
            ["hairTintMaterialCount"] =
                status.Tints?.HairTintMaterialCount ?? 0,
            ["skinTintMaterialCount"] =
                status.Tints?.SkinTintMaterialCount ?? 0,
            ["weightCompanionAssetCount"] =
                status.Tints?.WeightCompanionAssetCount ?? 0,
            ["weightMorphedMeshCount"] =
                status.Tints?.WeightMorphedMeshCount ?? 0,
            ["bodyGenTriAssetCount"] =
                status.Tints?.BodyGenTriAssetCount ?? 0,
            ["bodyGenRequestedMorphCount"] =
                status.Tints?.BodyGenRequestedMorphCount ?? 0,
            ["bodyGenResolvedMorphCount"] =
                status.Tints?.BodyGenResolvedMorphCount ?? 0,
            ["bodyGenUnresolvedMorphCount"] =
                status.Tints?.BodyGenUnresolvedMorphCount ?? 0,
            ["bodyGenMorphedMeshCount"] =
                status.Tints?.BodyGenMorphedMeshCount ?? 0,
            ["bodyGenPositionOffsetCount"] =
                status.Tints?.BodyGenPositionOffsetCount ?? 0,
            ["bodyGenUvOffsetCount"] =
                status.Tints?.BodyGenUvOffsetCount ?? 0,
            ["resolvedMaterialCount"] =
                status.Tints?.ResolvedMaterialCount ?? 0,
            ["resolvedTextureBindingCount"] =
                status.Tints?.ResolvedTextureBindingCount ?? 0,
            ["resolvedTextureImageCount"] =
                status.Tints?.ResolvedTextureImageCount ?? 0,
            ["unresolvedTextureBindingCount"] =
                status.Tints?.UnresolvedTextureBindingCount ?? 0
        }.ToImmutableDictionary(StringComparer.Ordinal);
        if (applications["resolvedMaterialCount"] == 0 ||
            applications["resolvedTextureBindingCount"] == 0)
            diagnostics.Add(Error(
                "npc-preview-material-route-not-applied",
                "Blender did not apply the resolved Skyrim material graph."));
        if (applications["unresolvedTextureBindingCount"] > 0)
            diagnostics.Add(Error(
                "npc-preview-material-route-incomplete",
                $"Blender could not bind {applications["unresolvedTextureBindingCount"]} declared material route(s)."));
        if (request.Source.Route == NpcVisualPreviewRoute.Cotr &&
            (applications["faceTintMaterialCount"] == 0 ||
             applications["faceDiffuseTimesFaceTintCount"] !=
             applications["faceTintMaterialCount"]))
            diagnostics.Add(Error(
                "npc-preview-cotr-facetint-not-applied",
                "The COtR face material did not retain an active slot-0 diffuse multiplied by the canonical slot-6 actor FaceTint."));
        int expectedSkinTintMaterials =
            request.Source.Assets
                .Where(item => item.Role is
                    NpcVisualAssetRole.Body or
                    NpcVisualAssetRole.Hands or
                    NpcVisualAssetRole.Feet or
                    NpcVisualAssetRole.Outfit)
                .SelectMany(item => item.Materials)
                .Count(item =>
                    item.ShaderType ==
                    SkyrimSkinTintShaderType);
        if (request.Source.SkinTintHex is not null &&
            request.Source.SkinTintAlpha > 0.001f &&
            expectedSkinTintMaterials > 0 &&
            applications["skinTintMaterialCount"] !=
            expectedSkinTintMaterials)
            diagnostics.Add(Error(
                "npc-preview-skin-tint-not-applied",
                $"The composed body graph declares {expectedSkinTintMaterials} SkinTint material(s), but Blender applied the actor QNAM tone to {applications["skinTintMaterialCount"]}."));
        int expectedWeightCompanions =
            request.Source.Assets.Count(item =>
                item.LowWeightMaterializedPath is not null);
        if (request.Source.Weight is >= 0 and < 100 &&
            expectedWeightCompanions > 0 &&
            (applications["weightCompanionAssetCount"] !=
                 expectedWeightCompanions ||
             applications["weightMorphedMeshCount"] <
                 expectedWeightCompanions ||
             Math.Abs(
                 (status.Tints?.AppliedWeightPercent ?? -1) -
                 request.Source.Weight) > 0.0001))
            diagnostics.Add(Error(
                "npc-preview-weight-not-applied",
                $"The actor weight {request.Source.Weight} was not proven across all {expectedWeightCompanions} _0/_1 mesh pair(s)."));
        int expectedBodyGenTris = request.Source.Assets.Count(item =>
            item.Role == NpcVisualAssetRole.Tri);
        int nonzeroBodyGenMorphs = request.Source.Morphs.Count(item =>
            Math.Abs(item.Value) >= 0.001f);
        if (expectedBodyGenTris > 0 &&
            applications["bodyGenTriAssetCount"] !=
            expectedBodyGenTris)
            diagnostics.Add(Error(
                "npc-preview-bodygen-tri-not-applied",
                $"The source graph admits {expectedBodyGenTris} PIRT file(s), but Blender applied {applications["bodyGenTriAssetCount"]}."));
        if (nonzeroBodyGenMorphs > 0 &&
            expectedBodyGenTris > 0 &&
            (applications["bodyGenResolvedMorphCount"] == 0 ||
             applications["bodyGenMorphedMeshCount"] == 0 ||
             applications["bodyGenPositionOffsetCount"] == 0))
            diagnostics.Add(Error(
                "npc-preview-bodygen-morph-not-applied",
                $"The source graph declares {nonzeroBodyGenMorphs} nonzero BodyGen assignment(s), but Blender did not prove a positional body morph."));
        if (applications["bodyGenUnresolvedMorphCount"] > 0)
            diagnostics.Add(Warning(
                "npc-preview-bodygen-slider-unresolved",
                $"{applications["bodyGenUnresolvedMorphCount"]} nonzero BodyGen slider name(s) are absent from the admitted PIRT geometry and remain unapplied."));
        int expectedBakedHairTints = request.Source.Assets
            .Where(item =>
                item.Role == NpcVisualAssetRole.FaceGeom)
            .SelectMany(item => item.Materials)
            .Count(item =>
                item.TintHex is not null &&
                item.TextureSlots.Any(slot =>
                    slot.AssetPath.Value.Contains(
                        "hair",
                        StringComparison.OrdinalIgnoreCase)));
        if (expectedBakedHairTints > 0 &&
            applications["hairTintMaterialCount"] !=
            expectedBakedHairTints)
            diagnostics.Add(Error(
                "npc-preview-facegeom-hair-tint-not-applied",
                $"The final FaceGeom declares {expectedBakedHairTints} baked HairTint material(s), but Blender applied {applications["hairTintMaterialCount"]}."));
        else if (expectedBakedHairTints == 0 &&
                 request.Source.HairColorHex is not null)
            diagnostics.Add(Warning(
                "npc-preview-facegeom-hair-tint-unresolved",
                "The NPC has a plugin hair color, but the final FaceGeom exposes no structurally readable baked HairTint; the imported NIF material is retained without fabricating a correction."));

        Sha256Hash statusHash =
            await HashFileAsync(statusPath, cancellationToken);
        return new NpcVisualPreviewRenderEvidence(
            status.BlenderVersion ?? "",
            status.RenderEngine ?? "",
            status.FaceGeomImportCount,
            status.FaceCameraUsedAuthoritativeGeometry,
            status.ArmatureCount,
            (status.Skeletons ?? []).ToImmutableArray(),
            status.FallbackMaterialCount,
            applications,
            roleCounts,
            roleLuminance,
            meshes,
            materials,
            statusPath,
            statusHash);
    }

    private static void RequireRolePixels(
        ImmutableDictionary<string, long> counts,
        string key,
        bool required,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (required &&
            (!counts.TryGetValue(key, out long count) ||
             count <= 0))
            diagnostics.Add(Error(
                "npc-preview-role-mask-empty",
                $"Declared role mask '{key}' contains no pixels."));
    }

    private static async ValueTask WriteContactSheetAsync(
        ImmutableArray<NpcVisualPreviewView> views,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination.Value))
            throw new IOException(
                "The contact sheet output already exists.");
        const int cell = 900;
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(
                cell * 3,
                cell * 2,
                SKColorType.Rgba8888,
                SKAlphaType.Premul))
            ?? throw new InvalidDataException(
                "Skia could not allocate the contact sheet.");
        surface.Canvas.Clear(new SKColor(10, 15, 22, 255));
        for (int index = 0; index < views.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SKBitmap bitmap =
                SKBitmap.Decode(views[index].ImagePath.Value)
                ?? throw new InvalidDataException(
                    $"Could not decode view '{views[index].Id}'.");
            surface.Canvas.DrawBitmap(
                bitmap,
                new SKRect(
                    (index % 3) * cell,
                    (index / 3) * cell,
                    (index % 3 + 1) * cell,
                    (index / 3 + 1) * cell));
        }
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        using SKData data =
            image.Encode(
                SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException(
                "Skia could not encode the contact sheet.");
        await using FileStream stream = new(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        data.SaveTo(stream);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<RenderStatus?> ReadStatusAsync(
        WorkspacePath path,
        WorkspacePath processEvidencePath,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-render-status-missing",
                $"Blender did not produce structured renderer evidence at '{path.Value}'; bounded process evidence is at '{processEvidencePath.Value}'."));
            return null;
        }
        FileInfo info = new(path.Value);
        if (info.Length is <= 0 or > MaximumStatusBytes)
        {
            diagnostics.Add(Error(
                "npc-preview-render-status-size",
                $"Renderer evidence at '{path.Value}' is outside its bounded size; bounded process evidence is at '{processEvidencePath.Value}'."));
            return null;
        }
        try
        {
            RenderStatus? status =
                JsonSerializer.Deserialize<RenderStatus>(
                await File.ReadAllBytesAsync(
                    path.Value, cancellationToken),
                JsonOptions);
            if (status is null)
                diagnostics.Add(Error(
                    "npc-preview-render-status-invalid",
                    $"Renderer evidence at '{path.Value}' was null; bounded process evidence is at '{processEvidencePath.Value}'."));
            return status;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(Error(
                "npc-preview-render-status-invalid",
                $"Renderer evidence at '{path.Value}' is invalid: {exception.Message} Bounded process evidence is at '{processEvidencePath.Value}'."));
            return null;
        }
    }

    private static async ValueTask VerifyToolAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string code,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (await HashFileAsync(
                path, cancellationToken) != expected)
            diagnostics.Add(Error(
                code,
                $"Admitted tool '{path}' differs from its pinned SHA-256."));
    }

    private static async ValueTask WriteNewAsync(
        WorkspacePath path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream, cancellationToken)));
    }

    private static ApplicationResourcePath LegacyProfileManifestPath() =>
        new(Path.Combine(
            AppContext.BaseDirectory,
            "runtime",
            "rendering",
            "npc-preview-profile-manifest.json"));

    private static Sha256Hash LegacyProfileManifestHash()
    {
        ApplicationResourcePath path = LegacyProfileManifestPath();
        if (!File.Exists(path.Value) ||
            Directory.Exists(path.Value))
            return new Sha256Hash(new string('0', 64));
        using FileStream stream = File.Open(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) ||
                     Directory.Exists(current)) &&
                    File.GetAttributes(current)
                        .HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error(
                        "npc-preview-render-reparse-refused",
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(Error(
                    "npc-preview-render-path-inspection-failed",
                    exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(Error(
                    "npc-preview-render-path-inspection-denied",
                    exception.Message));
                return;
            }
            string? parent =
                Directory.GetParent(current)?.FullName;
            if (string.Equals(
                    parent, current,
                    StringComparison.OrdinalIgnoreCase))
                break;
            current = parent ?? "";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(
        string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private const uint SkyrimSkinTintShaderType = 5;

    private static NpcVisualPreviewRenderResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], null, null, null, diagnostics.ToImmutable());

    private sealed record RenderStatus(
        bool Rendered,
        string? BlenderVersion = null,
        string? RenderEngine = null,
        int FaceGeomImportCount = 0,
        bool FaceCameraUsedAuthoritativeGeometry = false,
        int ArmatureCount = 0,
        string[]? Skeletons = null,
        int FallbackMaterialCount = 0,
        TintStatus? Tints = null,
        Dictionary<string, long>? RoleMaskPixelCounts = null,
        Dictionary<string, double?>? RoleMeanLuminance = null,
        RenderMesh[]? Meshes = null,
        RenderMesh[]? OmittedMaterialRoutes = null,
        RenderMaterial[]? Materials = null,
        RenderView[]? Views = null,
        string? Error = null);

    private sealed record TintStatus(
        int FaceTintMaterialCount = 0,
        int FaceDiffuseTimesFaceTintCount = 0,
        int HairTintMaterialCount = 0,
        int SkinTintMaterialCount = 0,
        int WeightCompanionAssetCount = 0,
        int WeightMorphedMeshCount = 0,
        double AppliedWeightPercent = -1,
        int BodyGenTriAssetCount = 0,
        int BodyGenRequestedMorphCount = 0,
        int BodyGenResolvedMorphCount = 0,
        int BodyGenUnresolvedMorphCount = 0,
        int BodyGenMorphedMeshCount = 0,
        int BodyGenPositionOffsetCount = 0,
        int BodyGenUvOffsetCount = 0,
        int ResolvedMaterialCount = 0,
        int ResolvedTextureBindingCount = 0,
        int ResolvedTextureImageCount = 0,
        int UnresolvedTextureBindingCount = 0);

    private sealed record RenderMesh(
        string? Role = null,
        string? AssetPath = null,
        string? ObjectName = null,
        int VertexCount = 0,
        string[]? Materials = null,
        double[]? WorldTransform = null);

    private sealed record RenderMaterial(
        string? ObjectName = null,
        string? MaterialName = null,
        Dictionary<string, string>? TextureBindings = null,
        string[]? LoadedImages = null,
        string? BlendMethod = null,
        bool HasAlpha = false);

    private sealed record RenderView(
        string? Id = null,
        string? Image = null,
        string? ImageSha256 = null,
        string? RoleMask = null,
        string? RoleMaskSha256 = null,
        int Width = 0,
        int Height = 0);
}
