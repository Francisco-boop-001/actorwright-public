using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFaceGenBakeTargetDiscovery()
    {
        const string carrierPluginPath =
            @"K:\ExampleWorkspace\projects\Emi2FreshBuild\03-builds\feasibility-probes\ck-carrier-root\Data\EmiCarrierProbe.esp";
        PluginInspection carrierInspection = await new BethesdaPluginReader()
            .ReadAsync(new PluginReadRequest(GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(carrierPluginPath)), CancellationToken.None);
        NpcRecordMetadata carrierMetadata = carrierInspection.Records
            .Single(record => record.IsNpc).NpcMetadata ??
            throw new InvalidOperationException(
                "The real carrier NPC omitted typed metadata.");
        Assert(carrierMetadata.Race is not null &&
               !carrierMetadata.HeadParts.IsDefault &&
               carrierMetadata.HeadParts.Length > 0 &&
               carrierMetadata.HeadParts.All(reference =>
                   reference.FormId.Value is > 0 and <= 0x00FF_FFFF),
            "The real plugin reader flattened race/headpart FormKeys to ambiguous local IDs.");

        var pluginA = new PluginName("BaseFaces.esp");
        var pluginB = new PluginName("WinningFaces.esp");
        ImmutableArray<PluginName> order = [pluginA, pluginB];
        var inventory = new GameInventory(
            GameEdition.SkyrimSpecialEdition,
            order,
            [],
            [
                Npc(pluginB, pluginA, 0x00000801, "OverrideWinner",
                    [pluginA, pluginB]),
                Npc(pluginA, pluginA, 0x00000800, "BaseWinner", [pluginA]),
                Npc(pluginB, pluginB, 0x00000802, "Deleted", [pluginB],
                    isDeleted: true),
                new NpcRecordSummary(pluginB, new FormId(0x00000803),
                    "Unparseable", "Unparseable", "NPC_", false,
                    Metadata: null,
                    new NpcProvenance(NpcProvenanceKind.Base, pluginB,
                        [pluginB]),
                    NpcChangeState.Unchanged,
                    [],
                    pluginB)
            ],
            Assets: null,
            Diagnostics: []);
        var root = new WorkspacePath("K:\\ExampleWorkspace");
        var service = new FaceGenBakeTargetDiscoveryService(
            new FixedInventoryService(inventory),
            new KOnlyWorkspacePolicy(root,
                new WorkspacePath("F:\\ExampleGame")),
            root);
        var dataRoot = new WorkspacePath(AppContext.BaseDirectory);

        FaceGenBakeTargetDiscoveryResult all = await service.DiscoverAsync(
            new FaceGenBakeTargetDiscoveryRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                order), CancellationToken.None);
        Assert(all.Accepted, "A valid copied-Data winner set was refused.");
        Assert(all.Targets.Length == 2,
            "Deleted and unparseable NPCs were not excluded from the bake universe.");
        Assert(all.Targets[0].EditorId == "BaseWinner" &&
               all.Targets[1].EditorId == "OverrideWinner",
            "Bake targets did not retain explicit load-order then FormID ordering.");
        Assert(all.Targets[1].OriginatingPlugin == pluginA &&
               all.Targets[1].WinningPlugin == pluginB &&
               all.Targets[1].OverrideChain.SequenceEqual(order) &&
               all.Targets[1].Race == new FormReference(pluginA,
                   new FormId(0x00000019)) &&
               all.Targets[1].Weight == 50F &&
               all.Targets[1].HeadParts.SequenceEqual([
                   new FormReference(pluginA, new FormId(0x00000100))]),
            "Override identity was flattened to the winning provider.");
        Assert(all.Diagnostics.Any(item =>
                item.Code == "facegen-bake-target-unparseable" &&
                item.Severity == DiagnosticSeverity.Warning),
            "An unparseable winning NPC was silently omitted.");

        FaceGenBakeTargetDiscoveryResult filtered = await service.DiscoverAsync(
            new FaceGenBakeTargetDiscoveryRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                order,
                pluginB), CancellationToken.None);
        Assert(filtered.Accepted && filtered.Targets.Length == 1 &&
               filtered.Targets[0].EditorId == "OverrideWinner",
            "Winning-plugin filtering did not match upstream bake-all semantics.");
    }

    private static NpcRecordSummary Npc(
        PluginName winning,
        PluginName owner,
        uint formId,
        string editorId,
        ImmutableArray<PluginName> chain,
        bool isDeleted = false) =>
        new(
            winning,
            new FormId(formId),
            editorId,
            editorId,
            "NPC_",
            isDeleted,
            new NpcRecordMetadata(
                NpcSex.Female,
                new FormId(0x00000019),
                SkyrimWeight: 50,
                Fallout4ThinWeight: null,
                Fallout4MuscularWeight: null,
                Fallout4FatWeight: null,
                HasTemplate: false,
                TemplateFormId: null,
                IsTemplateSource: false,
                IsPlaced: true,
                IsInLeveledList: false,
                IsCharGenFacePreset: false,
                HeadPartFormIds: [new FormId(0x00000100)],
                Race: new FormReference(owner, new FormId(0x00000019)),
                HeadParts: [new FormReference(owner, new FormId(0x00000100))]),
            new NpcProvenance(chain.Length > 1
                ? NpcProvenanceKind.Override
                : NpcProvenanceKind.Base, owner, chain),
            chain.Length > 1 ? NpcChangeState.Changed : NpcChangeState.Unchanged,
            [NpcCategory.Unique],
            owner);

    private sealed class FixedInventoryService(GameInventory inventory) :
        IGameInventoryService
    {
        public ValueTask<GameInventory> ReadAsync(
            GameInventoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(inventory);
        }
    }
}
