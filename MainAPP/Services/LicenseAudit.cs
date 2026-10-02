using Kanban.Collector.Core.Services;
using LicenseManager.Services;
using LicenseManager.ViewModels;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>把激活成功和激活码被拒绝记进审计。不记录激活码本身。</summary>
internal static class LicenseAudit
{
    public static void Watch(ActivationViewModel viewModel, LicenseGate gate)
    {
        viewModel.ActivationSucceeded += () => Record(gate, succeeded: true, error: null);
        viewModel.ActivationFailed += () => Record(gate, succeeded: false, error: viewModel.ErrorMessage);
    }

    private static void Record(LicenseGate gate, bool succeeded, string? error)
    {
        var license = gate.CurrentLicense;
        var detail = succeeded
            ? license?.IsPermanent == true
                ? Strings.M308
                : license?.ExpireDate?.ToLocalTime().ToString("yyyy-MM-dd")
            : error;
        if (detail is { Length: > 400 })
            detail = detail[..400];
        AuditLog.Record("License.Activate", "License", null, succeeded, detail);
    }
}
