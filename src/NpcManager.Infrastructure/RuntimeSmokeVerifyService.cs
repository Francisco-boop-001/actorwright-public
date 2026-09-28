using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Validates operator-supplied runtime smoke evidence without judging pixels or
/// claiming that the game was actually run by this process.
/// </summary>
public sealed class RuntimeSmokeVerifyService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IRuntimeSmokeVerifyService
{
    private const long MaximumReportBytes = 2 * 1024 * 1024;
    private const long MaximumScreenshotBytes = 64 * 1024 * 1024;

    private static readonly ImmutableArray<string> Fallout4Screenshots =
        ["face", "neck", "body", "hands", "eyes", "outfit"];

    private static readonly ImmutableArray<string> SkyrimScreenshots =
        ["face", "neck", "body", "hands", "eyes", "hair", "outfit"];

    public async ValueTask<RuntimeSmokeVerifyResult> VerifyAsync(
        RuntimeSmokeVerifyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return Invalid(request, diagnostics.ToImmutable());
        }

        try
        {
            var acceptance = await ReadJsonAsync(request.PackageAcceptance, cancellationToken);
            var runtime = await ReadJsonAsync(request.RuntimeReport, cancellationToken);
            ValidateAcceptance(acceptance, diagnostics);
            ValidateRuntime(runtime, request, acceptance, diagnostics);
            var screenshots = runtime.ValueKind == JsonValueKind.Object
                ? ReadScreenshotPaths(runtime, request, diagnostics)
                : ImmutableArray<string>.Empty;
            var isValid = !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
            return new RuntimeSmokeVerifyResult(
                isValid,
                runtime.ValueKind == JsonValueKind.Object && runtime.TryGetProperty("schemaVersion", out var schema)
                    ? schema.GetString() ?? string.Empty
                    : string.Empty,
                request.Edition.ToWireName(),
                request.RuntimeReport.Value,
                request.PackageAcceptance.Value,
                screenshots,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("runtime-report-read-failed", DiagnosticSeverity.Error,
                "The runtime evidence JSON could not be read or parsed."));
            return Invalid(request, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(RuntimeSmokeVerifyRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePath(request.RuntimeReport, "runtime-report", diagnostics);
        ValidatePath(request.PackageAcceptance, "package-acceptance", diagnostics);
        return diagnostics.ToImmutable();
    }

    private void ValidatePath(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic($"{role}-outside-lab", DiagnosticSeverity.Error,
                $"The {role} must remain under the K-only lab root."));
        }
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic($"{role}-parent-missing", DiagnosticSeverity.Error,
                $"The {role} parent directory does not exist."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(path.Value, role, diagnostics);
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(new Diagnostic($"{role}-missing", DiagnosticSeverity.Error,
                $"The {role} does not exist."));
        }
    }

    private static async ValueTask<JsonElement> ReadJsonAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (info.Length <= 0 || info.Length > MaximumReportBytes)
        {
            throw new InvalidDataException("Runtime evidence JSON exceeds the accepted size bounds.");
        }
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static void ValidateAcceptance(
        JsonElement acceptance,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (acceptance.ValueKind != JsonValueKind.Object ||
            !acceptance.TryGetProperty("packageArchiveSha256", out var hash) ||
            hash.ValueKind != JsonValueKind.String || !IsSha256(hash.GetString()))
        {
            diagnostics.Add(new Diagnostic("package-archive-hash-invalid", DiagnosticSeverity.Error,
                "Package acceptance must contain a 64-character SHA-256 archive hash."));
        }
        if (acceptance.ValueKind != JsonValueKind.Object ||
            !acceptance.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            (status.GetString() is not "PASS" and not "PASS_WITH_SCOPED_LIMITS"))
        {
            diagnostics.Add(new Diagnostic("package-acceptance-status-invalid", DiagnosticSeverity.Error,
                "Package acceptance status must be PASS or PASS_WITH_SCOPED_LIMITS."));
        }
        if (acceptance.ValueKind != JsonValueKind.Object ||
            !acceptance.TryGetProperty("runtimeReleaseClaim", out var runtimeClaim) ||
            runtimeClaim.ValueKind != JsonValueKind.False)
        {
            diagnostics.Add(new Diagnostic("package-runtime-claim-invalid", DiagnosticSeverity.Error,
                "Package acceptance must explicitly set runtimeReleaseClaim to false."));
        }
    }

    private void ValidateRuntime(
        JsonElement runtime,
        RuntimeSmokeVerifyRequest request,
        JsonElement acceptance,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (runtime.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("runtime-report-root-invalid", DiagnosticSeverity.Error,
                "Runtime smoke evidence must be a JSON object."));
            return;
        }
        RequireString(runtime, "schemaVersion", "1", diagnostics, "runtime-schema-unsupported");
        RequireString(runtime, "game", request.Edition.ToWireName(), diagnostics, "runtime-game-mismatch");
        RequireString(runtime, "status", "PASS", diagnostics, "runtime-status-not-pass");
        RequireBoolean(runtime, "controlNpcSameFrame", true, diagnostics);
        RequireBoolean(runtime, "providerMatchesPackage", true, diagnostics);

