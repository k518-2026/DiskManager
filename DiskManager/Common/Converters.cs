using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DiskManager.Common;

public class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ulong u) return Fmt.Bytes(u);
        if (value is long l) return Fmt.Bytes(l);
        if (value is int i) return Fmt.Bytes((long)i);
        if (value is double d) return Fmt.Bytes((ulong)d);
        return "-";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>健康状態の文字列を色に変換します。</summary>
public class HealthBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        if (s.Contains("良好") || s.Contains("正常") || s.Contains("OK"))
            return new SolidColorBrush(Color.FromRgb(0x35, 0xC1, 0xA6));
        if (s.Contains("注意") || s.Contains("警告"))
            return new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        if (s.Contains("不良") || s.Contains("故障") || s.Contains("危険"))
            return new SolidColorBrush(Color.FromRgb(0xE5, 0x54, 0x4B));
        return new SolidColorBrush(Color.FromRgb(0x8E, 0x9B, 0xAA));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value != null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
}

/// <summary>0.0〜1.0 の比率を、パラメーターで指定した最大幅(px)に変換します。</summary>
public class PercentWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ratio = 0;
        if (value is double dd) ratio = dd;
        else if (value is float ff) ratio = ff;
        double max = 120;
        if (parameter != null)
            double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out max);
        if (ratio < 0) ratio = 0;
        if (ratio > 1) ratio = 1;
        return Math.Round(ratio * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
