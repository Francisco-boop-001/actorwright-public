using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Pure serializer boundary from semantic NPC runtime rows to the complete
/// pinned NPCM_Manolov_ApplySSE VMAD property set.
/// </summary>
public static class SkyrimNpcApplySseVmadAdapter
{
    /// <summary>
    /// Converts semantic runtime rows into the complete pinned SSE array shape.
    /// All empty categories receive a one-element typed sentinel.
    /// </summary>
    public static SkyrimNpcApplySseVmadPayload ToVmadPayload(
        SkyrimNpcRuntimeAppearancePayload runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Require(runtime.CanEmit,
            "The RaceMenu runtime payload contains unsupported source rows and cannot be emitted.");
        Require(runtime.Overlays.Length <= SkyrimNpcApplySseContract.PapyrusArrayLimit &&
                runtime.SkinOverrides.Length <= SkyrimNpcApplySseContract.PapyrusArrayLimit &&
                runtime.NodeTransforms.Length <= SkyrimNpcApplySseContract.PapyrusArrayLimit,
            "A runtime category exceeds Skyrim Papyrus's 128-element array limit.");

        ValidateSemanticRows(runtime);
        var overlayArrays = new SkyrimNpcApplySseOverlayArrays(
            NonEmpty(runtime.Overlays.Select(item => item.Node), string.Empty),
            NonEmpty(runtime.Overlays.Select(item => item.Diffuse), string.Empty),
            NonEmpty(runtime.Overlays.Select(item => item.Normal), string.Empty),
            NonEmpty(runtime.Overlays.Select(item => item.HasEmissiveColor), false),
            NonEmpty(runtime.Overlays.Select(item => item.PackedEmissiveColorArgb), 0),
            NonEmpty(runtime.Overlays.Select(item => item.HasEmissiveMultiple), false),
            NonEmpty(runtime.Overlays.Select(item => item.EmissiveMultiple), 0F),
            NonEmpty(runtime.Overlays.Select(item => item.HasTint), false),
            NonEmpty(runtime.Overlays.Select(item => item.PackedTintArgb), 0),
            NonEmpty(runtime.Overlays.Select(item => item.HasAlpha), false),
            NonEmpty(runtime.Overlays.Select(item => item.Alpha), 0F));
        var skinArrays = new SkyrimNpcApplySseSkinArrays(
            NonEmpty(runtime.SkinOverrides.Select(item => item.SlotMaskBits), 0),
            NonEmpty(runtime.SkinOverrides.Select(item => item.Diffuse), string.Empty),
            NonEmpty(runtime.SkinOverrides.Select(item => item.Normal), string.Empty),
            NonEmpty(runtime.SkinOverrides.Select(item => item.HasTint), false),
            NonEmpty(runtime.SkinOverrides.Select(item => item.PackedTintArgb), 0));
        var nodeArrays = new SkyrimNpcApplySseNodeArrays(
            NonEmpty(runtime.NodeTransforms.Select(item => item.Node), string.Empty),
            NonEmpty(runtime.NodeTransforms.Select(item => item.HasScale), false),
            NonEmpty(runtime.NodeTransforms.Select(item => item.Scale), 0F),
            NonEmpty(runtime.NodeTransforms.Select(item => item.HasPosition), false),
            NonEmpty(runtime.NodeTransforms.Select(item => item.PositionX), 0F),
            NonEmpty(runtime.NodeTransforms.Select(item => item.PositionY), 0F),
            NonEmpty(runtime.NodeTransforms.Select(item => item.PositionZ), 0F),
            NonEmpty(runtime.NodeTransforms.Select(item => item.HasRotation), false),
            RotationColumn(runtime.NodeTransforms, 0),
            RotationColumn(runtime.NodeTransforms, 1),
            RotationColumn(runtime.NodeTransforms, 2),
            RotationColumn(runtime.NodeTransforms, 3),
            RotationColumn(runtime.NodeTransforms, 4),
            RotationColumn(runtime.NodeTransforms, 5),
            RotationColumn(runtime.NodeTransforms, 6),
            RotationColumn(runtime.NodeTransforms, 7),
            RotationColumn(runtime.NodeTransforms, 8),
            NonEmpty(runtime.NodeTransforms.Select(item => item.ScaleMode), 0));

        var schemaVersion = ComputeSchemaVersion(runtime.IsFemale, overlayArrays, skinArrays,
            nodeArrays);
        var payload = new SkyrimNpcApplySseVmadPayload(runtime.IsFemale, schemaVersion,
            overlayArrays, skinArrays, nodeArrays);
        ValidateVmadPayload(payload);
        return payload;
    }

