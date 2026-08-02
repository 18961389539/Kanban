using System.Reflection;
using System.Runtime.CompilerServices;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IDisposable 契约测试：扫描 MainAPP 程序集中所有实现 <see cref="IDisposable"/> 的非抽象类，
/// 验证以下契约：
///
/// 1. <b>公共 Dispose 方法存在</b>：实现 IDisposable 的类必须有 <c>public void Dispose()</c>
///    （接口契约编译期保证，但反射验证防止显式接口实现导致公开 API 缺失）。
/// 2. <b>Dispose 幂等性</b>：对有公共无参构造的 IDisposable 类，实例化后连续调用 Dispose() 两次不抛异常。
///    项目约定中多处提到 Dispose 应检查 _disposed flag 并安全返回（如 HslPlcDriver）。
///
/// 此测试是 <see cref="ViewModelDisposableContractTests"/> 的补充：
/// ViewModelDisposableContractTests 专注 ViewModel 事件泄漏，本测试覆盖所有 IDisposable 实现的通用契约。
/// </summary>
[Trait("Category", "Contract")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class IDisposableContractTests
{
    /// <summary>MainAPP 业务程序集（通过已知类型定位）。</summary>
    private static readonly Assembly s_mainAppAssembly = typeof(IPlcDriver).Assembly;

    /// <summary>
    /// 所有实现 IDisposable 的非抽象公共类。
    /// 排除：接口、抽象类、泛型类型（泛型类型实例化需要类型参数）。
    /// </summary>
    public static TheoryData<Type> DisposableTypes
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var type in s_mainAppAssembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                if (!typeof(IDisposable).IsAssignableFrom(type)) continue;
                // 跳过编译器生成的匿名类型和委托
                if (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null) continue;
                // 跳过 WPF 控件/窗口（需要 STA 线程实例化，不在单元测试范围）
                if (type.FullName?.Contains(".Views.") == true) continue;
                if (type.FullName?.Contains(".Controls.") == true) continue;
                data.Add(type);
            }
            return data;
        }
    }

    // ──────────── 公共 Dispose 方法契约 ────────────

    [Theory]
    [MemberData(nameof(DisposableTypes))]
    public void HasPublicDisposeMethod(Type type)
    {
        var disposeMethod = type.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
        Assert.True(disposeMethod is not null,
            $"{type.FullName} 实现了 IDisposable 但缺少 public Dispose() 方法。" +
            "如果是显式接口实现，请添加 public Dispose() 包装方法以便调用方直接使用。");
    }

    // ──────────── Dispose 幂等性契约（仅测试可无参实例化的类） ────────────

    /// <summary>
    /// 对有公共无参构造的 IDisposable 类，验证 Dispose() 连续调用两次不抛异常。
    /// 项目约定要求 Dispose 检查 _disposed flag 并安全返回（如 HslPlcDriver）。
    /// </summary>
    [Fact]
    public void DisposableTypes_WithParameterlessConstructor_DisposeIsIdempotent()
    {
        var failures = new List<string>();
        int tested = 0;

        foreach (var type in GetInstantiableDisposableTypes())
        {
            try
            {
                var instance = Activator.CreateInstance(type);
                Assert.NotNull(instance);
                var disposeMethod = type.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
                if (disposeMethod is null) continue;

                disposeMethod.Invoke(instance, null);  // 第一次 Dispose
                disposeMethod.Invoke(instance, null);  // 第二次 Dispose（应不抛异常）
                tested++;
            }
            catch (TargetInvocationException ex)
            {
                // 解包 TargetInvocationException 获取真实异常
                failures.Add($"{type.FullName}: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"{type.FullName}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"以下 {failures.Count} 个 IDisposable 类的 Dispose() 重复调用抛出异常（应幂等）：\n  " +
            string.Join("\n  ", failures) +
            $"\n（共测试 {tested} 个可实例化的 IDisposable 类）");
    }

    /// <summary>获取有公共无参构造的 IDisposable 类，用于实例化测试。</summary>
    private static List<Type> GetInstantiableDisposableTypes()
    {
        var result = new List<Type>();
        foreach (var type in s_mainAppAssembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;
            if (!typeof(IDisposable).IsAssignableFrom(type)) continue;
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null) continue;
            if (type.FullName?.Contains(".Views.") == true) continue;
            if (type.FullName?.Contains(".Controls.") == true) continue;
            // 只测试有公共无参构造的类
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            result.Add(type);
        }
        return result;
    }
}
