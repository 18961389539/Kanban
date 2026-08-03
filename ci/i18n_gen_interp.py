# -*- coding: utf-8 -*-
"""状态机扫描中文插值串（处理表达式内字符串字面量），生成候选清单 TSV。"""
import re, glob, collections, os

def extract_interps(text):
    """返回 [(start, end, inner), ...]：状态机处理 {表达式} 内的 "..." 字符串字面量。"""
    res = []
    i, n = 0, len(text)
    while i < n:
        # 找 $"
        j = text.find('$"', i)
        if j < 0:
            break
        k = j + 2
        in_expr = False
        depth = 0
        while k < n:
            c = text[k]
            if in_expr:
                if c == '"':
                    # 字符串字面量：跳到配对
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
                    break  # 插值串结束
                k += 1
        res.append((j, k, text[j+2:k]))
        i = k
    return res

def find_holes(inner):
    """返回 [(expr, fmt_or_None), ...]。"""
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
    """洞 → {i:fmt}；文本段先转义再插占位符（避免占位符 {0:P2} 被二次转义）。"""
    out = []
    pos, k, n = 0, 0, len(inner)
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

items = {}
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
        if EXCLUDE.match(tpl.lstrip()):
            continue  # CSV 表头 / # 注释行（数据文件内容）
        if re.search(r'\{?\d[^}]*\}?.*\.(csv|pdf)$', tpl.strip()) and re.search(r'\{\d', tpl):
            continue  # 导出文件名模板（跨语言保持稳定，不本地化）
        items.setdefault(inner, []).append((base, line_no, holes))

sorted_items = sorted(items.items(), key=lambda kv: kv[0])
print('待迁移唯一串: %d  总处: %d' % (len(sorted_items), sum(len(v) for v in items.values())))
with open('ci/interp_candidates.tsv', 'w', encoding='utf-8') as f:
    for idx, (inner, locs) in enumerate(sorted_items, 1):
        holes = locs[0][2]
        tpl = template_of(inner, holes)
        args = ', '.join(e for e, _ in holes)
        f.write('F%03d\t%s\t%d\t%d\t%s\t%s:%d\n' % (idx, tpl, len(locs), len(holes), args, locs[0][0], locs[0][1]))
print('写入 ci/interp_candidates.tsv')
