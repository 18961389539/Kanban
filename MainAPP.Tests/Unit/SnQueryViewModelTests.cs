using System;
using System.Collections.Generic;
using System.Linq;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// SN 追溯查询 VM（历史查询页「SN 追溯」Tab）单元测试。
/// 通过 <see cref="FakeSnStore"/> 桩注入内存数据，验证空输入/命中/未命中/存储异常/重置等全部分支。
/// 不触发任何 SQLite 文件 IO。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class SnQueryViewModelTests
{
    [Fact]
    public void IsSupported_False_WhenNoStoreInjected()
    {
        var vm = new SnQueryViewModel(null);
        Assert.False(vm.IsSupported);
    }

    [Fact]
    public void IsSupported_True_WhenStoreInjected()
    {
        var vm = new SnQueryViewModel(new FakeSnStore([]));
        Assert.True(vm.IsSupported);
    }

    [Fact]
    public async Task Search_EmptyInput_SetsErrorAndSkipsQuery()
    {
        var vm = new SnQueryViewModel(new FakeSnStore([]));

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(Strings.K921, vm.QueryError);
        Assert.False(vm.HasQueried, "空输入应在查询前返回，不标记已查询");
        Assert.Empty(vm.Results);
        Assert.False(vm.IsEmptyResult);
    }

    [Fact]
    public async Task Search_Found_PopulatesResults()
    {
        var store = new FakeSnStore(new[]
        {
            new SnEventRecord { Sn = "SN-001", DeviceId = "dev-1", DeviceName = "设备A", Result = 0, Timestamp = new DateTime(2026, 1, 1, 9, 0, 0) },
            new SnEventRecord { Sn = "SN-999", DeviceId = "dev-2", DeviceName = "设备B", Result = 1, Timestamp = new DateTime(2026, 1, 1, 9, 5, 0) },
        });
        var vm = new SnQueryViewModel(store);

        vm.SnInput = "SN-001";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasQueried);
        Assert.Null(vm.QueryError);
        Assert.Single(vm.Results);
        Assert.Equal("SN-001", vm.Results[0].Sn);
        Assert.False(vm.IsEmptyResult);
    }

    [Fact]
    public async Task Search_NotFound_SetsError()
    {
        var vm = new SnQueryViewModel(new FakeSnStore([]));

        vm.SnInput = "SN-001";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasQueried);
        Assert.Equal(string.Format(Strings.K922, "SN-001"), vm.QueryError);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsEmptyResult);
    }

    [Fact]
    public async Task Search_StoreThrows_ClearsResultsAndSetsError()
    {
        var boom = new InvalidOperationException("db down");
        var vm = new SnQueryViewModel(new FakeSnStore([], boom));

        vm.SnInput = "SN-001";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasQueried);
        Assert.NotNull(vm.QueryError);
        Assert.Contains(boom.Message, vm.QueryError);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Reset_ClearsAllState()
    {
        var store = new FakeSnStore(new[]
        {
            new SnEventRecord { Sn = "SN-001", DeviceId = "dev-1", DeviceName = "设备A", Timestamp = new DateTime(2026, 1, 1, 9, 0, 0) },
        });
        var vm = new SnQueryViewModel(store);
        vm.SnInput = "SN-001";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Results);

        vm.Reset();

        Assert.Equal(string.Empty, vm.SnInput);
        Assert.Empty(vm.Results);
        Assert.False(vm.HasQueried);
        Assert.Null(vm.QueryError);
    }

    /// <summary>ISnEventStore 内存桩：QueryBySn 精确匹配，可选抛异常模拟存储故障。</summary>
    private sealed class FakeSnStore : ISnEventStore
    {
        private readonly List<SnEventRecord> _records;
        private readonly Exception? _throwOnQuery;

        public FakeSnStore(IEnumerable<SnEventRecord> records, Exception? throwOnQuery = null)
        {
            _records = records.ToList();
            _throwOnQuery = throwOnQuery;
        }

        public void Append(SnEventRecord record) => _records.Add(record);

        public List<SnEventRecord> QueryBySn(string sn)
        {
            if (_throwOnQuery is not null) throw _throwOnQuery;
            return _records.Where(r => r.Sn == sn).ToList();
        }

        public (int Total, List<SnEventRecord> Items) QueryByWorkOrder(int workOrderId, int page, int pageSize)
            => (0, []);

        public (int Total, List<SnEventRecord> Items) QueryByTimeRange(string? deviceId, DateTime from, DateTime to, int page, int pageSize)
            => (0, []);

        public SnEventStoreDiagnosticsSnapshot GetDiagnosticsSnapshot()
            => new();
    }
}
