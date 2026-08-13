#!/usr/bin/env python3
"""
Append Web 端历史查询页新增的 Web_Hq_*/Web_Nav_* key（三语 resx + Strings.cs 强类型属性）。

幂等：已存在的 key 跳过。用法：python ci/i18n_add_web_hq_keys.py

约束（对应 LocalizationGuardTests）：
- key 必须同时出现在 Strings.resx / en / ja 与 Strings.cs；
- en/ja 不得含 CJK 字符；
- Strings.cs 无重复 key。
"""

import re
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
RESX_DIR = PROJECT_ROOT / "MainAPP" / "Resources"
STRINGS_CS = RESX_DIR / "Strings.cs"

# (zh, en, ja) —— 与页面 L.T(key) 一一对应
KEYS = [
    # 导航
    ("Nav_Dashboard", "看板", "Dashboard", "ダッシュボード"),
    ("Nav_History", "历史查询", "History Query", "履歴照会"),
    # 筛选栏
    ("Hq_Device", "设备", "Device", "デバイス"),
    ("Hq_AllDevices", "全部设备", "All Devices", "全デバイス"),
    ("Hq_QuickRange", "快捷时间", "Quick Range", "クイック範囲"),
    ("Hq_Custom", "自定义", "Custom", "カスタム"),
    ("Hq_Today", "今天", "Today", "今日"),
    ("Hq_Yesterday", "昨天", "Yesterday", "昨日"),
    ("Hq_Last7d", "近 7 天", "Last 7 Days", "直近7日"),
    ("Hq_Last30d", "近 30 天", "Last 30 Days", "直近30日"),
    ("Hq_ThisWeek", "本周", "This Week", "今週"),
    ("Hq_ThisMonth", "本月", "This Month", "今月"),
    ("Hq_From", "开始", "From", "開始"),
    ("Hq_To", "结束", "To", "終了"),
    ("Hq_Shift", "班次", "Shift", "シフト"),
    ("Hq_AllShifts", "全部班次", "All Shifts", "全シフト"),
    ("Hq_Search", "查询", "Search", "検索"),
    ("Hq_Reset", "重置", "Reset", "リセット"),
    ("Hq_Export", "导出 CSV", "Export CSV", "CSV エクスポート"),
    # KPI / 图表
    ("Hq_TotalOk", "总 OK", "Total OK", "OK 合計"),
    ("Hq_TotalNg", "总 NG", "Total NG", "NG 合計"),
    ("Hq_QualityRate", "合格率", "Quality Rate", "良品率"),
    ("Hq_ChartTitle", "产量趋势（15 分钟分桶）", "Production Trend (15-min buckets)", "生産トレンド（15分バケット）"),
    ("Hq_ChartOk", "OK 产量", "OK Output", "OK 生産量"),
    ("Hq_ChartNg", "NG 产量", "NG Output", "NG 生産量"),
    # 表格
    ("Hq_Time", "时间", "Time", "時間"),
    ("Hq_DeviceId", "设备 ID", "Device ID", "デバイス ID"),
    ("Hq_DeviceName", "设备名称", "Device Name", "デバイス名"),
    ("Hq_OkCol", "OK", "OK", "OK"),
    ("Hq_NgCol", "NG", "NG", "NG"),
    ("Hq_StatusWord", "状态字", "Status Word", "状態語"),
    ("Hq_PrevPage", "上一页", "Prev", "前へ"),
    ("Hq_NextPage", "下一页", "Next", "次へ"),
    ("Hq_PageInfo", "第 {0} / {1} 页 · 共 {2} 条", "Page {0} / {1} · {2} rows", "{0} / {1} ページ · 全 {2} 件"),
    # 状态/提示
    ("Hq_NoData", "暂无数据", "No Data", "データなし"),
    ("Hq_NoDataHint", "请调整时间范围或设备筛选后重试", "Adjust the time range or device filter and retry", "時間範囲またはデバイス条件を変更して再試行してください"),
    ("Hq_QueryFailed", "查询失败，请确认采集服务正常后重试", "Query failed. Ensure the collection service is running", "照会に失敗しました。収集サービスの稼働を確認してください"),
    ("Hq_ValidationRange", "请检查开始/结束时间（格式 yyyy-MM-ddTHH:mm，且开始不得晚于结束）", "Check from/to (format yyyy-MM-ddTHH:mm, from must not be later than to)", "開始/終了時刻を確認してください（形式 yyyy-MM-ddTHH:mm、開始は終了より後にできません）"),
    ("Hq_ExportEmpty", "当前页无数据可导出", "Nothing to export on this page", "このページにエクスポート対象がありません"),
    ("Hq_Loading", "查询中", "Querying", "照会中"),
    ("Hq_Analyzing", "正在分析产量数据", "Analyzing production data", "生産データを分析中"),
    ("Hq_Truncated", "数据量超过 10 万条上限，KPI/图表基于已取部分数据（建议缩小时间范围）", "Over 100k rows; KPI/chart based on fetched subset (narrow the time range)", "10万件超のため、KPI・グラフは取得済みデータに基づきます（時間範囲を絞ってください）"),
    ("Hq_HintTitle", "产量历史查询", "Production History Query", "生産履歴照会"),
    ("Hq_HintBody", "选择设备与时间范围后点击“查询”，可查看 KPI、15 分钟产量趋势与明细", "Select a device and time range, then click Search for KPI, 15-min trend and details", "デバイスと時間範囲を選択して「検索」をクリックすると、KPI・15分トレンド・明細を確認できます"),
    # 洞察
    ("Hq_InsightPeakHigh", "峰值 {0} 件（{1}），高于均值 {2:P0}", "Peak {0} ({1}), {2:P0} above avg", "ピーク {0} 個（{1}）、平均より {2:P0} 高い"),
    ("Hq_InsightPeakLow", "峰值 {0} 件（{1}），低于均值 {2:P0}", "Peak {0} ({1}), {2:P0} below avg", "ピーク {0} 個（{1}）、平均より {2:P0} 低い"),
    ("Hq_InsightSteady", "平均 {0} 件/桶，峰值 {1} 件（{2}），产量平稳", "Avg {0}/bucket, peak {1} ({2}), steady output", "平均 {0} 個/バケット、ピーク {1} 個（{2}）、安定"),
    ("Hq_InsightDrop", "检测到 {0} 次突降，最严重 {1} 下降 {2:P0}（{3} → {4} 件）", "{0} sudden drop(s), worst {1} -{2:P0} ({3} → {4} pcs)", "急落 {0} 回、最悪 {1} -{2:P0}（{3} → {4} 個）"),
    # ── 历史查询四 Tab（状态/报警/OEE） ──
    ("Tab_Production", "产量", "Production", "生産"),
    ("Tab_Status", "状态", "Status", "状態"),
    ("Tab_Alarm", "报警", "Alarm", "警報"),
    ("Tab_Oee", "OEE", "OEE", "OEE"),
    ("Hq_NeedDevice", "请先选择设备（该 Tab 按单设备查询）", "Select a device first (this tab queries one device)", "先にデバイスを選択してください（このタブは単一デバイス照会）"),
    ("Hq_StateUnknown", "初始", "Initial", "初期"),
    ("Hq_StatusRun", "运行时长", "Run Time", "稼働時間"),
    ("Hq_StatusAlarm", "报警时长", "Alarm Time", "アラーム時間"),
    ("Hq_StatusPause", "待机时长", "Paused Time", "待機時間"),
    ("Hq_StatusPie", "状态时长占比", "State Duration Share", "状態時間割合"),
    ("Hq_StatusDaily", "每日状态时长（小时）", "Daily State Hours", "日別状態時間（時間）"),
    ("Hq_StatusGantt", "状态甘特图", "Status Gantt", "状態ガントチャート"),
    ("Hq_StatusTransitions", "状态转换记录", "Status Transitions", "状態遷移記録"),
    ("Hq_StatusHintTitle", "状态历史查询", "Status History Query", "状態履歴照会"),
    ("Hq_StatusHintBody", "选择设备与时间范围后点击“查询”，可查看状态时长占比、每日分布、甘特图与转换明细", "Select a device and time range, then click Search for state durations, daily distribution, gantt and transitions", "デバイスと時間範囲を選択して「検索」をクリックすると、状態時間・日別分布・ガントチャート・遷移明細を確認できます"),
    ("Hq_EventTime", "事件时间", "Event Time", "イベント時刻"),
    ("Hq_PrevState", "前一状态", "Previous State", "前状態"),
    ("Hq_CurrState", "当前状态", "Current State", "現在の状態"),
    ("Hq_AlarmName", "报警名称", "Alarm Name", "アラーム名"),
    ("Hq_AllAlarms", "全部报警", "All Alarms", "全アラーム"),
    ("Hq_AlarmId", "报警 ID", "Alarm ID", "アラーム ID"),
    ("Hq_AlarmPlc", "PLC 地址", "PLC Address", "PLC アドレス"),
    ("Hq_AlarmType", "事件类型", "Event Type", "イベント種別"),
    ("Hq_AlarmTriggered", "触发", "Triggered", "発生"),
    ("Hq_AlarmRecovered", "恢复", "Recovered", "復旧"),
    ("Hq_AlarmPending", "待恢复", "Pending", "未復旧"),
    ("Hq_AlarmChart", "报警频次排行（Top 20）", "Alarm Frequency (Top 20)", "アラーム頻度（Top 20）"),
    ("Hq_AlarmEvents", "报警事件", "Alarm Events", "アラームイベント"),
    ("Hq_AlarmHintTitle", "报警历史查询", "Alarm History Query", "警報履歴照会"),
    ("Hq_AlarmHintBody", "选择设备与时间范围后点击“查询”，可查看触发/恢复/待恢复统计、频次排行与事件明细", "Select a device and time range, then click Search for trigger/recover/pending stats, frequency and events", "デバイスと時間範囲を選択して「検索」をクリックすると、発生/復旧/未復旧の統計・頻度・イベントを確認できます"),
    ("Hq_OeeOverview", "OEE 综合概览", "Overall OEE", "OEE 総合"),
    ("Hq_OeeTrend", "班次 OEE 趋势", "Shift OEE Trend", "シフト OEE トレンド"),
    ("Hq_OeeDetails", "班次 OEE 明细", "Shift OEE Details", "シフト OEE 明細"),
    ("Hq_ShiftTime", "开始时间", "Start Time", "開始時刻"),
    ("Hq_OeeHintTitle", "OEE 历史查询", "OEE History Query", "OEE 履歴照会"),
    ("Hq_OeeHintBody", "选择设备与时间范围后点击“查询”，可查看四率、班次 OEE 趋势与明细", "Select a device and time range, then click Search for the four rates, shift OEE trend and details", "デバイスと時間範囲を選択して「検索」をクリックすると、四率・シフト OEE トレンド・明細を確認できます"),
    # ── 状态/报警/OEE 洞察 ──
    ("Hq_InsLongestRun", "最长连续运行 {0} 分钟（{1} ~ {2}）", "Longest run {0} min ({1} ~ {2})", "最長連続稼働 {0} 分（{1} ~ {2}）"),
    ("Hq_InsLongestAlarm", "最长连续报警 {0} 分钟（{1} ~ {2}）", "Longest alarm {0} min ({1} ~ {2})", "最長連続警報 {0} 分（{1} ~ {2}）"),
    ("Hq_InsLongestAlarmLong", "⚠ 单次报警超 30 分钟：最长 {0} 分钟（{1} ~ {2}）", "⚠ Alarm over 30 min: longest {0} min ({1} ~ {2})", "⚠ 30分超の警報：最長 {0} 分（{1} ~ {2}）"),
    ("Hq_InsLongestPause", "最长连续待机 {0} 分钟（{1} ~ {2}）", "Longest pause {0} min ({1} ~ {2})", "最長連続待機 {0} 分（{1} ~ {2}）"),
    ("Hq_InsPauseRatio", "待机占比 {0:P0}，超过阈值 {1:P0}", "Paused ratio {0:P0}, above threshold {1:P0}", "待機割合 {0:P0}、閾値 {1:P0} 超"),
    ("Hq_InsAlarmRatio", "报警占比 {0:P0}，超过阈值 {1:P0}", "Alarm ratio {0:P0}, above threshold {1:P0}", "警報割合 {0:P0}、閾値 {1:P0} 超"),
    ("Hq_InsAlarmTop3", "报警频次 TOP3：{0}", "Alarm frequency TOP3: {0}", "警報頻度 TOP3：{0}"),
    ("Hq_InsAlarmTop", "{0}（{1} 次，占 {2:P0}）", "{0} ({1} times, {2:P0})", "{0}（{1} 回、{2:P0}）"),
    ("Hq_InsAlarmPending", "待恢复报警：{0}", "Pending alarms: {0}", "未復旧警報：{0}"),
    ("Hq_InsAlarmPendingItem", "{0}（{1}，自 {2}）", "{0} ({1}, since {2})", "{0}（{1}、{2} から）"),
    ("Hq_InsAlarmChain", "连锁触发模式 {0} 出现 {1} 次", "Chain trigger pattern {0} x{1}", "連鎖発生パターン {0} が {1} 回"),
    ("Hq_InsOeeWeak", "{0} 偏低（{1:P0}），是 OEE 短板", "{0} is low ({1:P0}), the OEE bottleneck", "{0} が低い（{1:P0}）、OEE の足を引っ張る要因"),
    ("Hq_InsOeeGap", "{0}（{1:P0}）领先 {2}（{3:P0}）", "{0} ({1:P0}) leads {2} ({3:P0})", "{0}（{1:P0}）が {2}（{3:P0}）より高い"),
    ("Hq_InsOeeBalance", "合格率 {0:P0} × 性能率 {1:P0} × 可用率 {2:P0} = OEE {3:P0}", "Quality {0:P0} × Performance {1:P0} × Availability {2:P0} = OEE {3:P0}", "良品率 {0:P0} × 性能率 {1:P0} × 稼働率 {2:P0} = OEE {3:P0}"),
    ("Hq_InsShiftWorst", "班次 {0}（{1}）OEE {2:P0} 最低，与最佳班次差距 {3:P0}", "Shift {0} ({1}) has lowest OEE {2:P0}, {3:P0} behind the best", "シフト {0}（{1}）の OEE {2:P0} が最低、最高シフトより {3:P0} 低い"),
    ("Hq_InsShiftDrag", "主要拖累：{0}（{1:P0} vs 最佳 {2:P0}）", "Main drag: {0} ({1:P0} vs best {2:P0})", "主な原因：{0}（{1:P0} vs 最高 {2:P0}）"),
    # ── 报警中心页 ──
    ("Nav_AlarmCenter", "报警中心", "Alarm Center", "警報センター"),
    ("Ac_ActiveCount", "当前活跃", "Active Alarms", "現在の警報"),
    ("Ac_AffectedDevices", "涉及设备", "Devices Affected", "影響デバイス"),
    ("Ac_TodayTrigger", "今日触发", "Today Triggered", "今日の発生"),
    ("Ac_TodayRecover", "今日恢复", "Today Recovered", "今日の復旧"),
    ("Ac_Longest", "最长持续", "Longest Active", "最長継続"),
    ("Ac_ActiveTitle", "实时活跃报警", "Live Active Alarms", "リアルタイム警報"),
    ("Ac_LevelHigh", "高", "High", "高"),
    ("Ac_LevelMedium", "中", "Medium", "中"),
    ("Ac_LevelLow", "低", "Low", "低"),
    ("Ac_StatsTitle", "报警统计", "Alarm Statistics", "警報統計"),
    ("Ac_Range1h", "近 1 小时", "Last 1 Hour", "直近1時間"),
    ("Ac_Range4h", "近 4 小时", "Last 4 Hours", "直近4時間"),
    ("Ac_Range24h", "近 24 小时", "Last 24 Hours", "直近24時間"),
    ("Ac_TopTitle", "报警排行", "Alarm Ranking", "警報ランキング"),
    ("Ac_RecentTitle", "最近事件", "Recent Events", "最近のイベント"),
    ("Ac_Rank", "排名", "Rank", "順位"),
    ("Ac_Count", "次数", "Count", "回数"),
    ("Ac_TotalDuration", "累计时长", "Total Duration", "累計時間"),
    ("Ac_Level", "级别", "Level", "レベル"),
    ("Ac_AlarmName", "报警名称", "Alarm Name", "アラーム名"),
    ("Ac_Device", "设备", "Device", "デバイス"),
    # ── 运行监控页 ──
    ("Nav_Monitoring", "运行监控", "Runtime Monitoring", "稼働監視"),
    ("Mo_AcqStatus", "采集状态", "Acquisition", "収集状態"),
    ("Mo_AcqRunning", "采集中", "Acquiring", "収集中"),
    ("Mo_AcqStopped", "已停止", "Stopped", "停止中"),
    ("Mo_SuccessRate", "采集成功率", "Cycle Success Rate", "サイクル成功率"),
    ("Mo_ConsecutiveFail", "连续失败", "Consecutive Failures", "連続失敗"),
    ("Mo_PendingHistory", "历史积压", "History Backlog", "履歴滞留"),
    ("Mo_DeviceLive", "设备实时状态", "Live Device Status", "デバイスリアルタイム状態"),
    ("Mo_Live3s", "3s 刷新", "Refresh 3s", "3秒更新"),
    ("Mo_CycleHealth", "采集循环健康", "Acquisition Cycle Health", "収集サイクル健全性"),
    ("Mo_Refresh10s", "10s 刷新", "Refresh 10s", "10秒更新"),
    ("Mo_CompletedCycles", "完成循环", "Completed Cycles", "完了サイクル"),
    ("Mo_FailedCycles", "失败循环", "Failed Cycles", "失敗サイクル"),
    ("Mo_LastCycleMs", "最近循环耗时", "Last Cycle", "直近サイクル"),
    ("Mo_AvgCycleMs", "平均循环耗时", "Avg Cycle", "平均サイクル"),
    ("Mo_MaxCycleMs", "最大循环耗时", "Max Cycle", "最大サイクル"),
    ("Mo_DevicesRead", "设备读取", "Devices Read", "デバイス読取"),
    ("Mo_LastSuccessAt", "上次成功", "Last Success", "最終成功"),
    ("Mo_LastFailureAt", "上次失败", "Last Failure", "最終失敗"),
    ("Mo_HistoryHealth", "历史落库健康", "History Persistence", "履歴保存健全性"),
    ("Mo_LastFlushAt", "上次落库", "Last Flush", "最終書込"),
    ("Mo_FlushFailures", "落库失败", "Flush Failures", "書込失敗"),
    ("Mo_RecoveryFile", "恢复文件", "Recovery File", "リカバリファイル"),
    ("Mo_DbSize", "数据库大小", "DB Size", "DB サイズ"),
    ("Mo_EstimatedOps", "估算读操作", "Est. Read Ops", "推定読取回数"),
    ("Mo_Disconnects", "断连次数", "Disconnects", "切断回数"),
    ("Mo_TimingBreakdown", "读取耗时分解（ms）", "Read Timing Breakdown (ms)", "読取時間内訳（ms）"),
    ("Mo_TmBatchPlan", "批计划", "Batch Plan", "バッチ計画"),
    ("Mo_TmDword", "字读取", "DWord Read", "ワード読取"),
    ("Mo_TmAlarm", "报警", "Alarm", "警報"),
    ("Mo_TmDefect", "缺陷", "Defect", "欠陥"),
    ("Mo_TmCountAlarm", "计数报警", "Count Alarm", "カウント警報"),
    ("Mo_TmHistory", "历史写入", "History Write", "履歴書込"),
    # ── 生产复盘页 ──
    ("Nav_Review", "生产复盘", "Production Review", "生産レビュー"),
    ("Rv_HintTitle", "生产复盘分析", "Production Review", "生産レビュー分析"),
    ("Rv_HintBody", "选择设备与时间范围后点击“查询”，生成健康评分、复盘结论与周期对比", "Select a device and time range, then click Search for health score, conclusions and period comparison", "デバイスと時間範囲を選択して「検索」をクリックすると、健康スコア・結論・期間比較を確認できます"),
    ("Rv_Health", "设备健康评分", "Device Health", "デバイス健康スコア"),
    ("Rv_AlarmCount", "报警次数", "Alarm Count", "警報回数"),
    ("Rv_CurrentShift", "当前班次", "Current Shift", "現在のシフト"),
    ("Rv_WorkOrder", "当前工单", "Current Work Order", "現在の工単"),
    ("Rv_Product", "产品 / 批次", "Product / Batch", "製品 / ロット"),
    ("Rv_Recipe", "当前配方", "Current Recipe", "現在のレシピ"),
    ("Rv_RecipeHint", "配方值来自当前设备配置", "Recipe value from device config", "レシピ値はデバイス設定から"),
    ("Rv_Conclusions", "复盘结论", "Conclusions", "レビュー結論"),
    ("Rv_ConclusionsHint", "基于当前生产、设备与质量数据生成", "Generated from production, device and quality data", "生産・設備・品質データから生成"),
    ("Rv_Met", "达到", "Met", "達成"),
    ("Rv_NotMet", "低于", "Below", "未達"),
    ("Rv_QualityConclusion", "良品率为 {0:P1}，{1} {2:P0} 目标", "Quality is {0:P1}, {1} the {2:P0} target", "良品率 {0:P1}、目標 {2:P0} を{1}"),
    ("Rv_OeeConclusion", "OEE 为 {0:P1}，{1} {2:P0} 目标", "OEE is {0:P1}, {1} the {2:P0} target", "OEE {0:P1}、目標 {2:P0} を{1}"),
    ("Rv_DowntimeConclusion", "最长停机为 {0:F1} 小时，设备：{1}，报警：{2}", "Longest downtime {0:F1} h, device: {1}, alarm: {2}", "最長停止 {0:F1} 時間、設備：{1}、警報：{2}"),
    ("Rv_BestShiftConclusion", "{0}产量最高，共 {1:N0} 件，良品率 {2:P1}", "{0} has the highest output ({1:N0} pcs), quality {2:P1}", "{0} の生産が最多（{1:N0} 個）、良品率 {2:P1}"),
    ("Rv_TopDefectConclusion", "主要缺陷为 {0}（{1}），当前累计 {2:N0} 件，占当前缺陷 Top10 的 {3:F1}%", "Top defect: {0} ({1}), {2:N0} pcs, {3:F1}% of Top10", "主要欠陥は {0}（{1}）、累計 {2:N0} 個、Top10 の {3:F1}%"),
    ("Rv_AlarmCountConclusion", "当前范围共触发 {0:N0} 次报警，请结合 Top 5 报警确认主要停机来源", "{0:N0} alarms triggered; check Top 5 alarms for downtime sources", "この期間に警報 {0:N0} 回発生、Top5 警報で停止要因を確認してください"),
    ("Rv_NoConclusion", "当前时间范围没有足够的产量或报警数据生成复盘结论", "Not enough output or alarm data in this range to generate conclusions", "この期間には結論を生成するのに十分な生産・警報データがありません"),
    ("Rv_HealthLowOutput", "节拍异常：实际 {0:F1} 件/小时，低于目标 {1:F0} 件/小时的 80%", "Cycle anomaly: {0:F1} pcs/h, below 80% of target {1:F0} pcs/h", "タクト異常：実績 {0:F1} 個/時、目標 {1:F0} 個/時の80%未満"),
    ("Rv_HealthAlarmSpike", "报警突增：当前 {0} 次，上一周期 {1} 次", "Alarm spike: {0} now vs {1} last period", "警報急増：今回 {0} 回、前周期 {1} 回"),
    ("Rv_HealthDefectSpike", "缺陷率突增：当前 {0} 个，上一周期 {1} 个", "Defect spike: {0} now vs {1} last period", "欠陥急増：今回 {0} 個、前周期 {1} 個"),
    ("Rv_HealthNoOutput", "运行无产量：{0}-{1} 持续 {2:F0} min", "Running with no output: {0}-{1} for {2:F0} min", "稼働中に生産なし：{0}-{1} で {2:F0} 分"),
    ("Rv_NoIssues", "未发现健康问题", "No health issues found", "健康上の問題なし"),
    ("Rv_OutputAchievement", "节拍达成", "Cycle Achievement", "タクト達成"),
    ("Rv_LongestDowntime", "最长停机", "Longest Downtime", "最長停止"),
    ("Rv_PeakHour", "峰值时段", "Peak Hour", "ピーク時間帯"),
    ("Rv_ValleyHour", "谷值时段", "Valley Hour", "ボトム時間帯"),
    ("Rv_PeakValleyOk", "OK {0} 件 · 占 {1:P0}", "OK {0} pcs · {1:P0} share", "OK {0} 個 · {1:P0} の割合"),
    ("Rv_Comparison", "周期对比", "Period Comparison", "期間比較"),
    ("Rv_ComparisonHint", "当前设备与上一周期的变化", "Changes vs the previous period", "前周期との変化"),
    ("Rv_CurrentOutput", "当前总产量", "Current Output", "今回の総生産"),
    ("Rv_BaselineOutput", "上一周期产量", "Previous Output", "前周期の生産"),
    ("Rv_BaselineQuality", "上一周期良品率", "Previous Quality", "前周期の良品率"),
    ("Rv_BaselineOee", "上一周期 OEE", "Previous OEE", "前周期の OEE"),
    ("Rv_Trend", "产量趋势", "Output Trend", "生産トレンド"),
    ("Rv_AlarmRank", "报警排行（Top 5）", "Alarm Ranking (Top 5)", "警報ランキング（Top 5）"),
    ("Rv_Timeline", "设备状态时间线", "Status Timeline", "状態タイムライン"),
    ("Rv_TimelineHint", "运行、报警、待机与未知区间；悬浮查看该段产量与报警", "Run, alarm, pause and unknown segments; hover for output and alarms", "稼働・警報・待機・不明区間。ホバーで生産と警報を確認"),
    ("Rv_ShiftDetails", "班次明细", "Shift Details", "シフト明細"),
    ("Rv_AlarmCol", "报警", "Alarms", "警報"),
    # ── 产线预览页 ──
    ("Nav_Line", "产线预览", "Production Line", "生産ライン"),
    ("Ln_Title", "产线总览", "Production Line Overview", "生産ライン概要"),
    ("Ln_DevCount", "共 {0} 台设备", "{0} devices", "デバイス {0} 台"),
    ("Ln_CurrentShift", "当前班次", "Current Shift", "現在のシフト"),
    ("Ln_ShiftProgress", "班次进度", "Shift Progress", "シフト進捗"),
    ("Ln_WeightedOee", "加权 OEE", "Weighted OEE", "加重 OEE"),
    ("Ln_FilterAll", "全部状态", "All Statuses", "全状態"),
    ("Ln_Search", "搜索设备", "Search devices", "デバイス検索"),
    ("Ln_SortName", "按名称", "By Name", "名前順"),
    ("Ln_SortOutput", "按产量", "By Output", "生産順"),
    ("Ln_SortOee", "按 OEE", "By OEE", "OEE 順"),
    ("Ln_Cycle", "节拍（实际/目标）", "Cycle (Actual/Target)", "タクト（実績/目標）"),
    ("Ln_Downtime", "停机", "Downtime", "停止"),
    ("Ln_NoDevices", "暂无设备", "No devices", "デバイスなし"),
    ("Ln_NoDevicesHint", "请确认采集服务与仿真器已启动", "Ensure the collection service and simulator are running", "収集サービスとシミュレーターの起動を確認してください"),
    ("Ln_EmptyFilter", "没有匹配的设备", "No matching devices", "一致するデバイスがありません"),
    # ── 只读管理页（设备/工单/配方/设置/审计） ──
    ("Nav_Devices", "设备", "Devices", "デバイス"),
    ("Nav_WorkOrders", "工单", "Work Orders", "工単"),
    ("Nav_Recipes", "配方", "Recipes", "レシピ"),
    ("Nav_Settings", "设置", "Settings", "設定"),
    ("Nav_Audit", "审计", "Audit", "監査"),
    ("Dv_Title", "设备管理（只读）", "Devices (Read-only)", "デバイス管理（読み取り専用）"),
    ("Dv_ReadOnly", "只读视图 · 写操作请使用 WPF 端", "Read-only · use WPF for edits", "読み取り専用 · 編集は WPF を使用"),
    ("Dv_NoDevices", "暂无设备配置", "No device configs", "デバイス設定なし"),
    ("Dv_DeviceId", "设备 ID", "Device ID", "デバイス ID"),
    ("Dv_MachineType", "机型", "Machine Type", "機種"),
    ("Dv_AlarmCount", "报警 {0} 条", "{0} alarms", "警報 {0} 件"),
    ("Dv_DefectCount", "缺陷 {0} 条", "{0} defects", "欠陥 {0} 件"),
    ("Dv_CountAlarmCount", "计数报警 {0} 条", "{0} count alarms", "カウント警報 {0} 件"),
    ("Dv_Detail", "{0} 配置详情", "{0} Config Details", "{0} 設定詳細"),
    ("Dv_AddressConfig", "地址配置", "Address Config", "アドレス設定"),
    ("Dv_OkCountAddr", "OK 计数地址", "OK Count Address", "OK カウントアドレス"),
    ("Dv_NgCountAddr", "NG 计数地址", "NG Count Address", "NG カウントアドレス"),
    ("Dv_StatusAddr", "状态字地址", "Status Word Address", "状態語アドレス"),
    ("Dv_ResetAddr", "产量复位地址", "Reset Address", "リセットアドレス"),
    ("Dv_RecipeAddr", "配方地址", "Recipe Address", "レシピアドレス"),
    ("Dv_AlarmConfig", "报警配置", "Alarm Config", "警報設定"),
    ("Dv_DefectConfig", "缺陷配置", "Defect Config", "欠陥設定"),
    ("Dv_CountAlarmConfig", "计数报警配置", "Count Alarm Config", "カウント警報設定"),
    ("Dv_None", "无配置", "None", "設定なし"),
    ("Dv_Name", "名称", "Name", "名称"),
    ("Dv_PlcAddr", "PLC 地址", "PLC Address", "PLC アドレス"),
    ("Dv_Description", "描述", "Description", "説明"),
    ("Dv_Severity", "严重度", "Severity", "重大度"),
    ("Dv_Category", "类别", "Category", "カテゴリ"),
    ("Dv_Threshold", "阈值", "Threshold", "閾値"),
    ("Wo_Title", "工单管理（只读）", "Work Orders (Read-only)", "工単管理（読み取り専用）"),
    ("Wo_None", "暂无工单", "No work orders", "工単なし"),
    ("Wo_List", "工单列表", "Work Order List", "工単リスト"),
    ("Wo_OrderNo", "工单号", "Order No.", "工単番号"),
    ("Wo_Product", "产品", "Product", "製品"),
    ("Wo_PlanQty", "计划量", "Target Qty", "計画数"),
    ("Wo_DoneOk", "完成 OK", "Done OK", "完了 OK"),
    ("Wo_DoneNg", "完成 NG", "Done NG", "完了 NG"),
    ("Wo_PlanStart", "计划开始", "Planned Start", "計画開始"),
    ("Wo_PlanEnd", "计划结束", "Planned End", "計画終了"),
    ("Wo_Status", "状态", "Status", "状態"),
    ("Rc_Title", "配方管理（只读）", "Recipes (Read-only)", "レシピ管理（読み取り専用）"),
    ("Rc_None", "暂无配方", "No recipes", "レシピなし"),
    ("Rc_RecipeValue", "配方值", "Recipe Value", "レシピ値"),
    ("Rc_RecipeId", "配方 ID", "Recipe ID", "レシピ ID"),
    ("Rc_ItemCount", "{0} 个参数", "{0} params", "{0} パラメータ"),
    ("St_Title", "采集设置（只读）", "Settings (Read-only)", "収集設定（読み取り専用）"),
    ("St_Acquisition", "采集参数", "Acquisition", "収集パラメータ"),
    ("St_PollingMs", "轮询间隔", "Polling Interval", "ポーリング間隔"),
    ("St_HistoryScans", "历史写入间隔（扫描次）", "History Write (scans)", "履歴書込間隔（スキャン）"),
    ("St_BatchMaxLen", "批读最大长度", "Batch Read Max", "バッチ読取最大長"),
    ("St_BatchGapSlots", "批读最大间隙", "Batch Read Gap", "バッチ読取ギャップ"),
    ("St_Plc", "PLC 连接", "PLC Connection", "PLC 接続"),
    ("St_PlcBrand", "PLC 品牌", "PLC Brand", "PLC ブランド"),
    ("St_PlcIp", "IP 地址", "IP Address", "IP アドレス"),
    ("St_PlcPort", "端口", "Port", "ポート"),
    ("St_PlcTimeout", "超时", "Timeout", "タイムアウト"),
    ("St_SiemensModel", "机型", "Model", "機種"),
    ("St_DataFormat", "数据格式", "Data Format", "データ形式"),
    ("St_AddrZero", "地址从 0 开始", "Zero-based Addr", "0 起点アドレス"),
    ("St_ReadSplits", "读取分段", "Read Splits", "読取分割"),
    ("St_Shifts", "班次配置", "Shift Config", "シフト設定"),
    ("St_NoShifts", "未配置班次", "No shifts configured", "シフト未設定"),
    ("Au_Title", "审计查询（只读）", "Audit Log (Read-only)", "監査ログ（読み取り専用）"),
    ("Au_Operator", "操作人", "Operator", "操作者"),
    ("Au_Action", "操作类型", "Action", "操作種別"),
    ("Au_Result", "结果", "Result", "結果"),
    ("Au_AllResults", "全部结果", "All Results", "全結果"),
    ("Au_Success", "成功", "Success", "成功"),
    ("Au_Failed", "失败", "Failed", "失敗"),
    ("Au_TargetType", "对象类型", "Target Type", "対象種別"),
    ("Au_TargetId", "对象 ID", "Target ID", "対象 ID"),
    ("Au_Detail", "详情", "Detail", "詳細"),
    ("Au_None", "暂无审计记录", "No audit records", "監査記録なし"),
    ("Au_List", "审计记录", "Audit Records", "監査記録"),
]


