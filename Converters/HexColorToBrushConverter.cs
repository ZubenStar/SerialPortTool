using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace SerialPortTool.Converters;

/// <summary>
/// Converts hex color string to SolidColorBrush
/// </summary>
public class HexColorToBrushConverter : IValueConverter
{
    // Only ever accessed from the UI thread (x:Bind/{Binding} evaluation), so a plain
    // dictionary avoids ConcurrentDictionary bookkeeping on every item realize.
    private static readonly Dictionary<string, SolidColorBrush> _brushCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Brush used when the bound hex is empty or unparseable.
    /// </summary>
    /// <remarks>
    /// Deliberately a mid grey rather than the themed text brush: this path is unreachable in normal
    /// operation (every construction site assigns a colour), and a theme-aware lookup would have to
    /// come from <c>Application.Current.Resources</c>, which does not resolve through the window
    /// root's <c>ElementTheme</c> — it would hand back the *system* theme's brush and paint dark rows
    /// in the light colour. A neutral grey is legible on both palettes and needs no maintenance.
    /// </remarks>
    private static readonly Brush _fallbackBrush = new SolidColorBrush(Colors.Gray);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hexColor || string.IsNullOrWhiteSpace(hexColor))
        {
            return _fallbackBrush;
        }

        if (_brushCache.TryGetValue(hexColor, out var cached))
        {
            return cached;
        }

        // Shared with the ViewModel's slot resolver so the app has exactly one hex parser.
        var color = PortColorPalette.ParseHex(hexColor);
        if (color == Colors.Transparent)
        {
            return _fallbackBrush;
        }

        var brush = new SolidColorBrush(color);
        _brushCache[hexColor] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
