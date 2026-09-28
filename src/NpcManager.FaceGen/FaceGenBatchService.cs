using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>Runs strict FaceGen analysis for an explicit ordered batch manifest.</summary>
public sealed class FaceGenBatchService(IFaceGenService faceGenService, IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenBatchService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenBatchResult> BuildAsync(FaceGenBatchRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.BatchManifestPath));
        if (!string.Equals(Path.GetExtension(request.BatchManifestPath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-batch-manifest-extension", DiagnosticSeverity.Error,
                "FaceGen batch manifests must use the .json extension."));
        if (!File.Exists(request.BatchManifestPath.Value))
            diagnostics.Add(new Diagnostic("facegen-batch-manifest-missing", DiagnosticSeverity.Error,
                "The FaceGen batch manifest does not exist."));
        else if (File.GetAttributes(request.BatchManifestPath.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("facegen-batch-manifest-reparse-refused", DiagnosticSeverity.Error,
                "FaceGen batch manifest files may not be reparse points."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.BatchManifestPath.Value);
            if (info.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facegen-batch-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceGen batch manifests may not exceed {MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.BatchManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facegen-batch-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        ImmutableArray<string> paths;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            paths = ReadManifestPaths(document.RootElement, request.Edition, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-batch-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var entries = ImmutableArray.CreateBuilder<FaceGenBatchEntry>(paths.Length);
        foreach (var pathText in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FaceGenBatchEntry entry;
            try
            {
                var analysis = await faceGenService.VerifyAsync(new FaceGenVerifyRequest(request.Edition,
                    new WorkspacePath(pathText), null, request.StrictShapes), cancellationToken);
                var entryDiagnostics = analysis.Diagnostics;
                var status = analysis.Manifest is not null && analysis.IsApplicable
                    ? FaceGenBatchEntryStatus.Passed
                    : IsSkipped(entryDiagnostics)
                        ? FaceGenBatchEntryStatus.Skipped
                        : FaceGenBatchEntryStatus.Failed;
                if (status == FaceGenBatchEntryStatus.Skipped)
                    entryDiagnostics = DowngradeSkip(entryDiagnostics);
                entry = new FaceGenBatchEntry(pathText, analysis.Manifest?.NpcFormId.ToString(), status,
                    analysis.ValidHeadShapeCount, entryDiagnostics);
            }
            catch (ArgumentException exception)
            {
                entry = new FaceGenBatchEntry(pathText, null, FaceGenBatchEntryStatus.Failed, 0,
                    [new Diagnostic("facegen-batch-item-path-invalid", DiagnosticSeverity.Error, exception.Message)]);
            }
            entries.Add(entry);
            diagnostics.AddRange(entry.Diagnostics);
        }

        var entryArray = entries.ToImmutable();
        var artifact = new FaceGenBatchArtifact("1", "facegen-batch-semantic-build",
            request.Edition.ToWireName(), inputHash.Value, entryArray.Length,
            entryArray.Count(item => item.Status == FaceGenBatchEntryStatus.Passed),
            entryArray.Count(item => item.Status == FaceGenBatchEntryStatus.Skipped),
            entryArray.Count(item => item.Status == FaceGenBatchEntryStatus.Failed), entryArray);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, outputBytes, cancellationToken);
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            var hasFailures = artifact.Failed > 0;
            if (hasFailures)
                diagnostics.Add(new Diagnostic("facegen-batch-items-failed", DiagnosticSeverity.Error,
                    $"{artifact.Failed} of {artifact.Attempted} FaceGen batch items failed."));
            return new FaceGenBatchResult(true, hasFailures, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("facegen-batch-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new FaceGenBatchResult(false, artifact.Failed > 0, artifact, null, diagnostics.ToImmutable());
        }
    }

    private static ImmutableArray<string> ReadManifestPaths(JsonElement root, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("facegen-batch-root", DiagnosticSeverity.Error,
                "FaceGen batch manifest must contain an object."));
            return [];
        }
        if (!TryGet(root, "schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
            diagnostics.Add(new Diagnostic("facegen-batch-schema", DiagnosticSeverity.Error,
                "FaceGen batch schemaVersion must be 1."));
        if (!TryGet(root, "edition", out var editionElement) || editionElement.ValueKind != JsonValueKind.String ||
            !GameEditionExtensions.TryParseWireName(editionElement.GetString() ?? string.Empty, out var manifestEdition) ||
            manifestEdition != edition)
            diagnostics.Add(new Diagnostic("facegen-batch-edition-mismatch", DiagnosticSeverity.Error,
                "FaceGen batch edition must match --edition|--game."));
        if (!TryGet(root, "manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array ||
            manifests.GetArrayLength() is 0 or > MaxEntries)
        {
            diagnostics.Add(new Diagnostic("facegen-batch-list-invalid", DiagnosticSeverity.Error,
                $"manifests must be a non-empty array with at most {MaxEntries} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifests.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } rawValue)
            {
                diagnostics.Add(new Diagnostic("facegen-batch-item-invalid", DiagnosticSeverity.Error,
                    "Batch manifest entries must be unique, non-empty JSON strings."));
                continue;
            }
            var value = rawValue.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                diagnostics.Add(new Diagnostic("facegen-batch-item-invalid", DiagnosticSeverity.Error,
                    "Batch manifest entries must be unique, non-empty JSON strings."));
                continue;
            }
            result.Add(value);
        }
        return result.ToImmutable();
    }

    private static bool IsSkipped(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Code == "facegen-zero-shapes") &&
        diagnostics.Where(item => item.Code != "facegen-zero-shapes").All(item => item.Severity != DiagnosticSeverity.Error);

    private static ImmutableArray<Diagnostic> DowngradeSkip(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Select(item => item.Code == "facegen-zero-shapes"
            ? new Diagnostic(item.Code, DiagnosticSeverity.Warning, item.Message) : item).ToImmutableArray();

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facegen-batch-output-outside-lab",
            DiagnosticSeverity.Error, "FaceGen batch outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-batch-output-extension", DiagnosticSeverity.Error,
                "FaceGen batch artifacts must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facegen-batch-output-exists",
            DiagnosticSeverity.Error, "FaceGen batch outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facegen-batch-output-parent-missing",
            DiagnosticSeverity.Error, "FaceGen batch output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default; return false;
    }

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("facegen-batch-duplicate-key",
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

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGenBatchResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, true, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
