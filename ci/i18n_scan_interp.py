# -*- coding: utf-8 -*-
"""扫描用户可见的中文插值格式串（$"..."），排除日志行与业务数据。"""
import re, glob, collections, os

pat_interp = re.compile(r'\$"((?:\\.|[^"\\])*)"')
interp = []
for p in glob.glob('MainAPP/**/*.cs', recursive=True):
    if 'obj' in p or 'bin' in p or 'Designer' in p:
        continue
    base = os.path.basename(p)
    if base in ('SampleDeviceBuilder.cs', 'Strings.cs'):
        continue
    for i, line in enumerate(open(p, encoding='utf-8-sig').read().split('\n'), 1):
        if line.lstrip().startswith('//'):
            continue
        for m in pat_interp.finditer(line):
            inner = m.group(1)
            if not re.search(r'[\u4e00-\u9fa5]', inner):
                continue
            if re.search(r'_logger\.|Logger|Log\.|Log\(|WriteLine', line):
                continue
            interp.append((base, i, inner))

print('用户可见中文插值串: %d 处' % len(interp))
uniq = collections.Counter(inner for _, _, inner in interp)
print('唯一格式串: %d 个' % len(uniq))
byf = collections.Counter(b for b, _, _ in interp)
print('按文件分布:')
for f, n in byf.most_common(12):
    print('  %3d  %s' % (n, f))
print('\n--- 唯一样例 ---')
for s, c in uniq.most_common():
    print('x%2d  %s' % (c, s))
