using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 本地化 StringFormat 转换器：解决 XAML StringFormat 属性不支持 {x:Static} 资源的问题。
/// Binding 值通过 ConverterParameter 中指定的 Strings 资源 key 进行格式化。
/// 同时支持单值绑定（IValueConverter）和多值绑定（IMultiValueConverter）。
/// 
/// 用法：
///   <!-- 旧（硬编码中文）：Text="{Binding Count, StringFormat={}{0:N0} 件}" -->
///   <!-- 新（单值，支持多语言）：Text="{Binding Count, Converter={StaticResource LocFormatConverter}, ConverterParameter=F245}" -->
///   <!-- 新（多值，支持多语言）：Text 内嵌 MultiBinding Converter={StaticResource LocFormatConverter} ConverterParameter=F292 ... -->
/// 
/// F245 = "{0:N0} 件"（中文）/ "{0:N0} pcs"（English）/ "{0:N0} 個"（日本語）
/// </summary>
public class StringFormatConverter : IValueConverter, IMultiValueConverter
{
    /// <summary>将数值按 ConverterParameter 指定的格式 key 格式化。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string key) return value?.ToString() ?? string.Empty;
        var format = Strings.S(key, key);
        try
        {
            return string.Format(format, value);
        }
        catch
        {
            return value?.ToString() ?? string.Empty;
        }
    }

    /// <summary>将多个绑定值按 ConverterParameter 指定的格式 key 格式化。</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string key) return string.Join(", ", values.Select(v => v?.ToString() ?? ""));
        var format = Strings.S(key, key);
        try
        {
            return string.Format(format, values);
        }
        catch
        {
            return string.Join(", ", values.Select(v => v?.ToString() ?? ""));
        }
    }

    /// <summary>逆向转换不支持。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("StringFormatConverter does not support ConvertBack");

    /// <summary>多值逆向转换不支持。</summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException("StringFormatConverter does not support ConvertBack (multi)");
}
