using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Hash-bound RaceMenu body metadata apply service. The service writes a
/// new canonical .jslot only; it never executes NiOverride or mutates a game root.</summary>
public sealed class SkyrimBodyTransformService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimBodyTransformService
{
    private static readonly Sha256Hash EmptyHash = new(new string('0', 64));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async ValueTask<SkyrimBodyTransformProposal> ApplyAsync(
        SkyrimBodyTransformApplyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        SkyrimBodyTransformCodec.ValidateReplacements(request.TransformReplacement, request.SkinReplacement, diagnostics);
        var inputBytes = Array.Empty<byte>();
        Sha256Hash inputHash = EmptyHash;
        if (!HasErrors(diagnostics))
        {
            try
            {
                inputBytes = await File.ReadAllBytesAsync(request.PresetPath.Value, cancellationToken);
                inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(inputBytes)));
                if (request.ExpectedInputSha256 is { } expected && expected != inputHash)
                    diagnostics.Add(new Diagnostic("sse-transform-stale-input", DiagnosticSeverity.Error,
                        "The RaceMenu preset hash does not match the expected input hash."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("sse-transform-read-failed", DiagnosticSeverity.Error, exception.Message));
            }
        }

        var parseDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SkyrimBodyTransformCodec.SourceSections? source = null;
        if (!HasErrors(diagnostics) && !SkyrimBodyTransformCodec.TryParseSource(inputBytes, out source, parseDiagnostics))
            source = null;
        diagnostics.AddRange(parseDiagnostics);
        if (source is null)
            return Empty(request, inputHash, diagnostics.ToImmutable());

        var effectiveTransforms = request.TransformReplacement is { } transformReplacement
            ? SkyrimBodyTransformCodec.MaterializeTransforms(transformReplacement) : source.Transforms;
        var effectiveSkins = request.SkinReplacement is { } skinReplacement
            ? SkyrimBodyTransformCodec.MaterializeSkinOverrides(skinReplacement) : source.SkinOverrides;
        if (HasErrors(diagnostics))
            return new SkyrimBodyTransformProposal(request.Edition, request.NpcFormId, request.PresetPath,
                request.OutputPath, inputHash, null, source.Transforms, source.SkinOverrides,
                effectiveTransforms, effectiveSkins, false, diagnostics.ToImmutable());

        SkyrimBodyTransformCodec.ReplaceSections(source.Root, request.TransformReplacement.HasValue,
            request.TransformReplacement.GetValueOrDefault(), request.SkinReplacement.HasValue,
            request.SkinReplacement.GetValueOrDefault(), source);
        var outputBytes = Encoding.UTF8.GetBytes(source.Root.ToJsonString(JsonOptions));
        var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes)));
        try
        {
            await WriteNewFileAsync(request.OutputPath.Value, outputBytes, cancellationToken);
            return new SkyrimBodyTransformProposal(request.Edition, request.NpcFormId, request.PresetPath,
                request.OutputPath, inputHash, outputHash, source.Transforms, source.SkinOverrides,
                effectiveTransforms, effectiveSkins, true, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("sse-transform-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new SkyrimBodyTransformProposal(request.Edition, request.NpcFormId, request.PresetPath,
                request.OutputPath, inputHash, null, source.Transforms, source.SkinOverrides,
                effectiveTransforms, effectiveSkins, false, diagnostics.ToImmutable());
        }
    }

    private void ValidateRequest(SkyrimBodyTransformApplyRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("sse-transform-game-unsupported", DiagnosticSeverity.Error,
                "RaceMenu node transforms and skin overrides are supported for Skyrim SE only."));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetPath));
        if (!string.Equals(Path.GetExtension(request.PresetPath.Value), ".jslot", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("sse-transform-input-extension", DiagnosticSeverity.Error,
                "RaceMenu body metadata input must use the .jslot extension."));
        if (!File.Exists(request.PresetPath.Value))
            diagnostics.Add(new Diagnostic("sse-transform-input-missing", DiagnosticSeverity.Error,
                "The explicit RaceMenu preset input does not exist."));
        else
        {
            var attributes = File.GetAttributes(request.PresetPath.Value);
            if (attributes.HasFlag(FileAttributes.Directory))
                diagnostics.Add(new Diagnostic("sse-transform-input-directory", DiagnosticSeverity.Error,
                    "The RaceMenu preset input must be a regular file."));
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                diagnostics.Add(new Diagnostic("sse-transform-input-reparse", DiagnosticSeverity.Error,
                    "RaceMenu preset input may not traverse a reparse point."));
            if (HasReparsePath(request.PresetPath.Value))
                diagnostics.Add(new Diagnostic("sse-transform-input-reparse", DiagnosticSeverity.Error,
                    "RaceMenu preset input may not traverse a reparse-point parent."));
            if (new FileInfo(request.PresetPath.Value).Length > SkyrimBodyTransformCodec.MaxBytes)
                diagnostics.Add(new Diagnostic("sse-transform-size-limit", DiagnosticSeverity.Error,
                    $"RaceMenu preset exceeds the {SkyrimBodyTransformCodec.MaxBytes} byte safety limit."));
        }

        if (!string.Equals(Path.GetExtension(request.OutputPath.Value), ".jslot", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("sse-transform-output-extension", DiagnosticSeverity.Error,
                "RaceMenu body metadata output must use the .jslot extension."));
        if (!request.OutputPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("sse-transform-output-outside-lab", DiagnosticSeverity.Error,
                "RaceMenu body metadata output must remain under the K-only lab root."));
        if (File.Exists(request.OutputPath.Value))
            diagnostics.Add(new Diagnostic("sse-transform-output-exists", DiagnosticSeverity.Error,
                "RaceMenu body metadata apply never overwrites an existing artifact."));
        if (string.Equals(request.PresetPath.Value, request.OutputPath.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("sse-transform-input-output-same", DiagnosticSeverity.Error,
                "RaceMenu preset input and output must be different files."));
        var parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("sse-transform-output-parent", DiagnosticSeverity.Error,
                "RaceMenu body metadata output directory must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
            if (HasReparsePath(parent))
                diagnostics.Add(new Diagnostic("sse-transform-output-reparse", DiagnosticSeverity.Error,
                    "RaceMenu output may not traverse a reparse-point parent."));
        }
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static SkyrimBodyTransformProposal Empty(SkyrimBodyTransformApplyRequest request,
        Sha256Hash inputHash, ImmutableArray<Diagnostic> diagnostics) =>
        new(request.Edition, request.NpcFormId, request.PresetPath, request.OutputPath,
            inputHash, null, [], [],
            request.TransformReplacement is { } transforms
                ? SkyrimBodyTransformCodec.MaterializeTransforms(transforms) : [],
            request.SkinReplacement is { } skins
                ? SkyrimBodyTransformCodec.MaterializeSkinOverrides(skins) : [],
            false, diagnostics);

    private static bool HasReparsePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
