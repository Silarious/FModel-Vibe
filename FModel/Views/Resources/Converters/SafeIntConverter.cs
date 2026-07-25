using System;
using System.Globalization;
using System.Windows.Data;

namespace FModel.Views.Resources.Converters;

/// <summary>
/// Int ↔ string for TextBox bindings. Empty / partial input keeps the previous value
/// (<see cref="Binding.DoNothing"/>) so tab switches and mid-edit clears do not throw.
/// </summary>
public sealed class SafeIntConverter : IValueConverter
{
    public static readonly SafeIntConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            int i => i.ToString(culture ?? CultureInfo.CurrentCulture),
            null => "0",
            _ => value.ToString() ?? "0"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i)
            return i;

        var text = value as string ?? value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return Binding.DoNothing;

        return int.TryParse(text.Trim(), NumberStyles.Integer, culture ?? CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}
