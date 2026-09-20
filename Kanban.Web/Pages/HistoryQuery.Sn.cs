using System.Text;
using Kanban.Contracts.Dtos;
using Kanban.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kanban.Web.Pages;

/// <summary>历史查询页 Tab 4：SN 精确追溯（对齐 WPF SnQueryViewModel）。</summary>
public partial class HistoryQuery
{
    private string SnInput { get; set; } = "";
    private bool SnHasQueried { get; set; }
    private bool SnIsLoading { get; set; }
    private string? SnError { get; set; }
    private List<SnEventRecordDto> SnRows { get; set; } = [];

    private async Task SnSearchAsync()
    {
        ValidationMessage = null;
        SnError = null;
        var sn = SnInput.Trim();
        if (string.IsNullOrEmpty(sn))
        {
            ValidationMessage = L.T("K921");
            return;
        }

        SnIsLoading = true;
        SnHasQueried = true;
        try
        {
            var response = await Dashboard.QuerySnEventsAsync(new SnEventQueryRequest
            {
                Sn = sn,
                Page = 1,
                PageSize = 200,
            });
            SnRows = [.. response.Items];
            if (SnRows.Count == 0)
                SnError = L.T("K922", sn);
        }
        catch (Exception)
        {
            SnRows = [];
            SnError = L.T("Hq_QueryFailed");
        }
        finally
        {
            SnIsLoading = false;
        }
    }

    private async Task SnExportCsvAsync()
    {
        if (SnRows.Count == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',',
            C(L.T("Hq_SnCol")), C(L.T("Hq_Time")), C(L.T("Csv_DeviceId")), C(L.T("Csv_DeviceName")),
            C(L.T("Hq_SnWorkOrder")), C(L.T("Hq_Shift")), C(L.T("K924")), C(L.T("Hq_SnSource"))));
        foreach (var r in SnRows)
        {
            sb.AppendLine(string.Join(',',
                C(r.Sn),
                C(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                C(r.DeviceId),
                C(r.DeviceName),
                C(r.WorkOrderId?.ToString() ?? ""),
                C(r.ShiftName),
                C(r.Result == 0 ? L.T("Lbl_Ok") : L.T("Lbl_Ng")),
                C(r.Source)));
        }

        var fileName = L.T("Csv_FileSn", SnInput.Trim());
        await JS.InvokeVoidAsync("KanbanECharts.download", fileName, "\uFEFF" + sb, "text/csv;charset=utf-8");
    }

    private void ResetSn()
    {
        SnInput = "";
        SnHasQueried = false;
        SnIsLoading = false;
        SnError = null;
        SnRows = [];
    }

    private void OnSnInput(ChangeEventArgs e) => SnInput = e.Value?.ToString() ?? "";

    private async Task OnSnKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SnSearchAsync();
    }
}
