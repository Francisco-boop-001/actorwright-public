using System.Collections.Immutable;
using System.Drawing;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal readonly record struct SkyrimNativeTintRecordKey(string Plugin, uint FormId)
{
    public static SkyrimNativeTintRecordKey From(FormReference reference) =>
        new(reference.Plugin.Value.ToUpperInvariant(), reference.FormId.Value);

    public static SkyrimNativeTintRecordKey From(Mutagen.Bethesda.Plugins.FormKey key) =>
        new(key.ModKey.ToString().ToUpperInvariant(), key.ID);
}

internal sealed record SkyrimNativeTintNpc(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    NpcSex Sex,
    FormReference Race,
    ImmutableArray<SkyrimNativeTintAuthoredLayer> Layers,
    bool IsDeleted);

internal sealed record SkyrimNativeTintAuthoredLayer(
    ushort? Index,
    Color? Color,
    float? Coverage);

internal sealed record SkyrimNativeTintRace(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    ImmutableArray<SkyrimNativeTintRaceLayer> MaleLayers,
    ImmutableArray<SkyrimNativeTintRaceLayer> FemaleLayers,
    bool IsDeleted);

internal sealed record SkyrimNativeTintRaceLayer(
    ushort? Index,
    string? MaskPath,
    int? MaskType,
    FormReference? DefaultColor,
    ImmutableArray<SkyrimNativeTintPreset> Presets);

internal sealed record SkyrimNativeTintPreset(
    FormReference? Color,
    float? Coverage);

internal sealed record SkyrimNativeTintColor(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    Color Color,
    bool IsDeleted);

internal sealed record SkyrimNativeTintCatalog(
    ImmutableDictionary<SkyrimNativeTintRecordKey, SkyrimNativeTintNpc> Npcs,
    ImmutableDictionary<SkyrimNativeTintRecordKey, SkyrimNativeTintRace> Races,
    ImmutableDictionary<SkyrimNativeTintRecordKey, SkyrimNativeTintColor> Colors);