    /// <summary>Adapts the strongly typed payload to all 36 pinned VMAD properties in script order.</summary>
    public static ImmutableArray<RuntimeScriptInputProperty> ToVmadProperties(
        SkyrimNpcApplySseVmadPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateVmadPayload(payload);
        var overlay = payload.Overlays;
        var skin = payload.SkinOverrides;
        var node = payload.NodeTransforms;
        ImmutableArray<RuntimeScriptInputProperty> properties =
        [
            Property("IsFemale", RuntimeScriptPropertyType.BoolValue, payload.IsFemale),
            Property("SchemaVersion", RuntimeScriptPropertyType.IntValue, payload.SchemaVersion),
            Property("OvlNode", RuntimeScriptPropertyType.StringArray, overlay.Nodes),
            Property("OvlDiffuse", RuntimeScriptPropertyType.StringArray, overlay.Diffuse),
            Property("OvlNormal", RuntimeScriptPropertyType.StringArray, overlay.Normal),
            Property("OvlHasEmissiveColor", RuntimeScriptPropertyType.BoolArray, overlay.HasEmissiveColor),
            Property("OvlEmissiveColor", RuntimeScriptPropertyType.IntArray, overlay.EmissiveColor),
            Property("OvlHasEmissiveMultiple", RuntimeScriptPropertyType.BoolArray, overlay.HasEmissiveMultiple),
            Property("OvlEmissiveMultiple", RuntimeScriptPropertyType.FloatArray, overlay.EmissiveMultiple),
            Property("OvlHasTint", RuntimeScriptPropertyType.BoolArray, overlay.HasTint),
            Property("OvlTint", RuntimeScriptPropertyType.IntArray, overlay.Tint),
            Property("OvlHasAlpha", RuntimeScriptPropertyType.BoolArray, overlay.HasAlpha),
            Property("OvlAlpha", RuntimeScriptPropertyType.FloatArray, overlay.Alpha),
            Property("SkinSlot", RuntimeScriptPropertyType.IntArray, skin.Slots),
            Property("SkinDiffuse", RuntimeScriptPropertyType.StringArray, skin.Diffuse),
            Property("SkinNormal", RuntimeScriptPropertyType.StringArray, skin.Normal),
            Property("SkinHasTint", RuntimeScriptPropertyType.BoolArray, skin.HasTint),
            Property("SkinTint", RuntimeScriptPropertyType.IntArray, skin.Tint),
            Property("NodeName", RuntimeScriptPropertyType.StringArray, node.Names),
            Property("NodeHasScale", RuntimeScriptPropertyType.BoolArray, node.HasScale),
            Property("NodeScale", RuntimeScriptPropertyType.FloatArray, node.Scale),
            Property("NodeHasPos", RuntimeScriptPropertyType.BoolArray, node.HasPosition),
            Property("NodePosX", RuntimeScriptPropertyType.FloatArray, node.PositionX),
            Property("NodePosY", RuntimeScriptPropertyType.FloatArray, node.PositionY),
            Property("NodePosZ", RuntimeScriptPropertyType.FloatArray, node.PositionZ),
            Property("NodeHasRot", RuntimeScriptPropertyType.BoolArray, node.HasRotation),
            Property("NodeRotM0", RuntimeScriptPropertyType.FloatArray, node.RotationM0),
            Property("NodeRotM1", RuntimeScriptPropertyType.FloatArray, node.RotationM1),
            Property("NodeRotM2", RuntimeScriptPropertyType.FloatArray, node.RotationM2),
            Property("NodeRotM3", RuntimeScriptPropertyType.FloatArray, node.RotationM3),
            Property("NodeRotM4", RuntimeScriptPropertyType.FloatArray, node.RotationM4),
            Property("NodeRotM5", RuntimeScriptPropertyType.FloatArray, node.RotationM5),
            Property("NodeRotM6", RuntimeScriptPropertyType.FloatArray, node.RotationM6),
            Property("NodeRotM7", RuntimeScriptPropertyType.FloatArray, node.RotationM7),
            Property("NodeRotM8", RuntimeScriptPropertyType.FloatArray, node.RotationM8),
            Property("NodeScaleMode", RuntimeScriptPropertyType.IntArray, node.ScaleMode)
        ];
        Require(properties.Length == SkyrimNpcApplySseContract.PropertyCount,
            "The SSE VMAD adapter did not produce the complete pinned property set.");
        return properties;
    }

