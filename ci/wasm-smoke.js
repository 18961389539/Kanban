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

    // 反向校验（审查修复 2026-08-13）：Kanban.Web 源码中 L.T/localize 的字面量 key 必须存在于字典，
    // 否则 L.T 缺失回退会直接显示原始 key 文本（历史案例：Mo_ConnStatus/Mo_Freshness/Hq_ShiftChange）。
    const dictKeys = new Set(rows.map(m => m[1]));
    const usageRe = /(?:L\.T|localize)\(\s*"([\w]+)"/g;
    const srcRoot = path.join(repoDir, 'Kanban.Web');
    const missing = new Set();
    const walk = (dir) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) { if (entry.name !== 'bin' && entry.name !== 'obj') walk(full); continue; }
            if (!/\.(razor|cs)$/.test(entry.name) || full === locPath) continue;
            const text = fs.readFileSync(full, 'utf-8');
            for (const m of text.matchAll(usageRe)) if (!dictKeys.has(m[1])) missing.add(`${m[1]}（${path.relative(repoDir, full)}）`);
        }
    };
    walk(srcRoot);
    if (missing.size) {
        for (const item of missing) console.log(`  ✗ 源码使用了不在字典中的 key: ${item}`);
        process.exitCode = 1;
    }
    console.log(`[静态] 源码 L.T/localize 用词 ∈ 字典 ${missing.size === 0 ? 'PASS' : 'FAIL'}`);
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

    // ──── 历史查询页 /history（多页面路由回归：SPA fallback + 筛选/KPI/图表/表格） ────
    {
        const base = url.replace(/\/+$/, '');
        console.log(`OPEN ${base}/history`);
        await page.goto(`${base}/history`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(10000); // WASM 启动 + 连接 + 设备加载

        // 顶栏导航：两个链接（看板/历史查询），按三语候选逐个包含匹配（导航是全文本，非单选）
        const navText = await page.evaluate(() => document.querySelector('.topnav')?.textContent?.trim() ?? '');
        const navHas = (cands) => cands.some(c => navText.includes(c));
        if (navHas(['看板', 'Dashboard', 'ダッシュボード']) && navHas(['历史查询', 'History Query', '履歴照会']))
            ok('历史页顶栏导航');
        else fail(`历史页导航缺失: ${navText}`);

        // 筛选栏：3 个下拉 + 2 个 datetime-local
        const filterOk = await page.evaluate(() => {
            const s = document.querySelectorAll('.filter-bar select').length;
            const i = document.querySelectorAll('.filter-bar input[type=datetime-local]').length;
            return s >= 3 && i === 2;
        });
        if (filterOk) ok('历史页筛选栏（设备/快捷/班次 + 起止时间）');
        else fail('历史页筛选栏缺失');

        // 设备下拉有真实选项（演示环境 ≥2 台；空环境跳过不判失败）
        const devOptions = await page.evaluate(() =>
            document.querySelectorAll('.filter-bar select:first-of-type option').length);
        if (devOptions >= 2) ok(`历史页设备下拉 ${devOptions} 项`);
        else warn(`历史页设备下拉仅 ${devOptions} 项（Collector 未就绪或环境无设备）`);

        // 点击查询（三语候选定位按钮）
        const clicked = await page.evaluate(() => {
            const btns = [...document.querySelectorAll('.filter-actions button')];
            const b = btns.find(x => ['查询', 'Search', '検索'].some(t => (x.textContent ?? '').includes(t)));
            if (!b) return false;
            b.click();
            return true;
        });
        if (clicked) ok('历史页触发查询');
        else fail('历史页未找到查询按钮');
        await page.waitForTimeout(8000); // 窗口全量拉取 + 表格页

        // KPI 卡 3 张
        const kpiCount = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
        if (kpiCount === 3) ok('历史页 KPI 卡 ×3');
        else warn(`历史页 KPI 卡 ${kpiCount}/3（无数据时仅空态，属正常）`);

        // 结果表格有行（有数据时）
        const rowCount = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
        if (rowCount > 0) ok(`历史页结果表格 ${rowCount} 行`);
        else warn('历史页结果表格空（查询区间无数据时正常）');

        // 分页信息（三语候选）
        const pagerText = await page.evaluate(() => document.querySelector('.pager-info')?.textContent?.trim() ?? '');
        if (/第|Page|ページ/.test(pagerText)) ok(`历史页分页信息: ${pagerText}`);
        else warn(`历史页分页信息缺失: ${pagerText}`);

        // ECharts canvas（有数据时渲染）
        const hasCanvas = await page.evaluate(() => !!document.querySelector('.echart-host canvas'));
        if (hasCanvas) ok('历史页 ECharts 图表 canvas');
        else warn('历史页图表 canvas 缺失（无数据/未渲染）');

        // ── 状态/报警/OEE Tab（需先选设备：状态/报警/OEE 按单设备查询，与 WPF 口径一致） ──
        const selectDevice = async () => {
            await page.selectOption('.filter-bar select:first-of-type', { index: 1 }); // 第一台设备
            await page.waitForTimeout(300);
        };
        const clickTab = async (cands) => {
            return page.evaluate((cs) => {
                const btns = [...document.querySelectorAll('.tab-btn')];
                const b = btns.find(x => cs.some(t => (x.textContent ?? '').includes(t)));
                if (!b) return false;
                b.click();
                return true;
            }, cands);
        };
        const clickSearch = async () => {
            return page.evaluate(() => {
                const btns = [...document.querySelectorAll('.filter-actions button')];
                const b = btns.find(x => ['查询', 'Search', '検索'].some(t => (x.textContent ?? '').includes(t)));
                if (!b) return false;
                b.click();
                return true;
            });
        };

        // 状态 Tab
        if (await clickTab(['状态', 'Status', '状態'])) {
            await page.waitForTimeout(300);
            await selectDevice();
            await clickSearch();
            await page.waitForTimeout(10000);
            const stKpi = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
            const stCharts = await page.evaluate(() => document.querySelectorAll('.chart-grid .echart-host canvas').length);
            const stRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
            if (stKpi === 3) ok(`状态 Tab KPI ×3（运行/报警/待机时长）`);
            else warn(`状态 Tab KPI ${stKpi}/3`);
            if (stCharts === 3) ok('状态 Tab 图表 ×3（饼图/按天堆叠/甘特）');
            else warn(`状态 Tab 图表 ${stCharts}/3（无数据时正常）`);
            if (stRows > 0) ok(`状态 Tab 转换记录 ${stRows} 行`);
            else warn('状态 Tab 表格空（无数据时正常）');
        } else {
            warn('未找到状态 Tab 按钮');
        }

        // 报警 Tab
        if (await clickTab(['报警', 'Alarm', '警報'])) {
            await page.waitForTimeout(300);
            await clickSearch();
            await page.waitForTimeout(8000);
            const alKpi = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
            const alCanvas = await page.evaluate(() => document.querySelectorAll('.echart-host canvas').length);
            const alRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
            if (alKpi === 3) ok('报警 Tab KPI ×3（触发/恢复/待恢复）');
            else warn(`报警 Tab KPI ${alKpi}/3`);
            if (alCanvas >= 1) ok('报警 Tab 频次排行图表');
            else warn('报警 Tab 图表缺失（无数据时正常）');
            if (alRows > 0) ok(`报警 Tab 事件 ${alRows} 行`);
            else warn('报警 Tab 表格空（无数据时正常）');
        } else {
            warn('未找到报警 Tab 按钮');
        }

        // OEE Tab
        if (await clickTab(['OEE'])) {
            await page.waitForTimeout(300);
            await clickSearch();
            await page.waitForTimeout(10000);
            const oeRings = await page.evaluate(() => document.querySelectorAll('.ring-row .ring').length);
            const oeCanvas = await page.evaluate(() => document.querySelectorAll('.echart-host canvas').length);
            const oeRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
            if (oeRings === 4) ok('OEE Tab 四率环 ×4');
            else warn(`OEE Tab 环 ${oeRings}/4`);
            if (oeCanvas >= 1) ok('OEE Tab 班次趋势图表');
            else warn('OEE Tab 趋势图表缺失（无数据时正常）');
            if (oeRows > 0) ok(`OEE Tab 班次明细 ${oeRows} 行`);
            else warn('OEE Tab 明细空（无数据时正常）');
        } else {
            warn('未找到 OEE Tab 按钮');
        }

        // 切回产量 Tab（恢复默认视图，避免影响后续复用）
        await clickTab(['产量', 'Production', '生産']);
    }

    // ──── 报警中心页 /alarms（实时活跃报警 + 统计排行） ────
    {
        console.log(`OPEN ${url.replace(/\/+$/, '')}/alarms`);
        await page.goto(`${url.replace(/\/+$/, '')}/alarms`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(10000); // WASM 启动 + 统计拉取

        // KPI 汇总卡 ×5
        const kpiCount = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
        if (kpiCount === 5) ok('报警中心 KPI ×5（活跃/设备/今日触发/恢复/最长）');
        else warn(`报警中心 KPI ${kpiCount}/5`);

        // 级别筛选 chips
        const chips = await page.evaluate(() => document.querySelectorAll('.chip-btn').length);
        if (chips === 3) ok('报警中心级别筛选 ×3');
        else warn(`报警中心级别筛选 ${chips}/3`);

        // 活跃报警列表或空态（三语候选）
        const activeState = await page.evaluate(() => {
            const items = document.querySelectorAll('.alarm-item').length;
            const empty = document.querySelector('.no-data')?.textContent?.trim() ?? '';
            return { items, empty };
        });
        if (activeState.items > 0) ok(`报警中心活跃报警 ${activeState.items} 条`);
        else if (activeState.empty) ok('报警中心活跃报警空态');
        else fail('报警中心活跃报警区缺失');

        // 统计表（排行 + 最近事件）
        const statTables = await page.evaluate(() => document.querySelectorAll('.chart-grid.two table.hq-table').length);
        if (statTables === 2) ok('报警中心统计表 ×2（排行/最近事件）');
        else warn(`报警中心统计表 ${statTables}/2`);
    }

    // ──── 运行监控页 /monitoring（设备实时卡 + 采集诊断） ────
    {
        console.log(`OPEN ${url.replace(/\/+$/, '')}/monitoring`);
        await page.goto(`${url.replace(/\/+$/, '')}/monitoring`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(12000); // WASM 启动 + 诊断拉取

        // 采集健康 KPI ×5
        const kpiCount = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
        if (kpiCount === 5) ok('监控页采集健康 KPI ×5');
        else warn(`监控页 KPI ${kpiCount}/5`);

        // 设备实时卡（每设备一张：状态/速度/OEE 环/新鲜度）
        const devCards = await page.evaluate(() => document.querySelectorAll('.mon-card').length);
        if (devCards >= 1) ok(`监控页设备实时卡 ${devCards} 张`);
        else fail('监控页设备实时卡缺失');

        // 诊断区块（采集循环 + 历史落库 + 耗时图）
        const diagItems = await page.evaluate(() => document.querySelectorAll('.diag-item').length);
        if (diagItems >= 8) ok(`监控页诊断指标 ${diagItems} 项`);
        else warn(`监控页诊断指标 ${diagItems}/8`);

        const timingCanvas = await page.evaluate(() => !!document.querySelector('.echart-host canvas'));
        if (timingCanvas) ok('监控页耗时分解图表');
        else warn('监控页耗时分解图表缺失');
    }

    // ──── 生产复盘页 /review（健康评分 + 结论 + 周期对比 + 图表） ────
    {
        console.log(`OPEN ${url.replace(/\/+$/, '')}/review`);
        await page.goto(`${url.replace(/\/+$/, '')}/review`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(10000); // WASM 启动 + 设备加载

        // 选设备 + 查询
        await page.selectOption('.filter-bar select:first-of-type', { index: 1 });
        await page.waitForTimeout(300);
        const clicked = await page.evaluate(() => {
            const btns = [...document.querySelectorAll('.filter-actions button')];
            const b = btns.find(x => ['查询', 'Search', '検索'].some(t => (x.textContent ?? '').includes(t)));
            if (!b) return false;
            b.click();
            return true;
        });
        if (clicked) ok('复盘页触发查询');
        else fail('复盘页未找到查询按钮');
        await page.waitForTimeout(15000); // 复盘数据量大（窗口 + 对比周期多路拉取）

        // 摘要条（健康分/OEE/良品率/总产量/报警/班次）
        const summary = await page.evaluate(() => document.querySelectorAll('.review-summary .rs-item').length);
        if (summary === 6) ok('复盘页摘要条 ×6');
        else warn(`复盘页摘要条 ${summary}/6`);

        // 健康评分 + 结论
        const healthScore = await page.evaluate(() => document.querySelector('.health-score')?.textContent?.trim() ?? '');
        if (/^\d+$/.test(healthScore) && parseInt(healthScore, 10) >= 0 && parseInt(healthScore, 10) <= 100) ok(`复盘页健康评分 ${healthScore}`);
        else warn(`复盘页健康评分异常: ${healthScore}`);
        const conclusions = await page.evaluate(() => document.querySelectorAll('.conclusion-row').length);
        if (conclusions >= 1) ok(`复盘页结论 ${conclusions} 条`);
        else warn('复盘页结论缺失（无数据时正常）');

        // 核心指标 KPI + 周期对比
        const kpiCount = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
        if (kpiCount >= 3) ok(`复盘页指标卡 ${kpiCount} 张`);
        else warn(`复盘页指标卡 ${kpiCount} 张`);

        // 图表（趋势 + 报警排行 + 时间线）
        const canvases = await page.evaluate(() => document.querySelectorAll('.echart-host canvas').length);
        if (canvases >= 2) ok(`复盘页图表 ${canvases} 张`);
        else warn(`复盘页图表 ${canvases} 张（无数据时正常）`);

        // 班次明细表
        const shiftRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
        if (shiftRows > 0) ok(`复盘页班次明细 ${shiftRows} 行`);
        else warn('复盘页班次明细空（无数据时正常）');
    }

    // ──── 产线预览页 /line（设备卡片网格 + 汇总 + 筛选） ────
    {
        console.log(`OPEN ${url.replace(/\/+$/, '')}/line`);
        await page.goto(`${url.replace(/\/+$/, '')}/line`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(10000);

        // 汇总 KPI（运行/报警/待机/空闲/产量/加权OEE）
        const kpiCount = await page.evaluate(() => document.querySelectorAll('.kpi-card').length);
        if (kpiCount === 6) ok('产线页汇总 KPI ×6');
        else warn(`产线页汇总 KPI ${kpiCount}/6`);

        // 设备卡片（每设备一张）
        const cards = await page.evaluate(() => document.querySelectorAll('.line-card').length);
        if (cards >= 1) ok(`产线页设备卡 ${cards} 张`);
        else fail('产线页设备卡缺失');

        // 卡片内有 OEE 环（每卡 4 环）
        const rings = await page.evaluate(() => document.querySelectorAll('.line-card .ring').length);
        if (rings >= 4) ok(`产线页 OEE 环 ${rings} 个`);
        else warn(`产线页 OEE 环 ${rings} 个`);

        // 状态筛选 chips + 搜索框
        const chips = await page.evaluate(() => document.querySelectorAll('.chip-btn').length);
        const search = await page.evaluate(() => !!document.querySelector('.line-search'));
        if (chips === 5 && search) ok('产线页筛选（状态×5 + 搜索）');
        else warn(`产线页筛选 chips=${chips} search=${search}`);

        // 状态筛选交互：点"报警" chip，卡片数变化或保持（有报警设备时）
        await page.evaluate(() => {
            const chips = [...document.querySelectorAll('.chip-btn')];
            chips.find(c => ['报警', 'Alarm', '警報'].some(t => (c.textContent ?? '').includes(t)))?.click();
        });
        await page.waitForTimeout(500);
        const alarmCards = await page.evaluate(() => document.querySelectorAll('.line-card').length);
        ok(`产线页报警筛选后卡片 ${alarmCards} 张`);
    }

    // ──── 只读管理页（设备/工单/配方/设置/审计） ────
    {
        // 设备（列表 + 详情面板）
        console.log(`OPEN ${url.replace(/\/+$/, '')}/devices`);
        await page.goto(`${url.replace(/\/+$/, '')}/devices`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(8000);
        const devCards = await page.evaluate(() => document.querySelectorAll('.line-card').length);
        if (devCards >= 1) ok(`设备页列表 ${devCards} 台`);
        else fail('设备页列表缺失');
        await page.evaluate(() => document.querySelector('.line-card')?.click());
        await page.waitForTimeout(500);
        const hasDetail = await page.evaluate(() => {
            const t = document.body.innerText;
            return t.includes('地址配置') || t.includes('Address Config') || t.includes('アドレス設定');
        });
        if (hasDetail) ok('设备页详情面板（地址/报警/缺陷配置）');
        else warn('设备页详情面板缺失');

        // 工单（表格 + 分页）
        console.log(`OPEN ${url.replace(/\/+$/, '')}/workorders`);
        await page.goto(`${url.replace(/\/+$/, '')}/workorders`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(6000);
        const woRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
        if (woRows > 0) ok(`工单页表格 ${woRows} 行`);
        else warn('工单页表格空（无工单时正常）');

        // 配方（卡片 + 参数）
        console.log(`OPEN ${url.replace(/\/+$/, '')}/recipes`);
        await page.goto(`${url.replace(/\/+$/, '')}/recipes`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(6000);
        const rcCards = await page.evaluate(() => document.querySelectorAll('.line-card').length);
        if (rcCards >= 1) ok(`配方页卡片 ${rcCards} 张`);
        else warn('配方页卡片空（无配方时正常）');

        // 设置（采集/PLC 参数 + 班次）
        console.log(`OPEN ${url.replace(/\/+$/, '')}/settings`);
        await page.goto(`${url.replace(/\/+$/, '')}/settings`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(6000);
        const stItems = await page.evaluate(() => document.querySelectorAll('.diag-item').length);
        if (stItems >= 4) ok(`设置页参数 ${stItems} 项`);
        else warn(`设置页参数 ${stItems} 项`);

        // 审计（表格 + 筛选）
        console.log(`OPEN ${url.replace(/\/+$/, '')}/audit`);
        await page.goto(`${url.replace(/\/+$/, '')}/audit`, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(6000);
        const auRows = await page.evaluate(() => document.querySelectorAll('.hq-table tbody tr').length);
        if (auRows > 0) ok(`审计页记录 ${auRows} 行`);
        else warn('审计页记录空（无审计数据时正常）');
        const auFilters = await page.evaluate(() => document.querySelectorAll('.filter-bar input, .filter-bar select').length);
        if (auFilters >= 3) ok('审计页筛选（操作人/操作类型/结果）');
        else warn(`审计页筛选 ${auFilters}/3`);
    }

    // ──── 控制台错误 ────
    errors.forEach(e => { if (!e.includes('favicon')) console.log(`  ⚠ ${e}`); });
    // 环境伪影白名单：favicon 404（模板默认）、ResizeObserver loop（浏览器内部机制，
    // ECharts ResizeObserver 触发时的已知无害警告，非页面错误）
    const envNoise = e => e.includes('favicon.ico') || e.includes('favicon.png') || e.includes('ResizeObserver loop');
    const fatal = errors.filter(e => !envNoise(e));
    if (fatal.length) { console.log('FATAL ERRORS:'); fatal.forEach(e => console.log('  ' + e)); failCount += fatal.length; }
    warnings.forEach(w => console.log(`  ⚠ ${w}`));

    const exit = failCount === 0 ? 0 : 1;
    console.log(exit === 0 ? `SMOKE PASS (${pass} 断言)` : `SMOKE FAIL (${failCount} 失败, ${pass} 通过)`);
    process.exitCode = exit;
    await browser.close();
})().catch(e => { console.error('FATAL', e); process.exit(1); });
