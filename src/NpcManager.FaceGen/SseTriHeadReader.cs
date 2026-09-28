using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>Strict, allocation-bounded reader for the Skyrim FRTRI003 face-morph subset.</summary>
public sealed class SseTriHeadReader : ISseTriHeadReader
{
    private const int HeaderLength = 64;
    private const int MaxVertices = 1_000_000;
    private const int MaxTriangles = 4_000_000;
    private const int MaxMorphs = 65_535;
    private const int MaxModifierVertices = 16_000_000;
    private const int MaxMorphNameBytes = 4_096;

    private static readonly byte[] Magic = "FRTRI003"u8.ToArray();

    public SseTriHeadReadResult Read(SseTriHeadReadRequest request)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Bytes.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("sse-tri-bytes-empty", "The FRTRI003 source is empty."));
            return Refused(diagnostics);
        }

        Sha256Hash actualHash = new(Convert.ToHexString(SHA256.HashData(request.Bytes.AsSpan())));
        if (actualHash != request.ExpectedSourceSha256)
        {
            diagnostics.Add(Error("sse-tri-hash-mismatch",
                $"FRTRI003 source hash {actualHash} does not match {request.ExpectedSourceSha256}."));
            return Refused(diagnostics);
        }

        try
        {
            SseTriHeadDocument document = Parse(
                request.SourcePath, actualHash, request.Bytes.AsSpan(), diagnostics);
            return new SseTriHeadReadResult(true, document, diagnostics.ToImmutable());
        }
        catch (TriFormatException exception)
        {
            diagnostics.Add(Error(exception.Code, exception.Message));
            return Refused(diagnostics);
        }
        catch (OverflowException)
        {
            diagnostics.Add(Error("sse-tri-count-overflow",
                "FRTRI003 count arithmetic overflowed the supported address space."));
            return Refused(diagnostics);
        }
    }

    private static SseTriHeadDocument Parse(
        AssetPath sourcePath,
        Sha256Hash sourceSha256,
        ReadOnlySpan<byte> data,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (data.Length < HeaderLength)
        {
            throw Invalid("sse-tri-truncated", "FRTRI003 is shorter than its 64-byte header.");
        }

        Cursor cursor = new(data);
        if (!cursor.ReadBytes(Magic.Length, "format identifier").SequenceEqual(Magic))
        {
            throw Invalid("sse-tri-version", "Only the exact FRTRI003 format identifier is supported.");
        }

        int vertexCount = ReadBoundedCount(ref cursor, "vertex", MaxVertices, requirePositive: true);
        int triangleCount = ReadBoundedCount(ref cursor, "triangle", MaxTriangles);
        uint quadCount = cursor.ReadUInt32("quad count");
        uint unknown2 = cursor.ReadUInt32("unknown2");
        uint unknown3 = cursor.ReadUInt32("unknown3");
        int uvCount = ReadBoundedCount(ref cursor, "UV", MaxVertices);
        uint flags = cursor.ReadUInt32("flags");
        int morphCount = ReadBoundedCount(ref cursor, "morph", MaxMorphs);
        int modifierCount = ReadBoundedCount(ref cursor, "modifier", MaxMorphs);
        int modifierVertexCount = ReadBoundedCount(ref cursor, "modifier vertex", MaxModifierVertices);
        uint unknown7 = cursor.ReadUInt32("unknown7");
        uint unknown8 = cursor.ReadUInt32("unknown8");
        uint unknown9 = cursor.ReadUInt32("unknown9");
        uint unknown10 = cursor.ReadUInt32("unknown10");

        if (quadCount != 0 || unknown2 != 0 || unknown3 != 0 || unknown7 != 0 || unknown8 != 0 ||
            unknown9 != 0 || unknown10 != 0 || (flags & ~1U) != 0)
        {
            throw Invalid("sse-tri-unsupported-layout",
                "FRTRI003 uses unsupported quad, reserved-field, or flag values.");
        }

        long fixedSectionBytes = checked(
            (long)vertexCount * 3 * sizeof(float) +
            (long)modifierVertexCount * 3 * sizeof(float) +
            (long)triangleCount * 3 * sizeof(uint) +
            (long)uvCount * 2 * sizeof(float) +
            (long)triangleCount * 3 * sizeof(uint));
        if (fixedSectionBytes > cursor.Remaining)
        {
            throw Invalid("sse-tri-truncated",
                $"FRTRI003 declares {fixedSectionBytes} fixed-section bytes but only {cursor.Remaining} remain.");
        }

        ImmutableArray<Vector3>.Builder baseVertices = ImmutableArray.CreateBuilder<Vector3>(vertexCount);
        for (int index = 0; index < vertexCount; index++)
        {
            baseVertices.Add(ReadFiniteVector3(ref cursor, $"base vertex {index}"));
        }

        Vector3[] modifierVertices = new Vector3[modifierVertexCount];
        for (int index = 0; index < modifierVertexCount; index++)
        {
            modifierVertices[index] = ReadFiniteVector3(ref cursor, $"modifier vertex {index}");
        }

        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                uint index = cursor.ReadUInt32($"triangle {triangle} corner {corner}");
                if (index >= (uint)vertexCount)
                {
                    throw Invalid("sse-tri-index",
                        $"Triangle {triangle} references vertex {index}, outside {vertexCount} vertices.");
                }
            }
        }

        for (int index = 0; index < uvCount; index++)
        {
            float u = cursor.ReadSingle($"UV {index} U");
            float v = cursor.ReadSingle($"UV {index} V");
            if (!float.IsFinite(u) || !float.IsFinite(v))
            {
                throw Invalid("sse-tri-nonfinite", $"UV {index} contains a non-finite coordinate.");
            }
        }

        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                // FaceMorphLib treats this array as an opaque, unused topology stream. Some genuine
                // RaceMenu TRI providers contain one-past-UV-count values here, so preserve strict
                // byte bounds without inventing a semantic constraint the bake never consumes.
                _ = cursor.ReadUInt32($"texture triangle {triangle} corner {corner}");
            }
        }

        ImmutableArray<SseTriHeadMorph>.Builder morphs =
            ImmutableArray.CreateBuilder<SseTriHeadMorph>(checked(morphCount + modifierCount));
        Dictionary<string, SseTriHeadMorph> morphsByName =
            new(StringComparer.OrdinalIgnoreCase);
        for (int morphIndex = 0; morphIndex < morphCount; morphIndex++)
        {
            string name = ReadMorphName(ref cursor, $"morph {morphIndex}");
            float multiplier = cursor.ReadSingle($"morph {morphIndex} multiplier");
            if (!float.IsFinite(multiplier))
            {
                throw Invalid("sse-tri-nonfinite", $"Morph '{name}' has a non-finite multiplier.");
            }

            ImmutableArray<SseTriHeadVertexDelta>.Builder deltas =
                ImmutableArray.CreateBuilder<SseTriHeadVertexDelta>(vertexCount);
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                short x = cursor.ReadInt16($"morph '{name}' vertex {vertexIndex} X");
                short y = cursor.ReadInt16($"morph '{name}' vertex {vertexIndex} Y");
                short z = cursor.ReadInt16($"morph '{name}' vertex {vertexIndex} Z");
                Vector3 delta = new(x * multiplier, y * multiplier, z * multiplier);
                EnsureFinite(delta, $"Morph '{name}' vertex {vertexIndex}");
                deltas.Add(new SseTriHeadVertexDelta(vertexIndex, delta));
            }

            AddOrValidateMorph(morphs, morphsByName, diagnostics,
                new SseTriHeadMorph(name, SseTriHeadMorphEncoding.DenseInt16,
                    multiplier, deltas.MoveToImmutable()));
        }

        int modifierVertexIndex = 0;
        for (int modifierIndex = 0; modifierIndex < modifierCount; modifierIndex++)
        {
            string name = ReadMorphName(ref cursor, $"modifier {modifierIndex}");
            int affectedCount = ReadBoundedCount(ref cursor, $"modifier '{name}' affected vertex",
                vertexCount);
            int[] affectedIndices = new int[affectedCount];
            HashSet<int> uniqueIndices = new();
            for (int index = 0; index < affectedCount; index++)
            {
                uint affected = cursor.ReadUInt32($"modifier '{name}' affected index {index}");
                if (affected >= (uint)vertexCount)
                {
                    throw Invalid("sse-tri-index",
                        $"Modifier '{name}' references vertex {affected}, outside {vertexCount} vertices.");
                }

                int affectedIndex = (int)affected;
                if (!uniqueIndices.Add(affectedIndex))
                {
                    throw Invalid("sse-tri-index",
                        $"Modifier '{name}' repeats vertex {affectedIndex}.");
                }

                affectedIndices[index] = affectedIndex;
            }

            if (modifierVertices.Length - modifierVertexIndex < affectedCount)
            {
                throw Invalid("sse-tri-modifier-pool",
                    $"Modifier '{name}' consumes more vertices than the shared modifier pool contains.");
            }

            ImmutableArray<SseTriHeadVertexDelta>.Builder deltas =
                ImmutableArray.CreateBuilder<SseTriHeadVertexDelta>(affectedCount);
            for (int index = 0; index < affectedCount; index++)
            {
                int affectedIndex = affectedIndices[index];
                Vector3 delta = modifierVertices[modifierVertexIndex++] - baseVertices[affectedIndex];
                EnsureFinite(delta, $"Modifier '{name}' vertex {affectedIndex}");
                deltas.Add(new SseTriHeadVertexDelta(affectedIndex, delta));
            }

            AddOrValidateMorph(morphs, morphsByName, diagnostics,
                new SseTriHeadMorph(name,
                    SseTriHeadMorphEncoding.ModifierAbsolutePositions,
                    1F, deltas.MoveToImmutable()));
        }

        if (modifierVertexIndex != modifierVertices.Length)
        {
            throw Invalid("sse-tri-modifier-pool",
                $"FRTRI003 declares {modifierVertices.Length} modifier vertices but consumes {modifierVertexIndex}.");
        }

        if (cursor.Remaining != 0)
        {
            throw Invalid("sse-tri-trailing-bytes",
                $"FRTRI003 has {cursor.Remaining} unparsed trailing bytes.");
        }

        return new SseTriHeadDocument(sourcePath, sourceSha256, vertexCount, triangleCount,
            uvCount, flags, baseVertices.MoveToImmutable(), morphs.ToImmutable());
    }

    private static int ReadBoundedCount(ref Cursor cursor, string label, int maximum,
        bool requirePositive = false)
    {
        uint raw = cursor.ReadUInt32($"{label} count");
        if (raw > maximum || (requirePositive && raw == 0))
        {
            throw Invalid("sse-tri-count",
                $"FRTRI003 {label} count {raw} is outside the supported range.");
        }

        return checked((int)raw);
    }

    private static Vector3 ReadFiniteVector3(ref Cursor cursor, string label)
    {
        Vector3 value = new(cursor.ReadSingle($"{label} X"), cursor.ReadSingle($"{label} Y"),
            cursor.ReadSingle($"{label} Z"));
        EnsureFinite(value, label);
        return value;
    }

    private static void EnsureFinite(Vector3 value, string label)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw Invalid("sse-tri-nonfinite", $"{label} contains a non-finite coordinate.");
        }
    }

    private static string ReadMorphName(ref Cursor cursor, string label)
    {
        uint rawLength = cursor.ReadUInt32($"{label} name length");
        if (rawLength is 0 or > MaxMorphNameBytes)
        {
            throw Invalid("sse-tri-name", $"{label} has invalid name length {rawLength}.");
        }

        int length = checked((int)rawLength);
        ReadOnlySpan<byte> bytes = cursor.ReadBytes(length, $"{label} name");
        if (bytes[^1] != 0 || !IsPrintableAscii(bytes[..^1]))
        {
            throw Invalid("sse-tri-name",
                $"{label} name must be printable ASCII with one trailing NUL byte.");
        }

        string name = Encoding.ASCII.GetString(bytes[..^1]);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw Invalid("sse-tri-name", $"{label} name is empty.");
        }

        return name;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddOrValidateMorph(
        ImmutableArray<SseTriHeadMorph>.Builder morphs,
        Dictionary<string, SseTriHeadMorph> morphsByName,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        SseTriHeadMorph morph)
    {
        if (!morphsByName.TryGetValue(morph.Name,
                out SseTriHeadMorph? existing))
        {
            morphsByName.Add(morph.Name, morph);
            morphs.Add(morph);
            return;
        }

        bool equivalent =
            existing.Encoding == morph.Encoding &&
            BitConverter.SingleToInt32Bits(existing.Multiplier) ==
            BitConverter.SingleToInt32Bits(morph.Multiplier) &&
            existing.Deltas.SequenceEqual(morph.Deltas);
        int mismatch = equivalent
            ? -1
            : FirstDeltaMismatch(existing.Deltas, morph.Deltas);
        diagnostics.Add(Warning("sse-tri-duplicate-morph",
            $"FRTRI003 duplicate case-insensitive morph name '{morph.Name}' uses the first occurrence " +
            $"({existing.Encoding}/{existing.Deltas.Length} versus " +
            $"{morph.Encoding}/{morph.Deltas.Length}; first delta mismatch {mismatch})."));
    }

    private static int FirstDeltaMismatch(
        ImmutableArray<SseTriHeadVertexDelta> left,
        ImmutableArray<SseTriHeadVertexDelta> right)
    {
        int shared = Math.Min(left.Length, right.Length);
        for (int index = 0; index < shared; index++)
        {
            if (left[index] != right[index]) return index;
        }
        return left.Length == right.Length ? -1 : shared;
    }

    private static SseTriHeadReadResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private static TriFormatException Invalid(string code, string message) => new(code, message);

    private sealed class TriFormatException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;

        public Cursor(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public int Offset { get; private set; }

        public int Remaining => _data.Length - Offset;

        public ReadOnlySpan<byte> ReadBytes(int length, string label)
        {
            if (length < 0 || length > Remaining)
            {
                throw Invalid("sse-tri-truncated",
                    $"FRTRI003 ended while reading {label} at byte {Offset}.");
            }

            ReadOnlySpan<byte> result = _data.Slice(Offset, length);
            Offset += length;
            return result;
        }

        public short ReadInt16(string label) =>
            BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(sizeof(short), label));

        public uint ReadUInt32(string label) =>
            BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint), label));

        public float ReadSingle(string label) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                ReadBytes(sizeof(float), label)));
    }
}
