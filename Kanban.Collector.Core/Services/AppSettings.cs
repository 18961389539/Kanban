using Kanban.Localization;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanban.ComponentModel;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Models;
using Serilog;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据采集模式。
/// Local：MainAPP 进程内直接采集 PLC（旧单机模式，默认，行为不变）。
/// Remote：MainAPP 作为瘦客户端连接 Kanban.Collector 采集服务进程获取实时数据。
/// </summary>
public enum KanbanDataMode
{
    Local = 0,
    Remote = 1,
}

/// <summary>
/// 运行模式。
/// Full：完整模式（默认）——展示 + 管理（设备/工单/设置/复盘等全部页面可用）。
/// Viewer：展示模式（屏端大屏）——只保留展示页（主页/产线/报警中心/设备详情），
/// 管理入口隐藏、导航受限、退出需确认，防止车间工人误触管理功能或关掉看板。
/// </summary>
public enum KanbanRunMode
{
    Full = 0,
    Viewer = 1,
}

/// <summary>
/// 旧版界面语言枚举。新运行时使用 <see cref="AppSettings.LanguageCode"/>，
/// 保留此枚举用于兼容旧 settings.json 和旧调用方。
/// </summary>
public enum AppLanguage
{
    Zh = 0,
    En = 1,
    Ja = 2,
    PtBr = 3,
}

/// <summary>
/// 应用全局设置服务，统一管理配置和设备列表持久化
/// </summary>
public partial class AppSettings : ObservableObject
{
    public const int CurrentSchemaVersion = SettingsMigrationRunner.CurrentVersion;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string? _loadErrorMessage;

    /// <summary>
    /// 配置文件加载错误消息（供调用方通知用户）。
    /// settings.json 损坏时备份为 .corrupt 并回退默认值，记录此属性而非静默启动。
    /// </summary>
    public string? LoadErrorMessage
    {
        get => _loadErrorMessage;
    set => SetProperty(ref _loadErrorMessage, value);
    }

    private int _schemaVersion = CurrentSchemaVersion;

    /// <summary>settings.json 结构版本；缺少该字段的旧配置按版本 0 迁移。</summary>
    public int SchemaVersion
    {
        get => _schemaVersion;
    set => SetProperty(ref _schemaVersion, value);
    }

    private string _configDirectory = "Config";

    /// <summary>
    /// 配置文件目录名（不可改为绝对路径）。
    /// 所有持久化文件统一存放于此。默认位于 %APPDATA%/Kanban/Config，
    /// 避免因 clean build 或删除 bin 目录导致数据丢失。
    /// </summary>
    public string ConfigDirectory
    {
        get => _configDirectory;
    set => SetProperty(ref _configDirectory, value);
    }

    /// <summary>
    /// 数据根目录（默认 %APPDATA%/Kanban），可通过环境变量 KANBAN_DATA_DIR 覆盖。
    /// </summary>
    [JsonIgnore]
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanban");

    /// <summary>
    /// 设置文件名（本类自身的持久化文件，存放PLC连接配置）
    /// </summary>
    public string SettingsFileName { get; set; } = "settings.json";

    /// <summary>
    /// 设置文件完整路径（派生属性，不参与序列化）
    /// </summary>
    [JsonIgnore]
    public string SettingsFilePath => GetFilePath(SettingsFileName);

    private PlcConfig _plcConfig = new();

    /// <summary>
    /// PLC连接配置
    /// </summary>
    public PlcConfig PlcConfig
    {
        get => _plcConfig;
    set
    {
        if (SetProperty(ref _plcConfig, value))
            OnPlcConfigChanged(value);
    }
    }

    private ObservableCollection<ConnectionProfile> _connectionProfiles = new();

    /// <summary>命名连接档案；default 档案与 PlcConfig 保持兼容别名关系。</summary>
    public ObservableCollection<ConnectionProfile> ConnectionProfiles
    {
        get => _connectionProfiles;
    set => SetProperty(ref _connectionProfiles, value);
    }

    public AppSettings()
    {
        _connectionProfiles.Add(new ConnectionProfile { Config = _plcConfig });
    }

    private int _pollingIntervalMs = 200;

    /// <summary>
    /// PLC 数据采集轮询间隔（毫秒），默认 200
    /// </summary>
    public int PollingIntervalMs
    {
        get => _pollingIntervalMs;
    set => SetProperty(ref _pollingIntervalMs, value);
    }

