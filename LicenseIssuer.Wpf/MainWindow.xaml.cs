using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LicenseManager.Crypto;

namespace LicenseIssuer.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 默认永久授权：禁用日期选择
        ExpirePicker.IsEnabled = false;
    }

    private void PermanentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ExpirePicker.IsEnabled = PermanentCheckBox.IsChecked != true;
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var machine = MachineCodeBox.Text.Trim();

        // 1. 校验机器码格式（8 字符 Base32：A-Z 2-7）
        if (machine.Length != 8)
        {
            ShowStatus($"机器码必须为 8 字符，当前 {machine.Length} 位。", false);
            return;
        }
        if (!Base32.TryDecode(machine, out var machineHashBytes) || machineHashBytes.Length != 5)
        {
            ShowStatus("机器码不是合法的 Base32 编码（仅允许 A-Z 与 2-7）。", false);
            return;
        }

        // 2. 计算过期日期（UTC，到当天结束）
        DateTime? expireUtc = null;
        if (PermanentCheckBox.IsChecked != true)
        {
            if (ExpirePicker.SelectedDate == null)
            {
                ShowStatus("请选择到期日期，或勾选「永久授权」。", false);
                return;
            }
            expireUtc = ExpirePicker.SelectedDate.Value.Date.AddDays(1); // 本地"次日零点"，编码时向上取整到天，避免提前失效
        }

        // 3. 生成激活码（与客户端共用同一 HMAC 密钥，客户端可验证通过）
        try
        {
            var productKey = ProductKeyCodec.Encode(machineHashBytes, expireUtc);

            var record = new IssuedRecord
            {
                MachineCode = machine,
                ExpireDate = expireUtc,
                IsPermanent = !expireUtc.HasValue,
                ProductKey = productKey,
                IssuedAt = DateTime.UtcNow,
            };
            var savedPath = IssuerStore.Save(record);

            ActivationCodeBox.Text = productKey;
            CopyButton.IsEnabled = true;

            var typeText = expireUtc.HasValue
                ? $"限期授权（到期 {ExpirePicker.SelectedDate:yyyy-MM-dd}）"
                : "永久授权";
            ShowStatus($"已生成{typeText}，记录已保存：{savedPath}", true);
        }
        catch (Exception ex)
        {
            ShowStatus($"生成失败：{ex.Message}", false);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ActivationCodeBox.Text)) return;
        Clipboard.SetText(ActivationCodeBox.Text);
        ShowStatus("激活码已复制到剪贴板。", true);
    }

    private void ShowStatus(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))   // #34D399 绿
            : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));   // #F87171 红
    }
}
