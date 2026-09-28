using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class BethesdaSkyrimSelectiveAppearanceNpcStateReader
    : ISkyrimSelectiveAppearanceNpcStateReader
{
    public NpcOutfitSnapshot ReadOutfits(
        WorkspacePath pluginPath,
        FormId npcFormId) =>
        BethesdaNpcMutationAdapter.Read(
            GameEdition.SkyrimSpecialEdition,
            pluginPath,
            npcFormId).Outfits ?? new NpcOutfitSnapshot(null, null);
}
