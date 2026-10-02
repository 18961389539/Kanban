using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

public interface IAssistantQueryEngine
{
    string Answer(DateTime now, string question, string? selectedDeviceName, CancellationToken cancellationToken);
}

/// <summary>按解析出的时间窗查历史，回答由程序写成固定句子。不启动本地模型。</summary>
public sealed class AssistantQueryEngine : IAssistantQueryEngine
{
    private readonly IProductionHistoryReader _production;
    private readonly IStatusTransitionHistoryService _status;
    private readonly DeviceRepository _devices;
    private readonly AppSettings _settings;
    private readonly ILogger<AssistantQueryEngine> _logger;

    public AssistantQueryEngine(
        IProductionHistoryReader production,
        IStatusTransitionHistoryService status,
        DeviceRepository devices,
        AppSettings settings,
        ILogger<AssistantQueryEngine> logger)
    {
        _production = production;
        _status = status;
        _devices = devices;
        _settings = settings;
        _logger = logger;
    }

    public string Answer(DateTime now, string question, string? selectedDeviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = _devices.GetDevicesSnapshot();
        var names = devices
            .Select(device => device.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToList();
        if (!AssistantQuery.TryParse(question, now, names, selectedDeviceName, out var ask))
            return Strings.Assistant_Unrecognized;

        var shifts = _settings.GetShiftsSnapshot();
        var windows = AssistantQuery.ResolveWindows(ask, now, shifts, AssistantQuery.RetentionDays());
        var open = windows.Where(window => window.Problem == null).ToList();
        Dictionary<string, List<ProductionLog>>? production = null;
        Dictionary<string, List<StatusTransitionRecord>>? transitions = null;
        var productionFailed = false;
        var statusFailed = false;
        if (open.Count > 0)
        {
            var from = open.Min(window => window.From).AddDays(-1);
            var to = open.Max(window => window.To);
            var ids = devices.Select(device => device.Id).ToList();
            if (ask.Pieces)
                productionFailed = !TryLoad(ids, from, to, out production);
            if (ask.AlarmDuration)
                statusFailed = !TryLoadStatus(ids, open.Min(window => window.From), to, out transitions);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var text = AssistantQuery.Render(
            ask,
            now,
            devices,
            selectedDeviceName,
            windows,
            production,
            productionFailed,
            transitions,
            statusFailed);
        return string.IsNullOrWhiteSpace(text) ? Strings.Assistant_Unrecognized : text;
    }

    private bool TryLoad(List<string> ids, DateTime from, DateTime to, out Dictionary<string, List<ProductionLog>>? production)
    {
        try
        {
            production = ids.Count == 0 ? [] : _production.QueryProductionLogsBatchStrict(from, to, ids);
            return true;
        }
        catch (Exception ex)
        {
            production = null;
            _logger.LogWarning(ex, "AI 问答按时间窗读取产量失败");
            return false;
        }
    }

    private bool TryLoadStatus(
        List<string> ids,
        DateTime from,
        DateTime to,
        out Dictionary<string, List<StatusTransitionRecord>>? transitions)
    {
        try
        {
            transitions = ids.Count == 0 ? [] : _status.QueryStatusTransitionsBatchStrict(from, to, ids);
            return true;
        }
        catch (Exception ex)
        {
            transitions = null;
            _logger.LogWarning(ex, "AI 问答按时间窗读取报警时长失败");
            return false;
        }
    }
}
