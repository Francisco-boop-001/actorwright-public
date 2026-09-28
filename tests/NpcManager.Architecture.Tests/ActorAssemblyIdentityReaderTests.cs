using Actorwright.PublicFixtures;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyIdentityReader()
    {
        var root = ActorwrightWorkspace.ResolveRoot();
        var pluginPath = new WorkspacePath(Path.Combine(root.Value, "tests", "fixtures", "skyrim-plugin-topology", SyntheticSkyrimPluginTopology.RepairedFileName));
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath("F:\\ExampleGame"));
        var reader = new BethesdaActorAssemblyIdentityReader(policy, root);
        var result = await reader.ReadAsync(new ActorAssemblyIdentityReadRequest(
            pluginPath,
            new ActorAssemblyActorIdentity(new PluginName(SyntheticSkyrimPluginTopology.RepairedFileName), new FormId(SyntheticSkyrimPluginTopology.BaseNpcLocalId)),
            new ActorAssemblyPlacement(ActorAssemblyPlacementMode.PersistentReference, new FormId(SyntheticSkyrimPluginTopology.PlacedReferenceLocalId))), CancellationToken.None);
        Assert(result.BaseNpc.TypedRecord.Signature == "NPC_" && result.BaseNpc.RawRecord.Signature == "NPC_",
            $"The base NPC was not observed through both paths: typed={result.BaseNpc.TypedRecord.Status}/{result.BaseNpc.TypedRecord.Signature}, raw={result.BaseNpc.RawRecord.Status}/{result.BaseNpc.RawRecord.Signature}; {string.Join(" | ", result.Diagnostics.Select(item => item.Code + ":" + item.Message))}");
        var placed = result.PlacedReference ?? throw new InvalidOperationException($"The persistent reference evidence was omitted. {string.Join(" | ", result.Diagnostics.Select(item => item.Code + ":" + item.Message))}");
        Assert(placed.TypedRecord.Signature == "ACHR" && placed.RawRecord.Signature == "ACHR",
            $"The placed ACHR was not kept separate from the base NPC: typed={placed.TypedRecord.Status}/{placed.TypedRecord.Signature}, raw={placed.RawRecord.Status}/{placed.RawRecord.Signature}, typedBase={placed.TypedBase.Status}/{placed.TypedBase.Plugin}/{placed.TypedBase.FormId}, rawBase={placed.RawNameBase.Status}/{placed.RawNameBase.Plugin}/{placed.RawNameBase.FormId}; {string.Join(" | ", result.Diagnostics.Select(item => item.Code + ":" + item.Message))}");
        Assert(placed.TypedBase.FormId?.Value == 0x800 && placed.RawNameBase.FormId?.Value == 0x800,
            "The placed ACHR base did not resolve to the declared NPC.");
    }
}
