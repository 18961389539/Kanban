namespace Kanban.Web;

/// <summary>屏端界面语言（与 Kanban.Core.Services.AppLanguage 值对齐：0=中文/1=English/2=日本語）。</summary>
public enum WebLanguage
{
    Zh = 0,
    En = 1,
    Ja = 2,
}

/// <summary>
/// WASM 屏端多语言字典（zh/en/ja 三语内嵌）。
/// 语言来源：Collector settings.json 的 Language（经 Hub GetLanguageAsync 拉取，屏端零配置），
/// DashboardState 拉取后设置 <see cref="Current"/>；页面文案统一经 <see cref="T"/> 访问。
/// 与 WPF 端一致的三语 key 一致性由 CI 冒烟脚本对源码做静态校验（防漏翻译）。
/// 字典内嵌（非 fetch）：字典小、同步可用、无首屏网络往返；切换语言重启服务端即全端生效。
/// </summary>
public static class L
{
    private static readonly Dictionary<string, string[]> D = new()
    {
        // ── 连接 / 空态 ──
        ["conn_live"] = new[] { "实时", "Live", "リアルタイム" },
        ["conn_down"] = new[] { "未连接", "Disconnected", "未接続" },
        ["no_device_data"] = new[] { "暂无设备数据", "No device data", "デバイスデータなし" },
        ["waiting_conn"] = new[] { "等待连接采集服务…", "Waiting for the collection service…", "収集サービスへの接続待ち…" },
        ["empty_hint"] = new[] { "确认 PlcSimulator 与 Kanban.Collector 已启动，且 CollectorHubUrl 端口一致", "Ensure PlcSimulator and Kanban.Collector are running and the CollectorHubUrl port matches", "PlcSimulator と Kanban.Collector が起動し、CollectorHubUrl のポートが一致していることを確認してください" },
        ["no_dev_selected"] = new[] { "未选择设备", "No device selected", "デバイス未選択" },
        ["select_hint"] = new[] { "从右上角选择要查看的设备", "Select a device from the top right", "右上から表示するデバイスを選択してください" },

        // ── 状态汇总 / 设备状态 ──
        ["st_running"] = new[] { "运行", "Running", "稼働" },
        ["st_alarm"] = new[] { "报警", "Alarm", "アラーム" },
        ["st_paused"] = new[] { "暂停", "Paused", "一時停止" },
        ["st_idle"] = new[] { "待机", "Idle", "待機" },
        ["dev_count"] = new[] { "设备", "Devices", "デバイス" },

        // ── 卡片标题 ──
        ["card_dev_status"] = new[] { "设备状态", "Device Status", "デバイス状態" },
        ["card_prod_status"] = new[] { "当前生产状态", "Current Production", "現在の生産状態" },
        ["card_alarms"] = new[] { "实时故障", "Live Alarms", "リアルタイム故障" },
        ["card_oee"] = new[] { "OEE 概览", "OEE Overview", "OEE 概要" },
        ["card_output"] = new[] { "产量明细", "Output Details", "生産明細" },
        ["card_workorder"] = new[] { "当前工单", "Current Work Order", "現在の工単" },
        ["card_trend"] = new[] { "速度趋势", "Speed Trend", "速度トレンド" },
        ["card_shift"] = new[] { "班次进度", "Shift Progress", "班次進捗" },
        ["card_source"] = new[] { "数据源", "Data Source", "データソース" },

        // ── 标签 ──
        ["lbl_run"] = new[] { "运行", "Run", "稼働" },
        ["lbl_alarm"] = new[] { "报警", "Alarm", "アラーム" },
        ["lbl_paused"] = new[] { "暂停", "Paused", "一時停止" },
        ["real_time_speed"] = new[] { "实时速度（件/小时）", "Real-time Speed (pcs/h)", "リアルタイム速度（個/時）" },
        ["lbl_target_cycle"] = new[] { "目标节拍", "Target Cycle", "目標タクト" },
        ["lbl_actual_cycle"] = new[] { "实际节拍", "Actual Cycle", "実績タクト" },
        ["lbl_total_output"] = new[] { "总产量", "Total Output", "総生産数" },
        ["lbl_ng_rate"] = new[] { "不良率", "NG Rate", "不良率" },
        ["lbl_achievement"] = new[] { "达成率", "Achievement", "達成率" },
        ["lbl_availability"] = new[] { "可用率", "Availability", "稼働率" },
        ["lbl_performance"] = new[] { "性能率", "Performance", "性能率" },
        ["lbl_quality"] = new[] { "合格率", "Quality", "良品率" },
        ["lbl_ok"] = new[] { "OK 良品", "OK Good", "OK 良品" },
        ["lbl_ng"] = new[] { "NG 不良", "NG Defect", "NG 不良" },
        ["lbl_planned"] = new[] { "计划", "Planned", "計画" },
        ["lbl_elapsed"] = new[] { "已运行", "Elapsed", "稼働時間" },
        ["lbl_remaining"] = new[] { "剩余", "Remaining", "残り" },
        ["lbl_conn_status"] = new[] { "连接状态", "Connection", "接続状態" },
        ["lbl_dev_total"] = new[] { "设备总数", "Total Devices", "デバイス総数" },
        ["lbl_data_fresh"] = new[] { "数据更新", "Data Freshness", "データ更新" },
        ["lbl_snap_seq"] = new[] { "快照序号", "Snapshot Seq", "スナップショット番号" },
        ["lbl_svc_addr"] = new[] { "服务地址", "Service Address", "サービスアドレス" },
        ["lbl_svc_version"] = new[] { "服务版本", "Service Version", "サービスバージョン" },

        // ── 值 ──
        ["val_connected"] = new[] { "已连接", "Connected", "接続済み" },
        ["val_disconnected"] = new[] { "断开", "Disconnected", "切断" },
        ["val_no_alarms"] = new[] { "当前无激活报警", "No active alarms", "アクティブアラームなし" },
        ["val_no_data"] = new[] { "暂无数据", "No data", "データなし" },
        ["val_no_workorder"] = new[] { "暂无工单", "No work orders", "工単なし" },
        ["val_wait_data"] = new[] { "等待数据积累…", "Waiting for data…", "データ蓄積待ち…" },
        ["val_dyn_addr"] = new[] { "动态派生 · :5129", "Auto-derived · :5129", "自動導出 · :5129" },

        // ── 工单状态 ──
        ["wo_running"] = new[] { "生产中", "In Production", "生産中" },
        ["wo_completed"] = new[] { "已完成", "Completed", "完了" },
        ["wo_aborted"] = new[] { "已终止", "Aborted", "中止" },
        ["wo_pending"] = new[] { "待开始", "Pending", "未開始" },

        // ── 组合行（含占位符） ──
        ["meta_statusword"] = new[] { "状态字 0x{0} · 目标 {1} 件/h", "Status word 0x{0} · Target {1} pcs/h", "状態語 0x{0} · 目標 {1} 個/h" },
        ["meta_quality_rate"] = new[] { "合格率 {0} · 不良率 {1}", "Quality {0} · NG rate {1}", "良品率 {0} · 不良率 {1}" },
        ["meta_plan_wo"] = new[] { "计划 {0} 件", "Planned {0} pcs", "計画 {0} 個" },
        ["meta_trend_current"] = new[] { "当前 {0} 件/h · 采样 0.5s", "Current {0} pcs/h · Sampling 0.5s", "現在 {0} 個/h · サンプリング 0.5s" },
        ["meta_shift_auto"] = new[] { "按班次配置自动计算", "Auto-calculated from shift config", "班次設定から自動計算" },
        ["badge_recent_1min"] = new[] { "近 1 分钟", "Last 1 min", "直近 1 分" },
        ["wo_progress"] = new[] { "{0} / {1} 件", "{0} / {1} pcs", "{0} / {1} 個" },
        ["wo_progress_pending"] = new[] { "-- / {0} 件", "-- / {0} pcs", "-- / {0} 個" },

        // ── 数据新鲜度 ──
        ["fresh_none"] = new[] { "未收到", "Not received", "未受信" },
        ["fresh_live"] = new[] { "实时", "Live", "リアルタイム" },
        ["fresh_secs"] = new[] { "{0} 秒前", "{0}s ago", "{0} 秒前" },
        ["fresh_mins"] = new[] { "{0} 分钟前", "{0} min ago", "{0} 分前" },
    };

    /// <summary>当前屏端语言（DashboardState 从 Collector 拉取后设置；默认中文）。</summary>
    public static WebLanguage Current { get; set; } = WebLanguage.Zh;

    /// <summary>取当前语言文案；支持 {0}/{1} 占位符；缺失 key 回退 key 本身（便于排查）。</summary>
    public static string T(string key, params object[] args)
    {
        if (D.TryGetValue(key, out var v))
        {
            var s = v[(int)Current];
            return args.Length > 0 ? string.Format(s, args) : s;
        }
        return key;
    }
}
