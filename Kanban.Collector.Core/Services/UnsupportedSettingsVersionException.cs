namespace Kanban.Collector.Core.Services;

internal sealed class UnsupportedSettingsVersionException(int version, int currentVersion)
    : InvalidOperationException($"settings.json 版本 {version} 高于当前支持版本 {currentVersion}。")
{
    public int Version { get; } = version;
    public int CurrentVersion { get; } = currentVersion;
}
