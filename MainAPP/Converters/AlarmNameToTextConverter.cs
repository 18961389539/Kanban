using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Services;

namespace MainAPP.Converters;

/// <summary>
/// 报警配置多语言名称转换：按当前界面语言返回 Alarm 的 NameEn/NameJa/NamePt，
/// 缺失回退 Name。用于报警管理列表等绑定 Alarm 对象的显示点。
/// </summary>
public class AlarmNameToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Alarm alarm ? AlarmNameLocalizer.Resolve(alarm) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