        if (!runtime.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object ||
            !package.TryGetProperty("archiveSha256", out var packageHash) ||
            packageHash.ValueKind != JsonValueKind.String ||
            acceptance.ValueKind != JsonValueKind.Object ||
            !acceptance.TryGetProperty("packageArchiveSha256", out var acceptanceHash) ||
            acceptanceHash.ValueKind != JsonValueKind.String ||
            !string.Equals(packageHash.GetString(), acceptanceHash.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("runtime-package-hash-mismatch", DiagnosticSeverity.Error,
                "Runtime evidence package hash does not match package acceptance."));
        }

        ValidateTarget(runtime, diagnostics);
        if (!runtime.TryGetProperty("control", out var control) || control.ValueKind != JsonValueKind.Object ||
            !HasNonEmptyString(control, "formId") || !HasNonEmptyString(control, "plugin"))
        {
            diagnostics.Add(new Diagnostic("runtime-control-identity-missing", DiagnosticSeverity.Error,
                "Runtime evidence must identify the in-frame control NPC."));
        }
        else if (runtime.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object &&
                 HasNonEmptyString(target, "formId") && HasNonEmptyString(target, "plugin") &&
                 string.Equals(target.GetProperty("formId").GetString(), control.GetProperty("formId").GetString(),
                     StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(target.GetProperty("plugin").GetString(), control.GetProperty("plugin").GetString(),
                     StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("runtime-control-target-same", DiagnosticSeverity.Error,
                "Runtime evidence control NPC must be distinct from the target NPC."));
        }
        ValidateEnvironmentFingerprint(runtime, request, diagnostics);
        if (!runtime.TryGetProperty("operator", out var operatorData) || operatorData.ValueKind != JsonValueKind.Object ||
            !HasNonEmptyString(operatorData, "name") || !HasNonEmptyString(operatorData, "capturedAt"))
        {
            diagnostics.Add(new Diagnostic("runtime-operator-metadata-missing", DiagnosticSeverity.Error,
                "Runtime evidence must identify the operator and capture time."));
        }
    }

