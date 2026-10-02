using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Metrics;
using MainAPP.Models;
using OfflineCause = Kanban.Contracts.Enums.OfflineCause;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;


public interface IAssistantFactSheet
{
    string ReadSection(string kind, DateTime now, string? selectedDeviceName, string? periodText, string? deviceName);
}

/// <summary>工具调用时按类别查询。</summary>
public sealed class AssistantFactSheet : IAssistantFactSheet
{
    /// <summary>审计和条码一次最多带回的行数。其余记录留在原页面里查。</summary>
    public const int MaxListedRows = 40;

    private readonly IProductionHistoryReader _production;
    private readonly IAlarmHistoryService _alarmHistory;
    private readonly IActiveAlarmStateService _activeAlarms;
    private readonly IStatusTransitionHistoryService _status;
    private readonly IDefectHistoryReader _defects;
    private readonly DeviceRepository _devices;
    private readonly WorkOrderRepository _orders;
    private readonly AppSettings _settings;
    private readonly ILogger<AssistantFactSheet> _logger;
    private readonly ISnEventStore? _serials;
    private readonly UserStore? _users;
    private readonly IAuditService? _audit;

    public AssistantFactSheet(
        IProductionHistoryReader production,
        IAlarmHistoryService alarmHistory,
        IActiveAlarmStateService activeAlarms,
        IStatusTransitionHistoryService status,
        IDefectHistoryReader defects,
        DeviceRepository devices,
        WorkOrderRepository orders,
        AppSettings settings,
        ILogger<AssistantFactSheet> logger,
        ISnEventStore? serials = null,
        UserStore? users = null,
        IAuditService? audit = null)
    {
        _production = production;
        _alarmHistory = alarmHistory;
        _activeAlarms = activeAlarms;
        _status = status;
        _defects = defects;
        _devices = devices;
        _orders = orders;
        _settings = settings;
        _logger = logger;
        _serials = serials;
        _users = users;
        _audit = audit;
    }


    public string ReadSection(string kind, DateTime now, string? selectedDeviceName, string? periodText, string? deviceName)
    {
        try
        {
            if (!TrySelectDevices(selectedDeviceName, deviceName, out var devices, out var problem))
                return problem;
            var (from, to) = RecordRange(now, string.IsNullOrWhiteSpace(periodText) ? null : periodText);
            var period = $"{from:yyyy-MM-dd HH:mm} 至 {to:yyyy-MM-dd HH:mm}";
            var ids = devices.Select(device => device.Id).ToList();
            var lines = kind switch
            {
                "list_alarms" => AlarmSection(now, ids, from, to, period),
                "list_status" => StatusSection(devices, ids, from, to, period),
                "list_snapshots" => SnapshotSection(devices, ids, from, to, period),
                "list_defects" => DefectSection(devices, from, to, period),
                "list_work_orders" => OrderSection(now, devices, ids),
                "list_runtime" => RuntimeSection(now, devices),
                "list_barcodes" => BarcodeSection(from, to, period),
                "list_accounts" => AccountSection(),
                "list_audit" => AuditSection(from, to, period),
                "list_addresses" => string.Join("\n", AddressLines(devices)),
                _ => "没有这个查询。",
            };
            return string.IsNullOrWhiteSpace(lines) ? "这次没有查到。" : lines;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 问答工具 {Kind} 失败", kind);
            return "这次没有查到。";
        }
    }

    private bool TrySelectDevices(string? selectedDeviceName, string? deviceName, out List<Device> devices, out string problem)
    {
        devices = _devices.GetDevicesSnapshot().ToList();
        problem = "";
        var name = deviceName?.Trim() ?? "";
        if (name is "这台" or "当前设备" or "选中设备" or "this device" or "selected device")
            name = selectedDeviceName?.Trim() ?? "";
        if (name.Length == 0 || name is "全厂" or "whole plant")
            return true;
        var matched = devices.Where(device => string.Equals(device.Name?.Trim(), name, StringComparison.Ordinal)).ToList();
        if (matched.Count == 0)
        {
            problem = $"没有名为 {name} 的设备。";
            return false;
        }

        devices = matched;
        return true;
    }

