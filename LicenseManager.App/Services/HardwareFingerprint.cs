using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LicenseManager.Crypto;
using Microsoft.Win32;

namespace LicenseManager.Services;

/// <summary>
/// 机器码生成：组合 CPU + 主板 + 主硬盘序列号，SHA256 后截断 5 字节，Base32 编码为 8 字符。
/// </summary>
/// <remarks>
/// 缓存机制（P2-1）：
/// - 进程级缓存：Lazy&lt;byte[]&gt; 保证进程内只计算一次
/// - 持久化缓存：首次计算后用 DPAPI 加密写入 %AppData%\Kanban\hwid.dat，后续启动直接读取
/// - DPAPI 加密绑定当前 Windows 用户，复制 hwid.dat 到其他机器无法解密
/// - 缓存优先策略：WMI 偶发失败时回退到缓存，避免机器码变化导致已激活用户被踢出
/// - 缓存文件被删除（如重装系统）→ 重新查 WMI 计算
///
/// 单一硬件变化（如换硬盘）不会立即导致失效，因为 CPU 和主板序列号保持不变。
/// 完全重装系统或更换主板/CPU 才需要重新激活。
/// </remarks>
[Obfuscation(Exclude = false, ApplyToMembers = true)]
public static class HardwareFingerprint
{
    private const string CacheFileName = "hwid.dat";

    /// <summary>
    /// 进程级缓存（Lazy 保证线程安全的延迟初始化）。
    /// 首次访问时触发 LoadOrComputeHash，后续直接返回缓存值。
    /// </summary>
    private static readonly Lazy<byte[]> _hashLazy = new(LoadOrComputeHash);

    /// <summary>获取当前机器码（8 字符，Base32 编码）。</summary>
    public static string GetMachineCode()
    {
        return Base32.Encode(_hashLazy.Value);
    }

    /// <summary>获取机器码哈希（5 字节，用于激活码绑定）。</summary>
    public static byte[] GetMachineCodeHash()
    {
        return _hashLazy.Value;
    }

    /// <summary>
    /// 加载或计算机器码哈希。
    /// 优先从持久化缓存加载；缓存不存在时查 WMI 计算并写入缓存。
    /// </summary>
    private static byte[] LoadOrComputeHash()
    {
        // 1. 尝试从持久化缓存加载（WMI 偶发失败时的回退依据）
        var cached = LoadFromCache();
        if (cached != null && cached.Length == 5) return cached;

        // 2. 查询 WMI 计算哈希
        var hash = ComputeHashFromWmi();

        // 3. 写入持久化缓存（DPAPI 加密，防止跨机器复制）
        SaveToCache(hash);

        return hash;
    }

    /// <summary>从 WMI 查询硬件信息并计算哈希。</summary>
    private static byte[] ComputeHashFromWmi()
    {
        var cpu = GetCpuId();
        var board = GetBaseBoardSerial();
        var disk = GetDiskSerial();
        var raw = $"{cpu}|{board}|{disk}";

        // 三项 WMI 全部为空时，此前会退化为 SHA256("||") 固定值，导致同类机器机器码完全相同、授权绑定失效。
        // 兜底改用 MachineGuid（重装系统前稳定）与网卡 MAC 组合；仍无法取得任何标识时才抛异常，
        // 避免把退化指纹写入持久化缓存（否则已激活用户全部失效）。
        if (string.IsNullOrEmpty(cpu) && string.IsNullOrEmpty(board) && string.IsNullOrEmpty(disk))
        {
            var machineGuid = GetMachineGuid();
            var mac = GetMacAddress();
            if (string.IsNullOrEmpty(machineGuid) && string.IsNullOrEmpty(mac))
            {
                throw new InvalidOperationException(
                    "无法采集任何硬件标识（CPU/主板/磁盘/MachineGuid/MAC 均失败），无法生成机器码。");
            }
            raw = $"fallback|{machineGuid}|{mac}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return hash.AsSpan(0, 5).ToArray();
    }

    /// <summary>Windows 安装级 MachineGuid（重装系统前稳定；来自注册表 Cryptography 项）。</summary>
    private static string GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>第一块启用状态非回环网卡的物理地址（十六进制串，无分隔符）。</summary>
    private static string GetMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            return nic?.GetPhysicalAddress()?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ──────────── 持久化缓存（DPAPI 加密）────────────

    private static string GetCacheFilePath()
    {
        return Path.Combine(LicensePaths.ResolveDataDirectory(), CacheFileName);
    }

    /// <summary>从缓存文件加载哈希（DPAPI 解密）。失败返回 null。</summary>
    private static byte[]? LoadFromCache()
    {
        try
        {
            var path = GetCacheFilePath();
            if (!File.Exists(path)) return null;
            var encrypted = File.ReadAllBytes(path);
            return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>将哈希写入缓存文件（DPAPI 加密）。失败静默忽略。</summary>
    private static void SaveToCache(byte[] hash)
    {
        try
        {
            var encrypted = ProtectedData.Protect(hash, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(GetCacheFilePath(), encrypted);
        }
        catch
        {
            // 缓存写入失败不影响功能（下次启动会重新查 WMI）
        }
    }

    // ──────────── WMI 查询 ────────────

    private static string GetCpuId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var id = obj["ProcessorId"]?.ToString();
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch { /* WMI 失败时退化为空字符串 */ }
        return string.Empty;
    }

    private static string GetBaseBoardSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
            foreach (var obj in searcher.Get())
            {
                var sn = obj["SerialNumber"]?.ToString();
                if (!string.IsNullOrEmpty(sn)) return sn;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string GetDiskSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0");
            foreach (var obj in searcher.Get())
            {
                var sn = obj["SerialNumber"]?.ToString();
                if (!string.IsNullOrEmpty(sn)) return sn;
            }
        }
        catch { }
        return string.Empty;
    }
}
