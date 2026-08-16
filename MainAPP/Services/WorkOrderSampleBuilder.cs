using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;

namespace MainAPP.Services;

/// <summary>
/// 工单样本数据生成器（DEBUG 用）：为每台设备生成 3 个样本工单，
/// 覆盖 Running/Pending/Completed/Aborted 状态，数据可复现（固定随机种子）。
/// 与 SampleDeviceBuilder 同类，从 WorkOrderManagerViewModel 抽出。
/// </summary>
public static class WorkOrderSampleBuilder
{
    public static List<WorkOrder> BuildSampleWorkOrders(IReadOnlyList<Device> devices)
    {
        var now = DateTime.Now;
        var products = new[]
        {
            ("P-1001", "外壳组件A"),
            ("P-1002", "外壳组件B"),
            ("P-2001", "电路板模组"),
            ("P-2002", "传感器模组"),
            ("P-3001", "连接器"),
            ("P-3002", "端子台"),
            ("P-4001", "散热片"),
            ("P-4002", "支架组件"),
        };

        var remarks = new[]
        {
            "常规生产批次",
            "客户加急订单",
            "试产验证",
            "返工批次",
            null,
        };

        List<WorkOrder> result = [];
        var rng = new Random(42); // 固定种子确保可复现

        for (var i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var product = products[i % products.Length];
            var dayOffset = i / 4; // 每 4 台设备错开一天

            // 1. Running 工单（当前进行中，计划时间覆盖现在）
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-R",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 500 + rng.Next(0, 10) * 100,
                PlannedStart = now.AddDays(-dayOffset).AddHours(-6),
                PlannedEnd = now.AddDays(-dayOffset).AddHours(2),
                Status = WorkOrderStatus.Running,
                Remark = remarks[i % remarks.Length],
                CreatedAt = now.AddDays(-dayOffset).AddHours(-8),
                UpdatedAt = now.AddDays(-dayOffset).AddHours(-6),
            });

            // 2. Pending 工单（待开始，计划时间在未来）
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-P",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 800 + rng.Next(0, 8) * 100,
                PlannedStart = now.AddDays(1 + dayOffset).Date.AddHours(8),
                PlannedEnd = now.AddDays(1 + dayOffset).Date.AddHours(20),
                Status = WorkOrderStatus.Pending,
                Remark = remarks[(i + 2) % remarks.Length],
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now.AddDays(-1),
            });

            // 3. 已结束工单（Completed 或 Aborted，计划时间在过去）
            var completed = i % 3 != 0; // 2/3 为 Completed，1/3 为 Aborted
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-{(completed ? "C" : "A")}",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 1000 + rng.Next(0, 6) * 100,
                PlannedStart = now.AddDays(-2 - dayOffset).Date.AddHours(8),
                PlannedEnd = now.AddDays(-2 - dayOffset).Date.AddHours(20),
                Status = completed ? WorkOrderStatus.Completed : WorkOrderStatus.Aborted,
                Remark = completed ? "已完成交付" : "因设备故障中止",
                CreatedAt = now.AddDays(-3 - dayOffset),
                UpdatedAt = now.AddDays(-2 - dayOffset).Date.AddHours(20),
            });
        }

        return result;
    }
}
