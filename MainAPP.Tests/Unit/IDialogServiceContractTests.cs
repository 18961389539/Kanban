using System.Windows;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IDialogService 契约测试：验证所有可在单元测试中实例化的 IDialogService 实现对同一接口的行为一致性。
///
/// 当前实现：
/// - <see cref="DialogService"/>（生产实现，依赖 HandyControl MessageBox/Growl 与 WPF Application.MainWindow，
///   无法在无 UI 容器的单元测试中实例化，由 E2E 测试覆盖）
/// - <see cref="FakeDialogService"/>（测试桩，记录调用便于断言）
///
/// 契约覆盖：
/// - <see cref="IDialogService.Show"/> 不抛异常并返回有效 <see cref="MessageBoxResult"/>
/// - <see cref="IDialogService.NotifySuccess"/>/<see cref="NotifyWarning"/>/<see cref="NotifyError"/>/<see cref="NotifyInfo"/> 不抛异常
/// - <see cref="IDialogService.ShowSaveFileDialog"/>/<see cref="ShowOpenFileDialog"/> 不抛异常，返回 null 表示取消
/// - <see cref="IDialogService.ShowConfigErrors"/> 不抛异常，传入空列表返回 null
/// - <see cref="IDialogService.ShowPasswordInput"/> 不抛异常
/// - 防御性契约：所有方法对 null/empty 字符串不抛异常
///
/// 若未来新增可在单元测试中实例化的 IDialogService 实现（如基于 Mock 的 LocalDialogService），
/// 应将其加入 <see cref="Implementations"/> 列表，自动跑同一组用例。
/// </summary>
[Trait("Category","Contract")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class IDialogServiceContractTests
{
    /// <summary>
    /// 所有应通过契约测试的 IDialogService 实现工厂。
    /// 使用 TheoryData&lt;T&gt; 强类型成员数据：编译期检查工厂类型，避免运行时 InvalidCastException；
    /// 且测试名显示为工厂类型而非 object[]，可读性更好。
    /// </summary>
    public static TheoryData<Func<IDialogService>> Implementations => new()
    {
        () => new FakeDialogService(),
    };

    // ──────────── 接口契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Implements_IDialogService(Func<IDialogService> factory)
    {
        var svc = factory();
        Assert.IsAssignableFrom<IDialogService>(svc);
    }

    // ──────────── Show 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Show_ReturnsMessageBoxResult(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.Show("消息", "标题", MessageBoxButton.OK, MessageBoxImage.Information);
        Assert.True(Enum.IsDefined(typeof(MessageBoxResult), result));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Show_WithNullMessage_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.Show(null!, "标题", MessageBoxButton.OK, MessageBoxImage.Information);
        Assert.True(Enum.IsDefined(typeof(MessageBoxResult), result));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Show_WithEmptyMessage_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.Show(string.Empty, string.Empty, MessageBoxButton.OK, MessageBoxImage.None);
        Assert.True(Enum.IsDefined(typeof(MessageBoxResult), result));
    }

    [Theory]
    [InlineData(MessageBoxButton.OK)]
    [InlineData(MessageBoxButton.OKCancel)]
    [InlineData(MessageBoxButton.YesNo)]
    [InlineData(MessageBoxButton.YesNoCancel)]
    public void Show_SupportsAllButtonCombinations(MessageBoxButton buttons)
    {
        var svc = new FakeDialogService();
        var result = svc.Show("消息", "标题", buttons, MessageBoxImage.Question);
        Assert.True(Enum.IsDefined(typeof(MessageBoxResult), result));
    }

    [Theory]
    [InlineData(MessageBoxImage.Error)]
    [InlineData(MessageBoxImage.Information)]
    [InlineData(MessageBoxImage.None)]
    [InlineData(MessageBoxImage.Question)]
    public void Show_SupportsAllIconTypes(MessageBoxImage icon)
    {
        var svc = new FakeDialogService();
        var result = svc.Show("消息", "标题", MessageBoxButton.OK, icon);
        Assert.True(Enum.IsDefined(typeof(MessageBoxResult), result));
    }

    // ──────────── Notify* 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifySuccess_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifySuccess("成功消息");
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifyWarning_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifyWarning("警告消息");
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifyError_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifyError("错误消息");
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifyInfo_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifyInfo("信息消息");
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifyMethods_WithNullMessage_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifySuccess(null!);
        svc.NotifyWarning(null!);
        svc.NotifyError(null!);
        svc.NotifyInfo(null!);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void NotifyMethods_WithEmptyMessage_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        svc.NotifySuccess(string.Empty);
        svc.NotifyWarning(string.Empty);
        svc.NotifyError(string.Empty);
        svc.NotifyInfo(string.Empty);
    }

    // ──────────── ShowSaveFileDialog 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowSaveFileDialog_ReturnsStringOrNull(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowSaveFileDialog("保存", "default.json", "JSON|*.json");
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowSaveFileDialog_WithNullArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowSaveFileDialog(null!, null!, null!);
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowSaveFileDialog_WithEmptyArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowSaveFileDialog(string.Empty, string.Empty, string.Empty);
        Assert.True(result is null || result is string);
    }

    // ──────────── ShowOpenFileDialog 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowOpenFileDialog_ReturnsStringOrNull(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowOpenFileDialog("打开", "JSON|*.json");
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowOpenFileDialog_WithNullArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowOpenFileDialog(null!, null!);
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowOpenFileDialog_WithEmptyArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowOpenFileDialog(string.Empty, string.Empty);
        Assert.True(result is null || result is string);
    }

    // ──────────── ShowConfigErrors 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowConfigErrors_WithEmptyList_ReturnsNull(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowConfigErrors(new List<DeviceConfigError>());
        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowConfigErrors_WithNonEmptyList_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        var errors = new List<DeviceConfigError>
        {
            new() { Device = device, TargetTabIndex = 0, Message = "名称不能为空" },
        };
        var result = svc.ShowConfigErrors(errors);
        // 默认行为：未点击任何错误，返回 null
        Assert.True(result is null || result is DeviceConfigError);
    }

    // ──────────── ShowPasswordInput 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowPasswordInput_ReturnsStringOrNull(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowPasswordInput("输入密码", "请输入管理员密码");
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowPasswordInput_WithNullArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowPasswordInput(null!, null!);
        Assert.True(result is null || result is string);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ShowPasswordInput_WithEmptyArgs_DoesNotThrow(Func<IDialogService> factory)
    {
        var svc = factory();
        var result = svc.ShowPasswordInput(string.Empty, string.Empty);
        Assert.True(result is null || result is string);
    }

    // ──────────── 行为一致性契约（Fake 特有，验证桩行为可预测） ────────────

    [Fact]
    public void FakeDialogService_Show_RecordsCallArgs()
    {
        var svc = new FakeDialogService();
        svc.Show("消息1", "标题1", MessageBoxButton.OK, MessageBoxImage.Warning);
        svc.Show("消息2", "标题2", MessageBoxButton.YesNo, MessageBoxImage.Error);

        Assert.Equal(2, svc.ShowCalls.Count);
        Assert.Equal(("消息1", "标题1", MessageBoxButton.OK, MessageBoxImage.Warning), svc.ShowCalls[0]);
        Assert.Equal(("消息2", "标题2", MessageBoxButton.YesNo, MessageBoxImage.Error), svc.ShowCalls[1]);
    }

    [Fact]
    public void FakeDialogService_Show_ReturnsConfiguredResult()
    {
        var svc = new FakeDialogService { ShowResult = MessageBoxResult.No };
        var result = svc.Show("消息", "标题", MessageBoxButton.YesNo, MessageBoxImage.Question);
        Assert.Equal(MessageBoxResult.No, result);
    }

    [Fact]
    public void FakeDialogService_NotifyMethods_RecordsMessages()
    {
        var svc = new FakeDialogService();
        svc.NotifySuccess("s1");
        svc.NotifyWarning("w1");
        svc.NotifyError("e1");
        svc.NotifyInfo("i1");

        Assert.Single(svc.Success, "s1");
        Assert.Single(svc.Warning, "w1");
        Assert.Single(svc.Error, "e1");
        Assert.Single(svc.Info, "i1");
    }

    [Fact]
    public void FakeDialogService_ShowSaveFileDialog_ReturnsConfiguredPath()
    {
        var svc = new FakeDialogService { SaveFilePath = @"C:\temp\file.json" };
        var result = svc.ShowSaveFileDialog("保存", "default.json", "JSON|*.json");
        Assert.Equal(@"C:\temp\file.json", result);
        Assert.Single(svc.SaveFileDialogTitles, "保存");
    }

    [Fact]
    public void FakeDialogService_ShowOpenFileDialog_ReturnsConfiguredPath()
    {
        var svc = new FakeDialogService { OpenFilePath = @"C:\temp\open.json" };
        var result = svc.ShowOpenFileDialog("打开", "JSON|*.json");
        Assert.Equal(@"C:\temp\open.json", result);
        Assert.Single(svc.OpenFileDialogTitles, "打开");
    }

    [Fact]
    public void FakeDialogService_ShowConfigErrors_ReturnsConfiguredError()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        var expectedError = new DeviceConfigError
        {
            Device = device,
            TargetTabIndex = 0,
            Message = "名称不能为空"
        };
        var svc = new FakeDialogService { ShowConfigErrorsResult = expectedError };

        var errors = new List<DeviceConfigError> { expectedError };
        var result = svc.ShowConfigErrors(errors);

        Assert.Same(expectedError, result);
        Assert.Single(svc.ConfigErrorCalls);
        Assert.Same(errors, svc.ConfigErrorCalls[0]);
    }

    [Fact]
    public void FakeDialogService_ShowPasswordInput_ReturnsConfiguredPassword()
    {
        var svc = new FakeDialogService { PasswordInputResult = "secret123" };
        var result = svc.ShowPasswordInput("标题", "请输入密码");

        Assert.Equal("secret123", result);
        Assert.Single(svc.PasswordInputCalls, ("标题", "请输入密码"));
    }
}
