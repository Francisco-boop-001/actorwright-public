using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes an explicit, reviewable reset/delete action without mutating a plugin.</summary>
public sealed class ChangeActionService(IWorkspacePolicy policy, WorkspacePath labRoot) : IChangeActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ChangeActionResult> UpdateAsync(ChangeActionRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        try
        {
            var sessionBytes = await File.ReadAllBytesAsync(request.Session.Value, cancellationToken);
            var sessionText = await File.ReadAllTextAsync(request.Session.Value, cancellationToken);
            using var document = JsonDocument.Parse(sessionText, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("A change session must be a JSON object.");
            var baseline = ReadRecords(document.RootElement, "baseline");
            var working = ReadRecords(document.RootElement, "working");
            var candidates = baseline.Concat(working).Where(item => item.FormId == request.Record &&
                (request.Signature is null || string.Equals(item.Signature, request.Signature, StringComparison.Ordinal))).ToArray();
            var identities = candidates.Select(item => (item.FormId.Value, item.Signature)).Distinct().ToArray();
            if (identities.Length == 0)
            {
                diagnostics.Add(new Diagnostic("changes-action-record-missing", DiagnosticSeverity.Error, $"Record {request.Record} was not found in the change session."));
                return Refused(diagnostics);
            }
            if (identities.Length > 1)
            {
                diagnostics.Add(new Diagnostic("changes-action-signature-required", DiagnosticSeverity.Error, "The FormID occurs under multiple signatures; specify --signature."));
                return Refused(diagnostics);
            }
            var signature = identities[0].Signature;
            var baseRecord = baseline.FirstOrDefault(item => item.FormId == request.Record && item.Signature == signature);
            var workingRecord = working.FirstOrDefault(item => item.FormId == request.Record && item.Signature == signature);
            if (request.Action == ChangeAction.Reset && baseRecord is null)
            {
                diagnostics.Add(new Diagnostic("changes-action-reset-baseline-missing", DiagnosticSeverity.Error, "Reset requires a baseline record; use delete for an explicitly new record."));
                return Refused(diagnostics);
            }
            if (request.Action == ChangeAction.Delete && workingRecord is null)
            {
                diagnostics.Add(new Diagnostic("changes-action-delete-working-missing", DiagnosticSeverity.Error, "Delete requires a working record."));
                return Refused(diagnostics);
            }
            var selected = workingRecord ?? baseRecord!;
            var outcome = request.Action == ChangeAction.Reset ? "restore-baseline" : baseRecord is null ? "remove-new-record" : "drop-override-restore-parent";
            var artifact = new ChangeActionArtifact("1", "change-action-proposal", request.Edition.ToWireName(), request.Action.ToString().ToLowerInvariant(),
                request.Session.Value, Convert.ToHexString(SHA256.HashData(sessionBytes)), request.Record.ToString(), signature,
                selected.EditorId, baseRecord is not null, workingRecord is not null, outcome,
                baseRecord?.Fields ?? ImmutableDictionary<string, string?>.Empty, workingRecord?.Fields ?? ImmutableDictionary<string, string?>.Empty, true);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
            var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, request.Output.Value, overwrite: false);
                return new ChangeActionResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
            }
            catch (OperationCanceledException) { TryDelete(temporary); throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { TryDelete(temporary); diagnostics.Add(new Diagnostic("changes-action-write-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics, artifact); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { diagnostics.Add(new Diagnostic("changes-action-session-invalid", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
    }

    private void ValidatePaths(ChangeActionRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition is not (GameEdition.Fallout4 or GameEdition.SkyrimSpecialEdition)) diagnostics.Add(new Diagnostic("changes-action-game", DiagnosticSeverity.Error, "Change actions support Fallout 4 and Skyrim SE."));
        ValidateSession(request.Session, diagnostics);
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("changes-action-output-outside-lab", DiagnosticSeverity.Error, "Change-action proposals must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".changes-action.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("changes-action-output-extension", DiagnosticSeverity.Error, "Change-action proposals require the .changes-action.json extension."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("changes-action-output-exists", DiagnosticSeverity.Error, "Change-action proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("changes-action-output-parent-missing", DiagnosticSeverity.Error, "The change-action output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (request.Record.Value == 0) diagnostics.Add(new Diagnostic("changes-action-record-invalid", DiagnosticSeverity.Error, "The target FormID may not be null."));
        if (request.Signature is not null)
        {
            try { _ = new RecordSignature(request.Signature); }
            catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("changes-action-signature-invalid", DiagnosticSeverity.Error, exception.Message)); }
        }
    }

    private void ValidateSession(WorkspacePath session, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!session.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("changes-action-session-outside-lab", DiagnosticSeverity.Error, "Change sessions must remain under the K-only lab root."));
        if (!session.Value.EndsWith(".changes-session.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("changes-action-session-extension", DiagnosticSeverity.Error, "Change sessions require the .changes-session.json extension."));
        if (!File.Exists(session.Value)) { diagnostics.Add(new Diagnostic("changes-action-session-missing", DiagnosticSeverity.Error, "The change session does not exist.")); return; }
        try
        {
            if ((File.GetAttributes(session.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("changes-action-session-reparse", DiagnosticSeverity.Error, "The change session may not be a reparse point."));
            else if (new FileInfo(session.Value).Length > 16 * 1024 * 1024) diagnostics.Add(new Diagnostic("changes-action-session-size", DiagnosticSeverity.Error, "The change session may not exceed 16 MiB."));
            else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, session));
        }
        catch (IOException exception) { diagnostics.Add(new Diagnostic("changes-action-session-unreadable", DiagnosticSeverity.Error, exception.Message)); }
        catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("changes-action-session-unreadable", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static ImmutableArray<SessionRecord> ReadRecords(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) throw new FormatException($"Change session requires a {name} array.");
        var records = ImmutableArray.CreateBuilder<SessionRecord>(); var seen = new HashSet<(uint, string)>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("formId", out var form) || form.ValueKind != JsonValueKind.String || !FormId.TryParse(form.GetString()!, out var formId) || formId.Value == 0 || !item.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String) throw new FormatException($"{name} contains an invalid record identity.");
            var sig = new RecordSignature(signature.GetString()!); if (!seen.Add((formId.Value, sig.Value))) throw new FormatException($"{name} contains duplicate record {formId} {sig.Value}.");
            var editorId = item.TryGetProperty("editorId", out var editor) ? editor.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => editor.GetString(), _ => throw new FormatException("Record editorId must be a string or null.") } : null;
            var fields = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            if (item.TryGetProperty("fields", out var fieldObject))
            {
                if (fieldObject.ValueKind != JsonValueKind.Object) throw new FormatException("Record fields must be an object.");
                foreach (var field in fieldObject.EnumerateObject()) fields[field.Name] = Scalar(field.Value);
            }
            records.Add(new SessionRecord(formId, sig.Value, editorId, fields.ToImmutable()));
        }
        return records.ToImmutable();
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(), _ => throw new FormatException("Change fields must be scalar JSON values.") };
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ChangeActionResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, ChangeActionArtifact? artifact = null) => new(false, artifact, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private sealed record SessionRecord(FormId FormId, string Signature, string? EditorId, ImmutableDictionary<string, string?> Fields);
}
