using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

public static class BethesdaSkyrimDialogueVerifier
{
    public static ImmutableArray<Diagnostic> Verify(byte[] source, byte[] output, SkyrimDialogueProposal proposal,
        BethesdaSkyrimVanillaDialogueAuthority authority)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        void Error(string code, string message) => diagnostics.Add(new(code, DiagnosticSeverity.Error, message));
        try
        {
            var before = BethesdaSkyrimNpcFinishCoreRaw.Read(source, proposal.Manifest.Npc.FormId.Value);
            var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(output);
            uint owner = (uint)proposal.MasterOrder.Length;
            var identities = census.NonTes4Records.Select(x => (x.Signature, x.RawFormId)).ToArray();
            if (identities.Distinct().Count() != identities.Length) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, "Duplicate output record identity.");
            foreach (var row in proposal.Records)
                if (census.NonTes4Records.Count(x => x.RawFormId == (owner << 24 | row.LocalFormId) && x.Signature == row.Signature) != 1)
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, $"Missing or duplicate {row.Signature} 0x{row.LocalFormId:X8}.");
            if (census.NonTes4Records.Any(x => x.RawFormId >> 24 != owner)) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyForeignOverride, "Output contains a foreign override.");
            if (census.NonTes4Records.Length != before.Records.Length - 1 + proposal.Records.Length)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, "Output record count differs from source plus planned allocations.");
            if (census.NonTes4Records.Any(x => (x.RawFormId & 0xFFFFFF) is < 0x800 or > 0xFFF) || census.NonTes4Records.Length + proposal.Budget.Headroom > 2048)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLightRange, "Output exceeds the conservative local FormID range or headroom.");
            foreach (var row in before.Records.Where(x => x.Signature != "TES4"))
            {
                var after = census.NonTes4Records.SingleOrDefault(x => x.Signature == row.Signature && x.RawFormId == row.RawFormId);
                if (after is null) { Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, "Original record missing."); continue; }
                bool equal = row == before.Target
                    ? BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(source, row).Where(x => x.Signature != "VTCK").SelectMany(x => x.Bytes)
                        .SequenceEqual(BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(output, after).Where(x => x.Signature != "VTCK").SelectMany(x => x.Bytes)) &&
                        row.Bytes.AsSpan(8, 16).SequenceEqual(after.Bytes.AsSpan(8, 16))
                    : row.Bytes.SequenceEqual(after.Bytes);
                if (!equal) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "An unrelated source byte changed: " + row.Signature);
            }
            using var stream = new MemoryStream(output, writable: false);
            using var mod = SkyrimMod.CreateFromBinaryOverlay(stream, SkyrimRelease.SkyrimSE, authority.NpcKey.ModKey);
            if (mod.IsSmallMaster != proposal.LightPlugin || !mod.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).SequenceEqual(proposal.MasterOrder))
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLightRange, "Header light flag or master order changed.");
            var afterHeader = BethesdaSkyrimNpcFinishCoreRaw.Read(output, proposal.Manifest.Npc.FormId.Value).Tes4;
            if (afterHeader.Flags != before.Tes4.Flags || afterHeader.RecordCount != census.NonTes4Records.Length + census.Groups.Length || afterHeader.NextFormId != proposal.NextFormId)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "TES4 flags, HEDR count, or allocation frontier changed.");
            if (!before.Tes4.Subrecords.Where(x => x.Signature != "HEDR").SelectMany(x => x.Bytes).SequenceEqual(afterHeader.Subrecords.Where(x => x.Signature != "HEDR").SelectMany(x => x.Bytes)))
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Unrelated TES4 subrecord bytes changed.");
            var allKeys = mod.EnumerateMajorRecords().Select(x => x.FormKey).ToHashSet();
            foreach (var link in mod.EnumerateMajorRecords().SelectMany(x => x.EnumerateFormLinks()))
                if (!link.FormKey.IsNull && !allKeys.Contains(link.FormKey) && !authority.Contains(link.FormKey))
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "Unresolved form link: " + link.FormKey);
            var vtyp = proposal.Records.Single(x => x.Signature == "VTYP");
            var voice = mod.VoiceTypes.SingleOrDefault(x => x.FormKey.ID == vtyp.LocalFormId);
            var npc = mod.Npcs.SingleOrDefault(x => x.FormKey == authority.NpcKey);
            if (voice is null || voice.EditorID != vtyp.EditorId || voice.Flags != (proposal.Manifest.Npc.Female ? VoiceType.Flag.Female : 0) || npc?.Voice.FormKey != voice.FormKey)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "NPC voice binding or VoiceType flags mismatch.");
            var questRow = proposal.Records.Single(x => x.Signature == "QUST");
            var quest = mod.Quests.SingleOrDefault(x => x.FormKey.ID == questRow.LocalFormId);
            if (quest is null || quest.EditorID != questRow.EditorId || quest.Flags != Quest.Flag.StartGameEnabled || quest.Priority != proposal.Manifest.QuestPriority || quest.Aliases.Count != 0 ||
                quest.VirtualMachineAdapter is not null || quest.DialogConditions.Count != 1 || quest.DialogConditions[0].Data is not IGetIsIDConditionDataGetter id || id.Object.Link.FormKey != authority.NpcKey)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "Quest actor binding, flags, priority, aliases or script state mismatch.");
            foreach (var group in proposal.Manifest.Lines.GroupBy(x => x.Topic))
            {
                var topicRow = proposal.Records.Single(x => x.Signature == "DIAL" && x.EditorId == group.Key);
                var topic = mod.DialogTopics.SingleOrDefault(x => x.FormKey.ID == topicRow.LocalFormId);
                var (subtype, name, category) = BethesdaSkyrimDialogueScratchWriter.Subtype(group.First().Subtype);
                if (topic is null) continue;
                if (topic.EditorID != group.Key || topic.Subtype != subtype || topic.SubtypeName != new RecordType(name) || topic.Category != category || topic.Quest.FormKey != quest?.FormKey || topic.Priority != group.First().Priority)
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "Topic routing mismatch: " + group.Key);
                if (subtype == DialogTopic.SubtypeEnum.Custom)
                {
                    var branch = mod.DialogBranches.SingleOrDefault(x => x.FormKey == topic.Branch.FormKey);
                    if (branch is null || branch.Quest.FormKey != quest?.FormKey || branch.StartingTopic.FormKey != topic.FormKey || branch.Category != DialogBranch.CategoryType.Player || branch.Flags != DialogBranch.Flag.TopLevel)
                        Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "Custom topic requires a top-level player branch.");
                }
                foreach (var line in group)
                {
                    uint infoId = proposal.Records.Single(x => x.LineId == line.Id).LocalFormId;
                    var info = topic.Responses.SingleOrDefault(x => x.FormKey.ID == infoId);
                    if (info is null) { Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, "INFO is missing from its topic."); continue; }
                    if (info.Topic.FormKey != topic.FormKey || info.Flags?.Flags != BethesdaSkyrimDialogueScratchWriter.RepeatFlags(line) ||
                        info.Responses.Count != 1 || info.Responses[0].ResponseNumber != 1 || info.Responses[0].Text.String != line.Text ||
                        info.Responses[0].Emotion != Enum.Parse<Emotion>(line.Emotion) || info.Responses[0].EmotionValue != line.EmotionValue || info.Prompt?.String != line.Prompt)
                        Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "INFO response, prompt, flags or topic mismatch: " + line.Id);
                    // Resolve engine lookup from reopened records, not the generated asset ledger.
                    var assets = proposal.Assets.Where(x => x.LineId == line.Id).ToArray();
                    if (assets.Length != 1 || info.Responses.Count != 1 || quest?.EditorID is null || topic.EditorID is null || voice?.EditorID is null ||
                        assets[0].InfoLocalFormId != info.FormKey.ID || assets[0].VoiceTypeEditorId != voice.EditorID ||
                        assets[0].RelativeDirectory != $"sound/voice/{mod.ModKey.FileName.String}/{voice.EditorID}" ||
                        assets[0].FileStem != SkyrimDialogueAssetPlan.LookupStem(quest.EditorID, topic.EditorID, info.FormKey.ID, info.Responses[0].ResponseNumber))
                        Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Voice lookup differs from final records: " + line.Id);
                    if (line.Action != SkyrimDialogueAction.None)
                    {
                        var vm = info.VirtualMachineAdapter;
                        var property = vm?.Scripts.SingleOrDefault(x => x.Name == "ActorwrightFollowerDialogue")?.Properties.OfType<IScriptObjectPropertyGetter>().SingleOrDefault(x => x.Name == "DialogueFollower");
                        if (vm?.ScriptFragments?.FileName != "ActorwrightFollowerDialogue" || vm.ScriptFragments.OnEnd?.ScriptName != "ActorwrightFollowerDialogue" ||
                            vm.ScriptFragments.OnEnd.FragmentName != "Fragment_" + line.Action || property?.Object.FormKey != authority.Resolve<IQuestGetter>("DialogueFollower") || property.Alias != -1)
                            Error(SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved, "Follower fragment/property mismatch: " + line.Id);
                    }
                    else if (info.VirtualMachineAdapter is not null) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Unexpected INFO script.");
                }
            }
            // Compare the complete planned record payloads too, including all CTDA parameters and OR/run-on bits.
            byte[] expected = BethesdaSkyrimDialogueScratchWriter.Write(proposal, authority);
            foreach (var row in BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(expected).NonTes4Records.Where(x => x.Signature != "NPC_"))
            {
                var actual = census.NonTes4Records.SingleOrDefault(x => x.RawFormId == row.RawFormId && x.Signature == row.Signature);
                if (actual is not null && !actual.Bytes.SequenceEqual(row.Bytes)) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Planned record bytes differ: " + row.Signature);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or Mutagen.Bethesda.Plugins.Exceptions.RecordException)
        { Error(SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing, "Plugin readback failed: " + ex.Message); }
        return diagnostics.ToImmutable();
    }
}
