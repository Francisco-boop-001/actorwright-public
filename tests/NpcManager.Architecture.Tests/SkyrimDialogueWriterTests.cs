using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimDialogueWriterTests
{
    internal static async Task RunAsync()
    {
        var type = typeof(LocalOperationJournal).Assembly.GetType("NpcManager.Infrastructure.SkyrimNpcDialogueService");
        if (type is null || !typeof(ISkyrimNpcDialogueService).IsAssignableFrom(type))
            throw new InvalidOperationException("Dialogue transaction must expose the production ISkyrimNpcDialogueService implementation.");
        await SkyrimDialogueServiceTests.RunAsync();
        var fixture = new SkyrimDialogueFixtures(extraRecords: true);
        var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new SkyrimDialogueServiceTests.Journal());
        var plan = await service.AnalyzeAsync(fixture.Request(), default);
        SkyrimDialogueServiceTests.Check(plan.Succeeded, "existing source records admitted");
        var applied = await service.ApplyAsync(new(fixture.PathOf("proposal.json"), plan.DocumentSha256!.Value, fixture.PathOf("synthesis.json"),
            fixture.Save("synthesis.json", fixture.Synthesis()), fixture.PathOf("output"), null), null, default);
        SkyrimDialogueServiceTests.Check(applied.Succeeded, "existing top-level VTYP/QUST groups merged: " + SkyrimDialogueServiceTests.Details(applied.Diagnostics));
        var before = NpcManager.Formats.Bethesda.BethesdaRawPluginInventory.Read(fixture.PathOf("Example.esp"));
        var after = NpcManager.Formats.Bethesda.BethesdaRawPluginInventory.Read(fixture.PathOf("output/Example.esp"));
        foreach (var record in before.Where(x => x.Signature != "TES4" && x.RawFormId != 0x01000800))
            SkyrimDialogueServiceTests.Check(after.Single(x => x.Signature == record.Signature && x.RawFormId == record.RawFormId).Sha256 == record.Sha256,
                "unrelated source major record remains byte-identical");
        var verified = await service.VerifyAsync(new(applied.DocumentPath!.Value, applied.DocumentSha256!.Value), default);
        SkyrimDialogueServiceTests.Check(verified.Succeeded, "existing voice and quest package verifies");
        Console.WriteLine("PASS dialogue writer existing voice replacement, group merge and unrelated record byte preservation");
    }

}
