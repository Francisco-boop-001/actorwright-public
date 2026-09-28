using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimLeveledEntryEditor()
    {
        PluginName plugin = new("NpcManager.esp");
        FormReference target = new(plugin, new FormId(0x901));
        FormReference armor = new(plugin, new FormId(0x800));
        FormReference nested = new(plugin, new FormId(0x902));
        FormReference deeper = new(plugin, new FormId(0x903));
        var armorCandidate = new SkyrimLeveledEntryCandidate(
            armor, SkyrimOutfitEditorItemKind.Armor, "Travel armor");
        SkyrimLeveledEntryEditorResult created =
            SkyrimLeveledEntryEditorRules.Apply(
                GameEdition.SkyrimSpecialEdition,
                SkyrimLeveledEntryEditorMode.Add,
                armorCandidate,
                10,
                short.MaxValue,
                0);
        Assert(created.Accepted && created.Entry is { } entry &&
               entry.Item == armor && entry.Level == 10 &&
               entry.Count == short.MaxValue && entry.ChanceNone == 0,
            "The Skyrim LVLO editor lost its qualified reference or signed-range values.");
        Assert(!SkyrimLeveledEntryEditorRules.Apply(
                    GameEdition.SkyrimSpecialEdition,
                    SkyrimLeveledEntryEditorMode.Add,
                    armorCandidate,
                    short.MaxValue + 1,
                    1,
                    0).Accepted &&
               !SkyrimLeveledEntryEditorRules.Apply(
                    GameEdition.SkyrimSpecialEdition,
                    SkyrimLeveledEntryEditorMode.Add,
                    armorCandidate,
                    1,
                    1,
                    1).Accepted,
            "Unrepresentable Skyrim LVLO values did not fail closed.");

        SkyrimLeveledListEditorDocument document =
            SkyrimLeveledListEditorRules.Create(
                GameEdition.SkyrimSpecialEdition,
                "Travel",
                0,
                0,
                false,
                false,
                false,
                []).Document ?? throw new InvalidOperationException(
                    "Could not create the LVLI header for entry transition tests.");
        SkyrimLeveledListDocumentEditResult first =
            SkyrimLeveledEntryEditorRules.Add(
                document, target, SkyrimOutfitEditorItemKind.Armor,
                created.Entry, null);
        SkyrimLeveledEntryEditorResult repeated =
            SkyrimLeveledEntryEditorRules.Apply(
                GameEdition.SkyrimSpecialEdition,
                SkyrimLeveledEntryEditorMode.Add,
                armorCandidate,
                20,
                2,
                0);
        SkyrimLeveledListDocumentEditResult second =
            SkyrimLeveledEntryEditorRules.Add(
                first.Document, target, SkyrimOutfitEditorItemKind.Armor,
                repeated.Entry, null);
        Assert(second.Accepted && second.Document is { Entries.Length: 2 },
            "The repeated-reference LVLO document was refused.");
        SkyrimLeveledListEditorDocument duplicated = second.Document ??
            throw new InvalidOperationException("The repeated-reference document is missing.");
        Assert(duplicated.Entries[0].Item == duplicated.Entries[1].Item &&
               duplicated.Entries.Select(item => item.Level)
                   .SequenceEqual(new ushort[] { 10, 20 }),
            "Legal repeated LVLO references or their order were lost.");

        SkyrimLeveledListDocumentEditResult replaced =
            SkyrimLeveledEntryEditorRules.Replace(
                duplicated, 0, target, SkyrimOutfitEditorItemKind.Armor,
                repeated.Entry, null);
        SkyrimLeveledListDocumentEditResult removed =
            SkyrimLeveledEntryEditorRules.Remove(replaced.Document, 1);
        Assert(removed.Accepted && removed.Document is { Entries.Length: 1 },
            "Immutable replace/remove refused a valid transition.");
        SkyrimLeveledListEditorDocument retained = removed.Document ??
            throw new InvalidOperationException("The retained LVLO document is missing.");
        Assert(retained.Entries[0].Level == 20,
            "Immutable replace/remove did not retain the intended ordered LVLO row.");

        var cycleEntry = new LeveledListEntryProposal(nested, 1, 1, 0);
        var cycleEvidence = new SkyrimLeveledListCycleEvidence(true,
            new Dictionary<FormReference, ImmutableArray<FormReference>>
            {
                [nested] = [deeper],
                [deeper] = [target],
                [target] = []
            }.ToImmutableDictionary());
        Assert(!SkyrimLeveledEntryEditorRules.Add(
                    retained, target, SkyrimOutfitEditorItemKind.LeveledList,
                    cycleEntry, cycleEvidence).Accepted &&
               !SkyrimLeveledEntryEditorRules.Add(
                    retained, target, SkyrimOutfitEditorItemKind.LeveledList,
                    new LeveledListEntryProposal(target, 1, 1, 0),
                    cycleEvidence).Accepted,
            "Self or transitive leveled-list cycle evidence did not fail closed.");
        FormReference caseVariantNested = new(
            new PluginName("npcmanager.esp"), nested.FormId);
        var ambiguousEvidence = new SkyrimLeveledListCycleEvidence(true,
            new Dictionary<FormReference, ImmutableArray<FormReference>>
            {
                [nested] = [],
                [caseVariantNested] = []
            }.ToImmutableDictionary());
        Assert(!SkyrimLeveledEntryEditorRules.Add(
                    retained, target, SkyrimOutfitEditorItemKind.LeveledList,
                    cycleEntry, ambiguousEvidence).Accepted,
            "Case-variant duplicate adjacency nodes did not fail closed.");
        Assert(!SkyrimLeveledEntryEditorRules.Cancel().Accepted,
            "Leveled-entry Cancel returned an accepted row.");

        await VerifyRepeatedReferenceBinaryRoundTrip();
    }

    private static async Task VerifyRepeatedReferenceBinaryRoundTrip()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "leveled-entry-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "Source.esp");
            ModKey sourceKey = ModKey.FromNameAndExtension("Source.esp");
            var source = new SkyrimMod(sourceKey, SkyrimRelease.SkyrimSE);
            source.Armors.Add(new Armor(
                new FormKey(sourceKey, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "SourceArmor",
                Name = "Source armor"
            });
            source.LeveledItems.Add(new LeveledItem(
                new FormKey(sourceKey, 0x801), SkyrimRelease.SkyrimSE)
            {
                EditorID = "SourceList"
            });
            source.WriteToBinary(new FilePath(sourcePath),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });

            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath("F:\\ExampleGame"));
            var reader = new BethesdaPluginReader();
            var proposalService = new LeveledListProposalService(
                reader, policy, labRoot);
            WorkspacePath proposalPath = new(
                Path.Combine(root, "duplicates.leveled-list-proposal.json"));
            FormReference armor = new(
                new PluginName("Source.esp"), new FormId(0x800));
            LeveledListProposalResult proposal =
                await proposalService.ProposeAsync(
                    new LeveledListProposalRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(sourcePath),
                        new FormId(0x801),
                        new EditorId("SourceList"),
                        0,
                        0,
                        false,
                        false,
                        true,
                        [
                            new LeveledListEntryProposal(armor, 1, 1, 0),
                            new LeveledListEntryProposal(armor, 20, 2, 0)
                        ],
                        proposalPath),
                    CancellationToken.None);
            Assert(proposal.Written && proposal.Artifact?.Entries.Length == 2,
                "The typed proposal rejected legal repeated LVLO references.");

            WorkspacePath output = new(Path.Combine(root, "Output.esp"));
            var writer = new BethesdaLeveledListBinaryWriteService(policy, labRoot);
            LeveledListBinaryWriteResult write = await writer.WriteAsync(
                new LeveledListBinaryWriteRequest(
                    GameEdition.SkyrimSpecialEdition, proposalPath, output),
                CancellationToken.None);
            Assert(write.Written,
                "The Skyrim LVLI writer rejected repeated references: " +
                string.Join("; ", write.Diagnostics.Select(item => item.Message)));

            ModKey outputKey = ModKey.FromNameAndExtension("Output.esp");
            using var reopened = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(output.Value)),
                SkyrimRelease.SkyrimSE);
            ILeveledItemEntryGetter[] entries = reopened.LeveledItems.Single()
                .Entries?.ToArray() ?? [];
            Assert(entries.Length == 2 &&
                   entries.All(item => item.Data?.Reference.FormKey ==
                       new FormKey(sourceKey, 0x800)) &&
                   entries.Select(item => item.Data!.Level)
                       .SequenceEqual(new short[] { 1, 20 }) &&
                   entries.Select(item => item.Data!.Count)
                       .SequenceEqual(new short[] { 1, 2 }),
                "Independent Skyrim LVLI readback lost repeated-reference row order or values.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
