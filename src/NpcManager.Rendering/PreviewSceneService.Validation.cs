using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Rendering;

public sealed partial class PreviewSceneService
{
    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preview-output-outside-lab",
            DiagnosticSeverity.Error, "Preview outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-output-extension", DiagnosticSeverity.Error,
                "Semantic preview artifacts must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("preview-output-exists",
            DiagnosticSeverity.Error, "Preview outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("preview-output-parent-missing",
            DiagnosticSeverity.Error, "Preview output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateImageRequest(PreviewSceneRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.AssetRoot is null && request.ImageOutputPath is not null)
            diagnostics.Add(new Diagnostic("preview-render-asset-root-required", DiagnosticSeverity.Error,
                "Pixel rendering requires --asset-root alongside --image-output."));
        if (request.AssetRoot is not null && request.ImageOutputPath is null)
            diagnostics.Add(new Diagnostic("preview-render-image-output-required", DiagnosticSeverity.Error,
                "--asset-root is only valid when --image-output is supplied."));
        if (request.AssetRoot is { } assetRoot)
        {
            if (!assetRoot.IsUnder(labRoot) || !Directory.Exists(assetRoot.Value))
                diagnostics.Add(new Diagnostic("preview-render-asset-root-invalid", DiagnosticSeverity.Error,
                    "Preview asset roots must be existing K-local directories."));
            else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, assetRoot));
        }
        if (request.ImageOutputPath is { } imageOutput)
        {
            if (!imageOutput.IsUnder(labRoot) ||
                !imageOutput.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("preview-render-image-output-invalid", DiagnosticSeverity.Error,
                    "Preview image outputs must be new .png files under K."));
            if (File.Exists(imageOutput.Value))
                diagnostics.Add(new Diagnostic("preview-render-image-output-exists", DiagnosticSeverity.Error,
                    "Preview image outputs never overwrite existing files."));
            var parent = Path.GetDirectoryName(imageOutput.Value);
            if (parent is null || !Directory.Exists(parent))
                diagnostics.Add(new Diagnostic("preview-render-image-output-parent-missing", DiagnosticSeverity.Error,
                    "The preview image output directory must already exist."));
            else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
            if (request.ImageWidth is < 64 or > 2048 || request.ImageHeight is < 64 or > 2048)
                diagnostics.Add(new Diagnostic("preview-render-image-dimensions-invalid", DiagnosticSeverity.Error,
                    "Preview image dimensions must be between 64 and 2048 pixels."));
        }
        return diagnostics.ToImmutable();
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
        { value = text; return true; }
        value = string.Empty; return false;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default; return false;
    }

    internal static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("preview-manifest-duplicate-key",
                    DiagnosticSeverity.Error, $"Duplicate JSON property '{path}.{property.Name}'."));
                ValidateDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray()) ValidateDuplicateProperties(child, $"{path}[{index++}]", diagnostics);
        }
    }

    internal static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PreviewSceneResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

}
