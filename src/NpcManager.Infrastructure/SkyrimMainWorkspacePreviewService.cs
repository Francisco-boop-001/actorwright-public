using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Binds one selected winning NPC to the existing semantic scene, Blender
/// image, deterministic reroll, and Blender/PyNifly NIF services. All output
/// remains static off-engine evidence and explicitly lacks runtime authority.
/// </summary>
public sealed class SkyrimMainWorkspacePreviewService(
    IPreviewSceneService previewSceneService,
    IPreviewRerollService previewRerollService,
    IPreviewNifBinaryExportService nifExporter,
    IWorkspacePolicy policy,
    WorkspacePath labRoot)
    : ISkyrimMainWorkspacePreviewService
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async ValueTask<SkyrimMainWorkspacePreviewResult>
        RenderAsync(
            SkyrimMainWorkspacePreviewRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateSelected(request.SelectedRecord, diagnostics);
        if (request.Options is null)
            diagnostics.Add(Error(
                "main-workspace-preview-options",
                "Preview requires one options document."));
        if (HasErrors(diagnostics) ||
            request.SelectedRecord is null ||
            request.Options is null)
            return PreviewRefused(
                request.SelectedRecord, diagnostics);
        ManifestInspection? manifest = await InspectManifestAsync(
            request.ManifestPath,
            request.ExpectedManifestSha256,
            request.SelectedRecord.Identity.FormId,
            diagnostics,
            cancellationToken);
        if (request.Lighting is { } lighting)
            diagnostics.AddRange(
                SkyrimLightingRules.Validate(lighting));
        if (request.Animation is { } animation)
        {
            if (manifest is not null &&
                animation.ManifestSha256 != manifest.Sha256)
                diagnostics.Add(Error(
                    "main-workspace-preview-animation-stale",
                    "The selected animation belongs to a different manifest hash."));
            if (animation.Clip is null ||
                string.IsNullOrWhiteSpace(animation.Clip.Id))
                diagnostics.Add(Error(
                    "main-workspace-preview-animation-invalid",
                    "The selected animation clip is invalid."));
        }
        if (HasErrors(diagnostics) || manifest is null)
            return PreviewRefused(
                request.SelectedRecord, diagnostics);

        FormReference? outfit = null;
        if (!string.IsNullOrWhiteSpace(request.VariantId))
        {
            if (!manifest.Variants.TryGetValue(
                    request.VariantId, out FormReference selectedOutfit))
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-variant-missing",
                    $"Variant '{request.VariantId}' is absent from the exact manifest."));
                return PreviewRefused(
                    request.SelectedRecord, diagnostics);
            }
            outfit = selectedOutfit;
        }

        SkyrimMainWorkspacePreviewProjection projection =
            SkyrimMainWorkspaceRules.ProjectPreview(request.Options);
        PreviewSceneResult rendered;
        bool sceneExisted =
            File.Exists(request.ScenePath.Value);
        bool imageExisted =
            File.Exists(request.ImagePath.Value);
        try
        {
            rendered = await previewSceneService.RenderAsync(
                new PreviewSceneRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.ManifestPath,
                    request.ScenePath,
                    projection.VisibleAssets,
                    projection.Morphs,
                    outfit,
                    request.VariantId,
                    AnimationId: request.Animation?.Clip.Id,
                    AnimationFrame:
                        request.Animation is null ? null : 0,
                    AnimationPlaybackRate:
                        request.Animation?.Clip.FramesPerSecond,
                    AnimationPlaying: false,
                    AssetRoot: request.AssetRoot,
                    ImageOutputPath: request.ImagePath,
                    ImageWidth: 512,
                    ImageHeight: 512,
                    RenderHeadwear: false,
                    LightingOverride: request.Lighting),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        diagnostics.AddRange(rendered.Diagnostics);
        if (!rendered.Written ||
            rendered.Artifact is null ||
            rendered.OutputSha256 is null ||
            HasErrors(diagnostics))
        {
            if (!sceneExisted)
                TryDelete(request.ScenePath.Value);
            if (!imageExisted)
                TryDelete(request.ImagePath.Value);
            return PreviewRefused(
                request.SelectedRecord,
                diagnostics,
                rendered.Artifact);
        }

        PreviewSceneArtifact artifact = rendered.Artifact;
        Sha256Hash? imageHash = null;
        if (!string.Equals(
                artifact.NpcFormId,
                request.SelectedRecord.Identity.FormId.ToString(),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "main-workspace-preview-result-npc",
                "The rendered scene belongs to a different NPC FormID."));
        if (!string.Equals(
                artifact.InputManifestSha256,
                manifest.Sha256.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "main-workspace-preview-result-manifest",
                "The rendered scene did not retain the exact manifest hash."));
        if (!File.Exists(request.ScenePath.Value) ||
            await HashFileAsync(
                request.ScenePath.Value, cancellationToken) !=
            rendered.OutputSha256)
            diagnostics.Add(Error(
                "main-workspace-preview-scene-hash",
                "The rendered scene file does not match its result hash."));

        if (artifact.RenderedImage is not { } image)
            diagnostics.Add(Error(
                "main-workspace-preview-image-missing",
                "The renderer did not return one PNG artifact."));
        else
        {
            if (!string.Equals(
                    Path.GetFullPath(image.Path),
                    request.ImagePath.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                image.Width != 512 ||
                image.Height != 512 ||
                image.MeshCount <= 0 ||
                !File.Exists(request.ImagePath.Value))
                diagnostics.Add(Error(
                    "main-workspace-preview-image-shape",
                    "The rendered PNG path, dimensions, or mesh count is invalid."));
            else
            {
                imageHash = await HashFileAsync(
                    request.ImagePath.Value, cancellationToken);
                if (!string.Equals(
                        image.Sha256,
                        imageHash.Value.Value,
                        StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(Error(
                        "main-workspace-preview-image-hash",
                        "The rendered PNG bytes do not match their reported SHA-256."));
            }
        }

        if (request.Lighting is { } expectedLighting &&
            (artifact.Lighting is null ||
             !SkyrimLightingRules.AreEquivalent(
                 expectedLighting, artifact.Lighting)))
            diagnostics.Add(Error(
                "main-workspace-preview-lighting",
                "The rendered scene did not retain the selected lighting override."));
        if (request.Animation is { } expectedAnimation &&
            (artifact.Animation is null ||
             !string.Equals(
                 artifact.Animation.Id,
                 expectedAnimation.Clip.Id,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 artifact.Animation.Path,
                 expectedAnimation.Clip.Path,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(
                 artifact.Animation.Skeleton,
                 expectedAnimation.Clip.Skeleton,
                 StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error(
                "main-workspace-preview-animation",
                "The rendered scene did not retain the selected animation."));
        await ValidateSourceAssetsAsync(
            request.AssetRoot,
            artifact.Assets,
            diagnostics,
            cancellationToken);

        if (HasErrors(diagnostics) || imageHash is null)
        {
            TryDelete(request.ScenePath.Value);
            TryDelete(request.ImagePath.Value);
            return PreviewRefused(
                request.SelectedRecord, diagnostics, artifact);
        }
        return new SkyrimMainWorkspacePreviewResult(
            true,
            request.SelectedRecord,
            artifact,
            request.ScenePath,
            rendered.OutputSha256,
            request.ImagePath,
            imageHash,
            false,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimMainWorkspacePreviewResult>
        RerollAsync(
            SkyrimMainWorkspaceRerollRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string? parent =
            Path.GetDirectoryName(request.Preview.ScenePath.Value);
        if (parent is null || !Directory.Exists(parent))
            return PreviewRefused(
                request.Preview.SelectedRecord,
                [
                    Error(
                        "main-workspace-reroll-parent",
                        "The reroll scene parent directory does not exist.")
                ]);
        string temporary = Path.Combine(
            parent,
            ".main-workspace-reroll-" +
            Guid.NewGuid().ToString("N") +
            ".json");
        try
        {
            PreviewRerollResult reroll =
                await previewRerollService.RerollAsync(
                    new PreviewRerollRequest(
                        GameEdition.SkyrimSpecialEdition,
                        request.Preview.ManifestPath,
                        new WorkspacePath(temporary),
                        request.Preview.SelectedRecord.Identity.FormId,
                        request.Seed),
                    cancellationToken);
            if (!reroll.Written ||
                reroll.Artifact?.SelectedVariantId is not { } selected)
                return PreviewRefused(
                    request.Preview.SelectedRecord,
                    reroll.Diagnostics);
            SkyrimMainWorkspacePreviewResult rendered =
                await RenderAsync(
                    request.Preview with { VariantId = selected },
                    cancellationToken);
            return rendered with
            {
                Diagnostics =
                    reroll.Diagnostics.AddRange(
                        rendered.Diagnostics)
            };
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async ValueTask<SkyrimMainWorkspaceNifExportResult>
        ExportNifAsync(
            SkyrimMainWorkspaceNifExportRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateSelected(request.SelectedRecord, diagnostics);
        if (HasErrors(diagnostics) ||
            request.SelectedRecord is null)
            return NifRefused(diagnostics);
        diagnostics.AddRange(
            policy.EvaluateReadRoot(
                labRoot, request.ScenePath));
        if (!IsHashValid(request.ExpectedSceneSha256))
            diagnostics.Add(Error(
                "main-workspace-nif-scene-hash",
                "NIF export requires one non-default scene SHA-256."));
        if (!File.Exists(request.ScenePath.Value))
            diagnostics.Add(Error(
                "main-workspace-nif-scene-missing",
                "The accepted preview scene no longer exists."));
        Sha256Hash? actualSceneHash = null;
        PreviewSceneArtifact? scene = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(
                    request.ScenePath.Value, cancellationToken);
                actualSceneHash = new Sha256Hash(
                    Convert.ToHexString(SHA256.HashData(bytes)));
                if (actualSceneHash !=
                    request.ExpectedSceneSha256)
                    diagnostics.Add(Error(
                        "main-workspace-nif-scene-changed",
                        "The preview scene changed after acceptance."));
                else
                    scene =
                        JsonSerializer.Deserialize<PreviewSceneArtifact>(
                            bytes, JsonOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException or
                ArgumentException)
            {
                diagnostics.Add(Error(
                    "main-workspace-nif-scene-read",
                    exception.Message));
            }
        }
        if (scene is null && !HasErrors(diagnostics))
            diagnostics.Add(Error(
                "main-workspace-nif-scene-empty",
                "The accepted preview scene is empty."));
        else if (scene is not null &&
                 (!string.Equals(
                      scene.ArtifactKind,
                      "preview-scene-semantic-build",
                      StringComparison.Ordinal) ||
                  !string.Equals(
                      scene.NpcFormId,
                      request.SelectedRecord.Identity.FormId.ToString(),
                      StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error(
                "main-workspace-nif-scene-npc",
                "The accepted preview scene belongs to a different selected NPC."));
        if (HasErrors(diagnostics) ||
            actualSceneHash is null ||
            scene is null)
            return NifRefused(diagnostics);

        PreviewNifBinaryExportResult exported =
            await nifExporter.ExportAsync(
                new PreviewNifBinaryExportRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.ScenePath,
                    request.AssetRoot,
                    request.Destination),
                cancellationToken);
        diagnostics.AddRange(exported.Diagnostics);
        if (!exported.Written ||
            exported.Artifact is null ||
            exported.OutputSha256 is null ||
            HasErrors(diagnostics))
            return NifRefused(diagnostics, exported.Artifact);

        PreviewNifBinaryExportArtifact artifact =
            exported.Artifact;
        Sha256Hash? observedOutput = null;
        if (File.Exists(request.Destination.Value))
            observedOutput = await HashFileAsync(
                request.Destination.Value, cancellationToken);
        if (observedOutput != exported.OutputSha256 ||
            !string.Equals(
                artifact.SceneSha256,
                actualSceneHash.Value.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFullPath(artifact.OutputPath),
                request.Destination.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                artifact.OutputSha256,
                observedOutput?.Value,
                StringComparison.OrdinalIgnoreCase) ||
            artifact.MeshCount <= 0 ||
            artifact.ByteLength <= 32)
            diagnostics.Add(Error(
                "main-workspace-nif-output-readback",
                "The exported NIF failed exact path, hash, size, mesh, or scene readback."));
        if (HasErrors(diagnostics) ||
            observedOutput is null)
        {
            TryDelete(request.Destination.Value);
            return NifRefused(diagnostics, artifact);
        }
        return new SkyrimMainWorkspaceNifExportResult(
            true,
            artifact,
            request.Destination,
            observedOutput,
            false,
            diagnostics.ToImmutable());
    }

    private async ValueTask<ManifestInspection?>
        InspectManifestAsync(
            WorkspacePath path,
            Sha256Hash expectedHash,
            FormId selectedFormId,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        diagnostics.AddRange(
            policy.EvaluateReadRoot(labRoot, path));
        if (!IsHashValid(expectedHash))
            diagnostics.Add(Error(
                "main-workspace-preview-manifest-hash",
                "Preview requires one non-default manifest SHA-256."));
        if (!File.Exists(path.Value) ||
            !path.Value.EndsWith(
                ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "main-workspace-preview-manifest-missing",
                "Preview requires an existing K-local JSON manifest."));
        if (HasErrors(diagnostics))
            return null;
        try
        {
            FileInfo info = new(path.Value);
            if (info.Length is <= 0 or > MaximumManifestBytes)
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-manifest-size",
                    "The preview manifest is empty or exceeds 4 MiB."));
                return null;
            }
            byte[] bytes = await File.ReadAllBytesAsync(
                path.Value, cancellationToken);
            Sha256Hash actual = new(
                Convert.ToHexString(SHA256.HashData(bytes)));
            if (actual != expectedHash)
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-manifest-changed",
                    "The preview manifest changed after selection."));
                return null;
            }
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling =
                        JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (document.RootElement.ValueKind !=
                    JsonValueKind.Object ||
                !TryGet(
                    document.RootElement,
                    "npcFormId",
                    out JsonElement formElement) ||
                formElement.ValueKind != JsonValueKind.String ||
                !FormId.TryParse(
                    formElement.GetString() ?? string.Empty,
                    out FormId manifestFormId))
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-manifest-formid",
                    "The preview manifest has no valid npcFormId."));
                return null;
            }
            if (manifestFormId != selectedFormId)
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-npc-mismatch",
                    $"Selected NPC '{selectedFormId}' does not match manifest NPC '{manifestFormId}'."));
                return null;
            }
            return new ManifestInspection(
                actual,
                manifestFormId,
                ReadVariants(document.RootElement, diagnostics));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            diagnostics.Add(Error(
                "main-workspace-preview-manifest-read",
                exception.Message));
            return null;
        }
    }

    private static ImmutableDictionary<string, FormReference>
        ReadVariants(
            JsonElement root,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var variants =
            ImmutableDictionary.CreateBuilder<string, FormReference>(
                StringComparer.OrdinalIgnoreCase);
        if (!TryGet(root, "variants", out JsonElement element))
            return variants.ToImmutable();
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Error(
                "main-workspace-preview-variants",
                "Manifest variants must be an array."));
            return variants.ToImmutable();
        }
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGet(item, "id", out JsonElement idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !TryGet(
                    item, "outfit", out JsonElement outfitElement) ||
                outfitElement.ValueKind != JsonValueKind.String ||
                idElement.GetString() is not { } id ||
                !FormReference.TryParse(
                    outfitElement.GetString() ?? string.Empty,
                    out FormReference outfit) ||
                string.IsNullOrWhiteSpace(id) ||
                id.Length > 128 ||
                id != id.Trim() ||
                id.Any(char.IsControl) ||
                !variants.TryAdd(id, outfit))
                diagnostics.Add(Error(
                    "main-workspace-preview-variant-invalid",
                    "Each preview variant requires one unique ID and qualified outfit."));
        }
        return variants.ToImmutable();
    }

    private async ValueTask ValidateSourceAssetsAsync(
        WorkspacePath assetRoot,
        ImmutableArray<PreviewSceneAsset> assets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(
            policy.EvaluateReadRoot(labRoot, assetRoot));
        foreach (PreviewSceneAsset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AssetPath relative = new(asset.Path);
                var full = new WorkspacePath(Path.GetFullPath(
                    Path.Combine(
                        assetRoot.Value,
                        relative.Value.Replace(
                            '/', Path.DirectorySeparatorChar))));
                if (!full.IsUnder(assetRoot) ||
                    !File.Exists(full.Value))
                {
                    diagnostics.Add(Error(
                        "main-workspace-preview-source-missing",
                        $"Preview source '{asset.Path}' is missing."));
                    continue;
                }
                diagnostics.AddRange(
                    policy.EvaluateReadRoot(assetRoot, full));
                Sha256Hash observed = await HashFileAsync(
                    full.Value, cancellationToken);
                if (!string.Equals(
                        observed.Value,
                        asset.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(Error(
                        "main-workspace-preview-source-hash",
                        $"Preview source '{asset.Path}' changed after manifest review."));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error(
                    "main-workspace-preview-source-path",
                    exception.Message));
            }
        }
    }

    private static void ValidateSelected(
        SkyrimMainWorkspaceRecord? record,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (record is null ||
            record.Kind != SkyrimMainWorkspaceRecordKind.Npc ||
            record.IsSourceDeleted ||
            !string.Equals(
                record.Identity.Signature,
                "NPC_",
                StringComparison.Ordinal))
            diagnostics.Add(Error(
                "main-workspace-preview-selection",
                "Preview and NIF export require one selected live NPC_."));
    }

    private static bool TryGet(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in
                     element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool IsHashValid(Sha256Hash hash) =>
        hash.Value is { Length: 64 } &&
        hash.Value.Any(character => character != '0');

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
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

    private static SkyrimMainWorkspacePreviewResult
        PreviewRefused(
            SkyrimMainWorkspaceRecord? selected,
            IEnumerable<Diagnostic> diagnostics,
            PreviewSceneArtifact? artifact = null) =>
        new(
            false,
            selected,
            artifact,
            null,
            null,
            null,
            null,
            false,
            diagnostics.ToImmutableArray());

    private static SkyrimMainWorkspaceNifExportResult
        NifRefused(
            IEnumerable<Diagnostic> diagnostics,
            PreviewNifBinaryExportArtifact? artifact = null) =>
        new(
            false,
            artifact,
            null,
            null,
            false,
            diagnostics.ToImmutableArray());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Only exact outputs created by this bounded call are considered.
        }
    }

    private sealed record ManifestInspection(
        Sha256Hash Sha256,
        FormId FormId,
        ImmutableDictionary<string, FormReference> Variants);
}
