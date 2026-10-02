using System.Globalization;
using System.Text.RegularExpressions;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.ViewModels;

namespace MainAPP.Services;

public enum AssistantTimeKind
{
    Calendar,
    CurrentShift,
    PreviousShift,
}

public readonly record struct AssistantWindow(AssistantTimeKind Kind, DateTime From, DateTime To);

/// <summary>问句收成的一次计算。任何日期都是时间窗，不按「昨天」「前天」各写一套回答。</summary>
public readonly record struct AssistantAsk(
    bool Pieces,
    bool AlarmDuration,
    bool RankLowestQuality,
    bool Plant,
    IReadOnlyList<string> DeviceNames,
    bool UseSelectedDevice,
    IReadOnlyList<AssistantWindow> Windows);

/// <summary>把问句解析成时间窗、指标和范围，再用同一套窗口差分写成固定句子。</summary>
public static class AssistantQuery
{
    public const int DefaultRetentionDays = 365;

    public static IReadOnlyList<AssistantWindow> ReadWindows(string? question, DateTime now)
    {
        var windows = new List<AssistantWindow>();
        CollectTimes((question ?? "").Trim(), now, windows);
        return windows
            .GroupBy(window => (window.Kind, window.From, window.To))
            .Select(group => group.First())
            .ToList();
    }

    public static bool TryParse(
        string? question,
        DateTime now,
        IReadOnlyList<string>? deviceNames,
        string? selectedDeviceName,
        out AssistantAsk ask)
    {
        ask = default;
        var text = (question ?? "").Trim();
        if (text.Length == 0)
            return false;

        var pieces = HasAny(text, "产量", "生产情况", "多少件", "几件", "件数", "出来高", "生産", "production", "output", "pieces", "produção", "producao");
        var quality = HasAny(text, "合格率", "良品率", "quality", "qualidade", "品質");
        var alarm = HasAny(text, "报警时长", "alarm duration", "アラーム時間", "duração do alarme", "duracao do alarme");
        if (!pieces && !quality && !alarm)
            return false;

        var windows = new List<AssistantWindow>();
        CollectTimes(text, now, windows);
        var distinct = windows
            .GroupBy(window => (window.Kind, window.From, window.To))
            .Select(group => group.First())
            .ToList();
        if (distinct.Count == 0)
            return false;

        var useSelected = HasAny(text, "这台", "当前设备", "选中的设备", "选中设备", "this device", "selected device");
        if (useSelected && string.IsNullOrWhiteSpace(selectedDeviceName))
            return false;

        var names = MatchDevices(text, deviceNames);
        var plant = HasAny(text, "全厂", "whole plant", "工場全体") || (names.Count == 0 && !useSelected);

        ask = new AssistantAsk(
            pieces || quality,
            alarm,
            (pieces || quality) && HasAny(text, "哪台", "哪一台", "最低", "拖后腿", "which device", "lowest", "最も低い", "どの設備", "menor"),
            plant,
            names,
            useSelected,
            distinct);
        return true;
    }

    public static int RetentionDays()
    {
        var raw = Environment.GetEnvironmentVariable("KANBAN_HISTORY_RETENTION_DAYS");
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultRetentionDays;
        return int.TryParse(raw, out var days) && days >= 0 && days <= 36500
            ? days
            : DefaultRetentionDays;
    }

    internal readonly record struct ResolvedWindow(DateTime From, DateTime To, bool Shift, string ShiftName, string? Problem);

    internal static List<ResolvedWindow> ResolveWindows(
        AssistantAsk ask,
        DateTime now,
        IReadOnlyList<ShiftConfig> shifts,
        int retentionDays)
    {
        var resolved = new List<ResolvedWindow>();
        foreach (var window in ask.Windows)
        {
            if (window.Kind == AssistantTimeKind.CurrentShift)
            {
                var (shift, start, _) = ShiftConfigResolver.ResolveCurrentShift(shifts, now);
                resolved.Add(shift == null
                    ? new ResolvedWindow(now, now, true, "", "这个班次没有排班，算不了。")
                    : Finish(start, now, true, shift.Name, now, retentionDays));
                continue;
            }

            if (window.Kind == AssistantTimeKind.PreviousShift)
            {
                if (!TryPreviousShift(shifts, now, out var name, out var from, out var to))
                    resolved.Add(new ResolvedWindow(now, now, true, "", "这个班次没有排班，算不了。"));
                else
                    resolved.Add(Finish(from, to, true, name, now, retentionDays));
                continue;
            }

            resolved.Add(Finish(window.From, window.To, false, "", now, retentionDays));
        }

        return resolved;
    }

