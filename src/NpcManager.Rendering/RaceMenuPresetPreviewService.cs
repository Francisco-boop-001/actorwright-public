using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Reopens a selected RaceMenu preset and its same-stem CharGen export, then
/// renders the NIF through the pinned off-engine renderer. It never mutates the
/// active build request and never claims Skyrim runtime authority.
/// </summary>
public sealed class RaceMenuPresetPreviewService(
    IPreviewImageRenderer renderer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath cacheRoot) : IRaceMenuPresetPreviewService
{
    private const string CacheSchema = "racemenu-preset-preview-v1";
    private const long MaximumPresetBytes = 16L * 1024 * 1024;
    private const long MaximumFaceGeomBytes = 256L * 1024 * 1024;
    private const long MaximumFaceTintBytes = 128L * 1024 * 1024;
    private const long MaximumCachedImageBytes = 64L * 1024 * 1024;

    public async ValueTask<RaceMenuPresetPreviewResult> RenderAsync(
        RaceMenuPresetPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        WorkspacePath faceGeom = ChangeExtension(request.Preset, ".nif");
        WorkspacePath faceTint = ChangeExtension(request.Preset, ".dds");
        ValidateBoundFile(request.Preset, MaximumPresetBytes, "preset", diagnostics);
        ValidateBoundFile(faceGeom, MaximumFaceGeomBytes, "CharGen NIF", diagnostics);
        ValidateBoundFile(faceTint, MaximumFaceTintBytes, "CharGen DDS", diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash presetHash = await HashAsync(request.Preset, cancellationToken)
            .ConfigureAwait(false);
        if (presetHash != request.ExpectedPresetSha256)
        {
            diagnostics.Add(Error("preset-preview-preset-stale",
                "The selected .jslot changed after the catalog was loaded."));
            return Refused(diagnostics);
        }

        Sha256Hash faceGeomHash = await HashAsync(faceGeom, cancellationToken)
            .ConfigureAwait(false);
        Sha256Hash faceTintHash = await HashAsync(faceTint, cancellationToken)
            .ConfigureAwait(false);
        var companion = new RaceMenuPresetCompanionExport(
            request.Preset, presetHash, faceGeom, faceGeomHash, faceTint, faceTintHash);

        if (!EnsureCacheRoot(diagnostics))
            return Refused(diagnostics, companion);

        string cacheKey = CacheKey(request, faceGeomHash, faceTintHash);
        var output = new WorkspacePath(Path.Combine(cacheRoot.Value,
            $"racemenu-{cacheKey}.png"));
        if (File.Exists(output.Value))
        {
            PreviewRenderedImage? cached = await ReadCachedImageAsync(
                output, request.Width, request.Height, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                diagnostics.Add(new Diagnostic("preset-preview-cache-hit",
                    DiagnosticSeverity.Info,
                    "Reused the exact hash-bound off-engine CharGen preview."));
                return new RaceMenuPresetPreviewResult(
                    true, companion, cached, diagnostics.ToImmutable());
            }

            diagnostics.Add(Error("preset-preview-cache-invalid",
                "The deterministic preview cache entry exists but is not a valid PNG for this request."));
            return Refused(diagnostics, companion);
        }

        string relativeNif = Path.GetFileName(faceGeom.Value);
        var asset = new PreviewSceneAsset(
            "face", relativeNif, "same-stem-charGen-export",
            faceGeomHash.Value, Visible: true, Included: true);
        PreviewImageRenderResult rendered = await renderer.RenderAsync(
            new PreviewImageRenderRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(Path.GetDirectoryName(faceGeom.Value)!),
                output,
                [asset],
                request.Width,
                request.Height),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(rendered.Diagnostics);
        if (!rendered.Rendered || rendered.Image is null || HasErrors(diagnostics))
            return Refused(diagnostics, companion);

        return new RaceMenuPresetPreviewResult(
            true, companion, rendered.Image, diagnostics.ToImmutable());
    }

    private void ValidateRequest(
        RaceMenuPresetPreviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.Preset.IsUnder(labRoot) ||
            !request.Preset.Value.EndsWith(".jslot", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("preset-preview-source-invalid",
                "The preview source must be a K-local RaceMenu .jslot."));
        }
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Preset));
        }

        if (!cacheRoot.IsUnder(labRoot))
        {
            diagnostics.Add(Error("preset-preview-cache-root",
                "The preview cache must remain under the K-only lab root."));
        }
        if (request.Width is < 64 or > 1024 || request.Height is < 64 or > 1024)
        {
            diagnostics.Add(Error("preset-preview-dimensions",
                "Preset preview dimensions must be between 64 and 1024 pixels."));
        }
    }

    private static WorkspacePath ChangeExtension(WorkspacePath source, string extension) =>
        new(Path.ChangeExtension(source.Value, extension));

    private static void ValidateBoundFile(
        WorkspacePath path,
        long maximumBytes,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!File.Exists(path.Value))
            {
                diagnostics.Add(Error("preset-preview-companion-missing",
                    $"The selected preset has no same-stem {role} at '{path.Value}'."));
                return;
            }
            if (HasReparsePoint(path.Value))
            {
                diagnostics.Add(Error("preset-preview-reparse-refused",
                    $"The {role} path traverses a reparse point."));
                return;
            }
            long length = new FileInfo(path.Value).Length;
            if (length <= 0 || length > maximumBytes)
            {
                diagnostics.Add(Error("preset-preview-companion-size",
                    $"The {role} is outside the accepted byte bound."));
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("preset-preview-companion-inspection",
                $"The {role} could not be inspected: {exception.Message}"));
        }
    }

    private bool EnsureCacheRoot(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, cacheRoot));
            if (HasErrors(diagnostics)) return false;
            Directory.CreateDirectory(cacheRoot.Value);
            if (HasReparsePoint(cacheRoot.Value))
            {
                diagnostics.Add(Error("preset-preview-cache-reparse",
                    "The preview cache traverses a reparse point."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("preset-preview-cache-create",
                $"The K-local preview cache could not be prepared: {exception.Message}"));
            return false;
        }
    }

    private static async ValueTask<PreviewRenderedImage?> ReadCachedImageAsync(
        WorkspacePath path,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        if (HasReparsePoint(path.Value)) return null;
        var info = new FileInfo(path.Value);
        if (info.Length is < 24 or > MaximumCachedImageBytes) return null;

        var header = new byte[24];
        await using (var stream = new FileStream(path.Value, FileMode.Open,
                         FileAccess.Read, FileShare.Read, 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(header, cancellationToken)
                .ConfigureAwait(false);
        }
        ReadOnlySpan<byte> png = header;
        if (!png[..8].SequenceEqual(new byte[]
            { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
            png.Slice(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
            png.Slice(20, 4));
        if (width != expectedWidth || height != expectedHeight) return null;

        Sha256Hash hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        return new PreviewRenderedImage(
            path.Value, hash.Value, width, height, 1,
            Edition: GameEdition.SkyrimSpecialEdition.ToWireName(),
            DeformationMode: "cached-static-charGen-preview");
    }

    private static async ValueTask<Sha256Hash> HashAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path.Value, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static string CacheKey(
        RaceMenuPresetPreviewRequest request,
        Sha256Hash faceGeomHash,
        Sha256Hash faceTintHash)
    {
        string authority = string.Join('|',
            CacheSchema,
            request.ExpectedPresetSha256.Value,
            faceGeomHash.Value,
            faceTintHash.Value,
            request.Width,
            request.Height);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)))[..32];
    }

    private static bool HasReparsePoint(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static RaceMenuPresetPreviewResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        RaceMenuPresetCompanionExport? companion = null) =>
        new(false, companion, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