    private int _historyWriteIntervalScans = 25;

    /// <summary>
    /// 历史数据写入间隔（扫描次数），默认 25 次（≈5秒）
    /// </summary>
    public int HistoryWriteIntervalScans
    {
        get => _historyWriteIntervalScans;
    set => SetProperty(ref _historyWriteIntervalScans, value);
    }

    private int _plcBatchReadMaxLength = 64;

    /// <summary>PLC 连续批量读取的最大逻辑值数量，默认 64 个值。</summary>
    public int PlcBatchReadMaxLength
    {
        get => _plcBatchReadMaxLength;
    set => SetProperty(ref _plcBatchReadMaxLength, value);
    }

    private int _plcBatchReadMaxGapSlots = 1;

    /// <summary>批量读取允许跨过的最大连续逻辑地址空洞数，默认 1 个。</summary>
    public int PlcBatchReadMaxGapSlots
    {
        get => _plcBatchReadMaxGapSlots;
    set => SetProperty(ref _plcBatchReadMaxGapSlots, value);
    }

    private int _dashboardRefreshIntervalMs = 3000;

    /// <summary>
    /// 主页仪表板刷新间隔（毫秒），默认 3000ms（3 秒）
    /// </summary>
    public int DashboardRefreshIntervalMs
    {
        get => _dashboardRefreshIntervalMs;
    set => SetProperty(ref _dashboardRefreshIntervalMs, value);
    }

    private bool _isDarkTheme;

