using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace SerialPortTool.Converters;

/// <summary>
/// Converts hex color string to SolidColorBrush
/// </summary>
public class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hexColor && !string.IsNullOrEmpty(hexColor))
        {
            try
            {
                var color = ParseHexColor(hexColor);
                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Black);
            }
        }
        return new SolidColorBrush(Colors.Black);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');

        byte a = 255;
        byte r, g, b;

        if (hex.Length == 6)
        {
            r = System.Convert.ToByte(hex.Substring(0, 2), 16);
            g = System.Convert.ToByte(hex.Substring(2, 2), 16);
            b = System.Convert.ToByte(hex.Substring(4, 2), 16);
        }
        else if (hex.Length == 8)
        {
            a = System.Convert.ToByte(hex.Substring(0, 2), 16);
            r = System.Convert.ToByte(hex.Substring(2, 2), 16);
            g = System.Convert.ToByte(hex.Substring(4, 2), 16);
            b = System.Convert.ToByte(hex.Substring(6, 2), 16);
        }
        else
        {
            return Colors.Black;
        }

        return Color.FromArgb(a, r, g, b);
    }
}
