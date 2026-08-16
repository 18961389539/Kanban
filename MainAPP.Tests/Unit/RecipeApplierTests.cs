using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 配方下发执行器（RecipeApplier）单测：伪 IDeviceAdapter 驱动，覆盖写 PLC 全链路——
/// 未连接/校验失败/备份失败/写失败回滚/读回不一致回滚/成功/取消回滚。
/// 装配：真实 PlcConnectionManager（[ObservableProperty] 的 IsConnected 是 public setter，可直接置位）
/// + NSubstitute 的 IPlcDriver / IDeviceAdapterResolver / IDeviceAdapter。
/// </summary>
public class RecipeApplierTests
{
    private static readonly Recipe ValidRecipe = new()
    {
        Name = "测试配方",
        Items =
        {
            new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" },
        },
    };

    private static Device TestDevice() => new() { Name = "测试设备", MachineType = "注塑机" };

    /// <summary>装配 RecipeApplier + 伪 IDeviceAdapter；connected=false 时模拟 PLC 未连接。</summary>
    private static (RecipeApplier Applier, IDeviceAdapter Adapter) CreateApplier(bool connected = true)
    {
        var services = new ServiceCollection();
        var settings = new AppSettings();
        services.AddSingleton(settings);
        var driver = Substitute.For<IPlcDriver>();
        services.AddSingleton(driver);
        var manager = new PlcConnectionManager(driver, settings)
        {
            IsConnected = connected,
        };
        services.AddSingleton(manager);

        var adapter = Substitute.For<IDeviceAdapter>();
        var resolver = Substitute.For<IDeviceAdapterResolver>();
        resolver.Resolve(Arg.Any<Device>()).Returns(adapter);
        services.AddSingleton<IDeviceAdapterResolver>(resolver);

        var provider = services.BuildServiceProvider();
        return (new RecipeApplier(provider), adapter);
    }

    /// <summary>默认成功行为：读回原值、写入成功（Int32 项默认地址 D108 值 50）。</summary>
    private static void SetupHappyPath(IDeviceAdapter adapter)
    {
        adapter.ReadInt32(Arg.Any<string>()).Returns(PlcOperationResult<int>.Success(50));
        adapter.WriteInt32(Arg.Any<string>(), Arg.Any<int>()).Returns(PlcOperationResult.Success());
    }

    [Fact]
    public void Apply_NotConnected_FailsWithPlcNotConnected()
    {
        var (applier, _) = CreateApplier(connected: false);

        var result = applier.Apply(TestDevice(), ValidRecipe);

        Assert.False(result.Success);
        Assert.Contains("PLC 未连接", result.Message);
    }

    [Fact]
    public void Apply_InvalidRecipe_FailsWithValidation()
    {
        var (applier, _) = CreateApplier();
        var invalid = new Recipe { Name = "" }; // 空名 + 空参数项 → 校验失败

        var result = applier.Apply(TestDevice(), invalid);

        Assert.False(result.Success);
        Assert.Contains("配方", result.Message);
    }

    [Fact]
    public void Apply_BackupReadFailed_AbortsWithoutWrite()
    {
        var (applier, adapter) = CreateApplier();
        adapter.ReadInt32(Arg.Any<string>()).Returns(PlcOperationResult<int>.Fail("读失败"));

        var result = applier.Apply(TestDevice(), ValidRecipe);

        Assert.False(result.Success);
        Assert.Contains("备份", result.Message);
        adapter.DidNotReceiveWithAnyArgs().WriteInt32(default!, default);
    }

    [Fact]
    public void Apply_WriteFailed_RollsBackWrittenValue()
    {
        var (applier, adapter) = CreateApplier();
        SetupHappyPath(adapter);
        adapter.WriteInt32("D108", 50).Returns(PlcOperationResult.Fail("写失败")); // 写失败（回滚写同值也会失败）

        var result = applier.Apply(TestDevice(), ValidRecipe);

        Assert.False(result.Success);
        Assert.Contains("已回滚", result.Message);
        // 写 1 次（下发）+ 回滚 1 次（备份值 50 写回）= 2 次；失败场景回滚尽力而为
        adapter.Received(2).WriteInt32("D108", 50);
    }

    [Fact]
    public void Apply_ReadBackMismatch_RollsBack()
    {
        var (applier, adapter) = CreateApplier();
        // 备份读回 50，写后再读回 60 → 校验不一致
        var calls = 0;
        adapter.ReadInt32(Arg.Any<string>()).Returns(_ =>
        {
            calls++;
            return PlcOperationResult<int>.Success(calls == 1 ? 50 : 60);
        });
        adapter.WriteInt32(Arg.Any<string>(), Arg.Any<int>()).Returns(PlcOperationResult.Success());

        var result = applier.Apply(TestDevice(), ValidRecipe);

        Assert.False(result.Success);
        Assert.Contains("校验不一致", result.Message);
        adapter.Received(2).WriteInt32("D108", 50); // 下发 1 次 + 回滚 1 次
    }

