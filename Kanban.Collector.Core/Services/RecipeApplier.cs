using Kanban.Collector.Core.Localization;
using Kanban.Contracts.Dtos;
using Kanban.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Kanban.Core.Services;

/// <summary>
/// 配方下发执行器：把配方参数项写入 PLC，写后读回校验，失败自动回滚。
/// 由**持有 PLC 连接的进程**调用（Local 模式 = MainAPP；Remote 模式 = Collector 经 Hub 转发），
/// 遵循 ADR-2 单写者——写 PLC 动作集中在此，两端共用。
///
/// PLC 依赖（IPlcDriver/PlcConnectionManager/IDeviceAdapterResolver）**延迟到 Apply 时解析**：
/// 避免构造期（Host 启动 MetaPublisher→ConfigSyncHandler 链）在 settings.Load() 之前触达
/// SharedPlcDriverRouter，导致驱动按未加载的默认 PlcConfig（192.168.1.2）构造。
/// </summary>
public sealed class RecipeApplier(IServiceProvider services)
{
    /// <summary>
    /// 下发配方到指定设备。失败时自动用写入前的备份值回滚。
    /// <paramref name="progress"/> 逐项推送进度（每项写入前 + 校验完成后；Local 模式传 Progress 回调、Remote 模式经 Hub 推送）。
    /// <paramref name="ct"/> 取消时已写项自动回滚（Local 模式 UI 取消用；Remote 模式为 SignalR 请求-响应，无法中途取消）。
    /// </summary>
    public RecipeApplyResultDto Apply(Device device, Recipe recipe,
        Action<RecipeApplyProgressDto>? progress = null, CancellationToken ct = default)
    {
        var itemResults = new List<RecipeItemResultDto>();

        if (device == null || recipe == null)
            return new RecipeApplyResultDto(false, RecipeValidationMessages.RecipeDeviceOrRecipeEmpty, itemResults);

        var connectionManager = services.GetRequiredService<PlcConnectionManager>();
        var adapterResolver = services.GetRequiredService<IDeviceAdapterResolver>();

        if (!connectionManager.IsConnected)
            return new RecipeApplyResultDto(false, RecipeValidationMessages.RecipePlcNotConnected, itemResults);

        var preErrors = RecipeValidator.Validate(recipe);
        if (preErrors.Count > 0)
            return new RecipeApplyResultDto(false, string.Join("；", preErrors), itemResults);

        var adapter = adapterResolver.Resolve(device);
        // ── 1) 备份当前值（写前读回）；声明在 try 外供 catch 回滚使用 ──
        var backups = new List<(RecipeItem Item, string Value)>();

        try
        {
            foreach (var item in recipe.Items)
            {
                if (!TryRead(adapter, item, out var current, out _))
                {
                    itemResults.Add(new RecipeItemResultDto(item.ParamName, false,
                        string.Format(RecipeValidationMessages.RecipeReadBackupFailed, item.PlcAddress)));
                    return new RecipeApplyResultDto(false, RecipeValidationMessages.RecipeBackupAbort, itemResults);
                }
                backups.Add((item, current));
            }

            // ── 2) 写入全部参数项 ──
            var total = recipe.Items.Count;
            var writeIndex = 0;
            foreach (var item in recipe.Items)
            {
                writeIndex++;
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(new RecipeApplyProgressDto(item.ParamName, writeIndex, total, false,
                    string.Format(RecipeValidationMessages.RecipeWriteInProgress, item.ParamName)));

                if (!RecipeValidator.TryConvert(item, out var intValue, out var floatValue, out var boolValue, out var stringValue, out var uint16Value))
                {
                    itemResults.Add(new RecipeItemResultDto(item.ParamName, false,
                        string.Format(RecipeValidationMessages.RecipeValueParseFailed, item.Value)));
                    return new RecipeApplyResultDto(false,
                        string.Format(RecipeValidationMessages.RecipeValueParseAbort, item.ParamName), itemResults);
                }

                var write = WriteValue(adapter, item, intValue, floatValue, boolValue, stringValue, uint16Value);
                if (!write.IsSuccess)
                {
                    Rollback(adapter, backups);
                    itemResults.Add(new RecipeItemResultDto(item.ParamName, false,
                        string.Format(RecipeValidationMessages.RecipeWriteFailed, write.Message)));
                    return new RecipeApplyResultDto(false,
                        string.Format(RecipeValidationMessages.RecipeWriteRollback, item.ParamName), itemResults);
                }
            }

            // ── 3) 读回校验（逐项比对，Float 带容差） ──
            var verifyIndex = 0;
            foreach (var item in recipe.Items)
            {
                verifyIndex++;
                ct.ThrowIfCancellationRequested();

                if (!TryRead(adapter, item, out var readBack, out _))
                {
                    Rollback(adapter, backups);
                    itemResults.Add(new RecipeItemResultDto(item.ParamName, false, RecipeValidationMessages.RecipeReadBackFailed));
                    return new RecipeApplyResultDto(false,
                        string.Format(RecipeValidationMessages.RecipeReadBackRollback, item.ParamName), itemResults);
                }

                if (!ValuesEqual(item, readBack))
                {
                    Rollback(adapter, backups);
                    itemResults.Add(new RecipeItemResultDto(item.ParamName, false,
                        string.Format(RecipeValidationMessages.RecipeReadBackMismatch, item.Value, readBack)));
                    return new RecipeApplyResultDto(false,
                        string.Format(RecipeValidationMessages.RecipeMismatchRollback, item.ParamName), itemResults);
                }

                itemResults.Add(new RecipeItemResultDto(item.ParamName, true, RecipeValidationMessages.RecipeWriteVerified));
                progress?.Invoke(new RecipeApplyProgressDto(item.ParamName, verifyIndex, total, true,
                    RecipeValidationMessages.RecipeWriteVerified));
            }

            return new RecipeApplyResultDto(true, RecipeValidationMessages.RecipeApplySuccess, itemResults);
        }
        catch (OperationCanceledException)
        {
            // 取消：回滚已写项，向调用方明确"已取消"而非失败
            try { Rollback(adapter, backups); } catch { /* 回滚自身失败不掩盖取消原因 */ }
            return new RecipeApplyResultDto(false, RecipeValidationMessages.RecipeApplyCancelled, itemResults);
        }
        catch (Exception ex)
        {
            try { Rollback(adapter, backups); } catch { /* 回滚自身失败不掩盖原始异常 */ }
            return new RecipeApplyResultDto(false, string.Format(RecipeValidationMessages.RecipeApplyException, ex.Message), itemResults);
        }
    }

