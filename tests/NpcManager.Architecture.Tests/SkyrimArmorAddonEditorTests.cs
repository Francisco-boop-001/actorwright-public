using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions ArmorAddonTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async Task TestSkyrimArmorAddonEditor()
    {
        PluginName source = new("Source.esp");
        FormReference owningRace = new(source, new FormId(0x900));
        FormReference additionalRace = new(source, new FormId(0x901));
        SkyrimArmorAddonEditorDocument document = ArmorAddonEditorDocument(
            owningRace, additionalRace);

        SkyrimArmorAddonEditorResult accepted =
            SkyrimArmorAddonEditorRules.Save(document, []);
        Assert(accepted.Accepted && accepted.Document == document,
            "The complete immutable Skyrim ARMA document was refused.");
        Assert(!SkyrimArmorAddonEditorRules.Cancel().Accepted,
            "Armor-addon Cancel returned an accepted document.");

        foreach (SkyrimArmorAddonEditorIntent intent in
                 Enum.GetValues<SkyrimArmorAddonEditorIntent>())
        {
            SkyrimArmorAddonEditorDocument candidate = intent switch
            {
                SkyrimArmorAddonEditorIntent.BlankNew => document with
                {
                    Intent = intent,
                    SeedFromSource = false
                },
                SkyrimArmorAddonEditorIntent.NewFromTemplate => document with
                {
                    Intent = intent,
                    SeedFromSource = true
                },
                SkyrimArmorAddonEditorIntent.OverrideExisting => document with
                {
                    Intent = intent,
                    Mode = ArmorAddonProposalMode.Override,
                    EditorId = new EditorId("SourceAddon"),
                    TargetFormId = null,
                    TargetPlugin = null,
                    SeedFromSource = false
                },
                SkyrimArmorAddonEditorIntent.EditAuthored => document with
                {
                    Intent = intent
                },
                _ => throw new InvalidOperationException("Unexpected intent.")
            };
            Assert(SkyrimArmorAddonEditorRules.Save(candidate, []).Accepted,
                $"The {intent} Armor-addon intent was refused.");
        }

        Assert(!SkyrimArmorAddonEditorRules.Save(
                document with { WeaponAdjust = 100000.01 }, []).Accepted,
            "Out-of-range weapon adjustment was accepted.");
        Assert(SkyrimArmorAddonEditorRules.Save(
                document with { WeaponAdjust = 100000 }, []).Accepted,
            "The inclusive maximum weapon adjustment was refused.");
        Assert(!SkyrimArmorAddonEditorRules.Save(
                document with { EditorId = new EditorId("WrongPrefix") }, []).Accepted,
            "A new Armor-addon EditorID without the required prefix was accepted.");
        Assert(!SkyrimArmorAddonEditorRules.Save(
                document, [new EditorId("NPCM_ARMA_TRAVEL")]).Accepted,
            "A duplicate Armor-addon EditorID was accepted case-insensitively.");
        Assert(!SkyrimArmorAddonEditorRules.Save(document with
        {
            AdditionalRaces = [additionalRace, additionalRace]
        }, []).Accepted,
            "Duplicate ordered additional races were accepted.");
        Assert(!SkyrimArmorAddonEditorRules.Save(document with
        {
            MaleModel = "meshes\\armor\\travel_m.nif"
        }, []).Accepted,
            "A model path prefixed with Meshes was accepted.");

        WorkspacePath output = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\gate020.armor-addon-proposal.json");
        SkyrimArmorAddonProposalAdapterResult adapted =
            SkyrimArmorAddonEditorRules.ToProposal(document, output);
        Assert(adapted.Accepted && adapted.Proposal is
        {
            CompleteDocument: true,
            SeedFromSource: true,
            TargetPlugin.Value: "NewAddon.esp"
        } &&
            adapted.Proposal.Patch.AdditionalRaces?.SequenceEqual(
                [additionalRace]) == true &&
            adapted.Proposal.Patch.MaleWeightSliderFlags == 1 &&
            adapted.Proposal.Patch.FemaleWeightSliderFlags == 0,
            "The complete Armor-addon document did not map exactly to the typed proposal.");
        ArmorAddonProposalRequest proposal = adapted.Proposal ??
            throw new InvalidOperationException(
                "The accepted Armor-addon proposal is missing.");

        SkyrimArmorAddonReferenceTransition row =
            SkyrimArmorAddonEditorRules.ToReferenceRow(
                document, owningRace, output);
        Assert(row.Accepted && row.Row is { } acceptedRow &&
               acceptedRow.Reference == new FormReference(
                   new PluginName("NewAddon.esp"), new FormId(0xB00)) &&
               acceptedRow.Compatibility.PrimaryRace == owningRace &&
               acceptedRow.AuthoredProposal == proposal,
            "The complete Armor-addon child did not retain qualified identity, compatibility, and proposal state.");
        Assert(!SkyrimArmorAddonEditorRules.ToReferenceRow(
                document with { Race = additionalRace, AdditionalRaces = [] },
                owningRace, output).Accepted,
            "An Armor-addon incompatible with the owning Armor race was accepted.");

        await VerifySkyrimArmorAddonBinaryRoundTrip(document);
    }

    private static SkyrimArmorAddonEditorDocument ArmorAddonEditorDocument(
        FormReference owningRace,
        FormReference additionalRace) =>
        new(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorAddonEditorIntent.NewFromTemplate,
            ArmorAddonProposalMode.New,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\Source.esp"),
            new FormId(0xA00),
            new EditorId("npcm_ARMA_Travel"),
            new FormId(0xB00),
            new PluginName("NewAddon.esp"),
            true,
            "armor\\travel_m.nif",
            "armor\\travel_f.nif",
            "armor\\travel_1st_m.nif",
            "armor\\travel_1st_f.nif",
            0x14,
            owningRace,
            [additionalRace],
            new FormReference(owningRace.Plugin, new FormId(0x902)),
            new FormReference(owningRace.Plugin, new FormId(0x903)),
            new FormReference(owningRace.Plugin, new FormId(0x904)),
            new FormReference(owningRace.Plugin, new FormId(0x905)),
            new FormReference(owningRace.Plugin, new FormId(0x906)),
            new FormReference(owningRace.Plugin, new FormId(0x907)),
            4,
            5,
            true,
            false,
            7,
            123.25);

    private static async Task VerifySkyrimArmorAddonBinaryRoundTrip(
        SkyrimArmorAddonEditorDocument template)
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "armor-addon-editor-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "Source.esp");
            WriteArmorAddonSource(sourcePath);
            SkyrimArmorAddonEditorDocument newDocument = template with
            {
                SourcePlugin = new WorkspacePath(sourcePath)
            };
            await WriteAndVerifyArmorAddon(
                newDocument,
                Path.Combine(root, "new.armor-addon-proposal.json"),
                Path.Combine(root, "NewAddon.esp"),
                ModKey.FromNameAndExtension("NewAddon.esp"),
                0xB00,
                expectClearedOptionalFields: false);

            SkyrimArmorAddonEditorDocument overrideDocument =
                newDocument with
                {
                    Intent = SkyrimArmorAddonEditorIntent.OverrideExisting,
                    Mode = ArmorAddonProposalMode.Override,
                    EditorId = new EditorId("SourceAddon"),
                    TargetFormId = null,
                    TargetPlugin = null,
                    SeedFromSource = false,
                    MaleModel = null,
                    FemaleModel = null,
                    MaleFirstPersonModel = null,
                    FemaleFirstPersonModel = null,
                    Race = null,
                    AdditionalRaces = [],
                    MaleSkinTexture = null,
                    FemaleSkinTexture = null,
                    MaleSkinTextureSwapList = null,
                    FemaleSkinTextureSwapList = null,
                    FootstepSet = null,
                    ArtObject = null,
                    MalePriority = 10,
                    FemalePriority = 11,
                    MaleWeightSliderEnabled = false,
                    FemaleWeightSliderEnabled = true,
                    DetectionSound = 12,
                    WeaponAdjust = -54321.25
                };
            await WriteAndVerifyArmorAddon(
                overrideDocument,
                Path.Combine(root, "override.armor-addon-proposal.json"),
                Path.Combine(root, "OverrideAddon.esp"),
                ModKey.FromNameAndExtension("Source.esp"),
                0xA00,
                expectClearedOptionalFields: true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteAndVerifyArmorAddon(
        SkyrimArmorAddonEditorDocument document,
        string proposalPath,
        string outputPath,
        ModKey expectedOutputKey,
        uint expectedFormId,
        bool expectClearedOptionalFields)
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath("F:\\ExampleGame"));
        SkyrimArmorAddonProposalAdapterResult adapted =
            SkyrimArmorAddonEditorRules.ToProposal(
                document, new WorkspacePath(proposalPath));
        Assert(adapted.Accepted && adapted.Proposal is
        {
            CompleteDocument: true
        },
            "The accepted Armor-addon editor document did not map to one complete typed proposal.");

        var proposalService = new ArmorAddonProposalService(
            new BethesdaPluginReader(), policy, labRoot);
        ArmorAddonProposalResult proposal =
            await proposalService.ProposeAsync(
                adapted.Proposal!, CancellationToken.None);
        Assert(proposal.Written && proposal.Artifact is
        {
            CompleteDocument: true
        },
            "The complete typed ARMA proposal was refused: " +
            string.Join("; ", proposal.Diagnostics.Select(item => item.Message)));

        var writer = new BethesdaArmorAddonBinaryWriteService(policy, labRoot);
        string incompleteProposalPath = proposalPath + ".incomplete.json";
        string incompleteOutputPath = outputPath + ".incomplete.esp";
        await File.WriteAllTextAsync(
            incompleteProposalPath,
            JsonSerializer.Serialize(
                proposal.Artifact! with { ChangedFields = ["slotMask"] },
                ArmorAddonTestJsonOptions));
        ArmorAddonBinaryWriteResult incompleteWrite = await writer.WriteAsync(
            new ArmorAddonBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(incompleteProposalPath),
                new WorkspacePath(incompleteOutputPath)),
            CancellationToken.None);
        Assert(!incompleteWrite.Written &&
               !File.Exists(incompleteOutputPath) &&
               incompleteWrite.Diagnostics.Any(item =>
                   item.Code == "armor-addon-binary-complete-document"),
            "The binary writer accepted a forged incomplete complete-document proposal.");

        ArmorAddonBinaryWriteResult write = await writer.WriteAsync(
            new ArmorAddonBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(proposalPath),
                new WorkspacePath(outputPath)),
            CancellationToken.None);
        Assert(write.Written,
            "The complete Skyrim ARMA writer was refused: " +
            string.Join("; ", write.Diagnostics.Select(item => item.Message)));

        using var reopened = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(ModKey.FromNameAndExtension(
                    Path.GetFileName(outputPath)),
                new FilePath(outputPath)),
            SkyrimRelease.SkyrimSE);
        IArmorAddonGetter addon = reopened.ArmorAddons.Single();
        Assert(reopened.EnumerateMajorRecords().Count() == 1 &&
               addon.FormKey == new FormKey(
                   expectedOutputKey, expectedFormId) &&
               addon.EditorID == document.EditorId.Value &&
               addon.BodyTemplate?.FirstPersonFlags ==
                   (BipedObjectFlag)document.SlotMask &&
               addon.DetectionSoundValue == document.DetectionSound &&
               Math.Abs(addon.WeaponAdjust - document.WeaponAdjust) < 0.01 &&
               addon.Priority?.Male == document.MalePriority &&
               addon.Priority?.Female == document.FemalePriority &&
               addon.WeightSliderEnabled?.Male ==
                   document.MaleWeightSliderEnabled &&
               addon.WeightSliderEnabled?.Female ==
                   document.FemaleWeightSliderEnabled,
            "Independent ARMA readback lost identity, scalar data, or emitted unrelated records.");
        if (expectClearedOptionalFields)
            Assert(addon.Race.IsNull &&
                   addon.FootstepSound.IsNull &&
                   addon.ArtObject.IsNull &&
                   addon.AdditionalRaces.Count == 0 &&
                   addon.WorldModel?.Male is null &&
                   addon.WorldModel?.Female is null &&
                   addon.FirstPersonModel?.Male is null &&
                   addon.FirstPersonModel?.Female is null &&
                   addon.SkinTexture?.Male?.IsNull != false &&
                   addon.SkinTexture?.Female?.IsNull != false &&
                   addon.TextureSwapList?.Male?.IsNull != false &&
                   addon.TextureSwapList?.Female?.IsNull != false,
                "The complete override failed to clear absent optional ARMA fields.");
        else
            Assert(addon.Race.FormKey == ToArmorAddonFormKey(
                       document.Race!.Value) &&
                   addon.FootstepSound.FormKey == ToArmorAddonFormKey(
                       document.FootstepSet!.Value) &&
                   addon.ArtObject.FormKey == ToArmorAddonFormKey(
                       document.ArtObject!.Value) &&
                   addon.AdditionalRaces.Select(item => item.FormKey)
                       .SequenceEqual(document.AdditionalRaces.Select(
                           ToArmorAddonFormKey)) &&
                   addon.WorldModel?.Male?.File == document.MaleModel &&
                   addon.WorldModel?.Female?.File == document.FemaleModel &&
                   addon.FirstPersonModel?.Male?.File ==
                       document.MaleFirstPersonModel &&
                   addon.FirstPersonModel?.Female?.File ==
                       document.FemaleFirstPersonModel &&
                   addon.SkinTexture?.Male?.FormKey == ToArmorAddonFormKey(
                       document.MaleSkinTexture!.Value) &&
                   addon.SkinTexture?.Female?.FormKey == ToArmorAddonFormKey(
                       document.FemaleSkinTexture!.Value) &&
                   addon.TextureSwapList?.Male?.FormKey ==
                       ToArmorAddonFormKey(
                           document.MaleSkinTextureSwapList!.Value) &&
                   addon.TextureSwapList?.Female?.FormKey ==
                       ToArmorAddonFormKey(
                           document.FemaleSkinTextureSwapList!.Value),
                "The new ARMA lost a supported model, race, skin, or footstep route.");
    }

    private static void WriteArmorAddonSource(string sourcePath)
    {
        ModKey key = ModKey.FromNameAndExtension("Source.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var sourceAddon = new ArmorAddon(
            new FormKey(key, 0xA00), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SourceAddon",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x04
            },
            Race = new FormLinkNullable<IRaceGetter>(
                new FormKey(key, 0x900)),
            FootstepSound = new FormLinkNullable<IFootstepSetGetter>(
                new FormKey(key, 0x906)),
            ArtObject = new FormLinkNullable<IArtObjectGetter>(
                new FormKey(key, 0x907)),
            DetectionSoundValue = 9,
            WeaponAdjust = 25,
            WorldModel = new GenderedItem<Model?>(
                new Model { File = "armor\\old_m.nif" },
                new Model { File = "armor\\old_f.nif" }),
            FirstPersonModel = new GenderedItem<Model?>(
                new Model { File = "armor\\old_1st_m.nif" },
                new Model { File = "armor\\old_1st_f.nif" }),
            SkinTexture = new GenderedItem<
                IFormLinkNullableGetter<ITextureSetGetter>>(
                new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(key, 0x902)),
                new FormLinkNullable<ITextureSetGetter>(
                    new FormKey(key, 0x903))),
            TextureSwapList = new GenderedItem<
                IFormLinkNullableGetter<IFormListGetter>>(
                new FormLinkNullable<IFormListGetter>(
                    new FormKey(key, 0x904)),
                new FormLinkNullable<IFormListGetter>(
                    new FormKey(key, 0x905))),
            Priority = new GenderedItem<byte>(6, 7),
            WeightSliderEnabled = new GenderedItem<bool>(true, true)
        };
        sourceAddon.AdditionalRaces.Add(new FormLink<IRaceGetter>(
            new FormKey(key, 0x901)));
        mod.ArmorAddons.Add(sourceAddon);
        mod.WriteToBinary(new FilePath(sourcePath),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }

    private static FormKey ToArmorAddonFormKey(FormReference reference) =>
        new(ModKey.FromNameAndExtension(reference.Plugin.Value),
            reference.FormId.Value);
}
