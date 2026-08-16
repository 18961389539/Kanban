using System.Runtime.CompilerServices;
using LicenseManager.Crypto;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 测试模块初始化：注入固定 HMAC 密钥到环境变量，供 License 相关测试使用。
/// 修复 #2 后，生产代码已移除内嵌密钥回退，任何签发/验签都要求 KANBAN_HMAC_KEY 环境变量，
/// 因此测试必须在模块加载早期（任何测试访问 EmbeddedKey.HmacKey 的 Lazy 缓存之前）注入。
/// </summary>
public static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // 32 字节确定性测试密钥（Base64），仅用于测试，非生产密钥。
        var testKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, testKey);
    }
}
