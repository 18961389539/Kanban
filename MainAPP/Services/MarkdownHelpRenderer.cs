using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MainAPP.Services;

/// <summary>
/// 将项目内 Markdown 手册转为可在 WPF WebBrowser 中显示的 HTML（支持标题、列表、图片、代码块）。
/// </summary>
internal static class MarkdownHelpRenderer
{
    private static readonly Regex ImageRegex = new(
        @"!\[(?<alt>[^\]]*)\]\((?<src>[^)]+)\)",
        RegexOptions.Compiled);

    public static string RenderFile(string markdownPath, string appBaseDirectory)
    {
        var markdown = File.ReadAllText(markdownPath);
        var manualDir = Path.GetDirectoryName(markdownPath) ?? appBaseDirectory;
        var body = RenderBody(markdown, manualDir, appBaseDirectory);
        return WrapHtml(body, Path.GetFileNameWithoutExtension(markdownPath));
    }

    private static string RenderBody(string markdown, string manualDir, string appBaseDirectory)
    {
        var sb = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var codeLang = string.Empty;
        var codeBuilder = new StringBuilder();
        var inList = false;
        var inTable = false;
        var tableHeaderPending = false;
        var inQuote = false;
        var quoteBuilder = new StringBuilder();

        void CloseList()
        {
            if (!inList) return;
            sb.AppendLine("</ul>");
            inList = false;
        }

        void CloseQuote()
        {
            if (!inQuote) return;
            sb.Append("<blockquote>").Append(quoteBuilder).AppendLine("</blockquote>");
            inQuote = false;
            quoteBuilder.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // 离开表格（遇到非表格行）时闭合 <table>，避免表格吞掉后续内容
            if (inTable && !line.StartsWith('|'))
            {
                sb.AppendLine("</table>");
                inTable = false;
                tableHeaderPending = false;
            }

            // 离开引用块（遇到非引用行）时闭合 <blockquote>
            if (inQuote && !line.StartsWith('>'))
            {
                CloseQuote();
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    CloseList();
                    inCode = true;
                    codeLang = line.Length > 3 ? line[3..].Trim() : string.Empty;
                    codeBuilder.Clear();
                }
                else
                {
                    var cssClass = codeLang.Equals("mermaid", StringComparison.OrdinalIgnoreCase)
                        ? "code mermaid"
                        : "code";
                    sb.Append("<pre class=\"").Append(cssClass).Append("\">")
                        .Append(WebUtility.HtmlEncode(codeBuilder.ToString().TrimEnd()))
                        .AppendLine("</pre>");
                    inCode = false;
                    codeLang = string.Empty;
                }

                continue;
            }

            if (inCode)
            {
                codeBuilder.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList();
                CloseQuote();
                continue;
            }

            if (line.StartsWith('#'))
            {
                CloseList();
                var level = line.TakeWhile(c => c == '#').Count();
                level = Math.Clamp(level, 1, 4);
                var rawTitle = line[level..].Trim();
                var text = InlineFormat(rawTitle, manualDir, appBaseDirectory);
                var anchor = Slugify(rawTitle);
                if (anchor.Length > 0)
                    sb.Append("<h").Append(level).Append(" id=\"").Append(anchor).Append("\">").Append(text).Append("</h").Append(level).AppendLine(">");
                else
                    sb.Append("<h").Append(level).Append('>').Append(text).Append("</h").Append(level).AppendLine(">");
                continue;
            }

            if (line.StartsWith('>'))
            {
                CloseList();
                if (!inQuote)
                {
                    quoteBuilder.Clear();
                    inQuote = true;
                }
                else
                {
                    quoteBuilder.Append("<br/>");
                }
                var text = InlineFormat(line[1..].Trim(), manualDir, appBaseDirectory);
                quoteBuilder.Append(text);
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (!inList)
                {
                    sb.AppendLine("<ul>");
                    inList = true;
                }

                var text = InlineFormat(line[2..].Trim(), manualDir, appBaseDirectory);
                sb.Append("<li>").Append(text).AppendLine("</li>");
                continue;
            }

            if (line.StartsWith('|'))
            {
                CloseList();
                if (!inTable)
                {
                    sb.AppendLine("<table>");
                    inTable = true;
                    tableHeaderPending = true;
                }
                // 跳过 Markdown 表格分隔行（| --- | --- |）
                if (IsTableSeparatorRow(line))
                {
                    tableHeaderPending = false;
                    continue;
                }
                var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);
                var tag = tableHeaderPending ? "th" : "td";
                tableHeaderPending = false;
                sb.Append("<tr>");
                foreach (var cell in cells)
                    sb.Append('<').Append(tag).Append('>').Append(InlineFormat(cell, manualDir, appBaseDirectory)).Append("</").Append(tag).Append('>');
                sb.AppendLine("</tr>");
                continue;
            }

