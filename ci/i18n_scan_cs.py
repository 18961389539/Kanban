# -*- coding: utf-8 -*-
import re, glob, collections, os
pat = re.compile(r'"((?:\\.|[^"\\])*[\u4e00-\u9fa5](?:\\.|[^"\\])*)"')
static, interp = [], []
for p in glob.glob('MainAPP/**/*.cs', recursive=True):
    if 'obj' in p or 'bin' in p or 'Designer' in p:
        continue
    base = os.path.basename(p)
    if base in ('SampleDeviceBuilder.cs', 'Strings.cs'):
        continue
    text = open(p, encoding='utf-8-sig').read()
    text = re.sub(r'//.*', '', text)
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    for m in pat.finditer(text):
        inner = m.group(1)
        if '{' in inner or '$' in inner or '%' in inner or '\\n' in inner or '+ ' in inner:
            interp.append((base, inner))
        else:
            static.append((base, inner))
print('纯静态: %d 处 | 插值/格式串: %d 处' % (len(static), len(interp)))
byf = collections.Counter(f for f, s in static)
for f, n in byf.most_common(15):
    print('  %3d  %s' % (n, f))
print('--- 静态样例 ---')
for f, s in static[:40]:
    print('  [%s] %s' % (f, s))
