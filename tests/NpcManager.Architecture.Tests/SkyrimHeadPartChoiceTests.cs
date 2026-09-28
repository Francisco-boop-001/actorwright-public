using System.Collections.Immutable;
using System.Security.Cryptography;
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
    private static async Task TestSkyrimHeadPartChoiceCatalog()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "headpart-choice-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        const string baseName = "HeadPartBase.esp";
        const string overrideName = "HeadPartOverride.esp";
        string basePath = Path.Combine(dataRoot, baseName);
        string overridePath = Path.Combine(dataRoot, overrideName);
        try
        {
            CreateHeadPartBase(basePath);
            CreateHeadPartOverride(baseName, overridePath);
            var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
            var copiedDataRoot = new WorkspacePath(dataRoot);
            ImmutableArray<PluginName> pluginOrder =
                [new PluginName(baseName), new PluginName(overrideName)];
            SkyrimFaceRecordPluginAuthorityResult authority =
                await authorityLoader.LoadAsync(
                    new SkyrimFaceRecordPluginAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        copiedDataRoot,
                        pluginOrder),
                    CancellationToken.None);
            Assert(authority.Accepted && authority.Authorities.Length == 2,
                "The synthetic copied HDPT authority was not hash-bound.");

            var service = new BethesdaSkyrimHeadPartChoiceService(authorityLoader);
            var race = new FormReference(new PluginName(baseName), new FormId(0x900));
            SkyrimHeadPartChoiceResult female = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Female,
                    NpcHeadPartType.Hair,
                    null),
                CancellationToken.None);
            Assert(female.Accepted &&
                   female.Candidates.Select(item => item.EditorId).SequenceEqual([
                       "HairFemaleOverride",
                       "HairNeutral",
                       "HairRaceDefault",
                       "HairUnrestricted"
                   ]),
                "Female HDPT filtering did not apply override, gender, extra, and race rules exactly.");
            SkyrimHeadPartChoiceCandidate winner = female.Candidates[0];
            Assert(winner.Reference == new FormReference(
                       new PluginName(baseName), new FormId(0xA00)) &&
                   winner.Provider.Plugin == new PluginName(overrideName) &&
                   winner.RaceMatch == SkyrimHeadPartRaceMatchKind.ValidRaceList &&
                   winner.PreviewModels.Length == 2 &&
                   winner.PreviewModels[0].IsRoot && !winner.PreviewModels[1].IsRoot,
                "Winning HDPT provenance or bounded HNAM preview closure changed.");
            Assert(female.Candidates.Single(item => item.EditorId == "HairNeutral")
                       .SupportsMale == false &&
                   female.Candidates.Single(item => item.EditorId == "HairNeutral")
                       .SupportsFemale == false &&
                   female.Candidates.Single(item => item.EditorId == "HairUnrestricted")
                       .RaceMatch == SkyrimHeadPartRaceMatchKind.HumanoidUnrestricted &&
                   female.Candidates.Single(item => item.EditorId == "HairRaceDefault")
                       .RaceMatch == SkyrimHeadPartRaceMatchKind.RaceDefault,
                "Gender-neutral, humanoid-unrestricted, or race-default evidence changed.");

            SkyrimHeadPartChoiceResult male = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Male,
                    NpcHeadPartType.Hair,
                    null),
                CancellationToken.None);
            Assert(male.Candidates.Select(item => item.EditorId).SequenceEqual([
                       "HairMale", "HairNeutral"
                   ]),
                "Male HDPT filtering did not retain only male and neutral records.");

            SkyrimHeadPartChoiceResult misc = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Female,
                    NpcHeadPartType.Misc,
                    "MiscExtra"),
                CancellationToken.None);
            Assert(misc.Candidates.Length == 1 && misc.Candidates[0].IsExtra,
                "The Misc bucket did not retain its explicitly requested extra HDPT.");

            SkyrimHeadPartChoiceResult invalidPreview = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Female,
                    NpcHeadPartType.Eyebrows,
                    "InvalidPreview"),
                CancellationToken.None);
            Assert(invalidPreview.Accepted && invalidPreview.Candidates.Length == 1 &&
                   invalidPreview.Candidates[0].ModelNif is null &&
                   invalidPreview.Candidates[0].PreviewModels.IsEmpty,
                "An invalid root model retained a misleading partial HNAM preview closure.");

            SkyrimHeadPartChoiceResult filtered = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Female,
                    NpcHeadPartType.Hair,
                    "override"),
                CancellationToken.None);
            Assert(filtered.Candidates.Length == 1 &&
                   filtered.Candidates[0].EditorId == "HairFemaleOverride",
                "HDPT text filtering did not use the cached compatible catalog semantics.");

            Sha256Hash baseHash = HashFile(basePath);
            Sha256Hash overrideHash = HashFile(overridePath);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(root),
                copiedDataRoot,
                new WorkspacePath(Path.Combine(root, "load-order.json")),
                new WorkspacePath(Path.Combine(root, "output")),
                new Sha256Hash(new string('A', 64)),
                [
                    new PluginClosureReviewEntry(
                        new PluginName(baseName), 0, true, true, true, false, true,
                        new WorkspacePath(basePath), baseHash, []),
                    new PluginClosureReviewEntry(
                        new PluginName(overrideName), 1, true, true, true, false, true,
                        new WorkspacePath(overridePath), overrideHash,
                        [new PluginName(baseName)])
                ],
                [],
                [],
                [],
                0,
                new Sha256Hash(new string('B', 64)),
                new Sha256Hash(new string('C', 64)),
                false);
            var editLoadService = new SkyrimHeadPartEditLoadService(service);
            SkyrimHeadPartEditLoadResult typedBaseline = await editLoadService.LoadAsync(
                new SkyrimHeadPartEditLoadRequest(
                    intake,
                    new PluginName(baseName),
                    new FormId(0xB00)),
                CancellationToken.None);
            Assert(typedBaseline.Accepted && typedBaseline.Snapshot is not null &&
                   typedBaseline.Snapshot.HeadParts.Single().Type == NpcHeadPartType.Hair,
                "The production head-part edit load did not reconcile the NPC PNAM baseline with the typed HDPT catalog.");

            SkyrimHeadPartChoiceResult missingRace = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    new FormReference(new PluginName(baseName), new FormId(0xFFFF)),
                    NpcSex.Female,
                    NpcHeadPartType.Hair,
                    null),
                CancellationToken.None);
            Assert(!missingRace.Accepted && missingRace.Diagnostics.Any(item =>
                    item.Code == "headpart-choice-race-missing"),
                "Missing RACE authority did not fail closed.");

            SkyrimHeadPartChoiceResult invalidType = await service.SearchAsync(
                new SkyrimHeadPartChoiceRequest(
                    copiedDataRoot,
                    pluginOrder,
                    race,
                    NpcSex.Female,
                    (NpcHeadPartType)999,
                    null),
                CancellationToken.None);
            Assert(!invalidType.Accepted && invalidType.Diagnostics.Any(item =>
                    item.Code == "headpart-choice-type-invalid"),
                "An undefined head-part enum crossed the typed service boundary.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() =>
                service.SearchAsync(
                    new SkyrimHeadPartChoiceRequest(
                        copiedDataRoot,
                        pluginOrder,
                        race,
                        NpcSex.Female,
                        NpcHeadPartType.Hair,
                        null),
                    cancelled.Token).AsTask());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateHeadPartBase(string path)
    {
        var key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var raceKey = new FormKey(key, 0x900);
        var otherRaceKey = new FormKey(key, 0x901);
        var validRacesKey = new FormKey(key, 0x910);
        var wrongRacesKey = new FormKey(key, 0x911);
        var femaleKey = new FormKey(key, 0xA00);
        var maleKey = new FormKey(key, 0xA01);
        var neutralKey = new FormKey(key, 0xA02);
        var extraHairKey = new FormKey(key, 0xA03);
        var unrestrictedKey = new FormKey(key, 0xA04);
        var defaultKey = new FormKey(key, 0xA05);
        var wrongRaceKey = new FormKey(key, 0xA06);
        var miscExtraKey = new FormKey(key, 0xA07);
        var invalidPreviewKey = new FormKey(key, 0xA08);
        var npcKey = new FormKey(key, 0xB00);

        mod.FormLists.Add(new FormList(validRacesKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ValidRaces",
            Items = [new FormLink<IRaceGetter>(raceKey)]
        });
        mod.FormLists.Add(new FormList(wrongRacesKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "WrongRaces",
            Items = [new FormLink<IRaceGetter>(otherRaceKey)]
        });
        var femaleHead = new HeadData();
        femaleHead.HeadParts.Add(new HeadPartReference
        {
            Head = new FormLinkNullable<IHeadPartGetter>(defaultKey)
        });
        mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "TargetRace",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), femaleHead)
        });
        mod.Races.Add(new Race(otherRaceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "OtherRace"
        });

        HeadPart female = Part(femaleKey, "HairFemaleBase", HeadPart.Flag.Female,
            HeadPart.TypeEnum.Hair, validRacesKey, "female-base.nif");
        female.ExtraParts.Add(new FormLink<IHeadPartGetter>(miscExtraKey));
        mod.HeadParts.Add(female);
        mod.HeadParts.Add(Part(maleKey, "HairMale", HeadPart.Flag.Male,
            HeadPart.TypeEnum.Hair, validRacesKey, "male.nif"));
        mod.HeadParts.Add(Part(neutralKey, "HairNeutral", 0,
            HeadPart.TypeEnum.Hair, validRacesKey, "neutral.nif"));
        mod.HeadParts.Add(Part(extraHairKey, "HairExtraExcluded",
            HeadPart.Flag.Female | HeadPart.Flag.IsExtraPart,
            HeadPart.TypeEnum.Hair, validRacesKey, "extra-hair.nif"));
        mod.HeadParts.Add(Part(unrestrictedKey, "HairUnrestricted", HeadPart.Flag.Female,
            HeadPart.TypeEnum.Hair, null, "unrestricted.nif"));
        mod.HeadParts.Add(Part(defaultKey, "HairRaceDefault", HeadPart.Flag.Female,
            HeadPart.TypeEnum.Hair, wrongRacesKey, "default.nif"));
        mod.HeadParts.Add(Part(wrongRaceKey, "HairWrongRace", HeadPart.Flag.Female,
            HeadPart.TypeEnum.Hair, wrongRacesKey, "wrong-race.nif"));
        mod.HeadParts.Add(Part(miscExtraKey, "MiscExtra",
            HeadPart.Flag.Female | HeadPart.Flag.IsExtraPart,
            HeadPart.TypeEnum.Misc, validRacesKey, "misc-extra.nif"));
        HeadPart invalidPreview = Part(invalidPreviewKey, "InvalidPreview",
            HeadPart.Flag.Female, HeadPart.TypeEnum.Eyebrows,
            validRacesKey, "C:/invalid.nif");
        invalidPreview.ExtraParts.Add(new FormLink<IHeadPartGetter>(miscExtraKey));
        mod.HeadParts.Add(invalidPreview);
        var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "HeadPartEditNpc",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(raceKey)
        };
        npc.HeadParts.Add(new FormLink<IHeadPartGetter>(femaleKey));
        mod.Npcs.Add(npc);
        Write(mod, path);

        HeadPart Part(
            FormKey formKey,
            string editorId,
            HeadPart.Flag flags,
            HeadPart.TypeEnum type,
            FormKey? validRaces,
            string model)
        {
            var part = new HeadPart(formKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = editorId,
                Name = editorId,
                Flags = flags,
                Type = type,
                Model = new Model { File = $"meshes/headpart-choice/{model}" }
            };
            if (validRaces is { } list)
                part.ValidRaces = new FormLinkNullable<IFormListGetter>(list);
            return part;
        }
    }

    private static void CreateHeadPartOverride(string baseName, string path)
    {
        var key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var baseKey = ModKey.FromNameAndExtension(baseName);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = baseKey });
        var female = new HeadPart(new FormKey(baseKey, 0xA00), SkyrimRelease.SkyrimSE)
        {
            EditorID = "HairFemaleOverride",
            Name = "Female override",
            Flags = HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            ValidRaces = new FormLinkNullable<IFormListGetter>(
                new FormKey(baseKey, 0x910)),
            Model = new Model { File = "meshes/headpart-choice/female-override.nif" }
        };
        female.ExtraParts.Add(new FormLink<IHeadPartGetter>(
            new FormKey(baseKey, 0xA07)));
        mod.HeadParts.Add(female);
        Write(mod, path);
    }

    private static void Write(SkyrimMod mod, string path) =>
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}
