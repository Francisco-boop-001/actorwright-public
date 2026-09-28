using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Body-skin selection carried by the NPC editor.
/// Fallout 4 stores the vanilla selection on NPC.WNAM (Mutagen's <c>Npc.Skin</c>).
/// LooksMenu template IDs are retained as an explicit request so the boundary can reject
/// them when no trusted template/provider catalog is available.</summary>
public sealed record NpcSkinPatch(OptionalFormReference Fallout4Skin, string? PresetSkinId = null)
{
    public bool IsEmpty => !Fallout4Skin.IsSpecified && string.IsNullOrWhiteSpace(PresetSkinId);
}
