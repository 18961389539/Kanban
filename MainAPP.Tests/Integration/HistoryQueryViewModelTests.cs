using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 查询页 HistoryQueryViewModel 集成测试。
/// 通过真实 SQLite 数据库 + HistoryService 验证：
/// - Tab 切换分发到对应查询
/// - 设备筛选、时间筛选、快捷时间
/// - 分页（PreviousPage / NextPage / 越界保护）
/// - 空结果 IsEmptyResult 状态
/// - 重置按钮、Search 重置到首页
/// - 产量汇总按 (DeviceId, ShiftName) 分组取末条
/// - 报警 Trigger/Recover/Pending 统计
/// - OEE 计算依赖设备 TargetCycle
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class HistoryQueryViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;
    private readonly HistoryService _historyService;
    private readonly DeviceRepository _deviceRepo;
    private readonly HistoryQueryViewModel _vm;

    // 测试设备：两台，便于验证设备筛选
    private readonly Device _dev1 = new() { Id = "dev-001", Name = "设备A", TargetCycle = 600 };
    private readonly Device _dev2 = new() { Id = "dev-002", Name = "设备B", TargetCycle = 400 };

    public HistoryQueryViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanQueryVMTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);

        using (var ctx = _db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();

        _historyService = new HistoryService(_db, NullLogger<HistoryService>.Instance);
        _deviceRepo = new DeviceRepository(_appSettings);
        _deviceRepo.Devices.Add(_dev1);
        _deviceRepo.Devices.Add(_dev2);

        _vm = new HistoryQueryViewModel(_historyService, _deviceRepo, _appSettings, new StubDialogService());
    }

    public void Dispose()
    {
        try
        {
            _historyService.Dispose();
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    // ──────────── 辅助方法 ────────────

    private void InsertProductionLog(string deviceId, string deviceName, string shiftName,
        int ok, int ng, int statusWord, DateTime ts)
    {
        using var ctx = _db.CreateProductionLogContext();
        ctx.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            ShiftName = shiftName,
            OkProduction = ok,
            NgProduction = ng,
            StatusWord = statusWord,
            Timestamp = ts
        });
        ctx.SaveChanges();
    }

    private void InsertStatusTransition(string deviceId, string deviceName,
        int prev, int cur, DateTime ts)
    {
        _historyService.LogStatusTransition(deviceId, deviceName, prev, cur, ts);
    }

    private void InsertAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, AlarmEventType eventType, DateTime ts)
    {
        // 直接写库：避免 EventType=Recovered(恢复) 走异步队列导致测试不确定
        // （HistoryService.LogAlarmEvent 中 EventType=Recovered 入队，5s 后才 flush）
        using var ctx = _db.CreateAlarmEventContext();

        ctx.AlarmEvents.Add(new AlarmEventRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            AlarmId = alarmId,
            AlarmName = alarmName,
            PlcAddress = plcAddress,
            EventType = eventType,
            EventTime = ts
        });
        ctx.SaveChanges();
    }

    // ════════════════════ 设备筛选下拉 ════════════════════

    [Fact]
    public void RefreshDeviceFilterItems_IncludesOnlyDevices()
    {
        // 业务修正后设备为必选，下拉仅含实际设备（不再有"全部设备"项）
        _vm.RefreshDeviceFilterItems();

        Assert.Equal(2, _vm.DeviceFilterItems.Count);
        Assert.Equal("dev-001", _vm.DeviceFilterItems[0].Id);
        Assert.Equal("dev-002", _vm.DeviceFilterItems[1].Id);
    }

    // ════════════════════ 快捷时间 ════════════════════

    [Fact]
    public void QuickTimeIndex_Today_SetsFromToTodayRange()
    {
        _vm.QuickTimeIndex = 1; // 今天

        var now = DateTime.Now;
        Assert.Equal(now.Date, _vm.FromDate);
        Assert.Equal(now.Date.AddDays(1).AddSeconds(-1), _vm.ToDate);
    }

    [Fact]
    public void QuickTimeIndex_Last7Days_SetsFromToSevenDaysRange()
    {
        _vm.QuickTimeIndex = 3; // 近7天

        var now = DateTime.Now;
        Assert.Equal(now.Date.AddDays(-7), _vm.FromDate);
        Assert.Equal(now.Date.AddDays(1).AddSeconds(-1), _vm.ToDate);
    }

    [Fact]
    public void QuickTimeIndex_Custom_KeepsExistingFromTo()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0);
        var to = new DateTime(2026, 1, 2, 0, 0, 0);
        _vm.FromDate = from;
        _vm.ToDate = to;

        _vm.QuickTimeIndex = 0; // 自定义

        Assert.Equal(from, _vm.FromDate);
        Assert.Equal(to, _vm.ToDate);
    }

    // ════════════════════ 重置 ════════════════════

    [Fact]
    public void Reset_ClearsFiltersToDefaults()
    {
        _vm.SelectedDeviceId = "dev-001";
        _vm.QuickTimeIndex = 1;
        _vm.FromDate = new DateTime(2020, 1, 1);
        _vm.ToDate = new DateTime(2020, 1, 2);

        _vm.ResetCommand.Execute(null);

        Assert.Null(_vm.SelectedDeviceId);
        Assert.Equal(-1, _vm.QuickTimeIndex);
        Assert.Equal(DateTime.Today.AddDays(-1), _vm.FromDate);
        Assert.Equal(DateTime.Today.AddDays(1).AddSeconds(-1), _vm.ToDate);
    }

    // ════════════════════ Tab 0: 产量查询 ════════════════════

    [Fact]
    public void Search_ProductionEmptyResult_SetsIsEmptyResult()
    {
        _vm.SelectedTabIndex = 0;
        _vm.FromDate = new DateTime(2020, 1, 1);
        _vm.ToDate = new DateTime(2020, 1, 2);

        _vm.SearchCommand.Execute(null);

        Assert.True(_vm.HasQueried);
        Assert.Equal(1, _vm.CurrentPage); // Search 重置首页
        Assert.Equal(0, _vm.TotalCount);
        Assert.Equal(0, _vm.TotalPages);
        Assert.True(_vm.IsEmptyResult);
        Assert.Empty(_vm.ProductionQuery.ProductionLogs);
        Assert.Null(_vm.ProductionQuery.ProductionChart); // 无数据时不构建图表
    }

    [Fact]
    public void Search_ProductionWithMultiShift_SumsLastPerShift()
    {
        // 设备A：白班 3 条递增，夜班 2 条递增
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        // 白班窗口前基准（在 FromDate 之前，用于白班窗口差分）
        InsertProductionLog("dev-001", "设备A", "白班", 0, 0, 1, t0.AddMinutes(-5));
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t0.AddMinutes(10));
        InsertProductionLog("dev-001", "设备A", "白班", 150, 8, 1, t0.AddMinutes(20));
        InsertProductionLog("dev-001", "设备A", "白班", 200, 10, 1, t0.AddMinutes(30));
        // 夜班：首条 OK=0 即作为窗口内基准（#2 回退逻辑：无窗口前基准时用首条）
        InsertProductionLog("dev-001", "设备A", "夜班", 0, 0, 1, t0.AddHours(12));
        InsertProductionLog("dev-001", "设备A", "夜班", 80, 4, 1, t0.AddHours(12).AddMinutes(10));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0.AddMinutes(-1);
        _vm.ToDate = t0.AddHours(13);

        _vm.SearchCommand.Execute(null);

        // 窗口内 5 条（白班基准在 FromDate 之前，不计入 TotalCount）
        Assert.Equal(5, _vm.TotalCount);
        // 白班末条 200 − 窗口前基准 0 = 200；夜班末条 80 − 首条 0 = 80 → 合计 OK=280
        Assert.Equal(280, _vm.ProductionQuery.TotalOk);
        // 白班 10 − 0 = 10；夜班 4 − 0 = 4 → 合计 NG=14
        Assert.Equal(14, _vm.ProductionQuery.TotalNg);
        // 合格率 = 280 / (280 + 14) ≈ 0.9523...
        Assert.InRange(_vm.ProductionQuery.QualityRate, 0.95, 0.96);
        Assert.False(_vm.IsEmptyResult);
        Assert.NotNull(_vm.ProductionQuery.ProductionChart);
    }

    [Fact]
    public void Search_ProductionFilterByDevice_ExcludesOtherDevice()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t);
        InsertProductionLog("dev-002", "设备B", "白班", 200, 10, 1, t.AddMinutes(1));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(1, _vm.TotalCount);
        Assert.All(_vm.ProductionQuery.ProductionLogs, p => Assert.Equal("dev-001", p.DeviceId));
    }

    [Fact]
    public void Search_ProductionReturnsEmpty_WhenSelectedDeviceIdNull()
    {
        // 业务修正后设备为必选：未选设备时产量查询直接返回，不跨设备汇总
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t);
        InsertProductionLog("dev-002", "设备B", "白班", 200, 10, 1, t.AddMinutes(1));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = null; // 未选设备
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(0, _vm.TotalCount); // 早退，不返回任何记录
    }

    [Fact]
    public void Search_ProductionPageNavigation_RespectsBounds()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        // 插入 60 条数据，PageSize=50 → 2 页
        for (int i = 0; i < 60; i++)
            InsertProductionLog("dev-001", "设备A", "白班", i, 0, 1, t.AddMinutes(i));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(70);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(60, _vm.TotalCount);
        Assert.Equal(2, _vm.TotalPages);
        Assert.Equal(1, _vm.CurrentPage);
        Assert.Equal(50, _vm.ProductionQuery.ProductionLogs.Count);

        // PreviousPage 在首页应禁用
        Assert.False(_vm.PreviousPageCommand.CanExecute(null));
        // NextPage 应可用
        Assert.True(_vm.NextPageCommand.CanExecute(null));

        // 翻到第 2 页
        _vm.NextPageCommand.Execute(null);
        Assert.Equal(2, _vm.CurrentPage);
        Assert.Equal(10, _vm.ProductionQuery.ProductionLogs.Count); // 60 - 50

        // NextPage 在末页应禁用
        Assert.False(_vm.NextPageCommand.CanExecute(null));
        Assert.True(_vm.PreviousPageCommand.CanExecute(null));

        // 翻回第 1 页
        _vm.PreviousPageCommand.Execute(null);
        Assert.Equal(1, _vm.CurrentPage);
        Assert.Equal(50, _vm.ProductionQuery.ProductionLogs.Count);
    }

    // ════════════════════ Tab 1: 状态时长 ════════════════════

    [Fact]
    public void Search_StatusRequiresDevice_ReturnsEarlyWhenNull()
    {
        // 未选设备 → 直接 return，TotalCount 保持 0
        _vm.SelectedTabIndex = 1;
        _vm.SelectedDeviceId = null;

        _vm.SearchCommand.Execute(null);

        Assert.True(_vm.HasQueried); // Search 仍标记 HasQueried
        Assert.Equal(0, _vm.TotalCount);
        Assert.Equal(0, _vm.StatusQuery.RunTimeSeconds);
        Assert.Equal(0, _vm.StatusQuery.AlarmTimeSeconds);
    }

    [Fact]
    public void Search_StatusWithTransitions_CalculatesDurations()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        // 区间外初始状态：上一条转换在 FromDate 之前，CurrentState=1（运行）
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-30));
        // 区间内：8:00 运行 → 8:10 报警 → 8:20 恢复运行
        InsertStatusTransition("dev-001", "设备A", 1, 2, t0.AddMinutes(10));
        InsertStatusTransition("dev-001", "设备A", 2, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 1;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);

        _vm.SearchCommand.Execute(null);

        // 初始段 8:00-8:10 运行 600s
        // 8:10-8:20 报警 600s
        // 8:20-8:30 运行 600s
        Assert.Equal(1200, _vm.StatusQuery.RunTimeSeconds);
        Assert.Equal(600, _vm.StatusQuery.AlarmTimeSeconds);
        Assert.Equal(2, _vm.TotalCount);
        Assert.NotNull(_vm.StatusQuery.StatusChart);
    }

    // ════════════════════ Tab 2: 报警记录 ════════════════════

    [Fact]
    public void Search_AlarmCountsTriggerRecoverPending()
    {
        var t0 = new DateTime(2026, 7, 23, 10, 0, 0);
        // alm-001：触发→恢复（已恢复）
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温", "M100", AlarmEventType.Triggered, t0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温", "M100", AlarmEventType.Recovered, t0.AddMinutes(5));
        // alm-002：仅触发，未恢复（待恢复）
        InsertAlarmEvent("dev-001", "设备A", "alm-002", "低压", "M101", AlarmEventType.Triggered, t0.AddMinutes(10));

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0.AddMinutes(-1);
        _vm.ToDate = t0.AddMinutes(20);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(3, _vm.TotalCount);
        Assert.Equal(2, _vm.AlarmQuery.AlarmTriggerCount); // 2 个 EventType=1
        Assert.Equal(1, _vm.AlarmQuery.AlarmRecoverCount); // 1 个 EventType=2
        Assert.Equal(1, _vm.AlarmQuery.AlarmPendingCount); // alm-002 未恢复
        Assert.NotNull(_vm.AlarmQuery.AlarmChart);
    }

    [Fact]
    public void Search_AlarmFilterByDevice()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "报警A", "M100", AlarmEventType.Triggered, t);
        InsertAlarmEvent("dev-002", "设备B", "alm-002", "报警B", "M200", AlarmEventType.Triggered, t);

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-002";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(1);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(1, _vm.TotalCount);
        Assert.All(_vm.AlarmQuery.AlarmEvents, e => Assert.Equal("dev-002", e.DeviceId));
    }

    // ════════════════════ Tab 3: OEE 分析 ════════════════════

    [Fact]
    public void Search_OeeRequiresDevice_ReturnsEarlyWhenNull()
    {
        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = null;

        _vm.SearchCommand.Execute(null);

        Assert.True(_vm.HasQueried);
        Assert.Equal(0, _vm.OeeQuery.OeeOkProduction);
        Assert.Equal(0, _vm.OeeQuery.OeeTargetCycle);
    }

    [Fact]
    public void Search_OeeCalculation_UsesDeviceTargetCycle()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        // dev-001 TargetCycle=600
        // 状态：8:00-8:30 运行 1800s（区间内无转换，使用 initialState=1）
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        // 窗口前基准：白班实例内 OK=0/NG=0，用于窗口差分（否则单条快照无增量）
        InsertProductionLog("dev-001", "设备A", "白班", 0, 0, 1, t0.AddMinutes(-5));
        // 产量：白班末条 OK=300, NG=10
        InsertProductionLog("dev-001", "设备A", "白班", 300, 10, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(600, _vm.OeeQuery.OeeTargetCycle);
        // 窗口差分：末条 300 − 窗口前基准 0 = 300
        Assert.Equal(300, _vm.OeeQuery.OeeOkProduction);
        Assert.Equal(10, _vm.OeeQuery.OeeNgProduction);
        Assert.Equal(1800, _vm.OeeQuery.OeeRunTime);

        // 合格率 = 300 / 310
        Assert.InRange(_vm.OeeQuery.OeeQualityRate, 0.967, 0.968);
        // 性能率 = (300+10) / (600 * (1800/3600)) = 310 / 300 → clamp 到 1
        Assert.Equal(1.0, _vm.OeeQuery.OeePerformanceRate);
        // 可用率 = 1800 / (1800 + 0) = 1
        Assert.Equal(1.0, _vm.OeeQuery.OeeAvailabilityRate);
        // OEE = 1 * 1 * 合格率
        Assert.InRange(_vm.OeeQuery.OeeValue, 0.967, 0.968);
        Assert.NotNull(_vm.OeeQuery.OeeChart);
    }

    // ════════════════════ Tab 切换分发 ════════════════════

    [Fact]
    public void QueryCurrentTab_DispatchesBySelectedTabIndex()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "报警", "M100", AlarmEventType.Triggered, t);

        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);

        // Tab 0 → 产量查询
        _vm.SelectedTabIndex = 0;
        _vm.QueryCurrentTabCommand.Execute(null);
        Assert.Equal(1, _vm.TotalCount);
        Assert.Single(_vm.ProductionQuery.ProductionLogs);
        Assert.Empty(_vm.AlarmQuery.AlarmEvents);

        // Tab 2 → 报警查询
        _vm.SelectedTabIndex = 2;
        _vm.QueryCurrentTabCommand.Execute(null);
        Assert.Equal(1, _vm.TotalCount);
        Assert.Single(_vm.AlarmQuery.AlarmEvents);
        // 注意：各 Tab 集合独立，QueryAlarm 不清空 ProductionLogs
        Assert.Single(_vm.ProductionQuery.ProductionLogs);
    }

    // ════════════════════ IsEmptyResult 状态机 ════════════════════

    [Fact]
    public void IsEmptyResult_FalseBeforeAnyQuery()
    {
        Assert.False(_vm.IsEmptyResult); // HasQueried 默认 false
        Assert.False(_vm.HasQueried);
    }

    [Fact]
    public void IsEmptyResult_TrueAfterQueryWithNoData()
    {
        _vm.SelectedTabIndex = 0;
        _vm.FromDate = new DateTime(2020, 1, 1);
        _vm.ToDate = new DateTime(2020, 1, 2);

        Assert.False(_vm.IsEmptyResult);

        _vm.SearchCommand.Execute(null);

        Assert.True(_vm.HasQueried);
        Assert.Equal(0, _vm.TotalCount);
        Assert.True(_vm.IsEmptyResult);
    }

    // ════════════════════ Search 重置到首页 ════════════════════

    [Fact]
    public void Search_AlwaysResetsToFirstPage()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        for (int i = 0; i < 60; i++)
            InsertProductionLog("dev-001", "设备A", "白班", i, 0, 1, t.AddMinutes(i));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(70);

        // 先翻到第 2 页
        _vm.SearchCommand.Execute(null);
        _vm.NextPageCommand.Execute(null);
        Assert.Equal(2, _vm.CurrentPage);

        // 再次查询应回到第 1 页
        _vm.SearchCommand.Execute(null);
        Assert.Equal(1, _vm.CurrentPage);
    }

    // ════════════════════ 班次筛选 ════════════════════

    [Fact]
    public void Search_ProductionFilterByShift()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t);
        InsertProductionLog("dev-001", "设备A", "夜班", 200, 10, 1, t.AddMinutes(1));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);

        // 先查全部，触发班次下拉生成（包含 白班 + 夜班）
        _vm.SearchCommand.Execute(null);
        Assert.Equal(3, _vm.ShiftFilterItems.Count); // 全部 + 白班 + 夜班

        // 选中"白班"再查，应只返回 1 条
        _vm.SelectedShiftName = "白班";
        _vm.SearchCommand.Execute(null);

        Assert.Equal(1, _vm.TotalCount);
        Assert.All(_vm.ProductionQuery.ProductionLogs, p => Assert.Equal("白班", p.ShiftName));
    }

    [Fact]
    public void Search_ProductionShiftFilterItems_RefreshedAfterQuery()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "早班", 100, 5, 1, t);
        InsertProductionLog("dev-001", "设备A", "中班", 100, 5, 1, t.AddMinutes(1));
        InsertProductionLog("dev-001", "设备A", "晚班", 100, 5, 1, t.AddMinutes(2));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(5);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(4, _vm.ShiftFilterItems.Count); // 全部 + 3 个班次
        Assert.Null(_vm.ShiftFilterItems[0].Value);
        Assert.Equal("全部班次", _vm.ShiftFilterItems[0].DisplayText);
    }

    // ════════════════════ 报警类型筛选 ════════════════════

    [Fact]
    public void Search_AlarmFilterByName()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, t);
        InsertAlarmEvent("dev-001", "设备A", "alm-002", "低压报警", "M101", AlarmEventType.Triggered, t.AddMinutes(1));

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);

        // 先查询一次，让 AlarmNameFilterItems 填充
        _vm.SearchCommand.Execute(null);
        Assert.Equal(3, _vm.AlarmNameFilterItems.Count); // 全部 + 高温报警 + 低压报警

        // 选中"高温报警"再查
        _vm.SelectedAlarmName = "高温报警";
        _vm.SearchCommand.Execute(null);

        Assert.Equal(1, _vm.TotalCount);
        Assert.All(_vm.AlarmQuery.AlarmEvents, e => Assert.Equal("高温报警", e.AlarmName));
    }

    // ════════════════════ 智能快捷时间 ════════════════════

    [Fact]
    public void QuickTimeIndex_ThisWeek_SetsMondayToSundayRange()
    {
        // 配置班次，避免无班次时本班次测试返回原值
        _appSettings.Shifts.Clear();
        _appSettings.Shifts.Add(new ShiftConfig { Name = "全天", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(0, 0, 0) });

        _vm.QuickTimeIndex = 7; // 本周

        var now = DateTime.Now;
        int diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = now.Date.AddDays(-diff);
        var sundayEnd = monday.AddDays(7).AddSeconds(-1);

        Assert.Equal(monday, _vm.FromDate);
        Assert.Equal(sundayEnd, _vm.ToDate);
    }

    [Fact]
    public void QuickTimeIndex_ThisMonth_SetsFirstToLastDayRange()
    {
        _vm.QuickTimeIndex = 8; // 本月

        var now = DateTime.Now;
        var firstDay = new DateTime(now.Year, now.Month, 1);
        var lastDay = firstDay.AddMonths(1).AddSeconds(-1);

        Assert.Equal(firstDay, _vm.FromDate);
        Assert.Equal(lastDay, _vm.ToDate);
    }

    [Fact]
    public void QuickTimeIndex_CurrentShift_UsesAppSettingsShifts()
    {
        // 配置一个 8:00-20:00 白班
        _appSettings.Shifts.Clear();
        _appSettings.Shifts.Add(new ShiftConfig { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) });

        var originalFrom = _vm.FromDate;
        _vm.QuickTimeIndex = 5; // 本班次

        var now = DateTime.Now;
        var timeOfDay = now.TimeOfDay;
        // 若当前在 8:00-20:00 之间，本班次应等于今天 8:00 到今天 20:00
        if (timeOfDay >= new TimeSpan(8, 0, 0) && timeOfDay < new TimeSpan(20, 0, 0))
        {
            Assert.Equal(now.Date.AddHours(8), _vm.FromDate);
            Assert.Equal(now.Date.AddHours(20), _vm.ToDate);
        }
        // 否则 GetShiftRange 找不到班次，保持原值
        // （夜间 20:00-24:00 不属于任何班次，因为 0:00-8:00 也没配置）
        // 测试不强校验此分支，避免依赖运行时间
    }

    // ════════════════════ Reset 包含新筛选 ════════════════════

    [Fact]
    public void Reset_ClearsShiftAndAlarmNameFilters()
    {
        _vm.SelectedDeviceId = "dev-001";
        _vm.SelectedShiftName = "白班";
        _vm.SelectedAlarmName = "高温";
        _vm.QuickTimeIndex = 1;

        _vm.ResetCommand.Execute(null);

        Assert.Null(_vm.SelectedDeviceId);
        Assert.Null(_vm.SelectedShiftName);
        Assert.Null(_vm.SelectedAlarmName);
        Assert.Equal(-1, _vm.QuickTimeIndex);
    }

    // ════════════════════ 智能洞察 ════════════════════

    [Fact]
    public void Search_ProductionInsight_SetWhenDataExists()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        // 多条递增，制造峰值差异
        for (int i = 0; i < 10; i++)
            InsertProductionLog("dev-001", "设备A", "白班", i * 10, 0, 1, t.AddMinutes(i * 15));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddHours(3);

        _vm.SearchCommand.Execute(null);

        Assert.NotNull(_vm.ProductionQuery.ProductionInsight);
        Assert.Contains("峰值", _vm.ProductionQuery.ProductionInsight);
    }

    [Fact]
    public void Search_AlarmInsight_TopAlarmsGenerated()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        // 高温报警触发 3 次，低压报警触发 1 次
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, t);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, t.AddMinutes(10));
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, t.AddMinutes(20));
        InsertAlarmEvent("dev-001", "设备A", "alm-002", "低压报警", "M101", AlarmEventType.Triggered, t.AddMinutes(30));

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(60);

        _vm.SearchCommand.Execute(null);

        Assert.NotNull(_vm.AlarmQuery.AlarmInsight);
        Assert.Contains("高温报警", _vm.AlarmQuery.AlarmInsight);
        Assert.Contains("3次", _vm.AlarmQuery.AlarmInsight);
    }

    [Fact]
    public void Search_OeeInsight_BottleneckIdentifiedWhenLow()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        // 大量 NG，拉低合格率
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        InsertProductionLog("dev-001", "设备A", "白班", 100, 80, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);

        _vm.SearchCommand.Execute(null);

        Assert.NotNull(_vm.OeeQuery.OeeInsight);
        // 合格率 = 100 / 180 ≈ 0.556 < 0.6，应提示瓶颈
        Assert.Contains("瓶颈", _vm.OeeQuery.OeeInsight);
        Assert.Contains("C良品率", _vm.OeeQuery.OeeInsight);
    }

    [Fact]
    public void Search_StatusInsight_LongestRunAndAlarmGenerated()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        InsertStatusTransition("dev-001", "设备A", 1, 2, t0.AddMinutes(10));
        InsertStatusTransition("dev-001", "设备A", 2, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 1;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);

        _vm.SearchCommand.Execute(null);

        Assert.NotNull(_vm.StatusQuery.StatusInsight);
        Assert.Contains("最长运行", _vm.StatusQuery.StatusInsight);
        Assert.Contains("最长报警", _vm.StatusQuery.StatusInsight);
    }

    // ════════════════════ 异常点标注 ════════════════════

    [Fact]
    public void Search_ProductionChartAnnotations_AddedWhenSuddenDrop()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        // 制造 OK 突降：100 → 50（跌幅 50%）
        InsertProductionLog("dev-001", "设备A", "白班", 100, 0, 1, t);
        InsertProductionLog("dev-001", "设备A", "白班", 50, 0, 1, t.AddMinutes(15));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(20);

        _vm.SearchCommand.Execute(null);

        Assert.NotNull(_vm.ProductionQuery.ProductionChart);
        // 突降点应被标注（至少 1 个 PointAnnotation）
        Assert.NotEmpty(_vm.ProductionQuery.ProductionChart!.Annotations);
    }

    // ════════════════════ 空状态码 ════════════════════

    [Fact]
    public void EmptyStateCode_NotQueriedBeforeSearch()
    {
        _vm.SelectedTabIndex = 1; // 状态 Tab
        Assert.Equal(0, _vm.EmptyStateCode); // 未查询
    }

    [Fact]
    public void EmptyStateCode_NoDeviceWhenTabRequiresDevice()
    {
        _vm.SelectedTabIndex = 1; // 状态 Tab
        _vm.SelectedDeviceId = null;
        _vm.SearchCommand.Execute(null);

        Assert.Equal(1, _vm.EmptyStateCode); // NoDevice
    }

    [Fact]
    public void EmptyStateCode_NoDataWhenDeviceSelectedButEmpty()
    {
        // 已选设备但查询区间无数据 → EmptyStateCode=2 (NoData)
        _vm.SelectedTabIndex = 0; // 产量 Tab
        _vm.SelectedDeviceId = "dev-001"; // 选中设备
        _vm.FromDate = new DateTime(2020, 1, 1);
        _vm.ToDate = new DateTime(2020, 1, 2);

        _vm.SearchCommand.Execute(null);

        Assert.Equal(2, _vm.EmptyStateCode); // NoData
    }

    // ════════════════════ 会话级条件记忆 ════════════════════

    [Fact]
    public void SaveAndRestoreLastQuery_RestoresAllFields()
    {
        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.QuickTimeIndex = -1;
        _vm.FromDate = new DateTime(2026, 1, 15, 10, 0, 0);
        _vm.ToDate = new DateTime(2026, 1, 16, 18, 0, 0);
        _vm.SelectedShiftName = "白班";

        _vm.SaveLastQuery();

        // 改变条件
        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = null;
        _vm.QuickTimeIndex = 1;
        _vm.SelectedShiftName = null;

        // 恢复
        _vm.RestoreLastQuery();

        Assert.Equal(2, _vm.SelectedTabIndex);
        Assert.Equal("dev-001", _vm.SelectedDeviceId);
        Assert.Equal(-1, _vm.QuickTimeIndex);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 0, 0), _vm.FromDate);
        Assert.Equal(new DateTime(2026, 1, 16, 18, 0, 0), _vm.ToDate);
        Assert.Equal("白班", _vm.SelectedShiftName);
    }

    [Fact]
    public void RestoreLastQuery_NoOpWhenNeverSaved()
    {
        var beforeTab = _vm.SelectedTabIndex;
        var beforeDevice = _vm.SelectedDeviceId;

        _vm.RestoreLastQuery();

        Assert.Equal(beforeTab, _vm.SelectedTabIndex);
        Assert.Equal(beforeDevice, _vm.SelectedDeviceId);
    }

    // ════════════════════ 导出 CSV ════════════════════

    [Fact]
    public async Task Export_ProductionWritesCsvFile()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t);
        InsertProductionLog("dev-001", "设备A", "白班", 200, 10, 1, t.AddMinutes(1));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(2);
        _vm.SearchCommand.Execute(null);

        await _vm.ExportCommand.ExecuteAsync(null);

        var exportDir = System.IO.Path.Combine(_appSettings.ConfigDirectory, "Exports");
        var files = System.IO.Directory.GetFiles(exportDir, "产量_*.csv");
        Assert.Single(files);
        var content = System.IO.File.ReadAllText(files[0], System.Text.Encoding.UTF8);
        Assert.Contains("时间,设备ID,设备名称,班次,OK产量,NG产量,状态字", content);
        Assert.Contains("设备A", content);
        // 末尾应含 KPI 摘要
        Assert.Contains("# 总 OK", content);
        // UTF-8 BOM：通过读取原始字节验证
        var bytes = System.IO.File.ReadAllBytes(files[0]);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public async Task Export_AlarmWritesCsvFile()
    {
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, t);

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(1);
        _vm.SearchCommand.Execute(null);

        await _vm.ExportCommand.ExecuteAsync(null);

        var exportDir = System.IO.Path.Combine(_appSettings.ConfigDirectory, "Exports");
        var files = System.IO.Directory.GetFiles(exportDir, "报警_*.csv");
        Assert.Single(files);
        var content = System.IO.File.ReadAllText(files[0], System.Text.Encoding.UTF8);
        Assert.Contains("事件时间,设备ID,设备名称,报警ID,报警名称", content);
        Assert.Contains("高温报警", content);
    }

    [Fact]
    public async Task Export_CsvEscapesCommaInAlarmName()
    {
        // 报警名包含逗号，CSV 应转义
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "温度,过高", "M100", AlarmEventType.Triggered, t);

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(1);
        _vm.SearchCommand.Execute(null);

        await _vm.ExportCommand.ExecuteAsync(null);

        var exportDir = System.IO.Path.Combine(_appSettings.ConfigDirectory, "Exports");
        var files = System.IO.Directory.GetFiles(exportDir, "报警_*.csv");
        var content = System.IO.File.ReadAllText(files[0], System.Text.Encoding.UTF8);
        // 应该用双引号包裹
        Assert.Contains("\"温度,过高\"", content);
    }

    // ════════════════════ 行高亮阈值常量 ════════════════════

    [Fact]
    public void NgAlarmThreshold_IsFive()
    {
        // 与 View 中 DataTrigger ConverterParameter=">=5" 保持一致
        Assert.Equal(5, HistoryQueryViewModel.NgAlarmThreshold);
    }

    [Fact]
    public void LowPerformanceThreshold_IsSixTenths()
    {
        // 与 View 中 DataTrigger ConverterParameter="<0.6" 保持一致
        Assert.Equal(0.6, HistoryQueryViewModel.LowPerformanceThreshold, 3);
    }

    // ════════════════════ 产量 Tab：NG 超阈值行数据 ════════════════════

    [Fact]
    public void Search_ProductionRows_IncludeHighNgRowsForHighlight()
    {
        // 验证查询结果中存在 NgProduction >= NgAlarmThreshold 的行，供 View 高亮
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 100, 3, 1, t);   // NG=3 不高亮
        InsertProductionLog("dev-001", "设备A", "白班", 200, 8, 1, t.AddMinutes(1)); // NG=8 高亮
        InsertProductionLog("dev-001", "设备A", "白班", 300, 15, 1, t.AddMinutes(2)); // NG=15 高亮

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(5);
        _vm.SearchCommand.Execute(null);

        var highNgRows = _vm.ProductionQuery.ProductionLogs.Where(p => p.NgProduction >= HistoryQueryViewModel.NgAlarmThreshold).ToList();
        Assert.Equal(2, highNgRows.Count);
        Assert.Contains(highNgRows, p => p.NgProduction == 8);
        Assert.Contains(highNgRows, p => p.NgProduction == 15);
        // NG=3 的行不应在待高亮集合
        Assert.DoesNotContain(highNgRows, p => p.NgProduction == 3);
    }

    // ════════════════════ 报警 Tab：触发事件行 ════════════════════

    [Fact]
    public void Search_AlarmRows_IncludeTriggerEventsForHighlight()
    {
        // 验证查询结果中存在 EventType=Triggered（触发）的行，供 View 高亮
        var t = new DateTime(2026, 7, 23, 10, 0, 0);
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温", "M100", AlarmEventType.Triggered, t);       // 触发 - 高亮
        InsertAlarmEvent("dev-001", "设备A", "alm-001", "高温", "M100", AlarmEventType.Recovered, t.AddMinutes(5)); // 恢复 - 不高亮
        InsertAlarmEvent("dev-001", "设备A", "alm-002", "低压", "M101", AlarmEventType.Triggered, t.AddMinutes(10)); // 触发 - 高亮

        _vm.SelectedTabIndex = 2;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t.AddMinutes(-1);
        _vm.ToDate = t.AddMinutes(20);
        _vm.SearchCommand.Execute(null);

        var triggerRows = _vm.AlarmQuery.AlarmEvents.Where(e => e.EventType == AlarmEventType.Triggered).ToList();
        Assert.Equal(2, triggerRows.Count);
        Assert.All(triggerRows, e => Assert.Equal(AlarmEventType.Triggered, e.EventType));
    }

    // ════════════════════ OEE Tab：按班次明细表格 ════════════════════

    [Fact]
    public void Search_OeeShiftDetails_PopulatedAfterQuery()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        InsertProductionLog("dev-001", "设备A", "白班", 300, 10, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);
        _vm.SearchCommand.Execute(null);

        // 查询后应至少有 1 个班次明细记录
        Assert.NotEmpty(_vm.OeeQuery.OeeShiftDetails);
        var first = _vm.OeeQuery.OeeShiftDetails[0];
        Assert.Equal("白班", first.ShiftName);
        // 各指标应在 [0, 1] 范围
        Assert.InRange(first.Quality, 0, 1);
        Assert.InRange(first.Performance, 0, 1);
        Assert.InRange(first.Availability, 0, 1);
        Assert.InRange(first.Oee, 0, 1);
    }

    [Fact]
    public void Search_OeeShiftDetails_LowPerformanceShiftIdentified()
    {
        // 构造低性能率场景：实际产量远低于目标节拍产能
        // dev-001 TargetCycle=600 个/小时 → 期望 1 小时产 600 个
        // 但实际只产 50 个 OK → 性能率 = 50 / 600 ≈ 0.083 < 0.6
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        InsertProductionLog("dev-001", "设备A", "白班", 50, 0, 1, t0.AddMinutes(55));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(60);
        _vm.SearchCommand.Execute(null);

        Assert.NotEmpty(_vm.OeeQuery.OeeShiftDetails);
        var lowPerfShifts = _vm.OeeQuery.OeeShiftDetails
            .Where(s => s.Performance < HistoryQueryViewModel.LowPerformanceThreshold)
            .ToList();
        Assert.NotEmpty(lowPerfShifts);
        Assert.All(lowPerfShifts, s => Assert.True(s.Performance < 0.6));
    }

    [Fact]
    public void Search_OeeShiftDetails_ClearedOnRequery()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10));
        InsertProductionLog("dev-001", "设备A", "白班", 100, 5, 1, t0.AddMinutes(20));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0;
        _vm.ToDate = t0.AddMinutes(30);
        _vm.SearchCommand.Execute(null);
        var firstCount = _vm.OeeQuery.OeeShiftDetails.Count;
        Assert.True(firstCount > 0);

        // 再次查询（无数据的时间范围）
        _vm.FromDate = new DateTime(2020, 1, 1);
        _vm.ToDate = new DateTime(2020, 1, 2);
        _vm.SearchCommand.Execute(null);

        // 重新查询时应已清空（无班次明细）
        Assert.Empty(_vm.OeeQuery.OeeShiftDetails);
    }

    // ════════════════════ ComparisonConverter 单元测试 ════════════════════

    [Theory]
    [InlineData(5, ">=5", true)]
    [InlineData(10, ">=5", true)]
    [InlineData(4, ">=5", false)]
    [InlineData(0, ">=5", false)]
    [InlineData(0.5, "<0.6", true)]
    [InlineData(0.6, "<0.6", false)]
    [InlineData(0.59, "<0.6", true)]
    [InlineData(0, "<0.6", true)]
    [InlineData(10, "==10", true)]
    [InlineData(11, "==10", false)]
    [InlineData(11, "!=10", true)]
    [InlineData(10, ">9", true)]
    [InlineData(9, ">9", false)]
    [InlineData(5, "<=5", true)]
    [InlineData(6, "<=5", false)]
    public void ComparisonConverter_EvaluatesCorrectly(double value, string expr, bool expected)
    {
        var conv = new MainAPP.Converters.ComparisonConverter();
        var result = conv.Convert(value, typeof(bool), expr, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComparisonConverter_ReturnsFalseOnInvalidParameter()
    {
        var conv = new MainAPP.Converters.ComparisonConverter();
        Assert.False((bool)conv.Convert(5, typeof(bool), "invalid", System.Globalization.CultureInfo.InvariantCulture));
        Assert.False((bool)conv.Convert(5, typeof(bool), "", System.Globalization.CultureInfo.InvariantCulture));
        // 用变量传 null 避免 CS8625；用 ! 抑制 CS8604（Converter 内部已处理 null）
        object? nullValue = null;
        Assert.False((bool)conv.Convert(nullValue!, typeof(bool), ">=5", System.Globalization.CultureInfo.InvariantCulture));
    }

    // ════════════════════ P0：产量与时间窗口同口径（窗口差分） ════════════════════

    [Fact]
    public void Search_OeeSubsetWindow_ProductionScopedToWindowNotFullShift()
    {
        // P0 修复验证：OEE 产量按查询窗口差分，而非取窗口内末条累计；
        // 否则非整班次子集窗口下分子覆盖整班次、分母只算窗口 → 性能率被高估，与主页 OEE 对不上。
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0); // 白班 08:00 起
        InsertStatusTransition("dev-001", "设备A", 0, 1, t0.AddMinutes(-10)); // 全程运行
        // 班次内累计：08:00→0, 10:00→200(前两小时产200), 14:00→400(再产200), 20:00→600
        InsertProductionLog("dev-001", "设备A", "白班", 0, 0, 1, t0);
        InsertProductionLog("dev-001", "设备A", "白班", 200, 0, 1, t0.AddHours(2));
        InsertProductionLog("dev-001", "设备A", "白班", 400, 0, 1, t0.AddHours(6));
        InsertProductionLog("dev-001", "设备A", "白班", 600, 0, 1, t0.AddHours(12));

        _vm.SelectedTabIndex = 3;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0.AddHours(2); // 子集窗口 10:00
        _vm.ToDate = t0.AddHours(6);   // 到 14:00

        _vm.SearchCommand.Execute(null);

        // 窗口内(10:00-14:00)实际产量 = 400 - 200 = 200；旧实现会取 400 导致性能率虚高
        Assert.Equal(200, _vm.OeeQuery.OeeOkProduction);
        Assert.Equal(0, _vm.OeeQuery.OeeNgProduction);
        // 运行时间 = 4h = 14400s
        Assert.Equal(14400, _vm.OeeQuery.OeeRunTime);
        // 性能率 = 200 / (600 * 4) ≈ 0.0833（与窗口时间口径一致）
        Assert.InRange(_vm.OeeQuery.OeePerformanceRate, 0.08, 0.09);
    }

    [Fact]
    public void Search_ProductionSubsetWindow_TotalOkScopedToWindow()
    {
        // 产量页"总OK"应与 OEE 页一致：子集窗口内产量 = 末条累计 − 窗口起点累计，而非末条累计。
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        InsertProductionLog("dev-001", "设备A", "白班", 0, 0, 1, t0);
        InsertProductionLog("dev-001", "设备A", "白班", 200, 0, 1, t0.AddHours(2));
        InsertProductionLog("dev-001", "设备A", "白班", 400, 0, 1, t0.AddHours(6));

        _vm.SelectedTabIndex = 0;
        _vm.SelectedDeviceId = "dev-001";
        _vm.FromDate = t0.AddHours(2); // 10:00
        _vm.ToDate = t0.AddHours(6);   // 14:00

        _vm.SearchCommand.Execute(null);

        // 窗口内(10:00-14:00)产量 = 400 - 200 = 200，而非末条累计 400
        Assert.Equal(200, _vm.ProductionQuery.TotalOk);
        Assert.Equal(0, _vm.ProductionQuery.TotalNg);
    }

    /// <summary>不弹窗的 IDialogService 桩，避免测试中 MessageBox/Growl 阻塞。</summary>
    private sealed class StubDialogService : IDialogService
    {
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
            => MessageBoxResult.OK;
        public void NotifySuccess(string message) { }
        public void NotifyWarning(string message) { }
        public void NotifyError(string message) { }
        public void NotifyInfo(string message) { }
        public string? ShowSaveFileDialog(string title, string defaultFileName, string filter) => null;
        public string? ShowOpenFileDialog(string title, string filter) => null;
        public MainAPP.Models.DeviceConfigError? ShowConfigErrors(System.Collections.Generic.IReadOnlyList<MainAPP.Models.DeviceConfigError> errors) => null;
        public string? ShowPasswordInput(string title, string message) => null;
        public Kanban.Core.Entities.WorkOrder? ShowWorkOrderEditor(Kanban.Core.Entities.WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null) => null;
    }
}
