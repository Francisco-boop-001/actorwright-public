using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions OutfitFixtureJsonOptions = new()
    {
        WriteIndented = true
    };

    private static int EmitOutfitProductionFixture(string destination)
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        var projectWork = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work"));
        WorkspacePath root;
        try { root = new WorkspacePath(destination); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        if (!root.IsUnder(projectWork) || Directory.Exists(root.Value) ||
            File.Exists(root.Value))
        {
            Console.Error.WriteLine(
                "The fixture destination must be a fresh path under the project 03-builds/work root.");
            return 1;
        }
        try
        {
            string data = Path.Combine(root.Value, "Data");
            Directory.CreateDirectory(data);
            string basePath = Path.Combine(data, "OutfitBase.esm");
            string providerPath = Path.Combine(data, "OutfitProvider.esp");
            CreateOutfitProductionBase(basePath);
            CreateOutfitProductionOverride(providerPath);
            var loadOrder = new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                plugins = new object[]
                {
                    new { name = "OutfitBase.esm", order = 0, enabled = true },
                    new { name = "OutfitProvider.esp", order = 1, enabled = true }
                }
            };
            File.WriteAllText(
                Path.Combine(root.Value, "load-order.json"),
                JsonSerializer.Serialize(loadOrder, OutfitFixtureJsonOptions));
            var expected = new
            {
                templatePlugin = "OutfitProvider.esp",
                templateFormId = "0x00000900",
                newTargetFormId = "0x00000A00",
                armors = new[]
                {
                    new { reference = "OutfitBase.esm|0x00000800", slotMask = "0x00000004" },
                    new { reference = "OutfitBase.esm|0x00000801", slotMask = "0x00000008" }
                },
                leveledList = "OutfitBase.esm|0x00000810",
                outfitOwner = "OutfitBase.esm",
                outfitWinner = "OutfitProvider.esp",
                baseSha256 = HashOutfitFile(basePath).Value,
                providerSha256 = HashOutfitFile(providerPath).Value
            };
            File.WriteAllText(
                Path.Combine(root.Value, "expected.json"),
                JsonSerializer.Serialize(expected, OutfitFixtureJsonOptions));
            Console.WriteLine(root.Value);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int EmitLeveledListProductionFixture(string destination)
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        var projectWork = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work"));
        WorkspacePath root;
        try { root = new WorkspacePath(destination); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        if (!root.IsUnder(projectWork) || Directory.Exists(root.Value) ||
            File.Exists(root.Value))
        {
            Console.Error.WriteLine(
                "The fixture destination must be a fresh path under the project 03-builds/work root.");
            return 1;
        }
        try
        {
            string phase1 = Path.Combine(root.Value, "phase1");
            string phase1Data = Path.Combine(phase1, "Data");
            string phase2 = Path.Combine(root.Value, "phase2");
            string phase2Data = Path.Combine(phase2, "Data");
            Directory.CreateDirectory(phase1Data);
            Directory.CreateDirectory(phase2Data);
            string phase1Base = Path.Combine(phase1Data, "OutfitBase.esm");
            string phase1Provider = Path.Combine(phase1Data, "OutfitProvider.esp");
            CreateOutfitProductionBase(phase1Base);
            CreateOutfitProductionOverride(phase1Provider);
            File.Copy(phase1Base, Path.Combine(phase2Data, "OutfitBase.esm"));
            string providerAfter = Path.Combine(phase2Data, "OutfitProviderAfter.esp");
            CreateOutfitProductionAfterGeneratedList(providerAfter);

            WriteFixtureLoadOrder(
                Path.Combine(phase1, "load-order.json"),
                ["OutfitBase.esm", "OutfitProvider.esp"]);
            WriteFixtureLoadOrder(
                Path.Combine(phase2, "load-order.json"),
                ["OutfitBase.esm", "CreatedList.esp", "OutfitProviderAfter.esp"]);
            var expected = new
            {
                targetFormId = "0x00000A10",
                editorId = "npcm_LVLI_TravelGear",
                chanceNone = 25,
                maxCount = 0,
                flags = "0x05",
                entry = new
                {
                    reference = "OutfitBase.esm|0x00000800",
                    level = 7,
                    count = 2,
                    chanceNone = 0
                },
                masters = new[] { "OutfitBase.esm" },
                phase2TemplatePlugin = "OutfitProviderAfter.esp",
                phase2TemplateFormId = "0x00000900",
                phase2OutfitItems = new[]
                {
                    "OutfitBase.esm|0x00000800",
                    "CreatedList.esp|0x00000A10"
                },
                phase1BaseSha256 = HashOutfitFile(phase1Base).Value,
                phase1ProviderSha256 = HashOutfitFile(phase1Provider).Value,
                phase2ProviderSha256 = HashOutfitFile(providerAfter).Value
            };
            File.WriteAllText(
                Path.Combine(root.Value, "expected.json"),
                JsonSerializer.Serialize(expected, OutfitFixtureJsonOptions));
            Console.WriteLine(root.Value);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void WriteFixtureLoadOrder(
        string path,
        IReadOnlyList<string> plugins)
    {
        var loadOrder = new
        {
            schemaVersion = 1,
            edition = "skyrimse",
            plugins = plugins.Select((name, order) => new
            {
                name,
                order,
                enabled = true
            }).ToArray()
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(loadOrder, OutfitFixtureJsonOptions));
    }

    private static async Task TestSkyrimOutfitProductionWorkflow()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "outfit-production-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string basePath = Path.Combine(dataRoot, "OutfitBase.esm");
            string providerPath = Path.Combine(dataRoot, "OutfitProvider.esp");
            CreateOutfitProductionBase(basePath);
            CreateOutfitProductionOverride(providerPath);

            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var pluginReader = new BethesdaPluginReader();
            var outfitChoices = new OutfitChoiceService(pluginReader, policy, labRoot);
            var itemReader = new BethesdaSkyrimOutfitItemCatalogReader();
            var loader = new SkyrimOutfitProductionLoadService(
                outfitChoices,
                itemReader);
            ReviewedGameIntake intake = CreateOutfitReviewedIntake(
                root,
                dataRoot,
                basePath,
                providerPath);

            SkyrimOutfitProductionLoadResult loaded = await loader.LoadAsync(
                new SkyrimOutfitProductionLoadRequest(
                    intake,
                    new PluginName("OutfitProvider.esp"),
                    new FormId(0x900),
                    new FormId(0xA00),
                    11),
                CancellationToken.None);
            SkyrimOutfitProductionState state = loaded.State ??
                throw new InvalidDataException(
                    "The production outfit loader omitted its accepted state: " +
                    FormatOutfitDiagnostics(loaded.Diagnostics));
            OutfitChoiceCandidate existing = state.Outfits.Single(item =>
                item.FormId == new FormId(0x900));
            SkyrimOutfitEditorItem armor = state.Items.Single(item =>
                item.Reference.FormId == new FormId(0x800));
            SkyrimOutfitEditorItem leveled = state.Items.Single(item =>
                item.Reference.FormId == new FormId(0x810));
            Assert(loaded.Accepted &&
                   existing.Plugin == new PluginName("OutfitProvider.esp") &&
                   existing.Provenance.SourcePlugin == new PluginName("OutfitBase.esm") &&
                   armor.EffectiveSlotMask == 0x04 &&
                   leveled.Kind == SkyrimOutfitEditorItemKind.LeveledList &&
                   leveled.RealizationSeed == 11 &&
                   !leveled.PreviewRealization.IsDefaultOrEmpty,
                "Reviewed outfit authority lost winner/owner identity, ARMO slots, or LVLI realization: " +
                FormatOutfitDiagnostics(loaded.Diagnostics));

            ImmutableArray<SkyrimOutfitPreviewArmor> repeated = itemReader.Resolve(
                new SkyrimOutfitPreviewResolveRequest(
                    intake.DataRoot,
                    state.PluginOrder,
                    leveled.Reference,
                    11));
            Assert(repeated.SequenceEqual(leveled.PreviewRealization),
                "The same LVLI seed did not reproduce the loaded terminal realization.");

            var proposalService = new OutfitProposalService(
                pluginReader,
                policy,
                labRoot);
            var writer = new BethesdaOutfitBinaryWriteService(policy, labRoot);
            var transaction = new SkyrimOutfitProductionTransactionService(
                proposalService,
                writer,
                pluginReader,
                policy,
                labRoot);

            SkyrimOutfitEditorDraft newDraft = SkyrimOutfitEditorRules.BeginNew(
                state.TemplatePluginPath,
                state.TemplateFormId,
                new EditorId("NpcManager_ProductionOutfit"),
                state.NewTargetFormId).Draft ??
                throw new InvalidDataException("Could not begin a new outfit draft.");
            newDraft = RequireOutfitDraft(SkyrimOutfitEditorRules.Add(newDraft, armor));
            newDraft = RequireOutfitDraft(SkyrimOutfitEditorRules.Add(newDraft, leveled));
            WorkspacePath newProposalPath = new(Path.Combine(
                root,
                "new.outfit-proposal.json"));
            SkyrimOutfitEditorCommitResult newCommit = SkyrimOutfitEditorRules.Save(
                GameEdition.SkyrimSpecialEdition,
                newDraft,
                newProposalPath);
            var newRequest = new SkyrimOutfitProductionTransactionRequest(
                newCommit.Proposal ?? throw new InvalidDataException(
                    "New outfit Save omitted its typed proposal."),
                new WorkspacePath(Path.Combine(root, "NewOutfitOutput.esp")));

            SkyrimOutfitProductionProposal newProposal = await transaction.AnalyzeAsync(
                newRequest,
                CancellationToken.None);
            Assert(newProposal.IsApplicable && File.Exists(newProposalPath.Value) &&
                   !File.Exists(newRequest.OutputPlugin.Value),
                "New OTFT review did not remain proposal-only: " +
                FormatOutfitDiagnostics(newProposal.Diagnostics));
            byte[] reviewedProposalBytes = await File.ReadAllBytesAsync(
                newProposalPath.Value);
            await File.AppendAllTextAsync(newProposalPath.Value, " ");
            SkyrimOutfitProductionResult staleProposalApply =
                await transaction.ApplyAsync(
                    newRequest,
                    newProposal,
                    CancellationToken.None);
            Assert(!staleProposalApply.Applied &&
                   !File.Exists(newRequest.OutputPlugin.Value) &&
                   staleProposalApply.Diagnostics.Any(item =>
                       item.Code == "outfit-production-proposal-hash"),
                "The production transaction accepted a proposal changed after review.");
            await File.WriteAllBytesAsync(newProposalPath.Value,
                reviewedProposalBytes);
            SkyrimOutfitProductionResult newApplied = await transaction.ApplyAsync(
                newRequest,
                newProposal,
                CancellationToken.None);
            Assert(newApplied.Applied &&
                   newApplied.Verification is
                   {
                       IsValid: true,
                       RecordCount: 1,
                       OrderedItemsMatch: true,
                       MasterSetMatches: true,
                       OwnerMatches: true
                   },
                "New OTFT production transaction failed: " +
                FormatOutfitDiagnostics(newApplied.Diagnostics));
            SkyrimOutfitProductionVerification newVerified = await transaction.VerifyAsync(
                newRequest,
                newProposal,
                CancellationToken.None);
            Assert(newVerified.IsValid &&
                   newVerified.OwnerPlugin == new PluginName("NewOutfitOutput.esp") &&
                   newVerified.TargetFormId == new FormId(0xA00),
                "Explicit new OTFT second readback lost self-owned identity.");

            SkyrimOutfitEditorDraft overrideDraft = SkyrimOutfitEditorRules.BeginOverride(
                existing,
                state.TemplatePluginPath,
                state.Items).Draft ??
                throw new InvalidDataException("Could not begin an outfit override draft.");
            overrideDraft = RequireOutfitDraft(
                SkyrimOutfitEditorRules.Move(overrideDraft, 1, -1));
            WorkspacePath overrideProposalPath = new(Path.Combine(
                root,
                "override.outfit-proposal.json"));
            SkyrimOutfitEditorCommitResult overrideCommit = SkyrimOutfitEditorRules.Save(
                GameEdition.SkyrimSpecialEdition,
                overrideDraft,
                overrideProposalPath);
            var overrideRequest = new SkyrimOutfitProductionTransactionRequest(
                overrideCommit.Proposal ?? throw new InvalidDataException(
                    "Override Save omitted its typed proposal."),
                new WorkspacePath(Path.Combine(root, "OverrideOutfitOutput.esp")));
            SkyrimOutfitProductionProposal overrideProposal = await transaction.AnalyzeAsync(
                overrideRequest,
                CancellationToken.None);
            var reboundRequest = overrideRequest with
            {
                OutputPlugin = new WorkspacePath(Path.Combine(
                    root,
                    "ReboundOverrideOutput.esp"))
            };
            SkyrimOutfitProductionResult reboundApply = await transaction.ApplyAsync(
                reboundRequest,
                overrideProposal,
                CancellationToken.None);
            Assert(!reboundApply.Applied &&
                   !File.Exists(reboundRequest.OutputPlugin.Value) &&
                   reboundApply.Diagnostics.Any(item =>
                       item.Code == "outfit-production-request-binding"),
                "The production transaction accepted an apply request not bound to its review.");
            SkyrimOutfitProductionResult overrideApplied = await transaction.ApplyAsync(
                overrideRequest,
                overrideProposal,
                CancellationToken.None);
            Assert(overrideApplied.Applied &&
                   overrideApplied.Verification is
                   {
                       IsValid: true,
                       RecordCount: 1,
                       OrderedItemsMatch: true,
                       OwnerMatches: true,
                       OwnerPlugin: { } overrideOwner
                   } &&
                   overrideOwner == new PluginName("OutfitBase.esm"),
                "Override OTFT production transaction lost source ownership or order: " +
                FormatOutfitDiagnostics(overrideApplied.Diagnostics));

            string cyclePath = Path.Combine(dataRoot, "OutfitCycle.esp");
            CreateOutfitProductionCycle(cyclePath);
            SkyrimOutfitItemCatalogResult cycleCatalog = itemReader.Read(
                new SkyrimOutfitItemCatalogRequest(
                    intake.DataRoot,
                    [new PluginName("OutfitCycle.esp")],
                    11));
            Assert(!cycleCatalog.Accepted &&
                   cycleCatalog.Diagnostics.Any(item =>
                       item.Code == "outfit-catalog-lvli-cycle"),
                "The production LVLI resolver did not fail closed on a cycle.");

            await File.AppendAllTextAsync(providerPath, "stale");
            SkyrimOutfitProductionLoadResult staleLoad = await loader.LoadAsync(
                new SkyrimOutfitProductionLoadRequest(
                    intake,
                    new PluginName("OutfitProvider.esp"),
                    new FormId(0x900),
                    new FormId(0xA01),
                    11),
                CancellationToken.None);
            Assert(!staleLoad.Accepted && staleLoad.State is null &&
                   staleLoad.Diagnostics.Any(item =>
                       item.Code == "outfit-production-plugin-stale"),
                "The production loader accepted a copied plugin changed after review.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SkyrimOutfitEditorDraft RequireOutfitDraft(
        SkyrimOutfitDraftEditResult result) =>
        result.Accepted && result.Draft is not null
            ? result.Draft
            : throw new InvalidDataException(
                "The outfit draft transition was refused: " +
                FormatOutfitDiagnostics(result.Diagnostics));

    private static ReviewedGameIntake CreateOutfitReviewedIntake(
        string root,
        string dataRoot,
        string basePath,
        string providerPath)
    {
        PluginClosureReviewEntry Entry(
            string name,
            int order,
            string path,
            ImmutableArray<PluginName> masters) => new(
            new PluginName(name),
            order,
            true,
            true,
            true,
            false,
            true,
            new WorkspacePath(path),
            HashOutfitFile(path),
            masters);

        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            new WorkspacePath(dataRoot),
            new WorkspacePath(Path.Combine(root, "load-order.json")),
            new WorkspacePath(Path.Combine(root, "future-output")),
            new Sha256Hash(new string('1', 64)),
            [
                Entry("OutfitBase.esm", 0, basePath, []),
                Entry("OutfitProvider.esp", 1, providerPath,
                    [new PluginName("OutfitBase.esm")])
            ],
            [],
            [],
            [],
            0,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
    }

    private static void CreateOutfitProductionBase(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var armorA = new Armor(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionArmorA",
            Name = "Production armor A",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x04
            }
        };
        var armorB = new Armor(new FormKey(key, 0x801), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionArmorB",
            Name = "Production armor B",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x08
            }
        };
        mod.Armors.Add(armorA);
        mod.Armors.Add(armorB);
        var leveled = new LeveledItem(
            new FormKey(key, 0x810),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionLeveledArmor",
            Entries =
            [
                new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 1,
                        Count = 1,
                        Reference = new FormLink<IItemGetter>(armorA.FormKey)
                    }
                },
                new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 1,
                        Count = 1,
                        Reference = new FormLink<IItemGetter>(armorB.FormKey)
                    }
                }
            ]
        };
        mod.LeveledItems.Add(leveled);
        mod.Outfits.Add(new Outfit(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionBaseOutfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(armorA.FormKey),
                new FormLink<IOutfitTargetGetter>(leveled.FormKey)
            ]
        });
        WriteOutfitFixture(mod, path);
    }

    private static void CreateOutfitProductionOverride(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        ModKey owner = ModKey.FromNameAndExtension("OutfitBase.esm");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = owner });
        mod.Outfits.Add(new Outfit(new FormKey(owner, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionBaseOutfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(new FormKey(owner, 0x800)),
                new FormLink<IOutfitTargetGetter>(new FormKey(owner, 0x810))
            ]
        });
        WriteOutfitFixture(mod, path);
    }

    private static void CreateOutfitProductionAfterGeneratedList(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        ModKey owner = ModKey.FromNameAndExtension("OutfitBase.esm");
        ModKey createdList = ModKey.FromNameAndExtension("CreatedList.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = owner });
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = createdList });
        mod.Outfits.Add(new Outfit(new FormKey(owner, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionBaseOutfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(new FormKey(owner, 0x800)),
                new FormLink<IOutfitTargetGetter>(new FormKey(createdList, 0xA10))
            ]
        });
        WriteOutfitFixture(mod, path);
    }

    private static void CreateOutfitProductionCycle(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        FormKey first = new(key, 0x820);
        FormKey second = new(key, 0x821);
        mod.LeveledItems.Add(new LeveledItem(first, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionCycleA",
            Entries =
            [
                new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 1,
                        Count = 1,
                        Reference = new FormLink<IItemGetter>(second)
                    }
                }
            ]
        });
        mod.LeveledItems.Add(new LeveledItem(second, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionCycleB",
            Entries =
            [
                new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 1,
                        Count = 1,
                        Reference = new FormLink<IItemGetter>(first)
                    }
                }
            ]
        });
        WriteOutfitFixture(mod, path);
    }

    private static void WriteOutfitFixture(SkyrimMod mod, string path) =>
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });

    private static Sha256Hash HashOutfitFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string FormatOutfitDiagnostics(
        IEnumerable<Diagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(item =>
        $"{item.Code}:{item.Message}"));
}
