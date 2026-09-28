using NpcManager.Application;

namespace NpcManager.FaceGen;

public sealed partial class SkyrimNativeFaceTintBuildService
{
    private static byte[] Compose(
        SkyrimNativeFaceTintRecordRoute route,
        Dictionary<string, FaceTintTextureDecodeResult> masks,
        CancellationToken cancellationToken)
    {
        var rgb = new float[checked(Width * Height * 3)];
        Array.Fill(rgb, 0.5F);
        foreach (SkyrimNativeFaceTintLayerRoute layer in route.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.Coverage <= 0F)
            {
                continue;
            }

            FaceTintTextureDecodeResult mask = masks[layer.MaskPath.Value];
            byte[] pixels = mask.Bytes!;
            float red = layer.Red / 255F;
            float green = layer.Green / 255F;
            float blue = layer.Blue / 255F;
            for (var y = 0; y < Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < Width; x++)
                {
                    float effective = SampleRedBilinear(
                        pixels, mask.Width, mask.Height, x, y) / 255F * layer.Coverage;
                    float inverse = 1F - effective;
                    int offset = checked((y * Width + x) * 3);
                    rgb[offset] = rgb[offset] * inverse + red * effective;
                    rgb[offset + 1] = rgb[offset + 1] * inverse + green * effective;
                    rgb[offset + 2] = rgb[offset + 2] * inverse + blue * effective;
                }
            }
        }

        var bgra = new byte[checked(Width * Height * 4)];
        for (var source = 0; source < rgb.Length; source += 3)
        {
            if ((source & 0x3F_FFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int destination = source / 3 * 4;
            bgra[destination] = ToByte(rgb[source + 2]);
            bgra[destination + 1] = ToByte(rgb[source + 1]);
            bgra[destination + 2] = ToByte(rgb[source]);
            bgra[destination + 3] = byte.MaxValue;
        }
        return bgra;
    }

    private static byte SampleRedBilinear(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY)
    {
        double sourceX = (destinationX + 0.5D) * sourceWidth / Width - 0.5D;
        double sourceY = (destinationY + 0.5D) * sourceHeight / Height - 0.5D;
        int x0 = (int)Math.Floor(sourceX);
        int y0 = (int)Math.Floor(sourceY);
        double xWeight = sourceX - x0;
        double yWeight = sourceY - y0;
        if (x0 < 0)
        {
            x0 = 0;
            xWeight = 0D;
        }
        if (y0 < 0)
        {
            y0 = 0;
            yWeight = 0D;
        }
        int x1 = Math.Min(x0 + 1, sourceWidth - 1);
        int y1 = Math.Min(y0 + 1, sourceHeight - 1);
        if (x0 >= sourceWidth - 1)
        {
            x0 = sourceWidth - 1;
            x1 = x0;
            xWeight = 0D;
        }
        if (y0 >= sourceHeight - 1)
        {
            y0 = sourceHeight - 1;
            y1 = y0;
            yWeight = 0D;
        }

        byte topLeft = bgra[checked((y0 * sourceWidth + x0) * 4 + 2)];
        byte topRight = bgra[checked((y0 * sourceWidth + x1) * 4 + 2)];
        byte bottomLeft = bgra[checked((y1 * sourceWidth + x0) * 4 + 2)];
        byte bottomRight = bgra[checked((y1 * sourceWidth + x1) * 4 + 2)];
        double top = topLeft * (1D - xWeight) + topRight * xWeight;
        double bottom = bottomLeft * (1D - xWeight) + bottomRight * xWeight;
        return (byte)Math.Clamp(Math.Round(
            top * (1D - yWeight) + bottom * yWeight,
            MidpointRounding.ToEven), 0D, 255D);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp(MathF.Round(value * 255F, MidpointRounding.ToEven),
            0F, 255F);
}
