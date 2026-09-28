using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

internal static class SkyrimNpcVisualMaterialRouting
{
    internal const uint SkinTintShaderType = 5;

    internal static ImmutableArray<NpcVisualMaterial>
        ApplySkinTextureSet(
            ImmutableArray<NpcVisualMaterial> materials,
            ImmutableArray<NpcVisualTextureSlot> overrides)
    {
        if (materials.IsDefaultOrEmpty ||
            overrides.IsDefaultOrEmpty)
            return materials;

        ImmutableHashSet<int> overriddenSlots =
            overrides.Select(item => item.Slot)
                .ToImmutableHashSet();
        return materials.Select(material =>
            material.ShaderType != SkinTintShaderType
                ? material
                : material with
                {
                    TextureSlots = material.TextureSlots
                        .Where(item =>
                            !overriddenSlots.Contains(item.Slot))
                        .Concat(overrides)
                        .OrderBy(item => item.Slot)
                        .ToImmutableArray()
                })
            .ToImmutableArray();
    }
}