    internal static string Render(
        AssistantAsk ask,
        IReadOnlyList<Device> devices,
        string? selectedDeviceName,
        IReadOnlyList<ResolvedWindow> windows,
        IReadOnlyDictionary<string, List<ProductionLog>>? production,
        bool productionFailed,
        IReadOnlyDictionary<string, List<StatusTransitionRecord>>? transitions,
        bool statusFailed)
    {
        var scopes = new List<Device?>();
        if (ask.Plant)
            scopes.Add(null);
        foreach (var name in ask.DeviceNames)
        {
            var device = devices.FirstOrDefault(item => string.Equals(item.Name?.Trim(), name, StringComparison.Ordinal));
            if (device != null && !scopes.Contains(device))
                scopes.Add(device);
        }

        if (ask.UseSelectedDevice)
        {
            var selected = devices.FirstOrDefault(item =>
                string.Equals(item.Name?.Trim(), selectedDeviceName?.Trim(), StringComparison.Ordinal));
            if (selected == null)
                return "没有这台设备，算不了。";
            if (!scopes.Contains(selected))
                scopes.Add(selected);
        }

        if (scopes.Count == 0)
            scopes.Add(null);

        var lines = new List<string>();
        var totals = new List<(DateTime From, DateTime To, int Total)>();
        foreach (var window in windows)
        {
            if (window.Problem != null)
            {
                lines.Add(window.Problem);
                continue;
            }

            foreach (var scope in scopes)
            {
                if (ask.Pieces)
                {
                    lines.Add(productionFailed
                        ? $"{Clock(window)}，{ScopeName(scope)}：这次没有查到。"
                        : PieceLine(window, scope, devices, production, totals));
                }

                if (ask.AlarmDuration)
                    lines.Add(AlarmLine(window, scope, devices, transitions, statusFailed));
            }

            if (ask.RankLowestQuality && ask.Pieces && !productionFailed && scopes.Any(scope => scope == null))
                lines.Add(RankLine(window, devices, production));
            else if (ask.RankLowestQuality && ask.Pieces && !productionFailed && scopes.Count(scope => scope != null) > 1)
                lines.Add(RankLine(window, scopes.Where(scope => scope != null).Cast<Device>().ToList(), production));
        }

        if (ask.Windows.Count == 2 && scopes.Count == 1 && totals.Count == 2)
            lines.Add(DeltaLine(totals[0], totals[1]));
        return string.Join("\n", lines.Where(line => line.Length > 0));
    }

    internal static (int Ok, int Ng) WindowOutput(IReadOnlyList<ProductionLog> logs, DateTime from, DateTime to)
    {
        var ordered = logs.OrderBy(log => log.Timestamp).ToList();
        var inWindow = ordered.Where(log => log.Timestamp >= from && log.Timestamp <= to).ToList();
        if (inWindow.Count == 0)
            return (0, 0);
        var baseline = ordered.Where(log => log.Timestamp < from).ToList();
        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(inWindow, baseline, from);
        if (inWindow.Count == 1
            && HistoryQueryHelper.FindBaselineBeforeWindow(baseline, inWindow[0].ShiftName) == null)
        {
            ok = Math.Max(0, inWindow[0].OkProduction);
            ng = Math.Max(0, inWindow[0].NgProduction);
        }

        return (ok, ng);
    }

