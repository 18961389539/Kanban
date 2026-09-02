using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

public sealed class PlcRuntimeSession : IDisposable
{
    private readonly bool _ownsResources;
    private bool _disposed;

    internal PlcRuntimeSession(
        string profileId,
        IPlcDriver driver,
        IPlcRuntimeProfileProvider profileProvider,
        PlcConnectionManager connectionManager,
        bool ownsResources)
    {
        ProfileId = NormalizeProfileId(profileId);
        Driver = driver;
        ProfileProvider = profileProvider;
        ConnectionManager = connectionManager;
        _ownsResources = ownsResources;
    }

    public string ProfileId { get; }
    public IPlcDriver Driver { get; }
    public IPlcRuntimeProfileProvider ProfileProvider { get; }
    public PlcConnectionManager ConnectionManager { get; }
    public PlcRuntimeProfile Profile => ProfileProvider.Current;

    // 签名按 Profile.Version 缓存：Version 仅 Refresh 时递增，避免每次访问都做全对象 JSON 序列化。
    // Profile.Config 是不可变快照，Version 不变即签名不变，缓存安全。
    private string _signature = string.Empty;
    private long _signatureVersion = -1;

    public string ConfigurationSignature
    {
        get
        {
            var profile = Profile;
            if (profile.Version != _signatureVersion)
            {
                _signature = profile.Config.GetConfigurationSignature();
                _signatureVersion = profile.Version;
            }
            return _signature;
        }
    }

    internal void Configure(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_disposed)
            throw new ObjectDisposedException(nameof(PlcRuntimeSession));
        ConnectionManager.Disconnect();
        Driver.Configure(config);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_ownsResources) return;

        try { ConnectionManager.Disconnect(); }
        finally { Driver.Dispose(); }
    }

    internal static string NormalizeProfileId(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? ConnectionProfile.DefaultId
            : profileId.Trim();
}

/// <summary>
/// 单个连接档案的运行健康快照。只包含连接状态与非敏感配置标识，供 Collector
/// readiness/metrics 使用，不暴露 PLC 密码或完整配置。
/// </summary>
public sealed record PlcRuntimeSessionDiagnosticsSnapshot
{
    public string ProfileId { get; init; } = ConnectionProfile.DefaultId;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public string ProtocolKey { get; init; } = DataSourceProtocolKeys.Plc;
    public PlcBrand Brand { get; init; }
    public bool IsConnected { get; init; }
    public string ConnectionStatus { get; init; } = string.Empty;
    public int ConsecutiveFailures { get; init; }
    public int TotalDisconnectCount { get; init; }
    public DateTime? DisconnectedAt { get; init; }
    public TimeSpan? LastDisconnectDuration { get; init; }
    public DateTime? LastSuccessfulAcquisitionAt { get; init; }
    public DateTime? LastAcquisitionFailureAt { get; init; }
    public int ConsecutiveAcquisitionFailures { get; init; }
    public int AcquisitionFailureCount { get; init; }
    public string? LastAcquisitionFailureMessage { get; init; }
}

public interface IPlcRuntimeSessionManager : IDisposable
{
    PlcRuntimeSession Get(string? profileId);
    IReadOnlyList<PlcRuntimeSession> GetSnapshot();
    IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot> GetDiagnosticsSnapshot();
    void RecordAcquisitionResults(
        IReadOnlySet<string> successfulProfileIds,
        IReadOnlySet<string> failedProfileIds,
        bool noDevicesToRead,
        string? failureMessage = null);
    bool IsAnyConnected { get; }
    bool IsConnected(string? profileId);
    void EnsureConnectedAll();
    void MarkDisconnected(string? profileId, DisconnectionReason reason = DisconnectionReason.ReadFailure);
    void MarkAllDisconnected(DisconnectionReason reason = DisconnectionReason.ReadFailure);
    void Disconnect(string? profileId);
    void RefreshFromSettings();
}

public sealed class PlcRuntimeSessionManager : IPlcRuntimeSessionManager
{
    private readonly object _sync = new();
    private readonly AppSettings _settings;
    private readonly ISharedPlcDriverFactory _factory;
    private readonly IPlcAddressCodecResolver _codecResolver;
    private readonly IPlcBrandRegistry _brandRegistry;
    private readonly Dictionary<string, PlcRuntimeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProfileAcquisitionDiagnostics> _acquisitionDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlcRuntimeSession _defaultSession;
    private bool _disposed;

