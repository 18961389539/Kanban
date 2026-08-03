# -*- coding: utf-8 -*-
"""提取 C# 用户可见静态中文消息（排除日志/数据类），按调用点模式精确匹配。"""
import re, glob, collections, os, json

# 用户可见调用点模式：Notify*/MessageBox/dialog.Show/Exception/Validate return/属性赋值/Stub 集合
PATTERNS = [
    r'Notify(?:Success|Warning|Error|Info)\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'MessageBox\.Show\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'_?dialog\.Show\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'\.ShowPasswordInput\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'new \w*(?:Exception|Error)\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'throw new \w*Exception\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'return\s+"([^"$]*[\u4e00-\u9fa5][^"]*)";',
    r'StatusMessage\s*=\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'\.(?:Warning|Info|Success)\.Add\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
    r'Confirm\(\s*"([^"$]*[\u4e00-\u9fa5][^"]*)"',
]
# 排除：日志 API 行的字符串、已有 Strings. 引用的行
LOG_RE = re.compile(r'\.(?:Log|LogDebug|LogInformation|LogWarning|LogError|LogVerbose)\(|Log\.(?:Debug|Information|Warning|Error)\(')

msgs = collections.Counter()
loc = {}
for p in glob.glob('MainAPP/**/*.cs', recursive=True):
    if 'obj' in p or 'bin' in p or 'Designer' in p:
        continue
    base = os.path.basename(p)
    if base in ('SampleDeviceBuilder.cs', 'Strings.cs', 'Localization.cs'):
        continue
    text = open(p, encoding='utf-8-sig').read()
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    lines = text.split('\n')
    for i, line in enumerate(lines):
        if LOG_RE.search(line):
            continue  # 日志行不迁移
        if 'Strings.' in line:
            continue  # 已资源化
        line2 = re.sub(r'//.*', '', line)
        for pat in PATTERNS:
            for m in re.finditer(pat, line2):
                v = m.group(1)
                if len(v) > 80:
                    continue  # 超长跳过
                msgs[v] += 1
                loc.setdefault(v, []).append(base)

items = sorted(msgs.items(), key=lambda x: -x[1])
with open(r'D:\SourceCode\Kanban0731\Kanban\ci\cs_msgs.tsv', 'w', encoding='utf-8') as f:
    for i, (v, c) in enumerate(items, 1):
        f.write('%d\t%d\t%s\t%s\n' % (i, c, v, ','.join(sorted(set(loc[v]))[:2])))
print('唯一用户可见静态消息: %d 处（总实例 %d）' % (len(items), sum(msgs.values())))
for i, (v, c) in enumerate(items[:30], 1):
    print('%3d x%d  %s' % (i, c, v))
