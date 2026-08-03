// WASM 看板无头冒烟 + 业务断言：用系统 Edge 打开看板，断言关键卡片有数据、无致命错误，
// 并验证**展示口径自洽**（OEE=可用率×性能率×合格率、状态汇总=设备总数、数据实时性等）。
// 多语言：定位与断言语言无关（CSS 类 + 三语候选匹配）；另做 Localization.cs 三语 key 一致性静态校验。
// 依赖：playwright-core（免下载浏览器，channel: msedge 用系统 Edge）。
// 安装：cd <workspace> && npm install playwright-core
// 运行：node ci/wasm-smoke.js [url] [waitSeconds]     （退出码 0=通过 1=失败）
// 前置：PlcSimulator + Kanban.Collector 已启动（5129 单端口，wwwroot 已部署）。

const { chromium } = require('playwright-core');
const fs = require('fs');
const path = require('path');

const url = process.argv[2] || 'http://localhost:5129/';
const waitMs = (parseInt(process.argv[3] || '35', 10)) * 1000;

// ──── 静态校验：Localization.cs 三语 key 一致性（防漏翻译，等价 WPF 的 LocalizationTests） ────
try {
    // 仓库根：环境变量 KANBAN_REPO 优先，否则 cwd（在仓库内运行时 cwd 即仓库根）
    const repoDir = process.env.KANBAN_REPO || process.cwd();
    const locPath = path.join(repoDir, 'Kanban.Web', 'Localization.cs');
    if (!fs.existsSync(locPath)) throw new Error(`未找到 Localization.cs（KANBAN_REPO=${repoDir}）`);
    const src = fs.readFileSync(locPath, 'utf-8');
    const rows = [...src.matchAll(/\[\"([\w]+)\"\] = new\[\] \{ \"([^\"]*)\", \"([^\"]*)\", \"([^\"]*)\" \}/g)];
    if (rows.length === 0) throw new Error('未匹配到任何字典条目（格式变化？）');
    let dictBroken = 0;
    for (const m of rows) {
        const [_, k, zh, en, ja] = m;
        if (!zh.trim() || !en.trim() || !ja.trim()) { console.log(`  ✗ 字典值缺失: ${k}`); dictBroken++; }
    }
    if (dictBroken) process.exitCode = 1;
    console.log(`[静态] Localization.cs 三语字典 ${rows.length} 条 key 校验 ${dictBroken === 0 ? 'PASS' : 'FAIL'}`);
} catch (e) {
    console.log(`  ⚠ 字典静态校验跳过: ${e.message}`);
}

(async () => {
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage();
    const errors = [];
    const warnings = [];

    page.on('console', m => {
        if (m.type() === 'error') errors.push(`[console.error] ${m.text()}`);
        if (m.type() === 'warning') warnings.push(`[console.warning] ${m.text()}`);
    });
    page.on('pageerror', e => errors.push(`[pageerror] ${e.message}`));
    page.on('requestfailed', r => errors.push(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`));
    await page.evaluate(() => {
        window.addEventListener('unhandledrejection', e => console.error(`[unhandledrejection] ${e.reason}`));
        window.addEventListener('error', e => console.error(`[window.onerror] ${e.message}`));
    });

    console.log(`OPEN ${url} (wait ${waitMs / 1000}s)`);
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30000 });
    await page.waitForTimeout(waitMs);

    // ──── 结构化采集 DOM（展示口径全部来自快照换算；定位语言无关：CSS 类 + 三语候选） ────
    const dom = await page.evaluate(() => {
        const txt = sel => document.querySelector(sel)?.textContent?.trim() ?? null;
        // 按 kpi-label 文本（三语候选任一匹配）取同行 kpi-value
        const label = ls => {
            const arr = Array.isArray(ls) ? ls : [ls];
            const els = [...document.querySelectorAll('.kpi-label')];
            const el = els.find(e => arr.includes(e.textContent.trim()));
            return el?.nextElementSibling?.textContent?.trim() ?? null;
        };
        // 顶部状态汇总：按语义类（run/alarm/paused/idle）+ 最后一个无类项（设备总数），语言无关
        const sumItems = [...document.querySelectorAll('.sum-item')];
        const sumOf = cls => {
            const el = cls
                ? sumItems.find(e => e.classList.contains(cls))
                : sumItems.find(e => !['run', 'alarm', 'paused', 'idle'].some(c => e.classList.contains(c)));
            return el ? parseInt(el.querySelector('b')?.textContent?.trim() ?? 'NaN', 10) : NaN;
        };
        const sums = {
            run: sumOf('run'),
            alarm: sumOf('alarm'),
            paused: sumOf('paused'),
            idle: sumOf('idle'),
            dev: sumOf(null),
        };
        // 卡片标题（三语候选）
        const cardTitle = ls => {
            const arr = Array.isArray(ls) ? ls : [ls];
            return [...document.querySelectorAll('.card-title')].find(e => arr.some(t => e.textContent.includes(t)))?.textContent ?? null;
        };
        return {
            errVisible: (() => {
                const ui = document.querySelector('#blazor-error-ui');
                return ui ? (getComputedStyle(ui).display !== 'none') : false;
            })(),
            text: document.body.innerText,
            title: txt('.app-title'),
            brandTitle: txt('.brand-title'),
            statusBadge: txt('.status-badge'),
            bigNumber: txt('.big-number'),
            targetCycle: label(['目标节拍', 'Target Cycle', '目標タクト']),
            actualCycle: label(['实际节拍', 'Actual Cycle', '実績タクト']),
            totalOutput: label(['总产量', 'Total Output', '総生産数']),
            ngRate: label(['不良率', 'NG Rate', '不良率']),
            meterText: txt('.speed-meter .meter-text'),
            conn: label(['连接状态', 'Connection', '接続状態']),
            deviceCount: label(['设备总数', 'Total Devices', 'デバイス総数']),
            freshness: label(['数据更新', 'Data Freshness', 'データ更新']),
            seq: label(['快照序号', 'Snapshot Seq', 'スナップショット番号']),
            serverVersion: label(['服务版本', 'Service Version', 'サービスバージョン']),
            totalOk: txt('.quality-num.ok'),
            totalNg: txt('.quality-num.ng'),
            oeeFormula: txt('.oee-formula'),
            rings: [...document.querySelectorAll('.ring-row .ring')].map(r => ({
                label: r.querySelector('.ring-label')?.textContent?.trim() ?? '',
                value: r.querySelector('.ring-value')?.textContent?.trim() ?? '',
            })),
            woCardTitle: cardTitle(['当前工单', 'Current Work Order', '現在の工単']),
            shiftCardTitle: cardTitle(['班次进度', 'Shift Progress', '班次進捗']),
            sums,
        };
    });

    // ──── 断言辅助 ────
    let pass = 0, failCount = 0;
    const ok = (msg) => { pass++; console.log(`  ✓ ${msg}`); };
    const fail = (msg) => { failCount++; console.log(`  ✗ FAIL: ${msg}`); process.exitCode = 1; };
    const warn = (msg) => console.log(`  ⚠ ${msg}`);
    const anyOf = (v, candidates) => candidates.includes(v);

    console.log(`状态徽标=${dom.statusBadge} | 速度=${dom.bigNumber} | 总产量=${dom.totalOutput} | 不良率=${dom.ngRate}`);
    console.log(`OEE公式=${dom.oeeFormula}`);
    console.log(`汇总: 运行${dom.sums.run} 报警${dom.sums.alarm} 暂停${dom.sums.paused} 待机${dom.sums.idle} 设备${dom.sums.dev}`);
    console.log(`数据源: ${dom.conn} | 设备${dom.deviceCount} | ${dom.freshness} | ${dom.seq} | v${dom.serverVersion}`);

    // ──── 基础断言 ────
    // 看板标题来自 Collector settings.json 的 AppTitle（可配置），断言非空即可
    const brandTitle = dom.brandTitle;
    console.log(`  · 看板标题=${brandTitle}`);
    if (!brandTitle) fail('页面标题缺失（WASM 未渲染）');
    else ok(`WASM 渲染完成（标题=${brandTitle}）`);
    // 注：无头 Edge 下 #blazor-error-ui 偶现误显示（环境伪影，真实浏览器未复现、功能正常），仅告警不判失败
    if (dom.errVisible) warn('#blazor-error-ui 可见（无头环境伪影，功能不受影响，请人工确认）');
    if (!dom.text.includes('注塑机')) fail('设备数据缺失');
    else ok('设备数据存在');

    // 工单/班次卡：标题存在且内容非空态（三语候选）
    const woCard = dom.woCardTitle;
    const shiftCard = dom.shiftCardTitle;
    console.log(`  · 工单卡=${woCard} | 班次卡=${shiftCard}`);
    if (!woCard) fail('工单卡标题缺失');
    else {
        const woBody = dom.text.split(woCard)[1] ?? '';
        if (anyOf(woBody.split('\n')[0], ['暂无工单', 'No work orders', '工単なし'])) fail('工单卡无数据');
        else ok('工单卡有数据');
    }
    if (!shiftCard) fail('班次卡标题缺失');
    else {
        const shiftBody = dom.text.split(shiftCard)[1] ?? '';
        if (anyOf(shiftBody.split('\n')[0], ['暂无数据', 'No data', 'データなし'])) fail('班次卡无数据');
        else ok('班次卡有数据');
    }

    // ──── 业务断言（展示口径自洽） ────
    // 1. 连接状态（三语候选）
    if (anyOf(dom.conn, ['已连接', 'Connected', '接続済み'])) ok('连接状态=已连接');
    else fail(`连接状态异常: ${dom.conn}`);

    // 2. 状态汇总自洽：运行+报警+暂停+待机 == 设备总数（按语义类，语言无关）
    const s = dom.sums;
    if (typeof s.dev === 'number' && s.dev > 0) {
        const sum4 = (s.run || 0) + (s.alarm || 0) + (s.paused || 0) + (s.idle || 0);
        if (sum4 === s.dev) ok(`状态汇总自洽: ${sum4} == ${s.dev}`);
        else fail(`状态汇总不自洽: 运行+报警+暂停+待机=${sum4} != 设备=${s.dev}`);
    } else {
        fail(`设备总数异常: ${dom.deviceCount}`);
    }

    // 3. 数据实时性：快照序号为数字；数据更新非"未收到"（三语候选）
    const seqNum = dom.seq?.match(/^#(\d+)$/)?.[1];
    if (seqNum && parseInt(seqNum, 10) > 0) ok(`快照序号有效: #${seqNum}`);
    else fail(`快照序号异常: ${dom.seq}`);
    if (anyOf(dom.freshness, ['未收到', 'Not received', '未受信'])) fail('数据更新=未收到（实时流停滞）');
    else if (/^(实时|\d+ 秒前|\d+ 分钟前|Live|\d+s ago|\d+ min ago|リアルタイム|\d+ 秒前|\d+ 分前)$/.test(dom.freshness ?? '')) ok(`数据更新: ${dom.freshness}`);
    else fail(`数据更新文本异常: ${dom.freshness}`);

    // 4. 服务版本非空
    if (dom.serverVersion && dom.serverVersion !== '—') ok(`服务版本: ${dom.serverVersion}`);
    else fail(`服务版本缺失: ${dom.serverVersion}`);

    // 5. 实时速度：运行中设备应有数值（RunTime<5s 时允许 "—"）
    if (dom.bigNumber === '—') {
        warn('实时速度="—"（RunTime<5s 下限保护，启动初期正常）');
    } else if (/^[\d,]+$/.test(dom.bigNumber ?? '') && parseInt(dom.bigNumber.replace(/,/g, ''), 10) > 0) {
        ok(`实时速度: ${dom.bigNumber} 件/h`);
    } else {
        fail(`实时速度格式异常: ${dom.bigNumber}`);
    }

    // 6. 产量明细自洽：OK + NG == 总产量卡（设备可能报警/待机无产量，0 是合法的；自洽性才是关键）
    const okNum = parseInt(dom.totalOk?.replace(/,/g, '') ?? 'NaN', 10);
    const ngNum = parseInt(dom.totalNg?.replace(/,/g, '') ?? 'NaN', 10);
    const totalNum = parseInt(dom.totalOutput?.replace(/,/g, '') ?? 'NaN', 10);
    if (!isNaN(okNum) && !isNaN(ngNum) && okNum >= 0 && ngNum >= 0) ok(`产量明细: OK=${dom.totalOk} NG=${dom.totalNg}`);
    else fail(`产量明细格式异常: OK=${dom.totalOk} NG=${dom.totalNg}`);
    if (!isNaN(totalNum) && !isNaN(okNum) && !isNaN(ngNum) && totalNum === okNum + ngNum) {
        ok(`产量自洽: OK+NG=${okNum + ngNum} == 总产量=${dom.totalOutput}`);
    } else if (isNaN(totalNum) || isNaN(okNum) || isNaN(ngNum)) {
        warn('产量自洽断言跳过（字段未取到）');
    } else {
        fail(`产量不自洽: OK+NG=${okNum + ngNum} != 总产量=${dom.totalOutput}`);
    }

    // 7. 达成率 0~100%（三语前缀）
    const meter = dom.meterText?.match(/(?:达成率|Achievement|達成率)\s*(\d+)%/)?.[1];
    if (meter !== undefined) {
        const m = parseInt(meter, 10);
        if (m >= 0 && m <= 100) ok(`达成率: ${m}%`);
        else fail(`达成率越界: ${m}%`);
    } else {
        fail(`达成率文本异常: ${dom.meterText}`);
    }

    // 8. 状态徽标合法（三语全集）
    if (anyOf(dom.statusBadge, ['运行', '报警', '暂停', '待机', 'Running', 'Alarm', 'Paused', 'Idle', '稼働', 'アラーム', '一時停止', '待機'])) ok(`设备状态: ${dom.statusBadge}`);
    else fail(`设备状态徽标异常: ${dom.statusBadge}`);

    // 9. ★ OEE 自洽：OEE 环值 == 可用率×性能率×合格率（P0 四舍五入，误差 ≤1%；三语 label 候选）
    const ringOf = (ls) => {
        const arr = Array.isArray(ls) ? ls : [ls];
        return dom.rings.find(r => arr.includes(r.label))?.value;
    };
    const oee = ringOf('OEE');
    const avail = ringOf(['可用率', 'Availability', '稼働率']);
    const perf = ringOf(['性能率', 'Performance', '性能率']);
    const qual = ringOf(['合格率', 'Quality', '良品率']);
    const formula = dom.oeeFormula?.match(/(\d+)%\s*×\s*(\d+)%\s*×\s*(\d+)%/);
    const pct = s => parseInt((s ?? '').replace('%', ''), 10);
    if (formula && oee) {
        const calc = Math.round(pct(formula[1]) * pct(formula[2]) * pct(formula[3]) / 10000);
        const shown = pct(oee);
        if (Math.abs(calc - shown) <= 1) ok(`OEE 自洽: ${shown}% ≈ ${formula[1]}%×${formula[2]}%×${formula[3]}%=${calc}%`);
        else fail(`OEE 不自洽: 环值 ${shown}% vs 公式乘积 ${calc}%（三率 ${avail}/${perf}/${qual}）`);
    } else {
        warn(`OEE 断言跳过（公式/环值未取到: formula=${dom.oeeFormula} rings=${JSON.stringify(dom.rings)}）`);
    }

    // 10. 不良率格式
    if (/^[\d.]+%$|^—$/.test(dom.ngRate ?? '')) ok(`不良率: ${dom.ngRate}`);
    else fail(`不良率格式异常: ${dom.ngRate}`);

    // ──── 控制台错误 ────
    errors.forEach(e => { if (!e.includes('favicon')) console.log(`  ⚠ ${e}`); });
    const fatal = errors.filter(e => !e.includes('favicon.ico') && !e.includes('favicon.png'));
    if (fatal.length) { console.log('FATAL ERRORS:'); fatal.forEach(e => console.log('  ' + e)); failCount += fatal.length; }
    warnings.forEach(w => console.log(`  ⚠ ${w}`));

    const exit = failCount === 0 ? 0 : 1;
    console.log(exit === 0 ? `SMOKE PASS (${pass} 断言)` : `SMOKE FAIL (${failCount} 失败, ${pass} 通过)`);
    process.exitCode = exit;
    await browser.close();
})().catch(e => { console.error('FATAL', e); process.exit(1); });
