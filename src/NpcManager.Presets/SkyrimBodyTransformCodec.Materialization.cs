using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class SkyrimBodyTransformCodec
{
    internal static ImmutableArray<SkyrimNodeTransform> MaterializeTransforms(
        ImmutableArray<SkyrimNodeTransformPatch> patches) =>
        patches.Select(patch => new SkyrimNodeTransform(
            patch.Node,
            patch.FirstPerson,
            [new RaceMenuTransformKeySet(patch.KeyName, BuildTransformValues(patch))],
            patch.Scale,
            patch.ScaleMode,
            patch.Position,
            patch.RotationMatrix)).ToImmutableArray();

    internal static ImmutableArray<SkyrimSkinOverride> MaterializeSkinOverrides(
        ImmutableArray<SkyrimSkinOverridePatch> patches) =>
        patches.Select(patch => new SkyrimSkinOverride(
            patch.SlotMask,
            patch.FirstPerson,
            BuildSkinValues(patch),
            patch.Textures,
            patch.Tint,
            patch.Alpha)).ToImmutableArray();

    private static JsonObject BuildTransform(SkyrimNodeTransformPatch patch)
    {
        var values = new JsonArray(BuildTransformValues(patch).Select(Value).ToArray());
        return new JsonObject
        {
            ["firstPerson"] = patch.FirstPerson,
            ["node"] = patch.Node,
            ["keys"] = new JsonArray(new JsonObject { ["name"] = patch.KeyName, ["values"] = values })
        };
    }

    private static JsonObject BuildTransform(SkyrimNodeTransform transform)
    {
        var keys = new JsonArray(transform.KeySets.Select(keySet =>
            (JsonNode)new JsonObject
            {
                ["name"] = keySet.Name,
                ["values"] = new JsonArray(keySet.Values.Select(Value).ToArray())
            }).ToArray());
        return new JsonObject
        {
            ["firstPerson"] = transform.FirstPerson,
            ["node"] = transform.Node,
            ["keys"] = keys
        };
    }

    private static JsonObject BuildSkin(SkyrimSkinOverridePatch patch)
    {
        var values = new JsonArray(BuildSkinValues(patch).Select(Value).ToArray());
        return new JsonObject
        {
            ["firstPerson"] = patch.FirstPerson,
            ["slotMask"] = patch.SlotMask,
            ["values"] = values
        };
    }

    private static ImmutableArray<RaceMenuValue> BuildTransformValues(SkyrimNodeTransformPatch patch)
    {
        var values = ImmutableArray.CreateBuilder<RaceMenuValue>();
        if (patch.Scale is { } scale) values.Add(new RaceMenuValue(30, 4, 0, RaceMenuScalar.FromNumber(scale)));
        if (patch.Position.Length == 3)
            for (var index = 0; index < 3; index++) values.Add(new RaceMenuValue(31, 4, index,
                RaceMenuScalar.FromNumber(patch.Position[index])));
        if (patch.RotationMatrix.Length == 9)
            for (var index = 0; index < 9; index++) values.Add(new RaceMenuValue(32, 4, index,
                RaceMenuScalar.FromNumber(patch.RotationMatrix[index])));
        if (patch.ScaleMode is { } mode) values.Add(new RaceMenuValue(33, 3, 0, RaceMenuScalar.FromInteger(mode)));
        return values.ToImmutable();
    }

    private static ImmutableArray<RaceMenuValue> BuildSkinValues(SkyrimSkinOverridePatch patch)
    {
        var values = ImmutableArray.CreateBuilder<RaceMenuValue>();
        foreach (var texture in patch.Textures.OrderBy(item => item.Key))
            values.Add(new RaceMenuValue(9, 2, texture.Key, RaceMenuScalar.FromText(texture.Value)));
        if (patch.Tint.Length == 4)
        {
            var bytes = patch.Tint.Select(value => (uint)Math.Clamp(Math.Round(value * 255, MidpointRounding.ToEven), 0, 255)).ToArray();
            var bits = (bytes[3] << 24) | (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
            values.Add(new RaceMenuValue(7, 3, -1, RaceMenuScalar.FromInteger(unchecked((int)bits))));
        }
        if (patch.Alpha is { } alpha) values.Add(new RaceMenuValue(8, 4, -1, RaceMenuScalar.FromNumber(alpha)));
        return values.ToImmutable();
    }

    private static JsonObject BuildSkin(SkyrimSkinOverride skin)
    {
        var values = new JsonArray(skin.Values.Select(Value).ToArray());
        return new JsonObject
        {
            ["firstPerson"] = skin.FirstPerson,
            ["slotMask"] = skin.SlotMask,
            ["values"] = values
        };
    }

    private static JsonObject Value(RaceMenuValue value) => Value(value.Key, value.Type, value.Index, value.Data);

    private static JsonObject Value(int key, int type, int index, RaceMenuScalar scalar) =>
        new() { ["key"] = key, ["type"] = type, ["index"] = index, ["data"] = ScalarNode(scalar) };

    private static JsonNode? ScalarNode(RaceMenuScalar scalar) => scalar.Kind switch
    {
        RaceMenuScalarKind.SignedInteger => scalar.IntegerValue,
        RaceMenuScalarKind.FloatingPoint => scalar.NumberValue,
        RaceMenuScalarKind.Text => scalar.StringValue,
        RaceMenuScalarKind.Boolean => scalar.BooleanValue,
        _ => null
    };

}
