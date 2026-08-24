using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanban.Collector.Core.Models;

/// <summary>命名的设备连接配置。运行时会话由连接档案 ID 选择；当前阶段仍使用单一活动 PLC 会话。</summary>
public partial class ConnectionProfile : ObservableObject
{
    public const string DefaultId = "default";
    public const string DefaultName = "Default connection";

    [ObservableProperty]
    private string _id = DefaultId;

    [ObservableProperty]
    private string _name = DefaultName;

    [ObservableProperty]
    private PlcConfig _config = new();

    public ConnectionProfile CreateSnapshot() => new()
    {
        Id = Id,
        Name = Name,
        Config = Config.CreateSnapshot(),
    };
}