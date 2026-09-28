using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimDialogueMasterClosureTests
{
    internal static async Task RunAsync()
    {
        var fixture = new SkyrimDialogueFixtures();
        Write("Update.esm", ["Skyrim.esm"], "TransitiveGlobal");
        Write("Provider.esp", ["Skyrim.esm", "Update.esm"]);
        var source = SkyrimMod.CreateFromBinary(fixture.PathOf("Example.esp").Value, SkyrimRelease.SkyrimSE);
        {
            source.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension("Provider.esp") });
            Save(source);
        }
        var service = new SkyrimNpcDialogueService(fixture.PathOf("."), journal: new SkyrimDialogueServiceTests.Journal());
        var request = fixture.Request() with { LoadOrder = Order("Unrelated.esp", "Skyrim.esm", "Update.esm", "Provider.esp", "Example.esp", "Other.esp") };
        var result = await service.AnalyzeAsync(request, default);
        Check(result.Succeeded, "transitive closure and unrelated load-order superset: " + string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Check(result.Document!.MasterOrder.SequenceEqual(["Skyrim.esm", "Provider.esp"]), "source direct master order retained");
        Check(result.Document.CopiedMasterSha256!.Count == 3 && result.Document.CopiedMasterSha256["Update.esm"] == fixture.Hash("Update.esm"), "transitive dependency hash bound");
        var authority = new BethesdaSkyrimVanillaDialogueAuthority(fixture.Manifest,
            File.ReadAllBytes(fixture.PathOf("Example.esp").Value),
            new Dictionary<string, byte[]>
            {
                ["Skyrim.esm"] = File.ReadAllBytes(fixture.PathOf("Skyrim.esm").Value),
                ["Update.esm"] = File.ReadAllBytes(fixture.PathOf("Update.esm").Value),
                ["Provider.esp"] = File.ReadAllBytes(fixture.PathOf("Provider.esp").Value)
            });
        Check(authority.Resolve<IGlobalGetter>("TransitiveGlobal").ModKey == ModKey.FromNameAndExtension("Update.esm"), "transitive copied records admitted to authority");
        var synthesis = fixture.Synthesis();
        var apply = new SkyrimDialogueApplyRequest(fixture.PathOf("proposal.json"), result.DocumentSha256!.Value, fixture.PathOf("synthesis.json"), fixture.Save("synthesis.json", synthesis), fixture.PathOf("closure-output"), null);
        var original = File.ReadAllBytes(fixture.PathOf("Update.esm").Value);
        Write("Update.esm", ["Skyrim.esm"], "Drift");
        var drift = await service.ApplyAsync(apply, null, default);
        Check(!drift.Succeeded && drift.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch), "transitive master drift refuses apply");
        File.WriteAllBytes(fixture.PathOf("Update.esm").Value, original);
        var applied = await service.ApplyAsync(apply, null, default);
        Check(applied.Succeeded, "closure apply: " + string.Join("; ", applied.Diagnostics.Select(x => x.Message)));
        var verified = await service.VerifyAsync(new(applied.DocumentPath!.Value, applied.DocumentSha256!.Value), default);
        Check(verified.Succeeded, "closure output verifies with original direct masters");
        using (var output = SkyrimMod.CreateFromBinaryOverlay(fixture.PathOf("closure-output/Example.esp").Value, SkyrimRelease.SkyrimSE))
            Check(output.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).SequenceEqual(["Skyrim.esm", "Provider.esp"]), "serialized direct master order retained");
        await Refuse(Order("Skyrim.esm", "Provider.esp", "Update.esm", "Example.esp"), "Provider.esp -> Update.esm", "order");
        await Refuse(Order("Skyrim.esm", "Provider.esp", "Example.esp"), "Provider.esp -> Update.esm", "unlisted");
        File.Delete(fixture.PathOf("Update.esm").Value);
        await Refuse(request.LoadOrder, "Provider.esp -> Update.esm", "missing");
        Write("Update.esm", ["Provider.esp"]);
        await Refuse(request.LoadOrder, "Update.esm -> Provider.esp", "cycle");
        Console.WriteLine("PASS dialogue recursive copied-master closure, order, missing edge, cycle, superset and transitive drift");

        async Task Refuse(ImmutableArray<PluginName> order, string edge, string name)
        {
            var refused = await service.AnalyzeAsync(request with { LoadOrder = order, Output = fixture.PathOf(name + ".json") }, default);
            Check(!refused.Succeeded && refused.Diagnostics.Any(x => x.Code == SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved && x.Message.Contains(edge, StringComparison.Ordinal)), "individual " + name + " edge diagnostic: " + string.Join("; ", refused.Diagnostics.Select(x => x.Message)));
        }
        void Write(string name, string[] masters, string? editor = null)
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            foreach (string master in masters) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension(master) });
            if (editor is not null) mod.Globals.Add(new GlobalFloat(new(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = editor });
            Save(mod);
        }
        void Save(SkyrimMod mod) => mod.WriteToBinary(fixture.PathOf(mod.ModKey.FileName.String).Value, new BinaryWriteParameters
        { MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
    }
    private static ImmutableArray<PluginName> Order(params string[] names) => names.Select(x => new PluginName(x)).ToImmutableArray();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
