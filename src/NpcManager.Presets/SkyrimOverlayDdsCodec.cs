using System.Buffers.Binary;
using NpcManager.Application;

namespace NpcManager.Presets;

/// <summary>Writes the bounded fold result as deterministic uncompressed BGRA8 DDS.
/// The upstream bake delegates compression/mip generation to DirectXTex; this codec
/// keeps the core independent of a native process and makes the emitted bytes auditable.
/// A later provider/codec adapter can add BCn output without changing the compositor.</summary>
internal static class SkyrimOverlayDdsCodec
{
    private const uint HeaderFlags = 0x0002_100F;
    private const uint PixelFlags = 0x0000_0041;
    private const uint CapsTexture = 0x0000_1000;

    public static byte[] Encode(SkyrimRgbaRaster raster)
    {
        var payloadLength = checked(raster.Width * raster.Height * 4);
        var bytes = new byte[128 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x20534444); // DDS magic
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), HeaderFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)raster.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)raster.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(raster.Width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 1); // one deterministic mip level
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), PixelFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000); // R in BGRA byte order
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), CapsTexture);

        for (var pixel = 0; pixel < raster.PixelCount; pixel++)
        {
            var source = pixel * 4;
            var target = 128 + source;
            bytes[target] = Quantize(raster.Pixels[source + 2]);
            bytes[target + 1] = Quantize(raster.Pixels[source + 1]);
            bytes[target + 2] = Quantize(raster.Pixels[source]);
            bytes[target + 3] = Quantize(raster.Pixels[source + 3]);
        }

        return bytes;
    }

    private static byte Quantize(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255d, MidpointRounding.ToEven), 0d, 255d);
}