            CloseList();
            var paragraph = InlineFormat(line.Trim(), manualDir, appBaseDirectory);
            sb.Append("<p>").Append(paragraph).AppendLine("</p>");
        }

        CloseList();
        CloseQuote();
        if (inCode)
        {
            sb.Append("<pre class=\"code\">")
                .Append(WebUtility.HtmlEncode(codeBuilder.ToString().TrimEnd()))
                .AppendLine("</pre>");
        }

        return sb.ToString();
    }

    private static string InlineFormat(string text, string manualDir, string appBaseDirectory)
    {
        text = WebUtility.HtmlEncode(text);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"`([^`]+)`", "<code>$1</code>");
        text = ImageRegex.Replace(text, match =>
        {
            // text 整体已 HtmlEncode，这里拿到的 alt/src 已经是编码后的值，直接复用，避免二次编码。
            var alt = match.Groups["alt"].Value;
            var src = ResolveImageUrl(match.Groups["src"].Value, manualDir, appBaseDirectory);
            return $"<img alt=\"{alt}\" src=\"{src}\" />";
        });
        text = Regex.Replace(text, @"\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)",
            m =>
            {
                // url 同样已是编码后的值，直接复用。
                var label = m.Groups["label"].Value;
                var url = m.Groups["url"].Value;
                return $"<a href=\"{url}\">{label}</a>";
            });
        return text;
    }

    /// <summary>
    /// 判断是否为 Markdown 表格分隔行（只含 <c>|</c>、<c>-</c>、<c>:</c>、空格）。仅按整行内容判断，
    /// 避免把含 "---" 的正常单元格内容误判为分隔行。
    /// </summary>
    private static bool IsTableSeparatorRow(string line)
    {
        var content = line.Trim('|');
        if (content.Length == 0) return false;
        var hasDash = false;
        foreach (var ch in content)
        {
            if (ch is '-' or ':' or ' ')
                hasDash |= ch == '-';
            else
                return false;
        }
        return hasDash;
    }

    /// <summary>
    /// 生成标题锚点 id（供页内 <c>[文字](#anchor)</c> 链接跳转）。
    /// 规则：字母转小写；字母/数字/汉字等字母类字符保留；空格与连字符转 <c>-</c>；其余标点（含全角括号、冒号、句点）移除。
    /// 例如 "附录 B：工程师与管理员补充" → "附录-b工程师与管理员补充"，"12.7 运行模式（通用设置）" → "127-运行模式通用设置"。
    /// </summary>
    private static string Slugify(string text)
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
            // 其余标点直接忽略
        }
        return sb.ToString().Trim('-');
    }

    private static string ResolveImageUrl(string src, string manualDir, string appBaseDirectory)
    {
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return WebUtility.HtmlEncode(src);

        var normalized = src.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalized))
        {
            var fromBase = Path.GetFullPath(Path.Combine(appBaseDirectory, normalized));
            if (File.Exists(fromBase))
                return new Uri(fromBase).AbsoluteUri;
        }

        var fromManual = Path.GetFullPath(Path.Combine(manualDir, normalized));
        if (File.Exists(fromManual))
            return new Uri(fromManual).AbsoluteUri;

        var fileName = Path.GetFileName(normalized);
        var fallback = Path.Combine(appBaseDirectory, "screenshots", fileName);
        if (File.Exists(fallback))
            return new Uri(fallback).AbsoluteUri;

        return WebUtility.HtmlEncode(src);
    }

    private static string WrapHtml(string body, string title)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        return $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta http-equiv="X-UA-Compatible" content="IE=edge" />
<title>{{safeTitle}}</title>
<style>
body { font-family: "Microsoft YaHei", Segoe UI, sans-serif; margin: 24px 32px 48px; background: #1A2029; color: #E5E7EB; line-height: 1.65; }
h1,h2,h3,h4 { color: #F3F4F6; margin-top: 1.4em; margin-bottom: 0.6em; }
h1 { font-size: 28px; border-bottom: 1px solid #374151; padding-bottom: 8px; }
h2 { font-size: 22px; }
h3 { font-size: 18px; color: #D1D5DB; }
p, li { font-size: 15px; }
blockquote { border-left: 4px solid #3B82F6; margin: 12px 0; padding: 8px 16px; background: #111827; color: #CBD5E1; }
ul { padding-left: 24px; }
img { max-width: 100%; height: auto; border: 1px solid #374151; border-radius: 6px; margin: 12px 0; box-shadow: 0 8px 24px rgba(0,0,0,.35); }
code { background: #111827; padding: 2px 6px; border-radius: 4px; }
pre.code { background: #0F172A; padding: 12px 16px; border-radius: 6px; overflow-x: auto; white-space: pre-wrap; }
pre.mermaid { background: #111827; color: #94A3B8; font-size: 13px; }
a { color: #60A5FA; }
table { width: 100%; border-collapse: collapse; margin: 12px 0; }
th, td { border: 1px solid #374151; padding: 6px 10px; font-size: 13px; text-align: left; vertical-align: top; }
th { background: #111827; color: #F3F4F6; }
strong { color: #F9FAFB; }
</style>
</head>
<body>
{{body}}
</body>
</html>
""";
    }
}
