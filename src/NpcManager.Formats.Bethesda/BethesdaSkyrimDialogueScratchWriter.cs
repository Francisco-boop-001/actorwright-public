using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaSkyrimDialogueScratchWriter
{
    public static SkyrimDialogueProposal Plan(SkyrimDialogueAnalyzeRequest request, SkyrimDialogueManifest manifest,
        byte[] source, BethesdaSkyrimVanillaDialogueAuthority authority)
    {
        var raw = BethesdaSkyrimNpcFinishCoreRaw.Read(source, manifest.Npc.FormId.Value);
        uint owner = (uint)raw.Tes4.Masters.Length;
        var owned = raw.Records.Where(x => x.Signature != "TES4" && x.RawFormId >> 24 == owner).ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var allocations = ImmutableArray.CreateBuilder<SkyrimDialogueRecordAllocation>();
        var assets = ImmutableArray.CreateBuilder<SkyrimDialogueAssetPlan>();
        uint next = Math.Max(0x800, Math.Max(raw.Tes4.NextFormId, owned.Max(x => (x.RawFormId & 0xFFFFFF) + 1)));
        int planned = 2 + manifest.Lines.Select(x => x.Topic).Distinct(StringComparer.Ordinal).Count() + manifest.Lines.Length +
            manifest.Lines.Where(x => x.Subtype == "Custom").Select(x => x.Topic).Distinct(StringComparer.Ordinal).Count();
        bool fits = owned.Length + planned + 64 <= 2048 && next + planned + 64 <= 0x1000 &&
            owned.All(x => (x.RawFormId & 0xFFFFFF) is >= 0x800 and <= 0xFFF);
        var budget = new SkyrimDialogueBudget(owned.Length, planned, 64, 2048, fits);
        try
        {
            if (!request.Plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || !manifest.Npc.Plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
                Fail(SkyrimNpcDialogueDiagnosticCodes.PluginTypeRefused, "Dialogue requires an .esp filename.");
            if (manifest.Schema != SkyrimNpcDialogueSchemas.Manifest || manifest.Lines.IsDefaultOrEmpty || manifest.Npc.Plugin.Value != Path.GetFileName(request.Plugin.Value))
                Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Manifest schema, lines, or plugin identity is invalid.");
            if (raw.Records.Any(x => x.Signature != "TES4" && x.RawFormId >> 24 != owner))
                Fail(SkyrimNpcDialogueDiagnosticCodes.VerifyForeignOverride, "Dialogue authoring requires a source without foreign overrides.");
            if (raw.Records.Any(x => x.Signature == "INFO"))
                Fail(SkyrimNpcDialogueDiagnosticCodes.SourceDialogueUnsupported, "Inherited dialogue/audio import is unsupported. Supply the silent NPC source before dialogue authoring; existing INFO records cannot be admitted without their audio authority.");
            if ((raw.Target!.Flags & 0x40000u) != 0) Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Compressed source NPC is not admitted for raw replacement.");
            RequireId(manifest.QuestEditorId); RequireId(manifest.Npc.VoicePrefix);
            if (manifest.Lines.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Lines.Length)
                Fail(SkyrimNpcDialogueDiagnosticCodes.LineDuplicate, "Line IDs must be unique.");
            foreach (var line in manifest.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) Fail(SkyrimNpcDialogueDiagnosticCodes.LineTextEmpty, "Line text is empty: " + line.Id);
                if (!LineId().IsMatch(line.Id)) Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Invalid line ID.");
                RequireId(line.Topic); RequireId(line.Category.Replace('-', '_'));
                _ = Subtype(line.Subtype);
                if (!Enum.TryParse(line.Emotion, false, out Emotion emotion) || !Enum.IsDefined(emotion) || line.EmotionValue > 100 ||
                    !Enum.IsDefined(line.Action) || !Enum.IsDefined(line.Repeat) || !float.IsFinite(line.Priority) || line.RandomPercent is < 0 or > 100 || line.CooldownHours < 0)
                    Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Invalid emotion, action, repeat, priority or thinning.");
                if (line.Subtype == "Custom" && string.IsNullOrWhiteSpace(line.Prompt) || line.Action != SkyrimDialogueAction.None && line.Subtype != "Custom")
                    Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Custom topics need a prompt; actions need Custom topics.");
                foreach (var condition in line.Conditions) _ = authority.Condition(condition);
                if (line.Action != SkyrimDialogueAction.None) _ = authority.Resolve<IQuestGetter>("DialogueFollower");
            }
            foreach (var topic in manifest.Lines.GroupBy(x => x.Topic, StringComparer.Ordinal))
                if (topic.Select(x => x.Subtype).Distinct().Count() != 1 || topic.Select(x => x.Priority).Distinct().Count() != 1)
                    Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Lines sharing a topic need identical subtype and priority.");
            if (!fits) Fail(SkyrimNpcDialogueDiagnosticCodes.BudgetExceeded, $"Owned {owned.Length} + planned {planned} + headroom 64 exceeds the 2048-record conservative range; next 0x{next:X}.");
            Allocate("VTYP", manifest.Npc.VoicePrefix + "Voice"); Allocate("QUST", manifest.QuestEditorId);
            foreach (var topic in manifest.Lines.GroupBy(x => x.Topic))
            {
                Allocate("DIAL", topic.Key);
                if (topic.First().Subtype == "Custom") Allocate("DLBR", topic.Key + "Branch");
                foreach (var line in topic)
                {
                    uint info = Allocate("INFO", manifest.Npc.VoicePrefix + "Line_" + line.Id.Replace('-', '_'), line.Id);
                    string voice = manifest.Npc.VoicePrefix + "Voice";
                    assets.Add(new(line.Id, info, voice, $"sound/voice/{manifest.Npc.Plugin.Value}/{voice}",
                        SkyrimDialogueAssetPlan.LookupStem(manifest.QuestEditorId, topic.Key, info, 1)));
                }
            }
            if (manifest.Lines.Any(x => x.CooldownHours > 0)) diagnostics.Add(new(SkyrimNpcDialogueDiagnosticCodes.CooldownAdvisory, DiagnosticSeverity.Warning,
                "Cooldown hours are advisory; INFO random-percent thinning is used (30 percent when absent). No globals, polling, or true time cooldown are installed."));
        }
        catch (InvalidDataException ex)
        {
            int split = ex.Message.IndexOf(':');
            diagnostics.Add(new(split > 0 ? ex.Message[..split] : SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, DiagnosticSeverity.Error, ex.Message));
        }
        return new(SkyrimNpcDialogueSchemas.Proposal, request.Manifest.Value, request.ManifestSha256, manifest, request.Plugin.Value, manifest.Npc.Plugin,
            request.PluginSha256, request.DataRoot.Value, request.LoadOrder, request.SampleAuthority.Value, request.SampleAuthoritySha256, authority.LightPlugin,
            authority.Masters, next, allocations.ToImmutable(), assets.ToImmutable(), budget,
            diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) ? SkyrimDialogueProposalStatus.Refused : SkyrimDialogueProposalStatus.ReadyForReviewedWrite, diagnostics.ToImmutable());
        uint Allocate(string signature, string editorId, string? lineId = null) { RequireId(editorId); allocations.Add(new(signature, next, editorId, lineId)); return next++; }
    }

    public static byte[] Write(SkyrimDialogueProposal proposal, BethesdaSkyrimVanillaDialogueAuthority authority)
    {
        if (proposal.Status != SkyrimDialogueProposalStatus.ReadyForReviewedWrite) Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Refused proposal cannot be written.");
        ModKey key = authority.NpcKey.ModKey;
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { IsSmallMaster = proposal.LightPlugin };
        foreach (string master in proposal.MasterOrder) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension(master) });
        mod.ModHeader.Stats.NextFormID = proposal.NextFormId;
        var voiceRow = proposal.Records.Single(x => x.Signature == "VTYP");
        var questRow = proposal.Records.Single(x => x.Signature == "QUST");
        var voice = new VoiceType(new(key, voiceRow.LocalFormId), SkyrimRelease.SkyrimSE) { EditorID = voiceRow.EditorId, Flags = proposal.Manifest.Npc.Female ? VoiceType.Flag.Female : 0 };
        mod.VoiceTypes.Add(voice);
        var quest = new Quest(new(key, questRow.LocalFormId), SkyrimRelease.SkyrimSE)
        { EditorID = questRow.EditorId, Flags = Quest.Flag.StartGameEnabled, Priority = proposal.Manifest.QuestPriority };
        quest.DialogConditions.Add(authority.Condition(new("GetIsID", "self", null, "==", 1, false, "subject")));
        mod.Quests.Add(quest);
        var npc = (Npc)authority.Npc.DeepCopy(); npc.IsCompressed = false; npc.Voice.SetTo(voice.FormKey); mod.Npcs.Add(npc);
        foreach (var topicLines in proposal.Manifest.Lines.GroupBy(x => x.Topic))
        {
            var first = topicLines.First(); var row = proposal.Records.Single(x => x.Signature == "DIAL" && x.EditorId == topicLines.Key);
            var (subtype, name, category) = Subtype(first.Subtype);
            var topic = new DialogTopic(new(key, row.LocalFormId), SkyrimRelease.SkyrimSE)
            { EditorID = row.EditorId, Priority = first.Priority, Subtype = subtype, SubtypeName = new RecordType(name), Category = category };
            topic.Quest.SetTo(quest.FormKey);
            if (first.Subtype == "Custom")
            {
                var branchRow = proposal.Records.Single(x => x.Signature == "DLBR" && x.EditorId == row.EditorId + "Branch");
                var branch = new DialogBranch(new(key, branchRow.LocalFormId), SkyrimRelease.SkyrimSE)
                { EditorID = branchRow.EditorId, Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
                branch.Quest.SetTo(quest.FormKey); branch.StartingTopic.SetTo(topic.FormKey); topic.Branch.SetTo(branch.FormKey); mod.DialogBranches.Add(branch);
            }
            FormKey previous = FormKey.Null;
            foreach (var line in topicLines)
            {
                var infoRow = proposal.Records.Single(x => x.LineId == line.Id);
                var info = new DialogResponses(new(key, infoRow.LocalFormId), SkyrimRelease.SkyrimSE)
                { EditorID = infoRow.EditorId, Flags = new DialogResponseFlags { Flags = RepeatFlags(line) } };
                info.Topic.SetTo(topic.FormKey); if (!previous.IsNull) info.PreviousDialog.SetTo(previous); previous = info.FormKey;
                if (line.Prompt is not null) info.Prompt = line.Prompt;
                info.Responses.Add(new DialogResponse { Text = line.Text, Emotion = Enum.Parse<Emotion>(line.Emotion), EmotionValue = line.EmotionValue, ResponseNumber = 1 });
                foreach (var condition in line.Conditions) info.Conditions.Add(authority.Condition(condition));
                int thinning = line.RandomPercent > 0 ? line.RandomPercent : line.CooldownHours > 0 ? 30 : 0;
                if (thinning > 0) info.Conditions.Add(authority.Condition(new("GetRandomPercent", null, null, "<", thinning, false, "subject")));
                if (line.Action != SkyrimDialogueAction.None)
                {
                    info.VirtualMachineAdapter = new DialogResponsesAdapter
                    {
                        Version = 5, ObjectFormat = 2,
                        ScriptFragments = new ScriptFragments { ExtraBindDataVersion = 2, FileName = "ActorwrightFollowerDialogue",
                            OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "ActorwrightFollowerDialogue", FragmentName = "Fragment_" + line.Action } }
                    };
                    var script = new ScriptEntry { Name = "ActorwrightFollowerDialogue" };
                    var property = new ScriptObjectProperty { Name = "DialogueFollower", Flags = ScriptProperty.Flag.Edited, Alias = -1 };
                    property.Object.SetTo(authority.Resolve<IQuestGetter>("DialogueFollower")); script.Properties.Add(property); info.VirtualMachineAdapter.Scripts.Add(script);
                }
                topic.Responses.Add(info);
            }
            mod.DialogTopics.Add(topic);
        }
        using var stream = new MemoryStream();
        mod.WriteToBinary(stream, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
        return stream.ToArray();
    }

    internal static DialogResponses.Flag RepeatFlags(SkyrimDialogueLine line) => (line.Repeat switch
    { SkyrimDialogueRepeatPolicy.Once => DialogResponses.Flag.SayOnce, SkyrimDialogueRepeatPolicy.Random => DialogResponses.Flag.Random, _ => 0 }) |
        (line.Subtype == "Goodbye" ? DialogResponses.Flag.Goodbye : 0);

    internal static (DialogTopic.SubtypeEnum Subtype, string Name, DialogTopic.CategoryEnum Category) Subtype(string subtype)
    {
        // Skyrim SNAM signatures, verified against TES5Edit wbDefinitionsTES5.pas.
        string name = subtype switch
        {
            "Custom" => "CUST", "Hello" => "HELO", "Goodbye" => "GBYE", "Idle" => "IDLE", "Attack" => "ATCK", "PowerAttack" => "POAT",
            "Hit" => "HIT_", "Flee" => "FLEE", "Bleedout" => "BLED", "AllyKilled" => "ALKL", "Taunt" => "TAUT", "Death" => "DETH", "Block" => "BLOC",
            "NormalToCombat" => "NOTC", "CombatToNormal" => "COTN", "LostToNormal" => "LOTN", "NormalToAlert" => "NOTA", "AlertToCombat" => "ALTC",
            "Steal" => "STEA", "Assault" => "ASSA", "Murder" => "MURD", "Trespass" => "TRES", "PickpocketNC" => "PICN",
            "ObserveCombat" => "OBCO", "NoticeCorpse" => "NOTI",
            _ => throw new InvalidDataException($"{SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid}: Unsupported dialogue subtype '{subtype}'.")
        };
        var value = Enum.Parse<DialogTopic.SubtypeEnum>(subtype);
        var category = value == DialogTopic.SubtypeEnum.Custom ? DialogTopic.CategoryEnum.Topic :
            value is >= DialogTopic.SubtypeEnum.Attack and <= DialogTopic.SubtypeEnum.WerewolfTransformCrime ? DialogTopic.CategoryEnum.Combat :
            value is >= DialogTopic.SubtypeEnum.AlertIdle and <= DialogTopic.SubtypeEnum.DetectFriendDie ? DialogTopic.CategoryEnum.Detection : DialogTopic.CategoryEnum.Misc;
        return (value, name, category);
    }
    private static void RequireId(string id) { if (!EditorId().IsMatch(id)) Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "Invalid EditorID: " + id); }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex EditorId();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex LineId();
    private static void Fail(string code, string message) => BethesdaSkyrimVanillaDialogueAuthority.Fail(code, message);
}
