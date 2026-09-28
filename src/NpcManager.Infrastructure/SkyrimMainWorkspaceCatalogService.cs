using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Revalidates a reviewed plugin closure, resolves the last provider in exact
/// reviewed order, and projects one immutable NPC_/LVLN browser snapshot.
/// </summary>
public sealed class SkyrimMainWorkspaceCatalogService : ISkyrimMainWorkspaceCatalogService
{
    private readonly ISkyrimMainWorkspacePluginReader pluginReader;
    private readonly Func<string, CancellationToken, ValueTask<Sha256Hash>> hashAsync;

    public SkyrimMainWorkspaceCatalogService(ISkyrimMainWorkspacePluginReader pluginReader)
        : this(pluginReader, HashAsync)
    {
    }

    internal SkyrimMainWorkspaceCatalogService(ISkyrimMainWorkspacePluginReader pluginReader,
        Func<string, CancellationToken, ValueTask<Sha256Hash>> hashAsync)
    {
        this.pluginReader = pluginReader;
        this.hashAsync = hashAsync;
    }

    public async ValueTask<SkyrimMainWorkspaceCatalogResult> LoadAsync(
        SkyrimMainWorkspaceCatalogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition !=
            GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error(
                "workspace-edition-not-skyrim",
                "The Skyrim main workspace requires a reviewed Skyrim SE intake."));
            return Refused(diagnostics);
        }

        PluginClosureReviewEntry[] entries = request.Intake.Plugins
            .Where(entry =>
                entry.Enabled && entry.Exists && entry.ReadSucceeded)
            .OrderBy(entry => entry.Order)
            .ThenBy(
                entry => entry.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                entry => entry.Plugin.Value,
                StringComparer.Ordinal)
            .ToArray();
        foreach (IGrouping<string, PluginClosureReviewEntry> duplicate in
                 entries.GroupBy(
                     entry => entry.Plugin.Value,
                     StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            diagnostics.Add(Error(
                "workspace-plugin-duplicate",
                $"Reviewed intake contains plugin '{duplicate.Key}' more than once."));
        if (HasErrors(diagnostics))
            return Refused(diagnostics);
        var reviewedOrder = entries.ToDictionary(
            entry => entry.Plugin,
            entry => entry.Order);
        var providers =
            new Dictionary<RecordKey,
                List<SkyrimMainWorkspacePluginRecord>>();

        foreach (PluginClosureReviewEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.SourceHash is not { } reviewedHash)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-source-hash-missing",
                    $"Reviewed plugin '{entry.Plugin}' has no source hash."));
                continue;
            }
            if (!File.Exists(entry.Path.Value))
            {
                diagnostics.Add(Error(
                    "workspace-plugin-missing",
                    $"Reviewed plugin '{entry.Plugin}' is no longer present."));
                continue;
            }

            Sha256Hash currentHash;
            try
            {
                currentHash = await hashAsync(
                    entry.Path.Value,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-hash-failed",
                    $"Reviewed plugin '{entry.Plugin}' could not be hashed: {exception.Message}"));
                continue;
            }
            if (currentHash != reviewedHash)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-hash-changed",
                    $"Reviewed plugin '{entry.Plugin}' changed after intake review."));
                continue;
            }

            SkyrimMainWorkspacePluginReadResult read;
            try
            {
                read = await Task.Run(() => pluginReader.Read(entry.Path), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-read-failed",
                    $"Reviewed plugin '{entry.Plugin}' could not be read: {exception.Message}"));
                continue;
            }
            Sha256Hash afterReadHash;
            try
            {
                afterReadHash = await hashAsync(
                    entry.Path.Value,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-rehash-failed",
                    $"Reviewed plugin '{entry.Plugin}' could not be rehashed after reading: {exception.Message}"));
                continue;
            }
            if (afterReadHash != currentHash)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-changed-during-read",
                    $"Reviewed plugin '{entry.Plugin}' changed while its records were being read."));
                continue;
            }

            if (read.Plugin != entry.Plugin)
            {
                diagnostics.Add(Error(
                    "workspace-plugin-identity-changed",
                    $"Reviewed plugin '{entry.Plugin}' reopened as '{read.Plugin}'."));
                continue;
            }
            if (!read.Masters.SequenceEqual(entry.Masters))
            {
                diagnostics.Add(Error(
                    "workspace-plugin-masters-changed",
                    $"Reviewed plugin '{entry.Plugin}' has a different master list."));
                continue;
            }

            foreach (SkyrimMainWorkspacePluginRecord record in read.Records)
            {
                if (record.Provider != entry.Plugin)
                {
                    diagnostics.Add(Error(
                        "workspace-record-provider-changed",
                        $"Record {record.Owner}|{record.FormId} named provider '{record.Provider}' instead of '{entry.Plugin}'."));
                    continue;
                }
                var key = new RecordKey(
                    record.Owner, record.FormId, record.Signature);
                if (!providers.TryGetValue(key, out var chain))
                {
                    chain = [];
                    providers.Add(key, chain);
                }
                chain.Add(record);
            }
        }

        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        ImmutableArray<SkyrimMainWorkspaceRecord> projected;
        try
        {
            var winners = providers.ToDictionary(
                item => item.Key,
                item => item.Value[^1]);
            projected = providers.Select(item =>
                    Project(
                        item.Key,
                        item.Value,
                        winners))
                .OrderBy(record =>
                    reviewedOrder[record.Identity.WinningProvider])
                .ThenBy(record => record.Kind)
                .ThenBy(
                    record => record.EditorId ?? record.Name ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    record => record.EditorId ?? record.Name ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(record => record.Identity.FormId.Value)
                .ToImmutableArray();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or KeyNotFoundException)
        {
            diagnostics.Add(Error(
                "workspace-catalog-resolution-failed",
                exception.Message));
            return Refused(diagnostics);
        }
        ImmutableArray<Diagnostic> acceptedDiagnostics =
            diagnostics.ToImmutable();
        var snapshot = new SkyrimMainWorkspaceSnapshot(
            request.Intake.IntakeFingerprint,
            projected,
            acceptedDiagnostics);
        return new SkyrimMainWorkspaceCatalogResult(
            true, snapshot, acceptedDiagnostics);
    }

    private static SkyrimMainWorkspaceRecord Project(
        RecordKey key,
        List<SkyrimMainWorkspacePluginRecord> chain,
        IReadOnlyDictionary<RecordKey,
            SkyrimMainWorkspacePluginRecord> winners)
    {
        SkyrimMainWorkspacePluginRecord winner = chain[^1];
        SkyrimMainWorkspaceRecordKind kind =
            key.Signature switch
            {
                "NPC_" => SkyrimMainWorkspaceRecordKind.Npc,
                "LVLN" => SkyrimMainWorkspaceRecordKind.LeveledNpc,
                _ => throw new InvalidDataException(
                    $"Unsupported main-workspace signature '{key.Signature}'.")
            };
        var identity = new SkyrimMainWorkspaceIdentity(
            key.Owner,
            winner.Provider,
            key.FormId,
            key.Signature);
        ImmutableArray<SkyrimMainWorkspaceIdentity> entries =
            winner.LeveledNpcEntries
                .Select(reference =>
                    ResolveEntry(reference, winners))
                .ToImmutableArray();
        bool changed = chain.Count > 1 ||
                       winner.Owner != winner.Provider;
        NpcChangeState changeState = winner.IsDeleted
            ? NpcChangeState.Deleted
            : changed
                ? NpcChangeState.Changed
                : NpcChangeState.Unchanged;
        return new SkyrimMainWorkspaceRecord(
            identity,
            kind,
            winner.EditorId,
            winner.Name,
            winner.IsDeleted,
            kind == SkyrimMainWorkspaceRecordKind.LeveledNpc &&
            entries.IsEmpty,
            winner.NpcMetadata?.Sex,
            kind == SkyrimMainWorkspaceRecordKind.Npc
                ? Classify(winner.NpcMetadata)
                : [],
            changeState,
            chain.Select(record => record.Provider)
                .ToImmutableArray(),
            entries,
            winner.RawRecordSha256.Value);
    }

    private static SkyrimMainWorkspaceIdentity ResolveEntry(
        FormReference reference,
        IReadOnlyDictionary<RecordKey,
            SkyrimMainWorkspacePluginRecord> winners)
    {
        KeyValuePair<RecordKey, SkyrimMainWorkspacePluginRecord>[] matches =
            winners.Where(item =>
                    item.Key.Owner == reference.Plugin &&
                    item.Key.FormId == reference.FormId &&
                    item.Key.Signature is "NPC_" or "LVLN")
                .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"LVLN entry '{reference}' did not resolve to exactly one reviewed NPC_/LVLN winner.");
        return new SkyrimMainWorkspaceIdentity(
            reference.Plugin,
            matches[0].Value.Provider,
            reference.FormId,
            matches[0].Key.Signature);
    }

    private static ImmutableArray<NpcCategory> Classify(
        NpcRecordMetadata? metadata)
    {
        if (metadata is null)
            return [];
        var categories =
            ImmutableArray.CreateBuilder<NpcCategory>();
        bool inWorld =
            metadata.IsPlaced || metadata.IsInLeveledList;
        if (inWorld)
            categories.Add(
                metadata.HasTemplate
                    ? NpcCategory.Generic
                    : NpcCategory.Unique);
        if (metadata.IsTemplateSource)
            categories.Add(NpcCategory.Template);
        if (!inWorld &&
            !metadata.IsTemplateSource &&
            !metadata.IsCharGenFacePreset)
            categories.Add(NpcCategory.Unused);
        return categories.ToImmutable();
    }

    private static async ValueTask<Sha256Hash> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static SkyrimMainWorkspaceCatalogResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private readonly record struct RecordKey(
        PluginName Owner,
        FormId FormId,
        string Signature);
}
