using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// PlcSimulator + MainAPP 联合仿真测试夹具：封装进程生命周期、配置预置与清理。
/// 每个测试方法创建独立实例，通过独立 TempDir 隔离数据目录，避免相互污染。
/// </summary>
internal sealed class SimulationContext : IDisposable
{
    public string TempDir { get; }
    public Process? Simulator { get; private set; }
    public KanbanAppFixture? App { get; private set; }
    private readonly StringBuilder _simOutput = new();
    private bool _disposed;

    public SimulationContext()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "KanbanSim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(Path.Combine(TempDir, "Config"));
    }

    /// <summary>定位解决方案根目录（从测试 bin 向上查找 Kanban.slnx）</summary>
    private static string LocateSlnRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Kanban.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new FileNotFoundException("未找到 Kanban.slnx 所在的解决方案根目录");
        return dir.FullName;
    }

    /// <summary>预置 devices.json：3 台设备（注塑机1/2、组装机1）</summary>
    public void PrepareDevicesJson()
    {
        var json = """
[
  {
    "Id": "device-001", "Name": "注塑机1",
    "OkCountAddress": "D100", "NgCountAddress": "D102",
    "StatusCountAddress": "D104", "ProductionResetAddress": "D106",
    "RecipeName": "", "RecipeValue": 50, "RecipeAddress": "D108", "TargetCycle": 100,
    "Alarms": [
      {"Id":"device-001_M100","DeviceId":"device-001","Name":"高温报警","PlcAddress":"M100","Duration":"00:00:00","Description":"","Level":0,"StartTime":"0001-01-01T00:00:00","EndTime":"0001-01-01T00:00:00"},
      {"Id":"device-001_M101","DeviceId":"device-001","Name":"低油压报警","PlcAddress":"M101","Duration":"00:00:00","Description":"","Level":0,"StartTime":"0001-01-01T00:00:00","EndTime":"0001-01-01T00:00:00"}
    ],
    "Defects": [
      {"Id":"d001","DeviceId":"device-001","Name":"毛边","Count":0,"PlcAddress":"D110","Severity":0,"Category":0},
      {"Id":"d002","DeviceId":"device-001","Name":"缩水","Count":0,"PlcAddress":"D112","Severity":0,"Category":0}
    ],
    "CountAlarms": [
      {"IsTriggered":false,"Id":"ca001","DeviceId":"device-001","Name":"连续不良","PlcAddress":"D114","MaxValue":10,"CurrentValue":0,"Enabled":true,"Description":"","Unit":""}
    ]
  },
  {
    "Id": "device-002", "Name": "注塑机2",
    "OkCountAddress": "D200", "NgCountAddress": "D202",
    "StatusCountAddress": "D204", "ProductionResetAddress": "D206",
    "RecipeName": "", "RecipeValue": 60, "RecipeAddress": "D208", "TargetCycle": 100,
    "Alarms": [
      {"Id":"device-002_M200","DeviceId":"device-002","Name":"高温报警","PlcAddress":"M200","Duration":"00:00:00","Description":"","Level":0,"StartTime":"0001-01-01T00:00:00","EndTime":"0001-01-01T00:00:00"},
      {"Id":"device-002_M201","DeviceId":"device-002","Name":"门未关报警","PlcAddress":"M201","Duration":"00:00:00","Description":"","Level":0,"StartTime":"0001-01-01T00:00:00","EndTime":"0001-01-01T00:00:00"}
    ],
    "Defects": [
      {"Id":"d003","DeviceId":"device-002","Name":"划痕","Count":0,"PlcAddress":"D210","Severity":0,"Category":0}
    ],
    "CountAlarms": [
      {"IsTriggered":false,"Id":"ca002","DeviceId":"device-002","Name":"停机次数","PlcAddress":"D212","MaxValue":5,"CurrentValue":0,"Enabled":true,"Description":"","Unit":""}
    ]
  },
  {
    "Id": "device-003", "Name": "组装机1",
    "OkCountAddress": "D300", "NgCountAddress": "D302",
    "StatusCountAddress": "D304", "ProductionResetAddress": "D306",
    "RecipeName": "", "RecipeValue": 100, "RecipeAddress": "D308", "TargetCycle": 200,
    "Alarms": [
      {"Id":"device-003_M300","DeviceId":"device-003","Name":"缺料报警","PlcAddress":"M300","Duration":"00:00:00","Description":"","Level":0,"StartTime":"0001-01-01T00:00:00","EndTime":"0001-01-01T00:00:00"}
    ],
    "Defects": [
      {"Id":"d004","DeviceId":"device-003","Name":"漏装","Count":0,"PlcAddress":"D310","Severity":0,"Category":0},
      {"Id":"d005","DeviceId":"device-003","Name":"错装","Count":0,"PlcAddress":"D312","Severity":0,"Category":0}
    ],
    "CountAlarms": [
      {"IsTriggered":false,"Id":"ca003","DeviceId":"device-003","Name":"连续NG","PlcAddress":"D314","MaxValue":8,"CurrentValue":0,"Enabled":true,"Description":"","Unit":""}
    ]
  }
]
""";
        File.WriteAllText(Path.Combine(TempDir, "Config", "devices.json"), json);
    }

    /// <summary>
    /// 预置 settings.json（PLC 127.0.0.1:4999，可选短周期班次）。
    /// shiftMinutes > 0 时配置短周期班次（当前时间开始，每 shiftMinutes 分钟切换一次）。
    /// </summary>
    public void PrepareSettingsJson(int shiftMinutes = 0)
    {
        string json;
        if (shiftMinutes > 0)
        {
            // 短周期班次：A班从当前时间开始，B班紧随其后，各 shiftMinutes 分钟
            var now = DateTime.Now;
            var aStart = now.ToString("HH:mm:ss");
            var aEnd = now.AddMinutes(shiftMinutes).ToString("HH:mm:ss");
            var bEnd = now.AddMinutes(shiftMinutes * 2).ToString("HH:mm:ss");
            json = $$"""
{
  "PlcConfig": { "IpAddress": "127.0.0.1", "Port": 4999 },
  "PollingIntervalMs": 1000,
  "HistoryWriteIntervalScans": 60,
  "DashboardRefreshIntervalMs": 1000,
  "IsDarkTheme": true,
  "UiScale": 1,
  "Shifts": [
    { "Name": "A班", "StartTime": "{{aStart}}", "EndTime": "{{aEnd}}" },
    { "Name": "B班", "StartTime": "{{aEnd}}", "EndTime": "{{bEnd}}" }
  ]
}
""";
        }
        else
        {
            json = """
{
  "PlcConfig": { "IpAddress": "127.0.0.1", "Port": 4999 },
  "PollingIntervalMs": 1000,
  "HistoryWriteIntervalScans": 60,
  "DashboardRefreshIntervalMs": 1000,
  "IsDarkTheme": true,
  "UiScale": 1,
  "Shifts": [
    { "Name": "白班", "StartTime": "08:00:00", "EndTime": "20:00:00" },
    { "Name": "夜班", "StartTime": "20:00:00", "EndTime": "08:00:00" }
  ]
}
""";
        }
        File.WriteAllText(Path.Combine(TempDir, "Config", "settings.json"), json);
    }

    /// <summary>
    /// 预置 work_orders.db。
    /// 默认插入 9 条工单（3 台设备 × 3 种状态）。
    /// singleRunning=true 时仅插入 1 条 Running 工单（WO-TEST-001-R），便于工单完成/中止测试精确定位。
    /// firstTargetQty 覆盖 WO-TEST-001-R 的目标产量（产量达标测试用小值快速触发）。
    /// </summary>
    public void PrepareWorkOrdersDb(bool singleRunning = false, int firstTargetQty = 1000)
    {
        var dbPath = Path.Combine(TempDir, "Config", "work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 建表
        var createTableSql = """
CREATE TABLE IF NOT EXISTS WorkOrders (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderNo TEXT NOT NULL,
    ProductCode TEXT NOT NULL,
    ProductName TEXT NOT NULL,
    DeviceId TEXT NOT NULL,
    DeviceName TEXT,
    TargetQuantity INTEGER NOT NULL,
    PlannedStart TEXT NOT NULL,
    PlannedEnd TEXT NOT NULL,
    Status INTEGER NOT NULL,
    CompletedOkCount INTEGER,
    CompletedNgCount INTEGER,
    Remark TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
)
""";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = createTableSql;
            cmd.ExecuteNonQuery();
        }

        // 建索引
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_WorkOrders_DeviceId_Status ON WorkOrders(DeviceId, Status)";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_WorkOrders_Status ON WorkOrders(Status)";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_WorkOrders_CreatedAt ON WorkOrders(CreatedAt)";
            cmd.ExecuteNonQuery();
        }

        // 插入工单
        var now = DateTime.Now;
        var runningStart = now.AddHours(-1).ToString("o");
        var runningEnd = now.AddHours(8).ToString("o");
        var pendingStart = now.AddHours(2).ToString("o");
        var pendingEnd = now.AddHours(10).ToString("o");
        var finishedStart = now.AddHours(-10).ToString("o");
        var finishedEnd = now.AddHours(-2).ToString("o");
        var nowStr = now.ToString("o");

        // 工单数据：OrderNo, ProductCode, ProductName, DeviceId, DeviceName, TargetQty, PlannedStart, PlannedEnd, Status, CompletedOk, CompletedNg
        var orders = new List<(string OrderNo, string Code, string Name, string DevId, string DevName, int Qty, string Start, string End, int Status, int? Ok, int? Ng)>
        {
            // device-001/注塑机1
            ("WO-TEST-001-R", "P-A", "注塑件A", "device-001", "注塑机1", firstTargetQty, runningStart, runningEnd, 1, null, null),
            ("WO-TEST-001-P", "P-A", "注塑件A", "device-001", "注塑机1", 800, pendingStart, pendingEnd, 0, null, null),
            ("WO-TEST-001-C", "P-A", "注塑件A", "device-001", "注塑机1", 1000, finishedStart, finishedEnd, 2, 950, 50),
        };
        if (!singleRunning)
        {
            orders.AddRange(new (string, string, string, string, string, int, string, string, int, int?, int?)[]
            {
                // device-002/注塑机2
                ("WO-TEST-002-R", "P-B", "注塑件B", "device-002", "注塑机2", 1200, runningStart, runningEnd, 1, null, null),
                ("WO-TEST-002-P", "P-B", "注塑件B", "device-002", "注塑机2", 900, pendingStart, pendingEnd, 0, null, null),
                ("WO-TEST-002-A", "P-B", "注塑件B", "device-002", "注塑机2", 1200, finishedStart, finishedEnd, 3, 400, 100),
                // device-003/组装机1
                ("WO-TEST-003-R", "P-C", "组装件C", "device-003", "组装机1", 500, runningStart, runningEnd, 1, null, null),
                ("WO-TEST-003-P", "P-C", "组装件C", "device-003", "组装机1", 400, pendingStart, pendingEnd, 0, null, null),
                ("WO-TEST-003-C", "P-C", "组装件C", "device-003", "组装机1", 500, finishedStart, finishedEnd, 2, 480, 20),
            });
        }

        using var tx = conn.BeginTransaction();
        foreach (var o in orders)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName, TargetQuantity, PlannedStart, PlannedEnd, Status, CompletedOkCount, CompletedNgCount, Remark, CreatedAt, UpdatedAt)
