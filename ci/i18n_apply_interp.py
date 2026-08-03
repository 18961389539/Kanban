# -*- coding: utf-8 -*-
"""第四批：插值格式串迁移应用脚本。
1) 状态机扫描 $"...{...}..."（处理表达式内字符串字面量），排除日志/CSV表头/文件名
2) 排序分配 F001.. 编号（幂等：已生成的按现有 F key 续号）
3) 合并 i18n_interp_batch1..4.json 翻译，校验占位符一致
4) 追加 resx（zh=中文模板，en/ja=翻译）+ Strings.cs F 属性
5) 替换源码 $"...": 无洞 → Strings.Fxxx；有洞 → string.Format(Strings.Fxxx, args)
"""
import json, re, glob, os

RESX = {
    'zh': 'MainAPP/Resources/Strings.resx',
    'en': 'MainAPP/Resources/Strings.en.resx',
    'ja': 'MainAPP/Resources/Strings.ja.resx',
}
STRINGS_CS = 'MainAPP/Resources/Strings.cs'

def extract_interps(text):
    res = []
    i, n = 0, len(text)
    while i < n:
        j = text.find('$"', i)
        if j < 0:
            break
        # 跳过 // 注释行内的 $"（粗略：$" 前最近非空白 // 即认为注释）
        line_start = text.rfind('\n', 0, j) + 1
        prefix = text[line_start:j]
        if '//' in prefix:
            i = j + 2
            continue
        k = j + 2
        in_expr = False
        depth = 0
        while k < n:
            c = text[k]
            if in_expr:
                if c == '"':
                    k += 1
                    while k < n:
                        if text[k] == '\\':
                            k += 2
                            continue
                        if text[k] == '"':
                            break
                        k += 1
                    k += 1
                    continue
                if c == '{':
                    depth += 1
                elif c == '}':
                    depth -= 1
                    if depth == 0:
                        in_expr = False
                k += 1
            else:
                if c == '\\':
                    k += 2
                    continue
                if c == '{':
                    in_expr = True
                    depth = 1
                elif c == '"':
                    break
                k += 1
        res.append((j, k + 1, text[j+2:k]))
        i = k
    return res

def find_holes(inner):
    holes = []
    i, n = 0, len(inner)
    while i < n:
        if inner[i] != '{':
            i += 1
            continue
        j, depth, in_str = i + 1, 1, False
        while j < n and depth > 0:
            c = inner[j]
            if in_str:
                if c == '\\':
                    j += 2
                    continue
                if c == '"':
                    in_str = False
            else:
                if c == '"':
                    in_str = True
                elif c == '{':
                    depth += 1
                elif c == '}':
                    depth -= 1
            j += 1
        raw = inner[i+1:j-1]
        fmt = None
        d, s = 0, False
        for k2, ch in enumerate(raw):
            if s:
                if ch == '\\':
                    continue
                if ch == '"':
                    s = False
            else:
                if ch == '"':
                    s = True
                elif ch == '(':
                    d += 1
                elif ch == ')':
                    d -= 1
                elif ch == ':' and d == 0:
                    fmt = raw[k2+1:].strip()
                    raw = raw[:k2]
                    break
        holes.append((raw.strip(), fmt if fmt else None))
        i = j
    return holes

def template_of(inner, holes):
    out, pos, k, n = [], 0, 0, len(inner)
    while pos < n:
        if inner[pos] == '{':
            j, depth, in_str = pos + 1, 1, False
            while j < n and depth > 0:
                c = inner[j]
                if in_str:
                    if c == '\\':
                        j += 2
                        continue
                    if c == '"':
                        in_str = False
                else:
                    if c == '"':
                        in_str = True
                    elif c == '{':
                        depth += 1
                    elif c == '}':
                        depth -= 1
                j += 1
            fmt = holes[k][1]
            out.append('{%d%s}' % (k, ':' + fmt if fmt else ''))
            k += 1
            pos = j
        else:
            cs = pos
            while pos < n and inner[pos] != '{':
                pos += 1
            chunk = inner[cs:pos]
            chunk = chunk.replace('\\{', '\x00').replace('\\}', '\x01')
            chunk = chunk.replace('\\n', '\n').replace('\\t', '\t')
            chunk = chunk.replace('\\"', '"').replace('\\\\', '\\')
            chunk = chunk.replace('{', '{{').replace('}', '}}')
            chunk = chunk.replace('\x00', '{{').replace('\x01', '}}')
            out.append(chunk)
    return ''.join(out)

EXCLUDE = re.compile(r'^(#\s|.{1,12},[\{])')
def is_filename_tpl(tpl):
    return re.search(r'\{\d', tpl) and re.search(r'\.(csv|pdf)$', tpl.strip())

