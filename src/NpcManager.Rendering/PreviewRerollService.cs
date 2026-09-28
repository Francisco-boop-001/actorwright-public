using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>Chooses a validated preview variant with a portable deterministic seed.</summary>
public sealed class PreviewRerollService(IWorkspacePolicy policy, WorkspacePath labRoot) : IPreviewRerollService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<PreviewRerollResult> RerollAsync(PreviewRerollRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ManifestPath));
        if (!string.Equals(Path.GetExtension(request.ManifestPath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-reroll-manifest-extension", DiagnosticSeverity.Error,
                "Preview reroll manifests must use the .json extension."));
        if (!File.Exists(request.ManifestPath.Value))
            diagnostics.Add(new Diagnostic("preview-reroll-manifest-missing", DiagnosticSeverity.Error,
                "The preview reroll manifest does not exist."));
        else if (File.GetAttributes(request.ManifestPath.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("preview-reroll-manifest-reparse-refused", DiagnosticSeverity.Error,
                "Preview reroll manifests may not be reparse points."));
        if (PreviewSceneService.HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ManifestPath.Value);
            if (info.Length > PreviewSceneService.MaxBytes)
            {
                diagnostics.Add(new Diagnostic("preview-reroll-manifest-size-limit", DiagnosticSeverity.Error,
                    $"Preview reroll manifests may not exceed {PreviewSceneService.MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-reroll-manifest-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        (string NpcFormId, ImmutableArray<PreviewSceneInputAsset> Assets,
            ImmutableArray<PreviewSceneInputMorph> Morphs, ImmutableArray<PreviewSceneInputVariant> Variants) manifest;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            PreviewSceneService.ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            manifest = PreviewSceneService.ReadManifest(document.RootElement, request.Edition, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("preview-reroll-manifest-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (PreviewSceneService.HasErrors(diagnostics)) return Refused(diagnostics, inputHash);
        if (!string.Equals(manifest.NpcFormId, request.NpcFormId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("preview-reroll-npc-mismatch", DiagnosticSeverity.Error,
                $"Preview reroll NPC '{request.NpcFormId}' does not match manifest NPC '{manifest.NpcFormId}'."));
            return Refused(diagnostics, inputHash);
        }

        var candidates = manifest.Variants.Select((variant, index) => new PreviewRerollCandidate(
            variant.Id, variant.Outfit.ToString(), index, true)).ToImmutableArray();
        if (candidates.Length == 0)
        {
            diagnostics.Add(new Diagnostic("preview-reroll-no-candidates", DiagnosticSeverity.Error,
                "Preview reroll requires at least one validated manifest variant."));
            return Refused(diagnostics, inputHash);
        }

        var selectedIndex = SelectIndex(request.Seed, candidates.Length);
        var selected = candidates[selectedIndex];
        var artifact = new PreviewRerollArtifact("1", "preview-reroll-semantic-build",
            request.Edition.ToWireName(), request.NpcFormId.ToString(), request.Seed,
            inputHash.Value, candidates.Length, candidates.Count(item => item.Eligible), selectedIndex,
            selected.Id, selected.Outfit, candidates);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        try
        {
            await PreviewArtifactFileWriter.WriteAsync(request.OutputPath, outputBytes, cancellationToken);
            return new PreviewRerollResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-reroll-output-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PreviewRerollResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preview-reroll-output-outside-lab",
            DiagnosticSeverity.Error, "Preview reroll outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-reroll-output-extension", DiagnosticSeverity.Error,
                "Semantic preview reroll artifacts must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("preview-reroll-output-exists",
            DiagnosticSeverity.Error, "Preview reroll outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("preview-reroll-output-parent-missing",
            DiagnosticSeverity.Error, "Preview reroll output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static int SelectIndex(long seed, int count)
    {
        var value = unchecked((ulong)seed) + 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (int)(value % (ulong)count);
    }

    private static PreviewRerollResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

}
