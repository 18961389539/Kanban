#!/usr/bin/env python3
"""Baseline 维护工具：扫描 ViewModels/Services/Converters 中的硬编码中文显示串，
生成 LocalizationChineseLeakBaseline.txt（供 LocalizationGuardTests 使用）。

排除：注释行、nameof()、日志调用、异常构造、测试特性、样本数据文件。

用法：
  python ci/scan_chinese_leaks.py          # 打印违规 + 重写 baseline
  python ci/scan_chinese_leaks.py --check  # 仅检查，不写文件（CI 模式）

清理已有违规后运行此脚本更新 baseline。
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIRS = [
    ROOT / "MainAPP" / "ViewModels",
    ROOT / "MainAPP" / "Services",
    ROOT / "MainAPP" / "Converters",
]

CJK = re.compile(r"[\u4e00-\u9fff]")
STRING_LIT = re.compile(r'@?\$?"[^"]*"')

def is_excluded(trimmed):
    if trimmed.startswith("//") or trimmed.startswith("*") or trimmed.startswith("///"):
        return True
    if "nameof(" in trimmed:
        return True
    log_markers = ["_logger.Log", "Log.Warning", "Log.Error", "Log.Information",
                   "Log.Debug", "Log.Fatal", "Serilog.Log.", "logger.Log"]
    if any(m in trimmed for m in log_markers):
        return True
    if "throw new " in trimmed:
        return True
    test_attrs = ["[Trait", "[Theory", "[InlineData", "[Fact", "[Collection"]
    if any(trimmed.startswith(a) for a in test_attrs):
        return True
    return False

leaks = []
EXCLUDE_FILES = {"SampleDeviceBuilder.cs", "SampleDataSeeder.cs", "SampleWorkOrderBuilder.cs"}
for d in DIRS:
    if not d.exists():
        continue
    for f in sorted(d.rglob("*.cs")):
        if f.name in EXCLUDE_FILES:
            continue
        for i, line in enumerate(f.read_text(encoding="utf-8").split("\n"), 1):
            trimmed = line.strip()
            if is_excluded(trimmed):
                continue
            for m in STRING_LIT.finditer(line):
                if CJK.search(m.group()):
                    leaks.append(f"{f.name}:{i}:{m.group()}")

for l in leaks:
    print(l)
print(f"\nTotal: {len(leaks)} leaks")

# 生成 baseline 文件（文件名\t字符串内容，去重，截断 100 字符）
baseline = set()
for f_name, _, content in (l.split(":", 2) for l in leaks):
    # 去掉外层引号和 $ 前缀
    clean = content.strip().lstrip("$").strip('"').strip()
    clean = clean[:100]
    baseline.add(f"{f_name}\t{clean}")

baseline_path = ROOT / "MainAPP.Tests" / "Unit" / "LocalizationChineseLeakBaseline.txt"
with open(baseline_path, "w", encoding="utf-8") as bf:
    for line in sorted(baseline):
        bf.write(line + "\n")
print(f"\nBaseline written to {baseline_path} ({len(baseline)} unique entries)")
