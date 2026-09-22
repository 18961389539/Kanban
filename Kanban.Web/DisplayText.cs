using Kanban.Contracts.Text;

namespace Kanban.Web;

/// <summary>屏端展示层乱码还原，委托给共享修复表。</summary>
internal static class DisplayText
{
    public static string Repair(string? value) => GbkMojibake.Repair(value);
}
