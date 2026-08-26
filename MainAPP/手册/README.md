# MainAPP 操作手册 / User Manuals

本目录包含 MainAPP 面向**产线操作员、班组长和现场管理员**的中英文用户使用手册。截图位于仓库根目录 [`screenshots/`](../../screenshots/)，构建时会复制到程序输出目录。

**程序内访问：**

1. 左侧导航栏**最下方**点击 **「使用手册 (F1)」**
2. 或按键盘 **F1**

手册以 Markdown 转 HTML 在应用内打开，随界面语言选择中/英版本。

| 语言 | 文档 |
| --- | --- |
| 简体中文 | [MainAPP用户使用手册.md](./MainAPP用户使用手册.md) |
| English | [MainAPP_User_Manual_EN.md](./MainAPP_User_Manual_EN.md) |

> 界面语言为 English 时按 F1 打开英文手册，其他语言（含日本語 / Português）打开中文手册（见手册附录 B.2）。

## 截图列表 / Screenshots

| 文件 | 页面（中文 / EN） |
| --- | --- |
| `01_home.png` | 主页 / Home |
| `02_production_line.png` | 产线总览 / Production Line |
| `03_alarm_center.png` | 报警中心 / Alarm Center |
| `04_device_manager.png` | 设备管理 / Device Manager |
| `05_history_query.png` | 历史查询 / History Query |
| `06_overview.png` | 生产复盘 / Review |
| `07_work_order.png` | 工单管理 / Work Orders |
| `08_settings.png` | 系统设置 / System Settings |
| `09_runtime_monitoring.png` | 运行监控 / Runtime Monitor |
| `10_data_monitoring.png` | 数据监控 / Data Monitoring |
| `11_recipe_manager.png` | 配方管理 / Recipes |
| `12_user_manager.png` | 用户管理 / User Management |
| `13_audit.png` | 审计日志 / Audit Log |

## 重新生成截图 / Regenerate Screenshots

1. 在项目根目录启动完整环境（PlcSimulator + Collector + MainAPP）：

   ```powershell
   $env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
   .\start_env.ps1
   ```

2. 等待 MainAPP 主窗口出现（默认管理员自动登录）。

3. 运行 UI 自动化截图测试：

   ```powershell
   dotnet test MainAPP.UIAutomation\MainAPP.UIAutomation.csproj `
     --filter "FullyQualifiedName~ScreenshotCaptureTests" `
     --logger "console;verbosity=detailed"
   ```

4. 输出目录：`screenshots/`（解决方案根目录）。重新构建 MainAPP 后截图会复制到输出目录。

> 全屏模式下侧边栏默认折叠；测试会自动展开导航后再逐页截图。