    private static ResolvedWindow Finish(
        DateTime from,
        DateTime to,
        bool shift,
        string? shiftName,
        DateTime now,
        int retentionDays)
    {
        if (to > now)
            to = now;
        var name = string.IsNullOrWhiteSpace(shiftName) ? "班次" : shiftName.Trim();
        if (from > now)
            return new ResolvedWindow(from, to, shift, name, "这个时间还没到，没有查到。");
        var floor = now.Date.AddDays(-retentionDays);
        if (retentionDays > 0 && from < floor)
            return new ResolvedWindow(from, to, shift, name, $"{from:yyyy-MM-dd HH:mm} 至 {to:yyyy-MM-dd HH:mm} 超出保留的 {retentionDays} 天，没有查到。");
        return new ResolvedWindow(from, to, shift, name, null);
    }

    private static string PieceLine(
        ResolvedWindow window,
        Device? scope,
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<ProductionLog>>? production,
        List<(DateTime From, DateTime To, int Total)> totals)
    {
        var (ok, ng) = Sum(scope, devices, production, window.From, window.To);
        var total = ok + ng;
        totals.Add((window.From, window.To, total));
        var clock = $"{Clock(window)}，{ScopeName(scope)}：合格 {ok} 件，不良 {ng} 件，合计 {total} 件";
        var tail = window.Shift ? "这是班次窗口差分，不是首页累计。" : "这是窗口差分。";
        if (total == 0)
            return $"{clock}。这段没有产量，没有良品率。{tail}";
        return $"{clock}，良品率 {Percent(OeeCalculator.CalculateQualityRate(ok, ng))}。{tail}";
    }

    private static string RankLine(
        ResolvedWindow window,
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<ProductionLog>>? production)
    {
        var rows = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .Select(device =>
            {
                var (ok, ng) = Sum(device, devices, production, window.From, window.To);
                var total = ok + ng;
                return (Name: device.Name.Trim(), Total: total, Rate: total == 0 ? double.NaN : OeeCalculator.CalculateQualityRate(ok, ng));
            })
            .Where(row => row.Total > 0)
            .ToList();
        if (rows.Count == 0)
            return $"{Clock(window)}，没有产量，排不出合格率最低的设备。";
        var lowest = rows.Min(row => row.Rate);
        var names = rows.Where(row => row.Rate == lowest).Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        return $"{Clock(window)}，合格率最低的是 {string.Join("、", names)}，{Percent(lowest)}。只在有产量的设备里比。";
    }

    private static string AlarmLine(
        ResolvedWindow window,
        Device? scope,
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<StatusTransitionRecord>>? transitions,
        bool statusFailed)
    {
        var clock = $"{Clock(window)}，{ScopeName(scope)}";
        if (statusFailed || transitions == null)
            return $"{clock}：报警时长这次没有查到。";
        var pool = scope == null ? devices : [scope];
        double alarm = 0;
        var counted = 0;
        var missing = 0;
        string? worstName = null;
        var worst = 0d;
        foreach (var device in pool)
        {
            if (!transitions.TryGetValue(device.Id, out var list) || list.Count == 0)
            {
                missing++;
                continue;
            }

            var ordered = list.OrderBy(item => item.EventTime).ToList();
            var durations = OeeCalculator.CalculateStateDurations(ordered, window.From, window.To, ordered[0].PreviousState);
            alarm += durations.AlarmTime;
            counted++;
            if (durations.AlarmTime > worst && !string.IsNullOrWhiteSpace(device.Name))
            {
                worst = durations.AlarmTime;
                worstName = device.Name.Trim();
            }
        }

        if (counted == 0)
            return $"{clock}：没有状态切换记录，没有算出报警时长。";
        var text = $"{clock}：有状态切换的 {counted} 台，报警时长合计 {Hours(alarm)} 小时。";
        if (worstName != null && worst > 0)
            text += $"最长的是 {worstName}，{Hours(worst)} 小时。";
        if (missing > 0)
            text += $"另有 {missing} 台没有状态切换，没有计入。";
        return text + "这是状态里的报警时长，不是报警次数。";
    }

    private static (int Ok, int Ng) Sum(
        Device? scope,
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<ProductionLog>>? production,
        DateTime from,
        DateTime to)
    {
        var pool = scope == null ? devices : [scope];
        var ok = 0;
        var ng = 0;
        foreach (var device in pool)
        {
            List<ProductionLog>? logs = null;
            production?.TryGetValue(device.Id, out logs);
            var piece = WindowOutput(logs ?? [], from, to);
            ok += piece.Ok;
            ng += piece.Ng;
        }

        return (ok, ng);
    }

