using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a deterministic, typed new/template/override record proposal.</summary>
public sealed class RecordProposalService(IWorkspacePolicy policy, WorkspacePath labRoot) : IRecordProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<RecordProposalResult> ProposeAsync(RecordProposalRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);
        var masters = request.MasterDependencies.Select(item => item.Value).ToImmutableArray();
        var canonicalFields = new
        {
            edition = request.Edition.ToWireName(),
            signature = request.Signature.Value,
            mode = request.Mode.ToString(),
            formId = request.FormId.ToString(),
            sourceFormId = request.SourceFormId?.ToString(),
            editorId = request.EditorId.Value,
            name = request.Name,
            masters
        };
        var canonical = request.HeadPart is null ? JsonSerializer.SerializeToUtf8Bytes(canonicalFields) : JsonSerializer.SerializeToUtf8Bytes(new
        {
            canonicalFields.edition, canonicalFields.signature, canonicalFields.mode, canonicalFields.formId,
            canonicalFields.sourceFormId, canonicalFields.editorId, canonicalFields.name, canonicalFields.masters,
            headPart = request.HeadPart
        });
        var artifact = new RecordProposalArtifact("1", "record-proposal", request.Edition.ToWireName(), request.Mode, request.Signature.Value,
            request.FormId.ToString(), request.SourceFormId?.ToString(), request.EditorId.Value, request.Name, masters,
            "explicit-local-form-id", Convert.ToHexString(SHA256.HashData(canonical)), true, request.HeadPart);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.Output.Value, overwrite: false);
            return new RecordProposalResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("record-proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics, artifact); }
    }

    private void Validate(RecordProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition is not (GameEdition.Fallout4 or GameEdition.SkyrimSpecialEdition)) diagnostics.Add(new Diagnostic("record-proposal-game", DiagnosticSeverity.Error, "Record proposals support Fallout 4 and Skyrim SE."));
        if (request.Mode is not (RecordProposalMode.New or RecordProposalMode.Template or RecordProposalMode.Override)) diagnostics.Add(new Diagnostic("record-proposal-mode", DiagnosticSeverity.Error, "Record proposal mode must be new, template, or override."));
        if (request.FormId.Value == 0 || request.FormId.Value > 0x00FF_FFFF) diagnostics.Add(new Diagnostic("record-proposal-form-id", DiagnosticSeverity.Error, "Record FormIDs must be nonzero 24-bit local identifiers."));
        if (request.Mode == RecordProposalMode.Override && request.SourceFormId != request.FormId) diagnostics.Add(new Diagnostic("record-proposal-override-source", DiagnosticSeverity.Error, "Override proposals require sourceFormId to equal the target FormID."));
        if (request.Mode is RecordProposalMode.Template or RecordProposalMode.Override)
        {
            if (request.SourceFormId is null || request.SourceFormId.Value.Value == 0) diagnostics.Add(new Diagnostic("record-proposal-source-required", DiagnosticSeverity.Error, "Template and override proposals require a nonzero source FormID."));
            else if (request.SourceFormId.Value.Value > 0x00FF_FFFF) diagnostics.Add(new Diagnostic("record-proposal-source-form-id", DiagnosticSeverity.Error, "Source FormIDs must be 24-bit local identifiers."));
        }
        else if (request.SourceFormId is not null) diagnostics.Add(new Diagnostic("record-proposal-new-source", DiagnosticSeverity.Error, "New proposals may not carry a source FormID."));
        if (string.IsNullOrWhiteSpace(request.EditorId.Value) || request.EditorId.Value.Length > 128) diagnostics.Add(new Diagnostic("record-proposal-editor-id", DiagnosticSeverity.Error, "EditorID must be 1 to 128 characters."));
        if (request.Name is { Length: > 4096 } || request.Name?.Any(char.IsControl) == true) diagnostics.Add(new Diagnostic("record-proposal-name", DiagnosticSeverity.Error, "Name must be at most 4096 characters and contain no control characters."));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var master in request.MasterDependencies)
        {
            if (!seen.Add(master.Value)) diagnostics.Add(new Diagnostic("record-proposal-master-duplicate", DiagnosticSeverity.Error, $"Master '{master.Value}' is duplicated."));
        }
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("record-proposal-output-outside-lab", DiagnosticSeverity.Error, "Record proposals must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".record-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("record-proposal-output-extension", DiagnosticSeverity.Error, "Record proposals require the .record-proposal.json extension."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("record-proposal-output-exists", DiagnosticSeverity.Error, "Record proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("record-proposal-output-parent-missing", DiagnosticSeverity.Error, "The record proposal output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static RecordProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, RecordProposalArtifact? artifact = null) => new(false, artifact, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
