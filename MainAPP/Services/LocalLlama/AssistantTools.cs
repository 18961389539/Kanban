using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainAPP.Services;

public sealed record AssistantToolParam(string Name, string Description, bool Required);

public sealed record AssistantToolSpec(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<AssistantToolParam> Parameters);

public sealed record AssistantToolCall(string Id, string Name, string ArgumentsJson);

public sealed record AssistantChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<AssistantToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);

public readonly record struct AssistantChatDelta(string? Text, IReadOnlyList<AssistantToolCall>? ToolCalls);

public static class AssistantToolCatalog
{
    private static AssistantToolParam Period { get; } = new(
        "period",
        "问句里的时间，例如昨天、当前班次、近7天。",
        false);

    private static AssistantToolParam Device { get; } = new(
        "device",
        "设备名。这台表示当前选中的设备。",
        false);

    public static IReadOnlyList<AssistantToolSpec> All { get; } =
    [
        Tool("query_output", "产量", "查询产量和合格率。", Period, Device),
        Tool("query_alarm_duration", "报警时长", "查询状态为报警的小时。", Period, Device),
        Tool("list_alarms", "报警记录", "列出报警的触发和恢复，以及当前还没恢复的报警。", Period, Device),
        Tool("list_status", "状态记录", "列出运行、报警、暂停、离线的状态切换。", Period, Device),
        Tool("list_defects", "缺陷", "查询这段时间的缺陷件数。", Period, Device),
        Tool("list_work_orders", "工单", "查询工单。", Device),
        Tool("list_snapshots", "产量快照", "列出产量快照。", Period, Device),
        Tool("list_runtime", "当前运行", "查询当前班次、配方、性能率、开动率和综合 OEE。", Device),
        Tool("list_barcodes", "条码", "查询条码记录。", Period),
        Tool("list_accounts", "账号", "查询账号。"),
        Tool("list_audit", "审计", "查询审计。", Period),
        Tool("list_addresses", "设备地址", "查询设备点位地址。"),
    ];

    private static AssistantToolSpec Tool(string name, string title, string description, params AssistantToolParam[] parameters)
        => new(name, title, description, parameters);
}

public static class AssistantToolQuestions
{
    public static string ForMetric(string tool, string? period, string? device, string userQuestion)
    {
        var metric = tool == "query_alarm_duration" ? "报警时长" : "产量";
        var when = string.IsNullOrWhiteSpace(period) ? "" : period.Trim();
        var who = string.IsNullOrWhiteSpace(device) ? "" : device.Trim();
        if (who is "全厂" or "整个厂")
            who = "";
        if (when.Length == 0 && who.Length == 0)
            return string.IsNullOrWhiteSpace(userQuestion) ? "今天的" + metric : userQuestion.Trim();
        if (when.Contains("产量", StringComparison.Ordinal)
            || when.Contains("合格率", StringComparison.Ordinal)
            || when.Contains("报警时长", StringComparison.Ordinal))
            return who.Length == 0 ? when : who + when;
        var head = who.Length == 0 ? when : who + when;
        return head + "的" + metric;
    }
}

public static class AssistantToolXml
{
    private static readonly Regex FunctionPattern = new(
        "<function\\s+name=\"([^\"]+)\">(.*?)</function>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ParamPattern = new(
        "<param\\s+name=\"([^\"]+)\">(.*?)</param>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<AssistantToolCall> ReadCalls(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("<function", StringComparison.Ordinal))
            return [];
        var calls = new List<AssistantToolCall>();
        var index = 0;
        foreach (Match function in FunctionPattern.Matches(text))
        {
            var arguments = new Dictionary<string, string>();
            foreach (Match param in ParamPattern.Matches(function.Groups[2].Value))
            {
                var value = param.Groups[2].Value.Trim();
                const string start = "<![CDATA[";
                if (value.StartsWith(start, StringComparison.Ordinal) && value.EndsWith("]]>", StringComparison.Ordinal))
                    value = value[start.Length..^3];
                arguments[param.Groups[1].Value.Trim()] = value;
            }

            calls.Add(new AssistantToolCall(
                "call_" + index,
                function.Groups[1].Value.Trim(),
                JsonSerializer.Serialize(arguments)));
            index++;
        }

        return calls;
    }
}

public interface IAssistantToolBroker
{
    IReadOnlyList<AssistantToolSpec> Tools { get; }

    string Execute(
        string name,
        string argumentsJson,
        DateTime now,
        string? selectedDeviceName,
        string userQuestion,
        CancellationToken cancellationToken);
}

public sealed class AssistantToolBroker : IAssistantToolBroker
{
    private readonly IAssistantQueryEngine _engine;
    private readonly IAssistantFactSheet _facts;

    public AssistantToolBroker(IAssistantQueryEngine engine, IAssistantFactSheet facts)
    {
        _engine = engine;
        _facts = facts;
    }

    public IReadOnlyList<AssistantToolSpec> Tools => AssistantToolCatalog.All;

    public string Execute(
        string name,
        string argumentsJson,
        DateTime now,
        string? selectedDeviceName,
        string userQuestion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var (period, device) = ReadArgs(argumentsJson);
            var text = name is "query_output" or "query_alarm_duration"
                ? Metric(name, period, device, userQuestion, now, selectedDeviceName, cancellationToken)
                : _facts.ReadSection(name, now, selectedDeviceName, period, device);
            return Trim(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return "这次没有查到。";
        }
    }

    private string Metric(
        string name,
        string period,
        string device,
        string userQuestion,
        DateTime now,
        string? selectedDeviceName,
        CancellationToken cancellationToken)
    {
        var question = AssistantToolQuestions.ForMetric(name, period, device, userQuestion);
        var text = _engine.Answer(now, question, selectedDeviceName, cancellationToken);
        return text;
    }

    private static (string Period, string Device) ReadArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ("", "");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ("", "");
            return (Text(document.RootElement, "period"), Text(document.RootElement, "device"));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Trim(string? text)
    {
        var lines = (text ?? "").Split('\n').Select(AssistantPrompt.CleanFact).OfType<string>().ToList();
        var joined = string.Join("\n", lines);
        return joined.Length == 0 ? "这次没有查到。" : joined;
    }
}
