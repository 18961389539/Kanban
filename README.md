# Kanban 工业看板

基于 .NET 10 和 WPF 的工业生产看板系统，面向产量、设备状态、报警、缺陷、工单和 OEE 数据展示与管理。

## 项目结构

| 项目 | 说明 |
| --- | --- |
| `MainAPP` | WPF 主应用、设备采集、历史数据、看板页面和设置管理 |
| `MainAPP.Tests` | 单元测试和集成测试 |
| `MainAPP.E2E` | 使用临时目录和 SQLite 的端到端流程测试 |
| `MainAPP.UIAutomation` | WPF UI 自动化测试 |
| `MainAPP.Benchmarks` | 性能基准测试 |
| `PlcSimulator` | PLC 通信模拟器 |
| `LicenseManager.App` | 授权验证和激活逻辑 |
| `LicenseIssuer.CLI` | 激活码签发、验证和撤销命令行工具 |
| `LicenseIssuer.Wpf` | 激活码签发桌面工具 |

解决方案入口为 `Kanban.slnx`。

## 环境要求

- Windows
- .NET SDK `10.0.100` 或兼容的最新次版本
- Visual Studio 2022 或 VS Code
- 可选：用于真实 PLC 通信的 `HslCommunication.dll`

项目目标框架为 `net10.0-windows`，需要在 Windows 环境下构建和运行 WPF 项目。

## 快速开始

还原依赖并构建主应用：

```powershell
dotnet restore Kanban.slnx
dotnet build MainAPP\MainAPP.csproj
```

启动主应用：

```powershell
dotnet run --project MainAPP\MainAPP.csproj
```

首次运行时，应用会在用户配置目录创建设置、设备和基线数据文件。测试使用独立临时目录，不会修改真实配置。

## 测试

运行主单元测试：

```powershell
dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj --no-restore
```

运行 E2E 测试：

```powershell
dotnet test --project MainAPP.E2E\MainAPP.E2E.csproj --no-restore
```

运行 UI 自动化测试：

```powershell
dotnet test --project MainAPP.UIAutomation\MainAPP.UIAutomation.csproj --no-restore
```

WPF 测试会使用 STA 线程和共享 UI 资源。建议单独、串行运行 E2E、UI Automation 和其他 WPF 测试，避免多个测试宿主同时占用窗口、资源或构建输出文件。

项目中的辅助脚本：

- `ci/run-ui-automation.ps1`：运行 UI 自动化流程
- `screenshot_all.ps1`：批量生成页面截图

## 性能基准

运行基准测试项目：

```powershell
dotnet run --project MainAPP.Benchmarks\MainAPP.Benchmarks.csproj -c Release
```

## 架构概览

主应用采用 WPF + MVVM + Microsoft.Extensions.Hosting：

- ViewModel 通过 DI 获取服务和页面依赖。
- `MainAppServiceCollectionExtensions.AddMainAppCoreServices` 统一注册生产和测试共用的基础设施。
- `AddMainAppPresentationServices` 按应用、设备、历史、工单和导航模块注册表现层；页面通过 `NavigationPage` 集合声明，不在 `MainWindow` 中维护具体页面清单。
- 历史数据按领域提供窄接口：`IProductionHistoryService`、`IAlarmHistoryService` 和 `IStatusTransitionHistoryService`。
- `IPlcDriver` 隔离具体 PLC 库；`IDeviceAdapter` 进一步隔离设备协议和采集业务。
- 共享 PLC 支持三菱 MC、西门子 S7 和 Modbus TCP；品牌配置位于 `PlcConfig`，所有设备共享一个活动 PLC 连接。
- 地址格式按品牌解析：三菱使用 `D/M`，西门子使用 `DBx.DBDn` / `DBx.DBXn.b`，Modbus 使用 `HRn` / `Cn`。
- `Device.Capabilities` 根据设备配置派生兼容能力，不写入 `devices.json`；运行时 PLC 品牌以共享 `PlcConfig.Brand` 为准，所有设备复用同一个活动驱动。
- 生产、报警、状态转换和工单数据使用独立的 SQLite 数据上下文。
- Serilog 负责文件日志，运行监控页面提供采集周期、资源和历史存储诊断。
- `settings.json` 带有 `SchemaVersion`，旧版本配置通过迁移器升级，未来不兼容版本会在启动时明确报告。

新增 PLC 品牌时，优先实现对应的共享驱动和地址 codec，并通过 `IDeviceAdapter` 暴露唯一的 `Brand` 能力；所有设备继续共享一个活动连接。新增单域历史调用方时，优先依赖对应的窄接口，不要直接依赖完整的 `IHistoryService` 门面。

## 数据与配置

应用运行时会使用以下类型的数据文件：

- `settings.json`：应用设置
- `devices.json`：设备和地址配置
- `baselines.json`：产量基线
- SQLite 数据库：生产日志、报警事件、状态转换和工单数据
- Serilog 日志文件：运行日志和异常信息

配置文件损坏时，应用会尝试生成 `.corrupt` 备份，并保留错误信息供界面提示。生产环境请定期备份配置目录和历史数据库。

## PLC 与模拟器

真实 PLC 通信由 `HslPlcDriver` 实现，业务层依赖 `IPlcDriver` 接口。测试和开发环境可以注入 Fake 驱动或运行 `PlcSimulator`，避免直接连接生产 PLC。

使用真实 PLC 前，请确认：

1. PLC IP 地址和端口配置正确。
2. 设备地址符合当前地址解析规则。
3. 生产环境已部署匹配版本的 `HslCommunication.dll`。
4. 已验证读写权限和生产复位命令，避免误操作现场设备。

## 开发约定

- 修改后优先运行受影响项目的窄测试，再运行完整测试。
- WPF 测试和 UI 自动化测试串行执行。
- 不要在 ViewModel 中直接创建具体服务或 DbContext。
- 新增服务优先定义接口，并通过 DI 注册。
- 新增配置字段时同步考虑默认值、校验、持久化和旧配置兼容。
- 不要提交 `bin`、`obj`、测试结果、截图和本地运行日志。
