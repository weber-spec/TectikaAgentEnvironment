import { chromium } from 'playwright';
import fs from 'node:fs';

const EXEC = '/root/chrome/chrome-linux64/chrome';
const BASE = 'http://localhost:3000';
const OUT = new URL('./shots/', import.meta.url).pathname;
fs.mkdirSync(OUT, { recursive: true });

const errors = [];
const results = [];

function attach(page, tag) {
  page.on('console', m => { if (m.type() === 'error') errors.push(`[${tag}] console.error: ${m.text()}`); });
  page.on('pageerror', e => errors.push(`[${tag}] pageerror: ${e.message}`));
  page.on('requestfailed', r => {
    const u = r.url();
    if (u.includes('/api/')) errors.push(`[${tag}] requestfailed: ${u} ${r.failure()?.errorText}`);
  });
}

async function shot(page, name) {
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${OUT}${name}.png`, fullPage: false });
  results.push(name);
  console.log('  📸', name);
}

const browser = await chromium.launch({ executablePath: EXEC, headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const ctx = await browser.newContext({ viewport: { width: 1480, height: 900 }, deviceScaleFactor: 1 });
const page = await ctx.newPage();
attach(page, 'main');

try {
  console.log('▶ boards gallery');
  await page.goto(`${BASE}/boards`, { waitUntil: 'networkidle' });
  await shot(page, '01-boards');

  console.log('▶ open first board');
  await page.locator('a[href^="/boards/board-"]').first().click();
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(800);
  await shot(page, '02-board-table');

  // switch views via tabs
  for (const [label, name] of [['Kanban', '03-kanban'], ['Timeline', '04-timeline'], ['Calendar', '05-calendar'], ['Cards', '06-cards'], ['Chart', '07-chart'], ['Canvas', '08-canvas']]) {
    const tab = page.getByRole('button', { name: new RegExp(label, 'i') }).first();
    if (await tab.count()) { await tab.click(); await page.waitForTimeout(900); await shot(page, name); }
    else errors.push(`[views] tab not found: ${label}`);
  }

  // back to table, open toolbar popovers
  await page.getByRole('button', { name: /Main Table|Table/i }).first().click();
  await page.waitForTimeout(500);
  const filterBtn = page.getByRole('button', { name: /Filter/i }).first();
  if (await filterBtn.count()) { await filterBtn.click(); await page.waitForTimeout(400); await shot(page, '09-filter'); await page.keyboard.press('Escape'); }

  // open an item panel
  console.log('▶ open item panel');
  const openBtn = page.getByRole('button', { name: /^Open$/ }).first();
  if (await openBtn.count()) { await openBtn.click(); }
  else { await page.locator('text=Implement checkout').first().click().catch(() => {}); }
  await page.waitForTimeout(1000);
  await shot(page, '10-item-panel');
  await page.keyboard.press('Escape');

  // automations modal
  const autoBtn = page.getByRole('button', { name: /Automate/i }).first();
  if (await autoBtn.count()) { await autoBtn.click(); await page.waitForTimeout(500); await shot(page, '11-automations'); await page.keyboard.press('Escape'); }

  // other pages
  for (const [path, name] of [['/dashboards', '12-dashboards'], ['/analytics', '13-analytics'], ['/agents', '14-agents'], ['/approvals', '15-approvals'], ['/settings', '16-settings']]) {
    console.log('▶', path);
    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(900);
    await shot(page, name);
  }

  // command palette
  console.log('▶ command palette');
  await page.goto(`${BASE}/boards`, { waitUntil: 'networkidle' });
  await page.keyboard.press('Control+k');
  await page.waitForTimeout(500);
  await shot(page, '17-command-palette');
  await page.keyboard.press('Escape');

} catch (e) {
  errors.push(`FATAL: ${e.message}`);
  console.error(e);
} finally {
  await browser.close();
}

console.log('\n=== QA SUMMARY ===');
console.log('screens captured:', results.length);
console.log('errors:', errors.length);
errors.forEach(e => console.log('  ✗', e));
fs.writeFileSync(`${OUT}errors.txt`, errors.join('\n'));
process.exit(errors.length ? 1 : 0);
