using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.Converters;

/// <summary>
/// 报警/缺陷/计数报警/数据采集源配置多语言名称转换：按当前界面语言返回对应语言字段
/// （NameEn/NameJa/NamePt），缺失回退主名称。用于各管理 Tab 配置列表等绑定对象本身的显示点。
/// 数据采集源为定时模式（无触发地址）时附加「定时采集」后缀（资源 Dsm_TimedSuffix），与 DisplayName 口径一致。
/// </summary>
public class LocalizedNameToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Alarm alarm => AlarmNameLocalizer.Resolve(alarm),
            Defect defect => AlarmNameLocalizer.Resolve(defect),
            CounterAlarm counterAlarm => AlarmNameLocalizer.Resolve(counterAlarm),
            DataSource source => AlarmNameLocalizer.Resolve(source)
                + (source.HasTrigger ? string.Empty : Strings.Dsm_TimedSuffix),
            DataSourceValue valueItem => AlarmNameLocalizer.Resolve(valueItem),
            DataSourceEnumValue enumValue => AlarmNameLocalizer.Resolve(enumValue),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
