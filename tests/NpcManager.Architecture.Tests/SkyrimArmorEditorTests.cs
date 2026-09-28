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
    private static async Task TestSkyrimArmorEditor()
    {
        PluginName sourcePlugin = new("Source.esp");
        FormReference race = new(sourcePlugin, new FormId(0x900));
        FormReference enchantment = new(sourcePlugin, new FormId(0x901));
        FormReference pickup = new(sourcePlugin, new FormId(0x902));
        FormReference drop = new(sourcePlugin, new FormId(0x903));
        FormReference equipmentType = new(sourcePlugin, new FormId(0x904));
        FormReference blockMaterial = new(sourcePlugin, new FormId(0x905));
        FormReference keyword = new(sourcePlugin, new FormId(0x906));
        FormReference addon = new(sourcePlugin, new FormId(0x907));
        FormReference templateArmor = new(sourcePlugin, new FormId(0xA01));
        var document = new SkyrimArmorEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorEditorIntent.NewFromTemplate,
            ArmorProposalMode.New,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\armor-editor-placeholder\\Source.esp"),
            new FormId(0xA00),
            new EditorId("npcm_ARMO_Travel"),
            new FormId(0xB00),
            "Travel armor",
            race,
            enchantment,
            true,
            "A complete immutable armor document.",
            uint.MaxValue,
            12.5,
            37.25,
            0x14,
            pickup,
            drop,
            equipmentType,
            blockMaterial,
            new ArmorObjectBounds(-10, -20, -30, 40, 50, 60),
            "armor\\travel_m.nif",
            null,
            templateArmor,
            [addon, addon],
            [keyword]);

        SkyrimArmorEditorResult accepted = SkyrimArmorEditorRules.Save(document, []);
        Assert(accepted.Accepted && accepted.Document == document,
            "The complete immutable Skyrim armor document was refused.");
        Assert(!SkyrimArmorEditorRules.Save(document with
        {
            ObjectBounds = new ArmorObjectBounds(1, 0, 0, 0, 0, 0)
        }, []).Accepted,
            "Inverted Skyrim object bounds did not fail closed.");
        Assert(!SkyrimArmorEditorRules.Cancel().Accepted,
            "Armor Cancel returned an accepted document.");

        SkyrimArmorEditorResult repeated =
            SkyrimArmorEditorCollectionRules.AddArmorAddon(document, addon);
        Assert(repeated.Accepted && repeated.Document?.ArmorAddons.Length == 3,
            "Legal repeated ordered Skyrim ARMA references were rejected.");
        Assert(!SkyrimArmorEditorCollectionRules.AddKeyword(document, keyword).Accepted,
            "A duplicate qualified KWDA reference was accepted.");
        Assert(!SkyrimArmorEditorCollectionRules.RecalculateSlots(
                document, [new SkyrimArmorAddonSlotEvidence(addon, 0x20),
                    new SkyrimArmorAddonSlotEvidence(addon, 0x40)]).Accepted,
            "Ambiguous ARMA slot evidence did not fail closed.");
        SkyrimArmorEditorResult recalculated =
            SkyrimArmorEditorCollectionRules.RecalculateSlots(
                document, [new SkyrimArmorAddonSlotEvidence(addon, 0x60)]);
        Assert(recalculated.Accepted && recalculated.Document?.SlotMask == 0x60,
            "Complete repeated-ARMA evidence did not produce the exact BOD2 union.");

        var outfitItem = new SkyrimOutfitEditorItem(
            new FormReference(new PluginName("NewArmor.esp"), new FormId(0xB00)),
            SkyrimOutfitEditorItemKind.Armor,
            "Travel armor",
            document.SlotMask,
            AuthoredArmor: document);
        var outfitDraft = new SkyrimOutfitEditorDraft(
            OutfitProposalMode.New,
            document.SourcePlugin,
            new FormId(0xB10),
            new EditorId("npcm_Outfit_Travel"),
            new FormId(0xB01),
            [outfitItem]);
        SkyrimOutfitEditorCommitResult outfitCommit = SkyrimOutfitEditorRules.Save(
            GameEdition.SkyrimSpecialEdition,
            outfitDraft,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\armor-child.outfit-proposal.json"));
        Assert(outfitCommit.Accepted &&
               outfitCommit.AuthoredArmors.SequenceEqual([document]),
            "The outer outfit transaction lost its authored armor document.");
        Assert(!SkyrimOutfitEditorRules.Cancel().Accepted,
            "Outer outfit Cancel accepted an authored armor transaction.");

        await VerifySkyrimArmorBinaryRoundTrip(document);
    }

    private static async Task VerifySkyrimArmorBinaryRoundTrip(
        SkyrimArmorEditorDocument template)
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "armor-editor-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "Source.esp");
            WriteArmorSource(sourcePath);
            SkyrimArmorEditorDocument newDocument = template with
            {
                SourcePlugin = new WorkspacePath(sourcePath)
            };
            await WriteAndVerifyArmor(newDocument,
                Path.Combine(root, "new.armor-proposal.json"),
                Path.Combine(root, "NewArmor.esp"),
                expectedOutputKey: ModKey.FromNameAndExtension("NewArmor.esp"),
                expectedFormId: 0xB00,
                expectClearedOptionalFields: false);
            await VerifyArmorCleanupFailures(
                Path.Combine(root, "new.armor-proposal.json"),
                root);

            var overrideDocument = newDocument with
            {
                Intent = SkyrimArmorEditorIntent.OverrideExisting,
                Mode = ArmorProposalMode.Override,
                EditorId = new EditorId("SourceArmor"),
                TargetFormId = null,
                Name = "Overridden armor",
                Enchantment = null,
                PickupSound = null,
                DropSound = null,
                EquipmentType = null,
                AlternateBlockMaterial = null,
                TemplateArmor = null,
                MaleWorldModel = null,
                FemaleWorldModel = null,
                ArmorAddons = [template.ArmorAddons[0]],
                Keywords = []
            };
            await WriteAndVerifyArmor(overrideDocument,
                Path.Combine(root, "override.armor-proposal.json"),
                Path.Combine(root, "OverrideArmor.esp"),
                expectedOutputKey: ModKey.FromNameAndExtension("Source.esp"),
                expectedFormId: 0xA00,
                expectClearedOptionalFields: true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteAndVerifyArmor(
        SkyrimArmorEditorDocument document,
        string proposalPath,
        string outputPath,
        ModKey expectedOutputKey,
        uint expectedFormId,
        bool expectClearedOptionalFields)
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath("F:\\ExampleGame"));
        SkyrimArmorProposalAdapterResult adapted = SkyrimArmorEditorRules.ToProposal(
            document, new WorkspacePath(proposalPath));
        Assert(adapted.Accepted && adapted.Proposal is { CompleteDocument: true },
            "The accepted Armor editor document did not map to one complete typed proposal.");
        var proposalService = new ArmorProposalService(
            new BethesdaPluginReader(), policy, labRoot);
        ArmorProposalResult proposal = await proposalService.ProposeAsync(
            adapted.Proposal!, CancellationToken.None);
        Assert(proposal.Written && proposal.Artifact is { CompleteDocument: true } &&
               proposal.Artifact.ArmorAddons.Length == document.ArmorAddons.Length,
            "The complete typed ARMO proposal was refused: " +
            string.Join("; ", proposal.Diagnostics.Select(item => item.Message)));

        var writer = new BethesdaArmorBinaryWriteService(policy, labRoot);
        ArmorBinaryWriteResult write = await writer.WriteAsync(
            new ArmorBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(proposalPath),
                new WorkspacePath(outputPath)),
            CancellationToken.None);
        Assert(write.Written,
            "The complete Skyrim ARMO writer was refused: " +
            string.Join("; ", write.Diagnostics.Select(item => item.Message)));

        using var reopened = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(ModKey.FromNameAndExtension(Path.GetFileName(outputPath)),
                new FilePath(outputPath)),
            SkyrimRelease.SkyrimSE);
        IArmorGetter armor = reopened.Armors.Single();
        Assert(armor.FormKey == new FormKey(expectedOutputKey, expectedFormId) &&
               armor.EditorID == document.EditorId.Value &&
               armor.Name?.String == document.Name &&
               armor.Description?.String == document.Description &&
               armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable) == document.NonPlayable &&
               armor.Value == document.Value &&
               Math.Abs(armor.Weight - document.Weight) < 0.0001 &&
               Math.Abs(armor.ArmorRating - document.ArmorRating) < 0.0001 &&
               armor.BodyTemplate?.FirstPersonFlags == (BipedObjectFlag)document.SlotMask &&
               armor.ObjectBounds?.First.X == document.ObjectBounds.MinimumX &&
               armor.ObjectBounds?.Second.Z == document.ObjectBounds.MaximumZ &&
               armor.Armature.Select(item => item.FormKey).SequenceEqual(
                   document.ArmorAddons.Select(ToFormKey)) &&
               (armor.Keywords?.Select(item => item.FormKey) ?? []).SequenceEqual(
                   document.Keywords.Select(ToFormKey)),
            "Independent ARMO readback lost identity, scalar data, bounds, flags, or ordered references.");
        if (expectClearedOptionalFields)
            Assert(armor.ObjectEffect.IsNull && armor.PickUpSound.IsNull &&
                   armor.PutDownSound.IsNull && armor.EquipmentType.IsNull &&
                   armor.AlternateBlockMaterial.IsNull && armor.TemplateArmor.IsNull &&
                   armor.WorldModel?.Male is null && armor.WorldModel?.Female is null,
                "The complete override failed to clear absent optional fields.");
        else
            Assert(armor.ObjectEffect.FormKey == ToFormKey(document.Enchantment!.Value) &&
                   armor.WorldModel?.Male?.Model?.File == document.MaleWorldModel &&
                   armor.WorldModel?.Female is null,
                "The new ARMO lost its optional reference or explicit model state.");
    }

    private static async Task VerifyArmorCleanupFailures(
        string proposalPath,
        string root)
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath("F:\\ExampleGame"));

        var commitFailure = new CommitFailureWithLockedTemporaryOperations();
        WorkspacePath commitFailureOutput = new(
            Path.Combine(root, "CommitFailure.esp"));
        ArmorBinaryWriteResult temporaryFailure =
            await new BethesdaArmorBinaryWriteService(
                    policy,
                    labRoot,
                    commitFailure)
                .WriteAsync(
                    new ArmorBinaryWriteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(proposalPath),
                        commitFailureOutput),
                    CancellationToken.None);
        Assert(!temporaryFailure.Written &&
               commitFailure.TemporaryPath is not null &&
               File.Exists(commitFailure.TemporaryPath) &&
               temporaryFailure.Diagnostics.Any(item =>
                   item.Code == "armor-binary-cleanup-failed" &&
                   item.Severity == DiagnosticSeverity.Warning &&
                   item.Message.Contains(
                       commitFailure.TemporaryPath,
                       StringComparison.Ordinal)),
            "A locked armor temporary plugin was not reported with its surviving path.");
        commitFailure.Dispose();
        File.Delete(commitFailure.TemporaryPath!);

        var hashFailure = new HashFailureWithLockedOutputOperations();
        WorkspacePath hashFailureOutput = new(
            Path.Combine(root, "HashFailure.esp"));
        ArmorBinaryWriteResult rollbackFailure =
            await new BethesdaArmorBinaryWriteService(
                    policy,
                    labRoot,
                    hashFailure)
                .WriteAsync(
                    new ArmorBinaryWriteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(proposalPath),
                        hashFailureOutput),
                    CancellationToken.None);
        Assert(!rollbackFailure.Written &&
               File.Exists(hashFailureOutput.Value) &&
               rollbackFailure.Diagnostics.Any(item =>
                   item.Code == "armor-binary-rollback-failed" &&
                   item.Severity == DiagnosticSeverity.Error &&
                   item.Message.Contains(
                       hashFailureOutput.Value,
                       StringComparison.Ordinal)),
            "A locked committed armor output was not reported when hash failure rollback could not remove it.");
        hashFailure.Dispose();
        File.Delete(hashFailureOutput.Value);
    }

    private sealed class CommitFailureWithLockedTemporaryOperations :
        IArmorBinaryFileOperations,
        IDisposable
    {
        private readonly ArmorBinaryFileOperations _files = new();
        private FileStream? _lock;

        public string? TemporaryPath { get; private set; }

        public void CommitNoOverwrite(string temporary, string output)
        {
            TemporaryPath = temporary;
            _lock = new FileStream(
                temporary,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            throw new IOException("Injected commit failure while the temporary plugin is locked.");
        }

        public ValueTask<Sha256Hash> HashAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hashing must not run after commit failure.");

        public ArmorBinaryCleanupResult DeleteIfPresent(string path) =>
            _files.DeleteIfPresent(path);

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    private sealed class HashFailureWithLockedOutputOperations :
        IArmorBinaryFileOperations,
        IDisposable
    {
        private readonly ArmorBinaryFileOperations _files = new();
        private FileStream? _lock;

        public void CommitNoOverwrite(string temporary, string output) =>
            _files.CommitNoOverwrite(temporary, output);

        public ValueTask<Sha256Hash> HashAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return ValueTask.FromException<Sha256Hash>(
                new IOException(
                    "Injected hash failure while the committed output is locked."));
        }

        public ArmorBinaryCleanupResult DeleteIfPresent(string path) =>
            _files.DeleteIfPresent(path);

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    private static void WriteArmorSource(string sourcePath)
    {
        ModKey key = ModKey.FromNameAndExtension("Source.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Races.Add(new Race(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceRace" });
        mod.Races.Add(new Race(new FormKey(key, 0x90B), SkyrimRelease.SkyrimSE)
        { EditorID = "OtherRace" });
        mod.Races.Add(new Race(new FormKey(key, 0x916), SkyrimRelease.SkyrimSE)
        { EditorID = "ThirdRace" });
        mod.TextureSets.Add(new TextureSet(
            new FormKey(key, 0x910), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceMaleSkin" });
        mod.TextureSets.Add(new TextureSet(
            new FormKey(key, 0x911), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceFemaleSkin" });
        mod.FormLists.Add(new FormList(
            new FormKey(key, 0x912), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceMaleSkinSwaps" });
        mod.FormLists.Add(new FormList(
            new FormKey(key, 0x913), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceFemaleSkinSwaps" });
        mod.FootstepSets.Add(new FootstepSet(
            new FormKey(key, 0x914), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceFootsteps" });
        mod.ArtObjects.Add(new ArtObject(
            new FormKey(key, 0x915), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceArtObject" });
        mod.ObjectEffects.Add(new ObjectEffect(new FormKey(key, 0x901), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceEnchantment" });
        mod.SoundDescriptors.Add(new SoundDescriptor(new FormKey(key, 0x902), SkyrimRelease.SkyrimSE)
        { EditorID = "SourcePickup" });
        mod.SoundDescriptors.Add(new SoundDescriptor(new FormKey(key, 0x903), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceDrop" });
        mod.EquipTypes.Add(new EquipType(new FormKey(key, 0x904), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceEquipType" });
        mod.MaterialTypes.Add(new MaterialType(new FormKey(key, 0x905), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceMaterial" });
        mod.Keywords.Add(new Keyword(new FormKey(key, 0x906), SkyrimRelease.SkyrimSE)
        { EditorID = "SourceKeyword" });
        mod.Keywords.Add(new Keyword(new FormKey(key, 0x909), SkyrimRelease.SkyrimSE)
        { EditorID = "SecondKeyword" });
        var sourceAddon = new ArmorAddon(
            new FormKey(key, 0x907), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SourceAddon",
            Race = new FormLinkNullable<IRaceGetter>(new FormKey(key, 0x900)),
            FootstepSound = new FormLinkNullable<IFootstepSetGetter>(
                new FormKey(key, 0x914)),
            ArtObject = new FormLinkNullable<IArtObjectGetter>(
                new FormKey(key, 0x915)),
            DetectionSoundValue = 9,
            WeaponAdjust = 25,
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x04
            },
            WorldModel = new GenderedItem<Model?>(
                new Model { File = "armor\\source_addon_m.nif" },
                new Model { File = "armor\\source_addon_f.nif" }),
            FirstPersonModel = new GenderedItem<Model?>(
                new Model { File = "armor\\source_addon_1st_m.nif" },
                new Model { File = "armor\\source_addon_1st_f.nif" }),
            SkinTexture = new GenderedItem<
                IFormLinkNullableGetter<ITextureSetGetter>>(
                new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(key, 0x910)),
                new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(key, 0x911))),
            TextureSwapList = new GenderedItem<
                IFormLinkNullableGetter<IFormListGetter>>(
                new FormLinkNullable<IFormListGetter>(
                    new FormKey(key, 0x912)),
                new FormLinkNullable<IFormListGetter>(
                    new FormKey(key, 0x913))),
            Priority = new GenderedItem<byte>(6, 7),
            WeightSliderEnabled = new GenderedItem<bool>(true, true)
        };
        sourceAddon.AdditionalRaces.Add(new FormLink<IRaceGetter>(
            new FormKey(key, 0x90B)));
        mod.ArmorAddons.Add(sourceAddon);
        mod.ArmorAddons.Add(new ArmorAddon(new FormKey(key, 0x908), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SecondAddon",
            Race = new FormLinkNullable<IRaceGetter>(new FormKey(key, 0x900)),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x08
            }
        });
        mod.ArmorAddons.Add(new ArmorAddon(new FormKey(key, 0x90A), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ReplacementAddon",
            Race = new FormLinkNullable<IRaceGetter>(new FormKey(key, 0x90B)),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x10
            }
        });
        mod.ArmorAddons.Add(new ArmorAddon(new FormKey(key, 0x90C), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SoonDeletedAddon",
            Race = new FormLinkNullable<IRaceGetter>(new FormKey(key, 0x900)),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x40
            }
        });
        mod.Armors.Add(new Armor(new FormKey(key, 0xA01), SkyrimRelease.SkyrimSE)
        { EditorID = "TemplateArmor" });
        var sourceArmor = new Armor(new FormKey(key, 0xA00), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SourceArmor",
            Name = "Source armor",
            Description = "Source description",
            Value = 10,
            Weight = 2,
            ArmorRating = 5,
            BodyTemplate = new BodyTemplate { FirstPersonFlags = (BipedObjectFlag)0x04 },
            Race = new FormLinkNullable<IRaceGetter>(new FormKey(key, 0x900)),
            ObjectEffect = new FormLinkNullable<IObjectEffectGetter>(new FormKey(key, 0x901)),
            PickUpSound = new FormLinkNullable<ISoundDescriptorGetter>(new FormKey(key, 0x902)),
            PutDownSound = new FormLinkNullable<ISoundDescriptorGetter>(new FormKey(key, 0x903)),
            EquipmentType = new FormLinkNullable<IEquipTypeGetter>(new FormKey(key, 0x904)),
            AlternateBlockMaterial = new FormLinkNullable<IMaterialTypeGetter>(new FormKey(key, 0x905)),
            TemplateArmor = new FormLinkNullable<IArmorGetter>(new FormKey(key, 0xA01)),
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16 { X = -1, Y = -1, Z = -1 },
                Second = new P3Int16 { X = 1, Y = 1, Z = 1 }
            },
            Keywords = [new FormLink<IKeywordGetter>(new FormKey(key, 0x906))],
            WorldModel = new GenderedItem<ArmorModel?>(
                new ArmorModel { Model = new Model { File = "armor\\old_m.nif" } },
                new ArmorModel { Model = new Model { File = "armor\\old_f.nif" } })
        };
        sourceArmor.Armature.Add(new FormLink<IArmorAddonGetter>(new FormKey(key, 0x907)));
        mod.Armors.Add(sourceArmor);
        mod.WriteToBinary(new FilePath(sourcePath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static void WriteArmorAddonReferenceProvider(string providerPath)
    {
        ModKey key = ModKey.FromNameAndExtension("Provider.esp");
        ModKey source = ModKey.FromNameAndExtension("Source.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = source });
        var additionalCompatible = new ArmorAddon(
            new FormKey(source, 0x908), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProviderSecondAddon",
            Race = new FormLinkNullable<IRaceGetter>(
                new FormKey(source, 0x90B)),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x20
            }
        };
        additionalCompatible.AdditionalRaces.Add(new FormLink<IRaceGetter>(
            new FormKey(source, 0x900)));
        mod.ArmorAddons.Add(additionalCompatible);
        mod.ArmorAddons.Add(new ArmorAddon(
            new FormKey(source, 0x90C), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DeletedAddon",
            Race = new FormLinkNullable<IRaceGetter>(
                new FormKey(source, 0x900)),
            IsDeleted = true
        });
        mod.WriteToBinary(new FilePath(providerPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static FormKey ToFormKey(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);
}
