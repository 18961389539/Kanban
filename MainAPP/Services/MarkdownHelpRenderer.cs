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

        void CloseList()
        {
            if (!inList) return;
            sb.AppendLine("</ul>");
            inList = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

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
                continue;
            }

            if (line.StartsWith('#'))
            {
                CloseList();
                var level = line.TakeWhile(c => c == '#').Count();
                level = Math.Clamp(level, 1, 4);
                var text = InlineFormat(line[level..].Trim(), manualDir, appBaseDirectory);
                sb.Append("<h").Append(level).Append('>').Append(text).Append("</h").Append(level).AppendLine(">");
                continue;
            }

            if (line.StartsWith("> "))
            {
                CloseList();
                var text = InlineFormat(line[2..].Trim(), manualDir, appBaseDirectory);
                sb.Append("<blockquote>").Append(text).AppendLine("</blockquote>");
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
                // 跳过 Markdown 表格分隔行
                if (line.Contains("---")) continue;
                var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);
                sb.Append("<p class=\"table-row\">")
                    .Append(string.Join(" · ", cells.Select(c => InlineFormat(c, manualDir, appBaseDirectory))))
                    .AppendLine("</p>");
                continue;
            }

            CloseList();
            var paragraph = InlineFormat(line.Trim(), manualDir, appBaseDirectory);
            sb.Append("<p>").Append(paragraph).AppendLine("</p>");
        }

        CloseList();
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
            var alt = WebUtility.HtmlEncode(match.Groups["alt"].Value);
            var src = ResolveImageUrl(match.Groups["src"].Value, manualDir, appBaseDirectory);
            return $"<img alt=\"{alt}\" src=\"{src}\" />";
        });
        text = Regex.Replace(text, @"\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)",
            m =>
            {
                var label = m.Groups["label"].Value;
                var url = WebUtility.HtmlEncode(m.Groups["url"].Value);
                return $"<a href=\"{url}\">{label}</a>";
            });
        return text;
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
.table-row { color: #CBD5E1; font-size: 14px; }
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
