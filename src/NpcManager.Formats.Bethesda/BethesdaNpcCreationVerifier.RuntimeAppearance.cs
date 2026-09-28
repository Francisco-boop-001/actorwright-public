using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcCreationVerifier
{
    private const byte VmadBoolType = 5;
    private const byte VmadStringArrayType = 12;
    private const byte VmadIntArrayType = 13;
    private const byte VmadFloatArrayType = 14;
    private const byte VmadBoolArrayType = 15;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void VerifyTypedRuntimeAppearance(
        INpcGetter npc,
        SkyrimNpcApplySseVmadPayload? expected,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool permitUnrelatedScripts = false)
    {
        if (expected is null) return;
        try
        {
            var adapter = npc.VirtualMachineAdapter;
            var scripts = adapter?.Scripts
                .Where(item => string.Equals(item.Name,
                    SkyrimNpcApplySseContract.ScriptName,
                    StringComparison.Ordinal))
                .ToArray() ?? [];
            var script = scripts.Length == 1 ? scripts[0] : null;
            var matches = adapter is not null && adapter.Version == 5 &&
                adapter.ObjectFormat == 2 && script is not null &&
                (permitUnrelatedScripts || adapter.Scripts.Count == 1) &&
                script.Properties.Count == SkyrimNpcApplySseContract.PropertyCount &&
                TypedRuntimePropertiesMatch(script.Properties, expected);
            Check(matches, "npc-create-typed-runtime-vmad-mismatch",
                "Typed read-back of NPCM_Manolov_ApplySSE did not match the complete 36-property runtime payload.",
                diagnostics);
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                             ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error("npc-create-typed-runtime-vmad-mismatch", exception.Message));
        }
    }

    private static bool TypedRuntimePropertiesMatch(
        IReadOnlyList<IScriptPropertyGetter> actual,
        SkyrimNpcApplySseVmadPayload expected)
    {
        var overlay = expected.Overlays;
        var skin = expected.SkinOverrides;
        var node = expected.NodeTransforms;
        var index = 0;
        return Match(actual[index++], "IsFemale", expected.IsFemale) &&
            Match(actual[index++], "SchemaVersion", expected.SchemaVersion) &&
            Match(actual[index++], "OvlNode", overlay.Nodes) &&
            Match(actual[index++], "OvlDiffuse", overlay.Diffuse) &&
            Match(actual[index++], "OvlNormal", overlay.Normal) &&
            Match(actual[index++], "OvlHasEmissiveColor", overlay.HasEmissiveColor) &&
            Match(actual[index++], "OvlEmissiveColor", overlay.EmissiveColor) &&
            Match(actual[index++], "OvlHasEmissiveMultiple", overlay.HasEmissiveMultiple) &&
            Match(actual[index++], "OvlEmissiveMultiple", overlay.EmissiveMultiple) &&
            Match(actual[index++], "OvlHasTint", overlay.HasTint) &&
            Match(actual[index++], "OvlTint", overlay.Tint) &&
            Match(actual[index++], "OvlHasAlpha", overlay.HasAlpha) &&
            Match(actual[index++], "OvlAlpha", overlay.Alpha) &&
            Match(actual[index++], "SkinSlot", skin.Slots) &&
            Match(actual[index++], "SkinDiffuse", skin.Diffuse) &&
            Match(actual[index++], "SkinNormal", skin.Normal) &&
            Match(actual[index++], "SkinHasTint", skin.HasTint) &&
            Match(actual[index++], "SkinTint", skin.Tint) &&
            Match(actual[index++], "NodeName", node.Names) &&
            Match(actual[index++], "NodeHasScale", node.HasScale) &&
            Match(actual[index++], "NodeScale", node.Scale) &&
            Match(actual[index++], "NodeHasPos", node.HasPosition) &&
            Match(actual[index++], "NodePosX", node.PositionX) &&
            Match(actual[index++], "NodePosY", node.PositionY) &&
            Match(actual[index++], "NodePosZ", node.PositionZ) &&
            Match(actual[index++], "NodeHasRot", node.HasRotation) &&
            Match(actual[index++], "NodeRotM0", node.RotationM0) &&
            Match(actual[index++], "NodeRotM1", node.RotationM1) &&
            Match(actual[index++], "NodeRotM2", node.RotationM2) &&
            Match(actual[index++], "NodeRotM3", node.RotationM3) &&
            Match(actual[index++], "NodeRotM4", node.RotationM4) &&
            Match(actual[index++], "NodeRotM5", node.RotationM5) &&
            Match(actual[index++], "NodeRotM6", node.RotationM6) &&
            Match(actual[index++], "NodeRotM7", node.RotationM7) &&
            Match(actual[index++], "NodeRotM8", node.RotationM8) &&
            Match(actual[index], "NodeScaleMode", node.ScaleMode);
    }

    private static bool Match(IScriptPropertyGetter actual, string name, bool expected) =>
        actual is IScriptBoolPropertyGetter property && PropertyHeaderMatches(property, name) &&
        property.Data == expected;

    private static bool Match(IScriptPropertyGetter actual, string name, int expected) =>
        actual is IScriptIntPropertyGetter property && PropertyHeaderMatches(property, name) &&
        property.Data == expected;

    private static bool Match(
        IScriptPropertyGetter actual,
        string name,
        ImmutableArray<string> expected) =>
        actual is IScriptStringListPropertyGetter property &&
        PropertyHeaderMatches(property, name) && property.Data.SequenceEqual(expected);

    private static bool Match(
        IScriptPropertyGetter actual,
        string name,
        ImmutableArray<bool> expected) =>
        actual is IScriptBoolListPropertyGetter property &&
        PropertyHeaderMatches(property, name) && property.Data.SequenceEqual(expected);

    private static bool Match(
        IScriptPropertyGetter actual,
        string name,
        ImmutableArray<int> expected) =>
        actual is IScriptIntListPropertyGetter property &&
        PropertyHeaderMatches(property, name) && property.Data.SequenceEqual(expected);

    private static bool Match(
        IScriptPropertyGetter actual,
        string name,
        ImmutableArray<float> expected) =>
        actual is IScriptFloatListPropertyGetter property &&
        PropertyHeaderMatches(property, name) && property.Data.Count == expected.Length &&
        property.Data.Zip(expected).All(pair =>
            BitConverter.SingleToInt32Bits(pair.First) == BitConverter.SingleToInt32Bits(pair.Second));

    private static bool PropertyHeaderMatches(IScriptPropertyGetter property, string name) =>
        string.Equals(property.Name, name, StringComparison.Ordinal) &&
        (int)property.Flags == 1;

    private static void VerifyRawRuntimeAppearance(
        RawPluginSnapshot raw,
        SkyrimNpcApplySseVmadPayload? expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (expected is null) return;
        try
        {
            var npc = raw.Records.Single(item => item.Signature == "NPC_");
            byte[]? vmad = null;
            var count = 0;
            ReadSubrecords(npc.Body.Span, (signature, data) =>
            {
                if (signature != "VMAD") return;
                count++;
                vmad = data.ToArray();
            });
            if (count != 1 || vmad is null)
            {
                throw new InvalidDataException(
                    $"The raw NPC contains {count} VMAD subrecords; expected exactly one.");
            }
            VerifyRawRuntimePayload(vmad, expected);
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                             ArgumentException or InvalidOperationException or
                                             OverflowException or DecoderFallbackException)
        {
            diagnostics.Add(Error("npc-create-raw-runtime-vmad-mismatch", exception.Message));
        }
    }

    private static void VerifyRawRuntimePayload(
        ReadOnlySpan<byte> payload,
        SkyrimNpcApplySseVmadPayload expected)
    {
        var reader = new RawVmadReader(payload);
        RequireRaw(reader.ReadUInt16() == 5, "VMAD version is not 5.");
        RequireRaw(reader.ReadUInt16() == 2, "VMAD object format is not 2.");
        RequireRaw(reader.ReadUInt16() == 1, "VMAD does not contain exactly one script.");
        RequireRaw(string.Equals(reader.ReadString(), SkyrimNpcApplySseContract.ScriptName,
            StringComparison.Ordinal), "VMAD script name does not match the pinned SSE apply script.");
        RequireRaw(reader.ReadByte() == 0, "VMAD script status is not the required zero value.");
        RequireRaw(reader.ReadUInt16() == SkyrimNpcApplySseContract.PropertyCount,
            "VMAD does not contain exactly 36 properties.");

        var overlay = expected.Overlays;
        var skin = expected.SkinOverrides;
        var node = expected.NodeTransforms;
        ExpectBool(ref reader, "IsFemale", expected.IsFemale);
        ExpectInt(ref reader, "SchemaVersion", expected.SchemaVersion);
        ExpectStrings(ref reader, "OvlNode", overlay.Nodes);
        ExpectStrings(ref reader, "OvlDiffuse", overlay.Diffuse);
        ExpectStrings(ref reader, "OvlNormal", overlay.Normal);
        ExpectBools(ref reader, "OvlHasEmissiveColor", overlay.HasEmissiveColor);
        ExpectInts(ref reader, "OvlEmissiveColor", overlay.EmissiveColor);
        ExpectBools(ref reader, "OvlHasEmissiveMultiple", overlay.HasEmissiveMultiple);
        ExpectFloats(ref reader, "OvlEmissiveMultiple", overlay.EmissiveMultiple);
        ExpectBools(ref reader, "OvlHasTint", overlay.HasTint);
        ExpectInts(ref reader, "OvlTint", overlay.Tint);
        ExpectBools(ref reader, "OvlHasAlpha", overlay.HasAlpha);
        ExpectFloats(ref reader, "OvlAlpha", overlay.Alpha);
        ExpectInts(ref reader, "SkinSlot", skin.Slots);
        ExpectStrings(ref reader, "SkinDiffuse", skin.Diffuse);
        ExpectStrings(ref reader, "SkinNormal", skin.Normal);
        ExpectBools(ref reader, "SkinHasTint", skin.HasTint);
        ExpectInts(ref reader, "SkinTint", skin.Tint);
        ExpectStrings(ref reader, "NodeName", node.Names);
        ExpectBools(ref reader, "NodeHasScale", node.HasScale);
        ExpectFloats(ref reader, "NodeScale", node.Scale);
        ExpectBools(ref reader, "NodeHasPos", node.HasPosition);
        ExpectFloats(ref reader, "NodePosX", node.PositionX);
        ExpectFloats(ref reader, "NodePosY", node.PositionY);
        ExpectFloats(ref reader, "NodePosZ", node.PositionZ);
        ExpectBools(ref reader, "NodeHasRot", node.HasRotation);
        ExpectFloats(ref reader, "NodeRotM0", node.RotationM0);
        ExpectFloats(ref reader, "NodeRotM1", node.RotationM1);
        ExpectFloats(ref reader, "NodeRotM2", node.RotationM2);
        ExpectFloats(ref reader, "NodeRotM3", node.RotationM3);
        ExpectFloats(ref reader, "NodeRotM4", node.RotationM4);
        ExpectFloats(ref reader, "NodeRotM5", node.RotationM5);
        ExpectFloats(ref reader, "NodeRotM6", node.RotationM6);
        ExpectFloats(ref reader, "NodeRotM7", node.RotationM7);
        ExpectFloats(ref reader, "NodeRotM8", node.RotationM8);
        ExpectInts(ref reader, "NodeScaleMode", node.ScaleMode);
        reader.RequireComplete();
    }

    private static void ExpectBool(ref RawVmadReader reader, string name, bool expected)
    {
        ExpectPropertyHeader(ref reader, name, VmadBoolType);
        RequireRaw(reader.ReadByte() == (expected ? 1 : 0), $"VMAD {name} value differs.");
    }

    private static void ExpectInt(ref RawVmadReader reader, string name, int expected)
    {
        ExpectPropertyHeader(ref reader, name, 3);
        RequireRaw(reader.ReadInt32() == expected, $"VMAD {name} value differs.");
    }

    private static void ExpectStrings(
        ref RawVmadReader reader,
        string name,
        ImmutableArray<string> expected)
    {
        ExpectPropertyHeader(ref reader, name, VmadStringArrayType);
        RequireRaw(reader.ReadCount() == expected.Length, $"VMAD {name} length differs.");
        foreach (var value in expected)
        {
            RequireRaw(string.Equals(reader.ReadString(), value, StringComparison.Ordinal),
                $"VMAD {name} string value differs.");
        }
    }

    private static void ExpectBools(
        ref RawVmadReader reader,
        string name,
        ImmutableArray<bool> expected)
    {
        ExpectPropertyHeader(ref reader, name, VmadBoolArrayType);
        RequireRaw(reader.ReadCount() == expected.Length, $"VMAD {name} length differs.");
        foreach (var value in expected)
        {
            RequireRaw(reader.ReadByte() == (value ? 1 : 0), $"VMAD {name} value differs.");
        }
    }

    private static void ExpectInts(
        ref RawVmadReader reader,
        string name,
        ImmutableArray<int> expected)
    {
        ExpectPropertyHeader(ref reader, name, VmadIntArrayType);
        RequireRaw(reader.ReadCount() == expected.Length, $"VMAD {name} length differs.");
        foreach (var value in expected)
        {
            RequireRaw(reader.ReadInt32() == value, $"VMAD {name} value differs.");
        }
    }

    private static void ExpectFloats(
        ref RawVmadReader reader,
        string name,
        ImmutableArray<float> expected)
    {
        ExpectPropertyHeader(ref reader, name, VmadFloatArrayType);
        RequireRaw(reader.ReadCount() == expected.Length, $"VMAD {name} length differs.");
        foreach (var value in expected)
        {
            RequireRaw(BitConverter.SingleToInt32Bits(reader.ReadSingle()) ==
                       BitConverter.SingleToInt32Bits(value), $"VMAD {name} value differs.");
        }
    }

    private static void ExpectPropertyHeader(
        ref RawVmadReader reader,
        string expectedName,
        byte expectedType)
    {
        RequireRaw(string.Equals(reader.ReadString(), expectedName, StringComparison.Ordinal),
            $"VMAD property order or name differs at {expectedName}.");
        RequireRaw(reader.ReadByte() == expectedType,
            $"VMAD {expectedName} has the wrong property type.");
        RequireRaw(reader.ReadByte() == 1,
            $"VMAD {expectedName} has the wrong property flags.");
    }

    private static void RequireRaw(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private ref struct RawVmadReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> data = bytes;
        private int position;

        public byte ReadByte()
        {
            Require(sizeof(byte));
            return data[position++];
        }

        public ushort ReadUInt16()
        {
            Require(sizeof(ushort));
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            position += sizeof(ushort);
            return value;
        }

        public int ReadInt32()
        {
            Require(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
            position += sizeof(int);
            return value;
        }

        public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

        public int ReadCount()
        {
            Require(sizeof(uint));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            position += sizeof(uint);
            if (value > SkyrimNpcApplySseContract.PapyrusArrayLimit)
            {
                throw new InvalidDataException("A VMAD array exceeds Skyrim's 128-element limit.");
            }
            return checked((int)value);
        }

        public string ReadString()
        {
            var length = ReadUInt16();
            Require(length);
            var value = StrictUtf8.GetString(data.Slice(position, length));
            position += length;
            if (value.Contains('\0')) throw new InvalidDataException("A VMAD string contains NUL.");
            return value;
        }

        public void RequireComplete()
        {
            if (position != data.Length)
            {
                throw new InvalidDataException("The VMAD payload contains trailing bytes.");
            }
        }

        private void Require(int length)
        {
            if (length < 0 || position > data.Length - length)
            {
                throw new InvalidDataException("The VMAD payload is truncated.");
            }
        }
    }
}
