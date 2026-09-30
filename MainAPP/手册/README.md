# Kanban 操作手册 / User Manuals

本目录包含 Kanban 面向**产线操作员、班组长和现场管理员**的中英文用户使用手册。截图位于仓库根目录 [`screenshots/`](../../screenshots/)，构建时会复制到程序输出目录。

**程序内访问：**

1. 左侧导航栏**最底部**点击 **「使用手册 (F1)」**
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

### 截图标注规则 / Annotation Rules

截图测试内置标注层（`ScreenshotCaptureTests.DrawAnnotations`），以**窗口尺寸比例坐标**在图上绘制半透明高亮框 + 文字标签，便于手册读者定位关键区域：

- 位置坐标填在 `PageAnnotations` 字典（文件名 → 标注列表），坐标为 0~1 比例，随分辨率缩放。
- 配色约定：框与标签=品牌蓝 `#3B82F6`，文字白色，标签底色深蓝 `#111827`。
- 新增标注区域前，先截一张未标注图人工核对区域坐标，再填入字典并重截。
- 当前示例：`01_home.png` 标注了「使用手册 (F1)」与「切换用户 (Ctrl+L)」入口坐标（基于 1920×1080 布局，换分辨率时按比例自动缩放，但建议实机校准）。

> **注意（2026-09）：** 侧边栏底部已移除授权状态卡；新增 `Ctrl+L` 切换用户快捷键。所有含侧边栏底部的截图（01～13）均需在最新界面下重新生成，手册 2.3/3.1/3.2 与截图索引中的相关描述已同步更新。

## 手册维护检查单 / Maintenance Checklist

每次程序 UI/功能变更后，按此清单检查并同步手册，避免手册与界面脱节：

1. **导航与入口变化**：侧边栏、顶部栏增减入口 → 同步手册 2.3 / 3.1 及截图索引。
2. **快捷键变化**：新增或移除 `Ctrl+*` / F 键 → 同步 2.3“其他常用快捷键”和**附录 C 快捷键速查表**。
3. **页面布局变化**：卡片、按钮、状态提示变化 → 同步对应章节描述。
4. **状态/横幅语义变化**：连接横幅、授权卡片等 → 同步 3.2 及快速诊断表。
5. **配置项变化**：设置新增/改名 → 同步 12 章及附录 B。
6. **流程变化**：登录、开机、关机、工单等步骤变化 → 同步 2 / 4 / 11 / 17 / 20 章。
7. **截图**：涉及任何可见界面变化的修改 → 按上方步骤重新生成截图并重拍中文+英文两版，必要时在图上标注关键区域。
8. **版本与日期**：修改完成并改版后，更新两版手册开头的“最后更新”及“适用程序”头注。

> 原则：**改界面必改手册**；拿不准时宁可在手册里写“以程序内实际显示为准”。
