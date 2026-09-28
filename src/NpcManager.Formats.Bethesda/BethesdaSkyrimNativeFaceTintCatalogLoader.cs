using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimNativeFaceTintCatalogLoader
{
    public static SkyrimNativeTintCatalog Load(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> pluginOrder,
        CancellationToken cancellationToken)
    {
        var npcs = ImmutableDictionary.CreateBuilder<SkyrimNativeTintRecordKey, SkyrimNativeTintNpc>();
        var races = ImmutableDictionary.CreateBuilder<SkyrimNativeTintRecordKey, SkyrimNativeTintRace>();
        var colors = ImmutableDictionary.CreateBuilder<SkyrimNativeTintRecordKey, SkyrimNativeTintColor>();

        foreach (var authority in pluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                authority.Path.Value, SkyrimRelease.SkyrimSE);
            if (!string.Equals(mod.ModKey.ToString(), authority.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Copied plugin '{authority.Path}' identifies itself as '{mod.ModKey}', not '{authority.Plugin}'.");
            }

            var provider = new SkyrimFaceRecordProvider(
                authority.Plugin, authority.Path, authority.ExpectedSha256);
            foreach (var record in mod.Npcs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FormKey raceKey = record.Race.FormKey;
                if (raceKey.IsNull)
                {
                    continue;
                }

                npcs[SkyrimNativeTintRecordKey.From(record.FormKey)] = new SkyrimNativeTintNpc(
                    ToReference(record.FormKey),
                    provider,
                    record.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
                        ? NpcSex.Female
                        : NpcSex.Male,
                    ToReference(raceKey),
                    record.TintLayers.Select(layer => new SkyrimNativeTintAuthoredLayer(
                        layer.Index,
                        layer.Color,
                        layer.InterpolationValue)).ToImmutableArray(),
                    record.IsDeleted);
            }

            foreach (var record in mod.Races)
            {
                cancellationToken.ThrowIfCancellationRequested();
                races[SkyrimNativeTintRecordKey.From(record.FormKey)] = new SkyrimNativeTintRace(
                    ToReference(record.FormKey),
                    provider,
                    ReadRaceLayers(record.HeadData?.Male),
                    ReadRaceLayers(record.HeadData?.Female),
                    record.IsDeleted);
            }

            foreach (var record in mod.Colors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                colors[SkyrimNativeTintRecordKey.From(record.FormKey)] = new SkyrimNativeTintColor(
                    ToReference(record.FormKey), provider, record.Color, record.IsDeleted);
            }
        }

        return new SkyrimNativeTintCatalog(
            npcs.ToImmutable(), races.ToImmutable(), colors.ToImmutable());
    }

    private static ImmutableArray<SkyrimNativeTintRaceLayer> ReadRaceLayers(
        IHeadDataGetter? headData)
    {
        if (headData is null)
        {
            return [];
        }

        return headData.TintMasks.Select(layer => new SkyrimNativeTintRaceLayer(
            layer.Index,
            layer.FileName?.GivenPath,
            layer.MaskType is { } maskType ? Convert.ToInt32(maskType) : null,
            ToReference(layer.PresetDefault.FormKeyNullable),
            layer.Presets.Select(preset => new SkyrimNativeTintPreset(
                ToReference(preset.Color.FormKeyNullable),
                preset.DefaultValue)).ToImmutableArray())).ToImmutableArray();
    }

    private static FormReference ToReference(FormKey key) =>
        new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static FormReference? ToReference(FormKey? key) =>
        key is { } formKey && !formKey.IsNull ? ToReference(formKey) : null;
}
