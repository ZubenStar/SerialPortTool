using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace SerialPortTool.Converters;

/// <summary>
/// Converts string to Visibility (not empty = Visible, empty = Collapsed)
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string str)
        {
            return !string.IsNullOrEmpty(str) ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
