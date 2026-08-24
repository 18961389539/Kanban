using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>
    /// 配置文件加载错误消息（供调用方通知用户）。
    /// settings.json 损坏时备份为 .corrupt 并回退默认值，记录此属性而非静默启动。
    /// </summary>
    [ObservableProperty]
    private string? _loadErrorMessage;

    /// <summary>settings.json 结构版本；缺少该字段的旧配置按版本 0 迁移。</summary>
    [ObservableProperty]
    private int _schemaVersion = CurrentSchemaVersion;

    /// <summary>
    /// 配置文件目录名（不可改为绝对路径）。
    /// 所有持久化文件统一存放于此。默认位于 %APPDATA%/Kanban/Config，
    /// 避免因 clean build 或删除 bin 目录导致数据丢失。
    /// </summary>
    [ObservableProperty]
    private string _configDirectory = "Config";

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

    /// <summary>
    /// PLC连接配置
    /// </summary>
    [ObservableProperty]
    private PlcConfig _plcConfig = new();

    /// <summary>命名连接档案；default 档案与 PlcConfig 保持兼容别名关系。</summary>
    [ObservableProperty]
    private ObservableCollection<ConnectionProfile> _connectionProfiles = new();

    public AppSettings()
    {
        _connectionProfiles.Add(new ConnectionProfile { Config = _plcConfig });
    }

    /// <summary>
    /// PLC 数据采集轮询间隔（毫秒），默认 200
    /// </summary>
    [ObservableProperty]
    private int _pollingIntervalMs = 200;

    /// <summary>
    /// 历史数据写入间隔（扫描次数），默认 25 次（≈5秒）
    /// </summary>
    [ObservableProperty]
    private int _historyWriteIntervalScans = 25;

    /// <summary>PLC 连续批量读取的最大逻辑值数量，默认 64 个值。</summary>
    [ObservableProperty]
    private int _plcBatchReadMaxLength = 64;

    /// <summary>批量读取允许跨过的最大连续逻辑地址空洞数，默认 1 个。</summary>
    [ObservableProperty]
    private int _plcBatchReadMaxGapSlots = 1;

    /// <summary>
    /// 主页仪表板刷新间隔（毫秒），默认 3000ms（3 秒）
    /// </summary>
    [ObservableProperty]
    private int _dashboardRefreshIntervalMs = 3000;

    /// <summary>
    /// 是否使用暗色主题
    /// </summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>
    /// 界面字号缩放（大屏远距可读性）：1.0=标准 100%，1.15=大屏 115%，1.3=超大屏 130%。
    /// 启动时与设置切换时由 FontSizeManager 应用到全局语义字号资源。
    /// </summary>
    [ObservableProperty]
    private double _uiScale = 1.0;

    /// <summary>
    /// 看板标题（窗口/页面标题，settings.json 可配置）。
    /// WPF 窗口标题与 WASM 屏端标题均使用此值；屏端经 Hub 的 GetTitleAsync 拉取（零配置）。
    /// </summary>
    [ObservableProperty]
    private string _appTitle = "生产看板";

    /// <summary>
    /// 旧版界面语言枚举（兼容字段）。新语言不应添加枚举值，实际语言由 LanguageCode 驱动。
    /// </summary>
    [ObservableProperty]
    private AppLanguage _language = AppLanguage.Zh;

    /// <summary>
    /// 当前界面语言文化代码，由 Localization.csv 的语言列驱动，例如 zh-CN、en-US、ja-JP、pt-BR。
    /// 现场新增语言时只需在 CSV 增加列并重新发布，配置无需修改代码。
    /// </summary>
    [ObservableProperty]
    private string _languageCode = string.Empty;

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

    partial void OnLanguageChanged(AppLanguage value)
    {
        var legacyCode = LegacyLanguageCode(value);
        if (!string.Equals(LanguageCode, legacyCode, StringComparison.OrdinalIgnoreCase))
            LanguageCode = legacyCode;
    }

    partial void OnLanguageCodeChanged(string value)
    {
        var legacyLanguage = TryGetLegacyLanguage(value);
        if (legacyLanguage.HasValue && Language != legacyLanguage.Value)
            Language = legacyLanguage.Value;
    }

    /// <summary>
    /// 是否启用新报警声音。默认开启；关闭后仍保留页面上的视觉提醒和报警历史。
    /// </summary>
    [ObservableProperty]
    private bool _enableAlarmSound = true;

    /// <summary>是否自动生成上一自然日的单设备生产日报 PDF，默认关闭。</summary>
    [ObservableProperty]
    private bool _enableAutomaticDailyReport;

    /// <summary>自动日报生成时刻。程序运行中会在该时刻之后生成一次上一自然日报表。</summary>
    [ObservableProperty]
    private TimeSpan _automaticDailyReportTime = new(23, 59, 0);

    /// <summary>日报主节点标识：多屏部署时仅在标记为主节点的实例生成日报，避免重复 PDF。默认开启（单机无感知）。</summary>
    [ObservableProperty]
    private bool _automaticDailyReportIsMaster = true;

    /// <summary>
    /// 数据采集模式：Local（本进程采集，默认）/ Remote（连 Kanban.Collector 服务进程）。
    /// </summary>
    [ObservableProperty]
    private KanbanDataMode _dataMode;

    /// <summary>
    /// Remote 模式下 Collector 的 SignalR 地址（默认 http://127.0.0.1:5129/hubs/kanban）。
    /// 多屏部署时指向工控机上 Collector 的地址。端口与路径引用 Kanban.Contracts.KanbanHubPaths。
    /// </summary>
    [ObservableProperty]
    private string _collectorHubUrl = $"http://127.0.0.1:{Kanban.Contracts.KanbanHubPaths.DefaultPort}{Kanban.Contracts.KanbanHubPaths.HubPath}";

    /// <summary>
    /// 运行模式：Full（展示+管理，默认）/ Viewer（屏端只展示）。
    /// </summary>
    [ObservableProperty]
    private KanbanRunMode _runMode = KanbanRunMode.Full;

    /// <summary>
    /// 是否已完成首次运行引导（欢迎向导）。完成后不再自动弹出，仍可通过 F1 / 帮助按钮打开手册。
    /// </summary>
    [ObservableProperty]
    private bool _hasCompletedFirstRunGuide;

    /// <summary>
    /// 班次配置列表（支持多班次编辑），默认白班 08:00-20:00 + 夜班 20:00-次日08:00
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ShiftConfig> _shifts = GetDefaultShifts();

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
    /// 每路径互斥锁：保证同一文件路径的并发写串行化，避免临时文件名冲突与丢失更新。
    /// 多设备基线同时持久化时（班次切换瞬间），不同 path 各自一把锁互不阻塞，同 path 串行。
    /// </summary>
    private static readonly Dictionary<string, object> s_pathLocks = new();
    private static readonly object s_pathLocksLock = new();

    /// <summary>
    /// 原子写入文件：先写临时文件再 File.Move(overwrite:true) 覆盖目标文件。
    /// File.Move 在同一卷上是原子操作，避免写入中途崩溃产生截断的文件。
    /// 写入前若目标文件已存在，先复制为同名 .bak 作为"上一版本"备份，
    /// 防止保存内容本身有误（误删设备、配错地址等）时旧版被直接覆盖、无法回滚。
    /// devices.json / settings.json / baselines.json 三个持久化文件共用本方法，均自动获得备份。
    /// </summary>
    /// <remarks>
    /// 线程安全：对同一 path 用专属锁串行化，避免固定 .tmp 路径在并发写时被覆盖导致丢失更新。
    /// 临时文件名用 <see cref="Path.GetRandomFileName"/> 生成唯一串，进一步消除冲突窗口。
    /// 备份失败与临时文件写入失败均记录 Serilog 警告但不阻断主流程（避免磁盘短暂故障导致整体不可用）。
    /// </remarks>
    public static void WriteFileAtomically(string path, string content)
    {
        // 取（或创建）该 path 专属的锁对象，不同 path 互不阻塞
        object? pathLock;
        lock (s_pathLocksLock)
        {
            if (!s_pathLocks.TryGetValue(path, out pathLock))
            {
                pathLock = new object();
                s_pathLocks[path] = pathLock;
            }
        }

        lock (pathLock!)
        {
            // 保存前版本备份：把当前有效版本留作 .bak（备份失败不影响主写入）
            if (File.Exists(path))
            {
                try
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                // 注意：目标 .bak 被占用/被安全软件短暂锁定/权限拒绝时抛 UnauthorizedAccessException，
                // 与 IOException 平级（均非 IOException 子类），必须一并兜住，否则备份失败会冒泡
                // 成未处理异常（如登录时 users.json 备份失败曾导致 MainAPP 直接崩溃退出）。
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 备份失败不应阻断主写入流程，但需记录便于排查磁盘/权限问题
                    Log.Warning(ex, "备份文件 {Path} → .bak 失败，继续主写入", path);
                }
            }

            // 唯一临时文件名：避免固定 .tmp 在极端并发场景（即使已加锁，跨进程时仍可能冲突）下被覆盖
            var tempPath = path + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // 主写入失败：清理残留临时文件，避免下次写入时遇到同名文件冲突
                Log.Error(ex, "原子写入 {Path} 失败", path);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* 清理失败无法进一步处理，等待下次写入覆盖或人工清理 */ }
                throw;
            }
        }
    }

    /// <summary>
    /// 获取配置目录下指定文件的完整路径，基于 DataRoot（%APPDATA%/Kanban）。
    /// </summary>
    public string GetFilePath(string fileName)
    {
        return Path.Combine(DataRoot, ConfigDirectory, fileName);
    }

    /// <summary>
    /// 确保配置目录存在
    /// </summary>
    public void EnsureDirectory()
    {
        var fullPath = Path.Combine(DataRoot, ConfigDirectory);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
    }

    /// <summary>
    /// 保存本类全部设置属性（序列化整个 AppSettings 实例）。
    /// 采用原子写入（写临时文件 → 重命名），避免断电/强制关机时产生半截 JSON 导致配置损坏。
    /// </summary>
    public void Save()
    {
        SynchronizeDefaultConnectionProfile();
        WriteSettingsFile(this);
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

    /// <summary>
    /// 把指定实例的完整设置序列化到 settings.json（原子写入）。
    /// 供 Remote 设置同步等"先落盘、后生效"流程使用：写入候选配置成功后，
    /// 调用方才把候选值应用到运行实例，避免磁盘写入失败时内存/驱动已变、磁盘未变的分裂状态。
    /// </summary>
    internal static void WriteSettingsFile(AppSettings candidate)
    {
        candidate.SynchronizeDefaultConnectionProfile();
        candidate.EnsureDirectory();
        candidate.SchemaVersion = CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(candidate, JsonOptions);
        WriteFileAtomically(candidate.SettingsFilePath, json);
    }

    /// <summary>
    /// 加载本类全部设置属性
    /// </summary>
    public void Load()
    {
        if (!File.Exists(SettingsFilePath)) return;

        try
        {
            var json = new SettingsMigrationRunner().Migrate(File.ReadAllText(SettingsFilePath));
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings != null)
            {
                settings.EnsureConnectionProfiles();
                SchemaVersion = CurrentSchemaVersion;
                ConnectionProfiles = settings.ConnectionProfiles ?? new ObservableCollection<ConnectionProfile>();
                PlcConfig = settings.PlcConfig ?? new PlcConfig();
                EnsureConnectionProfiles();
                // ConfigDirectory 固定为程序运行目录下的 "Config"，不从 settings.json 读回，
                // 避免旧配置把数据目录指向其他位置导致数据分散
                SettingsFileName = settings.SettingsFileName;
                PollingIntervalMs = settings.PollingIntervalMs;
                HistoryWriteIntervalScans = settings.HistoryWriteIntervalScans;
                PlcBatchReadMaxLength = settings.PlcBatchReadMaxLength;
                PlcBatchReadMaxGapSlots = settings.PlcBatchReadMaxGapSlots;
                DashboardRefreshIntervalMs = settings.DashboardRefreshIntervalMs;
                AppTitle = string.IsNullOrWhiteSpace(settings.AppTitle) ? "生产看板" : settings.AppTitle;
                var legacyLanguage = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.Zh;
                var loadedLanguageCode = LocalizationCatalog.IsSupported(settings.LanguageCode)
                    ? LocalizationCatalog.Normalize(settings.LanguageCode)
                    : LegacyLanguageCode(legacyLanguage);
                Language = LegacyLanguage(loadedLanguageCode);
                LanguageCode = loadedLanguageCode;
                IsDarkTheme = settings.IsDarkTheme;
                UiScale = settings.UiScale;
                EnableAlarmSound = settings.EnableAlarmSound;
                EnableAutomaticDailyReport = settings.EnableAutomaticDailyReport;
                AutomaticDailyReportTime = settings.AutomaticDailyReportTime;
                DataMode = Enum.IsDefined(settings.DataMode) ? settings.DataMode : KanbanDataMode.Local;
                if (!string.IsNullOrWhiteSpace(settings.CollectorHubUrl))
                    CollectorHubUrl = settings.CollectorHubUrl;
                RunMode = Enum.IsDefined(settings.RunMode) ? settings.RunMode : KanbanRunMode.Full;
                HasCompletedFirstRunGuide = settings.HasCompletedFirstRunGuide;
                // 保证至少一个班次：若加载到空集合或 null，回退到默认两个班次
                Shifts = (settings.Shifts is { Count: > 0 } shifts)
                    ? shifts
                    : GetDefaultShifts();
            }
        }
        catch (UnsupportedSettingsVersionException ex)
        {
            Log.Error(ex, "settings.json 版本过高，保留原文件并停止加载");
            LoadErrorMessage = $"settings.json 版本 {ex.Version} 高于当前程序支持的版本 {ex.CurrentVersion}，" +
                               "请升级程序后再使用此配置文件。原文件已保留。";
        }
        catch (Exception ex)
        {
            // 文件损坏：备份原文件供用户恢复（参照 DeviceRepository.LoadAll 的 .corrupt 机制）
            Log.Warning(ex, "settings.json 解析失败，将备份原文件并回退默认值");
            var corruptPath = SettingsFilePath + ".corrupt";
            try
            {
                if (File.Exists(corruptPath))
                    File.Delete(corruptPath);
                File.Move(SettingsFilePath, corruptPath);
            }
            catch (Exception backupEx)
            {
                Log.Warning(backupEx, "settings.json 备份为 .corrupt 失败，原文件保留原位");
            }

            LoadErrorMessage = $"配置文件 settings.json 损坏（{ex.Message}），已备份为 settings.json.corrupt。" +
                               "已恢复为默认设置，请重新保存配置。";
            // 使用默认值继续运行，不崩溃
            Shifts = GetDefaultShifts();
        }
    }

    /// <summary>
    /// 默认班次配置：白班 08:00-20:00 + 夜班 20:00-次日 08:00（跨天）
    /// </summary>
    internal static ObservableCollection<ShiftConfig> GetDefaultShifts() => new()
    {
        new ShiftConfig { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
        new ShiftConfig { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) }
    };

    /// <summary>
    /// 启动时验证配置完整性，返回所有验证错误。空列表表示通过。
    /// 启动阶段调用（App.OnStartup 中 Load 后立即调用）：
    /// - PLC IP 必须为合法 IPv4（启动 PLC 连接前拦截，避免无效 IP 触发连接超时再回退）
    /// - PLC 端口必须在 1-65535 范围内
    /// - 轮询间隔必须为正数（≥1ms），避免 0 触发 CPU 满载
    /// - 历史写入间隔必须为正数（≥1 次），避免 0 触发每轮写库
    /// - 主页刷新间隔必须为正数（≥1ms），避免 0 触发 UI 满刷新
    /// - 班次配置至少一个，且每个班次名称非空、起止时间合法
    /// </summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (ConnectionProfiles is null || ConnectionProfiles.Count == 0)
            EnsureConnectionProfiles();

        ValidateConnectionProfiles(ConnectionProfiles!, errors);
        ValidatePlcConfig(PlcConfig, errors, profileId: null);

        // 间隔类配置：必须为正数；同时设上限防止 Remote 设置同步传入极端值（如 1ms 轮询造成 CPU 满载）
        if (PollingIntervalMs < 10 || PollingIntervalMs > 60000)
            errors.Add(string.Format(ValidationMessages.PollingIntervalInvalid, PollingIntervalMs));
        if (HistoryWriteIntervalScans < 1 || HistoryWriteIntervalScans > 3600)
            errors.Add(string.Format(ValidationMessages.HistoryWriteIntervalInvalid, HistoryWriteIntervalScans));
        if (DashboardRefreshIntervalMs < 1)
            errors.Add(string.Format(ValidationMessages.DashboardRefreshIntervalInvalid, DashboardRefreshIntervalMs));
        // 批量读取参数上限：超出后 PlcScanPipeline 的 Math.Clamp 上限会抛 ArgumentOutOfRangeException
        if (PlcBatchReadMaxLength < 1 || PlcBatchReadMaxLength > 1024)
            errors.Add(string.Format(ValidationMessages.PlcBatchReadMaxLengthInvalid, PlcBatchReadMaxLength));
        if (PlcBatchReadMaxGapSlots < 0 || PlcBatchReadMaxGapSlots > 256)
            errors.Add(string.Format(ValidationMessages.PlcBatchReadMaxGapSlotsInvalid, PlcBatchReadMaxGapSlots));

        // 班次配置验证
        if (Shifts is null || Shifts.Count == 0)
        {
            errors.Add(ValidationMessages.NoShiftsConfigured);
        }
        else
        {
            for (int i = 0; i < Shifts.Count; i++)
            {
                var s = Shifts[i];
                if (string.IsNullOrWhiteSpace(s.Name))
                    errors.Add(string.Format(ValidationMessages.ShiftNameEmpty, i + 1));
                // ShiftConfig.Contains 自身验证起止时间（跨天班次合法），这里只检查零时长
                if (s.StartTime == s.EndTime)
                    errors.Add(string.Format(ValidationMessages.ShiftStartEndEqual, s.Name, s.StartTime));
            }
        }

        return errors;
    }

    private static void ValidateConnectionProfiles(
        IReadOnlyList<ConnectionProfile> profiles,
        List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (profile is null)
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileEmpty, index + 1));
                continue;
            }

            var profileId = profile.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileIdEmpty, index + 1));
            }
            else if (!ids.Add(profileId))
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileIdDuplicate, profileId));
            }

            var profileLabel = string.IsNullOrWhiteSpace(profileId)
                ? (index + 1).ToString()
                : profileId;
            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add(string.Format(ValidationMessages.ConnectionProfileNameEmpty, profileLabel));
            if (profile.Config is null)
            {
                errors.Add(string.Format(ValidationMessages.ConnectionProfileConfigMissing, profileLabel));
                continue;
            }

            if (!string.Equals(profileId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
                ValidatePlcConfig(profile.Config, errors, profileId);
        }
    }

    private static void ValidatePlcConfig(PlcConfig config, List<string> errors, string? profileId)
    {
        void Add(string message)
        {
            errors.Add(string.IsNullOrWhiteSpace(profileId)
                || string.Equals(profileId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"连接档案 '{profileId}'：{message}");
        }

        // PLC IP 验证：System.Net.IPAddress.TryParse 接受 "1.2.3.4" 但也接受 "1"、"::1" 等，
        // 用 AddressFamily 限定 InterNetwork（IPv4），与三菱 MC 协议一致。
        var ip = config.IpAddress ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ip))
        {
            Add(ValidationMessages.PlcIpEmpty);
        }
        else if (!System.Net.IPAddress.TryParse(ip, out var addr)
                 || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Add(string.Format(ValidationMessages.PlcIpInvalid, ip));
        }

        // PLC 端口验证：1-65535
        if (config.Port < 1 || config.Port > 65535)
            Add(string.Format(ValidationMessages.PlcPortOutOfRange, config.Port));
        if (!Enum.IsDefined(config.Brand))
            Add(string.Format(ValidationMessages.PlcBrandInvalid, config.Brand));
        else
        {
            var brandErrors = new List<string>();
            PlcBrandDescriptors.CreateDefault().Resolve(config.Brand).Validate(config, brandErrors);
            foreach (var brandError in brandErrors)
                Add(brandError);
        }
        if (config.TimeoutMs < 100 || config.TimeoutMs > 60000)
            Add(string.Format(ValidationMessages.PlcTimeoutOutOfRange, config.TimeoutMs));
    }

    partial void OnPlcConfigChanged(PlcConfig value)
    {
        if (_connectionProfiles is null)
            return;
        var defaultProfile = _connectionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is not null && !ReferenceEquals(defaultProfile.Config, value))
            defaultProfile.Config = value;
    }
}

