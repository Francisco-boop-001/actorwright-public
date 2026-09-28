using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Produces an auditable correction plan after the strict FaceGen shape gate.
/// Correction inputs are explicit: this service never guesses race/headpart
/// providers or mutates a NIF in order to infer a trigger.
/// </summary>
public sealed class FaceGenCorrectionService(IFaceGenService faceGenService, IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenCorrectionService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxCorrections = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenCorrectionResult> BuildAsync(FaceGenCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        var analysis = await faceGenService.VerifyAsync(new FaceGenVerifyRequest(request.Edition,
            request.ManifestPath, request.NpcFormId, request.StrictShapes), cancellationToken);
        diagnostics.AddRange(analysis.Diagnostics);
        if (analysis.Manifest is null || !analysis.IsApplicable || HasErrors(diagnostics))
            return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ManifestPath.Value);
            if (info.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facegen-correction-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceGen correction manifests may not exceed {MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facegen-correction-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        ImmutableArray<FaceGenCorrectionInput> inputs;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            inputs = ReadInputs(document.RootElement, request.Edition, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-correction-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics, analysis.Manifest.SourceHash);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, analysis.Manifest.SourceHash);

        var decisions = inputs.OrderBy(item => item.Kind).Select(item =>
        {
            var changed = item.Trigger && !string.Equals(item.Before, item.After, StringComparison.Ordinal);
            return new FaceGenCorrectionDecision(item.Kind, item.Trigger,
                TriggerName(item.Kind), item.Before, item.Trigger ? item.After : item.Before,
                item.Trigger ? (changed ? "applied" : "triggered-no-change") : "preserved-non-trigger");
        }).ToImmutableArray();
        var artifact = new FaceGenCorrectionArtifact("1", "facegen-correction-semantic-build",
            request.Edition.ToWireName(), analysis.Manifest.NpcFormId.ToString(),
            analysis.Manifest.SourceHash.Value, decisions);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, outputBytes, cancellationToken);
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            return new FaceGenCorrectionResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("facegen-correction-write-failed", DiagnosticSeverity.Error,
                exception.Message));
            return new FaceGenCorrectionResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private static ImmutableArray<FaceGenCorrectionInput> ReadInputs(JsonElement root, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "corrections", out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxCorrections)
        {
            diagnostics.Add(new Diagnostic("facegen-correction-list-invalid", DiagnosticSeverity.Error,
                $"corrections must be an array with at most {MaxCorrections} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<FaceGenCorrectionInput>();
        var seen = new HashSet<FaceGenCorrectionKind>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.corrections[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "kind", out var kindText) ||
                !FaceGenCorrectionKindExtensions.TryParseWireName(kindText, out var kind) ||
                !TryGet(item, "trigger", out var triggerElement) ||
                (triggerElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False) ||
                !TryGetString(item, "before", out var before) || before.Length is 0 or > 512 ||
                !TryGetString(item, "after", out var after) || after.Length is 0 or > 512)
            {
                diagnostics.Add(new Diagnostic("facegen-correction-invalid", DiagnosticSeverity.Error,
                    $"{path} requires kind, boolean trigger, and non-empty before/after values."));
                continue;
            }
            if (!seen.Add(kind))
            {
                diagnostics.Add(new Diagnostic("facegen-correction-duplicate", DiagnosticSeverity.Error,
                    $"Correction '{kind.ToWireName()}' occurs more than once."));
                continue;
            }
            if (!IsSupported(kind, edition))
            {
                diagnostics.Add(new Diagnostic("facegen-correction-game-mismatch", DiagnosticSeverity.Error,
                    $"Correction '{kind.ToWireName()}' is not supported for {edition.ToWireName()}."));
                continue;
            }
            var trigger = triggerElement.GetBoolean();
            if (trigger && string.Equals(before, after, StringComparison.Ordinal))
                diagnostics.Add(new Diagnostic("facegen-correction-no-change", DiagnosticSeverity.Error,
                    $"Triggered correction '{kind.ToWireName()}' must change its semantic value."));
            result.Add(new FaceGenCorrectionInput(kind, trigger, before, after));
        }
        return result.ToImmutable();
    }

    private static bool IsSupported(FaceGenCorrectionKind kind, GameEdition edition) => edition switch
    {
        GameEdition.Fallout4 => kind is FaceGenCorrectionKind.GhoulHeadRear or FaceGenCorrectionKind.EyebrowsFixedColor
            or FaceGenCorrectionKind.MouthVanilla,
        GameEdition.SkyrimSpecialEdition => kind is FaceGenCorrectionKind.SseNeutralDetail or FaceGenCorrectionKind.SseOverlayFold,
        _ => false
    };

    private static string TriggerName(FaceGenCorrectionKind kind) => kind switch
    {
        FaceGenCorrectionKind.GhoulHeadRear => "race-is-ghoul",
        FaceGenCorrectionKind.EyebrowsFixedColor => "eyebrow-headpart-requires-fixed-color",
        FaceGenCorrectionKind.MouthVanilla => "mouth-headpart-requires-vanilla-texture",
        FaceGenCorrectionKind.SseNeutralDetail => "sse-face-tint-fold-enabled",
        FaceGenCorrectionKind.SseOverlayFold => "sse-racemenu-face-overlay-present",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported FaceGen correction.")
    };

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facegen-correction-output-outside-lab",
            DiagnosticSeverity.Error, "FaceGen correction outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-correction-output-extension", DiagnosticSeverity.Error,
                "FaceGen correction artifacts must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facegen-correction-output-exists",
            DiagnosticSeverity.Error, "FaceGen correction outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facegen-correction-output-parent-missing",
            DiagnosticSeverity.Error, "FaceGen correction output directory must already exist."));
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

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } parsed)
        { value = parsed.Trim(); return true; }
        value = string.Empty; return false;
    }

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("facegen-correction-duplicate-key",
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

    private static FaceGenCorrectionResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
