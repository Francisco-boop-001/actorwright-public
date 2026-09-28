using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimLeveledListProductionWorkflow()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "leveled-list-production-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string basePath = Path.Combine(dataRoot, "OutfitBase.esm");
            string providerPath = Path.Combine(dataRoot, "OutfitProvider.esp");
            CreateOutfitProductionBase(basePath);
            CreateOutfitProductionOverride(providerPath);
            ReviewedGameIntake intake = CreateOutfitReviewedIntake(
                root,
                dataRoot,
                basePath,
                providerPath);

            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var catalogReader = new BethesdaSkyrimOutfitItemCatalogReader();
            var loader = new SkyrimLeveledListProductionLoadService(catalogReader);
            SkyrimLeveledListProductionLoadResult loaded = await loader.LoadAsync(
                new SkyrimLeveledListProductionLoadRequest(intake, 11),
                CancellationToken.None);
            SkyrimLeveledListProductionState state = loaded.State ??
                throw new InvalidDataException(
                    "The new-LVLI loader omitted its accepted state: " +
                    FormatOutfitDiagnostics(loaded.Diagnostics));
            SkyrimOutfitEditorItem armor = state.EntryCandidates.Single(item =>
                item.Kind == SkyrimOutfitEditorItemKind.Armor &&
                item.Reference.FormId == new FormId(0x800));

            SkyrimLeveledListEditorDocument header =
                SkyrimLeveledListEditorRules.Create(
                    GameEdition.SkyrimSpecialEdition,
                    "ProductionCreated",
                    25,
                    0,
                    true,
                    true,
                    true,
                    state.ExistingEditorIds).Document ??
                throw new InvalidDataException("Could not create the production LVLI header.");
            SkyrimLeveledEntryEditorResult row = SkyrimLeveledEntryEditorRules.Apply(
                GameEdition.SkyrimSpecialEdition,
                SkyrimLeveledEntryEditorMode.Add,
                new SkyrimLeveledEntryCandidate(
                    armor.Reference,
                    armor.Kind,
                    armor.DisplayName),
                1,
                1,
                0);
            SkyrimLeveledListDocumentEditResult withEntry =
                SkyrimLeveledEntryEditorRules.Add(
                    header,
                    new FormReference(
                        new PluginName("CreatedList.esp"),
                        new FormId(0xA10)),
                    armor.Kind,
                    row.Entry,
                    null);
            SkyrimLeveledEntryEditorResult boundaryRow =
                SkyrimLeveledEntryEditorRules.Apply(
                    GameEdition.SkyrimSpecialEdition,
                    SkyrimLeveledEntryEditorMode.Add,
                    new SkyrimLeveledEntryCandidate(
                        armor.Reference,
                        armor.Kind,
                        armor.DisplayName),
                    short.MaxValue,
                    short.MaxValue,
                    0);
            SkyrimLeveledListDocumentEditResult withBoundary =
                SkyrimLeveledEntryEditorRules.Add(
                    withEntry.Document,
                    new FormReference(
                        new PluginName("CreatedList.esp"),
                        new FormId(0xA10)),
                    armor.Kind,
                    boundaryRow.Entry,
                    null);
            SkyrimLeveledListEditorDocument document = withBoundary.Document ??
                throw new InvalidDataException(
                    "Could not attach the repeated boundary LVLO rows to the production document.");

            WorkspacePath proposalPath = new(Path.Combine(
                root,
                "created.leveled-list-production-proposal.json"));
            WorkspacePath outputPath = new(Path.Combine(root, "CreatedList.esp"));
            var request = new SkyrimLeveledListProductionRequest(
                state,
                new FormId(0xA10),
                document,
                proposalPath,
                outputPath);
            var transaction = new SkyrimLeveledListProductionTransactionService(
                new BethesdaSkyrimLeveledListProductionBinaryWriter(policy, labRoot),
                new BethesdaSkyrimLeveledListProductionOutputReader(),
                policy,
                labRoot);

            SkyrimLeveledListProductionProposal proposal =
                await transaction.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable && File.Exists(proposalPath.Value) &&
                   !File.Exists(outputPath.Value) &&
                   proposal.Artifact is
                   {
                       NewSelfOwnedRecord: true,
                       NoUnrelatedRecords: true,
                       Entries.Length: 2
                   } &&
                   proposal.Artifact.MasterDependencies
                       .SequenceEqual(["OutfitBase.esm"]),
                "New LVLI review was not proposal-only or lost its exact master surface: " +
                FormatOutfitDiagnostics(proposal.Diagnostics));

            byte[] proposalBytes = await File.ReadAllBytesAsync(proposalPath.Value);
            await File.AppendAllTextAsync(proposalPath.Value, " ");
            SkyrimLeveledListProductionResult stale = await transaction.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(!stale.Applied && !File.Exists(outputPath.Value) &&
                   stale.Diagnostics.Any(item =>
                       item.Code == "leveled-list-production-proposal-hash"),
                "The new LVLI transaction accepted a changed proposal.");
            await File.WriteAllBytesAsync(proposalPath.Value, proposalBytes);

            SkyrimLeveledListProductionResult rebound = await transaction.ApplyAsync(
                request with { TargetFormId = new FormId(0xA11) },
                proposal,
                CancellationToken.None);
            Assert(!rebound.Applied && !File.Exists(outputPath.Value) &&
                   rebound.Diagnostics.Any(item =>
                       item.Code == "leveled-list-production-request-binding"),
                "The new LVLI transaction accepted an apply request not bound to Review.");

            SkyrimLeveledListProductionResult applied = await transaction.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                LeveledListRecordCount: 1,
                OtherRecordCount: 0,
                SelfOwned: true,
                HeaderMatches: true,
                EntriesMatch: true,
                MasterSetMatches: true
            },
                "The new LVLI production transaction failed: " +
                FormatOutfitDiagnostics(applied.Diagnostics));
            SkyrimLeveledListProductionVerification verified =
                await transaction.VerifyAsync(
                    request,
                    proposal,
                    CancellationToken.None);
            Assert(verified.IsValid &&
                   verified.OwnerPlugin == new PluginName("CreatedList.esp") &&
                   verified.TargetFormId == new FormId(0xA10),
                "The explicit second LVLI readback lost self-owned identity.");

            ModKey outputKey = ModKey.FromNameAndExtension("CreatedList.esp");
            using var reopened = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(outputPath.Value)),
                SkyrimRelease.SkyrimSE);
            ILeveledItemGetter written = reopened.LeveledItems.Single();
            ILeveledItemEntryDataGetter[] entries = written.Entries?
                .Select(item => item.Data ?? throw new InvalidDataException(
                    "An independently reopened LVLO row has no data."))
                .ToArray() ??
                throw new InvalidDataException("The independently reopened LVLO rows are missing.");
            Assert(written.FormKey == new FormKey(outputKey, 0xA10) &&
                   written.EditorID == "npcm_LVLI_ProductionCreated" &&
                   Math.Round(written.ChanceNone.Value * 100D) == 25D &&
                   entries.Length == 2 &&
                   entries[0].Level == 1 && entries[0].Count == 1 &&
                   entries[1].Level == short.MaxValue &&
                   entries[1].Count == short.MaxValue &&
                   entries.All(data => data.Reference.FormKey == new FormKey(
                       ModKey.FromNameAndExtension("OutfitBase.esm"), 0x800)),
                "Independent Mutagen readback lost the repeated ordered LVLO boundaries.");

            SkyrimLeveledListProductionProposal duplicate =
                await transaction.AnalyzeAsync(
                    request with
                    {
                        State = state with
                        {
                            ExistingEditorIds =
                                state.ExistingEditorIds.Add(document.EditorId)
                        },
                        OutputProposal = new WorkspacePath(Path.Combine(
                            root,
                            "duplicate.leveled-list-production-proposal.json")),
                        OutputPlugin = new WorkspacePath(Path.Combine(
                            root,
                            "Duplicate.esp"))
                    },
                    CancellationToken.None);
            Assert(!duplicate.IsApplicable &&
                   duplicate.Diagnostics.Any(item =>
                       item.Code == "leveled-list-production-editor-id-duplicate"),
                "The production transaction accepted an existing LVLI EditorID.");

            await File.AppendAllTextAsync(providerPath, "stale");
            SkyrimLeveledListProductionLoadResult staleLoad =
                await loader.LoadAsync(
                    new SkyrimLeveledListProductionLoadRequest(intake, 11),
                    CancellationToken.None);
            Assert(!staleLoad.Accepted && staleLoad.State is null &&
                   staleLoad.Diagnostics.Any(item =>
                       item.Code == "leveled-list-production-plugin-stale"),
                "The new-LVLI loader accepted reviewed hash drift.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
