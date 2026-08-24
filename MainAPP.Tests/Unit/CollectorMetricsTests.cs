using Kanban.Collector.Core.Services;
using Kanban.Collector.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class CollectorMetricsTests
{
    [Fact]
    public void Render_IncludesReaderMetricsWithProtocolLabelsAndHistogramBuckets()
    {
        CollectorMetrics.UpdateReaderDiagnostics([
            new DataSourceReaderDiagnosticsSnapshot
            {
                ProtocolKey = "simulated",
                ResolveCount = 2,
                ValidationCount = 3,
                ValidationSuccessCount = 2,
                ValidationFailureCount = 1,
                ReadCount = 2,
                ReadSuccessCount = 1,
                ReadFailureCount = 1,
                ReadDurationTotalMilliseconds = 7,
                ReadDurationBucketCounts = [1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0],
                ReadP95Milliseconds = 5,
                ReadP99Milliseconds = 10,
                AcknowledgementCount = 1,
                AcknowledgementSuccessCount = 1,
                AcknowledgementDurationTotalMilliseconds = 3,
                AcknowledgementDurationBucketCounts = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                AcknowledgementP95Milliseconds = 5,
                AcknowledgementP99Milliseconds = 5,
            },
        ]);

        var output = CollectorMetrics.Render();

        Assert.Contains("kanban_reader_resolve_total{protocol=\"simulated\"} 2", output);
        Assert.Contains("kanban_reader_validation_total{protocol=\"simulated\",result=\"failure\"} 1", output);
        Assert.Contains("kanban_reader_read_total{protocol=\"simulated\",result=\"success\"} 1", output);
        Assert.Contains("kanban_reader_read_duration_milliseconds_bucket{protocol=\"simulated\",le=\"10\"} 2", output);
        Assert.Contains("kanban_reader_read_duration_milliseconds_bucket{protocol=\"simulated\",le=\"+Inf\"} 2", output);
        Assert.Contains("kanban_reader_read_duration_milliseconds_sum{protocol=\"simulated\"} 7", output);
        Assert.Contains("kanban_reader_ack_duration_milliseconds_count{protocol=\"simulated\"} 1", output);
    }

    [Fact]
    public void Render_IncludesProfileHealthAndRemovesProfilesThatAreNoLongerActive()
    {
        CollectorMetrics.UpdateRuntimeSessionDiagnostics([
            new PlcRuntimeSessionDiagnosticsSnapshot
            {
                ProfileId = "line-a",
                ProtocolKey = "plc",
                Brand = Kanban.Collector.Core.Models.PlcBrand.Mitsubishi,
                IsConnected = false,
                ConsecutiveFailures = 3,
                TotalDisconnectCount = 2,
                DisconnectedAt = DateTime.Now.AddSeconds(-4),
                LastDisconnectDuration = TimeSpan.FromSeconds(7),
                LastSuccessfulAcquisitionAt = DateTime.Now.AddSeconds(-15),
                ConsecutiveAcquisitionFailures = 4,
                AcquisitionFailureCount = 9,
            },
        ]);

        var output = CollectorMetrics.Render();

        Assert.Contains("kanban_plc_profile_connected{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 0", output);
        Assert.Contains("kanban_plc_profile_consecutive_failures{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 3", output);
        Assert.Contains("kanban_plc_profile_disconnect_total{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 2", output);
        Assert.Contains("kanban_plc_profile_last_disconnect_seconds{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 7", output);
        Assert.Contains("kanban_plc_profile_acquisition_consecutive_failures{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 4", output);
        Assert.Contains("kanban_plc_profile_acquisition_failure_total{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} 9", output);
        Assert.Contains("kanban_plc_profile_last_successful_acquisition_timestamp_seconds{profile=\"line-a\",protocol=\"plc\",brand=\"Mitsubishi\"} ", output);

        CollectorMetrics.UpdateRuntimeSessionDiagnostics([
            new PlcRuntimeSessionDiagnosticsSnapshot
            {
                ProfileId = "line-b",
                ProtocolKey = "plc",
                Brand = Kanban.Collector.Core.Models.PlcBrand.Siemens,
            },
        ]);

        output = CollectorMetrics.Render();
        Assert.DoesNotContain("profile=\"line-a\"", output);
        Assert.Contains("profile=\"line-b\"", output);
    }
}
