using System.Text;

namespace MainAPP.Services;

/// <summary>一段说明里的一段连续文字。星号包住的部分是控件名，侧栏加粗显示。</summary>
public sealed class PageHelpRun
{
    public string Text { get; init; } = "";
    public bool IsBold { get; init; }
}

/// <summary>颜色对照表的一行。</summary>
public sealed class PageHelpTableRow
{
    public bool IsHeader { get; init; }
    public IReadOnlyList<PageHelpTableCell> Cells { get; init; } = [];
}

/// <summary>颜色对照表的一格。</summary>
public sealed class PageHelpTableCell
{
    public IReadOnlyList<PageHelpRun> Runs { get; init; } = [];
}

/// <summary>本页说明的一段。标题、项目符号、步骤和表格分开，便于侧栏排版。</summary>
public sealed class PageHelpBlock
{
    public bool IsHeading { get; init; }
    public bool IsBullet { get; init; }
    public bool IsOrdered { get; init; }
    public bool IsTable { get; init; }
    public int Number { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<PageHelpRun> Runs { get; init; } = [];
    public IReadOnlyList<PageHelpTableRow> Rows { get; init; } = [];
}

/// <summary>一个 <c>###</c> 小节。侧栏一次只展开一节。</summary>
public sealed class PageHelpSection
{
    public string Title { get; init; } = "";
    public IReadOnlyList<PageHelpBlock> Blocks { get; init; } = [];
}

/// <summary>一页说明：第一段标题之前的文字始终可见，后面按小节折叠。</summary>
public sealed class PageHelpDocument
{
    public IReadOnlyList<PageHelpBlock> Intro { get; init; } = [];
    public IReadOnlyList<PageHelpSection> Sections { get; init; } = [];
}

/// <summary>
/// 从使用手册 Markdown 中取出某一页的说明。
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
    public const string Assistant = "assistant";
    public const string Login = "login";

    public static IReadOnlyList<PageHelpBlock> Extract(string? markdown, string? key)
    {
        var document = ExtractDocument(markdown, key);
        List<PageHelpBlock> blocks = [..document.Intro];
        foreach (var section in document.Sections)
        {
            blocks.Add(new PageHelpBlock
            {
                IsHeading = true,
                Text = section.Title,
                Runs = ParseRuns(section.Title)
            });
            blocks.AddRange(section.Blocks);
        }

        return blocks;
    }

