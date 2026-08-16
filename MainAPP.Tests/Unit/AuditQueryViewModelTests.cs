using Kanban.Core.Entities;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 审计查询页 ViewModel 单元测试：快捷时间预设、手动改日期清除预设、重置、
/// 选中记录与成功率统计。IAuditService 用 NSubstitute 桩，不触数据库。
/// </summary>
public class AuditQueryViewModelTests
{
    private readonly IAuditService _audit;
    private readonly IDialogService _dialog;

    public AuditQueryViewModelTests()
    {
        _audit = Substitute.For<IAuditService>();
        _audit.QueryPaged(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns((new List<AuditEntry>(), 0));
        _dialog = Substitute.For<IDialogService>();
    }

    private AuditQueryViewModel NewVm() => new(_audit, _dialog);

    [Fact]
    public void DefaultPresetIsLast7Days()
    {
        var vm = NewVm();

        Assert.Equal(2, vm.PresetIndex);
        Assert.True(vm.From <= DateTime.Today && vm.From >= DateTime.Today.AddDays(-7));
    }

    [Fact]
    public async Task ApplyingTodayPresetSetsRangeAndQueries()
    {
        var vm = NewVm();

        vm.PresetIndex = 1;

        Assert.Equal(1, vm.PresetIndex);
        Assert.Equal(DateTime.Today, vm.From);
        Assert.Equal(1, vm.Page);

        // 查询已后台化（审查修复 2026-08-13），轮询等待后台 QueryPaged 真正执行后再断言调用次数。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _audit.Received(1).QueryPaged(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                    Arg.Any<int>(), Arg.Any<int>());
                return;
            }
            catch
            {
                await Task.Delay(10);
            }
        }

        _audit.Received(1).QueryPaged(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
            Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ApplyingMonthPresetStartsAtFirstDayOfMonth()
    {
        var vm = NewVm();

        vm.PresetIndex = 4;

        Assert.Equal(4, vm.PresetIndex);
        var firstDay = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        Assert.Equal(firstDay, vm.From);
    }

    [Fact]
    public void ManualFromChangeClearsPresetHighlight()
    {
        var vm = NewVm();
        Assert.Equal(2, vm.PresetIndex);

        vm.From = DateTime.Today.AddDays(-3);

        Assert.Equal(0, vm.PresetIndex);
        // 手动改日期不清除查询结果
        _audit.DidNotReceiveWithAnyArgs().QueryPaged(default, default, null, null, null, null, 0, 0);
    }

    [Fact]
    public void ManualToChangeClearsPresetHighlight()
    {
        var vm = NewVm();

        vm.To = DateTime.Now.AddHours(-1);

        Assert.Equal(0, vm.PresetIndex);
    }

    [Fact]
    public void ResetRestoresDefaultPresetAndReQueries()
    {
        var vm = NewVm();
        vm.PresetIndex = 3;
        vm.ResultFilterIndex = 2;

        vm.ResetCommand.Execute(null);

        Assert.Equal(2, vm.PresetIndex);
        Assert.Equal(0, vm.ResultFilterIndex);
        _audit.Received(2).QueryPaged(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
            Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task QueryCountsSucceededAndFailedOnCurrentPage()
    {
        var entries = new List<AuditEntry>
        {
            new() { Succeeded = true },
            new() { Succeeded = true },
            new() { Succeeded = false },
        };
        _audit.QueryPaged(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns((entries, 3));

        var vm = NewVm();
        vm.QueryCommand.Execute(null);

        // 查询已后台化（审查修复 2026-08-13），轮询等待结果回写（无 Dispatcher 环境直接同步回写）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.PageSucceeded != 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(2, vm.PageSucceeded);
        Assert.Equal(1, vm.PageFailed);
        Assert.Equal(66.67, vm.PageSuccessRate, 1);
    }

    [Fact]
    public void SelectedEntryIsStoredForDetailPanel()
    {
        var vm = NewVm();
        var entry = new AuditEntry { Action = "Device.Update", BeforeJson = "{}", AfterJson = "{}" };

        vm.SelectedEntry = entry;

        Assert.Same(entry, vm.SelectedEntry);
    }
}
