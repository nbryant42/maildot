using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
using System;

namespace maildot.Converters;

public sealed class UnreadCountToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value is int i ? i : 0;
        return count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
