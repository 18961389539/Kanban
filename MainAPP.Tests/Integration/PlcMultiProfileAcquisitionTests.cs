using System.IO;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Data;
using MainAPP.Tests.Unit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcMultiProfileAcquisitionTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly AppSettings _settings;
    private readonly TrackingDriverFactory _factory = new();
    private readonly PlcRuntimeSessionManager _sessions;
    private readonly DeviceRepository _deviceRepository;
    private readonly InMemoryHistoryService _history = new();
    private readonly PlcDataAcquisitionService _service;
    private readonly FakePlcDriver _driverA;
    private readonly FakePlcDriver _driverB;

    public PlcMultiProfileAcquisitionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "KanbanMultiProfile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settings = new AppSettings { ConfigDirectory = _tempDirectory };
        _settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "line-a",
            Name = "Line A",
            Config = new PlcConfig { IpAddress = "10.0.0.11", Port = 4999 },
        });
        _settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "line-b",
            Name = "Line B",
            Config = new PlcConfig { IpAddress = "10.0.0.12", Port = 4999 },
        });

        var codecResolver = new PlcAddressCodecResolver(_settings);
        var brandRegistry = PlcBrandDescriptors.CreateDefault();
        var defaultProvider = new PlcRuntimeProfileProvider(_settings, codecResolver, brandRegistry);
        var defaultDriver = new SharedPlcDriverRouter(_factory, _settings.PlcConfig, defaultProvider);
        var defaultConnectionManager = new PlcConnectionManager(defaultDriver, _settings, defaultProvider);
        _sessions = new PlcRuntimeSessionManager(
            _settings,
            _factory,
            codecResolver,
            brandRegistry,
            defaultProvider,
            defaultDriver,
            defaultConnectionManager);

        var defaultSession = _sessions.Get(ConnectionProfile.DefaultId);
        _sessions.Get("line-a");
        _sessions.Get("line-b");
        _driverA = DriverFor("10.0.0.11");
        _driverB = DriverFor("10.0.0.12");

        var adapterResolver = new DeviceAdapterResolver(
            [new PlcDeviceAdapter(
                defaultSession.Driver,
                defaultSession.ProfileProvider,
                codecResolver,
                _sessions)],
            _settings);
        _deviceRepository = new DeviceRepository(_settings);
        _service = new PlcDataAcquisitionService(
            defaultSession.Driver,
            defaultSession.ConnectionManager,
            _settings,
            _history,
            _deviceRepository,
            new ProductionBaselineStore(_settings),
            NullLogger<PlcDataAcquisitionService>.Instance,
            adapterResolver: adapterResolver,
            runtimeSessions: _sessions);
    }

    [Fact]
    public void AcquisitionRoutesSameAddressesByProfileAndPreservesOtherSession()
    {
        var deviceA = AddDevice("device-a", "line-a");
        var deviceB = AddDevice("device-b", "line-b");
        SetProductionValues(_driverA, 11, 1, (int)DeviceStatus.Running);
        SetProductionValues(_driverB, 22, 2, (int)DeviceStatus.Alarm);
        _driverA.SetBool("M100", true);
        _driverB.SetBool("M100", false);

        _sessions.EnsureConnectedAll();
        var successfulDevices = _service.RefreshDeviceData(out var noDevicesToRead);
        var alarmScanSucceeded = _service.ScanAlarms();

        Assert.False(noDevicesToRead);
        Assert.Equal(2, successfulDevices.Count);
        Assert.Equal(11, _deviceRepository.RuntimeMap[deviceA.Id].OkProduction);
        Assert.Equal(22, _deviceRepository.RuntimeMap[deviceB.Id].OkProduction);
        Assert.Equal(1, _driverA.ReadInt32BatchCallCount);
        Assert.Equal(1, _driverB.ReadInt32BatchCallCount);
        Assert.True(alarmScanSucceeded);
        var alarmEvent = Assert.Single(_history.AlarmEvents);
        Assert.Equal(deviceA.Id, alarmEvent.DeviceId);

        var lineB = _sessions.Get("line-b");
        var lineBConfigureCount = _driverB.ConfigureCallCount;
        var lineBDisconnectCount = _driverB.DisconnectCallCount;
        _settings.FindConnectionProfile("line-a")!.Config.IpAddress = "10.0.0.21";
        _sessions.RefreshFromSettings();

        Assert.Equal("10.0.0.21", _sessions.Get("line-a").Profile.Config.IpAddress);
        Assert.Same(lineB, _sessions.Get("line-b"));
        Assert.Equal("10.0.0.12", lineB.Profile.Config.IpAddress);
        Assert.Equal(lineBConfigureCount, _driverB.ConfigureCallCount);
        Assert.Equal(lineBDisconnectCount, _driverB.DisconnectCallCount);
        Assert.True(_sessions.IsConnected("line-b"));

        _sessions.MarkDisconnected("line-a");
        Assert.False(_sessions.IsConnected("line-a"));
        Assert.True(_sessions.IsConnected("line-b"));
    }

    [Fact]
    public async Task PollingDisconnectsOnlyProfileWithFailedReads()
    {
        var deviceA = AddDevice("device-a", "line-a");
        var deviceB = AddDevice("device-b", "line-b");
        _driverA.SetFailing("D100");
        _driverA.SetFailing("D102");
        _driverA.SetFailing("D104");
        SetProductionValues(_driverB, 22, 2, (int)DeviceStatus.Running);
        _driverA.SetBool("M100", false);
        _driverB.SetBool("M100", false);
        _settings.PollingIntervalMs = 20;

        _service.Start();
        try
        {
            await WaitUntilAsync(
                () => !_sessions.IsConnected("line-a") && _sessions.IsConnected("line-b"),
                TimeSpan.FromSeconds(2));

            Assert.Equal(22, _deviceRepository.RuntimeMap[deviceB.Id].OkProduction);
            Assert.Equal(0, _deviceRepository.RuntimeMap[deviceA.Id].OkProduction);
        }
        finally
        {
            await _service.StopAsync();
        }
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    private Device AddDevice(string id, string profileId)
    {
        var device = new Device
        {
            Id = id,
            Name = id,
            ConnectionProfileId = profileId,
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
        };
        device.Alarms.Add(new Alarm
        {
            Id = id + "-alarm",
            DeviceId = id,
            Name = id + " alarm",
            PlcAddress = "M100",
            Level = AlarmLevel.High,
        });
        _deviceRepository.Devices.Add(device);
        _deviceRepository.AddRuntime(device);
        return device;
    }

    private static void SetProductionValues(FakePlcDriver driver, int ok, int ng, int status)
    {
        driver.SetInt32("D100", ok);
        driver.SetInt32("D102", ng);
        driver.SetInt32("D104", status);
    }

    private FakePlcDriver DriverFor(string ipAddress)
        => _factory.Created.Single(entry => entry.Config.IpAddress == ipAddress).Driver;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待 keyed profile 采集状态超时。");
            await Task.Delay(10);
        }
    }

    private sealed class TrackingDriverFactory : ISharedPlcDriverFactory
    {
        public List<(PlcConfig Config, FakePlcDriver Driver)> Created { get; } = [];

        public IPlcDriver Create(PlcConfig config)
        {
            var driver = new FakePlcDriver();
            Created.Add((config.CreateSnapshot(), driver));
            return driver;
        }
    }
}
