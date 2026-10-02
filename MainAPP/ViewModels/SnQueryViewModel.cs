using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// SN 追溯查询（历史查询页「SN 追溯」Tab）：
/// 输入序列号 → 反查该件的设备/工单/班次/时间/判定结果。
/// 最小版：仅本地采集模式（Local）支持；Remote 模式（独立 Collector）需 V2 补 SignalR 查询通道。
/// </summary>
public partial class SnQueryViewModel : ObservableObject
{
    private readonly ISnEventStore? _snEventStore;

    /// <summary>序列号输入。</summary>
    [ObservableProperty]
    private string _snInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SnEventRecord> _results = new();

    [ObservableProperty]
    private bool _hasQueried;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _queryError;

    /// <summary>SN 追溯是否可用：注入的存储非空即可用（Local=本地库，Remote=SignalR 查询通道）。</summary>
    public bool IsSupported => _snEventStore != null;

    public bool IsEmptyResult => HasQueried && Results.Count == 0 && string.IsNullOrEmpty(QueryError);

    public SnQueryViewModel(ISnEventStore? snEventStore = null)
    {
        _snEventStore = snEventStore;
    }

    partial void OnQueryErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmptyResult));

    [RelayCommand]
    private async Task SearchAsync()
    {
        var sn = SnInput.Trim();
        QueryError = null;
        if (string.IsNullOrEmpty(sn))
        {
            QueryError = MainAPP.Resources.Strings.Lbl_PleaseEnterSNTrace;
            return;
        }

        IsLoading = true;
        HasQueried = true;
        try
        {
            var list = await Task.Run(() => _snEventStore?.QueryBySn(sn) ?? []);
            Results.Clear();
            foreach (var record in list)
                Results.Add(record);
            if (list.Count == 0)
                QueryError = string.Format(MainAPP.Resources.Strings.Lbl_NoTraceRecordsFoundSN, sn);
            Kanban.Collector.Core.Services.AuditLog.Record(
                "History.Query", "History", sn, detail: string.Format(MainAPP.Resources.Strings.Audit_Detail_SnQuery, sn, list.Count));
        }
        catch (Exception ex)
        {
            Results.Clear();
            QueryError = string.Format(MainAPP.Resources.Strings.Lbl_QueryFailed, ex.Message);
            Kanban.Collector.Core.Services.AuditLog.Record(
                "History.Query", "History", sn, succeeded: false, detail: QueryError);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Reset()
    {
        SnInput = string.Empty;
        Results.Clear();
        HasQueried = false;
        QueryError = null;
    }
}
