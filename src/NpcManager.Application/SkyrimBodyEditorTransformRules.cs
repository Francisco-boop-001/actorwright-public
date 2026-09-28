using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimBodyEditorDocumentRules
{
    public static SkyrimBodyEditorDocument UpsertTransform(
        SkyrimBodyEditorDocument document,
        string node,
        bool firstPerson,
        float? scale,
        int? scaleMode,
        ImmutableArray<float> position,
        ImmutableArray<float> rotationMatrix)
    {
        RequireValid(document);
        string normalized = node?.Trim() ?? string.Empty;
        if (!ValidName(normalized)) throw new ArgumentException("A valid node name is required.", nameof(node));
        ValidateTransformComponents(scale, scaleMode, position, rotationMatrix);
        if (scale is null && scaleMode is null && position.IsDefaultOrEmpty && rotationMatrix.IsDefaultOrEmpty)
            throw new ArgumentException("At least one transform component is required.", nameof(scale));

        int index = FindIndex(document.NodeTransforms, item =>
            item.FirstPerson == firstPerson &&
            string.Equals(item.Node, normalized, StringComparison.OrdinalIgnoreCase));
        SkyrimNodeTransform updated = index >= 0
            ? UpdateTransform(document.NodeTransforms[index], scale, scaleMode, position, rotationMatrix)
            : CreateTransform(normalized, firstPerson, scale, scaleMode, position, rotationMatrix);
        ImmutableArray<SkyrimNodeTransform> transforms = index >= 0
            ? document.NodeTransforms.SetItem(index, updated)
            : document.NodeTransforms.Length < MaximumTransforms
                ? document.NodeTransforms.Add(updated)
                : throw new InvalidOperationException("The node-transform row limit has been reached.");
        return document with { NodeTransforms = transforms };
    }

    public static SkyrimBodyEditorDocument RemoveTransform(
        SkyrimBodyEditorDocument document,
        string node,
        bool firstPerson)
    {
        RequireValid(document);
        string normalized = node?.Trim() ?? string.Empty;
        return document with
        {
            NodeTransforms = document.NodeTransforms
                .Where(item => item.FirstPerson != firstPerson ||
                               !string.Equals(item.Node, normalized, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray()
        };
    }

    private static SkyrimNodeTransform CreateTransform(
        string node,
        bool firstPerson,
        float? scale,
        int? scaleMode,
        ImmutableArray<float> position,
        ImmutableArray<float> rotation)
    {
        var values = BuildEditableTransformValues([], scale, scaleMode, position, rotation);
        return new SkyrimNodeTransform(node, firstPerson,
            [new RaceMenuTransformKeySet("RSMTransform", values)],
            scale, scaleMode, Initialized(position), Initialized(rotation));
    }

    private static SkyrimNodeTransform UpdateTransform(
        SkyrimNodeTransform current,
        float? scale,
        int? scaleMode,
        ImmutableArray<float> position,
        ImmutableArray<float> rotation)
    {
        int keyIndex = FindIndex(current.KeySets, item =>
            string.Equals(item.Name, "RSMTransform", StringComparison.OrdinalIgnoreCase));
        if (keyIndex < 0 && current.KeySets.Length == 1) keyIndex = 0;
        var keySets = current.KeySets.ToBuilder();
        if (keyIndex < 0)
        {
            keySets.Add(new RaceMenuTransformKeySet("RSMTransform",
                BuildEditableTransformValues([], scale, scaleMode, position, rotation)));
        }
        else
        {
            RaceMenuTransformKeySet keySet = keySets[keyIndex];
            keySets[keyIndex] = keySet with
            {
                Values = BuildEditableTransformValues(
                    keySet.Values, scale, scaleMode, position, rotation)
            };
        }
        return current with
        {
            KeySets = keySets.ToImmutable(),
            Scale = scale,
            ScaleMode = scaleMode,
            Position = Initialized(position),
            RotationMatrix = Initialized(rotation)
        };
    }

    private static ImmutableArray<RaceMenuValue> BuildEditableTransformValues(
        ImmutableArray<RaceMenuValue> source,
        float? scale,
        int? scaleMode,
        ImmutableArray<float> position,
        ImmutableArray<float> rotation)
    {
        int scaleIndex = source.FirstOrDefault(item => item.Key == 30)?.Index ?? 2;
        int modeIndex = source.FirstOrDefault(item => item.Key == 33)?.Index ?? 3;
        var result = source.Where(item => item.Key is not (30 or 31 or 32 or 33)).ToImmutableArray().ToBuilder();
        if (scale is { } scaleValue)
            result.Add(new RaceMenuValue(30, 4, scaleIndex, RaceMenuScalar.FromNumber(scaleValue)));
        if (position.Length == 3)
            for (int index = 0; index < 3; index++)
                result.Add(new RaceMenuValue(31, 4, index, RaceMenuScalar.FromNumber(position[index])));
        if (rotation.Length == 9)
            for (int index = 0; index < 9; index++)
                result.Add(new RaceMenuValue(32, 4, index, RaceMenuScalar.FromNumber(rotation[index])));
        if (scaleMode is { } mode)
            result.Add(new RaceMenuValue(33, 3, modeIndex, RaceMenuScalar.FromInteger(mode)));
        return result.ToImmutable();
    }

    private static void ValidateTransformComponents(
        float? scale,
        int? scaleMode,
        ImmutableArray<float> position,
        ImmutableArray<float> rotation)
    {
        if (scale is { } scaleValue && (!float.IsFinite(scaleValue) ||
                                        scaleValue is < MinimumTransformScale or > MaximumTransformScale))
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (scaleMode is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(scaleMode));
        if (position.IsDefault || position.Length is not (0 or 3) ||
            position.Any(value => !float.IsFinite(value) || Math.Abs(value) > MaximumTransformPosition))
            throw new ArgumentException("Position must be empty or three finite safe values.", nameof(position));
        if (rotation.IsDefault || rotation.Length is not (0 or 9) ||
            (!rotation.IsDefaultOrEmpty && !ValidRotationMatrix(rotation)))
            throw new ArgumentException("Rotation must be empty or a finite right-handed 3x3 rotation matrix.", nameof(rotation));
    }

    private static void ValidateTransforms(
        ImmutableArray<SkyrimNodeTransform> transforms,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (transforms.IsDefault || transforms.Length > MaximumTransforms)
        {
            diagnostics.Add(Error("body-editor-transform-shape",
                $"Node transforms must be initialized and contain at most {MaximumTransforms} rows."));
            return;
        }
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimNodeTransform? transform in transforms)
        {
            if (transform is null || !ValidName(transform.Node) ||
                !identities.Add($"{(transform.FirstPerson ? 1 : 0)}\0{transform.Node}") ||
                transform.KeySets.IsDefault || transform.KeySets.IsDefaultOrEmpty ||
                transform.Scale is < MinimumTransformScale or > MaximumTransformScale ||
                transform.Scale is { } scale && !float.IsFinite(scale) ||
                transform.ScaleMode is < 0 or > 3 ||
                transform.Position.IsDefault || transform.Position.Length is not (0 or 3) ||
                transform.Position.Any(value => !float.IsFinite(value) || Math.Abs(value) > MaximumTransformPosition) ||
                transform.RotationMatrix.IsDefault || transform.RotationMatrix.Length is not (0 or 9) ||
                (!transform.RotationMatrix.IsDefaultOrEmpty && !ValidRotationMatrix(transform.RotationMatrix)))
            {
                diagnostics.Add(Error("body-editor-transform-value", "A node-transform row is invalid."));
                continue;
            }
            foreach (RaceMenuTransformKeySet? keySet in transform.KeySets)
            {
                if (keySet is null || !ValidName(keySet.Name) || keySet.Values.IsDefault)
                {
                    diagnostics.Add(Error("body-editor-transform-key-set", "A transform key set is invalid."));
                    continue;
                }
                var values = new HashSet<(int Key, int Index)>();
                foreach (RaceMenuValue? value in keySet.Values)
                    if (value is null || value.Data is null ||
                        !values.Add((value.Key, value.Index)) || !ValidTransformValue(value))
                        diagnostics.Add(Error("body-editor-transform-key-value", "A transform value is invalid or duplicated."));
            }
        }
    }

    private static bool ValidTransformValue(RaceMenuValue value)
    {
        if (value.Key is 30 or 31 or 32)
        {
            int maximumIndex = value.Key switch { 31 => 2, 32 => 8, _ => 511 };
            return value.Type == 4 && value.Index is >= 0 && value.Index <= maximumIndex &&
                   value.Data.Kind == RaceMenuScalarKind.FloatingPoint &&
                   double.IsFinite(value.Data.NumberValue) &&
                   (value.Key != 30 || value.Data.NumberValue is >= MinimumTransformScale and <= MaximumTransformScale) &&
                   (value.Key != 31 || Math.Abs(value.Data.NumberValue) <= MaximumTransformPosition);
        }
        return value.Key == 33 && value.Type == 3 && value.Index is >= 0 and < 512 &&
               value.Data.Kind == RaceMenuScalarKind.SignedInteger &&
               value.Data.IntegerValue is >= 0 and <= 3;
    }

    private static bool TransformArrayEquivalent(
        ImmutableArray<SkyrimNodeTransform> left,
        ImmutableArray<SkyrimNodeTransform> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            SkyrimNodeTransform a = left[index];
            SkyrimNodeTransform b = right[index];
            if (!string.Equals(a.Node, b.Node, StringComparison.Ordinal) ||
                a.FirstPerson != b.FirstPerson || a.Scale != b.Scale || a.ScaleMode != b.ScaleMode ||
                !a.Position.SequenceEqual(b.Position) || !a.RotationMatrix.SequenceEqual(b.RotationMatrix) ||
                !TransformKeySetsEquivalent(a.KeySets, b.KeySets)) return false;
        }
        return true;
    }

    private static bool ValidRotationMatrix(ImmutableArray<float> values)
    {
        if (values.Length != 9 || values.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1.5F))
            return false;
        var r0 = (X: values[0], Y: values[1], Z: values[2]);
        var r1 = (X: values[3], Y: values[4], Z: values[5]);
        var r2 = (X: values[6], Y: values[7], Z: values[8]);
        static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        float determinant = r0.X * (r1.Y * r2.Z - r1.Z * r2.Y) -
                            r0.Y * (r1.X * r2.Z - r1.Z * r2.X) +
                            r0.Z * (r1.X * r2.Y - r1.Y * r2.X);
        return Math.Abs(Dot(r0, r0) - 1F) <= 0.05F &&
               Math.Abs(Dot(r1, r1) - 1F) <= 0.05F &&
               Math.Abs(Dot(r2, r2) - 1F) <= 0.05F &&
               Math.Abs(Dot(r0, r1)) <= 0.05F &&
               Math.Abs(Dot(r0, r2)) <= 0.05F &&
               Math.Abs(Dot(r1, r2)) <= 0.05F && determinant > 0.8F;
    }

    private static bool TransformKeySetsEquivalent(
        ImmutableArray<RaceMenuTransformKeySet> left,
        ImmutableArray<RaceMenuTransformKeySet> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal) ||
                !left[index].Values.SequenceEqual(right[index].Values)) return false;
        return true;
    }

    private static ImmutableArray<T> Initialized<T>(ImmutableArray<T> values) =>
        values.IsDefault ? ImmutableArray<T>.Empty : values;
}
