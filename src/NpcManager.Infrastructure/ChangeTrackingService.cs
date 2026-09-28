using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Computes stable field-level changes from an explicit K-local session file.</summary>
public sealed class ChangeTrackingService(IWorkspacePolicy policy, WorkspacePath labRoot) : IChangeTrackingService
{
    public async ValueTask<ChangeListResult> ListAsync(ChangeListRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePath(request, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return new(false, request.Edition, [], diagnostics.ToImmutable());
        try
        {
            await using var stream = File.OpenRead(request.Session.Value);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 }, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A change session must be a JSON object.");
            var baseline = ParseRecords(root, "baseline"); var working = ParseRecords(root, "working");
            var changes = ImmutableArray.CreateBuilder<ChangedRecord>();
            var allKeys = baseline.Keys.Concat(working.Keys).Distinct().OrderBy(key => key.FormId.Value).ThenBy(key => key.Signature, StringComparer.Ordinal).ToArray();
            foreach (var key in allKeys)
            {
                baseline.TryGetValue(key, out var left); working.TryGetValue(key, out var right);
                if (left is null || right is null) { changes.Add(new ChangedRecord(key.FormId, key.Signature, right?.EditorId ?? left?.EditorId, [new ChangeFieldDifference("record", left is null ? null : "present", right is null ? null : "present")])); continue; }
                var fields = left.Fields.Keys.Concat(right.Fields.Keys).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).Select(name => new ChangeFieldDifference(name, left.Fields.GetValueOrDefault(name), right.Fields.GetValueOrDefault(name))).Where(field => !string.Equals(field.Baseline, field.Working, StringComparison.Ordinal)).ToImmutableArray();
                if (!string.Equals(left.EditorId, right.EditorId, StringComparison.Ordinal)) fields = fields.Insert(0, new ChangeFieldDifference("editorId", left.EditorId, right.EditorId));
                if (fields.Length > 0) changes.Add(new ChangedRecord(key.FormId, key.Signature, right.EditorId, fields));
            }
            return new(true, request.Edition, changes.ToImmutable(), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { diagnostics.Add(new Diagnostic("changes-session-invalid", DiagnosticSeverity.Error, exception.Message)); return new(false, request.Edition, [], diagnostics.ToImmutable()); }
    }

    private void ValidatePath(ChangeListRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition is not (GameEdition.Fallout4 or GameEdition.SkyrimSpecialEdition)) diagnostics.Add(new Diagnostic("changes-game", DiagnosticSeverity.Error, "Changes support Fallout 4 and Skyrim SE."));
        if (!request.Session.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("changes-session-outside-lab", DiagnosticSeverity.Error, "Change sessions must remain under the K-only lab root."));
        if (!request.Session.Value.EndsWith(".changes-session.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("changes-session-extension", DiagnosticSeverity.Error, "Change sessions require the .changes-session.json extension."));
        if (!File.Exists(request.Session.Value)) diagnostics.Add(new Diagnostic("changes-session-missing", DiagnosticSeverity.Error, "The change session does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.Session.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("changes-session-reparse", DiagnosticSeverity.Error, "The change session may not be a reparse point."));
                else if (new FileInfo(request.Session.Value).Length > 16 * 1024 * 1024) diagnostics.Add(new Diagnostic("changes-session-size", DiagnosticSeverity.Error, "The change session may not exceed 16 MiB."));
                else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Session));
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("changes-session-unreadable", DiagnosticSeverity.Error, exception.Message)); }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("changes-session-unreadable", DiagnosticSeverity.Error, exception.Message)); }
        }
    }

    private static SortedDictionary<RecordKey, SessionRecord> ParseRecords(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) throw new FormatException($"Change session requires a {name} array.");
        var records = new SortedDictionary<RecordKey, SessionRecord>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("formId", out var form) || form.ValueKind != JsonValueKind.String || !FormId.TryParse(form.GetString()!, out var formId) || formId.Value == 0 || !item.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String) throw new FormatException($"{name} contains an invalid record identity.");
            var sig = new RecordSignature(signature.GetString()!); var key = new RecordKey(formId, sig.Value); if (records.ContainsKey(key)) throw new FormatException($"{name} contains duplicate record {formId} {sig.Value}.");
            var editorId = item.TryGetProperty("editorId", out var editor)
                ? editor.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => editor.GetString(),
                    _ => throw new FormatException("Record editorId must be a string or null.")
                }
                : null;
            var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            if (item.TryGetProperty("fields", out var fieldObject))
            {
                if (fieldObject.ValueKind != JsonValueKind.Object) throw new FormatException("Record fields must be an object.");
                foreach (var field in fieldObject.EnumerateObject()) fields[field.Name] = Scalar(field.Value);
            }
            records.Add(key, new SessionRecord(editorId, fields.ToImmutableDictionary(StringComparer.Ordinal)));
        }
        return records;
    }
    private static string? Scalar(JsonElement value) => value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(), _ => throw new FormatException("Change fields must be scalar JSON values.") };
    private readonly record struct RecordKey(FormId FormId, string Signature) : IComparable<RecordKey> { public int CompareTo(RecordKey other) { var form = FormId.Value.CompareTo(other.FormId.Value); return form != 0 ? form : string.Compare(Signature, other.Signature, StringComparison.Ordinal); } }
    private sealed record SessionRecord(string? EditorId, ImmutableDictionary<string, string?> Fields);
}
