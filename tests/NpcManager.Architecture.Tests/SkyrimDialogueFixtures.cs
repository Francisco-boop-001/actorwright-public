using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal sealed class SkyrimDialogueFixtures
{
    internal readonly string Root = Path.Combine(Environment.CurrentDirectory, "artifacts", "dialogue-tests", Guid.NewGuid().ToString("N"));
    internal static readonly ModKey Master = ModKey.FromNameAndExtension("Skyrim.esm");
    internal static readonly ModKey Plugin = ModKey.FromNameAndExtension("Example.esp");
    internal SkyrimDialogueManifest Manifest { get; }
    internal SkyrimVoiceSampleAuthority Sample { get; }
    internal WorkspacePath PathOf(string relative) => new(Path.Combine(Root, relative));
    internal SkyrimDialogueFixtures(bool light = true, bool extraRecords = false, bool existingInfo = false)
    {
        Directory.CreateDirectory(Root);
        var master = new SkyrimMod(Master, SkyrimRelease.SkyrimSE);
        uint id = 0x1000;
        foreach (string name in new[] { "DialogueFollower", "MQ101", "MQ103", "MQ104", "MQ105", "MQ105Ustengrav", "MQ201", "MQ301", "MQ303", "CW01A", "CW01B" })
            master.Quests.Add(new Quest(new(Master, id++), SkyrimRelease.SkyrimSE) { EditorID = name });
        foreach (string name in new[] { "PotentialFollowerFaction", "CurrentFollowerFaction", "DismissedFollowerFaction" })
            master.Factions.Add(new Faction(new(Master, id++), SkyrimRelease.SkyrimSE) { EditorID = name });
        master.Globals.Add(new GlobalFloat(new(Master, id++), SkyrimRelease.SkyrimSE) { EditorID = "PlayerFollowerCount", Data = 0 });
        foreach (string name in new[] { "LocTypeDungeon", "LocTypeCity", "LocTypeInn", "LocTypeTemple" })
            master.Keywords.Add(new Keyword(new(Master, id++), SkyrimRelease.SkyrimSE) { EditorID = name });
        foreach (string name in new[] { "SkyrimOvercastRain", "SkyrimStormRain", "SkyrimOvercastSnow", "SkyrimStormSnow" })
            master.Weathers.Add(new Weather(new(Master, id++), SkyrimRelease.SkyrimSE) { EditorID = name });
        master.Npcs.Add(new Npc(new(Master, 7), SkyrimRelease.SkyrimSE) { EditorID = "Player" });
        var cell = new Cell(new(Master, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "FixtureCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Persistent.Add(new PlacedNpc(new(Master, 0x14), SkyrimRelease.SkyrimSE) { Base = new FormLinkNullable<INpcGetter>(new FormKey(Master, 7)) });
        master.Cells.AddInteriorCell(cell);
        Write(master, PathOf("Skyrim.esm").Value);
        var plugin = new SkyrimMod(Plugin, SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        plugin.ModHeader.MasterReferences.Add(new MasterReference { Master = Master });
        plugin.ModHeader.Stats.NextFormID = 0x801;
        plugin.Npcs.Add(new Npc(new(Plugin, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "ExampleActor", Name = "Preserve me", Configuration = new NpcConfiguration { Flags = NpcConfiguration.Flag.Female } });
        if (extraRecords)
        {
            var voice = new VoiceType(new(Plugin, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "ExistingVoice", Flags = VoiceType.Flag.AllowDefaultDialog };
            plugin.VoiceTypes.Add(voice); plugin.Npcs.Single().Voice.SetTo(voice.FormKey);
            plugin.Npcs.Add(new Npc(new(Plugin, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "OtherActor", Name = "Untouched actor" });
            plugin.Quests.Add(new Quest(new(Plugin, 0x803), SkyrimRelease.SkyrimSE) { EditorID = "UnrelatedQuest", Priority = 20 });
            plugin.ModHeader.Stats.NextFormID = 0x804;
        }
        if (existingInfo)
        {
            var quest = new Quest(new(Plugin, 0x810), SkyrimRelease.SkyrimSE) { EditorID = "InheritedQuest" }; plugin.Quests.Add(quest);
            var topic = new DialogTopic(new(Plugin, 0x811), SkyrimRelease.SkyrimSE) { EditorID = "InheritedIdle", Subtype = DialogTopic.SubtypeEnum.Idle, SubtypeName = new RecordType("IDLE"), Category = DialogTopic.CategoryEnum.Misc };
            topic.Quest.SetTo(quest.FormKey);
            var info = new DialogResponses(new(Plugin, 0x812), SkyrimRelease.SkyrimSE); info.Topic.SetTo(topic.FormKey); info.Responses.Add(new DialogResponse { Text = "Existing audio is elsewhere.", ResponseNumber = 1 });
            topic.Responses.Add(info); plugin.DialogTopics.Add(topic); plugin.ModHeader.Stats.NextFormID = 0x813;
        }
        Write(plugin, PathOf("Example.esp").Value);
        var npc = new SkyrimDialogueNpcIdentity(new("Example.esp"), new(0x800), new EditorId("ExampleActor"), "AWExample", true);
        string[] subtypes = ["Hello", "Custom", "Custom", "Custom", "Custom", "Custom", "Attack", "Idle", "Hello", "Goodbye", "Hit", "Idle"];
        SkyrimDialogueAction[] actions = [SkyrimDialogueAction.None, SkyrimDialogueAction.Recruit, SkyrimDialogueAction.Dismiss, SkyrimDialogueAction.Wait, SkyrimDialogueAction.Follow, SkyrimDialogueAction.Trade];
        var lines = Enumerable.Range(0, 12).Select(i => new SkyrimDialogueLine("line-" + i, "category-" + i, "AWExampleTopic" + i, subtypes[i], "Original fixture response " + i,
            "Neutral", 50, i == 0 ? [new("IsInInterior", null, null, "==", 0, false, "subject")] : [], 50,
            i == 0 ? SkyrimDialogueRepeatPolicy.Once : SkyrimDialogueRepeatPolicy.Random, 30, i == 7 ? 2 : 0,
            i < 6 ? actions[i] : SkyrimDialogueAction.None, subtypes[i] == "Custom" ? "Fixture prompt " + i : null, "reviewed", "positive", "negative")).ToImmutableArray();
        Manifest = new(SkyrimNpcDialogueSchemas.Manifest, npc, "en", new("Example", [], [], null, null), "AWExampleDialogue", 60, null, lines);
        byte[] wave = Wave();
        File.WriteAllBytes(PathOf("ref.wav").Value, wave);
        var audio = new SkyrimVoiceSampleAudio(24000, 1, 16, "pcm", 24000, 1, -6, -9);
        Sample = new(SkyrimNpcVoiceSchemas.Sample, npc.Plugin, npc.FormId, npc.EditorId, npc.VoicePrefix, PathOf("ref.wav").Value, SkyrimNpcVoiceDocumentCodec.Hash(wave), audio,
            PathOf("ref.wav").Value, SkyrimNpcVoiceDocumentCodec.Hash(wave), audio, SkyrimNpcVoiceDocumentCodec.UtcNow());
        Save("sample.json", Sample);
    }
    internal Sha256Hash Save<T>(string name, T value)
    {
        byte[] bytes = SkyrimNpcVoiceDocumentCodec.Serialize(value);
        File.WriteAllBytes(PathOf(name).Value, bytes);
        return SkyrimNpcVoiceDocumentCodec.Hash(bytes);
    }
    internal SkyrimDialogueAnalyzeRequest Request(SkyrimDialogueManifest? manifest = null, string output = "proposal.json") => new(
        PathOf("dialogue.json"), Save("dialogue.json", manifest ?? Manifest), PathOf("Example.esp"), Hash("Example.esp"), PathOf("."), [new("Skyrim.esm"), new("Example.esp")], PathOf("sample.json"), Hash("sample.json"), PathOf(output));
    internal Sha256Hash Hash(string name) => SkyrimNpcVoiceDocumentCodec.Hash(File.ReadAllBytes(PathOf(name).Value));
    internal SkyrimVoiceSynthesisManifest Synthesis(SkyrimDialogueManifest? manifest = null)
    {
        var lines = (manifest ?? Manifest).Lines.Select(line =>
        {
            File.WriteAllBytes(PathOf(line.Id + ".wav").Value, Wave());
            return new SkyrimVoiceSynthesisLine(line.Id, line.Text, "en", SkyrimNpcVoiceDocumentCodec.Hash(System.Text.Encoding.UTF8.GetBytes(line.Text)), SkyrimVoiceLineStatus.Succeeded,
                line.Id + ".wav", Hash(line.Id + ".wav"), 1, 24000, 0, null, null);
        }).ToImmutableArray();
        return new(SkyrimNpcVoiceSchemas.Synthesis, PathOf("dialogue.json").Value, Hash("dialogue.json"), PathOf("sample.json").Value, Hash("sample.json"), Sample.NormalizedSha256,
            "http://127.0.0.1:1", "fixture", SkyrimNpcVoiceDocumentCodec.Hash("settings"u8), Sample.NormalizedPath, lines, SkyrimNpcVoiceDocumentCodec.UtcNow());
    }
    internal static byte[] Wave()
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(48036); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(48000);
        for (int i = 0; i < 24000; i++) writer.Write((short)(16000 * Math.Sin(i * 2 * Math.PI * 440 / 24000)));
        return output.ToArray();
    }
    private static void Write(SkyrimMod mod, string path) => mod.WriteToBinary(path, new BinaryWriteParameters
    { LowerRangeDisallowedHandler = ALowerRangeDisallowedHandlerOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
}