    public static PageHelpDocument ExtractDocument(string? markdown, string? key)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(key))
            return new PageHelpDocument();

        var open = $"<!-- page-help:{key} -->";
        const string close = "<!-- /page-help -->";
        var inside = false;
        List<PageHelpBlock> intro = [];
        List<PageHelpSection> sections = [];
        List<PageHelpBlock>? current = null;
        List<string> tableLines = [];

        void FlushTable()
        {
            if (tableLines.Count == 0)
                return;
            Add(ParseTable(tableLines));
            tableLines.Clear();
        }

        void Add(PageHelpBlock block)
        {
            if (current is null)
                intro.Add(block);
            else
                current.Add(block);
        }

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
            {
                FlushTable();
                continue;
            }

            if (line.StartsWith('|'))
            {
                tableLines.Add(line);
                continue;
            }

            FlushTable();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                current = [];
                sections.Add(new PageHelpSection
                {
                    Title = PlainText(line[4..].Trim()),
                    Blocks = current
                });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var text = line[2..].Trim();
                Add(new PageHelpBlock
                {
                    IsBullet = true,
                    Text = PlainText(text),
                    Runs = ParseRuns(text)
                });
                continue;
            }

            if (TryOrderedItem(line, out var number, out var step))
            {
                Add(new PageHelpBlock
                {
                    IsOrdered = true,
                    Number = number,
                    Text = PlainText(step),
                    Runs = ParseRuns(step)
                });
                continue;
            }

            Add(new PageHelpBlock
            {
                Text = PlainText(line),
                Runs = ParseRuns(line)
            });
        }

        FlushTable();
        return new PageHelpDocument { Intro = intro, Sections = sections };
    }

    /// <summary>页首说明，不含后面的小节和表格。给本地模型看时标成手册，避免把举例当成这一班的数。</summary>
    public static string IntroExcerpt(string? markdown, string? key, int maxChars = 800)
    {
        if (maxChars <= 0)
            return "";
        var text = string.Join("\n", ExtractDocument(markdown, key).Intro
            .Select(block => block.Text)
            .Where(line => !string.IsNullOrWhiteSpace(line))).Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    /// <summary>标记上方最近一个二级标题的锚点，与手册窗口里的标题 id 相同。</summary>
    public static string? ChapterAnchor(string? markdown, string? key)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(key))
            return null;

        var open = $"<!-- page-help:{key} -->";
        string? current = null;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
                current = Slugify(line[3..].Trim());
            else if (line == open)
                return current;
        }

        return null;
    }

    public static string? HelpKeyForNavigation(string? pageKey) => pageKey switch
    {
        "Home" => Home,
        "ProductionLine" => ProductionLine,
        "AlarmCenter" => AlarmCenter,
        "WorkOrder" => WorkOrders,
        "Overview" => Overview,
        "HistoryQuery" => History,
        "Assistant" => Assistant,
        "DeviceManager" => DeviceManager,
        "RecipeManager" => Recipes,
        "Settings" => Settings,
        "RuntimeMonitoring" => RuntimeMonitor,
        "UserManager" => Users,
        "Audit" => Audit,
        "DataSourceMonitoring" => DataSource,
        "DeviceDetail" => DeviceDetail,
        _ => null
    };

    /// <summary>
    /// 生成标题锚点 id。字母转小写；字母、数字和汉字保留；空格与连字符转 <c>-</c>；其余标点移除。
    /// 例如 "12.7 运行模式（通用设置）" → "127-运行模式通用设置"。
    /// </summary>
    public static string Slugify(string text)
    {
        var sb = new StringBuilder();
        var lastWasDash = false;
        foreach (var ch in text)
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || category == System.Globalization.UnicodeCategory.OtherLetter)
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
            {
                if (sb.Length > 0 && !lastWasDash)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
        }

        return sb.ToString().Trim('-');
    }

    public static IReadOnlyList<PageHelpRun> ParseRuns(string text)
    {
        List<PageHelpRun> runs = [];
        var bold = false;
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                if (sb.Length > 0)
                {
                    runs.Add(new PageHelpRun { Text = sb.ToString(), IsBold = bold });
                    sb.Clear();
                }

                bold = !bold;
                i++;
                continue;
            }

            sb.Append(text[i]);
        }

        if (sb.Length > 0)
            runs.Add(new PageHelpRun { Text = sb.ToString(), IsBold = bold });
        if (runs.Count == 0)
            runs.Add(new PageHelpRun { Text = "" });
        return runs;
    }

    private static string PlainText(string text)
        => string.Concat(ParseRuns(text).Select(run => run.Text));

    private static bool TryOrderedItem(string line, out int number, out string text)
    {
        number = 0;
        text = "";
        var i = 0;
        if (line.Length == 0 || !char.IsDigit(line[0]))
            return false;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        if (i >= line.Length || line[i] != '.')
            return false;
        if (i + 1 >= line.Length || line[i + 1] != ' ')
            return false;
        if (!int.TryParse(line[..i], out number))
            return false;
        text = line[(i + 2)..].Trim();
        return text.Length > 0;
    }

    private static PageHelpBlock ParseTable(List<string> lines)
    {
        var hasSeparator = lines.Exists(IsTableSeparatorRow);
        List<PageHelpTableRow> rows = [];
        var headerPending = hasSeparator;
        foreach (var line in lines)
        {
            if (IsTableSeparatorRow(line))
            {
                headerPending = false;
                continue;
            }

            var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);
            rows.Add(new PageHelpTableRow
            {
                IsHeader = headerPending,
                Cells = cells.Select(cell => new PageHelpTableCell { Runs = ParseRuns(cell) }).ToArray()
            });
            headerPending = false;
        }

        return new PageHelpBlock { IsTable = true, Rows = rows };
    }

    private static bool IsTableSeparatorRow(string line)
    {
        var content = line.Trim('|');
        if (content.Length == 0)
            return false;
        var hasDash = false;
        foreach (var ch in content)
        {
            if (ch is '-' or ':' or ' ' or '|')
                hasDash |= ch == '-';
            else
                return false;
        }

        return hasDash;
    }
}