    private static void ValidateTarget(JsonElement runtime, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!runtime.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("runtime-target-identity-missing", DiagnosticSeverity.Error,
                "Runtime evidence target identity is missing."));
            return;
        }
        foreach (var field in new[] { "formId", "plugin", "faceGeomProvider", "faceTintProvider", "bodySkinProvider", "outfitProvider" })
        {
            if (!HasNonEmptyString(target, field))
            {
                diagnostics.Add(new Diagnostic("runtime-target-field-missing", DiagnosticSeverity.Error,
                    $"Runtime evidence target.{field} is missing."));
            }
        }
        if (!target.TryGetProperty("headpartProviders", out var headparts) ||
            headparts.ValueKind != JsonValueKind.Array || headparts.GetArrayLength() == 0 ||
            headparts.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            diagnostics.Add(new Diagnostic("runtime-headpart-providers-missing", DiagnosticSeverity.Error,
                "Runtime evidence must identify at least one headpart provider."));
        }
    }

    private void ValidateEnvironmentFingerprint(
        JsonElement runtime,
        RuntimeSmokeVerifyRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!runtime.TryGetProperty("environmentFingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.Object ||
            !HasNonEmptyString(fingerprint, "path") || !HasNonEmptyString(fingerprint, "sha256"))
        {
            diagnostics.Add(new Diagnostic("runtime-environment-fingerprint-missing", DiagnosticSeverity.Error,
                "Runtime evidence environment fingerprint is missing."));
            return;
        }
        var pathText = fingerprint.GetProperty("path").GetString()!;
        WorkspacePath path;
        try { path = new WorkspacePath(Path.IsPathFullyQualified(pathText) ? pathText : Path.Combine(Path.GetDirectoryName(request.RuntimeReport.Value)!, pathText)); }
        catch (ArgumentException)
        {
            diagnostics.Add(new Diagnostic("runtime-environment-path-invalid", DiagnosticSeverity.Error,
                "The environment fingerprint path is invalid."));
            return;
        }
        if (!path.IsUnder(labRoot) || !File.Exists(path.Value))
        {
            diagnostics.Add(new Diagnostic("runtime-environment-path-invalid", DiagnosticSeverity.Error,
                "Environment fingerprint must reference an existing K-local file."));
            return;
        }
        AddReparseDiagnostic(path.Value, "runtime-environment", diagnostics);
        try
        {
            using var stream = File.OpenRead(path.Value);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, fingerprint.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("runtime-environment-hash-mismatch", DiagnosticSeverity.Error,
                    "Environment fingerprint SHA-256 does not match the referenced file."));
            }
        }
        catch (IOException)
        {
            diagnostics.Add(new Diagnostic("runtime-environment-read-failed", DiagnosticSeverity.Error,
                "The environment fingerprint could not be read safely."));
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("runtime-environment-read-failed", DiagnosticSeverity.Error,
                "The environment fingerprint could not be read safely."));
        }
    }

    private ImmutableArray<string> ReadScreenshotPaths(
        JsonElement runtime,
        RuntimeSmokeVerifyRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var required = request.Edition == GameEdition.Fallout4 ? Fallout4Screenshots : SkyrimScreenshots;
        if (!runtime.TryGetProperty("screenshots", out var screenshots) || screenshots.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("runtime-screenshots-missing", DiagnosticSeverity.Error,
                "Runtime evidence screenshots are missing."));
            return ImmutableArray<string>.Empty;
        }
        var validated = ImmutableArray.CreateBuilder<string>();
        foreach (var name in required)
        {
            if (!screenshots.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-missing", DiagnosticSeverity.Error,
                    $"Runtime evidence screenshot '{name}' is missing."));
                continue;
            }
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-path-invalid", DiagnosticSeverity.Error,
                    $"Runtime evidence screenshot '{name}' must name a non-empty path."));
                continue;
            }
            WorkspacePath path;
            try { path = new WorkspacePath(Path.IsPathFullyQualified(text) ? text : Path.Combine(Path.GetDirectoryName(request.RuntimeReport.Value)!, text)); }
            catch (ArgumentException)
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-path-invalid", DiagnosticSeverity.Error,
                    $"Runtime evidence screenshot '{name}' has an invalid path."));
                continue;
            }
            if (!path.IsUnder(labRoot) || !File.Exists(path.Value))
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-path-invalid", DiagnosticSeverity.Error,
                    $"Runtime evidence screenshot '{name}' must be an existing K-local file."));
                continue;
            }
            var diagnosticsBeforePathInspection = diagnostics.Count;
            try
            {
                if (new FileInfo(path.Value).Length > MaximumScreenshotBytes)
                {
                    diagnostics.Add(new Diagnostic("runtime-screenshot-too-large", DiagnosticSeverity.Error,
                        $"Runtime evidence screenshot '{name}' exceeds the accepted size bound."));
                }
                else if (!HasSupportedImageSignature(path.Value))
                {
                    diagnostics.Add(new Diagnostic("runtime-screenshot-format-invalid", DiagnosticSeverity.Error,
                        $"Runtime evidence screenshot '{name}' is not a supported image file."));
                }
                AddReparseDiagnostic(path.Value, $"runtime-screenshot-{name}", diagnostics);
            }
            catch (IOException)
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-inspection-failed", DiagnosticSeverity.Error,
                    "The runtime evidence screenshot could not be inspected safely."));
            }
            catch (UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("runtime-screenshot-inspection-failed", DiagnosticSeverity.Error,
                    "The runtime evidence screenshot could not be inspected safely."));
            }
            if (diagnostics.Count != diagnosticsBeforePathInspection)
                continue;
            validated.Add(name);
        }
        return validated.ToImmutable();
    }

    private static bool HasSupportedImageSignature(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            header.Length, FileOptions.SequentialScan);
        var read = stream.Read(header);
        if (read >= 8 && header[..8].SequenceEqual(new byte[]
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return true;
        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        if (read >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return true;
        if (read >= 2 && header[0] == 0x42 && header[1] == 0x4D) return true;
        return read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
    }

    private static void RequireString(JsonElement root, string property, string expected,
        ImmutableArray<Diagnostic>.Builder diagnostics, string code)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error,
                $"Runtime evidence property '{property}' must equal '{expected}'."));
        }
    }

    private static void RequireBoolean(JsonElement root, string property, bool expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty(property, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            diagnostics.Add(new Diagnostic($"runtime-{property}-missing", DiagnosticSeverity.Error,
                $"Runtime evidence property '{property}' must be true."));
        }
    }

    private static bool HasNonEmptyString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void AddReparseDiagnostic(string path, string role, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
                        $"The {role} path traverses a reparse point."));
                    return;
                }
            }
            catch (IOException)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error,
                    "The runtime evidence path could not be inspected safely."));
                return;
            }
            catch (UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error,
                    "The runtime evidence path could not be inspected safely."));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static RuntimeSmokeVerifyResult Invalid(
        RuntimeSmokeVerifyRequest request,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(false, string.Empty, request.Edition.ToWireName(), request.RuntimeReport.Value,
            request.PackageAcceptance.Value, ImmutableArray<string>.Empty, diagnostics);
}
