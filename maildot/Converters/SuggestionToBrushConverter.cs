using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace maildot.Converters;

public sealed partial class SuggestionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double score || score <= 0)
        {
            return new SolidColorBrush(Colors.Transparent);
        }

        // score expected in [0,1]; clamp just in case.
        var t = Math.Min(1.0, Math.Max(0.0, score));

        // Interpolate between soft green (best) and soft orange (worst).
        var start = (r: 0xD9, g: 0xF2, b: 0xE3);
        var end = (r: 0xFF, g: 0xE8, b: 0xD5);

        static byte Lerp(byte a, byte b, double p) => (byte)(a + (b - a) * p);

        var color = Color.FromArgb(255,
            Lerp((byte)start.r, (byte)end.r, t),
            Lerp((byte)start.g, (byte)end.g, t),
            Lerp((byte)start.b, (byte)end.b, t));

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
