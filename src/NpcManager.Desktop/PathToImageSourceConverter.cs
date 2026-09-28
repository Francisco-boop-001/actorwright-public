using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace NpcManager.Desktop;

public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        try
        {
            return OnLoadImageSource.Load(value as string);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => Binding.DoNothing;
}
