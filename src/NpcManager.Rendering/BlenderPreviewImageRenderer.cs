using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;
using DesktopProcessIdentityId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopProcessIdentityId;

namespace NpcManager.Rendering;

/// <summary>
/// Renders imported NIF meshes through the pinned, K-local Blender/PyNifly
/// adapter. It never exports NIFs or touches a live game root.
/// </summary>
public sealed class BlenderPreviewImageRenderer(
    WorkspacePath executablePath,
    WorkspacePath profileRoot,
    EmbeddedBlenderScriptId scriptId,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash expectedExecutableSha256) : IPreviewImageRenderer
{
    private const int MaximumAssets = 64;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const long MaximumStatusBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<PreviewImageRenderResult> RenderAsync(
        PreviewImageRenderRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics);

        string? animationPath = null;
        string? skeletonPath = null;
        if (request.Animation is { } animation)
        {
            animationPath = ResolveAnimationAsset(request.AssetRoot, animation.Path, false, diagnostics);
            skeletonPath = ResolveAnimationAsset(request.AssetRoot, animation.Skeleton, true, diagnostics);
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return Refused(diagnostics);
        }

        var assets = request.Assets.Where(item => item.Included && item.Visible).ToImmutableArray();
        if (assets.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("preview-render-no-assets", DiagnosticSeverity.Error,
                "At least one visible included NIF asset is required for pixel rendering."));
            return Refused(diagnostics);
        }

        var resolved = ImmutableArray.CreateBuilder<string>();
        var resolvedHairAssets = ImmutableArray.CreateBuilder<string>();
        var resolvedFaceAssets = ImmutableArray.CreateBuilder<string>();
        foreach (var asset in assets)
        {
            var path = ResolveAsset(request.AssetRoot, asset.Path, diagnostics);
            if (path is null) continue;
            var hash = await HashFileAsync(new WorkspacePath(path), cancellationToken);
            if (!string.Equals(hash.Value, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("preview-render-asset-hash-mismatch", DiagnosticSeverity.Error,
                    $"Preview asset '{asset.Path}' does not match its manifest hash."));
            else
            {
                resolved.Add(path);
                if (string.Equals(asset.Category, "hair", StringComparison.OrdinalIgnoreCase))
                    resolvedHairAssets.Add(path);
                if (string.Equals(asset.Category, "face", StringComparison.OrdinalIgnoreCase))
                    resolvedFaceAssets.Add(path);
            }
        }
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics);

        var parent = Path.GetDirectoryName(request.OutputPath.Value)!;
        var requestPath = Path.Combine(parent, ".preview-render-request-" + Guid.NewGuid().ToString("N"));
        var statusPath = Path.Combine(parent, ".preview-render-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var payload = new
            {
                root = labRoot.Value,
                output = request.OutputPath.Value,
                edition = request.Edition.ToWireName(),
                width = request.Width,
                height = request.Height,
                assets = resolved.ToImmutable(),
                hairAssets = resolvedHairAssets.ToImmutable(),
                faceAssets = resolvedFaceAssets.ToImmutable(),
                morphs = request.Morphs.IsDefaultOrEmpty
                    ? []
                    : request.Morphs.Select(item => new { name = item.Name, value = item.Value }).ToImmutableArray(),
                animation = request.Animation is null ? null : new
                {
                    id = request.Animation.Id,
                    path = animationPath,
                    skeleton = skeletonPath,
                    frame = request.Animation.Frame,
                    timeSeconds = request.Animation.TimeSeconds,
                    playbackRate = request.Animation.PlaybackRate,
                    playing = request.Animation.Playing,
                    additive = request.Animation.Additive
                },
                hairZap = request.HairZap is null ? null : new
                {
                    renderHeadwear = request.HairZap.RenderHeadwear,
                    coveredSlots = request.HairZap.CoveredSlots,
                    top = request.HairZap.TopCovered,
                    @long = request.HairZap.LongCovered,
                    faceCull = request.HairZap.FaceGenHeadCovered
                }
            };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            var beforeHash = await HashFileAsync(executablePath, cancellationToken);
            if (beforeHash != expectedExecutableSha256)
            {
                diagnostics.Add(new Diagnostic("preview-render-tool-hash-mismatch", DiagnosticSeverity.Error,
                    "The pinned Blender executable hash does not match its admitted manifest."));
                return Refused(diagnostics);
            }

            var processResult = await RunBlenderAsync(requestPath, statusPath, cancellationToken);
            diagnostics.AddRange(processResult.Diagnostics);
            var afterHash = await HashFileAsync(executablePath, cancellationToken);
            if (afterHash != expectedExecutableSha256)
                diagnostics.Add(new Diagnostic("preview-render-tool-changed", DiagnosticSeverity.Error,
                    "The Blender executable changed while rendering."));
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return Refused(diagnostics);

            var status = await ReadStatusAsync(statusPath, diagnostics, cancellationToken);
            if (status is null || !status.Rendered)
            {
                diagnostics.Add(new Diagnostic("preview-render-failed", DiagnosticSeverity.Error,
                    status?.Error ?? "The Blender renderer did not produce a successful status."));
                return Refused(diagnostics);
            }
            if (!string.Equals(status.Output, request.OutputPath.Value, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(request.OutputPath.Value))
            {
                diagnostics.Add(new Diagnostic("preview-render-output-binding", DiagnosticSeverity.Error,
                    "The Blender status did not bind the requested output path."));
                return Refused(diagnostics);
            }
            var outputHash = await HashFileAsync(request.OutputPath, cancellationToken);
            if (!string.Equals(outputHash.Value, status.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("preview-render-output-hash", DiagnosticSeverity.Error,
                    "The rendered PNG hash differs from the renderer status."));
                return Refused(diagnostics);
            }
            if (status.Width != request.Width || status.Height != request.Height || status.MeshCount <= 0)
            {
                diagnostics.Add(new Diagnostic("preview-render-output-shape", DiagnosticSeverity.Error,
                    "The rendered PNG dimensions or imported mesh count are invalid."));
                return Refused(diagnostics);
            }
            if (!string.Equals(status.Edition, request.Edition.ToWireName(), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("preview-render-edition-status", DiagnosticSeverity.Error,
                    "The renderer status did not bind the requested game edition."));
                return Refused(diagnostics);
            }
            if (status.ArmatureCount < 0 || status.DeformationMode is not
                ("nif-skinned-evaluated" or "nif-mesh-evaluated"))
            {
                diagnostics.Add(new Diagnostic("preview-render-deformation-status", DiagnosticSeverity.Error,
                    "The renderer did not report an admitted NIF deformation mode."));
                return Refused(diagnostics);
            }
            if (request.Animation is null)
            {
                if (status.AnimationApplied || status.AnimationId is not null || status.AnimationFrame is not null)
                {
                    diagnostics.Add(new Diagnostic("preview-render-animation-unrequested", DiagnosticSeverity.Error,
                        "The renderer reported animation state for a scene without an animation request."));
                    return Refused(diagnostics);
                }
            }
            else if (!status.AnimationApplied || !string.Equals(status.AnimationId, request.Animation.Id,
                         StringComparison.OrdinalIgnoreCase) || status.AnimationFrame != request.Animation.Frame ||
                     status.ArmatureCount <= 0)
            {
                diagnostics.Add(new Diagnostic("preview-render-animation-status", DiagnosticSeverity.Error,
                    "The renderer did not prove the requested HKX animation was applied to a NIF armature."));
                return Refused(diagnostics);
            }
            var requestedMorphNames = request.Morphs.IsDefaultOrEmpty
                ? ImmutableArray<string>.Empty
                : request.Morphs.Select(item => item.Name).ToImmutableArray();
            if (!requestedMorphNames.SequenceEqual(status.MorphNames, StringComparer.OrdinalIgnoreCase) ||
                (!requestedMorphNames.IsDefaultOrEmpty && !status.MorphDeformed))
            {
                diagnostics.Add(new Diagnostic("preview-render-morph-status", DiagnosticSeverity.Error,
                    "The renderer status did not prove the requested morphs were applied and deformed the mesh."));
                return Refused(diagnostics);
            }
            if (request.HairZap is null)
            {
                if (status.HairZapApplied || status.HairZapAffectedMeshCount != 0 || status.HairZapRemovedFaceCount != 0 ||
                    status.FaceCullApplied || status.FaceCullAffectedMeshCount != 0)
                {
                    diagnostics.Add(new Diagnostic("preview-render-hair-zap-unrequested", DiagnosticSeverity.Error,
                        "The renderer reported hair-partition zapping for a scene without a hair-zap request."));
                    return Refused(diagnostics);
                }
            }
            else if (status.HairZapTop != request.HairZap.TopCovered ||
                     status.HairZapLong != request.HairZap.LongCovered ||
                     status.FaceCullApplied != request.HairZap.FaceGenHeadCovered ||
                     (request.HairZap.FaceGenHeadCovered && status.FaceCullAffectedMeshCount <= 0) ||
                     (request.HairZap.Parts != PreviewHairZapParts.None &&
                      (!status.HairZapApplied || status.HairZapAffectedMeshCount <= 0)))
            {
                diagnostics.Add(new Diagnostic("preview-render-hair-zap-status", DiagnosticSeverity.Error,
                    "The renderer did not prove the requested headwear hair-partition mask."));
                return Refused(diagnostics);
            }
            return new PreviewImageRenderResult(true,
                new PreviewRenderedImage(request.OutputPath.Value, outputHash.Value,
                    status.Width, status.Height, status.MeshCount, status.MorphNames,
                    status.MorphDeformed, status.Edition, status.ArmatureCount,
                    status.DeformationMode, status.AnimationId, status.AnimationApplied,
                     status.AnimationFrame, status.AnimationPoseDeformed,
                     status.HairZapApplied, status.HairZapTop, status.HairZapLong,
                     status.HairZapAffectedMeshCount, status.HairZapRemovedFaceCount,
                     status.FaceCullApplied, status.FaceCullAffectedMeshCount), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("preview-render-io-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("preview-render-access-denied", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(statusPath);
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(PreviewImageRenderRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!executablePath.IsUnder(labRoot) || !File.Exists(executablePath.Value))
            diagnostics.Add(new Diagnostic("preview-render-tool-invalid", DiagnosticSeverity.Error,
                "The pinned Blender executable must exist under the K-only lab root."));
        if (!profileRoot.IsUnder(labRoot) || !Directory.Exists(profileRoot.Value))
            diagnostics.Add(new Diagnostic("preview-render-profile-invalid", DiagnosticSeverity.Error,
                "The staged Blender profile must exist under the K-only lab root."));
        if (!request.AssetRoot.IsUnder(labRoot) || !Directory.Exists(request.AssetRoot.Value))
            diagnostics.Add(new Diagnostic("preview-render-asset-root-invalid", DiagnosticSeverity.Error,
                "Preview asset roots must be existing K-local directories."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.AssetRoot));
        if (!request.OutputPath.IsUnder(labRoot) ||
            !request.OutputPath.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-render-output-invalid", DiagnosticSeverity.Error,
                "Preview image outputs must be new .png files under K."));
        var parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("preview-render-output-parent-missing", DiagnosticSeverity.Error,
                "The preview image output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPath.Value))
            diagnostics.Add(new Diagnostic("preview-render-output-exists", DiagnosticSeverity.Error,
                "Preview image outputs never overwrite existing files."));
        if (request.Width is < 64 or > 2048 || request.Height is < 64 or > 2048)
            diagnostics.Add(new Diagnostic("preview-render-dimensions-invalid", DiagnosticSeverity.Error,
                "Preview image dimensions must be between 64 and 2048 pixels."));
        if (request.Assets.Length > MaximumAssets)
            diagnostics.Add(new Diagnostic("preview-render-asset-count", DiagnosticSeverity.Error,
                $"Preview rendering accepts at most {MaximumAssets} assets."));
        AddReparseDiagnostic(diagnostics, executablePath.Value, "Blender executable");
        AddReparseDiagnostic(diagnostics, profileRoot.Value, "Blender profile");
        AddReparseDiagnostic(diagnostics, request.AssetRoot.Value, "preview asset root");
        AddReparseDiagnostic(diagnostics, request.OutputPath.Value, "preview image output");
        return diagnostics.ToImmutable();
    }

    private static string? ResolveAsset(WorkspacePath root, string relative, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            diagnostics.Add(new Diagnostic("preview-render-asset-path-invalid", DiagnosticSeverity.Error,
                "Preview asset paths must be non-empty relative paths."));
            return null;
        }
        try
        {
            var full = new WorkspacePath(Path.GetFullPath(Path.Combine(root.Value,
                relative.Replace('/', Path.DirectorySeparatorChar))));
            if (!full.IsUnder(root) || !full.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(full.Value))
            {
                diagnostics.Add(new Diagnostic("preview-render-asset-missing", DiagnosticSeverity.Error,
                    $"Preview asset '{relative}' must be an existing NIF under the asset root."));
                return null;
            }
            if (new FileInfo(full.Value).Length is <= 0 or > MaximumAssetBytes)
            {
                diagnostics.Add(new Diagnostic("preview-render-asset-size", DiagnosticSeverity.Error,
                    $"Preview asset '{relative}' is outside the accepted size bound."));
                return null;
            }
            AddReparseDiagnostic(diagnostics, full.Value, "preview asset");
            return full.Value;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("preview-render-asset-path-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
    }

    private static string? ResolveAnimationAsset(WorkspacePath root, string relative, bool skeleton,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            diagnostics.Add(new Diagnostic("preview-render-animation-path-invalid", DiagnosticSeverity.Error,
                "Preview animation and skeleton paths must be non-empty relative paths."));
            return null;
        }
        try
        {
            var full = new WorkspacePath(Path.GetFullPath(Path.Combine(root.Value,
                relative.Replace('/', Path.DirectorySeparatorChar))));
            var extension = Path.GetExtension(full.Value);
            var validExtension = skeleton
                ? string.Equals(extension, ".hkx", StringComparison.OrdinalIgnoreCase)
                : string.Equals(extension, ".hkx", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(extension, ".kf", StringComparison.OrdinalIgnoreCase);
            if (!full.IsUnder(root) || !validExtension || !File.Exists(full.Value))
            {
                diagnostics.Add(new Diagnostic("preview-render-animation-missing", DiagnosticSeverity.Error,
                    $"Preview {(skeleton ? "skeleton" : "animation")} '{relative}' must be an existing K-local animation file."));
                return null;
            }
            if (new FileInfo(full.Value).Length is <= 0 or > MaximumAssetBytes)
            {
                diagnostics.Add(new Diagnostic("preview-render-animation-size", DiagnosticSeverity.Error,
                    $"Preview {(skeleton ? "skeleton" : "animation")} '{relative}' is outside the accepted size bound."));
                return null;
            }
            AddReparseDiagnostic(diagnostics, full.Value, skeleton ? "preview skeleton" : "preview animation");
            return full.Value;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("preview-render-animation-path-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
    }

    private async ValueTask<ProcessResult> RunBlenderAsync(string requestPath, string statusPath,
        CancellationToken cancellationToken)
    {
        EmbeddedBlenderInvocation invocation = EmbeddedBlenderInvocationFactory.Create(
            scriptId.Value, ["--request", requestPath, "--status", statusPath]);
        var start = new ProcessStartInfo
        {
            FileName = executablePath.Value,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in invocation.Arguments) start.ArgumentList.Add(argument);
        start.Environment["BLENDER_USER_CONFIG"] = Path.Combine(profileRoot.Value, "config");
        start.Environment["BLENDER_USER_SCRIPTS"] = Path.Combine(profileRoot.Value, "scripts");
        start.Environment["BLENDER_USER_DATA"] = Path.Combine(profileRoot.Value, "data");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The Blender renderer could not be started.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(5));
        var token = budget.Token;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                Activity.Current,
                DesktopFailureOperationId.BlenderPreviewImage,
                DesktopProcessIdentityId.Blender,
                expectedExecutableSha256.Value,
                process.ExitCode,
                "preview-render-process-failed");
            var details = string.Join(" ", new[] { output, error }
                .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
            if (details.Length > 2048) details = details[..2048];
            return new ProcessResult([new Diagnostic("preview-render-process-failed", DiagnosticSeverity.Error,
                $"Blender exited with code {process.ExitCode}: {details}")]);
        }
        return new ProcessResult([]);
    }

    private static async ValueTask<RenderStatus?> ReadStatusAsync(string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumStatusBytes)
        {
            diagnostics.Add(new Diagnostic("preview-render-status-size", DiagnosticSeverity.Error,
                "The Blender status file is outside the accepted size bound."));
            return null;
        }
        try
        {
            var status = JsonSerializer.Deserialize<RenderStatus>(
                await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
            return status;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("preview-render-status-invalid", DiagnosticSeverity.Error, exception.Message));
            return null;
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(WorkspacePath path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("preview-render-reparse-refused", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("preview-render-path-inspection-failed", DiagnosticSeverity.Error,
                    exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("preview-render-path-inspection-denied", DiagnosticSeverity.Error,
                    exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static PreviewImageRenderResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record ProcessResult(ImmutableArray<Diagnostic> Diagnostics);

    private sealed record RenderStatus(bool Rendered, string? Output, string? Sha256,
        int Width, int Height, int MeshCount, string? Error,
        ImmutableArray<string> MorphNames = default, bool MorphDeformed = false,
        string? Edition = null, int ArmatureCount = 0, string? DeformationMode = null,
        string? AnimationId = null, bool AnimationApplied = false, int? AnimationFrame = null,
        bool AnimationPoseDeformed = false,
        bool HairZapApplied = false, bool HairZapTop = false, bool HairZapLong = false,
        int HairZapAffectedMeshCount = 0, int HairZapRemovedFaceCount = 0,
        bool FaceCullApplied = false, int FaceCullAffectedMeshCount = 0);
}
