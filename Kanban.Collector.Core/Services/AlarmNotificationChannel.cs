using System.Media;
using System.Threading.Channels;
using MainAPP.Models;

namespace MainAPP.Services;

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
/// </summary>
public interface IAlarmNotificationChannel
{
    /// <summary>快速入队，不得在调用线程执行网络、音频或其他阻塞 I/O。</summary>
    void Enqueue(AlarmNotification notification);
}

/// <summary>
/// Windows 本地声音通知通道。采集线程只入队，声音由独立消费者播放。
/// </summary>
public sealed class SystemAlarmNotificationChannel : IAlarmNotificationChannel, IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly Channel<AlarmNotification> _highPriorityQueue = Channel.CreateUnbounded<AlarmNotification>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<AlarmNotification> _normalQueue = Channel.CreateBounded<AlarmNotification>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private long _droppedNormalNotifications;

    /// <summary>普通等级声音因队列满被丢弃的数量，高等级报警不会走此队列。</summary>
    public long DroppedNormalNotifications => Interlocked.Read(ref _droppedNormalNotifications);

    public SystemAlarmNotificationChannel(AppSettings appSettings)
    {
        _appSettings = appSettings;
        _consumer = ConsumeAsync();
    }

    public void Enqueue(AlarmNotification notification)
    {
        if (!_appSettings.EnableAlarmSound) return;
        if (notification.Level == AlarmLevel.High)
        {
            _highPriorityQueue.Writer.TryWrite(notification);
            return;
        }

        if (!_normalQueue.Writer.TryWrite(notification))
            Interlocked.Increment(ref _droppedNormalNotifications);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                AlarmNotification notification;
                if (_highPriorityQueue.Reader.TryRead(out var high))
                {
                    notification = high;
                }
                else if (_normalQueue.Reader.TryRead(out var normal))
                {
                    notification = normal;
                }
                else
                {
                    var highWait = _highPriorityQueue.Reader.WaitToReadAsync(_cancellation.Token).AsTask();
                    var normalWait = _normalQueue.Reader.WaitToReadAsync(_cancellation.Token).AsTask();
                    await Task.WhenAny(highWait, normalWait);
                    continue;
                }

                if (!_appSettings.EnableAlarmSound) continue;

                var sound = notification.Level switch
                {
                    AlarmLevel.High => SystemSounds.Hand,
                    AlarmLevel.Medium => SystemSounds.Exclamation,
                    _ => SystemSounds.Beep,
                };
                sound.Play();
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _highPriorityQueue.Writer.TryComplete();
        _normalQueue.Writer.TryComplete();
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
