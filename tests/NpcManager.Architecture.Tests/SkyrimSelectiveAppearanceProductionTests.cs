using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimSelectiveAppearancePluginPatch()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "selective-paste-plugin-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixtureSource = Path.Combine(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
                "tests", "fixtures", "skyrim-production",
                "FaceEditFixtureSSE.esp");
            string sourcePath = Path.Combine(root, "FaceEditFixtureSSE.esp");
            string outfitProviderPath = Path.Combine(root, "SelectiveOutfits.esp");
            File.Copy(fixtureSource, sourcePath, overwrite: false);
            CreateSelectiveOutfitProvider(outfitProviderPath);

            var sourcePlugin = new WorkspacePath(sourcePath);
            var sourceHash = HashFile(sourcePath);
            var source = new BethesdaSkyrimFaceEditSourceReader().Read(
                sourcePlugin,
                new FormId(0x800));
            var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
                source.OrderedHeadParts
                    .Select((item, index) => (SkyrimNpcHeadPartSource)
                        new ExternalSkyrimNpcHeadPart(
                            item,
                            (NpcHeadPartType)(index + 1)))
                    .ToImmutableArray(),
                new ExternalSkyrimNpcHairColor(source.HairColor),
                new ExternalSkyrimNpcFaceTextureSet(
                    source.HeadTexture ?? throw new InvalidDataException(
                        "The complete face fixture lost FTST.")),
                source.Weight ?? throw new InvalidDataException(
                    "The complete face fixture lost NAM7."),
                new SkyrimFaceMorphPatch(
                    source.FaceMorphs.Nam9Sliders,
                    source.FaceMorphs.Nam9Trailing,
                    source.FaceMorphs.NamaValues),
                source.FaceTints,
                source.Qnam ?? throw new InvalidDataException(
                    "The complete face fixture lost QNAM."));
            var outfitPatch = new NpcOutfitPatch(
                OptionalFormReference.Set(new FormReference(
                    new PluginName("SelectiveOutfits.esp"),
                    new FormId(0x900))),
                OptionalFormReference.Set(new FormReference(
                    new PluginName("SelectiveOutfits.esp"),
                    new FormId(0x901))));
            var request = new NpcAppearanceOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                sourcePlugin,
                sourceHash,
                new FormId(0x800),
                new WorkspacePath(Path.Combine(root, "selective-paste.proposal.json")),
                new WorkspacePath(Path.Combine(root, "SelectivePasteOutput.esp")),
                source.Race,
                source.Sex,
                appearance,
                null,
                OutfitPatch: outfitPatch,
                IsCharGenFacePreset: true);
            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var service = new NpcAppearanceOverrideService(
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot);

            NpcAppearanceOverrideProposal proposal = await service.AnalyzeAsync(
                request,
                CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.RequiredMasters.Contains(
                       new PluginName("SelectiveOutfits.esp")) &&
                   proposal.ChangedNpcSubrecords.Contains("DOFT") &&
                   proposal.ChangedNpcSubrecords.Contains("SOFT") &&
                   proposal.ChangedNpcSubrecords.Contains("ACBS"),
                "The selective-paste proposal did not bind its plugin-only carrier surface.");

            NpcAppearanceOverrideResult applied = await service.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(applied.Applied &&
                   applied.Verification is
                   {
                       IsValid: true,
                       RecordPatchMatches: true,
                       SourceOwnedTargetCount: 1,
                       SelfOwnedTargetCount: 0
                   },
                "The plugin-only selective-paste transaction was not independently verified: " +
                string.Join(" | ", applied.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

            ModKey outputKey = ModKey.FromNameAndExtension("SelectivePasteOutput.esp");
            ModKey sourceKey = ModKey.FromNameAndExtension("FaceEditFixtureSSE.esp");
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(request.OutputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            INpcGetter npc = output.Npcs.Single(item =>
                item.FormKey == new FormKey(sourceKey, 0x800));
            Assert(npc.DefaultOutfit.FormKeyNullable == new FormKey(
                       ModKey.FromNameAndExtension("SelectiveOutfits.esp"), 0x900) &&
                   npc.SleepingOutfit.FormKeyNullable == new FormKey(
                       ModKey.FromNameAndExtension("SelectiveOutfits.esp"), 0x901) &&
                   npc.Configuration.Flags.HasFlag(
                       NpcConfiguration.Flag.IsCharGenFacePreset),
                "Typed readback lost DOFT, SOFT, or the ACBS CharGen bit.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CreateSelectiveOutfitProvider(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Outfits.Add(new Outfit(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SelectiveSourceDefaultOutfit"
        });
        mod.Outfits.Add(new Outfit(new FormKey(key, 0x901), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SelectiveSourceSleepingOutfit"
        });
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static async Task TestSkyrimSelectiveAppearanceDualCarrierTransaction()
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        string fixtureRoot = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "tests", "fixtures",
            "skyrim-production");
        string presetRoot = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "tools", "fixtures",
            "sky-gui-014");
        var plugin = new PluginName("SelectivePasteFixtureSSE.esp");
        var pluginPath = new WorkspacePath(Path.Combine(fixtureRoot, plugin.Value));
        var sourcePreset = new WorkspacePath(Path.Combine(presetRoot, "source.jslot"));
        var targetPreset = new WorkspacePath(Path.Combine(presetRoot, "target.jslot"));
        Sha256Hash pluginHash = HashFile(pluginPath.Value);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(fixtureRoot),
            new WorkspacePath(Path.Combine(fixtureRoot, "load-order.json")),
            new WorkspacePath(Path.Combine(fixtureRoot, "future-output")),
            new Sha256Hash(new string('1', 64)),
            [
                new PluginClosureReviewEntry(
                    plugin, 0, true, true, true, false, true,
                    pluginPath, pluginHash, [])
            ],
            [], [], [], 0,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath("F:\\ExampleGame"));
        var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(
            policy,
            labRoot);
        var faceLoader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(
                new BethesdaSkyrimHeadPartChoiceService(authorityLoader)),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        var presets = new PresetService(policy, labRoot);
        var aggregate = new SkyrimSelectiveAppearancePasteLoadService(
            faceLoader,
            presets,
            new BethesdaSkyrimSelectiveAppearanceNpcStateReader());

        SkyrimSelectiveAppearancePasteLoadResult loaded = await aggregate.LoadAsync(
            new SkyrimSelectiveAppearancePasteLoadRequest(
                intake,
                plugin,
                new FormId(0x900),
                sourcePreset,
                plugin,
                new FormId(0x800),
                targetPreset),
            CancellationToken.None);
        SkyrimSelectiveAppearancePasteLoadedState state = loaded.State ??
            throw new InvalidDataException(
                "The aggregate loader omitted its accepted state: " +
                string.Join(" | ", loaded.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));
        Assert(loaded.Accepted &&
               state.Source.Document.Body.Weight == 72F &&
               state.Target.Document.Body.Weight == 31F &&
               state.Source.Document.Face.CustomMorphs.Single().Name ==
                   "SourceSmile" &&
               state.Target.Document.Face.CustomMorphs.Single().Name ==
                   "TargetFrown" &&
               state.Source.Document.Outfits.DefaultOutfit?.FormId ==
                   new FormId(0x80A) &&
               state.Target.Document.Outfits.DefaultOutfit?.FormId ==
                   new FormId(0x808),
            "The aggregate loader did not reconstruct both exact plugin/preset endpoints: " +
            string.Join(" | ", loaded.Diagnostics.Select(item =>
                $"{item.Code}:{item.Message}")));

        string root = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work",
            "selective-paste-appearance-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var selection = new SkyrimSelectiveAppearancePasteSelection(
            [
                SkyrimAppearancePasteCategory.BodyWeight,
                SkyrimAppearancePasteCategory.BodyShape,
                SkyrimAppearancePasteCategory.Outfits,
                SkyrimAppearancePasteCategory.FaceParts,
                SkyrimAppearancePasteCategory.Sculpt,
                SkyrimAppearancePasteCategory.CharGenFlag
            ]);
            var request = new SkyrimSelectiveAppearancePasteTransactionRequest(
                state,
                selection,
                new WorkspacePath(Path.Combine(root, "selective-paste.proposal.json")),
                new WorkspacePath(Path.Combine(root, "plugin.proposal.json")),
                new WorkspacePath(Path.Combine(root, "SelectivePasteResult.esp")),
                new WorkspacePath(Path.Combine(root, "SelectivePasteResult.jslot")));
            var service = new SkyrimSelectiveAppearancePasteTransactionService(
                new NpcAppearanceOverrideService(policy, labRoot),
                presets,
                presets,
                policy,
                labRoot);

            SkyrimSelectiveAppearancePasteProposal proposal =
                await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   File.Exists(request.TransactionProposalPath.Value) &&
                   File.Exists(request.PluginProposalPath.Value) &&
                   !File.Exists(request.OutputPlugin.Value) &&
                   !File.Exists(request.OutputPreset.Value),
                "Selective-paste review did not remain proposal-only: " +
                string.Join(" | ", proposal.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

            SkyrimSelectiveAppearancePasteTransactionResult applied =
                await service.ApplyAsync(request, proposal, CancellationToken.None);
            Assert(applied.Applied &&
                   applied.Verification is
                   {
                       IsValid: true,
                       SelectedPresetSectionsMatchSource: true,
                       UncheckedPresetSectionsPreserveTarget: true,
                       PluginVerification:
                       {
                           IsValid: true,
                           RecordPatchMatches: true,
                           SourceOwnedTargetCount: 1,
                           SelfOwnedTargetCount: 0
                       }
                   } &&
                   File.Exists(request.OutputPlugin.Value) &&
                   File.Exists(request.OutputPreset.Value),
                "The dual-carrier transaction did not apply and independently reopen both outputs: " +
                string.Join(" | ", applied.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

            SkyrimSelectiveAppearancePasteVerificationResult verified =
                await service.VerifyAsync(request, proposal, CancellationToken.None);
            Assert(verified.IsValid && verified.OutputPresetSha256 is not null,
                "The explicit second verification pass refused the retained dual output.");

            ModKey outputKey = ModKey.FromNameAndExtension("SelectivePasteResult.esp");
            ModKey ownerKey = ModKey.FromNameAndExtension(plugin.Value);
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(request.OutputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            INpcGetter npc = output.Npcs.Single(item =>
                item.FormKey == new FormKey(ownerKey, 0x800));
            Assert(npc.Weight == 72F &&
                   npc.DefaultOutfit.FormKeyNullable ==
                       new FormKey(ownerKey, 0x80A) &&
                   npc.SleepingOutfit.FormKeyNullable ==
                       new FormKey(ownerKey, 0x80B) &&
                   npc.Configuration.Flags.HasFlag(
                       NpcConfiguration.Flag.IsCharGenFacePreset) &&
                   npc.HairColor.FormKeyNullable == new FormKey(ownerKey, 0x802),
                "Typed output did not copy selected source values while preserving unchecked target HCLF.");

            var rollbackRequest = request with
            {
                TransactionProposalPath = new WorkspacePath(Path.Combine(
                    root, "rollback-selective-paste.proposal.json")),
                PluginProposalPath = new WorkspacePath(Path.Combine(
                    root, "rollback-plugin.proposal.json")),
                OutputPlugin = new WorkspacePath(Path.Combine(
                    root, "RollbackResult.esp")),
                OutputPreset = new WorkspacePath(Path.Combine(
                    root, "RollbackResult.jslot"))
            };
            SkyrimSelectiveAppearancePasteProposal rollbackProposal =
                await service.AnalyzeAsync(rollbackRequest, CancellationToken.None);
            var failingService = new SkyrimSelectiveAppearancePasteTransactionService(
                new NpcAppearanceOverrideService(policy, labRoot),
                presets,
                new ThrowingPresetCopyService(),
                policy,
                labRoot);
            SkyrimSelectiveAppearancePasteTransactionResult rolledBack =
                await failingService.ApplyAsync(
                    rollbackRequest,
                    rollbackProposal,
                    CancellationToken.None);
            Assert(!rolledBack.Applied &&
                   !File.Exists(rollbackRequest.OutputPlugin.Value) &&
                   !File.Exists(rollbackRequest.OutputPreset.Value) &&
                   rolledBack.Diagnostics.Any(item =>
                    item.Code == "selective-paste-apply-failed"),
                "A preset-writer failure retained a partial plugin output instead of rolling back both carriers.");

            var lockedRollbackRequest = request with
            {
                TransactionProposalPath = new WorkspacePath(Path.Combine(
                    root, "locked-rollback-selective-paste.proposal.json")),
                PluginProposalPath = new WorkspacePath(Path.Combine(
                    root, "locked-rollback-plugin.proposal.json")),
                OutputPlugin = new WorkspacePath(Path.Combine(
                    root, "LockedRollbackResult.esp")),
                OutputPreset = new WorkspacePath(Path.Combine(
                    root, "LockedRollbackResult.jslot"))
            };
            SkyrimSelectiveAppearancePasteProposal lockedRollbackProposal =
                await service.AnalyzeAsync(
                    lockedRollbackRequest,
                    CancellationToken.None);
            using (var lockedPresetFailure =
                   new LockingThrowingPresetCopyService(
                       lockedRollbackRequest.OutputPlugin.Value))
            {
                var lockedRollbackService =
                    new SkyrimSelectiveAppearancePasteTransactionService(
                        new NpcAppearanceOverrideService(policy, labRoot),
                        presets,
                        lockedPresetFailure,
                        policy,
                        labRoot);
                SkyrimSelectiveAppearancePasteTransactionResult lockedRollback =
                    await lockedRollbackService.ApplyAsync(
                        lockedRollbackRequest,
                        lockedRollbackProposal,
                        CancellationToken.None);
                Assert(!lockedRollback.Applied &&
                       File.Exists(lockedRollbackRequest.OutputPlugin.Value) &&
                       !File.Exists(lockedRollbackRequest.OutputPreset.Value) &&
                       lockedRollback.Diagnostics.Any(item =>
                           item.Code == "selective-paste-cleanup-failed" &&
                           item.Severity == DiagnosticSeverity.Error &&
                           item.Message.Contains(
                               lockedRollbackRequest.OutputPlugin.Value,
                               StringComparison.Ordinal)),
                    "A locked selective-paste plugin rollback did not report the exact surviving path.");
            }
            File.Delete(lockedRollbackRequest.OutputPlugin.Value);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class ThrowingPresetCopyService : IPresetCopyService
    {
        public ValueTask<PresetCopyResult> CopyAsync(
            PresetCopyRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<PresetCopyResult>(
                new InvalidDataException("Injected preset-writer failure after plugin write."));
    }

    private sealed class LockingThrowingPresetCopyService(string outputPlugin) :
        IPresetCopyService,
        IDisposable
    {
        private FileStream? _lock;

        public ValueTask<PresetCopyResult> CopyAsync(
            PresetCopyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lock = new FileStream(
                outputPlugin,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return ValueTask.FromException<PresetCopyResult>(
                new InvalidDataException(
                    "Injected preset-writer failure while the plugin is locked."));
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
