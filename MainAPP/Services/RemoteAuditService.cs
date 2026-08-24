using System.Threading.Channels;
using Kanban.Client;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

/// <summary>
/// Remote 模式审计适配器：查询走监控连接，写入走管理连接，MainAPP 不创建或写本地 audit_logs.db。
/// 启动早期管理连接尚未建立时先进入有界内存队列，连接恢复后再发送到 Collector。
/// </summary>
public sealed class RemoteAuditService : IAuditService, IAsyncDisposable
{
    private const int QueueCapacity = 2048;
    private readonly KanbanDataClient _monitorClient;
    private readonly KanbanAdminClient _adminClient;
    private readonly ILogger<RemoteAuditService> _logger;
    private readonly Channel<AuditLogRecordRequest> _pending;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _writerTask;
    private int _droppedCount;
    private int _disposeStarted;

    public RemoteAuditService(
        KanbanDataClient monitorClient,
        KanbanAdminClient adminClient,
        ILogger<RemoteAuditService> logger)
    {
        _monitorClient = monitorClient;
        _adminClient = adminClient;
        _logger = logger;
        _pending = Channel.CreateBounded<AuditLogRecordRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(() => WriteLoopAsync(_shutdownCts.Token));
    }

    public int DroppedCount => Volatile.Read(ref _droppedCount);

    public void Record(
        string action,
        string? targetType = null,
        string? targetId = null,
        bool succeeded = true,
        string? detail = null,
        string? operatorName = null,
        string? beforeJson = null,
        string? afterJson = null)
    {
        // Remote 操作人的可信来源在 Collector 管理 Hub；此处不把本地 operator 写入本地库，
        // operatorName 仅作为兼容参数保留，不能覆盖服务端主体。
        var request = new AuditLogRecordRequest
        {
            Action = action ?? string.Empty,
            TargetType = targetType,
            TargetId = targetId,
            Succeeded = succeeded,
            Detail = detail,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
        };
        if (!_pending.Writer.TryWrite(request))
        {
            var dropped = Interlocked.Increment(ref _droppedCount);
            if (dropped == 1 || dropped % 100 == 0)
                _logger.LogWarning("Remote 审计待发送队列已丢弃 {Dropped} 条记录", dropped);
        }
    }

    public (List<AuditEntry> Items, int Total) QueryPaged(
        DateTime from,
        DateTime to,
        string? operatorName,
        string? action,
        string? targetType,
        bool? succeeded,
        int page,
        int pageSize)
    {
        var response = _monitorClient.QueryAuditLogsAsync(new AuditLogQueryRequest
        {
            From = from,
            To = to,
            Operator = operatorName,
            Action = action,
            TargetType = targetType,
            Succeeded = succeeded,
            Page = page,
            PageSize = pageSize,
        }).ConfigureAwait(false).GetAwaiter().GetResult();
        return (response.Items.Select(ToEntity).ToList(), response.Total);
    }

    public (List<AuditEntry> Items, int Total) QueryAll(
        DateTime from,
        DateTime to,
        string? operatorName,
        string? action,
        string? targetType,
        bool? succeeded,
        int maxResults = 10000)
    {
        var response = _monitorClient.QueryAuditLogsAsync(new AuditLogQueryRequest
        {
            From = from,
            To = to,
            Operator = operatorName,
            Action = action,
            TargetType = targetType,
            Succeeded = succeeded,
            Page = 1,
            PageSize = Math.Clamp(maxResults, 1, 100000),
        }).ConfigureAwait(false).GetAwaiter().GetResult();
        return (response.Items.Select(ToEntity).ToList(), response.Total);
    }

    /// <summary>Remote 审计保留期由 Collector 唯一负责，展示端不执行本地清理。</summary>
    public int CleanupOldEntries(int retentionDays = 30) => 0;

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _pending.Reader.ReadAllAsync(cancellationToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!_adminClient.IsConnected)
                            await _adminClient.ConnectAsync(cancellationToken);
                        await _adminClient.RecordAuditAsync(request, cancellationToken);
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Remote 审计发送失败，2 秒后重试");
                        try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static AuditEntry ToEntity(AuditLogEntryDto dto)
        => new()
        {
            Id = dto.Id,
            Timestamp = dto.Timestamp,
            Operator = dto.Operator,
            Action = dto.Action,
            TargetType = dto.TargetType,
            TargetId = dto.TargetId,
            Succeeded = dto.Succeeded,
            Detail = dto.Detail,
            BeforeJson = dto.BeforeJson,
            AfterJson = dto.AfterJson,
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _pending.Writer.TryComplete();
        try { await _writerTask.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { _logger.LogWarning("等待 Remote 审计队列排空超时"); }
        finally
        {
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
        }
    }
}
