using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound ARMO addon-entry proposal without binary plugin mutation.</summary>
public sealed class ArmorAddonModelProposalService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IArmorAddonModelProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorAddonModelProposalResult> ProposeAsync(ArmorAddonModelProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("armor-addon-model-source-form-invalid", DiagnosticSeverity.Error, "The source ARMO FormID may not be null."));
        if (request.Entries.IsDefaultOrEmpty || request.Entries.Length > 4096) diagnostics.Add(new Diagnostic("armor-addon-model-entry-count", DiagnosticSeverity.Error, "An armor-addon model proposal requires 1 to 4096 entries."));
        if (request.Entries.Select(item => item.Index).Distinct().Count() != request.Entries.Length) diagnostics.Add(new Diagnostic("armor-addon-model-duplicate-index", DiagnosticSeverity.Error, "Armor-addon indexes may not be duplicated."));
        if (request.Entries.Any(item => item.Addon.FormId.Value == 0)) diagnostics.Add(new Diagnostic("armor-addon-model-null-reference", DiagnosticSeverity.Error, "Armor-addon references may not use the null FormID."));
        if (request.Edition == GameEdition.SkyrimSpecialEdition && request.Entries.Any(item => item.Index != 0)) diagnostics.Add(new Diagnostic("armor-addon-model-skyrim-index", DiagnosticSeverity.Error, "Skyrim ARMO addon entries use an implicit index of zero."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("armor-addon-model-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("armor-addon-model-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId && string.Equals(record.Signature, "ARMO", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("armor-addon-model-source-not-found", DiagnosticSeverity.Error, $"ARMO source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("armor-addon-model-source-duplicate", DiagnosticSeverity.Error, $"ARMO source record {request.SourceFormId} is duplicated in the source plugin."));
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        foreach (var entry in request.Entries) if (!allowed.Contains(entry.Addon.Plugin)) diagnostics.Add(new Diagnostic("armor-addon-model-master-unresolved", DiagnosticSeverity.Error, $"Armor addon {entry.Addon} is not provided by the source plugin or its masters."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var artifact = new ArmorAddonModelProposalArtifact("1", "armor-addon-model-entries-proposal", request.Edition.ToWireName(), request.SourcePlugin.Value,
            request.SourceFormId.ToString(), inputHash.Value, request.Entries.Select(item => new ArmorAddonModelEntryArtifact(item.Index, item.Addon.ToString())).ToImmutableArray(),
            inspection.Masters.Select(item => item.Value).ToImmutableArray(), true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new ArmorAddonModelProposalResult(true, artifact, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("armor-addon-model-write-failed", DiagnosticSeverity.Error, exception.Message)); return new ArmorAddonModelProposalResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private void ValidatePaths(ArmorAddonModelProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (sourceParent is null) diagnostics.Add(new Diagnostic("armor-addon-model-source-parent", DiagnosticSeverity.Error, "The armor-addon model source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-addon-model-output-outside-lab", DiagnosticSeverity.Error, "Armor-addon model proposals must remain under the K-only lab root."));
        if (!request.OutputProposal.Value.EndsWith(".armor-addon-model-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("armor-addon-model-output-extension", DiagnosticSeverity.Error, "Armor-addon model proposals must use the .armor-addon-model-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("armor-addon-model-output-exists", DiagnosticSeverity.Error, "Armor-addon model proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputProposal.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("armor-addon-model-output-parent-missing", DiagnosticSeverity.Error, "The armor-addon model output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("armor-addon-model-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("armor-addon-model-source-reparse", DiagnosticSeverity.Error, "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("armor-addon-model-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorAddonModelProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
