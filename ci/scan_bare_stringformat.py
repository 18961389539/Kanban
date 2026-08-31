#!/usr/bin/env python3
"""扫描 WPF XAML 中的裸 StringFormat（无 {0} 占位符且不是合法格式说明符）。

WPF Binding.StringFormat 有两套语义，不能一刀切：
  1. 复合格式串（含 {0}）：直接交给 String.Format。
  2. 不含 {0} 时，WPF 会把值当标准/自定义格式说明符套用
     （等价于 {0:N0}），这是官方支持的写法——如 `StringFormat=N0` 用于显示千分位。
因此只有「既不含 {0}、也不是合法格式说明符」的裸字面量才是缺陷
（如 `StringFormat=OK` 会渲染成字面量 "OK"）。

审查修复 2026-08-13：一次性修正 30 处后加此守卫，防止再引入。
用法：python ci/scan_bare_stringformat.py
返回码：0=通过；1=存在裸字面量 StringFormat。
"""
import glob
import re
import sys

ROOTS = ["MainAPP/Views", "MainAPP/Controls", "MainAPP/Styles"]

# StringFormat 值：以引号/右花括号结尾；单引号包裹（'OEE = {0}'）合法，跳过
PATTERN = re.compile(r"StringFormat=([^}\"']*)")

# 标准格式说明符：单个字母 + 可选精度数字（N0 / C2 / P1 / F0 / D3 / X / G …）
STANDARD_SPEC = re.compile(r"^[A-Za-z]\d*$")
# 日期/时间自定义格式允许出现的字符（含中文字面量，如 yyyy年MM月）
DATE_CHARS = re.compile(r"^[yYMDdHhmsfFtzK\s:/\-.'\"\u4e00-\u9fff]+$")
# 重复占位符（yyyy / MM / dd / HH / mm / ss）或显式分隔符——用于把 "OK" 这类
# 恰好由单字母格式符拼成的串排除在外
DATE_REPEAT = re.compile(r"([yYMDdHhmsfF])\1")
DATE_SEPARATOR = re.compile(r"[:/\-.]")


def is_legal_bare_format(fmt: str) -> bool:
    """不含 {0} 时，判断是否为 WPF 支持的合法格式说明符。"""
    if STANDARD_SPEC.match(fmt):
        return True
    if DATE_CHARS.match(fmt) and (DATE_REPEAT.search(fmt) or DATE_SEPARATOR.search(fmt)):
        return True
    return False


def scan() -> list[str]:
    problems: list[str] = []
    files = []
    for root in ROOTS:
        files.extend(glob.glob(f"{root}/**/*.xaml", recursive=True))
    for path in files:
        with open(path, encoding="utf-8-sig") as handle:
            for i, line in enumerate(handle, 1):
                for m in PATTERN.finditer(line):
                    fmt = m.group(1).strip()
                    if not fmt or "{" in fmt:
                        continue
                    if is_legal_bare_format(fmt):
                        continue
                    problems.append(f"{path}:{i}: StringFormat={fmt}")
    return problems


def main() -> int:
    problems = scan()
    if problems:
        for p in problems:
            print(p, file=sys.stderr)
        print("裸 StringFormat（无 {0} 占位符且非合法格式说明符，会渲染成字面量）"
              f"共 {len(problems)} 处", file=sys.stderr)
        return 1
    print("OK: 无裸 StringFormat")
    return 0


if __name__ == "__main__":
    sys.exit(main())
