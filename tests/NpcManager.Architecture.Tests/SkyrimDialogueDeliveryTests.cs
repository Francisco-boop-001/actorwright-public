using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using static NpcManager.Architecture.Tests.SkyrimDialogueServiceTests;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimDialogueDeliveryTests
{
    internal static async Task RunAsync()
    {
        Check(SkyrimDialogueAssetPlan.LookupStem("Q", "", 0xFE000806, 2) == "Q__00000806_2", "local ID and response number");
        byte[] qualified = WavSampleCodec.WritePcm16(WavSampleCodec.Resample(WavSampleCodec.Decode(SkyrimDialogueFixtures.Wave()).Samples, 24000, 44100), 44100);
        Check(SkyrimDialogueVoiceAssetWriter.PrepareDelivery(qualified).AsSpan().SequenceEqual(qualified), "qualified delivery passes through unchanged");
        // Expected names come from the game lookup rule, not the producer's helper.
        foreach (var (quest, topic, prefix) in new[] {
            ("DLWNDialogue", "DLWNFollow", "DLWNDialogue_DLWNFollow"),
            ("ABCDEFGHIJK", "ABCDEFGHIJKLMN", "ABCDEFGHIJK_ABCDEFGHIJKLMN"),
            ("ABCDEFGHIJK", "ABCDEFGHIJKLMNO", "ABCDEFGHIJ_ABCDEFGHIJKLMNO"),
            ("Short", "ABCDEFGHIJKLMNOPQRSTU", "Short_ABCDEFGHIJKLMNOPQRST") })
        {
            var fixture = new SkyrimDialogueFixtures();
            var manifest = fixture.Manifest with { QuestEditorId = quest, Lines = [fixture.Manifest.Lines[0] with { Topic = topic }] };
            var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new Journal());
            var plan = await service.AnalyzeAsync(fixture.Request(manifest), default);
            Check(plan.Succeeded, "delivery plan: " + Details(plan.Diagnostics));
            var asset = plan.Document!.Assets.Single();
            Check(asset.FileStem == $"{prefix}_{asset.InfoLocalFormId:X8}_1", "game lookup: " + asset.FileStem);
            var synthesis = fixture.Synthesis(manifest);
            var applied = await service.ApplyAsync(new(fixture.PathOf("proposal.json"), plan.DocumentSha256!.Value,
                fixture.PathOf("synthesis.json"), fixture.Save("synthesis.json", synthesis), fixture.PathOf("output"), null), null, default);
            Check(applied.Succeeded, "delivery apply: " + Details(applied.Diagnostics));
            var output = applied.Document!;
            var delivered = output.Assets.Single();
            byte[] bytes = File.ReadAllBytes(fixture.PathOf("output/" + delivered.Wav).Value);
            var audio = WavSampleCodec.Measure(WavSampleCodec.Decode(bytes));
            Check(audio.SampleRate == 44100 && audio.Channels == 1 && audio.BitsPerSample == 16 && audio.Format == "pcm", "final delivery format");
            Check(Math.Abs(audio.DurationSeconds - 1) <= 1d / 44100 && audio.RmsDbfs > -20, "duration and signal preserved");
            Check(fixture.Hash(synthesis.Lines[0].OutputFile!) == synthesis.Lines[0].OutputSha256, "source synthesis unchanged");
            var verified = await service.VerifyAsync(new(applied.DocumentPath!.Value, applied.DocumentSha256!.Value), default);
            Check(verified.Succeeded && verified.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.LipToolMissing), "verify retains missing-LIP warning");

            var authority = new BethesdaSkyrimVanillaDialogueAuthority(manifest, File.ReadAllBytes(fixture.PathOf("Example.esp").Value),
                new Dictionary<string, byte[]> { ["Skyrim.esm"] = File.ReadAllBytes(fixture.PathOf("Skyrim.esm").Value) });
            var forgedPlan = plan.Document with { Assets = [asset with { FileStem = "wrong_but_hash_bound" }] };
            var errors = BethesdaSkyrimDialogueVerifier.Verify(File.ReadAllBytes(fixture.PathOf("Example.esp").Value),
                File.ReadAllBytes(fixture.PathOf("output/Example.esp").Value), forgedPlan, authority);
            Check(errors.Any(x => x.Severity == DiagnosticSeverity.Error), "binary readback rejects a forged lookup plan");

            // Different well-formed delivery with a refreshed ledger must fail source binding.
            byte[] replacement = WavSampleCodec.WritePcm16(WavSampleCodec.Decode(bytes).Samples.Select(x => x * 0.5f).ToArray(), 44100);
            File.WriteAllBytes(fixture.PathOf("output/" + delivered.Wav).Value, replacement);
            var substituted = output with { Assets = [delivered with { WavSha256 = SkyrimNpcVoiceDocumentCodec.Hash(replacement) }] };
            var substitutedHash = fixture.Save("output/dialogue-output-manifest.json", substituted);
            var substituteResult = await service.VerifyAsync(new(applied.DocumentPath!.Value, substitutedHash), default);
            Check(!substituteResult.Succeeded && substituteResult.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding), "rehashed valid WAV still requires original synthesis binding");

            // Rebind the output ledger too: hash consistency alone must not admit 24 kHz delivery.
            File.WriteAllBytes(fixture.PathOf("output/" + delivered.Wav).Value, File.ReadAllBytes(fixture.PathOf(synthesis.Lines[0].OutputFile!).Value));
            output = output with { Assets = [delivered with { WavSha256 = synthesis.Lines[0].OutputSha256!.Value }] };
            var changedHash = fixture.Save("output/dialogue-output-manifest.json", output);
            var rejected = await service.VerifyAsync(new(applied.DocumentPath!.Value, changedHash), default);
            Check(!rejected.Succeeded, "rehashed synthesis intermediate is not delivery");
        }
        Console.WriteLine("PASS consumer lookup boundaries, binary-derived rejection, delivery format/source binding and missing LIP status");
    }
}