    private static string DeltaLine(
        (DateTime From, DateTime To, int Total) first,
        (DateTime From, DateTime To, int Total) second)
    {
        var earlier = first.From <= second.From ? first : second;
        var later = first.From <= second.From ? second : first;
        var gap = later.Total - earlier.Total;
        var left = $"{later.From:yyyy-MM-dd HH:mm} 至 {later.To:yyyy-MM-dd HH:mm}";
        var right = $"{earlier.From:yyyy-MM-dd HH:mm} 至 {earlier.To:yyyy-MM-dd HH:mm}";
        if (gap == 0)
            return $"{left} 和 {right} 一样多，都是 {later.Total} 件。两段都是窗口差分。";
        var word = gap > 0 ? "多" : "少";
        return $"{left} 比 {right} {word} {Math.Abs(gap)} 件。两段都是窗口差分。";
    }

    private static string Clock(ResolvedWindow window)
        => window.Shift
            ? $"班次窗 {window.ShiftName}（{window.From:yyyy-MM-dd HH:mm} 至 {window.To:yyyy-MM-dd HH:mm}）"
            : $"{window.From:yyyy-MM-dd HH:mm} 至 {window.To:yyyy-MM-dd HH:mm}";

    private static string ScopeName(Device? scope)
        => scope == null || string.IsNullOrWhiteSpace(scope.Name) ? "全厂" : scope.Name.Trim();

    private static string Percent(double rate)
        => (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Hours(double seconds)
        => (seconds / 3600d).ToString("0.0", CultureInfo.InvariantCulture);

    private static bool TryPreviousShift(
        IReadOnlyList<ShiftConfig> shifts,
        DateTime now,
        out string name,
        out DateTime from,
        out DateTime to)
    {
        name = "";
        from = default;
        to = default;
        var (current, index) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts.Count == 0 || index < 0)
            return false;
        var previous = shifts[(index - 1 + shifts.Count) % shifts.Count];
        var range = previous.ResolveRange(current.ResolveRange(now).Start.AddMinutes(-1));
        name = string.IsNullOrWhiteSpace(previous.Name) ? "上一班" : previous.Name.Trim();
        from = range.Start;
        to = range.End;
        return true;
    }

