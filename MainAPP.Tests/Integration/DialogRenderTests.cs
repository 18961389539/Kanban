using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Views;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.Tests.Integration;

/// <summary>
/// 独立对话框 UI 渲染冒烟测试：验证 ConfigErrorDialog / PasswordInputDialog
/// 在共享 STA 线程中可实例化、XAML 解析无异常、数据绑定无错误，且关键视觉元素就位。
/// 不做深度交互断言（交互由 ViewModel 层 FakeDialogService 接缝覆盖）。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class DialogRenderTests : WpfTestHost
{
    private readonly ITestOutputHelper _output;

    public DialogRenderTests(WpfStaFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public void ConfigErrorDialog_Loads_WithErrors()
    {
        var errors = new List<DeviceConfigError>
        {
            new() { Device = new Device { Name = "注塑机A1" }, TargetTabIndex = 0, Message = "配方地址缺失" },
            new() { Device = new Device { Name = "焊接机器人B2" }, TargetTabIndex = 1, Message = "报警名重复" },
        };

        RunOnSta(app =>
        {
            var dialog = new ConfigErrorDialog(errors);
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            dialog.UpdateLayout();

            // 关键视觉元素就位
            var listBox = FindVisualDescendants<ListBox>(dialog).FirstOrDefault();
            Assert.NotNull(listBox);
            Assert.Equal(2, listBox.Items.Count);

            // DataContext=this，Errors 属性对外可读
            Assert.Same(errors, dialog.Errors);
            Assert.Null(dialog.SelectedError);

            dialog.Close();
        });

        AssertNoBindingErrors();
    }

    [Fact]
    public void ConfigErrorDialog_Loads_EmptyList_NoException()
    {
        RunOnSta(app =>
        {
            var dialog = new ConfigErrorDialog(new List<DeviceConfigError>());
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            dialog.UpdateLayout();

            var listBox = FindVisualDescendants<ListBox>(dialog).FirstOrDefault();
            Assert.NotNull(listBox);
            Assert.Empty(listBox.Items);

            dialog.Close();
        });

        AssertNoBindingErrors();
    }

    [Fact]
    public void PasswordInputDialog_Loads_AndHasPasswordBox()
    {
        RunOnSta(app =>
        {
            var dialog = new PasswordInputDialog("请输入管理员密码", "导出配置需要验证身份");
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            dialog.UpdateLayout();

            // 构造参数已正确绑定到窗口
            Assert.Equal("请输入管理员密码", dialog.Title);
            Assert.Equal("导出配置需要验证身份", dialog.Message);

            // 关键输入元素就位
            var pwdBox = FindVisualDescendants<PasswordBox>(dialog).FirstOrDefault();
            Assert.NotNull(pwdBox);

            dialog.Close();
        });

        AssertNoBindingErrors();
    }

    [Fact]
    public void PasswordInputDialog_SetPassword_ReadsBack()
    {
        RunOnSta(app =>
        {
            var dialog = new PasswordInputDialog("密码", "提示");
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            dialog.UpdateLayout();

            var pwdBox = FindVisualDescendants<PasswordBox>(dialog).FirstOrDefault();
            Assert.NotNull(pwdBox);
            pwdBox.Password = "s3cr3t";

            // 暴露的 Password 属性应回读输入值
            Assert.Equal("s3cr3t", dialog.Password);

            dialog.Close();
        });

        AssertNoBindingErrors();
    }

    private void AssertNoBindingErrors()
    {
        var severe = BindingErrors
            .Where(e => e.Contains("System.Windows.Data Error"))
            .ToList();
        if (severe.Count == 0) return;

        foreach (var e in severe)
            _output.WriteLine("Binding error: " + e);
        Assert.Empty(severe);
    }
}
