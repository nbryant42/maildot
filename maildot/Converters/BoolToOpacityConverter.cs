using System;
using Microsoft.UI.Xaml.Data;

namespace maildot.Converters;

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isVisible && isVisible ? 1.0d : 0.0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
