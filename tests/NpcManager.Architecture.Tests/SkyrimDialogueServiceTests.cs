using System.Buffers.Binary;
using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimDialogueServiceTests
{
    internal static async Task RunAsync()
    {
        await SkyrimDialogueDeliveryTests.RunAsync();
        await SkyrimDialogueMasterClosureTests.RunAsync();
        var fixture = new SkyrimDialogueFixtures(); var journal = new Journal();
        var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: journal);
        var request = fixture.Request();
        var inherited = new SkyrimDialogueFixtures(existingInfo: true);
        await Refusal(new SkyrimNpcDialogueService(inherited.PathOf("."), journal: new Journal()).AnalyzeAsync(inherited.Request(), default), "dialogue-source-dialogue-unsupported");
        var analyzed = await service.AnalyzeAsync(request, default); Check(analyzed.Succeeded, "analyze: " + Details(analyzed.Diagnostics));
        var proposal = analyzed.Document!;
        Check(proposal.Records[0].Signature == "VTYP" && proposal.Records[0].LocalFormId == 0x801 && proposal.Records[1].Signature == "QUST" && proposal.Records[1].LocalFormId == 0x802, "deterministic VTYP/QUST allocations");
        Check(proposal.Records.Count(x => x.Signature == "DLBR") == 5 && proposal.Records.Count(x => x.Signature == "DIAL") == 12 && proposal.Records.Count(x => x.Signature == "INFO") == 12 && !proposal.Records.Any(x => x.Signature == "GLOB"), "actual record budget includes five player branches and no unused globals");
        Check(proposal.Budget == new SkyrimDialogueBudget(1, 31, 64, 2048, true), "exact budget");
        Check(proposal.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.CooldownAdvisory), "cooldown advisory");
        Check(proposal.Assets.All(x => x.FileStem == $"AWExampleD_{fixture.Manifest.Lines.Single(l => l.Id == x.LineId).Topic[..15]}_{x.InfoLocalFormId:X8}_1"), "eight-digit local INFO asset names");
        var synthesis = fixture.Synthesis(); var synthesisHash = fixture.Save("synthesis.json", synthesis);
        var apply = new SkyrimDialogueApplyRequest(fixture.PathOf("proposal.json"), analyzed.DocumentSha256!.Value, fixture.PathOf("synthesis.json"), synthesisHash, fixture.PathOf("output"), null);
        var applied = await service.ApplyAsync(apply, null, default); Check(applied.Succeeded, "apply: " + Details(applied.Diagnostics));
        Check(applied.Document!.LipTool == "none" && applied.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.LipToolMissing), "absent lip tooling is truthful warning");
        var verify = new SkyrimDialogueVerifyRequest(applied.DocumentPath!.Value, applied.DocumentSha256!.Value);
        var verified = await service.VerifyAsync(verify, default); Check(verified.Succeeded && verified.Document!.Verified && verified.Document.AudioCount == 12, "independent verify: " + Details(verified.Diagnostics));
        Check(journal.Records.Count == 3 && journal.Records.All(x => x.Outcome == "succeeded"), "stage journal entries");
        Check(journal.Records.All(x => !x.RequestDigest.Any(char.IsLower) && x.ArtifactHashes.All(hash => !hash.Any(char.IsLower))), "journal uses admitted uppercase hashes");

        string pluginPath = fixture.PathOf("output/Example.esp").Value;
        using (var output = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE))
        {
            Check(output.IsSmallMaster && output.VoiceTypes.Single().Flags == VoiceType.Flag.Female && output.Npcs.Single().Voice.FormKey == output.VoiceTypes.Single().FormKey, "typed NPC voice flags/link");
            var quest = output.Quests.Single();
            Check(quest.Flags == Quest.Flag.StartGameEnabled && quest.Priority == 60 && quest.Aliases.Count == 0 && quest.DialogConditions.Single().Data is IGetIsIDConditionDataGetter condition && condition.Object.Link.FormKey == new FormKey(SkyrimDialogueFixtures.Plugin, 0x800), "quest actor condition and no aliases");
            foreach (var topic in output.DialogTopics)
            {
                var line = fixture.Manifest.Lines.Single(x => x.Topic == topic.EditorID); var info = topic.Responses.Single();
                Check(topic.Quest.FormKey == quest.FormKey && info.Topic.FormKey == topic.FormKey && info.Responses.Single().Text.String == line.Text, "topic/INFO routing and response");
                Check(topic.Subtype == Enum.Parse<DialogTopic.SubtypeEnum>(line.Subtype), "subtype");
                if (line.Subtype == "Custom")
                {
                    var branch = output.DialogBranches.Single(x => x.FormKey == topic.Branch.FormKey);
                    Check(branch.Flags == DialogBranch.Flag.TopLevel && branch.Category == DialogBranch.CategoryType.Player && branch.Quest.FormKey == quest.FormKey && branch.StartingTopic.FormKey == topic.FormKey && info.Prompt!.String == line.Prompt, "player branch and prompt");
                    var vm = info.VirtualMachineAdapter!;
                    Check(vm.ScriptFragments!.OnEnd!.ScriptName == "ActorwrightFollowerDialogue" && vm.ScriptFragments.OnEnd.FragmentName == "Fragment_" + line.Action, "action fragment");
                    Check(vm.Scripts.Single().Properties.OfType<IScriptObjectPropertyGetter>().Single().Object.FormKey == new FormKey(SkyrimDialogueFixtures.Master, 0x1000), "vanilla follower quest property");
                }
                if (line.Id == "line-0") Check(info.Flags!.Flags.HasFlag(DialogResponses.Flag.SayOnce) && info.Conditions[0].Data is IIsInInteriorConditionDataGetter && ((IConditionFloatGetter)info.Conditions[0]).ComparisonValue == 0, "condition and once flag roundtrip");
            }
        }
        byte[] before = File.ReadAllBytes(fixture.PathOf("Example.esp").Value); byte[] after = File.ReadAllBytes(pluginPath);
        byte[] seq = File.ReadAllBytes(fixture.PathOf("output/Seq/Example.seq").Value);
        Check(seq.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(seq) == (0x01000000 | proposal.Records[1].LocalFormId), "SEQ raw quest FormID");
        var preservation = BethesdaSkyrimDialogueVerifier.Verify(before, after, proposal, new(fixture.Manifest, before, new Dictionary<string, byte[]> { ["Skyrim.esm"] = File.ReadAllBytes(fixture.PathOf("Skyrim.esm").Value) }));
        Check(preservation.IsEmpty, "raw preservation proof");
        Check(!await Succeeded(service.ApplyAsync(apply, null, default)), "existing output refused");
        string wavPath = Path.Combine(applied.Document.PackageRoot, applied.Document.Assets[0].Wav); byte[] wav = File.ReadAllBytes(wavPath); File.Delete(wavPath);
        await Refusal(service.VerifyAsync(verify, default), SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing); File.WriteAllBytes(wavPath, wav);
        File.WriteAllBytes(pluginPath, before); await Refusal(service.VerifyAsync(verify, default), SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing); File.WriteAllBytes(pluginPath, after);
        await Refusal(service.VerifyAsync(verify with { ManifestSha256 = SkyrimNpcVoiceDocumentCodec.Hash("wrong"u8) }, default), SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding);
        byte[] masterBytes = File.ReadAllBytes(fixture.PathOf("Skyrim.esm").Value); byte[] changedMaster = masterBytes.ToArray(); changedMaster[^1] ^= 1;
        File.WriteAllBytes(fixture.PathOf("Skyrim.esm").Value, changedMaster);
        await Refusal(service.ApplyAsync(apply with { OutputRoot = fixture.PathOf("master-drift-output") }, null, default), SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch);
        File.WriteAllBytes(fixture.PathOf("Skyrim.esm").Value, masterBytes);
        var stale = synthesis with { Lines = synthesis.Lines.SetItem(0, synthesis.Lines[0] with { Text = "Changed" }) };
        await Refusal(service.ApplyAsync(apply with { SynthesisSha256 = fixture.Save("synthesis.json", stale), OutputRoot = fixture.PathOf("stale-output") }, null, default), SkyrimNpcDialogueDiagnosticCodes.SynthesisIncomplete);
        fixture.Save("synthesis.json", synthesis);
        var resumedSynthesis = synthesis with { Lines = synthesis.Lines.Select(line => line with { Status = SkyrimVoiceLineStatus.Skipped }).ToImmutableArray() };
        var resumedApply = await service.ApplyAsync(apply with { SynthesisSha256 = fixture.Save("synthesis.json", resumedSynthesis), OutputRoot = fixture.PathOf("resumed-output") }, null, default);
        Check(resumedApply.Succeeded, "byte-valid reused synthesis remains applicable: " + Details(resumedApply.Diagnostics));
        var resumedVerify = await service.VerifyAsync(new(resumedApply.DocumentPath!.Value, resumedApply.DocumentSha256!.Value), default);
        Check(resumedVerify.Succeeded && resumedVerify.Document!.AudioCount == 12, "reused synthesis output independently verifies");
        fixture.Save("synthesis.json", synthesis);
        foreach (string shape in new[] { "null-row", "empty", "missing" })
        {
            var malformedSynthesis = System.Text.Json.Nodes.JsonNode.Parse(SkyrimNpcVoiceDocumentCodec.Serialize(synthesis))!.AsObject();
            if (shape == "null-row") malformedSynthesis["lines"]![0] = null;
            else if (shape == "empty") malformedSynthesis["lines"] = new System.Text.Json.Nodes.JsonArray();
            else malformedSynthesis.Remove("lines");
            byte[] malformedBytes = System.Text.Encoding.UTF8.GetBytes(malformedSynthesis.ToJsonString());
            File.WriteAllBytes(fixture.PathOf("synthesis.json").Value, malformedBytes);
            await Refusal(service.ApplyAsync(apply with { SynthesisSha256 = SkyrimNpcVoiceDocumentCodec.Hash(malformedBytes), OutputRoot = fixture.PathOf("malformed-" + shape) }, null, default), SkyrimNpcDialogueDiagnosticCodes.SynthesisIncomplete);
        }
        fixture.Save("synthesis.json", synthesis);
        var badCondition = fixture.Manifest with { Lines = fixture.Manifest.Lines.SetItem(0, fixture.Manifest.Lines[0] with { Conditions = [new("NotAFunction", null, null, "==", 1, false, "subject")] }) };
        await Refusal(service.AnalyzeAsync(fixture.Request(badCondition, "unknown.json"), default), SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown);
        var unresolved = fixture.Manifest with { Lines = fixture.Manifest.Lines.SetItem(0, fixture.Manifest.Lines[0] with { Conditions = [new("GetInFaction", "NotAFaction", null, "==", 1, false, "subject")] }) };
        await Refusal(service.AnalyzeAsync(fixture.Request(unresolved, "unresolved.json"), default), SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved);
        var duplicate = fixture.Manifest with { Lines = fixture.Manifest.Lines.Add(fixture.Manifest.Lines[0]) };
        await Refusal(service.AnalyzeAsync(fixture.Request(duplicate, "duplicate.json"), default), SkyrimNpcDialogueDiagnosticCodes.LineDuplicate);
        var tooMany = fixture.Manifest with { Lines = Enumerable.Range(0, 1985).Select(i => fixture.Manifest.Lines[0] with { Id = "many-" + i }).ToImmutableArray() };
        await Refusal(service.AnalyzeAsync(fixture.Request(tooMany, "budget.json"), default), SkyrimNpcDialogueDiagnosticCodes.BudgetExceeded);
        var nonLight = new SkyrimDialogueFixtures(light: false); var nonLightService = new SkyrimNpcDialogueService(nonLight.PathOf("."), journal: new Journal());
        var nonLightPlan = await nonLightService.AnalyzeAsync(nonLight.Request(), default); Check(nonLightPlan.Succeeded && !nonLightPlan.Document!.LightPlugin, "ordinary ESP retains light flag off");
        await FullTemplate();
        await EdgeCases();
        await SeqAuthority();
        Console.WriteLine("PASS dialogue service copied-master plan/apply/verify, typed routing, record/asset/hash tamper, budget and preservation");
    }
    private static async Task SeqAuthority()
    {
        var fixture = new SkyrimDialogueFixtures(extraRecords: true); var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new Journal());
        Directory.CreateDirectory(fixture.PathOf("source-package/Seq").Value); Directory.CreateDirectory(fixture.PathOf("Seq").Value);
        File.Copy(fixture.PathOf("Example.esp").Value, fixture.PathOf("source-package/Example.esp").Value);
        byte[] adjacent = [3, 8, 0, 1]; byte[] copied = [3, 8, 0, 1, 3, 8, 0, 1];
        File.WriteAllBytes(fixture.PathOf("source-package/Seq/Example.seq").Value, adjacent); File.WriteAllBytes(fixture.PathOf("Seq/Example.seq").Value, copied);
        var request = fixture.Request() with { Plugin = fixture.PathOf("source-package/Example.esp") };
        var plan = await service.AnalyzeAsync(request, default); Check(plan.Succeeded, "SEQ source plan");
        Check(plan.Document!.SourceSeqPath == fixture.PathOf("source-package/Seq/Example.seq").Value && plan.Document.SourceSeqSha256 == SkyrimNpcVoiceDocumentCodec.Hash(adjacent), "adjacent SEQ beats copied DataRoot and is hashed");
        var apply = new SkyrimDialogueApplyRequest(fixture.PathOf("proposal.json"), plan.DocumentSha256!.Value, fixture.PathOf("synthesis.json"), fixture.Save("synthesis.json", fixture.Synthesis()), fixture.PathOf("output"), null);
        File.WriteAllBytes(fixture.PathOf("source-package/Seq/Example.seq").Value, copied);
        await Refusal(service.ApplyAsync(apply, null, default), SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch);
        File.WriteAllBytes(fixture.PathOf("source-package/Seq/Example.seq").Value, adjacent);
        var applied = await service.ApplyAsync(apply, null, default); Check(applied.Succeeded, "SEQ preserving apply: " + Details(applied.Diagnostics));
        Check(File.ReadAllBytes(fixture.PathOf("output/Seq/Example.seq").Value).SequenceEqual(SkyrimDialogueVoiceAssetWriter.Seq(adjacent, 0x01000000 | plan.Document.Records.Single(x => x.Signature == "QUST").LocalFormId)), "existing SEQ retained and new quest appended");
        var absence = new SkyrimDialogueFixtures(); var absentService = new SkyrimNpcDialogueService(absence.PathOf("."), journal: new Journal());
        var absentPlan = await absentService.AnalyzeAsync(absence.Request(), default); Check(absentPlan.Succeeded && absentPlan.Document!.SourceSeqSha256 is null, "SEQ absence is recorded");
        Directory.CreateDirectory(absence.PathOf("Seq").Value); File.WriteAllBytes(absence.PathOf("Seq/Example.seq").Value, adjacent);
        var absentApply = new SkyrimDialogueApplyRequest(absence.PathOf("proposal.json"), absentPlan.DocumentSha256!.Value, absence.PathOf("synthesis.json"), absence.Save("synthesis.json", absence.Synthesis()), absence.PathOf("output"), null);
        await Refusal(absentService.ApplyAsync(absentApply, null, default), SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch);
    }
    private static async Task EdgeCases()
    {
        var fixture = new SkyrimDialogueFixtures(); var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new Journal());
        var request = fixture.Request();
        var superset = await service.AnalyzeAsync(request with { LoadOrder = [new("UnusedBefore.esp"), new("Skyrim.esm"), new("Example.esp"), new("UnusedAfter.esp")], Output = fixture.PathOf("superset.json") }, default);
        Check(superset.Succeeded, "normal load-order superset resolves only source masters: " + Details(superset.Diagnostics));
        var masterOnly = await service.AnalyzeAsync(request with { LoadOrder = [new("Skyrim.esm")], Output = fixture.PathOf("masters-only.json") }, default);
        Check(masterOnly.Succeeded, "source plugin is optional in declared load order");
        await Refusal(service.AnalyzeAsync(request with { LoadOrder = [new("Example.esp"), new("Skyrim.esm")], Output = fixture.PathOf("bad-order.json") }, default), SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved);
        File.Copy(request.Plugin.Value, fixture.PathOf("Example.esl").Value);
        await Refusal(service.AnalyzeAsync(request with { Plugin = fixture.PathOf("Example.esl"), Output = fixture.PathOf("esl.json") }, default), SkyrimNpcDialogueDiagnosticCodes.PluginTypeRefused);
        var empty = fixture.Manifest with { Lines = fixture.Manifest.Lines.SetItem(0, fixture.Manifest.Lines[0] with { Text = " " }) };
        await Refusal(service.AnalyzeAsync(fixture.Request(empty, "empty.json"), default), SkyrimNpcDialogueDiagnosticCodes.LineTextEmpty);
        request = fixture.Request();
        await Refusal(service.AnalyzeAsync(request with { PluginSha256 = SkyrimNpcVoiceDocumentCodec.Hash("wrong-source"u8) }, default), SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch);
        await Refusal(service.AnalyzeAsync(request with { Manifest = new WorkspacePath(@"F:\not-read\dialogue.json") }, default), "protected-root-refused");
        byte[] malformed = "{}"u8.ToArray(); File.WriteAllBytes(request.Manifest.Value, malformed);
        await Refusal(service.AnalyzeAsync(request with { ManifestSha256 = SkyrimNpcVoiceDocumentCodec.Hash(malformed) }, default), SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid);
        byte[] seq = [0x00, 0x08, 0x00, 0x01];
        Check(SkyrimDialogueVoiceAssetWriter.Seq(seq, 0x01000802).SequenceEqual(new byte[] { 0, 8, 0, 1, 2, 8, 0, 1 }) && SkyrimDialogueVoiceAssetWriter.Seq(seq, 0x01000800).SequenceEqual(seq), "SEQ append and idempotent existing quest");
    }
    private static async Task FullTemplate()
    {
        var fixture = new SkyrimDialogueFixtures();
        var service = new SkyrimNpcDialogueService(fixture.PathOf("."), SkyrimDialogueCoverageTemplates.All, new Journal());
        fixture.Save("profile.json", fixture.Manifest.Profile);
        var template = await service.CreateTemplateAsync(new("follower-core-v1", fixture.PathOf("profile.json"), fixture.Manifest.Npc, "en", fixture.PathOf("template.json")), default);
        Check(template.Succeeded && template.Document!.Lines.Length >= 78, "full original template is created");
        var manifest = template.Document!;
        var plan = await service.AnalyzeAsync(fixture.Request(manifest), default);
        Check(plan.Succeeded, "full template conditions/subtypes: " + Details(plan.Diagnostics));
        var synthesis = fixture.Synthesis(manifest);
        var apply = await service.ApplyAsync(new(fixture.PathOf("proposal.json"), plan.DocumentSha256!.Value, fixture.PathOf("synthesis.json"), fixture.Save("synthesis.json", synthesis), fixture.PathOf("output"), null), null, default);
        Check(apply.Succeeded, "full template apply: " + Details(apply.Diagnostics));
        var verify = await service.VerifyAsync(new(apply.DocumentPath!.Value, apply.DocumentSha256!.Value), default);
        Check(verify.Succeeded && verify.Document!.AudioCount == manifest.Lines.Length, "full template verify: " + Details(verify.Diagnostics));
        using var mod = SkyrimMod.CreateFromBinaryOverlay(fixture.PathOf("output/Example.esp").Value, SkyrimRelease.SkyrimSE);
        Check(mod.DialogBranches.Count == manifest.Lines.Where(x => x.Subtype == "Custom").Select(x => x.Topic).Distinct().Count(), "every full-template Custom topic has its player branch");
        Check(mod.DialogTopics.SelectMany(x => x.Responses).SelectMany(x => x.Conditions).Any(x => x.Data is ILocationHasKeywordConditionDataGetter), "native current-location keyword function serialized");
        Console.WriteLine($"PASS full follower-core-v1 template: {manifest.Lines.Length} lines, {mod.DialogTopics.Count} topics, {mod.DialogBranches.Count} player branches, {plan.Document!.Budget.PlannedRecords} allocated records");
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Dialogue assertion failed: " + message); }
    internal static string Details(IEnumerable<Diagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(x => x.Code + ": " + x.Message));
    internal static async Task Refusal<T>(ValueTask<SkyrimDialogueStageResult<T>> stage, string code) where T : class
    { var result = await stage; Check(!result.Succeeded && result.Diagnostics.Any(x => x.Code == code), "expected " + code + ": " + Details(result.Diagnostics)); }
    private static async Task<bool> Succeeded<T>(ValueTask<SkyrimDialogueStageResult<T>> stage) where T : class => (await stage).Succeeded;
    internal sealed class Journal : ILocalOperationJournal
    {
        internal List<OperationJournalRecord> Records { get; } = [];
        public ValueTask<OperationJournalAppendResult> AppendAsync(OperationJournalRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.FromResult(new OperationJournalAppendResult(true, null, null)); }
    }
}
