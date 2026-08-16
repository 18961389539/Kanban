using System.Threading.Channels;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 新报警通知内容。通知通道只接收领域事件，不依赖 WPF 页面生命周期。
/// </summary>
public sealed record AlarmNotification(
    string DeviceId,
    string DeviceName,
    string AlarmId,
    string AlarmName,
    AlarmLevel Level,
    DateTime EventTime);

/// <summary>
/// 报警通知通道抽象。后续可增加安灯、Webhook 或企业微信实现。
/// Windows 声音实现（SystemAlarmNotificationChannel）位于 MainAPP（依赖 WPF 的
/// System.Media.SystemSounds，Core 不依赖 WPF；无头 Collector 不注册，静默）。
/// </summary>
public interface IAlarmNotificationChannel
{
    /// <summary>快速入队，不得在调用线程执行网络、音频或其他阻塞 I/O。</summary>
    void Enqueue(AlarmNotification notification);
}