VALUES (@orderNo, @productCode, @productName, @deviceId, @deviceName, @targetQty, @plannedStart, @plannedEnd, @status, @completedOk, @completedNg, @remark, @createdAt, @updatedAt)
""";
            cmd.Parameters.AddWithValue("@orderNo", o.OrderNo);
            cmd.Parameters.AddWithValue("@productCode", o.Code);
            cmd.Parameters.AddWithValue("@productName", o.Name);
            cmd.Parameters.AddWithValue("@deviceId", o.DevId);
            cmd.Parameters.AddWithValue("@deviceName", o.DevName);
            cmd.Parameters.AddWithValue("@targetQty", o.Qty);
            cmd.Parameters.AddWithValue("@plannedStart", o.Start);
            cmd.Parameters.AddWithValue("@plannedEnd", o.End);
            cmd.Parameters.AddWithValue("@status", o.Status);
            cmd.Parameters.AddWithValue("@completedOk", (object?)o.Ok ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completedNg", (object?)o.Ng ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remark", DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", nowStr);
            cmd.Parameters.AddWithValue("@updatedAt", nowStr);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>启动 PlcSimulator 进程（dotnet run --no-build，假设已构建）</summary>
    public void StartSimulator(string scenario, int speed)
    {
        var slnRoot = LocateSlnRoot();
        var projPath = Path.Combine(slnRoot, "PlcSimulator", "PlcSimulator.csproj");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{projPath}\" -- --fresh --scenario {scenario} --speed {speed}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["KANBAN_DATA_DIR"] = TempDir;
        Simulator = Process.Start(psi);

        // 异步读取输出到 _simOutput（stdout + stderr 都读，避免缓冲区满导致死锁）
        Simulator!.OutputDataReceived += (s, e) => { if (e.Data != null) lock (_simOutput) _simOutput.AppendLine(e.Data); };
        Simulator.BeginOutputReadLine();
        Simulator.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_simOutput) _simOutput.AppendLine(e.Data); };
        Simulator.BeginErrorReadLine();

        // 等待 PlcSimulator 启动并监听端口（dotnet run --no-build 首次启动需 5-10s）
        // 轮询端口 4999，最多等待 20s
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectResult = tcp.BeginConnect("127.0.0.1", 4999, null, null);
                if (connectResult.AsyncWaitHandle.WaitOne(500) && tcp.Connected)
                {
                    tcp.EndConnect(connectResult);
                    Console.WriteLine($"  [PlcSimulator] 端口 4999 已监听");
                    return;
                }
            }
            catch { /* 端口尚未监听，继续等待 */ }
        }
        Console.WriteLine($"  [PlcSimulator] 端口 4999 等待超时（20s），PlcSimulator 输出:\n{GetSimulatorOutput()}");
    }

    /// <summary>启动 MainAPP（通过 KanbanAppFixture，复用 TempDir 作为数据目录）</summary>
    public void StartApp()
    {
        App = new KanbanAppFixture(TempDir);
    }

    /// <summary>读取 PlcSimulator 输出</summary>
    public string GetSimulatorOutput()
    {
        lock (_simOutput) return _simOutput.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. 停 MainAPP（App.Dispose 内部会 Kill 进程并等待退出，释放 Global\Kanban_SingleInstance Mutex）
        try { App?.Dispose(); } catch { /* ignore */ }

        // 2. 停 PlcSimulator（Kill 整个进程树，避免 dotnet 子进程残留）
        try
        {
            if (Simulator != null && !Simulator.HasExited)
            {
                Simulator.Kill(entireProcessTree: true);
                Simulator.WaitForExit(5000);
            }
            Simulator?.Dispose();
        }
        catch { /* ignore */ }

        // 3. 保留临时目录（含 .db 数据库文件），便于数据积累分析与调试失败测试。
        //    不删除任何模式下的临时目录。
        Console.WriteLine($"  [SimulationContext] 保留临时目录: {TempDir}");
    }
}

/// <summary>
/// 仿真流程测试：启动 PlcSimulator + MainAPP，通过 UI 自动化验证完整业务流程。
/// 每个测试方法用独立 SimulationContext 隔离进程与临时数据目录。
/// LongRunning 标记的测试需要较长时间（等待断线重连/班次切换），应单独运行。
/// </summary>
[Trait("Category", "Simulation")]
[Collection("UIA")]
public class SimulationFlowTests
{
    /// <summary>导航到指定页面（通过主导航 ListBox 按名称匹配并选中）</summary>
    private static void NavigateToPage(FlaUI.Core.AutomationElements.Window window, UIA3Automation automation, string pageName)
    {
        var cf = automation.ConditionFactory;
        var navList = window.FindFirstDescendant(cf.ByName("主导航"))?.AsListBox();
        Assert.NotNull(navList);
        foreach (var item in navList!.Items)
        {
            if (item.Name.Contains(pageName) || (item.Text ?? string.Empty).Contains(pageName))
            {
                item.Select();
                Thread.Sleep(1500); // 等待页面渲染（PageTransition 0.2s + 数据绑定 + 缓冲）
                return;
            }
        }
        Assert.Fail($"未找到导航项: {pageName}");
    }

    /// <summary>查找工单列表 ListBox（排除主导航，优先找包含"WO-"或"工单"文本的列表）</summary>
    private static ListBox? FindWorkOrderListBox(FlaUI.Core.AutomationElements.Window window, UIA3Automation automation)
    {
        var cf = automation.ConditionFactory;
        var allLists = window.FindAllDescendants(cf.ByControlType(ControlType.List));
        // 优先找包含工单号文本（WO-TEST）的列表
        foreach (var element in allLists)
        {
            var lb = element.AsListBox();
            if ((lb.Name ?? string.Empty) == "主导航" || lb.Items.Length == 0) continue;
            foreach (var item in lb.Items)
            {
                if (ContainsTextRecursive(item, "WO-"))
                    return lb;
            }
        }
        // 回退：取第一个有数据的非导航列表
        foreach (var element in allLists)
        {
            var lb = element.AsListBox();
            if ((lb.Name ?? string.Empty) != "主导航" && lb.Items.Length > 0)
                return lb;
        }
        return null;
    }

    /// <summary>选中第一个状态匹配的工单（点击 ListBoxItem 触发 SelectedItem 绑定）</summary>
    private static bool SelectFirstWorkOrderByStatus(ListBox list, string statusText)
    {
        foreach (var item in list.Items)
        {
            if (ContainsTextRecursive(item, statusText))
            {
                // 诊断：输出选中项的文本
                Console.WriteLine($"  [诊断] 选中 ListBoxItem: Name='{item.Name}' Text='{item.Text}' Items={list.Items.Length}");
                // 确认是工单列表项（包含 WO- 文本）
                if (!ContainsTextRecursive(item, "WO-"))
                {
                    Console.WriteLine("  [诊断] 警告：选中项不包含 WO- 文本，可能不是工单项");
                }
                // 先用 Select() 设置 IsSelected=true（触发 SelectedItem TwoWay 绑定），
                // 再用 Click() 模拟鼠标点击（触发焦点切换 + PreviewMouseLeftButtonDown 事件），
                // 两者结合确保 WPF ListBox 正确更新 SelectedWorkOrder
                var lbi = item.AsListBoxItem();
                try { lbi.Select(); } catch { }
                Thread.Sleep(300);
                item.Click();
                Thread.Sleep(1500); // 等待 SelectedWorkOrder 绑定更新 + 详情面板渲染
                return true;
            }
        }
        return false;
    }

    /// <summary>递归遍历元素树查找包含指定文本的后代</summary>
    private static bool ContainsTextRecursive(FlaUI.Core.AutomationElements.AutomationElement element, string text)
    {
        var name = element.Name ?? string.Empty;
        if (name.Contains(text)) return true;
        foreach (var child in element.FindAllChildren())
        {
            if (ContainsTextRecursive(child, text)) return true;
        }
        return false;
    }

    /// <summary>查询工单状态（0=Pending, 1=Running, 2=Completed, 3=Aborted）</summary>
    private static int QueryWorkOrderStatus(string tempDir, string orderNo)
    {
        var dbPath = Path.Combine(tempDir, "Config", "work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Status FROM WorkOrders WHERE OrderNo = @orderNo";
        cmd.Parameters.AddWithValue("@orderNo", orderNo);
        var result = cmd.ExecuteScalar();
        return result == null ? -1 : Convert.ToInt32(result);
    }

    /// <summary>
    /// 点击按钮并处理可能弹出的 HandyControl MessageBox 确认对话框。
    /// WorkOrderService.AbortWorkOrder 会弹"确认中止"YesNo 框，需点击"是"才能继续。
    /// 方案：主线程确保窗口前台后调用 button.Invoke()（会阻塞至模态对话框关闭），
    /// 后台线程用 Win32 PostMessage 发送回车键（HC MessageBox 默认聚焦"是"按钮）。
    /// 用 PostMessage 而非 FlaUI.Keyboard：避免 UIA 在模态对话框期间阻塞。
    /// </summary>
    private static void ClickButtonAndConfirmDialog(
        FlaUI.Core.AutomationElements.Window mainWindow,
        UIA3Automation automation,
        Button button,
        string dialogTitleHint,
        int dialogTimeoutMs = 10000)
    {
        // 先确保主窗口在前台（否则按钮点击可能不触发）
        try { mainWindow.Focus(); Thread.Sleep(500); } catch { }

        // 后台线程：延迟后轮询查找对话框，找到后用 PostMessage 发送回车键
        var confirmed = false;
        var confirmThread = new Thread(() =>
        {
            // 延迟 800ms 等待对话框出现
            Thread.Sleep(800);

            var deadline = DateTime.UtcNow.AddMilliseconds(dialogTimeoutMs);
            while (DateTime.UtcNow < deadline && !confirmed)
            {
                try
                {
                    var dialogHwnd = FindDialogWindow(dialogTitleHint);
                    if (dialogHwnd != IntPtr.Zero)
                    {
                        Console.WriteLine($"  [诊断] 找到对话框 HWND={dialogHwnd}，发送回车键确认");
                        // 用 keybd_event 发送全局回车键（由系统路由到前台窗口）
                        // 比 PostMessage 更可靠：WPF 窗口通过 InputManager 处理全局键盘事件
                        keybd_event(VK_RETURN, 0, 0, IntPtr.Zero);     // 按下
                        Thread.Sleep(50);
                        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);  // 释放
                        confirmed = true;
                    }
                    else
                    {
                        // 兜底：对话框标题可能为空，检查前台窗口是否是新的 Window
                        var fg = GetForegroundWindow();
                        if (fg != IntPtr.Zero)
                        {
                            // 获取前台窗口标题
                            var fgSb = new System.Text.StringBuilder(256);
                            GetWindowText(fg, fgSb, 256);
                            var fgTitle = fgSb.ToString();
                            // 前台窗口不是主窗口（标题不匹配"看板系统"）时发送回车键
                            if (!fgTitle.Contains("看板系统"))
                            {
                                Console.WriteLine($"  [诊断] 前台窗口变更 HWND={fg} Title='{fgTitle}'，发送回车键");
                                // 用 keybd_event 发送全局回车键（与主路径一致，比 PostMessage 更可靠）
                                keybd_event(VK_RETURN, 0, 0, IntPtr.Zero);
                                Thread.Sleep(50);
                                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
                                confirmed = true;
                            }
                        }
                    }
                }
                catch { }
                if (!confirmed) Thread.Sleep(200);
            }
        });
        confirmThread.IsBackground = true;
        confirmThread.Start();

        // 短暂延迟确保后台线程已启动
        Thread.Sleep(200);

        // 主线程调用 button.Invoke()：触发 Command → AbortWorkOrder → HC MessageBox 模态阻塞
        try
        {
            button.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [诊断] button.Invoke() 异常: {ex.Message}");
            try { button.Click(); } catch { }
        }

        // 等待后台线程完成（对话框关闭后 Invoke 返回，后台线程也已确认）
        confirmThread.Join(dialogTimeoutMs + 3000);
        if (!confirmed)
        {
            Console.WriteLine($"  [诊断] 未找到对话框「{dialogTitleHint}」或未成功确认");
            if (ContainsTextRecursive(mainWindow, "无法中止"))
                Console.WriteLine("  [诊断] 主窗口检测到「无法中止」文本，状态校验可能失败");
        }
    }

    /// <summary>用 Win32 EnumWindows 查找标题包含 hint 的顶级窗口</summary>
    private static IntPtr FindDialogWindow(string titleHint)
    {
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            if (sb.ToString().Contains(titleHint))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 轮询查找主窗口中包含指定文本的元素（用于检测 HandyControl Growl 通知）。
    /// Growl 通知出现在 GrowlContainer StackPanel 中，包含 TextBlock 显示消息。
    /// 通知有自动消失时间（默认 3s），所以需要高频轮询。
    /// </summary>
    private static bool WaitForGrowlText(
        FlaUI.Core.Application app,
        UIA3Automation automation,
        string textFragment,
        int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            // Growl 通知可能在独立窗口或主窗口的 GrowlContainer 中
            // 先检查主窗口
            try
            {
                var mainWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
                if (mainWindow != null && ContainsTextRecursive(mainWindow, textFragment))
                    return true;
            }
            catch { /* 主窗口获取失败，继续轮询 */ }

            // 也检查所有顶级窗口（Growl 可能创建独立浮层）
            var allWindows = automation.GetDesktop().FindAllChildren();
            foreach (var win in allWindows)
            {
                if (win.ControlType != ControlType.Window) continue;
                if (ContainsTextRecursive(win, textFragment))
                    return true;
            }

            Thread.Sleep(500);
        }
        return false;
    }

    [Fact]
    public void Simulation_WorkOrderComplete()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        ctx.PrepareSettingsJson();
        // singleRunning=true：仅 1 条 Running 工单，确保选中即 WO-TEST-001-R
        ctx.PrepareWorkOrdersDb(singleRunning: true);
        ctx.StartSimulator("counteralarm", 20);
        ctx.StartApp();

        // 等待 MainAPP 连接 PLC 并加载数据
        Thread.Sleep(5000);

        var automation = ctx.App!.Automation;
        var window = ctx.App.MainWindow;

        // 导航到工单管理页
        NavigateToPage(window, automation, "工单管理");

        // 选中"进行中"工单（仅 1 条，确保选中 WO-TEST-001-R）
        var woList = FindWorkOrderListBox(window, automation);
        Assert.NotNull(woList);
        Assert.True(SelectFirstWorkOrderByStatus(woList!, "进行中"), "未找到进行中的工单");
        Thread.Sleep(2000); // 等待 SelectedWorkOrder 绑定更新 + CanComplete 重新评估

        // 查找"完成"按钮（Content="完成"）
        var cf = automation.ConditionFactory;
        Button? completeBtn = null;
        // 重试选中 + 查找按钮（最多 3 次）：首次 Click 可能未触发 SelectedItem 绑定
        for (var attempt = 0; attempt < 3; attempt++)
        {
            completeBtn = window.FindFirstDescendant(cf.ByControlType(ControlType.Button).And(cf.ByName("完成")))?.AsButton();
            if (completeBtn == null)
            {
                // 退化查找：遍历所有按钮找文本匹配
                var allBtns = window.FindAllDescendants(cf.ByControlType(ControlType.Button));
                foreach (var b in allBtns)
                {
                    if ((b.Name ?? string.Empty).Contains("完成") || (b.HelpText ?? string.Empty).Contains("完成"))
                    {
                        completeBtn = b.AsButton();
                        break;
                    }
                }
            }
            if (completeBtn == null) break;
            if (completeBtn.IsEnabled) break;

            // 按钮未启用：诊断输出 + 重试选中
            Console.WriteLine($"  [诊断] 尝试 {attempt + 1}: 完成按钮 IsEnabled=false, 重新选中工单...");
            // 诊断：输出所有按钮的 Name 和 IsEnabled
            if (attempt == 0)
            {
                var diagBtns = window.FindAllDescendants(cf.ByControlType(ControlType.Button));
                foreach (var b in diagBtns)
                {
                    var name = b.Name ?? "";
                    if (name.Contains("完成") || name.Contains("中止") || name.Contains("开始"))
                        Console.WriteLine($"  [诊断] 按钮: Name='{name}' IsEnabled={b.IsEnabled}");
                }
            }
            // 重新选中
            SelectFirstWorkOrderByStatus(woList!, "进行中");
            Thread.Sleep(2000);
        }
        Assert.NotNull(completeBtn);
        Assert.True(completeBtn!.IsEnabled, "完成按钮未启用（SelectedWorkOrder 可能非 Running 状态）");

        // 点击完成按钮（无确认对话框，直接执行）
        completeBtn.Invoke();
        Thread.Sleep(2000); // 等待命令执行 + 数据库写入

        // 断言：工单状态变为已完成（Status=2）
        var status = QueryWorkOrderStatus(ctx.TempDir, "WO-TEST-001-R");
        Assert.Equal(2, status);
    }

    [Fact]
    public void Simulation_WorkOrderAbort()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        ctx.PrepareSettingsJson();
        // singleRunning=true：仅 1 条 Running 工单，确保选中即 WO-TEST-001-R
        ctx.PrepareWorkOrdersDb(singleRunning: true);
        ctx.StartSimulator("counteralarm", 20);
        ctx.StartApp();

        Thread.Sleep(5000);

        var automation = ctx.App!.Automation;
        var window = ctx.App.MainWindow;

        NavigateToPage(window, automation, "工单管理");

        var woList = FindWorkOrderListBox(window, automation);
        Assert.NotNull(woList);
        Assert.True(SelectFirstWorkOrderByStatus(woList!, "进行中"), "未找到进行中的工单");
        Thread.Sleep(2000); // 等待 SelectedWorkOrder 绑定更新 + CanAbort 重新评估

        // 查找"中止"按钮
        var cf = automation.ConditionFactory;
        Button? abortBtn = null;
        // 重试选中 + 查找按钮（最多 3 次）
        for (var attempt = 0; attempt < 3; attempt++)
        {
            abortBtn = window.FindFirstDescendant(cf.ByControlType(ControlType.Button).And(cf.ByName("中止")))?.AsButton();
            if (abortBtn == null)
            {
                var allBtns = window.FindAllDescendants(cf.ByControlType(ControlType.Button));
                foreach (var b in allBtns)
                {
                    if ((b.Name ?? string.Empty).Contains("中止") || (b.HelpText ?? string.Empty).Contains("中止"))
                    {
                        abortBtn = b.AsButton();
                        break;
                    }
                }
            }
            if (abortBtn == null) break;
            if (abortBtn.IsEnabled) break;

            Console.WriteLine($"  [诊断] 尝试 {attempt + 1}: 中止按钮 IsEnabled=false, 重新选中工单...");
            SelectFirstWorkOrderByStatus(woList!, "进行中");
            Thread.Sleep(2000);
        }
        Assert.NotNull(abortBtn);
        Assert.True(abortBtn!.IsEnabled, "中止按钮未启用（SelectedWorkOrder 可能非 Running/Pending 状态）");

        // 点击中止按钮 + 处理"确认中止"对话框（点击"是"）
        ClickButtonAndConfirmDialog(window, automation, abortBtn, "确认中止");
        Thread.Sleep(2000); // 等待命令执行 + 数据库写入

        // 断言：工单状态变为已中止（Status=3）
        var status = QueryWorkOrderStatus(ctx.TempDir, "WO-TEST-001-R");
        Assert.Equal(3, status);
    }

    /// <summary>
    /// 产量达标自动提示测试：
    /// 设置工单目标产量=5 件，启动仿真器高速产出，验证 MainAPP 在产量达到目标时弹出 Growl 成功通知。
    /// HomeViewModel.CheckWorkOrderCompletionTarget 在 SyncRuntime 中每秒检查一次，
    /// 当 TotalOkProduction + TotalNgProduction >= TargetQuantity 时弹 Growl + 写 INF 日志。
    /// 断言优先检查日志文件（Serilog 落盘可靠），Growl UI 检测作为辅助。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void Simulation_ProductionTargetReached()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        ctx.PrepareSettingsJson();
        // 仅 1 条 Running 工单，目标产量=5 件（device-001 注塑机1，首页默认设备）
        ctx.PrepareWorkOrdersDb(singleRunning: true, firstTargetQty: 5);
        ctx.StartSimulator("normal", 20);
        ctx.StartApp();

        // 等待 MainAPP 连接 PLC 并加载数据
        Thread.Sleep(5000);

        var automation = ctx.App!.Automation;
        // 首页默认显示 device-001，CurrentWorkOrder 应为 WO-TEST-001-R
        // 仿真器 speed=20，device-001 节拍~72s/件，加速后约 3.6s/件（预热期 4.32s/件）
        // BatchUpdateSize=3，每 3 件写一次 PLC。6 件（2 批次）约 26s 达到目标 5 件
        // SyncRuntime 每秒执行一次，加上日志写入延迟，60s 超时足够覆盖预热 + 批量更新
        var uiFound = WaitForGrowlText(ctx.App.App, automation, "产量已达标", timeoutMs: 60000);

        // 日志文件兜底：CheckWorkOrderCompletionTarget 达标时写 INF 日志，比 Growl UIA 检测更可靠。
        // 用 FileShare.ReadWrite 打开，避免 Serilog 持有写锁导致 IOException。
        var logPath = Path.Combine(ctx.TempDir, "Config", "Logs", $"kanban_{DateTime.Now:yyyyMMdd}.log");
        var logFound = false;
        if (File.Exists(logPath))
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var logContent = reader.ReadToEnd();
                logFound = logContent.Contains("产量已达标");
            }
            catch { /* 读取失败，忽略 */ }
        }

        // 断言：Growl UI 或日志文件任一检测到"产量已达标"即通过
        Assert.True(uiFound || logFound,
            $"未检测到产量达标通知（UI Growl: {uiFound}, 日志: {logFound}）。" +
            $"日志路径: {logPath}, 存在: {File.Exists(logPath)}。" +
            $"PlcSimulator 输出:\n{ctx.GetSimulatorOutput()}");
    }

    [Fact]
    public void Simulation_ProductionReset()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        ctx.PrepareSettingsJson();
        ctx.PrepareWorkOrdersDb();
        ctx.StartSimulator("normal", 20);
        ctx.StartApp();

        // 等待设备产出（产量 > 0）
        Thread.Sleep(10000);

        var automation = ctx.App!.Automation;
        var window = ctx.App.MainWindow;

        // 导航到设备管理页
        NavigateToPage(window, automation, "设备管理");

        var cf = automation.ConditionFactory;

        // 选中第一台设备：用 ControlType.List 精确匹配 ListBox（避免匹配到 TextBlock）
        var deviceList = window.FindFirstDescendant(
            cf.ByControlType(ControlType.List).And(cf.ByName("设备列表")))?.AsListBox();
        // 回退：遍历所有 List 找包含设备名的
        if (deviceList == null || deviceList.Items.Length == 0)
        {
            var allLists = window.FindAllDescendants(cf.ByControlType(ControlType.List));
            foreach (var element in allLists)
            {
                var lb = element.AsListBox();
                if (lb.Items.Length == 0) continue;
                // 设备列表项包含"注塑机"或"组装机"文本
                if (ContainsTextRecursive(lb, "注塑机") || ContainsTextRecursive(lb, "组装机"))
                {
                    deviceList = lb;
                    break;
                }
            }
        }
        Assert.NotNull(deviceList);
        Assert.True(deviceList!.Items.Length > 0, "设备列表为空");
        deviceList!.Items[0].Select();
        Thread.Sleep(2000); // 等待设备详情渲染

        // 确保选中"设备参数"Tab（TabItem AutomationProperties.Name="设备参数"）
        var paramTab = window.FindFirstDescendant(cf.ByName("设备参数"))?.AsTabItem();
        if (paramTab != null)
        {
            try { paramTab.Select(); } catch { }
            Thread.Sleep(1000);
        }

        // 找到"手动触发 OEE 清零"按钮
        var resetBtn = window.FindFirstDescendant(cf.ByName("手动触发 OEE 清零"))?.AsButton();
        if (resetBtn == null)
        {
            // 回退：遍历所有按钮找文本匹配
            var allBtns = window.FindAllDescendants(cf.ByControlType(ControlType.Button));
            foreach (var b in allBtns)
            {
                if ((b.Name ?? string.Empty).Contains("清零") || (b.HelpText ?? string.Empty).Contains("清零"))
                {
                    resetBtn = b.AsButton();
                    break;
                }
            }
        }
        Assert.NotNull(resetBtn);
        Assert.True(resetBtn!.IsEnabled, "清零按钮未启用");

        // 点击清零按钮 + 处理"确认 OEE 清零"对话框
        ClickButtonAndConfirmDialog(window, automation, resetBtn, "确认 OEE 清零");

        // 等待 PlcSimulator 清零监听循环检测到触发位（每 500ms 轮询一次）
        Thread.Sleep(3000);

        // 断言：PlcSimulator 输出包含"清零"字样
        var output = ctx.GetSimulatorOutput();
        Assert.Contains("清零", output);
    }

    [Fact]
    [Trait("Category", "LongRunning")]
    public void Simulation_PlcDisconnectReconnect()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        ctx.PrepareSettingsJson();
        ctx.PrepareWorkOrdersDb();
        ctx.StartSimulator("disconnect", 10);
        ctx.StartApp();

        // 等待 MainAPP 连接成功
        Thread.Sleep(10000);

        // 轮询 PlcSimulator 输出，等待断线仿真与恢复监听字样出现。
        // disconnect 场景配置：首次延迟 30s 断线，持续 15s 后恢复。
        // PlcSimulator 启动需要 ~5s，加上 10s MainAPP 连接等待 + 30s 延迟 + 15s 断线 = ~60s 出现恢复消息。
        // 用 90s 超时轮询，比固定 Sleep 更可靠（避免时序边界问题）。
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var foundDisconnect = false;
        var foundReconnect = false;
        while (DateTime.UtcNow < deadline)
        {
            var output = ctx.GetSimulatorOutput();
            if (!foundDisconnect && output.Contains("断线仿真"))
            {
                foundDisconnect = true;
                Console.WriteLine($"  [诊断] 检测到断线仿真，等待恢复...");
            }
            if (!foundReconnect && output.Contains("恢复 TCP 监听"))
            {
                foundReconnect = true;
                Console.WriteLine($"  [诊断] 检测到恢复 TCP 监听");
                break;
            }
            Thread.Sleep(1000);
        }

        // 断言：PlcSimulator 输出包含断线仿真与恢复监听字样
        var finalOutput = ctx.GetSimulatorOutput();
        Assert.True(foundDisconnect, $"未检测到断线仿真。PlcSimulator 输出:\n{finalOutput}");
        Assert.True(foundReconnect, $"未检测到恢复 TCP 监听。PlcSimulator 输出:\n{finalOutput}");
    }

    [Fact]
    [Trait("Category", "LongRunning")]
    public void Simulation_ShiftChange()
    {
        using var ctx = new SimulationContext();
        ctx.PrepareDevicesJson();
        // 短周期班次：2 分钟切换一次
        ctx.PrepareSettingsJson(shiftMinutes: 2);
        ctx.PrepareWorkOrdersDb();
        ctx.StartSimulator("normal", 10);
        ctx.StartApp();

        // 等待班次切换发生（A班 2 分钟 → B班）
        Thread.Sleep(130000);

        // 断言：MainAPP 日志包含"班次切换"字样
        // 日志路径：Config/Logs/kanban_YYYYMMDD.log（按天滚动）
        // 用 FileShare.ReadWrite 打开，避免 Serilog 持有写锁导致 IOException
        var logPath = Path.Combine(ctx.TempDir, "Config", "Logs", $"kanban_{DateTime.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(logPath), $"日志文件不存在: {logPath}");
        string logContent;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            logContent = reader.ReadToEnd();
        Assert.Contains("班次切换", logContent);
    }
}
