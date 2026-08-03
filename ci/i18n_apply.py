#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""XAML 中文字面量 → 多语言资源 批量迁移（zh/en/ja）。
用法: python i18n_apply.py
- 读 ci/i18n_batch1..4.json（中文原文 → [en, ja]）
- 对比 /tmp/zh_list.tsv（582 条唯一文案）找出遗漏
- 按频率生成 K001.. 语义 key，追加到三语 resx
- 替换 Views/MainWindow/Controls 下 XAML 的静态中文属性值为 {x:Static resources:Strings.Kxxx}
- 为每个 XAML 补 xmlns:resources
"""
import json, re, glob, os, collections, sys
from xml.sax.saxutils import escape

ROOT = r"D:\SourceCode\Kanban0731\Kanban"
BATCHES = [os.path.join(ROOT, "ci", f"i18n_batch{i}.json") for i in range(1, 5)]
RESX = {
    "zh": os.path.join(ROOT, "MainAPP", "Resources", "Strings.resx"),
    "en": os.path.join(ROOT, "MainAPP", "Resources", "Strings.en.resx"),
    "ja": os.path.join(ROOT, "MainAPP", "Resources", "Strings.ja.resx"),
}
XAML_FILES = (glob.glob(os.path.join(ROOT, "MainAPP", "Views", "*.xaml"))
              + [os.path.join(ROOT, "MainAPP", "MainWindow.xaml")]
              + glob.glob(os.path.join(ROOT, "MainAPP", "Controls", "*.xaml")))
# 语言下拉的母语自名不随 UI 语言翻译（保持 中文/English/日本語）
EXCLUDE = {"中文", "日本語"}

def strip_comments(text):
    return re.sub(r"<!--.*?-->", "", text, flags=re.S)

def load_batches():
    trans = {}
    for p in BATCHES:
        if os.path.exists(p):
            trans.update(json.load(open(p, encoding="utf-8")))
    return trans

def load_zh_list():
    items = []
    for line in open(r"/tmp/zh_list.tsv", encoding="utf-8"):
        parts = line.rstrip("\n").split("\t")
        if len(parts) >= 3:
            items.append((int(parts[0]), int(parts[1]), parts[2]))
    return items

def gen_keys(items):
    keys = {}
    for i, cnt, zh in items:
        keys[zh] = f"K{i:03d}"
    return keys

def append_resx(path, entries):
    text = open(path, encoding="utf-8").read()
    if not text.strip().endswith("</root>"):
        raise RuntimeError(f"resx 结构异常: {path}")
    existing = set(re.findall(r'<data name="([^"]+)"', text))
    new_entries = [(k, v) for k, v in entries if k not in existing]
    if new_entries:
        block = "\n".join(
            f'  <data name="{k}" xml:space="preserve"><value>{escape(v)}</value></data>'
            for k, v in new_entries)
        text = text.replace("</root>", block + "\n</root>")
        open(path, "w", encoding="utf-8").write(text)
    return len(new_entries)

STRINGS_CS = os.path.join(ROOT, "MainAPP", "Resources", "Strings.cs")

def append_strings_cs(prop_entries):
    """给 Strings.cs 追加 Kxxx 强类型属性（幂等）。prop_entries: [(key, zh_fallback)]"""
    text = open(STRINGS_CS, encoding="utf-8").read()
    existing = set(re.findall(r'public static string (K\d+)', text))
    lines = []
    for key, zh in prop_entries:
        if key in existing:
            continue
        lines.append(f'    public static string {key} => S("{key}", "{zh}");')
    if not lines:
        return 0
    block = "\n".join(lines) + "\n"
    # 在类结束的最后一个 "}" 前插入
    idx = text.rstrip().rfind("}")
    text = text[:idx] + block + text[idx:]
    open(STRINGS_CS, "w", encoding="utf-8").write(text)
    return len(lines)

def add_xmlns(text, fname):
    if "xmlns:resources=" in text:
        return text, False
    # 匹配根标签（含 xmlns:mc），把 xmlns:resources 插到标签的 '>' 之前
    m = re.search(r'(<(\w+)[^>]*?\sxmlns:mc="[^"]*"[^>]*?>)', text)
    if not m:
        return text, False
    ns = ' xmlns:resources="clr-namespace:MainAPP.Resources"'
    insert_at = m.end(1) - 1  # '>' 前
    return text[:insert_at] + ns + text[insert_at:], True

def replace_xaml(path, keys):
    text = open(path, encoding="utf-8-sig").read()
    orig = text
    text, added_ns = add_xmlns(text, path)
    body = strip_comments(text)  # 注释里的中文不替换，但保持原文本
    replaced = 0
    def repl(m):
        nonlocal replaced
        attr, val = m.group(1), m.group(2).strip()
        if val in EXCLUDE:
            return m.group(0)
        key = keys.get(val)
        if not key:
            return m.group(0)
        replaced += 1
        return f'{attr}="{{x:Static resources:Strings.{key}}}"'
    # 只替换非注释区：逐行处理，跳过注释行（简化为按行判断）
    lines = text.split("\n")
    in_comment = False
    out = []
    for ln in lines:
        if "<!--" in ln:
            in_comment = True
        if not in_comment:
            ln = re.sub(r'([\w.:]+)="([^"]*[\u4e00-\u9fa5][^"]*)"', repl, ln)
        if "-->" in ln:
            in_comment = False
        out.append(ln)
    text = "\n".join(out)
    if text != orig:
        open(path, "w", encoding="utf-8-sig").write(text)
    return replaced, added_ns

def main():
    trans = load_batches()
    items = load_zh_list()
    keys = gen_keys(items)

    # 1. 遗漏检查
    missing = [(zh, cnt) for i, cnt, zh in items if zh not in trans]
    if missing:
        print(f"⚠️ 遗漏 {len(missing)} 条未翻译（先补 ci/i18n_batch5.json）:")
        for zh, cnt in missing:
            print(f"   x{cnt}  {zh}")
        sys.exit(1)

    # 2. 生成 resx 条目（只加新 K key）+ Strings.cs 强类型属性
    prop_entries = []
    for lang, path in RESX.items():
        entries = []
        for i, cnt, zh in items:
            if zh in EXCLUDE:
                continue
            k = keys[zh]
            v = zh if lang == "zh" else trans[zh][0 if lang == "en" else 1]
            entries.append((k, v))
            if lang == "zh":
                prop_entries.append((k, zh))
        n = append_resx(path, entries)
        print(f"resx 追加 {n} 条 -> {os.path.basename(path)}")
    n_props = append_strings_cs(prop_entries)
    print(f"Strings.cs 追加 {n_props} 个属性")

    # 3. 替换 XAML
    total_replaced = 0
    for p in XAML_FILES:
        if not os.path.exists(p):
            continue
        n, ns = replace_xaml(p, keys)
        total_replaced += n
        if n or ns:
            print(f"  {os.path.basename(p):<42} 替换 {n:3d}  xmlns {'+'+'' if ns else ''}")
    print(f"\n合计替换 {total_replaced} 处 XAML 文案")

if __name__ == "__main__":
    main()