    /// <summary>
    /// 是否使用暗色主题
    /// </summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
    set => SetProperty(ref _isDarkTheme, value);
    }

    private double _uiScale = 1.0;

    /// <summary>
    /// 界面字号缩放（大屏远距可读性）：1.0=标准 100%，1.15=大屏 115%，1.3=超大屏 130%。
    /// 启动时与设置切换时由 FontSizeManager 应用到全局语义字号资源。
    /// </summary>
    public double UiScale
    {
        get => _uiScale;
    set => SetProperty(ref _uiScale, value);
    }

    private string _appTitle = "生产看板";

    /// <summary>
    /// 看板标题（窗口/页面标题，settings.json 可配置）。
    /// WPF 窗口标题与 WASM 屏端标题均使用此值；屏端经 Hub 的 GetTitleAsync 拉取（零配置）。
    /// </summary>
    public string AppTitle
    {
        get => _appTitle;
    set => SetProperty(ref _appTitle, value);
    }

    private AppLanguage _language = AppLanguage.Zh;

    /// <summary>
    /// 旧版界面语言枚举（兼容字段）。新语言不应添加枚举值，实际语言由 LanguageCode 驱动。
    /// </summary>
    public AppLanguage Language
    {
        get => _language;
    set
    {
        if (SetProperty(ref _language, value))
            OnLanguageChanged(value);
    }
    }

    private string _languageCode = string.Empty;

    /// <summary>
    /// 当前界面语言文化代码，由 Localization.csv 的语言列驱动，例如 zh-CN、en-US、ja-JP、pt-BR。
    /// 现场新增语言时只需在 CSV 增加列并重新发布，配置无需修改代码。
    /// </summary>
    public string LanguageCode
    {
        get => _languageCode;
    set
    {
        if (SetProperty(ref _languageCode, value))
            OnLanguageCodeChanged(value);
    }
    }

    /// <summary>规范化后的有效语言代码；未知/损坏配置回退到 CSV 默认语言。</summary>
    [JsonIgnore]
    public string EffectiveLanguageCode
    {
        get
        {
            if (LocalizationCatalog.IsSupported(LanguageCode))
                return LocalizationCatalog.Normalize(LanguageCode);
            return LegacyLanguageCode(Language);
        }
    }

    /// <summary>把旧枚举映射为兼容文化代码。</summary>
    public static string LegacyLanguageCode(AppLanguage language) => language switch
    {
        AppLanguage.En => "en-US",
        AppLanguage.Ja => "ja-JP",
        AppLanguage.PtBr => "pt-BR",
        _ => LocalizationCatalog.DefaultLanguage,
    };

    private static AppLanguage? TryGetLegacyLanguage(string? languageCode)
    {
        if (string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase))
            return AppLanguage.En;
        if (string.Equals(languageCode, "ja-JP", StringComparison.OrdinalIgnoreCase))
            return AppLanguage.Ja;
        if (string.Equals(languageCode, "pt-BR", StringComparison.OrdinalIgnoreCase))
            return AppLanguage.PtBr;
        if (string.Equals(languageCode, LocalizationCatalog.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            return AppLanguage.Zh;
        return null;
    }

    /// <summary>把旧版语言代码映射为兼容枚举；新增语言无法映射时回退为中文。</summary>
    public static AppLanguage LegacyLanguage(string? languageCode)
        => TryGetLegacyLanguage(languageCode) ?? AppLanguage.Zh;

    private void OnLanguageChanged(AppLanguage value)
    {
        var legacyCode = LegacyLanguageCode(value);
        if (!string.Equals(LanguageCode, legacyCode, StringComparison.OrdinalIgnoreCase))
            LanguageCode = legacyCode;
    }

    private void OnLanguageCodeChanged(string value)
    {
        var legacyLanguage = TryGetLegacyLanguage(value);
        if (legacyLanguage.HasValue && Language != legacyLanguage.Value)
            Language = legacyLanguage.Value;
    }

    private bool _enableAlarmSound = true;

    /// <summary>
    /// 是否启用新报警声音。默认开启；关闭后仍保留页面上的视觉提醒和报警历史。
    /// </summary>
    public bool EnableAlarmSound
    {
        get => _enableAlarmSound;
    set => SetProperty(ref _enableAlarmSound, value);
    }

    private bool _enableAutomaticDailyReport;

    /// <summary>是否自动生成上一自然日的单设备生产日报 PDF，默认关闭。</summary>
    public bool EnableAutomaticDailyReport
    {
        get => _enableAutomaticDailyReport;
    set => SetProperty(ref _enableAutomaticDailyReport, value);
    }

    private TimeSpan _automaticDailyReportTime = new(23, 59, 0);

    /// <summary>自动日报生成时刻。程序运行中会在该时刻之后生成一次上一自然日报表。</summary>
    public TimeSpan AutomaticDailyReportTime
    {
        get => _automaticDailyReportTime;
    set => SetProperty(ref _automaticDailyReportTime, value);
    }

    private bool _automaticDailyReportIsMaster = true;

    /// <summary>日报主节点标识：多屏部署时仅在标记为主节点的实例生成日报，避免重复 PDF。默认开启（单机无感知）。</summary>
    public bool AutomaticDailyReportIsMaster
    {
        get => _automaticDailyReportIsMaster;
    set => SetProperty(ref _automaticDailyReportIsMaster, value);
    }

    private KanbanDataMode _dataMode;

    /// <summary>
    /// 数据采集模式：Local（本进程采集，默认）/ Remote（连 Kanban.Collector 服务进程）。
    /// </summary>
    public KanbanDataMode DataMode
    {
        get => _dataMode;
    set => SetProperty(ref _dataMode, value);
    }

    private string _collectorHubUrl = $"http://127.0.0.1:{Kanban.Contracts.KanbanHubPaths.DefaultPort}{Kanban.Contracts.KanbanHubPaths.HubPath}";

    /// <summary>
    /// Remote 模式下 Collector 的 SignalR 地址（默认 http://127.0.0.1:5129/hubs/kanban）。
    /// 多屏部署时指向工控机上 Collector 的地址。端口与路径引用 Kanban.Contracts.KanbanHubPaths。
    /// </summary>
    public string CollectorHubUrl
    {
        get => _collectorHubUrl;
    set => SetProperty(ref _collectorHubUrl, value);
    }

    private KanbanRunMode _runMode = KanbanRunMode.Full;

    /// <summary>
    /// 运行模式：Full（展示+管理，默认）/ Viewer（屏端只展示）。
    /// </summary>
    public KanbanRunMode RunMode
    {
        get => _runMode;
    set => SetProperty(ref _runMode, value);
    }

    private bool _displayCarouselEnabled = true;

    /// <summary>
    /// 过道电视轮播：首页 → 产线 → 报警（无报警跳过）。
    /// 默认开启；完整模式可在显示设置中关闭。展示模式始终开启。
    /// </summary>
    public bool DisplayCarouselEnabled
    {
        get => _displayCarouselEnabled;
    set => SetProperty(ref _displayCarouselEnabled, value);
    }

    /// <summary>当前是否应跑轮播（展示模式或显式勾选）。</summary>
    [JsonIgnore]
    public bool IsDisplayCarouselActive => DisplayCarouselEnabled || RunMode == KanbanRunMode.Viewer;

    private bool _hasCompletedFirstRunGuide;

    /// <summary>
    /// 是否已完成首次运行引导（欢迎向导）。完成后不再自动弹出，仍可通过 F1 / 帮助按钮打开手册。
    /// </summary>
    public bool HasCompletedFirstRunGuide
    {
        get => _hasCompletedFirstRunGuide;
    set => SetProperty(ref _hasCompletedFirstRunGuide, value);
    }

    private ObservableCollection<ShiftConfig> _shifts = GetDefaultShifts();

    /// <summary>
    /// 班次配置列表（支持多班次编辑），默认白班 08:00-20:00 + 夜班 20:00-次日08:00
    /// </summary>
    public ObservableCollection<ShiftConfig> Shifts
    {
        get => _shifts;
    set => SetProperty(ref _shifts, value);
    }

    /// <summary>
    /// 班次集合读写锁（审查修复 2026-08-13）：ConfigSyncHandler（Hub 线程）与 SettingsViewModel.CopySettings
    /// （UI 线程）原地 Clear+Add 写入、采集轮询线程与 Hub 快照枚举读取时共同持有，
    /// 消除"写入中途被枚举 → Collection was modified / 半集合窗口"竞态。
    /// [JsonIgnore]：AppSettings 经 JSON 序列化落盘/克隆（CloneSettings 用 JSON 往返），
    /// 不标记会把锁对象写进 settings.json 且克隆后锁实例分裂。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object ShiftsLock { get; } = new();

    /// <summary>
    /// 设置整体更新锁（审查修复 2026-09-02，P0-2）：覆盖 <see cref="Shifts"/> 以外的全部设置字段
    /// （轮询参数、批量读上限、PlcConfig、ConnectionProfiles 换引用等）。
    /// 写侧（SettingsViewModel.CopySettings / ConfigSyncHandler）写入时持有；
    /// 采集线程（PlcDataAcquisitionService / PlcScanPipeline / PlcRuntimeSession）跨线程读取时经
    /// 各自的 Get*Snapshot() 取快照后再使用，禁止在锁外直接枚举可变集合。
    /// [JsonIgnore]：同 ShiftsLock，避免锁对象被序列化进 settings.json。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object UpdateLock { get; } = new();

    /// <summary>
    /// 班次配置的锁内只读快照（审查修复 2026-09-02，P0-1）。
    /// 跨线程读取点（ViewModel 的 Task.Run 后台查询、LastShiftComparisonProvider 等）必须经本方法
    /// 取快照，禁止在锁外直接枚举 <see cref="Shifts"/>——UI 线程 CopySettings 的 Clear+Add 会与
    /// 后台枚举并发，触发 "Collection was modified" 或读到半集合窗口。
    /// UI 线程上的纯展示读取（同一 Dispatcher）可保留直读。
    /// </summary>
    public IReadOnlyList<ShiftConfig> GetShiftsSnapshot()
    {
        lock (ShiftsLock)
            return Shifts.ToList();
    }


    /// <summary>按档案 ID 查找连接配置；空 ID 兼容为默认档案。</summary>
    public ConnectionProfile? FindConnectionProfile(string? profileId)
    {
        EnsureConnectionProfiles();
        var normalizedId = string.IsNullOrWhiteSpace(profileId)
            ? ConnectionProfile.DefaultId
            : profileId.Trim();
        return ConnectionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>获取默认连接档案，确保旧配置已完成内存迁移。</summary>
    [JsonIgnore]
    public ConnectionProfile DefaultConnectionProfile
        => FindConnectionProfile(ConnectionProfile.DefaultId)!;

    /// <summary>
    /// 确保连接档案集合可用并完成旧配置迁移。默认档案的 Config 绑定到兼容的 PlcConfig 实例，
    /// 其余档案保留独立快照，供下一阶段 keyed runtime session 使用。
    /// </summary>
    public void EnsureConnectionProfiles()
    {
        ConnectionProfiles ??= new ObservableCollection<ConnectionProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ConnectionProfiles.Count; index++)
        {
            var profile = ConnectionProfiles[index]
                ?? throw new InvalidDataException(ValidationMessages.ConnectionProfileEntryNull);
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = index == 0 ? ConnectionProfile.DefaultId : Guid.NewGuid().ToString("N");
            profile.Id = profile.Id.Trim();
            if (!ids.Add(profile.Id))
                throw new InvalidDataException(string.Format(ValidationMessages.ConnectionProfileIdDuplicate, profile.Id));
            if (profile.Config is null)
                profile.Config = new PlcConfig();
            if (string.IsNullOrWhiteSpace(profile.Name)
                && string.Equals(profile.Id, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
                profile.Name = ConnectionProfile.DefaultName;
        }

        var defaultProfile = ConnectionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
        {
            defaultProfile = new ConnectionProfile
            {
                Id = ConnectionProfile.DefaultId,
                Name = ConnectionProfile.DefaultName,
                Config = PlcConfig,
            };
            ConnectionProfiles.Insert(0, defaultProfile);
        }

        defaultProfile.Config = PlcConfig;
    }

    /// <summary>使旧 PlcConfig 别名与默认档案保持同一实例。</summary>
    public void SynchronizeDefaultConnectionProfile()
    {
        EnsureConnectionProfiles();
        DefaultConnectionProfile.Config = PlcConfig;
    }

    /// <summary>创建连接档案的独立快照，供草稿和 Remote 同步使用。</summary>
    public ObservableCollection<ConnectionProfile> CreateConnectionProfilesSnapshot()
    {
        SynchronizeDefaultConnectionProfile();
        return new ObservableCollection<ConnectionProfile>(
            ConnectionProfiles.Select(profile => profile.CreateSnapshot()));
    }

    /// <summary>
    /// 创建全字段草稿副本（采集设置同步用）：把**所有持久化字段**拷贝到新实例，
    /// 供 ConfigSyncHandler 先落盘、后生效流程承载候选值。
    /// 草稿必须与运行实例除待改字段外完全一致——若只拷贝采集字段就全量序列化落盘，
    /// Language/AppTitle/DataMode/UiScale/IsDarkTheme 等非采集字段会被默认值覆盖
    /// （Remote 每保存一次采集设置即重置 MainAPP 语言/主题/日报配置）。
    /// PlcConfig 深拷贝、Shifts 拷贝为新集合，保证草稿独立于运行实例。
    /// </summary>
    internal AppSettings CreateDraft()
    {
        var draft = new AppSettings
        {
            SchemaVersion = SchemaVersion,
            ConfigDirectory = ConfigDirectory,
            SettingsFileName = SettingsFileName,
            PlcConfig = PlcConfig.CreateSnapshot(),
            PollingIntervalMs = PollingIntervalMs,
            HistoryWriteIntervalScans = HistoryWriteIntervalScans,
            PlcBatchReadMaxLength = PlcBatchReadMaxLength,
            PlcBatchReadMaxGapSlots = PlcBatchReadMaxGapSlots,
            DashboardRefreshIntervalMs = DashboardRefreshIntervalMs,
            IsDarkTheme = IsDarkTheme,
            UiScale = UiScale,
            AppTitle = AppTitle,
            LanguageCode = EffectiveLanguageCode,
            EnableAlarmSound = EnableAlarmSound,
            EnableAutomaticDailyReport = EnableAutomaticDailyReport,
            AutomaticDailyReportTime = AutomaticDailyReportTime,
            DataMode = DataMode,
            CollectorHubUrl = CollectorHubUrl,
            RunMode = RunMode,
            DisplayCarouselEnabled = DisplayCarouselEnabled,
            HasCompletedFirstRunGuide = HasCompletedFirstRunGuide,
            // 锁内快照拷贝：与原地写入方互斥（审查修复 2026-08-13）
            Shifts = LockedShiftsSnapshot(),
        };
        draft.ConnectionProfiles = CreateConnectionProfilesSnapshot();
        draft.EnsureConnectionProfiles();
        return draft;
    }

    /// <summary>班次集合锁内快照（CreateDraft/采集读取共用；审查修复 2026-08-13）。</summary>
    internal ObservableCollection<ShiftConfig> LockedShiftsSnapshot()
    {
        lock (ShiftsLock)
            return new ObservableCollection<ShiftConfig>(Shifts);
    }


    private void OnPlcConfigChanged(PlcConfig value)
    {
        if (_connectionProfiles is null)
            return;
        var defaultProfile = _connectionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is not null && !ReferenceEquals(defaultProfile.Config, value))
            defaultProfile.Config = value;
    }
}

