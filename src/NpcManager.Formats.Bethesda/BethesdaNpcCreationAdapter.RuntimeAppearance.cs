using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcCreationAdapter
{
    internal static void ApplyRuntimeAppearance(
        Npc npc,
        SkyrimNpcApplySseVmadPayload? payload,
        bool preserveUnrelatedScripts = false)
    {
        if (payload is null) return;

        var adapter = preserveUnrelatedScripts && npc.VirtualMachineAdapter is { } existing
            ? existing
            : new VirtualMachineAdapter
            {
                Version = 5,
                ObjectFormat = 2
            };
        if (adapter.Version != 5 || adapter.ObjectFormat != 2)
        {
            throw new InvalidDataException(
                "The existing NPC VMAD does not use the supported Skyrim version 5/object format 2 shape.");
        }
        for (var index = adapter.Scripts.Count - 1; index >= 0; index--)
        {
            if (string.Equals(adapter.Scripts[index].Name,
                    SkyrimNpcApplySseContract.ScriptName,
                    StringComparison.Ordinal))
            {
                adapter.Scripts.RemoveAt(index);
            }
        }
        var script = new ScriptEntry { Name = SkyrimNpcApplySseContract.ScriptName };
        var overlay = payload.Overlays;
        var skin = payload.SkinOverrides;
        var node = payload.NodeTransforms;

        script.Properties.Add(BoolProperty("IsFemale", payload.IsFemale));
        script.Properties.Add(IntProperty("SchemaVersion", payload.SchemaVersion));
        script.Properties.Add(StringArrayProperty("OvlNode", overlay.Nodes));
        script.Properties.Add(StringArrayProperty("OvlDiffuse", overlay.Diffuse));
        script.Properties.Add(StringArrayProperty("OvlNormal", overlay.Normal));
        script.Properties.Add(BoolArrayProperty("OvlHasEmissiveColor", overlay.HasEmissiveColor));
        script.Properties.Add(IntArrayProperty("OvlEmissiveColor", overlay.EmissiveColor));
        script.Properties.Add(BoolArrayProperty("OvlHasEmissiveMultiple", overlay.HasEmissiveMultiple));
        script.Properties.Add(FloatArrayProperty("OvlEmissiveMultiple", overlay.EmissiveMultiple));
        script.Properties.Add(BoolArrayProperty("OvlHasTint", overlay.HasTint));
        script.Properties.Add(IntArrayProperty("OvlTint", overlay.Tint));
        script.Properties.Add(BoolArrayProperty("OvlHasAlpha", overlay.HasAlpha));
        script.Properties.Add(FloatArrayProperty("OvlAlpha", overlay.Alpha));
        script.Properties.Add(IntArrayProperty("SkinSlot", skin.Slots));
        script.Properties.Add(StringArrayProperty("SkinDiffuse", skin.Diffuse));
        script.Properties.Add(StringArrayProperty("SkinNormal", skin.Normal));
        script.Properties.Add(BoolArrayProperty("SkinHasTint", skin.HasTint));
        script.Properties.Add(IntArrayProperty("SkinTint", skin.Tint));
        script.Properties.Add(StringArrayProperty("NodeName", node.Names));
        script.Properties.Add(BoolArrayProperty("NodeHasScale", node.HasScale));
        script.Properties.Add(FloatArrayProperty("NodeScale", node.Scale));
        script.Properties.Add(BoolArrayProperty("NodeHasPos", node.HasPosition));
        script.Properties.Add(FloatArrayProperty("NodePosX", node.PositionX));
        script.Properties.Add(FloatArrayProperty("NodePosY", node.PositionY));
        script.Properties.Add(FloatArrayProperty("NodePosZ", node.PositionZ));
        script.Properties.Add(BoolArrayProperty("NodeHasRot", node.HasRotation));
        script.Properties.Add(FloatArrayProperty("NodeRotM0", node.RotationM0));
        script.Properties.Add(FloatArrayProperty("NodeRotM1", node.RotationM1));
        script.Properties.Add(FloatArrayProperty("NodeRotM2", node.RotationM2));
        script.Properties.Add(FloatArrayProperty("NodeRotM3", node.RotationM3));
        script.Properties.Add(FloatArrayProperty("NodeRotM4", node.RotationM4));
        script.Properties.Add(FloatArrayProperty("NodeRotM5", node.RotationM5));
        script.Properties.Add(FloatArrayProperty("NodeRotM6", node.RotationM6));
        script.Properties.Add(FloatArrayProperty("NodeRotM7", node.RotationM7));
        script.Properties.Add(FloatArrayProperty("NodeRotM8", node.RotationM8));
        script.Properties.Add(IntArrayProperty("NodeScaleMode", node.ScaleMode));

        if (script.Properties.Count != SkyrimNpcApplySseContract.PropertyCount)
        {
            throw new InvalidDataException(
                "The NPC writer did not construct the complete pinned SSE VMAD property set.");
        }
        adapter.Scripts.Add(script);
        npc.VirtualMachineAdapter = adapter;
    }

    private static ScriptBoolProperty BoolProperty(string name, bool value) => new()
    {
        Name = name,
        Data = value,
        Flags = (ScriptProperty.Flag)1
    };

    private static ScriptIntProperty IntProperty(string name, int value) => new()
    {
        Name = name,
        Data = value,
        Flags = (ScriptProperty.Flag)1
    };

    private static ScriptStringListProperty StringArrayProperty(
        string name,
        ImmutableArray<string> values) => new()
        {
            Name = name,
            Data = ToExtendedList(values),
            Flags = (ScriptProperty.Flag)1
        };

    private static ScriptBoolListProperty BoolArrayProperty(
        string name,
        ImmutableArray<bool> values) => new()
        {
            Name = name,
            Data = ToExtendedList(values),
            Flags = (ScriptProperty.Flag)1
        };

    private static ScriptIntListProperty IntArrayProperty(
        string name,
        ImmutableArray<int> values) => new()
        {
            Name = name,
            Data = ToExtendedList(values),
            Flags = (ScriptProperty.Flag)1
        };

    private static ScriptFloatListProperty FloatArrayProperty(
        string name,
        ImmutableArray<float> values) => new()
        {
            Name = name,
            Data = ToExtendedList(values),
            Flags = (ScriptProperty.Flag)1
        };

    private static ExtendedList<T> ToExtendedList<T>(ImmutableArray<T> values)
    {
        var result = new ExtendedList<T>();
        foreach (var value in values) result.Add(value);
        return result;
    }
}
