#!/usr/bin/env python3
"""Baseline 维护工具：扫描 ViewModels/Services/Converters 中的硬编码中文显示串，
生成 LocalizationChineseLeakBaseline.txt（供 LocalizationGuardTests 使用）。

排除：注释行、nameof()、日志调用、异常构造、测试特性、样本数据文件。

用法：
  python ci/scan_chinese_leaks.py          # 维护模式：打印违规 + 重写 baseline
  python ci/scan_chinese_leaks.py --check  # CI 模式：只读，不写文件
                                           #   存在 baseline 之外的「新违规」时退出码 1
                                           #   baseline 缺失或文件不可读时退出码 1（拒绝静默放行）

清理已有违规后运行本脚本（不带 --check）更新 baseline。

⚠ 本脚本的排除规则与 MainAPP.Tests/Unit/LocalizationGuardTests.cs 的
  FindChineseDisplayStrings 必须保持一致，否则两侧算出的条目对不上，
  --check 会与实际测试结果互相矛盾。改任一侧时都要同步另一侧。
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
BASELINE_PATH = ROOT / "MainAPP.Tests" / "Unit" / "LocalizationChineseLeakBaseline.txt"

CJK = re.compile(r"[\u4e00-\u9fff]")
STRING_LIT = re.compile(r'@?\$?"[^"]*"')
EXCLUDE_FILES = {"SampleDeviceBuilder.cs", "SampleDataSeeder.cs", "SampleWorkOrderBuilder.cs"}


def is_excluded(trimmed):
    if trimmed.startswith("//") or trimmed.startswith("*"):
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


def scan():
    """返回 [(文件名, 行号, 原始字面量)]。"""
    leaks = []
    for d in DIRS:
        if not d.exists():
            continue
        for f in sorted(d.rglob("*.cs")):
            if f.name in EXCLUDE_FILES:
                continue
            for i, line in enumerate(f.read_text(encoding="utf-8").split("\n"), 1):
                if is_excluded(line.strip()):
                    continue
                for m in STRING_LIT.finditer(line):
                    if CJK.search(m.group()):
                        leaks.append((f.name, i, m.group()))
    return leaks


def to_entry(file_name, literal):
    """转成 baseline 条目，规则须与 C# 侧 FindChineseDisplayStrings 完全一致。"""
    clean = literal.strip().lstrip("$").strip('"').strip()
    return f"{file_name}\t{clean[:100]}"


def read_baseline():
    if not BASELINE_PATH.exists():
        print(f"ERROR: baseline 不存在：{BASELINE_PATH}", file=sys.stderr)
        print("       先运行 `python ci/scan_chinese_leaks.py` 生成。", file=sys.stderr)
        return None
    try:
        return {line for line in BASELINE_PATH.read_text(encoding="utf-8").splitlines() if line.strip()}
    except OSError as error:
        print(f"ERROR: 无法读取 baseline：{error}", file=sys.stderr)
        return None


def main():
    check_only = "--check" in sys.argv[1:]
    unknown = [a for a in sys.argv[1:] if a != "--check"]
    if unknown:
        print(f"ERROR: 未知参数 {unknown}", file=sys.stderr)
        return 2

    leaks = scan()
    for file_name, line_no, literal in leaks:
        print(f"{file_name}:{line_no}:{literal}")
    print(f"\nTotal: {len(leaks)} leaks")

    entries = {to_entry(n, lit) for n, _, lit in leaks}

    if check_only:
        baseline = read_baseline()
        if baseline is None:
            return 1
        new_leaks = sorted(entries - baseline)
        if new_leaks:
            print(f"\n发现 {len(new_leaks)} 处 baseline 之外的新增硬编码中文：", file=sys.stderr)
            for item in new_leaks:
                print(f"  {item}", file=sys.stderr)
            print("\n处理：优先迁移到 resx；确属有意的再不带 --check 重跑以更新 baseline。",
                  file=sys.stderr)
            return 1
        removed = len(baseline - entries)
        print(f"\nOK: 无新增硬编码中文（baseline 内 {len(entries)} 项"
              + (f"，其中 {removed} 项已修复、可清理 baseline" if removed else "") + "）")
        return 0

    BASELINE_PATH.write_text(
        "".join(line + "\n" for line in sorted(entries)), encoding="utf-8")
    print(f"\nBaseline written to {BASELINE_PATH} ({len(entries)} unique entries)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
