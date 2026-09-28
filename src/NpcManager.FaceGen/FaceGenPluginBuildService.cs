using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>Builds a deterministic semantic report for one winning-plugin target.</summary>
public sealed class FaceGenPluginBuildService(IFaceGenService faceGenService, IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenPluginBuildService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenPluginBuildResult> BuildAsync(FaceGenPluginBuildRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.TargetManifestPath));
        if (!string.Equals(Path.GetExtension(request.TargetManifestPath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-extension", DiagnosticSeverity.Error,
                "FaceGen plugin-target manifests must use the .json extension."));
        if (!File.Exists(request.TargetManifestPath.Value))
            diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-missing", DiagnosticSeverity.Error,
                "The FaceGen plugin-target manifest does not exist."));
        else if (File.GetAttributes(request.TargetManifestPath.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-reparse-refused", DiagnosticSeverity.Error,
                "FaceGen plugin-target manifest files may not be reparse points."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.TargetManifestPath.Value);
            if (info.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceGen plugin-target manifests may not exceed {MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.TargetManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        ImmutableArray<FaceGenPluginTargetEntry> entries;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            entries = ReadEntries(document.RootElement, request, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-plugin-target-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var selected = entries.Where(item => item.WinningPlugin == request.TargetPlugin).ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("facegen-plugin-target-no-matches", DiagnosticSeverity.Error,
                $"No NPC entries in the target manifest belong to '{request.TargetPlugin}'."));
            return Refused(diagnostics, inputHash);
        }

        var results = ImmutableArray.CreateBuilder<FaceGenPluginTargetEntryResult>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.WinningPlugin != request.TargetPlugin)
            {
                var excluded = ImmutableArray.Create(new Diagnostic("facegen-plugin-target-excluded",
                    DiagnosticSeverity.Info,
                    $"NPC {entry.NpcFormId} is won by '{entry.WinningPlugin}', not the selected target plugin."));
                results.Add(new FaceGenPluginTargetEntryResult(FaceGenPluginTargetDisposition.Excluded,
                    FaceGenBatchEntryStatus.Skipped, entry.WinningPlugin.Value, entry.NpcFormId.ToString(), entry.ManifestPath,
                    null, 0, excluded));
                diagnostics.AddRange(excluded);
                continue;
            }

            FaceGenPluginTargetEntryResult result;
            try
            {
                var analysis = await faceGenService.VerifyAsync(new FaceGenVerifyRequest(request.Edition,
                    new WorkspacePath(entry.ManifestPath), entry.NpcFormId, request.StrictShapes), cancellationToken);
                var status = analysis.Manifest is not null && analysis.IsApplicable
                    ? FaceGenBatchEntryStatus.Passed
                    : IsSkippable(analysis.Diagnostics)
                        ? FaceGenBatchEntryStatus.Skipped
                        : FaceGenBatchEntryStatus.Failed;
                var entryDiagnostics = status == FaceGenBatchEntryStatus.Skipped
                    ? DowngradeZeroShape(analysis.Diagnostics)
                    : analysis.Diagnostics;
                result = new FaceGenPluginTargetEntryResult(FaceGenPluginTargetDisposition.Selected, status,
                    entry.WinningPlugin.Value, entry.NpcFormId.ToString(), entry.ManifestPath,
                    DeriveOutputPath(request.TargetPlugin, entry.NpcFormId), analysis.ValidHeadShapeCount,
                    entryDiagnostics);
            }
            catch (ArgumentException exception)
            {
                result = new FaceGenPluginTargetEntryResult(FaceGenPluginTargetDisposition.Selected,
                    FaceGenBatchEntryStatus.Failed, entry.WinningPlugin.Value, entry.NpcFormId.ToString(), entry.ManifestPath,
                    DeriveOutputPath(request.TargetPlugin, entry.NpcFormId), 0,
                    [new Diagnostic("facegen-plugin-target-item-path-invalid", DiagnosticSeverity.Error,
                        exception.Message)]);
            }
            results.Add(result);
            diagnostics.AddRange(result.Diagnostics);
        }

        var resultArray = results.ToImmutable();
        var artifact = new FaceGenPluginBuildArtifact("1", "facegen-plugin-target-semantic-build",
            request.Edition.ToWireName(), request.TargetPlugin.Value, inputHash.Value, selected.Length,
            selected.Length, entries.Length - selected.Length,
            resultArray.Count(item => item.Disposition == FaceGenPluginTargetDisposition.Selected &&
                item.Status == FaceGenBatchEntryStatus.Passed),
            resultArray.Count(item => item.Disposition == FaceGenPluginTargetDisposition.Selected &&
                item.Status == FaceGenBatchEntryStatus.Skipped),
            resultArray.Count(item => item.Disposition == FaceGenPluginTargetDisposition.Selected &&
                item.Status == FaceGenBatchEntryStatus.Failed), resultArray);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, outputBytes, cancellationToken);
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            var hasFailures = artifact.Failed > 0;
            if (hasFailures)
                diagnostics.Add(new Diagnostic("facegen-plugin-target-items-failed", DiagnosticSeverity.Error,
                    $"{artifact.Failed} of {artifact.Attempted} selected FaceGen items failed."));
            return new FaceGenPluginBuildResult(true, hasFailures, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("facegen-plugin-target-write-failed", DiagnosticSeverity.Error,
                exception.Message));
            return new FaceGenPluginBuildResult(false, artifact.Failed > 0, artifact, null, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<FaceGenPluginTargetEntry> ReadEntries(JsonElement root,
        FaceGenPluginBuildRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("facegen-plugin-target-root", DiagnosticSeverity.Error,
                "FaceGen plugin-target manifests must contain an object."));
            return [];
        }
        if (!TryGet(root, "schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
            diagnostics.Add(new Diagnostic("facegen-plugin-target-schema", DiagnosticSeverity.Error,
                "FaceGen plugin-target schemaVersion must be 1."));
        if (!TryGet(root, "edition", out var editionElement) || editionElement.ValueKind != JsonValueKind.String ||
            !GameEditionExtensions.TryParseWireName(editionElement.GetString() ?? string.Empty, out var edition) ||
            edition != request.Edition)
            diagnostics.Add(new Diagnostic("facegen-plugin-target-edition-mismatch", DiagnosticSeverity.Error,
                "FaceGen plugin-target edition must match --edition|--game."));
        if (!TryGet(root, "targetPlugin", out var targetElement) || targetElement.ValueKind != JsonValueKind.String ||
            !TryPlugin(targetElement.GetString(), out var manifestTarget) || manifestTarget != request.TargetPlugin)
            diagnostics.Add(new Diagnostic("facegen-plugin-target-plugin-mismatch", DiagnosticSeverity.Error,
                "FaceGen plugin-target targetPlugin must match --plugin|--target-plugin."));
        if (!TryGet(root, "entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array ||
            entriesElement.GetArrayLength() is 0 or > MaxEntries)
        {
            diagnostics.Add(new Diagnostic("facegen-plugin-target-list-invalid", DiagnosticSeverity.Error,
                $"entries must be a non-empty array with at most {MaxEntries} entries."));
            return [];
        }

        var result = ImmutableArray.CreateBuilder<FaceGenPluginTargetEntry>();
        var selectedIds = new HashSet<uint>();
        var index = 0;
        foreach (var element in entriesElement.EnumerateArray())
        {
            var path = $"$.entries[{index++}]";
            if (element.ValueKind != JsonValueKind.Object ||
                !TryGetString(element, "winningPlugin", out var pluginText) || !TryPlugin(pluginText, out var plugin) ||
                !TryGetString(element, "npcFormId", out var formText) || !FormId.TryParse(formText, out var formId) ||
                !TryGetString(element, "manifestPath", out var manifestPath) ||
                !Path.IsPathFullyQualified(manifestPath.Trim()) || manifestPath.Contains('\0'))
            {
                diagnostics.Add(new Diagnostic("facegen-plugin-target-entry-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires safe winningPlugin, npcFormId, and fully qualified manifestPath values."));
                continue;
            }
            string normalizedPath;
            try { normalizedPath = Path.GetFullPath(manifestPath.Trim()); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("facegen-plugin-target-entry-path-invalid", DiagnosticSeverity.Error,
                    $"{path}.manifestPath is invalid: {exception.Message}"));
                continue;
            }
            if (!new WorkspacePath(normalizedPath).IsUnder(labRoot))
            {
                diagnostics.Add(new Diagnostic("facegen-plugin-target-entry-outside-lab", DiagnosticSeverity.Error,
                    $"'{path}.manifestPath' must remain under the K-only lab root."));
                continue;
            }
            if (plugin == request.TargetPlugin && !selectedIds.Add(formId.Value))
            {
                diagnostics.Add(new Diagnostic("facegen-plugin-target-duplicate-formid", DiagnosticSeverity.Error,
                    $"Selected target plugin contains duplicate NPC FormID {formId}."));
                continue;
            }
            result.Add(new FaceGenPluginTargetEntry(plugin, formId, normalizedPath));
        }
        return result.ToImmutable();
    }

    private static bool TryPlugin(string? text, out PluginName plugin)
    {
        try
        {
            plugin = new PluginName(text ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            plugin = default;
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string DeriveOutputPath(PluginName targetPlugin, FormId formId) =>
        $"FaceGen/{Path.GetFileNameWithoutExtension(targetPlugin.Value)}/{formId}.json";

    private static bool IsSkippable(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Code == "facegen-zero-shapes") &&
        diagnostics.Where(item => item.Code != "facegen-zero-shapes").All(item => item.Severity != DiagnosticSeverity.Error);

    private static ImmutableArray<Diagnostic> DowngradeZeroShape(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Select(item => item.Code == "facegen-zero-shapes"
            ? new Diagnostic(item.Code, DiagnosticSeverity.Warning, item.Message) : item).ToImmutableArray();

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facegen-plugin-target-output-outside-lab",
            DiagnosticSeverity.Error, "FaceGen plugin-target outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-plugin-target-output-extension", DiagnosticSeverity.Error,
                "FaceGen plugin-target artifacts must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facegen-plugin-target-output-exists",
            DiagnosticSeverity.Error, "FaceGen plugin-target outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facegen-plugin-target-output-parent-missing",
            DiagnosticSeverity.Error, "FaceGen plugin-target output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("facegen-plugin-target-duplicate-key",
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

    private static FaceGenPluginBuildResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, true, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
