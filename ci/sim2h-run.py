#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
sim2h-run.py - 2 小时模拟仿真监控 + 结果汇总（发布版程序，KANBAN_DATA_DIR=run-demo/sim2h）。

职责：
  1. 等待指定时长（默认 120 分钟），期间每 10 分钟写一行进度到 <DataRoot>/progress.log
  2. 到点后收集汇总：
     - Collector /metrics 计数（快照/报警事件/状态事件/订阅者峰值）
     - 四个 SQLite 库的表行数（production_logs / alarm_events / status_transitions / defect_history）
     - 报警事件按报警名聚合
     - 模拟器 sim_log 尾部
  3. 汇总写入 <DataRoot>/summary.txt 并打印

用法：
  python ci/sim2h-run.py --minutes 120 --data-root D:/SourceCode/Kanban0731/Kanban/run-demo/sim2h
"""
import argparse
import json
import os
import sqlite3
import time
import urllib.request
from datetime import datetime, timedelta

def log(msg, path):
    line = f"{datetime.now().strftime('%Y-%m-%d %H:%M:%S')} {msg}"
    print(line, flush=True)
    if path:
        try:
            with open(path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception as e:
            print(f"(progress 写入失败: {e})", flush=True)

def fetch_metrics(url):
    try:
        with urllib.request.urlopen(url, timeout=10) as r:
            return r.read().decode("utf-8", "replace")
    except Exception as e:
        return f"(metrics 获取失败: {e})"

def db_counts(data_root):
    """统计四个 SQLite 库所有表的行数。"""
    result = {}
    for db_name in ("production_logs.db", "alarm_events.db", "status_transitions.db", "defect_history.db"):
        path = os.path.join(data_root, "Config", db_name)
        if not os.path.exists(path):
            result[db_name] = "(库不存在)"
            continue
        try:
            con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
            tables = [r[0] for r in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchall()]
            counts = {}
            for t in tables:
                counts[t] = con.execute(f"SELECT COUNT(*) FROM \"{t}\"").fetchone()[0]
            con.close()
            result[db_name] = counts
        except Exception as e:
            result[db_name] = f"(读取失败: {e})"
    return result

def alarm_breakdown(data_root):
    path = os.path.join(data_root, "Config", "alarm_events.db")
    if not os.path.exists(path):
        return {}
    try:
        con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        rows = con.execute(
            "SELECT AlarmName, EventType, COUNT(*) FROM AlarmEvents GROUP BY AlarmName, EventType").fetchall()
        con.close()
        return {f"{name}({et})": c for name, et, c in rows}
    except Exception as e:
        return {"error": str(e)}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--minutes", type=int, default=120)
    ap.add_argument("--data-root", default=r"D:\SourceCode\Kanban0731\Kanban\run-demo\sim2h")
    ap.add_argument("--metrics", default="http://127.0.0.1:5129/metrics")
    args = ap.parse_args()

    total = timedelta(minutes=args.minutes)
    start = datetime.now()
    end = start + total
    progress = os.path.join(args.data_root, "progress.log")
    summary_path = os.path.join(args.data_root, "summary.txt")

    log(f"仿真监控启动：时长 {args.minutes} 分钟，预计结束 {end.strftime('%H:%M:%S')}", progress)

    next_log = start + timedelta(minutes=10)
    while datetime.now() < end:
        time.sleep(30)
        now = datetime.now()
        if now >= next_log:
            remain = end - now
            m = int(remain.total_seconds() // 60)
            log(f"进行中：剩余约 {m} 分钟（结束 {end.strftime('%H:%M:%S')}）", progress)
            next_log = now + timedelta(minutes=10)

    log("时长到点，开始收集汇总……", progress)

    # ── 汇总 ──
    lines = []
    lines.append("=" * 60)
    lines.append("2 小时模拟仿真结果汇总（发布版程序）")
    lines.append(f"仿真时段：{start.strftime('%Y-%m-%d %H:%M:%S')} ~ {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}（{args.minutes} 分钟）")
    lines.append("=" * 60)

    lines.append("\n[1] Collector /metrics")
    lines.append(fetch_metrics(args.metrics))

    lines.append("\n[2] SQLite 数据落库行数")
    for db, counts in db_counts(args.data_root).items():
        lines.append(f"  {db}: {counts}")

    lines.append("\n[3] 报警事件按报警名/类型聚合")
    breakdown = alarm_breakdown(args.data_root)
    if breakdown:
        for k, v in breakdown.items():
            lines.append(f"  {k}: {v} 次")
    else:
        lines.append("  (无)")

    sim_log = os.path.join(args.data_root, "Config", "sim_log.txt")
    lines.append("\n[4] 模拟器 sim_log 尾部 8 行")
    if os.path.exists(sim_log):
        with open(sim_log, encoding="utf-8", errors="replace") as f:
            lines.extend(f.read().splitlines()[-8:])
    else:
        lines.append("  (sim_log.txt 不存在)")

    summary = "\n".join(lines)
    with open(summary_path, "w", encoding="utf-8") as f:
        f.write(summary)
    log(f"汇总已写入 {summary_path}", progress)
    print("\n" + summary, flush=True)

if __name__ == "__main__":
    main()
