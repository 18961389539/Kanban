# -*- coding: utf-8 -*-
"""C# 用户可见静态消息 → 资源 批量迁移（M 前缀，编号稳定可幂等）。
读 ci/i18n_cs_batch.json（中文 → [en, ja]）：
- 编号：优先沿用 Strings.cs 已存在的 M key（中文反查），新词按 JSON 顺序续号——编号永不漂移
- 只处理含命中映射字符串的文件（避免 BOM/using 污染无关文件）
- 写回保留原 BOM 状态
"""
import re, glob, os, json
from xml.sax.saxutils import escape

ROOT = r"D:\SourceCode\Kanban0731\Kanban"
BATCH = os.path.join(ROOT, "ci", "i18n_cs_batch.json")
RESX = {
    "zh": os.path.join(ROOT, "MainAPP", "Resources", "Strings.resx"),
    "en": os.path.join(ROOT, "MainAPP", "Resources", "Strings.en.resx"),
    "ja": os.path.join(ROOT, "MainAPP", "Resources", "Strings.ja.resx"),
}
STRINGS_CS = os.path.join(ROOT, "MainAPP", "Resources", "Strings.cs")
LOG_RE = re.compile(r'\.(?:Log|LogDebug|LogInformation|LogWarning|LogError|LogVerbose)\(|Log\.(?:Debug|Information|Warning|Error)\(')

def read_bom(path):
    with open(path, 'rb') as f:
        return f.read(3) == b'\xef\xbb\xbf'

def write_preserve_bom(path, text, had_bom):
    enc = 'utf-8-sig' if had_bom else 'utf-8'
    open(path, 'w', encoding=enc, newline='').write(text)

def existing_m_keys():
    """从 Strings.cs 提取 {key: 中文fallback}（幂等/编号稳定的基础）。"""
    text = open(STRINGS_CS, encoding='utf-8-sig').read()
    out = {}
    for m in re.finditer(r'public static string (M\d+) => S\("(?:M\d+)", "([^"]*)"\);', text):
        out[m.group(1)] = m.group(2)
    return out

def append_resx(path, entries):
    text = open(path, encoding='utf-8-sig').read()
    existing = set(re.findall(r'<data name="([^"]+)"', text))
    new_entries = [(k, v) for k, v in entries if k not in existing]
    if new_entries:
        block = "\n".join(
            '  <data name="%s" xml:space="preserve"><value>%s</value></data>' % (k, escape(v))
            for k, v in new_entries)
        text = text.replace("</root>", block + "\n</root>")
        open(path, 'w', encoding='utf-8-sig').write(text)
    return len(new_entries)

def append_strings_cs(prop_entries):
    text = open(STRINGS_CS, encoding='utf-8-sig').read()
    existing = set(re.findall(r'public static string (M\d+)', text))
    lines = []
    for key, zh in prop_entries:
        if key in existing:
            continue
        lines.append('    public static string %s => S("%s", "%s");' % (key, key, zh))
    if lines:
        idx = text.rstrip().rfind("}")
        text = text[:idx] + "\n".join(lines) + "\n" + text[idx:]
        open(STRINGS_CS, 'w', encoding='utf-8-sig').write(text)
    return len(lines)

def replace_in_file(path, mapping):
    had_bom = read_bom(path)
    text = open(path, encoding='utf-8-sig').read()
    lines = text.split("\n")
    out = []
    replaced = 0
    hit = False
    for line in lines:
        if LOG_RE.search(line) or "Strings." in line:
            out.append(line)
            continue
        line2 = re.sub(r'//.*', '', line)
        def repl(m):
            nonlocal replaced
            v = m.group(0)[1:-1]
            key = mapping.get(v)
            if not key:
                return m.group(0)
            replaced += 1
            return 'Strings.%s' % key
        newline = re.sub(r'"([^"$]*[\u4e00-\u9fa5][^"]*)"', repl, line2)
        if newline != line2:
            hit = True
            if line2 in line:
                line = line.replace(line2, newline)
            else:
                line = newline
        out.append(line)
    if not hit:
        return 0, False
    text = "\n".join(out)
    if 'using MainAPP.Resources;' not in text:
        m = re.search(r'(^using [^;]+;\n)', text, re.M)
        if m:
            text = text[:m.end(1)] + "using MainAPP.Resources;\n" + text[m.end(1):]
    write_preserve_bom(path, text, had_bom)
    return replaced, True

def main():
    trans = json.load(open(BATCH, encoding='utf-8'))
    existing = existing_m_keys()
    existing_rev = {zh: k for k, zh in existing.items()}
    nums = sorted(int(k[1:]) for k in existing)
    next_no = (nums[-1] + 1) if nums else 1

    mapping = {}
    for zh, pair in trans.items():
        if zh in existing_rev:
            mapping[zh] = existing_rev[zh]
        else:
            key = 'M%03d' % next_no
            next_no += 1
            mapping[zh] = key
            existing[key] = zh  # 本批新增，后续重复跑沿用

    prop_entries = []
    for lang, path in RESX.items():
        entries = []
        for zh, pair in trans.items():
            k = mapping[zh]
            v = zh if lang == 'zh' else pair[0 if lang == 'en' else 1]
            entries.append((k, v))
            if lang == 'zh':
                prop_entries.append((k, zh))
        n = append_resx(path, entries)
        print('resx 追加 %d 条 -> %s' % (n, os.path.basename(path)))
    n_props = append_strings_cs(prop_entries)
    print('Strings.cs 追加 %d 个属性' % n_props)

    total = 0
    for p in glob.glob(os.path.join(ROOT, 'MainAPP', '**', '*.cs'), recursive=True):
        if 'obj' in p or 'bin' in p or 'Designer' in p:
            continue
        base = os.path.basename(p)
        if base in ('Strings.cs', 'Localization.cs', 'SampleDeviceBuilder.cs'):
            continue
        n, changed = replace_in_file(p, mapping)
        if n or changed:
            print('  %-46s 替换 %3d' % (base, n))
        total += n
    print('合计替换 %d 处 C# 用户消息' % total)

if __name__ == '__main__':
    main()
