using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace maildot.Converters;

public sealed class LabelCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = 0;
        if (value is int i)
        {
            count = i;
        }
        else if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
            {
                count++;
                if (count > 0) break;
            }
        }

        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
