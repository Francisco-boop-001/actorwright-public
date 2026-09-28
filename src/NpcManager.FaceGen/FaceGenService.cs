using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed class FaceGenService(IWorkspacePolicy policy, WorkspacePath labRoot) : IFaceGenService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxShapes = 2048;

    public ValueTask<FaceGenAnalysisResult> DiagnoseAsync(FaceGenDiagnoseRequest request, CancellationToken cancellationToken) =>
        AnalyzeAsync(request.Edition, request.ManifestPath, request.NpcFormId, strictShapes: false, cancellationToken);

    public ValueTask<FaceGenAnalysisResult> VerifyAsync(FaceGenVerifyRequest request, CancellationToken cancellationToken) =>
        AnalyzeAsync(request.Edition, request.ManifestPath, request.NpcFormId, request.StrictShapes, cancellationToken);

    private async ValueTask<FaceGenAnalysisResult> AnalyzeAsync(GameEdition edition, WorkspacePath manifestPath,
        FormId? requestedNpc, bool strictShapes, CancellationToken cancellationToken)
    {
        var diagnostics = ValidatePath(manifestPath).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return FailedResult(diagnostics.ToImmutable());

        try
        {
            var fileInfo = new FileInfo(manifestPath.Value);
            if (fileInfo.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facegen-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceGen manifest exceeds the {MaxBytes} byte safety limit."));
                return FailedResult(diagnostics.ToImmutable());
            }

            var bytes = await File.ReadAllBytesAsync(manifestPath.Value, cancellationToken);
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (!TryParseJson(bytes, out var document, diagnostics)) return FailedResult(diagnostics.ToImmutable());
            using (document)
            {
                var manifest = ParseManifest(document.RootElement, edition, sourceHash, diagnostics);
                if (manifest is null) return FailedResult(diagnostics.ToImmutable());
                if (requestedNpc is { } expected && expected != manifest.NpcFormId)
                    diagnostics.Add(new Diagnostic("facegen-npc-mismatch", DiagnosticSeverity.Error,
                        $"Manifest NPC {manifest.NpcFormId} does not match requested NPC {expected}."));

                var validHeadShapes = manifest.Shapes.Where(IsValidHeadShape).ToImmutableArray();
                if (validHeadShapes.Length == 0)
                    diagnostics.Add(new Diagnostic("facegen-zero-shapes", DiagnosticSeverity.Error,
                        "No applicable, included, non-empty head shape with a valid topology hash was found."));

                foreach (var shape in manifest.Shapes.Where(shape => shape.IncludedInOutput && shape.Role != FaceGenShapeRole.Head))
                {
                    var severity = strictShapes ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                    var code = strictShapes ? "facegen-poison-shape" : "facegen-non-head-shape";
                    diagnostics.Add(new Diagnostic(code, severity,
                        $"Shape '{shape.Name}' ({shape.Role.ToWireName()}) is included in the FaceGen output surface."));
                }

                var resultDiagnostics = diagnostics.ToImmutable();
                return new FaceGenAnalysisResult(validHeadShapes.Length > 0 &&
                    !resultDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
                    validHeadShapes.Length, manifest, validHeadShapes, resultDiagnostics);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-read-failed", DiagnosticSeverity.Error, exception.Message));
            return FailedResult(diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-read-denied", DiagnosticSeverity.Error, exception.Message));
            return FailedResult(diagnostics.ToImmutable());
        }
    }

    private static FaceGenManifest? ParseManifest(JsonElement root, GameEdition expectedEdition, Sha256Hash sourceHash,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-root-shape", DiagnosticSeverity.Error, "FaceGen manifest root must be a JSON object."));
            return null;
        }

        if (!TryGet(root, "schemaVersion", out var schemaElement) || !schemaElement.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-schema-unsupported", DiagnosticSeverity.Error, "FaceGen manifest schemaVersion must be 1."));
            return null;
        }

        if (!TryGet(root, "edition", out var editionElement) || editionElement.ValueKind != JsonValueKind.String ||
            !GameEditionExtensions.TryParseWireName(editionElement.GetString()!, out var manifestEdition))
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-edition-invalid", DiagnosticSeverity.Error, "Manifest edition must be fallout4 or skyrimse."));
            return null;
        }
        if (manifestEdition != expectedEdition)
            diagnostics.Add(new Diagnostic("facegen-manifest-edition-mismatch", DiagnosticSeverity.Error,
                $"Manifest edition {manifestEdition.ToWireName()} does not match requested edition {expectedEdition.ToWireName()}."));

        if (!TryGet(root, "npcFormId", out var formElement) || formElement.ValueKind != JsonValueKind.String ||
            !FormId.TryParse(formElement.GetString()!, out var npcFormId))
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-formid-invalid", DiagnosticSeverity.Error, "Manifest npcFormId must be a hexadecimal FormID string."));
            return null;
        }
        if (!TryGet(root, "shapes", out var shapesElement) || shapesElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-shapes-invalid", DiagnosticSeverity.Error, "Manifest shapes must be an array."));
            return null;
        }
        if (shapesElement.GetArrayLength() > MaxShapes)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-shape-limit", DiagnosticSeverity.Error, $"Manifest contains more than {MaxShapes} shapes."));
            return null;
        }

        var shapes = ImmutableArray.CreateBuilder<FaceGenShape>();
        var shapeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in shapesElement.EnumerateArray())
        {
            var shape = ParseShape(element, index, diagnostics);
            if (shape is not null)
            {
                if (!shapeNames.Add(shape.Name))
                    diagnostics.Add(new Diagnostic("facegen-shape-duplicate", DiagnosticSeverity.Error, $"Shape name '{shape.Name}' occurs more than once."));
                else shapes.Add(shape);
            }
            index++;
        }
        return new FaceGenManifest(manifestEdition, npcFormId, shapes.ToImmutable(), sourceHash);
    }

    private static FaceGenShape? ParseShape(JsonElement element, int index, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var path = $"$.shapes[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("facegen-shape-invalid", DiagnosticSeverity.Error, $"'{path}' must be an object."));
            return null;
        }
        if (!TryGetString(element, "name", out var name) || name.Length is 0 or > 255 || name.Any(char.IsControl))
        {
            diagnostics.Add(new Diagnostic("facegen-shape-name-invalid", DiagnosticSeverity.Error, $"'{path}.name' must be a safe non-empty name."));
            return null;
        }
        if (!TryGetString(element, "role", out var roleText) || !FaceGenShapeRoleExtensions.TryParseWireName(roleText, out var role))
        {
            diagnostics.Add(new Diagnostic("facegen-shape-role-invalid", DiagnosticSeverity.Error, $"'{path}.role' is not a supported shape role."));
            return null;
        }
        if (!TryGetString(element, "sourcePath", out var sourceText))
        {
            diagnostics.Add(new Diagnostic("facegen-shape-source-invalid", DiagnosticSeverity.Error, $"'{path}.sourcePath' is required."));
            return null;
        }
        AssetPath sourcePath;
        try { sourcePath = new AssetPath(sourceText); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("facegen-shape-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
        if (!TryGet(element, "vertexCount", out var vertexElement) || !vertexElement.TryGetInt32(out var vertexCount) || vertexCount < 0 || vertexCount > 10_000_000)
        {
            diagnostics.Add(new Diagnostic("facegen-shape-vertex-count-invalid", DiagnosticSeverity.Error, $"'{path}.vertexCount' must be an integer from 0 to 10000000."));
            return null;
        }
        if (!TryGetString(element, "topologySha256", out var hashText))
        {
            diagnostics.Add(new Diagnostic("facegen-shape-topology-missing", DiagnosticSeverity.Error, $"'{path}.topologySha256' is required."));
            return null;
        }
        Sha256Hash topologyHash;
        try { topologyHash = new Sha256Hash(hashText); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("facegen-shape-topology-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
        if (!TryGet(element, "applicable", out var applicableElement) || applicableElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            !TryGet(element, "includedInOutput", out var includedElement) || includedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            diagnostics.Add(new Diagnostic("facegen-shape-flags-invalid", DiagnosticSeverity.Error, $"'{path}' requires boolean applicable and includedInOutput flags."));
            return null;
        }
        return new FaceGenShape(name, role, sourcePath, vertexCount, topologyHash, applicableElement.GetBoolean(), includedElement.GetBoolean());
    }

    private ImmutableArray<Diagnostic> ValidatePath(WorkspacePath path)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facegen-manifest-outside-lab", DiagnosticSeverity.Error, "FaceGen manifests must remain under the K-only lab root."));
        if (!File.Exists(path.Value)) diagnostics.Add(new Diagnostic("facegen-manifest-missing", DiagnosticSeverity.Error, "The explicit FaceGen manifest does not exist."));
        if (File.Exists(path.Value) && File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("facegen-manifest-reparse-refused", DiagnosticSeverity.Error, "FaceGen manifest files may not be reparse points."));
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("facegen-manifest-parent-invalid", DiagnosticSeverity.Error, "FaceGen manifest has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool IsValidHeadShape(FaceGenShape shape) => shape.Role == FaceGenShapeRole.Head && shape.Applicable &&
        shape.IncludedInOutput && shape.VertexCount > 0;

    private static bool TryParseJson(ReadOnlyMemory<byte> bytes, out JsonDocument document, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        document = null!;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (HasDuplicateKeys(document.RootElement, "$", diagnostics))
            {
                document.Dispose(); document = null!; return false;
            }
            return true;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-manifest-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return false;
        }
    }

    private static bool HasDuplicateKeys(JsonElement element, string path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var found = false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    diagnostics.Add(new Diagnostic("facegen-manifest-duplicate-key", DiagnosticSeverity.Error, $"Duplicate JSON property '{path}.{property.Name}'."));
                    found = true;
                }
                found |= HasDuplicateKeys(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) found |= HasDuplicateKeys(item, $"{path}[{index++}]", diagnostics);
        }
        return found;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        }
        value = default; return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } parsed)
        { value = parsed; return true; }
        value = string.Empty; return false;
    }

    private static FaceGenAnalysisResult FailedResult(ImmutableArray<Diagnostic> diagnostics) =>
        new(false, 0, null, ImmutableArray<FaceGenShape>.Empty, diagnostics);
}