    public PlcRuntimeSessionManager(
        AppSettings settings,
        ISharedPlcDriverFactory factory,
        IPlcAddressCodecResolver codecResolver,
        IPlcBrandRegistry brandRegistry,
        IPlcRuntimeProfileProvider defaultProfileProvider,
        SharedPlcDriverRouter defaultDriver,
        PlcConnectionManager defaultConnectionManager)
    {
        _settings = settings;
        _factory = factory;
        _codecResolver = codecResolver;
        _brandRegistry = brandRegistry;
        _defaultSession = new PlcRuntimeSession(
            ConnectionProfile.DefaultId,
            defaultDriver,
            defaultProfileProvider,
            defaultConnectionManager,
            ownsResources: false);
        _sessions[ConnectionProfile.DefaultId] = _defaultSession;
        RefreshFromSettings();
    }

    public PlcRuntimeSession Get(string? profileId)
    {
        var normalizedId = PlcRuntimeSession.NormalizeProfileId(profileId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var profile = _settings.FindConnectionProfile(normalizedId)
                ?? throw new InvalidOperationException($"不存在的连接档案：{normalizedId}。");
            if (string.Equals(normalizedId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
                return _defaultSession;

            if (!_sessions.TryGetValue(normalizedId, out var session))
            {
                session = CreateSession(profile);
                _sessions[normalizedId] = session;
            }
            else if (!string.Equals(session.ConfigurationSignature,
                         profile.Config.GetConfigurationSignature(), StringComparison.Ordinal))
            {
                session.Configure(profile.Config);
            }
            return session;
        }
    }

    public IReadOnlyList<PlcRuntimeSession> GetSnapshot()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _sessions.Values.ToArray();
        }
    }