    public static RuntimeScriptProposalRequest CreateProposalRequest(
        SkyrimNpcRuntimeAppearancePayload runtime,
        SkyrimNpcRuntimeProposalBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var payload = ToVmadPayload(runtime);
        return new RuntimeScriptProposalRequest(
            GameEdition.SkyrimSpecialEdition,
            binding.NpcFormId,
            SkyrimNpcApplySseContract.ScriptName,
            ToVmadProperties(payload),
            ImmutableArray<RuntimeScriptObjectReference>.Empty,
            ImmutableArray<RuntimeScriptFragment>.Empty,
            binding.Output,
            binding.SourcePlugin);
    }

    private static ImmutableArray<float> RotationColumn(
        ImmutableArray<SkyrimNpcRuntimeNodeTransform> rows,
        int column) =>
        NonEmpty(rows.Select(item => item.HasRotation ? item.RotationRowMajor[column] : 0F), 0F);

    private static ImmutableArray<T> NonEmpty<T>(IEnumerable<T> values, T sentinel)
    {
        var array = values.ToImmutableArray();
        return array.IsDefaultOrEmpty ? [sentinel] : array;
    }

    /// <summary>
    /// Produces a stable positive Int32 from the canonical typed property stream.
    /// The pinned upstream hash implementation is not present in the source
    /// snapshot, so the algorithm is version-tagged here rather than guessed.
    /// </summary>
    private static int ComputeSchemaVersion(
        bool isFemale,
        SkyrimNpcApplySseOverlayArrays overlay,
        SkyrimNpcApplySseSkinArrays skin,
        SkyrimNpcApplySseNodeArrays node)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NPCM_Manolov_ApplySSE/runtime-payload/v1");
            writer.Write(isFemale);
            WriteArray(writer, "OvlNode", overlay.Nodes);
            WriteArray(writer, "OvlDiffuse", overlay.Diffuse);
            WriteArray(writer, "OvlNormal", overlay.Normal);
            WriteArray(writer, "OvlHasEmissiveColor", overlay.HasEmissiveColor);
            WriteArray(writer, "OvlEmissiveColor", overlay.EmissiveColor);
            WriteArray(writer, "OvlHasEmissiveMultiple", overlay.HasEmissiveMultiple);
            WriteArray(writer, "OvlEmissiveMultiple", overlay.EmissiveMultiple);
            WriteArray(writer, "OvlHasTint", overlay.HasTint);
            WriteArray(writer, "OvlTint", overlay.Tint);
            WriteArray(writer, "OvlHasAlpha", overlay.HasAlpha);
            WriteArray(writer, "OvlAlpha", overlay.Alpha);
            WriteArray(writer, "SkinSlot", skin.Slots);
            WriteArray(writer, "SkinDiffuse", skin.Diffuse);
            WriteArray(writer, "SkinNormal", skin.Normal);
            WriteArray(writer, "SkinHasTint", skin.HasTint);
            WriteArray(writer, "SkinTint", skin.Tint);
            WriteArray(writer, "NodeName", node.Names);
            WriteArray(writer, "NodeHasScale", node.HasScale);
            WriteArray(writer, "NodeScale", node.Scale);
            WriteArray(writer, "NodeHasPos", node.HasPosition);
            WriteArray(writer, "NodePosX", node.PositionX);
            WriteArray(writer, "NodePosY", node.PositionY);
            WriteArray(writer, "NodePosZ", node.PositionZ);
            WriteArray(writer, "NodeHasRot", node.HasRotation);
            WriteArray(writer, "NodeRotM0", node.RotationM0);
            WriteArray(writer, "NodeRotM1", node.RotationM1);
            WriteArray(writer, "NodeRotM2", node.RotationM2);
            WriteArray(writer, "NodeRotM3", node.RotationM3);
            WriteArray(writer, "NodeRotM4", node.RotationM4);
            WriteArray(writer, "NodeRotM5", node.RotationM5);
            WriteArray(writer, "NodeRotM6", node.RotationM6);
            WriteArray(writer, "NodeRotM7", node.RotationM7);
            WriteArray(writer, "NodeRotM8", node.RotationM8);
            WriteArray(writer, "NodeScaleMode", node.ScaleMode);
        }
        var hash = SHA256.HashData(stream.ToArray());
        var version = BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
        return version == 0 ? 1 : version;
    }

    private static void WriteArray(BinaryWriter writer, string name,
        ImmutableArray<string> values)
    {
        writer.Write(name);
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static void WriteArray(BinaryWriter writer, string name,
        ImmutableArray<bool> values)
    {
        writer.Write(name);
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static void WriteArray(BinaryWriter writer, string name,
        ImmutableArray<int> values)
    {
        writer.Write(name);
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static void WriteArray(BinaryWriter writer, string name,
        ImmutableArray<float> values)
    {
        writer.Write(name);
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static RuntimeScriptInputProperty Property<T>(
        string name,
        RuntimeScriptPropertyType type,
        T value) =>
        new(name, type, JsonSerializer.SerializeToElement(value));

    private static void ValidateSemanticRows(SkyrimNpcRuntimeAppearancePayload runtime)
    {
        Require(!runtime.Overlays.IsDefault && !runtime.SkinOverrides.IsDefault &&
                !runtime.NodeTransforms.IsDefault && !runtime.Dispositions.IsDefault,
            "Runtime payload collections must be initialized.");
        foreach (var overlay in runtime.Overlays)
        {
            Require(!string.IsNullOrEmpty(overlay.Node) &&
                    !overlay.Node.StartsWith("Face", StringComparison.OrdinalIgnoreCase) &&
                    overlay.Diffuse is not null && overlay.Normal is not null &&
                    !overlay.Diffuse.Contains('\0') && !overlay.Normal.Contains('\0') &&
                    float.IsFinite(overlay.EmissiveMultiple) &&
                    float.IsFinite(overlay.Alpha) && overlay.Alpha is >= 0F and <= 1F,
                "A runtime overlay row is invalid or face-owned.");
        }
        foreach (var skin in runtime.SkinOverrides)
        {
            Require(skin.SlotMaskBits != 0 && skin.Diffuse is not null &&
                    skin.Normal is not null && !skin.Diffuse.Contains('\0') &&
                    !skin.Normal.Contains('\0'),
                "A runtime skin row has an invalid slot sentinel or texture value.");
        }
        foreach (var node in runtime.NodeTransforms)
        {
            Require(!string.IsNullOrEmpty(node.Node) && !node.Node.Contains('\0') &&
                    float.IsFinite(node.Scale) && float.IsFinite(node.PositionX) &&
                    float.IsFinite(node.PositionY) && float.IsFinite(node.PositionZ) &&
                    node.RotationRowMajor.Length == 9 &&
                    node.RotationRowMajor.All(float.IsFinite) &&
                    node.ScaleMode is >= -1 and <= 3,
                "A runtime node row is not a complete finite transform.");
        }
    }

    private static void ValidateVmadPayload(SkyrimNpcApplySseVmadPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload.Overlays);
        ArgumentNullException.ThrowIfNull(payload.SkinOverrides);
        ArgumentNullException.ThrowIfNull(payload.NodeTransforms);
        Require(payload.SchemaVersion is not (0 or -1),
            "SchemaVersion must be deterministic, nonzero, and distinct from the script's -1 initial state.");
        ValidateParallel(payload.Overlays.Nodes.Length,
            payload.Overlays.Diffuse.Length, payload.Overlays.Normal.Length,
            payload.Overlays.HasEmissiveColor.Length,
            payload.Overlays.EmissiveColor.Length,
            payload.Overlays.HasEmissiveMultiple.Length,
            payload.Overlays.EmissiveMultiple.Length,
            payload.Overlays.HasTint.Length, payload.Overlays.Tint.Length,
            payload.Overlays.HasAlpha.Length, payload.Overlays.Alpha.Length);
        ValidateParallel(payload.SkinOverrides.Slots.Length,
            payload.SkinOverrides.Diffuse.Length, payload.SkinOverrides.Normal.Length,
            payload.SkinOverrides.HasTint.Length, payload.SkinOverrides.Tint.Length);
        ValidateParallel(payload.NodeTransforms.Names.Length,
            payload.NodeTransforms.HasScale.Length, payload.NodeTransforms.Scale.Length,
            payload.NodeTransforms.HasPosition.Length, payload.NodeTransforms.PositionX.Length,
            payload.NodeTransforms.PositionY.Length, payload.NodeTransforms.PositionZ.Length,
            payload.NodeTransforms.HasRotation.Length, payload.NodeTransforms.RotationM0.Length,
            payload.NodeTransforms.RotationM1.Length, payload.NodeTransforms.RotationM2.Length,
            payload.NodeTransforms.RotationM3.Length, payload.NodeTransforms.RotationM4.Length,
            payload.NodeTransforms.RotationM5.Length, payload.NodeTransforms.RotationM6.Length,
            payload.NodeTransforms.RotationM7.Length, payload.NodeTransforms.RotationM8.Length,
            payload.NodeTransforms.ScaleMode.Length);
        Require(payload.Overlays.Nodes.All(ValidString) &&
                payload.Overlays.Diffuse.All(ValidString) &&
                payload.Overlays.Normal.All(ValidString) &&
                payload.SkinOverrides.Diffuse.All(ValidString) &&
                payload.SkinOverrides.Normal.All(ValidString) &&
                payload.NodeTransforms.Names.All(ValidString),
            "VMAD string arrays contain a null, NUL, or oversized value.");
        Require(AllFinite(payload.Overlays.EmissiveMultiple) &&
                AllFinite(payload.Overlays.Alpha) && AllFinite(payload.NodeTransforms.Scale) &&
                AllFinite(payload.NodeTransforms.PositionX) &&
                AllFinite(payload.NodeTransforms.PositionY) &&
                AllFinite(payload.NodeTransforms.PositionZ) &&
                AllFinite(payload.NodeTransforms.RotationM0) &&
                AllFinite(payload.NodeTransforms.RotationM1) &&
                AllFinite(payload.NodeTransforms.RotationM2) &&
                AllFinite(payload.NodeTransforms.RotationM3) &&
                AllFinite(payload.NodeTransforms.RotationM4) &&
                AllFinite(payload.NodeTransforms.RotationM5) &&
                AllFinite(payload.NodeTransforms.RotationM6) &&
                AllFinite(payload.NodeTransforms.RotationM7) &&
                AllFinite(payload.NodeTransforms.RotationM8),
            "VMAD float arrays must contain only finite values.");
    }

    private static bool ValidString(string? value) =>
        value is { Length: <= 32_768 } && !value.Contains('\0');

    private static bool AllFinite(ImmutableArray<float> values) =>
        values.All(float.IsFinite);

    private static void ValidateParallel(int expected, params int[] lengths)
    {
        Require(expected is > 0 and <= SkyrimNpcApplySseContract.PapyrusArrayLimit &&
                lengths.All(length => length == expected),
            "Every pinned VMAD array must be present, nonempty, within 128 elements, and parallel to its category.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
