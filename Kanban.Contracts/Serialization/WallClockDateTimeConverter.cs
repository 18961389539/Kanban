using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kanban.Contracts.Serialization;

/// <summary>
/// 墙钟时间透传转换器（SignalR JSON 协议专用）：DateTime 按字面时间序列化（"yyyy-MM-ddTHH:mm:ss.fffffff"，
/// 不带时区偏移/Kind 后缀），反序列化按字面解析为 Kind=Unspecified，全程零时区换算。
///
/// 背景：System.Text.Json 默认把 Kind=Local 的 DateTime 序列化为带偏移（+08:00），Blazor WASM 端
/// 反序列化时按<b>浏览器时区</b>换算——异地访问 Web 看板时所有时间偏移显示，与 WPF 端（工厂本地
/// 时间）不一致。本转换器让 Web 端始终显示 Collector 所在工厂的墙钟时间，与 WPF 端口径一致。
///
/// 兼容性：
/// - 新客户端读旧服务端（带偏移 "+08:00"）：回退路径取字面墙钟（DateTimeOffset.DateTime），正确；
/// - 旧客户端读新服务端（无偏移字面量）：STJ 默认解析为 Unspecified，按字面显示，正确；
/// - "Z" 结尾（UTC）负载：取字面 UTC 数字（不换算）。业务全域使用本地墙钟，正常不会出现 Z；
///   此路径仅为防御性兜底，且客户端无从得知工厂时区，字面读取是最小意外选择。
///
/// DateTime? 覆盖（设计审查实证 2026-09-16）：STJ 对 Nullable&lt;T&gt; 经 NullableConverterFactory
/// 复用底层 T 的已注册转换器，故 DTO 中的 DateTime?（HistoryQueryRequest.From/To 等）同样走本转换器，
/// 无需单独的可空转换器。回归测试见 WallClockDateTimeConverterTests.NullableDateTime_WithValue_*。
///
/// Kind 语义注意：JSON 通道反序列化结果为 Kind=Unspecified，而 MessagePack 通道（WPF）由
/// NativeDateTimeResolver 保留 Kind=Local。消费方不得对反序列化值调用 ToLocalTime()/ToUniversalTime()
/// （两端行为不同）；墙钟语义下这些换算本就无意义，按字面显示/比较即可。
/// 仅用于 SignalR JSON 通道（Collector 服务端 + Kanban.Client useMessagePack:false 分支成对注册）。
/// </summary>
public sealed class WallClockDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return default;
        // 新格式：无偏移字面量 → 字面解析（Kind=Unspecified）
        if (DateTime.TryParseExact(s, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;
        // 旧格式回退：带偏移/Z → 取字面墙钟（忽略偏移，不做时区换算）
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.DateTime;
        // 兜底：其他合法 DateTime 文本（如无毫秒的 ISO 变体）
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        // 无论 Kind 如何均按字面墙钟写出（Collector 全域本地时间，字面即工厂时间）
        => writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}
