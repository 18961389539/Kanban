using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 集合包含判断 Converter（IMultiValueConverter）：values[0] 为待判定的元素，
/// values[1] 为实现 <see cref="ICollection{T}"/> 的集合，元素在集合中时返回 true。
/// 用于工单列表行：判定当前工单 Id 是否命中 ViewModel 的逾期/计划冲突集合，
/// 从而驱动行背景高亮与标签显隐（集合作为 INotifyPropertyChanged 属性，替换时自动重估）。
/// </summary>
public sealed class CollectionContainsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2 || values[0] == null || values[1] == null)
            return false;
        var item = values[0];
        // 兼容 ICollection<T>、IEnumerable<T> 等常见集合形态；values[1] 为 VM 的集合属性引用。
        var collection = values[1] as System.Collections.IEnumerable;
        if (collection == null)
            return false;
        return collection.Cast<object>().Contains(item);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}