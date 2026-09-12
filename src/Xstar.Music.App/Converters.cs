using System.Globalization;
using System.Windows.Data;

namespace Xstar.Music.App;

/// <summary>Converts an artwork file path to a BitmapImage for data templates.</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? ArtworkLoader.Load(path) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