    private void Rollback(IDeviceAdapter adapter, List<(RecipeItem Item, string Value)> backups)
    {
        // 回滚必须写回「写入前的备份值」而非配方目标值：TryConvert 解析 backups 元组中的 value。
        foreach (var (item, value) in backups)
        {
            if (!RecipeValidator.TryConvert(item, value, out var iv, out var fv, out var bv, out var sv, out var uv))
                continue;
            try { WriteValue(adapter, item, iv, fv, bv, sv, uv); } catch { /* 尽力回滚 */ }
        }
    }

    private static bool TryRead(IDeviceAdapter adapter, RecipeItem item, out string value, out string error)
    {
        value = ""; error = "";
        switch (item.DataType)
        {
            case Kanban.Contracts.Enums.PlcDataType.Int32:
                var ri = adapter.ReadInt32(item.PlcAddress);
                if (!ri.IsSuccess) { error = ri.Message; return false; }
                value = ri.Content.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case Kanban.Contracts.Enums.PlcDataType.Float:
                var rf = adapter.ReadFloat(item.PlcAddress);
                if (!rf.IsSuccess) { error = rf.Message; return false; }
                value = rf.Content.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case Kanban.Contracts.Enums.PlcDataType.UInt16:
                var ru = adapter.ReadUInt16(item.PlcAddress);
                if (!ru.IsSuccess) { error = ru.Message; return false; }
                value = ru.Content.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case Kanban.Contracts.Enums.PlcDataType.Bool:
                var rb = adapter.ReadBool(item.PlcAddress);
                if (!rb.IsSuccess) { error = rb.Message; return false; }
                value = rb.Content ? "1" : "0";
                return true;
            case Kanban.Contracts.Enums.PlcDataType.String:
                var len = (ushort)Math.Max(8, Math.Max(item.Value.Length, 1) + 1);
                var rs = adapter.ReadString(item.PlcAddress, len);
                if (!rs.IsSuccess) { error = rs.Message; return false; }
                value = rs.Content;
                return true;
            default:
                error = $"不支持的数据类型 {item.DataType}";
                return false;
        }
    }

    private static PlcOperationResult WriteValue(IDeviceAdapter adapter, RecipeItem item,
        int intValue, float floatValue, bool boolValue, string stringValue, ushort uint16Value)
    {
        return item.DataType switch
        {
            Kanban.Contracts.Enums.PlcDataType.Int32 => adapter.WriteInt32(item.PlcAddress, intValue),
            Kanban.Contracts.Enums.PlcDataType.Float => adapter.WriteFloat(item.PlcAddress, floatValue),
            Kanban.Contracts.Enums.PlcDataType.UInt16 => adapter.WriteUInt16(item.PlcAddress, uint16Value),
            Kanban.Contracts.Enums.PlcDataType.Bool => adapter.WriteBool(item.PlcAddress, boolValue),
            Kanban.Contracts.Enums.PlcDataType.String => adapter.WriteString(item.PlcAddress, stringValue),
            _ => PlcOperationResult.Fail(string.Format(RecipeValidationMessages.RecipeUnsupportedType, item.DataType)),
        };
    }

    private static bool ValuesEqual(RecipeItem item, string readBack)
    {
        if (!RecipeValidator.TryConvert(item, out var intValue, out var floatValue, out var boolValue, out var stringValue, out var uint16Value))
            return false;

        return item.DataType switch
        {
            Kanban.Contracts.Enums.PlcDataType.Int32 =>
                int.TryParse(readBack, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ri) && ri == intValue,
            Kanban.Contracts.Enums.PlcDataType.Float =>
                float.TryParse(readBack, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rf)
                && Math.Abs(rf - floatValue) <= 0.001f * Math.Max(1f, Math.Abs(floatValue)),
            Kanban.Contracts.Enums.PlcDataType.UInt16 =>
                ushort.TryParse(readBack, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ru) && ru == uint16Value,
            Kanban.Contracts.Enums.PlcDataType.Bool =>
                readBack is "1" or "True" or "true" ? boolValue : (readBack is "0" or "False" or "false" ? !boolValue : false),
            Kanban.Contracts.Enums.PlcDataType.String => string.Equals(readBack, stringValue, StringComparison.Ordinal),
            _ => false,
        };
    }
}
