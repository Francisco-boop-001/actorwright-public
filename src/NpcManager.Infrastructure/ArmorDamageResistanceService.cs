using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed Fallout 4 DAMA proposal without binary plugin mutation.</summary>
public sealed class ArmorDamageResistanceService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IArmorDamageResistanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorDamageResistanceResult> ProposeAsync(ArmorDamageResistanceRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("armor-damage-resist-game",
            DiagnosticSeverity.Error, "Armor damage resistance proposals are Fallout 4-only."));
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (sourceParent is null) diagnostics.Add(new Diagnostic("armor-damage-resist-source-parent",
            DiagnosticSeverity.Error, "The damage resistance source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-damage-resist-output-outside-lab",
            DiagnosticSeverity.Error, "Damage resistance outputs must remain under the K-only lab root."));
        if (!request.OutputProposal.Value.EndsWith(".armor-damage-resist-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("armor-damage-resist-output-extension",
            DiagnosticSeverity.Error, "Damage resistance proposals must use the .armor-damage-resist-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("armor-damage-resist-output-exists",
            DiagnosticSeverity.Error, "Damage resistance proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputProposal.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("armor-damage-resist-output-parent-missing",
            DiagnosticSeverity.Error, "The damage resistance output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("armor-damage-resist-source-missing",
            DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("armor-damage-resist-source-reparse", DiagnosticSeverity.Error,
                        "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("armor-damage-resist-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("armor-damage-resist-source-form-invalid",
            DiagnosticSeverity.Error, "The source ARMO FormID may not be null."));
        if (request.Entries.IsDefaultOrEmpty || request.Entries.Length > 4096) diagnostics.Add(new Diagnostic("armor-damage-resist-entry-count",
            DiagnosticSeverity.Error, "A damage resistance proposal requires 1 to 4096 entries."));
        if (request.Entries.Select(item => item.DamageType).Distinct().Count() != request.Entries.Length)
            diagnostics.Add(new Diagnostic("armor-damage-resist-duplicate", DiagnosticSeverity.Error,
                "Damage type entries may not be duplicated."));
        if (request.Entries.Any(item => item.DamageType.FormId.Value == 0)) diagnostics.Add(new Diagnostic("armor-damage-resist-null-type",
            DiagnosticSeverity.Error, "Damage type references may not use the null FormID."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("armor-damage-resist-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("armor-damage-resist-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics, inputHash); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId && string.Equals(record.Signature, "ARMO", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("armor-damage-resist-source-not-found", DiagnosticSeverity.Error,
            $"ARMO source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("armor-damage-resist-source-duplicate", DiagnosticSeverity.Error,
            $"ARMO source record {request.SourceFormId} is duplicated in the source plugin."));
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        foreach (var entry in request.Entries)
            if (!allowed.Contains(entry.DamageType.Plugin)) diagnostics.Add(new Diagnostic("armor-damage-resist-master-unresolved",
                DiagnosticSeverity.Error, $"Damage type {entry.DamageType} is not provided by the source plugin or its masters."));
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);
        var artifact = new ArmorDamageResistanceArtifact("1", "armor-damage-resistance-proposal", request.Edition.ToWireName(),
            request.SourcePlugin.Value, request.SourceFormId.ToString(), inputHash.Value,
            request.Entries.Select(item => new ArmorDamageResistanceEntryArtifact(item.DamageType.ToString(), item.Value)).ToImmutableArray(),
            inspection.Masters.Select(item => item.Value).ToImmutableArray(), true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new ArmorDamageResistanceResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("armor-damage-resist-write-failed", DiagnosticSeverity.Error, exception.Message)); return new ArmorDamageResistanceResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorDamageResistanceResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? hash = null) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
