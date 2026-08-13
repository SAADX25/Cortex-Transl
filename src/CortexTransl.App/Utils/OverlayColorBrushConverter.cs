using System.Globalization;
using System.Windows.Data;

namespace CortexTransl.App.Utils;

public sealed class OverlayColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return OverlayTextColors.ToBrush(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
