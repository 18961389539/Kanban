using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcRuntimeSessionTests : IDisposable
{
    private readonly AppSettings _settings = new();
    private readonly TrackingDriverFactory _factory = new();
    private readonly IPlcAddressCodecResolver _codecResolver;
    private readonly PlcRuntimeSessionManager _sessions;

    public PlcRuntimeSessionTests()
    {
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

        _codecResolver = new PlcAddressCodecResolver(_settings);
        var brandRegistry = PlcBrandDescriptors.CreateDefault();
        var defaultProvider = new PlcRuntimeProfileProvider(
            _settings, _codecResolver, brandRegistry);
        var defaultDriver = new SharedPlcDriverRouter(
            _factory, _settings.PlcConfig, defaultProvider);
        var defaultConnectionManager = new PlcConnectionManager(
            defaultDriver, _settings, defaultProvider);
        _sessions = new PlcRuntimeSessionManager(
            _settings,
            _factory,
            _codecResolver,
            brandRegistry,
            defaultProvider,
            defaultDriver,
            defaultConnectionManager);
    }

    [Fact]
    public void Profiles_CreateIndependentDriversAndAdapters()
    {
        var lineA = _sessions.Get("line-a");
        var lineB = _sessions.Get("line-b");

        Assert.NotSame(lineA.Driver, lineB.Driver);
        Assert.Equal("10.0.0.11", lineA.Profile.Config.IpAddress);
        Assert.Equal("10.0.0.12", lineB.Profile.Config.IpAddress);

        var resolver = new DeviceAdapterResolver(
            [new PlcDeviceAdapter(
                _sessions.Get(ConnectionProfile.DefaultId).Driver,
                _sessions.Get(ConnectionProfile.DefaultId).ProfileProvider,
                _codecResolver,
                _sessions)],
            _settings);

        var adapterA = resolver.Resolve(new Device { ConnectionProfileId = "line-a" });
        var adapterB = resolver.Resolve(new Device { ConnectionProfileId = "line-b" });

        Assert.NotSame(adapterA, adapterB);
        var driverA = DriverFor("10.0.0.11");
        var driverB = DriverFor("10.0.0.12");
        driverA.SetInt32("D100", 11);
        driverB.SetInt32("D100", 22);

        Assert.Equal(11, adapterA.ReadInt32("D100").Content);
        Assert.Equal(22, adapterB.ReadInt32("D100").Content);
    }

    [Fact]
    public void RefreshingOneProfile_DoesNotReconfigureTheOtherDriver()
    {
        var lineA = _sessions.Get("line-a");
        var lineB = _sessions.Get("line-b");
        var driverA = DriverFor("10.0.0.11");
        var driverB = DriverFor("10.0.0.12");
        var lineBConfigureCount = driverB.ConfigureCallCount;

        _settings.FindConnectionProfile("line-a")!.Config.IpAddress = "10.0.0.21";
        _sessions.RefreshFromSettings();

        Assert.Equal("10.0.0.21", lineA.Profile.Config.IpAddress);
        Assert.Equal("10.0.0.12", lineB.Profile.Config.IpAddress);
        Assert.True(driverA.ConfigureCallCount > 0);
        Assert.Equal(lineBConfigureCount, driverB.ConfigureCallCount);
    }

    [Fact]
    public void RemovingOneProfile_DisposesOnlyItsDriver()
    {
        var lineA = _sessions.Get("line-a");
        var lineB = _sessions.Get("line-b");
        var driverA = DriverFor("10.0.0.11");
        var driverB = DriverFor("10.0.0.12");

        _settings.ConnectionProfiles.Remove(_settings.FindConnectionProfile("line-a")!);
        _sessions.RefreshFromSettings();

        Assert.Equal(1, driverA.DisconnectCallCount);
        Assert.Equal(0, driverB.DisconnectCallCount);
        Assert.Throws<InvalidOperationException>(() => _sessions.Get("line-a"));
        Assert.Same(lineB, _sessions.Get("line-b"));
    }

    [Fact]
    public void AcquisitionDiagnostics_AreIsolatedAndRecoverPerProfile()
    {
        _sessions.Get("line-a");
        _sessions.Get("line-b");

        _sessions.RecordAcquisitionResults(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "line-a" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "line-b" },
            noDevicesToRead: false,
            failureMessage: "line-b read failed");

        var first = _sessions.GetDiagnosticsSnapshot().ToDictionary(snapshot => snapshot.ProfileId);
        Assert.NotNull(first["line-a"].LastSuccessfulAcquisitionAt);
        Assert.Equal(0, first["line-a"].ConsecutiveAcquisitionFailures);
        Assert.Null(first["line-b"].LastSuccessfulAcquisitionAt);
        Assert.Equal(1, first["line-b"].ConsecutiveAcquisitionFailures);
        Assert.Equal(1, first["line-b"].AcquisitionFailureCount);
        Assert.Equal("line-b read failed", first["line-b"].LastAcquisitionFailureMessage);

        _sessions.RecordAcquisitionResults(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "line-a" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "line-a" },
            noDevicesToRead: false,
            failureMessage: "line-a scan failed");

        var scanFailure = _sessions.GetDiagnosticsSnapshot().Single(snapshot => snapshot.ProfileId == "line-a");
        Assert.NotNull(scanFailure.LastSuccessfulAcquisitionAt);
        Assert.Equal(1, scanFailure.ConsecutiveAcquisitionFailures);
        Assert.Equal(1, scanFailure.AcquisitionFailureCount);
        Assert.Equal("line-a scan failed", scanFailure.LastAcquisitionFailureMessage);

        _sessions.RecordAcquisitionResults(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "line-b" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            noDevicesToRead: false);

        var recovered = _sessions.GetDiagnosticsSnapshot().Single(snapshot => snapshot.ProfileId == "line-b");
        Assert.NotNull(recovered.LastSuccessfulAcquisitionAt);
        Assert.Equal(0, recovered.ConsecutiveAcquisitionFailures);
        Assert.Equal(1, recovered.AcquisitionFailureCount);
    }

    public void Dispose() => _sessions.Dispose();

    private FakePlcDriver DriverFor(string ipAddress)
        => _factory.Created.Single(entry => entry.Config.IpAddress == ipAddress).Driver;

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