def append_resx(path: Path, values: dict[str, str], tag: str) -> list[str]:
    text = path.read_text(encoding="utf-8")
    existing = set(re.findall(r'<data name="(\w+)"', text))
    added: list[str] = []
    for key, value in values.items():
        if key in existing:
            continue
        block = (
            f'  <data name="{key}" xml:space="preserve">\n'
            f"    <value>{value}</value>\n"
            "  </data>\n"
        )
        # 插入 </root> 之前（保持文件尾部格式一致）
        text = text.replace("</root>", block + "</root>", 1)
        added.append(key)
    path.write_text(text, encoding="utf-8")
    print(f"[{tag}] +{len(added)} keys ({path.name})")
    return added


def append_strings_cs(keys: list[str]) -> list[str]:
    text = STRINGS_CS.read_text(encoding="utf-8")
    existing = set(re.findall(r'public static string (\w+) => S\(', text))
    added: list[str] = []
    lines = []
    for key in keys:
        if key in existing:
            continue
        lines.append(f'        public static string {key} => S("{key}", "{key}");')
        added.append(key)
    if lines:
        # 插入最后一个 } 之前（Web_* 区块尾部）
        idx = text.rstrip().rfind("}")
        text = text[:idx] + "\n".join(lines) + "\n" + text[idx:]
        STRINGS_CS.write_text(text, encoding="utf-8")
    print(f"[Strings.cs] +{len(added)} properties")
    return added


if __name__ == "__main__":
    zh = {f"Web_{k}": v for k, v, _, _ in KEYS}
    en = {f"Web_{k}": v for k, _, v, _ in KEYS}
    ja = {f"Web_{k}": v for k, _, _, v in KEYS}

    append_resx(RESX_DIR / "Strings.resx", zh, "zh")
    append_resx(RESX_DIR / "Strings.en.resx", en, "en")
    append_resx(RESX_DIR / "Strings.ja.resx", ja, "ja")
    append_strings_cs([f"Web_{k}" for k, _, _, _ in KEYS])
    print("done")
