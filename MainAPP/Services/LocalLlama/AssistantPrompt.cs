using System.Text.RegularExpressions;

namespace MainAPP.Services;

/// <summary>送给本地模型的短事实。产量表、地址和连接串不进这里。</summary>
public sealed record AssistantPromptContext(
    string PageKey,
    string PageName,
    string? DeviceName,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string>? DeviceNames = null,
    string? ManualExcerpt = null);

public sealed record AssistantHistoryFacts(DateTime From, DateTime To, IReadOnlyList<string> Notes);

/// <summary>模型只改写已经给出的事实。数字和能否操作设备都写死在这段说明里。</summary>
public static class AssistantPrompt
{
    public const string System =
        """
        你是这块生产看板的说明员。只根据用户消息里已经给出的事实回答，用提问所用的语言。
        不要编造产量、良品率、开动率或报警次数。没有给出的数，就说页面上还没有这个数。
        不要确认报警，不要开始或结束工单，不要下发配方，不要写 PLC。
        良品率 = 合格数 / (合格数 + 不良数)。
        性能率 = (合格数 + 不良数) / (额定件每小时 × 运行小时)，最高 100%。
        开动率 = 运行 / (运行 + 报警)，待机和离线不计入。
        """;

    public static string User(AssistantPromptContext context, string question)
    {
        var device = string.IsNullOrWhiteSpace(context.DeviceName) ? "未选择" : context.DeviceName.Trim();
        var range = context.From is DateTime from && context.To is DateTime to
            ? $"{from:yyyy-MM-dd HH:mm} 至 {to:yyyy-MM-dd HH:mm}"
            : "还没有历史查询时间";
        var notes = FormatList(context.Notes.Select(CleanFact).OfType<string>());
        var names = FormatList((context.DeviceNames ?? []).Select(CleanFact).OfType<string>());
        var manual = CleanBlock(context.ManualExcerpt);
        return $"""
            当前页面：{context.PageName}
            设备：{device}
            设备名单（只有名称）：
            {names}
            查询时间：{range}
            手册说明（不是本班的数）：
            {manual}
            已经算好的说明：
            {notes}
            问题：{question.Trim()}
            """;
    }

    internal static string CleanBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "（没有）";
        var lines = text.Split('\n').Select(CleanFact).OfType<string>().ToList();
        return lines.Count == 0 ? "（没有）" : string.Join("\n", lines);
    }

    internal static string? CleanFact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (value.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Data Source", StringComparison.OrdinalIgnoreCase)
            || value.Contains("://", StringComparison.Ordinal)
            || Regex.IsMatch(value, @"^[A-Za-z]{1,4}\d+(\.\d+)?$"))
            return null;
        return value;
    }

    private static string FormatList(IEnumerable<string> items)
    {
        var lines = items.Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? "（没有）" : string.Join("\n", lines.Select(item => "- " + item));
    }
}