    [Fact]
    public void Apply_WriteFailed_WhenBackupDiffersFromNewValue_RollsBackBackupValue()
    {
        // 回归：Rollback 必须写回「写入前备份值」而非配方新值（假回滚缺陷）。
        // 第 1 项新值 60、备份值 50；第 2 项写失败 → 第 1 项应被写回 50。
        var (applier, adapter) = CreateApplier();
        var recipe = new Recipe
        {
            Name = "双参数配方",
            Items =
            {
                new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "60" },
                new RecipeItem { ParamName = "温度", PlcAddress = "D110", DataType = PlcDataType.Int32, Value = "70" },
            },
        };
        // 备份读：D108→50、D110→55；写后读回返回新值（D108→60，D110 不会走到读回）
        var reads = new Dictionary<string, int> { ["D108"] = 0, ["D110"] = 0 };
        adapter.ReadInt32(Arg.Any<string>()).Returns(call =>
        {
            var addr = call.Arg<string>();
            reads[addr]++;
            return PlcOperationResult<int>.Success(reads[addr] == 1
                ? (addr == "D108" ? 50 : 55)
                : (addr == "D108" ? 60 : 70));
        });
        adapter.WriteInt32(Arg.Any<string>(), Arg.Any<int>()).Returns(PlcOperationResult.Success());
        adapter.WriteInt32("D110", 70).Returns(PlcOperationResult.Fail("写失败"));

        var result = applier.Apply(TestDevice(), recipe);

        Assert.False(result.Success);
        Assert.Contains("已回滚", result.Message);
        // D108：下发 1 次（60）+ 回滚 1 次（备份值 50）；D110：下发 1 次（70）+ 回滚 1 次（备份值 55）
        adapter.Received(1).WriteInt32("D108", 60);
        adapter.Received(1).WriteInt32("D108", 50);
        adapter.Received(1).WriteInt32("D110", 70);
        adapter.Received(1).WriteInt32("D110", 55);
    }

    [Fact]
    public void Apply_ReadBackMismatch_StringItem_IsNotRolledBack()
    {
        // 回归（审查修复）：String 备份按目标值长度读回，PLC 现值更长时会被截断——
        // 回滚写回截断值会覆盖真实数据，因此 String 项跳过回滚并在失败消息中明示。
        var (applier, adapter) = CreateApplier();
        var recipe = new Recipe
        {
            Name = "字符串参数配方",
            Items =
            {
                new RecipeItem { ParamName = "批次号", PlcAddress = "D200", DataType = PlcDataType.String, Value = "NEW-BATCH" },
                new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" },
            },
        };
        // String 备份读回恒为 OLD-BATCH → 第 1 项读回校验不一致触发回滚
        adapter.ReadString(Arg.Any<string>(), Arg.Any<ushort>()).Returns(PlcOperationResult<string>.Success("OLD-BATCH"));
        adapter.WriteString(Arg.Any<string>(), Arg.Any<string>()).Returns(PlcOperationResult.Success());
        adapter.ReadInt32(Arg.Any<string>()).Returns(PlcOperationResult<int>.Success(50));
        adapter.WriteInt32(Arg.Any<string>(), Arg.Any<int>()).Returns(PlcOperationResult.Success());

        var result = applier.Apply(TestDevice(), recipe);

        Assert.False(result.Success);
        Assert.Contains("校验不一致", result.Message);
        Assert.Contains("字符串参数未自动回滚", result.Message); // 跳过回滚的明示提示
        adapter.Received(1).WriteString("D200", "NEW-BATCH");  // String 仅下发，不回滚
        adapter.Received(2).WriteInt32("D108", 50);            // Int32 项下发 1 次 + 回滚 1 次（回滚仅跳过 String）
    }

    [Fact]
    public void Apply_Success_ReturnsVerifiedPerItem()
    {
        var (applier, adapter) = CreateApplier();
        SetupHappyPath(adapter);

        var result = applier.Apply(TestDevice(), ValidRecipe);

        Assert.True(result.Success);
        var item = Assert.Single(result.Items);
        Assert.True(item.Success);
        Assert.Contains("通过", item.Message);
        adapter.Received(1).WriteInt32("D108", 50); // 无回滚
        adapter.Received(2).ReadInt32("D108");      // 备份 + 读回
    }

    [Fact]
    public void Apply_PreCancelled_RollsBackAndReportsCancelled()
    {
        var (applier, adapter) = CreateApplier();
        SetupHappyPath(adapter);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = applier.Apply(TestDevice(), ValidRecipe, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("已取消", result.Message);
        // 备份循环完成后、写循环第一项前取消 → 回滚把备份值写回 1 次
        adapter.Received(1).WriteInt32("D108", 50);
    }

    [Fact]
    public void Apply_CancelledMidWay_RollsBackCompletedItems()
    {
        var (applier, adapter) = CreateApplier();
        SetupHappyPath(adapter);
        var recipe = new Recipe
        {
            Name = "双参数配方",
            Items =
            {
                new RecipeItem { ParamName = "节拍", PlcAddress = "D108", DataType = PlcDataType.Int32, Value = "50" },
                new RecipeItem { ParamName = "温度", PlcAddress = "D110", DataType = PlcDataType.Int32, Value = "60" },
            },
        };
        using var cts = new CancellationTokenSource();
        // 第一项写入完成后立即取消 → 写循环第二项前抛 OCE → 回滚已写项
        adapter.WriteInt32("D108", 50).Returns(_ =>
        {
            cts.Cancel();
            return PlcOperationResult.Success();
        });

        var result = applier.Apply(TestDevice(), recipe, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("已取消", result.Message);
        // 第一项写 1 次（下发）+ 回滚 1 次（写回备份值 50）；
        // 第二项未下发，回滚同样写回其备份值 50（Rollback 遍历全部备份项，含尚未写入项，保守幂等回滚）
        // 注：SetupHappyPath 对 D108/D110 的备份读回均为 50，故回滚值 = 50（修复前假回滚会写配方新值 60）。
        adapter.Received(2).WriteInt32("D108", 50);
        adapter.Received(1).WriteInt32("D110", 50);
    }
}
