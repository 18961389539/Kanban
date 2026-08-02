using System.Text.Json.Serialization;

namespace LicenseManager.Models;

/// <summary>
/// 试用期跟踪状态：持久化到本地，用于检测时间回拨和计算剩余天数。
/// 文件本身由 <see cref="LicenseManager.Services.LicenseStore"/> 用 DPAPI 加密 + HMAC 签名保护，防篡改。
/// </summary>
public class TrialState
{
    /// <summary>首次启动时间（UTC）。试用期 30 天从此刻开始计算。</summary>
    public DateTime FirstLaunchUtc { get; set; }

    /// <summary>上次启动时间（UTC）。用于检测时间回拨（当前时间不应早于上次启动时间）。</summary>
    public DateTime LastLaunchUtc { get; set; }

    /// <summary>上次记录的系统启动时间（Environment.TickCount64，毫秒）。
    /// 用于检测系统时间被回拨（系统启动时间不会倒退）。</summary>
    public long LastSystemUptimeMs { get; set; }

    /// <summary>累计启动次数（仅用于统计，不参与授权判断）</summary>
    public int LaunchCount { get; set; }
}
