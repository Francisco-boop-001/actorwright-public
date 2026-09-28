using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

internal sealed class ImmutableInt32ArrayJsonConverter
    : JsonConverter<ImmutableArray<int>>
{
    public override ImmutableArray<int> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[] bytes = ReadPayload(ref reader, sizeof(int));
        var values = ImmutableArray.CreateBuilder<int>(
            bytes.Length / sizeof(int));
        for (var offset = 0;
             offset < bytes.Length;
             offset += sizeof(int))
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(offset, sizeof(int))));
        }
        return values.ToImmutable();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ImmutableArray<int> value,
        JsonSerializerOptions options)
    {
        byte[] bytes = new byte[
            checked(value.Length * sizeof(int))];
        for (var index = 0; index < value.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                value[index]);
        }
        writer.WriteBase64StringValue(bytes);
    }

    private static byte[] ReadPayload(
        ref Utf8JsonReader reader,
        int stride)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                "Compact immutable integer arrays must be base64 strings.");
        byte[] bytes = reader.GetBytesFromBase64();
        if (bytes.Length % stride != 0)
            throw new JsonException(
                "Compact immutable integer array length is invalid.");
        return bytes;
    }
}

internal sealed class ImmutableVector2ArrayJsonConverter
    : JsonConverter<ImmutableArray<Vector2>>
{
    private const int Stride = sizeof(float) * 2;

    public override ImmutableArray<Vector2> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[] bytes = ReadPayload(ref reader);
        var values = ImmutableArray.CreateBuilder<Vector2>(
            bytes.Length / Stride);
        for (var offset = 0;
             offset < bytes.Length;
             offset += Stride)
        {
            values.Add(new Vector2(
                ReadSingle(bytes.AsSpan(offset, sizeof(float))),
                ReadSingle(bytes.AsSpan(
                    offset + sizeof(float),
                    sizeof(float)))));
        }
        return values.ToImmutable();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ImmutableArray<Vector2> value,
        JsonSerializerOptions options)
    {
        byte[] bytes = new byte[
            checked(value.Length * Stride)];
        for (var index = 0; index < value.Length; index++)
        {
            int offset = index * Stride;
            WriteSingle(
                bytes.AsSpan(offset, sizeof(float)),
                value[index].X);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(float),
                    sizeof(float)),
                value[index].Y);
        }
        writer.WriteBase64StringValue(bytes);
    }

    private static byte[] ReadPayload(
        ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                "Compact immutable Vector2 arrays must be base64 strings.");
        byte[] bytes = reader.GetBytesFromBase64();
        if (bytes.Length % Stride != 0)
            throw new JsonException(
                "Compact immutable Vector2 array length is invalid.");
        return bytes;
    }

    private static float ReadSingle(
        ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static void WriteSingle(
        Span<byte> bytes,
        float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes,
            BitConverter.SingleToInt32Bits(value));
}

internal sealed class ImmutableVector3ArrayJsonConverter
    : JsonConverter<ImmutableArray<Vector3>>
{
    private const int Stride = sizeof(float) * 3;

    public override ImmutableArray<Vector3> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[] bytes = ReadPayload(ref reader);
        var values = ImmutableArray.CreateBuilder<Vector3>(
            bytes.Length / Stride);
        for (var offset = 0;
             offset < bytes.Length;
             offset += Stride)
        {
            values.Add(new Vector3(
                ReadSingle(bytes.AsSpan(offset, sizeof(float))),
                ReadSingle(bytes.AsSpan(
                    offset + sizeof(float),
                    sizeof(float))),
                ReadSingle(bytes.AsSpan(
                    offset + sizeof(float) * 2,
                    sizeof(float)))));
        }
        return values.ToImmutable();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ImmutableArray<Vector3> value,
        JsonSerializerOptions options)
    {
        byte[] bytes = new byte[
            checked(value.Length * Stride)];
        for (var index = 0; index < value.Length; index++)
        {
            int offset = index * Stride;
            WriteSingle(
                bytes.AsSpan(offset, sizeof(float)),
                value[index].X);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(float),
                    sizeof(float)),
                value[index].Y);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(float) * 2,
                    sizeof(float)),
                value[index].Z);
        }
        writer.WriteBase64StringValue(bytes);
    }

    private static byte[] ReadPayload(
        ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                "Compact immutable Vector3 arrays must be base64 strings.");
        byte[] bytes = reader.GetBytesFromBase64();
        if (bytes.Length % Stride != 0)
            throw new JsonException(
                "Compact immutable Vector3 array length is invalid.");
        return bytes;
    }

    private static float ReadSingle(
        ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static void WriteSingle(
        Span<byte> bytes,
        float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes,
            BitConverter.SingleToInt32Bits(value));
}

internal sealed class ImmutableTriDeltaArrayJsonConverter
    : JsonConverter<ImmutableArray<SseTriHeadVertexDelta>>
{
    private const int Stride =
        sizeof(int) + sizeof(float) * 3;

    public override ImmutableArray<SseTriHeadVertexDelta> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        byte[] bytes = ReadPayload(ref reader);
        var values =
            ImmutableArray.CreateBuilder<SseTriHeadVertexDelta>(
                bytes.Length / Stride);
        for (var offset = 0;
             offset < bytes.Length;
             offset += Stride)
        {
            values.Add(new SseTriHeadVertexDelta(
                BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.AsSpan(offset, sizeof(int))),
                new Vector3(
                    ReadSingle(bytes.AsSpan(
                        offset + sizeof(int),
                        sizeof(float))),
                    ReadSingle(bytes.AsSpan(
                        offset + sizeof(int) + sizeof(float),
                        sizeof(float))),
                    ReadSingle(bytes.AsSpan(
                        offset + sizeof(int) + sizeof(float) * 2,
                        sizeof(float))))));
        }
        return values.ToImmutable();
    }

    public override void Write(
        Utf8JsonWriter writer,
        ImmutableArray<SseTriHeadVertexDelta> value,
        JsonSerializerOptions options)
    {
        byte[] bytes = new byte[
            checked(value.Length * Stride)];
        for (var index = 0; index < value.Length; index++)
        {
            int offset = index * Stride;
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(offset, sizeof(int)),
                value[index].VertexIndex);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(int),
                    sizeof(float)),
                value[index].Delta.X);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(int) + sizeof(float),
                    sizeof(float)),
                value[index].Delta.Y);
            WriteSingle(
                bytes.AsSpan(
                    offset + sizeof(int) + sizeof(float) * 2,
                    sizeof(float)),
                value[index].Delta.Z);
        }
        writer.WriteBase64StringValue(bytes);
    }

    private static byte[] ReadPayload(
        ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                "Compact immutable TRI-delta arrays must be base64 strings.");
        byte[] bytes = reader.GetBytesFromBase64();
        if (bytes.Length % Stride != 0)
            throw new JsonException(
                "Compact immutable TRI-delta array length is invalid.");
        return bytes;
    }

    private static float ReadSingle(
        ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static void WriteSingle(
        Span<byte> bytes,
        float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes,
            BitConverter.SingleToInt32Bits(value));
}
