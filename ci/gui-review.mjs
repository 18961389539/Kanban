// WEB 全量 GUI 审查驱动 — 第二轮：逐页全量证据采集 + 交互
import { chromium } from 'playwright-core';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const BASE = process.env.WEB_REVIEW_URL ?? 'http://localhost:5129/';
const SHOT_DIR = join(process.cwd(), 'gui-test-screenshots');
mkdirSync(SHOT_DIR, { recursive: true });

const consoleErrors = [];
let step = 0;
const log = (m) => { console.log(`[s${step}] ${m}`); };

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('console', m => { if (m.type() === 'error') consoleErrors.push(`[s${step}] [console.error] ${m.text()}`); });
page.on('pageerror', e => consoleErrors.push(`[s${step}] [pageerror] ${e.message}`));
page.on('requestfailed', r => consoleErrors.push(`[s${step}] [requestfailed] ${r.url()} ${r.failure()?.errorText}`));

const report = {}; // 页面 → 证据

async function pageEvidence(label) {
  const ev = await page.evaluate(() => {
    const sel = s => [...document.querySelectorAll(s)];
    const errUi = document.querySelector('#blazor-error-ui');
    return {
      text: document.body?.innerText ?? '',
      selects: sel('select').map(s => ({ cls: s.className, options: [...s.options].map(o => o.text.trim()) })),
      buttons: sel('button').map(b => (b.textContent ?? '').trim()).filter(Boolean).slice(0, 30),
      links: sel('a').map(a => (a.textContent ?? '').trim()).filter(Boolean).slice(0, 30),
      inputs: sel('input').map(i => ({ type: i.type, placeholder: i.placeholder ?? '' })),
      kpis: sel('.kpi-label').map(e => e.textContent?.trim()).slice(0, 20),
      cards: sel('.card-title').map(e => e.textContent?.trim()).slice(0, 20),
      errorUiVisible: errUi ? getComputedStyle(errUi).display !== 'none' : false,
      tables: sel('table').map(t => [...t.querySelectorAll('th')].map(h => h.textContent?.trim()).slice(0, 12)),
      rowCount: sel('tbody tr').length,
    };
  });
  report[label] = ev;
  const path = join(SHOT_DIR, `t${String(step).padStart(2, '0')}_${label}.png`);
  await page.screenshot({ path });
  return { ...ev, shot: path };
}

async function gotoPage(url, label) {
  log(`goto ${label} ${url}`);
  step++;
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.waitForTimeout(6000);
  const ev = await pageEvidence(label);
  log(`  text.len=${ev.text.length} errUi=${ev.errorUiVisible} selects=${ev.selects.length} buttons=${ev.buttons.length} rows=${ev.rowCount}`);
  writeFileSync(join(SHOT_DIR, 'evidence.json'), JSON.stringify(report, null, 2));
  return ev;
}

try {
  // ── 首页 ──
  await gotoPage(BASE, 'home');
  // 等 8s 看实时数据是否到来
  step++; await page.waitForTimeout(8000);
  const home2 = await pageEvidence('home_late');
  log(`home late: text.len=${home2.text.length} kpis=${JSON.stringify(home2.kpis)} cards=${JSON.stringify(home2.cards)}`);
  // 设备下拉
  if (home2.selects.length) {
    const devSel = page.locator('select').first();
    const opts = await devSel.locator('option').allTextContents();
    if (opts.length > 1) {
      await devSel.selectOption({ label: opts[1] });
      step++; await page.waitForTimeout(2000);
      const after = await pageEvidence('home_switched');
      log(`device switched to ${opts[1]}: text.len=${after.text.length}`);
    } else {
      log('home device select has 1 option only:', JSON.stringify(home2.selects[0].options));
    }
  }

  // ── 历史查询 ──
  const h = await gotoPage(BASE + 'history', 'history');
  log(`history selects=${JSON.stringify(h.selects)}`);
  // 若设备下拉只有"全部设备"→ 记录并尝试直接查询（无设备也应有空态反馈）
  const preset = page.locator('select').nth(1); // 快捷时间（第 2 个下拉）
  if (await preset.count()) {
    const opts = await preset.locator('option').allTextContents();
    log(`history preset options=${JSON.stringify(opts)}`);
    if (opts.length > 1) {
      await preset.selectOption({ label: opts[1] });
      step++; await page.waitForTimeout(1000);
    }
  }
  const searchBtn = page.locator('button', { hasText: /查询|Search|検索/ }).first();
  if (await searchBtn.count()) {
    await searchBtn.click();
    step++; await page.waitForTimeout(5000);
    const after = await pageEvidence('history_searched');
    log(`history searched: text.len=${after.text.length} rows=${after.rowCount} errUi=${after.errorUiVisible}`);
  } else {
    log('history: 查询按钮未找到');
    await pageEvidence('history_nosearch');
  }

  // 其余页面
  for (const [label, url] of [
    ['alarmcenter', 'alarms'], ['monitoring', 'monitoring'], ['review', 'review'],
    ['devices', 'devices'], ['workorders', 'workorders'], ['recipes', 'recipes'],
    ['settings', 'settings'], ['audit', 'audit'],
  ]) {
    await gotoPage(BASE + url, label);
  }
} catch (e) {
  consoleErrors.push(`[s${step}] [exception] ${e.message}`);
  const p = join(SHOT_DIR, 't99_error.png');
  await page.screenshot({ path: p }).catch(() => {});
  log('EXCEPTION', e.message, 'shot:', p);
} finally {
  await browser.close();
  writeFileSync(join(SHOT_DIR, 'evidence.json'), JSON.stringify(report, null, 2));
  writeFileSync(join(SHOT_DIR, 'console_errors.json'), JSON.stringify(consoleErrors, null, 2));
  console.log('\n=== 控制台错误 ===');
  for (const e of consoleErrors) console.log(e);
  if (!consoleErrors.length) console.log('（无）');
  console.log('\n=== 证据文件 ===');
  console.log(join(SHOT_DIR, 'evidence.json'));
  console.log(join(SHOT_DIR, 'console_errors.json'));
}
