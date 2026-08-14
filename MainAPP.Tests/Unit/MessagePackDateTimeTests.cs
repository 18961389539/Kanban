using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// MessagePack 协议 DateTime 时区漂移回归测试（P1 修复，2026-08-14）。
/// 背景：默认 StandardResolver 把 DateTime 序列化为 int64 ticks 且不保留 Kind，
/// 反序列化一律得到 Kind=Utc——WPF 渲染 Utc 时隐式 ToLocalTime（GMT+8 下 +8h），
/// 导致报警/快照/事件时间戳整体漂移（报警中心/主页持续时长与真实时刻对不上）。
/// 修复：Collector/Client/测试 Hub 三处 AddMessagePackProtocol 统一配置
/// CompositeResolver(NativeDateTimeResolver + StandardResolver)——
/// Native 仅覆盖 DateTime（保留 Kind），其余类型回退 Standard。
/// 本测试锁定该语义，防止后续把 resolver 改回默认、漏配两端，
/// 或误用 WithResolver 直接替换导致 DTO 无法序列化（FormatterNotRegisteredException）。
/// </summary>
public class MessagePackDateTimeTests
{
    /// <summary>与生产三处 AddMessagePackProtocol 完全一致的 resolver 配置。
    /// Native 覆盖 DateTime（保留 Kind）；ContractlessStandardResolver 覆盖任意 DTO
    /// （与 SignalR MessagePackHubProtocol 默认语义一致），缺一不可。</summary>
    private static MessagePackSerializerOptions ProductionOptions =>
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                NativeDateTimeResolver.Instance,
                ContractlessStandardResolver.Instance));

    [Fact]
    public void NativeDateTimeResolver_RoundTrips_ValueAndKind()
    {
        var options = ProductionOptions;
        var original = new DateTime(2026, 8, 14, 4, 15, 1, DateTimeKind.Local);

        var packed = MessagePackSerializer.Serialize(original, options);
        var restored = MessagePackSerializer.Deserialize<DateTime>(packed, options);

        // DateTime.Equals 同时比较 Ticks 与 Kind → 值与 Kind 都必须保留
        Assert.Equal(original, restored);
        Assert.Equal(DateTimeKind.Local, restored.Kind);
    }

    [Fact]
    public void DefaultResolver_RoundTrips_ShiftsToUtcByLocalOffset()
    {
        // 回归对照：证明默认 resolver 的漂移机理——DateTime 序列化时被 ToUniversalTime
        // （GMT+8 下值 -8h）且 Kind=Utc；WPF 直接 StringFormat 渲染显示的是 UTC 时刻（早 8 小时）。
        var options = MessagePackSerializerOptions.Standard;
        var original = new DateTime(2026, 8, 14, 4, 15, 1, DateTimeKind.Local);

        var packed = MessagePackSerializer.Serialize(original, options);
        var restored = MessagePackSerializer.Deserialize<DateTime>(packed, options);

        Assert.Equal(DateTimeKind.Utc, restored.Kind);
        Assert.Equal(original.ToUniversalTime().Ticks, restored.Ticks);
    }

    [Fact]
    public void ActiveAlarmDto_StartTime_RoundTrips_WithLocalKind()
    {
        // 生产真实负载：快照内嵌 ActiveAlarmDto.StartTime 必须原样往返。
        // （WPF 主页实时故障 EventTime 显示、报警中心时间列均依赖此字段）
        var options = ProductionOptions;
        var dto = new ActiveAlarmDto
        {
            AlarmId = "A1",
            Name = "温控偏差-2",
            PlcAddress = "M100",
            Description = "",
            Level = AlarmLevel.Medium,
            StartTime = new DateTime(2026, 8, 14, 4, 15, 1, DateTimeKind.Local),
        };

        var packed = MessagePackSerializer.Serialize(dto, options);
        var restored = MessagePackSerializer.Deserialize<ActiveAlarmDto>(packed, options);

        Assert.Equal(dto.StartTime, restored.StartTime);
        Assert.Equal(DateTimeKind.Local, restored.StartTime.Kind);
    }

    [Fact]
    public void DeviceSnapshotDto_Serializes_WithProductionResolver()
    {
        // 回归（2026-08-14 实测）：误用 Standard.WithResolver(NativeDateTimeResolver)
        // 替换整个 resolver 后，业务 DTO 无格式化器 → FormatterNotRegisteredException，
        // 服务端推送快照失败并中止连接（"Failed writing message. Aborting connection."）。
        // CompositeResolver 组合必须保证 DeviceSnapshotDto（快照/报警/状态全链路）可序列化。
        var options = ProductionOptions;
        var snapshot = new DeviceSnapshotDto
        {
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            Status = DeviceStatus.Running,
            Timestamp = DateTime.Now,
            ActiveAlarms =
            [
                new ActiveAlarmDto
                {
                    AlarmId = "dev-1_M100",
                    Name = "温度异常",
                    PlcAddress = "M100",
                    Description = "",
                    Level = AlarmLevel.Medium,
                    StartTime = DateTime.Now,
                },
            ],
        };

        var packed = MessagePackSerializer.Serialize(snapshot, options);
        var restored = MessagePackSerializer.Deserialize<DeviceSnapshotDto>(packed, options);

        Assert.Equal("dev-1", restored.DeviceId);
        Assert.Single(restored.ActiveAlarms);
        Assert.Equal(DateTimeKind.Local, restored.ActiveAlarms[0].StartTime.Kind);
        Assert.Equal(DateTimeKind.Local, restored.Timestamp.Kind);
    }
}
