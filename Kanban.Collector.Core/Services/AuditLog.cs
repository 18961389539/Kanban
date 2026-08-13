namespace Kanban.Core.Services;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 操作审计静态门面：让任意业务代码一行调用即可记录审计（自动携带当前操作人），
/// 避免给所有 ViewModel/Service 追加构造参数。行为与项目现有静态 Serilog Log 用法一致。
/// - 未初始化（测试/未接入进程）时调用静默忽略，绝不抛异常、绝不干扰业务。
/// - Initialize 由宿主进程在启动早期调用一次（MainAPP 在用户登录前，Collector 在服务初始化时）。
/// </summary>
public static class AuditLog
{
    private static IAuditService? _service;
    private static Func<string>? _operatorProvider;
    private static readonly object Sync = new();

    /// <summary>前后值 JSON 序列化选项：忽略 null 属性、紧凑输出、驼峰命名与实体 JSON 风格一致。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>单字段 JSON 摘要的硬上限（与数据库列 MaxLength 对齐）。</summary>
    private const int MaxPreviewChars = 4000;

    /// <summary>
    /// 初始化门面（幂等，重复调用以最后一次为准）。
    /// <paramref name="operatorProvider"/>：返回当前操作人显示名的委托（MainAPP 传入 UserSession.CurrentUserDisplay）。
    /// </summary>
    public static void Initialize(IAuditService? service, Func<string>? operatorProvider = null)
    {
        lock (Sync)
        {
            _service = service;
            _operatorProvider = operatorProvider;
        }
    }

    /// <summary>测试用：清空门面状态，回到静默 Noop（internal：仅测试程序集经 InternalsVisibleTo 可见，不进公开 API 面）。</summary>
    internal static void ResetForTest()
    {
        lock (Sync)
        {
            _service = null;
            _operatorProvider = null;
        }
    }

    /// <summary>记录一条审计事件（未初始化时静默忽略）。</summary>
    public static void Record(string action, string? targetType = null, string? targetId = null,
        bool succeeded = true, string? detail = null,
        object? before = null, object? after = null)
    {
        IAuditService? service;
        Func<string>? provider;
        lock (Sync)
        {
            service = _service;
            provider = _operatorProvider;
        }
        if (service is null) return;

        string operatorName;
        try { operatorName = provider?.Invoke() ?? string.Empty; }
        catch { operatorName = string.Empty; }

        try
        {
            service.Record(action, targetType, targetId, succeeded, detail, operatorName,
                beforeJson: Serialize(before), afterJson: Serialize(after));
        }
        catch
        {
            // 审计失败不影响业务主流程（与日志框架的容错原则一致）
        }
    }

    /// <summary>
    /// 对象 → JSON 摘要；null/序列化失败返回 null。调用方应传匿名对象（只含关键字段，避免敏感字段落库）。
    /// 超出 <see cref="MaxPreviewChars"/> 时不会直接截断 JSON（那会产生非法 JSON），
    /// 而是改写为固定结构的「截断预览」，保证归档/查询侧始终能解析。
    /// </summary>
    private static string? Serialize(object? value)
    {
        if (value is null) return null;
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            if (json.Length <= MaxPreviewChars) return json;

            var sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
            return JsonSerializer.Serialize(new
            {
                truncated = true,
                preview = json[..Math.Min(json.Length, MaxPreviewChars)],
                sha256,
            }, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
