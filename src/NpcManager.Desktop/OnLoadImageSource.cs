using System.Collections.Immutable;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NpcManager.Desktop;

public static class OnLoadImageSource
{
    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static ImageSource? Load(ImmutableArray<byte> encodedImage)
    {
        if (encodedImage.IsDefaultOrEmpty)
            return null;

        using var stream = new MemoryStream(
            encodedImage.ToArray(), writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
