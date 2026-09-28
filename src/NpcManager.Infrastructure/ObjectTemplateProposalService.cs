using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound Fallout 4 OBTS combination/include proposal.</summary>
public sealed class ObjectTemplateProposalService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IObjectTemplateProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ObjectTemplateProposalResult> ProposeAsync(ObjectTemplateProposalRequest request,
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
        { diagnostics.Add(new Diagnostic("object-template-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("object-template-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId && string.Equals(record.Signature, "ARMO", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("object-template-source-not-found", DiagnosticSeverity.Error, $"ARMO source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("object-template-source-duplicate", DiagnosticSeverity.Error, $"ARMO source record {request.SourceFormId} is duplicated in the source plugin."));
        if (request.Mode == ObjectTemplateProposalMode.Override && matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0].EditorId)) diagnostics.Add(new Diagnostic("object-template-source-editor-id-missing", DiagnosticSeverity.Error, "Override proposals require the source ARMO EditorID."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        ValidateReferences(request.Patch, allowed, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var editorId = request.Mode == ObjectTemplateProposalMode.Override ? matches[0].EditorId! : request.Patch.EditorId!.Value.Value;
        var patchBytes = JsonSerializer.SerializeToUtf8Bytes(request.Patch, JsonOptions);
        var combinations = request.Patch.Combinations.Select(item => new ObjectTemplateCombinationArtifact(item.DisplayName,
            item.IsDefault, item.IsEditorOnly, item.ParentCombinationIndex, item.LevelMin, item.LevelMax,
            item.MinLevelForRanks, item.AltLevelsPerTier, item.Keywords.Select(item => item.ToString()).ToImmutableArray(),
            item.Includes.Select(include => new ObjectTemplateIncludeArtifact(include.Mod.ToString(), include.AttachPointIndex,
                include.IsOptional, include.DontUseAll)).ToImmutableArray())).ToImmutableArray();
        var artifact = new ObjectTemplateProposalArtifact("1", "object-template-combinations-proposal", request.Edition.ToWireName(),
            request.Mode, request.SourcePlugin.Value, request.SourceFormId.ToString(), editorId, inputHash.Value,
            Convert.ToHexString(SHA256.HashData(patchBytes)), combinations, inspection.Masters.Select(item => item.Value).ToImmutableArray(), true,
            request.Patch.TargetFormId?.ToString());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new ObjectTemplateProposalResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("object-template-write-failed", DiagnosticSeverity.Error, exception.Message)); return new ObjectTemplateProposalResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private void ValidatePaths(ObjectTemplateProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (sourceParent is null) diagnostics.Add(new Diagnostic("object-template-source-parent", DiagnosticSeverity.Error, "The object-template source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-output-outside-lab", DiagnosticSeverity.Error, "Object-template proposals must remain under the K-only lab root."));
        if (!request.OutputProposal.Value.EndsWith(".object-template-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-output-extension", DiagnosticSeverity.Error, "Object-template proposals must use the .object-template-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("object-template-output-exists", DiagnosticSeverity.Error, "Object-template proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputProposal.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("object-template-output-parent-missing", DiagnosticSeverity.Error, "The object-template output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("object-template-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try { if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("object-template-source-reparse", DiagnosticSeverity.Error, "The source plugin may not be a reparse point.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("object-template-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("object-template-source-form-invalid", DiagnosticSeverity.Error, "The source ARMO FormID may not be null."));
    }

    private static void ValidatePatch(ObjectTemplateProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("object-template-game", DiagnosticSeverity.Error, "Object-template combinations are Fallout 4-only."));
        if (request.Mode is not (ObjectTemplateProposalMode.New or ObjectTemplateProposalMode.Override)) diagnostics.Add(new Diagnostic("object-template-mode-invalid", DiagnosticSeverity.Error, "Object-template proposal mode must be new or override."));
        if (request.Mode == ObjectTemplateProposalMode.New && request.Patch.EditorId is null) diagnostics.Add(new Diagnostic("object-template-editor-id-required", DiagnosticSeverity.Error, "A new object-template proposal requires an explicit EditorID."));
        if (request.Mode == ObjectTemplateProposalMode.Override && request.Patch.EditorId is not null) diagnostics.Add(new Diagnostic("object-template-override-editor-id", DiagnosticSeverity.Error, "Override proposals inherit the source EditorID and may not supply a replacement."));
        if (request.Mode == ObjectTemplateProposalMode.New && (request.Patch.TargetFormId is null || request.Patch.TargetFormId.Value.Value == 0 || request.Patch.TargetFormId.Value.Value > 0x00FF_FFFF)) diagnostics.Add(new Diagnostic("object-template-target-form-required", DiagnosticSeverity.Error, "New object-template proposals require a nonzero plugin-local 24-bit target FormID."));
        if (request.Mode == ObjectTemplateProposalMode.Override && request.Patch.TargetFormId is not null && request.Patch.TargetFormId.Value != request.SourceFormId) diagnostics.Add(new Diagnostic("object-template-override-target", DiagnosticSeverity.Error, "Override object-template proposals must target the source FormID."));
        if (request.Patch.Combinations.IsDefaultOrEmpty || request.Patch.Combinations.Length > 4096) diagnostics.Add(new Diagnostic("object-template-combination-count", DiagnosticSeverity.Error, "An object-template proposal requires 1 to 4096 combinations."));
        for (var i = 0; i < request.Patch.Combinations.Length; i++)
        {
            var combination = request.Patch.Combinations[i];
            if (combination.DisplayName is { Length: > 4096 } || combination.DisplayName?.Any(char.IsControl) == true) diagnostics.Add(new Diagnostic("object-template-name", DiagnosticSeverity.Error, "Combination names must be at most 4096 characters and contain no control characters."));
            if (combination.ParentCombinationIndex is { } parent && (parent < 0 || parent >= request.Patch.Combinations.Length)) diagnostics.Add(new Diagnostic("object-template-parent", DiagnosticSeverity.Error, "Parent combination indexes must be non-negative and refer to an existing combination."));
            if (combination.LevelMin > combination.LevelMax) diagnostics.Add(new Diagnostic("object-template-level-range", DiagnosticSeverity.Error, "Combination minimum level may not exceed maximum level."));
            if (combination.Keywords.IsDefault || combination.Keywords.Length > 4096 || combination.Keywords.Any(item => item.FormId.Value == 0) || combination.Keywords.Distinct().Count() != combination.Keywords.Length) diagnostics.Add(new Diagnostic("object-template-keywords", DiagnosticSeverity.Error, "Combination keywords must be unique, non-null, and bounded."));
            if (combination.Includes.IsDefault || combination.Includes.Length > 4096 || combination.Includes.Any(item => item.Mod.FormId.Value == 0) || combination.Includes.Select(item => item.Mod).Distinct().Count() != combination.Includes.Length) diagnostics.Add(new Diagnostic("object-template-includes", DiagnosticSeverity.Error, "Combination includes must be unique, non-null, and bounded."));
        }
        if (request.Patch.Combinations.All(item => item.Keywords.IsDefaultOrEmpty && item.Includes.IsDefaultOrEmpty && item.DisplayName is null && !item.IsDefault && !item.IsEditorOnly && item.ParentCombinationIndex is null && item.LevelMin == 0 && item.LevelMax == 0 && item.MinLevelForRanks == 0 && item.AltLevelsPerTier == 0)) diagnostics.Add(new Diagnostic("object-template-empty-patch", DiagnosticSeverity.Error, "The object-template proposal must contain at least one supported field."));
    }

    private static void ValidateReferences(ObjectTemplateProposalPatch patch, ImmutableHashSet<PluginName> allowed, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var reference in patch.Combinations.SelectMany(item => item.Keywords).Concat(patch.Combinations.SelectMany(item => item.Includes.Select(include => include.Mod))))
        {
            if (reference.FormId.Value == 0) diagnostics.Add(new Diagnostic("object-template-reference-null", DiagnosticSeverity.Error, $"Object-template reference {reference} uses the null FormID."));
            if (!allowed.Contains(reference.Plugin)) diagnostics.Add(new Diagnostic("object-template-master-unresolved", DiagnosticSeverity.Error, $"Object-template reference {reference} is not provided by the source plugin or its masters."));
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ObjectTemplateProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