    private string AlarmSection(
        DateTime now,
        IReadOnlyList<string> ids,
        DateTime from,
        DateTime to,
        string period)
    {
        var lines = new List<string>();
        List<ActiveAlarmStateRecord>? active = null;
        try
        {
            active = _activeAlarms.QueryActive();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 问答工具读取未恢复报警失败");
        }

        lines.AddRange(AlarmLines(now, null, active).Take(1));
        Dictionary<string, List<AlarmEventRecord>>? alarms = null;
        if (ids.Count > 0)
            alarms = _alarmHistory.QueryAlarmEventsBatchStrict(from, to, ids);
        else
            alarms = [];
        var events = (alarms ?? new Dictionary<string, List<AlarmEventRecord>>())
            .SelectMany(pair => pair.Value)
            .Where(alarm => alarm.EventType is AlarmEventType.Triggered or AlarmEventType.Recovered)
            .OrderByDescending(alarm => alarm.EventTime)
            .ToList();
        if (events.Count == 0)
            lines.Add($"{period}没有报警记录。");
        foreach (var alarm in events)
        {
            var kind = alarm.EventType == AlarmEventType.Triggered ? "触发" : "恢复";
            var address = string.IsNullOrWhiteSpace(alarm.PlcAddress) ? "" : $" 地址 {alarm.PlcAddress.Trim()}";
            lines.Add($"报警记录 {alarm.DeviceName} {alarm.AlarmName}{address} {alarm.EventTime:yyyy-MM-dd HH:mm} {kind}。");
        }

        return string.Join("\n", lines);
    }

    private string StatusSection(IReadOnlyList<Device> devices, IReadOnlyList<string> ids, DateTime from, DateTime to, string period)
    {
        var transitions = ids.Count == 0
            ? new Dictionary<string, List<StatusTransitionRecord>>()
            : _status.QueryStatusTransitionsBatchStrict(from, to, ids);
        var lines = RecordLines(devices, null, null, transitions, period, includeSnapshots: false, includeStatus: true).ToList();
        return lines.Count == 0 ? $"{period}没有状态记录。" : string.Join("\n", lines);
    }

    private string SnapshotSection(IReadOnlyList<Device> devices, IReadOnlyList<string> ids, DateTime from, DateTime to, string period)
    {
        var production = ids.Count == 0
            ? new Dictionary<string, List<ProductionLog>>()
            : _production.QueryProductionLogsBatchStrict(from, to, ids);
        return string.Join("\n", RecordLines(devices, production, null, null, period, includeSnapshots: true, includeStatus: false));
    }