    private static void CollectTimes(string text, DateTime now, List<AssistantWindow> windows)
    {
        var hits = new List<(int Start, int Length, AssistantWindow Window)>();
        void Phrase(string phrase, AssistantWindow window)
        {
            var index = 0;
            while ((index = text.IndexOf(phrase, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                hits.Add((index, phrase.Length, window));
                index += phrase.Length;
            }
        }

        Phrase("大前天", Day(now.Date.AddDays(-3), now));
        Phrase("前天", Day(now.Date.AddDays(-2), now));
        Phrase("昨天", Day(now.Date.AddDays(-1), now));
        Phrase("昨日", Day(now.Date.AddDays(-1), now));
        Phrase("今天", Open(now.Date, now));
        Phrase("今日", Open(now.Date, now));
        Phrase("当前班次", Shift(AssistantTimeKind.CurrentShift));
        Phrase("这一班", Shift(AssistantTimeKind.CurrentShift));
        Phrase("本班", Shift(AssistantTimeKind.CurrentShift));
        Phrase("上一班", Shift(AssistantTimeKind.PreviousShift));
        Phrase("今年", Open(new DateTime(now.Year, 1, 1), now));
        Phrase("去年", Year(now.Year - 1, now));
        Phrase("前年", Year(now.Year - 2, now));
        Phrase("这个月", Open(new DateTime(now.Year, now.Month, 1), now));
        Phrase("本月", Open(new DateTime(now.Year, now.Month, 1), now));
        Phrase("上个月", Month(now.AddMonths(-1), now));
        Phrase("上月", Month(now.AddMonths(-1), now));
        Phrase("本周", Open(WeekStart(now), now));
        Phrase("这周", Open(WeekStart(now), now));
        Phrase("上个星期", Week(WeekStart(now).AddDays(-7), now));
        Phrase("上星期", Week(WeekStart(now).AddDays(-7), now));
        Phrase("上周", Week(WeekStart(now).AddDays(-7), now));
        Phrase("the day before yesterday", Day(now.Date.AddDays(-2), now));
        Phrase("day before yesterday", Day(now.Date.AddDays(-2), now));
        Phrase("yesterday", Day(now.Date.AddDays(-1), now));
        Phrase("today", Open(now.Date, now));
        Phrase("this shift", Shift(AssistantTimeKind.CurrentShift));
        Phrase("current shift", Shift(AssistantTimeKind.CurrentShift));
        Phrase("previous shift", Shift(AssistantTimeKind.PreviousShift));
        Phrase("last shift", Shift(AssistantTimeKind.PreviousShift));
        Phrase("this year", Open(new DateTime(now.Year, 1, 1), now));
        Phrase("last year", Year(now.Year - 1, now));
        Phrase("this month", Open(new DateTime(now.Year, now.Month, 1), now));
        Phrase("last month", Month(now.AddMonths(-1), now));
        Phrase("this week", Open(WeekStart(now), now));
        Phrase("last week", Week(WeekStart(now).AddDays(-7), now));
        Phrase("一昨日", Day(now.Date.AddDays(-2), now));
        Phrase("今日", Open(now.Date, now));
        Phrase("昨日", Day(now.Date.AddDays(-1), now));
        Phrase("このシフト", Shift(AssistantTimeKind.CurrentShift));
        Phrase("前のシフト", Shift(AssistantTimeKind.PreviousShift));
        Phrase("anteontem", Day(now.Date.AddDays(-2), now));
        Phrase("ontem", Day(now.Date.AddDays(-1), now));
        Phrase("hoje", Open(now.Date, now));
        Phrase("neste turno", Shift(AssistantTimeKind.CurrentShift));
        Phrase("este turno", Shift(AssistantTimeKind.CurrentShift));
        Phrase("turno anterior", Shift(AssistantTimeKind.PreviousShift));
        Phrase("este ano", Open(new DateTime(now.Year, 1, 1), now));
        Phrase("ano passado", Year(now.Year - 1, now));

        foreach (Match match in Regex.Matches(text, @"(?<n>\d+)天前"))
            AddRelative(hits, match, now, int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), rolling: false);
        foreach (Match match in Regex.Matches(text, @"(?:最近|过去|近)(?<n>\d+)天"))
            AddRelative(hits, match, now, int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), rolling: true);
        foreach (Match match in Regex.Matches(text, @"last (?<n>\d+) days", RegexOptions.IgnoreCase))
            AddRelative(hits, match, now, int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), rolling: true);
        foreach (Match match in Regex.Matches(text, @"(?<n>\d+) days? ago", RegexOptions.IgnoreCase))
            AddRelative(hits, match, now, int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), rolling: false);
        foreach (Match match in Regex.Matches(text, @"(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})"))
            AddDate(hits, match, now, match.Groups["y"], match.Groups["m"], match.Groups["d"]);
        foreach (Match match in Regex.Matches(text, @"(?<y>\d{4})年(?<m>\d{1,2})月(?<d>\d{1,2})日"))
            AddDate(hits, match, now, match.Groups["y"], match.Groups["m"], match.Groups["d"]);
        foreach (Match match in Regex.Matches(text, @"(?<y>\d{4})年(?<m>\d{1,2})月"))
            AddMonth(hits, match, now, int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture));
        foreach (Match match in Regex.Matches(text, @"(?<m>\d{1,2})月(?<d>\d{1,2})日"))
            AddDate(hits, match, now, null, match.Groups["m"], match.Groups["d"], now.Year);

        var accepted = new List<(int Start, int Length, AssistantWindow Window)>();
        foreach (var hit in hits.OrderBy(hit => hit.Start).ThenByDescending(hit => hit.Length))
        {
            if (accepted.Any(item => hit.Start < item.Start + item.Length && item.Start < hit.Start + hit.Length))
                continue;
            accepted.Add(hit);
            windows.Add(hit.Window);
        }
    }

    private static void AddRelative(
        List<(int Start, int Length, AssistantWindow Window)> hits,
        Match match,
        DateTime now,
        int days,
        bool rolling)
    {
        if (days is < 1 or > 10000)
            return;
        hits.Add((match.Index, match.Length, rolling ? Rolling(now, days) : Day(now.Date.AddDays(-days), now)));
    }

    private static void AddDate(
        List<(int Start, int Length, AssistantWindow Window)> hits,
        Match match,
        DateTime now,
        Group? year,
        Group month,
        Group day,
        int defaultYear = 0)
    {
        var y = year == null ? defaultYear : int.Parse(year.Value, CultureInfo.InvariantCulture);
        if (!TryDate(y, int.Parse(month.Value, CultureInfo.InvariantCulture), int.Parse(day.Value, CultureInfo.InvariantCulture), out var date))
            return;
        hits.Add((match.Index, match.Length, Day(date, now)));
    }

    private static void AddMonth(
        List<(int Start, int Length, AssistantWindow Window)> hits,
        Match match,
        DateTime now,
        int year,
        int month)
    {
        if (month is < 1 or > 12)
            return;
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1).AddTicks(-1);
        hits.Add((match.Index, match.Length, start.Year == now.Year && start.Month == now.Month ? Open(start, now) : new AssistantWindow(AssistantTimeKind.Calendar, start, end > now ? now : end)));
    }

    private static AssistantWindow Day(DateTime day, DateTime now)
    {
        var from = day.Date;
        var to = from.AddDays(1).AddTicks(-1);
        if (to > now)
            to = now;
        return new AssistantWindow(AssistantTimeKind.Calendar, from, to);
    }

    private static AssistantWindow Open(DateTime from, DateTime now)
        => new(AssistantTimeKind.Calendar, from, now);

    private static AssistantWindow Year(int year, DateTime now)
    {
        var start = new DateTime(year, 1, 1);
        var end = start.AddYears(1).AddTicks(-1);
        return new AssistantWindow(AssistantTimeKind.Calendar, start, end > now ? now : end);
    }

    private static AssistantWindow Month(DateTime sample, DateTime now)
    {
        var start = new DateTime(sample.Year, sample.Month, 1);
        var end = start.AddMonths(1).AddTicks(-1);
        return new AssistantWindow(AssistantTimeKind.Calendar, start, end > now ? now : end);
    }

    private static AssistantWindow Week(DateTime monday, DateTime now)
    {
        var end = monday.AddDays(7).AddTicks(-1);
        return new AssistantWindow(AssistantTimeKind.Calendar, monday, end > now ? now : end);
    }

    private static AssistantWindow Rolling(DateTime now, int days)
        => new(AssistantTimeKind.Calendar, now.Date.AddDays(-(days - 1)), now);

    private static AssistantWindow Shift(AssistantTimeKind kind)
        => new(kind, default, default);

    private static DateTime WeekStart(DateTime now)
    {
        var delta = ((int)now.DayOfWeek + 6) % 7;
        return now.Date.AddDays(-delta);
    }

    private static bool TryDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        try
        {
            date = new DateTime(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static List<string> MatchDevices(string text, IReadOnlyList<string>? deviceNames)
    {
        var hits = new List<(int Start, int Length, string Name)>();
        foreach (var name in (deviceNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct().OrderByDescending(name => name.Length))
        {
            var index = 0;
            while ((index = text.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
            {
                if (Bounded(text, index, name.Length) && hits.All(hit => index >= hit.Start + hit.Length || hit.Start >= index + name.Length))
                    hits.Add((index, name.Length, name));
                index += name.Length;
            }
        }

        return hits.OrderBy(hit => hit.Start).Select(hit => hit.Name).Distinct().ToList();
    }

    private static bool Bounded(string text, int index, int length)
    {
        var before = index == 0 || !IsAsciiWord(text[index - 1]);
        var after = index + length >= text.Length || !IsAsciiWord(text[index + length]);
        return before && after;
    }

    private static bool IsAsciiWord(char value)
        => value is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool HasAny(string text, params string[] words)
        => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
