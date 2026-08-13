using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using System;
using System.Threading.Tasks;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// PLC 读写命令的结果状态。ViewModel 据此映射到对应的通知级别（Success/Info/Warning/Error）。
/// Cancelled 仅用于需用户二次确认的危险操作被用户拒绝时，ViewModel 不弹通知。
/// </summary>
public enum PlcOpStatus
{
    Success,
    Info,
    Warning,
    Error,
    Cancelled,
}

/// <summary>
/// PLC 读写命令的统一返回结果。Status 决定通知级别，Message 为通知文案，
/// ReadValue 仅 ReadPlcValueAsync 设置（用于调用方进一步处理读取到的值）。
/// </summary>
public record PlcOpResult(PlcOpStatus Status, string Message, int? ReadValue = null);

/// <summary>
/// 设备 PLC 命令处理器：封装配方写入、OEE 清零、PLC 读取、计数报警清零四类操作的
/// 前置校验（连接/地址格式/值范围）与 PLC 读写。所有方法返回 <see cref="PlcOpResult"/>，
/// 不直接调用 IDialogService——通知与确认 UI 由 ViewModel 负责，便于单元测试。
/// 危险操作（OEE 清零）通过 confirmCallback 让调用方控制二次确认 UI。
/// </summary>
public class DevicePlcCommandHandler(
    IPlcDriver plc,
    PlcConnectionManager connectionManager,
    IPlcDataAcquisitionService dataAcquisitionService,
    IDeviceAdapterResolver? adapterResolver = null,
    IPlcAddressCodecResolver? codecResolver = null,
    IPlcRuntimeProfileProvider? profileProvider = null)
{
    private readonly IDeviceAdapterResolver? _adapterResolver = adapterResolver;
    private readonly IDeviceAdapter _fallbackAdapter = new PlcDeviceAdapter(plc, profileProvider, codecResolver);
    private readonly PlcConnectionManager _connectionManager = connectionManager;
    private readonly IPlcDataAcquisitionService _dataAcquisitionService = dataAcquisitionService;

    /// <summary>
    /// 向选中设备写入配方值（RecipeAddress ← RecipeValue）。
    /// 前置校验：PLC 连接、配方地址非空、地址为 D 字、配方值在 0-999999 区间。
    /// 写入在后台线程执行（IPlcDriver 内部加锁，与采集线程竞争时仅短暂等待）。
    /// </summary>
    /// <returns>
    /// Success=写入成功；Info=未配置配方地址（跳过）；Warning=连接/格式/范围/写入失败；
    /// Error=异常。Message 用于 ViewModel 拼装 RecipeStatus 文案。
    /// </returns>
    public async Task<PlcOpResult> WriteRecipeAsync(Device device)
    {
        if (!_connectionManager.IsConnected)
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M179);

        if (string.IsNullOrWhiteSpace(device.RecipeAddress))
            return new PlcOpResult(PlcOpStatus.Info, Strings.M180);

        if (GetAdapter(device).AddressCodec.Parse(device.RecipeAddress) is not { IsValid: true, Type: PlcAddressType.DWord })
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F230, device.RecipeAddress));

        if (device.RecipeValue < 0 || device.RecipeValue > 999_999)
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F229, device.RecipeValue));

        try
        {
            var result = await Task.Run(() => GetAdapter(device).WriteInt32(device.RecipeAddress!, device.RecipeValue));
            return result.IsSuccess
                ? new PlcOpResult(PlcOpStatus.Success, result.Message)
                : new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F070, result.Message));
        }
        catch (Exception ex)
        {
            return new PlcOpResult(PlcOpStatus.Error, string.Format(Strings.F071, ex.Message));
        }
    }

    /// <summary>
    /// 手动触发选中设备的 OEE 清零：触发 PLC 清零 + 同步软件侧 OEE 累计清零（产量+时间+报警）+ 基线清零。
    /// 与班次切换自动触发的清零逻辑一致，此处仅做手动即时触发且只作用于选中设备。
    /// 该操作会清零当前产量/时间/报警累计且不可撤销，属危险写操作——通过 confirmCallback 让调用方
    /// 弹出二次确认框，回调返回 false 时直接返回 Cancelled 跳过 PLC 写入。
    /// </summary>
    /// <param name="device">要清零的设备。</param>
    /// <param name="confirmCallback">调用方提供的二次确认回调，参数为待清零设备，返回 true 表示用户确认执行。</param>
    /// <returns>
    /// Cancelled=用户拒绝确认；Success=已触发清零；Warning=连接/地址/格式/清零失败；Error=异常。
    /// </returns>
    public async Task<PlcOpResult> ResetProductionAsync(Device device, Func<Device, bool> confirmCallback)
    {
        if (!_connectionManager.IsConnected)
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M181);

        var addr = device.ProductionResetAddress;
        if (string.IsNullOrWhiteSpace(addr))
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M182);

        if (GetAdapter(device).AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F008, addr));

        if (!confirmCallback(device))
            return new PlcOpResult(PlcOpStatus.Cancelled, Strings.M183);

        try
        {
            var success = await Task.Run(() => _dataAcquisitionService.ResetDeviceProduction(device));
            return success
                ? new PlcOpResult(PlcOpStatus.Success, string.Format(Strings.F116, device.Name))
                : new PlcOpResult(PlcOpStatus.Warning, Strings.M184);
        }
        catch (Exception ex)
        {
            return new PlcOpResult(PlcOpStatus.Error, string.Format(Strings.F162, ex.Message));
        }
    }

    /// <summary>
    /// 从 PLC 读取指定 D 字地址的当前值，用于调试/验证地址配置是否正确。
    /// </summary>
    /// <param name="address">PLC D 字地址（如 D100）。</param>
    /// <returns>
    /// Success=读取成功（ReadValue 为读到的值）；Warning=连接/格式/读取失败；Error=异常。
    /// </returns>
    public async Task<PlcOpResult> ReadPlcValueAsync(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M185);

        if (!_connectionManager.IsConnected)
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M186);

        if (_fallbackAdapter.AddressCodec.Parse(address) is not { IsValid: true, Type: PlcAddressType.DWord })
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F082, address));

        try
        {
            var result = await Task.Run(() => _fallbackAdapter.ReadInt32(address));
            return result.IsSuccess
                ? new PlcOpResult(PlcOpStatus.Success, string.Format(Strings.F031, address, result.Content), result.Content)
                : new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F216, result.Message));
        }
        catch (Exception ex)
        {
            return new PlcOpResult(PlcOpStatus.Error, string.Format(Strings.F217, ex.Message));
        }
    }

    /// <summary>
    /// 清空指定计数报警的当前值：向 PLC 写 0 并同步复位 <see cref="CountAlarm.CurrentValue"/>。
    /// </summary>
    /// <param name="alarm">要清空的计数报警，必须已配置 PlcAddress。</param>
    /// <returns>
    /// Success=已清空（alarm.CurrentValue 已置 0）；Warning=连接/格式/写入失败；Error=异常。
    /// </returns>
    public async Task<PlcOpResult> ResetCountAlarmValueAsync(CountAlarm alarm)
    {
        if (alarm == null || string.IsNullOrWhiteSpace(alarm.PlcAddress))
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M185);

        if (!_connectionManager.IsConnected)
            return new PlcOpResult(PlcOpStatus.Warning, Strings.M188);

        if (_fallbackAdapter.AddressCodec.Parse(alarm.PlcAddress) is not { IsValid: true, Type: PlcAddressType.DWord })
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F083, alarm.PlcAddress));

        try
        {
            var result = await Task.Run(() => _fallbackAdapter.WriteInt32(alarm.PlcAddress!, 0));
            if (result.IsSuccess)
            {
                alarm.CurrentValue = 0;
                return new PlcOpResult(PlcOpStatus.Success, string.Format(Strings.F111, alarm.Name));
            }
            return new PlcOpResult(PlcOpStatus.Warning, string.Format(Strings.F160, result.Message));
        }
        catch (Exception ex)
        {
            return new PlcOpResult(PlcOpStatus.Error, string.Format(Strings.F161, ex.Message));
        }
    }

    private IDeviceAdapter GetAdapter(Device device) => _adapterResolver?.Resolve(device) ?? _fallbackAdapter;
}
