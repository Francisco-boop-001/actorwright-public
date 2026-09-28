using System.Buffers.Binary;

namespace NpcManager.FaceGen;

public sealed partial class RaceMenuNpcFaceTextureBuildService
{
    private static byte[] EncodeBgra8(int width, int height, byte[] pixels)
    {
        var payload = checked(width * height * 4);
        if (pixels.Length != payload)
            throw new ArgumentException("The raster byte count does not match its dimensions.");
        var bytes = new byte[128 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0000_100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        Buffer.BlockCopy(pixels, 0, bytes, 128, payload);
        return bytes;
    }
}
