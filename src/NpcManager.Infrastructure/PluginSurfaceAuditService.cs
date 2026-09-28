using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Compares the complete typed major-record surface of two copied plugins.
/// This is intentionally read-only: it detects record-family and record-level
/// drift, but does not resolve a live load order or claim deployment/runtime
/// behavior.
/// </summary>
public sealed class PluginSurfaceAuditService(
    IPluginReader reader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IPluginSurfaceAuditService
{
    private static readonly ImmutableHashSet<string> RiskySignatures =
        ["WRLD", "CELL", "LAND", "WATR", "LTEX", "NAVM", "NAVI", "LCTN", "REGN", "CLMT", "MUSC", "IMGS"];

    public async ValueTask<PluginSurfaceAuditResult> AuditAsync(
        PluginSurfaceAuditRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return Invalid(request, diagnostics.ToImmutable());
        }

        var before = await ReadOperandAsync(
            "before", request.Edition, request.Before, diagnostics, cancellationToken);
        if (before is null)
        {
            return Invalid(request, diagnostics.ToImmutable());
        }

        var after = await ReadOperandAsync(
            "after", request.Edition, request.After, diagnostics, cancellationToken);
        if (after is null)
        {
            return Invalid(request, diagnostics.ToImmutable());
        }

        if (request.NormalizeMasterIndex)
        {
            if (request.Edition != GameEdition.SkyrimSpecialEdition)
            {
                diagnostics.Add(new Diagnostic("plugin-audit-normalization-edition", DiagnosticSeverity.Error,
                    "--normalize-master-index currently requires Skyrim Special Edition."));
                return Invalid(request, diagnostics.ToImmutable());
            }
            var masters = before.Masters.Concat(after.Masters)
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            before = await ReadOperandAsync("before", request.Edition, request.Before,
                diagnostics, cancellationToken, masters);
            after = await ReadOperandAsync("after", request.Edition, request.After,
                diagnostics, cancellationToken, masters);
            if (before is null || after is null) return Invalid(request, diagnostics.ToImmutable());
            if (before.Records.Concat(after.Records).Any(row => row.RawRecordSha256 is null))
            {
                diagnostics.Add(new Diagnostic("plugin-audit-normalization-unavailable", DiagnosticSeverity.Error,
                    "Normalized audit requires a complete raw record digest for every record."));
                return Invalid(request, diagnostics.ToImmutable());
            }
        }

        diagnostics.AddRange(before.Diagnostics);
        diagnostics.AddRange(after.Diagnostics);
        AddDuplicateFormIdDiagnostics(diagnostics, before, "before");
        AddDuplicateFormIdDiagnostics(diagnostics, after, "after");

        var changes = Compare(before.Records, after.Records, request.NormalizeMasterIndex ||
            before.Diagnostics.Concat(after.Diagnostics).Any(row => row.Code == "plugin-audit-skeletal-world-parent"));
        var risky = before.Records.Concat(after.Records)
            .Select(record => record.Signature.ToUpperInvariant())
            .Where(RiskySignatures.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToImmutableArray();
        var providerResolutions = await ResolveProvidersAsync(request, diagnostics, cancellationToken);
        var isValid = !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        return new PluginSurfaceAuditResult(
            isValid,
            1,
            request.Edition.ToWireName(),
            before.Plugin.Value,
            after.Plugin.Value,
            before.Records.Length,
            after.Records.Length,
            changes.Count(item => item.ChangeKind == "added"),
            changes.Count(item => item.ChangeKind == "removed"),
            changes.Count(item => item.ChangeKind == "changed"),
            before.Masters.Select(master => master.Value).ToImmutableArray(),
            after.Masters.Select(master => master.Value).ToImmutableArray(),
            risky,
            changes,
            diagnostics.ToImmutable(),
            providerResolutions);
    }

    private async ValueTask<PluginInspection?> ReadOperandAsync(
        string role,
        GameEdition edition,
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken,
        ImmutableArray<PluginName> normalizedMasters = default)
    {
        try
        {
            return await reader.ReadAsync(
                new PluginReadRequest(edition, path) { NormalizedMasterOrder = normalizedMasters, AllowSkeletalWorldParents = true }, cancellationToken);
        }
        catch (PluginReadDiagnosticException exception)
        {
            diagnostics.Add(new Diagnostic(exception.Code, DiagnosticSeverity.Error,
                $"{role} plugin '{path.Value}': {exception.Message}"));
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("plugin-read-failed", DiagnosticSeverity.Error,
                $"{role} plugin '{path.Value}': {exception.Message}"));
            return null;
        }
    }

    private async ValueTask<ImmutableArray<PluginSurfaceAuditProviderResolution>> ResolveProvidersAsync(
        PluginSurfaceAuditRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (request.PluginsRoot is null && request.LoadOrderPath is null)
            return [];
        if (request.PluginsRoot is null || request.LoadOrderPath is null)
            return [];

        var loadOrder = await new PluginLoadOrderService(reader, policy, labRoot).ResolveAsync(
            new PluginLoadOrderRequest(request.Edition, request.PluginsRoot.Value, request.LoadOrderPath.Value),
            cancellationToken);
        diagnostics.AddRange(loadOrder.Diagnostics);
        if (!loadOrder.IsValid)
            return [];

        var chains = new Dictionary<(string Owner, uint FormId, string Signature), List<PluginName>>();
        foreach (var entry in loadOrder.Entries.Where(item => item.Enabled && item.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = new WorkspacePath(Path.Combine(request.PluginsRoot.Value.Value, entry.Plugin.Value));
            try
            {
                var inspection = await reader.ReadAsync(
                    new PluginReadRequest(request.Edition, path), cancellationToken);
                diagnostics.AddRange(inspection.Diagnostics);
                foreach (var record in inspection.Records)
                {
                    var key = (
                        Owner: (record.OwnerPlugin ?? inspection.Plugin).Value.ToUpperInvariant(),
                        FormId: record.FormId.Value,
                        Signature: record.Signature.ToUpperInvariant());
                    if (!chains.TryGetValue(key, out var chain))
                    {
                        chain = [];
                        chains.Add(key, chain);
                    }
                    if (chain.Count == 0 || !string.Equals(chain[^1].Value, inspection.Plugin.Value,
                            StringComparison.OrdinalIgnoreCase))
                        chain.Add(inspection.Plugin);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("plugin-provider-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{entry.Plugin.Value}' could not be read for provider resolution: {exception.Message}"));
            }
        }

        return chains
            .OrderBy(item => item.Key.Owner, StringComparer.Ordinal)
            .ThenBy(item => item.Key.FormId)
            .ThenBy(item => item.Key.Signature, StringComparer.Ordinal)
            .Select(item => new PluginSurfaceAuditProviderResolution(
                new FormId(item.Key.FormId).ToString(),
                item.Key.Signature,
                item.Value[^1].Value,
                item.Value.Select(plugin => plugin.Value).ToImmutableArray()))
            .ToImmutableArray();
    }

    private ImmutableArray<Diagnostic> ValidatePaths(PluginSurfaceAuditRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Before.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic("before-outside-lab", DiagnosticSeverity.Error,
                "Before plugin must remain under the K-only lab root."));
        }
        if (!request.After.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic("after-outside-lab", DiagnosticSeverity.Error,
                "After plugin must remain under the K-only lab root."));
        }
        AddParentPolicyDiagnostics(diagnostics, request.Before, "before");
        AddParentPolicyDiagnostics(diagnostics, request.After, "after");
        if ((request.PluginsRoot is null) != (request.LoadOrderPath is null))
            diagnostics.Add(new Diagnostic("provider-load-order-pair-required", DiagnosticSeverity.Error,
                "Provider resolution requires both an explicit plugin root and load-order manifest."));
        AddReparseDiagnostic(diagnostics, request.Before.Value, "before");
        AddReparseDiagnostic(diagnostics, request.After.Value, "after");
        if (!File.Exists(request.Before.Value))
        {
            diagnostics.Add(new Diagnostic("before-missing", DiagnosticSeverity.Error, "Before plugin does not exist."));
        }
        if (!File.Exists(request.After.Value))
        {
            diagnostics.Add(new Diagnostic("after-missing", DiagnosticSeverity.Error, "After plugin does not exist."));
        }
        return diagnostics.ToImmutable();
    }

    private void AddParentPolicyDiagnostics(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        WorkspacePath plugin,
        string role)
    {
        var parent = Path.GetDirectoryName(plugin.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic($"{role}-parent-missing", DiagnosticSeverity.Error,
                $"The {role} plugin parent directory does not exist."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
                        $"The {role} path traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void AddDuplicateFormIdDiagnostics(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        PluginInspection inspection,
        string role)
    {
        foreach (var group in inspection.Records
                     .GroupBy(record => (
                         Owner: (record.OwnerPlugin ?? inspection.Plugin).Value.ToUpperInvariant(),
                         record.FormId.Value))
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(new Diagnostic("duplicate-form-id", DiagnosticSeverity.Error,
                $"The {role} plugin contains owner-qualified FormID " +
                $"{group.Key.Owner}|0x{group.Key.Value:X8} more than once."));
        }
    }

    private static ImmutableArray<PluginSurfaceAuditRecordChange> Compare(
        ImmutableArray<PluginRecordSummary> before,
        ImmutableArray<PluginRecordSummary> after, bool normalized)
    {
        var beforeMap = before.GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var afterMap = after.GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return beforeMap.Keys.Union(afterMap.Keys, StringComparer.Ordinal)
            .Select(key => CreateChange(key, beforeMap.GetValueOrDefault(key), afterMap.GetValueOrDefault(key), normalized))
            .Where(change => change is not null)
            .Select(change => change!)
            .OrderBy(change => change.FormId, StringComparer.Ordinal)
            .ThenBy(change => change.Signature, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static PluginSurfaceAuditRecordChange? CreateChange(
        string key,
        PluginRecordSummary? before,
        PluginRecordSummary? after, bool normalized)
    {
        var record = after ?? before!;
        var beforeFingerprint = before is null ? null : Fingerprint(before, normalized);
        var afterFingerprint = after is null ? null : Fingerprint(after, normalized);
        var changeKind = before is null ? "added" : after is null ? "removed" :
            string.Equals(beforeFingerprint, afterFingerprint, StringComparison.Ordinal) ? null : "changed";
        return changeKind is null
            ? null
            : new PluginSurfaceAuditRecordChange(
                record.FormId.ToString(),
                record.Signature,
                changeKind,
                beforeFingerprint,
                afterFingerprint);
    }

    private static string Key(PluginRecordSummary record) =>
        $"{record.OwnerPlugin?.Value.ToUpperInvariant() ?? "<UNKNOWN>"}:" +
        $"{record.FormId.Value:X8}:{record.Signature.ToUpperInvariant()}";

    private static string Fingerprint(PluginRecordSummary record, bool normalized)
    {
        // The full relocated bytes already include every typed field. Original
        // raw-index metadata must not reintroduce physical layout differences.
        if (normalized) return record.RawRecordSha256!;
        var builder = new StringBuilder()
            .Append(record.FormId.Value.ToString("X8", CultureInfo.InvariantCulture)).Append('|')
            .Append(record.Signature).Append('|')
            .Append(record.EditorId).Append('|')
            .Append(record.Name).Append('|')
            .Append(record.IsNpc).Append('|')
            .Append(record.IsDeleted).Append('|')
            .Append(record.ModelPath?.Value).Append('|');
        builder.Append(record.RawRecordSha256).Append('|');
        foreach (var outfit in record.OutfitItems) builder.Append(outfit.Value.ToString("X8", CultureInfo.InvariantCulture)).Append(',');
        builder.Append('|');
        if (record.NpcMetadata is { } metadata)
        {
            builder.Append(metadata.Sex).Append('|')
                .Append(metadata.RaceFormId?.Value.ToString("X8", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.SkyrimWeight?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.Fallout4ThinWeight?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.Fallout4MuscularWeight?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.Fallout4FatWeight?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.HasTemplate).Append('|')
                .Append(metadata.TemplateFormId?.Value.ToString("X8", CultureInfo.InvariantCulture)).Append('|')
                .Append(metadata.IsTemplateSource).Append('|')
                .Append(metadata.IsPlaced).Append('|')
                .Append(metadata.IsInLeveledList).Append('|')
                .Append(metadata.IsCharGenFacePreset).Append('|');
            foreach (var headPart in metadata.HeadPartFormIds) builder.Append(headPart.Value.ToString("X8", CultureInfo.InvariantCulture)).Append(',');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static PluginSurfaceAuditResult Invalid(
        PluginSurfaceAuditRequest request,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(
            false,
            1,
            request.Edition.ToWireName(),
            Path.GetFileName(request.Before.Value),
            Path.GetFileName(request.After.Value),
            0,
            0,
            0,
            0,
            0,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            ImmutableArray<PluginSurfaceAuditRecordChange>.Empty,
            diagnostics,
            ImmutableArray<PluginSurfaceAuditProviderResolution>.Empty);
}
