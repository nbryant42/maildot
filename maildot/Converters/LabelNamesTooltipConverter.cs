using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Data;

namespace maildot.Converters;

public sealed class LabelNamesTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is IEnumerable<string> names)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            if (list.Length > 0)
            {
                return string.Join(", ", list);
            }
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
