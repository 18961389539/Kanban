using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>可取消的生产历史读取能力，供 Remote 工单汇总等网络调用使用。</summary>
public interface IAsyncProductionHistoryReader
{
    Task<List<ProductionLog>> QueryProductionLogsAsync(
        DateTime from,
        DateTime to,
        string? deviceId = null,
        string? shiftName = null,
        CancellationToken cancellationToken = default);

    Task<List<ProductionLog>> QueryProductionLogsByWorkOrderAsync(
        int workOrderId,
        CancellationToken cancellationToken = default);

    Task<ProductionLog?> GetLatestProductionBeforeAsync(
        string deviceId,
        DateTime before,
        string shiftName,
        CancellationToken cancellationToken = default);
}