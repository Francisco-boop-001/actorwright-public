using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Presets;

internal static partial class LooksMenuPresetCodec
{
    private static PresetDocument EmptyDocument(GameEdition edition, NpcManager.Domain.Sha256Hash hash, ImmutableArray<Diagnostic> diagnostics) =>
        new(PresetFormat.LooksMenu, edition,
            new PresetAppearance(null, ImmutableArray<PresetHeadPart>.Empty, null, null,
                ImmutableDictionary<string, float>.Empty, ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty, ImmutableArray<float>.Empty, ImmutableArray<PresetTint>.Empty,
                ImmutableArray<PresetOverlay>.Empty, null, new PresenceBuilder().Build(), ImmutableArray<PresetUnknownField>.Empty), hash, diagnostics);

    private sealed class PresenceBuilder
    {
        internal bool Gender;
        internal bool HeadParts;
        internal bool HairColor;
        internal bool Weight;
        internal bool Morphs;
        internal bool BodyMorphs;
        internal bool Tints;
        internal bool Overlays;
        internal bool Skin;
        internal bool Fallout4BodyMorphs;
        internal bool ChargenFaceMorphs;
        internal bool FaceBoneRegions;
        internal bool FacialMorphIntensity;
        internal PresetFieldPresence Build() => new(Gender, HeadParts, HairColor, Weight, Morphs, BodyMorphs, Tints,
            Overlays, Skin, Fallout4BodyMorphs, ChargenFaceMorphs, FaceBoneRegions, FacialMorphIntensity);
    }
}
