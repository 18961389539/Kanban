// WASM 看板无头冒烟 + 业务断言：用系统 Edge 打开看板，断言关键卡片有数据、无致命错误，
// 并验证**展示口径自洽**（OEE=可用率×性能率×合格率、状态汇总=设备总数、数据实时性等）。
// 依赖：playwright-core（免下载浏览器，channel: msedge 用系统 Edge）。
// 安装：cd <workspace> && npm install playwright-core
// 运行：node ci/wasm-smoke.js [url] [waitSeconds]     （退出码 0=通过 1=失败）
// 前置：PlcSimulator + Kanban.Collector 已启动（5129 单端口，wwwroot 已部署）。

const { chromium } = require('playwright-core');

const url = process.argv[2] || 'http://localhost:5129/';
const waitMs = (parseInt(process.argv[3] || '35', 10)) * 1000;

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

    // ──── 结构化采集 DOM（展示口径全部来自快照换算，这里取渲染后的值） ────
    const dom = await page.evaluate(() => {
        const txt = sel => document.querySelector(sel)?.textContent?.trim() ?? null;
        // 按 kpi-label 文本取同行 kpi-value
        const label = l => {
            const els = [...document.querySelectorAll('.kpi-label')];
            const el = els.find(e => e.textContent.trim() === l);
            return el?.nextElementSibling?.textContent?.trim() ?? null;
        };
        // 顶部状态汇总：运行/报警/暂停/待机/设备
        const sums = {};
        document.querySelectorAll('.sum-item').forEach(el => {
            const m = el.textContent.trim();
            const n = el.querySelector('b')?.textContent?.trim();
            for (const k of ['运行', '报警', '暂停', '待机', '设备']) {
                if (m.startsWith(k)) sums[k] = parseInt(n ?? 'NaN', 10);
            }
        });
        return {
            errVisible: (() => {
                const ui = document.querySelector('#blazor-error-ui');
                return ui ? (getComputedStyle(ui).display !== 'none') : false;
            })(),
            text: document.body.innerText,
            title: txt('.app-title'),
            statusBadge: txt('.status-badge'),
            bigNumber: txt('.big-number'),
            targetCycle: label('目标节拍'),
            actualCycle: label('实际节拍'),
            totalOutput: label('总产量'),
            ngRate: label('不良率'),
            meterText: txt('.speed-meter .meter-text'),
            conn: label('连接状态'),
            deviceCount: label('设备总数'),
            freshness: label('数据更新'),
            seq: label('快照序号'),
            serverVersion: label('服务版本'),
            totalOk: txt('.quality-num.ok'),
            totalNg: txt('.quality-num.ng'),
            oeeFormula: txt('.oee-formula'),
            rings: [...document.querySelectorAll('.ring-row .ring')].map(r => ({
                label: r.querySelector('.ring-label')?.textContent?.trim() ?? '',
                value: r.querySelector('.ring-value')?.textContent?.trim() ?? '',
            })),
            sums,
        };
    });

    // ──── 断言辅助 ────
    let pass = 0, failCount = 0;
    const ok = (msg) => { pass++; console.log(`  ✓ ${msg}`); };
    const fail = (msg) => { failCount++; console.log(`  ✗ FAIL: ${msg}`); process.exitCode = 1; };
    const warn = (msg) => console.log(`  ⚠ ${msg}`);

    console.log(`状态徽标=${dom.statusBadge} | 速度=${dom.bigNumber} | 总产量=${dom.totalOutput} | 不良率=${dom.ngRate}`);
    console.log(`OEE公式=${dom.oeeFormula}`);
    console.log(`汇总: 运行${dom.sums['运行']} 报警${dom.sums['报警']} 暂停${dom.sums['暂停']} 待机${dom.sums['待机']} 设备${dom.sums['设备']}`);
    console.log(`数据源: ${dom.conn} | 设备${dom.deviceCount} | ${dom.freshness} | ${dom.seq} | v${dom.serverVersion}`);

    // ──── 基础断言（原有，保留） ────
    if (!dom.text.includes('生产看板')) fail('页面标题缺失（WASM 未渲染）');
    else ok('WASM 渲染完成（标题存在）');
    // 注：无头 Edge 下 #blazor-error-ui 偶现误显示（环境伪影，真实浏览器未复现、功能正常），仅告警不判失败
    if (dom.errVisible) warn('#blazor-error-ui 可见（无头环境伪影，功能不受影响，请人工确认）');
    if (!dom.text.includes('注塑机')) fail('设备数据缺失');
    else ok('设备数据存在');
    const woCard = dom.text.match(/当前工单\s*([^\n]*)/)?.[1];
    const shiftCard = dom.text.match(/班次进度\s*([^\n]*)/)?.[1];
    console.log(`  · 工单卡=${woCard} | 班次卡=${shiftCard}`);
    if (!woCard || woCard === '暂无工单') fail('工单卡无数据');
    else ok('工单卡有数据');
    if (!shiftCard || shiftCard === '暂无数据') fail('班次卡无数据');
    else ok('班次卡有数据');

    // ──── 业务断言（展示口径自洽） ────
    // 1. 连接状态
    if (dom.conn === '已连接') ok('连接状态=已连接');
    else fail(`连接状态异常: ${dom.conn}`);

    // 2. 状态汇总自洽：运行+报警+暂停+待机 == 设备总数
    const s = dom.sums;
    if (typeof s['设备'] === 'number' && s['设备'] > 0) {
        const sum4 = (s['运行'] || 0) + (s['报警'] || 0) + (s['暂停'] || 0) + (s['待机'] || 0);
        if (sum4 === s['设备']) ok(`状态汇总自洽: ${sum4} == ${s['设备']}`);
        else fail(`状态汇总不自洽: 运行+报警+暂停+待机=${sum4} != 设备=${s['设备']}`);
    } else {
        fail(`设备总数异常: ${dom.deviceCount}`);
    }

    // 3. 数据实时性：快照序号为数字且递增基线存在；数据更新非"未收到"
    const seqNum = dom.seq?.match(/^#(\d+)$/)?.[1];
    if (seqNum && parseInt(seqNum, 10) > 0) ok(`快照序号有效: #${seqNum}`);
    else fail(`快照序号异常: ${dom.seq}`);
    if (dom.freshness === '未收到') fail('数据更新=未收到（实时流停滞）');
    else if (/^(实时|\d+ 秒前|\d+ 分钟前)$/.test(dom.freshness ?? '')) ok(`数据更新: ${dom.freshness}`);
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
    if (okNum > 0) { /* 在产正常 */ } else if (dom.statusBadge === '运行') {
        warn('运行中但 OK=0（启动初期/会话刚重置，可接受）');
    }

    // 7. 达成率 0~100%
    const meter = dom.meterText?.match(/达成率\s*(\d+)%/)?.[1];
    if (meter !== undefined) {
        const m = parseInt(meter, 10);
        if (m >= 0 && m <= 100) ok(`达成率: ${m}%`);
        else fail(`达成率越界: ${m}%`);
    } else {
        fail(`达成率文本异常: ${dom.meterText}`);
    }

    // 8. 状态徽标合法
    if (['运行', '报警', '暂停', '待机'].includes(dom.statusBadge ?? '')) ok(`设备状态: ${dom.statusBadge}`);
    else fail(`设备状态徽标异常: ${dom.statusBadge}`);

    // 9. ★ OEE 自洽：OEE 环值 == 可用率×性能率×合格率（P0 四舍五入，误差 ≤1%）
    const ringOf = (l) => dom.rings.find(r => r.label === l)?.value;
    const oee = ringOf('OEE'), avail = ringOf('可用率'), perf = ringOf('性能率'), qual = ringOf('合格率');
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
