using System.Text.Json;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Contracts.Serialization;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// WallClockDateTimeConverter（SignalR JSON 通道墙钟透传）回归测试（2026-09-16）。
/// 背景：STJ 默认把 Kind=Local 的 DateTime 序列化为带偏移（+08:00），Blazor WASM 端反序列化
/// 按<b>浏览器时区</b>换算——异地访问 Web 看板时全部时间偏移显示，与 WPF 端（工厂本地时间）不一致。
/// 修复：Collector AddJsonProtocol 与 Kanban.Client useMessagePack:false 分支成对注册本转换器，
/// DateTime 按字面墙钟传输（无偏移、零时区换算），Web 端始终显示工厂本地时间。
/// 本测试锁定该语义，防止后续改回默认序列化或漏配任一端。
/// </summary>
public class WallClockDateTimeConverterTests
{
    private static JsonSerializerOptions ProductionOptions => new()
    {
        Converters = { new WallClockDateTimeConverter() },
    };

    [Fact]
    public void Write_LocalDateTime_EmitsWallClockWithoutOffset()
    {
        var value = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Local);

        var json = JsonSerializer.Serialize(value, ProductionOptions);

        // 字面墙钟：无 +08:00 偏移、无 Z 后缀（与运行机器时区无关，任何时区下断言均稳定）
        Assert.Equal("\"2026-09-16T09:00:00.0000000\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesWallClockTicks_AsUnspecified()
    {
        var original = new DateTime(2026, 9, 16, 9, 0, 0, 123, DateTimeKind.Local);

        var restored = JsonSerializer.Deserialize<DateTime>(
            JsonSerializer.Serialize(original, ProductionOptions), ProductionOptions);

        Assert.Equal(original.Ticks, restored.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, restored.Kind);
    }

    [Fact]
    public void Read_LegacyOffsetPayload_TakesLiteralWallClock_NoTzConversion()
    {
        // 旧服务端（默认 STJ）写出带偏移：字面 09:00 +08:00。转换器必须取字面 09:00，
        // 不得按浏览器时区换算（STJ 默认在 UTC 浏览器会得 01:00 Local——即被修复的漂移）。
        var restored = JsonSerializer.Deserialize<DateTime>("\"2026-09-16T09:00:00+08:00\"", ProductionOptions);

        Assert.Equal(new DateTime(2026, 9, 16, 9, 0, 0), restored);
        Assert.Equal(DateTimeKind.Unspecified, restored.Kind);
    }

    [Fact]
    public void Read_LegacyUtcPayload_TakesLiteralWallClock()
    {
        var restored = JsonSerializer.Deserialize<DateTime>("\"2026-09-16T01:02:03Z\"", ProductionOptions);

        Assert.Equal(new DateTime(2026, 9, 16, 1, 2, 3), restored);
    }

    [Fact]
    public void NullableDateTime_Null_RoundTrips()
    {
        DateTime? value = null;

        var json = JsonSerializer.Serialize(value, ProductionOptions);
        var restored = JsonSerializer.Deserialize<DateTime?>(json, ProductionOptions);

        Assert.Equal("null", json);
        Assert.Null(restored);
    }

    [Fact]
    public void NullableDateTime_WithValue_UsesWallClockConverter()
    {
        // 设计审查回归（2026-09-16）：DTO 大量 DateTime?（HistoryQueryRequest.From/To、
        // DataSourceValueSnapshotDto.LastUpdatedAt 等）。STJ 对 Nullable<T> 经由
        // NullableConverterFactory 复用底层 T 的已注册转换器——本测试锁定该行为，
        // 若未来 STJ 行为变化或有人误加默认 DateTime? 转换器，此处立即失败。
        DateTime? value = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Local);

        var json = JsonSerializer.Serialize(value, ProductionOptions);
        var restored = JsonSerializer.Deserialize<DateTime?>(json, ProductionOptions);

        // 无偏移字面量 = 确实走了 WallClock 转换器（默认序列化会带 +08:00）
        Assert.Equal("\"2026-09-16T09:00:00.0000000\"", json);
        Assert.NotNull(restored);
        Assert.Equal(value.Value.Ticks, restored!.Value.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, restored.Value.Kind);
    }

    [Fact]
    public void HistoryQueryRequest_NullableFromTo_SerializeWithoutOffset()
    {
        // 真实契约负载：查询请求的可空时间范围经 JSON 通道必须按墙钟字面透传，
        // 否则浏览器时区会污染服务端查询窗口（墙钟重构的核心场景）。
        var req = new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            From = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Local),
            To = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Local),
        };

        var json = JsonSerializer.Serialize(req, ProductionOptions);
        var restored = JsonSerializer.Deserialize<HistoryQueryRequest>(json, ProductionOptions);

        Assert.DoesNotContain("+08", json);
        Assert.Equal(req.From!.Value.Ticks, restored!.From!.Value.Ticks);
        Assert.Equal(req.To!.Value.Ticks, restored.To!.Value.Ticks);
    }

    [Fact]
    public void DeviceSnapshotDto_Serializes_WithWallClockConverter()
    {
        // 生产真实负载：快照（含内嵌报警 StartTime）经 JSON 通道往返，字面值必须保留。
        var options = ProductionOptions;
        var snapshot = new DeviceSnapshotDto
        {
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            Status = DeviceStatus.Running,
            Timestamp = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Local),
            ActiveAlarms =
            [
                new ActiveAlarmDto
                {
                    AlarmId = "dev-1_M100",
                    Name = "温度异常",
                    PlcAddress = "M100",
                    Description = "",
                    Level = AlarmLevel.Medium,
                    StartTime = new DateTime(2026, 9, 16, 8, 30, 0, DateTimeKind.Local),
                },
            ],
        };

        var restored = JsonSerializer.Deserialize<DeviceSnapshotDto>(
            JsonSerializer.Serialize(snapshot, options), options);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Timestamp.Ticks, restored!.Timestamp.Ticks);
        Assert.Single(restored.ActiveAlarms);
        Assert.Equal(snapshot.ActiveAlarms[0].StartTime.Ticks, restored.ActiveAlarms[0].StartTime.Ticks);
    }
}