    private string DefectSection(IReadOnlyList<Device> devices, DateTime from, DateTime to, string period)
    {
        var facts = new List<(string DeviceName, string DefectName, int Count, string Severity)>();
        foreach (var device in devices)
        {
            var bounds = _defects.QueryWindowBounds(from, to, device.Id);
            var points = bounds
                .Select(row => new DefectHistoryPoint(row.DefectId, row.DefectName, row.Timestamp, row.Count, row.ShiftName))
                .ToList();
            foreach (var pair in DefectWindowMetrics.ComputeIncrements(points, from, to))
            {
                if (pair.Value <= 0 || string.IsNullOrWhiteSpace(device.Name))
                    continue;
                var sample = bounds.LastOrDefault(row => row.DefectId == pair.Key);
                var name = sample?.DefectName;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                facts.Add((device.Name.Trim(), name.Trim(), pair.Value, SeverityText(sample!.Severity)));
            }
        }

        if (facts.Count == 0)
            return $"{period}没有新增缺陷件数。";
        var shown = facts
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.DeviceName, StringComparer.Ordinal)
            .Select(item => $"{item.DeviceName} {DefectPiece(item.DefectName, item.Severity, item.Count)}");
        return $"{period}缺陷新增（窗口差分，不是累计末值）：{string.Join("；", shown)}。";
    }

    private static DateTime LogStart(DateTime today, IReadOnlyList<WorkOrder> orders)
    {
        var from = today.AddDays(-2);
        var earliest = orders
            .Where(order => order.Status == WorkOrderStatus.Running && order.StartedAt is DateTime)
            .Select(order => order.StartedAt!.Value)
            .DefaultIfEmpty(from)
            .Min();
        if (earliest.AddHours(-1) < from)
            from = earliest.AddHours(-1);
        var floor = today.AddDays(-7);
        return from < floor ? floor : from;
    }

    private string OrderSection(DateTime now, IReadOnlyList<Device> devices, IReadOnlyList<string> ids)
    {
        var names = devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var orders = _orders.GetSnapshot().Where(order => names.Contains(order.DeviceId)).ToList();
        Dictionary<string, List<ProductionLog>>? production = null;
        if (ids.Count > 0)
            production = _production.QueryProductionLogsBatchStrict(LogStart(now.Date, orders), now, ids);
        var lines = new List<string> { "工单看的是现在的状态，不是问句那段历史。" };
        lines.AddRange(OrderLines(now, orders, production));
        return string.Join("\n", lines);
    }

    private string RuntimeSection(DateTime now, IReadOnlyList<Device> devices)
    {
        var runtimes = _devices.GetRuntimesSnapshot();
        var wanted = devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var rows = runtimes.Where(runtime => wanted.Contains(runtime.DeviceId)).ToList();
        return string.Join("\n", ShiftLines(now, devices, rows.ToDictionary(runtime => runtime.DeviceId), _settings.GetShiftsSnapshot()));
    }

    private string BarcodeSection(DateTime from, DateTime to, string period)
    {
        if (_serials == null)
            return $"{period}没有条码记录。";
        var (total, items) = _serials.QueryByTimeRange(null, from, to, 1, MaxListedRows);
        var lines = SerialLines(items, period).ToList();
        if (total > items.Count)
            lines.Add("还有更多条码，这次没有全部列出。");
        return string.Join("\n", lines);
    }

    private string AccountSection()
    {
        var accounts = _users == null ? [] : _users.GetAll().ToList();
        return string.Join("\n", AccountLines(accounts));
    }

    private string AuditSection(DateTime from, DateTime to, string period)
    {
        if (_audit == null)
            return $"{period}没有审计记录。";
        var (items, total) = _audit.QueryPaged(from, to, null, null, null, null, 1, MaxListedRows);
        var lines = AuditLines(items, period).ToList();
        if (total > items.Count)
            lines.Add("还有更多审计记录，这次没有全部列出。");
        return string.Join("\n", lines);
    }


    private (DateTime From, DateTime To) RecordRange(DateTime now, string? question)
    {
        var windows = AssistantQuery.ReadWindows(question, now);
        if (windows.Count == 0)
            return (now.Date, now);
        var resolved = AssistantQuery.ResolveWindows(
            new AssistantAsk(true, false, false, true, [], false, windows),
            now,
            _settings.GetShiftsSnapshot(),
            AssistantQuery.RetentionDays());
        var open = resolved.Where(window => window.Problem == null).ToList();
        if (open.Count == 0)
            return (now.Date, now);
        return (open.Min(window => window.From), open.Max(window => window.To));
    }


    private static IEnumerable<string> ShiftLines(
        DateTime now,
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, DeviceRuntime> runtimes,
        IReadOnlyList<ShiftConfig> shifts)
    {
        var (shift, start, end) = ShiftConfigResolver.ResolveCurrentShift(shifts, now);
        if (shift == null)
            yield return "当前没有排到班次。下面的班次件数仍是首页正在累加的数，换班归零。";
        else
        {
            yield return $"当前班次：{shift.Name}，{start:HH:mm} 至 {end:HH:mm}。下面的班次件数是首页正在累加的数，换班归零。";
            var progress = ShiftProgress(now, start, end);
            if (progress != null)
                yield return progress;
        }

        var rows = devices.Select(device =>
        {
            runtimes.TryGetValue(device.Id, out var runtime);
            return (device, runtime);
        }).ToList();
        var ok = rows.Sum(row => row.runtime?.TotalOkProduction ?? 0);
        var ng = rows.Sum(row => row.runtime?.TotalNgProduction ?? 0);
        var run = rows.Sum(row => row.runtime?.RunTime ?? 0);
        var alarm = rows.Sum(row => row.runtime?.AlarmTime ?? 0);
        var paused = rows.Sum(row => row.runtime?.PausedTime ?? 0);
        var availability = OeeCalculator.CalculateAvailabilityRate(run, alarm);
        yield return $"当前班次全厂：合格 {ok} 件，不良 {ng} 件，合计 {ok + ng} 件，良品率 {Percent(OeeCalculator.CalculateQualityRate(ok, ng))}。运行 {Hours(run)} 小时，报警 {Hours(alarm)} 小时，待机 {Hours(paused)} 小时，加总后的开动率 {Percent(availability)}。开动率不含待机和离线。";
        var weakest = WeakestLine(rows);
        if (weakest != null)
            yield return weakest;
        var gapLeader = LargestGapLine(rows);
        if (gapLeader != null)
            yield return gapLeader;
        yield return StatusCount(rows.Select(row => row.runtime?.StatusWord ?? (int)DeviceStatus.Offline));
        var offline = OfflineLine(rows);
        if (offline != null)
            yield return offline;
        yield return "下面每台的当前配方是设备现在的配置，不是历史当时的配方。性能率和开动率是首页已经算好的当前班次。还差的件数用同一套额定：目标产能乘以已运行小时。";
        yield return $"颜色档和看板相同：良品率达到 {KpiThresholds.QualityGood * 100:0}% 为绿、达到 {KpiThresholds.QualityWarning * 100:0}% 为黄、否则为红。综合 OEE 达到 {KpiThresholds.OeeGood * 100:0}% 为绿、达到 {KpiThresholds.OeeWarning * 100:0}% 为黄、否则为红。";
        yield return "全厂不汇总综合 OEE。综合 OEE 只按单台给出。";

        foreach (var row in rows.OrderByDescending(row => (row.runtime?.TotalOkProduction ?? 0) + (row.runtime?.TotalNgProduction ?? 0)).ThenBy(row => row.device.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(row.device.Name))
                continue;
            var deviceOk = row.runtime?.TotalOkProduction ?? 0;
            var deviceNg = row.runtime?.TotalNgProduction ?? 0;
            if (deviceOk + deviceNg == 0 && (row.runtime?.StatusWord ?? (int)DeviceStatus.Offline) == (int)DeviceStatus.Offline)
                continue;

            var quality = OeeCalculator.CalculateQualityRate(deviceOk, deviceNg);
            var oee = row.runtime?.Oee ?? 0;
            var recipe = string.IsNullOrWhiteSpace(row.device.RecipeName) ? "未填" : row.device.RecipeName.Trim();
            var target = row.device.TargetCycle > 0 ? $"{row.device.TargetCycle} 件/小时" : "未设";
            var rates = row.runtime == null
                ? ""
                : $"，性能率 {Percent(row.runtime.PerformanceRate)}，开动率 {Percent(row.runtime.AvailabilityRate)}";
            var gap = RatedGap(row.device.TargetCycle, row.runtime?.RunTime ?? 0, deviceOk + deviceNg);
            var pace = PaceText(row.runtime, row.device.TargetCycle, deviceOk, deviceNg);
            var status = StatusText(row.runtime?.StatusWord ?? (int)DeviceStatus.Offline, row.runtime?.OfflineCause ?? OfflineCause.None);
            yield return $"{row.device.Name.Trim()}：{status}，当前配方 {recipe}，目标产能 {target}，当前班次合格 {deviceOk} 件，不良 {deviceNg} 件，良品率 {Percent(quality)}（{QualityBand(quality)}）{rates}{gap}{pace}，综合 OEE {Percent(oee)}（{OeeBand(oee)}）。";
        }
    }


    private static (int Ok, int Ng) WindowOutput(IReadOnlyList<ProductionLog> logs, DateTime from, DateTime to)
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

    private static IEnumerable<string> AlarmLines(
        DateTime now,
        IReadOnlyDictionary<string, List<AlarmEventRecord>>? alarmEvents,
        IReadOnlyList<ActiveAlarmStateRecord>? activeAlarms)
    {
        if (activeAlarms == null)
            yield return "当前未恢复报警这次没有查到。";
        else
        {
            var active = activeAlarms
                .Where(alarm => alarm.IsActive && !string.IsNullOrWhiteSpace(alarm.AlarmName))
                .Select(alarm => $"{alarm.DeviceName} {alarm.AlarmName}".Trim())
                .ToList();
            yield return active.Count == 0
                ? "当前没有未恢复报警。"
                : $"当前未恢复报警 {activeAlarms.Count(alarm => alarm.IsActive)} 条：{string.Join("；", active)}。";
        }

        if (alarmEvents == null)
        {
            yield return "今天的报警次数这次没有查到。";
            yield break;
        }

        var triggered = alarmEvents
            .SelectMany(pair => pair.Value)
            .Where(alarm => alarm.EventType == AlarmEventType.Triggered && !string.IsNullOrWhiteSpace(alarm.AlarmName))
            .GroupBy(alarm => $"{alarm.DeviceName} {alarm.AlarmName}".Trim())
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
        if (triggered.Count == 0)
        {
            yield return "今天还没有触发过报警。";
            yield break;
        }

        var shown = triggered.Select(group => $"{group.Name} {group.Count} 次");
        yield return $"今天触发过的报警：{string.Join("；", shown)}。这是触发次数，不是当前还没恢复的条数。";
        var longest = LongestAlarm(now, alarmEvents);
        if (longest != null)
            yield return longest;
    }

    private static IEnumerable<string> OrderLines(
        DateTime now,
        IReadOnlyList<WorkOrder> orders,
        IReadOnlyDictionary<string, List<ProductionLog>>? productionLogs)
    {
        var today = now.Date;
        var running = orders.Where(order => order.Status == WorkOrderStatus.Running).ToList();
        var finished = orders
            .Where(order => order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted
                && order.CompletedAt >= today)
            .ToList();
        if (running.Count == 0)
            yield return "没有进行中的工单。";
        foreach (var order in running)
        {
            var progress = "";
            if (order.StartedAt is DateTime started && productionLogs != null)
            {
                productionLogs.TryGetValue(order.DeviceId, out var logs);
                var (orderOk, orderNg) = WindowOutput(logs ?? [], started, now);
                progress = $"，从 {started:yyyy-MM-dd HH:mm} 到现在窗口合格 {orderOk} 件，不良 {orderNg} 件";
                if (order.TargetQuantity > 0)
                {
                    var rate = Math.Min(1.0, WorkOrderSchedule.AchievementRate(orderOk, order.TargetQuantity));
                    var left = Math.Max(0, order.TargetQuantity - orderOk);
                    progress += left == 0
                        ? $"，完成率 {Percent(rate)}，合格数已达到目标"
                        : $"，完成率 {Percent(rate)}，还差 {left} 件";
                    progress += SchedulePhrase(order, orderOk, now);
                }
            }

            yield return $"进行中工单 {order.OrderNo}，{order.DeviceName}，产品 {Blank(order.ProductName)}，目标 {order.TargetQuantity} 件{progress}。";
        }

        if (running.Any(order => order.StartedAt != null && order.TargetQuantity > 0 && productionLogs != null))
            yield return "工单完成率是合格数除以目标，和工单页同一套。不良数不计入完成率。";
        if (running.Any(order => order.StartedAt != null && order.TargetQuantity > 0 && order.PlannedEnd > order.PlannedStart && productionLogs != null))
            yield return "相对计划的偏差是完成率减去计划时间进度，小于 -10% 算进度落后。这和本班已过百分之几不是同一套。";

        if (finished.Count == 0)
            yield return "今天没有已结束的工单。";
        foreach (var order in finished)
        {
            var state = order.Status == WorkOrderStatus.Completed ? "已完成" : "已中止";
            var pieces = order.CompletedOkCount is int ok && order.CompletedNgCount is int ng
                ? $"，合格 {ok} 件，不良 {ng} 件"
                : "";
            yield return $"今天{state}工单 {order.OrderNo}，{order.DeviceName}，产品 {Blank(order.ProductName)}{pieces}。";
        }

        var names = running.Concat(finished)
            .Select(order => order.ProductName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        yield return names.Count == 0
            ? "今天工单上没有产品名称。"
            : $"今天工单上的产品名称：{string.Join("、", names)}。这是种类，不是件数。";
    }


    private static string? LongestAlarm(
        DateTime now,
        IReadOnlyDictionary<string, List<AlarmEventRecord>> alarmEvents)
    {
        var best = TimeSpan.Zero;
        string? label = null;
        foreach (var group in alarmEvents
            .SelectMany(pair => pair.Value)
            .Where(alarm => !string.IsNullOrWhiteSpace(alarm.AlarmName))
            .GroupBy(alarm => string.IsNullOrWhiteSpace(alarm.AlarmId)
                ? $"{alarm.DeviceId}|{alarm.AlarmName}"
                : $"{alarm.DeviceId}|{alarm.AlarmId}"))
        {
            DateTime? open = null;
            foreach (var alarm in group.OrderBy(item => item.EventTime).ThenBy(item => item.Id))
            {
                if (alarm.EventType == AlarmEventType.Triggered)
                    open ??= alarm.EventTime;
                else if (alarm.EventType == AlarmEventType.Recovered && open is DateTime started)
                {
                    Consider(alarm.EventTime - started, $"{alarm.DeviceName} {alarm.AlarmName}".Trim());
                    open = null;
                }
            }

            if (open is DateTime still)
            {
                var sample = group.First();
                Consider(now - still, $"{sample.DeviceName} {sample.AlarmName}".Trim());
            }

            void Consider(TimeSpan span, string name)
            {
                if (span > best && !string.IsNullOrWhiteSpace(name))
                {
                    best = span;
                    label = name;
                }
            }
        }

        return label == null || best <= TimeSpan.Zero
            ? null
            : $"今天持续时间最长的报警：{label}，{DurationText(best)}。从触发算到恢复，还没恢复的算到现在。这是时长，不是次数。";
    }


    private static string StatusCount(IEnumerable<int> words)
    {
        var list = words.ToList();
        return $"设备状态：运行 {list.Count(word => word == (int)DeviceStatus.Running)} 台，报警 {list.Count(word => word == (int)DeviceStatus.Alarm)} 台，待机 {list.Count(word => word == (int)DeviceStatus.Paused)} 台，离线 {list.Count(word => word == (int)DeviceStatus.Offline)} 台。";
    }

    private static string? ShiftProgress(DateTime now, DateTime start, DateTime end)
    {
        var total = (end - start).TotalSeconds;
        if (total <= 0)
            return null;
        var elapsed = Math.Clamp((now - start).TotalSeconds, 0, total);
        var remain = end - now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;
        return $"本班已过 {elapsed / total * 100:0.0}%，还剩 {RemainText(remain)}。这是时间进度，不是产量进度。";
    }

    private static string RemainText(TimeSpan remain)
        => remain.TotalHours >= 1
            ? $"{remain.TotalHours:0.0} 小时"
            : $"{Math.Max(0, (int)Math.Round(remain.TotalMinutes))} 分钟";

    private static string? WeakestLine(List<(Device device, DeviceRuntime? runtime)> rows)
    {
        var producing = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.device.Name)
                && (row.runtime?.TotalOkProduction ?? 0) + (row.runtime?.TotalNgProduction ?? 0) > 0)
            .ToList();
        if (producing.Count == 0)
            return null;
        var worstQuality = producing
            .OrderBy(row => OeeCalculator.CalculateQualityRate(row.runtime?.TotalOkProduction ?? 0, row.runtime?.TotalNgProduction ?? 0))
            .ThenBy(row => row.device.Name, StringComparer.Ordinal)
            .First();
        var worstOee = producing
            .OrderBy(row => row.runtime?.Oee ?? 0)
            .ThenBy(row => row.device.Name, StringComparer.Ordinal)
            .First();
        var quality = OeeCalculator.CalculateQualityRate(worstQuality.runtime?.TotalOkProduction ?? 0, worstQuality.runtime?.TotalNgProduction ?? 0);
        return $"当前班次良品率最低的是 {worstQuality.device.Name.Trim()}，{Percent(quality)}。综合 OEE 最低的是 {worstOee.device.Name.Trim()}，{Percent(worstOee.runtime?.Oee ?? 0)}。{DragText(worstOee.runtime)}只在有班次产量的设备里比。";
    }

    private static string DragText(DeviceRuntime? runtime)
    {
        if (runtime == null)
            return "";
        var quality = OeeCalculator.CalculateQualityRate(runtime.TotalOkProduction, runtime.TotalNgProduction);
        var performance = runtime.PerformanceRate;
        var availability = runtime.AvailabilityRate;
        var lowest = Math.Min(quality, Math.Min(performance, availability));
        var names = new List<string>();
        if (Nearly(quality, lowest))
            names.Add($"良品率 {Percent(quality)}");
        if (Nearly(performance, lowest))
            names.Add($"性能率 {Percent(performance)}");
        if (Nearly(availability, lowest))
            names.Add($"开动率 {Percent(availability)}");
        return $"这台综合 OEE 里更低的是{string.Join("、", names)}。";
    }

    private static bool Nearly(double left, double right) => Math.Abs(left - right) <= 0.0000001;

    private static string? OfflineLine(List<(Device device, DeviceRuntime? runtime)> rows)
    {
        var offline = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.device.Name)
                && (row.runtime?.StatusWord ?? (int)DeviceStatus.Offline) == (int)DeviceStatus.Offline)
            .Select(row => $"{row.device.Name.Trim()} {StatusText((int)DeviceStatus.Offline, row.runtime?.OfflineCause ?? OfflineCause.None)}")
            .ToList();
        if (offline.Count == 0)
            return "当前没有离线设备。";
        return $"当前离线：{string.Join("；", offline)}。这是现在的状态原因。";
    }

    private static string? LargestGapLine(List<(Device device, DeviceRuntime? runtime)> rows)
    {
        var worstName = "";
        var worstPieces = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.device.Name))
                continue;
            var actual = (row.runtime?.TotalOkProduction ?? 0) + (row.runtime?.TotalNgProduction ?? 0);
            if (!TryRatedShortfall(row.device.TargetCycle, row.runtime?.RunTime ?? 0, actual, out _, out var pieces) || pieces <= worstPieces)
                continue;
            worstPieces = pieces;
            worstName = row.device.Name.Trim();
        }

        return worstPieces <= 0
            ? null
            : $"当前班次离额定差得最多的是 {worstName}，还差 {worstPieces} 件。按已运行小时的额定，不是按整班时间。";
    }


    private static string PaceText(DeviceRuntime? runtime, int targetCycle, int ok, int ng)
    {
        if (runtime == null)
            return "";
        var speed = SnapshotMetrics.RealtimeSpeed(runtime.RunTime, ok, ng);
        if (speed <= 0)
            return "，运行不足 5 秒，没有当前节拍";
        if (targetCycle <= 0)
            return $"，当前节拍 {speed:0.0} 件/小时，目标产能未设";
        var achieved = SnapshotMetrics.AchievementRate(speed, targetCycle);
        return $"，当前节拍 {speed:0.0} 件/小时，速度达成率 {Percent(achieved)}";
    }

    private static string QualityBand(double rate)
        => rate >= KpiThresholds.QualityGood ? "绿" : rate >= KpiThresholds.QualityWarning ? "黄" : "红";

    private static string OeeBand(double rate)
        => rate >= KpiThresholds.OeeGood ? "绿" : rate >= KpiThresholds.OeeWarning ? "黄" : "红";

    private static string SeverityText(DefectSeverity severity) => severity switch
    {
        DefectSeverity.Critical => "严重",
        DefectSeverity.Major => "一般",
        _ => "轻微",
    };

    private static string DefectPiece(string name, string severity, int count)
    {
        var level = string.IsNullOrWhiteSpace(severity) ? "" : severity.Trim() + " ";
        return $"{name.Trim()} {level}{count} 件";
    }

    private static string RatedGap(int targetCycle, double runSeconds, int actual)
    {
        if (targetCycle <= 0 || runSeconds <= 0)
            return "";
        if (!TryRatedShortfall(targetCycle, runSeconds, actual, out var ideal, out var pieces))
            return "，按已运行小时已达到额定";
        return $"，按已运行小时额定 {ideal:0} 件，还差 {pieces} 件";
    }

    private static bool TryRatedShortfall(int targetCycle, double runSeconds, int actual, out double ideal, out int piecesShort)
    {
        ideal = targetCycle * (runSeconds / 3600.0);
        var shortfall = ideal - actual;
        if (targetCycle <= 0 || runSeconds <= 0 || shortfall <= 0.05)
        {
            piecesShort = 0;
            return false;
        }

        piecesShort = (int)Math.Ceiling(shortfall - 1e-6);
        return piecesShort > 0;
    }

    private static string SchedulePhrase(WorkOrder order, int orderOk, DateTime now)
    {
        if (order.TargetQuantity <= 0 || order.PlannedEnd <= order.PlannedStart)
            return "";
        var rate = WorkOrderSchedule.AchievementRate(orderOk, order.TargetQuantity);
        var status = (Kanban.Contracts.Enums.WorkOrderStatus)(int)order.Status;
        var kind = WorkOrderSchedule.Classify(status, order.PlannedStart, order.PlannedEnd, rate, now);
        var deviation = WorkOrderSchedule.ProgressDeviation(status, order.PlannedStart, order.PlannedEnd, rate, now);
        return $"，相对计划：{ScheduleText(kind)}，偏差 {deviation * 100:0.0} 个百分点";
    }

    private static string ScheduleText(WorkOrderScheduleKind kind) => kind switch
    {
        WorkOrderScheduleKind.MetPendingComplete => "已达标待完成",
        WorkOrderScheduleKind.OverdueIncomplete => "已超期未完成",
        WorkOrderScheduleKind.Behind => "进度落后",
        WorkOrderScheduleKind.OnTrack => "正常生产",
        WorkOrderScheduleKind.CompletedMet => "已完成且达到目标",
        WorkOrderScheduleKind.CompletedShort => "已完成但未达目标",
        WorkOrderScheduleKind.Aborted => "已中止",
        _ => "正常生产",
    };

    private static string StatusText(int word, OfflineCause cause = OfflineCause.None) => word switch
    {
        (int)DeviceStatus.Running => "运行",
        (int)DeviceStatus.Alarm => "报警",
        (int)DeviceStatus.Paused => "待机",
        _ => cause switch
        {
            OfflineCause.PlcReported => "离线(PLC)",
            OfflineCause.CommsLost => "离线(通讯中断)",
            OfflineCause.AcquisitionStopped => "离线(采集停止)",
            OfflineCause.GapFilled => "离线(采集空窗)",
            _ => "离线",
        },
    };

    private static IEnumerable<string> AddressLines(IReadOnlyList<Device> devices)
    {
        foreach (var device in devices.OrderBy(device => device.Name, StringComparer.Ordinal))
        {
            var parts = new List<string>();
            Add(device.OkCountAddress, "合格计数");
            Add(device.NgCountAddress, "不良计数");
            Add(device.StatusCountAddress, "状态");
            Add(device.RecipeAddress, "配方");
            foreach (var alarm in device.Alarms)
                Add(alarm.PlcAddress, string.IsNullOrWhiteSpace(alarm.Name) ? "报警" : alarm.Name.Trim());
            if (parts.Count == 0 || string.IsNullOrWhiteSpace(device.Name))
                continue;
            yield return $"{device.Name.Trim()} 的地址：{string.Join("、", parts)}。";

            void Add(string? address, string label)
            {
                if (!string.IsNullOrWhiteSpace(address))
                    parts.Add($"{label} {address.Trim()}");
            }
        }
    }

    private static IEnumerable<string> RecordLines(
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<ProductionLog>>? productionLogs,
        IReadOnlyDictionary<string, List<AlarmEventRecord>>? alarmEvents,
        IReadOnlyDictionary<string, List<StatusTransitionRecord>>? statusTransitions,
        string period = "今天",
        bool includeSnapshots = true,
        bool includeStatus = true)
    {
        if (includeSnapshots)
        {
        yield return $"下面是{period}的产量快照，是班次累计，不是窗口差分。";
        if (productionLogs == null)
        {
            yield return "产量快照这次没有查到。";
        }
        else
        {
            var names = devices.ToDictionary(device => device.Id, device => device.Name);
            var logs = productionLogs
                .SelectMany(pair => pair.Value)
                .OrderByDescending(log => log.Timestamp)
                .ToList();
            foreach (var log in logs)
            {
                names.TryGetValue(log.DeviceId, out var name);
                var shown = string.IsNullOrWhiteSpace(log.DeviceName) ? name : log.DeviceName;
                if (string.IsNullOrWhiteSpace(shown))
                    shown = log.DeviceId;
                yield return $"产量快照 {shown.Trim()} {log.Timestamp:yyyy-MM-dd HH:mm} {Blank(log.ShiftName)} 班次累计合格 {log.OkProduction} 件，不良 {log.NgProduction} 件。";
            }

            if (logs.Count == 0)
                yield return "这段时间没有产量快照。";
        }
        }

        if (alarmEvents != null)
        {
            var events = alarmEvents
                .SelectMany(pair => pair.Value)
                .Where(alarm => alarm.EventType is AlarmEventType.Triggered or AlarmEventType.Recovered)
                .OrderByDescending(alarm => alarm.EventTime)
                .ToList();
            foreach (var alarm in events)
            {
                var kind = alarm.EventType == AlarmEventType.Triggered ? "触发" : "恢复";
                var address = string.IsNullOrWhiteSpace(alarm.PlcAddress) ? "" : $" 地址 {alarm.PlcAddress.Trim()}";
                yield return $"报警记录 {alarm.DeviceName} {alarm.AlarmName}{address} {alarm.EventTime:yyyy-MM-dd HH:mm} {kind}。";
            }

        }

        if (includeStatus && statusTransitions != null)
        {
            var names = devices.ToDictionary(device => device.Id, device => device.Name);
            var rows = statusTransitions
                .SelectMany(pair => pair.Value)
                .OrderByDescending(row => row.EventTime)
                .ToList();
            foreach (var row in rows)
            {
                names.TryGetValue(row.DeviceId, out var fallback);
                var shown = string.IsNullOrWhiteSpace(row.DeviceName) ? fallback : row.DeviceName;
                if (string.IsNullOrWhiteSpace(shown))
                    shown = row.DeviceId;
                var from = StatusText(row.PreviousState);
                var to = StatusText(row.CurrentState, (OfflineCause)row.OfflineCause);
                yield return $"状态记录 {shown.Trim()} {row.EventTime:yyyy-MM-dd HH:mm} 从 {from} 到 {to}。";
            }
        }
    }

    private static IEnumerable<string> SerialLines(IReadOnlyList<SnEventRecord> serials, string period = "今天")
    {
        if (serials.Count == 0)
        {
            yield return $"{period}没有条码记录。";
            yield break;
        }

        foreach (var row in serials)
        {
            if (string.IsNullOrWhiteSpace(row.Sn))
                continue;
            var result = row.Result == 0 ? "合格" : "不良";
            yield return $"条码 {row.Sn.Trim()}，{Blank(row.DeviceName)}，{row.Timestamp:yyyy-MM-dd HH:mm}，{result}。";
        }
    }

    private static IEnumerable<string> AccountLines(IReadOnlyList<User> accounts)
    {
        if (accounts.Count == 0)
        {
            yield return "没有账号。";
            yield break;
        }

        foreach (var user in accounts)
        {
            if (string.IsNullOrWhiteSpace(user.Username))
                continue;
            var state = user.IsActive ? "启用" : "停用";
            yield return $"账号 {user.DisplayLabel.Trim()}，用户名 {user.Username.Trim()}，角色 {RoleText(user.Role)}，{state}。";
        }
    }

    private static IEnumerable<string> AuditLines(IReadOnlyList<AuditEntry> audits, string period = "今天")
    {
        if (audits.Count == 0)
        {
            yield return $"{period}没有审计记录。";
            yield break;
        }

        foreach (var entry in audits.OrderByDescending(entry => entry.Timestamp))
        {
            var who = string.IsNullOrWhiteSpace(entry.Operator) ? "未登录" : entry.Operator.Trim();
            var result = entry.Succeeded ? "成功" : "失败";
            var target = string.IsNullOrWhiteSpace(entry.TargetId) ? entry.TargetType : $"{entry.TargetType} {entry.TargetId}";
            yield return $"审计 {entry.Timestamp:yyyy-MM-dd HH:mm} {who} {entry.Action} {target} {result}。";
        }
    }

    private static string RoleText(UserRole role) => role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Engineer => "工程师",
        _ => "操作员",
    };

    private static string Percent(double rate) => $"{rate * 100:0.0}%";

    private static string Hours(double seconds) => $"{seconds / 3600:0.0}";

    private static string DurationText(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{span.TotalHours:0.0} 小时"
            : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} 分钟";

    private static string Blank(string? text) => string.IsNullOrWhiteSpace(text) ? "未填" : text.Trim();
}
