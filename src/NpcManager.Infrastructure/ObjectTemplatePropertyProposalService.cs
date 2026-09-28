using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound Fallout 4 OBTS property proposal.</summary>
public sealed class ObjectTemplatePropertyProposalService(IPluginReader pluginReader, IWorkspacePolicy policy, WorkspacePath labRoot) : IObjectTemplatePropertyProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly ImmutableHashSet<string> ValueTypes = ["IntType", "FloatType", "BoolType", "StringType", "FormIDInt", "EnumType", "FormIDFloat"];

    public async ValueTask<ObjectTemplatePropertyProposalResult> ProposeAsync(ObjectTemplatePropertyProposalRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics); ValidateProperties(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("object-template-property-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("object-template-property-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId && string.Equals(record.Signature, "ARMO", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) diagnostics.Add(new Diagnostic("object-template-property-source", DiagnosticSeverity.Error, matches.Length == 0 ? "ARMO source record was not found." : "ARMO source record is duplicated."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        foreach (var property in request.Properties.Where(item => item.Value1FormId is not null)) if (!allowed.Contains(property.Value1FormId!.Value.Plugin)) diagnostics.Add(new Diagnostic("object-template-property-master-unresolved", DiagnosticSeverity.Error, $"Property FormID {property.Value1FormId} is not provided by the source plugin or its masters."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var patchBytes = JsonSerializer.SerializeToUtf8Bytes(request.Properties, JsonOptions);
        var artifact = new ObjectTemplatePropertyProposalArtifact("1", "object-template-properties-proposal", request.Edition.ToWireName(), request.SourcePlugin.Value, request.SourceFormId.ToString(), matches[0].EditorId ?? "", inputHash.Value, Convert.ToHexString(SHA256.HashData(patchBytes)), request.Properties.Select(item => new ObjectTemplatePropertyArtifact(item.ValueType, item.FunctionType, item.PropertyIndex, item.Value1Integer, item.Value1Float, item.Value1FormId?.ToString(), item.Value2Integer, item.Value2Float, item.StepValue, item.CombinationIndex)).ToImmutableArray(), inspection.Masters.Select(item => item.Value).ToImmutableArray(), true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions); var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken); File.Move(temporary, request.OutputProposal.Value, overwrite: false); return new(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable()); }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { TryDelete(temporary); diagnostics.Add(new Diagnostic("object-template-property-write-failed", DiagnosticSeverity.Error, exception.Message)); return new(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private void ValidatePaths(ObjectTemplatePropertyProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value); if (sourceParent is null) diagnostics.Add(new Diagnostic("object-template-property-source-parent", DiagnosticSeverity.Error, "The source plugin must have a parent directory.")); else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-property-output-outside-lab", DiagnosticSeverity.Error, "Object-template property proposals must remain under K."));
        if (!request.OutputProposal.Value.EndsWith(".object-template-properties-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-property-output-extension", DiagnosticSeverity.Error, "Object-template property proposals require the .object-template-properties-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("object-template-property-output-exists", DiagnosticSeverity.Error, "Proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputProposal.Value); if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("object-template-property-output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist.")); else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("object-template-property-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try { if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("object-template-property-source-reparse", DiagnosticSeverity.Error, "The source plugin may not be a reparse point.")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("object-template-property-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("object-template-property-source-form-invalid", DiagnosticSeverity.Error, "The source FormID may not be null."));
    }

    private static void ValidateProperties(ObjectTemplatePropertyProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("object-template-property-game", DiagnosticSeverity.Error, "Object-template properties are Fallout 4-only."));
        if (request.Properties.IsDefaultOrEmpty || request.Properties.Length > 4096) diagnostics.Add(new Diagnostic("object-template-property-count", DiagnosticSeverity.Error, "Properties must contain 1 to 4096 entries."));
        foreach (var property in request.Properties)
        {
            if (property.CombinationIndex < 0 || property.CombinationIndex > 4095) diagnostics.Add(new Diagnostic("object-template-property-combination-index", DiagnosticSeverity.Error, "Combination indexes must be in the 0..4095 range."));
            if (!ValueTypes.Contains(property.ValueType)) diagnostics.Add(new Diagnostic("object-template-property-value-type", DiagnosticSeverity.Error, $"Unsupported OMOD value type '{property.ValueType}'."));
            if (property.ValueType is "FormIDInt" or "FormIDFloat" && property.Value1FormId is null) diagnostics.Add(new Diagnostic("object-template-property-form-id", DiagnosticSeverity.Error, "FormID property types require Value1FormId."));
            if (property.ValueType is not ("FormIDInt" or "FormIDFloat") && property.Value1FormId is not null) diagnostics.Add(new Diagnostic("object-template-property-form-id", DiagnosticSeverity.Error, "Only FormID property types may set Value1FormId."));
            if (property.Value1Float is { } value1 && !double.IsFinite(value1)) diagnostics.Add(new Diagnostic("object-template-property-finite", DiagnosticSeverity.Error, "Value1Float must be finite."));
            if (!double.IsFinite(property.Value2Float) || !double.IsFinite(property.StepValue)) diagnostics.Add(new Diagnostic("object-template-property-finite", DiagnosticSeverity.Error, "Value2Float and StepValue must be finite."));
        }
    }
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ObjectTemplatePropertyProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
