using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CodeBridge.Converters;

/// <summary>
/// 布尔值转状态颜色转换器
/// </summary>
public class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning
                ? new SolidColorBrush(Colors.LimeGreen)  // 运行中：绿色
                : new SolidColorBrush(Colors.Gray);       // 空闲：灰色
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 字符串转可见性转换器
/// </summary>
public class String2VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 整数转可见性转换器（大于0时可见）
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int count)
        {
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值反转转换器
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return value;
    }
}

/// <summary>
/// Null 转可见性转换器（null 时 Visible，非 null 时 Collapsed）
/// 用于 WebView 加载占位符：WebView.CoreWebView2 为 null 时显示加载动画
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        // value 为 null 时返回 Visible（显示加载动画）
        // value 不为 null 时返回 Collapsed（隐藏加载动画）
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 布尔值转可见性转换器
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// 布尔值反转转可见性转换器
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v != Visibility.Visible;
        }
        return true;
    }
}

/// <summary>
/// 布尔值转状态文本转换器（工作中/待机中）
/// </summary>
public class BoolToStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning ? "工作中" : "待机中";
        }
        return "待机中";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 多布尔值转可见性转换器（任意一个为 true 时隐藏）
/// 用于解决 WebView2 Airspace 问题：当任意 Drawer 打开时隐藏 WebView2
/// </summary>
public class AnyBoolToCollapsedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        // 如果任意一个值为 true（即任意 Drawer 打开），则隐藏 WebView2
        foreach (var value in values)
        {
            if (value is bool b && b)
            {
                return Visibility.Collapsed;
            }
        }
        // 所有 Drawer 都关闭时，显示 WebView2
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shell 类型转选中状态转换器
/// </summary>
public class ShellTypeToCheckedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string shellType && parameter is string targetType2)
        {
            return string.Equals(shellType, targetType2, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string targetType2)
        {
            return targetType2;
        }
        return Binding.DoNothing;
    }
}
