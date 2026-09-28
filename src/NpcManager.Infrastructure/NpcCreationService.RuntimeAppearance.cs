using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcCreationService
{
    private const int MaximumRuntimeStringLength = 32_768;

    private static void ValidateRuntimeAppearance(
        NpcCreationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateRuntimeAppearance(
            request.RuntimeAppearance,
            request.Traits.Sex,
            diagnostics);
    }

    internal static void ValidateRuntimeAppearance(
        SkyrimNpcApplySseVmadPayload? payload,
        NpcSex sex,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (payload is null) return;

        if (payload.IsFemale != (sex == NpcSex.Female))
        {
            diagnostics.Add(Error("npc-create-runtime-sex-mismatch",
                "The SSE runtime payload sex must match the created NPC sex."));
        }
        if (payload.SchemaVersion is 0 or -1)
        {
            diagnostics.Add(Error("npc-create-runtime-schema-version",
                "The SSE runtime SchemaVersion must be nonzero and distinct from the script's -1 initial state."));
        }
        if (payload.Overlays is null || payload.SkinOverrides is null ||
            payload.NodeTransforms is null)
        {
            diagnostics.Add(Error("npc-create-runtime-payload-missing",
                "The SSE runtime payload must contain overlay, skin, and node array groups."));
            return;
        }

        ValidateRuntimeOverlayArrays(payload.Overlays, diagnostics);
        ValidateRuntimeSkinArrays(payload.SkinOverrides, diagnostics);
        ValidateRuntimeNodeArrays(payload.NodeTransforms, diagnostics);
    }

    private static void ValidateRuntimeOverlayArrays(
        SkyrimNpcApplySseOverlayArrays arrays,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var initialized = !arrays.Nodes.IsDefault && !arrays.Diffuse.IsDefault &&
            !arrays.Normal.IsDefault && !arrays.HasEmissiveColor.IsDefault &&
            !arrays.EmissiveColor.IsDefault && !arrays.HasEmissiveMultiple.IsDefault &&
            !arrays.EmissiveMultiple.IsDefault && !arrays.HasTint.IsDefault &&
            !arrays.Tint.IsDefault &&
            !arrays.HasAlpha.IsDefault && !arrays.Alpha.IsDefault;
        ValidateRuntimeParallelArrays("overlay", initialized, arrays.Nodes.Length,
            diagnostics, arrays.Diffuse.Length, arrays.Normal.Length,
            arrays.HasEmissiveColor.Length, arrays.EmissiveColor.Length,
            arrays.HasEmissiveMultiple.Length, arrays.EmissiveMultiple.Length,
            arrays.HasTint.Length,
            arrays.Tint.Length, arrays.HasAlpha.Length, arrays.Alpha.Length);
        ValidateRuntimeStrings("overlay", arrays.Nodes, diagnostics);
        ValidateRuntimeStrings("overlay", arrays.Diffuse, diagnostics);
        ValidateRuntimeStrings("overlay", arrays.Normal, diagnostics);
        ValidateRuntimeFloats("overlay emissive multiple", arrays.EmissiveMultiple, diagnostics);
        ValidateRuntimeFloats("overlay alpha", arrays.Alpha, diagnostics, requireUnitInterval: true);
    }

    private static void ValidateRuntimeSkinArrays(
        SkyrimNpcApplySseSkinArrays arrays,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var initialized = !arrays.Slots.IsDefault && !arrays.Diffuse.IsDefault &&
            !arrays.Normal.IsDefault && !arrays.HasTint.IsDefault && !arrays.Tint.IsDefault;
        ValidateRuntimeParallelArrays("skin", initialized, arrays.Slots.Length,
            diagnostics, arrays.Diffuse.Length, arrays.Normal.Length, arrays.HasTint.Length,
            arrays.Tint.Length);
        ValidateRuntimeStrings("skin", arrays.Diffuse, diagnostics);
        ValidateRuntimeStrings("skin", arrays.Normal, diagnostics);
    }

    private static void ValidateRuntimeNodeArrays(
        SkyrimNpcApplySseNodeArrays arrays,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var initialized = !arrays.Names.IsDefault && !arrays.HasScale.IsDefault &&
            !arrays.Scale.IsDefault && !arrays.HasPosition.IsDefault &&
            !arrays.PositionX.IsDefault && !arrays.PositionY.IsDefault &&
            !arrays.PositionZ.IsDefault && !arrays.HasRotation.IsDefault &&
            !arrays.RotationM0.IsDefault && !arrays.RotationM1.IsDefault &&
            !arrays.RotationM2.IsDefault && !arrays.RotationM3.IsDefault &&
            !arrays.RotationM4.IsDefault && !arrays.RotationM5.IsDefault &&
            !arrays.RotationM6.IsDefault && !arrays.RotationM7.IsDefault &&
            !arrays.RotationM8.IsDefault && !arrays.ScaleMode.IsDefault;
        ValidateRuntimeParallelArrays("node", initialized, arrays.Names.Length,
            diagnostics, arrays.HasScale.Length, arrays.Scale.Length,
            arrays.HasPosition.Length, arrays.PositionX.Length, arrays.PositionY.Length,
            arrays.PositionZ.Length, arrays.HasRotation.Length, arrays.RotationM0.Length,
            arrays.RotationM1.Length, arrays.RotationM2.Length, arrays.RotationM3.Length,
            arrays.RotationM4.Length, arrays.RotationM5.Length, arrays.RotationM6.Length,
            arrays.RotationM7.Length, arrays.RotationM8.Length, arrays.ScaleMode.Length);
        ValidateRuntimeStrings("node", arrays.Names, diagnostics);
        ValidateRuntimeFloats("node scale", arrays.Scale, diagnostics);
        ValidateRuntimeFloats("node position X", arrays.PositionX, diagnostics);
        ValidateRuntimeFloats("node position Y", arrays.PositionY, diagnostics);
        ValidateRuntimeFloats("node position Z", arrays.PositionZ, diagnostics);
        ValidateRuntimeFloats("node rotation M0", arrays.RotationM0, diagnostics);
        ValidateRuntimeFloats("node rotation M1", arrays.RotationM1, diagnostics);
        ValidateRuntimeFloats("node rotation M2", arrays.RotationM2, diagnostics);
        ValidateRuntimeFloats("node rotation M3", arrays.RotationM3, diagnostics);
        ValidateRuntimeFloats("node rotation M4", arrays.RotationM4, diagnostics);
        ValidateRuntimeFloats("node rotation M5", arrays.RotationM5, diagnostics);
        ValidateRuntimeFloats("node rotation M6", arrays.RotationM6, diagnostics);
        ValidateRuntimeFloats("node rotation M7", arrays.RotationM7, diagnostics);
        ValidateRuntimeFloats("node rotation M8", arrays.RotationM8, diagnostics);
        if (!arrays.ScaleMode.IsDefault && arrays.ScaleMode.Any(value => value is < -1 or > 3))
        {
            diagnostics.Add(Error("npc-create-runtime-node-scale-mode",
                "Every SSE runtime node scale mode must be between -1 and 3."));
        }
    }

    private static void ValidateRuntimeParallelArrays(
        string category,
        bool initialized,
        int expectedLength,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        params int[] otherLengths)
    {
        if (!initialized)
        {
            diagnostics.Add(Error($"npc-create-runtime-{category}-array-default",
                $"Every SSE runtime {category} array must be initialized."));
        }
        if (expectedLength is <= 0 or > SkyrimNpcApplySseContract.PapyrusArrayLimit)
        {
            diagnostics.Add(Error($"npc-create-runtime-{category}-array-count",
                $"Every SSE runtime {category} array must contain 1 through {SkyrimNpcApplySseContract.PapyrusArrayLimit} values."));
        }
        if (otherLengths.Any(length => length != expectedLength))
        {
            diagnostics.Add(Error($"npc-create-runtime-{category}-array-length-mismatch",
                $"Every SSE runtime {category} array must have the same length."));
        }
    }

    private static void ValidateRuntimeStrings(
        string category,
        ImmutableArray<string> values,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (values.IsDefault) return;
        if (values.Any(value => value is null || value.Length > MaximumRuntimeStringLength ||
                                value.Contains('\0') ||
                                Encoding.UTF8.GetByteCount(value) > ushort.MaxValue))
        {
            diagnostics.Add(Error($"npc-create-runtime-{category}-string-invalid",
                $"SSE runtime {category} strings cannot be null, contain NUL, exceed {MaximumRuntimeStringLength} characters, or exceed the VMAD UInt16 byte length."));
        }
    }

    private static void ValidateRuntimeFloats(
        string role,
        ImmutableArray<float> values,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool requireUnitInterval = false)
    {
        if (values.IsDefault) return;
        if (values.Any(value => !float.IsFinite(value) ||
                                requireUnitInterval && value is < 0f or > 1f))
        {
            diagnostics.Add(Error("npc-create-runtime-float-invalid",
                $"Every SSE runtime {role} value must be finite" +
                (requireUnitInterval ? " and between 0 and 1." : ".")));
        }
    }

    internal static bool RuntimeAppearancePayloadsEqual(
        SkyrimNpcApplySseVmadPayload? left,
        SkyrimNpcApplySseVmadPayload? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.IsFemale != right.IsFemale ||
            left.SchemaVersion != right.SchemaVersion || left.Overlays is null ||
            right.Overlays is null || left.SkinOverrides is null ||
            right.SkinOverrides is null || left.NodeTransforms is null ||
            right.NodeTransforms is null) return false;

        return OverlayArraysEqual(left.Overlays, right.Overlays) &&
            SkinArraysEqual(left.SkinOverrides, right.SkinOverrides) &&
            NodeArraysEqual(left.NodeTransforms, right.NodeTransforms);
    }

    private static bool OverlayArraysEqual(
        SkyrimNpcApplySseOverlayArrays left,
        SkyrimNpcApplySseOverlayArrays right) =>
        ArraysEqual(left.Nodes, right.Nodes) && ArraysEqual(left.Diffuse, right.Diffuse) &&
        ArraysEqual(left.Normal, right.Normal) &&
        ArraysEqual(left.HasEmissiveColor, right.HasEmissiveColor) &&
        ArraysEqual(left.EmissiveColor, right.EmissiveColor) &&
        ArraysEqual(left.HasEmissiveMultiple, right.HasEmissiveMultiple) &&
        FloatArraysEqual(left.EmissiveMultiple, right.EmissiveMultiple) &&
        ArraysEqual(left.HasTint, right.HasTint) &&
        ArraysEqual(left.Tint, right.Tint) && ArraysEqual(left.HasAlpha, right.HasAlpha) &&
        FloatArraysEqual(left.Alpha, right.Alpha);

    private static bool SkinArraysEqual(
        SkyrimNpcApplySseSkinArrays left,
        SkyrimNpcApplySseSkinArrays right) =>
        ArraysEqual(left.Slots, right.Slots) && ArraysEqual(left.Diffuse, right.Diffuse) &&
        ArraysEqual(left.Normal, right.Normal) && ArraysEqual(left.HasTint, right.HasTint) &&
        ArraysEqual(left.Tint, right.Tint);

    private static bool NodeArraysEqual(
        SkyrimNpcApplySseNodeArrays left,
        SkyrimNpcApplySseNodeArrays right) =>
        ArraysEqual(left.Names, right.Names) && ArraysEqual(left.HasScale, right.HasScale) &&
        FloatArraysEqual(left.Scale, right.Scale) &&
        ArraysEqual(left.HasPosition, right.HasPosition) &&
        FloatArraysEqual(left.PositionX, right.PositionX) &&
        FloatArraysEqual(left.PositionY, right.PositionY) &&
        FloatArraysEqual(left.PositionZ, right.PositionZ) &&
        ArraysEqual(left.HasRotation, right.HasRotation) &&
        FloatArraysEqual(left.RotationM0, right.RotationM0) &&
        FloatArraysEqual(left.RotationM1, right.RotationM1) &&
        FloatArraysEqual(left.RotationM2, right.RotationM2) &&
        FloatArraysEqual(left.RotationM3, right.RotationM3) &&
        FloatArraysEqual(left.RotationM4, right.RotationM4) &&
        FloatArraysEqual(left.RotationM5, right.RotationM5) &&
        FloatArraysEqual(left.RotationM6, right.RotationM6) &&
        FloatArraysEqual(left.RotationM7, right.RotationM7) &&
        FloatArraysEqual(left.RotationM8, right.RotationM8) &&
        ArraysEqual(left.ScaleMode, right.ScaleMode);

    private static bool FloatArraysEqual(
        ImmutableArray<float> left,
        ImmutableArray<float> right) =>
        left.IsDefault == right.IsDefault &&
        (left.IsDefault || left.Length == right.Length && left.Zip(right).All(pair =>
            BitConverter.SingleToInt32Bits(pair.First) ==
            BitConverter.SingleToInt32Bits(pair.Second)));
}
