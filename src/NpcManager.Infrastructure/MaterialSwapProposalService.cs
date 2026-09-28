using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound Fallout 4 MSWP proposal without binary plugin mutation.</summary>
public sealed class MaterialSwapProposalService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IMaterialSwapProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<MaterialSwapProposalResult> ProposeAsync(MaterialSwapProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        ValidatePatch(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("material-swap-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("material-swap-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId && string.Equals(record.Signature, "MSWP", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("material-swap-source-not-found", DiagnosticSeverity.Error, $"MSWP source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("material-swap-source-duplicate", DiagnosticSeverity.Error, $"MSWP source record {request.SourceFormId} is duplicated in the source plugin."));
        if (request.Mode == MaterialSwapProposalMode.Override && matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0].EditorId)) diagnostics.Add(new Diagnostic("material-swap-source-editor-id-missing", DiagnosticSeverity.Error, "Override proposals require the source MSWP EditorID."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var editorId = request.Mode == MaterialSwapProposalMode.Override ? matches[0].EditorId! : request.Patch.EditorId!.Value.Value;
        var patchBytes = JsonSerializer.SerializeToUtf8Bytes(request.Patch, JsonOptions);
        var normalizedEntries = request.Patch.Entries.Select(item => new MaterialSwapEntryArtifact(
            NormalizeMaterial(item.OriginalMaterial), NormalizeMaterial(item.ReplacementMaterial),
            item.ColorRemapIndex, item.TreeFolder)).ToImmutableArray();
        var artifact = new MaterialSwapProposalArtifact("1", "material-swap-record-proposal", request.Edition.ToWireName(), request.Mode,
            request.SourcePlugin.Value, request.SourceFormId.ToString(), editorId, inputHash.Value,
            Convert.ToHexString(SHA256.HashData(patchBytes)), request.Patch.TreeFolder,
            normalizedEntries, true, request.TargetFormId?.ToString());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new MaterialSwapProposalResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("material-swap-write-failed", DiagnosticSeverity.Error, exception.Message)); return new MaterialSwapProposalResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private void ValidatePaths(MaterialSwapProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (sourceParent is null) diagnostics.Add(new Diagnostic("material-swap-source-parent", DiagnosticSeverity.Error, "The material-swap source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("material-swap-output-outside-lab", DiagnosticSeverity.Error, "Material-swap proposals must remain under the K-only lab root."));
        if (!request.OutputProposal.Value.EndsWith(".material-swap-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("material-swap-output-extension", DiagnosticSeverity.Error, "Material-swap proposals must use the .material-swap-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("material-swap-output-exists", DiagnosticSeverity.Error, "Material-swap proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputProposal.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("material-swap-output-parent-missing", DiagnosticSeverity.Error, "The material-swap output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("material-swap-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try { if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("material-swap-source-reparse", DiagnosticSeverity.Error, "The source plugin may not be a reparse point.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("material-swap-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("material-swap-source-form-invalid", DiagnosticSeverity.Error, "The source MSWP FormID may not be null."));
    }

    private static void ValidatePatch(MaterialSwapProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("material-swap-game", DiagnosticSeverity.Error, "Material-swap proposals are Fallout 4-only."));
        if (request.Mode is not (MaterialSwapProposalMode.New or MaterialSwapProposalMode.Override)) diagnostics.Add(new Diagnostic("material-swap-mode-invalid", DiagnosticSeverity.Error, "Material-swap proposal mode must be new or override."));
        if (request.Mode == MaterialSwapProposalMode.New && request.Patch.EditorId is null) diagnostics.Add(new Diagnostic("material-swap-editor-id-required", DiagnosticSeverity.Error, "A new material-swap proposal requires an explicit EditorID."));
        if (request.Mode == MaterialSwapProposalMode.Override && request.Patch.EditorId is not null) diagnostics.Add(new Diagnostic("material-swap-override-editor-id", DiagnosticSeverity.Error, "Override proposals inherit the source EditorID and may not supply a replacement."));
        if (request.Mode == MaterialSwapProposalMode.New && (request.TargetFormId is null || request.TargetFormId.Value.Value == 0 || request.TargetFormId.Value.Value > 0x00FF_FFFF)) diagnostics.Add(new Diagnostic("material-swap-new-target-invalid", DiagnosticSeverity.Error, "New material-swap proposals require a nonzero plugin-local 24-bit target FormID."));
        if (request.Mode == MaterialSwapProposalMode.Override && request.TargetFormId is not null && request.TargetFormId != request.SourceFormId) diagnostics.Add(new Diagnostic("material-swap-override-target-invalid", DiagnosticSeverity.Error, "Override material-swap proposals must target the source FormID."));
        if (request.Patch.Entries.IsDefaultOrEmpty || request.Patch.Entries.Length > 4096) diagnostics.Add(new Diagnostic("material-swap-entry-count", DiagnosticSeverity.Error, "A material-swap proposal requires 1 to 4096 entries."));
        if (request.Patch.TreeFolder is { Length: > 4096 } || request.Patch.TreeFolder?.Any(char.IsControl) == true) diagnostics.Add(new Diagnostic("material-swap-tree-folder", DiagnosticSeverity.Error, "TreeFolder must be at most 4096 characters and contain no control characters."));
        foreach (var entry in request.Patch.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalMaterial) && string.IsNullOrWhiteSpace(entry.ReplacementMaterial)) diagnostics.Add(new Diagnostic("material-swap-empty-entry", DiagnosticSeverity.Error, "Each material substitution must contain an original or replacement material."));
            ValidateMaterial(entry.OriginalMaterial, "original", diagnostics);
            ValidateMaterial(entry.ReplacementMaterial, "replacement", diagnostics);
            if (entry.TreeFolder is { Length: > 4096 } || entry.TreeFolder?.Any(char.IsControl) == true) diagnostics.Add(new Diagnostic("material-swap-entry-tree-folder", DiagnosticSeverity.Error, "Entry TreeFolder must be at most 4096 characters and contain no control characters."));
            if (entry.ColorRemapIndex is { } index && (!double.IsFinite(index) || index < 0 || index > 1)) diagnostics.Add(new Diagnostic("material-swap-color-remap", DiagnosticSeverity.Error, "Color remap indexes must be finite values from 0 to 1."));
        }
        if (Changed(request.Patch).IsDefaultOrEmpty) diagnostics.Add(new Diagnostic("material-swap-empty-patch", DiagnosticSeverity.Error, "The material-swap proposal must contain at least one supported field."));
    }

    private static void ValidateMaterial(string value, string role, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { _ = new AssetPath(value); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic($"material-swap-{role}-path", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static string NormalizeMaterial(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : new AssetPath(value).Value;

    private static ImmutableArray<string> Changed(MaterialSwapProposalPatch patch)
    {
        var fields = ImmutableArray.CreateBuilder<string>();
        if (patch.EditorId is not null) fields.Add("editorId");
        if (patch.TreeFolder is not null) fields.Add("treeFolder");
        if (!patch.Entries.IsDefault) fields.Add("entries");
        return fields.ToImmutable();
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static MaterialSwapProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
