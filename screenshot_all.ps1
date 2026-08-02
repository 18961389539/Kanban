# 用 Win32 API + SendKeys 切换页面并截图
# 避免使用 UIAutomationClient（PS5 兼容性问题）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    public const int SW_RESTORE = 9;
}
'@
Add-Type -TypeDefinition $code -Language CSharp

$savePath = "d:\projects\私活\苏州大屏\Code\Kanban\screenshots"
if (-not (Test-Path $savePath)) { New-Item -ItemType Directory -Path $savePath -Force | Out-Null }

function Take-Screenshot($name) {
    Start-Sleep -Milliseconds 1500
    $proc = Get-Process -Name MainAPP -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $proc) { Write-Host "[ERR] MainAPP not running"; return $false }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host "[ERR] No window handle"; return $false }

    $rect = New-Object WinApi+RECT
    [WinApi]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { Write-Host "[ERR] Invalid window size $w x $h"; return $false }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $full = "$savePath\$name.png"
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  [OK] $name.png ($w x $h)"
    return $true
}

# 1. 激活 MainAPP 窗口（选择有窗口句柄的进程）
$proc = Get-Process -Name MainAPP -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
if ($null -eq $proc) { Write-Host "[ERR] MainAPP not running or no window"; exit 1 }
$hwnd = $proc.MainWindowHandle
[WinApi]::ShowWindow($hwnd, [WinApi]::SW_RESTORE) | Out-Null
Start-Sleep -Milliseconds 300
[WinApi]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

# 2. 截主页
Write-Host "[01_home]"
Take-Screenshot "01_home" | Out-Null

# 3. 切换到其他页面：用 SendKeys 发送方向键
# ListBox 当前选中第0项（主页），按 Down 切换到下一项
# 导航顺序：主页(0) 产线(1) 报警(2) 设备(3) 历史(4) 概览(5) 工单(6) 设置(7)
$names = @("02_production_line", "03_alarm_center", "04_device_manager", "05_history_query", "06_overview", "07_work_order", "08_settings")

foreach ($name in $names) {
    Write-Host "[$name] sending DOWN + ENTER"
    # 确保窗口在前台
    [WinApi]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Take-Screenshot $name | Out-Null
}

Write-Host ""
Write-Host "Done. Screenshots in: $savePath"
Get-ChildItem $savePath | Select-Object Name, Length | Format-Table -AutoSize
