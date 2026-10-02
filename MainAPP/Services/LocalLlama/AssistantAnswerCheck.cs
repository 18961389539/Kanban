using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MainAPP.Services;

/// <summary>回答里的数字必须能在这次送出的事实里找到。对不上只做标记，不改回答。</summary>
public static class AssistantAnswerCheck
{
    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

    public static bool HasNumberOutside(string answer, IEnumerable<string> allowedText)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in allowedText)
            Collect(text, allowed);
        foreach (Match match in NumberPattern.Matches(answer ?? ""))
        {
            var key = Normalize(match.Value);
            if (key != null && !allowed.Contains(key))
                return true;
        }

        return false;
    }

    private static void Collect(string? text, HashSet<string> allowed)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Match match in NumberPattern.Matches(text))
        {
            var key = Normalize(match.Value);
            if (key != null)
                allowed.Add(key);
        }
    }

    private static string? Normalize(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