    public IReadOnlyList<PlcRuntimeSessionDiagnosticsSnapshot> GetDiagnosticsSnapshot()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _sessions.Values
                .Select(session =>
                {
                    var config = session.Profile.Config;
                    var connection = session.ConnectionManager;
                    return new PlcRuntimeSessionDiagnosticsSnapshot
                    {
                        ProfileId = session.ProfileId,
                        IpAddress = config.IpAddress,
                        Port = config.Port,
                        ProtocolKey = string.IsNullOrWhiteSpace(config.ProtocolKey)
                            ? DataSourceProtocolKeys.Plc
                            : config.ProtocolKey.Trim().ToLowerInvariant(),
                        Brand = config.Brand,
                        IsConnected = connection.IsConnected,
                        ConnectionStatus = connection.ConnectionStatus,
                        ConsecutiveFailures = connection.ConsecutiveFailures,
                        TotalDisconnectCount = connection.TotalDisconnectCount,
                        DisconnectedAt = connection.DisconnectedAt,
                        LastDisconnectDuration = connection.LastDisconnectDuration,
                        LastSuccessfulAcquisitionAt = _acquisitionDiagnostics.TryGetValue(session.ProfileId, out var acquisition)
                            ? acquisition.LastSuccessfulAt
                            : null,
                        LastAcquisitionFailureAt = acquisition?.LastFailureAt,
                        ConsecutiveAcquisitionFailures = acquisition?.ConsecutiveFailures ?? 0,
                        AcquisitionFailureCount = acquisition?.FailureCount ?? 0,
                        LastAcquisitionFailureMessage = acquisition?.LastFailureMessage,
                    };
                })
                .ToArray();
        }
    }

    public void RecordAcquisitionResults(
        IReadOnlySet<string> successfulProfileIds,
        IReadOnlySet<string> failedProfileIds,
        bool noDevicesToRead,
        string? failureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(successfulProfileIds);
        ArgumentNullException.ThrowIfNull(failedProfileIds);
        if (noDevicesToRead) return;

        var successes = NormalizeProfileIds(successfulProfileIds);
        var failures = NormalizeProfileIds(failedProfileIds);
        var now = DateTime.Now;

        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (var profileId in successes)
            {
                if (!_sessions.ContainsKey(profileId)) continue;
                var diagnostics = GetOrCreateAcquisitionDiagnostics(profileId);
                diagnostics.LastSuccessfulAt = now;
                diagnostics.ConsecutiveFailures = 0;
            }

            foreach (var profileId in failures)
            {
                if (!_sessions.ContainsKey(profileId)) continue;
                var diagnostics = GetOrCreateAcquisitionDiagnostics(profileId);
                diagnostics.ConsecutiveFailures++;
                diagnostics.FailureCount++;
                diagnostics.LastFailureAt = now;
                diagnostics.LastFailureMessage = string.IsNullOrWhiteSpace(failureMessage)
                    ? "profile 采集失败"
                    : failureMessage;
            }
        }
    }

    public bool IsAnyConnected
    {
        get
        {
            lock (_sync)
                return _sessions.Values.Any(session => session.ConnectionManager.IsConnected);
        }
    }

    public bool IsConnected(string? profileId)
        => Get(profileId).ConnectionManager.IsConnected;

    public void EnsureConnectedAll()
    {
        RefreshFromSettings();
        foreach (var session in GetSnapshot())
            session.ConnectionManager.EnsureConnected();
    }

    public void MarkDisconnected(string? profileId, DisconnectionReason reason = DisconnectionReason.ReadFailure)
        => Get(profileId).ConnectionManager.MarkDisconnected(reason);

    public void MarkAllDisconnected(DisconnectionReason reason = DisconnectionReason.ReadFailure)
    {
        foreach (var session in GetSnapshot())
            session.ConnectionManager.MarkDisconnected(reason);
    }

    public void Disconnect(string? profileId)
        => Get(profileId).ConnectionManager.Disconnect();

    public void RefreshFromSettings()
    {
        List<PlcRuntimeSession> removed;

        // 审查修复 2026-09-02（P0-2）：先经 UpdateLock 取设置快照（含 Ensure 迁移），再进 _sync。
        // _sync 与写侧（SettingsViewModel.CopySettings / ConfigSyncHandler 的 UpdateLock）不是同一把锁，
        // 原先在 _sync 内枚举 ConnectionProfiles 且调用 EnsureConnectionProfiles（其 Insert(0,…) 分支
        // 会修改集合结构、原地改写 profile 属性），与写侧并发会抛 "Collection was modified" 或漏配档案。
        // 锁顺序固定：UpdateLock（外）→ _sync（内）；持 _sync 期间绝不获取 UpdateLock（防 A-B-BA 死锁）。
        List<ConnectionProfile> profilesSnapshot;
        ConnectionProfile defaultProfileSnapshot;
        lock (_settings.UpdateLock)
        {
            _settings.EnsureConnectionProfiles();
            defaultProfileSnapshot = _settings.DefaultConnectionProfile;
            profilesSnapshot = _settings.ConnectionProfiles.ToList();
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ConnectionProfile.DefaultId,
            };

            var defaultConfig = defaultProfileSnapshot.Config;
            if (!string.Equals(_defaultSession.ConfigurationSignature,
                    defaultConfig.GetConfigurationSignature(), StringComparison.Ordinal))
                _defaultSession.Configure(defaultConfig);

            foreach (var profile in profilesSnapshot)
            {
                var profileId = PlcRuntimeSession.NormalizeProfileId(profile.Id);
                if (string.Equals(profileId, ConnectionProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
                    continue;

                activeIds.Add(profileId);
                if (!_sessions.TryGetValue(profileId, out var session))
                {
                    _sessions[profileId] = CreateSession(profile);
                }
                else if (!string.Equals(session.ConfigurationSignature,
                             profile.Config.GetConfigurationSignature(), StringComparison.Ordinal))
                {
                    session.Configure(profile.Config);
                }
            }

            removed = _sessions
                .Where(pair => !activeIds.Contains(pair.Key) && !ReferenceEquals(pair.Value, _defaultSession))
                .Select(pair => pair.Value)
                .ToList();
            foreach (var session in removed)
                _sessions.Remove(session.ProfileId);
            foreach (var session in removed)
                _acquisitionDiagnostics.Remove(session.ProfileId);
        }

        foreach (var session in removed)
            session.Dispose();
    }

    public void Dispose()
    {
        List<PlcRuntimeSession> sessions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values
                .Where(session => !ReferenceEquals(session, _defaultSession))
                .ToList();
            _sessions.Clear();
            _sessions[ConnectionProfile.DefaultId] = _defaultSession;
            _acquisitionDiagnostics.Clear();
        }

        foreach (var session in sessions)
            session.Dispose();
    }

    private PlcRuntimeSession CreateSession(ConnectionProfile profile)
    {
        var provider = new PlcRuntimeProfileProvider(profile.Config, _codecResolver, _brandRegistry);
        var driver = new SharedPlcDriverRouter(_factory, profile.Config, provider);
        var connectionManager = new PlcConnectionManager(driver, _settings, provider, profile.Id);
        return new PlcRuntimeSession(profile.Id, driver, provider, connectionManager, ownsResources: true);
    }

    private ProfileAcquisitionDiagnostics GetOrCreateAcquisitionDiagnostics(string profileId)
    {
        if (!_acquisitionDiagnostics.TryGetValue(profileId, out var diagnostics))
        {
            diagnostics = new ProfileAcquisitionDiagnostics();
            _acquisitionDiagnostics[profileId] = diagnostics;
        }
        return diagnostics;
    }

    private static HashSet<string> NormalizeProfileIds(IEnumerable<string> profileIds)
        => profileIds
            .Select(PlcRuntimeSession.NormalizeProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class ProfileAcquisitionDiagnostics
    {
        public DateTime? LastSuccessfulAt { get; set; }
        public DateTime? LastFailureAt { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int FailureCount { get; set; }
        public string? LastFailureMessage { get; set; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PlcRuntimeSessionManager));
    }
}