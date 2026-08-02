# MainAPP

`MainAPP` 是 Kanban 工业看板的 WPF 主应用，负责设备采集、生产数据、报警、状态历史、工单、OEE 看板和运行监控。

## 技术栈

- .NET 10 / `net10.0-windows`
- WPF
- CommunityToolkit.Mvvm
- Microsoft.Extensions.Hosting 和 Dependency Injection
- Entity Framework Core + SQLite
- HslCommunication 三菱 MC PLC 通信
- HandyControl、Material.Icons.WPF、OxyPlot
- Serilog 文件日志

## 构建与启动

从仓库根目录执行：

```powershell
dotnet restore Kanban.slnx
dotnet build MainAPP\MainAPP.csproj
dotnet run --project MainAPP\MainAPP.csproj
```

主应用是 Windows GUI 程序，必须在 Windows 环境运行。项目通过 `MainAPP\DLLS\HslCommunication.dll` 引用 PLC 通信库，部署或构建前请确认该文件存在。

## 启动流程

应用启动大致按以下顺序执行：

1. 检查单实例和授权状态。
2. 启动 Host 和依赖注入容器。
3. 加载应用设置、字号缩放和生产基线。
4. 校验配置并加载设备列表。
5. 初始化生产、报警、状态转换和工单数据库。
6. 加载工单数据。
7. 创建并显示 `MainWindow`。
8. 启动 PLC 连接管理和数据采集循环。

启动阶段发生异常时会写入日志并显示错误提示，不建议在 ViewModel 中绕过启动协调器直接启动后台服务。

## 数据目录

默认数据根目录：

```text
%APPDATA%\Kanban
```

可以通过环境变量指定独立数据目录，适合开发、演示和多环境测试：

```powershell
$env:KANBAN_DATA_DIR = "D:\KanbanData\dev"
dotnet run --project MainAPP\MainAPP.csproj
```

配置文件位于数据根目录下的 `Config` 子目录：

| 文件 | 内容 |
| --- | --- |
| `settings.json` | PLC 连接、轮询间隔、班次、界面设置 |
| `devices.json` | 设备、PLC 地址、报警、缺陷和计数报警配置 |
| `baselines.json` | 设备产量基线和班次基线 |
| `*.bak` | 保存前的上一版本备份 |
| `*.corrupt` | 检测到 JSON 损坏时的原文件备份 |

历史数据使用独立 SQLite 数据库保存，具体数据库路径由 `DatabaseProvider` 根据数据目录管理。生产环境请备份整个 `%APPDATA%\Kanban` 目录，而不是只备份单个 JSON 文件。

## PLC 配置

真实 PLC 连接由以下组件协作完成：

- `IPlcDriver`：PLC 协议通信抽象
- `HslPlcDriver`：基于 HslCommunication 的默认实现
- `PlcConnectionManager`：连接、断开、重连和状态管理
- `PlcDataAcquisitionService`：轮询设备并更新运行时数据
- `IDeviceAdapter`：设备协议适配器边界

新增设备协议时，实现 `IDeviceAdapter` 并注册到 DI。不要在 `PlcDataAcquisitionService` 中增加协议判断分支。

使用真实 PLC 前请确认：

- PLC IP 地址和端口正确。
- 设备地址符合当前地址格式规则。
- OK、NG、状态、报警和复位地址与现场程序一致。
- 已在非生产环境验证写入命令和生产复位操作。

## 代码结构

```text
MainAPP/
├── Data/          SQLite Provider、仓储
├── Entities/      数据库实体
├── Models/        设备、运行时状态、配置和领域模型
├── Services/      采集、PLC、历史、配置、工单和监控服务
├── ViewModels/    MVVM 页面和业务状态
├── Views/         WPF 页面与控件
├── Styles/        全局画刷、主题、组件和页面资源
├── Controls/      自定义控件
├── Converters/    WPF 值转换器
└── Mapping/       AutoMapper 配置
```

核心设计约定：

- 生产和测试共用 `MainAppServiceCollectionExtensions.AddMainAppCoreServices` 的基础设施注册。
- 单域历史调用方优先依赖 `IProductionHistoryService`、`IAlarmHistoryService` 或 `IStatusTransitionHistoryService`。
- `Device.Capabilities` 根据设备配置派生，不写入 `devices.json`。
- ViewModel 通过接口和 DI 获取依赖，不直接创建具体服务或 DbContext。
- 设备列表和运行时集合由 `DeviceRepository` 统一管理，后台采集使用快照访问。

## 开发与调试

查看主应用诊断时，优先使用运行监控页面观察：

- PLC 连接状态
- 采集循环是否运行
- 最近采集成功设备数
- 采集周期和失败次数
- PLC 读取操作估算数量
- 进程、内存、磁盘和历史存储状态

主应用日志由 Serilog 写入数据目录下的日志文件。遇到启动、PLC 连接或数据落库问题时，应同时检查界面诊断和日志中的 `SourceContext`。

## 相关测试

从仓库根目录执行：

```powershell
dotnet test --project MainAPP.Tests\MainAPP.Tests.csproj --no-restore
dotnet test --project MainAPP.E2E\MainAPP.E2E.csproj --no-restore
dotnet test --project MainAPP.UIAutomation\MainAPP.UIAutomation.csproj --no-restore
```

WPF 测试需要 STA 线程和共享资源。E2E、UI Automation 以及其他 WPF 测试应串行执行，避免多个测试进程同时占用窗口、资源或构建输出。

测试宿主使用临时目录和临时 SQLite 数据库，不会修改真实的 `%APPDATA%\Kanban` 数据。

## 相关文档

- [仓库总览](../README.md)
- [主程序易用性建议](../docs/MainAPP易用性建议.md)
- [授权激活码设计方案](../docs/授权激活码设计方案.md)
