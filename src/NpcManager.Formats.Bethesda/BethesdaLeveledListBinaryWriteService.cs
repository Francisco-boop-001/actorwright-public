using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Materializes one hash-bound LVLI proposal into a new ordinary plugin.
/// The source record, hash, links, and supported game fields are revalidated;
/// the temporary binary is independently read back before promotion.
/// </summary>
public sealed class BethesdaLeveledListBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : ILeveledListBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<LeveledListBinaryWriteResult> WriteAsync(
        LeveledListBinaryWriteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());

        LeveledListProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The leveled-list proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<LeveledListProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-empty", DiagnosticSeverity.Error,
                "The leveled-list proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        if (!string.Equals(proposal.ArtifactKind, "leveled-list-record-proposal", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-kind", DiagnosticSeverity.Error,
                "The proposal artifact kind must be leveled-list-record-proposal."));
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var edition) || edition != request.Edition)
            diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-edition", DiagnosticSeverity.Error,
                "The proposal edition does not match the write request."));
        if (!FormId.TryParse(proposal.ListFormId, out var listFormId) || listFormId.Value == 0)
            diagnostics.Add(new Diagnostic("leveled-list-binary-list-form", DiagnosticSeverity.Error,
                "The proposal list FormID is invalid."));
        if (proposal.ChanceNone > 100)
            diagnostics.Add(new Diagnostic("leveled-list-binary-chance-range", DiagnosticSeverity.Error,
                "The list chance-none value must be 0..100."));
        if (proposal.Entries.IsDefaultOrEmpty || proposal.Entries.Length > 4096)
            diagnostics.Add(new Diagnostic("leveled-list-binary-entries-count", DiagnosticSeverity.Error,
                "The proposal must contain 1 to 4096 entries."));

        EditorId? editorId = null;
        if (!string.IsNullOrWhiteSpace(proposal.EditorId))
        {
            try { editorId = new EditorId(proposal.EditorId); }
            catch (ArgumentException)
            { diagnostics.Add(new Diagnostic("leveled-list-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is invalid.")); }
        }

        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics, "source");
        if (sourcePath is null || !File.Exists(sourcePath.Value.Value))
            diagnostics.Add(new Diagnostic("leveled-list-binary-source-missing", DiagnosticSeverity.Error,
                "The proposal source plugin does not exist under the K-only lab root."));
        else
        {
            AddReparseDiagnostic(diagnostics, sourcePath.Value.Value, "source");
            if (!string.Equals(Path.GetFileName(sourcePath.Value.Value), Path.GetFileName(proposal.SourcePlugin), StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("leveled-list-binary-source-name-mismatch", DiagnosticSeverity.Error,
                    "The proposal source plugin filename must match the source path filename."));
        }
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics);
        var itemReferences = ParseEntries(proposal, diagnostics);
        if (request.Edition == GameEdition.SkyrimSpecialEdition && proposal.MaxCount != 0)
            diagnostics.Add(new Diagnostic("leveled-list-binary-sse-max-count-unsupported", DiagnosticSeverity.Error,
                "Skyrim SE LVLI has no serializable MaxCount field in the supported Mutagen model; use maxCount 0."));
        if (request.Edition == GameEdition.SkyrimSpecialEdition && proposal.Entries.Any(entry => entry.ChanceNone != 0))
            diagnostics.Add(new Diagnostic("leveled-list-binary-sse-entry-chance-unsupported", DiagnosticSeverity.Error,
                "Skyrim SE LVLI entry chance-none values are not represented by the supported record model; use zero entry chance values."));
        if (sourcePath is null || sourcePlugin is null || listFormId.Value == 0 || HasErrors(diagnostics))
            return Refused(request, listFormId.Value == 0 ? null : listFormId, diagnostics.ToImmutable());

        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("leveled-list-binary-input-hash-mismatch", DiagnosticSeverity.Error,
                    "The proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (sourcePlugin.Value == outputModKey)
                diagnostics.Add(new Diagnostic("leveled-list-binary-output-master-self", DiagnosticSeverity.Error,
                    "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, listFormId, diagnostics.ToImmutable());

            var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                switch (request.Edition)
                {
                    case GameEdition.Fallout4:
                        WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, listFormId, itemReferences, editorId, temporary);
                        VerifyFallout4(temporary, sourcePlugin.Value, outputModKey, proposal, listFormId, itemReferences, editorId, diagnostics);
                        break;
                    case GameEdition.SkyrimSpecialEdition:
                        WriteSkyrim(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, listFormId, itemReferences, editorId, temporary);
                        VerifySkyrim(temporary, sourcePlugin.Value, outputModKey, proposal, listFormId, itemReferences, editorId, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new Diagnostic("leveled-list-binary-edition-unsupported", DiagnosticSeverity.Error,
                            "The requested game edition is unsupported."));
                        break;
                }
                if (HasErrors(diagnostics)) return Refused(request, listFormId, diagnostics.ToImmutable());
                File.Move(temporary, request.Output.Value, overwrite: false);
                var outputHash = new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(request.Output.Value, cancellationToken))));
                return new LeveledListBinaryWriteResult(true, request.Proposal, request.Output,
                    listFormId, outputHash, diagnostics.ToImmutable());
            }
            finally { TryDelete(temporary); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("leveled-list-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, listFormId, diagnostics.ToImmutable());
        }
    }

    private static ImmutableArray<LeveledListEntryArtifact> ParseEntries(LeveledListProposalArtifact proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var entries = ImmutableArray.CreateBuilder<LeveledListEntryArtifact>();
        foreach (var entry in proposal.Entries)
        {
            if (!FormReference.TryParse(entry.Item, out var reference) || reference.FormId.Value == 0)
                diagnostics.Add(new Diagnostic("leveled-list-binary-item-invalid", DiagnosticSeverity.Error,
                    $"Leveled-list item '{entry.Item}' is not a valid non-null FormReference."));
            if (entry.Level == 0 || entry.Level > short.MaxValue || entry.Count == 0 || entry.Count > short.MaxValue || entry.ChanceNone > 100)
                diagnostics.Add(new Diagnostic("leveled-list-binary-entry-range", DiagnosticSeverity.Error,
                    "Leveled-list entry level/count must fit signed 16-bit fields and chance-none must be 0..100."));
            entries.Add(entry);
        }
        return entries.ToImmutable();
    }

    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        LeveledListProposalArtifact proposal, FormId listFormId,
        ImmutableArray<LeveledListEntryArtifact> entries, EditorId? editorId, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var sourceKey = new FormKey(sourceModKey, listFormId.Value);
        if (overlay.LeveledItems.FirstOrDefault(item => item.FormKey == sourceKey) is null)
            throw new InvalidDataException($"LVLI source record {proposal.ListFormId} was not found in the source plugin.");
        ValidateEntryMasters(entries, sourceModKey, overlay.ModHeader.MasterReferences.Select(master => master.Master));
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, entries, outputModKey);
        var list = new Fo4.LeveledItem(sourceKey, Fo4.Fallout4Release.Fallout4)
        {
            EditorID = editorId?.Value,
            ChanceNone = PercentFromByte(proposal.ChanceNone),
            MaxCount = proposal.MaxCount == 0 ? null : proposal.MaxCount,
            Flags = BuildFo4Flags(proposal)
        };
        list.Entries ??= [];
        foreach (var entry in entries)
        {
            if (!FormReference.TryParse(entry.Item, out var reference)) throw new InvalidDataException("Invalid LVLI item reference.");
            list.Entries.Add(new Fo4.LeveledItemEntry
            {
                Data = new Fo4.LeveledItemEntryData
                {
                    Level = checked((short)entry.Level),
                    Count = checked((short)entry.Count),
                    Reference = new FormLink<Fo4.IItemGetter>(ToFormKey(reference)),
                    ChanceNone = PercentFromByte(entry.ChanceNone)
                }
            });
        }
        mod.LeveledItems.Add(list);
        WriteMod(mod, destination);
    }

    private static void WriteSkyrim(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        LeveledListProposalArtifact proposal, FormId listFormId,
        ImmutableArray<LeveledListEntryArtifact> entries, EditorId? editorId, string destination)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Sse.SkyrimRelease.SkyrimSE);
        var sourceKey = new FormKey(sourceModKey, listFormId.Value);
        if (overlay.LeveledItems.FirstOrDefault(item => item.FormKey == sourceKey) is null)
            throw new InvalidDataException($"LVLI source record {proposal.ListFormId} was not found in the source plugin.");
        ValidateEntryMasters(entries, sourceModKey, overlay.ModHeader.MasterReferences.Select(master => master.Master));
        var mod = new Sse.SkyrimMod(outputModKey, Sse.SkyrimRelease.SkyrimSE);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, entries, outputModKey);
        var list = new Sse.LeveledItem(sourceKey, Sse.SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId?.Value,
            ChanceNone = PercentFromByte(proposal.ChanceNone),
            Flags = BuildSseFlags(proposal)
        };
        list.Entries ??= [];
        foreach (var entry in entries)
        {
            if (!FormReference.TryParse(entry.Item, out var reference)) throw new InvalidDataException("Invalid LVLI item reference.");
            list.Entries.Add(new Sse.LeveledItemEntry
            {
                Data = new Sse.LeveledItemEntryData
                {
                    Level = checked((short)entry.Level),
                    Count = checked((short)entry.Count),
                    Reference = new FormLink<Sse.IItemGetter>(ToFormKey(reference))
                }
            });
        }
        mod.LeveledItems.Add(list);
        WriteMod(mod, destination);
    }

    private static void VerifyFallout4(string path, ModKey sourceModKey, ModKey outputModKey,
        LeveledListProposalArtifact proposal, FormId listFormId,
        ImmutableArray<LeveledListEntryArtifact> entries, EditorId? editorId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var records = mod.LeveledItems.ToArray();
        var actual = records.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, listFormId.Value));
        var actualEntries = actual?.Entries?.ToArray() ?? [];
        if (records.Length != 1 || actual is null || !SameHeader(actual, proposal, editorId) ||
            actualEntries.Length != entries.Length || !SameFo4Entries(actualEntries, entries))
            diagnostics.Add(new Diagnostic("leveled-list-binary-readback-mismatch", DiagnosticSeverity.Error,
                "Independent FO4 LVLI read-back did not match the proposal."));
    }

    private static void VerifySkyrim(string path, ModKey sourceModKey, ModKey outputModKey,
        LeveledListProposalArtifact proposal, FormId listFormId,
        ImmutableArray<LeveledListEntryArtifact> entries, EditorId? editorId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var records = mod.LeveledItems.ToArray();
        var actual = records.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, listFormId.Value));
        var actualEntries = actual?.Entries?.ToArray() ?? [];
        if (records.Length != 1 || actual is null || !SameHeader(actual, proposal, editorId) ||
            actualEntries.Length != entries.Length || !SameSseEntries(actualEntries, entries))
            diagnostics.Add(new Diagnostic("leveled-list-binary-readback-mismatch", DiagnosticSeverity.Error,
                "Independent Skyrim SE LVLI read-back did not match the proposal."));
    }

    private static bool SameHeader(Fo4.ILeveledItemGetter actual, LeveledListProposalArtifact expected, EditorId? editorId) =>
        string.Equals(actual.EditorID, editorId?.Value, StringComparison.Ordinal) &&
        actual.ChanceNone == PercentFromByte(expected.ChanceNone) &&
        actual.MaxCount == (expected.MaxCount == 0 ? null : expected.MaxCount) &&
        actual.Flags == BuildFo4Flags(expected);

    private static bool SameHeader(Sse.ILeveledItemGetter actual, LeveledListProposalArtifact expected, EditorId? editorId) =>
        string.Equals(actual.EditorID, editorId?.Value, StringComparison.Ordinal) &&
        actual.ChanceNone == PercentFromByte(expected.ChanceNone) && actual.Flags == BuildSseFlags(expected);

    private static bool SameFo4Entries(Fo4.ILeveledItemEntryGetter[] actual, ImmutableArray<LeveledListEntryArtifact> expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            var data = actual[index].Data;
            if (data is null || !FormReference.TryParse(expected[index].Item, out var reference) ||
                data.Level != expected[index].Level || data.Count != expected[index].Count ||
                data.Reference.FormKey != ToFormKey(reference) || data.ChanceNone != PercentFromByte(expected[index].ChanceNone))
                return false;
        }
        return true;
    }

    private static bool SameSseEntries(Sse.ILeveledItemEntryGetter[] actual, ImmutableArray<LeveledListEntryArtifact> expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            var data = actual[index].Data;
            if (data is null || !FormReference.TryParse(expected[index].Item, out var reference) ||
                data.Level != expected[index].Level || data.Count != expected[index].Count ||
                data.Reference.FormKey != ToFormKey(reference))
                return false;
        }
        return true;
    }

    private static Fo4.LeveledItem.Flag BuildFo4Flags(LeveledListProposalArtifact proposal) =>
        (proposal.CalculateAllLevels ? Fo4.LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
        (proposal.CalculateEachInCount ? Fo4.LeveledItem.Flag.CalculateForEachItemInCount : 0) |
        (proposal.UseAll ? Fo4.LeveledItem.Flag.UseAll : 0);

    private static Sse.LeveledItem.Flag BuildSseFlags(LeveledListProposalArtifact proposal) =>
        (proposal.CalculateAllLevels ? Sse.LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
        (proposal.CalculateEachInCount ? Sse.LeveledItem.Flag.CalculateForEachItemInCount : 0) |
        (proposal.UseAll ? Sse.LeveledItem.Flag.UseAll : 0);

    private static Percent PercentFromByte(byte value) => new(value / 100d);

    private static void AddMasters(ExtendedList<MasterReference> masters, ModKey source,
        ImmutableArray<LeveledListEntryArtifact> entries, ModKey output)
    {
        foreach (var key in new[] { source }.Concat(entries.Select(entry => FormReference.TryParse(entry.Item, out var reference) ? ToFormKey(reference).ModKey : default))
                     .Where(key => key != default && key != output).Distinct())
            masters.Add(new MasterReference { Master = key });
    }

    private static void ValidateEntryMasters(ImmutableArray<LeveledListEntryArtifact> entries,
        ModKey source, IEnumerable<ModKey> declaredMasters)
    {
        var allowed = declaredMasters.Append(source).ToHashSet();
        foreach (var entry in entries)
        {
            if (!FormReference.TryParse(entry.Item, out var reference))
                throw new InvalidDataException($"Leveled-list item '{entry.Item}' is invalid.");
            var plugin = new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin);
            if (!allowed.Contains(plugin))
                throw new InvalidDataException($"Leveled-list item '{entry.Item}' is not provided by the source plugin or its declared masters.");
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(LeveledListBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("leveled-list-binary-output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("leveled-list-binary-output-extension", DiagnosticSeverity.Error, "The bounded LVLI writer emits ordinary .esp plugins only."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("leveled-list-binary-output-exists", DiagnosticSeverity.Error, "Binary leveled-list writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("leveled-list-binary-output-parent", DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("leveled-list-binary-proposal-missing", DiagnosticSeverity.Error, "The leveled-list proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("leveled-list-binary-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("leveled-list-binary-source-outside-lab", DiagnosticSeverity.Error, $"The {role} path must remain under the K-only lab root."));
            return path;
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("leveled-list-binary-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static FormKey ToFormKey(FormReference reference) => new(new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin), reference.FormId.Value);
    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination), new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck });

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("leveled-list-binary-reparse", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("leveled-list-binary-path-inspection", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static LeveledListBinaryWriteResult Refused(LeveledListBinaryWriteRequest request, FormId? listFormId, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, listFormId, null, diagnostics);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
