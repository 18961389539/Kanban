#!/usr/bin/env python3
"""扫描 WPF XAML 中的裸 StringFormat（无 {0} 占位符）。

WPF Binding.StringFormat 走 String.Format(culture, format, value)：
格式串不含 {0} 时输出就是格式串本身（如 StringFormat=P1 会渲染成字面量 "P1"）。
审查修复 2026-08-13：一次性修正 30 处后加此守卫，防止再引入。
用法：python ci/scan_bare_stringformat.py [--check]
返回码：0=通过；1=存在裸 StringFormat（或 --check 时列出）。
"""
import glob
import re
import sys

ROOTS = ["MainAPP/Views", "MainAPP/Controls", "MainAPP/Styles"]

# StringFormat 值：以引号/右花括号结尾；单引号包裹（'OEE = {0}'）合法，跳过
PATTERN = re.compile(r"StringFormat=([^}\"']*)")


def scan() -> list[str]:
    problems: list[str] = []
    files = []
    for root in ROOTS:
        files.extend(glob.glob(f"{root}/**/*.xaml", recursive=True))
    for path in files:
        for i, line in enumerate(open(path, encoding="utf-8-sig"), 1):
            for m in PATTERN.finditer(line):
                fmt = m.group(1)
                if fmt and "{" not in fmt:
                    problems.append(f"{path}:{i}: StringFormat={fmt}")
    return problems


def main() -> int:
    problems = scan()
    if problems:
        for p in problems:
            print(p, file=sys.stderr)
        print(f"裸 StringFormat（缺 {0} 占位符，会渲染成字面量）共 {len(problems)} 处", file=sys.stderr)
        return 1
    print("OK: 无裸 StringFormat")
    return 0


if __name__ == "__main__":
    sys.exit(main())
