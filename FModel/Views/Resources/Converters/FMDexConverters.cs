using System;
using System.Globalization;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using FModel.FMDex;

namespace FModel.Views.Resources.Converters;

public sealed class FMDexTagsConverter : IValueConverter
{
    public static readonly FMDexTagsConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameFile entry)
            return string.Empty;

        if (!FMDexService.Instance.TryGetEntry(entry, out var e) || e.Tags is not { Count: > 0 })
            return "Unindexed";

        return string.Join(", ", e.Tags);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FMDexIsUnindexedConverter : IValueConverter
{
    public static readonly FMDexIsUnindexedConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameFile entry)
            return false;
        return !FMDexService.Instance.IsIndexed(entry);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
