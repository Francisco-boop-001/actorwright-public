using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NpcManager.Desktop;

namespace NpcManager.Desktop.Smoke;

internal static class OnLoadImageSourceTests
{
    public static void Run()
    {
        Assert(OnLoadImageSource.Load(null) is null,
            "The image loader accepted a null path.");
        Assert(OnLoadImageSource.Load("   ") is null,
            "The image loader accepted a blank path.");

        string root = Path.Combine(
            Path.GetTempPath(),
            $"actorwright-onload-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "preview.png");
        try
        {
            Assert(OnLoadImageSource.Load(imagePath) is null,
                "The image loader accepted a missing path.");

            WriteSolidPng(imagePath, red: true);
            BitmapSource first = AssertBitmap(OnLoadImageSource.Load(imagePath),
                "The image loader did not decode the first real PNG.");
            Assert(first.IsFrozen,
                "The first decoded image was not frozen.");
            byte[] firstPixelsBeforeReplacement = CopyBgra32Pixels(first);
            AssertSolidColor(firstPixelsBeforeReplacement, blue: 0, red: 255,
                "The first decoded image was not red.");

            File.Delete(imagePath);
            WriteSolidPng(imagePath, red: false);
            BitmapSource second = AssertBitmap(OnLoadImageSource.Load(imagePath),
                "The image loader did not decode the same-path replacement PNG.");
            Assert(second.IsFrozen,
                "The replacement decoded image was not frozen.");

            byte[] firstPixelsAfterReplacement = CopyBgra32Pixels(first);
            byte[] secondPixels = CopyBgra32Pixels(second);
            Assert(firstPixelsBeforeReplacement.SequenceEqual(firstPixelsAfterReplacement),
                "The first decoded image changed after its source path was replaced.");
            Assert(!firstPixelsAfterReplacement.SequenceEqual(secondPixels),
                "The same-path replacement reused stale decoded pixels.");
            AssertSolidColor(firstPixelsAfterReplacement, blue: 0, red: 255,
                "The first decoded image no longer remained red.");
            AssertSolidColor(secondPixels, blue: 255, red: 0,
                "The same-path replacement image was not blue.");

            var converter = new PathToImageSourceConverter();
            var image = new Image();
            BindingOperations.SetBinding(image, Image.SourceProperty, new Binding
            {
                Source = imagePath,
                Converter = converter
            });
            image.GetBindingExpression(Image.SourceProperty)?.UpdateTarget();
            BitmapSource bound = AssertBitmap(image.Source,
                "The path converter did not supply the rendered Image binding.");
            AssertSolidColor(CopyBgra32Pixels(bound), blue: 255, red: 0,
                "The rendered Image binding did not show the replacement pixels.");
            File.Delete(imagePath);

            File.WriteAllBytes(imagePath, [0x00, 0x01, 0x02, 0x03]);
            object? invalid = converter.Convert(
                imagePath,
                typeof(ImageSource),
                parameter: null,
                CultureInfo.InvariantCulture);
            Assert(invalid is null,
                "The path converter surfaced an unsupported image payload.");
            Assert(ReferenceEquals(
                    converter.ConvertBack(
                        bound,
                        typeof(string),
                        parameter: null,
                        CultureInfo.InvariantCulture),
                    Binding.DoNothing),
                "The one-way image converter attempted a source write.");
        }
        finally
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
            Directory.Delete(root, recursive: false);
        }
    }

    private static void WriteSolidPng(string path, bool red)
    {
        byte blue = red ? (byte)0 : (byte)255;
        byte redChannel = red ? (byte)255 : (byte)0;
        byte[] pixels =
        [
            blue, 0, redChannel, 255,
            blue, 0, redChannel, 255,
            blue, 0, redChannel, 255,
            blue, 0, redChannel, 255
        ];
        BitmapSource source = BitmapSource.Create(
            pixelWidth: 2,
            pixelHeight: 2,
            dpiX: 96,
            dpiY: 96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        encoder.Save(stream);
    }

    private static BitmapSource AssertBitmap(ImageSource? source, string message)
    {
        if (source is BitmapSource bitmap)
            return bitmap;
        throw new InvalidOperationException(message);
    }

    private static byte[] CopyBgra32Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, offset: 0);
        return pixels;
    }

    private static void AssertSolidColor(
        byte[] pixels,
        byte blue,
        byte red,
        string message)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != blue ||
                pixels[offset + 1] != 0 ||
                pixels[offset + 2] != red ||
                pixels[offset + 3] != 255)
                throw new InvalidOperationException(message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
