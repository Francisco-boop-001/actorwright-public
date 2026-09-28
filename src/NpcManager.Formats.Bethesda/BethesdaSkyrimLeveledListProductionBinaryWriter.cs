using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class BethesdaSkyrimLeveledListProductionBinaryWriter(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimLeveledListProductionBinaryWriter
{
    public async ValueTask<SkyrimLeveledListProductionWriteResult> WriteAsync(
        SkyrimLeveledListProductionArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(artifact, outputPlugin).ToBuilder();
        if (HasErrors(diagnostics))
            return Refused(outputPlugin, diagnostics);

        string temporary = outputPlugin.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(outputPlugin.Value));
            var mod = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
            foreach (string dependency in artifact.MasterDependencies)
                mod.ModHeader.MasterReferences.Add(new MasterReference
                {
                    Master = ModKey.FromNameAndExtension(dependency)
                });

            if (!FormId.TryParse(artifact.TargetFormId, out FormId target))
                throw new InvalidDataException("The proposal target FormID is invalid.");
            var list = new LeveledItem(
                new FormKey(outputKey, target.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = artifact.EditorId,
                ChanceNone = new Percent(artifact.ChanceNone / 100D),
                Flags = BuildFlags(artifact),
                Entries = []
            };
            foreach (SkyrimLeveledListProductionEntryArtifact entry in artifact.Entries)
            {
                if (!FormReference.TryParse(entry.Item, out FormReference reference))
                    throw new InvalidDataException(
                        $"Leveled-list item '{entry.Item}' is invalid.");
                list.Entries.Add(new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = checked((short)entry.Level),
                        Count = checked((short)entry.Count),
                        Reference = new FormLink<IItemGetter>(new FormKey(
                            ModKey.FromNameAndExtension(reference.Plugin.Value),
                            reference.FormId.Value))
                    }
                });
            }
            mod.LeveledItems.Add(list);
            mod.WriteToBinary(new FilePath(temporary), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
            File.Move(temporary, outputPlugin.Value, overwrite: false);
            Sha256Hash hash = await HashFileAsync(
                outputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
            return new(true, outputPlugin, hash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           OverflowException)
        {
            TryDelete(temporary);
            diagnostics.Add(Error("leveled-list-production-binary-write",
                exception.Message));
            return Refused(outputPlugin, diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> Validate(
        SkyrimLeveledListProductionArtifact artifact,
        WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot))
            diagnostics.Add(Error("leveled-list-production-output-root",
                "The new LVLI output must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("leveled-list-production-output-extension",
                "The new LVLI output must be an ordinary .esp plugin."));
        if (File.Exists(output.Value))
            diagnostics.Add(Error("leveled-list-production-output-exists",
                "The new LVLI writer never overwrites an existing plugin."));
        string? parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error("leveled-list-production-output-parent",
                "The new LVLI output directory must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!string.Equals(artifact.SchemaVersion, "1", StringComparison.Ordinal) ||
            !string.Equals(artifact.ArtifactKind,
                "skyrim-new-leveled-list-production-proposal",
                StringComparison.Ordinal) ||
            !string.Equals(artifact.Edition, "skyrimse", StringComparison.Ordinal) ||
            !artifact.NewSelfOwnedRecord || !artifact.NoUnrelatedRecords)
            diagnostics.Add(Error("leveled-list-production-artifact-contract",
                "The proposal is not a supported new Skyrim LVLI artifact."));
        if (!string.Equals(artifact.OutputPlugin, Path.GetFileName(output.Value),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("leveled-list-production-output-binding",
                "The output filename does not match the reviewed proposal."));
        if (artifact.MaxCount != 0)
            diagnostics.Add(Error("leveled-list-production-max-count",
                "Skyrim production writes require Max Count zero."));
        return diagnostics.ToImmutable();
    }

    private static LeveledItem.Flag BuildFlags(
        SkyrimLeveledListProductionArtifact artifact) =>
        (artifact.CalculateAllLevels
            ? LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
        (artifact.CalculateEachInCount
            ? LeveledItem.Flag.CalculateForEachItemInCount : 0) |
        (artifact.UseAll ? LeveledItem.Flag.UseAll : 0);

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

    private static SkyrimLeveledListProductionWriteResult Refused(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, null, diagnostics.ToImmutable());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
