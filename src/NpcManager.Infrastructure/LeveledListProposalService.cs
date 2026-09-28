using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound LVLI proposal without binary plugin mutation.</summary>
public sealed class LeveledListProposalService : ILeveledListProposalService
{
    private readonly IPluginReader _pluginReader;
    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly IFileArtifactCleanup _cleanup;

    public LeveledListProposalService(
        IPluginReader pluginReader,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
        : this(pluginReader, policy, labRoot, new FileArtifactCleanup())
    {
    }

    internal LeveledListProposalService(
        IPluginReader pluginReader,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IFileArtifactCleanup cleanup)
    {
        _pluginReader = pluginReader;
        _policy = policy;
        _labRoot = labRoot;
        _cleanup = cleanup;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<LeveledListProposalResult> ProposeAsync(LeveledListProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var parent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("leveled-list-source-parent", DiagnosticSeverity.Error,
            "The source plugin must have a parent Data directory."));
        else diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, request.SourcePlugin));
        diagnostics.AddRange(ValidateDestination(request.OutputProposal));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("leveled-list-source-missing",
            DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("leveled-list-source-reparse", DiagnosticSeverity.Error,
                        "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("leveled-list-source-attributes-failed", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        if (request.ListFormId.Value == 0) diagnostics.Add(new Diagnostic("leveled-list-form-id-invalid",
            DiagnosticSeverity.Error, "The leveled-list FormID may not be null."));
        if (request.Entries.IsDefaultOrEmpty || request.Entries.Length > 4096)
            diagnostics.Add(new Diagnostic("leveled-list-entries-count", DiagnosticSeverity.Error,
                "A leveled-list proposal requires 1 to 4096 entries."));
        if (request.ChanceNone > 100 || request.Entries.Any(item => item.ChanceNone > 100 || item.Level == 0 || item.Count == 0))
            diagnostics.Add(new Diagnostic("leveled-list-entry-range", DiagnosticSeverity.Error,
                "Chance-none values must be 0..100 and entry levels/counts must be non-zero."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("leveled-list-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }

        PluginInspection inspection;
        try { inspection = await _pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("leveled-list-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics, inputHash); }
        var matches = inspection.Records.Where(record => record.FormId == request.ListFormId &&
            string.Equals(record.Signature, "LVLI", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("leveled-list-source-not-found", DiagnosticSeverity.Error,
            $"LVLI source record {request.ListFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("leveled-list-source-duplicate", DiagnosticSeverity.Error,
            $"LVLI source record {request.ListFormId} is duplicated in the source plugin."));
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        foreach (var entry in request.Entries)
        {
            if (entry.Item.FormId.Value == 0) diagnostics.Add(new Diagnostic("leveled-list-item-null", DiagnosticSeverity.Error,
                $"Leveled-list item {entry.Item} uses the null FormID."));
            if (!allowed.Contains(entry.Item.Plugin)) diagnostics.Add(new Diagnostic("leveled-list-master-unresolved", DiagnosticSeverity.Error,
                $"Leveled-list item {entry.Item} is not provided by the source plugin or its masters."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);
        var artifact = new LeveledListProposalArtifact("1", "leveled-list-record-proposal", request.Edition.ToWireName(),
            request.SourcePlugin.Value, request.ListFormId.ToString(), inputHash.Value, request.EditorId?.Value, request.ChanceNone,
            request.MaxCount, request.CalculateAllLevels, request.CalculateEachInCount, request.UseAll,
            request.Entries.Select(item => new LeveledListEntryArtifact(item.Item.ToString(), item.Level, item.Count, item.ChanceNone)).ToImmutableArray(),
            inspection.Masters.Select(item => item.Value).ToImmutableArray(), true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new LeveledListProposalResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            AttachCancellationCleanupFailure(
                exception,
                temporary,
                _cleanup.DeleteIfPresent(temporary));
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddCleanupDiagnostic(
                diagnostics,
                temporary,
                _cleanup.DeleteIfPresent(temporary));
            diagnostics.Add(new Diagnostic("leveled-list-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new LeveledListProposalResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(_labRoot)) diagnostics.Add(new Diagnostic("leveled-list-output-outside-lab", DiagnosticSeverity.Error,
            "Leveled-list proposals must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".leveled-list-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("leveled-list-output-extension", DiagnosticSeverity.Error,
            "Leveled-list proposals must use the .leveled-list-proposal.json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("leveled-list-output-exists", DiagnosticSeverity.Error,
            "Leveled-list proposals never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("leveled-list-output-parent-missing", DiagnosticSeverity.Error,
            "The leveled-list proposal output directory must already exist."));
        else diagnostics.AddRange(_policy.Evaluate(_labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static LeveledListProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? hash = null) => new(false, null, null, diagnostics.ToImmutable());
    private static void AddCleanupDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        FileArtifactCleanupResult cleanup)
    {
        if (!cleanup.Succeeded)
            diagnostics.Add(new Diagnostic(
                "leveled-list-cleanup-failed",
                DiagnosticSeverity.Warning,
                $"Cleanup could not remove '{path}': {cleanup.ErrorMessage}"));
    }

    private static void AttachCancellationCleanupFailure(
        OperationCanceledException exception,
        string path,
        FileArtifactCleanupResult cleanup)
    {
        if (!cleanup.Succeeded)
            exception.Data["leveled-list-cleanup-failed"] =
                $"Cleanup could not remove '{path}': {cleanup.ErrorMessage}";
    }
}
