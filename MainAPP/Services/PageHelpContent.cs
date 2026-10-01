namespace MainAPP.Services;

/// <summary>本页说明的一段文字。项目符号与普通段落分开，便于侧栏排版。</summary>
public sealed class PageHelpBlock
{
    public bool IsHeading { get; init; }
    public bool IsBullet { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// 从使用手册 Markdown 中取出某一页的短说明。
/// 正文写在 <c>&lt;!-- page-help:键 --&gt;</c> 与 <c>&lt;!-- /page-help --&gt;</c> 之间，手册仍是唯一来源。
/// </summary>
public static class PageHelpContent
{
    public const string Home = "home";
    public const string AlarmCenter = "alarm-center";
    public const string History = "history";
    public const string HistoryOee = "history-oee";
    public const string ProductionLine = "production-line";
    public const string WorkOrders = "work-orders";
    public const string Overview = "overview";
    public const string DeviceDetail = "device-detail";
    public const string RuntimeMonitor = "runtime-monitor";
    public const string DeviceManager = "device-manager";
    public const string Settings = "settings";
    public const string Recipes = "recipes";
    public const string Users = "users";
    public const string Audit = "audit";
    public const string DataSource = "data-source";
    public const string Login = "login";

    public static IReadOnlyList<PageHelpBlock> Extract(string? markdown, string? key)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(key))
            return [];

        var open = $"<!-- page-help:{key} -->";
        const string close = "<!-- /page-help -->";
        var inside = false;
        List<PageHelpBlock> blocks = [];
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!inside)
            {
                if (line == open)
                    inside = true;
                continue;
            }

            if (line == close)
                break;
            if (line.Length == 0)
                continue;
            if (line.StartsWith("### ", StringComparison.Ordinal))
                blocks.Add(new PageHelpBlock { IsHeading = true, Text = line[4..].Trim() });
            else if (line.StartsWith("- ", StringComparison.Ordinal))
                blocks.Add(new PageHelpBlock { IsBullet = true, Text = line[2..].Trim() });
            else
                blocks.Add(new PageHelpBlock { Text = line });
        }

        return blocks;
    }
}
