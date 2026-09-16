using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using System;
using System.Collections.Generic;
using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 虚拟设备样本数据构造器：仅供 DEBUG 构建下的"生成虚拟设备"命令调用，
/// 用于 UI 预览/调试。所有方法纯静态，无副作用，不依赖 ViewModel 状态。
/// 地址段分配策略见 <see cref="BuildSampleDevices"/> 顶部注释。
/// </summary>
public static class SampleDeviceBuilder
{
    /// <summary>
    /// 构造 20 台虚拟设备样本（注塑机×4 / 焊接机×4 / 装配机×3 / 检测机×3 / CNC×2 / 包装机×2 / 激光打标×1 / 清洗×1），
    /// 覆盖不同行业设备配置形态。地址段分配（跨设备唯一，由方法末尾 AssertNoDuplicateAddresses 校验）：
    ///   - 主 PLC 地址：d1-d10 用 D100-D196；d11/d12 用 D40x/D41x；d13-d20 用 D42x-D49x
    ///   - 配方地址：D500-D538（偶数步进，20 台）
    ///   - 报警位：M100-M296（每设备 8 位槽位）
    ///   - 缺陷地址：Int32 占 2 字，偶数步进（隔 2 个字，避免高字写入下一缺陷）。d1-d10 用 D800-D904；d11-d20 用 D920-D1012；D906-D918 空出
    ///   - 计数报警：d1-d10 用 D300-D393；d11/d12 用 D70x/D71x；d13-d20 用 D72x-D79x
    /// 每台设备配置 6-8 个报警 + 4-6 个缺陷 + 4-5 个计数报警，覆盖不同严重等级/类别/单位。
    /// </summary>
    public static List<Device> BuildSampleDevices()
    {
        var devices = new List<Device>(20);

        // ── 设备 1：注塑机A1（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        var d1 = new Device
        {
            Name = "注塑机A1",
            TargetCycle = 600,
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
            RecipeName = "配方A-标准",
            RecipeValue = 1,
            RecipeAddress = "D500",
        };
        AddSampleAlarm(d1, "温度过高", "M100", AlarmLevel.High, "模温超过设定上限 80°C，需检查冷却水循环");
        AddSampleAlarm(d1, "压力低", "M101", AlarmLevel.Medium, "液压系统压力低于 5MPa，可能漏油");
        AddSampleAlarm(d1, "料斗空", "M102", AlarmLevel.Low, "料斗内原料不足，请及时补料");
        AddSampleAlarm(d1, "模具未关", "M103", AlarmLevel.High, "安全门未关闭，注塑动作被联锁阻止");
        AddSampleAlarm(d1, "螺杆异常", "M104", AlarmLevel.Medium, "螺杆转动阻力异常，可能卡料或轴承磨损");
        AddSampleAlarm(d1, "润滑不足", "M105", AlarmLevel.Low, "导柱润滑脂不足，建议手动加注");
        AddSampleAlarm(d1, "加热圈断路", "M106", AlarmLevel.High, "加热圈开路，温度无法上升");
        AddSampleAlarm(d1, "锁模力不足", "M107", AlarmLevel.Medium, "锁模力低于设定值 80%，可能胀模");
        AddSampleDefect(d1, "划痕", "D800", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "尺寸偏大", "D802", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d1, "飞边", "D804", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d1, "缩水", "D806", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "气泡", "D808", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "黑点", "D810", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCounterAlarm(d1, "连续NG次数", "D300", 10, "个", "连续 NG 超过 10 个时停机检查模具");
        AddSampleCounterAlarm(d1, "停机次数", "D301", 5, "次", "班次内异常停机超过 5 次需检修");
        AddSampleCounterAlarm(d1, "模具保养计数", "D302", 10000, "模次", "累计模次达 1 万需保养模具");
        AddSampleCounterAlarm(d1, "班次产量", "D303", 0, "件", "班次产量计数（阈值 0 表示仅记录不停机）");
        AddSampleCounterAlarm(d1, "能耗累计", "D304", 0, "kWh", "能耗累计（阈值 0 表示仅记录不停机）");
        devices.Add(d1);

        // ── 设备 2：注塑机A2（中量配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        var d2 = new Device
        {
            Name = "注塑机A2",
            TargetCycle = 550,
            OkCountAddress = "D110",
            NgCountAddress = "D112",
            StatusCountAddress = "D114",
            ProductionResetAddress = "D116",
            RecipeName = "配方A-快速",
            RecipeValue = 2,
            RecipeAddress = "D502",
        };
        AddSampleAlarm(d2, "温度过高", "M110", AlarmLevel.High, "模温超过设定上限 80°C");
        AddSampleAlarm(d2, "料斗空", "M111", AlarmLevel.Low, "料斗内原料不足，请及时补料");
        AddSampleAlarm(d2, "模具未对齐", "M112", AlarmLevel.Medium, "模具合模面对不齐，可能损坏型腔");
        AddSampleAlarm(d2, "伺服报警", "M113", AlarmLevel.High, "伺服驱动器报警代码 0x21，需复位");
        AddSampleAlarm(d2, "储料不足", "M114", AlarmLevel.Low, "炮筒储料量低于预设值");
        AddSampleAlarm(d2, "加热圈断路", "M115", AlarmLevel.High, "二段加热圈开路");
        AddSampleAlarm(d2, "锁模力不足", "M116", AlarmLevel.Medium, "锁模力低于设定值 80%");
        AddSampleDefect(d2, "色差", "D812", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d2, "气泡", "D814", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d2, "黑点", "D816", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d2, "尺寸偏小", "D818", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d2, "缩水", "D820", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleCounterAlarm(d2, "连续NG次数", "D310", 8, "个", "连续 NG 超过 8 个时停机检查");
        AddSampleCounterAlarm(d2, "维护计数", "D311", 2000, "次", "累计注塑次数达到 2000 次需保养");
        AddSampleCounterAlarm(d2, "能耗累计", "D312", 0, "kWh", "能耗累计（阈值 0 表示仅记录不停机）");
        AddSampleCounterAlarm(d2, "模具保养计数", "D313", 8000, "模次", "累计模次达 8000 需保养模具");
        devices.Add(d2);

        // ── 设备 3：焊接机B1（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // PLC 地址用 D12x 段（避开 d1 主地址 D10x、计数 D300-D304；缺陷已迁到 D8xx）
        var d3 = new Device
        {
            Name = "焊接机B1",
            TargetCycle = 400,
            OkCountAddress = "D120",
            NgCountAddress = "D122",
            StatusCountAddress = "D124",
            ProductionResetAddress = "D126",
            RecipeName = "焊接参数-强",
            RecipeValue = 3,
            RecipeAddress = "D504",
        };
        AddSampleAlarm(d3, "焊头过热", "M120", AlarmLevel.High, "焊头温度超过 600°C，需停机冷却");
        AddSampleAlarm(d3, "气压不足", "M121", AlarmLevel.Medium, "压缩空气压力低于 0.4MPa");
        AddSampleAlarm(d3, "电极磨损", "M122", AlarmLevel.Low, "电极帽磨损达到阈值，需更换");
        AddSampleAlarm(d3, "冷却水断流", "M123", AlarmLevel.High, "冷却水流量低于阈值，可能烧坏电极");
        AddSampleAlarm(d3, "焊接超时", "M124", AlarmLevel.Medium, "单点焊接时间超过 3 秒，可能工件松动");
        AddSampleAlarm(d3, "电流异常", "M125", AlarmLevel.High, "焊接电流超出设定范围 ±10%");
        AddSampleAlarm(d3, "电压波动", "M126", AlarmLevel.Medium, "网电压波动超过 ±15%");
        AddSampleAlarm(d3, "气动阀卡死", "M127", AlarmLevel.Low, "气动换向阀响应超时，需检修");
        AddSampleDefect(d3, "虚焊", "D822", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d3, "焊疤过大", "D824", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d3, "焊穿", "D826", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d3, "焊偏", "D828", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d3, "气孔", "D830", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d3, "裂纹", "D832", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCounterAlarm(d3, "虚焊次数", "D320", 3, "次", "单班次虚焊次数超过 3 次需校准参数");
        AddSampleCounterAlarm(d3, "维护计数", "D321", 5000, "次", "累计焊接次数达到 5000 次需保养");
        AddSampleCounterAlarm(d3, "电极磨损计数", "D322", 800, "次", "电极焊接达 800 次需修磨");
        AddSampleCounterAlarm(d3, "焊穿次数", "D323", 2, "次", "单班次焊穿超 2 次需停机");
        AddSampleCounterAlarm(d3, "电流异常次数", "D324", 3, "次", "单班次电流异常超 3 次需检修");
        devices.Add(d3);

        // ── 设备 4：焊接机B2（中量配置：6 报警 + 5 缺陷 + 5 计数报警） ──
        var d4 = new Device
        {
            Name = "焊接机B2",
            TargetCycle = 420,
            OkCountAddress = "D130",
            NgCountAddress = "D132",
            StatusCountAddress = "D134",
            ProductionResetAddress = "D136",
            RecipeName = "焊接参数-弱",
            RecipeValue = 4,
            RecipeAddress = "D506",
        };
        AddSampleAlarm(d4, "焊头过热", "M130", AlarmLevel.High, "焊头温度超过 600°C");
        AddSampleAlarm(d4, "气压不足", "M131", AlarmLevel.Medium, "压缩空气压力低于 0.4MPa");
        AddSampleAlarm(d4, "电极磨损", "M132", AlarmLevel.Low, "电极帽磨损达到阈值");
        AddSampleAlarm(d4, "冷却水断流", "M133", AlarmLevel.High, "冷却水流量低于阈值");
        AddSampleAlarm(d4, "焊接超时", "M134", AlarmLevel.Medium, "单点焊接时间超过 3 秒");
        AddSampleAlarm(d4, "电压波动", "M135", AlarmLevel.Medium, "网电压波动超过 ±15%");
        AddSampleDefect(d4, "脱焊", "D834", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d4, "虚焊", "D836", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d4, "焊疤过大", "D838", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d4, "气孔", "D840", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d4, "裂纹", "D842", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCounterAlarm(d4, "脱焊次数", "D330", 2, "次", "单班次脱焊次数超 2 次需停机");
        AddSampleCounterAlarm(d4, "维护计数", "D331", 1000, "次", "累计焊接次数达到 1000 次需保养");
        AddSampleCounterAlarm(d4, "虚焊次数", "D332", 5, "次", "单班次虚焊次数超 5 次需校准");
        AddSampleCounterAlarm(d4, "电流异常次数", "D333", 3, "次", "单班次电流异常超 3 次需停机");
        AddSampleCounterAlarm(d4, "电极磨损计数", "D334", 600, "次", "电极焊接达 600 次需修磨");
        devices.Add(d4);

        // ── 设备 5：装配机C1（重配置：7 报警 + 6 缺陷 + 4 计数报警） ──
        var d5 = new Device
        {
            Name = "装配机C1",
            TargetCycle = 300,
            OkCountAddress = "D140",
            NgCountAddress = "D142",
            StatusCountAddress = "D144",
            ProductionResetAddress = "D146",
            RecipeName = "装配方案-C1",
            RecipeValue = 5,
            RecipeAddress = "D508",
        };
        AddSampleAlarm(d5, "电机过载", "M140", AlarmLevel.High, "伺服电机电流超过额定值 150%");
        AddSampleAlarm(d5, "气压低", "M141", AlarmLevel.Medium, "气动元件供气压力低于 0.5MPa");
        AddSampleAlarm(d5, "螺丝用尽", "M142", AlarmLevel.Low, "螺丝供料器内剩余数量不足");
        AddSampleAlarm(d5, "夹具松动", "M143", AlarmLevel.Medium, "夹具夹紧力低于阈值，工件可能位移");
        AddSampleAlarm(d5, "位置超差", "M144", AlarmLevel.High, "伺服定位偏差超过 ±0.1mm");
        AddSampleAlarm(d5, "传感器故障", "M145", AlarmLevel.High, "位置传感器无信号，可能断线");
        AddSampleAlarm(d5, "气缸卡阻", "M146", AlarmLevel.Medium, "气缸动作超时，可能卡死");
        AddSampleDefect(d5, "错件", "D844", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d5, "漏装", "D846", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d5, "浮高", "D848", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d5, "滑丝", "D850", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d5, "错位", "D852", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d5, "损伤", "D854", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCounterAlarm(d5, "错件次数", "D340", 0, "个", "错件计数（阈值 0 表示仅记录不触发）");
        AddSampleCounterAlarm(d5, "漏装次数", "D341", 0, "个", "漏装计数（阈值 0 表示仅记录不触发）");
        AddSampleCounterAlarm(d5, "滑丝次数", "D342", 5, "个", "单班次滑丝次数超 5 个需更换螺丝刀头");
        AddSampleCounterAlarm(d5, "维护计数", "D343", 3000, "次", "累计运行达 3000 次需保养");
        devices.Add(d5);

        // ── 设备 6：检测机D1（中量配置：7 报警 + 6 缺陷 + 4 计数报警） ──
        var d6 = new Device
        {
            Name = "检测机D1",
            TargetCycle = 800,
            OkCountAddress = "D150",
            NgCountAddress = "D152",
            StatusCountAddress = "D154",
            ProductionResetAddress = "D156",
            RecipeName = "检测方案-默认",
            RecipeValue = 6,
            RecipeAddress = "D510",
        };
        AddSampleAlarm(d6, "相机断开", "M150", AlarmLevel.High, "工业相机连接中断，无法继续检测");
        AddSampleAlarm(d6, "光源故障", "M151", AlarmLevel.Medium, "光源亮度异常，可能影响检测精度");
        AddSampleAlarm(d6, "镜头污染", "M152", AlarmLevel.Low, "镜头表面有异物，建议清洁");
        AddSampleAlarm(d6, "传送带卡阻", "M153", AlarmLevel.High, "传送带运行阻力异常，可能卡料");
        AddSampleAlarm(d6, "检测超时", "M154", AlarmLevel.Medium, "单件检测时间超过 2 秒，可能算法异常");
        AddSampleAlarm(d6, "工位未到位", "M155", AlarmLevel.Medium, "分度工位未到位，检测启动被联锁");
        AddSampleAlarm(d6, "气压低", "M156", AlarmLevel.Low, "剔除气缸供气压力低于 0.4MPa");
        AddSampleDefect(d6, "划痕", "D856", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d6, "尺寸超差", "D858", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d6, "脏污", "D860", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d6, "变形", "D862", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d6, "缺件", "D864", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d6, "错件", "D866", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCounterAlarm(d6, "NG连续", "D350", 5, "个", "连续 NG 超过 5 个需复检相机标定");
        AddSampleCounterAlarm(d6, "复检次数", "D351", 3, "次", "单班次复检超 3 次需校准检测算法");
        AddSampleCounterAlarm(d6, "设备保养计数", "D352", 720, "h", "累计运行 720 小时需保养");
        AddSampleCounterAlarm(d6, "剔除次数", "D353", 50, "次", "单班次剔除超 50 次需检查剔除机构");
        devices.Add(d6);

        // ── 设备 7：注塑机A3（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D16x，报警 M16x，缺陷 D868 起偶数步进，计数 D36x，配方 D512
        var d7 = new Device
        {
            Name = "注塑机A3",
            TargetCycle = 580,
            OkCountAddress = "D160",
            NgCountAddress = "D162",
            StatusCountAddress = "D164",
            ProductionResetAddress = "D166",
            RecipeName = "配方A-高精度",
            RecipeValue = 7,
            RecipeAddress = "D512",
        };
        AddSampleAlarm(d7, "温度过高", "M160", AlarmLevel.High, "模温超过设定上限 80°C");
        AddSampleAlarm(d7, "压力低", "M161", AlarmLevel.Medium, "液压系统压力低于 5MPa");
        AddSampleAlarm(d7, "料斗空", "M162", AlarmLevel.Low, "料斗内原料不足");
        AddSampleAlarm(d7, "模具未关", "M163", AlarmLevel.High, "安全门未关闭");
        AddSampleAlarm(d7, "螺杆异常", "M164", AlarmLevel.Medium, "螺杆转动阻力异常");
        AddSampleAlarm(d7, "加热圈断路", "M165", AlarmLevel.High, "三段加热圈开路");
        AddSampleAlarm(d7, "润滑不足", "M166", AlarmLevel.Low, "导柱润滑脂不足");
        AddSampleDefect(d7, "飞边", "D868", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d7, "尺寸偏大", "D870", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d7, "缩水", "D872", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d7, "气泡", "D874", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d7, "黑点", "D876", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCounterAlarm(d7, "连续NG次数", "D360", 8, "个", "连续 NG 超过 8 个时停机检查");
        AddSampleCounterAlarm(d7, "停机次数", "D361", 4, "次", "班次内异常停机超 4 次需检修");
        AddSampleCounterAlarm(d7, "模具保养计数", "D362", 12000, "模次", "累计模次达 1.2 万需保养");
        AddSampleCounterAlarm(d7, "班次产量", "D363", 0, "件", "班次产量计数（阈值 0 表示仅记录不停机）");
        devices.Add(d7);

        // ── 设备 8：焊接机B3（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D17x，报警 M17x，缺陷 D878 起偶数步进，计数 D37x，配方 D514
        var d8 = new Device
        {
            Name = "焊接机B3",
            TargetCycle = 380,
            OkCountAddress = "D170",
            NgCountAddress = "D172",
            StatusCountAddress = "D174",
            ProductionResetAddress = "D176",
            RecipeName = "焊接参数-中",
            RecipeValue = 8,
            RecipeAddress = "D514",
        };
        AddSampleAlarm(d8, "焊头过热", "M170", AlarmLevel.High, "焊头温度超过 600°C");
        AddSampleAlarm(d8, "气压不足", "M171", AlarmLevel.Medium, "压缩空气压力低于 0.4MPa");
        AddSampleAlarm(d8, "电极磨损", "M172", AlarmLevel.Low, "电极帽磨损达到阈值");
        AddSampleAlarm(d8, "冷却水断流", "M173", AlarmLevel.High, "冷却水流量低于阈值");
        AddSampleAlarm(d8, "电流异常", "M174", AlarmLevel.High, "焊接电流超出设定范围 ±10%");
        AddSampleAlarm(d8, "焊接超时", "M175", AlarmLevel.Medium, "单点焊接时间超过 3 秒");
        AddSampleDefect(d8, "虚焊", "D878", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d8, "焊疤过大", "D880", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d8, "焊穿", "D882", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d8, "焊偏", "D884", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleCounterAlarm(d8, "虚焊次数", "D370", 4, "次", "单班次虚焊次数超 4 次需校准");
        AddSampleCounterAlarm(d8, "维护计数", "D371", 4000, "次", "累计焊接次数达 4000 次需保养");
        AddSampleCounterAlarm(d8, "电极磨损计数", "D372", 700, "次", "电极焊接达 700 次需修磨");
        AddSampleCounterAlarm(d8, "焊穿次数", "D373", 1, "次", "单班次焊穿超 1 次需停机检修");
        devices.Add(d8);

        // ── 设备 9：装配机C2（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D18x，报警 M18x，缺陷 D886 起偶数步进，计数 D38x，配方 D516
        var d9 = new Device
        {
            Name = "装配机C2",
            TargetCycle = 280,
            OkCountAddress = "D180",
            NgCountAddress = "D182",
            StatusCountAddress = "D184",
            ProductionResetAddress = "D186",
            RecipeName = "装配方案-C2",
            RecipeValue = 9,
            RecipeAddress = "D516",
        };
        AddSampleAlarm(d9, "电机过载", "M180", AlarmLevel.High, "伺服电机电流超过额定值 150%");
        AddSampleAlarm(d9, "气压低", "M181", AlarmLevel.Medium, "气动元件供气压力低于 0.5MPa");
        AddSampleAlarm(d9, "螺丝用尽", "M182", AlarmLevel.Low, "螺丝供料器内剩余数量不足");
        AddSampleAlarm(d9, "夹具松动", "M183", AlarmLevel.Medium, "夹具夹紧力低于阈值");
        AddSampleAlarm(d9, "位置超差", "M184", AlarmLevel.High, "伺服定位偏差超过 ±0.1mm");
        AddSampleAlarm(d9, "气缸卡阻", "M185", AlarmLevel.Medium, "气缸动作超时");
        AddSampleAlarm(d9, "传感器故障", "M186", AlarmLevel.High, "位置传感器无信号");
        AddSampleDefect(d9, "错件", "D886", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d9, "漏装", "D888", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d9, "浮高", "D890", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d9, "滑丝", "D892", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d9, "损伤", "D894", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCounterAlarm(d9, "错件次数", "D380", 0, "个", "错件计数（阈值 0 表示立即停机）");
        AddSampleCounterAlarm(d9, "漏装次数", "D381", 0, "个", "漏装计数（阈值 0 表示立即停机）");
        AddSampleCounterAlarm(d9, "滑丝次数", "D382", 3, "个", "单班次滑丝超 3 个需更换螺丝刀头");
        AddSampleCounterAlarm(d9, "维护计数", "D383", 2500, "次", "累计运行达 2500 次需保养");
        devices.Add(d9);

        // ── 设备 10：检测机D2（中量配置：6 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D19x，报警 M19x，缺陷 D896 起偶数步进，计数 D39x，配方 D518
        var d10 = new Device
        {
            Name = "检测机D2",
            TargetCycle = 750,
            OkCountAddress = "D190",
            NgCountAddress = "D192",
            StatusCountAddress = "D194",
            ProductionResetAddress = "D196",
            RecipeName = "检测方案-高精度",
            RecipeValue = 10,
            RecipeAddress = "D518",
        };
        AddSampleAlarm(d10, "相机断开", "M190", AlarmLevel.High, "工业相机连接中断");
        AddSampleAlarm(d10, "光源故障", "M191", AlarmLevel.Medium, "光源亮度异常");
        AddSampleAlarm(d10, "镜头污染", "M192", AlarmLevel.Low, "镜头表面有异物");
        AddSampleAlarm(d10, "传送带卡阻", "M193", AlarmLevel.High, "传送带运行阻力异常");
        AddSampleAlarm(d10, "检测超时", "M194", AlarmLevel.Medium, "单件检测时间超过 2 秒");
        AddSampleAlarm(d10, "工位未到位", "M195", AlarmLevel.Medium, "分度工位未到位");
        AddSampleDefect(d10, "划痕", "D896", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d10, "尺寸超差", "D898", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d10, "脏污", "D900", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d10, "变形", "D902", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d10, "缺件", "D904", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCounterAlarm(d10, "NG连续", "D390", 4, "个", "连续 NG 超过 4 个需复检标定");
        AddSampleCounterAlarm(d10, "复检次数", "D391", 2, "次", "单班次复检超 2 次需校准");
        AddSampleCounterAlarm(d10, "设备保养计数", "D392", 600, "h", "累计运行 600 小时需保养");
        AddSampleCounterAlarm(d10, "剔除次数", "D393", 30, "次", "单班次剔除超 30 次需检查剔除机构");
        devices.Add(d10);

        // ── 设备 11：CNC加工中心E1（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // 主地址 D40x，报警 M20x，缺陷 D920 起偶数步进，计数 D70x，配方 D520
        var d11 = new Device
        {
            Name = "CNC加工中心E1",
            TargetCycle = 200,
            OkCountAddress = "D400",
            NgCountAddress = "D402",
            StatusCountAddress = "D404",
            ProductionResetAddress = "D406",
            RecipeName = "加工程序-E1",
            RecipeValue = 11,
            RecipeAddress = "D520",
        };
        AddSampleAlarm(d11, "主轴过热", "M200", AlarmLevel.High, "主轴温度超过 75°C，需停机冷却");
        AddSampleAlarm(d11, "刀具断裂", "M201", AlarmLevel.High, "刀具断裂检测触发，立即停机");
        AddSampleAlarm(d11, "润滑油不足", "M202", AlarmLevel.Medium, "主轴润滑油位低");
        AddSampleAlarm(d11, "气压低", "M203", AlarmLevel.Medium, "气动夹具供气压力低于 0.5MPa");
        AddSampleAlarm(d11, "伺服报警", "M204", AlarmLevel.High, "伺服驱动器报警代码 0x32");
        AddSampleAlarm(d11, "急停按下", "M205", AlarmLevel.High, "急停按钮被按下，所有动作停止");
        AddSampleAlarm(d11, "冷却液断流", "M206", AlarmLevel.Medium, "冷却液流量低于阈值");
        AddSampleAlarm(d11, "排屑器卡阻", "M207", AlarmLevel.Low, "排屑器运行阻力异常");
        AddSampleDefect(d11, "尺寸超差", "D920", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d11, "表面粗糙", "D922", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d11, "毛刺", "D924", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d11, "振纹", "D926", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d11, "碰伤", "D928", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d11, "位置度超差", "D930", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleCounterAlarm(d11, "刀具寿命计数", "D700", 0, "次", "刀具使用次数（阈值 0 表示仅记录）");
        AddSampleCounterAlarm(d11, "主轴保养计数", "D701", 2000, "h", "累计运行 2000 小时保养主轴");
        AddSampleCounterAlarm(d11, "连续NG次数", "D702", 3, "个", "连续 NG 超过 3 个需更换刀具");
        AddSampleCounterAlarm(d11, "急停次数", "D703", 2, "次", "单班次急停超 2 次需检查");
        AddSampleCounterAlarm(d11, "班次产量", "D704", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d11);

        // ── 设备 12：包装机F1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D41x，报警 M21x，缺陷 D932 起偶数步进，计数 D71x，配方 D522
        var d12 = new Device
        {
            Name = "包装机F1",
            TargetCycle = 120,
            OkCountAddress = "D410",
            NgCountAddress = "D412",
            StatusCountAddress = "D414",
            ProductionResetAddress = "D416",
            RecipeName = "包装方案-F1",
            RecipeValue = 12,
            RecipeAddress = "D522",
        };
        AddSampleAlarm(d12, "薄膜断料", "M210", AlarmLevel.High, "包装膜断裂或用尽，需更换膜卷");
        AddSampleAlarm(d12, "封切温度异常", "M211", AlarmLevel.High, "封切温度超出设定范围 ±10°C");
        AddSampleAlarm(d12, "气压低", "M212", AlarmLevel.Medium, "气动元件供气压力低于 0.4MPa");
        AddSampleAlarm(d12, "伺服报警", "M213", AlarmLevel.High, "伺服驱动器报警代码 0x15");
        AddSampleAlarm(d12, "色标丢失", "M214", AlarmLevel.Medium, "色标传感器未检测到色标，可能跑偏");
        AddSampleAlarm(d12, "输送带卡阻", "M215", AlarmLevel.Low, "输送带运行阻力异常");
        AddSampleDefect(d12, "封口不良", "D932", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d12, "标签偏移", "D934", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleDefect(d12, "包装破损", "D936", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d12, "漏封", "D938", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleCounterAlarm(d12, "连续NG次数", "D710", 5, "个", "连续 NG 超过 5 个需检查封切机构");
        AddSampleCounterAlarm(d12, "维护计数", "D711", 1500, "次", "累计运行达 1500 次需保养");
        AddSampleCounterAlarm(d12, "膜卷更换计数", "D712", 0, "卷", "膜卷使用计数（阈值 0 表示仅记录）");
        AddSampleCounterAlarm(d12, "班次产量", "D713", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d12);

        // ── 设备 13：注塑机A4（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D42x，报警 M22x，缺陷 D940 起偶数步进，计数 D72x，配方 D524
        var d13 = new Device
        {
            Name = "注塑机A4",
            TargetCycle = 620,
            OkCountAddress = "D420",
            NgCountAddress = "D422",
            StatusCountAddress = "D424",
            ProductionResetAddress = "D426",
            RecipeName = "配方A-环保",
            RecipeValue = 13,
            RecipeAddress = "D524",
        };
        AddSampleAlarm(d13, "温度过高", "M220", AlarmLevel.High, "模温超过设定上限 80°C");
        AddSampleAlarm(d13, "压力低", "M221", AlarmLevel.Medium, "液压系统压力低于 5MPa");
        AddSampleAlarm(d13, "料斗空", "M222", AlarmLevel.Low, "料斗内原料不足");
        AddSampleAlarm(d13, "模具未关", "M223", AlarmLevel.High, "安全门未关闭");
        AddSampleAlarm(d13, "螺杆异常", "M224", AlarmLevel.Medium, "螺杆转动阻力异常");
        AddSampleAlarm(d13, "加热圈断路", "M225", AlarmLevel.High, "一段加热圈开路");
        AddSampleAlarm(d13, "锁模力不足", "M226", AlarmLevel.Medium, "锁模力低于设定值 80%");
        AddSampleDefect(d13, "飞边", "D940", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d13, "尺寸偏小", "D942", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d13, "缩水", "D944", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d13, "气泡", "D946", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d13, "黑点", "D948", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCounterAlarm(d13, "连续NG次数", "D720", 9, "个", "连续 NG 超过 9 个时停机检查");
        AddSampleCounterAlarm(d13, "停机次数", "D721", 3, "次", "班次内异常停机超 3 次需检修");
        AddSampleCounterAlarm(d13, "模具保养计数", "D722", 9000, "模次", "累计模次达 9000 需保养");
        AddSampleCounterAlarm(d13, "班次产量", "D723", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d13);

        // ── 设备 14：焊接机B4（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D43x，报警 M23x，缺陷 D950 起偶数步进，计数 D73x，配方 D526
        var d14 = new Device
        {
            Name = "焊接机B4",
            TargetCycle = 360,
            OkCountAddress = "D430",
            NgCountAddress = "D432",
            StatusCountAddress = "D434",
            ProductionResetAddress = "D436",
            RecipeName = "焊接参数-精密",
            RecipeValue = 14,
            RecipeAddress = "D526",
        };
        AddSampleAlarm(d14, "焊头过热", "M230", AlarmLevel.High, "焊头温度超过 600°C");
        AddSampleAlarm(d14, "气压不足", "M231", AlarmLevel.Medium, "压缩空气压力低于 0.4MPa");
        AddSampleAlarm(d14, "电极磨损", "M232", AlarmLevel.Low, "电极帽磨损达到阈值");
        AddSampleAlarm(d14, "冷却水断流", "M233", AlarmLevel.High, "冷却水流量低于阈值");
        AddSampleAlarm(d14, "焊接超时", "M234", AlarmLevel.Medium, "单点焊接时间超过 3 秒");
        AddSampleAlarm(d14, "气动阀卡死", "M235", AlarmLevel.Low, "气动换向阀响应超时");
        AddSampleDefect(d14, "虚焊", "D950", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d14, "焊疤过大", "D952", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d14, "焊偏", "D954", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d14, "气孔", "D956", DefectSeverity.Major, DefectCategory.Function);
        AddSampleCounterAlarm(d14, "虚焊次数", "D730", 3, "次", "单班次虚焊次数超 3 次需校准");
        AddSampleCounterAlarm(d14, "维护计数", "D731", 6000, "次", "累计焊接次数达 6000 次需保养");
        AddSampleCounterAlarm(d14, "电极磨损计数", "D732", 900, "次", "电极焊接达 900 次需修磨");
        AddSampleCounterAlarm(d14, "班次产量", "D733", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d14);

        // ── 设备 15：装配机C3（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D44x，报警 M24x，缺陷 D958 起偶数步进，计数 D74x，配方 D528
        var d15 = new Device
        {
            Name = "装配机C3",
            TargetCycle = 260,
            OkCountAddress = "D440",
            NgCountAddress = "D442",
            StatusCountAddress = "D444",
            ProductionResetAddress = "D446",
            RecipeName = "装配方案-C3",
            RecipeValue = 15,
            RecipeAddress = "D528",
        };
        AddSampleAlarm(d15, "电机过载", "M240", AlarmLevel.High, "伺服电机电流超过额定值 150%");
        AddSampleAlarm(d15, "气压低", "M241", AlarmLevel.Medium, "气动元件供气压力低于 0.5MPa");
        AddSampleAlarm(d15, "螺丝用尽", "M242", AlarmLevel.Low, "螺丝供料器内剩余数量不足");
        AddSampleAlarm(d15, "夹具松动", "M243", AlarmLevel.Medium, "夹具夹紧力低于阈值");
        AddSampleAlarm(d15, "位置超差", "M244", AlarmLevel.High, "伺服定位偏差超过 ±0.1mm");
        AddSampleAlarm(d15, "传感器故障", "M245", AlarmLevel.High, "位置传感器无信号");
        AddSampleAlarm(d15, "气缸卡阻", "M246", AlarmLevel.Medium, "气缸动作超时");
        AddSampleDefect(d15, "错件", "D958", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d15, "漏装", "D960", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d15, "浮高", "D962", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d15, "滑丝", "D964", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d15, "错位", "D966", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleCounterAlarm(d15, "错件次数", "D740", 0, "个", "错件计数（阈值 0 表示立即停机）");
        AddSampleCounterAlarm(d15, "漏装次数", "D741", 0, "个", "漏装计数（阈值 0 表示立即停机）");
        AddSampleCounterAlarm(d15, "滑丝次数", "D742", 4, "个", "单班次滑丝超 4 个需更换螺丝刀头");
        AddSampleCounterAlarm(d15, "维护计数", "D743", 3500, "次", "累计运行达 3500 次需保养");
        devices.Add(d15);

        // ── 设备 16：检测机D3（中量配置：6 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D45x，报警 M25x，缺陷 D968 起偶数步进，计数 D75x，配方 D530
        var d16 = new Device
        {
            Name = "检测机D3",
            TargetCycle = 700,
            OkCountAddress = "D450",
            NgCountAddress = "D452",
            StatusCountAddress = "D454",
            ProductionResetAddress = "D456",
            RecipeName = "检测方案-高速",
            RecipeValue = 16,
            RecipeAddress = "D530",
        };
        AddSampleAlarm(d16, "相机断开", "M250", AlarmLevel.High, "工业相机连接中断");
        AddSampleAlarm(d16, "光源故障", "M251", AlarmLevel.Medium, "光源亮度异常");
        AddSampleAlarm(d16, "镜头污染", "M252", AlarmLevel.Low, "镜头表面有异物");
        AddSampleAlarm(d16, "传送带卡阻", "M253", AlarmLevel.High, "传送带运行阻力异常");
        AddSampleAlarm(d16, "检测超时", "M254", AlarmLevel.Medium, "单件检测时间超过 2 秒");
        AddSampleAlarm(d16, "工位未到位", "M255", AlarmLevel.Medium, "分度工位未到位");
        AddSampleDefect(d16, "划痕", "D968", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d16, "尺寸超差", "D970", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d16, "脏污", "D972", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d16, "变形", "D974", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d16, "错件", "D976", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCounterAlarm(d16, "NG连续", "D750", 6, "个", "连续 NG 超过 6 个需复检标定");
        AddSampleCounterAlarm(d16, "复检次数", "D751", 4, "次", "单班次复检超 4 次需校准");
        AddSampleCounterAlarm(d16, "设备保养计数", "D752", 800, "h", "累计运行 800 小时需保养");
        AddSampleCounterAlarm(d16, "剔除次数", "D753", 40, "次", "单班次剔除超 40 次需检查剔除机构");
        devices.Add(d16);

        // ── 设备 17：CNC加工中心E2（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // 主地址 D46x，报警 M26x，缺陷 D978 起偶数步进，计数 D76x，配方 D532
        var d17 = new Device
        {
            Name = "CNC加工中心E2",
            TargetCycle = 180,
            OkCountAddress = "D460",
            NgCountAddress = "D462",
            StatusCountAddress = "D464",
            ProductionResetAddress = "D466",
            RecipeName = "加工程序-E2",
            RecipeValue = 17,
            RecipeAddress = "D532",
        };
        AddSampleAlarm(d17, "主轴过热", "M260", AlarmLevel.High, "主轴温度超过 75°C，需停机冷却");
        AddSampleAlarm(d17, "刀具断裂", "M261", AlarmLevel.High, "刀具断裂检测触发，立即停机");
        AddSampleAlarm(d17, "润滑油不足", "M262", AlarmLevel.Medium, "主轴润滑油位低");
        AddSampleAlarm(d17, "气压低", "M263", AlarmLevel.Medium, "气动夹具供气压力低于 0.5MPa");
        AddSampleAlarm(d17, "伺服报警", "M264", AlarmLevel.High, "伺服驱动器报警代码 0x41");
        AddSampleAlarm(d17, "急停按下", "M265", AlarmLevel.High, "急停按钮被按下，所有动作停止");
        AddSampleAlarm(d17, "冷却液断流", "M266", AlarmLevel.Medium, "冷却液流量低于阈值");
        AddSampleAlarm(d17, "刀库异常", "M267", AlarmLevel.Low, "刀库换刀动作超时");
        AddSampleDefect(d17, "尺寸超差", "D978", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d17, "表面粗糙", "D980", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d17, "毛刺", "D982", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d17, "振纹", "D984", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d17, "碰伤", "D986", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d17, "同轴度超差", "D988", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleCounterAlarm(d17, "刀具寿命计数", "D760", 0, "次", "刀具使用次数（阈值 0 表示仅记录）");
        AddSampleCounterAlarm(d17, "主轴保养计数", "D761", 1800, "h", "累计运行 1800 小时保养主轴");
        AddSampleCounterAlarm(d17, "连续NG次数", "D762", 2, "个", "连续 NG 超过 2 个需更换刀具");
        AddSampleCounterAlarm(d17, "急停次数", "D763", 1, "次", "单班次急停超 1 次需检查");
        AddSampleCounterAlarm(d17, "班次产量", "D764", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d17);

        // ── 设备 18：包装机F2（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D47x，报警 M27x，缺陷 D990 起偶数步进，计数 D77x，配方 D534
        var d18 = new Device
        {
            Name = "包装机F2",
            TargetCycle = 100,
            OkCountAddress = "D470",
            NgCountAddress = "D472",
            StatusCountAddress = "D474",
            ProductionResetAddress = "D476",
            RecipeName = "包装方案-F2",
            RecipeValue = 18,
            RecipeAddress = "D534",
        };
        AddSampleAlarm(d18, "薄膜断料", "M270", AlarmLevel.High, "包装膜断裂或用尽，需更换膜卷");
        AddSampleAlarm(d18, "封切温度异常", "M271", AlarmLevel.High, "封切温度超出设定范围 ±10°C");
        AddSampleAlarm(d18, "气压低", "M272", AlarmLevel.Medium, "气动元件供气压力低于 0.4MPa");
        AddSampleAlarm(d18, "伺服报警", "M273", AlarmLevel.High, "伺服驱动器报警代码 0x22");
        AddSampleAlarm(d18, "色标丢失", "M274", AlarmLevel.Medium, "色标传感器未检测到色标");
        AddSampleAlarm(d18, "输送带卡阻", "M275", AlarmLevel.Low, "输送带运行阻力异常");
        AddSampleDefect(d18, "封口不良", "D990", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d18, "标签偏移", "D992", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleDefect(d18, "包装破损", "D994", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d18, "漏封", "D996", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleCounterAlarm(d18, "连续NG次数", "D770", 4, "个", "连续 NG 超过 4 个需检查封切机构");
        AddSampleCounterAlarm(d18, "维护计数", "D771", 1200, "次", "累计运行达 1200 次需保养");
        AddSampleCounterAlarm(d18, "膜卷更换计数", "D772", 0, "卷", "膜卷使用计数（阈值 0 表示仅记录）");
        AddSampleCounterAlarm(d18, "班次产量", "D773", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d18);

        // ── 设备 19：激光打标机G1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D48x，报警 M28x，缺陷 D998 起偶数步进，计数 D78x，配方 D536
        var d19 = new Device
        {
            Name = "激光打标机G1",
            TargetCycle = 150,
            OkCountAddress = "D480",
            NgCountAddress = "D482",
            StatusCountAddress = "D484",
            ProductionResetAddress = "D486",
            RecipeName = "打标方案-G1",
            RecipeValue = 19,
            RecipeAddress = "D536",
        };
        AddSampleAlarm(d19, "激光功率异常", "M280", AlarmLevel.High, "激光功率超出设定范围 ±10%");
        AddSampleAlarm(d19, "冷却水断流", "M281", AlarmLevel.High, "冷却水流量低于阈值，可能烧坏激光器");
        AddSampleAlarm(d19, "温度过高", "M282", AlarmLevel.Medium, "激光器温度超过 40°C");
        AddSampleAlarm(d19, "振镜报警", "M283", AlarmLevel.High, "振镜驱动器报警，打标动作停止");
        AddSampleAlarm(d19, "气压低", "M284", AlarmLevel.Low, "保护气压力低于阈值");
        AddSampleAlarm(d19, "标定丢失", "M285", AlarmLevel.Medium, "标定数据丢失，需重新标定");
        AddSampleDefect(d19, "标记缺失", "D998", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d19, "标记模糊", "D1000", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d19, "位置偏移", "D1002", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d19, "标记深浅不一", "D1004", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCounterAlarm(d19, "连续NG次数", "D780", 3, "个", "连续 NG 超过 3 个需检查激光器");
        AddSampleCounterAlarm(d19, "维护计数", "D781", 2500, "次", "累计运行达 2500 次需保养");
        AddSampleCounterAlarm(d19, "激光器寿命计数", "D782", 0, "h", "激光器使用小时数（阈值 0 表示仅记录）");
        AddSampleCounterAlarm(d19, "班次产量", "D783", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d19);

        // ── 设备 20：清洗机H1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D49x，报警 M29x，缺陷 D1006 起偶数步进，计数 D79x，配方 D538
        var d20 = new Device
        {
            Name = "清洗机H1",
            TargetCycle = 240,
            OkCountAddress = "D490",
            NgCountAddress = "D492",
            StatusCountAddress = "D494",
            ProductionResetAddress = "D496",
            RecipeName = "清洗方案-H1",
            RecipeValue = 20,
            RecipeAddress = "D538",
        };
        AddSampleAlarm(d20, "加热器故障", "M290", AlarmLevel.High, "加热器断路，温度无法上升");
        AddSampleAlarm(d20, "温度过低", "M291", AlarmLevel.Medium, "清洗液温度低于设定值 60°C");
        AddSampleAlarm(d20, "喷淋压力低", "M292", AlarmLevel.Medium, "喷淋压力低于 0.3MPa");
        AddSampleAlarm(d20, "液位低", "M293", AlarmLevel.Low, "清洗液液位低于阈值");
        AddSampleAlarm(d20, "过滤器堵塞", "M294", AlarmLevel.Medium, "过滤器压差超阈值，需更换滤芯");
        AddSampleAlarm(d20, "传送卡阻", "M295", AlarmLevel.High, "传送链卡阻，可能卡料");
        AddSampleDefect(d20, "残留异物", "D1006", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d20, "水印", "D1008", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d20, "氧化", "D1010", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d20, "划伤", "D1012", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleCounterAlarm(d20, "连续NG次数", "D790", 5, "个", "连续 NG 超过 5 个需检查喷淋");
        AddSampleCounterAlarm(d20, "维护计数", "D791", 1800, "次", "累计运行达 1800 次需保养");
        AddSampleCounterAlarm(d20, "滤芯更换计数", "D792", 500, "h", "累计运行 500 小时需更换滤芯");
        AddSampleCounterAlarm(d20, "班次产量", "D793", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d20);

        AssertNoDuplicateAddresses(devices);
        return devices;
    }

    /// <summary>
    /// 校验所有设备的 PLC 地址（主地址 + 配方 + 报警 + 缺陷 + 计数报警）跨设备唯一。
    /// 若发现重复，抛出 InvalidOperationException 并列出所有重复地址，防止虚拟数据污染预览。
    /// </summary>
    private static void AssertNoDuplicateAddresses(List<Device> devices)
    {
        var addressMap = new Dictionary<string, string>(StringComparer.Ordinal); // address -> "设备名.字段"
        List<string> duplicates = [];

        void Add(string? addr, string source)
        {
            if (string.IsNullOrWhiteSpace(addr)) return;
            if (addressMap.TryGetValue(addr, out var existing))
                duplicates.Add($"  {addr}：{existing} ↔ {source}");
            else
                addressMap[addr] = source;
        }

        // 三菱 D 区 Int32 占 2 字：登记起始地址及其高字，避免 D800 与 D801 被当成互不相关。
        void AddInt32(string? addr, string source)
        {
            if (string.IsNullOrWhiteSpace(addr)) return;
            var parsed = PlcAddressParser.Parse(addr);
            if (!parsed.IsValid || parsed.Type != PlcAddressType.DWord || parsed.AddressStride < 2)
            {
                Add(addr, source);
                return;
            }

            for (var i = 0; i < parsed.AddressStride; i++)
                Add($"{parsed.AddressGroup}{parsed.AddressOffset + i}", i == 0 ? source : $"{source}+{i}");
        }

        foreach (var d in devices)
        {
            AddInt32(d.OkCountAddress, $"{d.Name}.OkCount");
            AddInt32(d.NgCountAddress, $"{d.Name}.NgCount");
            AddInt32(d.StatusCountAddress, $"{d.Name}.StatusCount");
            AddInt32(d.ProductionResetAddress, $"{d.Name}.ProductionReset");
            AddInt32(d.RecipeAddress, $"{d.Name}.Recipe");
            foreach (var a in d.Alarms)
                Add(a.PlcAddress, $"{d.Name}.Alarm[{a.Name}]");
            foreach (var def in d.Defects)
                AddInt32(def.PlcAddress, $"{d.Name}.Defect[{def.Name}]");
            foreach (var c in d.CounterAlarms)
                Add(c.PlcAddress, $"{d.Name}.CounterAlarm[{c.Name}]");
        }

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"虚拟数据存在重复 PLC 地址（{duplicates.Count} 处）：\n{string.Join("\n", duplicates)}");
    }

    private static void AddSampleAlarm(Device d, string name, string addr, AlarmLevel level, string desc)
    {
        // 先设 DeviceId 再设 PlcAddress，确保 OnPlcAddressChanged 能基于新 DeviceId 生成确定性 Id
        d.Alarms.Add(new Alarm
        {
            DeviceId = d.Id,
            Name = name,
            PlcAddress = addr,
            Level = level,
            Description = desc,
        });
    }

    private static void AddSampleDefect(Device d, string name, string addr, DefectSeverity sev, DefectCategory cat)
    {
        d.Defects.Add(new Defect
        {
            DeviceId = d.Id,
            Name = name,
            PlcAddress = addr,
            Severity = sev,
            Category = cat,
        });
    }

    private static void AddSampleCounterAlarm(Device d, string name, string addr, int max, string unit, string desc)
    {
        d.CounterAlarms.Add(new CounterAlarm
        {
            DeviceId = d.Id,
            Name = name,
            PlcAddress = addr,
            MaxValue = max,
            Unit = unit,
            Description = desc,
            Enabled = true,
        });
    }
}