# 1. 收集
items = {}  # inner -> list[(file, holes)]
for p in glob.glob('MainAPP/**/*.cs', recursive=True):
    if 'obj' in p or 'bin' in p or 'Designer' in p:
        continue
    base = os.path.basename(p)
    if base in ('SampleDeviceBuilder.cs', 'Strings.cs'):
        continue
    text = open(p, encoding='utf-8-sig').read()
    for start, end, inner in extract_interps(text):
        if not re.search(r'[\u4e00-\u9fa5]', inner):
            continue
        line_no = text[:start].count('\n') + 1
        line = text.split('\n')[line_no - 1]
        if re.search(r'_logger\.|Logger|Log\.|Log\(|WriteLine', line):
            continue
        holes = find_holes(inner)
        tpl = template_of(inner, holes)
        if EXCLUDE.match(tpl.lstrip()) or is_filename_tpl(tpl):
            continue
        items.setdefault(inner, []).append((p, line_no, holes, tpl))

sorted_inner = sorted(items.keys())
# 幂等编号：读现有 Strings.cs 的 F key 最大值
existing = set()
if os.path.exists(STRINGS_CS):
    for m in re.finditer(r'public static string (F\d+)', open(STRINGS_CS, encoding='utf-8').read()):
        existing.add(int(m.group(1)[1:]))
next_no = max(existing) + 1 if existing else 1
key_map = {}
for inner in sorted_inner:
    if inner in key_map:
        continue
    key_map[inner] = 'F%03d' % next_no
    next_no += 1

# 2. 翻译合并
trans = {}
for b in glob.glob('ci/i18n_interp_batch*.json'):
    d = json.load(open(b, encoding='utf-8'))
    for k, v in d.items():
        trans[k] = v
print('翻译条目: %d  待迁移串: %d' % (len(trans), len(sorted_inner)))

# 3. 校验：每条都有翻译 + 占位符一致
missing = [inner for inner in sorted_inner if key_map[inner] not in trans]
if missing:
    print('!! 缺翻译 %d 条:' % len(missing))
    for inner in missing[:10]:
        print('  ', key_map[inner], template_of(inner, find_holes(inner))[:60])
    raise SystemExit(1)

def placeholders(t):
    return set(re.findall(r'\{(\d+)', t))

for inner in sorted_inner:
    k = key_map[inner]
    tpl = template_of(inner, find_holes(inner))
    ph_tpl = placeholders(tpl)
    for lang, t in zip(('en', 'ja'), trans[k]):
        ph = placeholders(t)
        if ph != ph_tpl:
            print('!! 占位符不一致 %s[%s]: 模板%s 翻译%s' % (k, lang, sorted(ph_tpl), sorted(ph)))
            print('   模板:', tpl[:80])
            print('   翻译:', t[:80])
            raise SystemExit(1)

# 4. 追加 resx
def xml_escape(v):
    return v.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')

def append_resx(path, entries):
    text = open(path, encoding='utf-8').read()
    existing_keys = set(re.findall(r'<data name="([^"]+)"', text))
    new = [(k, v) for k, v in entries if k not in existing_keys]
    if new:
        block = '\n'.join('  <data name="%s" xml:space="preserve"><value>%s</value></data>' % (k, xml_escape(v)) for k, v in new)
        text = text.replace('</root>', block + '\n</root>')
        open(path, 'w', encoding='utf-8').write(text)
    return len(new)

for lang, path in RESX.items():
    entries = []
    for inner in sorted_inner:
        k = key_map[inner]
        if lang == 'zh':
            v = template_of(inner, find_holes(inner))
        else:
            v = trans[k][0 if lang == 'en' else 1]
        entries.append((k, v))
    n = append_resx(path, entries)
    print('resx %s 追加 %d 条' % (lang, n))

# 5. 追加 Strings.cs
cs_text = open(STRINGS_CS, encoding='utf-8').read()
def cs_escape(v):
    return v.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\t', '\\t')
new_props = []
for inner in sorted_inner:
    k = key_map[inner]
    if ('public static string %s =>' % k) in cs_text:
        continue
    tpl = template_of(inner, find_holes(inner))
    new_props.append('    public static string %s => S("%s", "%s");' % (k, k, cs_escape(tpl)))
if new_props:
    cs_text = cs_text.replace('}\n', '\n' + '\n'.join(new_props) + '\n}\n', 1)
    open(STRINGS_CS, 'w', encoding='utf-8').write(cs_text)
print('Strings.cs 追加 %d 属性' % len(new_props))

# 6. 替换源码
changed = []
by_file = {}
for inner, lst in items.items():
    for p, line_no, holes, tpl in lst:
        by_file.setdefault(p, []).append((inner, holes))
for p, lst in by_file.items():
    text = open(p, encoding='utf-8').read()
    repls = []
    for start, end, inner in extract_interps(text):
        if inner not in key_map:
            continue
        holes = find_holes(inner)
        k = key_map[inner]
        if not holes:
            repl = 'Strings.%s' % k
        else:
            args = ', '.join(e for e, _ in holes)
            repl = 'string.Format(Strings.%s, %s)' % (k, args)
        repls.append((start, end, repl))
    if not repls:
        continue
    # 从后往前替换
    for start, end, repl in sorted(repls, key=lambda r: -r[0]):
        text = text[:start] + repl + text[end:]
    open(p, 'w', encoding='utf-8').write(text)
    changed.append((os.path.basename(p), len(repls)))
print('替换文件 %d 个，共 %d 处' % (len(changed), sum(n for _, n in changed)))
for f, n in sorted(changed):
    print('  %3d  %s' % (n, f))
