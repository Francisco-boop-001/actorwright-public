using System.Buffers.Binary;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimDialogueLipSyncTests
{
    private static void CheckVoiceExclusions()
    {
        foreach (string name in new[] { "ExampleExchange", "ExampleMigrationArchive" })
        {
            string excluded = Path.Combine(@"K:\", name);
            foreach (string value in new[] { excluded, Path.Combine(excluded, "tools"), excluded.ToLowerInvariant() })
                SkyrimDialogueServiceTests.Check(ActorwrightWorkspace.IsVoiceExcludedPath(new(value)), "pure voice exclusion: " + value);
            SkyrimDialogueServiceTests.Check(!ActorwrightWorkspace.IsVoiceExcludedPath(new(excluded + "-lookalike")), "unrelated sibling remains independent");
        }
        foreach (string value in new[] { @"F:\", @"F:\unrelated\file.wav" })
            SkyrimDialogueServiceTests.Check(ActorwrightWorkspace.IsVoiceExcludedPath(new(value)), "whole protected drive remains excluded");
        foreach (string value in new[] { @"K:\ExampleWorkspace", @"k:\exampleworkspace\projects\offline\voice", @"K:\Actorwright\artifacts\voice", @"K:\xVAS\tools" })
            SkyrimDialogueServiceTests.Check(!ActorwrightWorkspace.IsVoiceExcludedPath(new(value)), "offline consumer, product artifacts and external tools are not excluded");
    }

    internal static int? RunTool(string[] args)
    {
        string? name = Path.GetFileName(Environment.ProcessPath);
        if (name is not ("FaceFXWrapper.exe" or "xWMAEncode.exe" or "fuz_extractor.exe")) return null;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "dialogue-fake-tool-fail"))) return 29;
        if (name == "FaceFXWrapper.exe" && args.Length == 7 && args[0] == "Skyrim" && args[1] == "USEnglish" && args[6].All(x => char.IsLetter(x) || char.IsWhiteSpace(x)))
        { File.Copy(args[3], args[4]); File.WriteAllBytes(args[5], "fixture-lip"u8.ToArray()); return 0; }
        if (name == "xWMAEncode.exe" && args is ["-b", "160000", _, var xwm]) { File.WriteAllBytes(xwm, "fixture-xwm"u8.ToArray()); return 0; }
        if (name == "fuz_extractor.exe" && args is ["-c", var fuz, var lip, var audio])
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "dialogue-fake-fuz-bad"))) { File.WriteAllText(fuz, "not a FUZ"); return 0; }
            byte[] lipBytes = File.ReadAllBytes(lip); byte[] header = new byte[12]; "FUZE"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)lipBytes.Length); File.WriteAllBytes(fuz, header.Concat(lipBytes).Concat(File.ReadAllBytes(audio)).ToArray()); return 0;
        }
        return 31;
    }

    internal static async Task RunAsync()
    {
        CheckVoiceExclusions();
        string bin = AppContext.BaseDirectory; string host = Path.Combine(bin, "NpcManager.Architecture.Tests.exe");
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(Environment.ProcessPath));
        string[] generated = ["FaceFXWrapper.exe", "xWMAEncode.exe", "fuz_extractor.exe", "FonixData.cdf"];
        foreach (string name in generated) SkyrimDialogueServiceTests.Check(!File.Exists(Path.Combine(bin, name)), "fake tool output is fresh");
        try
        {
            foreach (string name in generated[..3]) File.Copy(host, Path.Combine(bin, name));
            File.WriteAllText(Path.Combine(bin, "FonixData.cdf"), "fixture-cdf");
            var fixture = new SkyrimDialogueFixtures(); var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new SkyrimDialogueServiceTests.Journal());
            var plan = await service.AnalyzeAsync(fixture.Request(), default); SkyrimDialogueServiceTests.Check(plan.Succeeded, "lip plan");
            var apply = new SkyrimDialogueApplyRequest(fixture.PathOf("proposal.json"), plan.DocumentSha256!.Value, fixture.PathOf("synthesis.json"), fixture.Save("synthesis.json", fixture.Synthesis()), fixture.PathOf("lip-output"), new(bin));
            var result = await service.ApplyAsync(apply, null, default); SkyrimDialogueServiceTests.Check(result.Succeeded, "lip apply: " + SkyrimDialogueServiceTests.Details(result.Diagnostics));
            SkyrimDialogueServiceTests.Check(result.Document!.Assets.All(x => x.Lip is not null && x.LipSha256 is not null && x.Fuz is not null && x.FuzSha256 is not null), "LIP/FUZ files are mapped and hashed");
            var verified = await service.VerifyAsync(new(result.DocumentPath!.Value, result.DocumentSha256!.Value), default);
            SkyrimDialogueServiceTests.Check(verified.Succeeded && verified.Document!.LipCount == 12, "lip package verifies");
            File.WriteAllText(Path.Combine(bin, "dialogue-fake-fuz-bad"), "bad");
            await SkyrimDialogueServiceTests.Refusal(service.ApplyAsync(apply with { OutputRoot = fixture.PathOf("bad-fuz") }, null, default), SkyrimNpcDialogueDiagnosticCodes.LipToolFailed);
            File.Delete(Path.Combine(bin, "dialogue-fake-fuz-bad"));
            File.WriteAllText(Path.Combine(bin, "dialogue-fake-tool-fail"), "fail");
            await SkyrimDialogueServiceTests.Refusal(service.ApplyAsync(apply with { OutputRoot = fixture.PathOf("tool-failure") }, null, default), SkyrimNpcDialogueDiagnosticCodes.LipToolFailed);
            SkyrimDialogueServiceTests.Check(!Directory.Exists(fixture.PathOf("tool-failure").Value), "failed tool never publishes final output");
            File.Delete(Path.Combine(bin, "dialogue-fake-tool-fail"));
            File.WriteAllText(Path.Combine(bin, "FaceFXWrapper.exe"), "invalid executable fixture");
            await SkyrimDialogueServiceTests.Refusal(service.ApplyAsync(apply with { OutputRoot = fixture.PathOf("tool-start-failure") }, null, default), SkyrimNpcDialogueDiagnosticCodes.LipToolFailed);
        }
        finally
        {
            foreach (string name in generated.Concat(["dialogue-fake-tool-fail", "dialogue-fake-fuz-bad"])) File.Delete(Path.Combine(bin, name));
        }
        Console.WriteLine("PASS dialogue admitted fake executable LIP/FUZ transaction and failed-tool refusal");
    }
}
