using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Builds a deterministic semantic FaceGeom artifact from a validated manifest.
/// A vetted NIF codec is intentionally not hidden behind this boundary: until one
/// is admitted, the command writes an auditable JSON build plan and never claims a
/// game-loadable mesh.
/// </summary>
public sealed class FaceGeomBuildService(IFaceGenService faceGenService, IWorkspacePolicy policy, WorkspacePath labRoot)
    : IFaceGeomBuildService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxMetadataItems = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGeomBuildResult> BuildAsync(FaceGeomBuildRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        var analysis = await faceGenService.VerifyAsync(
            new FaceGenVerifyRequest(request.Edition, request.ManifestPath, request.NpcFormId, request.StrictShapes), cancellationToken);
        diagnostics.AddRange(analysis.Diagnostics);
        if (analysis.Manifest is null || !analysis.IsApplicable || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new FaceGeomBuildResult(false, null, null, diagnostics.ToImmutable());

        var metadata = await ReadMetadataAsync(request.ManifestPath, cancellationToken);
        diagnostics.AddRange(metadata.Diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new FaceGeomBuildResult(false, null, null, diagnostics.ToImmutable());

        var accepted = analysis.AcceptedShapes.Select(shape => shape.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var artifact = new FaceGeomBuildArtifact(
            "1",
            "facegeom-semantic-build",
            request.Edition.ToWireName(),
            analysis.Manifest.NpcFormId.ToString(),
            analysis.Manifest.SourceHash.Value,
            analysis.Manifest.Shapes.OrderBy(shape => shape.Name, StringComparer.Ordinal)
                .Select(shape => new FaceGeomBuildShape(shape.Name, shape.Role, shape.SourcePath.Value,
                    shape.VertexCount, shape.TopologySha256.Value, accepted.Contains(shape.Name), Decision(shape, accepted)))
                .ToImmutableArray(),
            metadata.HeadParts,
            metadata.Morphs,
            metadata.TextureRoutes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            await using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                await handle.FlushAsync(cancellationToken);
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            return new FaceGeomBuildResult(true, artifact, outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("facegeom-build-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new FaceGeomBuildResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private static async ValueTask<MetadataResult> ReadMetadataAsync(WorkspacePath path, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            var fileInfo = new FileInfo(path.Value);
            if (fileInfo.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facegeom-build-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceGeom build manifest exceeds the {MaxBytes} byte safety limit."));
                return new MetadataResult(ImmutableArray<FaceGeomHeadPart>.Empty,
                    ImmutableArray<FaceGeomMorphApplication>.Empty, ImmutableArray<FaceGeomTextureRoute>.Empty,
                    diagnostics.ToImmutable());
            }

            var bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var root = document.RootElement;
            var heads = ReadHeadParts(root, diagnostics);
            var morphs = ReadMorphs(root, diagnostics);
            var textures = ReadTextureRoutes(root, diagnostics);
            return new MetadataResult(heads, morphs, textures, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-manifest-json-invalid", DiagnosticSeverity.Error, exception.Message));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-manifest-read-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-manifest-read-denied", DiagnosticSeverity.Error, exception.Message));
        }

        return new MetadataResult(ImmutableArray<FaceGeomHeadPart>.Empty,
            ImmutableArray<FaceGeomMorphApplication>.Empty, ImmutableArray<FaceGeomTextureRoute>.Empty,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<FaceGeomHeadPart> ReadHeadParts(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "headParts", out var element)) return ImmutableArray<FaceGeomHeadPart>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxMetadataItems)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-headparts-invalid", DiagnosticSeverity.Error,
                $"'headParts' must be an array with at most {MaxMetadataItems} entries."));
            return ImmutableArray<FaceGeomHeadPart>.Empty;
        }
        var result = ImmutableArray.CreateBuilder<FaceGeomHeadPart>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.headParts[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "editorId", out var editorId) ||
                editorId.Length is 0 or > 255 || !TryGet(item, "partType", out var typeElement) ||
                !typeElement.TryGetInt32(out var partType) || partType is < 0 or > 9 ||
                !TryGetString(item, "meshPath", out var meshPath))
            {
                diagnostics.Add(new Diagnostic("facegeom-build-headpart-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires editorId, partType 0..9, and a normalized meshPath."));
                continue;
            }
            try
            {
                var asset = new AssetPath(meshPath);
                if (!names.Add(editorId)) diagnostics.Add(new Diagnostic("facegeom-build-headpart-duplicate", DiagnosticSeverity.Error,
                    $"Headpart '{editorId}' occurs more than once."));
                else result.Add(new FaceGeomHeadPart(editorId, partType, asset.Value));
            }
            catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("facegeom-build-headpart-invalid", DiagnosticSeverity.Error, exception.Message)); }
        }
        return result.OrderBy(item => item.EditorId, StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<FaceGeomMorphApplication> ReadMorphs(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "morphs", out var element)) return ImmutableArray<FaceGeomMorphApplication>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxMetadataItems)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-morphs-invalid", DiagnosticSeverity.Error,
                $"'morphs' must be an array with at most {MaxMetadataItems} entries."));
            return ImmutableArray<FaceGeomMorphApplication>.Empty;
        }
        var result = ImmutableArray.CreateBuilder<FaceGeomMorphApplication>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.morphs[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "name", out var name) ||
                name.Length is 0 or > 255 || !TryGet(item, "value", out var valueElement) ||
                !valueElement.TryGetSingle(out var value) || !float.IsFinite(value))
            {
                diagnostics.Add(new Diagnostic("facegeom-build-morph-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires a non-empty name and finite value."));
                continue;
            }
            if (!names.Add(name)) diagnostics.Add(new Diagnostic("facegeom-build-morph-duplicate", DiagnosticSeverity.Error,
                $"Morph '{name}' occurs more than once."));
            else result.Add(new FaceGeomMorphApplication(name, value));
        }
        return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<FaceGeomTextureRoute> ReadTextureRoutes(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "textureRoutes", out var element)) return ImmutableArray<FaceGeomTextureRoute>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxMetadataItems)
        {
            diagnostics.Add(new Diagnostic("facegeom-build-texture-routes-invalid", DiagnosticSeverity.Error,
                $"'textureRoutes' must be an array with at most {MaxMetadataItems} entries."));
            return ImmutableArray<FaceGeomTextureRoute>.Empty;
        }
        var result = ImmutableArray.CreateBuilder<FaceGeomTextureRoute>();
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.textureRoutes[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "slot", out var slot) ||
                slot.Length is 0 or > 64 || !TryGetString(item, "path", out var texturePath) ||
                !TryGetString(item, "provider", out var provider) || provider.Length is 0 or > 255)
            {
                diagnostics.Add(new Diagnostic("facegeom-build-texture-route-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires slot, normalized path, and provider."));
                continue;
            }
            try
            {
                var asset = new AssetPath(texturePath);
                if (!slots.Add(slot)) diagnostics.Add(new Diagnostic("facegeom-build-texture-route-duplicate", DiagnosticSeverity.Error,
                    $"Texture slot '{slot}' occurs more than once."));
                else result.Add(new FaceGeomTextureRoute(slot, asset.Value, provider));
            }
            catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("facegeom-build-texture-route-invalid", DiagnosticSeverity.Error, exception.Message)); }
        }
        return result.OrderBy(item => item.Slot, StringComparer.Ordinal).ToImmutableArray();
    }

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facegeom-build-output-outside-lab", DiagnosticSeverity.Error, "FaceGeom build outputs must remain under the K-only lab root."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facegeom-build-output-exists", DiagnosticSeverity.Error, "FaceGeom build outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facegeom-build-output-parent-missing", DiagnosticSeverity.Error, "FaceGeom build output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static string Decision(FaceGenShape shape, ImmutableHashSet<string> accepted) =>
        accepted.Contains(shape.Name) ? "included-head-shape" : shape.Role != FaceGenShapeRole.Head ? "excluded-non-head-role" :
        !shape.Applicable ? "excluded-not-applicable" : !shape.IncludedInOutput ? "excluded-not-requested" : "excluded-invalid-head-shape";

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } parsed)
        { value = parsed.Trim(); return true; }
        value = string.Empty; return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record MetadataResult(ImmutableArray<FaceGeomHeadPart> HeadParts,
        ImmutableArray<FaceGeomMorphApplication> Morphs, ImmutableArray<FaceGeomTextureRoute> TextureRoutes,
        ImmutableArray<Diagnostic> Diagnostics);
}
