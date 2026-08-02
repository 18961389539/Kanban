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
    ///   - 缺陷地址：d1-d10 用 D200-D294；d11/d12 用 D60x/D61x；d13-d20 用 D62x-D69x
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
        AddSampleDefect(d1, "划痕", "D200", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "尺寸偏大", "D201", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d1, "飞边", "D202", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d1, "缩水", "D203", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "气泡", "D204", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d1, "黑点", "D205", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCountAlarm(d1, "连续NG次数", "D300", 10, "个", "连续 NG 超过 10 个时停机检查模具");
        AddSampleCountAlarm(d1, "停机次数", "D301", 5, "次", "班次内异常停机超过 5 次需检修");
        AddSampleCountAlarm(d1, "模具保养计数", "D302", 10000, "模次", "累计模次达 1 万需保养模具");
        AddSampleCountAlarm(d1, "班次产量", "D303", 0, "件", "班次产量计数（阈值 0 表示仅记录不停机）");
        AddSampleCountAlarm(d1, "能耗累计", "D304", 0, "kWh", "能耗累计（阈值 0 表示仅记录不停机）");
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
        AddSampleDefect(d2, "色差", "D210", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d2, "气泡", "D211", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d2, "黑点", "D212", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d2, "尺寸偏小", "D213", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d2, "缩水", "D214", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleCountAlarm(d2, "连续NG次数", "D310", 8, "个", "连续 NG 超过 8 个时停机检查");
        AddSampleCountAlarm(d2, "维护计数", "D311", 2000, "次", "累计注塑次数达到 2000 次需保养");
        AddSampleCountAlarm(d2, "能耗累计", "D312", 0, "kWh", "能耗累计（阈值 0 表示仅记录不停机）");
        AddSampleCountAlarm(d2, "模具保养计数", "D313", 8000, "模次", "累计模次达 8000 需保养模具");
        devices.Add(d2);

        // ── 设备 3：焊接机B1（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // PLC 地址用 D12x 段（避开 d1 的缺陷 D200-D205、计数 D300-D304）
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
        AddSampleDefect(d3, "虚焊", "D220", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d3, "焊疤过大", "D221", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d3, "焊穿", "D222", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d3, "焊偏", "D223", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d3, "气孔", "D224", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d3, "裂纹", "D225", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCountAlarm(d3, "虚焊次数", "D320", 3, "次", "单班次虚焊次数超过 3 次需校准参数");
        AddSampleCountAlarm(d3, "维护计数", "D321", 5000, "次", "累计焊接次数达到 5000 次需保养");
        AddSampleCountAlarm(d3, "电极磨损计数", "D322", 800, "次", "电极焊接达 800 次需修磨");
        AddSampleCountAlarm(d3, "焊穿次数", "D323", 2, "次", "单班次焊穿超 2 次需停机");
        AddSampleCountAlarm(d3, "电流异常次数", "D324", 3, "次", "单班次电流异常超 3 次需检修");
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
        AddSampleDefect(d4, "脱焊", "D230", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d4, "虚焊", "D231", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d4, "焊疤过大", "D232", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d4, "气孔", "D233", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d4, "裂纹", "D234", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCountAlarm(d4, "脱焊次数", "D330", 2, "次", "单班次脱焊次数超 2 次需停机");
        AddSampleCountAlarm(d4, "维护计数", "D331", 1000, "次", "累计焊接次数达到 1000 次需保养");
        AddSampleCountAlarm(d4, "虚焊次数", "D332", 5, "次", "单班次虚焊次数超 5 次需校准");
        AddSampleCountAlarm(d4, "电流异常次数", "D333", 3, "次", "单班次电流异常超 3 次需停机");
        AddSampleCountAlarm(d4, "电极磨损计数", "D334", 600, "次", "电极焊接达 600 次需修磨");
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
        AddSampleDefect(d5, "错件", "D240", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d5, "漏装", "D241", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d5, "浮高", "D242", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d5, "滑丝", "D243", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d5, "错位", "D244", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d5, "损伤", "D245", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCountAlarm(d5, "错件次数", "D340", 0, "个", "错件计数（阈值 0 表示任意错件均需立即停机）");
        AddSampleCountAlarm(d5, "漏装次数", "D341", 0, "个", "漏装计数（阈值 0 表示任意漏装均需立即停机）");
        AddSampleCountAlarm(d5, "滑丝次数", "D342", 5, "个", "单班次滑丝次数超 5 个需更换螺丝刀头");
        AddSampleCountAlarm(d5, "维护计数", "D343", 3000, "次", "累计运行达 3000 次需保养");
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
        AddSampleDefect(d6, "划痕", "D250", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d6, "尺寸超差", "D251", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d6, "脏污", "D252", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d6, "变形", "D253", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d6, "缺件", "D254", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d6, "错件", "D255", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCountAlarm(d6, "NG连续", "D350", 5, "个", "连续 NG 超过 5 个需复检相机标定");
        AddSampleCountAlarm(d6, "复检次数", "D351", 3, "次", "单班次复检超 3 次需校准检测算法");
        AddSampleCountAlarm(d6, "设备保养计数", "D352", 720, "h", "累计运行 720 小时需保养");
        AddSampleCountAlarm(d6, "剔除次数", "D353", 50, "次", "单班次剔除超 50 次需检查剔除机构");
        devices.Add(d6);

        // ── 设备 7：注塑机A3（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D16x，报警 M16x，缺陷 D26x，计数 D36x，配方 D512（避开 d1-d6 的地址段）
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
        AddSampleDefect(d7, "飞边", "D260", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d7, "尺寸偏大", "D261", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d7, "缩水", "D262", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d7, "气泡", "D263", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d7, "黑点", "D264", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCountAlarm(d7, "连续NG次数", "D360", 8, "个", "连续 NG 超过 8 个时停机检查");
        AddSampleCountAlarm(d7, "停机次数", "D361", 4, "次", "班次内异常停机超 4 次需检修");
        AddSampleCountAlarm(d7, "模具保养计数", "D362", 12000, "模次", "累计模次达 1.2 万需保养");
        AddSampleCountAlarm(d7, "班次产量", "D363", 0, "件", "班次产量计数（阈值 0 表示仅记录不停机）");
        devices.Add(d7);

        // ── 设备 8：焊接机B3（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D17x，报警 M17x，缺陷 D27x，计数 D37x，配方 D514
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
        AddSampleDefect(d8, "虚焊", "D270", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d8, "焊疤过大", "D271", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d8, "焊穿", "D272", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d8, "焊偏", "D273", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleCountAlarm(d8, "虚焊次数", "D370", 4, "次", "单班次虚焊次数超 4 次需校准");
        AddSampleCountAlarm(d8, "维护计数", "D371", 4000, "次", "累计焊接次数达 4000 次需保养");
        AddSampleCountAlarm(d8, "电极磨损计数", "D372", 700, "次", "电极焊接达 700 次需修磨");
        AddSampleCountAlarm(d8, "焊穿次数", "D373", 1, "次", "单班次焊穿超 1 次需停机检修");
        devices.Add(d8);

        // ── 设备 9：装配机C2（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D18x，报警 M18x，缺陷 D28x，计数 D38x，配方 D516
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
        AddSampleDefect(d9, "错件", "D280", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d9, "漏装", "D281", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d9, "浮高", "D282", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d9, "滑丝", "D283", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d9, "损伤", "D284", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCountAlarm(d9, "错件次数", "D380", 0, "个", "错件计数（阈值 0 表示立即停机）");
        AddSampleCountAlarm(d9, "漏装次数", "D381", 0, "个", "漏装计数（阈值 0 表示立即停机）");
        AddSampleCountAlarm(d9, "滑丝次数", "D382", 3, "个", "单班次滑丝超 3 个需更换螺丝刀头");
        AddSampleCountAlarm(d9, "维护计数", "D383", 2500, "次", "累计运行达 2500 次需保养");
        devices.Add(d9);

        // ── 设备 10：检测机D2（中量配置：6 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D19x，报警 M19x，缺陷 D29x，计数 D39x，配方 D518
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
        AddSampleDefect(d10, "划痕", "D290", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d10, "尺寸超差", "D291", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d10, "脏污", "D292", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d10, "变形", "D293", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d10, "缺件", "D294", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCountAlarm(d10, "NG连续", "D390", 4, "个", "连续 NG 超过 4 个需复检标定");
        AddSampleCountAlarm(d10, "复检次数", "D391", 2, "次", "单班次复检超 2 次需校准");
        AddSampleCountAlarm(d10, "设备保养计数", "D392", 600, "h", "累计运行 600 小时需保养");
        AddSampleCountAlarm(d10, "剔除次数", "D393", 30, "次", "单班次剔除超 30 次需检查剔除机构");
        devices.Add(d10);

        // ── 设备 11：CNC加工中心E1（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // 主地址 D40x（跳过 D200-D399 缺陷/计数段），报警 M20x，缺陷 D60x，计数 D70x，配方 D520
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
        AddSampleDefect(d11, "尺寸超差", "D600", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d11, "表面粗糙", "D601", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d11, "毛刺", "D602", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d11, "振纹", "D603", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d11, "碰伤", "D604", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d11, "位置度超差", "D605", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleCountAlarm(d11, "刀具寿命计数", "D700", 0, "次", "刀具使用次数（阈值 0 表示仅记录）");
        AddSampleCountAlarm(d11, "主轴保养计数", "D701", 2000, "h", "累计运行 2000 小时保养主轴");
        AddSampleCountAlarm(d11, "连续NG次数", "D702", 3, "个", "连续 NG 超过 3 个需更换刀具");
        AddSampleCountAlarm(d11, "急停次数", "D703", 2, "次", "单班次急停超 2 次需检查");
        AddSampleCountAlarm(d11, "班次产量", "D704", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d11);

        // ── 设备 12：包装机F1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D41x，报警 M21x，缺陷 D61x，计数 D71x，配方 D522
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
        AddSampleDefect(d12, "封口不良", "D610", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d12, "标签偏移", "D611", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleDefect(d12, "包装破损", "D612", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d12, "漏封", "D613", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleCountAlarm(d12, "连续NG次数", "D710", 5, "个", "连续 NG 超过 5 个需检查封切机构");
        AddSampleCountAlarm(d12, "维护计数", "D711", 1500, "次", "累计运行达 1500 次需保养");
        AddSampleCountAlarm(d12, "膜卷更换计数", "D712", 0, "卷", "膜卷使用计数（阈值 0 表示仅记录）");
        AddSampleCountAlarm(d12, "班次产量", "D713", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d12);

        // ── 设备 13：注塑机A4（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D42x，报警 M22x，缺陷 D62x，计数 D72x，配方 D524
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
        AddSampleDefect(d13, "飞边", "D620", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d13, "尺寸偏小", "D621", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d13, "缩水", "D622", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d13, "气泡", "D623", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d13, "黑点", "D624", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleCountAlarm(d13, "连续NG次数", "D720", 9, "个", "连续 NG 超过 9 个时停机检查");
        AddSampleCountAlarm(d13, "停机次数", "D721", 3, "次", "班次内异常停机超 3 次需检修");
        AddSampleCountAlarm(d13, "模具保养计数", "D722", 9000, "模次", "累计模次达 9000 需保养");
        AddSampleCountAlarm(d13, "班次产量", "D723", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d13);

        // ── 设备 14：焊接机B4（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D43x，报警 M23x，缺陷 D63x，计数 D73x，配方 D526
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
        AddSampleDefect(d14, "虚焊", "D630", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d14, "焊疤过大", "D631", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d14, "焊偏", "D632", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d14, "气孔", "D633", DefectSeverity.Major, DefectCategory.Function);
        AddSampleCountAlarm(d14, "虚焊次数", "D730", 3, "次", "单班次虚焊次数超 3 次需校准");
        AddSampleCountAlarm(d14, "维护计数", "D731", 6000, "次", "累计焊接次数达 6000 次需保养");
        AddSampleCountAlarm(d14, "电极磨损计数", "D732", 900, "次", "电极焊接达 900 次需修磨");
        AddSampleCountAlarm(d14, "班次产量", "D733", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d14);

        // ── 设备 15：装配机C3（重配置：7 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D44x，报警 M24x，缺陷 D64x，计数 D74x，配方 D528
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
        AddSampleDefect(d15, "错件", "D640", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d15, "漏装", "D641", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleDefect(d15, "浮高", "D642", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d15, "滑丝", "D643", DefectSeverity.Major, DefectCategory.Function);
        AddSampleDefect(d15, "错位", "D644", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleCountAlarm(d15, "错件次数", "D740", 0, "个", "错件计数（阈值 0 表示立即停机）");
        AddSampleCountAlarm(d15, "漏装次数", "D741", 0, "个", "漏装计数（阈值 0 表示立即停机）");
        AddSampleCountAlarm(d15, "滑丝次数", "D742", 4, "个", "单班次滑丝超 4 个需更换螺丝刀头");
        AddSampleCountAlarm(d15, "维护计数", "D743", 3500, "次", "累计运行达 3500 次需保养");
        devices.Add(d15);

        // ── 设备 16：检测机D3（中量配置：6 报警 + 5 缺陷 + 4 计数报警） ──
        // 主地址 D45x，报警 M25x，缺陷 D65x，计数 D75x，配方 D530
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
        AddSampleDefect(d16, "划痕", "D650", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d16, "尺寸超差", "D651", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d16, "脏污", "D652", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d16, "变形", "D653", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d16, "错件", "D654", DefectSeverity.Critical, DefectCategory.Function);
        AddSampleCountAlarm(d16, "NG连续", "D750", 6, "个", "连续 NG 超过 6 个需复检标定");
        AddSampleCountAlarm(d16, "复检次数", "D751", 4, "次", "单班次复检超 4 次需校准");
        AddSampleCountAlarm(d16, "设备保养计数", "D752", 800, "h", "累计运行 800 小时需保养");
        AddSampleCountAlarm(d16, "剔除次数", "D753", 40, "次", "单班次剔除超 40 次需检查剔除机构");
        devices.Add(d16);

        // ── 设备 17：CNC加工中心E2（重配置：8 报警 + 6 缺陷 + 5 计数报警） ──
        // 主地址 D46x，报警 M26x，缺陷 D66x，计数 D76x，配方 D532
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
        AddSampleDefect(d17, "尺寸超差", "D660", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleDefect(d17, "表面粗糙", "D661", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d17, "毛刺", "D662", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d17, "振纹", "D663", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d17, "碰伤", "D664", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d17, "同轴度超差", "D665", DefectSeverity.Critical, DefectCategory.Dimension);
        AddSampleCountAlarm(d17, "刀具寿命计数", "D760", 0, "次", "刀具使用次数（阈值 0 表示仅记录）");
        AddSampleCountAlarm(d17, "主轴保养计数", "D761", 1800, "h", "累计运行 1800 小时保养主轴");
        AddSampleCountAlarm(d17, "连续NG次数", "D762", 2, "个", "连续 NG 超过 2 个需更换刀具");
        AddSampleCountAlarm(d17, "急停次数", "D763", 1, "次", "单班次急停超 1 次需检查");
        AddSampleCountAlarm(d17, "班次产量", "D764", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d17);

        // ── 设备 18：包装机F2（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D47x，报警 M27x，缺陷 D67x，计数 D77x，配方 D534
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
        AddSampleDefect(d18, "封口不良", "D670", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d18, "标签偏移", "D671", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleDefect(d18, "包装破损", "D672", DefectSeverity.Critical, DefectCategory.Packaging);
        AddSampleDefect(d18, "漏封", "D673", DefectSeverity.Major, DefectCategory.Packaging);
        AddSampleCountAlarm(d18, "连续NG次数", "D770", 4, "个", "连续 NG 超过 4 个需检查封切机构");
        AddSampleCountAlarm(d18, "维护计数", "D771", 1200, "次", "累计运行达 1200 次需保养");
        AddSampleCountAlarm(d18, "膜卷更换计数", "D772", 0, "卷", "膜卷使用计数（阈值 0 表示仅记录）");
        AddSampleCountAlarm(d18, "班次产量", "D773", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d18);

        // ── 设备 19：激光打标机G1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D48x，报警 M28x，缺陷 D68x，计数 D78x，配方 D536
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
        AddSampleDefect(d19, "标记缺失", "D680", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d19, "标记模糊", "D681", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d19, "位置偏移", "D682", DefectSeverity.Major, DefectCategory.Dimension);
        AddSampleDefect(d19, "标记深浅不一", "D683", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleCountAlarm(d19, "连续NG次数", "D780", 3, "个", "连续 NG 超过 3 个需检查激光器");
        AddSampleCountAlarm(d19, "维护计数", "D781", 2500, "次", "累计运行达 2500 次需保养");
        AddSampleCountAlarm(d19, "激光器寿命计数", "D782", 0, "h", "激光器使用小时数（阈值 0 表示仅记录）");
        AddSampleCountAlarm(d19, "班次产量", "D783", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
        devices.Add(d19);

        // ── 设备 20：清洗机H1（中量配置：6 报警 + 4 缺陷 + 4 计数报警） ──
        // 主地址 D49x，报警 M29x，缺陷 D69x，计数 D79x，配方 D538
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
        AddSampleDefect(d20, "残留异物", "D690", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleDefect(d20, "水印", "D691", DefectSeverity.Minor, DefectCategory.Appearance);
        AddSampleDefect(d20, "氧化", "D692", DefectSeverity.Critical, DefectCategory.Appearance);
        AddSampleDefect(d20, "划伤", "D693", DefectSeverity.Major, DefectCategory.Appearance);
        AddSampleCountAlarm(d20, "连续NG次数", "D790", 5, "个", "连续 NG 超过 5 个需检查喷淋");
        AddSampleCountAlarm(d20, "维护计数", "D791", 1800, "次", "累计运行达 1800 次需保养");
        AddSampleCountAlarm(d20, "滤芯更换计数", "D792", 500, "h", "累计运行 500 小时需更换滤芯");
        AddSampleCountAlarm(d20, "班次产量", "D793", 0, "件", "班次产量计数（阈值 0 表示仅记录）");
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

        foreach (var d in devices)
        {
            Add(d.OkCountAddress, $"{d.Name}.OkCount");
            Add(d.NgCountAddress, $"{d.Name}.NgCount");
            Add(d.StatusCountAddress, $"{d.Name}.StatusCount");
            Add(d.ProductionResetAddress, $"{d.Name}.ProductionReset");
            Add(d.RecipeAddress, $"{d.Name}.Recipe");
            foreach (var a in d.Alarms)
                Add(a.PlcAddress, $"{d.Name}.Alarm[{a.Name}]");
            foreach (var def in d.Defects)
                Add(def.PlcAddress, $"{d.Name}.Defect[{def.Name}]");
            foreach (var c in d.CountAlarms)
                Add(c.PlcAddress, $"{d.Name}.CountAlarm[{c.Name}]");
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

    private static void AddSampleCountAlarm(Device d, string name, string addr, int max, string unit, string desc)
    {
        d.CountAlarms.Add(new CountAlarm
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
