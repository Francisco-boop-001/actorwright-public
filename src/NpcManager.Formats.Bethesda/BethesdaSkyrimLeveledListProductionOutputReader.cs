using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class BethesdaSkyrimLeveledListProductionOutputReader :
    ISkyrimLeveledListProductionOutputReader
{
    public async ValueTask<SkyrimLeveledListProductionVerification> ReadAsync(
        SkyrimLeveledListProductionArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(outputPlugin.Value))
        {
            diagnostics.Add(Error("leveled-list-production-output-missing",
                "The output plugin does not exist for independent readback."));
            return Empty(outputPlugin, diagnostics);
        }

        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(outputPlugin.Value));
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            ILeveledItemGetter[] lists = mod.LeveledItems.ToArray();
            int majorCount = mod.EnumerateMajorRecords().Count();
            int otherCount = majorCount - lists.Length;
            if (lists.Length != 1 || otherCount != 0)
                diagnostics.Add(Error("leveled-list-production-record-surface",
                    "Independent readback requires exactly one LVLI and zero unrelated records."));

            ILeveledItemGetter? list = lists.Length == 1 ? lists[0] : null;
            FormId? target = FormId.TryParse(artifact.TargetFormId,
                out FormId parsedTarget) ? parsedTarget : null;
            bool selfOwned = list is not null && target is not null &&
                             list.FormKey.ModKey == outputKey &&
                             list.FormKey.ID == target.Value.Value;
            bool headerMatches = list is not null &&
                                 string.Equals(list.EditorID, artifact.EditorId,
                                     StringComparison.Ordinal) &&
                                 PercentByte(list.ChanceNone) == artifact.ChanceNone &&
                                 list.Flags == BuildFlags(artifact) &&
                                 artifact.MaxCount == 0;
            ILeveledItemEntryGetter[] actualEntries =
                list?.Entries?.ToArray() ?? [];
            bool entriesMatch = SameEntries(actualEntries, artifact.Entries);
            string[] actualMasters = mod.ModHeader.MasterReferences
                .Select(item => item.Master.FileName.String)
                .ToArray();
            bool mastersMatch = actualMasters.SequenceEqual(
                artifact.MasterDependencies,
                StringComparer.OrdinalIgnoreCase);

            if (!selfOwned)
                diagnostics.Add(Error("leveled-list-production-identity",
                    "Independent readback did not preserve the self-owned LVLI FormID."));
            if (!headerMatches)
                diagnostics.Add(Error("leveled-list-production-header",
                    "Independent readback did not preserve EDID, LVLF, LVLD, or zero Max Count."));
            if (!entriesMatch)
                diagnostics.Add(Error("leveled-list-production-entries",
                    "Independent readback did not preserve the ordered LVLO rows."));
            if (!mastersMatch)
                diagnostics.Add(Error("leveled-list-production-masters",
                    "Independent readback did not preserve the exact master order."));

            Sha256Hash hash = await HashFileAsync(outputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
            bool valid = lists.Length == 1 && otherCount == 0 && selfOwned &&
                         headerMatches && entriesMatch && mastersMatch &&
                         !HasErrors(diagnostics);
            return new(
                valid,
                outputPlugin,
                hash,
                lists.Length,
                otherCount,
                list is null ? null : new PluginName(list.FormKey.ModKey.FileName),
                list is null ? null : new FormId(list.FormKey.ID),
                selfOwned,
                headerMatches,
                entriesMatch,
                mastersMatch,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("leveled-list-production-output-read",
                exception.Message));
            return Empty(outputPlugin, diagnostics);
        }
    }

    private static bool SameEntries(
        ILeveledItemEntryGetter[] actual,
        ImmutableArray<SkyrimLeveledListProductionEntryArtifact> expected)
    {
        if (actual.Length != expected.Length) return false;
        for (int index = 0; index < expected.Length; index++)
        {
            ILeveledItemEntryDataGetter? data = actual[index].Data;
            if (data is null ||
                !FormReference.TryParse(expected[index].Item,
                    out FormReference reference) ||
                data.Level != expected[index].Level ||
                data.Count != expected[index].Count ||
                data.Reference.FormKey != new FormKey(
                    ModKey.FromNameAndExtension(reference.Plugin.Value),
                    reference.FormId.Value) ||
                expected[index].ChanceNone != 0)
                return false;
        }
        return true;
    }

    private static LeveledItem.Flag BuildFlags(
        SkyrimLeveledListProductionArtifact artifact) =>
        (artifact.CalculateAllLevels
            ? LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
        (artifact.CalculateEachInCount
            ? LeveledItem.Flag.CalculateForEachItemInCount : 0) |
        (artifact.UseAll ? LeveledItem.Flag.UseAll : 0);

    private static byte PercentByte(Percent percent) => checked((byte)Math.Round(
        percent.Value * 100D,
        MidpointRounding.AwayFromZero));

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private static SkyrimLeveledListProductionVerification Empty(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        output,
        null,
        0,
        0,
        null,
        null,
        false,
        false,
        false,
        false,
        diagnostics.ToImmutable());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
