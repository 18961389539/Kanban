using System.Threading.Tasks;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// PLC 数据采集服务抽象接口：定义外部调用方（ViewModel / DevicePlcCommandHandler 等）
/// 依赖的公共契约。实现类 <see cref="PlcDataAcquisitionService"/> 负责定时轮询设备产量、
/// 状态、报警位与缺陷计数。通过接口解耦，便于测试替换与依赖反转。
/// </summary>
public interface IPlcDataAcquisitionService
{
    /// <summary>
    /// 采集循环是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 启动数据采集循环。线程安全：并发调用不会生成多个轮询任务。
    /// </summary>
    void Start();

    /// <summary>
    /// 停止数据采集循环（异步）：标记离线 + Cancel + 等待轮询循环退出。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 上班次产量汇总的只读快照（线程安全拷贝）。
    /// </summary>
    (int Ok, int Ng, string ShiftName) GetLastShiftSummary(string deviceId);

    /// <summary>
    /// 重置所有设备的班次累计：触发 PLC 清零 + 清空软件侧基线/OEE/报警状态。
    /// </summary>
    void ResetShift();

    /// <summary>
    /// 手动 OEE 清零单台设备：触发 PLC 清零 + 清零软件侧 OEE 累计值 + 设置基线清零窗口。
    /// 返回 PLC 清零是否成功。
    /// </summary>
    bool ResetDeviceProduction(Device device);

    /// <summary>
    /// 手动「全部设备 OEE 清零」：对全部设备执行与班次切换相同的 PLC 触发 + 软件侧清零 + 1 秒基线窗口。
    /// 返回 (PLC 触发位写入成功的设备数, 参与设备总数)；软件侧清零始终执行，不依赖 PLC 写入结果。
    /// </summary>
    (int Triggered, int Total) ResetAllDevicesProduction();

    /// <summary>
    /// 清理已删除设备的残留内存状态（产量基线 + 报警/状态字典），避免内存泄漏。
    /// </summary>
    void RemoveDeviceState(Device device);

    /// <summary>
    /// 清理已删除报警的残留内存状态，避免内存泄漏。
    /// </summary>
    void RemoveAlarmState(string alarmId);

    /// <summary>
    /// 采集运行诊断快照（轮询周期/设备/批量读/历史写入耗时等）。
    /// </summary>
    AcquisitionDiagnosticsSnapshot GetDiagnosticsSnapshot();
}
