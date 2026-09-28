using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Rendering;

public sealed partial class BlenderPreviewNifExporter
{
    private ImmutableArray<Diagnostic> ValidateRequest(PreviewNifBinaryExportRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!executablePath.IsUnder(labRoot) || !File.Exists(executablePath.Value))
            diagnostics.Add(new Diagnostic("preview-nif-binary-tool-invalid", DiagnosticSeverity.Error,
                "The pinned Blender executable must exist under the K-only lab root."));
        if (!profileRoot.IsUnder(labRoot) || !Directory.Exists(profileRoot.Value))
            diagnostics.Add(new Diagnostic("preview-nif-binary-profile-invalid", DiagnosticSeverity.Error,
                "The staged Blender profile must exist under the K-only lab root."));
        if (!request.ScenePath.IsUnder(labRoot) || !File.Exists(request.ScenePath.Value) ||
            !request.ScenePath.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-invalid", DiagnosticSeverity.Error,
                "Binary NIF export scenes must be existing K-local JSON artifacts."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ScenePath));
        if (!request.AssetRoot.IsUnder(labRoot) || !Directory.Exists(request.AssetRoot.Value))
            diagnostics.Add(new Diagnostic("preview-nif-binary-asset-root-invalid", DiagnosticSeverity.Error,
                "Binary NIF source roots must be existing K-local directories."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.AssetRoot));
        if (!request.OutputPath.IsUnder(labRoot) ||
            !request.OutputPath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-binary-output-invalid", DiagnosticSeverity.Error,
                "Binary NIF outputs must be new .nif files under K."));
        var parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("preview-nif-binary-output-parent", DiagnosticSeverity.Error,
                "The binary NIF output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPath.Value))
            diagnostics.Add(new Diagnostic("preview-nif-binary-output-exists", DiagnosticSeverity.Error,
                "Binary NIF outputs never overwrite existing files."));
        AddReparseDiagnostic(diagnostics, executablePath.Value, "Blender executable");
        AddReparseDiagnostic(diagnostics, profileRoot.Value, "Blender profile");
        AddReparseDiagnostic(diagnostics, request.ScenePath.Value, "preview scene");
        AddReparseDiagnostic(diagnostics, request.AssetRoot.Value, "preview asset root");
        AddReparseDiagnostic(diagnostics, request.OutputPath.Value, "binary NIF output");
        return diagnostics.ToImmutable();
    }

    private static string? ResolveAsset(WorkspacePath root, string relative,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-asset-path", DiagnosticSeverity.Error,
                "Binary NIF asset paths must be non-empty relative paths."));
            return null;
        }
        try
        {
            var full = new WorkspacePath(Path.GetFullPath(Path.Combine(root.Value,
                relative.Replace('/', Path.DirectorySeparatorChar))));
            if (!full.IsUnder(root) || !full.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(full.Value))
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-asset-missing", DiagnosticSeverity.Error,
                    $"Binary NIF asset '{relative}' must be an existing NIF under the asset root."));
                return null;
            }
            if (new FileInfo(full.Value).Length is <= 0 or > MaximumAssetBytes)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-asset-size", DiagnosticSeverity.Error,
                    $"Binary NIF asset '{relative}' is outside the accepted size bound."));
                return null;
            }
            AddReparseDiagnostic(diagnostics, full.Value, "binary NIF asset");
            return full.Value;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-asset-path", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
    }

    private static void ValidateMorphs(PreviewSceneArtifact scene,
        ImmutableArray<PreviewNifExportMorph> morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scene.Morphs.Where(item => item.Applied && item.Included))
        {
            if (!string.Equals(item.Category, "vertex", StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("preview-nif-binary-morph-category", DiagnosticSeverity.Error,
                    $"Preview NIF export supports vertex morphs only; '{item.Name}' is '{item.Category}'."));
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 255 ||
                !float.IsFinite(item.Value) || item.Value is < -1f or > 1f)
                diagnostics.Add(new Diagnostic("preview-nif-binary-morph-invalid", DiagnosticSeverity.Error,
                    "Preview NIF morph names must be non-empty and values must be finite in -1..1."));
            else if (!names.Add(item.Name))
                diagnostics.Add(new Diagnostic("preview-nif-binary-morph-duplicate", DiagnosticSeverity.Error,
                    $"Preview NIF morph '{item.Name}' occurs more than once."));
        }
        if (!morphs.IsDefaultOrEmpty && morphs.All(item => item.Value == 0f))
            diagnostics.Add(new Diagnostic("preview-nif-binary-morph-zero", DiagnosticSeverity.Error,
                "At least one included preview NIF morph must be non-zero."));
    }

    private static async ValueTask<int?> ReadStatusVertexCountAsync(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllBytesAsync(path, cancellationToken));
            if (document.RootElement.TryGetProperty("vertexCount", out JsonElement value) &&
                value.TryGetInt32(out int vertexCount) && vertexCount > 0)
                return vertexCount;
            diagnostics.Add(new Diagnostic("preview-nif-binary-status-vertex-count",
                DiagnosticSeverity.Error,
                "The preview NIF status must report a positive independently checkable vertex count."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-status-vertex-count",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        return null;
    }

}
