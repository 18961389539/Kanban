using Kanban.ComponentModel;

namespace Kanban.Collector.Core.Models;

/// <summary>命名的设备连接配置。运行时会话由连接档案 ID 选择；当前阶段仍使用单一活动 PLC 会话。</summary>
public partial class ConnectionProfile : ObservableObject
{
    public const string DefaultId = "default";
    public const string DefaultName = "Default connection";

    private string _id = DefaultId;

    public string Id
    {
        get => _id;
    set => SetProperty(ref _id, value);
    }

    private string _name = DefaultName;

    public string Name
    {
        get => _name;
    set => SetProperty(ref _name, value);
    }

    private PlcConfig _config = new();

    public PlcConfig Config
    {
        get => _config;
    set => SetProperty(ref _config, value);
    }

    public ConnectionProfile CreateSnapshot() => new()
    {
        Id = Id,
        Name = Name,
        Config = Config.CreateSnapshot(),
    };
}