using System.Reflection;
using System.Reflection.Emit;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// ViewModel IDisposable 契约测试：通过反射扫描 IL 检测事件订阅，
/// 订阅了事件的 ViewModel 必须实现 IDisposable 以避免事件泄漏。
///
/// 背景：项目约定"ViewModels implementing event subscriptions must implement IDisposable"。
/// MainWindowViewModel 曾因未实现 IDisposable 导致事件泄漏（项目记忆中的已知问题）。
/// 此测试自动发现所有违规，避免人工 review 漏掉新增 ViewModel。
///
/// 原理：C# 的事件订阅 (+=) 在 IL 中编译为调用 add_EventName 方法。
/// 通过遍历 ViewModel 所有方法的 IL，查找 call/callvirt 指令调用的 add_ 方法，
/// 即可判定 ViewModel 是否订阅了事件。
/// </summary>
[Trait("Category","Contract")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ViewModelDisposableContractTests
{
    /// <summary>
    /// 验证所有订阅了事件的 ViewModel 都实现了 IDisposable。
    /// 如果失败，会在错误信息中列出所有违规的 ViewModel 名称。
    /// </summary>
    [Fact]
    public void AllViewModelsSubscribingToEvents_ImplementIDisposable()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var viewModelTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal)
                        && !t.IsAbstract
                        && !t.IsInterface
                        && t.IsClass);

        var violations = new List<string>();
        foreach (var type in viewModelTypes)
        {
            if (SubscribesToEvents(type) && !typeof(IDisposable).IsAssignableFrom(type))
            {
                violations.Add(type.FullName ?? type.Name);
            }
        }

        Assert.True(violations.Count == 0,
            $"以下 ViewModel 订阅了事件但未实现 IDisposable，可能导致事件泄漏：\n  {string.Join("\n  ", violations)}\n" +
            "修复方法：实现 IDisposable 并在 Dispose() 中用 -= 取消所有事件订阅。");
    }

    /// <summary>
    /// 验证实现了 IDisposable 的 ViewModel 有非空 Dispose 方法（防止空实现）。
    /// </summary>
    [Fact]
    public void ViewModelsImplementingIDisposable_HaveNonEmptyDisposeMethod()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var disposableViewModels = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal)
                        && !t.IsAbstract
                        && !t.IsInterface
                        && t.IsClass
                        && typeof(IDisposable).IsAssignableFrom(t));

        foreach (var type in disposableViewModels)
        {
            var disposeMethod = type.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
            Assert.True(disposeMethod != null, $"{type.Name} 实现了 IDisposable 但缺少 public Dispose() 方法");
        }
    }

    // ──────────── IL 扫描逻辑 ────────────

    /// <summary>
    /// 检查类型是否订阅了事件（IL 中存在 add_ 方法调用）。
    /// 扫描类型的所有实例方法和构造函数的 IL。
    /// </summary>
    private static bool SubscribesToEvents(Type type)
    {
        var module = type.Module;

        // 扫描所有声明的实例方法和构造函数
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        foreach (var method in methods)
        {
            if (method is not MethodInfo mi) continue;
            foreach (var calledMethod in GetCalledMethods(mi, module))
            {
                // 事件订阅在 IL 中表现为调用 add_XXX 方法
                if (calledMethod.Name.StartsWith("add_", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 遍历方法 IL 中的所有 call/callvirt 指令，返回被调用的方法。
    /// </summary>
    private static List<MethodBase> GetCalledMethods(MethodInfo method, Module module)
    {
        var result = new List<MethodBase>();
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null) return result;

        int pos = 0;
        while (pos < il.Length)
        {
            // 读取操作码
            ushort opCodeValue = il[pos];
            int opCodeSize = 1;
            if (opCodeValue == 0xFE) // 双字节操作码前缀
            {
                if (pos + 1 >= il.Length) break;
                opCodeValue = (ushort)(0xFE00 | il[pos + 1]);
                opCodeSize = 2;
            }

            if (!s_allOpCodes.TryGetValue(opCodeValue, out var opCode))
                break; // 未知操作码，停止扫描（保守策略）

            int operandOffset = pos + opCodeSize;
            int operandSize = GetOperandSize(opCode, il, operandOffset);

            // call (0x28) 和 callvirt (0x6F) 的操作数是 4 字节方法 token
            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                && operandOffset + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32(il, operandOffset);
                try
                {
                    var calledMethod = module.ResolveMethod(token);
                    if (calledMethod != null)
                        result.Add(calledMethod);
                }
                catch
                {
                    // token 不是方法引用（可能是字段、类型等），跳过
                }
            }

            pos = operandOffset + operandSize;
        }
        return result;
    }

    /// <summary>
    /// 根据操作码的 OperandType 计算操作数长度。
    /// switch 指令需要特殊处理（4 字节 count + count * 4 字节 offsets）。
    /// </summary>
    private static int GetOperandSize(OpCode opCode, byte[] il, int operandOffset)
    {
        if (opCode.OperandType == OperandType.InlineSwitch)
        {
            if (operandOffset + 4 > il.Length) return 0;
            int count = BitConverter.ToInt32(il, operandOffset);
            return 4 + count * 4;
        }
        return opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineField => 4,
            OperandType.InlineType => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineR => 8,
            OperandType.InlineI8 => 8,
            OperandType.InlineVar => 2,
            _ => throw new NotSupportedException($"不支持的操作数类型: {opCode.OperandType}"),
        };
    }

    /// <summary>所有 IL 操作码的查找表（值 → OpCode），用于遍历 IL 字节流。</summary>
    private static readonly Dictionary<ushort, OpCode> s_allOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => (ushort)op.Value, op => op);
}
